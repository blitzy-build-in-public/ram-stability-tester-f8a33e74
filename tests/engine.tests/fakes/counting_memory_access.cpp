/**
 * @file counting_memory_access.cpp
 * @brief The counting and recording IMemoryAccess layer: an independent verified-byte count and
 *        an ordered per-worker log of every primitive the step runner issues.
 *
 * Test-only, like its header: compiled into engine.tests.exe and ramquick_engine_test.dll, never
 * into ramquick_engine.dll, and free of GoogleTest so that both test binaries can link it.
 *
 * @par No traffic of its own
 * The layer issues no load, store, flush or fence to the memory under test and changes nothing
 * it forwards: every primitive reaches the inner layer with the caller's ctx, range, pattern
 * specs, direction and sink, and the inner result is returned unchanged. What it counts and logs
 * is therefore an independent observation of the step runner's traffic rather than a copy of
 * the engine's coverage_counter, and the stress bound is unaffected by the layer.
 *
 * @par What the log is for
 * - Flush-before-verification: test_step_runner.cpp walks log_for_worker(w). For every line a
 *   verify or verify_and_stream record covers, or a verify_strided record visits, it requires,
 *   since that line's previous store-type or load-type record, a fence, a flush whose range
 *   covers the line, and a fence, in that order. A reread_word record satisfies the rule on its
 *   own, because its kernel fences, flushes and fences the word's line itself. Hold steps must
 *   also show the phase_kind::pre_hold_flush phase between each pass's write and its
 *   verification.
 * - Turnaround timing: test_access_orders.cpp compares each turnaround verify's
 *   ctx.write_time_ns with the clock_ns and clock_after_ns that bracket the stream_fill of the
 *   same block, so the check holds whether the runner stamps the time just before or just after
 *   streaming the block.
 * Both checks rely on each worker's log being in true issue order, including a re-read issued
 * from inside the verification that found the mismatch; impl::track explains how.
 */
#include "fakes/counting_memory_access.h"
#include "core/session.h"
#include "core/clock.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <new>
#include <type_traits>
#include <utility>
#include <vector>

namespace rq::engine::test {

static_assert(k_counting_step_slots >= k_max_step_count,
              "every step index a valid session can use needs its own per-step counter");

namespace {

// Records a worker's slot reserves on its first append: at 120 bytes a record, about 7.5 MiB per
// logging worker. Enough that a typical scaled run never reallocates mid-pass, while a slot that
// never logs reserves nothing, so enabling the log for 1024 slots stays cheap.
constexpr std::size_t k_initial_records_per_worker = 65'536;

constexpr std::uint64_t range_bytes(std::size_t word_count) noexcept {
    return static_cast<std::uint64_t>(word_count) * sizeof(std::uint64_t);
}

std::uintptr_t address_of(const std::uint64_t* word) noexcept {
    return reinterpret_cast<std::uintptr_t>(word);
}

// Coverage counts exactly the bytes a verification primitive compares; re-reads, fills, flushes
// and fences never count, as the engine's coverage_counter defines.
constexpr bool counts_as_verified(primitive_kind kind) noexcept {
    return kind == primitive_kind::verify || kind == primitive_kind::verify_and_stream ||
           kind == primitive_kind::verify_strided;
}

// A strided call touches visit_count whole lines of its span, so its record's length is theirs.
constexpr std::uint64_t visited_bytes(const strided_visits& visits) noexcept {
    return visits.visit_count * k_cache_line_bytes;
}

// A layer with nothing to forward to would silently drop the step runner's traffic and count
// nothing, voiding every coverage and flush-rule assertion built on it, so a null inner layer
// ends the process at once. The message also goes to stderr, where a death test can match it.
std::unique_ptr<IMemoryAccess> require_inner(std::unique_ptr<IMemoryAccess> inner) noexcept {
    if (inner == nullptr) [[unlikely]] {
        constexpr const char* message =
            "counting_memory_access: the inner IMemoryAccess layer must not be null\n";
        OutputDebugStringA(message);
        std::fputs(message, stderr);
        std::fflush(stderr);
        std::abort();
    }
    return inner;
}

}  // namespace

#ifdef _MSC_VER
#pragma warning(push)
// C4324: the cache-line alignment of the counters and log slots is intentional; it keeps the hot
// counters, and each worker's slot, off one another's cache lines.
#pragma warning(disable : 4324)
#endif

struct counting_memory_access::impl {
    /** One worker's log: its records in issue order, guarded by the slot's own mutex. */
    struct alignas(64) log_slot {
        std::mutex mutex;
        std::vector<primitive_record> records;
        /** Most records this slot keeps; set by enable_log. */
        std::size_t cap = 0;
    };

