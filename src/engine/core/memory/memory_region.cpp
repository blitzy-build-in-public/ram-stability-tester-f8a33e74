/**
 * @file memory_region.cpp
 * @brief Chunked allocation and locking of the region under test, with large pages where the
 *        token allows them, shrink-and-retry on a locking shortfall and RAII release.
 *
 * Allocation and locking run in Preparing on the coordinator thread, one 256 MiB chunk at a
 * time with a stop check before each, so a stop waits for at most one chunk's commit-and-lock
 * and neither the UI nor Windows is starved while gigabytes are faulted in and locked.
 *
 * Stress bound: this file touches page contents only as far as VirtualLock faults them in, and
 * issues no cache flushes. Every OS change it makes is process-local (the working-set limits and
 * the enabled state of a privilege the token already holds), and release() undoes both.
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

namespace rq::engine {

namespace {

/** Closes a kernel handle on scope exit; used for the process token. */
class scoped_handle {
public:
    explicit scoped_handle(HANDLE handle) noexcept : handle_(handle) {}
    ~scoped_handle() {
        if (handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
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

/** Fills os_error and end_detail of an insufficient_memory outcome for a failed OS call. */
void set_detail(allocation_outcome& outcome, const char* call, DWORD error,
                std::uint64_t attempted_mib, std::uint64_t min_mib) noexcept {
    char buffer[256];
    std::snprintf(buffer, sizeof(buffer),
                  "%s failed (Windows error %lu) at %llu MiB; %llu MiB minimum not lockable", call,
                  error, attempted_mib, min_mib);
    outcome.os_error = error;
    copy_utf8_truncated(outcome.end_detail, sizeof(outcome.end_detail), buffer);
}

/**
 * Owns a 4 KiB-page allocation from VirtualAlloc until it is locked and handed to the chunk
 * list, so a failed lock frees it at once (it is not yet in the list that release() walks).
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

/**
 * Unlocks (4 KiB-page chunks only; large pages are never locked) and frees every chunk, then
 * clears the list. A failed unlock does not keep the chunk from being freed.
 *
 * @return Bytes whose VirtualFree succeeded.
 */
std::uint64_t free_chunks(std::vector<chunk_view>& chunks) noexcept {
    std::uint64_t freed = 0;
    for (const chunk_view& chunk : chunks) {
        if (!chunk.large_pages && !VirtualUnlock(chunk.base, chunk.bytes)) {
            const DWORD error = GetLastError();
            // A chunk locked by a test's lock override may never have been locked for real.
            if (error != ERROR_NOT_LOCKED) {
                debug_log(L"VirtualUnlock of the chunk at %llu MiB failed (Windows error %lu)",
                          chunk.logical_offset / k_mib, error);
            }
        }
        if (VirtualFree(chunk.base, 0, MEM_RELEASE)) {
            freed += chunk.bytes;
        } else {
            const DWORD error = GetLastError();
            debug_log(L"VirtualFree of the chunk at %llu MiB failed (Windows error %lu)",
                      chunk.logical_offset / k_mib, error);
        }
    }
    chunks.clear();
    return freed;
}

/** Restores working-set limits read by GetProcessWorkingSetSizeEx; logs a failure. */
void restore_working_set(std::uint64_t minimum, std::uint64_t maximum,
                         std::uint32_t flags) noexcept {
    if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), static_cast<SIZE_T>(minimum),
                                    static_cast<SIZE_T>(maximum), flags)) {
        debug_log(L"Restoring the working-set limits failed (Windows error %lu)", GetLastError());
    }
}

/** The failing OS call of an attempt and its GetLastError(), captured right after the call. */
struct attempt_failure {
    const char* call = "";
    DWORD error = ERROR_SUCCESS;
};

enum class attempt_status { ok, stopped, failed };

