/**
 * @file test_memory_region.cpp
 * @brief Region tests: the slice and chunk split, 2 MiB rounding, the shrink-and-retry
 *        sequence, real chunked allocation and locking, and release of the locked region on
 *        every end reason.
 *
 * - MemoryRegionPlan and MemoryRegionRetry: plan_region and next_retry_bytes as pure functions,
 *   against sizes worked out by hand, plus a whole retry sequence down to the minimum.
 * - MemoryRegionAllocate and MemoryRegionRefusal: memory_region over real memory without
 *   elevation. Every chunk is committed, every page is resident and locked (QueryWorkingSetEx),
 *   slices follow worker order in logical offsets, a scripted lock override drives
 *   shrink-and-retry and exhaustion deterministically, a stop before or during preparation frees
 *   everything, and release() and the destructor leave every former chunk base MEM_FREE with the
 *   working-set limits restored.
 * - MemoryRegionLargePages: large-page chunks, skipped when the token lacks
 *   SeLockMemoryPrivilege, which nobody holds by default.
 * - MemoryRegionRelease: whole sessions ending in each of completed, user stop, first error,
 *   thermal stop, affinity failure, host fault and engine fault. Released reports the tested (or
 *   planned) bytes, and every chunk base the run touched is MEM_FREE when Released is raised.
 *   That check runs inside on_released itself, before anything else in the process can reuse
 *   the freed address range. The two end reasons that fail before any traffic touch no chunk, so
 *   their release is shown by the commit charge instead: it is sampled inside the failing pin,
 *   after the region was committed, and again inside on_released.
 *
 * Regions stay at or below 512 MiB and sessions use at most two workers. Locking up to that size
 * needs no elevation once the working set is raised, which memory_region does itself.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <psapi.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <ostream>
#include <set>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/memory/memory_access.h"
#include "core/memory/memory_region.h"
#include "core/runner/worker_pool.h"
#include "core/session.h"
#include "fakes/counting_memory_access.h"
#include "fakes/fault_injecting_memory_access.h"
#include "fakes/fault_spec.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_gib = 1024 * k_mib;
constexpr std::uint64_t k_tib = 1024 * k_gib;

/** The x64 base page and the VirtualAlloc allocation granularity. */
constexpr std::uint64_t k_page_bytes = 4096;
constexpr std::uint64_t k_allocation_granularity_bytes = 64 * 1024;

/** The Windows errors the scripted failures report. */
constexpr std::uint32_t k_error_working_set_quota = 1453;
constexpr std::uint32_t k_error_invalid_parameter = 87;
static_assert(k_error_working_set_quota == ERROR_WORKING_SET_QUOTA, "Windows error 1453");
static_assert(k_error_invalid_parameter == ERROR_INVALID_PARAMETER, "Windows error 87");

/** The lock override's "always fail" setting. */
constexpr std::uint32_t k_always = UINT32_MAX;

/** Session regions: 64 MiB for runs with traffic, 32 MiB minimum. */
constexpr std::uint64_t k_session_region_bytes = 64 * k_mib;
constexpr std::uint64_t k_session_min_region_bytes = 32 * k_mib;

/**
 * Region of the two runs that fail before any traffic. It is four times the commit-charge
 * slack, so a region that stayed committed could not hide inside it.
 */
constexpr std::uint64_t k_no_traffic_region_bytes = 128 * k_mib;

/** Allowed drift of the process commit charge around a run, for heaps and thread stacks. */
constexpr std::uint64_t k_private_usage_slack_bytes = 32 * k_mib;

/** Session run options, as the application passes them except for the region size. */
constexpr std::uint32_t k_session_step_count = 2;
constexpr std::uint32_t k_short_step_ms = 300;
constexpr std::uint32_t k_long_step_ms = 1000;
constexpr std::uint32_t k_detail_cap = 10'000;
constexpr std::uint32_t k_progress_interval_ms = 250;
constexpr std::uint64_t k_seed = 0x5EED0F0ED1CEB00CULL;

/** Step gate setting that never aborts. */
constexpr std::uint32_t k_no_step = UINT32_MAX;

/** Longest wait for an engine event; a missed event fails the test instead of hanging it. */
constexpr std::chrono::seconds k_event_timeout{30};

/** First-error injection: bit 17 flipped in a few words of every pass. */
constexpr std::uint32_t k_flipped_bit = 17;
constexpr std::uint32_t k_flips_per_pass = 4;
constexpr std::uint32_t k_max_injections = 64;

/** The worker whose pin the scripted affinity fails, and the exception text it throws. */
constexpr std::uint32_t k_failing_worker = 0;
constexpr char k_injected_engine_fault[] = "injected engine fault";

/* ---------------------------------------------------------------------------------------------
 * Formatting and OS queries
 * ------------------------------------------------------------------------------------------- */

std::uintptr_t address_of(const void* pointer) noexcept {
    return reinterpret_cast<std::uintptr_t>(pointer);
}

/** 0x followed by 16 uppercase hex digits, the form the logs use for addresses. */
std::string hex_address(std::uintptr_t address) {
    char text[19];
    std::snprintf(text, sizeof(text), "0x%016llX", static_cast<unsigned long long>(address));
    return text;
}

const char* end_reason_name(std::uint32_t reason) noexcept {
    switch (reason) {
    case RQ_END_COMPLETED:
        return "RQ_END_COMPLETED";
    case RQ_END_FIRST_ERROR:
        return "RQ_END_FIRST_ERROR";
    case RQ_END_USER_STOP:
        return "RQ_END_USER_STOP";
    case RQ_END_THERMAL:
        return "RQ_END_THERMAL";
    case RQ_END_HOST_FAULT:
        return "RQ_END_HOST_FAULT";
    case RQ_END_INSUFFICIENT_MEMORY:
        return "RQ_END_INSUFFICIENT_MEMORY";
    case RQ_END_AFFINITY_FAILED:
        return "RQ_END_AFFINITY_FAILED";
    case RQ_END_INTERNAL_FAULT:
        return "RQ_END_INTERNAL_FAULT";
    default:
        return "no end reason";
    }
}

const char* memory_state_name(DWORD state) noexcept {
    switch (state) {
    case MEM_COMMIT:
        return "MEM_COMMIT";
    case MEM_RESERVE:
        return "MEM_RESERVE";
    case MEM_FREE:
        return "MEM_FREE";
    default:
        return "unknown state";
    }
}

/** Success when VirtualQuery reports the page at base as MEM_FREE. */
::testing::AssertionResult is_freed(std::uintptr_t base) {
    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(reinterpret_cast<const void*>(base), &info, sizeof(info)) != sizeof(info)) {
        return ::testing::AssertionFailure() << "VirtualQuery(" << hex_address(base)
                                             << ") failed (Windows error " << GetLastError() << ")";
    }
    if (info.State != MEM_FREE) {
        return ::testing::AssertionFailure()
               << "address " << hex_address(base)
               << " was not freed: " << memory_state_name(info.State) << ", allocation base "
               << hex_address(address_of(info.AllocationBase));
    }
    return ::testing::AssertionSuccess();
}

/** Expects VirtualQuery to report base as MEM_FREE; context names the exit path. */
void expect_freed(const void* base, const std::string& context) {
    EXPECT_TRUE(is_freed(address_of(base))) << context;
}

/** The process commit charge (PROCESS_MEMORY_COUNTERS_EX.PrivateUsage); 0 if the query fails. */
std::uint64_t current_private_usage() noexcept {
    PROCESS_MEMORY_COUNTERS_EX counters{};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(),
                              reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
                              sizeof(counters))) {
        return 0;
    }
    return counters.PrivateUsage;
}

/** The process working-set limits, which release() must restore. */
struct working_set_limits {
    SIZE_T minimum = 0;
    SIZE_T maximum = 0;
    DWORD flags = 0;
};

working_set_limits current_working_set_limits() {
    working_set_limits limits{};
    EXPECT_TRUE(GetProcessWorkingSetSizeEx(GetCurrentProcess(), &limits.minimum, &limits.maximum,
                                           &limits.flags))
        << "GetProcessWorkingSetSizeEx failed (Windows error " << GetLastError() << ")";
    return limits;
}