    /** What enter() hands to leave(): the call's record and where it was logged, if anywhere. */
    struct entry {
        primitive_record record;
        /** The slot holding the record, or null when it was not logged. */
        log_slot* slot;
        /** Position of the record in slot->records; stable because the log is never trimmed. */
        std::size_t index;
    };

    explicit impl(const counting_options& opts) : options(opts) {}

    /**
     * Wraps one primitive: counts it, logs it, forwards it, adds verified bytes, notifies the
     * observer and completes the log record, in that order.
     *
     * The log record is reserved before forwarding and no lock is held across the forward. The
     * step runner binds the error collector's sink to the outermost layer, and the collector
     * calls reread_word on it from inside the inner verify, on the same thread. Holding the
     * slot's mutex across the forward would deadlock on that nested call, and appending only
     * after the forward returned would place the nested re-read before the verify that issued
     * it. Reserving first keeps each worker's log in true issue order, which merged_log's seq
     * order agrees with.
     *
     * @param forward Performs the inner call with the caller's exact arguments; its result,
     *        the mismatch count or the re-read value, is returned unchanged.
     * @param visits The strided kinds' visits, logged with the record; zero for other kinds.
     */
    template <typename Forward>
    auto track(primitive_kind kind, const access_context& ctx, std::uintptr_t begin,
               std::uint64_t bytes, Forward&& forward,
               const strided_visits& visits = strided_visits{}) noexcept {
        const entry call = enter(kind, ctx, begin, bytes, visits);
        if constexpr (std::is_void_v<std::invoke_result_t<Forward&>>) {
            forward();
            leave(call);
        } else {
            const auto result = forward();
            leave(call);
            return result;
        }
    }

    /** Counts the call, builds its record and reserves its log entry. */
    entry enter(primitive_kind kind, const access_context& ctx, std::uintptr_t begin,
                std::uint64_t bytes, const strided_visits& visits) noexcept {
        primitive_total.fetch_add(1, std::memory_order_relaxed);
        entry call{};
        call.record.seq = seq_source.fetch_add(1, std::memory_order_relaxed);
        call.record.kind = kind;
        call.record.worker = ctx.worker_index;
        call.record.begin = begin;
        call.record.bytes = bytes;
        call.record.visits = visits;
        call.record.ctx = ctx;
        call.record.clock_ns = read_clock();
        call.record.clock_after_ns = 0;
        call.slot = nullptr;
        call.index = 0;
        // Acquire pairs with enable_log's release, so the slots it created are visible here.
        if (log_enabled.load(std::memory_order_acquire)) {
            append(call);
        }
        return call;
    }

    /**
     * Appends call.record to its worker's slot and sets call.slot and call.index, or marks the
     * log overflowed when the worker has no slot, its slot is full or the allocation fails.
     */
    void append(entry& call) noexcept {
        if (call.record.worker >= slots.size()) {
            mark_overflowed();
            return;
        }
        log_slot& slot = *slots[call.record.worker];
        const std::lock_guard lock(slot.mutex);
        if (slot.records.size() >= slot.cap) {
            mark_overflowed();
            return;
        }
        try {
            if (slot.records.capacity() == 0) {
                slot.records.reserve(std::min(slot.cap, k_initial_records_per_worker));
            }
            slot.records.push_back(call.record);
        } catch (const std::bad_alloc&) {
            // The overrides are noexcept: a failed log allocation must degrade to an incomplete
            // log, which tests detect through log_overflowed(), never to std::terminate.
            mark_overflowed();
            return;
        }
        call.slot = &slot;
        call.index = slot.records.size() - 1;
    }

