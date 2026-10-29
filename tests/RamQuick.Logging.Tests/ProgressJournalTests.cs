using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using RamQuick.Core;
using Xunit;

namespace RamQuick.Logging.Tests;

/// <summary>
/// Deterministic journal and summary inputs shared by the Logging tests (ProgressJournalTests,
/// CrashDetectorTests, ResultsLogTests): two fictitious machines, the Quick plan's six steps and
/// builders for every journal record.
/// </summary>
/// <remarks>
/// Nothing here reads the real machine, the clock or a random source, so a recovered summary that
/// shows any of these values can only have taken it from the journal. Machine A's values are
/// deliberately unlike any build host; machine B differs from A in every captured field. Step
/// indices are 0-based, as in <c>RamQuick.Core</c>; the journal persists them 1-based.
/// </remarks>
internal static class JournalSamples
{
    /// <summary>One gibibyte, the default tested region.</summary>
    public const ulong GiB = 1UL << 30;

    /// <summary>The operating-system description captured for machine A.</summary>
    public const string OsA = "Machine A OS 10.0.99999";

    /// <summary>The operating-system description captured for machine B.</summary>
    public const string OsB = "Machine B OS 10.0.88888";

    /// <summary>The product version every sample <c>begin</c> record carries.</summary>
    public const string AppVersion = "1.0.0-test";

    /// <summary>The test type every sample <c>begin</c> record carries.</summary>
    public const string QuickTestType = "Quick";

    /// <summary>The <c>SensorStatus</c> name of a fresh reading.</summary>
    public const string AvailableStatus = "Available";

    /// <summary>The <c>SensorStatus</c> name of a reading the missing PawnIO driver prevents.</summary>
    public const string DriverUnavailableStatus = "DriverUnavailable";

    /// <summary>The address of <see cref="ErrorRecords(int)"/>'s first record.</summary>
    public const ulong FirstErrorAddress = 0x0000023A4F7C1D48;

    /// <summary>The region offset of <see cref="ErrorRecords(int)"/>'s first record, inside 1 GiB.</summary>
    public const ulong FirstErrorRegionOffset = 0x000000000C7C1D48;

    /// <summary>The value <see cref="ErrorRecords(int)"/>'s first record expected.</summary>
    public const ulong FirstErrorExpected = 0x5A3C5A3C5A3C5A3C;

    /// <summary>The value <see cref="ErrorRecords(int)"/>'s first record read: bit 49 flipped.</summary>
    public const ulong FirstErrorActual = 0x5A3E5A3C5A3C5A3C;

    // Each completed-run step verifies the whole region once per pass, so its coverage is exact.
    private const int CompletedRunPasses = 4;

    /// <summary>The fixed start of every fake clock, the AAP's example journal time.</summary>
    public static readonly DateTimeOffset StartUtc = new(2026, 10, 28, 14, 5, 51, 120, TimeSpan.Zero);

    /// <summary>The run id of machine A's sample run.</summary>
    public static readonly Guid RunIdA = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    /// <summary>The run id of machine B's sample run.</summary>
    public static readonly Guid RunIdB = new("9b2d7c1a-6e4f-4a8b-8c3d-5f6e7a8b9c0d");

    // This project does not reference RamQuick.Plans, so the Quick steps are restated as data:
    // names, patterns, orders, holds, budgets and mechanisms exactly as TestPlans.cs defines them.
    private static readonly StepAttributes[] _quickSteps =
    {
        QuickStep(
            0,
            "High-toggle sweep",
            PatternKind.HighToggle,
            AccessOrder.SequentialAscending,
            TimeSpan.Zero,
            40,
            25,
            InstabilityMechanism.SignalIntegrity,
            InstabilityMechanism.SignalIntegrity | InstabilityMechanism.VddVddqVoltage | InstabilityMechanism.RefreshTimings),
        QuickStep(
            1,
            "Random bank thrash",
            PatternKind.Random,
            AccessOrder.BankStride,
            TimeSpan.Zero,
            50,
            35,
            InstabilityMechanism.RowTimings,
            InstabilityMechanism.RowTimings | InstabilityMechanism.ActivationWindow
                | InstabilityMechanism.BankRankChannelSwitching),
        QuickStep(
            2,
            "Read-after-write turnaround",
            PatternKind.Random,
            AccessOrder.Turnaround,
            TimeSpan.Zero,
            50,
            35,
            InstabilityMechanism.ReadWriteTurnaround,
            InstabilityMechanism.ReadWriteTurnaround | InstabilityMechanism.WriteTimings),
        QuickStep(
            3,
            "Moving inversion, walking bits",
            PatternKind.MovingInversionWalking,
            AccessOrder.SequentialAscending,
            TimeSpan.Zero,
            60,
            50,
            InstabilityMechanism.VddVddqVoltage,
            InstabilityMechanism.VddVddqVoltage),
        QuickStep(
            4,
            "Refresh hold",
            PatternKind.Random,
            AccessOrder.RandomBlocks,
            TimeSpan.FromSeconds(5),
            50,
            20,
            InstabilityMechanism.RefreshTimings,
            InstabilityMechanism.RefreshTimings | InstabilityMechanism.TemperatureSensitivity),
        QuickStep(
            5,
            "Full-load random mix",
            PatternKind.Random,
            AccessOrder.RandomBlocks,
            TimeSpan.Zero,
            50,
            35,
            InstabilityMechanism.SocImcVoltage,
            InstabilityMechanism.SocImcVoltage | InstabilityMechanism.BankRankChannelSwitching),
    };

    /// <summary>Creates a fake clock standing at <see cref="StartUtc"/>.</summary>
    public static FakeTimeProvider CreateTimeProvider() => new(StartUtc);

    /// <summary>Machine A: 7 logical processors in one group, AVX2, 32 GiB.</summary>
    public static SystemSnapshot SystemA() => new()
    {
        CpuName = "Machine A Test CPU",
        LogicalProcessors = 7,
        ProcessorGroups = 1,
        BestIsa = InstructionSet.Avx2,
        TotalPhysicalBytes = 34_359_738_368,
        AvailablePhysicalBytes = 30_000_000_000,
        LargePageMinimum = 2_097_152,
        LockPrivilegeHeld = false,
    };

    /// <summary>Machine B: 64 logical processors in two groups, AVX-512, 128 GiB.</summary>
    public static SystemSnapshot SystemB() => new()
    {
        CpuName = "Machine B Test CPU",
        LogicalProcessors = 64,
        ProcessorGroups = 2,
        BestIsa = InstructionSet.Avx512,
        TotalPhysicalBytes = 137_438_953_472,
        AvailablePhysicalBytes = 120_000_000_000,
        LargePageMinimum = 2_097_152,
        LockPrivilegeHeld = true,
    };

    /// <summary>Machine A's memory: 32 GiB of DDR5-6000 in two DIMMs on two channels.</summary>
    public static JournalMemoryInfo MemoryA() => new()
    {
        InstalledBytes = 34_359_738_368,
        Type = MemoryType.DDR5,
        SpeedMTs = 6000,
        Channels = 2,
        Dimms = 2,
    };

    /// <summary>Machine B's memory: 128 GiB of DDR4-3200 in eight DIMMs on four channels.</summary>
    public static JournalMemoryInfo MemoryB() => new()
    {
        InstalledBytes = 137_438_953_472,
        Type = MemoryType.DDR4,
        SpeedMTs = 3200,
        Channels = 4,
        Dimms = 8,
    };

