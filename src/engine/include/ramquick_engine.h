/**
 * @file ramquick_engine.h
 * @brief RAMQuick test engine public C API, version 1.
 *
 * The flat C ABI between the native test engine and its hosts. ramquick_engine.dll exports it
 * to the managed wrapper (RamQuick.Interop, which binds it with source-generated LibraryImport
 * and mirrors every struct byte for byte in NativeStructs.cs). The test-only
 * ramquick_engine_test.dll exports the same functions, compiled from the same api.cpp.
 *
 * @par Versioning
 * Any change to a struct layout, a value or a function or callback signature bumps
 * RQ_API_VERSION. The wrapper compares rq_api_version() with its own constant before any other
 * call and refuses to bind on a mismatch, so a mismatched DLL is reported as a corrupted
 * installation rather than misread. Published values are never renumbered.
 *
 * @par Representation rules
 * - Every function and every callback uses the __cdecl convention (RQ_CALL). Every function is
 *   exported by RQ_API under its undecorated name.
 * - Integers are <stdint.h> fixed-width types. Every enumeration field is a uint32_t carrying
 *   one of the #define'd RQ_* values, never a C enum, so its width is fixed on both sides of
 *   the boundary. Value 0 is invalid unless a group lists it. Every boolean is a uint32_t
 *   holding 0 or 1. Byte counts and measured times are uint64_t, configured times are uint32_t
 *   milliseconds, and rq_progress.remaining_ms is int64_t.
 * - Natural alignment, with no #pragma pack. The reserved fields put every uint64_t and every
 *   pointer on an 8-byte boundary and make every struct size a multiple of 8, so no implicit
 *   padding exists. Callers set reserved fields to 0; the engine writes them as 0. The size and
 *   the key offsets of every struct are checked at compile time by RQ_STATIC_ASSERT.
 * - Top-level structs begin with uint32_t struct_size, which the writer of the struct sets to
 *   its sizeof. Array-element structs (rq_step_def, rq_step_result, rq_error_record) carry no
 *   struct_size: their sizes are fixed for RQ_API_VERSION, and rq_session_config.step_def_size
 *   lets the engine validate the step array.
 * - Strings are UTF-8 and NUL-terminated within fixed char arrays: RQ_NAME_SIZE bytes for names
 *   and RQ_END_DETAIL_SIZE bytes for end_detail. The engine truncates longer text at a UTF-8
 *   character boundary.
 * - An array is a const T* paired with a uint32_t count. The pointer may be null only when the
 *   count is 0.
 *
 * @par Test time
 * rq_progress.elapsed_ms, rq_error_record.time_into_test_ms, rq_run_result.elapsed_ms and
 * rq_run_result.first_error_ms are "test time": milliseconds measured from the instant
 * preparation completes, immediately before on_prepared is raised. Preparation (allocating and
 * locking memory, creating and pinning workers) is reported separately as
 * rq_prepared.prepare_ms.
 *
 * @par Threading
 * rq_session_start creates one coordinator thread for the session and returns at once. The
 * coordinator allocates and locks the region in chunks, creates one pinned worker per
 * configured worker, sequences the steps, schedules passes and raises every callback. Workers
 * never call back, and callbacks never run concurrently with each other. rq_api_version,
 * rq_result_message and rq_query_system may be called from any thread. rq_session_stop may be
 * called from any thread at any time, including from inside a callback. Sessions in one process
 * may run concurrently, each on its own coordinator, so callbacks of different sessions may
 * overlap. The process has one minimum working set, and VirtualLock can lock 4 KiB pages only up
 * to it. A session raises it before its first 4 KiB-page chunk, by the bytes from that chunk to
 * the end of its region; large-page chunks before that one are outside the working set and add
 * nothing, so a region of large pages alone raises nothing. The raises of concurrent sessions
 * add up, with one headroom for the process's own pages; each is withdrawn once its session's
 * memory is freed, and the limits in force before the first raise return once the last is
 * withdrawn.
 *
 * @par Event order
 * create -> start -> prepared -> (step_starting -> step_finished, with progress* and errors*
 * between them)* -> completed -> released -> get_result -> destroy.
 * A preparation failure (RQ_END_INSUFFICIENT_MEMORY, RQ_END_AFFINITY_FAILED or
 * RQ_END_INTERNAL_FAULT) delivers completed and then released, with no prepared. A stop
 * recorded before start likewise delivers completed and then released straight away. released
 * is withheld only when a chunk cannot be freed (see rq_on_released).
 *
 * @par Error contract
 * Result-returning functions are noexcept and convert any C++ exception to RQ_E_INTERNAL, so no
 * C++ exception ever crosses this boundary. A C++ exception caught in the coordinator or in a
 * worker ends the run with RQ_END_INTERNAL_FAULT, with the exception's what() text in
 * rq_run_result.end_detail. Structured (SEH) exceptions such as an access violation, an illegal
 * instruction or an in-page error are deliberately neither caught nor translated: continuing
 * after one is unsafe, and under unstable memory the fault may itself be a symptom. They
 * terminate the process, and the host reports the unfinished run on its next launch through
 * journal recovery. rq_api_version and rq_result_message return constants and have no failure
 * path.
 */
#pragma once
#ifndef RAMQUICK_ENGINE_H
#define RAMQUICK_ENGINE_H

#include <stdint.h>
#include <stddef.h>

/** Calling convention of every exported function and every callback. */
#define RQ_CALL __cdecl

/**
 * Export attribute of every engine function. Defined unconditionally: no .def file exists, so
 * this attribute alone exports the API from both ramquick_engine.dll and
 * ramquick_engine_test.dll, which compile the same api.cpp. It is harmless in a translation
 * unit that only calls the API, such as the native test runner.
 */
#define RQ_API __declspec(dllexport)

/**
 * Exception specification of every exported function: noexcept in C++, nothing in C.
 *
 * The definitions in api.cpp are noexcept, and C++17 and later reject a redeclaration whose
 * exception specification differs from an earlier declaration, so the declarations here carry
 * the same specification. Callback typedefs deliberately carry none: hosts pass ordinary
 * function pointers, and a noexcept function converts to them implicitly.
 */
