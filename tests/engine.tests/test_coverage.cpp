/**
 * @file test_coverage.cpp
 * @brief Coverage accounting: the verified bytes the engine reports, held against the
 *        independent count of counting_memory_access (success criterion 5: within 1 %).
 *
 * A verified byte is one whose 8-byte word a verification primitive (verify, verify_and_stream or
 * verify_strided) read and compared within the current step. Moving inversion verifies twice per
 * pass, re-reads never count, and data written but not yet verified when a stop arrives never
 * counts. The engine counts through coverage_counter; counting_memory_access sums the same
 * primitives on its own, word_count x 8 for verify and verify_and_stream and visit_count x 64 for
 * verify_strided, whose call compares only the lines it visits, not its whole span. Agreement
 * between the two is therefore evidence, not a count agreeing with itself.
 *
 * - CoverageMatrix: one step of every pattern in every access order, both moving-inversion
 *   patterns included, and a 5 s hold, in All and Half thread mode, on every instruction set the
 *   CPU offers (SKIPPED where it lacks one). The reported totals lie within 99-101 % of the
 *   independent count, and are in fact exact; every byte of every slice is verified once per pass
 *   (twice for moving inversion), whichever worker covers it. The runner ignores a
 *   moving-inversion step's order, so for each of the five the primitive log must show every
 *   worker writing the base ascending, then sweeping each group it covers once ascending and once
 *   descending.
 * - CoverageReread: mismatches injected before verification are each re-read once, and the
 *   re-reads change neither total.
 * - CoverageStop: a stop in mid-pass ends the step incomplete; what the cut pass verified is
 *   counted, and what it only wrote is not.
 * - CoveragePercent and CoverageCounter: coverage_percent and the counter's publication rules.
 * - CoverageSession: a whole session through session_create, with the run result, the step
 *   results and the last progress report held against the count.
 *
 * The step-level tests run the production step runner, worker pool, error collector and locked
 * memory region (32 MiB on up to four pinned workers) over counting -> real memory access, with a
 * fake clock that gives every pass 200 ms, so each step runs exactly three passes and a 5 s hold
 * completes at once. Nothing needs elevation: 4 KiB pages are locked after raising the minimum
 * working set, which the default policy allows every user.
 */
#include <gtest/gtest.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <memory>
#include <mutex>
#include <new>
#include <ostream>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_region.h"
#include "core/memory/real_memory_access.h"
#include "core/runner/coverage_counter.h"
#include "core/runner/step_runner.h"
#include "core/runner/worker_pool.h"
#include "core/errors/error_collector.h"
#include "core/session.h"
#include "fakes/fake_clock.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_ns_per_ms = 1'000'000U;

/** Workers of the step-level harness at most; fewer on a machine with fewer processors. */
constexpr std::uint32_t k_max_workers = 4U;

/** Region the step-level harness requests: four 8 MiB slices of one chunk each. */
constexpr std::uint64_t k_region_bytes = 32U * k_mib;

/** Fake duration the pass script gives every pass, and the passes each step's budget holds. */
constexpr std::uint64_t k_pass_ms = 200U;
constexpr std::uint64_t k_pass_ns = k_pass_ms * k_ns_per_ms;
constexpr std::uint32_t k_planned_passes = 3U;

/** Hold of the hold step: Q5's 5 s, which the fake clock completes at once. */
constexpr std::uint32_t k_hold_ms = 5'000U;
constexpr std::uint64_t k_hold_pass_ns = (k_pass_ms + k_hold_ms) * k_ns_per_ms;

constexpr std::uint64_t k_seed = 0x0C0FFEE5EED12345ULL;
constexpr std::uint32_t k_detail_cap = 10'000U;

/** Coverage floor of the harness steps, 100 %; it is reported only and never extends a step. */
constexpr std::uint32_t k_coverage_budget_bp = 10'000U;

/** Band of success criterion 5, in percent of the independent count. */
constexpr std::uint64_t k_band_low_percent = 99U;
constexpr std::uint64_t k_band_high_percent = 101U;

/** Bit the injected mismatches flip, and the most mismatches one run injects. */
constexpr std::uint64_t k_injected_bit = std::uint64_t{1} << 17U;
constexpr std::uint64_t k_max_injections = 32U;

/** Pass the mid-pass stop interrupts, and how far through that pass's verification it falls. */
constexpr std::uint32_t k_stop_pass = 1U;
constexpr std::uint64_t k_stop_at_percent = 40U;

/** slice_holding's result for an address outside every chunk of the region. */
constexpr std::size_t k_no_slice = static_cast<std::size_t>(-1);

/** Instruction sets of the matrix; each runs only where the CPU offers it. */
constexpr std::array<std::uint32_t, 3> k_isas = {RQ_ISA_SSE2, RQ_ISA_AVX2, RQ_ISA_AVX512};

static_assert(k_stop_pass < k_planned_passes, "the stop must fall inside the step's schedule");
static_assert(k_coverage_publish_bytes == k_mib, "workers publish coverage once per 1 MiB block");
static_assert(planned_passes(k_planned_passes * k_pass_ns, k_pass_ns) == k_planned_passes,
              "a budget of three scripted passes must schedule exactly three");
static_assert(planned_passes(k_planned_passes * k_hold_pass_ns, k_hold_pass_ns) == k_planned_passes,
              "a hold step's budget must schedule exactly three passes with their holds");
static_assert(coverage_percent(3U * k_region_bytes, k_region_bytes) == 300.0,
              "three full passes are 300 % coverage");

/* ---------------------------------------------------------------------------------------------
 * Labels: test names and failure context
 * ------------------------------------------------------------------------------------------- */

const char* isa_label(std::uint32_t isa) noexcept {
    switch (isa) {
    case RQ_ISA_SSE2:
        return "SSE2";
    case RQ_ISA_AVX2:
        return "AVX2";
    case RQ_ISA_AVX512:
        return "AVX512";
    default:
        return "UnknownIsa";
    }
}

const char* pattern_label(std::uint32_t pattern) noexcept {
    switch (pattern) {
    case RQ_PATTERN_CHECKERBOARD:
        return "Checkerboard";
    case RQ_PATTERN_MOVING_INVERSION_WALKING:
        return "MovingInversionWalking";
    case RQ_PATTERN_MOVING_INVERSION_RANDOM:
        return "MovingInversionRandom";
    case RQ_PATTERN_RANDOM:
        return "Random";
    case RQ_PATTERN_HIGH_TOGGLE:
        return "HighToggle";
    default:
        return "UnknownPattern";
    }
}

const char* order_label(std::uint32_t order) noexcept {
    switch (order) {
    case RQ_ORDER_SEQUENTIAL_ASCENDING:
        return "SequentialAscending";
    case RQ_ORDER_SEQUENTIAL_DESCENDING:
        return "SequentialDescending";
    case RQ_ORDER_BANK_STRIDE:
        return "BankStride";
    case RQ_ORDER_RANDOM_BLOCKS:
        return "RandomBlocks";
    case RQ_ORDER_TURNAROUND:
        return "Turnaround";
    default:
        return "UnknownOrder";
    }
}

const char* thread_label(std::uint32_t thread_mode) noexcept {
    return thread_mode == RQ_THREADS_HALF ? "Half" : "All";
}

/** One step shape: pattern, access order and hold. */
struct step_shape {
    std::uint32_t pattern = 0;
    std::uint32_t order = 0;
    std::uint32_t hold_ms = 0;
};

bool is_moving_inversion(std::uint32_t pattern) noexcept {
    return pattern == RQ_PATTERN_MOVING_INVERSION_WALKING ||
           pattern == RQ_PATTERN_MOVING_INVERSION_RANDOM;
}

/**
 * "Random_BankStride", "Random_RandomBlocks_Hold5s" or "MovingInversionRandom_Turnaround". The
 * order is named for moving inversion too, although the runner ignores it, because the matrix
 * runs each inversion pattern in every order and every case needs a distinct name.
 */
