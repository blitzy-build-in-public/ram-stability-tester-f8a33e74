using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Unicode;
using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// What a <see cref="StepGate"/> decides for a step that is about to start. The values equal the native
/// <c>RQ_ACTION_CONTINUE</c> and <c>RQ_ACTION_ABORT</c> codes that <c>on_step_starting</c> returns, so the session
/// passes a decision to the engine by value.
/// </summary>
public enum StepGateDecision : uint
{
    /// <summary>
    /// Run the step. Native <c>RQ_ACTION_CONTINUE</c>.
    /// </summary>
    Continue = 0,

    /// <summary>
    /// Start no memory traffic for the step and end the run with the stop reason already recorded, or with
    /// <c>host_fault</c> when none was recorded. Native <c>RQ_ACTION_ABORT</c>.
    /// </summary>
    Abort = 1,
}

/// <summary>
/// Decides, synchronously on the engine's coordinator thread, whether a step that is about to start may run.
/// </summary>
/// <remarks>
/// <para>
/// The engine calls the gate from <c>on_step_starting</c>, after the previous step has finished and before any memory
/// traffic of this step. Whatever the gate makes durable is therefore on disk before the step's first access, which is
/// what lets the progress journal attribute a hang in the step's first moments to this step rather than the previous
/// one.
/// </para>
/// <para>
/// The gate may block. <c>RunController</c> enqueues the journal's <c>step</c> record and waits at most 5 s for its
/// durable acknowledgment together with <paramref name="stopEvent"/>
/// (<c>ProgressJournal.RecordStepStarting(step.Index, step.Count, step.Name).Wait(stopEvent,
/// ProgressJournal.AcknowledgmentTimeout)</c>). It returns <see cref="StepGateDecision.Continue"/> on acknowledgment,
/// and <see cref="StepGateDecision.Abort"/> when the stop event woke the wait, the acknowledgment timed out or the
/// write failed.
/// </para>
/// <para>
/// An Abort for any reason other than a woken stop event must be preceded by
/// <c>RequestStop(StopReason.HostFault)</c> on the session, so the run ends with <c>host_fault</c>. The session
/// requests that stop itself when a gate returns Abort while the stop event is unset; the first recorded reason wins,
/// so a gate that already requested a stop keeps its reason. An exception thrown by the gate likewise requests a
/// host-fault stop and aborts the step.
/// </para>
/// <para>
/// The gate must not dispose the session: <c>rq_session_destroy</c> cannot run inside an engine callback.
/// </para>
/// </remarks>
/// <param name="step">The step about to start, from the session's validated copy of the plan's steps.</param>
/// <param name="stopEvent">
/// The session's managed stop event. The session sets it only after <c>rq_session_stop</c> has recorded the native stop
/// reason, so a gate woken by it can return Abort without requesting a stop of its own. The gate only waits on it and
/// never sets, resets or disposes it.
/// </param>
/// <returns>Whether the step may run.</returns>
public delegate StepGateDecision StepGate(EngineStepStarted step, WaitHandle stopEvent);

/// <summary>
/// Receives each progress snapshot synchronously on the engine's coordinator thread, before the snapshot is posted to
/// the session's synchronization context as <c>ProgressChanged</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sink runs ahead of the post so that a consumer off the UI thread, the journal writer's 1 Hz heartbeat, sees the
/// latest progress even while the UI context is busy. Its production target is <c>ProgressJournal.PublishProgress</c>,
/// which only stores the snapshot atomically.
/// </para>
/// <para>
/// It must not block or perform I/O: the coordinator raises every callback and sequences the steps, so time spent
/// here delays the next progress tick and the stop path. An exception it throws stops the run with a host fault.
/// </para>
/// </remarks>
/// <param name="progress">The snapshot, immutable and safe to keep or publish to another thread.</param>
public delegate void ProgressSink(RunProgress progress);

/// <summary>
/// Receives a managed exception that the session caught in an engine callback or an event subscriber, so its owner can
/// log it. <c>RamQuick.Interop</c> does not reference <c>RamQuick.Logging</c>, so the session reports the fault here
/// and <c>RunController</c> queues the write to <c>&lt;data&gt;\logs\app-errors.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sink runs on the thread where the fault was caught: the engine's coordinator for a callback, including the step
/// gate and the progress sink, or the session's synchronization context for an event subscriber. After it returns, the
/// session requests a host-fault stop, so the run ends ABORTED rather than continuing with lost events.
/// </para>
/// <para>
/// It must neither block nor throw. An exception it throws is discarded, because no managed exception may unwind into
/// native frames.
/// </para>
/// </remarks>
/// <param name="callbackName">
/// The callback or event in which the fault was caught, for example <c>on_progress</c> or <c>Completed handler</c>.
/// </param>
/// <param name="exception">The exception that was caught.</param>
public delegate void CallbackFaultSink(string callbackName, Exception exception);

