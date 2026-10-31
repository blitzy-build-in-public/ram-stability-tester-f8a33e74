/**
 * @file fault_injecting_memory_access.cpp
 * @brief The fault-injecting IMemoryAccess layer: deterministic target selection, corruption
 *        just before each forwarded verification, and the ground-truth injection log.
 *
 * Test-only: compiled into engine.tests.exe and ramquick_engine_test.dll, never into
 * ramquick_engine.dll, and never at a higher /arch level, so the STL is free to use here.
 *
 * @par Injection points
 * Every corruption is stored inside verify, verify_strided or verify_and_stream, immediately
 * before the call is forwarded; a verify_strided call stores only on the lines it visits. The
 * step runner issues those calls after the range's last write and after its fence, flush and
 * fence, so that one point is each injection point of the verification plan:
 * - after a window's streamed write and fence (windowed steps, BankStride included);
 * - after the slice write and the hold (hold steps);
 * - between a moving-inversion sweep's write and the next sweep's verification;
 * - after a turnaround block's write, before its own verification;
 * - for delay-dependent faults the same point, gated on the age of the data.
 * Nothing can overwrite the stored value before the forwarded verification loads it.
 *
 * The stores are plain stores to the region, issued above the counting layer, so they never
 * appear in counting_memory_access's primitive log. That is deliberate: the
 * flush-before-verification assertions of test_step_runner.cpp examine the runner's own
 * sequence, on a counting -> real stack or below this layer, and an injected store is not part
 * of that sequence.
 */
#include "fakes/fault_injecting_memory_access.h"
#include "core/patterns/patterns.h"
#include "core/kernels/kernels.h"

#include <algorithm>
#include <array>
#include <cassert>
#include <iterator>
#include <map>
#include <mutex>
#include <new>
#include <shared_mutex>
#include <stdexcept>
#include <system_error>
#include <utility>

