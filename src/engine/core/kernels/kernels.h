/**
 * @file kernels.h
 * @brief The kernel table every instruction-set level implements, the sink the verification
 *        kernels report mismatches to, and the declarations of the three kernel sets and of the
 *        baseline fence, flush and re-read helpers.
 *
 * Three kernel sets implement one kernel_table of function pointers: kernels_sse2.cpp at the x64
 * SSE2 baseline, kernels_avx2.cpp compiled with /arch:AVX2 and kernels_avx512.cpp compiled with
 * /arch:AVX512. isa_dispatch (core/kernels/isa_dispatch.h) selects one set per session from
 * CPUID and XCR0, and the real memory-access layer (core/memory/real_memory_access.h) forwards
 * every IMemoryAccess primitive to the selected table's entries.
 *
 * @par Flush-before-verification rule
 * Between the most recent store or load to a line and that line's next verification load, the
 * step runner (core/runner/step_runner.cpp) executes full_fence, flush_range over the line and
 * full_fence, in that order. The first fence makes earlier streaming stores globally visible
 * before the flush; the flush writes the line back and invalidates it at every cache level,
 * whatever its prior state; the second fence orders the flush before the verification load. No
 * entry of this table relies on a line's earlier cache residency, so the rule holds alike on the
 * first pass and on later ones, whether a copy was left by a verification load, an earlier pass
 * or a hardware prefetch. Only reread applies the whole sequence itself, to a single line.
 *
 * @par Stress bound
 * Lines are flushed only as that rule requires: once per verified line per phase, once more
 * before a hold, and once per re-read. No caller runs flush-and-reload loops over a small
 * address set, the row-hammer access style the product excludes.
 *
 * @par Build constraint
 * kernels_avx2.cpp and kernels_avx512.cpp include this header. An inline function with external
 * linkage compiled in one of those units becomes a COMDAT holding AVX instructions, which the
 * linker may keep for baseline callers too; on a CPU without the extension that raises an
 * illegal-instruction fault, which terminates the process. This header therefore holds only
 * declarations, type aliases, a constexpr scalar and aggregates with trivial special members.
 * mismatch_sink declares no member with a body, and the AVX units only call it through a
 * reference, so they emit neither its constructor nor its vftable. Include nothing else here.
 *
 * Threading: every entry is stateless and may be called concurrently from every worker on
 * disjoint ranges.
 */
#pragma once

#include <cstdint>
#include <cstddef>

#include "core/patterns/patterns.h"

