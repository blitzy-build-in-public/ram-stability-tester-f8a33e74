/**
 * @file memory_access.h
 * @brief IMemoryAccess, the seam every memory primitive of the engine passes through, and the
 *        access_context each primitive carries.
 *
 * The step runner (core/runner/step_runner.cpp) and the error collector
 * (core/errors/error_collector.cpp) never call a kernel_table entry directly. They issue every
 * streaming fill, fence, flush, verification, moving-inversion sweep and re-read through this
 * interface, so test layers can observe and alter memory traffic without any engine change:
 * - real_memory_access (core/memory/real_memory_access.h) forwards each primitive to the
 *   session's kernel_table;
 * - counting_memory_access (tests/engine.tests/fakes) counts verified bytes independently of
 *   coverage_counter and records every primitive with its context and range, in order;
 * - fault_injecting_memory_access (tests/engine.tests/fakes) corrupts words just before their
 *   verification and logs the ground truth of each corruption.
 *
 * @par Build constraint
 * Include only from baseline (SSE2) translation units. Never include this header from
 * core/kernels/kernels_avx2.cpp or core/kernels/kernels_avx512.cpp, nor from kernels.h or
 * patterns.h, which those units include. IMemoryAccess has an inline virtual destructor and no
 * key function, so every unit that constructs or destroys a layer emits that destructor and the
 * vftable as COMDATs. Compiled at /arch:AVX2 or /arch:AVX512, the linker could keep those copies
 * for baseline callers too, and code built for a higher /arch level would run before ISA
 * dispatch had selected it; on a CPU without the extension that is an illegal-instruction fault.
 * <type_traits> is acceptable here only because the header stays baseline-only.
 */
#pragma once

#include <cstdint>
#include <cstddef>
#include <type_traits>

#include "ramquick_engine.h"
#include "core/kernels/kernels.h"

