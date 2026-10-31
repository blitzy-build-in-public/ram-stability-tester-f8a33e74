/**
 * @file test_memory_region.cpp
 * @brief Region tests: the slice and chunk split, 2 MiB rounding, the shrink-and-retry
 *        sequence, real chunked allocation and locking, and release of the locked region on
 *        every end reason.
 *
 * - memory_region_plan and memory_region_retry: plan_region and next_retry_bytes as pure functions,
 *   against sizes worked out by hand, plus a whole retry sequence down to the minimum.
 * - memory_region_allocate and memory_region_refusal: memory_region over real memory without
 *   elevation. Every chunk is committed, every page is resident and locked (QueryWorkingSetEx),
 *   slices follow worker order in logical offsets, a scripted lock override drives
 *   shrink-and-retry and exhaustion deterministically, a stop before preparation allocates
 *   nothing, a stop during preparation keeps the chunks it locked, unexposed, until release()
 *   frees exactly them, and release() and the destructor leave every former chunk base MEM_FREE
 *   with the working-set limits restored.
 * - MemoryRegionLargePages: large-page chunks, skipped when the token lacks
 *   SeLockMemoryPrivilege, which nobody holds by default.
 * - memory_region_responsiveness: while allocate() prepares a 1 GiB region, over one worker and
 *   over one per logical processor, another thread that commits, touches and frees fresh memory
 *   every 10 ms keeps completing, and no probe takes longer than the 250 ms within which the UI
 *   must answer throughout a run.
 * - MemoryRegionRelease: whole sessions ending in each of completed, user stop, first error,
 *   thermal stop, insufficient memory, affinity failure, host fault and engine fault. Each raises
 *   Completed and then Released, once each and last. Released reports the tested (or planned)
 *   bytes, and every chunk base the run touched is MEM_FREE when Released is raised. That check
 *   runs inside on_released itself, before anything else in the process can reuse the freed
 *   address range. The two end reasons that fail at the pin, before any traffic, touch no chunk,
 *   so their release is shown by the commit charge instead: it is sampled inside the failing
 *   pin, after the region was committed, and again inside on_released. Insufficient memory comes
 *   from a scripted session lock override that locks every chunk of each attempt but the last,
 *   down to the minimum. allocate() frees each partial attempt itself, so Released reports no
 *   bytes; its release is shown by every chunk base the override saw being MEM_FREE inside
 *   on_released, by the commit charge there, and by the working-set limits being back to their
 *   values before start. A scripted free override
 *   refuses chunks: a refused chunk stays owned and committed, Released is withheld, and a chunk
 *   still refused when its region or session is destroyed passes to the process-wide owner
 *   until a later retry frees it and restores the working-set limits.
 *
 * Regions stay at or below 512 MiB and sessions use at most two workers, except in
 * memory_region_responsiveness, which locks 1 GiB, the application's smallest region. Locking up
 * to that size needs no elevation once the working set is raised, which memory_region does
 * itself.
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
#include <thread>
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

/** The "always fail" setting of the lock and free scripts. */
constexpr std::uint32_t k_always = UINT32_MAX;

/** Session regions: 64 MiB for runs with traffic, 32 MiB minimum. */
constexpr std::uint64_t k_session_region_bytes = 64 * k_mib;
constexpr std::uint64_t k_session_min_region_bytes = 32 * k_mib;