void expect_limits_restored(const working_set_limits& before, const std::string& context) {
    const working_set_limits after = current_working_set_limits();
    EXPECT_EQ(after.minimum, before.minimum) << context << ": minimum working set not restored";
    EXPECT_EQ(after.maximum, before.maximum) << context << ": maximum working set not restored";
    EXPECT_EQ(after.flags, before.flags) << context << ": working-set flags not restored";
}

/** Success when the first and last page of the chunk are resident large pages. */
::testing::AssertionResult pages_large(const chunk_view& chunk) {
    char* const base = reinterpret_cast<char*>(chunk.base);
    PSAPI_WORKING_SET_EX_INFORMATION info[2]{};
    info[0].VirtualAddress = base;
    info[1].VirtualAddress = base + chunk.bytes - k_page_bytes;
    if (!QueryWorkingSetEx(GetCurrentProcess(), info, static_cast<DWORD>(sizeof(info)))) {
        return ::testing::AssertionFailure()
               << "QueryWorkingSetEx failed (Windows error " << GetLastError() << ")";
    }
    for (const PSAPI_WORKING_SET_EX_INFORMATION& page : info) {
        const unsigned valid = static_cast<unsigned>(page.VirtualAttributes.Valid);
        const unsigned large = static_cast<unsigned>(page.VirtualAttributes.LargePage);
        if (valid == 0 || large == 0) {
            return ::testing::AssertionFailure()
                   << "page at " << hex_address(address_of(page.VirtualAddress))
                   << " is not a resident large page (valid " << valid << ", large " << large
                   << ")";
        }
    }
    return ::testing::AssertionSuccess();
}

/**
 * Success when every 4 KiB page of the chunk is resident and locked, which is what VirtualLock
 * guarantees for a 4 KiB-page chunk.
 */
::testing::AssertionResult pages_locked(const chunk_view& chunk) {
    const std::size_t pages = static_cast<std::size_t>(chunk.bytes / k_page_bytes);
    std::vector<PSAPI_WORKING_SET_EX_INFORMATION> info(pages);
    char* const base = reinterpret_cast<char*>(chunk.base);
    for (std::size_t page = 0; page < pages; ++page) {
        info[page].VirtualAddress = base + page * k_page_bytes;
    }
    if (!QueryWorkingSetEx(GetCurrentProcess(), info.data(),
                           static_cast<DWORD>(pages * sizeof(info[0])))) {
        return ::testing::AssertionFailure()
               << "QueryWorkingSetEx failed (Windows error " << GetLastError() << ")";
    }
    for (std::size_t page = 0; page < pages; ++page) {
        const unsigned valid = static_cast<unsigned>(info[page].VirtualAttributes.Valid);
        const unsigned locked = static_cast<unsigned>(info[page].VirtualAttributes.Locked);
        if (valid == 0 || locked == 0) {
            return ::testing::AssertionFailure()
                   << "page " << page << " at "
                   << hex_address(address_of(base + page * k_page_bytes))
                   << " is not resident and locked (valid " << valid << ", locked " << locked
                   << ")";
        }
    }
    return ::testing::AssertionSuccess();
}

/* ---------------------------------------------------------------------------------------------
 * Region helpers
 * ------------------------------------------------------------------------------------------- */

/** Every chunk base of an allocated region, copied before release() clears the list. */
std::vector<const void*> chunk_bases(const memory_region& region) {
    std::vector<const void*> bases;
    for (const chunk_view& chunk : region.chunks()) {
        bases.push_back(chunk.base);
    }
    return bases;
}

/** Writes one word per 4 KiB page of the chunk and reads each back. */
::testing::AssertionResult touch_every_page(const chunk_view& chunk) {
    constexpr std::size_t k_words_per_page = static_cast<std::size_t>(k_page_bytes / 8);
    volatile std::uint64_t* const words = chunk.base;
    const std::size_t pages = static_cast<std::size_t>(chunk.bytes / k_page_bytes);
    for (std::size_t page = 0; page < pages; ++page) {
        words[page * k_words_per_page] = chunk.logical_offset + page;
    }
    for (std::size_t page = 0; page < pages; ++page) {
        const std::uint64_t value = words[page * k_words_per_page];
        if (value != chunk.logical_offset + page) {
            return ::testing::AssertionFailure()
                   << "page " << page << " of the chunk at " << hex_address(address_of(chunk.base))
                   << " read back " << value;
        }
    }
    return ::testing::AssertionSuccess();
}

/**
 * Stands in for VirtualLock through region_options::lock_override.
 *
 * Calls up to failures fail with ERROR_WORKING_SET_QUOTA, set as the last error exactly as a
 * failed VirtualLock sets it; later calls lock for real. Call stop_on_call, counted from 1,
 * raises the stop signal before it locks, so the next chunk's stop check sees it. Runs on the
 * calling thread of allocate(), so it needs no synchronisation.
 */
class lock_script {
public:
    explicit lock_script(std::uint32_t failures, stop_signal* stop = nullptr,
                         std::uint32_t stop_on_call = 0)
        : failures_(failures), stop_(stop), stop_on_call_(stop_on_call) {
        // Reserved up front so recording a call never allocates inside allocate().
        bases_.reserve(256);
    }

    lock_function bind() {
        return [this](void* address, std::size_t bytes) { return lock(address, bytes); };
    }

    std::uint32_t calls() const noexcept { return calls_; }

    /** Base of every chunk the override was asked to lock, in call order. */
    const std::vector<const void*>& bases() const noexcept { return bases_; }

private:
    bool lock(void* address, std::size_t bytes) {
        ++calls_;
        if (bases_.size() < bases_.capacity()) {
            bases_.push_back(address);
        }
        if (stop_ != nullptr && calls_ == stop_on_call_) {
            stop_->request(RQ_END_USER_STOP, 0, "Stopped by the lock override");
        }
        if (calls_ <= failures_) {
            SetLastError(k_error_working_set_quota);
            return false;
        }
        return VirtualLock(address, bytes) != FALSE;
    }

    std::uint32_t failures_;
    stop_signal* stop_;
    std::uint32_t stop_on_call_;
    std::uint32_t calls_ = 0;
    std::vector<const void*> bases_;
};

/** Options for allocate() with 4 KiB pages; the large-page test sets lock_privilege_held. */
region_options make_options(std::uint64_t region_bytes, std::uint64_t min_region_bytes,
                            std::uint32_t worker_count, lock_function lock_override = {}) {
    region_options options{};
    options.region_bytes = region_bytes;
    options.min_region_bytes = min_region_bytes;
    options.worker_count = worker_count;
    options.lock_privilege_held = false;
    options.lock_override = std::move(lock_override);
    return options;
}

/** Expects the accessors to describe an unallocated region. */
void expect_unallocated(const memory_region& region, const std::string& context) {
    EXPECT_FALSE(region.allocated()) << context;
    EXPECT_EQ(region.tested_bytes(), 0u) << context;
    EXPECT_EQ(region.slice_bytes(), 0u) << context;
    EXPECT_EQ(region.worker_count(), 0u) << context;
    EXPECT_EQ(region.page_mode(), 0u) << context;
    EXPECT_EQ(region.large_page_bytes(), 0u) << context;
    EXPECT_TRUE(region.chunks().empty()) << context;
    EXPECT_TRUE(region.slice(0).chunks.empty()) << context;
}

/* ---------------------------------------------------------------------------------------------
 * Session helpers
 * ------------------------------------------------------------------------------------------- */

/**
 * Collects the allocation base of every range the run's primitives touched, through
 * counting_memory_access's observer, which runs on every worker concurrently.
 */