#ifdef __cplusplus
#define RQ_NOEXCEPT noexcept
#else
#define RQ_NOEXCEPT
#endif

/** Compile-time assertion usable from both C11 and C++. */
#ifdef __cplusplus
#define RQ_STATIC_ASSERT(cond, msg) static_assert(cond, msg)
#else
#define RQ_STATIC_ASSERT(cond, msg) _Static_assert(cond, msg)
#endif

/** Version of this API, returned by rq_api_version(). */
#define RQ_API_VERSION 1u

/** Size in bytes of every name field, including its NUL terminator (at most 63 UTF-8 bytes). */
#define RQ_NAME_SIZE 64

/** Size in bytes of rq_run_result.end_detail, including its NUL terminator. */
#define RQ_END_DETAIL_SIZE 128

/** Value of rq_run_result.first_error_ms when the run found no error. */
#define RQ_NO_FIRST_ERROR UINT64_MAX

#ifdef __cplusplus
extern "C" {
#endif

/* The product and this ABI are Windows x64 only; every layout below assumes 8-byte pointers. */
RQ_STATIC_ASSERT(sizeof(void*) == 8, "RAMQuick engine ABI is x64 only");

/* ---------------------------------------------------------------------------------------------
 * Handle and result codes
 * ------------------------------------------------------------------------------------------- */

/**
 * Opaque session handle, returned by rq_session_create and invalidated by rq_session_destroy.
 * struct rq_session_t is defined only inside the engine, which validates every handle it
 * receives against its session registry.
 */
typedef struct rq_session_t* rq_session_handle;

/** Result of every fallible function: RQ_OK or a negative RQ_E_* code. */
typedef int32_t rq_result;

/** Success. Managed mapping: success. */
#define RQ_OK 0

/**
 * A null or invalid pointer, a wrong struct_size or step_def_size, a bad count, an empty or
 * unterminated name, an out-of-range value, a zero budget, region_bytes below min_region_bytes,
 * an unavailable forced ISA or an invalid stop reason. Managed mapping: ArgumentException, a
 * programming error.
 */
#define RQ_E_INVALID_ARGUMENT (-1)

/**
 * A null handle, or one unknown to the session registry or already destroyed. Managed mapping:
 * ObjectDisposedException.
 */
#define RQ_E_INVALID_HANDLE (-2)

/**
 * The call is not valid in the session's current state: a second rq_session_start,
 * rq_session_get_result before Completed, or rq_session_destroy from inside a callback.
 * Managed mapping: InvalidOperationException.
 */
#define RQ_E_INVALID_STATE (-3)

/**
 * Not enough memory could be locked, even after shrinking to min_region_bytes. Not returned by
 * any v1 function: the condition arrives through on_completed as RQ_END_INSUFFICIENT_MEMORY,
 * with os_error and end_detail filled. Managed mapping: RunEndReason.InsufficientMemory, with
 * os_error and end_detail in EndDetail.
 */
#define RQ_E_INSUFFICIENT_MEMORY (-4)

/**
 * Locking failed at one size during shrink-and-retry (each retry is 90 % of the previous size,
 * down to min_region_bytes). Not returned by any v1 function, and v1 has no per-retry event:
 * rq_prepared.tested_bytes reports the size finally locked, so the host logs any shrink against
 * its planned region_bytes, and on final failure the engine may summarise the retries in
 * end_detail. Managed mapping: logged per retry; the final outcome is as for
 * RQ_E_INSUFFICIENT_MEMORY.
 */
#define RQ_E_LOCK_FAILED (-5)

/**
 * rq_session_start could not create the coordinator thread. A worker that cannot be created
 * during preparation instead ends the run with RQ_END_INTERNAL_FAULT and os_error. Managed
 * mapping: EngineException, reported as RunEndReason.EngineFault.
 */
#define RQ_E_THREAD_CREATE (-6)

/**
 * The engine's rq_api_version() differs from the version the host was built against. Never
 * returned by the engine; reserved for the wrapper's bind check. Managed mapping:
 * EngineException at bind, which the application reports as a corrupted installation.
 */
#define RQ_E_VERSION_MISMATCH (-7)

/**
 * A worker could not be pinned to its logical processor. Not returned by any v1 function: the
 * condition arrives through on_completed as RQ_END_AFFINITY_FAILED, with os_error and
 * end_detail filled. Managed mapping: RunEndReason.AffinityFailed, with EndDetail reading
 * "Windows error n: " followed by end_detail, where n is os_error.
 */
#define RQ_E_AFFINITY_FAILED (-8)

/**
 * A C++ exception was caught at the boundary. Managed mapping: EngineException carrying the
 * rq_result_message text; as an end reason (RQ_END_INTERNAL_FAULT), RunEndReason.EngineFault
 * with end_detail in EndDetail.
 */
#define RQ_E_INTERNAL (-99)

/* ---------------------------------------------------------------------------------------------
 * Value groups. Each is carried in a uint32_t field; 0 is invalid unless the group lists it.
 * The managed enums of the same meaning use the same numbers and convert by value: PatternKind,
 * AccessOrder, ThreadMode, InstructionSet (which has no member for RQ_ISA_AUTO), PageMode,
 * AddressKind, AccessType and RereadOutcome in RamQuick.Core, and StopReason and
 * StepGateDecision in RamQuick.Interop. End reasons are the exception: RamQuick.Core's
 * RunEndReason numbers its members differently, so a host maps each RQ_END_* value to the reason
 * of the same meaning explicitly, never by a cast (RQ_END_INTERNAL_FAULT is EngineFault).
 * ------------------------------------------------------------------------------------------- */

/**
 * @name Data patterns (rq_step_def.pattern)
 * Logical data values, each a stateless function of word index, pass and seed, so verification
 * and re-reads regenerate the expected value without storing it. Burst ordering, ECC and
 * controller data scrambling decide what the DQ lines actually carry.
 * @{
 */
/** 0x5555... and 0xAAAA... alternating per word, inverted every pass. */
#define RQ_PATTERN_CHECKERBOARD 1u
/** Moving inversion over walking ones (even passes) and walking zeros (odd passes). */
#define RQ_PATTERN_MOVING_INVERSION_WALKING 2u
/** Moving inversion over RQ_PATTERN_RANDOM base data. */
#define RQ_PATTERN_MOVING_INVERSION_RANDOM 3u
/** SplitMix64 of seed, word index and pass. */
#define RQ_PATTERN_RANDOM 4u
/** Complement between consecutive 64-bit words and 32-bit halves, alternating per pass. */
#define RQ_PATTERN_HIGH_TOGGLE 5u
/** @} */

/**
 * @name Access orders (rq_step_def.access_order)
 * Orders act on virtual addresses. Page mapping, channel and rank interleaving and bank hashing
 * are invisible to software, so these are exposure heuristics, not guaranteed physical
 * sequences. Moving-inversion patterns imply ascending-then-descending whole-slice sweeps and
 * ignore this field.
 * @{
 */
/** Ascending addresses, window by window. */
#define RQ_ORDER_SEQUENTIAL_ASCENDING 1u
/** Descending addresses, window by window. */
#define RQ_ORDER_SEQUENTIAL_DESCENDING 2u
/** 64-byte lines at 8 KiB, 64 KiB and 256 KiB strides, cycling per pass. */
#define RQ_ORDER_BANK_STRIDE 3u
/** A seeded permutation of 4 KiB blocks. */
#define RQ_ORDER_RANDOM_BLOCKS 4u
/** Write a 4 KiB block, then verify the block four positions behind it. */
#define RQ_ORDER_TURNAROUND 5u
/** @} */

/**
 * @name Thread modes (rq_step_def.thread_mode)
 * @{
 */
/** Every worker runs on its own slice. */
#define RQ_THREADS_ALL 1u
/** Only even-indexed workers run, each covering its own slice and its partner's. */
#define RQ_THREADS_HALF 2u
/** @} */

/**
 * @name Instruction sets (rq_session_config.isa_override, rq_system_info.isa_best,
 * rq_prepared.isa_selected)
 * In rq_system_info.isa_available_mask, bit n set means value n is available, for example
 * (1u << RQ_ISA_AVX2). AVX-512 requires the F, CD, BW, DQ and VL subsets plus OS-enabled ZMM
 * state; SSE2 is architectural on x64 and always available.
 * @{
 */
/** Select the best available set. Valid only in rq_session_config.isa_override. */
#define RQ_ISA_AUTO 0u
/** SSE2 baseline kernels; the reduced, slower path used only when AVX2 is absent. */
#define RQ_ISA_SSE2 1u
/** AVX2 kernels, the full-speed baseline. */
#define RQ_ISA_AVX2 2u
/** AVX-512 kernels. */
#define RQ_ISA_AVX512 3u
/** @} */

/**
 * @name Page modes (rq_prepared.page_mode)
 * @{
 */
/** Every chunk uses 4 KiB pages locked with VirtualLock. */
#define RQ_PAGE_MODE_STANDARD 1u
/** Every chunk uses large pages. */
#define RQ_PAGE_MODE_LARGE 2u
/** Some large-page chunks fell back to 4 KiB pages. */
#define RQ_PAGE_MODE_MIXED 3u
/** @} */

/**
 * @name Address kinds (rq_prepared.address_kind, rq_error_record.address_kind)
 * v1 always reports RQ_ADDRESS_KIND_VIRTUAL: no documented user-mode API yields physical
 * addresses.
 * @{
 */
/** rq_error_record.address is a virtual address in the engine process. */
#define RQ_ADDRESS_KIND_VIRTUAL 1u
/** Reserved for a physical address; never reported by v1. */
#define RQ_ADDRESS_KIND_PHYSICAL 2u
/** @} */

/**
 * @name Access types (rq_error_record.access_type)
 * The verification access that found the mismatch.
 * @{
 */
/** Windowed verification shortly after the window was written. */
#define RQ_ACCESS_VERIFY_AFTER_WRITE 1u
/** Whole-slice verification after an idle hold (rq_step_def.hold_ms > 0). */
#define RQ_ACCESS_VERIFY_AFTER_HOLD 2u
/** Verify-and-write sweep of a moving-inversion pass. */
#define RQ_ACCESS_READ_MODIFY_WRITE 3u
/** Turnaround verification of a block written just before, including the drain blocks. */
#define RQ_ACCESS_TURNAROUND_READ 4u
/** @} */

/**
 * @name Re-read outcomes (rq_error_record.reread_outcome)
 * Every mismatching word is re-read once: fence, flush of its cache line, fence, one load. The
 * outcome classifies the raw re-read value against expected and actual. Every outcome is a
 * confirmed error. Interpreting an outcome (likely read-path, write-or-storage or transient
 * fault) is an inference the host makes and labels as such; the engine makes none.
 * @{
 */
/** The re-read value equals the expected value. */
#define RQ_REREAD_MATCHES_EXPECTED 1u
/** The re-read value equals the first, wrong value. */
#define RQ_REREAD_REPEATS_ACTUAL 2u
/** The re-read value equals neither. */
#define RQ_REREAD_DIFFERS 3u
/** @} */

/**
 * @name Stop reasons (the stop_reason argument of rq_session_stop)
 * Each maps to the end reason of the same meaning: RQ_STOP_USER to RQ_END_USER_STOP,
 * RQ_STOP_THERMAL to RQ_END_THERMAL and RQ_STOP_HOST_FAULT to RQ_END_HOST_FAULT.
 * @{
 */
/** The user pressed Stop or closed the window. */
#define RQ_STOP_USER 1u
/** A temperature reading reached its thermal limit. */
#define RQ_STOP_THERMAL 2u
/** The host can no longer support the run, for example its progress journal failed. */
#define RQ_STOP_HOST_FAULT 3u
/** @} */

/**
 * @name End reasons (rq_run_result.end_reason)
 * The end reason is first-writer-wins: the first of a stop request, the engine's own
 * RQ_END_FIRST_ERROR (Stop on error) and a fault to be recorded is kept, and it alone decides
 * the terminal state. os_error (where applicable) and end_detail are filled for every reason
 * except RQ_END_COMPLETED.
 * @{
 */
/** Every step ran to its schedule. PASS when total_errors is 0, otherwise FAIL. */
#define RQ_END_COMPLETED 1u
/** Stop on error was set and a worker confirmed a mismatch. */
#define RQ_END_FIRST_ERROR 2u
/** Stopped by RQ_STOP_USER. */
#define RQ_END_USER_STOP 3u
/** Stopped by RQ_STOP_THERMAL. */
#define RQ_END_THERMAL 4u
/** Stopped by RQ_STOP_HOST_FAULT, or a step gate aborted with no stop reason recorded. */
#define RQ_END_HOST_FAULT 5u
/**
 * Locking failed even at min_region_bytes; os_error holds the last Windows error. The engine
 * never tests fewer than min_region_bytes: a region whose 2 MiB-rounded slices test fewer ends
 * with this reason before anything is locked, with os_error 0. Locked pages are resident, so a
 * region whose rounded slices test more than total_phys_bytes ends the same way.
 */
#define RQ_END_INSUFFICIENT_MEMORY 6u
/** SetThreadGroupAffinity failed for a worker; no traffic ran. os_error holds the error. */
#define RQ_END_AFFINITY_FAILED 7u
/** A C++ exception or thread-creation failure inside the engine. */
#define RQ_END_INTERNAL_FAULT 8u
/** @} */

/**
 * @name Step gate actions (the return value of rq_on_step_starting)
 * @{
 */
/** Run the step. */
#define RQ_ACTION_CONTINUE 0u
/** Start no traffic for the step and end the run. */
#define RQ_ACTION_ABORT 1u
/** @} */

/* ---------------------------------------------------------------------------------------------
 * System probe and session configuration
 * ------------------------------------------------------------------------------------------- */

/** Machine description filled by rq_query_system. */
typedef struct rq_system_info {
    /** Set by the caller to sizeof(rq_system_info). */
    uint32_t struct_size;
    /** Logical processors across all processor groups. */
    uint32_t logical_processors;
    /** Processor groups; a group holds at most 64 logical processors. */
    uint32_t processor_groups;
    /** Bit n set when RQ_ISA_* value n is available. */
    uint32_t isa_available_mask;
    /** Best available RQ_ISA_* value; never RQ_ISA_AUTO. */
    uint32_t isa_best;
    /** Bool: the token holds SeLockMemoryPrivilege. Observed only; the probe enables nothing. */
    uint32_t lock_privilege_held;
    /** Total physical memory in bytes. */
    uint64_t total_phys_bytes;
    /** Available physical memory in bytes at the time of the call. */
    uint64_t avail_phys_bytes;
    /** Large-page granularity in bytes, or 0 when large pages are unsupported. */
    uint64_t large_page_min_bytes;
    /** CPUID processor brand string. */
    char cpu_name[RQ_NAME_SIZE];
} rq_system_info;

RQ_STATIC_ASSERT(sizeof(rq_system_info) == 112, "rq_system_info must be 112 bytes");
RQ_STATIC_ASSERT(offsetof(rq_system_info, total_phys_bytes) == 24,
                 "rq_system_info.total_phys_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_system_info, avail_phys_bytes) == 32,
                 "rq_system_info.avail_phys_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_system_info, large_page_min_bytes) == 40,
                 "rq_system_info.large_page_min_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_system_info, cpu_name) == 48, "rq_system_info.cpu_name offset");

/**
 * One step of a test plan; an array element of rq_session_config.steps. Its size is fixed for
 * RQ_API_VERSION, so it carries no struct_size; rq_session_config.step_def_size must equal
 * sizeof(rq_step_def).
 */
typedef struct rq_step_def {
    /** Non-empty, NUL-terminated, at most 63 UTF-8 bytes; shown to the user verbatim. */
    char name[RQ_NAME_SIZE];
    /** RQ_PATTERN_* value. */
    uint32_t pattern;
    /** RQ_ORDER_* value; ignored by the moving-inversion patterns. */
    uint32_t access_order;
    /** RQ_THREADS_* value. */
    uint32_t thread_mode;
    /**
     * 0 for a windowed step. A value > 0 makes each pass write the whole slice, wait this many
     * milliseconds on the stop event, then verify the whole slice.
     */
    uint32_t hold_ms;
    /** Time budget in milliseconds, > 0. Passes are scheduled against it. */
    uint32_t time_budget_ms;
    /**
     * Coverage floor in hundredths of a percent (basis points), so 25 % is 2500; > 0. It is
     * reported and summed into the plan's coverage target, and it never extends a step: time
     * governs, so fail-fast ordering is never traded for coverage.
     */
    uint32_t coverage_budget_bp;
    /** Reserved; set to 0. */
    uint32_t reserved0;
    /** Reserved; set to 0. */
    uint32_t reserved1;
} rq_step_def;

RQ_STATIC_ASSERT(sizeof(rq_step_def) == 96, "rq_step_def must be 96 bytes");
RQ_STATIC_ASSERT(offsetof(rq_step_def, name) == 0, "rq_step_def.name offset");
RQ_STATIC_ASSERT(offsetof(rq_step_def, pattern) == 64, "rq_step_def.pattern offset");
RQ_STATIC_ASSERT(offsetof(rq_step_def, time_budget_ms) == 80, "rq_step_def.time_budget_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_step_def, reserved1) == 92, "rq_step_def.reserved1 offset");

/**
 * Session configuration passed to rq_session_create. The engine deep-copies it together with
 * the step array, the step names and the callbacks, so the caller may free its copies as soon
 * as rq_session_create returns.
 */
typedef struct rq_session_config {
    /** Set by the caller to sizeof(rq_session_config). */
    uint32_t struct_size;
    /** Set by the caller to sizeof(rq_step_def). */
    uint32_t step_def_size;
    /**
     * Planned bytes under test. The engine rounds each worker's slice down to 2 MiB. A region
     * whose rounded slices test more than rq_system_info.total_phys_bytes ends with
     * RQ_END_INSUFFICIENT_MEMORY before anything is locked.
     */
    uint64_t region_bytes;
    /**
     * Floor for shrink-and-retry when locking fails; > 0 and <= region_bytes. The engine never
     * tests fewer bytes: a region whose 2 MiB-rounded slices test fewer ends with
     * RQ_END_INSUFFICIENT_MEMORY before anything is locked.
     */
    uint64_t min_region_bytes;
    /** Seed of the random patterns and of the RQ_ORDER_RANDOM_BLOCKS permutation. */
    uint64_t seed;
    /** Workers, from 1 to rq_system_info.logical_processors; one per logical processor. */
    uint32_t worker_count;
    /** Bool: halt the run on the first confirmed error (RQ_END_FIRST_ERROR). */
    uint32_t stop_on_error;
    /** Most detailed error records delivered through on_errors per run (10,000 in the app). */
    uint32_t error_detail_cap;
    /** Interval between on_progress calls in milliseconds, > 0 (250 for 4 Hz). */
    uint32_t progress_interval_ms;
    /** RQ_ISA_* value: RQ_ISA_AUTO, or a forced set that must be available. */
    uint32_t isa_override;
    /** Entries in steps, >= 1. */
    uint32_t step_count;
    /** Ordered steps of the plan. */
    const rq_step_def* steps;
} rq_session_config;

RQ_STATIC_ASSERT(sizeof(rq_session_config) == 64, "rq_session_config must be 64 bytes");
RQ_STATIC_ASSERT(offsetof(rq_session_config, region_bytes) == 8,
                 "rq_session_config.region_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_session_config, min_region_bytes) == 16,
                 "rq_session_config.min_region_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_session_config, seed) == 24, "rq_session_config.seed offset");
RQ_STATIC_ASSERT(offsetof(rq_session_config, worker_count) == 32,
                 "rq_session_config.worker_count offset");
RQ_STATIC_ASSERT(offsetof(rq_session_config, step_count) == 52,
                 "rq_session_config.step_count offset");
RQ_STATIC_ASSERT(offsetof(rq_session_config, steps) == 56, "rq_session_config.steps offset");

/* ---------------------------------------------------------------------------------------------
 * Callback payloads. Engine-owned and valid only during the callback that receives them.
 * ------------------------------------------------------------------------------------------- */

/** Payload of on_prepared: the region is locked and the workers are pinned. */
typedef struct rq_prepared {
    /** sizeof(rq_prepared). */
    uint32_t struct_size;
    /** Workers created and pinned. */
    uint32_t worker_count;
    /** RQ_PAGE_MODE_* value. */
    uint32_t page_mode;
    /** RQ_ISA_* value selected for the session; never RQ_ISA_AUTO. */
    uint32_t isa_selected;
    /** RQ_ADDRESS_KIND_* value of reported addresses; RQ_ADDRESS_KIND_VIRTUAL in v1. */
    uint32_t address_kind;
    /** Reserved; written as 0. */
    uint32_t reserved0;
    /**
     * Bytes under test: slice x workers, after 2 MiB slice rounding and any lock shrink. The
     * denominator of coverage.
     */
    uint64_t tested_bytes;
    /** Preparation time in milliseconds, from start to this callback. */
    uint64_t prepare_ms;
} rq_prepared;

RQ_STATIC_ASSERT(sizeof(rq_prepared) == 40, "rq_prepared must be 40 bytes");
RQ_STATIC_ASSERT(offsetof(rq_prepared, tested_bytes) == 24, "rq_prepared.tested_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_prepared, prepare_ms) == 32, "rq_prepared.prepare_ms offset");

/** Payload of on_step_starting. */
typedef struct rq_step_start {
    /** sizeof(rq_step_start). */
    uint32_t struct_size;
    /** 0-based index into rq_session_config.steps. */
    uint32_t index;
    /** Steps in the plan. */
    uint32_t count;
    /** Reserved; written as 0. */
    uint32_t reserved0;
    /** The step's name, as configured. */
    char name[RQ_NAME_SIZE];
} rq_step_start;

RQ_STATIC_ASSERT(sizeof(rq_step_start) == 80, "rq_step_start must be 80 bytes");
RQ_STATIC_ASSERT(offsetof(rq_step_start, name) == 16, "rq_step_start.name offset");

/**
 * Outcome of one started step: the payload of on_step_finished and an element of
 * rq_run_result.step_results. Its size is fixed for RQ_API_VERSION, so it carries no
 * struct_size.
 */
typedef struct rq_step_result {
    /** 0-based index into rq_session_config.steps. */
    uint32_t index;
    /** Bool: every scheduled pass ran without a stop. */
    uint32_t completed;
    /** Passes completed. */
    uint32_t passes;
    /** Reserved; written as 0. */
    uint32_t reserved0;
    /** Measured duration of the step in milliseconds. */
    uint64_t actual_ms;
    /** max(0, actual_ms - time_budget_ms); nonzero when a single pass outgrew the budget. */
    uint64_t overrun_ms;
    /** Bytes verified in this step. */
    uint64_t verified_bytes;
    /** True error count of this step; never capped. */
    uint64_t errors;
} rq_step_result;

RQ_STATIC_ASSERT(sizeof(rq_step_result) == 48, "rq_step_result must be 48 bytes");
RQ_STATIC_ASSERT(offsetof(rq_step_result, actual_ms) == 16, "rq_step_result.actual_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_step_result, overrun_ms) == 24, "rq_step_result.overrun_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_step_result, verified_bytes) == 32,
                 "rq_step_result.verified_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_step_result, errors) == 40, "rq_step_result.errors offset");

/** Payload of on_progress. */
typedef struct rq_progress {
    /** sizeof(rq_progress). */
    uint32_t struct_size;
    /** 0-based index of the running step. */
    uint32_t step_index;
    /** Steps in the plan. */
    uint32_t step_count;
    /** 0-based index of the pass in progress. */
    uint32_t pass_index;
    /**
     * Current schedule of the step, max(1, round(time_budget / mean pass duration)); it can
     * change between ticks as pass durations are measured.
     */
    uint32_t passes_planned;
    /** Reserved; written as 0. */
    uint32_t reserved0;
    /** Test time in milliseconds (see "Test time" above). */
    uint64_t elapsed_ms;
    /** Milliseconds since the running step started. */
    uint64_t step_elapsed_ms;
    /**
     * Estimated milliseconds remaining, or -1 ("calculating") until step 1 completes its first
     * pass. Then the running step's term plus the time budgets of every later step; the term
     * is max(0, time_budget - step_elapsed) before the step's first pass completes and
     * max(0, passes_planned x mean pass - step_elapsed) after it.
     */
    int64_t remaining_ms;
    /** Bytes verified so far in the run. */
    uint64_t verified_bytes;
    /** Bytes under test; equal to rq_prepared.tested_bytes. */
    uint64_t region_bytes;
    /** True error total so far; never capped. */
    uint64_t total_errors;
} rq_progress;

RQ_STATIC_ASSERT(sizeof(rq_progress) == 72, "rq_progress must be 72 bytes");
RQ_STATIC_ASSERT(offsetof(rq_progress, elapsed_ms) == 24, "rq_progress.elapsed_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_progress, step_elapsed_ms) == 32,
                 "rq_progress.step_elapsed_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_progress, remaining_ms) == 40, "rq_progress.remaining_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_progress, verified_bytes) == 48, "rq_progress.verified_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_progress, region_bytes) == 56, "rq_progress.region_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_progress, total_errors) == 64, "rq_progress.total_errors offset");

/**
 * One confirmed error; an element of the on_errors batch. Its size is fixed for RQ_API_VERSION,
 * so it carries no struct_size.
 */
typedef struct rq_error_record {
    /** Confirmation order, starting at 0 in each session. */
    uint64_t sequence;
    /** Address of the mismatching 8-byte word, of kind address_kind. */
    uint64_t address;
    /**
     * Logical byte offset of the word in [0, tested_bytes). The tested region is the worker
     * slices concatenated in worker order, each slice its chunks in order. Chunks are separate
     * allocations, so this is not address minus a base address.
     */
    uint64_t region_offset;
    /** Value the word should hold. */
    uint64_t expected;
    /** Value the verification load returned. */
    uint64_t actual;
    /** Value of the flushed re-read. */
    uint64_t reread;
    /** expected XOR actual. */
    uint64_t bit_mask;
    /**
     * Microseconds from the end of the stream that last wrote the word's range to the
     * verification load, including any hold. A prefetch fill after the flush can precede the
     * load, so this can overstate the data's age by that interval.
     */
    uint64_t time_since_write_us;
    /** Test time of the confirmation in milliseconds (see "Test time" above). */
    uint64_t time_into_test_ms;
    /** 0-based index of the step. */
    uint32_t step_index;
    /** 0-based index of the pass. */
    uint32_t pass_index;
    /** RQ_ACCESS_* value. */
    uint32_t access_type;
    /** RQ_REREAD_* value. */
    uint32_t reread_outcome;
    /** RQ_ADDRESS_KIND_* value of address. */
    uint32_t address_kind;
    /** 0-based index of the worker that verified the word. */
    uint32_t worker_index;
    /** The worker's processor number within its processor group. */
    uint32_t logical_processor;
    /** The worker's processor group. */
    uint32_t processor_group;
    /** Workers active in the step: all of them, or half in RQ_THREADS_HALF. */
    uint32_t active_workers;
    /** Reserved; written as 0. */
    uint32_t reserved0;
} rq_error_record;

RQ_STATIC_ASSERT(sizeof(rq_error_record) == 112, "rq_error_record must be 112 bytes");
RQ_STATIC_ASSERT(offsetof(rq_error_record, sequence) == 0, "rq_error_record.sequence offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, address) == 8, "rq_error_record.address offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, region_offset) == 16,
                 "rq_error_record.region_offset offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, expected) == 24, "rq_error_record.expected offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, actual) == 32, "rq_error_record.actual offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, reread) == 40, "rq_error_record.reread offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, bit_mask) == 48, "rq_error_record.bit_mask offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, time_since_write_us) == 56,
                 "rq_error_record.time_since_write_us offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, time_into_test_ms) == 64,
                 "rq_error_record.time_into_test_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, step_index) == 72, "rq_error_record.step_index offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, logical_processor) == 96,
                 "rq_error_record.logical_processor offset");
