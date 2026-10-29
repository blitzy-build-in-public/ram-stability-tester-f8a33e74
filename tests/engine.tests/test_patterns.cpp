/**
 * @file test_patterns.cpp
 * @brief Pattern generator tests: the formulas core/patterns/patterns.h documents, the property
 *        each pattern exists for, and agreement of pattern_word and pattern_cursor with the
 *        out-of-line reference expected_value.
 *
 * Pure computation: no memory is locked, no thread is started and nothing depends on the CPU,
 * so no test is ever skipped. Only the isa_scalar instantiations are used: patterns.h assigns
 * each vector tag to its own kernel unit, and an AVX tag instantiated in this baseline unit could
 * leave AVX code where baseline callers reach it. test_kernels.cpp checks the SSE2, AVX2 and
 * AVX-512 generators against expected_value.
 *
 * Every pattern assertion names the kind, seed, pass, complement flag and word index, so a
 * failure identifies the exact word that is wrong; the SplitMix64 building-block assertions name
 * their input.
 */
#include <gtest/gtest.h>
#include <bit>
#include <cstdint>
#include <vector>

#include "ramquick_engine.h"
#include "core/patterns/patterns.h"

namespace rq::engine::test {
namespace {

// The cursor tests below step one word per advance(), which holds only for one lane per value.
static_assert(simd<isa_scalar>::lanes == 1, "the scalar cursor must hold one word per block");

/** The five RQ_PATTERN_* kinds. */
constexpr std::uint32_t k_all_kinds[] = {
    RQ_PATTERN_CHECKERBOARD, RQ_PATTERN_MOVING_INVERSION_WALKING,
    RQ_PATTERN_MOVING_INVERSION_RANDOM, RQ_PATTERN_RANDOM, RQ_PATTERN_HIGH_TOGGLE};

/**
 * The kinds that ignore the seed and whose consecutive passes are exact complements: the
 * inverted checkerboard, the per-pass high-toggle alternation and walking ones then zeros.
 */
constexpr std::uint32_t k_alternating_kinds[] = {
    RQ_PATTERN_CHECKERBOARD, RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_PATTERN_HIGH_TOGGLE};

constexpr std::uint64_t k_seeds[] = {0ULL, 1ULL, 0x9E3779B97F4A7C15ULL};
constexpr std::uint32_t k_passes[] = {0U, 1U, 2U, 3U};

/**
 * Word indices at the 8-word cache-line and 64-word rotation boundaries, and far into a large
 * region (2^37 + 5 words is beyond 1 TiB), where a 32-bit truncation of the index would show.
 */
constexpr std::uint64_t k_words[] = {0ULL,  1ULL,  7ULL,        8ULL,
                                     63ULL, 64ULL, 1ULL << 20U, (1ULL << 37U) + 5ULL};

/** Passes over which the per-pass alternation is checked: three full even/odd cycles. */
constexpr std::uint32_t k_alternation_passes = 6U;

/** The SplitMix64 increment as published, restated here so the header constant is checked. */
constexpr std::uint64_t k_published_gamma = 0x9E3779B97F4A7C15ULL;

/** Name of a kind for failure messages and parameterised test names (alphanumeric only). */
const char* kind_name(std::uint32_t kind) noexcept {
    switch (kind) {
    case RQ_PATTERN_CHECKERBOARD:
        return "Checkerboard";
    case RQ_PATTERN_MOVING_INVERSION_WALKING:
        return "MovingInversionWalking";
    case RQ_PATTERN_MOVING_INVERSION_RANDOM:
        return "MovingInversionRandom";
    case RQ_PATTERN_RANDOM:
        return "Random";
    case RQ_PATTERN_HIGH_TOGGLE:
        return "HighToggle";
    default:
        return "OutOfRange";
    }
}

/** Test-name generator for the suites parameterised on a kind. */
std::string kind_test_name(const ::testing::TestParamInfo<std::uint32_t>& info) {
    return kind_name(info.param);
}

pattern_spec make_spec(std::uint32_t kind, std::uint64_t seed, std::uint32_t pass,
                       bool complement = false) noexcept {
    return pattern_spec{kind, seed, pass, complement};
}

/** expected_value of one word of an uncomplemented pattern instance. */
std::uint64_t value(std::uint32_t kind, std::uint64_t seed, std::uint32_t pass,
                    std::uint64_t word) noexcept {
    return expected_value(make_spec(kind, seed, pass), word);
}

/** Failure context naming the pattern instance and the word. */
::testing::Message where(const pattern_spec& spec, std::uint64_t word) {
    ::testing::Message message;
    message << "kind " << kind_name(spec.kind) << " (" << spec.kind << "), seed " << spec.seed
            << ", pass " << spec.pass << ", complement " << (spec.complement ? "on" : "off")
            << ", word " << word;
    return message;
}

/**
 * Words 0 to 511, then 64-word runs from 2^20 and from 2^37 + 5, so word parity and the 64-word
 * rotation are seen both near the region's start and far into it.
 */
std::vector<std::uint64_t> sampled_words() {
    std::vector<std::uint64_t> words;
    for (std::uint64_t word = 0; word < 512; ++word) {
        words.push_back(word);
    }
    for (const std::uint64_t run_start : {1ULL << 20U, (1ULL << 37U) + 5ULL}) {
        for (std::uint64_t word = run_start; word < run_start + 64; ++word) {
            words.push_back(word);
        }
    }
    return words;
}

/** SplitMix64's output function (Stafford's Mix13), restated from the published constants. */
std::uint64_t reference_mix13(std::uint64_t z) noexcept {
    z = (z ^ (z >> 30U)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27U)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31U);
}

/**
 * A 64-bit LCG with Knuth's MMIX constants that supplies sampled test inputs. Deliberately not
 * SplitMix64, so the inputs of the random-pattern tests do not come from the generator under
 * test. Its full period makes every output within one test distinct.
 */
class sample_source {
public:
    explicit sample_source(std::uint64_t state) noexcept : state_(state) {}