    /// <summary>
    /// Returns the first <paramref name="count"/> Quick steps, indexed from 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not 1 to 6.</exception>
    public static IReadOnlyList<StepAttributes> QuickSteps(int count = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _quickSteps.Length);
        return _quickSteps.Take(count).ToArray();
    }

    /// <summary>
    /// Returns the results base name <c>ResultsLog.OpenRun</c> reserves, formatting
    /// <paramref name="localStart"/> in its own offset so the name is the same on every host.
    /// </summary>
    public static string BaseName(Guid runId, DateTimeOffset localStart, string testType = QuickTestType) =>
        "RamQuick-"
        + localStart.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + "-" + testType
        + "-" + runId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a Quick <c>begin</c> record through <see cref="JournalBegin.Create"/>; the available bytes are
    /// <paramref name="system"/>'s. <see cref="JournalRecord.Utc"/> is left for the journal to stamp.
    /// </summary>
    public static JournalBegin Begin(
        Guid runId,
        string logBaseName,
        SystemSnapshot system,
        string operatingSystem,
        JournalMemoryInfo memory,
        DateTimeOffset startRequestedUtc,
        IReadOnlyList<StepAttributes>? steps = null,
        bool stopOnError = true,
        ulong plannedBytes = GiB)
    {
        ArgumentNullException.ThrowIfNull(system);
        return JournalBegin.Create(
            runId,
            logBaseName,
            QuickTestType,
            stopOnError,
            startRequestedUtc,
            AppVersion,
            steps ?? QuickSteps(),
            system,
            operatingSystem,
            memory,
            system.AvailablePhysicalBytes,
            plannedBytes);
    }

    /// <summary>Machine A's Quick <c>begin</c> record, run <see cref="RunIdA"/>.</summary>
    public static JournalBegin BeginA(DateTimeOffset startRequestedUtc, int stepCount = 6) =>
        Begin(RunIdA, BaseName(RunIdA, startRequestedUtc), SystemA(), OsA, MemoryA(), startRequestedUtc, QuickSteps(stepCount));

    /// <summary>Machine B's Quick <c>begin</c> record, run <see cref="RunIdB"/>.</summary>
    public static JournalBegin BeginB(DateTimeOffset startRequestedUtc, int stepCount = 6) =>
        Begin(RunIdB, BaseName(RunIdB, startRequestedUtc), SystemB(), OsB, MemoryB(), startRequestedUtc, QuickSteps(stepCount));

    /// <summary>A <c>prepared</c> record for a virtual-address region.</summary>
    public static JournalPrepared Prepared(
        ulong testedBytes = GiB,
        int workers = 7,
        PageMode pageMode = PageMode.Standard,
        InstructionSet isaSelected = InstructionSet.Avx2,
        long preparationMs = 1_250)
    {
        return new JournalPrepared
        {
            TestedBytes = testedBytes,
            Workers = workers,
            PageMode = pageMode,
            IsaSelected = isaSelected,
            AddressKind = AddressKind.Virtual,
            PreparationMs = preparationMs,
        };
    }

    /// <summary>The outcome the engine reports for <paramref name="step"/>.</summary>
    public static StepOutcome Outcome(
        StepAttributes step,
        TimeSpan actual,
        int passes,
        ulong verifiedBytes,
        ulong errors,
        bool completed,
        TimeSpan overrun = default)
    {
        return new StepOutcome
        {
            Attributes = step,
            ActualDuration = actual,
            PassesCompleted = passes,
            VerifiedBytes = verifiedBytes,
            Errors = errors,
            Completed = completed,
            Overrun = overrun,
        };
    }

    /// <summary>
    /// A progress snapshot on pass 1 of 1, named after the Quick step at <paramref name="stepIndex"/>, with
    /// coverage computed by <see cref="RunProgress.CoveragePercentOf"/>.
    /// </summary>
    public static RunProgress Progress(
        int stepIndex,
        int stepCount,
        TimeSpan elapsed,
        TimeSpan? remaining,
        ulong verified,
        ulong region,
        ulong totalErrors)
    {
        string stepName = stepIndex >= 0 && stepIndex < _quickSteps.Length
            ? _quickSteps[stepIndex].Name
            : "Step " + (stepIndex + 1).ToString(CultureInfo.InvariantCulture);
        return new RunProgress
        {
            StepIndex = stepIndex,
            StepCount = stepCount,
            StepName = stepName,
            PassIndex = 0,
            PassesPlanned = 1,
            Elapsed = elapsed,
            Remaining = remaining,
            VerifiedBytes = verified,
            RegionBytes = region,
            CoveragePercent = RunProgress.CoveragePercentOf(verified, region),
            TotalErrors = totalErrors,
        };
    }

    /// <summary>
    /// Sensor readings for the heartbeat: a present temperature is <see cref="AvailableStatus"/>, a missing one
    /// <see cref="DriverUnavailableStatus"/>.
    /// </summary>
    public static JournalReadings Readings(double? cpuC, double? ramC) => new(
        cpuC,
        cpuC is null ? DriverUnavailableStatus : AvailableStatus,
        ramC,
        ramC is null ? DriverUnavailableStatus : AvailableStatus);

    /// <summary>A <c>result</c> record built through <see cref="JournalResult.Create"/>.</summary>
    public static JournalResult Result(
        RunState terminal,
        RunEndReason reason,
        ulong totalErrors,
        ulong verifiedBytes,
        TimeSpan elapsed,
        TimeSpan? firstError,
        int stepsCompleted,
        DateTimeOffset completedUtc,
        DateTimeOffset verdictUtc,
        string? endDetail = null) => JournalResult.Create(
            terminal,
            reason,
            endDetail,
            totalErrors,
            verifiedBytes,
            elapsed,
            firstError,
            stepsCompleted,
            completedUtc,
            verdictUtc);

    /// <summary>
    /// Returns <paramref name="count"/> error records found by "Random bank thrash" (step index 1). The first
    /// is the AAP's example error: <see cref="FirstErrorAddress"/>, expected <see cref="FirstErrorExpected"/>,
    /// actual and re-read <see cref="FirstErrorActual"/>. Later records alternate expected words with high bits
    /// set (<c>0xFFFF000000000001</c>) and with leading zeros (<c>0x00000000000000FF</c>), flip bit
    /// <c>i mod 64</c>, and cycle the re-read outcome through matches-expected, repeats-actual and differs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IReadOnlyList<ErrorRecord> ErrorRecords(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var records = new ErrorRecord[count];
        for (int i = 0; i < count; i++)
        {
            records[i] = ErrorRecordAt(i);
        }

        return records;
    }

    /// <summary>
    /// A completed Quick run on machine A, as <see cref="RunSummary.Build"/> receives it: every record the run
    /// journalled, <paramref name="errors"/> as the stored detail and <paramref name="causeHints"/> as the
    /// analyzer's items. The verdict is PASS with no errors and otherwise FAIL with end reason Completed, so
    /// Stop on error is off whenever errors exist. Each step ran its budget in four full passes over 1 GiB, so the
    /// run took 300 s of test time and verified exactly 2,400 %.
    /// </summary>
    /// <param name="errors">The stored error records.</param>
    /// <param name="causeHints">The ranked hints.</param>
    /// <param name="totalErrors">The true total; defaults to the stored count and may not be below it.</param>
    /// <param name="runId">The run id; defaults to <see cref="RunIdA"/>.</param>
    /// <param name="startRequestedUtc">When Start was accepted; defaults to <see cref="StartUtc"/>.</param>
    public static RunSummarySource CompletedRunSource(
        IReadOnlyList<ErrorRecord> errors,
        SummaryCauseHints causeHints,
        ulong? totalErrors = null,
        Guid? runId = null,
        DateTimeOffset? startRequestedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(causeHints);
        ulong total = totalErrors ?? (ulong)errors.Count;
        if (total < (ulong)errors.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalErrors),
                total,
                "The true error total cannot be below the number of stored records.");
        }

        Guid id = runId ?? RunIdA;
        DateTimeOffset start = startRequestedUtc ?? StartUtc;
        JournalPrepared prepared = Prepared();
        DateTimeOffset running = start + TimeSpan.FromMilliseconds(prepared.PreparationMs);
        IReadOnlyList<StepAttributes> steps = QuickSteps();

        // Stored records count against the step that found them; the rest of the true total against the step
        // of the last stored record, or the second step when no detail was stored.
        var stepErrors = new ulong[steps.Count];
        foreach (ErrorRecord error in errors)
        {
            stepErrors[Math.Clamp(error.StepIndex, 0, steps.Count - 1)]++;
        }

        int remainderStep = errors.Count > 0 ? Math.Clamp(errors[^1].StepIndex, 0, steps.Count - 1) : 1;
        stepErrors[remainderStep] += total - (ulong)errors.Count;

        var finishedSteps = new JournalStepEnd[steps.Count];
        TimeSpan testTime = TimeSpan.Zero;
        ulong verifiedBytes = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            StepAttributes step = steps[i];
            ulong stepVerified = CompletedRunPasses * GiB;
            verifiedBytes += stepVerified;
            testTime += step.TimeBudget;
            StepOutcome outcome = Outcome(step, step.TimeBudget, CompletedRunPasses, stepVerified, stepErrors[i], completed: true);
            finishedSteps[i] = JournalStepEnd.FromOutcome(outcome) with { Utc = running + testTime };
        }

        DateTimeOffset completedUtc = running + testTime;
        DateTimeOffset verdictUtc = completedUtc + TimeSpan.FromMilliseconds(40);
        TimeSpan? firstError = errors.Count > 0
            ? errors.Min(error => error.TimeIntoTest)
            : total > 0 ? TimeSpan.FromSeconds(72) : null;
        TimeSpan lastBeatTime = testTime - TimeSpan.FromSeconds(1);
        RunProgress lastProgress = Progress(
            steps.Count - 1,
            steps.Count,
            lastBeatTime,
            TimeSpan.FromSeconds(1),
            verifiedBytes,
            GiB,
            total);
        JournalBegin begin = Begin(id, BaseName(id, start), SystemA(), OsA, MemoryA(), start, stopOnError: total == 0);
        JournalResult result = Result(
            total == 0 ? RunState.Passed : RunState.Failed,
            RunEndReason.Completed,
            total,
            verifiedBytes,
            testTime,
            firstError,
            steps.Count,
            completedUtc,
            verdictUtc);

        return new RunSummarySource
        {
            Begin = begin with { Utc = start },
            Prepared = prepared with { Utc = running },
            FinishedSteps = finishedSteps,
            Result = result with { Utc = verdictUtc },
            Released = new JournalReleased
            {
                ReleasedBytes = GiB,
                ReleaseMs = 850,
                Utc = completedUtc + TimeSpan.FromMilliseconds(900),
            },
            LastBeat = JournalBeat.From(lastProgress, Readings(71.5, 48.0)) with { Utc = running + lastBeatTime },
            StoredErrors = errors,
            ErrorDetailAvailable = true,
            Temperatures = SummaryTemperatures.Initial.Merge(71.5, AvailableStatus, 48.0, AvailableStatus),
            CauseHints = causeHints,
        };
    }

    /// <summary>Serializes each record as the journal line <see cref="JournalLineSerializer"/> writes.</summary>
    public static IReadOnlyList<string> Lines(params JournalRecord[] records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Select(JournalLineSerializer.Serialize).ToArray();
    }

    private static StepAttributes QuickStep(
        int index,
        string name,
        PatternKind pattern,
        AccessOrder order,
        TimeSpan hold,
        int timeBudgetSeconds,
        int coverageBudgetPercent,
        InstabilityMechanism primary,
        InstabilityMechanism mechanisms)
    {
        return new StepAttributes
        {
            Index = index,
            Name = name,
            Pattern = pattern,
            Order = order,
            Threads = ThreadMode.All,
            Hold = hold,
            TimeBudget = TimeSpan.FromSeconds(timeBudgetSeconds),
            CoverageBudgetPercent = coverageBudgetPercent,
            PrimaryMechanism = primary,
            Mechanisms = mechanisms,
        };
    }

    private static ErrorRecord ErrorRecordAt(int i)
    {
        ulong expected;
        ulong actual;
        ulong reread;
        RereadOutcome outcome;
        if (i == 0)
        {
            (expected, actual, reread, outcome) =
                (FirstErrorExpected, FirstErrorActual, FirstErrorActual, RereadOutcome.RereadRepeatsActual);
        }
        else
        {
            expected = i % 2 == 0 ? 0xFFFF000000000001 : 0x00000000000000FF;
            actual = expected ^ (1UL << (i % 64));
            (reread, outcome) = (i % 3) switch
            {
                0 => (expected, RereadOutcome.RereadMatchesExpected),
                1 => (actual, RereadOutcome.RereadRepeatsActual),

                // A single-bit flip never makes ~actual equal expected, so this re-read differs from both.
                _ => (~actual, RereadOutcome.RereadDiffers),
            };
        }

        ulong spacing = (ulong)i * 0x40;
        return new ErrorRecord
        {
            Sequence = (ulong)i,
            Address = FirstErrorAddress + spacing,
            AddressKind = AddressKind.Virtual,
            RegionOffset = FirstErrorRegionOffset + spacing,
            Expected = expected,
            Actual = actual,
            Reread = reread,
            RereadOutcome = outcome,
            StepIndex = 1,
            StepName = _quickSteps[1].Name,
            PassIndex = i / 1000,
            AccessType = AccessType.VerifyAfterWrite,
            TimeSinceWrite = TimeSpan.FromMicroseconds(750),
            TimeIntoTest = TimeSpan.FromSeconds(72) + TimeSpan.FromMilliseconds(i),
            WorkerIndex = i % 7,
            LogicalProcessor = i % 7,
            ProcessorGroup = 0,
            ActiveWorkers = 7,
        };
    }
}