    /** Adds verified bytes, notifies the observer, then stamps clock_after_ns into the log. */
    void leave(const entry& call) noexcept {
        if (counts_as_verified(call.record.kind)) {
            // The whole range counts whatever the mismatches: coverage_counter also counts every
            // word compared, and the two totals must be comparable.
            verified_total.fetch_add(call.record.bytes, std::memory_order_relaxed);
            if (call.record.ctx.step_index < k_counting_step_slots) {
                step_totals[call.record.ctx.step_index].fetch_add(call.record.bytes,
                                                                  std::memory_order_relaxed);
            }
        }
        // Set before the run and read without synchronisation. The observer must not throw:
        // an exception escaping a noexcept override calls std::terminate, which is a test bug.
        if (observer) {
            observer(call.record);
        }
        if (call.slot != nullptr) {
            // Read after the observer, so a fake-clock advance it made is already visible.
            const std::uint64_t after = read_clock();
            const std::lock_guard lock(call.slot->mutex);
            call.slot->records[call.index].clock_after_ns = after;
        }
    }

    std::uint64_t read_clock() const noexcept {
        return options.clock != nullptr ? options.clock->now_ns() : 0;
    }

    // Once a slot is full, every later call of that worker would store the flag again; checking
    // first keeps that line shared instead of bouncing it between overflowing workers.
    void mark_overflowed() noexcept {
        if (!overflowed.load(std::memory_order_relaxed)) {
            overflowed.store(true, std::memory_order_release);
        }
    }

    const counting_options options;

    // Each hot counter sits on its own cache line: every worker updates them on every primitive.
    alignas(64) std::atomic<std::uint64_t> verified_total{0};
    alignas(64) std::atomic<std::uint64_t> primitive_total{0};
    alignas(64) std::atomic<std::uint64_t> seq_source{0};
    alignas(64) std::array<std::atomic<std::uint64_t>, k_counting_step_slots> step_totals{};

    // Read-mostly: log_enabled is set once before the run, overflowed at most once per run.
    alignas(64) std::atomic<bool> log_enabled{false};
    std::atomic<bool> overflowed{false};
    // Created by enable_log before log_enabled is set, and never resized afterwards.
    std::vector<std::unique_ptr<log_slot>> slots;
    std::function<void(const primitive_record&)> observer;
};

#ifdef _MSC_VER
#pragma warning(pop)
#endif

counting_memory_access::counting_memory_access(std::unique_ptr<IMemoryAccess> inner,
                                               counting_options options)
    : inner_(require_inner(std::move(inner))), impl_(std::make_unique<impl>(options)) {
    if (options.log_primitives) {
        enable_log(options.max_records_per_worker);
    }
}

counting_memory_access::~counting_memory_access() = default;

void counting_memory_access::stream_fill(const access_context& ctx, std::uint64_t* words,
                                         std::size_t word_count, const pattern_spec& spec,
                                         std::uint64_t first_word_index, int direction) noexcept {
    impl_->track(primitive_kind::stream_fill, ctx, address_of(words), range_bytes(word_count), [&] {
        inner_->stream_fill(ctx, words, word_count, spec, first_word_index, direction);
    });
}

void counting_memory_access::fence(const access_context& ctx) noexcept {
    impl_->track(primitive_kind::fence, ctx, 0, 0, [&] { inner_->fence(ctx); });
}

void counting_memory_access::flush(const access_context& ctx, const std::uint64_t* words,
                                   std::size_t word_count) noexcept {
    impl_->track(primitive_kind::flush, ctx, address_of(words), range_bytes(word_count),
                 [&] { inner_->flush(ctx, words, word_count); });
}

std::uint64_t counting_memory_access::verify(const access_context& ctx, const std::uint64_t* words,
                                             std::size_t word_count, const pattern_spec& expected,
                                             std::uint64_t first_word_index, int direction,
                                             mismatch_sink& sink) noexcept {
    return impl_->track(primitive_kind::verify, ctx, address_of(words), range_bytes(word_count),
                        [&] {
                            return inner_->verify(ctx, words, word_count, expected,
                                                  first_word_index, direction, sink);
                        });
}

std::uint64_t
counting_memory_access::verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                          std::size_t word_count, const pattern_spec& expected,
                                          const pattern_spec& next, std::uint64_t first_word_index,
                                          int direction, mismatch_sink& sink) noexcept {
    return impl_->track(primitive_kind::verify_and_stream, ctx, address_of(words),
                        range_bytes(word_count), [&] {
                            return inner_->verify_and_stream(ctx, words, word_count, expected, next,
                                                             first_word_index, direction, sink);
                        });
}

void counting_memory_access::stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                                                 std::size_t span_word_count,
                                                 const strided_visits& visits,
                                                 const pattern_spec& spec,
                                                 std::uint64_t span_first_word_index) noexcept {
    impl_->track(
        primitive_kind::stream_fill_strided, ctx, address_of(span), visited_bytes(visits),
        [&] {
            inner_->stream_fill_strided(ctx, span, span_word_count, visits, spec,
                                        span_first_word_index);
        },
        visits);
}