    std::uint64_t next() noexcept {
        state_ = state_ * 6364136223846793005ULL + 1442695040888963407ULL;
        return state_;
    }

private:
    std::uint64_t state_;
};

/** One sampled random-pattern input. */
struct sample {
    std::uint64_t seed;
    std::uint64_t word;
    std::uint32_t pass;
};

/**
 * count distinct inputs: any 64-bit seed, a word below 2^38 (a 2 TiB region) and a pass below
 * 1024. Word and pass take the LCG's high bits, its strongest.
 */
std::vector<sample> sample_inputs(std::size_t count, std::uint64_t source_seed) {
    sample_source source(source_seed);
    std::vector<sample> samples;
    samples.reserve(count);
    for (std::size_t i = 0; i < count; ++i) {
        const std::uint64_t seed = source.next();
        const std::uint64_t word = source.next() >> 26U;
        const auto pass = static_cast<std::uint32_t>(source.next() >> 54U);
        samples.push_back(sample{seed, word, pass});
    }
    return samples;
}

/**
 * Sweeps a scalar cursor `steps` words from `start` in `direction` and checks word_index() and
 * current() against expected_value at every step. Stops at the first mismatch, so a broken
 * generator reports one failure per sweep rather than thousands.
 */
void expect_cursor_sweep(const pattern_spec& spec, std::uint64_t start, int direction,
                         std::uint64_t steps) {
    // patterns.h: any negative direction moves down one block, any other value up one block,
    // modulo 2^64.
    const std::uint64_t word_step = direction < 0 ? ~0ULL : 1ULL;
    pattern_cursor<isa_scalar> cursor(spec, start, direction);
    std::uint64_t word = start;
    for (std::uint64_t step = 0; step < steps; ++step) {
        const std::uint64_t expected = expected_value(spec, word);
        if (cursor.word_index() != word || cursor.current() != expected) {
            EXPECT_EQ(cursor.word_index(), word)
                << where(spec, word) << ", direction " << direction << ", step " << step;
            EXPECT_EQ(cursor.current(), expected)
                << where(spec, word) << ", direction " << direction << ", step " << step;
            return;
        }
        cursor.advance();
        word += word_step;
    }
}

/** Fixtures of the suites parameterised on a kind: all five, and the three alternating kinds. */
class PatternsEveryKind : public ::testing::TestWithParam<std::uint32_t> {};
class PatternsPassSensitivity : public ::testing::TestWithParam<std::uint32_t> {};

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * Every kind: determinism, cursor agreement and the complement flag
 * ------------------------------------------------------------------------------------------- */

TEST_P(PatternsEveryKind, ReferenceIsDeterministicAndEqualsPatternWord) {
    const std::uint32_t kind = GetParam();
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            for (const bool complement : {false, true}) {
                const pattern_spec spec = make_spec(kind, seed, pass, complement);
                for (const std::uint64_t word : k_words) {
                    const std::uint64_t first = expected_value(spec, word);
                    EXPECT_EQ(expected_value(spec, word), first) << where(spec, word);
                    EXPECT_EQ(pattern_word<isa_scalar>(spec, word), first) << where(spec, word);
                }
            }
        }
    }
}

