/**
 * @file test_worker_pool.cpp
 * @brief Worker pool tests: one pinned, below-normal-priority worker per logical processor across
 *        every processor group, Half mode, the affinity seam and its failure, stop and shutdown.
 *
 * - WorkerPoolPlacement: enumerate_logical_processors() lists every active logical processor of
 *   every group, group-major.
 * - WorkerPoolPinning: with every placement, each worker pins itself on its own thread, then runs
 *   its task on exactly its placement's processor, with only that processor in its group
 *   affinity, at THREAD_PRIORITY_BELOW_NORMAL.
 * - WorkerPoolHalfMode: an active_set::even phase runs exactly the even-indexed workers, the odd
 *   partners sleep through it and run the next all-thread phase.
 * - WorkerPoolAffinityFailure: a pin failure injected through the affinity seam ends start() with
 *   affinity_failed, the failing worker's placement and its Windows error, after which no phase
 *   can run and every worker thread has been joined.
 * - WorkerPoolStop: phases posted after a stop, and phases running when one arrives, end within
 *   the 2 s stop bound; start() reports a pending stop; shutdown() is idempotent and closes every
 *   thread handle.
 * - WorkerPoolContract: argument and state checks, and task exceptions contained as an
 *   internal-fault stop without breaking the phase barrier.
 * - WorkerPoolSession: a session whose affinity fails one worker ends RQ_END_AFFINITY_FAILED with
 *   the Windows error and the placement in its detail, raises neither Prepared nor a step gate,
 *   issues no memory traffic and releases the memory it allocated in preparation.
 *
 * Nothing here needs elevation. Tasks never spin: blocking tasks wait on the stop signal, so a
 * test occupying every logical processor leaves them free. Every pool and session is torn down
 * by RAII, so a failed assertion never leaves a worker running into the next test.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/runner/worker_pool.h"
#include "core/session.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

/** failing_affinity::fail_worker value under which every pin goes to the production affinity. */
constexpr std::uint32_t k_no_failure = UINT32_MAX;

/** The Windows error injected into a failing pin: ERROR_INVALID_PARAMETER. */
constexpr std::uint32_t k_injected_error = static_cast<std::uint32_t>(ERROR_INVALID_PARAMETER);
static_assert(k_injected_error == 87U, "the injected pin error is ERROR_INVALID_PARAMETER (87)");

/**
 * Bound on any one phase. Generous because below-normal-priority workers on a loaded host can
 * wait several seconds for the balance-set manager's starvation boost; a hang still fails.
 */
