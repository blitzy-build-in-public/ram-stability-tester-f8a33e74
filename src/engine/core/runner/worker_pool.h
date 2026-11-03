/**
 * @file worker_pool.h
 * @brief The pinned worker pool: one below-normal-priority thread per logical processor across
 *        every processor group, phase dispatch with a barrier, Half mode, and the affinity seam.
 *
 * The coordinator starts the pool during preparation and then drives every pass phase (write,
 * hold, verify) through it: post_phase() hands one task to the active workers, and wait_phase()
 * is the barrier the coordinator crosses before the next phase. The coordinator alone sequences
 * steps and raises C API callbacks; workers only run phase tasks.
 *
 * Pinning is explicit, by processor group, because a group holds at most 64 logical processors
 * and Windows 10 does not spread a process's threads across groups. Every worker pins itself
 * through thread_affinity before it can run any task, and start() fails on any failed pin, so no
 * unpinned worker ever issues memory traffic.
 *
 * Free of <windows.h>: thread handles and synchronization objects live in worker_pool.cpp behind
 * a private impl, so includers such as core/session.h get no Win32 dependency from here. It must
 * not include core/session.h or core/runner/step_runner.h, which depend on it. The AVX2 and
 * AVX-512 kernel translation units must not include it.
 */
#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <span>

#include "core/system_info.h"

namespace rq::engine {

class stop_signal;

/**
 * The worker pool's affinity seam: pins the calling thread to one logical processor.
 *
 * The pool calls pin() on each worker thread itself, as that thread's first action and before it
 * can run any phase task, so an implementation pins the calling thread and no other.
 *
 * Production uses win32_thread_affinity. The test DLL (rq_fault_spec.affinity_fail_worker and
 * affinity_fail_error) and tests/engine.tests/test_worker_pool.cpp substitute it to fail one
 * worker with a chosen Windows error, for example 87 (ERROR_INVALID_PARAMETER).
 */
class thread_affinity {
public:
    virtual ~thread_affinity() = default;

    /**
     * Pins the calling thread to one logical processor.
     *
     * Called once on each worker thread, before that worker issues any memory traffic, and
     * concurrently from different workers: implementations must be safe for concurrent calls.
     * Failure is reported through the return value, never by throwing.
     *
     * @param worker_index Index of the calling worker, 0 to worker_count - 1. The production
     *        implementation ignores it; substitutes use it to choose the worker that fails.
     * @param group Processor group of the target processor (logical_processor::group).
     * @param processor Index of the target processor within its group, 0-63
     *        (logical_processor::number).
     * @return 0 when the calling thread is pinned; otherwise a nonzero Windows error code, which
     *         worker_pool::start reports as pool_start_result::os_error.
     */
    virtual uint32_t pin(uint32_t worker_index, uint16_t group, uint8_t processor) = 0;
};

/**
 * The production affinity: SetThreadGroupAffinity on the calling thread with
 * GROUP_AFFINITY{ .Mask = KAFFINITY{1} << processor, .Group = group }.
 *
 * Stateless, so one instance serves every worker of every session concurrently.
 */
class win32_thread_affinity final : public thread_affinity {
public:
    /**
     * Pins the calling thread through SetThreadGroupAffinity(GetCurrentThread(), ...).
     *
     * @return 0 on success; otherwise GetLastError(), or ERROR_GEN_FAILURE when the failed call
     *         left the last error at 0, so a failure is never reported as success.
     */
    uint32_t pin(uint32_t worker_index, uint16_t group, uint8_t processor) override;
};

/**
 * Creates the production affinity: the default for session_dependencies::affinity when it is
 * null (core/session.h).
 *
 * @throws std::bad_alloc when the object cannot be allocated.
 */
std::shared_ptr<thread_affinity> make_win32_thread_affinity();

/** Which workers run a posted phase. */
enum class active_set : uint32_t {
    /** Every worker: RQ_THREADS_ALL steps. */
    all,
    /**
     * Even-indexed workers only (0, 2, 4, ...): RQ_THREADS_HALF steps. Each odd worker sleeps
     * while its even partner, worker i - 1, covers both slices. With an odd worker count the
     * last worker is even and has no partner, so it covers only its own slice. The pool knows
     * nothing of slices: the step runner maps them from the worker index.
     */
    even,
};

/**
 * One phase of a pass (write, hold or verify) for one worker, called with that worker's index.
 *
 * - Runs once on each active worker thread per posted phase. All active workers call the same
 *   function object concurrently, so its target must tolerate concurrent calls and select any
 *   per-worker state by worker_index.
 * - Runs even when a stop is already pending; the task decides how little to do. It must return
 *   promptly once stop_signal::stop_requested() is true: tasks check after every 1 MiB block.
 * - Must never raise a C API callback: only the coordinator does that.
 * - A C++ exception escaping it is caught by the worker: a std::exception becomes
 *   stop.request(RQ_END_INTERNAL_FAULT, 0, e.what()), anything else
 *   stop.request(RQ_END_INTERNAL_FAULT, 0, "unknown C++ exception in worker"). The worker then
 *   completes the phase as usual, so the barrier never deadlocks. SEH faults are never caught
 *   (no __try, no translator): they terminate the process by design.
 */
using phase_task = std::function<void(uint32_t worker_index)>;

/** Outcome of worker_pool::start, with the diagnostics the session turns into an end reason. */
struct pool_start_result {
    /** How start ended. */
    enum class status : uint32_t {
        /** Every worker is pinned, set to below-normal priority where possible, and idle. */
        ok,
        /**
         * A worker's pin failed. The session ends the run RQ_END_AFFINITY_FAILED with os_error
         * and the detail "SetThreadGroupAffinity failed: group g, processor p". No traffic has
         * run, and the pool has been shut down.
         */
        affinity_failed,
        /**
         * CreateThread failed. The session ends the run as an engine fault (RQ_E_THREAD_CREATE,
         * reported as RQ_END_INTERNAL_FAULT) with os_error. The pool has been shut down.
         */
        thread_create_failed,
        /**
         * A stop was requested before start returned, and no creation or pin failed. The pool
         * has been shut down; the session ends the run with the recorded stop reason.
         */
        stopped,
    };

