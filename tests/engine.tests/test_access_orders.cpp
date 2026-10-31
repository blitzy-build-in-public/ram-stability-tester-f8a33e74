/**
 * @file test_access_orders.cpp
 * @brief Access-order tests: every order visits each line or block of its window exactly once
 *        per phase, the turnaround schedule's start-up, steady state and drain at window and pass
 *        boundaries, and the write times behind turnaround time_since_write_us.
 *
 * - access_order_sequential, access_order_bank_stride and access_order_random_blocks: the
 *   documented range sequence of each order_cursor order.
 * - access_order_cursor: for every cursor order, window size and pass, every 64-byte line of the
 *   window is visited exactly once per phase; reset() and a second cursor built from the same
 *   parameters replay the phase, as the runner's verify phase does; and no line recurs within 64
 *   visits, across the write and verify phases as well.
 * - access_order_block_permutation and AccessOrderPermutationKey: the stateless keyed bijection
 *   behind RandomBlocks, and its key.
 * - access_order_turnaround_schedule and AccessOrderWindowAndPassBoundaries: the 4n + 1 actions of
 *   a window of n blocks, each block streamed, flushed and verified once per pass, the fence,
 *   flush, fence cycle before every verification, and a fresh start-up for every window of
 *   every pass.
 * - AccessOrderContract: the spans and orders the cursor and the schedule refuse.
 * - AccessOrderTurnaroundMetadata: the real step runner over 8 MiB of locked memory, logged by
 *   counting_memory_access under a fake clock. The primitive sequence is the schedule of every
 *   window of every pass, and every turnaround verification, drain blocks included, carries the
 *   stream time of its own block, from which time_since_write_us is computed.
 *
 * Windows are 2 MiB (the smallest span), 6 MiB (a 2 MiB multiple that is not a power of two)
 * and 64 MiB (the largest window); passes are 0 to 5, two cycles of the three BankStride
 * strides.
 *
 * Virtual level only. Orders act on virtual offsets; physical page mapping, channel and rank
 * interleaving and the memory controller's bank hashing are invisible to software, so
 * BankStride, RandomBlocks and Turnaround are exposure heuristics, and nothing here asserts a
 * physical sequence. What is checked is the visitation property behind assumption A2 of the
 * stress bound: no order revisits a line or block within a phase, so none alternates over a
 * small address set.
 *
 * Only AccessOrderTurnaroundMetadata touches memory or starts a thread. It needs no elevation:
 * 8 MiB locks once memory_region has raised the working set.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <ostream>
#include <set>
#include <stdexcept>
#include <string>
#include <tuple>
#include <type_traits>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/memory_region.h"
#include "core/memory/real_memory_access.h"
#include "core/patterns/access_order.h"
#include "core/runner/coverage_counter.h"
#include "core/runner/worker_pool.h"
#include "core/runner/step_runner.h"
#include "core/errors/error_collector.h"
#include "fakes/fake_clock.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

/**
 * Window sizes under test: the smallest span, a 2 MiB multiple that is not a power of two, and
 * the largest window. Their block counts, 512, 1536 and 16384, are the turnaround window sizes.
 */
constexpr std::uint64_t k_test_spans[] = {2 * k_mib, 6 * k_mib, 64 * k_mib};

/** Passes 0 to 5: two full cycles of the three BankStride strides. */
constexpr std::uint32_t k_test_passes = 6;

/**
 * RandomBlocks key inputs. All nonzero, so a cursor that dropped one of them would build a
 * different permutation than the one the tests expect.
 */
constexpr std::uint64_t k_seed = 0x0123456789ABCDEFULL;
constexpr std::uint32_t k_step_index = 2;
constexpr std::uint64_t k_salt = 0x0000000140000000ULL;

/** A2: any this many consecutive visits must touch this many distinct lines or blocks. */
constexpr std::uint64_t k_alternation_window = 64;

/**
 * Largest share of positions two unrelated block orders may have in common. Independent random
 * permutations share about one position on average, whatever their length.
 */
constexpr double k_max_coincidence = 0.05;

/**
 * Domain sizes of the permutation tests: the block counts of 2, 6 and 64 MiB windows, and an
 * odd count that no window has. 16384 = 4^7 fills its Feistel domain exactly; the others make
 * the cycle walk restrict a larger domain (4^5, 4^6 and 4^8).
 */
constexpr std::uint64_t k_permutation_sizes[] = {512, 1536, 16384, 16384 * 3 + 1};

/** Marks a block with no stream yet in the current window. */
constexpr std::size_t k_no_record = SIZE_MAX;

static_assert(k_test_spans[0] == k_span_granularity_bytes && k_test_spans[2] == k_max_window_bytes,
              "the spans under test must include the smallest span and the largest window");
static_assert(k_turnaround_distance == 4,
              "the plan verifies the block four positions behind the one just written");
static_assert(k_span_granularity_bytes / k_line_bytes >= k_alternation_window,
              "the smallest span must hold more lines than the alternation window");

/* ---------------------------------------------------------------------------------------------
 * Names for parameters and failure messages
 * ------------------------------------------------------------------------------------------- */

std::string span_name(std::uint64_t span_bytes) {
    return std::to_string(span_bytes / k_mib) + "MiB";
}

const char* op_name(turnaround_op op) noexcept {
    switch (op) {
    case turnaround_op::stream:
        return "stream";
    case turnaround_op::fence:
        return "fence";
    case turnaround_op::flush:
        return "flush";
    case turnaround_op::verify:
        return "verify";
    }
    return "invalid op";
}

const char* stage_name(turnaround_stage stage) noexcept {
    switch (stage) {
    case turnaround_stage::startup:
        return "startup";
    case turnaround_stage::steady:
        return "steady";
    case turnaround_stage::drain:
        return "drain";
    }
    return "invalid stage";
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
    case primitive_kind::stream_fill_strided:
        return "stream_fill_strided";
    case primitive_kind::verify_strided:
        return "verify_strided";
    }
    return "invalid primitive";
}

/** "op stage block b (offset o, length l)", for failure messages. */
std::string describe(const turnaround_action& action) {
    return std::string(op_name(action.op)) + " " + stage_name(action.stage) + " block " +
           std::to_string(action.block) + " (offset " + std::to_string(action.offset) +
           ", length " + std::to_string(action.length) + ")";
}

/* ---------------------------------------------------------------------------------------------
 * Visit bookkeeping
 * ------------------------------------------------------------------------------------------- */

/**
 * Visit counts of the units (lines or blocks) of one span. It reports the first unit not
 * visited exactly once; counts saturate at 255, which is still "more than once".
 */
class visit_map {
public:
    visit_map(std::uint64_t span_bytes, std::uint64_t unit_bytes)
        : unit_bytes_(unit_bytes), counts_(static_cast<std::size_t>(span_bytes / unit_bytes), 0) {}

    /**
     * Counts one visit of every unit in [offset, offset + length).
     *
     * @return false, counting nothing, when the range is empty, splits a unit or leaves the span.
     */
    bool visit(std::uint64_t offset, std::uint64_t length) noexcept {
        if (length == 0 || offset % unit_bytes_ != 0 || length % unit_bytes_ != 0) {
            return false;
        }
        const std::uint64_t first = offset / unit_bytes_;
        const std::uint64_t count = length / unit_bytes_;
        if (first >= counts_.size() || count > counts_.size() - first) {
            return false;
        }
        for (std::uint64_t unit = first; unit < first + count; ++unit) {
            std::uint8_t& visits = counts_[static_cast<std::size_t>(unit)];
            if (visits < UINT8_MAX) {
                ++visits;
            }
        }
        return true;
    }

    /** Index of the first unit visited 0 or more than 1 times; units() when there is none. */
    std::uint64_t first_not_once() const noexcept {
        const auto it = std::find_if(counts_.begin(), counts_.end(),
                                     [](std::uint8_t visits) { return visits != 1; });
        return static_cast<std::uint64_t>(it - counts_.begin());
    }

