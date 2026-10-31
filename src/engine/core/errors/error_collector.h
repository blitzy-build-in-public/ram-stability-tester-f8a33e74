/**
 * @file error_collector.h
 * @brief Mismatch confirmation and accounting: the flushed re-read and its classification, the
 *        stored-detail cap, batching for on_errors, exact true totals and Stop on error.
 *
 * Every mismatching word a verification kernel finds reaches the calling worker's
 * worker_error_sink, which hands it to the session's error_collector. The collector re-reads
 * the word once through the session's outermost IMemoryAccess (fence, flush of its line, fence,
 * one load), classifies the re-read, counts the error, stores its detail while the cap allows
 * and, with Stop on error, requests the stop on the run's first error. The coordinator drains
 * newly stored records and the true total into on_errors batches.
 *
 * @par Threading
 * - The mismatch path (worker_error_sink::on_mismatch) runs concurrently on every worker and is
 *   lock-free: atomic counters, one slot owned by each detection ordinal and a ready flag per
 *   slot. No lock is held across the re-read, which re-enters the memory-access layers on the
 *   same thread from inside their own verification.
 * - The mismatch path never allocates, never raises a callback and never waits: the constructor
 *   preallocates everything, and callbacks come only from the coordinator, which alone calls
 *   begin_run(), begin_step() and drain().
 *
 * @par Stored detail and true totals
 * The n-th confirmed error of the run (0-based) gets rq_error_record.sequence = n and is stored
 * in slot n when n < detail_cap. Stored detail is therefore the first detail_cap errors in
 * detection order, while the total, the per-step counts and first_error_ms stay exact whatever
 * the cap. Once the cap is reached, drain() can return count == 0 with a larger total and
 * has_news set; the session still raises on_errors then, so the host's running count keeps
 * rising after batches stop carrying records. The session drains once more after the workers
 * have joined and before raising on_completed, so every stored record and the final total are
 * delivered.
 *
 * @par Stop on error
 * The first worker to confirm an error requests the stop itself with RQ_END_FIRST_ERROR, without
 * waiting for any callback. stop_signal is first-writer-wins, so a user, thermal or host-fault
 * stop recorded earlier keeps its reason. Errors confirmed after the request, before the workers
 * observe the stop flag at their next 1 MiB block, are still counted and, below the cap, stored.
 *
 * @par Build constraint
 * Includes STL headers and memory_access.h, so include it only from baseline (SSE2) units, never
 * from core/kernels/* or core/patterns/patterns.h. core/clock.h is deliberately not included: it
 * brings <windows.h>, and this header needs stop_signal and IClock only by reference.
 */
#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <vector>

#include "ramquick_engine.h"
#include "core/kernels/kernels.h"
#include "core/memory/memory_access.h"
#include "core/system_info.h"