std::string shape_label(const step_shape& shape) {
    std::string label = std::string(pattern_label(shape.pattern)) + "_" + order_label(shape.order);
    if (shape.hold_ms > 0U) {
        label += "_Hold" + std::to_string(shape.hold_ms / 1'000U) + "s";
    }
    return label;
}

/** gtest's value printer for step_shape parameters, found by argument-dependent lookup. */
void PrintTo(const step_shape& shape, std::ostream* os) {
    *os << shape_label(shape);
}

std::string case_label(std::uint32_t isa, const step_shape& shape, std::uint32_t thread_mode) {
    return std::string(isa_label(isa)) + "_" + shape_label(shape) + "_" + thread_label(thread_mode);
}

/* ---------------------------------------------------------------------------------------------
 * Step shapes and definitions
 * ------------------------------------------------------------------------------------------- */

/** Every non-inverting pattern in every access order: windowed and turnaround steps. */
std::vector<step_shape> pattern_order_shapes() {
    std::vector<step_shape> shapes;
    for (const std::uint32_t pattern :
         {RQ_PATTERN_CHECKERBOARD, RQ_PATTERN_RANDOM, RQ_PATTERN_HIGH_TOGGLE}) {
        for (const std::uint32_t order :
             {RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_ORDER_SEQUENTIAL_DESCENDING, RQ_ORDER_BANK_STRIDE,
              RQ_ORDER_RANDOM_BLOCKS, RQ_ORDER_TURNAROUND}) {
            shapes.push_back(step_shape{pattern, order, 0U});
        }
    }
    return shapes;
}

/**
 * Both moving-inversion patterns in every access order. The runner must ignore the configured
 * order, because a moving-inversion pass always writes the base ascending and then sweeps
 * ascending and descending. Configuring each order, rather than one, is what lets CoverageMatrix
 * prove that every accepted value is ignored, not just one of them.
 */
std::vector<step_shape> moving_inversion_shapes() {
    std::vector<step_shape> shapes;
    for (const std::uint32_t pattern :
         {RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_PATTERN_MOVING_INVERSION_RANDOM}) {
        for (const std::uint32_t order :
             {RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_ORDER_SEQUENTIAL_DESCENDING, RQ_ORDER_BANK_STRIDE,
              RQ_ORDER_RANDOM_BLOCKS, RQ_ORDER_TURNAROUND}) {
            shapes.push_back(step_shape{pattern, order, 0U});
        }
    }
    return shapes;
}

/** The hold step as Q5 defines it: random data in RandomBlocks order, held 5 s. */
std::vector<step_shape> hold_shapes() {
    return {step_shape{RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, k_hold_ms}};
}

/** One shape of each step kind the runner distinguishes: windowed, turnaround, hold, inversion. */
std::vector<step_shape> step_kind_shapes() {
    return {step_shape{RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U},
            step_shape{RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, 0U},
            step_shape{RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, k_hold_ms},
            step_shape{RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U}};
}

rq_step_def step_definition(const std::string& name, std::uint32_t pattern, std::uint32_t order,
                            std::uint32_t thread_mode, std::uint32_t hold_ms,
                            std::uint32_t time_budget_ms, std::uint32_t coverage_budget_bp) {
    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), name.c_str());
    step.pattern = pattern;
    step.access_order = order;
    step.thread_mode = thread_mode;
    step.hold_ms = hold_ms;
    step.time_budget_ms = time_budget_ms;
    step.coverage_budget_bp = coverage_budget_bp;
    return step;
}

/**
 * A harness step: every pass lasts k_pass_ms of fake time plus its hold, so a budget of
 * k_planned_passes such passes schedules exactly that many (see the static_asserts above).
 */
rq_step_def harness_step(const step_shape& shape, std::uint32_t thread_mode) {
    const auto budget_ms =
        static_cast<std::uint32_t>(k_planned_passes * (k_pass_ms + shape.hold_ms));
    return step_definition(shape_label(shape), shape.pattern, shape.order, thread_mode,
                           shape.hold_ms, budget_ms, k_coverage_budget_bp);
}

/** Verifications of each byte per pass: two for moving inversion's two sweeps, else one. */
std::uint64_t verifications_per_pass(const rq_step_def& step) noexcept {
    return classify_step(step) == step_kind::moving_inversion ? 2U : 1U;
}

/* ---------------------------------------------------------------------------------------------
 * The acceptance band and the CPU's instruction sets
 * ------------------------------------------------------------------------------------------- */

/**
 * Success criterion 5: 0.99 x counted <= reported <= 1.01 x counted, in integer arithmetic. On
 * failure the message names the case, both totals and their ratio.
 */
::testing::AssertionResult within_band(const std::string& what, std::uint64_t reported,
                                       std::uint64_t counted) {
    const bool representable =
        counted <= UINT64_MAX / k_band_high_percent && reported <= UINT64_MAX / 100U;
    if (representable && reported * 100U >= counted * k_band_low_percent &&
        reported * 100U <= counted * k_band_high_percent) {
        return ::testing::AssertionSuccess();
    }
    ::testing::AssertionResult failure = ::testing::AssertionFailure();
    failure << what << ": reported " << reported << " bytes, counted " << counted << " bytes, ";
    if (counted == 0U) {
        failure << "ratio undefined";
    } else {
        failure << "ratio " << static_cast<double>(reported) / static_cast<double>(counted);
    }
    return failure << " (band " << k_band_low_percent << " % to " << k_band_high_percent
                   << " % of counted)";
}

std::uint32_t available_isa_mask() noexcept {
    return select_isa(read_cpu_features(), RQ_ISA_AUTO).available_mask;
}

bool isa_available(std::uint32_t isa) noexcept {
    return (available_isa_mask() & (1U << isa)) != 0U;
}

/** The set the application runs with: AVX-512, else AVX2, else SSE2. */
std::uint32_t best_isa() noexcept {
    return select_isa(read_cpu_features(), RQ_ISA_AUTO).best;
}

/* ---------------------------------------------------------------------------------------------
 * Step-level harness
 * ------------------------------------------------------------------------------------------- */

/**
 * The worker-side tallies of one slice and one worker index, on a cache line of their own, so
 * the per-primitive updates of different workers never share a line. Padded rather than
 * alignas(64), which MSVC reports as C4324 under /W4.
 */
struct lane_tally {
    /** Bytes verified inside the slice with this index, by whichever worker covered it. */
    std::atomic<std::uint64_t> slice_verified{0};
    /** Bytes verified by the worker with this index, in whichever slices it covered. */
    std::atomic<std::uint64_t> worker_verified{0};
    char padding[k_cache_line_bytes - 2U * sizeof(std::atomic<std::uint64_t>)];
};

/** A relaxed atomic count on a cache line of its own; see lane_tally for the padding. */
struct padded_counter {
    std::atomic<std::uint64_t> value{0};
    char padding[k_cache_line_bytes - sizeof(std::atomic<std::uint64_t>)];
};

static_assert(sizeof(lane_tally) == k_cache_line_bytes &&
                  sizeof(padded_counter) == k_cache_line_bytes,
              "every tally must fill exactly one cache line");

/**
 * Records the primitive log keeps per worker in a moving-inversion step. Each pass has three
 * whole-slice phases (base write and two sweeps); each issues one data call and one flush per
 * 1 MiB group it covers, plus two fences per covered slice, and a worker covers at most the whole
 * region in at most two slices. The cap holds that for every pass, where the layer's default of
 * 1 << 20 would make each logging worker reserve about 7.5 MiB up front.
 */
constexpr std::size_t k_inversion_log_records_per_worker = std::size_t{1} << 12U;