RQ_STATIC_ASSERT(offsetof(rq_error_record, reserved0) == 108, "rq_error_record.reserved0 offset");

/** Run outcome: the payload of on_completed, and the header copied by rq_session_get_result. */
typedef struct rq_run_result {
    /** sizeof(rq_run_result); set by the caller of rq_session_get_result. */
    uint32_t struct_size;
    /** RQ_END_* value: the first recorded end reason. */
    uint32_t end_reason;
    /** Steps whose result has completed = 1. */
    uint32_t steps_completed;
    /** Entries in step_results: one per step whose gate returned RQ_ACTION_CONTINUE. */
    uint32_t step_result_count;
    /** True error total; never capped. */
    uint64_t total_errors;
    /** Bytes verified in the run. */
    uint64_t verified_bytes;
    /** Test time at completion in milliseconds (see "Test time" above); 0 if preparation failed. */
    uint64_t elapsed_ms;
    /** Test time of the first confirmed error in milliseconds, or RQ_NO_FIRST_ERROR. */
    uint64_t first_error_ms;
    /**
     * step_result_count results in step order. Engine-owned: during on_completed valid only for
     * the call; from rq_session_get_result valid until rq_session_destroy.
     */
    const rq_step_result* step_results;
    /**
     * GetLastError code of the failing call for RQ_END_AFFINITY_FAILED,
     * RQ_END_INSUFFICIENT_MEMORY and thread-creation faults; otherwise 0.
     */
    uint32_t os_error;
    /** Reserved; written as 0. */
    uint32_t reserved0;
    /**
     * Short English diagnostic for every end reason except RQ_END_COMPLETED, where it is empty;
     * for example "SetThreadGroupAffinity failed: group 1, processor 12", or the what() text of
     * a caught C++ exception.
     */
    char end_detail[RQ_END_DETAIL_SIZE];
} rq_run_result;

