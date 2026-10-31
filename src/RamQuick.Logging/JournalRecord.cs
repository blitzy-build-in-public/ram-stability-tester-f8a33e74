using System.Text.Json.Serialization;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// One line of the progress journal, <c>&lt;data&gt;\journal\active-run.jsonl</c>: a JSON object whose
/// <c>"t"</c> property names the record type.
/// </summary>
/// <remarks>
/// <para>
/// A run writes, in order: <see cref="JournalBegin"/> (<c>begin</c>), <see cref="JournalPrepared"/>
/// (<c>prepared</c>), then per step <see cref="JournalStepStart"/> (<c>step</c>) and
/// <see cref="JournalStepEnd"/> (<c>stepEnd</c>) with <see cref="JournalBeat"/> (<c>beat</c>) heartbeats in
/// between, then <see cref="JournalResult"/> (<c>result</c>), <see cref="JournalReleased"/>
/// (<c>released</c>) and <see cref="JournalEnd"/> (<c>end</c>). The <c>begin</c>, <c>step</c>,
/// <c>result</c> and <c>end</c> records are acknowledged: their writer waits until the line is durable on
/// disk. The others are written without acknowledgment.
/// </para>
/// <para>
/// Serialization: <c>JournalLineSerializer</c> in <c>RunSummaryJsonContext.cs</c> always serializes through
/// this base type, so the <c>"t"</c> discriminator is the first property of every line. .NET 8
/// System.Text.Json requires the discriminator to come first when reading, so a line written any other way
/// cannot be parsed. Property names are camelCased (<c>Utc</c> becomes <c>"utc"</c>) and enumerations are
/// written by member name. No derived type may declare a property named <c>T</c>, which would collide with
/// the discriminator.
/// </para>
/// <para>
/// Index rule: every step number persisted in the journal is 1-based, matching the UI's "step n of N".
/// <c>RamQuick.Core</c>'s in-memory step indices are 0-based, so the factory methods add 1.
/// </para>
/// <para>
/// Run attribution: the records that state a run's outcome, <c>stepEnd</c>, <c>result</c>, <c>released</c>
/// and <c>end</c>, carry the run id of their <c>begin</c> record, which <c>ProgressJournal</c> stamps, so
/// recovery attributes each to its own run and ignores one that names another. The <c>step</c>,
/// <c>beat</c> and <c>prepared</c> records keep the shape of the plan's example lines and belong to the run
/// whose <c>begin</c> they follow.
/// </para>
/// <para>
/// Recovery: <c>CrashDetector</c> rebuilds a crashed run's summaries from these records alone, never from
/// the next launch's machine state, so every value a summary needs is captured here. Records are plain data
/// with no I/O, clock reads or mutable static state. No member is <see langword="required"/>, so a record
/// with missing fields still parses, and a field it lacks reaches the summary as not captured
/// (<c>null</c>), never as a measured value. Where a missing value and a captured 0 must stay distinct, the
/// member is nullable: a missing one reads back as <see langword="null"/>, and a captured 0 stays 0. Other
/// missing numbers read back as 0, which the summary treats as not captured where no run can measure 0 (a
/// processor count, the tested bytes). A missing timestamp reads back as <c>default(DateTimeOffset)</c>,
/// 0001-01-01, which no clock produces, and the summary treats it as not captured.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(JournalBegin), "begin")]
[JsonDerivedType(typeof(JournalPrepared), "prepared")]
[JsonDerivedType(typeof(JournalStepStart), "step")]
[JsonDerivedType(typeof(JournalStepEnd), "stepEnd")]
[JsonDerivedType(typeof(JournalBeat), "beat")]
[JsonDerivedType(typeof(JournalResult), "result")]
[JsonDerivedType(typeof(JournalReleased), "released")]
[JsonDerivedType(typeof(JournalEnd), "end")]
public abstract record JournalRecord
{
    /// <summary>
    /// Gets the UTC time the record was enqueued. <c>ProgressJournal</c> stamps it from its
    /// <see cref="TimeProvider"/> when the record is enqueued, and <c>CrashDetector</c> stamps the
    /// <c>end</c> record it writes during recovery. JSON <c>"utc"</c>, ISO 8601 with a <c>Z</c> suffix.
    /// </summary>
    public DateTimeOffset Utc { get; init; }
}

/// <summary>
/// The <c>begin</c> record: the run's identity, its full plan and the machine state captured at Start.
/// Acknowledged, and written before <c>rq_session_start</c>; Start is refused when it cannot be made
/// durable.
/// </summary>
/// <remarks>
/// <para>
/// Everything a recovered summary reports about the plan, the system and the memory configuration comes
/// from this record, so a crash recovered on another launch never reports that launch's machine.
/// </para>
/// <para>
/// Value equality compares <see cref="Steps"/> element by element, so a record read back from the journal
/// equals the one that was written.
/// </para>
/// <para>
/// <see cref="Steps"/> is a snapshot: its init accessor copies the supplied list, so changing that list
/// after <see cref="Create"/>, an object initializer or deserialization has returned, for example while
/// the record waits in the writer's queue, changes neither the plan that reaches the journal nor the
/// record's equality and hash code.
/// </para>
/// </remarks>
public sealed record JournalBegin : JournalRecord
{
    // The System.Text.Json source generator in .NET 8 assigns default(T) to every init-only property a line
    // omits, which would leave a non-nullable reference null. The init accessors restore the empty value.
    private readonly string _logBaseName = string.Empty;
    private readonly string _testType = string.Empty;
    private readonly string _appVersion = string.Empty;
    private readonly IReadOnlyList<JournalStep> _steps = Array.Empty<JournalStep>();
    private readonly JournalSystemInfo _system = new();
    private readonly JournalMemoryInfo _memory = new();

