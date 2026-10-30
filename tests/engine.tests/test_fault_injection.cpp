/**
 * @file test_fault_injection.cpp
 * @brief Engine-level detection of injected faults: every corruption the fault-injecting layer
 *        applies is reported exactly once, with its word address, bit, step, pass and access type.
 *
 * Success criterion 3 at engine level. Each fault class runs one session of the Standard plan
 * scaled to a tenth of its time budgets (holds kept at 5 s), about two minutes of real time, over
 * a 512 MiB locked region with Stop on error off:
 * - FaultInjectionPlan checks the scaled plan itself against the Standard plan and validates the
 *   session configuration, so a broken harness fails in milliseconds, not after a long run.
 * - FaultInjection/FaultInjectionTest runs the four classes: single-bit flips (bit 17) spread
 *   over the region, a stuck bit (bit 5) in one word, an address-region fault inside a contiguous
 *   1/32 of the region, and a delay-dependent fault that only hold verifications can reach.
 * - FaultInjectionLayer drives the fault-injecting layer directly over an unlocked 1 MiB buffer
 *   through 1,000 (step, pass) keys, in milliseconds: once max_injections is spent it builds no
 *   more target sets, its table never holds more than the newest pass's set, and a reclaimed
 *   set asked for again injects the same words and bits. FaultInjectionLayerStrided checks that
 *   a BankStride verify_strided call injects exactly the single-bit-flip targets, or the stuck
 *   word, on the lines it visits and none elsewhere.
 *
 * The session is built through session_create with the stack the test DLL uses, fault-injecting
 * over counting over real, so the engine runs its production coordinator, worker pool, step
 * runner and error collector. Every injection whose verification ran must match exactly one
 * reported rq_error_record by word address, bit mask, step, pass and access type, compared as
 * multisets, with nothing unmatched on either side. Before matching, the test asserts that the
 * expected reports stay at or below 2,000, well under the 10,000-record detail cap, so no record
 * can be missing for capacity reasons. The expected top-ranked cause of each class belongs to the
 * managed analyzer tests, not to this file.
 *
 * Injections ride only on the engine's own verification opportunities: the fault layer adds one
 * store per corrupted word and no extra access, and the stuck word is touched only when the
 * engine verifies it, so the runs keep the stress bound's design limits.
 *
 * Runs non-elevated: locking 512 MiB needs only the working-set raise the engine performs. A host
 * with less than 1.5 GiB available fails with an explicit message instead of skipping, because a
 * skipped detection test would read as a pass.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <iterator>
#include <map>
#include <memory>
#include <mutex>
#include <ostream>
#include <set>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/session.h"
#include "core/system_info.h"
#include "core/kernels/isa_dispatch.h"
#include "core/kernels/kernels.h"
#include "core/patterns/patterns.h"
#include "core/memory/memory_access.h"
#include "core/memory/real_memory_access.h"
#include "test_dll/ramquick_engine_test.h"
#include "fakes/fake_clock.h"
#include "fakes/fault_spec.h"
#include "fakes/counting_memory_access.h"
#include "fakes/fault_injecting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_mib = std::uint64_t{1} << 20U;

/** Planned and minimum bytes under test: no shrink, so every run tests the same region. */
constexpr std::uint64_t k_region_bytes = 512 * k_mib;

/** Smallest worker slice the harness allows; it bounds the worker count at 128. */
constexpr std::uint64_t k_min_worker_slice_bytes = 4 * k_mib;

/** Available physical memory the runs require: the region plus generous headroom. */
constexpr std::uint64_t k_min_available_bytes = 1536 * k_mib;

/** Detail cap the application passes. */
constexpr std::uint32_t k_detail_cap = 10'000U;

/** Upper bound of the expected reports per class, asserted before matching. */
constexpr std::uint64_t k_max_expected_reports = 2'000U;

static_assert(k_max_expected_reports < k_detail_cap,
              "every expected report must fit under the detail cap");

/** 4 Hz, as the application runs. */
constexpr std::uint32_t k_progress_interval_ms = 250U;

/** Pattern and permutation seed of every session. */
constexpr std::uint64_t k_session_seed = 0x52414D517569636BULL;

/** Seed of the fault layer's target selection, independent of the pattern seed. */
constexpr std::uint64_t k_fault_seed = 0x0F1E2D3C4B5A6978ULL;

/** Hold of the scaled plan's hold steps: the Quick hold, kept at full length. */
constexpr std::uint32_t k_hold_ms = 5'000U;

/** Indices of the two hold steps of the scaled plan, Q5 and S10. */
constexpr std::uint32_t k_refresh_hold_step = 4U;
constexpr std::uint32_t k_refresh_hold_long_step = 9U;

/** Time allowed beyond the plan's budgets for preparation, overruns and release. */
constexpr std::uint32_t k_released_slack_ms = 60'000U;

/**
 * Passes of each step offered to the fault layer. The layer's max_injections cap is global and
 * claimed in time order, so on a fast host the first step alone could exhaust a 2,000-report
 * budget and leave turnaround, moving-inversion and hold verifications untested. Offering every
 * step the same leading passes spreads the injections over all twelve steps and all four access
 * types whatever the host's bandwidth.
 */
constexpr std::uint32_t k_injected_passes_per_step = 8U;

constexpr std::uint64_t k_us_per_ms = 1'000U;

/** Distinct entries each mismatch message lists at most. */
constexpr std::size_t k_listed_mismatches = 10U;

/* ---------------------------------------------------------------------------------------------
 * The scaled Standard plan
 * ------------------------------------------------------------------------------------------- */

/** One row of the plan table: an rq_step_def without its fixed-size name array. */
struct plan_step {
    const char* name;
    std::uint32_t pattern;
    std::uint32_t access_order;
    std::uint32_t thread_mode;
    std::uint32_t hold_ms;
    std::uint32_t time_budget_ms;
    std::uint32_t coverage_budget_bp;
};

/**
 * The Standard plan's twelve steps in order, with every time budget divided by 10 and the hold
 * steps' holds kept at 5 s. Coverage floors are the Standard plan's, in basis points. The
 * moving-inversion steps ignore access_order and carry the ascending order only because the
 * engine validates the field for every step.
 */
