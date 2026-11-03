/**
 * @file counting_memory_access.h
 * @brief Substitute IMemoryAccess layer that counts verified bytes independently of the engine's
 *        coverage_counter and records every primitive call, with its context and address range,
 *        in issue order.
 *
 * Test-only: compiled into engine.tests.exe and ramquick_engine_test.dll, never into
 * ramquick_engine.dll. The AVX kernel units never include this header, so it may use the STL.
 */
#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <type_traits>
#include <vector>

#include "core/memory/memory_access.h"
#include "core/clock.h"

namespace rq::engine::test {

/** The IMemoryAccess primitive a primitive_record describes. */
enum class primitive_kind : std::uint32_t {
    /** IMemoryAccess::stream_fill: a store-type access to its range. */
    stream_fill,
    /** IMemoryAccess::fence: one MFENCE; the record's range is empty. */
    fence,
    /** IMemoryAccess::flush: CLFLUSHOPT or CLFLUSH of every line overlapping its range. */
    flush,
    /** IMemoryAccess::verify: a load-type access to its range; counts as verified bytes. */
    verify,
    /**
     * IMemoryAccess::verify_and_stream: a load followed by a store of every word of its range;
     * counts as verified bytes.
     */
    verify_and_stream,
    /**
     * IMemoryAccess::reread_word: one flushed load of a single word. The kernel performs its own
     * fence, flush and fence, so no separate fence or flush records precede it. Never counts.
     */
    reread_word,
    /**
     * IMemoryAccess::stream_fill_strided: a store-type access to each line its record's visits
     * name, not to the whole span.
     */
    stream_fill_strided,
    /**
     * IMemoryAccess::verify_strided: a load-type access to each line its record's visits name;
     * counts visit_count x 64 verified bytes.
     */
    verify_strided,
};

/**
 * One primitive call as the layer saw it on entry, plus the clock readings around it.
 *
 * Plain data, copied into the log, into snapshots and into std::sort; on x64 it occupies 120
 * bytes, which is what tests size counting_options::max_records_per_worker against.
 */
struct primitive_record {
    /**
     * Global issue order across every worker and every kind, from one atomic counter in the
     * layer. Taken on entry, before the call is forwarded, so a reread_word issued from inside a
     * verify's sink (the error collector re-reads through the outermost layer) always carries a
     * later seq than the verify that found the mismatch.
     */
    std::uint64_t seq = 0;
    /** Which primitive was called. */
    primitive_kind kind{};
    /** ctx.worker_index of the call: the log slot the record is stored in. */
    std::uint32_t worker = 0;
    /**
     * First byte of the call's range: the words pointer for stream_fill, flush, verify and
     * verify_and_stream, the span pointer for the strided kinds, the word for reread_word, 0 for
     * fence.
     */
    std::uintptr_t begin = 0;
    /**
     * Length of the range in bytes: word_count x 8, 8 for reread_word, 0 for fence, and for the
     * strided kinds visits.visit_count x 64, the bytes the call visits rather than the span's.
     */
    std::uint64_t bytes = 0;
    /**
     * The strided kinds: the call's visits, relative to begin, which name the lines the call
     * touches (strided_visits in core/kernels/kernels.h). All zero for every other kind.
     */
    strided_visits visits{};
    /** Copy of the call's access_context. */
    access_context ctx{};
    /** counting_options::clock read on entry, before forwarding; 0 without a clock. */
    std::uint64_t clock_ns = 0;
    /**
     * counting_options::clock read after the forwarded call and after the observer returned;
     * 0 without a clock, and 0 in the record the observer receives.
     *
     * Bracketing the call with clock_ns lets a test match a verification's ctx.write_time_ns
     * against the stream_fill of the same block whether the runner stamps the time before or
     * after issuing the stream, and an advance of a fake clock made by the observer (for example
     * pass_duration_script::on_pass_begin) is already visible here.
     */
    std::uint64_t clock_after_ns = 0;
};

static_assert(std::is_trivially_copyable_v<primitive_record>,
              "primitive_record is copied into logs, snapshots and sorts as plain data");

/** Construction options of counting_memory_access. */
struct counting_options {
    /** Enables the primitive log at construction, as enable_log(max_records_per_worker). */
    bool log_primitives = false;
    /** Per-worker record cap used when log_primitives is set. */
    std::size_t max_records_per_worker = std::size_t{1} << 20;
    /**
     * Number of per-worker log slots. A primitive whose ctx.worker_index is at or above it is
     * still forwarded and counted, but cannot be logged and sets log_overflowed().
     */
    std::uint32_t max_workers = 1024;
    /**
     * Clock read around every primitive for primitive_record::clock_ns and clock_after_ns; null
     * leaves both 0. Not owned: it must outlive every primitive call through the layer. Pass the
     * session's clock (a fake_clock in native tests) so the readings share the time base of
     * access_context::write_time_ns.
     */
    const IClock* clock = nullptr;
};

/**
 * Number of per-step verified-byte counters. At least k_max_step_count of core/session.h,
 * which counting_memory_access.cpp static_asserts, so the step index of any valid session has
 * its own counter.
 */
inline constexpr std::uint32_t k_counting_step_slots = 1024;

/**
 * The independent counter and primitive recorder of the native tests and the test DLL.
 *
 * Every primitive is forwarded unchanged to the inner layer: the same ctx, range, pattern
 * specs, direction and, for verification, the caller's sink, with the inner result returned
 * as is. The layer never alters data, order or timing beyond the cost of its own counting and
 * logging, so the flush-before-verification assertions of test_step_runner.cpp examine exactly
 * the sequence the step runner issued. It issues no load, store, flush or fence of its own to
 * the memory under test, and so adds no stress beyond the runner's (stress bound).
 *
 * @par Two independent counts
 * - verified_bytes() sums word_count x 8 over every verify and verify_and_stream call and
 *   visit_count x 64 over every verify_strided call, and verified_bytes_for_step() does so per
 *   ctx.step_index.
 * - primitive_count() counts every call of any kind.
 *
 * They are kept apart from the engine's coverage_counter on purpose: success criterion 5 holds
 * the coverage the engine reports to within 1 % of an independent count of bytes verified, and
 * a count derived from coverage_counter would only agree with itself. test_coverage.cpp,
 * rq_test_get_independent_verified_bytes and the managed CoverageAndTimingTests read
 * verified_bytes(); test_worker_pool.cpp asserts primitive_count() == 0 for "no traffic".
 *
 * @par Primitive log
 * Off by default. When enabled, each worker's calls are appended to that worker's own slot in
 * issue order, so log_for_worker() gives the exact per-worker sequence the flush-rule checker
 * walks, and merged_log() gives every worker's records in global seq order.
 *
 * @par Stacks
 * rq_test_session_create builds fault_injecting_memory_access -> counting_memory_access ->
 * real_memory_access inside session_dependencies::wrap_memory_access (core/session.h), with
 * default counting_options: no log and no clock, so DLL sessions carry no logging memory.
 * Native tests may also use counting -> real alone, with logging and a fake_clock.
 *
 * @par Threading
 * All workers call the primitives concurrently, and the error collector's re-read re-enters
 * reread_word from inside verify or verify_and_stream on the same thread, so no lock is held
 * across a forwarded call. The counting accessors may be read at any time; they are exact once
 * the run has quiesced. Configuration (enable_log, set_observer, reset_counts) happens only while
 * no primitive is in flight.
 *
 * @par Exceptions
 * Every override is noexcept, stricter than the base where only reread_word is: the layer's own
 * work can fail only on log allocation, which degrades to log_overflowed() instead of throwing.
 * The inner layer must therefore not throw from a forwarded primitive, which real_memory_access
 * never does, and neither may the observer: an escaping exception calls std::terminate.
 */
class counting_memory_access final : public IMemoryAccess {
public:
    /**
     * @param inner The layer every primitive is forwarded to, normally real_memory_access. Must
     *        not be null: the constructor terminates the process on null, because a layer with
     *        nothing to forward to would silently drop the step runner's traffic.
     * @param options Logging, worker-slot count and clock; see counting_options. With
     *        options.log_primitives set the log is enabled here, as
     *        enable_log(options.max_records_per_worker).
     * @throws std::bad_alloc when the layer's state or the log slots cannot be allocated.
     */
    explicit counting_memory_access(std::unique_ptr<IMemoryAccess> inner,
                                    counting_options options = {});

