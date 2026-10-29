using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// The JSON summary of one run, schema version 1: <c>&lt;base&gt;-summary.json</c>, rendered for reading as
/// <c>&lt;base&gt;-summary.txt</c>. Each section of the schema is one sealed record.
/// </summary>
/// <remarks>
/// <para>
/// Value rules: <c>RunSummaryJsonContext</c> camelCases every property name. Every quantity (bytes, counts,
/// offsets, durations, percentages, temperatures) is a number, and every double is finite. Addresses, bit
/// masks and raw 64-bit data words are strings formatted by <see cref="SummaryFormat.Hex64"/>. Timestamps are
/// UTC <see cref="DateTimeOffset"/> values. Enumerations are held as their member names, so no member is
/// enum-typed. Nullable members are written as <c>null</c>, never omitted: <c>null</c> marks a value the run
/// never captured.
/// </para>
/// <para>
/// Construction: <see cref="Build"/> is the single producer, used by normal-run publication and by crash
/// recovery alike, so the two cannot drift. It is pure, with no I/O, clock or environment reads, so a
/// recovered summary carries the crashed run's journal values and never the next launch's machine state.
/// </para>
/// <para>
/// Deserialization: every type is parameterless-constructible, with init-only properties and no
/// <see langword="required"/> member; the one get-only property, <see cref="SummaryCauseHints.Label"/>, is
/// written but never read back. The .NET 8 System.Text.Json source generator assigns default(T) to every
/// init-only property a document omits, so the init accessors of non-nullable reference properties restore
/// their empty value, and a summary read back never holds <see langword="null"/> where its type says it
/// cannot. Value equality compares list members element by element, so a summary read back equals the one
/// that was written whenever its timestamps are whole milliseconds.
/// </para>
/// <para>
/// Immutability: every list-valued member of these records and of <see cref="RunSummarySource"/> is a
/// read-only snapshot taken at construction. Its init accessor copies the supplied list, whether
/// <see cref="Build"/>, an object initializer, a <see langword="with"/> expression or deserialization
/// supplies it, and exposes the copy through a wrapper that cannot be cast back to a writable list. A
/// summary therefore cannot change while it is published on a background thread, and its equality and
/// hash code stay fixed.
/// </para>
/// </remarks>
public sealed record RunSummary
{
    /// <summary>
    /// The schema version this build writes, the summary's <c>schemaVersion</c>.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private readonly SummaryApp _app = new();
    private readonly SummaryRun _run = new();
    private readonly SummarySystem _system = new();
    private readonly SummaryMemory _memory = new();
    private readonly SummaryPlan _plan = new();
    private readonly IReadOnlyList<SummaryStep> _steps = Array.Empty<SummaryStep>();
    private readonly SummaryCoverage _coverage = new();
    private readonly SummaryTemperatures _temperatures = new();
    private readonly SummaryErrors _errors = new();
    private readonly SummaryCauseHints _causeHints = new();

    /// <summary>
    /// Gets the format version, <see cref="CurrentSchemaVersion"/> for every summary this build writes. A
    /// document that omits it reads back as 0.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Gets the product and build that ran the test (<c>app.*</c>).
    /// </summary>
    public SummaryApp App
    {
        get => _app;
        init => _app = value ?? new SummaryApp();
    }

    /// <summary>
    /// Gets the run's identity, outcome and times (<c>run.*</c>).
    /// </summary>
    public SummaryRun Run
    {
        get => _run;
        init => _run = value ?? new SummaryRun();
    }

    /// <summary>
    /// Gets the machine context captured at Start (<c>system.*</c>).
    /// </summary>
    public SummarySystem System
    {
        get => _system;
        init => _system = value ?? new SummarySystem();
    }

    /// <summary>
    /// Gets the capacities and detected memory configuration (<c>memory.*</c>).
    /// </summary>
    public SummaryMemory Memory
    {
        get => _memory;
        init => _memory = value ?? new SummaryMemory();
    }

    /// <summary>
    /// Gets the plan's duration and coverage targets (<c>plan.*</c>).
    /// </summary>
    public SummaryPlan Plan
    {
        get => _plan;
        init => _plan = value ?? new SummaryPlan();
    }

    /// <summary>
    /// Gets one entry per step that started, in step order, planned values beside actuals (<c>steps[]</c>).
    /// </summary>
    public IReadOnlyList<SummaryStep> Steps
    {
        get => _steps;
        init => _steps = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets the coverage outcome (<c>coverage.*</c>).
    /// </summary>
    public SummaryCoverage Coverage
    {
        get => _coverage;
        init => _coverage = value ?? new SummaryCoverage();
    }

    /// <summary>
    /// Gets the peak fresh temperatures and the worst status seen per sensor (<c>temperatures.*</c>).
    /// </summary>
    public SummaryTemperatures Temperatures
    {
        get => _temperatures;
        init => _temperatures = value ?? new SummaryTemperatures();
    }

    /// <summary>
    /// Gets the true error total and the stored error detail (<c>errors.*</c>).
    /// </summary>
    public SummaryErrors Errors
    {
        get => _errors;
        init => _errors = value ?? new SummaryErrors();
    }

    /// <summary>
    /// Gets the ranked cause hints, always labelled <see cref="SummaryCauseHints.HintLabel"/>
    /// (<c>causeHints.*</c>).
    /// </summary>
    public SummaryCauseHints CauseHints
    {
        get => _causeHints;
        init => _causeHints = value ?? new SummaryCauseHints();
    }

    /// <summary>
    /// Gets where a run that never reached a verdict stopped, or <see langword="null"/> for every run that
    /// did (<c>unfinishedRun</c>).
    /// </summary>
    public SummaryUnfinishedRun? UnfinishedRun { get; init; }

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing <see cref="Steps"/> element
    /// by element.
    /// </summary>
    /// <param name="other">The summary to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(RunSummary? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && SchemaVersion == other.SchemaVersion
            && App == other.App
            && Run == other.Run
            && System == other.System
            && Memory == other.Memory
            && Plan == other.Plan
            && Steps.SequenceEqual(other.Steps)
            && Coverage == other.Coverage
            && Temperatures == other.Temperatures
            && Errors == other.Errors
            && CauseHints == other.CauseHints
            && UnfinishedRun == other.UnfinishedRun);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(RunSummary)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(SchemaVersion);
        hash.Add(App);
        hash.Add(Run);
        hash.Add(System);
        hash.Add(Memory);
        hash.Add(Plan);
        foreach (SummaryStep step in Steps)
        {
            hash.Add(step);
        }

