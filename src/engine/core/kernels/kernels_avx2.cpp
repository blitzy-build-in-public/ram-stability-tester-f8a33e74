/**
 * @file kernels_avx2.cpp
 * @brief The AVX2 kernel set: streaming fill, verification and moving-inversion sweeps over
 *        four-word YMM vectors, the engine's full-speed baseline.
 *
 * engine_core.vcxproj compiles this unit, and only this unit, with /arch:AVX2, so any
 * instruction the compiler emits here may be VEX-encoded and would fault on a CPU without AVX2.
 * Nothing in it is therefore reachable before isa_dispatch has selected the AVX2 set:
 * - the kernels and their helpers have internal linkage, so no baseline unit binds to them by
 *   name and the linker never keeps an AVX2 copy of a shared inline function for baseline
 *   callers;
 * - the only templates instantiated from patterns.h carry the isa_avx2 tag, so their COMDATs
 *   are named for this set alone;
 * - k_kernels_avx2 is constinit, so no dynamic initialiser runs at DLL load on every CPU;
 * - the fence, flush and re-read entries are the baseline helpers from kernels_sse2.cpp.
 *
 * Mismatching lanes are found with a plain loop rather than bit-scan, LZCNT, TZCNT or POPCNT
 * intrinsics, so the set needs no CPUID feature beyond those isa_dispatch requires for it.
 *
 * The sink is baseline code. MSVC emits VZEROUPPER before every call and return in a function
 * that has dirtied the upper YMM state, so the AVX-to-SSE transition needs no code here.
 */
#include "core/kernels/kernels.h"
#include "core/patterns/patterns.h"

#include <immintrin.h>
// MSVC declares _mm_stream_si64x, to which <emmintrin.h> maps _mm_stream_si64 (MOVNTI), only in
// <intrin.h>. That header holds declarations alone, so it compiles no function in this
// /arch:AVX2 unit under a name a baseline unit could share.
#include <intrin.h>
#include <cstdint>
#include <cstddef>

