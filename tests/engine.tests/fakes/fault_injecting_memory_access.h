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
 * A corruption is applied only inside verify and verify_and_stream, immediately before the call
 * is forwarded to the inner layer. The step runner issues those calls after the range's last
 * write and after its fence, flush and fence, so nothing can overwrite a corruption before the
 * forwarded verification checks it. That one point covers every injection point of the
 * verification plan:
 * - windowed steps: a window's verify, after its streamed write and fence;
 * - hold steps: the slice's verify, after the slice write and the hold;
 * - moving inversion: each sweep's verify_and_stream, between the values the base write or the
 *   previous sweep streamed and this sweep's loads;
 * - turnaround: block i - 4's verify, after that block's write and before its own verification;
 * - delay-dependent faults: the same point, just before the verification load, gated on age.
 *
 * stream_fill, fence, flush and reread_word forward unchanged: a store anywhere else could be
 * overwritten before it is checked. The error collector's flushed re-read therefore sees the
 * stored wrong value, and a single-bit flip reports RQ_REREAD_REPEATS_ACTUAL.
 *
 * @par Fault kinds
 * Targets are logical region byte offsets (address - access_context::region_base), so they do
 * not depend on where Windows placed each chunk. They are a stateless function of seed, step and
 * pass, so one (step, pass) always selects the same words for a given seed. Within one
 * (step, pass) a word is targeted at most once, so one verification of a word yields at most one
 * log entry and one error record.
 * - RQ_FAULT_NONE: pure pass-through, with no store, no lock and no log entry.
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
 * - RQ_FAULT_DELAY_DEPENDENT: the single-bit-flip targets, injected only where
 *   clock.now_ns() - ctx.write_time_ns >= delay_threshold_ms x 10^6. Only hold verifications
 *   (RQ_ACCESS_VERIFY_AFTER_HOLD) let data reach that age, and windowed verifications never do.
 *   The gate is deliberately time-based only, so the tests' assertion that reports appear only
 *   after holds also checks the runner's write_time_ns stamps.
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
 * - verified_opportunity = 0 and reserved0 = 0.
 *
 * verified_opportunity becomes 1 once the forwarded call for that range has returned. The kernel
 * verifies the whole range in one call, so every logged injection of a completed run is verified.
 * 0 survives only when the forwarded call throws or the process dies inside it, which keeps the
 * ABI field honest.
 *
 * @par Caps
 * spec.max_injections caps the logged injections of the whole run, across all workers; 0 means
 * none. Candidates beyond the cap are neither stored nor logged. Moving inversion verifies twice
 * per pass, so a target can be injected, and logged, twice in one pass, once per sweep; the tests
 * match by multiset.
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
 * As in IMemoryAccess, only reread_word is noexcept: stream_fill, fence, flush, verify and
 * verify_and_stream propagate whatever an inner IMemoryAccess implementation throws, and the
 * worker thread entry points turn that into a stop with RQ_END_INTERNAL_FAULT. In the standard
 * stacks the inner layer is counting_memory_access, whose six overrides are noexcept and which
 * contains its own log-allocation failure by setting log_overflowed() instead of throwing, so no
 * primitive exception reaches this layer from it: an exception escaping its inner layer or its
 * observer calls std::terminate instead. When this layer cannot allocate its own bookkeeping, a
 * target set or a log entry, it skips injection for that call and still forwards it.
 */
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
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

/**
 * Corrupts words of a verified range just before forwarding the verification, logs every
 * corruption, and forwards every other primitive unchanged. See the file comment for the
 * injection points, the fault kinds, the log and the threading contract.
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
     * @throws std::invalid_argument when inner is null.
     * @throws std::bad_alloc when the layer's state cannot be allocated.
     */
    fault_injecting_memory_access(std::unique_ptr<IMemoryAccess> inner, const rq_fault_spec& spec,
                                  const IClock& clock);

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

    /** Forwards unchanged. */
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
     * Consistent once the run has quiesced (after Completed); during a run, later injections
     * extend it, and an entry whose verification is still in flight shows verified_opportunity 0.
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

private:
    /**
     * The mutex-guarded log, the per-(step, pass) target sets with their per-worker lookup
     * cache, and the constants derived from the spec. Defined in the .cpp, so the containers and
     * locks behind them stay private to the implementation and its includers never compile them.
     */
    struct impl;

    /** The wrapped layer; every primitive forwards to it with the caller's ctx and sink. */
    std::unique_ptr<IMemoryAccess> inner_;
    /** The spec in effect; immutable after construction, so workers read it without a lock. */
    rq_fault_spec spec_;
    /** Session clock for the delay-dependent age; not owned. */
    const IClock& clock_;
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
