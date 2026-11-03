/**
 * @file fault_injecting_memory_access.h
 * @brief The fault-injecting IMemoryAccess layer: corrupts words just before their verification
 *        and logs the ground truth of every corruption.
 *
 * Test-only: compiled into engine.tests.exe and ramquick_engine_test.dll, never into
 * ramquick_engine.dll. The AVX kernel units never include it, so it may use the STL freely.
 *
 * rq_test_session_create builds fault-injecting -> counting -> real for every test session, kind
 * RQ_FAULT_NONE included, so rq_test_get_injection_log and rq_test_get_independent_verified_bytes
 * work on every test session. test_fault_injection.cpp and test_error_collector.cpp build the
 * same stack directly through session_dependencies::wrap_memory_access. The tests then match each
 * logged injection whose verification ran to exactly one reported rq_error_record by word
 * address, bit mask, step and pass, as multisets, with nothing unmatched on either side.
 *
 * @par Survivable injection points
 * A corruption is applied only inside verify, verify_strided and verify_and_stream, immediately
 * before the call is forwarded to the inner layer. The step runner issues those calls after the
 * range's last write and after its fence, flush and fence, so nothing can overwrite a
 * corruption before the forwarded verification checks it. A verify_strided call is offered only
 * the targets on the lines it visits: the rest of its span is verified by the window's other
 * strided calls, each of which injects its own lines' targets. That one point covers every
 * injection point of the verification plan:
 * - windowed steps: a window's verify or BankStride verify_strided, after its streamed write and
 *   fence;
 * - hold steps: the slice's verify, after the slice write and the hold;
 * - moving inversion: each sweep's verify_and_stream, between the values the base write or the
 *   previous sweep streamed and this sweep's loads;
 * - turnaround: block i - 4's verify, after that block's write and before its own verification;
 * - delay-dependent faults: the same point, just before the verification load, gated on age.
 *
 * stream_fill, stream_fill_strided, fence, flush and reread_word forward unchanged: a store
 * anywhere else could be overwritten before it is checked. The error collector's flushed re-read
 * therefore sees the stored wrong value, and a single-bit flip reports RQ_REREAD_REPEATS_ACTUAL.
 * With a hold_clock attached, fence also counts the pre-hold flush's fences for that clock
 * before forwarding, which stores nothing.
 *
 * @par Fault kinds
 * Targets are logical region byte offsets (address - access_context::region_base), so they do
 * not depend on where Windows placed each chunk. They are a stateless function of seed, step and
 * pass, so one (step, pass) always selects the same words for a given seed. Within one
 * (step, pass) a word is targeted at most once, so one verification of a word yields at most one
 * log entry and one error record.
 * - RQ_FAULT_NONE: pass-through, with no store and no log entry. Its verifications take no
 *   lock; with a hold_clock attached, its pre-hold fences are still counted, so a test that
 *   injects nothing can still be told when a hold begins.
 * - RQ_FAULT_SINGLE_BIT_FLIP: up to per_pass_count distinct words per (step, pass), spread over
 *   [region_offset, region_offset + region_length), each with bit `bit` inverted from its
 *   expected value, or a seeded random bit per target when bit is RQ_FAULT_RANDOM_BIT.
 * - RQ_FAULT_STUCK_BIT: the single word at region_offset, its bit `bit` forced to
 *   (stuck_value & 1) at every verification of that word; RQ_FAULT_RANDOM_BIT selects one seeded
 *   bit for the whole run. Stored and logged only where the expected bit differs, because only
 *   those verifications can mismatch. per_pass_count is ignored.
 * - RQ_FAULT_ADDRESS_REGION: as the single-bit flip, every target inside
 *   [region_offset, region_offset + region_length). The tests use a contiguous 1/32 of the
 *   region and a random bit.
 * - RQ_FAULT_DELAY_DEPENDENT: the single-bit-flip targets, injected only where the data's age
 *   reaches delay_threshold_ms x 10^6. With a hold_clock attached, as in every session of
 *   rq_test_session_create, the age is hold_clock::controlled_age_ns(ctx.write_time_ns,
 *   clock.now_ns()): the configured durations of the holds that began after the write, so only
 *   verifications after a hold (RQ_ACCESS_VERIFY_AFTER_HOLD) see an age above 0, however long a
 *   descheduled worker takes between a window's write and its verification. Without one, as in
 *   the native tests on fake_clock, it is clock.now_ns() - ctx.write_time_ns, which only fake
 *   time moves. The gate is deliberately time-based only, so the tests' assertion that reports
 *   appear only after holds also checks the runner's write_time_ns stamps.
 *
 * The stored value is expected_value(expected, word index) (core/patterns/patterns.h) with the
 * target bit inverted, so the word differs from its expectation in exactly that bit. Targets at
 * or beyond the bytes the engine finally tests are never verified, so they are never injected or
 * logged.
 *
 * @par Logging
 * Each injection is logged before its store, so a corruption is never stored unless it is
 * logged. The rq_injected_fault entry holds:
 * - word_offset = word address - ctx.region_base, the logical region byte offset, equal to the
 *   reported rq_error_record.region_offset;
 * - bit_mask = 1ull << bit, and bit;
 * - step_index and pass_index from ctx;
 * - access_type = ctx.access_type;
 * - verified_opportunity = 0 and reserved0 = 0;
 * - address = the word's virtual address, the pointer the corruption is stored through, which
 *   the error collector reports as rq_error_record.address;
 * - injected_ns = clock.now_ns(), read once per call before its entries are logged and its
 *   corruptions stored, so it precedes the forwarded verification that confirms them.
 *
 * verified_opportunity becomes 1 once the forwarded call for that range has returned. The kernel
 * verifies the whole range, or every visited line, in one call, so every logged injection of a
 * completed run is verified.
 * 0 survives only when the forwarded call throws or the process dies inside it, which keeps the
 * ABI field honest.
 *
 * @par Caps
 * spec.max_injections caps the logged injections of the whole run, across all workers; 0 means
 * none. Candidates beyond the cap are neither stored nor logged. Moving inversion verifies twice
 * per pass, so a target can be injected, and logged, twice in one pass, once per sweep; the tests
 * match by multiset. Once the cap is spent, verify, verify_strided and verify_and_stream forward
 * at once, with no clock read, target lookup or allocation, so the rest of a long run builds no
 * target set.
 *
 * spec.injected_passes_per_step caps the passes of each step that inject: a verification whose
 * ctx.pass_index is at or above a nonzero quota forwards at once, before the cap is consulted,
 * with no clock read, target lookup or allocation. A step's corruptions are then at most quota x
 * per_pass_count per sweep (one stuck word for RQ_FAULT_STUCK_BIT), twice that for moving
 * inversion, so a test bounds its expected reports from the plan and keeps max_injections out of
 * reach instead of having it spent, in time order, before the last step.
 *
 * @par Target sets
 * A (step, pass)'s targets are built on its first lookup and shared by every worker. The engine
 * issues (step, pass) keys in ascending order, all verifications of a pass ending at a phase
 * barrier before the next pass's begin, so the layer's table keeps only the newest pass's set,
 * two at most when a caller goes back to an older key, and each worker's lookup cache keeps at
 * most its own last set alive. Retained memory is therefore bounded by the worker count, not by
 * the passes run. A reclaimed set asked for again is rebuilt identically, being a function of
 * seed, step and pass alone. retained_target_set_count() and built_target_set_count() expose
 * this to the tests.
 *
 * @par Threading
 * Every worker calls concurrently on disjoint ranges. The log is guarded by a mutex and the
 * counters are atomics, but no lock is held across a forwarded call: the error collector's sink
 * re-enters reread_word on this layer from inside the inner verification, on the same thread.
 *
 * @par Stress bound
 * The layer adds one plain store per injected word, at a verification opportunity the engine
 * already has. It adds no loop, no flush-and-reload, no repeated access to a small address set
 * and no memory traffic beyond those stores, so fault-injection runs keep the stress bound's
 * design limits. That is a property of the design, not a measurement.
 *
 * @par Exceptions
 * As in IMemoryAccess, only reread_word is noexcept: stream_fill, stream_fill_strided, fence,
 * flush, verify, verify_strided and verify_and_stream propagate whatever an inner IMemoryAccess
 * implementation throws, and the worker thread entry points turn that into a stop with
 * RQ_END_INTERNAL_FAULT. In the standard stacks the inner layer is counting_memory_access, whose
 * eight overrides are noexcept and which contains its own log-allocation failure by setting
 * log_overflowed() instead of throwing, so no primitive exception reaches this layer from it:
 * an exception escaping its inner layer or its observer calls std::terminate instead. When this
 * layer cannot allocate its own bookkeeping, a target set or a log entry, it skips injection for
 * that call and still forwards it.
 */
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <vector>

