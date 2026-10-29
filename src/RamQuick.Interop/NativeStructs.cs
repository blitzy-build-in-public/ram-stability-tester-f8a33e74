using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace RamQuick.Interop;

/// <summary>
/// Managed copies of the value <c>#define</c>s of <c>ramquick_engine.h</c>, one constant per native symbol.
/// </summary>
/// <remarks>
/// <para>
/// Every enumeration field of the C API is a <c>uint32_t</c> carrying one of these values, never a C enum, so the
/// constants are <see cref="uint"/> and the structs below carry <see cref="uint"/> fields. Published values are never
/// renumbered; any change bumps <see cref="ApiVersion"/>.
/// </para>
/// <para>
/// The pattern, access-order, thread-mode, instruction-set, page-mode, address-kind, access-type and re-read values
/// equal the numbers of the <c>RamQuick.Core</c> enums of the same meaning, so those convert by a checked cast. The
/// end reasons do <em>not</em> share numbers with <c>RunEndReason</c> and are mapped by an explicit switch. The
/// <c>RQ_OK</c> and <c>RQ_E_*</c> result codes are mirrored by <c>EngineResultCode</c>, not here.
/// </para>
/// </remarks>
internal static class NativeConstants
{
    /// <summary><c>RQ_API_VERSION</c>: the C API version this wrapper is built against.</summary>
    internal const uint ApiVersion = 1u;

    /// <summary><c>RQ_NAME_SIZE</c>: bytes of every name field, NUL terminator included (at most 63 UTF-8 bytes).</summary>
    internal const int NameSize = 64;

    /// <summary><c>RQ_END_DETAIL_SIZE</c>: bytes of <c>rq_run_result.end_detail</c>, NUL terminator included.</summary>
    internal const int EndDetailSize = 128;

    /// <summary><c>RQ_NO_FIRST_ERROR</c>: <c>rq_run_result.first_error_ms</c> when the run found no error.</summary>
    internal const ulong NoFirstError = ulong.MaxValue;

    /// <summary><c>RQ_PATTERN_CHECKERBOARD</c>: alternating 0x55/0xAA words, inverted every pass.</summary>
    internal const uint PatternCheckerboard = 1u;

    /// <summary><c>RQ_PATTERN_MOVING_INVERSION_WALKING</c>: moving inversion over walking ones and zeros.</summary>
    internal const uint PatternMovingInversionWalking = 2u;

    /// <summary><c>RQ_PATTERN_MOVING_INVERSION_RANDOM</c>: moving inversion over random base data.</summary>
    internal const uint PatternMovingInversionRandom = 3u;

    /// <summary><c>RQ_PATTERN_RANDOM</c>: SplitMix64 of seed, word index and pass.</summary>
    internal const uint PatternRandom = 4u;

    /// <summary><c>RQ_PATTERN_HIGH_TOGGLE</c>: complement between consecutive words and halves.</summary>
    internal const uint PatternHighToggle = 5u;

    /// <summary><c>RQ_ORDER_SEQUENTIAL_ASCENDING</c>: ascending addresses, window by window.</summary>
    internal const uint OrderSequentialAscending = 1u;

    /// <summary><c>RQ_ORDER_SEQUENTIAL_DESCENDING</c>: descending addresses, window by window.</summary>
    internal const uint OrderSequentialDescending = 2u;

    /// <summary><c>RQ_ORDER_BANK_STRIDE</c>: 64-byte lines at power-of-two virtual strides.</summary>
    internal const uint OrderBankStride = 3u;

    /// <summary><c>RQ_ORDER_RANDOM_BLOCKS</c>: a seeded permutation of 4 KiB blocks.</summary>
    internal const uint OrderRandomBlocks = 4u;

    /// <summary><c>RQ_ORDER_TURNAROUND</c>: write a block, verify the block four positions behind it.</summary>
    internal const uint OrderTurnaround = 5u;

    /// <summary><c>RQ_THREADS_ALL</c>: every worker runs on its own slice.</summary>
    internal const uint ThreadsAll = 1u;

    /// <summary><c>RQ_THREADS_HALF</c>: only even-indexed workers run, each covering its partner's slice too.</summary>
    internal const uint ThreadsHalf = 2u;