constexpr std::array<plan_step, 12> k_scaled_standard_steps{{
    {"High-toggle sweep", RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0U,
     4'000U, 2'500U},
    {"Random bank thrash", RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, RQ_THREADS_ALL, 0U, 5'000U,
     3'500U},
    {"Read-after-write turnaround", RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, RQ_THREADS_ALL, 0U,
     5'000U, 3'500U},
    {"Moving inversion, walking bits", RQ_PATTERN_MOVING_INVERSION_WALKING,
     RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0U, 6'000U, 5'000U},
    {"Refresh hold", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, k_hold_ms, 5'000U,
     2'000U},
    {"Full-load random mix", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, 0U, 5'000U,
     3'500U},
    {"Moving inversion, random", RQ_PATTERN_MOVING_INVERSION_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_ALL, 0U, 15'000U, 12'000U},
    {"Checkerboard and inverted", RQ_PATTERN_CHECKERBOARD, RQ_ORDER_SEQUENTIAL_DESCENDING,
     RQ_THREADS_ALL, 0U, 12'000U, 8'000U},
    {"High-toggle bank thrash", RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_BANK_STRIDE, RQ_THREADS_ALL, 0U,
     12'000U, 10'000U},
    {"Refresh hold, long", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, k_hold_ms,
     15'000U, 4'000U},
    {"Reduced-load contrast", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_HALF, 0U,
     12'000U, 6'000U},
    {"Heat soak", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, 0U, 24'000U, 20'000U},
}};

constexpr std::uint32_t k_step_count = static_cast<std::uint32_t>(k_scaled_standard_steps.size());

/** The scaled plan as the engine receives it. */
std::vector<rq_step_def> scaled_standard_plan() {
    std::vector<rq_step_def> steps;
    steps.reserve(k_scaled_standard_steps.size());
    for (const plan_step& row : k_scaled_standard_steps) {
        rq_step_def step{};
        copy_utf8_truncated(step.name, sizeof(step.name), row.name);
        step.pattern = row.pattern;
        step.access_order = row.access_order;
        step.thread_mode = row.thread_mode;
        step.hold_ms = row.hold_ms;
        step.time_budget_ms = row.time_budget_ms;
        step.coverage_budget_bp = row.coverage_budget_bp;
        steps.push_back(step);
    }
    return steps;
}

/** Sum of the plan's time budgets in milliseconds. */
std::uint64_t total_budget_ms(const std::vector<rq_step_def>& steps) noexcept {
    std::uint64_t total = 0;
    for (const rq_step_def& step : steps) {
        total += step.time_budget_ms;
    }
    return total;
}

/**
 * One worker per active logical processor, up to the count at which every slice still holds
 * k_min_worker_slice_bytes.
 */
std::uint32_t harness_worker_count() {
    const std::uint64_t processors = enumerate_logical_processors().size();
    const std::uint64_t by_slice = k_region_bytes / k_min_worker_slice_bytes;
    return static_cast<std::uint32_t>(processors < by_slice ? processors : by_slice);
}

/** The session configuration of every fault-injection run. */
rq_session_config make_config(const std::vector<rq_step_def>& steps, std::uint32_t workers) {
    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = k_region_bytes;
    config.min_region_bytes = k_region_bytes;
    config.seed = k_session_seed;
    config.worker_count = workers;
    config.stop_on_error = 0U;
    config.error_detail_cap = k_detail_cap;
    config.progress_interval_ms = k_progress_interval_ms;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = static_cast<std::uint32_t>(steps.size());
    config.steps = steps.data();
    return config;
}

/* ---------------------------------------------------------------------------------------------
 * Names and failure context
 * ------------------------------------------------------------------------------------------- */

/** 0x followed by 16 uppercase hex digits. */
std::string hex64(std::uint64_t value) {
    char text[19];
    std::snprintf(text, sizeof(text), "0x%016llX", static_cast<unsigned long long>(value));
    return text;
}

/** RQ_END_* value as text for failure messages. */
const char* end_reason_name(std::uint32_t reason) noexcept {
    switch (reason) {
    case RQ_END_COMPLETED:
        return "completed";
    case RQ_END_FIRST_ERROR:
        return "first_error";
    case RQ_END_USER_STOP:
        return "user_stop";
    case RQ_END_THERMAL:
        return "thermal";
    case RQ_END_HOST_FAULT:
        return "host_fault";
    case RQ_END_INSUFFICIENT_MEMORY:
        return "insufficient_memory";
    case RQ_END_AFFINITY_FAILED:
        return "affinity_failed";
    case RQ_END_INTERNAL_FAULT:
        return "internal_fault";
    default:
        return "invalid end reason";
    }
}

/**
 * What one verification of one word yields: logical word offset, bit mask, step, pass and access
 * type. An injection and the record it causes must have the same key.
 */
using match_key =
    std::tuple<std::uint64_t, std::uint64_t, std::uint32_t, std::uint32_t, std::uint32_t>;

match_key key_of(const rq_injected_fault& injection) noexcept {
    return {injection.word_offset, injection.bit_mask, injection.step_index, injection.pass_index,
            injection.access_type};
}

match_key key_of(const rq_error_record& record) noexcept {
    return {record.region_offset & ~std::uint64_t{7}, record.bit_mask, record.step_index,
            record.pass_index, record.access_type};
}

std::string describe(const match_key& key) {
    return "offset " + hex64(std::get<0>(key)) + ", mask " + hex64(std::get<1>(key)) + ", step " +
           std::to_string(std::get<2>(key)) + ", pass " + std::to_string(std::get<3>(key)) +
           ", access " + std::to_string(std::get<4>(key));
}

/* ---------------------------------------------------------------------------------------------
 * The memory-access stack
 * ------------------------------------------------------------------------------------------- */

/**
 * The outermost layer of the harness stack: offers the fault layer the verifications of the
 * first k_injected_passes_per_step passes of every step and sends later passes' verifications
 * straight to the counting layer beneath it, so the cap-bounded injections reach every step.
 *
 * Every other primitive, and every re-read, goes through the fault layer, which forwards them
 * unchanged. The counting layer therefore still sees every verification, and the fault layer's
 * log holds exactly the corruptions that were stored. Owned by the session through
 * std::unique_ptr<IMemoryAccess>, like any layer stack wrap_memory_access returns.
 */
class early_pass_gate final : public IMemoryAccess {
public:
    early_pass_gate(std::unique_ptr<fault_injecting_memory_access> fault,
                    std::uint32_t injected_passes) noexcept
        : fault_(std::move(fault)), injected_passes_(injected_passes) {}

    void stream_fill(const access_context& ctx, std::uint64_t* words, std::size_t word_count,
                     const pattern_spec& spec, std::uint64_t first_word_index,
                     int direction) override {
        fault_->stream_fill(ctx, words, word_count, spec, first_word_index, direction);
    }

    void fence(const access_context& ctx) override { fault_->fence(ctx); }

    void flush(const access_context& ctx, const std::uint64_t* words,
               std::size_t word_count) override {
        fault_->flush(ctx, words, word_count);
    }

    std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                         std::size_t word_count, const pattern_spec& expected,
                         std::uint64_t first_word_index, int direction,
                         mismatch_sink& sink) override {
        return layer_for(ctx).verify(ctx, words, word_count, expected, first_word_index, direction,
                                     sink);
    }

    std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                    std::size_t word_count, const pattern_spec& expected,
                                    const pattern_spec& next, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) override {
        return layer_for(ctx).verify_and_stream(ctx, words, word_count, expected, next,
                                                first_word_index, direction, sink);
    }

    void stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                             std::size_t span_word_count, const strided_visits& visits,
                             const pattern_spec& spec,
                             std::uint64_t span_first_word_index) override {
        fault_->stream_fill_strided(ctx, span, span_word_count, visits, spec,
                                    span_first_word_index);
    }

    std::uint64_t verify_strided(const access_context& ctx, const std::uint64_t* span,
                                 std::size_t span_word_count, const strided_visits& visits,
                                 const pattern_spec& expected, std::uint64_t span_first_word_index,
                                 mismatch_sink& sink) override {
        return layer_for(ctx).verify_strided(ctx, span, span_word_count, visits, expected,
                                             span_first_word_index, sink);
    }

    std::uint64_t reread_word(const access_context& ctx,
                              const std::uint64_t* word) noexcept override {
        return fault_->reread_word(ctx, word);
    }

private:
    IMemoryAccess& layer_for(const access_context& ctx) noexcept {
        if (ctx.pass_index < injected_passes_) {
            return *fault_;
        }
        return *fault_->inner();
    }

    std::unique_ptr<fault_injecting_memory_access> fault_;
    std::uint32_t injected_passes_;
};

/* ---------------------------------------------------------------------------------------------
 * Callback recorder
 * ------------------------------------------------------------------------------------------- */

/**
 * Everything the session reports, copied out of the engine-owned payloads. Callbacks run on the
 * coordinator thread one at a time; the test thread reads under the mutex once Released has
 * arrived. user_data of the session, so it must outlive session_destroy.
 */
struct run_recorder {
    std::mutex mutex;
    std::condition_variable changed;

    std::vector<rq_error_record> records;
    std::uint64_t last_batch_total = 0;
    std::vector<rq_step_result> finished_steps;
    rq_prepared prepared{};
    bool prepared_seen = false;
    rq_run_result result{};
    /** Copy of result.step_results, which is valid only during on_completed. */
    std::vector<rq_step_result> result_steps;
    bool completed = false;
    bool released = false;
    std::uint64_t released_bytes = 0;

    std::atomic<std::uint32_t> gates_raised{0};
    std::atomic<std::uint64_t> progress_ticks{0};

    /**
     * First exception a callback caught, so it never unwinds into the engine. Claimed by one
     * writer and published through failure_ready before Released is signalled.
     */
    std::atomic<bool> failure_claimed{false};
    std::atomic<bool> failure_ready{false};
    char failure_text[256] = {};

    void note_failure(const char* callback, const char* what) noexcept {
        bool expected = false;
        if (failure_claimed.compare_exchange_strong(expected, true)) {
            std::snprintf(failure_text, sizeof(failure_text), "%s threw: %s", callback, what);
            failure_ready.store(true, std::memory_order_release);
        }
    }

    /** Waits until on_released has been recorded or the timeout elapses. */
    bool wait_released(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex);
        return changed.wait_for(lock, timeout, [this] { return released; });
    }
};