#include "core/patterns/patterns.h"
#include "core/kernels/kernels.h"
#include "core/memory/memory_access.h"
#include "core/clock.h"
#include "test_dll/ramquick_engine_test.h"
// Not used by the declarations: included so that every caller has normalized(), which the
// constructor's spec must already have passed through.
#include "fakes/fault_spec.h"

namespace rq::engine::test {

/** Closes a hold-entered event handle; the deleter of hold_event_handle. */
struct hold_event_closer {
    void operator()(HANDLE handle) const noexcept {
        close_handle_checked(handle, L"hold-entered event");
    }
};

/**
 * Owns a duplicate of rq_fault_spec.hold_entered_event, so it is closed on every path: held by
 * rq_test_session_create from the duplication until hold_clock takes it over, then by the clock.
 */
using hold_event_handle = std::unique_ptr<void, hold_event_closer>;

/**
 * The controlled clock of every test session: real time for the engine, a hold-only timeline for
 * the delay-dependent fault's age, and the hold-entry notification of
 * rq_fault_spec.hold_entered_event.
 *
 * now_ns() and the wait of wait_for_stop() are the base clock's, so pass scheduling, budgets, the
 * hold waits themselves, time_since_write_us, the progress cadence and the stop bound all run on
 * real time unchanged. The clock adds a record of hold entries:
 * - The fault layer reports each fence of a pre-hold flush through note_pre_hold_fence. Every
 *   slice gets exactly two per hold pass (fence, flush of the slice, fence), in All and Half mode
 *   alike, from the active worker that covers it, and a stop that cuts the slice's write leaves
 *   it none. When one (step, pass) has counted twice the slice count, every participating worker
 *   has written its slices and fenced their flush, and the clock is armed.
 * - The first wait_for_stop after arming is the coordinator's hold wait: the hold write's phase
 *   barrier lies between the last fence and it, and the engine's only other wait, the spacing of
 *   error batches, runs between steps. That call disarms the clock and, unless a stop has been
 *   requested, records the entry at the base clock's reading with the step's configured hold as
 *   its advance, then signals the hold-entered event. A hold that a stop precedes never begins,
 *   so nothing is recorded or signalled for it.
 * - controlled_age_ns(write_ns, now_ns) sums the advances of the entries recorded in
 *   (write_ns, now_ns]. Data verified after a hold that began after its write ages by exactly
 *   the configured hold, and data verified with no hold in between ages 0, however the workers
 *   were scheduled. The delay-dependent class's "reported only after holds" is then a property
 *   of the plan rather than of host load, while its 2000 ms threshold still compares with a hold
 *   the run really waited.
 *
 * Thread-safe: workers call note_pre_hold_fence and controlled_age_ns concurrently, the
 * coordinator calls wait_for_stop, and tests read hold_entry_count. Neither copyable nor movable:
 * the session owns it through std::shared_ptr<IClock>, and the fault layer refers to it for the
 * session's lifetime.
 */
class hold_clock final : public IClock {
public:
    /**
     * @param base The real clock: qpc_clock in the test DLL, fake_clock in the native tests.
     *        Shared, so it lives as long as this clock. Must not be null.
     * @param slice_count The session's slice count, rq_session_config.worker_count; at least 1.
     * @param hold_ms_by_step Each step's configured hold in milliseconds in plan order, 0 for a
     *        step without one. An entry armed for a step beyond it advances by 0.
     * @param hold_entered The event to signal at each hold entry, owned from here on and closed
     *        by the destructor, or empty for none.
     * @throws std::invalid_argument when base is null or slice_count is 0.
     * @throws std::bad_alloc when the timeline cannot be reserved.
     */
    hold_clock(std::shared_ptr<IClock> base, std::uint32_t slice_count,
               std::vector<std::uint32_t> hold_ms_by_step, hold_event_handle hold_entered);

