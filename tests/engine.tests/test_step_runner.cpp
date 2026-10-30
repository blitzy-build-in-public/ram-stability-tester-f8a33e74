/**
 * @file test_step_runner.cpp
 * @brief Step runner tests: pass scheduling against the time budget, overrun, the observer
 *        contract, the time-remaining formula, and the flush-before-verification rule asserted
 *        from the recorded primitive sequence of every phase kind.
 *
 * - StepRunnerHelpers, StepRunnerPlannedPasses and StepRunnerClassify: the pure helpers
 *   planned_passes, classify_step, verify_access_type and step_pattern_seed.
 * - StepRunnerStable and StepRunnerScheduling: step_runner::run_step over a real locked region
 *   with a fake_clock whose pass durations are scripted. Stable passes end within half a mean
 *   pass of the budget, +/-30 % jittered passes within one longest pass, an oversized pass (30 s
 *   against a 10 s budget) runs alone and reports its 20 s overrun, and the coverage budget never
 *   extends a step.
 * - StepRunnerObserver: no schedule is reported before the first pass completes, the schedule is
 *   planned_passes of the measured mean afterwards, elapsed time never decreases, and holds tick
 *   once per wait slice.
 * - RemainingTimeFormula and RemainingTimeStable: compute_remaining_ms as a pure function, and
 *   against the actual remaining time of a stable two-step run.
 * - StepRunnerFlushRule: at least three passes of every phase kind (windowed in four orders,
 *   hold, moving inversion, turnaround, and Half mode on one worker and on up to two, as many as
 *   the host has logical processors), each with two injected mismatches so the log carries
 *   re-reads. counting_memory_access records every primitive in issue order, and the checker
 *   requires, for every verified line on every pass, a fence, a flush covering the line and a
 *   fence after the line's previous store or load; hold steps show the same sequence before the
 *   hold and again after it, and the session-clock readings around the records place each pass's
 *   pre-hold closing fence no later than the start of the hold the runner actually waited, and
 *   its post-hold sequence no earlier than that hold's end and at least the hold after the
 *   fence. Each line is flushed exactly as often per pass as the rule requires, never more: no
 *   flush-and-reload over a small address set, the access style the product's stress bound
 *   excludes. No flush call covers more than one 1 MiB work group, the unit after which workers
 *   check the stop flag. A BankStride pass issues exactly one strided stream and one strided
 *   verification per 1 MiB, whose lines expand to order_cursor's BankStride sequence.
 * - StepRunnerFlushStop: a stop that lands during the first 1 MiB of every window or slice
 *   flush (windowed in two orders, pre-hold, post-hold, moving-inversion base write and sweep),
 *   or during a window's final write group, ends the phase within one work group of flushing,
 *   with its closing fence, no verification after the stop and no coverage for the cut window
 *   or slice.
 * - StepRunnerBankStrideStop: a stop during a BankStride window's verification ends it after
 *   the 1 MiB strided verification in flight, whose bytes alone are counted.
 * - StepRunnerFlushChecker and StepRunnerHoldBracketChecker: the checker reports a log with a
 *   dropped or duplicated fence or flush, and a hold log whose pre-hold flush is moved after
 *   the wait or whose post-hold flush is moved before it, in time only, with order and counts
 *   unchanged.
 *
 * Holds of 5 s finish instantly in fake time, while real worker threads still stream, flush and
 * verify real memory, so regions stay at 16 MiB or less. No test depends on an instruction set
 * beyond what select_isa reports, nor on elevation, so none is skipped.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <ostream>
#include <random>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/session.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/memory_region.h"
#include "core/memory/real_memory_access.h"
#include "core/patterns/access_order.h"
#include "core/runner/coverage_counter.h"
#include "core/runner/step_runner.h"
#include "core/runner/worker_pool.h"
#include "core/errors/error_collector.h"
#include "fakes/fake_clock.h"
#include "fakes/counting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_ns_per_ms = 1'000'000;
constexpr std::uint64_t k_ns_per_s = 1'000'000'000;

constexpr std::uint64_t k_session_seed = 0x0123456789ABCDEFull;

/** Steps the error collector is sized for; every step index used here is below it. */
constexpr std::uint32_t k_collector_steps = 8;

/** Detail cap the application passes. */
constexpr std::uint32_t k_detail_cap = 10'000;

/** Region of the scheduling tests: one minimal 2 MiB slice keeps 20-pass runs fast. */
constexpr std::uint64_t k_schedule_region_bytes = 2 * k_mib;

/**
 * Region of the flush-rule tests: 262,144 lines, one chunk and one window per slice. BankStride
 * included: its windows are issued as one strided stream and one strided verification per
 * 1 MiB, so its log is as short as the sequential orders'.
 */
constexpr std::uint64_t k_flush_region_bytes = 16 * k_mib;

/** Per-worker log cap of the flush-rule tests. */
constexpr std::size_t k_max_log_records = std::size_t{1} << 20;

/**
 * The turnaround block and the distance its verification lags its write: the turnaround
 * schedule's contract (four blocks of 4 KiB behind), restated here because the test checks the
 * runner against it rather than against the schedule's own implementation.
 */
constexpr std::uint64_t k_turnaround_block = 4096;
constexpr std::uint64_t k_turnaround_lag = 4;

/** Passes every flush-rule case runs: at least three, so later passes are checked too. */
constexpr std::uint32_t k_flush_passes = 3;

/** Scripted duration of every flush-rule pass, before any hold. */
constexpr std::uint64_t k_flush_pass_ns = k_ns_per_s;

/** Step index of the flush-rule runs: nonzero, so a dropped index would show. */
constexpr std::uint32_t k_flush_step_index = 3;

/** Passes whose first eligible fence receives an injected mismatch: the first and a later one. */
constexpr std::array<std::uint32_t, 2> k_injection_passes = {0, 2};

/** The bit the injected mismatches flip. */
constexpr std::uint64_t k_flipped_bit = std::uint64_t{1} << 17;

/* ---------------------------------------------------------------------------------------------
 * Names and failure context
 * ------------------------------------------------------------------------------------------- */

/** 0x followed by 16 uppercase hex digits. */
std::string hex64(std::uint64_t value) {
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string text = "0x0000000000000000";
    for (std::size_t i = text.size(); i-- > 2;) {
        text[i] = digits[value & 0xF];
        value >>= 4;
    }
    return text;
}

const char* kind_name(step_kind kind) noexcept {
    switch (kind) {
    case step_kind::windowed:
        return "windowed";
    case step_kind::hold:
        return "hold";
    case step_kind::moving_inversion:
        return "moving inversion";
    case step_kind::turnaround:
        return "turnaround";
    }
    return "invalid step kind";
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
    case primitive_kind::stream_fill_strided:
        return "stream_fill_strided";
    case primitive_kind::verify_strided:
        return "verify_strided";
    }
    return "invalid primitive";
}

/**
 * One-letter tags of the per-pass signatures: the primitive, then its phase. The strided
 * primitives have letters of their own, so a BankStride pass that fell back to per-line
 * stream_fill and verify calls would not match its signature.
 */
char primitive_letter(primitive_kind kind) noexcept {
    switch (kind) {
    case primitive_kind::stream_fill:
        return 'S';
    case primitive_kind::fence:
        return 'F';
    case primitive_kind::flush:
        return 'X';
    case primitive_kind::verify:
        return 'V';
    case primitive_kind::verify_and_stream:
        return 'M';
    case primitive_kind::reread_word:
        return 'R';
    case primitive_kind::stream_fill_strided:
        return 'T';
    case primitive_kind::verify_strided:
        return 'U';
    }
    return '?';
}

/** Whether a record is one of the strided primitives, whose begin is its span's first byte. */
constexpr bool is_strided(primitive_kind kind) noexcept {
    return kind == primitive_kind::stream_fill_strided || kind == primitive_kind::verify_strided;
}

char phase_letter(phase_kind phase) noexcept {
    switch (phase) {
    case phase_kind::write:
        return 'w';
    case phase_kind::pre_hold_flush:
        return 'p';
    case phase_kind::verify:
        return 'v';
    case phase_kind::sweep:
        return 's';
    case phase_kind::turnaround:
        return 't';
    case phase_kind::reread:
        return 'r';
    }
    return '?';
}

/**
 * "verify 0x... +1048576 B (pass 2, phase verify, worker 0)" for failure messages; a strided
 * record also names its visits: "verify_strided 0x... +1048576 B, visits [16384, +16384) of
 * 1024 lines per sweep at stride 65536 (...)".
 */
std::string describe(const primitive_record& record) {
    std::string text = std::string(primitive_name(record.kind)) + " " + hex64(record.begin) + " +" +
                       std::to_string(record.bytes) + " B";
    if (is_strided(record.kind)) {
        text += ", visits [" + std::to_string(record.visits.first_visit) + ", +" +
                std::to_string(record.visits.visit_count) + ") of " +
                std::to_string(record.visits.lines_per_sweep) + " lines per sweep at stride " +
                std::to_string(record.visits.stride_bytes);
    }
    return text + " (pass " + std::to_string(record.ctx.pass_index) + ", phase " +
           phase_name(record.ctx.phase) + ", worker " + std::to_string(record.worker) + ")";
}

/** Byte offset of visit k of a strided record from its span's first byte (kernels.h). */
constexpr std::uint64_t strided_visit_offset(const strided_visits& visits,
                                             std::uint64_t k) noexcept {
    return k / visits.lines_per_sweep * k_cache_line_bytes +
           k % visits.lines_per_sweep * visits.stride_bytes;
}

/**
 * Whether a verification record read the word at `address`: inside [begin, begin + bytes) for
 * a plain range; on one of the lines the call visited for a strided record.
 */
bool record_covers(const primitive_record& record, std::uintptr_t address) noexcept {
    if (address < record.begin) {
        return false;
    }
    const std::uint64_t offset = address - record.begin;
    if (!is_strided(record.kind)) {
        return offset < record.bytes;
    }
    const strided_visits& v = record.visits;
    if (v.stride_bytes == 0 || v.lines_per_sweep == 0 ||
        offset >= v.lines_per_sweep * v.stride_bytes) {
        return false;
    }
    const std::uint64_t visit =
        offset % v.stride_bytes / k_cache_line_bytes * v.lines_per_sweep + offset / v.stride_bytes;
    return visit >= v.first_visit && visit - v.first_visit < v.visit_count;
}

std::uint64_t abs_diff(std::uint64_t a, std::uint64_t b) noexcept {
    return a > b ? a - b : b - a;
}

/* ---------------------------------------------------------------------------------------------
 * Steps
 * ------------------------------------------------------------------------------------------- */

/**
 * A step as the session's validated, deep-copied plan holds it. The coverage floor defaults to
 * 25 %: reported only, it never extends a step.
 */
rq_step_def make_step(std::uint32_t pattern, std::uint32_t order, std::uint32_t threads,
                      std::uint32_t hold_ms, std::uint32_t budget_ms,
                      std::uint32_t coverage_bp = 2'500) {
    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), "Step runner test step");
    step.pattern = pattern;
    step.access_order = order;
    step.thread_mode = threads;
    step.hold_ms = hold_ms;
    step.time_budget_ms = budget_ms;
    step.coverage_budget_bp = coverage_bp;
    return step;
}

/* ---------------------------------------------------------------------------------------------
 * Harness: the session's runner wiring over a fake clock and a recording layer
 * ------------------------------------------------------------------------------------------- */

/** What runner_fixture builds. */
struct harness_options {
    /** Workers, one per slice, pinned to the first logical processors. */
    std::uint32_t workers = 1;
    /** Region under test, split evenly over the workers; also the minimum, so none shrinks. */
    std::uint64_t region_bytes = k_schedule_region_bytes;
    /** Whether counting_memory_access keeps its per-worker primitive log. */
    bool log_primitives = false;
};

/** One hold as the step runner waited it: session-clock readings at its start and its end. */
struct hold_window {
    std::uint64_t begin_ns = 0;
    std::uint64_t end_ns = 0;
};

/**
 * The step runner's session clock: forwards to the fixture's fake_clock and records every hold
 * the runner waits. A hold issues no memory primitive, so the primitive log alone cannot show
 * whether a flush ran before or after it; these windows are what the flush-rule checker brackets
 * the pre-hold and post-hold sequences against.
 *
 * A hold is waited in slices. A wait that starts where the previous window ended, with no clock
 * advance in between, extends that window; within a hold nothing else moves fake time, while
 * pass_driver advances it at each pass's first primitive, so successive passes' holds stay
 * distinct windows. Calls are forwarded, so fake_clock::waited_ms() and wait_calls() count them
 * as before.
 *
 * Only the coordinator calls wait_for_stop (the test's thread inside runner_fixture::run), so the
 * windows need no lock; the test reads them after run_step returns. now_ns() is also called from
 * workers and only forwards, so it is as thread-safe as the clock it wraps.
 */
class hold_recording_clock final : public IClock {
public:
    /** @param inner The clock to forward to; held by reference, it must outlive this one. */
    explicit hold_recording_clock(IClock& inner) noexcept : inner_(inner) {}

    hold_recording_clock(const hold_recording_clock&) = delete;
    hold_recording_clock& operator=(const hold_recording_clock&) = delete;
    hold_recording_clock(hold_recording_clock&&) = delete;
    hold_recording_clock& operator=(hold_recording_clock&&) = delete;

    std::uint64_t now_ns() const noexcept override { return inner_.now_ns(); }

    /**
     * Forwards the wait and records the clock readings around it.
     *
     * @throws whatever the wrapped wait throws, or std::bad_alloc when the window list cannot
     *         grow; either leaves run_step through its documented exception path.
     */
    bool wait_for_stop(const stop_signal& signal, std::uint64_t timeout_ms) override {
        const std::uint64_t begin = inner_.now_ns();
        const bool stopped = inner_.wait_for_stop(signal, timeout_ms);
        const std::uint64_t end = inner_.now_ns();
        if (!windows_.empty() && windows_.back().end_ns == begin) {
            windows_.back().end_ns = end;
        } else {
            windows_.push_back(hold_window{begin, end});
        }
        return stopped;
    }