class address_capture {
public:
    /** The observer; noexcept, as the counting layer requires of it. */
    void observe(const primitive_record& record) noexcept {
        if (record.bytes == 0) {
            return;  // A fence has no range.
        }
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<const void*>(record.begin), &info, sizeof(info)) !=
            sizeof(info)) {
            query_failures_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        std::lock_guard<std::mutex> lock(mutex_);
        bases_.insert(address_of(info.AllocationBase));
    }

    std::vector<std::uintptr_t> bases() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return {bases_.begin(), bases_.end()};
    }

    std::uint64_t query_failures() const noexcept {
        return query_failures_.load(std::memory_order_relaxed);
    }

private:
    mutable std::mutex mutex_;
    std::set<std::uintptr_t> bases_;
    std::atomic<std::uint64_t> query_failures_{0};
};

/** What run_recorder saw, copied under its lock. */
struct recorded_run {
    bool prepared = false;
    rq_prepared prepared_info{};
    std::uint32_t gate_calls = 0;
    std::uint32_t steps_finished = 0;
    std::uint32_t progress_calls = 0;
    std::uint64_t error_total = 0;
    bool completed = false;
    /** The on_completed header; step_results is cleared, the engine owns that array. */
    rq_run_result result{};
    bool released = false;
    std::uint64_t released_bytes = 0;
    std::uint64_t release_ms = 0;
    /** Commit charge sampled inside on_released. */
    std::uint64_t private_usage_at_release = 0;
    /** One description per captured base that was not MEM_FREE inside on_released. */
    std::vector<std::string> unfreed;
};

/**
 * The rq_callbacks sink of the session tests: static RQ_CALL thunks with the recorder as
 * user_data. Callbacks run one at a time on the coordinator; the test thread waits for them
 * through a condition variable.
 */
class run_recorder {
public:
    explicit run_recorder(const address_capture& capture) : capture_(capture) {}

    run_recorder(const run_recorder&) = delete;
    run_recorder& operator=(const run_recorder&) = delete;

    rq_callbacks callbacks() noexcept {
        rq_callbacks sink{};
        sink.struct_size = sizeof(rq_callbacks);
        sink.user_data = this;
        sink.on_prepared = &on_prepared;
        sink.on_step_starting = &on_step_starting;
        sink.on_step_finished = &on_step_finished;
        sink.on_progress = &on_progress;
        sink.on_errors = &on_errors;
        sink.on_completed = &on_completed;
        sink.on_released = &on_released;
        return sink;
    }

    /** Makes the gate of step_index return RQ_ACTION_ABORT. Call before the session starts. */
    void abort_gate_at(std::uint32_t step_index) noexcept { abort_at_ = step_index; }

    bool wait_for_progress() {
        return wait_until([this] { return run_.progress_calls > 0 || run_.completed; });
    }

    bool wait_for_released() {
        return wait_until([this] { return run_.released; });
    }

    recorded_run snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return run_;
    }

private:
    static run_recorder& self(void* user_data) noexcept {
        return *static_cast<run_recorder*>(user_data);
    }

    template <class Predicate>
    bool wait_until(Predicate done) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, k_event_timeout, done);
    }

    template <class Update>
    void update(Update apply) noexcept {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            apply(run_);
        }
        changed_.notify_all();
    }

    static void RQ_CALL on_prepared(void* user_data, const rq_prepared* info) noexcept {
        self(user_data).update([info](recorded_run& run) {
            run.prepared = true;
            run.prepared_info = *info;
        });
    }

    static std::uint32_t RQ_CALL on_step_starting(void* user_data,
                                                  const rq_step_start* info) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([](recorded_run& run) { ++run.gate_calls; });
        return info->index == recorder.abort_at_ ? RQ_ACTION_ABORT : RQ_ACTION_CONTINUE;
    }

    static void RQ_CALL on_step_finished(void* user_data, const rq_step_result*) noexcept {
        self(user_data).update([](recorded_run& run) { ++run.steps_finished; });
    }

    static void RQ_CALL on_progress(void* user_data, const rq_progress*) noexcept {
        self(user_data).update([](recorded_run& run) { ++run.progress_calls; });
    }

    static void RQ_CALL on_errors(void* user_data, const rq_error_record*, std::uint32_t,
                                  std::uint64_t total_errors) noexcept {
        self(user_data).update([total_errors](recorded_run& run) {
            run.error_total = std::max(run.error_total, total_errors);
        });
    }

    static void RQ_CALL on_completed(void* user_data, const rq_run_result* result) noexcept {
        self(user_data).update([result](recorded_run& run) {
            run.completed = true;
            run.result = *result;
            run.result.step_results = nullptr;
        });
    }

    /**
     * Checks every captured base here, on the coordinator right after the region was freed:
     * by the time the test thread wakes, another allocation could have reused the range.
     */
    static void RQ_CALL on_released(void* user_data, std::uint64_t released_bytes,
                                    std::uint64_t release_ms) noexcept {
        run_recorder& recorder = self(user_data);
        const std::uint64_t private_usage = current_private_usage();
        std::vector<std::string> unfreed;
        for (const std::uintptr_t base : recorder.capture_.bases()) {
            const ::testing::AssertionResult freed = is_freed(base);
            if (!freed) {
                unfreed.emplace_back(freed.message());
            }
        }
        recorder.update([&](recorded_run& run) {
            run.released = true;
            run.released_bytes = released_bytes;
            run.release_ms = release_ms;
            run.private_usage_at_release = private_usage;
            run.unfreed = std::move(unfreed);
        });
    }

    const address_capture& capture_;
    std::uint32_t abort_at_ = k_no_step;
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    recorded_run run_;
};

/** How the scripted affinity fails worker k_failing_worker's pin. */
enum class pin_failure {
    /** No scripted affinity: the session pins through win32_thread_affinity. */
    none,
    /** pin returns ERROR_INVALID_PARAMETER: preparation ends with RQ_END_AFFINITY_FAILED. */
    windows_error,
    /** pin throws: the worker entry point turns the C++ exception into RQ_END_INTERNAL_FAULT. */
    exception,
};

/**
 * Fails one worker's pin and pins every other worker for real. The pool pins only after the
 * region is allocated and locked, so the commit charge sampled in the failing pin includes the
 * whole region.
 */
class scripted_affinity final : public IThreadAffinity {
public:
    explicit scripted_affinity(pin_failure failure)
        : failure_(failure), real_(make_win32_thread_affinity()) {}

    std::uint32_t pin(std::uint32_t worker_index, std::uint16_t group,
                      std::uint8_t processor) override {
        if (worker_index != k_failing_worker) {
            return real_->pin(worker_index, group, processor);
        }
        private_usage_at_pin_.store(current_private_usage(), std::memory_order_release);
        if (failure_ == pin_failure::exception) {
            // Breaks pin's never-throw contract on purpose: a C++ exception escaping into a
            // worker entry point is the engine fault under test.
            throw std::runtime_error(k_injected_engine_fault);
        }
        return k_error_invalid_parameter;
    }

    /** Commit charge inside the failing pin; 0 until that pin has run. */
    std::uint64_t private_usage_at_pin() const noexcept {
        return private_usage_at_pin_.load(std::memory_order_acquire);
    }

private:
    pin_failure failure_;
    std::shared_ptr<IThreadAffinity> real_;
    std::atomic<std::uint64_t> private_usage_at_pin_{0};
};

/** Owns a session handle and destroys it on scope exit, so a failed assertion never leaks it. */
class session_guard {
public:
    session_guard() = default;
    ~session_guard() {
        if (handle_ != nullptr) {
            session_destroy(handle_);
        }
    }
    session_guard(const session_guard&) = delete;
    session_guard& operator=(const session_guard&) = delete;

    rq_session_handle* out() noexcept { return &handle_; }
    rq_session_handle get() const noexcept { return handle_; }

    /** Destroys the session now, returning session_destroy's result. */
    rq_result destroy() { return session_destroy(std::exchange(handle_, nullptr)); }

private:
    rq_session_handle handle_ = nullptr;
};

/** Two workers where the machine has them, the smallest pool that still splits the region. */
std::uint32_t session_worker_count() {
    return static_cast<std::uint32_t>(
        std::min<std::size_t>(2, enumerate_logical_processors().size()));
}