    /** Destroys the inner layer. Defined in the .cpp, which also emits the vftable there. */
    ~counting_memory_access() override;

    counting_memory_access(const counting_memory_access&) = delete;
    counting_memory_access& operator=(const counting_memory_access&) = delete;
    counting_memory_access(counting_memory_access&&) = delete;
    counting_memory_access& operator=(counting_memory_access&&) = delete;

    /** Counts, logs (begin = words, bytes = word_count x 8) and forwards stream_fill. */
    void stream_fill(const access_context& ctx, std::uint64_t* words, std::size_t word_count,
                     const pattern_spec& spec, std::uint64_t first_word_index,
                     int direction) noexcept override;

    /** Counts, logs (empty range) and forwards fence. */
    void fence(const access_context& ctx) noexcept override;

    /** Counts, logs (begin = words, bytes = word_count x 8) and forwards flush. */
    void flush(const access_context& ctx, const std::uint64_t* words,
               std::size_t word_count) noexcept override;

    /**
     * Counts, logs and forwards verify, then adds word_count x 8 to the verified totals, the
     * whole range whatever the mismatches, as the engine's coverage_counter counts every word
     * compared.
     *
     * @return The inner layer's mismatch count.
     */
    std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                         std::size_t word_count, const pattern_spec& expected,
                         std::uint64_t first_word_index, int direction,
                         mismatch_sink& sink) noexcept override;

