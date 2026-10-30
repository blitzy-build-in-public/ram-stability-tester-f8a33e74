/**
 * @file memory_region.cpp
 * @brief Chunked allocation and locking of the region under test, with large pages where the
 *        token allows them, shrink-and-retry on a locking shortfall and RAII release.
 *
 * Allocation and locking run in Preparing on the coordinator thread, one 256 MiB chunk at a
 * time with a stop check before each, so a stop waits for at most one chunk's commit-and-lock.
 * Each 4 KiB-page chunk is faulted in page by page before VirtualLock pins it, so neither the UI
 * nor Windows is starved while gigabytes are faulted in and locked.
 *
 * Stress bound: this file touches page contents only to fault 4 KiB pages in, with one store of
 * zero per page once per allocation, and issues no cache flushes. Every OS change it makes is
 * process-local (the working-set limits and the enabled state of a privilege the token already
 * holds), and release() undoes both.
 */
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "core/memory/memory_region.h"
#include "core/clock.h"
#include "ramquick_engine.h"

#include <cstdio>
#include <cwchar>
#include <cstdarg>
#include <algorithm>
#include <utility>
#include <vector>

namespace rq::engine {

namespace {

/**
 * Closes a kernel handle on scope exit; used for the process token. A failed close reaches the
 * debugger through close_handle_checked, the most a destructor can do about it.
 */
class scoped_handle {
public:
    explicit scoped_handle(HANDLE handle) noexcept : handle_(handle) {}
    ~scoped_handle() {
        if (handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE) {
            close_handle_checked(handle_, L"process token (lock-pages privilege adjustment)");
        }
    }
    scoped_handle(const scoped_handle&) = delete;
    scoped_handle& operator=(const scoped_handle&) = delete;

    HANDLE get() const noexcept { return handle_; }

private:
    HANDLE handle_;
};

/**
 * Writes one line to the debugger.
 *
 * The C API has no warning channel, so the per-retry RQ_E_LOCK_FAILED reports, large-page
 * fallbacks and release or restore failures are visible to a debugger only; the final outcome
 * reaches the host through allocation_outcome. _vsnwprintf_s with _TRUNCATE is used rather than
 * vswprintf_s because an overlong message must be cut, whereas vswprintf_s would raise the CRT
 * invalid-parameter handler, which terminates a static-CRT process.
 */
void debug_log(_Printf_format_string_ const wchar_t* format, ...) noexcept {
    static constexpr wchar_t k_prefix[] = L"RAMQuick engine: ";
    constexpr std::size_t k_prefix_length = std::size(k_prefix) - 1;
    wchar_t buffer[256];
    std::wmemcpy(buffer, k_prefix, k_prefix_length);

    // Leaves room for the trailing newline and the terminator.
    wchar_t* const text = buffer + k_prefix_length;
    const std::size_t text_capacity = std::size(buffer) - k_prefix_length - 1;
    va_list args;
    va_start(args, format);
    const int written = _vsnwprintf_s(text, text_capacity, _TRUNCATE, format, args);
    va_end(args);

    const std::size_t length = written >= 0 ? static_cast<std::size_t>(written) : std::wcslen(text);
    text[length] = L'\n';
    text[length + 1] = L'\0';
    OutputDebugStringW(buffer);
}

/**
 * Fills os_error and end_detail of an insufficient_memory outcome for a failed OS call.
 *
 * The Windows error travels in os_error alone: the host prefixes it to the detail ("Windows
 * error n: "), so a code in the text would be shown twice.
 */
void set_detail(allocation_outcome& outcome, const char* call, DWORD error,
                std::uint64_t attempted_mib, std::uint64_t min_mib) noexcept {
    char buffer[256];
    std::snprintf(buffer, sizeof(buffer), "%s failed at %llu MiB; %llu MiB minimum not lockable",
                  call, attempted_mib, min_mib);
    outcome.os_error = error;
    copy_utf8_truncated(outcome.end_detail, sizeof(outcome.end_detail), buffer);
}

/**
 * Owns a 4 KiB-page allocation from VirtualAlloc until it is handed to the chunk list, locked,
 * or unlocked after a failed lock for the attempt's cleanup to free. Only an exception from the
 * lock override leaves it here, and the destructor then frees it.
 */
class pending_allocation {
public:
    explicit pending_allocation(void* base) noexcept : base_(base) {}
    ~pending_allocation() {
        if (base_ != nullptr && !VirtualFree(base_, 0, MEM_RELEASE)) {
            debug_log(L"VirtualFree of an unlocked chunk failed (Windows error %lu)",
                      GetLastError());
        }
    }
    pending_allocation(const pending_allocation&) = delete;
    pending_allocation& operator=(const pending_allocation&) = delete;