    /** Visits counted for one unit, saturated at 255. Precondition: unit < units(). */
    std::uint32_t visits(std::uint64_t unit) const noexcept {
        return counts_[static_cast<std::size_t>(unit)];
    }

    std::uint64_t units() const noexcept { return counts_.size(); }
    std::uint64_t unit_bytes() const noexcept { return unit_bytes_; }

private:
    std::uint64_t unit_bytes_;
    std::vector<std::uint8_t> counts_;
};

/**
 * "unit u (offset o) was visited v times", for the first unit of a map not visited exactly
 * once.
 */
std::string describe_first_not_once(const visit_map& map) {
    const std::uint64_t unit = map.first_not_once();
    if (unit >= map.units()) {
        return "every unit was visited exactly once";
    }
    return "unit " + std::to_string(unit) + " (offset " + std::to_string(unit * map.unit_bytes()) +
           ") was visited " + std::to_string(map.visits(unit)) + " times";
}

/**
 * Replays turnaround actions and enforces, for every block, the cycle the
 * flush-before-verification rule requires: stream; a fence; flush; a fence; verify. The first
 * fence makes the block's streaming stores globally visible before the flush, the flush evicts
 * every cached copy, and the second fence orders the flush before the verification loads. A
 * verified block may be streamed again, by a later pass, which starts its cycle afresh.
 */
class flush_rule_checker {
public:
    explicit flush_rule_checker(std::uint64_t blocks)
        : states_(static_cast<std::size_t>(blocks), block_state::idle) {}

    /**
     * Applies one action to the block it names; a fence names none and ignores block.
     *
     * @return An empty string when the action is allowed at this point; otherwise what it
     *         violates.
     */
    std::string apply(turnaround_op op, std::uint64_t block) {
        if (op == turnaround_op::fence) {
            // Completes every pending step: a streamed block may now be flushed, and a flushed
            // block verified.
            for (const std::size_t pending : awaiting_fence_) {
                if (states_[pending] == block_state::streamed) {
                    states_[pending] = block_state::stream_fenced;
                } else if (states_[pending] == block_state::flushed) {
                    states_[pending] = block_state::ready;
                }
            }
            awaiting_fence_.clear();
            return {};
        }
        if (block >= states_.size()) {
            return "block " + std::to_string(block) + " lies outside the " +
                   std::to_string(states_.size()) + "-block span";
        }
        const std::size_t index = static_cast<std::size_t>(block);
        block_state& state = states_[index];
        const std::string name = "block " + std::to_string(block);
        if (op == turnaround_op::stream) {
            if (state != block_state::idle) {
                return name + " was streamed again before its previous stream was verified";
            }
            state = block_state::streamed;
            awaiting_fence_.push_back(index);
            ++unverified_;
            return {};
        }
        if (op == turnaround_op::flush) {
            if (state != block_state::stream_fenced) {
                return name + " was flushed without a stream followed by a fence before it";
            }
            state = block_state::flushed;
            awaiting_fence_.push_back(index);
            return {};
        }
        if (state != block_state::ready) {
            return name + " was verified without stream, fence, flush and fence since its "
                          "previous verification";
        }
        state = block_state::idle;
        --unverified_;
        return {};
    }

    /** Blocks streamed and not yet verified: 0 whenever a window has been drained. */
    std::uint64_t unverified() const noexcept { return unverified_; }

private:
    enum class block_state : std::uint8_t {
        /** Never streamed, or verified since its last stream. */
        idle,
        /** Streamed; its stores are not yet fenced. */
        streamed,
        /** Streamed and fenced: ready to be flushed. */
        stream_fenced,
        /** Flushed; the flush is not yet fenced. */
        flushed,
        /** Flushed and fenced: ready to be verified. */
        ready,
    };

    std::vector<block_state> states_;
    // Blocks streamed or flushed since the last fence.
    std::vector<std::size_t> awaiting_fence_;
    std::uint64_t unverified_ = 0;
};

/* ---------------------------------------------------------------------------------------------
 * Cursors, permutations and schedules
 * ------------------------------------------------------------------------------------------- */

/** Cursor parameters of one order over one span in one pass, with the shared key inputs. */
order_params params_for(std::uint32_t order, std::uint64_t span_bytes,
                        std::uint32_t pass) noexcept {
    return order_params{.order = order,
                        .span_bytes = span_bytes,
                        .seed = k_seed,
                        .step_index = k_step_index,
                        .pass = pass,
                        .salt = k_salt};
}

/** Every remaining range of a cursor's phase, in order. */
std::vector<order_range> collect_ranges(order_cursor& cursor) {
    std::vector<order_range> ranges;
    ranges.reserve(static_cast<std::size_t>(cursor.total_ranges()));
    order_range range{};
    while (cursor.next(range)) {
        ranges.push_back(range);
    }
    return ranges;
}

bool same_range(const order_range& a, const order_range& b) noexcept {
    return a.offset == b.offset && a.length == b.length && a.direction == b.direction;
}

/** perm(0) ... perm(size() - 1). */
std::vector<std::uint64_t> images_of(const block_permutation& perm) {
    std::vector<std::uint64_t> images(static_cast<std::size_t>(perm.size()));
    for (std::uint64_t i = 0; i < perm.size(); ++i) {
        images[static_cast<std::size_t>(i)] = perm(i);
    }
    return images;
}

/** Share of positions at which two block orders of equal length hold the same block. */
double coincidence(const std::vector<std::uint64_t>& a, const std::vector<std::uint64_t>& b) {
    std::size_t same = 0;
    for (std::size_t i = 0; i < a.size() && i < b.size(); ++i) {
        if (a[i] == b[i]) {
            ++same;
        }
    }
    return a.empty() ? 0.0 : static_cast<double>(same) / static_cast<double>(a.size());
}

/** Every remaining action of a schedule, in order. */
std::vector<turnaround_action> collect_actions(turnaround_schedule& schedule) {
    std::vector<turnaround_action> actions;
    actions.reserve(static_cast<std::size_t>(schedule.action_count()));
    turnaround_action action{};
    while (schedule.next(action)) {
        actions.push_back(action);
    }
    return actions;
}

bool same_action(const turnaround_action& a, const turnaround_action& b) noexcept {
    return a.op == b.op && a.stage == b.stage && a.block == b.block && a.offset == b.offset &&
           a.length == b.length;
}

/**
 * The action sequence access_order.h documents for a window of `blocks` blocks, built here
 * independently of turnaround_schedule, with D = k_turnaround_distance:
 * - start-up: for b = 0 ... D - 1, stream b, fence;
 * - steady state: for i = D ... n - 1, stream i, flush i - D, fence, verify i - D;
 * - drain: flush n - D ... n - 1, one fence, verify n - D ... n - 1.
 * Fences name no block, so their block, offset and length are 0.
 */
std::vector<turnaround_action> documented_schedule(std::uint64_t blocks) {
    constexpr std::uint64_t d = k_turnaround_distance;
    std::vector<turnaround_action> actions;
    actions.reserve(static_cast<std::size_t>(4 * blocks + 1));
    const auto add_block = [&actions](turnaround_op op, turnaround_stage stage,
                                      std::uint64_t block) {
        actions.push_back(
            turnaround_action{op, stage, block, block * k_block_bytes, k_block_bytes});
    };
    const auto add_fence = [&actions](turnaround_stage stage) {
        actions.push_back(turnaround_action{turnaround_op::fence, stage, 0, 0, 0});
    };
    for (std::uint64_t b = 0; b < d; ++b) {
        add_block(turnaround_op::stream, turnaround_stage::startup, b);
        add_fence(turnaround_stage::startup);
    }
    for (std::uint64_t i = d; i < blocks; ++i) {
        add_block(turnaround_op::stream, turnaround_stage::steady, i);
        add_block(turnaround_op::flush, turnaround_stage::steady, i - d);
        add_fence(turnaround_stage::steady);
        add_block(turnaround_op::verify, turnaround_stage::steady, i - d);
    }
    for (std::uint64_t b = blocks - d; b < blocks; ++b) {
        add_block(turnaround_op::flush, turnaround_stage::drain, b);
    }
    add_fence(turnaround_stage::drain);
    for (std::uint64_t b = blocks - d; b < blocks; ++b) {
        add_block(turnaround_op::verify, turnaround_stage::drain, b);
    }
    return actions;
}