constexpr std::chrono::milliseconds k_phase_timeout{10'000};

/** Slice of each wait_phase call within k_phase_timeout. */
constexpr std::uint32_t k_wait_slice_ms = 25U;

/** Cap on a blocking task's wait for the stop signal, so no task can outlive its test. */
constexpr std::uint64_t k_task_wait_cap_ms = 10'000U;

/** The product's stop bound: a stop request to Completed within 2 s. */
constexpr std::chrono::milliseconds k_stop_bound{2'000};

/** Handles the process may gain for reasons unrelated to the pool between two counts. */
constexpr DWORD k_handle_tolerance = 4U;

/** Workers of the tests that exercise the pool's contract rather than the machine's topology. */
constexpr std::uint32_t k_small_pool = 4U;

constexpr std::uint64_t k_mib = std::uint64_t{1} << 20U;

/** Session test: region, shrink floor, step budget and the bound on the whole run. */
constexpr std::uint64_t k_session_region_bytes = 128U * k_mib;
constexpr std::uint64_t k_session_min_region_bytes = 64U * k_mib;
constexpr std::uint32_t k_session_step_budget_ms = 1'000U;
constexpr std::chrono::seconds k_session_timeout{60};

constexpr std::uint64_t k_seed = 0x5EED0F00DCAFEBABULL;

/* ---------------------------------------------------------------------------------------------
 * Descriptions for failure messages
 * ------------------------------------------------------------------------------------------- */

std::string describe(unsigned group, unsigned number) {
    return "group " + std::to_string(group) + ", processor " + std::to_string(number);
}

std::string describe(const logical_processor& processor) {
    return describe(processor.group, processor.number);
}

std::string describe(const PROCESSOR_NUMBER& processor) {
    return describe(processor.Group, processor.Number);
}

const char* outcome_name(pool_start_result::status outcome) {
    switch (outcome) {
    case pool_start_result::status::ok:
        return "ok";
    case pool_start_result::status::affinity_failed:
        return "affinity_failed";
    case pool_start_result::status::thread_create_failed:
        return "thread_create_failed";
    case pool_start_result::status::stopped:
        return "stopped";
    }
    return "unknown";
}

std::string describe(const pool_start_result& result) {
    return std::string("start outcome ") + outcome_name(result.outcome) + " (failed worker " +
           std::to_string(result.failed_worker) + ", " + describe(result.group, result.processor) +
           ", Windows error " + std::to_string(result.os_error) + ", priority failures " +
           std::to_string(result.priority_failures) + ", first priority error " +
           std::to_string(result.first_priority_error) + ")";
}

/* ---------------------------------------------------------------------------------------------
 * Substitutes and helpers
 * ------------------------------------------------------------------------------------------- */

/**
 * The affinity seam's substitute: pins through the production win32_thread_affinity, except
 * that fail_worker's pin returns fail_error without pinning, as a failing SetThreadGroupAffinity
 * would. Counts each worker's pin calls and records the thread each call ran on, which shows
 * that every worker pins itself on its own thread.
 *
 * Thread-safe: workers call pin() concurrently, each touching only its own slots. Read the
 * records once start() has returned, whose wait for every worker orders them before the read.
 */
class failing_affinity final : public IThreadAffinity {
public:
    failing_affinity(std::uint32_t worker_count, std::uint32_t fail_worker,
                     std::uint32_t fail_error)
        : fail_worker_(fail_worker), fail_error_(fail_error), calls_(worker_count),
          thread_ids_(worker_count) {}

    std::uint32_t pin(std::uint32_t worker_index, std::uint16_t group,
                      std::uint8_t processor) override {
        if (worker_index < calls_.size()) {
            calls_[worker_index].fetch_add(1U, std::memory_order_relaxed);
            thread_ids_[worker_index].store(GetCurrentThreadId(), std::memory_order_relaxed);
        } else {
            out_of_range_calls_.fetch_add(1U, std::memory_order_relaxed);
        }
        if (worker_index == fail_worker_) {
            return fail_error_;
        }
        return production_.pin(worker_index, group, processor);
    }

    /** Pin calls made for worker. */
    std::uint32_t calls(std::uint32_t worker) const {
        return calls_.at(worker).load(std::memory_order_relaxed);
    }

    /** Thread of worker's last pin call; 0 when it was never pinned. */
    DWORD thread_id(std::uint32_t worker) const {
        return thread_ids_.at(worker).load(std::memory_order_relaxed);
    }

    /** Pin calls whose worker index was at or above the worker count given at construction. */
    std::uint32_t out_of_range_calls() const {
        return out_of_range_calls_.load(std::memory_order_relaxed);
    }

private:
    win32_thread_affinity production_;
    std::uint32_t fail_worker_;
    std::uint32_t fail_error_;
    std::vector<std::atomic<std::uint32_t>> calls_;
    std::vector<std::atomic<DWORD>> thread_ids_;
    std::atomic<std::uint32_t> out_of_range_calls_{0U};
};

/**
 * Requests a stop when its scope ends, so a test that bails out while a blocking task runs
 * still lets that task return before the pool's destructor joins it. Declare it after the pool,
 * so it runs first; it is the owner's "request a stop before tearing down mid-phase" duty.
 */
class stop_on_exit {
public:
    explicit stop_on_exit(stop_signal& stop) noexcept : stop_(stop) {}
    ~stop_on_exit() { stop_.request(RQ_END_USER_STOP, 0U, "test scope ended"); }

    stop_on_exit(const stop_on_exit&) = delete;
    stop_on_exit& operator=(const stop_on_exit&) = delete;

private:
    stop_signal& stop_;
};

/** Every active logical processor, group-major: the placements the session itself uses. */
std::vector<logical_processor> machine_placements() {
    return enumerate_logical_processors();
}

/** Waits for the posted phase in short slices, failing rather than blocking on a hang. */
::testing::AssertionResult phase_completes(worker_pool& pool) {
    const auto deadline = std::chrono::steady_clock::now() + k_phase_timeout;
    for (;;) {
        if (pool.wait_phase(k_wait_slice_ms)) {
            return ::testing::AssertionSuccess();
        }
        if (std::chrono::steady_clock::now() >= deadline) {
            return ::testing::AssertionFailure() << "the posted phase did not complete within "
                                                 << k_phase_timeout.count() << " ms";
        }
    }
}

/** The process's open handle count, which falls back once the pool closes its threads. */
DWORD process_handle_count() {
    DWORD count = 0;
    if (GetProcessHandleCount(GetCurrentProcess(), &count) == FALSE) {
        ADD_FAILURE() << "GetProcessHandleCount failed, Windows error " << GetLastError();
    }
    return count;
}

/**
 * Asserts that no handle leaked since baseline. Only growth is checked: the count may also fall
 * when unrelated system threads of the process exit, which says nothing about the pool.
 */
::testing::AssertionResult no_handle_leak(DWORD baseline, const char* when) {
    const DWORD now = process_handle_count();
    if (now <= baseline + k_handle_tolerance) {
        return ::testing::AssertionSuccess();
    }
    return ::testing::AssertionFailure()
           << when << ": " << now << " handles are open against a baseline of " << baseline
           << " (tolerance " << k_handle_tolerance << "), so thread handles leaked";
}

/** What one worker saw of its own thread while running a phase task. */
struct worker_observation {
    std::atomic<std::uint32_t> runs{0U};
    DWORD thread_id = 0;
    PROCESSOR_NUMBER current{};
    GROUP_AFFINITY affinity{};
    bool affinity_read = false;
    DWORD affinity_error = 0;
    int priority = THREAD_PRIORITY_ERROR_RETURN;
};

/** Records the calling worker's thread, processor, group affinity and priority. */
void observe_current_thread(worker_observation& seen) noexcept {
    seen.thread_id = GetCurrentThreadId();
    GetCurrentProcessorNumberEx(&seen.current);
    seen.affinity_read = GetThreadGroupAffinity(GetCurrentThread(), &seen.affinity) != FALSE;
    seen.affinity_error = seen.affinity_read ? 0 : GetLastError();
    seen.priority = GetThreadPriority(GetCurrentThread());
    seen.runs.fetch_add(1U, std::memory_order_relaxed);
}

/* ---------------------------------------------------------------------------------------------
 * Session callbacks
 * ------------------------------------------------------------------------------------------- */

/**
 * Every callback a session raised, counted under one lock. Callbacks run on the coordinator,
 * the test thread waits for Released, and the lock orders the two.
 */
struct session_events {
    std::mutex lock;
    std::condition_variable changed;
    std::uint32_t prepared = 0;
    std::uint32_t step_starting = 0;
    std::uint32_t step_finished = 0;
    std::uint32_t progress = 0;
    std::uint32_t error_batches = 0;
    std::uint32_t completed = 0;
    std::uint32_t released = 0;
    /** The header delivered with Completed; its step_results pointer is never dereferenced. */
    rq_run_result result{};
    std::uint64_t released_bytes = 0;
};

session_events& events_of(void* user_data) noexcept {
    return *static_cast<session_events*>(user_data);
}

void RQ_CALL record_prepared(void* user_data, const rq_prepared*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard hold(events.lock);
    ++events.prepared;
}

/**
 * Counts the gate. It returns ABORT because no gate may be raised after an affinity failure: if
 * one is, the step still starts no traffic, and the count fails the test.
 */
std::uint32_t RQ_CALL record_step_starting(void* user_data, const rq_step_start*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard hold(events.lock);
    ++events.step_starting;
    return RQ_ACTION_ABORT;
}

void RQ_CALL record_step_finished(void* user_data, const rq_step_result*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard hold(events.lock);
    ++events.step_finished;
}

void RQ_CALL record_progress(void* user_data, const rq_progress*) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard hold(events.lock);
    ++events.progress;
}

void RQ_CALL record_errors(void* user_data, const rq_error_record*, std::uint32_t,
                           std::uint64_t) noexcept {
    session_events& events = events_of(user_data);
    const std::lock_guard hold(events.lock);
    ++events.error_batches;
}

void RQ_CALL record_completed(void* user_data, const rq_run_result* result) noexcept {
    session_events& events = events_of(user_data);
    {
        const std::lock_guard hold(events.lock);
        ++events.completed;
        events.result = *result;
    }
    events.changed.notify_all();
}

void RQ_CALL record_released(void* user_data, std::uint64_t released_bytes,
                             std::uint64_t) noexcept {
    session_events& events = events_of(user_data);
    {
        const std::lock_guard hold(events.lock);
        ++events.released;
        events.released_bytes = released_bytes;
    }
    events.changed.notify_all();
}

rq_callbacks recording_callbacks(session_events& events) {
    rq_callbacks callbacks{};
    callbacks.struct_size = static_cast<std::uint32_t>(sizeof(rq_callbacks));
    callbacks.user_data = &events;
    callbacks.on_prepared = &record_prepared;
    callbacks.on_step_starting = &record_step_starting;
    callbacks.on_step_finished = &record_step_finished;
    callbacks.on_progress = &record_progress;
    callbacks.on_errors = &record_errors;
    callbacks.on_completed = &record_completed;
    callbacks.on_released = &record_released;
    return callbacks;
}

/** Destroys its session on every exit path, so a failed assertion never leaks memory or threads. */
class session_guard {
public:
    session_guard() noexcept = default;
    ~session_guard() {
        if (handle_ != nullptr && session_destroy(handle_) != RQ_OK) {
            ADD_FAILURE() << "session_destroy failed while unwinding the test";
        }
    }

    session_guard(const session_guard&) = delete;
    session_guard& operator=(const session_guard&) = delete;

    rq_session_handle* out() noexcept { return &handle_; }
    rq_session_handle get() const noexcept { return handle_; }

    /** Destroys the session now; the guard then owns nothing. */
    rq_result destroy() { return session_destroy(std::exchange(handle_, nullptr)); }

private:
    rq_session_handle handle_ = nullptr;
};

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolPlacement
 * ------------------------------------------------------------------------------------------- */

TEST(WorkerPoolPlacement, ListsEveryActiveProcessorOfEveryGroupInGroupMajorOrder) {
    const std::vector<logical_processor> placements = machine_placements();
    ASSERT_FALSE(placements.empty());
    EXPECT_EQ(placements.size(),
              static_cast<std::size_t>(GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)));

    const WORD groups = GetActiveProcessorGroupCount();
    std::vector<std::size_t> per_group(groups, 0U);
    for (std::size_t i = 0; i < placements.size(); ++i) {
        const logical_processor& placement = placements[i];
        ASSERT_LT(placement.group, groups) << "entry " << i << ": " << describe(placement);
        EXPECT_LT(static_cast<unsigned>(placement.number), 64U)
            << "entry " << i << ": " << describe(placement);
        ++per_group[placement.group];
        if (i > 0U) {
            const logical_processor& previous = placements[i - 1U];
            const bool group_major =
                previous.group < placement.group ||
                (previous.group == placement.group && previous.number < placement.number);
            EXPECT_TRUE(group_major)
                << "entry " << i << " (" << describe(placement) << ") follows entry " << i - 1U
                << " (" << describe(previous) << ")";
        }
    }
    for (WORD group = 0; group < groups; ++group) {
        EXPECT_EQ(per_group[group], static_cast<std::size_t>(GetActiveProcessorCount(group)))
            << "processors listed for group " << group;
    }
    RecordProperty("processor_groups", static_cast<int>(groups));
    RecordProperty("logical_processors", static_cast<int>(placements.size()));
}

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolPinning
 * ------------------------------------------------------------------------------------------- */