    /// <summary>
    /// Gets the run id, the GUID whose 32-hex-digit <c>N</c> form ends the results file base name.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Gets the results file base name that <c>ResultsLog.OpenRun</c> reserved for this run,
    /// <c>RamQuick-&lt;yyyyMMdd-HHmmss&gt;-&lt;TestType&gt;-&lt;runId&gt;</c>. The <c>.log</c> and both
    /// summaries share it.
    /// </summary>
    public string LogBaseName
    {
        get => _logBaseName;
        init => _logBaseName = value ?? string.Empty;
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
    /// Gets a value indicating whether Stop on error was on for the run, or <see langword="null"/> when the
    /// record did not capture it; <see cref="Create"/> always sets it.
    /// </summary>
    public bool? StopOnError { get; init; }

    /// <summary>
    /// Gets the UTC time Start was accepted, which becomes the summary's <c>run.startRequestedUtc</c> and
    /// the origin of <c>run.timeToVerdictSeconds</c>.
    /// </summary>
    public DateTimeOffset StartRequestedUtc { get; init; }

    /// <summary>
    /// Gets the product build version that ran the test, the summary's <c>app.version</c>.
    /// </summary>
    public string AppVersion
    {
        get => _appVersion;
        init => _appVersion = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the full step definitions of the plan, in run order, each numbered from 1: a read-only copy of
    /// the list supplied at construction, empty when none was.
    /// </summary>
    public IReadOnlyList<JournalStep> Steps
    {
        get => _steps;
        init => _steps = JournalValues.ReadOnlySnapshot(value);
    }

    /// <summary>
    /// Gets the system snapshot captured for this run: OS, CPU, logical processors, processor groups and
    /// best instruction set.
    /// </summary>
    public JournalSystemInfo System
    {
        get => _system;
        init => _system = value ?? new JournalSystemInfo();
    }

    /// <summary>
    /// Gets the detected-memory summary captured for this run: installed bytes, type, speed, channels
    /// and DIMMs.
    /// </summary>
    public JournalMemoryInfo Memory
    {
        get => _memory;
        init => _memory = value ?? new JournalMemoryInfo();
    }

    /// <summary>
    /// Gets the physical memory available at Start, in bytes; the summary's
    /// <c>memory.availableBytesAtStart</c>. <see langword="null"/> when the record did not capture it,
    /// which a captured 0 stays distinct from.
    /// </summary>
    public ulong? AvailableBytes { get; init; }

    /// <summary>
    /// Gets the region size planned for the run before locking, in bytes; the summary's
    /// <c>memory.plannedBytes</c>. <see langword="null"/> when the record did not capture it, which a
    /// captured 0 stays distinct from.
    /// </summary>
    public ulong? PlannedBytes { get; init; }

    /// <summary>
    /// Gets the plan's target duration in seconds: the sum of the steps' time budgets. Derived from
    /// <see cref="Steps"/> and never serialized; a <see langword="null"/> entry, which a damaged journal line
    /// can hold, is skipped. It never throws: the budgets are summed as <see cref="double"/> values, exact
    /// for every total below 2^53 ms, so budgets a damaged line made too large cannot overflow the sum.
    /// </summary>
    [JsonIgnore]
    public double TargetSeconds =>
        Steps.Where(step => step is not null).Sum(step => (double)step.TimeBudgetMs) / 1000.0;

    /// <summary>
    /// Gets the plan's coverage target as a percentage: the sum of the steps' coverage budgets. Derived from
    /// <see cref="Steps"/> and never serialized; a <see langword="null"/> entry, which a damaged journal line
    /// can hold, is skipped.
    /// </summary>
    [JsonIgnore]
    public double TargetCoveragePercent =>
        Steps.Where(step => step is not null).Sum(step => (long)step.CoverageBudgetPercent);

    /// <summary>
    /// Creates the <c>begin</c> record for a run that is about to start.
    /// </summary>
    /// <param name="runId">The run id; never <see cref="Guid.Empty"/>.</param>
    /// <param name="logBaseName">The base name <c>ResultsLog.OpenRun</c> reserved.</param>
    /// <param name="testType">The test type name, for example <c>"Quick"</c>.</param>
    /// <param name="stopOnError">Whether Stop on error is on.</param>
    /// <param name="startRequestedUtc">The UTC time Start was accepted.</param>
    /// <param name="appVersion">The product build version.</param>
    /// <param name="steps">
    /// The plan's steps in run order. Each element's 0-based <see cref="StepAttributes.Index"/> must equal
    /// its position, because recovery joins step results to definitions by number.
    /// </param>
    /// <param name="system">The engine's system snapshot captured for this run.</param>
    /// <param name="operatingSystem">
    /// The operating-system description captured for this run; <see cref="SystemSnapshot"/> has none.
    /// </param>
    /// <param name="memory">The detected-memory summary captured for this run.</param>
    /// <param name="availableBytes">The physical memory available at Start, in bytes.</param>
    /// <param name="plannedBytes">The region size planned before locking, in bytes.</param>
    /// <returns>The record, with <see cref="JournalRecord.Utc"/> left for the journal to stamp.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="runId"/> is empty, a string argument is empty or white space,
    /// <paramref name="steps"/> is empty or contains <see langword="null"/>, or a step's index does not
    /// match its position.
    /// </exception>
    public static JournalBegin Create(
        Guid runId,
        string logBaseName,
        string testType,
        bool stopOnError,
        DateTimeOffset startRequestedUtc,
        string appVersion,
        IReadOnlyList<StepAttributes> steps,
        SystemSnapshot system,
        string operatingSystem,
        JournalMemoryInfo memory,
        ulong availableBytes,
        ulong plannedBytes)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run id must not be empty.", nameof(runId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logBaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(testType);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(memory);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A plan must contain at least one step.", nameof(steps));
        }

