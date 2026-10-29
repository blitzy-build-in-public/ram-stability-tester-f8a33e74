/**
 * @file fake_clock.h
 * @brief Substitute IClock for the native tests, and scripted pass durations driven through it.
 *
 * Every engine timing reads only the session's IClock: pass scheduling and step budgets, overrun,
 * time remaining, hold waits, time_since_write_us and first_error_ms. Substituting a clock that
 * moves only when a test moves it makes the duration and time-remaining tests deterministic
 * (stable, +/-30 % jittered and oversized passes, a 30 s pass against a 10 s budget) without
 * changing how real memory is written and verified. Holds of 5, 30 and 120 s complete instantly.
 *
 * Test-only: compiled into engine.tests.exe and never into either engine DLL. The AVX kernel
 * units never include it.
 */
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <type_traits>
#include <unordered_set>
#include <utility>
#include <vector>

#include "core/clock.h"

namespace rq::engine::test {

/**
 * An IClock whose time advances only through advance(), set() and wait_for_stop().
 *
 * Every member is thread-safe: workers, the coordinator and test observers read and advance it
 * concurrently. The clock is neither copyable nor movable, because the session, its workers and
 * pass_duration_script hold references to it; it must outlive all of them.
 */
class fake_clock final : public IClock {
public:
    /**
     * @param start_ns Initial reading. The nonzero default keeps "time 0" distinct from an unset
     *        write_time_ns or pass-start time, so a test can tell "never stamped" from "stamped at
     *        the start".
     */
    explicit fake_clock(uint64_t start_ns = 1'000'000'000) noexcept : now_(start_ns) {}

    fake_clock(const fake_clock&) = delete;
    fake_clock& operator=(const fake_clock&) = delete;
    fake_clock(fake_clock&&) = delete;
    fake_clock& operator=(fake_clock&&) = delete;

    /** The current fake time in nanoseconds. */
    uint64_t now_ns() const noexcept override { return now_.load(std::memory_order_acquire); }

    /**
     * Moves fake time forward by ns. Safe to call from several threads at once, as several
     * workers' observers may.
     *
     * The addition saturates at UINT64_MAX instead of wrapping, so even a "wait forever" timeout
     * passed to wait_for_stop cannot make time run backwards, which IClock's monotonic contract
     * forbids.
     */
    void advance(uint64_t ns) noexcept {
        uint64_t current = now_.load(std::memory_order_relaxed);
        uint64_t next = 0;
        do {
            next = current > UINT64_MAX - ns ? UINT64_MAX : current + ns;
        } while (!now_.compare_exchange_weak(current, next, std::memory_order_acq_rel,
                                             std::memory_order_relaxed));
    }

    /**
     * Sets fake time to ns. Tests use it only between runs: time must never go backwards while
     * a session is running, because pass durations and elapsed times are differences of readings.
     */
    void set(uint64_t ns) noexcept { now_.store(ns, std::memory_order_release); }

    /**
     * Advances fake time by timeout_ms instead of blocking.
     *
     * The step runner waits a hold in slices through this call, so that it can keep ticking its
     * observer and progress between slices. Advancing by exactly each slice's timeout makes a 5 s
     * hold total 5 s of fake time and finish instantly, and a stop requested meanwhile still ends
     * the hold at the next slice, exactly as qpc_clock's event wait would.
     *
     * @return true at once, without advancing, when the signal is already stopped; otherwise
     *         whether a stop arrived while time was advanced.
     */
    bool wait_for_stop(const stop_signal& signal, uint64_t timeout_ms) override {
        if (signal.stop_requested()) {
            return true;
        }
        advance(timeout_ms > UINT64_MAX / k_ns_per_ms ? UINT64_MAX : timeout_ms * k_ns_per_ms);
        wait_calls_.fetch_add(1, std::memory_order_relaxed);
        waited_ms_.fetch_add(timeout_ms, std::memory_order_relaxed);
        return signal.stop_requested();
    }

    /** Number of wait_for_stop calls that advanced time, i.e. were not already stopped. */
    uint64_t wait_calls() const noexcept { return wait_calls_.load(std::memory_order_relaxed); }

    /** Sum of timeout_ms over the wait_for_stop calls counted by wait_calls(). */
    uint64_t waited_ms() const noexcept { return waited_ms_.load(std::memory_order_relaxed); }

private:
    static constexpr uint64_t k_ns_per_ms = 1'000'000;

    std::atomic<uint64_t> now_;
    std::atomic<uint64_t> wait_calls_{0};
    std::atomic<uint64_t> waited_ms_{0};
};

// A pure virtual added to IClock must fail here, not in whichever test first constructs the fake.
static_assert(!std::is_abstract_v<fake_clock>, "fake_clock must implement every IClock member");

/**
 * Gives each pass a scripted duration by advancing a fake_clock when the pass is first seen.
 *
 * Intended use: tests call on_pass_begin(rec.ctx.step_index, rec.ctx.pass_index) from the
 * counting_memory_access observer (set_observer). The observer runs after each primitive has been
 * forwarded, so the advance happens during the pass's first primitive, after the step runner has
 * read the pass's start time. Pass k of the run therefore lasts per_pass_ns[k] fake nanoseconds,
 * plus any hold time that fake_clock::wait_for_stop adds. Every worker issues primitives for the
 * same (step, pass), so only the first sighting of a pair advances; later ones are ignored.
 *
 * Equal entries give stable passes, entries varied by up to +/-30 % give jittered ones, and an
 * entry above the step's budget gives an oversized pass (for example 30 s against a 10 s budget).
 * step_runner then derives passes_planned, overrun_ns and time remaining from these fake
 * durations, deterministically.
 *
 * Caveat: a scripted advance inside a pass also ages every write made earlier in that pass. Do
 * not combine a script with RQ_FAULT_DELAY_DEPENDENT injection, which compares
 * now - write_time_ns with its 2000 ms threshold and would fire outside hold steps.
 *
 * Thread-safe. The clock must outlive the script.
 */
class pass_duration_script {
public:
    /**
     * @param clock The clock to advance; held by reference.
     * @param per_pass_ns Durations consumed in first-seen order; the last entry repeats once the
     *        script runs out, and an empty script never advances.
     */
    pass_duration_script(fake_clock& clock, std::vector<uint64_t> per_pass_ns)
        : clock_(clock), per_pass_ns_(std::move(per_pass_ns)) {}

