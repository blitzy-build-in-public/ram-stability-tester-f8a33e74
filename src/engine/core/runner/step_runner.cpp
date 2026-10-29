/**
 * @file step_runner.cpp
 * @brief The pass phases of the four step kinds, composed from IMemoryAccess primitives under
 *        the flush-before-verification rule, and pass scheduling against the step's time budget.
 *
 * Division of labour:
 * - The coordinator (run_step) posts one phase task per pass phase to the worker pool, waits on
 *   the phase barrier and through holds in slices of k_observer_tick_ms, ticks the observer
 *   between slices and after every completed pass, and schedules passes from their measured mean.
 * - Workers (the phase tasks) issue every memory primitive through IMemoryAccess, so the counting
 *   and fault-injecting test layers see the exact sequence. They check the stop flag before every
 *   1 MiB work group, count coverage after every verification call, and never touch the observer
 *   or any C API callback.
 *
 * Stress bound: every order visits each line or block of its span exactly once per phase, so no
 * step alternates over a small address set. Lines are flushed only where the rule needs it: once
 * before each verification, once more before a hold, and once per re-read (which the error
 * collector issues). Nothing loops flush-and-reload, the row-hammer style the product excludes.
 * Orders act on virtual addresses; physical mapping, interleaving and bank hashing are invisible
 * to software, so BankStride and Turnaround are exposure heuristics, not guaranteed physical
 * sequences.
 */
#include "core/runner/step_runner.h"
#include "core/runner/worker_pool.h"
#include "core/runner/coverage_counter.h"
#include "core/clock.h"
#include "core/memory/memory_access.h"
#include "core/memory/memory_region.h"
#include "core/patterns/patterns.h"
#include "core/patterns/access_order.h"
#include "core/errors/error_collector.h"
#include "ramquick_engine.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <utility>
#include <vector>

