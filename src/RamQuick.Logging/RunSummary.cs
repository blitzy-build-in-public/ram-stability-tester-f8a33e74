using System.Globalization;
using System.Text;
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
    /// can measure 0 (a processor count, the tested bytes, the plan's step count). So are a run id, test type
    /// or app version the <c>begin</c> record did not capture (<see cref="Guid.Empty"/>, empty text), the plan
    /// targets and whether coverage reached them when it holds no plan, and the coverage and error total of an
    /// unfinished run placed at an unknown step that left neither a heartbeat nor a step result. A
    /// <c>begin</c> record <see cref="JournalBegin.Create"/> made captures the run id, test type, app version,
    /// Stop on error and plan, so a run that publishes its own summary never reports them as null.
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
    /// Damage: when <see cref="RunSummarySource.JournalDamage"/> is set, for either kind of run,
    /// <see cref="SummaryRun.EndDetail"/> ends with "The progress journal held damaged data: " and the damage.
    /// When <see cref="RunSummarySource.StepPositionUnknown"/> is set, an unfinished run is reported as stopped
    /// at an unknown step.
    /// </para>
    /// <para>
    /// Steps: one entry per started step, in step order. A finished step is the last
    /// <see cref="RunSummarySource.FinishedSteps"/> entry for its number, joined by number to its definition
    /// in the <c>begin</c> record; an entry with no definition is skipped, because the summary cannot
    /// describe a step the plan never held. The running step of an unfinished run follows with
    /// <see langword="null"/> actuals and <see cref="SummaryStep.Completed"/> <see langword="false"/>. Every
    /// step name, in <c>steps[]</c>, the end detail and <see cref="UnfinishedRun"/>, passes through
    /// <see cref="SummaryFormat.StepName"/>, which leaves a plan's names unchanged; where a <c>step</c> record's
    /// name is left blank by it, the definition with the same number names the step.
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
        // Where recovery could not place an unfinished run, a finished step does not stand in for the one it stopped in.
        (int? lastStepNumber, string? lastStepName) = result is null && source.StepPositionUnknown
            ? (null, null)
            : LastPosition(definitions, finishedSteps, source.UnfinishedStep);

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

        // Only a begin record recovery could not read lacks the run id, test type or version; each is then not captured.
        var run = new SummaryRun
        {
            Id = begin.RunId == Guid.Empty ? null : SummaryFormat.RunId(begin.RunId),
            TestType = begin.TestType.Length > 0 ? begin.TestType : null,
            StopOnError = begin.StopOnError,
            Verdict = result is null ? FailVerdict : VerdictOf(result),
            EndReason = result is null ? nameof(RunEndReason.PreviousRunUnfinished) : result.EndReason.ToString(),

            // A result's detail is redacted here as well as by JournalResult.Create: recovery reads results back
            // from journals, which an earlier build may have written unredacted, and the summary is shared.
            EndDetail = WithJournalDamage(
                result is null
                    ? UnfinishedDetail(
                        lastStepNumber,
                        stepCount,
                        lastStepName,
                        source.StepPositionUnknown,
                        lastBeat is not null,
                        lastBeatUtc is not null)
                    : SummaryFormat.RedactPersonalPaths(result.EndDetail),
                source.JournalDamage),
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

        // A begin record without a plan captured no targets, so they and whether coverage reached them are unknown.
        bool planCaptured = begin.Steps.Any(step => step is not null);
        double? targetSeconds = planCaptured ? Finite(begin.TargetSeconds) : null;
        double? targetCoveragePercent = planCaptured ? Finite(begin.TargetCoveragePercent) : null;

        // An unfinished run that recovery could not place and that left neither a heartbeat nor a step result
        // captured no traffic figures at all. A run known to have stopped while preparing ran no traffic, so its 0s
        // are measured.
        bool trafficUnknown = result is null && source.StepPositionUnknown && lastBeat is null && finishedSteps.Count == 0;
        double? coveragePercent = trafficUnknown
            ? null
            : (result is null ? null : Percent(result.VerifiedBytes, testedBytes))
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
            App = new SummaryApp { Name = source.AppName, Version = begin.AppVersion.Length > 0 ? begin.AppVersion : null },
            Run = run,
            System = BuildSystem(begin.System, prepared),
            Memory = BuildMemory(begin, prepared, testedBytes, source.Released),
            Plan = new SummaryPlan
            {
                TargetSeconds = targetSeconds,
                TargetCoveragePercent = targetCoveragePercent,
            },
            Steps = BuildSteps(definitions, finishedSteps, source.UnfinishedStep, testedBytes),
            Coverage = new SummaryCoverage
            {
                Percent = coveragePercent,
                TargetPercent = targetCoveragePercent,
                TargetReached = coveragePercent is { } reached && targetCoveragePercent is { } target ? reached >= target : null,
            },
            Temperatures = temperatures,
            Errors = BuildErrors(source, result, lastBeat, finishedSteps, trafficUnknown),
            CauseHints = source.CauseHints,
            UnfinishedRun = result is not null
                ? null
                : new SummaryUnfinishedRun
                {
                    LastStepIndex = lastStepNumber,
                    LastStepName = lastStepName,
                    StepCount = stepCount > 0 ? stepCount : null,
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
        bool stepPositionUnknown,
        bool hasHeartbeat,
        bool heartbeatStamped)
    {
        // A plan size neither the begin record nor the step record captured is left out rather than stated as 0.
        string step = stepCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"at step {lastStepNumber} of {stepCount}")
            : string.Create(CultureInfo.InvariantCulture, $"at step {lastStepNumber}");

        // Without step data the run either never left preparation or, when its journal is damaged, stopped at a
        // step the journal no longer shows; only the first is "before step 1".
        string position = lastStepNumber is null
            ? stepPositionUnknown ? "at an unknown step" : "while preparing memory (before step 1)"
            : string.IsNullOrEmpty(lastStepName) ? step : $"{step} ({lastStepName})";

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

    // A summary recovery rebuilt from a damaged journal says so, after whatever detail the run itself recorded.
    private static string WithJournalDamage(string endDetail, string? journalDamage)
    {
        if (journalDamage is null)
        {
            return endDetail;
        }

        string damage = $"The progress journal held damaged data: {journalDamage}.";
        return endDetail.Length == 0 ? damage : endDetail + " " + damage;
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
            string? name = SummaryFormat.StepName(unfinishedStep.Name)
                ?? DefinitionName(definitions, unfinishedStep.Index);
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
        definitions.TryGetValue(number, out JournalStep? definition) ? SummaryFormat.StepName(definition.Name) : null;

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
        Name = SummaryFormat.StepName(definition.Name) ?? string.Empty,
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
        IReadOnlyList<JournalStepEnd> finishedSteps,
        bool totalUnknown)
    {
        IReadOnlyList<ErrorRecord> stored = source.StoredErrors;

        // Group validates the records, rejecting a null element before any is mapped.
        IReadOnlyList<ErrorGroup> groups = ErrorGrouping.Group(stored);

        ulong? total = totalUnknown
            ? null
            : result?.TotalErrors ?? Math.Max(lastBeat?.Errors ?? 0, SumErrors(finishedSteps));
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
            Truncated = total is { } known && known > (ulong)stored.Count && stored.Count >= ErrorGrouping.StoredDetailCap,
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
    // See RunSummary: the init accessor keeps the name non-null when a document omits it.
    private readonly string _name = string.Empty;

    /// <summary>
    /// Gets the product name, <c>"RAMQuick"</c>.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the product build version that ran the test, as the <c>begin</c> record captured it, or
    /// <see langword="null"/> when the journal did not capture it.
    /// </summary>
    public string? Version { get; init; }
}

/// <summary>
/// The run's identity, outcome and times, the summary's <c>run</c> object.
/// </summary>
public sealed record SummaryRun
{
    // See RunSummary: the init accessors keep the strings non-null when a document omits them.
    private readonly string _verdict = string.Empty;
    private readonly string _endReason = string.Empty;
    private readonly string _endDetail = string.Empty;

    /// <summary>
    /// Gets the run id: the run GUID in its 32-hex-digit <c>N</c> form, which also ends the results file
    /// base name. <see langword="null"/> only in a summary rebuilt from a journal that never captured the
    /// run's id; <see cref="RunSummaryJson.TryDeserialize"/> accepts no other summary without one.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gets the test type name, for example <c>"Quick"</c>, or <see langword="null"/> when the journal did not
    /// capture it.
    /// </summary>
    public string? TestType { get; init; }

    /// <summary>
    /// Gets a value indicating whether Stop on error was on, or <see langword="null"/> when the journal did not
    /// capture it.
    /// </summary>
    public bool? StopOnError { get; init; }

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
    /// Gets the end reason's diagnostic detail: empty when the end reason has no diagnostic, as for
    /// <see cref="RunEndReason.Completed"/>, and never <see langword="null"/>. For an unfinished run it states
    /// where the run stopped and which times are approximate or unknown.
    /// </summary>
    public string EndDetail
    {
        get => _endDetail;
        init => _endDetail = value ?? string.Empty;
    }

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
/// The plan's targets, the summary's <c>plan</c> object. Both are <see langword="null"/> when the journal did not
/// capture the plan.
/// </summary>
public sealed record SummaryPlan
{
    /// <summary>
    /// Gets the plan's target duration in seconds: the sum of its steps' time budgets.
    /// </summary>
    public double? TargetSeconds { get; init; }

    /// <summary>
    /// Gets the plan's coverage target as a percentage: the sum of its steps' coverage budgets.
    /// </summary>
    public double? TargetCoveragePercent { get; init; }
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
    /// Gets the step's name as the plan defines it. <see cref="RunSummary.Build"/> writes it through
    /// <see cref="SummaryFormat.StepName"/>, which leaves a plan's names unchanged, and writes an empty name when
    /// the <c>begin</c> record holds none.
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
    /// Gets the coverage the run reached, as a percentage of the tested bytes, or <see langword="null"/> when the
    /// journal of an unfinished run captured neither where it stopped nor any of its traffic.
    /// </summary>
    public double? Percent { get; init; }

    /// <summary>
    /// Gets the plan's coverage target, as a percentage, or <see langword="null"/> when the journal did not
    /// capture the plan.
    /// </summary>
    public double? TargetPercent { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Percent"/> reached <see cref="TargetPercent"/>, or
    /// <see langword="null"/> when either is unknown.
    /// </summary>
    public bool? TargetReached { get; init; }
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
    /// Gets the true error total, from the engine's counters, or <see langword="null"/> when the journal of an
    /// unfinished run captured neither where it stopped nor any of its traffic.
    /// </summary>
    public ulong? Total { get; init; }

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
    /// Gets the name of the step at <see cref="LastStepIndex"/>, as <see cref="SummaryFormat.StepName"/> writes
    /// it, or <see langword="null"/> when there is none.
    /// </summary>
    public string? LastStepName { get; init; }

    /// <summary>
    /// Gets the number of steps in the plan, or <see langword="null"/> when neither the <c>begin</c> record nor the
    /// step's own record captured it.
    /// </summary>
    public int? StepCount { get; init; }

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
    /// <summary>
    /// The most UTF-8 bytes a step name may hold, 63: the native <c>rq_step_def.name</c> field is 64 bytes and
    /// NUL-terminated. <c>RamQuick.Plans.StepDefinition.MaxNameUtf8Bytes</c> states the same limit for the plans,
    /// which this library does not reference.
    /// </summary>
    public const int MaxStepNameUtf8Bytes = 63;

    private const string HexPrefix = "0x";

    // Ends a step name StepName cut, so the cut shows: U+2026, three UTF-8 bytes.
    private const string StepNameCutMarker = "\u2026";

    // What UTF-8 writes for a surrogate without its partner, so PlainText writes the same.
    private const char UnpairedSurrogateReplacement = '\uFFFD';

    // The placeholders RedactPersonalPaths leaves where a personal part of a path stood.
    private const string UserProfileToken = "%USERPROFILE%";
    private const string LocalAppDataToken = "%LOCALAPPDATA%";
    private const string RoamingAppDataToken = "%APPDATA%";
    private const string TempToken = "%TEMP%";
    private const string AppFolderToken = "<app folder>";
    private const string UserToken = "<user>";
    private const string MachineToken = "<machine>";

    // The placeholders the results log writes, before these rules run, for its run's data folder and for the current
    // user name where it is a path segment.
    internal const string DataFolderToken = "<data>";
    internal const string UserNameToken = "%USERNAME%";

    // Every placeholder of either family: the profile and computer-name rules leave each one as it stands, so a
    // second redaction returns the first one's text and a computer named DATA or TEMP corrupts none of them.
    private static readonly string[] _placeholders =
    {
        UserProfileToken,
        LocalAppDataToken,
        RoamingAppDataToken,
        TempToken,
        AppFolderToken,
        UserToken,
        MachineToken,
        DataFolderToken,
        UserNameToken,
    };

    // Windows keeps every account's profile folder here, named after the account.
    private const string ProfilesFolderName = "Users";

    // No Windows user name may hold these, so besides white space, separators and quotes they end a name in a path.
    private const string NameTerminators = "\"'[]:;|=,+*?<>";

    // No Windows file name may hold these, so a profile folder name, which may hold anything else, ends before one.
    private const string InvalidFileNameCharacters = "\"<>|:*?";

    // An apostrophe closing a quoted path is followed by one of these, white space or the end of the name's text.
    private const string ClosingQuoteFollowers = ".,;:)]!?";

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

    /// <summary>
    /// Returns <paramref name="text"/> as one line of plain text. Every control character (Unicode category Cc,
    /// line breaks, escape and NUL included), format character (Cf, such as the bidirectional controls U+202A to
    /// U+202E and U+2066 to U+2069, the zero-width characters U+200B to U+200F and U+FEFF) and line or paragraph
    /// separator (U+2028, U+2029) becomes one space, and every surrogate without its partner becomes U+FFFD, as
    /// UTF-8 writes it. Text read back from a journal or a summary file is untrusted: a line break in it would
    /// start a line that reads as another field or another verdict, an escape or a NUL would hide or cut what
    /// follows, and a bidirectional control would reorder what the reader sees.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>
    /// The plain text, in which each replaced character, a surrogate pair counting as one, is one character and
    /// everything else is kept verbatim; <paramref name="text"/> itself when it holds nothing to replace.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string PlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? plain = null;
        int index = 0;
        while (index < text.Length)
        {
            char? replacement = PlainTextReplacement(text, index, out int length);
            if (replacement is { } written)
            {
                plain ??= new StringBuilder(text.Length).Append(text, 0, index);
                plain.Append(written);
            }
            else
            {
                plain?.Append(text, index, length);
            }

            index += length;
        }

        return plain?.ToString() ?? text;
    }

    /// <summary>
    /// Returns a step name taken from a journal as the window and the summaries show it: <see cref="PlainText"/>,
    /// trimmed, and at most <see cref="MaxStepNameUtf8Bytes"/> UTF-8 bytes. A longer name is cut after the last
    /// whole character that leaves room for a closing "…" (U+2026), so the cut shows, and a surrogate pair is never
    /// split. A name within the limit that holds none of the characters <see cref="PlainText"/> replaces and no
    /// white space at either end, as every plan's step name does, is returned unchanged.
    /// </summary>
    /// <param name="name">The name as the journal holds it, or <see langword="null"/>.</param>
    /// <returns>
    /// The name to show, or <see langword="null"/> when <paramref name="name"/> is <see langword="null"/> or
    /// holds nothing but white space and the characters <see cref="PlainText"/> replaces.
    /// </returns>
    public static string? StepName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string plain = PlainText(name).Trim();
        if (plain.Length == 0)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(plain) <= MaxStepNameUtf8Bytes)
        {
            return plain;
        }

        // Plain text holds no surrogate without its partner, so every position read here starts a whole scalar.
        int budget = MaxStepNameUtf8Bytes - Encoding.UTF8.GetByteCount(StepNameCutMarker);
        int bytes = 0;
        int end = 0;
        while (end < plain.Length)
        {
            Rune scalar = Rune.GetRuneAt(plain, end);
            if (bytes + scalar.Utf8SequenceLength > budget)
            {
                break;
            }

            bytes += scalar.Utf8SequenceLength;
            end += scalar.Utf16SequenceLength;
        }

        return plain[..end].TrimEnd() + StepNameCutMarker;
    }

    // What PlainText writes for the scalar at index, whose UTF-16 length, 2 for a surrogate pair and otherwise 1,
    // is returned in length: null to keep it, a space for a control, format or separator character, and
    // UnpairedSurrogateReplacement for a surrogate without its partner.
    private static char? PlainTextReplacement(string text, int index, out int length)
    {
        char character = text[index];
        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            int scalar = char.ConvertToUtf32(character, text[index + 1]);
            return IsReplacedCategory(CharUnicodeInfo.GetUnicodeCategory(scalar)) ? ' ' : null;
        }

        length = 1;
        if (char.IsSurrogate(character))
        {
            return UnpairedSurrogateReplacement;
        }

        return IsReplacedCategory(CharUnicodeInfo.GetUnicodeCategory(character)) ? ' ' : null;
    }

    private static bool IsReplacedCategory(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Returns <paramref name="text"/> with the personal parts of the Windows paths it holds replaced by
    /// placeholders. The journal's <c>result</c> record and both summaries persist an end detail through it,
    /// because an end detail can carry an exception message, and an exception message can name a path in the
    /// user's profile, while the summaries are made to be shared and no log may record personal data.
    /// </summary>
    /// <param name="text">The text, typically an end detail or an exception message.</param>
    /// <returns>
    /// The redacted text; <paramref name="text"/> itself when it holds nothing to redact.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The rules apply in this order, ignoring letter case and treating <c>\</c> and <c>/</c> alike:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The current user's profile folder, local and roaming application data folders, temporary folder and the
    /// application's own folder become <c>%USERPROFILE%</c>, <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>,
    /// <c>%TEMP%</c> and <c>&lt;app folder&gt;</c>, the longest folder first, wherever one ends where a name
    /// ends. A folder the environment cannot name, a relative one and a bare drive or share root are skipped.
    /// </description></item>
    /// <item><description>
    /// The folder after any remaining <c>&lt;drive&gt;:\Users\</c> becomes <c>&lt;user&gt;</c>, whichever
    /// account's profile it is. The whole profile folder name is redacted, spaces and apostrophes included, and
    /// it never runs past a character no file name may hold, such as a line break or a colon. It runs to the
    /// separator after it; where none follows, to the quote that closes a quoted path, or else to the end of the
    /// line, less the spaces and dots Windows drops from the end of a name and a drive letter starting another
    /// path. A folder named exactly like a placeholder is left as it stands.
    /// </description></item>
    /// <item><description>
    /// A path segment equal to the current user name, following a separator, becomes <c>&lt;user&gt;</c>.
    /// </description></item>
    /// <item><description>
    /// The computer name, as a whole word, becomes <c>&lt;machine&gt;</c>, except inside a placeholder.
    /// </description></item>
    /// </list>
    /// <para>
    /// In rules 1 and 3, a name ends at the end of the text, a separator, white space, a quote, a character no
    /// Windows user name may hold, or a dot that ends a sentence. Everything else is kept verbatim. Rule 4 never
    /// rewrites any part of a placeholder, these or the <c>&lt;data&gt;</c> and <c>%USERNAME%</c> the results log
    /// writes, so a computer named <c>DATA</c> or <c>TEMP</c> corrupts none of them. The result is stable: redacting
    /// it again returns it unchanged. An environment lookup that fails skips its rule, so no text makes this method
    /// throw.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string RedactPersonalPaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The environment is read on every call; an empty text needs none of it.
        return text.Length == 0 ? text : RedactPersonalPaths(text, PersonalPathSnapshot.Capture());
    }

    /// <summary>
    /// Returns <paramref name="text"/> redacted by the rules of <see cref="RedactPersonalPaths(string)"/>, with
    /// the folders and names they replace taken from <paramref name="snapshot"/> instead of the environment.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="snapshot">The folders and names to redact, as <see cref="PersonalPathSnapshot.Capture"/> read them.</param>
    /// <returns>
    /// The redacted text; <paramref name="text"/> itself when it holds nothing to redact.
    /// </returns>
    /// <remarks>
    /// For a caller that redacts many texts, such as the results log with one free-text value per error line: the
    /// environment is read once, not per text. Like the public overload, it never throws for any text.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static string RedactPersonalPaths(string text, PersonalPathSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (text.Length == 0)
        {
            return text;
        }

        string redacted = text;
        foreach ((string folder, string token) in snapshot.Folders)
        {
            redacted = ReplaceFolder(redacted, folder, token);
        }

        redacted = RedactProfileFolderNames(redacted);
        if (snapshot.UserName is { } userName)
        {
            redacted = ReplaceUserSegments(redacted, userName);
        }

        if (snapshot.MachineName is { } machineName)
        {
            redacted = ReplaceMachineWords(redacted, machineName);
        }

        return redacted;
    }

    // Longest first, so a folder inside another (the temporary folder inside local application data inside the
    // profile) is replaced by its own, more specific placeholder.
    private static IEnumerable<(string Folder, string Token)> PersonalFolders()
    {
        var folders = new List<(string Folder, string Token)>(5);
        AddPersonalFolder(folders, static () => SpecialFolder(Environment.SpecialFolder.UserProfile), UserProfileToken);
        AddPersonalFolder(folders, static () => SpecialFolder(Environment.SpecialFolder.LocalApplicationData), LocalAppDataToken);
        AddPersonalFolder(folders, static () => SpecialFolder(Environment.SpecialFolder.ApplicationData), RoamingAppDataToken);
        AddPersonalFolder(folders, static () => Path.GetTempPath(), TempToken);
        AddPersonalFolder(folders, static () => AppContext.BaseDirectory, AppFolderToken);
        return folders.OrderByDescending(static entry => entry.Folder.Length);
    }

    private static string SpecialFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    private static void AddPersonalFolder(List<(string Folder, string Token)> folders, Func<string?> lookup, string token)
    {
        string? folder = EnvironmentValue(lookup)?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            // A drive or share root would match the start of every path on that volume, and a relative folder
            // the start of unrelated names, so neither identifies anything personal.
            string? root = Path.GetPathRoot(folder)?.TrimEnd('\\', '/');
            if (Path.IsPathFullyQualified(folder) && !string.Equals(root, folder, StringComparison.OrdinalIgnoreCase))
            {
                folders.Add((folder, token));
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed: a folder that cannot be examined is skipped like one the environment cannot name.
        }
    }

    // The redaction runs while a failure is being recorded, so an environment that cannot answer, such as a
    // profile that is not loaded, only skips its rule rather than raising a failure of its own.
    private static string? EnvironmentValue(Func<string?> lookup)
    {
        try
        {
            string? value = lookup();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReplaceFolder(string text, string folder, string token)
    {
        StringBuilder? result = null;
        int copied = 0;
        int index = 0;
        while (index <= text.Length - folder.Length)
        {
            if (StartsWord(text, index)
                && PathTextEquals(text.AsSpan(index, folder.Length), folder)
                && EndsName(text, index + folder.Length))
            {
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, index - copied).Append(token);
                index += folder.Length;
                copied = index;
            }
            else
            {
                index++;
            }
        }

        return Finish(result, text, copied);
    }

    // <drive>:\Users\<name>: whoever's profile a path names, its folder name is the account's name.
    private static string RedactProfileFolderNames(string text)
    {
        StringBuilder? result = null;
        int copied = 0;
        int prefixLength = 3 + ProfilesFolderName.Length + 1;
        int index = 0;
        while (index <= text.Length - prefixLength)
        {
            if (char.IsAsciiLetter(text[index])
                && StartsWord(text, index)
                && text[index + 1] == ':'
                && IsSeparator(text[index + 2])
                && text.AsSpan(index + 3, ProfilesFolderName.Length).Equals(ProfilesFolderName, StringComparison.OrdinalIgnoreCase)
                && IsSeparator(text[index + prefixLength - 1]))
            {
                // The placeholder starts with a character that ends a name, so a redacted folder reads as empty
                // and is left as it is. A folder named like a %…% placeholder, such as the %USERNAME% the results
                // log writes for the current user's name, names no account and is left as well.
                int nameStart = index + prefixLength;
                int nameEnd = ProfileFolderNameEnd(text, index, nameStart);
                if (nameEnd > nameStart && !IsPlaceholder(text.AsSpan(nameStart, nameEnd - nameStart)))
                {
                    result ??= new StringBuilder(text.Length);
                    result.Append(text, copied, nameStart - copied).Append(UserToken);
                    copied = nameEnd;
                }

                index = Math.Max(nameEnd, index + 1);
            }
            else
            {
                index++;
            }
        }

        return Finish(result, text, copied);
    }

    private static string ReplaceUserSegments(string text, string userName)
    {
        StringBuilder? result = null;
        int copied = 0;
        int index = 1;
        while (index <= text.Length - userName.Length)
        {
            if (IsSeparator(text[index - 1])
                && text.AsSpan(index, userName.Length).Equals(userName, StringComparison.OrdinalIgnoreCase)
                && EndsName(text, index + userName.Length))
            {
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, index - copied).Append(UserToken);
                index += userName.Length;
                copied = index;
            }
            else
            {
                index++;
            }
        }

        return Finish(result, text, copied);
    }

    private static string ReplaceMachineWords(string text, string machineName)
    {
        StringBuilder? result = null;
        int copied = 0;
        int index = 0;
        while (index <= text.Length - machineName.Length)
        {
            int end = index + machineName.Length;
            if ((index == 0 || !IsMachineNameCharacter(text[index - 1]))
                && (end == text.Length || !IsMachineNameCharacter(text[end]))
                && text.AsSpan(index, machineName.Length).Equals(machineName, StringComparison.OrdinalIgnoreCase)
                && !OverlapsPlaceholder(text, index, end))
            {
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, index - copied).Append(MachineToken);
                index = end;
                copied = index;
            }
            else
            {
                index++;
            }
        }

        return Finish(result, text, copied);
    }

    // A computer named like a placeholder's word, such as DATA, TEMP or USER, must not turn the placeholder into
    // other text, which keeps the redaction stable and every placeholder readable: true when a placeholder written
    // in text overlaps the characters from start up to end.
    private static bool OverlapsPlaceholder(string text, int start, int end)
    {
        foreach (string placeholder in _placeholders)
        {
            for (int at = Math.Max(0, start - placeholder.Length + 1); at < end; at++)
            {
                if (StartsWithAt(text, at, placeholder))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPlaceholder(ReadOnlySpan<char> name)
    {
        foreach (string placeholder in _placeholders)
        {
            if (name.Equals(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAt(string text, int start, string value) =>
        text.Length - start >= value.Length && text.AsSpan(start, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase);

    private static string Finish(StringBuilder? result, string text, int copied) =>
        result is null ? text : result.Append(text, copied, text.Length - copied).ToString();

    private static bool StartsWord(string text, int index) => index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    private static bool IsSeparator(char character) => character is '\\' or '/';

    private static bool IsQuote(char character) => character is '"' or '\'';

    private static bool IsMachineNameCharacter(char character) => char.IsLetterOrDigit(character) || character is '-' or '_';

    private static bool PathTextEquals(ReadOnlySpan<char> text, string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            char actual = text[i];
            char expected = path[i];
            bool same = IsSeparator(actual)
                ? IsSeparator(expected)
                : char.ToUpperInvariant(actual) == char.ToUpperInvariant(expected);
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndsName(string text, int index)
    {
        if (index >= text.Length)
        {
            return true;
        }

        char next = text[index];

        // Names hold dots, so a dot ends one only where it ends a sentence.
        if (next == '.')
        {
            return index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]) || IsQuote(text[index + 1]);
        }

        return IsSeparator(next) || char.IsWhiteSpace(next) || NameTerminators.Contains(next);
    }

    // A profile folder is named after its account, and Windows lets that name hold spaces, apostrophes, dots and
    // brackets, so the whole component up to the next separator is the name. Only a character no file name may
    // hold ends the scan sooner. Without a separator, the quote that opened the path closes the name; failing that
    // the name runs to where the scan stopped, because any part of it left in place still names the account.
    private static int ProfileFolderNameEnd(string text, int pathStart, int nameStart)
    {
        int limit = nameStart;
        while (limit < text.Length && !IsSeparator(text[limit]) && MayBeInFileName(text[limit]))
        {
            limit++;
        }

        if (limit < text.Length && IsSeparator(text[limit]))
        {
            return limit;
        }

        // A colon stops the scan; when it follows a drive letter, that letter starts the next path, not this name,
        // and so does an apostrophe quoting that path. Redacted with this name, the apostrophe would leave the next
        // path unquoted on a second redaction and change how much of it is redacted.
        if (limit > nameStart
            && limit + 1 < text.Length
            && text[limit] == ':'
            && IsSeparator(text[limit + 1])
            && char.IsAsciiLetter(text[limit - 1])
            && StartsWord(text, limit - 1))
        {
            limit--;
            if (limit > nameStart && text[limit - 1] == '\'')
            {
                limit--;
            }
        }

        char opening = pathStart > 0 ? text[pathStart - 1] : '\0';
        if (opening == '"' && limit < text.Length && text[limit] == '"')
        {
            return limit;
        }

        if (opening == '\'')
        {
            // An apostrophe inside the name, as in O'Connor, is followed by more of the name.
            for (int quote = nameStart; quote < limit; quote++)
            {
                if (text[quote] == '\''
                    && (quote + 1 == limit
                        || char.IsWhiteSpace(text[quote + 1])
                        || ClosingQuoteFollowers.Contains(text[quote + 1])))
                {
                    return quote;
                }
            }
        }

        // Windows drops the spaces and dots a file name ends with, so trailing ones belong to the sentence.
        int end = limit;
        while (end > nameStart && (char.IsWhiteSpace(text[end - 1]) || text[end - 1] == '.'))
        {
            end--;
        }

        return end;
    }

    // Line and paragraph separators end a line, which no name in a message spans.
    private static bool MayBeInFileName(char character) =>
        !char.IsControl(character)
        && character is not ('\u2028' or '\u2029')
        && !InvalidFileNameCharacters.Contains(character);

    /// <summary>
    /// The folders and names the rules of <see cref="RedactPersonalPaths(string)"/> replace, read from the
    /// environment at one moment.
    /// </summary>
    /// <remarks>
    /// The public overload reads a new one per call. The results log reads one and reuses it for every value it
    /// writes, up to one per error line, because the account's folders and the computer's name do not change while
    /// the process runs.
    /// </remarks>
    internal sealed class PersonalPathSnapshot
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PersonalPathSnapshot"/> class.
        /// </summary>
        /// <param name="folders">
        /// The fully qualified folders, none a bare drive or share root and none ending in a separator, each with its
        /// placeholder, the longest folder first.
        /// </param>
        /// <param name="userName">The current user name, or <see langword="null"/> to skip its rule.</param>
        /// <param name="machineName">The computer name, or <see langword="null"/> to skip its rule.</param>
        internal PersonalPathSnapshot((string Folder, string Token)[] folders, string? userName, string? machineName)
        {
            Folders = folders;
            UserName = userName;
            MachineName = machineName;
        }

        /// <summary>
        /// Gets the folders and their placeholders, the longest folder first.
        /// </summary>
        internal (string Folder, string Token)[] Folders { get; }

        /// <summary>
        /// Gets the current user name, or <see langword="null"/> when the environment could not name it.
        /// </summary>
        internal string? UserName { get; }

        /// <summary>
        /// Gets the computer name, or <see langword="null"/> when the environment could not name it.
        /// </summary>
        internal string? MachineName { get; }

        /// <summary>
        /// Reads the current user's profile, local and roaming application data and temporary folders, the
        /// application's folder, the user name and the computer name. Never throws: a lookup that fails leaves its
        /// rule out.
        /// </summary>
        /// <returns>The snapshot.</returns>
        internal static PersonalPathSnapshot Capture() => new(
            PersonalFolders().ToArray(),
            EnvironmentValue(static () => Environment.UserName),
            EnvironmentValue(static () => Environment.MachineName));
    }
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
    /// Gets the first damage recovery found in the run's journal, as a phrase without a trailing period, or
    /// <see langword="null"/> when there was none. When set, <c>run.endDetail</c> ends with "The progress
    /// journal held damaged data: " followed by it and a period.
    /// </summary>
    public string? JournalDamage { get; init; }

    /// <summary>
    /// Gets a value indicating whether recovery could not tell where an unfinished run stopped, because its
    /// journal is damaged. A run without a <see cref="Result"/> then reads "did not finish at an unknown step"
    /// in <c>run.endDetail</c>, instead of "did not finish while preparing memory (before step 1)" or a step
    /// taken from its <see cref="FinishedSteps"/>, and its <c>unfinishedRun</c> names no last step.
    /// </summary>
    public bool StepPositionUnknown { get; init; }

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