TEST_P(PatternsEveryKind, ScalarCursorFollowsReferenceAscendingAndDescending) {
    constexpr std::uint64_t sweep_words = 4096;
    // A descending sweep from 0 wraps modulo 2^64; one from 4095 ends exactly at word 0.
    constexpr std::uint64_t starts[] = {0ULL, 4095ULL, (1ULL << 37U) + 5ULL};
    const std::uint32_t kind = GetParam();
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            for (const bool complement : {false, true}) {
                const pattern_spec spec = make_spec(kind, seed, pass, complement);
                for (const std::uint64_t start : starts) {
                    expect_cursor_sweep(spec, start, 1, sweep_words);
                    expect_cursor_sweep(spec, start, -1, sweep_words);
                }
            }
        }
    }
}

TEST_P(PatternsEveryKind, CursorDirectionIsNormalisedToOneWordUpOrDown) {
    constexpr int directions[] = {0, 2, 1000, -2, -1000};
    const std::uint32_t kind = GetParam();
    for (const std::uint32_t pass : {0U, 1U}) {
        const pattern_spec spec = make_spec(kind, 1ULL, pass);
        for (const int direction : directions) {
            expect_cursor_sweep(spec, 64ULL, direction, 64ULL);
        }
    }
}

TEST_P(PatternsEveryKind, ComplementFlagInvertsEveryBit) {
    const std::uint32_t kind = GetParam();
    for (const std::uint64_t seed : k_seeds) {
        for (std::uint32_t pass = 0; pass < k_alternation_passes; ++pass) {
            const pattern_spec plain = make_spec(kind, seed, pass, false);
            const pattern_spec inverted = make_spec(kind, seed, pass, true);
            for (const std::uint64_t word : k_words) {
                EXPECT_EQ(expected_value(inverted, word), ~expected_value(plain, word))
                    << where(inverted, word);
            }
        }
    }
}

// gtest names an instantiated suite "<prefix>/<suite>", so the prefix keeps the Patterns* filter
// selecting these tests.
INSTANTIATE_TEST_SUITE_P(PatternsAllKinds, PatternsEveryKind, ::testing::ValuesIn(k_all_kinds),
                         kind_test_name);

/* ---------------------------------------------------------------------------------------------
 * Checkerboard, high-toggle and walking bits: per-pass inversion and seed independence
 * ------------------------------------------------------------------------------------------- */

// Moving inversion over walking bits and the inverted checkerboard rely on each pass rewriting
// every bit of every word, so consecutive passes must be exact complements, not merely differ.
TEST_P(PatternsPassSensitivity, ConsecutivePassesAreExactComplements) {
    const std::uint32_t kind = GetParam();
    const std::vector<std::uint64_t> words = sampled_words();
    for (std::uint32_t pass = 0; pass < k_alternation_passes; ++pass) {
        const pattern_spec spec = make_spec(kind, 0ULL, pass);
        for (const std::uint64_t word : words) {
            EXPECT_EQ(value(kind, 0ULL, pass + 1U, word), ~expected_value(spec, word))
                << where(spec, word) << ", compared with pass " << pass + 1U;
        }
    }
}