    /** Closes the hold-entered event, if any. No call may be in flight. */
    ~hold_clock() override;

    hold_clock(const hold_clock&) = delete;
    hold_clock& operator=(const hold_clock&) = delete;
    hold_clock(hold_clock&&) = delete;
    hold_clock& operator=(hold_clock&&) = delete;

    /** The base clock's reading, unchanged. */
    std::uint64_t now_ns() const noexcept override;

    /**
     * Records and signals a hold entry when the clock is armed and no stop has been requested
     * (see the class comment), then waits on the base clock.
     *
     * @return The base clock's wait result.
     * @throws std::bad_alloc when the timeline cannot grow, std::system_error when SetEvent
     *         fails, and whatever the base wait throws; the coordinator ends the run with
     *         RQ_END_INTERNAL_FAULT on each.
     */
    bool wait_for_stop(const stop_signal& signal, std::uint64_t timeout_ms) override;

    /**
     * Counts one fence of the pre-hold flush of (step_index, pass_index), and arms the clock
     * when the count reaches twice the slice count. Counting restarts at 1 when another
     * (step, pass) arrives: a hold pass's pre-hold fences all fall inside its write phase,
     * between two barriers, so keys never interleave. Allocates nothing.
     *
     * @throws std::system_error when the counting mutex cannot be locked.
     */
    void note_pre_hold_fence(std::uint32_t step_index, std::uint32_t pass_index);