TEST(WorkerPoolPinning, EveryWorkerRunsOnlyOnItsOwnProcessorAtBelowNormalPriority) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto count = static_cast<std::uint32_t>(placements.size());
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    std::vector<worker_observation> seen(count);
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);
    EXPECT_EQ(started.priority_failures, 0U) << describe(started);
    EXPECT_EQ(pool.worker_count(), count);
    EXPECT_EQ(pool.active_count(active_set::all), count);
    EXPECT_EQ(affinity.out_of_range_calls(), 0U);

    pool.post_phase([&seen](std::uint32_t worker) { observe_current_thread(seen[worker]); },
                    active_set::all);
    ASSERT_TRUE(phase_completes(pool));
    EXPECT_FALSE(stop.stop_requested()) << "a task raised a stop: " << stop.snapshot().detail;

    const DWORD test_thread = GetCurrentThreadId();
    std::set<DWORD> threads;
    std::set<unsigned> groups_run;
    for (std::uint32_t i = 0; i < count; ++i) {
        const worker_observation& worker = seen[i];
        const logical_processor& expected = placements[i];
        const std::uint32_t runs = worker.runs.load(std::memory_order_relaxed);
        if (runs != 1U) {
            ADD_FAILURE() << "worker " << i << " (" << describe(expected) << ") ran its task "
                          << runs << " times instead of once";
            continue;
        }
        EXPECT_TRUE(worker.current.Group == expected.group &&
                    worker.current.Number == expected.number)
            << "worker " << i << ": expected " << describe(expected) << ", ran on "
            << describe(worker.current);
        if (worker.affinity_read) {
            EXPECT_TRUE(worker.affinity.Group == expected.group &&
                        worker.affinity.Mask == (KAFFINITY{1} << expected.number))
                << "worker " << i << ": expected affinity " << describe(expected)
                << " alone, got group " << worker.affinity.Group << " mask 0x" << std::hex
                << worker.affinity.Mask << std::dec;
        } else {
            ADD_FAILURE() << "worker " << i << ": GetThreadGroupAffinity failed, Windows error "
                          << worker.affinity_error;
        }
        EXPECT_EQ(worker.priority, THREAD_PRIORITY_BELOW_NORMAL) << "worker " << i;
        EXPECT_EQ(affinity.calls(i), 1U) << "pin calls for worker " << i;
        EXPECT_EQ(affinity.thread_id(i), worker.thread_id)
            << "worker " << i << " was pinned on another thread than the one running its task";
        EXPECT_NE(worker.thread_id, test_thread) << "worker " << i << " ran on the test thread";
        threads.insert(worker.thread_id);
        groups_run.insert(worker.current.Group);
    }
    EXPECT_EQ(threads.size(), static_cast<std::size_t>(count)) << "workers sharing a thread";

    const WORD groups = GetActiveProcessorGroupCount();
    EXPECT_EQ(groups_run.size(), static_cast<std::size_t>(groups))
        << "workers ran in " << groups_run.size() << " of " << groups << " processor groups";
    RecordProperty("processor_groups", static_cast<int>(groups));
    RecordProperty("workers", static_cast<int>(count));
    if (groups == 1U) {
        RecordProperty("group_coverage",
                       "single processor group: pinning across groups is not exercised here");
    }
}

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolHalfMode
 * ------------------------------------------------------------------------------------------- */

