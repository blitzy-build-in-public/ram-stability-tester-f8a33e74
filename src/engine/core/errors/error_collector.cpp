/**
 * @file error_collector.cpp
 * @brief Mismatch confirmation and accounting: the flushed re-read and its classification, the
 *        stored-detail cap, on_errors batching, exact true totals and Stop on error.
 *
 * Compiled at the x64 SSE2 baseline; nothing here needs a vector instruction.
 *
 * The mismatch path (worker_error_sink::on_mismatch, then error_collector::record) runs on every
 * worker at once, inside a noexcept verification kernel. It therefore takes no lock, allocates
 * nothing, raises no callback and never waits: the constructor preallocates every slot, the
 * counters are lock-free atomics, and each detection ordinal owns exactly one record slot. Text
 * for the Stop on error detail is built in a stack buffer by the helpers below rather than with
 * std::string, std::format or snprintf, which allocate or take CRT locale locks.
 *
 * Exceptions: only the constructor throws (std::invalid_argument, std::bad_alloc). The session's
 * coordinator converts either into a stop with RQ_END_INTERNAL_FAULT.
 */
#include "core/errors/error_collector.h"
#include "core/clock.h"

#include <atomic>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <stdexcept>

namespace rq::engine {

namespace {

constexpr std::uint64_t k_ns_per_us = 1'000;
constexpr std::uint64_t k_ns_per_ms = 1'000'000;

/**
 * Appends NUL-terminated text at cursor without writing at or past end, and leaves the buffer
 * NUL-terminated. Text that does not fit is cut at a byte; every string this file formats is
 * ASCII, and stop_signal::request truncates its copy at a UTF-8 boundary anyway.
 *
 * @param cursor Position of the current terminator; must lie below end.
 * @param end One past the last byte of the buffer.
 * @param text NUL-terminated text to append.
 * @return The position of the new terminator, which the next append overwrites; below end.
 */
char* append_text(char* cursor, char* const end, const char* text) noexcept {
    while (*text != '\0' && end - cursor > 1) {
        *cursor++ = *text++;
    }
    *cursor = '\0';
    return cursor;
}

/** Appends value in decimal, as append_text does. */
char* append_decimal(char* cursor, char* const end, std::uint64_t value) noexcept {
    // 20 digits hold UINT64_MAX; the extra byte is the terminator append_text reads up to.
    char digits[21];
    char* first = digits + sizeof(digits) - 1;
    *first = '\0';
    do {
        *--first = static_cast<char>('0' + static_cast<int>(value % 10));
        value /= 10;
    } while (value != 0);
    return append_text(cursor, end, first);
}

/**
 * Appends value as "0x" followed by exactly 16 uppercase hexadecimal digits, the form the JSON
 * summary and the UI use for addresses, so a stop detail can be matched against them verbatim.
 */
char* append_hex16(char* cursor, char* const end, std::uint64_t value) noexcept {
    constexpr char k_hex_digits[] = "0123456789ABCDEF";
    char text[19];
    text[0] = '0';
    text[1] = 'x';
    for (int i = 17; i >= 2; --i) {
        text[i] = k_hex_digits[value & 0xFu];
        value >>= 4;
    }
    text[18] = '\0';
    return append_text(cursor, end, text);
}

}  // namespace

std::uint32_t classify_reread(std::uint64_t expected, std::uint64_t actual,
                              std::uint64_t reread) noexcept {
    // The outcome is an inference about where the fault sits, never an established cell state,
    // and every outcome is a confirmed error: each one shows instability. On the mismatch path
    // expected != actual, so the first two branches cannot both apply.
    if (reread == expected) {
        return RQ_REREAD_MATCHES_EXPECTED;
    }
    if (reread == actual) {
        return RQ_REREAD_REPEATS_ACTUAL;
    }
    return RQ_REREAD_DIFFERS;
}

/* ---------------------------------------------------------------------------------------------
 * worker_error_sink
 * ------------------------------------------------------------------------------------------- */

void worker_error_sink::bind(const access_context& ctx) noexcept {
    context_ = ctx;
}

void worker_error_sink::on_mismatch(const std::uint64_t* address, std::uint64_t expected,
                                    std::uint64_t actual) noexcept {
    assert(owner_ != nullptr);
    owner_->record(*this, address, expected, actual);
}

/* ---------------------------------------------------------------------------------------------
 * error_collector: construction and coordinator-side setup
 * ------------------------------------------------------------------------------------------- */

error_collector::error_collector(const error_collector_options& options, stop_signal& stop,
                                 const IClock& clock, IMemoryAccess& memory)
    : stop_(stop), clock_(clock), memory_(memory) {
    // Validated before anything is allocated, so a rejected configuration costs nothing.
    if (options.placements.empty()) {
        throw std::invalid_argument("error_collector: at least one worker placement is required");
    }
    if (options.placements.size() > UINT32_MAX) {
        throw std::invalid_argument("error_collector: worker count exceeds the 32-bit range");
    }
    if (options.step_count == 0) {
        throw std::invalid_argument("error_collector: the plan must have at least one step");
    }

    detail_cap_ = options.detail_cap;
    step_count_ = options.step_count;
    worker_count_ = static_cast<std::uint32_t>(options.placements.size());
    stop_on_error_ = options.stop_on_error;

    // Everything the mismatch path touches is allocated here, once. make_unique<T[]>(n)
    // value-initialises: records are zeroed, and since C++20 a value-initialised std::atomic
    // holds 0, so every ready flag and per-step count starts at 0.
    if (detail_cap_ != 0) {
        records_ = std::make_unique<rq_error_record[]>(detail_cap_);
        ready_ = std::make_unique<std::atomic<std::uint32_t>[]>(detail_cap_);
    }
    step_errors_ = std::make_unique<std::atomic<std::uint64_t>[]>(step_count_);
    sinks_ = std::make_unique<worker_error_sink[]>(worker_count_);
    for (std::uint32_t i = 0; i < worker_count_; ++i) {
        worker_error_sink& s = sinks_[i];
        s.owner_ = this;
        s.worker_index_ = i;
        s.placement_ = options.placements[i];
    }
}

error_collector::~error_collector() = default;

void error_collector::begin_run(std::uint64_t t0_ns) noexcept {
    // Relaxed suffices: workers start traffic only after the coordinator's thread creation or
    // phase dispatch, which orders this store before every worker's load.
    t0_ns_.store(t0_ns, std::memory_order_relaxed);
}

void error_collector::begin_step([[maybe_unused]] std::uint32_t step_index,
                                 std::uint32_t active_workers) noexcept {
    assert(step_index < step_count_);
    // Written between steps while every worker is idle; the next phase dispatch publishes it.
    active_workers_.store(active_workers, std::memory_order_relaxed);
}

worker_error_sink& error_collector::sink(std::uint32_t worker_index) noexcept {
    assert(worker_index < worker_count_);
    return sinks_[worker_index];
}

std::uint32_t error_collector::worker_count() const noexcept {
    return worker_count_;
}

std::uint32_t error_collector::step_count() const noexcept {
    return step_count_;
}

std::uint32_t error_collector::detail_cap() const noexcept {
    return detail_cap_;
}

/* ---------------------------------------------------------------------------------------------
 * error_collector: the mismatch path (workers)
 * ------------------------------------------------------------------------------------------- */

void error_collector::record(worker_error_sink& sink, const std::uint64_t* address,
                             std::uint64_t expected, std::uint64_t actual) noexcept {
    // Detection time, read first: the verification load has just happened, and time since write
    // is measured to that load. A prefetch fill after the line's flush can precede the load, so
    // the age can overstate how old the data was when it was fetched by that interval.
    const std::uint64_t now = clock_.now_ns();

    // The verification's context with only the phase changed, so every layer's log and the
    // record keep the step, pass, access type, worker and write time that found the mismatch.
    access_context ctx = sink.context_;
    ctx.phase = phase_kind::reread;

    // Exactly one re-read, through the session's outermost layer, so the counting layer logs it
    // and the fault-injecting layer passes it through to memory that already holds its
    // corruption (an injected flip then classifies as RQ_REREAD_REPEATS_ACTUAL). The real layer
    // runs fence, flush of the word's line, fence, then one load, so the value cannot come from
    // a cached copy that predates the flush. No lock is held here: this call re-enters the layer
    // whose verify is running on this thread.
    const std::uint64_t reread = memory_.reread_word(ctx, address);
    const std::uint32_t outcome = classify_reread(expected, actual, reread);

    // The true total, never capped. Its fetch_add also hands out the detection ordinal, which is
    // unique, so slot n below belongs to this worker alone and needs no second reservation.
    const std::uint64_t n = total_.fetch_add(1, std::memory_order_relaxed);
    assert(ctx.step_index < step_count_);
    if (ctx.step_index < step_count_) {
        step_errors_[ctx.step_index].fetch_add(1, std::memory_order_relaxed);
    }

    const std::uint64_t t0 = t0_ns_.load(std::memory_order_relaxed);
    const std::uint64_t time_into_test_ms = now >= t0 ? (now - t0) / k_ns_per_ms : 0;
    // Workers race, so ordinal 0 need not be the earliest error; keep the minimum time instead.
    std::uint64_t earliest = first_error_ms_.load(std::memory_order_relaxed);
    while (time_into_test_ms < earliest &&
           !first_error_ms_.compare_exchange_weak(earliest, time_into_test_ms,
                                                  std::memory_order_relaxed)) {
        // A failed exchange reloaded earliest; retry only while this error is still earlier.
    }

    if (n < detail_cap_) {
        const std::uint64_t word_address =
            static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(address));
        rq_error_record& r = records_[n];
        r.sequence = n;
        r.address = word_address;
        // Modulo 2^64 by design: region_base is the chunk's logical base, which may lie below
        // zero, so the difference is the word's logical offset in the tested region.
        r.region_offset = word_address - static_cast<std::uint64_t>(ctx.region_base);
        r.expected = expected;
        r.actual = actual;
        r.reread = reread;
        r.bit_mask = expected ^ actual;
        // A substitute clock can trail a write stamp; report 0 rather than wrap.
        r.time_since_write_us =
            now >= ctx.write_time_ns ? (now - ctx.write_time_ns) / k_ns_per_us : 0;
        r.time_into_test_ms = time_into_test_ms;
        r.step_index = ctx.step_index;
        r.pass_index = ctx.pass_index;
        r.access_type = ctx.access_type;
        r.reread_outcome = outcome;
        // No documented user-mode API yields physical addresses, so v1 reports virtual ones.
        r.address_kind = RQ_ADDRESS_KIND_VIRTUAL;
        // The executing worker, which in Half mode is the even worker even while it covers its
        // partner's slice: the load-contrast analysis needs who was loading memory, not whose
        // slice the word lies in.
        r.worker_index = sink.worker_index_;
        r.logical_processor = sink.placement_.number;
        r.processor_group = sink.placement_.group;
        r.active_workers = active_workers_.load(std::memory_order_relaxed);
        r.reserved0 = 0;
        // Publishes the slot: drain()'s acquire load of this flag makes every field above
        // visible to the coordinator.
        ready_[n].store(1, std::memory_order_release);
    }