namespace rq::engine {

// step_runner.h cannot include these headers, so the equalities its constants promise are
// checked here, where all of them are visible.
static_assert(k_work_group_bytes == k_coverage_publish_bytes,
              "the stop and coverage unit must be the coverage counter's publication unit");
static_assert(k_work_group_bytes == k_sequential_range_bytes,
              "a sequential range must be exactly one work group");
static_assert(k_window_bytes == k_max_window_bytes,
              "the runner's window must be the access orders' maximum window");
static_assert(k_work_group_bytes % k_block_bytes == 0 && k_work_group_bytes % k_line_bytes == 0,
              "every BankStride line and RandomBlocks block must fall inside one work group");

namespace {

constexpr std::uint64_t k_ns_per_ms = 1'000'000;

/** Bytes per 64-bit word: pointer arithmetic and logical word indices are in words. */
constexpr std::uint64_t k_word_bytes = sizeof(std::uint64_t);

/** Work groups per full window: the size of a windowed task's stream-time table. */
constexpr std::size_t k_groups_per_window =
    static_cast<std::size_t>(k_window_bytes / k_work_group_bytes);

/** 4 KiB turnaround blocks per work group: the turnaround stop-check interval (256 blocks). */
constexpr std::uint64_t k_blocks_per_group = k_work_group_bytes / k_block_bytes;

/**
 * Ring of turnaround stream times, indexed by block number modulo its size. Block i is streamed
 * before block i - k_turnaround_distance is verified, so at most k_turnaround_distance + 1 stamps
 * are live at once; a power of two keeps the modulo a mask.
 */
constexpr std::size_t k_turnaround_ring = 8;
static_assert(k_turnaround_ring >= k_turnaround_distance + 1,
              "the ring must hold every block streamed but not yet verified, plus the new one");

/**
 * What one pass's phase tasks need, captured by value in every task: a phase can outlive
 * run_step's frame when an exception leaves it, so no task refers to that frame.
 */
struct pass_params {
    std::uint32_t step_index = 0;
    std::uint32_t pass = 0;
    /** The step's RQ_ORDER_* value; windowed tasks iterate it, the other kinds fix their own. */
    std::uint32_t order = 0;
    /** RQ_ACCESS_* of the step kind's verifications (verify_access_type). */
    std::uint32_t access_type = 0;
    active_set set = active_set::all;
    /** Session seed: feeds the RandomBlocks permutation key together with step and pass. */
    std::uint64_t order_seed = 0;
    /** The pattern this pass writes and verifies, seeded per step. */
    pattern_spec base{};
    /** base inverted: the ascending sweep writes it and the descending sweep checks it. */
    pattern_spec complement{};
};

/** Session-clock time elapsed since start, 0 if a clock ever reads earlier than start. */
std::uint64_t elapsed_since(const IClock& clock, std::uint64_t start) noexcept {
    const std::uint64_t now = clock.now_ns();
    return now > start ? now - start : 0;
}

/** Word count of a byte length; every range here is whole words. */
constexpr std::size_t words_of(std::uint64_t bytes) noexcept {
    return static_cast<std::size_t>(bytes / k_word_bytes);
}

/** First word of the range at offset bytes into the chunk. */
std::uint64_t* address_in(const chunk_view& chunk, std::uint64_t offset) noexcept {
    return chunk.base + offset / k_word_bytes;
}

/** Logical word index of the word at offset bytes into the chunk: what the patterns key on. */
constexpr std::uint64_t logical_word(const chunk_view& chunk, std::uint64_t offset) noexcept {
    return (chunk.logical_offset + offset) / k_word_bytes;
}

/** Slice-relative offset of a chunk: logical offsets concatenate each slice's chunks in order. */
constexpr std::uint64_t offset_in_slice(const slice_view& slice, const chunk_view& chunk) noexcept {
    return chunk.logical_offset - slice.logical_offset;
}

/** The slices [first, last) one worker covers in a phase. */
struct slice_range {
    std::size_t first = 0;
    std::size_t last = 0;
};

/**
 * Worker w covers slice w; in Half mode the active even worker also covers its idle partner's
 * slice w + 1 when that exists, processed after its own within each phase.
 */
slice_range covered_slices(std::size_t slice_count, active_set set, std::uint32_t worker) noexcept {
    const std::size_t first = worker;
    if (first >= slice_count) {
        return slice_range{slice_count, slice_count};
    }
    const std::size_t span = set == active_set::even ? 2 : 1;
    return slice_range{first, std::min(slice_count, first + span)};
}

/**
 * Maps slice-relative offsets to the chunk that holds them. Chunk sizes may be any 2 MiB
 * multiple (tests build such slices), so the lookup is a binary search on logical offsets, with
 * the last hit cached because consecutive RandomBlocks blocks often share a chunk.
 */
class chunk_locator {
public:
    explicit chunk_locator(const slice_view& slice) noexcept : slice_(slice) {}

    /** The chunk holding slice-relative offset rel. Precondition: rel < slice.bytes. */
    const chunk_view& find(std::uint64_t rel) noexcept {
        const std::uint64_t logical = slice_.logical_offset + rel;
        const chunk_view& cached = slice_.chunks[cached_];
        if (logical >= cached.logical_offset && logical - cached.logical_offset < cached.bytes) {
            return cached;
        }
        // The first chunk starts at the slice's own offset, so upper_bound never returns begin.
        const auto it = std::upper_bound(
            slice_.chunks.begin(), slice_.chunks.end(), logical,
            [](std::uint64_t value, const chunk_view& c) { return value < c.logical_offset; });
        cached_ = static_cast<std::size_t>(it - slice_.chunks.begin()) - 1;
        return slice_.chunks[cached_];
    }

private:
    const slice_view& slice_;
    std::size_t cached_ = 0;
};

/** Per-slice stream-time tables of the whole-slice phases, owned by the runner. */
using slice_time_tables = std::vector<std::vector<std::uint64_t>>;

/**
 * One worker's share of one pass phase: the primitive sequences of the four step kinds.
 *
 * Built on the worker's stack at the start of each phase task. Its destructor publishes the
 * worker's pending coverage, so every task ends with coverage.publish on every return path, a
 * stop and an exception included, and the step total is exact once the barrier is crossed.
 *
 * Every primitive carries the executing worker's index, also in Half mode when the range lies in
 * the partner's slice, and the error collector's sink of that worker.
 */
class phase_worker {
public:
    phase_worker(const step_runner_dependencies& deps, const pass_params& params,
                 std::uint32_t worker) noexcept
        : deps_(deps), params_(params), worker_(worker),
          covered_(covered_slices(deps.slices.size(), params.set, worker)) {}