namespace rq::engine::test {

namespace {

/**
 * One SplitMix64 step: the golden-ratio increment followed by the Mix13 finalizer, so that 0 maps
 * to a well-mixed value too. Target selection hashes with this local function rather than the
 * engine's pattern generators, so fault positions stay independent of the data the patterns
 * write.
 */
constexpr std::uint64_t mix(std::uint64_t z) noexcept {
    z += k_splitmix_gamma;
    z = (z ^ (z >> 30)) * k_splitmix_mul1;
    z = (z ^ (z >> 27)) * k_splitmix_mul2;
    return z ^ (z >> 31);
}

/** Salt of the per-target index, so target i never hashes like a seed or a (step, pass) key. */
constexpr std::uint64_t k_target_index_salt = 0x632BE59BD9B4E019ull;
/** Salt of the random-bit draw, so a target's bit is independent of its offset. */
constexpr std::uint64_t k_target_bit_salt = 0xD1B54A32D192ED03ull;
/** Workers with a lock-free target-set cache slot; higher indices use the locked lookup. */
constexpr std::uint32_t k_worker_slots = 1024;
/** Log entries preallocated at most: 56 MiB, enough for every cap the tests use. */
constexpr std::uint64_t k_max_preallocated_log_entries = std::uint64_t{1} << 20;
constexpr std::uint64_t k_ns_per_ms = 1'000'000;

/** Key of one (step, pass) target set. */
constexpr std::uint64_t set_key(std::uint32_t step_index, std::uint32_t pass_index) noexcept {
    return (std::uint64_t{step_index} << 32) | pass_index;
}

/**
 * The defaults the layer can resolve without knowing the region size, idempotent with
 * normalized() (fakes/fault_spec.h), which callers apply first.
 */
rq_fault_spec with_layer_defaults(const rq_fault_spec& spec) noexcept {
    rq_fault_spec result = spec;
    if (result.delay_threshold_ms == 0) {
        result.delay_threshold_ms = k_default_delay_threshold_ms;
    }
    if (result.kind == RQ_FAULT_STUCK_BIT) {
        result.region_offset = detail::containing_word_offset(result.region_offset);
    }
    return result;
}

/** Rejects a null inner layer before any other member is built. */
std::unique_ptr<IMemoryAccess> require_inner(std::unique_ptr<IMemoryAccess> inner) {
    if (inner == nullptr) {
        throw std::invalid_argument("fault_injecting_memory_access requires an inner layer");
    }
    return inner;
}

/**
 * Fences of one slice's pre-hold flush: fence_flush_fence (core/runner/step_runner.cpp) fences
 * before and after flushing the slice, and issues both even when a stop cuts the flush short.
 */
constexpr std::uint64_t k_pre_hold_fences_per_slice = 2;
/** Hold entries reserved up front: far more hold passes than any shipped or scaled plan runs. */
constexpr std::size_t k_reserved_hold_entries = 256;

/** Rejects a null base clock before any other member is built. */
std::shared_ptr<IClock> require_base_clock(std::shared_ptr<IClock> base) {
    if (base == nullptr) {
        throw std::invalid_argument("hold_clock requires a base clock");
    }
    return base;
}

/** Rejects a slice count of 0, which no session has and which would arm on no fence. */
std::uint32_t require_slices(std::uint32_t slice_count) {
    if (slice_count == 0) {
        throw std::invalid_argument("hold_clock requires at least one slice");
    }
    return slice_count;
}

/** Reserves room for n elements; false, with v unchanged, when the allocation fails. */
template <class T>
bool try_reserve(std::vector<T>& v, std::size_t n) noexcept {
    try {
        v.reserve(n);
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    }
}

/**
 * Claims up to `wanted` injections against the run-wide cap without a lock: the
 * compare-exchange never moves the count past the cap, so concurrent workers can never
 * overshoot it together.
 *
 * @return The number claimed, from 0 (cap reached) to wanted.
 */
std::uint64_t claim_against_cap(std::atomic<std::uint64_t>& reserved, std::uint64_t cap,
                                std::uint64_t wanted) noexcept {
    std::uint64_t current = reserved.load(std::memory_order_relaxed);
    std::uint64_t granted = 0;
    do {
        if (current >= cap) {
            return 0;
        }
        granted = std::min(wanted, cap - current);
    } while (
        !reserved.compare_exchange_weak(current, current + granted, std::memory_order_relaxed));
    return granted;
}

/**
 * Whether a strided call visits the line holding span-relative byte offset `offset`; always true
 * for a plain range (null visits), which verifies every word it covers.
 *
 * Visit k's line lies at (k / L) x 64 + (k % L) x s, so a line at offset o with o / s < L is
 * visit ((o % s) / 64) x L + o / s; the preconditions in core/kernels/kernels.h make that the
 * only visit that can name it. The call visits it when that k lies in
 * [first_visit, first_visit + visit_count).
 */
bool on_visited_line(const strided_visits* visits, std::uint64_t offset) noexcept {
    if (visits == nullptr) {
        return true;
    }
    if (visits->visit_count == 0 || visits->stride_bytes == 0 || visits->lines_per_sweep == 0) {
        return false;
    }
    const std::uint64_t line_in_sweep = offset / visits->stride_bytes;
    if (line_in_sweep >= visits->lines_per_sweep) {
        return false;
    }
    const std::uint64_t sweep = offset % visits->stride_bytes / k_cache_line_bytes;
    const std::uint64_t visit = sweep * visits->lines_per_sweep + line_in_sweep;
    return visit >= visits->first_visit && visit - visits->first_visit < visits->visit_count;
}

}  // namespace

struct fault_injecting_memory_access::impl {
    /** One word to corrupt in a (step, pass): its logical region byte offset and its bit. */
    struct target {
        std::uint64_t offset;
        std::uint32_t bit;
    };

    /** The targets of one (step, pass), sorted by offset, at most one per word; immutable. */
    struct target_set {
        std::uint64_t key;
        std::vector<target> targets;
    };

    /** One word of the current call to corrupt. */
    struct candidate {
        /** The word inside the verified range. */
        const std::uint64_t* word;
        /** Its logical region byte offset. */
        std::uint64_t offset;
        /** Its expected value with the target bit inverted. */
        std::uint64_t corrupted;
        /** The inverted bit, 0-63. */
        std::uint32_t bit;
    };

    /**
     * A worker's most recent target set, one cache line per worker so that workers looking up
     * their own sets never share a line, and a repeat lookup touches no reference count that
     * other workers also change. The slot's reference keeps its set alive after the table has
     * reclaimed it, until this worker looks up another (step, pass). The padding is explicit,
     * so the alignment adds none and the type needs no padding warning suppressed.
     */
    struct alignas(k_cache_line_bytes) worker_slot {
        /** Read and replaced only by the call holding `leased`, so no use of it races. */
        std::shared_ptr<const target_set> set;
        /**
         * Taken without waiting by the call that uses `set`. Each worker index belongs to one
         * worker thread, so the engine always finds it free; a concurrent caller reusing an
         * index finds it taken and holds its own reference for the call instead.
         */
        std::atomic<bool> leased{false};
        unsigned char padding[k_cache_line_bytes - sizeof(std::shared_ptr<const target_set>) -
                              sizeof(std::atomic<bool>)];
    };
    static_assert(sizeof(worker_slot) == k_cache_line_bytes, "one cache line per worker slot");

    /** A slot's lease for one lookup, returned on every exit, a throw included. */
    class slot_lease {
    public:
        explicit slot_lease(worker_slot& slot) noexcept : slot_(slot) {}
        ~slot_lease() { slot_.leased.store(false, std::memory_order_release); }
        slot_lease(const slot_lease&) = delete;
        slot_lease& operator=(const slot_lease&) = delete;

