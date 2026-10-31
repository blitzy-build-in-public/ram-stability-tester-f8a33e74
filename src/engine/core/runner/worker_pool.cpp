/**
 * @file worker_pool.cpp
 * @brief The pinned worker pool: thread creation and pinning, phase dispatch, the phase barrier
 *        and shutdown.
 *
 * Compiled at the x64 SSE2 baseline. Workers block on condition variables whenever they have no
 * phase to run, so a sleeping Half partner or an idle pool leaves its processor free.
 *
 * Exceptions: every worker entry point catches C++ exceptions and converts them to an
 * RQ_END_INTERNAL_FAULT stop. SEH faults (access violation, illegal instruction, in-page error)
 * are deliberately never handled here: no __try, no translator, no vectored handler. Under
 * unstable memory such a fault may itself be a symptom, continuing after one is unsafe, and the
 * next launch reports the run through journal recovery.
 */
#include <windows.h>
#include <intrin.h>

#include "core/runner/worker_pool.h"
#include "core/clock.h"
#include "ramquick_engine.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <iterator>
#include <memory>
#include <stdexcept>
#include <system_error>
#include <utility>
#include <vector>

namespace rq::engine {

namespace {

/** Owns one Win32 handle (a worker thread) and closes it through close_handle_checked. */
class unique_handle {
public:
    unique_handle() noexcept = default;
    explicit unique_handle(HANDLE handle) noexcept : handle_(handle) {}
    ~unique_handle() { reset(); }

    unique_handle(const unique_handle&) = delete;
    unique_handle& operator=(const unique_handle&) = delete;

    unique_handle(unique_handle&& other) noexcept
        : handle_(std::exchange(other.handle_, nullptr)) {}
    unique_handle& operator=(unique_handle&& other) noexcept {
        if (this != &other) {
            reset();
            handle_ = std::exchange(other.handle_, nullptr);
        }
        return *this;
    }

    HANDLE get() const noexcept { return handle_; }

    void reset() noexcept {
        if (handle_ != nullptr) {
            // A failed close is reported and the handle given up all the same: closing the
            // value again could close a handle that has since been reused for another object.
            close_handle_checked(handle_, L"worker thread handle");
            handle_ = nullptr;
        }
    }

private:
    HANDLE handle_ = nullptr;
};

/** Holds an SRW lock exclusively for its scope. SRW locks cannot fail, so this never throws. */
class srw_exclusive {
public:
    explicit srw_exclusive(SRWLOCK& lock) noexcept : lock_(lock) {
        AcquireSRWLockExclusive(&lock_);
    }
    ~srw_exclusive() { ReleaseSRWLockExclusive(&lock_); }