run_recorder& recorder_of(void* user_data) noexcept {
    return *static_cast<run_recorder*>(user_data);
}

void RQ_CALL record_prepared(void* user_data, const rq_prepared* info) noexcept {
    run_recorder& recorder = recorder_of(user_data);
    try {
        const std::lock_guard<std::mutex> lock(recorder.mutex);
        recorder.prepared = *info;
        recorder.prepared_seen = true;
    } catch (const std::exception& e) {
        recorder.note_failure("on_prepared", e.what());
    }
}

/** The step gate: every step runs; the count shows how many were offered. */
std::uint32_t RQ_CALL continue_every_step(void* user_data, const rq_step_start*) noexcept {
    recorder_of(user_data).gates_raised.fetch_add(1, std::memory_order_relaxed);
    return RQ_ACTION_CONTINUE;
}

void RQ_CALL record_step_finished(void* user_data, const rq_step_result* result) noexcept {
    run_recorder& recorder = recorder_of(user_data);
    try {
        const std::lock_guard<std::mutex> lock(recorder.mutex);
        recorder.finished_steps.push_back(*result);
    } catch (const std::exception& e) {
        recorder.note_failure("on_step_finished", e.what());
    }
}

void RQ_CALL count_progress(void* user_data, const rq_progress*) noexcept {
    recorder_of(user_data).progress_ticks.fetch_add(1, std::memory_order_relaxed);
}

void RQ_CALL record_errors(void* user_data, const rq_error_record* records, std::uint32_t count,
                           std::uint64_t total_errors) noexcept {
    run_recorder& recorder = recorder_of(user_data);
    try {
        const std::lock_guard<std::mutex> lock(recorder.mutex);
        if (count != 0) {
            recorder.records.insert(recorder.records.end(), records, records + count);
        }
        recorder.last_batch_total = total_errors;
    } catch (const std::exception& e) {
        recorder.note_failure("on_errors", e.what());
    }
}

void RQ_CALL record_completed(void* user_data, const rq_run_result* result) noexcept {
    run_recorder& recorder = recorder_of(user_data);
    try {
        const std::lock_guard<std::mutex> lock(recorder.mutex);
        recorder.result = *result;
        recorder.result_steps.assign(result->step_results,
                                     result->step_results + result->step_result_count);
        recorder.result.step_results = nullptr;
        recorder.completed = true;
    } catch (const std::exception& e) {
        recorder.note_failure("on_completed", e.what());
    }
}

void RQ_CALL record_released(void* user_data, std::uint64_t released_bytes,
                             std::uint64_t) noexcept {
    run_recorder& recorder = recorder_of(user_data);
    try {
        {
            const std::lock_guard<std::mutex> lock(recorder.mutex);
            recorder.released_bytes = released_bytes;
            recorder.released = true;
        }
        recorder.changed.notify_all();
    } catch (const std::exception& e) {
        recorder.note_failure("on_released", e.what());
    }
}

rq_callbacks make_callbacks(run_recorder& recorder) noexcept {
    rq_callbacks callbacks{};
    callbacks.struct_size = sizeof(rq_callbacks);
    callbacks.user_data = &recorder;
    callbacks.on_prepared = &record_prepared;
    callbacks.on_step_starting = &continue_every_step;
    callbacks.on_step_finished = &record_step_finished;
    callbacks.on_progress = &count_progress;
    callbacks.on_errors = &record_errors;
    callbacks.on_completed = &record_completed;
    callbacks.on_released = &record_released;
    return callbacks;
}

/* ---------------------------------------------------------------------------------------------
 * Session ownership
 * ------------------------------------------------------------------------------------------- */

/**
 * Owns a session handle and destroys it on every exit path, so a failed assertion never leaves
 * a coordinator running or 512 MiB locked. Destroying stops the run, joins the coordinator and
 * releases the region (session_destroy).
 */
class session_guard {
public:
    session_guard() = default;
    session_guard(const session_guard&) = delete;
    session_guard& operator=(const session_guard&) = delete;

    ~session_guard() {
        if (handle_ == nullptr) {
            return;
        }
        try {
            const rq_result result = destroy();
            EXPECT_EQ(result, RQ_OK) << "session_destroy during cleanup";
        } catch (const std::exception& e) {
            ADD_FAILURE() << "session_destroy threw during cleanup: " << e.what();
        }
    }

    rq_session_handle* out() noexcept { return &handle_; }
    rq_session_handle get() const noexcept { return handle_; }

    /** Destroys the session now; the handle is invalid from then on. */
    rq_result destroy() {
        const rq_session_handle handle = handle_;
        handle_ = nullptr;
        return handle == nullptr ? RQ_OK : session_destroy(handle);
    }

private:
    rq_session_handle handle_ = nullptr;
};

/* ---------------------------------------------------------------------------------------------
 * The run harness
 * ------------------------------------------------------------------------------------------- */

/** What one scaled-Standard run produced, copied out before the session was destroyed. */
struct run_outcome {
    /** The spec the fault layer applied, after normalization and its own defaults. */
    rq_fault_spec spec{};
    std::uint32_t worker_count = 0;
    rq_prepared prepared{};
    rq_run_result result{};
    std::vector<rq_step_result> step_results;
    std::vector<rq_step_result> finished_steps;
    std::uint32_t gates_raised = 0;
    std::uint64_t progress_ticks = 0;
    std::vector<rq_error_record> records;
    std::uint64_t last_batch_total = 0;
    std::uint64_t released_bytes = 0;
    /** The fault layer's ground truth, consistent because the run has quiesced. */
    std::vector<rq_injected_fault> injections;
    /** The counting layer's independent total of verified bytes. */
    std::uint64_t counted_verified_bytes = 0;
    /** True when the layer session_create handed to the wrapper was the real one. */
    bool real_layer_innermost = false;
};

/**
 * Runs the scaled Standard plan over 512 MiB with the given fault and fills `out`. Fatal
 * assertions end the helper; callers wrap it in ASSERT_NO_FATAL_FAILURE.
 */
void run_scaled_standard(const rq_fault_spec& raw_spec, run_outcome& out) {
    rq_system_info system{};
    system.struct_size = sizeof(rq_system_info);
    ASSERT_EQ(query_system(&system), RQ_OK);
    ASSERT_GE(system.avail_phys_bytes, k_min_available_bytes)
        << "Fault-injection runs lock " << k_region_bytes / k_mib << " MiB and need at least "
        << k_min_available_bytes / k_mib << " MiB available, but only "
        << system.avail_phys_bytes / k_mib << " MiB are. Close other applications and rerun.";

    const std::vector<rq_step_def> steps = scaled_standard_plan();
    out.worker_count = harness_worker_count();
    ASSERT_GE(out.worker_count, 1U);
    const rq_session_config config = make_config(steps, out.worker_count);

    const rq_fault_spec spec = normalized(raw_spec, k_region_bytes);
    const char* reason = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_region_bytes, &reason))
        << fault_kind_name(spec.kind) << ": " << (reason != nullptr ? reason : "");

    // Declared before the guard so the user_data, the clock and the layer pointers outlive
    // session_destroy.
    run_recorder recorder;
    const rq_callbacks callbacks = make_callbacks(recorder);
    const std::shared_ptr<qpc_clock> clock = std::make_shared<qpc_clock>();
    fault_injecting_memory_access* fault_layer = nullptr;
    counting_memory_access* counting_layer = nullptr;
    bool real_layer_innermost = false;

    session_dependencies dependencies;
    // The fault layer's delay-dependent age must use the clock that stamps write times, so both
    // receive this one instance.
    dependencies.clock = clock;
    dependencies.wrap_memory_access =
        [&](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        real_layer_innermost = dynamic_cast<const real_memory_access*>(real.get()) != nullptr;
        auto counting = std::make_unique<counting_memory_access>(std::move(real));
        counting_layer = counting.get();
        auto fault =
            std::make_unique<fault_injecting_memory_access>(std::move(counting), spec, *clock);
        fault_layer = fault.get();
        return std::make_unique<early_pass_gate>(std::move(fault), k_injected_passes_per_step);
    };

    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), session.out()), RQ_OK);
    ASSERT_NE(fault_layer, nullptr);
    ASSERT_NE(counting_layer, nullptr);
    ASSERT_EQ(session_start(session.get()), RQ_OK);

    const std::chrono::milliseconds timeout(
        static_cast<std::chrono::milliseconds::rep>(total_budget_ms(steps) + k_released_slack_ms));
    ASSERT_TRUE(recorder.wait_released(timeout))
        << fault_kind_name(spec.kind) << ": Released did not arrive within " << timeout.count()
        << " ms of start; " << recorder.gates_raised.load() << " of " << k_step_count
        << " steps had started.";

    out.spec = fault_layer->spec();
    out.injections = fault_layer->injection_log();
    out.counted_verified_bytes = counting_layer->verified_bytes();
    out.real_layer_innermost = real_layer_innermost;
    out.gates_raised = recorder.gates_raised.load();
    out.progress_ticks = recorder.progress_ticks.load();
    {
        const std::lock_guard<std::mutex> lock(recorder.mutex);
        ASSERT_TRUE(recorder.completed) << "Released arrived without Completed";
        ASSERT_TRUE(recorder.prepared_seen) << "the run was never prepared";
        out.prepared = recorder.prepared;
        out.result = recorder.result;
        out.step_results = recorder.result_steps;
        out.finished_steps = recorder.finished_steps;
        out.records = recorder.records;
        out.last_batch_total = recorder.last_batch_total;
        out.released_bytes = recorder.released_bytes;
    }
    ASSERT_EQ(session.destroy(), RQ_OK);
    ASSERT_FALSE(recorder.failure_ready.load(std::memory_order_acquire)) << recorder.failure_text;
}

