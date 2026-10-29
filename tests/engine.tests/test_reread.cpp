/**
 * @file test_reread.cpp
 * @brief Error confirmation tests: the flushed re-read of a mismatching word and its
 *        classification.
 *
 * - RereadClassification: classify_reread as a pure function, over value pairs that include 0,
 *   UINT64_MAX, single-bit and multi-bit differences.
 * - RereadCollector and RereadCollectorPath: error_collector's mismatch path over a substitute
 *   layer that scripts the re-read value. Each mismatch is re-read exactly once, through the
 *   outermost layer, with the verification's context in which only the phase changes, and the
 *   record keeps the raw expected, actual and re-read values with the inferred outcome. Every
 *   outcome is a confirmed error.
 * - RereadRealLayer and RereadRealPath: real_memory_access::reread_word over the host's kernel
 *   table, alone and under counting_memory_access, including a mismatch found by a real
 *   verification, which re-reads the stored wrong value (RQ_REREAD_REPEATS_ACTUAL). Re-reads
 *   never count as verified bytes.
 *
 * The direct fence, flush, fence and load behaviour of reread_clflush and reread_clflushopt, and
 * tables_for as a pure function, are covered by test_kernels.cpp; cap, batching, totals and
 * Stop on error by test_error_collector.cpp.
 *
 * Memory is one committed, unlocked 2 MiB VirtualAlloc region per test. Each re-read touches its
 * line once: no flush-and-reload loop over a small address set, the access style the product's
 * stress bound excludes. Nothing here depends on the host CPU's instruction sets beyond what
 * select_isa reports, so no test is skipped.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <ostream>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/patterns/patterns.h"
#include "core/kernels/kernels.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/real_memory_access.h"
#include "core/errors/error_collector.h"
#include "fakes/fake_clock.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

/** Bytes of each test region. */
constexpr std::size_t k_region_bytes = std::size_t{2} << 20U;

/** Bytes of the window the real-path tests stream and verify: half the region. */
constexpr std::size_t k_window_bytes = std::size_t{1} << 20U;
constexpr std::size_t k_window_words = k_window_bytes / sizeof(std::uint64_t);

/** Words per 64-byte cache line. */
constexpr std::size_t k_line_words = k_cache_line_bytes / sizeof(std::uint64_t);

/** Word the mismatch tests report, so its logical region offset is 800 bytes. */
constexpr std::size_t k_mismatch_word = 100U;
constexpr std::uint64_t k_mismatch_offset = k_mismatch_word * sizeof(std::uint64_t);

/**
 * Steps, step and pass of every context. Nonzero step, pass and (in RereadCollector) worker
 * values make "preserved" distinguishable from "zeroed".
 */
constexpr std::uint32_t k_step_count = 3U;
constexpr std::uint32_t k_step = 2U;
constexpr std::uint32_t k_pass = 2U;

/** Detail cap the application passes. */
constexpr std::uint32_t k_detail_cap = 10'000U;

constexpr std::uint64_t k_seed = 0x0123456789ABCDEFULL;

constexpr std::uint64_t k_ns_per_us = 1'000U;
constexpr std::uint64_t k_ns_per_ms = 1'000'000U;

/** Age of the written data at detection in the collector tests: 1 ms after test time zero. */
constexpr std::uint64_t k_write_after_t0_ns = k_ns_per_ms;

/** Fake time from test time zero to the mismatch: 7.35 ms. */
constexpr std::uint64_t k_detection_after_t0_ns = 7'350'000U;

/** The bit the real-path corruption flips. */
constexpr std::uint64_t k_flipped_bit = std::uint64_t{1} << 17U;

/** Words re-read in the streamed-window test, and the stride between them. */
constexpr std::size_t k_spread_rereads = 16U;
constexpr std::size_t k_spread_stride_words = k_window_words / k_spread_rereads;

/** Logical index of the streamed window's first word: not 0, so index and offset differ. */
constexpr std::uint64_t k_spread_first_word_index = 4097U;

static_assert(2U * k_window_bytes <= k_region_bytes, "the window must fit the region");
static_assert(k_spread_stride_words >= k_line_words,
              "each spread re-read must fall on its own cache line");

/* ---------------------------------------------------------------------------------------------
 * Names and failure context
 * ------------------------------------------------------------------------------------------- */

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

/** RQ_REREAD_* value as the class name the host shows. */
const char* outcome_name(std::uint32_t outcome) noexcept {
    switch (outcome) {
    case RQ_REREAD_MATCHES_EXPECTED:
        return "RereadMatchesExpected";
    case RQ_REREAD_REPEATS_ACTUAL:
        return "RereadRepeatsActual";
    case RQ_REREAD_DIFFERS:
        return "RereadDiffers";
    default:
        return "invalid outcome";
    }
}