    /**
     * The controlled age of data written at write_ns and verified at now_ns: the sum, saturating
     * at UINT64_MAX, of the configured holds of the entries recorded at times in
     * (write_ns, now_ns]. Read without a lock when no entry follows write_ns, the case of every
     * verification without a hold in between, and otherwise under a shared lock.
     */
    std::uint64_t controlled_age_ns(std::uint64_t write_ns, std::uint64_t now_ns) const;

    /** Hold entries recorded so far. Lock-free. */
    std::uint64_t hold_entry_count() const noexcept;

private:
    /** One recorded hold: when it began on the base clock, and its configured duration. */
    struct hold_entry {
        std::uint64_t entry_ns;
        std::uint64_t advance_ns;
    };

    /** The real clock; never null. */
    std::shared_ptr<IClock> base_;
    /** Pre-hold fences of one hold pass: two per slice. */
    std::uint64_t fences_per_hold_;
    /** Configured hold of each step in milliseconds; immutable after construction. */
    std::vector<std::uint32_t> hold_ms_by_step_;
    /** The hold-entered event, or empty. */
    hold_event_handle hold_entered_;

    /**
     * Guards the counting and arming state below, atomics included: every member is read and
     * written only under it, so the atomics' operations are relaxed. Never held across a wait.
     */
    std::mutex arm_mutex_;
    /** Whether counting_key_ names a (step, pass) yet. */
    std::atomic<bool> counting_{false};
    /**
     * The (step, pass) whose pre-hold fences are being counted; recorded data, kept plain under
     * arm_mutex_.
     */
    std::uint64_t counting_key_ = 0;
    /** Pre-hold fences counted for counting_key_. */
    std::atomic<std::uint64_t> fence_count_{0};
    /** Set when fence_count_ reaches fences_per_hold_, cleared by the next wait_for_stop. */
    std::atomic<bool> armed_{false};
    /** The step the armed hold belongs to; recorded data, kept plain under arm_mutex_. */
    std::uint32_t armed_step_ = 0;

    /** Guards timeline_: shared for ages, exclusive to append an entry. */
    mutable std::shared_mutex timeline_mutex_;
    /** Every recorded hold, in recording order. */
    std::vector<hold_entry> timeline_;
    /** The latest entry_ns recorded, 0 before the first; the lock-free test of the age. */
    std::atomic<std::uint64_t> latest_entry_ns_{0};
    /** timeline_.size(), published after each append. */
    std::atomic<std::uint64_t> entries_{0};
};

/**
 * Corrupts words of a verified range just before forwarding the verification, logs every
 * corruption, and forwards every other primitive unchanged. See the file comment for the
 * injection points, the fault kinds, the log, the target-set lifetime and the threading
 * contract.
 *
 * Neither copyable nor movable: the session owns it through std::unique_ptr<IMemoryAccess>, and
 * tests and test_exports.cpp hold non-owning pointers to it for the session's lifetime.
 */
class fault_injecting_memory_access final : public IMemoryAccess {
public:
    /**
     * Wraps inner.
     *
     * @param inner The wrapped layer, owned from here on: counting_memory_access over
     *        real_memory_access in the standard stacks. Must not be null.
     * @param spec A validated spec that has already passed through normalized()
     *        (fakes/fault_spec.h), which needs the region size the layer does not know: it
     *        resolves region_length 0 to the rest of the region and aligns a stuck word's
     *        offset. The spec is copied. For the target-range kinds a region_length of 0 means
     *        no targets, so an unnormalized spec injects nothing rather than everywhere. The
     *        layer re-applies the defaults it can resolve alone: delay_threshold_ms 0 becomes
     *        k_default_delay_threshold_ms and a stuck-bit region_offset is aligned down to its
     *        8-byte word.
     * @param clock The session clock, which also stamps access_context::write_time_ns, so that
     *        delay-dependent ages are measured in one time base. Must outlive every primitive
     *        call; the session's own clock does.
     * @param holds The session's hold_clock, normally the same object as clock, or null. When
     *        set, the layer reports every pre-hold fence to it and measures delay-dependent ages
     *        with hold_clock::controlled_age_ns; when null, as in the native tests on
     *        fake_clock, ages are clock.now_ns() - write_time_ns. Must outlive every primitive
     *        call.
     * @throws std::invalid_argument when inner is null.
     * @throws std::bad_alloc when the layer's state cannot be allocated.
     */
    fault_injecting_memory_access(std::unique_ptr<IMemoryAccess> inner, const rq_fault_spec& spec,
                                  const IClock& clock, hold_clock* holds = nullptr);

