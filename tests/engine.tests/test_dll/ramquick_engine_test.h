/**
 * @file ramquick_engine_test.h
 * @brief Test-only extensions of the RAMQuick engine C API, version 1.
 *
 * The C ABI of ramquick_engine_test.dll, the engine build used only by the native and managed
 * test suites. That DLL links engine_core.lib with the substitute memory-access layers and
 * exports the production API of ramquick_engine.h, compiled from the same src/engine/api/api.cpp,
 * plus the six rq_test_* functions declared here. It is never published:
 * scripts/publish-portable.ps1 verifies that it is absent from the portable folder.
 *
 * RamQuick.IntegrationTests binds the DLL once per process (EngineLibrary.Bind) and mirrors every
 * struct below byte for byte in TestEngineNative.cs. tests/engine.tests/fakes/fault_spec.h adds
 * C++ helpers over rq_fault_spec. This header is the only definition of rq_fault_spec,
 * rq_injected_fault and rq_struct_sizes; no other file may redefine them.
 *
 * @par Representation, threading and errors
 * The rules of ramquick_engine.h apply unchanged: __cdecl functions (RQ_CALL) exported under
 * their undecorated names (RQ_API); <stdint.h> fixed-width fields; #define'd uint32_t values
 * instead of C enums; booleans as uint32_t 0 or 1; natural alignment with explicit reserved
 * fields and no #pragma pack; struct_size first in top-level structs, and array-element structs
 * of a size fixed for RQ_API_VERSION. Every function is noexcept and converts any C++ exception
 * to RQ_E_INTERNAL; SEH faults are not caught. Sessions created here follow the production
 * threading rules and event order. None of the six functions raises a callback.
 */
#pragma once
#ifndef RAMQUICK_ENGINE_TEST_H
#define RAMQUICK_ENGINE_TEST_H

#include <stdint.h>
#include <stddef.h>
#include "ramquick_engine.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------------------------------------
 * Fault kinds and sentinels
 * ------------------------------------------------------------------------------------------- */

/**
 * @name Fault kinds (rq_fault_spec.kind)
 * The corruption classes the fault-injecting memory-access layer applies. A corruption is
 * applied, and logged, only after the last write to its word before that word's next
 * verification, so no logged corruption can be overwritten before it is checked: after a
 * window's streamed write and fence, after a hold step's slice write, between a moving-inversion
 * sweep's write and the next sweep's verification, after a turnaround block's write and before
 * its verification, and, for RQ_FAULT_DELAY_DEPENDENT, just before the verification load.
 * Injections ride on the engine's own verification opportunities: each adds one store per
 * corrupted word, and they add no extra verification or flush-and-reload loop.
 * @{
 */
/** No corruption: the layer passes every access through unchanged. */
#define RQ_FAULT_NONE 0u
/**
 * Bit rq_fault_spec.bit flipped in per_pass_count words per (step, pass), at seeded offsets
 * spread over the target range. The flipped value is stored, so the flushed re-read repeats it:
 * every report is expected with RQ_REREAD_REPEATS_ACTUAL.
 */
#define RQ_FAULT_SINGLE_BIT_FLIP 1u
/**
 * Bit rq_fault_spec.bit of the word at region_offset forced to (stuck_value & 1) at every
 * verification of that word. Only verifications whose expected bit differs from the forced
 * value can mismatch, so only those are logged; every report groups into one word.
 */
#define RQ_FAULT_STUCK_BIT 2u
/**
 * A random bit flipped in per_pass_count words per (step, pass), every one inside
 * [region_offset, region_offset + region_length).
 */
#define RQ_FAULT_ADDRESS_REGION 3u
/**
 * A random bit flipped, just before the verification load, in words whose time since write is
 * at least delay_threshold_ms. Only hold steps let data reach that age, so reports are expected
 * only with RQ_ACCESS_VERIFY_AFTER_HOLD.
 */
#define RQ_FAULT_DELAY_DEPENDENT 4u
/** @} */

/** Value of rq_fault_spec.bit that selects a seeded random bit for each injection (UINT32_MAX). */
#define RQ_FAULT_RANDOM_BIT 0xFFFFFFFFu

/** Value of rq_fault_spec.affinity_fail_worker meaning that no worker's pin fails (UINT32_MAX). */
#define RQ_FAULT_NO_WORKER 0xFFFFFFFFu

/**
 * Delay threshold in milliseconds applied when rq_fault_spec.delay_threshold_ms is 0. Equal to
 * k_default_delay_threshold_ms in tests/engine.tests/fakes/fault_spec.h.
 */
#define RQ_FAULT_DEFAULT_DELAY_THRESHOLD_MS 2000u

/* ---------------------------------------------------------------------------------------------
 * Structs
 * ------------------------------------------------------------------------------------------- */