/** count windowed random-data steps in ascending order, each with the given time budget. */
std::vector<rq_step_def> make_steps(std::uint32_t count, std::uint32_t budget_ms) {
    std::vector<rq_step_def> steps(count);
    for (std::uint32_t index = 0; index < count; ++index) {
        rq_step_def& step = steps[index];
        std::snprintf(step.name, sizeof(step.name), "Random sweep %u", index + 1);
        step.pattern = RQ_PATTERN_RANDOM;
        step.access_order = RQ_ORDER_SEQUENTIAL_ASCENDING;
        step.thread_mode = RQ_THREADS_ALL;
        step.hold_ms = 0;
        step.time_budget_ms = budget_ms;
        step.coverage_budget_bp = 2500;
    }
    return steps;
}

rq_session_config make_config(const std::vector<rq_step_def>& steps, std::uint64_t region_bytes,
                              std::uint32_t worker_count, std::uint32_t stop_on_error) {
    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = region_bytes;
    config.min_region_bytes = k_session_min_region_bytes;
    config.seed = k_seed;
    config.worker_count = worker_count;
    config.stop_on_error = stop_on_error;
    config.error_detail_cap = k_detail_cap;
    config.progress_interval_ms = k_progress_interval_ms;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = static_cast<std::uint32_t>(steps.size());
    config.steps = steps.data();
    return config;
}

/** Single-bit flips of bit 17 in a few words of every pass, spread over the whole region. */
rq_fault_spec bit_flip_spec(std::uint64_t region_bytes) {
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_SINGLE_BIT_FLIP);
    spec.bit = k_flipped_bit;
    spec.per_pass_count = k_flips_per_pass;
    spec.max_injections = k_max_injections;
    spec.seed = k_seed;
    return normalized(spec, region_bytes);
}

/** Non-owning views of the session's substitute layers, set inside wrap_memory_access. */
struct layer_handles {
    counting_memory_access* counting = nullptr;
    fault_injecting_memory_access* fault = nullptr;
};

/**
 * The session's seams: counting -> real with the address capture as observer, under the
 * fault-injecting layer when a spec is given. The fault layer and the session share one clock.
 */
session_dependencies make_dependencies(address_capture& capture, layer_handles& layers,
                                       std::optional<rq_fault_spec> fault,
                                       std::shared_ptr<IClock> clock,
                                       std::shared_ptr<IThreadAffinity> affinity) {
    session_dependencies dependencies;
    dependencies.clock = clock;
    dependencies.affinity = std::move(affinity);
    dependencies.wrap_memory_access =
        [&capture, &layers, fault,
         clock](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        auto counting = std::make_unique<counting_memory_access>(std::move(real));
        counting->set_observer(
            [&capture](const primitive_record& record) { capture.observe(record); });
        layers.counting = counting.get();
        if (!fault) {
            return counting;
        }
        auto injecting =
            std::make_unique<fault_injecting_memory_access>(std::move(counting), *fault, *clock);
        layers.fault = injecting.get();
        return injecting;
    };
    return dependencies;
}

const char* status_name(allocation_status status) noexcept {
    switch (status) {
    case allocation_status::ok:
        return "ok";
    case allocation_status::stopped:
        return "stopped";
    case allocation_status::insufficient_memory:
        return "insufficient_memory";
    }
    return "unknown status";
}

/* ---------------------------------------------------------------------------------------------
 * Pure planning: plan_region
 * ------------------------------------------------------------------------------------------- */

/** A region, a worker count and the plan worked out by hand for them. */
struct plan_case {
    const char* name;
    std::uint64_t region_bytes;
    std::uint32_t worker_count;
    std::uint32_t planned_workers;
    std::uint64_t slice_bytes;
    std::uint32_t full_chunks;
    std::uint64_t final_chunk_bytes;
    std::uint32_t chunks_per_slice;
};

void PrintTo(const plan_case& value, std::ostream* os) {
    *os << value.name;
}

class MemoryRegionPlan : public ::testing::TestWithParam<plan_case> {};

TEST_P(MemoryRegionPlan, SplitsSlicesIntoChunksRoundedToTwoMib) {
    const plan_case& expected = GetParam();
    const region_plan plan = plan_region(expected.region_bytes, expected.worker_count);

    EXPECT_EQ(plan.worker_count, expected.planned_workers);
    EXPECT_EQ(plan.slice_bytes, expected.slice_bytes);
    EXPECT_EQ(plan.tested_bytes, expected.slice_bytes * expected.planned_workers);
    EXPECT_EQ(plan.full_chunks_per_slice, expected.full_chunks);
    EXPECT_EQ(plan.final_chunk_bytes, expected.final_chunk_bytes);
    EXPECT_EQ(plan.chunks_per_slice, expected.chunks_per_slice);

    // Invariants every plan keeps.
    EXPECT_LE(plan.tested_bytes, expected.region_bytes);
    EXPECT_EQ(plan.slice_bytes % k_slice_granularity_bytes, 0u);
    EXPECT_EQ(plan.final_chunk_bytes % k_slice_granularity_bytes, 0u);
    EXPECT_LT(plan.final_chunk_bytes, k_chunk_bytes);
    EXPECT_EQ(plan.full_chunks_per_slice * k_chunk_bytes + plan.final_chunk_bytes,
              plan.slice_bytes);
    if (plan.worker_count != 0) {
        EXPECT_LT(expected.region_bytes / plan.worker_count - plan.slice_bytes,
                  k_slice_granularity_bytes)
            << "a slice is rounded down by less than 2 MiB";
    }
}

INSTANTIATE_TEST_SUITE_P(
    MemoryRegionPlanCases, MemoryRegionPlan,
    ::testing::Values(
        // 10240 / 12 = 853.3 MiB, rounded down to 852 MiB = 3 x 256 + 84 MiB.
        plan_case{"TenGibOverTwelveWorkers", 10 * k_gib, 12, 12, 852 * k_mib, 3, 84 * k_mib, 4},
        plan_case{"OneGibOverFourWorkers", k_gib, 4, 4, 256 * k_mib, 1, 0, 1},
        plan_case{"ThirtyTwoGibOverThirtyTwoWorkers", 32 * k_gib, 32, 32, 1024 * k_mib, 4, 0, 4},
        plan_case{"ThousandMibOverThreeWorkers", 1000 * k_mib, 3, 3, 332 * k_mib, 1, 76 * k_mib, 2},
        plan_case{"OneGibOverThreeWorkers", k_gib, 3, 3, 340 * k_mib, 1, 84 * k_mib, 2},
        plan_case{"FullAndFinalChunkInOneSlice", 262 * k_mib, 1, 1, 262 * k_mib, 1, 6 * k_mib, 2},
        plan_case{"UnroundedRegionRoundsDown", 3 * k_mib + 4095, 1, 1, 2 * k_mib, 0, 2 * k_mib, 1},
        plan_case{"ExactlyTwoMibPerWorker", 8 * k_mib, 4, 4, 2 * k_mib, 0, 2 * k_mib, 1},
        plan_case{"BelowTwoMibPerWorker", 8 * k_mib - 1, 4, 4, 0, 0, 0, 0},
        plan_case{"NoWorkers", k_gib, 0, 0, 0, 0, 0, 0}),
    [](const ::testing::TestParamInfo<plan_case>& info) { return std::string(info.param.name); });

/* ---------------------------------------------------------------------------------------------
 * Pure planning: next_retry_bytes
 * ------------------------------------------------------------------------------------------- */

/** One failed attempt and the next size worked out by hand. */
struct retry_case {
    const char* name;
    std::uint64_t attempted_bytes;
    std::uint64_t min_region_bytes;
    std::uint32_t worker_count;
    std::uint64_t expected_bytes;
};

void PrintTo(const retry_case& value, std::ostream* os) {
    *os << value.name;
}

class MemoryRegionRetry : public ::testing::TestWithParam<retry_case> {};