const char* phase_name(phase_kind phase) noexcept {
    switch (phase) {
    case phase_kind::write:
        return "write";
    case phase_kind::pre_hold_flush:
        return "pre_hold_flush";
    case phase_kind::verify:
        return "verify";
    case phase_kind::sweep:
        return "sweep";
    case phase_kind::turnaround:
        return "turnaround";
    case phase_kind::reread:
        return "reread";
    }
    return "invalid phase";
}

const char* primitive_name(primitive_kind kind) noexcept {
    switch (kind) {
    case primitive_kind::stream_fill:
        return "stream_fill";
    case primitive_kind::fence:
        return "fence";
    case primitive_kind::flush:
        return "flush";
    case primitive_kind::verify:
        return "verify";
    case primitive_kind::verify_and_stream:
        return "verify_and_stream";
    case primitive_kind::reread_word:
        return "reread_word";
    }
    return "invalid primitive";
}

/** Virtual address of a word, as rq_error_record.address carries it. */
std::uint64_t address_of(const std::uint64_t* word) noexcept {
    return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(word));
}

/* ---------------------------------------------------------------------------------------------
 * Region, kernels and placements
 * ------------------------------------------------------------------------------------------- */

/** Releases a VirtualAlloc region. */
struct virtual_free_deleter {
    void operator()(std::uint64_t* words) const noexcept { VirtualFree(words, 0, MEM_RELEASE); }
};

/**
 * k_region_bytes of committed, unlocked, zeroed read-write memory, released on destruction. Its
 * base serves as access_context::region_base, so a word's logical offset is its byte distance
 * from the base.
 */
class region_buffer {
public:
    region_buffer()
        : words_(static_cast<std::uint64_t*>(
              VirtualAlloc(nullptr, k_region_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))) {}

    /** False when VirtualAlloc failed. */
    bool valid() const noexcept { return words_ != nullptr; }
    std::uint64_t* data() noexcept { return words_.get(); }
    std::uintptr_t base() const noexcept { return reinterpret_cast<std::uintptr_t>(words_.get()); }

private:
    std::unique_ptr<std::uint64_t, virtual_free_deleter> words_;
};

/** The table a session on this host uses: the automatic selection, patched by tables_for. */
kernel_table host_kernels() noexcept {
    return tables_for(select_isa(read_cpu_features(), RQ_ISA_AUTO));
}

/** A context in the k_step, k_pass verification of `worker` over `region`. */
access_context context_for(const region_buffer& region, phase_kind phase, std::uint32_t access_type,
                           std::uint32_t worker, std::uint64_t write_time_ns) noexcept {
    return access_context{k_step, k_pass, phase, access_type, worker, region.base(), write_time_ns};
}

/**
 * Collector options as the session builds them for the application: the 10,000-record cap,
 * Stop on error off, k_step_count steps, and `workers` placements from the host's processors.
 * The collector only copies placements into records, so a host with fewer processors than
 * workers repeats its last one.
 */
error_collector_options collector_options(std::uint32_t workers) {
    const std::vector<logical_processor> processors = enumerate_logical_processors();
    error_collector_options options;
    options.detail_cap = k_detail_cap;
    options.stop_on_error = false;
    options.step_count = k_step_count;
    for (std::uint32_t i = 0; i < workers; ++i) {
        options.placements.push_back(
            processors[i < processors.size() ? i : processors.size() - 1U]);
    }
    return options;
}

/* ---------------------------------------------------------------------------------------------
 * Substitute layer controlling the re-read value
 * ------------------------------------------------------------------------------------------- */

/** One reread_word call as scripted_reread_access received it. */
struct reread_call {
    access_context ctx;
    const std::uint64_t* word;
};

/**
 * Forwards every primitive to the layer it wraps, except reread_word, which returns a scripted
 * value instead of loading and logs the call with the context it received.
 *
 * The scripted value models what a flushed re-read can return after a mismatch: the expected
 * value, the first wrong value again, or a third value. Real memory without a fault can only
 * produce the second (a stored wrong value), so this layer is how the collector tests reach
 * every outcome. File-local because no other test needs it.
 *
 * reread_word is noexcept and may run on any worker, so its log is preallocated and indexed by
 * an atomic counter; calls beyond the log's capacity are still counted.
 */