/// <summary>
/// Tests for <see cref="ProgressJournal"/>, <see cref="JournalAck"/>, the journal line format and
/// <see cref="FileJournalStorage"/>: durable acknowledgment of <c>begin</c>, <c>step</c>, <c>result</c> and
/// <c>end</c>; the step gate's timeout, failure and stop outcomes; journal I/O confined to one background writer
/// thread; heartbeats; record order and shapes; and the failure flag that stands in for an event.
/// </summary>
/// <remarks>
/// The engine-level consequences of a gate outcome (Abort, the HostFault stop request, no traffic for the step)
/// belong to RunControllerTests and CoverageAndTimingTests. Every timer and stamp runs on a
/// <see cref="FakeTimeProvider"/>; real time is used only for the step gate's 5 s timeout and the 2 s stop bound,
/// which the specification states in real time.
/// </remarks>
public sealed partial class ProgressJournalTests
{
    private const string BeginType = "begin";
    private const string PreparedType = "prepared";
    private const string StepType = "step";
    private const string StepEndType = "stepEnd";
    private const string BeatType = "beat";
    private const string ResultType = "result";
    private const string ReleasedType = "released";
    private const string EndType = "end";
    private const string ClosedMessage = "The progress journal is closed.";
    private const string SimulatedFailureText = "Simulated journal write failure";
    private const string TornBeat = "{\"t\":\"beat\",\"step\":2,\"elap";
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string?[] _fullRunRecordTypes =
    {
        BeginType, PreparedType, StepType, StepEndType, BeatType, ResultType, ReleasedType, EndType,
    };