/** The counting_memory_access record kind each turnaround operation is issued as. */
primitive_kind primitive_for(turnaround_op op) noexcept {
    switch (op) {
    case turnaround_op::stream:
        return primitive_kind::stream_fill;
    case turnaround_op::flush:
        return primitive_kind::flush;
    case turnaround_op::verify:
        return primitive_kind::verify;
    case turnaround_op::fence:
        break;
    }
    return primitive_kind::fence;
}

/* ---------------------------------------------------------------------------------------------
 * The turnaround metadata run
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_ns_per_ms = 1'000'000;

/**
 * Fake time of one primitive. The counting layer's observer advances the clock by this much on
 * every call, so every log record has its own time and the stream brackets of different blocks
 * never overlap: two streams are always separated by at least one other action.
 */
constexpr std::uint64_t k_primitive_ns = 1'000;

/**
 * The metadata run's single slice, viewed as two chunks of 2 MiB and 6 MiB. The runner cuts its
 * windows inside each chunk, so every pass runs two turnaround windows and crosses a window
 * boundary as well as, from the second pass on, a pass boundary.
 */
constexpr std::uint64_t k_meta_chunk_bytes[] = {2 * k_mib, 6 * k_mib};
constexpr std::uint64_t k_meta_slice_bytes = k_meta_chunk_bytes[0] + k_meta_chunk_bytes[1];

/** Primitives per pass: one per turnaround action, 4n + 1 per window of n blocks. */
constexpr std::uint64_t k_meta_pass_primitives = (4 * (k_meta_chunk_bytes[0] / k_block_bytes) + 1) +
                                                 (4 * (k_meta_chunk_bytes[1] / k_block_bytes) + 1);

/** Fake duration of one pass: nothing but the primitives advances the clock. */
constexpr std::uint64_t k_meta_pass_ns = k_meta_pass_primitives * k_primitive_ns;

/** Passes the budget schedules under the fake clock. */
constexpr std::uint32_t k_meta_passes = 4;

/** Time budget of the step: about 4 fake passes of 8.194 ms each. */
constexpr std::uint32_t k_meta_budget_ms = 33;

/** Per-worker log capacity: twice the records a clean run issues. */
constexpr std::size_t k_meta_log_capacity =
    static_cast<std::size_t>(2 * k_meta_passes * k_meta_pass_primitives);

/** Detail records kept should real memory ever mismatch; a clean run stores none. */
constexpr std::uint32_t k_meta_detail_cap = 16;

/** Q3's coverage budget, 35 %, in basis points; the runner never extends a step for it. */
constexpr std::uint32_t k_meta_coverage_budget_bp = 3500;

static_assert(k_meta_chunk_bytes[0] % k_span_granularity_bytes == 0 &&
                  k_meta_chunk_bytes[1] % k_span_granularity_bytes == 0 &&
                  k_meta_chunk_bytes[1] <= k_window_bytes,
              "each chunk view must be a valid span and one runner window");
static_assert(k_meta_pass_ns > k_min_mean_pass_ns,
              "a fake pass must outlast the scheduler's floor, so the budget sets the pass count");
static_assert(planned_passes(std::uint64_t{k_meta_budget_ms} * k_ns_per_ms, k_meta_pass_ns) ==
                  k_meta_passes,
              "the budget must schedule exactly k_meta_passes fake passes");
static_assert(k_meta_passes >= 3, "three passes cross two pass boundaries");

/** Keeps the last schedule run_step reported and counts its ticks. */
class recording_observer final : public step_observer {
public:
    void tick(const step_status& status) override {
        last_ = status;
        ++ticks_;
    }

    const step_status& last() const noexcept { return last_; }
    std::uint64_t ticks() const noexcept { return ticks_; }

private:
    step_status last_{};
    std::uint64_t ticks_ = 0;
};

/** "pass p, window w, action a (op stage block ...), log record r", for failure messages. */
std::string describe_meta(std::uint32_t pass, std::size_t window, std::size_t action_index,
                          const turnaround_action& action, std::size_t record) {
    return "pass " + std::to_string(pass) + ", window " + std::to_string(window) + " (" +
           span_name(k_meta_chunk_bytes[window]) + "), action " + std::to_string(action_index) +
           " (" + describe(action) + "), log record " + std::to_string(record);
}

/* ---------------------------------------------------------------------------------------------
 * Parameters and fixtures
 * ------------------------------------------------------------------------------------------- */

/** One order_cursor order and the length of every range it yields. */
struct order_case {
    const char* name;
    std::uint32_t order;
    std::uint64_t range_bytes;
};

void PrintTo(const order_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr order_case k_ascending{"SequentialAscending", RQ_ORDER_SEQUENTIAL_ASCENDING,
                                 k_sequential_range_bytes};
constexpr order_case k_descending{"SequentialDescending", RQ_ORDER_SEQUENTIAL_DESCENDING,
                                  k_sequential_range_bytes};
constexpr order_case k_bank_stride{"BankStride", RQ_ORDER_BANK_STRIDE, k_line_bytes};
constexpr order_case k_random_blocks{"RandomBlocks", RQ_ORDER_RANDOM_BLOCKS, k_block_bytes};

constexpr order_case k_cursor_orders[] = {k_ascending, k_descending, k_bank_stride,
                                          k_random_blocks};
constexpr order_case k_sequential_orders[] = {k_ascending, k_descending};

/** "Order over a <n>MiB window, pass p", for failure messages. */
std::string describe_phase(const char* order, std::uint64_t span_bytes, std::uint32_t pass) {
    return std::string(order) + " over a " + span_name(span_bytes) + " window, pass " +
           std::to_string(pass);
}

using cursor_param = std::tuple<order_case, std::uint64_t, std::uint32_t>;
using sequential_param = std::tuple<order_case, std::uint64_t>;
using span_pass_param = std::tuple<std::uint64_t, std::uint32_t>;

std::string cursor_param_name(const ::testing::TestParamInfo<cursor_param>& info) {
    return std::string(std::get<0>(info.param).name) + "_" + span_name(std::get<1>(info.param)) +
           "_Pass" + std::to_string(std::get<2>(info.param));
}

std::string sequential_param_name(const ::testing::TestParamInfo<sequential_param>& info) {
    return std::string(std::get<0>(info.param).name) + "_" + span_name(std::get<1>(info.param));
}

std::string span_pass_param_name(const ::testing::TestParamInfo<span_pass_param>& info) {
    return span_name(std::get<0>(info.param)) + "_Pass" + std::to_string(std::get<1>(info.param));
}

std::string span_param_name(const ::testing::TestParamInfo<std::uint64_t>& info) {
    return span_name(info.param);
}

std::string permutation_size_name(const ::testing::TestParamInfo<std::uint64_t>& info) {
    return "Blocks" + std::to_string(info.param);
}

/** Parameters: a cursor order, a window size and a pass. */
class access_order_cursor : public ::testing::TestWithParam<cursor_param> {
protected:
    const order_case& order_under_test() const { return std::get<0>(GetParam()); }
    std::uint64_t span() const { return std::get<1>(GetParam()); }
    std::uint32_t pass() const { return std::get<2>(GetParam()); }
    order_params params() const { return params_for(order_under_test().order, span(), pass()); }
    std::string where() const { return describe_phase(order_under_test().name, span(), pass()); }
};

/** Parameters: a sequential order and a window size. */
class access_order_sequential : public ::testing::TestWithParam<sequential_param> {};

/** Parameters: a window size and a pass. */
class access_order_bank_stride : public ::testing::TestWithParam<span_pass_param> {};

/** Parameters: a window size and a pass. */
class access_order_random_blocks : public ::testing::TestWithParam<span_pass_param> {};

/** Parameter: the permutation's domain size. */
class access_order_block_permutation : public ::testing::TestWithParam<std::uint64_t> {};

/** Parameter: the window size; the schedule has span / 4096 blocks. */
class access_order_turnaround_schedule : public ::testing::TestWithParam<std::uint64_t> {};

}  // namespace