    private:
        worker_slot& slot_;
    };

    explicit impl(const rq_fault_spec& spec)
        : kind(spec.kind), seed(spec.seed), max_injections(spec.max_injections),
          injected_passes_per_step(spec.injected_passes_per_step), stuck_offset(spec.region_offset),
          stuck_value_bit(spec.stuck_value & 1) {
        // The target range holds only whole words: its first word starts at or after
        // region_offset and its last ends at or before region_offset + region_length.
        first_word = spec.region_offset / detail::k_word_bytes +
                     (spec.region_offset % detail::k_word_bytes != 0 ? 1 : 0);
        const std::uint64_t range_end = spec.region_length > UINT64_MAX - spec.region_offset
                                            ? UINT64_MAX
                                            : spec.region_offset + spec.region_length;
        const std::uint64_t end_word = range_end / detail::k_word_bytes;
        word_count = end_word > first_word ? end_word - first_word : 0;
        delay_threshold_ns = std::uint64_t{spec.delay_threshold_ms} * k_ns_per_ms;
        fixed_bit = spec.bit < 64 ? spec.bit : k_random_bit;
        // A random stuck bit is drawn once from the seed: a stuck cell is one fixed bit for the
        // whole run, unlike the per-target bits of the other kinds.
        stuck_bit = spec.bit < 64 ? spec.bit : static_cast<std::uint32_t>(mix(spec.seed) & 63);

        if (detail::uses_target_range(kind)) {
            targets_per_pass = std::min<std::uint64_t>(
                {spec.per_pass_count, std::uint64_t{k_max_targets_per_pass}, word_count});
            enabled = max_injections > 0 && targets_per_pass > 0;
        } else if (kind == RQ_FAULT_STUCK_BIT) {
            enabled = max_injections > 0;
        }
        if (enabled) {
            // Best effort: preallocation only keeps regrowth out of the log mutex. Without it
            // the log grows on demand, and a failure there skips that call's injections.
            static_cast<void>(
                try_reserve(log, static_cast<std::size_t>(
                                     std::min(max_injections, k_max_preallocated_log_entries))));
        }
    }

    /**
     * Derives the targets of one (step, pass) from the seed alone, so the same seed always
     * selects the same words, on every run and whichever worker asks first, and a set rebuilt
     * after its reclamation equals the original.
     *
     * Offsets are logical region offsets (address - access_context::region_base), so they do
     * not depend on where Windows placed each chunk. Targets at or beyond the bytes the engine
     * finally tests are never verified, so they are never injected or logged.
     *
     * @throws std::bad_alloc when the set cannot be allocated.
     */
    std::shared_ptr<const target_set> build_set(std::uint64_t key) const {
        auto set = std::make_shared<target_set>();
        set->key = key;
        set->targets.reserve(static_cast<std::size_t>(targets_per_pass));
        const std::uint64_t key_hash = mix(key);
        for (std::uint64_t i = 0; i < targets_per_pass; ++i) {
            const std::uint64_t h = mix(seed ^ key_hash ^ mix(i + k_target_index_salt));
            const std::uint64_t offset = (first_word + h % word_count) * detail::k_word_bytes;
            const std::uint32_t bit =
                fixed_bit != k_random_bit
                    ? fixed_bit
                    : static_cast<std::uint32_t>(mix(h ^ k_target_bit_salt) & 63);
            set->targets.push_back(target{offset, bit});
        }
        // At most one target per word and (step, pass): a second one would log two injections
        // for the single error record that one verification of the word produces, and the tests
        // match each injection to exactly one record. The stable sort keeps the earliest
        // generated target of a word first, and unique keeps that one.
        std::stable_sort(set->targets.begin(), set->targets.end(),
                         [](const target& a, const target& b) { return a.offset < b.offset; });
        set->targets.erase(
            std::unique(set->targets.begin(), set->targets.end(),
                        [](const target& a, const target& b) { return a.offset == b.offset; }),
            set->targets.end());
        return set;
    }