    // The default real-time ceiling for anything asynchronous to the test thread.
    private static readonly TimeSpan _ceiling = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _stopBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _nonBlockingBound = TimeSpan.FromMilliseconds(100);
    private static readonly DateTimeOffset _t0 = JournalSamples.StartUtc;
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // Acknowledged writes
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BeginType)]
    [InlineData(ResultType)]
    [InlineData(EndType)]
    public async Task AcknowledgedRecord_StorageBlocked_CompletesOnlyAfterDurableAsync(string recordType)
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        if (recordType != BeginType)
        {
            await BeginAcknowledgedAsync(journal);
        }

        storage.BlockNext(recordType);
        try
        {
            Task<JournalAckStatus> pending = recordType switch
            {
                BeginType => journal.BeginAsync(JournalSamples.BeginA(_t0)),
                ResultType => journal.RecordResultAsync(PassedResult()),
                _ => journal.EndAsync(),
            };

            Assert.True(storage.WaitForBlocked(_ceiling), $"The {recordType} append never reached the storage.");
            Assert.False(pending.IsCompleted, $"The {recordType} acknowledgment completed before the record was durable.");
            Assert.DoesNotContain(recordType, storage.RecordTypesWritten());

            storage.Release();

            Assert.Equal(JournalAckStatus.Acknowledged, await pending.WaitAsync(_ceiling, Cancellation));
            Assert.Contains(recordType, storage.RecordTypesWritten());
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public async Task BeginAsync_WriteFails_ReturnsFailedSetsFailureAndDeletesJournalAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        storage.FailNext(BeginType);

        JournalAckStatus status = await journal.BeginAsync(JournalSamples.BeginA(_t0)).WaitAsync(_ceiling, Cancellation);

        Assert.Equal(JournalAckStatus.Failed, status);
        JournalFailure failure = AssertFailure(journal, BeginType);
        Assert.Contains(SimulatedFailureText, failure.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(failure.Exception);
        Assert.Equal(_t0, failure.OccurredUtc);

        // Start is refused, so no journal may survive to be misreported as an unfinished run.
        WaitUntil(() => storage.IsDeleted, "The writer did not delete the journal after the begin record failed.");
        Assert.Empty(storage.Lines);

        // The writer has exited: later records are refused at once rather than queued.
        using var stopEvent = new ManualResetEvent(false);
        JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
        Assert.Equal(JournalAckStatus.Failed, ack.Wait(stopEvent, TimeSpan.Zero));
        Assert.Equal(ClosedMessage, ack.Failure?.Message);
        Assert.Equal(JournalAckStatus.Failed, await journal.RecordResultAsync(PassedResult()).WaitAsync(_ceiling, Cancellation));
        Assert.Equal(JournalAckStatus.Failed, await journal.EndAsync().WaitAsync(_ceiling, Cancellation));
        Assert.Equal(1, storage.AppendCallCount);
    }

    [Fact]
    public async Task BeginAsync_AcknowledgmentTimesOut_ReturnsTimedOutAndDeletesJournalOnceAppendReturnsAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        storage.BlockNext(BeginType);
        try
        {
            Task<JournalAckStatus> pending = journal.BeginAsync(JournalSamples.BeginA(_t0));
            Assert.True(storage.WaitForBlocked(_ceiling), "The begin append never reached the storage.");

            // The awaitable bound runs on the journal's clock.
            time.Advance(ProgressJournal.AcknowledgmentTimeout);

            Assert.Equal(JournalAckStatus.TimedOut, await pending.WaitAsync(_ceiling, Cancellation));
            Assert.False(storage.IsDeleted);

            storage.Release();

            // The stalled append finally returns; the writer then deletes the begin-only journal and exits.
            WaitUntil(() => storage.IsDeleted, "The writer kept a journal whose begin record timed out.");
            Assert.Equal(new string?[] { BeginType }, storage.RecordTypesWritten());
            Assert.Empty(storage.ReadLines());
            Assert.Null(journal.Failure);

            using var stopEvent = new ManualResetEvent(false);
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
            Assert.Equal(JournalAckStatus.Failed, ack.Wait(stopEvent, _ceiling));
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public async Task EndAsync_Acknowledged_DeletesJournalAfterAppendingEndAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        JournalBegin begin = await BeginAcknowledgedAsync(journal);

        JournalAckStatus status = await journal.EndAsync().WaitAsync(_ceiling, Cancellation);

        Assert.Equal(JournalAckStatus.Acknowledged, status);
        AppendCall endCall = storage.Calls[^1];
        Assert.Equal(EndType, endCall.RecordType);
        Assert.Equal(AppendOutcome.Succeeded, endCall.Outcome);
        JournalEnd end = Assert.IsType<JournalEnd>(Parse(storage.Lines[^1]));
        Assert.Equal(begin.RunId, end.RunId);
        Assert.True(end.Published);

        // IsDeleted stays true only while nothing has been appended since the delete, so end came first.
        Assert.True(storage.IsDeleted);
        Assert.Equal(1, storage.DeleteCount);
        Assert.False(storage.Exists());
        Assert.Empty(storage.ReadLines());
        Assert.Null(journal.Failure);

        // The writer has finished the run: nothing more is accepted.
        using var stopEvent = new ManualResetEvent(false);
        JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
        Assert.Equal(JournalAckStatus.Failed, ack.Wait(stopEvent, _ceiling));
        Assert.Equal(1, storage.DeleteCount);
    }

    [Fact]
    public async Task EndAsync_EndAppendFails_StillDeletesJournalAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        storage.FailNext(EndType);

        JournalAckStatus status = await journal.EndAsync().WaitAsync(_ceiling, Cancellation);

        // The summaries are already published, so a journal left behind would be misreported at the next launch.
        Assert.Equal(JournalAckStatus.Acknowledged, status);
        Assert.True(storage.IsDeleted);
        Assert.DoesNotContain(EndType, storage.RecordTypesWritten());
        AssertFailure(journal, EndType);
    }

    [Fact]
    public async Task EndAsync_DeleteFails_ReturnsFailedAndKeepsJournalAsync()
    {
        var storage = new ThrowingCleanupStorage { FailDelete = true };
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);

        JournalAckStatus status = await journal.EndAsync().WaitAsync(_ceiling, Cancellation);

        Assert.Equal(JournalAckStatus.Failed, status);
        JournalFailure failure = AssertFailure(journal, EndType);
        Assert.StartsWith("The journal could not be deleted: ", failure.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(failure.Exception);
        Assert.Equal(new string?[] { BeginType, EndType }, storage.Inner.RecordTypesWritten());
        Assert.True(storage.Inner.Exists());
        Assert.Equal(0, storage.Inner.DeleteCount);
    }


    // ---------------------------------------------------------------------------------------------
    // Step gate
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AcknowledgmentTimeout_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProgressJournal.AcknowledgmentTimeout);
    }

    [Fact]
    public async Task RecordStepStarting_Acknowledged_ReturnsAcknowledgedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);

        JournalAck ack = journal.RecordStepStarting(2, 6, "Read-after-write turnaround");
        JournalAckStatus status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);

        Assert.Equal(JournalAckStatus.Acknowledged, status);
        Assert.True(ack.IsCompleted);
        Assert.Null(ack.Failure);
        Assert.Equal(JournalAckStatus.Acknowledged, await ack.Completion.WaitAsync(_ceiling, Cancellation));

        // Durable before the gate returns, and numbered from 1 as in "step 3 of 6".
        string line = Assert.Single(storage.Lines, candidate => RecordTypeOf(candidate) == StepType);
        Assert.Equal(
            new JournalStepStart { Index = 3, Count = 6, Name = "Read-after-write turnaround", Utc = _t0 },
            Parse(line));
    }

    [Fact]
    public async Task RecordStepStarting_StalledWrite_TimesOutAfterFiveSecondsAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);
        storage.BlockAll(StepType);
        try
        {
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
            long started = Stopwatch.GetTimestamp();

            // The specification bounds the gate in real time, so this waits the real 5 s.
            JournalAckStatus status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            Assert.Equal(JournalAckStatus.TimedOut, status);
            Assert.InRange(elapsed, TimeSpan.FromSeconds(4.9), TimeSpan.FromSeconds(7));
            Assert.False(ack.IsCompleted);
            Assert.Equal(1, storage.BlockedCount);

            // A stall is not a write failure; the gate's own outcome reports it.
            Assert.Null(journal.Failure);
        }
        finally
        {
            storage.ClearRules();
            storage.Release();
        }
    }

    [Fact]
    public async Task RecordStepStarting_FailingWrite_ReturnsFailedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);
        storage.FailNext(StepType);

        JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
        long started = Stopwatch.GetTimestamp();
        JournalAckStatus status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(JournalAckStatus.Failed, status);
        Assert.True(elapsed < _stopBound, $"The failed step record took {elapsed} to report.");
        Assert.NotNull(ack.Failure);
        Assert.Equal(StepType, ack.Failure.RecordType);
        Assert.Contains(SimulatedFailureText, ack.Failure.Message, StringComparison.Ordinal);
        Assert.Same(ack.Failure, AssertFailure(journal, StepType));
        Assert.DoesNotContain(StepType, storage.RecordTypesWritten());
    }

    [Fact]
    public async Task RecordStepStarting_StopDuringWait_ReturnsStoppedWithinTwoSecondsAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);
        storage.BlockNext(StepType);
        try
        {
            JournalAck ack = journal.RecordStepStarting(1, 6, JournalSamples.QuickSteps()[1].Name);
            Assert.True(storage.WaitForBlocked(_ceiling), "The step append never reached the storage.");

            JournalAckStatus status = JournalAckStatus.Acknowledged;
            long returned = 0;
            var coordinator = new Thread(() =>
            {
                status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);
                returned = Stopwatch.GetTimestamp();
            })
            {
                IsBackground = true,
                Name = "Test coordinator",
            };
            coordinator.Start();

            await Task.Delay(TimeSpan.FromMilliseconds(250), Cancellation);
            Assert.True(coordinator.IsAlive, "The step gate returned before the stop was requested.");

            long stopped = Stopwatch.GetTimestamp();
            stopEvent.Set();

            Assert.True(coordinator.Join(_ceiling), "The step gate did not return after the stop.");
            Assert.Equal(JournalAckStatus.Stopped, status);
            TimeSpan latency = Stopwatch.GetElapsedTime(stopped, returned);
            Assert.True(latency < _stopBound, $"The step gate took {latency} to honour the stop.");
            Assert.False(ack.IsCompleted);
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public async Task RecordStepStarting_StopAlreadySet_ReturnsStoppedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(true);
        await BeginAcknowledgedAsync(journal);
        storage.BlockNext(StepType);
        try
        {
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
            Assert.True(storage.WaitForBlocked(_ceiling), "The step append never reached the storage.");

            long started = Stopwatch.GetTimestamp();
            JournalAckStatus status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);

            Assert.Equal(JournalAckStatus.Stopped, status);
            Assert.True(Stopwatch.GetElapsedTime(started) < _stopBound);
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public async Task Wait_AcknowledgedBeforeStop_AcknowledgmentTakesPrecedenceAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);

        JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
        Assert.Equal(JournalAckStatus.Acknowledged, await ack.Completion.WaitAsync(_ceiling, Cancellation));
        stopEvent.Set();

        // A record already durable lets the step run; the stop is honoured at the engine's next check.
        Assert.Equal(JournalAckStatus.Acknowledged, ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout));
        Assert.Equal(JournalAckStatus.Acknowledged, ack.Wait(stopEvent, TimeSpan.Zero));
    }

    [Fact]
    public async Task Wait_StopEventDisposed_ReturnsStoppedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        storage.BlockNext(StepType);
        try
        {
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);
            Assert.True(storage.WaitForBlocked(_ceiling), "The step append never reached the storage.");
            var stopEvent = new ManualResetEvent(false);
            stopEvent.Dispose();

            Assert.Equal(JournalAckStatus.Stopped, ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout));
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public async Task Wait_InvalidArguments_ThrowAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(true);
        await BeginAcknowledgedAsync(journal);
        storage.BlockNext(StepType);
        try
        {
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);

            Assert.Throws<ArgumentNullException>(() => ack.Wait(null!, ProgressJournal.AcknowledgmentTimeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => ack.Wait(stopEvent, TimeSpan.FromMilliseconds(-2)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ack.Wait(stopEvent, TimeSpan.FromMilliseconds(int.MaxValue + 1.0)));

            // An infinite bound is valid; the signalled stop ends the wait.
            Assert.Equal(JournalAckStatus.Stopped, ack.Wait(stopEvent, Timeout.InfiniteTimeSpan));
        }
        finally
        {
            storage.Release();
        }
    }

    [Theory]
    [InlineData(-1, 6)]
    [InlineData(6, 6)]
    [InlineData(0, 0)]
    public async Task RecordStepStarting_IndexOrCountOutOfRange_ThrowsAsync(int stepIndex, int stepCount)
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);

        Assert.Throws<ArgumentOutOfRangeException>(() => journal.RecordStepStarting(stepIndex, stepCount, "Step"));
        Assert.Equal(new string?[] { BeginType }, storage.RecordTypesWritten());
    }


    // ---------------------------------------------------------------------------------------------
    // Writer-thread ownership
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AllAppends_RunOnSingleDedicatedWriterThreadAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        int testThreadId = Environment.CurrentManagedThreadId;

        // The "dispatcher" plays the UI thread and the coordinator, and advances the fake clock itself, so the
        // heartbeat timer's callback also runs there; every append must still happen on the writer.
        (DrivenRun run, int dispatcherThreadId) = RunOnDedicatedThread(
            () => DriveFullRun(journal, storage, time),
            "Test dispatcher");
        await AssertRunAcknowledgedAsync(run);

        IReadOnlyList<AppendCall> calls = storage.Calls;
        Assert.Equal(_fullRunRecordTypes, calls.Select(call => call.RecordType));
        int writerThreadId = calls[0].ManagedThreadId;
        Assert.All(calls, call =>
        {
            Assert.Equal(writerThreadId, call.ManagedThreadId);
            Assert.False(call.IsThreadPoolThread, $"The {call.RecordType} append ran on a thread-pool thread.");

            // A background writer cannot keep the process alive once abandoned.
            Assert.True(call.IsBackground, $"The {call.RecordType} append ran on a foreground thread.");
            Assert.Equal(AppendOutcome.Succeeded, call.Outcome);
        });
        Assert.NotEqual(dispatcherThreadId, writerThreadId);
        Assert.NotEqual(testThreadId, writerThreadId);
    }

    [Fact]
    public async Task PublishProgress_StorageStalled_NeverBlocksAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        StepAttributes step = JournalSamples.QuickSteps()[0];
        StepOutcome outcome = JournalSamples.Outcome(step, step.TimeBudget, 12, 2 * JournalSamples.GiB, 0, true);
        var snapshots = new RunProgress[1_000];
        var readings = new JournalReadings[1_000];
        for (int i = 0; i < snapshots.Length; i++)
        {
            snapshots[i] = JournalSamples.Progress(
                0,
                6,
                TimeSpan.FromMilliseconds(250 * i),
                null,
                (ulong)i * 1_048_576,
                JournalSamples.GiB,
                0);
            readings[i] = JournalSamples.Readings(50.0 + (i % 10), 40.0);
        }

        storage.BlockAll();
        try
        {
            AssertReturnsPromptly(() => journal.RecordPrepared(JournalSamples.Prepared()), "RecordPrepared");
            Assert.True(storage.WaitForBlocked(_ceiling), "The writer did not stall on the prepared record.");

            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < snapshots.Length; i++)
            {
                journal.PublishProgress(snapshots[i]);
                journal.PublishReadings(readings[i]);
            }

            TimeSpan publishing = Stopwatch.GetElapsedTime(started);
            Assert.True(
                publishing < TimeSpan.FromMilliseconds(500),
                $"1,000 progress and readings publications took {publishing} with the writer stalled.");

            AssertReturnsPromptly(() => journal.RecordStepFinished(outcome), "RecordStepFinished");
            AssertReturnsPromptly(
                () => journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(850)),
                "RecordReleased");
            JournalAck? ack = null;
            AssertReturnsPromptly(() => ack = journal.RecordStepStarting(1, 6, step.Name), "RecordStepStarting");
            Assert.False(ack?.IsCompleted);

            // FakeTimeProvider runs the heartbeat timer's callback on this thread: it must only signal the writer.
            AssertReturnsPromptly(() => time.Advance(ProgressJournal.HeartbeatInterval), "the heartbeat timer");

            Assert.Equal(1, storage.BlockedCount);
            Assert.Equal(new string?[] { BeginType }, storage.RecordTypesWritten());
        }
        finally
        {
            storage.ClearRules();
            storage.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Heartbeat and failure flag
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Beat_EverySimulatedSecond_UsesLatestSnapshotAsync()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ProgressJournal.HeartbeatInterval);
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);

        RunProgress first = JournalSamples.Progress(0, 6, TimeSpan.FromSeconds(10), null, JournalSamples.GiB / 4, JournalSamples.GiB, 0);
        journal.PublishProgress(first);
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => CountOf(storage, BeatType) == 1, "The first heartbeat was not written.");

        // The AAP's example beat: step 3, 104,250 ms, 71.5 °C and 48.0 °C.
        RunProgress second = JournalSamples.Progress(
            2,
            6,
            TimeSpan.FromMilliseconds(104_250),
            TimeSpan.FromSeconds(196),
            (4 * JournalSamples.GiB) + (JournalSamples.GiB / 8),
            JournalSamples.GiB,
            3);
        journal.PublishProgress(second);
        journal.PublishReadings(JournalSamples.Readings(71.5, 48.0));
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => CountOf(storage, BeatType) == 2, "The second heartbeat was not written.");

        IReadOnlyList<JournalBeat> beats = RecordsOf<JournalBeat>(storage);
        Assert.Equal(1, beats[0].Step);
        Assert.Equal(10_000, beats[0].ElapsedMs);
        Assert.Equal(_t0 + TimeSpan.FromSeconds(1), beats[0].Utc);

        JournalBeat latest = beats[1];
        Assert.Equal(3, latest.Step);
        Assert.Equal(104_250, latest.ElapsedMs);
        Assert.Equal(412.5, latest.Coverage);
        Assert.Equal(3UL, latest.Errors);
        Assert.Equal(71.5, latest.CpuC);
        Assert.Equal(48.0, latest.RamC);
        Assert.Equal(JournalSamples.AvailableStatus, latest.CpuStatus);
        Assert.Equal(JournalSamples.AvailableStatus, latest.RamStatus);
        Assert.Equal(_t0 + TimeSpan.FromSeconds(2), latest.Utc);

        // Exactly one beat per simulated second, and none while the clock stands still.
        for (int expected = 3; expected <= 5; expected++)
        {
            time.Advance(ProgressJournal.HeartbeatInterval);
            int target = expected;
            WaitUntil(() => CountOf(storage, BeatType) == target, $"Heartbeat {target} was not written.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        Assert.Equal(5, CountOf(storage, BeatType));
        Assert.Null(journal.Failure);
    }

    [Fact]
    public async Task Beat_NoProgressPublished_WritesNoBeatAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);

        // Heartbeats start with the first progress snapshot; a due beat without one writes nothing.
        time.Advance(ProgressJournal.HeartbeatInterval);
        journal.RecordPrepared(JournalSamples.Prepared());
        WaitUntil(() => CountOf(storage, PreparedType) == 1, "The prepared record was not written.");
        await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        Assert.Equal(0, CountOf(storage, BeatType));

        // Before any readings are published, the beat records them as unknown.
        journal.PublishProgress(JournalSamples.Progress(0, 6, TimeSpan.FromSeconds(1), null, 0, JournalSamples.GiB, 0));
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => CountOf(storage, BeatType) == 1, "No heartbeat followed the first progress snapshot.");

        JournalBeat beat = Assert.Single(RecordsOf<JournalBeat>(storage));
        Assert.Null(beat.CpuC);
        Assert.Null(beat.RamC);
        Assert.Equal(JournalReadings.UnknownStatus, beat.CpuStatus);
        Assert.Equal(JournalReadings.UnknownStatus, beat.RamStatus);
    }

    [Fact]
    public async Task BeatWriteFails_SetsFailureAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        storage.FailNext(BeatType);

        journal.PublishProgress(JournalSamples.Progress(0, 6, TimeSpan.FromSeconds(1), null, 0, JournalSamples.GiB, 0));
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => journal.Failure is not null, "A failed heartbeat did not set the failure flag.");

        JournalFailure failure = AssertFailure(journal, BeatType);
        Assert.Contains(SimulatedFailureText, failure.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(failure.Exception);
        Assert.Equal(_t0 + ProgressJournal.HeartbeatInterval, failure.OccurredUtc);

        // Nobody waits on a heartbeat: the writer carries on, and the first failure stays recorded.
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => CountOf(storage, BeatType) == 1, "The writer stopped after a failed heartbeat.");
        Assert.Same(failure, journal.Failure);
    }

    // stepEnd is the case the specification names; prepared and released take the same unacknowledged path.
    [Theory]
    [InlineData(StepEndType)]
    [InlineData(PreparedType)]
    [InlineData(ReleasedType)]
    public async Task UnacknowledgedRecordWriteFails_SetsFailureAsync(string recordType)
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        await BeginAcknowledgedAsync(journal);
        storage.FailNext(recordType);

        OfferUnacknowledged(journal, recordType);
        WaitUntil(() => journal.Failure is not null, $"A failed {recordType} record did not set the failure flag.");

        JournalFailure failure = AssertFailure(journal, recordType);
        Assert.Contains(SimulatedFailureText, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(recordType, storage.RecordTypesWritten());

        // First writer wins: a later failure never replaces the first. The acknowledged step record that
        // follows proves the writer has already handled the second failing record, queued before it.
        string secondType = recordType == PreparedType ? ReleasedType : PreparedType;
        storage.FailNext(secondType);
        OfferUnacknowledged(journal, secondType);
        JournalAck ack = journal.RecordStepStarting(1, 6, JournalSamples.QuickSteps()[1].Name);
        Assert.Equal(JournalAckStatus.Acknowledged, ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout));
        Assert.Contains(storage.Calls, call => call.RecordType == secondType && call.Outcome == AppendOutcome.Failed);
        Assert.Same(failure, journal.Failure);
    }

    [Fact]
    public async Task RecordResultAsync_WriteFails_ReturnsFailedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        storage.FailNext(ResultType);

        JournalAckStatus status = await journal.RecordResultAsync(PassedResult()).WaitAsync(_ceiling, Cancellation);

        Assert.Equal(JournalAckStatus.Failed, status);
        AssertFailure(journal, ResultType);
        Assert.DoesNotContain(ResultType, storage.RecordTypesWritten());

        // The journal stays, so the next launch retries publication from it.
        Assert.False(storage.IsDeleted);
    }

    [Fact]
    public async Task RecordStepStarting_QueuedWhenBeginFails_FailsAsClosedAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);
        using var stopEvent = new ManualResetEvent(false);
        storage.BlockNext(BeginType);
        try
        {
            Task<JournalAckStatus> begin = journal.BeginAsync(JournalSamples.BeginA(_t0));
            Assert.True(storage.WaitForBlocked(_ceiling), "The begin append never reached the storage.");
            JournalAck ack = journal.RecordStepStarting(0, 6, JournalSamples.QuickSteps()[0].Name);

            storage.Release(failBlocked: true);

            // The writer closes without writing what it had queued behind the failed begin record.
            Assert.Equal(JournalAckStatus.Failed, await begin.WaitAsync(_ceiling, Cancellation));
            Assert.Equal(JournalAckStatus.Failed, ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout));
            Assert.Equal(StepType, ack.Failure?.RecordType);
            Assert.Equal(ClosedMessage, ack.Failure?.Message);
            WaitUntil(() => storage.IsDeleted, "The writer did not delete the journal after the begin record failed.");
            Assert.Equal(1, storage.AppendCallCount);
        }
        finally
        {
            storage.Release();
        }
    }

    [Fact]
    public void AbandonedJournal_IsCollectedWhileWriterRunsAndFinalizerStopsWriter()
    {
        var storage = new ThrowingCleanupStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        WeakReference journal = BeginAndAbandon(storage, time);

        // The writer thread and the heartbeat timer reach only the writer core, never the journal itself.
        for (int attempt = 0; attempt < 10 && journal.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(journal.IsAlive, "A journal nothing references was kept alive by its writer.");

        // The finalizer stopped the writer, which closed the storage and left the journal for recovery.
        WaitUntil(() => storage.IsDisposed, "The abandoned journal's writer never closed its storage.");
        Assert.Equal(new string?[] { BeginType }, storage.Inner.RecordTypesWritten());
        Assert.True(storage.Inner.Exists());
        Assert.Equal(0, storage.Inner.DeleteCount);
    }

    [Fact]
    public void ProgressJournal_ExposesNoEvents()
    {
        Assert.Empty(typeof(ProgressJournal).GetEvents(AllMembers));
        PropertyInfo failure = Assert.Single(
            typeof(ProgressJournal).GetProperties(AllMembers),
            property => property.Name == nameof(ProgressJournal.Failure));
        Assert.Equal(typeof(JournalFailure), failure.PropertyType);
    }

    [Fact]
    public void JournalWriter_HoldsNoDelegateEventOrJournalReference()
    {
        // The writer thread and the heartbeat timer reach only this core, so nothing a run creates is kept alive
        // through it: no callback, no event and no path back to the journal.
        Type writer = Assert.Single(
            typeof(ProgressJournal).GetNestedTypes(BindingFlags.NonPublic),
            type => type.Name == "JournalWriter");
        Assert.Empty(writer.GetEvents(AllMembers));
        Assert.All(writer.GetFields(AllMembers), field =>
        {
            Assert.False(
                typeof(Delegate).IsAssignableFrom(field.FieldType),
                $"JournalWriter.{field.Name} holds a delegate.");
            Assert.NotEqual(typeof(ProgressJournal), field.FieldType);
        });
    }


    // ---------------------------------------------------------------------------------------------
    // Record shapes and order
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FullRun_WritesRecordsInOrder_OneJsonLinePerRecordAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using var journal = new ProgressJournal(storage, time);

        DrivenRun run = DriveFullRun(journal, storage, time);
        await AssertRunAcknowledgedAsync(run);

        // No beat follows the result record, although the clock moved on after it.
        Assert.Equal(_fullRunRecordTypes, storage.RecordTypesWritten());
        IReadOnlyList<string> lines = storage.Lines;
        DateTimeOffset oneSecond = _t0 + TimeSpan.FromSeconds(1);
        DateTimeOffset twoSeconds = _t0 + TimeSpan.FromSeconds(2);
        var expected = new JournalRecord[]
        {
            run.Begin with { Utc = _t0 },
            run.Prepared with { Utc = _t0 },
            new JournalStepStart { Index = 1, Count = 6, Name = run.Outcome.Attributes.Name, Utc = _t0 },
            JournalStepEnd.FromOutcome(run.Outcome) with { Utc = _t0 },
            JournalBeat.From(run.Progress, run.Readings) with { Utc = oneSecond },
            run.Result with { Utc = oneSecond },
            new JournalReleased { ReleasedBytes = JournalSamples.GiB, ReleaseMs = 850, Utc = twoSeconds },
            new JournalEnd { RunId = run.Begin.RunId, Published = true, Utc = twoSeconds },
        };

        Assert.Equal(expected.Length, lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.StartsWith("{\"t\":\"" + _fullRunRecordTypes[i] + "\"", line, StringComparison.Ordinal);

            JournalRecord record = Parse(line);
            Assert.IsType(expected[i].GetType(), record);
            Assert.Equal(expected[i], record);

            string utc = UtcTextOf(line);
            Assert.Matches(UtcPattern(), utc);
            Assert.Equal(expected[i].Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), utc);
        }

        // Persisted step numbers are 1-based for the 0-based index 0.
        Assert.Equal(1, Assert.IsType<JournalStepStart>(Parse(lines[2])).Index);
        Assert.Equal(1, Assert.IsType<JournalStepEnd>(Parse(lines[3])).Index);
        Assert.Equal(1, Assert.IsType<JournalBeat>(Parse(lines[4])).Step);
        Assert.True(storage.IsDeleted);
        Assert.Null(journal.Failure);
    }

    [Theory]
    [InlineData(BeginType)]
    [InlineData(PreparedType)]
    [InlineData(StepType)]
    [InlineData(StepEndType)]
    [InlineData(BeatType)]
    [InlineData(ResultType)]
    [InlineData(ReleasedType)]
    [InlineData(EndType)]
    public void Serializer_RoundTripsEveryRecordType(string recordType)
    {
        JournalRecord record = SampleRecord(recordType);

        string line = JournalLineSerializer.Serialize(record);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("{\"t\":\"" + recordType + "\"", line, StringComparison.Ordinal);
        Assert.True(JournalLineSerializer.TryParse(line, out JournalRecord? parsed), $"The {recordType} line did not parse.");
        Assert.IsType(record.GetType(), parsed);
        Assert.Equal(record, parsed);
        Assert.Equal(line, JournalLineSerializer.Serialize(parsed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData(TornBeat)]
    [InlineData("{\"t\":\"beat\",\"step\":2,")]
    [InlineData("{\"step\":2,\"t\":\"beat\"}")]
    [InlineData("{\"t\":\"heartbeat\",\"step\":2}")]
    [InlineData("{\"t\":\"beat\",\"step\":\"two\"}")]
    [InlineData("{\"t\":\"step\",\"utc\":\"yesterday\"}")]
    [InlineData("{\"t\":\"step\",\"utc\":1730124351}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Serializer_MalformedLine_TryParseReturnsFalse(string line)
    {
        Assert.False(JournalLineSerializer.TryParse(line, out JournalRecord? record));
        Assert.Null(record);
    }

    [Fact]
    public void Serializer_ParseAll_DropsTornFinalLine()
    {
        JournalRecord begin = SampleRecord(BeginType);
        JournalRecord step = SampleRecord(StepType);
        var lines = new List<string>(JournalSamples.Lines(begin, step)) { TornBeat };

        IReadOnlyList<JournalRecord> records = JournalLineSerializer.ParseAll(lines);

        Assert.Equal(new[] { begin, step }, records);
    }

    [Fact]
    public void JournalBegin_Create_CapturesPlanAndMachineAndComparesByValue()
    {
        JournalBegin begin = JournalSamples.BeginA(_t0);

        Assert.Equal(300.0, begin.TargetSeconds);
        Assert.Equal(200.0, begin.TargetCoveragePercent);
        Assert.Equal(Enumerable.Range(1, 6), begin.Steps.Select(step => step.Number));
        Assert.Equal(5_000, begin.Steps[4].HoldMs);
        Assert.Equal(JournalSamples.OsA, begin.System.Os);
        Assert.Equal("Machine A Test CPU", begin.System.CpuName);
        Assert.Equal(JournalSamples.SystemA().AvailablePhysicalBytes, begin.AvailableBytes);
        Assert.Equal(JournalSamples.GiB, begin.PlannedBytes);

        // Steps compare element by element, so a record read back equals the one written.
        JournalBegin same = JournalSamples.BeginA(_t0);
        Assert.Equal(begin, same);
        Assert.Equal(begin.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(begin, JournalSamples.BeginA(_t0, stepCount: 5));
        Assert.NotEqual(begin, JournalSamples.BeginB(_t0));
    }

    [Fact]
    public void JournalBegin_Create_RejectsInconsistentInput()
    {
        IReadOnlyList<StepAttributes> steps = JournalSamples.QuickSteps();
        SystemSnapshot system = JournalSamples.SystemA();
        JournalMemoryInfo memory = JournalSamples.MemoryA();
        Guid runId = JournalSamples.RunIdA;
        string baseName = JournalSamples.BaseName(runId, _t0);

        Assert.Throws<ArgumentException>(
            () => JournalSamples.Begin(Guid.Empty, baseName, system, JournalSamples.OsA, memory, _t0));
        Assert.Throws<ArgumentException>(() => JournalSamples.Begin(runId, baseName, system, "  ", memory, _t0));
        Assert.Throws<ArgumentException>(
            () => JournalSamples.Begin(runId, baseName, system, JournalSamples.OsA, memory, _t0, Array.Empty<StepAttributes>()));

        // Recovery joins step results to definitions by number, so each index must equal its position.
        Assert.Throws<ArgumentException>(
            () => JournalSamples.Begin(runId, baseName, system, JournalSamples.OsA, memory, _t0, new[] { steps[1] }));
        Assert.Throws<ArgumentNullException>(
            () => JournalSamples.Begin(runId, baseName, system, JournalSamples.OsA, null!, _t0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void JournalReadings_NonFiniteTemperature_Throws(double celsius)
    {
        // JSON cannot carry a non-finite number, so one would fail every heartbeat write.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JournalReadings(celsius, JournalSamples.AvailableStatus, 40.0, JournalSamples.AvailableStatus));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JournalReadings(60.0, JournalSamples.AvailableStatus, celsius, JournalSamples.AvailableStatus));
    }

    [Fact]
    public void RecordFactories_InvalidValues_Throw()
    {
        DateTimeOffset completed = _t0 + TimeSpan.FromSeconds(301);
        Assert.Throws<ArgumentException>(() => new JournalReadings(60.0, " ", 40.0, JournalSamples.AvailableStatus));
        Assert.Throws<ArgumentException>(() => new JournalReadings(60.0, JournalSamples.AvailableStatus, 40.0, string.Empty));
        Assert.Throws<ArgumentException>(() => JournalSamples.Result(
            RunState.Running, RunEndReason.Completed, 0, 0, TimeSpan.Zero, null, 0, completed, completed));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalSamples.Result(
            RunState.Passed, RunEndReason.Completed, 0, 0, TimeSpan.FromMilliseconds(-1), null, 0, completed, completed));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalSamples.Result(
            RunState.Failed, RunEndReason.FirstError, 1, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), 0, completed, completed));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalSamples.Result(
            RunState.Aborted, RunEndReason.UserStop, 0, 0, TimeSpan.Zero, null, -1, completed, completed));
        Assert.Throws<ArgumentNullException>(() => JournalStepEnd.FromOutcome(null!));
        Assert.Throws<ArgumentNullException>(() => JournalBeat.From(null!, null));
        Assert.Throws<ArgumentNullException>(() => JournalStep.FromAttributes(null!));
        Assert.Throws<ArgumentNullException>(() => JournalSystemInfo.FromSnapshot(null!, JournalSamples.OsA));
        Assert.Throws<ArgumentNullException>(() => JournalLineSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => JournalLineSerializer.ParseAll(null!));
    }

    // ---------------------------------------------------------------------------------------------
    // FileJournalStorage
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileJournalStorage_AppendDurable_LineOnDiskImmediatelyAsync()
    {
        using var root = new TestDataRoot();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        using ProgressJournal journal = ProgressJournal.Create(root.Data, time);
        JournalBegin begin = JournalSamples.BeginA(_t0);

        Assert.Equal(JournalAckStatus.Acknowledged, await journal.BeginAsync(begin).WaitAsync(_ceiling, Cancellation));

        // Read through a separate handle while the journal still holds the file open for appending.
        byte[] content = ReadShared(root.Data.JournalPath);
        Assert.Equal((byte)'{', content[0]);
        Assert.Equal((byte)'\n', content[^1]);
        string line = Assert.Single(_utf8NoBom.GetString(content).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(begin with { Utc = _t0 }, Parse(line));

        // Disposing never deletes the journal, so a run without its end record stays recoverable.
        journal.Dispose();
        Assert.True(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public void FileJournalStorage_ReadLines_ReturnsTornFinalLine()
    {
        using var root = new TestDataRoot();
        IReadOnlyList<string> complete = JournalSamples.Lines(SampleRecord(BeginType), SampleRecord(StepType));

        // A crash mid-append leaves the final record without its terminator; CRLF endings are tolerated.
        File.WriteAllText(root.Data.JournalPath, complete[0] + "\r\n" + complete[1] + "\n" + TornBeat, _utf8NoBom);
        using var storage = new FileJournalStorage(root.Data.JournalPath);

        IReadOnlyList<string> lines = storage.ReadLines();

        Assert.Equal(new[] { complete[0], complete[1], TornBeat }, lines);
        IReadOnlyList<JournalRecord> records = JournalLineSerializer.ParseAll(lines);
        Assert.Equal(2, records.Count);
        Assert.IsType<JournalBegin>(records[0]);
        Assert.IsType<JournalStepStart>(records[1]);
    }

    [Fact]
    public void FileJournalStorage_AppendAfterTornLine_StartsOnNewLine()
    {
        using var root = new TestDataRoot();
        File.WriteAllText(root.Data.JournalPath, TornBeat, _utf8NoBom);
        string step = JournalLineSerializer.Serialize(SampleRecord(StepType));
        using var storage = new FileJournalStorage(root.Data.JournalPath);

        storage.AppendDurable(step);

        // The fragment stays a line of its own, so the record after it still parses.
        Assert.Equal(new[] { TornBeat, step }, storage.ReadLines());
        Assert.IsType<JournalStepStart>(Assert.Single(JournalLineSerializer.ParseAll(storage.ReadLines())));
    }

    [Fact]
    public void FileJournalStorage_Delete_RemovesFile()
    {
        using var root = new TestDataRoot();
        string path = root.Data.JournalPath;
        IReadOnlyList<string> lines = JournalSamples.Lines(SampleRecord(BeginType), SampleRecord(StepType));
        using var storage = new FileJournalStorage(path);
        Assert.Equal(Path.GetFullPath(path), storage.Location);
        Assert.False(storage.Exists());
        Assert.Empty(storage.ReadLines());
        storage.Delete();

        storage.AppendDurable(lines[0]);
        Assert.True(storage.Exists());
        Assert.True(File.Exists(path));

        storage.Delete();
        Assert.False(storage.Exists());
        Assert.False(File.Exists(path));
        Assert.Empty(storage.ReadLines());

        // A later append recreates the journal, and its folder when that is gone too.
        Directory.Delete(root.Data.JournalDirectory);
        storage.AppendDurable(lines[1]);
        Assert.True(storage.Exists());
        Assert.Equal(new[] { lines[1] }, storage.ReadLines());
    }

    [Fact]
    public void FileJournalStorage_InvalidUse_Throws()
    {
        using var root = new TestDataRoot();
        string line = JournalLineSerializer.Serialize(SampleRecord(StepType));
        Assert.Throws<ArgumentNullException>(() => new FileJournalStorage(null!));
        Assert.Throws<ArgumentException>(() => new FileJournalStorage("   "));
        var storage = new FileJournalStorage(root.Data.JournalPath);

        Assert.Throws<ArgumentNullException>(() => storage.AppendDurable(null!));
        Assert.Throws<ArgumentException>(() => storage.AppendDurable(line + "\n" + line));
        Assert.Throws<ArgumentException>(() => storage.AppendDurable(line + "\r"));
        Assert.False(storage.Exists());

        storage.AppendDurable(line);
        storage.Dispose();
        storage.Dispose();

        // A closed storage reports an ordinary write failure, and leaves the journal in place.
        Assert.Throws<IOException>(() => storage.AppendDurable(line));
        Assert.Equal(new[] { line }, storage.ReadLines());
    }

    // ---------------------------------------------------------------------------------------------
    // Lifetime and argument validation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_WithStorageBlocked_ReturnsPromptlyAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        var journal = new ProgressJournal(storage, time);
        await BeginAcknowledgedAsync(journal);
        StepAttributes step = JournalSamples.QuickSteps()[0];
        storage.BlockAll();
        try
        {
            journal.RecordPrepared(JournalSamples.Prepared());
            Assert.True(storage.WaitForBlocked(_ceiling), "The writer did not stall on the prepared record.");

            long started = Stopwatch.GetTimestamp();
            journal.Dispose();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            // Dispose waits at most 1 s for a stalled writer, then leaves it behind as a background thread.
            Assert.True(elapsed < _stopBound, $"Dispose took {elapsed} with the writer stalled.");

            // Afterwards every recording method is a no-op and every acknowledgment has failed.
            JournalAck ack = journal.RecordStepStarting(0, 6, step.Name);
            Assert.True(ack.IsCompleted);
            Assert.Equal(JournalAckStatus.Failed, await ack.Completion.WaitAsync(_ceiling, Cancellation));
            Assert.Equal(StepType, ack.Failure?.RecordType);
            Assert.Equal(ClosedMessage, ack.Failure?.Message);
            Assert.Equal(JournalAckStatus.Failed, await journal.RecordResultAsync(PassedResult()).WaitAsync(_ceiling, Cancellation));
            Assert.Equal(JournalAckStatus.Failed, await journal.EndAsync().WaitAsync(_ceiling, Cancellation));
            journal.RecordPrepared(JournalSamples.Prepared());
            journal.RecordStepFinished(JournalSamples.Outcome(step, step.TimeBudget, 12, JournalSamples.GiB, 0, true));
            journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(850));
            journal.Dispose();
            Assert.Equal(1, storage.BlockedCount);
        }
        finally
        {
            storage.ClearRules();
            storage.Release();
            journal.Dispose();
        }

        // The abandoned writer finishes the record it was stalled on and exits without deleting the journal.
        WaitUntil(() => CountOf(storage, PreparedType) == 1, "The stalled prepared record was never completed.");
        await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        Assert.Equal(new string?[] { BeginType, PreparedType }, storage.RecordTypesWritten());
        Assert.Equal(0, storage.DeleteCount);
        Assert.True(storage.Exists());
    }

    [Fact]
    public async Task Dispose_BeforeBegin_ClosesStorageAndRefusesBeginAsync()
    {
        using var storage = new StalledJournalStorage();
        var journal = new ProgressJournal(storage, JournalSamples.CreateTimeProvider());

        journal.Dispose();

        // With no writer thread, Dispose closes the storage itself.
        Assert.Throws<ObjectDisposedException>(() => storage.AppendDurable("{\"t\":\"probe\"}"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.BeginAsync(JournalSamples.BeginA(_t0)));
        Assert.False(storage.Exists());
        Assert.Null(journal.Failure);
    }

    [Fact]
    public void Dispose_StorageCloseFails_ReportsCloseFailure()
    {
        var storage = new ThrowingCleanupStorage { FailDispose = true };
        var journal = new ProgressJournal(storage, JournalSamples.CreateTimeProvider());

        journal.Dispose();

        JournalFailure failure = AssertFailure(journal, "close");
        Assert.StartsWith("The journal could not be closed: ", failure.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(failure.Exception);
        Assert.Equal(_t0, failure.OccurredUtc);
    }

    [Fact]
    public async Task BeginAsync_CalledTwice_ThrowsInvalidOperationAsync()
    {
        using var storage = new StalledJournalStorage();
        using var journal = new ProgressJournal(storage, JournalSamples.CreateTimeProvider());
        await BeginAcknowledgedAsync(journal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync(JournalSamples.BeginA(_t0)));
        Assert.Equal(new string?[] { BeginType }, storage.RecordTypesWritten());
    }

    [Fact]
    public async Task RecordMethods_BeforeBegin_ThrowInvalidOperationAsync()
    {
        using var storage = new StalledJournalStorage();
        using var journal = new ProgressJournal(storage, JournalSamples.CreateTimeProvider());
        StepAttributes step = JournalSamples.QuickSteps()[0];

        // begin must be the journal's first line.
        Assert.Throws<InvalidOperationException>(() => journal.RecordPrepared(JournalSamples.Prepared()));
        Assert.Throws<InvalidOperationException>(() => journal.RecordStepStarting(0, 6, step.Name));
        Assert.Throws<InvalidOperationException>(
            () => journal.RecordStepFinished(JournalSamples.Outcome(step, step.TimeBudget, 12, JournalSamples.GiB, 0, true)));
        Assert.Throws<InvalidOperationException>(
            () => journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(850)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RecordResultAsync(PassedResult()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.EndAsync());

        // Snapshots may be published at any time; they are only stored for the next heartbeat.
        journal.PublishProgress(JournalSamples.Progress(0, 6, TimeSpan.Zero, null, 0, JournalSamples.GiB, 0));
        journal.PublishReadings(JournalSamples.Readings(null, null));
        Assert.Equal(0, storage.AppendCallCount);
    }

    [Fact]
    public async Task RecordMethods_InvalidArguments_ThrowAsync()
    {
        using var storage = new StalledJournalStorage();
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        Assert.Throws<ArgumentNullException>(() => new ProgressJournal(null!, time));
        Assert.Throws<ArgumentNullException>(() => new ProgressJournal(storage, null!));
        using (var root = new TestDataRoot())
        {
            Assert.Throws<ArgumentNullException>(() => ProgressJournal.Create(null!, time));
            Assert.Throws<ArgumentNullException>(() => ProgressJournal.Create(root.Data, null!));
        }

        using var journal = new ProgressJournal(storage, time);
        await Assert.ThrowsAsync<ArgumentNullException>(() => journal.BeginAsync(null!));
        await BeginAcknowledgedAsync(journal);

        Assert.Throws<ArgumentNullException>(() => journal.RecordPrepared(null!));
        Assert.Throws<ArgumentNullException>(() => journal.RecordStepStarting(0, 6, null!));
        Assert.Throws<ArgumentNullException>(() => journal.RecordStepFinished(null!));
        Assert.Throws<ArgumentNullException>(() => journal.PublishProgress(null!));
        Assert.Throws<ArgumentNullException>(() => journal.PublishReadings(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(-1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => journal.RecordResultAsync(null!));

        // The terminal state arrives with the record and must be terminal.
        await Assert.ThrowsAsync<ArgumentException>(
            () => journal.RecordResultAsync(PassedResult() with { TerminalState = RunState.Running }));
        Assert.Equal(new string?[] { BeginType }, storage.RecordTypesWritten());
    }


    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Polls <paramref name="condition"/> every 10 ms in real time, because the writer thread runs
    /// asynchronously to the test, and fails with <paramref name="because"/> once <paramref name="timeout"/>
    /// expires.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string because)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > timeout)
            {
                Assert.True(condition(), because);
                return;
            }

            Thread.Sleep(10);
        }
    }

    private static void WaitUntil(Func<bool> condition, string because) => WaitUntil(condition, _ceiling, because);

    private static async Task<JournalBegin> BeginAcknowledgedAsync(ProgressJournal journal)
    {
        JournalBegin begin = JournalSamples.BeginA(_t0);
        JournalAckStatus status = await journal.BeginAsync(begin).WaitAsync(_ceiling, Cancellation);
        Assert.Equal(JournalAckStatus.Acknowledged, status);
        return begin;
    }

    private static JournalResult PassedResult() => JournalSamples.Result(
        RunState.Passed,
        RunEndReason.Completed,
        0,
        4 * JournalSamples.GiB,
        TimeSpan.FromSeconds(300),
        null,
        6,
        _t0 + TimeSpan.FromSeconds(301),
        _t0 + TimeSpan.FromMilliseconds(301_040));

    private static JournalFailure AssertFailure(ProgressJournal journal, string recordType)
    {
        JournalFailure? failure = journal.Failure;
        Assert.NotNull(failure);
        Assert.Equal(recordType, failure.RecordType);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        return failure;
    }

    private static void AssertReturnsPromptly(Action action, string what)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed < _nonBlockingBound, $"{what} took {elapsed} with the writer stalled.");
    }

    private static void OfferUnacknowledged(ProgressJournal journal, string recordType)
    {
        StepAttributes step = JournalSamples.QuickSteps()[0];
        switch (recordType)
        {
            case PreparedType:
                journal.RecordPrepared(JournalSamples.Prepared());
                break;
            case StepEndType:
                journal.RecordStepFinished(
                    JournalSamples.Outcome(step, step.TimeBudget, 12, 2 * JournalSamples.GiB, 0, true));
                break;
            case ReleasedType:
                journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(850));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(recordType), recordType, "Not an unacknowledged record type.");
        }
    }

    // Not inlined and not async, so no frame or state machine of the caller can keep the journal reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BeginAndAbandon(ThrowingCleanupStorage storage, FakeTimeProvider time)
    {
        var journal = new ProgressJournal(storage, time);
        Task<JournalAckStatus> begin = journal.BeginAsync(JournalSamples.BeginA(_t0));
        WaitUntil(() => begin.IsCompleted, "The begin record was not acknowledged.");
        Assert.Equal(new string?[] { BeginType }, storage.Inner.RecordTypesWritten());
        return new WeakReference(journal);
    }

    private static int CountOf(StalledJournalStorage storage, string recordType) =>
        storage.RecordTypesWritten().Count(type => type == recordType);

    private static IReadOnlyList<T> RecordsOf<T>(StalledJournalStorage storage)
        where T : JournalRecord =>
        storage.Lines.Select(Parse).OfType<T>().ToArray();

    private static JournalRecord Parse(string line)
    {
        Assert.True(JournalLineSerializer.TryParse(line, out JournalRecord? record), $"Unparseable journal line: {line}");
        Assert.NotNull(record);
        return record;
    }

    private static string? RecordTypeOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("t").GetString();
    }

    private static string UtcTextOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement utc = document.RootElement.GetProperty("utc");
        Assert.Equal(JsonValueKind.String, utc.ValueKind);
        return utc.GetString()!;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a new background thread and returns its result with the
    /// thread's managed id, rethrowing whatever it threw.
    /// </summary>
    private static (T Result, int ThreadId) RunOnDedicatedThread<T>(Func<T> work, string name)
    {
        T? result = default;
        int threadId = 0;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"The {name} thread did not finish.");
        failure?.Throw();
        return (result!, threadId);
    }

    /// <summary>
    /// Offers a one-step run's records in journal order without blocking on any task: begin, prepared, the
    /// gated step, stepEnd, one heartbeat at +1 s, result, then released and end after the clock has moved
    /// on to +2 s. Every record before the beat is stamped at <see cref="JournalSamples.StartUtc"/>.
    /// </summary>
    private static DrivenRun DriveFullRun(ProgressJournal journal, StalledJournalStorage storage, FakeTimeProvider time)
    {
        using var stopEvent = new ManualResetEvent(false);
        StepAttributes step = JournalSamples.QuickSteps()[0];
        JournalBegin begin = JournalSamples.BeginA(_t0);
        JournalPrepared prepared = JournalSamples.Prepared();
        StepOutcome outcome = JournalSamples.Outcome(step, step.TimeBudget, 12, 2 * JournalSamples.GiB, 0, true);
        RunProgress progress = JournalSamples.Progress(
            0,
            6,
            step.TimeBudget,
            TimeSpan.FromSeconds(260),
            2 * JournalSamples.GiB,
            JournalSamples.GiB,
            0);
        JournalReadings readings = JournalSamples.Readings(71.5, 48.0);
        JournalResult result = JournalSamples.Result(
            RunState.Passed,
            RunEndReason.Completed,
            0,
            2 * JournalSamples.GiB,
            step.TimeBudget,
            null,
            1,
            _t0 + TimeSpan.FromSeconds(1),
            _t0 + TimeSpan.FromSeconds(1));

        Task<JournalAckStatus> beginTask = journal.BeginAsync(begin);
        WaitUntil(() => beginTask.IsCompleted, "The begin record was not acknowledged.");
        journal.RecordPrepared(prepared);
        JournalAckStatus stepStatus = journal.RecordStepStarting(0, 6, step.Name)
            .Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);
        journal.RecordStepFinished(outcome);
        journal.PublishProgress(progress);
        journal.PublishReadings(readings);

        // The beat must be written before result stops the heartbeats.
        time.Advance(ProgressJournal.HeartbeatInterval);
        WaitUntil(() => CountOf(storage, BeatType) == 1, "The heartbeat was not written.");
        Task<JournalAckStatus> resultTask = journal.RecordResultAsync(result);
        time.Advance(ProgressJournal.HeartbeatInterval);
        journal.RecordReleased(JournalSamples.GiB, TimeSpan.FromMilliseconds(850));
        Task<JournalAckStatus> endTask = journal.EndAsync();
        return new DrivenRun(begin, prepared, outcome, progress, readings, result, beginTask, stepStatus, resultTask, endTask);
    }

    private static async Task AssertRunAcknowledgedAsync(DrivenRun run)
    {
        Assert.Equal(JournalAckStatus.Acknowledged, await run.BeginTask.WaitAsync(_ceiling, Cancellation));
        Assert.Equal(JournalAckStatus.Acknowledged, run.StepStatus);
        Assert.Equal(JournalAckStatus.Acknowledged, await run.ResultTask.WaitAsync(_ceiling, Cancellation));
        Assert.Equal(JournalAckStatus.Acknowledged, await run.EndTask.WaitAsync(_ceiling, Cancellation));
    }

    /// <summary>
    /// One sample of each journal record type, stamped to the millisecond so it round-trips exactly. Values
    /// differ from the run defaults and exercise nulls, flags, sub-millisecond truncation and escaped text.
    /// </summary>
    private static JournalRecord SampleRecord(string recordType)
    {
        StepAttributes turnaround = JournalSamples.QuickSteps()[2];
        DateTimeOffset completed = _t0 + TimeSpan.FromSeconds(76);
        StepOutcome interrupted = JournalSamples.Outcome(
            turnaround,
            TimeSpan.FromMilliseconds(51_250.9),
            9,
            28 * JournalSamples.GiB,
            2,
            false,
            TimeSpan.FromMilliseconds(1_250));
        RunProgress progress = JournalSamples.Progress(
            2,
            6,
            TimeSpan.FromMilliseconds(104_250),
            TimeSpan.FromSeconds(196),
            (4 * JournalSamples.GiB) + 1,
            JournalSamples.GiB,
            0);

        JournalRecord record = recordType switch
        {
            BeginType => JournalSamples.BeginB(_t0),
            PreparedType => JournalSamples.Prepared(16 * JournalSamples.GiB, 64, PageMode.Mixed, InstructionSet.Avx512, 3_750),
            StepType => new JournalStepStart { Index = 3, Count = 6, Name = turnaround.Name },
            StepEndType => JournalStepEnd.FromOutcome(interrupted),
            BeatType => JournalBeat.From(progress, JournalSamples.Readings(71.5, null)),
            ResultType => JournalSamples.Result(
                RunState.Failed,
                RunEndReason.FirstError,
                1,
                5 * JournalSamples.GiB,
                TimeSpan.FromMilliseconds(72_412),
                TimeSpan.FromMilliseconds(72_000),
                1,
                completed,
                completed + TimeSpan.FromMilliseconds(50),
                "Stopped on the first error at step 2 of 6 (\"Random bank thrash\")"),
            ReleasedType => new JournalReleased { ReleasedBytes = 16 * JournalSamples.GiB, ReleaseMs = 1_875 },
            EndType => new JournalEnd { RunId = JournalSamples.RunIdB, Published = true },
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, "Unknown journal record type."),
        };

        return record with { Utc = _t0 + TimeSpan.FromMilliseconds(93_750) };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcPattern();

    /// <summary>The inputs <see cref="DriveFullRun"/> offered and the acknowledgments it collected.</summary>
    private sealed record DrivenRun(
        JournalBegin Begin,
        JournalPrepared Prepared,
        StepOutcome Outcome,
        RunProgress Progress,
        JournalReadings Readings,
        JournalResult Result,
        Task<JournalAckStatus> BeginTask,
        JournalAckStatus StepStatus,
        Task<JournalAckStatus> ResultTask,
        Task<JournalAckStatus> EndTask);

    /// <summary>
    /// A <see cref="StalledJournalStorage"/> whose <see cref="Delete"/> or <see cref="Dispose"/> throws on
    /// demand, for the journal's delete and close failure paths, which the substitute itself never takes, and
    /// which reports whether its owner has closed it.
    /// </summary>
    private sealed class ThrowingCleanupStorage : IJournalStorage
    {
        private int _disposed;

        public StalledJournalStorage Inner { get; } = new();

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool FailDelete { get; init; }

        public bool FailDispose { get; init; }

        public string Location => Inner.Location;

        public bool Exists() => Inner.Exists();

        public void AppendDurable(string line) => Inner.AppendDurable(line);

        public IReadOnlyList<string> ReadLines() => Inner.ReadLines();

        public void Delete()
        {
            if (FailDelete)
            {
                throw new IOException("Simulated journal delete failure (ThrowingCleanupStorage)");
            }

            Inner.Delete();
        }

        public void Dispose()
        {
            Inner.Dispose();
            _ = Interlocked.Exchange(ref _disposed, 1);
            if (FailDispose)
            {
                throw new IOException("Simulated journal close failure (ThrowingCleanupStorage)");
            }
        }
    }
}