TEST_P(PatternsPassSensitivity, SeedDoesNotChangeValues) {
    const std::uint32_t kind = GetParam();
    const std::vector<std::uint64_t> words = sampled_words();
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            const pattern_spec spec = make_spec(kind, seed, pass);
            for (const std::uint64_t word : words) {
                EXPECT_EQ(expected_value(spec, word), value(kind, 0ULL, pass, word))
                    << where(spec, word) << ", compared with seed 0";
            }
        }
    }
}

INSTANTIATE_TEST_SUITE_P(PatternsAlternatingKinds, PatternsPassSensitivity,
                         ::testing::ValuesIn(k_alternating_kinds), kind_test_name);

/* ---------------------------------------------------------------------------------------------
 * Checkerboard
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsCheckerboard, AlternatesPerWordInTheDocumentedPhase) {
    // patterns.h: 0xAAAA... where (word ^ pass) is odd, otherwise 0x5555..., so pass 0 starts
    // with 0x5555... at word 0 and every pass swaps the two.
    const pattern_spec first_pass = make_spec(RQ_PATTERN_CHECKERBOARD, 0ULL, 0U);
    EXPECT_EQ(expected_value(first_pass, 0ULL), 0x5555555555555555ULL) << where(first_pass, 0);
    EXPECT_EQ(expected_value(first_pass, 1ULL), 0xAAAAAAAAAAAAAAAAULL) << where(first_pass, 1);

    const std::vector<std::uint64_t> words = sampled_words();
    for (std::uint32_t pass = 0; pass < k_alternation_passes; ++pass) {
        const pattern_spec spec = make_spec(RQ_PATTERN_CHECKERBOARD, 0ULL, pass);
        for (const std::uint64_t word : words) {
            const std::uint64_t expected =
                ((word ^ pass) & 1ULL) != 0 ? 0xAAAAAAAAAAAAAAAAULL : 0x5555555555555555ULL;
            EXPECT_EQ(expected_value(spec, word), expected) << where(spec, word);
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * High toggle
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsHighToggle, AlternatesPerWordInTheDocumentedPhase) {
    // patterns.h: 0xFFFFFFFF00000000 where (word ^ pass) is odd, otherwise 0x00000000FFFFFFFF.
    const pattern_spec first_pass = make_spec(RQ_PATTERN_HIGH_TOGGLE, 0ULL, 0U);
    EXPECT_EQ(expected_value(first_pass, 0ULL), 0x00000000FFFFFFFFULL) << where(first_pass, 0);
    EXPECT_EQ(expected_value(first_pass, 1ULL), 0xFFFFFFFF00000000ULL) << where(first_pass, 1);

    const std::vector<std::uint64_t> words = sampled_words();
    for (std::uint32_t pass = 0; pass < k_alternation_passes; ++pass) {
        const pattern_spec spec = make_spec(RQ_PATTERN_HIGH_TOGGLE, 0ULL, pass);
        for (const std::uint64_t word : words) {
            const std::uint64_t expected =
                ((word ^ pass) & 1ULL) != 0 ? 0xFFFFFFFF00000000ULL : 0x00000000FFFFFFFFULL;
            EXPECT_EQ(expected_value(spec, word), expected) << where(spec, word);
        }
    }
}

// Maximum toggling on both 64-bit (DDR4 channel) and 32-bit (DDR5 sub-channel) beats needs
// complementary neighbouring words and complementary halves within each word.
TEST(PatternsHighToggle, NeighbouringWordsAndWordHalvesAreComplements) {
    const std::vector<std::uint64_t> words = sampled_words();
    for (const std::uint32_t pass : k_passes) {
        const pattern_spec spec = make_spec(RQ_PATTERN_HIGH_TOGGLE, 0ULL, pass);
        for (const std::uint64_t word : words) {
            const std::uint64_t v = expected_value(spec, word);
            EXPECT_EQ(expected_value(spec, word + 1ULL), ~v)
                << where(spec, word) << ", compared with word " << word + 1ULL;
            EXPECT_EQ(v >> 32U, ~v & 0xFFFFFFFFULL) << where(spec, word);
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Moving inversion over walking bits
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsMovingInversionWalking, OneBitSetOnEvenPassesAndOneBitClearedOnOddPasses) {
    const std::vector<std::uint64_t> words = sampled_words();
    for (std::uint32_t pass = 0; pass < k_alternation_passes; ++pass) {
        const int expected_set_bits = pass % 2U == 0 ? 1 : 63;
        const pattern_spec spec = make_spec(RQ_PATTERN_MOVING_INVERSION_WALKING, 0ULL, pass);
        for (const std::uint64_t word : words) {
            EXPECT_EQ(std::popcount(expected_value(spec, word)), expected_set_bits)
                << where(spec, word);
        }
    }
}

TEST(PatternsMovingInversionWalking, WalkingBitIsWordIndexModulo64) {
    // patterns.h: the walking bit is 1 << (word & 63), so it advances one position per word.
    const std::vector<std::uint64_t> words = sampled_words();
    for (const std::uint32_t pass : k_passes) {
        const pattern_spec spec = make_spec(RQ_PATTERN_MOVING_INVERSION_WALKING, 0ULL, pass);
        for (const std::uint64_t word : words) {
            const std::uint64_t v = expected_value(spec, word);
            const std::uint64_t walking_bit = pass % 2U == 0 ? v : ~v;
            EXPECT_EQ(walking_bit, 1ULL << (word & 63ULL)) << where(spec, word);
        }
    }
}

TEST(PatternsMovingInversionWalking, EveryBitPositionOccursInAny64ConsecutiveWords) {
    constexpr std::uint64_t starts[] = {
        0ULL, 1ULL, 37ULL, 63ULL, 64ULL, 1ULL << 20U, (1ULL << 37U) + 5ULL};
    for (const std::uint32_t pass : k_passes) {
        const pattern_spec spec = make_spec(RQ_PATTERN_MOVING_INVERSION_WALKING, 0ULL, pass);
        for (const std::uint64_t start : starts) {
            std::uint64_t positions_seen = 0;
            for (std::uint64_t word = start; word < start + 64ULL; ++word) {
                const std::uint64_t v = expected_value(spec, word);
                positions_seen |= pass % 2U == 0 ? v : ~v;
            }
            EXPECT_EQ(positions_seen, ~0ULL) << where(spec, start) << " to " << start + 63ULL;
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Out-of-range kinds
 * ------------------------------------------------------------------------------------------- */