        hash.Add(Coverage);
        hash.Add(Temperatures);
        hash.Add(Errors);
        hash.Add(CauseHints);
        hash.Add(UnfinishedRun);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Builds the summary of a run from its journal records and the sections its caller supplies.
    /// </summary>
    /// <param name="source">
    /// The run's records. A normal run passes every record it journalled, its stored error records and its
    /// cause hints. Recovery passes the records read back from the journal, no error detail and
    /// <see cref="SummaryCauseHints.Empty"/>.
    /// </param>
    /// <returns>The summary, schema version <see cref="CurrentSchemaVersion"/>.</returns>
    /// <remarks>
    /// <para>
    /// Pure and deterministic: the same source always yields an equal summary. Every value comes from the
    /// source, and a value it does not hold is <see langword="null"/>: a missing record, a nullable field a
    /// record left empty, a timestamp a record omitted (<c>default(DateTimeOffset)</c>), or a 0 where no run
    /// can measure 0 (a processor count, the tested bytes).
    /// </para>
    /// <para>
    /// Times: <see cref="SummaryRun.DurationSeconds"/> is the engine's measured test time from the result
    /// (the last heartbeat's for an unfinished run), never a difference of wall-clock stamps. The UTC stamps
    /// come from their records: <see cref="SummaryRun.EndedUtc"/> from the result's Completed stamp, or for
    /// an unfinished run from the last heartbeat's stamp alone. <see cref="SummaryRun.TimeToVerdictSeconds"/>
    /// is the interval between the Start-accepted and verdict stamps.
    /// </para>
    /// <para>
    /// A run without a <see cref="RunSummarySource.Result"/> never reached a verdict. It is reported as
    /// <c>FAIL</c> with end reason <see cref="RunEndReason.PreviousRunUnfinished"/>, an end detail naming
    /// where it stopped and which times are approximate or unknown, its end time, duration and totals taken
    /// from the last heartbeat, and an <see cref="UnfinishedRun"/> section. A time the last heartbeat did not
    /// capture, and both times when no heartbeat was written, are <see langword="null"/>; no other record's
    /// stamp stands in for one.
    /// </para>
    /// <para>
    /// Steps: one entry per started step, in step order. A finished step is the last
    /// <see cref="RunSummarySource.FinishedSteps"/> entry for its number, joined by number to its definition
    /// in the <c>begin</c> record; an entry with no definition is skipped, because the summary cannot
    /// describe a step the plan never held. The running step of an unfinished run follows with
    /// <see langword="null"/> actuals and <see cref="SummaryStep.Completed"/> <see langword="false"/>.
    /// </para>
    /// <para>
    /// Errors: the total is the result's true total. Without a result it is the last heartbeat's total,
    /// raised to the sum of the finished steps' totals when that is larger. Groups, distinct addresses and
    /// records describe <see cref="RunSummarySource.StoredErrors"/> only.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> or its <see cref="RunSummarySource.Begin"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="RunSummarySource.StoredErrors"/> contains a <see langword="null"/> element.
    /// </exception>
    public static RunSummary Build(RunSummarySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        JournalBegin begin = source.Begin
            ?? throw new ArgumentNullException(nameof(source), "The summary source carries no begin record.");

        JournalPrepared? prepared = source.Prepared;
        JournalResult? result = source.Result;
        JournalBeat? lastBeat = source.LastBeat;

        // A prepared region is never empty, so 0 is what a prepared record that omitted the field reads back
        // as: not captured.
        ulong? testedBytes = prepared?.TestedBytes is { } tested && tested > 0 ? tested : null;
        Dictionary<int, JournalStep> definitions = DefinitionsByNumber(begin);
        IReadOnlyList<JournalStepEnd> finishedSteps = LatestPerStep(source.FinishedSteps);
        int stepCount = begin.Steps.Count > 0 ? begin.Steps.Count : source.UnfinishedStep?.Count ?? 0;
        (int? lastStepNumber, string? lastStepName) = LastPosition(definitions, finishedSteps, source.UnfinishedStep);

        DateTimeOffset? startRequestedUtc = CapturedUtc(begin.StartRequestedUtc);
        DateTimeOffset? startedUtc = CapturedUtc(prepared?.Utc);
        DateTimeOffset? lastBeatUtc = CapturedUtc(lastBeat?.Utc);

        // A finished run's end is the Completed stamp its result captured, never an approximation. An
        // unfinished run's end is the last heartbeat's stamp alone: another record's stamp is not a time the
        // journal captured as the end, so without a stamped heartbeat the end is not captured.
        DateTimeOffset? endedUtc = result is not null ? CapturedUtc(result.CompletedUtc) : lastBeatUtc;
        DateTimeOffset? verdictUtc = CapturedUtc(result?.VerdictUtc);

        // The engine's monotonic test time, Running to Completed (0 when preparation failed), not the
        // difference of two wall-clock stamps that a clock correction or a dispatcher delay can skew.
        double? durationSeconds = result is not null
            ? Seconds(result.ElapsedMs)
            : lastBeat is null ? null : Seconds(lastBeat.ElapsedMs);

        var run = new SummaryRun
        {
            Id = SummaryFormat.RunId(begin.RunId),
            TestType = begin.TestType,
            StopOnError = begin.StopOnError,
            Verdict = result is null ? FailVerdict : VerdictOf(result),
            EndReason = result is null ? nameof(RunEndReason.PreviousRunUnfinished) : result.EndReason.ToString(),
            EndDetail = result is null
                ? UnfinishedDetail(
                    lastStepNumber,
                    stepCount,
                    lastStepName,
                    lastBeat is not null,
                    lastBeatUtc is not null)
                : result.EndDetail,
            StartRequestedUtc = startRequestedUtc,
            StartedUtc = startedUtc,
            EndedUtc = endedUtc,
            VerdictUtc = verdictUtc,
            DurationSeconds = durationSeconds,
            TimeToVerdictSeconds = SecondsBetween(startRequestedUtc, verdictUtc),
            FirstErrorSeconds = result?.FirstErrorMs is { } firstErrorMs ? Seconds(firstErrorMs) : null,
            Recovered = source.Recovered,
            Partial = source.Partial,
        };

        double targetCoveragePercent = Finite(begin.TargetCoveragePercent);
        double coveragePercent = (result is null ? null : Percent(result.VerifiedBytes, testedBytes))
            ?? FiniteOrNull(lastBeat?.Coverage)
            ?? 0;

        SummaryTemperatures temperatures = source.Temperatures;
        if (source.Recovered && lastBeat is not null)
        {
            // Recovery folds every heartbeat into the temperatures it supplies. Merging the last one again
            // is idempotent, and keeps its peak when a caller supplied Initial instead.
            temperatures = temperatures.Merge(lastBeat.CpuC, lastBeat.CpuStatus, lastBeat.RamC, lastBeat.RamStatus);
        }

        return new RunSummary
        {
            SchemaVersion = CurrentSchemaVersion,
            App = new SummaryApp { Name = source.AppName, Version = begin.AppVersion },
            Run = run,
            System = BuildSystem(begin.System, prepared),
            Memory = BuildMemory(begin, prepared, testedBytes, source.Released),
            Plan = new SummaryPlan
            {
                TargetSeconds = Finite(begin.TargetSeconds),
                TargetCoveragePercent = targetCoveragePercent,
            },
            Steps = BuildSteps(definitions, finishedSteps, source.UnfinishedStep, testedBytes),
            Coverage = new SummaryCoverage
            {
                Percent = coveragePercent,
                TargetPercent = targetCoveragePercent,
                TargetReached = coveragePercent >= targetCoveragePercent,
            },
            Temperatures = temperatures,
            Errors = BuildErrors(source, result, lastBeat, finishedSteps),
            CauseHints = source.CauseHints,
            UnfinishedRun = result is not null
                ? null
                : new SummaryUnfinishedRun
                {
                    LastStepIndex = lastStepNumber,
                    LastStepName = lastStepName,
                    StepCount = stepCount,
                    LastHeartbeatUtc = lastBeatUtc,
                    LastCoveragePercent = FiniteOrNull(lastBeat?.Coverage),
                },
        };
    }

    // An unfinished run is a FAIL result. A result record whose verdict text was lost falls back to its
    // terminal state's text and then to FAIL as well, never to PASS.
    private static string FailVerdict => RunState.Failed.ToVerdictText()!;

    private static string VerdictOf(JournalResult result) =>
        result.Verdict.Length > 0 ? result.Verdict : result.TerminalState.ToVerdictText() ?? FailVerdict;

    private static string UnfinishedDetail(
        int? lastStepNumber,
        int stepCount,
        string? lastStepName,
        bool hasHeartbeat,
        bool heartbeatStamped)
    {
        string position = lastStepNumber is not { } number
            ? "while preparing memory (before step 1)"
            : string.IsNullOrEmpty(lastStepName)
                ? string.Create(CultureInfo.InvariantCulture, $"at step {number} of {stepCount}")
                : string.Create(CultureInfo.InvariantCulture, $"at step {number} of {stepCount} ({lastStepName})");

        // The schema has no field for approximate values, so the detail labels them. Both times come from the
        // last heartbeat alone: its elapsed time gives durationSeconds and its stamp gives endedUtc, and a
        // value the heartbeat did not capture is null, which the detail calls unknown.
        string approximation = !hasHeartbeat
            ? "No heartbeat was recorded: endedUtc and durationSeconds are unknown."
            : heartbeatStamped
                ? "endedUtc and durationSeconds are approximate (last heartbeat)."
                : "durationSeconds is approximate (last heartbeat) and endedUtc is unknown.";

        return $"The previous test did not finish {position}, which likely indicates severe instability. {approximation}";
    }

