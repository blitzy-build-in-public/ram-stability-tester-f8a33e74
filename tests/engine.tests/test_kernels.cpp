/**
 * @file test_kernels.cpp
 * @brief Kernel-set equivalence, flush and re-read helper, and instruction-set selection tests.
 *
 * - Kernels*: the AVX2 and AVX-512 kernel sets against the SSE2 reference set and against the
 *   out-of-line expected_value, over every pattern kind, pass, complement flag, direction, start
 *   offset within a cache line and range length the kernels split differently; the shape of the
 *   three raw tables; and the baseline flush and re-read helpers.
 * - IsaDispatch*: the pure select_isa over synthetic cpu_features built from the bit constants
 *   in core/kernels/isa_dispatch.h, tables_for over synthetic selections, and a host sanity
 *   check that expects nothing of the host CPU.
 *
 * An AVX2 or AVX-512 kernel is executed only from a fixture whose SetUp has confirmed, through
 * select_isa(read_cpu_features(), RQ_ISA_AUTO), that the host supports that set; on any other
 * CPU the test reports SKIPPED. Executing those kernels without the extension would raise an
 * illegal-instruction fault, an SEH exception nothing catches. Reading the tables' function
 * pointers executes nothing, so the table and tables_for tests need no particular CPU.
 *
 * Buffers are committed with VirtualAlloc, never locked, and stay below 2 MiB each. Each flush
 * and re-read check touches a handful of lines once: no flush-and-reload loop over a small
 * address set, the access style the product's stress bound excludes.
 *
 * Every kernel assertion names the ISA, pattern, pass, complement flag, direction, first word
 * index, word count and start offset, so a failure identifies the exact case.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <ostream>
#include <string>
#include <tuple>
#include <vector>

#include "ramquick_engine.h"
#include "core/patterns/patterns.h"
#include "core/kernels/kernels.h"
#include "core/kernels/isa_dispatch.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Matrix
 * ------------------------------------------------------------------------------------------- */

/** The five RQ_PATTERN_* kinds. */
constexpr std::uint32_t k_all_kinds[] = {
    RQ_PATTERN_CHECKERBOARD, RQ_PATTERN_MOVING_INVERSION_WALKING,
    RQ_PATTERN_MOVING_INVERSION_RANDOM, RQ_PATTERN_RANDOM, RQ_PATTERN_HIGH_TOGGLE};

/** The three kernel sets. SSE2 is checked against expected_value alone, on every CPU. */
constexpr std::uint32_t k_all_isas[] = {RQ_ISA_SSE2, RQ_ISA_AVX2, RQ_ISA_AVX512};

constexpr std::uint32_t k_passes[] = {0U, 1U, 2U, 3U};

/** One seed: seed handling is the pattern formulas' concern, covered by test_patterns.cpp. */
constexpr std::uint64_t k_seed = 0x0123456789ABCDEFULL;

/**
 * Logical index of each range's first word: even and odd, below and above a page of words, and
 * far beyond 2^32, where a 32-bit truncation in a vector index computation would show.
 */
constexpr std::uint64_t k_first_word_indices[] = {0ULL, 1ULL, 3ULL, 4097ULL, (1ULL << 37U) + 3ULL};

constexpr int k_directions[] = {1, -1};

/** Words per 64-byte cache line: one AVX-512 vector, two AVX2 vectors, four SSE2 vectors. */
constexpr std::size_t k_line_words = k_cache_line_bytes / sizeof(std::uint64_t);

/**
 * Short ranges: one word, ranges shorter than every vector width, exactly one line, one word
 * more, and odd lengths spanning several vectors. Each runs at every 8-byte start position
 * within a cache line, so every head and tail length of every set occurs.
 */
constexpr std::size_t k_short_counts[] = {1U, 3U, 7U, 8U, 9U, 63U, 1023U};

/** One MiB of words, and five words more: long cursor runs with and without a scalar tail. */
constexpr std::size_t k_long_counts[] = {(std::size_t{1} << 20U) / sizeof(std::uint64_t),
                                         (std::size_t{1} << 20U) / sizeof(std::uint64_t) + 5U};

/**
 * Start offsets, in words from a 64-byte boundary, of the long ranges: aligned for every set,
 * and 8-byte aligned but not 16-, 32- or 64-byte aligned, the longest head of every set. The
 * short ranges cover the other positions.
 */
constexpr std::size_t k_long_offsets[] = {0U, 1U};

constexpr std::size_t k_max_range_words = k_long_counts[1];

/** Guard words on each side of a range: two AVX-512 vectors' worth, more than any overrun. */
constexpr std::size_t k_guard_words = 64U;

/** Value of every guard word: no pattern produces it within a range under test. */
constexpr std::uint64_t k_guard_value = 0xC0FFEE0DDBA11ED5ULL;

/** Words of a test buffer: leading guard, the largest start offset, the range, trailing guard. */
constexpr std::size_t k_buffer_words =
    k_guard_words + (k_line_words - 1U) + k_max_range_words + k_guard_words;

static_assert(k_guard_words % k_line_words == 0U,
              "the leading guard must keep the range start on a 64-byte boundary plus offset");
static_assert(k_buffer_words * sizeof(std::uint64_t) <= (std::size_t{2} << 20U),
              "each test buffer must stay within 2 MiB");

/** A range's length and start position. */
struct range_layout {
    /** Words in the range. */
    std::size_t words;
    /** Words from the preceding 64-byte boundary to the range's first word, 0 to 7. */
    std::size_t offset;
};

/** Every short count at every start offset within a line. */
const std::vector<range_layout>& short_layouts() {
    static const std::vector<range_layout> layouts = [] {
        std::vector<range_layout> result;
        for (const std::size_t words : k_short_counts) {
            for (std::size_t offset = 0; offset < k_line_words; ++offset) {
                result.push_back(range_layout{words, offset});
            }
        }
        return result;
    }();
    return layouts;
}

/** The short layouts, then the long counts at the long offsets. */
const std::vector<range_layout>& all_layouts() {
    static const std::vector<range_layout> layouts = [] {
        std::vector<range_layout> result = short_layouts();
        for (const std::size_t words : k_long_counts) {
            for (const std::size_t offset : k_long_offsets) {
                result.push_back(range_layout{words, offset});
            }
        }
        return result;
    }();
    return layouts;
}

/** First word index of the verification tests' long ranges. */
constexpr std::uint64_t k_long_verification_first_word = 4097U;

/**
 * Layouts the verification tests run at a first word index: every short layout, plus the long
 * layouts at k_long_verification_first_word only. The fill test keeps the full cross product.
 * A long range adds only a long run of cursor advances, which does not depend on where the run
 * starts, while the short layouts already meet every first index at every head and tail length;
 * running the 1 MiB ranges at all five indices would multiply the verification tests' run time
 * about fivefold (over three minutes in Debug) without reaching another code path.
 */
const std::vector<range_layout>& verification_layouts(std::uint64_t first_word_index) {
    return first_word_index == k_long_verification_first_word ? all_layouts() : short_layouts();
}

/** Words in the longest range of a layout list. */
std::size_t longest_range(const std::vector<range_layout>& layouts) noexcept {
    std::size_t longest = 0;
    for (const range_layout& layout : layouts) {
        longest = std::max(longest, layout.words);
    }
    return longest;
}

/** Every pass of a kind, with and without the complement flag. */
std::vector<pattern_spec> specs_of(std::uint32_t kind) {
    std::vector<pattern_spec> specs;
    for (const std::uint32_t pass : k_passes) {
        for (const bool complement : {false, true}) {
            specs.push_back(pattern_spec{kind, k_seed, pass, complement});
        }
    }
    return specs;
}

/**
 * The `next` spec a verify_and_stream sweep writes: on even passes the moving-inversion
 * complement, the production use; on odd passes a different kind, seed and pass, so a kernel
 * that streamed ~expected instead of `next` fails there.
 */