    /** Every hold window so far, in wait order. Read after run_step returns. */
    std::span<const hold_window> windows() const noexcept { return windows_; }

private:
    IClock& inner_;
    std::vector<hold_window> windows_;
};

/**
 * Everything session::state::prepare builds for a step runner, with substitutes: a locked
 * memory_region split into one slice per worker, a worker_pool pinned to the first placements
 * with the Win32 affinity, counting_memory_access over real_memory_access as the outermost layer
 * (the one the error collector re-reads through), a coverage_counter, an error_collector with
 * the application's cap and Stop on error off, a stop_signal, and a fake_clock that is the
 * session clock of every component. The runner reads it through a hold_recording_clock, the
 * only path to wait_for_stop, so holds() shows when each hold ran.
 *
 * Construction never throws: a failure leaves ready() false with setup_error() saying why, so
 * the test can ASSERT on it. The destructor shuts the pool down before anything the runner's
 * phase tasks refer to is destroyed, then releases the region.
 */
class runner_fixture {
public:
    explicit runner_fixture(const harness_options& options) {
        try {
            build(options);
        } catch (const std::exception& e) {
            setup_error_ = std::string("runner_fixture: ") + e.what();
            runner_.reset();
        }
    }

    ~runner_fixture() {
        pool_.shutdown();
        runner_.reset();
        region_.release();
    }

    runner_fixture(const runner_fixture&) = delete;
    runner_fixture& operator=(const runner_fixture&) = delete;
    runner_fixture(runner_fixture&&) = delete;
    runner_fixture& operator=(runner_fixture&&) = delete;

    bool ready() const noexcept { return runner_ != nullptr; }
    const std::string& setup_error() const noexcept { return setup_error_; }

    fake_clock& clock() noexcept { return clock_; }
    stop_signal& stop() noexcept { return stop_; }
    counting_memory_access& memory() noexcept { return *memory_; }
    error_collector& errors() noexcept { return *errors_; }
    std::span<const slice_view> slices() const noexcept { return slices_; }
    std::uint64_t tested_bytes() const noexcept { return region_.tested_bytes(); }

    /** The hold windows the runner waited, in order. Read after run(). */
    std::span<const hold_window> holds() const noexcept { return runner_clock_.windows(); }

    /** One run_step call on this (the coordinator) thread. Precondition: ready(). */
    step_outcome run(const rq_step_def& step, std::uint32_t step_index, step_observer& observer) {
        return runner_->run_step(step, step_index, observer);
    }

private:
    void build(const harness_options& options) {
        std::vector<logical_processor> placements = enumerate_logical_processors();
        if (placements.size() < options.workers) {
            setup_error_ = "runner_fixture: the host has fewer logical processors than workers";
            return;
        }
        placements.resize(options.workers);

        region_options request{};
        request.region_bytes = options.region_bytes;
        request.min_region_bytes = options.region_bytes;
        request.worker_count = options.workers;
        request.lock_privilege_held = false;
        const allocation_outcome allocated = region_.allocate(request, stop_);
        if (allocated.status != allocation_status::ok) {
            setup_error_ = std::string("runner_fixture: region allocation failed (Windows error ") +
                           std::to_string(allocated.os_error) + "): " + allocated.end_detail;
            return;
        }
        for (std::uint32_t worker = 0; worker < options.workers; ++worker) {
            slices_.push_back(region_.slice(worker));
        }

        affinity_ = make_win32_thread_affinity();
        memory_ = std::make_unique<counting_memory_access>(
            std::make_unique<real_memory_access>(
                tables_for(select_isa(read_cpu_features(), RQ_ISA_AUTO))),
            counting_options{.log_primitives = options.log_primitives,
                             .max_records_per_worker = k_max_log_records,
                             .max_workers = options.workers,
                             .clock = &clock_});
        coverage_ = std::make_unique<coverage_counter>(options.workers);

        error_collector_options collector{};
        collector.detail_cap = k_detail_cap;
        collector.stop_on_error = false;
        collector.step_count = k_collector_steps;
        collector.placements = placements;
        errors_ = std::make_unique<error_collector>(collector, stop_, clock_, *memory_);
        errors_->begin_run(clock_.now_ns());

        const pool_start_result started =
            pool_.start(placements, options.workers, *affinity_, stop_);
        if (started.outcome != pool_start_result::status::ok) {
            setup_error_ = "runner_fixture: worker_pool::start failed for worker " +
                           std::to_string(started.failed_worker) + " (Windows error " +
                           std::to_string(started.os_error) + ")";
            return;
        }

        runner_ = std::make_unique<step_runner>(step_runner_dependencies{
            .slices = slices_,
            .memory = *memory_,
            .pool = pool_,
            .errors = *errors_,
            .coverage = *coverage_,
            .clock = runner_clock_,
            .stop = stop_,
            .seed = k_session_seed,
        });
    }

    // Declaration order is dependency order: everything the pool's workers and the runner's
    // phase tasks refer to is declared before pool_ and therefore destroyed after it.
    fake_clock clock_;
    hold_recording_clock runner_clock_{clock_};
    stop_signal stop_;
    memory_region region_;
    std::vector<slice_view> slices_;
    std::shared_ptr<IThreadAffinity> affinity_;
    std::unique_ptr<counting_memory_access> memory_;
    std::unique_ptr<coverage_counter> coverage_;
    std::unique_ptr<error_collector> errors_;
    worker_pool pool_;
    std::unique_ptr<step_runner> runner_;
    std::string setup_error_;
};

/** Stores every step_status the runner reports, in order. */
class recording_observer final : public step_observer {
public:
    void tick(const step_status& status) override { statuses_.push_back(status); }

    const std::vector<step_status>& statuses() const noexcept { return statuses_; }

private:
    std::vector<step_status> statuses_;
};

/**
 * The counting layer's observer for one or more runs: gives each pass its scripted fake
 * duration and, on request, injects mismatches.
 *
 * Pass durations: on worker 0's first primitive of each (step, pass), pass_duration_script
 * advances the fake clock by the next scripted duration. The step runner reads the pass start
 * before posting the pass's first phase, so pass k lasts exactly per_pass_ns[k] plus any hold.
 *
 * Injection: on worker 0's first fence of an armed pass that directly follows a flush in the
 * armed phase (the final fence of a window's, slice's or block's fence, flush, fence), one plain
 * store flips k_flipped_bit in the first word of that flush's range. The range is verified next,
 * so each injection yields exactly one mismatch and one flushed re-read. The store goes through
 * no IMemoryAccess primitive, so the recorded sequence stays exactly the runner's.
 *
 * Installed on construction and removed on destruction. Every member the observer touches is
 * used only on worker 0's thread while a step runs, or by the test between runs.
 */
class pass_driver {
public:
    pass_driver(runner_fixture& fixture, std::vector<std::uint64_t> per_pass_ns)
        : memory_(fixture.memory()), script_(fixture.clock(), std::move(per_pass_ns)) {
        memory_.set_observer([this](const primitive_record& record) { on_primitive(record); });
    }

    ~pass_driver() { memory_.set_observer({}); }

    pass_driver(const pass_driver&) = delete;
    pass_driver& operator=(const pass_driver&) = delete;
    pass_driver(pass_driver&&) = delete;
    pass_driver& operator=(pass_driver&&) = delete;

    /** Arms one injection on each listed pass, at the final fence of fence_phase. Before a run. */
    void inject_on_passes(phase_kind fence_phase, std::span<const std::uint32_t> passes) {
        inject_phase_ = fence_phase;
        armed_count_ = std::min(passes.size(), armed_passes_.size());
        std::copy_n(passes.begin(), armed_count_, armed_passes_.begin());
        armed_done_.fill(false);
    }

    /** Address of each injected word, in injection order. Read after the run. */
    std::span<const std::uintptr_t> injected() const noexcept {
        return std::span<const std::uintptr_t>(injected_.data(), injected_count_);
    }

    /** True when the script could not record a pass (allocation failure). */
    bool script_failed() const noexcept { return script_failed_.load(std::memory_order_relaxed); }

private:
    static constexpr std::size_t k_max_injections = 4;

    void on_primitive(const primitive_record& record) noexcept {
        if (record.worker != 0) {
            return;
        }
        const std::uint64_t key =
            (std::uint64_t{record.ctx.step_index} << 32) | record.ctx.pass_index;
        if (key != last_pass_key_) {
            last_pass_key_ = key;
            try {
                script_.on_pass_begin(record.ctx.step_index, record.ctx.pass_index);
            } catch (const std::exception&) {
                // The observer runs inside noexcept overrides, so a failure becomes a flag the
                // test asserts instead of std::terminate.
                script_failed_.store(true, std::memory_order_relaxed);
            }
        }
        if (record.kind == primitive_kind::fence && previous_was_flush_ &&
            record.ctx.phase == inject_phase_) {
            inject(record.ctx.pass_index);
        }
        previous_was_flush_ = record.kind == primitive_kind::flush;
        if (previous_was_flush_) {
            last_flush_begin_ = record.begin;
        }
    }

    void inject(std::uint32_t pass) noexcept {
        for (std::size_t i = 0; i < armed_count_; ++i) {
            if (armed_passes_[i] != pass || armed_done_[i] || injected_count_ >= k_max_injections) {
                continue;
            }
            armed_done_[i] = true;
            volatile std::uint64_t* word =
                reinterpret_cast<volatile std::uint64_t*>(last_flush_begin_);
            *word = *word ^ k_flipped_bit;
            injected_[injected_count_++] = last_flush_begin_;
            return;
        }
    }

    counting_memory_access& memory_;
    pass_duration_script script_;
    std::atomic<bool> script_failed_{false};
    std::uint64_t last_pass_key_ = UINT64_MAX;
    bool previous_was_flush_ = false;
    std::uintptr_t last_flush_begin_ = 0;
    phase_kind inject_phase_ = phase_kind::verify;
    std::array<std::uint32_t, k_max_injections> armed_passes_{};
    std::array<bool, k_max_injections> armed_done_{};
    std::size_t armed_count_ = 0;
    std::array<std::uintptr_t, k_max_injections> injected_{};
    std::size_t injected_count_ = 0;
};

/* ---------------------------------------------------------------------------------------------
 * Flush-before-verification checker
 * ------------------------------------------------------------------------------------------- */

/** Per-line verifications and flushes one pass of a step kind performs: exactly these. */
struct kind_expectations {
    std::uint32_t verifies_per_pass = 0;
    std::uint32_t flushes_per_pass = 0;
};

/**
 * Windowed and turnaround passes verify and flush each line once; hold passes flush it twice
 * (before and after the hold) and verify it once; moving inversion flushes it after the base
 * write and after each of its two sweeps, and verifies it in both sweeps.
 */
constexpr kind_expectations expectations_for(step_kind kind) noexcept {
    switch (kind) {
    case step_kind::hold:
        return kind_expectations{1, 2};
    case step_kind::moving_inversion:
        return kind_expectations{2, 3};
    case step_kind::windowed:
    case step_kind::turnaround:
        break;
    }
    return kind_expectations{1, 1};
}

/**
 * The region's chunks keyed by base address, each mapped to a dense run of 64-byte line
 * indices: the address-range map the checker resolves every recorded range through. Line
 * indices follow the chunks' logical order, so ascending line indices are ascending logical
 * offsets.
 */
class line_table {
public:
    explicit line_table(std::span<const slice_view> slices) {
        for (const slice_view& slice : slices) {
            for (const chunk_view& chunk : slice.chunks) {
                const chunk_lines entry{reinterpret_cast<std::uintptr_t>(chunk.base), chunk.bytes,
                                        line_count_};
                by_address_.emplace(entry.base, entry);
                by_line_.push_back(entry);
                line_count_ += static_cast<std::size_t>(chunk.bytes / k_cache_line_bytes);
            }
        }
    }

    std::size_t line_count() const noexcept { return line_count_; }

    /**
     * The lines [first, last) a non-empty range overlaps.
     *
     * @return false when the range is empty or does not lie inside one chunk.
     */
    bool resolve(std::uintptr_t begin, std::uint64_t bytes, std::size_t& first,
                 std::size_t& last) const {
        if (bytes == 0) {
            return false;
        }
        auto it = by_address_.upper_bound(begin);
        if (it == by_address_.begin()) {
            return false;
        }
        --it;
        const chunk_lines& chunk = it->second;
        const std::uint64_t offset = begin - chunk.base;
        if (offset >= chunk.bytes || bytes > chunk.bytes - offset) {
            return false;
        }
        first = chunk.first_line + static_cast<std::size_t>(offset / k_cache_line_bytes);
        last = chunk.first_line +
               static_cast<std::size_t>((offset + bytes - 1) / k_cache_line_bytes) + 1;
        return true;
    }

    /** Virtual address of a line, for failure messages. Precondition: line < line_count(). */
    std::uintptr_t address_of(std::size_t line) const {
        const auto it = std::upper_bound(
            by_line_.begin(), by_line_.end(), line,
            [](std::size_t value, const chunk_lines& chunk) { return value < chunk.first_line; });
        const chunk_lines& chunk = *(it - 1);
        return chunk.base + (line - chunk.first_line) * k_cache_line_bytes;
    }

private:
    struct chunk_lines {
        std::uintptr_t base = 0;
        std::uint64_t bytes = 0;
        std::size_t first_line = 0;
    };

    std::map<std::uintptr_t, chunk_lines> by_address_;
    std::vector<chunk_lines> by_line_;
    std::size_t line_count_ = 0;
};

/**
 * Walks one worker's primitive log in issue order and checks, for every line, the
 * flush-before-verification rule and the per-pass flush and verification counts.
 *
 * Positions are 1-based log indices, 0 meaning none, so "a fence after position p" is simply
 * "the latest fence position is greater than p". Per line it keeps the line's last store or load
 * (stream_fill, verify, verify_and_stream, reread_word, and the strided stream and verification
 * of the lines they visit) and the first flush covering the line
 * that some fence separates from that access. A verification then needs that flush and a later
 * fence. Choosing the earliest such flush is exact: if any fence, flush, fence sequence follows
 * the access, the earliest fence-preceded flush is followed by its final fence too.
 *
 * Hold steps additionally need a pre_hold_flush-phase flush after a fence following the slice
 * write, a pre_hold_flush-phase fence after it, then a verify-phase fence, a verify-phase flush
 * and a fence before the verification: the sequence before the hold and again after it.
 * Positions alone cannot place those sequences in time, because the hold issues no primitive: a
 * pre-hold flush issued after the wait would log the same order. So each pass's records are also
 * bracketed by that pass's hold window on the same clock. The pass's last pre_hold_flush-phase
 * record must be the closing fence, completed (clock_after_ns) no later than the hold began, and
 * its first verify-phase record must start (clock_ns) no earlier than the hold ended and at least
 * the hold's duration after that fence: no dirty cached copy is left while the hold runs.
 * Moving-inversion lines whose last access was a sweep need the full sequence before their next
 * store and at the end of the log, so every sweep, the last one included, is followed by it.
 */
