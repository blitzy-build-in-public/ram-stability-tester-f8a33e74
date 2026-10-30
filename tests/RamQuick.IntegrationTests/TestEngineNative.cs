using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RamQuick.App.Services;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;

namespace RamQuick.IntegrationTests;

/// <summary>
/// The managed declarations of the six <c>rq_test_*</c> exports of <c>ramquick_engine_test.dll</c>
/// (<c>tests/engine.tests/test_dll/ramquick_engine_test.h</c>). This is the only managed declaration of those exports;
/// tests reach them through <see cref="TestEngines"/> and <see cref="SessionProbe"/>.
/// </summary>
/// <remarks>
/// <para>
/// No resolver is registered here. Default probing loads the library from this project's output root, the directory
/// from which <c>EngineLibrary</c>'s resolver loads it once <see cref="EngineBindingFixture"/> has bound the wrapper, so
/// the loader maps one module: sessions created through the wrapper and these exports share one session registry and
/// one live-session count.
/// </para>
/// <para>
/// Handles are passed as raw <see cref="nint"/> values rather than as <c>SafeSessionHandle</c>, so the source generator
/// never needs a public parameterless SafeHandle constructor; <see cref="SessionProbe"/> keeps the handle alive with
/// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> for every call.
/// </para>
/// </remarks>
internal static unsafe partial class TestEngineNative
{
    /// <summary>Native <c>RQ_OK</c>.</summary>
    internal const int ResultOk = 0;

    /// <summary>Native <c>RQ_FAULT_RANDOM_BIT</c>: a seeded random bit for each injection.</summary>
    internal const uint RandomBit = uint.MaxValue;

    /// <summary>Native <c>RQ_FAULT_NO_WORKER</c>: no worker's pin fails.</summary>
    internal const uint NoAffinityFailWorker = uint.MaxValue;

    /// <summary>Native <c>RQ_FAULT_DEFAULT_DELAY_THRESHOLD_MS</c>, applied when <c>delay_threshold_ms</c> is 0.</summary>
    internal const uint DefaultDelayThresholdMs = 2000;

    private const string Library = EngineBindingFixture.TestEngineFileName;

    /// <summary><c>rq_test_session_create</c>: <c>rq_session_create</c> over the fault-injecting layer.</summary>
    /// <param name="config">The session configuration; non-null.</param>
    /// <param name="callbacks">The callback table; non-null.</param>
    /// <param name="fault">The fault specification, or null for none.</param>
    /// <param name="session">Receives the new handle.</param>
    /// <returns>The native <c>rq_result</c>.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_session_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int SessionCreate(
        RqSessionConfig* config,
        RqCallbacks* callbacks,
        RqFaultSpec* fault,
        nint* session);

    /// <summary><c>rq_test_get_independent_verified_bytes</c>: the counting layer's verified-byte total.</summary>
    /// <param name="session">A handle created by <c>rq_test_session_create</c>.</param>
    /// <param name="bytes">Receives the total.</param>
    /// <returns>The native <c>rq_result</c>.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_get_independent_verified_bytes")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int GetIndependentVerifiedBytes(nint session, ulong* bytes);

    /// <summary><c>rq_test_get_injection_log</c>: copies up to <paramref name="cap"/> entries of the injection log.</summary>
    /// <param name="session">A handle created by <c>rq_test_session_create</c>.</param>
    /// <param name="buffer">The destination of <paramref name="cap"/> entries; null only when <paramref name="cap"/> is 0.</param>
    /// <param name="cap">The capacity of <paramref name="buffer"/>.</param>
    /// <param name="count">Receives the total number of entries logged.</param>
    /// <returns>The native <c>rq_result</c>.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_get_injection_log")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int GetInjectionLog(nint session, RqInjectedFault* buffer, uint cap, uint* count);

    /// <summary><c>rq_test_live_session_count</c>: the sessions of this DLL not yet destroyed.</summary>
    /// <returns>The live-session count.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_live_session_count")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial uint LiveSessionCount();

    /// <summary><c>rq_test_pending_stop_reason</c>: the <c>RQ_END_*</c> reason recorded so far, or 0.</summary>
    /// <param name="session">Any session handle.</param>
    /// <param name="reason">Receives the reason.</param>
    /// <returns>The native <c>rq_result</c>.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_pending_stop_reason")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int PendingStopReason(nint session, uint* reason);

    /// <summary><c>rq_test_struct_sizes</c>: the engine's <c>sizeof</c> of every ABI struct.</summary>
    /// <param name="sizes">Caller-owned, with <see cref="RqStructSizes.StructSize"/> set.</param>
    /// <returns>The native <c>rq_result</c>.</returns>
    [LibraryImport(Library, EntryPoint = "rq_test_struct_sizes")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int StructSizes(RqStructSizes* sizes);

    /// <summary>
    /// Describes a native <c>rq_result</c> for an exception message: its <c>RQ_*</c> name and value.
    /// </summary>
    /// <param name="result">The result code.</param>
    /// <returns>For example <c>RQ_E_INVALID_ARGUMENT (-1)</c>.</returns>
    internal static string DescribeResult(int result)
    {
        string name = result switch
        {
            0 => "RQ_OK",
            -1 => "RQ_E_INVALID_ARGUMENT",
            -2 => "RQ_E_INVALID_HANDLE",
            -3 => "RQ_E_INVALID_STATE",
            -4 => "RQ_E_INSUFFICIENT_MEMORY",
            -5 => "RQ_E_LOCK_FAILED",
            -6 => "RQ_E_THREAD_CREATE",
            -7 => "RQ_E_VERSION_MISMATCH",
            -8 => "RQ_E_AFFINITY_FAILED",
            -99 => "RQ_E_INTERNAL",
            _ => "unknown rq_result",
        };

        return string.Create(CultureInfo.InvariantCulture, $"{name} ({result})");
    }

    /// <summary>Throws when an <c>rq_test_*</c> export did not return <c>RQ_OK</c>.</summary>
    /// <param name="result">The export's result.</param>
    /// <param name="export">The export's name, for the message.</param>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> is not <c>RQ_OK</c>.</exception>
    internal static void ThrowIfFailed(int result, string export)
    {
        if (result != ResultOk)
        {
            throw new InvalidOperationException($"{export} failed with {DescribeResult(result)}.");
        }
    }
}

/// <summary>The fault kinds of <c>rq_fault_spec.kind</c> (<c>RQ_FAULT_*</c>).</summary>
internal enum FaultKind : uint
{
    /// <summary><c>RQ_FAULT_NONE</c>: every access passes through unchanged.</summary>
    None = 0,

    /// <summary><c>RQ_FAULT_SINGLE_BIT_FLIP</c>: one fixed bit flipped in spread words per (step, pass).</summary>
    SingleBitFlip = 1,

    /// <summary><c>RQ_FAULT_STUCK_BIT</c>: one bit of one word forced to a value at every verification.</summary>
    StuckBit = 2,

    /// <summary><c>RQ_FAULT_ADDRESS_REGION</c>: a random bit flipped in words inside one contiguous range.</summary>
    AddressRegion = 3,

    /// <summary><c>RQ_FAULT_DELAY_DEPENDENT</c>: a random bit flipped in words held at least the delay threshold.</summary>
    DelayDependent = 4,
}

/// <summary>
/// The sizes of the test-only ABI structs fixed by <c>ramquick_engine_test.h</c>, against which the mirrors below are
/// checked before any of them crosses the boundary.
/// </summary>
internal static class ExpectedSizes
{
    /// <summary><c>sizeof(rq_fault_spec)</c>: 80, with the per-step pass quota and the hold-entered event.</summary>
    public const int FaultSpec = 80;

    /// <summary><c>sizeof(rq_injected_fault)</c>, with the corrupted word's address and the injection time.</summary>
    public const int InjectedFault = 56;

    /// <summary><c>sizeof(rq_struct_sizes)</c>.</summary>
    public const int StructSizes = 56;

    /// <summary>
    /// Throws unless every mirror has its native size. A mismatched mirror would make the engine read or write past
    /// the managed struct instead of failing, so the check runs before every call that passes one.
    /// </summary>
    /// <exception cref="InvalidOperationException">A mirror's size differs from the native size.</exception>
    public static void EnsureMirrorsMatch()
    {
        Check(Unsafe.SizeOf<RqFaultSpec>(), FaultSpec, nameof(RqFaultSpec));
        Check(Unsafe.SizeOf<RqInjectedFault>(), InjectedFault, nameof(RqInjectedFault));
        Check(Unsafe.SizeOf<RqStructSizes>(), StructSizes, nameof(RqStructSizes));
    }

    private static void Check(int actual, int expected, string mirror)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{mirror} is {actual} bytes, but its native struct is {expected} bytes; the mirror no longer matches ramquick_engine_test.h."));
        }
    }
}

