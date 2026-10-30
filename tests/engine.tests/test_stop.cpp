/**
 * @file test_stop.cpp
 * @brief Stop latency: on_completed within 2 s of a user stop in every phase kind, on a locked
 *        region of at least 1 GiB, with the release time after it measured and logged.
 *
 * StopAtRandomPoints/StopLatency runs one production session per case through core/session.h
 * (qpc_clock, the real memory-access layer under counting_memory_access, win32 thread affinity)
 * with one worker per logical processor and a single 60 s step of the phase kind under test,
 * so the stop always lands inside that step. Each phase kind runs at three random points:
 * - preparing: rq_session_stop 0-300 ms after rq_session_start, before on_prepared, so the
 *   stop meets memory preparation at chunk granularity or the worker pool start-up;
 * - windowed write and windowed verify: RANDOM, SEQUENTIAL_ASCENDING;
 * - hold: RANDOM, RANDOM_BLOCKS with a 30 s hold, stopped 0-5 s into the hold;
 * - moving inversion: MOVING_INVERSION_WALKING sweeps;
 * - turnaround: RANDOM, TURNAROUND.
 *
 * A counting_memory_access observer detects the phases. It only raises an atomic flag and sets
 * an auto-reset event, on a worker thread; the test thread waits on that event, so every stop
 * is requested, and its QueryPerformanceCounter instant taken, on the test thread. For traffic
 * phases the test first waits for the phase, sleeps a random 0-200 ms, then re-arms the
 * observer and stops at once on the next primitive of that phase, so the stop lands inside it
 * rather than wherever the sleep happened to end. The hold is detected when every worker has
 * issued both fences of its pre-hold flush, just before the coordinator enters the hold.
 *
 * The gate is the 2 s bound from the stop request to on_completed, which ramquick_engine.h
 * places on the engine. Release time grows with the region size and is reported, not bounded,
 * so release_ms and released_bytes are recorded as XML properties and printed but never gated.
 * TearDownTestSuite records the largest stop latency of the run.
 *
 * Random points come from a std::mt19937_64 per case, seeded from a suite seed recorded as an
 * XML property; setting RAMQUICK_STOP_TEST_SEED to that value replays the same delays and engine
 * seeds.
 *
 * Nothing here needs elevation: the engine raises the minimum working set before locking, which
 * the default policy permits. The suite fails, rather than skips, when less than 2 GiB of
 * physical memory is available, because the latency it gates is defined on a 1 GiB region.
 *
 * CloseHandleChecked and CloseHandleCheckedFailure cover close_handle_checked, through which the
 * stop signal and the engine's other handle owners close their handles: a failed close is
 * reported once, to the caller and to the debugger, and never retried. They need neither memory
 * nor elevation, so the 2 GiB precondition does not apply to them.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <mutex>
#include <optional>
#include <ostream>
#include <random>
#include <stdexcept>
#include <string>
#include <system_error>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/memory/memory_access.h"
#include "core/session.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_mib = std::uint64_t{1} << 20U;
constexpr std::uint64_t k_gib = std::uint64_t{1} << 30U;

/** The region under test is the smallest one of whole 2 MiB slices that reaches 1 GiB. */
constexpr std::uint64_t k_target_region_bytes = k_gib;

/** Below this much available physical memory the suite fails its precondition. */
constexpr std::uint64_t k_min_available_bytes = 2 * k_gib;

/** Workers are capped so that every slice of the 1 GiB region holds at least 4 MiB. */
constexpr std::uint64_t k_min_worker_slice_bytes = 4 * k_mib;
constexpr std::uint32_t k_max_workers =
    static_cast<std::uint32_t>(k_target_region_bytes / k_min_worker_slice_bytes);

/** The single step's budget: far beyond every stop point, so the stop lands inside it. */
constexpr std::uint32_t k_step_budget_ms = 60'000U;
/** Hold of the hold case: the stop lands 0-5 s into it. */
constexpr std::uint32_t k_hold_ms = 30'000U;
/** 25 %, any value > 0: coverage budgets never extend a step. */
constexpr std::uint32_t k_coverage_budget_bp = 2'500U;
/** As the application passes them. */
constexpr std::uint32_t k_error_detail_cap = 10'000U;
constexpr std::uint32_t k_progress_interval_ms = 250U;

/** The bound under test: stop request to on_completed. */
constexpr std::uint64_t k_ns_per_ms = 1'000'000U;
constexpr std::uint64_t k_stop_bound_ns = 2'000U * k_ns_per_ms;