        var journalSteps = new JournalStep[steps.Count];
        for (int position = 0; position < steps.Count; position++)
        {
            StepAttributes step = steps[position]
                ?? throw new ArgumentException($"Step {position} is null.", nameof(steps));
            if (step.Index != position)
            {
                throw new ArgumentException(
                    $"Step '{step.Name}' at position {position} has index {step.Index}; "
                    + "every step's index must equal its position.",
                    nameof(steps));
            }

            journalSteps[position] = JournalStep.FromAttributes(step);
        }

        return new JournalBegin
        {
            RunId = runId,
            LogBaseName = logBaseName,
            TestType = testType,
            StopOnError = stopOnError,
            StartRequestedUtc = startRequestedUtc,
            AppVersion = appVersion,
            Steps = journalSteps,
            System = JournalSystemInfo.FromSnapshot(system, operatingSystem),
            Memory = memory,
            AvailableBytes = availableBytes,
            PlannedBytes = plannedBytes,
        };
    }

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing <see cref="Steps"/>
    /// element by element.
    /// </summary>
    /// <param name="other">The record to compare with.</param>
    /// <returns><see langword="true"/> when every value, including <see cref="JournalRecord.Utc"/>, is equal.</returns>
    public bool Equals(JournalBegin? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && base.Equals(other)
            && RunId == other.RunId
            && string.Equals(LogBaseName, other.LogBaseName, StringComparison.Ordinal)
            && string.Equals(TestType, other.TestType, StringComparison.Ordinal)
            && StopOnError == other.StopOnError
            && StartRequestedUtc == other.StartRequestedUtc
            && string.Equals(AppVersion, other.AppVersion, StringComparison.Ordinal)
            && Steps.SequenceEqual(other.Steps)
            && System == other.System
            && Memory == other.Memory
            && AvailableBytes == other.AvailableBytes
            && PlannedBytes == other.PlannedBytes);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(JournalBegin)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(base.GetHashCode());
        hash.Add(RunId);
        hash.Add(LogBaseName, StringComparer.Ordinal);
        hash.Add(TestType, StringComparer.Ordinal);
        hash.Add(StopOnError);
        hash.Add(StartRequestedUtc);
        hash.Add(AppVersion, StringComparer.Ordinal);
        foreach (JournalStep step in Steps)
        {
            hash.Add(step);
        }

        hash.Add(System);
        hash.Add(Memory);
        hash.Add(AvailableBytes);
        hash.Add(PlannedBytes);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One plan step as captured in the <c>begin</c> record: the step attributes recovery needs to rebuild the
/// summary's <c>steps[]</c>.
/// </summary>
/// <remarks>
/// Not a journal line of its own. Durations are whole milliseconds and enumerations are written by member
/// name, <see cref="Mechanisms"/> as a comma-separated list of flags.
/// </remarks>
public sealed record JournalStep
{
    // See JournalBegin: the init accessor keeps the name non-null when a line omits it.
    private readonly string _name = string.Empty;

    /// <summary>
    /// Gets the step's 1-based number in its plan: <see cref="StepAttributes.Index"/> + 1.
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    /// Gets the step's name, exactly as the plan defines it.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the data pattern the step writes and verifies.
    /// </summary>
    public PatternKind Pattern { get; init; }

    /// <summary>
    /// Gets the order in which the step visits each worker's slice.
    /// </summary>
    public AccessOrder AccessOrder { get; init; }

    /// <summary>
    /// Gets how many workers the step keeps active.
    /// </summary>
    public ThreadMode ThreadMode { get; init; }

    /// <summary>
    /// Gets the idle hold between writing and verifying, in whole milliseconds; 0 for a windowed step.
    /// </summary>
    public long HoldMs { get; init; }

    /// <summary>
    /// Gets the step's time budget, in whole milliseconds.
    /// </summary>
    public long TimeBudgetMs { get; init; }

    /// <summary>
    /// Gets the step's coverage budget as a percentage of the tested bytes.
    /// </summary>
    public int CoverageBudgetPercent { get; init; }

    /// <summary>
    /// Gets the single mechanism the step chiefly targets.
    /// </summary>
    public InstabilityMechanism PrimaryMechanism { get; init; }

