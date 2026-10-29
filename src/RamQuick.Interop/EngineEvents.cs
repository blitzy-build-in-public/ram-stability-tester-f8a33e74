using System.Diagnostics;
using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// The engine prepared the run: the region under test is allocated and locked, and one worker is pinned to each
/// logical processor. Copied from <c>rq_prepared</c> and raised as <c>Prepared</c> on the
/// <see cref="SynchronizationContext"/> captured when the session was created.
/// </summary>
/// <remarks>
/// Raised at most once, at the origin of test time, before the first step starts. A preparation failure raises no
/// Prepared: the run goes straight to Completed with its end reason.
/// </remarks>
/// <param name="TestedBytes">
/// The bytes under test, in bytes: each worker's slice times the worker count, after 2 MiB rounding and any
/// shrink-and-retry of locking. Shown as "Tested capacity" and the denominator of every coverage figure.
/// </param>
/// <param name="WorkerCount">The number of workers created and pinned, one per logical processor.</param>
/// <param name="PageMode">The kind of pages backing the locked region, written as the summary's <c>memory.pageMode</c>.</param>
/// <param name="InstructionSet">The instruction set of the memory kernels, fixed for the session.</param>
/// <param name="AddressKind">The kind of address every error record of the session carries; virtual in v1.</param>
/// <param name="PrepareTime">The time from Start to the end of preparation.</param>
public sealed record EnginePrepared(
    ulong TestedBytes,
    int WorkerCount,
    PageMode PageMode,
    InstructionSet InstructionSet,
    AddressKind AddressKind,
    TimeSpan PrepareTime)
{
    /// <summary>
    /// Gets a value indicating whether the engine fell back to the SSE2 kernels because AVX2 is unavailable, in which
    /// case the notice line reads "Reduced instruction set (SSE2): this test runs slower".
    /// </summary>
    public bool IsReducedInstructionSet => InstructionSet == InstructionSet.Sse2;
}

/// <summary>
/// A step is about to start, copied from <c>rq_step_start</c>.
/// </summary>
/// <remarks>
/// The same record is first passed synchronously to <c>EngineSessionOptions.StepGate</c> on the engine's coordinator
/// thread, before any memory traffic of the step, and then, only when the gate lets the step run, raised as
/// <c>StepStarted</c> on the <see cref="SynchronizationContext"/> captured when the session was created.
/// </remarks>
/// <param name="Step">The attributes of the starting step, from the session's copy of the plan's steps.</param>
/// <param name="Index">The zero-based index of the step in the plan, equal to <see cref="StepAttributes.Index"/>. Display adds 1.</param>
/// <param name="Count">The number of steps in the plan.</param>
public sealed record EngineStepStarted(StepAttributes Step, int Index, int Count)
{
    /// <summary>
    /// Gets the step's name, exactly as the plan defines it and the UI shows it; the same text the engine reports in
    /// <c>rq_step_start.name</c>.
    /// </summary>
    public string Name => Step.Name;
}

/// <summary>
/// A started step has finished, copied from <c>rq_step_result</c> and raised as <c>StepFinished</c> on the
/// <see cref="SynchronizationContext"/> captured when the session was created.
/// </summary>
/// <remarks>
/// Raised once for every step whose gate let it run, including the step a stop interrupted, whose
/// <see cref="StepOutcome.Completed"/> is <see langword="false"/>.
/// </remarks>
/// <param name="Outcome">
/// The step's attributes with what the engine measured: actual duration, overrun, passes completed, verified bytes,
/// the true error count and whether the step ran to the end of its schedule.
/// </param>
public sealed record EngineStepFinished(StepOutcome Outcome);

