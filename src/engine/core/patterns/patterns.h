/**
 * @file patterns.h
 * @brief Data-pattern generators: the scalar reference formulas and an ISA-tagged vector cursor.
 *
 * Every value is a stateless function of a pattern_spec and a logical word index, so writers,
 * verifiers and re-reads regenerate the expected value of any word instead of storing it.
 *
 * One header serves translation units compiled at three instruction-set levels: the SSE2
 * baseline (patterns.cpp, kernels_sse2.cpp and the rest of engine_core), /arch:AVX2
 * (kernels_avx2.cpp) and /arch:AVX512 (kernels_avx512.cpp). An inline function with external
 * linkage that an AVX unit compiles becomes a COMDAT holding AVX instructions, and the linker
 * may keep that copy for baseline callers too; on a CPU without the extension the result is an
 * illegal-instruction fault, which terminates the process. Hence the rules this header keeps:
 * - every function is a template whose arguments include an ISA tag, so each unit instantiates
 *   its own distinctly mangled symbols: pattern_word<isa_avx2> is not pattern_word<isa_sse2>;
 * - the one untagged function, expected_value, is only declared here and is defined out of
 *   line in patterns.cpp at the baseline;
 * - pattern_spec and the tags have only trivial special members, so no constructor is emitted;
 * - namespace-scope constants are constexpr scalars and no SIMD object lives at namespace
 *   scope, so no unit gets a dynamic initialiser;
 * - <cstdint> and <cstddef> are used for their types only, and no other standard header is
 *   included: its inline functions would be compiled at each unit's level under shared names.
 *
 * Threading: every function is pure and may be called from any thread. A pattern_cursor holds
 * per-sweep state and is used by one worker at a time.
 */
#pragma once

#include <immintrin.h>
#include <cstdint>
#include <cstddef>

#include "ramquick_engine.h"