    /// <summary>
    /// Gets every mechanism the step targets, as flags.
    /// </summary>
    public InstabilityMechanism Mechanisms { get; init; }

    /// <summary>
    /// Captures a plan step, converting its 0-based index to a 1-based number and its durations to whole
    /// milliseconds.
    /// </summary>
    /// <param name="attributes">The step as planned.</param>
    /// <returns>The captured step.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attributes"/> is <see langword="null"/>.</exception>
    public static JournalStep FromAttributes(StepAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return new JournalStep
        {
            Number = attributes.Index + 1,
            Name = attributes.Name,
            Pattern = attributes.Pattern,
            AccessOrder = attributes.Order,
            ThreadMode = attributes.Threads,
            HoldMs = JournalValues.WholeMilliseconds(attributes.Hold),
            TimeBudgetMs = JournalValues.WholeMilliseconds(attributes.TimeBudget),
            CoverageBudgetPercent = attributes.CoverageBudgetPercent,
            PrimaryMechanism = attributes.PrimaryMechanism,
            Mechanisms = attributes.Mechanisms,
        };
    }
}

/// <summary>
/// The system snapshot captured in the <c>begin</c> record, the source of the summary's <c>system.*</c>
/// fields.
/// </summary>
public sealed record JournalSystemInfo
{
    // See JournalBegin: the init accessors keep the strings non-null when a line omits them.
    private readonly string _os = string.Empty;
    private readonly string _cpuName = string.Empty;

    /// <summary>
    /// Gets the operating-system description captured at Start.
    /// </summary>
    public string Os
    {
        get => _os;
        init => _os = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the CPU brand string.
    /// </summary>
    public string CpuName
    {
        get => _cpuName;
        init => _cpuName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the number of logical processors across all processor groups.
    /// </summary>
    public int LogicalProcessors { get; init; }

    /// <summary>
    /// Gets the number of processor groups.
    /// </summary>
    public int ProcessorGroups { get; init; }

    /// <summary>
    /// Gets the best instruction set the CPU and the OS both support. The set the session actually selected
    /// is <see cref="JournalPrepared.IsaSelected"/>.
    /// </summary>
    public InstructionSet BestIsa { get; init; }

    /// <summary>
    /// Gets the total physical memory at Start, in bytes.
    /// </summary>
    public ulong TotalPhysicalBytes { get; init; }

    /// <summary>
    /// Gets the physical memory available when the snapshot was taken, in bytes.
    /// </summary>
    public ulong AvailablePhysicalBytes { get; init; }

    /// <summary>
    /// Gets the large-page granularity, in bytes, or 0 when large pages are unsupported.
    /// </summary>
    public ulong LargePageMinimum { get; init; }

    /// <summary>
    /// Gets a value indicating whether the process token already held <c>SeLockMemoryPrivilege</c>.
    /// </summary>
    public bool LockPrivilegeHeld { get; init; }

    /// <summary>
    /// Captures an engine system snapshot together with the operating-system description, which the
    /// snapshot does not carry.
    /// </summary>
    /// <param name="snapshot">The engine's system snapshot.</param>
    /// <param name="operatingSystem">The operating-system description captured by the caller.</param>
    /// <returns>The captured system information.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operatingSystem"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public static JournalSystemInfo FromSnapshot(SystemSnapshot snapshot, string operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        return new JournalSystemInfo
        {
            Os = operatingSystem,
            CpuName = snapshot.CpuName,
            LogicalProcessors = snapshot.LogicalProcessors,
            ProcessorGroups = snapshot.ProcessorGroups,
            BestIsa = snapshot.BestIsa,
            TotalPhysicalBytes = snapshot.TotalPhysicalBytes,
            AvailablePhysicalBytes = snapshot.AvailablePhysicalBytes,
            LargePageMinimum = snapshot.LargePageMinimum,
            LockPrivilegeHeld = snapshot.LockPrivilegeHeld,
        };
    }
}

/// <summary>
/// The detected-memory summary captured in the <c>begin</c> record, the source of the summary's
/// <c>memory.installedBytes</c>, <c>type</c>, <c>speedMTs</c>, <c>channels</c> and <c>dimms</c>.
/// </summary>
/// <remarks>
/// <c>RunController</c> maps it from the SMBIOS memory inventory, which this library does not reference.
/// A field SMBIOS did not provide stays <see langword="null"/> and is written as <c>null</c> in the summary.
/// </remarks>
public sealed record JournalMemoryInfo
{
    /// <summary>
    /// Gets the installed memory, in bytes, or <see langword="null"/> when unknown.
    /// </summary>
    public ulong? InstalledBytes { get; init; }

    /// <summary>
    /// Gets the DRAM generation of the installed modules.
    /// </summary>
    public MemoryType Type { get; init; }

    /// <summary>
    /// Gets the configured memory speed in MT/s, or <see langword="null"/> when unknown.
    /// </summary>
    public int? SpeedMTs { get; init; }