/** Fixed Half-mode worker counts: a lone even worker, one pair, a pair plus an unpaired worker. */
constexpr std::uint32_t k_half_mode_fixed_counts[] = {1U, 2U, 3U};

/**
 * The Half-mode cases to register: each fixed count the machine has processors for, then 0 for
 * every placement. A count the machine lacks is not registered, because processor count is no
 * reason for a worker-pool test to skip. gtest calls this inside InitGoogleTest, so it reads the
 * non-throwing active-processor count, which WorkerPoolPlacement asserts equals the placements.
 * If that read fails (0), every fixed count is registered and the case's own check reports it.
 */
std::vector<std::uint32_t> half_mode_worker_counts() {
    const auto active = static_cast<std::uint32_t>(GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
    std::vector<std::uint32_t> counts;
    for (const std::uint32_t count : k_half_mode_fixed_counts) {
        if (active == 0U || count <= active) {
            counts.push_back(count);
        }
    }
    counts.push_back(0U);
    return counts;
}

/**
 * Worker count of a Half-mode case; 0 stands for every placement of the machine. Only counts the
 * machine has are registered.
 */
class WorkerPoolHalfMode : public ::testing::TestWithParam<std::uint32_t> {};

TEST_P(WorkerPoolHalfMode, EvenPhaseRunsExactlyTheEvenWorkersAndTheOddPartnersRunTheNextPhase) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto available = static_cast<std::uint32_t>(placements.size());
    const std::uint32_t count = GetParam() == 0U ? available : GetParam();
    ASSERT_LE(count, available) << "registered for " << count << " workers but only " << available
                                << " placements exist";
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    std::vector<std::atomic<std::uint32_t>> even_runs(count);
    std::vector<std::atomic<std::uint32_t>> all_runs(count);
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);
    EXPECT_EQ(pool.active_count(active_set::all), count);
    EXPECT_EQ(pool.active_count(active_set::even), count / 2U + count % 2U)
        << "ceil(" << count << " / 2) workers run a Half step";

    pool.post_phase(
        [&even_runs](std::uint32_t worker) {
            even_runs[worker].fetch_add(1U, std::memory_order_relaxed);
        },
        active_set::even);
    ASSERT_TRUE(phase_completes(pool));

    std::uint32_t ran = 0;
    for (std::uint32_t i = 0; i < count; ++i) {
        const std::uint32_t runs = even_runs[i].load(std::memory_order_relaxed);
        if (i % 2U == 0U) {
            EXPECT_EQ(runs, 1U) << "even worker " << i << " must run the Half phase once";
        } else {
            EXPECT_EQ(runs, 0U) << "odd worker " << i << " must sleep while worker " << i - 1U
                                << " covers its slice";
        }
        ran += runs;
    }
    EXPECT_EQ(ran, pool.active_count(active_set::even));
    if (count % 2U == 1U) {
        EXPECT_EQ(even_runs[count - 1U].load(std::memory_order_relaxed), 1U)
            << "the last of " << count << " workers is even with no partner and runs its own slice";
    }

    // The sleeping partners are idle, not lost: the next all-thread phase runs every worker once
    // and does not rerun the Half task.
    pool.post_phase(
        [&all_runs](std::uint32_t worker) {
            all_runs[worker].fetch_add(1U, std::memory_order_relaxed);
        },
        active_set::all);
    ASSERT_TRUE(phase_completes(pool));
    for (std::uint32_t i = 0; i < count; ++i) {
        EXPECT_EQ(all_runs[i].load(std::memory_order_relaxed), 1U) << "worker " << i;
        EXPECT_EQ(even_runs[i].load(std::memory_order_relaxed), i % 2U == 0U ? 1U : 0U)
            << "worker " << i << " reran the Half task";
    }
    EXPECT_FALSE(stop.stop_requested());
}