namespace rq::engine {

// Defined in core/clock.h, which this header must not include (see the file comment). The class
// key matches those definitions, so MSVC raises no C4099.
class stop_signal;
class IClock;

/**
 * Minimum spacing between two on_errors callbacks, 100 ms. Errors reach the host in batches at
 * most 10 times a second, so a badly unstable profile cannot flood the UI thread with one post
 * per error. core/session.cpp drains no more often than this while the run is active.
 */
inline constexpr std::uint64_t k_error_batch_interval_ns = 100'000'000;

/**
 * Classifies the flushed re-read of a mismatching word against its expected and first values.
 *
 * Pure; callable from any thread. Every outcome is a confirmed error, since each one shows
 * instability. The host attaches an inference to each outcome and always presents it as an
 * inference, never as an established cell state; the engine records only the classification:
 * - RQ_REREAD_MATCHES_EXPECTED: "likely read-path fault".
 * - RQ_REREAD_REPEATS_ACTUAL: "likely write or storage fault". A repeatable read-path fault
 *   produces the same values, so this outcome cannot exclude one.
 * - RQ_REREAD_DIFFERS: "likely transient fault".
 *
 * @param expected Pattern value the word should hold.
 * @param actual Value the verification load returned; differs from expected on the mismatch
 *        path, so the first two outcomes never overlap.
 * @param reread Value the flushed re-read returned.
 * @return RQ_REREAD_MATCHES_EXPECTED when reread == expected; otherwise RQ_REREAD_REPEATS_ACTUAL
 *         when reread == actual; otherwise RQ_REREAD_DIFFERS.
 */
std::uint32_t classify_reread(std::uint64_t expected, std::uint64_t actual,
                              std::uint64_t reread) noexcept;

/** Construction parameters of an error_collector, taken from the session configuration. */
struct error_collector_options {
    /**
     * Records stored in full per run: rq_session_config.error_detail_cap, 10,000 from the app.
     * 0 stores none while every count stays exact. Session validation enforces the upper limit
     * (core/session.h) before the collector preallocates that many slots.
     */
    std::uint32_t detail_cap = 0;
    /** Request the stop with RQ_END_FIRST_ERROR on the run's first confirmed error. */
    bool stop_on_error = false;
    /** Steps in the plan, at least 1; sizes the per-step true counts. */
    std::uint32_t step_count = 0;
    /**
     * Processor of each worker, indexed by worker: worker i is pinned to placements[i]. Its size
     * is the worker count, at least 1. Copied into each record's logical_processor and
     * processor_group.
     */
    std::vector<logical_processor> placements;
};

/** What one error_collector::drain() delivers to on_errors. */
struct error_batch {
    /**
     * The newly ready records, contiguous and in sequence order; null when count is 0. Points
     * into the collector's own storage, where each slot is written once, so it stays valid for
     * the collector's lifetime.
     */
    const rq_error_record* records = nullptr;
    /** Number of records; 0 when none is newly ready, as always once the cap is reached. */
    std::uint32_t count = 0;
    /** Exact true total of confirmed errors at the drain, never capped. */
    std::uint64_t total_errors = 0;
    /**
     * True when count > 0 or total_errors changed since the previous drain; the session raises
     * on_errors only then.
     */
    bool has_news = false;
};

class error_collector;

#ifdef _MSC_VER
#pragma warning(push)
// C4324: the cache-line alignment of the sinks and the total is intentional; it prevents false
// sharing between workers.
#pragma warning(disable : 4324)
#endif

/**
 * One worker's mismatch_sink: holds the context of the verification its worker issues next and
 * hands each mismatch that verification reports, with that context, to the owning collector.
 *
 * Owned by its error_collector, one per worker, and initialised by it; never constructed
 * elsewhere. Aligned to a cache line because its worker rewrites the bound context before every
 * verification call, and adjacent sinks would otherwise share lines across workers.
 * Non-copyable and non-movable: the kernels hold it as mismatch_sink& while they run.
 *
 * Threading: used only by its own worker thread, never concurrently.
 */
class alignas(k_cache_line_bytes) worker_error_sink final : public mismatch_sink {
public:
    /**
     * An unbound sink, initialised afterwards by its collector. Public only so the collector can
     * preallocate one array of sinks.
     */
    worker_error_sink() noexcept = default;

    worker_error_sink(const worker_error_sink&) = delete;
    worker_error_sink& operator=(const worker_error_sink&) = delete;
    worker_error_sink(worker_error_sink&&) = delete;
    worker_error_sink& operator=(worker_error_sink&&) = delete;

    /**
     * Stores, by value, the context of the verify or verify_and_stream call the worker issues
     * next with this sink; every mismatch that call reports is recorded with it.
     *
     * Owning worker thread only, before each verification call. The copy is taken because the
     * runner's context is a temporary that may not outlive the call.
     */
    void bind(const access_context& ctx) noexcept;

    /**
     * Confirms and records one mismatch: re-reads the word through the session's outermost
     * memory-access layer, classifies it, counts it, stores its detail below the cap and, with
     * Stop on error, requests the stop on the run's first error.
     *
     * Called by the kernel on the owning worker, before verify_and_stream overwrites the word,
     * so the re-read sees the stored wrong value. Lock-free, allocation-free and non-blocking;
     * raises no callback. Defined in error_collector.cpp, which makes it the key function, so
     * the vftable is emitted once, in a baseline unit.
     */
    void on_mismatch(const std::uint64_t* address, std::uint64_t expected,
                     std::uint64_t actual) noexcept override;

    /** Index of the worker this sink belongs to; written into each of its records. */
    std::uint32_t worker_index() const noexcept { return worker_index_; }

private:
    friend class error_collector;