static_assert(std::uint64_t{k_planned_passes} * 3U *
                      (2U * (k_region_bytes / k_work_group_bytes) + 2U * 2U) <=
                  k_inversion_log_records_per_worker,
              "the inversion log must hold every pass of a worker that covers the whole region");

/** The step observer: keeps the last schedule run_step reported and how often it ticked. */
class status_recorder final : public step_observer {
public:
    void tick(const step_status& status) override {
        last_ = status;
        ++ticks_;
    }

    std::uint64_t ticks() const noexcept { return ticks_; }
    const step_status& last() const noexcept { return last_; }

private:
    step_status last_{};
    std::uint64_t ticks_ = 0;
};

/** What the harness's primitive observer does beyond scripting pass durations and tallying. */
enum class traffic_action : std::uint32_t {
    /** Nothing: a clean run. */
    none,
    /** Flips k_injected_bit in the first word of a range about to be verified. */
    inject_mismatches,
    /** Requests a stop once k_stop_at_percent of pass k_stop_pass's verification is done. */
    stop_mid_pass,
};

/**
 * One step of the production runner over a locked region, with a counting layer between the
 * runner and the real kernels of the instruction set under test.
 *
 * The counting layer's observer runs on the calling worker after every primitive. It advances
 * the fake clock by k_pass_ms on each pass's first primitive; tallies verified bytes per slice,
 * per worker and in pass k_stop_pass from each record's own address range, apart from both the
 * engine's coverage_counter and the layer's own totals; counts re-reads; and applies the
 * configured traffic_action.
 *
 * Non-copyable and non-movable: the workers and the observer refer to its members.
 */
class step_harness {
public:
    step_harness() = default;

    /** Joins the workers before the runner, the collector, the counters and the region go. */
    ~step_harness() {
        if (pool_) {
            // After a normal run_step no phase is outstanding. After an exception one can be, and
            // its tasks return within one 1 MiB block only once a stop has been requested.
            stop_.request(RQ_END_USER_STOP);
            pool_->shutdown();
        }
    }

    step_harness(const step_harness&) = delete;
    step_harness& operator=(const step_harness&) = delete;
    step_harness(step_harness&&) = delete;
    step_harness& operator=(step_harness&&) = delete;

    /** Locks the region, builds the layers and the collector, and starts the pinned workers. */
    ::testing::AssertionResult prepare(std::uint32_t isa,
                                       traffic_action action = traffic_action::none);

    /**
     * Runs step as step 0 of a one-step plan and returns what run_step reported. A
     * moving-inversion step runs with the counting layer's primitive log on, capped at
     * k_inversion_log_records_per_worker, so that its sweeps can be checked call by call
     * (inversion_sweeps_match); every other step kind leaves the log off.
     */
    step_outcome run(const rq_step_def& step);

    std::uint64_t tested_bytes() const noexcept { return region_.tested_bytes(); }
    std::uint32_t worker_count() const noexcept { return worker_count_; }
    const slice_view& slice(std::uint32_t index) const noexcept { return slices_[index]; }
    const counting_memory_access& memory() const noexcept { return *memory_; }
    const coverage_counter& coverage() const noexcept { return *coverage_; }
    error_collector& errors() noexcept { return *errors_; }
    const status_recorder& status() const noexcept { return status_; }
    std::uint32_t pending_stop_reason() const noexcept { return stop_.pending_reason(); }

    std::uint64_t slice_verified(std::uint32_t slice) const noexcept {
        return lanes_[slice].slice_verified.load(std::memory_order_relaxed);
    }
    std::uint64_t worker_verified(std::uint32_t worker) const noexcept {
        return lanes_[worker].worker_verified.load(std::memory_order_relaxed);
    }
    /** Verified bytes whose range lay in no chunk of the region, or came from no known worker. */
    std::uint64_t stray_verified() const noexcept {
        return stray_verified_.value.load(std::memory_order_relaxed);
    }
    std::uint64_t stop_pass_verified() const noexcept {
        return stop_pass_verified_.value.load(std::memory_order_relaxed);
    }
    /** Bytes stream_fill and verify_and_stream wrote in pass k_stop_pass. */
    std::uint64_t stop_pass_streamed() const noexcept {
        return stop_pass_streamed_.value.load(std::memory_order_relaxed);
    }
    std::uint64_t rereads() const noexcept {
        return rereads_.value.load(std::memory_order_relaxed);
    }
    std::uint64_t injected() const noexcept {
        return std::min(injection_attempts_.value.load(std::memory_order_relaxed),
                        k_max_injections);
    }
    /** True when a pass's scripted duration could not be applied (allocation failure). */
    bool script_failed() const noexcept { return script_failed_.load(std::memory_order_relaxed); }

private:
    void on_primitive(const primitive_record& record) noexcept;
    void begin_pass_once(const access_context& ctx) noexcept;
    void tally_verified(const primitive_record& record) noexcept;
    void tally_streamed(const primitive_record& record) noexcept;
    void inject_mismatch(const primitive_record& record) noexcept;
    std::size_t slice_holding(std::uintptr_t address) const noexcept;

    // Declaration order is construction order: each member is built before anything that holds
    // a reference to it, and the destructor joins the workers before any of them is destroyed.
    stop_signal stop_;
    fake_clock clock_;
    pass_duration_script script_{clock_, {k_pass_ns}};
    memory_region region_;
    std::vector<logical_processor> placements_;
    std::vector<slice_view> slices_;
    std::unique_ptr<counting_memory_access> memory_;
    std::unique_ptr<error_collector> errors_;
    std::unique_ptr<coverage_counter> coverage_;
    std::shared_ptr<IThreadAffinity> affinity_;
    std::unique_ptr<worker_pool> pool_;
    std::unique_ptr<step_runner> runner_;
    status_recorder status_;

    // Written by the test thread before post_phase publishes them to the workers.
    std::uint32_t worker_count_ = 0;
    traffic_action action_ = traffic_action::none;
    std::uint64_t stop_threshold_bytes_ = 0;

    // Updated by the workers' observer calls.
    std::atomic<std::uint64_t> pass_key_seen_{0};
    std::array<lane_tally, k_max_workers> lanes_{};
    padded_counter stray_verified_{};
    padded_counter stop_pass_verified_{};
    padded_counter stop_pass_streamed_{};
    padded_counter rereads_{};
    padded_counter injection_attempts_{};
    std::atomic<bool> script_failed_{false};
};