namespace rq::engine {

/** One x64 cache line: the granularity of flush_range and of the re-read flush. */
inline constexpr std::size_t k_cache_line_bytes = 64;

/* ---------------------------------------------------------------------------------------------
 * Mismatch reporting
 * ------------------------------------------------------------------------------------------- */

/**
 * Receives each mismatching word that verify or verify_and_stream finds.
 *
 * Invoked only on the mismatch path, synchronously on the worker thread that called the kernel,
 * once per mismatching 64-bit word, in traversal order: ascending address for direction +1,
 * descending for -1. verify_and_stream calls it before it overwrites the word, so a re-read the
 * sink performs (the reread entry, reached through IMemoryAccess::reread_word) sees the stored
 * wrong value rather than the next pattern's.
 *
 * Implemented by core/errors/error_collector, one per-worker sink bound to the current
 * access_context, and by the test sinks in tests/engine.tests. An implementation must not throw:
 * the kernels are noexcept, so an escaping exception calls std::terminate. The production sink
 * also preallocates everything and never waits, because the call sits inside every worker's
 * verification loop.
 */
class mismatch_sink {
public:
    /**
     * Reports one mismatching word.
     *
     * @param address Virtual address of the word.
     * @param expected Pattern value the word should hold.
     * @param actual Value the verification load returned. The kernel never loads the word a
     *        second time for it: a transient fault need not repeat, and the sink's re-read is
     *        the deliberate second read.
     */
    virtual void on_mismatch(const std::uint64_t* address, std::uint64_t expected,
                             std::uint64_t actual) noexcept = 0;

protected:
    /**
     * Non-virtual and protected: sinks are never deleted through this base. Defaulted on its
     * first declaration, so it is trivial and emits no code in any unit.
     */
    ~mismatch_sink() = default;
};

/* ---------------------------------------------------------------------------------------------
 * Kernel entry types
 * ------------------------------------------------------------------------------------------- */

/**
 * @name Kernel entry types
 * Common semantics of the range entries (fill_stream, verify and verify_and_stream):
 * - A range is `words` 64-bit words starting at an 8-byte-aligned pointer. It may be as short as
 *   one word or several MiB, and its start and end may fall anywhere at 8-byte granularity: each
 *   kernel splits it into scalar edge words and aligned vectors itself. words == 0 does nothing,
 *   and verify then returns 0.
 * - Word p[i] has logical word index first_word_index + i, and its value is the stateless pattern
 *   function of (spec, first_word_index + i) from core/patterns/patterns.h, bit-identical to
 *   expected_value. The index is logical, never the virtual address.
 * - direction is +1 for ascending traversal and -1 for descending; kernels treat any negative
 *   value as descending. It changes only the order of stores, loads and sink calls, never the
 *   value a word receives.
 * - Kernels never check the stop flag, never count coverage and never fence unless stated. The
 *   step runner composes them into the per-phase sequences of the flush-before-verification
 *   rule, and the IMemoryAccess layers count and log around them.
 * @{
 */

/**
 * Streams the pattern over the range with non-temporal stores: MOVNTDQ or VMOVNTDQ for aligned
 * vectors, MOVNTI (_mm_stream_si64) for unaligned edge words. No fence: streaming stores are
 * weakly ordered and drain through write-combining buffers, so the caller fences before any
 * flush or verification of the range. Nothing relies on how a streaming store treats a line that
 * is already cached; the flush before verification removes any such copy.
 */
using fill_stream_fn = void (*)(std::uint64_t* dst, std::size_t words, const pattern_spec& spec,
                                std::uint64_t first_word_index, int direction) noexcept;

/**
 * One MFENCE. It orders every earlier load, store, streaming store and CLFLUSHOPT before every
 * later load or store. SFENCE is never used as this fence: it orders stores only, not earlier
 * stores against later loads, so a verification load could pass a flush still in flight.
 */
using full_fence_fn = void (*)() noexcept;

/**
 * CLFLUSHOPT of every 64-byte line overlapping [p, p + bytes), or CLFLUSH where CPUID.7.0:EBX
 * bit 23 reports no CLFLUSHOPT. Either writes the line back and invalidates it at every cache
 * level, whatever its prior state. No fence: CLFLUSHOPT is weakly ordered, and the caller's
 * MFENCE on each side orders it. bytes == 0 does nothing.
 */
using flush_range_fn = void (*)(const void* p, std::size_t bytes) noexcept;

/**
 * Loads every word with ordinary loads and compares it with its regenerated expected value,
 * reporting each mismatching word to the sink individually. Returns the number of mismatching
 * words. Never uses MOVNTDQA, which has no non-temporal effect on write-back memory. Relies on
 * the caller having applied fence, flush and fence to the range since its last store or load,
 * and assumes nothing about earlier cache residency.
 */
using verify_fn = std::uint64_t (*)(const std::uint64_t* src, std::size_t words,
                                    const pattern_spec& spec, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) noexcept;

/**
 * One moving-inversion sweep over the range: for each word in traversal order, load it, compare
 * it with `expected`, report a mismatch to the sink before overwriting, then stream the `next`
 * pattern's value for the same logical index non-temporally. Returns the number of mismatching
 * words. No fence: the runner follows each sweep with fence, flush and fence, so the next
 * sweep's loads never hit a copy that this sweep's own loads left.
 */
using verify_and_stream_fn = std::uint64_t (*)(std::uint64_t* p, std::size_t words,
                                               const pattern_spec& expected,
                                               const pattern_spec& next,
                                               std::uint64_t first_word_index, int direction,
                                               mismatch_sink& sink) noexcept;

/**
 * Re-reads one word under the re-read rule: MFENCE, a flush of the word's line (CLFLUSHOPT or
 * CLFLUSH, matching the table's flush_range), MFENCE, then one volatile 64-bit load, whose value
 * it returns. The first fence makes earlier stores globally visible before the flush, the second
 * orders the flush before the load, and the volatile load cannot be elided or hoisted. A re-read
 * never counts as coverage.
 */
using reread_fn = std::uint64_t (*)(const std::uint64_t* p) noexcept;

/** @} */

/* ---------------------------------------------------------------------------------------------
 * Strided entries: the BankStride order in bulk
 * ------------------------------------------------------------------------------------------- */

/**
 * The visits one strided kernel call makes: 64-byte lines a fixed stride apart within a sweep,
 * each sweep starting one line further on. That is the RQ_ORDER_BANK_STRIDE order, whose
 * reference definition is order_cursor (core/patterns/access_order.h); one call covers many of
 * its visits, so a work group of 16,384 lines crosses the kernel table once rather than once per
 * line, and pays no per-line range split or pattern-cursor construction.
 *
 * Visit k is the line at byte offset (k / lines_per_sweep) x 64 + (k % lines_per_sweep) x
 * stride_bytes from the span start. A call makes visits first_visit to first_visit +
 * visit_count - 1, in that order. With lines_per_sweep x stride_bytes equal to the span, visits
 * 0 to span / 64 - 1 name every line of the span exactly once, so one phase may be issued as
 * consecutive calls over consecutive slices of the visit sequence.
 *
 * Preconditions, which the kernels do not check: when visit_count is non-zero, stride_bytes is
 * a non-zero multiple of 64, lines_per_sweep is non-zero, lines_per_sweep x stride_bytes is no
 * larger than the span, and first_visit + visit_count <= lines_per_sweep x stride_bytes / 64.
 * Every visited line then lies inside the span and distinct visits name distinct lines.
 * visit_count == 0 makes a call do nothing, whatever the other fields hold.
 *
 * An aggregate without member initialisers, for the reason pattern_spec gives: initialise every
 * field, for example strided_visits{stride, span / stride, first, count}.
 */
struct strided_visits {
    /** Distance in bytes between consecutive visits of one sweep. */
    std::uint64_t stride_bytes;
    /** Visits per sweep: the span divided by the stride for a whole-span order. */
    std::uint64_t lines_per_sweep;
    /** Index k of the call's first visit in the order's visit sequence. */
    std::uint64_t first_visit;
    /** Visits the call makes. */
    std::uint64_t visit_count;
};

/**
 * @name Strided entry types
 * Common semantics of fill_stream_strided and verify_strided:
 * - `span` points at the span's first word and is 64-byte aligned, so every visited line is one
 *   whole cache line and is handled with aligned vectors alone, with no scalar edge word.
 * - Word span[j] has logical word index span_first_word_index + j, and its value is the stateless
 *   pattern function of (spec, span_first_word_index + j), bit-identical to expected_value, as
 *   for the range entries.
 * - Each visited line's eight words are handled in ascending address order, and the lines in
 *   visit order.
 * - The kernels never check the stop flag, never count coverage and never fence: the step runner
 *   sizes each call to one work group and composes the calls into the flush-before-verification
 *   sequence, as for the range entries.
 * @{
 */

/**
 * Streams the pattern over every visited line with aligned non-temporal stores (MOVNTDQ or
 * VMOVNTDQ). Each line is written whole by consecutive streaming stores, so its write-combining
 * buffer fills completely. No fence, as for fill_stream_fn.
 */
using fill_stream_strided_fn = void (*)(std::uint64_t* span, const strided_visits& visits,
                                        const pattern_spec& spec,
                                        std::uint64_t span_first_word_index) noexcept;

/**
 * Loads every word of every visited line with aligned ordinary loads, never MOVNTDQA, and
 * compares it with its regenerated expected value. Each mismatching word is reported to the sink
 * individually, in visit order and ascending within a line. Returns the number of mismatching
 * words. Relies on the caller having applied fence, flush and fence to every visited line since
 * its last store or load, as for verify_fn.
 */
using verify_strided_fn = std::uint64_t (*)(const std::uint64_t* span, const strided_visits& visits,
                                            const pattern_spec& spec,
                                            std::uint64_t span_first_word_index,
                                            mismatch_sink& sink) noexcept;

/** @} */

/**
 * One instruction-set level's kernels.
 *
 * An aggregate with no member initialisers and only trivial special members, so each kernel
 * unit can define its table constinit (static initialisation only: no dynamic initialiser runs
 * at DLL load on every CPU) and consumers copy it by value. tables_for returns a copy with the
 * CLFLUSH helpers patched in where CLFLUSHOPT is absent, and real_memory_access keeps its own
 * copy. Every entry is non-null in the three kernel sets.
 */
struct kernel_table {
    /**
     * RQ_ISA_* value of the set the vector entries belong to: RQ_ISA_SSE2, RQ_ISA_AVX2 or
     * RQ_ISA_AVX512, never RQ_ISA_AUTO. isa_dispatch and the tests read it. full_fence,
     * flush_range and reread point at the baseline helpers in every set.
     */
    std::uint32_t isa;
    /** Streaming fill of a range; see fill_stream_fn. */
    fill_stream_fn fill_stream;
    /** MFENCE; see full_fence_fn. */
    full_fence_fn full_fence;
    /** Line flush of a range; see flush_range_fn. */
    flush_range_fn flush_range;
    /** Verification of a range; see verify_fn. */
    verify_fn verify;
    /** Moving-inversion sweep of a range; see verify_and_stream_fn. */
    verify_and_stream_fn verify_and_stream;
    /** Flushed re-read of one word; see reread_fn. */
    reread_fn reread;
    /** Streaming fill of BankStride visits; see fill_stream_strided_fn. */
    fill_stream_strided_fn fill_stream_strided;
    /** Verification of BankStride visits; see verify_strided_fn. */
    verify_strided_fn verify_strided;
};

/* ---------------------------------------------------------------------------------------------
 * Kernel sets and baseline helpers
 * ------------------------------------------------------------------------------------------- */

/**
 * @name Kernel sets
 * Each is defined in its own unit as constinit const kernel_table ... = { ... } after including
 * this header, so this declaration gives it external linkage and constinit rules out a dynamic
 * initialiser. The raw sets' flush_range and reread entries use CLFLUSHOPT. Obtain a table
 * through tables_for (core/kernels/isa_dispatch.h), which copies the set select_isa chose and
 * patches in the CLFLUSH helpers where CLFLUSHOPT is absent. Never call an entry of
 * k_kernels_avx2 or k_kernels_avx512 unless select_isa reported that set available: on a CPU
 * without it their vector entries raise an illegal-instruction fault, which terminates the
 * process.
 * @{
 */
/**
 * SSE2 kernels (kernels_sse2.cpp, x64 baseline): the reduced path used only when AVX2 is absent,
 * and the reference the other sets are tested against.
 */
extern const kernel_table k_kernels_sse2;
/** AVX2 kernels (kernels_avx2.cpp, /arch:AVX2): the full-speed baseline. */
extern const kernel_table k_kernels_avx2;
/** AVX-512 kernels (kernels_avx512.cpp, /arch:AVX512): require F, CD, BW, DQ and VL. */
extern const kernel_table k_kernels_avx512;
/** @} */

/**
 * @name Baseline fence, flush and re-read helpers
 * Defined in kernels_sse2.cpp at the SSE2 baseline, so they run on every x64 CPU and no fence or
 * flush code is ever compiled at a higher /arch level. All three sets point their full_fence,
 * flush_range and reread entries at these, and tables_for substitutes the CLFLUSH variants when
 * CPUID reports no CLFLUSHOPT.
 * @{
 */
/** full_fence_fn: one MFENCE. */
void full_fence_mfence() noexcept;
/** flush_range_fn with CLFLUSHOPT; requires CPUID.7.0:EBX bit 23. No fence. */
void flush_range_clflushopt(const void* p, std::size_t bytes) noexcept;
/** flush_range_fn with CLFLUSH, architectural on x64; used where CLFLUSHOPT is absent. No fence. */
void flush_range_clflush(const void* p, std::size_t bytes) noexcept;
/** reread_fn with CLFLUSHOPT between the two MFENCEs; requires CPUID.7.0:EBX bit 23. */
std::uint64_t reread_clflushopt(const std::uint64_t* p) noexcept;
/** reread_fn with CLFLUSH between the two MFENCEs; used where CLFLUSHOPT is absent. */
std::uint64_t reread_clflush(const std::uint64_t* p) noexcept;
/** @} */

}  // namespace rq::engine