    ~phase_worker() { deps_.coverage.publish(worker_); }

    phase_worker(const phase_worker&) = delete;
    phase_worker& operator=(const phase_worker&) = delete;
    phase_worker(phase_worker&&) = delete;
    phase_worker& operator=(phase_worker&&) = delete;

    /** Windowed pass: per window, stream in the step's order, fence, flush, fence, verify. */
    void windowed();

    /** Turnaround pass: the turnaround_schedule of every window. */
    void turnaround();

    /** Hold pass, phase A: stream each slice in RandomBlocks order, then the pre-hold flush. */
    void hold_write(slice_time_tables& tables);

    /** Hold pass, phase B: fence, flush, fence each slice, then verify it in the same order. */
    void hold_verify(const slice_time_tables& tables);

    /** Moving-inversion pass, phase 1: the ascending base write, then fence, flush, fence. */
    void inversion_base_write(slice_time_tables& tables);

    /**
     * Moving-inversion pass, phase 2 (ascending: verify base, write complement) or phase 3
     * (descending: verify complement, write base), each followed by fence, flush, fence.
     */
    void inversion_sweep(slice_time_tables& tables, bool ascending);

private:
    access_context context(phase_kind phase, std::uint32_t access_type,
                           const chunk_view& chunk) const noexcept {
        return access_context{
            params_.step_index, params_.pass, phase, access_type, worker_, logical_base(chunk), 0};
    }

    bool stop_requested() const noexcept { return deps_.stop.stop_requested(); }

    /** The RandomBlocks cursor of a hold step's slice; the verify phase rebuilds the same one. */
    order_cursor slice_cursor(const slice_view& slice) const {
        // Hold steps always use RandomBlocks over the whole slice, whatever the step's order.
        // The salt is the slice's logical offset, so every slice gets its own permutation.
        return order_cursor(order_params{RQ_ORDER_RANDOM_BLOCKS, slice.bytes, params_.order_seed,
                                         params_.step_index, params_.pass, slice.logical_offset});
    }

    bool fence_flush_fence(const slice_view& slice, phase_kind phase, std::uint32_t access_type);
    bool windowed_window(const chunk_view& chunk, std::uint64_t window_offset,
                         std::uint64_t window_bytes);
    bool turnaround_window(const chunk_view& chunk, std::uint64_t window_offset,
                           std::uint64_t window_bytes);
    bool hold_stream_slice(const slice_view& slice, std::vector<std::uint64_t>& stream_times);
    bool hold_verify_slice(const slice_view& slice, const std::vector<std::uint64_t>& stream_times);