RQ_STATIC_ASSERT(sizeof(rq_run_result) == 192, "rq_run_result must be 192 bytes");
RQ_STATIC_ASSERT(offsetof(rq_run_result, total_errors) == 16, "rq_run_result.total_errors offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, verified_bytes) == 24,
                 "rq_run_result.verified_bytes offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, elapsed_ms) == 32, "rq_run_result.elapsed_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, first_error_ms) == 40,
                 "rq_run_result.first_error_ms offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, step_results) == 48, "rq_run_result.step_results offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, os_error) == 56, "rq_run_result.os_error offset");
RQ_STATIC_ASSERT(offsetof(rq_run_result, end_detail) == 64, "rq_run_result.end_detail offset");

/* ---------------------------------------------------------------------------------------------
 * Callbacks
 *
 * Rules common to every callback:
 * - It runs on the session's coordinator thread, never on a worker, and never concurrently with
 *   another callback of the same session.
 * - Payload pointers, and the arrays they reference, are engine-owned and valid only during the
 *   call. Copy whatever is kept.
 * - It must not let any exception, C++ or managed, unwind into the engine.
 * - It may call rq_session_stop, and rq_session_get_result once on_completed has been raised. It
 *   must not call rq_session_destroy, which returns RQ_E_INVALID_STATE there.
 * - user_data is rq_callbacks.user_data, passed back unchanged.
 * ------------------------------------------------------------------------------------------- */