namespace rq::engine {

/**
 * The part of a pass that issued a primitive.
 *
 * counting_memory_access tags each entry of its ordered primitive log with it. The
 * flush-before-verification assertions of tests/engine.tests/test_step_runner.cpp group that
 * log by phase, and they require a pre_hold_flush phase between the write and the verification
 * of every pass of a hold step.
 */
enum class phase_kind : std::uint32_t {
    /**
     * Streaming fill of a window or slice, plus its trailing fence. In a moving-inversion step
     * the base write's trailing sequence is the full fence, flush and fence over the slice, as
     * after each sweep, because the first sweep verifies at once. Verifies nothing.
     */
    write,
    /**
     * Hold steps only: the fence, flush and fence over the slice after its write and before
     * the hold. Without it a dirty cached copy could hold the new data back from memory for the
     * whole hold, and the verification would then measure the cache rather than DRAM retention.
     */
    pre_hold_flush,
    /**
     * The fence, flush and fence over a window or slice, then its verification:
     * RQ_ACCESS_VERIFY_AFTER_WRITE in windowed steps, RQ_ACCESS_VERIFY_AFTER_HOLD after a hold.
     */
    verify,
    /**
     * One moving-inversion verify-and-stream sweep over the slice
     * (RQ_ACCESS_READ_MODIFY_WRITE), with its trailing fence, flush and fence, which keep the
     * next sweep's loads off copies this sweep's own loads left in the caches.
     */
    sweep,
    /**
     * The per-window turnaround schedule of core/patterns/access_order.h
     * (RQ_ACCESS_TURNAROUND_READ): stream block i, flush block i - 4, fence, verify block i - 4,
     * plus its start-up and drain. Every primitive of the schedule carries this phase.
     */
    turnaround,
    /**
     * The single flushed re-read of a mismatching word. The error collector issues it from
     * mismatch_sink::on_mismatch with a copy of the verification's context in which only phase
     * changes, so the record keeps the step, pass, access type and write time of the
     * verification that found the mismatch.
     */
    reread,
};

/**
 * Where a primitive comes from: the step, pass, phase and worker that issued it, and how its
 * addresses map to logical region offsets and write times.
 *
 * A trivially copyable aggregate without member initialisers. The runner builds one per range
 * it issues and passes it by const reference; the error collector copies a verification's
 * context and sets phase to phase_kind::reread for the re-read call, and substitute layers copy
 * it into their logs. No layer retains the reference beyond the call.
 */
struct access_context {
    /** Index of the running step in the plan, from 0; becomes rq_error_record.step_index. */
    std::uint32_t step_index;
    /**
     * Pass within the step, from 0, the same value as the pass's pattern_spec::pass; becomes
     * rq_error_record.pass_index.
     */
    std::uint32_t pass_index;
    /** The part of the pass that issued the primitive. */
    phase_kind phase;
    /**
     * RQ_ACCESS_* value of the verifications the issuing phase performs: VERIFY_AFTER_WRITE or
     * VERIFY_AFTER_HOLD for verify, READ_MODIFY_WRITE for sweep, TURNAROUND_READ for turnaround
     * and, for reread, the value of the verification that found the mismatch. 0 in write and
     * pre_hold_flush, which verify nothing. Consumers read it only on verify, verify_strided,
     * verify_and_stream and reread_word, where it is never 0; it becomes
     * rq_error_record.access_type.
     */
    std::uint32_t access_type;
    /**
     * Worker that issued the call; becomes rq_error_record.worker_index. In All mode it also
     * owns the slice the range lies in. In Half mode an active even worker also issues ranges in
     * its odd partner's slice, so the slice owner is then not necessarily this worker.
     */
    std::uint32_t worker_index;
    /**
     * The address logical region offset 0 would have if the range's chunk continued down to it:
     * logical_base() of the chunk (core/memory/memory_region.h). The runner sets it per chunk
     * so that reinterpret_cast<std::uintptr_t>(address) - region_base, computed modulo 2^64, is
     * the logical region offset of any address in the range. Logical offsets concatenate the
     * slices in worker order and each slice's chunks in order. The offset becomes
     * rq_error_record.region_offset, and offset / 8 is the logical word index the patterns use.
     *
     * An integer rather than a pointer because it wraps below zero by design whenever a chunk
     * sits at a lower address than its logical offset. Never convert it to a pointer and never
     * dereference it.
     */
    std::uintptr_t region_base;
    /**
     * Session-clock reading (IClock::now_ns(), core/clock.h) taken right after the runner
     * finishes streaming the unit that holds the verified range. In windowed and whole-slice
     * phases that unit is the 1 MiB work group (k_work_group_bytes, core/runner/step_runner.h),
     * each successive 1 MiB of ranges in issue order; in turnaround it is the 4 KiB block.
     * Verification walks the same grouping, so each verified group carries the stamp of the
     * stream that last wrote it. Windowed and hold phases verify in stream order, so the k-th
     * verified group carries the k-th streamed group's stamp; moving inversion stamps each 1 MiB
     * block as its base write or sweep streams it, and the next sweep verifies the block under
     * that stamp in either direction. No single stamp stands for a whole 64 MiB window, and
     * consumers must not substitute one.
     *
     * time_since_write_us = (now - write_time_ns) / 1000 is therefore exact to the group or
     * block, not to the word: a word stored earlier in its group is older than the figure by up
     * to the time the rest of the group took to stream. A prefetch fill after the flush can also
     * precede the verification load, so the figure can overstate the data's age when it was
     * fetched (see rq_error_record.time_since_write_us). The fault-injecting layer applies a
     * delay-dependent fault only where the data's age since write_time_ns reaches the fault's
     * threshold, 2000 ms by default. In the test DLL that age is measured on a controlled hold
     * clock, which only the holds begun after the write advance, so only hold verifications
     * reach it whatever the scheduling. Consumers read it only on verify, verify_strided,
     * verify_and_stream and reread_word.
     */
    std::uint64_t write_time_ns;
};

static_assert(std::is_trivially_copyable_v<access_context>,
              "access_context is copied by value into re-read calls and substitute-layer logs");
static_assert(std::is_aggregate_v<access_context>,
              "access_context must stay an aggregate initialised field by field");

/**
 * The memory-access seam: one virtual primitive per kernel_table entry.
 *
 * Each primitive takes the issuing access_context first, followed by the matching kernel's
 * parameters in the kernel's order and with the meanings core/kernels/kernels.h gives them
 * (see "Common semantics of the range entries" there, for first_word_index and direction in
 * particular). Ranges are a pointer to 8-byte-aligned 64-bit words plus a word count. The
 * strided primitives take a 64-byte-aligned span, its word count and the strided_visits of the
 * call instead (see "Strided entries" there). The real layer therefore forwards every call
 * without reordering or converting anything except flush's word count, which
 * kernel_table::flush_range takes in bytes, and it drops the strided primitives' span word
 * count, which only substitute layers use to bound their bookkeeping.
 *
 * @par Layers
 * The test DLL composes fault-injecting -> counting -> real, each layer owning the next
 * through std::unique_ptr<IMemoryAccess> (session_dependencies::wrap_memory_access in
 * core/session.h); the production DLL uses real_memory_access alone. Every layer forwards each
 * call to the layer it wraps with the same ctx and, for verification, the caller's sink. The
 * error collector re-reads through the outermost layer, so every layer sees every re-read.
 *
 * @par Threading
 * Every primitive may be called concurrently from all workers on disjoint ranges, and
 * implementations must be safe for that: real_memory_access is stateless, and substitute layers
 * synchronise or partition their logs. reread_word also re-enters the outermost layer on the
 * same thread from inside that layer's own verify or verify_and_stream, through the sink, so no
 * layer may hold a lock across a forwarded verification that its reread_word also takes.
 *
 * @par Flush-before-verification rule
 * The interface does not enforce the rule; the step runner composes the primitives so that,
 * between a line's most recent store or load and its next verification load, it issues fence,
 * a flush covering the line, and fence, in that order. Hold steps add the same sequence before
 * the hold (phase_kind::pre_hold_flush). counting_memory_access records every call with its
 * context and address range, and a strided call with its span and visits, in order, so
 * test_step_runner.cpp asserts that sequence for every verified line, on later passes as on the
 * first. A strided verification needs it for each line it visits, and the runner's one fence,
 * flush of the window and fence before the window's verifications cover them all. verify,
 * verify_strided, verify_and_stream and reread_word never assume anything about a line's
 * earlier cache residency, and no implementation loads with MOVNTDQA, which has no
 * non-temporal effect on write-back memory.
 *
 * @par Coverage
 * A verify or verify_and_stream call verifies word_count x 8 bytes, and a verify_strided call
 * visit_count x 64 bytes, the lines it visits. Moving inversion verifies in both sweeps of a
 * pass, so it counts twice per pass. reread_word, stream_fill, stream_fill_strided, flush and
 * fence never count. counting_memory_access sums exactly these bytes, which gives the
 * independent count rq_test_get_independent_verified_bytes returns and test_coverage.cpp holds
 * coverage_counter's total against.
 *
 * @par Fault injection
 * A substitute layer may modify words in [words, words + word_count) immediately before it
 * forwards verify or verify_and_stream, and words of the lines a verify_strided call visits
 * immediately before it forwards that call; never a word of the span the call does not visit,
 * which a later call verifies. That point follows the last write to those words before their
 * verification, so a corruption made there always survives to be checked. The layer logs its
 * ground truth from ctx.step_index, ctx.pass_index, the logical offset
 * (address - ctx.region_base) and expected_value(expected, word index) from
 * core/patterns/patterns.h.
 *
 * @par Exceptions
 * Only reread_word is noexcept (see there). The other primitives may throw, so substitute
 * layers can allocate log storage; the worker thread entry points convert any C++ exception
 * into a stop with RQ_END_INTERNAL_FAULT. real_memory_access never throws.
 */
class IMemoryAccess {
public:
    /** Layers are owned and destroyed through std::unique_ptr<IMemoryAccess>. */
    virtual ~IMemoryAccess() = default;

