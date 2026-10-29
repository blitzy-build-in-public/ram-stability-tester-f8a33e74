/**
 * @file access_order.h
 * @brief Access orders over a span, and the per-window turnaround schedule.
 *
 * A span is the address range one pass phase covers: a window of at most k_max_window_bytes for
 * windowed and turnaround steps, or a worker's whole slice for hold steps and moving-inversion
 * sweeps. Every offset here is a byte offset relative to the span start. Nothing here touches
 * memory: the step runner (core/runner/step_runner.cpp) maps offsets to chunk pointers and
 * logical word indices and issues every memory primitive through IMemoryAccess.
 *
 * @par Which order a step uses
 * - Windowed steps (no hold) write and then verify each window in the step's access order. The
 *   verify phase uses a cursor with identical parameters (or reset()), so both phases visit the
 *   same sequence and each verified group can be paired with its write time.
 * - Turnaround steps execute one turnaround_schedule per window per pass.
 * - Hold steps write and verify the whole slice in RQ_ORDER_RANDOM_BLOCKS order, whatever the
 *   step's access order.
 * - Moving-inversion steps ignore the access order: the base write and the ascending sweep use
 *   RQ_ORDER_SEQUENTIAL_ASCENDING, and the descending sweep RQ_ORDER_SEQUENTIAL_DESCENDING, over
 *   the slice.
 *
 * @par Exposure heuristics
 * Strides and orders act on virtual offsets. Physical page mapping, channel and rank
 * interleaving and the memory controller's bank hashing are invisible to software. BankStride,
 * RandomBlocks and Turnaround therefore make frequent row, bank and rank changes and
 * write-to-read switches likely; they do not guarantee any particular physical sequence.
 *
 * @par Stress bound (assumption A2 in docs/METHODOLOGY.md)
 * Every order visits each line or block of its span exactly once per phase, so no order
 * alternates over a small address set. The turnaround schedule flushes each block once, only
 * where the flush-before-verification rule needs it, so there is no flush-and-reload loop.
 * Whole-window sweeps spread activations across many rows, unlike row-hammer loops, which the
 * product excludes.
 *
 * @par Build constraint
 * Include only from baseline (SSE2) translation units: core/patterns/access_order.cpp,
 * core/runner/step_runner.cpp and the tests in tests/engine.tests. Never include it from
 * core/kernels/kernels.h, core/patterns/patterns.h, kernels_avx2.cpp or kernels_avx512.cpp: its
 * inline functions have external linkage, and compiled in a /arch:AVX2 or /arch:AVX512 unit they
 * would become COMDATs the linker may select for baseline callers, running those instructions
 * before ISA dispatch has chosen them.
 *
 * @par Definitions
 * bank_stride_bytes and the const accessors are inline here. permutation_key, the constructor
 * and call operator of block_permutation, and the constructors, next() and reset() of
 * order_cursor and turnaround_schedule are defined in core/patterns/access_order.cpp.
 */
#pragma once

#include <array>
#include <cstdint>

#include "ramquick_engine.h"