::testing::AssertionResult step_harness::prepare(std::uint32_t isa, traffic_action action) {
    action_ = action;
    const isa_selection selection = select_isa(read_cpu_features(), isa);
    if (selection.override_rejected || selection.selected != isa) {
        return ::testing::AssertionFailure() << isa_label(isa) << " cannot be selected on this CPU";
    }

    const std::vector<logical_processor> processors = enumerate_logical_processors();
    worker_count_ =
        static_cast<std::uint32_t>(std::min<std::size_t>(k_max_workers, processors.size()));
    placements_.assign(processors.begin(), processors.begin() + worker_count_);

    region_options request{};
    request.region_bytes = k_region_bytes;
    // The smallest slice per worker, so a split that does not divide 32 MiB evenly is accepted.
    request.min_region_bytes = std::uint64_t{worker_count_} * k_slice_granularity_bytes;
    request.worker_count = worker_count_;
    // 4 KiB pages locked with VirtualLock on every machine, so the layout never depends on
    // whether the token happens to hold SeLockMemoryPrivilege.
    request.lock_privilege_held = false;
    const allocation_outcome allocated = region_.allocate(request, stop_);
    if (allocated.status != allocation_status::ok) {
        return ::testing::AssertionFailure() << "memory_region::allocate failed (Windows error "
                                             << allocated.os_error << "): " << allocated.end_detail;
    }
    slices_.reserve(worker_count_);
    for (std::uint32_t worker = 0; worker < worker_count_; ++worker) {
        slices_.push_back(region_.slice(worker));
    }

    memory_ = std::make_unique<counting_memory_access>(
        std::make_unique<real_memory_access>(tables_for(selection)));
    const auto* real = dynamic_cast<const real_memory_access*>(memory_->inner());
    if (real == nullptr || real->kernels().isa != isa) {
        return ::testing::AssertionFailure()
               << "the real layer does not run the " << isa_label(isa) << " kernels";
    }
    memory_->set_observer([this](const primitive_record& record) { on_primitive(record); });

    error_collector_options options{};
    options.detail_cap = k_detail_cap;
    options.stop_on_error = false;
    options.step_count = 1U;
    options.placements = placements_;
    errors_ = std::make_unique<error_collector>(options, stop_, clock_, *memory_);
    coverage_ = std::make_unique<coverage_counter>(worker_count_);

    affinity_ = make_win32_thread_affinity();
    pool_ = std::make_unique<worker_pool>();
    const pool_start_result started = pool_->start(placements_, worker_count_, *affinity_, stop_);
    if (started.outcome != pool_start_result::status::ok) {
        return ::testing::AssertionFailure()
               << "worker_pool::start failed for worker " << started.failed_worker
               << " (Windows error " << started.os_error << ")";
    }
    runner_ = std::make_unique<step_runner>(step_runner_dependencies{
        .slices = slices_,
        .memory = *memory_,
        .pool = *pool_,
        .errors = *errors_,
        .coverage = *coverage_,
        .clock = clock_,
        .stop = stop_,
        .seed = k_seed,
    });
    errors_->begin_run(clock_.now_ns());
    return ::testing::AssertionSuccess();
}

step_outcome step_harness::run(const rq_step_def& step) {
    stop_threshold_bytes_ =
        tested_bytes() * verifications_per_pass(step) * k_stop_at_percent / 100U;
    if (is_moving_inversion(step.pattern)) {
        // Enabled while the workers are idle, as the layer requires, and only here: the sweep
        // order is what this log proves, and the other kinds' checks need no record of calls.
        memory_->enable_log(k_inversion_log_records_per_worker);
    }
    return runner_->run_step(step, 0U, status_);
}

void step_harness::on_primitive(const primitive_record& record) noexcept {
    begin_pass_once(record.ctx);
    switch (record.kind) {
    case primitive_kind::verify:
    case primitive_kind::verify_strided:
        // A strided record's bytes are the lines it visits, the bytes it verifies.
        tally_verified(record);
        break;
    case primitive_kind::verify_and_stream:
        // A moving-inversion sweep verifies the range and streams its next value in one call.
        tally_verified(record);
        tally_streamed(record);
        break;
    case primitive_kind::stream_fill:
    case primitive_kind::stream_fill_strided:
        tally_streamed(record);
        break;
    case primitive_kind::flush:
        if (action_ == traffic_action::inject_mismatches) {
            inject_mismatch(record);
        }
        break;
    case primitive_kind::reread_word:
        rereads_.value.fetch_add(1U, std::memory_order_relaxed);
        break;
    case primitive_kind::fence:
        break;
    }
}

void step_harness::begin_pass_once(const access_context& ctx) noexcept {
    // Keys only grow within a run (+1 keeps 0 for "none seen"), so the first primitive of each
    // pass wins the exchange and every later one skips the script's lock.
    const std::uint64_t key = ((std::uint64_t{ctx.step_index} << 32U) | ctx.pass_index) + 1U;
    std::uint64_t seen = pass_key_seen_.load(std::memory_order_acquire);
    while (seen < key) {
        if (pass_key_seen_.compare_exchange_weak(seen, key, std::memory_order_acq_rel,
                                                 std::memory_order_acquire)) {
            try {
                script_.on_pass_begin(ctx.step_index, ctx.pass_index);
            } catch (const std::bad_alloc&) {
                // The observer must not throw; the test fails through script_failed() instead.
                script_failed_.store(true, std::memory_order_relaxed);
            }
            return;
        }
    }
}

void step_harness::tally_verified(const primitive_record& record) noexcept {
    const std::size_t slice = slice_holding(record.begin);
    if (slice != k_no_slice && slice < lanes_.size() && record.worker < lanes_.size()) {
        lanes_[slice].slice_verified.fetch_add(record.bytes, std::memory_order_relaxed);
        lanes_[record.worker].worker_verified.fetch_add(record.bytes, std::memory_order_relaxed);
    } else {
        stray_verified_.value.fetch_add(record.bytes, std::memory_order_relaxed);
    }
    if (record.ctx.pass_index != k_stop_pass) {
        return;
    }
    const std::uint64_t before =
        stop_pass_verified_.value.fetch_add(record.bytes, std::memory_order_relaxed);
    if (action_ == traffic_action::stop_mid_pass && before < stop_threshold_bytes_ &&
        before + record.bytes >= stop_threshold_bytes_) {
        stop_.request(RQ_END_USER_STOP, 0U, "test_coverage: mid-pass stop");
    }
}

void step_harness::tally_streamed(const primitive_record& record) noexcept {
    if (record.ctx.pass_index == k_stop_pass) {
        stop_pass_streamed_.value.fetch_add(record.bytes, std::memory_order_relaxed);
    }
}

void step_harness::inject_mismatch(const primitive_record& record) noexcept {
    // Only flushes whose range is verified later in the same phase: a windowed or hold
    // verification (verify), a turnaround block (turnaround) and the moving-inversion base
    // write's closing flush (write), which the ascending sweep verifies. The flush after a sweep
    // is skipped, because the next pass's base write may overwrite its range unverified.
    const phase_kind phase = record.ctx.phase;
    if (phase != phase_kind::verify && phase != phase_kind::turnaround &&
        phase != phase_kind::write) {
        return;
    }
    if (record.bytes < sizeof(std::uint64_t) ||
        injection_attempts_.value.fetch_add(1U, std::memory_order_relaxed) >= k_max_injections) {
        return;
    }
    // Every such flush follows a fence issued after the range's streaming stores, so this plain
    // store cannot be overtaken by a write-combining buffer, and the verification load the
    // runner issues after its next fence, on this same thread, observes it whether or not the
    // flush has completed.
    volatile std::uint64_t* const word = reinterpret_cast<volatile std::uint64_t*>(record.begin);
    const std::uint64_t value = *word;
    *word = value ^ k_injected_bit;
}

std::size_t step_harness::slice_holding(std::uintptr_t address) const noexcept {
    for (std::size_t s = 0; s < slices_.size(); ++s) {
        for (const chunk_view& chunk : slices_[s].chunks) {
            const auto base = reinterpret_cast<std::uintptr_t>(chunk.base);
            if (address >= base && address - base < chunk.bytes) {
                return s;
            }
        }
    }
    return k_no_slice;
}

/** Slice indices [first, last) that one worker covers in a pass. */
struct covered_range {
    std::uint32_t first = 0;
    std::uint32_t last = 0;
};

/** The slices a worker covers in one pass: its own, and its partner's in Half mode. */
covered_range covered_slices(const step_harness& harness, std::uint32_t worker,
                             std::uint32_t thread_mode) noexcept {
    if (thread_mode != RQ_THREADS_HALF) {
        return covered_range{worker, worker + 1U};
    }
    if (worker % 2U != 0U) {
        // The idle partner: its even neighbour covers its slice.
        return covered_range{worker, worker};
    }
    return covered_range{worker, std::min(worker + 2U, harness.worker_count())};
}

/** Bytes of the slices a worker covers in one pass. */
std::uint64_t covered_bytes(const step_harness& harness, std::uint32_t worker,
                            std::uint32_t thread_mode) noexcept {
    const covered_range covered = covered_slices(harness, worker, thread_mode);
    std::uint64_t bytes = 0;
    for (std::uint32_t s = covered.first; s < covered.last; ++s) {
        bytes += harness.slice(s).bytes;
    }
    return bytes;
}