    /** Hands the allocation over; the destructor then frees nothing. */
    void* hand_over() noexcept {
        void* const base = base_;
        base_ = nullptr;
        return base;
    }

private:
    void* base_;
};

/**
 * Sets the enabled state of SeLockMemoryPrivilege in the process token.
 *
 * This only enables or disables a privilege the token already holds, a process-local token
 * change; no privilege is ever assigned to an account. AdjustTokenPrivileges returns TRUE even
 * when the token lacks the privilege, so success also requires last-error ERROR_SUCCESS rather
 * than ERROR_NOT_ALL_ASSIGNED.
 *
 * @param enable Whether to enable or disable the privilege.
 * @param was_enabled Receives, on success, whether the privilege was enabled before the call.
 *        PreviousState lists only the privileges the call changed, so an empty list means the
 *        state was already the requested one.
 * @param error Receives the Windows error on failure.
 * @return true when the privilege is now in the requested state.
 */
bool adjust_lock_memory_privilege(bool enable, bool& was_enabled, DWORD& error) noexcept {
    HANDLE raw_token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &raw_token)) {
        error = GetLastError();
        return false;
    }
    const scoped_handle token(raw_token);

    LUID luid{};
    if (!LookupPrivilegeValueW(nullptr, SE_LOCK_MEMORY_NAME, &luid)) {
        error = GetLastError();
        return false;
    }

    TOKEN_PRIVILEGES requested{};
    requested.PrivilegeCount = 1;
    requested.Privileges[0].Luid = luid;
    requested.Privileges[0].Attributes = enable ? SE_PRIVILEGE_ENABLED : 0;
    TOKEN_PRIVILEGES previous{};
    DWORD returned = 0;
    const BOOL adjusted = AdjustTokenPrivileges(token.get(), FALSE, &requested, sizeof(previous),
                                                &previous, &returned);
    error = GetLastError();
    if (!adjusted || error != ERROR_SUCCESS) {
        return false;
    }
    const bool changed = previous.PrivilegeCount == 1;
    was_enabled =
        changed ? (previous.Privileges[0].Attributes & SE_PRIVILEGE_ENABLED) != 0 : enable;
    return true;
}

/** What free_chunks() did. */
struct chunks_freed {
    /** Bytes whose VirtualFree succeeded. */
    std::uint64_t bytes = 0;
    /** GetLastError() of the last VirtualFree that failed; ERROR_SUCCESS when none failed. */
    DWORD free_error = ERROR_SUCCESS;
};

/**
 * Unlocks (4 KiB-page chunks only; large pages are never locked) and frees every chunk. A
 * failed unlock does not keep the chunk from being freed.
 *
 * A chunk whose VirtualFree fails is still allocated, so it stays in the list, which keeps
 * ownership of it for a later call to retry: dropping it would lose the allocation while the
 * caller reports the region released. The kept chunks are moved to the front of the same
 * vector, in order, so this noexcept path never allocates.
 *
 * @param free_override Replaces VirtualFree(base, 0, MEM_RELEASE) when non-empty.
 */
chunks_freed free_chunks(std::vector<chunk_view>& chunks,
                         const free_function& free_override) noexcept {
    chunks_freed freed{};
    std::size_t kept = 0;
    for (const chunk_view& chunk : chunks) {
        if (!chunk.large_pages && !VirtualUnlock(chunk.base, chunk.bytes)) {
            const DWORD error = GetLastError();
            // A chunk locked by a test's lock override may never have been locked for real, and
            // a chunk kept by an earlier call was already unlocked by it.
            if (error != ERROR_NOT_LOCKED) {
                debug_log(L"VirtualUnlock of the chunk at %llu MiB failed (Windows error %lu)",
                          chunk.logical_offset / k_mib, error);
            }
        }
        const bool released = free_override ? free_override(chunk.base)
                                            : VirtualFree(chunk.base, 0, MEM_RELEASE) != FALSE;
        if (released) {
            freed.bytes += chunk.bytes;
        } else {
            freed.free_error = GetLastError();
            debug_log(L"VirtualFree of the chunk at %llu MiB failed (Windows error %lu); the "
                      L"chunk is kept for a later release",
                      chunk.logical_offset / k_mib, freed.free_error);
            chunks[kept++] = chunk;
        }
    }
    chunks.erase(chunks.begin() + static_cast<std::ptrdiff_t>(kept), chunks.end());
    return freed;
}

/** The failing OS call of an attempt and its GetLastError(), captured right after the call. */
struct attempt_failure {
    const char* call = "";
    DWORD error = ERROR_SUCCESS;
};

/** Holds an SRW lock exclusively for its lifetime; acquiring one cannot fail or throw. */
class exclusive_lock {
public:
    explicit exclusive_lock(SRWLOCK& lock) noexcept : lock_(lock) {
        AcquireSRWLockExclusive(&lock_);
    }
    ~exclusive_lock() { ReleaseSRWLockExclusive(&lock_); }
    exclusive_lock(const exclusive_lock&) = delete;
    exclusive_lock& operator=(const exclusive_lock&) = delete;

private:
    SRWLOCK& lock_;
};

constexpr std::uint64_t saturating_add(std::uint64_t a, std::uint64_t b) noexcept {
    return a > UINT64_MAX - b ? UINT64_MAX : a + b;
}