namespace rq::engine {

/* ---------------------------------------------------------------------------------------------
 * Instruction-set tags
 * ------------------------------------------------------------------------------------------- */

/**
 * @name Instruction-set tags
 * A tag selects the vector type and code path of every generator and is part of every
 * generator's mangled name. Each kernel unit instantiates the generators only with its own tag:
 * kernels_sse2.cpp with isa_sse2, kernels_avx2.cpp with isa_avx2 and kernels_avx512.cpp with
 * isa_avx512. patterns.cpp and other baseline code that works one word at a time use
 * isa_scalar.
 * @{
 */
/** Plain 64-bit integer code: one word per value. */
struct isa_scalar {};
/** SSE2, the x64 baseline: two words per vector. */
struct isa_sse2 {};
/** AVX2: four words per vector. */
struct isa_avx2 {};
/** AVX-512 with F, CD, BW, DQ and VL, all of which dispatch requires: eight words per vector. */
struct isa_avx512 {};
/** @} */

/**
 * Vector type and lane count of an ISA tag. Each lane holds one 64-bit word, and lane 0 is the
 * word at the lowest address. Specialisations hold only a type alias and a constant, never a
 * function, so they emit no code.
 */
template <class Isa>
struct simd;

/** isa_scalar: one word in a plain integer. */
template <>
struct simd<isa_scalar> {
    /** Value type. */
    using vec = std::uint64_t;
    /** Words per value. */
    static constexpr std::size_t lanes = 1;
};

/** isa_sse2: two words in an XMM register. */
template <>
struct simd<isa_sse2> {
    /** Vector type. */
    using vec = __m128i;
    /** Words per vector. */
    static constexpr std::size_t lanes = 2;
};

/** isa_avx2: four words in a YMM register. */
template <>
struct simd<isa_avx2> {
    /** Vector type. */
    using vec = __m256i;
    /** Words per vector. */
    static constexpr std::size_t lanes = 4;
};

/** isa_avx512: eight words in a ZMM register. */
template <>
struct simd<isa_avx512> {
    /** Vector type. */
    using vec = __m512i;
    /** Words per vector. */
    static constexpr std::size_t lanes = 8;
};

/* ---------------------------------------------------------------------------------------------
 * Pattern selection and constants
 * ------------------------------------------------------------------------------------------- */

/**
 * One pattern instance: which pattern, for which seed and pass, and whether inverted.
 *
 * An aggregate with only trivial special members. It deliberately has no default member
 * initialisers, which would make its default constructor non-trivial and emit it as an inline
 * function in every unit (see the file comment), so initialise every field, for example
 * pattern_spec{RQ_PATTERN_RANDOM, seed, pass, false}.
 */
struct pattern_spec {
    /** RQ_PATTERN_* value. core/session.cpp has validated its range before any step runs. */
    std::uint32_t kind;
    /**
     * The step's pattern seed, step_pattern_seed(rq_session_config.seed, step_index) from
     * core/runner/step_runner.h, as the step runner passes it through every IMemoryAccess call.
     * The step index is mixed in so that steps sharing a pattern do not write identical data.
     * Only RQ_PATTERN_RANDOM and RQ_PATTERN_MOVING_INVERSION_RANDOM use it. It is not the
     * access-order seed: the RandomBlocks permutation is keyed by the raw session seed. So
     * regenerating a word from a saved session seed needs the step's seed derived first.
     */
    std::uint64_t seed;
    /** The step's pass index, from 0. Selects the per-pass inversion and the random stream. */
    std::uint32_t pass;
    /**
     * Inverts every value. The moving-inversion sweeps set it for the complement they write in
     * the ascending sweep and verify in the descending one.
     */
    bool complement;
};

/**
 * SplitMix64 increment: 2^64 divided by the golden ratio, made odd. An odd step visits every
 * 64-bit counter value once, so distinct words never share a generator input.
 */
constexpr std::uint64_t k_splitmix_gamma = 0x9E3779B97F4A7C15ull;
/** First SplitMix64 finalizer multiplier (Stafford's Mix13), as published with SplitMix64. */
constexpr std::uint64_t k_splitmix_mul1 = 0xBF58476D1CE4E5B9ull;
/** Second SplitMix64 finalizer multiplier; with the xor-shifts it spreads every input bit. */
constexpr std::uint64_t k_splitmix_mul2 = 0x94D049BB133111EBull;
/** Checkerboard word of even parity, 0101...: every bit differs from both neighbours. */
constexpr std::uint64_t k_checker_even = 0x5555555555555555ull;
/** Checkerboard word of odd parity, 1010...: the complement, so adjacent words also differ. */
constexpr std::uint64_t k_checker_odd = 0xAAAAAAAAAAAAAAAAull;
/** High-toggle word of even parity: the 32-bit halves are complements of each other. */
constexpr std::uint64_t k_toggle_even = 0x00000000FFFFFFFFull;
/** High-toggle word of odd parity: k_toggle_even's complement, so adjacent words differ fully. */
constexpr std::uint64_t k_toggle_odd = 0xFFFFFFFF00000000ull;
/** Every bit set: the XOR mask that forms complements and walking zeros. */
constexpr std::uint64_t k_all_ones = ~0ull;

namespace detail {

/** True when A and B are the same tag. A local is_same, because <type_traits> is excluded. */
template <class A, class B>
constexpr bool same_isa_v = false;
/** The matching case of same_isa_v. */
template <class A>
constexpr bool same_isa_v<A, A> = true;

/** True for the four instruction-set tags. */
template <class Isa>
constexpr bool is_isa_tag_v = same_isa_v<Isa, isa_scalar> || same_isa_v<Isa, isa_sse2> ||
                              same_isa_v<Isa, isa_avx2> || same_isa_v<Isa, isa_avx512>;

}  // namespace detail

/* ---------------------------------------------------------------------------------------------
 * Scalar reference formulas
 *
 * These are logical values. Burst ordering, ECC and memory-controller data scrambling (Intel
 * documents scrambling for its 12th-generation Core processors, for example) decide what the DQ
 * lines actually carry, so no pattern guarantees a particular toggle rate on the bus. For the
 * same reason the checkerboard and high-toggle patterns are logical worst cases that stay
 * within the daily-use stress bound: where the controller scrambles, bus data is
 * pseudo-random, as ordinary workloads' data also is.
 *
 * w is the logical word index in the region under test: the logical byte offset divided by 8,
 * with the worker slices concatenated in worker order and each slice's chunks in order. It is
 * never a virtual address, so values do not depend on where Windows placed each chunk, and a
 * verifier or re-read regenerates the expected value from the offset alone.
 * ------------------------------------------------------------------------------------------- */

/**
 * SplitMix64 output function (Stafford's Mix13): a bijection on 64-bit values in which every
 * output bit depends on every input bit.
 */
template <class Isa>
constexpr std::uint64_t splitmix64_finalize(std::uint64_t z) noexcept {
    static_assert(detail::is_isa_tag_v<Isa>, "Isa must be one of the isa_* tags");
    z = (z ^ (z >> 30)) * k_splitmix_mul1;
    z = (z ^ (z >> 27)) * k_splitmix_mul2;
    return z ^ (z >> 31);
}

/**
 * Per-pass key of the random patterns: the SplitMix64 output for the seed at counter pass + 1,
 * so each pass draws its own stream and a pass's data never repeats the previous pass's.
 */
template <class Isa>
constexpr std::uint64_t pattern_pass_key(std::uint64_t seed, std::uint32_t pass) noexcept {
    return splitmix64_finalize<Isa>(seed + k_splitmix_gamma * (std::uint64_t{pass} + 1));
}

/**
 * Scalar reference value of word w. Every vector path reproduces it bit for bit.
 *
 * - RQ_PATTERN_RANDOM and RQ_PATTERN_MOVING_INVERSION_RANDOM: SplitMix64 over seed, word index
 *   and pass.
 * - RQ_PATTERN_CHECKERBOARD: 0x5555... and 0xAAAA... alternating per word and swapped every
 *   pass, so one step writes both the checkerboard and the inverted checkerboard.
 * - RQ_PATTERN_HIGH_TOGGLE: consecutive 64-bit words are complements, the 32-bit halves of each
 *   word are complements, and the assignment alternates per pass. It aims at maximum toggling
 *   on 64-bit (DDR4 channel) and 32-bit (DDR5 sub-channel) beats.
 * - RQ_PATTERN_MOVING_INVERSION_WALKING: one bit, rotating with the word index, set on even
 *   passes (walking ones) and cleared on odd passes (walking zeros), MemTest86-style.
 *
 * spec.complement inverts the result.
 */
template <class Isa>
constexpr std::uint64_t pattern_word(const pattern_spec& s, std::uint64_t w) noexcept {
    std::uint64_t base = 0;
    switch (s.kind) {
    case RQ_PATTERN_RANDOM:
    case RQ_PATTERN_MOVING_INVERSION_RANDOM:
        base = splitmix64_finalize<Isa>(pattern_pass_key<Isa>(s.seed, s.pass) +
                                        k_splitmix_gamma * (w + 1));
        break;
    case RQ_PATTERN_CHECKERBOARD:
        base = ((w ^ s.pass) & 1) != 0 ? k_checker_odd : k_checker_even;
        break;
    case RQ_PATTERN_HIGH_TOGGLE:
        base = ((w ^ s.pass) & 1) != 0 ? k_toggle_odd : k_toggle_even;
        break;
    case RQ_PATTERN_MOVING_INVERSION_WALKING: {
        const std::uint64_t bit = std::uint64_t{1} << (w & 63);
        base = (s.pass & 1) != 0 ? ~bit : bit;
        break;
    }
    default:
        // Unreachable: core/session.cpp rejects out-of-range kinds, and this header has no
        // assert. A constant keeps the scalar and vector paths equal even so.
        base = 0;
        break;
    }
    return s.complement ? ~base : base;
}

/* ---------------------------------------------------------------------------------------------
 * Lane-wise vector operations used by pattern_cursor. Each is a function template on the tag,
 * so an instantiation exists only in the units that compile that tag.
 * ------------------------------------------------------------------------------------------- */

namespace detail {

/** Vector type of an ISA tag. */
template <class Isa>
using vec_t = typename simd<Isa>::vec;

/** Every lane set to x. */
template <class Isa>
vec_t<Isa> vec_splat(std::uint64_t x) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return x;
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        return _mm_set1_epi64x(static_cast<long long>(x));
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        return _mm256_set1_epi64x(static_cast<long long>(x));
    } else {
        return _mm512_set1_epi64(static_cast<long long>(x));
    }
}