/**
 * Fault specification passed to rq_test_session_create, which copies it, so the caller may free
 * it on return. Offsets are logical region byte offsets, as in rq_error_record.region_offset:
 * the worker slices concatenated in worker order. Target selection is a stateless function of
 * seed, step and pass, so the same (step, pass) always selects the same words for a given seed.
 * Kind RQ_FAULT_NONE ignores every injection field, so a spec may request only an affinity
 * failure.
 */
typedef struct rq_fault_spec {
    /** Set by the caller to sizeof(rq_fault_spec). */
    uint32_t struct_size;
    /** RQ_FAULT_* value. */
    uint32_t kind;
    /** Bit index 0-63 to corrupt, or RQ_FAULT_RANDOM_BIT for a seeded random bit per injection. */
    uint32_t bit;
    /**
     * Words corrupted per (step, pass), from 1 to 2^20; required for RQ_FAULT_SINGLE_BIT_FLIP,
     * RQ_FAULT_ADDRESS_REGION and RQ_FAULT_DELAY_DEPENDENT, ignored by RQ_FAULT_STUCK_BIT.
     */
    uint32_t per_pass_count;
    /**
     * Global cap on logged corruptions, >= 1 for every kind except RQ_FAULT_NONE. Tests size it
     * so the expected reports stay at or below 2,000, well under the 10,000-record detail cap, or
     * above 15,000 for the cap-exhaustion test.
     */
    uint64_t max_injections;
    /**
     * Start of the target range, below rq_session_config.region_bytes. For RQ_FAULT_STUCK_BIT,
     * the stuck word: the 8-byte word containing this offset, so callers pass an aligned offset.
     */
    uint64_t region_offset;
    /**
     * Length of the target range in bytes; 0 means the rest of the region from region_offset.
     * The range must hold at least one whole aligned 8-byte word. Targets beyond the bytes the
     * engine finally tests are never verified, so they are never injected or logged.
     */
    uint64_t region_length;
    /** RQ_FAULT_STUCK_BIT only: bit 0 is the value the stuck bit is forced to. */
    uint64_t stuck_value;
    /**
     * RQ_FAULT_DELAY_DEPENDENT only: minimum time since write in milliseconds; 0 means
     * RQ_FAULT_DEFAULT_DELAY_THRESHOLD_MS.
     */
    uint32_t delay_threshold_ms;
    /**
     * Index of the worker whose pin to its logical processor fails, below
     * rq_session_config.worker_count, or RQ_FAULT_NO_WORKER for none.
     */
    uint32_t affinity_fail_worker;
    /**
     * Windows error the failing pin reports, for example 87 (ERROR_INVALID_PARAMETER); nonzero
     * whenever affinity_fail_worker names a worker, since a pin returning 0 means success.
     */
    uint32_t affinity_fail_error;
    /** Reserved; set to 0. It aligns seed to 8 bytes. */
    uint32_t reserved0;
    /** Seed of target-word selection and of the random bit choice. */
    uint64_t seed;
} rq_fault_spec;

RQ_STATIC_ASSERT(sizeof(rq_fault_spec) == 72, "rq_fault_spec must be 72 bytes");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, max_injections) == 16,
                 "rq_fault_spec.max_injections offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, region_offset) == 24,
                 "rq_fault_spec.region_offset offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, region_length) == 32,
                 "rq_fault_spec.region_length offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, stuck_value) == 40, "rq_fault_spec.stuck_value offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, delay_threshold_ms) == 48,
                 "rq_fault_spec.delay_threshold_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, affinity_fail_worker) == 52,
                 "rq_fault_spec.affinity_fail_worker offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, reserved0) == 60, "rq_fault_spec.reserved0 offset");
RQ_STATIC_ASSERT(offsetof(rq_fault_spec, seed) == 64, "rq_fault_spec.seed offset");

/**
 * Ground truth of one applied corruption; an element of the array filled by
 * rq_test_get_injection_log. Its size is fixed for RQ_API_VERSION, so it carries no
 * struct_size.
 */
typedef struct rq_injected_fault {
    /**
     * Logical region byte offset of the corrupted 8-byte word, comparable with
     * (rq_error_record.region_offset & ~7).
     */
    uint64_t word_offset;
    /** Expected value XOR stored value; the reported rq_error_record.bit_mask must equal it. */
    uint64_t bit_mask;
    /** Index 0-63 of the corrupted bit. */
    uint32_t bit;
    /** 0-based index of the step whose verification the corruption targets. */
    uint32_t step_index;
    /** 0-based index of the pass whose verification the corruption targets. */
    uint32_t pass_index;
    /** RQ_ACCESS_* value of that verification. */
    uint32_t access_type;
    /**
     * Bool: 1 once the verification that follows the injection has run. It stays 0 only when a
     * stop or the end of the run intervened, and such entries are excluded from matching.
     */
    uint32_t verified_opportunity;
    /** Reserved; written as 0. */
    uint32_t reserved0;
} rq_injected_fault;

