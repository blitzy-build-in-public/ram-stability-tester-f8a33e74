/**
 * @file step_runner.h
 * @brief Step execution: the pass phases of the four step kinds, pass scheduling against the
 *        time budget, overrun, and the observer through which the coordinator keeps progress
 *        and error batches flowing while a step runs.
 *
 * The session's coordinator runs each step of the plan through one step_runner::run_step call
 * on its own thread. The runner classifies the step, posts each pass phase to the worker pool,
 * issues every memory primitive through IMemoryAccess, waits on the phase barrier and on holds
 * in short slices, ticking its observer between them, and repeats passes until the schedule
 * derived from the step's time budget is met or a stop is observed. Workers only run phase
 * tasks; only the coordinator ticks the observer, and only the session raises C API callbacks.
 *
 * @par Flush-before-verification rule
 * Between a line's most recent store or load and its next verification load, the runner issues
 * a fence, a flush covering the line and a fence, in that order:
 * - windowed steps: stream the window, fence, flush the window, fence, verify the window;
 * - hold steps: stream the slice, fence, flush the slice, fence (phase_kind::pre_hold_flush),
 *   hold, then fence, flush the slice, fence, verify the slice;
 * - moving inversion: the base write and each verify-and-stream sweep are each followed by a
 *   fence, a flush of the slice and a fence;
 * - turnaround: the per-window turnaround schedule of core/patterns/access_order.h.
 * Nothing relies on a line's earlier cache residency, so the rule holds on the first pass and
 * on every later one alike.
 *
 * @par Stress bound
 * Lines are flushed only where that rule requires: once per verified line per phase, once more
 * before a hold, and once per re-read (issued by the error collector). Every access order visits
 * each line or block of its span exactly once per phase, so no step alternates over a small
 * address set and nothing loops flush-and-reload, the row-hammer style the product excludes.
 *
 * @par Layering
 * The runner sits below the session. It never includes core/session.h, whose
 * compute_remaining_ms turns the step_status reported here into rq_progress.remaining_ms, and
 * core/session.cpp includes this header. Free of <windows.h>: the clock, the stop signal, the
 * memory-access seam, the pool, the collector and the coverage counter are only forward-declared,
 * because they are held by reference. Include it only from baseline (SSE2) translation units,
 * never from core/kernels/* or core/patterns/patterns.h.
 */
#pragma once

#include <cstdint>
#include <span>
#include <vector>

#include "ramquick_engine.h"
#include "core/memory/memory_region.h"