    /**
     * How start ended; the fields below are its diagnostics. Named outcome rather than status
     * so the member does not hide the enumeration's name inside the struct.
     */
    status outcome = status::ok;
    /**
     * The worker whose pin or thread creation failed: for affinity_failed the lowest failing
     * index, so the report is deterministic. 0 for ok and stopped.
     */
    uint32_t failed_worker = 0;
    /** Processor group of failed_worker's placement. 0 for ok and stopped. */
    uint16_t group = 0;
    /** Processor number within the group of failed_worker's placement. 0 for ok and stopped. */
    uint8_t processor = 0;
    /**
     * Windows error of the failing pin (thread_affinity::pin) or CreateThread call; nonzero for
     * affinity_failed and thread_create_failed, 0 for ok and stopped.
     */
    uint32_t os_error = 0;
    /**
     * Workers whose SetThreadPriority(THREAD_PRIORITY_BELOW_NORMAL) failed after a successful
     * pin. Not fatal, because priority only protects responsiveness: the session reports a
     * nonzero count with OutputDebugStringW and the run continues.
     */
    uint32_t priority_failures = 0;
    /** Windows error of the lowest-indexed priority failure; 0 when priority_failures is 0. */
    uint32_t first_priority_error = 0;
};

/**
 * One pinned, below-normal-priority thread per placement, driven phase by phase by its owner,
 * the session's coordinator.
 *
 * @par Lifecycle
 * Construct, start(), then (post_phase(), wait_phase() until it returns true) for every phase,
 * then shutdown() or destruction. The destructor calls shutdown(), so every thread is joined and
 * every handle and event is closed on every exit path. The thread_affinity and stop_signal passed
 * to start() must outlive the pool.
 *
 * @par Threading
 * - start(), post_phase(), wait_phase() and shutdown() are called only by the owning thread,
 *   never from a phase task. worker_count() and active_count() may also be called from tasks.
 * - Idle workers block and never spin, so a worker waiting out a hold, or a sleeping Half
 *   partner, leaves its processor free.
 * - Worker entry points catch C++ exceptions as phase_task describes, and never catch SEH faults.
 *
 * Non-copyable and non-movable: worker threads refer to the pool's state while they run.
 */
class worker_pool {
public:
    /**
     * Creates an idle pool: no thread runs until start().
     *
     * @throws std::bad_alloc, or std::system_error when a synchronization object cannot be
     *         created. The coordinator converts either to RQ_END_INTERNAL_FAULT.
     */
    worker_pool();

    /** Calls shutdown(). */
    ~worker_pool();

    worker_pool(const worker_pool&) = delete;
    worker_pool& operator=(const worker_pool&) = delete;
    worker_pool(worker_pool&&) = delete;
    worker_pool& operator=(worker_pool&&) = delete;

    /**
     * Creates the workers and waits until every one has pinned itself and reported.
     *
     * Worker i runs on placements[i]: group-major across every processor group, as
     * enumerate_logical_processors() returns them. Each worker thread, as its first actions,
     * calls affinity.pin(i, placements[i].group, placements[i].number); after a successful pin
     * it sets its own priority to THREAD_PRIORITY_BELOW_NORMAL, counting a failure without
     * stopping; then it reports ready and waits for a phase. A worker whose pin failed reports
     * too, but never runs a task.
     *
     * Outcome, in order of precedence:
     * - CreateThread fails for worker i: no further thread is created, those already created are
     *   shut down, and the result is thread_create_failed for worker i.
     * - A pin failed: once every worker has reported, the pool is shut down and the result is
     *   affinity_failed for the lowest failing index, with its placement and the pin's error.
     * - stop.stop_requested() is true once every worker has reported: the pool is shut down and
     *   the result is stopped.
     * - Otherwise ok, with the priority-failure count, and phases may be posted.
     *
     * No phase can run before start returns ok. The wait for workers to report is not
     * stop-interruptible; it lasts one pin and one priority change per worker.
     *
     * @param placements Processor of each worker. Needs to stay valid only until start returns.
     * @param worker_count Number of workers to create: 1 to placements.size().
     * @param affinity Pins each worker on its own thread. Must outlive the pool.
     * @param stop The session's stop signal, which receives task exceptions and is checked once
     *        every worker has reported. Must outlive the pool.
     * @return The outcome; worker_count() reports worker_count only when it is ok.
     * @throws std::invalid_argument when worker_count is 0 or exceeds placements.size();
     *         std::logic_error when start was already called; std::bad_alloc. No thread is left
     *         running when start throws.
     */
    pool_start_result start(std::span<const logical_processor> placements, uint32_t worker_count,
                            thread_affinity& affinity, stop_signal& stop);