/* ---------------------------------------------------------------------------------------------
 * Shared assertions
 * ------------------------------------------------------------------------------------------- */

/** The injections whose verification ran: the only ones that can be matched. */
std::vector<rq_injected_fault> verified_injections(const run_outcome& run) {
    std::vector<rq_injected_fault> verified;
    verified.reserve(run.injections.size());
    for (const rq_injected_fault& injection : run.injections) {
        if (injection.verified_opportunity == 1U) {
            verified.push_back(injection);
        }
    }
    return verified;
}

/** Verified injections per step index. */
std::vector<std::uint64_t> injections_per_step(const std::vector<rq_injected_fault>& verified) {
    std::vector<std::uint64_t> per_step(k_step_count, 0);
    for (const rq_injected_fault& injection : verified) {
        if (injection.step_index < k_step_count) {
            ++per_step[injection.step_index];
        }
    }
    return per_step;
}

/** Lists up to k_listed_mismatches keys, each repeated as often as it is unmatched. */
std::string list_unmatched(const std::map<match_key, std::int64_t>& balance, bool injections) {
    std::string text;
    std::size_t listed = 0;
    for (const auto& [key, count] : balance) {
        const std::int64_t unmatched = injections ? count : -count;
        for (std::int64_t i = 0; i < unmatched && listed < k_listed_mismatches; ++i, ++listed) {
            text += "\n  " + describe(key);
        }
    }
    return text;
}

/**
 * The core of criterion 3: the verified injections and the reported records are the same
 * multiset of (word offset, bit mask, step, pass, access type). Moving inversion can inject one
 * word once per sweep, so a key may legitimately occur twice; the counts must still agree.
 */
void expect_one_record_per_injection(const std::vector<rq_injected_fault>& verified,
                                     const std::vector<rq_error_record>& records) {
    std::map<match_key, std::int64_t> balance;
    for (const rq_injected_fault& injection : verified) {
        ++balance[key_of(injection)];
    }
    for (const rq_error_record& record : records) {
        --balance[key_of(record)];
    }
    std::uint64_t unmatched_injections = 0;
    std::uint64_t unmatched_records = 0;
    for (const auto& entry : balance) {
        if (entry.second > 0) {
            unmatched_injections += static_cast<std::uint64_t>(entry.second);
        } else if (entry.second < 0) {
            unmatched_records += static_cast<std::uint64_t>(-entry.second);
        }
    }
    EXPECT_EQ(unmatched_injections, 0U)
        << "injections without a matching record (first " << k_listed_mismatches
        << "):" << list_unmatched(balance, true);
    EXPECT_EQ(unmatched_records, 0U)
        << "records without a matching injection (first " << k_listed_mismatches
        << "):" << list_unmatched(balance, false);
}

/** Every record is internally consistent and lies inside the tested region and the plan. */
void expect_well_formed_records(const run_outcome& run) {
    for (const rq_error_record& record : run.records) {
        SCOPED_TRACE("record " + std::to_string(record.sequence) + " at offset " +
                     hex64(record.region_offset));
        EXPECT_EQ(record.address % 8U, 0U) << "address " << hex64(record.address);
        EXPECT_EQ(record.region_offset % 8U, 0U);
        EXPECT_LT(record.region_offset, run.prepared.tested_bytes);
        EXPECT_EQ(record.address_kind, RQ_ADDRESS_KIND_VIRTUAL);
        EXPECT_EQ(record.bit_mask, record.expected ^ record.actual);
        EXPECT_EQ(std::popcount(record.bit_mask), 1) << "mask " << hex64(record.bit_mask);
        EXPECT_LT(record.step_index, k_step_count);
        EXPECT_LT(record.worker_index, run.worker_count);
        EXPECT_GE(record.reread_outcome, RQ_REREAD_MATCHES_EXPECTED);
        EXPECT_LE(record.reread_outcome, RQ_REREAD_DIFFERS);
    }
}

/** True when every one of the k_step_count steps holds at least one verified injection. */
bool every_step_injected(const std::vector<std::uint64_t>& per_step) noexcept {
    for (const std::uint64_t count : per_step) {
        if (count == 0) {
            return false;
        }
    }
    return true;
}

std::string per_step_text(const std::vector<std::uint64_t>& per_step) {
    std::string text;
    for (std::size_t i = 0; i < per_step.size(); ++i) {
        text += (i == 0 ? "" : ",") + std::to_string(per_step[i]);
    }
    return text;
}

/* ---------------------------------------------------------------------------------------------
 * The four fault classes
 * ------------------------------------------------------------------------------------------- */

/** The single-bit-flip lane and the stuck bit with the value it is forced to. */
constexpr std::uint32_t k_flipped_bit = 17U;
constexpr std::uint32_t k_stuck_bit = 5U;
constexpr std::uint64_t k_stuck_value = 1U;

/** Targets per (step, pass) of the classes that draw them from a range. */
constexpr std::uint32_t k_flips_per_pass = 4U;
constexpr std::uint32_t k_region_faults_per_pass = 8U;
constexpr std::uint32_t k_delay_faults_per_pass = 48U;

/** The stuck word, mid-region and 8-byte aligned. */
constexpr std::uint64_t k_stuck_word_offset = k_region_bytes / 2;

/** The address-region fault's contiguous 1/32 of the region, starting at 5/16 of it. */
constexpr std::uint64_t k_fault_region_offset = 5 * (k_region_bytes / 16);
constexpr std::uint64_t k_fault_region_length = k_region_bytes / 32;

/** Minimum age of the data a delay-dependent fault corrupts. */
constexpr std::uint32_t k_delay_threshold_ms = 2'000U;

static_assert(k_delay_threshold_ms < k_hold_ms, "only data held through a hold may reach the age");

/**
 * Worst-case injections of a class: each verification of a word yields at most one injection,
 * a moving-inversion pass verifies a word twice and every other pass once, and only the first
 * k_injected_passes_per_step passes of a step are offered to the fault layer. The bounds are
 * deterministic, so each class's max_injections cap (set to k_max_expected_reports) is never
 * what limits its injections, whatever the host's speed.
 */
constexpr std::uint64_t k_max_verifications_per_word_per_pass = 2U;
constexpr std::uint64_t worst_case_injections(std::uint64_t targets_per_pass,
                                              std::uint64_t steps) noexcept {
    return targets_per_pass * k_max_verifications_per_word_per_pass * k_injected_passes_per_step *
           steps;
}
static_assert(worst_case_injections(k_flips_per_pass, k_step_count) <= k_max_expected_reports);
static_assert(worst_case_injections(1U, k_step_count) <= k_max_expected_reports);
static_assert(worst_case_injections(k_region_faults_per_pass, k_step_count) <=
              k_max_expected_reports);
// Only the two hold steps let data reach the delay threshold.
static_assert(worst_case_injections(k_delay_faults_per_pass, 2U) <= k_max_expected_reports);