INSTANTIATE_TEST_SUITE_P(WorkerPoolSizes, WorkerPoolHalfMode,
                         ::testing::ValuesIn(half_mode_worker_counts()),
                         [](const ::testing::TestParamInfo<std::uint32_t>& info) {
                             return info.param == 0U
                                        ? std::string("EveryPlacement")
                                        : std::to_string(info.param) + std::string("Workers");
                         });

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolAffinityFailure
 * ------------------------------------------------------------------------------------------- */

/** Which worker's pin fails. */
enum class failing_worker : std::uint32_t {
    /** min(1, n - 1): the second worker, or the only one. */
    second,
    /** n - 1: on a multi-group machine a processor outside group 0. */
    last,
};

class WorkerPoolAffinityFailure : public ::testing::TestWithParam<failing_worker> {};

TEST_P(WorkerPoolAffinityFailure, StartReportsTheFailingPlacementAndErrorAndNoPhaseCanRun) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto count = static_cast<std::uint32_t>(placements.size());
    const std::uint32_t fail =
        GetParam() == failing_worker::second ? std::min(1U, count - 1U) : count - 1U;
    failing_affinity affinity(count, fail, k_injected_error);
    stop_signal stop;
    std::atomic<std::uint32_t> task_runs{0U};
    const DWORD handles_before = process_handle_count();
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::affinity_failed) << describe(started);
    EXPECT_EQ(started.failed_worker, fail) << describe(started);
    EXPECT_TRUE(started.group == placements[fail].group &&
                started.processor == placements[fail].number)
        << "expected worker " << fail << "'s placement " << describe(placements[fail])
        << ", reported " << describe(started.group, started.processor);
    EXPECT_EQ(started.os_error, k_injected_error);
    EXPECT_EQ(pool.worker_count(), 0U);
    EXPECT_EQ(pool.active_count(active_set::all), 0U);
    EXPECT_EQ(pool.active_count(active_set::even), 0U);
    EXPECT_FALSE(stop.stop_requested()) << "the pool reports the failure; the session stops";

    // Every worker reported, the failing one included, each from its own thread.
    const DWORD test_thread = GetCurrentThreadId();
    std::set<DWORD> threads;
    for (std::uint32_t i = 0; i < count; ++i) {
        EXPECT_EQ(affinity.calls(i), 1U) << "pin calls for worker " << i;
        EXPECT_NE(affinity.thread_id(i), 0U) << "worker " << i << " never pinned";
        EXPECT_NE(affinity.thread_id(i), test_thread) << "worker " << i;
        threads.insert(affinity.thread_id(i));
    }
    EXPECT_EQ(threads.size(), static_cast<std::size_t>(count)) << "workers sharing a thread";
    EXPECT_EQ(affinity.out_of_range_calls(), 0U);

    // start() has already shut the pool down: every worker joined and its handle closed.
    EXPECT_TRUE(no_handle_leak(handles_before, "after the failed start"));

    // An unpinned run never produces traffic: no phase can be posted.
    EXPECT_THROW(
        pool.post_phase(
            [&task_runs](std::uint32_t) { task_runs.fetch_add(1U, std::memory_order_relaxed); },
            active_set::all),
        std::logic_error);
    EXPECT_TRUE(pool.wait_phase(0U));
    pool.shutdown();
    pool.shutdown();
    EXPECT_EQ(task_runs.load(std::memory_order_relaxed), 0U);
    EXPECT_TRUE(no_handle_leak(handles_before, "after shutdown"));
}

INSTANTIATE_TEST_SUITE_P(WorkerPoolFailingWorker, WorkerPoolAffinityFailure,
                         ::testing::Values(failing_worker::second, failing_worker::last),
                         [](const ::testing::TestParamInfo<failing_worker>& info) {
                             return info.param == failing_worker::second
                                        ? std::string("SecondWorker")
                                        : std::string("LastWorker");
                         });

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolStop
 * ------------------------------------------------------------------------------------------- */