    /**
     * Streams `spec` over the range with non-temporal stores, as kernel_table::fill_stream.
     * No fence: streaming stores are weakly ordered, so the runner fences before any flush or
     * verification of the range.
     *
     * @param ctx Issuing context.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing.
     * @param spec Pattern instance whose values the range receives.
     * @param first_word_index As for fill_stream_fn.
     * @param direction As for fill_stream_fn.
     */
    virtual void stream_fill(const access_context& ctx, std::uint64_t* words,
                             std::size_t word_count, const pattern_spec& spec,
                             std::uint64_t first_word_index, int direction) = 0;

    /**
     * One MFENCE, as kernel_table::full_fence: orders every earlier load, store, streaming
     * store and CLFLUSHOPT before every later load or store. The real layer ignores ctx;
     * substitute layers log it, which is how the flush-rule assertions place each fence.
     *
     * @param ctx Issuing context.
     */
    virtual void fence(const access_context& ctx) = 0;

    /**
     * CLFLUSHOPT, or CLFLUSH where the CPU lacks it, of every 64-byte line overlapping the
     * range, as kernel_table::flush_range over word_count x 8 bytes. Either writes the line
     * back and invalidates it at every cache level, whatever its prior state. No fence: the
     * runner's MFENCE on each side orders it.
     *
     * @param ctx Issuing context.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing.
     */
    virtual void flush(const access_context& ctx, const std::uint64_t* words,
                       std::size_t word_count) = 0;