    srw_exclusive(const srw_exclusive&) = delete;
    srw_exclusive& operator=(const srw_exclusive&) = delete;

private:
    SRWLOCK& lock_;
};

/** A group holds at most 64 logical processors: the bit width of KAFFINITY on x64. */
constexpr unsigned k_processors_per_group = 64;

/** End detail for a worker exception that is not a std::exception. */
constexpr char k_unknown_exception_detail[] = "unknown C++ exception in worker";

/** GetLastError() of a call that failed, never 0, so a failure is never reported as success. */
uint32_t last_error_or_general_failure() noexcept {
    const DWORD error = GetLastError();
    return error != 0 ? static_cast<uint32_t>(error) : static_cast<uint32_t>(ERROR_GEN_FAILURE);
}

/**
 * Sleeps on cv until woken; the caller holds lock exclusively and holds it again on return.
 *
 * @throws std::system_error when the wait itself fails, which an INFINITE wait does only on
 *         a broken process. Worker and owner callers convert it as their contracts state.
 */
void sleep_on(CONDITION_VARIABLE& cv, SRWLOCK& lock) {
    if (SleepConditionVariableSRW(&cv, &lock, INFINITE, 0) == FALSE) {
        throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                "SleepConditionVariableSRW failed in the worker pool");
    }
}

/** Whether worker_index runs a phase posted with set. */
bool is_active(active_set set, uint32_t worker_index) noexcept {
    return set == active_set::all || worker_index % 2u == 0u;
}

/**
 * Ends the process because shutdown() could not confirm that a worker exited.
 *
 * A worker whose join is unconfirmed may still be running: it uses the pool's lock, condition
 * variables and counter and the task's captures, all of which the owner frees once shutdown()
 * returns. Continuing could let a live pinned worker write freed memory, and a memory tester must
 * never report a verdict over state that may be corrupted. So the process ends here, as it does
 * for an SEH fault, and the next launch reports the run through journal recovery; std::thread
 * likewise terminates rather than abandon a joinable thread.
 *
 * __fastfail rather than std::terminate or abort(): it runs no handler, atexit function or static
 * destructor, any of which could touch the state in question. The diagnostic goes to the debugger
 * first, because the C API cannot carry it once the process is gone. _snwprintf_s with _TRUNCATE
 * cuts an overlong line instead of raising the CRT invalid-parameter handler.
 */
[[noreturn]] void fail_unconfirmed_join(std::size_t worker_index, DWORD wait_result,
                                        DWORD error) noexcept {
    wchar_t buffer[192];
    _snwprintf_s(buffer, std::size(buffer), _TRUNCATE,
                 L"RAMQuick engine: worker_pool::shutdown could not confirm that worker %zu exited "
                 L"(wait result 0x%08lX, Windows error %lu); ending the process\n",
                 worker_index, wait_result, error);
    OutputDebugStringW(buffer);
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}

}  // namespace

uint32_t win32_thread_affinity::pin([[maybe_unused]] uint32_t worker_index, uint16_t group,
                                    uint8_t processor) {
    // A larger index would shift past KAFFINITY's width, which is undefined, not just wrong.
    if (processor >= k_processors_per_group) {
        return static_cast<uint32_t>(ERROR_INVALID_PARAMETER);
    }
    // Explicit groups, because Windows 10 never moves a thread out of its process's initial
    // group by itself, and one group holds at most 64 processors.
    GROUP_AFFINITY affinity{};
    affinity.Mask = KAFFINITY{1} << processor;
    affinity.Group = group;
    if (SetThreadGroupAffinity(GetCurrentThread(), &affinity, nullptr) == FALSE) {
        return last_error_or_general_failure();
    }
    return 0;
}

std::shared_ptr<thread_affinity> make_win32_thread_affinity() {
    return std::make_shared<win32_thread_affinity>();
}

/**
 * Pool state. One SRW lock guards the phase state, the start records and the ready count; the
 * three condition variables share it, so every check-then-sleep is atomic against the wake it
 * waits for.
 *
 * Completion is a condition variable rather than a Win32 event: the last worker of a phase can
 * be preempted between its final decrement and raising an event, and a late SetEvent landing
 * after the next post_phase would leave the event signalled for a phase still running, turning
 * every later wait_phase call into an immediate return and the coordinator into a spin. A
 * predicate checked under the lock cannot go stale that way.
 */
struct worker_pool::impl {
    /** One worker's thread context and start record; its address is the thread parameter. */
    struct worker_slot {
        impl* pool = nullptr;
        uint32_t index = 0;
        logical_processor placement{};
        /** Start record, written by the worker under lock before it reports ready. */
        uint32_t pin_error = 0;
        uint32_t priority_error = 0;
        /** Set under lock when the worker's loop ended on a C++ exception; see depart(). */
        std::atomic<bool> departed{false};
    };

    static DWORD WINAPI thread_entry(void* context);

    uint32_t prepare_worker(worker_slot& slot) noexcept;
    void run_phases(const worker_slot& slot, bool pinned, uint64_t& seen_generation);
    void run_task(const phase_task& body, uint32_t worker_index) noexcept;
    void finish_share() noexcept;
    void depart(worker_slot& slot, uint64_t seen_generation) noexcept;
    void request_internal_fault(const char* detail) noexcept;