class flush_rule_checker {
public:
    /**
     * @param hold_ns Hold steps: the step's hold duration. Ignored for other kinds.
     * @param holds Hold steps: the hold window of each pass, in pass order (runner_fixture::holds);
     *        a pass without one is reported. Ignored for other kinds. Must outlive check().
     */
    flush_rule_checker(step_kind kind, const line_table& lines, std::uint32_t passes,
                       std::uint64_t hold_ns = 0, std::span<const hold_window> holds = {})
        : kind_(kind), expected_(expectations_for(kind)), passes_(passes), hold_ns_(hold_ns),
          holds_(holds), lines_(lines), states_(lines.line_count()) {}

    /** Checks one worker's complete log. Call once. */
    void check(const std::vector<primitive_record>& log) {
        if (log.empty()) {
            add_failure(std::string(kind_name(kind_)) + ": the worker issued no primitive");
            return;
        }
        worker_ = log.front().worker;
        if (log.size() >= UINT32_MAX) {
            fail_record(log.front(), 0, "a log short enough for 32-bit positions");
            return;
        }
        std::uint32_t last_pass = 0;
        for (std::size_t i = 0; i < log.size(); ++i) {
            const primitive_record& record = log[i];
            const std::uint32_t pos = static_cast<std::uint32_t>(i) + 1;
            if (record.ctx.pass_index < last_pass) {
                fail_record(record, pos, "passes in ascending order (a later pass ran earlier)");
            }
            last_pass = std::max(last_pass, record.ctx.pass_index);
            if (record.kind == primitive_kind::fence) {
                last_fence_ = pos;
                last_fence_by_phase_[static_cast<std::size_t>(record.ctx.phase)] = pos;
                continue;
            }
            if (is_strided(record.kind)) {
                check_strided(record, pos);
                continue;
            }
            std::size_t first = 0;
            std::size_t last = 0;
            if (!lines_.resolve(record.begin, record.bytes, first, last)) {
                fail_record(record, pos, "a range inside one chunk of the region");
                continue;
            }
            for (std::size_t line = first; line < last; ++line) {
                on_line(record, pos, line);
            }
        }
        for (std::size_t line = 0; line < states_.size(); ++line) {
            finish_line(line);
        }
        if (kind_ == step_kind::hold) {
            check_hold_brackets(log);
        }
    }

    std::uint64_t failure_count() const noexcept { return failure_count_; }

    /** The first failures, one per line, then the total. */
    std::string report() const {
        std::string text;
        for (const std::string& message : messages_) {
            text += message;
            text += '\n';
        }
        text += std::to_string(failure_count_) + " flush-rule failure(s)";
        return text;
    }

private:
    static constexpr std::size_t k_max_messages = 20;
    static constexpr std::size_t k_phase_count = 6;

    struct line_state {
        /** Position of the line's last store or load. */
        std::uint32_t access = 0;
        primitive_kind access_kind = primitive_kind::fence;
        /** First flush after the access that a fence separates from it. */
        std::uint32_t armed_flush = 0;
        /** Whether any flush covered the line since the access, fenced or not. */
        bool flushed_since_access = false;
        /** Hold steps: the armed pre-hold flush, and the post-hold flush that follows it. */
        std::uint32_t pre_hold_flush = 0;
        std::uint32_t post_hold_flush = 0;
        /** The pass the counts below belong to, plus one; 0 before the line's first record. */
        std::uint32_t pass_plus_one = 0;
        std::uint32_t pass_verifies = 0;
        std::uint32_t pass_flushes = 0;
        std::uint32_t total_verifies = 0;
    };

    /** Latest fence of one phase, 0 when none. */
    std::uint32_t fence_of(phase_kind phase) const noexcept {
        return last_fence_by_phase_[static_cast<std::size_t>(phase)];
    }

    /**
     * A strided record touches the lines it visits, not the range [begin, begin + bytes): each
     * visited line is checked like a line of a plain stream or verification. The span its visits
     * address, lines_per_sweep strides from begin, must lie inside one chunk.
     */
    void check_strided(const primitive_record& record, std::uint32_t pos) {
        const strided_visits& v = record.visits;
        const bool well_formed = v.stride_bytes != 0 && v.stride_bytes % k_cache_line_bytes == 0 &&
                                 v.lines_per_sweep != 0 && v.visit_count != 0 &&
                                 record.bytes == v.visit_count * k_cache_line_bytes &&
                                 v.first_visit + v.visit_count <=
                                     v.lines_per_sweep * (v.stride_bytes / k_cache_line_bytes);
        std::size_t first = 0;
        std::size_t last = 0;
        if (!well_formed ||
            !lines_.resolve(record.begin, v.lines_per_sweep * v.stride_bytes, first, last)) {
            fail_record(record, pos, "visits of whole lines of a span inside one chunk");
            return;
        }
        for (std::uint64_t k = v.first_visit; k < v.first_visit + v.visit_count; ++k) {
            on_line(record, pos,
                    first +
                        static_cast<std::size_t>(strided_visit_offset(v, k) / k_cache_line_bytes));
        }
    }

    void on_line(const primitive_record& record, std::uint32_t pos, std::size_t line) {
        line_state& state = states_[line];
        const std::uint32_t pass_plus_one = record.ctx.pass_index + 1;
        if (state.pass_plus_one != pass_plus_one) {
            if (state.pass_plus_one != 0) {
                finish_pass(state, line);
            }
            state.pass_plus_one = pass_plus_one;
            state.pass_verifies = 0;
            state.pass_flushes = 0;
        }
        switch (record.kind) {
        case primitive_kind::flush:
            on_flush(state, record, pos);
            break;
        case primitive_kind::verify:
        case primitive_kind::verify_and_stream:
        case primitive_kind::verify_strided:
            ++state.pass_verifies;
            ++state.total_verifies;
            check_verification(state, record, pos, line);
            mark_access(state, record.kind, pos);
            break;
        case primitive_kind::stream_fill:
        case primitive_kind::stream_fill_strided:
            if (kind_ == step_kind::moving_inversion &&
                state.access_kind == primitive_kind::verify_and_stream) {
                check_sweep_followed(state, record, pos, line);
            }
            mark_access(state, record.kind, pos);
            break;
        case primitive_kind::reread_word:
            // The kernel's own fence, flush, fence precede its load; after it the line has been
            // loaded, so the next verification needs a fresh sequence.
            mark_access(state, record.kind, pos);
            break;
        case primitive_kind::fence:
            break;
        }
    }

    void on_flush(line_state& state, const primitive_record& record, std::uint32_t pos) {
        ++state.pass_flushes;
        if (state.access == 0) {
            return;
        }
        state.flushed_since_access = true;
        if (state.armed_flush == 0 && last_fence_ > state.access) {
            state.armed_flush = pos;
        }
        if (kind_ != step_kind::hold) {
            return;
        }
        if (record.ctx.phase == phase_kind::pre_hold_flush) {
            if (state.pre_hold_flush == 0 && last_fence_ > state.access) {
                state.pre_hold_flush = pos;
            }
        } else if (record.ctx.phase == phase_kind::verify && state.pre_hold_flush != 0 &&
                   state.post_hold_flush == 0) {
            const std::uint32_t pre_hold_fence = fence_of(phase_kind::pre_hold_flush);
            if (pre_hold_fence > state.pre_hold_flush &&
                fence_of(phase_kind::verify) > pre_hold_fence) {
                state.post_hold_flush = pos;
            }
        }
    }

    /** The element of the fence, flush, fence sequence missing since the line's last access. */
    const char* missing_sequence_element(const line_state& state) const noexcept {
        if (state.access == 0) {
            return "a write before the verification";
        }
        if (state.armed_flush == 0) {
            if (state.flushed_since_access) {
                return "the first fence, between the line's last access and its flush";
            }
            return last_fence_ > state.access ? "a flush covering the line"
                                              : "the first fence after the line's last access";
        }
        if (last_fence_ <= state.armed_flush) {
            return "the second fence, between the flush and the verification";
        }
        return nullptr;
    }

    void check_verification(const line_state& state, const primitive_record& record,
                            std::uint32_t pos, std::size_t line) {
        if (const char* missing = missing_sequence_element(state)) {
            fail_line(record, pos, line, missing);
            return;
        }
        if (kind_ != step_kind::hold) {
            return;
        }
        if (state.pre_hold_flush == 0) {
            fail_line(record, pos, line,
                      "the pre-hold flush (phase pre_hold_flush) after a fence following the "
                      "slice write");
        } else if (fence_of(phase_kind::pre_hold_flush) <= state.pre_hold_flush) {
            fail_line(record, pos, line, "the pre-hold fence after the pre-hold flush");
        } else if (state.post_hold_flush == 0) {
            fail_line(record, pos, line,
                      "the post-hold fence and flush (phase verify) after the pre-hold sequence");
        } else if (last_fence_ <= state.post_hold_flush) {
            fail_line(record, pos, line,
                      "the fence between the post-hold flush and the verification");
        }
    }

    /** A moving-inversion sweep's lines need the full sequence before anything else touches them.
     */
    void check_sweep_followed(const line_state& state, const primitive_record& record,
                              std::uint32_t pos, std::size_t line) {
        if (const char* missing = missing_sequence_element(state)) {
            fail_line(
                record, pos, line,
                (std::string(missing) + ", after the sweep that last loaded the line").c_str());
        }
    }

    static void mark_access(line_state& state, primitive_kind kind, std::uint32_t pos) noexcept {
        state.access = pos;
        state.access_kind = kind;
        state.armed_flush = 0;
        state.flushed_since_access = false;
        state.pre_hold_flush = 0;
        state.post_hold_flush = 0;
    }

    /** Exact per-pass counts: never fewer verifications, never a flush beyond the rule. */
    void finish_pass(const line_state& state, std::size_t line) {
        const std::uint32_t pass = state.pass_plus_one - 1;
        if (state.pass_verifies != expected_.verifies_per_pass) {
            fail_count(line, pass, "verifications", state.pass_verifies,
                       expected_.verifies_per_pass);
        }
        if (state.pass_flushes != expected_.flushes_per_pass) {
            fail_count(line, pass, "flushes", state.pass_flushes, expected_.flushes_per_pass);
        }
    }

    void finish_line(std::size_t line) {
        const line_state& state = states_[line];
        if (state.pass_plus_one == 0) {
            add_failure(where(line, 0) + ": never written or verified by the step");
            return;
        }
        finish_pass(state, line);
        const std::uint32_t last_pass = state.pass_plus_one - 1;
        const std::uint64_t expected_total = std::uint64_t{expected_.verifies_per_pass} * passes_;
        if (state.total_verifies != expected_total) {
            add_failure(where(line, last_pass) + ": verified " +
                        std::to_string(state.total_verifies) + " times over the run, expected " +
                        std::to_string(expected_total));
        }
        if (kind_ == step_kind::moving_inversion &&
            state.access_kind == primitive_kind::verify_and_stream) {
            if (const char* missing = missing_sequence_element(state)) {
                add_failure(where(line, last_pass) +
                            ": the final sweep is not followed by fence, flush, fence; missing " +
                            missing);
            }
        }
    }

    /**
     * Hold steps: places each pass's pre-hold and post-hold sequences in time against the pass's
     * hold window, which the per-line position checks cannot do because the wait between the
     * two sequences issues no primitive.
     */
    void check_hold_brackets(const std::vector<primitive_record>& log) {
        // 1-based log positions, 0 meaning none: each pass's last pre_hold_flush-phase record
        // (the closing fence) and its first verify-phase record (the post-hold opening fence).
        std::vector<std::uint32_t> closing(passes_, 0);
        std::vector<std::uint32_t> opening(passes_, 0);
        for (std::size_t i = 0; i < log.size(); ++i) {
            const access_context& ctx = log[i].ctx;
            if (ctx.pass_index >= passes_) {
                continue;
            }
            const std::uint32_t pos = static_cast<std::uint32_t>(i) + 1;
            if (ctx.phase == phase_kind::pre_hold_flush) {
                closing[ctx.pass_index] = pos;
            } else if (ctx.phase == phase_kind::verify && opening[ctx.pass_index] == 0) {
                opening[ctx.pass_index] = pos;
            }
        }
        for (std::uint32_t pass = 0; pass < passes_; ++pass) {
            if (pass >= holds_.size()) {
                add_failure(where_pass(pass) + ": missing the pass's hold window (" +
                            std::to_string(holds_.size()) + " recorded for " +
                            std::to_string(passes_) + " passes)");
                continue;
            }
            const hold_window& hold = holds_[pass];
            if (closing[pass] == 0) {
                add_failure(
                    where_pass(pass) +
                    ": missing the pre-hold closing fence (no pre_hold_flush-phase record)");
                continue;
            }
            const primitive_record& fence = log[closing[pass] - 1];
            if (fence.kind != primitive_kind::fence) {
                fail_hold(fence, closing[pass],
                          "the pass's last pre_hold_flush-phase record to be the pre-hold "
                          "closing fence");
                continue;
            }
            if (fence.clock_after_ns > hold.begin_ns) {
                fail_hold(
                    fence, closing[pass],
                    "the pre-hold closing fence to complete before the hold: clock_after_ns " +
                        std::to_string(fence.clock_after_ns) + " ns is after the hold's begin_ns " +
                        std::to_string(hold.begin_ns) + " ns");
            }
            if (opening[pass] == 0) {
                add_failure(where_pass(pass) +
                            ": missing the post-hold sequence (no verify-phase record)");
                continue;
            }
            const primitive_record& open = log[opening[pass] - 1];
            if (open.clock_ns < hold.end_ns) {
                fail_hold(open, opening[pass],
                          "the post-hold sequence to start after the hold: clock_ns " +
                              std::to_string(open.clock_ns) + " ns is before the hold's end_ns " +
                              std::to_string(hold.end_ns) + " ns");
            }
            const std::uint64_t earliest = fence.clock_after_ns > UINT64_MAX - hold_ns_
                                               ? UINT64_MAX
                                               : fence.clock_after_ns + hold_ns_;
            if (open.clock_ns < earliest) {
                fail_hold(open, opening[pass],
                          "the post-hold sequence to start at least the hold (" +
                              std::to_string(hold_ns_) +
                              " ns) after the pre-hold closing fence: clock_ns " +
                              std::to_string(open.clock_ns) + " ns is before " +
                              std::to_string(earliest) + " ns, the fence's clock_after_ns " +
                              std::to_string(fence.clock_after_ns) + " ns plus the hold");
            }
        }
    }

