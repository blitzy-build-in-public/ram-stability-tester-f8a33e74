/**
 * @file session.h
 * @brief Session lifecycle behind the C API: the dependency seams, configuration validation,
 *        the time-remaining estimate, the session object and the handle registry.
 *
 * The core side of rq_session_create, rq_session_start, rq_session_stop, rq_session_get_result
 * and rq_session_destroy. Everything the production DLL and the test-only DLL share lives here
 * and in session.cpp, never in api.cpp:
 * - src/engine/api/api.cpp, compiled into ramquick_engine.dll and ramquick_engine_test.dll, is a
 *   thin translation: argument checks that need no session, one forwarding call, and the
 *   boundary error contract.
 * - tests/engine.tests/test_dll/test_exports.cpp calls session_create with substitute
 *   dependencies and reads the test accessors through acquire_session.
 * Both binaries therefore share one validation, one deep copy, one stop-reason record and one
 * session registry, which is what rq_test_live_session_count and rq_test_pending_stop_reason
 * observe.
 *
 * @par Exceptions
 * Functions not marked noexcept may throw std::bad_alloc or std::system_error, and session_create
 * also lets through what a substitute layer's wrapper throws. The ABI layer (api.cpp,
 * test_exports.cpp) applies the boundary error contract of ramquick_engine.h:
 * try { ... } catch (...) { return RQ_E_INTERNAL; }. SEH faults (access violation, illegal
 * instruction, in-page error) are never caught or translated anywhere in the engine: they
 * terminate the process, and the host reports the unfinished run at its next launch through
 * journal recovery.
 *
 * @par Build constraint
 * Includes core/clock.h, and through it <windows.h>, so it belongs to baseline (SSE2)
 * translation units only. kernels_avx2.cpp, kernels_avx512.cpp, kernels.h and patterns.h never
 * include it.
 */
#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/memory/memory_access.h"
#include "core/runner/worker_pool.h"

namespace rq::engine {

/* ---------------------------------------------------------------------------------------------
 * Dependency seams
 * ------------------------------------------------------------------------------------------- */

/**
 * The replaceable seams of one session: its clock, its memory-access layer stack, its thread
 * affinity, and the lock and free calls of its region. Passed by value to session_create, which
 * moves every member into the session.
 *
 * - api.cpp passes a default-constructed instance, the production configuration: qpc_clock, the
 *   real memory-access layer alone and win32_thread_affinity.
 * - test_exports.cpp (rq_test_session_create) supplies a wrapper that builds
 *   fault_injecting_memory_access over counting_memory_access over the real layer, for every
 *   test session including RQ_FAULT_NONE, so its layer queries find the same stack on every test
 *   session. It also supplies a clock that the fault layer reads too: delay-dependent injection
 *   (2000 ms or more since write) compares IClock::now_ns() with access_context::write_time_ns,
 *   which the runner stamps from the session clock, so the two must be one instance. When
 *   rq_fault_spec.affinity_fail_worker names a worker, it supplies an affinity that fails that
 *   worker's pin with affinity_fail_error.
 */
struct session_dependencies {
    /**
     * The session's only clock; null selects qpc_clock. Shared ownership exists so the caller
     * can hand the same instance to its substitute layers, and the clock outlives every one of
     * them. Read concurrently by the coordinator and every worker.
     */
    std::shared_ptr<IClock> clock;

    /**
     * Wraps the real layer in substitute layers; empty means the real layer alone.
     *
     * Called exactly once, inside session_create on its calling thread, with the
     * real_memory_access built over the session's kernel table. It returns the outermost layer,
     * which owns the layers beneath it through std::unique_ptr<IMemoryAccess>. The session owns
     * that result for its lifetime, and the step runner and the error collector issue every
     * primitive and every re-read through it. A null result makes session_create fail with
     * RQ_E_INVALID_ARGUMENT.
     */
    std::function<std::unique_ptr<IMemoryAccess>(std::unique_ptr<IMemoryAccess> real)>
        wrap_memory_access;