namespace rq::engine {

/** One cache line: the unit BankStride visits, and the granularity of every flush. */
inline constexpr std::uint64_t k_line_bytes = 64;

/** The 4 KiB block that RandomBlocks permutes and Turnaround streams, flushes and verifies. */
inline constexpr std::uint64_t k_block_bytes = 4096;

/**
 * Largest sequential range. Workers check the stop flag and add to the coverage counter after
 * every 1 MiB of work, so no range is longer than that unit.
 */
inline constexpr std::uint64_t k_sequential_range_bytes = 1ull << 20;

/**
 * Granularity of a span: every window and slice is a non-zero multiple of 2 MiB. Chunk and slice
 * sizes are 2 MiB multiples, so every stride, block and 1 MiB range divides a span evenly and no
 * range crosses a chunk boundary.
 */
inline constexpr std::uint64_t k_span_granularity_bytes = 2ull << 20;

/**
 * Window of a windowed or turnaround step. Writing and then verifying at most 64 MiB at a time
 * keeps the time since write short however large the slice. The runner cuts windows of at most
 * this size inside one chunk.
 */
inline constexpr std::uint64_t k_max_window_bytes = 64ull << 20;

/**
 * BankStride strides, cycled per pass. Power-of-two virtual strides at and above common row and
 * bank-interleave granularities, so consecutive visits are likely to change row or bank. The
 * physical mapping is unknown, so this is a heuristic.
 */
inline constexpr std::uint64_t k_bank_strides[3] = {8ull << 10, 64ull << 10, 256ull << 10};

/**
 * Turnaround verifies a block this many blocks behind its write, so reads follow writes to
 * nearby addresses within microseconds. Block i is streamed before block i - 4 is verified, so
 * the runner's ring of per-block stream times needs at least k_turnaround_distance + 1 entries.
 */
inline constexpr std::uint64_t k_turnaround_distance = 4;

static_assert(k_block_bytes % k_line_bytes == 0, "a block must be whole cache lines");
static_assert(k_sequential_range_bytes % k_block_bytes == 0,
              "a sequential range must be whole blocks");
static_assert(k_span_granularity_bytes % k_sequential_range_bytes == 0,
              "a span must be whole sequential ranges");
static_assert(k_max_window_bytes % k_span_granularity_bytes == 0,
              "the maximum window must be a valid span");
static_assert(k_bank_strides[0] % k_line_bytes == 0 && k_bank_strides[1] % k_line_bytes == 0 &&
                  k_bank_strides[2] % k_line_bytes == 0,
              "every stride must be whole cache lines");
static_assert(k_span_granularity_bytes % k_bank_strides[0] == 0 &&
                  k_span_granularity_bytes % k_bank_strides[1] == 0 &&
                  k_span_granularity_bytes % k_bank_strides[2] == 0,
              "every stride must divide every span, or BankStride would miss lines");
static_assert(k_span_granularity_bytes / k_block_bytes >= 2 * k_turnaround_distance,
              "the smallest span must keep turnaround start-up and drain blocks distinct");

/**
 * BankStride stride of a pass: k_bank_strides[pass % 3], so consecutive passes cycle through
 * 8 KiB, 64 KiB and 256 KiB.
 */
constexpr std::uint64_t bank_stride_bytes(std::uint32_t pass) noexcept {
    return k_bank_strides[pass % 3u];
}

/**
 * Key of the RandomBlocks permutation of one window or slice: a deterministic 64-bit mix of all
 * four inputs. The same inputs always reproduce the same order, and a different value in any
 * input gives an unrelated order with overwhelming probability.
 *
 * @param seed Session seed (rq_session_config.seed).
 * @param step_index 0-based index of the step in the plan.
 * @param pass 0-based pass index within the step.
 * @param salt Runner-chosen discriminator, for example worker and window index.
 */
std::uint64_t permutation_key(std::uint64_t seed, std::uint32_t step_index, std::uint32_t pass,
                              std::uint64_t salt) noexcept;

/**
 * Stateless keyed bijection on [0, count): the RandomBlocks visiting order.
 *
 * A balanced Feistel network on 2h bits, where h >= 1 is the smallest value with 4^h >= count,
 * applies k_feistel_rounds rounds of a keyed 64-bit mix. Each round is invertible whatever the
 * mix, so the network permutes [0, 4^h). Cycle walking restricts it to [0, count): the network is
 * applied repeatedly until the result is below count. The walk stays on the cycle that contains
 * the input, and the input is itself below count, so it terminates and the restriction is a
 * bijection. The choice of h gives 4^h <= 4 * count, so the mean number of applications over all
 * inputs, 4^h / count, is at most 4.
 *
 * It holds no table, so a slice of tens of millions of blocks costs a few words, and building
 * one per window or slice per pass allocates nothing. Copyable.
 */
class block_permutation {
public:
    /** Feistel rounds per application; four rounds of a strong mix scramble the order well. */
    static constexpr std::uint32_t k_feistel_rounds = 4;

    /**
     * @param count Domain size. Precondition: count >= 1. Any larger value is valid; h never
     *        exceeds 32.
     * @param key Permutation key, normally from permutation_key().
     */
    block_permutation(std::uint64_t count, std::uint64_t key) noexcept;

    /**
     * Image of index under the permutation. Pure.
     *
     * @param index Precondition: index < size().
     * @return A value in [0, size()); distinct indexes give distinct values.
     */
    std::uint64_t operator()(std::uint64_t index) const noexcept;