/** Waits that only keep a broken engine from hanging the suite; none of them is a gate. */
constexpr DWORD k_phase_timeout_ms = 30'000U;
constexpr std::chrono::milliseconds k_completed_timeout{10'000};
constexpr std::chrono::milliseconds k_released_timeout{120'000};

/** Random stop points per phase kind; an int because ::testing::Range generates the points. */
constexpr int k_points_per_kind = 3;
constexpr std::uint64_t k_points_per_kind_u64 = static_cast<std::uint64_t>(k_points_per_kind);

/**
 * Attempts of the preparing case. Each attempt that finds on_prepared already raised aims the
 * next one at under half its delay and its measured preparation time.
 */
constexpr std::uint32_t k_prepare_attempts = 8U;

/** Environment variable that replays a recorded suite seed. */
constexpr char k_seed_variable[] = "RAMQUICK_STOP_TEST_SEED";

/* ---------------------------------------------------------------------------------------------
 * Cases
 * ------------------------------------------------------------------------------------------- */

/** Where the stop lands. */
enum class stop_case : std::uint32_t {
    preparing,
    windowed_write,
    windowed_verify,
    hold,
    moving_inversion,
    turnaround,
};

/** One phase kind: its step, the phase the observer waits for and the random-delay range. */
struct case_spec {
    stop_case kind;
    /** Test-name fragment and XML property suffix. */
    const char* label;
    const char* step_name;
    std::uint32_t pattern;
    /** Moving inversion ignores the order, but validation still requires a valid one. */
    std::uint32_t access_order;
    std::uint32_t hold_ms;
    /** Phase that triggers the stop; unused by the preparing case. */
    phase_kind target;
    /** Upper bound of the random delay: after start (preparing) or after the phase began. */
    std::uint32_t max_delay_ms;
};

constexpr std::array<case_spec, 6> k_cases{{
    {stop_case::preparing, "preparing", "Stop while preparing", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::write, 300U},
    {stop_case::windowed_write, "windowed_write", "Stop in windowed write", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::write, 200U},
    {stop_case::windowed_verify, "windowed_verify", "Stop in windowed verify", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::verify, 200U},
    {stop_case::hold, "hold", "Stop in refresh hold", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS,
     k_hold_ms, phase_kind::pre_hold_flush, 5'000U},
    {stop_case::moving_inversion, "moving_inversion", "Stop in moving inversion",
     RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::sweep,
     200U},
    {stop_case::turnaround, "turnaround", "Stop in turnaround", RQ_PATTERN_RANDOM,
     RQ_ORDER_TURNAROUND, 0U, phase_kind::turnaround, 200U},
}};

constexpr bool cases_in_enum_order() noexcept {
    for (std::size_t i = 0; i < k_cases.size(); ++i) {
        if (static_cast<std::size_t>(k_cases[i].kind) != i) {
            return false;
        }
    }
    return true;
}
static_assert(cases_in_enum_order(), "k_cases is indexed by stop_case");

const case_spec& spec_for(stop_case kind) noexcept {
    return k_cases[static_cast<std::size_t>(kind)];
}

/** gtest prints parameters through this, so failures name the case instead of raw bytes. */
void PrintTo(stop_case kind, std::ostream* os) {
    *os << spec_for(kind).label;
}

const char* phase_name(phase_kind phase) noexcept {
    switch (phase) {
    case phase_kind::write:
        return "write";
    case phase_kind::pre_hold_flush:
        return "pre_hold_flush";
    case phase_kind::verify:
        return "verify";
    case phase_kind::sweep:
        return "sweep";
    case phase_kind::turnaround:
        return "turnaround";
    case phase_kind::reread:
        return "reread";
    }
    return "invalid phase";
}

/* ---------------------------------------------------------------------------------------------
 * Time, seeds and formatting
 * ------------------------------------------------------------------------------------------- */

/** QueryPerformanceCounter in nanoseconds; one clock for the test thread and every callback. */
std::uint64_t qpc_now_ns() noexcept {
    static const qpc_clock clock;
    return clock.now_ns();
}

/** Nanoseconds from then to now; 0 if now is not later. */
constexpr std::uint64_t since(std::uint64_t now, std::uint64_t then) noexcept {
    return now > then ? now - then : 0;
}

double to_ms(std::uint64_t ns) noexcept {
    return static_cast<double>(ns) / static_cast<double>(k_ns_per_ms);
}

std::string format_ms(std::uint64_t ns) {
    char text[32];
    std::snprintf(text, sizeof(text), "%.3f", to_ms(ns));
    return text;
}

/** 0x followed by 16 uppercase hex digits. */
std::string hex64(std::uint64_t value) {
    char text[19];
    std::snprintf(text, sizeof(text), "0x%016llX", static_cast<unsigned long long>(value));
    return text;
}

/** SplitMix64 finaliser: derives an independent per-case seed from the suite seed. */
constexpr std::uint64_t mix_seed(std::uint64_t seed, std::uint64_t salt) noexcept {
    std::uint64_t z = seed + 0x9E3779B97F4A7C15ULL * (salt + 1U);
    z = (z ^ (z >> 30U)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27U)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31U);
}

std::uint32_t random_delay_ms(std::mt19937_64& rng, std::uint32_t max_ms) {
    return std::uniform_int_distribution<std::uint32_t>(0U, max_ms)(rng);
}

/**
 * Bytes of the region under test for the given worker count: whole 2 MiB slices reaching at
 * least 1 GiB.
 *
 * The engine rounds each slice down to k_min_slice_bytes and ends a run whose rounded slices
 * test fewer than min_region_bytes with RQ_END_INSUFFICIENT_MEMORY before locking anything. A
 * literal 1 GiB region would therefore fail on every logical-processor count that does not
 * divide 512; rounding the slice up keeps one worker per logical processor and at least 1 GiB
 * locked, and equals 1 GiB exactly on power-of-two counts.
 */
constexpr std::uint64_t region_bytes_for(std::uint32_t workers) noexcept {
    const std::uint64_t per_worker = (k_target_region_bytes + workers - 1U) / workers;
    const std::uint64_t slice =
        (per_worker + k_min_slice_bytes - 1U) / k_min_slice_bytes * k_min_slice_bytes;
    return slice * workers;
}
static_assert(region_bytes_for(128U) == k_gib, "power-of-two worker counts test exactly 1 GiB");
static_assert(region_bytes_for(12U) >= k_gib &&
                  region_bytes_for(12U) % (12U * k_min_slice_bytes) == 0,
              "other counts round each slice up to whole 2 MiB");

std::string end_reason_name(std::uint32_t reason) {
    switch (reason) {
    case RQ_END_COMPLETED:
        return "COMPLETED";
    case RQ_END_FIRST_ERROR:
        return "FIRST_ERROR";
    case RQ_END_USER_STOP:
        return "USER_STOP";
    case RQ_END_THERMAL:
        return "THERMAL";
    case RQ_END_HOST_FAULT:
        return "HOST_FAULT";
    case RQ_END_INSUFFICIENT_MEMORY:
        return "INSUFFICIENT_MEMORY";
    case RQ_END_AFFINITY_FAILED:
        return "AFFINITY_FAILED";
    case RQ_END_INTERNAL_FAULT:
        return "INTERNAL_FAULT";
    default:
        return "end reason " + std::to_string(reason);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Callback recorder
 * ------------------------------------------------------------------------------------------- */

/** What one session's callbacks delivered, with the QPC instant of each lifecycle event. */
struct run_record {
    std::uint32_t gates = 0;
    std::uint32_t prepared_calls = 0;
    std::uint64_t prepared_ns = 0;
    rq_prepared prepared_info{};

    std::uint32_t completed_calls = 0;
    std::uint64_t completed_ns = 0;
    /** The on_completed payload; step_results is cleared, its pointer was valid for the call. */
    rq_run_result result{};
    /** The single step's result, when step_result_count is 1. */
    bool has_step_result = false;
    rq_step_result step_result{};

    std::uint32_t released_calls = 0;
    std::uint64_t released_ns = 0;
    std::uint64_t released_bytes = 0;
    std::uint64_t release_ms = 0;

    bool prepared() const noexcept { return prepared_calls != 0; }
    bool completed() const noexcept { return completed_calls != 0; }
    bool released() const noexcept { return released_calls != 0; }

    std::string end_detail() const {
        const char* const begin = result.end_detail;
        return std::string(begin, std::find(begin, begin + RQ_END_DETAIL_SIZE, '\0'));
    }

    /** One line for failure messages. */
    std::string describe() const {
        std::string text = "prepared=" + std::to_string(prepared_calls) +
                           " gates=" + std::to_string(gates) +
                           " completed=" + std::to_string(completed_calls) +
                           " released=" + std::to_string(released_calls);
        if (completed()) {
            text += " end_reason=" + end_reason_name(result.end_reason) +
                    " os_error=" + std::to_string(result.os_error) + " end_detail='" +
                    end_detail() + "'";
        }
        return text;
    }
};

/**
 * The session's callback set. Every callback runs on the coordinator; each one only stamps the
 * time, copies its payload under the lock and notifies, so none of them delays the engine.
 */
class run_recorder {
public:
    run_recorder() = default;
    run_recorder(const run_recorder&) = delete;
    run_recorder& operator=(const run_recorder&) = delete;

    /** All seven callbacks, which rq_session_create requires, with this as user_data. */
    rq_callbacks callbacks() noexcept {
        rq_callbacks set{};
        set.struct_size = sizeof(rq_callbacks);
        set.reserved0 = 0;
        set.user_data = this;
        set.on_prepared = &run_recorder::on_prepared;
        set.on_step_starting = &run_recorder::on_step_starting;
        set.on_step_finished = &run_recorder::on_step_finished;
        set.on_progress = &run_recorder::on_progress;
        set.on_errors = &run_recorder::on_errors;
        set.on_completed = &run_recorder::on_completed;
        set.on_released = &run_recorder::on_released;
        return set;
    }

    bool wait_completed(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [this] { return record_.completed(); });
    }

    bool wait_released(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [this] { return record_.released(); });
    }

    run_record snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return record_;
    }

private:
    static run_recorder& self(void* user_data) noexcept {
        return *static_cast<run_recorder*>(user_data);
    }

    static void RQ_CALL on_prepared(void* user_data, const rq_prepared* info) noexcept {
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            ++recorder.record_.prepared_calls;
            recorder.record_.prepared_ns = now;
            recorder.record_.prepared_info = *info;
        }
        recorder.changed_.notify_all();
    }

    static std::uint32_t RQ_CALL on_step_starting(void* user_data, const rq_step_start*) noexcept {
        run_recorder& recorder = self(user_data);
        std::lock_guard<std::mutex> lock(recorder.mutex_);
        ++recorder.record_.gates;
        return RQ_ACTION_CONTINUE;
    }

    static void RQ_CALL on_step_finished(void*, const rq_step_result*) noexcept {}

    static void RQ_CALL on_progress(void*, const rq_progress*) noexcept {}

    static void RQ_CALL on_errors(void*, const rq_error_record*, std::uint32_t,
                                  std::uint64_t) noexcept {}

    static void RQ_CALL on_completed(void* user_data, const rq_run_result* result) noexcept {
        // Stamped first: the bound under test ends at this callback's entry.
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            run_record& record = recorder.record_;
            ++record.completed_calls;
            record.completed_ns = now;
            record.result = *result;
            record.result.step_results = nullptr;
            record.has_step_result = result->step_result_count == 1U;
            if (record.has_step_result) {
                record.step_result = result->step_results[0];
            }
        }
        recorder.changed_.notify_all();
    }

    static void RQ_CALL on_released(void* user_data, std::uint64_t released_bytes,
                                    std::uint64_t release_ms) noexcept {
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            ++recorder.record_.released_calls;
            recorder.record_.released_ns = now;
            recorder.record_.released_bytes = released_bytes;
            recorder.record_.release_ms = release_ms;
        }
        recorder.changed_.notify_all();
    }

    mutable std::mutex mutex_;
    std::condition_variable changed_;
    run_record record_;
};

