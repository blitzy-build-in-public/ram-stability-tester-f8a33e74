/**
 * @file memory_region.h
 * @brief The region under test: one slice per worker, built from separately allocated and
 *        locked chunks, with shrink-and-retry, per-chunk large-page fallback and RAII release.
 *
 * The Windows rules behind the design:
 * - VirtualLock can lock only up to the process's minimum working-set size. allocate()
 *   therefore raises the minimum with SetProcessWorkingSetSizeEx before locking, to the
 *   attempt's region plus k_working_set_headroom_bytes. Raising it needs
 *   SeIncreaseWorkingSetPrivilege, which the default policy grants to Users, so a 1 GiB region
 *   locks without elevation. The limits in force before the first raise are restored by
 *   release().
 * - Large pages (VirtualAlloc with MEM_LARGE_PAGES) need SeLockMemoryPrivilege enabled in the
 *   token. "Lock pages in memory" is assigned to nobody by default, Administrators included, so
 *   large pages are attempted only when the token already holds the privilege. allocate() then
 *   enables it if it is disabled, and release() disables it again. The privilege is never
 *   assigned. Large-page memory is non-pageable by definition, so large-page chunks are never
 *   passed to VirtualLock.
 * - A large-page allocation can fail from physical fragmentation even when enough memory is
 *   free. Each large-page chunk that fails falls back to 4 KiB pages for that chunk alone, and
 *   page_mode() reports the mix.
 * - Every chunk is allocated, committed and locked by its own calls, with a stop check before
 *   each, so a stop during preparation waits for at most one chunk of k_chunk_bytes. Release is
 *   chunk by chunk as well.
 * - No documented user-mode API yields physical addresses, so every address here is virtual.
 *
 * Deliberately free of <windows.h>: Win32 handles and values are held as fixed-width integers,
 * and the process token is opened, used and closed inside memory_region.cpp, which also
 * includes core/clock.h for stop_signal. The AVX2 and AVX-512 kernel translation units must
 * not include this header.
 */
#pragma once

#include <cstdint>
#include <cstddef>
#include <functional>
#include <span>
#include <vector>

#include "ramquick_engine.h"