/**
 * The process's working-set limits, shared by every region that raises them.
 *
 * VirtualLock is bounded by a single minimum working set per process, so the regions of
 * sessions running at once in one process must share it. Each region adds its contribution,
 * the bytes it is about to lock, and the minimum is the sum of every live contribution plus
 * one k_working_set_headroom_bytes, which for a lone region is its own bytes plus the
 * headroom. Without the sum, each raise would replace the minimum another live region relies
 * on, and the next VirtualLock of either would fail with ERROR_WORKING_SET_QUOTA. The limits in
 * force before the first contribution are restored once the last one is withdrawn; they are
 * kept until a restore succeeds, so a failed restore never makes raised limits the baseline.
 *
 * Both limits are soft (QUOTA_LIMITS_HARDWS_*_DISABLE): the minimum never forces other
 * processes to be trimmed and the maximum never caps this one. Every change runs under one SRW
 * lock, which the noexcept release path can take because acquiring it cannot fail. Constant
 * initialized and trivially destructible, so it is usable from any region, static ones
 * included.
 */
class working_set_ledger {
public:
    constexpr working_set_ledger() noexcept = default;

    /**
     * Adds contribution and sets the limits to cover it. On failure the ledger and the limits
     * are unchanged, and failure names the call that failed.
     */
    bool add(std::uint64_t contribution, attempt_failure& failure) noexcept {
        const exclusive_lock guard(lock_);
        if (!baseline_saved_) {
            SIZE_T minimum = 0;
            SIZE_T maximum = 0;
            DWORD flags = 0;
            if (!GetProcessWorkingSetSizeEx(GetCurrentProcess(), &minimum, &maximum, &flags)) {
                failure = attempt_failure{"GetProcessWorkingSetSizeEx", GetLastError()};
                return false;
            }
            baseline_minimum_ = minimum;
            baseline_maximum_ = maximum;
            baseline_flags_ = flags;
            baseline_saved_ = true;
        }
        // Saturated, so an absurd total fails in SetProcessWorkingSetSizeEx with its own error
        // instead of wrapping to a small minimum.
        const std::uint64_t total = saturating_add(total_, contribution);
        if (!set_limits(total)) {
            failure = attempt_failure{"SetProcessWorkingSetSizeEx", GetLastError()};
            return false;
        }
        total_ = total;
        ++holders_;
        return true;
    }

    /**
     * Withdraws a contribution add() accepted: lowers the minimum to the remaining ones, or
     * restores the saved limits when it was the last. A failure is logged; a minimum left too
     * high only costs headroom, and the next change sets the limits afresh.
     */
    void remove(std::uint64_t contribution) noexcept {
        const exclusive_lock guard(lock_);
        // Every withdrawal pairs with an accepted add(), so an empty ledger has nothing to undo.
        if (holders_ == 0) {
            return;
        }
        total_ -= std::min(total_, contribution);
        --holders_;
        if (holders_ != 0) {
            if (!set_limits(total_)) {
                debug_log(L"Lowering the minimum working set to %llu MiB failed (Windows error "
                          L"%lu)",
                          saturating_add(total_, k_working_set_headroom_bytes) / k_mib,
                          GetLastError());
            }
            return;
        }
        total_ = 0;
        if (SetProcessWorkingSetSizeEx(GetCurrentProcess(), baseline_minimum_, baseline_maximum_,
                                       baseline_flags_)) {
            baseline_saved_ = false;
        } else {
            debug_log(L"Restoring the working-set limits failed (Windows error %lu)",
                      GetLastError());
        }
    }

private:
    /** Minimum = total + headroom; maximum = at least the saved maximum, and headroom above it. */
    bool set_limits(std::uint64_t total) const noexcept {
        const std::uint64_t minimum = saturating_add(total, k_working_set_headroom_bytes);
        const std::uint64_t maximum = std::max<std::uint64_t>(
            baseline_maximum_, saturating_add(minimum, k_working_set_headroom_bytes));
        return SetProcessWorkingSetSizeEx(
                   GetCurrentProcess(), static_cast<SIZE_T>(minimum), static_cast<SIZE_T>(maximum),
                   QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE) != FALSE;
    }

    SRWLOCK lock_ = SRWLOCK_INIT;
    // Sum of the live contributions, and how many there are. Plain fields guarded by lock_
    // rather than atomics: each change must stay paired with the SetProcessWorkingSetSizeEx
    // call that applies it, which no atomic counter could order.
    std::uint64_t total_ = 0;
    std::uint32_t holders_ = 0;
    // The limits read before the first contribution, kept until a restore to them succeeds.
    bool baseline_saved_ = false;
    SIZE_T baseline_minimum_ = 0;
    SIZE_T baseline_maximum_ = 0;
    DWORD baseline_flags_ = 0;
};

/** The ledger of this process. */
working_set_ledger& process_working_set() noexcept {
    static working_set_ledger ledger;
    return ledger;
}

/** Withdraws a region's working-set raise, if it holds one, and zeroes it. */
void restore_working_set(std::uint64_t& contribution) noexcept {
    if (contribution != 0) {
        process_working_set().remove(contribution);
        contribution = 0;
    }
}