/* ---------------------------------------------------------------------------------------------
 * Phase trigger
 * ------------------------------------------------------------------------------------------- */

/** Owns a Win32 event handle. */
class event_handle {
public:
    /** An unsignalled auto-reset event. @throws std::system_error when it cannot be created. */
    event_handle() : handle_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
        if (handle_ == nullptr) {
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                    "CreateEventW failed");
        }
    }
    ~event_handle() { CloseHandle(handle_); }
    event_handle(const event_handle&) = delete;
    event_handle& operator=(const event_handle&) = delete;

    HANDLE get() const noexcept { return handle_; }

private:
    HANDLE handle_;
};

/**
 * The counting layer's observer: signals the test thread when the target phase is reached.
 *
 * observe() runs on worker threads, after each primitive has been forwarded. It never calls a
 * session function and never blocks: it reads and swaps atomics and sets an auto-reset event.
 * - Traffic phases: while armed, the first primitive of the target phase disarms the trigger
 *   and sets the event. arm() rearms it for the next primitive of that phase.
 * - Hold (hold_entry_fences != 0): every worker's pre-hold flush is a fence, its flushes and a
 *   fence, so the event is set when pass 0 has issued hold_entry_fences pre_hold_flush fences,
 *   two per worker: the moment the coordinator crosses the barrier into the hold. Verify-phase
 *   primitives are counted too, because a stop inside the hold must leave none.
 */