TEST(WorkerPoolStop, PhasePostedAfterAStopReturnsPromptlyAndShutdownIsIdempotent) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto count = static_cast<std::uint32_t>(placements.size());
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    std::atomic<std::uint32_t> runs{0U};
    std::atomic<std::uint32_t> saw_stop{0U};
    const DWORD handles_before = process_handle_count();
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);
    ASSERT_TRUE(stop.request(RQ_END_USER_STOP, 0U, "user stop before the phase"));

    // Tasks run even with a stop pending and decide how little to do; these do nothing.
    const auto posted_at = std::chrono::steady_clock::now();
    pool.post_phase(
        [&stop, &runs, &saw_stop](std::uint32_t) {
            runs.fetch_add(1U, std::memory_order_relaxed);
            if (stop.stop_requested()) {
                saw_stop.fetch_add(1U, std::memory_order_relaxed);
            }
        },
        active_set::all);
    ASSERT_TRUE(phase_completes(pool));
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - posted_at);
    EXPECT_LT(elapsed, k_stop_bound) << "the phase took " << elapsed.count() << " ms";
    EXPECT_EQ(runs.load(std::memory_order_relaxed), count);
    EXPECT_EQ(saw_stop.load(std::memory_order_relaxed), count);
    EXPECT_EQ(stop.pending_reason(), RQ_END_USER_STOP) << "the pool must not replace the reason";

    pool.shutdown();
    EXPECT_TRUE(no_handle_leak(handles_before, "after the first shutdown"));
    pool.shutdown();
    EXPECT_TRUE(no_handle_leak(handles_before, "after the second shutdown"));
    EXPECT_EQ(pool.worker_count(), count) << "shutdown leaves worker_count unchanged";
    EXPECT_TRUE(pool.wait_phase(0U));
    EXPECT_THROW(pool.post_phase([](std::uint32_t) {}, active_set::all), std::logic_error);
}

TEST(WorkerPoolStop, RunningPhaseEndsWithinTheStopBoundOnceAStopIsRequested) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto count = static_cast<std::uint32_t>(placements.size());
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    qpc_clock clock;

    // Entry is counted under a lock so the last worker in can wake the test thread, which then
    // requests the stop knowing every task is blocked in its wait. The shared counter is atomic,
    // yet still changed under the lock so the wait cannot miss the last entrant's notification.
    // The gate is declared before the pool because the tasks that touch it run until the pool's
    // destructor joins them.
    struct entry_gate {
        std::mutex lock;
        std::condition_variable all_entered;
        std::atomic<std::uint32_t> entered{0U};
    };
    entry_gate entry;
    std::atomic<std::uint32_t> woken_by_stop{0U};
    worker_pool pool;
    const stop_on_exit release_tasks(stop);

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);

    // Each task blocks as a hold does, on the stop signal rather than in a sleep.
    pool.post_phase(
        [&stop, &clock, &entry, count, &woken_by_stop](std::uint32_t) {
            bool last = false;
            {
                const std::lock_guard hold(entry.lock);
                last = entry.entered.fetch_add(1U, std::memory_order_relaxed) + 1U == count;
            }
            if (last) {
                entry.all_entered.notify_all();
            }
            if (clock.wait_for_stop(stop, k_task_wait_cap_ms)) {
                woken_by_stop.fetch_add(1U, std::memory_order_relaxed);
            }
        },
        active_set::all);
    {
        std::unique_lock hold(entry.lock);
        ASSERT_TRUE(entry.all_entered.wait_for(
            hold, k_phase_timeout,
            [&entry, count] { return entry.entered.load(std::memory_order_relaxed) == count; }))
            << entry.entered.load(std::memory_order_relaxed) << " of " << count
            << " workers began the phase within " << k_phase_timeout.count() << " ms";
    }
    EXPECT_FALSE(pool.wait_phase(0U)) << "the phase must still be outstanding";

    const auto requested_at = std::chrono::steady_clock::now();
    ASSERT_TRUE(stop.request(RQ_END_USER_STOP, 0U, "user stop during the phase"));
    ASSERT_TRUE(phase_completes(pool));
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - requested_at);
    EXPECT_LT(elapsed, k_stop_bound)
        << "the phase ended " << elapsed.count() << " ms after the stop request";
    EXPECT_EQ(woken_by_stop.load(std::memory_order_relaxed), count);
}

TEST(WorkerPoolStop, StartReportsAStopRequestedBeforeEveryWorkerReported) {
    const std::vector<logical_processor> placements = machine_placements();
    const std::uint32_t count =
        std::min(k_small_pool, static_cast<std::uint32_t>(placements.size()));
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    ASSERT_TRUE(stop.request(RQ_END_USER_STOP, 0U, "user stop before start"));
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    EXPECT_EQ(started.outcome, pool_start_result::status::stopped) << describe(started);
    EXPECT_EQ(started.os_error, 0U);
    EXPECT_EQ(pool.worker_count(), 0U);
    for (std::uint32_t i = 0; i < count; ++i) {
        EXPECT_EQ(affinity.calls(i), 1U) << "pin calls for worker " << i;
    }
    EXPECT_THROW(pool.post_phase([](std::uint32_t) {}, active_set::all), std::logic_error);
}