/**
 * The owner of the chunks that regions still held when they were destroyed, because Windows
 * refused to free them.
 *
 * A destroyed region cannot keep owning a chunk, and dropping the chunk would leave a committed,
 * possibly locked, allocation with no owner and no retry, under a working-set raise nobody
 * withdraws. Each entry therefore keeps what its region held: the chunks, the free override
 * that frees them and the working-set contribution that covers them, which stays in the ledger
 * until every chunk of the entry is freed, so the minimum never drops beneath pages that may
 * still be locked. Every release() in the process retries the entries, so any later region
 * frees them once Windows allows it; Windows reclaims what is still refused at process exit.
 *
 * Every change runs under one SRW lock, which the noexcept release path can take because
 * acquiring it cannot fail. Lock order: this lock, then the ledger's; the ledger never takes
 * this one. The constructor is constexpr and noexcept, and memory_region's constructor makes
 * the first use, so the owner outlives every region, static ones included.
 */
class orphan_owner {
public:
    constexpr orphan_owner() noexcept = default;

    /**
     * Takes over the chunks, free override and working-set contribution of a region being
     * destroyed, leaving them empty and zero there, so nothing withdraws the contribution twice.
     */
    void adopt(std::vector<chunk_view>& chunks, free_function& free_override,
               std::uint64_t& contribution) noexcept {
        const exclusive_lock guard(lock_);
        try {
            entries_.emplace_back();
        } catch (...) {
            // Growing the list can fail only for want of memory, and then ownership of the
            // chunks can be kept nowhere. Ending the process is the one way left to have the
            // pages freed: Windows reclaims them, locked ones included, with the process, and
            // the host reports the unfinished run at its next launch.
            debug_log(L"The orphaned-chunk list could not grow; ending the process so Windows "
                      L"reclaims the chunks");
            __fastfail(FAST_FAIL_FATAL_APP_EXIT);
        }
        orphan_entry& entry = entries_.back();
        // Swaps move without copying an element or allocating, and cannot throw.
        entry.chunks.swap(chunks);
        entry.free_override.swap(free_override);
        entry.ws_contribution = std::exchange(contribution, 0);
    }

    /**
     * Frees every adopted chunk Windows now allows, each with its own region's free override.
     * An entry whose chunks are all freed withdraws its working-set contribution and is erased,
     * which moves the later entries down and allocates nothing.
     */
    void retry() noexcept {
        const exclusive_lock guard(lock_);
        for (orphan_entry& entry : entries_) {
            free_chunks(entry.chunks, entry.free_override);
            if (entry.chunks.empty()) {
                restore_working_set(entry.ws_contribution);
            }
        }
        std::erase_if(entries_, [](const orphan_entry& entry) { return entry.chunks.empty(); });
    }

    /** Bytes of every adopted chunk still allocated. */
    std::uint64_t bytes() noexcept {
        const exclusive_lock guard(lock_);
        std::uint64_t total = 0;
        for (const orphan_entry& entry : entries_) {
            for (const chunk_view& chunk : entry.chunks) {
                total += chunk.bytes;
            }
        }
        return total;
    }

private:
    /** What one destroyed region still held. */
    struct orphan_entry {
        std::vector<chunk_view> chunks;
        free_function free_override;
        std::uint64_t ws_contribution = 0;
    };

    SRWLOCK lock_ = SRWLOCK_INIT;
    std::vector<orphan_entry> entries_;
};

/** The orphan owner of this process. */
orphan_owner& process_orphans() noexcept {
    static orphan_owner orphans;
    return orphans;
}

enum class attempt_status { ok, stopped, failed };

/** x rounded down to a multiple of unit; unit must be non-zero. */
constexpr std::uint64_t round_down(std::uint64_t x, std::uint64_t unit) noexcept {
    return x / unit * unit;
}

/** The x64 Windows standard page, the unit in which a 4 KiB-page chunk is committed and locked. */
constexpr std::uint64_t k_page_bytes = 4096;

static_assert(k_slice_granularity_bytes % k_page_bytes == 0, "every chunk is whole pages");

/**
 * Faults every page of a freshly committed 4 KiB-page chunk in, with one store of zero per page,
 * so that the VirtualLock that follows pins pages that are already resident.
 *
 * VirtualLock faults non-resident pages in itself, inside one call that keeps the process's
 * working-set lock held throughout, so every other thread of the process that commits or touches
 * a page meanwhile (the UI thread rendering, the runtime compiling) would wait for the whole
 * call, and so for the whole preparation. Each fault taken here takes and releases that lock on
 * its own, so the other threads' faults interleave with these. The minimum working set is raised
 * before the first 4 KiB-page chunk, so the pages faulted in here stay resident until they are
 * locked. The pages are demand-zero, so a store of zero leaves their contents unchanged; it is
 * volatile so that it is kept. Each page is stored to once per allocation and nothing is flushed,
 * so the stress bound is unaffected. Cannot fail: the pages are committed.
 */
void fault_in_pages(void* base, std::uint64_t bytes) noexcept {
    volatile std::uint8_t* const first = static_cast<volatile std::uint8_t*>(base);
    for (std::uint64_t offset = 0; offset < bytes; offset += k_page_bytes) {
        first[offset] = 0;
    }
}

}  // namespace