// core/session.cpp rejects these before any step runs. patterns.h still defines them as the
// constant 0, so that the scalar reference and the cursor agree even for a kind never written.
TEST(PatternsOutOfRangeKind, YieldsTheConstantAndTheCursorAgrees) {
    constexpr std::uint32_t kinds[] = {0U, 6U, 0xFFFFFFFFU};
    for (const std::uint32_t kind : kinds) {
        for (const bool complement : {false, true}) {
            const pattern_spec spec = make_spec(kind, 1ULL, 1U, complement);
            const std::uint64_t constant = complement ? ~0ULL : 0ULL;
            for (const std::uint64_t word : k_words) {
                EXPECT_EQ(expected_value(spec, word), constant) << where(spec, word);
            }
            expect_cursor_sweep(spec, 0ULL, 1, 64ULL);
            expect_cursor_sweep(spec, 64ULL, -1, 64ULL);
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * SplitMix64 building blocks
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsSplitMix, FinalizerReproducesThePublishedSeedZeroStream) {
    // SplitMix64 seeded with 0 outputs Mix13(n * gamma) for n = 1, 2, 3, ...
    constexpr std::uint64_t published_stream[] = {0xE220A8397B1DCDAFULL, 0x6E789E6AA1B965F4ULL,
                                                  0x06C45D188009454FULL};
    std::uint64_t counter = 0;
    for (const std::uint64_t published : published_stream) {
        counter += k_published_gamma;
        EXPECT_EQ(splitmix64_finalize<isa_scalar>(counter), published) << "input " << counter;
    }
    EXPECT_EQ(k_splitmix_gamma, k_published_gamma);
}

TEST(PatternsSplitMix, FinalizerEqualsMix13OverSampledInputs) {
    sample_source source(0x452821E638D01377ULL);
    for (int i = 0; i < 10'000; ++i) {
        const std::uint64_t z = source.next();
        EXPECT_EQ(splitmix64_finalize<isa_scalar>(z), reference_mix13(z)) << "input " << z;
    }
}

TEST(PatternsSplitMix, PassKeyIsTheFinalizerAtCounterPassPlusOne) {
    EXPECT_EQ(pattern_pass_key<isa_scalar>(0ULL, 0U), 0xE220A8397B1DCDAFULL);
    EXPECT_EQ(pattern_pass_key<isa_scalar>(0ULL, 1U), 0x6E789E6AA1B965F4ULL);
    // The last pass shows whether pass + 1 is formed in 64 bits, as documented, not in 32.
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : {0U, 1U, 2U, 3U, 0xFFFFFFFFU}) {
            const std::uint64_t counter = seed + k_published_gamma * (std::uint64_t{pass} + 1ULL);
            EXPECT_EQ(pattern_pass_key<isa_scalar>(seed, pass), reference_mix13(counter))
                << "seed " << seed << ", pass " << pass;
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Random
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsRandom, FollowsTheDocumentedSplitMixFormula) {
    // Known answers computed independently of patterns.h from the published SplitMix64.
    struct known_answer {
        std::uint64_t seed;
        std::uint32_t pass;
        std::uint64_t word;
        std::uint64_t value;
    };
    constexpr known_answer known_answers[] = {
        {0ULL, 0U, 0ULL, 0xA706DD2F4D197E6FULL},
        {1ULL, 2U, 7ULL, 0x78F5B242ABF88965ULL},
        {0x9E3779B97F4A7C15ULL, 3U, (1ULL << 37U) + 5ULL, 0xED5831691D9F57A9ULL},
    };
    for (const known_answer& answer : known_answers) {
        const pattern_spec spec = make_spec(RQ_PATTERN_RANDOM, answer.seed, answer.pass);
        EXPECT_EQ(expected_value(spec, answer.word), answer.value) << where(spec, answer.word);
    }

    // patterns.h: Mix13(pass key + gamma * (word + 1)).
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            const pattern_spec spec = make_spec(RQ_PATTERN_RANDOM, seed, pass);
            const std::uint64_t key = pattern_pass_key<isa_scalar>(seed, pass);
            for (const std::uint64_t word : k_words) {
                EXPECT_EQ(expected_value(spec, word),
                          reference_mix13(key + k_published_gamma * (word + 1ULL)))
                    << where(spec, word);
            }
        }
    }
}