const char* primitive_label(primitive_kind kind) noexcept {
    switch (kind) {
    case primitive_kind::stream_fill:
        return "stream_fill";
    case primitive_kind::fence:
        return "fence";
    case primitive_kind::flush:
        return "flush";
    case primitive_kind::verify:
        return "verify";
    case primitive_kind::verify_and_stream:
        return "verify_and_stream";
    case primitive_kind::reread_word:
        return "reread_word";
    case primitive_kind::stream_fill_strided:
        return "stream_fill_strided";
    case primitive_kind::verify_strided:
        return "verify_strided";
    }
    return "unknown primitive";
}

/** "slice 1 + 3 MiB": where an address lies in the region, for failure messages. */
std::string region_label(const step_harness& harness, std::uintptr_t address) {
    for (std::uint32_t s = 0; s < harness.worker_count(); ++s) {
        const slice_view& slice = harness.slice(s);
        for (const chunk_view& chunk : slice.chunks) {
            const auto base = reinterpret_cast<std::uintptr_t>(chunk.base);
            if (address >= base && address - base < chunk.bytes) {
                const std::uint64_t offset =
                    chunk.logical_offset - slice.logical_offset + (address - base);
                return "slice " + std::to_string(s) + " + " +
                       (offset % k_mib == 0U ? std::to_string(offset / k_mib) + " MiB"
                                             : std::to_string(offset) + " B");
            }
        }
    }
    return "an address outside the region";
}

/** One data call of a moving-inversion pass: its primitive and the 1 MiB group it starts at. */
struct inversion_call {
    primitive_kind kind{};
    std::uintptr_t begin = 0;

    friend bool operator==(const inversion_call&, const inversion_call&) = default;
};

/**
 * The data calls one moving-inversion pass must issue on a worker, derived from the slices it
 * covers and their chunks alone, never from the step's configured order, which the runner
 * ignores. The base write streams every covered 1 MiB group ascending; the first sweep then
 * verifies and streams each group once ascending, and the second once descending. As
 * step_runner.h defines a whole-slice sweep, every phase takes the covered slices in index order,
 * and the descending sweep reverses the chunks and groups within each slice.
 */
std::vector<inversion_call> expected_inversion_pass(const step_harness& harness,
                                                    std::uint32_t worker,
                                                    std::uint32_t thread_mode) {
    std::vector<std::uintptr_t> ascending;
    std::vector<std::uintptr_t> descending;
    const covered_range covered = covered_slices(harness, worker, thread_mode);
    for (std::uint32_t s = covered.first; s < covered.last; ++s) {
        std::vector<std::uintptr_t> groups;
        for (const chunk_view& chunk : harness.slice(s).chunks) {
            const auto base = reinterpret_cast<std::uintptr_t>(chunk.base);
            for (std::uint64_t offset = 0; offset < chunk.bytes; offset += k_work_group_bytes) {
                groups.push_back(base + offset);
            }
        }
        ascending.insert(ascending.end(), groups.begin(), groups.end());
        descending.insert(descending.end(), groups.rbegin(), groups.rend());
    }
    std::vector<inversion_call> calls;
    calls.reserve(3U * ascending.size());
    for (const std::uintptr_t group : ascending) {
        calls.push_back(inversion_call{primitive_kind::stream_fill, group});
    }
    for (const std::uintptr_t group : ascending) {
        calls.push_back(inversion_call{primitive_kind::verify_and_stream, group});
    }
    for (const std::uintptr_t group : descending) {
        calls.push_back(inversion_call{primitive_kind::verify_and_stream, group});
    }
    return calls;
}

/**
 * Holds one worker's logged moving-inversion traffic to expected_inversion_pass, pass by pass
 * and call by call. Fences and flushes are passed over, because test_step_runner.cpp checks
 * their placement. Every other call must be a base-write stream_fill (phase_kind::write) or a
 * sweep verify_and_stream (phase_kind::sweep, RQ_ACCESS_READ_MODIFY_WRITE) of one whole 1 MiB
 * group, in step 0 and in one of the passes the step completed.
 */
::testing::AssertionResult inversion_sweeps_match(const step_harness& harness, std::uint32_t worker,
                                                  std::uint32_t thread_mode, std::uint32_t passes) {
    const std::vector<inversion_call> expected =
        expected_inversion_pass(harness, worker, thread_mode);
    const std::size_t groups = expected.size() / 3U;
    std::vector<std::vector<inversion_call>> actual(passes);
    for (const primitive_record& record : harness.memory().log_for_worker(worker)) {
        if (record.kind == primitive_kind::fence || record.kind == primitive_kind::flush) {
            continue;
        }
        const access_context& ctx = record.ctx;
        const bool base_write =
            record.kind == primitive_kind::stream_fill && ctx.phase == phase_kind::write;
        const bool sweep = record.kind == primitive_kind::verify_and_stream &&
                           ctx.phase == phase_kind::sweep &&
                           ctx.access_type == RQ_ACCESS_READ_MODIFY_WRITE;
        if (!(base_write || sweep) || record.bytes != k_work_group_bytes || ctx.step_index != 0U ||
            ctx.pass_index >= passes) {
            return ::testing::AssertionFailure()
                   << "worker " << worker << " issued an unexpected "
                   << primitive_label(record.kind) << " (phase "
                   << static_cast<std::uint32_t>(ctx.phase) << ", access type " << ctx.access_type
                   << ", step " << ctx.step_index << ", pass " << ctx.pass_index << ", "
                   << record.bytes << " bytes) at " << region_label(harness, record.begin);
        }
        actual[ctx.pass_index].push_back(inversion_call{record.kind, record.begin});
    }
    for (std::uint32_t pass = 0; pass < passes; ++pass) {
        const std::vector<inversion_call>& calls = actual[pass];
        const std::size_t common = std::min(calls.size(), expected.size());
        for (std::size_t i = 0; i < common; ++i) {
            if (calls[i] == expected[i]) {
                continue;
            }
            const char* part = i < groups        ? "base write"
                               : i < 2U * groups ? "ascending sweep"
                                                 : "descending sweep";
            return ::testing::AssertionFailure()
                   << "worker " << worker << " pass " << pass << ": data call " << i << " (" << part
                   << ") is " << primitive_label(calls[i].kind) << " at "
                   << region_label(harness, calls[i].begin) << ", expected "
                   << primitive_label(expected[i].kind) << " at "
                   << region_label(harness, expected[i].begin);
        }
        if (calls.size() != expected.size()) {
            return ::testing::AssertionFailure()
                   << "worker " << worker << " pass " << pass << ": " << calls.size()
                   << " data calls, expected " << expected.size() << " (" << groups
                   << " covered groups, each in the base write and both sweeps)";
        }
    }
    return ::testing::AssertionSuccess();
}

/* ---------------------------------------------------------------------------------------------
 * CoverageMatrix: every pattern x order x instruction set x thread mode
 * ------------------------------------------------------------------------------------------- */

using matrix_param = std::tuple<std::uint32_t, step_shape, std::uint32_t>;

class CoverageMatrix : public ::testing::TestWithParam<matrix_param> {};