    thread_affinity* affinity = nullptr;
    stop_signal* stop = nullptr;

    /** Sized once in start(), before any thread exists, so slot addresses stay valid. */
    std::vector<worker_slot> slots;
    std::vector<unique_handle> threads;

    // Owner-thread state. worker_count is also read by tasks; it is written before the first
    // post_phase, whose lock publishes it, and never again.
    uint32_t worker_count = 0;
    bool start_called = false;
    bool started_ok = false;
    bool shut_down = false;

    SRWLOCK lock = SRWLOCK_INIT;
    /** Workers wait here for a new phase or the exit request. */
    CONDITION_VARIABLE work_cv = CONDITION_VARIABLE_INIT;
    /** start() waits here until every worker has reported. */
    CONDITION_VARIABLE ready_cv = CONDITION_VARIABLE_INIT;
    /** wait_phase() waits here until remaining reaches 0. */
    CONDITION_VARIABLE done_cv = CONDITION_VARIABLE_INIT;

    // Guarded by lock. generation, exit_requested and ready_count, like worker_slot::departed,
    // are atomic storage, as every shared counter and flag is, while the lock still orders them
    // against the condition-variable predicates, so each access under it is relaxed.
    phase_task task;
    active_set phase_set = active_set::all;
    std::atomic<uint64_t> generation{0};
    std::atomic<bool> exit_requested{false};
    std::atomic<uint32_t> ready_count{0};

    /**
     * Active workers that have not finished the posted phase. Decremented outside the lock with
     * acq_rel: the owner's acquire load that reads 0 synchronizes with every decrement of the
     * release sequence, so everything the tasks did happens-before the owner's next action.
     */
    std::atomic<uint32_t> remaining{0};
};

/**
 * A worker thread: pin, lower priority and report, then run phases until exit. Every C++
 * exception is contained here; SEH faults pass through untouched and end the process.
 */
DWORD WINAPI worker_pool::impl::thread_entry(void* context) {
    worker_slot& slot = *static_cast<worker_slot*>(context);
    impl& pool = *slot.pool;
    const uint32_t pin_error = pool.prepare_worker(slot);
    uint64_t seen_generation = 0;
    try {
        pool.run_phases(slot, pin_error == 0, seen_generation);
    } catch (const std::exception& e) {
        pool.request_internal_fault(e.what());
        pool.depart(slot, seen_generation);
    } catch (...) {
        pool.request_internal_fault(k_unknown_exception_detail);
        pool.depart(slot, seen_generation);
    }
    return 0;
}

/**
 * Pins the calling worker before it does anything else, so no unpinned worker can ever issue
 * memory traffic, then lowers its priority so the UI thread keeps the processor when it needs it.
 * Always reports ready, even after a failed or throwing pin, so start() never waits forever.
 *
 * @return The pin's Windows error; 0 when the worker is pinned.
 */
uint32_t worker_pool::impl::prepare_worker(worker_slot& slot) noexcept {
    // Stays nonzero when pin throws: the thread is then not known to be pinned.
    uint32_t pin_error = static_cast<uint32_t>(ERROR_GEN_FAILURE);
    try {
        pin_error = affinity->pin(slot.index, slot.placement.group, slot.placement.number);
    } catch (const std::exception& e) {
        request_internal_fault(e.what());
    } catch (...) {
        request_internal_fault(k_unknown_exception_detail);
    }

    // Priority only protects responsiveness, so a failure is counted and the run continues.
    uint32_t priority_error = 0;
    if (pin_error == 0 &&
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL) == FALSE) {
        priority_error = last_error_or_general_failure();
    }

    srw_exclusive hold(lock);
    slot.pin_error = pin_error;
    slot.priority_error = priority_error;
    if (ready_count.fetch_add(1, std::memory_order_relaxed) + 1 == slots.size()) {
        WakeAllConditionVariable(&ready_cv);
    }
    return pin_error;
}