/// <summary>
/// A batch of confirmed errors, copied from one <c>on_errors</c> call and raised as <c>ErrorsDetected</c> on the
/// <see cref="SynchronizationContext"/> captured when the session was created, at most ten times a second.
/// </summary>
/// <param name="Records">
/// The detailed records confirmed since the previous batch, in confirmation order. Empty once the run's detail cap
/// (10,000 records) is reached, while <paramref name="TotalErrors"/> keeps rising.
/// </param>
/// <param name="TotalErrors">The run's exact true error total so far, from engine counters; never capped.</param>
public sealed record EngineErrorsDetected(IReadOnlyList<ErrorRecord> Records, ulong TotalErrors);

/// <summary>
/// The run has ended and every worker has joined, copied from <c>rq_run_result</c> and raised as <c>Completed</c>
/// on the <see cref="SynchronizationContext"/> captured when the session was created.
/// </summary>
/// <remarks>
/// Raised exactly once per started session. The locked memory is released afterwards, and <c>Released</c> follows.
/// </remarks>
/// <param name="EndReason">
/// Why the run ended: the engine's first recorded end reason, which alone decides the terminal state. Native
/// <c>internal_fault</c>, and any end-reason code this wrapper does not know, map to
/// <see cref="RunEndReason.EngineFault"/>.
/// </param>
/// <param name="TotalErrors">The run's exact true error total, from engine counters; never capped.</param>
/// <param name="VerifiedBytes">The bytes verified in the run, in bytes; the numerator of the run's coverage.</param>
/// <param name="Elapsed">The test time at completion, measured from the end of preparation; zero when preparation failed.</param>
/// <param name="FirstErrorTime">
/// The test time of the first confirmed error, or <see langword="null"/> when the run found no error.
/// </param>
/// <param name="StepsCompleted">The number of steps that ran to the end of their schedule.</param>
/// <param name="StepResults">
/// One outcome per step whose gate let it run, in step order; empty when no step started.
/// </param>
/// <param name="OsError">
/// The Windows error code (<c>GetLastError</c>) of the failing call for affinity, memory and thread-creation faults,
/// otherwise 0.
/// </param>
/// <param name="NativeEndDetail">
/// The engine's own short English diagnostic, or <see langword="null"/> when it reported none, as for
/// <see cref="RunEndReason.Completed"/>.
/// </param>
/// <param name="EndDetail">
/// The detail text <c>RunStateMachine.EndDetail</c> receives, or <see langword="null"/> when there is none, as for
/// <see cref="RunEndReason.Completed"/>. For <see cref="RunEndReason.AffinityFailed"/>, and for
/// <see cref="RunEndReason.InsufficientMemory"/> and <see cref="RunEndReason.EngineFault"/> whenever
/// <paramref name="OsError"/> is nonzero, it reads "Windows error n: " followed by
/// <paramref name="NativeEndDetail"/>, where n is <paramref name="OsError"/>. Otherwise it is
/// <paramref name="NativeEndDetail"/>.
/// </param>
public sealed record EngineCompleted(
    RunEndReason EndReason,
    ulong TotalErrors,
    ulong VerifiedBytes,
    TimeSpan Elapsed,
    TimeSpan? FirstErrorTime,
    int StepsCompleted,
    IReadOnlyList<StepOutcome> StepResults,
    uint OsError,
    string? NativeEndDetail,
    string? EndDetail);

/// <summary>
/// The engine has released the locked region, copied from <c>on_released</c> and raised as <c>Released</c> on the
/// <see cref="SynchronizationContext"/> captured when the session was created.
/// </summary>
/// <remarks>
/// Raised exactly once, after <c>Completed</c>. The run's summaries are published only after it, so that they carry
/// the release time.
/// </remarks>
/// <param name="ReleasedBytes">The bytes unlocked and freed, in bytes.</param>
/// <param name="ReleaseTime">The time the release took, written as the summary's <c>memory.releaseSeconds</c>.</param>
public sealed record EngineReleased(ulong ReleasedBytes, TimeSpan ReleaseTime);