TEST_P(CoverageMatrix, ReportedTotalsMatchIndependentCount) {
    const auto& [isa, shape, thread_mode] = GetParam();
    if (!isa_available(isa)) {
        GTEST_SKIP() << "CPU lacks " << isa_label(isa);
    }
    const std::string label = case_label(isa, shape, thread_mode);
    step_harness harness;
    ASSERT_TRUE(harness.prepare(isa)) << label;

    const rq_step_def step = harness_step(shape, thread_mode);
    const step_outcome outcome = harness.run(step);
    ASSERT_FALSE(harness.script_failed()) << label;
    EXPECT_TRUE(outcome.completed) << label;
    ASSERT_EQ(outcome.passes, k_planned_passes) << label;
    EXPECT_EQ(harness.status().last().passes_done, outcome.passes) << label;

    const std::uint64_t tested = harness.tested_bytes();
    const std::uint64_t per_pass = tested * verifications_per_pass(step);
    const std::uint64_t counted = harness.memory().verified_bytes();
    const std::uint64_t reported = harness.coverage().session_total();

    // Success criterion 5 on every total the engine reports from coverage_counter.
    EXPECT_TRUE(within_band(label + " session_total", reported, counted));
    EXPECT_TRUE(within_band(label + " step_total", harness.coverage().step_total(), counted));
    EXPECT_TRUE(within_band(label + " step_outcome", outcome.verified_bytes, counted));
    // Read after the last phase barrier, the counter's totals are exact.
    EXPECT_EQ(reported, counted) << label;
    EXPECT_EQ(harness.coverage().step_total(), counted) << label;
    EXPECT_EQ(outcome.verified_bytes, counted) << label;
    EXPECT_EQ(harness.memory().verified_bytes_for_step(0U), counted) << label;

    // Every byte under test is verified once per pass, twice for moving inversion, and the
    // percentage follows: 300 % for three passes, 600 % for three inversion passes.
    EXPECT_EQ(counted, std::uint64_t{outcome.passes} * per_pass) << label;
    EXPECT_DOUBLE_EQ(coverage_percent(reported, tested),
                     100.0 * outcome.passes * static_cast<double>(verifications_per_pass(step)))
        << label;

    // Slice by slice and worker by worker, from the verified ranges' own addresses: in Half mode
    // the even workers cover their partners' slices, so the region is verified exactly as in All.
    EXPECT_EQ(harness.stray_verified(), 0U) << label;
    for (std::uint32_t s = 0; s < harness.worker_count(); ++s) {
        EXPECT_EQ(harness.slice_verified(s), std::uint64_t{outcome.passes} *
                                                 harness.slice(s).bytes *
                                                 verifications_per_pass(step))
            << label << " slice " << s;
    }
    for (std::uint32_t w = 0; w < harness.worker_count(); ++w) {
        EXPECT_EQ(harness.worker_verified(w), std::uint64_t{outcome.passes} *
                                                  covered_bytes(harness, w, thread_mode) *
                                                  verifications_per_pass(step))
            << label << " worker " << w;
    }

    // A clean run: nothing to confirm, so nothing re-read.
    EXPECT_EQ(harness.errors().true_total(), 0U) << label;
    EXPECT_EQ(harness.rereads(), 0U) << label;

    // Moving inversion ignores the configured order: whichever of the five the step names, each
    // worker writes the base ascending and then sweeps every group it covers once ascending and
    // once descending, in every pass.
    if (is_moving_inversion(shape.pattern)) {
        ASSERT_FALSE(harness.memory().log_overflowed()) << label << ": the primitive log is cut";
        for (std::uint32_t w = 0; w < harness.worker_count(); ++w) {
            EXPECT_TRUE(inversion_sweeps_match(harness, w, thread_mode, outcome.passes)) << label;
        }
    }
}

std::string matrix_case_name(const ::testing::TestParamInfo<matrix_param>& info) {
    const auto& [isa, shape, thread_mode] = info.param;
    return case_label(isa, shape, thread_mode);
}

// The prefixes start with "Coverage", so --gtest_filter=Coverage* selects every instantiation.
INSTANTIATE_TEST_SUITE_P(CoveragePatternOrder, CoverageMatrix,
                         ::testing::Combine(::testing::ValuesIn(k_isas),
                                            ::testing::ValuesIn(pattern_order_shapes()),
                                            ::testing::Values(RQ_THREADS_ALL, RQ_THREADS_HALF)),
                         matrix_case_name);
INSTANTIATE_TEST_SUITE_P(CoverageMovingInversion, CoverageMatrix,
                         ::testing::Combine(::testing::ValuesIn(k_isas),
                                            ::testing::ValuesIn(moving_inversion_shapes()),
                                            ::testing::Values(RQ_THREADS_ALL, RQ_THREADS_HALF)),
                         matrix_case_name);
INSTANTIATE_TEST_SUITE_P(CoverageHold, CoverageMatrix,
                         ::testing::Combine(::testing::ValuesIn(k_isas),
                                            ::testing::ValuesIn(hold_shapes()),
                                            ::testing::Values(RQ_THREADS_ALL, RQ_THREADS_HALF)),
                         matrix_case_name);

std::string shape_case_name(const ::testing::TestParamInfo<step_shape>& info) {
    return shape_label(info.param);
}

/* ---------------------------------------------------------------------------------------------
 * CoverageReread: re-reads of confirmed mismatches never count
 * ------------------------------------------------------------------------------------------- */

class CoverageReread : public ::testing::TestWithParam<step_shape> {};

TEST_P(CoverageReread, RereadsOfInjectedMismatchesAreNotCounted) {
    const step_shape& shape = GetParam();
    const std::string label = shape_label(shape);
    step_harness harness;
    ASSERT_TRUE(harness.prepare(best_isa(), traffic_action::inject_mismatches)) << label;

    const rq_step_def step = harness_step(shape, RQ_THREADS_ALL);
    const step_outcome outcome = harness.run(step);
    ASSERT_FALSE(harness.script_failed()) << label;
    EXPECT_TRUE(outcome.completed) << label;
    ASSERT_EQ(outcome.passes, k_planned_passes) << label;

    // Each injected word fails its verification once and is re-read once.
    const std::uint64_t injected = harness.injected();
    ASSERT_GT(injected, 0U) << label << ": no mismatch was injected";
    EXPECT_EQ(harness.errors().true_total(), injected) << label;
    EXPECT_EQ(harness.rereads(), injected) << label;
    const error_batch batch = harness.errors().drain();
    ASSERT_EQ(batch.count, injected) << label;
    for (std::uint32_t i = 0; i < batch.count; ++i) {
        // The flipped value is stored, so the flushed re-read repeats it.
        EXPECT_EQ(batch.records[i].reread_outcome, RQ_REREAD_REPEATS_ACTUAL)
            << label << " record " << i;
        EXPECT_EQ(batch.records[i].bit_mask, k_injected_bit) << label << " record " << i;
    }

    // The totals of a clean run: the comparisons count, the re-reads do not.
    const std::uint64_t counted = harness.memory().verified_bytes();
    EXPECT_EQ(counted,
              std::uint64_t{outcome.passes} * harness.tested_bytes() * verifications_per_pass(step))
        << label;
    EXPECT_TRUE(within_band(label + " session_total", harness.coverage().session_total(), counted));
    EXPECT_TRUE(within_band(label + " step_outcome", outcome.verified_bytes, counted));
    EXPECT_EQ(harness.coverage().session_total(), counted) << label;
    EXPECT_EQ(outcome.verified_bytes, counted) << label;
}

INSTANTIATE_TEST_SUITE_P(CoverageStepKinds, CoverageReread, ::testing::ValuesIn(step_kind_shapes()),
                         shape_case_name);

/* ---------------------------------------------------------------------------------------------
 * CoverageStop: a stop in mid-pass counts what was verified, never what was only written
 * ------------------------------------------------------------------------------------------- */

class CoverageStop : public ::testing::TestWithParam<step_shape> {};