    /**
     * The target set of key from the shared table, built on a miss: a shared lookup finds a
     * set another worker built, and only a miss takes the exclusive lock, rechecks and builds.
     *
     * Building reclaims every table entry except the new set and the newest key's. The engine
     * issues (step, pass) keys in ascending order, every verification of a pass ending at a
     * phase barrier before the next pass's begin, so the table then holds one generation; a
     * caller going back to an older key makes it hold two at most. Safety rests on ownership
     * alone, never on that order: a worker slot or a call still holding a reclaimed set keeps
     * it alive, and a reclaimed key asked for again is rebuilt identically (build_set).
     *
     * @throws std::bad_alloc when the set or its table node cannot be allocated; the table is
     *         then unchanged.
     */
    std::shared_ptr<const target_set> shared_set(std::uint64_t key) {
        {
            const std::shared_lock<std::shared_mutex> lock(sets_mutex);
            const auto it = sets.find(key);
            if (it != sets.end()) {
                return it->second;
            }
        }
        const std::unique_lock<std::shared_mutex> lock(sets_mutex);
        auto it = sets.find(key);
        if (it == sets.end()) {
            it = sets.emplace(key, build_set(key)).first;
            built_sets.fetch_add(1, std::memory_order_relaxed);
            const auto newest = std::prev(sets.end());
            for (auto other = sets.begin(); other != sets.end();) {
                other = other == it || other == newest ? std::next(other) : sets.erase(other);
            }
            retained_sets.store(sets.size(), std::memory_order_relaxed);
        }
        return it->second;
    }

    /**
     * Appends to `out` the candidates of every target of `set` inside [range_off, range_end)
     * and, for a strided call, on a line its visits name, in ascending offset order. Allocates
     * only when at least one target is kept, or for a plain range falls in it.
     *
     * @throws std::bad_alloc when `out` cannot be allocated.
     */
    static void collect_targets(const target_set& set, const std::uint64_t* words,
                                std::uint64_t range_off, std::uint64_t range_end,
                                const pattern_spec& expected, std::uint64_t first_word_index,
                                const strided_visits* visits, std::vector<candidate>& out) {
        const auto by_offset = [](const target& t, std::uint64_t offset) {
            return t.offset < offset;
        };
        const auto first =
            std::lower_bound(set.targets.begin(), set.targets.end(), range_off, by_offset);
        const auto last = std::lower_bound(first, set.targets.end(), range_end, by_offset);
        if (first == last) {
            return;
        }
        if (visits == nullptr) {
            // A strided call keeps only the targets on its own lines, so it grows `out` per
            // target instead of reserving for the whole span.
            out.reserve(static_cast<std::size_t>(last - first));
        }
        for (auto it = first; it != last; ++it) {
            if (!on_visited_line(visits, it->offset - range_off)) {
                continue;
            }
            const std::uint64_t j = (it->offset - range_off) / detail::k_word_bytes;
            const std::uint64_t e = expected_value(expected, first_word_index + j);
            out.push_back(
                candidate{words + j, it->offset, e ^ (std::uint64_t{1} << it->bit), it->bit});
        }
    }

    /**
     * Appends to `out` every word of the range that this call should corrupt, in ascending
     * offset order: for a strided call (non-null visits) only the words on the lines it visits.
     * Allocates only to build a (step, pass)'s target set that the table does not hold, and when
     * at least one target falls in the range.
     *
     * @throws std::bad_alloc when a target set or `out` cannot be allocated.
     */
    void collect(const access_context& ctx, const std::uint64_t* words, std::uint64_t range_off,
                 std::uint64_t range_end, const pattern_spec& expected,
                 std::uint64_t first_word_index, const strided_visits* visits,
                 std::vector<candidate>& out) {
        if (range_end <= range_off) {
            // Only a range whose logical offset wraps 2^64 gets here, which a consistent
            // region_base never produces; it holds no target.
            return;
        }
        if (kind == RQ_FAULT_STUCK_BIT) {
            if (stuck_offset < range_off || stuck_offset >= range_end ||
                !on_visited_line(visits, stuck_offset - range_off)) {
                return;
            }
            const std::uint64_t j = (stuck_offset - range_off) / detail::k_word_bytes;
            const std::uint64_t e = expected_value(expected, first_word_index + j);
            // Only a verification whose expected bit differs from the stuck value can mismatch,
            // so only that one is corrupted, logged and counted against the cap.
            if (((e >> stuck_bit) & 1) == stuck_value_bit) {
                return;
            }
            out.push_back(
                candidate{words + j, stuck_offset, e ^ (std::uint64_t{1} << stuck_bit), stuck_bit});
            return;
        }
        const std::uint64_t key = set_key(ctx.step_index, ctx.pass_index);
        worker_slot* const slot =
            ctx.worker_index < k_worker_slots ? &slots[ctx.worker_index] : nullptr;
        if (slot != nullptr && !slot->leased.exchange(true, std::memory_order_acquire)) {
            // A repeat lookup for the (step, pass) this worker is in takes no lock and leaves
            // the shared reference count alone; only the worker's first call in a new pass
            // reaches the table. The lease ends here, before the call is forwarded.
            const slot_lease lease(*slot);
            if (slot->set == nullptr || slot->set->key != key) {
                slot->set = shared_set(key);
            }
            collect_targets(*slot->set, words, range_off, range_end, expected, first_word_index,
                            visits, out);
            return;
        }
        // Workers beyond the slots, and a concurrent caller reusing a leased worker index, hold
        // their own reference for the call, so a reclamation cannot free the set under them.
        const std::shared_ptr<const target_set> set = shared_set(key);
        collect_targets(*set, words, range_off, range_end, expected, first_word_index, visits, out);
    }