/** Lane k set to v[k], for every lane; v needs no particular alignment. */
template <class Isa>
vec_t<Isa> vec_load_lanes(const std::uint64_t* v) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return v[0];
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        return _mm_loadu_si128(reinterpret_cast<const __m128i*>(v));
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        return _mm256_loadu_si256(reinterpret_cast<const __m256i*>(v));
    } else {
        return _mm512_loadu_si512(v);
    }
}

/** Lane-wise a + b modulo 2^64. */
template <class Isa>
vec_t<Isa> vec_add(vec_t<Isa> a, vec_t<Isa> b) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return a + b;
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        return _mm_add_epi64(a, b);
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        return _mm256_add_epi64(a, b);
    } else {
        return _mm512_add_epi64(a, b);
    }
}

/** Lane-wise a ^ b. */
template <class Isa>
vec_t<Isa> vec_xor(vec_t<Isa> a, vec_t<Isa> b) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return a ^ b;
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        return _mm_xor_si128(a, b);
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        return _mm256_xor_si256(a, b);
    } else {
        return _mm512_xor_si512(a, b);
    }
}

/** Lane-wise logical right shift by Shift bits, 0 < Shift < 64. */
template <class Isa, int Shift>
vec_t<Isa> vec_srli(vec_t<Isa> a) noexcept {
    static_assert(Shift > 0 && Shift < 64, "shift count out of range");
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return a >> Shift;
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        return _mm_srli_epi64(a, Shift);
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        return _mm256_srli_epi64(a, Shift);
    } else {
        return _mm512_srli_epi64(a, static_cast<unsigned int>(Shift));
    }
}