TEST(PatternsRandom, ReproducibleAndChangedBySeedWordOrPass) {
    constexpr std::size_t sample_count = 10'000;
    const std::vector<sample> samples = sample_inputs(sample_count, 0x243F6A8885A308D3ULL);
    std::vector<std::uint64_t> values;
    values.reserve(sample_count);
    for (std::size_t i = 0; i < samples.size(); ++i) {
        const sample& input = samples[i];
        const pattern_spec spec = make_spec(RQ_PATTERN_RANDOM, input.seed, input.pass);
        const std::uint64_t v = expected_value(spec, input.word);
        EXPECT_EQ(expected_value(spec, input.word), v) << where(spec, input.word);

        // The nearest neighbours of the input, where a structural collision would show first: one
        // flipped seed bit, the next word and the next pass.
        const std::uint64_t other_seed = input.seed ^ (1ULL << (i % 64U));
        EXPECT_NE(value(RQ_PATTERN_RANDOM, other_seed, input.pass, input.word), v)
            << where(spec, input.word) << ", compared with seed " << other_seed;
        EXPECT_NE(value(RQ_PATTERN_RANDOM, input.seed, input.pass, input.word + 1ULL), v)
            << where(spec, input.word) << ", compared with word " << input.word + 1ULL;
        EXPECT_NE(value(RQ_PATTERN_RANDOM, input.seed, input.pass + 1U, input.word), v)
            << where(spec, input.word) << ", compared with pass " << input.pass + 1U;
        values.push_back(v);
    }

    // The sampled inputs are distinct, so no two of their values may collide.
    const std::uint64_t* const data = values.data();
    std::size_t collisions = 0;
    for (std::size_t i = 0; i < values.size(); ++i) {
        for (std::size_t j = i + 1; j < values.size(); ++j) {
            if (data[i] == data[j]) {
                ++collisions;
                const sample& first = samples[i];
                const sample& second = samples[j];
                ADD_FAILURE() << where(make_spec(RQ_PATTERN_RANDOM, first.seed, first.pass),
                                       first.word)
                              << " collides with "
                              << where(make_spec(RQ_PATTERN_RANDOM, second.seed, second.pass),
                                       second.word);
            }
        }
    }
    EXPECT_EQ(collisions, std::size_t{0});
}