static_assert(std::is_trivially_copyable_v<block_permutation> && sizeof(block_permutation) <= 64,
              "a permutation holds no table, so building one per window or slice per pass "
              "allocates nothing");

/* ---------------------------------------------------------------------------------------------
 * Sequential orders: 1 MiB ranges tiling the window, low to high or high to low
 * ------------------------------------------------------------------------------------------- */

TEST_P(access_order_sequential, RangesTileTheWindowContiguouslyInTheirDirectionInEveryPass) {
    const auto& [order, span] = GetParam();
    const bool ascending = order.order == RQ_ORDER_SEQUENTIAL_ASCENDING;
    std::vector<order_range> pass_zero;
    for (std::uint32_t pass = 0; pass < k_test_passes; ++pass) {
        const std::string where = describe_phase(order.name, span, pass);
        order_cursor cursor(params_for(order.order, span, pass));
        const std::vector<order_range> ranges = collect_ranges(cursor);
        ASSERT_EQ(ranges.size(), span / k_sequential_range_bytes) << where;
        for (std::size_t k = 0; k < ranges.size(); ++k) {
            const order_range& range = ranges[k];
            const std::uint64_t expected_offset =
                ascending ? k * k_sequential_range_bytes
                          : span - (std::uint64_t{k} + 1) * k_sequential_range_bytes;
            ASSERT_EQ(range.length, k_sequential_range_bytes) << where << ", range " << k;
            ASSERT_EQ(range.offset, expected_offset) << where << ", range " << k;
            ASSERT_EQ(range.direction, ascending ? 1 : -1) << where << ", range " << k;
            if (k > 0) {
                // No gap and no overlap: each range continues where the previous one ended, in
                // the direction of traversal.
                const order_range& previous = ranges[k - 1];
                if (ascending) {
                    ASSERT_EQ(range.offset, previous.offset + previous.length)
                        << where << ", range " << k;
                } else {
                    ASSERT_EQ(range.offset + range.length, previous.offset)
                        << where << ", range " << k;
                }
            }
        }
        // The traversal runs from one end of the window to the other.
        const order_range& low = ascending ? ranges.front() : ranges.back();
        const order_range& high = ascending ? ranges.back() : ranges.front();
        EXPECT_EQ(low.offset, std::uint64_t{0}) << where;
        EXPECT_EQ(high.offset + high.length, span) << where;

        // Sequential orders ignore the pass: every pass traverses the window identically.
        if (pass == 0) {
            pass_zero = ranges;
        } else {
            ASSERT_TRUE(std::equal(ranges.begin(), ranges.end(), pass_zero.begin(), pass_zero.end(),
                                   same_range))
                << where << " differs from pass 0";
        }
    }
}

INSTANTIATE_TEST_SUITE_P(AccessOrderSequentialOrders, access_order_sequential,
                         ::testing::Combine(::testing::ValuesIn(k_sequential_orders),
                                            ::testing::ValuesIn(k_test_spans)),
                         sequential_param_name);

/* ---------------------------------------------------------------------------------------------
 * BankStride: one line per range, a stride apart within a sweep, one line further per sweep
 * ------------------------------------------------------------------------------------------- */

TEST(AccessOrderBankStrideCycle, StrideCyclesThrough8KiB64KiBAnd256KiBWithThePass) {
    EXPECT_EQ(bank_stride_bytes(0), std::uint64_t{8} << 10);
    EXPECT_EQ(bank_stride_bytes(1), std::uint64_t{64} << 10);
    EXPECT_EQ(bank_stride_bytes(2), std::uint64_t{256} << 10);
    for (std::uint32_t pass = 0; pass < k_test_passes; ++pass) {
        EXPECT_EQ(bank_stride_bytes(pass), k_bank_strides[pass % 3]) << "pass " << pass;
    }
    EXPECT_EQ(bank_stride_bytes(UINT32_MAX), k_bank_strides[UINT32_MAX % 3]);
}

TEST_P(access_order_bank_stride, SweepsVisitLinesOneStrideApartAndStartOneLineFurtherEachSweep) {
    const auto& [span, pass] = GetParam();
    const std::string where = describe_phase("BankStride", span, pass);
    const std::uint64_t stride = bank_stride_bytes(pass);
    const std::uint64_t sweeps = stride / k_line_bytes;
    const std::uint64_t lines_per_sweep = span / stride;
    order_cursor cursor(params_for(RQ_ORDER_BANK_STRIDE, span, pass));
    ASSERT_EQ(cursor.total_ranges(), sweeps * lines_per_sweep) << where;

    order_range range{};
    std::uint64_t previous_offset = 0;
    for (std::uint64_t sweep = 0; sweep < sweeps; ++sweep) {
        for (std::uint64_t line = 0; line < lines_per_sweep; ++line) {
            if (!cursor.next(range)) {
                FAIL() << where << ": the phase ended early, at sweep " << sweep << ", line "
                       << line;
            }
            if (range.length != k_line_bytes || range.direction != 1) {
                FAIL() << where << ": sweep " << sweep << ", line " << line << " has length "
                       << range.length << " and direction " << range.direction
                       << "; expected one line, ascending";
            }
            // The first line of sweep j is line j of the window; every later one lies one
            // stride beyond the previous visit.
            const std::uint64_t expected =
                line == 0 ? sweep * k_line_bytes : previous_offset + stride;
            if (range.offset != expected) {
                FAIL() << where << ": sweep " << sweep << ", line " << line << " is at offset "
                       << range.offset << ", expected " << expected;
            }
            previous_offset = range.offset;
        }
    }
    EXPECT_FALSE(cursor.next(range)) << where << ": ranges beyond the documented sweeps";
}

INSTANTIATE_TEST_SUITE_P(AccessOrderBankStrideWindows, access_order_bank_stride,
                         ::testing::Combine(::testing::ValuesIn(k_test_spans),
                                            ::testing::Range(0U, k_test_passes)),
                         span_pass_param_name);

/* ---------------------------------------------------------------------------------------------
 * RandomBlocks: the keyed permutation of the window's 4 KiB blocks
 * ------------------------------------------------------------------------------------------- */

TEST_P(access_order_random_blocks, VisitsBlocksInTheOrderOfTheirKeyedPermutation) {
    const auto& [span, pass] = GetParam();
    const std::string where = describe_phase("RandomBlocks", span, pass);
    const std::uint64_t blocks = span / k_block_bytes;
    // Every key input comes from order_params, so the verify phase, built from the same
    // parameters, reproduces the write phase's order.
    const block_permutation perm(blocks, permutation_key(k_seed, k_step_index, pass, k_salt));
    order_cursor cursor(params_for(RQ_ORDER_RANDOM_BLOCKS, span, pass));
    ASSERT_EQ(cursor.total_ranges(), blocks) << where;

    order_range range{};
    for (std::uint64_t i = 0; i < blocks; ++i) {
        if (!cursor.next(range)) {
            FAIL() << where << ": the phase ended early, at range " << i;
        }
        const std::uint64_t expected = perm(i) * k_block_bytes;
        if (range.offset != expected || range.length != k_block_bytes || range.direction != 1) {
            FAIL() << where << ": range " << i << " is (" << range.offset << ", " << range.length
                   << ", " << range.direction << "); expected (" << expected << ", "
                   << k_block_bytes << ", 1)";
        }
    }
    EXPECT_FALSE(cursor.next(range)) << where << ": ranges beyond the window's blocks";
}