/// <summary>
/// Mirror of <c>rq_fault_spec</c> (80 bytes): natural alignment, fields in native order.
/// <see cref="InjectedPassesPerStep"/> aligns <see cref="Seed"/> to 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RqFaultSpec
{
    /// <summary><c>struct_size</c>: <see cref="ExpectedSizes.FaultSpec"/>.</summary>
    public uint StructSize;

    /// <summary><c>kind</c>: a <see cref="FaultKind"/> value.</summary>
    public uint Kind;

    /// <summary><c>bit</c>: 0-63, or <see cref="TestEngineNative.RandomBit"/>.</summary>
    public uint Bit;

    /// <summary><c>per_pass_count</c>: words corrupted per (step, pass), 1 to 2^20 where the kind needs it.</summary>
    public uint PerPassCount;

    /// <summary><c>max_injections</c>: the global cap on logged corruptions.</summary>
    public ulong MaxInjections;

    /// <summary><c>region_offset</c>: start of the target range, or the stuck word, as a logical region offset.</summary>
    public ulong RegionOffset;

    /// <summary><c>region_length</c>: length of the target range; 0 means the rest of the region.</summary>
    public ulong RegionLength;

    /// <summary><c>stuck_value</c>: bit 0 is the value a stuck bit is forced to.</summary>
    public ulong StuckValue;

    /// <summary><c>delay_threshold_ms</c>: minimum time since write of a delay-dependent fault; 0 means 2000.</summary>
    public uint DelayThresholdMs;

    /// <summary><c>affinity_fail_worker</c>: the worker whose pin fails, or <see cref="TestEngineNative.NoAffinityFailWorker"/>.</summary>
    public uint AffinityFailWorker;

    /// <summary><c>affinity_fail_error</c>: the Windows error the failing pin reports; nonzero with a named worker.</summary>
    public uint AffinityFailError;

    /// <summary><c>injected_passes_per_step</c>: passes of each step that inject, from pass 0; 0 means every pass.</summary>
    public uint InjectedPassesPerStep;

    /// <summary><c>seed</c>: seed of target-word selection and of the random bit choice.</summary>
    public ulong Seed;

    /// <summary><c>hold_entered_event</c>: a Win32 event handle signalled each time a hold begins, or 0 for none.</summary>
    public ulong HoldEnteredEvent;
}

/// <summary>Mirror of <c>rq_injected_fault</c> (56 bytes), the ground truth of one applied corruption.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RqInjectedFault
{
    /// <summary><c>word_offset</c>: logical region byte offset of the corrupted 8-byte word.</summary>
    public ulong WordOffset;

    /// <summary><c>bit_mask</c>: expected value XOR stored value.</summary>
    public ulong BitMask;

    /// <summary><c>bit</c>: index 0-63 of the corrupted bit.</summary>
    public uint Bit;

    /// <summary><c>step_index</c>: zero-based step whose verification the corruption targets.</summary>
    public uint StepIndex;

    /// <summary><c>pass_index</c>: zero-based pass whose verification the corruption targets.</summary>
    public uint PassIndex;

    /// <summary><c>access_type</c>: an <c>RQ_ACCESS_*</c> value, equal to <see cref="RamQuick.Core.AccessType"/>.</summary>
    public uint AccessType;

    /// <summary><c>verified_opportunity</c>: 1 once the verification that follows the injection has run.</summary>
    public uint VerifiedOpportunity;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;

    /// <summary><c>address</c>: virtual address of the corrupted 8-byte word, the pointer the corruption was stored through.</summary>
    public ulong Address;

    /// <summary><c>injected_ns</c>: session-clock nanoseconds read just before the call's corruptions were stored.</summary>
    public ulong InjectedNs;
}

/// <summary>Mirror of <c>rq_struct_sizes</c> (56 bytes): the engine's <c>sizeof</c> of every ABI struct.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RqStructSizes
{
    /// <summary><c>struct_size</c>: set by the caller to <see cref="ExpectedSizes.StructSizes"/>.</summary>
    public uint StructSize;

    /// <summary><c>sizeof(rq_system_info)</c>: 112.</summary>
    public uint SystemInfo;

    /// <summary><c>sizeof(rq_step_def)</c>: 96.</summary>
    public uint StepDef;

    /// <summary><c>sizeof(rq_session_config)</c>: 64.</summary>
    public uint SessionConfig;

    /// <summary><c>sizeof(rq_callbacks)</c>: 72.</summary>
    public uint Callbacks;

    /// <summary><c>sizeof(rq_prepared)</c>: 40.</summary>
    public uint Prepared;

    /// <summary><c>sizeof(rq_step_start)</c>: 80.</summary>
    public uint StepStart;

    /// <summary><c>sizeof(rq_step_result)</c>: 48.</summary>
    public uint StepResult;

    /// <summary><c>sizeof(rq_progress)</c>: 72.</summary>
    public uint Progress;

    /// <summary><c>sizeof(rq_error_record)</c>: 112.</summary>
    public uint ErrorRecord;

    /// <summary><c>sizeof(rq_run_result)</c>: 192.</summary>
    public uint RunResult;

    /// <summary><c>sizeof(rq_fault_spec)</c>: 80.</summary>
    public uint FaultSpec;

    /// <summary><c>sizeof(rq_injected_fault)</c>: 56.</summary>
    public uint InjectedFault;

    /// <summary><c>reserved0</c>: written as 0.</summary>
    public uint Reserved0;
}

/// <summary>
/// An immutable fault specification for <see cref="TestEngines.Create(FaultSpec?)"/>, converted to <c>rq_fault_spec</c>
/// by <see cref="ToNative"/>. The factories build the four fault classes of the integration tests and the injected
/// affinity failure; <see langword="with"/> adjusts any field.
/// </summary>
/// <remarks>
/// Offsets are logical region byte offsets, the worker slices concatenated in worker order: the frame of
/// <see cref="ErrorRecord.RegionOffset"/> and <see cref="InjectedFault.WordOffset"/>. The factories check what can be
/// checked without the region size and the worker count; the engine rejects the rest, such as a range outside the
/// region, with <c>RQ_E_INVALID_ARGUMENT</c>, which session creation reports as <see cref="ArgumentException"/>.
/// </remarks>
internal sealed record FaultSpec
{
    private const ulong WordBytes = 8;
    private const int MaxBitIndex = 63;
    private const int MaxPerPassCount = 1 << 20;

    /// <summary>Gets the specification that injects nothing: every access passes through the fault layer unchanged.</summary>
    public static FaultSpec None { get; } = new();

    /// <summary>Gets the fault kind. Defaults to <see cref="FaultKind.None"/>.</summary>
    public FaultKind Kind { get; init; }

    /// <summary>Gets the bit index 0-63 to corrupt, or <see cref="TestEngineNative.RandomBit"/>, the default.</summary>
    public uint Bit { get; init; } = TestEngineNative.RandomBit;

    /// <summary>Gets the words corrupted per (step, pass); required by every kind except stuck bit and none.</summary>
    public uint PerPassCount { get; init; }

    /// <summary>Gets the global cap on logged corruptions; at least 1 for every kind except none.</summary>
    public ulong MaxInjections { get; init; }

    /// <summary>Gets the start of the target range, or the stuck word's offset.</summary>
    public ulong RegionOffset { get; init; }

    /// <summary>Gets the length of the target range in bytes; 0, the default, means the rest of the region.</summary>
    public ulong RegionLength { get; init; }

    /// <summary>Gets the value, in bit 0, that a stuck bit is forced to.</summary>
    public ulong StuckValue { get; init; }

    /// <summary>
    /// Gets the minimum time since write, in milliseconds, of a delay-dependent fault. Defaults to
    /// <see cref="TestEngineNative.DefaultDelayThresholdMs"/>.
    /// </summary>
    public uint DelayThresholdMs { get; init; } = TestEngineNative.DefaultDelayThresholdMs;

    /// <summary>
    /// Gets the index of the worker whose pin to its logical processor fails, or
    /// <see cref="TestEngineNative.NoAffinityFailWorker"/>, the default.
    /// </summary>
    public uint AffinityFailWorker { get; init; } = TestEngineNative.NoAffinityFailWorker;

    /// <summary>Gets the Windows error the failing pin reports; nonzero whenever a worker is named.</summary>
    public uint AffinityFailError { get; init; }

    /// <summary>Gets the seed of target-word selection and of the random bit choice.</summary>
    public ulong Seed { get; init; }

    /// <summary>
    /// Gets the per-step pass quota: only passes whose index is below it inject, so later passes of every step run
    /// clean. 0, the default, means every pass.
    /// </summary>
    /// <remarks>
    /// With <see cref="PerPassCount"/> it bounds each step's corruptions by the plan alone, so a correctness test can
    /// spread its expected reports over every step and keep <see cref="MaxInjections"/> out of reach, instead of having
    /// the cap spent, in time order, before the last step.
    /// </remarks>
    public uint InjectedPassesPerStep { get; init; }

    /// <summary>
    /// Gets the Win32 event handle the engine signals each time a hold begins: once every worker has written its slice
    /// and fenced its pre-hold flush, and the coordinator starts waiting the hold. 0, the default, means none.
    /// </summary>
    /// <remarks>
    /// Session creation duplicates the handle, so the caller keeps ownership of its event, which must stay open until
    /// the session has been created; a handle that cannot be duplicated fails creation with <c>RQ_E_INVALID_ARGUMENT</c>.
    /// Honoured for <see cref="FaultKind.None"/> too, so a test that injects nothing can wait for the hold.
    /// </remarks>
    public nint HoldEnteredEvent { get; init; }

    /// <summary>
    /// Flips bit <paramref name="bit"/> in <paramref name="perPass"/> words per (step, pass), at seeded offsets spread
    /// over the whole region. The flipped value is stored, so every report is expected to repeat it on re-read.
    /// </summary>
    /// <param name="bit">The bit index, 0-63.</param>
    /// <param name="perPass">Words corrupted per (step, pass), 1 to 2^20.</param>
    /// <param name="max">The cap on logged corruptions, at least 1.</param>
    /// <param name="seed">The target-selection seed.</param>
    /// <returns>The specification.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static FaultSpec SingleBitFlip(int bit, int perPass, long max, ulong seed) => new()
    {
        Kind = FaultKind.SingleBitFlip,
        Bit = CheckBit(bit),
        PerPassCount = CheckPerPass(perPass),
        MaxInjections = CheckMax(max),
        Seed = seed,
    };