class scripted_reread_access final : public IMemoryAccess {
public:
    scripted_reread_access(std::unique_ptr<IMemoryAccess> inner, std::uint64_t reread_value)
        : inner_(std::move(inner)), reread_value_(reread_value), calls_(k_call_capacity) {}

    void stream_fill(const access_context& ctx, std::uint64_t* words, std::size_t word_count,
                     const pattern_spec& spec, std::uint64_t first_word_index,
                     int direction) override {
        inner_->stream_fill(ctx, words, word_count, spec, first_word_index, direction);
    }

    void fence(const access_context& ctx) override { inner_->fence(ctx); }

    void flush(const access_context& ctx, const std::uint64_t* words,
               std::size_t word_count) override {
        inner_->flush(ctx, words, word_count);
    }

    std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                         std::size_t word_count, const pattern_spec& expected,
                         std::uint64_t first_word_index, int direction,
                         mismatch_sink& sink) override {
        return inner_->verify(ctx, words, word_count, expected, first_word_index, direction, sink);
    }

    std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                    std::size_t word_count, const pattern_spec& expected,
                                    const pattern_spec& next, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) override {
        return inner_->verify_and_stream(ctx, words, word_count, expected, next, first_word_index,
                                         direction, sink);
    }

    std::uint64_t reread_word(const access_context& ctx,
                              const std::uint64_t* word) noexcept override {
        const std::size_t slot = call_count_.fetch_add(1, std::memory_order_relaxed);
        if (slot < calls_.size()) {
            calls_[slot] = reread_call{ctx, word};
        }
        return reread_value_;
    }

    /** reread_word calls so far, logged or not. */
    std::size_t call_count() const noexcept { return call_count_.load(std::memory_order_relaxed); }

    /** The logged call at `index`, which must be below min(call_count(), k_call_capacity). */
    const reread_call& call(std::size_t index) const noexcept { return calls_[index]; }

private:
    static constexpr std::size_t k_call_capacity = 16U;

    std::unique_ptr<IMemoryAccess> inner_;
    const std::uint64_t reread_value_;
    std::vector<reread_call> calls_;
    std::atomic<std::size_t> call_count_{0};
};

/* ---------------------------------------------------------------------------------------------
 * Parameters
 * ------------------------------------------------------------------------------------------- */

/** An expected value and a different first value, as a verification reports them. */
struct value_pair {
    const char* name;
    std::uint64_t expected;
    std::uint64_t actual;
};

void PrintTo(const value_pair& pair, std::ostream* os) {
    *os << pair.name << " (expected " << hex64(pair.expected) << ", actual " << hex64(pair.actual)
        << ")";
}

constexpr value_pair k_value_pairs[] = {
    {"ZeroGainsBitZero", 0U, 1U},
    {"ZeroBecomesAllOnes", 0U, UINT64_MAX},
    {"AllOnesLosesBit63", UINT64_MAX, UINT64_MAX ^ (std::uint64_t{1} << 63U)},
    {"CheckerboardInverted", 0x5555555555555555ULL, 0xAAAAAAAAAAAAAAAAULL},
    {"RandomWordFlipsBit17", 0x5A3C96E1F00F7EC3ULL, 0x5A3C96E1F00F7EC3ULL ^ k_flipped_bit},
    {"RandomWordFlipsSixBits", 0x0123456789ABCDEFULL,
     0x0123456789ABCDEFULL ^ 0x00F0000000000F00ULL},
};

/**
 * Re-read values that equal neither value of the pair: candidates around both values and both
 * extremes, minus any that coincide with one of them.
 */
std::vector<std::uint64_t> third_values(const value_pair& pair) {
    const std::uint64_t e = pair.expected;
    const std::uint64_t a = pair.actual;
    const std::uint64_t candidates[] = {0U,
                                        1U,
                                        UINT64_MAX,
                                        ~e,
                                        ~a,
                                        e ^ a,
                                        e ^ (std::uint64_t{1} << 40U),
                                        a ^ (std::uint64_t{1} << 40U),
                                        e ^ 0x8000000000000001ULL};
    std::vector<std::uint64_t> values;
    for (const std::uint64_t candidate : candidates) {
        if (candidate != e && candidate != a) {
            values.push_back(candidate);
        }
    }
    return values;
}

/** One mismatch with the value the scripted re-read returns and the outcome it must yield. */
struct outcome_case {
    const char* name;
    std::uint64_t expected;
    std::uint64_t actual;
    std::uint64_t reread;
    std::uint32_t outcome;
};