    const step_runner_dependencies& deps_;
    const pass_params params_;
    const std::uint32_t worker_;
    const slice_range covered_;
};

bool phase_worker::fence_flush_fence(const slice_view& slice, phase_kind phase,
                                     std::uint32_t access_type) {
    if (slice.chunks.empty()) {
        return true;
    }
    const access_context fence_ctx = context(phase, access_type, slice.chunks.front());
    // First fence: every earlier streaming store and load of the slice is globally visible and
    // complete before any flush, so the flush writes back the new data rather than racing it.
    deps_.memory.fence(fence_ctx);
    for (const chunk_view& chunk : slice.chunks) {
        // One flush per chunk keeps the stop latency within one chunk's flush; whatever this
        // phase would still protect is verified only after it, so a stop can end it here.
        if (stop_requested()) {
            return false;
        }
        // Writes back and invalidates every line at every cache level, whatever left it cached:
        // this phase's own accesses, an earlier pass or a hardware prefetch.
        deps_.memory.flush(context(phase, access_type, chunk), chunk.base, words_of(chunk.bytes));
    }
    // Second fence: the weakly ordered flushes complete before any later load, so the next
    // verification of these lines is served from beyond the CPU caches.
    deps_.memory.fence(fence_ctx);
    return true;
}

/* ---------------------------------------------------------------------------------------------
 * Windowed steps (RQ_ACCESS_VERIFY_AFTER_WRITE)
 * ------------------------------------------------------------------------------------------- */

void phase_worker::windowed() {
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        for (const chunk_view& chunk : deps_.slices[s].chunks) {
            // Windows are cut inside one chunk, a separate allocation, so no range spans two.
            for (std::uint64_t offset = 0; offset < chunk.bytes; offset += k_window_bytes) {
                if (!windowed_window(chunk, offset,
                                     std::min(k_window_bytes, chunk.bytes - offset))) {
                    return;
                }
            }
        }
    }
}

bool phase_worker::windowed_window(const chunk_view& chunk, std::uint64_t window_offset,
                                   std::uint64_t window_bytes) {
    // The salt is the window's logical offset, unique in the region, so every window of every
    // pass gets its own RandomBlocks permutation; BankStride takes its stride from the pass.
    order_cursor cursor(order_params{params_.order, window_bytes, params_.order_seed,
                                     params_.step_index, params_.pass,
                                     chunk.logical_offset + window_offset});
    std::array<std::uint64_t, k_groups_per_window> stream_times{};
    access_context ctx = context(phase_kind::write, 0, chunk);
    std::size_t group = 0;
    std::uint64_t group_bytes = 0;
    order_range range{};
    while (cursor.next(range)) {
        if (group_bytes == 0 && stop_requested()) {
            // Nothing of this window has been verified yet, so none of it counts as coverage.
            return false;
        }
        const std::uint64_t offset = window_offset + range.offset;
        deps_.memory.stream_fill(ctx, address_in(chunk, offset), words_of(range.length),
                                 params_.base, logical_word(chunk, offset), range.direction);
        group_bytes += range.length;
        if (group_bytes == k_work_group_bytes) {
            stream_times[group++] = deps_.clock.now_ns();
            group_bytes = 0;
        }
    }

    ctx.phase = phase_kind::verify;
    ctx.access_type = params_.access_type;
    // Fence, flush, fence over the window: the first fence makes the streaming stores globally
    // visible before the flush; the flush evicts any copy left cached by the previous pass's
    // verification or a prefetch; the second fence orders the flush before the verification
    // loads, so none of them is served by a copy older than this pass's write.
    deps_.memory.fence(ctx);
    deps_.memory.flush(ctx, address_in(chunk, window_offset), words_of(window_bytes));
    deps_.memory.fence(ctx);

    // The identical sequence and grouping as the write, so the k-th verified group carries the
    // k-th streamed group's stamp and time_since_write_us is exact to the group.
    cursor.reset();
    worker_error_sink& sink = deps_.errors.sink(worker_);
    group = 0;
    group_bytes = 0;
    while (cursor.next(range)) {
        if (group_bytes == 0) {
            if (stop_requested()) {
                return false;
            }
            ctx.write_time_ns = stream_times[group];
            sink.bind(ctx);
        }
        const std::uint64_t offset = window_offset + range.offset;
        const std::size_t words = words_of(range.length);
        deps_.memory.verify(ctx, address_in(chunk, offset), words, params_.base,
                            logical_word(chunk, offset), range.direction, sink);
        deps_.coverage.add(worker_, static_cast<std::uint64_t>(words) * k_word_bytes);
        group_bytes += range.length;
        if (group_bytes == k_work_group_bytes) {
            ++group;
            group_bytes = 0;
        }
    }
    return true;
}

/* ---------------------------------------------------------------------------------------------
 * Turnaround steps (RQ_ACCESS_TURNAROUND_READ)
 * ------------------------------------------------------------------------------------------- */

void phase_worker::turnaround() {
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        for (const chunk_view& chunk : deps_.slices[s].chunks) {
            for (std::uint64_t offset = 0; offset < chunk.bytes; offset += k_window_bytes) {
                if (!turnaround_window(chunk, offset,
                                       std::min(k_window_bytes, chunk.bytes - offset))) {
                    return;
                }
            }
        }
    }
}