    /// <summary>
    /// <c>RQ_ISA_AUTO</c>: select the best available instruction set; valid only in
    /// <see cref="RqSessionConfig.IsaOverride"/>.
    /// </summary>
    internal const uint IsaAuto = 0u;

    /// <summary><c>RQ_ISA_SSE2</c>: the reduced, slower kernels used only when AVX2 is absent.</summary>
    internal const uint IsaSse2 = 1u;

    /// <summary><c>RQ_ISA_AVX2</c>: the full-speed baseline kernels.</summary>
    internal const uint IsaAvx2 = 2u;

    /// <summary><c>RQ_ISA_AVX512</c>: AVX-512 kernels (F, CD, BW, DQ and VL plus OS-enabled ZMM state).</summary>
    /// <remarks>In <see cref="RqSystemInfo.IsaAvailableMask"/>, bit <c>n</c> set means ISA value <c>n</c> is available.</remarks>
    internal const uint IsaAvx512 = 3u;

    /// <summary><c>RQ_PAGE_MODE_STANDARD</c>: every chunk uses 4 KiB pages locked with <c>VirtualLock</c>.</summary>
    internal const uint PageModeStandard = 1u;

    /// <summary><c>RQ_PAGE_MODE_LARGE</c>: every chunk uses large pages.</summary>
    internal const uint PageModeLarge = 2u;

    /// <summary><c>RQ_PAGE_MODE_MIXED</c>: some large-page chunks fell back to 4 KiB pages.</summary>
    internal const uint PageModeMixed = 3u;

    /// <summary><c>RQ_ADDRESS_KIND_VIRTUAL</c>: addresses are virtual addresses in the engine process.</summary>
    internal const uint AddressKindVirtual = 1u;

    /// <summary><c>RQ_ADDRESS_KIND_PHYSICAL</c>: reserved for physical addresses; never reported by API version 1.</summary>
    internal const uint AddressKindPhysical = 2u;

    /// <summary><c>RQ_ACCESS_VERIFY_AFTER_WRITE</c>: windowed verification shortly after the window was written.</summary>
    internal const uint AccessVerifyAfterWrite = 1u;

    /// <summary><c>RQ_ACCESS_VERIFY_AFTER_HOLD</c>: whole-slice verification after an idle hold.</summary>
    internal const uint AccessVerifyAfterHold = 2u;

    /// <summary><c>RQ_ACCESS_READ_MODIFY_WRITE</c>: verify-and-write sweep of a moving-inversion pass.</summary>
    internal const uint AccessReadModifyWrite = 3u;

    /// <summary><c>RQ_ACCESS_TURNAROUND_READ</c>: turnaround verification of a block written just before.</summary>
    internal const uint AccessTurnaroundRead = 4u;

    /// <summary><c>RQ_REREAD_MATCHES_EXPECTED</c>: the flushed re-read returned the expected value.</summary>
    internal const uint RereadMatchesExpected = 1u;

    /// <summary><c>RQ_REREAD_REPEATS_ACTUAL</c>: the flushed re-read returned the first, wrong value again.</summary>
    internal const uint RereadRepeatsActual = 2u;

    /// <summary><c>RQ_REREAD_DIFFERS</c>: the flushed re-read returned neither value.</summary>
    internal const uint RereadDiffers = 3u;

    /// <summary><c>RQ_STOP_USER</c>: the user pressed Stop or closed the window; ends as <see cref="EndUserStop"/>.</summary>
    internal const uint StopUser = 1u;

    /// <summary><c>RQ_STOP_THERMAL</c>: a temperature reached its thermal limit; ends as <see cref="EndThermal"/>.</summary>
    internal const uint StopThermal = 2u;

    /// <summary><c>RQ_STOP_HOST_FAULT</c>: the host can no longer support the run; ends as <see cref="EndHostFault"/>.</summary>
    internal const uint StopHostFault = 3u;

    /// <summary><c>RQ_END_COMPLETED</c>: every step ran to its schedule; PASS when the true error total is 0.</summary>
    internal const uint EndCompleted = 1u;

    /// <summary><c>RQ_END_FIRST_ERROR</c>: Stop on error was set and a worker confirmed a mismatch.</summary>
    internal const uint EndFirstError = 2u;