/**
 * Raised once when preparation succeeds, at the origin of test time. Not raised when
 * preparation fails or a stop was recorded before start. See the common rules above.
 */
typedef void(RQ_CALL* rq_on_prepared)(void* user_data, const rq_prepared* info);

/**
 * The step gate, raised before any memory traffic of each step. It may block, for example while
 * the host makes a journal record durable (the application waits up to 5 s), and the 2 s stop
 * bound assumes it returns promptly once the host has requested a stop. RQ_ACTION_CONTINUE runs
 * the step. Any other value is treated as RQ_ACTION_ABORT: the step starts no traffic, gets no
 * on_step_finished and no step_results entry, and the run ends with the pending stop reason, or
 * RQ_END_HOST_FAULT when none has been recorded. See the common rules above.
 *
 * @return RQ_ACTION_CONTINUE or RQ_ACTION_ABORT.
 */
typedef uint32_t(RQ_CALL* rq_on_step_starting)(void* user_data, const rq_step_start* info);

/**
 * Raised once for every step whose gate returned RQ_ACTION_CONTINUE, including a step cut short
 * by a stop (completed = 0). See the common rules above.
 */
typedef void(RQ_CALL* rq_on_step_finished)(void* user_data, const rq_step_result* result);

/**
 * Raised every rq_session_config.progress_interval_ms while a step runs. See the common rules
 * above.
 */