bool phase_worker::turnaround_window(const chunk_view& chunk, std::uint64_t window_offset,
                                     std::uint64_t window_bytes) {
    // A fresh schedule per window and pass, so every window begins with its start-up stage.
    turnaround_schedule schedule(window_bytes);
    const access_context ctx = context(phase_kind::turnaround, params_.access_type, chunk);
    std::array<std::uint64_t, k_turnaround_ring> stream_times{};
    worker_error_sink& sink = deps_.errors.sink(worker_);
    std::uint64_t streamed = 0;
    turnaround_action action{};
    while (schedule.next(action)) {
        const std::uint64_t offset = window_offset + action.offset;
        switch (action.op) {
        case turnaround_op::stream:
            if (streamed % k_blocks_per_group == 0 && stop_requested()) {
                // Blocks streamed but not yet verified are simply never counted.
                return false;
            }
            deps_.memory.stream_fill(ctx, address_in(chunk, offset), words_of(action.length),
                                     params_.base, logical_word(chunk, offset), 1);
            stream_times[action.block % k_turnaround_ring] = deps_.clock.now_ns();
            ++streamed;
            break;
        case turnaround_op::fence:
            // Orders the block streams and flushes before it ahead of the verification loads
            // after it. Each block meets its own fenced stream, then flush, fence, verify.
            deps_.memory.fence(ctx);
            break;
        case turnaround_op::flush:
            // The block's stores were fenced in the iteration that streamed it, so this writes
            // back the new data and evicts any copy, one a previous pass's verification left
            // included.
            deps_.memory.flush(ctx, address_in(chunk, offset), words_of(action.length));
            break;
        case turnaround_op::verify: {
            access_context verify_ctx = ctx;
            verify_ctx.write_time_ns = stream_times[action.block % k_turnaround_ring];
            sink.bind(verify_ctx);
            const std::size_t words = words_of(action.length);
            deps_.memory.verify(verify_ctx, address_in(chunk, offset), words, params_.base,
                                logical_word(chunk, offset), 1, sink);
            deps_.coverage.add(worker_, static_cast<std::uint64_t>(words) * k_word_bytes);
            break;
        }
        }
    }
    return true;
}

/* ---------------------------------------------------------------------------------------------
 * Hold steps (RQ_ACCESS_VERIFY_AFTER_HOLD)
 * ------------------------------------------------------------------------------------------- */

void phase_worker::hold_write(slice_time_tables& tables) {
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        const slice_view& slice = deps_.slices[s];
        if (!hold_stream_slice(slice, tables[s])) {
            return;
        }
        // Pre-hold flush: without it a dirty cached copy could hold the new data back from DRAM
        // for the whole hold, and the verification would measure the cache, not retention.
        if (!fence_flush_fence(slice, phase_kind::pre_hold_flush, 0)) {
            return;
        }
    }
}

bool phase_worker::hold_stream_slice(const slice_view& slice,
                                     std::vector<std::uint64_t>& stream_times) {
    if (slice.bytes == 0) {
        return true;
    }
    order_cursor cursor = slice_cursor(slice);
    chunk_locator locator(slice);
    std::size_t group = 0;
    std::uint64_t group_bytes = 0;
    order_range range{};
    while (cursor.next(range)) {
        if (group_bytes == 0 && stop_requested()) {
            return false;
        }
        const chunk_view& chunk = locator.find(range.offset);
        const std::uint64_t offset = slice.logical_offset + range.offset - chunk.logical_offset;
        deps_.memory.stream_fill(context(phase_kind::write, 0, chunk), address_in(chunk, offset),
                                 words_of(range.length), params_.base, logical_word(chunk, offset),
                                 range.direction);
        group_bytes += range.length;
        if (group_bytes == k_work_group_bytes) {
            stream_times[group++] = deps_.clock.now_ns();
            group_bytes = 0;
        }
    }
    return true;
}