rq_fault_spec single_bit_flip_spec() noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_SINGLE_BIT_FLIP);
    spec.bit = k_flipped_bit;
    spec.per_pass_count = k_flips_per_pass;
    spec.max_injections = k_max_expected_reports;
    // region_offset 0 and region_length 0: targets spread over the whole region.
    spec.seed = k_fault_seed;
    return spec;
}

rq_fault_spec stuck_bit_spec() noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_STUCK_BIT);
    spec.bit = k_stuck_bit;
    spec.max_injections = k_max_expected_reports;
    spec.region_offset = k_stuck_word_offset;
    spec.stuck_value = k_stuck_value;
    spec.seed = k_fault_seed;
    return spec;
}

rq_fault_spec address_region_spec() noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_ADDRESS_REGION);
    spec.bit = k_random_bit;
    spec.per_pass_count = k_region_faults_per_pass;
    spec.max_injections = k_max_expected_reports;
    spec.region_offset = k_fault_region_offset;
    spec.region_length = k_fault_region_length;
    spec.seed = k_fault_seed;
    return spec;
}

rq_fault_spec delay_dependent_spec() noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_DELAY_DEPENDENT);
    spec.bit = k_random_bit;
    spec.per_pass_count = k_delay_faults_per_pass;
    spec.max_injections = k_max_expected_reports;
    spec.delay_threshold_ms = k_delay_threshold_ms;
    spec.seed = k_fault_seed;
    return spec;
}

/**
 * Single-bit flips on one lane at spread addresses: every report on bit 17, re-read repeating
 * the stored wrong value, spread over at least half the region, in every step and through every
 * access type.
 */
void check_single_bit_flip(const run_outcome& run, const std::vector<rq_injected_fault>& verified) {
    const std::vector<std::uint64_t> per_step = injections_per_step(verified);
    EXPECT_TRUE(every_step_injected(per_step))
        << "injections per step: " << per_step_text(per_step);
    std::set<std::uint32_t> access_types;
    for (const rq_injected_fault& injection : verified) {
        access_types.insert(injection.access_type);
    }
    const std::set<std::uint32_t> every_access_type{
        RQ_ACCESS_VERIFY_AFTER_WRITE, RQ_ACCESS_VERIFY_AFTER_HOLD, RQ_ACCESS_READ_MODIFY_WRITE,
        RQ_ACCESS_TURNAROUND_READ};
    EXPECT_EQ(access_types, every_access_type);

    ASSERT_FALSE(run.records.empty());
    std::uint64_t lowest = UINT64_MAX;
    std::uint64_t highest = 0;
    for (const rq_error_record& record : run.records) {
        SCOPED_TRACE("record " + std::to_string(record.sequence));
        EXPECT_EQ(record.bit_mask, std::uint64_t{1} << k_flipped_bit);
        EXPECT_EQ(record.reread_outcome, RQ_REREAD_REPEATS_ACTUAL);
        EXPECT_EQ(record.reread, record.actual);
        lowest = record.region_offset < lowest ? record.region_offset : lowest;
        highest = record.region_offset > highest ? record.region_offset : highest;
    }
    EXPECT_GE(highest - lowest, run.prepared.tested_bytes / 2)
        << "reports span " << hex64(lowest) << " to " << hex64(highest);
}

/**
 * A stuck bit in one word: logged only where the expected bit differs from the stuck value, and
 * every report on that word and that bit, so all of them group into one word.
 */
void check_stuck_bit(const run_outcome& run, const std::vector<rq_injected_fault>& verified) {
    ASSERT_LT(k_stuck_word_offset, run.prepared.tested_bytes);
    for (const rq_injected_fault& injection : verified) {
        EXPECT_EQ(injection.word_offset, k_stuck_word_offset);
        EXPECT_EQ(injection.bit, k_stuck_bit);
    }
    std::set<std::uint64_t> words;
    for (const rq_error_record& record : run.records) {
        SCOPED_TRACE("record " + std::to_string(record.sequence));
        words.insert(record.region_offset & ~std::uint64_t{7});
        EXPECT_EQ(record.region_offset, k_stuck_word_offset);
        EXPECT_EQ(record.bit_mask, std::uint64_t{1} << k_stuck_bit);
        EXPECT_NE((record.expected >> k_stuck_bit) & 1U, k_stuck_value)
            << "a verification whose expected bit equals the stuck value cannot mismatch";
        EXPECT_EQ((record.actual >> k_stuck_bit) & 1U, k_stuck_value);
    }
    EXPECT_EQ(words.size(), 1U);
}

/**
 * A random bit flipped in words of one contiguous 1/32 of the region: every report inside it,
 * in every step, and on more than one bit.
 */
void check_address_region(const run_outcome& run, const std::vector<rq_injected_fault>& verified) {
    const std::vector<std::uint64_t> per_step = injections_per_step(verified);
    EXPECT_TRUE(every_step_injected(per_step))
        << "injections per step: " << per_step_text(per_step);
    std::set<std::uint64_t> masks;
    for (const rq_error_record& record : run.records) {
        SCOPED_TRACE("record " + std::to_string(record.sequence));
        EXPECT_GE(record.region_offset, k_fault_region_offset);
        EXPECT_LT(record.region_offset, k_fault_region_offset + k_fault_region_length);
        masks.insert(record.bit_mask);
    }
    EXPECT_GT(masks.size(), 1U) << "a random bit per injection should vary the lane";
}

/**
 * A random bit flipped only in data at least 2 s old: reports only after a hold, only in the two
 * hold steps (Q5 and S10), and in both of them.
 */
void check_delay_dependent(const run_outcome& run, const std::vector<rq_injected_fault>& verified) {
    const std::vector<std::uint64_t> per_step = injections_per_step(verified);
    for (std::uint32_t step = 0; step < k_step_count; ++step) {
        if (step == k_refresh_hold_step || step == k_refresh_hold_long_step) {
            EXPECT_GT(per_step[step], 0U) << "hold step " << step << " has no injection";
        } else {
            EXPECT_EQ(per_step[step], 0U) << "step " << step << " has no hold";
        }
    }
    for (const rq_error_record& record : run.records) {
        SCOPED_TRACE("record " + std::to_string(record.sequence));
        EXPECT_EQ(record.access_type, RQ_ACCESS_VERIFY_AFTER_HOLD);
        EXPECT_TRUE(record.step_index == k_refresh_hold_step ||
                    record.step_index == k_refresh_hold_long_step)
            << "step " << record.step_index;
        EXPECT_GE(record.time_since_write_us, std::uint64_t{k_delay_threshold_ms} * k_us_per_ms);
    }
}

/** One fault class of the parameterized test. */
struct fault_case {
    const char* name;
    rq_fault_spec (*make_spec)() noexcept;
    void (*check)(const run_outcome&, const std::vector<rq_injected_fault>&);
};

/** Names the parameter in gtest output instead of dumping its bytes. */
void PrintTo(const fault_case& fault, std::ostream* os) {
    *os << fault.name;
}

const std::array<fault_case, 4> k_fault_cases{{
    {"single_bit_flip", &single_bit_flip_spec, &check_single_bit_flip},
    {"stuck_bit", &stuck_bit_spec, &check_stuck_bit},
    {"address_region", &address_region_spec, &check_address_region},
    {"delay_dependent", &delay_dependent_spec, &check_delay_dependent},
}};

/* ---------------------------------------------------------------------------------------------
 * The layer driven directly
 * ------------------------------------------------------------------------------------------- */

/** Bytes of the buffer the layer tests stream and verify. */
constexpr std::size_t k_layer_region_bytes = std::size_t{1} << 20U;
constexpr std::size_t k_layer_region_words = k_layer_region_bytes / sizeof(std::uint64_t);

/**
 * Ranges each pass verifies the buffer in, so that every (step, pass) is looked up several
 * times: through the table by a worker's first range of the pass, through its slot afterwards.
 */
constexpr std::size_t k_layer_ranges = 16U;
constexpr std::size_t k_layer_range_words = k_layer_region_words / k_layer_ranges;

static_assert(k_layer_range_words * k_layer_ranges == k_layer_region_words,
              "the ranges must cover the buffer exactly");

/**
 * Worker indices the ranges rotate through: three that each cache the current pass's set in
 * their own slot, and one far beyond the layer's per-worker slots, which looks every call up in
 * the table.
 */