/** x rounded down to a multiple of unit; unit must be non-zero. */
constexpr std::uint64_t round_down(std::uint64_t x, std::uint64_t unit) noexcept {
    return x / unit * unit;
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

memory_region::memory_region() noexcept = default;

memory_region::~memory_region() {
    release();
}

allocation_outcome memory_region::allocate(const region_options& options, const stop_signal& stop) {
    // A region still held from an earlier call is released first, so a repeated call never
    // leaks. On an unallocated region this changes nothing.
    release();

    allocation_outcome outcome{};

    // The first attempt is the largest and no retry needs more chunks, so this one reservation
    // is the only operation that can throw. It runs before any OS state is touched, so an
    // exception leaves nothing allocated, locked, raised or enabled.
    const region_plan first_plan = plan_region(options.region_bytes, options.worker_count);
    chunks_.reserve(static_cast<std::size_t>(first_plan.worker_count) *
                    first_plan.chunks_per_slice);

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

    // Whether the limits in force before this call's first raise have been saved. They are
    // read once per call, so every restore returns to them even after a failed restore.
    bool limits_saved = false;

    // VirtualLock locks only up to the minimum working set, so the minimum is raised to the
    // bytes still to be locked plus headroom for the process's own pages. Both limits are soft
    // (QUOTA_LIMITS_HARDWS_*_DISABLE): the minimum never forces other processes to be trimmed
    // and the maximum never caps this one. The raise needs SeIncreaseWorkingSetPrivilege, which
    // the default policy grants to Users. Holding it suffices; it need not be enabled, and a
    // standard user's token holds it disabled, so a 1 GiB region locks without elevation.
    auto raise_working_set = [&](std::uint64_t locked_bytes, attempt_failure& failure) -> bool {
        if (!limits_saved) {
            SIZE_T saved_min = 0;
            SIZE_T saved_max = 0;
            DWORD saved_flags = 0;
            if (!GetProcessWorkingSetSizeEx(GetCurrentProcess(), &saved_min, &saved_max,
                                            &saved_flags)) {
                failure = attempt_failure{"GetProcessWorkingSetSizeEx", GetLastError()};
                return false;
            }
            saved_ws_min_ = saved_min;
            saved_ws_max_ = saved_max;
            saved_ws_flags_ = saved_flags;
            limits_saved = true;
        }
        const std::uint64_t minimum = locked_bytes + k_working_set_headroom_bytes;
        const std::uint64_t maximum =
            std::max(saved_ws_max_, minimum + k_working_set_headroom_bytes);
        if (!SetProcessWorkingSetSizeEx(
                GetCurrentProcess(), static_cast<SIZE_T>(minimum), static_cast<SIZE_T>(maximum),
                QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE)) {
            failure = attempt_failure{"SetProcessWorkingSetSizeEx", GetLastError()};
            return false;
        }
        ws_raised_ = true;
        ws_min_current_ = minimum;
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
                    if (!ws_raised_ && !raise_working_set(plan.tested_bytes - offset, failure)) {
                        return attempt_status::failed;
                    }
                    void* const allocation =
                        VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
                    if (allocation == nullptr) {
                        failure = attempt_failure{"VirtualAlloc", GetLastError()};
                        return attempt_status::failed;
                    }
                    pending_allocation pending(allocation);
                    const bool locked = options.lock_override
                                            ? options.lock_override(allocation, size)
                                            : VirtualLock(allocation, size) != FALSE;
                    if (!locked) {
                        // The lock override reports its failure through the last error too, and
                        // stands in for VirtualLock, so the failure is named after it.
                        failure = attempt_failure{"VirtualLock", GetLastError()};
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
        if (stop.stop_requested()) {
            release();
            outcome.status = allocation_status::stopped;
            return outcome;
        }
        ++outcome.attempts;
        outcome.large_page_fallbacks = 0;

        const region_plan plan = plan_region(bytes, options.worker_count);
        if (plan.slice_bytes == 0) {
            // Only a first attempt can get here, since every retry size is a multiple of
            // worker_count x 2 MiB, and no smaller size could plan a slice either.
            char buffer[256];
            std::snprintf(buffer, sizeof(buffer),
                          "Region of %llu MiB cannot give each of %u workers a 2 MiB slice",
                          bytes / k_mib, options.worker_count);
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
            release();
            outcome.status = allocation_status::stopped;
            return outcome;
        }

        // The attempt failed: drop its chunks and working-set raise, but keep the privilege
        // state for the retry.
        free_chunks(chunks_);
        if (ws_raised_) {
            restore_working_set(saved_ws_min_, saved_ws_max_, saved_ws_flags_);
            ws_raised_ = false;
            ws_min_current_ = 0;
        }

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
    const std::uint64_t released = free_chunks(chunks_);
    plan_ = region_plan{};

    // Restored only after every chunk is unlocked and freed, so the lower minimum never has to
    // hold locked pages.
    if (ws_raised_) {
        restore_working_set(saved_ws_min_, saved_ws_max_, saved_ws_flags_);
        ws_raised_ = false;
        ws_min_current_ = 0;
    }

    // Only a privilege this object enabled is disabled again; one that was already enabled is
    // left as it was found.
    if (privilege_enabled_by_us_) {
        bool was_enabled = false;
        DWORD error = ERROR_SUCCESS;
        if (!adjust_lock_memory_privilege(false, was_enabled, error)) {
            debug_log(L"SeLockMemoryPrivilege could not be disabled again (Windows error %lu)",
                      error);
        }
        privilege_enabled_by_us_ = false;
    }
    return released;
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

}  // namespace rq::engine