    /// <summary>
    /// Forces bit <paramref name="bit"/> of the word at <paramref name="wordOffset"/> to <paramref name="stuckValue"/>
    /// at every verification of that word. Only verifications whose expected bit differs are logged, and every report
    /// groups into that one word.
    /// </summary>
    /// <param name="wordOffset">The stuck word's logical region offset; a multiple of 8.</param>
    /// <param name="bit">The bit index, 0-63.</param>
    /// <param name="stuckValue">The forced bit value, 0 or 1.</param>
    /// <param name="max">The cap on logged corruptions, at least 1.</param>
    /// <returns>The specification.</returns>
    /// <exception cref="ArgumentException"><paramref name="wordOffset"/> is not 8-byte aligned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Another argument is out of range.</exception>
    public static FaultSpec StuckBit(ulong wordOffset, int bit, ulong stuckValue, long max)
    {
        // The engine would silently use the word containing an unaligned offset; a test computing its expected
        // reports from the offset it passed must pass the word itself.
        if (wordOffset % WordBytes != 0)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The stuck word offset 0x{wordOffset:X} is not 8-byte aligned."),
                nameof(wordOffset));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(stuckValue, 1UL);

        return new FaultSpec
        {
            Kind = FaultKind.StuckBit,
            Bit = CheckBit(bit),
            RegionOffset = wordOffset,
            StuckValue = stuckValue,
            MaxInjections = CheckMax(max),
        };
    }

    /// <summary>
    /// Flips a seeded random bit in <paramref name="perPass"/> words per (step, pass), every one inside
    /// [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>).
    /// </summary>
    /// <param name="offset">The start of the range, a logical region offset.</param>
    /// <param name="length">The length of the range in bytes, holding at least one whole aligned word; 0 for the rest of the region.</param>
    /// <param name="perPass">Words corrupted per (step, pass), 1 to 2^20.</param>
    /// <param name="max">The cap on logged corruptions, at least 1.</param>
    /// <param name="seed">The target-selection and bit seed.</param>
    /// <returns>The specification.</returns>
    /// <exception cref="ArgumentException">The range holds no whole aligned 8-byte word.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Another argument is out of range.</exception>
    public static FaultSpec AddressRegion(ulong offset, ulong length, int perPass, long max, ulong seed)
    {
        if (length != 0 && !HoldsWholeWord(offset, length))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The range [0x{offset:X}, +0x{length:X}) holds no whole aligned 8-byte word."),
                nameof(length));
        }

        return new FaultSpec
        {
            Kind = FaultKind.AddressRegion,
            PerPassCount = CheckPerPass(perPass),
            MaxInjections = CheckMax(max),
            RegionOffset = offset,
            RegionLength = length,
            Seed = seed,
        };
    }

    /// <summary>
    /// Flips a seeded random bit, just before the verification load, in up to <paramref name="perPass"/> words per
    /// (step, pass) whose time since write is at least <see cref="DelayThresholdMs"/> (2000 ms unless changed with
    /// <see langword="with"/>).
    /// </summary>
    /// <remarks>
    /// The test library measures that age on its controlled hold clock, not on the real clock: the age advances only by
    /// each hold's configured duration, once every worker has written and flushed its slice and the hold has begun.
    /// Only verifications after a hold therefore see data at or above the threshold, whatever the scheduling of the
    /// workers in real time, and a windowed verification that a descheduled worker runs seconds late still sees age 0.
    /// </remarks>
    /// <param name="perPass">Words corrupted per (step, pass), 1 to 2^20.</param>
    /// <param name="max">The cap on logged corruptions, at least 1.</param>
    /// <param name="seed">The target-selection and bit seed.</param>
    /// <returns>The specification, targeting the whole region.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static FaultSpec DelayDependent(int perPass, long max, ulong seed) => new()
    {
        Kind = FaultKind.DelayDependent,
        PerPassCount = CheckPerPass(perPass),
        MaxInjections = CheckMax(max),
        Seed = seed,
    };

    /// <summary>
    /// Injects no corruption, but makes the pin of worker <paramref name="worker"/> fail with
    /// <paramref name="windowsError"/>, so preparation ends the run with <c>affinity_failed</c> and no memory traffic.
    /// </summary>
    /// <param name="worker">The worker index, below the session's worker count.</param>
    /// <param name="windowsError">The Windows error the failing pin reports, for example 87; nonzero.</param>
    /// <returns>The specification.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static FaultSpec AffinityFailure(int worker, uint windowsError)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(worker);
        ArgumentOutOfRangeException.ThrowIfZero(windowsError);

        return new FaultSpec
        {
            AffinityFailWorker = (uint)worker,
            AffinityFailError = windowsError,
        };
    }

    /// <summary>Converts the specification to the native struct, with <c>struct_size</c> <see cref="ExpectedSizes.FaultSpec"/>.</summary>
    /// <returns>The native specification.</returns>
    public RqFaultSpec ToNative() => new()
    {
        StructSize = ExpectedSizes.FaultSpec,
        Kind = (uint)Kind,
        Bit = Bit,
        PerPassCount = PerPassCount,
        MaxInjections = MaxInjections,
        RegionOffset = RegionOffset,
        RegionLength = RegionLength,
        StuckValue = StuckValue,
        DelayThresholdMs = DelayThresholdMs,
        AffinityFailWorker = AffinityFailWorker,
        AffinityFailError = AffinityFailError,
        InjectedPassesPerStep = InjectedPassesPerStep,
        Seed = Seed,

        // The handle's bits unchanged: a HANDLE is pointer-sized, and the native field carries it as an integer.
        HoldEnteredEvent = unchecked((ulong)HoldEnteredEvent),
    };

    private static uint CheckBit(int bit, [CallerArgumentExpression(nameof(bit))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, MaxBitIndex, name);
        return (uint)bit;
    }

    private static uint CheckPerPass(int perPass, [CallerArgumentExpression(nameof(perPass))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perPass, 1, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perPass, MaxPerPassCount, name);
        return (uint)perPass;
    }

    private static ulong CheckMax(long max, [CallerArgumentExpression(nameof(max))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1L, name);
        return (ulong)max;
    }

    // The native rule: the first aligned word at or after offset must end within the range.
    private static bool HoldsWholeWord(ulong offset, ulong length)
    {
        ulong end = offset + length;
        if (end < offset)
        {
            return false;
        }

        ulong firstWord = (offset + (WordBytes - 1)) & ~(WordBytes - 1);
        return firstWord >= offset && firstWord + WordBytes <= end;
    }
}

/// <summary>
/// The ground truth of one corruption the fault layer applied, copied from <c>rq_injected_fault</c>.
/// </summary>
/// <param name="WordOffset">
/// The logical region byte offset of the corrupted 8-byte word, comparable with
/// <c>ErrorRecord.RegionOffset &amp; ~7</c>.
/// </param>
/// <param name="BitMask">Expected value XOR stored value; the reported <see cref="ErrorRecord.BitMask"/> must equal it.</param>
/// <param name="Bit">The index 0-63 of the corrupted bit.</param>
/// <param name="StepIndex">The zero-based step whose verification the corruption targets.</param>
/// <param name="PassIndex">The zero-based pass whose verification the corruption targets.</param>
/// <param name="AccessType">The access type of that verification.</param>
/// <param name="VerifiedOpportunity">
/// Whether the verification that follows the injection ran; <see langword="false"/> only when a stop or the end of the
/// run intervened, and such entries are excluded from matching.
/// </param>
/// <param name="Address">
/// The virtual address of the corrupted 8-byte word: the pointer the fault layer stored the corruption through, which the
/// reported <see cref="ErrorRecord.Address"/> must equal exactly.
/// </param>
/// <param name="InjectedNs">
/// The session clock's reading in nanoseconds, taken once per verification call just before that call's corruptions were
/// stored, so it precedes the error each one causes; <see cref="InjectedAt"/> is the same instant as a
/// <see cref="Stopwatch"/> timestamp.
/// </param>
internal sealed record InjectedFault(
    ulong WordOffset,
    ulong BitMask,
    int Bit,
    int StepIndex,
    int PassIndex,
    RamQuick.Core.AccessType AccessType,
    bool VerifiedOpportunity,
    ulong Address,
    ulong InjectedNs)
{
    private const ulong NanosecondsPerSecond = 1_000_000_000;

    /// <summary>
    /// Gets <see cref="InjectedNs"/> as a <see cref="Stopwatch.GetTimestamp"/> value, comparable with the receipt
    /// timestamps <see cref="InstrumentedRun"/> records.
    /// </summary>
    /// <remarks>
    /// The test engine's session clock reads <c>QueryPerformanceCounter</c> as floor(count × 10^9 / frequency), and a
    /// high-resolution <see cref="Stopwatch"/> reads the same counter at the same frequency. The timestamp is therefore
    /// floor(ns × frequency / 10^9), computed as (ns / 10^9) × frequency + (ns mod 10^9) × frequency / 10^9 in 128-bit
    /// arithmetic so no intermediate product overflows. Both floors round down, so the timestamp is at or before the
    /// counter value the engine read, and a latency measured from it is an upper bound on the true latency.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="Stopwatch"/> does not read the high-resolution counter.</exception>
    /// <exception cref="OverflowException">The timestamp exceeds <see cref="long.MaxValue"/>.</exception>
    public long InjectedAt
    {
        get
        {
            if (!Stopwatch.IsHighResolution)
            {
                throw new InvalidOperationException(
                    "Stopwatch does not read QueryPerformanceCounter, so the engine's session-clock times have no Stopwatch equivalent.");
            }

            ulong frequency = (ulong)Stopwatch.Frequency;
            UInt128 wholeSecondTicks = (UInt128)(InjectedNs / NanosecondsPerSecond) * frequency;
            UInt128 fractionTicks = (UInt128)(InjectedNs % NanosecondsPerSecond) * frequency / NanosecondsPerSecond;
            return checked((long)(wholeSecondTicks + fractionTicks));
        }
    }

    /// <summary>Copies a native log entry.</summary>
    /// <param name="native">The entry.</param>
    /// <returns>The managed entry.</returns>
    /// <exception cref="InvalidOperationException">The entry carries an undefined access type.</exception>
    public static InjectedFault From(in RqInjectedFault native)
    {
        var accessType = (RamQuick.Core.AccessType)native.AccessType;
        if (!Enum.IsDefined(accessType))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"rq_test_get_injection_log reported the undefined access type {native.AccessType} for word 0x{native.WordOffset:X}."));
        }

        return new InjectedFault(
            native.WordOffset,
            native.BitMask,
            checked((int)native.Bit),
            checked((int)native.StepIndex),
            checked((int)native.PassIndex),
            accessType,
            native.VerifiedOpportunity != 0,
            native.Address,
            native.InjectedNs);
    }
}