void PrintTo(const outcome_case& c, std::ostream* os) {
    *os << c.name << " (expected " << hex64(c.expected) << ", actual " << hex64(c.actual)
        << ", re-read " << hex64(c.reread) << ", " << outcome_name(c.outcome) << ")";
}

constexpr std::uint64_t k_random_word = 0x5A3C96E1F00F7EC3ULL;
constexpr std::uint64_t k_multi_bit_word = 0x0123456789ABCDEFULL;

constexpr outcome_case k_outcome_cases[] = {
    {"MatchesExpectedOneBit", k_random_word, k_random_word ^ k_flipped_bit, k_random_word,
     RQ_REREAD_MATCHES_EXPECTED},
    {"MatchesExpectedAllBits", 0U, UINT64_MAX, 0U, RQ_REREAD_MATCHES_EXPECTED},
    {"RepeatsActualOneBit", k_random_word, k_random_word ^ k_flipped_bit,
     k_random_word ^ k_flipped_bit, RQ_REREAD_REPEATS_ACTUAL},
    {"RepeatsActualAllBits", UINT64_MAX, 0U, 0U, RQ_REREAD_REPEATS_ACTUAL},
    {"DiffersMultiBit", k_multi_bit_word, k_multi_bit_word ^ 0x00F0000000000F00ULL,
     k_multi_bit_word ^ 0x0000000000000100ULL, RQ_REREAD_DIFFERS},
    {"DiffersFromBothExtremes", 0U, UINT64_MAX, 0x5555555555555555ULL, RQ_REREAD_DIFFERS},
};

/** The phase and access type of the verification that found the mismatch. */
struct source_context {
    const char* name;
    phase_kind phase;
    std::uint32_t access_type;
};

void PrintTo(const source_context& source, std::ostream* os) {
    *os << source.name << " (phase " << phase_name(source.phase) << ", access type "
        << source.access_type << ")";
}

constexpr source_context k_source_contexts[] = {
    {"VerifyAfterWrite", phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE},
    {"VerifyAfterHold", phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_HOLD},
    {"Sweep", phase_kind::sweep, RQ_ACCESS_READ_MODIFY_WRITE},
    {"Turnaround", phase_kind::turnaround, RQ_ACCESS_TURNAROUND_READ},
};

using collector_param = std::tuple<outcome_case, source_context>;

std::string collector_param_name(const ::testing::TestParamInfo<collector_param>& info) {
    return std::string(std::get<0>(info.param).name) + "_" + std::get<1>(info.param).name;
}

std::string value_pair_name(const ::testing::TestParamInfo<value_pair>& info) {
    return info.param.name;
}

std::string clflushopt_name(const ::testing::TestParamInfo<bool>& info) {
    return info.param ? "Clflushopt" : "Clflush";
}

class RereadClassification : public ::testing::TestWithParam<value_pair> {};

class RereadCollector : public ::testing::TestWithParam<collector_param> {};

/** Parameter: whether the synthetic selection reports CLFLUSHOPT. */
class RereadRealLayer : public ::testing::TestWithParam<bool> {};

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * classify_reread: which first value the flushed re-read equals
 * ------------------------------------------------------------------------------------------- */

TEST_P(RereadClassification, ClassifiesByWhichFirstValueTheRereadEquals) {
    const value_pair& pair = GetParam();
    const std::uint64_t e = pair.expected;
    const std::uint64_t a = pair.actual;
    SCOPED_TRACE(::testing::Message() << "expected " << hex64(e) << ", actual " << hex64(a));

    EXPECT_EQ(classify_reread(e, a, e), RQ_REREAD_MATCHES_EXPECTED)
        << "re-read " << hex64(e) << " gave " << outcome_name(classify_reread(e, a, e))
        << ", not RereadMatchesExpected";
    EXPECT_EQ(classify_reread(e, a, a), RQ_REREAD_REPEATS_ACTUAL)
        << "re-read " << hex64(a) << " gave " << outcome_name(classify_reread(e, a, a))
        << ", not RereadRepeatsActual";

    const std::vector<std::uint64_t> others = third_values(pair);
    // Complementary pairs such as 0 and UINT64_MAX leave the fewest candidates: four.
    ASSERT_GE(others.size(), 4U) << "too few third values to exercise RereadDiffers";
    for (const std::uint64_t reread : others) {
        EXPECT_EQ(classify_reread(e, a, reread), RQ_REREAD_DIFFERS)
            << "re-read " << hex64(reread) << " gave "
            << outcome_name(classify_reread(e, a, reread)) << ", not RereadDiffers";
    }
}