    /**
     * Counts, logs and forwards verify_and_stream, then adds word_count x 8 to the verified
     * totals. Each of the two sweeps of a moving-inversion pass verifies, so such a pass counts
     * twice, exactly as coverage accounting defines.
     *
     * @return The inner layer's mismatch count.
     */
    std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                    std::size_t word_count, const pattern_spec& expected,
                                    const pattern_spec& next, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) noexcept override;

    /**
     * Counts, logs (begin = span, bytes = visit_count x 64, with the visits) and forwards
     * stream_fill_strided.
     */
    void stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                             std::size_t span_word_count, const strided_visits& visits,
                             const pattern_spec& spec,
                             std::uint64_t span_first_word_index) noexcept override;

    /**
     * Counts, logs (begin = span, bytes = visit_count x 64, with the visits) and forwards
     * verify_strided, then adds visit_count x 64 to the verified totals: every visited line is
     * compared whole, whatever the mismatches, and the rest of the span is not verified.
     *
     * @return The inner layer's mismatch count.
     */
    std::uint64_t verify_strided(const access_context& ctx, const std::uint64_t* span,
                                 std::size_t span_word_count, const strided_visits& visits,
                                 const pattern_spec& expected, std::uint64_t span_first_word_index,
                                 mismatch_sink& sink) noexcept override;

    /**
     * Counts, logs (begin = word, bytes = 8) and forwards reread_word. Re-reads never add to the
     * verified totals: coverage excludes them.
     *
     * @return The value the inner layer's flushed load returned.
     */
    std::uint64_t reread_word(const access_context& ctx,
                              const std::uint64_t* word) noexcept override;

    /**
     * Sum of word_count x 8 over every verify and verify_and_stream call, and of visit_count x 64
     * over every verify_strided call, since the last reset.
     */
    std::uint64_t verified_bytes() const noexcept;

    /**
     * As verified_bytes(), restricted to calls whose ctx.step_index equals step_index.
     *
     * @return 0 for a step_index at or above k_counting_step_slots; such calls still add to
     *         verified_bytes().
     */
    std::uint64_t verified_bytes_for_step(std::uint32_t step_index) const noexcept;

    /** Number of primitive calls of any kind since the last reset, logged or not. */
    std::uint64_t primitive_count() const noexcept;

    /**
     * Zeroes the verified totals, the per-step totals and the primitive count. Leaves the log,
     * its overflow flag and the seq counter alone. Call only while no primitive is in flight.
     */
    void reset_counts() noexcept;

    /**
     * Enables the primitive log with a cap of max_records_per_worker records per worker.
     *
     * Call before the run. Creates one cache-line-aligned slot, with its own mutex and an empty
     * record vector, for each of counting_options::max_workers workers; records are reserved
     * lazily on each worker's first append, so enabling never allocates max_workers x cap
     * records up front. Calling it again before a run replaces the caps and keeps the records.
     * A worker that reaches its cap stops logging and sets log_overflowed(); its counting
     * continues.
     *
     * @throws std::bad_alloc when the slots cannot be allocated.
     */
    void enable_log(std::size_t max_records_per_worker = std::size_t{1} << 20);

    /**
     * Snapshot of one worker's records in issue order. A reread_word issued from inside a verify
     * follows that verify's record.
     *
     * Read the log accessors only after the run has quiesced (after run_step returns, or after
     * Completed): a snapshot taken mid-run lacks later records and shows clock_after_ns == 0
     * for calls still in flight.
     *
     * @return An empty vector when the log was never enabled or worker is at or above
     *         counting_options::max_workers.
     * @throws std::bad_alloc when the copy cannot be allocated.
     */
    std::vector<primitive_record> log_for_worker(std::uint32_t worker) const;

    /**
     * Every worker's records, sorted by seq. Read it after the run has quiesced, as
     * log_for_worker().
     *
     * @throws std::bad_alloc when the copy cannot be allocated.
     */
    std::vector<primitive_record> merged_log() const;

    /**
     * True once a record could not be logged: a worker reached its cap, a ctx.worker_index was
     * at or above counting_options::max_workers, or a record allocation failed. A flush-rule
     * check over an overflowed log is incomplete, so tests assert this is false first.
     */
    bool log_overflowed() const noexcept;

    /**
     * Installs a callback invoked once per primitive, synchronously on the calling thread
     * (worker or coordinator), after the call has been forwarded and whether or not logging is
     * enabled. It receives the call's record with clock_after_ns == 0, which is not yet known.
     *
     * Set it before the run; it is read without synchronisation. Several workers call it
     * concurrently, so it must be thread-safe, and it must not throw. Because it runs after the
     * forward, the observer of a verify that triggered re-reads runs after the observers of
     * those re-reads; use seq where issue order matters.
     *
     * Uses: pass_duration_script::on_pass_begin (test_step_runner.cpp, test_coverage.cpp), phase
     * detection (test_stop.cpp), capture of written addresses (test_memory_region.cpp).
     *
     * @param observer The callback; an empty function removes it.
     */
    void set_observer(std::function<void(const primitive_record&)> observer);

    /**
     * The wrapped layer, never null. A pointer, so callers can dynamic_cast it directly, as
     * with fault_injecting_memory_access::inner().
     */
    IMemoryAccess* inner() noexcept;

    /** See the non-const overload. */
    const IMemoryAccess* inner() const noexcept;

private:
    /** Counters, log slots, flags, observer and options; defined in the .cpp. */
    struct impl;

    std::unique_ptr<IMemoryAccess> inner_;
    std::unique_ptr<impl> impl_;
};

}  // namespace rq::engine::test