class phase_trigger {
public:
    phase_trigger(phase_kind target, std::uint32_t hold_entry_fences)
        : target_(target), hold_entry_fences_(hold_entry_fences) {}
    phase_trigger(const phase_trigger&) = delete;
    phase_trigger& operator=(const phase_trigger&) = delete;

    void observe(const primitive_record& record) noexcept {
        const phase_kind phase = record.ctx.phase;
        if (hold_entry_fences_ != 0U) {
            if (phase == phase_kind::pre_hold_flush && record.kind == primitive_kind::fence &&
                record.ctx.pass_index == 0U &&
                hold_fences_.fetch_add(1U, std::memory_order_acq_rel) + 1U == hold_entry_fences_) {
                fire();
            } else if (phase == phase_kind::verify) {
                verify_primitives_.fetch_add(1U, std::memory_order_relaxed);
            }
            return;
        }
        // The plain load keeps the shared flag read-mostly while disarmed.
        if (phase == target_ && armed_.load(std::memory_order_relaxed) &&
            armed_.exchange(false, std::memory_order_acq_rel)) {
            fire();
        }
    }

    /** Rearms a traffic-phase trigger after its event has been consumed. */
    void arm() noexcept { armed_.store(true, std::memory_order_release); }

    /** Waits for the event; true when it was set within the timeout. */
    bool wait(DWORD timeout_ms) const noexcept {
        return WaitForSingleObject(event_.get(), timeout_ms) == WAIT_OBJECT_0;
    }

    /** QPC instant of the last firing; 0 before the first. */
    std::uint64_t fired_ns() const noexcept { return fired_ns_.load(std::memory_order_acquire); }

    /** Hold mode only: verify-phase primitives seen so far. */
    std::uint64_t verify_primitives() const noexcept {
        return verify_primitives_.load(std::memory_order_relaxed);
    }

private:
    void fire() noexcept {
        fired_ns_.store(qpc_now_ns(), std::memory_order_release);
        SetEvent(event_.get());
    }

    const phase_kind target_;
    const std::uint32_t hold_entry_fences_;
    event_handle event_;
    std::atomic<bool> armed_{true};
    std::atomic<std::uint32_t> hold_fences_{0};
    std::atomic<std::uint64_t> verify_primitives_{0};
    std::atomic<std::uint64_t> fired_ns_{0};
};

/* ---------------------------------------------------------------------------------------------
 * Session under test
 * ------------------------------------------------------------------------------------------- */

/**
 * One production session over a counting layer: its single-step plan, its recorder and its
 * phase trigger. The destructor destroys the session first, which joins the coordinator and
 * guarantees no callback or observer call runs after it, so the recorder and the trigger
 * outlive every engine reference to them on every exit path.
 */
class stop_session {
public:
    stop_session(const case_spec& spec, std::uint32_t workers, std::uint64_t engine_seed)
        : trigger_(spec.target, spec.target == phase_kind::pre_hold_flush ? 2U * workers : 0U) {
        copy_utf8_truncated(step_.name, sizeof(step_.name), spec.step_name);
        step_.pattern = spec.pattern;
        step_.access_order = spec.access_order;
        step_.thread_mode = RQ_THREADS_ALL;
        step_.hold_ms = spec.hold_ms;
        step_.time_budget_ms = k_step_budget_ms;
        step_.coverage_budget_bp = k_coverage_budget_bp;

        config_.struct_size = sizeof(rq_session_config);
        config_.step_def_size = sizeof(rq_step_def);
        config_.region_bytes = region_bytes_for(workers);
        config_.min_region_bytes = config_.region_bytes;
        config_.seed = engine_seed;
        config_.worker_count = workers;
        config_.stop_on_error = 0U;
        config_.error_detail_cap = k_error_detail_cap;
        config_.progress_interval_ms = k_progress_interval_ms;
        config_.isa_override = RQ_ISA_AUTO;
        config_.step_count = 1U;
        config_.steps = &step_;
    }

    ~stop_session() {
        if (handle_ != nullptr) {
            session_destroy(handle_);
        }
    }

    stop_session(const stop_session&) = delete;
    stop_session& operator=(const stop_session&) = delete;

    /** session_create with the production clock and affinity and a counting layer. */
    rq_result create() {
        session_dependencies dependencies;
        dependencies.wrap_memory_access =
            [this](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
            auto counting = std::make_unique<counting_memory_access>(std::move(real));
            phase_trigger* const trigger = &trigger_;
            counting->set_observer(
                [trigger](const primitive_record& record) noexcept { trigger->observe(record); });
            layer_ = counting.get();
            return std::unique_ptr<IMemoryAccess>(std::move(counting));
        };
        const rq_callbacks callbacks = recorder_.callbacks();
        return session_create(&config_, &callbacks, std::move(dependencies), &handle_);
    }

    rq_result start() { return session_start(handle_); }

    /** The stop request: its QPC instant is taken on this thread just before the call. */
    rq_result stop_now(std::uint64_t& stop_ns) {
        stop_ns = qpc_now_ns();
        return session_stop(handle_, RQ_STOP_USER);
    }

    rq_result destroy() {
        const rq_result result = session_destroy(handle_);
        handle_ = nullptr;
        return result;
    }

    const rq_session_config& config() const noexcept { return config_; }
    run_recorder& recorder() noexcept { return recorder_; }
    phase_trigger& trigger() noexcept { return trigger_; }

    /** Primitives issued through the counting layer; exact once Completed has been raised. */
    std::uint64_t primitive_count() const noexcept {
        return layer_ != nullptr ? layer_->primitive_count() : 0U;
    }

private:
    rq_step_def step_{};
    rq_session_config config_{};
    run_recorder recorder_;
    phase_trigger trigger_;
    // Owned by the session; valid until the session is destroyed.
    counting_memory_access* layer_ = nullptr;
    rq_session_handle handle_ = nullptr;
};