constexpr std::array<std::uint32_t, 4> k_layer_workers{0U, 1U, 2U, 1U << 20U};

/** The capped test's 1,000 (step, pass) keys, in the ascending order the engine issues them. */
constexpr std::uint32_t k_layer_steps = 4U;
constexpr std::uint32_t k_layer_passes_per_step = 250U;

/** Targets per (step, pass) and the run-wide cap of the capped test: spent within a few passes. */
constexpr std::uint32_t k_layer_flips_per_pass = 4U;
constexpr std::uint64_t k_layer_max_injections = 10U;

/** Targets per (step, pass) of the rebuild test, whose cap never limits it. */
constexpr std::uint32_t k_rebuild_flips_per_pass = 16U;

/** Releases a VirtualAlloc buffer. */
struct virtual_free_deleter {
    void operator()(std::uint64_t* words) const noexcept { VirtualFree(words, 0, MEM_RELEASE); }
};

/** Counts the reported mismatches, and those that no single-bit flip can cause. */
class mismatch_tally final : public mismatch_sink {
public:
    void on_mismatch(const std::uint64_t*, std::uint64_t expected,
                     std::uint64_t actual) noexcept override {
        ++mismatches;
        if (std::popcount(expected ^ actual) != 1) {
            ++multi_bit_mismatches;
        }
    }

    std::uint64_t mismatches = 0;
    std::uint64_t multi_bit_mismatches = 0;
};

/**
 * The test DLL's stack, fault-injecting -> counting -> real over the host's kernels, driven
 * directly over a committed, unlocked k_layer_region_bytes buffer. The buffer's base serves as
 * access_context::region_base, so a word's logical offset is its distance from the base. The
 * fake clock never moves, which no single-bit flip depends on.
 */
class layer_harness {
public:
    /** @param spec A valid spec that has passed through normalized() for the buffer. */
    explicit layer_harness(const rq_fault_spec& spec)
        : buffer_(static_cast<std::uint64_t*>(VirtualAlloc(
              nullptr, k_layer_region_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))),
          layer_(std::make_unique<counting_memory_access>(std::make_unique<real_memory_access>(
                     tables_for(select_isa(read_cpu_features(), RQ_ISA_AUTO)))),
                 spec, clock_) {}

    /** False when VirtualAlloc failed. */
    bool valid() const noexcept { return buffer_ != nullptr; }

    const fault_injecting_memory_access& layer() const noexcept { return layer_; }

    /**
     * Streams the buffer with the (step, pass)'s random pattern and fences, then verifies it
     * through the layer in k_layer_ranges ranges rotated over k_layer_workers, as a pass's
     * verify phase would. Streaming first restores every word an earlier pass corrupted, so
     * only this pass's injections can mismatch. No flush: these tests check the layer's
     * bookkeeping, not DRAM, and the fence alone orders the streamed values before the loads.
     *
     * @return The mismatches the verifications returned.
     */
    std::uint64_t verify_pass(std::uint32_t step, std::uint32_t pass, mismatch_sink& sink) {
        const pattern_spec expected = stream_pass(step, pass);
        std::uint64_t mismatches = 0;
        for (std::size_t r = 0; r < k_layer_ranges; ++r) {
            const access_context verify_ctx{step,
                                            pass,
                                            phase_kind::verify,
                                            RQ_ACCESS_VERIFY_AFTER_WRITE,
                                            k_layer_workers[r % k_layer_workers.size()],
                                            base(),
                                            clock_.now_ns()};
            const std::size_t first = r * k_layer_range_words;
            mismatches += layer_.verify(verify_ctx, buffer_.get() + first, k_layer_range_words,
                                        expected, first, 1, sink);
        }
        return mismatches;
    }

    /**
     * The first half of verify_pass: streams the buffer with the (step, pass)'s random pattern
     * through the layer and fences.
     *
     * @return The pattern streamed, which the pass's verifications expect.
     */
    pattern_spec stream_pass(std::uint32_t step, std::uint32_t pass) {
        const pattern_spec expected{RQ_PATTERN_RANDOM, k_session_seed, pass, false};
        const access_context write_ctx{
            step, pass, phase_kind::write, 0U, k_layer_workers[0], base(), clock_.now_ns()};
        layer_.stream_fill(write_ctx, buffer_.get(), k_layer_region_words, expected, 0U, 1);
        layer_.fence(write_ctx);
        return expected;
    }

    /**
     * One strided verification of the visits over the whole buffer as the span, through the
     * layer on one worker, against the pattern stream_pass(step, pass) wrote.
     *
     * @return The mismatches the verification returned.
     */
    std::uint64_t verify_strided(std::uint32_t step, std::uint32_t pass, std::uint32_t worker,
                                 const strided_visits& visits, mismatch_sink& sink) {
        const pattern_spec expected{RQ_PATTERN_RANDOM, k_session_seed, pass, false};
        const access_context verify_ctx{
            step,   pass,   phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE,
            worker, base(), clock_.now_ns()};
        return layer_.verify_strided(verify_ctx, buffer_.get(), k_layer_region_words, visits,
                                     expected, 0U, sink);
    }

private:
    std::uintptr_t base() const noexcept { return reinterpret_cast<std::uintptr_t>(buffer_.get()); }

    std::unique_ptr<std::uint64_t, virtual_free_deleter> buffer_;
    // Declared before the layer, which holds a reference to it.
    fake_clock clock_;
    fault_injecting_memory_access layer_;
};

/** A single-bit-flip spec for the layer tests, normalized for the buffer. */
rq_fault_spec layer_spec(std::uint32_t bit, std::uint32_t per_pass_count,
                         std::uint64_t max_injections) noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_SINGLE_BIT_FLIP);
    spec.bit = bit;
    spec.per_pass_count = per_pass_count;
    spec.max_injections = max_injections;
    spec.seed = k_fault_seed;
    return normalized(spec, k_layer_region_bytes);
}

/** (word offset, bit) of every logged injection of one (step, pass), in logging order. */
std::vector<std::pair<std::uint64_t, std::uint32_t>>
logged_targets(const std::vector<rq_injected_fault>& log, std::uint32_t step, std::uint32_t pass) {
    std::vector<std::pair<std::uint64_t, std::uint32_t>> targets;
    for (const rq_injected_fault& injection : log) {
        if (injection.step_index == step && injection.pass_index == pass) {
            targets.emplace_back(injection.word_offset, injection.bit);
        }
    }
    return targets;
}

/**
 * The strided layer test's visits: the 8 KiB BankStride stride over the 1 MiB buffer, 128 lines
 * per sweep. The layer's visited-line test does not depend on the stride; test_kernels.cpp
 * covers all three.
 */
constexpr std::uint64_t k_strided_layer_stride = std::uint64_t{8} << 10U;
constexpr std::uint64_t k_strided_layer_lines_per_sweep =
    k_layer_region_bytes / k_strided_layer_stride;
constexpr std::uint64_t k_strided_layer_visits = k_layer_region_bytes / 64U;

/** First visit of the test's second strided call: half the phase plus half a sweep, mid-sweep. */
constexpr std::uint64_t k_strided_layer_split =
    k_strided_layer_visits / 2U + k_strided_layer_lines_per_sweep / 2U;

/**
 * The stuck word of the strided test: 4 KiB plus one word past mid-buffer, on the line of visit
 * (4096 / 64) * 128 + 64, the second call's first visit.
 */
constexpr std::uint64_t k_strided_stuck_offset = k_layer_region_bytes / 2U + 4096U + 8U;

static_assert((k_strided_stuck_offset % k_strided_layer_stride) / 64U *
                      k_strided_layer_lines_per_sweep +
                  k_strided_stuck_offset / k_strided_layer_stride ==
              k_strided_layer_split);

/** Targets of the strided test's single-bit flips: enough to land on both calls' lines. */
constexpr std::uint32_t k_strided_flips_per_pass = 64U;

rq_fault_spec strided_flip_spec() noexcept {
    return layer_spec(k_random_bit, k_strided_flips_per_pass, k_max_expected_reports);
}

/**
 * A stuck bit in the strided test's stuck word, stuck at the complement of the bit pass 0
 * writes there, so that its verification mismatches.
 */