TEST(WorkerPoolStop, AffinityFailureTakesPrecedenceOverAPendingStop) {
    const std::vector<logical_processor> placements = machine_placements();
    const std::uint32_t count =
        std::min(k_small_pool, static_cast<std::uint32_t>(placements.size()));
    const std::uint32_t fail = count - 1U;
    failing_affinity affinity(count, fail, k_injected_error);
    stop_signal stop;
    ASSERT_TRUE(stop.request(RQ_END_USER_STOP, 0U, "user stop before start"));
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    EXPECT_EQ(started.outcome, pool_start_result::status::affinity_failed) << describe(started);
    EXPECT_EQ(started.failed_worker, fail);
    EXPECT_EQ(started.os_error, k_injected_error);
    EXPECT_EQ(pool.worker_count(), 0U);
}

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolContract
 * ------------------------------------------------------------------------------------------- */

TEST(WorkerPoolContract, StartRejectsInvalidWorkerCountsAndASecondStart) {
    const std::vector<logical_processor> placements = machine_placements();
    const auto available = static_cast<std::uint32_t>(placements.size());
    const std::uint32_t count = std::min(k_small_pool, available);
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    worker_pool pool;

    EXPECT_THROW(pool.start(placements, 0U, affinity, stop), std::invalid_argument);
    EXPECT_THROW(pool.start(placements, available + 1U, affinity, stop), std::invalid_argument);
    for (std::uint32_t i = 0; i < count; ++i) {
        EXPECT_EQ(affinity.calls(i), 0U) << "a rejected start created worker " << i;
    }

    // A rejected call is not the pool's one start.
    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);
    EXPECT_THROW(pool.start(placements, count, affinity, stop), std::logic_error);
    EXPECT_EQ(pool.worker_count(), count);
}

TEST(WorkerPoolContract, PostPhaseRejectsAnEmptyTaskAndAPhaseWhileOneIsOutstanding) {
    const std::vector<logical_processor> placements = machine_placements();
    const std::uint32_t count =
        std::min(k_small_pool, static_cast<std::uint32_t>(placements.size()));
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    qpc_clock clock;
    std::atomic<std::uint32_t> woken_by_stop{0U};
    worker_pool pool;
    const stop_on_exit release_tasks(stop);

    EXPECT_THROW(pool.post_phase([](std::uint32_t) {}, active_set::all), std::logic_error)
        << "posting before start";
    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);

    EXPECT_THROW(pool.post_phase(phase_task{}, active_set::all), std::invalid_argument);
    EXPECT_TRUE(pool.wait_phase(0U)) << "a rejected post leaves no phase outstanding";

    pool.post_phase(
        [&stop, &clock, &woken_by_stop](std::uint32_t) {
            if (clock.wait_for_stop(stop, k_task_wait_cap_ms)) {
                woken_by_stop.fetch_add(1U, std::memory_order_relaxed);
            }
        },
        active_set::all);
    EXPECT_FALSE(pool.wait_phase(0U)) << "the blocking phase must be outstanding";
    EXPECT_THROW(pool.post_phase([](std::uint32_t) {}, active_set::all), std::logic_error);

    ASSERT_TRUE(stop.request(RQ_END_USER_STOP, 0U, "user stop"));
    ASSERT_TRUE(phase_completes(pool));
    EXPECT_EQ(woken_by_stop.load(std::memory_order_relaxed), count);
}

/**
 * Posts a phase whose worker 0 throws, then a phase on every worker. The exception must become an
 * internal-fault stop with the given detail, the barrier must still complete, and the worker
 * that threw must keep serving phases.
 */
void expect_task_exception_contained(bool standard_exception, const char* expected_detail) {
    const std::vector<logical_processor> placements = machine_placements();
    const std::uint32_t count =
        std::min(k_small_pool, static_cast<std::uint32_t>(placements.size()));
    failing_affinity affinity(count, k_no_failure, 0U);
    stop_signal stop;
    std::vector<std::atomic<std::uint32_t>> first_runs(count);
    std::vector<std::atomic<std::uint32_t>> second_runs(count);
    worker_pool pool;

    const pool_start_result started = pool.start(placements, count, affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok) << describe(started);

    pool.post_phase(
        [&first_runs, standard_exception](std::uint32_t worker) {
            first_runs[worker].fetch_add(1U, std::memory_order_relaxed);
            if (worker != 0U) {
                return;
            }
            if (standard_exception) {
                throw std::runtime_error("injected task failure");
            }
            throw 42;
        },
        active_set::all);
    ASSERT_TRUE(phase_completes(pool)) << "a throwing task must not break the phase barrier";

    const stop_signal::end_info end = stop.snapshot();
    EXPECT_EQ(end.reason, RQ_END_INTERNAL_FAULT);
    EXPECT_EQ(end.os_error, 0U);
    EXPECT_STREQ(end.detail, expected_detail);

    pool.post_phase(
        [&second_runs](std::uint32_t worker) {
            second_runs[worker].fetch_add(1U, std::memory_order_relaxed);
        },
        active_set::all);
    ASSERT_TRUE(phase_completes(pool));
    for (std::uint32_t i = 0; i < count; ++i) {
        EXPECT_EQ(first_runs[i].load(std::memory_order_relaxed), 1U) << "worker " << i;
        EXPECT_EQ(second_runs[i].load(std::memory_order_relaxed), 1U)
            << "worker " << i << " stopped serving phases";
    }
}

TEST(WorkerPoolContract, StandardExceptionInATaskBecomesAnInternalFaultStop) {
    expect_task_exception_contained(true, "injected task failure");
}