    /// <summary><c>RQ_END_USER_STOP</c>: stopped by <see cref="StopUser"/>.</summary>
    internal const uint EndUserStop = 3u;

    /// <summary><c>RQ_END_THERMAL</c>: stopped by <see cref="StopThermal"/>.</summary>
    internal const uint EndThermal = 4u;

    /// <summary>
    /// <c>RQ_END_HOST_FAULT</c>: stopped by <see cref="StopHostFault"/>, or a step gate aborted with no stop reason
    /// recorded.
    /// </summary>
    internal const uint EndHostFault = 5u;

    /// <summary><c>RQ_END_INSUFFICIENT_MEMORY</c>: locking failed even at the minimum region size.</summary>
    internal const uint EndInsufficientMemory = 6u;

    /// <summary><c>RQ_END_AFFINITY_FAILED</c>: a worker could not be pinned; no traffic ran.</summary>
    internal const uint EndAffinityFailed = 7u;

    /// <summary><c>RQ_END_INTERNAL_FAULT</c>: a C++ exception or thread-creation failure inside the engine.</summary>
    internal const uint EndInternalFault = 8u;

    /// <summary><c>RQ_ACTION_CONTINUE</c>: step-gate result that runs the step.</summary>
    internal const uint ActionContinue = 0u;

    /// <summary><c>RQ_ACTION_ABORT</c>: step-gate result that starts no traffic for the step and ends the run.</summary>
    internal const uint ActionAbort = 1u;
}

// The structs below mirror ramquick_engine.h byte for byte. They use the native field order, natural alignment and
// no Pack, exactly as the header uses no #pragma pack. The native reserved fields are kept, so every ulong and
// pointer field starts on an 8-byte boundary and no implicit padding exists on either side. Every field is
// blittable (no bool, char or managed reference), so the runtime passes pointers to these structs straight through,
// with no marshalling copy. Each Size constant equals the header's static_assert, and InteropMarshallingTests
// compares it and sizeof(T) with rq_test_struct_sizes.

/// <summary>Mirror of <c>rq_system_info</c>, the machine description filled by <c>rq_query_system</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqSystemInfo
{
    /// <summary>Size of <c>rq_system_info</c> in bytes, the value <see cref="StructSize"/> must hold.</summary>
    internal const int Size = 112;

    /// <summary><c>struct_size</c>: set by the caller to <see cref="Size"/> before the call.</summary>
    public uint StructSize;

    /// <summary><c>logical_processors</c>: logical processors across all processor groups.</summary>
    public uint LogicalProcessors;

    /// <summary><c>processor_groups</c>: processor groups; a group holds at most 64 logical processors.</summary>
    public uint ProcessorGroups;

    /// <summary><c>isa_available_mask</c>: bit <c>n</c> set when ISA value <c>n</c> (<c>NativeConstants.Isa*</c>) is available.</summary>
    public uint IsaAvailableMask;

    /// <summary><c>isa_best</c>: the best available ISA value; never <see cref="NativeConstants.IsaAuto"/>.</summary>
    public uint IsaBest;

    /// <summary><c>lock_privilege_held</c>: 1 when the token holds <c>SeLockMemoryPrivilege</c>, else 0; observed only.</summary>
    public uint LockPrivilegeHeld;

    /// <summary><c>total_phys_bytes</c>: total physical memory in bytes.</summary>
    public ulong TotalPhysBytes;

    /// <summary><c>avail_phys_bytes</c>: available physical memory in bytes at the time of the call.</summary>
    public ulong AvailPhysBytes;

    /// <summary><c>large_page_min_bytes</c>: large-page granularity in bytes, or 0 when large pages are unsupported.</summary>
    public ulong LargePageMinBytes;

    /// <summary><c>cpu_name</c>: the CPUID processor brand string, UTF-8 and NUL-terminated; read with <see cref="FixedUtf8.Read"/>.</summary>
    public fixed byte CpuName[NativeConstants.NameSize];
}