    private static SummarySystem BuildSystem(JournalSystemInfo system, JournalPrepared? prepared)
    {
        // The set the session selected, else the best set the begin record captured. An undefined value is
        // what a record that omitted the field reads back as, so it counts as not captured.
        InstructionSet? isa = prepared is not null && Enum.IsDefined(prepared.IsaSelected)
            ? prepared.IsaSelected
            : Enum.IsDefined(system.BestIsa) ? system.BestIsa : null;

        // JournalBegin.Create requires an OS description, and every machine has a processor and a group,
        // so an empty description or a zero count was never captured.
        return new SummarySystem
        {
            Os = string.IsNullOrWhiteSpace(system.Os) ? null : system.Os,
            Cpu = string.IsNullOrWhiteSpace(system.CpuName) ? null : system.CpuName,
            LogicalProcessors = system.LogicalProcessors > 0 ? system.LogicalProcessors : null,
            ProcessorGroups = system.ProcessorGroups > 0 ? system.ProcessorGroups : null,
            InstructionSet = isa is { } named ? SummaryFormat.InstructionSetName(named) : null,
            ReducedInstructionSet = isa is { } selected ? selected == InstructionSet.Sse2 : null,
        };
    }

    // The begin record's quantities are nullable, so one it never captured stays null here and a captured 0
    // stays 0. testedBytes is Build's: null when preparation never finished or its record lacked the size.
    private static SummaryMemory BuildMemory(
        JournalBegin begin,
        JournalPrepared? prepared,
        ulong? testedBytes,
        JournalReleased? released)
    {
        JournalMemoryInfo memory = begin.Memory;
        return new SummaryMemory
        {
            InstalledBytes = memory.InstalledBytes,
            AvailableBytesAtStart = begin.AvailableBytes,
            PlannedBytes = begin.PlannedBytes,
            TestedBytes = testedBytes,
            ReleaseSeconds = released is null ? null : Seconds(released.ReleaseMs),
            PageMode = prepared is null ? null : DefinedNameOrNull(prepared.PageMode),
            AddressKind = prepared is null ? null : DefinedNameOrNull(prepared.AddressKind),
            Type = DefinedNameOrNull(memory.Type) ?? nameof(MemoryType.Unknown),
            SpeedMTs = memory.SpeedMTs,
            Channels = memory.Channels,
            Dimms = memory.Dimms,
        };
    }

    private static Dictionary<int, JournalStep> DefinitionsByNumber(JournalBegin begin)
    {
        var definitions = new Dictionary<int, JournalStep>();
        foreach (JournalStep? step in begin.Steps)
        {
            if (step is not null)
            {
                definitions.TryAdd(step.Number, step);
            }
        }

        return definitions;
    }

    // A step reports one stepEnd; should a journal hold two for one number, the later one wins.
    private static IReadOnlyList<JournalStepEnd> LatestPerStep(IReadOnlyList<JournalStepEnd> finishedSteps)
    {
        var latest = new SortedDictionary<int, JournalStepEnd>();
        foreach (JournalStepEnd? step in finishedSteps)
        {
            if (step is not null)
            {
                latest[step.Index] = step;
            }
        }

        return latest.Values.ToArray();
    }

    // Where an unfinished run stopped: the step that was running, else the last step that finished, else
    // nowhere, because the run never left preparation.
    private static (int? Number, string? Name) LastPosition(
        Dictionary<int, JournalStep> definitions,
        IReadOnlyList<JournalStepEnd> finishedSteps,
        JournalStepStart? unfinishedStep)
    {
        if (unfinishedStep is not null)
        {
            string? name = unfinishedStep.Name.Length > 0
                ? unfinishedStep.Name
                : DefinitionName(definitions, unfinishedStep.Index);
            return (unfinishedStep.Index, name);
        }

        if (finishedSteps.Count > 0)
        {
            int number = finishedSteps[^1].Index;
            return (number, DefinitionName(definitions, number));
        }

        return (null, null);
    }

    private static string? DefinitionName(Dictionary<int, JournalStep> definitions, int number) =>
        definitions.TryGetValue(number, out JournalStep? definition) ? definition.Name : null;

    private static SummaryStep[] BuildSteps(
        Dictionary<int, JournalStep> definitions,
        IReadOnlyList<JournalStepEnd> finishedSteps,
        JournalStepStart? unfinishedStep,
        ulong? testedBytes)
    {
        var steps = new SortedDictionary<int, SummaryStep>();
        foreach (JournalStepEnd end in finishedSteps)
        {
            if (definitions.TryGetValue(end.Index, out JournalStep? definition))
            {
                steps[end.Index] = PlannedStep(definition) with
                {
                    ActualSeconds = Seconds(end.ActualMs),
                    OverrunSeconds = Seconds(end.OverrunMs),
                    Passes = end.Passes,
                    CoveragePercent = Percent(end.VerifiedBytes, testedBytes),
                    Errors = end.Errors,
                    Completed = end.Completed,
                };
            }
        }

        if (unfinishedStep is not null
            && !finishedSteps.Any(end => end.Index == unfinishedStep.Index)
            && definitions.TryGetValue(unfinishedStep.Index, out JournalStep? running))
        {
            steps[unfinishedStep.Index] = PlannedStep(running);
        }

        return steps.Values.ToArray();
    }

    private static SummaryStep PlannedStep(JournalStep definition) => new()
    {
        Index = definition.Number,
        Name = definition.Name,
        Pattern = definition.Pattern.ToString(),
        AccessOrder = definition.AccessOrder.ToString(),
        ThreadMode = definition.ThreadMode.ToString(),
        HoldSeconds = Seconds(definition.HoldMs),
        PrimaryMechanism = definition.PrimaryMechanism.ToString(),
        PlannedSeconds = Seconds(definition.TimeBudgetMs),
        PlannedCoveragePercent = definition.CoverageBudgetPercent,
    };

    private static SummaryErrors BuildErrors(
        RunSummarySource source,
        JournalResult? result,
        JournalBeat? lastBeat,
        IReadOnlyList<JournalStepEnd> finishedSteps)
    {
        IReadOnlyList<ErrorRecord> stored = source.StoredErrors;

        // Group validates the records, rejecting a null element before any is mapped.
        IReadOnlyList<ErrorGroup> groups = ErrorGrouping.Group(stored);

        ulong total = result?.TotalErrors ?? Math.Max(lastBeat?.Errors ?? 0, SumErrors(finishedSteps));
        var groupItems = new SummaryErrorGroup[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            groupItems[i] = ToSummaryGroup(groups[i]);
        }

        var recordItems = new SummaryErrorRecord[stored.Count];
        for (int i = 0; i < stored.Count; i++)
        {
            recordItems[i] = ToSummaryRecord(stored[i]);
        }

        return new SummaryErrors
        {
            Total = total,
            Truncated = total > (ulong)stored.Count && stored.Count >= ErrorGrouping.StoredDetailCap,
            DetailAvailable = source.ErrorDetailAvailable,
            StoredCount = stored.Count,
            StoredCap = ErrorGrouping.StoredDetailCap,
            StoredDistinctAddresses = ErrorGrouping.CountDistinctAddresses(stored),
            Groups = groupItems,
            Records = recordItems,
        };
    }

    private static ulong SumErrors(IReadOnlyList<JournalStepEnd> finishedSteps)
    {
        ulong sum = 0;
        foreach (JournalStepEnd step in finishedSteps)
        {
            sum += step.Errors;
        }

        return sum;
    }

    private static SummaryErrorGroup ToSummaryGroup(ErrorGroup group) => new()
    {
        WordAddress = SummaryFormat.Hex64(group.WordAddress),
        Count = group.Count,
        BitMaskUnion = SummaryFormat.Hex64(group.BitMaskUnion),
        FirstStepName = group.FirstStepName,
        FirstTimeIntoTest = group.FirstTimeIntoTest.TotalSeconds,
        LastTimeIntoTest = group.LastTimeIntoTest.TotalSeconds,
    };