    // Stop on error: the first confirming worker requests the stop itself, without waiting for
    // any callback. request() only claims the reason, sets an atomic flag and signals a Win32
    // event; it never blocks. stop_signal is first-writer-wins, so a user, thermal or host-fault
    // stop recorded earlier keeps its reason. Errors confirmed later in the current 1 MiB block,
    // before the workers next poll the flag, are still counted and stored above.
    if (stop_on_error_ && !stop_issued_.exchange(true, std::memory_order_acq_rel)) {
        char detail[RQ_END_DETAIL_SIZE];
        char* const end = detail + sizeof(detail);
        char* cursor = append_text(detail, end, "First error: step ");
        cursor = append_decimal(cursor, end, std::uint64_t{ctx.step_index} + 1);
        cursor = append_text(cursor, end, " of ");
        cursor = append_decimal(cursor, end, step_count_);
        cursor = append_text(cursor, end, ", pass ");
        cursor = append_decimal(cursor, end, ctx.pass_index);
        cursor = append_text(cursor, end, ", worker ");
        cursor = append_decimal(cursor, end, sink.worker_index_);
        cursor = append_text(cursor, end, ", virtual address ");
        append_hex16(cursor, end,
                     static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(address)));
        stop_.request(RQ_END_FIRST_ERROR, 0, detail);
    }
}

