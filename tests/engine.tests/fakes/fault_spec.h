/**
 * @file fault_spec.h
 * @brief C++ helpers that build, normalize and validate rq_fault_spec for the native tests and
 *        the test-only engine DLL.
 *
 * rq_fault_spec, rq_injected_fault and the RQ_FAULT_* values are defined once, in
 * test_dll/ramquick_engine_test.h; this header only wraps them. It is test code: engine.tests.exe
 * and ramquick_engine_test.dll include it, ramquick_engine.dll never does, and no AVX kernel
 * translation unit includes it.
 *
 * The four fault classes follow the automated verification plan:
 * - RQ_FAULT_SINGLE_BIT_FLIP: a fixed bit (bit 17 in the tests) flipped in per_pass_count words
 *   per (step, pass), at seeded offsets spread over the target range, which defaults to the
 *   whole region. The expected top-ranked cause is VoltageSignalIntegrity.
 * - RQ_FAULT_STUCK_BIT: one bit of one word forced to (stuck_value & 1) at every verification of
 *   that word, logged only where the expected bit differs, so every report groups into one
 *   ErrorGroup (RankOrDimm).
 * - RQ_FAULT_ADDRESS_REGION: a random bit flipped in per_pass_count words inside a contiguous
 *   range, a 1/32 of the region in the tests (RankOrDimm).
 * - RQ_FAULT_DELAY_DEPENDENT: a random bit flipped just before the verification load in words
 *   whose time since write is at least delay_threshold_ms, which only hold verifications reach
 *   (RefreshRetention).
 *
 * Injections ride only on the engine's normal verification opportunities, after a word's last
 * write and before its next verification. The fault layer therefore adds no memory traffic, no
 * flush-and-reload loop and no repeated access to a small address set, which keeps fault-injection
 * runs inside the same stress bound as production runs.
 */
#pragma once

#include <cstdint>

#include "test_dll/ramquick_engine_test.h"