namespace rq::engine {

// Defined in core/clock.h, which includes <windows.h>; a forward declaration keeps it out of
// this header.
class stop_signal;

/** One mebibyte, in bytes. */
inline constexpr std::uint64_t k_mib = 1024ull * 1024ull;

/**
 * Slice granularity, 2 MiB: every slice, and therefore every chunk, is a multiple of it.
 *
 * It is the x64 large-page size (GetLargePageMinimum), so any chunk can be a large-page
 * allocation, and every access-order stride, 4 KiB block and 1 MiB range divides a slice evenly.
 */
inline constexpr std::uint64_t k_slice_granularity_bytes = 2 * k_mib;

/**
 * Allocation unit, 256 MiB: the most memory one VirtualAlloc and one VirtualLock call cover.
 *
 * It bounds how long a stop waits during preparation, and a failed large-page allocation costs
 * one chunk of large pages rather than a whole slice.
 */
inline constexpr std::uint64_t k_chunk_bytes = 256 * k_mib;

/**
 * Working-set headroom, 256 MiB: the minimum working set is raised to the attempt's region plus
 * this, so the process's own code, stacks and heaps still fit beside the locked pages.
 */
inline constexpr std::uint64_t k_working_set_headroom_bytes = 256 * k_mib;

/** Each retry attempts this percentage of the previous size: the region shrinks 10 % per retry. */
inline constexpr std::uint32_t k_retry_percent = 90;

static_assert(k_chunk_bytes % k_slice_granularity_bytes == 0,
              "chunks must split slices into 2 MiB multiples");
static_assert(k_retry_percent > 0 && k_retry_percent < 100, "every retry must shrink the region");

/**
 * How a region splits into worker slices, and each slice into chunks. Produced by plan_region().
 *
 * Every slice has the same layout: full_chunks_per_slice chunks of k_chunk_bytes, then one
 * smaller final chunk of final_chunk_bytes when that is non-zero.
 */
struct region_plan {
    /** Number of slices, one per worker. */
    std::uint32_t worker_count = 0;
    /** Bytes per slice; a multiple of k_slice_granularity_bytes. */
    std::uint64_t slice_bytes = 0;
    /** slice_bytes x worker_count: the bytes under test (rq_prepared.tested_bytes). */
    std::uint64_t tested_bytes = 0;
    /** Chunks of exactly k_chunk_bytes in each slice. */
    std::uint32_t full_chunks_per_slice = 0;
    /** Size of each slice's smaller final chunk, a multiple of 2 MiB; 0 when there is none. */
    std::uint64_t final_chunk_bytes = 0;
    /** full_chunks_per_slice, plus 1 when final_chunk_bytes is non-zero. */
    std::uint32_t chunks_per_slice = 0;
};

/**
 * Splits a region evenly across the workers, and each slice into chunks.
 *
 * - slice_bytes = (region_bytes / worker_count) rounded down to k_slice_granularity_bytes.
 * - full_chunks_per_slice = slice_bytes / k_chunk_bytes, and
 *   final_chunk_bytes = slice_bytes % k_chunk_bytes.
 * - tested_bytes = slice_bytes x worker_count, so it never exceeds region_bytes.
 *
 * The managed sizing (MemorySizing.cs) passes multiples of worker_count x 2 MiB, so in
 * production the first attempt's tested_bytes equals region_bytes. Native tests may pass
 * unrounded sizes. allocate() then uses this plan as it is when its tested_bytes reaches
 * min_region_bytes, and refuses the request otherwise. Examples: 32 GiB over 32 workers gives
 * 1024 MiB slices of four full chunks and no final chunk; 30 GiB over 24 workers gives 1280 MiB
 * slices of five full chunks; 1000 MiB over 3 workers gives 332 MiB slices (256 + 76 MiB) and
 * 996 MiB tested; 1 GiB over 3 workers gives 340 MiB slices and 1020 MiB tested, which a 1 GiB
 * minimum refuses.
 *
 * Pure: makes no OS call; callable from any thread.
 *
 * @param region_bytes Bytes requested for the region.
 * @param worker_count Number of workers. 0 yields an all-zero plan.
 * @return The plan. When the region holds less than 2 MiB per worker, every field except
 *         worker_count is 0.
 */
region_plan plan_region(std::uint64_t region_bytes, std::uint32_t worker_count) noexcept;

/**
 * The size to attempt after an attempt at attempted_bytes failed: one step of the
 * shrink-and-retry sequence.
 *
 * With unit = worker_count x k_slice_granularity_bytes:
 * - shrunk = (attempted_bytes x k_retry_percent / 100) rounded down to a multiple of unit;
 * - floor = min_region_bytes rounded up to a multiple of unit;
 * - the result is shrunk when shrunk >= floor. Otherwise it is floor when floor is below
 *   attempted_bytes, one final attempt clamped to the minimum; otherwise it is 0, meaning no
 *   further attempt.
 *
 * The clamped final attempt is this engine's reading of "the size shrinks 10 % per retry down
 * to 1 GiB": the retries end with an attempt at the minimum itself, not at the last 90 % step
 * that stays above it. Every non-zero result is a multiple of unit, so plan_region() of it has
 * tested_bytes equal to it, and tested_bytes >= min_region_bytes holds for every retry. The
 * results strictly decrease, so the sequence always ends with 0. Example with 8 workers (unit
 * 16 MiB) and a 1 GiB minimum: 1200 MiB -> 1072 MiB -> 1024 MiB (clamped) -> 0.
 *
 * Pure: makes no OS call; callable from any thread.
 *
 * @param attempted_bytes Size of the attempt that failed.
 * @param min_region_bytes Smallest acceptable region (rq_session_config.min_region_bytes).
 * @param worker_count Number of workers. 0 yields 0.
 * @return The next size to attempt, or 0 when the sequence is exhausted.
 */
std::uint64_t next_retry_bytes(std::uint64_t attempted_bytes, std::uint64_t min_region_bytes,
                               std::uint32_t worker_count) noexcept;

/**
 * One chunk: a single VirtualAlloc allocation, committed and locked, holding a contiguous run
 * of one worker's slice.
 */
struct chunk_view {
    /**
     * First word of the chunk. At least 64 KiB aligned (the allocation granularity), and 2 MiB
     * aligned for large pages, so the alignment of every SIMD width holds.
     */
    std::uint64_t* base = nullptr;
    /** Size in bytes; a multiple of k_slice_granularity_bytes. */
    std::uint64_t bytes = 0;
    /**
     * Offset of the chunk in the logical region, which is the slices concatenated in worker
     * order with each slice's chunks in order: chunk j of slice i starts at
     * i x slice_bytes + j x k_chunk_bytes. Region offsets (rq_error_record.region_offset) are
     * measured on this scale.
     */
    std::uint64_t logical_offset = 0;
    /** True for large pages; false for 4 KiB pages locked with VirtualLock. */
    bool large_pages = false;
};

/**
 * The address region offset 0 would have if the region were one allocation continuing through
 * this chunk: base - logical_offset.
 *
 * It is the access_context::region_base for every range inside c, so that a word's address
 * minus region_base is its logical region offset. The subtraction is modulo 2^64 by design and
 * the result is never dereferenced (see core/memory/memory_access.h).
 */
inline std::uintptr_t logical_base(const chunk_view& c) noexcept {
    return reinterpret_cast<std::uintptr_t>(c.base) - c.logical_offset;
}

/**
 * One worker's slice of the region, as its chunks in ascending logical order.
 *
 * Chunks are separate allocations, so the runner computes its windows (at most 64 MiB) per
 * chunk and never issues a range that spans two chunks. The final chunk need not be a multiple
 * of 64 MiB, so its last window can be shorter. In Half thread mode the active even worker i
 * covers slice(i) and, when worker i + 1 exists, slice(i + 1).
 */
struct slice_view {
    /** Index of the worker the slice belongs to. */
    std::uint32_t worker_index = 0;
    /** worker_index x slice_bytes: the slice's offset in the logical region. */
    std::uint64_t logical_offset = 0;
    /** Size of the slice; the sum of its chunks' bytes. */
    std::uint64_t bytes = 0;
    /** The slice's chunks, ascending; valid until memory_region::release(). */
    std::span<const chunk_view> chunks;
};

/**
 * Locks one 4 KiB-page chunk in place of VirtualLock.
 *
 * Called on the coordinator thread with the chunk's base and size. Returns true on success. On
 * failure it returns false with the Windows last-error set, exactly as VirtualLock does, and
 * allocate() treats that as a lock failure of the attempt. It must not throw. An empty function
 * means VirtualLock. The seam exists only so that test_memory_region.cpp can force
 * shrink-and-retry deterministically; production always passes it empty.
 */
using lock_function = std::function<bool(void* address, std::size_t bytes)>;

/**
 * What allocate() builds.
 *
 * The session's stop_signal is passed as allocate()'s second parameter rather than held here,
 * so that this stays a default-constructible aggregate that callers fill field by field.
 */
struct region_options {
    /** Size of the first attempt (rq_session_config.region_bytes). */
    std::uint64_t region_bytes = 0;
    /**
     * Smallest acceptable region (rq_session_config.min_region_bytes, 1 GiB from the host).
     * No attempt tests fewer bytes: a first size whose plan falls below it is refused, and no
     * retry goes below it.
     */
    std::uint64_t min_region_bytes = 0;
    /** Number of slices to build, one per worker; must be non-zero. */
    std::uint32_t worker_count = 0;
    /**
     * Whether the token holds SeLockMemoryPrivilege, from lock_memory_privilege_held()
     * (core/system_info.h). Large pages are attempted only when it is true.
     */
    bool lock_privilege_held = false;
    /** Replaces VirtualLock when non-empty; a test seam, empty in production. */
    lock_function lock_override;
};

/** How allocate() ended. */
enum class allocation_status : std::uint32_t {
    /** The region is allocated and locked, and the views are valid. */
    ok,
    /** The stop signal was raised before a chunk; everything allocated was released. */
    stopped,
    /**
     * No attempt down to the minimum could be allocated and locked, or the first size's plan
     * has no slice or tests fewer than min_region_bytes and was refused before any allocation.
     * Everything was released.
     */
    insufficient_memory,
};

/**
 * Result of allocate().
 *
 * The session maps it as follows:
 * - ok: preparation continues with the workers.
 * - stopped: the run ends with the stop reason already pending on the stop signal.
 * - insufficient_memory: the session calls
 *   stop_signal::request(RQ_END_INSUFFICIENT_MEMORY, os_error, end_detail), then raises
 *   Completed and Released with no Prepared.
 */
struct allocation_outcome {
    /** How the call ended. */
    allocation_status status = allocation_status::ok;
    /**
     * GetLastError() of the call that failed last: SetProcessWorkingSetSizeEx, VirtualAlloc or
     * VirtualLock (or the lock override). Filled for insufficient_memory only, and 0 otherwise,
     * including for a refused request, where no OS call failed.
     */
    std::uint32_t os_error = 0;
    /** Sizes attempted: 1 for the first attempt, plus one per retry. */
    std::uint32_t attempts = 0;
    /** Large-page chunks of the last attempt that fell back to 4 KiB pages. */
    std::uint32_t large_page_fallbacks = 0;
    /**
     * English diagnostic for insufficient_memory, naming the failing call and the last size
     * attempted; for a refused request it names the requested size and worker count, and the
     * planned and minimum sizes when a slice could be planned. UTF-8, NUL-terminated. Empty for
     * ok and stopped.
     */
    char end_detail[RQ_END_DETAIL_SIZE] = {};
};

/**
 * The locked region under test, split into one slice per worker and allocated in chunks.
 *
 * @par Ownership and threading
 * Owned by the session. allocate() and release() run on the coordinator thread. After
 * allocate() returns ok, the views are immutable until release() and may be read concurrently
 * by every worker. Non-copyable and non-movable, because workers hold views into it while they
 * run.
 *
 * @par Views and release
 * release() clears the chunk list, so every slice_view, chunk_view span and base pointer taken
 * earlier dangles afterwards. Tests that confirm VirtualQuery(base).State == MEM_FREE must copy
 * the bases first.
 *
 * @par Exceptions
 * allocate() throws nothing once the first OS allocation has been made. The one operation that
 * can throw is the reservation of the chunk list (std::bad_alloc), which runs before any
 * VirtualAlloc and is sized for the first attempt, the largest, so no retry grows it. Any
 * exception therefore leaves no chunk allocated, and the coordinator converts it into
 * RQ_END_INTERNAL_FAULT.
 *
 * @par Release on every exit path
 * The coordinator calls release() after Completed for every end reason. rq_session_destroy
 * releases the region when the coordinator never ran, and the destructor is the RAII backstop.
 * If the process is terminated, Windows reclaims the locked pages with it.
 */
class memory_region {
public:
    /** An unallocated region; makes no OS call. */
    memory_region() noexcept;