    /**
     * Pins each worker thread; null selects make_win32_thread_affinity(). The worker pool calls
     * it concurrently from every worker thread during preparation, so it must be thread-safe.
     * Shared ownership keeps it alive for the session's lifetime.
     */
    std::shared_ptr<thread_affinity> affinity;

    /**
     * Frees each chunk of the session's region in place of VirtualFree(MEM_RELEASE); empty, as
     * in production, means VirtualFree. The same signature and contract as memory_region.h's
     * free_function, spelled out so this header pulls in no region type: return false with the
     * Windows last-error set to refuse, leaving the chunk allocated, and never throw. The
     * coordinator's preparation hands it to the region, which calls it from the coordinator's
     * release after on_completed and from shutdown()'s retry, never concurrently. If it still
     * refuses a chunk when the session is destroyed, the process-wide owner of orphaned chunks
     * keeps it with that chunk and calls it from every later release in the process, so what
     * it refers to must stay valid until it has freed the chunk. A test seam that makes a
     * release fail deterministically.
     */
    std::function<bool(void* address)> free_override;

    /**
     * Locks each chunk of the session's region in place of VirtualLock; empty, as in
     * production, means VirtualLock. The same signature and contract as memory_region.h's
     * lock_function, spelled out so this header pulls in no region type: return false with the
     * Windows last-error set to fail that chunk's lock, and never throw. The coordinator's
     * preparation hands it to the region, which calls it on the coordinator for every chunk it
     * locks, so shrink-and-retry and exhaustion follow from it exactly as from VirtualLock.
     * While it is set the region uses 4 KiB pages only, whatever the token holds, because
     * large-page chunks are never locked and would bypass it; so every chunk passes through it.
     * A test seam that makes lock exhaustion (RQ_END_INSUFFICIENT_MEMORY) deterministic.
     */
    std::function<bool(void* address, std::size_t bytes)> lock_override;
};

/* ---------------------------------------------------------------------------------------------
 * Configuration validation
 * ------------------------------------------------------------------------------------------- */

/**
 * Most steps a plan may hold. Far above the 19 steps of the Exhaustive plan; it bounds the deep
 * copy and the allocation-free name-uniqueness check.
 */
inline constexpr std::uint32_t k_max_step_count = 1024;

/**
 * Largest rq_session_config.error_detail_cap. The coordinator preallocates the detail storage
 * at start, 112 bytes per rq_error_record, so the cap bounds it at about 107 MiB. The
 * application passes 10,000.
 */
inline constexpr std::uint32_t k_max_error_detail_cap = 1'000'000;

/**
 * Smallest slice a worker may receive: the 2 MiB granularity to which the engine rounds every
 * worker's slice down. validate_config requires min_region_bytes / worker_count to reach it, so
 * even a region shrunk to its minimum gives every worker a non-empty, 2 MiB-aligned slice.
 */
inline constexpr std::uint64_t k_min_slice_bytes = 2ull << 20;

/**
 * The machine facts validation checks against, so the pure validate_config overload can be
 * tested with synthetic limits.
 */
struct validation_limits {
    /**
     * Upper bound of rq_session_config.worker_count: the active logical processors across every
     * processor group, enumerate_logical_processors().size() on the real machine.
     */
    std::uint32_t logical_processors = 0;
    /**
     * Bit n set when RQ_ISA_* value n is available, as isa_selection::available_mask and
     * rq_system_info.isa_available_mask report it. Bit RQ_ISA_AUTO is never meaningful here.
     */
    std::uint32_t isa_available_mask = 0;
};

/**
 * Validates a session configuration and its callback set against the given machine limits.
 *
 * Pure: it reads only its arguments and the memory they point to, allocates nothing, keeps no
 * state and makes no OS call, so test_api_boundary.cpp can drive it with synthetic limits. Rules
 * are checked in the order listed; the first violation returns RQ_E_INVALID_ARGUMENT. The core
 * never returns RQ_E_VERSION_MISMATCH: the managed wrapper raises that at bind time from
 * rq_api_version.
 *
 * Structure:
 * - config and callbacks are non-null; config->struct_size == sizeof(rq_session_config),
 *   callbacks->struct_size == sizeof(rq_callbacks) and config->step_def_size ==
 *   sizeof(rq_step_def).
 * - 1 <= step_count <= k_max_step_count and steps != nullptr. The ABI rule "the pointer may be
 *   null only when the count is 0" is subsumed, because a count of 0 is itself invalid.
 *
 * Each step:
 * - name is non-empty, NUL-terminated within RQ_NAME_SIZE bytes (at most 63 bytes of text) and
 *   well-formed UTF-8: no stray continuation byte, no overlong form, no surrogate code point
 *   (U+D800 to U+DFFF) and nothing above U+10FFFF. Names are unique within the plan, compared
 *   byte for byte.
 * - pattern is RQ_PATTERN_CHECKERBOARD to RQ_PATTERN_HIGH_TOGGLE (1 to 5); access_order is
 *   RQ_ORDER_SEQUENTIAL_ASCENDING to RQ_ORDER_TURNAROUND (1 to 5), checked even where the
 *   pattern ignores it; thread_mode is RQ_THREADS_ALL or RQ_THREADS_HALF.
 * - time_budget_ms > 0 and coverage_budget_bp > 0. reserved0 and reserved1 are ignored.
 * - Supported shapes, so the runner never meets an undefined combination:
 *   - RQ_PATTERN_MOVING_INVERSION_WALKING and RQ_PATTERN_MOVING_INVERSION_RANDOM run ascending
 *     then descending whole-slice sweeps, ignore access_order and require hold_ms == 0;
 *   - hold_ms > 0 requires a pattern other than moving inversion and access_order ==
 *     RQ_ORDER_RANDOM_BLOCKS, the order in which a hold step writes its whole slice;
 *   - RQ_ORDER_TURNAROUND requires hold_ms == 0.
 *
 * Region and workers:
 * - region_bytes >= min_region_bytes > 0.
 * - 1 <= worker_count <= limits.logical_processors.
 * - min_region_bytes / worker_count >= k_min_slice_bytes.
 *
 * Run options:
 * - stop_on_error is 0 or 1; error_detail_cap <= k_max_error_detail_cap, where 0 is valid and
 *   delivers totals only; progress_interval_ms > 0. seed takes any value.
 * - isa_override is RQ_ISA_AUTO, or RQ_ISA_SSE2, RQ_ISA_AVX2 or RQ_ISA_AVX512 with its bit set
 *   in limits.isa_available_mask. Forcing an unavailable set would execute instructions the CPU
 *   lacks, an illegal-instruction SEH fault the engine never catches.
 *
 * Callbacks:
 * - All seven function pointers are non-null, as ramquick_engine.h requires of
 *   rq_session_create; the managed wrapper always supplies all seven. user_data is opaque and
 *   unchecked, and reserved0 is ignored.
 *
 * @param config Configuration to check; may be null.
 * @param callbacks Callback set to check; may be null.
 * @param limits Machine limits to check against.
 * @return RQ_OK or RQ_E_INVALID_ARGUMENT.
 */
rq_result validate_config(const rq_session_config* config, const rq_callbacks* callbacks,
                          const validation_limits& limits) noexcept;

/**
 * Validates a session configuration and its callback set against this machine.
 *
 * Obtains the limits from enumerate_logical_processors().size() and from the available-set mask
 * of select_isa(read_cpu_features(), RQ_ISA_AUTO), then applies the pure overload.
 * test_exports.cpp calls it before reading any field of an rq_session_config it has not yet
 * checked. session_create derives the same limits from the topology snapshot it keeps for the
 * session's worker placements, and applies the pure overload itself.
 *
 * @param config Configuration to check; may be null.
 * @param callbacks Callback set to check; may be null.
 * @return RQ_OK or RQ_E_INVALID_ARGUMENT.
 * @throws std::system_error when the processor query fails, and std::bad_alloc. ABI callers
 *         convert either to RQ_E_INTERNAL.
 */
rq_result validate_config(const rq_session_config* config, const rq_callbacks* callbacks);

/* ---------------------------------------------------------------------------------------------
 * Time remaining
 * ------------------------------------------------------------------------------------------- */

/**
 * Inputs of compute_remaining_ms: the plan and the running step's schedule, as the step runner
 * reports them to its observer at every tick.
 *
 * A plain aggregate. steps refers to the session's deep-copied step array and needs to stay
 * valid only for the call.
 */
struct remaining_inputs {
    /** The plan's steps in order; each step's budget is its time_budget_ms. */
    std::span<const rq_step_def> steps;
    /** Index of the running step, from 0. */
    std::uint32_t step_index = 0;
    /** Passes of the running step completed so far. */
    std::uint32_t passes_done = 0;
    /**
     * The running step's current schedule, max(1, round(budget / mean pass)); 0 until its first
     * pass completes.
     */
    std::uint32_t passes_planned = 0;
    /** Mean measured pass duration of the running step in nanoseconds; 0 until its first pass. */
    std::uint64_t mean_pass_ns = 0;
    /** Session-clock nanoseconds since the running step started. */
    std::uint64_t step_elapsed_ns = 0;
};

/**
 * The rq_progress.remaining_ms estimate.
 *
 * Pure and allocation-free; tests/engine.tests/test_step_runner.cpp checks it directly.
 * - Returns -1 ("calculating...") while step_index is 0 and passes_done is 0, whatever the other
 *   fields hold.
 * - Otherwise returns 0 when step_index is at or past the end of steps.
 * - Otherwise returns the running step's term plus the time_budget_ms of every later step. The
 *   term is max(0, time_budget_ms x 10^6 - step_elapsed_ns) while passes_done is 0, and
 *   max(0, passes_planned x mean_pass_ns - step_elapsed_ns) once it is not. It is computed in
 *   nanoseconds and converted to milliseconds by truncating division before the later budgets
 *   are added.
 * - Every product and sum saturates instead of wrapping, and the result is capped at INT64_MAX.
 *
 * The estimate is exact only under the stable-pass conditions of the step scheduling model:
 * the first pass no longer than the budget and pass durations within +/-10 % of their mean. It
 * is low during an oversized first pass, whose term reaches 0 at the budget while the pass is
 * still running, and it includes the real duration of an overrunning step once that pass ends.
 *
 * @param in The plan and the running step's schedule.
 * @return Milliseconds remaining, or -1 while the first step has no completed pass.
 */
std::int64_t compute_remaining_ms(const remaining_inputs& in) noexcept;

/* ---------------------------------------------------------------------------------------------
 * The session
 * ------------------------------------------------------------------------------------------- */

/**
 * One test session: its deep-copied configuration, its dependencies, its stop signal and, once
 * started, its coordinator thread, which prepares memory and workers, sequences the steps,
 * raises every callback and releases memory.
 *
 * @par Event contract
 * The coordinator follows the threading rules, the event order and the payload rules of
 * ramquick_engine.h exactly. Workers never raise a callback, and callbacks never run
 * concurrently. The end reason is first-writer-wins through the session's stop_signal (host
 * stops, RQ_END_FIRST_ERROR from a worker, faults, and RQ_END_COMPLETED recorded once every step
 * has run) and alone decides the terminal state. on_completed is raised after every worker has
 * joined; the locked region is then released for every end reason, and on_released follows
 * once every chunk is freed. A chunk Windows refuses to free withholds on_released and stays
 * held for shutdown() to retry; one still refused when the session is destroyed passes to the
 * process-wide owner of orphaned chunks (core/memory/memory_region.h), which every later
 * release in the process retries.
 *
 * @par Ownership and threading
 * Created only by session_create and owned through std::shared_ptr: by the registry's handle
 * and by any caller of acquire_session for the duration of its call. The coordinator never owns
 * a reference, and session_destroy joins it while the handle still holds one, so the destructor
 * never runs on the coordinator thread. start() and shutdown() are host-thread calls; stop(),
 * get_result(), on_coordinator_thread(), memory_access() and pending_stop_reason() may be called
 * from any thread, including from inside a callback.
 *
 * Non-copyable and non-movable: the coordinator and the workers refer to the session's state
 * while they run. Its internals are defined in session.cpp, so this header pulls in none of the
 * runner, region or collector types.
 */
class session {
public:
    /**
     * Internal: constructs an idle session. Only session_create calls it, after validate_config
     * has returned RQ_OK for the same config and callbacks against limits taken from the same
     * processors snapshot; other callers use session_create.
     *
     * Deep-copies the configuration scalars, the step array with its inline names and the
     * callback struct, so the caller may free its copies once construction returns. Keeps the
     * first config.worker_count entries of processors as the worker placements: worker i is
     * pinned to entry i, and preparation never queries the topology again, so a processor gone
     * inactive since creation fails its worker's pin with RQ_END_AFFINITY_FAILED. Runs ISA
     * dispatch once (read_cpu_features, select_isa with config.isa_override, tables_for) and
     * keeps that kernel table for the session's lifetime. Builds real_memory_access over it and
     * passes it through dependencies.wrap_memory_access when that is set. Fills a null clock
     * with qpc_clock and a null affinity with make_win32_thread_affinity(). Reserves the
     * per-step result storage. Allocates nothing large: no region, no error-detail storage, and
     * no thread.
     *
     * @param config A validated configuration; read only during the call.
     * @param callbacks A validated callback set; read only during the call.
     * @param dependencies Seams to adopt; see session_dependencies.
     * @param processors enumerate_logical_processors() as taken for validation, in its
     *        group-major order; adopted.
     * @throws std::invalid_argument when processors has fewer than config.worker_count entries
     *         or config.isa_override names a set this CPU lacks, which validation rules out;
     *         when dependencies.wrap_memory_access returns null; or when the wrapper itself
     *         throws it. session_create maps it to RQ_E_INVALID_ARGUMENT.
     * @throws std::system_error when the stop event cannot be created, std::bad_alloc, and
     *         anything else the wrapper throws.
     */
    session(const rq_session_config& config, const rq_callbacks& callbacks,
            session_dependencies dependencies, std::vector<logical_processor> processors);