    /**
     * Claims the candidates against the cap and logs the claimed ones, all under one lock, so
     * this call's entries are contiguous from *first_entry. A claim whose entries cannot be
     * allocated is returned, so no corruption is ever stored unless it is logged. Every entry
     * of the call carries one session-clock reading, taken before any of its corruptions is
     * stored, and the address each corruption will be stored through.
     *
     * @return The number of leading candidates claimed and logged; only those may be stored.
     */
    std::size_t log_candidates(fault_injecting_memory_access& layer, const access_context& ctx,
                               const std::vector<candidate>& candidates, std::size_t* first_entry) {
        const std::uint64_t granted =
            claim_against_cap(layer.reserved_, max_injections, candidates.size());
        if (granted == 0) {
            return 0;
        }
        const auto count = static_cast<std::size_t>(granted);
        // Read outside the lock: the stores follow this call, so the reading precedes each one.
        const std::uint64_t injected_ns = layer.clock_.now_ns();
        {
            const std::lock_guard<std::mutex> lock(log_mutex);
            // Geometric growth keeps appends amortised once the preallocation is used up;
            // after the reserve, push_back of the trivially copyable entry cannot throw.
            if (log.capacity() - log.size() < count &&
                !try_reserve(log, std::max(log.size() + count, log.capacity() * 2))) {
                layer.reserved_.fetch_sub(granted, std::memory_order_relaxed);
                return 0;
            }
            *first_entry = log.size();
            for (std::size_t k = 0; k < count; ++k) {
                rq_injected_fault entry{};
                entry.word_offset = candidates[k].offset;
                entry.bit_mask = std::uint64_t{1} << candidates[k].bit;
                entry.bit = candidates[k].bit;
                entry.step_index = ctx.step_index;
                entry.pass_index = ctx.pass_index;
                entry.access_type = ctx.access_type;
                entry.verified_opportunity = 0;
                entry.reserved0 = 0;
                entry.address = static_cast<std::uint64_t>(
                    reinterpret_cast<std::uintptr_t>(candidates[k].word));
                entry.injected_ns = injected_ns;
                log.push_back(entry);
            }
        }
        layer.logged_.fetch_add(granted, std::memory_order_release);
        return count;
    }

    /** Marks this call's log entries verified once the forwarded verification has returned. */
    void mark_verified(fault_injecting_memory_access& layer, std::size_t first_entry,
                       std::size_t count) {
        {
            const std::lock_guard<std::mutex> lock(log_mutex);
            for (std::size_t k = first_entry; k < first_entry + count; ++k) {
                log[k].verified_opportunity = 1;
            }
        }
        layer.verified_.fetch_add(count, std::memory_order_release);
    }