region_plan plan_region(std::uint64_t region_bytes, std::uint32_t worker_count) noexcept {
    region_plan plan{};
    if (worker_count == 0) {
        return plan;
    }
    plan.worker_count = worker_count;
    const std::uint64_t slice = round_down(region_bytes / worker_count, k_slice_granularity_bytes);
    const std::uint64_t full_chunks = slice / k_chunk_bytes;
    // A slice of 2^60 bytes or more would overflow the 32-bit chunk count. No such slice fits
    // the x64 user address space, so it is reported like a region too small to plan.
    if (slice == 0 || full_chunks >= UINT32_MAX) {
        return plan;
    }
    plan.slice_bytes = slice;
    plan.tested_bytes = slice * worker_count;
    plan.full_chunks_per_slice = static_cast<std::uint32_t>(full_chunks);
    plan.final_chunk_bytes = slice % k_chunk_bytes;
    plan.chunks_per_slice = plan.full_chunks_per_slice + (plan.final_chunk_bytes != 0 ? 1u : 0u);
    return plan;
}

std::uint64_t next_retry_bytes(std::uint64_t attempted_bytes, std::uint64_t min_region_bytes,
                               std::uint32_t worker_count) noexcept {
    if (worker_count == 0) {
        return 0;
    }
    // At most 2^32 x 2 MiB = 2^53, so the unit itself cannot overflow.
    const std::uint64_t unit = static_cast<std::uint64_t>(worker_count) * k_slice_granularity_bytes;

    // attempted x 90 / 100 computed as q x 90 + r x 90 / 100 (attempted = 100q + r): the same
    // floor, without the product overflowing for any attempted size.
    const std::uint64_t scaled =
        attempted_bytes / 100 * k_retry_percent + attempted_bytes % 100 * k_retry_percent / 100;
    const std::uint64_t shrunk = round_down(scaled, unit);

    // min rounded up to a multiple of unit. When that is not representable no size can reach
    // the minimum, so the sequence ends.
    const std::uint64_t min_down = round_down(min_region_bytes, unit);
    std::uint64_t floor_bytes = min_down;
    if (min_down != min_region_bytes) {
        if (min_down > UINT64_MAX - unit) {
            return 0;
        }
        floor_bytes = min_down + unit;
    }

    if (shrunk >= floor_bytes) {
        return shrunk;
    }
    return floor_bytes < attempted_bytes ? floor_bytes : 0;
}

memory_region::memory_region() noexcept {
    // Constructed before any region is complete, so the owner is destroyed after every region,
    // static ones included, whose destructor may hand it chunks.
    process_orphans();
}

memory_region::~memory_region() {
    release();
    if (!chunks_.empty()) {
        // Ownership never ends with the object: a chunk Windows still refuses to free passes,
        // with the override that frees it and the working-set raise that covers it, to the
        // process-wide owner that every later release() retries.
        debug_log(L"%llu MiB could not be freed and pass to the owner of orphaned chunks",
                  held_bytes() / k_mib);
        process_orphans().adopt(chunks_, free_override_, ws_contribution_);
    }
}