rq_fault_spec strided_stuck_bit_spec() noexcept {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_STUCK_BIT);
    spec.bit = k_stuck_bit;
    spec.max_injections = k_max_expected_reports;
    spec.region_offset = k_strided_stuck_offset;
    const pattern_spec written{RQ_PATTERN_RANDOM, k_session_seed, 0U, false};
    spec.stuck_value =
        ~(expected_value(written, k_strided_stuck_offset / sizeof(std::uint64_t)) >> k_stuck_bit) &
        1U;
    spec.seed = k_fault_seed;
    return normalized(spec, k_layer_region_bytes);
}

/** One fault class of the strided layer test. */
struct strided_layer_case {
    const char* name;
    rq_fault_spec (*make_spec)() noexcept;
};

void PrintTo(const strided_layer_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr std::array<strided_layer_case, 2> k_strided_layer_cases{{
    {"single_bit_flip", &strided_flip_spec},
    {"stuck_bit", &strided_stuck_bit_spec},
}};

/** Whether the byte at offset from the span start lies on a line a strided call visits. */
constexpr bool on_visited_line(const strided_visits& v, std::uint64_t offset) noexcept {
    const std::uint64_t visit =
        offset % v.stride_bytes / 64U * v.lines_per_sweep + offset / v.stride_bytes;
    return offset < v.lines_per_sweep * v.stride_bytes && visit >= v.first_visit &&
           visit - v.first_visit < v.visit_count;
}

class FaultInjectionLayerStrided : public ::testing::TestWithParam<strided_layer_case> {};

/* ---------------------------------------------------------------------------------------------
 * Tests
 * ------------------------------------------------------------------------------------------- */

TEST(FaultInjectionPlan, ScaledStandardPlanIsTheStandardPlanAtOneTenth) {
    const std::vector<rq_step_def> steps = scaled_standard_plan();
    ASSERT_EQ(steps.size(), 12U);

    // Standard: 1200 s and 800 %, of which the Quick block is 300 s and 200 %.
    EXPECT_EQ(total_budget_ms(steps), 120'000U);
    std::uint64_t coverage_bp = 0;
    std::uint64_t quick_ms = 0;
    std::uint64_t quick_bp = 0;
    for (std::size_t i = 0; i < steps.size(); ++i) {
        coverage_bp += steps[i].coverage_budget_bp;
        if (i < 6U) {
            quick_ms += steps[i].time_budget_ms;
            quick_bp += steps[i].coverage_budget_bp;
        }
        const bool hold_step = i == k_refresh_hold_step || i == k_refresh_hold_long_step;
        EXPECT_EQ(steps[i].hold_ms, hold_step ? k_hold_ms : 0U) << steps[i].name;
        EXPECT_GE(steps[i].time_budget_ms, steps[i].hold_ms) << steps[i].name;
        EXPECT_EQ(steps[i].thread_mode, i == 10U ? RQ_THREADS_HALF : RQ_THREADS_ALL)
            << steps[i].name;
    }
    EXPECT_EQ(coverage_bp, 80'000U);
    EXPECT_EQ(quick_ms, 30'000U);
    EXPECT_EQ(quick_bp, 20'000U);

    run_recorder recorder;
    const rq_callbacks callbacks = make_callbacks(recorder);
    const rq_session_config config = make_config(steps, harness_worker_count());
    EXPECT_GE(config.region_bytes / config.worker_count, k_min_worker_slice_bytes);
    EXPECT_EQ(validate_config(&config, &callbacks), RQ_OK);
}

TEST(FaultInjectionPlan, EveryFaultClassSpecIsValidForTheRegion) {
    for (const fault_case& fault : k_fault_cases) {
        SCOPED_TRACE(fault.name);
        const rq_fault_spec spec = normalized(fault.make_spec(), k_region_bytes);
        const char* reason = nullptr;
        EXPECT_TRUE(validate_fault_spec(spec, k_region_bytes, &reason))
            << (reason != nullptr ? reason : "");
        EXPECT_EQ(spec.max_injections, k_max_expected_reports);
        EXPECT_STREQ(fault_kind_name(spec.kind), fault.name);
    }
}

class FaultInjectionTest : public ::testing::TestWithParam<fault_case> {};

TEST_P(FaultInjectionTest, EveryInjectionIsReportedOnceWithItsWordBitStepAndPass) {
    const fault_case& fault = GetParam();
    run_outcome run;
    ASSERT_NO_FATAL_FAILURE(run_scaled_standard(fault.make_spec(), run));

    // The run itself: prepared over the real layer, every step ran to its schedule, completed.
    EXPECT_EQ(run.result.end_reason, RQ_END_COMPLETED)
        << end_reason_name(run.result.end_reason) << ": " << run.result.end_detail;
    EXPECT_TRUE(run.real_layer_innermost);
    EXPECT_EQ(run.prepared.worker_count, run.worker_count);
    EXPECT_GT(run.prepared.tested_bytes, 0U);
    EXPECT_LE(run.prepared.tested_bytes, k_region_bytes);
    EXPECT_GT(run.released_bytes, 0U);
    EXPECT_GT(run.progress_ticks, 0U);
    EXPECT_EQ(run.gates_raised, k_step_count);
    EXPECT_EQ(run.finished_steps.size(), k_step_count);
    EXPECT_EQ(run.result.steps_completed, k_step_count);
    ASSERT_EQ(run.result.step_result_count, k_step_count);
    ASSERT_EQ(run.step_results.size(), k_step_count);
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        EXPECT_EQ(run.step_results[i].index, i);
        EXPECT_EQ(run.step_results[i].completed, 1U) << "step " << i;
        EXPECT_GE(run.step_results[i].passes, 1U) << "step " << i;
    }

    // Precondition: every expected report fits well under the detail cap, so a missing record
    // can only be a detection failure.
    const std::vector<rq_injected_fault> verified = verified_injections(run);
    const std::vector<std::uint64_t> per_step = injections_per_step(verified);
    RecordProperty("fault_kind", fault.name);
    RecordProperty("workers", std::to_string(run.worker_count));
    RecordProperty("tested_bytes", std::to_string(run.prepared.tested_bytes));
    RecordProperty("injections_logged", std::to_string(run.injections.size()));
    RecordProperty("injections_verified", std::to_string(verified.size()));
    RecordProperty("records_reported", std::to_string(run.records.size()));
    RecordProperty("true_total_errors", std::to_string(run.result.total_errors));
    RecordProperty("injections_per_step", per_step_text(per_step));
    EXPECT_EQ(run.injections.size(), verified.size())
        << "a completed run verifies every logged injection";
    ASSERT_GT(verified.size(), 0U) << "no injection reached a verification";
    ASSERT_LE(verified.size(), k_max_expected_reports);
    ASSERT_LT(verified.size(), k_detail_cap);

    // Totals: the true total, the detailed records and each step's count agree with the truth.
    EXPECT_EQ(run.result.total_errors, verified.size());
    EXPECT_EQ(run.records.size(), run.result.total_errors)
        << "below the detail cap every error must arrive as a record";
    EXPECT_EQ(run.last_batch_total, run.result.total_errors);
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        EXPECT_EQ(run.step_results[i].errors, per_step[i]) << "step " << i;
    }

    // The counting layer sat in the verification path and saw the bytes the engine reports.
    ASSERT_GT(run.counted_verified_bytes, 0U);
    EXPECT_NEAR(static_cast<double>(run.result.verified_bytes),
                static_cast<double>(run.counted_verified_bytes),
                0.01 * static_cast<double>(run.counted_verified_bytes));

    expect_well_formed_records(run);
    expect_one_record_per_injection(verified, run.records);
    fault.check(run, verified);
}

INSTANTIATE_TEST_SUITE_P(FaultInjection, FaultInjectionTest, ::testing::ValuesIn(k_fault_cases),
                         [](const ::testing::TestParamInfo<fault_case>& info) {
                             return std::string(info.param.name);
                         });

TEST(FaultInjectionLayer, SpentCapStopsTargetSetBuildsAndTheTableKeepsOnlyTheNewestPass) {
    const rq_fault_spec spec =
        layer_spec(k_flipped_bit, k_layer_flips_per_pass, k_layer_max_injections);
    const char* reason = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_layer_region_bytes, &reason))
        << (reason != nullptr ? reason : "");
    layer_harness harness(spec);
    ASSERT_TRUE(harness.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    const fault_injecting_memory_access& layer = harness.layer();

    mismatch_tally sink;
    std::uint64_t returned_mismatches = 0;
    bool spent = false;
    std::uint64_t built_when_spent = 0;
    for (std::uint32_t step = 0; step < k_layer_steps; ++step) {
        for (std::uint32_t pass = 0; pass < k_layer_passes_per_step; ++pass) {
            returned_mismatches += harness.verify_pass(step, pass, sink);
            ASSERT_LE(layer.retained_target_set_count(), 1U)
                << "step " << step << ", pass " << pass;
            if (!spent && layer.injection_count() == k_layer_max_injections) {
                spent = true;
                built_when_spent = layer.built_target_set_count();
            }
        }
    }
    RecordProperty("target_sets_built", std::to_string(layer.built_target_set_count()));

    // The cap holds, every logged injection was verified and reported once, as one bit.
    ASSERT_TRUE(spent) << "the cap was never reached";
    EXPECT_EQ(layer.injection_count(), k_layer_max_injections);
    EXPECT_EQ(layer.verified_opportunity_count(), k_layer_max_injections);
    EXPECT_EQ(sink.mismatches, k_layer_max_injections);
    EXPECT_EQ(returned_mismatches, k_layer_max_injections);
    EXPECT_EQ(sink.multi_bit_mismatches, 0U);
    const std::vector<rq_injected_fault> log = layer.injection_log();
    ASSERT_EQ(log.size(), k_layer_max_injections);
    for (const rq_injected_fault& injection : log) {
        EXPECT_EQ(injection.verified_opportunity, 1U);
        EXPECT_EQ(injection.bit, k_flipped_bit);
        EXPECT_LT(injection.word_offset, k_layer_region_bytes);
    }

    // Every target lies in the verified buffer, so each set built before the cap was spent
    // yielded at least one injection; none was built for the hundreds of passes after it.
    EXPECT_GE(built_when_spent, 1U);
    EXPECT_LE(built_when_spent, k_layer_max_injections);
    EXPECT_EQ(layer.built_target_set_count(), built_when_spent);
    EXPECT_EQ(layer.retained_target_set_count(), 1U);
}

TEST(FaultInjectionLayer, AReclaimedTargetSetIsRebuiltWithTheSameWordsAndBits) {
    // A seeded random bit per target, so the rebuild must reproduce bits as well as offsets.
    const rq_fault_spec spec =
        layer_spec(k_random_bit, k_rebuild_flips_per_pass, k_max_expected_reports);
    const char* reason = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_layer_region_bytes, &reason))
        << (reason != nullptr ? reason : "");
    layer_harness harness(spec);
    ASSERT_TRUE(harness.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    const fault_injecting_memory_access& layer = harness.layer();
    mismatch_tally sink;

    harness.verify_pass(0U, 0U, sink);
    const std::vector<std::pair<std::uint64_t, std::uint32_t>> first =
        logged_targets(layer.injection_log(), 0U, 0U);
    ASSERT_GE(first.size(), 1U);
    EXPECT_EQ(layer.built_target_set_count(), 1U);
    EXPECT_EQ(layer.retained_target_set_count(), 1U);

    // Pass 1 reclaims pass 0's set from the table, and every slotted worker drops it too.
    harness.verify_pass(0U, 1U, sink);
    EXPECT_EQ(layer.built_target_set_count(), 2U);
    EXPECT_EQ(layer.retained_target_set_count(), 1U);

    // Going back to pass 0 rebuilds its set, and the table keeps the newest pass's beside it.
    harness.verify_pass(0U, 0U, sink);
    EXPECT_EQ(layer.built_target_set_count(), 3U);
    EXPECT_EQ(layer.retained_target_set_count(), 2U);
    const std::vector<std::pair<std::uint64_t, std::uint32_t>> both =
        logged_targets(layer.injection_log(), 0U, 0U);
    ASSERT_EQ(both.size(), 2U * first.size());
    const std::vector<std::pair<std::uint64_t, std::uint32_t>> again(
        both.begin() + static_cast<std::ptrdiff_t>(first.size()), both.end());
    EXPECT_EQ(again, first);

    // A newer pass returns the table to one generation.
    harness.verify_pass(0U, 2U, sink);
    EXPECT_EQ(layer.built_target_set_count(), 4U);
    EXPECT_EQ(layer.retained_target_set_count(), 1U);

    EXPECT_EQ(layer.verified_opportunity_count(), layer.injection_count());
    EXPECT_EQ(sink.mismatches, layer.injection_count());
    EXPECT_EQ(sink.multi_bit_mismatches, 0U);
}

// A BankStride verification reads only the lines it visits, so it injects exactly the targets on
// those lines, each once, and none elsewhere: split over two calls, as the step runner splits a
// window into work groups, the pass injects exactly what a plain verification of the buffer does.
TEST_P(FaultInjectionLayerStrided, VerifyStridedInjectsExactlyTheTargetsOnItsVisitedLines) {
    const rq_fault_spec spec = GetParam().make_spec();
    const char* reason = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_layer_region_bytes, &reason))
        << (reason != nullptr ? reason : "");
    using target = std::pair<std::uint64_t, std::uint32_t>;

    // The reference: what a plain verification of the whole buffer injects in (step 0, pass 0).
    layer_harness reference(spec);
    ASSERT_TRUE(reference.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    mismatch_tally reference_sink;
    reference.verify_pass(0U, 0U, reference_sink);
    std::vector<target> all = logged_targets(reference.layer().injection_log(), 0U, 0U);
    std::sort(all.begin(), all.end());
    ASSERT_FALSE(all.empty()) << "the reference pass injected nothing";

    layer_harness harness(spec);
    ASSERT_TRUE(harness.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    const fault_injecting_memory_access& layer = harness.layer();
    harness.stream_pass(0U, 0U);
    // A slotted worker first, then one the layer looks up in its table.
    const std::pair<std::uint32_t, strided_visits> calls[] = {
        {k_layer_workers[0], strided_visits{k_strided_layer_stride, k_strided_layer_lines_per_sweep,
                                            0U, k_strided_layer_split}},
        {k_layer_workers[3],
         strided_visits{k_strided_layer_stride, k_strided_layer_lines_per_sweep,
                        k_strided_layer_split, k_strided_layer_visits - k_strided_layer_split}},
    };
    mismatch_tally sink;
    std::uint64_t returned = 0;
    std::size_t logged = 0;
    for (const auto& [worker, visits] : calls) {
        SCOPED_TRACE("visits [" + std::to_string(visits.first_visit) + ", +" +
                     std::to_string(visits.visit_count) + ")");
        returned += harness.verify_strided(0U, 0U, worker, visits, sink);
        const std::vector<target> so_far = logged_targets(layer.injection_log(), 0U, 0U);
        std::vector<target> injected(so_far.begin() + static_cast<std::ptrdiff_t>(logged),
                                     so_far.end());
        logged = so_far.size();
        std::sort(injected.begin(), injected.end());
        std::vector<target> expected;
        std::copy_if(all.begin(), all.end(), std::back_inserter(expected),
                     [&](const target& t) { return on_visited_line(visits, t.first); });
        EXPECT_EQ(injected, expected);
        if (&visits == &calls[0].second) {
            EXPECT_LT(expected.size(), all.size())
                << "no target lies off the first call's lines, so none could be wrongly injected";
        }
    }

    // Together the calls injected and reported every target once, as one bit.
    EXPECT_EQ(logged, all.size());
    EXPECT_EQ(returned, all.size());
    EXPECT_EQ(sink.mismatches, all.size());
    EXPECT_EQ(sink.multi_bit_mismatches, 0U);
    EXPECT_EQ(layer.injection_count(), all.size());
    EXPECT_EQ(layer.verified_opportunity_count(), all.size());
    for (const rq_injected_fault& injection : layer.injection_log()) {
        EXPECT_EQ(injection.verified_opportunity, 1U);
    }
}

INSTANTIATE_TEST_SUITE_P(FaultInjectionLayerStridedClasses, FaultInjectionLayerStrided,
                         ::testing::ValuesIn(k_strided_layer_cases),
                         [](const ::testing::TestParamInfo<strided_layer_case>& info) {
                             return std::string(info.param.name);
                         });

}  // namespace
}  // namespace rq::engine::test