RQ_STATIC_ASSERT(sizeof(rq_injected_fault) == 40, "rq_injected_fault must be 40 bytes");
RQ_STATIC_ASSERT(offsetof(rq_injected_fault, bit_mask) == 8, "rq_injected_fault.bit_mask offset");
RQ_STATIC_ASSERT(offsetof(rq_injected_fault, bit) == 16, "rq_injected_fault.bit offset");
RQ_STATIC_ASSERT(offsetof(rq_injected_fault, access_type) == 28,
                 "rq_injected_fault.access_type offset");
RQ_STATIC_ASSERT(offsetof(rq_injected_fault, verified_opportunity) == 32,
                 "rq_injected_fault.verified_opportunity offset");
RQ_STATIC_ASSERT(offsetof(rq_injected_fault, reserved0) == 36,
                 "rq_injected_fault.reserved0 offset");

/**
 * The engine's sizeof of every ABI struct, filled by rq_test_struct_sizes. Managed tests
 * (InteropMarshallingTests) compare their mirrors' sizes with these values.
 */
typedef struct rq_struct_sizes {
    /** Set by the caller to sizeof(rq_struct_sizes). */
    uint32_t struct_size;
    /** sizeof(rq_system_info): 112. */
    uint32_t system_info;
    /** sizeof(rq_step_def): 96. */
    uint32_t step_def;
    /** sizeof(rq_session_config): 64. */
    uint32_t session_config;
    /** sizeof(rq_callbacks): 72. */
    uint32_t callbacks;
    /** sizeof(rq_prepared): 40. */
    uint32_t prepared;
    /** sizeof(rq_step_start): 80. */
    uint32_t step_start;
    /** sizeof(rq_step_result): 48. */
    uint32_t step_result;
    /** sizeof(rq_progress): 72. */
    uint32_t progress;
    /** sizeof(rq_error_record): 112. */
    uint32_t error_record;
    /** sizeof(rq_run_result): 192. */
    uint32_t run_result;
    /** sizeof(rq_fault_spec): 72. */
    uint32_t fault_spec;
    /** sizeof(rq_injected_fault): 40. */
    uint32_t injected_fault;
    /** Reserved; written as 0. */
    uint32_t reserved0;
} rq_struct_sizes;

RQ_STATIC_ASSERT(sizeof(rq_struct_sizes) == 56, "rq_struct_sizes must be 56 bytes");
RQ_STATIC_ASSERT(offsetof(rq_struct_sizes, fault_spec) == 44, "rq_struct_sizes.fault_spec offset");
RQ_STATIC_ASSERT(offsetof(rq_struct_sizes, injected_fault) == 48,
                 "rq_struct_sizes.injected_fault offset");
RQ_STATIC_ASSERT(offsetof(rq_struct_sizes, reserved0) == 52, "rq_struct_sizes.reserved0 offset");

/* ---------------------------------------------------------------------------------------------
 * Functions
 *
 * Rules common to every function below:
 * - It is callable from any thread. Every function except rq_test_session_create may also be
 *   called from inside a callback, as the managed step gate does with
 *   rq_test_pending_stop_reason.
 * - It raises no callback. A session created by rq_test_session_create raises the production
 *   callbacks once rq_session_start has been called.
 * - A handle argument is validated against the session registry; a destroyed handle is
 *   reported as RQ_E_INVALID_HANDLE, never dereferenced.
 * ------------------------------------------------------------------------------------------- */