TEST_P(MemoryRegionRetry, ShrinksTenPercentRoundedToTheWorkerUnit) {
    const retry_case& expected = GetParam();
    EXPECT_EQ(next_retry_bytes(expected.attempted_bytes, expected.min_region_bytes,
                               expected.worker_count),
              expected.expected_bytes);
}

INSTANTIATE_TEST_SUITE_P(
    MemoryRegionRetryCases, MemoryRegionRetry,
    ::testing::Values(
        // 8 workers: unit 16 MiB. 1200 x 0.9 = 1080, rounded down to 1072 MiB.
        retry_case{"ShrinksTenPercent", 1200 * k_mib, k_gib, 8, 1072 * k_mib},
        // 1072 x 0.9 = 964.8 falls below the minimum: one final attempt at the minimum.
        retry_case{"ClampsTheFinalAttemptToTheMinimum", 1072 * k_mib, k_gib, 8, 1024 * k_mib},
        retry_case{"EndsAfterTheMinimum", 1024 * k_mib, k_gib, 8, 0},
        retry_case{"EndsBelowTheMinimum", 512 * k_mib, k_gib, 8, 0},
        // 2 workers: unit 4 MiB. 128 -> 115.2 -> 112 MiB, and 100 -> 90 -> 88 MiB.
        retry_case{"RoundsDownToTheUnit", 128 * k_mib, 32 * k_mib, 2, 112 * k_mib},
        retry_case{"RoundsDownAgain", 100 * k_mib, 32 * k_mib, 2, 88 * k_mib},
        // 3 workers: unit 6 MiB, so the 1 GiB minimum rounds up to 1026 MiB.
        retry_case{"RoundsTheMinimumUpToTheUnit", 1100 * k_mib, k_gib, 3, 1026 * k_mib},
        retry_case{"EndsAfterTheRoundedMinimum", 1026 * k_mib, k_gib, 3, 0},
        retry_case{"LargeSizesDoNotOverflow", 100 * k_tib, k_gib, 1, 90 * k_tib},
        retry_case{"NoWorkers", k_gib, 32 * k_mib, 0, 0}),
    [](const ::testing::TestParamInfo<retry_case>& info) { return std::string(info.param.name); });

TEST(MemoryRegionRetrySequence, FromTenGibEndsAtTheRoundedMinimum) {
    constexpr std::uint32_t k_workers = 12;
    constexpr std::uint64_t k_unit = k_workers * k_slice_granularity_bytes;  // 24 MiB
    constexpr std::uint64_t k_min = k_gib;
    // The first multiple of 24 MiB at or above 1 GiB: 43 x 24 MiB.
    constexpr std::uint64_t k_rounded_min = 1032 * k_mib;
    // Far more than the ~30 steps of a 10 % shrink from 10 GiB to 1 GiB.
    constexpr std::size_t k_step_limit = 64;

    std::uint64_t attempted = plan_region(10 * k_gib, k_workers).tested_bytes;
    ASSERT_EQ(attempted, 10224 * k_mib);
    std::vector<std::uint64_t> sizes;
    while (sizes.size() < k_step_limit) {
        const std::uint64_t next = next_retry_bytes(attempted, k_min, k_workers);
        if (next == 0) {
            break;
        }
        ASSERT_LT(next, attempted) << "retry sizes strictly decrease";
        EXPECT_EQ(next % k_unit, 0u) << next;
        EXPECT_GE(next, k_min) << "no retry goes below the minimum";
        EXPECT_EQ(plan_region(next, k_workers).tested_bytes, next)
            << "every retry size plans all of itself";
        const std::uint64_t shrunk = attempted * 9 / 10 / k_unit * k_unit;
        EXPECT_TRUE(next == shrunk || next == k_rounded_min)
            << next << " is neither the 10 % shrink of " << attempted << " nor the minimum";
        sizes.push_back(next);
        attempted = next;
    }
    ASSERT_LT(sizes.size(), k_step_limit) << "the sequence must end with 0";
    ASSERT_FALSE(sizes.empty());
    EXPECT_EQ(sizes.back(), k_rounded_min);
    EXPECT_EQ(next_retry_bytes(sizes.back(), k_min, k_workers), 0u);
}

/* ---------------------------------------------------------------------------------------------
 * Allocation over real memory
 * ------------------------------------------------------------------------------------------- */

struct allocation_case {
    const char* name;
    std::uint64_t region_bytes;
    std::uint32_t worker_count;
};

void PrintTo(const allocation_case& value, std::ostream* os) {
    *os << value.name;
}

class MemoryRegionAllocate : public ::testing::TestWithParam<allocation_case> {};

TEST_P(MemoryRegionAllocate, LocksEveryChunkOfEverySliceAndReleasesThem) {
    const allocation_case& request = GetParam();
    const region_plan expected = plan_region(request.region_bytes, request.worker_count);
    ASSERT_GT(expected.tested_bytes, 0u);
    const working_set_limits limits_before = current_working_set_limits();

    stop_signal stop;
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(request.region_bytes, 64 * k_mib, request.worker_count), stop);
    ASSERT_EQ(outcome.status, allocation_status::ok)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    EXPECT_EQ(outcome.attempts, 1u);
    EXPECT_EQ(outcome.os_error, 0u);
    EXPECT_EQ(outcome.large_page_fallbacks, 0u);
    EXPECT_EQ(outcome.end_detail[0], '\0');

    EXPECT_TRUE(region.allocated());
    EXPECT_EQ(region.tested_bytes(), expected.tested_bytes);
    EXPECT_EQ(region.slice_bytes(), expected.slice_bytes);
    EXPECT_EQ(region.worker_count(), request.worker_count);
    EXPECT_EQ(region.plan().chunks_per_slice, expected.chunks_per_slice);
    EXPECT_EQ(region.page_mode(), RQ_PAGE_MODE_STANDARD) << "4 KiB pages without the privilege";
    EXPECT_EQ(region.large_page_bytes(), 0u);
    ASSERT_EQ(region.chunks().size(),
              std::size_t{request.worker_count} * expected.chunks_per_slice);

    // Slices follow worker order and their chunks tile the logical region without a gap.
    std::uint64_t next_offset = 0;
    for (std::uint32_t worker = 0; worker < request.worker_count; ++worker) {
        const slice_view slice = region.slice(worker);
        EXPECT_EQ(slice.worker_index, worker);
        EXPECT_EQ(slice.logical_offset, worker * expected.slice_bytes);
        EXPECT_EQ(slice.bytes, expected.slice_bytes);
        ASSERT_EQ(slice.chunks.size(), expected.chunks_per_slice);
        std::uint64_t slice_total = 0;
        for (std::size_t j = 0; j < slice.chunks.size(); ++j) {
            const chunk_view& chunk = slice.chunks[j];
            SCOPED_TRACE(::testing::Message() << "worker " << worker << ", chunk " << j << " at "
                                              << hex_address(address_of(chunk.base)));
            EXPECT_EQ(&chunk, &region.chunks()[worker * expected.chunks_per_slice + j]);
            EXPECT_EQ(chunk.logical_offset, next_offset);
            EXPECT_EQ(chunk.logical_offset, slice.logical_offset + j * k_chunk_bytes);
            EXPECT_EQ(chunk.bytes, j < expected.full_chunks_per_slice ? k_chunk_bytes
                                                                      : expected.final_chunk_bytes);
            EXPECT_FALSE(chunk.large_pages);
            EXPECT_EQ(logical_base(chunk), address_of(chunk.base) - chunk.logical_offset);
            EXPECT_EQ(address_of(chunk.base) % k_allocation_granularity_bytes, 0u);

            MEMORY_BASIC_INFORMATION info{};
            ASSERT_EQ(VirtualQuery(chunk.base, &info, sizeof(info)), sizeof(info));
            EXPECT_EQ(info.AllocationBase, chunk.base) << "one allocation per chunk";
            EXPECT_EQ(info.State, static_cast<DWORD>(MEM_COMMIT));
            EXPECT_EQ(info.Type, static_cast<DWORD>(MEM_PRIVATE));
            EXPECT_EQ(info.Protect, static_cast<DWORD>(PAGE_READWRITE));
            EXPECT_GE(info.RegionSize, chunk.bytes);
            EXPECT_TRUE(pages_locked(chunk));
            EXPECT_TRUE(touch_every_page(chunk));

            next_offset += chunk.bytes;
            slice_total += chunk.bytes;
        }
        EXPECT_EQ(slice_total, slice.bytes);
    }
    EXPECT_EQ(next_offset, region.tested_bytes());
    EXPECT_EQ(region.slice(request.worker_count).bytes, 0u) << "out-of-range slice is empty";
    EXPECT_TRUE(region.slice(request.worker_count).chunks.empty());

    const std::vector<const void*> bases = chunk_bases(region);
    EXPECT_EQ(region.release(), expected.tested_bytes);
    EXPECT_EQ(region.release(), 0u) << "release() is idempotent";
    for (const void* base : bases) {
        expect_freed(base, "after release()");
    }
    expect_unallocated(region, "after release()");
    expect_limits_restored(limits_before, "after release()");
}