    // Set once by the owning collector's constructor, before any worker runs.
    error_collector* owner_ = nullptr;
    std::uint32_t worker_index_ = 0;
    logical_processor placement_{};
    // Rewritten by bind() before every verification call and read on the mismatch path.
    access_context context_{};
};

static_assert(alignof(worker_error_sink) == k_cache_line_bytes,
              "each worker's sink must start its own cache line");

/**
 * Confirms, counts and stores the errors of one run, and batches them for on_errors.
 *
 * Constructed by the session's coordinator at run start, once the worker placements are known,
 * and destroyed after the run's final drain; never at rq_session_create. It owns one
 * worker_error_sink per worker. Non-copyable and non-movable: the sinks point back at it.
 *
 * @par Threading
 * - Worker side: each worker uses only its own sink(), through bind() and the kernels'
 *   on_mismatch calls. The mismatch path is lock-free and may run on every worker at once.
 * - Coordinator side: begin_run(), begin_step() and drain() run on the coordinator only, and
 *   begin_run() and begin_step() only while no worker is running a phase.
 * - true_total(), step_errors(), first_error_ms(), stored_count() and truncated() may be called
 *   from any thread at any time. Read while workers run they are momentary values; read after
 *   the phase barrier or once the workers have joined they are exact.
 */
class error_collector final {
public:
    /**
     * Preallocates every record slot, ready flag, per-step counter and sink, so the mismatch
     * path never allocates.
     *
     * @param options Cap, Stop on error, step count and worker placements; read during
     *        construction and not retained.
     * @param stop The session's stop signal; must outlive the collector.
     * @param clock The session clock, the one the step runner stamps write times with; must
     *        outlive the collector.
     * @param memory The session's outermost memory-access layer, so every layer (counting and
     *        fault-injecting in the test DLL) sees every re-read; must outlive the collector.
     * @throws std::invalid_argument when options.placements is empty or options.step_count is 0.
     * @throws std::bad_alloc when the storage cannot be allocated. The coordinator converts
     *         either into a stop with RQ_END_INTERNAL_FAULT.
     */
    error_collector(const error_collector_options& options, stop_signal& stop, const IClock& clock,
                    IMemoryAccess& memory);

    /** Frees the storage. No worker may still use a sink, and no batch pointer may be in use. */
    ~error_collector();

    error_collector(const error_collector&) = delete;
    error_collector& operator=(const error_collector&) = delete;
    error_collector(error_collector&&) = delete;
    error_collector& operator=(error_collector&&) = delete;

    /**
     * Sets test time zero, the origin of time_into_test_ms and first_error_ms.
     *
     * Coordinator only, before any worker traffic. t0_ns must be the IClock reading the session
     * measures rq_progress.elapsed_ms and rq_run_result.elapsed_ms from (the instant preparation
     * completes, before on_prepared), so each error's time into the test agrees with the elapsed
     * time the host shows.
     */
    void begin_run(std::uint64_t t0_ns) noexcept;

    /**
     * Prepares for the step about to start.
     *
     * Coordinator only, between steps, while no worker is running a phase.
     *
     * @param step_index Index of the step about to start, below step_count(). Records take their
     *        step from the bound access_context, so this only marks the step boundary.
     * @param active_workers Workers that run the step's phases (worker_pool::active_count): all
     *        of them, or half rounded up in Half mode. Becomes rq_error_record.active_workers of
     *        the step's records.
     */
    void begin_step(std::uint32_t step_index, std::uint32_t active_workers) noexcept;

    /**
     * The sink of one worker, to bind and pass to the kernels as mismatch_sink&.
     *
     * Callable from any thread; the returned sink is then used only by that worker.
     *
     * @param worker_index Below worker_count(); asserted in debug builds.
     */
    worker_error_sink& sink(std::uint32_t worker_index) noexcept;

    /** Number of workers, one sink each: the size of the options' placements. */
    std::uint32_t worker_count() const noexcept;

    /** Steps in the plan: the options' step_count. */
    std::uint32_t step_count() const noexcept;

    /** Most records stored in full: the options' detail_cap. */
    std::uint32_t detail_cap() const noexcept;