/**
 * Region of the two runs whose pin fails before any traffic. It is four times the commit-charge
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
 * counting_memory_access's observer, which runs on every worker concurrently, and of every chunk
 * a scripted lock override was asked to lock.
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

    /**
     * Adds a chunk's allocation base directly: a lock override receives the base VirtualAlloc
     * returned, so it needs no query. noexcept, as the lock override's contract requires.
     */
    void record(const void* allocation_base) noexcept {
        std::lock_guard<std::mutex> lock(mutex_);
        bases_.insert(address_of(allocation_base));
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

/**
 * The callbacks that order a run. Progress and error batches interleave freely, so they are left
 * out.
 */
enum class engine_event {
    prepared,
    step_starting,
    step_finished,
    completed,
    released,
};

void PrintTo(engine_event event, std::ostream* os) {
    switch (event) {
    case engine_event::prepared:
        *os << "prepared";
        return;
    case engine_event::step_starting:
        *os << "step_starting";
        return;
    case engine_event::step_finished:
        *os << "step_finished";
        return;
    case engine_event::completed:
        *os << "completed";
        return;
    case engine_event::released:
        *os << "released";
        return;
    }
    *os << "unknown event";
}

/** What run_recorder keeps under its lock besides its counters and flags. */
struct run_payload {
    /** The ordering callbacks in the order they were raised. */
    std::vector<engine_event> events;
    rq_prepared prepared_info{};
    /** The on_completed header; step_results is cleared, the engine owns that array. */
    rq_run_result result{};
    std::uint64_t released_bytes = 0;
    std::uint64_t release_ms = 0;
    /** Commit charge sampled inside on_released. */
    std::uint64_t private_usage_at_release = 0;
    /** One description per captured base that was not MEM_FREE inside on_released. */
    std::vector<std::string> unfreed;
};

/** What run_recorder saw, its payload and its counters and flags, copied under its lock. */
struct recorded_run : run_payload {
    bool prepared = false;
    std::uint32_t gate_calls = 0;
    std::uint32_t steps_finished = 0;
    std::uint32_t progress_calls = 0;
    std::uint64_t error_total = 0;
    bool completed = false;
    bool released = false;
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

    /**
     * Makes every step gate wait, for at most k_event_timeout, until partner has seen Prepared
     * or Completed, so two sessions hold their regions at once before either runs a step. Call
     * before the session starts.
     */
    void hold_gate_for(run_recorder& partner) noexcept { partner_ = &partner; }

    bool wait_for_progress() {
        return wait_until([this] {
            return progress_calls_.load(std::memory_order_relaxed) > 0 ||
                   completed_.load(std::memory_order_relaxed);
        });
    }

    bool wait_for_released() {
        return wait_until([this] { return released_.load(std::memory_order_relaxed); });
    }

    bool wait_for_completed() {
        return wait_until([this] { return completed_.load(std::memory_order_relaxed); });
    }

    /** Waits at most timeout for Released; false when it has not been raised by then. */
    bool released_within(std::chrono::milliseconds timeout) {
        return wait_until([this] { return released_.load(std::memory_order_relaxed); }, timeout);
    }

    recorded_run snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        recorded_run run{payload_};
        run.prepared = prepared_.load(std::memory_order_relaxed);
        run.gate_calls = gate_calls_.load(std::memory_order_relaxed);
        run.steps_finished = steps_finished_.load(std::memory_order_relaxed);
        run.progress_calls = progress_calls_.load(std::memory_order_relaxed);
        run.error_total = error_total_.load(std::memory_order_relaxed);
        run.completed = completed_.load(std::memory_order_relaxed);
        run.released = released_.load(std::memory_order_relaxed);
        return run;
    }

private:
    static run_recorder& self(void* user_data) noexcept {
        return *static_cast<run_recorder*>(user_data);
    }

    template <class Predicate>
    bool wait_until(Predicate done, std::chrono::milliseconds timeout = k_event_timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, done);
    }

    template <class Update>
    void update(Update apply) noexcept {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            apply(payload_);
        }
        changed_.notify_all();
    }

    static void RQ_CALL on_prepared(void* user_data, const rq_prepared* info) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder, info](run_payload& run) {
            run.events.push_back(engine_event::prepared);
            recorder.prepared_.store(true, std::memory_order_relaxed);
            run.prepared_info = *info;
        });
    }

    static std::uint32_t RQ_CALL on_step_starting(void* user_data,
                                                  const rq_step_start* info) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder](run_payload& run) {
            run.events.push_back(engine_event::step_starting);
            recorder.gate_calls_.fetch_add(1, std::memory_order_relaxed);
        });
        if (recorder.partner_ != nullptr) {
            run_recorder& partner = *recorder.partner_;
            partner.wait_until([&partner] {
                return partner.prepared_.load(std::memory_order_relaxed) ||
                       partner.completed_.load(std::memory_order_relaxed);
            });
        }
        return info->index == recorder.abort_at_ ? RQ_ACTION_ABORT : RQ_ACTION_CONTINUE;
    }

    static void RQ_CALL on_step_finished(void* user_data, const rq_step_result*) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder](run_payload& run) {
            run.events.push_back(engine_event::step_finished);
            recorder.steps_finished_.fetch_add(1, std::memory_order_relaxed);
        });
    }

    static void RQ_CALL on_progress(void* user_data, const rq_progress*) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder](run_payload&) {
            recorder.progress_calls_.fetch_add(1, std::memory_order_relaxed);
        });
    }

    static void RQ_CALL on_errors(void* user_data, const rq_error_record*, std::uint32_t,
                                  std::uint64_t total_errors) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder, total_errors](run_payload&) {
            recorder.error_total_.store(
                std::max(recorder.error_total_.load(std::memory_order_relaxed), total_errors),
                std::memory_order_relaxed);
        });
    }

    static void RQ_CALL on_completed(void* user_data, const rq_run_result* result) noexcept {
        run_recorder& recorder = self(user_data);
        recorder.update([&recorder, result](run_payload& run) {
            run.events.push_back(engine_event::completed);
            recorder.completed_.store(true, std::memory_order_relaxed);
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
        recorder.update([&](run_payload& run) {
            run.events.push_back(engine_event::released);
            recorder.released_.store(true, std::memory_order_relaxed);
            run.released_bytes = released_bytes;
            run.release_ms = release_ms;
            run.private_usage_at_release = private_usage;
            run.unfreed = std::move(unfreed);
        });
    }

    const address_capture& capture_;
    std::uint32_t abort_at_ = k_no_step;
    run_recorder* partner_ = nullptr;
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    run_payload payload_;
    // Written and read only under mutex_, which orders them, so every access is relaxed.
    std::atomic<bool> prepared_{false};
    std::atomic<std::uint32_t> gate_calls_{0};
    std::atomic<std::uint32_t> steps_finished_{0};
    std::atomic<std::uint32_t> progress_calls_{0};
    std::atomic<std::uint64_t> error_total_{0};
    std::atomic<bool> completed_{false};
    std::atomic<bool> released_{false};
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
class scripted_affinity final : public thread_affinity {
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
    std::shared_ptr<thread_affinity> real_;
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
                                       std::shared_ptr<thread_affinity> affinity) {
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

class memory_region_plan : public ::testing::TestWithParam<plan_case> {};

TEST_P(memory_region_plan, SplitsSlicesIntoChunksRoundedToTwoMib) {
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
    MemoryRegionPlanCases, memory_region_plan,
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

class memory_region_retry : public ::testing::TestWithParam<retry_case> {};

TEST_P(memory_region_retry, ShrinksTenPercentRoundedToTheWorkerUnit) {
    const retry_case& expected = GetParam();
    EXPECT_EQ(next_retry_bytes(expected.attempted_bytes, expected.min_region_bytes,
                               expected.worker_count),
              expected.expected_bytes);
}

INSTANTIATE_TEST_SUITE_P(
    MemoryRegionRetryCases, memory_region_retry,
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

class memory_region_allocate : public ::testing::TestWithParam<allocation_case> {};

TEST_P(memory_region_allocate, LocksEveryChunkOfEverySliceAndReleasesThem) {
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

INSTANTIATE_TEST_SUITE_P(MemoryRegionAllocateCases, memory_region_allocate,
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
    EXPECT_NE(detail.find("at 32 MiB"), std::string::npos) << "names the last size: " << detail;
    EXPECT_NE(detail.find("32 MiB minimum"), std::string::npos) << detail;
    // The error is os_error's alone: the host prefixes "Windows error n: " to the detail.
    EXPECT_EQ(detail.find("Windows error"), std::string::npos) << detail;
    EXPECT_EQ(detail.find("1453"), std::string::npos) << detail;

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

class memory_region_refusal : public ::testing::TestWithParam<refusal_case> {};

TEST_P(memory_region_refusal, RefusesBeforeAnyAllocation) {
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
    MemoryRegionRefusalCases, memory_region_refusal,
    ::testing::Values(
        refusal_case{"NoTwoMibSlicePerWorker", 3 * k_mib, 2 * k_mib, 2,
                     "cannot give each of 2 workers a 2 MiB slice"},
        // 34 MiB over 4 workers plans 8 MiB slices: 32 MiB tested, below the 34 MiB minimum.
        refusal_case{"PlanBelowTheMinimum", 34 * k_mib, 34 * k_mib, 4,
                     "tests 32 MiB, below the 34 MiB minimum"}),
    [](const ::testing::TestParamInfo<refusal_case>& info) {
        return std::string(info.param.name);
    });

/**
 * A first size above physical memory, as a function of the machine's total physical bytes, so
 * the bound is exercised whatever the machine holds.
 */
struct physical_bound_case {
    const char* name;
    std::uint32_t worker_count;
    std::uint64_t (*region_bytes)(std::uint64_t total_phys_bytes);
};

void PrintTo(const physical_bound_case& value, std::ostream* os) {
    *os << value.name;
}

class memory_region_refusal_above_physical_memory
    : public ::testing::TestWithParam<physical_bound_case> {};

TEST_P(memory_region_refusal_above_physical_memory, RefusesBeforeReservingTheChunkList) {
    // The refusal makes one query and allocates nothing, so this limit is generous.
    constexpr long long k_refusal_limit_ms = 1000;
    const physical_bound_case& request = GetParam();

    MEMORYSTATUSEX memory_status{};
    memory_status.dwLength = sizeof(memory_status);
    ASSERT_TRUE(GlobalMemoryStatusEx(&memory_status))
        << "GlobalMemoryStatusEx failed (Windows error " << GetLastError() << ")";
    const std::uint64_t total_phys = memory_status.ullTotalPhys;
    const std::uint64_t region_bytes = request.region_bytes(total_phys);
    ASSERT_GT(region_bytes, total_phys);
    const region_plan plan = plan_region(region_bytes, request.worker_count);
    if (plan.slice_bytes != 0) {
        ASSERT_GT(plan.tested_bytes, total_phys) << "the plan itself must exceed physical memory";
    }

    // The request is rounded up and physical memory down to whole MiB, as the engine does.
    char expected_detail[RQ_END_DETAIL_SIZE];
    std::snprintf(expected_detail, sizeof(expected_detail),
                  "Region of %llu MiB exceeds the %llu MiB of physical memory",
                  region_bytes / k_mib + (region_bytes % k_mib != 0 ? 1u : 0u), total_phys / k_mib);

    const working_set_limits limits_before = current_working_set_limits();
    stop_signal stop;
    // Every lock fails, so even an engine that went on to shrink and retry would lock nothing.
    lock_script script(k_always);
    memory_region region;
    const std::uint64_t private_before = current_private_usage();
    ASSERT_NE(private_before, 0u) << "GetProcessMemoryInfo failed";
    const auto started = std::chrono::steady_clock::now();
    const allocation_outcome outcome = region.allocate(
        make_options(region_bytes, k_session_min_region_bytes, request.worker_count, script.bind()),
        stop);
    const long long elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                     std::chrono::steady_clock::now() - started)
                                     .count();
    // Read while the region still exists, so a reserved chunk list would still be committed.
    const std::uint64_t private_after = current_private_usage();
    const std::uint64_t private_growth =
        private_after > private_before ? private_after - private_before : 0;

    ASSERT_EQ(outcome.status, allocation_status::insufficient_memory)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    EXPECT_EQ(outcome.attempts, 1u);
    EXPECT_EQ(outcome.os_error, 0u) << "no OS call failed";
    EXPECT_EQ(outcome.large_page_fallbacks, 0u);
    EXPECT_STREQ(outcome.end_detail, expected_detail);
    EXPECT_EQ(script.calls(), 0u) << "nothing was allocated or locked";
    EXPECT_LT(elapsed_ms, k_refusal_limit_ms);
    EXPECT_LE(private_growth, k_private_usage_slack_bytes)
        << "the commit charge grew by " << private_growth / k_mib << " MiB";
    expect_unallocated(region, "after a request above physical memory");
    expect_limits_restored(limits_before, "after a request above physical memory");
}

INSTANTIATE_TEST_SUITE_P(
    MemoryRegionRefusalAbovePhysicalMemoryCases, memory_region_refusal_above_physical_memory,
    ::testing::Values(
        // The largest plannable slice, 2^32 - 2 chunks of 256 MiB: nearly 1 EiB, whose chunk list
        // alone would need over 100 GiB.
        physical_bound_case{"NearOneEibOnOneWorker", 1,
                            [](std::uint64_t) -> std::uint64_t {
                                return (std::uint64_t{UINT32_MAX} - 1) * k_chunk_bytes;
                            }},
        // The first multiple of the 6 MiB unit of 3 workers above physical memory, so the plan
        // tests all of it.
        physical_bound_case{"JustAbovePhysicalMemory", 3,
                            [](std::uint64_t total_phys_bytes) -> std::uint64_t {
                                constexpr std::uint64_t k_unit = 3 * k_slice_granularity_bytes;
                                return (total_phys_bytes / k_unit + 1) * k_unit;
                            }},
        // A slice of 2^60 bytes or more has no plan, so the request itself is compared.
        physical_bound_case{"TooLargeToPlan", 1,
                            [](std::uint64_t) -> std::uint64_t { return UINT64_MAX; }}),
    [](const ::testing::TestParamInfo<physical_bound_case>& info) {
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
    EXPECT_EQ(region.release(), 0u) << "nothing was allocated";
}

TEST(MemoryRegionStop, StopDuringPreparationKeepsLockedChunksUntilRelease) {
    // Four 16 MiB slices of one chunk each; the stop arrives while chunk 2 is locked, so the
    // check before chunk 3 sees it. allocate() returns at once, holding chunks 1 and 2, so
    // that the coordinator can raise Completed before it releases them.
    constexpr std::uint32_t k_workers = 4;
    constexpr std::uint32_t k_stop_on_call = 2;
    constexpr std::uint64_t k_slice_bytes = 16 * k_mib;
    constexpr std::uint64_t k_locked_bytes = k_stop_on_call * k_slice_bytes;
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
    const std::vector<const void*> bases = script.bases();

    // Held but never exposed as a usable region.
    expect_unallocated(region, "after a stop during preparation");
    for (const void* base : bases) {
        MEMORY_BASIC_INFORMATION info{};
        ASSERT_EQ(VirtualQuery(base, &info, sizeof(info)), sizeof(info));
        EXPECT_EQ(info.AllocationBase, base) << "one allocation per chunk";
        EXPECT_EQ(info.State, static_cast<DWORD>(MEM_COMMIT))
            << "a chunk locked before the stop was freed before release(): "
            << memory_state_name(info.State);
        EXPECT_TRUE(pages_locked(chunk_view{static_cast<std::uint64_t*>(const_cast<void*>(base)),
                                            k_slice_bytes, 0, false}))
            << "a chunk locked before the stop was unlocked before release()";
    }
    EXPECT_GE(static_cast<std::uint64_t>(current_working_set_limits().minimum), k_locked_bytes)
        << "the working-set raise covering the held chunks was undone before release()";

    EXPECT_EQ(region.release(), k_locked_bytes) << "release() frees exactly the held chunks";
    EXPECT_EQ(region.release(), 0u) << "release() is idempotent";
    for (const void* base : bases) {
        expect_freed(base, "after release() of a stopped region");
    }
    expect_unallocated(region, "after release() of a stopped region");
    expect_limits_restored(limits_before, "after release() of a stopped region");
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

/** The Windows error the scripted free refusals report. */
constexpr std::uint32_t k_error_invalid_address = 487;
static_assert(k_error_invalid_address == ERROR_INVALID_ADDRESS, "Windows error 487");

/** How long a session test watches for a Released that must be withheld. */
constexpr std::chrono::milliseconds k_released_withheld_wait{1000};

/**
 * Stands in for VirtualFree through region_options::free_override and
 * session_dependencies::free_override.
 *
 * The first chunk it is asked to free is refused up to refusals times (every time for k_always,
 * until stop_refusing()): the call leaves the chunk allocated and returns false with
 * ERROR_INVALID_ADDRESS as the last error, exactly as a failed VirtualFree reports it. Every
 * other call frees for real. A session calls it from its coordinator and later from the
 * destroying thread, and the owner of orphaned chunks from whichever thread retries them, so
 * the record is guarded.
 */
class free_script {
public:
    explicit free_script(std::uint32_t refusals) : refusals_(refusals) {}

    /**
     * Stops refusing and retries the orphaned chunks, so no chunk a destroyed region handed to
     * the process-wide owner keeps calling this script once it is gone, even after a failed
     * assertion ended the test early, and later tests start with nothing orphaned.
     */
    ~free_script() {
        stop_refusing();
        retry_orphaned_chunks();
    }

    free_script(const free_script&) = delete;
    free_script& operator=(const free_script&) = delete;

    /** Makes every later call free for real. */
    void stop_refusing() {
        std::lock_guard<std::mutex> lock(mutex_);
        refusals_.store(refused_.load(std::memory_order_relaxed), std::memory_order_relaxed);
    }

    free_function bind() {
        return [this](void* address) { return release_chunk(address); };
    }

    std::uint32_t calls() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return calls_.load(std::memory_order_relaxed);
    }

    std::uint32_t refusals_made() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return refused_.load(std::memory_order_relaxed);
    }

    /** The chunk whose free is refused: the first one asked for; null before any call. */
    const void* refused_base() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return refused_base_;
    }

    /** Waits, for at most k_event_timeout, until count calls have been made. */
    bool wait_for_calls(std::uint32_t count) {
        std::unique_lock<std::mutex> lock(mutex_);
        return called_.wait_for(lock, k_event_timeout,
                                [&] { return calls_.load(std::memory_order_relaxed) >= count; });
    }

private:
    bool release_chunk(void* address) noexcept {
        bool refuse = false;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            calls_.fetch_add(1, std::memory_order_relaxed);
            if (refused_base_ == nullptr) {
                refused_base_ = address;
            }
            if (address == refused_base_ && refused_.load(std::memory_order_relaxed) <
                                                refusals_.load(std::memory_order_relaxed)) {
                refused_.fetch_add(1, std::memory_order_relaxed);
                refuse = true;
            }
        }
        called_.notify_all();
        if (refuse) {
            // Set after the lock is dropped, so nothing can overwrite it before the region reads
            // it.
            SetLastError(k_error_invalid_address);
            return false;
        }
        return VirtualFree(address, 0, MEM_RELEASE) != FALSE;
    }

    // The counts and the refusal limit stop_refusing() lowers are written and read only under
    // mutex_, which orders them, so every access is relaxed.
    std::atomic<std::uint32_t> refusals_;
    mutable std::mutex mutex_;
    std::condition_variable called_;
    std::atomic<std::uint32_t> calls_{0};
    std::atomic<std::uint32_t> refused_{0};
    const void* refused_base_ = nullptr;
};

/** Success when VirtualQuery reports base as the start of a committed allocation. */
::testing::AssertionResult is_committed_allocation(const void* base) {
    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(base, &info, sizeof(info)) != sizeof(info)) {
        return ::testing::AssertionFailure() << "VirtualQuery(" << hex_address(address_of(base))
                                             << ") failed (Windows error " << GetLastError() << ")";
    }
    if (info.AllocationBase != base || info.State != MEM_COMMIT) {
        return ::testing::AssertionFailure()
               << "address " << hex_address(address_of(base))
               << " is not a committed allocation: " << memory_state_name(info.State)
               << ", allocation base " << hex_address(address_of(info.AllocationBase));
    }
    return ::testing::AssertionSuccess();
}

TEST(MemoryRegionRelease, KeepsAChunkWindowsRefusesToFreeUntilALaterReleaseFreesIt) {
    // Two 32 MiB slices of one chunk each. The first chunk's free is refused twice: by the first
    // release() and by the release() a repeated allocate() runs first.
    constexpr std::uint64_t k_region = 64 * k_mib;
    constexpr std::uint64_t k_chunk = 32 * k_mib;
    const working_set_limits limits_before = current_working_set_limits();

    free_script frees(2);
    stop_signal stop;
    memory_region region;
    region_options options = make_options(k_region, k_chunk, 2);
    options.free_override = frees.bind();
    const allocation_outcome outcome = region.allocate(options, stop);
    ASSERT_EQ(outcome.status, allocation_status::ok)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    const std::vector<const void*> bases = chunk_bases(region);
    ASSERT_EQ(bases.size(), 2u);
    EXPECT_EQ(region.held_bytes(), k_region);

    // The refused chunk stays committed and owned, with the raise that covers it; only the other
    // chunk's bytes count as released.
    EXPECT_EQ(region.release(), k_chunk) << "release() reports only the bytes it freed";
    ASSERT_EQ(frees.refused_base(), bases[0]) << "chunks are freed in logical order";
    EXPECT_EQ(region.held_bytes(), k_chunk);
    expect_unallocated(region, "with a refused chunk held");
    EXPECT_TRUE(is_committed_allocation(bases[0])) << "the refused chunk was dropped or freed";
    expect_freed(bases[1], "the chunk whose free succeeded");
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              k_region + k_working_set_headroom_bytes)
        << "the working-set raise was undone while a chunk is held";

    // A repeated allocate() retries the held chunk first and, refused again, plans nothing over
    // it; the override of the earlier call is kept for that chunk.
    lock_script locks(0);
    const allocation_outcome refused =
        region.allocate(make_options(k_region, k_chunk, 2, locks.bind()), stop);
    ASSERT_EQ(refused.status, allocation_status::insufficient_memory)
        << status_name(refused.status);
    EXPECT_EQ(refused.attempts, 0u);
    EXPECT_EQ(refused.os_error, k_error_invalid_address);
    const std::string detail = refused.end_detail;
    EXPECT_NE(detail.find("VirtualFree"), std::string::npos) << detail;
    EXPECT_NE(detail.find("32 MiB"), std::string::npos) << "names the held bytes: " << detail;
    EXPECT_EQ(locks.calls(), 0u) << "a chunk was locked over the held one";
    EXPECT_EQ(frees.refusals_made(), 2u);
    EXPECT_EQ(region.held_bytes(), k_chunk);
    EXPECT_TRUE(is_committed_allocation(bases[0]));

    // The next release() frees it and only then restores the limits.
    EXPECT_EQ(region.release(), k_chunk);
    EXPECT_EQ(region.held_bytes(), 0u);
    EXPECT_EQ(frees.calls(), 4u) << "two chunks, then the held one twice";
    expect_freed(bases[0], "after the retried release()");
    expect_unallocated(region, "after the retried release()");
    expect_limits_restored(limits_before, "after the retried release()");
    EXPECT_EQ(region.release(), 0u) << "release() is idempotent";
}

TEST(MemoryRegionRelease, EndsTheRetriesWhenAFailedAttemptsChunkCannotBeFreed) {
    // The first chunk's lock fails, so the attempt's cleanup frees that unlocked chunk, and the
    // free is refused once: no retry is planned over the held chunk.
    constexpr std::uint64_t k_region = 64 * k_mib;
    constexpr std::uint64_t k_chunk = 32 * k_mib;
    const working_set_limits limits_before = current_working_set_limits();

    free_script frees(1);
    lock_script locks(1);
    stop_signal stop;
    memory_region region;
    region_options options = make_options(k_region, k_chunk, 2, locks.bind());
    options.free_override = frees.bind();
    const allocation_outcome outcome = region.allocate(options, stop);
    ASSERT_EQ(outcome.status, allocation_status::insufficient_memory)
        << status_name(outcome.status);
    EXPECT_EQ(outcome.attempts, 1u) << "a retry was planned over a held chunk";
    EXPECT_EQ(outcome.os_error, k_error_invalid_address);
    const std::string detail = outcome.end_detail;
    EXPECT_NE(detail.find("VirtualFree"), std::string::npos) << detail;
    EXPECT_EQ(locks.calls(), 1u);
    ASSERT_EQ(locks.bases().size(), 1u);
    const void* const base = locks.bases()[0];
    EXPECT_EQ(frees.refused_base(), base) << "the chunk whose lock failed is freed by the cleanup";
    EXPECT_EQ(region.held_bytes(), k_chunk);
    expect_unallocated(region, "after a failed attempt left a chunk held");
    EXPECT_TRUE(is_committed_allocation(base)) << "the refused chunk was dropped or freed";
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              k_region + k_working_set_headroom_bytes)
        << "the working-set raise was undone while a chunk is held";

    EXPECT_EQ(region.release(), k_chunk);
    EXPECT_EQ(region.held_bytes(), 0u);
    expect_freed(base, "after release() of the held chunk");
    expect_limits_restored(limits_before, "after release() of the held chunk");
}

TEST(MemoryRegionRelease, DestructorHandsAChunkStillRefusedToTheProcessWideOwner) {
    // Two 32 MiB slices of one chunk each. The first chunk's free is refused on every call until
    // the script stops refusing, so the region is destroyed still holding it.
    constexpr std::uint64_t k_region = 64 * k_mib;
    constexpr std::uint64_t k_chunk = 32 * k_mib;
    ASSERT_EQ(orphaned_bytes(), 0u) << "an earlier test left orphaned chunks";
    const working_set_limits limits_before = current_working_set_limits();

    free_script frees(k_always);
    std::vector<const void*> bases;
    {
        stop_signal stop;
        memory_region region;
        region_options options = make_options(k_region, k_chunk, 2);
        options.free_override = frees.bind();
        const allocation_outcome outcome = region.allocate(options, stop);
        ASSERT_EQ(outcome.status, allocation_status::ok)
            << status_name(outcome.status) << ": " << outcome.end_detail;
        bases = chunk_bases(region);
        ASSERT_EQ(bases.size(), 2u);
    }

    // The destructor's release() freed the second chunk and was refused the first, which
    // outlives the region under the process-wide owner: still committed, and still covered by
    // the region's working-set raise.
    ASSERT_EQ(frees.refused_base(), bases[0]) << "chunks are freed in logical order";
    EXPECT_EQ(frees.calls(), 2u);
    EXPECT_EQ(orphaned_bytes(), k_chunk);
    EXPECT_TRUE(is_committed_allocation(bases[0])) << "the refused chunk was dropped or freed";
    expect_freed(bases[1], "the chunk whose free succeeded");
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              k_region + k_working_set_headroom_bytes)
        << "the working-set raise was undone while an orphaned chunk is held";

    // An explicit retry that Windows still refuses keeps the chunk orphaned.
    retry_orphaned_chunks();
    EXPECT_EQ(frees.calls(), 3u);
    EXPECT_EQ(orphaned_bytes(), k_chunk);
    EXPECT_TRUE(is_committed_allocation(bases[0])) << "a refused retry dropped or freed the chunk";

    // Once Windows allows it, the release() of any later region frees it, with the override the
    // destroyed region kept, without counting it as that region's, and restores the limits.
    frees.stop_refusing();
    memory_region later;
    EXPECT_EQ(later.release(), 0u) << "the orphaned bytes were counted as the later region's";
    EXPECT_EQ(frees.calls(), 4u);
    EXPECT_EQ(orphaned_bytes(), 0u);
    expect_freed(bases[0], "after a later region's release()");
    expect_limits_restored(limits_before, "after a later region's release()");
}