/**
 * The phase loop: waits for a new generation or the exit request and runs each phase this
 * worker is active in. Exit wins over an outstanding phase, which is then skipped.
 *
 * seen_generation is the caller's, so that depart() knows which phase this worker still owes
 * if a wait throws. Only sleep_on can throw here; it does so before the generation is taken.
 *
 * Inactive workers (odd indices in an even phase) only record the generation and sleep again.
 * A worker that slept through several posts takes the latest one: every phase it was active in
 * had to wait for it, so a skipped generation is always one it had no share in.
 *
 * @param pinned False after a failed pin: such a worker never runs a task. start() shuts the
 *        pool down in that case, so no phase is posted to it in the first place.
 */
void worker_pool::impl::run_phases(const worker_slot& slot, bool pinned,
                                   uint64_t& seen_generation) {
    for (;;) {
        const phase_task* posted_task = nullptr;
        active_set posted_set = active_set::all;
        {
            srw_exclusive hold(lock);
            while (!exit_requested.load(std::memory_order_relaxed) &&
                   generation.load(std::memory_order_relaxed) == seen_generation) {
                sleep_on(work_cv, lock);
            }
            if (exit_requested.load(std::memory_order_relaxed)) {
                return;
            }
            seen_generation = generation.load(std::memory_order_relaxed);
            posted_set = phase_set;
            // The task object stays in place until the next post_phase, which cannot happen
            // before this worker has finished it, or until shutdown, which joins this thread.
            posted_task = &task;
        }
        if (pinned && is_active(posted_set, slot.index)) {
            run_task(*posted_task, slot.index);
            finish_share();
        }
    }
}

/** Runs one task, converting any C++ exception into an internal-fault stop. */
void worker_pool::impl::run_task(const phase_task& body, uint32_t worker_index) noexcept {
    try {
        body(worker_index);
    } catch (const std::exception& e) {
        request_internal_fault(e.what());
    } catch (...) {
        request_internal_fault(k_unknown_exception_detail);
    }
}

/** Counts one finished share of the posted phase; the last one wakes the owner. */
void worker_pool::impl::finish_share() noexcept {
    if (remaining.fetch_sub(1, std::memory_order_acq_rel) == 1) {
        // Passing through the lock orders this wake after the owner's check-then-sleep, which
        // runs under the lock, so the wake cannot fall between the owner's check and its sleep.
        {
            srw_exclusive hold(lock);
        }
        WakeAllConditionVariable(&done_cv);
    }
}

/**
 * Records that this worker's loop has ended on an exception and settles the share it owes, so
 * the barrier still completes: post_phase counts only workers that have not departed, and if the
 * current phase already counted this one, its share is released here.
 */
void worker_pool::impl::depart(worker_slot& slot, uint64_t seen_generation) noexcept {
    srw_exclusive hold(lock);
    slot.departed.store(true, std::memory_order_relaxed);
    if (!exit_requested.load(std::memory_order_relaxed) &&
        generation.load(std::memory_order_relaxed) != seen_generation &&
        is_active(phase_set, slot.index) &&
        remaining.fetch_sub(1, std::memory_order_acq_rel) == 1) {
        WakeAllConditionVariable(&done_cv);
    }
}

/** First writer wins inside stop_signal, so an earlier stop reason is kept. */
void worker_pool::impl::request_internal_fault(const char* detail) noexcept {
    stop->request(RQ_END_INTERNAL_FAULT, 0, detail);
}

worker_pool::worker_pool() : impl_(std::make_unique<impl>()) {}

worker_pool::~worker_pool() {
    shutdown();
}