/**
 * Lane-wise low 64 bits of a * b.
 *
 * SSE2 and AVX2 have no 64-bit multiply (vpmullq needs AVX-512DQ), so the product is built from
 * 32 x 32 -> 64-bit multiplies: lo(a)lo(b) + ((lo(a)hi(b) + hi(a)lo(b)) << 32). The hi(a)hi(b)
 * term lies wholly above bit 63. AVX-512 uses vpmullq directly: dispatch selects the AVX-512
 * kernels only when DQ is present.
 */
template <class Isa>
vec_t<Isa> vec_mullo(vec_t<Isa> a, std::uint64_t b) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return a * b;
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        // pmuludq reads only the low 32 bits of each lane, so the full splat of b serves as lo(b).
        const __m128i b_lo = _mm_set1_epi64x(static_cast<long long>(b));
        const __m128i b_hi = _mm_set1_epi64x(static_cast<long long>(b >> 32));
        const __m128i lo_lo = _mm_mul_epu32(a, b_lo);
        const __m128i cross =
            _mm_add_epi64(_mm_mul_epu32(a, b_hi), _mm_mul_epu32(_mm_srli_epi64(a, 32), b_lo));
        return _mm_add_epi64(lo_lo, _mm_slli_epi64(cross, 32));
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        const __m256i b_lo = _mm256_set1_epi64x(static_cast<long long>(b));
        const __m256i b_hi = _mm256_set1_epi64x(static_cast<long long>(b >> 32));
        const __m256i lo_lo = _mm256_mul_epu32(a, b_lo);
        const __m256i cross = _mm256_add_epi64(_mm256_mul_epu32(a, b_hi),
                                               _mm256_mul_epu32(_mm256_srli_epi64(a, 32), b_lo));
        return _mm256_add_epi64(lo_lo, _mm256_slli_epi64(cross, 32));
    } else {
        return _mm512_mullo_epi64(a, _mm512_set1_epi64(static_cast<long long>(b)));
    }
}