INSTANTIATE_TEST_SUITE_P(MemoryRegionAllocateCases, MemoryRegionAllocate,
                         ::testing::Values(
                             // One 262 MiB slice: a full 256 MiB chunk and a 6 MiB final chunk.
                             allocation_case{"FullAndFinalChunkInOneSlice", 262 * k_mib, 1},
                             // 131 MiB per worker rounds down to 130 MiB slices, 260 MiB tested.
                             allocation_case{"TwoSlicesRoundedDownToTwoMib", 262 * k_mib, 2}),
                         [](const ::testing::TestParamInfo<allocation_case>& info) {
                             return std::string(info.param.name);
                         });

TEST(MemoryRegionShrink, RetriesAtNinetyPercentAfterLockFailures) {
    constexpr std::uint64_t k_region = 128 * k_mib;
    constexpr std::uint64_t k_min = 32 * k_mib;
    constexpr std::uint32_t k_workers = 2;
    constexpr std::uint32_t k_failures = 3;
    const working_set_limits limits_before = current_working_set_limits();

    std::uint64_t expected = k_region;
    for (std::uint32_t retry = 0; retry < k_failures; ++retry) {
        expected = next_retry_bytes(expected, k_min, k_workers);
    }
    // 128 -> 112 -> 100 -> 88 MiB.
    ASSERT_EQ(expected, 88 * k_mib);

    stop_signal stop;
    lock_script script(k_failures);
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(k_region, k_min, k_workers, script.bind()), stop);
    ASSERT_EQ(outcome.status, allocation_status::ok)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    EXPECT_EQ(outcome.attempts, k_failures + 1);
    EXPECT_EQ(outcome.os_error, 0u);
    EXPECT_EQ(outcome.end_detail[0], '\0');
    EXPECT_EQ(region.tested_bytes(), expected);
    EXPECT_EQ(region.slice_bytes(), expected / k_workers);

    // Each failed attempt fails on its first chunk; the last attempt locks one chunk per slice.
    const std::uint32_t chunks = static_cast<std::uint32_t>(region.chunks().size());
    EXPECT_EQ(chunks, k_workers);
    EXPECT_EQ(script.calls(), k_failures + chunks);
    for (const chunk_view& chunk : region.chunks()) {
        EXPECT_TRUE(pages_locked(chunk));
    }

    const std::vector<const void*> bases = chunk_bases(region);
    EXPECT_EQ(region.release(), expected);
    for (const void* base : bases) {
        expect_freed(base, "after release() of a shrunk region");
    }
    expect_limits_restored(limits_before, "after release() of a shrunk region");
}

TEST(MemoryRegionShrink, ReportsInsufficientMemoryWhenNoSizeLocks) {
    constexpr std::uint64_t k_region = 128 * k_mib;
    constexpr std::uint64_t k_min = 32 * k_mib;
    constexpr std::uint32_t k_workers = 2;
    const working_set_limits limits_before = current_working_set_limits();

    // The first attempt plus one per non-zero retry size, ending with the 32 MiB minimum.
    std::uint32_t expected_attempts = 1;
    std::uint64_t last_size = k_region;
    for (std::uint64_t next = next_retry_bytes(last_size, k_min, k_workers); next != 0;
         next = next_retry_bytes(last_size, k_min, k_workers)) {
        ++expected_attempts;
        last_size = next;
    }
    ASSERT_EQ(last_size, k_min);

    stop_signal stop;
    lock_script script(k_always);
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(k_region, k_min, k_workers, script.bind()), stop);
    ASSERT_EQ(outcome.status, allocation_status::insufficient_memory)
        << status_name(outcome.status);
    EXPECT_EQ(outcome.os_error, k_error_working_set_quota);
    EXPECT_EQ(outcome.attempts, expected_attempts);
    EXPECT_EQ(script.calls(), expected_attempts) << "each attempt fails on its first chunk";

    const void* const terminator =
        std::memchr(outcome.end_detail, '\0', sizeof(outcome.end_detail));
    ASSERT_NE(terminator, nullptr) << "end_detail must be NUL-terminated";
    const std::string detail = outcome.end_detail;
    EXPECT_FALSE(detail.empty());
    EXPECT_NE(detail.find("VirtualLock"), std::string::npos) << detail;
    EXPECT_NE(detail.find("1453"), std::string::npos) << detail;
    EXPECT_NE(detail.find("at 32 MiB"), std::string::npos) << "names the last size: " << detail;
    EXPECT_NE(detail.find("32 MiB minimum"), std::string::npos) << detail;

    ASSERT_EQ(script.bases().size(), script.calls());
    for (const void* base : script.bases()) {
        expect_freed(base, "after insufficient_memory");
    }
    expect_unallocated(region, "after insufficient_memory");
    expect_limits_restored(limits_before, "after insufficient_memory");
}

/** A first size that cannot plan the minimum, and the text that names why. */
struct refusal_case {
    const char* name;
    std::uint64_t region_bytes;
    std::uint64_t min_region_bytes;
    std::uint32_t worker_count;
    const char* expected_detail;
};

void PrintTo(const refusal_case& value, std::ostream* os) {
    *os << value.name;
}

class MemoryRegionRefusal : public ::testing::TestWithParam<refusal_case> {};

TEST_P(MemoryRegionRefusal, RefusesBeforeAnyAllocation) {
    const refusal_case& request = GetParam();
    stop_signal stop;
    lock_script script(0);
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(request.region_bytes, request.min_region_bytes,
                                     request.worker_count, script.bind()),
                        stop);
    ASSERT_EQ(outcome.status, allocation_status::insufficient_memory)
        << status_name(outcome.status);
    EXPECT_EQ(outcome.attempts, 1u);
    EXPECT_EQ(outcome.os_error, 0u) << "no OS call failed";
    EXPECT_EQ(script.calls(), 0u) << "nothing was allocated or locked";
    const std::string detail = outcome.end_detail;
    EXPECT_NE(detail.find(request.expected_detail), std::string::npos) << detail;
    expect_unallocated(region, "after a refused request");
}

INSTANTIATE_TEST_SUITE_P(
    MemoryRegionRefusalCases, MemoryRegionRefusal,
    ::testing::Values(
        refusal_case{"NoTwoMibSlicePerWorker", 3 * k_mib, 2 * k_mib, 2,
                     "cannot give each of 2 workers a 2 MiB slice"},
        // 34 MiB over 4 workers plans 8 MiB slices: 32 MiB tested, below the 34 MiB minimum.
        refusal_case{"PlanBelowTheMinimum", 34 * k_mib, 34 * k_mib, 4,
                     "tests 32 MiB, below the 34 MiB minimum"}),
    [](const ::testing::TestParamInfo<refusal_case>& info) {
        return std::string(info.param.name);
    });

