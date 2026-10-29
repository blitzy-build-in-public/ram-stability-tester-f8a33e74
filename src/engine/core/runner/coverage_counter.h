/**
 * @file coverage_counter.h
 * @brief Verified-byte accounting: per-worker pending counts published to the session and step
 *        totals that coverage is reported from.
 *
 * Coverage % = 100 x bytes verified / bytes under test. A verified byte is a byte whose 8-byte
 * word was read by a verification primitive and compared with its expected value within the
 * current step. The counting rule every caller follows:
 * - The step runner calls add() with the exact byte count (words x 8) of each
 *   IMemoryAccess::verify or verify_and_stream call it issued, and nothing else: reread_word,
 *   streams and flushes are never counted.
 * - Moving inversion verifies in both sweeps of a pass, so it calls add() for both and counts
 *   twice per pass.
 * - Bytes written but not yet verified when a stop arrives are never counted, because only
 *   verification calls reach add().
 * - In Half thread mode the active even worker covers its own and its partner's slice and
 *   counts both through its own slot; the idle partner's slot stays untouched.
 *
 * Each worker accumulates into its own cache-line-sized slot with plain (non-atomic)
 * arithmetic and adds the slot to the shared totals once per k_coverage_publish_bytes, so the
 * verification loops never contend on a shared line. Every phase task ends with publish(),
 * including an early return on stop, so once the phase barrier completes the totals equal the
 * bytes the workers verified exactly: the independent count of counting_memory_access in the
 * native tests.
 *
 * Header-only and free of <windows.h> and of engine headers. The AVX2 and AVX-512 kernel
 * translation units must not include it.
 */
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>

namespace rq::engine {

/**
 * Publication granularity of a worker's verified bytes, 1 MiB.
 *
 * A worker adds its pending count to the session and step totals once at least this many bytes
 * are pending, so a total read mid-phase lags the true count by less than this per worker. It is
 * also the block size after which workers check the session's stop flag, which bounds both the
 * reporting lag and the stop latency by the same unit of work.
 */
inline constexpr std::uint64_t k_coverage_publish_bytes = 1ull << 20;

/**
 * Coverage percentage: 100 x verified_bytes / tested_bytes.
 *
 * Coverage counts bytes verified, not bytes written: moving inversion counts both verify
 * sweeps, re-reads never count, and bytes written but not verified when a stop arrives never
 * count. 100 % means every byte under test was verified once; 1000 % means ten times.
 *
 * Pure; callable from any thread.
 *
 * @param verified_bytes Bytes verified, from coverage_counter::session_total or step_total.
 * @param tested_bytes Bytes under test (rq_prepared.tested_bytes).
 * @return The percentage, or 0 when tested_bytes is 0.
 */
constexpr double coverage_percent(std::uint64_t verified_bytes,
                                  std::uint64_t tested_bytes) noexcept {
    return tested_bytes == 0
               ? 0.0
               : 100.0 * static_cast<double>(verified_bytes) / static_cast<double>(tested_bytes);
}

#ifdef _MSC_VER
#pragma warning(push)
// C4324: the cache-line padding of the slots and totals is intentional; it prevents false sharing.
#pragma warning(disable : 4324)
#endif

/**
 * Session-wide and per-step verified-byte totals, fed by one padded slot per worker.
 *
 * Owned by the session and shared by its workers and its coordinator for the session's
 * lifetime. Non-copyable and non-movable, because workers hold references to it while they run.
 *
 * @par Threading
 * - add() and publish() are worker-side: each worker calls them only with its own
 *   worker_index, and no two threads ever call them concurrently for the same index. Different
 *   indices may be used concurrently from different threads.
 * - begin_step() is coordinator-side and is called only between steps, when no worker is
 *   running a phase.
 * - session_total() and step_total() may be called from any thread at any time. Read
 *   mid-phase, each lags the true count by less than k_coverage_publish_bytes per worker; read
 *   after the phase barrier, which every worker reaches only after its final publish(), each is
 *   exact.
 * - All atomic operations are relaxed: the totals publish no other data, and the phase barrier
 *   supplies the ordering that makes the post-barrier reads exact.
 */
class coverage_counter {
public:
    /**
     * Allocates one zeroed slot per worker; the session and step totals start at 0.
     *
     * Called by the session before any worker starts.
     *
     * @param worker_count Number of workers; valid worker indices are 0 to worker_count - 1.
     * @throws std::bad_alloc when the slots cannot be allocated. Session construction runs
     *         inside the engine boundary's try/catch, which converts it to a result code.
     */
    explicit coverage_counter(std::uint32_t worker_count)
        : slots_(std::make_unique<worker_slot[]>(worker_count)) {}