allocation_outcome memory_region::allocate(const region_options& options, const stop_signal& stop) {
    // A region still held from an earlier call is released first, so a repeated call never
    // leaks. On an unallocated region this changes nothing.
    release();

    allocation_outcome outcome{};
    if (!chunks_.empty()) {
        // Windows refused to free a chunk of the earlier region, so it stays owned for a later
        // release() to retry. A plan over it would mix it into the new region's chunk list, so
        // the call is refused before any attempt; the free error is the one that failed last.
        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "VirtualFree failed; %llu MiB of an earlier region is still held",
                      held_bytes() / k_mib);
        outcome.status = allocation_status::insufficient_memory;
        outcome.os_error = free_error_;
        copy_utf8_truncated(outcome.end_detail, sizeof(outcome.end_detail), buffer);
        return outcome;
    }
    // Kept for every later release() of the chunks this call allocates. Copied before any OS
    // state is touched, as the chunk-list reservation below is, since a copy may throw.
    free_override_ = options.free_override;

    // Ends the call at its first size, before the chunk list is reserved or any OS state is
    // touched, as the first attempt's refusal of a size that cannot reach the minimum does.
    auto refuse_first_size = [&outcome](const char* detail, std::uint32_t os_error) {
        outcome.status = allocation_status::insufficient_memory;
        outcome.attempts = 1;
        outcome.os_error = os_error;
        copy_utf8_truncated(outcome.end_detail, sizeof(outcome.end_detail), detail);
        return outcome;
    };

    const region_plan first_plan = plan_region(options.region_bytes, options.worker_count);

    // Locked pages stay resident, so a first size that plans more bytes than the machine has
    // can never be locked. The host sizes its request from available memory, so such a request
    // is malformed rather than a shortfall to shrink through, and it is refused before the
    // reservation below: plan_region() accepts nearly 2^32 chunks per slice, so a single-worker
    // request near 1 EiB would otherwise reserve over 100 GiB of chunk views. A plan with no
    // slice is either too small, which the first attempt refuses, or too large to plan, so the
    // requested size stands in for its tested bytes.
    MEMORYSTATUSEX memory_status{};
    memory_status.dwLength = sizeof(memory_status);
    if (!GlobalMemoryStatusEx(&memory_status)) {
        const DWORD error = GetLastError();
        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "GlobalMemoryStatusEx failed, so the %llu MiB region was not checked "
                      "against physical memory",
                      options.region_bytes / k_mib);
        return refuse_first_size(buffer, error);
    }
    const std::uint64_t planned_bytes =
        first_plan.slice_bytes != 0 ? first_plan.tested_bytes : options.region_bytes;
    if (planned_bytes > memory_status.ullTotalPhys) {
        // The request is rounded up and physical memory down to whole MiB, so the text never
        // shows a request equal to the memory it exceeds. No OS call failed, so os_error is 0.
        const std::uint64_t requested_mib =
            options.region_bytes / k_mib + (options.region_bytes % k_mib != 0 ? 1u : 0u);
        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "Region of %llu MiB exceeds the %llu MiB of physical memory", requested_mib,
                      memory_status.ullTotalPhys / k_mib);
        return refuse_first_size(buffer, 0);
    }

    // Both factors are 32-bit, so the product is exact in 64 bits. Within physical memory it is
    // at most one entry per 256 MiB plus one per worker; the check keeps the reservation within
    // what the list can hold should that bound ever be loosened.
    const std::uint64_t chunk_count =
        static_cast<std::uint64_t>(first_plan.worker_count) * first_plan.chunks_per_slice;
    if (chunk_count > chunks_.max_size()) {
        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "Region of %llu MiB over %u workers needs %llu chunks, more than can be "
                      "tracked",
                      options.region_bytes / k_mib, options.worker_count, chunk_count);
        return refuse_first_size(buffer, 0);
    }

    // The first attempt is the largest and no retry needs more chunks, so this one reservation
    // and the copy of the free override above are the only operations that can throw. They run
    // before any OS state is touched, so an exception leaves nothing allocated, locked, raised or
    // enabled.
    chunks_.reserve(static_cast<std::size_t>(chunk_count));

    // Large pages only when the token already holds SeLockMemoryPrivilege and the processor
    // supports them; otherwise every chunk uses 4 KiB pages locked with VirtualLock.
    std::uint64_t large_page_minimum = 0;
    if (options.lock_privilege_held) {
        large_page_minimum = GetLargePageMinimum();
        if (large_page_minimum != 0) {
            bool was_enabled = false;
            DWORD error = ERROR_SUCCESS;
            if (adjust_lock_memory_privilege(true, was_enabled, error)) {
                privilege_enabled_by_us_ = !was_enabled;
            } else {
                debug_log(L"SeLockMemoryPrivilege could not be enabled (Windows error %lu); "
                          L"using 4 KiB pages",
                          error);
                large_page_minimum = 0;
            }
        }
    }
    const bool use_large_pages = large_page_minimum != 0;

    // VirtualLock locks only up to the minimum working set, so the minimum is raised by the
    // bytes still to be locked, on top of the raise of every other live region; the ledger adds
    // the headroom for the process's own pages once. The raise needs
    // SeIncreaseWorkingSetPrivilege, which the default policy grants to Users. Holding it
    // suffices; it need not be enabled, and a standard user's token holds it disabled, so a
    // 1 GiB region locks without elevation.
    auto raise_working_set = [&](std::uint64_t locked_bytes, attempt_failure& failure) -> bool {
        if (!process_working_set().add(locked_bytes, failure)) {
            return false;
        }
        ws_contribution_ = locked_bytes;
        return true;
    };

    // One attempt at plan: slice by slice and chunk by chunk, so chunks_ ends in ascending
    // logical order.
    auto run_attempt = [&](const region_plan& plan, attempt_failure& failure) -> attempt_status {
        for (std::uint32_t worker = 0; worker < plan.worker_count; ++worker) {
            for (std::uint32_t j = 0; j < plan.chunks_per_slice; ++j) {
                // Checked before every chunk, so a stop waits for at most one chunk's
                // commit-and-lock.
                if (stop.stop_requested()) {
                    return attempt_status::stopped;
                }
                const std::uint64_t size =
                    j < plan.full_chunks_per_slice ? k_chunk_bytes : plan.final_chunk_bytes;
                const std::uint64_t offset = worker * plan.slice_bytes + j * k_chunk_bytes;

                void* base = nullptr;
                bool large = false;
                if (use_large_pages && size % large_page_minimum == 0) {
                    // Large pages are non-pageable, so the chunk needs no VirtualLock.
                    base = VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT | MEM_LARGE_PAGES,
                                        PAGE_READWRITE);
                    if (base != nullptr) {
                        large = true;
                    } else {
                        // Physical fragmentation can defeat a large-page allocation even when
                        // enough memory is free. This chunk alone falls back; the next one tries
                        // large pages again.
                        const DWORD error = GetLastError();
                        ++outcome.large_page_fallbacks;
                        debug_log(L"Large-page allocation of the chunk at %llu MiB failed "
                                  L"(Windows error %lu); using 4 KiB pages for it",
                                  offset / k_mib, error);
                    }
                }

                if (!large) {
                    // Raised once per attempt, before its first 4 KiB-page chunk, to that chunk
                    // and every later one. Without large pages that chunk is the first, so the
                    // raise covers tested_bytes. With large pages it is the first fallback:
                    // large-page memory is non-pageable and outside the working set, so raising
                    // the minimum by the whole region up front would charge the same physical
                    // memory twice and could itself make the large-page allocations fail. Later
                    // chunks are included because they may fall back too, and an attempt whose
                    // chunks are all large never raises the working set.
                    if (ws_contribution_ == 0 &&
                        !raise_working_set(plan.tested_bytes - offset, failure)) {
                        return attempt_status::failed;
                    }
                    void* const allocation =
                        VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
                    if (allocation == nullptr) {
                        failure = attempt_failure{"VirtualAlloc", GetLastError()};
                        return attempt_status::failed;
                    }
                    pending_allocation pending(allocation);
                    // Faulted in page by page first, so the lock (or the override standing in
                    // for it) never keeps the working-set lock while it faults the chunk in.
                    fault_in_pages(allocation, size);
                    const bool locked = options.lock_override
                                            ? options.lock_override(allocation, size)
                                            : VirtualLock(allocation, size) != FALSE;
                    if (!locked) {
                        // The lock override reports its failure through the last error too, and
                        // stands in for VirtualLock, so the failure is named after it.
                        failure = attempt_failure{"VirtualLock", GetLastError()};
                        // The unlocked chunk joins the list, within the capacity reserved for
                        // this attempt, so the attempt's cleanup frees it with the others and
                        // keeps owning it should Windows refuse to free it.
                        chunks_.push_back(chunk_view{
                            static_cast<std::uint64_t*>(pending.hand_over()), size, offset, false});
                        return attempt_status::failed;
                    }
                    base = pending.hand_over();
                }

                // Capacity was reserved for the largest attempt, so this never reallocates.
                chunks_.push_back(
                    chunk_view{static_cast<std::uint64_t*>(base), size, offset, large});
            }
        }
        return attempt_status::ok;
    };

    std::uint64_t bytes = options.region_bytes;
    for (;;) {
        // A stop returns at once and leaves whatever the region holds (chunks locked before
        // it, a working-set raise, an enabled privilege) to release(). The coordinator calls
        // that after Completed, so the stop bound never includes a teardown that grows with the
        // bytes already locked. plan_ stays unset, so nothing held is exposed as a region.
        if (stop.stop_requested()) {
            outcome.status = allocation_status::stopped;
            return outcome;
        }
        ++outcome.attempts;
        outcome.large_page_fallbacks = 0;

        const region_plan plan = plan_region(bytes, options.worker_count);
        if (plan.slice_bytes == 0 || plan.tested_bytes < options.min_region_bytes) {
            // Only a first attempt can get here: every retry size is a multiple of
            // worker_count x 2 MiB at or above the minimum, so its plan tests all of it.
            // tested_bytes is the requested size rounded down to that multiple, so when it is 0 or
            // below the minimum, no size up to the request plans more. The request is an upper
            // bound the host sized to leave memory free, so it is refused rather than grown, and
            // before any chunk is allocated. No OS call failed, so os_error stays 0.
            char buffer[256];
            if (plan.slice_bytes == 0) {
                std::snprintf(buffer, sizeof(buffer),
                              "Region of %llu MiB cannot give each of %u workers a 2 MiB slice",
                              bytes / k_mib, options.worker_count);
            } else {
                // The minimum is rounded up to whole MiB, so the text never shows a planned size
                // equal to the minimum it falls below.
                const std::uint64_t min_mib = options.min_region_bytes / k_mib +
                                              (options.min_region_bytes % k_mib != 0 ? 1u : 0u);
                std::snprintf(buffer, sizeof(buffer),
                              "Region of %llu MiB over %u workers tests %llu MiB, below the %llu "
                              "MiB minimum",
                              bytes / k_mib, options.worker_count, plan.tested_bytes / k_mib,
                              min_mib);
            }
            copy_utf8_truncated(outcome.end_detail, sizeof(outcome.end_detail), buffer);
            release();
            outcome.status = allocation_status::insufficient_memory;
            return outcome;
        }

        attempt_failure failure{};
        const attempt_status status = run_attempt(plan, failure);
        if (status == attempt_status::ok) {
            plan_ = plan;
            outcome.status = allocation_status::ok;
            return outcome;
        }
        if (status == attempt_status::stopped) {
            // The attempt's chunks stay held for release(), as for a stop before an attempt.
            outcome.status = allocation_status::stopped;
            return outcome;
        }

        // The attempt failed: drop its chunks and working-set raise, but keep the privilege
        // state for the retry.
        const chunks_freed freed = free_chunks(chunks_, free_override_);
        if (!chunks_.empty()) {
            // Windows refused to free a chunk of the attempt. It stays owned, with the
            // working-set raise that covers it and the privilege state, until release() frees
            // it. No retry is planned over it: slice() indexes chunks_ by position, and the
            // list's capacity covers one attempt's chunks only.
            free_error_ = freed.free_error;
            set_detail(outcome, "VirtualFree", freed.free_error, bytes / k_mib,
                       options.min_region_bytes / k_mib);
            outcome.status = allocation_status::insufficient_memory;
            return outcome;
        }
        restore_working_set(ws_contribution_);

        const std::uint64_t next =
            next_retry_bytes(bytes, options.min_region_bytes, options.worker_count);
        if (next == 0) {
            set_detail(outcome, failure.call, failure.error, bytes / k_mib,
                       options.min_region_bytes / k_mib);
            release();
            outcome.status = allocation_status::insufficient_memory;
            return outcome;
        }
        debug_log(L"%hs failed (Windows error %lu) at %llu MiB; retrying at %llu MiB "
                  L"(RQ_E_LOCK_FAILED)",
                  failure.call, failure.error, bytes / k_mib, next / k_mib);
        bytes = next;
    }
}