TEST_P(CoverageStop, MidPassStopCountsOnlyVerifiedBytes) {
    const step_shape& shape = GetParam();
    const std::string label = shape_label(shape);
    step_harness harness;
    ASSERT_TRUE(harness.prepare(best_isa(), traffic_action::stop_mid_pass)) << label;

    const rq_step_def step = harness_step(shape, RQ_THREADS_ALL);
    const step_outcome outcome = harness.run(step);
    ASSERT_FALSE(harness.script_failed()) << label;
    EXPECT_EQ(harness.pending_stop_reason(), RQ_END_USER_STOP) << label;
    EXPECT_FALSE(outcome.completed) << label;
    // The cut pass is not counted as a pass.
    ASSERT_EQ(outcome.passes, k_stop_pass) << label;

    const std::uint64_t per_pass = harness.tested_bytes() * verifications_per_pass(step);
    const std::uint64_t verified_in_cut_pass = harness.stop_pass_verified();
    const std::uint64_t streamed_in_cut_pass = harness.stop_pass_streamed();
    EXPECT_GE(verified_in_cut_pass, per_pass * k_stop_at_percent / 100U) << label;
    EXPECT_LT(verified_in_cut_pass, per_pass) << label << ": the stop did not cut the pass short";

    // What the cut pass verified is counted, by both counters alike.
    const std::uint64_t counted = harness.memory().verified_bytes();
    EXPECT_EQ(counted, std::uint64_t{k_stop_pass} * per_pass + verified_in_cut_pass) << label;
    EXPECT_TRUE(within_band(label + " session_total", harness.coverage().session_total(), counted));
    EXPECT_TRUE(within_band(label + " step_total", harness.coverage().step_total(), counted));
    EXPECT_TRUE(within_band(label + " step_outcome", outcome.verified_bytes, counted));
    EXPECT_EQ(harness.coverage().session_total(), counted) << label;
    EXPECT_EQ(outcome.verified_bytes, counted) << label;

    // The cut pass wrote more than it verified, and neither total includes that excess.
    EXPECT_GT(streamed_in_cut_pass, verified_in_cut_pass) << label;
    EXPECT_LT(harness.coverage().session_total(),
              std::uint64_t{k_stop_pass} * per_pass + streamed_in_cut_pass)
        << label;
}

INSTANTIATE_TEST_SUITE_P(CoverageStepKinds, CoverageStop, ::testing::ValuesIn(step_kind_shapes()),
                         shape_case_name);

/* ---------------------------------------------------------------------------------------------
 * CoveragePercent and CoverageCounter: the formula and the publication rules
 * ------------------------------------------------------------------------------------------- */

struct percent_case {
    std::uint64_t verified_bytes = 0;
    std::uint64_t tested_bytes = 0;
    double expected_percent = 0.0;
};

constexpr std::uint64_t k_30_gib = 30U * 1024U * k_mib;

constexpr std::array<percent_case, 10> k_percent_cases = {{
    // Nothing verified yet.
    {0U, k_region_bytes, 0.0},
    // Half the region once.
    {k_region_bytes / 2U, k_region_bytes, 50.0},
    // Every byte once is 100 %; every byte ten times is 1000 %.
    {k_region_bytes, k_region_bytes, 100.0},
    {10U * k_region_bytes, k_region_bytes, 1000.0},
    // The Quick, Standard and Exhaustive targets on a 30 GiB region.
    {2U * k_30_gib, k_30_gib, 200.0},
    {8U * k_30_gib, k_30_gib, 800.0},
    {25U * k_30_gib, k_30_gib, 2500.0},
    // A region shrunk to a size that is no power of two.
    {3U * 996U * k_mib, 996U * k_mib, 300.0},
    // No bytes under test: 0 rather than a division by zero.
    {0U, 0U, 0.0},
    {k_region_bytes, 0U, 0.0},
}};

void PrintTo(const percent_case& c, std::ostream* os) {
    *os << c.verified_bytes << " of " << c.tested_bytes << " bytes";
}

class CoveragePercent : public ::testing::TestWithParam<percent_case> {};

TEST_P(CoveragePercent, IsHundredTimesVerifiedOverTested) {
    const percent_case& c = GetParam();
    EXPECT_DOUBLE_EQ(coverage_percent(c.verified_bytes, c.tested_bytes), c.expected_percent);
}

INSTANTIATE_TEST_SUITE_P(CoverageFormula, CoveragePercent, ::testing::ValuesIn(k_percent_cases));

