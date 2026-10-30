/**
 * @file test_kernels.cpp
 * @brief Kernel-set equivalence, flush and re-read helper, and instruction-set selection tests.
 *
 * - Kernels*: the AVX2 and AVX-512 kernel sets against the SSE2 reference set and against the
 *   out-of-line expected_value, over every pattern kind, pass, complement flag, direction, start
 *   offset within a cache line and range length the kernels split differently; the shape of the
 *   three raw tables; and the baseline flush and re-read helpers.
 * - kernels_traced*: the instructions the built full_fence, flush_range and reread helpers
 *   execute, called directly and through real_memory_access's fence, flush and reread_word,
 *   recorded by an in-process single-step trace.
 * - KernelsStrided*: the BankStride entries of every set against expected_value and, for the
 *   AVX sets, against SSE2, over every kind, the three strides, 2 MiB, 6 MiB and 64 MiB spans
 *   and calls that start or end mid-sweep; and the identity of their visit sequence with
 *   order_cursor's BankStride ranges.
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
 * Buffers are committed with VirtualAlloc and never locked. The range buffers stay below 2 MiB
 * each; the strided buffers hold one 64 MiB window, the largest span the step runner issues, so
 * the 8 KiB stride's 8,192-line sweeps are reached. Each flush and re-read check touches a
 * handful of lines once: no flush-and-reload loop over a small address set, the access style the
 * product's stress bound excludes.
 *
 * The flush and re-read value checks cannot tell a working flush from none: write-back memory
 * returns the same data either way, and a re-read that loads before it fences still returns the
 * stored word. The kernels_traced tests therefore run each helper with the trap flag set, so a
 * vectored exception handler sees every instruction it executes, and compare the fences, the
 * flushes with the lines they name and the loads of the test page with the helper's contract.
 * A debugger receives every single-step exception before any vectored handler, so under one
 * those tests report SKIPPED.
 *
 * Every kernel assertion names the ISA, pattern, pass, complement flag, direction, first word
 * index, word count and start offset, so a failure identifies the exact case.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <intrin.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <ostream>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/patterns/patterns.h"
#include "core/patterns/access_order.h"
#include "core/kernels/kernels.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/real_memory_access.h"

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

/* ---------------------------------------------------------------------------------------------
 * Strided kernels: spans, visit slices and reports
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_strided_mib = std::uint64_t{1} << 20U;

/** Lines one strided call of the step runner visits: one 1 MiB work group. */
constexpr std::uint64_t k_group_visits = k_strided_mib / k_cache_line_bytes;

/**
 * Span sizes: the smallest window, a window that is not a power of two, and the largest window
 * (k_max_window_bytes), where the 8 KiB stride gives 8,192 lines per sweep.
 */
constexpr std::uint64_t k_small_strided_spans[] = {2U * k_strided_mib, 6U * k_strided_mib};
constexpr std::uint64_t k_largest_strided_span = k_max_window_bytes;

/**
 * Logical index of each span's first word: 0, and an odd index beyond 2^32, so that every
 * visited line starts at an odd word, the alternating patterns at their other parity and the
 * walking bit at an odd rotation.
 */
constexpr std::uint64_t k_strided_first_words[] = {0ULL, (1ULL << 37U) + 3ULL};

/** Words of a strided buffer: guard, the largest span, guard. */
constexpr std::size_t k_strided_buffer_words =
    k_guard_words + static_cast<std::size_t>(k_largest_strided_span / sizeof(std::uint64_t)) +
    k_guard_words;

static_assert(k_guard_words * sizeof(std::uint64_t) % k_cache_line_bytes == 0,
              "the span after the leading guard must start on a 64-byte boundary");

/** Byte offset of visit k from the span start: the kernels.h formula, restated independently. */
constexpr std::uint64_t visit_offset(const strided_visits& visits, std::uint64_t k) noexcept {
    return k / visits.lines_per_sweep * k_cache_line_bytes +
           k % visits.lines_per_sweep * visits.stride_bytes;
}

/**
 * The slices of one phase's visit sequence a test issues: the whole phase in one call; a call
 * that starts and ends mid-sweep (first_visit and visit_count both off a multiple of
 * lines_per_sweep); and, where the span holds two, the step runner's second work group.
 */
std::vector<strided_visits> visit_slices(std::uint64_t span_bytes, std::uint64_t stride) {
    const std::uint64_t lines_per_sweep = span_bytes / stride;
    const std::uint64_t total = span_bytes / k_cache_line_bytes;
    std::vector<strided_visits> slices{strided_visits{stride, lines_per_sweep, 0U, total}};
    const std::uint64_t first = lines_per_sweep / 2U + 3U;
    slices.push_back(strided_visits{stride, lines_per_sweep, first,
                                    std::min(total - first, 3U * lines_per_sweep + 5U)});
    if (total >= 2U * k_group_visits) {
        slices.push_back(strided_visits{stride, lines_per_sweep, k_group_visits, k_group_visits});
    }
    return slices;
}

/** One strided kernel invocation. */
struct strided_case {
    std::uint32_t isa;
    pattern_spec spec;
    std::uint64_t span_bytes;
    std::uint64_t first_word_index;
    strided_visits visits;
};

/** Failure context naming every parameter of a strided case. */
::testing::Message where(const strided_case& c) {
    ::testing::Message message;
    message << "ISA " << isa_display_name(c.isa) << ", pattern " << kind_name(c.spec.kind)
            << ", pass " << c.spec.pass << ", complement " << (c.spec.complement ? "on" : "off")
            << ", span " << c.span_bytes / k_strided_mib << " MiB, first word index "
            << c.first_word_index << ", stride " << c.visits.stride_bytes << ", lines per sweep "
            << c.visits.lines_per_sweep << ", visits [" << c.visits.first_visit << ", +"
            << c.visits.visit_count << ")";
    return message;
}

/**
 * Whether a call visits the line of its span with this index: visit_offset inverted. The line
 * at byte offset o, inside lines_per_sweep strides of the span start, is visit
 * (o % stride) / 64 * lines_per_sweep + o / stride; no line beyond is ever visited. Arithmetic
 * rather than a per-line table, which Debug's checked containers make the strided tests'
 * slowest step.
 */
constexpr bool visits_line(const strided_visits& visits, std::uint64_t line) noexcept {
    const std::uint64_t offset = line * k_cache_line_bytes;
    if (offset >= visits.lines_per_sweep * visits.stride_bytes) {
        return false;
    }
    const std::uint64_t visit =
        offset % visits.stride_bytes / k_cache_line_bytes * visits.lines_per_sweep +
        offset / visits.stride_bytes;
    return visit >= visits.first_visit && visit - visits.first_visit < visits.visit_count;
}