    // An error record keeps Core's member names and its 0-based step and pass indices, so a reader maps it
    // straight back to the plan; the step's 1-based steps[] number is stepIndex + 1.
    private static SummaryErrorRecord ToSummaryRecord(ErrorRecord record) => new()
    {
        Sequence = record.Sequence,
        Address = SummaryFormat.Hex64(record.Address),
        AddressKind = record.AddressKind.ToString(),
        RegionOffset = record.RegionOffset,
        Expected = SummaryFormat.Hex64(record.Expected),
        Actual = SummaryFormat.Hex64(record.Actual),
        Reread = SummaryFormat.Hex64(record.Reread),
        BitMask = SummaryFormat.Hex64(record.BitMask),
        BitPositions = record.BitPositions,
        RereadOutcome = record.RereadOutcome.ToString(),
        StepIndex = record.StepIndex,
        StepName = record.StepName,
        PassIndex = record.PassIndex,
        AccessType = record.AccessType.ToString(),
        TimeSinceWrite = record.TimeSinceWrite.TotalSeconds,
        TimeIntoTest = record.TimeIntoTest.TotalSeconds,
        WorkerIndex = record.WorkerIndex,
        LogicalProcessor = record.LogicalProcessor,
        ProcessorGroup = record.ProcessorGroup,
        ActiveWorkers = record.ActiveWorkers,
    };

    // A record that omits a timestamp reads it back as default(DateTimeOffset), 0001-01-01T00:00Z, which no
    // clock produces; the summary reports such a stamp as not captured.
    private static DateTimeOffset? CapturedUtc(DateTimeOffset? stamp) =>
        stamp is { } value && value != default ? value : null;

    // Persisted durations are whole milliseconds and never negative.
    private static double Seconds(long milliseconds) => Math.Max(0L, milliseconds) / 1000.0;

    // UTC stamps come from the wall clock, which a time correction can step backwards; a negative interval is
    // reported as 0 rather than as a duration that cannot exist.
    private static double? SecondsBetween(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } start && to is { } end ? Math.Max(0.0, (end - start).TotalSeconds) : null;

    private static double? Percent(ulong part, ulong? whole) =>
        whole is { } total && total > 0 ? 100.0 * part / total : null;

    // System.Text.Json refuses to write NaN or an infinity, so no non-finite value may reach a summary.
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    // A defined member's name; an undefined value is what a record that omitted the field reads back as.
    private static string? DefinedNameOrNull<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value.ToString() : null;
}

/// <summary>
/// The product and build that ran the test, the summary's <c>app</c> object.
/// </summary>
public sealed record SummaryApp
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _name = string.Empty;
    private readonly string _version = string.Empty;

    /// <summary>
    /// Gets the product name, <c>"RAMQuick"</c>.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the product build version that ran the test, as the <c>begin</c> record captured it.
    /// </summary>
    public string Version
    {
        get => _version;
        init => _version = value ?? string.Empty;
    }
}