/// <summary>
/// Mirror of <c>rq_step_def</c>, one step of a test plan and an element of <see cref="RqSessionConfig.Steps"/>.
/// </summary>
/// <remarks>
/// An array element: it carries no <c>struct_size</c>, and <see cref="RqSessionConfig.StepDefSize"/> must equal
/// <see cref="Size"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqStepDef
{
    /// <summary>Size of <c>rq_step_def</c> in bytes, the value <see cref="RqSessionConfig.StepDefSize"/> must hold.</summary>
    internal const int Size = 96;

    /// <summary>
    /// <c>name</c>: non-empty, at most 63 UTF-8 bytes, NUL-terminated; written with <see cref="FixedUtf8.Write"/> and
    /// shown to the user verbatim.
    /// </summary>
    public fixed byte Name[NativeConstants.NameSize];

    /// <summary><c>pattern</c>: a <c>NativeConstants.Pattern*</c> value.</summary>
    public uint Pattern;

    /// <summary><c>access_order</c>: a <c>NativeConstants.Order*</c> value; ignored by the moving-inversion patterns.</summary>
    public uint AccessOrder;

    /// <summary><c>thread_mode</c>: a <c>NativeConstants.Threads*</c> value.</summary>
    public uint ThreadMode;

    /// <summary>
    /// <c>hold_ms</c>: 0 for a windowed step; otherwise each pass writes the whole slice, waits this many milliseconds,
    /// then verifies the whole slice.
    /// </summary>
    public uint HoldMs;

    /// <summary><c>time_budget_ms</c>: the step's time budget in milliseconds, greater than 0.</summary>
    public uint TimeBudgetMs;

    /// <summary>
    /// <c>coverage_budget_bp</c>: the step's coverage floor in hundredths of a percent (25 % is 2500), greater than 0.
    /// It never extends a step.
    /// </summary>
    public uint CoverageBudgetBp;

    /// <summary><c>reserved0</c>: set to 0.</summary>
    public uint Reserved0;

    /// <summary><c>reserved1</c>: set to 0.</summary>
    public uint Reserved1;
}

/// <summary>Mirror of <c>rq_session_config</c>, the configuration passed to <c>rq_session_create</c>.</summary>
/// <remarks>
/// <c>rq_session_create</c> deep-copies the configuration, the step array and the step names, so the memory behind
/// <see cref="Steps"/> needs to stay pinned only for that call.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqSessionConfig
{
    /// <summary>Size of <c>rq_session_config</c> in bytes, the value <see cref="StructSize"/> must hold.</summary>
    internal const int Size = 64;

    /// <summary><c>struct_size</c>: set by the caller to <see cref="Size"/>.</summary>
    public uint StructSize;

    /// <summary><c>step_def_size</c>: set by the caller to <see cref="RqStepDef.Size"/>.</summary>
    public uint StepDefSize;

    /// <summary><c>region_bytes</c>: planned bytes under test; the engine rounds each worker's slice down to 2 MiB.</summary>
    public ulong RegionBytes;

    /// <summary><c>min_region_bytes</c>: floor for shrink-and-retry when locking fails; greater than 0 and at most <see cref="RegionBytes"/>.</summary>
    public ulong MinRegionBytes;

    /// <summary><c>seed</c>: seed of the random patterns and of the random-block permutation.</summary>
    public ulong Seed;

    /// <summary><c>worker_count</c>: workers, from 1 to <see cref="RqSystemInfo.LogicalProcessors"/>.</summary>
    public uint WorkerCount;

    /// <summary><c>stop_on_error</c>: 1 to halt the run on the first confirmed error, else 0.</summary>
    public uint StopOnError;

    /// <summary><c>error_detail_cap</c>: most detailed error records delivered per run (10,000 in the application).</summary>
    public uint ErrorDetailCap;

    /// <summary><c>progress_interval_ms</c>: interval between progress callbacks in milliseconds, greater than 0.</summary>
    public uint ProgressIntervalMs;

    /// <summary><c>isa_override</c>: <see cref="NativeConstants.IsaAuto"/>, or a forced ISA value that must be available.</summary>
    public uint IsaOverride;

    /// <summary><c>step_count</c>: entries in <see cref="Steps"/>, at least 1.</summary>
    public uint StepCount;

    /// <summary><c>steps</c>: the plan's ordered steps; caller-owned and read only during <c>rq_session_create</c>.</summary>
    public RqStepDef* Steps;
}