/**
 * Succeeds when every visited line holds expected_value for its words and every other line of
 * the span still holds k_guard_value; otherwise names the first wrong word.
 */
::testing::AssertionResult strided_fill_correct(const std::uint64_t* span, const strided_case& c) {
    const std::uint64_t lines = c.span_bytes / k_cache_line_bytes;
    for (std::uint64_t line = 0; line < lines; ++line) {
        const bool visited = visits_line(c.visits, line);
        const auto first = static_cast<std::size_t>(line * k_line_words);
        for (std::size_t w = 0; w < k_line_words; ++w) {
            const std::uint64_t want =
                visited ? expected_value(c.spec, c.first_word_index + first + w) : k_guard_value;
            if (span[first + w] != want) {
                return ::testing::AssertionFailure()
                       << (visited ? "visited" : "unvisited") << " line " << line << ", word " << w
                       << " holds " << hex64(span[first + w]) << ", expected " << hex64(want);
            }
        }
    }
    return ::testing::AssertionSuccess();
}

/** Writes expected_value into every word of the lines a call visits. */
void write_visited_lines(std::uint64_t* span, const strided_case& c) {
    for (std::uint64_t k = c.visits.first_visit; k < c.visits.first_visit + c.visits.visit_count;
         ++k) {
        const std::size_t first = static_cast<std::size_t>(visit_offset(c.visits, k) / 8U);
        for (std::size_t w = 0; w < k_line_words; ++w) {
            span[first + w] = expected_value(c.spec, c.first_word_index + first + w);
        }
    }
}

/**
 * Corrupts words of the visited lines and, where one exists, a word of an unvisited line, which
 * no report may name. Returns the mismatches verify_strided must report, in visit order and
 * ascending within a line: words 0 and 7 of the first visited line, words 2 and 3 of a middle
 * one and word 7 of the last one.
 */
std::vector<observed_mismatch> corrupt_visited_lines(std::uint64_t* span, const strided_case& c) {
    const strided_visits& v = c.visits;
    const std::uint64_t last = v.first_visit + v.visit_count - 1U;
    const std::pair<std::uint64_t, std::size_t> targets[] = {
        {v.first_visit, 0U},
        {v.first_visit, 7U},
        {v.first_visit + v.visit_count / 2U, 2U},
        {v.first_visit + v.visit_count / 2U, 3U},
        {last, 7U}};
    std::vector<observed_mismatch> expected;
    std::pair<std::uint64_t, std::size_t> previous{0U, 0U};
    for (const auto& [visit, w] : targets) {
        if (!expected.empty() && std::pair<std::uint64_t, std::size_t>{visit, w} <= previous) {
            continue;  // A call of one or two visits names a line more than once.
        }
        previous = {visit, w};
        const std::size_t word = static_cast<std::size_t>(visit_offset(v, visit) / 8U) + w;
        const std::uint64_t want = expected_value(c.spec, c.first_word_index + word);
        span[word] = want ^ (std::uint64_t{1} << ((word * 7U + 63U) % 64U));
        expected.push_back(observed_mismatch{word, want, span[word], span[word]});
    }
    const std::uint64_t lines = c.span_bytes / k_cache_line_bytes;
    std::uint64_t unvisited = 0;
    while (unvisited < lines && visits_line(v, unvisited)) {
        ++unvisited;
    }
    if (unvisited < lines) {
        const auto word = static_cast<std::size_t>(unvisited * k_line_words + 5U);
        span[word] = ~expected_value(c.spec, c.first_word_index + word);
    }
    return expected;
}

/** Succeeds when the sink saw exactly `expected`, in that order, with every value intact. */
::testing::AssertionResult strided_reports_match(const recording_sink& sink,
                                                 const std::uint64_t* span,
                                                 const std::vector<observed_mismatch>& expected) {
    if (sink.overflow() != 0U) {
        return ::testing::AssertionFailure()
               << sink.overflow() << " reports beyond the recording capacity";
    }
    if (sink.reports().size() != expected.size()) {
        return ::testing::AssertionFailure() << sink.reports().size() << " reports for "
                                             << expected.size() << " corruptions on visited lines";
    }
    for (std::size_t k = 0; k < expected.size(); ++k) {
        const recording_sink::report& r = sink.reports()[k];
        const observed_mismatch seen{static_cast<std::size_t>(r.address - span), r.expected,
                                     r.actual, r.in_memory};
        if (!(seen == expected[k])) {
            return ::testing::AssertionFailure() << "report " << k << " is " << seen << " where "
                                                 << expected[k] << " comes next in visit order";
        }
    }
    return ::testing::AssertionSuccess();
}

/**
 * Records the line sequence of a verification in which every word mismatches: each visited
 * line must yield its eight words as eight consecutive reports, ascending, with actual the
 * complement of expected. Storage is reserved up front, so on_mismatch never allocates.
 */
class line_order_sink final : public mismatch_sink {
public:
    line_order_sink(const std::uint64_t* span, std::size_t lines) : span_(span) {
        line_offsets_.reserve(lines);
    }
    ~line_order_sink() = default;
    line_order_sink(const line_order_sink&) = delete;
    line_order_sink& operator=(const line_order_sink&) = delete;

    void on_mismatch(const std::uint64_t* address, std::uint64_t expected,
                     std::uint64_t actual) noexcept override {
        const auto word = static_cast<std::uint64_t>(address - span_);
        const std::uint64_t position = reports_++ % k_line_words;
        if (actual != ~expected) {
            ++wrong_values_;
        }
        if (position == 0U) {
            line_word_ = word;
            if (word % k_line_words != 0U) {
                ++broken_lines_;
            }
            if (line_offsets_.size() == line_offsets_.capacity()) {
                ++overflow_;
            } else {
                line_offsets_.push_back(word * sizeof(std::uint64_t));
            }
        } else if (word != line_word_ + position) {
            ++broken_lines_;
        }
    }

    /** Byte offset of each visited line from the span start, in report order. */
    const std::vector<std::uint64_t>& line_offsets() const noexcept { return line_offsets_; }
    std::uint64_t reports() const noexcept { return reports_; }
    /** Reports that broke the eight-ascending-words-per-line shape. */
    std::uint64_t broken_lines() const noexcept { return broken_lines_; }
    std::uint64_t wrong_values() const noexcept { return wrong_values_; }
    std::uint64_t overflow() const noexcept { return overflow_; }

private:
    const std::uint64_t* span_;
    std::vector<std::uint64_t> line_offsets_;
    std::uint64_t line_word_ = 0;
    std::uint64_t reports_ = 0;
    std::uint64_t broken_lines_ = 0;
    std::uint64_t wrong_values_ = 0;
    std::uint64_t overflow_ = 0;
};