pattern_spec next_spec_for(const pattern_spec& spec) noexcept {
    if ((spec.pass & 1U) == 0U) {
        return pattern_spec{spec.kind, spec.seed, spec.pass, !spec.complement};
    }
    return pattern_spec{spec.kind % 5U + 1U, spec.seed + 1U, spec.pass + 1U, spec.complement};
}

/* ---------------------------------------------------------------------------------------------
 * Names and failure context
 * ------------------------------------------------------------------------------------------- */

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

/** Alphanumeric ISA name for parameterised test names. */
const char* isa_test_name(std::uint32_t isa) noexcept {
    switch (isa) {
    case RQ_ISA_AUTO:
        return "Auto";
    case RQ_ISA_SSE2:
        return "SSE2";
    case RQ_ISA_AVX2:
        return "AVX2";
    case RQ_ISA_AVX512:
        return "AVX512";
    default:
        return "OutOfRange";
    }
}

/** ISA name as the product spells it, for failure and skip messages. */
const char* isa_display_name(std::uint32_t isa) noexcept {
    return isa == RQ_ISA_AVX512 ? "AVX-512" : isa_test_name(isa);
}

/** 0x followed by 16 uppercase hex digits. */
std::string hex64(std::uint64_t value) {
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string text = "0x0000000000000000";
    for (std::size_t i = text.size(); i-- > 2U;) {
        text[i] = digits[value & 0xFU];
        value >>= 4U;
    }
    return text;
}

/** One kernel invocation of the matrix. */
struct kernel_case {
    std::uint32_t isa;
    pattern_spec spec;
    std::uint64_t first_word_index;
    int direction;
    range_layout layout;
};

/** Failure context naming every parameter of a kernel case. */
::testing::Message where(const kernel_case& c) {
    ::testing::Message message;
    message << "ISA " << isa_display_name(c.isa) << ", pattern " << kind_name(c.spec.kind) << " ("
            << c.spec.kind << "), pass " << c.spec.pass << ", complement "
            << (c.spec.complement ? "on" : "off") << ", direction " << c.direction
            << ", first word index " << c.first_word_index << ", words " << c.layout.words
            << ", start offset " << c.layout.offset * sizeof(std::uint64_t)
            << " bytes past a 64-byte boundary";
    return message;
}

/* ---------------------------------------------------------------------------------------------
 * Buffers
 * ------------------------------------------------------------------------------------------- */

/** Releases a VirtualAlloc region. */
struct virtual_free_deleter {
    void operator()(std::uint64_t* words) const noexcept { VirtualFree(words, 0, MEM_RELEASE); }
};

/**
 * Committed, unlocked read-write memory from VirtualAlloc, released on destruction. Its base is
 * at an allocation-granularity boundary, so a range placed k_guard_words + offset words in
 * starts exactly offset words past a 64-byte boundary.
 */
class test_buffer {
public:
    explicit test_buffer(std::size_t words)
        : words_(static_cast<std::uint64_t*>(VirtualAlloc(
              nullptr, words * sizeof(std::uint64_t), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))),
          size_(words_ != nullptr ? words : 0U) {}

    /** False when VirtualAlloc failed; size() is then 0. */
    bool valid() const noexcept { return words_ != nullptr; }
    std::uint64_t* data() noexcept { return words_.get(); }
    std::size_t size() const noexcept { return size_; }

    /**
     * First word of a range with this layout, k_guard_words + layout.offset words in, so
     * k_guard_words guard words precede it and at least k_guard_words follow it.
     */
    std::uint64_t* place(const range_layout& layout) noexcept {
        return words_.get() + k_guard_words + layout.offset;
    }

private:
    std::unique_ptr<std::uint64_t, virtual_free_deleter> words_;
    std::size_t size_;
};

/** Sets the k_guard_words words before and after [range, range + words) to k_guard_value. */
void arm_guards(std::uint64_t* range, std::size_t words) noexcept {
    std::fill(range - k_guard_words, range, k_guard_value);
    std::fill(range + words, range + words + k_guard_words, k_guard_value);
}

/** Succeeds when every guard word around [range, range + words) still holds k_guard_value. */
::testing::AssertionResult guards_intact(const std::uint64_t* range, std::size_t words) {
    for (std::size_t i = 1; i <= k_guard_words; ++i) {
        const std::uint64_t before = *(range - i);
        const std::uint64_t after = range[words + i - 1U];
        if (before != k_guard_value) {
            return ::testing::AssertionFailure()
                   << "guard word " << i << " before the range was overwritten with "
                   << hex64(before);
        }
        if (after != k_guard_value) {
            return ::testing::AssertionFailure()
                   << "guard word " << i << " after the range was overwritten with "
                   << hex64(after);
        }
    }
    return ::testing::AssertionSuccess();
}

/** expected_value(spec, first_word_index + i) for i in [0, words). */
void compute_reference(std::vector<std::uint64_t>& out, const pattern_spec& spec,
                       std::uint64_t first_word_index, std::size_t words) {
    out.resize(words);
    for (std::size_t i = 0; i < words; ++i) {
        out[i] = expected_value(spec, first_word_index + static_cast<std::uint64_t>(i));
    }
}

/** Succeeds when actual[i] == expected[i] for every i < words; otherwise names the first word. */
::testing::AssertionResult words_equal(const std::uint64_t* actual, const std::uint64_t* expected,
                                       std::size_t words) {
    const auto difference = std::mismatch(actual, actual + words, expected);
    if (difference.first == actual + words) {
        return ::testing::AssertionSuccess();
    }
    return ::testing::AssertionFailure()
           << "word " << static_cast<std::size_t>(difference.first - actual) << " holds "
           << hex64(*difference.first) << ", expected " << hex64(*difference.second);
}

/* ---------------------------------------------------------------------------------------------
 * Mismatch recording
 * ------------------------------------------------------------------------------------------- */

/** One reported or injected mismatch, positioned by word offset within its range. */
struct observed_mismatch {
    /** Word offset from the range's first word. */
    std::size_t word;
    std::uint64_t expected;
    std::uint64_t actual;
    /** The word's content when the sink ran: verify_and_stream must not have overwritten it. */
    std::uint64_t in_memory;

    bool operator==(const observed_mismatch&) const = default;
};

std::ostream& operator<<(std::ostream& os, const observed_mismatch& m) {
    return os << "{word " << m.word << ", expected " << hex64(m.expected) << ", actual "
              << hex64(m.actual) << ", in memory " << hex64(m.in_memory) << "}";
}

/**
 * Sink recording every report in call order, with the word's content read during the call.
 * Storage is reserved up front, so on_mismatch never allocates and cannot throw; reports beyond
 * the capacity, which no test provokes, are counted as overflow and fail the comparison.
 */
class recording_sink final : public mismatch_sink {
public:
    struct report {
        const std::uint64_t* address;
        std::uint64_t expected;
        std::uint64_t actual;
        std::uint64_t in_memory;
    };

    recording_sink() { reports_.reserve(k_capacity); }
    ~recording_sink() = default;
    recording_sink(const recording_sink&) = delete;
    recording_sink& operator=(const recording_sink&) = delete;

    void on_mismatch(const std::uint64_t* address, std::uint64_t expected,
                     std::uint64_t actual) noexcept override {
        if (reports_.size() == reports_.capacity()) {
            ++overflow_;
            return;
        }
        reports_.push_back(report{address, expected, actual, *address});
    }

    const std::vector<report>& reports() const noexcept { return reports_; }
    std::uint64_t overflow() const noexcept { return overflow_; }

private:
    static constexpr std::size_t k_capacity = 256U;

    std::vector<report> reports_;
    std::uint64_t overflow_ = 0;
};

/**
 * Succeeds when the sink saw exactly the injected mismatches (sorted by word), each reported
 * once, in traversal order (ascending addresses for a non-negative direction, descending for a
 * negative one), with the wrong value still in memory during the call.
 */