    /**
     * The shared body of verify, verify_strided and verify_and_stream: stores this call's
     * corruptions, forwards the call through `forward` with no lock held, then marks the
     * corruptions verified.
     *
     * `expected` is the pattern the range should hold when the forwarded call loads it, which
     * for a moving-inversion sweep is the sweep's expected spec, never its next one: in the
     * descending sweep that is the complement the ascending sweep streamed. Each sweep verifies
     * a word once, so a target can be injected, and logged, once per sweep, and each injection
     * yields one error record.
     *
     * For verify_strided, [words, words + word_count) is the span and `visits` the call's
     * visits: only targets on visited lines are injected, because the window's other strided
     * calls verify, and inject, the rest of the span. Null for the plain ranges, all of whose
     * words the call verifies.
     *
     * The forwarded call is outside every try block, so an exception from the inner layer
     * propagates unchanged and is never mistaken for this layer's own allocation failure.
     */
    template <class Forward>
    std::uint64_t inject_then_forward(fault_injecting_memory_access& layer,
                                      const access_context& ctx, const std::uint64_t* words,
                                      std::size_t word_count, const pattern_spec& expected,
                                      std::uint64_t first_word_index, const strided_visits* visits,
                                      Forward&& forward) {
        // Kind RQ_FAULT_NONE, every test session's default, stays a pure pass-through: no
        // clock read, no lock, no store and no log entry. So does a call that verifies nothing.
        if (!enabled || word_count == 0 || (visits != nullptr && visits->visit_count == 0)) {
            return forward();
        }
        // A pass beyond the per-step quota runs clean, before the cap is consulted: no clock
        // read, no target lookup and no allocation. The quota bounds each step's corruptions by
        // the plan, so a test can keep the run-wide cap out of reach and inject into every step.
        if (injected_passes_per_step != 0 && ctx.pass_index >= injected_passes_per_step) {
            return forward();
        }
        // Once the run-wide cap is spent no candidate can be claimed, so the call forwards at
        // once: no clock read, no target lookup, no allocation, and no set built for a
        // (step, pass) that could inject nothing. The load is only a snapshot, because
        // log_candidates hands back a claim it could not log, so claim_against_cap stays the
        // cap's one authority. Entries that calls still in flight logged are theirs to mark
        // verified; this call returns before it could touch any.
        if (layer.reserved_.load(std::memory_order_relaxed) >= max_injections) {
            return forward();
        }
        const std::uint64_t range_off = reinterpret_cast<std::uintptr_t>(words) - ctx.region_base;
        const std::uint64_t range_end = range_off + word_count * detail::k_word_bytes;
        // Word j of the range has logical index first_word_index + j whatever the direction,
        // and region_base makes the address offset agree with that index.
        assert(first_word_index == range_off / detail::k_word_bytes);
        if (kind == RQ_FAULT_DELAY_DEPENDENT) {
            // Time-based only, deliberately not filtered on access_type: the tests' assertion
            // that every report follows a hold therefore also checks the runner's
            // write_time_ns stamps. On a hold clock the age is controlled time, which only the
            // holds that began after the write advance, so a worker descheduled between a
            // window's write and its verification never ages that window's data.
            const std::uint64_t now = layer.clock_.now_ns();
            const std::uint64_t age = layer.holds_ != nullptr
                                          ? layer.holds_->controlled_age_ns(ctx.write_time_ns, now)
                                          : (now > ctx.write_time_ns ? now - ctx.write_time_ns : 0);
            if (age < delay_threshold_ns) {
                return forward();
            }
        }

        std::vector<candidate> candidates;
        std::size_t first_entry = 0;
        std::size_t injected = 0;
        try {
            collect(ctx, words, range_off, range_end, expected, first_word_index, visits,
                    candidates);
            if (!candidates.empty()) {
                injected = log_candidates(layer, ctx, candidates, &first_entry);
            }
        } catch (const std::bad_alloc&) {
            // No claim survives a throw here: collect claims nothing, and log_candidates
            // returns its claim itself. The call is then verified without injections.
            injected = 0;
        }
        for (std::size_t k = 0; k < injected; ++k) {
            // The seam passes the range as const because verification only reads it, but it is
            // writable region memory that the calling worker alone touches during this phase.
            // A plain cached store suffices: the forwarded verification loads it on this
            // thread in program order, and the error collector's re-read (fence, flush, fence,
            // load) writes the dirty line back and reloads the same wrong value, so a flip
            // reports RQ_REREAD_REPEATS_ACTUAL.
            *const_cast<volatile std::uint64_t*>(candidates[k].word) = candidates[k].corrupted;
        }
        // No lock is held here: the error collector's sink re-enters reread_word on this layer
        // from inside the forwarded verification, on this thread.
        const std::uint64_t mismatches = forward();
        if (injected != 0) {
            mark_verified(layer, first_entry, injected);
        }
        return mismatches;
    }

    // Constants derived from the spec in effect; immutable after construction.
    const std::uint32_t kind;
    const std::uint64_t seed;
    const std::uint64_t max_injections;
    /** Passes of each step that inject, from pass 0; 0 means every pass. */
    const std::uint32_t injected_passes_per_step;
    /** RQ_FAULT_STUCK_BIT: logical offset of the stuck word, aligned to its 8-byte word. */
    const std::uint64_t stuck_offset;
    /** RQ_FAULT_STUCK_BIT: the value the stuck bit is forced to, 0 or 1. */
    const std::uint64_t stuck_value_bit;
    /** RQ_FAULT_STUCK_BIT: the stuck bit, fixed for the whole run. */
    std::uint32_t stuck_bit = 0;
    /** spec.bit when it names a bit, k_random_bit for a seeded bit per target. */
    std::uint32_t fixed_bit = k_random_bit;
    /** Target-range kinds: logical index of the first whole word of the range. */
    std::uint64_t first_word = 0;
    /** Target-range kinds: whole words in the range. */
    std::uint64_t word_count = 0;
    /** Target-range kinds: targets drawn per (step, pass) before duplicates are removed. */
    std::uint64_t targets_per_pass = 0;
    /** RQ_FAULT_DELAY_DEPENDENT: minimum age of injected data. */
    std::uint64_t delay_threshold_ns = 0;
    /** False when the spec can inject nothing: every call then passes straight through. */
    bool enabled = false;

    /** Guards log; never held across a forwarded call. */
    mutable std::mutex log_mutex;
    /** Every logged injection, in logging order. */
    std::vector<rq_injected_fault> log;