    /** Destroys the log, the target sets and the inner layer. No call may be in flight. */
    ~fault_injecting_memory_access() override;

    fault_injecting_memory_access(const fault_injecting_memory_access&) = delete;
    fault_injecting_memory_access& operator=(const fault_injecting_memory_access&) = delete;
    fault_injecting_memory_access(fault_injecting_memory_access&&) = delete;
    fault_injecting_memory_access& operator=(fault_injecting_memory_access&&) = delete;

    /** Forwards unchanged: a corruption stored here could be overwritten before it is checked. */
    void stream_fill(const access_context& ctx, std::uint64_t* words, std::size_t word_count,
                     const pattern_spec& spec, std::uint64_t first_word_index,
                     int direction) override;

    /**
     * Forwards unchanged. With a hold_clock attached, a fence whose ctx.phase is
     * phase_kind::pre_hold_flush is first reported to it through note_pre_hold_fence, for every
     * kind, RQ_FAULT_NONE included; no other fence takes a lock.
     */
    void fence(const access_context& ctx) override;

    /** Forwards unchanged. */
    void flush(const access_context& ctx, const std::uint64_t* words,
               std::size_t word_count) override;

    /**
     * Injects the targets of ctx's (step, pass) that fall in the range, then forwards the call
     * with its original arguments and no lock held, then marks their log entries verified.
     *
     * Word j of the range has logical index first_word_index + j whatever the direction, so its
     * expected value is expected_value(expected, first_word_index + j). The range is const in
     * the seam because verification only reads it, but it is writable region memory that the
     * calling worker alone touches during this phase, which is what makes the injected store
     * well defined.
     *
     * @return The inner layer's mismatch count, unchanged.
     */
    std::uint64_t verify(const access_context& ctx, const std::uint64_t* words,
                         std::size_t word_count, const pattern_spec& expected,
                         std::uint64_t first_word_index, int direction,
                         mismatch_sink& sink) override;

    /**
     * As verify, for one moving-inversion sweep. Each corrupted value derives from `expected`,
     * the values this sweep verifies, never from `next`: in the descending sweep that is the
     * complement the ascending sweep streamed.
     *
     * @return The inner layer's mismatch count, unchanged.
     */
    std::uint64_t verify_and_stream(const access_context& ctx, std::uint64_t* words,
                                    std::size_t word_count, const pattern_spec& expected,
                                    const pattern_spec& next, std::uint64_t first_word_index,
                                    int direction, mismatch_sink& sink) override;

    /** Forwards unchanged: a corruption stored here could be overwritten before it is checked. */
    void stream_fill_strided(const access_context& ctx, std::uint64_t* span,
                             std::size_t span_word_count, const strided_visits& visits,
                             const pattern_spec& spec,
                             std::uint64_t span_first_word_index) override;