TEST(MemoryRegionRelease, SessionWithholdsReleasedUntilDestroyFreesARefusedChunk) {
    const std::uint32_t workers = session_worker_count();
    const std::uint32_t live_before = live_session_count();
    const working_set_limits limits_before = current_working_set_limits();

    const std::vector<rq_step_def> steps = make_steps(1, k_short_step_ms);
    const rq_session_config config = make_config(steps, k_session_region_bytes, workers, 0);
    const region_plan planned = plan_region(config.region_bytes, workers);
    const std::uint32_t chunk_count = workers * planned.chunks_per_slice;
    ASSERT_GT(chunk_count, 0u);

    address_capture capture;
    run_recorder recorder(capture);
    const rq_callbacks callbacks = recorder.callbacks();
    free_script frees(1);
    layer_handles layers;
    session_dependencies dependencies =
        make_dependencies(capture, layers, std::nullopt, std::make_shared<qpc_clock>(), nullptr);
    dependencies.free_override = frees.bind();

    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), session.out()), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before + 1);
    ASSERT_EQ(session_start(session.get()), RQ_OK);
    ASSERT_TRUE(recorder.wait_for_completed()) << "Completed not raised within 30 s";

    // The coordinator's release after Completed asks for every chunk once and is refused one,
    // which the session then still holds.
    ASSERT_TRUE(frees.wait_for_calls(chunk_count)) << "the coordinator released no chunk";
    const void* const refused = frees.refused_base();
    ASSERT_NE(refused, nullptr);
    EXPECT_TRUE(is_committed_allocation(refused)) << "the refused chunk was dropped or freed";
    // The coordinator raises Released right after that release, if at all, so its absence for
    // this long shows it withheld rather than not yet reached; destroying the session sooner
    // would suppress it either way.
    EXPECT_FALSE(recorder.released_within(k_released_withheld_wait))
        << "Released confirmed a release that left a chunk allocated";

    // Destroying the session retries the held chunk, and no callback follows it.
    ASSERT_EQ(session.destroy(), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before) << "session not destroyed";
    const recorded_run run = recorder.snapshot();
    ASSERT_TRUE(run.completed);
    EXPECT_EQ(run.result.end_reason, RQ_END_COMPLETED)
        << end_reason_name(run.result.end_reason) << ": " << run.result.end_detail;
    EXPECT_TRUE(run.prepared);
    EXPECT_FALSE(run.released) << "Released confirmed a release that left a chunk allocated";
    EXPECT_EQ(frees.refusals_made(), 1u);
    EXPECT_EQ(frees.calls(), chunk_count + 1) << "every chunk once, then the held one again";
    expect_freed(refused, "after session_destroy");
    for (const std::uintptr_t base : capture.bases()) {
        EXPECT_TRUE(is_freed(base)) << "after session_destroy";
    }
    expect_limits_restored(limits_before, "after session_destroy");
}