INSTANTIATE_TEST_SUITE_P(AccessOrderRandomBlocksWindows, access_order_random_blocks,
                         ::testing::Combine(::testing::ValuesIn(k_test_spans),
                                            ::testing::Range(0U, k_test_passes)),
                         span_pass_param_name);

/* ---------------------------------------------------------------------------------------------
 * Every cursor order: exactly once per phase, a replayable phase, no small-set alternation
 * ------------------------------------------------------------------------------------------- */

TEST_P(access_order_cursor, VisitsEveryLineOfTheWindowExactlyOncePerPhase) {
    const std::uint64_t range_bytes = order_under_test().range_bytes;
    order_cursor cursor(params());
    ASSERT_EQ(cursor.total_ranges(), span() / range_bytes) << where();
    ASSERT_EQ(cursor.bytes_emitted(), std::uint64_t{0}) << where();

    visit_map lines(span(), k_line_bytes);
    order_range range{};
    std::uint64_t yielded = 0;
    while (cursor.next(range)) {
        if (range.length != range_bytes) {
            FAIL() << where() << ": range " << yielded << " has length " << range.length
                   << ", expected " << range_bytes;
        }
        // Inside one aligned 1 MiB unit, so no range crosses a chunk and the runner's 1 MiB
        // stop and coverage checks fall between ranges.
        if (range.offset / k_sequential_range_bytes !=
            (range.offset + range.length - 1) / k_sequential_range_bytes) {
            FAIL() << where() << ": range " << yielded << " at offset " << range.offset
                   << " crosses a 1 MiB boundary";
        }
        if (!lines.visit(range.offset, range.length)) {
            FAIL() << where() << ": range " << yielded << " at offset " << range.offset
                   << " leaves the window or splits a line";
        }
        ++yielded;
        if (cursor.bytes_emitted() != yielded * range_bytes) {
            FAIL() << where() << ": bytes_emitted() is " << cursor.bytes_emitted() << " after "
                   << yielded << " ranges";
        }
    }
    EXPECT_EQ(yielded, cursor.total_ranges()) << where();
    EXPECT_EQ(cursor.bytes_emitted(), span()) << where();
    EXPECT_EQ(lines.first_not_once(), lines.units())
        << where() << ": line " << describe_first_not_once(lines);

    // Exhausted: next() yields nothing more and leaves its argument as it was.
    const order_range sentinel{12345, 678, -7};
    order_range untouched = sentinel;
    EXPECT_FALSE(cursor.next(untouched)) << where();
    EXPECT_TRUE(same_range(untouched, sentinel)) << where() << ": next() wrote past the phase";
}

TEST_P(access_order_cursor, ResetAndAnIdenticalCursorReplayThePhase) {
    // The runner's verify phase revisits its write phase through reset() (windowed steps) or a
    // cursor rebuilt from the same parameters (hold steps); both must yield the same sequence.
    order_cursor cursor(params());
    const std::vector<order_range> first = collect_ranges(cursor);
    ASSERT_EQ(first.size(), cursor.total_ranges()) << where();
    cursor.reset();
    EXPECT_EQ(cursor.bytes_emitted(), std::uint64_t{0}) << where() << ": after reset()";
    const std::vector<order_range> replayed = collect_ranges(cursor);
    order_cursor twin(params());
    const std::vector<order_range> rebuilt = collect_ranges(twin);
    ASSERT_EQ(replayed.size(), first.size()) << where() << ": after reset()";
    ASSERT_EQ(rebuilt.size(), first.size()) << where() << ": rebuilt cursor";
    for (std::size_t i = 0; i < first.size(); ++i) {
        if (!same_range(replayed[i], first[i])) {
            FAIL() << where() << ": after reset(), range " << i << " is at offset "
                   << replayed[i].offset << " instead of " << first[i].offset;
        }
        if (!same_range(rebuilt[i], first[i])) {
            FAIL() << where() << ": the rebuilt cursor's range " << i << " is at offset "
                   << rebuilt[i].offset << " instead of " << first[i].offset;
        }
    }
}

TEST_P(access_order_cursor, NoLineRecursWithinSixtyFourVisitsAcrossTheWriteAndVerifyPhases) {
    // A2 at the virtual level. A windowed step writes and then verifies each window in the same
    // order, so two consecutive phases are walked line by line, in each range's direction: no
    // line may recur within a phase, nor within k_alternation_window visits across the phase
    // boundary, where a flush-and-reload loop over a small set would show.
    order_cursor cursor(params());
    std::vector<std::uint64_t> last_visit(static_cast<std::size_t>(span() / k_line_bytes),
                                          UINT64_MAX);
    std::uint64_t ordinal = 0;
    for (std::uint32_t phase = 0; phase < 2; ++phase) {
        const std::uint64_t phase_start = ordinal;
        order_range range{};
        while (cursor.next(range)) {
            const std::uint64_t first_line = range.offset / k_line_bytes;
            const std::uint64_t lines = range.length / k_line_bytes;
            for (std::uint64_t j = 0; j < lines; ++j) {
                const std::uint64_t line = first_line + (range.direction > 0 ? j : lines - 1 - j);
                if (line >= last_visit.size()) {
                    FAIL() << where() << ": line " << line << " lies outside the window";
                }
                std::uint64_t& last = last_visit[static_cast<std::size_t>(line)];
                if (last != UINT64_MAX && last >= phase_start) {
                    FAIL() << where() << ": line " << line << " visited twice in phase " << phase;
                }
                if (last != UINT64_MAX && ordinal - last < k_alternation_window) {
                    FAIL() << where() << ": line " << line << " recurred after only "
                           << ordinal - last << " visits";
                }
                last = ordinal++;
            }
        }
        cursor.reset();
    }
    EXPECT_EQ(ordinal, 2 * (span() / k_line_bytes)) << where();
}

INSTANTIATE_TEST_SUITE_P(AccessOrderAllCursorOrders, access_order_cursor,
                         ::testing::Combine(::testing::ValuesIn(k_cursor_orders),
                                            ::testing::ValuesIn(k_test_spans),
                                            ::testing::Range(0U, k_test_passes)),
                         cursor_param_name);

/* ---------------------------------------------------------------------------------------------
 * The RandomBlocks permutation and its key
 * ------------------------------------------------------------------------------------------- */

TEST_P(access_order_block_permutation, IsABijectionOnItsDomain) {
    const std::uint64_t n = GetParam();
    const block_permutation perm(n, permutation_key(k_seed, k_step_index, 0, k_salt));
    ASSERT_EQ(perm.size(), n);
    std::vector<std::uint8_t> hit(static_cast<std::size_t>(n), 0);
    for (std::uint64_t i = 0; i < n; ++i) {
        const std::uint64_t image = perm(i);
        if (image >= n) {
            FAIL() << n << " blocks: perm(" << i << ") = " << image << " lies outside the domain";
        }
        if (hit[static_cast<std::size_t>(image)] != 0) {
            FAIL() << n << " blocks: perm(" << i << ") = " << image << " repeats an earlier image";
        }
        hit[static_cast<std::size_t>(image)] = 1;
    }
}

TEST_P(access_order_block_permutation, IsAStatelessFunctionOfItsKey) {
    const std::uint64_t n = GetParam();
    const std::uint64_t key = permutation_key(k_seed, k_step_index, 1, k_salt);
    const block_permutation perm(n, key);
    const std::vector<std::uint64_t> forward = images_of(perm);
    const block_permutation twin(n, key);
    const block_permutation copy = perm;
    // Queried backwards, so state carried from one call to the next would change the images.
    for (std::uint64_t i = n; i-- > 0;) {
        const std::uint64_t expected = forward[static_cast<std::size_t>(i)];
        if (perm(i) != expected || twin(i) != expected || copy(i) != expected) {
            FAIL() << n << " blocks: image of " << i << " is not reproducible: forward " << expected
                   << ", again " << perm(i) << ", same key " << twin(i) << ", copy " << copy(i);
        }
    }
}