typedef void(RQ_CALL* rq_on_progress)(void* user_data, const rq_progress* progress);

/**
 * Raised at most every 100 ms with the detailed records confirmed since the previous batch.
 * Once error_detail_cap records have been delivered, count may be 0 and records null while
 * total_errors keeps rising. total_errors is always the exact true total. See the common rules
 * above.
 */
typedef void(RQ_CALL* rq_on_errors)(void* user_data, const rq_error_record* records, uint32_t count,
                                    uint64_t total_errors);

/**
 * Raised exactly once for every started session, after every worker has joined. The 2 s stop
 * bound runs from a stop request to this callback. Memory is released after it returns. See
 * the common rules above.
 */
typedef void(RQ_CALL* rq_on_completed)(void* user_data, const rq_run_result* result);

/**
 * Raised once, after on_completed, when every chunk has been unlocked and freed. release_ms
 * grows with the region size; it is reported, not bounded. Not raised when Windows refuses to
 * free a chunk (VirtualFree fails), because the release cannot be confirmed: the session keeps
 * owning that chunk and retries it in rq_session_destroy, and the host proceeds as for a
 * Released that never arrives. A chunk still refused when rq_session_destroy returns passes to
 * a process-wide owner that every later release in the process retries, and Windows reclaims
 * it at process exit at the latest; on_released stays withheld for that session. See the common
 * rules above.
 */