/* ---------------------------------------------------------------------------------------------
 * error_collector: batching and totals (coordinator and observers)
 * ------------------------------------------------------------------------------------------- */

error_batch error_collector::drain() noexcept {
    const std::uint32_t start = delivered_;
    std::uint32_t end = start;
    // Contiguous ready slots only: a slot reserved but not yet filled ends the batch, and it and
    // everything after it come with a later drain. Once every slot has been delivered the loop
    // condition fails at once, and with a cap of 0 ready_ is never touched.
    while (end < detail_cap_ && ready_[end].load(std::memory_order_acquire) != 0) {
        ++end;
    }

    // Read after the acquire loads above, each of which synchronises with a release store
    // sequenced after that error's fetch_add, so this total already counts every delivered
    // record. The clamp is a defensive restatement of that guarantee.
    std::uint64_t total = total_.load(std::memory_order_acquire);
    if (total < end) {
        total = end;
    }

    error_batch batch;
    batch.count = end - start;
    // Slots are written once and never reused, so this pointer stays valid for the collector's
    // lifetime, longer than the on_errors call the C API promises it for.
    batch.records = batch.count != 0 ? &records_[start] : nullptr;
    batch.total_errors = total;
    batch.has_news = batch.count != 0 || total != last_drained_total_;

    delivered_ = end;
    last_drained_total_ = total;
    return batch;
}

std::uint64_t error_collector::true_total() const noexcept {
    return total_.load(std::memory_order_relaxed);
}

std::uint64_t error_collector::step_errors(std::uint32_t step_index) const noexcept {
    if (step_index >= step_count_) {
        return 0;
    }
    return step_errors_[step_index].load(std::memory_order_relaxed);
}

std::uint64_t error_collector::first_error_ms() const noexcept {
    return first_error_ms_.load(std::memory_order_relaxed);
}

std::uint32_t error_collector::stored_count() const noexcept {
    const std::uint64_t total = true_total();
    return total < detail_cap_ ? static_cast<std::uint32_t>(total) : detail_cap_;
}

bool error_collector::truncated() const noexcept {
    return true_total() > detail_cap_;
}

}  // namespace rq::engine