TEST_P(access_order_block_permutation, EveryKeyInputGivesAnUnrelatedOrder) {
    const std::uint64_t n = GetParam();
    const std::vector<std::uint64_t> base =
        images_of(block_permutation(n, permutation_key(k_seed, k_step_index, 0, k_salt)));
    struct key_variant {
        const char* name;
        std::uint64_t key;
    };
    const key_variant variants[] = {
        {"seed + 1", permutation_key(k_seed + 1, k_step_index, 0, k_salt)},
        {"step + 1", permutation_key(k_seed, k_step_index + 1, 0, k_salt)},
        {"pass + 1", permutation_key(k_seed, k_step_index, 1, k_salt)},
        {"salt + 1", permutation_key(k_seed, k_step_index, 0, k_salt + 1)},
    };
    for (const key_variant& variant : variants) {
        const double shared = coincidence(base, images_of(block_permutation(n, variant.key)));
        EXPECT_LT(shared, k_max_coincidence)
            << n << " blocks, " << variant.name << ": " << shared * 100.0
            << " % of positions hold the same block";
    }
    std::vector<std::uint64_t> ascending(static_cast<std::size_t>(n));
    for (std::uint64_t i = 0; i < n; ++i) {
        ascending[static_cast<std::size_t>(i)] = i;
    }
    const double fixed = coincidence(base, ascending);
    EXPECT_LT(fixed, k_max_coincidence)
        << n << " blocks: " << fixed * 100.0 << " % of blocks stay in their sequential position";
}

INSTANTIATE_TEST_SUITE_P(AccessOrderPermutationSizes, access_order_block_permutation,
                         ::testing::ValuesIn(k_permutation_sizes), permutation_size_name);

TEST(AccessOrderPermutationKey, IsReproducibleAndDistinctWheneverAnyInputDiffers) {
    EXPECT_EQ(permutation_key(k_seed, k_step_index, 3, k_salt),
              permutation_key(k_seed, k_step_index, 3, k_salt));
    // Step and pass are packed into disjoint halves, so exchanging them changes the key.
    EXPECT_NE(permutation_key(k_seed, 1, 2, k_salt), permutation_key(k_seed, 2, 1, k_salt));

    // Salts as the runner chooses them: window logical offsets, 64 MiB apart.
    constexpr std::uint32_t k_values = 8;
    std::set<std::uint64_t> keys;
    for (std::uint64_t seed = 0; seed < k_values; ++seed) {
        for (std::uint32_t step = 0; step < k_values; ++step) {
            for (std::uint32_t pass = 0; pass < k_values; ++pass) {
                for (std::uint64_t window = 0; window < k_values; ++window) {
                    keys.insert(permutation_key(seed, step, pass, window * k_max_window_bytes));
                }
            }
        }
    }
    EXPECT_EQ(keys.size(), std::size_t{k_values} * k_values * k_values * k_values)
        << "two input combinations share a key";
}

/* ---------------------------------------------------------------------------------------------
 * The turnaround schedule of one window
 * ------------------------------------------------------------------------------------------- */

TEST_P(access_order_turnaround_schedule, HasFourNPlusOneActionsInStartupSteadyAndDrainOrder) {
    const std::uint64_t span = GetParam();
    const std::uint64_t n = span / k_block_bytes;
    turnaround_schedule schedule(span);
    ASSERT_EQ(schedule.block_count(), n) << span_name(span);
    ASSERT_EQ(schedule.action_count(), 4 * n + 1) << span_name(span);

    // Start-up streams blocks 0-3, each followed by a fence, and verifies nothing; the steady
    // state streams i, flushes i - 4, fences and verifies i - 4; the drain flushes the last four
    // blocks, fences once and verifies them, as drain-stage verifications.
    const std::vector<turnaround_action> actions = collect_actions(schedule);
    const std::vector<turnaround_action> expected = documented_schedule(n);
    ASSERT_EQ(actions.size(), expected.size()) << span_name(span);
    for (std::size_t i = 0; i < actions.size(); ++i) {
        if (!same_action(actions[i], expected[i])) {
            FAIL() << span_name(span) << " window: action " << i << " is " << describe(actions[i])
                   << ", expected " << describe(expected[i]);
        }
    }

    // Exhausted: next() yields nothing more and leaves its argument as it was.
    const turnaround_action sentinel{turnaround_op::verify, turnaround_stage::drain, 77, 88, 99};
    turnaround_action untouched = sentinel;
    EXPECT_FALSE(schedule.next(untouched)) << span_name(span);
    EXPECT_TRUE(same_action(untouched, sentinel))
        << span_name(span) << ": next() wrote past the schedule";
}

TEST_P(access_order_turnaround_schedule, StreamsFlushesAndVerifiesEveryBlockExactlyOnce) {
    const std::uint64_t span = GetParam();
    const std::uint64_t n = span / k_block_bytes;
    turnaround_schedule schedule(span);
    visit_map streamed(span, k_block_bytes);
    visit_map flushed(span, k_block_bytes);
    visit_map verified(span, k_block_bytes);
    std::uint64_t fences = 0;
    std::uint64_t index = 0;
    turnaround_action action{};
    while (schedule.next(action)) {
        const std::string where = span_name(span) + " window, action " + std::to_string(index) +
                                  " (" + describe(action) + ")";
        if (action.op == turnaround_op::fence) {
            if (action.block != 0 || action.offset != 0 || action.length != 0) {
                FAIL() << where << ": a fence names no block";
            }
            ++fences;
        } else {
            if (action.offset != action.block * k_block_bytes || action.length != k_block_bytes) {
                FAIL() << where << ": the range is not the named block";
            }
            visit_map& map = action.op == turnaround_op::stream  ? streamed
                             : action.op == turnaround_op::flush ? flushed
                                                                 : verified;
            if (!map.visit(action.offset, action.length)) {
                FAIL() << where << ": the block lies outside the window";
            }
        }
        ++index;
    }
    EXPECT_EQ(fences, n + 1) << span_name(span);
    EXPECT_EQ(streamed.first_not_once(), streamed.units())
        << span_name(span) << ", streams: block " << describe_first_not_once(streamed);
    EXPECT_EQ(flushed.first_not_once(), flushed.units())
        << span_name(span) << ", flushes: block " << describe_first_not_once(flushed);
    EXPECT_EQ(verified.first_not_once(), verified.units())
        << span_name(span) << ", verifications: block " << describe_first_not_once(verified);
}

TEST_P(access_order_turnaround_schedule, EveryVerificationFollowsFenceFlushFenceAfterItsStream) {
    const std::uint64_t span = GetParam();
    turnaround_schedule schedule(span);
    flush_rule_checker checker(schedule.block_count());
    std::uint64_t index = 0;
    turnaround_action action{};
    while (schedule.next(action)) {
        const std::string violation = checker.apply(action.op, action.block);
        if (!violation.empty()) {
            FAIL() << span_name(span) << " window, action " << index << " (" << describe(action)
                   << "): " << violation;
        }
        ++index;
    }
    EXPECT_EQ(checker.unverified(), std::uint64_t{0})
        << span_name(span) << ": blocks streamed but never verified";
}

TEST_P(access_order_turnaround_schedule, ResetReplaysTheIdenticalSchedule) {
    const std::uint64_t span = GetParam();
    turnaround_schedule schedule(span);
    const std::vector<turnaround_action> first = collect_actions(schedule);
    schedule.reset();
    const std::vector<turnaround_action> replayed = collect_actions(schedule);
    ASSERT_EQ(replayed.size(), first.size()) << span_name(span);
    for (std::size_t i = 0; i < first.size(); ++i) {
        if (!same_action(replayed[i], first[i])) {
            FAIL() << span_name(span) << ": after reset(), action " << i << " is "
                   << describe(replayed[i]) << " instead of " << describe(first[i]);
        }
    }
}