void phase_worker::hold_verify(const slice_time_tables& tables) {
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        const slice_view& slice = deps_.slices[s];
        // The second flush follows the hold, so any copy filled before the hold ended, by a
        // prefetch or speculation, is removed before the verification loads.
        if (!fence_flush_fence(slice, phase_kind::verify, params_.access_type)) {
            return;
        }
        if (!hold_verify_slice(slice, tables[s])) {
            return;
        }
    }
}

bool phase_worker::hold_verify_slice(const slice_view& slice,
                                     const std::vector<std::uint64_t>& stream_times) {
    if (slice.bytes == 0) {
        return true;
    }
    // The same permutation and grouping as the write, so each verified group carries the stamp
    // of the group that wrote it, taken before the hold.
    order_cursor cursor = slice_cursor(slice);
    chunk_locator locator(slice);
    worker_error_sink& sink = deps_.errors.sink(worker_);
    access_context ctx{};
    const chunk_view* bound_chunk = nullptr;
    std::size_t group = 0;
    std::uint64_t group_bytes = 0;
    order_range range{};
    while (cursor.next(range)) {
        const bool group_start = group_bytes == 0;
        if (group_start && stop_requested()) {
            return false;
        }
        const chunk_view& chunk = locator.find(range.offset);
        if (group_start || &chunk != bound_chunk) {
            // The context changes with the group's stamp and with the chunk's region_base.
            ctx = context(phase_kind::verify, params_.access_type, chunk);
            ctx.write_time_ns = stream_times[group];
            sink.bind(ctx);
            bound_chunk = &chunk;
        }
        const std::uint64_t offset = slice.logical_offset + range.offset - chunk.logical_offset;
        const std::size_t words = words_of(range.length);
        deps_.memory.verify(ctx, address_in(chunk, offset), words, params_.base,
                            logical_word(chunk, offset), range.direction, sink);
        deps_.coverage.add(worker_, static_cast<std::uint64_t>(words) * k_word_bytes);
        group_bytes += range.length;
        if (group_bytes == k_work_group_bytes) {
            ++group;
            group_bytes = 0;
        }
    }
    return true;
}

/* ---------------------------------------------------------------------------------------------
 * Moving-inversion steps (RQ_ACCESS_READ_MODIFY_WRITE)
 * ------------------------------------------------------------------------------------------- */

void phase_worker::inversion_base_write(slice_time_tables& tables) {
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        const slice_view& slice = deps_.slices[s];
        std::vector<std::uint64_t>& stream_times = tables[s];
        for (const chunk_view& chunk : slice.chunks) {
            const access_context ctx = context(phase_kind::write, 0, chunk);
            const std::uint64_t first_group = offset_in_slice(slice, chunk) / k_work_group_bytes;
            for (std::uint64_t offset = 0; offset < chunk.bytes; offset += k_work_group_bytes) {
                if (stop_requested()) {
                    return;
                }
                deps_.memory.stream_fill(ctx, address_in(chunk, offset),
                                         words_of(k_work_group_bytes), params_.base,
                                         logical_word(chunk, offset), 1);
                stream_times[first_group + offset / k_work_group_bytes] = deps_.clock.now_ns();
            }
        }
        // The ascending sweep verifies at once, so the base write ends with the full fence,
        // flush and fence over the slice rather than a fence alone.
        if (!fence_flush_fence(slice, phase_kind::write, 0)) {
            return;
        }
    }
}