    /// <summary>
    /// Gets the number of memory channels, or <see langword="null"/> when the SMBIOS locators could not be
    /// parsed.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Gets the number of populated DIMMs, or <see langword="null"/> when the record did not capture it; a
    /// captured 0 stays 0.
    /// </summary>
    public int? Dimms { get; init; }
}

/// <summary>
/// The <c>prepared</c> record: what the engine reported when preparation finished. Written on Prepared,
/// without acknowledgment.
/// </summary>
/// <remarks>
/// Its <see cref="JournalRecord.Utc"/> is the time the run entered Running, which becomes the summary's
/// <c>run.startedUtc</c>. A journal without this record crashed while preparing memory.
/// </remarks>
public sealed record JournalPrepared : JournalRecord
{
    /// <summary>
    /// Gets the bytes actually locked and tested, <c>slice × workers</c>, after any shrink-and-retry.
    /// </summary>
    public ulong TestedBytes { get; init; }

    /// <summary>
    /// Gets the number of pinned workers.
    /// </summary>
    public int Workers { get; init; }

    /// <summary>
    /// Gets the kind of pages backing the region.
    /// </summary>
    public PageMode PageMode { get; init; }

    /// <summary>
    /// Gets the instruction set the session selected for its kernels.
    /// </summary>
    public InstructionSet IsaSelected { get; init; }

    /// <summary>
    /// Gets the kind of address error records carry; <see cref="AddressKind.Virtual"/> in v1.
    /// </summary>
    public AddressKind AddressKind { get; init; }

    /// <summary>
    /// Gets the time preparation took, in whole milliseconds.
    /// </summary>
    public long PreparationMs { get; init; }
}

/// <summary>
/// The <c>step</c> record: a step is about to start. Acknowledged: the engine coordinator waits until it
/// is durable before the step's memory traffic begins, so a hang in the step's first moments is attributed
/// to this step and not the previous one.
/// </summary>
/// <remarks>
/// The AAP example line is
/// <c>{"t":"step","index":3,"count":6,"name":"Read-after-write turnaround","utc":"2026-10-28T14:05:51.120Z"}</c>.
/// </remarks>
public sealed record JournalStepStart : JournalRecord
{
    // See JournalBegin: the init accessor keeps the name non-null when a line omits it.
    private readonly string _name = string.Empty;

    /// <summary>
    /// Gets the step's 1-based number, as in "step 3 of 6": Core's 0-based step index + 1. JSON
    /// <c>"index"</c>.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets the number of steps in the plan.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Gets the step's name.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }
}

/// <summary>
/// The <c>stepEnd</c> record: what the engine measured for one finished or interrupted step. Written on
/// StepFinished, without acknowledgment.
/// </summary>
public sealed record JournalStepEnd : JournalRecord
{
    /// <summary>
    /// Gets the id of the run that measured the step, as its <c>begin</c> record holds it. JSON
    /// <c>"runId"</c>. <c>ProgressJournal</c> stamps it, so recovery attaches the record only to that run;
    /// <see cref="FromOutcome"/> leaves it <see cref="Guid.Empty"/>.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Gets the step's 1-based number: Core's 0-based step index + 1.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets the time the step actually ran, in whole milliseconds.
    /// </summary>
    public long ActualMs { get; init; }

    /// <summary>
    /// Gets the number of passes the step finished.
    /// </summary>
    public int Passes { get; init; }

    /// <summary>
    /// Gets the bytes the step verified.
    /// </summary>
    public ulong VerifiedBytes { get; init; }

    /// <summary>
    /// Gets the true number of errors the step detected, from the engine's counters.
    /// </summary>
    public ulong Errors { get; init; }

    /// <summary>
    /// Gets a value indicating whether the step ran to the end of its schedule.
    /// </summary>
    public bool Completed { get; init; }

    /// <summary>
    /// Gets how far the step ran past its time budget, in whole milliseconds; 0 when it stayed within it.
    /// </summary>
    public long OverrunMs { get; init; }

    /// <summary>
    /// Captures a step outcome, converting its 0-based index to a 1-based number and its durations to whole
    /// milliseconds.
    /// </summary>
    /// <param name="outcome">The step outcome reported by the engine.</param>
    /// <returns>The record, with <see cref="JournalRecord.Utc"/> left for the journal to stamp.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    public static JournalStepEnd FromOutcome(StepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new JournalStepEnd
        {
            Index = outcome.Attributes.Index + 1,
            ActualMs = JournalValues.WholeMilliseconds(outcome.ActualDuration),
            Passes = outcome.PassesCompleted,
            VerifiedBytes = outcome.VerifiedBytes,
            Errors = outcome.Errors,
            Completed = outcome.Completed,
            OverrunMs = JournalValues.WholeMilliseconds(outcome.Overrun),
        };
    }
}


/// <summary>
/// The <c>beat</c> record: a 1 Hz heartbeat from the latest progress snapshot and sensor readings. Written
/// by the journal's writer thread without acknowledgment, from the first progress snapshot until the
/// <c>result</c> record.
/// </summary>
/// <remarks>
/// <para>
/// The last heartbeat gives an unfinished run's approximate end time, duration, coverage and error total;
/// every heartbeat contributes to its peak temperatures. The AAP example line is
/// <c>{"t":"beat","step":3,"elapsedMs":104250,"coverage":412.6,"errors":0,"cpuC":71.5,"ramC":48.0,"utc":"…"}</c>.
/// </para>
/// </remarks>
public sealed record JournalBeat : JournalRecord
{
    // See JournalBegin: the init accessors keep the statuses non-null when a line omits them. "Unknown"
    // rather than an empty string, because the summary ranks an unrecognised status as a failure.
    private readonly string _cpuStatus = JournalReadings.UnknownStatus;
    private readonly string _ramStatus = JournalReadings.UnknownStatus;