    /**
     * Calls shutdown(), so a session released without session_destroy still records a stop,
     * joins its coordinator and frees its memory. Never runs on the coordinator thread (see
     * Ownership and threading).
     */
    ~session();

    session(const session&) = delete;
    session& operator=(const session&) = delete;
    session(session&&) = delete;
    session& operator=(session&&) = delete;

    /**
     * Starts the coordinator thread and returns at once; preparation, the steps and every
     * callback follow on that thread.
     *
     * Allowed once per session, whatever its outcome: a second call, or a call after shutdown(),
     * returns RQ_E_INVALID_STATE. A stop recorded before start makes the coordinator raise
     * on_completed, with the end reason mapped from the first recorded stop, and then
     * on_released straight away, with no on_prepared. After RQ_E_THREAD_CREATE no callback is
     * ever raised, and the session can only be destroyed.
     *
     * Host thread only; never from a callback.
     *
     * @return RQ_OK; RQ_E_INVALID_STATE; RQ_E_THREAD_CREATE when the thread cannot be created.
     * @throws std::bad_alloc when the thread's start state cannot be allocated.
     */
    rq_result start();

    /**
     * Requests a stop: RQ_STOP_USER, RQ_STOP_THERMAL and RQ_STOP_HOST_FAULT are recorded as
     * RQ_END_USER_STOP, RQ_END_THERMAL and RQ_END_HOST_FAULT through stop_signal::request, with
     * a short English detail naming the stop.
     *
     * The reason is recorded before the call returns, and the first reason recorded wins: a
     * later stop, Stop on error or fault keeps the earlier reason. The managed wrapper relies on
     * that ordering, setting its stop event only after rq_session_stop has returned.
     * Idempotent and non-blocking; callable from any thread at any time, including from inside
     * a callback, before start and after completion, where it has no further effect.
     *
     * @param stop_reason An RQ_STOP_* value.
     * @return RQ_OK; RQ_E_INVALID_ARGUMENT for any other value, which records nothing.
     */
    rq_result stop(std::uint32_t stop_reason) noexcept;