/// <summary>
/// Creates <see cref="NativeEngine"/> instances over the test-only engine library this process is bound to.
/// </summary>
/// <remarks>
/// <see cref="NativeEngine"/>'s constructor is the bind check (<c>rq_api_version</c>), so the
/// <see cref="EngineBindingFixture"/> must have bound the wrapper first; an assembly fixture always has.
/// </remarks>
internal static unsafe class TestEngines
{
    /// <summary>
    /// Creates an engine whose sessions are created by <c>rq_test_session_create</c> with <paramref name="fault"/>:
    /// the fault-injecting layer over the counting layer over the real layer, so every <see cref="SessionProbe"/>
    /// query works on its sessions.
    /// </summary>
    /// <param name="fault">The fault to inject, or <see langword="null"/> for none (the same as <see cref="FaultSpec.None"/>).</param>
    /// <returns>The engine.</returns>
    /// <exception cref="Exception"><c>EngineException</c>: the bound library reports another API version.</exception>
    public static NativeEngine Create(FaultSpec? fault = null)
    {
        ExpectedSizes.EnsureMirrorsMatch();

        // Converted once: the record is immutable, and every session of this engine gets the same specification.
        RqFaultSpec? nativeFault = fault?.ToNative();
        return new NativeEngine((config, callbacks, session) =>
        {
            // A local of the lambda, never captured, so it is a fixed stack variable whose address can be passed.
            // The engine copies the specification before rq_test_session_create returns.
            RqFaultSpec local = nativeFault.GetValueOrDefault();
            return TestEngineNative.SessionCreate(config, callbacks, nativeFault.HasValue ? &local : null, session);
        });
    }

    /// <summary>
    /// Creates an engine through <see cref="NativeEngine"/>'s public constructor, whose sessions are created by the
    /// production <c>rq_session_create</c> that the bound test library also exports. The fault-layer queries
    /// (<see cref="SessionProbe.GetInjectionLog"/>, <see cref="SessionProbe.GetIndependentVerifiedBytes"/>) report
    /// <c>RQ_E_INVALID_ARGUMENT</c> on its sessions.
    /// </summary>
    /// <returns>The engine.</returns>
    /// <exception cref="Exception"><c>EngineException</c>: the bound library reports another API version.</exception>
    public static NativeEngine CreateDefault() => new();
}

/// <summary>
/// Queries a live engine session through the <c>rq_test_*</c> exports. Each query keeps the session's handle alive
/// for the duration of the native call, so a concurrent <c>Dispose</c> cannot destroy the session under it.
/// </summary>
internal static unsafe class SessionProbe
{
    /// <summary>The value <see cref="GetPendingStopReason"/> returns while no end reason has been recorded.</summary>
    public const uint NoPendingReason = 0;

    /// <summary>
    /// Reads the counting layer's total of bytes passed to the verify primitives, independent of the engine's coverage
    /// counter. Valid during and after a run, until the session is disposed.
    /// </summary>
    /// <param name="session">A session of an engine from <see cref="TestEngines.Create(FaultSpec?)"/>.</param>
    /// <returns>The independently counted verified bytes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The session is not a native engine session, the export failed, or the session has been disposed
    /// (<see cref="ObjectDisposedException"/>).
    /// </exception>
    public static ulong GetIndependentVerifiedBytes(IEngineSession session)
    {
        SafeSessionHandle handle = AsEngineSession(session).Handle;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            ulong bytes = 0;
            TestEngineNative.ThrowIfFailed(
                TestEngineNative.GetIndependentVerifiedBytes(handle.DangerousGetHandle(), &bytes),
                "rq_test_get_independent_verified_bytes");
            return bytes;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Copies the injection log, one entry per applied corruption in the order applied. It is consistent once
    /// Completed has been raised; during a run it is a snapshot that later injections extend.
    /// </summary>
    /// <param name="session">A session of an engine from <see cref="TestEngines.Create(FaultSpec?)"/>.</param>
    /// <returns>The log; empty when nothing was injected.</returns>
    /// <exception cref="InvalidOperationException">
    /// The session is not a native engine session, an export failed, the log shrank between the count query and the
    /// copy, or the session has been disposed (<see cref="ObjectDisposedException"/>).
    /// </exception>
    public static IReadOnlyList<InjectedFault> GetInjectionLog(IEngineSession session)
    {
        const string ExportName = "rq_test_get_injection_log";

        ExpectedSizes.EnsureMirrorsMatch();
        SafeSessionHandle handle = AsEngineSession(session).Handle;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            nint raw = handle.DangerousGetHandle();

            uint total = 0;
            TestEngineNative.ThrowIfFailed(TestEngineNative.GetInjectionLog(raw, null, 0, &total), ExportName);
            if (total == 0)
            {
                return Array.Empty<InjectedFault>();
            }

            if (total == uint.MaxValue)
            {
                throw new InvalidOperationException($"{ExportName} reported a saturated entry count (UINT32_MAX).");
            }

            var entries = new RqInjectedFault[total];
            uint totalAtCopy = 0;
            fixed (RqInjectedFault* buffer = entries)
            {
                TestEngineNative.ThrowIfFailed(TestEngineNative.GetInjectionLog(raw, buffer, total, &totalAtCopy), ExportName);
            }

            // The log only grows; entries logged between the two calls are beyond the copied snapshot.
            if (totalAtCopy < total)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ExportName} reported {total} entries, then {totalAtCopy}; the injection log must never shrink."));
            }

            var faults = new InjectedFault[total];
            for (int i = 0; i < faults.Length; i++)
            {
                faults[i] = InjectedFault.From(in entries[i]);
            }