/// <summary>
/// The run's identity, outcome and times, the summary's <c>run</c> object.
/// </summary>
public sealed record SummaryRun
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _id = string.Empty;
    private readonly string _testType = string.Empty;
    private readonly string _verdict = string.Empty;
    private readonly string _endReason = string.Empty;

    /// <summary>
    /// Gets the run id: the run GUID in its 32-hex-digit <c>N</c> form, which also ends the results file
    /// base name.
    /// </summary>
    public string Id
    {
        get => _id;
        init => _id = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the test type name, for example <c>"Quick"</c>.
    /// </summary>
    public string TestType
    {
        get => _testType;
        init => _testType = value ?? string.Empty;
    }

    /// <summary>
    /// Gets a value indicating whether Stop on error was on.
    /// </summary>
    public bool StopOnError { get; init; }

    /// <summary>
    /// Gets the verdict: <c>"PASS"</c>, <c>"FAIL"</c> or <c>"ABORTED"</c>.
    /// </summary>
    public string Verdict
    {
        get => _verdict;
        init => _verdict = value ?? string.Empty;
    }

    /// <summary>
    /// Gets why the run ended: a <see cref="RunEndReason"/> member name.
    /// </summary>
    public string EndReason
    {
        get => _endReason;
        init => _endReason = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the end reason's diagnostic detail, or <see langword="null"/> when there is none. For an
    /// unfinished run it states where the run stopped and which times are approximate or unknown.
    /// </summary>
    public string? EndDetail { get; init; }

    /// <summary>
    /// Gets the UTC time Start was accepted, the origin of <see cref="TimeToVerdictSeconds"/>, or
    /// <see langword="null"/> when the journal did not capture it.
    /// </summary>
    public DateTimeOffset? StartRequestedUtc { get; init; }

    /// <summary>
    /// Gets the UTC time the run entered Running, or <see langword="null"/> when preparation never finished
    /// or the journal did not capture the time.
    /// </summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>
    /// Gets the UTC time the engine reported Completed, or <see langword="null"/> when the journal did not
    /// capture it. For an unfinished run it is the last heartbeat's time and approximate;
    /// <see langword="null"/> when no heartbeat was written or the last one captured no time.
    /// </summary>
    public DateTimeOffset? EndedUtc { get; init; }

    /// <summary>
    /// Gets the UTC time the terminal state was shown, or <see langword="null"/> when the run never reached
    /// a verdict or the journal did not capture the time.
    /// </summary>
    public DateTimeOffset? VerdictUtc { get; init; }

    /// <summary>
    /// Gets the engine's measured test time from entering Running to Completed, in seconds: its monotonic
    /// elapsed time, which no wall-clock correction skews, and 0 when preparation failed. For an unfinished
    /// run it is the last heartbeat's elapsed time and approximate; <see langword="null"/> when no
    /// heartbeat was written.
    /// </summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// Gets the time from Start accepted to the verdict shown, in seconds, or <see langword="null"/> when
    /// there was no verdict or the journal did not capture either time. This is the fail-fast acceptance
    /// measure.
    /// </summary>
    public double? TimeToVerdictSeconds { get; init; }

    /// <summary>
    /// Gets the time into the test of the first error, in seconds, or <see langword="null"/> when there was
    /// no error or the run never reached a verdict.
    /// </summary>
    public double? FirstErrorSeconds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the summary was rebuilt from a progress journal at a later launch.
    /// </summary>
    public bool Recovered { get; init; }

    /// <summary>
    /// Gets a value indicating whether the summary is partial because the run never finished.
    /// </summary>
    public bool Partial { get; init; }
}

/// <summary>
/// The machine context captured at Start, the summary's <c>system</c> object. A value the run never captured
/// is <see langword="null"/>.
/// </summary>
public sealed record SummarySystem
{
    /// <summary>
    /// Gets the operating-system description.
    /// </summary>
    public string? Os { get; init; }

    /// <summary>
    /// Gets the CPU brand string.
    /// </summary>
    public string? Cpu { get; init; }

    /// <summary>
    /// Gets the number of logical processors across all processor groups.
    /// </summary>
    public int? LogicalProcessors { get; init; }

    /// <summary>
    /// Gets the number of processor groups.
    /// </summary>
    public int? ProcessorGroups { get; init; }

    /// <summary>
    /// Gets the instruction set of the test kernels: <c>"AVX-512"</c>, <c>"AVX2"</c> or <c>"SSE2"</c>. It
    /// is the set the session selected, or the best available set when preparation never finished.
    /// </summary>
    public string? InstructionSet { get; init; }

    /// <summary>
    /// Gets a value indicating whether the reduced SSE2 path ran, which makes the test slower.
    /// </summary>
    public bool? ReducedInstructionSet { get; init; }
}

/// <summary>
/// The capacities and detected memory configuration, the summary's <c>memory</c> object.
/// </summary>
public sealed record SummaryMemory
{
    // See RunSummary: the init accessor keeps the type non-null when a document omits it.
    private readonly string _type = nameof(MemoryType.Unknown);

    /// <summary>
    /// Gets the installed memory from SMBIOS, in bytes, or <see langword="null"/> when unknown.
    /// </summary>
    public ulong? InstalledBytes { get; init; }

    /// <summary>
    /// Gets the physical memory available at Start, in bytes, or <see langword="null"/> when the journal did
    /// not capture it.
    /// </summary>
    public ulong? AvailableBytesAtStart { get; init; }

    /// <summary>
    /// Gets the region size planned before locking, in bytes, or <see langword="null"/> when the journal did
    /// not capture it.
    /// </summary>
    public ulong? PlannedBytes { get; init; }

    /// <summary>
    /// Gets the bytes actually locked and tested, or <see langword="null"/> when preparation never finished
    /// or the journal did not capture the size.
    /// </summary>
    public ulong? TestedBytes { get; init; }

    /// <summary>
    /// Gets the time the engine took to release the region, in seconds, or <see langword="null"/> when the
    /// release was not confirmed before the summary was published.
    /// </summary>
    public double? ReleaseSeconds { get; init; }

    /// <summary>
    /// Gets the pages backing the region: <c>"Large"</c>, <c>"Standard"</c> or <c>"Mixed"</c>.
    /// </summary>
    public string? PageMode { get; init; }

    /// <summary>
    /// Gets the kind of address the error records carry; <c>"Virtual"</c> in v1.
    /// </summary>
    public string? AddressKind { get; init; }

    /// <summary>
    /// Gets the DRAM generation: <c>"DDR4"</c>, <c>"DDR5"</c>, <c>"Other"</c> or <c>"Unknown"</c>.
    /// </summary>
    public string Type
    {
        get => _type;
        init => _type = value ?? nameof(MemoryType.Unknown);
    }

    /// <summary>
    /// Gets the configured memory speed in MT/s, or <see langword="null"/> when unknown.
    /// </summary>
    public int? SpeedMTs { get; init; }

    /// <summary>
    /// Gets the number of memory channels, or <see langword="null"/> when unknown.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Gets the number of populated DIMMs, or <see langword="null"/> when the journal did not capture it.
    /// </summary>
    public int? Dimms { get; init; }
}

/// <summary>
/// The plan's targets, the summary's <c>plan</c> object.
/// </summary>
public sealed record SummaryPlan
{
    /// <summary>
    /// Gets the plan's target duration in seconds: the sum of its steps' time budgets.
    /// </summary>
    public double TargetSeconds { get; init; }

    /// <summary>
    /// Gets the plan's coverage target as a percentage: the sum of its steps' coverage budgets.
    /// </summary>
    public double TargetCoveragePercent { get; init; }
}


/// <summary>
/// One started step, its plan beside its actuals: an element of the summary's <c>steps</c> array.
/// </summary>
public sealed record SummaryStep
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _name = string.Empty;
    private readonly string _pattern = string.Empty;
    private readonly string _accessOrder = string.Empty;
    private readonly string _threadMode = string.Empty;
    private readonly string _primaryMechanism = string.Empty;

    /// <summary>
    /// Gets the step's 1-based number in its plan.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets the step's name, exactly as the plan defines it.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the data pattern: a <see cref="PatternKind"/> member name.
    /// </summary>
    public string Pattern
    {
        get => _pattern;
        init => _pattern = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the access order: an <see cref="Core.AccessOrder"/> member name.
    /// </summary>
    public string AccessOrder
    {
        get => _accessOrder;
        init => _accessOrder = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the thread mode: a <see cref="Core.ThreadMode"/> member name.
    /// </summary>
    public string ThreadMode
    {
        get => _threadMode;
        init => _threadMode = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the idle hold between writing and verifying, in seconds; 0 for a windowed step.
    /// </summary>
    public double HoldSeconds { get; init; }

    /// <summary>
    /// Gets the mechanism the step chiefly targets: an <see cref="InstabilityMechanism"/> member name.
    /// </summary>
    public string PrimaryMechanism
    {
        get => _primaryMechanism;
        init => _primaryMechanism = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the step's time budget, in seconds.
    /// </summary>
    public double PlannedSeconds { get; init; }

    /// <summary>
    /// Gets the step's coverage budget, as a percentage of the tested bytes.
    /// </summary>
    public double PlannedCoveragePercent { get; init; }

    /// <summary>
    /// Gets the time the step actually ran, in seconds, or <see langword="null"/> when it never finished.
    /// </summary>
    public double? ActualSeconds { get; init; }

    /// <summary>
    /// Gets how far the step ran past its time budget, in seconds, or <see langword="null"/> when it never
    /// finished.
    /// </summary>
    public double? OverrunSeconds { get; init; }

    /// <summary>
    /// Gets the number of passes the step finished, or <see langword="null"/> when it never finished.
    /// </summary>
    public int? Passes { get; init; }

    /// <summary>
    /// Gets the step's verified bytes as a percentage of the tested bytes, or <see langword="null"/> when
    /// the step never finished or the tested bytes are unknown.
    /// </summary>
    public double? CoveragePercent { get; init; }

    /// <summary>
    /// Gets the true number of errors the step detected, from the engine's counters, or
    /// <see langword="null"/> when the step never finished.
    /// </summary>
    public ulong? Errors { get; init; }

    /// <summary>
    /// Gets a value indicating whether the step ran to the end of its schedule.
    /// </summary>
    public bool Completed { get; init; }
}

/// <summary>
/// The coverage outcome, the summary's <c>coverage</c> object. Coverage is verified bytes divided by tested
/// bytes, as a percentage.
/// </summary>
public sealed record SummaryCoverage
{
    /// <summary>
    /// Gets the coverage the run reached, as a percentage of the tested bytes.
    /// </summary>
    public double Percent { get; init; }

    /// <summary>
    /// Gets the plan's coverage target, as a percentage.
    /// </summary>
    public double TargetPercent { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Percent"/> reached <see cref="TargetPercent"/>.
    /// </summary>
    public bool TargetReached { get; init; }
}

/// <summary>
/// The peak fresh temperatures and the worst status seen per sensor, the summary's <c>temperatures</c>
/// object.
/// </summary>
/// <remarks>
/// <para>
/// Statuses are <c>RamQuick.Sensors.SensorStatus</c> member names, or <c>"Unknown"</c> while no reading has
/// been seen. From least to most severe they rank <c>Unknown</c>, <c>Available</c>, <c>Stale</c>,
/// <c>NoSensor</c>, <c>DriverUnavailable</c>, <c>Failed</c>, <c>Hung</c>; an unrecognised status ranks, and
/// is recorded, as <c>Failed</c>.
/// </para>
/// <para>
/// Start from <see cref="Initial"/> and fold every reading through <see cref="Merge"/>.
/// </para>
/// </remarks>
public sealed record SummaryTemperatures
{
    private const string FailedStatus = "Failed";

    // Lowest to highest severity; the index is the rank.
    private static readonly string[] _statusesBySeverity =
    {
        JournalReadings.UnknownStatus,
        "Available",
        "Stale",
        "NoSensor",
        "DriverUnavailable",
        FailedStatus,
        "Hung",
    };

    // See RunSummary: the init accessors keep the statuses non-null when a document omits them.
    private readonly string _cpuStatus = JournalReadings.UnknownStatus;
    private readonly string _ramStatus = JournalReadings.UnknownStatus;

    /// <summary>
    /// Gets the starting value before any reading: no temperatures, and <c>"Unknown"</c> for both statuses.
    /// </summary>
    public static SummaryTemperatures Initial { get; } = new();

    /// <summary>
    /// Gets the highest fresh CPU reading in degrees Celsius, or <see langword="null"/> when none was
    /// available.
    /// </summary>
    public double? CpuMaxC { get; init; }

    /// <summary>
    /// Gets the highest fresh RAM reading (the maximum across DIMMs) in degrees Celsius, or
    /// <see langword="null"/> when none was available.
    /// </summary>
    public double? RamMaxC { get; init; }

    /// <summary>
    /// Gets the worst CPU sensor status seen.
    /// </summary>
    public string CpuStatus
    {
        get => _cpuStatus;
        init => _cpuStatus = value ?? JournalReadings.UnknownStatus;
    }

    /// <summary>
    /// Gets the worst RAM sensor status seen.
    /// </summary>
    public string RamStatus
    {
        get => _ramStatus;
        init => _ramStatus = value ?? JournalReadings.UnknownStatus;
    }

    /// <summary>
    /// Folds one pair of readings into the peaks and worst statuses.
    /// </summary>
    /// <param name="cpuCelsius">
    /// The CPU temperature, passed only when its reading is available and fresh; otherwise
    /// <see langword="null"/>. A non-finite value is ignored.
    /// </param>
    /// <param name="cpuStatus">The CPU reading's status name.</param>
    /// <param name="ramCelsius">
    /// The RAM temperature, passed only when its reading is available and fresh; otherwise
    /// <see langword="null"/>. A non-finite value is ignored.
    /// </param>
    /// <param name="ramStatus">The RAM reading's status name.</param>
    /// <returns>
    /// A new instance holding, per sensor, the maximum of the peak and the new temperature and the more
    /// severe of the two statuses.
    /// </returns>
    /// <exception cref="ArgumentNullException">A status is <see langword="null"/>.</exception>
    public SummaryTemperatures Merge(double? cpuCelsius, string cpuStatus, double? ramCelsius, string ramStatus)
    {
        ArgumentNullException.ThrowIfNull(cpuStatus);
        ArgumentNullException.ThrowIfNull(ramStatus);
        return new SummaryTemperatures
        {
            CpuMaxC = Peak(CpuMaxC, cpuCelsius),
            RamMaxC = Peak(RamMaxC, ramCelsius),
            CpuStatus = MoreSevere(CpuStatus, cpuStatus),
            RamStatus = MoreSevere(RamStatus, ramStatus),
        };
    }

    private static double? Peak(double? peak, double? reading)
    {
        if (reading is not { } value || !double.IsFinite(value))
        {
            return peak;
        }

        return peak is { } current && current >= value ? current : value;
    }

    private static string MoreSevere(string current, string reported)
    {
        string recorded = Recognised(current);
        string candidate = Recognised(reported);
        return Severity(candidate) > Severity(recorded) ? candidate : recorded;
    }

    private static string Recognised(string status) =>
        Array.IndexOf(_statusesBySeverity, status) >= 0 ? status : FailedStatus;

    private static int Severity(string status) => Array.IndexOf(_statusesBySeverity, status);
}


/// <summary>
/// The true error total and the stored error detail, the summary's <c>errors</c> object.
/// </summary>
/// <remarks>
/// <see cref="Total"/> comes from the engine's counters and is never derived from stored records. Every
/// <c>stored*</c> member, <see cref="Groups"/> and <see cref="Records"/> describe the stored records only, at
/// most <see cref="ErrorGrouping.StoredDetailCap"/> per run, so once the cap is reached they no longer add up
/// to the total.
/// </remarks>
public sealed record SummaryErrors
{
    // See RunSummary: the init accessors keep the lists non-null when a document omits them.
    private readonly IReadOnlyList<SummaryErrorGroup> _groups = Array.Empty<SummaryErrorGroup>();
    private readonly IReadOnlyList<SummaryErrorRecord> _records = Array.Empty<SummaryErrorRecord>();

    /// <summary>
    /// Gets the true error total, from the engine's counters.
    /// </summary>
    public ulong Total { get; init; }

    /// <summary>
    /// Gets a value indicating whether the stored-detail cap was reached while more errors were counted.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Gets a value indicating whether stored error detail is present; <see langword="false"/> for a
    /// summary rebuilt from a progress journal, which holds no error records.
    /// </summary>
    public bool DetailAvailable { get; init; }

    /// <summary>
    /// Gets the number of stored error records.
    /// </summary>
    public int StoredCount { get; init; }

    /// <summary>
    /// Gets the cap on stored error records, <see cref="ErrorGrouping.StoredDetailCap"/>.
    /// </summary>
    public int StoredCap { get; init; }

    /// <summary>
    /// Gets the number of distinct 8-byte word addresses among the stored records.
    /// </summary>
    public int StoredDistinctAddresses { get; init; }

    /// <summary>
    /// Gets every group of stored records sharing a word address, largest first.
    /// </summary>
    public IReadOnlyList<SummaryErrorGroup> Groups
    {
        get => _groups;
        init => _groups = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets the stored error records, in the order they were stored.
    /// </summary>
    public IReadOnlyList<SummaryErrorRecord> Records
    {
        get => _records;
        init => _records = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing <see cref="Groups"/> and
    /// <see cref="Records"/> element by element.
    /// </summary>
    /// <param name="other">The section to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(SummaryErrors? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Total == other.Total
            && Truncated == other.Truncated
            && DetailAvailable == other.DetailAvailable
            && StoredCount == other.StoredCount
            && StoredCap == other.StoredCap
            && StoredDistinctAddresses == other.StoredDistinctAddresses
            && Groups.SequenceEqual(other.Groups)
            && Records.SequenceEqual(other.Records));

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(SummaryErrors)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Total);
        hash.Add(Truncated);
        hash.Add(DetailAvailable);
        hash.Add(StoredCount);
        hash.Add(StoredCap);
        hash.Add(StoredDistinctAddresses);
        foreach (SummaryErrorGroup group in Groups)
        {
            hash.Add(group);
        }

        foreach (SummaryErrorRecord record in Records)
        {
            hash.Add(record);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The stored records that hit one 8-byte word: an element of the summary's <c>errors.groups</c> array.
/// </summary>
public sealed record SummaryErrorGroup
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _wordAddress = string.Empty;
    private readonly string _bitMaskUnion = string.Empty;
    private readonly string _firstStepName = string.Empty;

    /// <summary>
    /// Gets the word's virtual address, as <see cref="SummaryFormat.Hex64"/> text.
    /// </summary>
    public string WordAddress
    {
        get => _wordAddress;
        init => _wordAddress = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the number of stored records that hit the word.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Gets every bit that flipped at the word in at least one stored record, as
    /// <see cref="SummaryFormat.Hex64"/> text.
    /// </summary>
    public string BitMaskUnion
    {
        get => _bitMaskUnion;
        init => _bitMaskUnion = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the name of the step that found the word's earliest stored error.
    /// </summary>
    public string FirstStepName
    {
        get => _firstStepName;
        init => _firstStepName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the time into the test of the word's earliest stored error, in seconds:
    /// <see cref="ErrorGroup.FirstTimeIntoTest"/> under the same name, as a number.
    /// </summary>
    public double FirstTimeIntoTest { get; init; }

    /// <summary>
    /// Gets the time into the test of the word's latest stored error, in seconds:
    /// <see cref="ErrorGroup.LastTimeIntoTest"/> under the same name, as a number.
    /// </summary>
    public double LastTimeIntoTest { get; init; }
}

/// <summary>
/// One stored error record: an element of the summary's <c>errors.records</c> array.
/// </summary>
/// <remarks>
/// It carries every <see cref="ErrorRecord"/> member except the grouping key
/// <see cref="ErrorRecord.WordAddress"/> under the same name, so its camelCase JSON names are Core's. Raw
/// 64-bit values are <see cref="SummaryFormat.Hex64"/> text, durations are numbers of seconds, step and pass
/// indices keep Core's 0-based values, and worker and processor identifiers are the engine's raw values.
/// </remarks>
public sealed record SummaryErrorRecord
{
    // See RunSummary: the init accessors keep the values non-null when a document omits them.
    private readonly string _address = string.Empty;
    private readonly string _addressKind = string.Empty;
    private readonly string _expected = string.Empty;
    private readonly string _actual = string.Empty;
    private readonly string _reread = string.Empty;
    private readonly string _bitMask = string.Empty;
    private readonly IReadOnlyList<int> _bitPositions = Array.Empty<int>();
    private readonly string _rereadOutcome = string.Empty;
    private readonly string _stepName = string.Empty;
    private readonly string _accessType = string.Empty;

    /// <summary>
    /// Gets the engine's record number within the run, in confirmation order from 0.
    /// </summary>
    public ulong Sequence { get; init; }

    /// <summary>
    /// Gets the address of the mismatching word, of kind <see cref="AddressKind"/>.
    /// </summary>
    public string Address
    {
        get => _address;
        init => _address = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the kind of <see cref="Address"/>: <c>"Virtual"</c> in v1.
    /// </summary>
    public string AddressKind
    {
        get => _addressKind;
        init => _addressKind = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the word's byte offset within the tested region.
    /// </summary>
    public ulong RegionOffset { get; init; }

    /// <summary>
    /// Gets the value the word should hold.
    /// </summary>
    public string Expected
    {
        get => _expected;
        init => _expected = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the value the verification load returned.
    /// </summary>
    public string Actual
    {
        get => _actual;
        init => _actual = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the value of the single cache-flushed re-read.
    /// </summary>
    public string Reread
    {
        get => _reread;
        init => _reread = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the flipped bits, expected XOR actual.
    /// </summary>
    public string BitMask
    {
        get => _bitMask;
        init => _bitMask = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the indices, 0 to 63 in ascending order, of the flipped bits.
    /// </summary>
    public IReadOnlyList<int> BitPositions
    {
        get => _bitPositions;
        init => _bitPositions = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets how the re-read compared with the expected and first-read values: a
    /// <see cref="Core.RereadOutcome"/> member name. It supports an inference, never an established fault
    /// location.
    /// </summary>
    public string RereadOutcome
    {
        get => _rereadOutcome;
        init => _rereadOutcome = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the 0-based index, in the plan's step array, of the step that found the error:
    /// <see cref="ErrorRecord.StepIndex"/> unchanged. The step's <c>steps[]</c> entry has
    /// <see cref="SummaryStep.Index"/> <c>stepIndex + 1</c>.
    /// </summary>
    public int StepIndex { get; init; }

    /// <summary>
    /// Gets the name of the step that found the error.
    /// </summary>
    public string StepName
    {
        get => _stepName;
        init => _stepName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the 0-based index, within the step, of the pass that found the error:
    /// <see cref="ErrorRecord.PassIndex"/> unchanged.
    /// </summary>
    public int PassIndex { get; init; }

    /// <summary>
    /// Gets the verification access that found the mismatch: a <see cref="Core.AccessType"/> member name.
    /// </summary>
    public string AccessType
    {
        get => _accessType;
        init => _accessType = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the time from the last write of the word's range to the verification load, in seconds:
    /// <see cref="ErrorRecord.TimeSinceWrite"/> under the same name, as a number.
    /// </summary>
    public double TimeSinceWrite { get; init; }

    /// <summary>
    /// Gets the test time at which the error was confirmed, in seconds from entering Running:
    /// <see cref="ErrorRecord.TimeIntoTest"/> under the same name, as a number.
    /// </summary>
    public double TimeIntoTest { get; init; }

    /// <summary>
    /// Gets the engine's index of the worker that verified the word.
    /// </summary>
    public int WorkerIndex { get; init; }

    /// <summary>
    /// Gets the worker's logical processor number within its processor group.
    /// </summary>
    public int LogicalProcessor { get; init; }

    /// <summary>
    /// Gets the processor group of the worker's logical processor.
    /// </summary>
    public int ProcessorGroup { get; init; }

    /// <summary>
    /// Gets the number of workers active in the step.
    /// </summary>
    public int ActiveWorkers { get; init; }

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing <see cref="BitPositions"/>
    /// element by element.
    /// </summary>
    /// <param name="other">The record to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(SummaryErrorRecord? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Sequence == other.Sequence
            && Address == other.Address
            && AddressKind == other.AddressKind
            && RegionOffset == other.RegionOffset
            && Expected == other.Expected
            && Actual == other.Actual
            && Reread == other.Reread
            && BitMask == other.BitMask
            && BitPositions.SequenceEqual(other.BitPositions)
            && RereadOutcome == other.RereadOutcome
            && StepIndex == other.StepIndex
            && StepName == other.StepName
            && PassIndex == other.PassIndex
            && AccessType == other.AccessType
            && TimeSinceWrite.Equals(other.TimeSinceWrite)
            && TimeIntoTest.Equals(other.TimeIntoTest)
            && WorkerIndex == other.WorkerIndex
            && LogicalProcessor == other.LogicalProcessor
            && ProcessorGroup == other.ProcessorGroup
            && ActiveWorkers == other.ActiveWorkers);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(SummaryErrorRecord)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Sequence);
        hash.Add(Address);
        hash.Add(AddressKind);
        hash.Add(RegionOffset);
        hash.Add(Expected);
        hash.Add(Actual);
        hash.Add(Reread);
        hash.Add(BitMask);
        foreach (int position in BitPositions)
        {
            hash.Add(position);
        }

        hash.Add(RereadOutcome);
        hash.Add(StepIndex);
        hash.Add(StepName);
        hash.Add(PassIndex);
        hash.Add(AccessType);
        hash.Add(TimeSinceWrite);
        hash.Add(TimeIntoTest);
        hash.Add(WorkerIndex);
        hash.Add(LogicalProcessor);
        hash.Add(ProcessorGroup);
        hash.Add(ActiveWorkers);
        return hash.ToHashCode();
    }
}


/// <summary>
/// The ranked cause hints, the summary's <c>causeHints</c> object.
/// </summary>
/// <remarks>
/// Hints are always labelled as hints: <see cref="Label"/> is the constant <see cref="HintLabel"/> on every
/// instance. It is neither settable nor stored, so no object initializer, <see langword="with"/> expression
/// or deserialized document can give a summary another label; System.Text.Json writes it as
/// <c>causeHints.label</c> and ignores the member when reading.
/// </remarks>
public sealed record SummaryCauseHints
{
    /// <summary>
    /// The label every cause-hint list carries, <c>"Hint — not a diagnosis"</c> with an em dash (U+2014). It
    /// equals <c>RamQuick.Analysis.CauseHints.HintLabel</c>, which this library cannot reference, so normal
    /// and recovered summaries alike carry the analyzer's label.
    /// </summary>
    public const string HintLabel = "Hint \u2014 not a diagnosis";

    // See RunSummary: the init accessor keeps the list non-null when a document omits it.
    private readonly IReadOnlyList<SummaryCauseHint> _items = Array.Empty<SummaryCauseHint>();

    /// <summary>
    /// Gets the labelled list with no items, used when no error detail was analysed.
    /// </summary>
    public static SummaryCauseHints Empty { get; } = new();

    /// <summary>
    /// Gets the label written above the hints: always <see cref="HintLabel"/>, on every instance,
    /// <see cref="Empty"/> included.
    /// </summary>
    public string Label => HintLabel;

    /// <summary>
    /// Gets the hints in rank order, most likely first; empty when no error detail was analysed.
    /// </summary>
    public IReadOnlyList<SummaryCauseHint> Items
    {
        get => _items;
        init => _items = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same items, compared element by element;
    /// <see cref="Label"/> is the same constant on every instance.
    /// </summary>
    /// <param name="other">The section to compare with.</param>
    /// <returns><see langword="true"/> when every item is equal.</returns>
    public bool Equals(SummaryCauseHints? other) =>
        ReferenceEquals(this, other)
        || (other is not null && Items.SequenceEqual(other.Items));

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(SummaryCauseHints)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (SummaryCauseHint item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// One ranked probable cause: an element of the summary's <c>causeHints.items</c> array. It is a hint, never
/// a diagnosis.
/// </summary>
public sealed record SummaryCauseHint
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _category = string.Empty;
    private readonly string _suggestion = string.Empty;
    private readonly string _evidence = string.Empty;

    /// <summary>
    /// Gets the hint's 1-based rank, 1 being the most likely.
    /// </summary>
    public int Rank { get; init; }

    /// <summary>
    /// Gets the cause category's display title.
    /// </summary>
    public string Category
    {
        get => _category;
        init => _category = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the category's share of the kept scores, as a percentage.
    /// </summary>
    public double LikelihoodPercent { get; init; }

    /// <summary>
    /// Gets the plain-language suggestion for the category.
    /// </summary>
    public string Suggestion
    {
        get => _suggestion;
        init => _suggestion = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the evidence line behind the hint.
    /// </summary>
    public string Evidence
    {
        get => _evidence;
        init => _evidence = value ?? string.Empty;
    }
}

/// <summary>
/// Where a run that never reached a verdict stopped, the summary's <c>unfinishedRun</c> object. Present only
/// in summaries rebuilt from a journal without a <c>result</c> record.
/// </summary>
public sealed record SummaryUnfinishedRun
{
    /// <summary>
    /// Gets the 1-based number of the step that was running, else of the last step that finished, or
    /// <see langword="null"/> when the run stopped while preparing memory, before step 1.
    /// </summary>
    public int? LastStepIndex { get; init; }

    /// <summary>
    /// Gets the name of the step at <see cref="LastStepIndex"/>, or <see langword="null"/> when there is
    /// none.
    /// </summary>
    public string? LastStepName { get; init; }

    /// <summary>
    /// Gets the number of steps in the plan.
    /// </summary>
    public int StepCount { get; init; }

    /// <summary>
    /// Gets the UTC time of the last heartbeat, or <see langword="null"/> when none was written or it did
    /// not capture its time.
    /// </summary>
    public DateTimeOffset? LastHeartbeatUtc { get; init; }

    /// <summary>
    /// Gets the coverage at the last heartbeat, as a percentage, or <see langword="null"/> when none was
    /// written.
    /// </summary>
    public double? LastCoveragePercent { get; init; }
}

/// <summary>
/// The value formats the summaries share.
/// </summary>
public static class SummaryFormat
{
    private const string HexPrefix = "0x";

    /// <summary>
    /// Formats a 64-bit value as <c>"0x"</c> plus 16 uppercase hex digits, for example
    /// <c>"0x0000023A4F7C1D48"</c>.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The 18-character text.</returns>
    public static string Hex64(ulong value) => HexPrefix + value.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses hex text written by <see cref="Hex64"/>. A <c>0x</c> or <c>0X</c> prefix is optional, and
    /// either digit case is accepted.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="value">The parsed value, or 0 when parsing fails.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="text"/> holds 1 to 16 significant hex digits and nothing
    /// else; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParseHex64(string? text, out ulong value)
    {
        ReadOnlySpan<char> digits = text.AsSpan();
        if (digits.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[HexPrefix.Length..];
        }

        return ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Returns the summary name of an instruction set.
    /// </summary>
    /// <param name="isa">The instruction set.</param>
    /// <returns><c>"AVX-512"</c>, <c>"AVX2"</c> or <c>"SSE2"</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="isa"/> is not a defined <see cref="InstructionSet"/> value.
    /// </exception>
    public static string InstructionSetName(InstructionSet isa) => isa switch
    {
        InstructionSet.Avx512 => "AVX-512",
        InstructionSet.Avx2 => "AVX2",
        InstructionSet.Sse2 => "SSE2",
        _ => throw new ArgumentOutOfRangeException(nameof(isa), isa, "The instruction set is not a defined value."),
    };

    /// <summary>
    /// Formats a run id in its 32-hex-digit <c>N</c> form, as used in the summary and the results file base
    /// name.
    /// </summary>
    /// <param name="id">The run id.</param>
    /// <returns>The 32 lowercase hex digits.</returns>
    public static string RunId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// The inputs of <see cref="RunSummary.Build"/>: a run's journal records and the sections its caller
/// supplies.
/// </summary>
/// <remarks>
/// <para>
/// A normal run passes every record it journalled, <see cref="ErrorDetailAvailable"/>
/// <see langword="true"/>, its stored error records (at most <see cref="ErrorGrouping.StoredDetailCap"/>),
/// the temperatures it folded from every reading, and the analyzer's ranked items as its cause hints. Every
/// <see cref="SummaryCauseHints"/> carries the constant label <see cref="SummaryCauseHints.HintLabel"/>, so
/// no caller supplies one.
/// </para>
/// <para>
/// Recovery passes the records read back from the journal, no error detail, the temperatures folded from
/// every heartbeat, <see cref="SummaryCauseHints.Empty"/> and <see cref="Recovered"/>
/// <see langword="true"/>; <see cref="Partial"/> is <see langword="true"/> when there is no
/// <see cref="Result"/>.
/// </para>
/// </remarks>
public sealed record RunSummarySource
{
    /// <summary>
    /// The product name written as <c>app.name</c> unless the caller supplies another.
    /// </summary>
    public const string DefaultAppName = "RAMQuick";

    private readonly IReadOnlyList<JournalStepEnd> _finishedSteps = Array.Empty<JournalStepEnd>();
    private readonly IReadOnlyList<ErrorRecord> _storedErrors = Array.Empty<ErrorRecord>();
    private readonly SummaryTemperatures _temperatures = SummaryTemperatures.Initial;
    private readonly SummaryCauseHints _causeHints = SummaryCauseHints.Empty;
    private readonly string _appName = DefaultAppName;

    /// <summary>
    /// Gets the run's <c>begin</c> record: its identity, plan and captured machine state.
    /// </summary>
    public required JournalBegin Begin { get; init; }

    /// <summary>
    /// Gets the <c>prepared</c> record, or <see langword="null"/> when preparation never finished.
    /// </summary>
    public JournalPrepared? Prepared { get; init; }

    /// <summary>
    /// Gets the <c>stepEnd</c> records, in any order.
    /// </summary>
    public IReadOnlyList<JournalStepEnd> FinishedSteps
    {
        get => _finishedSteps;
        init => _finishedSteps = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets the <c>step</c> record of the step that was running when an unfinished run stopped, or
    /// <see langword="null"/>. Ignored when a <c>stepEnd</c> exists for the same step.
    /// </summary>
    public JournalStepStart? UnfinishedStep { get; init; }

    /// <summary>
    /// Gets the <c>result</c> record, or <see langword="null"/> when the run never reached a verdict.
    /// </summary>
    public JournalResult? Result { get; init; }

    /// <summary>
    /// Gets the <c>released</c> record, or <see langword="null"/> when the release was not confirmed before
    /// publication.
    /// </summary>
    public JournalReleased? Released { get; init; }

    /// <summary>
    /// Gets the last <c>beat</c> record, or <see langword="null"/> when none was written.
    /// </summary>
    public JournalBeat? LastBeat { get; init; }

    /// <summary>
    /// Gets the stored error records, empty when no detail is available.
    /// </summary>
    public IReadOnlyList<ErrorRecord> StoredErrors
    {
        get => _storedErrors;
        init => _storedErrors = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="StoredErrors"/> holds the run's error detail.
    /// </summary>
    public bool ErrorDetailAvailable { get; init; }

    /// <summary>
    /// Gets the peak temperatures and worst statuses, <see cref="SummaryTemperatures.Initial"/> by default.
    /// </summary>
    public SummaryTemperatures Temperatures
    {
        get => _temperatures;
        init => _temperatures = value ?? SummaryTemperatures.Initial;
    }

    /// <summary>
    /// Gets the cause hints, <see cref="SummaryCauseHints.Empty"/> by default.
    /// </summary>
    public SummaryCauseHints CauseHints
    {
        get => _causeHints;
        init => _causeHints = value ?? SummaryCauseHints.Empty;
    }

    /// <summary>
    /// Gets a value indicating whether the summary is rebuilt from a journal at a later launch.
    /// </summary>
    public bool Recovered { get; init; }

    /// <summary>
    /// Gets a value indicating whether the summary is partial because the run never finished.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// Gets the product name written as <c>app.name</c>; an empty or white-space value becomes
    /// <see cref="DefaultAppName"/>.
    /// </summary>
    public string AppName
    {
        get => _appName;
        init => _appName = string.IsNullOrWhiteSpace(value) ? DefaultAppName : value;
    }
}