    /**
     * Copies the run result header into out.
     *
     * The result is stored immediately before on_completed is raised, so it is available inside
     * that callback and from then on. out->step_results then points to the session-owned array
     * of out->step_result_count entries, valid until the session is destroyed. Callable from
     * any thread.
     *
     * @param out Caller-owned; struct_size must be sizeof(rq_run_result).
     * @return RQ_OK; RQ_E_INVALID_ARGUMENT for a null out or a wrong struct_size, which writes
     *         nothing; RQ_E_INVALID_STATE until the result has been stored.
     */
    rq_result get_result(rq_run_result* out) const noexcept;

    /**
     * True when called on this session's coordinator thread, that is, from inside one of its
     * callbacks; false on every other thread, before start and after the coordinator has
     * exited. session_destroy uses it to refuse destruction from inside a callback.
     */
    bool on_coordinator_thread() const noexcept;

    /**
     * Destroy semantics short of freeing the object.
     *
     * Records RQ_END_USER_STOP (first writer wins, so an earlier reason is kept), suppresses
     * every callback not yet raised, and joins the coordinator, which first lets a callback in
     * flight return. The coordinator still releases the locked region with its callbacks
     * suppressed, and the region is released again here, which covers a coordinator that never
     * ran and retries a chunk Windows refused to free, so no exit path leaves memory locked. No
     * callback runs after it returns.
     *
     * Idempotent: session_destroy calls it, and the destructor calls it again. Host thread only;
     * never on the coordinator thread, which would join itself (session_destroy checks
     * on_coordinator_thread() first). Joining cannot deadlock against the managed UI thread,
     * because the managed wrapper posts every event to its synchronization context instead of
     * blocking the coordinator on it.
     *
     * @throws std::system_error only when joining the coordinator fails, which the precondition
     *         rules out.
     */
    void shutdown();