    /**
     * Loads every word of the range and compares it with its `expected` value, as
     * kernel_table::verify, reporting each mismatching word to `sink` in traversal order.
     * Relies on the runner's fence, flush and fence since the range's last store or load.
     *
     * @param ctx Issuing context; phase verify or turnaround.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing and returns 0.
     * @param expected Pattern instance the range should hold.
     * @param first_word_index As for verify_fn.
     * @param direction As for verify_fn.
     * @param sink Receives each mismatch: the error collector's per-worker sink, which re-reads
     *        the word through the outermost layer's reread_word.
     * @return Number of mismatching words.
     */
    virtual std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                                 std::size_t word_count, const pattern_spec& expected,
                                 std::uint64_t first_word_index, int direction,
                                 mismatch_sink& sink) = 0;

    /**
     * One moving-inversion sweep, as kernel_table::verify_and_stream: for each word in
     * traversal order, load it, compare it with its `expected` value, report a mismatch to
     * `sink`, then stream `next`'s value for the same logical index non-temporally. The sink
     * runs before the overwrite, so its re-read sees the stored wrong value rather than the
     * next pattern's. No fence.
     *
     * @param ctx Issuing context; phase sweep.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing and returns 0.
     * @param expected Pattern instance the range should hold.
     * @param next Pattern instance the range receives.
     * @param first_word_index As for verify_and_stream_fn.
     * @param direction As for verify_and_stream_fn.
     * @param sink Receives each mismatch, as for verify.
     * @return Number of mismatching words.
     */
    virtual std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                            std::size_t word_count, const pattern_spec& expected,
                                            const pattern_spec& next,
                                            std::uint64_t first_word_index, int direction,
                                            mismatch_sink& sink) = 0;

    /**
     * Streams `spec` over the lines `visits` names, as kernel_table::fill_stream_strided: the
     * BankStride write of one work group in one call, so the seam and the kernel table are
     * crossed once per 16,384 lines rather than once per line. No fence, as for stream_fill.
     *
     * @param ctx Issuing context; phase write.
     * @param span First word of the span the visits are relative to, 64-byte aligned: the
     *        window.
     * @param span_word_count Words in the span; every visited line lies inside it. The real
     *        layer ignores it; substitute layers bound their bookkeeping with it.
     * @param visits The call's visits (core/kernels/kernels.h); visit_count 0 does nothing.
     * @param spec Pattern instance whose values the visited lines receive.
     * @param span_first_word_index Logical word index of span[0]: span[j] receives the value of
     *        index span_first_word_index + j.
     */
    virtual void stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                                     std::size_t span_word_count, const strided_visits& visits,
                                     const pattern_spec& spec,
                                     std::uint64_t span_first_word_index) = 0;

    /**
     * Loads every word of the lines `visits` names and compares it with its `expected` value,
     * as kernel_table::verify_strided, reporting each mismatching word to `sink` in visit order
     * and ascending within a line. Verifies visit_count x 64 bytes. Relies on the runner's
     * fence, flush and fence covering every visited line since its last store or load.
     *
     * @param ctx Issuing context; phase verify.
     * @param span First word of the span, 64-byte aligned, as for stream_fill_strided.
     * @param span_word_count Words in the span, as for stream_fill_strided.
     * @param visits The call's visits; visit_count 0 does nothing and returns 0.
     * @param expected Pattern instance the visited lines should hold.
     * @param span_first_word_index Logical word index of span[0], as for stream_fill_strided.
     * @param sink Receives each mismatch, as for verify.
     * @return Number of mismatching words.
     */
    virtual std::uint64_t verify_strided(const access_context& ctx, const std::uint64_t* span,
                                         std::size_t span_word_count, const strided_visits& visits,
                                         const pattern_spec& expected,
                                         std::uint64_t span_first_word_index,
                                         mismatch_sink& sink) = 0;

    /**
     * Re-reads one word under the re-read rule, as kernel_table::reread: fence, a flush of the
     * word's line, fence, then one load, whose value it returns. Never counts as coverage.
     *
     * noexcept because the error collector calls it from mismatch_sink::on_mismatch, which is
     * noexcept and runs inside a noexcept kernel: an exception escaping here calls
     * std::terminate. Substitute layers must not throw from it, so they preallocate the log
     * storage it writes or catch internally.
     *
     * @param ctx The verification's context with phase set to phase_kind::reread.
     * @param word The mismatching word.
     * @return The value the flushed load returned.
     */
    virtual std::uint64_t reread_word(const access_context& ctx,
                                      const std::uint64_t* word) noexcept = 0;

protected:
    /** Constructed only as a concrete layer. */
    IMemoryAccess() = default;
    /**
     * Protected copy operations prevent slicing assignment through an IMemoryAccess reference
     * while leaving each concrete layer free to decide whether it is copyable.
     */
    IMemoryAccess(const IMemoryAccess&) = default;
    /** See the copy constructor. */
    IMemoryAccess& operator=(const IMemoryAccess&) = default;
};

}  // namespace rq::engine