            return Array.AsReadOnly(faults);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Reads the end reason the session has recorded so far under the first-writer-wins rule, as a native
    /// <c>RQ_END_*</c> value, or <see cref="NoPendingReason"/>. Works on every session, production or test, and may be
    /// called from inside a step gate.
    /// </summary>
    /// <param name="session">A native engine session.</param>
    /// <returns>The <c>RQ_END_*</c> value, or 0.</returns>
    /// <exception cref="InvalidOperationException">
    /// The session is not a native engine session, the export failed, or the session has been disposed
    /// (<see cref="ObjectDisposedException"/>).
    /// </exception>
    public static uint GetPendingStopReason(IEngineSession session)
    {
        SafeSessionHandle handle = AsEngineSession(session).Handle;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            uint reason = 0;
            TestEngineNative.ThrowIfFailed(
                TestEngineNative.PendingStopReason(handle.DangerousGetHandle(), &reason),
                "rq_test_pending_stop_reason");
            return reason;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Maps a requested stop to the <c>RQ_END_*</c> value <see cref="GetPendingStopReason"/> reports once
    /// <c>rq_session_stop</c> has recorded it first: User â†’ 3, Thermal â†’ 4, HostFault â†’ 5.
    /// </summary>
    /// <param name="reason">The requested stop.</param>
    /// <returns>The expected pending reason.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is undefined.</exception>
    public static uint ExpectedPendingReason(StopReason reason) => reason switch
    {
        StopReason.User => NativeConstants.EndUserStop,
        StopReason.Thermal => NativeConstants.EndThermal,
        StopReason.HostFault => NativeConstants.EndHostFault,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Undefined StopReason."),
    };

    /// <summary>
    /// Returns the number of sessions of the bound library not yet destroyed, created through
    /// <c>rq_session_create</c> and <c>rq_test_session_create</c> alike. A snapshot: finalisation of abandoned sessions
    /// lowers it asynchronously.
    /// </summary>
    /// <returns>The live-session count.</returns>
    public static int LiveSessionCount() => checked((int)TestEngineNative.LiveSessionCount());

    /// <summary>Reads the engine's <c>sizeof</c> of every ABI struct.</summary>
    /// <returns>The sizes, with <see cref="RqStructSizes.StructSize"/> 56.</returns>
    /// <exception cref="InvalidOperationException">The export failed or a mirror's size is wrong.</exception>
    public static RqStructSizes GetStructSizes()
    {
        ExpectedSizes.EnsureMirrorsMatch();
        RqStructSizes sizes = default;
        sizes.StructSize = ExpectedSizes.StructSizes;
        TestEngineNative.ThrowIfFailed(TestEngineNative.StructSizes(&sizes), "rq_test_struct_sizes");
        return sizes;
    }

    /// <summary>
    /// Returns the object the native <c>user_data</c> roots, for weak-reference lifetime assertions only; holding it
    /// strongly keeps nothing but the callback state itself alive.
    /// </summary>
    /// <param name="session">A native engine session.</param>
    /// <returns>The session's callback state.</returns>
    /// <exception cref="InvalidOperationException">The session is not a native engine session.</exception>
    public static object GetCallbackState(IEngineSession session) => AsEngineSession(session).CallbackState;

    /// <summary>Copies the run result through <c>rq_session_get_result</c>.</summary>
    /// <param name="session">A native engine session that has not been disposed.</param>
    /// <returns>The result, or <see langword="null"/> before the engine has completed the run.</returns>
    /// <exception cref="InvalidOperationException">The session is not a native engine session, or has been disposed.</exception>
    public static EngineCompleted? GetResult(IEngineSession session) => AsEngineSession(session).GetResult();

    /// <summary>
    /// Calls the session's own <c>on_completed</c> thunk, through the callback table the session gave the engine, with
    /// <paramref name="result"/>: a terminal payload the engine never produces, such as a null pointer or a step-result
    /// array the ABI forbids. The call runs on the calling thread, which stands in for the coordinator.
    /// </summary>
    /// <param name="session">
    /// A native engine session that has not been disposed and whose own run never reaches <c>on_completed</c>, such as
    /// one never started.
    /// </param>
    /// <param name="result">The payload, valid for the duration of the call, or null.</param>
    /// <exception cref="InvalidOperationException">
    /// The session is not a native engine session, or has been disposed (<see cref="ObjectDisposedException"/>).
    /// </exception>
    public static void RaiseCompleted(IEngineSession session, RqRunResult* result)
    {
        SafeSessionHandle handle = AsEngineSession(session).Handle;
        bool added = false;
        try
        {
            // Held for the call, so a concurrent Dispose cannot free the GCHandle the thunk resolves user_data through.
            handle.DangerousAddRef(ref added);
            RqCallbacks callbacks = CallbackThunks.CreateTable(handle.CallbackUserData);
            callbacks.OnCompleted(callbacks.UserData, result);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Calls the session's own <c>on_errors</c> thunk, through the callback table the session gave the engine, with
    /// <paramref name="records"/>, <paramref name="count"/> and <paramref name="totalErrors"/> exactly as given, so a
    /// test can pass a pointer and count the ABI forbids. The call runs on the calling thread, which stands in for the
    /// coordinator.
    /// </summary>
    /// <param name="session">A native engine session that has not been disposed.</param>
    /// <param name="records">The records, valid for the duration of the call, or null.</param>
    /// <param name="count">The record count the engine would report.</param>
    /// <param name="totalErrors">The true error total the engine would report.</param>
    /// <exception cref="InvalidOperationException">
    /// The session is not a native engine session, or has been disposed (<see cref="ObjectDisposedException"/>).
    /// </exception>
    public static void RaiseErrors(IEngineSession session, RqErrorRecord* records, uint count, ulong totalErrors)
    {
        SafeSessionHandle handle = AsEngineSession(session).Handle;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            RqCallbacks callbacks = CallbackThunks.CreateTable(handle.CallbackUserData);
            callbacks.OnErrors(callbacks.UserData, records, count, totalErrors);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static EngineSession AsEngineSession(IEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session as EngineSession
            ?? throw new InvalidOperationException(
                $"SessionProbe needs a session created by NativeEngine (RamQuick.Interop.EngineSession), not {session.GetType().FullName}.");
    }
}


/// <summary>A value together with the <see cref="Stopwatch.GetTimestamp"/> value at which it was received.</summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <param name="Timestamp">The <see cref="Stopwatch"/> timestamp of receipt.</param>
/// <param name="Value">The value received.</param>
internal readonly record struct Timestamped<T>(long Timestamp, T Value)
{
    /// <summary>Returns the time from <paramref name="startTimestamp"/> to this receipt.</summary>
    /// <param name="startTimestamp">An earlier <see cref="Stopwatch"/> timestamp.</param>
    /// <returns>The elapsed time; negative when <paramref name="startTimestamp"/> is later.</returns>
    public TimeSpan Since(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp, Timestamp);
}

/// <summary>
/// Counts the calls of one session's step gate and progress sink, which run synchronously on the engine's coordinator
/// thread. Holds no reference to the session, its options or its owner.
/// </summary>
internal sealed class CallbackCounters
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(10);

    private int _gateCalls;
    private int _progressSinkCalls;
    private int _gateThreadId;

    /// <summary>Gets the number of step-gate calls so far.</summary>
    public int GateCalls => Volatile.Read(ref _gateCalls);

    /// <summary>Gets the number of progress-sink calls so far.</summary>
    public int ProgressSinkCalls => Volatile.Read(ref _progressSinkCalls);

    /// <summary>Gets the managed thread id of the latest step-gate call (the coordinator), or 0 before the first.</summary>
    public int GateThreadId => Volatile.Read(ref _gateThreadId);

    /// <summary>
    /// Waits until at least <paramref name="gateCalls"/> gate calls and <paramref name="sinkCalls"/> sink calls have
    /// been counted, polling every 10 ms.
    /// </summary>
    /// <param name="gateCalls">The gate calls to wait for.</param>
    /// <param name="sinkCalls">The sink calls to wait for.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes once both counts are reached.</returns>
    /// <exception cref="TimeoutException">The counts were not reached within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task WaitAsync(int gateCalls, int sinkCalls, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gateCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(sinkCalls);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A timeout must be non-negative or infinite.");
        }

        var stopwatch = Stopwatch.StartNew();
        while (GateCalls < gateCalls || ProgressSinkCalls < sinkCalls)
        {
            if (timeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Timed out after {timeout.TotalSeconds:0.###} s waiting for {gateCalls} step-gate and {sinkCalls} "
                    + $"progress-sink calls; counted {GateCalls} and {ProgressSinkCalls}."));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Counts one step-gate call on the calling (coordinator) thread.</summary>
    internal void OnGate()
    {
        // The thread id is written before the count, so a waiter that sees the count also sees the id.
        Volatile.Write(ref _gateThreadId, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _gateCalls);
    }

    /// <summary>Counts one progress-sink call.</summary>
    internal void OnProgressSink() => Interlocked.Increment(ref _progressSinkCalls);
}

/// <summary>
/// What <see cref="InstrumentedEngine"/> observed of one session: event receipts with <see cref="Stopwatch"/>
/// timestamps, the coordinator callback counts, and the fault layer's ground truth snapshotted at Completed.
/// </summary>
/// <remarks>
/// <para>
/// The run refers to its session and the session's callback state only weakly, and holds no controller, journal or
/// options reference, so recording a run never keeps an abandoned session alive (<c>SessionLifetimeTests</c>).
/// </para>
/// <para>
/// Every member is thread-safe: events arrive on the session's captured context, which is the thread pool when no
/// context was current, while the counters are written on the coordinator.
/// </para>
/// </remarks>
internal sealed class InstrumentedRun
{
    private readonly object _sync = new();
    private readonly List<Timestamped<RunProgress>> _progress = new();
    private readonly TaskCompletionSource<EngineCompleted> _completedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<EngineReleased> _releasedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EnginePrepared? _prepared;
    private long? _preparedAt;
    private long? _firstErrorAt;
    private int _errorBatchCount;
    private ulong _latestErrorTotal;
    private EngineCompleted? _completed;
    private long? _completedAt;
    private EngineReleased? _released;
    private long? _releasedAt;
    private IReadOnlyList<InjectedFault>? _injectionLog;
    private ulong? _independentVerifiedBytes;
    private Exception? _probeFailure;

    /// <summary>Creates the record of one session at its creation.</summary>
    /// <param name="counters">The session's coordinator callback counters.</param>
    /// <param name="session">A weak reference to the session.</param>
    /// <param name="callbackState">A weak reference to the session's callback state; without a target for a non-native session.</param>
    internal InstrumentedRun(CallbackCounters counters, WeakReference<IEngineSession> session, WeakReference<object> callbackState)
    {
        Counters = counters;
        Session = session;
        CallbackState = callbackState;
        CreatedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Gets the step-gate and progress-sink counts; zero unless the engine counts callbacks.</summary>
    public CallbackCounters Counters { get; }

    /// <summary>Gets a weak reference to the session.</summary>
    public WeakReference<IEngineSession> Session { get; }

    /// <summary>Gets a weak reference to the session's callback state, the object the native <c>user_data</c> roots.</summary>
    public WeakReference<object> CallbackState { get; }

    /// <summary>Gets the <see cref="Stopwatch"/> timestamp at which the session was created.</summary>
    public long CreatedAt { get; }

    /// <summary>Gets the Prepared payload, or <see langword="null"/> before it arrived.</summary>
    public EnginePrepared? Prepared
    {
        get
        {
            lock (_sync)
            {
                return _prepared;
            }
        }
    }

    /// <summary>Gets the timestamp of the Prepared receipt, or <see langword="null"/> before it arrived.</summary>
    public long? PreparedAt
    {
        get
        {
            lock (_sync)
            {
                return _preparedAt;
            }
        }
    }

    /// <summary>Gets every ProgressChanged payload received so far, in receipt order, with its receipt timestamp.</summary>
    public IReadOnlyList<Timestamped<RunProgress>> Progress
    {
        get
        {
            lock (_sync)
            {
                return _progress.ToArray();
            }
        }
    }

    /// <summary>Gets the most recent progress snapshot, or <see langword="null"/> before the first.</summary>
    public RunProgress? LatestProgress
    {
        get
        {
            lock (_sync)
            {
                return _progress.Count == 0 ? null : _progress[^1].Value;
            }
        }
    }

    /// <summary>
    /// Gets the timestamp of the first ErrorsDetected receipt with a nonzero true total, or <see langword="null"/>
    /// while no error has been reported.
    /// </summary>
    public long? FirstErrorAt
    {
        get
        {
            lock (_sync)
            {
                return _firstErrorAt;
            }
        }
    }

    /// <summary>Gets the number of ErrorsDetected batches received.</summary>
    public int ErrorBatchCount
    {
        get
        {
            lock (_sync)
            {
                return _errorBatchCount;
            }
        }
    }

    /// <summary>Gets the largest true error total any ErrorsDetected batch carried; 0 before the first.</summary>
    public ulong LatestErrorTotal
    {
        get
        {
            lock (_sync)
            {
                return _latestErrorTotal;
            }
        }
    }

    /// <summary>Gets the Completed payload, or <see langword="null"/> before it arrived.</summary>
    public EngineCompleted? Completed
    {
        get
        {
            lock (_sync)
            {
                return _completed;
            }
        }
    }

    /// <summary>Gets the timestamp of the Completed receipt, taken before the fault-layer snapshot.</summary>
    public long? CompletedAt
    {
        get
        {
            lock (_sync)
            {
                return _completedAt;
            }
        }
    }

    /// <summary>Gets the Released payload, or <see langword="null"/> before it arrived.</summary>
    public EngineReleased? Released
    {
        get
        {
            lock (_sync)
            {
                return _released;
            }
        }
    }

    /// <summary>Gets the timestamp of the Released receipt, or <see langword="null"/> before it arrived.</summary>
    public long? ReleasedAt
    {
        get
        {
            lock (_sync)
            {
                return _releasedAt;
            }
        }
    }

    /// <summary>
    /// Gets the injection log snapshotted when Completed was received, while the native session was still alive.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Completed has not been received, or the snapshot failed, for example on a production session, whose fault-layer
    /// queries the engine refuses; <see cref="ProbeFailure"/> is the inner exception.
    /// </exception>
    public IReadOnlyList<InjectedFault> InjectionLog
    {
        get
        {
            lock (_sync)
            {
                return _injectionLog ?? throw NotCaptured("injection log");
            }
        }
    }

    /// <summary>
    /// Gets the counting layer's verified-byte total snapshotted when Completed was received.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Completed has not been received, or the snapshot failed; <see cref="ProbeFailure"/> is the inner exception.
    /// </exception>
    public ulong IndependentVerifiedBytes
    {
        get
        {
            lock (_sync)
            {
                return _independentVerifiedBytes ?? throw NotCaptured("independent verified-byte count");
            }
        }
    }

    /// <summary>Gets why the fault-layer snapshot at Completed failed, or <see langword="null"/>.</summary>
    public Exception? ProbeFailure
    {
        get
        {
            lock (_sync)
            {
                return _probeFailure;
            }
        }
    }

    /// <summary>Gets a task that completes with the Completed payload once it has been received and snapshotted.</summary>
    public Task<EngineCompleted> CompletedTask => _completedSource.Task;

    /// <summary>Gets a task that completes with the Released payload once it has been received.</summary>
    public Task<EngineReleased> ReleasedTask => _releasedSource.Task;

    /// <summary>Records the Prepared receipt.</summary>
    /// <param name="prepared">The payload.</param>
    internal void OnPrepared(EnginePrepared prepared)
    {
        long at = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _prepared = prepared;
            _preparedAt = at;
        }
    }

    /// <summary>Records one ProgressChanged receipt.</summary>
    /// <param name="progress">The payload.</param>
    internal void OnProgress(RunProgress progress)
    {
        long at = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _progress.Add(new Timestamped<RunProgress>(at, progress));
        }
    }

    /// <summary>Records one ErrorsDetected receipt.</summary>
    /// <param name="errors">The payload.</param>
    internal void OnErrors(EngineErrorsDetected errors)
    {
        long at = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _errorBatchCount++;
            _latestErrorTotal = Math.Max(_latestErrorTotal, errors.TotalErrors);
            if (_firstErrorAt is null && errors.TotalErrors > 0)
            {
                _firstErrorAt = at;
            }
        }
    }

    /// <summary>
    /// Records the Completed receipt and snapshots the fault layer's injection log and verified-byte count. The native
    /// session is alive until it is disposed, and this handler runs before the session's owner sees Completed.
    /// </summary>
    /// <param name="session">The session that raised the event (its sender), or <see langword="null"/>.</param>
    /// <param name="completed">The payload.</param>
    internal void OnCompleted(IEngineSession? session, EngineCompleted completed)
    {
        long at = Stopwatch.GetTimestamp();
        IReadOnlyList<InjectedFault>? log = null;
        ulong? verifiedBytes = null;
        Exception? failure = null;
        try
        {
            if (session is null)
            {
                failure = new InvalidOperationException("Completed was raised without its session as the sender.");
            }
            else
            {
                log = SessionProbe.GetInjectionLog(session);
                verifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            }
        }
        catch (InvalidOperationException exception)
        {
            // A production session, a non-native session or a concurrently disposed one: recorded and reported when
            // a test reads the snapshot, instead of turning the Completed delivery into a host-fault stop.
            failure = exception;
        }
        finally
        {
            lock (_sync)
            {
                _completed = completed;
                _completedAt = at;
                _injectionLog = log;
                _independentVerifiedBytes = verifiedBytes;
                _probeFailure = failure;
            }

            _completedSource.TrySetResult(completed);
        }
    }

    /// <summary>Records the Released receipt.</summary>
    /// <param name="released">The payload.</param>
    internal void OnReleased(EngineReleased released)
    {
        long at = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _released = released;
            _releasedAt = at;
        }

        _releasedSource.TrySetResult(released);
    }