    /**
     * Test accessor: the outermost memory-access layer, through which the runner and the error
     * collector issue every primitive. It is the layer wrap_memory_access returned, or the real
     * layer alone when no wrapper was set.
     *
     * test_exports.cpp reaches its substitute layers' own accessors through it with dynamic_cast
     * (a production session yields no substitute layer). Callers never issue primitives on it.
     * Valid for the session's lifetime; callable from any thread.
     */
    IMemoryAccess& memory_access() noexcept;

    /**
     * Test accessor: the RQ_END_* value recorded so far under the first-writer-wins rule, or 0
     * when none has been recorded; stop_signal::pending_reason. rq_test_pending_stop_reason
     * returns it. Lock-free; callable from any thread, including from inside a callback.
     */
    std::uint32_t pending_stop_reason() const noexcept;

private:
    /**
     * Configuration, dependencies, stop signal, kernel table, memory-access stack, coordinator
     * thread, result storage and lifecycle flags; defined in session.cpp.
     */
    struct state;
    std::unique_ptr<state> state_;
};

/* ---------------------------------------------------------------------------------------------
 * Handle-level functions: what api.cpp and test_exports.cpp call
 *
 * Handles are opaque rq_session_handle values that session_create registers and
 * session_destroy unregisters. Every function validates a handle against the registry under its
 * lock and never dereferences one it does not know, so null, never-issued and destroyed handles
 * all give RQ_E_INVALID_HANDLE. A session is looked up and held through std::shared_ptr for the
 * duration of each call, so a concurrent session_destroy cannot free it mid-call.
 * ------------------------------------------------------------------------------------------- */

/**
 * Validates a configuration and creates an idle, registered session.
 *
 * Steps: out null gives RQ_E_INVALID_ARGUMENT and writes nothing; otherwise *out is set to null
 * first. The active logical processors are enumerated once (enumerate_logical_processors), and
 * the pure validate_config must return RQ_OK against the limits of that snapshot and of this
 * CPU's instruction sets, exactly those the two-argument overload applies, else its code is
 * returned. The session is then constructed: it keeps the snapshot's first worker_count entries
 * as its worker placements, so start never enumerates again, ISA dispatch runs once, the
 * configuration, the steps, their names and the callbacks are deep-copied, and the
 * memory-access stack is built through dependencies.wrap_memory_access; a null wrapper result
 * gives RQ_E_INVALID_ARGUMENT. Finally the session is registered and its handle stored in *out.
 * Allocates nothing large and starts no thread.
 *
 * Any failure, an exception included, registers nothing, leaves *out null and leaves
 * live_session_count() unchanged.
 *
 * @param config Session configuration; may be null, which validation rejects.
 * @param callbacks Callback set; may be null, which validation rejects.
 * @param dependencies Seams for the session; a default-constructed instance is production.
 * @param out Receives the new handle; set to null on failure.
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT.
 * @throws std::bad_alloc, std::system_error and anything the wrapper throws other than
 *         std::invalid_argument; the ABI layer converts them to RQ_E_INTERNAL.
 */
rq_result session_create(const rq_session_config* config, const rq_callbacks* callbacks,
                         session_dependencies dependencies, rq_session_handle* out);

/**
 * session::start on a registered session.
 *
 * @return RQ_E_INVALID_HANDLE for an unknown handle; otherwise as session::start.
 * @throws std::bad_alloc, as session::start.
 */
rq_result session_start(rq_session_handle handle);

/**
 * session::stop on a registered session. Callable from any thread, including from inside a
 * callback; never blocks beyond the registry lookup.
 *
 * @return RQ_E_INVALID_HANDLE for an unknown handle; otherwise as session::stop.
 * @throws std::system_error only if the registry lock itself fails.
 */
rq_result session_stop(rq_session_handle handle, std::uint32_t stop_reason);

/**
 * session::get_result on a registered session.
 *
 * @return RQ_E_INVALID_HANDLE for an unknown handle; otherwise as session::get_result.
 * @throws std::system_error only if the registry lock itself fails.
 */
rq_result session_get_result(rq_session_handle handle, rq_run_result* out);

/**
 * Destroys a session: unregisters it, calls session::shutdown(), which stops it if running,
 * suppresses the callbacks not yet raised, joins the coordinator after any callback in flight
 * and releases its memory, then frees the handle. No callback of the session runs after it
 * returns, and the handle is invalid from then on; SafeSessionHandle.ReleaseHandle in
 * RamQuick.Interop relies on both.
 *
 * The registry lock is released before shutdown() joins, so other sessions stay usable while
 * this one winds down. The caller must not race any other call on the same handle with this
 * one; the managed SafeSessionHandle guarantees that.
 *
 * @return RQ_OK; RQ_E_INVALID_HANDLE for an unknown handle; RQ_E_INVALID_STATE when called on
 *         the session's coordinator thread, that is, from inside one of its callbacks, in which
 *         case the session stays registered and alive.
 * @throws std::system_error from the registry lock or the join.
 */
rq_result session_destroy(rq_session_handle handle);

/**
 * Looks a handle up in the registry.
 *
 * The returned pointer keeps the session object alive for as long as the caller holds it, even
 * if another thread destroys the handle meanwhile; session_destroy still guarantees that no
 * callback runs after it returns. Callers hold it for one call only (test_exports.cpp holds it
 * for each query). Callable from any thread, including from inside a callback.
 *
 * @return The registered session, or null for a null, never-issued or destroyed handle.
 * @throws std::system_error only if the registry lock itself fails.
 */
std::shared_ptr<session> acquire_session(rq_session_handle handle);

/**
 * Sessions created by session_create and not yet destroyed, production and test sessions
 * alike; rq_test_live_session_count returns it. A snapshot that concurrent creates and
 * destroys change. Has no failure path; callable from any thread.
 */
std::uint32_t live_session_count() noexcept;

}  // namespace rq::engine