INSTANTIATE_TEST_SUITE_P(RereadValuePairs, RereadClassification, ::testing::ValuesIn(k_value_pairs),
                         value_pair_name);

/* ---------------------------------------------------------------------------------------------
 * error_collector's mismatch path over a scripted re-read
 * ------------------------------------------------------------------------------------------- */

TEST_P(RereadCollector, RereadsOnceThroughTheOutermostLayerAndKeepsTheRawValues) {
    const auto& [c, source] = GetParam();
    SCOPED_TRACE(::testing::Message()
                 << c.name << ": expected " << hex64(c.expected) << ", actual " << hex64(c.actual)
                 << ", scripted re-read " << hex64(c.reread) << ", class "
                 << outcome_name(c.outcome) << ", found by " << source.name);

    region_buffer region;
    ASSERT_TRUE(region.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();

    // Worker 1 of two, so the worker index, like step and pass, is nonzero.
    constexpr std::uint32_t worker = 1U;
    const error_collector_options options = collector_options(2U);
    fake_clock clock;
    stop_signal stop;
    scripted_reread_access memory(std::make_unique<real_memory_access>(host_kernels()), c.reread);
    error_collector collector(options, stop, clock, memory);

    const std::uint64_t t0 = clock.now_ns();
    collector.begin_run(t0);
    collector.begin_step(k_step, 2U);
    const access_context verification =
        context_for(region, source.phase, source.access_type, worker, t0 + k_write_after_t0_ns);
    worker_error_sink& sink = collector.sink(worker);
    sink.bind(verification);
    clock.advance(k_detection_after_t0_ns);

    const std::uint64_t* const word = region.data() + k_mismatch_word;
    sink.on_mismatch(word, c.expected, c.actual);

    // Exactly one re-read, through the layer the collector was given, of the mismatching word,
    // with the verification's context in which only the phase changed.
    ASSERT_EQ(memory.call_count(), 1U) << "re-reads issued for one mismatch";
    const reread_call& call = memory.call(0U);
    EXPECT_EQ(call.word, word);
    EXPECT_TRUE(call.ctx.phase == phase_kind::reread)
        << "re-read issued in phase " << phase_name(call.ctx.phase);
    EXPECT_EQ(call.ctx.step_index, verification.step_index);
    EXPECT_EQ(call.ctx.pass_index, verification.pass_index);
    EXPECT_EQ(call.ctx.access_type, verification.access_type);
    EXPECT_EQ(call.ctx.worker_index, verification.worker_index);
    EXPECT_EQ(call.ctx.region_base, verification.region_base);
    EXPECT_EQ(call.ctx.write_time_ns, verification.write_time_ns);

    // Every outcome is a confirmed error.
    EXPECT_EQ(collector.true_total(), 1U);
    EXPECT_EQ(collector.step_errors(k_step), 1U);
    const error_batch batch = collector.drain();
    EXPECT_TRUE(batch.has_news);
    EXPECT_EQ(batch.total_errors, 1U);
    ASSERT_EQ(batch.count, 1U);
    ASSERT_NE(batch.records, nullptr);
    const rq_error_record& r = batch.records[0];

    EXPECT_EQ(r.reread_outcome, c.outcome) << "classified " << outcome_name(r.reread_outcome)
                                           << ", expected " << outcome_name(c.outcome);
    EXPECT_EQ(r.expected, c.expected) << "recorded " << hex64(r.expected);
    EXPECT_EQ(r.actual, c.actual) << "recorded " << hex64(r.actual);
    EXPECT_EQ(r.reread, c.reread) << "recorded " << hex64(r.reread);
    EXPECT_EQ(r.bit_mask, c.expected ^ c.actual) << "recorded " << hex64(r.bit_mask);
    EXPECT_EQ(r.address, address_of(word)) << "recorded " << hex64(r.address);
    EXPECT_EQ(r.region_offset, k_mismatch_offset);
    EXPECT_EQ(r.address_kind, RQ_ADDRESS_KIND_VIRTUAL);
    EXPECT_EQ(r.step_index, k_step);
    EXPECT_EQ(r.pass_index, k_pass);
    EXPECT_EQ(r.access_type, source.access_type);
    EXPECT_EQ(r.worker_index, worker);
    EXPECT_EQ(r.time_since_write_us, (k_detection_after_t0_ns - k_write_after_t0_ns) / k_ns_per_us);
    EXPECT_EQ(r.time_into_test_ms, k_detection_after_t0_ns / k_ns_per_ms);
    EXPECT_EQ(r.sequence, 0U);
}

INSTANTIATE_TEST_SUITE_P(RereadOutcomes, RereadCollector,
                         ::testing::Combine(::testing::ValuesIn(k_outcome_cases),
                                            ::testing::ValuesIn(k_source_contexts)),
                         collector_param_name);

TEST(RereadCollectorPath, CleanVerificationsIssueNoReread) {
    // A re-read follows a mismatch only: a clean pass adds no flush or load beyond the runner's
    // own sequence. Every primitive goes through the scripted layer's forwarding, as a session
    // issues it through its outermost layer.
    region_buffer region;
    ASSERT_TRUE(region.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    const error_collector_options options = collector_options(1U);
    fake_clock clock;
    stop_signal stop;
    scripted_reread_access memory(std::make_unique<real_memory_access>(host_kernels()), 0U);
    error_collector collector(options, stop, clock, memory);
    collector.begin_run(clock.now_ns());
    collector.begin_step(k_step, 1U);

    std::uint64_t* const range = region.data();
    const pattern_spec base{RQ_PATTERN_MOVING_INVERSION_RANDOM, k_seed, k_pass, false};
    const pattern_spec complement{RQ_PATTERN_MOVING_INVERSION_RANDOM, k_seed, k_pass, true};
    worker_error_sink& sink = collector.sink(0U);

    const access_context write = context_for(region, phase_kind::write, 0U, 0U, clock.now_ns());
    memory.stream_fill(write, range, k_window_words, base, 0U, 1);
    memory.fence(write);

    const access_context verification = context_for(
        region, phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE, 0U, write.write_time_ns);
    memory.fence(verification);
    memory.flush(verification, range, k_window_words);
    memory.fence(verification);
    sink.bind(verification);
    EXPECT_EQ(memory.verify(verification, range, k_window_words, base, 0U, 1, sink), 0U);

    const access_context sweep = context_for(region, phase_kind::sweep, RQ_ACCESS_READ_MODIFY_WRITE,
                                             0U, write.write_time_ns);
    memory.fence(sweep);
    memory.flush(sweep, range, k_window_words);
    memory.fence(sweep);
    sink.bind(sweep);
    EXPECT_EQ(memory.verify_and_stream(sweep, range, k_window_words, base, complement, 0U, 1, sink),
              0U);
    memory.fence(sweep);
    memory.flush(sweep, range, k_window_words);
    memory.fence(sweep);
    sink.bind(verification);
    EXPECT_EQ(memory.verify(verification, range, k_window_words, complement, 0U, -1, sink), 0U);

    EXPECT_EQ(memory.call_count(), 0U) << "re-reads issued without a mismatch";
    EXPECT_EQ(collector.true_total(), 0U);
    const error_batch batch = collector.drain();
    EXPECT_FALSE(batch.has_news);
    EXPECT_EQ(batch.count, 0U);
}

/* ---------------------------------------------------------------------------------------------
 * real_memory_access::reread_word: the fence, flush, fence, load helper of the session's table
 * ------------------------------------------------------------------------------------------- */

TEST_P(RereadRealLayer, KeepsTheRereadHelperItsSelectionNeeds) {
    const bool clflushopt = GetParam();
    // Synthetic SSE2 selection: constructing the layer copies the table and executes nothing.
    const isa_selection selection{.selected = RQ_ISA_SSE2,
                                  .best = RQ_ISA_SSE2,
                                  .available_mask = 1U << RQ_ISA_SSE2,
                                  .clflushopt = clflushopt,
                                  .override_rejected = false};
    real_memory_access layer(tables_for(selection));
    EXPECT_EQ(layer.kernels().reread, clflushopt ? &reread_clflushopt : &reread_clflush)
        << "the layer's re-read is not the " << (clflushopt ? "CLFLUSHOPT" : "CLFLUSH")
        << " fence-flush-fence-load helper";

    // The layer's re-read is executed only where the host can run it: CLFLUSH is architectural
    // on x64, CLFLUSHOPT needs CPUID.7.0:EBX bit 23. The identity check above runs everywhere.
    if (clflushopt && !select_isa(read_cpu_features(), RQ_ISA_AUTO).clflushopt) {
        return;
    }
    region_buffer region;
    ASSERT_TRUE(region.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    // An ordinary store leaves the line dirty in the cache; the re-read's flush writes it back
    // before its load.
    constexpr std::uint64_t stored = 0xC3A5F00F96E15A3CULL;
    std::uint64_t* const word = region.data() + 3U * k_line_words + 5U;
    *static_cast<volatile std::uint64_t*>(word) = stored;
    const access_context ctx =
        context_for(region, phase_kind::reread, RQ_ACCESS_VERIFY_AFTER_WRITE, 0U, 0U);
    const std::uint64_t reread = layer.reread_word(ctx, word);
    EXPECT_EQ(reread, stored) << "re-read " << hex64(reread) << ", stored " << hex64(stored);
}

INSTANTIATE_TEST_SUITE_P(RereadHelpers, RereadRealLayer, ::testing::Bool(), clflushopt_name);

TEST(RereadRealPath, ReturnsUnfencedStreamedValuesAndNeverCountsAsCoverage) {
    region_buffer region;
    ASSERT_TRUE(region.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    fake_clock clock;
    counting_options options;
    options.log_primitives = true;
    options.max_records_per_worker = 64U;
    options.max_workers = 1U;
    options.clock = &clock;
    counting_memory_access memory(std::make_unique<real_memory_access>(host_kernels()), options);

    // A 1 MiB window streamed with non-temporal stores and deliberately not fenced: each
    // re-read's own leading MFENCE must make the streamed data visible before its flush and
    // load.
    std::uint64_t* const range = region.data();
    const pattern_spec spec{RQ_PATTERN_RANDOM, k_seed, k_pass, false};
    const access_context write = context_for(region, phase_kind::write, 0U, 0U, clock.now_ns());
    memory.stream_fill(write, range, k_window_words, spec, k_spread_first_word_index, 1);

    // Sixteen words 64 KiB apart, each at a different position within its line, each re-read
    // once.
    const access_context reread_ctx = context_for(
        region, phase_kind::reread, RQ_ACCESS_VERIFY_AFTER_WRITE, 0U, write.write_time_ns);
    std::vector<std::size_t> indices;
    for (std::size_t i = 0; i < k_spread_rereads; ++i) {
        indices.push_back(i * k_spread_stride_words + i % k_line_words);
    }
    for (const std::size_t index : indices) {
        const std::uint64_t expected =
            expected_value(spec, k_spread_first_word_index + static_cast<std::uint64_t>(index));
        const std::uint64_t reread = memory.reread_word(reread_ctx, range + index);
        EXPECT_EQ(reread, expected) << "word " << index << ": re-read " << hex64(reread)
                                    << ", streamed " << hex64(expected);
    }

    // Re-reads are logged as one record each and never add to the independent verified count.
    EXPECT_EQ(memory.verified_bytes(), 0U) << "re-reads counted as verified bytes";
    EXPECT_EQ(memory.verified_bytes_for_step(k_step), 0U);
    EXPECT_EQ(memory.primitive_count(), 1U + k_spread_rereads);
    ASSERT_FALSE(memory.log_overflowed());
    const std::vector<primitive_record> log = memory.merged_log();
    ASSERT_EQ(log.size(), 1U + k_spread_rereads);
    EXPECT_TRUE(log[0].kind == primitive_kind::stream_fill)
        << "first record is " << primitive_name(log[0].kind);
    EXPECT_EQ(log[0].bytes, k_window_bytes);
    for (std::size_t i = 0; i < k_spread_rereads; ++i) {
        const primitive_record& record = log[1U + i];
        EXPECT_TRUE(record.kind == primitive_kind::reread_word)
            << "record " << 1U + i << " is " << primitive_name(record.kind);
        EXPECT_EQ(record.begin, reinterpret_cast<std::uintptr_t>(range + indices[i]));
        EXPECT_EQ(record.bytes, sizeof(std::uint64_t));
        EXPECT_TRUE(record.ctx.phase == phase_kind::reread)
            << "record " << 1U + i << " in phase " << phase_name(record.ctx.phase);
    }
}

TEST(RereadRealPath, MismatchFromARealVerificationRepeatsTheStoredWrongValue) {
    region_buffer region;
    ASSERT_TRUE(region.valid()) << "VirtualAlloc failed with Windows error " << GetLastError();
    const error_collector_options collector_opts = collector_options(1U);
    fake_clock clock;
    stop_signal stop;
    counting_options counting_opts;
    counting_opts.log_primitives = true;
    counting_opts.max_records_per_worker = 64U;
    counting_opts.max_workers = 1U;
    counting_opts.clock = &clock;
    // counting -> real, with the collector re-reading through the counting layer, as a test-DLL
    // session's outermost layers do.
    counting_memory_access memory(std::make_unique<real_memory_access>(host_kernels()),
                                  counting_opts);
    error_collector collector(collector_opts, stop, clock, memory);
    const std::uint64_t t0 = clock.now_ns();
    collector.begin_run(t0);
    collector.begin_step(k_step, 1U);

    // The runner's windowed pass: stream and fence, then fence, flush and fence before verifying.
    std::uint64_t* const range = region.data();
    const pattern_spec spec{RQ_PATTERN_RANDOM, k_seed, k_pass, false};
    const access_context write = context_for(region, phase_kind::write, 0U, 0U, t0);
    memory.stream_fill(write, range, k_window_words, spec, 0U, 1);
    memory.fence(write);
    const access_context verification =
        context_for(region, phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE, 0U, t0);
    memory.fence(verification);
    memory.flush(verification, range, k_window_words);
    memory.fence(verification);

    // A plain store of a wrong value after the flush models a storage fault, at the point where
    // the fault-injecting layer corrupts words: the line is dirty in the cache, so only the
    // re-read's flush puts the wrong value in memory before its load.
    const std::uint64_t expected = expected_value(spec, k_mismatch_word);
    const std::uint64_t corrupted = expected ^ k_flipped_bit;
    std::uint64_t* const word = range + k_mismatch_word;
    *static_cast<volatile std::uint64_t*>(word) = corrupted;
    clock.advance(k_detection_after_t0_ns);

    worker_error_sink& sink = collector.sink(0U);
    sink.bind(verification);
    EXPECT_EQ(memory.verify(verification, range, k_window_words, spec, 0U, 1, sink), 1U);

    EXPECT_EQ(collector.true_total(), 1U);
    const error_batch batch = collector.drain();
    ASSERT_EQ(batch.count, 1U);
    ASSERT_NE(batch.records, nullptr);
    const rq_error_record& r = batch.records[0];
    EXPECT_EQ(r.reread_outcome, RQ_REREAD_REPEATS_ACTUAL)
        << "classified " << outcome_name(r.reread_outcome) << ": expected " << hex64(r.expected)
        << ", actual " << hex64(r.actual) << ", re-read " << hex64(r.reread);
    EXPECT_EQ(r.expected, expected) << "recorded " << hex64(r.expected);
    EXPECT_EQ(r.actual, corrupted) << "recorded " << hex64(r.actual);
    EXPECT_EQ(r.reread, corrupted) << "recorded " << hex64(r.reread);
    EXPECT_EQ(r.bit_mask, k_flipped_bit) << "recorded " << hex64(r.bit_mask);
    EXPECT_EQ(r.address, address_of(word)) << "recorded " << hex64(r.address);
    EXPECT_EQ(r.region_offset, k_mismatch_offset);
    EXPECT_EQ(r.access_type, RQ_ACCESS_VERIFY_AFTER_WRITE);
    EXPECT_EQ(r.time_since_write_us, k_detection_after_t0_ns / k_ns_per_us);

    // One re-read record, right after the verification that found the mismatch, carrying its
    // context with phase reread; only the verification's bytes count.
    EXPECT_EQ(memory.verified_bytes(), k_window_bytes);
    ASSERT_FALSE(memory.log_overflowed());
    const std::vector<primitive_record> log = memory.merged_log();
    const primitive_kind expected_kinds[] = {primitive_kind::stream_fill, primitive_kind::fence,
                                             primitive_kind::fence,       primitive_kind::flush,
                                             primitive_kind::fence,       primitive_kind::verify,
                                             primitive_kind::reread_word};
    ASSERT_EQ(log.size(), std::size(expected_kinds));
    for (std::size_t i = 0; i < log.size(); ++i) {
        EXPECT_TRUE(log[i].kind == expected_kinds[i])
            << "record " << i << " is " << primitive_name(log[i].kind) << ", expected "
            << primitive_name(expected_kinds[i]);
    }
    const primitive_record& verify_record = log[5];
    const primitive_record& reread_record = log[6];
    EXPECT_GT(reread_record.seq, verify_record.seq);
    EXPECT_EQ(reread_record.begin, reinterpret_cast<std::uintptr_t>(word));
    EXPECT_EQ(reread_record.bytes, sizeof(std::uint64_t));
    EXPECT_TRUE(reread_record.ctx.phase == phase_kind::reread)
        << "re-read issued in phase " << phase_name(reread_record.ctx.phase);
    EXPECT_EQ(reread_record.ctx.step_index, verification.step_index);
    EXPECT_EQ(reread_record.ctx.pass_index, verification.pass_index);
    EXPECT_EQ(reread_record.ctx.access_type, verification.access_type);
    EXPECT_EQ(reread_record.ctx.write_time_ns, verification.write_time_ns);
}

}  // namespace rq::engine::test