namespace rq::engine::test {

/** Minimum time since write of a delay-dependent injection when the spec leaves it at 0. */
inline constexpr uint32_t k_default_delay_threshold_ms = 2000;

/** rq_fault_spec.affinity_fail_worker value meaning that every worker's pin succeeds. */
inline constexpr uint32_t k_no_affinity_fail_worker = UINT32_MAX;

/** rq_fault_spec.bit value that selects a seeded random bit for each injection. */
inline constexpr uint32_t k_random_bit = UINT32_MAX;

/**
 * Upper bound of rq_fault_spec.per_pass_count. The fault layer builds its target set for each
 * (step, pass) in memory, so the bound keeps a mistyped spec from exhausting it.
 */
inline constexpr uint32_t k_max_targets_per_pass = 1u << 20;

// The C header and these constants describe the same ABI values; a drift on either side must
// fail the build rather than silently inject into the wrong bit or worker.
static_assert(k_default_delay_threshold_ms == RQ_FAULT_DEFAULT_DELAY_THRESHOLD_MS,
              "k_default_delay_threshold_ms must equal RQ_FAULT_DEFAULT_DELAY_THRESHOLD_MS");
static_assert(k_no_affinity_fail_worker == RQ_FAULT_NO_WORKER,
              "k_no_affinity_fail_worker must equal RQ_FAULT_NO_WORKER");
static_assert(k_random_bit == RQ_FAULT_RANDOM_BIT, "k_random_bit must equal RQ_FAULT_RANDOM_BIT");

namespace detail {

/** Byte size of the words the engine verifies and the fault layer corrupts. */
inline constexpr uint64_t k_word_bytes = 8;

/** Offset of the 8-byte word that contains byte offset, the stuck word of RQ_FAULT_STUCK_BIT. */
inline constexpr uint64_t containing_word_offset(uint64_t offset) noexcept {
    return offset & ~(k_word_bytes - 1);
}

/** The kinds that pick per_pass_count target words per (step, pass) from a target range. */
inline constexpr bool uses_target_range(uint32_t kind) noexcept {
    return kind == RQ_FAULT_SINGLE_BIT_FLIP || kind == RQ_FAULT_ADDRESS_REGION ||
           kind == RQ_FAULT_DELAY_DEPENDENT;
}

/** region_length after the zero-means-rest-of-region rule; 0 when the offset lies outside. */
inline constexpr uint64_t effective_region_length(uint64_t region_offset, uint64_t region_length,
                                                  uint64_t region_bytes) noexcept {
    if (region_length != 0) {
        return region_length;
    }
    return region_offset < region_bytes ? region_bytes - region_offset : 0;
}

/**
 * True when [offset, offset + length) contains at least one whole 8-byte-aligned word. The range
 * must lie inside the region, which fault_spec_failure checks first. The test uses the distance
 * to the next aligned offset and never forms offset + length, so no step can wrap.
 */
inline constexpr bool holds_whole_word(uint64_t offset, uint64_t length) noexcept {
    const uint64_t misalignment = offset & (k_word_bytes - 1);
    const uint64_t skip = misalignment == 0 ? 0 : k_word_bytes - misalignment;
    return length >= skip && length - skip >= k_word_bytes;
}

/** The first rule spec breaks, as a static English string, or nullptr when it is valid. */
inline const char* fault_spec_failure(const rq_fault_spec& spec, uint64_t region_bytes) noexcept {
    if (spec.struct_size != sizeof(rq_fault_spec)) {
        return "struct_size does not match rq_fault_spec";
    }
    if (spec.kind > RQ_FAULT_DELAY_DEPENDENT) {
        return "unknown fault kind";
    }
    if (spec.bit >= 64u && spec.bit != k_random_bit) {
        return "bit must be 0-63 or random";
    }
    // A pin that reports error 0 reads as success, so the requested failure would never happen.
    if (spec.affinity_fail_worker != k_no_affinity_fail_worker && spec.affinity_fail_error == 0) {
        return "affinity_fail_error must be a nonzero Windows error";
    }
    // RQ_FAULT_NONE ignores every injection field, so an affinity-only spec stays valid.
    if (spec.kind != RQ_FAULT_NONE) {
        if (region_bytes < k_word_bytes || spec.region_offset >= region_bytes) {
            return "region_offset outside the region";
        }
        // region_offset < region_bytes here, so the subtraction cannot wrap.
        if (spec.region_length != 0 && spec.region_length > region_bytes - spec.region_offset) {
            return "region exceeds the tested region";
        }
        if (spec.max_injections == 0) {
            return "max_injections must be at least 1";
        }
        if (uses_target_range(spec.kind)) {
            if (spec.per_pass_count == 0 || spec.per_pass_count > k_max_targets_per_pass) {
                return "per_pass_count must be 1..2^20";
            }
            const uint64_t length =
                effective_region_length(spec.region_offset, spec.region_length, region_bytes);
            if (!holds_whole_word(spec.region_offset, length)) {
                return "region holds no whole word";
            }
        } else if (region_bytes - containing_word_offset(spec.region_offset) < k_word_bytes) {
            // The stuck word is the one containing region_offset; when region_bytes is not a
            // multiple of 8 that word can straddle the end of the region, where no verification
            // reads it whole.
            return "region holds no whole word";
        }
    }
    if (spec.reserved0 != 0) {
        return "reserved0 must be zero";
    }
    return nullptr;
}

}  // namespace detail

/**
 * A spec of the given kind with every field at its neutral value: struct_size set,
 * delay_threshold_ms 2000, no failing worker, a random bit per injection, and every other field
 * 0.
 *
 * max_injections stays 0, which means "inject nothing". Callers must set it for every kind other
 * than RQ_FAULT_NONE: validate_fault_spec rejects a zero cap for those kinds, so a forgotten cap
 * fails loudly instead of producing a run that silently injects nothing.
 *
 * @param kind RQ_FAULT_* value; not checked here, validate_fault_spec does.
 */
inline rq_fault_spec make_fault_spec(uint32_t kind = RQ_FAULT_NONE) noexcept {
    rq_fault_spec spec{};
    spec.struct_size = static_cast<uint32_t>(sizeof(rq_fault_spec));
    spec.kind = kind;
    spec.bit = k_random_bit;
    spec.delay_threshold_ms = k_default_delay_threshold_ms;
    spec.affinity_fail_worker = k_no_affinity_fail_worker;
    return spec;
}

/**
 * A copy of spec with its defaults resolved, the form the fault-injecting layer expects:
 * - RQ_FAULT_STUCK_BIT: region_offset aligned down to its 8-byte word. The stuck word is the word
 *   that contains region_offset, which is why validate_fault_spec accepts an unaligned offset.
 * - region_length 0 becomes the rest of the region from region_offset (0 when region_offset is
 *   at or beyond region_bytes), so single-bit flips and delay-dependent faults spread over the
 *   whole region by default. For a stuck bit it is computed from the aligned offset, so the range
 *   holds the stuck word whole.
 * - delay_threshold_ms 0 becomes k_default_delay_threshold_ms.
 * No other field changes, and a spec that validates still validates once normalized.
 *
 * @param spec The spec as the caller passed it.
 * @param region_bytes rq_session_config.region_bytes, the planned region size. The engine may
 *        test fewer bytes once each slice is rounded down to 2 MiB or locking shrinks the region;
 *        targets beyond the tested bytes are never verified, so they are never injected or logged.
 */
inline rq_fault_spec normalized(const rq_fault_spec& spec, uint64_t region_bytes) noexcept {
    rq_fault_spec result = spec;
    if (result.kind == RQ_FAULT_STUCK_BIT) {
        result.region_offset = detail::containing_word_offset(result.region_offset);
    }
    result.region_length =
        detail::effective_region_length(result.region_offset, result.region_length, region_bytes);
    if (result.delay_threshold_ms == 0) {
        result.delay_threshold_ms = k_default_delay_threshold_ms;
    }
    return result;
}

/**
 * Checks spec against region_bytes, stopping at the first rule broken, in this order:
 * struct_size; kind; bit; the affinity pair; for every kind but RQ_FAULT_NONE the target range
 * and max_injections; for the target-range kinds per_pass_count and a whole word in the range
 * (for RQ_FAULT_STUCK_BIT, the stuck word inside the region); reserved0.
 *
 * Valid on a raw spec, as rq_test_session_create calls it before normalized(), and on a
 * normalized one. affinity_fail_worker is not compared with the worker count, which this
 * function does not know; rq_test_session_create checks that against the session config.
 *
 * @param spec The spec to check.
 * @param region_bytes rq_session_config.region_bytes.
 * @param reason When non-null, receives a static English string naming the broken rule, or
 *        nullptr when the spec is valid.
 * @return true when the spec is valid.
 */
inline bool validate_fault_spec(const rq_fault_spec& spec, uint64_t region_bytes,
                                const char** reason) noexcept {
    const char* failure = detail::fault_spec_failure(spec, region_bytes);
    if (reason != nullptr) {
        *reason = failure;
    }
    return failure == nullptr;
}

/** The snake_case name of an RQ_FAULT_* kind for test messages and RecordProperty. */
inline const char* fault_kind_name(uint32_t kind) noexcept {
    switch (kind) {
    case RQ_FAULT_NONE:
        return "none";
    case RQ_FAULT_SINGLE_BIT_FLIP:
        return "single_bit_flip";
    case RQ_FAULT_STUCK_BIT:
        return "stuck_bit";
    case RQ_FAULT_ADDRESS_REGION:
        return "address_region";
    case RQ_FAULT_DELAY_DEPENDENT:
        return "delay_dependent";
    default:
        return "unknown";
    }
}

}  // namespace rq::engine::test