    /// <summary>
    /// Gets the current step's 1-based number: Core's 0-based step index + 1. JSON <c>"step"</c>.
    /// </summary>
    public int Step { get; init; }

    /// <summary>
    /// Gets the time elapsed since the run entered Running, in whole milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Gets the coverage so far, as an unrounded percentage of the tested bytes.
    /// </summary>
    public double Coverage { get; init; }

    /// <summary>
    /// Gets the true number of errors detected so far, from the engine's counters.
    /// </summary>
    public ulong Errors { get; init; }

    /// <summary>
    /// Gets the CPU temperature in degrees Celsius, or <see langword="null"/> unless that reading was
    /// available and fresh.
    /// </summary>
    public double? CpuC { get; init; }

    /// <summary>
    /// Gets the RAM temperature in degrees Celsius (the maximum across DIMMs), or <see langword="null"/>
    /// unless that reading was available and fresh.
    /// </summary>
    public double? RamC { get; init; }

    /// <summary>
    /// Gets the CPU reading's status, a <c>RamQuick.Sensors.SensorStatus</c> member name, or
    /// <see cref="JournalReadings.UnknownStatus"/> when no reading had been published.
    /// </summary>
    public string CpuStatus
    {
        get => _cpuStatus;
        init => _cpuStatus = value ?? JournalReadings.UnknownStatus;
    }

    /// <summary>
    /// Gets the RAM reading's status, a <c>RamQuick.Sensors.SensorStatus</c> member name, or
    /// <see cref="JournalReadings.UnknownStatus"/> when no reading had been published.
    /// </summary>
    public string RamStatus
    {
        get => _ramStatus;
        init => _ramStatus = value ?? JournalReadings.UnknownStatus;
    }

    /// <summary>
    /// Builds a heartbeat from a progress snapshot and the latest sensor readings, converting the 0-based
    /// step index to a 1-based number.
    /// </summary>
    /// <param name="progress">The latest progress snapshot.</param>
    /// <param name="readings">
    /// The latest sensor readings, or <see langword="null"/> when none has been published yet, which
    /// records <see cref="JournalReadings.Unknown"/>.
    /// </param>
    /// <returns>The record, with <see cref="JournalRecord.Utc"/> left for the journal to stamp.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is <see langword="null"/>.</exception>
    public static JournalBeat From(RunProgress progress, JournalReadings? readings)
    {
        ArgumentNullException.ThrowIfNull(progress);
        JournalReadings latest = readings ?? JournalReadings.Unknown;
        return new JournalBeat
        {
            Step = progress.StepIndex + 1,
            ElapsedMs = JournalValues.WholeMilliseconds(progress.Elapsed),
            Coverage = progress.CoveragePercent,
            Errors = progress.TotalErrors,
            CpuC = latest.CpuCelsius,
            RamC = latest.RamCelsius,
            CpuStatus = latest.CpuStatus,
            RamStatus = latest.RamStatus,
        };
    }
}

/// <summary>
/// The latest CPU and RAM readings as the journal records them in heartbeats. An input only:
/// <c>RunController</c> publishes it to <c>ProgressJournal.PublishReadings</c> whenever the sensor monitor
/// posts, and it is never a journal line itself.
/// </summary>
/// <remarks>
/// <c>RunController</c> maps it from <c>RamQuick.Sensors.SensorReadings</c>, which this library does not
/// reference: a temperature is present only when its reading is available and fresh, and a status is the
/// <c>SensorStatus</c> member name.
/// </remarks>
public sealed record JournalReadings
{
    /// <summary>
    /// The status recorded when no sensor reading has been published yet.
    /// </summary>
    public const string UnknownStatus = "Unknown";

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalReadings"/> class.
    /// </summary>
    /// <param name="cpuCelsius">
    /// The CPU temperature in degrees Celsius, or <see langword="null"/> unless the reading is available
    /// and fresh.
    /// </param>
    /// <param name="cpuStatus">The CPU reading's status name.</param>
    /// <param name="ramCelsius">
    /// The RAM temperature in degrees Celsius, or <see langword="null"/> unless the reading is available
    /// and fresh.
    /// </param>
    /// <param name="ramStatus">The RAM reading's status name.</param>
    /// <exception cref="ArgumentOutOfRangeException">A temperature is NaN or infinite.</exception>
    /// <exception cref="ArgumentException">A status is <see langword="null"/>, empty or white space.</exception>
    public JournalReadings(double? cpuCelsius, string cpuStatus, double? ramCelsius, string ramStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cpuStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(ramStatus);

        // The journal's JSON writer rejects NaN and infinities, so a non-finite value would fail every
        // heartbeat write; it is refused here, where the value enters the journal.
        CpuCelsius = RequireFinite(cpuCelsius, nameof(cpuCelsius));
        RamCelsius = RequireFinite(ramCelsius, nameof(ramCelsius));
        CpuStatus = cpuStatus;
        RamStatus = ramStatus;
    }