typedef void(RQ_CALL* rq_on_released)(void* user_data, uint64_t released_bytes,
                                      uint64_t release_ms);

/**
 * Callback set passed to rq_session_create, which copies it. All seven function pointers are
 * required; rq_session_create rejects a null one with RQ_E_INVALID_ARGUMENT.
 */
typedef struct rq_callbacks {
    /** Set by the caller to sizeof(rq_callbacks). */
    uint32_t struct_size;
    /** Reserved; set to 0. */
    uint32_t reserved0;
    /**
     * Opaque to the engine and passed to every callback. Whatever it refers to must stay valid
     * until rq_session_destroy returns.
     */
    void* user_data;
    /** See rq_on_prepared. */
    rq_on_prepared on_prepared;
    /** See rq_on_step_starting. */
    rq_on_step_starting on_step_starting;
    /** See rq_on_step_finished. */
    rq_on_step_finished on_step_finished;
    /** See rq_on_progress. */
    rq_on_progress on_progress;
    /** See rq_on_errors. */
    rq_on_errors on_errors;
    /** See rq_on_completed. */
    rq_on_completed on_completed;
    /** See rq_on_released. */
    rq_on_released on_released;
} rq_callbacks;

RQ_STATIC_ASSERT(sizeof(rq_callbacks) == 72, "rq_callbacks must be 72 bytes");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, user_data) == 8, "rq_callbacks.user_data offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_prepared) == 16, "rq_callbacks.on_prepared offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_step_starting) == 24,
                 "rq_callbacks.on_step_starting offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_step_finished) == 32,
                 "rq_callbacks.on_step_finished offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_progress) == 40, "rq_callbacks.on_progress offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_errors) == 48, "rq_callbacks.on_errors offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_completed) == 56, "rq_callbacks.on_completed offset");