/**
 * One kernel set and one pattern kind for the strided entries. SetUp skips the test before its
 * body can execute a kernel the host lacks. The spans sit k_guard_words into committed, unlocked
 * buffers, 64-byte aligned, with guard words on both sides.
 */
class KernelsStrided : public ::testing::TestWithParam<kernel_param> {
protected:
    void SetUp() override {
        require_isa(isa());
        if (IsSkipped()) {
            return;
        }
        ASSERT_TRUE(tested_buffer_.valid() && reference_buffer_.valid())
            << "VirtualAlloc failed with Windows error " << GetLastError();
        ASSERT_EQ(reinterpret_cast<std::uintptr_t>(span(tested_buffer_)) % k_cache_line_bytes, 0U);
    }

    std::uint32_t isa() const { return std::get<0>(GetParam()); }
    std::uint32_t kind() const { return std::get<1>(GetParam()); }
    const kernel_table& tested() const { return table_of(isa()); }
    bool compare_with_sse2() const { return isa() != RQ_ISA_SSE2; }

    static std::uint64_t* span(test_buffer& buffer) noexcept {
        return buffer.data() + k_guard_words;
    }

    /**
     * Fills the span with k_guard_value, arms the guards, runs fill_stream_strided with one
     * table and fences.
     */
    static std::uint64_t* run_strided_fill(const kernel_table& table, test_buffer& buffer,
                                           const strided_case& c) {
        std::uint64_t* const s = span(buffer);
        const auto words = static_cast<std::size_t>(c.span_bytes / sizeof(std::uint64_t));
        std::fill(s, s + words, k_guard_value);
        arm_guards(s, words);
        table.fill_stream_strided(s, c.visits, c.spec, c.first_word_index);
        table.full_fence();
        return s;
    }

    test_buffer tested_buffer_{k_strided_buffer_words};
    test_buffer reference_buffer_{k_strided_buffer_words};
};

/** A spec and the logical index of the span's first word it is written from. */
struct strided_start {
    pattern_spec spec;
    std::uint64_t first_word_index;
};

/**
 * The starts of a kind a strided test runs over the small spans: passes 0 to 3, alternating the
 * complement flag, the first two at word 0 and the last two at the odd index. Each line's start
 * parity combined with the pass (what selects an alternating block) thus meets both complement
 * flags in both parities. Pairing rather than crossing the specs with the indices halves the
 * strided tests' run time without reaching another code path.
 */
std::vector<strided_start> strided_starts_of(std::uint32_t kind) {
    return {strided_start{pattern_spec{kind, k_seed, 0U, false}, k_strided_first_words[0]},
            strided_start{pattern_spec{kind, k_seed, 1U, true}, k_strided_first_words[0]},
            strided_start{pattern_spec{kind, k_seed, 2U, false}, k_strided_first_words[1]},
            strided_start{pattern_spec{kind, k_seed, 3U, true}, k_strided_first_words[1]}};
}