/** splitmix64_finalize applied to every lane. */
template <class Isa>
vec_t<Isa> splitmix64_finalize_vec(vec_t<Isa> z) noexcept {
    z = vec_mullo<Isa>(vec_xor<Isa>(z, vec_srli<Isa, 30>(z)), k_splitmix_mul1);
    z = vec_mullo<Isa>(vec_xor<Isa>(z, vec_srli<Isa, 27>(z)), k_splitmix_mul2);
    return vec_xor<Isa>(z, vec_srli<Isa, 31>(z));
}

/** Lane k set to 1 << ((w + k) & 63): the walking-ones base of the block whose lane 0 is w. */
template <class Isa>
vec_t<Isa> walking_bits(std::uint64_t w) noexcept {
    if constexpr (same_isa_v<Isa, isa_scalar>) {
        return std::uint64_t{1} << (w & 63);
    } else if constexpr (same_isa_v<Isa, isa_sse2>) {
        // SSE2 has no per-lane variable shift; two scalar shifts are exact and cheap.
        return _mm_set_epi64x(static_cast<long long>(std::uint64_t{1} << ((w + 1) & 63)),
                              static_cast<long long>(std::uint64_t{1} << (w & 63)));
    } else if constexpr (same_isa_v<Isa, isa_avx2>) {
        const __m256i index =
            _mm256_and_si256(_mm256_add_epi64(_mm256_set1_epi64x(static_cast<long long>(w)),
                                              _mm256_set_epi64x(3, 2, 1, 0)),
                             _mm256_set1_epi64x(63));
        return _mm256_sllv_epi64(_mm256_set1_epi64x(1), index);
    } else {
        const __m512i index =
            _mm512_and_si512(_mm512_add_epi64(_mm512_set1_epi64(static_cast<long long>(w)),
                                              _mm512_set_epi64(7, 6, 5, 4, 3, 2, 1, 0)),
                             _mm512_set1_epi64(63));
        return _mm512_sllv_epi64(_mm512_set1_epi64(1), index);
    }
}

}  // namespace detail

/* ---------------------------------------------------------------------------------------------
 * Vector generator
 * ------------------------------------------------------------------------------------------- */

/**
 * Generates a pattern one block at a time for a sweep through consecutive blocks.
 *
 * A block is simd<Isa>::lanes consecutive words starting at word_index(). current() returns
 * them in ascending memory order, lane k holding pattern_word<Isa>(spec, word_index() + k), so
 * a kernel writes the block with one aligned or streaming store at the address of that word, or
 * compares a loaded block against it. advance() moves one block up for an ascending sweep or
 * one block down for a descending sweep, which starts at the highest block.
 *
 * Each step is incremental: the random counters advance by one vector add, and the alternating
 * patterns keep their precomputed blocks. seek() jumps to any other block just as cheaply, so a
 * kernel that visits many short runs, such as the BankStride lines of a strided call, builds one
 * cursor per call and seeks per run. A kernel that needs single words, for an unaligned edge or
 * to re-check a mismatching block word by word, calls pattern_word with its own tag or
 * expected_value.
 *
 * Every member is a member of this class template, so its symbols carry the tag (see the file
 * comment). Not thread-safe: one worker uses a cursor at a time. Copying is trivial.
 */
template <class Isa>
class pattern_cursor {
    static_assert(detail::is_isa_tag_v<Isa>, "Isa must be one of the isa_* tags");

public:
    /** The tag's vector type: one 64-bit word per lane, lane 0 at the lowest address. */
    using vec = typename simd<Isa>::vec;