/// <summary>Mirror of <c>rq_callbacks</c>, the callback set passed to <c>rq_session_create</c>, which copies it.</summary>
/// <remarks>
/// <para>
/// All seven function pointers are required. Every callback is <c>__cdecl</c>, runs on the session's coordinator
/// thread (never on a worker, never concurrently with another callback of the session), and must not let any
/// exception unwind into the engine. Payload pointers, and the arrays they reference, are engine-owned and valid only
/// during the call, so a callback copies whatever it keeps.
/// </para>
/// <para>
/// The fields are unmanaged function pointers to static <c>[UnmanagedCallersOnly]</c> methods rather than delegates,
/// so no delegate exists for the garbage collector to free while native code still calls it.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqCallbacks
{
    /// <summary>Size of <c>rq_callbacks</c> in bytes, the value <see cref="StructSize"/> must hold.</summary>
    internal const int Size = 72;

    /// <summary><c>struct_size</c>: set by the caller to <see cref="Size"/>.</summary>
    public uint StructSize;

    /// <summary><c>reserved0</c>: set to 0.</summary>
    public uint Reserved0;

    /// <summary>
    /// <c>user_data</c>: opaque to the engine and passed back to every callback; whatever it refers to must stay valid
    /// until <c>rq_session_destroy</c> returns.
    /// </summary>
    public void* UserData;

    /// <summary><c>on_prepared</c>: raised once when preparation succeeds, at the origin of test time.</summary>
    public delegate* unmanaged[Cdecl]<void*, RqPrepared*, void> OnPrepared;

    /// <summary>
    /// <c>on_step_starting</c>: the step gate, raised before any memory traffic of each step; returns
    /// <see cref="NativeConstants.ActionContinue"/> or <see cref="NativeConstants.ActionAbort"/>. It may block.
    /// </summary>
    public delegate* unmanaged[Cdecl]<void*, RqStepStart*, uint> OnStepStarting;

    /// <summary><c>on_step_finished</c>: raised once for every step whose gate returned <see cref="NativeConstants.ActionContinue"/>.</summary>
    public delegate* unmanaged[Cdecl]<void*, RqStepResult*, void> OnStepFinished;

    /// <summary><c>on_progress</c>: raised every <see cref="RqSessionConfig.ProgressIntervalMs"/> while a step runs.</summary>
    public delegate* unmanaged[Cdecl]<void*, RqProgress*, void> OnProgress;

    /// <summary>
    /// <c>on_errors</c>: raised at most every 100 ms with the records confirmed since the previous batch, their count
    /// and the exact true error total. Once the detail cap is reached the count may be 0 and the records pointer null.
    /// </summary>
    public delegate* unmanaged[Cdecl]<void*, RqErrorRecord*, uint, ulong, void> OnErrors;

    /// <summary><c>on_completed</c>: raised exactly once, after every worker has joined; memory is released afterwards.</summary>
    public delegate* unmanaged[Cdecl]<void*, RqRunResult*, void> OnCompleted;

    /// <summary><c>on_released</c>: raised exactly once, after <c>on_completed</c>, with the released bytes and release time in milliseconds.</summary>
    public delegate* unmanaged[Cdecl]<void*, ulong, ulong, void> OnReleased;
}

/// <summary>Mirror of <c>rq_prepared</c>, the payload of <c>on_prepared</c>: the region is locked and the workers pinned.</summary>
/// <remarks>Engine-owned and valid only during the callback.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqPrepared
{
    /// <summary>Size of <c>rq_prepared</c> in bytes.</summary>
    internal const int Size = 40;

    /// <summary><c>struct_size</c>: <see cref="Size"/>, written by the engine.</summary>
    public uint StructSize;

    /// <summary><c>worker_count</c>: workers created and pinned.</summary>
    public uint WorkerCount;

    /// <summary><c>page_mode</c>: a <c>NativeConstants.PageMode*</c> value.</summary>
    public uint PageMode;

    /// <summary><c>isa_selected</c>: the ISA value selected for the session; never <see cref="NativeConstants.IsaAuto"/>.</summary>
    public uint IsaSelected;

    /// <summary><c>address_kind</c>: a <c>NativeConstants.AddressKind*</c> value; virtual in API version 1.</summary>
    public uint AddressKind;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary>
    /// <c>tested_bytes</c>: bytes under test (slice times workers, after 2 MiB rounding and any lock shrink), the
    /// denominator of coverage.
    /// </summary>
    public ulong TestedBytes;

    /// <summary><c>prepare_ms</c>: preparation time in milliseconds, from start to this callback.</summary>
    public ulong PrepareMs;
}