TEST(MemoryRegionStop, StopBeforeAllocateAllocatesNothing) {
    stop_signal stop;
    stop.request(RQ_END_USER_STOP, 0, "Stopped before allocate");
    lock_script script(0);
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(64 * k_mib, 32 * k_mib, 2, script.bind()), stop);
    ASSERT_EQ(outcome.status, allocation_status::stopped) << status_name(outcome.status);
    EXPECT_EQ(outcome.attempts, 0u);
    EXPECT_EQ(outcome.os_error, 0u);
    EXPECT_EQ(outcome.end_detail[0], '\0');
    EXPECT_EQ(script.calls(), 0u);
    expect_unallocated(region, "after a stop before allocate");
}

TEST(MemoryRegionStop, StopDuringPreparationFreesEveryEarlierChunk) {
    // Four 16 MiB slices of one chunk each; the stop arrives while chunk 2 is locked, so the
    // check before chunk 3 sees it.
    constexpr std::uint32_t k_workers = 4;
    constexpr std::uint32_t k_stop_on_call = 2;
    const working_set_limits limits_before = current_working_set_limits();

    stop_signal stop;
    lock_script script(0, &stop, k_stop_on_call);
    memory_region region;
    const allocation_outcome outcome =
        region.allocate(make_options(64 * k_mib, 32 * k_mib, k_workers, script.bind()), stop);
    ASSERT_EQ(outcome.status, allocation_status::stopped) << status_name(outcome.status);
    EXPECT_EQ(outcome.attempts, 1u);
    EXPECT_EQ(outcome.os_error, 0u);
    EXPECT_EQ(outcome.end_detail[0], '\0');
    EXPECT_EQ(script.calls(), k_stop_on_call) << "no chunk after the stop was allocated";
    ASSERT_EQ(script.bases().size(), k_stop_on_call);
    for (const void* base : script.bases()) {
        expect_freed(base, "after a stop during preparation");
    }
    expect_unallocated(region, "after a stop during preparation");
    expect_limits_restored(limits_before, "after a stop during preparation");
}

TEST(MemoryRegionRelease, DestructorReleasesEveryChunk) {
    const working_set_limits limits_before = current_working_set_limits();
    std::vector<const void*> bases;
    {
        stop_signal stop;
        memory_region region;
        const allocation_outcome outcome =
            region.allocate(make_options(64 * k_mib, 32 * k_mib, 2), stop);
        ASSERT_EQ(outcome.status, allocation_status::ok)
            << status_name(outcome.status) << ": " << outcome.end_detail;
        bases = chunk_bases(region);
        ASSERT_EQ(bases.size(), 2u);
    }
    for (const void* base : bases) {
        expect_freed(base, "after the destructor");
    }
    expect_limits_restored(limits_before, "after the destructor");
}

TEST(MemoryRegionLargePages, AllocatesLargePageChunksWhenThePrivilegeIsHeld) {
    if (!lock_memory_privilege_held()) {
        GTEST_SKIP() << "SeLockMemoryPrivilege not held";
    }
    const std::uint64_t large_page_bytes = GetLargePageMinimum();
    ASSERT_NE(large_page_bytes, 0u) << "x64 processors support large pages";
    ASSERT_EQ(k_chunk_bytes % large_page_bytes, 0u);

    // Two 256 MiB slices of one chunk each, both eligible for large pages.
    constexpr std::uint64_t k_region = 512 * k_mib;
    stop_signal stop;
    memory_region region;
    region_options options = make_options(k_region, 32 * k_mib, 2);
    options.lock_privilege_held = true;
    const allocation_outcome outcome = region.allocate(options, stop);
    ASSERT_EQ(outcome.status, allocation_status::ok)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    ASSERT_EQ(region.chunks().size(), 2u);

    std::uint32_t large_chunks = 0;
    std::uint64_t large_bytes = 0;
    for (const chunk_view& chunk : region.chunks()) {
        SCOPED_TRACE(::testing::Message() << "chunk at " << hex_address(address_of(chunk.base)));
        if (chunk.large_pages) {
            ++large_chunks;
            large_bytes += chunk.bytes;
            EXPECT_EQ(chunk.bytes, k_chunk_bytes);
            EXPECT_EQ(address_of(chunk.base) % large_page_bytes, 0u);
            EXPECT_TRUE(pages_large(chunk));
        } else {
            // A chunk whose large-page allocation fell back is locked like any 4 KiB chunk.
            EXPECT_TRUE(pages_locked(chunk));
        }
    }
    EXPECT_EQ(large_chunks + outcome.large_page_fallbacks, region.chunks().size());
    EXPECT_EQ(region.large_page_bytes(), large_bytes);
    const std::uint32_t expected_mode = large_chunks == region.chunks().size() ? RQ_PAGE_MODE_LARGE
                                        : large_chunks == 0 ? RQ_PAGE_MODE_STANDARD
                                                            : RQ_PAGE_MODE_MIXED;
    EXPECT_EQ(region.page_mode(), expected_mode);

    const std::vector<const void*> bases = chunk_bases(region);
    EXPECT_EQ(region.release(), k_region);
    for (const void* base : bases) {
        expect_freed(base, "after release() of large-page chunks");
    }
    expect_unallocated(region, "after release() of large-page chunks");

    // Physical fragmentation can defeat every large-page allocation even with the privilege;
    // the fallback path is checked above, but the large-page path was not exercised.
    if (large_chunks == 0) {
        GTEST_SKIP() << "every large-page chunk fell back to 4 KiB pages (physical fragmentation)";
    }
}

/* ---------------------------------------------------------------------------------------------
 * Release on every end reason
 * ------------------------------------------------------------------------------------------- */

/** The ways a session can end. */
enum class end_scenario {
    completed,
    user_stop,
    first_error,
    thermal,
    affinity_failure,
    host_fault,
    engine_fault,
};

/**
 * How a scenario drives the session, and the end it must reach. The defaults describe a run
 * that prepares and runs two short steps over the session region with no fault.
 */
struct scenario_spec {
    const char* name = "";
    std::uint32_t end_reason = 0;
    /** Substring end_detail must contain; null when it must be empty. */
    const char* detail = nullptr;
    std::uint32_t os_error = 0;
    /** Whether preparation succeeds, and with it whether the steps generate traffic. */
    bool prepared = true;
    std::uint32_t step_budget_ms = k_short_step_ms;
    std::uint64_t region_bytes = k_session_region_bytes;
    std::uint32_t stop_on_error = 0;
    /** RQ_STOP_* requested once the first progress callback has arrived; 0 for none. */
    std::uint32_t host_stop = 0;
    /** Step whose gate aborts; k_no_step for none. */
    std::uint32_t abort_gate_at = k_no_step;
    bool inject_bit_flips = false;
    pin_failure pin = pin_failure::none;
};

scenario_spec spec_for(end_scenario scenario) {
    scenario_spec spec;
    switch (scenario) {
    case end_scenario::completed:
        spec.name = "Completed";
        spec.end_reason = RQ_END_COMPLETED;
        break;
    case end_scenario::user_stop:
        spec.name = "UserStop";
        spec.end_reason = RQ_END_USER_STOP;
        spec.detail = "Stop requested by host: user";
        spec.step_budget_ms = k_long_step_ms;
        spec.host_stop = RQ_STOP_USER;
        break;
    case end_scenario::first_error:
        spec.name = "FirstError";
        spec.end_reason = RQ_END_FIRST_ERROR;
        spec.detail = "First error: step 1 of 2";
        spec.step_budget_ms = k_long_step_ms;
        spec.stop_on_error = 1;
        spec.inject_bit_flips = true;
        break;
    case end_scenario::thermal:
        spec.name = "Thermal";
        spec.end_reason = RQ_END_THERMAL;
        spec.detail = "Stop requested by host: thermal limit";
        spec.step_budget_ms = k_long_step_ms;
        spec.host_stop = RQ_STOP_THERMAL;
        break;
    case end_scenario::affinity_failure:
        spec.name = "AffinityFailure";
        spec.end_reason = RQ_END_AFFINITY_FAILED;
        spec.detail = "SetThreadGroupAffinity failed";
        spec.os_error = k_error_invalid_parameter;
        spec.prepared = false;
        spec.region_bytes = k_no_traffic_region_bytes;
        spec.pin = pin_failure::windows_error;
        break;
    case end_scenario::host_fault:
        // Step 2's gate aborts, so step 1 has touched every chunk before the fault.
        spec.name = "HostFault";
        spec.end_reason = RQ_END_HOST_FAULT;
        spec.detail = "Step gate aborted step 2 of 2";
        spec.abort_gate_at = 1;
        break;
    case end_scenario::engine_fault:
        spec.name = "EngineFault";
        spec.end_reason = RQ_END_INTERNAL_FAULT;
        spec.detail = k_injected_engine_fault;
        spec.prepared = false;
        spec.region_bytes = k_no_traffic_region_bytes;
        spec.pin = pin_failure::exception;
        break;
    }
    return spec;
}