pool_start_result worker_pool::start(std::span<const logical_processor> placements, uint32_t count,
                                     thread_affinity& affinity, stop_signal& stop) {
    impl& pool = *impl_;
    if (pool.start_called) {
        throw std::logic_error("worker_pool::start may be called only once");
    }
    if (count == 0 || count > placements.size()) {
        throw std::invalid_argument(
            "worker_pool::start: worker_count must be 1 to placements.size()");
    }

    // Everything that can throw is allocated before the first thread exists, so a throw here
    // leaves no thread running. Slots are sized once and never resized: their addresses are the
    // thread parameters. Assigned rather than resized: an atomic member makes a slot neither
    // copyable nor movable, and the assignment only takes over the new buffer.
    pool.slots = std::vector<impl::worker_slot>(count);
    pool.threads.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        pool.slots[i].pool = &pool;
        pool.slots[i].index = i;
        pool.slots[i].placement = placements[i];
    }
    pool.affinity = &affinity;
    pool.stop = &stop;
    pool.start_called = true;

    pool_start_result result{};
    for (uint32_t i = 0; i < count; ++i) {
        HANDLE thread = CreateThread(nullptr, 0, &impl::thread_entry, &pool.slots[i], 0, nullptr);
        if (thread == nullptr) {
            result.outcome = pool_start_result::status::thread_create_failed;
            result.failed_worker = i;
            result.group = placements[i].group;
            result.processor = placements[i].number;
            result.os_error = last_error_or_general_failure();
            shutdown();
            return result;
        }
        // Capacity was reserved above, so this cannot reallocate or throw.
        pool.threads.emplace_back(thread);
    }

    try {
        srw_exclusive hold(pool.lock);
        while (pool.ready_count.load(std::memory_order_relaxed) < count) {
            sleep_on(pool.ready_cv, pool.lock);
        }
    } catch (...) {
        shutdown();
        throw;
    }

    // Every start record was written under the lock before the ready count observed above, so
    // the records are complete and no worker writes them again.
    for (const impl::worker_slot& slot : pool.slots) {
        if (slot.pin_error != 0) {
            // The lowest failing index, so the report is deterministic.
            result.outcome = pool_start_result::status::affinity_failed;
            result.failed_worker = slot.index;
            result.group = slot.placement.group;
            result.processor = slot.placement.number;
            result.os_error = slot.pin_error;
            shutdown();
            return result;
        }
    }
    if (stop.stop_requested()) {
        result.outcome = pool_start_result::status::stopped;
        shutdown();
        return result;
    }
    for (const impl::worker_slot& slot : pool.slots) {
        if (slot.priority_error != 0) {
            if (result.priority_failures == 0) {
                result.first_priority_error = slot.priority_error;
            }
            ++result.priority_failures;
        }
    }
    pool.worker_count = count;
    pool.started_ok = true;
    return result;
}

void worker_pool::post_phase(phase_task task, active_set set) {
    impl& pool = *impl_;
    if (!task) {
        throw std::invalid_argument("worker_pool::post_phase: the task is empty");
    }
    if (!pool.started_ok || pool.shut_down) {
        throw std::logic_error("worker_pool::post_phase: the pool is not running");
    }
    if (pool.remaining.load(std::memory_order_acquire) != 0) {
        throw std::logic_error("worker_pool::post_phase: the previous phase is still outstanding");
    }

    {
        srw_exclusive hold(pool.lock);
        uint32_t active = 0;
        for (const impl::worker_slot& slot : pool.slots) {
            if (!slot.departed.load(std::memory_order_relaxed) && is_active(set, slot.index)) {
                ++active;
            }
        }
        pool.task = std::move(task);
        pool.phase_set = set;
        // Workers read remaining only after taking the new generation under this lock, so the
        // count is in place before any of them can decrement it.
        pool.remaining.store(active, std::memory_order_relaxed);
        pool.generation.fetch_add(1, std::memory_order_relaxed);
    }
    WakeAllConditionVariable(&pool.work_cv);
}