/// <summary>
/// Copies native engine payloads into the managed event records and <c>RamQuick.Core</c> types.
/// </summary>
/// <remarks>
/// <para>
/// Every method is pure and runs on the engine's coordinator thread inside a callback thunk. Payload pointers, and
/// the arrays they reference, are engine-owned and valid only during the callback, so each method copies everything
/// it needs and retains no pointer.
/// </para>
/// <para>
/// <c>steps</c> is the session's immutable copy of the plan's steps, the same list the engine was configured with.
/// Native payloads carry step indices only, so step attributes and names are taken from it rather than re-decoded.
/// </para>
/// <para>
/// A payload that cannot belong to the configured session, such as an undefined enumeration value, a step index out
/// of range or a count beyond <see cref="int"/>, means the engine and this wrapper disagree on the ABI. Such a
/// payload throws <see cref="InvalidOperationException"/>, which the thunk reports as a host fault, so the run ends
/// ABORTED instead of continuing on misread data.
/// </para>
/// </remarks>
internal static unsafe class EngineEventMapper
{
    /// <summary>Maps an <c>on_prepared</c> payload.</summary>
    /// <param name="p">The engine's payload.</param>
    /// <returns>The managed copy.</returns>
    /// <exception cref="InvalidOperationException">A field holds a value the ABI does not define.</exception>
    internal static EnginePrepared ToPrepared(in RqPrepared p)
    {
        return new EnginePrepared(
            TestedBytes: p.TestedBytes,
            WorkerCount: ToInt32(p.WorkerCount, "rq_prepared.worker_count"),
            PageMode: Defined(unchecked((PageMode)p.PageMode), p.PageMode, "rq_prepared.page_mode"),
            InstructionSet: Defined(unchecked((InstructionSet)p.IsaSelected), p.IsaSelected, "rq_prepared.isa_selected"),
            AddressKind: Defined(unchecked((AddressKind)p.AddressKind), p.AddressKind, "rq_prepared.address_kind"),
            PrepareTime: FromMilliseconds(p.PrepareMs));
    }

    /// <summary>Maps an <c>on_step_starting</c> payload.</summary>
    /// <param name="s">The engine's payload.</param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>The managed copy, whose name and attributes come from <paramref name="steps"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The step count differs from <paramref name="steps"/>, or the index is out of range.
    /// </exception>
    internal static EngineStepStarted ToStepStarted(in RqStepStart s, IReadOnlyList<StepAttributes> steps)
    {
        EnsureStepCount(s.Count, steps, "rq_step_start.count");
        StepAttributes step = StepAt(steps, s.Index, "rq_step_start.index");
        return new EngineStepStarted(Step: step, Index: (int)s.Index, Count: steps.Count);
    }

    /// <summary>Maps one <c>rq_step_result</c>, the <c>on_step_finished</c> payload or an element of the run result.</summary>
    /// <param name="r">The engine's step result.</param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>The step's outcome, with its attributes from <paramref name="steps"/>.</returns>
    /// <exception cref="InvalidOperationException">The index is out of range or the pass count exceeds <see cref="int"/>.</exception>
    internal static StepOutcome ToOutcome(in RqStepResult r, IReadOnlyList<StepAttributes> steps)
    {
        return new StepOutcome
        {
            Attributes = StepAt(steps, r.Index, "rq_step_result.index"),
            ActualDuration = FromMilliseconds(r.ActualMs),
            PassesCompleted = ToInt32(r.Passes, "rq_step_result.passes"),
            VerifiedBytes = r.VerifiedBytes,
            Errors = r.Errors,
            Completed = r.Completed != 0,
            Overrun = FromMilliseconds(r.OverrunMs),
        };
    }