/** One kernel set for the visit-order identity test. */
class KernelsStridedOrder : public ::testing::TestWithParam<std::uint32_t> {
protected:
    void SetUp() override {
        require_isa(GetParam());
        if (IsSkipped()) {
            return;
        }
        ASSERT_TRUE(buffer_.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    }

    test_buffer buffer_{k_strided_buffer_words};
};

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
 * Strided kernels: every set against expected_value, the AVX sets also against SSE2
 * ------------------------------------------------------------------------------------------- */

TEST_P(KernelsStrided, FillStreamStridedWritesExactlyTheVisitedLines) {
    // Every start, stride and slice over the 2 MiB and 6 MiB spans, with the SSE2 result
    // compared byte for byte; the 64 MiB window once per stride, whole phase.
    for (const strided_start& start : strided_starts_of(kind())) {
        for (const std::uint64_t span_bytes : k_small_strided_spans) {
            for (const std::uint64_t stride : k_bank_strides) {
                for (const strided_visits& visits : visit_slices(span_bytes, stride)) {
                    const strided_case c{isa(), start.spec, span_bytes, start.first_word_index,
                                         visits};
                    const auto words = static_cast<std::size_t>(span_bytes / 8U);
                    const std::uint64_t* const out = run_strided_fill(tested(), tested_buffer_, c);
                    ASSERT_TRUE(guards_intact(out, words)) << where(c);
                    ASSERT_TRUE(strided_fill_correct(out, c)) << where(c);
                    if (compare_with_sse2()) {
                        const std::uint64_t* const reference =
                            run_strided_fill(k_kernels_sse2, reference_buffer_, c);
                        ASSERT_EQ(std::memcmp(out, reference, words * sizeof(std::uint64_t)), 0)
                            << "differs from the SSE2 reference, " << where(c);
                    }
                }
            }
        }
    }
    const pattern_spec spec{kind(), k_seed, 1U, false};
    for (const std::uint64_t stride : k_bank_strides) {
        const strided_case c{isa(), spec, k_largest_strided_span, k_strided_first_words[1],
                             visit_slices(k_largest_strided_span, stride).front()};
        const std::uint64_t* const out = run_strided_fill(tested(), tested_buffer_, c);
        ASSERT_TRUE(guards_intact(out, static_cast<std::size_t>(c.span_bytes / 8U))) << where(c);
        ASSERT_TRUE(strided_fill_correct(out, c)) << where(c);
    }
}

TEST_P(KernelsStrided, VerifyStridedAcceptsCorrectDataWithoutCallingTheSink) {
    std::uint64_t* const s = span(tested_buffer_);
    for (const strided_start& start : strided_starts_of(kind())) {
        for (const std::uint64_t span_bytes : k_small_strided_spans) {
            for (const std::uint64_t stride : k_bank_strides) {
                for (const strided_visits& visits : visit_slices(span_bytes, stride)) {
                    const strided_case c{isa(), start.spec, span_bytes, start.first_word_index,
                                         visits};
                    write_visited_lines(s, c);
                    for (const kernel_table* table : {&tested(), &k_kernels_sse2}) {
                        recording_sink sink;
                        ASSERT_EQ(
                            table->verify_strided(s, visits, c.spec, c.first_word_index, sink), 0U)
                            << isa_display_name(table->isa) << " set, " << where(c);
                        ASSERT_TRUE(sink.reports().empty() && sink.overflow() == 0U)
                            << sink.reports().size() << " sink calls, "
                            << isa_display_name(table->isa) << " set, " << where(c);
                    }
                }
            }
        }
    }
}

TEST_P(KernelsStrided, VerifyStridedReportsCorruptionsInVisitOrderLikeSse2) {
    std::uint64_t* const s = span(tested_buffer_);
    const auto run = [&](const strided_case& c) {
        write_visited_lines(s, c);
        const std::vector<observed_mismatch> expected = corrupt_visited_lines(s, c);
        for (const kernel_table* table : {&tested(), &k_kernels_sse2}) {
            recording_sink sink;
            ASSERT_EQ(table->verify_strided(s, c.visits, c.spec, c.first_word_index, sink),
                      static_cast<std::uint64_t>(expected.size()))
                << isa_display_name(table->isa) << " set, " << where(c);
            ASSERT_TRUE(strided_reports_match(sink, s, expected))
                << isa_display_name(table->isa) << " set, " << where(c);
        }
    };
    for (const strided_start& start : strided_starts_of(kind())) {
        for (const std::uint64_t span_bytes : k_small_strided_spans) {
            for (const std::uint64_t stride : k_bank_strides) {
                for (const strided_visits& visits : visit_slices(span_bytes, stride)) {
                    ASSERT_NO_FATAL_FAILURE(run(strided_case{isa(), start.spec, span_bytes,
                                                             start.first_word_index, visits}));
                }
            }
        }
    }
    // The 64 MiB window: the mid-sweep slice and a work group, at every stride.
    const pattern_spec spec{kind(), k_seed, 2U, true};
    for (const std::uint64_t stride : k_bank_strides) {
        const std::vector<strided_visits> slices = visit_slices(k_largest_strided_span, stride);
        for (std::size_t i = 1; i < slices.size(); ++i) {
            ASSERT_NO_FATAL_FAILURE(run(strided_case{isa(), spec, k_largest_strided_span,
                                                     k_strided_first_words[1], slices[i]}));
        }
    }
}

TEST_P(KernelsStrided, ZeroVisitsAreANoOpWhateverTheOtherFields) {
    const pattern_spec spec{kind(), k_seed, 0U, false};
    constexpr std::size_t words = k_line_words * 4U;
    const strided_visits empty_calls[] = {
        strided_visits{0U, 0U, 0U, 0U},
        strided_visits{k_bank_strides[0], 256U, 7U, 0U},
    };
    std::uint64_t* const s = span(tested_buffer_);
    for (const strided_visits& visits : empty_calls) {
        const strided_case c{isa(), spec, words * sizeof(std::uint64_t), 0U, visits};
        std::fill(s, s + words, k_guard_value);
        arm_guards(s, words);
        tested().fill_stream_strided(s, visits, spec, 0U);
        tested().full_fence();
        recording_sink sink;
        // Every word mismatches spec, so a single load would be reported.
        ASSERT_EQ(tested().verify_strided(s, visits, spec, 0U, sink), 0U) << where(c);
        ASSERT_TRUE(sink.reports().empty()) << where(c);
        ASSERT_TRUE(guards_intact(s, words)) << where(c);
        ASSERT_TRUE(std::all_of(s, s + words, [](std::uint64_t w) { return w == k_guard_value; }))
            << "a zero-visit fill wrote memory, " << where(c);
    }
}

INSTANTIATE_TEST_SUITE_P(KernelsStridedSetsAndKinds, KernelsStrided,
                         ::testing::Combine(::testing::ValuesIn(k_all_isas),
                                            ::testing::ValuesIn(k_all_kinds)),
                         kernel_param_name);

// The strided kernels' visit sequence is the BankStride order itself: issued work group by work
// group as the step runner issues it, over a span in which every word mismatches, the line
// sequence the reports reveal must equal order_cursor's BankStride ranges, in order.
TEST_P(KernelsStridedOrder, VisitSequenceEqualsTheBankStrideCursor) {
    const kernel_table& table = table_of(GetParam());
    std::uint64_t* const s = buffer_.data() + k_guard_words;
    constexpr std::uint64_t spans[] = {2U * k_strided_mib, 6U * k_strided_mib,
                                       k_largest_strided_span};
    for (const std::uint64_t span_bytes : spans) {
        for (std::uint32_t pass = 0; pass < 3U; ++pass) {
            const std::uint64_t stride = bank_stride_bytes(pass);
            const std::uint64_t lines = span_bytes / k_cache_line_bytes;
            const pattern_spec spec{RQ_PATTERN_RANDOM, k_seed, pass, false};
            const pattern_spec complement{RQ_PATTERN_RANDOM, k_seed, pass, true};
            const std::uint64_t first = 4097U * k_line_words;
            k_kernels_sse2.fill_stream(s, static_cast<std::size_t>(span_bytes / 8U), complement,
                                       first, 1);
            k_kernels_sse2.full_fence();

            line_order_sink sink(s, static_cast<std::size_t>(lines));
            std::uint64_t mismatches = 0;
            for (std::uint64_t group = 0; group < lines / k_group_visits; ++group) {
                const strided_visits visits{stride, span_bytes / stride, group * k_group_visits,
                                            k_group_visits};
                mismatches += table.verify_strided(s, visits, spec, first, sink);
            }
            const std::string where_text = std::string(isa_display_name(GetParam())) + ", span " +
                                           std::to_string(span_bytes / k_strided_mib) +
                                           " MiB, pass " + std::to_string(pass);
            ASSERT_EQ(mismatches, lines * k_line_words) << where_text;
            ASSERT_EQ(sink.reports(), lines * k_line_words) << where_text;
            ASSERT_EQ(sink.overflow(), 0U) << where_text;
            ASSERT_EQ(sink.broken_lines(), 0U)
                << "reports not eight ascending words per line, " << where_text;
            ASSERT_EQ(sink.wrong_values(), 0U) << where_text;

            order_cursor cursor(
                order_params{RQ_ORDER_BANK_STRIDE, span_bytes, k_seed, 0U, pass, 0U});
            ASSERT_EQ(cursor.total_ranges(), lines) << where_text;
            order_range range{};
            std::size_t k = 0;
            while (cursor.next(range)) {
                ASSERT_EQ(range.length, k_cache_line_bytes) << where_text;
                ASSERT_EQ(sink.line_offsets()[k], range.offset)
                    << "visit " << k << " differs from the cursor, " << where_text;
                ++k;
            }
            ASSERT_EQ(k, sink.line_offsets().size()) << where_text;
        }
    }
}

INSTANTIATE_TEST_SUITE_P(KernelsStridedAllSets, KernelsStridedOrder,
                         ::testing::ValuesIn(k_all_isas), isa_param_name);

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
    EXPECT_TRUE(table.fill_stream_strided != nullptr);
    EXPECT_TRUE(table.verify_strided != nullptr);
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
            EXPECT_NE(sets[a]->fill_stream_strided, sets[b]->fill_stream_strided)
                << isa_display_name(sets[a]->isa) << " and " << isa_display_name(sets[b]->isa);
            EXPECT_NE(sets[a]->verify_strided, sets[b]->verify_strided)
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
 * Flush and re-read helpers: the instructions their built bodies execute
 * ------------------------------------------------------------------------------------------- */

// The value tests above pass for a flush_range that flushes nothing and for a re-read that loads
// before it fences, because write-back memory returns the stored data either way. These tests
// observe the instructions instead. With the trap flag (EFLAGS.TF) set, the CPU raises a
// single-step exception after every instruction, and its context holds the address of the next
// one; a vectored handler decodes that instruction before it executes and records the fences,
// the flushes with the line each one names, and the loads of the test page. A trace of the
// running code holds in both configurations, where a scan of its bytes would not: Release
// inlines the line loop into each flush_range, while Debug calls it out of line and reloads
// every local from the stack. Only a trace sees which lines a flush loop actually names.

namespace {

/** EFLAGS.TF: while set, the CPU raises a single-step trap after every instruction. */
constexpr DWORD k_trap_flag = 0x100U;

/** The longest x64 instruction, in bytes. */
constexpr std::size_t k_max_instruction_bytes = 15U;

/**
 * Instructions one traced call may execute before the trace is abandoned as runaway. Every
 * traced call executes far fewer, Debug's /JMC and /RTC checks included.
 */
constexpr std::size_t k_trace_step_limit = 100000U;

/** Events one traced call may record; the longest expected trace holds four. */
constexpr std::size_t k_trace_event_capacity = 64U;

/**
 * A debugger receives every single-step exception first-chance, before any vectored handler,
 * and consumes it, so the trace cannot run under one.
 */
constexpr char k_debugger_skip[] =
    "a debugger receives every single-step exception first, so no instruction trace can run";

/** First line of the traced region: lines on both sides stay inside the test page. */
constexpr std::size_t k_traced_first_line = 4U;

/** The instructions a trace records. */
enum class traced_op : std::uint8_t {
    mfence,
    sfence,
    lfence,
    clflush,
    clflushopt,
    /** CLWB writes a line back but may leave it cached, so it never serves as the flush. */
    clwb,
    /** MOV r, m (opcode 8B) reading the watched page. */
    load,
};

const char* op_name(traced_op op) noexcept {
    switch (op) {
    case traced_op::mfence:
        return "MFENCE";
    case traced_op::sfence:
        return "SFENCE";
    case traced_op::lfence:
        return "LFENCE";
    case traced_op::clflush:
        return "CLFLUSH";
    case traced_op::clflushopt:
        return "CLFLUSHOPT";
    case traced_op::clwb:
        return "CLWB";
    case traced_op::load:
        return "LOAD";
    default:
        return "OutOfRange";
    }
}

/** One recorded instruction. */
struct traced_event {
    traced_op op;
    /**
     * For a flush, the 64-byte line its operand lies in, which is the line the CPU flushes; for
     * a load, its exact effective address; 0 for a fence.
     */
    std::uintptr_t address;
    /** Bytes a load reads; 0 for a fence or flush. */
    std::uint32_t width;