::testing::AssertionResult reports_match(const recording_sink& sink, const std::uint64_t* range,
                                         std::size_t words, int direction,
                                         const std::vector<observed_mismatch>& injected) {
    if (sink.overflow() != 0U) {
        return ::testing::AssertionFailure()
               << sink.overflow() << " reports beyond the recording capacity";
    }
    std::vector<observed_mismatch> seen;
    for (const recording_sink::report& r : sink.reports()) {
        if (r.address < range || r.address >= range + words) {
            return ::testing::AssertionFailure() << "report outside the range, at word offset "
                                                 << static_cast<std::ptrdiff_t>(r.address - range);
        }
        seen.push_back(observed_mismatch{static_cast<std::size_t>(r.address - range), r.expected,
                                         r.actual, r.in_memory});
    }
    for (std::size_t k = 1; k < seen.size(); ++k) {
        const bool in_order =
            direction < 0 ? seen[k].word < seen[k - 1U].word : seen[k].word > seen[k - 1U].word;
        if (!in_order) {
            return ::testing::AssertionFailure()
                   << "report " << k << " at word " << seen[k].word << " follows word "
                   << seen[k - 1U].word << ", against the traversal order";
        }
    }
    std::sort(seen.begin(), seen.end(), [](const observed_mismatch& a, const observed_mismatch& b) {
        return a.word < b.word;
    });
    if (seen.size() != injected.size()) {
        return ::testing::AssertionFailure()
               << seen.size() << " reports for " << injected.size() << " injected corruptions";
    }
    for (std::size_t k = 0; k < seen.size(); ++k) {
        if (!(seen[k] == injected[k])) {
            return ::testing::AssertionFailure()
                   << "report " << seen[k] << " where " << injected[k] << " was injected";
        }
    }
    return ::testing::AssertionSuccess();
}

/**
 * Word offsets to corrupt in a range: the first and last words; the first word on a 64-byte
 * boundary, a vector boundary for every set, and the scalar head word before it; the scalar tail
 * word after the last whole line and the last word of that line; and two adjacent words inside
 * one vector of every width. Sorted and distinct.
 */
std::vector<std::size_t> corruption_offsets(const std::uint64_t* range, std::size_t words) {
    std::vector<std::size_t> offsets{0U, words - 1U};
    const std::size_t misaligned =
        static_cast<std::size_t>(reinterpret_cast<std::uintptr_t>(range) % k_cache_line_bytes) /
        sizeof(std::uint64_t);
    const std::size_t first_boundary = (k_line_words - misaligned) % k_line_words;
    if (first_boundary < words) {
        offsets.push_back(first_boundary);
        if (first_boundary > 0U) {
            offsets.push_back(first_boundary - 1U);
        }
        const std::size_t whole_lines = (words - first_boundary) / k_line_words;
        const std::size_t tail_first = first_boundary + whole_lines * k_line_words;
        if (whole_lines > 0U) {
            offsets.push_back(first_boundary + 2U);
            offsets.push_back(first_boundary + 3U);
            offsets.push_back(tail_first - 1U);
        }
        if (tail_first < words) {
            offsets.push_back(tail_first);
        }
    }
    std::sort(offsets.begin(), offsets.end());
    offsets.erase(std::unique(offsets.begin(), offsets.end()), offsets.end());
    return offsets;
}

/**
 * Flips one bit in each word at `offsets`, a different bit per offset, and returns the
 * mismatches a verification against `expected` must report, sorted by word.
 */
std::vector<observed_mismatch> inject_corruptions(std::uint64_t* range,
                                                  const std::vector<std::size_t>& offsets,
                                                  const std::uint64_t* expected) {
    std::vector<observed_mismatch> injected;
    for (const std::size_t word : offsets) {
        const std::uint64_t bit = std::uint64_t{1} << ((word * 7U + 63U) % 64U);
        range[word] = expected[word] ^ bit;
        injected.push_back(observed_mismatch{word, expected[word], range[word], range[word]});
    }
    return injected;
}

/* ---------------------------------------------------------------------------------------------
 * Host capability and table lookup
 * ------------------------------------------------------------------------------------------- */

/** The host's selection, exactly as the engine computes it for rq_system_info. */
isa_selection host_selection() noexcept {
    return select_isa(read_cpu_features(), RQ_ISA_AUTO);
}

/**
 * Skips the current test when the host lacks the set. Called from fixture SetUp, so the test
 * body, the only place a kernel of that set is executed, never runs on such a CPU.
 */
void require_isa(std::uint32_t isa) {
    if ((host_selection().available_mask & (1U << isa)) == 0U) {
        GTEST_SKIP() << "CPU lacks " << isa_display_name(isa);
    }
}

/** The raw kernel set for an RQ_ISA_* value other than AUTO. */
const kernel_table& table_of(std::uint32_t isa) noexcept {
    switch (isa) {
    case RQ_ISA_AVX512:
        return k_kernels_avx512;
    case RQ_ISA_AVX2:
        return k_kernels_avx2;
    default:
        return k_kernels_sse2;
    }
}

/* ---------------------------------------------------------------------------------------------
 * Fixtures and parameters
 * ------------------------------------------------------------------------------------------- */

/** (RQ_ISA_* set under test, RQ_PATTERN_* kind). */
using kernel_param = std::tuple<std::uint32_t, std::uint32_t>;

std::string kernel_param_name(const ::testing::TestParamInfo<kernel_param>& info) {
    return std::string(isa_test_name(std::get<0>(info.param))) + "_" +
           kind_name(std::get<1>(info.param));
}

/**
 * One kernel set and one pattern kind. SetUp skips the test before its body can execute a
 * kernel the host lacks; the buffers are committed but not touched until then.
 */
class KernelsEquivalence : public ::testing::TestWithParam<kernel_param> {
protected:
    void SetUp() override {
        require_isa(isa());
        if (IsSkipped()) {
            return;
        }
        ASSERT_TRUE(tested_buffer_.valid() && reference_buffer_.valid())
            << "VirtualAlloc failed with Windows error " << GetLastError();
        ASSERT_EQ(reinterpret_cast<std::uintptr_t>(tested_buffer_.data()) % k_cache_line_bytes, 0U);
        ASSERT_EQ(reinterpret_cast<std::uintptr_t>(reference_buffer_.data()) % k_cache_line_bytes,
                  0U);
    }

    std::uint32_t isa() const { return std::get<0>(GetParam()); }
    std::uint32_t kind() const { return std::get<1>(GetParam()); }
    const kernel_table& tested() const { return table_of(isa()); }

    /** The SSE2 comparison is meaningful only for the AVX sets; SSE2 is checked alone. */
    bool compare_with_sse2() const { return isa() != RQ_ISA_SSE2; }

    /** The tables a case runs on: the set under test and, for the AVX sets, SSE2. */
    std::vector<const kernel_table*> tables() const {
        std::vector<const kernel_table*> result{&tested()};
        if (compare_with_sse2()) {
            result.push_back(&k_kernels_sse2);
        }
        return result;
    }

    /** Buffer a table's case runs in: the set under test and the SSE2 reference never share. */
    test_buffer& buffer_for(const kernel_table& table) {
        return &table == &tested() ? tested_buffer_ : reference_buffer_;
    }

    test_buffer tested_buffer_{k_buffer_words};
    test_buffer reference_buffer_{k_buffer_words};
};

/**
 * Streams a case's range with one table after setting every word to the complement of its
 * expected value, so a word the kernel skips cannot pass, and arming the guards. Fences before
 * returning the range, so ordinary loads see the streamed data.
 */
std::uint64_t* run_fill(const kernel_table& table, test_buffer& buffer, const kernel_case& c,
                        const std::vector<std::uint64_t>& expected) {
    std::uint64_t* const range = buffer.place(c.layout);
    std::transform(expected.data(), expected.data() + c.layout.words, range,
                   [](std::uint64_t value) { return ~value; });
    arm_guards(range, c.layout.words);
    table.fill_stream(range, c.layout.words, c.spec, c.first_word_index, c.direction);
    table.full_fence();
    return range;
}

/** Parameter of the flush and re-read helper tests: one CLFLUSH or CLFLUSHOPT pair. */
struct flush_helpers {
    const char* name;
    flush_range_fn flush_range;
    reread_fn reread;
    bool needs_clflushopt;
};