    /**
     * As verify, for the lines `visits` names: injects exactly the targets of ctx's
     * (step, pass), and the stuck word, that lie on a line this call visits, then forwards the
     * call with its original arguments and no lock held, then marks their log entries verified.
     * A target elsewhere in the span is left to the call that visits its line, so each target
     * is injected once per verification of its word.
     *
     * Word j of the span has logical index span_first_word_index + j, so its expected value is
     * expected_value(expected, span_first_word_index + j).
     *
     * @return The inner layer's mismatch count, unchanged.
     */
    std::uint64_t verify_strided(const access_context& ctx, const std::uint64_t* span,
                                 std::size_t span_word_count, const strided_visits& visits,
                                 const pattern_spec& expected, std::uint64_t span_first_word_index,
                                 mismatch_sink& sink) override;

    /**
     * Forwards unchanged, so the flushed re-read returns the stored corruption. Runs re-entrantly
     * from inside this layer's own verify or verify_and_stream, which hold no lock at that point.
     */
    std::uint64_t reread_word(const access_context& ctx,
                              const std::uint64_t* word) noexcept override;

    /**
     * The wrapped layer; never null. test_exports.cpp dynamic_casts it to counting_memory_access
     * to read the independent verified-byte count.
     */
    IMemoryAccess* inner() noexcept;

    /** The wrapped layer; never null. */
    const IMemoryAccess* inner() const noexcept;

    /** The spec in effect: the constructor's copy after its defaults were re-applied. */
    const rq_fault_spec& spec() const noexcept;

    /**
     * A snapshot of the log, in the order the entries were logged, copied under the log mutex.
     * Each entry holds the fields listed under Logging in the file comment, the corrupted word's
     * virtual address and the call's injection time included. Consistent once the run has
     * quiesced (after Completed); during a run, later injections extend it, and an entry whose
     * verification is still in flight shows verified_opportunity 0.
     *
     * @throws std::bad_alloc when the copy cannot be allocated.
     */
    std::vector<rq_injected_fault> injection_log() const;

    /** Number of logged injections, never above spec().max_injections. Lock-free. */
    std::uint64_t injection_count() const noexcept;

    /**
     * Number of logged injections whose verification has run (verified_opportunity == 1).
     * Equals injection_count() once the run has quiesced, unless a forwarded call threw. Lock-free.
     */
    std::uint64_t verified_opportunity_count() const noexcept;

    /**
     * Number of target sets the layer's shared table holds: 0 before the first target lookup,
     * then 1 while (step, pass) keys arrive in ascending order, as the engine issues them, and 2
     * at most in any order. Sets that only a worker's lookup cache or an in-flight call still
     * references are not counted: each worker keeps at most one, its last, alive until it looks
     * up another (step, pass) or the layer is destroyed. Stays 0 for RQ_FAULT_STUCK_BIT and for
     * a spec that injects nothing, which look no targets up. Lock-free.
     */
    std::uint64_t retained_target_set_count() const noexcept;

    /**
     * Number of target sets built since construction, each rebuild of a reclaimed (step, pass)
     * included. Stops growing once spec().max_injections is spent, because a call that starts
     * after that looks no targets up. Lock-free.
     */
    std::uint64_t built_target_set_count() const noexcept;

private:
    /**
     * The mutex-guarded log, the table of in-flight (step, pass) target sets with their
     * per-worker lookup cache, and the constants derived from the spec. Defined in the .cpp, so
     * the containers and locks behind them stay private to the implementation and its includers
     * never compile them.
     */
    struct impl;

    /** The wrapped layer; every primitive forwards to it with the caller's ctx and sink. */
    std::unique_ptr<IMemoryAccess> inner_;
    /** The spec in effect; immutable after construction, so workers read it without a lock. */
    rq_fault_spec spec_;
    /** Session clock for the delay-dependent age and each log entry's injected_ns; not owned. */
    const IClock& clock_;
    /**
     * The session's hold clock, or null: counts pre-hold fences and measures delay-dependent
     * ages on its controlled timeline. Not owned.
     */
    hold_clock* holds_ = nullptr;
    /**
     * Injections reserved against spec_.max_injections by a compare-and-swap that never passes
     * the cap, so the global cap holds across workers without a lock. A reservation whose log
     * entry could not be allocated is returned, so it never stores a corruption.
     */
    std::atomic<std::uint64_t> reserved_{0};
    /** Log entries written; what injection_count() reports. */
    std::atomic<std::uint64_t> logged_{0};
    /** Log entries marked verified; what verified_opportunity_count() reports. */
    std::atomic<std::uint64_t> verified_{0};
    /** Never null after construction. */
    std::unique_ptr<impl> impl_;
};

}  // namespace rq::engine::test