    bool operator==(const traced_event&) const = default;
};

void PrintTo(const traced_event& event, std::ostream* os) {
    *os << op_name(event.op);
    if (event.op == traced_op::load) {
        *os << " of " << event.width << " bytes at " << hex64(event.address);
    } else if (event.address != 0U) {
        *os << " of line " << hex64(event.address);
    }
}

/**
 * Line address recorded for an FS- or GS-relative flush. The segment base is not in CONTEXT,
 * so the line cannot be resolved, and this value matches no line a test expects.
 */
constexpr std::uintptr_t k_unresolved_line = ~std::uintptr_t{k_cache_line_bytes - 1U};

/** The 64-byte line an address lies in. */
constexpr std::uintptr_t line_of(std::uintptr_t address) noexcept {
    return address & ~std::uintptr_t{k_cache_line_bytes - 1U};
}

/**
 * State the vectored handler records into, global because a vectored handler takes no context
 * argument. Only the thread named here is traced, and only while `active` holds; the handler
 * runs on that thread, so the other fields need no synchronisation. The event storage is fixed,
 * so the handler never allocates.
 */
struct trace_record {
    std::atomic<bool> active{false};
    DWORD thread_id = 0;
    /** RIP at which the trace ends: trace_end_marker's entry. */
    std::uintptr_t stop_at = 0;
    /** Loads are recorded only inside [watch_begin, watch_end), the test page. */
    std::uintptr_t watch_begin = 0;
    std::uintptr_t watch_end = 0;
    std::array<traced_event, k_trace_event_capacity> events{};
    std::size_t event_count = 0;
    std::size_t steps = 0;
    bool events_overflowed = false;
    bool step_limit_reached = false;
    bool reached_stop = false;
};

constinit trace_record current_trace{};

/** Calls of trace_end_marker, the counter that gives the marker a body of its own. */
std::atomic<std::uint64_t> trace_end_marker_calls{0U};

/**
 * Where every traced call ends: the handler stops tracing at this function's entry. Its body
 * is unique, so /OPT:ICF cannot fold it into a function the traced call runs, which would end
 * the trace early.
 */
__declspec(noinline) void trace_end_marker() noexcept {
    trace_end_marker_calls.fetch_add(1U, std::memory_order_relaxed);
}

/** The general-purpose register a ModRM, SIB or REX-extended register number names. */
std::uint64_t register_value(const CONTEXT& context, unsigned number) noexcept {
    switch (number) {
    case 0U:
        return context.Rax;
    case 1U:
        return context.Rcx;
    case 2U:
        return context.Rdx;
    case 3U:
        return context.Rbx;
    case 4U:
        return context.Rsp;
    case 5U:
        return context.Rbp;
    case 6U:
        return context.Rsi;
    case 7U:
        return context.Rdi;
    case 8U:
        return context.R8;
    case 9U:
        return context.R9;
    case 10U:
        return context.R10;
    case 11U:
        return context.R11;
    case 12U:
        return context.R12;
    case 13U:
        return context.R13;
    case 14U:
        return context.R14;
    default:
        return context.R15;
    }
}

/**
 * Effective address of the memory operand whose ModRM byte (mod != 11) is code[modrm_at], in
 * an instruction with no immediate, so a RIP-relative operand is relative to the end of its
 * displacement.
 */
std::uintptr_t memory_operand_address(const CONTEXT& context, const std::uint8_t* code,
                                      std::size_t modrm_at, std::uint8_t rex,
                                      bool address_size_override) noexcept {
    const std::uint8_t modrm = code[modrm_at];
    const unsigned mod = modrm >> 6U;
    const unsigned rm = modrm & 7U;
    const unsigned rex_x = (rex & 0x2U) != 0U ? 8U : 0U;
    const unsigned rex_b = (rex & 0x1U) != 0U ? 8U : 0U;
    std::size_t next = modrm_at + 1U;
    std::uint64_t address = 0U;
    bool disp32 = mod == 2U;
    bool rip_relative = false;
    if (rm == 4U) {
        const std::uint8_t sib = code[next++];
        const unsigned index = ((sib >> 3U) & 7U) | rex_x;
        const unsigned base = sib & 7U;
        // Index 100 without REX.X means no index, since RSP cannot be one.
        if (index != 4U) {
            address += register_value(context, index) << (sib >> 6U);
        }
        // Base 101 with mod 00 means a 32-bit displacement and no base, whatever REX.B holds.
        if (base == 5U && mod == 0U) {
            disp32 = true;
        } else {
            address += register_value(context, base | rex_b);
        }
    } else if (rm == 5U && mod == 0U) {
        rip_relative = true;
        disp32 = true;
    } else {
        address += register_value(context, rm | rex_b);
    }
    if (mod == 1U) {
        address += static_cast<std::uint64_t>(static_cast<std::int8_t>(code[next]));
        next += 1U;
    } else if (disp32) {
        std::int32_t displacement = 0;
        std::memcpy(&displacement, code + next, sizeof(displacement));
        address += static_cast<std::uint64_t>(static_cast<std::int64_t>(displacement));
        next += sizeof(displacement);
    }
    if (rip_relative) {
        address += context.Rip + next;
    }
    return address_size_override ? address & 0xFFFFFFFFU : address;
}

/** Appends one event; past the capacity it only flags the overflow. */
void record_event(traced_op op, std::uintptr_t address, std::uint32_t width) noexcept {
    if (current_trace.event_count == current_trace.events.size()) {
        current_trace.events_overflowed = true;
        return;
    }
    current_trace.events[current_trace.event_count++] = traced_event{op, address, width};
}

/**
 * Records the instruction at context.Rip when it is a fence, a flush or a MOV load of the
 * watched page. Only the encodings of those instructions are decoded: the CPU reports the next
 * instruction's address itself, so no general length decoder is needed.
 */
void record_instruction(const CONTEXT& context) noexcept {
    const auto* const code = reinterpret_cast<const std::uint8_t*>(context.Rip);
    bool operand_size_override = false;
    bool repeat_prefix = false;
    bool address_size_override = false;
    bool fs_gs_override = false;
    std::uint8_t rex = 0U;
    std::size_t at = 0U;
    // Legacy prefixes in any order, then REX. A REX prefix counts only immediately before the
    // opcode; the CPU ignores one that another prefix follows, and so does this loop.
    for (; at < k_max_instruction_bytes; ++at) {
        const std::uint8_t byte = code[at];
        if ((byte & 0xF0U) == 0x40U) {
            rex = byte;
            continue;
        }
        if (byte == 0x66U) {
            operand_size_override = true;
        } else if (byte == 0xF2U || byte == 0xF3U) {
            repeat_prefix = true;
        } else if (byte == 0x67U) {
            address_size_override = true;
        } else if (byte == 0x64U || byte == 0x65U) {
            fs_gs_override = true;
        } else if (byte != 0xF0U && byte != 0x26U && byte != 0x2EU && byte != 0x36U &&
                   byte != 0x3EU) {
            // Any byte but LOCK or an ES, CS, SS or DS override, which x64 addressing ignores,
            // is the opcode.
            break;
        }
        rex = 0U;
    }
    if (at == k_max_instruction_bytes) {
        return;
    }
    if (code[at] == 0x0FU && code[at + 1U] == 0xAEU) {
        const std::size_t modrm_at = at + 2U;
        const std::uint8_t modrm = code[modrm_at];
        const unsigned reg = (modrm >> 3U) & 7U;
        if ((modrm >> 6U) == 3U) {
            // The fences carry no 66, F2 or F3 prefix; with one, the same ModRM encodes another
            // instruction (TPAUSE, UMWAIT, INCSSP, RDFSBASE and others).
            if (operand_size_override || repeat_prefix) {
                return;
            }
            if (reg == 5U) {
                record_event(traced_op::lfence, 0U, 0U);
            } else if (reg == 6U) {
                record_event(traced_op::mfence, 0U, 0U);
            } else if (reg == 7U) {
                record_event(traced_op::sfence, 0U, 0U);
            }
            return;
        }
        // Memory forms: /7 is CLFLUSH, and CLFLUSHOPT with 66; /6 with 66 is CLWB. The other
        // forms (FXSAVE, XSAVE, LDMXCSR and the like) and the F2 and F3 forms flush nothing.
        if (repeat_prefix || (reg != 7U && !(reg == 6U && operand_size_override))) {
            return;
        }
        const traced_op flush = reg == 6U               ? traced_op::clwb
                                : operand_size_override ? traced_op::clflushopt
                                                        : traced_op::clflush;
        const std::uintptr_t line =
            fs_gs_override ? k_unresolved_line
                           : line_of(memory_operand_address(context, code, modrm_at, rex,
                                                            address_size_override));
        record_event(flush, line, 0U);
        return;
    }
    // MOV r, m. On x64 Windows an FS- or GS-relative load reads the TEB, never the test page.
    if (code[at] == 0x8BU && (code[at + 1U] >> 6U) != 3U && !fs_gs_override) {
        const std::uintptr_t address =
            memory_operand_address(context, code, at + 1U, rex, address_size_override);
        const std::uint32_t width = (rex & 0x8U) != 0U ? 8U : operand_size_override ? 2U : 4U;
        if (address < current_trace.watch_end && address + width > current_trace.watch_begin) {
            record_event(traced_op::load, address, width);
        }
    }
}

/**
 * The vectored handler. It claims only single-step exceptions on the tracing thread while a
 * trace is active. It records into the fixed trace_record, re-arms the trap flag for the next
 * instruction, and clears it at trace_end_marker or at the step limit.
 */
LONG NTAPI record_single_step(EXCEPTION_POINTERS* info) noexcept {
    if (info->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP ||
        !current_trace.active.load(std::memory_order_acquire) ||
        GetCurrentThreadId() != current_trace.thread_id) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    CONTEXT& context = *info->ContextRecord;
    const bool at_stop = context.Rip == current_trace.stop_at;
    if (at_stop || current_trace.steps == k_trace_step_limit) {
        current_trace.reached_stop = at_stop;
        current_trace.step_limit_reached = !at_stop;
        context.EFlags &= ~k_trap_flag;
        current_trace.active.store(false, std::memory_order_release);
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    ++current_trace.steps;
    record_instruction(context);
    context.EFlags |= k_trap_flag;
    return EXCEPTION_CONTINUE_EXECUTION;
}

/**
 * Runs body(context) with the trap flag set, then calls trace_end_marker, where the handler
 * ends the trace. The CPU raises no trap after the instruction that sets TF, only after the one
 * following it, so the single instruction that runs untraced is this trampoline's, at the
 * latest its call of body. Not inlined, so nothing of the caller runs while TF is set.
 */
__declspec(noinline) void run_traced(void (*body)(void*), void* context) noexcept {
    // Sequentially consistent: the store must not sink below the flag write.
    current_trace.active.store(true);
    __writeeflags(__readeflags() | k_trap_flag);
    body(context);
    trace_end_marker();
}

/** Calls a traced call object through the type-erased pointer run_traced takes. */
template <class Call>
void invoke_call(void* call) noexcept {
    (*static_cast<Call*>(call))();
}

/** Removes a vectored exception handler. */
struct vectored_handler_remover {
    void operator()(void* handler) const noexcept { RemoveVectoredExceptionHandler(handler); }
};

/**
 * One test's tracer: installs record_single_step first in the vectored handler chain on
 * construction and removes it on destruction. Loads are recorded only inside the watched page,
 * which keeps the traced code's own stack traffic (every local in Debug) out of the record;
 * fences and flushes are recorded wherever they point.
 */
class instruction_trace {
public:
    instruction_trace(const void* watched, std::size_t watched_bytes) noexcept
        : handler_(AddVectoredExceptionHandler(1U, &record_single_step)),
          watch_begin_(reinterpret_cast<std::uintptr_t>(watched)),
          watch_end_(watch_begin_ + watched_bytes) {}

    bool installed() const noexcept { return handler_ != nullptr; }

    /**
     * Executes call() with every instruction traced and keeps the events for events().
     *
     * @return Failure when the trace did not run to trace_end_marker or lost events.
     */
    template <class Call>
    ::testing::AssertionResult run(Call call) {
        if (!installed()) {
            return ::testing::AssertionFailure() << "no vectored exception handler is installed";
        }
        current_trace.thread_id = GetCurrentThreadId();
        current_trace.stop_at = reinterpret_cast<std::uintptr_t>(&trace_end_marker);
        current_trace.watch_begin = watch_begin_;
        current_trace.watch_end = watch_end_;
        current_trace.event_count = 0U;
        current_trace.steps = 0U;
        current_trace.events_overflowed = false;
        current_trace.step_limit_reached = false;
        current_trace.reached_stop = false;
        run_traced(&invoke_call<Call>, &call);
        current_trace.active.store(false, std::memory_order_release);
        events_.assign(current_trace.events.begin(),
                       current_trace.events.begin() +
                           static_cast<std::ptrdiff_t>(current_trace.event_count));
        if (current_trace.step_limit_reached) {
            return ::testing::AssertionFailure()
                   << "the traced call ran past " << k_trace_step_limit << " instructions";
        }
        if (!current_trace.reached_stop) {
            return ::testing::AssertionFailure()
                   << "the trace never reached its end marker: no single-step exception arrived";
        }
        if (current_trace.events_overflowed) {
            return ::testing::AssertionFailure()
                   << "the traced call executed more than " << k_trace_event_capacity
                   << " recorded instructions";
        }
        return ::testing::AssertionSuccess() << current_trace.steps << " instructions traced";
    }

    /** The recorded instructions of the last run, in execution order. */
    const std::vector<traced_event>& events() const noexcept { return events_; }

private:
    std::unique_ptr<void, vectored_handler_remover> handler_;
    std::uintptr_t watch_begin_;
    std::uintptr_t watch_end_;
    std::vector<traced_event> events_;
};

traced_event fence_event(traced_op fence) noexcept {
    return traced_event{fence, 0U, 0U};
}

traced_event flush_event(traced_op flush, const void* address) noexcept {
    return traced_event{flush, line_of(reinterpret_cast<std::uintptr_t>(address)), 0U};
}

/** The flush instruction a helper pair executes. */
traced_op flush_kind(const flush_helpers& helpers) noexcept {
    return helpers.needs_clflushopt ? traced_op::clflushopt : traced_op::clflush;
}

/** The flush instruction of the helpers tables_for(selection) installs. */
traced_op flush_kind(const isa_selection& selection) noexcept {
    return selection.clflushopt ? traced_op::clflushopt : traced_op::clflush;
}

/**
 * The re-read rule, instruction by instruction: MFENCE, a flush of the word's line, MFENCE,
 * then the one 64-bit load of the word.
 */
std::vector<traced_event> reread_sequence(traced_op flush, const std::uint64_t* word) {
    return {fence_event(traced_op::mfence), flush_event(flush, word),
            fence_event(traced_op::mfence),
            traced_event{traced_op::load, reinterpret_cast<std::uintptr_t>(word),
                         static_cast<std::uint32_t>(sizeof(std::uint64_t))}};
}

/** One flush_range call, positioned within the traced lines, and the lines it overlaps. */
struct flush_range_case {
    const char* name;
    /** Bytes from the first traced line's start to the range's first byte. */
    std::size_t offset;
    std::size_t bytes;
    /** Index of the first line the range overlaps, from the first traced line. */
    std::size_t first_line;
    std::size_t line_count;
};

void PrintTo(const flush_range_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr flush_range_case k_flush_range_cases[] = {
    // 13 bytes into line 0 through 7 bytes into line 3, the value test's range: both ends fall
    // inside a line, so both partial lines must be flushed.
    {"UnalignedStartAndEnd", 13U, 3U * k_cache_line_bytes - 6U, 0U, 4U},
    {"ZeroBytes", 13U, 0U, 0U, 0U},
    {"LastByteOfALine", 2U * k_cache_line_bytes - 1U, 1U, 1U, 1U},
    // The end is line 2's first byte, which lies outside the range.
    {"TwoWholeLines", 0U, 2U * k_cache_line_bytes, 0U, 2U},
    {"TwoBytesAcrossALineBoundary", k_cache_line_bytes - 1U, 2U, 0U, 2U},
};

using flush_range_param = std::tuple<flush_helpers, flush_range_case>;

std::string flush_range_param_name(const ::testing::TestParamInfo<flush_range_param>& info) {
    return std::string(std::get<0>(info.param).name) + "_" + std::get<1>(info.param).name;
}

/**
 * Fixture of the trace tests. Skips under a debugger and, for a CLFLUSHOPT pair, on a CPU
 * without CLFLUSHOPT; commits the page the traced calls address and installs the tracer over
 * it.
 */
template <class Base>
class trace_fixture : public Base {
protected:
    /** Called from SetUp, so a skip or failure here keeps the test body from running. */
    void set_up(bool needs_clflushopt) {
        if (IsDebuggerPresent()) {
            GTEST_SKIP() << k_debugger_skip;
        }
        if (needs_clflushopt && !host_selection().clflushopt) {
            GTEST_SKIP() << "CPU lacks CLFLUSHOPT";
        }
        ASSERT_TRUE(page_.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
        ASSERT_TRUE(trace_.installed()) << "AddVectoredExceptionHandler failed";
    }

    /** The first traced line, k_traced_first_line lines into the page. */
    std::uint64_t* traced_lines() noexcept {
        return page_.data() + k_traced_first_line * k_line_words;
    }

    test_buffer page_{k_page_words};
    instruction_trace trace_{page_.data(), k_page_words * sizeof(std::uint64_t)};
};

/** The fence helper and real_memory_access over the host's tables. */
class kernels_traced : public trace_fixture<::testing::Test> {
protected:
    void SetUp() override { set_up(false); }
};

/** One flush and re-read pair. */
class kernels_traced_helpers : public trace_fixture<::testing::TestWithParam<flush_helpers>> {
protected:
    void SetUp() override { set_up(GetParam().needs_clflushopt); }
};

/** One flush and re-read pair's flush_range over one range case. */
class kernels_traced_flush_range
    : public trace_fixture<::testing::TestWithParam<flush_range_param>> {
protected:
    void SetUp() override { set_up(std::get<0>(GetParam()).needs_clflushopt); }
};

}  // namespace

TEST_P(kernels_traced_flush_range, FlushesEachOverlappingLineOnceInAscendingOrderWithoutAFence) {
    const auto& [helpers, range] = GetParam();
    const auto* const lines = reinterpret_cast<const unsigned char*>(traced_lines());
    std::vector<traced_event> expected;
    for (std::size_t line = range.first_line; line < range.first_line + range.line_count; ++line) {
        expected.push_back(flush_event(flush_kind(helpers), lines + line * k_cache_line_bytes));
    }
    const flush_range_fn flush_range = helpers.flush_range;
    const unsigned char* const start = lines + range.offset;
    const std::size_t bytes = range.bytes;
    ASSERT_TRUE(trace_.run([flush_range, start, bytes] { flush_range(start, bytes); }));
    EXPECT_EQ(trace_.events(), expected) << helpers.name << ", " << range.name;
}

INSTANTIATE_TEST_SUITE_P(KernelsBaselineHelpers, kernels_traced_flush_range,
                         ::testing::Combine(::testing::ValuesIn(k_flush_helpers),
                                            ::testing::ValuesIn(k_flush_range_cases)),
                         flush_range_param_name);

TEST_P(kernels_traced_helpers, RereadFencesFlushesTheWordsLineFencesThenLoadsItOnce) {
    // Word 3 of its line, so the flush must name the word's line from an address inside it.
    constexpr std::uint64_t value = 0x96E15A3C7EC3F00FULL;
    std::uint64_t* const word = traced_lines() + 3U;
    *static_cast<volatile std::uint64_t*>(word) = value;
    const reread_fn reread = GetParam().reread;
    std::uint64_t returned = 0U;
    ASSERT_TRUE(trace_.run([reread, word, &returned] { returned = reread(word); }));
    EXPECT_EQ(trace_.events(), reread_sequence(flush_kind(GetParam()), word)) << GetParam().name;
    EXPECT_EQ(returned, value) << GetParam().name;
}

INSTANTIATE_TEST_SUITE_P(KernelsBaselineHelpers, kernels_traced_helpers,
                         ::testing::ValuesIn(k_flush_helpers), param_name<flush_helpers>);

TEST_F(kernels_traced, FullFenceExecutesOneMfenceAndNothingElse) {
    const full_fence_fn full_fence = &full_fence_mfence;
    ASSERT_TRUE(trace_.run([full_fence] { full_fence(); }));
    EXPECT_EQ(trace_.events(), std::vector<traced_event>{fence_event(traced_op::mfence)});
}

// The runner's primitives over the table a session uses: counting_memory_access logs these
// calls, and test_step_runner.cpp asserts the flush-before-verification rule on that log, so
// these traces tie each logged fence, flush and re-read to the instructions it executes.
TEST_F(kernels_traced, RealAccessFenceExecutesOneMfence) {
    real_memory_access access{tables_for(host_selection())};
    IMemoryAccess& layer = access;
    const access_context ctx{};
    ASSERT_TRUE(trace_.run([&layer, &ctx] { layer.fence(ctx); }));
    EXPECT_EQ(trace_.events(), std::vector<traced_event>{fence_event(traced_op::mfence)});
}

TEST_F(kernels_traced, RealAccessFlushFlushesEveryLineOfItsWords) {
    const isa_selection selection = host_selection();
    real_memory_access access{tables_for(selection)};
    IMemoryAccess& layer = access;
    const access_context ctx{};
    // 26 words from word 3 of line 0 are bytes 24 to 231, lines 0 to 3. Passing the word count
    // as a byte count would flush line 0 alone.
    std::uint64_t* const lines = traced_lines();
    std::uint64_t* const words = lines + 3U;
    std::vector<traced_event> expected;
    for (std::size_t line = 0U; line < 4U; ++line) {
        expected.push_back(flush_event(flush_kind(selection), lines + line * k_line_words));
    }
    ASSERT_TRUE(trace_.run([&layer, &ctx, words] { layer.flush(ctx, words, 26U); }));
    EXPECT_EQ(trace_.events(), expected);
}

TEST_F(kernels_traced, RealAccessRereadWordFencesFlushesTheLineFencesThenLoadsOnce) {
    const isa_selection selection = host_selection();
    real_memory_access access{tables_for(selection)};
    IMemoryAccess& layer = access;
    const access_context ctx{};
    constexpr std::uint64_t value = 0x0FF0C3A5965A3CE1ULL;
    std::uint64_t* const word = traced_lines() + 5U;
    *static_cast<volatile std::uint64_t*>(word) = value;
    std::uint64_t returned = 0U;
    ASSERT_TRUE(
        trace_.run([&layer, &ctx, word, &returned] { returned = layer.reread_word(ctx, word); }));
    EXPECT_EQ(trace_.events(), reread_sequence(flush_kind(selection), word));
    EXPECT_EQ(returned, value);
}

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
    EXPECT_EQ(table.fill_stream_strided, expected_set.fill_stream_strided);
    EXPECT_EQ(table.verify_strided, expected_set.verify_strided);
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