namespace rq::engine {
namespace {

using avx2 = simd<isa_avx2>;
using avx2_cursor = pattern_cursor<isa_avx2>;

/**
 * A range split around 32-byte boundaries, the alignment VMOVNTDQ and the aligned vector loads
 * require. Words [0, head) precede the first boundary, [head, tail) form `vectors` aligned
 * four-word vectors, and [tail, words) follow the last vector. Ranges start and end anywhere at
 * 8-byte granularity, so each edge holds 0 to 3 words.
 */
struct range_split {
    std::size_t head;
    std::size_t vectors;
    std::size_t tail;
};

range_split split_range(const std::uint64_t* p, std::size_t words) noexcept {
    constexpr std::size_t vector_bytes = avx2::lanes * sizeof(std::uint64_t);
    const std::size_t misaligned_words =
        static_cast<std::size_t>(reinterpret_cast<std::uintptr_t>(p) & (vector_bytes - 1)) /
        sizeof(std::uint64_t);
    std::size_t head = misaligned_words == 0 ? 0 : avx2::lanes - misaligned_words;
    if (head > words) {
        head = words;
    }
    const std::size_t vectors = (words - head) / avx2::lanes;
    return range_split{head, vectors, head + vectors * avx2::lanes};
}

/**
 * Visits a range in traversal order. Ascending: head words upwards, the vectors, tail words
 * upwards. Descending: tail words downwards, the vectors, head words downwards.
 *
 * edge(i) handles the scalar word at index i. body(first, step, count) handles the aligned
 * vectors: `count` of them, the first at word index `first`, each next one `step` words on,
 * modulo 2^64, so a descending step is 0 - lanes and starts at the highest vector. body is not
 * called when the range holds no whole aligned vector.
 */
template <class Edge, class Body>
void traverse(const range_split& split, std::size_t words, bool descending, Edge&& edge,
              Body&& body) noexcept {
    if (descending) {
        for (std::size_t i = words; i > split.tail;) {
            --i;
            edge(i);
        }
        if (split.vectors != 0) {
            body(split.tail - avx2::lanes, std::size_t{0} - avx2::lanes, split.vectors);
        }
        for (std::size_t i = split.head; i > 0;) {
            --i;
            edge(i);
        }
    } else {
        for (std::size_t i = 0; i < split.head; ++i) {
            edge(i);
        }
        if (split.vectors != 0) {
            body(split.head, avx2::lanes, split.vectors);
        }
        for (std::size_t i = split.tail; i < words; ++i) {
            edge(i);
        }
    }
}

/** MOVNTI of one edge word: the non-temporal store for words outside the aligned vectors. */
void stream_word(std::uint64_t* p, std::uint64_t value) noexcept {
    _mm_stream_si64(reinterpret_cast<long long*>(p), static_cast<long long>(value));
}

/** True when every 64-bit lane of a equals the same lane of b. */
bool all_lanes_equal(__m256i a, __m256i b) noexcept {
    return _mm256_movemask_epi8(_mm256_cmpeq_epi64(a, b)) == -1;
}

/**
 * Checks one edge word with an ordinary load and reports it if it mismatches. Returns the
 * number of mismatching words, 0 or 1.
 */
std::uint64_t check_word(const std::uint64_t* address, std::uint64_t expected,
                         mismatch_sink& sink) noexcept {
    const std::uint64_t actual = *address;
    if (actual == expected) {
        return 0;
    }
    sink.on_mismatch(address, expected, actual);
    return 1;
}

/**
 * Reports every mismatching lane of a vector whose compare failed, in traversal order: lane 0
 * first when ascending, lane 3 first when descending. Returns the number reported.
 *
 * `actual` values come from the vector the verification load returned, spilled to the stack:
 * memory is never read a second time, because a transient fault need not repeat and the sink's
 * re-read is the deliberate second read. Kept out of line so the hot loops carry only a compare
 * and a branch for a path that runs only on failing memory.
 */
__declspec(noinline) std::uint64_t report_block(const std::uint64_t* block, __m256i loaded,
                                                __m256i expected, bool descending,
                                                mismatch_sink& sink) noexcept {
    alignas(32) std::uint64_t actual_lanes[avx2::lanes];
    alignas(32) std::uint64_t expected_lanes[avx2::lanes];
    _mm256_store_si256(reinterpret_cast<__m256i*>(actual_lanes), loaded);
    _mm256_store_si256(reinterpret_cast<__m256i*>(expected_lanes), expected);
    std::uint64_t mismatches = 0;
    for (std::size_t n = 0; n < avx2::lanes; ++n) {
        const std::size_t k = descending ? avx2::lanes - 1 - n : n;
        if (actual_lanes[k] != expected_lanes[k]) {
            sink.on_mismatch(block + k, expected_lanes[k], actual_lanes[k]);
            ++mismatches;
        }
    }
    return mismatches;
}

/* ---------------------------------------------------------------------------------------------
 * Kernels. Contracts: fill_stream_fn, verify_fn and verify_and_stream_fn in kernels.h. None
 * fences, checks the stop flag or counts coverage; the step runner and the IMemoryAccess layers
 * do that around them.
 * ------------------------------------------------------------------------------------------- */

void fill_stream_avx2(std::uint64_t* dst, std::size_t words, const pattern_spec& spec,
                      std::uint64_t first_word_index, int direction) noexcept {
    if (words == 0) {
        return;
    }
    traverse(
        split_range(dst, words), words, direction < 0,
        [&](std::size_t i) {
            stream_word(dst + i, pattern_word<isa_avx2>(spec, first_word_index + i));
        },
        [&](std::size_t first, std::size_t step, std::size_t count) {
            avx2_cursor cursor(spec, first_word_index + first, direction);
            std::size_t i = first;
            for (std::size_t v = 0; v < count; ++v) {
                _mm256_stream_si256(reinterpret_cast<__m256i*>(dst + i), cursor.current());
                cursor.advance();
                i += step;
            }
        });
}

std::uint64_t verify_avx2(const std::uint64_t* src, std::size_t words, const pattern_spec& spec,
                          std::uint64_t first_word_index, int direction,
                          mismatch_sink& sink) noexcept {
    if (words == 0) {
        return 0;
    }
    const bool descending = direction < 0;
    std::uint64_t mismatches = 0;
    traverse(
        split_range(src, words), words, descending,
        [&](std::size_t i) {
            mismatches +=
                check_word(src + i, pattern_word<isa_avx2>(spec, first_word_index + i), sink);
        },
        [&](std::size_t first, std::size_t step, std::size_t count) {
            avx2_cursor cursor(spec, first_word_index + first, direction);
            std::size_t i = first;
            for (std::size_t v = 0; v < count; ++v) {
                const std::uint64_t* block = src + i;
                const __m256i loaded = _mm256_load_si256(reinterpret_cast<const __m256i*>(block));
                const __m256i expected = cursor.current();
                if (!all_lanes_equal(loaded, expected)) {
                    mismatches += report_block(block, loaded, expected, descending, sink);
                }
                cursor.advance();
                i += step;
            }
        });
    return mismatches;
}

std::uint64_t verify_and_stream_avx2(std::uint64_t* p, std::size_t words,
                                     const pattern_spec& expected, const pattern_spec& next,
                                     std::uint64_t first_word_index, int direction,
                                     mismatch_sink& sink) noexcept {
    if (words == 0) {
        return 0;
    }
    const bool descending = direction < 0;
    std::uint64_t mismatches = 0;
    // Each word is reported before it is overwritten, so a re-read by the sink sees the wrong
    // value that was stored rather than the next pattern's.
    traverse(
        split_range(p, words), words, descending,
        [&](std::size_t i) {
            const std::uint64_t w = first_word_index + i;
            mismatches += check_word(p + i, pattern_word<isa_avx2>(expected, w), sink);
            stream_word(p + i, pattern_word<isa_avx2>(next, w));
        },
        [&](std::size_t first, std::size_t step, std::size_t count) {
            avx2_cursor expected_cursor(expected, first_word_index + first, direction);
            avx2_cursor next_cursor(next, first_word_index + first, direction);
            std::size_t i = first;
            for (std::size_t v = 0; v < count; ++v) {
                std::uint64_t* block = p + i;
                const __m256i loaded = _mm256_load_si256(reinterpret_cast<const __m256i*>(block));
                const __m256i expected_block = expected_cursor.current();
                if (!all_lanes_equal(loaded, expected_block)) {
                    mismatches += report_block(block, loaded, expected_block, descending, sink);
                }
                _mm256_stream_si256(reinterpret_cast<__m256i*>(block), next_cursor.current());
                expected_cursor.advance();
                next_cursor.advance();
                i += step;
            }
        });
    return mismatches;
}

}  // namespace

// The raw set uses the CLFLUSHOPT helpers. tables_for substitutes the CLFLUSH ones on CPUs that
// have AVX2 but lack CLFLUSHOPT (Haswell and Broadwell, for example), so this table is reached
// only through tables_for.
constinit const kernel_table k_kernels_avx2 = {
    .isa = RQ_ISA_AVX2,
    .fill_stream = &fill_stream_avx2,
    .full_fence = &full_fence_mfence,
    .flush_range = &flush_range_clflushopt,
    .verify = &verify_avx2,
    .verify_and_stream = &verify_and_stream_avx2,
    .reread = &reread_clflushopt,
};

}  // namespace rq::engine