    /**
     * Positions the cursor on the block whose lane 0 is word block_first_word.
     *
     * @param spec Pattern instance. Only values derived from it are kept, so it need not
     *        outlive the cursor.
     * @param block_first_word Logical word index of lane 0 of the first block. Any value: it
     *        need not be a multiple of the lane count.
     * @param direction +1 to move up one block per advance(), -1 to move down. A negative value
     *        is treated as -1 and any other value as +1.
     */
    pattern_cursor(const pattern_spec& spec, std::uint64_t block_first_word, int direction) noexcept
        : counter_(detail::vec_splat<Isa>(0)), counter_origin_(detail::vec_splat<Isa>(0)),
          counter_step_(detail::vec_splat<Isa>(0)), flip_(detail::vec_splat<Isa>(flip_of(spec))),
          alt_current_(detail::vec_splat<Isa>(0)), alt_next_(detail::vec_splat<Isa>(0)),
          alt_even_(detail::vec_splat<Isa>(0)), alt_odd_(detail::vec_splat<Isa>(0)),
          w_(block_first_word),
          step_(direction < 0 ? std::uint64_t{0} - std::uint64_t{lanes} : std::uint64_t{lanes}),
          mode_(mode_of(spec.kind)) {
        std::uint64_t lane_values[lanes] = {};
        // The lane loops below stay scalar (loop(no_vector)). They run once per cursor, so
        // vectorising them gains nothing, and MSVC's auto-vectorizer would otherwise compile the
        // 64-bit multiply as an AVX-512 VPMULLQ path behind its own __isa_available check even in
        // the SSE2 and AVX2 units, putting instructions above each unit's level into its object.
        if (mode_ == mode::random) {
            const std::uint64_t key = pattern_pass_key<Isa>(spec.seed, spec.pass);
#pragma loop(no_vector)
            for (std::size_t k = 0; k < lanes; ++k) {
                lane_values[k] = key + k_splitmix_gamma * (k + 1);
            }
            counter_origin_ = detail::vec_load_lanes<Isa>(lane_values);
            counter_step_ = detail::vec_splat<Isa>(k_splitmix_gamma * step_);
        } else if (mode_ == mode::alternating) {
            // Every alternating value depends on its word index only through the index's parity
            // (pattern_word), so the blocks whose lane 0 is an even or an odd word are the only
            // two blocks there are.
#pragma loop(no_vector)
            for (std::size_t k = 0; k < lanes; ++k) {
                lane_values[k] = pattern_word<Isa>(spec, k);
            }
            alt_even_ = detail::vec_load_lanes<Isa>(lane_values);
#pragma loop(no_vector)
            for (std::size_t k = 0; k < lanes; ++k) {
                lane_values[k] = pattern_word<Isa>(spec, k + 1);
            }
            alt_odd_ = detail::vec_load_lanes<Isa>(lane_values);
        }
        seek(block_first_word);
    }

    /**
     * Repositions the cursor on the block whose lane 0 is word block_first_word, exactly as a
     * fresh construction with the same spec and direction would: current() and every later
     * advance() then match that cursor's. Costs one scalar multiply and one vector add, against
     * a construction's pattern_pass_key and lane loops, so a kernel visiting many short runs
     * builds one cursor and seeks once per run.
     *
     * @param block_first_word Logical word index of lane 0 of the new block. Any value.
     */
    void seek(std::uint64_t block_first_word) noexcept {
        w_ = block_first_word;
        // Unconditional, as in advance(): outside random mode counter_ is never read. Lane k's
        // input is pass key + gamma * (w + k + 1), the origin's lane plus gamma * w.
        counter_ = detail::vec_add<Isa>(
            counter_origin_, detail::vec_splat<Isa>(k_splitmix_gamma * block_first_word));
        const bool odd = (block_first_word & 1) != 0;
        alt_current_ = odd ? alt_odd_ : alt_even_;
        // The block lanes words on: of the same parity for an even lane count, of the other for
        // an odd one, which advance() swaps in.
        alt_next_ = odd != (lanes % 2 != 0) ? alt_odd_ : alt_even_;
    }

    /**
     * Values of the words [word_index(), word_index() + lanes) in ascending memory order:
     * lane k is pattern_word<Isa>(spec, word_index() + k).
     */
    vec current() const noexcept {
        if (mode_ == mode::random) {
            return detail::vec_xor<Isa>(detail::splitmix64_finalize_vec<Isa>(counter_), flip_);
        }
        if (mode_ == mode::walking) {
            return detail::vec_xor<Isa>(detail::walking_bits<Isa>(w_), flip_);
        }
        return alt_current_;
    }