    /** Guards sets: shared for lookups, exclusive to build a missing set and reclaim others. */
    std::shared_mutex sets_mutex;
    /**
     * Target sets by (step, pass) key: the newest key's and the one built last, so one or two
     * (see shared_set). Each entry shares ownership with the worker slots that cache it.
     */
    std::map<std::uint64_t, std::shared_ptr<const target_set>> sets;
    /** sets.size(), stored under the exclusive lock so that the observer reads it lock-free. */
    std::atomic<std::uint64_t> retained_sets{0};
    /** Target sets built since construction, a rebuild of a reclaimed key included. */
    std::atomic<std::uint64_t> built_sets{0};
    /** Per-worker cache of the last set looked up, indexed by access_context::worker_index. */
    std::array<worker_slot, k_worker_slots> slots{};
};

hold_clock::hold_clock(std::shared_ptr<IClock> base, std::uint32_t slice_count,
                       std::vector<std::uint32_t> hold_ms_by_step, hold_event_handle hold_entered)
    : base_(require_base_clock(std::move(base))),
      fences_per_hold_(std::uint64_t{require_slices(slice_count)} * k_pre_hold_fences_per_slice),
      hold_ms_by_step_(std::move(hold_ms_by_step)), hold_entered_(std::move(hold_entered)) {
    // Appends happen on the coordinator once per hold pass; reserving the usual run's worth
    // keeps them from reallocating under the exclusive lock in all but very long plans.
    timeline_.reserve(k_reserved_hold_entries);
}

hold_clock::~hold_clock() = default;

std::uint64_t hold_clock::now_ns() const noexcept {
    return base_->now_ns();
}

bool hold_clock::wait_for_stop(const stop_signal& signal, std::uint64_t timeout_ms) {
    bool entering = false;
    std::uint32_t step = 0;
    {
        const std::lock_guard<std::mutex> lock(arm_mutex_);
        if (armed_.load(std::memory_order_relaxed)) {
            armed_.store(false, std::memory_order_relaxed);
            entering = true;
            step = armed_step_;
        }
    }
    // A stop requested by now ends the step before its hold, so no hold begins: the base wait
    // returns at once and nothing is recorded or signalled.
    if (entering && !signal.stop_requested()) {
        const std::uint64_t entry_ns = base_->now_ns();
        const std::uint64_t hold_ms = step < hold_ms_by_step_.size() ? hold_ms_by_step_[step] : 0;
        {
            const std::unique_lock<std::shared_mutex> lock(timeline_mutex_);
            timeline_.push_back(hold_entry{entry_ns, hold_ms * k_ns_per_ms});
            if (entry_ns > latest_entry_ns_.load(std::memory_order_relaxed)) {
                latest_entry_ns_.store(entry_ns, std::memory_order_release);
            }
            entries_.store(timeline_.size(), std::memory_order_release);
        }
        // Signalled after the entry is recorded, so a test woken by the event finds it counted.
        if (hold_entered_ != nullptr && !SetEvent(hold_entered_.get())) {
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                    "SetEvent on the hold-entered event failed");
        }
    }
    return base_->wait_for_stop(signal, timeout_ms);
}

void hold_clock::note_pre_hold_fence(std::uint32_t step_index, std::uint32_t pass_index) {
    const std::uint64_t key = set_key(step_index, pass_index);
    const std::lock_guard<std::mutex> lock(arm_mutex_);
    if (!counting_.load(std::memory_order_relaxed) || counting_key_ != key) {
        counting_.store(true, std::memory_order_relaxed);
        counting_key_ = key;
        fence_count_.store(0, std::memory_order_relaxed);
    }
    const std::uint64_t counted = fence_count_.fetch_add(1, std::memory_order_relaxed) + 1;
    // Equality, not >=, so the hold arms once even if a caller fences the same key again.
    if (counted == fences_per_hold_) {
        armed_.store(true, std::memory_order_relaxed);
        armed_step_ = step_index;
    }
}

std::uint64_t hold_clock::controlled_age_ns(std::uint64_t write_ns, std::uint64_t now_ns) const {
    // Every entry lies at or before latest_entry_ns_, so none can fall in (write_ns, now_ns]
    // when the write is that recent: windowed, turnaround and moving-inversion data, written
    // and verified within one pass, take this path without touching the lock.
    if (write_ns >= now_ns || write_ns >= latest_entry_ns_.load(std::memory_order_acquire)) {
        return 0;
    }
    const std::shared_lock<std::shared_mutex> lock(timeline_mutex_);
    std::uint64_t age = 0;
    for (const hold_entry& entry : timeline_) {
        if (entry.entry_ns > write_ns && entry.entry_ns <= now_ns) {
            age = entry.advance_ns > UINT64_MAX - age ? UINT64_MAX : age + entry.advance_ns;
        }
    }
    return age;
}