TEST_P(access_order_turnaround_schedule, NoBlockRecursWithinSixtyFourStreamsFlushesOrVerifies) {
    // A2 for turnaround, whose reads deliberately trail its writes by four blocks: each
    // operation's own sequence (streams, flushes, verifications) touches distinct blocks, within
    // a pass and across the boundary to the next pass, which builds a fresh schedule.
    const std::uint64_t span = GetParam();
    turnaround_schedule schedule(span);
    const std::size_t blocks = static_cast<std::size_t>(schedule.block_count());
    constexpr turnaround_op k_block_ops[] = {turnaround_op::stream, turnaround_op::flush,
                                             turnaround_op::verify};
    std::array<std::vector<std::uint64_t>, 3> last_visit;
    for (std::vector<std::uint64_t>& last : last_visit) {
        last.assign(blocks, UINT64_MAX);
    }
    std::array<std::uint64_t, 3> ordinal{};
    for (std::uint32_t pass = 0; pass < 2; ++pass) {
        const std::array<std::uint64_t, 3> pass_start = ordinal;
        turnaround_action action{};
        while (schedule.next(action)) {
            const auto op = std::find(std::begin(k_block_ops), std::end(k_block_ops), action.op);
            if (op == std::end(k_block_ops)) {
                continue;  // a fence touches no block
            }
            const std::size_t kind = static_cast<std::size_t>(op - std::begin(k_block_ops));
            std::uint64_t& last = last_visit[kind][static_cast<std::size_t>(action.block)];
            if (last != UINT64_MAX && last >= pass_start[kind]) {
                FAIL() << span_name(span) << ", pass " << pass << ": block " << action.block
                       << " receives a second " << op_name(action.op);
            }
            if (last != UINT64_MAX && ordinal[kind] - last < k_alternation_window) {
                FAIL() << span_name(span) << ", pass " << pass << ": block " << action.block
                       << " recurs after only " << ordinal[kind] - last << " " << op_name(action.op)
                       << " actions";
            }
            last = ordinal[kind]++;
        }
        schedule.reset();
    }
    for (std::size_t kind = 0; kind < ordinal.size(); ++kind) {
        EXPECT_EQ(ordinal[kind], 2 * schedule.block_count())
            << span_name(span) << ": " << op_name(k_block_ops[kind]) << " actions";
    }
}

INSTANTIATE_TEST_SUITE_P(AccessOrderTurnaroundWindows, access_order_turnaround_schedule,
                         ::testing::ValuesIn(k_test_spans), span_param_name);

/* ---------------------------------------------------------------------------------------------
 * Turnaround at window and pass boundaries
 * ------------------------------------------------------------------------------------------- */

TEST(AccessOrderWindowAndPassBoundaries, EveryWindowOfEveryPassStartsAfreshAndDrainsItsBlocks) {
    // The windows the runner cuts from a 66 MiB chunk followed by a 6 MiB chunk: 64 MiB, its
    // 2 MiB remainder, then 6 MiB. Blocks are numbered across the whole slice, and one checker
    // follows every block through three passes, so a verification of a block streamed by an
    // earlier window or pass, without a fresh stream, fence, flush and fence, is caught.
    constexpr std::uint64_t k_windows[] = {64 * k_mib, 2 * k_mib, 6 * k_mib};
    constexpr std::uint32_t k_passes = 3;
    std::uint64_t slice_blocks = 0;
    for (const std::uint64_t window : k_windows) {
        slice_blocks += window / k_block_bytes;
    }
    flush_rule_checker checker(slice_blocks);
    for (std::uint32_t pass = 0; pass < k_passes; ++pass) {
        std::uint64_t first_block = 0;
        for (std::size_t w = 0; w < std::size(k_windows); ++w) {
            const std::string where = "pass " + std::to_string(pass) + ", window " +
                                      std::to_string(w) + " (" + span_name(k_windows[w]) + ")";
            turnaround_schedule schedule(k_windows[w]);
            turnaround_action action{};
            ASSERT_TRUE(schedule.next(action)) << where << ": empty schedule";
            // A fresh schedule begins with start-up: a stream of the window's first block, never
            // a verification left over from the previous window or pass.
            EXPECT_TRUE(action.op == turnaround_op::stream &&
                        action.stage == turnaround_stage::startup && action.block == 0)
                << where << ": begins with " << describe(action);
            std::uint64_t index = 0;
            do {
                if (action.op != turnaround_op::fence && action.block >= schedule.block_count()) {
                    FAIL() << where << ", action " << index << " (" << describe(action)
                           << "): the block lies outside the window";
                }
                const std::string violation = checker.apply(action.op, first_block + action.block);
                if (!violation.empty()) {
                    FAIL() << where << ", action " << index << " (" << describe(action)
                           << "): " << violation;
                }
                ++index;
            } while (schedule.next(action));
            // Drained: no block of this window waits for the next window or pass.
            ASSERT_EQ(checker.unverified(), std::uint64_t{0})
                << where << ": blocks left unverified when the window ended";
            first_block += schedule.block_count();
        }
        ASSERT_EQ(first_block, slice_blocks) << "pass " << pass;
    }
}

/* ---------------------------------------------------------------------------------------------
 * Spans and orders the cursor and the schedule refuse
 * ------------------------------------------------------------------------------------------- */

TEST(AccessOrderContract, CursorAndScheduleRefuseSpansThatAreNotNonZero2MiBMultiples) {
    // Exactly-once visitation rests on every stride, block and 1 MiB range dividing the span.
    const std::uint64_t bad_spans[] = {0, k_mib, 3 * k_mib, 2 * k_mib + k_block_bytes};
    for (const std::uint64_t span : bad_spans) {
        for (const order_case& order : k_cursor_orders) {
            EXPECT_THROW(static_cast<void>(order_cursor(params_for(order.order, span, 0))),
                         std::invalid_argument)
                << order.name << " over a span of " << span << " bytes";
        }
        EXPECT_THROW(static_cast<void>(turnaround_schedule(span)), std::invalid_argument)
            << "turnaround over a span of " << span << " bytes";
    }
}

TEST(AccessOrderContract, CursorRefusesTurnaroundAndUnknownOrders) {
    // Turnaround interleaves writes with verifications and runs through turnaround_schedule.
    const std::uint32_t bad_orders[] = {0, RQ_ORDER_TURNAROUND, RQ_ORDER_TURNAROUND + 1};
    for (const std::uint32_t order : bad_orders) {
        EXPECT_THROW(
            static_cast<void>(order_cursor(params_for(order, k_span_granularity_bytes, 0))),
            std::invalid_argument)
            << "order " << order;
    }
}

/* ---------------------------------------------------------------------------------------------
 * Turnaround write times through the real step runner
 * ------------------------------------------------------------------------------------------- */