/* ---------------------------------------------------------------------------------------------
 * Fixture
 * ------------------------------------------------------------------------------------------- */

using stop_param = std::tuple<stop_case, int>;

class StopLatency : public ::testing::TestWithParam<stop_param> {
protected:
    /** Resolves and records the suite seed once for every case. */
    static void SetUpTestSuite() {
        bool replayed = false;
        char text[32] = {};
        const DWORD length = GetEnvironmentVariableA(k_seed_variable, text, sizeof(text));
        if (length > 0U && length < sizeof(text)) {
            char* end = nullptr;
            const unsigned long long parsed = std::strtoull(text, &end, 0);
            replayed = end != text && *end == '\0';
            if (replayed) {
                suite_seed_ = static_cast<std::uint64_t>(parsed);
            } else {
                ADD_FAILURE() << k_seed_variable << "='" << text
                              << "' is not an unsigned integer; set it to a recorded suite_seed";
            }
        }
        if (!replayed) {
            std::random_device device;
            suite_seed_ = (std::uint64_t{device()} << 32U) ^ device();
        }
        max_latency_ns_ = 0;
        max_latency_label_.clear();
        measurements_ = 0;
        RecordProperty("suite_seed", hex64(suite_seed_));
        std::printf("[ stop     ] suite seed %s (replay with %s)\n", hex64(suite_seed_).c_str(),
                    k_seed_variable);
        std::fflush(stdout);
    }

    /** The largest stop-to-Completed latency of the run, as a suite property and a line. */
    static void TearDownTestSuite() {
        if (measurements_ == 0U) {
            return;
        }
        RecordProperty("max_stop_latency_ms", format_ms(max_latency_ns_));
        RecordProperty("max_stop_latency_case", max_latency_label_);
        std::printf("[ stop     ] max stop -> Completed latency %s ms (%s) over %u stops; bound "
                    "%s ms\n",
                    format_ms(max_latency_ns_).c_str(), max_latency_label_.c_str(), measurements_,
                    format_ms(k_stop_bound_ns).c_str());
        std::fflush(stdout);
    }

    /**
     * Precondition: the region must be lockable without starving the machine. Fails rather
     * than skips, so a short machine never reports a pass it did not measure.
     */
    void SetUp() override {
        rq_system_info info{};
        info.struct_size = sizeof(rq_system_info);
        ASSERT_EQ(query_system(&info), RQ_OK);
        ASSERT_GE(info.avail_phys_bytes, k_min_available_bytes)
            << "Precondition failed: the stop-latency tests lock a 1 GiB region and need at "
               "least 2 GiB of available physical memory, but only "
            << info.avail_phys_bytes / k_mib << " MiB is available. Close other applications.";
        const std::size_t processors = enumerate_logical_processors().size();
        workers_ = static_cast<std::uint32_t>(std::min<std::size_t>(processors, k_max_workers));
        ASSERT_GT(workers_, 0U);
    }

    void run_preparing_case(const case_spec& spec, const std::string& label, std::mt19937_64& rng);
    void run_traffic_case(const case_spec& spec, const std::string& label, std::mt19937_64& rng);

    /** Checks shared by every stop: reason, bound, event order and full release. */
    static void expect_stopped_within_bound(const run_record& record, std::uint64_t stop_ns,
                                            const std::string& label);

    /** Records one stop's latency and release time as XML properties and a log line. */
    static void report(const std::string& label, std::uint32_t delay_ms, std::uint64_t stop_ns,
                       const run_record& record);

    static inline std::uint64_t suite_seed_ = 0;
    static inline std::uint64_t max_latency_ns_ = 0;
    static inline std::string max_latency_label_;
    static inline std::uint32_t measurements_ = 0;

    std::uint32_t workers_ = 0;
};

void StopLatency::expect_stopped_within_bound(const run_record& record, std::uint64_t stop_ns,
                                              const std::string& label) {
    EXPECT_EQ(record.result.end_reason, RQ_END_USER_STOP) << label << ": " << record.describe();
    const std::uint64_t latency_ns = since(record.completed_ns, stop_ns);
    EXPECT_GE(record.completed_ns, stop_ns) << label << ": Completed preceded the stop request";
    EXPECT_LE(latency_ns, k_stop_bound_ns)
        << label << ": Completed arrived " << format_ms(latency_ns)
        << " ms after the stop request; the bound is 2 s";
    EXPECT_EQ(record.completed_calls, 1U) << label;
    EXPECT_EQ(record.released_calls, 1U) << label;
    EXPECT_GE(record.released_ns, record.completed_ns) << label << ": Released preceded Completed";
    if (record.prepared()) {
        // Every chunk the run locked is released, whatever the phase the stop met.
        EXPECT_EQ(record.released_bytes, record.prepared_info.tested_bytes) << label;
    }
}

void StopLatency::report(const std::string& label, std::uint32_t delay_ms, std::uint64_t stop_ns,
                         const run_record& record) {
    const std::uint64_t latency_ns = since(record.completed_ns, stop_ns);
    // Release time is measured and logged only: it grows with the region, and the 2 s bound
    // ends at Completed.
    RecordProperty("stop_delay_ms_" + label, delay_ms);
    RecordProperty("stop_latency_ms_" + label, format_ms(latency_ns));
    RecordProperty("release_ms_" + label, record.release_ms);
    RecordProperty("released_bytes_" + label, record.released_bytes);
    std::printf("[ stop     ] %s: delay %u ms, stop -> Completed %s ms, release %llu ms "
                "(%llu MiB)\n",
                label.c_str(), delay_ms, format_ms(latency_ns).c_str(),
                static_cast<unsigned long long>(record.release_ms),
                static_cast<unsigned long long>(record.released_bytes / k_mib));
    std::fflush(stdout);

    ++measurements_;
    if (latency_ns >= max_latency_ns_) {
        max_latency_ns_ = latency_ns;
        max_latency_label_ = label;
    }
}