    // Called under _sync.
    private InvalidOperationException NotCaptured(string what) => _completedAt is null
        ? new InvalidOperationException($"The {what} is snapshotted when Completed is received, which has not happened for this run.")
        : new InvalidOperationException($"The {what} could not be snapshotted when Completed was received.", _probeFailure);
}


/// <summary>
/// An <see cref="IEngine"/> decorator that records what every session it creates reports, for tests that drive the
/// production <c>RunController</c> graph and still need engine-side evidence: event timestamps, coordinator callback
/// counts and the fault layer's ground truth.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are returned unwrapped, so <see cref="SessionProbe"/> works on them, and the decorator subscribes its
/// handlers before returning a session, so they run ahead of the caller's.
/// </para>
/// <para>
/// Nothing here holds a strong reference to a session, its callback state, its options or its owner. Each run is an
/// <see cref="InstrumentedRun"/> holding weak references only; the session refers to its handlers, which capture only
/// the run; and the counting step gate and progress sink capture only the original delegate and the counters.
/// <c>SessionLifetimeTests</c> relies on this to prove that an abandoned controller graph is collected.
/// </para>
/// </remarks>
internal sealed class InstrumentedEngine : IEngine
{
    private readonly IEngine _inner;
    private readonly bool _countCallbacks;
    private readonly object _sync = new();
    private readonly List<InstrumentedRun> _runs = new();

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The engine that creates the sessions, normally <see cref="TestEngines.Create(FaultSpec?)"/>.</param>
    /// <param name="countCallbacks">
    /// Whether to wrap each session's step gate and progress sink with counting wrappers, reported through
    /// <see cref="InstrumentedRun.Counters"/>. A missing gate or sink is replaced by a counting one with the same effect:
    /// Continue, and nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public InstrumentedEngine(IEngine inner, bool countCallbacks = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _countCallbacks = countCallbacks;
    }

    /// <summary>
    /// Raised on the creating thread for every session, after the decorator has subscribed to it and before
    /// <see cref="CreateSession"/> returns it.
    /// </summary>
    public event Action<InstrumentedRun>? SessionCreated;

    /// <summary>Gets a snapshot of every run so far, in creation order.</summary>
    public IReadOnlyList<InstrumentedRun> Runs
    {
        get
        {
            lock (_sync)
            {
                return _runs.ToArray();
            }
        }
    }

    /// <summary>Gets the most recently created run, or <see langword="null"/> before the first session.</summary>
    public InstrumentedRun? LastRun
    {
        get
        {
            lock (_sync)
            {
                return _runs.Count == 0 ? null : _runs[^1];
            }
        }
    }

    /// <inheritdoc/>
    public SystemSnapshot QuerySystem() => _inner.QuerySystem();

    /// <inheritdoc/>
    public IEngineSession CreateSession(EngineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var counters = new CallbackCounters();
        IEngineSession session = _inner.CreateSession(_countCallbacks ? WithCountingHooks(options, counters) : options);

        // The fault-layer snapshot and the callback-state reference need the native session type; a run over another
        // engine records events only.
        object? callbackState = session is EngineSession ? SessionProbe.GetCallbackState(session) : null;
        var run = new InstrumentedRun(
            counters,
            new WeakReference<IEngineSession>(session),
            new WeakReference<object>(callbackState!));
        Subscribe(session, run);

        lock (_sync)
        {
            _runs.Add(run);
        }

        SessionCreated?.Invoke(run);
        return session;
    }

    // A static method of its own, so the handlers' closure holds the run alone: a closure shared with a lambda that
    // captured the session or the options would give the session's handlers a path to them.
    private static void Subscribe(IEngineSession session, InstrumentedRun run)
    {
        session.Prepared += (_, e) => run.OnPrepared(e);
        session.ProgressChanged += (_, e) => run.OnProgress(e);
        session.ErrorsDetected += (_, e) => run.OnErrors(e);

        // The sender is the session itself; taking it from the event keeps it out of the closure.
        session.Completed += (sender, e) => run.OnCompleted(sender as IEngineSession, e);
        session.Released += (_, e) => run.OnReleased(e);
    }

    // Every option is copied: EngineSessionOptions is a class with init-only properties, not a record, so there is no
    // with expression. A property added to it must be added here too.
    private static EngineSessionOptions WithCountingHooks(EngineSessionOptions options, CallbackCounters counters) => new()
    {
        RegionBytes = options.RegionBytes,
        MinRegionBytes = options.MinRegionBytes,
        Seed = options.Seed,
        WorkerCount = options.WorkerCount,
        StopOnError = options.StopOnError,
        ErrorDetailCap = options.ErrorDetailCap,
        ProgressInterval = options.ProgressInterval,
        IsaOverride = options.IsaOverride,
        Steps = options.Steps,
        StepGate = CountingGate(options.StepGate, counters),
        ProgressSink = CountingSink(options.ProgressSink, counters),
        CallbackFault = options.CallbackFault,
    };

    private static StepGate CountingGate(StepGate? original, CallbackCounters counters) => (step, stopEvent) =>
    {
        counters.OnGate();
        return original is null ? StepGateDecision.Continue : original(step, stopEvent);
    };

    private static ProgressSink CountingSink(ProgressSink? original, CallbackCounters counters) => progress =>
    {
        counters.OnProgressSink();
        original?.Invoke(progress);
    };
}