    /// <summary>Maps an <c>on_progress</c> payload.</summary>
    /// <param name="p">The engine's payload.</param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>
    /// The progress snapshot. <see cref="RunProgress.Remaining"/> is <see langword="null"/> ("calculating…") when
    /// <c>remaining_ms</c> is negative, and <see cref="RunProgress.CoveragePercent"/> is 100 × verified ÷ region bytes.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The step count differs from <paramref name="steps"/>, the step index is out of range, or a pass figure exceeds
    /// <see cref="int"/>.
    /// </exception>
    internal static RunProgress ToProgress(in RqProgress p, IReadOnlyList<StepAttributes> steps)
    {
        EnsureStepCount(p.StepCount, steps, "rq_progress.step_count");
        StepAttributes step = StepAt(steps, p.StepIndex, "rq_progress.step_index");
        return new RunProgress
        {
            StepIndex = (int)p.StepIndex,
            StepCount = steps.Count,
            StepName = step.Name,
            PassIndex = ToInt32(p.PassIndex, "rq_progress.pass_index"),
            PassesPlanned = ToInt32(p.PassesPlanned, "rq_progress.passes_planned"),
            Elapsed = FromMilliseconds(p.ElapsedMs),
            Remaining = p.RemainingMs < 0 ? null : FromMilliseconds((ulong)p.RemainingMs),
            VerifiedBytes = p.VerifiedBytes,
            RegionBytes = p.RegionBytes,
            CoveragePercent = RunProgress.CoveragePercentOf(p.VerifiedBytes, p.RegionBytes),
            TotalErrors = p.TotalErrors,
        };
    }

    /// <summary>Maps one <c>rq_error_record</c> of an <c>on_errors</c> batch.</summary>
    /// <param name="e">The engine's record.</param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>The managed record, its step name taken from <paramref name="steps"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// An enumeration field holds an undefined value, the step index is out of range, or a field exceeds
    /// <see cref="int"/>.
    /// </exception>
    internal static ErrorRecord ToErrorRecord(in RqErrorRecord e, IReadOnlyList<StepAttributes> steps)
    {
        // ErrorRecord derives its bit mask from Expected and Actual, so the native bit_mask is not copied; the engine
        // computes it the same way.
        Debug.Assert(e.BitMask == (e.Expected ^ e.Actual), "rq_error_record.bit_mask must equal expected XOR actual.");

        StepAttributes step = StepAt(steps, e.StepIndex, "rq_error_record.step_index");
        return new ErrorRecord
        {
            Sequence = e.Sequence,
            Address = e.Address,
            AddressKind = Defined(unchecked((AddressKind)e.AddressKind), e.AddressKind, "rq_error_record.address_kind"),
            RegionOffset = e.RegionOffset,
            Expected = e.Expected,
            Actual = e.Actual,
            Reread = e.Reread,
            RereadOutcome = Defined(
                unchecked((RereadOutcome)e.RereadOutcome),
                e.RereadOutcome,
                "rq_error_record.reread_outcome"),
            StepIndex = (int)e.StepIndex,
            StepName = step.Name,
            PassIndex = ToInt32(e.PassIndex, "rq_error_record.pass_index"),
            AccessType = Defined(unchecked((AccessType)e.AccessType), e.AccessType, "rq_error_record.access_type"),
            TimeSinceWrite = FromMicroseconds(e.TimeSinceWriteUs),
            TimeIntoTest = FromMilliseconds(e.TimeIntoTestMs),
            WorkerIndex = ToInt32(e.WorkerIndex, "rq_error_record.worker_index"),
            LogicalProcessor = ToInt32(e.LogicalProcessor, "rq_error_record.logical_processor"),
            ProcessorGroup = ToInt32(e.ProcessorGroup, "rq_error_record.processor_group"),
            ActiveWorkers = ToInt32(e.ActiveWorkers, "rq_error_record.active_workers"),
        };
    }

    /// <summary>Maps the records of one <c>on_errors</c> batch.</summary>
    /// <param name="records">
    /// The batch; empty when the engine passed a count of 0, as it does with a null pointer once the detail cap is reached.
    /// </param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>A new array of managed records in batch order, or an empty array.</returns>
    /// <exception cref="InvalidOperationException">A record cannot be mapped; see <see cref="ToErrorRecord"/>.</exception>
    internal static IReadOnlyList<ErrorRecord> ToErrorRecords(
        ReadOnlySpan<RqErrorRecord> records,
        IReadOnlyList<StepAttributes> steps)
    {
        if (records.IsEmpty)
        {
            return Array.Empty<ErrorRecord>();
        }

        var mapped = new ErrorRecord[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            mapped[i] = ToErrorRecord(in records[i], steps);
        }

        return mapped;
    }