    /** Domain size: the count given at construction. */
    std::uint64_t size() const noexcept { return count_; }

private:
    std::uint64_t count_ = 1;
    /** h: the bit width of each Feistel half. */
    std::uint32_t half_bits_ = 1;
    /** (1 << h) - 1. */
    std::uint64_t half_mask_ = 1;
    /** One key per round, derived from the constructor's key. */
    std::array<std::uint64_t, k_feistel_rounds> round_keys_{};
};

/** Parameters of one order_cursor: the order, the span and the RandomBlocks inputs. */
struct order_params {
    /**
     * RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_ORDER_SEQUENTIAL_DESCENDING, RQ_ORDER_BANK_STRIDE or
     * RQ_ORDER_RANDOM_BLOCKS.
     */
    std::uint32_t order = 0;
    /**
     * Span length in bytes, a non-zero multiple of k_span_granularity_bytes: the window for
     * windowed steps, the slice for hold steps and moving-inversion sweeps.
     */
    std::uint64_t span_bytes = 0;
    /** Session seed (rq_session_config.seed); feeds the RandomBlocks permutation key. */
    std::uint64_t seed = 0;
    /** 0-based step index; feeds the RandomBlocks permutation key. */
    std::uint32_t step_index = 0;
    /** 0-based pass index within the step; selects the BankStride stride and feeds the key. */
    std::uint32_t pass = 0;
    /**
     * Runner-chosen discriminator, for example worker and window index, so that each window or
     * slice gets its own RandomBlocks permutation. Equal salts give equal sequences, and
     * different salts let the runner distinguish phases if it wants to.
     */
    std::uint64_t salt = 0;
};

/**
 * One contiguous range yielded by order_cursor. Every range lies inside one aligned
 * k_sequential_range_bytes unit of the span, so it never crosses a chunk and the runner's
 * 1 MiB stop and coverage checks fall between ranges.
 */
struct order_range {
    /** Lowest byte offset of the range, relative to the span start, whatever the direction. */
    std::uint64_t offset = 0;
    /** Length in bytes: k_sequential_range_bytes, k_line_bytes or k_block_bytes by order. */
    std::uint64_t length = 0;
    /**
     * +1 to traverse from offset upwards; -1 to traverse from offset + length down to offset.
     * Passed unchanged as the kernels' direction argument, which takes the same low-end pointer
     * and changes only the order of the accesses.
     */
    std::int32_t direction = 1;
};

/**
 * Iterates the ranges of one pass phase over a span in one access order. Each order visits
 * every byte of the span exactly once per phase:
 *
 * - RQ_ORDER_SEQUENTIAL_ASCENDING: ranges (k * 1 MiB, 1 MiB) for k = 0 ... span / 1 MiB - 1,
 *   direction +1.
 * - RQ_ORDER_SEQUENTIAL_DESCENDING: the same ranges from the last to the first, direction -1, so
 *   the whole span is traversed from its high end to its low end.
 * - RQ_ORDER_BANK_STRIDE: stride s = bank_stride_bytes(pass). There are s / 64 sweeps; sweep j
 *   visits the 64-byte lines at offsets j * 64 + k * s for k = 0 ... span / s - 1, one line per
 *   range, direction +1. The start offset advances one line per sweep, so consecutive visits
 *   stay a stride apart while every line is still visited exactly once. Exposure heuristic: the
 *   stride is virtual, and whether it changes bank, row or rank depends on a mapping software
 *   cannot see.
 * - RQ_ORDER_RANDOM_BLOCKS: N = span / 4096 blocks; range i is (perm(i) * 4096, 4096), direction
 *   +1, with perm = block_permutation(N, permutation_key(seed, step_index, pass, salt)): a seeded
 *   permutation per window, or per slice for hold steps. Exposure heuristic: random virtual
 *   blocks make row misses and bank, rank and channel changes likely, not certain.
 *
 * RQ_ORDER_TURNAROUND interleaves writes with verifications and is driven by
 * turnaround_schedule instead.
 *
 * The cursor holds only integers and a block_permutation: it is cheap to copy, and iterating
 * allocates nothing, so the runner builds one per window or slice per pass.
 */
class order_cursor {
public:
    /**
     * @throws std::invalid_argument when span_bytes is 0 or not a multiple of
     *         k_span_granularity_bytes, or when order is unknown or RQ_ORDER_TURNAROUND. Session
     *         validation makes this unreachable; the worker or coordinator thread entry that
     *         catches it ends the run with RQ_END_INTERNAL_FAULT.
     */
    explicit order_cursor(const order_params& p);

    /**
     * Yields the next range of the phase.
     *
     * @return true with the range in out; false, without writing out, once every range of the
     *         phase has been yielded.
     */
    bool next(order_range& out) noexcept;

    /**
     * Restarts the identical sequence from the first range, so a window's verify phase can
     * revisit its write phase's order.
     */
    void reset() noexcept;

    /**
     * Ranges per phase: span / 1 MiB for the sequential orders, span / 64 for BankStride and
     * span / 4096 for RandomBlocks.
     */
    std::uint64_t total_ranges() const noexcept { return total_ranges_; }