    /**
     * Hands one phase task to the workers in set and wakes them.
     *
     * Owning thread only, with at most one phase outstanding: the previous phase must have been
     * completed, as shown by wait_phase() returning true. Each active worker runs task once with
     * its own index; inactive workers keep sleeping. The pool keeps the task object until the
     * next post_phase() or shutdown(); anything it captures by reference must stay valid until
     * the phase completes.
     *
     * @param task The phase body; must not be empty.
     * @param set The workers that run it.
     * @throws std::invalid_argument for an empty task; std::logic_error when start() has not
     *         returned ok, after shutdown(), or while a phase is outstanding.
     */
    void post_phase(phase_task task, active_set set);

    /**
     * The phase barrier: waits until every active worker has finished the posted task.
     *
     * Owning thread only. The wait is in real time, never through IClock, so a fake clock cannot
     * stall the owner: the step runner loops on it with a 25 ms timeout (k_observer_tick_ms) and
     * ticks its observer between calls, which drives progress callbacks and error batches.
     *
     * A true return is the barrier between the write, hold and verify phases: everything the
     * tasks did happens-before the owner's next action, so coverage_counter totals and
     * error_collector state read after it are exact.
     *
     * A condition-variable wait that fails other than by timing out means the barrier can never
     * be crossed, so it is never reported as an incomplete phase. wait_phase first requests an
     * RQ_END_INTERNAL_FAULT stop, stop.request(RQ_END_INTERNAL_FAULT, 0, detail), so running
     * tasks return within one 1 MiB block; then it throws. os_error is 0, and the detail reads
     * "worker_pool::wait_phase: SleepConditionVariableSRW failed (Windows error n)". The phase
     * stays outstanding.
     *
     * @param timeout_ms Upper bound on the wait, in milliseconds of real time; 0 only polls.
     * @return true when no phase is outstanding: the posted phase has completed, none has been
     *         posted, or shutdown() has run. false when the timeout elapsed first.
     * @throws std::system_error when the condition-variable wait fails other than by timing out,
     *         carrying the Windows error. The coordinator converts it to RQ_END_INTERNAL_FAULT,
     *         and the stop requested first keeps this diagnostic as the first writer.
     */
    bool wait_phase(uint32_t timeout_ms);

    /**
     * Number of workers started by a start() that returned ok; 0 before then and after a start
     * with any other outcome. Unchanged by shutdown().
     */
    uint32_t worker_count() const noexcept;

    /**
     * Number of workers that run a phase posted with set: worker_count() for all, and
     * ceil(worker_count() / 2) for even. Feeds error_collector::begin_step and
     * rq_error_record.active_workers.
     */
    uint32_t active_count(active_set set) const noexcept;

    /**
     * Signals exit, wakes every idle worker and joins every thread, one handle at a time:
     * WaitForMultipleObjects accepts at most 64 handles, fewer than a multi-group machine has
     * workers. Each handle is closed once its worker is joined.
     *
     * A worker counts as joined only when WaitForSingleObject returns WAIT_OBJECT_0. Any other
     * result leaves its liveness unknown, and a live worker would use the pool and task state
     * this call frees, so shutdown writes an OutputDebugStringW diagnostic and fails the process
     * fast (__fastfail(FAST_FAIL_FATAL_APP_EXIT)): it never returns with a worker of unknown
     * liveness. The next launch then reports the run through journal recovery.
     *
     * Idempotent: safe after a failed start, twice, and from the destructor. Owning thread only;
     * from a phase task it would deadlock. It does not request a stop: tasks already running are
     * waited for, so an owner tearing down mid-phase requests a stop first, and running tasks
     * then return within one 1 MiB block. A worker that sees the exit request before beginning
     * an outstanding phase skips it. The session calls it before raising Completed, so the
     * workers have joined by then.
     */
    void shutdown() noexcept;

private:
    /** Threads, handles, per-worker start records and phase state; defined in worker_pool.cpp. */
    struct impl;
    std::unique_ptr<impl> impl_;
};

}  // namespace rq::engine