    /// <summary>
    /// Maps an <c>on_completed</c> payload, or the header <c>rq_session_get_result</c> copied, including its step
    /// results.
    /// </summary>
    /// <param name="r">The engine's run result; its <c>step_results</c> array must still be valid.</param>
    /// <param name="steps">The session's copy of the plan's steps.</param>
    /// <returns>The managed copy; see <see cref="EngineCompleted"/> for the null conventions.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>step_results</c> is null with a nonzero count, a count exceeds <see cref="int"/>, or a step result cannot
    /// be mapped.
    /// </exception>
    internal static EngineCompleted ToCompleted(in RqRunResult r, IReadOnlyList<StepAttributes> steps)
    {
        int stepResultCount = ToInt32(r.StepResultCount, "rq_run_result.step_result_count");
        if (stepResultCount > 0 && r.StepResults == null)
        {
            throw AbiMismatch("rq_run_result.step_results is null with a nonzero step_result_count.");
        }

        StepOutcome[] stepResults = stepResultCount == 0 ? Array.Empty<StepOutcome>() : new StepOutcome[stepResultCount];
        for (int i = 0; i < stepResultCount; i++)
        {
            stepResults[i] = ToOutcome(in r.StepResults[i], steps);
        }

        string? nativeDetail = ReadEndDetail(in r);
        RunEndReason endReason = MapEndReason(r.EndReason);

        return new EngineCompleted(
            EndReason: endReason,
            TotalErrors: r.TotalErrors,
            VerifiedBytes: r.VerifiedBytes,
            Elapsed: FromMilliseconds(r.ElapsedMs),
            FirstErrorTime: r.FirstErrorMs == NativeConstants.NoFirstError ? null : FromMilliseconds(r.FirstErrorMs),
            StepsCompleted: ToInt32(r.StepsCompleted, "rq_run_result.steps_completed"),
            StepResults: stepResults,
            OsError: r.OsError,
            NativeEndDetail: nativeDetail,
            EndDetail: FormatEndDetail(endReason, r.OsError, nativeDetail));
    }

    /// <summary>Maps an <c>on_released</c> payload.</summary>
    /// <param name="releasedBytes">The engine's <c>released_bytes</c>.</param>
    /// <param name="releaseMs">The engine's <c>release_ms</c>.</param>
    /// <returns>The managed copy.</returns>
    internal static EngineReleased ToReleased(ulong releasedBytes, ulong releaseMs)
    {
        return new EngineReleased(releasedBytes, FromMilliseconds(releaseMs));
    }

    /// <summary>Maps a native <c>end_reason</c> code to <see cref="RunEndReason"/>.</summary>
    /// <param name="endReason">The native <c>RQ_END_*</c> value.</param>
    /// <returns>
    /// The managed reason. The two numberings differ, so the mapping is an explicit switch and never a cast. An
    /// unknown code is an engine fault.
    /// </returns>
    internal static RunEndReason MapEndReason(uint endReason) => endReason switch
    {
        NativeConstants.EndCompleted => RunEndReason.Completed,
        NativeConstants.EndFirstError => RunEndReason.FirstError,
        NativeConstants.EndUserStop => RunEndReason.UserStop,
        NativeConstants.EndThermal => RunEndReason.Thermal,
        NativeConstants.EndHostFault => RunEndReason.HostFault,
        NativeConstants.EndInsufficientMemory => RunEndReason.InsufficientMemory,
        NativeConstants.EndAffinityFailed => RunEndReason.AffinityFailed,
        NativeConstants.EndInternalFault => RunEndReason.EngineFault,
        _ => RunEndReason.EngineFault,
    };