/// <summary>Mirror of <c>rq_step_start</c>, the payload of the <c>on_step_starting</c> step gate.</summary>
/// <remarks>Engine-owned and valid only during the callback.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqStepStart
{
    /// <summary>Size of <c>rq_step_start</c> in bytes.</summary>
    internal const int Size = 80;

    /// <summary><c>struct_size</c>: <see cref="Size"/>, written by the engine.</summary>
    public uint StructSize;

    /// <summary><c>index</c>: 0-based position of the step in <see cref="RqSessionConfig.Steps"/>.</summary>
    public uint Index;

    /// <summary><c>count</c>: steps in the plan.</summary>
    public uint Count;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary><c>name</c>: the step's name as configured, UTF-8 and NUL-terminated.</summary>
    public fixed byte Name[NativeConstants.NameSize];
}

/// <summary>
/// Mirror of <c>rq_step_result</c>, the outcome of one started step: the payload of <c>on_step_finished</c> and an
/// element of <see cref="RqRunResult.StepResults"/>.
/// </summary>
/// <remarks>
/// An array element: it carries no <c>struct_size</c>. Engine-owned; valid only during the callback, or until
/// <c>rq_session_destroy</c> when reached through <c>rq_session_get_result</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqStepResult
{
    /// <summary>Size of <c>rq_step_result</c> in bytes.</summary>
    internal const int Size = 48;

    /// <summary><c>index</c>: 0-based position of the step in <see cref="RqSessionConfig.Steps"/>.</summary>
    public uint Index;

    /// <summary><c>completed</c>: 1 when every scheduled pass ran without a stop, else 0.</summary>
    public uint Completed;

    /// <summary><c>passes</c>: passes completed.</summary>
    public uint Passes;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary><c>actual_ms</c>: measured duration of the step in milliseconds.</summary>
    public ulong ActualMs;

    /// <summary><c>overrun_ms</c>: milliseconds beyond the step's time budget; nonzero when a single pass outgrew it.</summary>
    public ulong OverrunMs;

    /// <summary><c>verified_bytes</c>: bytes verified in this step.</summary>
    public ulong VerifiedBytes;

    /// <summary><c>errors</c>: the step's true error count; never capped.</summary>
    public ulong Errors;
}

/// <summary>Mirror of <c>rq_progress</c>, the payload of <c>on_progress</c>.</summary>
/// <remarks>Engine-owned and valid only during the callback. Every index is 0-based.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqProgress
{
    /// <summary>Size of <c>rq_progress</c> in bytes.</summary>
    internal const int Size = 72;

    /// <summary><c>struct_size</c>: <see cref="Size"/>, written by the engine.</summary>
    public uint StructSize;

    /// <summary><c>step_index</c>: 0-based index of the running step.</summary>
    public uint StepIndex;

    /// <summary><c>step_count</c>: steps in the plan.</summary>
    public uint StepCount;

    /// <summary><c>pass_index</c>: 0-based index of the pass in progress.</summary>
    public uint PassIndex;

    /// <summary><c>passes_planned</c>: the step's current pass schedule; it can change as pass durations are measured.</summary>
    public uint PassesPlanned;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary><c>elapsed_ms</c>: test time in milliseconds, measured from the end of preparation.</summary>
    public ulong ElapsedMs;

    /// <summary><c>step_elapsed_ms</c>: milliseconds since the running step started.</summary>
    public ulong StepElapsedMs;

    /// <summary>
    /// <c>remaining_ms</c>: estimated milliseconds remaining, or -1 ("calculating…") until step 1 completes its first
    /// pass. The only signed field of the API.
    /// </summary>
    public long RemainingMs;

    /// <summary><c>verified_bytes</c>: bytes verified so far in the run.</summary>
    public ulong VerifiedBytes;

    /// <summary><c>region_bytes</c>: bytes under test, equal to <see cref="RqPrepared.TestedBytes"/>.</summary>
    public ulong RegionBytes;

    /// <summary><c>total_errors</c>: the true error total so far; never capped.</summary>
    public ulong TotalErrors;
}