TEST(MemoryRegionRelease, SessionDestroyHandsAChunkStillRefusedToTheProcessWideOwner) {
    const std::uint32_t workers = session_worker_count();
    const std::uint32_t live_before = live_session_count();
    ASSERT_EQ(orphaned_bytes(), 0u) << "an earlier test left orphaned chunks";
    const working_set_limits limits_before = current_working_set_limits();

    const std::vector<rq_step_def> steps = make_steps(1, k_short_step_ms);
    const rq_session_config config = make_config(steps, k_session_region_bytes, workers, 0);
    const region_plan planned = plan_region(config.region_bytes, workers);
    const std::uint32_t chunk_count = workers * planned.chunks_per_slice;
    ASSERT_GT(chunk_count, 0u);
    // The refused chunk is the first one released: slice 0's first chunk.
    const std::uint64_t refused_bytes =
        planned.full_chunks_per_slice != 0 ? k_chunk_bytes : planned.final_chunk_bytes;

    address_capture capture;
    run_recorder recorder(capture);
    const rq_callbacks callbacks = recorder.callbacks();
    free_script frees(k_always);
    layer_handles layers;
    session_dependencies dependencies =
        make_dependencies(capture, layers, std::nullopt, std::make_shared<qpc_clock>(), nullptr);
    dependencies.free_override = frees.bind();

    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), session.out()), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before + 1);
    ASSERT_EQ(session_start(session.get()), RQ_OK);
    ASSERT_TRUE(recorder.wait_for_completed()) << "Completed not raised within 30 s";
    ASSERT_TRUE(frees.wait_for_calls(chunk_count)) << "the coordinator released no chunk";
    EXPECT_FALSE(recorder.released_within(k_released_withheld_wait))
        << "Released confirmed a release that left a chunk allocated";

    // Destroying the session retries the chunk and, refused every time, hands it to the
    // process-wide owner; the call still succeeds and no callback follows it.
    ASSERT_EQ(session.destroy(), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before) << "session not destroyed";
    const recorded_run run = recorder.snapshot();
    ASSERT_TRUE(run.completed);
    EXPECT_EQ(run.result.end_reason, RQ_END_COMPLETED)
        << end_reason_name(run.result.end_reason) << ": " << run.result.end_detail;
    EXPECT_FALSE(run.released) << "Released confirmed a release that left a chunk allocated";
    const void* const refused = frees.refused_base();
    ASSERT_NE(refused, nullptr);
    const std::uint32_t calls_at_destroy = frees.calls();
    EXPECT_GT(calls_at_destroy, chunk_count) << "session_destroy did not retry the held chunk";
    EXPECT_EQ(frees.refusals_made(), calls_at_destroy - (chunk_count - 1))
        << "only the refused chunk is asked for again";
    EXPECT_EQ(orphaned_bytes(), refused_bytes);
    EXPECT_TRUE(is_committed_allocation(refused)) << "the refused chunk was dropped or freed";
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              planned.tested_bytes + k_working_set_headroom_bytes)
        << "the working-set raise was undone while an orphaned chunk is held";

    // Once Windows allows it, a retry frees the chunk with the session's override and restores
    // the limits.
    frees.stop_refusing();
    retry_orphaned_chunks();
    EXPECT_EQ(frees.calls(), calls_at_destroy + 1);
    EXPECT_EQ(orphaned_bytes(), 0u);
    expect_freed(refused, "after retry_orphaned_chunks()");
    for (const std::uintptr_t base : capture.bases()) {
        EXPECT_TRUE(is_freed(base)) << "after retry_orphaned_chunks()";
    }
    expect_limits_restored(limits_before, "after retry_orphaned_chunks()");
}