/// <summary>The seven events of an <see cref="IEngineSession"/>, as recorded by <see cref="EngineRunRecorder"/>.</summary>
internal enum EngineEventKind
{
    /// <summary><see cref="IEngineSession.Prepared"/>.</summary>
    Prepared,

    /// <summary><see cref="IEngineSession.StepStarted"/>.</summary>
    StepStarted,

    /// <summary><see cref="IEngineSession.StepFinished"/>.</summary>
    StepFinished,

    /// <summary><see cref="IEngineSession.ProgressChanged"/>.</summary>
    ProgressChanged,

    /// <summary><see cref="IEngineSession.ErrorsDetected"/>.</summary>
    ErrorsDetected,

    /// <summary><see cref="IEngineSession.Completed"/>.</summary>
    Completed,

    /// <summary><see cref="IEngineSession.Released"/>.</summary>
    Released,
}

/// <summary>One event delivery recorded by <see cref="EngineRunRecorder"/>.</summary>
/// <param name="Kind">Which event was raised.</param>
/// <param name="Payload">The event record.</param>
/// <param name="Timestamp">The <see cref="Stopwatch"/> timestamp of receipt.</param>
/// <param name="ThreadId">The managed thread id the handler ran on.</param>
/// <param name="Context">The <see cref="SynchronizationContext.Current"/> the handler ran under.</param>
internal sealed record RecordedEngineEvent(
    EngineEventKind Kind,
    object Payload,
    long Timestamp,
    int ThreadId,
    SynchronizationContext? Context);


/// <summary>
/// Records every event of one session created directly through <see cref="IEngine.CreateSession"/>: the payload, the
/// <see cref="Stopwatch"/> timestamp of receipt, the managed thread and the synchronization context it ran on.
/// </summary>
/// <remarks>
/// Attach before <see cref="IEngineSession.Start"/>. The recorder holds no reference to the session; the session holds
/// the recorder through its handlers. Lists are snapshots in delivery order on the session's captured context, and
/// every member is thread-safe.
/// </remarks>
internal sealed class EngineRunRecorder
{
    private readonly object _sync = new();
    private readonly List<RecordedEngineEvent> _events = new();
    private readonly List<EngineStepStarted> _stepStarted = new();
    private readonly List<EngineStepFinished> _stepFinished = new();
    private readonly List<Timestamped<RunProgress>> _progress = new();
    private readonly List<EngineErrorsDetected> _errorBatches = new();
    private readonly List<ErrorRecord> _errors = new();
    private readonly TaskCompletionSource<EngineCompleted> _completedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<EngineReleased> _releasedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EnginePrepared? _prepared;
    private EngineCompleted? _completed;
    private EngineReleased? _released;

    private EngineRunRecorder()
    {
    }

    /// <summary>Gets the Prepared payload, or <see langword="null"/> before it arrived.</summary>
    public EnginePrepared? Prepared
    {
        get
        {
            lock (_sync)
            {
                return _prepared;
            }
        }
    }

    /// <summary>Gets every StepStarted payload so far.</summary>
    public IReadOnlyList<EngineStepStarted> StepStarted
    {
        get
        {
            lock (_sync)
            {
                return _stepStarted.ToArray();
            }
        }
    }

    /// <summary>Gets every StepFinished payload so far.</summary>
    public IReadOnlyList<EngineStepFinished> StepFinished
    {
        get
        {
            lock (_sync)
            {
                return _stepFinished.ToArray();
            }
        }
    }

    /// <summary>Gets every ProgressChanged payload so far.</summary>
    public IReadOnlyList<RunProgress> Progress
    {
        get
        {
            lock (_sync)
            {
                return _progress.Select(entry => entry.Value).ToArray();
            }
        }
    }

    /// <summary>Gets every ProgressChanged payload so far with its receipt timestamp.</summary>
    public IReadOnlyList<Timestamped<RunProgress>> TimedProgress
    {
        get
        {
            lock (_sync)
            {
                return _progress.ToArray();
            }
        }
    }

    /// <summary>Gets every detailed error record so far: the batches' records concatenated in delivery order.</summary>
    public IReadOnlyList<ErrorRecord> Errors
    {
        get
        {
            lock (_sync)
            {
                return _errors.ToArray();
            }
        }
    }

    /// <summary>Gets every ErrorsDetected payload so far, including the record-less batches past the detail cap.</summary>
    public IReadOnlyList<EngineErrorsDetected> ErrorBatches
    {
        get
        {
            lock (_sync)
            {
                return _errorBatches.ToArray();
            }
        }
    }

    /// <summary>Gets the Completed payload, or <see langword="null"/> before it arrived.</summary>
    public EngineCompleted? Completed
    {
        get
        {
            lock (_sync)
            {
                return _completed;
            }
        }
    }

    /// <summary>Gets the Released payload, or <see langword="null"/> before it arrived.</summary>
    public EngineReleased? Released
    {
        get
        {
            lock (_sync)
            {
                return _released;
            }
        }
    }

    /// <summary>Gets every event delivery so far, in delivery order.</summary>
    public IReadOnlyList<RecordedEngineEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>Gets the kind of every event delivery so far, in delivery order.</summary>
    public IReadOnlyList<EngineEventKind> EventKinds
    {
        get
        {
            lock (_sync)
            {
                return _events.Select(entry => entry.Kind).ToArray();
            }
        }
    }

    /// <summary>Gets the managed thread id of every event delivery so far, in delivery order.</summary>
    public IReadOnlyList<int> EventThreadIds
    {
        get
        {
            lock (_sync)
            {
                return _events.Select(entry => entry.ThreadId).ToArray();
            }
        }
    }

    /// <summary>Subscribes a new recorder to all seven events of <paramref name="session"/>.</summary>
    /// <param name="session">The session, not yet started.</param>
    /// <returns>The recorder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public static EngineRunRecorder Attach(IEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var recorder = new EngineRunRecorder();
        session.Prepared += recorder.OnPrepared;
        session.StepStarted += recorder.OnStepStarted;
        session.StepFinished += recorder.OnStepFinished;
        session.ProgressChanged += recorder.OnProgressChanged;
        session.ErrorsDetected += recorder.OnErrorsDetected;
        session.Completed += recorder.OnCompleted;
        session.Released += recorder.OnReleased;
        return recorder;
    }

    /// <summary>Returns the first delivery of <paramref name="kind"/>, or <see langword="null"/>.</summary>
    /// <param name="kind">The event kind.</param>
    /// <returns>The delivery, or <see langword="null"/> when none has been recorded.</returns>
    public RecordedEngineEvent? FirstEvent(EngineEventKind kind)
    {
        lock (_sync)
        {
            return _events.Find(entry => entry.Kind == kind);
        }
    }

    /// <summary>Waits for Completed.</summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The Completed payload.</returns>
    /// <exception cref="TimeoutException">Completed did not arrive in time; the message carries the last progress.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<EngineCompleted> WaitCompletedAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForAsync(_completedSource.Task, nameof(EngineEventKind.Completed), timeout, cancellationToken);

    /// <summary>Waits for Released.</summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The Released payload.</returns>
    /// <exception cref="TimeoutException">Released did not arrive in time; the message carries the last progress.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<EngineReleased> WaitReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForAsync(_releasedSource.Task, nameof(EngineEventKind.Released), timeout, cancellationToken);

    private async Task<T> WaitForAsync<T>(Task<T> task, string eventName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"Timed out after {timeout.TotalSeconds:0.###} s waiting for {eventName}; {Describe()}."),
                exception);
        }
    }

    private string Describe()
    {
        lock (_sync)
        {
            string counts = string.Join(
                ", ",
                _events.GroupBy(entry => entry.Kind).OrderBy(group => group.Key).Select(group => $"{group.Key} Ã—{group.Count()}"));
            string events = counts.Length == 0 ? "no events yet" : "events " + counts;
            if (_progress.Count == 0)
            {
                return events + ", no progress yet";
            }

            RunProgress last = _progress[^1].Value;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{events}; last progress: step {last.StepIndex + 1} of {last.StepCount} '{last.StepName}', "
                + $"pass {last.PassIndex + 1} of {last.PassesPlanned}, elapsed {last.Elapsed.TotalSeconds:0.0} s, "
                + $"coverage {last.CoveragePercent:0.0} %, errors {last.TotalErrors}");
        }
    }

    private void OnPrepared(object? sender, EnginePrepared e)
    {
        lock (_sync)
        {
            _prepared = e;
            Record(EngineEventKind.Prepared, e);
        }
    }

    private void OnStepStarted(object? sender, EngineStepStarted e)
    {
        lock (_sync)
        {
            _stepStarted.Add(e);
            Record(EngineEventKind.StepStarted, e);
        }
    }

    private void OnStepFinished(object? sender, EngineStepFinished e)
    {
        lock (_sync)
        {
            _stepFinished.Add(e);
            Record(EngineEventKind.StepFinished, e);
        }
    }

    private void OnProgressChanged(object? sender, RunProgress e)
    {
        lock (_sync)
        {
            RecordedEngineEvent recorded = Record(EngineEventKind.ProgressChanged, e);
            _progress.Add(new Timestamped<RunProgress>(recorded.Timestamp, e));
        }
    }

    private void OnErrorsDetected(object? sender, EngineErrorsDetected e)
    {
        lock (_sync)
        {
            _errorBatches.Add(e);
            _errors.AddRange(e.Records);
            Record(EngineEventKind.ErrorsDetected, e);
        }
    }

    private void OnCompleted(object? sender, EngineCompleted e)
    {
        lock (_sync)
        {
            _completed = e;
            Record(EngineEventKind.Completed, e);
        }

        _completedSource.TrySetResult(e);
    }

    private void OnReleased(object? sender, EngineReleased e)
    {
        lock (_sync)
        {
            _released = e;
            Record(EngineEventKind.Released, e);
        }

        _releasedSource.TrySetResult(e);
    }

    // Called under _sync, on the thread that delivers the event, so the thread and context are the handler's own.
    private RecordedEngineEvent Record(EngineEventKind kind, object payload)
    {
        var recorded = new RecordedEngineEvent(
            kind,
            payload,
            Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId,
            SynchronizationContext.Current);
        _events.Add(recorded);
        return recorded;
    }
}