/// <summary>Mirror of <c>rq_error_record</c>, one confirmed error and an element of the <c>on_errors</c> batch.</summary>
/// <remarks>An array element: it carries no <c>struct_size</c>. Engine-owned and valid only during the callback.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqErrorRecord
{
    /// <summary>Size of <c>rq_error_record</c> in bytes.</summary>
    internal const int Size = 112;

    /// <summary><c>sequence</c>: confirmation order, starting at 0 in each session.</summary>
    public ulong Sequence;

    /// <summary><c>address</c>: address of the mismatching 8-byte word, of kind <see cref="AddressKind"/>.</summary>
    public ulong Address;

    /// <summary><c>region_offset</c>: logical byte offset of the word in the tested region (not address minus a base).</summary>
    public ulong RegionOffset;

    /// <summary><c>expected</c>: the value the word should hold.</summary>
    public ulong Expected;

    /// <summary><c>actual</c>: the value the verification load returned.</summary>
    public ulong Actual;

    /// <summary><c>reread</c>: the value of the cache-flushed re-read.</summary>
    public ulong Reread;

    /// <summary><c>bit_mask</c>: <see cref="Expected"/> XOR <see cref="Actual"/>.</summary>
    public ulong BitMask;

    /// <summary><c>time_since_write_us</c>: microseconds from the end of the stream that last wrote the word to the verification load.</summary>
    public ulong TimeSinceWriteUs;

    /// <summary><c>time_into_test_ms</c>: test time of the confirmation in milliseconds.</summary>
    public ulong TimeIntoTestMs;

    /// <summary><c>step_index</c>: 0-based index of the step.</summary>
    public uint StepIndex;

    /// <summary><c>pass_index</c>: 0-based index of the pass.</summary>
    public uint PassIndex;

    /// <summary><c>access_type</c>: a <c>NativeConstants.Access*</c> value.</summary>
    public uint AccessType;

    /// <summary><c>reread_outcome</c>: a <c>NativeConstants.Reread*</c> value.</summary>
    public uint RereadOutcome;

    /// <summary><c>address_kind</c>: a <c>NativeConstants.AddressKind*</c> value describing <see cref="Address"/>.</summary>
    public uint AddressKind;

    /// <summary><c>worker_index</c>: 0-based index of the worker that verified the word.</summary>
    public uint WorkerIndex;

    /// <summary><c>logical_processor</c>: the worker's processor number within its processor group.</summary>
    public uint LogicalProcessor;

    /// <summary><c>processor_group</c>: the worker's processor group.</summary>
    public uint ProcessorGroup;

    /// <summary><c>active_workers</c>: workers active in the step, all of them or half.</summary>
    public uint ActiveWorkers;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;
}

/// <summary>
/// Mirror of <c>rq_run_result</c>, the payload of <c>on_completed</c> and the header copied by
/// <c>rq_session_get_result</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RqRunResult
{
    /// <summary>Size of <c>rq_run_result</c> in bytes, the value <see cref="StructSize"/> must hold.</summary>
    internal const int Size = 192;

    /// <summary><c>struct_size</c>: set by the caller of <c>rq_session_get_result</c> to <see cref="Size"/>.</summary>
    public uint StructSize;

    /// <summary><c>end_reason</c>: a <c>NativeConstants.End*</c> value, the first recorded end reason.</summary>
    public uint EndReason;

    /// <summary><c>steps_completed</c>: steps whose result has <see cref="RqStepResult.Completed"/> set.</summary>
    public uint StepsCompleted;

    /// <summary><c>step_result_count</c>: entries in <see cref="StepResults"/>, one per step whose gate continued.</summary>
    public uint StepResultCount;

    /// <summary><c>total_errors</c>: the true error total; never capped.</summary>
    public ulong TotalErrors;

    /// <summary><c>verified_bytes</c>: bytes verified in the run.</summary>
    public ulong VerifiedBytes;

    /// <summary><c>elapsed_ms</c>: test time at completion in milliseconds; 0 when preparation failed.</summary>
    public ulong ElapsedMs;

    /// <summary><c>first_error_ms</c>: test time of the first confirmed error, or <see cref="NativeConstants.NoFirstError"/>.</summary>
    public ulong FirstErrorMs;

    /// <summary>
    /// <c>step_results</c>: <see cref="StepResultCount"/> results in step order; null only when the count is 0.
    /// Engine-owned: valid only during <c>on_completed</c>, or until <c>rq_session_destroy</c> when copied by
    /// <c>rq_session_get_result</c>.
    /// </summary>
    public RqStepResult* StepResults;

    /// <summary><c>os_error</c>: the Windows error code of the failing call for affinity, memory and thread-creation faults; otherwise 0.</summary>
    public uint OsError;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary>
    /// <c>end_detail</c>: a short English diagnostic, UTF-8 and NUL-terminated, for every end reason except
    /// <see cref="NativeConstants.EndCompleted"/>, where it is empty.
    /// </summary>
    public fixed byte EndDetail[NativeConstants.EndDetailSize];
}