    /**
     * Collects the records that became ready since the previous drain, with the true total.
     *
     * Coordinator only. Returns the contiguous run of ready slots after the last delivered one,
     * stopping at the first slot a worker has reserved but not yet filled; that slot and the
     * ones after it come with a later drain. The session calls it at most once per
     * k_error_batch_interval_ns while the run is active, and once more after the workers have
     * joined, which then delivers every remaining stored record and the final total.
     */
    error_batch drain() noexcept;

    /**
     * Exact number of confirmed errors in the run, never capped: rq_progress.total_errors and
     * rq_run_result.total_errors. Any thread.
     */
    std::uint64_t true_total() const noexcept;

    /**
     * Confirmed errors of one step, never capped: rq_step_result.errors. Any thread.
     *
     * @return The count, or 0 for a step_index not below step_count().
     */
    std::uint64_t step_errors(std::uint32_t step_index) const noexcept;

    /**
     * Earliest test time, in milliseconds, of any confirmed error: rq_run_result.first_error_ms.
     * Workers race, so this is the minimum over every error rather than the time of sequence 0.
     * Any thread.
     *
     * @return The time, or RQ_NO_FIRST_ERROR when the run has confirmed none.
     */
    std::uint64_t first_error_ms() const noexcept;

    /**
     * Records stored: min(true_total(), detail_cap()). It counts reserved slots, so it equals
     * the ready records once the workers have joined. Any thread.
     */
    std::uint32_t stored_count() const noexcept;

    /** True when true_total() exceeds detail_cap(), so detail was dropped. Any thread. */
    bool truncated() const noexcept;

private:
    friend class worker_error_sink;

    /**
     * The mismatch path behind worker_error_sink::on_mismatch, on the sink's worker: re-read,
     * classify, count, store below the cap, and request the stop on the first error with Stop
     * on error. Lock-free, allocation-free and non-blocking.
     */
    void record(worker_error_sink& sink, const std::uint64_t* address, std::uint64_t expected,
                std::uint64_t actual) noexcept;

    // Read on every mismatch and never written while workers run: set by the constructor,
    // except t0_ns_ and active_workers_, which the coordinator sets only between phases.
    stop_signal& stop_;
    const IClock& clock_;
    IMemoryAccess& memory_;
    std::uint32_t detail_cap_ = 0;
    std::uint32_t step_count_ = 0;
    std::uint32_t worker_count_ = 0;
    bool stop_on_error_ = false;
    // detail_cap_ records and their ready flags (0 unfilled, 1 ready); both null when the cap is
    // 0. Slot n is filled only by the worker that confirmed error n, then published by its flag.
    std::unique_ptr<rq_error_record[]> records_;
    std::unique_ptr<std::atomic<std::uint32_t>[]> ready_;
    // step_count_ true per-step counts.
    std::unique_ptr<std::atomic<std::uint64_t>[]> step_errors_;
    // worker_count_ sinks, each on its own cache line.
    std::unique_ptr<worker_error_sink[]> sinks_;
    std::atomic<std::uint64_t> t0_ns_{0};
    std::atomic<std::uint32_t> active_workers_{0};

    // The true total, whose fetch_add hands out detection ordinals. Every erroring worker
    // writes it, so it sits alone on its line, away from the read-mostly fields above.
    alignas(k_cache_line_bytes) std::atomic<std::uint64_t> total_{0};
    // Also written on the mismatch path (minimum test time, first Stop on error), and starting
    // a new line so nothing shares total_'s.
    alignas(k_cache_line_bytes) std::atomic<std::uint64_t> first_error_ms_{RQ_NO_FIRST_ERROR};
    std::atomic<bool> stop_issued_{false};
    // Coordinator only: the next slot drain() delivers and the total the previous drain saw.
    std::uint32_t delivered_ = 0;
    std::uint64_t last_drained_total_ = 0;
};

#ifdef _MSC_VER
#pragma warning(pop)
#endif

static_assert(std::atomic<std::uint64_t>::is_always_lock_free &&
                  std::atomic<std::uint32_t>::is_always_lock_free &&
                  std::atomic<bool>::is_always_lock_free,
              "the mismatch path must stay lock-free");

}  // namespace rq::engine