TEST(PatternsRandom, BitsAreBalancedOver100000ConsecutiveWords) {
    constexpr std::uint64_t word_count = 100'000;
    // Binomial spreads: the total's standard deviation is 0.04 % of its mean and a single bit's
    // is 0.16 percentage points, so these bounds fail only for a biased generator.
    constexpr std::uint64_t total_low = word_count * 64U * 49U / 100U;
    constexpr std::uint64_t total_high = word_count * 64U * 51U / 100U;
    constexpr std::uint64_t bit_low = word_count * 48U / 100U;
    constexpr std::uint64_t bit_high = word_count * 52U / 100U;
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            const pattern_spec spec = make_spec(RQ_PATTERN_RANDOM, seed, pass);
            std::vector<std::uint64_t> set_counts(64, 0ULL);
            std::uint64_t total_set = 0;
            for (std::uint64_t word = 0; word < word_count; ++word) {
                std::uint64_t v = expected_value(spec, word);
                total_set += static_cast<std::uint64_t>(std::popcount(v));
                while (v != 0) {
                    ++set_counts[static_cast<std::size_t>(std::countr_zero(v))];
                    v &= v - 1ULL;
                }
            }
            EXPECT_GE(total_set, total_low) << where(spec, 0) << " to " << word_count - 1ULL;
            EXPECT_LE(total_set, total_high) << where(spec, 0) << " to " << word_count - 1ULL;
            for (std::size_t bit = 0; bit < set_counts.size(); ++bit) {
                EXPECT_GE(set_counts[bit], bit_low)
                    << where(spec, 0) << " to " << word_count - 1ULL << ", bit " << bit;
                EXPECT_LE(set_counts[bit], bit_high)
                    << where(spec, 0) << " to " << word_count - 1ULL << ", bit " << bit;
            }
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Moving inversion over random data
 * ------------------------------------------------------------------------------------------- */

TEST(PatternsMovingInversionRandom, BaseEqualsRandomForTheSameSeedWordAndPass) {
    const std::vector<std::uint64_t> words = sampled_words();
    for (const std::uint64_t seed : k_seeds) {
        for (const std::uint32_t pass : k_passes) {
            const pattern_spec spec = make_spec(RQ_PATTERN_MOVING_INVERSION_RANDOM, seed, pass);
            for (const std::uint64_t word : words) {
                EXPECT_EQ(expected_value(spec, word), value(RQ_PATTERN_RANDOM, seed, pass, word))
                    << where(spec, word);
            }
        }
    }
    for (const sample& input : sample_inputs(1'000, 0x13198A2E03707344ULL)) {
        const pattern_spec spec =
            make_spec(RQ_PATTERN_MOVING_INVERSION_RANDOM, input.seed, input.pass);
        EXPECT_EQ(expected_value(spec, input.word),
                  value(RQ_PATTERN_RANDOM, input.seed, input.pass, input.word))
            << where(spec, input.word);
    }
}

}  // namespace rq::engine::test