    /** Calls release(), so no exit path leaves memory allocated or locked. */
    ~memory_region();

    memory_region(const memory_region&) = delete;
    memory_region& operator=(const memory_region&) = delete;
    memory_region(memory_region&&) = delete;
    memory_region& operator=(memory_region&&) = delete;

    /**
     * Plans, allocates and locks the region, shrinking and retrying until an attempt succeeds
     * or the sequence of next_retry_bytes() is exhausted.
     *
     * The first size is options.region_bytes. When its plan has no slice, or its tested_bytes
     * is below options.min_region_bytes, no size up to it can reach the minimum, so the call
     * returns insufficient_memory before any allocation, with attempts 1 and os_error 0. Every
     * retry size plans tested_bytes equal to it and at least the minimum, so no attempt ever
     * tests fewer bytes than options.min_region_bytes.
     *
     * Each attempt plans its size with plan_region(), raises the minimum working set to
     * tested_bytes + k_working_set_headroom_bytes, and then, slice by slice and chunk by chunk,
     * checks the stop signal, allocates the chunk and locks it. With
     * options.lock_privilege_held, each chunk is first tried as a large-page allocation, and a
     * chunk that fails falls back to 4 KiB pages. 4 KiB-page chunks are locked with VirtualLock,
     * or with options.lock_override when it is set. A failure to raise the working set, to
     * allocate a chunk or to lock it releases that attempt's chunks and retries at
     * next_retry_bytes() of the attempt's size; when that is 0, the call returns
     * insufficient_memory. A raised stop releases everything and returns stopped.
     *
     * A region that is still allocated is released first, so a repeated call never leaks.
     *
     * @param options What to build; options.worker_count must be non-zero.
     * @param stop The session's stop signal, checked before every chunk. It is a parameter
     *        rather than a member of region_options, which keeps that an aggregate.
     * @return The outcome. On ok, plan(), slice() and chunks() describe the region.
     * @throws std::bad_alloc only from reserving the chunk list, before any OS allocation.
     */
    allocation_outcome allocate(const region_options& options, const stop_signal& stop);

