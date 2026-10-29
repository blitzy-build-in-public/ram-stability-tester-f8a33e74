/**
 * @file kernels_sse2.cpp
 * @brief The SSE2 kernel set and the baseline fence, flush and re-read helpers of all three sets.
 *
 * This unit is compiled at the x64 SSE2 baseline and must never receive an /arch setting. Its
 * vector kernels are the reduced path that dispatch selects only when AVX2 is absent, and the
 * reference the AVX2 and AVX-512 sets are tested against. Its helpers are the full_fence,
 * flush_range and reread entries of every set, so no fence or flush code is ever compiled at a
 * higher /arch level and every one of those entries runs on every x64 CPU.
 *
 * Pattern templates are instantiated here only with isa_sse2 (see the COMDAT rule in
 * core/patterns/patterns.h). Everything except the five helpers and k_kernels_sse2 has internal
 * linkage.
 */
#include "core/kernels/kernels.h"
#include "core/patterns/patterns.h"
#include "ramquick_engine.h"

#include <immintrin.h>
// MSVC 14.44 declares _mm_clflushopt and _mm_stream_si64x, the target of the _mm_stream_si64
// macro, only in <intrin.h>, not in <immintrin.h>. <intrin.h> holds declarations only, so it adds
// no code to this unit.
#include <intrin.h>
#include <cstdint>
#include <cstddef>

