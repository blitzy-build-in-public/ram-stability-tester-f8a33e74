using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RamQuick.Core;
using Xunit;

namespace RamQuick.Logging.Tests;

/// <summary>
/// Tests for <see cref="CrashDetector"/>: launch-time recovery of the progress journal a previous run left
/// behind. Covers no journal, a journal without a usable <c>begin</c>, an <c>end</c> record, a completed run
/// whose summaries are missing, kept, or blocked by foreign files, an unfinished run (in a step, while
/// preparing, behind a torn last line, recorded on another machine), a journal holding two runs, journal
/// values out of range and summary files with null array elements, and the failures that keep the journal so
/// the next launch retries.
/// </summary>
/// <remarks>
/// Every test roots its own <see cref="TestDataRoot"/>, so tests run in parallel. A journal is either a file
/// read through <see cref="FileJournalStorage"/> or lines seeded into a <see cref="StalledJournalStorage"/>,
/// whose append history shows the <c>end</c> record recovery writes before it deletes the journal. Published
/// summaries are asserted as JSON documents, because their value kinds are the schema, and are read back
/// through <see cref="RunSummaryJson.TryDeserialize"/> as well. <see cref="JournalRecovery"/> carries data
/// only; the messages built from it are asserted in RunControllerTests and CrashRecoveryProcessTests.
/// </remarks>
public sealed class CrashDetectorTests
{
    private const int StepCount = 3;
    private const string RandomBankThrash = "Random bank thrash";
    private const string EndType = "end";
    private const string SentinelVersion = "9.9.9-sentinel";
    private const string TornFragment = "{\"t\":\"beat\",\"step\":2,\"elapsedMs\":9";
    private const string FallbackBaseNamePrefix = "RamQuick-recovered-";
    private const string UnreadableJournalPrefix = "The progress journal could not be read: ";
    private const string DamagedJournalPrefix = "The progress journal holds damaged data: ";
    private const ulong ResultTotalErrors = 3;
    private const long ReleaseMs = 1_250;

    // The largest integer quantity the summary schema allows, 2^53 - 1.
    private const long MaxQuantity = 9_007_199_254_740_991;

    private static readonly Guid _runId = JournalSamples.RunIdA;
    private static readonly Guid _otherRunId = JournalSamples.RunIdB;
    private static readonly IReadOnlyList<StepAttributes> _steps = JournalSamples.QuickSteps(StepCount);

    // Start was accepted at 14:05:00 UTC, so the base name reads RamQuick-20261028-140500-Quick-<run id>.
    private static readonly DateTimeOffset _startRequestedUtc = new(2026, 10, 28, 14, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _runningUtc = _startRequestedUtc + TimeSpan.FromMilliseconds(1_250);
    private static readonly TimeSpan _step1Actual = TimeSpan.FromMilliseconds(40_250);
    private static readonly TimeSpan _step1Overrun = TimeSpan.FromMilliseconds(250);
    private static readonly DateTimeOffset _lastBeatUtc = _runningUtc + TimeSpan.FromSeconds(61);
    private static readonly DateTimeOffset _completedUtc = _runningUtc + TimeSpan.FromSeconds(75);
    private static readonly DateTimeOffset _verdictUtc = _completedUtc + TimeSpan.FromMilliseconds(40);

    // The next launch, an hour later: the instant that stamps the end records recovery appends.
    private static readonly DateTimeOffset _recoveryUtc = _startRequestedUtc + TimeSpan.FromHours(1);
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // Nothing to recover
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_NoJournal_ReturnsNone(bool useStorage)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        JournalRecovery recovery = useStorage
            ? CrashDetector.Detect(root.Data, storage, CreateRecoveryClock())
            : CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Same(JournalRecovery.None, recovery);
        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.False(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        Assert.Equal(0, storage.AppendCallCount);
        Assert.Equal(0, storage.DeleteCount);
        AssertLogsEmpty(root);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detect_EndPresent_DeletesJournalAndReturnsNone(bool endNamesRun)
    {
        using var root = new TestDataRoot();
        var end = new JournalEnd
        {
            RunId = endNamesRun ? _runId : Guid.Empty,
            Published = true,
            Utc = _verdictUtc + TimeSpan.FromSeconds(2),
        };
        WriteJournal(root, CompletedJournal(withReleased: true).Append(end).ToArray());

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Null(recovery.Summary);
        Assert.Empty(recovery.PublishedFiles);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        AssertLogsEmpty(root);
    }

    [Theory]
    [InlineData("Empty")]
    [InlineData("NoBegin")]
    [InlineData("InvalidBegin")]
    [InlineData("DiscriminatorNotFirst")]
    [InlineData("NotJson")]
    public void Detect_BeginMissingOrInvalid_ReturnsNone(string journalCase)
    {
        using var root = new TestDataRoot();
        WriteJournalLines(root, JournalWithoutUsableBegin(journalCase));
        Assert.True(File.Exists(root.Data.JournalPath));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // Begin is acknowledged before the engine starts, so a journal without one never carried traffic.
        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.Null(recovery.Summary);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        AssertLogsEmpty(root);
    }

    // ---------------------------------------------------------------------------------------------
    // Unfinished runs
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, 0UL)]
    [InlineData(true, 0UL)]
    [InlineData(false, 7UL)]
    public async Task Detect_MissingResult_ReturnsUnfinishedWithPartialFailSummaryAsync(bool useStorage, ulong lastBeatErrors)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        JournalRecord[] journal = UnfinishedJournal(lastBeatErrors: lastBeatErrors);