    /// <summary>
    /// Gets the readings recorded before any sensor reading has been published: no temperatures, and
    /// <see cref="UnknownStatus"/> for both sensors.
    /// </summary>
    public static JournalReadings Unknown { get; } = new(null, UnknownStatus, null, UnknownStatus);

    /// <summary>
    /// Gets the CPU temperature in degrees Celsius, or <see langword="null"/> when unavailable or stale.
    /// </summary>
    public double? CpuCelsius { get; }

    /// <summary>
    /// Gets the CPU reading's status name.
    /// </summary>
    public string CpuStatus { get; }

    /// <summary>
    /// Gets the RAM temperature in degrees Celsius, or <see langword="null"/> when unavailable or stale.
    /// </summary>
    public double? RamCelsius { get; }

    /// <summary>
    /// Gets the RAM reading's status name.
    /// </summary>
    public string RamStatus { get; }

    private static double? RequireFinite(double? celsius, string parameterName)
    {
        if (celsius is { } value && !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A temperature must be a finite number.");
        }

        return celsius;
    }
}

/// <summary>
/// The <c>result</c> record: the run's verdict and true totals. Acknowledged, and written immediately after
/// the terminal transition. Its presence tells recovery that the run finished and only its publication may
/// be missing.
/// </summary>
public sealed record JournalResult : JournalRecord
{
    // See JournalBegin: the init accessors keep the verdict and the detail non-null when a line omits them.
    private readonly string _verdict = string.Empty;
    private readonly string _endDetail = string.Empty;

    /// <summary>
    /// Gets the id of the run that reached the verdict, as its <c>begin</c> record holds it. JSON
    /// <c>"runId"</c>. <c>ProgressJournal</c> stamps it, so recovery takes the verdict only for that run and
    /// never from a record another run, or a tampered journal, left beside it; <see cref="Create"/> leaves
    /// it <see cref="Guid.Empty"/>.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Gets the terminal state the run reached: <see cref="RunState.Passed"/>, <see cref="RunState.Failed"/>
    /// or <see cref="RunState.Aborted"/>.
    /// </summary>
    public RunState TerminalState { get; init; }

    /// <summary>
    /// Gets the verdict text, <c>"PASS"</c>, <c>"FAIL"</c> or <c>"ABORTED"</c>, derived from
    /// <see cref="TerminalState"/>.
    /// </summary>
    public string Verdict
    {
        get => _verdict;
        init => _verdict = value ?? string.Empty;
    }

    /// <summary>
    /// Gets why the run reached its terminal state.
    /// </summary>
    public RunEndReason EndReason { get; init; }

    /// <summary>
    /// Gets the diagnostic detail of the end reason, which becomes the summary's <c>run.endDetail</c>: empty
    /// when the end reason has no diagnostic, as for <see cref="RunEndReason.Completed"/>, and never
    /// <see langword="null"/>.
    /// </summary>
    public string EndDetail
    {
        get => _endDetail;
        init => _endDetail = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the true error total from the engine's counters, never derived from stored records.
    /// </summary>
    public ulong TotalErrors { get; init; }

    /// <summary>
    /// Gets the bytes verified over the whole run.
    /// </summary>
    public ulong VerifiedBytes { get; init; }

    /// <summary>
    /// Gets the engine's test time at completion, in whole milliseconds: measured on its monotonic clock
    /// from the end of preparation (entering Running) to Completed, and 0 when preparation failed. It is
    /// the summary's <c>run.durationSeconds</c>, which no wall-clock correction can skew.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Gets the time into the test of the first error, in whole milliseconds, or <see langword="null"/>
    /// when there was no error.
    /// </summary>
    public long? FirstErrorMs { get; init; }

    /// <summary>
    /// Gets the number of steps that ran to the end of their schedule.
    /// </summary>
    public int StepsCompleted { get; init; }

    /// <summary>
    /// Gets the UTC time the engine reported Completed, which becomes the summary's <c>run.endedUtc</c>.
    /// </summary>
    public DateTimeOffset CompletedUtc { get; init; }

    /// <summary>
    /// Gets the UTC time the terminal state was shown, which becomes the summary's <c>run.verdictUtc</c>.
    /// </summary>
    public DateTimeOffset VerdictUtc { get; init; }

    /// <summary>
    /// Creates the <c>result</c> record from the terminal state just reached and the engine's totals.
    /// </summary>
    /// <param name="terminalState">
    /// The state the run machine just reached, passed in by the caller rather than cached by the journal.
    /// </param>
    /// <param name="endReason">Why the run reached that state.</param>
    /// <param name="endDetail">
    /// The end reason's diagnostic detail; <see langword="null"/> or empty when there is none, which
    /// <see cref="EndDetail"/> holds as empty. It is recorded through
    /// <see cref="SummaryFormat.RedactPersonalPaths(string)"/>, because it can quote an exception message naming a
    /// path in the user's profile, and the record feeds the shareable summaries.
    /// </param>
    /// <param name="totalErrors">The true error total from the engine's counters.</param>
    /// <param name="verifiedBytes">The bytes verified over the whole run.</param>
    /// <param name="elapsed">The engine's elapsed run time.</param>
    /// <param name="firstError">The time into the test of the first error, or <see langword="null"/>.</param>
    /// <param name="stepsCompleted">The number of steps that ran to the end of their schedule.</param>
    /// <param name="completedUtc">The UTC time the engine reported Completed.</param>
    /// <param name="verdictUtc">The UTC time the terminal state was shown.</param>
    /// <returns>The record, with <see cref="JournalRecord.Utc"/> left for the journal to stamp.</returns>
    /// <exception cref="ArgumentException"><paramref name="terminalState"/> is not a terminal state.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="elapsed"/> or <paramref name="firstError"/> is negative, or
    /// <paramref name="stepsCompleted"/> is negative.
    /// </exception>
    public static JournalResult Create(
        RunState terminalState,
        RunEndReason endReason,
        string? endDetail,
        ulong totalErrors,
        ulong verifiedBytes,
        TimeSpan elapsed,
        TimeSpan? firstError,
        int stepsCompleted,
        DateTimeOffset completedUtc,
        DateTimeOffset verdictUtc)
    {
        string verdict = (terminalState.IsTerminal() ? terminalState.ToVerdictText() : null)
            ?? throw new ArgumentException(
                $"The result record needs a terminal state, not {terminalState}.", nameof(terminalState));
        RequireNonNegative(elapsed, nameof(elapsed));
        if (firstError is { } firstErrorTime)
        {
            RequireNonNegative(firstErrorTime, nameof(firstError));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(stepsCompleted);

        return new JournalResult
        {
            TerminalState = terminalState,
            Verdict = verdict,
            EndReason = endReason,
            EndDetail = endDetail is null ? string.Empty : SummaryFormat.RedactPersonalPaths(endDetail),
            TotalErrors = totalErrors,
            VerifiedBytes = verifiedBytes,
            ElapsedMs = JournalValues.WholeMilliseconds(elapsed),
            FirstErrorMs = firstError is { } time ? JournalValues.WholeMilliseconds(time) : null,
            StepsCompleted = stepsCompleted,
            CompletedUtc = completedUtc,
            VerdictUtc = verdictUtc,
        };
    }

    private static void RequireNonNegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A duration must not be negative.");
        }
    }
}