std::uint64_t memory_region::release() noexcept {
    const chunks_freed freed = free_chunks(chunks_, free_override_);
    // Unusable from here on, even while a chunk Windows refused to free is still held.
    plan_ = region_plan{};

    if (!chunks_.empty()) {
        // The held chunk keeps the working-set raise that covers it until a later call frees
        // it, so the minimum is never lowered beneath pages that may still be locked.
        free_error_ = freed.free_error;
    } else {
        free_error_ = ERROR_SUCCESS;
        // The seam is needed only while a chunk it must free is held.
        free_override_ = nullptr;
        // Withdrawn only after every chunk is unlocked and freed, so the lower minimum never has
        // to hold this region's locked pages.
        restore_working_set(ws_contribution_);
    }
    // Every release in the process retries the chunks destroyed regions left behind, so each
    // keeps an owner that retries it. They are not this region's, so freed.bytes leaves them out.
    retry_orphaned_chunks();

    // Only a privilege this object enabled is disabled again; one that was already enabled is
    // left as it was found. A held chunk does not need it: the privilege governs new large-page
    // allocations only, not freeing one.
    if (privilege_enabled_by_us_) {
        bool was_enabled = false;
        DWORD error = ERROR_SUCCESS;
        if (!adjust_lock_memory_privilege(false, was_enabled, error)) {
            debug_log(L"SeLockMemoryPrivilege could not be disabled again (Windows error %lu)",
                      error);
        }
        privilege_enabled_by_us_ = false;
    }
    return freed.bytes;
}