/**
 * Validates a configuration and a fault specification and creates an idle test session.
 *
 * The contract of rq_session_create applies unchanged: the same validation of config and
 * callbacks, the same deep copy, an opaque handle that every production function accepts, and
 * destruction with rq_session_destroy. The fault specification is copied as well, so the caller
 * may free it on return. Allocates nothing large and starts no thread.
 *
 * The session's memory access is always the fault-injecting layer over the counting layer over
 * the real layer, also for a null spec or RQ_FAULT_NONE, which pass every access through, so
 * rq_test_get_independent_verified_bytes and rq_test_get_injection_log work on every test
 * session. A spec whose affinity_fail_worker names a worker makes that worker's pin fail with
 * affinity_fail_error: preparation ends the run with RQ_END_AFFINITY_FAILED, os_error set to
 * that error, and no memory traffic.
 *
 * @param config Session configuration; non-null.
 * @param callbacks Callback set; non-null.
 * @param fault Fault specification, or NULL for no fault (the same as RQ_FAULT_NONE).
 * @param out Receives the new handle; non-null. Set to NULL on failure.
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT for everything rq_session_create rejects and for a bad
 *         spec: a wrong struct_size, an unknown kind, a bit above 63 other than
 *         RQ_FAULT_RANDOM_BIT, a nonzero reserved0, a target range outside [0, region_bytes) or
 *         holding no whole word, max_injections 0 or per_pass_count outside 1 to 2^20 where the
 *         kind needs them, or an affinity_fail_worker not below worker_count or paired with
 *         affinity_fail_error 0; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_test_session_create(const rq_session_config* config,
                                                const rq_callbacks* callbacks,
                                                const rq_fault_spec* fault,
                                                rq_session_handle* out) RQ_NOEXCEPT;

/**
 * Reads the counting layer's total of bytes passed to the verification primitives: word_count x 8
 * for each verify and verify_and_stream call, and visit_count x 64 for each verify_strided call,
 * which compares only the 64-byte lines it visits rather than its whole span. Re-reads are
 * excluded. The layer counts independently of the engine's coverage counter, so tests compare
 * this total with rq_progress.verified_bytes and rq_run_result.verified_bytes, which must agree
 * within 1 %.
 *
 * Safe during and after a run, until rq_session_destroy; the total is read atomically.
 *
 * @param session A handle created by rq_test_session_create.
 * @param out Receives the byte total; non-null.
 * @return RQ_OK; RQ_E_INVALID_HANDLE for a null, unknown or destroyed handle;
 *         RQ_E_INVALID_ARGUMENT for a null out or a session not created by
 *         rq_test_session_create; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_test_get_independent_verified_bytes(rq_session_handle session,
                                                                uint64_t* out) RQ_NOEXCEPT;

/**
 * Copies the injection log: one rq_injected_fault per corruption the fault layer applied, in the
 * order applied, at most max_injections in all.
 *
 * Copies min(cap, total) entries into buffer and always writes the total logged to *count, so a
 * caller queries with cap 0 and a null buffer, allocates, then fetches. The log is consistent
 * once on_completed has been raised; during a run it is a snapshot, which later injections
 * extend and in which verified_opportunity may still be 0 for a pending verification. Valid
 * until rq_session_destroy.
 *
 * @param session A handle created by rq_test_session_create.
 * @param buffer Caller-owned array of cap entries; may be NULL only when cap is 0.
 * @param cap Number of entries buffer can hold.
 * @param count Receives the total number of entries logged, saturated at UINT32_MAX; non-null.
 * @return RQ_OK; RQ_E_INVALID_HANDLE for a null, unknown or destroyed handle;
 *         RQ_E_INVALID_ARGUMENT for a null count, a null buffer with cap > 0, or a session not
 *         created by rq_test_session_create; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_test_get_injection_log(rq_session_handle session,
                                                   rq_injected_fault* buffer, uint32_t cap,
                                                   uint32_t* count) RQ_NOEXCEPT;

/**
 * Returns the number of sessions in this DLL not yet destroyed, created by rq_session_create and
 * rq_test_session_create alike. SessionLifetimeTests asserts that it returns to 0 once abandoned
 * sessions have been finalised.
 *
 * Has no failure path and allocates nothing. The value is a snapshot that concurrent create and
 * destroy calls change.
 */
RQ_API uint32_t RQ_CALL rq_test_live_session_count(void) RQ_NOEXCEPT;

/**
 * Reads the end reason the session has recorded so far under the first-writer-wins rule, or 0
 * when none has been recorded yet.
 *
 * The value is an RQ_END_* value, never an RQ_STOP_* value: rq_session_stop records
 * RQ_STOP_USER, RQ_STOP_THERMAL and RQ_STOP_HOST_FAULT (1, 2, 3) as RQ_END_USER_STOP,
 * RQ_END_THERMAL and RQ_END_HOST_FAULT (3, 4, 5), and Stop on error records RQ_END_FIRST_ERROR
 * (2). Any other reason the engine records first, such as a fault, is reported as its own
 * RQ_END_* value. It lets the managed stop-ordering test observe that rq_session_stop recorded
 * its reason before the wrapper's managed stop event woke a waiting step gate.
 *
 * Works on every session, production or test, until rq_session_destroy.
 *
 * @param session Any session handle.
 * @param out Receives the RQ_END_* value or 0; non-null.
 * @return RQ_OK; RQ_E_INVALID_HANDLE for a null, unknown or destroyed handle;
 *         RQ_E_INVALID_ARGUMENT for a null out; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_test_pending_stop_reason(rq_session_handle session,
                                                     uint32_t* out) RQ_NOEXCEPT;

/**
 * Fills out with the engine's sizeof of every ABI struct and sets reserved0 to 0.
 *
 * Needs no session and allocates nothing.
 *
 * @param out Caller-owned; non-null, with struct_size set to sizeof(rq_struct_sizes).
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT for a null out or a wrong struct_size; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_test_struct_sizes(rq_struct_sizes* out) RQ_NOEXCEPT;

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* RAMQUICK_ENGINE_TEST_H */