        JournalRecovery recovery;
        if (useStorage)
        {
            storage.Seed(JournalSamples.Lines(journal));
            recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());
        }
        else
        {
            WriteJournal(root, journal);
            recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());
        }

        await AssertUnfinishedRecoveryAsync(root, recovery, lastBeatErrors);
        if (useStorage)
        {
            AssertEndWrittenThenDeleted(storage, _runId);
        }
        else
        {
            Assert.False(File.Exists(root.Data.JournalPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detect_TornLastLine_IsIgnoredAsync(bool useStorage)
    {
        using var cleanRoot = new TestDataRoot();
        using var tornRoot = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        JournalRecord[] journal = UnfinishedJournal();
        WriteJournal(cleanRoot, journal);
        JournalRecovery clean = CrashDetector.Detect(cleanRoot.Data, CreateRecoveryClock());

        JournalRecovery torn;
        if (useStorage)
        {
            storage.Seed(JournalSamples.Lines(journal).Append(TornFragment));
            torn = CrashDetector.Detect(tornRoot.Data, storage, CreateRecoveryClock());
        }
        else
        {
            WriteJournal(tornRoot, journal, TornFragment);
            string onDisk = await File.ReadAllTextAsync(tornRoot.Data.JournalPath, Cancellation);
            Assert.EndsWith("\n" + TornFragment, onDisk, StringComparison.Ordinal);
            torn = CrashDetector.Detect(tornRoot.Data, CreateRecoveryClock());
        }

        // The last complete heartbeat (61 s, 120.5 %) is used, never the fragment's 9 ms.
        await AssertUnfinishedRecoveryAsync(tornRoot, torn, lastBeatErrors: 0);
        Assert.Equal(
            clean with { PublishedFiles = Array.Empty<string>() },
            torn with { PublishedFiles = Array.Empty<string>() });
        (string cleanJson, string cleanText) = FinalPaths(cleanRoot, _runId);
        (string tornJson, string tornText) = FinalPaths(tornRoot, _runId);
        Assert.Equal(
            await File.ReadAllTextAsync(cleanJson, Cancellation),
            await File.ReadAllTextAsync(tornJson, Cancellation));
        Assert.Equal(
            await File.ReadAllTextAsync(cleanText, Cancellation),
            await File.ReadAllTextAsync(tornText, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detect_CrashDuringPreparing_ReportsInPreparationAsync(bool preparedWritten)
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = preparedWritten
            ? new JournalRecord[] { BeginRecord(_runId), PreparedRecord() }
            : new JournalRecord[] { BeginRecord(_runId) };
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.True(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Null(recovery.LastStepName);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Null(recovery.LastHeartbeatUtc);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);

        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal("FAIL", StringAt(summary, "run.verdict"));
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), StringAt(summary, "run.endReason"));
        Assert.True(At(summary, "run.recovered").GetBoolean());
        Assert.True(At(summary, "run.partial").GetBoolean());
        Assert.Contains("while preparing memory (before step 1)", StringAt(summary, "run.endDetail"), StringComparison.Ordinal);
        Assert.Equal(0, At(summary, "steps").GetArrayLength());
        Assert.Equal(StepCount, At(summary, "unfinishedRun.stepCount").GetInt32());
        Assert.Equal(0UL, At(summary, "errors.total").GetUInt64());
        Assert.False(At(summary, "errors.detailAvailable").GetBoolean());
        AssertNullAt(
            summary,
            "run.endedUtc",
            "run.durationSeconds",
            "run.verdictUtc",
            "run.firstErrorSeconds",
            "memory.releaseSeconds",
            "unfinishedRun.lastStepIndex",
            "unfinishedRun.lastStepName",
            "unfinishedRun.lastHeartbeatUtc",
            "unfinishedRun.lastCoveragePercent");
        if (preparedWritten)
        {
            Assert.Equal(_runningUtc, At(summary, "run.startedUtc").GetDateTimeOffset());
            Assert.Equal(JournalSamples.GiB, At(summary, "memory.testedBytes").GetUInt64());
        }
        else
        {
            AssertNullAt(summary, "run.startedUtc", "memory.testedBytes", "memory.pageMode");
        }

        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Contains("No step started.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detect_MachineA_JournalRecoveredWhileEnvironmentIsMachineB_UsesOnlyJournalValuesAsync(bool useStorage)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // Machine B is the launch doing the recovery: its own published run sits in the logs folder, and the
        // process itself runs on the build host. Neither may reach the recovered summary of A's crash.
        (string machineBJson, string machineBText) = WriteMachineBRun(root);
        byte[] machineBJsonBytes = File.ReadAllBytes(machineBJson);
        byte[] machineBTextBytes = File.ReadAllBytes(machineBText);

        // No prepared record: preparation-time values were never captured, so they must stay null.
        JournalRecord[] journal = UnfinishedJournal(includePrepared: false);
        JournalRecovery recovery;
        if (useStorage)
        {
            storage.Seed(JournalSamples.Lines(journal));
            recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());
        }
        else
        {
            WriteJournal(root, journal);
            recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());
        }

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);
        Assert.Equal(machineBJsonBytes, File.ReadAllBytes(machineBJson));
        Assert.Equal(machineBTextBytes, File.ReadAllBytes(machineBText));

        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        SystemSnapshot machineA = JournalSamples.SystemA();
        JournalMemoryInfo memoryA = JournalSamples.MemoryA();
        Assert.Equal(JournalSamples.OsA, StringAt(summary, "system.os"));
        Assert.Equal(machineA.CpuName, StringAt(summary, "system.cpu"));
        Assert.Equal(machineA.LogicalProcessors, At(summary, "system.logicalProcessors").GetInt32());
        Assert.Equal(machineA.ProcessorGroups, At(summary, "system.processorGroups").GetInt32());
        Assert.Equal(SummaryFormat.InstructionSetName(machineA.BestIsa), StringAt(summary, "system.instructionSet"));
        Assert.False(At(summary, "system.reducedInstructionSet").GetBoolean());
        Assert.Equal(memoryA.InstalledBytes, At(summary, "memory.installedBytes").GetUInt64());
        Assert.Equal(memoryA.Type.ToString(), StringAt(summary, "memory.type"));
        Assert.Equal(memoryA.SpeedMTs, At(summary, "memory.speedMTs").GetInt32());
        Assert.Equal(memoryA.Channels, At(summary, "memory.channels").GetInt32());
        Assert.Equal(memoryA.Dimms, At(summary, "memory.dimms").GetInt32());
        Assert.Equal(machineA.AvailablePhysicalBytes, At(summary, "memory.availableBytesAtStart").GetUInt64());
        Assert.Equal(JournalSamples.GiB, At(summary, "memory.plannedBytes").GetUInt64());
        AssertNullAt(
            summary,
            "run.startedUtc",
            "memory.testedBytes",
            "memory.pageMode",
            "memory.addressKind",
            "memory.releaseSeconds",
            "steps.0.coveragePercent");

        SystemSnapshot machineB = JournalSamples.SystemB();
        JournalMemoryInfo memoryB = JournalSamples.MemoryB();
        Assert.NotEqual(JournalSamples.OsB, StringAt(summary, "system.os"));
        Assert.NotEqual(RuntimeInformation.OSDescription, StringAt(summary, "system.os"));
        Assert.NotEqual(Environment.OSVersion.VersionString, StringAt(summary, "system.os"));
        Assert.NotEqual(machineB.CpuName, StringAt(summary, "system.cpu"));
        Assert.NotEqual(machineB.LogicalProcessors, At(summary, "system.logicalProcessors").GetInt32());
        if (Environment.ProcessorCount != machineA.LogicalProcessors)
        {
            Assert.NotEqual(Environment.ProcessorCount, At(summary, "system.logicalProcessors").GetInt32());
        }

        Assert.NotEqual(machineB.ProcessorGroups, At(summary, "system.processorGroups").GetInt32());
        Assert.NotEqual(SummaryFormat.InstructionSetName(machineB.BestIsa), StringAt(summary, "system.instructionSet"));
        Assert.NotEqual(memoryB.InstalledBytes, At(summary, "memory.installedBytes").GetUInt64());
        Assert.NotEqual(memoryB.Type.ToString(), StringAt(summary, "memory.type"));
        Assert.NotEqual(memoryB.SpeedMTs, At(summary, "memory.speedMTs").GetInt32());
        Assert.NotEqual(memoryB.Channels, At(summary, "memory.channels").GetInt32());
        Assert.NotEqual(memoryB.Dimms, At(summary, "memory.dimms").GetInt32());
        Assert.NotEqual(machineB.AvailablePhysicalBytes, At(summary, "memory.availableBytesAtStart").GetUInt64());

        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        foreach (string foreign in new[] { machineB.CpuName, JournalSamples.OsB, RuntimeInformation.OSDescription })
        {
            Assert.DoesNotContain(foreign, json, StringComparison.Ordinal);
            Assert.DoesNotContain(foreign, text, StringComparison.Ordinal);
        }

        Assert.Contains(machineA.CpuName, text, StringComparison.Ordinal);
        Assert.Contains(JournalSamples.OsA, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detect_FileOverload_DefaultTimeProvider_WorksAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal());

        JournalRecovery recovery = CrashDetector.Detect(root.Data);

        await AssertUnfinishedRecoveryAsync(root, recovery, lastBeatErrors: 0);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_StorageOverload_SeededStorage_DeletesAndWritesEnd(bool completedRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal()));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Equal(new string?[] { EndType }, storage.RecordTypesWritten());
        AssertEndWrittenThenDeleted(storage, _runId);
        Assert.True(recovery.JournalDeleted);
        Assert.All(recovery.PublishedFiles, path => Assert.True(File.Exists(path), path));
        Assert.Equal(2, recovery.PublishedFiles.Count);
    }

    [Fact]
    public async Task Detect_UnfinishedRun_JsonAlreadyPublished_ReturnsNoneAndRendersMissingTextAsync()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(UnfinishedJournal()));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        RunSummary published = SentinelSummary(_runId);
        File.WriteAllText(jsonPath, RunSummaryJson.Serialize(published), _utf8NoBom);
        byte[] publishedBytes = File.ReadAllBytes(jsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // The journal stopped recording after the results were published, so the run is not "unfinished".
        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.False(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Equal(published, recovery.Summary);
        Assert.Equal(publishedBytes, File.ReadAllBytes(jsonPath));
        Assert.Equal(textPath, Assert.Single(recovery.PublishedFiles));
        AssertNoRecoveredFiles(root, _runId);
        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(TextSummaryFormatter.Format(published), text);
        Assert.Contains(SentinelVersion, text, StringComparison.Ordinal);
        Assert.Null(recovery.PublicationError);
        AssertEndWrittenThenDeleted(storage, _runId);
    }

    // ---------------------------------------------------------------------------------------------
    // Completed runs whose summaries were not all published
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Detect_ResultWithoutEnd_NoSummary_RebuildsBothAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.False(recovery.IsUnfinished);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Equal(JournalSamples.QuickTestType, recovery.TestType);
        Assert.False(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Null(recovery.StepCount);
        Assert.Null(recovery.LastStepName);
        Assert.Null(recovery.LastHeartbeatUtc);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);
        AssertNoRecoveredFiles(root, _runId);

        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        RunSummary published = ParseSummary(json);
        Assert.Equal(recovery.Summary, published);

        // Rebuilt from the journal, so recovered; the run reached its verdict, so not partial.
        Assert.True(published.Run.Recovered);
        Assert.False(published.Run.Partial);
        Assert.Null(published.UnfinishedRun);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal(SummaryFormat.RunId(_runId), StringAt(summary, "run.id"));
        Assert.Equal("FAIL", StringAt(summary, "run.verdict"));
        Assert.Equal(nameof(RunEndReason.FirstError), StringAt(summary, "run.endReason"));
        Assert.True(At(summary, "run.recovered").GetBoolean());
        Assert.False(At(summary, "run.partial").GetBoolean());
        Assert.Equal(_startRequestedUtc, At(summary, "run.startRequestedUtc").GetDateTimeOffset());
        Assert.Equal(_runningUtc, At(summary, "run.startedUtc").GetDateTimeOffset());
        Assert.Equal(_completedUtc, At(summary, "run.endedUtc").GetDateTimeOffset());
        Assert.Equal(_verdictUtc, At(summary, "run.verdictUtc").GetDateTimeOffset());
        Assert.Equal(75.0, At(summary, "run.durationSeconds").GetDouble());
        Assert.Equal((_verdictUtc - _startRequestedUtc).TotalSeconds, At(summary, "run.timeToVerdictSeconds").GetDouble());
        Assert.Equal(72.0, At(summary, "run.firstErrorSeconds").GetDouble());
        Assert.Equal(ResultTotalErrors, At(summary, "errors.total").GetUInt64());
        Assert.False(At(summary, "errors.detailAvailable").GetBoolean());
        Assert.False(At(summary, "errors.truncated").GetBoolean());
        Assert.Equal(0, At(summary, "errors.storedCount").GetInt32());
        Assert.Equal(0, At(summary, "errors.records").GetArrayLength());
        Assert.Equal(0, At(summary, "errors.groups").GetArrayLength());
        Assert.Equal(SummaryCauseHints.HintLabel, StringAt(summary, "causeHints.label"));
        Assert.Equal(0, At(summary, "causeHints.items").GetArrayLength());
        Assert.Equal(150.0, At(summary, "coverage.percent").GetDouble());
        Assert.Equal(ReleaseMs / 1000.0, At(summary, "memory.releaseSeconds").GetDouble());
        AssertNullAt(summary, "unfinishedRun");

        JsonElement steps = At(summary, "steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.True(At(steps[0], "completed").GetBoolean());
        Assert.Equal(0UL, At(steps[0], "errors").GetUInt64());
        Assert.Equal(2, At(steps[1], "index").GetInt32());
        Assert.False(At(steps[1], "completed").GetBoolean());
        Assert.Equal(ResultTotalErrors, At(steps[1], "errors").GetUInt64());
        Assert.Equal(50.0, At(steps[1], "coveragePercent").GetDouble());

        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Equal(TextSummaryFormatter.Format(published), text);
        Assert.Contains("rebuilt from the progress journal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This summary is partial", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detect_ResultWithoutEnd_ReleaseSecondsAsync(bool releasedWritten)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: releasedWritten));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        using JsonDocument document = JsonDocument.Parse(json);
        if (releasedWritten)
        {
            Assert.Equal(1.25, At(document.RootElement, "memory.releaseSeconds").GetDouble());
            Assert.Equal(1.25, recovery.Summary!.Memory.ReleaseSeconds);
            Assert.Contains("Release time: 1.25 s", text, StringComparison.Ordinal);
        }
        else
        {
            AssertNullAt(document.RootElement, "memory.releaseSeconds");
            Assert.Null(recovery.Summary!.Memory.ReleaseSeconds);
            Assert.Contains("Release time: not confirmed", text, StringComparison.Ordinal);
        }
    }


    [Fact]
    public async Task Detect_ResultWithoutEnd_JsonAlreadyPublished_KeepsItAndRebuildsTextAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        RunSummary kept = SentinelSummary(_runId);
        File.WriteAllText(jsonPath, RunSummaryJson.Serialize(kept), _utf8NoBom);
        byte[] keptBytes = File.ReadAllBytes(jsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(keptBytes, File.ReadAllBytes(jsonPath));
        Assert.Equal(kept, recovery.Summary);
        Assert.Equal(SentinelVersion, recovery.Summary!.App.Version);
        Assert.Equal(textPath, Assert.Single(recovery.PublishedFiles));
        AssertNoRecoveredFiles(root, _runId);

        // The missing text is rebuilt from the journal, never copied from the kept JSON summary.
        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Contains(JournalSamples.AppVersion, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelVersion, text, StringComparison.Ordinal);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_ResultWithoutEnd_TextAlreadyPublished_KeepsItAndRebuildsJsonAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        File.WriteAllText(textPath, TextSummaryFormatter.Format(SentinelSummary(_runId)), _utf8NoBom);
        byte[] keptBytes = File.ReadAllBytes(textPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(keptBytes, File.ReadAllBytes(textPath));
        Assert.Equal(jsonPath, Assert.Single(recovery.PublishedFiles));
        AssertNoRecoveredFiles(root, _runId);
        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        RunSummary rebuilt = ParseSummary(json);
        Assert.Equal(recovery.Summary, rebuilt);
        Assert.Equal(JournalSamples.AppVersion, rebuilt.App.Version);
        Assert.Equal(nameof(RunEndReason.FirstError), rebuilt.Run.EndReason);
        Assert.True(rebuilt.Run.Recovered);
        Assert.Null(recovery.PublicationError);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_FinalFileUnparsable_LeftUntouched_RecoveredSummaryPublishedAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        File.WriteAllText(jsonPath, "not json", _utf8NoBom);
        byte[] foreignBytes = File.ReadAllBytes(jsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(foreignBytes, File.ReadAllBytes(jsonPath));
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(SummaryFormat.RunId(_runId), recovered.Run.Id);
        Assert.Equal(recovery.Summary, recovered);

        // The text name was free, so the text summary is published under its final name.
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(await File.ReadAllTextAsync(textPath, Cancellation)));
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryTextPath(root.Data, BaseNameOf(_runId))));
        Assert.Null(recovery.PublicationError);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detect_FinalFileOtherRunId_LeftUntouched_RecoveredSummaryPublishedAsync(bool completedRun)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal());
        (string jsonPath, string textPath) = WriteForeignSummaries(root, BaseNameOf(_runId), SentinelSummary(_otherRunId));
        byte[] foreignJson = File.ReadAllBytes(jsonPath);
        byte[] foreignText = File.ReadAllBytes(textPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Equal(foreignJson, File.ReadAllBytes(jsonPath));
        Assert.Equal(foreignText, File.ReadAllBytes(textPath));
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        string recoveredTextPath = ResultsLog.GetRecoveredSummaryTextPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, recoveredTextPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(SummaryFormat.RunId(_runId), recovered.Run.Id);
        Assert.Equal(completedRun ? "FirstError" : "PreviousRunUnfinished", recovered.Run.EndReason);
        Assert.Equal(recovery.Summary, recovered);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(await File.ReadAllTextAsync(recoveredTextPath, Cancellation)));
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_FinalAndRecoveredJsonHeldByOtherFiles_ReportsErrorAndKeepsJournalAsync()
    {
        using var root = new TestDataRoot();
        string journalPath = WriteJournal(root, CompletedJournal(withReleased: true));
        byte[] journalBytes = File.ReadAllBytes(journalPath);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        File.WriteAllText(jsonPath, "not json", _utf8NoBom);
        File.WriteAllText(recoveredJsonPath, RunSummaryJson.Serialize(SentinelSummary(_otherRunId)), _utf8NoBom);
        byte[] finalBytes = File.ReadAllBytes(jsonPath);
        byte[] recoveredBytes = File.ReadAllBytes(recoveredJsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.NotNull(recovery.PublicationError);
        Assert.Contains(jsonPath, recovery.PublicationError, StringComparison.Ordinal);
        Assert.Contains(recoveredJsonPath, recovery.PublicationError, StringComparison.Ordinal);
        Assert.Equal(finalBytes, File.ReadAllBytes(jsonPath));
        Assert.Equal(recoveredBytes, File.ReadAllBytes(recoveredJsonPath));

        // The text name was free and is published; the JSON could not be, so the journal stays, without an end.
        Assert.Equal(textPath, Assert.Single(recovery.PublishedFiles));
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(await File.ReadAllTextAsync(textPath, Cancellation)));
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(journalBytes, File.ReadAllBytes(journalPath));
    }

    // ---------------------------------------------------------------------------------------------
    // Failures that keep the journal for the next launch
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Detect_PublicationFails_KeepsJournalAndSetsPublicationErrorAsync(bool completedRun, bool useStorage)
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal();
        IReadOnlyList<string> lines = JournalSamples.Lines(journal);
        JournalRecoveryKind expectedKind = completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished;
        using var firstStorage = new StalledJournalStorage();
        string journalPath = root.Data.JournalPath;
        byte[] journalBytes = Array.Empty<byte>();
        if (useStorage)
        {
            firstStorage.Seed(lines);
        }
        else
        {
            WriteJournal(root, journal);
            journalBytes = File.ReadAllBytes(journalPath);
        }

        // A file in place of the logs folder makes every summary write fail.
        string blockingFile = root.CreateBlockingFile("logs");

        JournalRecovery failed = useStorage
            ? CrashDetector.Detect(root.Data, firstStorage, CreateRecoveryClock())
            : CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(expectedKind, failed.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failed.PublicationError));
        Assert.Empty(failed.PublishedFiles);
        Assert.False(failed.JournalDeleted);
        Assert.NotNull(failed.Summary);
        if (useStorage)
        {
            Assert.Equal(0, firstStorage.AppendCallCount);
            Assert.Equal(0, firstStorage.DeleteCount);
            Assert.True(firstStorage.Exists());
            Assert.Equal(lines, firstStorage.ReadLines());
        }
        else
        {
            Assert.Equal(journalBytes, File.ReadAllBytes(journalPath));
        }

        // The next launch resolves the data folder again, which recreates the logs folder, and retries.
        File.Delete(blockingFile);
        Assert.True(root.Data.CanWrite());
        using var nextStorage = new StalledJournalStorage();
        if (useStorage)
        {
            nextStorage.Seed(firstStorage.ReadLines());
        }

        JournalRecovery retried = useStorage
            ? CrashDetector.Detect(root.Data, nextStorage, CreateRecoveryClock())
            : CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(expectedKind, retried.Kind);
        Assert.Null(retried.PublicationError);
        Assert.True(retried.JournalDeleted);
        Assert.Equal(failed.Summary, retried.Summary);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, retried.PublishedFiles);
        Assert.Equal(retried.Summary, ParseSummary(await File.ReadAllTextAsync(jsonPath, Cancellation)));
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(await File.ReadAllTextAsync(textPath, Cancellation)));
        if (useStorage)
        {
            AssertEndWrittenThenDeleted(nextStorage, _runId);
        }
        else
        {
            Assert.False(File.Exists(journalPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_JournalUnreadable_KeepsJournalAndReportsError(bool accessDenied)
    {
        using var root = new TestDataRoot();
        Exception readFailure = accessDenied
            ? new UnauthorizedAccessException("Simulated access denial")
            : new IOException("Simulated read failure");
        var storage = new FaultingJournalStorage(JournalSamples.Lines(UnfinishedJournal()), readFailure: readFailure);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Equal(UnreadableJournalPrefix + readFailure.Message, recovery.PublicationError);
        Assert.False(recovery.JournalDeleted);
        Assert.Null(recovery.Summary);
        Assert.Equal(0, storage.DeleteCalls);
        Assert.Empty(storage.Appended);
        Assert.True(storage.IsDisposed);
        AssertLogsEmpty(root);
    }

    [Fact]
    public void Detect_JournalDeleteFails_PublishesAndReportsJournalKept()
    {
        using var root = new TestDataRoot();
        var storage = new FaultingJournalStorage(
            JournalSamples.Lines(UnfinishedJournal()),
            deleteFailure: new IOException("Simulated delete failure"));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // The summaries and the end record are durable, so the next launch only finishes the cleanup.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(1, storage.DeleteCalls);
        JournalEnd end = ParseEnd(Assert.Single(storage.Appended));
        Assert.Equal(_runId, end.RunId);
        Assert.Equal(_recoveryUtc, end.Utc);
        Assert.All(recovery.PublishedFiles, path => Assert.True(File.Exists(path), path));
        Assert.Equal(2, recovery.PublishedFiles.Count);
        Assert.True(storage.IsDisposed);
    }

    [Fact]
    public void Detect_EndAppendFails_StillDeletesJournal()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(CompletedJournal(withReleased: true)));
        storage.FailAll(EndType);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // Both summaries are on disk, so the journal is retired even though its end record failed.
        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        AppendCall end = Assert.Single(storage.Calls);
        Assert.Equal(EndType, end.RecordType);
        Assert.Equal(AppendOutcome.Failed, end.Outcome);
        Assert.True(storage.IsDeleted);
        Assert.Equal(2, recovery.PublishedFiles.Count);
    }


    // ---------------------------------------------------------------------------------------------
    // Journal shapes beyond a single well-formed run
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Detect_JournalHoldsTwoRuns_RecoversEachAndReportsLatestAsync()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // A recovery that failed kept run H's journal, and run G's records were appended after it.
        storage.Seed(JournalSamples.Lines(UnfinishedJournal(runId: _otherRunId))
            .Concat(JournalSamples.Lines(CompletedJournal(withReleased: true))));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        (string otherJson, string otherText) = FinalPaths(root, _otherRunId);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { otherJson, otherText, jsonPath, textPath }, recovery.PublishedFiles);
        RunSummary other = ParseSummary(await File.ReadAllTextAsync(otherJson, Cancellation));
        Assert.Equal(SummaryFormat.RunId(_otherRunId), other.Run.Id);
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), other.Run.EndReason);
        Assert.True(other.Run.Partial);
        RunSummary latest = ParseSummary(await File.ReadAllTextAsync(jsonPath, Cancellation));
        Assert.Equal(nameof(RunEndReason.FirstError), latest.Run.EndReason);
        Assert.Equal(recovery.Summary, latest);

        Assert.Equal(new string?[] { EndType, EndType }, storage.RecordTypesWritten());
        Assert.Equal(
            new[] { _otherRunId, _runId },
            storage.Lines.Select(line => ParseEnd(line).RunId).ToArray());
        Assert.True(storage.IsDeleted);
    }

    [Fact]
    public void Detect_EndOfFirstRunOnly_RecoversSecondRunAndWritesOneEnd()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        var firstEnd = new JournalEnd { RunId = _otherRunId, Published = true, Utc = _startRequestedUtc };
        storage.Seed(JournalSamples.Lines(UnfinishedJournal(runId: _otherRunId).Append(firstEnd).ToArray())
            .Concat(JournalSamples.Lines(UnfinishedJournal())));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.False(File.Exists(FinalPaths(root, _otherRunId).Json));
        AssertEndWrittenThenDeleted(storage, _runId);
    }

    [Theory]
    [InlineData(2, RandomBankThrash)]
    [InlineData(9, null)]
    public void Detect_StepRecordWithoutName_TakesNameFromPlan(int stepNumber, string? expectedName)
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = UnfinishedJournal();

        // A begin record without a test type and a step record without a name still parse; the name comes from
        // the plan's definition for that step number, and there is none for a number outside the plan.
        journal[0] = (JournalBegin)journal[0] with { TestType = string.Empty };
        int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
        journal[stepIndex] = (JournalStepStart)journal[stepIndex] with { Index = stepNumber, Name = string.Empty };
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.TestType);
        Assert.Equal(stepNumber, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Equal(expectedName, recovery.LastStepName);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_FinalFileUnreadable_TreatedAsForeign_RecoveredSummaryPublishedAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        File.WriteAllText(jsonPath, RunSummaryJson.Serialize(SentinelSummary(_runId)), _utf8NoBom);
        byte[] lockedBytes = File.ReadAllBytes(jsonPath);

        // A file that cannot be read cannot be confirmed as this run's, so it is left alone like a foreign one.
        JournalRecovery recovery;
        using (new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());
        }

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(lockedBytes, File.ReadAllBytes(jsonPath));
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(JournalSamples.AppVersion, recovered.App.Version);
        Assert.Equal(recovery.Summary, recovered);
        Assert.Null(recovery.PublicationError);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_UnusableBaseName_PublishesUnderFallbackNameAsync()
    {
        using var root = new TestDataRoot();
        const string DamagedBaseName = "..\\escaped";
        WriteJournal(root, UnfinishedJournal(baseName: DamagedBaseName));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // A base name that is not a plain file name must never reach outside the logs folder.
        string fallback = FallbackBaseNamePrefix + SummaryFormat.RunId(_runId);
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(fallback, recovery.LogBaseName);
        string jsonPath = ResultsLog.GetSummaryJsonPath(root.Data, fallback);
        string textPath = ResultsLog.GetSummaryTextPath(root.Data, fallback);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);
        Assert.Equal(
            SummaryFormat.RunId(_runId),
            ParseSummary(await File.ReadAllTextAsync(jsonPath, Cancellation)).Run.Id);
        Assert.Equal(2, Directory.GetFiles(root.Data.LogsDirectory).Length);
        Assert.False(File.Exists(root.Combine("escaped-summary.json")));
        Assert.False(File.Exists(root.Combine("escaped-summary.txt")));
        Assert.Null(recovery.PublicationError);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    // ---------------------------------------------------------------------------------------------
    // Damaged data: journal values out of range, summary files with null array elements
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TimeBudgetOverflow", false, "step 1's timeBudgetMs in the begin record")]
    [InlineData("TimeBudgetOverflow", true, "step 1's timeBudgetMs in the begin record")]
    [InlineData("TimeBudgetTotal", true, "the steps' total timeBudgetMs in the begin record")]
    [InlineData("NegativeTimeBudget", false, "step 2's timeBudgetMs in the begin record")]
    [InlineData("HoldAboveBound", true, "step 3's holdMs in the begin record")]
    [InlineData("NegativeHold", false, "step 1's holdMs in the begin record")]
    [InlineData("NegativeCoverageBudget", true, "step 2's coverageBudgetPercent in the begin record")]
    [InlineData("AvailableBytes", false, "availableBytes in the begin record")]
    [InlineData("PlannedBytes", true, "plannedBytes in the begin record")]
    [InlineData("InstalledBytes", false, "memory.installedBytes in the begin record")]
    [InlineData("TestedBytes", true, "testedBytes in the prepared record")]
    [InlineData("StepEndActualMs", false, "step 1's actualMs in a stepEnd record")]
    [InlineData("StepEndOverrunMs", true, "step 1's overrunMs in a stepEnd record")]
    [InlineData("StepEndVerifiedBytes", false, "step 1's verifiedBytes in a stepEnd record")]
    [InlineData("StepEndErrors", true, "step 1's errors in a stepEnd record")]
    [InlineData("StepErrorsTotal", false, "the steps' total errors in the stepEnd records")]
    [InlineData("BeatElapsedMs", false, "elapsedMs in a beat record")]
    [InlineData("BeatErrors", true, "errors in a beat record")]
    [InlineData("ResultTotalErrors", true, "totalErrors in the result record")]
    [InlineData("ResultVerifiedBytes", true, "verifiedBytes in the result record")]
    [InlineData("ResultElapsedMs", true, "elapsedMs in the result record")]
    [InlineData("ResultFirstErrorMs", true, "firstErrorMs in the result record")]
    [InlineData("ReleaseMs", true, "releaseMs in the released record")]
    [InlineData("ReleasedBytes", true, "releasedBytes in the released record")]
    public void Detect_DamagedValue_PublishesNothingAndKeepsJournal(string damage, bool completedRun, string damagedField)
    {
        using var root = new TestDataRoot();
        string journalPath = WriteJournal(root, DamagedJournal(damage, completedRun));
        byte[] journalBytes = File.ReadAllBytes(journalPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // A summary rebuilt from these records would state a value no run measured, so none is published, and
        // the journal is kept without an end record where the message says it is.
        Assert.Equal(
            DamagedJournalPrefix + damagedField + " is out of range. The journal is kept at '"
                + Path.GetFullPath(journalPath) + "'.",
            recovery.PublicationError);
        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Null(recovery.Summary);
        Assert.Empty(recovery.PublishedFiles);
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(journalBytes, File.ReadAllBytes(journalPath));
        AssertLogsEmpty(root);
        if (!completedRun)
        {
            Assert.False(recovery.InPreparation);
            Assert.Equal(2, recovery.LastStepNumber);
            Assert.Equal(StepCount, recovery.StepCount);
            Assert.Equal(RandomBankThrash, recovery.LastStepName);
            Assert.Equal(_lastBeatUtc, recovery.LastHeartbeatUtc);
        }
    }

    [Fact]
    public async Task Detect_ValuesAtTheBoundAndNegativeMeasuredTimes_AreNotDamageAsync()
    {
        using var root = new TestDataRoot();

        // Steps 2 and 3 keep their 50 s budgets, so the plan's total is exactly the bound. Measured times a
        // clock step made negative are clamped when the summary is built, never reported as damage.
        const long OtherBudgetsMs = 100_000;
        JournalRecord[] journal = CompletedJournal(withReleased: true).Select(record => record switch
        {
            JournalBegin begin => WithStep(
                WithStep(begin, 0, step => step with { TimeBudgetMs = MaxQuantity - OtherBudgetsMs }),
                1,
                step => step with { HoldMs = MaxQuantity }) with
            {
                AvailableBytes = MaxQuantity,
                PlannedBytes = MaxQuantity,
                Memory = begin.Memory with { InstalledBytes = MaxQuantity },
            },
            JournalPrepared prepared => prepared with { TestedBytes = MaxQuantity },
            JournalStepEnd end => end with
            {
                ActualMs = MaxQuantity,
                OverrunMs = -1,
                VerifiedBytes = MaxQuantity,
                Errors = MaxQuantity,
            },
            JournalBeat beat => beat with { ElapsedMs = -1, Errors = MaxQuantity },
            JournalResult result => result with
            {
                TotalErrors = MaxQuantity,
                VerifiedBytes = MaxQuantity,
                ElapsedMs = MaxQuantity,
                FirstErrorMs = -1,
            },
            JournalReleased released => released with { ReleaseMs = -1, ReleasedBytes = MaxQuantity },
            _ => record,
        }).ToArray();
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.True(recovery.JournalDeleted);
        string json = await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal((ulong)MaxQuantity, At(summary, "memory.testedBytes").GetUInt64());
        Assert.Equal((ulong)MaxQuantity, At(summary, "memory.installedBytes").GetUInt64());
        Assert.Equal((ulong)MaxQuantity, At(summary, "errors.total").GetUInt64());
        Assert.Equal(MaxQuantity / 1000.0, At(summary, "plan.targetSeconds").GetDouble());
        Assert.Equal(MaxQuantity / 1000.0, At(summary, "steps.1.holdSeconds").GetDouble());
        Assert.Equal(0.0, At(summary, "steps.0.overrunSeconds").GetDouble());
        Assert.Equal(0.0, At(summary, "run.firstErrorSeconds").GetDouble());
        Assert.Equal(0.0, At(summary, "memory.releaseSeconds").GetDouble());
    }

    [Theory]
    [InlineData(false, (ulong)MaxQuantity)]
    [InlineData(true, ResultTotalErrors)]
    public async Task Detect_StepErrorsAboveTheBoundOnlyWhenEveryRecordIsSummed_AreNotDamageAsync(
        bool completedRun,
        ulong expectedTotal)
    {
        using var root = new TestDataRoot();

        // The unfinished run repeats step 1's stepEnd, and only the later one counts, so its total is exactly the
        // bound. The completed run's two stepEnds sum above it, but its total comes from the result record.
        JournalRecord[] journal = (completedRun
                ? CompletedJournal(withReleased: true)
                : UnfinishedJournal().Append(FirstStepEndRecord()))
            .Select(record => record is JournalStepEnd end ? end with { Errors = MaxQuantity } : record)
            .ToArray();
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.True(recovery.JournalDeleted);
        string json = await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expectedTotal, At(document.RootElement, "errors.total").GetUInt64());
        Assert.Equal((ulong)MaxQuantity, At(document.RootElement, "steps.0.errors").GetUInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_DamagedRunBeforeValidRun_PublishesValidRunAndKeepsJournal(bool completedRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // Run H's damaged records were kept, and run G's records were appended after them.
        storage.Seed(JournalSamples.Lines(DamagedJournal("TimeBudgetOverflow", completedRun: false, runId: _otherRunId))
            .Concat(JournalSamples.Lines(completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal())));
        IReadOnlyList<string> lines = storage.ReadLines();

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(
            DamagedJournalPrefix + "step 1's timeBudgetMs in the begin record is out of range. The journal is kept at '"
                + storage.Location + "'.",
            recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.Equal(recovery.Summary, ParseSummary(File.ReadAllText(FinalPaths(root, _runId).Json)));
        Assert.False(File.Exists(FinalPaths(root, _otherRunId).Json));
        Assert.False(File.Exists(FinalPaths(root, _otherRunId).Text));
        AssertNoRecoveredFiles(root, _otherRunId);

        // Run H is still unpublished, so neither run gets an end record and the journal stays as it was.
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(0, storage.AppendCallCount);
        Assert.Equal(0, storage.DeleteCount);
        Assert.Equal(lines, storage.ReadLines());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_DamagedJournal_SummariesAlreadyPublished_RetiresJournal(bool completedRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(DamagedJournal("TimeBudgetOverflow", completedRun)));
        RunSummary published = SentinelSummary(_runId);
        (string jsonPath, string textPath) = WriteForeignSummaries(root, BaseNameOf(_runId), published);
        byte[] jsonBytes = File.ReadAllBytes(jsonPath);
        byte[] textBytes = File.ReadAllBytes(textPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // Both of the run's summaries are on disk, so nothing is rebuilt from the damaged values.
        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.None,
            recovery.Kind);
        Assert.Equal(published, recovery.Summary);
        Assert.Null(recovery.PublicationError);
        Assert.Empty(recovery.PublishedFiles);
        Assert.Equal(jsonBytes, File.ReadAllBytes(jsonPath));
        Assert.Equal(textBytes, File.ReadAllBytes(textPath));
        AssertNoRecoveredFiles(root, _runId);
        Assert.True(recovery.JournalDeleted);
        AssertEndWrittenThenDeleted(storage, _runId);
    }

    [Theory]
    [InlineData("steps", false)]
    [InlineData("steps", true)]
    [InlineData("errors.groups", false)]
    [InlineData("errors.groups", true)]
    [InlineData("errors.records", false)]
    [InlineData("errors.records", true)]
    [InlineData("causeHints.items", false)]
    [InlineData("causeHints.items", true)]
    public async Task Detect_FinalJsonHoldsNullArrayElement_LeftUntouched_RecoveredSummaryPublishedAsync(
        string arrayPath,
        bool completedRun)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal());
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        File.WriteAllText(jsonPath, WithNullElement(DetailedSummary(_runId), arrayPath), _utf8NoBom);
        byte[] damagedBytes = File.ReadAllBytes(jsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // Schema 1 holds objects in that array, so the file does not parse: it is left alone like a foreign one.
        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(damagedBytes, File.ReadAllBytes(jsonPath));
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(SummaryFormat.RunId(_runId), recovered.Run.Id);
        Assert.True(recovered.Run.Recovered);
        Assert.Equal(!completedRun, recovered.Run.Partial);
        Assert.Equal(recovery.Summary, recovered);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(await File.ReadAllTextAsync(textPath, Cancellation)));
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryTextPath(root.Data, BaseNameOf(_runId))));
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Theory]
    [InlineData("steps")]
    [InlineData("errors.groups")]
    [InlineData("errors.records")]
    [InlineData("causeHints.items")]
    public void TryDeserialize_NullArrayElement_ReturnsFalse(string arrayPath)
    {
        RunSummary summary = DetailedSummary(_runId);
        Assert.True(RunSummaryJson.TryDeserialize(RunSummaryJson.Serialize(summary), out RunSummary? intact));
        Assert.Equal(summary, intact);

        Assert.False(RunSummaryJson.TryDeserialize(WithNullElement(summary, arrayPath), out RunSummary? damaged));
        Assert.Null(damaged);
    }

    // ---------------------------------------------------------------------------------------------
    // Arguments
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Detect_NullDataDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>("dataDirectory", () => CrashDetector.Detect(null!));
    }

    [Fact]
    public void Detect_NullStorage_Throws()
    {
        using var root = new TestDataRoot();

        Assert.Throws<ArgumentNullException>(
            "storage",
            () => CrashDetector.Detect(root.Data, null!, CreateRecoveryClock()));
    }

    [Theory]
    [InlineData("dataDirectory")]
    [InlineData("timeProvider")]
    public void Detect_NullArgumentWithStorage_ThrowsAndDisposesStorage(string nullArgument)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(UnfinishedJournal()));
        DataDirectory? dataDirectory = nullArgument == "dataDirectory" ? null : root.Data;
        TimeProvider? timeProvider = nullArgument == "timeProvider" ? null : CreateRecoveryClock();

        Assert.Throws<ArgumentNullException>(
            nullArgument,
            () => CrashDetector.Detect(dataDirectory!, storage, timeProvider!));

        // Ownership passed to Detect, which disposed the storage and left the journal as it was.
        Assert.Throws<ObjectDisposedException>(() => storage.AppendDurable("{\"t\":\"end\"}"));
        Assert.True(storage.Exists());
        Assert.Equal(0, storage.DeleteCount);
        AssertLogsEmpty(root);
    }

    // ---------------------------------------------------------------------------------------------
    // Assertions
    // ---------------------------------------------------------------------------------------------

    // The full contract of the standard unfinished journal: a 3-step run killed in step 2.
    private static async Task AssertUnfinishedRecoveryAsync(TestDataRoot root, JournalRecovery recovery, ulong lastBeatErrors)
    {
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.True(recovery.IsUnfinished);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Equal(JournalSamples.QuickTestType, recovery.TestType);
        Assert.False(recovery.InPreparation);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.Equal(_lastBeatUtc, recovery.LastHeartbeatUtc);
        Assert.Null(recovery.PublicationError);
        Assert.True(recovery.JournalDeleted);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);
        AssertNoRecoveredFiles(root, _runId);

        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        RunSummary published = ParseSummary(json);
        Assert.Equal(recovery.Summary, published);
        Assert.True(published.Run.Recovered);
        Assert.True(published.Run.Partial);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal(RunSummary.CurrentSchemaVersion, At(summary, "schemaVersion").GetInt32());
        Assert.Equal(SummaryFormat.RunId(_runId), StringAt(summary, "run.id"));
        Assert.Equal(JournalSamples.QuickTestType, StringAt(summary, "run.testType"));
        Assert.Equal("FAIL", StringAt(summary, "run.verdict"));
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), StringAt(summary, "run.endReason"));
        Assert.True(At(summary, "run.recovered").GetBoolean());
        Assert.True(At(summary, "run.partial").GetBoolean());
        Assert.Equal(_startRequestedUtc, At(summary, "run.startRequestedUtc").GetDateTimeOffset());
        Assert.Equal(_runningUtc, At(summary, "run.startedUtc").GetDateTimeOffset());
        Assert.Equal(_lastBeatUtc, At(summary, "run.endedUtc").GetDateTimeOffset());
        Assert.Equal(61.0, At(summary, "run.durationSeconds").GetDouble());
        string endDetail = StringAt(summary, "run.endDetail");
        Assert.Contains("did not finish at step 2 of 3 (Random bank thrash)", endDetail, StringComparison.Ordinal);
        Assert.Contains("approx", endDetail, StringComparison.OrdinalIgnoreCase);
        AssertNullAt(
            summary,
            "run.verdictUtc",
            "run.timeToVerdictSeconds",
            "run.firstErrorSeconds",
            "memory.releaseSeconds");

        Assert.Equal(JournalSamples.GiB, At(summary, "memory.testedBytes").GetUInt64());
        Assert.Equal(nameof(PageMode.Standard), StringAt(summary, "memory.pageMode"));
        Assert.Equal("AVX2", StringAt(summary, "system.instructionSet"));
        Assert.Equal(140.0, At(summary, "plan.targetSeconds").GetDouble());
        Assert.Equal(95.0, At(summary, "plan.targetCoveragePercent").GetDouble());
        Assert.Equal(120.5, At(summary, "coverage.percent").GetDouble());

        // Peaks fold every heartbeat: the CPU peak is the last one's, the RAM peak an earlier one's.
        Assert.Equal(71.5, At(summary, "temperatures.cpuMaxC").GetDouble());
        Assert.Equal(49.5, At(summary, "temperatures.ramMaxC").GetDouble());
        Assert.Equal(JournalSamples.AvailableStatus, StringAt(summary, "temperatures.cpuStatus"));

        JsonElement steps = At(summary, "steps");
        Assert.Equal(2, steps.GetArrayLength());
        JsonElement finished = steps[0];
        Assert.Equal(1, At(finished, "index").GetInt32());
        Assert.Equal(_steps[0].Name, StringAt(finished, "name"));
        Assert.True(At(finished, "completed").GetBoolean());
        Assert.Equal(40.25, At(finished, "actualSeconds").GetDouble());
        Assert.Equal(0.25, At(finished, "overrunSeconds").GetDouble());
        Assert.Equal(1, At(finished, "passes").GetInt32());
        Assert.Equal(100.0, At(finished, "coveragePercent").GetDouble());
        Assert.Equal(0UL, At(finished, "errors").GetUInt64());
        JsonElement running = steps[1];
        Assert.Equal(2, At(running, "index").GetInt32());
        Assert.Equal(RandomBankThrash, StringAt(running, "name"));
        Assert.False(At(running, "completed").GetBoolean());
        Assert.Equal(50.0, At(running, "plannedSeconds").GetDouble());
        AssertNullAt(running, "actualSeconds", "overrunSeconds", "passes", "coveragePercent", "errors");

        Assert.Equal(lastBeatErrors, At(summary, "errors.total").GetUInt64());
        Assert.False(At(summary, "errors.detailAvailable").GetBoolean());
        Assert.False(At(summary, "errors.truncated").GetBoolean());
        Assert.Equal(0, At(summary, "errors.storedCount").GetInt32());
        Assert.Equal(0, At(summary, "errors.records").GetArrayLength());
        Assert.Equal(0, At(summary, "errors.groups").GetArrayLength());
        Assert.Equal(SummaryCauseHints.HintLabel, StringAt(summary, "causeHints.label"));
        Assert.Equal(0, At(summary, "causeHints.items").GetArrayLength());

        Assert.Equal(2, At(summary, "unfinishedRun.lastStepIndex").GetInt32());
        Assert.Equal(RandomBankThrash, StringAt(summary, "unfinishedRun.lastStepName"));
        Assert.Equal(StepCount, At(summary, "unfinishedRun.stepCount").GetInt32());
        Assert.Equal(_lastBeatUtc, At(summary, "unfinishedRun.lastHeartbeatUtc").GetDateTimeOffset());
        Assert.Equal(120.5, At(summary, "unfinishedRun.lastCoveragePercent").GetDouble());

        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Contains("approx", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TextSummaryFormatter.Format(published), text);
    }

    private static void AssertEndWrittenThenDeleted(StalledJournalStorage storage, Guid runId)
    {
        AppendCall call = Assert.Single(storage.Calls);
        Assert.Equal(EndType, call.RecordType);
        Assert.Equal(AppendOutcome.Succeeded, call.Outcome);
        JournalEnd end = ParseEnd(call.Line);
        Assert.Equal(runId, end.RunId);
        Assert.True(end.Published);
        Assert.Equal(_recoveryUtc, end.Utc);
        Assert.True(storage.IsDeleted);
        Assert.Equal(1, storage.DeleteCount);
        Assert.False(storage.Exists());
    }

    private static void AssertLogsEmpty(TestDataRoot root) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Data.LogsDirectory));

    private static void AssertNoRecoveredFiles(TestDataRoot root, Guid runId)
    {
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(runId))));
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryTextPath(root.Data, BaseNameOf(runId))));
    }

    // Each path must hold JSON null: a value the run never captured is written as null, never omitted.
    private static void AssertNullAt(JsonElement element, params string[] paths)
    {
        foreach (string path in paths)
        {
            Assert.Equal((path, JsonValueKind.Null), (path, At(element, path).ValueKind));
        }
    }

    // Follows a dotted path through objects; a numeric segment indexes an array.
    private static JsonElement At(JsonElement element, string path)
    {
        JsonElement current = element;
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                Assert.InRange(index, 0, current.GetArrayLength() - 1);
                current = current[index];
                continue;
            }

            Assert.True(current.TryGetProperty(segment, out JsonElement child), $"The summary has no '{path}' value.");
            current = child;
        }

        return current;
    }

    private static string StringAt(JsonElement element, string path)
    {
        JsonElement value = At(element, path);
        Assert.Equal((path, JsonValueKind.String), (path, value.ValueKind));
        return value.GetString()!;
    }

    private static RunSummary ParseSummary(string json)
    {
        Assert.True(RunSummaryJson.TryDeserialize(json, out RunSummary? summary), "The summary JSON does not read back.");
        return summary;
    }

    private static JournalEnd ParseEnd(string line)
    {
        Assert.True(JournalLineSerializer.TryParse(line, out JournalRecord? record), $"'{line}' is not a journal record.");
        return Assert.IsType<JournalEnd>(record);
    }


    // ---------------------------------------------------------------------------------------------
    // Journal and summary builders
    // ---------------------------------------------------------------------------------------------

    private static FakeTimeProvider CreateRecoveryClock() => new(_recoveryUtc);

    private static string BaseNameOf(Guid runId) => JournalSamples.BaseName(runId, _startRequestedUtc);

    private static (string Json, string Text) FinalPaths(TestDataRoot root, Guid runId) => (
        ResultsLog.GetSummaryJsonPath(root.Data, BaseNameOf(runId)),
        ResultsLog.GetSummaryTextPath(root.Data, BaseNameOf(runId)));

    private static string[] FinalPathList(TestDataRoot root, Guid runId)
    {
        (string jsonPath, string textPath) = FinalPaths(root, runId);
        return new[] { jsonPath, textPath };
    }

    // Machine A's begin record for the first three Quick steps.
    private static JournalBegin BeginRecord(Guid runId, string? baseName = null)
    {
        JournalBegin begin = JournalSamples.Begin(
            runId,
            baseName ?? BaseNameOf(runId),
            JournalSamples.SystemA(),
            JournalSamples.OsA,
            JournalSamples.MemoryA(),
            _startRequestedUtc,
            _steps);
        return begin with { Utc = _startRequestedUtc };
    }

    private static JournalPrepared PreparedRecord() => JournalSamples.Prepared() with { Utc = _runningUtc };

    private static JournalStepStart StepStartRecord(int number, TimeSpan elapsed) => new()
    {
        Index = number,
        Count = StepCount,
        Name = _steps[number - 1].Name,
        Utc = _runningUtc + elapsed,
    };

    private static JournalStepEnd FirstStepEndRecord()
    {
        StepOutcome outcome = JournalSamples.Outcome(
            _steps[0],
            _step1Actual,
            1,
            JournalSamples.GiB,
            0,
            completed: true,
            overrun: _step1Overrun);
        return JournalStepEnd.FromOutcome(outcome) with { Utc = _runningUtc + _step1Actual };
    }

    private static JournalBeat BeatRecord(int step, long elapsedMs, double coverage, ulong errors, double cpuC, double ramC) => new()
    {
        Step = step,
        ElapsedMs = elapsedMs,
        Coverage = coverage,
        Errors = errors,
        CpuC = cpuC,
        RamC = ramC,
        CpuStatus = JournalSamples.AvailableStatus,
        RamStatus = JournalSamples.AvailableStatus,
        Utc = _runningUtc + TimeSpan.FromMilliseconds(elapsedMs),
    };

    // A 3-step Quick run killed in step 2, "Random bank thrash", 61 s into the test at 120.5 % coverage.
    private static JournalRecord[] UnfinishedJournal(
        Guid? runId = null,
        ulong lastBeatErrors = 0,
        bool includePrepared = true,
        string? baseName = null)
    {
        var records = new List<JournalRecord> { BeginRecord(runId ?? _runId, baseName) };
        if (includePrepared)
        {
            records.Add(PreparedRecord());
        }

        records.Add(StepStartRecord(1, TimeSpan.Zero));
        records.Add(BeatRecord(1, 20_000, 50.0, 0, 65.5, 49.5));
        records.Add(FirstStepEndRecord());
        records.Add(StepStartRecord(2, _step1Actual));
        records.Add(BeatRecord(2, 50_000, 110.25, 0, 70.0, 47.5));
        records.Add(BeatRecord(2, 61_000, 120.5, lastBeatErrors, 71.5, 48.0));
        return records.ToArray();
    }

    // The same run reaching its verdict instead: FAIL by first error in step 2, 3 errors, 75 s of test time.
    private static JournalRecord[] CompletedJournal(bool withReleased, Guid? runId = null)
    {
        TimeSpan elapsed = TimeSpan.FromSeconds(75);
        StepOutcome secondStep = JournalSamples.Outcome(
            _steps[1],
            elapsed - _step1Actual,
            0,
            JournalSamples.GiB / 2,
            ResultTotalErrors,
            completed: false);
        JournalResult result = JournalSamples.Result(
            RunState.Failed,
            RunEndReason.FirstError,
            ResultTotalErrors,
            JournalSamples.GiB + (JournalSamples.GiB / 2),
            elapsed,
            TimeSpan.FromSeconds(72),
            1,
            _completedUtc,
            _verdictUtc);
        var records = new List<JournalRecord>
        {
            BeginRecord(runId ?? _runId),
            PreparedRecord(),
            StepStartRecord(1, TimeSpan.Zero),
            BeatRecord(1, 20_000, 50.0, 0, 65.5, 49.5),
            FirstStepEndRecord(),
            StepStartRecord(2, _step1Actual),
            BeatRecord(2, 61_000, 120.5, 0, 71.5, 48.0),
            JournalStepEnd.FromOutcome(secondStep) with { Utc = _completedUtc },
            result with { Utc = _verdictUtc },
        };
        if (withReleased)
        {
            records.Add(new JournalReleased
            {
                ReleasedBytes = JournalSamples.GiB,
                ReleaseMs = ReleaseMs,
                Utc = _completedUtc + TimeSpan.FromMilliseconds(1_300),
            });
        }

        return records.ToArray();
    }

    private static IReadOnlyList<string> JournalWithoutUsableBegin(string journalCase)
    {
        IReadOnlyList<string> body = JournalSamples.Lines(UnfinishedJournal().Skip(1).ToArray());
        string runIdText = _runId.ToString("D", CultureInfo.InvariantCulture);
        return journalCase switch
        {
            "Empty" => Array.Empty<string>(),
            "NoBegin" => body,
            "InvalidBegin" => body.Prepend("{\"t\":\"begin\",\"runId\":\"not-a-guid\",\"logBaseName\":\"x\"}").ToArray(),

            // .NET 8 System.Text.Json reads a polymorphic record only when its discriminator comes first.
            "DiscriminatorNotFirst" => body.Prepend("{\"runId\":\"" + runIdText + "\",\"t\":\"begin\"}").ToArray(),
            "NotJson" => new[] { "not json", "{" },
            _ => throw new ArgumentOutOfRangeException(nameof(journalCase), journalCase, "Unknown journal case."),
        };
    }

    // A valid summary of the given run that recovery cannot have produced: its app version is a sentinel.
    private static RunSummary SentinelSummary(Guid runId)
    {
        RunSummary summary = RunSummary.Build(
            JournalSamples.CompletedRunSource(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty, runId: runId));
        return summary with { App = new SummaryApp { Name = RunSummarySource.DefaultAppName, Version = SentinelVersion } };
    }

    // A valid summary of the given run with stored errors and a hint, so each of its object arrays has elements.
    private static RunSummary DetailedSummary(Guid runId)
    {
        var hints = new SummaryCauseHints
        {
            Items = new[]
            {
                new SummaryCauseHint
                {
                    Rank = 1,
                    Category = "Row timings",
                    LikelihoodPercent = 100.0,
                    Suggestion = "Loosen tRCD and tRP.",
                    Evidence = "Errors found under bank-stride order only.",
                },
            },
        };
        return RunSummary.Build(JournalSamples.CompletedRunSource(JournalSamples.ErrorRecords(3), hints, runId: runId));
    }

    // The summary's JSON document with a null element appended to the array at the dotted path.
    private static string WithNullElement(RunSummary summary, string arrayPath)
    {
        JsonNode document = JsonNode.Parse(RunSummaryJson.Serialize(summary))!;
        JsonArray array = arrayPath.Split('.').Aggregate(document, (node, name) => node[name]!).AsArray();
        Assert.NotEmpty(array);
        array.Add(null);
        return document.ToJsonString();
    }

    // The standard unfinished or completed journal with one value no run writes, named by the damage case.
    private static JournalRecord[] DamagedJournal(string damage, bool completedRun, Guid? runId = null)
    {
        JournalRecord[] journal = completedRun ? CompletedJournal(withReleased: true, runId) : UnfinishedJournal(runId);
        if (damage == "StepErrorsTotal")
        {
            // Every stepEnd value is within the bound, but the total an unfinished run's errors.total is built
            // from, the sum over steps 1 and 2, is one above it.
            JournalStepEnd secondStepEnd = JournalStepEnd.FromOutcome(
                JournalSamples.Outcome(_steps[1], TimeSpan.FromSeconds(20), 1, JournalSamples.GiB, 2, completed: true));
            journal = journal.Append(secondStepEnd with { Utc = _lastBeatUtc }).ToArray();
        }

        return journal.Select(record => (damage, record) switch
        {
            // The finding's case: two budgets whose sum overflows a long.
            ("TimeBudgetOverflow", JournalBegin begin) => WithStep(
                WithStep(begin, 0, step => step with { TimeBudgetMs = long.MaxValue }),
                1,
                step => step with { TimeBudgetMs = 1 }),
            ("TimeBudgetTotal", JournalBegin begin) => WithStep(begin, 0, step => step with { TimeBudgetMs = MaxQuantity }),
            ("NegativeTimeBudget", JournalBegin begin) => WithStep(begin, 1, step => step with { TimeBudgetMs = -1 }),
            ("HoldAboveBound", JournalBegin begin) => WithStep(begin, 2, step => step with { HoldMs = MaxQuantity + 1 }),
            ("NegativeHold", JournalBegin begin) => WithStep(begin, 0, step => step with { HoldMs = -1 }),
            ("NegativeCoverageBudget", JournalBegin begin) => WithStep(begin, 1, step => step with { CoverageBudgetPercent = -1 }),
            ("AvailableBytes", JournalBegin begin) => begin with { AvailableBytes = ulong.MaxValue },
            ("PlannedBytes", JournalBegin begin) => begin with { PlannedBytes = MaxQuantity + 1 },
            ("InstalledBytes", JournalBegin begin) => begin with { Memory = begin.Memory with { InstalledBytes = ulong.MaxValue } },
            ("TestedBytes", JournalPrepared prepared) => prepared with { TestedBytes = ulong.MaxValue },
            ("StepEndActualMs", JournalStepEnd end) => end with { ActualMs = long.MaxValue },
            ("StepEndOverrunMs", JournalStepEnd end) => end with { OverrunMs = MaxQuantity + 1 },
            ("StepEndVerifiedBytes", JournalStepEnd end) => end with { VerifiedBytes = ulong.MaxValue },
            ("StepEndErrors", JournalStepEnd end) => end with { Errors = ulong.MaxValue },
            ("StepErrorsTotal", JournalStepEnd { Index: 1 } end) => end with { Errors = MaxQuantity },
            ("BeatElapsedMs", JournalBeat beat) => beat with { ElapsedMs = long.MaxValue },
            ("BeatErrors", JournalBeat beat) => beat with { Errors = ulong.MaxValue },
            ("ResultTotalErrors", JournalResult result) => result with { TotalErrors = ulong.MaxValue },
            ("ResultVerifiedBytes", JournalResult result) => result with { VerifiedBytes = MaxQuantity + 1 },
            ("ResultElapsedMs", JournalResult result) => result with { ElapsedMs = long.MaxValue },
            ("ResultFirstErrorMs", JournalResult result) => result with { FirstErrorMs = MaxQuantity + 1 },
            ("ReleaseMs", JournalReleased released) => released with { ReleaseMs = long.MaxValue },
            ("ReleasedBytes", JournalReleased released) => released with { ReleasedBytes = ulong.MaxValue },
            _ => record,
        }).ToArray();
    }

    private static JournalBegin WithStep(JournalBegin begin, int position, Func<JournalStep, JournalStep> change)
    {
        JournalStep[] steps = begin.Steps.ToArray();
        steps[position] = change(steps[position]);
        return begin with { Steps = steps };
    }

    // Machine B's own run, published by the launch that now recovers machine A's journal.
    private static (string Json, string Text) WriteMachineBRun(TestDataRoot root)
    {
        DateTimeOffset start = _recoveryUtc - TimeSpan.FromMinutes(10);
        DateTimeOffset running = start + TimeSpan.FromSeconds(1);
        DateTimeOffset completed = running + TimeSpan.FromSeconds(140);
        SystemSnapshot machineB = JournalSamples.SystemB();
        JournalBegin begin = JournalSamples.BeginB(start, StepCount) with { Utc = start };
        JournalPrepared prepared = JournalSamples.Prepared(workers: machineB.LogicalProcessors, isaSelected: machineB.BestIsa);
        JournalResult result = JournalSamples.Result(
            RunState.Passed,
            RunEndReason.Completed,
            0,
            3 * JournalSamples.GiB,
            TimeSpan.FromSeconds(140),
            null,
            StepCount,
            completed,
            completed + TimeSpan.FromMilliseconds(40));
        RunSummary summary = RunSummary.Build(new RunSummarySource
        {
            Begin = begin,
            Prepared = prepared with { Utc = running },
            Result = result with { Utc = completed },
            Released = new JournalReleased
            {
                ReleasedBytes = JournalSamples.GiB,
                ReleaseMs = 900,
                Utc = completed + TimeSpan.FromSeconds(1),
            },
        });
        return WriteForeignSummaries(root, begin.LogBaseName, summary);
    }

    private static (string Json, string Text) WriteForeignSummaries(TestDataRoot root, string baseName, RunSummary summary)
    {
        string jsonPath = ResultsLog.GetSummaryJsonPath(root.Data, baseName);
        string textPath = ResultsLog.GetSummaryTextPath(root.Data, baseName);
        File.WriteAllText(jsonPath, RunSummaryJson.Serialize(summary), _utf8NoBom);
        File.WriteAllText(textPath, TextSummaryFormatter.Format(summary), _utf8NoBom);
        return (jsonPath, textPath);
    }

    private static string WriteJournal(TestDataRoot root, params JournalRecord[] records) =>
        WriteJournalLines(root, JournalSamples.Lines(records));

    private static string WriteJournal(TestDataRoot root, JournalRecord[] records, string tornFragment) =>
        WriteJournalLines(root, JournalSamples.Lines(records), tornFragment);

    // One line plus "\n" per record, as FileJournalStorage writes them, then an optional fragment without a
    // terminator, as a crash during an append leaves it.
    private static string WriteJournalLines(TestDataRoot root, IEnumerable<string> lines, string? tornFragment = null)
    {
        var content = new StringBuilder();
        foreach (string line in lines)
        {
            content.Append(line).Append('\n');
        }

        content.Append(tornFragment);
        Directory.CreateDirectory(root.Data.JournalDirectory);
        File.WriteAllText(root.Data.JournalPath, content.ToString(), _utf8NoBom);
        return root.Data.JournalPath;
    }

    /// <summary>
    /// A journal whose read or delete fails, for the I/O failure paths <see cref="StalledJournalStorage"/>
    /// cannot script. Appends always succeed and are recorded.
    /// </summary>
    private sealed class FaultingJournalStorage : IJournalStorage
    {
        private readonly List<string> _lines;
        private readonly List<string> _appended = new();
        private readonly Exception? _readFailure;
        private readonly Exception? _deleteFailure;

        public FaultingJournalStorage(IEnumerable<string> lines, Exception? readFailure = null, Exception? deleteFailure = null)
        {
            _lines = new List<string>(lines);
            _readFailure = readFailure;
            _deleteFailure = deleteFailure;
        }

        public string Location => "memory://faulting/active-run.jsonl";

        public IReadOnlyList<string> Appended => _appended.ToArray();

        public int DeleteCalls { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool Exists() => true;

        public void AppendDurable(string line) => _appended.Add(line);

        public IReadOnlyList<string> ReadLines() => _readFailure is null ? _lines.ToArray() : throw _readFailure;

        public void Delete()
        {
            DeleteCalls++;
            if (_deleteFailure is not null)
            {
                throw _deleteFailure;
            }

            _lines.Clear();
        }

        public void Dispose() => IsDisposed = true;
    }
}