/**
 * Regions of two sessions at once: each larger than the working-set headroom, so a minimum
 * that covered only one region could not also hold the other's locked pages, and each with its
 * minimum equal to its size, so no shrink can hide a shortfall.
 */
constexpr std::uint64_t k_concurrent_region_bytes = 512 * k_mib;
static_assert(k_concurrent_region_bytes > k_working_set_headroom_bytes,
              "each concurrent region must exceed the shared headroom");

TEST(MemoryRegionRelease, ConcurrentRegionsAddTheirWorkingSetRaises) {
    constexpr std::uint64_t k_region = k_concurrent_region_bytes;
    constexpr std::uint32_t k_workers = 2;
    const working_set_limits limits_before = current_working_set_limits();

    stop_signal stop;
    memory_region first;
    memory_region second;
    const allocation_outcome first_outcome =
        first.allocate(make_options(k_region, k_region, k_workers), stop);
    ASSERT_EQ(first_outcome.status, allocation_status::ok)
        << status_name(first_outcome.status) << ": " << first_outcome.end_detail;
    const allocation_outcome second_outcome =
        second.allocate(make_options(k_region, k_region, k_workers), stop);
    ASSERT_EQ(second_outcome.status, allocation_status::ok)
        << status_name(second_outcome.status) << ": " << second_outcome.end_detail;
    EXPECT_EQ(second_outcome.attempts, 1u);
    EXPECT_EQ(first.tested_bytes(), k_region);
    EXPECT_EQ(second.tested_bytes(), k_region);
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              2 * k_region + k_working_set_headroom_bytes)
        << "the minimum is not the sum of both raises plus one headroom";
    for (const memory_region* region : {&first, &second}) {
        for (const chunk_view& chunk : region->chunks()) {
            EXPECT_TRUE(pages_locked(chunk)) << "with both regions locked";
        }
    }

    // Releasing the first lowers the minimum to the second's raise, whose pages stay locked.
    const std::vector<const void*> first_bases = chunk_bases(first);
    EXPECT_EQ(first.release(), k_region);
    for (const void* base : first_bases) {
        expect_freed(base, "after release() of the first region");
    }
    EXPECT_EQ(static_cast<std::uint64_t>(current_working_set_limits().minimum),
              k_region + k_working_set_headroom_bytes)
        << "the first region's raise was not withdrawn alone";
    for (const chunk_view& chunk : second.chunks()) {
        EXPECT_TRUE(pages_locked(chunk)) << "after release() of the first region";
    }

    // Releasing the last restores the limits in force before either raise.
    const std::vector<const void*> second_bases = chunk_bases(second);
    EXPECT_EQ(second.release(), k_region);
    for (const void* base : second_bases) {
        expect_freed(base, "after release() of both regions");
    }
    expect_limits_restored(limits_before, "after release() of both regions");
}