TEST(WorkerPoolContract, OtherExceptionInATaskBecomesAnInternalFaultStop) {
    expect_task_exception_contained(false, "unknown C++ exception in worker");
}

/* ---------------------------------------------------------------------------------------------
 * WorkerPoolSession
 * ------------------------------------------------------------------------------------------- */

TEST(WorkerPoolSession, AffinityFailureEndsTheRunWithoutTrafficAndReleasesItsMemory) {
    const std::vector<logical_processor> placements = machine_placements();
    // The session pins worker i to placement i, so the failing worker's placement is known.
    const std::uint32_t workers = placements.size() >= 2U ? 2U : 1U;
    const std::uint32_t fail = workers - 1U;
    const std::uint32_t sessions_before = live_session_count();

    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), "Affinity failure probe");
    step.pattern = RQ_PATTERN_RANDOM;
    step.access_order = RQ_ORDER_SEQUENTIAL_ASCENDING;
    step.thread_mode = RQ_THREADS_ALL;
    step.hold_ms = 0U;
    step.time_budget_ms = k_session_step_budget_ms;
    step.coverage_budget_bp = 100U;

    rq_session_config config{};
    config.struct_size = static_cast<std::uint32_t>(sizeof(rq_session_config));
    config.step_def_size = static_cast<std::uint32_t>(sizeof(rq_step_def));
    config.region_bytes = k_session_region_bytes;
    config.min_region_bytes = k_session_min_region_bytes;
    config.seed = k_seed;
    config.worker_count = workers;
    config.stop_on_error = 0U;
    config.error_detail_cap = 100U;
    config.progress_interval_ms = 250U;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = 1U;
    config.steps = &step;

    session_events events;
    const rq_callbacks callbacks = recording_callbacks(events);

    const auto affinity = std::make_shared<failing_affinity>(workers, fail, k_injected_error);
    counting_memory_access* counting = nullptr;
    session_dependencies dependencies;
    dependencies.affinity = affinity;
    dependencies.wrap_memory_access =
        [&counting](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        auto layer = std::make_unique<counting_memory_access>(std::move(real));
        counting = layer.get();
        return layer;
    };

    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), session.out()), RQ_OK);
    ASSERT_NE(counting, nullptr) << "session_create did not build the counting layer";
    ASSERT_EQ(session_start(session.get()), RQ_OK);
    {
        std::unique_lock hold(events.lock);
        ASSERT_TRUE(events.changed.wait_for(hold, k_session_timeout,
                                            [&events] { return events.released > 0U; }))
            << "Released did not arrive within " << k_session_timeout.count() << " s";
    }

    // The counting layer belongs to the session, so it is read before the session is destroyed.
    const std::uint64_t primitives = counting->primitive_count();
    const std::uint64_t verified = counting->verified_bytes();
    rq_run_result queried{};
    queried.struct_size = static_cast<std::uint32_t>(sizeof(rq_run_result));
    const rq_result queried_status = session_get_result(session.get(), &queried);
    ASSERT_EQ(session.destroy(), RQ_OK);
    EXPECT_EQ(live_session_count(), sessions_before);

    // No callback runs once the session is destroyed, so the record is final.
    const std::lock_guard hold(events.lock);
    EXPECT_EQ(events.prepared, 0U) << "Prepared was raised although a worker was never pinned";
    EXPECT_EQ(events.step_starting, 0U) << "a step gate was raised after the pin failure";
    EXPECT_EQ(events.step_finished, 0U);
    EXPECT_EQ(events.progress, 0U);
    EXPECT_EQ(events.error_batches, 0U);
    EXPECT_EQ(events.completed, 1U);
    EXPECT_EQ(events.released, 1U);

    const rq_run_result& result = events.result;
    EXPECT_EQ(result.end_reason, RQ_END_AFFINITY_FAILED);
    EXPECT_EQ(result.os_error, k_injected_error);
    const std::string detail(result.end_detail);
    EXPECT_EQ(detail.rfind("SetThreadGroupAffinity failed: group ", 0), std::size_t{0}) << detail;
    EXPECT_EQ(detail, "SetThreadGroupAffinity failed: " + describe(placements[fail]))
        << "detail must name failing worker " << fail << "'s processor";
    EXPECT_EQ(result.steps_completed, 0U);
    EXPECT_EQ(result.step_result_count, 0U);
    EXPECT_EQ(result.total_errors, 0U);
    EXPECT_EQ(result.verified_bytes, 0U);
    EXPECT_EQ(result.elapsed_ms, 0U) << "test time never started";
    EXPECT_EQ(result.first_error_ms, RQ_NO_FIRST_ERROR);

    ASSERT_EQ(queried_status, RQ_OK);
    EXPECT_EQ(queried.end_reason, RQ_END_AFFINITY_FAILED);
    EXPECT_EQ(queried.os_error, k_injected_error);
    EXPECT_STREQ(queried.end_detail, result.end_detail);

    EXPECT_EQ(primitives, 0U) << "memory traffic ran although a worker was never pinned";
    EXPECT_EQ(verified, 0U);

    // The region was allocated and locked in preparation, before the pool started, and released.
    EXPECT_GT(events.released_bytes, 0U);
    EXPECT_LE(events.released_bytes, k_session_region_bytes);

    for (std::uint32_t i = 0; i < workers; ++i) {
        EXPECT_EQ(affinity->calls(i), 1U) << "pin calls for worker " << i;
    }
}

}  // namespace
}  // namespace rq::engine::test
