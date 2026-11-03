/**
 * @file access_order.cpp
 * @brief Access orders, the RandomBlocks permutation and the per-window turnaround schedule.
 *
 * Compiled at the x64 SSE2 baseline. Every function here is pure integer arithmetic on byte
 * offsets: nothing touches test memory, and iterating a cursor or a schedule allocates nothing,
 * so the runner can build one per window or slice per pass.
 *
 * Exposure heuristics: BankStride strides, RandomBlocks permutations and the turnaround distance
 * act on virtual offsets. Physical page mapping, channel and rank interleaving and the memory
 * controller's bank hashing are invisible to software, so these orders make row, bank and rank
 * changes and write-to-read switches likely; they never guarantee a physical sequence.
 *
 * Stress bound (assumption A2): every order visits each line or block of its span exactly once
 * per phase, nothing alternates over a small address set, and the turnaround schedule flushes
 * each block once, only where the flush-before-verification rule needs it, so there is no
 * flush-and-reload loop. Row-hammer patterns are out of scope.
 */
#include "core/patterns/access_order.h"

#include <stdexcept>

namespace rq::engine {

namespace {

/** SplitMix64 increment (2^64 / golden ratio, odd): spaces the per-round key states. */
constexpr std::uint64_t k_golden_gamma = 0x9E3779B97F4A7C15ull;

/** Pre-mix offset of the seed, so seed 0 does not start the chain at the mixer's fixed point. */
constexpr std::uint64_t k_seed_offset = 0xD1B54A32D192ED03ull;

/**
 * SplitMix64 finalizer: a bijection on 64-bit values with full avalanche, so every input bit
 * affects every output bit. It is the Feistel round function and the key-chaining step.
 */
constexpr std::uint64_t mix64(std::uint64_t z) noexcept {
    z ^= z >> 30;
    z *= 0xBF58476D1CE4E5B9ull;
    z ^= z >> 27;
    z *= 0x94D049BB133111EBull;
    z ^= z >> 31;
    return z;
}

/**
 * Turnaround stage lengths in actions, written in terms of the turnaround distance D:
 * start-up streams and fences D blocks (2D actions), each steady iteration issues stream, flush,
 * fence, verify (4 actions) for each of the n - D remaining blocks, and drain flushes D blocks,
 * fences once and verifies D blocks (2D + 1 actions).
 */
constexpr std::uint64_t k_startup_actions = 2 * k_turnaround_distance;
constexpr std::uint64_t k_steady_actions_per_block = 4;
constexpr std::uint64_t k_drain_actions = 2 * k_turnaround_distance + 1;

static_assert(k_startup_actions + k_drain_actions ==
                  k_steady_actions_per_block * k_turnaround_distance + 1,
              "the stage lengths must total turnaround_schedule::action_count(), 4n + 1");

/** A span is valid when it is a non-zero multiple of the 2 MiB span granularity. */
constexpr bool is_valid_span(std::uint64_t span_bytes) noexcept {
    return span_bytes != 0 && span_bytes % k_span_granularity_bytes == 0;
}

/** A stream, flush or verify of one whole block. */
constexpr turnaround_action block_action(turnaround_op op, turnaround_stage stage,
                                         std::uint64_t block) noexcept {
    return turnaround_action{op, stage, block, block * k_block_bytes, k_block_bytes};
}

/** An MFENCE; it names no block, so block, offset and length are all 0. */
constexpr turnaround_action fence_action(turnaround_stage stage) noexcept {
    return turnaround_action{turnaround_op::fence, stage, 0, 0, 0};
}

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * Permutation key and block permutation
 * ------------------------------------------------------------------------------------------- */

/*
 * key = mix64(mix64(mix64(seed ^ 0xD1B54A32D192ED03) ^ ((step_index << 32) | pass)) ^ salt),
 * where mix64 is the SplitMix64 finalizer and step_index is widened to 64 bits before the shift.
 *
 * Step and pass are packed into disjoint halves rather than mixed arithmetically, so distinct
 * (step_index, pass) pairs are distinct values exactly. mix64 is a bijection, so for a fixed
 * prefix every stage maps distinct inputs to distinct outputs: two keys that differ only in one
 * input never collide, and keys that differ in several collide only by chance, with probability
 * about 2^-64.
 */
std::uint64_t permutation_key(std::uint64_t seed, std::uint32_t step_index, std::uint32_t pass,
                              std::uint64_t salt) noexcept {
    const std::uint64_t step_and_pass = (static_cast<std::uint64_t>(step_index) << 32) | pass;
    std::uint64_t key = mix64(seed ^ k_seed_offset);
    key = mix64(key ^ step_and_pass);
    return mix64(key ^ salt);
}

block_permutation::block_permutation(std::uint64_t count, std::uint64_t key) noexcept
    : count_(count) {
    // The smallest h >= 1 with 4^h >= count. The shift is evaluated only for h <= 31; h = 32
    // gives a 2^64 domain, which holds every 64-bit count, so no shift ever reaches 64.
    std::uint32_t h = 1;
    while (h < 32 && (std::uint64_t{1} << (2 * h)) < count) {
        ++h;
    }
    half_bits_ = h;
    half_mask_ = (std::uint64_t{1} << h) - 1;

    // Round keys are the first k_feistel_rounds outputs of a SplitMix64 stream seeded with key,
    // so each round uses an independent-looking key derived from the one 64-bit input.
    for (std::uint32_t round = 0; round < k_feistel_rounds; ++round) {
        round_keys_[round] = mix64(key + (round + 1) * k_golden_gamma);
    }
}

std::uint64_t block_permutation::operator()(std::uint64_t index) const noexcept {
    // A single-element domain has one image. count 0 breaks the constructor's precondition;
    // answering 0 rather than walking keeps the call from looping forever.
    if (count_ <= 1) {
        return 0;
    }
    // index >= count breaks the precondition. Reducing it keeps the image inside the domain, so
    // a caller's offset stays inside its span, and keeps the walk off a cycle that might lie
    // wholly at or above count and never return. Valid indexes pass through unchanged.
    std::uint64_t x = index < count_ ? index : index % count_;

    // Each round maps (L, R) to (R, L ^ F(R)), which is invertible whatever F is, so the network
    // is a bijection on [0, 4^h). Cycle walking re-applies it until the value falls below count:
    // the orbit of x is a cycle that contains x itself, and x < count, so the walk always ends,
    // and distinct inputs end at distinct first in-range points of their cycles.
    do {
        std::uint64_t left = x >> half_bits_;
        std::uint64_t right = x & half_mask_;
        for (const std::uint64_t round_key : round_keys_) {
            const std::uint64_t next_right = left ^ (mix64(right ^ round_key) & half_mask_);
            left = right;
            right = next_right;
        }
        x = (left << half_bits_) | right;
    } while (x >= count_);
    return x;
}

/* ---------------------------------------------------------------------------------------------
 * order_cursor
 * ------------------------------------------------------------------------------------------- */

order_cursor::order_cursor(const order_params& p) : order_(p.order), span_bytes_(p.span_bytes) {
    // Session validation makes both throws unreachable; the messages become end_detail through
    // the thread-entry catch if a runner defect ever reaches them.
    if (!is_valid_span(p.span_bytes)) {
        throw std::invalid_argument("access order: span is not a non-zero multiple of 2 MiB");
    }
    switch (p.order) {
    case RQ_ORDER_SEQUENTIAL_ASCENDING:
    case RQ_ORDER_SEQUENTIAL_DESCENDING:
        total_ranges_ = span_bytes_ / k_sequential_range_bytes;
        break;
    case RQ_ORDER_BANK_STRIDE:
        // Every stride divides 2 MiB (static_assert in the header), so lines_per_sweep is exact
        // and the stride / 64 sweeps together visit every line once.
        stride_bytes_ = bank_stride_bytes(p.pass);
        lines_per_sweep_ = span_bytes_ / stride_bytes_;
        total_ranges_ = span_bytes_ / k_line_bytes;
        break;
    case RQ_ORDER_RANDOM_BLOCKS:
        total_ranges_ = span_bytes_ / k_block_bytes;
        permutation_ =
            block_permutation(total_ranges_, permutation_key(p.seed, p.step_index, p.pass, p.salt));
        break;
    default:
        throw std::invalid_argument(
            "access order: order is not sequential, bank-stride or random-block "
            "(turnaround uses turnaround_schedule)");
    }
}

bool order_cursor::next(order_range& out) noexcept {
    if (next_index_ >= total_ranges_) {
        return false;
    }
    const std::uint64_t k = next_index_;
    switch (order_) {
    case RQ_ORDER_SEQUENTIAL_ASCENDING:
        out = order_range{k * k_sequential_range_bytes, k_sequential_range_bytes, 1};
        break;
    case RQ_ORDER_SEQUENTIAL_DESCENDING:
        // The last range first, each traversed downwards, so the whole span runs high to low.
        out = order_range{(total_ranges_ - 1 - k) * k_sequential_range_bytes,
                          k_sequential_range_bytes, -1};
        break;
    case RQ_ORDER_BANK_STRIDE: {
        // Within a sweep, consecutive lines are one stride apart, which makes row and bank
        // changes likely under common interleave granularities. The mapping is invisible to
        // software, so this is an exposure heuristic, not a guaranteed physical sequence. Each
        // sweep starts one line further on, so the sweeps visit every line exactly once and no
        // line is revisited within the phase.
        const std::uint64_t sweep = k / lines_per_sweep_;
        const std::uint64_t line_in_sweep = k % lines_per_sweep_;
        out = order_range{sweep * k_line_bytes + line_in_sweep * stride_bytes_, k_line_bytes, 1};
        break;
    }
    default:
        // RQ_ORDER_RANDOM_BLOCKS: the constructor admits no other order. A bijection on the
        // block indexes visits every block once, in a seeded order unrelated to its neighbours.
        out = order_range{permutation_(k) * k_block_bytes, k_block_bytes, 1};
        break;
    }
    ++next_index_;
    bytes_emitted_ += out.length;
    return true;
}

void order_cursor::reset() noexcept {
    // Every range is a pure function of k and the constructor's parameters, so rewinding k
    // replays the identical sequence.
    next_index_ = 0;
    bytes_emitted_ = 0;
}

/* ---------------------------------------------------------------------------------------------
 * turnaround_schedule
 *
 * Why this order: every block b receives stream b; a fence (its own start-up fence, or the
 * fence of the steady iteration that streamed it); flush b; a fence; verify b. That is the
 * flush-before-verification rule: the first fence makes b's streaming stores globally visible
 * before the flush, the flush evicts any cached copy of b's lines, including one a previous
 * pass's verification loaded, and the second fence orders the flush before the verification
 * loads. Each block is streamed, flushed and verified exactly once per pass, and every verify
 * names the block whose stream it checks, so the runner records exact time_since_write_us.
 *
 * Exposure heuristic: verifying D blocks behind the write makes reads follow writes to nearby
 * virtual addresses within microseconds; which banks, ranks and rows those are is invisible to
 * software. Each block is flushed once per pass, so there is no flush-and-reload loop.
 * ------------------------------------------------------------------------------------------- */

turnaround_schedule::turnaround_schedule(std::uint64_t span_bytes) {
    // Session validation makes this unreachable; see order_cursor's constructor.
    if (!is_valid_span(span_bytes)) {
        throw std::invalid_argument(
            "turnaround schedule: span is not a non-zero multiple of 2 MiB");
    }
    // A 2 MiB multiple holds at least 512 blocks (static_assert in the header), so the start-up
    // and drain blocks are always distinct and the steady stage covers n - D >= D blocks.
    block_count_ = span_bytes / k_block_bytes;
}

bool turnaround_schedule::next(turnaround_action& out) noexcept {
    const std::uint64_t ordinal = next_action_;
    if (ordinal >= action_count()) {
        return false;
    }
    // The ordinal maps arithmetically to (stage, op, block), so the schedule needs no state
    // beyond one counter and reset() replays it exactly.
    const std::uint64_t n = block_count_;
    const std::uint64_t steady_end =
        k_startup_actions + k_steady_actions_per_block * (n - k_turnaround_distance);
    if (ordinal < k_startup_actions) {
        // Start-up: stream b, fence, for b = 0 ... D - 1. Nothing is verified yet: the first
        // verification waits until D blocks stand between it and the write stream.
        const std::uint64_t block = ordinal / 2;
        out = ordinal % 2 == 0
                  ? block_action(turnaround_op::stream, turnaround_stage::startup, block)
                  : fence_action(turnaround_stage::startup);
    } else if (ordinal < steady_end) {
        // Steady state, for i = D ... n - 1: stream i, flush i - D, fence, verify i - D. Block
        // i - D's stores were fenced in the iteration that streamed it, so this iteration's
        // flush and fence complete the rule for it.
        const std::uint64_t position = ordinal - k_startup_actions;
        const std::uint64_t streamed =
            k_turnaround_distance + position / k_steady_actions_per_block;
        const std::uint64_t checked = streamed - k_turnaround_distance;
        switch (position % k_steady_actions_per_block) {
        case 0:
            out = block_action(turnaround_op::stream, turnaround_stage::steady, streamed);
            break;
        case 1:
            out = block_action(turnaround_op::flush, turnaround_stage::steady, checked);
            break;
        case 2:
            out = fence_action(turnaround_stage::steady);
            break;
        default:
            out = block_action(turnaround_op::verify, turnaround_stage::steady, checked);
            break;
        }
    } else {
        // Drain, after the last stream: flush n - D ... n - 1, one fence, verify n - D ... n - 1.
        // The last fence of the steady stage already follows the final stream, so these blocks
        // meet the same rule as every other.
        const std::uint64_t position = ordinal - steady_end;
        const std::uint64_t first = n - k_turnaround_distance;
        if (position < k_turnaround_distance) {
            out = block_action(turnaround_op::flush, turnaround_stage::drain, first + position);
        } else if (position == k_turnaround_distance) {
            out = fence_action(turnaround_stage::drain);
        } else {
            out = block_action(turnaround_op::verify, turnaround_stage::drain,
                               first + (position - k_turnaround_distance - 1));
        }
    }
    next_action_ = ordinal + 1;
    return true;
}

void turnaround_schedule::reset() noexcept {
    next_action_ = 0;
}

}  // namespace rq::engine