namespace rq::engine {

namespace {

/** Words per XMM vector: lane 0 is the word at the lower address. */
constexpr std::size_t k_lanes = simd<isa_sse2>::lanes;
/** Bytes per XMM vector, and the alignment MOVDQA and MOVNTDQ require. */
constexpr std::uintptr_t k_vector_bytes = sizeof(__m128i);

static_assert(k_lanes * sizeof(std::uint64_t) == k_vector_bytes,
              "an SSE2 vector holds exactly simd<isa_sse2>::lanes 64-bit words");

using sse2_cursor = pattern_cursor<isa_sse2>;

/* ---------------------------------------------------------------------------------------------
 * Range partitioning
 * ------------------------------------------------------------------------------------------- */

/**
 * A range split at 16-byte boundaries: head words, then aligned vectors, then tail words. All
 * counts are in words except vectors. Ranges are 8-byte aligned, so head and tail are 0 or 1.
 */
struct range_parts {
    /** Words before the first 16-byte boundary: 1 when the range starts at 8 mod 16. */
    std::size_t head;
    /** Aligned two-word vectors, the first at word offset head. */
    std::size_t vectors;
    /** Words after the last vector. */
    std::size_t tail;
};

/** Splits [p, p + words); p must be 8-byte aligned (the kernels.h contract). */
range_parts split_range(const std::uint64_t* p, std::size_t words) noexcept {
    const bool starts_mid_vector =
        (reinterpret_cast<std::uintptr_t>(p) & (k_vector_bytes - 1)) != 0;
    const std::size_t head = starts_mid_vector && words != 0 ? 1 : 0;
    const std::size_t body = words - head;
    return range_parts{head, body / k_lanes, body % k_lanes};
}

/**
 * Word offset of lane 0 of the first vector a traversal visits: the lowest vector when
 * ascending, the highest when descending. A pattern_cursor starts there, because a descending
 * cursor moves down from the highest block.
 */
std::size_t first_vector_offset(const range_parts& parts, int direction) noexcept {
    if (direction < 0 && parts.vectors != 0) {
        return parts.head + (parts.vectors - 1) * k_lanes;
    }
    return parts.head;
}

/** The pattern_cursor direction for a kernel direction: any negative value is descending. */
int cursor_direction(int direction) noexcept {
    return direction < 0 ? -1 : 1;
}

/**
 * Visits the parts of a range in traversal order. word_op(i) handles the scalar word at word
 * offset i and vector_op(i) the aligned vector whose lane 0 is at word offset i. Ascending:
 * head, vectors from lowest to highest, tail. Descending: tail, vectors from highest to lowest,
 * head. Every word of the range is visited exactly once, so sink calls follow address order.
 */
template <class WordOp, class VectorOp>
void traverse(const range_parts& parts, int direction, const WordOp& word_op,
              const VectorOp& vector_op) noexcept {
    const std::size_t tail_offset = parts.head + parts.vectors * k_lanes;
    if (direction < 0) {
        if (parts.tail != 0) {
            word_op(tail_offset);
        }
        for (std::size_t v = parts.vectors; v-- != 0;) {
            vector_op(parts.head + v * k_lanes);
        }
        if (parts.head != 0) {
            word_op(0);
        }
    } else {
        if (parts.head != 0) {
            word_op(0);
        }
        for (std::size_t v = 0; v != parts.vectors; ++v) {
            vector_op(parts.head + v * k_lanes);
        }
        if (parts.tail != 0) {
            word_op(tail_offset);
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Loads, stores and comparison
 * ------------------------------------------------------------------------------------------- */

/** Ordinary aligned 16-byte load (MOVDQA); never MOVNTDQA, inert on write-back memory. */
__m128i load_vector(const std::uint64_t* p) noexcept {
    return _mm_load_si128(reinterpret_cast<const __m128i*>(p));
}

/**
 * Aligned 16-byte streaming store (MOVNTDQ). It goes to a write-combining buffer without
 * allocating a cache line and is weakly ordered; the caller fences before any flush or
 * verification of the range.
 */
void stream_vector(std::uint64_t* p, __m128i value) noexcept {
    _mm_stream_si128(reinterpret_cast<__m128i*>(p), value);
}

/** 8-byte streaming store (MOVNTI) for an edge word that no aligned vector covers. */
void stream_word(std::uint64_t* p, std::uint64_t value) noexcept {
    _mm_stream_si64(reinterpret_cast<long long*>(p), static_cast<long long>(value));
}

/**
 * True when both 64-bit lanes are equal. SSE2 has no 64-bit compare (PCMPEQQ is SSE4.1), but
 * two words are equal exactly when both of their 32-bit halves are, so the vector matches when
 * PCMPEQD sets all sixteen byte-mask bits.
 */
bool all_lanes_equal(__m128i a, __m128i b) noexcept {
    return _mm_movemask_epi8(_mm_cmpeq_epi32(a, b)) == 0xFFFF;
}

/**
 * Reports each mismatching lane of one vector to the sink in traversal order (descending: the
 * higher lane first) and returns how many there were. The values come from the registers the
 * comparison used, spilled to local arrays: the word is never loaded again for `actual`,
 * because a transient fault need not repeat and the sink's re-read is the deliberate second
 * read.
 */
std::uint64_t report_lanes(const std::uint64_t* address, __m128i actual, __m128i expected,
                           int direction, mismatch_sink& sink) noexcept {
    alignas(16) std::uint64_t actual_lanes[k_lanes];
    alignas(16) std::uint64_t expected_lanes[k_lanes];
    _mm_store_si128(reinterpret_cast<__m128i*>(actual_lanes), actual);
    _mm_store_si128(reinterpret_cast<__m128i*>(expected_lanes), expected);
    std::uint64_t mismatches = 0;
    for (std::size_t n = 0; n != k_lanes; ++n) {
        const std::size_t lane = direction < 0 ? k_lanes - 1 - n : n;
        if (actual_lanes[lane] != expected_lanes[lane]) {
            sink.on_mismatch(address + lane, expected_lanes[lane], actual_lanes[lane]);
            ++mismatches;
        }
    }
    return mismatches;
}

/* ---------------------------------------------------------------------------------------------
 * SSE2 kernels (contracts in kernels.h). None fences, checks the stop flag or counts coverage.
 * ------------------------------------------------------------------------------------------- */

/** fill_stream_fn: MOVNTDQ for aligned vectors, MOVNTI for edge words. No fence. */
void fill_stream_sse2(std::uint64_t* dst, std::size_t words, const pattern_spec& spec,
                      std::uint64_t first_word_index, int direction) noexcept {
    if (words == 0) {
        return;
    }
    const range_parts parts = split_range(dst, words);
    sse2_cursor cursor(spec, first_word_index + first_vector_offset(parts, direction),
                       cursor_direction(direction));
    traverse(
        parts, direction,
        [&](std::size_t i) {
            stream_word(dst + i, pattern_word<isa_sse2>(spec, first_word_index + i));
        },
        [&](std::size_t i) {
            stream_vector(dst + i, cursor.current());
            cursor.advance();
        });
}

/** verify_fn: ordinary loads compared with the regenerated pattern. */
std::uint64_t verify_sse2(const std::uint64_t* src, std::size_t words, const pattern_spec& spec,
                          std::uint64_t first_word_index, int direction,
                          mismatch_sink& sink) noexcept {
    if (words == 0) {
        return 0;
    }
    const range_parts parts = split_range(src, words);
    sse2_cursor cursor(spec, first_word_index + first_vector_offset(parts, direction),
                       cursor_direction(direction));
    std::uint64_t mismatches = 0;
    traverse(
        parts, direction,
        [&](std::size_t i) {
            const std::uint64_t expected = pattern_word<isa_sse2>(spec, first_word_index + i);
            const std::uint64_t actual = src[i];
            if (actual != expected) [[unlikely]] {
                sink.on_mismatch(src + i, expected, actual);
                ++mismatches;
            }
        },
        [&](std::size_t i) {
            const __m128i expected = cursor.current();
            const __m128i actual = load_vector(src + i);
            if (!all_lanes_equal(actual, expected)) [[unlikely]] {
                mismatches += report_lanes(src + i, actual, expected, direction, sink);
            }
            cursor.advance();
        });
    return mismatches;
}

/**
 * verify_and_stream_fn: per word or vector, load, compare with `expected`, report every
 * mismatch, and only then stream the `next` value to the same address, so a re-read the sink
 * performs still sees the wrong value. No fence.
 */
std::uint64_t verify_and_stream_sse2(std::uint64_t* p, std::size_t words,
                                     const pattern_spec& expected, const pattern_spec& next,
                                     std::uint64_t first_word_index, int direction,
                                     mismatch_sink& sink) noexcept {
    if (words == 0) {
        return 0;
    }
    const range_parts parts = split_range(p, words);
    const std::uint64_t first_block = first_word_index + first_vector_offset(parts, direction);
    sse2_cursor expected_cursor(expected, first_block, cursor_direction(direction));
    sse2_cursor next_cursor(next, first_block, cursor_direction(direction));
    std::uint64_t mismatches = 0;
    traverse(
        parts, direction,
        [&](std::size_t i) {
            const std::uint64_t word_index = first_word_index + i;
            const std::uint64_t want = pattern_word<isa_sse2>(expected, word_index);
            const std::uint64_t actual = p[i];
            if (actual != want) [[unlikely]] {
                sink.on_mismatch(p + i, want, actual);
                ++mismatches;
            }
            stream_word(p + i, pattern_word<isa_sse2>(next, word_index));
        },
        [&](std::size_t i) {
            const __m128i want = expected_cursor.current();
            const __m128i actual = load_vector(p + i);
            if (!all_lanes_equal(actual, want)) [[unlikely]] {
                mismatches += report_lanes(p + i, actual, want, direction, sink);
            }
            stream_vector(p + i, next_cursor.current());
            expected_cursor.advance();
            next_cursor.advance();
        });
    return mismatches;
}

/* ---------------------------------------------------------------------------------------------
 * Line flush shared by both flush_range helpers
 * ------------------------------------------------------------------------------------------- */

/**
 * Flushes every 64-byte line overlapping [p, p + bytes) with CLFLUSHOPT (UseClflushopt) or
 * CLFLUSH. No fence: both are ordered by the caller's MFENCE on each side.
 */
template <bool UseClflushopt>
void flush_lines(const void* p, std::size_t bytes) noexcept {
    if (bytes == 0) {
        return;
    }
    const std::uintptr_t begin = reinterpret_cast<std::uintptr_t>(p);
    const std::uintptr_t end = begin + bytes;
    for (std::uintptr_t line = begin & ~std::uintptr_t{k_cache_line_bytes - 1}; line < end;
         line += k_cache_line_bytes) {
        if constexpr (UseClflushopt) {
            _mm_clflushopt(reinterpret_cast<const void*>(line));
        } else {
            _mm_clflush(reinterpret_cast<const void*>(line));
        }
    }
}

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * Baseline fence, flush and re-read helpers (declared in kernels.h)
 * ------------------------------------------------------------------------------------------- */

/**
 * MFENCE orders every earlier load, store, streaming store and CLFLUSHOPT before every later
 * load or store. SFENCE would order stores only, so a verification load could pass a flush
 * still in flight.
 */
void full_fence_mfence() noexcept {
    _mm_mfence();
}

/**
 * CLFLUSHOPT writes the line back and invalidates it at every cache level, whatever its prior
 * state. It is weakly ordered, even against other flushes, which lets the lines of a range
 * drain in parallel; the caller's MFENCE orders it. Requires CPUID.7.0:EBX bit 23.
 */
void flush_range_clflushopt(const void* p, std::size_t bytes) noexcept {
    flush_lines<true>(p, bytes);
}

/**
 * CLFLUSH has the same write-back-and-invalidate effect and is architectural on x64, so it
 * serves CPUs whose CPUID.7.0:EBX bit 23 is clear. It is ordered against other CLFLUSHes and
 * against writes but not against later loads, so the caller fences on both sides, as for
 * CLFLUSHOPT.
 */
void flush_range_clflush(const void* p, std::size_t bytes) noexcept {
    flush_lines<false>(p, bytes);
}

/**
 * The re-read rule: the first MFENCE makes earlier stores, streaming ones included, globally
 * visible before the flush; the flush removes any cached copy of the line; the second MFENCE
 * orders the flush before the load. The volatile load cannot be elided, merged or hoisted
 * above the fences, so the value comes from beyond the CPU caches.
 */
std::uint64_t reread_clflushopt(const std::uint64_t* p) noexcept {
    _mm_mfence();
    _mm_clflushopt(p);
    _mm_mfence();
    return *static_cast<const volatile std::uint64_t*>(p);
}

/** reread_clflushopt with CLFLUSH, for CPUs without CLFLUSHOPT. */
std::uint64_t reread_clflush(const std::uint64_t* p) noexcept {
    _mm_mfence();
    _mm_clflush(p);
    _mm_mfence();
    return *static_cast<const volatile std::uint64_t*>(p);
}

/*
 * The raw table's flush_range and reread entries use CLFLUSHOPT. tables_for (isa_dispatch.cpp)
 * copies this table and substitutes flush_range_clflush and reread_clflush where CPUID reports
 * no CLFLUSHOPT, so consumers and tests obtain tables through tables_for rather than calling
 * these entries directly. Every initialiser is a function address, a constant expression, so
 * constinit guarantees static initialisation and no initialiser runs at DLL load.
 */
constinit const kernel_table k_kernels_sse2 = {
    .isa = RQ_ISA_SSE2,
    .fill_stream = &fill_stream_sse2,
    .full_fence = &full_fence_mfence,
    .flush_range = &flush_range_clflushopt,
    .verify = &verify_sse2,
    .verify_and_stream = &verify_and_stream_sse2,
    .reread = &reread_clflushopt,
};

}  // namespace rq::engine