    /** "<kind> pass P worker W line 0x...": where a line-level failure happened. */
    std::string where(std::size_t line, std::uint32_t pass) const {
        return std::string(kind_name(kind_)) + " pass " + std::to_string(pass) + " worker " +
               std::to_string(worker_) + " line " + hex64(lines_.address_of(line));
    }

    void fail_line(const primitive_record& record, std::uint32_t pos, std::size_t line,
                   const char* missing) {
        add_failure(where(line, record.ctx.pass_index) + " at record #" + std::to_string(pos) +
                    " (" + describe(record) + "): missing " + missing);
    }

    void fail_record(const primitive_record& record, std::uint32_t pos, const char* missing) {
        add_failure(std::string(kind_name(kind_)) + " record #" + std::to_string(pos) + " (" +
                    describe(record) + "): expected " + missing);
    }

    /** "<kind> pass P worker W": where a pass-level failure happened. */
    std::string where_pass(std::uint32_t pass) const {
        return std::string(kind_name(kind_)) + " pass " + std::to_string(pass) + " worker " +
               std::to_string(worker_);
    }

    void fail_hold(const primitive_record& record, std::uint32_t pos, const std::string& expected) {
        add_failure(where_pass(record.ctx.pass_index) + " record #" + std::to_string(pos) + " (" +
                    describe(record) + "): expected " + expected);
    }

    void fail_count(std::size_t line, std::uint32_t pass, const char* what, std::uint32_t actual,
                    std::uint32_t expected) {
        add_failure(where(line, pass) + ": " + std::to_string(actual) + " " + what +
                    ", expected exactly " + std::to_string(expected));
    }

    void add_failure(std::string message) {
        ++failure_count_;
        if (messages_.size() < k_max_messages) {
            messages_.push_back(std::move(message));
        }
    }

    const step_kind kind_;
    const kind_expectations expected_;
    const std::uint32_t passes_;
    const std::uint64_t hold_ns_;
    const std::span<const hold_window> holds_;
    const line_table& lines_;
    std::vector<line_state> states_;
    /** The worker whose log is checked; every record of a per-worker log carries it. */
    std::uint32_t worker_ = 0;
    std::uint32_t last_fence_ = 0;
    std::array<std::uint32_t, k_phase_count> last_fence_by_phase_{};
    std::uint64_t failure_count_ = 0;
    std::vector<std::string> messages_;
};

/* ---------------------------------------------------------------------------------------------
 * Per-pass structure
 * ------------------------------------------------------------------------------------------- */

/**
 * One pass of one worker's log as primitive-and-phase letter pairs, re-reads left out. Runs of
 * equal non-fence pairs collapse into one pair, so a window's 1 MiB streams read as one "Sw" (one
 * "Tw" for BankStride's strided streams) and its 1 MiB flush groups as one "Xv"; fences never
 * collapse, so every fence shows and "FXF" means exactly one fence on each side of a window's or
 * slice's flushes.
 */
std::string pass_signature(const std::vector<primitive_record>& log, std::uint32_t pass) {
    std::string signature;
    char previous_kind = 0;
    char previous_phase = 0;
    for (const primitive_record& record : log) {
        if (record.ctx.pass_index != pass || record.kind == primitive_kind::reread_word) {
            continue;
        }
        const char letter = primitive_letter(record.kind);
        const char phase = phase_letter(record.ctx.phase);
        if (letter != 'F' && letter == previous_kind && phase == previous_phase) {
            continue;
        }
        signature += letter;
        signature += phase;
        previous_kind = letter;
        previous_phase = phase;
    }
    return signature;
}

std::string repeated(const char* unit, std::size_t times) {
    std::string text;
    for (std::size_t i = 0; i < times; ++i) {
        text += unit;
    }
    return text;
}

/** Windows of k_window_bytes or less over the slices' chunks: windows never span chunks. */
std::size_t window_count(std::span<const slice_view> slices) noexcept {
    std::size_t windows = 0;
    for (const slice_view& slice : slices) {
        for (const chunk_view& chunk : slice.chunks) {
            windows +=
                static_cast<std::size_t>((chunk.bytes + k_window_bytes - 1) / k_window_bytes);
        }
    }
    return windows;
}

/**
 * The signature one pass must have when one worker covers every slice:
 * - windowed, per window: stream, then fence, flush, fence and verify (phase verify); in the
 *   BankStride order the stream and the verification are strided calls only;
 * - hold, per slice: stream and the pre-hold fence, flush, fence; then, after the hold, per
 *   slice: fence, flush, fence (phase verify) and verify;
 * - moving inversion, per slice and per sweep: the base write, then each verify-and-stream
 *   sweep, each followed by fence, flush, fence in its own phase.
 * Turnaround is checked action by action instead (expected_turnaround_pass).
 */
std::string expected_signature(step_kind kind, std::uint32_t order,
                               std::span<const slice_view> slices) {
    switch (kind) {
    case step_kind::windowed:
        return repeated(order == RQ_ORDER_BANK_STRIDE ? "TwFvXvFvUv" : "SwFvXvFvVv",
                        window_count(slices));
    case step_kind::hold:
        return repeated("SwFpXpFp", slices.size()) + repeated("FvXvFvVv", slices.size());
    case step_kind::moving_inversion:
        return repeated("SwFwXwFw", slices.size()) + repeated("MsFsXsFs", 2 * slices.size());
    case step_kind::turnaround:
        break;
    }
    return std::string();
}

/** One primitive as the turnaround schedule prescribes it: kind and range (empty for a fence). */
struct expected_action {
    primitive_kind kind = primitive_kind::fence;
    std::uintptr_t begin = 0;
    std::uint64_t bytes = 0;
};

/**
 * The actions one turnaround pass must issue, per window of n blocks of 4 KiB:
 * - start-up: blocks 0 to 3 each streamed, then fenced, with no verification;
 * - steady state, for i = 4 ... n - 1: stream block i, flush block i - 4, fence, verify i - 4;
 * - drain: flush blocks n - 4 ... n - 1, fence, verify them.
 * So each block sees its stream, a fence, its flush, a fence and its verification, in order.
 */
std::vector<expected_action> expected_turnaround_pass(std::span<const slice_view> slices) {
    std::vector<expected_action> actions;
    const auto block = [](std::uintptr_t window, std::uint64_t index, primitive_kind kind) {
        return expected_action{kind, window + index * k_turnaround_block, k_turnaround_block};
    };
    for (const slice_view& slice : slices) {
        for (const chunk_view& chunk : slice.chunks) {
            for (std::uint64_t offset = 0; offset < chunk.bytes; offset += k_window_bytes) {
                const std::uintptr_t window = reinterpret_cast<std::uintptr_t>(chunk.base) +
                                              static_cast<std::uintptr_t>(offset);
                const std::uint64_t n =
                    std::min(k_window_bytes, chunk.bytes - offset) / k_turnaround_block;
                for (std::uint64_t b = 0; b < k_turnaround_lag; ++b) {
                    actions.push_back(block(window, b, primitive_kind::stream_fill));
                    actions.push_back(expected_action{});
                }
                for (std::uint64_t i = k_turnaround_lag; i < n; ++i) {
                    actions.push_back(block(window, i, primitive_kind::stream_fill));
                    actions.push_back(block(window, i - k_turnaround_lag, primitive_kind::flush));
                    actions.push_back(expected_action{});
                    actions.push_back(block(window, i - k_turnaround_lag, primitive_kind::verify));
                }
                for (std::uint64_t b = n - k_turnaround_lag; b < n; ++b) {
                    actions.push_back(block(window, b, primitive_kind::flush));
                }
                actions.push_back(expected_action{});
                for (std::uint64_t b = n - k_turnaround_lag; b < n; ++b) {
                    actions.push_back(block(window, b, primitive_kind::verify));
                }
            }
        }
    }
    return actions;
}

/** One pass of one worker's log, re-reads left out. */
std::vector<primitive_record> pass_records(const std::vector<primitive_record>& log,
                                           std::uint32_t pass) {
    std::vector<primitive_record> records;
    for (const primitive_record& record : log) {
        if (record.ctx.pass_index == pass && record.kind != primitive_kind::reread_word) {
            records.push_back(record);
        }
    }
    return records;
}

/** Asserts one turnaround pass action by action, reporting the first deviation. */
void expect_turnaround_pass(const std::vector<primitive_record>& log, std::uint32_t pass,
                            std::span<const slice_view> slices) {
    const std::vector<expected_action> expected = expected_turnaround_pass(slices);
    const std::vector<primitive_record> actual = pass_records(log, pass);
    ASSERT_EQ(actual.size(), expected.size()) << "turnaround pass " << pass << " action count";
    for (std::size_t i = 0; i < expected.size(); ++i) {
        const expected_action& want = expected[i];
        const primitive_record& got = actual[i];
        const bool same =
            got.kind == want.kind && (want.kind == primitive_kind::fence ||
                                      (got.begin == want.begin && got.bytes == want.bytes));
        if (!same) {
            ADD_FAILURE() << "turnaround pass " << pass << " action " << i << ": expected "
                          << primitive_name(want.kind) << " " << hex64(want.begin) << " +"
                          << want.bytes << " B, got " << describe(got);
            return;
        }
        if (got.ctx.phase != phase_kind::turnaround ||
            got.ctx.access_type != RQ_ACCESS_TURNAROUND_READ) {
            ADD_FAILURE() << "turnaround pass " << pass << " action " << i
                          << ": expected phase turnaround with RQ_ACCESS_TURNAROUND_READ, got "
                          << describe(got);
            return;
        }
    }
}

/**
 * Asserts the two moving-inversion sweeps of one pass: every verify-and-stream covers one
 * 1 MiB block, the first sweep visits the blocks in ascending logical order, and the second
 * visits exactly the same blocks in the reverse order.
 */
void expect_inversion_sweeps(const std::vector<primitive_record>& log, std::uint32_t pass,
                             const line_table& lines, std::uint64_t covered_bytes) {
    std::vector<std::size_t> blocks;
    for (const primitive_record& record : pass_records(log, pass)) {
        if (record.kind != primitive_kind::verify_and_stream) {
            continue;
        }
        EXPECT_EQ(record.bytes, k_work_group_bytes) << "pass " << pass << ": " << describe(record);
        std::size_t first = 0;
        std::size_t last = 0;
        ASSERT_TRUE(lines.resolve(record.begin, record.bytes, first, last)) << describe(record);
        blocks.push_back(first);
    }
    const std::size_t per_sweep = static_cast<std::size_t>(covered_bytes / k_work_group_bytes);
    ASSERT_EQ(blocks.size(), 2 * per_sweep) << "moving inversion pass " << pass;
    const std::vector<std::size_t> ascending(blocks.begin(), blocks.begin() + per_sweep);
    const std::vector<std::size_t> descending(blocks.begin() + per_sweep, blocks.end());
    EXPECT_TRUE(std::adjacent_find(ascending.begin(), ascending.end(), std::greater_equal<>()) ==
                ascending.end())
        << "moving inversion pass " << pass << ": the first sweep is not ascending";
    EXPECT_TRUE(std::equal(ascending.rbegin(), ascending.rend(), descending.begin()))
        << "moving inversion pass " << pass
        << ": the second sweep is not the first one's blocks in descending order";
}

/**
 * Asserts the interface calls of one BankStride pass: no plain stream, verification or
 * verify-and-stream; per window, one stream_fill_strided (phase write) and one verify_strided
 * (phase verify) per 1 MiB work group, so each MiB crosses the memory interface once each way.
 * Each call covers its group's 16,384 visits at the pass's stride, and a window's calls of
 * either kind, expanded line by line, yield exactly order_cursor's BankStride ranges in order.
 */