RQ_STATIC_ASSERT(offsetof(rq_callbacks, on_released) == 64, "rq_callbacks.on_released offset");

/* ---------------------------------------------------------------------------------------------
 * Functions
 * ------------------------------------------------------------------------------------------- */

/**
 * Returns RQ_API_VERSION. The host calls it first and refuses to bind on a mismatch.
 *
 * Constant, with no failure path; allocates nothing; callable from any thread.
 */
RQ_API uint32_t RQ_CALL rq_api_version(void) RQ_NOEXCEPT;

/**
 * Describes the machine: processor topology, instruction sets, physical memory, large-page
 * granularity and whether SeLockMemoryPrivilege is held.
 *
 * Has no side effects: the privilege is observed, not enabled. Needs no session; callable from
 * any thread.
 *
 * @param out Caller-owned; non-null, with struct_size set to sizeof(rq_system_info).
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT for a null out or a wrong struct_size; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_query_system(rq_system_info* out) RQ_NOEXCEPT;

/**
 * Validates a configuration and creates an idle session.
 *
 * Validation covers struct_size and step_def_size, the step count and the step array, every
 * name (non-empty and NUL-terminated within RQ_NAME_SIZE), every value range, time and coverage
 * budgets > 0, region_bytes >= min_region_bytes > 0, worker_count, progress_interval_ms > 0,
 * isa_override (RQ_ISA_AUTO or an available set), and the callback set (struct_size and seven
 * non-null pointers). The configuration, the step array, the names and the callback struct are
 * deep-copied, so the caller may free its copies on return. Allocates nothing large and starts
 * no thread.
 *
 * @param config Session configuration; non-null.
 * @param callbacks Callback set; non-null.
 * @param out_session Receives the new handle; non-null. Set to NULL on failure.
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_session_create(const rq_session_config* config,
                                           const rq_callbacks* callbacks,
                                           rq_session_handle* out_session) RQ_NOEXCEPT;

/**
 * Starts the session's coordinator thread and returns immediately; preparation, the steps and
 * every callback follow on that thread. Allowed once per session.
 *
 * If a stop was recorded before start, the coordinator raises on_completed, with end_reason
 * mapped from the first recorded stop reason, and then on_released straight away, with no
 * on_prepared.
 *
 * @return RQ_OK; RQ_E_INVALID_HANDLE; RQ_E_INVALID_STATE when already started;
 *         RQ_E_THREAD_CREATE when the coordinator cannot be created; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_session_start(rq_session_handle session) RQ_NOEXCEPT;

/**
 * Requests a stop. Idempotent and non-blocking; callable from any thread, including from inside
 * a callback, before start and after completion.
 *
 * The reason is recorded before the call returns, and the first reason recorded wins and
 * becomes rq_run_result.end_reason. The managed wrapper relies on this ordering: it signals a
 * waiting step gate only after this call has returned. The engine records RQ_END_FIRST_ERROR
 * (Stop on error) and faults under the same first-writer-wins rule. Workers observe the stop
 * after their current 1 MiB block, holds wake at once, and preparation checks it before every
 * chunk.
 *
 * @param stop_reason An RQ_STOP_* value.
 * @return RQ_OK; RQ_E_INVALID_HANDLE; RQ_E_INVALID_ARGUMENT for any other stop_reason;
 *         RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_session_stop(rq_session_handle session,
                                         uint32_t stop_reason) RQ_NOEXCEPT;

/**
 * Copies the run result header into out once on_completed has been raised.
 *
 * out->step_results then points to a session-owned array of out->step_result_count entries
 * that stays valid until rq_session_destroy.
 *
 * @param out Caller-owned; non-null, with struct_size set to sizeof(rq_run_result).
 * @return RQ_OK; RQ_E_INVALID_HANDLE; RQ_E_INVALID_ARGUMENT for a null out or a wrong
 *         struct_size; RQ_E_INVALID_STATE before Completed; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_session_get_result(rq_session_handle session,
                                               rq_run_result* out) RQ_NOEXCEPT;

/**
 * Destroys a session: stops it if running, waits for in-flight callbacks, joins every thread,
 * releases the locked memory (also when the coordinator never ran, and retrying a chunk whose
 * release withheld on_released) and frees the session. No callback runs after it returns, and
 * the handle is invalid from then on. A chunk Windows still refuses to free does not fail the
 * call: it passes to a process-wide owner that every later release in the process retries, and
 * Windows reclaims it at process exit at the latest.
 *
 * The caller must not race any other call on the same handle with this one; the managed
 * SafeSessionHandle guarantees that.
 *
 * @return RQ_OK; RQ_E_INVALID_HANDLE for a null or unknown handle; RQ_E_INVALID_STATE when
 *         called from inside a callback; RQ_E_INTERNAL.
 */
RQ_API rq_result RQ_CALL rq_session_destroy(rq_session_handle session) RQ_NOEXCEPT;

/**
 * Returns a static, NUL-terminated English description of code, or "Unknown result code" for a
 * value not listed above. Never returns null.
 *
 * Constant, with no failure path; allocates nothing; callable from any thread.
 */
RQ_API const char* RQ_CALL rq_result_message(rq_result code) RQ_NOEXCEPT;

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* RAMQUICK_ENGINE_H */