TEST(MemoryRegionRelease, ConcurrentSessionsEachLockTheirWholeRegion) {
    const std::uint32_t workers = session_worker_count();
    const std::uint32_t live_before = live_session_count();
    const working_set_limits limits_before = current_working_set_limits();

    const std::vector<rq_step_def> steps = make_steps(1, k_short_step_ms);
    rq_session_config config = make_config(steps, k_concurrent_region_bytes, workers, 0);
    config.min_region_bytes = k_concurrent_region_bytes;
    ASSERT_EQ(plan_region(config.region_bytes, workers).tested_bytes, k_concurrent_region_bytes);

    // Each gate waits for the other session's Prepared, so both regions are locked at once
    // before either runs its step.
    address_capture first_capture;
    address_capture second_capture;
    run_recorder first(first_capture);
    run_recorder second(second_capture);
    first.hold_gate_for(second);
    second.hold_gate_for(first);
    const rq_callbacks first_callbacks = first.callbacks();
    const rq_callbacks second_callbacks = second.callbacks();

    layer_handles first_layers;
    layer_handles second_layers;
    session_guard first_session;
    session_guard second_session;
    ASSERT_EQ(session_create(&config, &first_callbacks,
                             make_dependencies(first_capture, first_layers, std::nullopt,
                                               std::make_shared<qpc_clock>(), nullptr),
                             first_session.out()),
              RQ_OK);
    ASSERT_EQ(session_create(&config, &second_callbacks,
                             make_dependencies(second_capture, second_layers, std::nullopt,
                                               std::make_shared<qpc_clock>(), nullptr),
                             second_session.out()),
              RQ_OK);
    EXPECT_EQ(live_session_count(), live_before + 2);

    // Started back to back, so the two preparations overlap as well.
    ASSERT_EQ(session_start(first_session.get()), RQ_OK);
    ASSERT_EQ(session_start(second_session.get()), RQ_OK);
    ASSERT_TRUE(first.wait_for_released()) << "first session: Released not raised within 30 s";
    ASSERT_TRUE(second.wait_for_released()) << "second session: Released not raised within 30 s";
    ASSERT_EQ(first_session.destroy(), RQ_OK);
    ASSERT_EQ(second_session.destroy(), RQ_OK);
    EXPECT_EQ(live_session_count(), live_before);

    const run_recorder* const recorders[] = {&first, &second};
    for (const run_recorder* recorder : recorders) {
        const recorded_run run = recorder->snapshot();
        SCOPED_TRACE(recorder == &first ? "first session" : "second session");
        ASSERT_TRUE(run.completed);
        EXPECT_EQ(run.result.end_reason, RQ_END_COMPLETED)
            << end_reason_name(run.result.end_reason) << " (Windows error " << run.result.os_error
            << "): " << run.result.end_detail;
        EXPECT_TRUE(run.prepared);
        EXPECT_EQ(run.prepared_info.tested_bytes, k_concurrent_region_bytes);
        EXPECT_EQ(run.result.steps_completed, 1u);
        EXPECT_EQ(run.released_bytes, k_concurrent_region_bytes);
        for (const std::string& unfreed : run.unfreed) {
            ADD_FAILURE() << unfreed;
        }
    }
    expect_limits_restored(limits_before, "after both sessions");
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
 * Responsiveness of the process during preparation
 * ------------------------------------------------------------------------------------------- */

/** The region prepared under the probe: 1 GiB, the smallest region the application runs. */
constexpr std::uint64_t k_responsiveness_region_bytes = k_gib;

/** Below this much available physical memory the measurement fails its precondition. */
constexpr std::uint64_t k_responsiveness_min_available_bytes = 2 * k_responsiveness_region_bytes;

/** Workers are capped so that every slice of the region holds at least 4 MiB, as test_stop does. */
constexpr std::uint64_t k_min_responsiveness_slice_bytes = 4 * k_mib;
constexpr std::uint32_t k_max_responsiveness_workers =
    static_cast<std::uint32_t>(k_responsiveness_region_bytes / k_min_responsiveness_slice_bytes);

/** Each probe commits one allocation granularity of fresh memory. */
constexpr std::uint64_t k_probe_bytes = k_allocation_granularity_bytes;

/** A probe starts every 10 ms; the system timer tick can stretch that to about 16 ms. */
constexpr std::chrono::milliseconds k_probe_interval{10};

/**
 * The longest a probe may take: the 250 ms within which the UI must service a probe posted every
 * 100 ms throughout a run, preparation included.
 */
constexpr std::chrono::microseconds k_max_probe_latency{250'000};

/**
 * Once allocate() runs for at least k_min_window_intervals probe intervals, at least
 * k_min_probes_inside probes must complete inside it: a thread that commits memory must keep
 * making progress during preparation, not merely be let through once it ends.
 */
constexpr std::uint32_t k_min_window_intervals = 5;
constexpr std::size_t k_min_probes_inside = 2;

/** Room for the probes of over a minute of preparation, so recording one does not reallocate. */
constexpr std::size_t k_probe_capacity = 8192;

using probe_clock = std::chrono::steady_clock;

/** When one probe began and ended. */
struct probe_sample {
    probe_clock::time_point begin;
    probe_clock::time_point end;
};

/**
 * Measures how long another thread of the process waits to commit and touch fresh memory.
 *
 * Its thread commits a fresh allocation with VirtualAlloc, writes one byte of it and frees it,
 * every k_probe_interval, and records when each probe began and ended. That is what any other
 * thread of the process does when it grows a heap, a stack or the runtime's code space: it
 * changes the address space and takes a page fault, so it waits for whatever keeps the
 * process's working-set lock. The constructor returns once the first probe has ended, so a
 * measurement never starts before the thread runs. stop() lets the probe in flight end before
 * the thread does, so a probe that straddles the measured call is measured to its end. A failed
 * VirtualAlloc or VirtualFree ends the probing, with its Windows error in failure().
 */
class allocation_probe {
public:
    allocation_probe() {
        // Reserved before the thread starts, so recording a probe never allocates.
        samples_.reserve(k_probe_capacity);
        thread_ = std::thread([this] { run(); });
        std::unique_lock<std::mutex> lock(mutex_);
        changed_.wait(lock, [this] { return !samples_.empty() || failure_ != ERROR_SUCCESS; });
    }
    ~allocation_probe() { stop(); }
    allocation_probe(const allocation_probe&) = delete;
    allocation_probe& operator=(const allocation_probe&) = delete;

    /** Ends the probing once the probe in flight has ended. Idempotent. */
    void stop() {
        {
            const std::lock_guard<std::mutex> lock(mutex_);
            stop_.store(true, std::memory_order_relaxed);
        }
        changed_.notify_all();
        if (thread_.joinable()) {
            thread_.join();
        }
    }

    /** Every probe, in order; read after stop(), which ends the thread that writes them. */
    const std::vector<probe_sample>& samples() const noexcept { return samples_; }

    /** The Windows error that ended the probing; ERROR_SUCCESS while every probe succeeded. */
    DWORD failure() {
        const std::lock_guard<std::mutex> lock(mutex_);
        return failure_;
    }

private:
    void run() {
        std::unique_lock<std::mutex> lock(mutex_);
        while (!stop_.load(std::memory_order_relaxed)) {
            lock.unlock();
            const probe_clock::time_point begin = probe_clock::now();
            DWORD error = ERROR_SUCCESS;
            void* const block =
                VirtualAlloc(nullptr, k_probe_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (block == nullptr) {
                error = GetLastError();
            } else {
                *static_cast<volatile std::uint8_t*>(block) = 1;
                if (!VirtualFree(block, 0, MEM_RELEASE)) {
                    error = GetLastError();
                }
            }
            const probe_clock::time_point end = probe_clock::now();
            lock.lock();
            if (error != ERROR_SUCCESS) {
                failure_ = error;
                changed_.notify_all();
                return;
            }
            samples_.push_back(probe_sample{begin, end});
            changed_.notify_all();
            changed_.wait_until(lock, begin + k_probe_interval,
                                [this] { return stop_.load(std::memory_order_relaxed); });
        }
    }

    std::mutex mutex_;
    std::condition_variable changed_;
    std::vector<probe_sample> samples_;
    DWORD failure_ = ERROR_SUCCESS;
    // Written and read only under mutex_, which orders it, so every access is relaxed.
    std::atomic<bool> stop_{false};
    std::thread thread_;
};

/** The worker counts whose preparation the probe watches. */
enum class responsiveness_case {
    /** One worker: four 256 MiB chunks, the most one VirtualLock call covers. */
    one_worker,
    /** One worker per logical processor, capped as above: the application's chunking. */
    every_logical_processor,
};

const char* responsiveness_case_name(responsiveness_case value) noexcept {
    return value == responsiveness_case::one_worker ? "OneWorker" : "EveryLogicalProcessor";
}

void PrintTo(responsiveness_case value, std::ostream* os) {
    *os << responsiveness_case_name(value);
}

std::int64_t whole_microseconds(probe_clock::duration duration) noexcept {
    return std::chrono::duration_cast<std::chrono::microseconds>(duration).count();
}

class memory_region_responsiveness : public ::testing::TestWithParam<responsiveness_case> {};

TEST_P(memory_region_responsiveness, AnotherThreadKeepsCommittingMemoryWhileTheRegionIsLocked) {
    MEMORYSTATUSEX memory{};
    memory.dwLength = sizeof(memory);
    ASSERT_TRUE(GlobalMemoryStatusEx(&memory))
        << "GlobalMemoryStatusEx failed (Windows error " << GetLastError() << ")";
    // Fails rather than skips, so a short machine never reports a pass it did not measure.
    ASSERT_GE(memory.ullAvailPhys, k_responsiveness_min_available_bytes)
        << "Precondition failed: the responsiveness test locks a 1 GiB region and needs at least "
           "2 GiB of available physical memory, but only "
        << memory.ullAvailPhys / k_mib << " MiB is available. Close other applications.";

    const std::uint32_t workers =
        GetParam() == responsiveness_case::one_worker
            ? 1u
            : static_cast<std::uint32_t>(std::min<std::size_t>(
                  enumerate_logical_processors().size(), k_max_responsiveness_workers));
    ASSERT_GT(workers, 0u);
    const region_plan plan = plan_region(k_responsiveness_region_bytes, workers);
    ASSERT_GT(plan.tested_bytes, 0u);
    const working_set_limits limits_before = current_working_set_limits();

    stop_signal stop;
    memory_region region;
    allocation_probe probe;
    ASSERT_EQ(probe.failure(), static_cast<DWORD>(ERROR_SUCCESS))
        << "the probe's VirtualAlloc or VirtualFree failed with this Windows error before "
           "allocate()";
    const probe_clock::time_point begin = probe_clock::now();
    // The minimum is the planned size itself, so no shrink can hide a slow lock behind a smaller
    // region.
    const allocation_outcome outcome = region.allocate(
        make_options(k_responsiveness_region_bytes, plan.tested_bytes, workers), stop);
    const probe_clock::time_point end = probe_clock::now();
    probe.stop();
    ASSERT_EQ(outcome.status, allocation_status::ok)
        << status_name(outcome.status) << ": " << outcome.end_detail;
    EXPECT_EQ(outcome.attempts, 1u);
    EXPECT_EQ(probe.failure(), static_cast<DWORD>(ERROR_SUCCESS))
        << "the probe's VirtualAlloc or VirtualFree failed with this Windows error during "
           "allocate()";

    // Every probe that overlapped allocate(), the ones in flight when it began or returned
    // included, each measured from its start to its end.
    std::size_t overlapping = 0;
    std::size_t completed_inside = 0;
    probe_clock::duration longest{0};
    for (const probe_sample& sample : probe.samples()) {
        if (sample.begin >= end || sample.end <= begin) {
            continue;
        }
        ++overlapping;
        longest = std::max(longest, sample.end - sample.begin);
        if (sample.end <= end) {
            ++completed_inside;
        }
    }
    const probe_clock::duration window = end - begin;
    const std::int64_t window_us = whole_microseconds(window);
    const std::int64_t longest_us = whole_microseconds(longest);
    RecordProperty("workers", workers);
    RecordProperty("chunks", region.chunks().size());
    RecordProperty("prepare_us", window_us);
    RecordProperty("probe_count", overlapping);
    RecordProperty("probes_completed_inside", completed_inside);
    RecordProperty("max_probe_latency_us", longest_us);
    std::printf("[ probe    ] %s: %u workers, %zu chunks, allocate() %.1f ms; %zu probes "
                "overlapped it, %zu completed inside it, longest %.1f ms\n",
                responsiveness_case_name(GetParam()), workers, region.chunks().size(),
                static_cast<double>(window_us) / 1000.0, overlapping, completed_inside,
                static_cast<double>(longest_us) / 1000.0);
    std::fflush(stdout);

    EXPECT_LE(longest_us, k_max_probe_latency.count())
        << "a thread committing 64 KiB waited " << longest_us
        << " us while allocate() prepared the region; the bound is " << k_max_probe_latency.count()
        << " us";
    if (window >= k_min_window_intervals * k_probe_interval) {
        EXPECT_GE(completed_inside, k_min_probes_inside)
            << "allocate() ran " << window_us << " us, yet only " << completed_inside << " of "
            << overlapping << " overlapping probes completed inside it";
    }

    const std::vector<const void*> bases = chunk_bases(region);
    EXPECT_EQ(region.release(), plan.tested_bytes);
    for (const void* base : bases) {
        expect_freed(base, "after release() of the probed region");
    }
    expect_limits_restored(limits_before, "after release() of the probed region");
}

INSTANTIATE_TEST_SUITE_P(MemoryRegionResponsivenessCases, memory_region_responsiveness,
                         ::testing::Values(responsiveness_case::one_worker,
                                           responsiveness_case::every_logical_processor),
                         [](const ::testing::TestParamInfo<responsiveness_case>& info) {
                             return std::string(responsiveness_case_name(info.param));
                         });

/* ---------------------------------------------------------------------------------------------
 * Release on every end reason
 * ------------------------------------------------------------------------------------------- */

/**
 * Stands in for VirtualLock through session_dependencies::lock_override, so that a whole
 * session exhausts its shrink-and-retry sequence deterministically.
 *
 * Constructed with the chunk count of every attempt allocate() makes, in order. Within each
 * attempt it locks every chunk for real except the attempt's last, which fails with
 * ERROR_WORKING_SET_QUOTA as the last error, exactly as a failed VirtualLock reports it. Every
 * failed attempt therefore holds locked chunks (with two workers or more) that allocate() must
 * free before it retries. A call past the listed attempts fails too, so an unexpected retry
 * still ends in exhaustion and shows in the call count. Every chunk base joins the capture,
 * whose bases on_released checks for MEM_FREE. Runs on the coordinator only, inside allocate(),
 * and the test reads the counts after the session is destroyed, so it needs no lock of its own.
 * It must outlive the session that calls it.
 */
class exhausting_lock_script {
public:
    exhausting_lock_script(std::vector<std::uint32_t> attempt_chunks, address_capture& capture)
        : attempt_chunks_(std::move(attempt_chunks)), capture_(capture) {}

    exhausting_lock_script(const exhausting_lock_script&) = delete;
    exhausting_lock_script& operator=(const exhausting_lock_script&) = delete;

    lock_function bind() {
        return [this](void* address, std::size_t bytes) { return lock(address, bytes); };
    }

    std::uint32_t calls() const noexcept { return calls_.load(std::memory_order_relaxed); }

    /** Chunks VirtualLock locked for real. */
    std::uint32_t real_locks() const noexcept {
        return real_locks_.load(std::memory_order_relaxed);
    }

private:
    bool lock(void* address, std::size_t bytes) noexcept {
        capture_.record(address);
        calls_.fetch_add(1, std::memory_order_relaxed);
        bool fail = true;
        if (attempt_ < attempt_chunks_.size()) {
            ++chunk_in_attempt_;
            fail = chunk_in_attempt_ == attempt_chunks_[attempt_];
            if (fail) {
                ++attempt_;
                chunk_in_attempt_ = 0;
            }
        }
        if (!fail) {
            if (VirtualLock(address, bytes) != FALSE) {
                real_locks_.fetch_add(1, std::memory_order_relaxed);
                return true;
            }
            // A real failure ends the attempt as well, so the next call starts the next one;
            // VirtualLock's own last error stands, and the counts expose the failure.
            ++attempt_;
            chunk_in_attempt_ = 0;
            return false;
        }
        // The last action, so nothing overwrites the error before allocate() reads it.
        SetLastError(k_error_working_set_quota);
        return false;
    }

    const std::vector<std::uint32_t> attempt_chunks_;
    address_capture& capture_;
    std::size_t attempt_ = 0;
    std::uint32_t chunk_in_attempt_ = 0;
    // Counted on the coordinator and read by the test once session_destroy has joined it, which
    // orders them, so every access is relaxed.
    std::atomic<std::uint32_t> calls_{0};
    std::atomic<std::uint32_t> real_locks_{0};
};

/** The ways a session can end. */
enum class end_scenario {
    completed,
    user_stop,
    first_error,
    thermal,
    insufficient_memory,
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
    /** Whether exhausting_lock_script fails a lock in every attempt down to the minimum. */
    bool exhaust_locks = false;
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
    case end_scenario::insufficient_memory:
        // The exact detail, naming the last size and the minimum, is checked in the test body.
        spec.name = "InsufficientMemory";
        spec.end_reason = RQ_END_INSUFFICIENT_MEMORY;
        spec.detail = "VirtualLock failed";
        spec.os_error = k_error_working_set_quota;
        spec.prepared = false;
        spec.exhaust_locks = true;
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

class memory_region_end_reason : public ::testing::TestWithParam<end_scenario> {};

TEST_P(memory_region_end_reason, ReleasesEveryChunkBeforeReleasedIsRaised) {
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

    // The chunk count of every attempt allocate() makes before the sequence is exhausted: the
    // first size, then each retry down to the minimum. The last size and the minimum name the
    // failure in end_detail.
    std::vector<std::uint32_t> attempt_chunks;
    std::uint64_t last_attempt_bytes = 0;
    std::optional<exhausting_lock_script> locks;
    if (spec.exhaust_locks) {
        for (std::uint64_t size = config.region_bytes; size != 0;
             size = next_retry_bytes(size, config.min_region_bytes, workers)) {
            attempt_chunks.push_back(workers * plan_region(size, workers).chunks_per_slice);
            last_attempt_bytes = size;
        }
        ASSERT_GE(attempt_chunks.size(), 2u) << reason << ": the scenario must shrink and retry";
        ASSERT_EQ(last_attempt_bytes, config.min_region_bytes)
            << reason << ": the retries must end at the minimum";
        locks.emplace(attempt_chunks, capture);
    }

    layer_handles layers;
    session_dependencies dependencies =
        make_dependencies(capture, layers, fault, std::make_shared<qpc_clock>(), affinity);
    if (locks) {
        dependencies.lock_override = locks->bind();
    }
    session_guard session;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), session.out()), RQ_OK);
    ASSERT_NE(layers.counting, nullptr);
    ASSERT_EQ(layers.fault != nullptr, spec.inject_bit_flips);
    EXPECT_EQ(live_session_count(), live_before + 1);

    const std::uint64_t private_before = current_private_usage();
    ASSERT_GT(private_before, 0u);
    const working_set_limits limits_before = current_working_set_limits();
    ASSERT_EQ(session_start(session.get()), RQ_OK);
    if (spec.host_stop != 0) {
        ASSERT_TRUE(recorder.wait_for_progress()) << reason << ": no progress within 30 s";
        ASSERT_EQ(session_stop(session.get(), spec.host_stop), RQ_OK);
    }
    ASSERT_TRUE(recorder.wait_for_released()) << reason << ": Released not raised within 30 s";
    if (spec.exhaust_locks) {
        // Checked before destroy, whose release would withdraw a raise the coordinator left.
        expect_limits_restored(limits_before, reason + ": after Released");
    }
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
    // Completed, then Released, once each and last; a preparation failure raises nothing else.
    const std::string trace = ::testing::PrintToString(run.events);
    EXPECT_EQ(std::count(run.events.begin(), run.events.end(), engine_event::completed), 1)
        << reason << ": " << trace;
    EXPECT_EQ(std::count(run.events.begin(), run.events.end(), engine_event::released), 1)
        << reason << ": " << trace;
    ASSERT_GE(run.events.size(), 2u) << reason << ": " << trace;
    EXPECT_EQ(run.events[run.events.size() - 2], engine_event::completed)
        << reason << ": " << trace;
    EXPECT_EQ(run.events.back(), engine_event::released) << reason << ": " << trace;
    if (!spec.prepared) {
        EXPECT_EQ(run.events,
                  (std::vector<engine_event>{engine_event::completed, engine_event::released}))
            << reason;
    }
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
    // the planned bytes that were allocated and locked before preparation failed. Exhausted
    // locking leaves nothing: allocate() freed every failed attempt itself.
    if (spec.exhaust_locks) {
        EXPECT_EQ(run.released_bytes, 0u) << reason;
        EXPECT_EQ(run.release_ms, 0u) << reason;
    } else {
        if (run.prepared) {
            EXPECT_EQ(run.prepared_info.tested_bytes, planned.tested_bytes) << reason;
            EXPECT_EQ(run.released_bytes, run.prepared_info.tested_bytes) << reason;
        } else {
            EXPECT_EQ(run.released_bytes, planned.tested_bytes) << reason;
        }
        EXPECT_GT(run.released_bytes, 0u) << reason;
    }

    // Every chunk base the run touched was MEM_FREE inside on_released.
    for (const std::string& unfreed : run.unfreed) {
        ADD_FAILURE() << reason << ": " << unfreed;
    }
    EXPECT_EQ(capture.query_failures(), 0u) << reason;
    const std::vector<std::uintptr_t> bases = capture.bases();

    if (spec.prepared) {
        EXPECT_FALSE(bases.empty()) << reason << ": the run touched no chunk";
        EXPECT_GT(primitives, 0u) << reason;
    } else if (spec.exhaust_locks) {
        // No size down to the minimum locked, so no slice reached a worker: no gate, step,
        // progress or traffic, and a result with nothing measured.
        EXPECT_EQ(primitives, 0u) << reason << ": traffic ran without a locked region";
        EXPECT_EQ(run.gate_calls, 0u) << reason;
        EXPECT_EQ(run.steps_finished, 0u) << reason;
        EXPECT_EQ(run.progress_calls, 0u) << reason;
        EXPECT_EQ(run.result.step_result_count, 0u) << reason;
        EXPECT_EQ(run.result.steps_completed, 0u) << reason;
        EXPECT_EQ(run.result.total_errors, 0u) << reason;
        EXPECT_EQ(run.result.verified_bytes, 0u) << reason;
        EXPECT_EQ(run.result.elapsed_ms, 0u) << reason;
        EXPECT_EQ(run.result.first_error_ms, RQ_NO_FIRST_ERROR) << reason;

        // The detail names the last size and the minimum. The Windows error is os_error's
        // alone, because the host prefixes "Windows error n: " to the detail.
        char expected_detail[RQ_END_DETAIL_SIZE];
        std::snprintf(expected_detail, sizeof(expected_detail),
                      "VirtualLock failed at %llu MiB; %llu MiB minimum not lockable",
                      last_attempt_bytes / k_mib, config.min_region_bytes / k_mib);
        EXPECT_EQ(detail, expected_detail) << reason;
        EXPECT_EQ(detail.find("Windows error"), std::string::npos) << reason << ": " << detail;

        // Every attempt asked for each of its chunks and locked all but the last for real, so
        // with two workers each failed attempt held a locked partial region that allocate()
        // freed before retrying; every one of those bases was MEM_FREE inside on_released.
        ASSERT_TRUE(locks.has_value());
        std::uint32_t expected_calls = 0;
        std::uint32_t expected_real_locks = 0;
        for (const std::uint32_t chunks : attempt_chunks) {
            expected_calls += chunks;
            expected_real_locks += chunks - 1;
        }
        EXPECT_EQ(locks->calls(), expected_calls)
            << reason << ": the attempts did not follow next_retry_bytes";
        EXPECT_EQ(locks->real_locks(), expected_real_locks) << reason;
        EXPECT_FALSE(bases.empty()) << reason << ": the lock override saw no chunk";
        EXPECT_LE(run.private_usage_at_release, private_before + k_private_usage_slack_bytes)
            << reason << ": the commit charge did not return after release (before "
            << private_before << ", at release " << run.private_usage_at_release << ")";
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

INSTANTIATE_TEST_SUITE_P(MemoryRegionEndReasons, memory_region_end_reason,
                         ::testing::Values(end_scenario::completed, end_scenario::user_stop,
                                           end_scenario::first_error, end_scenario::thermal,
                                           end_scenario::insufficient_memory,
                                           end_scenario::affinity_failure, end_scenario::host_fault,
                                           end_scenario::engine_fault),
                         [](const ::testing::TestParamInfo<end_scenario>& info) {
                             return std::string(spec_for(info.param).name);
                         });

}  // namespace
}  // namespace rq::engine::test