std::uint64_t memory_region::held_bytes() const noexcept {
    std::uint64_t bytes = 0;
    for (const chunk_view& chunk : chunks_) {
        bytes += chunk.bytes;
    }
    return bytes;
}

bool memory_region::allocated() const noexcept {
    return plan_.tested_bytes != 0;
}

const region_plan& memory_region::plan() const noexcept {
    return plan_;
}

std::uint64_t memory_region::tested_bytes() const noexcept {
    return plan_.tested_bytes;
}

std::uint64_t memory_region::slice_bytes() const noexcept {
    return plan_.slice_bytes;
}

std::uint32_t memory_region::worker_count() const noexcept {
    return plan_.worker_count;
}

std::uint32_t memory_region::page_mode() const noexcept {
    if (!allocated()) {
        return 0;
    }
    bool any_large = false;
    bool any_standard = false;
    for (const chunk_view& chunk : chunks_) {
        (chunk.large_pages ? any_large : any_standard) = true;
    }
    if (any_large) {
        return any_standard ? RQ_PAGE_MODE_MIXED : RQ_PAGE_MODE_LARGE;
    }
    return RQ_PAGE_MODE_STANDARD;
}

std::uint64_t memory_region::large_page_bytes() const noexcept {
    if (!allocated()) {
        return 0;
    }
    std::uint64_t bytes = 0;
    for (const chunk_view& chunk : chunks_) {
        if (chunk.large_pages) {
            bytes += chunk.bytes;
        }
    }
    return bytes;
}

slice_view memory_region::slice(std::uint32_t worker_index) const noexcept {
    // An index out of range, or an unallocated region, yields an empty view as the header
    // documents, rather than a debug assertion, so a caller may probe any index safely.
    if (worker_index >= plan_.worker_count) {
        return slice_view{};
    }
    const std::size_t per_slice = plan_.chunks_per_slice;
    return slice_view{
        worker_index, worker_index * plan_.slice_bytes, plan_.slice_bytes,
        std::span<const chunk_view>(chunks_).subspan(worker_index * per_slice, per_slice)};
}

std::span<const chunk_view> memory_region::chunks() const noexcept {
    if (!allocated()) {
        return {};
    }
    return chunks_;
}

std::uint64_t orphaned_bytes() noexcept {
    return process_orphans().bytes();
}

void retry_orphaned_chunks() noexcept {
    process_orphans().retry();
}

}  // namespace rq::engine