    /**
     * Releases the region chunk by chunk, VirtualUnlock (for 4 KiB-page chunks) and then
     * VirtualFree(MEM_RELEASE) for each. Then restores the working-set limits saved before the
     * first raise and, when allocate() enabled SeLockMemoryPrivilege, disables it again.
     *
     * Idempotent and noexcept, so it is safe from the destructor and from every exit path. A
     * failed unlock does not stop the chunk from being freed. Clears the chunk list and resets
     * the plan, so the accessors report an unallocated region afterwards.
     *
     * @return Bytes released (the Released callback's released_bytes); 0 when nothing was
     *         allocated.
     */
    std::uint64_t release() noexcept;

    /** True after allocate() returned ok, until release(). */
    bool allocated() const noexcept;

    /** Plan of the allocated attempt; all zero when not allocated. */
    const region_plan& plan() const noexcept;

    /** slice_bytes() x worker_count(): rq_prepared.tested_bytes; 0 when not allocated. */
    std::uint64_t tested_bytes() const noexcept;

    /** Bytes per slice; 0 when not allocated. */
    std::uint64_t slice_bytes() const noexcept;

    /** Number of slices, one per worker; 0 when not allocated. */
    std::uint32_t worker_count() const noexcept;

    /**
     * RQ_PAGE_MODE_STANDARD when every chunk uses 4 KiB pages, RQ_PAGE_MODE_LARGE when every
     * chunk uses large pages, and RQ_PAGE_MODE_MIXED when both occur because some large-page
     * chunks fell back. 0 when not allocated. Reported as rq_prepared.page_mode.
     */
    std::uint32_t page_mode() const noexcept;