    /// <summary>Composes the end detail <c>RunStateMachine.EndDetail</c> receives.</summary>
    /// <param name="reason">The mapped end reason.</param>
    /// <param name="osError">The native <c>os_error</c>.</param>
    /// <param name="nativeDetail">The native <c>end_detail</c> text, or <see langword="null"/> when it was empty.</param>
    /// <returns>
    /// <see langword="null"/> for <see cref="RunEndReason.Completed"/>. "Windows error n: " followed by
    /// <paramref name="nativeDetail"/> for <see cref="RunEndReason.AffinityFailed"/>, and for
    /// <see cref="RunEndReason.InsufficientMemory"/> and <see cref="RunEndReason.EngineFault"/> when
    /// <paramref name="osError"/> is nonzero; just "Windows error n" when that detail is missing. Otherwise
    /// <paramref name="nativeDetail"/>, or <see langword="null"/> when it is empty.
    /// </returns>
    internal static string? FormatEndDetail(RunEndReason reason, uint osError, string? nativeDetail)
    {
        string? detail = string.IsNullOrEmpty(nativeDetail) ? null : nativeDetail;
        return reason switch
        {
            RunEndReason.Completed => null,
            RunEndReason.AffinityFailed => WithOsError(osError, detail),
            RunEndReason.InsufficientMemory or RunEndReason.EngineFault when osError != 0 => WithOsError(osError, detail),
            _ => detail,
        };
    }

    private static string WithOsError(uint osError, string? detail)
    {
        return detail is null
            ? string.Create(CultureInfo.InvariantCulture, $"Windows error {osError}")
            : string.Create(CultureInfo.InvariantCulture, $"Windows error {osError}: {detail}");
    }

    private static string? ReadEndDetail(in RqRunResult r)
    {
        // An in parameter may refer to managed memory, so the fixed buffer is pinned for the read.
        fixed (byte* detail = r.EndDetail)
        {
            string text = FixedUtf8.Read(detail, NativeConstants.EndDetailSize);
            return text.Length == 0 ? null : text;
        }
    }

    private static StepAttributes StepAt(IReadOnlyList<StepAttributes> steps, uint index, string field)
    {
        if (index >= (uint)steps.Count)
        {
            throw AbiMismatch(string.Create(
                CultureInfo.InvariantCulture,
                $"{field} is {index}, but the session has {steps.Count} steps."));
        }

        return steps[(int)index];
    }

    private static void EnsureStepCount(uint count, IReadOnlyList<StepAttributes> steps, string field)
    {
        if (count != (uint)steps.Count)
        {
            throw AbiMismatch(string.Create(
                CultureInfo.InvariantCulture,
                $"{field} is {count}, but the session has {steps.Count} steps."));
        }
    }

    private static int ToInt32(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw AbiMismatch(string.Create(CultureInfo.InvariantCulture, $"{field} is {value}, beyond the supported range."));
        }

        return (int)value;
    }

    private static TEnum Defined<TEnum>(TEnum value, uint raw, string field)
        where TEnum : struct, Enum
    {
        return Enum.IsDefined(value)
            ? value
            : throw AbiMismatch(string.Create(CultureInfo.InvariantCulture, $"{field} is {raw}, which is not a defined value."));
    }

    // Times arrive as unsigned integers. Integer ticks keep them exact, and a value beyond TimeSpan's range, which no
    // real run produces, saturates rather than throwing from a callback.
    private static TimeSpan FromMilliseconds(ulong milliseconds) => FromUnits(milliseconds, TimeSpan.TicksPerMillisecond);

    private static TimeSpan FromMicroseconds(ulong microseconds) => FromUnits(microseconds, TimeSpan.TicksPerMicrosecond);

    private static TimeSpan FromUnits(ulong value, long ticksPerUnit)
    {
        return value > (ulong)(long.MaxValue / ticksPerUnit)
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)value * ticksPerUnit);
    }

    private static InvalidOperationException AbiMismatch(string message)
    {
        return new InvalidOperationException("Engine payload does not match the configured session: " + message);
    }
}