    /**
     * Bytes yielded since construction or the last reset(); lets the runner check the stop flag
     * and add coverage every 1 MiB.
     */
    std::uint64_t bytes_emitted() const noexcept { return bytes_emitted_; }

private:
    /** The RQ_ORDER_* value. */
    std::uint32_t order_ = 0;
    std::uint64_t span_bytes_ = 0;
    std::uint64_t total_ranges_ = 0;
    /** BankStride only: s. */
    std::uint64_t stride_bytes_ = 0;
    /** BankStride only: span / s, the lines visited per sweep. */
    std::uint64_t lines_per_sweep_ = 0;
    /** RandomBlocks only; the single-element default keeps the member valid for other orders. */
    block_permutation permutation_{1, 0};
    /** Ordinal of the next range, k. */
    std::uint64_t next_index_ = 0;
    std::uint64_t bytes_emitted_ = 0;
};

/** Operation of one turnaround_action; the runner issues it through IMemoryAccess. */
enum class turnaround_op : std::uint32_t {
    /** Non-temporal write of the block's pattern; the runner records the block's stream time. */
    stream,
    /**
     * MFENCE: orders every earlier load, store, streaming store and flush before every later
     * load or store.
     */
    fence,
    /** Write back and invalidate every line of the block at every cache level; no fence. */
    flush,
    /** Verification loads of the block, counted as coverage, as RQ_ACCESS_TURNAROUND_READ. */
    verify,
};

/** The part of a window's schedule an action belongs to. */
enum class turnaround_stage : std::uint32_t {
    /** Blocks 0 to 3 streamed and fenced; nothing verified yet. */
    startup,
    /** Block i streamed, then block i - 4 flushed, fenced and verified, for i = 4 ... n - 1. */
    steady,
    /** After the last stream: the last four blocks flushed, fenced and verified. */
    drain,
};

/** One action of a turnaround_schedule. */
struct turnaround_action {
    /** What to issue. */
    turnaround_op op = turnaround_op::fence;
    /** Which part of the schedule the action belongs to. */
    turnaround_stage stage = turnaround_stage::startup;
    /** Block index within the window; 0, and meaningless, for a fence. */
    std::uint64_t block = 0;
    /** block * k_block_bytes, relative to the window start; 0 for a fence. */
    std::uint64_t offset = 0;
    /** k_block_bytes; 0 for a fence. */
    std::uint64_t length = 0;
};

/**
 * The action sequence of one turnaround window of n = span / 4096 blocks. The runner builds a
 * fresh schedule per window per pass, so every window of every pass begins with start-up.
 *
 * The sequence has 4n + 1 actions (n streams, n flushes, n verifications, n + 1 fences):
 * - Start-up, 8 actions: for b = 0 ... 3, stream b, then fence. No verification.
 * - Steady state, 4 actions per i, for i = 4 ... n - 1: stream i, flush i - 4, fence,
 *   verify i - 4.
 * - Drain, 9 actions: flush n - 4, flush n - 3, flush n - 2, flush n - 1, fence, verify n - 4,
 *   verify n - 3, verify n - 2, verify n - 1.
 *
 * Flush before verification. Every block sees, in this order: its stream; a fence (the start-up
 * fence after its stream, or the fence of the steady iteration that streamed it); its flush; a
 * fence; its verification. The first fence makes the block's streaming stores globally visible
 * before the flush. The flush removes any cached copy, including one a previous pass's
 * verification left behind. The second fence orders the flush before the verification loads. No
 * verification is therefore served by a cached copy older than the block's write.
 *
 * Every block is streamed once and verified once per pass. Each verify names its block, so the
 * runner pairs it with that block's recorded stream time, and time_since_write_us is exact for
 * steady-state and drain blocks alike. Drain verifications are turnaround reads too: the runner
 * reports every verify as RQ_ACCESS_TURNAROUND_READ. Blocks streamed but not yet verified when a
 * stop arrives are never counted as coverage; the runner adds coverage only after a verify.
 *
 * Exposure heuristic: a read four blocks behind the write makes frequent write-to-read
 * switches on nearby virtual addresses; which banks, ranks and rows they reach is invisible to
 * software.
 */
class turnaround_schedule {
public:
    /**
     * @throws std::invalid_argument unless span_bytes is a non-zero multiple of
     *         k_span_granularity_bytes, which guarantees n = span / 4096 >= 512.
     */
    explicit turnaround_schedule(std::uint64_t span_bytes);

    /**
     * Yields the next action of the window.
     *
     * @return true with the action in out; false, without writing out, once all
     *         action_count() actions have been yielded.
     */
    bool next(turnaround_action& out) noexcept;

    /** Restarts the identical sequence from the first start-up action. */
    void reset() noexcept;

    /** n, the blocks in the window. */
    std::uint64_t block_count() const noexcept { return block_count_; }

    /** 4n + 1, the actions in the window's sequence. */
    std::uint64_t action_count() const noexcept { return 4 * block_count_ + 1; }

private:
    std::uint64_t block_count_ = 0;
    /** Ordinal of the next action, in [0, action_count()]. */
    std::uint64_t next_action_ = 0;
};

}  // namespace rq::engine