    /** Bytes held in large-page chunks; 0 when there are none or nothing is allocated. */
    std::uint64_t large_page_bytes() const noexcept;

    /**
     * Worker worker_index's slice. Its chunks are entries
     * [worker_index x chunks_per_slice, (worker_index + 1) x chunks_per_slice) of chunks().
     *
     * @param worker_index Below worker_count().
     * @return The slice; an empty view (no bytes, no chunks) when worker_index is out of range
     *         or nothing is allocated.
     */
    slice_view slice(std::uint32_t worker_index) const noexcept;

    /** Every chunk of every slice, in ascending logical order; empty when not allocated. */
    std::span<const chunk_view> chunks() const noexcept;

private:
    // Every chunk of every slice, in ascending logical order.
    std::vector<chunk_view> chunks_;
    // Plan of the allocated attempt; all zero when not allocated.
    region_plan plan_{};
    // Working-set limits and QUOTA_LIMITS_* flags read by GetProcessWorkingSetSizeEx before the
    // first raise (SIZE_T and DWORD held as fixed-width integers); release() restores them.
    std::uint64_t saved_ws_min_ = 0;
    std::uint64_t saved_ws_max_ = 0;
    std::uint32_t saved_ws_flags_ = 0;
    // True once allocate() has changed the working-set limits, so release() restores them.
    bool ws_raised_ = false;
    // Minimum working-set size this object last set.
    std::uint64_t ws_min_current_ = 0;
    // True when allocate() enabled SeLockMemoryPrivilege, held but disabled until then, so
    // release() disables it again. The token handle itself is never stored.
    bool privilege_enabled_by_us_ = false;
};

}  // namespace rq::engine