void PrintTo(end_scenario scenario, std::ostream* os) {
    *os << spec_for(scenario).name;
}

class MemoryRegionEndReason : public ::testing::TestWithParam<end_scenario> {};

TEST_P(MemoryRegionEndReason, ReleasesEveryChunkBeforeReleasedIsRaised) {
    const scenario_spec spec = spec_for(GetParam());
    const std::string reason = end_reason_name(spec.end_reason);
    const std::uint32_t workers = session_worker_count();
    const std::uint32_t live_before = live_session_count();

    const std::vector<rq_step_def> steps = make_steps(k_session_step_count, spec.step_budget_ms);
    const rq_session_config config =
        make_config(steps, spec.region_bytes, workers, spec.stop_on_error);
    const region_plan planned = plan_region(config.region_bytes, workers);
    ASSERT_GT(planned.tested_bytes, 0u);

    address_capture capture;
    run_recorder recorder(capture);
    recorder.abort_gate_at(spec.abort_gate_at);
    const rq_callbacks callbacks = recorder.callbacks();

    std::optional<rq_fault_spec> fault;
    if (spec.inject_bit_flips) {
        fault = bit_flip_spec(config.region_bytes);
        const char* invalid = nullptr;
        ASSERT_TRUE(validate_fault_spec(*fault, config.region_bytes, &invalid)) << invalid;
    }
    std::shared_ptr<scripted_affinity> affinity;
    if (spec.pin != pin_failure::none) {
        affinity = std::make_shared<scripted_affinity>(spec.pin);
    }

    layer_handles layers;
    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks,
                             make_dependencies(capture, layers, fault,
                                               std::make_shared<qpc_clock>(), affinity),
                             session.out()),
              RQ_OK);
    ASSERT_NE(layers.counting, nullptr);
    ASSERT_EQ(layers.fault != nullptr, spec.inject_bit_flips);
    EXPECT_EQ(live_session_count(), live_before + 1);

    const std::uint64_t private_before = current_private_usage();
    ASSERT_GT(private_before, 0u);
    ASSERT_EQ(session_start(session.get()), RQ_OK);
    if (spec.host_stop != 0) {
        ASSERT_TRUE(recorder.wait_for_progress()) << reason << ": no progress within 30 s";
        ASSERT_EQ(session_stop(session.get(), spec.host_stop), RQ_OK);
    }
    ASSERT_TRUE(recorder.wait_for_released()) << reason << ": Released not raised within 30 s";
    const recorded_run run = recorder.snapshot();

    rq_run_result fetched{};
    fetched.struct_size = sizeof(rq_run_result);
    ASSERT_EQ(session_get_result(session.get(), &fetched), RQ_OK);
    // The layers die with the session, so their counters are read first.
    const std::uint64_t primitives = layers.counting->primitive_count();
    const std::uint64_t injections = layers.fault != nullptr ? layers.fault->injection_count() : 0;
    ASSERT_EQ(session.destroy(), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before) << reason << ": session not destroyed";

    // The run ended the way the scenario drives it.
    ASSERT_TRUE(run.completed) << reason << ": Released without Completed";
    ASSERT_EQ(run.result.end_reason, spec.end_reason)
        << "expected " << reason << ", got " << end_reason_name(run.result.end_reason) << ": "
        << run.result.end_detail;
    EXPECT_EQ(fetched.end_reason, run.result.end_reason);
    EXPECT_EQ(run.result.os_error, spec.os_error) << reason;
    ASSERT_NE(std::memchr(run.result.end_detail, '\0', sizeof(run.result.end_detail)), nullptr);
    const std::string detail = run.result.end_detail;
    if (spec.detail == nullptr) {
        EXPECT_TRUE(detail.empty()) << reason << ": " << detail;
    } else {
        EXPECT_NE(detail.find(spec.detail), std::string::npos) << reason << ": " << detail;
    }
    EXPECT_EQ(run.prepared, spec.prepared) << reason;

    // Released reports every byte that was allocated: the tested bytes once prepared, otherwise
    // the planned bytes that were allocated and locked before preparation failed.
    if (run.prepared) {
        EXPECT_EQ(run.prepared_info.tested_bytes, planned.tested_bytes) << reason;
        EXPECT_EQ(run.released_bytes, run.prepared_info.tested_bytes) << reason;
    } else {
        EXPECT_EQ(run.released_bytes, planned.tested_bytes) << reason;
    }
    EXPECT_GT(run.released_bytes, 0u) << reason;

    // Every chunk base the run touched was MEM_FREE inside on_released.
    for (const std::string& unfreed : run.unfreed) {
        ADD_FAILURE() << reason << ": " << unfreed;
    }
    EXPECT_EQ(capture.query_failures(), 0u) << reason;
    const std::vector<std::uintptr_t> bases = capture.bases();

    if (spec.prepared) {
        EXPECT_FALSE(bases.empty()) << reason << ": the run touched no chunk";
        EXPECT_GT(primitives, 0u) << reason;
    } else {
        // Preparation failed after the region was locked and before any traffic, so no chunk
        // base was captured; the commit charge shows the release instead.
        EXPECT_EQ(primitives, 0u) << reason << ": traffic ran on unpinned workers";
        EXPECT_TRUE(bases.empty()) << reason;
        ASSERT_NE(affinity, nullptr) << reason << ": preparation can fail only at the pin";
        const std::uint64_t at_pin = affinity->private_usage_at_pin();
        ASSERT_GT(at_pin, 0u) << reason << ": the failing pin did not run";
        EXPECT_GE(at_pin + k_private_usage_slack_bytes, private_before + planned.tested_bytes)
            << reason << ": the region was not committed when the pin failed";
        EXPECT_LE(run.private_usage_at_release, private_before + k_private_usage_slack_bytes)
            << reason << ": the commit charge did not return after release (before "
            << private_before << ", at release " << run.private_usage_at_release << ")";
    }

    switch (GetParam()) {
    case end_scenario::completed:
        EXPECT_EQ(bases.size(), std::size_t{workers} * planned.chunks_per_slice)
            << "a completed run touches every chunk";
        EXPECT_EQ(run.result.steps_completed, k_session_step_count);
        EXPECT_EQ(run.result.total_errors, 0u);
        break;
    case end_scenario::first_error:
        EXPECT_GE(run.result.total_errors, 1u);
        EXPECT_NE(run.result.first_error_ms, RQ_NO_FIRST_ERROR);
        EXPECT_GE(injections, 1u);
        break;
    case end_scenario::host_fault:
        EXPECT_EQ(run.gate_calls, 2u);
        EXPECT_EQ(run.result.step_result_count, 1u) << "an aborted gate gets no step result";
        break;
    default:
        break;
    }
}

INSTANTIATE_TEST_SUITE_P(MemoryRegionEndReasons, MemoryRegionEndReason,
                         ::testing::Values(end_scenario::completed, end_scenario::user_stop,
                                           end_scenario::first_error, end_scenario::thermal,
                                           end_scenario::affinity_failure, end_scenario::host_fault,
                                           end_scenario::engine_fault),
                         [](const ::testing::TestParamInfo<end_scenario>& info) {
                             return std::string(spec_for(info.param).name);
                         });

}  // namespace
}  // namespace rq::engine::test