bool worker_pool::wait_phase(uint32_t timeout_ms) {
    impl& pool = *impl_;
    if (pool.remaining.load(std::memory_order_acquire) == 0) {
        return true;
    }
    if (timeout_ms == 0) {
        return false;
    }

    // Real time only, never the session's IClock: a fake clock must not stall the owner, which
    // ticks its observer between calls.
    using steady = std::chrono::steady_clock;
    const steady::time_point deadline = steady::now() + std::chrono::milliseconds(timeout_ms);
    uint32_t wait_error = 0;
    {
        srw_exclusive hold(pool.lock);
        for (;;) {
            if (pool.remaining.load(std::memory_order_acquire) == 0) {
                return true;
            }
            const steady::time_point now = steady::now();
            if (now >= deadline) {
                return false;
            }
            // Rounded up, so a sub-millisecond remainder does not become a zero-length wait loop.
            const auto left = std::chrono::ceil<std::chrono::milliseconds>(deadline - now);
            const DWORD wait_ms = static_cast<DWORD>(left.count());
            if (SleepConditionVariableSRW(&pool.done_cv, &pool.lock, wait_ms, 0) == FALSE) {
                // Read at once, before any other call can overwrite the thread's last error.
                const uint32_t error = last_error_or_general_failure();
                if (error != static_cast<uint32_t>(ERROR_TIMEOUT)) {
                    wait_error = error;
                    break;
                }
            }
        }
    }

    // Only a broken process fails an SRW wait other than by timing out, and the barrier can then
    // never be crossed. An ordinary false return would let the step runner retry forever without
    // a fault, so this becomes an internal-fault stop, raised with the pool lock released:
    // running tasks return within one 1 MiB block and the session's shutdown joins them
    // promptly. os_error stays 0, which the C API reserves for affinity, memory and
    // thread-creation faults, so the Windows error travels in the detail and the exception. The
    // phase stays outstanding.
    char detail[RQ_END_DETAIL_SIZE];
    std::snprintf(detail, sizeof(detail),
                  "worker_pool::wait_phase: SleepConditionVariableSRW failed (Windows error %lu)",
                  static_cast<unsigned long>(wait_error));
    // start() sets stop, and a phase can be outstanding only after start() returned ok.
    if (pool.stop != nullptr) {
        pool.request_internal_fault(detail);
    }
    throw std::system_error(static_cast<int>(wait_error), std::system_category(),
                            "worker_pool::wait_phase: SleepConditionVariableSRW failed");
}

uint32_t worker_pool::worker_count() const noexcept {
    return impl_->worker_count;
}

uint32_t worker_pool::active_count(active_set set) const noexcept {
    const uint32_t count = impl_->worker_count;
    // ceil(count / 2) without the overflow of (count + 1) / 2.
    return set == active_set::all ? count : count / 2u + count % 2u;
}

void worker_pool::shutdown() noexcept {
    impl& pool = *impl_;
    {
        srw_exclusive hold(pool.lock);
        pool.exit_requested.store(true, std::memory_order_relaxed);
    }
    WakeAllConditionVariable(&pool.work_cv);

    // One handle at a time: WaitForMultipleObjects accepts at most 64 handles, fewer than a
    // multi-group machine has workers. Only WAIT_OBJECT_0 proves the thread has exited; after
    // any other result its liveness is unknown, so nothing it may still use is freed.
    for (std::size_t i = 0; i < pool.threads.size(); ++i) {
        unique_handle& thread = pool.threads[i];
        const DWORD wait = WaitForSingleObject(thread.get(), INFINITE);
        // Read at once, before any other call can overwrite the thread's last error.
        const DWORD error = wait == WAIT_FAILED ? GetLastError() : ERROR_SUCCESS;
        if (wait != WAIT_OBJECT_0) {
            fail_unconfirmed_join(i, wait, error);
        }
        thread.reset();
    }
    pool.threads.clear();

    // Every worker has exited, so nothing reads the task any more, and a phase that workers
    // skipped on exit no longer counts as outstanding.
    pool.task = nullptr;
    pool.remaining.store(0, std::memory_order_release);
    pool.shut_down = true;
}

}  // namespace rq::engine
