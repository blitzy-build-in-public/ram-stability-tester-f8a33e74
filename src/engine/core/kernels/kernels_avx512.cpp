/**
 * @file kernels_avx512.cpp
 * @brief AVX-512 kernel set: streaming fill, verification and moving-inversion sweep over
 *        64-byte ZMM vectors, one cache line per vector.
 *
 * This unit is compiled with /arch:AVX512 (a per-file EnableEnhancedInstructionSet in
 * engine_core.vcxproj). isa_dispatch selects k_kernels_avx512 only when AVX-512 F, CD, BW, DQ and
 * VL are all present and the OS has enabled ZMM state, because those are every subset
 * /arch:AVX512 may emit here; on any other CPU an instruction from this unit raises an
 * illegal-instruction fault, which terminates the process. The unit therefore keeps the rules
 * kernels.h and patterns.h describe:
 * - it includes only those two headers and the intrinsic and integer headers, because another
 *   header's inline functions would be compiled here under names that baseline code shares;
 * - the kernels have internal linkage, and every template it instantiates carries isa_avx512;
 * - its only namespace-scope object is the constinit table, so no dynamic initialiser runs at
 *   DLL load on every CPU;
 * - fences, flushes and re-reads are the baseline helpers from kernels_sse2.cpp, so no fence or
 *   flush code is compiled at this level.
 *
 * Every range splits into a scalar head (the 0-7 words before the first 64-byte boundary), a body
 * of aligned 8-word vectors and a scalar tail (0-7 words). Ascending traversal visits head, body
 * and tail upward; descending visits tail, body and head downward, the body from its last vector
 * to its first. The edges stay scalar rather than masked: AVX-512 has no masked non-temporal
 * store, and scalar edges make every kernel set issue the same per-word operations there.
 */
#include "core/kernels/kernels.h"
#include "core/patterns/patterns.h"

#include <immintrin.h>
#include <cstdint>
#include <cstddef>

// _mm_stream_si64, the MOVNTI intrinsic for edge words, is an <emmintrin.h> macro for
// _mm_stream_si64x, which only <intrin.h> declares. <intrin.h> stays out of this unit like every
// header beyond the intrinsic and integer ones, so the intrinsic is declared here exactly as
// <intrin.h> declares it. The compiler expands each use to one MOVNTI: no function of that name
// is called, defined or emitted.
extern "C" void _mm_stream_si64x(__int64* dst, __int64 value);