TEST(CoverageCounter, PublishesWholeBlocksAndPendingBytesOnPublish) {
    coverage_counter counter(2U);
    EXPECT_EQ(counter.session_total(), 0U);
    EXPECT_EQ(counter.step_total(), 0U);

    // Below one block a worker's bytes stay pending; the block that completes it publishes all.
    counter.add(0U, k_coverage_publish_bytes - 8U);
    EXPECT_EQ(counter.session_total(), 0U);
    counter.add(0U, 8U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes);
    EXPECT_EQ(counter.step_total(), k_coverage_publish_bytes);

    // Each worker has its own pending count, published by its own publish(); a second publish
    // with nothing pending changes nothing.
    counter.add(1U, 4'096U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes);
    counter.publish(1U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes + 4'096U);
    counter.publish(1U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes + 4'096U);
    EXPECT_EQ(counter.step_total(), k_coverage_publish_bytes + 4'096U);

    // A new step restarts the step total; the session total runs on across steps.
    counter.begin_step();
    EXPECT_EQ(counter.step_total(), 0U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes + 4'096U);
    counter.add(0U, 64U);
    counter.publish(0U);
    EXPECT_EQ(counter.step_total(), 64U);
    EXPECT_EQ(counter.session_total(), k_coverage_publish_bytes + 4'096U + 64U);
}

/* ---------------------------------------------------------------------------------------------
 * CoverageSession: a whole session against the independent count
 * ------------------------------------------------------------------------------------------- */

/**
 * Workers the session prefers, so its region is covered by more than one pinned worker, and the
 * most it requests. session_create accepts at most one worker per active logical processor, so a
 * supported host with a single processor runs the session on one worker: this coverage check
 * runs on every host rather than skipping.
 */
constexpr std::uint32_t k_session_max_workers = 2U;
constexpr std::uint64_t k_session_region_bytes = 128U * k_mib;

/**
 * Bytes a session over workers (at least 1) tests, derived here rather than by the engine's
 * plan_region: each worker's slice is its share of the region rounded down to the 2 MiB slice
 * granularity.
 */
constexpr std::uint64_t session_tested_bytes(std::uint32_t workers) noexcept {
    const std::uint64_t share = k_session_region_bytes / workers;
    return std::uint64_t{workers} * (share - share % k_slice_granularity_bytes);
}

// Slices only shrink as workers are added, so the preferred count passing the engine's
// smallest-slice rule means every count down to one passes it too.
static_assert(session_tested_bytes(k_session_max_workers) / k_session_max_workers >=
                  k_min_slice_bytes,
              "every session worker count must give each worker a slice the engine accepts");

constexpr std::uint32_t k_session_step_ms = 1'000U;
constexpr std::uint32_t k_session_step_count = 3U;
// Every tick may report progress, so the last report follows the last pass closely.
constexpr std::uint32_t k_session_progress_interval_ms = 1U;
constexpr std::chrono::seconds k_session_timeout{120};

/** What the callbacks of one session saw, guarded by mutex; they run on the coordinator. */
struct session_events {
    std::mutex mutex;
    std::condition_variable released_changed;
    bool released = false;
    bool completed = false;
    std::uint32_t prepared_workers = 0;
    std::uint64_t tested_bytes = 0;
    std::uint32_t steps_started = 0;
    std::uint32_t steps_finished = 0;
    std::array<rq_step_result, k_session_step_count> finished{};
    std::uint64_t progress_reports = 0;
    std::uint64_t last_progress_bytes = 0;
    std::uint64_t progress_region_bytes = 0;
    bool progress_went_back = false;
    std::uint64_t error_total = 0;
};

session_events& events_of(void* user_data) noexcept {
    return *static_cast<session_events*>(user_data);
}

void RQ_CALL record_prepared(void* user_data, const rq_prepared* info) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    events.prepared_workers = info->worker_count;
    events.tested_bytes = info->tested_bytes;
}

std::uint32_t RQ_CALL continue_step(void* user_data, const rq_step_start*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    ++events.steps_started;
    return RQ_ACTION_CONTINUE;
}

void RQ_CALL record_step_finished(void* user_data, const rq_step_result* result) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    if (events.steps_finished < events.finished.size()) {
        events.finished[events.steps_finished] = *result;
    }
    ++events.steps_finished;
}

void RQ_CALL record_progress(void* user_data, const rq_progress* progress) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    if (events.progress_reports > 0U && progress->verified_bytes < events.last_progress_bytes) {
        events.progress_went_back = true;
    }
    events.last_progress_bytes = progress->verified_bytes;
    events.progress_region_bytes = progress->region_bytes;
    ++events.progress_reports;
}

void RQ_CALL record_errors(void* user_data, const rq_error_record*, std::uint32_t,
                           std::uint64_t total_errors) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    events.error_total = total_errors;
}

void RQ_CALL record_completed(void* user_data, const rq_run_result*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard<std::mutex> lock(events.mutex);
    events.completed = true;
}

void RQ_CALL record_released(void* user_data, std::uint64_t, std::uint64_t) noexcept {
    session_events& events = events_of(user_data);
    {
        const std::lock_guard<std::mutex> lock(events.mutex);
        events.released = true;
    }
    events.released_changed.notify_all();
}

bool wait_released(session_events& events, std::chrono::seconds timeout) {
    std::unique_lock<std::mutex> lock(events.mutex);
    return events.released_changed.wait_for(lock, timeout, [&events] { return events.released; });
}

/** Destroys a registered session on every exit path of the test, joining its coordinator. */
class session_guard {
public:
    explicit session_guard(rq_session_handle handle) noexcept : handle_(handle) {}

    ~session_guard() {
        try {
            const rq_result result = session_destroy(handle_);
            if (result != RQ_OK) {
                ADD_FAILURE() << "session_destroy returned " << result;
            }
        } catch (const std::exception& e) {
            ADD_FAILURE() << "session_destroy threw: " << e.what();
        }
    }

    session_guard(const session_guard&) = delete;
    session_guard& operator=(const session_guard&) = delete;
    session_guard(session_guard&&) = delete;
    session_guard& operator=(session_guard&&) = delete;

private:
    rq_session_handle handle_;
};

TEST(CoverageSession, RunStepAndProgressTotalsMatchIndependentCount) {
    // Two workers where the host has them, one on a single-processor host; the expected worker
    // count and layout follow the count actually requested.
    const std::vector<logical_processor> processors = enumerate_logical_processors();
    ASSERT_FALSE(processors.empty()) << "no active logical processor was enumerated";
    const auto workers =
        static_cast<std::uint32_t>(std::min<std::size_t>(k_session_max_workers, processors.size()));
    const std::uint64_t expected_tested_bytes = session_tested_bytes(workers);

    // Shaped like Quick's Q1, Q2 and Q4: one windowed sequential step, one bank-stride step and
    // one moving-inversion step, each on its 1 s budget of the real QPC clock.
    const std::array<rq_step_def, k_session_step_count> steps = {
        step_definition("High-toggle sweep", RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING,
                        RQ_THREADS_ALL, 0U, k_session_step_ms, 2'500U),
        step_definition("Random bank thrash", RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE,
                        RQ_THREADS_ALL, 0U, k_session_step_ms, 3'500U),
        step_definition("Moving inversion, walking bits", RQ_PATTERN_MOVING_INVERSION_WALKING,
                        RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0U, k_session_step_ms,
                        5'000U),
    };

    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = k_session_region_bytes;
    // The rounded layout itself, so the engine accepts it for this worker count and refuses to
    // shrink below it: tested_bytes must come out exactly as derived.
    config.min_region_bytes = expected_tested_bytes;
    config.seed = k_seed;
    config.worker_count = workers;
    config.stop_on_error = 0U;
    config.error_detail_cap = k_detail_cap;
    config.progress_interval_ms = k_session_progress_interval_ms;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = k_session_step_count;
    config.steps = steps.data();

    session_events events;
    rq_callbacks callbacks{};
    callbacks.struct_size = sizeof(rq_callbacks);
    callbacks.user_data = &events;
    callbacks.on_prepared = record_prepared;
    callbacks.on_step_starting = continue_step;
    callbacks.on_step_finished = record_step_finished;
    callbacks.on_progress = record_progress;
    callbacks.on_errors = record_errors;
    callbacks.on_completed = record_completed;
    callbacks.on_released = record_released;

    // The production stack (QPC clock, Win32 affinity, real kernels) with the counting layer as
    // the outermost memory access, so it sees every verification the runner issues.
    counting_memory_access* counting = nullptr;
    session_dependencies dependencies;
    dependencies.wrap_memory_access =
        [&counting](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        auto layer = std::make_unique<counting_memory_access>(std::move(real));
        counting = layer.get();
        return layer;
    };

    rq_session_handle handle = nullptr;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), &handle), RQ_OK);
    const session_guard guard(handle);
    ASSERT_NE(counting, nullptr);
    ASSERT_EQ(session_start(handle), RQ_OK);
    if (!wait_released(events, k_session_timeout)) {
        // Ends the run, so the guard's destroy joins the coordinator promptly.
        session_stop(handle, RQ_STOP_USER);
        FAIL() << "no Released within " << k_session_timeout.count() << " s";
    }

    rq_run_result result{};
    result.struct_size = sizeof(rq_run_result);
    ASSERT_EQ(session_get_result(handle, &result), RQ_OK);
    EXPECT_EQ(result.end_reason, RQ_END_COMPLETED) << result.end_detail;
    EXPECT_EQ(result.total_errors, 0U);
    ASSERT_EQ(result.step_result_count, k_session_step_count);

    // Every worker joined before Completed, so the independent count is final.
    const std::uint64_t counted = counting->verified_bytes();
    ASSERT_GT(counted, 0U);
    EXPECT_TRUE(within_band("rq_run_result.verified_bytes", result.verified_bytes, counted));

    std::uint64_t step_sum = 0;
    for (std::uint32_t i = 0; i < result.step_result_count; ++i) {
        const rq_step_result& step = result.step_results[i];
        const std::string what = "rq_step_result[" + std::to_string(i) + "].verified_bytes";
        EXPECT_EQ(step.index, i);
        EXPECT_EQ(step.completed, 1U) << what;
        EXPECT_GE(step.passes, 1U) << what;
        EXPECT_TRUE(within_band(what, step.verified_bytes, counting->verified_bytes_for_step(i)));
        step_sum += step.verified_bytes;
    }
    EXPECT_TRUE(within_band("sum of rq_step_result.verified_bytes", step_sum, counted));

    const std::lock_guard<std::mutex> lock(events.mutex);
    EXPECT_TRUE(events.completed);
    EXPECT_EQ(events.prepared_workers, workers);
    EXPECT_EQ(events.tested_bytes, expected_tested_bytes);
    EXPECT_EQ(events.steps_started, k_session_step_count);
    ASSERT_EQ(events.steps_finished, k_session_step_count);
    for (std::uint32_t i = 0; i < k_session_step_count; ++i) {
        // on_step_finished carried the values rq_session_get_result reports afterwards.
        EXPECT_EQ(events.finished[i].verified_bytes, result.step_results[i].verified_bytes)
            << "step " << i;
    }
    ASSERT_GT(events.progress_reports, 0U);
    EXPECT_FALSE(events.progress_went_back) << "rq_progress.verified_bytes decreased";
    EXPECT_EQ(events.progress_region_bytes, events.tested_bytes);
    EXPECT_TRUE(
        within_band("last rq_progress.verified_bytes", events.last_progress_bytes, counted));
    EXPECT_EQ(events.error_total, result.total_errors);
}

}  // namespace
}  // namespace rq::engine::test