void PrintTo(const flush_helpers& helpers, std::ostream* os) {
    *os << helpers.name;
}

constexpr flush_helpers k_flush_helpers[] = {
    {"Clflush", &flush_range_clflush, &reread_clflush, false},
    {"Clflushopt", &flush_range_clflushopt, &reread_clflushopt, true},
};

/** Test name of a parameter that carries its own name. */
template <class Param>
std::string param_name(const ::testing::TestParamInfo<Param>& info) {
    return info.param.name;
}

std::string isa_param_name(const ::testing::TestParamInfo<std::uint32_t>& info) {
    return isa_test_name(info.param);
}

/** Words of one 4 KiB page: room for the few lines a flush or re-read check touches. */
constexpr std::size_t k_page_words = 4096U / sizeof(std::uint64_t);

/** One flush and re-read pair. CLFLUSHOPT is executed only where CPUID reports it. */
class KernelsFlushHelpers : public ::testing::TestWithParam<flush_helpers> {
protected:
    void SetUp() override {
        if (GetParam().needs_clflushopt && !host_selection().clflushopt) {
            GTEST_SKIP() << "CPU lacks CLFLUSHOPT";
        }
        ASSERT_TRUE(page_.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    }

    test_buffer page_{k_page_words};
};

/** The raw kernel sets, one per RQ_ISA_* value; only their data is read. */
class KernelsRawTable : public ::testing::TestWithParam<std::uint32_t> {};

/** (isa_selection.selected, isa_selection.clflushopt) of a synthetic selection. */
using tables_for_param = std::tuple<std::uint32_t, bool>;

std::string tables_for_param_name(const ::testing::TestParamInfo<tables_for_param>& info) {
    return std::string(isa_test_name(std::get<0>(info.param))) +
           (std::get<1>(info.param) ? "_Clflushopt" : "_Clflush");
}

class IsaDispatchTablesFor : public ::testing::TestWithParam<tables_for_param> {};

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * Kernel equivalence: every set against expected_value, the AVX sets also against SSE2
 * ------------------------------------------------------------------------------------------- */

TEST_P(KernelsEquivalence, FillStreamMatchesExpectedValueAndSse2) {
    std::vector<std::uint64_t> expected;
    for (const pattern_spec& spec : specs_of(kind())) {
        for (const std::uint64_t first : k_first_word_indices) {
            compute_reference(expected, spec, first, k_max_range_words);
            for (const range_layout& layout : all_layouts()) {
                for (const int direction : k_directions) {
                    const kernel_case c{isa(), spec, first, direction, layout};
                    const std::uint64_t* const out =
                        run_fill(tested(), tested_buffer_, c, expected);
                    ASSERT_TRUE(guards_intact(out, layout.words)) << where(c);
                    ASSERT_TRUE(words_equal(out, expected.data(), layout.words)) << where(c);
                    if (compare_with_sse2()) {
                        const std::uint64_t* const reference =
                            run_fill(k_kernels_sse2, reference_buffer_, c, expected);
                        ASSERT_TRUE(guards_intact(reference, layout.words))
                            << "SSE2 reference, " << where(c);
                        ASSERT_TRUE(words_equal(reference, expected.data(), layout.words))
                            << "SSE2 reference, " << where(c);
                        ASSERT_EQ(std::memcmp(out, reference, layout.words * sizeof(std::uint64_t)),
                                  0)
                            << "differs from the SSE2 reference, " << where(c);
                    }
                }
            }
        }
    }
}

TEST_P(KernelsEquivalence, VerifyAcceptsCorrectDataWithoutCallingTheSink) {
    std::vector<std::uint64_t> expected;
    for (const pattern_spec& spec : specs_of(kind())) {
        for (const std::uint64_t first : k_first_word_indices) {
            const std::vector<range_layout>& layouts = verification_layouts(first);
            compute_reference(expected, spec, first, longest_range(layouts));
            for (const range_layout& layout : layouts) {
                std::uint64_t* const range = tested_buffer_.place(layout);
                std::copy_n(expected.data(), layout.words, range);
                arm_guards(range, layout.words);
                for (const int direction : k_directions) {
                    for (const kernel_table* table : tables()) {
                        const kernel_case c{table->isa, spec, first, direction, layout};
                        recording_sink sink;
                        const std::uint64_t mismatches =
                            table->verify(range, layout.words, spec, first, direction, sink);
                        ASSERT_EQ(mismatches, 0U) << where(c);
                        ASSERT_TRUE(sink.reports().empty() && sink.overflow() == 0U)
                            << sink.reports().size() << " sink calls, " << where(c);
                    }
                }
                const kernel_case written{isa(), spec, first, k_directions[0], layout};
                ASSERT_TRUE(guards_intact(range, layout.words))
                    << "verify wrote memory, both directions, " << where(written);
                ASSERT_TRUE(words_equal(range, expected.data(), layout.words))
                    << "verify wrote memory, both directions, " << where(written);
            }
        }
    }
}

TEST_P(KernelsEquivalence, VerifyReportsInjectedCorruptionsLikeSse2) {
    std::vector<std::uint64_t> expected;
    for (const pattern_spec& spec : specs_of(kind())) {
        for (const std::uint64_t first : k_first_word_indices) {
            const std::vector<range_layout>& layouts = verification_layouts(first);
            compute_reference(expected, spec, first, longest_range(layouts));
            for (const range_layout& layout : layouts) {
                std::uint64_t* const range = tested_buffer_.place(layout);
                std::copy_n(expected.data(), layout.words, range);
                const std::vector<observed_mismatch> injected = inject_corruptions(
                    range, corruption_offsets(range, layout.words), expected.data());
                for (const int direction : k_directions) {
                    std::vector<std::uint64_t> counts;
                    for (const kernel_table* table : tables()) {
                        const kernel_case c{table->isa, spec, first, direction, layout};
                        recording_sink sink;
                        const std::uint64_t mismatches =
                            table->verify(range, layout.words, spec, first, direction, sink);
                        ASSERT_EQ(mismatches, static_cast<std::uint64_t>(injected.size()))
                            << where(c);
                        ASSERT_TRUE(reports_match(sink, range, layout.words, direction, injected))
                            << where(c);
                        counts.push_back(mismatches);
                    }
                    // Both sets matched the same injected set, so their reports are the same
                    // set of (address, expected, actual) triples; the counts must agree too.
                    ASSERT_TRUE(std::equal(counts.begin() + 1, counts.end(), counts.begin()))
                        << where(kernel_case{isa(), spec, first, direction, layout});
                }
            }
        }
    }
}

TEST_P(KernelsEquivalence, VerifyAndStreamReportsBeforeOverwritingAndWritesNext) {
    std::vector<std::uint64_t> expected;
    std::vector<std::uint64_t> next_values;
    for (const pattern_spec& spec : specs_of(kind())) {
        const pattern_spec next = next_spec_for(spec);
        for (const std::uint64_t first : k_first_word_indices) {
            const std::vector<range_layout>& layouts = verification_layouts(first);
            compute_reference(expected, spec, first, longest_range(layouts));
            compute_reference(next_values, next, first, longest_range(layouts));
            for (const range_layout& layout : layouts) {
                for (const int direction : k_directions) {
                    std::vector<const std::uint64_t*> results;
                    for (const kernel_table* table : tables()) {
                        const kernel_case c{table->isa, spec, first, direction, layout};
                        std::uint64_t* const range = buffer_for(*table).place(layout);
                        std::copy_n(expected.data(), layout.words, range);
                        const std::vector<observed_mismatch> injected = inject_corruptions(
                            range, corruption_offsets(range, layout.words), expected.data());
                        arm_guards(range, layout.words);
                        recording_sink sink;
                        const std::uint64_t mismatches = table->verify_and_stream(
                            range, layout.words, spec, next, first, direction, sink);
                        table->full_fence();
                        ASSERT_EQ(mismatches, static_cast<std::uint64_t>(injected.size()))
                            << where(c);
                        ASSERT_TRUE(reports_match(sink, range, layout.words, direction, injected))
                            << where(c);
                        ASSERT_TRUE(guards_intact(range, layout.words)) << where(c);
                        ASSERT_TRUE(words_equal(range, next_values.data(), layout.words))
                            << "after the sweep to " << kind_name(next.kind) << " pass "
                            << next.pass << ", complement " << (next.complement ? "on" : "off")
                            << ", " << where(c);
                        results.push_back(range);
                    }
                    if (compare_with_sse2()) {
                        ASSERT_EQ(std::memcmp(results[0], results[1],
                                              layout.words * sizeof(std::uint64_t)),
                                  0)
                            << "differs from the SSE2 reference, "
                            << where(kernel_case{isa(), spec, first, direction, layout});
                    }
                }
            }
        }
    }
}

TEST_P(KernelsEquivalence, EveryNegativeDirectionTraversesDescending) {
    constexpr int negative_directions[] = {-2, -1000, std::numeric_limits<int>::min()};
    constexpr range_layout layout{1023U, 1U};
    constexpr std::uint64_t first = 4097U;
    const pattern_spec spec{kind(), k_seed, 1U, false};
    const pattern_spec next = next_spec_for(spec);
    std::vector<std::uint64_t> expected;
    std::vector<std::uint64_t> next_values;
    compute_reference(expected, spec, first, layout.words);
    compute_reference(next_values, next, first, layout.words);
    for (const int direction : negative_directions) {
        const kernel_case c{isa(), spec, first, direction, layout};
        std::uint64_t* const range = tested_buffer_.place(layout);
        std::copy_n(expected.data(), layout.words, range);
        const std::vector<observed_mismatch> injected =
            inject_corruptions(range, corruption_offsets(range, layout.words), expected.data());
        arm_guards(range, layout.words);

        recording_sink verify_sink;
        ASSERT_EQ(tested().verify(range, layout.words, spec, first, direction, verify_sink),
                  static_cast<std::uint64_t>(injected.size()))
            << where(c);
        ASSERT_TRUE(reports_match(verify_sink, range, layout.words, direction, injected))
            << where(c);

        recording_sink sweep_sink;
        ASSERT_EQ(tested().verify_and_stream(range, layout.words, spec, next, first, direction,
                                             sweep_sink),
                  static_cast<std::uint64_t>(injected.size()))
            << where(c);
        tested().full_fence();
        ASSERT_TRUE(reports_match(sweep_sink, range, layout.words, direction, injected))
            << where(c);
        ASSERT_TRUE(guards_intact(range, layout.words)) << where(c);
        ASSERT_TRUE(words_equal(range, next_values.data(), layout.words)) << where(c);

        tested().fill_stream(range, layout.words, spec, first, direction);
        tested().full_fence();
        ASSERT_TRUE(guards_intact(range, layout.words)) << where(c);
        ASSERT_TRUE(words_equal(range, expected.data(), layout.words)) << where(c);
    }
}

TEST_P(KernelsEquivalence, EmptyRangesAreNoOps) {
    const pattern_spec spec{kind(), k_seed, 0U, false};
    const pattern_spec next = next_spec_for(spec);
    for (const std::size_t offset : k_long_offsets) {
        const range_layout layout{0U, offset};
        std::uint64_t* const range = tested_buffer_.place(layout);
        arm_guards(range, 0U);
        for (const int direction : k_directions) {
            const kernel_case c{isa(), spec, 0U, direction, layout};
            tested().fill_stream(range, 0U, spec, 0U, direction);
            recording_sink sink;
            ASSERT_EQ(tested().verify(range, 0U, spec, 0U, direction, sink), 0U) << where(c);
            ASSERT_EQ(tested().verify_and_stream(range, 0U, spec, next, 0U, direction, sink), 0U)
                << where(c);
            tested().full_fence();
            ASSERT_TRUE(sink.reports().empty()) << where(c);
            ASSERT_TRUE(guards_intact(range, 0U)) << where(c);
        }
    }
}

// gtest names an instantiated suite "<prefix>/<suite>", so the prefix keeps the Kernels* filter
// selecting these tests.
INSTANTIATE_TEST_SUITE_P(KernelsAllSetsAndKinds, KernelsEquivalence,
                         ::testing::Combine(::testing::ValuesIn(k_all_isas),
                                            ::testing::ValuesIn(k_all_kinds)),
                         kernel_param_name);

/* ---------------------------------------------------------------------------------------------
 * Raw tables: tags, baseline helpers and distinct vector entries
 * ------------------------------------------------------------------------------------------- */

TEST_P(KernelsRawTable, IsTaggedWithItsSetAndUsesTheBaselineHelpers) {
    const std::uint32_t isa = GetParam();
    const kernel_table& table = table_of(isa);
    EXPECT_EQ(table.isa, isa);
    EXPECT_TRUE(table.fill_stream != nullptr);
    EXPECT_TRUE(table.verify != nullptr);
    EXPECT_TRUE(table.verify_and_stream != nullptr);
    EXPECT_EQ(table.full_fence, &full_fence_mfence);
    // The raw sets use CLFLUSHOPT; tables_for patches CLFLUSH in where it is absent.
    EXPECT_EQ(table.flush_range, &flush_range_clflushopt);
    EXPECT_EQ(table.reread, &reread_clflushopt);
}

INSTANTIATE_TEST_SUITE_P(KernelsAllSets, KernelsRawTable, ::testing::ValuesIn(k_all_isas),
                         isa_param_name);

// The equivalence tests would pass vacuously if a set reused another set's vector kernels.
TEST(KernelsTables, VectorEntriesAreDistinctPerSet) {
    const kernel_table* const sets[] = {&k_kernels_sse2, &k_kernels_avx2, &k_kernels_avx512};
    for (std::size_t a = 0; a < std::size(sets); ++a) {
        for (std::size_t b = a + 1U; b < std::size(sets); ++b) {
            EXPECT_NE(sets[a]->fill_stream, sets[b]->fill_stream)
                << isa_display_name(sets[a]->isa) << " and " << isa_display_name(sets[b]->isa);
            EXPECT_NE(sets[a]->verify, sets[b]->verify)
                << isa_display_name(sets[a]->isa) << " and " << isa_display_name(sets[b]->isa);
            EXPECT_NE(sets[a]->verify_and_stream, sets[b]->verify_and_stream)
                << isa_display_name(sets[a]->isa) << " and " << isa_display_name(sets[b]->isa);
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * Flush and re-read helpers
 * ------------------------------------------------------------------------------------------- */

TEST_P(KernelsFlushHelpers, RereadReturnsWordsJustStreamedWithoutAnExternalFence) {
    // Three lines' worth of words starting one word past a line boundary, streamed with the
    // baseline set's non-temporal stores and left unfenced: the re-read's own leading MFENCE
    // must make them visible before its flush and load.
    constexpr std::size_t words = 3U * k_line_words;
    constexpr std::uint64_t first = 100U;
    const pattern_spec spec{RQ_PATTERN_RANDOM, k_seed, 0U, false};
    std::uint64_t* const range = page_.data() + k_line_words + 1U;
    k_kernels_sse2.fill_stream(range, words, spec, first, 1);
    for (const std::size_t word : {std::size_t{0}, std::size_t{11}, words - 1U}) {
        EXPECT_EQ(GetParam().reread(range + word),
                  expected_value(spec, first + static_cast<std::uint64_t>(word)))
            << GetParam().name << ", word " << word;
    }
}

TEST_P(KernelsFlushHelpers, RereadReturnsAWordStillDirtyInTheCache) {
    // An ordinary store leaves the line dirty in the cache; the flush writes it back first.
    constexpr std::uint64_t value = 0x5A3C96E1F00F7EC3ULL;
    std::uint64_t* const word = page_.data() + 5U * k_line_words + 3U;
    *static_cast<volatile std::uint64_t*>(word) = value;
    EXPECT_EQ(GetParam().reread(word), value) << GetParam().name;
}

TEST_P(KernelsFlushHelpers, FlushRangeLeavesDataIntactForUnalignedStartAndLength) {
    constexpr std::size_t words = 5U * k_line_words;
    const pattern_spec spec{RQ_PATTERN_HIGH_TOGGLE, k_seed, 1U, false};
    std::uint64_t* const range = page_.data() + 8U * k_line_words;
    std::vector<std::uint64_t> expected(words);
    for (std::size_t i = 0; i < words; ++i) {
        expected[i] = expected_value(spec, static_cast<std::uint64_t>(i));
    }
    std::copy(expected.begin(), expected.end(), range);

    // 13 bytes into the first line through 7 bytes into the fourth: the start and the end both
    // fall inside a line, so every overlapping line must be flushed whole and nothing else
    // touched. A zero-byte flush and a one-byte flush of the last line complete the edges.
    const unsigned char* const bytes = reinterpret_cast<const unsigned char*>(range);
    GetParam().flush_range(bytes + 13, 3U * k_cache_line_bytes - 6U);
    GetParam().flush_range(bytes + 4U * k_cache_line_bytes, 0U);
    GetParam().flush_range(bytes + 4U * k_cache_line_bytes + 63U, 1U);
    full_fence_mfence();
    EXPECT_TRUE(words_equal(range, expected.data(), words)) << GetParam().name;
}

INSTANTIATE_TEST_SUITE_P(KernelsBaselineHelpers, KernelsFlushHelpers,
                         ::testing::ValuesIn(k_flush_helpers), param_name<flush_helpers>);

/* ---------------------------------------------------------------------------------------------
 * tables_for: the selected set, with CLFLUSH patched in where CLFLUSHOPT is absent
 * ------------------------------------------------------------------------------------------- */

TEST_P(IsaDispatchTablesFor, CopiesTheSelectedSetAndPatchesFlushesWithoutClflushopt) {
    const auto [selected, clflushopt] = GetParam();
    // Synthetic: tables_for reads only selected and clflushopt, and copying a table executes
    // nothing, so the AVX sets are inspected on any CPU.
    const isa_selection selection{.selected = selected,
                                  .best = selected,
                                  .available_mask = 1U << RQ_ISA_SSE2,
                                  .clflushopt = clflushopt,
                                  .override_rejected = false};
    // RQ_ISA_AUTO and values outside the RQ_ISA_* range yield the SSE2 set.
    const std::uint32_t expected_isa =
        selected == RQ_ISA_AVX512 || selected == RQ_ISA_AVX2 ? selected : RQ_ISA_SSE2;
    const kernel_table& expected_set = table_of(expected_isa);

    const kernel_table table = tables_for(selection);
    EXPECT_EQ(table.isa, expected_isa);
    EXPECT_EQ(table.fill_stream, expected_set.fill_stream);
    EXPECT_EQ(table.verify, expected_set.verify);
    EXPECT_EQ(table.verify_and_stream, expected_set.verify_and_stream);
    EXPECT_EQ(table.full_fence, &full_fence_mfence);
    EXPECT_EQ(table.flush_range, clflushopt ? &flush_range_clflushopt : &flush_range_clflush);
    EXPECT_EQ(table.reread, clflushopt ? &reread_clflushopt : &reread_clflush);
}

INSTANTIATE_TEST_SUITE_P(IsaDispatchSelections, IsaDispatchTablesFor,
                         ::testing::Combine(::testing::Values(RQ_ISA_AUTO, RQ_ISA_SSE2, RQ_ISA_AVX2,
                                                              RQ_ISA_AVX512, 7U),
                                            ::testing::Bool()),
                         tables_for_param_name);

/* ---------------------------------------------------------------------------------------------
 * select_isa over synthetic CPUID and XCR0 values
 * ------------------------------------------------------------------------------------------- */

namespace {

/** A highest basic leaf a current CPU reports; any value from 7 up enables leaf 7. */
constexpr std::uint32_t k_modern_max_basic_leaf = 0x16U;

/** XCR0 with x87, SSE and AVX state: what an OS reports on an AVX2 CPU. */
constexpr std::uint64_t k_xcr0_x87_sse_avx = 0x7U;

/** XCR0 with x87, SSE, AVX, opmask, ZMM_Hi256 and Hi16_ZMM state. */
constexpr std::uint64_t k_xcr0_x87_to_hi16_zmm = 0xE7U;

/** Bit of one RQ_ISA_* value in isa_selection.available_mask. */
constexpr std::uint32_t isa_bit(std::uint32_t isa) noexcept {
    return 1U << isa;
}

/** available_mask when `best` is the best set: availability is nested, SSE2 always present. */
constexpr std::uint32_t mask_up_to(std::uint32_t best) noexcept {
    std::uint32_t mask = 0U;
    for (std::uint32_t isa = RQ_ISA_SSE2; isa <= best; ++isa) {
        mask |= isa_bit(isa);
    }
    return mask;
}

/** Every AVX2 requirement, built from the individual bit constants rather than the masks. */
constexpr cpu_features avx2_full_features() noexcept {
    cpu_features features{};
    features.max_basic_leaf = k_modern_max_basic_leaf;
    features.leaf1_ecx = k_cpuid1_ecx_osxsave | k_cpuid1_ecx_avx | k_cpuid1_ecx_fma;
    features.leaf7_ebx = k_cpuid7_ebx_avx2 | k_cpuid7_ebx_bmi1 | k_cpuid7_ebx_bmi2;
    features.xcr0 = k_xcr0_x87_sse_avx;
    return features;
}

/** The AVX2 features plus AVX-512 F, DQ, CD, BW and VL and the opmask and ZMM state. */
constexpr cpu_features avx512_full_features() noexcept {
    cpu_features features = avx2_full_features();
    features.leaf7_ebx |= k_cpuid7_ebx_avx512f | k_cpuid7_ebx_avx512dq | k_cpuid7_ebx_avx512cd |
                          k_cpuid7_ebx_avx512bw | k_cpuid7_ebx_avx512vl;
    features.xcr0 = k_xcr0_x87_to_hi16_zmm;
    return features;
}

/**
 * Every AVX-512 feature bit and XCR0 state, but OSXSAVE clear: XGETBV cannot execute, so the
 * XCR0 value must be ignored and only SSE2 remains.
 */
constexpr cpu_features osxsave_clear_features() noexcept {
    cpu_features features = avx512_full_features();
    features.leaf1_ecx &= ~k_cpuid1_ecx_osxsave;
    return features;
}

/** A CPU reporting basic leaf 1 alone and no extension. */
constexpr cpu_features no_extension_features() noexcept {
    cpu_features features{};
    features.max_basic_leaf = 1U;
    return features;
}

/** Succeeds when a selection keeps the invariants isa_selection documents. */
::testing::AssertionResult selection_invariants_hold(const isa_selection& selection) {
    if ((selection.available_mask & isa_bit(RQ_ISA_SSE2)) == 0U) {
        return ::testing::AssertionFailure() << "SSE2 bit missing from the available mask";
    }
    if ((selection.available_mask & isa_bit(RQ_ISA_AUTO)) != 0U) {
        return ::testing::AssertionFailure() << "RQ_ISA_AUTO bit set in the available mask";
    }
    if ((selection.available_mask & ~mask_up_to(RQ_ISA_AVX512)) != 0U) {
        return ::testing::AssertionFailure()
               << "bits beyond RQ_ISA_AVX512 in the available mask " << selection.available_mask;
    }
    if (selection.best == RQ_ISA_AUTO ||
        (selection.available_mask & isa_bit(selection.best)) == 0U) {
        return ::testing::AssertionFailure() << "best " << selection.best << " is not available";
    }
    if (selection.selected == RQ_ISA_AUTO ||
        (selection.available_mask & isa_bit(selection.selected)) == 0U) {
        return ::testing::AssertionFailure()
               << "selected " << selection.selected << " is not available";
    }
    return ::testing::AssertionSuccess();
}

/** A synthetic CPU and the selection it must produce with RQ_ISA_AUTO. */
struct isa_profile {
    const char* name;
    cpu_features features;
    std::uint32_t best;
};

void PrintTo(const isa_profile& profile, std::ostream* os) {
    *os << profile.name;
}

constexpr isa_profile k_profiles[] = {
    {"Avx512Full", avx512_full_features(), RQ_ISA_AVX512},
    {"Avx2Full", avx2_full_features(), RQ_ISA_AVX2},
    {"OsxsaveClear", osxsave_clear_features(), RQ_ISA_SSE2},
    {"NoExtensions", no_extension_features(), RQ_ISA_SSE2},
};

/** One feature bit select_isa requires, and the register it is read from. */
struct required_bit {
    const char* name;
    std::uint32_t cpu_features::* field;
    std::uint32_t bit;
};

void PrintTo(const required_bit& required, std::ostream* os) {
    *os << required.name;
}

/** The five AVX-512 subsets /arch:AVX512 may emit. */
constexpr required_bit k_avx512_bits[] = {
    {"Avx512F", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx512f},
    {"Avx512CD", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx512cd},
    {"Avx512BW", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx512bw},
    {"Avx512DQ", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx512dq},
    {"Avx512VL", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx512vl},
};

/** OSXSAVE and AVX, and AVX2 with the FMA and BMI instructions /arch:AVX2 may emit. */
constexpr required_bit k_avx2_bits[] = {
    {"Osxsave", &cpu_features::leaf1_ecx, k_cpuid1_ecx_osxsave},
    {"Avx", &cpu_features::leaf1_ecx, k_cpuid1_ecx_avx},
    {"Fma", &cpu_features::leaf1_ecx, k_cpuid1_ecx_fma},
    {"Avx2", &cpu_features::leaf7_ebx, k_cpuid7_ebx_avx2},
    {"Bmi1", &cpu_features::leaf7_ebx, k_cpuid7_ebx_bmi1},
    {"Bmi2", &cpu_features::leaf7_ebx, k_cpuid7_ebx_bmi2},
};

/** An XCR0 value on top of the full AVX-512 feature bits, and the best set it allows. */
struct xcr0_case {
    const char* name;
    std::uint64_t xcr0;
    std::uint32_t best;
};

void PrintTo(const xcr0_case& c, std::ostream* os) {
    *os << c.name << " (XCR0 " << hex64(c.xcr0) << ")";
}

constexpr xcr0_case k_xcr0_cases[] = {
    {"AllAvx512State", 0xE7U, RQ_ISA_AVX512},
    {"WithoutX87", 0xE6U, RQ_ISA_AVX512},
    // PKRU (bit 9) and AMX tile state (bits 17 and 18) are not required and must not interfere.
    {"WithHigherComponents", 0x602E7U, RQ_ISA_AVX512},
    {"NoOpmask", 0xC7U, RQ_ISA_AVX2},
    {"NoZmmHi256", 0xA7U, RQ_ISA_AVX2},
    {"NoHi16Zmm", 0x67U, RQ_ISA_AVX2},
    {"AvxStateOnly", 0x7U, RQ_ISA_AVX2},
    {"NoAvxState", 0xE3U, RQ_ISA_SSE2},
    {"NoSseState", 0xE5U, RQ_ISA_SSE2},
    {"X87Only", 0x1U, RQ_ISA_SSE2},
    {"Zero", 0x0U, RQ_ISA_SSE2},
};

class IsaDispatchProfile : public ::testing::TestWithParam<isa_profile> {};
class IsaDispatchAvx512Requirement : public ::testing::TestWithParam<required_bit> {};
class IsaDispatchAvx2Requirement : public ::testing::TestWithParam<required_bit> {};
class IsaDispatchXcr0 : public ::testing::TestWithParam<xcr0_case> {};

}  // namespace

TEST(IsaDispatchBits, ConstantsAreTheDocumentedCpuidAndXcr0Bits) {
    EXPECT_EQ(k_cpuid1_ecx_fma, 1U << 12U);
    EXPECT_EQ(k_cpuid1_ecx_osxsave, 1U << 27U);
    EXPECT_EQ(k_cpuid1_ecx_avx, 1U << 28U);
    EXPECT_EQ(k_cpuid7_ebx_bmi1, 1U << 3U);
    EXPECT_EQ(k_cpuid7_ebx_avx2, 1U << 5U);
    EXPECT_EQ(k_cpuid7_ebx_bmi2, 1U << 8U);
    EXPECT_EQ(k_cpuid7_ebx_avx512f, 1U << 16U);
    EXPECT_EQ(k_cpuid7_ebx_avx512dq, 1U << 17U);
    EXPECT_EQ(k_cpuid7_ebx_clflushopt, 1U << 23U);
    EXPECT_EQ(k_cpuid7_ebx_avx512cd, 1U << 28U);
    EXPECT_EQ(k_cpuid7_ebx_avx512bw, 1U << 30U);
    EXPECT_EQ(k_cpuid7_ebx_avx512vl, 1U << 31U);
    EXPECT_EQ(k_xcr0_avx_state, 0x6U);
    EXPECT_EQ(k_xcr0_avx512_state, 0xE6U);
    EXPECT_EQ(k_avx2_required_leaf1_ecx,
              k_cpuid1_ecx_osxsave | k_cpuid1_ecx_avx | k_cpuid1_ecx_fma);
    EXPECT_EQ(k_avx2_required_leaf7_ebx, k_cpuid7_ebx_avx2 | k_cpuid7_ebx_bmi1 | k_cpuid7_ebx_bmi2);
    EXPECT_EQ(k_avx512_required_leaf7_ebx, k_avx2_required_leaf7_ebx | k_cpuid7_ebx_avx512f |
                                               k_cpuid7_ebx_avx512dq | k_cpuid7_ebx_avx512cd |
                                               k_cpuid7_ebx_avx512bw | k_cpuid7_ebx_avx512vl);
}

TEST_P(IsaDispatchProfile, AutoSelectsTheBestAvailableSet) {
    const isa_profile& profile = GetParam();
    const isa_selection selection = select_isa(profile.features, RQ_ISA_AUTO);
    EXPECT_TRUE(selection_invariants_hold(selection));
    EXPECT_EQ(selection.best, profile.best);
    EXPECT_EQ(selection.selected, profile.best);
    EXPECT_EQ(selection.available_mask, mask_up_to(profile.best));
    EXPECT_FALSE(selection.override_rejected);
    EXPECT_FALSE(selection.clflushopt);
}

TEST_P(IsaDispatchProfile, ClflushoptFollowsLeafSevenBit23Only) {
    const isa_profile& profile = GetParam();
    cpu_features features = profile.features;
    features.leaf7_ebx |= k_cpuid7_ebx_clflushopt;
    const isa_selection with_bit = select_isa(features, RQ_ISA_AUTO);
    // Leaf 7 is ignored below a highest basic leaf of 7, its CLFLUSHOPT bit included.
    EXPECT_EQ(with_bit.clflushopt, features.max_basic_leaf >= 7U);
    EXPECT_EQ(with_bit.best, profile.best);
    EXPECT_EQ(with_bit.available_mask, mask_up_to(profile.best));

    features.leaf7_ebx &= ~k_cpuid7_ebx_clflushopt;
    const isa_selection without_bit = select_isa(features, RQ_ISA_AUTO);
    EXPECT_FALSE(without_bit.clflushopt);
    EXPECT_EQ(without_bit.best, profile.best);
}

TEST_P(IsaDispatchProfile, OverrideSelectsAnAvailableSetAndRejectsAnyOther) {
    const isa_profile& profile = GetParam();
    for (const std::uint32_t isa : k_all_isas) {
        const isa_selection selection = select_isa(profile.features, isa);
        EXPECT_TRUE(selection_invariants_hold(selection)) << "override " << isa_display_name(isa);
        EXPECT_EQ(selection.best, profile.best) << "override " << isa_display_name(isa);
        if (isa <= profile.best) {
            EXPECT_EQ(selection.selected, isa) << "override " << isa_display_name(isa);
            EXPECT_FALSE(selection.override_rejected) << "override " << isa_display_name(isa);
        } else {
            // Forcing an unavailable set would execute instructions the CPU lacks.
            EXPECT_EQ(selection.selected, profile.best) << "override " << isa_display_name(isa);
            EXPECT_TRUE(selection.override_rejected) << "override " << isa_display_name(isa);
        }
    }
    for (const std::uint32_t out_of_range : {RQ_ISA_AVX512 + 1U, 31U, 32U, 0xFFFFFFFFU}) {
        const isa_selection selection = select_isa(profile.features, out_of_range);
        EXPECT_TRUE(selection_invariants_hold(selection)) << "override " << out_of_range;
        EXPECT_EQ(selection.selected, profile.best) << "override " << out_of_range;
        EXPECT_TRUE(selection.override_rejected) << "override " << out_of_range;
    }
}

INSTANTIATE_TEST_SUITE_P(IsaDispatchProfiles, IsaDispatchProfile, ::testing::ValuesIn(k_profiles),
                         param_name<isa_profile>);

TEST_P(IsaDispatchAvx512Requirement, ClearingTheBitFallsBackToAvx2) {
    cpu_features features = avx512_full_features();
    features.*(GetParam().field) &= ~GetParam().bit;
    const isa_selection selection = select_isa(features, RQ_ISA_AUTO);
    EXPECT_TRUE(selection_invariants_hold(selection));
    EXPECT_EQ(selection.best, RQ_ISA_AVX2);
    EXPECT_EQ(selection.selected, RQ_ISA_AVX2);
    EXPECT_EQ(selection.available_mask, mask_up_to(RQ_ISA_AVX2));
}

INSTANTIATE_TEST_SUITE_P(IsaDispatchAvx512Bits, IsaDispatchAvx512Requirement,
                         ::testing::ValuesIn(k_avx512_bits), param_name<required_bit>);

TEST_P(IsaDispatchAvx2Requirement, ClearingTheBitFallsBackToSse2) {
    // AVX-512 requires the whole AVX2 condition, so the full AVX-512 input falls back too.
    for (const cpu_features& base : {avx2_full_features(), avx512_full_features()}) {
        cpu_features features = base;
        features.*(GetParam().field) &= ~GetParam().bit;
        const isa_selection selection = select_isa(features, RQ_ISA_AUTO);
        EXPECT_TRUE(selection_invariants_hold(selection));
        EXPECT_EQ(selection.best, RQ_ISA_SSE2) << "from XCR0 " << hex64(base.xcr0);
        EXPECT_EQ(selection.selected, RQ_ISA_SSE2) << "from XCR0 " << hex64(base.xcr0);
        EXPECT_EQ(selection.available_mask, mask_up_to(RQ_ISA_SSE2))
            << "from XCR0 " << hex64(base.xcr0);
    }
}

INSTANTIATE_TEST_SUITE_P(IsaDispatchAvx2Bits, IsaDispatchAvx2Requirement,
                         ::testing::ValuesIn(k_avx2_bits), param_name<required_bit>);

TEST_P(IsaDispatchXcr0, RequiresTheOsToSaveEveryStateComponentTheSetUses) {
    cpu_features features = avx512_full_features();
    features.xcr0 = GetParam().xcr0;
    const isa_selection selection = select_isa(features, RQ_ISA_AUTO);
    EXPECT_TRUE(selection_invariants_hold(selection));
    EXPECT_EQ(selection.best, GetParam().best);
    EXPECT_EQ(selection.selected, GetParam().best);
    EXPECT_EQ(selection.available_mask, mask_up_to(GetParam().best));
}

INSTANTIATE_TEST_SUITE_P(IsaDispatchXcr0Values, IsaDispatchXcr0, ::testing::ValuesIn(k_xcr0_cases),
                         param_name<xcr0_case>);

TEST(IsaDispatchSelect, LeafSevenIsReadOnlyFromAHighestBasicLeafOfSeven) {
    cpu_features features = avx512_full_features();
    features.leaf7_ebx |= k_cpuid7_ebx_clflushopt;

    features.max_basic_leaf = 7U;
    const isa_selection at_seven = select_isa(features, RQ_ISA_AUTO);
    EXPECT_EQ(at_seven.best, RQ_ISA_AVX512);
    EXPECT_TRUE(at_seven.clflushopt);

    for (const std::uint32_t max_basic_leaf : {6U, 1U, 0U}) {
        features.max_basic_leaf = max_basic_leaf;
        const isa_selection below = select_isa(features, RQ_ISA_AUTO);
        EXPECT_TRUE(selection_invariants_hold(below)) << "highest basic leaf " << max_basic_leaf;
        EXPECT_EQ(below.best, RQ_ISA_SSE2) << "highest basic leaf " << max_basic_leaf;
        EXPECT_EQ(below.available_mask, mask_up_to(RQ_ISA_SSE2))
            << "highest basic leaf " << max_basic_leaf;
        EXPECT_FALSE(below.clflushopt) << "highest basic leaf " << max_basic_leaf;
    }
}

TEST(IsaDispatchSelect, IsPureForEqualInputs) {
    for (const isa_profile& profile : k_profiles) {
        for (const std::uint32_t isa_override : {RQ_ISA_AUTO, RQ_ISA_SSE2, RQ_ISA_AVX512}) {
            const isa_selection first = select_isa(profile.features, isa_override);
            const isa_selection second = select_isa(profile.features, isa_override);
            EXPECT_EQ(first.selected, second.selected) << profile.name;
            EXPECT_EQ(first.best, second.best) << profile.name;
            EXPECT_EQ(first.available_mask, second.available_mask) << profile.name;
            EXPECT_EQ(first.clflushopt, second.clflushopt) << profile.name;
            EXPECT_EQ(first.override_rejected, second.override_rejected) << profile.name;
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * The host: consistency only, no expectation about which CPU runs the tests
 * ------------------------------------------------------------------------------------------- */

TEST(IsaDispatchHost, FeatureReadIsRepeatableAndLeavesUnreadableFieldsZero) {
    const cpu_features first = read_cpu_features();
    const cpu_features second = read_cpu_features();
    EXPECT_EQ(first.max_basic_leaf, second.max_basic_leaf);
    EXPECT_EQ(first.leaf1_ecx, second.leaf1_ecx);
    EXPECT_EQ(first.leaf7_ebx, second.leaf7_ebx);
    EXPECT_EQ(first.xcr0, second.xcr0);
    if ((first.leaf1_ecx & k_cpuid1_ecx_osxsave) == 0U) {
        EXPECT_EQ(first.xcr0, 0U) << "XCR0 read without OSXSAVE";
    }
    if (first.max_basic_leaf < 7U) {
        EXPECT_EQ(first.leaf7_ebx, 0U) << "leaf 7 read above the highest basic leaf";
    }
}

TEST(IsaDispatchHost, AutoSelectionIsAvailableAndEveryAvailableSetCanBeForced) {
    const cpu_features features = read_cpu_features();
    const isa_selection selection = select_isa(features, RQ_ISA_AUTO);
    EXPECT_TRUE(selection_invariants_hold(selection));
    EXPECT_NE(selection.available_mask & isa_bit(selection.selected), 0U);
    EXPECT_EQ(selection.selected, selection.best);
    EXPECT_FALSE(selection.override_rejected);
    EXPECT_EQ(tables_for(selection).isa, selection.selected);
    for (const std::uint32_t isa : k_all_isas) {
        const bool available = (selection.available_mask & isa_bit(isa)) != 0U;
        const isa_selection forced = select_isa(features, isa);
        EXPECT_EQ(forced.override_rejected, !available) << isa_display_name(isa);
        EXPECT_EQ(forced.selected, available ? isa : selection.best) << isa_display_name(isa);
        EXPECT_EQ(forced.clflushopt, selection.clflushopt) << isa_display_name(isa);
    }
}

}  // namespace rq::engine::test