void expect_bank_stride_pass(const std::vector<primitive_record>& log, std::uint32_t pass,
                             std::span<const slice_view> slices, std::uint64_t covered_bytes) {
    constexpr std::uint64_t group_visits = k_work_group_bytes / k_cache_line_bytes;
    const std::uint64_t stride = bank_stride_bytes(pass);
    const std::vector<primitive_record> records = pass_records(log, pass);
    const auto plain = std::count_if(records.begin(), records.end(), [](const primitive_record& r) {
        return r.kind == primitive_kind::stream_fill || r.kind == primitive_kind::verify ||
               r.kind == primitive_kind::verify_and_stream;
    });
    EXPECT_EQ(plain, 0) << "BankStride pass " << pass << " issued per-range calls";

    for (const primitive_kind kind :
         {primitive_kind::stream_fill_strided, primitive_kind::verify_strided}) {
        const phase_kind phase =
            kind == primitive_kind::stream_fill_strided ? phase_kind::write : phase_kind::verify;
        std::uint64_t calls = 0;
        std::size_t windows = 0;
        std::uintptr_t span = 0;
        std::uint64_t span_visits = 0;
        std::uint64_t next_visit = 0;
        std::optional<order_cursor> cursor;
        for (const primitive_record& record : records) {
            if (record.kind != kind) {
                continue;
            }
            const strided_visits& v = record.visits;
            if (!cursor || record.begin != span) {
                ASSERT_EQ(next_visit, span_visits)
                    << "the previous window was left part-visited before " << describe(record);
                ASSERT_NE(v.lines_per_sweep, 0u) << describe(record);
                span = record.begin;
                span_visits = v.lines_per_sweep * stride / k_cache_line_bytes;
                next_visit = 0;
                cursor.emplace(
                    order_params{RQ_ORDER_BANK_STRIDE, v.lines_per_sweep * stride, 0, 0, pass, 0});
                ++windows;
            }
            ++calls;
            ASSERT_EQ(record.ctx.phase, phase) << describe(record);
            ASSERT_EQ(v.stride_bytes, stride) << describe(record);
            ASSERT_EQ(v.first_visit, next_visit) << describe(record);
            ASSERT_EQ(v.visit_count, group_visits) << describe(record);
            ASSERT_EQ(record.bytes, k_work_group_bytes) << describe(record);
            for (std::uint64_t k = v.first_visit; k < v.first_visit + v.visit_count; ++k) {
                order_range range{};
                ASSERT_TRUE(cursor->next(range)) << "visit " << k << " beyond the cursor's ranges";
                ASSERT_TRUE(range.offset == strided_visit_offset(v, k) &&
                            range.length == k_cache_line_bytes)
                    << "visit " << k << " at offset " << strided_visit_offset(v, k)
                    << " where the cursor yields " << range.offset << " +" << range.length << ": "
                    << describe(record);
            }
            next_visit += v.visit_count;
        }
        EXPECT_EQ(next_visit, span_visits) << "the last window was left part-visited";
        EXPECT_EQ(windows, window_count(slices)) << primitive_name(kind);
        EXPECT_EQ(calls, covered_bytes / k_work_group_bytes)
            << primitive_name(kind) << " calls of BankStride pass " << pass;
    }
}

/**
 * Asserts the re-reads of one worker's log: one per injected word, each a single 8-byte
 * reread_word right after the verification that found the mismatch (only other re-reads in
 * between), with that verification's context in which only the phase changed. No fence or
 * flush record belongs to a re-read: the kernel performs its own.
 */
void expect_rereads(const std::vector<primitive_record>& log,
                    std::span<const std::uintptr_t> injected) {
    std::size_t rereads = 0;
    for (std::size_t i = 0; i < log.size(); ++i) {
        const primitive_record& reread = log[i];
        if (reread.kind != primitive_kind::reread_word) {
            continue;
        }
        ++rereads;
        EXPECT_EQ(reread.bytes, sizeof(std::uint64_t)) << describe(reread);
        EXPECT_EQ(reread.ctx.phase, phase_kind::reread) << describe(reread);
        EXPECT_TRUE(std::find(injected.begin(), injected.end(), reread.begin) != injected.end())
            << "re-read of a word that was not injected: " << describe(reread);
        std::size_t j = i;
        while (j > 0 && log[j - 1].kind == primitive_kind::reread_word) {
            --j;
        }
        ASSERT_GT(j, 0u) << "re-read without a preceding verification: " << describe(reread);
        const primitive_record& verification = log[j - 1];
        ASSERT_TRUE(verification.kind == primitive_kind::verify ||
                    verification.kind == primitive_kind::verify_and_stream ||
                    verification.kind == primitive_kind::verify_strided)
            << "re-read " << describe(reread) << " follows " << describe(verification);
        EXPECT_TRUE(record_covers(verification, reread.begin))
            << "re-read " << describe(reread) << " lies outside " << describe(verification);
        EXPECT_EQ(reread.ctx.step_index, verification.ctx.step_index);
        EXPECT_EQ(reread.ctx.pass_index, verification.ctx.pass_index);
        EXPECT_EQ(reread.ctx.access_type, verification.ctx.access_type);
        EXPECT_EQ(reread.ctx.worker_index, verification.ctx.worker_index);
        EXPECT_EQ(reread.ctx.write_time_ns, verification.ctx.write_time_ns);
    }
    EXPECT_EQ(rereads, injected.size());
}

/**
 * Sum of the bytes of every verify, verify_and_stream and verify_strided record (a strided
 * record's bytes are its visited lines): what coverage counts.
 */
std::uint64_t logged_verified_bytes(const std::vector<primitive_record>& log) noexcept {
    std::uint64_t bytes = 0;
    for (const primitive_record& record : log) {
        if (record.kind == primitive_kind::verify ||
            record.kind == primitive_kind::verify_and_stream ||
            record.kind == primitive_kind::verify_strided) {
            bytes += record.bytes;
        }
    }
    return bytes;
}

/** The phase whose final fence precedes a verification of each kind: where injections go. */
constexpr phase_kind injection_phase(step_kind kind) noexcept {
    switch (kind) {
    case step_kind::moving_inversion:
        // The base write's fence, flush, fence precedes the ascending sweep's verification.
        return phase_kind::write;
    case step_kind::turnaround:
        return phase_kind::turnaround;
    case step_kind::windowed:
    case step_kind::hold:
        break;
    }
    return phase_kind::verify;
}

/* ---------------------------------------------------------------------------------------------
 * Pure helpers
 * ------------------------------------------------------------------------------------------- */

struct planned_case {
    const char* name;
    std::uint64_t budget_ns;
    std::uint64_t mean_pass_ns;
    std::uint32_t expected;
};

void PrintTo(const planned_case& c, std::ostream* os) {
    *os << c.name;
}