/// <summary>
/// Sizes regions for integration runs: small enough to lock without elevation, large enough that every worker gets a
/// whole slice.
/// </summary>
internal static class TestRegions
{
    /// <summary>A 256 MiB request, for <see cref="Bytes"/>.</summary>
    public const ulong Mib256 = 256;

    /// <summary>A 512 MiB request, for <see cref="Bytes"/>.</summary>
    public const ulong Mib512 = 512;

    /// <summary>A 1 GiB request, for <see cref="Bytes"/>.</summary>
    public const ulong Gib1 = 1024;

    private const ulong BytesPerMib = 1UL << 20;

    // Each worker's slice spans at least this many 2 MiB granules, however small the request.
    private const ulong MinimumGranulesPerWorker = 4;

    /// <summary>
    /// Returns <c>max(<paramref name="requestedMiB"/> MiB, workers Ã— 2 MiB Ã— 4)</c> rounded down to a multiple of
    /// <c>workers Ã— 2 MiB</c>, with one worker per logical processor. Pass the result as both
    /// <see cref="EngineSessionOptions.RegionBytes"/> and <see cref="EngineSessionOptions.MinRegionBytes"/>, so the
    /// engine's <c>region_bytes â‰¥ min_region_bytes</c> check passes and its slice rounding keeps the size unchanged.
    /// </summary>
    /// <param name="system">The machine, for its logical processor count.</param>
    /// <param name="requestedMiB">The requested size in MiB, at least 1; see <see cref="Mib256"/>, <see cref="Mib512"/>, <see cref="Gib1"/>.</param>
    /// <returns>The region size in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="requestedMiB"/> is 0, or the system reports no logical processor.
    /// </exception>
    public static ulong Bytes(SystemSnapshot system, ulong requestedMiB)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentOutOfRangeException.ThrowIfZero(requestedMiB);

        int workers = system.LogicalProcessors;
        ulong requested = checked(requestedMiB * BytesPerMib);
        ulong floor = checked((ulong)workers * MemorySizing.SliceGranularityBytes * MinimumGranulesPerWorker);
        return MemorySizing.RoundDownToSlices(Math.Max(requested, floor), workers);
    }
}

/// <summary>
/// Shortened plans for integration runs, all derived from <see cref="TestPlans"/> with <see langword="with"/>
/// expressions, so they keep the shipped steps' names, patterns, orders, thread modes and mechanisms.
/// </summary>
/// <remarks>
/// Each property builds a new plan on every access: plans are cheap, immutable data, and a validation failure then
/// surfaces at the call site rather than as a type-initialisation failure.
/// </remarks>
internal static class ScaledPlans
{
    private const long BudgetDivisor = 10;

    // The validator's hold rule, budget â‰¥ 2.5 Ã— hold, as hold â‰¤ budget Ã— 2 / 5 in ticks.
    private const long HoldRatioNumerator = 2;
    private const long HoldRatioDenominator = 5;

    private static readonly TimeSpan _faultInjectionHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _oneMillisecond = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Gets Standard's twelve steps with every time budget divided by 10 (120 s in all) and every hold set to 5 s, the
    /// fault-injection correctness plan.
    /// </summary>
    /// <remarks>
    /// The 5 s holds keep every hold step's data older than the delay-dependent fault's 2000 ms threshold. The plan is
    /// deliberately not validated: Q5's 5 s budget with its 5 s hold is below the validator's 2.5 Ã— hold floor. The
    /// engine accepts that step and runs it as one oversized pass, recording the overrun, which the correctness tests
    /// do not gate.
    /// </remarks>
    public static TestPlan FaultInjectionStandard => new(
        TestPlans.Standard.Type,
        TestPlans.Standard.Steps.Select(step => step with
        {
            TimeBudget = ScaleDown(step.TimeBudget),
            Hold = step.HasHold ? _faultInjectionHold : TimeSpan.Zero,
        }));

    /// <summary>
    /// Gets Quick with every time budget and every hold divided by 10 (30 s in all), so each hold step keeps its shipped
    /// budget-to-hold ratio: Q5 holds 0.5 s on its 5 s budget, 10 to 1 as shipped. Each hold is capped at budget / 2.5.
    /// Validated.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scaled plan breaks a plan rule.</exception>
    public static TestPlan TimingQuick => Validated(Timing(TestPlans.Quick), nameof(TimingQuick));

    /// <summary>
    /// Gets Standard with every time budget and every hold divided by 10 (120 s in all), so each hold step keeps its
    /// shipped budget-to-hold ratio: Q5 holds 0.5 s on its 5 s budget and S10 3 s on its 15 s, 10 to 1 and 5 to 1 as
    /// shipped. Each hold is capped at budget / 2.5. Validated.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scaled plan breaks a plan rule.</exception>
    public static TestPlan TimingStandard => Validated(Timing(TestPlans.Standard), nameof(TimingStandard));

    /// <summary>
    /// Builds a Quick-type plan from chosen Quick steps, each with its own time budget; a hold step keeps its hold,
    /// shortened where needed to at most budget Ã· 2.5. Validated, so a Quick step may appear only once.
    /// </summary>
    /// <param name="steps">The steps in run order: each a zero-based index into Quick's six steps and a time budget.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="steps"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index is outside Quick's steps, a budget is not positive, or a hold step's budget is too short to hold for
    /// 1 ms.
    /// </exception>
    /// <exception cref="InvalidOperationException">The plan breaks a plan rule, such as a repeated step name.</exception>
    public static TestPlan Custom(params (int quickStepIndex, TimeSpan budget)[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0)
        {
            throw new ArgumentException("A custom plan needs at least one step.", nameof(steps));
        }

        IReadOnlyList<StepDefinition> quick = TestPlans.Quick.Steps;
        var definitions = new StepDefinition[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            (int quickStepIndex, TimeSpan budget) = steps[i];
            if (quickStepIndex < 0 || quickStepIndex >= quick.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(steps),
                    quickStepIndex,
                    string.Create(CultureInfo.InvariantCulture, $"steps[{i}] names Quick step {quickStepIndex}; Quick has steps 0 to {quick.Count - 1}."));
            }

            if (budget <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(steps),
                    budget,
                    string.Create(CultureInfo.InvariantCulture, $"steps[{i}] has a time budget of {budget}; it must be positive."));
            }

            StepDefinition source = quick[quickStepIndex];
            definitions[i] = source with { TimeBudget = budget, Hold = FitHold(source.Hold, budget) };
        }

        return Validated(new TestPlan(TestPlans.Quick.Type, definitions), nameof(Custom));
    }

    /// <summary>Maps a plan's steps to the attributes <see cref="EngineSessionOptions.Steps"/> takes.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>One entry per step, indexed by position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public static IReadOnlyList<StepAttributes> Attributes(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ToStepAttributes();
    }

    // Holds are divided by 10 like the budgets, so every hold step keeps its shipped budget-to-hold ratio and pass count
    // and ends as near its budget as at full scale. A full-length hold on a tenth of the budget leaves the step a few
    // long passes that end most of a pass early, and every time-remaining estimate made before the step runs high by
    // that shortfall. A hold step keeps at least 1 ms, so it stays a hold step, and FitHold caps it at budget / 2.5.
    private static TestPlan Timing(TestPlan source) => new(
        source.Type,
        source.Steps.Select(step =>
        {
            TimeSpan budget = ScaleDown(step.TimeBudget);
            TimeSpan hold = ScaleDown(step.Hold);
            if (step.HasHold && hold < _oneMillisecond)
            {
                hold = _oneMillisecond;
            }

            return step with { TimeBudget = budget, Hold = FitHold(hold, budget) };
        }));

    // Budget Ã· 10 in whole milliseconds, the engine's resolution; the shipped budgets are whole seconds, so it is exact.
    private static TimeSpan ScaleDown(TimeSpan budget) => WholeMilliseconds(budget.Ticks / BudgetDivisor);

    // Integer ticks, floored, so the validator's exact check 2 Ã— budget â‰¥ 5 Ã— hold always holds for the result.
    private static TimeSpan FitHold(TimeSpan hold, TimeSpan budget)
    {
        if (hold <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        TimeSpan longest = WholeMilliseconds(budget.Ticks * HoldRatioNumerator / HoldRatioDenominator);
        if (longest < _oneMillisecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget),
                budget,
                "A hold step's time budget must allow a hold of at least 1 ms (budget â‰¥ 2.5 ms).");
        }

        return hold <= longest ? hold : longest;
    }

    private static TimeSpan WholeMilliseconds(long ticks) =>
        TimeSpan.FromTicks(ticks - (ticks % TimeSpan.TicksPerMillisecond));

    private static TestPlan Validated(TestPlan plan, string name)
    {
        IReadOnlyList<PlanViolation> violations = TestPlanValidator.Validate(plan);
        if (violations.Count != 0)
        {
            throw new InvalidOperationException(
                $"The scaled plan {name} breaks the plan rules: {string.Join("; ", violations.Select(violation => violation.Message))}");
        }

        return plan;
    }
}