void StopLatency::run_preparing_case(const case_spec& spec, const std::string& label,
                                     std::mt19937_64& rng) {
    std::uint32_t max_delay_ms = spec.max_delay_ms;
    for (std::uint32_t attempt = 0; attempt < k_prepare_attempts; ++attempt) {
        const std::string attempt_label = label + "_attempt" + std::to_string(attempt);
        stop_session run(spec, workers_, rng());
        ASSERT_EQ(run.create(), RQ_OK) << attempt_label;
        const std::uint32_t delay_ms = random_delay_ms(rng, max_delay_ms);
        ASSERT_EQ(run.start(), RQ_OK) << attempt_label;
        Sleep(delay_ms);
        std::uint64_t stop_ns = 0;
        ASSERT_EQ(run.stop_now(stop_ns), RQ_OK) << attempt_label;

        ASSERT_TRUE(run.recorder().wait_completed(k_completed_timeout))
            << attempt_label << ": no Completed within 10 s of the stop; "
            << run.recorder().snapshot().describe();
        ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
            << attempt_label << ": no Released; " << run.recorder().snapshot().describe();
        const run_record record = run.recorder().snapshot();
        const std::uint64_t primitives = run.primitive_count();
        ASSERT_EQ(run.destroy(), RQ_OK) << attempt_label;

        expect_stopped_within_bound(record, stop_ns, attempt_label);
        report(attempt_label, delay_ms, stop_ns, record);
        if (!record.prepared()) {
            // The stop met preparation: no step gate, no step result and no memory traffic,
            // and whatever was locked before the stop is released.
            EXPECT_EQ(record.gates, 0U) << attempt_label;
            EXPECT_EQ(record.result.step_result_count, 0U) << attempt_label;
            EXPECT_EQ(primitives, 0U) << attempt_label << ": a stop during preparation ran traffic";
            EXPECT_LE(record.released_bytes, run.config().region_bytes) << attempt_label;
            RecordProperty("preparing_attempts", attempt + 1U);
            return;
        }
        // Preparation finished before the stop, which then met the step. Aim the next attempt
        // at under half of both this delay and the preparation time just measured.
        const std::uint64_t aim_ms =
            std::min<std::uint64_t>(delay_ms, record.prepared_info.prepare_ms) / 2U;
        max_delay_ms = static_cast<std::uint32_t>(aim_ms);
        std::printf("[ stop     ] %s: Prepared arrived first (preparation took %llu ms); next "
                    "delay at most %u ms\n",
                    attempt_label.c_str(),
                    static_cast<unsigned long long>(record.prepared_info.prepare_ms), max_delay_ms);
        std::fflush(stdout);
    }
    FAIL() << label << ": preparation finished before the stop in all " << k_prepare_attempts
           << " attempts, so no stop met the Preparing state";
}

void StopLatency::run_traffic_case(const case_spec& spec, const std::string& label,
                                   std::mt19937_64& rng) {
    const bool hold = spec.target == phase_kind::pre_hold_flush;
    stop_session run(spec, workers_, rng());
    ASSERT_EQ(run.create(), RQ_OK) << label;
    ASSERT_EQ(run.start(), RQ_OK) << label;

    // Preparation and the step's first primitives come first; a run that ends early, for
    // example for lack of lockable memory, shows its end reason here.
    ASSERT_TRUE(run.trigger().wait(k_phase_timeout_ms))
        << label << ": phase " << phase_name(spec.target) << " not reached within 30 s; "
        << run.recorder().snapshot().describe();
    const std::uint32_t delay_ms = random_delay_ms(rng, spec.max_delay_ms);
    Sleep(delay_ms);
    if (!hold) {
        // The sleep ends anywhere in the pass; the next primitive of the target phase puts the
        // stop back inside that phase.
        run.trigger().arm();
        ASSERT_TRUE(run.trigger().wait(k_phase_timeout_ms))
            << label << ": phase " << phase_name(spec.target) << " did not recur; "
            << run.recorder().snapshot().describe();
    }
    std::uint64_t stop_ns = 0;
    ASSERT_EQ(run.stop_now(stop_ns), RQ_OK) << label;

    ASSERT_TRUE(run.recorder().wait_completed(k_completed_timeout))
        << label << ": no Completed within 10 s of the stop; "
        << run.recorder().snapshot().describe();
    ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
        << label << ": no Released; " << run.recorder().snapshot().describe();
    const run_record record = run.recorder().snapshot();
    const std::uint64_t primitives = run.primitive_count();
    const std::uint64_t post_hold_verifies = run.trigger().verify_primitives();
    const std::uint64_t phase_to_stop_ns = since(stop_ns, run.trigger().fired_ns());
    ASSERT_EQ(run.destroy(), RQ_OK) << label;

    expect_stopped_within_bound(record, stop_ns, label);
    // The stop landed inside the step: it was prepared, its gate continued, and its single
    // result is cut short.
    EXPECT_TRUE(record.prepared()) << label;
    EXPECT_EQ(record.prepared_info.tested_bytes, run.config().region_bytes) << label;
    EXPECT_EQ(record.gates, 1U) << label;
    ASSERT_TRUE(record.has_step_result) << label << ": " << record.describe();
    EXPECT_EQ(record.step_result.index, 0U) << label;
    EXPECT_EQ(record.step_result.completed, 0U) << label << ": the step ran to its schedule";
    EXPECT_GT(primitives, 0U) << label;
    if (hold) {
        // A stop inside the 30 s hold ends the pass before its verification.
        EXPECT_EQ(post_hold_verifies, 0U) << label << ": the stop landed after the hold";
    }
    RecordProperty("phase_to_stop_ms_" + label, format_ms(phase_to_stop_ns));
    report(label, delay_ms, stop_ns, record);
}

/* ---------------------------------------------------------------------------------------------
 * Tests
 * ------------------------------------------------------------------------------------------- */