namespace rq::engine {
namespace {

/** Vector type and lane count of this set: eight 64-bit words in a ZMM register. */
using avx512 = simd<isa_avx512>;
/** Block generator of this set; its symbols carry the isa_avx512 tag. */
using avx512_cursor = pattern_cursor<isa_avx512>;

static_assert(avx512::lanes * sizeof(std::uint64_t) == k_cache_line_bytes,
              "one AVX-512 vector must cover exactly one cache line");
static_assert(sizeof(avx512::vec) == k_cache_line_bytes, "ZMM vectors are 64 bytes");

/** How a range splits into scalar edge words and aligned vectors. */
struct range_split {
    /** Words before the first 64-byte boundary, 0-7; the whole range when it is shorter. */
    std::size_t head;
    /** Aligned 8-word vectors after the head. */
    std::size_t vectors;
    /** Words after the last whole vector, 0-7. */
    std::size_t tail;
};

/** Splits [p, p + words) at 64-byte boundaries; p is 8-byte aligned. */
range_split split_range(const std::uint64_t* p, std::size_t words) noexcept {
    const std::size_t misaligned_words =
        (reinterpret_cast<std::uintptr_t>(p) & (k_cache_line_bytes - 1)) / sizeof(std::uint64_t);
    std::size_t head = misaligned_words == 0 ? 0 : avx512::lanes - misaligned_words;
    if (head > words) {
        head = words;
    }
    const std::size_t rest = words - head;
    return range_split{head, rest / avx512::lanes, rest % avx512::lanes};
}

/**
 * Visits a range in traversal order. on_word(i) handles the edge word at offset i. on_body(first,
 * count, step) handles the body: `count` vectors, the first visited at word offset `first`, each
 * later one at `step` vectors from the previous, +1 ascending and -1 descending. A body
 * callback positions a pattern_cursor at `first` with direction `step`, which then advances in
 * the same order.
 */
template <class OnWord, class OnBody>
void traverse(const std::uint64_t* p, std::size_t words, int direction, OnWord&& on_word,
              OnBody&& on_body) noexcept {
    const range_split split = split_range(p, words);
    const std::size_t tail_first = split.head + split.vectors * avx512::lanes;
    if (direction >= 0) {
        for (std::size_t i = 0; i < split.head; ++i) {
            on_word(i);
        }
        if (split.vectors != 0) {
            on_body(split.head, split.vectors, 1);
        }
        for (std::size_t i = tail_first; i < words; ++i) {
            on_word(i);
        }
    } else {
        for (std::size_t i = words; i > tail_first;) {
            on_word(--i);
        }
        if (split.vectors != 0) {
            on_body(tail_first - avx512::lanes, split.vectors, -1);
        }
        for (std::size_t i = split.head; i > 0;) {
            on_word(--i);
        }
    }
}

/** Word offset of the n-th body vector visited from `first` in direction `step`. */
std::size_t body_offset(std::size_t first, std::size_t n, int step) noexcept {
    return step > 0 ? first + n * avx512::lanes : first - n * avx512::lanes;
}

/** One MOVNTI of an edge word. */
void stream_word(std::uint64_t* p, std::uint64_t value) noexcept {
    _mm_stream_si64(reinterpret_cast<long long*>(p), static_cast<long long>(value));
}

/**
 * Reports the mismatching lanes of one vector to the sink, in traversal order: lane 0 first when
 * ascending, lane 7 first when descending. `loaded` is the vector the verification load returned;
 * it is spilled to the stack and reported from there, never reloaded from memory, because a
 * transient fault need not repeat and the sink's re-read is the deliberate second read. Returns
 * the number of lanes reported.
 */
std::uint64_t report_vector(const std::uint64_t* block, avx512::vec expected, avx512::vec loaded,
                            __mmask8 differ, int direction, mismatch_sink& sink) noexcept {
    alignas(64) std::uint64_t expected_words[avx512::lanes];
    alignas(64) std::uint64_t actual_words[avx512::lanes];
    _mm512_store_si512(expected_words, expected);
    _mm512_store_si512(actual_words, loaded);
    std::uint64_t reported = 0;
    for (std::size_t n = 0; n < avx512::lanes; ++n) {
        const std::size_t lane = direction < 0 ? avx512::lanes - 1 - n : n;
        if (((static_cast<unsigned int>(differ) >> lane) & 1u) != 0) {
            sink.on_mismatch(block + lane, expected_words[lane], actual_words[lane]);
            ++reported;
        }
    }
    return reported;
}

/** fill_stream_fn: VMOVNTDQ for aligned vectors, MOVNTI for edge words, no fence. */
void fill_stream_avx512(std::uint64_t* dst, std::size_t words, const pattern_spec& spec,
                        std::uint64_t first_word_index, int direction) noexcept {
    traverse(
        dst, words, direction,
        [&](std::size_t i) noexcept {
            stream_word(dst + i, pattern_word<isa_avx512>(spec, first_word_index + i));
        },
        [&](std::size_t first, std::size_t count, int step) noexcept {
            avx512_cursor cursor(spec, first_word_index + first, step);
            for (std::size_t n = 0; n < count; ++n) {
                _mm512_stream_si512(dst + body_offset(first, n, step), cursor.current());
                cursor.advance();
            }
        });
}

/** verify_fn: ordinary loads compared with the regenerated pattern; never MOVNTDQA. */
std::uint64_t verify_avx512(const std::uint64_t* src, std::size_t words, const pattern_spec& spec,
                            std::uint64_t first_word_index, int direction,
                            mismatch_sink& sink) noexcept {
    std::uint64_t mismatches = 0;
    traverse(
        src, words, direction,
        [&](std::size_t i) noexcept {
            const std::uint64_t expected = pattern_word<isa_avx512>(spec, first_word_index + i);
            const std::uint64_t actual = src[i];
            if (actual != expected) {
                sink.on_mismatch(src + i, expected, actual);
                ++mismatches;
            }
        },
        [&](std::size_t first, std::size_t count, int step) noexcept {
            avx512_cursor cursor(spec, first_word_index + first, step);
            for (std::size_t n = 0; n < count; ++n) {
                const std::uint64_t* block = src + body_offset(first, n, step);
                const avx512::vec expected = cursor.current();
                const avx512::vec loaded = _mm512_load_si512(block);
                const __mmask8 differ = _mm512_cmpneq_epi64_mask(loaded, expected);
                if (differ != 0) {
                    mismatches += report_vector(block, expected, loaded, differ, step, sink);
                }
                cursor.advance();
            }
        });
    return mismatches;
}

/**
 * verify_and_stream_fn: per word or vector, load, compare with `expected`, report any mismatch,
 * and only then stream the `next` value over it, so a re-read the sink performs still sees the
 * wrong value. No fence.
 */
std::uint64_t verify_and_stream_avx512(std::uint64_t* p, std::size_t words,
                                       const pattern_spec& expected, const pattern_spec& next,
                                       std::uint64_t first_word_index, int direction,
                                       mismatch_sink& sink) noexcept {
    std::uint64_t mismatches = 0;
    traverse(
        p, words, direction,
        [&](std::size_t i) noexcept {
            const std::uint64_t word_index = first_word_index + i;
            const std::uint64_t want = pattern_word<isa_avx512>(expected, word_index);
            const std::uint64_t actual = p[i];
            if (actual != want) {
                sink.on_mismatch(p + i, want, actual);
                ++mismatches;
            }
            stream_word(p + i, pattern_word<isa_avx512>(next, word_index));
        },
        [&](std::size_t first, std::size_t count, int step) noexcept {
            avx512_cursor expected_cursor(expected, first_word_index + first, step);
            avx512_cursor next_cursor(next, first_word_index + first, step);
            for (std::size_t n = 0; n < count; ++n) {
                std::uint64_t* block = p + body_offset(first, n, step);
                const avx512::vec want = expected_cursor.current();
                const avx512::vec loaded = _mm512_load_si512(block);
                const __mmask8 differ = _mm512_cmpneq_epi64_mask(loaded, want);
                if (differ != 0) {
                    mismatches += report_vector(block, want, loaded, differ, step, sink);
                }
                _mm512_stream_si512(block, next_cursor.current());
                expected_cursor.advance();
                next_cursor.advance();
            }
        });
    return mismatches;
}

}  // namespace

// The raw table names the CLFLUSHOPT helpers. Callers obtain it only through tables_for, which
// patches in the CLFLUSH helpers where CPUID reports no CLFLUSHOPT.
constinit const kernel_table k_kernels_avx512 = {
    .isa = RQ_ISA_AVX512,
    .fill_stream = &fill_stream_avx512,
    .full_fence = &full_fence_mfence,
    .flush_range = &flush_range_clflushopt,
    .verify = &verify_avx512,
    .verify_and_stream = &verify_and_stream_avx512,
    .reread = &reread_clflushopt,
};

}  // namespace rq::engine