TEST(AccessOrderTurnaroundMetadata, EveryVerificationCarriesItsOwnBlocksStreamTimeAtEveryBoundary) {
    // Declared in dependency order and destroyed in reverse: the runner first, then the pool,
    // which joins its idle worker before the region, collector, counters, layers, clock and stop
    // signal it may touch are destroyed.
    stop_signal stop;
    fake_clock clock;
    counting_options counting{};
    counting.log_primitives = true;
    counting.max_records_per_worker = k_meta_log_capacity;
    counting.max_workers = 1;
    counting.clock = &clock;
    counting_memory_access memory(std::make_unique<real_memory_access>(
                                      tables_for(select_isa(read_cpu_features(), RQ_ISA_AUTO))),
                                  counting);
    // Every primitive takes k_primitive_ns of fake time, advanced after the call has been
    // forwarded, so a stream's record brackets the stamp the runner takes for its block.
    memory.set_observer([&clock](const primitive_record&) { clock.advance(k_primitive_ns); });

    const std::vector<logical_processor> processors = enumerate_logical_processors();
    ASSERT_FALSE(processors.empty());
    error_collector_options collector_options;
    collector_options.detail_cap = k_meta_detail_cap;
    collector_options.stop_on_error = false;
    collector_options.step_count = 1;
    collector_options.placements = {processors.front()};
    error_collector errors(collector_options, stop, clock, memory);
    coverage_counter coverage(1);

    memory_region region;
    region_options options{};
    options.region_bytes = k_meta_slice_bytes;
    options.min_region_bytes = k_meta_slice_bytes;
    options.worker_count = 1;
    options.lock_privilege_held = false;
    const allocation_outcome allocation = region.allocate(options, stop);
    ASSERT_EQ(allocation.status, allocation_status::ok)
        << "8 MiB could not be locked: " << allocation.end_detail << " (Windows error "
        << allocation.os_error << ")";
    const slice_view whole = region.slice(0);
    ASSERT_EQ(whole.bytes, k_meta_slice_bytes);
    ASSERT_EQ(whole.chunks.size(), std::size_t{1});
    const chunk_view& chunk = whole.chunks.front();
    // Two views of the one allocation. Their logical offsets continue across the split, as
    // across separately allocated chunks, so the runner runs one window in each.
    const std::array<chunk_view, 2> views = {
        chunk_view{chunk.base, k_meta_chunk_bytes[0], chunk.logical_offset, chunk.large_pages},
        chunk_view{chunk.base + k_meta_chunk_bytes[0] / sizeof(std::uint64_t),
                   k_meta_chunk_bytes[1], chunk.logical_offset + k_meta_chunk_bytes[0],
                   chunk.large_pages}};
    const std::array<slice_view, 1> slices = {
        slice_view{whole.worker_index, whole.logical_offset, whole.bytes, views}};

    const std::shared_ptr<thread_affinity> affinity = make_win32_thread_affinity();
    worker_pool pool;
    const pool_start_result started = pool.start(processors, 1, *affinity, stop);
    ASSERT_EQ(started.outcome, pool_start_result::status::ok)
        << "worker 0 did not start on group " << started.group << ", processor "
        << static_cast<unsigned>(started.processor) << " (Windows error " << started.os_error
        << ")";

    errors.begin_run(clock.now_ns());
    step_runner runner(step_runner_dependencies{.slices = slices,
                                                .memory = memory,
                                                .pool = pool,
                                                .errors = errors,
                                                .coverage = coverage,
                                                .clock = clock,
                                                .stop = stop,
                                                .seed = k_seed});
    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), "Read-after-write turnaround");
    step.pattern = RQ_PATTERN_RANDOM;
    step.access_order = RQ_ORDER_TURNAROUND;
    step.thread_mode = RQ_THREADS_ALL;
    step.hold_ms = 0;
    step.time_budget_ms = k_meta_budget_ms;
    step.coverage_budget_bp = k_meta_coverage_budget_bp;
    recording_observer observer;
    const step_outcome outcome = runner.run_step(step, 0, observer);
    pool.shutdown();

    ASSERT_TRUE(outcome.completed) << "the step was stopped, end reason " << stop.pending_reason();
    ASSERT_EQ(outcome.passes, k_meta_passes);
    // One tick after every completed pass at least, the last one reporting the final schedule.
    EXPECT_GE(observer.ticks(), std::uint64_t{outcome.passes});
    EXPECT_EQ(observer.last().passes_done, outcome.passes);
    EXPECT_EQ(observer.last().passes_planned, k_meta_passes);
    ASSERT_EQ(errors.true_total(), std::uint64_t{0}) << "real memory mismatched during the run";
    // Every block, drain blocks included, verified exactly once per pass.
    EXPECT_EQ(outcome.verified_bytes, std::uint64_t{k_meta_passes} * k_meta_slice_bytes);
    EXPECT_EQ(memory.verified_bytes(), std::uint64_t{k_meta_passes} * k_meta_slice_bytes);
    ASSERT_FALSE(memory.log_overflowed());

    const std::vector<primitive_record> log = memory.log_for_worker(0);
    ASSERT_EQ(log.size(), std::size_t{k_meta_passes} * k_meta_pass_primitives)
        << "the runner issued primitives beyond the windows' turnaround schedules";

    // The log must be, primitive for primitive, the schedule of every window of every pass,
    // each beginning with its own start-up and ending with its own drain.
    std::size_t record = 0;
    std::uint64_t verifications = 0;
    std::uint64_t drain_verifications = 0;
    for (std::uint32_t pass = 0; pass < outcome.passes; ++pass) {
        for (std::size_t w = 0; w < views.size(); ++w) {
            const std::uintptr_t window_base = reinterpret_cast<std::uintptr_t>(views[w].base);
            turnaround_schedule schedule(views[w].bytes);
            // Each block's stream record in this window and pass only, so a verification can
            // pair only with a stream of its own window and pass.
            std::vector<std::size_t> stream_record(static_cast<std::size_t>(schedule.block_count()),
                                                   k_no_record);
            turnaround_action action{};
            std::size_t action_index = 0;
            while (schedule.next(action)) {
                const primitive_record& rec = log[record];
                const auto where = [&] {
                    return describe_meta(pass, w, action_index, action, record);
                };
                ASSERT_EQ(rec.kind, primitive_for(action.op))
                    << where() << ": logged " << primitive_name(rec.kind);
                ASSERT_EQ(rec.ctx.step_index, 0U) << where();
                ASSERT_EQ(rec.ctx.pass_index, pass) << where();
                ASSERT_TRUE(rec.ctx.phase == phase_kind::turnaround) << where();
                if (action.op != turnaround_op::fence) {
                    ASSERT_EQ(rec.begin, window_base + action.offset) << where();
                    ASSERT_EQ(rec.bytes, action.length) << where();
                }
                if (action.op == turnaround_op::stream) {
                    stream_record[static_cast<std::size_t>(action.block)] = record;
                } else if (action.op == turnaround_op::verify) {
                    ASSERT_EQ(rec.ctx.access_type, RQ_ACCESS_TURNAROUND_READ) << where();
                    const std::size_t stream_index =
                        stream_record[static_cast<std::size_t>(action.block)];
                    ASSERT_NE(stream_index, k_no_record)
                        << where() << ": the block was not streamed in this window and pass";
                    const primitive_record& stream = log[stream_index];
                    // The stamp lies within the bracket of the block's own stream, whether the
                    // runner takes it just before or just after streaming; brackets of different
                    // streams never overlap. The first stale stamp ends the test, rather than
                    // repeating the same failure for every later block.
                    ASSERT_GE(rec.ctx.write_time_ns, stream.clock_ns)
                        << where() << ": the stamp predates the block's stream at record "
                        << stream_index;
                    ASSERT_LE(rec.ctx.write_time_ns, stream.clock_after_ns)
                        << where() << ": the stamp postdates the block's stream at record "
                        << stream_index;
                    // time_since_write_us = (now - write_time_ns) / 1000 is never negative.
                    ASSERT_GE(rec.clock_ns, rec.ctx.write_time_ns) << where();
                    ++verifications;
                    if (action.stage == turnaround_stage::drain) {
                        ++drain_verifications;
                    }
                }
                ++record;
                ++action_index;
            }
        }
    }
    EXPECT_EQ(record, log.size());
    EXPECT_EQ(verifications, std::uint64_t{k_meta_passes} * (k_meta_slice_bytes / k_block_bytes));
    EXPECT_EQ(drain_verifications,
              std::uint64_t{k_meta_passes} * views.size() * k_turnaround_distance);
}

}  // namespace rq::engine::test