/** max(1, round(budget / mean)) with half rounding up, the mean clamped to 1 us, saturated. */
constexpr planned_case k_planned_cases[] = {
    {"StableOneSecondPasses", 10 * k_ns_per_s, k_ns_per_s, 10},
    {"TwoAndAHalfRoundsUp", 10 * k_ns_per_s, 4 * k_ns_per_s, 3},
    {"OneAndAHalfRoundsUp", 9 * k_ns_per_s, 6 * k_ns_per_s, 2},
    {"FiveAndAHalfRoundsUp", 11 * k_ns_per_s, 2 * k_ns_per_s, 6},
    {"ThreeAndAThirdRoundsDown", 10 * k_ns_per_s, 3 * k_ns_per_s, 3},
    {"HalfAPassRoundsUpToOne", 10 * k_ns_per_s, 20 * k_ns_per_s, 1},
    {"UnderHalfAPassStillRunsOne", 10 * k_ns_per_s, 21 * k_ns_per_s, 1},
    {"OversizedPassRunsAlone", 10 * k_ns_per_s, 30 * k_ns_per_s, 1},
    {"ZeroBudgetRunsOne", 0, k_ns_per_s, 1},
    {"ZeroMeanIsClampedToOneMicrosecond", 5 * k_ns_per_ms, 0, 5'000},
    {"MeanBelowTheFloorIsClamped", 5 * k_ns_per_ms, k_min_mean_pass_ns - 1, 5'000},
    {"MeanAtTheFloor", 5 * k_ns_per_ms, k_min_mean_pass_ns, 5'000},
    {"MeanJustAboveTheFloor", 5 * k_ns_per_ms, k_min_mean_pass_ns + 1, 4'995},
    {"SaturatesAtUint32Max", UINT64_MAX, 1, UINT32_MAX},
    {"HugeMeanGivesOne", UINT64_MAX, UINT64_MAX, 1},
};

class StepRunnerPlannedPasses : public ::testing::TestWithParam<planned_case> {};

TEST_P(StepRunnerPlannedPasses, RoundsTheBudgetOverTheMeanHalfUp) {
    const planned_case& c = GetParam();
    EXPECT_EQ(planned_passes(c.budget_ns, c.mean_pass_ns), c.expected)
        << "budget " << c.budget_ns << " ns, mean " << c.mean_pass_ns << " ns";
}

INSTANTIATE_TEST_SUITE_P(StepRunnerSchedules, StepRunnerPlannedPasses,
                         ::testing::ValuesIn(k_planned_cases),
                         [](const ::testing::TestParamInfo<planned_case>& info) {
                             return std::string(info.param.name);
                         });

struct classify_case {
    const char* name;
    std::uint32_t pattern;
    std::uint32_t order;
    std::uint32_t hold_ms;
    step_kind kind;
    std::uint32_t access_type;
};

void PrintTo(const classify_case& c, std::ostream* os) {
    *os << c.name;
}

/** Precedence: a moving-inversion pattern, then a hold, then the turnaround order. */
constexpr classify_case k_classify_cases[] = {
    {"MovingInversionWalking", RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING,
     0, step_kind::moving_inversion, RQ_ACCESS_READ_MODIFY_WRITE},
    {"MovingInversionRandomIgnoresTurnaround", RQ_PATTERN_MOVING_INVERSION_RANDOM,
     RQ_ORDER_TURNAROUND, 0, step_kind::moving_inversion, RQ_ACCESS_READ_MODIFY_WRITE},
    {"MovingInversionWinsOverAHold", RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_RANDOM_BLOCKS,
     5'000, step_kind::moving_inversion, RQ_ACCESS_READ_MODIFY_WRITE},
    {"HoldOfFiveSeconds", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, 5'000, step_kind::hold,
     RQ_ACCESS_VERIFY_AFTER_HOLD},
    {"HoldWinsOverTurnaround", RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, 30'000, step_kind::hold,
     RQ_ACCESS_VERIFY_AFTER_HOLD},
    {"Turnaround", RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, 0, step_kind::turnaround,
     RQ_ACCESS_TURNAROUND_READ},
    {"HighToggleSequential", RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING, 0,
     step_kind::windowed, RQ_ACCESS_VERIFY_AFTER_WRITE},
    {"CheckerboardDescending", RQ_PATTERN_CHECKERBOARD, RQ_ORDER_SEQUENTIAL_DESCENDING, 0,
     step_kind::windowed, RQ_ACCESS_VERIFY_AFTER_WRITE},
    {"RandomBankStride", RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, 0, step_kind::windowed,
     RQ_ACCESS_VERIFY_AFTER_WRITE},
    {"RandomBlocksWithoutHold", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, 0, step_kind::windowed,
     RQ_ACCESS_VERIFY_AFTER_WRITE},
};

class StepRunnerClassify : public ::testing::TestWithParam<classify_case> {};

TEST_P(StepRunnerClassify, SelectsTheKindAndItsVerificationAccessType) {
    const classify_case& c = GetParam();
    const rq_step_def step = make_step(c.pattern, c.order, RQ_THREADS_ALL, c.hold_ms, 10'000);
    const step_kind kind = classify_step(step);
    EXPECT_EQ(kind, c.kind) << "classified as " << kind_name(kind);
    EXPECT_EQ(verify_access_type(kind), c.access_type);
}

INSTANTIATE_TEST_SUITE_P(StepRunnerKinds, StepRunnerClassify, ::testing::ValuesIn(k_classify_cases),
                         [](const ::testing::TestParamInfo<classify_case>& info) {
                             return std::string(info.param.name);
                         });

TEST(StepRunnerHelpers, PatternSeedsDifferPerStepAndFollowSplitMix64) {
    // SplitMix64's first output for state 0, as published with the generator.
    EXPECT_EQ(step_pattern_seed(0, 0), 0xE220A8397B1DCDAFull);
    std::vector<std::uint64_t> seeds;
    for (std::uint32_t step_index = 0; step_index < k_max_step_count; ++step_index) {
        seeds.push_back(step_pattern_seed(k_session_seed, step_index));
    }
    std::sort(seeds.begin(), seeds.end());
    EXPECT_TRUE(std::adjacent_find(seeds.begin(), seeds.end()) == seeds.end())
        << "two steps of one session share a pattern seed";
}

/* ---------------------------------------------------------------------------------------------
 * Pass scheduling and overrun, under the fake clock
 * ------------------------------------------------------------------------------------------- */

/** Asserts what every completed run reports: exact fake-time totals and exact coverage. */
void expect_outcome_accounting(runner_fixture& fixture, const step_outcome& outcome,
                               std::uint32_t step_index, std::uint64_t budget_ns) {
    EXPECT_EQ(outcome.overrun_ns, outcome.actual_ns > budget_ns ? outcome.actual_ns - budget_ns : 0)
        << "overrun must be max(0, actual - budget)";
    EXPECT_EQ(outcome.verified_bytes, std::uint64_t{outcome.passes} * fixture.tested_bytes())
        << "a windowed pass verifies every tested byte once";
    EXPECT_EQ(outcome.verified_bytes, fixture.memory().verified_bytes_for_step(step_index))
        << "the coverage counter must agree with the independent count";
}

struct stable_case {
    const char* name;
    std::uint64_t budget_ms;
    std::uint64_t pass_ns;
    std::uint32_t expected_passes;
};

void PrintTo(const stable_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr stable_case k_stable_cases[] = {
    {"OneSecondPassesFillTenSeconds", 10'000, k_ns_per_s, 10},
    {"ShortPassesRoundUp", 10'000, 950 * k_ns_per_ms, 11},
    {"LongPassesRoundDown", 10'000, 1'050 * k_ns_per_ms, 10},
    {"FourSecondPassesEndHalfAPassLate", 10'000, 4 * k_ns_per_s, 3},
};

class StepRunnerStable : public ::testing::TestWithParam<stable_case> {};

TEST_P(StepRunnerStable, EndsWithinHalfAMeanPassOfTheBudget) {
    const stable_case& c = GetParam();
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {c.pass_ns});
    recording_observer observer;
    const rq_step_def step = make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
                                       RQ_THREADS_ALL, 0, static_cast<std::uint32_t>(c.budget_ms));
    const std::uint64_t budget_ns = c.budget_ms * k_ns_per_ms;

    const step_outcome outcome = fixture.run(step, 0, observer);

    EXPECT_FALSE(driver.script_failed());
    EXPECT_TRUE(outcome.completed);
    EXPECT_EQ(outcome.passes, c.expected_passes);
    EXPECT_EQ(outcome.actual_ns, std::uint64_t{c.expected_passes} * c.pass_ns)
        << "under the fake clock a step lasts exactly its scripted passes";
    EXPECT_LE(abs_diff(outcome.actual_ns, budget_ns), c.pass_ns / 2)
        << "stable passes end within half a mean pass of the budget";
    expect_outcome_accounting(fixture, outcome, 0, budget_ns);
}

INSTANTIATE_TEST_SUITE_P(StepRunnerStablePasses, StepRunnerStable,
                         ::testing::ValuesIn(k_stable_cases),
                         [](const ::testing::TestParamInfo<stable_case>& info) {
                             return std::string(info.param.name);
                         });

TEST(StepRunnerScheduling, JitteredPassesEndWithinOneLongestPassOfTheBudget) {
    constexpr std::uint32_t k_budget_ms = 20'000;
    constexpr std::uint64_t k_budget_ns = std::uint64_t{k_budget_ms} * k_ns_per_ms;
    constexpr std::size_t k_scripted_passes = 64;
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    const rq_step_def step =
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0, k_budget_ms);

    for (std::uint64_t seed = 1; seed <= 20; ++seed) {
        SCOPED_TRACE("jitter seed " + std::to_string(seed));
        // +/-30 % around a 1 s mean.
        std::mt19937_64 generator(seed);
        std::uniform_int_distribution<std::uint64_t> duration(700 * k_ns_per_ms,
                                                              1'300 * k_ns_per_ms);
        std::vector<std::uint64_t> script(k_scripted_passes);
        for (std::uint64_t& pass_ns : script) {
            pass_ns = duration(generator);
        }
        // The schedule the runner must follow: after every pass, continue while fewer passes
        // are done than planned_passes of the measured mean.
        std::uint32_t expected_passes = 0;
        std::uint64_t expected_ns = 0;
        std::uint64_t longest_ns = 0;
        while (expected_passes < k_scripted_passes) {
            expected_ns += script[expected_passes];
            longest_ns = std::max(longest_ns, script[expected_passes]);
            ++expected_passes;
            if (expected_passes >= planned_passes(k_budget_ns, expected_ns / expected_passes)) {
                break;
            }
        }
        ASSERT_LT(expected_passes, k_scripted_passes) << "the script must outlast the schedule";

        // Every seed reruns step 0, whose independent per-step count would otherwise accumulate.
        fixture.memory().reset_counts();
        pass_driver driver(fixture, script);
        recording_observer observer;
        const step_outcome outcome = fixture.run(step, 0, observer);

        EXPECT_FALSE(driver.script_failed());
        EXPECT_TRUE(outcome.completed);
        EXPECT_EQ(outcome.passes, expected_passes);
        EXPECT_EQ(outcome.actual_ns, expected_ns);
        EXPECT_LE(abs_diff(outcome.actual_ns, k_budget_ns), longest_ns)
            << "jittered passes end within one longest pass of the budget";
        expect_outcome_accounting(fixture, outcome, 0, k_budget_ns);
    }
}

TEST(StepRunnerScheduling, OversizedPassRunsAloneAndReportsItsOverrun) {
    constexpr std::uint32_t k_budget_ms = 10'000;
    constexpr std::uint64_t k_budget_ns = std::uint64_t{k_budget_ms} * k_ns_per_ms;
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    // Step 0 gets one 30 s pass; step 1's passes then last 1 s each.
    pass_driver driver(fixture, {30 * k_ns_per_s, k_ns_per_s});
    const rq_step_def oversized =
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0, k_budget_ms);
    const rq_step_def next =
        make_step(RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0, 5'000);

    recording_observer observer;
    const step_outcome outcome = fixture.run(oversized, 0, observer);

    EXPECT_TRUE(outcome.completed) << "the single planned pass finished";
    EXPECT_EQ(outcome.passes, 1u);
    EXPECT_EQ(outcome.actual_ns, 30 * k_ns_per_s);
    EXPECT_EQ(outcome.overrun_ns, 20 * k_ns_per_s);
    expect_outcome_accounting(fixture, outcome, 0, k_budget_ns);
    ASSERT_FALSE(observer.statuses().empty());
    const step_status& last = observer.statuses().back();
    EXPECT_EQ(last.passes_done, 1u);
    EXPECT_EQ(last.passes_planned, 1u);
    EXPECT_EQ(last.mean_pass_ns, 30 * k_ns_per_s);

    // Later steps keep their own budgets.
    recording_observer next_observer;
    const step_outcome after = fixture.run(next, 1, next_observer);
    EXPECT_FALSE(driver.script_failed());
    EXPECT_TRUE(after.completed);
    EXPECT_EQ(after.passes, 5u);
    EXPECT_EQ(after.actual_ns, 5 * k_ns_per_s);
    EXPECT_EQ(after.overrun_ns, 0u);
    expect_outcome_accounting(fixture, after, 1, 5'000 * k_ns_per_ms);
}

TEST(StepRunnerScheduling, CoverageBudgetNeverExtendsAStep) {
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {k_ns_per_s});
    // A 10,000 % floor, ten times what ten passes of one verification each can reach.
    constexpr std::uint32_t k_unreachable_bp = 1'000'000;
    const rq_step_def step = make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
                                       RQ_THREADS_ALL, 0, 10'000, k_unreachable_bp);
    recording_observer observer;
    const step_outcome outcome = fixture.run(step, 0, observer);

    EXPECT_FALSE(driver.script_failed());
    EXPECT_TRUE(outcome.completed);
    EXPECT_EQ(outcome.passes, 10u) << "time governs, so the step ends with the stable schedule";
    EXPECT_EQ(outcome.actual_ns, 10 * k_ns_per_s);
    EXPECT_LT(coverage_percent(outcome.verified_bytes, fixture.tested_bytes()) * 100.0,
              static_cast<double>(k_unreachable_bp));
    expect_outcome_accounting(fixture, outcome, 0, 10 * k_ns_per_s);
}

/* ---------------------------------------------------------------------------------------------
 * Observer contract
 * ------------------------------------------------------------------------------------------- */

TEST(StepRunnerObserver, ReportsTheScheduleOnlyAfterTheFirstPassAndTicksThroughHolds) {
    constexpr std::uint32_t k_hold_ms = 5'000;
    constexpr std::uint32_t k_budget_ms = 30'000;
    constexpr std::uint64_t k_budget_ns = std::uint64_t{k_budget_ms} * k_ns_per_ms;
    // Each pass: 1 s of scripted traffic plus the 5 s hold, so 6 s and five passes.
    constexpr std::uint64_t k_pass_ns = k_ns_per_s + std::uint64_t{k_hold_ms} * k_ns_per_ms;
    constexpr std::uint32_t k_passes = 5;
    constexpr std::uint32_t k_hold_slices = k_hold_ms / k_observer_tick_ms;
    constexpr std::uint32_t k_step_index = 2;
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {k_ns_per_s});
    const rq_step_def step = make_step(RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL,
                                       k_hold_ms, k_budget_ms);
    ASSERT_EQ(classify_step(step), step_kind::hold);
    recording_observer observer;

    const step_outcome outcome = fixture.run(step, k_step_index, observer);

    EXPECT_FALSE(driver.script_failed());
    EXPECT_TRUE(outcome.completed);
    EXPECT_EQ(outcome.passes, k_passes);
    EXPECT_EQ(outcome.actual_ns, k_passes * k_pass_ns);
    EXPECT_EQ(outcome.overrun_ns, 0u);
    // The holds passed in fake time only, one k_observer_tick_ms slice per wait.
    EXPECT_EQ(fixture.clock().waited_ms(), std::uint64_t{k_passes} * k_hold_ms);
    EXPECT_EQ(fixture.clock().wait_calls(), std::uint64_t{k_passes} * k_hold_slices);

    const std::vector<step_status>& statuses = observer.statuses();
    ASSERT_FALSE(statuses.empty());
    std::vector<std::uint32_t> ticks_in_pass(k_passes + 1, 0);
    std::vector<std::uint32_t> ticks_after_pass(k_passes + 1, 0);
    std::uint64_t previous_elapsed = 0;
    for (std::size_t i = 0; i < statuses.size(); ++i) {
        const step_status& s = statuses[i];
        SCOPED_TRACE("tick " + std::to_string(i) + ": pass " + std::to_string(s.pass_index) +
                     ", done " + std::to_string(s.passes_done));
        EXPECT_EQ(s.step_index, k_step_index);
        EXPECT_GE(s.step_elapsed_ns, previous_elapsed) << "step_elapsed_ns must never decrease";
        previous_elapsed = s.step_elapsed_ns;
        ASSERT_LE(s.passes_done, k_passes);
        if (s.passes_done == 0) {
            EXPECT_EQ(s.passes_planned, 0u) << "no schedule before the first pass completes";
            EXPECT_EQ(s.mean_pass_ns, 0u) << "no mean before the first pass completes";
        } else {
            EXPECT_EQ(s.mean_pass_ns, k_pass_ns);
            EXPECT_EQ(s.passes_planned, planned_passes(k_budget_ns, s.mean_pass_ns));
        }
        // The pass in progress, or, at the tick right after a completed pass, that pass.
        if (s.pass_index == s.passes_done) {
            ++ticks_in_pass[s.pass_index];
        } else {
            ASSERT_EQ(s.pass_index + 1, s.passes_done);
            ++ticks_after_pass[s.pass_index];
            EXPECT_EQ(s.step_elapsed_ns, std::uint64_t{s.passes_done} * k_pass_ns)
                << "the tick after a pass reports the time of its end";
        }
    }
    for (std::uint32_t pass = 0; pass < k_passes; ++pass) {
        EXPECT_EQ(ticks_after_pass[pass], 1u) << "exactly one tick follows pass " << pass;
        EXPECT_GE(ticks_in_pass[pass], k_hold_slices)
            << "pass " << pass << " must tick once per hold slice";
    }
    EXPECT_EQ(statuses.back().passes_done, k_passes);
    EXPECT_EQ(statuses.back().passes_planned, k_passes);
}

/* ---------------------------------------------------------------------------------------------
 * Time remaining
 * ------------------------------------------------------------------------------------------- */

/** The plan of the formula cases: three steps of 40, 50 and 60 s. */
const std::array<rq_step_def, 3>& remaining_plan() {
    static const std::array<rq_step_def, 3> plan = {
        make_step(RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0, 40'000),
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, RQ_THREADS_ALL, 0, 50'000),
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, RQ_THREADS_ALL, 0, 60'000),
    };
    return plan;
}

struct remaining_case {
    const char* name;
    std::uint32_t step_index;
    std::uint32_t passes_done;
    std::uint32_t passes_planned;
    std::uint64_t mean_pass_ns;
    std::uint64_t step_elapsed_ns;
    std::int64_t expected_ms;
};

void PrintTo(const remaining_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr remaining_case k_remaining_cases[] = {
    {"CalculatingUntilTheFirstStepsFirstPass", 0, 0, 0, 0, 0, -1},
    {"CalculatingIgnoresEveryOtherField", 0, 0, 7, 3 * k_ns_per_s, 12 * k_ns_per_s, -1},
    {"FirstStepAfterItsFirstPassUsesTheSchedule", 0, 1, 4, 10 * k_ns_per_s, 10 * k_ns_per_s,
     30'000 + 50'000 + 60'000},
    {"LaterStepBeforeItsFirstPassUsesItsBudget", 1, 0, 0, 0, 10 * k_ns_per_s, 40'000 + 60'000},
    {"LaterStepAfterItsFirstPassUsesTheSchedule", 1, 2, 5, 9 * k_ns_per_s, 20 * k_ns_per_s,
     25'000 + 60'000},
    {"ScheduleTermClampsAtZero", 1, 5, 5, 9 * k_ns_per_s, 50 * k_ns_per_s, 60'000},
    {"LastStepHasNoLaterTerm", 2, 1, 3, 20 * k_ns_per_s, 20 * k_ns_per_s, 40'000},
    // A first pass of 80 s against step 1's 50 s budget: while it runs, the budget term
    // reaches 0 and the estimate is low (20 s low at 60 s in) ...
    {"OversizedFirstPassIsLowWhileItRuns", 1, 0, 0, 0, 60 * k_ns_per_s, 60'000},
    // ... and once it ends, the term uses the real 80 s mean, so the estimate is exact again.
    {"OversizedStepUsesItsRealMeanOnceItEnds", 1, 1, 1, 80 * k_ns_per_s, 80 * k_ns_per_s, 60'000},
    {"TruncatesToWholeMilliseconds", 2, 1, 1, 1'999'999, 0, 1},
    {"PastTheLastStepIsZero", 3, 1, 1, k_ns_per_s, 0, 0},
    {"SaturatesInsteadOfWrapping", 2, 1, UINT32_MAX, UINT64_MAX, 0,
     static_cast<std::int64_t>(UINT64_MAX / k_ns_per_ms)},
};

class RemainingTimeFormula : public ::testing::TestWithParam<remaining_case> {};

TEST_P(RemainingTimeFormula, ComputesTheRunningTermPlusLaterBudgets) {
    const remaining_case& c = GetParam();
    remaining_inputs in{};
    in.steps = remaining_plan();
    in.step_index = c.step_index;
    in.passes_done = c.passes_done;
    in.passes_planned = c.passes_planned;
    in.mean_pass_ns = c.mean_pass_ns;
    in.step_elapsed_ns = c.step_elapsed_ns;
    EXPECT_EQ(compute_remaining_ms(in), c.expected_ms);
}

INSTANTIATE_TEST_SUITE_P(RemainingTimeCases, RemainingTimeFormula,
                         ::testing::ValuesIn(k_remaining_cases),
                         [](const ::testing::TestParamInfo<remaining_case>& info) {
                             return std::string(info.param.name);
                         });

TEST(RemainingTimeStable, MatchesTheActualRemainingTimeAtEveryTick) {
    runner_fixture fixture(harness_options{});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {k_ns_per_s});
    const std::array<rq_step_def, 2> plan = {
        make_step(RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0, 10'000),
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_DESCENDING, RQ_THREADS_ALL, 0, 5'000),
    };

    // Each tick's time is its step's start plus the tick's step_elapsed_ns: one clock reading,
    // so a worker's concurrent advance cannot split it.
    std::array<recording_observer, 2> observers;
    std::array<std::uint64_t, 2> step_start{};
    for (std::uint32_t index = 0; index < plan.size(); ++index) {
        step_start[index] = fixture.clock().now_ns();
        const step_outcome outcome = fixture.run(plan[index], index, observers[index]);
        EXPECT_TRUE(outcome.completed);
    }
    const std::uint64_t run_end = fixture.clock().now_ns();
    EXPECT_FALSE(driver.script_failed());
    ASSERT_EQ(run_end - step_start[0], 15 * k_ns_per_s);

    std::size_t estimates = 0;
    for (std::uint32_t index = 0; index < plan.size(); ++index) {
        for (const step_status& s : observers[index].statuses()) {
            remaining_inputs in{};
            in.steps = plan;
            in.step_index = s.step_index;
            in.passes_done = s.passes_done;
            in.passes_planned = s.passes_planned;
            in.mean_pass_ns = s.mean_pass_ns;
            in.step_elapsed_ns = s.step_elapsed_ns;
            const std::int64_t estimate = compute_remaining_ms(in);
            if (index == 0 && s.passes_done == 0) {
                EXPECT_EQ(estimate, -1) << "calculating until step 1's first pass completes";
                continue;
            }
            const std::uint64_t actual_ns = run_end - (step_start[index] + s.step_elapsed_ns);
            const std::int64_t actual_ms = static_cast<std::int64_t>(actual_ns / k_ns_per_ms);
            EXPECT_EQ(estimate, actual_ms) << "step " << index << ", pass " << s.pass_index
                                           << ", elapsed " << s.step_elapsed_ns << " ns";
            ++estimates;
        }
    }
    // One tick after each of the 10 + 5 passes at least.
    EXPECT_GE(estimates, 15u);
}

/* ---------------------------------------------------------------------------------------------
 * Flush before verification, from the recorded primitive sequence
 * ------------------------------------------------------------------------------------------- */

struct flush_case {
    const char* name;
    std::uint32_t pattern;
    std::uint32_t order;
    std::uint32_t threads;
    std::uint32_t hold_ms;
    /** Workers wanted; fewer when the host has fewer logical processors. */
    std::uint32_t workers;
    std::uint64_t region_bytes;
};

void PrintTo(const flush_case& c, std::ostream* os) {
    *os << c.name;
}

/**
 * Every phase kind, the windowed one in each access order, plus Half mode twice: on two workers
 * where the host has two logical processors, so the even worker also covers its idle partner's
 * slice, and on one worker on every host, the odd count whose last even worker has no partner.
 */
constexpr flush_case k_flush_cases[] = {
    {"WindowedRandomSequentialAscending", RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_ALL, 0, 1, k_flush_region_bytes},
    {"WindowedCheckerboardSequentialDescending", RQ_PATTERN_CHECKERBOARD,
     RQ_ORDER_SEQUENTIAL_DESCENDING, RQ_THREADS_ALL, 0, 1, k_flush_region_bytes},
    {"WindowedHighToggleBankStride", RQ_PATTERN_HIGH_TOGGLE, RQ_ORDER_BANK_STRIDE, RQ_THREADS_ALL,
     0, 1, k_flush_region_bytes},
    {"WindowedRandomRandomBlocks", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, 0, 1,
     k_flush_region_bytes},
    {"HoldRandomBlocksFiveSeconds", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL,
     5'000, 1, k_flush_region_bytes},
    {"MovingInversionWalking", RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_ALL, 0, 1, k_flush_region_bytes},
    {"MovingInversionRandom", RQ_PATTERN_MOVING_INVERSION_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_ALL, 0, 1, k_flush_region_bytes},
    {"TurnaroundRandom", RQ_PATTERN_RANDOM, RQ_ORDER_TURNAROUND, RQ_THREADS_ALL, 0, 1,
     k_flush_region_bytes},
    {"WindowedRandomSequentialHalfUpToTwoWorkers", RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_HALF, 0, 2, k_flush_region_bytes},
    {"WindowedRandomSequentialHalfOnOneWorker", RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     RQ_THREADS_HALF, 0, 1, k_flush_region_bytes},
};

class StepRunnerFlushRule : public ::testing::TestWithParam<flush_case> {};

TEST_P(StepRunnerFlushRule, EveryVerifiedLineFollowsFenceFlushFenceOnEveryPass) {
    const flush_case& c = GetParam();
    // A supported host may have a single logical processor; the case then runs on the workers it
    // has, and its Half step on one worker is the even worker without a partner.
    const std::uint32_t workers = static_cast<std::uint32_t>(
        std::min<std::size_t>(c.workers, enumerate_logical_processors().size()));
    SCOPED_TRACE(std::to_string(workers) + " of " + std::to_string(c.workers) +
                 " wanted worker(s)");
    runner_fixture fixture(harness_options{workers, c.region_bytes, true});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    // Three passes of 1 s plus the hold each, against a budget of exactly three.
    const std::uint32_t pass_ms =
        static_cast<std::uint32_t>(k_flush_pass_ns / k_ns_per_ms) + c.hold_ms;
    const rq_step_def step =
        make_step(c.pattern, c.order, c.threads, c.hold_ms, k_flush_passes * pass_ms);
    const step_kind kind = classify_step(step);
    SCOPED_TRACE(std::string(kind_name(kind)) + " step " + c.name);
    pass_driver driver(fixture, {k_flush_pass_ns});
    driver.inject_on_passes(injection_phase(kind), k_injection_passes);
    recording_observer observer;

    const step_outcome outcome = fixture.run(step, k_flush_step_index, observer);

    ASSERT_FALSE(fixture.memory().log_overflowed())
        << "a check over a truncated log proves nothing";
    EXPECT_FALSE(driver.script_failed());
    ASSERT_TRUE(outcome.completed);
    ASSERT_EQ(outcome.passes, k_flush_passes);
    ASSERT_EQ(driver.injected().size(), k_injection_passes.size())
        << "every injection pass must reach its final fence";

    // One worker covers every slice: worker 0, which in Half mode on two workers also covers
    // worker 1's slice.
    const std::vector<primitive_record> log = fixture.memory().log_for_worker(0);
    for (std::uint32_t idle = 1; idle < workers; ++idle) {
        EXPECT_TRUE(fixture.memory().log_for_worker(idle).empty())
            << "the idle Half-mode partner, worker " << idle << ", issued primitives";
    }

    // The rule for every verified line, on the first pass and on every later one, with exact
    // per-pass flush counts: nothing is flushed beyond the rule. Hold steps' sequences are also
    // bracketed by the time each hold actually ran.
    const std::uint64_t hold_ns = std::uint64_t{c.hold_ms} * k_ns_per_ms;
    const line_table lines(fixture.slices());
    flush_rule_checker checker(kind, lines, outcome.passes, hold_ns, fixture.holds());
    checker.check(log);
    EXPECT_EQ(checker.failure_count(), 0u) << checker.report();

    // Workers check the stop flag every 1 MiB, so no flush call may cover more than one work
    // group, however large its window or slice. The signatures below still hold exactly one
    // fence on each side of the flushes, so splitting adds no fence.
    const auto oversized = std::find_if(log.begin(), log.end(), [](const primitive_record& r) {
        return r.kind == primitive_kind::flush && r.bytes > k_work_group_bytes;
    });
    EXPECT_TRUE(oversized == log.end())
        << "flush longer than one work group: " << describe(*oversized);

    // The phase structure of every pass.
    for (std::uint32_t pass = 0; pass < outcome.passes; ++pass) {
        SCOPED_TRACE("pass " + std::to_string(pass));
        switch (kind) {
        case step_kind::turnaround:
            expect_turnaround_pass(log, pass, fixture.slices());
            break;
        case step_kind::moving_inversion:
            EXPECT_EQ(pass_signature(log, pass),
                      expected_signature(kind, c.order, fixture.slices()));
            expect_inversion_sweeps(log, pass, lines, fixture.tested_bytes());
            break;
        case step_kind::windowed:
            EXPECT_EQ(pass_signature(log, pass),
                      expected_signature(kind, c.order, fixture.slices()));
            if (c.order == RQ_ORDER_BANK_STRIDE) {
                expect_bank_stride_pass(log, pass, fixture.slices(), fixture.tested_bytes());
            }
            break;
        case step_kind::hold:
            EXPECT_EQ(pass_signature(log, pass),
                      expected_signature(kind, c.order, fixture.slices()));
            break;
        }
    }

    // Each injected mismatch was confirmed by exactly one flushed re-read, which no verified
    // byte counts.
    expect_rereads(log, driver.injected());
    EXPECT_EQ(fixture.errors().true_total(), k_injection_passes.size());
    EXPECT_EQ(fixture.errors().step_errors(k_flush_step_index), k_injection_passes.size());
    const std::uint64_t expected_verified = std::uint64_t{outcome.passes} * fixture.tested_bytes() *
                                            expectations_for(kind).verifies_per_pass;
    EXPECT_EQ(logged_verified_bytes(log), expected_verified);
    EXPECT_EQ(fixture.memory().verified_bytes_for_step(k_flush_step_index), expected_verified);
    EXPECT_EQ(outcome.verified_bytes, expected_verified);

    if (kind == step_kind::hold) {
        // The holds passed in fake time only.
        EXPECT_EQ(fixture.clock().waited_ms(), std::uint64_t{outcome.passes} * c.hold_ms);
        // One contiguous window per pass, each exactly the hold: the windows the checker
        // bracketed the sequences against are the holds themselves.
        const std::span<const hold_window> holds = fixture.holds();
        ASSERT_EQ(holds.size(), outcome.passes);
        for (std::size_t pass = 0; pass < holds.size(); ++pass) {
            EXPECT_EQ(holds[pass].end_ns - holds[pass].begin_ns, hold_ns)
                << "hold of pass " << pass;
        }
    }
}

INSTANTIATE_TEST_SUITE_P(StepRunnerPhaseKinds, StepRunnerFlushRule,
                         ::testing::ValuesIn(k_flush_cases),
                         [](const ::testing::TestParamInfo<flush_case>& info) {
                             return std::string(info.param.name);
                         });

/* ---------------------------------------------------------------------------------------------
 * Stop granularity of the window and slice flushes
 * ------------------------------------------------------------------------------------------- */

/**
 * The counting layer's observer of one stop test: requests a user stop on worker 0's
 * occurrence-th record of one primitive kind in one phase of pass 0, and keeps that record's seq.
 *
 * The observer runs after the call was forwarded, so the stop lands as if it had arrived while
 * that call was still executing: from the worker's side, the record is the work in flight when
 * the stop arrived. Installed on construction and removed on destruction. The tests run one
 * worker, so every member is used only on its thread while a step runs, or by the test after.
 */
class stop_on_record {
public:
    stop_on_record(runner_fixture& fixture, primitive_kind kind, phase_kind phase,
                   std::uint32_t occurrence)
        : memory_(fixture.memory()), stop_(fixture.stop()), kind_(kind), phase_(phase),
          occurrence_(occurrence) {
        memory_.set_observer([this](const primitive_record& record) { on_primitive(record); });
    }

    ~stop_on_record() { memory_.set_observer({}); }

    stop_on_record(const stop_on_record&) = delete;
    stop_on_record& operator=(const stop_on_record&) = delete;
    stop_on_record(stop_on_record&&) = delete;
    stop_on_record& operator=(stop_on_record&&) = delete;

    /** Whether the triggering record was issued. Read after the run. */
    bool triggered() const noexcept { return triggered_; }

    /** seq of the triggering record. Meaningful once triggered(). */
    std::uint64_t trigger_seq() const noexcept { return trigger_seq_; }

private:
    void on_primitive(const primitive_record& record) noexcept {
        if (triggered_ || record.worker != 0 || record.ctx.pass_index != 0 ||
            record.kind != kind_ || record.ctx.phase != phase_) {
            return;
        }
        if (++seen_ == occurrence_) {
            triggered_ = true;
            trigger_seq_ = record.seq;
            stop_.request(RQ_END_USER_STOP);
        }
    }

    counting_memory_access& memory_;
    stop_signal& stop_;
    const primitive_kind kind_;
    const phase_kind phase_;
    const std::uint32_t occurrence_;
    std::uint32_t seen_ = 0;
    bool triggered_ = false;
    std::uint64_t trigger_seq_ = 0;
};

struct flush_stop_case {
    const char* name;
    std::uint32_t pattern;
    std::uint32_t order;
    std::uint32_t hold_ms;
    /** The record of pass 0 whose observer requests the stop: kind, phase and 1-based count. */
    primitive_kind trigger_kind;
    phase_kind trigger_phase;
    std::uint32_t trigger_occurrence;
    /** The phase whose fence, flush, fence the stop cuts short. */
    phase_kind cut_phase;
    /**
     * Flush bytes the worker may issue from the triggering record on, that record included: one
     * work group when the stop lands during a flush, none when it lands before the first.
     */
    std::uint64_t max_flushed_from_stop;
    /** Whole-slice verifications completed before the stop: the coverage the step keeps. */
    std::uint32_t verified_slices;
};

void PrintTo(const flush_stop_case& c, std::ostream* os) {
    *os << c.name;
}

/** The final 1 MiB stream of a SequentialAscending window of the 16 MiB, one-worker region. */
constexpr std::uint32_t k_final_write_group =
    static_cast<std::uint32_t>(k_flush_region_bytes / k_work_group_bytes);

/**
 * Every flush of a window or slice, each stopped during its first 1 MiB group, plus a windowed
 * stop during the final write group, which must end the window before any flush; the windowed
 * cases also in the BankStride order, whose windows are issued as strided calls.
 */
constexpr flush_stop_case k_flush_stop_cases[] = {
    {"WindowedVerifyFlush", RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, 0,
     primitive_kind::flush, phase_kind::verify, 1, phase_kind::verify, k_work_group_bytes, 0},
    {"WindowedFinalWriteGroup", RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, 0,
     primitive_kind::stream_fill, phase_kind::write, k_final_write_group, phase_kind::verify, 0, 0},
    {"BankStrideVerifyFlush", RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, 0, primitive_kind::flush,
     phase_kind::verify, 1, phase_kind::verify, k_work_group_bytes, 0},
    {"BankStrideFinalWriteGroup", RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, 0,
     primitive_kind::stream_fill_strided, phase_kind::write, k_final_write_group,
     phase_kind::verify, 0, 0},
    {"HoldPreHoldFlush", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, 5'000, primitive_kind::flush,
     phase_kind::pre_hold_flush, 1, phase_kind::pre_hold_flush, k_work_group_bytes, 0},
    {"HoldVerifyFlushAfterTheHold", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, 5'000,
     primitive_kind::flush, phase_kind::verify, 1, phase_kind::verify, k_work_group_bytes, 0},
    {"MovingInversionBaseWriteFlush", RQ_PATTERN_MOVING_INVERSION_WALKING,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0, primitive_kind::flush, phase_kind::write, 1,
     phase_kind::write, k_work_group_bytes, 0},
    {"MovingInversionSweepFlush", RQ_PATTERN_MOVING_INVERSION_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING,
     0, primitive_kind::flush, phase_kind::sweep, 1, phase_kind::sweep, k_work_group_bytes, 1},
};

class StepRunnerFlushStop : public ::testing::TestWithParam<flush_stop_case> {};

TEST_P(StepRunnerFlushStop, EndsThePhaseWithinOneWorkGroupAndVerifiesNothingAfter) {
    const flush_stop_case& c = GetParam();
    // One worker, one 16 MiB chunk and window: a flush issued whole would run 16 work groups
    // past the stop.
    runner_fixture fixture(harness_options{1, k_flush_region_bytes, true});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    const std::uint32_t pass_ms =
        static_cast<std::uint32_t>(k_flush_pass_ns / k_ns_per_ms) + c.hold_ms;
    const rq_step_def step =
        make_step(c.pattern, c.order, RQ_THREADS_ALL, c.hold_ms, k_flush_passes * pass_ms);
    stop_on_record trigger(fixture, c.trigger_kind, c.trigger_phase, c.trigger_occurrence);
    recording_observer observer;

    const step_outcome outcome = fixture.run(step, k_flush_step_index, observer);

    ASSERT_FALSE(fixture.memory().log_overflowed())
        << "a check over a truncated log proves nothing";
    ASSERT_TRUE(trigger.triggered()) << "pass 0 never issued the triggering record";
    EXPECT_FALSE(outcome.completed);
    EXPECT_EQ(outcome.passes, 0u);

    const std::vector<primitive_record> log = fixture.memory().log_for_worker(0);
    ASSERT_FALSE(log.empty());
    std::uint64_t flushed_from_stop = 0;
    std::uint32_t cut_phase_fences = 0;
    for (const primitive_record& record : log) {
        EXPECT_EQ(record.ctx.pass_index, 0u) << "a pass began after the stop: " << describe(record);
        if (record.kind == primitive_kind::fence && record.ctx.phase == c.cut_phase) {
            ++cut_phase_fences;
        }
        if (record.seq < trigger.trigger_seq()) {
            continue;
        }
        if (record.kind == primitive_kind::flush) {
            flushed_from_stop += record.bytes;
        }
        EXPECT_FALSE(record.kind == primitive_kind::verify ||
                     record.kind == primitive_kind::verify_and_stream ||
                     record.kind == primitive_kind::verify_strided)
            << "verification after the stop: " << describe(record);
    }
    EXPECT_LE(flushed_from_stop, c.max_flushed_from_stop);

    // The closing fence ends the phase on the stop too, so the flushes already issued are
    // ordered before it is abandoned; no fence is added inside the flush.
    const primitive_record& last = log.back();
    EXPECT_EQ(last.kind, primitive_kind::fence) << describe(last);
    EXPECT_EQ(last.ctx.phase, c.cut_phase) << describe(last);
    EXPECT_EQ(cut_phase_fences, 2u);

    // The window or slice whose flush was cut short is never verified, so it adds no coverage.
    const std::uint64_t expected_verified =
        std::uint64_t{c.verified_slices} * fixture.tested_bytes();
    EXPECT_EQ(logged_verified_bytes(log), expected_verified);
    EXPECT_EQ(fixture.memory().verified_bytes_for_step(k_flush_step_index), expected_verified);
    EXPECT_EQ(outcome.verified_bytes, expected_verified);
}

INSTANTIATE_TEST_SUITE_P(StepRunnerFlushStops, StepRunnerFlushStop,
                         ::testing::ValuesIn(k_flush_stop_cases),
                         [](const ::testing::TestParamInfo<flush_stop_case>& info) {
                             return std::string(info.param.name);
                         });

// A BankStride window verifies one 1 MiB work group per strided call and checks the stop flag
// before each: a stop that lands during a group's verification keeps that group's bytes and
// verifies nothing more.
TEST(StepRunnerBankStrideStop, EndsVerificationAfterTheWorkGroupInFlight) {
    constexpr std::uint32_t stop_group = 3;
    runner_fixture fixture(harness_options{1, k_flush_region_bytes, true});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    const rq_step_def step =
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_BANK_STRIDE, RQ_THREADS_ALL, 0,
                  k_flush_passes * static_cast<std::uint32_t>(k_flush_pass_ns / k_ns_per_ms));
    stop_on_record trigger(fixture, primitive_kind::verify_strided, phase_kind::verify, stop_group);
    recording_observer observer;

    const step_outcome outcome = fixture.run(step, k_flush_step_index, observer);

    ASSERT_FALSE(fixture.memory().log_overflowed())
        << "a check over a truncated log proves nothing";
    ASSERT_TRUE(trigger.triggered()) << "pass 0 never issued the triggering verification";
    EXPECT_FALSE(outcome.completed);
    EXPECT_EQ(outcome.passes, 0u);
    const std::vector<primitive_record> log = fixture.memory().log_for_worker(0);
    ASSERT_FALSE(log.empty());
    EXPECT_EQ(log.back().seq, trigger.trigger_seq())
        << "a primitive followed the stop: " << describe(log.back());
    const std::uint64_t expected_verified = std::uint64_t{stop_group} * k_work_group_bytes;
    EXPECT_EQ(logged_verified_bytes(log), expected_verified);
    EXPECT_EQ(fixture.memory().verified_bytes_for_step(k_flush_step_index), expected_verified);
    EXPECT_EQ(outcome.verified_bytes, expected_verified);
}

/* ---------------------------------------------------------------------------------------------
 * The checker itself: a sequence that breaks the rule must be reported, with what is missing
 * ------------------------------------------------------------------------------------------- */

enum class mutation_action { drop, duplicate };

struct mutation_case {
    const char* name;
    /** The occurrence-th verify-phase primitive of this kind in pass 1 is mutated. */
    primitive_kind target;
    std::uint32_t occurrence;
    mutation_action action;
    /** Text the checker's report must contain. */
    const char* reported;
};

void PrintTo(const mutation_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr mutation_case k_mutation_cases[] = {
    {"DroppedFirstFence", primitive_kind::fence, 0, mutation_action::drop,
     "missing the first fence, between the line's last access and its flush"},
    {"DroppedFlush", primitive_kind::flush, 0, mutation_action::drop,
     "missing a flush covering the line"},
    {"DroppedSecondFence", primitive_kind::fence, 1, mutation_action::drop,
     "missing the second fence, between the flush and the verification"},
    {"DuplicatedFlush", primitive_kind::flush, 0, mutation_action::duplicate,
     "2 flushes, expected exactly 1"},
};

class StepRunnerFlushChecker : public ::testing::TestWithParam<mutation_case> {};

TEST_P(StepRunnerFlushChecker, ReportsWhatAMutatedSequenceIsMissing) {
    const mutation_case& c = GetParam();
    runner_fixture fixture(harness_options{1, k_schedule_region_bytes, true});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {k_flush_pass_ns});
    const rq_step_def step =
        make_step(RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, RQ_THREADS_ALL, 0,
                  k_flush_passes * static_cast<std::uint32_t>(k_flush_pass_ns / k_ns_per_ms));
    recording_observer observer;
    const step_outcome outcome = fixture.run(step, 0, observer);
    ASSERT_TRUE(outcome.completed);
    ASSERT_EQ(outcome.passes, k_flush_passes);
    ASSERT_FALSE(fixture.memory().log_overflowed());
    std::vector<primitive_record> log = fixture.memory().log_for_worker(0);
    const line_table lines(fixture.slices());

    flush_rule_checker intact(step_kind::windowed, lines, outcome.passes);
    intact.check(log);
    ASSERT_EQ(intact.failure_count(), 0u) << intact.report();

    std::uint32_t seen = 0;
    const auto target = std::find_if(log.begin(), log.end(), [&](const primitive_record& r) {
        return r.ctx.pass_index == 1 && r.ctx.phase == phase_kind::verify && r.kind == c.target &&
               seen++ == c.occurrence;
    });
    ASSERT_TRUE(target != log.end()) << "pass 1 has no such verify-phase primitive";
    if (c.action == mutation_action::drop) {
        log.erase(target);
    } else {
        const primitive_record copy = *target;
        log.insert(target, copy);
    }

    flush_rule_checker mutated(step_kind::windowed, lines, outcome.passes);
    mutated.check(log);
    EXPECT_GT(mutated.failure_count(), 0u);
    const std::string report = mutated.report();
    EXPECT_NE(report.find("windowed pass 1 worker 0 line 0x"), std::string::npos) << report;
    EXPECT_NE(report.find(c.reported), std::string::npos) << report;
}

INSTANTIATE_TEST_SUITE_P(StepRunnerMutations, StepRunnerFlushChecker,
                         ::testing::ValuesIn(k_mutation_cases),
                         [](const ::testing::TestParamInfo<mutation_case>& info) {
                             return std::string(info.param.name);
                         });

/** Which of pass 1's hold sequences is moved across the hold, in time only. */
enum class hold_move {
    /** The pre-hold stream's fence, flush, fence, as a runner issuing them after the wait. */
    pre_hold_flush_after_the_hold,
    /** The post-hold fence, flush, fence, as a runner issuing them before the wait. */
    post_hold_flush_before_the_hold,
};

struct hold_mutation_case {
    const char* name;
    hold_move move;
    /** Text the checker's report must contain. */
    const char* reported;
};

void PrintTo(const hold_mutation_case& c, std::ostream* os) {
    *os << c.name;
}

constexpr hold_mutation_case k_hold_mutation_cases[] = {
    {"PreHoldFlushAfterTheHold", hold_move::pre_hold_flush_after_the_hold,
     "expected the pre-hold closing fence to complete before the hold"},
    {"PostHoldFlushBeforeTheHold", hold_move::post_hold_flush_before_the_hold,
     "expected the post-hold sequence to start after the hold"},
};

class StepRunnerHoldBracketChecker : public ::testing::TestWithParam<hold_mutation_case> {};

// A flush moved across the wait keeps the log's order and counts, so only the clock brackets
// can show that dirty cached data could have outlived the start of the hold.
TEST_P(StepRunnerHoldBracketChecker, ReportsAFlushMovedAcrossTheHold) {
    const hold_mutation_case& c = GetParam();
    constexpr std::uint32_t k_hold_ms = 5'000;
    constexpr std::uint64_t k_hold_ns = std::uint64_t{k_hold_ms} * k_ns_per_ms;
    runner_fixture fixture(harness_options{1, k_schedule_region_bytes, true});
    ASSERT_TRUE(fixture.ready()) << fixture.setup_error();
    pass_driver driver(fixture, {k_flush_pass_ns});
    const rq_step_def step = make_step(
        RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, RQ_THREADS_ALL, k_hold_ms,
        k_flush_passes * (static_cast<std::uint32_t>(k_flush_pass_ns / k_ns_per_ms) + k_hold_ms));
    ASSERT_EQ(classify_step(step), step_kind::hold);
    recording_observer observer;
    const step_outcome outcome = fixture.run(step, 0, observer);
    EXPECT_FALSE(driver.script_failed());
    ASSERT_TRUE(outcome.completed);
    ASSERT_EQ(outcome.passes, k_flush_passes);
    ASSERT_FALSE(fixture.memory().log_overflowed());
    const std::span<const hold_window> holds = fixture.holds();
    ASSERT_EQ(holds.size(), outcome.passes);
    const std::vector<primitive_record> log = fixture.memory().log_for_worker(0);
    const line_table lines(fixture.slices());

    flush_rule_checker intact(step_kind::hold, lines, outcome.passes, k_hold_ns, holds);
    intact.check(log);
    ASSERT_EQ(intact.failure_count(), 0u) << intact.report();

    // Pass 1's records keep their places; only their clock readings become what a runner that
    // issued them on the other side of the wait would have recorded.
    std::vector<primitive_record> moved_log = log;
    std::size_t moved = 0;
    bool before_first_verification = true;
    for (primitive_record& record : moved_log) {
        if (record.ctx.pass_index != 1) {
            continue;
        }
        if (c.move == hold_move::pre_hold_flush_after_the_hold) {
            if (record.ctx.phase == phase_kind::pre_hold_flush) {
                record.clock_ns = holds[1].end_ns;
                record.clock_after_ns = holds[1].end_ns;
                ++moved;
            }
            continue;
        }
        if (record.kind == primitive_kind::verify) {
            before_first_verification = false;
        }
        if (before_first_verification && record.ctx.phase == phase_kind::verify &&
            (record.kind == primitive_kind::fence || record.kind == primitive_kind::flush)) {
            record.clock_ns = holds[1].begin_ns;
            record.clock_after_ns = holds[1].begin_ns;
            ++moved;
        }
    }
    ASSERT_GT(moved, 0u) << "pass 1 has no record of the moved sequence";
    EXPECT_EQ(pass_signature(moved_log, 1), pass_signature(log, 1))
        << "the move must be invisible to order-only evidence";

    flush_rule_checker mutated(step_kind::hold, lines, outcome.passes, k_hold_ns, holds);
    mutated.check(moved_log);
    EXPECT_GT(mutated.failure_count(), 0u);
    const std::string report = mutated.report();
    EXPECT_NE(report.find("hold pass 1 worker 0 record #"), std::string::npos) << report;
    EXPECT_NE(report.find(c.reported), std::string::npos) << report;
    EXPECT_EQ(report.find("hold pass 0 "), std::string::npos) << report;
    EXPECT_EQ(report.find("hold pass 2 "), std::string::npos) << report;
}

INSTANTIATE_TEST_SUITE_P(StepRunnerHoldMutations, StepRunnerHoldBracketChecker,
                         ::testing::ValuesIn(k_hold_mutation_cases),
                         [](const ::testing::TestParamInfo<hold_mutation_case>& info) {
                             return std::string(info.param.name);
                         });

}  // namespace
}  // namespace rq::engine::test