    pass_duration_script(const pass_duration_script&) = delete;
    pass_duration_script& operator=(const pass_duration_script&) = delete;
    pass_duration_script(pass_duration_script&&) = delete;
    pass_duration_script& operator=(pass_duration_script&&) = delete;

    /**
     * Advances the clock by the next scripted duration the first time (step_index, pass_index)
     * is seen; does nothing for a pair already seen.
     *
     * The advance happens while the lock is held, so two pairs first seen concurrently consume
     * script entries in a well-defined order and each advance is complete before the other
     * pair's begins.
     *
     * @throws std::bad_alloc when the seen-set cannot grow.
     */
    void on_pass_begin(uint32_t step_index, uint32_t pass_index) {
        const uint64_t key = (uint64_t{step_index} << 32) | pass_index;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!seen_.insert(key).second || per_pass_ns_.empty()) {
            return;
        }
        clock_.advance(per_pass_ns_[next_entry_]);
        if (next_entry_ + 1 < per_pass_ns_.size()) {
            ++next_entry_;
        }
    }

    /** Number of distinct (step, pass) pairs seen so far. */
    size_t passes_seen() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return seen_.size();
    }

private:
    fake_clock& clock_;
    const std::vector<uint64_t> per_pass_ns_;
    // Guarded by mutex_.
    std::unordered_set<uint64_t> seen_;
    size_t next_entry_ = 0;
    mutable std::mutex mutex_;
};

}  // namespace rq::engine::test