/// <summary>
/// The configuration of one engine session: the region to test, the workers, the plan's steps, and the hooks the
/// coordinator calls synchronously. <c>IEngine.CreateSession</c> validates it as <see cref="Validate"/> does and
/// marshals it into <c>rq_session_config</c> and <c>rq_step_def[]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The <see cref="StepGate"/>, <see cref="ProgressSink"/> and <see cref="CallbackFault"/> delegates
/// are held by the <c>EngineSession</c> through these options, an edge that points outward from the session. They are
/// never held by the <c>CallbackState</c> that the native <c>GCHandle</c> roots. A delegate whose target holds the
/// session or its owner therefore cannot keep an abandoned session alive: once the owner lets the session go, nothing
/// native reaches it, and its handle's finalizer destroys the native session and releases its memory.
/// </para>
/// <para>
/// <b>Validation.</b> <see cref="Validate"/> reports programming errors as exceptions before any native call. The
/// session reads <see cref="Steps"/> once into its own copy and validates that copy, so the steps it marshals are the
/// steps it checked, and changes to the caller's list, during creation or after it, never reach it. Rules that
/// depend on the machine, such as a worker count above the logical processor count or a forced instruction set the CPU
/// lacks, are left to <c>rq_session_create</c>, whose <c>RQ_E_INVALID_ARGUMENT</c> surfaces as
/// <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public sealed class EngineSessionOptions
{
    /// <summary>
    /// The default <see cref="MinRegionBytes"/>: 1 GiB, the smallest region a run may shrink to when locking fails.
    /// </summary>
    public const ulong DefaultMinRegionBytes = 1UL << 30;

    /// <summary>
    /// The most steps a session may hold: 1,024, the engine's limit. <c>rq_session_create</c> rejects a longer step
    /// array with <c>RQ_E_INVALID_ARGUMENT</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> rejects a longer <see cref="Steps"/> list from its count alone, before reading any
    /// element, so an oversized list is never traversed, copied or marshalled.
    /// </remarks>
    public const int MaxStepCount = 1024;

    /// <summary>
    /// The default <see cref="ProgressInterval"/>: 250 ms, so progress reaches the UI at 4 Hz.
    /// </summary>
    public static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromMilliseconds(250);

    // rq_step_def.name is 64 bytes including its NUL terminator.
    private const int MaxStepNameBytes = NativeConstants.NameSize - 1;

    // rq_step_def.coverage_budget_bp holds percent × 100 in a uint32_t.
    private const int MaxCoverageBudgetPercent = (int)(uint.MaxValue / 100);

    // Every native duration field is a uint32_t count of milliseconds.
    private const string NativeMillisecondsRange = "between 1 ms and 4,294,967,295 ms in whole milliseconds";

    /// <summary>
    /// Gets the planned bytes under test, in bytes: <c>MemorySizing</c>'s
    /// <c>min(0.90 × available, available − 1 GiB)</c>, rounded down to a multiple of <c>workers × 2 MiB</c>. The
    /// engine divides it among the workers and rounds each slice down to 2 MiB; the bytes it actually locks are
    /// reported as <see cref="EnginePrepared.TestedBytes"/>. At least <see cref="MinRegionBytes"/>.
    /// Native <c>rq_session_config.region_bytes</c>.
    /// </summary>
    public required ulong RegionBytes { get; init; }

    /// <summary>
    /// Gets the floor for shrink-and-retry, in bytes. When locking fails at the planned size, the engine retries at
    /// 90 % of the previous size down to this floor; below it the run ends with <c>insufficient_memory</c>.
    /// Greater than 0 and at most <see cref="RegionBytes"/>. Defaults to <see cref="DefaultMinRegionBytes"/>.
    /// Native <c>rq_session_config.min_region_bytes</c>.
    /// </summary>
    public ulong MinRegionBytes { get; init; } = DefaultMinRegionBytes;

    /// <summary>
    /// Gets the seed of the random data patterns and of the random-block permutation. Any value is valid; defaults to
    /// 0. <c>RunController</c> supplies a fresh random value per run, and tests fix one to make runs reproducible.
    /// Native <c>rq_session_config.seed</c>.
    /// </summary>
    public ulong Seed { get; init; }

    /// <summary>
    /// Gets the number of workers, each pinned to its own logical processor. The application passes
    /// <see cref="SystemSnapshot.LogicalProcessors"/>, one worker per logical processor across every processor group.
    /// At least 1; the engine rejects a count above the logical processor count.
    /// Native <c>rq_session_config.worker_count</c>.
    /// </summary>
    public required int WorkerCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether the first confirmed error ends the run with <c>first_error</c>.
    /// The first worker to confirm a mismatch stops the run itself, without waiting for a callback. Defaults to
    /// <see langword="false"/>; the application passes the Stop on error checkbox.
    /// Native <c>rq_session_config.stop_on_error</c>.
    /// </summary>
    public bool StopOnError { get; init; }

    /// <summary>
    /// Gets the most detailed error records the engine delivers per run; past it, error batches carry only the true
    /// total. At least 1. Defaults to <see cref="ErrorGrouping.StoredDetailCap"/> (10,000).
    /// Native <c>rq_session_config.error_detail_cap</c>.
    /// </summary>
    public int ErrorDetailCap { get; init; } = ErrorGrouping.StoredDetailCap;

    /// <summary>
    /// Gets the interval between progress callbacks while a step runs. The engine receives whole milliseconds, so the
    /// interval is at least 1 ms after truncation and at most <see cref="uint.MaxValue"/> ms. Defaults to
    /// <see cref="DefaultProgressInterval"/>.
    /// Native <c>rq_session_config.progress_interval_ms</c>.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = DefaultProgressInterval;

    /// <summary>
    /// Gets the instruction set to force for the memory kernels, or <see langword="null"/>, the default, to let the
    /// engine select the best one the CPU and OS support (<c>RQ_ISA_AUTO</c>). The application always leaves it null;
    /// native equivalence checks and integration tests force a path. A forced set the CPU lacks is rejected by the
    /// engine. Native <c>rq_session_config.isa_override</c>.
    /// </summary>
    public InstructionSet? IsaOverride { get; init; }

    /// <summary>
    /// Gets the plan's ordered steps, mapped from <c>RamQuick.Plans.StepDefinition</c> by the caller, because this
    /// library never references the plans. At least one step and at most <see cref="MaxStepCount"/>;
    /// <see cref="StepAttributes.Index"/> equals each step's position, and every step name is non-empty, free of NUL
    /// characters, well-formed and at most 63 UTF-8 bytes. Names are rejected rather than truncated, because the session
    /// reports step names from this managed copy. A step with a hold uses <see cref="AccessOrder.RandomBlocks"/>, the
    /// order in which it writes its whole slice, and a pattern other than moving inversion, whose own whole-slice
    /// sweeps take no hold. Native <c>rq_session_config.steps</c> and <c>step_count</c>.
    /// </summary>
    public required IReadOnlyList<StepAttributes> Steps { get; init; }

    /// <summary>
    /// Gets the gate the coordinator calls before each step's memory traffic, or <see langword="null"/> to let every
    /// step run. See <see cref="Interop.StepGate"/> for the threading and Abort contract.
    /// </summary>
    public StepGate? StepGate { get; init; }

    /// <summary>
    /// Gets the sink the coordinator calls with each progress snapshot before it is posted, or
    /// <see langword="null"/> for none. It must not block; see <see cref="Interop.ProgressSink"/>.
    /// </summary>
    public ProgressSink? ProgressSink { get; init; }

    /// <summary>
    /// Gets the sink that receives managed exceptions the session caught in callbacks and event subscribers, or
    /// <see langword="null"/> for none. A caught fault stops the run with a host fault whether or not a sink is set;
    /// see <see cref="CallbackFaultSink"/>.
    /// </summary>
    public CallbackFaultSink? CallbackFault { get; init; }

    /// <summary>
    /// Checks every option that crosses the native boundary, so that a programming error surfaces as an exception
    /// before any native call.
    /// </summary>
    /// <remarks>
    /// Durations are checked at the engine's resolution of whole milliseconds, the value the session marshals after
    /// truncation, so a positive duration shorter than 1 ms is rejected instead of reaching the engine as 0.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MinRegionBytes"/> is 0; <see cref="RegionBytes"/> is below <see cref="MinRegionBytes"/>;
    /// <see cref="WorkerCount"/> or <see cref="ErrorDetailCap"/> is below 1; <see cref="ProgressInterval"/> is not
    /// between 1 ms and <see cref="uint.MaxValue"/> ms; or <see cref="IsaOverride"/> is not a defined
    /// <see cref="InstructionSet"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Steps"/> is null, empty or longer than <see cref="MaxStepCount"/>; a step is null, out of position,
    /// or has an invalid name, pattern, access order, thread mode, hold, time budget or coverage budget; or a step has
    /// a shape the engine does not run: a hold with a moving-inversion pattern, or a hold with an access order other
    /// than <see cref="AccessOrder.RandomBlocks"/>.
    /// </exception>
    public void Validate()
    {
        // The same single read of Steps a session takes, so the verdict is on one consistent list even when the
        // caller's list changes while it is read.
        _ = ValidateAndCopySteps();
    }

    /// <summary>
    /// Checks every option as <see cref="Validate"/> does and returns the steps it checked: a copy of
    /// <see cref="Steps"/> made by reading the caller's list once. <c>EngineSession</c> keeps and marshals this copy.
    /// </summary>
    /// <remarks>
    /// <see cref="Steps"/> holds the caller's list by reference, and another thread, or a list whose count or elements
    /// are unstable, can change what a second read returns. The copy is therefore taken before any step rule runs, from
    /// one <see cref="IReadOnlyCollection{T}.Count"/> read and exactly that many indexer reads, and the rules run over
    /// the copy, so the steps a session marshals and reports are the steps that were checked.
    /// </remarks>
    /// <returns>The checked steps, immutable and independent of the caller's list.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option is out of range, as for <see cref="Validate"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Steps"/> or one of its steps is invalid, as for <see cref="Validate"/>.
    /// </exception>
    internal ReadOnlyCollection<StepAttributes> ValidateAndCopySteps()
    {
        ArgumentOutOfRangeException.ThrowIfZero(MinRegionBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(RegionBytes, MinRegionBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(WorkerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ErrorDetailCap, 1);

        if (!FitsNativeMilliseconds(ProgressInterval))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProgressInterval),
                ProgressInterval,
                $"{nameof(ProgressInterval)} must be {NativeMillisecondsRange}.");
        }

        if (IsaOverride is { } isa && !Enum.IsDefined(isa))
        {
            throw new ArgumentOutOfRangeException(
                nameof(IsaOverride),
                isa,
                $"{nameof(IsaOverride)} must be null or a defined {nameof(InstructionSet)}.");
        }

        StepAttributes[] steps = CopySteps();
        ValidateSteps(steps);
        return Array.AsReadOnly(steps);
    }

    // Reads the caller's list exactly once. The count is read a single time and bounds everything after it, and the
    // copy is filled by indexer rather than enumeration, so a list that grows while it is read still yields a copy of
    // the size that was checked.
    private StepAttributes[] CopySteps()
    {
        // Steps is declared non-nullable, but an initializer can still assign null under a suppression or from code
        // without nullable analysis; it must fail here as an argument error, not later as a NullReferenceException.
        // A list reporting a negative count holds no steps either.
        IReadOnlyList<StepAttributes>? steps = Steps;
        int count = steps?.Count ?? 0;
        if (steps is null || count < 1)
        {
            throw new ArgumentException("A session needs at least one step.", nameof(Steps));
        }

        // Decided from the count alone, before any element is read or the copy is allocated: the session copies and
        // marshals every step it accepts, so an oversized list must fail here rather than after that work, at the
        // engine's own limit.
        if (count > MaxStepCount)
        {
            throw new ArgumentException(
                $"{nameof(Steps)} holds {count} steps; a session holds at most {MaxStepCount}, "
                + "the engine's limit.",
                nameof(Steps));
        }

        var copy = new StepAttributes[count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = steps[i];
        }

        return copy;
    }

    private static void ValidateSteps(StepAttributes[] steps)
    {
        Span<byte> nameScratch = stackalloc byte[MaxStepNameBytes];
        for (int i = 0; i < steps.Length; i++)
        {
            StepAttributes? step = steps[i];
            if (step is null)
            {
                throw StepError(i, "must not be null.");
            }

            if (step.Index != i)
            {
                throw StepError(i, $"has Index {step.Index}; a step's Index must equal its position in Steps.");
            }

            ValidateStepName(i, step.Name, nameScratch);

            if (!Enum.IsDefined(step.Pattern))
            {
                throw StepError(i, $"has the undefined {nameof(PatternKind)} value {(int)step.Pattern}.");
            }

            if (!Enum.IsDefined(step.Order))
            {
                throw StepError(i, $"has the undefined {nameof(AccessOrder)} value {(int)step.Order}.");
            }

            if (!Enum.IsDefined(step.Threads))
            {
                throw StepError(i, $"has the undefined {nameof(ThreadMode)} value {(int)step.Threads}.");
            }

            if (!FitsNativeMilliseconds(step.TimeBudget))
            {
                throw StepError(i, $"has the time budget {step.TimeBudget}; it must be {NativeMillisecondsRange}.");
            }

            // A positive hold shorter than 1 ms would reach the engine as 0, a windowed step, while
            // StepAttributes.HasHold still reports a hold step to the analyzer and the summary.
            if (step.Hold != TimeSpan.Zero && !FitsNativeMilliseconds(step.Hold))
            {
                throw StepError(i, $"has the hold {step.Hold}; it must be zero or {NativeMillisecondsRange}.");
            }

            // Only the step shapes the engine runs: rq_session_create rejects any other with a bare
            // RQ_E_INVALID_ARGUMENT, so they are checked here, where the error can name the step. A hold step writes its
            // whole slice in RandomBlocks order, so requiring it to declare that order keeps the order the summary and
            // the analyzer read equal to the order that runs.
            if (IsMovingInversion(step.Pattern) && step.HasHold)
            {
                throw StepError(
                    i,
                    $"(\"{step.Name}\") has the pattern {step.Pattern} with the hold {step.Hold}; moving inversion runs "
                    + "its own whole-slice sweeps and takes no hold.");
            }
            else if (step.HasHold && step.Order != AccessOrder.RandomBlocks)
            {
                throw StepError(
                    i,
                    $"(\"{step.Name}\") has the hold {step.Hold} with the access order {step.Order}; a hold step writes "
                    + $"its whole slice in {nameof(AccessOrder.RandomBlocks)} order, so its order must be "
                    + $"{nameof(AccessOrder.RandomBlocks)}.");
            }

            if (step.CoverageBudgetPercent < 1 || step.CoverageBudgetPercent > MaxCoverageBudgetPercent)
            {
                throw StepError(
                    i,
                    $"has the coverage budget {step.CoverageBudgetPercent} %; "
                    + $"it must be between 1 % and {MaxCoverageBudgetPercent} %.");
            }
        }
    }

    private static void ValidateStepName(int index, string? name, Span<byte> scratch)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw StepError(index, "has an empty name.");
        }

        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw StepError(
                index,
                "has a name containing a NUL character, which would end the native name field early.");
        }

        int byteCount = FixedUtf8.GetByteCount(name);
        if (byteCount > MaxStepNameBytes)
        {
            throw StepError(
                index,
                $"has the name \"{name}\", which is {byteCount} UTF-8 bytes; "
                + $"the native name field holds at most {MaxStepNameBytes}.");
        }

        // The native copy would replace an unpaired surrogate with U+FFFD, so the engine's name would differ from
        // the managed one the session reports. With the length already checked, InvalidData is the only failure left.
        if (Utf8.FromUtf16(name, scratch, out _, out _, replaceInvalidSequences: false) != OperationStatus.Done)
        {
            throw StepError(
                index,
                $"has the name \"{name}\", which contains an unpaired surrogate and is not valid UTF-16.");
        }
    }

    // The session marshals durations as (uint)TimeSpan.TotalMilliseconds, which truncates; integer division of the
    // ticks gives the same whole-millisecond value without floating-point rounding.
    private static bool FitsNativeMilliseconds(TimeSpan value)
    {
        long wholeMilliseconds = value.Ticks / TimeSpan.TicksPerMillisecond;
        return wholeMilliseconds >= 1 && wholeMilliseconds <= uint.MaxValue;
    }

    private static bool IsMovingInversion(PatternKind pattern) =>
        pattern is PatternKind.MovingInversionWalking or PatternKind.MovingInversionRandom;

    private static ArgumentException StepError(int index, string problem) =>
        new($"{nameof(Steps)}[{index}] {problem}", nameof(Steps));
}