void phase_worker::inversion_sweep(slice_time_tables& tables, bool ascending) {
    // Ascending: verify the base and stream its complement. Descending: verify the complement and
    // stream the base back. Walking ones or zeros alternate with pattern_spec::pass.
    const pattern_spec& expected = ascending ? params_.base : params_.complement;
    const pattern_spec& next = ascending ? params_.complement : params_.base;
    const int direction = ascending ? 1 : -1;
    worker_error_sink& sink = deps_.errors.sink(worker_);
    for (std::size_t s = covered_.first; s < covered_.last; ++s) {
        const slice_view& slice = deps_.slices[s];
        std::vector<std::uint64_t>& stream_times = tables[s];
        const std::size_t chunk_count = slice.chunks.size();
        for (std::size_t i = 0; i < chunk_count; ++i) {
            const chunk_view& chunk = slice.chunks[ascending ? i : chunk_count - 1 - i];
            access_context ctx = context(phase_kind::sweep, params_.access_type, chunk);
            const std::uint64_t first_group = offset_in_slice(slice, chunk) / k_work_group_bytes;
            const std::uint64_t groups = chunk.bytes / k_work_group_bytes;
            for (std::uint64_t j = 0; j < groups; ++j) {
                if (stop_requested()) {
                    return;
                }
                const std::uint64_t g = ascending ? j : groups - 1 - j;
                const std::uint64_t offset = g * k_work_group_bytes;
                std::uint64_t& stamp = stream_times[first_group + g];
                // The block was last streamed by the base write or the previous sweep, which
                // stamped it; this sweep restamps it for the next one, in either direction.
                ctx.write_time_ns = stamp;
                sink.bind(ctx);
                // The pointer and first word name the block's low end in both directions; the
                // direction changes only the traversal order.
                deps_.memory.verify_and_stream(ctx, address_in(chunk, offset),
                                               words_of(k_work_group_bytes), expected, next,
                                               logical_word(chunk, offset), direction, sink);
                deps_.coverage.add(worker_, k_work_group_bytes);
                stamp = deps_.clock.now_ns();
            }
        }
        // Keeps the next sweep's loads off copies this sweep's own loads left in the caches.
        if (!fence_flush_fence(slice, phase_kind::sweep, params_.access_type)) {
            return;
        }
    }
}

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * step_runner
 * ------------------------------------------------------------------------------------------- */

step_runner::step_runner(const step_runner_dependencies& dependencies) : deps_(dependencies) {
    slice_write_times_.reserve(deps_.slices.size());
    for (const slice_view& slice : deps_.slices) {
        const std::uint64_t groups =
            slice.bytes / k_work_group_bytes + (slice.bytes % k_work_group_bytes != 0 ? 1 : 0);
        slice_write_times_.emplace_back(static_cast<std::size_t>(groups), std::uint64_t{0});
    }
}