/// <summary>
/// Reads and writes the fixed-size, NUL-terminated UTF-8 text fields of the C API: <see cref="RqStepDef.Name"/>,
/// <see cref="RqStepStart.Name"/>, <see cref="RqSystemInfo.CpuName"/> and <see cref="RqRunResult.EndDetail"/>.
/// </summary>
internal static unsafe class FixedUtf8
{
    /// <summary>
    /// Decodes the text of a fixed UTF-8 field: up to its first NUL, or the whole field when it holds none.
    /// </summary>
    /// <param name="buffer">The first byte of the field; may be null only when <paramref name="capacity"/> is 0.</param>
    /// <param name="capacity">The size of the field in bytes, for example <see cref="NativeConstants.NameSize"/>.</param>
    /// <returns>
    /// The decoded text, with invalid UTF-8 sequences replaced by U+FFFD; <see cref="string.Empty"/> when the field
    /// is empty.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="buffer"/> is null and <paramref name="capacity"/> is not 0.
    /// </exception>
    internal static string Read(byte* buffer, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity == 0)
        {
            return string.Empty;
        }

        ArgumentNullException.ThrowIfNull(buffer);

        // The search never leaves the field, so a field the engine failed to terminate is read to its end and never
        // past it.
        var field = new ReadOnlySpan<byte>(buffer, capacity);
        int length = field.IndexOf((byte)0);
        if (length >= 0)
        {
            field = field[..length];
        }

        return field.IsEmpty ? string.Empty : Encoding.UTF8.GetString(field);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as UTF-8 into a fixed field, truncated to at most
    /// <paramref name="capacity"/> − 1 bytes at a character boundary, NUL-terminated, with the rest of the field
    /// zeroed.
    /// </summary>
    /// <param name="value">The text to write; unpaired surrogates are written as U+FFFD.</param>
    /// <param name="buffer">The first byte of the field.</param>
    /// <param name="capacity">The size of the field in bytes, at least 1 for the NUL terminator.</param>
    /// <returns>The number of text bytes written, excluding the NUL terminator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a NUL character.</exception>
    internal static int Write(string value, byte* buffer, int capacity)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        // The engine reads a field only up to its first NUL, so an embedded NUL would silently shorten the text it
        // sees; that is a caller error, not something to truncate quietly.
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Text for a fixed UTF-8 field must not contain a NUL character.", nameof(value));
        }

        var field = new Span<byte>(buffer, capacity);

        // Utf8.FromUtf16 transcodes whole scalar values only: when the text does not fit it stops before the first
        // scalar that would overflow, so the cut never splits a multi-byte sequence, which is the boundary rule the
        // engine applies to its own fields. With replacement on and a final block, Done and DestinationTooSmall are
        // the only possible statuses, and bytesWritten is correct for both.
        _ = Utf8.FromUtf16(value, field[..^1], out _, out int written, replaceInvalidSequences: true, isFinalBlock: true);

        // Zeroing from the end of the text writes the terminator and leaves no stale bytes behind it.
        field[written..].Clear();
        return written;
    }

    /// <summary>
    /// Returns the number of UTF-8 bytes <paramref name="value"/> encodes to, excluding any terminator; a step name
    /// fits a name field when this is at most <see cref="NativeConstants.NameSize"/> − 1.
    /// </summary>
    /// <param name="value">The text to measure; unpaired surrogates count as U+FFFD, as <see cref="Write"/> writes them.</param>
    /// <returns>The encoded length in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    internal static int GetByteCount(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value);
    }
}