    /** Moves to the next block of the sweep: word_index() changes by +lanes or -lanes, mod 2^64. */
    void advance() noexcept {
        w_ += step_;
        // Unconditional: one add costs less than a branch, and outside random mode counter_ is
        // never read.
        counter_ = detail::vec_add<Isa>(counter_, counter_step_);
        if constexpr (lanes % 2 != 0) {
            // With an odd lane count every step flips the parity of (word ^ pass). With an even
            // count the parity, and so alt_current_, never changes.
            const vec next = alt_next_;
            alt_next_ = alt_current_;
            alt_current_ = next;
        }
    }

    /** Logical word index of lane 0 of the current block. */
    std::uint64_t word_index() const noexcept { return w_; }

private:
    static constexpr std::size_t lanes = simd<Isa>::lanes;

    /** How current() derives a block. */
    enum class mode : std::uint32_t {
        /** SplitMix64 of a per-lane counter: RANDOM and MOVING_INVERSION_RANDOM. */
        random,
        /** Rotating single bit computed from the word index: MOVING_INVERSION_WALKING. */
        walking,
        /**
         * Two precomputed blocks chosen by word parity: CHECKERBOARD, HIGH_TOGGLE and, for an
         * out-of-range kind, the constant pattern_word returns.
         */
        alternating,
    };

    static constexpr mode mode_of(std::uint32_t kind) noexcept {
        if (kind == RQ_PATTERN_RANDOM || kind == RQ_PATTERN_MOVING_INVERSION_RANDOM) {
            return mode::random;
        }
        if (kind == RQ_PATTERN_MOVING_INVERSION_WALKING) {
            return mode::walking;
        }
        return mode::alternating;
    }

    /** XOR applied to computed blocks: the complement, and walking zeros on odd passes. */
    static constexpr std::uint64_t flip_of(const pattern_spec& spec) noexcept {
        std::uint64_t flip = spec.complement ? k_all_ones : 0;
        if (spec.kind == RQ_PATTERN_MOVING_INVERSION_WALKING && (spec.pass & 1) != 0) {
            flip = ~flip;
        }
        return flip;
    }

    // Vectors first, so their alignment needs no padding between members.
    /** Random mode: SplitMix64 input of each lane, pass key + gamma * (word + 1). */
    vec counter_;
    /** Random mode: counter_ at word 0, pass key + gamma * (k + 1), which seek() offsets. */
    vec counter_origin_;
    /** Random mode: added to counter_ by advance(), gamma times the signed word step. */
    vec counter_step_;
    /** Random and walking modes: XOR applied to the computed block. */
    vec flip_;
    /** Alternating mode: the current block's values, complement applied. */
    vec alt_current_;
    /** Alternating mode: the block lanes words on, swapped in by advance() for odd lanes. */
    vec alt_next_;
    /** Alternating mode: the block whose lane 0 is an even word; seek() selects from the two. */
    vec alt_even_;
    /** Alternating mode: the block whose lane 0 is an odd word. */
    vec alt_odd_;
    /** Logical word index of lane 0. */
    std::uint64_t w_;
    /** Added to w_ by advance(): +lanes or -lanes modulo 2^64. */
    std::uint64_t step_;
    /** How current() derives a block. */
    mode mode_;
};

/* ---------------------------------------------------------------------------------------------
 * Out-of-line reference
 * ------------------------------------------------------------------------------------------- */

/**
 * Expected value of one word: pattern_word<isa_scalar>(spec, word_index), defined out of line
 * in patterns.cpp at the SSE2 baseline.
 *
 * The ground truth the vector generators must match. Used for re-read expectations, by
 * tests/engine.tests/fakes/fault_injecting_memory_access.cpp and by the tests. Callable from
 * every unit, the AVX ones included, because its code is baseline code.
 */
std::uint64_t expected_value(const pattern_spec& spec, std::uint64_t word_index) noexcept;

}  // namespace rq::engine