TEST_P(StopLatency, CompletedWithinTwoSecondsOfStop) {
    const auto& [kind, point] = GetParam();
    const case_spec& spec = spec_for(kind);
    const std::string label = std::string(spec.label) + "_" + std::to_string(point);
    const std::uint64_t salt = static_cast<std::uint64_t>(kind) * k_points_per_kind_u64 +
                               static_cast<std::uint64_t>(point);
    const std::uint64_t case_seed = mix_seed(suite_seed_, salt);
    RecordProperty("case_seed", hex64(case_seed));
    std::mt19937_64 rng(case_seed);

    if (kind == stop_case::preparing) {
        run_preparing_case(spec, label, rng);
    } else {
        run_traffic_case(spec, label, rng);
    }
}

/** Test-name suffix: the phase kind and the point, for example hold_2. */
std::string case_name(const ::testing::TestParamInfo<stop_param>& info) {
    return std::string(spec_for(std::get<0>(info.param)).label) + "_" +
           std::to_string(std::get<1>(info.param));
}

const auto k_all_cases =
    ::testing::Values(stop_case::preparing, stop_case::windowed_write, stop_case::windowed_verify,
                      stop_case::hold, stop_case::moving_inversion, stop_case::turnaround);

INSTANTIATE_TEST_SUITE_P(StopAtRandomPoints, StopLatency,
                         ::testing::Combine(k_all_cases, ::testing::Range(0, k_points_per_kind)),
                         case_name);

/* ---------------------------------------------------------------------------------------------
 * Checked handle close
 * ------------------------------------------------------------------------------------------- */

/**
 * Collects the lines the constructing thread writes with OutputDebugStringW while it lives.
 *
 * OutputDebugStringW raises DBG_PRINTEXCEPTION_WIDE_C, whose first two parameters are the line's
 * length including its terminator and its address, and handles that exception itself when no
 * debugger is attached. A first vectored handler sees it before then, so the line can be read in
 * process, without a debugger and without the DBWIN buffer that every process on the host shares.
 * The handler only copies the text, never allocates, and continues the search for every
 * exception, so the output is unchanged and a fault still ends the process as the engine's error
 * contract requires. Lines of other threads are ignored. One capture may exist at a time.
 */
class debugger_line_capture {
public:
    /** @throws std::runtime_error when the vectored handler cannot be registered. */
    debugger_line_capture() : thread_id_(GetCurrentThreadId()) {
        active_.store(this, std::memory_order_release);
        cookie_ = AddVectoredExceptionHandler(1, &debugger_line_capture::on_exception);
        if (cookie_ == nullptr) {
            active_.store(nullptr, std::memory_order_release);
            throw std::runtime_error("AddVectoredExceptionHandler failed");
        }
    }
    ~debugger_line_capture() {
        RemoveVectoredExceptionHandler(cookie_);
        active_.store(nullptr, std::memory_order_release);
    }
    debugger_line_capture(const debugger_line_capture&) = delete;
    debugger_line_capture& operator=(const debugger_line_capture&) = delete;

    /** Every line written so far; one beyond k_max_lines is counted as an empty entry. */
    std::vector<std::wstring> lines() const {
        std::vector<std::wstring> result;
        for (std::size_t i = 0; i < count_; ++i) {
            result.emplace_back(i < k_max_lines ? std::wstring(text_[i].data(), length_[i])
                                                : std::wstring());
        }
        return result;
    }

private:
    static constexpr std::size_t k_max_lines = 8;
    static constexpr std::size_t k_max_line_chars = 512;