std::uint64_t counting_memory_access::verify_strided(
    const access_context& ctx, const std::uint64_t* span, std::size_t span_word_count,
    const strided_visits& visits, const pattern_spec& expected, std::uint64_t span_first_word_index,
    mismatch_sink& sink) noexcept {
    return impl_->track(
        primitive_kind::verify_strided, ctx, address_of(span), visited_bytes(visits),
        [&] {
            return inner_->verify_strided(ctx, span, span_word_count, visits, expected,
                                          span_first_word_index, sink);
        },
        visits);
}

std::uint64_t counting_memory_access::reread_word(const access_context& ctx,
                                                  const std::uint64_t* word) noexcept {
    return impl_->track(primitive_kind::reread_word, ctx, address_of(word), sizeof(std::uint64_t),
                        [&] { return inner_->reread_word(ctx, word); });
}

std::uint64_t counting_memory_access::verified_bytes() const noexcept {
    return impl_->verified_total.load(std::memory_order_relaxed);
}

std::uint64_t
counting_memory_access::verified_bytes_for_step(std::uint32_t step_index) const noexcept {
    return step_index < k_counting_step_slots
               ? impl_->step_totals[step_index].load(std::memory_order_relaxed)
               : 0;
}

std::uint64_t counting_memory_access::primitive_count() const noexcept {
    return impl_->primitive_total.load(std::memory_order_relaxed);
}

void counting_memory_access::reset_counts() noexcept {
    // seq is deliberately kept: the log survives a reset, and its records must stay ordered.
    impl_->verified_total.store(0, std::memory_order_relaxed);
    impl_->primitive_total.store(0, std::memory_order_relaxed);
    for (std::atomic<std::uint64_t>& total : impl_->step_totals) {
        total.store(0, std::memory_order_relaxed);
    }
}

void counting_memory_access::enable_log(std::size_t max_records_per_worker) {
    impl& state = *impl_;
    if (state.slots.empty()) {
        // Built aside and moved in, so a failed allocation leaves the layer as it was.
        std::vector<std::unique_ptr<impl::log_slot>> created;
        created.reserve(state.options.max_workers);
        for (std::uint32_t worker = 0; worker < state.options.max_workers; ++worker) {
            created.push_back(std::make_unique<impl::log_slot>());
        }
        state.slots = std::move(created);
    }
    for (const std::unique_ptr<impl::log_slot>& slot : state.slots) {
        const std::lock_guard lock(slot->mutex);
        slot->cap = max_records_per_worker;
    }
    // Release pairs with enter()'s acquire: a worker that sees the log enabled sees its slots.
    state.log_enabled.store(true, std::memory_order_release);
}

std::vector<primitive_record> counting_memory_access::log_for_worker(std::uint32_t worker) const {
    const impl& state = *impl_;
    if (!state.log_enabled.load(std::memory_order_acquire) || worker >= state.slots.size()) {
        return {};
    }
    impl::log_slot& slot = *state.slots[worker];
    const std::lock_guard lock(slot.mutex);
    return slot.records;
}

std::vector<primitive_record> counting_memory_access::merged_log() const {
    const impl& state = *impl_;
    std::vector<primitive_record> merged;
    if (!state.log_enabled.load(std::memory_order_acquire)) {
        return merged;
    }
    for (const std::unique_ptr<impl::log_slot>& slot : state.slots) {
        const std::lock_guard lock(slot->mutex);
        merged.insert(merged.end(), slot->records.begin(), slot->records.end());
    }
    // seq values are unique, so the order is total and the result deterministic.
    std::sort(merged.begin(), merged.end(),
              [](const primitive_record& a, const primitive_record& b) { return a.seq < b.seq; });
    return merged;
}

bool counting_memory_access::log_overflowed() const noexcept {
    return impl_->overflowed.load(std::memory_order_acquire);
}

void counting_memory_access::set_observer(std::function<void(const primitive_record&)> observer) {
    impl_->observer = std::move(observer);
}

IMemoryAccess* counting_memory_access::inner() noexcept {
    return inner_.get();
}

const IMemoryAccess* counting_memory_access::inner() const noexcept {
    return inner_.get();
}

}  // namespace rq::engine::test
