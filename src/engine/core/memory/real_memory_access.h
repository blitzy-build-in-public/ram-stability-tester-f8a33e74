/**
 * @file real_memory_access.h
 * @brief real_memory_access, the production IMemoryAccess layer: forwards every primitive to the
 *        session's kernel_table.
 *
 * The production DLL uses this layer alone; the test DLL wraps it as the innermost layer of
 * fault-injecting -> counting -> real (session_dependencies::wrap_memory_access in
 * core/session.h).
 *
 * @par Build constraint
 * Baseline-only, like core/memory/memory_access.h: include it only from SSE2 translation units,
 * never from core/kernels/kernels_avx2.cpp or core/kernels/kernels_avx512.cpp.
 */
#pragma once

#include <cstdint>
#include <cstddef>

#include "core/memory/memory_access.h"
#include "core/kernels/kernels.h"

namespace rq::engine {

/**
 * The real memory-access layer over one instruction-set level's kernels.
 *
 * @par State and threading
 * Holds nothing but its own copy of the kernel_table, taken at construction, and every kernel is
 * stateless. Concurrent calls from all workers on disjoint ranges are therefore safe without
 * synchronisation.
 *
 * @par Ordering
 * Adds no fence or flush of its own: each primitive is exactly one call to the kernel of the same
 * role. The step runner composes stream_fill, fence, flush and verify into the
 * flush-before-verification sequences (fence, flush of the line, fence, before every
 * verification load), which keeps every ordering step visible to counting_memory_access and its
 * assertions. reread_word alone carries the whole fence, flush-line, fence, load sequence,
 * because that sequence is the kernel_table::reread contract.
 *
 * @par Exceptions
 * Never throws: every forwarded kernel is noexcept.
 *
 * Final: substitute layers wrap it by composition through std::unique_ptr<IMemoryAccess>, never
 * by inheritance.
 */
class real_memory_access final : public IMemoryAccess {
public:
    /**
     * Copies `kernels`; the layer never refers to the caller's table afterwards, so a temporary
     * such as the value tables_for (core/kernels/isa_dispatch.h) returns may be passed directly.
     *
     * @param kernels A complete table: every entry non-null, with the CLFLUSH helpers already
     *        patched in where the CPU lacks CLFLUSHOPT.
     */
    explicit real_memory_access(const kernel_table& kernels) noexcept;

    /**
     * Defined out of line in real_memory_access.cpp, the key function, so the vftable is emitted
     * in that single baseline unit.
     */
    ~real_memory_access() override;

    /**
     * kernel_table::fill_stream over the range. ctx is unused. No fence.
     *
     * @param ctx Issuing context; ignored.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing.
     * @param spec Pattern instance whose values the range receives.
     * @param first_word_index Logical word index of words[0].
     * @param direction +1 ascending, negative descending.
     */
    void stream_fill(const access_context& ctx, std::uint64_t* words, std::size_t word_count,
                     const pattern_spec& spec, std::uint64_t first_word_index,
                     int direction) override;

    /**
     * kernel_table::full_fence: one MFENCE. ctx is unused.
     *
     * @param ctx Issuing context; ignored.
     */
    void fence(const access_context& ctx) override;

    /**
     * kernel_table::flush_range over word_count x 8 bytes: CLFLUSHOPT, or CLFLUSH where patched
     * in, of every line overlapping the range. ctx is unused. No fence.
     *
     * @param ctx Issuing context; ignored.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing.
     */
    void flush(const access_context& ctx, const std::uint64_t* words,
               std::size_t word_count) override;

    /**
     * kernel_table::verify over the range, reporting each mismatch to `sink`. ctx is unused.
     *
     * @param ctx Issuing context; ignored.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing and returns 0.
     * @param expected Pattern instance the range should hold.
     * @param first_word_index Logical word index of words[0].
     * @param direction +1 ascending, negative descending.
     * @param sink Receives each mismatching word in traversal order.
     * @return Number of mismatching words.
     */
    std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                         std::size_t word_count, const pattern_spec& expected,
                         std::uint64_t first_word_index, int direction,
                         mismatch_sink& sink) override;

    /**
     * kernel_table::verify_and_stream: one moving-inversion sweep over the range, reporting each
     * mismatch to `sink` before the word is overwritten with `next`'s value. ctx is unused. No
     * fence.
     *
     * @param ctx Issuing context; ignored.
     * @param words First word of the range.
     * @param word_count Number of 64-bit words; 0 does nothing and returns 0.
     * @param expected Pattern instance the range should hold.
     * @param next Pattern instance the range receives.
     * @param first_word_index Logical word index of words[0].
     * @param direction +1 ascending, negative descending.
     * @param sink Receives each mismatching word in traversal order.
     * @return Number of mismatching words.
     */
    std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                    std::size_t word_count, const pattern_spec& expected,
                                    const pattern_spec& next, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) override;

    /**
     * kernel_table::fill_stream_strided over the visited lines of the span. ctx and
     * span_word_count are unused. No fence.
     *
     * @param ctx Issuing context; ignored.
     * @param span First word of the span, 64-byte aligned.
     * @param span_word_count Words in the span; ignored, because the kernel needs only the
     *        visits.
     * @param visits The call's visits; visit_count 0 does nothing.
     * @param spec Pattern instance whose values the visited lines receive.
     * @param span_first_word_index Logical word index of span[0].
     */
    void stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                             std::size_t span_word_count, const strided_visits& visits,
                             const pattern_spec& spec,
                             std::uint64_t span_first_word_index) override;

    /**
     * kernel_table::verify_strided over the visited lines of the span, reporting each mismatch
     * to `sink`. ctx and span_word_count are unused.
     *
     * @param ctx Issuing context; ignored.
     * @param span First word of the span, 64-byte aligned.
     * @param span_word_count Words in the span; ignored.
     * @param visits The call's visits; visit_count 0 does nothing and returns 0.
     * @param expected Pattern instance the visited lines should hold.
     * @param span_first_word_index Logical word index of span[0].
     * @param sink Receives each mismatching word in visit order, ascending within a line.
     * @return Number of mismatching words.
     */
    std::uint64_t verify_strided(const access_context& ctx, const std::uint64_t* span,
                                 std::size_t span_word_count, const strided_visits& visits,
                                 const pattern_spec& expected, std::uint64_t span_first_word_index,
                                 mismatch_sink& sink) override;

    /**
     * kernel_table::reread: MFENCE, flush of the word's line, MFENCE, one volatile load. ctx is
     * unused. Never counts as coverage.
     *
     * @param ctx The verification's context with phase_kind::reread; ignored.
     * @param word The mismatching word.
     * @return The value the flushed load returned.
     */
    std::uint64_t reread_word(const access_context& ctx,
                              const std::uint64_t* word) noexcept override;

    /**
     * The table this layer forwards to, so tests and diagnostics can read kernels().isa.
     *
     * @return This layer's own copy; valid for the layer's lifetime.
     */
    const kernel_table& kernels() const noexcept;

private:
    /** The layer's own copy of the session's table. */
    kernel_table kernels_;
};

}  // namespace rq::engine