    static LONG CALLBACK on_exception(EXCEPTION_POINTERS* info) noexcept {
        const EXCEPTION_RECORD& record = *info->ExceptionRecord;
        debugger_line_capture* const capture = active_.load(std::memory_order_acquire);
        if (record.ExceptionCode != DBG_PRINTEXCEPTION_WIDE_C || record.NumberParameters < 2 ||
            capture == nullptr || GetCurrentThreadId() != capture->thread_id_) {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const std::size_t index = capture->count_++;
        const auto* const text = reinterpret_cast<const wchar_t*>(record.ExceptionInformation[1]);
        const std::size_t with_terminator =
            static_cast<std::size_t>(record.ExceptionInformation[0]);
        if (index < k_max_lines && text != nullptr && with_terminator > 0) {
            const std::size_t length = std::min(with_terminator - 1, k_max_line_chars);
            std::copy_n(text, length, capture->text_[index].data());
            capture->length_[index] = length;
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    static inline std::atomic<debugger_line_capture*> active_{nullptr};

    const DWORD thread_id_;
    PVOID cookie_ = nullptr;
    // Written only by the handler on thread_id_, which is also the only reader.
    std::size_t count_ = 0;
    std::array<std::array<wchar_t, k_max_line_chars>, k_max_lines> text_{};
    std::array<std::size_t, k_max_lines> length_{};
};

/**
 * A name no other event has, so a test can confirm that an event object was destroyed by opening
 * it by name, instead of probing a closed handle value the process may already have reused.
 */
std::wstring unique_event_name() {
    static std::atomic<std::uint32_t> next{0};
    return L"Local\\RAMQuick.engine.tests.CloseHandleChecked." +
           std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(next.fetch_add(1));
}

/** Creates an unsignalled manual-reset event under a new name. */
HANDLE create_named_event(const std::wstring& name) {
    const HANDLE event = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    if (event == nullptr) {
        ADD_FAILURE() << "CreateEventW failed (Windows error " << GetLastError() << ")";
    }
    return event;
}

/** Succeeds when no event object of this name exists any more: its last handle was closed. */
::testing::AssertionResult event_destroyed(const std::wstring& name) {
    const HANDLE reopened = OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
    if (reopened != nullptr) {
        close_handle_checked(reopened, L"reopened test event");
        return ::testing::AssertionFailure() << "the event object still exists";
    }
    const DWORD error = GetLastError();
    if (error != ERROR_FILE_NOT_FOUND) {
        return ::testing::AssertionFailure()
               << "OpenEventW failed with Windows error " << error << ", not ERROR_FILE_NOT_FOUND";
    }
    return ::testing::AssertionSuccess();
}

/**
 * Under a debugger CloseHandle raises an exception for a handle it cannot close instead of
 * returning FALSE, and the debugger consumes debug output before any vectored handler sees it.
 */
constexpr char k_debugger_skip[] =
    "a debugger turns a refused CloseHandle into an exception and consumes debug output";

static_assert(noexcept(close_handle_checked(nullptr, nullptr)),
              "handle owners call close_handle_checked from destructors");

TEST(CloseHandleChecked, ClosesTheHandleAndWritesNothing) {
    debugger_line_capture capture;
    const std::wstring name = unique_event_name();
    const HANDLE event = create_named_event(name);
    ASSERT_NE(event, nullptr);

    EXPECT_EQ(close_handle_checked(event, L"test event"), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_TRUE(event_destroyed(name));
    EXPECT_TRUE(capture.lines().empty());
}

TEST(CloseHandleChecked, StopSignalReportsAFailedCloseOfItsEvent) {
    if (IsDebuggerPresent()) {
        GTEST_SKIP() << k_debugger_skip;
    }
    debugger_line_capture capture;
    auto signal = std::make_unique<stop_signal>();
    const HANDLE event = signal->event_handle();
    ASSERT_TRUE(
        SetHandleInformation(event, HANDLE_FLAG_PROTECT_FROM_CLOSE, HANDLE_FLAG_PROTECT_FROM_CLOSE))
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";

    // The destructor's close is refused; it reports that once and gives the event up.
    signal.reset();
    EXPECT_EQ(capture.lines(),
              std::vector<std::wstring>{L"RAMQuick engine: CloseHandle of the session stop event "
                                        L"failed (Windows error 6)\n"});

    // The event is still open, so the test closes it; a second close would fail.
    ASSERT_TRUE(SetHandleInformation(event, HANDLE_FLAG_PROTECT_FROM_CLOSE, 0))
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(close_handle_checked(event, L"test event"), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_EQ(capture.lines().size(), 1U);
}

/** One description a failed close is reported with. */
struct close_failure_case {
    const char* name;
    /** The description passed to close_handle_checked; nullopt passes null. */
    std::optional<std::wstring> what;
    /** Whether the line is too long for the diagnostic and must be cut. */
    bool cut;
};

void PrintTo(const close_failure_case& value, std::ostream* os) {
    *os << value.name;
}

/**
 * Succeeds when lines holds exactly one line: full itself or, when cut, a shorter line that
 * begins as full does and still ends with its newline.
 */
::testing::AssertionResult reported_once(const std::vector<std::wstring>& lines,
                                         const std::wstring& full, bool cut) {
    if (lines.size() != 1U) {
        return ::testing::AssertionFailure() << lines.size() << " debugger lines, not 1";
    }
    const std::wstring& line = lines.front();
    const bool matches = cut ? !line.empty() && line.size() < full.size() && line.back() == L'\n' &&
                                   full.compare(0, line.size() - 1, line, 0, line.size() - 1) == 0
                             : line == full;
    if (!matches) {
        return ::testing::AssertionFailure() << "line \"" << line << "\" is not "
                                             << (cut ? "a cut of " : "") << "\"" << full << "\"";
    }
    return ::testing::AssertionSuccess();
}

class CloseHandleCheckedFailure : public ::testing::TestWithParam<close_failure_case> {};

TEST_P(CloseHandleCheckedFailure, ReportsOnceAndLeavesTheHandleOpen) {
    if (IsDebuggerPresent()) {
        GTEST_SKIP() << k_debugger_skip;
    }
    const close_failure_case& param = GetParam();
    const wchar_t* const what = param.what ? param.what->c_str() : nullptr;
    debugger_line_capture capture;
    const std::wstring name = unique_event_name();
    const HANDLE event = create_named_event(name);
    ASSERT_NE(event, nullptr);
    // A handle protected from close makes CloseHandle fail while the handle stays valid.
    ASSERT_TRUE(
        SetHandleInformation(event, HANDLE_FLAG_PROTECT_FROM_CLOSE, HANDLE_FLAG_PROTECT_FROM_CLOSE))
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";

    // Windows reports the refused close of a protected handle as ERROR_INVALID_HANDLE.
    EXPECT_EQ(close_handle_checked(event, what), static_cast<DWORD>(ERROR_INVALID_HANDLE));

    const std::wstring full = L"RAMQuick engine: CloseHandle of the " +
                              (what != nullptr ? std::wstring(what) : std::wstring(L"handle")) +
                              L" failed (Windows error 6)\n";
    EXPECT_TRUE(reported_once(capture.lines(), full, param.cut));

    // Neither forced closed nor stripped of its protection: the event is still open and usable.
    DWORD flags = 0;
    EXPECT_TRUE(GetHandleInformation(event, &flags))
        << "GetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(flags & HANDLE_FLAG_PROTECT_FROM_CLOSE,
              static_cast<DWORD>(HANDLE_FLAG_PROTECT_FROM_CLOSE));
    EXPECT_EQ(WaitForSingleObject(event, 0), static_cast<DWORD>(WAIT_TIMEOUT));

    // Once closable, one call closes it, the object goes away and nothing more is written.
    ASSERT_TRUE(SetHandleInformation(event, HANDLE_FLAG_PROTECT_FROM_CLOSE, 0))
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(close_handle_checked(event, what), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_TRUE(event_destroyed(name));
    EXPECT_EQ(capture.lines().size(), 1U);
}

/** Test-name suffix: the case's name, for example overlong. */
std::string failure_case_name(const ::testing::TestParamInfo<close_failure_case>& info) {
    return info.param.name;
}

const auto k_failure_cases =
    ::testing::Values(close_failure_case{"described", L"test event", false},
                      close_failure_case{"undescribed", std::nullopt, false},
                      close_failure_case{"overlong", std::wstring(400, L'w'), true});

INSTANTIATE_TEST_SUITE_P(Descriptions, CloseHandleCheckedFailure, k_failure_cases,
                         failure_case_name);

}  // namespace
}  // namespace rq::engine::test