    coverage_counter(const coverage_counter&) = delete;
    coverage_counter& operator=(const coverage_counter&) = delete;
    coverage_counter(coverage_counter&&) = delete;
    coverage_counter& operator=(coverage_counter&&) = delete;

    /** Frees the slots. No worker may be running. */
    ~coverage_counter() = default;

    /**
     * Records bytes verified by one worker, publishing them once a full block is pending.
     *
     * Worker-side: called only by the worker that owns worker_index, never concurrently for the
     * same index.
     *
     * @param worker_index The calling worker's index, below the constructor's worker_count.
     * @param bytes Exact byte count (words x 8) of one verify or verify_and_stream call.
     */
    void add(std::uint32_t worker_index, std::uint64_t bytes) noexcept {
        worker_slot& slot = slots_[worker_index];
        slot.pending += bytes;
        if (slot.pending >= k_coverage_publish_bytes) {
            publish_slot(slot);
        }
    }

    /**
     * Adds the worker's pending bytes to the session and step totals and clears them.
     *
     * Worker-side: called only by the worker that owns worker_index, never concurrently for the
     * same index. Every phase task calls it before arriving at the phase barrier, on an early
     * return for a stop as well as on normal completion; otherwise up to one block of verified
     * bytes would be missing from the step's reported total. Nothing is published when nothing
     * is pending.
     *
     * @param worker_index The calling worker's index, below the constructor's worker_count.
     */
    void publish(std::uint32_t worker_index) noexcept { publish_slot(slots_[worker_index]); }

    /**
     * Zeroes the step total for the step about to start. The session total is unaffected.
     *
     * Coordinator-side: called only between steps, when no worker is running a phase and every
     * slot has been published, so no bytes from the previous step can land in the new one.
     */
    void begin_step() noexcept { step_total_.store(0, std::memory_order_relaxed); }

    /**
     * Bytes verified since the session started, across every step.
     *
     * Callable from any thread. Source of rq_progress.verified_bytes and
     * rq_run_result.verified_bytes.
     *
     * @return The published total; exact after the phase barrier, otherwise lagging by less
     *         than k_coverage_publish_bytes per worker.
     */
    std::uint64_t session_total() const noexcept {
        return session_total_.load(std::memory_order_relaxed);
    }

    /**
     * Bytes verified since the last begin_step().
     *
     * Callable from any thread. Source of rq_step_result.verified_bytes.
     *
     * @return The published total; exact after the phase barrier, otherwise lagging by less
     *         than k_coverage_publish_bytes per worker.
     */
    std::uint64_t step_total() const noexcept {
        return step_total_.load(std::memory_order_relaxed);
    }

private:
    /** x64 cache-line size: the unit of false sharing. */
    static constexpr std::size_t k_cache_line_bytes = 64;

    /**
     * One worker's unpublished verified bytes. A full line each, so a worker's per-block
     * increments never invalidate a line another worker is writing.
     */
    struct alignas(k_cache_line_bytes) worker_slot {
        std::uint64_t pending = 0;
    };
    static_assert(alignof(worker_slot) == 64, "worker_slot must be aligned to a cache line");
    static_assert(sizeof(worker_slot) == 64, "worker_slot must occupy exactly one cache line");
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free,
                  "publishing must be a lock-free read-modify-write");

    void publish_slot(worker_slot& slot) noexcept {
        if (slot.pending == 0) {
            return;
        }
        session_total_.fetch_add(slot.pending, std::memory_order_relaxed);
        step_total_.fetch_add(slot.pending, std::memory_order_relaxed);
        slot.pending = 0;
    }

    // Read-only after construction. It occupies the object's first line on its own, so workers
    // reading it on every add() never share a line with the totals they publish to.
    std::unique_ptr<worker_slot[]> slots_;
    // Each total on its own line: the per-block fetch_add of one never invalidates the other.
    alignas(k_cache_line_bytes) std::atomic<std::uint64_t> session_total_{0};
    alignas(k_cache_line_bytes) std::atomic<std::uint64_t> step_total_{0};
};

#ifdef _MSC_VER
#pragma warning(pop)
#endif

}  // namespace rq::engine