std::uint64_t hold_clock::hold_entry_count() const noexcept {
    return entries_.load(std::memory_order_acquire);
}

fault_injecting_memory_access::fault_injecting_memory_access(std::unique_ptr<IMemoryAccess> inner,
                                                             const rq_fault_spec& spec,
                                                             const IClock& clock, hold_clock* holds)
    : inner_(require_inner(std::move(inner))), spec_(with_layer_defaults(spec)), clock_(clock),
      holds_(holds), impl_(std::make_unique<impl>(spec_)) {}

fault_injecting_memory_access::~fault_injecting_memory_access() = default;

void fault_injecting_memory_access::stream_fill(const access_context& ctx, std::uint64_t* words,
                                                std::size_t word_count, const pattern_spec& spec,
                                                std::uint64_t first_word_index, int direction) {
    inner_->stream_fill(ctx, words, word_count, spec, first_word_index, direction);
}

void fault_injecting_memory_access::fence(const access_context& ctx) {
    // Counted for every kind, RQ_FAULT_NONE included, so a test that injects nothing can still
    // be told when every worker has flushed its slice and the hold begins.
    if (holds_ != nullptr && ctx.phase == phase_kind::pre_hold_flush) {
        holds_->note_pre_hold_fence(ctx.step_index, ctx.pass_index);
    }
    inner_->fence(ctx);
}

void fault_injecting_memory_access::flush(const access_context& ctx, const std::uint64_t* words,
                                          std::size_t word_count) {
    inner_->flush(ctx, words, word_count);
}

std::uint64_t fault_injecting_memory_access::verify(const access_context& ctx,
                                                    const std::uint64_t* words,
                                                    std::size_t word_count,
                                                    const pattern_spec& expected,
                                                    std::uint64_t first_word_index, int direction,
                                                    mismatch_sink& sink) {
    return impl_->inject_then_forward(*this, ctx, words, word_count, expected, first_word_index,
                                      nullptr, [&] {
                                          return inner_->verify(ctx, words, word_count, expected,
                                                                first_word_index, direction, sink);
                                      });
}

std::uint64_t fault_injecting_memory_access::verify_and_stream(
    const access_context& ctx, std::uint64_t* words, std::size_t word_count,
    const pattern_spec& expected, const pattern_spec& next, std::uint64_t first_word_index,
    int direction, mismatch_sink& sink) {
    return impl_->inject_then_forward(
        *this, ctx, words, word_count, expected, first_word_index, nullptr, [&] {
            return inner_->verify_and_stream(ctx, words, word_count, expected, next,
                                             first_word_index, direction, sink);
        });
}

void fault_injecting_memory_access::stream_fill_strided(
    const access_context& ctx, std::uint64_t* span, std::size_t span_word_count,
    const strided_visits& visits, const pattern_spec& spec, std::uint64_t span_first_word_index) {
    inner_->stream_fill_strided(ctx, span, span_word_count, visits, spec, span_first_word_index);
}

std::uint64_t fault_injecting_memory_access::verify_strided(
    const access_context& ctx, const std::uint64_t* span, std::size_t span_word_count,
    const strided_visits& visits, const pattern_spec& expected, std::uint64_t span_first_word_index,
    mismatch_sink& sink) {
    return impl_->inject_then_forward(
        *this, ctx, span, span_word_count, expected, span_first_word_index, &visits, [&] {
            return inner_->verify_strided(ctx, span, span_word_count, visits, expected,
                                          span_first_word_index, sink);
        });
}

std::uint64_t fault_injecting_memory_access::reread_word(const access_context& ctx,
                                                         const std::uint64_t* word) noexcept {
    return inner_->reread_word(ctx, word);
}

IMemoryAccess* fault_injecting_memory_access::inner() noexcept {
    return inner_.get();
}

const IMemoryAccess* fault_injecting_memory_access::inner() const noexcept {
    return inner_.get();
}

const rq_fault_spec& fault_injecting_memory_access::spec() const noexcept {
    return spec_;
}

std::vector<rq_injected_fault> fault_injecting_memory_access::injection_log() const {
    const std::lock_guard<std::mutex> lock(impl_->log_mutex);
    return impl_->log;
}

std::uint64_t fault_injecting_memory_access::injection_count() const noexcept {
    return logged_.load(std::memory_order_acquire);
}

std::uint64_t fault_injecting_memory_access::verified_opportunity_count() const noexcept {
    return verified_.load(std::memory_order_acquire);
}

std::uint64_t fault_injecting_memory_access::retained_target_set_count() const noexcept {
    return impl_->retained_sets.load(std::memory_order_relaxed);
}

std::uint64_t fault_injecting_memory_access::built_target_set_count() const noexcept {
    return impl_->built_sets.load(std::memory_order_relaxed);
}

}  // namespace rq::engine::test