namespace rq::engine {

// Held by reference only. Each is defined in its own header (core/memory/memory_access.h,
// core/runner/worker_pool.h, core/runner/coverage_counter.h, core/errors/error_collector.h and,
// with <windows.h>, core/clock.h); the class keys match those definitions, so MSVC raises no
// C4099.
class IMemoryAccess;
class worker_pool;
class coverage_counter;
class error_collector;
class IClock;
class stop_signal;

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

/**
 * Longest real-time interval, in milliseconds, between two observer ticks while run_step waits.
 *
 * The coordinator waits on the phase barrier (worker_pool::wait_phase) and through hold waits
 * (IClock::wait_for_stop) in slices of at most this length and ticks the observer between them,
 * so the session can raise progress at 4 Hz and error batches at up to 10 Hz however long a
 * phase or a hold lasts.
 */
inline constexpr std::uint32_t k_observer_tick_ms = 25;

/**
 * Window of windowed and turnaround steps, 64 MiB: each worker writes, then verifies, its slice
 * one window at a time, so the time since write stays short however large the slice.
 *
 * A window is cut inside one chunk and never spans two, because chunks are separate
 * allocations, so a chunk's last window can be shorter. Chunks are multiples of 2 MiB, so every
 * window is too, and a turnaround window holds at least 512 blocks of 4 KiB. Equal to
 * k_max_window_bytes (core/patterns/access_order.h).
 */
inline constexpr std::uint64_t k_window_bytes = 64 * k_mib;

/**
 * Work group, 1 MiB: the unit of work after which a phase task checks the stop flag and
 * publishes its coverage, and the unit whose stream time is recorded in windowed and
 * whole-slice phases as access_context::write_time_ns.
 *
 * Equal to k_coverage_publish_bytes (core/runner/coverage_counter.h) and
 * k_sequential_range_bytes (core/patterns/access_order.h). This header includes neither, so
 * core/runner/step_runner.cpp, which includes both, asserts the equalities.
 */
inline constexpr std::uint64_t k_work_group_bytes = k_mib;

/**
 * Floor of the mean pass duration that planned_passes divides by, 1 us (1,000 ns).
 *
 * A clock that never advances, such as a fake clock a test does not drive, would give a mean of
 * 0 and an unbounded pass target, so the mean is clamped. The clamp bounds the target but does
 * not make such a step short: tests must still advance fake time.
 */
inline constexpr std::uint64_t k_min_mean_pass_ns = 1'000;

static_assert(k_window_bytes % k_slice_granularity_bytes == 0,
              "every window must be a whole number of 2 MiB slice units");
static_assert(k_chunk_bytes % k_window_bytes == 0, "a full chunk must split into whole windows");
static_assert(k_slice_granularity_bytes % k_work_group_bytes == 0,
              "every chunk and window must be whole work groups");
static_assert(k_window_bytes / k_work_group_bytes == 64,
              "a window's group stream times fit a 64-entry table");

/* ---------------------------------------------------------------------------------------------
 * Pure helpers: unit-testable without memory, threads or a clock
 * ------------------------------------------------------------------------------------------- */

/** How a step's passes are executed; decided by classify_step. */
enum class step_kind : std::uint32_t {
    /**
     * No hold, an order other than RQ_ORDER_TURNAROUND, and no moving inversion: each worker
     * streams a window in the step's order, then fences, flushes, fences and verifies it in the
     * same order (RQ_ACCESS_VERIFY_AFTER_WRITE), window by window.
     */
    windowed,
    /**
     * hold_ms > 0: each pass streams the whole slice in RQ_ORDER_RANDOM_BLOCKS order, flushes it
     * (phase_kind::pre_hold_flush), waits the hold on the stop signal, then flushes and verifies
     * the whole slice in the same order (RQ_ACCESS_VERIFY_AFTER_HOLD).
     */
    hold,
    /**
     * RQ_PATTERN_MOVING_INVERSION_WALKING or _RANDOM: each pass writes the base pattern
     * ascending, then sweeps ascending verifying the base and writing its complement, then
     * descending verifying the complement and writing the base (RQ_ACCESS_READ_MODIFY_WRITE),
     * each over the whole slice.
     */
    moving_inversion,
    /**
     * RQ_ORDER_TURNAROUND without a hold: per window, stream block i, flush block i - 4, fence,
     * verify block i - 4, with its start-up and drain (RQ_ACCESS_TURNAROUND_READ).
     */
    turnaround,
};

/**
 * Classifies a step, in this order of precedence:
 * - moving_inversion when the pattern is RQ_PATTERN_MOVING_INVERSION_WALKING or
 *   RQ_PATTERN_MOVING_INVERSION_RANDOM, whatever its access order: moving inversion implies
 *   the ascending and descending sweeps;
 * - otherwise hold when hold_ms > 0 (session validation guarantees RQ_ORDER_RANDOM_BLOCKS);
 * - otherwise turnaround when the access order is RQ_ORDER_TURNAROUND;
 * - otherwise windowed.
 *
 * Pure; callable from any thread. Validation (validate_config, core/session.h) has rejected
 * every unsupported shape before a step reaches the runner.
 */
constexpr step_kind classify_step(const rq_step_def& step) noexcept {
    if (step.pattern == RQ_PATTERN_MOVING_INVERSION_WALKING ||
        step.pattern == RQ_PATTERN_MOVING_INVERSION_RANDOM) {
        return step_kind::moving_inversion;
    }
    if (step.hold_ms > 0) {
        return step_kind::hold;
    }
    if (step.access_order == RQ_ORDER_TURNAROUND) {
        return step_kind::turnaround;
    }
    return step_kind::windowed;
}

/**
 * The RQ_ACCESS_* value of the verifications a step kind performs: the access_context
 * access_type of its verify and verify_and_stream calls, which becomes
 * rq_error_record.access_type.
 *
 * Pure; callable from any thread.
 *
 * @return RQ_ACCESS_VERIFY_AFTER_WRITE for windowed, RQ_ACCESS_VERIFY_AFTER_HOLD for hold,
 *         RQ_ACCESS_READ_MODIFY_WRITE for moving_inversion and RQ_ACCESS_TURNAROUND_READ for
 *         turnaround.
 */
constexpr std::uint32_t verify_access_type(step_kind kind) noexcept {
    switch (kind) {
    case step_kind::hold:
        return RQ_ACCESS_VERIFY_AFTER_HOLD;
    case step_kind::moving_inversion:
        return RQ_ACCESS_READ_MODIFY_WRITE;
    case step_kind::turnaround:
        return RQ_ACCESS_TURNAROUND_READ;
    case step_kind::windowed:
        break;
    }
    return RQ_ACCESS_VERIFY_AFTER_WRITE;
}

/**
 * The pass schedule of a step: max(1, round(budget_ns / m)) with m = max(mean_pass_ns,
 * k_min_mean_pass_ns), rounding half up as the integer quotient (budget_ns + m / 2) / m,
 * saturated to UINT32_MAX.
 *
 * Recomputed from the measured mean after every completed pass; the step continues while its
 * completed passes are fewer. Time governs: the coverage budget (coverage_budget_bp) never
 * extends a step, so fail-fast ordering is never traded for coverage. Consequences:
 * - every step runs at least one pass, and a first pass longer than the budget (as in a 30 s
 *   pass against a 10 s budget) gives exactly one pass, whose excess is recorded as overrun;
 * - stable passes, within +/-10 % of their mean, end within about half a mean pass of the
 *   budget, and jittered ones within one longest pass.
 *
 * Pure and overflow-free for every input; callable from any thread.
 *
 * @param budget_ns The step's time budget, time_budget_ms x 10^6.
 * @param mean_pass_ns Mean duration of the step's completed passes, in session-clock ns.
 * @return Passes to run, from 1 to UINT32_MAX.
 */
constexpr std::uint32_t planned_passes(std::uint64_t budget_ns,
                                       std::uint64_t mean_pass_ns) noexcept {
    const std::uint64_t mean =
        mean_pass_ns > k_min_mean_pass_ns ? mean_pass_ns : k_min_mean_pass_ns;
    // (budget + mean / 2) / mean without the sum, which could wrap: with budget = q x mean + r,
    // the quotient is q + 1 exactly when r + mean / 2 >= mean, that is r >= mean - mean / 2.
    const std::uint64_t quotient = budget_ns / mean;
    const std::uint64_t remainder = budget_ns % mean;
    const std::uint64_t rounded = quotient + (remainder >= mean - mean / 2 ? 1u : 0u);
    if (rounded == 0) {
        return 1;
    }
    return rounded > UINT32_MAX ? UINT32_MAX : static_cast<std::uint32_t>(rounded);
}

/**
 * The pattern seed of one step: pattern_spec::seed for every pass of step step_index.
 *
 * Steps that share a pattern (Q2, Q3, Q5 and Q6 are all RQ_PATTERN_RANDOM) would otherwise
 * write identical data on pass 0, so the session seed is mixed with the step index. The value is
 * the SplitMix64 output at counter step_index + 1 for the state seed; in arithmetic modulo 2^64:
 *
 *     z = seed + 0x9E3779B97F4A7C15 x (step_index + 1)
 *     z = (z ^ (z >> 30)) x 0xBF58476D1CE4E5B9
 *     z = (z ^ (z >> 27)) x 0x94D049BB133111EB
 *     result = z ^ (z >> 31)
 *
 * The finalizer is a bijection and the increment is odd, so distinct step indices always get
 * distinct seeds. The runner passes the result in pattern_spec::seed through every IMemoryAccess
 * call, so substitute layers and tests see it, and expected_value (core/patterns/patterns.h)
 * reproduces any word the step wrote.
 *
 * Pure; callable from any thread.
 *
 * @param seed The session seed, rq_session_config.seed.
 * @param step_index Index of the step in the plan, from 0.
 */
constexpr std::uint64_t step_pattern_seed(std::uint64_t seed, std::uint32_t step_index) noexcept {
    // Spelled out rather than named: core/patterns/patterns.h defines these SplitMix64 values as
    // namespace-scope constants, and this header must not include it (it pulls in
    // <immintrin.h>) nor redefine those names, since core/runner/step_runner.cpp includes both.
    std::uint64_t z = seed + 0x9E3779B97F4A7C15ull * (std::uint64_t{step_index} + 1);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

static_assert(planned_passes(10'000'000'000ull, 30'000'000'000ull) == 1,
              "a pass longer than the budget runs alone");
static_assert(planned_passes(10'000'000'000ull, 4'000'000'000ull) == 3, "2.5 rounds half up");
static_assert(planned_passes(10'000'000'000ull, 1'000'000'000ull) == 10,
              "stable 1 s passes fill a 10 s budget");
static_assert(planned_passes(40'000'000'000ull, 0) == 40'000'000,
              "a zero mean is clamped to k_min_mean_pass_ns");
static_assert(planned_passes(0, 1'000'000'000ull) == 1, "every step runs at least one pass");
static_assert(planned_passes(UINT64_MAX, 0) == UINT32_MAX, "the schedule saturates");
static_assert(step_pattern_seed(0, 0) == 0xE220A8397B1DCDAFull,
              "step 0 of seed 0 is SplitMix64's first output for state 0");
static_assert(step_pattern_seed(0, 1) != step_pattern_seed(0, 0),
              "steps sharing a pattern must not share a seed");

/* ---------------------------------------------------------------------------------------------
 * Observer contract
 * ------------------------------------------------------------------------------------------- */

/**
 * The running step's schedule, as run_step reports it to its observer at every tick.
 *
 * A plain aggregate built on the coordinator thread and passed by const reference, valid only
 * during the tick. Its fields are the running step's inputs of compute_remaining_ms
 * (remaining_inputs, core/session.h) and feed rq_progress.
 */
struct step_status {
    /** Index of the running step in the plan, from 0: rq_progress.step_index. */
    std::uint32_t step_index = 0;
    /**
     * Index of the most recently begun pass, from 0: the pass in progress or, at the tick that
     * follows a completed pass, that pass. The same value as the pass's pattern_spec::pass and
     * access_context::pass_index; rq_progress.pass_index.
     */
    std::uint32_t pass_index = 0;
    /** Passes of the step completed so far; a pass cut short by a stop never counts. */
    std::uint32_t passes_done = 0;
    /**
     * The current schedule, planned_passes(budget, mean_pass_ns), recomputed after every
     * completed pass; 0 until the first pass completes. rq_progress.passes_planned.
     */
    std::uint32_t passes_planned = 0;
    /** Mean duration of the completed passes in session-clock ns; 0 until the first one. */
    std::uint64_t mean_pass_ns = 0;
    /**
     * Session-clock ns since the step started, read from the session IClock at the tick;
     * non-decreasing within a step. rq_progress.step_elapsed_ms after division by 10^6.
     */
    std::uint64_t step_elapsed_ns = 0;
};

/**
 * The coordinator's hook into a running step.
 *
 * run_step calls tick() only on the coordinator thread, from inside run_step:
 * - at least every k_observer_tick_ms of real time while it waits on a phase barrier;
 * - between the slices of a hold wait;
 * - once after every completed pass.
 * Workers never call it.
 *
 * The session's observer decides from IClock::now_ns() whether a progress callback (every
 * progress_interval_ms) or an error batch (at least k_error_batch_interval_ns apart,
 * core/errors/error_collector.h) is due. It computes rq_progress.remaining_ms with
 * compute_remaining_ms (core/session.h): -1 until the first step's first pass completes; before
 * the current step's first pass completes, max(0, budget - step_elapsed); afterwards
 * max(0, passes_planned x mean_pass - step_elapsed); plus the budgets of every later step.
 *
 * tick() may raise C API callbacks, and a callback may call rq_session_stop. It must return
 * promptly: workers keep running meanwhile, but the barrier, the hold and the stop flag are
 * checked again only after it returns, so a slow tick adds to stop latency. An exception it
 * throws propagates out of run_step.
 *
 * Never destroyed through this base, whose destructor is therefore protected and non-virtual.
 */
class step_observer {
public:
    /**
     * Reports the running step's schedule. Coordinator thread only.
     *
     * @param status The schedule at this tick; valid only during the call.
     */
    virtual void tick(const step_status& status) = 0;

protected:
    /** Implementations are owned and destroyed by their concrete type. */
    ~step_observer() = default;
};

/* ---------------------------------------------------------------------------------------------
 * Outcome, dependencies and the runner
 * ------------------------------------------------------------------------------------------- */

/**
 * What one run_step call did. The session converts it to rq_step_result in milliseconds and
 * takes the step's true error count from error_collector::step_errors, not from here.
 */
struct step_outcome {
    /** True only when every planned pass finished and no stop was observed. */
    bool completed = false;
    /** Full passes completed; a pass cut short by a stop is not counted. */
    std::uint32_t passes = 0;
    /** Session-clock ns from the step's start until run_step returned. */
    std::uint64_t actual_ns = 0;
    /**
     * max(0, actual_ns - time_budget_ms x 10^6): the time past the budget, as for an oversized
     * pass (about 20 s for one 30 s pass against a 10 s budget). Stable passes can also end up
     * to about half a mean pass past the budget.
     */
    std::uint64_t overrun_ns = 0;
    /**
     * Bytes verified in the step: coverage_counter::step_total() once the last phase barrier
     * was crossed. It includes the bytes verified by a pass cut short by a stop.
     */
    std::uint64_t verified_bytes = 0;
};

/**
 * Everything a step_runner works with. Every referenced object, and the slice array the span
 * views, must outlive the runner, and must also outlive worker_pool::shutdown() of the pool
 * (see step_runner).
 *
 * An aggregate of references, filled field by field (designated initialisers) by the session
 * and by the native tests.
 */
struct step_runner_dependencies {
    /**
     * One slice per worker, in worker order: memory_region::slice(i) for i below the pool's
     * worker_count(), so slices.size() equals it. Tests may build slice_views over their own
     * buffers, with chunks of any 2 MiB multiple. In RQ_THREADS_HALF steps the active even
     * worker w also covers slice w + 1 when it exists.
     */
    std::span<const slice_view> slices;
    /**
     * The session's outermost memory-access layer, so the counting and fault-injecting layers
     * of the test DLL see every primitive. The same layer the error collector re-reads through.
     */
    IMemoryAccess& memory;
    /** The session's worker pool, already started with outcome ok; one worker per slice. */
    worker_pool& pool;
    /**
     * The run's error collector. run_step calls begin_step on it and binds each worker's sink
     * before that worker's verifications.
     */
    error_collector& errors;
    /** The session's coverage counter. run_step calls begin_step on it before the step. */
    coverage_counter& coverage;
    /**
     * The session clock, the one the error collector and the substitute layers read. now_ns()
     * is also called from workers to record write times, so implementations must be thread-safe.
     */
    IClock& clock;
    /** The session's stop signal: polled by workers every 1 MiB and waited on during holds. */
    const stop_signal& stop;
    /** The session seed, rq_session_config.seed; mixed per step by step_pattern_seed. */
    std::uint64_t seed = 0;
};

/**
 * Runs the steps of one session, one run_step call per step, on the coordinator thread.
 *
 * @par Threading
 * The constructor and run_step run on the coordinator, which alone sequences steps; run_step is
 * never called concurrently, and never from a phase task. Phase tasks run on the workers; each
 * worker writes only the write-time tables of the slices it covers in that phase, so the tables
 * need no synchronisation beyond the phase barrier.
 *
 * @par Lifetime and exceptions
 * An exception (from worker_pool::wait_phase, IClock::wait_for_stop or the observer) can leave
 * run_step while a phase is still outstanding. Phase tasks therefore refer only to state the
 * runner and its dependencies own, or capture by value, never to run_step's stack frame. The
 * session's coordinator catches the exception, requests RQ_END_INTERNAL_FAULT, which makes the
 * running tasks return within one work group, and shuts the pool down before destroying the
 * runner or any dependency.
 *
 * Non-copyable and non-movable: phase tasks refer to the runner while they run.
 */
class step_runner {
public:
    /**
     * Keeps the dependencies and preallocates one write-time table per slice of
     * ceil(slice.bytes / k_work_group_bytes) entries, for the whole-slice phases of hold and
     * moving-inversion steps, so no pass allocates. Defined in core/runner/step_runner.cpp.
     *
     * @param dependencies Copied; the references in it must outlive the runner.
     * @throws std::bad_alloc when the tables cannot be allocated. The coordinator converts it
     *         to RQ_END_INTERNAL_FAULT.
     */
    explicit step_runner(const step_runner_dependencies& dependencies);

    step_runner(const step_runner&) = delete;
    step_runner& operator=(const step_runner&) = delete;
    step_runner(step_runner&&) = delete;
    step_runner& operator=(step_runner&&) = delete;

    /**
     * Runs one step on the calling (coordinator) thread and returns when it is done or stopped.
     *
     * Calls errors.begin_step and coverage.begin_step, then runs passes of the step's kind
     * (classify_step) with pattern seed step_pattern_seed(seed, step_index), all through
     * dependencies.memory. After every completed pass it recomputes planned_passes from the
     * measured mean and ticks the observer; it continues while fewer passes are done. On a stop
     * it returns early, with completed false, as soon as every worker has finished its current
     * work group and the phase barrier is crossed. Every phase barrier has been crossed when it
     * returns normally.
     *
     * Preconditions: step passed validate_config (core/session.h); step_index is below the
     * error collector's step_count(); the pool has no phase outstanding. Defined in
     * core/runner/step_runner.cpp.
     *
     * @param step The step definition, from the session's deep-copied plan.
     * @param step_index Index of the step in the plan, from 0.
     * @param observer Ticked on this thread while the step runs; see step_observer.
     * @return What the step did.
     * @throws std::bad_alloc, std::logic_error or std::system_error, from the pool, the clock or
     *         the observer; only C++ exceptions, which the session's coordinator entry converts
     *         to RQ_END_INTERNAL_FAULT (see Lifetime and exceptions).
     */
    step_outcome run_step(const rq_step_def& step, std::uint32_t step_index,
                          step_observer& observer);

private:
    // The session's dependencies; immutable for the runner's lifetime.
    const step_runner_dependencies deps_;
    // One table per slice, in slice order, of ceil(slice.bytes / k_work_group_bytes) entries:
    // entry g holds the session-clock stamp taken right after the slice's g-th work group, in
    // issue order, was last streamed in a whole-slice phase. Allocated by the constructor and
    // only written afterwards; during a phase each slice's table is used by the one worker that
    // covers that slice.
    std::vector<std::vector<std::uint64_t>> slice_write_times_;
};

}  // namespace rq::engine