/// <summary>
/// The <c>released</c> record: the engine released the locked region. Written on Released, before the
/// summaries are published, without acknowledgment.
/// </summary>
/// <remarks>
/// Recovery uses it when present: <see cref="ReleaseMs"/> becomes the summary's
/// <c>memory.releaseSeconds</c>, which is <c>null</c> when the journal holds no <c>released</c> record.
/// </remarks>
public sealed record JournalReleased : JournalRecord
{
    /// <summary>
    /// Gets the id of the run whose memory was released, as its <c>begin</c> record holds it. JSON
    /// <c>"runId"</c>. <c>ProgressJournal</c> stamps it, so recovery attaches the record only to that run.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Gets the bytes the engine released.
    /// </summary>
    public ulong ReleasedBytes { get; init; }

    /// <summary>
    /// Gets the time the release took, in whole milliseconds.
    /// </summary>
    public long ReleaseMs { get; init; }
}

/// <summary>
/// The <c>end</c> record: both summaries of the run are published. Acknowledged, and written after
/// publication; the journal is deleted next. A journal holding this record needs no recovery.
/// </summary>
public sealed record JournalEnd : JournalRecord
{
    /// <summary>
    /// Gets the id of the run whose summaries were published.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the summaries were published; every writer sets it to
    /// <see langword="true"/>.
    /// </summary>
    public bool Published { get; init; }
}

/// <summary>
/// Value conversions and collection snapshots shared by the journal and summary records and their
/// writers.
/// </summary>
internal static class JournalValues
{
    /// <summary>
    /// Converts <paramref name="value"/> to whole milliseconds, truncating any sub-millisecond part, so
    /// every persisted duration has one exact integer representation.
    /// </summary>
    /// <param name="value">The duration to convert.</param>
    /// <returns>The duration in whole milliseconds.</returns>
    internal static long WholeMilliseconds(TimeSpan value) => value.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Takes the read-only snapshot a list-valued init accessor stores: the elements of
    /// <paramref name="items"/> copied into a private array, exposed through a wrapper that cannot be cast
    /// back to a writable array or list.
    /// </summary>
    /// <remarks>
    /// Records are enqueued, compared, hashed and serialized on other threads after construction, so a
    /// record must never share a collection its caller can still change. The factories, object
    /// initializers, <see langword="with"/> expressions that set a list, and System.Text.Json
    /// deserialization all assign through the init accessors that call this; a <see langword="with"/> copy
    /// that leaves a list unset shares the original's snapshot, which nothing can change. Elements are
    /// copied as they are, <see langword="null"/> included, so a damaged document still parses and the
    /// readers that already skip null elements keep doing so.
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The supplied elements, or <see langword="null"/>.</param>
    /// <returns>
    /// The read-only copy, in the supplied order; the shared empty array, which has no element to change,
    /// when <paramref name="items"/> is <see langword="null"/> or empty.
    /// </returns>
    internal static IReadOnlyList<T> ReadOnlySnapshot<T>(IEnumerable<T>? items)
    {
        if (items is null)
        {
            return Array.Empty<T>();
        }

        T[] copy = items.ToArray();
        return copy.Length == 0 ? Array.Empty<T>() : Array.AsReadOnly(copy);
    }
}