step_outcome step_runner::run_step(const rq_step_def& step, std::uint32_t step_index,
                                   step_observer& observer) {
    const step_kind kind = classify_step(step);
    const std::uint64_t budget_ns = std::uint64_t{step.time_budget_ms} * k_ns_per_ms;
    const std::uint64_t hold_ns = std::uint64_t{step.hold_ms} * k_ns_per_ms;
    const active_set set = step.thread_mode == RQ_THREADS_HALF ? active_set::even : active_set::all;

    deps_.errors.begin_step(step_index, deps_.pool.active_count(set));
    deps_.coverage.begin_step();

    pass_params params{};
    params.step_index = step_index;
    params.order = step.access_order;
    params.access_type = verify_access_type(kind);
    params.set = set;
    params.order_seed = deps_.seed;
    const std::uint64_t pattern_seed = step_pattern_seed(deps_.seed, step_index);

    // The schedule; coordinator-only state that no phase task refers to.
    std::uint32_t passes_done = 0;
    std::uint32_t passes_planned = 0;
    std::uint64_t total_pass_ns = 0;
    std::uint64_t mean_pass_ns = 0;
    bool stopped = false;
    const std::uint64_t step_start = deps_.clock.now_ns();

    const auto status_now = [&]() {
        step_status status{};
        status.step_index = step_index;
        status.pass_index = params.pass;
        status.passes_done = passes_done;
        status.passes_planned = passes_planned;
        status.mean_pass_ns = mean_pass_ns;
        status.step_elapsed_ns = elapsed_since(deps_.clock, step_start);
        return status;
    };
    // The barrier is waited in real time in short slices, so progress and error batches keep
    // flowing however long a phase lasts; only this thread ever ticks the observer.
    const auto wait_with_ticks = [&]() {
        while (!deps_.pool.wait_phase(k_observer_tick_ms)) {
            observer.tick(status_now());
        }
    };
    // Posts one phase, crosses its barrier and reports whether the pass may go on.
    const auto run_phase = [&](phase_task task) {
        deps_.pool.post_phase(std::move(task), set);
        wait_with_ticks();
        return !deps_.stop.stop_requested();
    };
    // The hold waits on the stop signal, never in a sleep, so a stop ends a 120 s hold at once.
    // It is measured against its own start on the session clock, so slice rounding never
    // stretches it, and it is cut into ticks so progress keeps flowing.
    const auto wait_hold = [&]() {
        const std::uint64_t hold_start = deps_.clock.now_ns();
        for (;;) {
            const std::uint64_t held = elapsed_since(deps_.clock, hold_start);
            if (held >= hold_ns) {
                return true;
            }
            const std::uint64_t left_ns = hold_ns - held;
            const std::uint64_t left_ms =
                left_ns / k_ns_per_ms + (left_ns % k_ns_per_ms != 0 ? 1 : 0);
            if (deps_.clock.wait_for_stop(deps_.stop,
                                          std::min<std::uint64_t>(k_observer_tick_ms, left_ms))) {
                return false;
            }
            observer.tick(status_now());
        }
    };

    for (;;) {
        if (deps_.stop.stop_requested()) {
            stopped = true;
            break;
        }
        params.pass = passes_done;
        params.base = pattern_spec{step.pattern, pattern_seed, params.pass, false};
        params.complement = pattern_spec{step.pattern, pattern_seed, params.pass, true};
        // Read before the pass's first primitive, so the whole pass counts toward its duration.
        const std::uint64_t pass_start = deps_.clock.now_ns();

        // Tasks capture the runner and a copy of the pass parameters, never this frame: an
        // exception can leave run_step while a phase is still outstanding.
        switch (kind) {
        case step_kind::windowed:
            run_phase([this, params](std::uint32_t worker) {
                phase_worker task(deps_, params, worker);
                task.windowed();
            });
            break;
        case step_kind::turnaround:
            run_phase([this, params](std::uint32_t worker) {
                phase_worker task(deps_, params, worker);
                task.turnaround();
            });
            break;
        case step_kind::hold:
            if (run_phase([this, params](std::uint32_t worker) {
                    phase_worker task(deps_, params, worker);
                    task.hold_write(slice_write_times_);
                }) &&
                wait_hold()) {
                run_phase([this, params](std::uint32_t worker) {
                    phase_worker task(deps_, params, worker);
                    task.hold_verify(slice_write_times_);
                });
            }
            break;
        case step_kind::moving_inversion:
            if (run_phase([this, params](std::uint32_t worker) {
                    phase_worker task(deps_, params, worker);
                    task.inversion_base_write(slice_write_times_);
                }) &&
                run_phase([this, params](std::uint32_t worker) {
                    phase_worker task(deps_, params, worker);
                    task.inversion_sweep(slice_write_times_, true);
                })) {
                run_phase([this, params](std::uint32_t worker) {
                    phase_worker task(deps_, params, worker);
                    task.inversion_sweep(slice_write_times_, false);
                });
            }
            break;
        }

        if (deps_.stop.stop_requested()) {
            // A pass cut short is not counted, but what it verified is already in the step total.
            stopped = true;
            break;
        }
        ++passes_done;
        total_pass_ns += elapsed_since(deps_.clock, pass_start);
        mean_pass_ns = total_pass_ns / passes_done;
        // Time governs: the coverage budget never extends a step, so fail-fast ordering is never
        // traded for coverage. A first pass longer than the budget yields exactly one pass.
        passes_planned = planned_passes(budget_ns, mean_pass_ns);
        observer.tick(status_now());
        if (passes_done >= passes_planned) {
            break;
        }
    }

    step_outcome outcome{};
    outcome.completed = !stopped && passes_done >= passes_planned;
    outcome.passes = passes_done;
    outcome.actual_ns = elapsed_since(deps_.clock, step_start);
    outcome.overrun_ns = outcome.actual_ns > budget_ns ? outcome.actual_ns - budget_ns : 0;
    // Exact: every task published its pending bytes before reaching the last barrier crossed.
    outcome.verified_bytes = deps_.coverage.step_total();
    return outcome;
}

}  // namespace rq::engine
