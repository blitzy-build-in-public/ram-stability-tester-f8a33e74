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
/// the next launch retries. Untrusted step and base names are covered too, with the
/// <see cref="SummaryFormat.PlainText"/> and <see cref="SummaryFormat.StepName"/> rules recovery holds them to.
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
    private const ulong ResultTotalErrors = 3;
    private const long ReleaseMs = 1_250;

    // The largest integer quantity the summary schema allows, 2^53 - 1.
    private const long MaxQuantity = 9_007_199_254_740_991;

    // The most UTF-8 bytes a step name may hold: the native rq_step_def.name field is 64 bytes and NUL-terminated.
    private const int MaxStepNameUtf8Bytes = 63;

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
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // One character more than the free text a record may carry into a summary.
    private static readonly string _tooLongText = new('t', 1_025);

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

    [Fact]
    public void Detect_EndPresent_DeletesJournalAndReturnsNone()
    {
        using var root = new TestDataRoot();
        var end = new JournalEnd { RunId = _runId, Published = true, Utc = _verdictUtc + TimeSpan.FromSeconds(2) };
        WriteJournal(root, CompletedJournal(withReleased: true).Append(end).ToArray());

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Null(recovery.Summary);
        Assert.Empty(recovery.PublishedFiles);
        Assert.Null(recovery.PublicationError);
        Assert.Null(recovery.JournalDamage);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        AssertLogsEmpty(root);
    }

    // An end without an id ends only a run whose id is unknown, and one naming another run ends none of this journal's
    // runs; either way it is not this run's end.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_EndOfAnotherRun_IsIgnoredAndRunPublished(bool namesAnotherRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        var end = new JournalEnd
        {
            RunId = namesAnotherRun ? _otherRunId : Guid.Empty,
            Published = true,
            Utc = _verdictUtc + TimeSpan.FromSeconds(2),
        };
        storage.Seed(JournalSamples.Lines(CompletedJournal(withReleased: true).Append(end).ToArray()));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.Equal("an end record of another run was ignored", recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        AssertEndWrittenThenMovedAside(storage, recovery, _runId);
    }

    [Theory]
    [InlineData("Empty")]
    [InlineData("TornBegin")]
    [InlineData("TornBeginAfterBlankLine")]
    [InlineData("BlankLines")]
    public void Detect_JournalWithoutRunRecords_ReturnsNone(string journalCase)
    {
        using var root = new TestDataRoot();
        (IReadOnlyList<string> lines, string? torn) = JournalWithoutUsableBegin(journalCase);
        WriteJournalLines(root, lines, torn);
        Assert.True(File.Exists(root.Data.JournalPath));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // Begin is acknowledged before the engine starts, so a journal that holds no more than a begin torn by a
        // crash during its own append, a last line with no terminator after it, never carried traffic.
        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.Null(recovery.Summary);
        Assert.Null(recovery.PublicationError);
        Assert.Null(recovery.JournalDamage);
        Assert.True(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        AssertLogsEmpty(root);
    }

    // Only the storage can tell whether the last line was terminated. A lone line that does not parse is a begin torn
    // during its own append only when it has no terminator; a storage that holds it complete reports a run whose begin
    // could not be read.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detect_LoneUnparseableLine_IsTornBeginOnlyWithoutItsTerminator(bool unterminated)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        string begin = JournalLineSerializer.Serialize(BeginRecord(_runId));
        storage.Seed(new[] { begin[..(begin.Length / 2)] });
        storage.LastLineUnterminated = unterminated;

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        if (unterminated)
        {
            Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
            Assert.Null(recovery.JournalDamage);
            Assert.True(recovery.JournalDeleted);
            Assert.Equal(1, storage.DeleteCount);
            Assert.Equal(0, storage.AppendCallCount);
            AssertLogsEmpty(root);
            return;
        }

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.Equal("the journal's begin record is missing or could not be read", recovery.JournalDamage);
        Assert.Equal(2, recovery.PublishedFiles.Count);
        AssertEndWrittenThenMovedAside(storage, recovery, Guid.Empty);
    }

    // A journal whose begin record is missing or does not parse still carried a run, so it is reported as unfinished
    // and published with what the rest of the journal holds, never deleted in silence.
    [Theory]
    [InlineData("NoBegin", "the journal's begin record is missing or could not be read")]
    [InlineData("NotJson", "the journal's begin record is missing or could not be read")]
    [InlineData("LoneLineNotJson", "the journal's begin record is missing or could not be read")]
    [InlineData("LoneLineRunIdOnly", "the journal's begin record is missing or could not be read")]
    [InlineData("BadRunIdFormat", "the begin record was damaged: runId could not be read")]
    [InlineData("RunIdNFormatOnly", "the begin record was damaged: runId could not be read")]
    [InlineData("BadStartRequestedUtc", "the begin record was damaged: startRequestedUtc could not be read")]
    [InlineData("BadStopOnError", "the begin record was damaged: stopOnError could not be read")]
    [InlineData("DiscriminatorNotFirst", "the begin record was damaged")]
    public async Task Detect_BeginMissingOrDamaged_RecoversUnfinishedRunAndMovesJournalAsideAsync(
        string journalCase,
        string expectedDamage)
    {
        using var root = new TestDataRoot();
        IReadOnlyList<string> lines = JournalWithDamagedBegin(journalCase);
        string journalPath = WriteJournalLines(root, lines);
        byte[] journalBytes = File.ReadAllBytes(journalPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // A lone line that does not parse is a complete line, with its terminator, so it is no begin torn by a crash
        // during its own append: it is a run whose begin could not be read.
        bool unidentified = journalCase is "NotJson" or "LoneLineNotJson" or "LoneLineRunIdOnly";
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(expectedDamage, recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        Assert.False(recovery.JournalDeleted);
        Assert.False(File.Exists(root.Data.JournalPath));
        AssertMovedAsideWithEnds(root, recovery, journalBytes, unidentified ? Guid.Empty : _runId);

        // The begin's base name is kept when it survived; otherwise the run is named after its id, which the
        // stepEnd record supplies, and a journal without one after a hash of its first line.
        bool beginBaseNameRead = journalCase is "BadRunIdFormat" or "BadStartRequestedUtc" or "BadStopOnError" or "DiscriminatorNotFirst";
        string expectedBaseName = unidentified
            ? FallbackBaseNamePrefix + AnonymousName(lines[0])
            : beginBaseNameRead ? BaseNameOf(_runId) : FallbackBaseNamePrefix + SummaryFormat.RunId(_runId);
        Assert.Equal(expectedBaseName, recovery.LogBaseName);
        Assert.Equal(unidentified ? null : _runId, recovery.RunId);
        string jsonPath = ResultsLog.GetSummaryJsonPath(root.Data, expectedBaseName);
        Assert.Equal(new[] { jsonPath, ResultsLog.GetSummaryTextPath(root.Data, expectedBaseName) }, recovery.PublishedFiles);

        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal("FAIL", StringAt(summary, "run.verdict"));
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), StringAt(summary, "run.endReason"));
        Assert.True(At(summary, "run.partial").GetBoolean());
        Assert.EndsWith(
            " The progress journal held damaged data: " + expectedDamage + ".",
            StringAt(summary, "run.endDetail"),
            StringComparison.Ordinal);

        string text = await File.ReadAllTextAsync(recovery.PublishedFiles[1], Cancellation);
        if (unidentified)
        {
            // Nothing of the run could be read: the step at which it stopped is unknown, never "before step 1", and
            // every field is null rather than a value the journal never held, its traffic figures included.
            Assert.False(recovery.InPreparation);
            Assert.Null(recovery.LastStepNumber);
            Assert.Null(recovery.StepCount);
            Assert.Contains("did not finish at an unknown step", StringAt(summary, "run.endDetail"), StringComparison.Ordinal);
            AssertNullAt(
                summary,
                "app.version",
                "run.id",
                "run.testType",
                "run.stopOnError",
                "run.startRequestedUtc",
                "run.endedUtc",
                "system.os",
                "memory.installedBytes",
                "plan.targetSeconds",
                "plan.targetCoveragePercent",
                "coverage.percent",
                "coverage.targetPercent",
                "coverage.targetReached",
                "errors.total",
                "unfinishedRun.stepCount");
            Assert.True(At(summary, "run.recovered").GetBoolean());

            // The text says so in words, and its run-id line still identifies it as this run's own on a retry.
            Assert.Equal(Guid.Empty, TextSummaryFormatter.TryParseRunId(text));
            Assert.True(TextSummaryFormatter.StatesRecovered(text));
            Assert.Contains("\r\nStop on error: Unknown\r\n", text, StringComparison.Ordinal);
            Assert.Contains("\r\nTotal errors (true count): Unknown\r\n", text, StringComparison.Ordinal);
            Assert.Contains("\r\nUnknown (target Unknown) \u2014 target reached: Unknown\r\n", text, StringComparison.Ordinal);
            Assert.Contains(
                "\r\nThe previous test did not finish at an unknown step, which likely indicates severe instability.\r\n",
                text,
                StringComparison.Ordinal);
            return;
        }

        // The step records survived, so the message keeps the AAP form "did not finish at step n of N (name)".
        Assert.False(recovery.InPreparation);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.Contains("did not finish at step 2 of 3 (Random bank thrash)", StringAt(summary, "run.endDetail"), StringComparison.Ordinal);
        Assert.Equal(SummaryFormat.RunId(_runId), StringAt(summary, "run.id"));
        Assert.Equal(_runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Equal(_lastBeatUtc, At(summary, "run.endedUtc").GetDateTimeOffset());

        // The heartbeats and the step record carry the traffic figures and the plan's size whatever became of the begin.
        Assert.Equal(120.5, At(summary, "coverage.percent").GetDouble());
        Assert.Equal(0UL, At(summary, "errors.total").GetUInt64());
        Assert.Equal(StepCount, At(summary, "unfinishedRun.stepCount").GetInt32());
        switch (journalCase)
        {
            case "NoBegin":
            case "RunIdNFormatOnly":
                // Every value the begin record carries was lost with it.
                AssertNullAt(
                    summary,
                    "app.version",
                    "run.testType",
                    "run.stopOnError",
                    "run.startRequestedUtc",
                    "system.os",
                    "system.cpu",
                    "memory.installedBytes",
                    "memory.plannedBytes",
                    "plan.targetSeconds",
                    "plan.targetCoveragePercent",
                    "coverage.targetPercent",
                    "coverage.targetReached");
                Assert.Contains("\r\nTest type: Unknown\r\nStop on error: Unknown\r\nTarget duration: Unknown\r\nTarget coverage: Unknown\r\n", text, StringComparison.Ordinal);
                break;
            case "BadStartRequestedUtc":
                AssertNullAt(summary, "run.startRequestedUtc");
                Assert.Equal(JournalSamples.OsA, StringAt(summary, "system.os"));
                break;
            case "BadStopOnError":
                // A salvaged begin keeps every field that read; only Stop on error was not captured.
                AssertNullAt(summary, "run.stopOnError");
                Assert.Equal(JournalSamples.QuickTestType, StringAt(summary, "run.testType"));
                Assert.Equal(JournalSamples.AppVersion, StringAt(summary, "app.version"));
                Assert.Equal(_startRequestedUtc, At(summary, "run.startRequestedUtc").GetDateTimeOffset());
                Assert.Equal(140.0, At(summary, "plan.targetSeconds").GetDouble());
                Assert.Equal(95.0, At(summary, "coverage.targetPercent").GetDouble());
                Assert.True(At(summary, "coverage.targetReached").GetBoolean());
                Assert.Contains("\r\nStop on error: Unknown\r\n", text, StringComparison.Ordinal);
                break;
            default:
                Assert.Equal(_startRequestedUtc, At(summary, "run.startRequestedUtc").GetDateTimeOffset());
                Assert.Equal(JournalSamples.OsA, StringAt(summary, "system.os"));
                Assert.Equal(2, At(summary, "steps").GetArrayLength());
                break;
        }
    }

    [Fact]
    public async Task Detect_WholeJournalUtf16_RecoversUnfinishedRunAtUnknownStepUnderHashedNameAsync()
    {
        using var root = new TestDataRoot();
        var content = new StringBuilder();
        foreach (string line in JournalSamples.Lines(UnfinishedJournal()))
        {
            content.Append(line).Append('\n');
        }

        // Saved by an editor as UTF-16LE with its byte-order mark: no line reads as UTF-8 JSON.
        Directory.CreateDirectory(root.Data.JournalDirectory);
        File.WriteAllText(root.Data.JournalPath, content.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        byte[] journalBytes = File.ReadAllBytes(root.Data.JournalPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.False(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Equal("the journal's begin record is missing or could not be read", recovery.JournalDamage);
        Assert.StartsWith(FallbackBaseNamePrefix, recovery.LogBaseName, StringComparison.Ordinal);
        Assert.Matches("^RamQuick-recovered-[0-9a-f]{32}$", recovery.LogBaseName);
        Assert.Equal(2, recovery.PublishedFiles.Count);
        Assert.Contains(
            "did not finish at an unknown step",
            ParseSummary(await File.ReadAllTextAsync(recovery.PublishedFiles[0], Cancellation)).Run.EndDetail,
            StringComparison.Ordinal);
        AssertMovedAsideWithEnds(root, recovery, journalBytes, Guid.Empty);

        // Another unidentified journal is named after its own first line, so the two never share files.
        using var otherRoot = new TestDataRoot();
        WriteJournalLines(otherRoot, new[] { "not json", "{" });
        JournalRecovery other = CrashDetector.Detect(otherRoot.Data, CreateRecoveryClock());
        Assert.Matches("^RamQuick-recovered-[0-9a-f]{32}$", other.LogBaseName);
        Assert.NotEqual(recovery.LogBaseName, other.LogBaseName);
    }

    [Fact]
    public async Task Detect_JournalWithUtf8ByteOrderMark_RecoversAsWrittenAsync()
    {
        using var root = new TestDataRoot();
        string journalPath = WriteJournal(root, UnfinishedJournal());
        byte[] withoutMark = File.ReadAllBytes(journalPath);
        File.WriteAllBytes(journalPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(withoutMark).ToArray());

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // The mark an editor added is not part of the begin line, so the journal recovers as if it had none.
        await AssertUnfinishedRecoveryAsync(root, recovery, lastBeatErrors: 0);
        Assert.Null(recovery.JournalDamage);
        Assert.False(File.Exists(journalPath));
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

    [Fact]
    public async Task Detect_LinkAtFinalJsonName_NeverReadOrReplaced_RecoveredSummaryPublishedAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);

        // The link points to a file that would pass as this run's summary if it were read through the link.
        string target = root.CreateLinkTarget();
        string pointedTo = Path.Combine(target, "pointed-to-summary.json");
        File.WriteAllText(pointedTo, RunSummaryJson.Serialize(SentinelSummary(_runId)), _utf8NoBom);
        byte[] pointedToBytes = File.ReadAllBytes(pointedTo);
        TestDataRoot.CreateFileSymbolicLinkOrSkip(jsonPath, pointedTo);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.True(recovered.Run.Recovered);
        Assert.Equal(recovery.Summary, recovered);
        Assert.Equal(pointedToBytes, File.ReadAllBytes(pointedTo));
        Assert.True(File.GetAttributes(jsonPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.Null(recovery.PublicationError);
        Assert.False(File.Exists(root.Data.JournalPath));
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
    [InlineData(2, false, RandomBankThrash)]
    [InlineData(2, true, RandomBankThrash)]
    [InlineData(9, false, null)]
    [InlineData(9, true, null)]
    public void Detect_StepRecordWithoutName_TakesNameFromPlan(int stepNumber, bool onlyInvisibleCharacters, string? expectedName)
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = UnfinishedJournal();

        // A begin record without a test type and a step record without a name still parse; the name comes from
        // the plan's definition for that step number, and there is none for a number outside the plan. A name of
        // nothing but line breaks, escape, NUL, format characters and spaces shows nothing, so it is as blank.
        journal[0] = (JournalBegin)journal[0] with { TestType = string.Empty };
        int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
        string name = onlyInvisibleCharacters ? "\r\n\u001b\u0000\u202E\u200B \u2066\uFEFF" : string.Empty;
        journal[stepIndex] = (JournalStepStart)journal[stepIndex] with { Index = stepNumber, Name = name };
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.TestType);
        Assert.Equal(stepNumber, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Equal(expectedName, recovery.LastStepName);
        Assert.Equal(expectedName, recovery.Summary?.UnfinishedRun?.LastStepName);
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

    // A base name that is not a plain file name, that is too long for every file of the run, or that holds a
    // control or format character falls back.
    [Theory]
    [InlineData("ParentFolder")]
    [InlineData("LongName300")]
    [InlineData("LongNameAboveMax")]
    [InlineData("BidiOverride")]
    [InlineData("NextLineControl")]
    public async Task Detect_UnusableBaseName_PublishesUnderFallbackNameAsync(string baseNameCase)
    {
        using var root = new TestDataRoot();
        string damagedBaseName = baseNameCase switch
        {
            "ParentFolder" => "..\\escaped",
            "LongName300" => new string('A', 300),
            "LongNameAboveMax" => new string('B', ResultsLog.MaxBaseNameLength + 1),
            "BidiOverride" => BaseNameOf(_runId) + "\u202Etxt.exe",
            "NextLineControl" => BaseNameOf(_runId) + "\u0085escaped",
            _ => throw new ArgumentOutOfRangeException(nameof(baseNameCase), baseNameCase, "Unknown base name case."),
        };
        WriteJournal(root, UnfinishedJournal(baseName: damagedBaseName));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // A base name that is not a plain file name must never reach outside the logs folder, and one holding a
        // control or format character, which Windows accepts in a file name, must never reach a published file
        // name or a notice that names the file.
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
    // Untrusted step names: one line of plain text within the 63-byte limit
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LineInjection", false)]
    [InlineData("LineInjection", true)]
    [InlineData("SpoofedPass", false)]
    [InlineData("SpoofedPass", true)]
    [InlineData("InvisibleCharacters", false)]
    [InlineData("InvisibleCharacters", true)]
    [InlineData("TenThousandCharacters", false)]
    [InlineData("TenThousandCharacters", true)]
    public async Task Detect_UntrustedStepName_RecoveredAsOnePlainLineWithinTheLimitAsync(string nameCase, bool onlyInDefinition)
    {
        using var root = new TestDataRoot();
        (string planted, string expected) = UntrustedStepName(nameCase);
        JournalRecord[] journal = UnfinishedJournal();

        // The killed step's definition holds the name as well, so steps[] names the step with it; when the step
        // record holds no name, the definition alone names the step.
        journal[0] = WithStep((JournalBegin)journal[0], 1, step => step with { Name = planted });
        int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
        journal[stepIndex] = (JournalStepStart)journal[stepIndex] with { Name = onlyInDefinition ? string.Empty : planted };
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(expected, recovery.LastStepName);
        AssertPlainText(expected);
        Assert.InRange(Encoding.UTF8.GetByteCount(expected), 1, MaxStepNameUtf8Bytes);
        Assert.Null(recovery.PublicationError);

        // A name longer than the plan's limit is also damage, so the journal is kept aside as evidence.
        bool tooLong = Encoding.UTF8.GetByteCount(planted) > MaxStepNameUtf8Bytes;
        Assert.Equal(
            !tooLong ? null
                : onlyInDefinition ? "step 2's name in the begin record is out of range"
                : "step 2's name in a step record is out of range",
            recovery.JournalDamage);
        Assert.Equal(tooLong, recovery.DamagedJournalPath is not null);
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        Assert.Equal(new[] { jsonPath, textPath }, recovery.PublishedFiles);

        // The JSON escapes every character, so its values are compared as read back.
        string json = await File.ReadAllTextAsync(jsonPath, Cancellation);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        string position = "did not finish at step 2 of 3 (" + expected + ")";
        Assert.Equal(expected, StringAt(summary, "unfinishedRun.lastStepName"));
        Assert.Equal(expected, StringAt(summary, "steps.1.name"));
        string endDetail = StringAt(summary, "run.endDetail");
        Assert.StartsWith("The previous test " + position + ", which likely indicates severe instability.", endDetail, StringComparison.Ordinal);
        AssertPlainText(endDetail);

        string text = await File.ReadAllTextAsync(textPath, Cancellation);
        Assert.Equal(TextSummaryFormatter.Format(ParseSummary(json)), text);
        string[] lines = text.Split("\r\n");
        Assert.Contains("Detail: " + endDetail, lines);
        Assert.Contains("The previous test " + position + ", which likely indicates severe instability.", lines);
        Assert.Single(lines, line => line.Contains(expected, StringComparison.Ordinal) && line.TrimStart().StartsWith("2 ", StringComparison.Ordinal));
        foreach (string line in lines)
        {
            AssertPlainText(line);
        }

        Assert.False(File.Exists(root.Data.JournalPath));
    }

    [Fact]
    public async Task Detect_KeptSummaryHoldsControlAndFormatCharacters_RendersMissingTextAsPlainLinesAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal());
        (string jsonPath, string textPath) = FinalPaths(root, _runId);

        // A kept summary is not rebuilt, so only the text renderer keeps its free text to one plain line: neither
        // the forged line nor the bidirectional and zero-width characters may reach the text.
        const string Forged = "Random\u202E bank\r\nSPOOFED: PASS\u2066 thrash\u200B";
        RunSummary sentinel = SentinelSummary(_runId);
        RunSummary kept = sentinel with
        {
            Run = sentinel.Run with { EndDetail = "Engine error\u202E: " + Forged },
            Steps = sentinel.Steps.Select(step => step with { Name = Forged }).ToArray(),
        };
        Assert.NotEmpty(kept.Steps);
        File.WriteAllText(jsonPath, RunSummaryJson.Serialize(kept), _utf8NoBom);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Equal(kept, recovery.Summary);
        Assert.Equal(textPath, Assert.Single(recovery.PublishedFiles));
        string[] lines = (await File.ReadAllTextAsync(textPath, Cancellation)).Split("\r\n");
        Assert.DoesNotContain(lines, line => line.StartsWith("SPOOFED", StringComparison.Ordinal));
        Assert.Contains("Detail: Engine error : Random  bank  SPOOFED: PASS  thrash", lines);
        Assert.Equal(
            kept.Steps.Count,
            lines.Count(line => line.Contains("  Random  bank  SPOOFED: PASS  thrash  ", StringComparison.Ordinal)));
        foreach (string line in lines)
        {
            AssertPlainText(line);
        }
    }

    [Theory]
    [InlineData("NulEscapeAndLineBreaks", "a b c  d e")]
    [InlineData("C1Controls", "a b c")]
    [InlineData("BidiControls", " a b c d e f g h ")]
    [InlineData("ZeroWidthAndSoftHyphen", "a b c d e f g h i")]
    [InlineData("Separators", "a b c")]
    [InlineData("SupplementaryFormat", "a b")]
    [InlineData("UnpairedHighSurrogate", "a\uFFFDb")]
    [InlineData("HighSurrogateAtEnd", "a\uFFFD")]
    [InlineData("UnpairedLowSurrogate", "\uFFFDa")]
    public void SummaryFormat_PlainText_ReplacesEachControlFormatAndSeparatorCharacter(string textCase, string expected)
    {
        string text = textCase switch
        {
            "NulEscapeAndLineBreaks" => "a\u0000b\u001bc\r\nd\te",
            "C1Controls" => "a\u0085b\u009bc",
            "BidiControls" => "\u202Aa\u202Bb\u202Cc\u202Dd\u202Ee\u2066f\u2067g\u2068h\u2069",
            "ZeroWidthAndSoftHyphen" => "a\u200Bb\u200Cc\u200Dd\u200Ee\u200Ff\u2060g\uFEFFh\u00ADi",
            "Separators" => "a\u2028b\u2029c",

            // U+E0041, a tag character: one format character written as a surrogate pair becomes one space.
            "SupplementaryFormat" => "a\U000E0041b",
            "UnpairedHighSurrogate" => "a\uD800b",
            "HighSurrogateAtEnd" => "a\uD83D",
            "UnpairedLowSurrogate" => "\uDC00a",
            _ => throw new ArgumentOutOfRangeException(nameof(textCase), textCase, "Unknown text case."),
        };

        string plain = SummaryFormat.PlainText(text);

        Assert.Equal(expected, plain);
        AssertPlainText(plain);
    }

    [Theory]
    [MemberData(nameof(LegitimateStepNames))]
    public void SummaryFormat_StepName_LegitimateName_IsUnchanged(string name)
    {
        Assert.Same(name, SummaryFormat.PlainText(name));
        Assert.Equal(name, SummaryFormat.StepName(name));
    }

    [Theory]
    [MemberData(nameof(LongStepNames))]
    public void SummaryFormat_StepName_LongName_IsCutOnAWholeCharacterWithAMarker(string name, string expected)
    {
        string? cut = SummaryFormat.StepName(name);

        Assert.NotNull(cut);
        Assert.Equal(expected, cut);
        Assert.EndsWith("\u2026", cut, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(cut), 1, MaxStepNameUtf8Bytes);
        AssertPlainText(cut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SummaryFormat_StepName_NullOrNothingVisible_IsNull(bool nothingVisible)
    {
        Assert.Equal(MaxStepNameUtf8Bytes, SummaryFormat.MaxStepNameUtf8Bytes);
        Assert.Null(SummaryFormat.StepName(nothingVisible ? " \r\n\u0000\u001b\u202E\u2069\u200B\uFEFF\u2028 " : null));
    }

    public static TheoryData<string> LegitimateStepNames()
    {
        var names = new TheoryData<string>();
        foreach (StepAttributes step in JournalSamples.QuickSteps())
        {
            names.Add(step.Name);
        }

        // Exactly the limit, in 1-, 2- and 4-byte characters.
        names.Add(new string('x', MaxStepNameUtf8Bytes));
        names.Add(string.Concat(Enumerable.Repeat("\u00E9", 31)) + "x");
        names.Add(string.Concat(Enumerable.Repeat("\U0001F600", 15)) + "abc");
        return names;
    }

    public static TheoryData<string, string> LongStepNames() => new()
    {
        // 64 bytes: 60 of them leave room for the 3-byte marker.
        { new string('a', MaxStepNameUtf8Bytes + 1), new string('a', 60) + "\u2026" },

        // 2-byte characters: 30 of them fill the 60 bytes before the marker.
        { string.Concat(Enumerable.Repeat("\u00E9", 40)), string.Concat(Enumerable.Repeat("\u00E9", 30)) + "\u2026" },

        // 4-byte characters after a 1-byte one: a 15th would need bytes 58 to 61, so the cut falls before it and
        // never between the halves of its surrogate pair.
        { "a" + string.Concat(Enumerable.Repeat("\U0001F600", 16)), "a" + string.Concat(Enumerable.Repeat("\U0001F600", 14)) + "\u2026" },

        // White space before the cut is dropped, so the marker follows the last visible character.
        { new string('a', 58) + "  " + new string('b', 10), new string('a', 58) + "\u2026" },
    };

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
    [InlineData("StepCountAboveBound", false, "the step count in the begin record")]
    [InlineData("TestTypeTooLong", false, "testType in the begin record")]
    [InlineData("AppVersionTooLong", true, "appVersion in the begin record")]
    [InlineData("OsTooLong", false, "system.os in the begin record")]
    [InlineData("CpuNameTooLong", true, "system.cpuName in the begin record")]
    [InlineData("VerdictTooLong", true, "verdict in the result record")]
    [InlineData("EndDetailTooLong", true, "endDetail in the result record")]
    public async Task Detect_DamagedValue_PublishesWithoutItAndMovesJournalAsideAsync(
        string damage,
        bool completedRun,
        string damagedField)
    {
        using var root = new TestDataRoot();
        string journalPath = WriteJournal(root, DamagedJournal(damage, completedRun));
        byte[] journalBytes = File.ReadAllBytes(journalPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // The damaged value is left out and the rest is published, so the journal never blocks a later Start. It
        // is kept aside, as it was, for evidence; a damaged result leaves the run without a verdict.
        string expectedDamage = damagedField + " is out of range";
        Assert.Null(recovery.PublicationError);
        Assert.Equal(expectedDamage, recovery.JournalDamage);
        bool verdictKept = completedRun && !damage.StartsWith("Result", StringComparison.Ordinal);
        Assert.Equal(verdictKept ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(BaseNameOf(_runId), recovery.LogBaseName);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.False(recovery.JournalDeleted);
        Assert.False(File.Exists(journalPath));
        AssertMovedAsideWithEnds(root, recovery, journalBytes, _runId);

        string json = await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation);
        Assert.Equal(recovery.Summary, ParseSummary(json));
        Assert.DoesNotContain(_tooLongText, json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        AssertEveryIntegerWithinBound(document.RootElement);
        Assert.EndsWith(
            "The progress journal held damaged data: " + expectedDamage + ".",
            StringAt(document.RootElement, "run.endDetail"),
            StringComparison.Ordinal);
        if (!verdictKept)
        {
            Assert.False(recovery.InPreparation);
            Assert.Equal(2, recovery.LastStepNumber);
            Assert.Equal(StepCount, recovery.StepCount);
            Assert.Equal(RandomBankThrash, recovery.LastStepName);
            Assert.Equal("FAIL", StringAt(document.RootElement, "run.verdict"));
            Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), StringAt(document.RootElement, "run.endReason"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_HeartbeatElapsedAtLongMaxValue_PublishesAndMovesJournalAside(bool useStorage)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        JournalRecord[] journal = DamagedJournal("BeatElapsedMs", completedRun: false);

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

        // Every heartbeat is damaged, so the end time and duration are not captured, and the run is still FAIL.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal("elapsedMs in a beat record is out of range", recovery.JournalDamage);
        Assert.Null(recovery.LastHeartbeatUtc);
        Assert.Null(recovery.Summary!.Run.DurationSeconds);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        if (useStorage)
        {
            AssertEndWrittenThenMovedAside(storage, recovery, _runId);
        }
        else
        {
            Assert.False(File.Exists(root.Data.JournalPath));
            Assert.Equal(DamagedJournalPath(root), recovery.DamagedJournalPath);
            Assert.True(File.Exists(recovery.DamagedJournalPath));
        }

        // The journal no longer stands in the way: the next launch finds nothing to recover.
        Assert.Same(JournalRecovery.None, CrashDetector.Detect(root.Data, CreateRecoveryClock()));
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
    public void Detect_DamagedRunBeforeValidRun_PublishesBothAndMovesJournalAside(bool completedRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // Run H's damaged records were kept, and run G's records were appended after them.
        storage.Seed(JournalSamples.Lines(DamagedJournal("TimeBudgetOverflow", completedRun: false, runId: _otherRunId))
            .Concat(JournalSamples.Lines(completedRun ? CompletedJournal(withReleased: true) : UnfinishedJournal())));
        IReadOnlyList<string> lines = storage.ReadLines();

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // H is published without its plan, which held the damaged budget; G is published whole.
        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.Unfinished,
            recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Null(recovery.PublicationError);
        Assert.Equal("step 1's timeBudgetMs in the begin record is out of range", recovery.JournalDamage);
        Assert.Equal(FinalPathList(root, _otherRunId).Concat(FinalPathList(root, _runId)), recovery.PublishedFiles);
        Assert.Equal(recovery.Summary, ParseSummary(File.ReadAllText(FinalPaths(root, _runId).Json)));
        Assert.DoesNotContain("damaged", recovery.Summary!.Run.EndDetail, StringComparison.Ordinal);
        RunSummary other = ParseSummary(File.ReadAllText(FinalPaths(root, _otherRunId).Json));
        Assert.Empty(other.Steps);

        // Without its plan, H's targets were never captured.
        Assert.Null(other.Plan.TargetSeconds);
        Assert.Null(other.Plan.TargetCoveragePercent);
        Assert.Null(other.Coverage.TargetPercent);
        Assert.Null(other.Coverage.TargetReached);
        Assert.EndsWith(
            "The progress journal held damaged data: step 1's timeBudgetMs in the begin record is out of range.",
            other.Run.EndDetail,
            StringComparison.Ordinal);

        AssertEndWrittenThenMovedAside(storage, recovery, _otherRunId, _runId);
        Assert.Equal(lines, Assert.Single(storage.MovedAside).Lines.Take(lines.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_DamagedJournal_SummariesAlreadyPublished_KeepsThemAndMovesJournalAside(bool completedRun)
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(DamagedJournal("TimeBudgetOverflow", completedRun)));
        RunSummary published = SentinelSummary(_runId);
        (string jsonPath, string textPath) = WriteForeignSummaries(root, BaseNameOf(_runId), published);
        byte[] jsonBytes = File.ReadAllBytes(jsonPath);
        byte[] textBytes = File.ReadAllBytes(textPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // Both of the run's summaries are on disk and kept; the damaged journal is moved aside, not deleted.
        Assert.Equal(
            completedRun ? JournalRecoveryKind.CompletedUnpublished : JournalRecoveryKind.None,
            recovery.Kind);
        Assert.Equal(published, recovery.Summary);
        Assert.Null(recovery.PublicationError);
        Assert.Equal("step 1's timeBudgetMs in the begin record is out of range", recovery.JournalDamage);
        Assert.Empty(recovery.PublishedFiles);
        Assert.Equal(jsonBytes, File.ReadAllBytes(jsonPath));
        Assert.Equal(textBytes, File.ReadAllBytes(textPath));
        AssertNoRecoveredFiles(root, _runId);
        AssertEndWrittenThenMovedAside(storage, recovery, _runId);
    }

    // ---------------------------------------------------------------------------------------------
    // Untrusted journals: foreign records, repeated begins, oversized input, names and links
    // ---------------------------------------------------------------------------------------------

    // A record naming another run, or none, is never this run's: a foreign PASS cannot turn a crash into a verdict.
    [Theory]
    [InlineData("result", true)]
    [InlineData("result", false)]
    [InlineData("stepEnd", true)]
    [InlineData("released", true)]
    public async Task Detect_OutcomeRecordOfAnotherRun_IsIgnoredAsDamageAsync(string recordType, bool namesAnotherRun)
    {
        using var root = new TestDataRoot();
        Guid foreignId = namesAnotherRun ? new Guid("11111111-2222-3333-4444-555555555555") : Guid.Empty;
        JournalRecord foreign = recordType switch
        {
            "result" => JournalSamples.Result(
                RunState.Passed,
                RunEndReason.Completed,
                0,
                3 * JournalSamples.GiB,
                TimeSpan.FromSeconds(1),
                null,
                StepCount,
                _completedUtc,
                _verdictUtc) with { RunId = foreignId, Utc = _verdictUtc },
            "stepEnd" => JournalStepEnd.FromOutcome(JournalSamples.Outcome(
                _steps[1], TimeSpan.FromSeconds(50), 1, JournalSamples.GiB, 0, completed: true)) with
            {
                RunId = foreignId,
                Utc = _lastBeatUtc,
            },
            _ => new JournalReleased { RunId = foreignId, ReleasedBytes = JournalSamples.GiB, ReleaseMs = 1, Utc = _verdictUtc },
        };
        bool completedRun = recordType == "released";
        JournalRecord[] journal = completedRun ? CompletedJournal(withReleased: false) : UnfinishedJournal();
        WriteJournal(root, journal.Append(foreign).ToArray());

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal($"a {recordType} record of another run was ignored", recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        RunSummary summary = ParseSummary(await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation));
        Assert.Equal(recovery.Summary, summary);
        if (completedRun)
        {
            Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
            Assert.Null(summary.Memory.ReleaseSeconds);
            return;
        }

        // The run still stopped in step 2: FAIL with the unfinished step, never the foreign PASS or step result.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.Equal("FAIL", summary.Run.Verdict);
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), summary.Run.EndReason);
        Assert.True(summary.Run.Partial);
        Assert.False(Assert.Single(summary.Steps, step => step.Index == 2).Completed);
        Assert.Contains("did not finish at step 2 of 3 (Random bank thrash)", summary.Run.EndDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_RepeatedBegin_ContinuesItsRunAndReportsItUnfinished()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // The begin line appears a second time inside the run, as a tampered or replayed journal holds it.
        IReadOnlyList<string> lines = JournalSamples.Lines(UnfinishedJournal());
        storage.Seed(lines.Take(3).Append(lines[0]).Concat(lines.Skip(3)));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // One run, one pair of summaries, and the FAIL is shown rather than taken for an already published run.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal("a repeated begin record", recovery.JournalDamage);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.Equal(2, recovery.Summary!.Steps.Count);
        AssertNoRecoveredFiles(root, _runId);
        AssertEndWrittenThenMovedAside(storage, recovery, _runId);
    }

    // Neither the lost begin record nor step records without a count captured the plan's size, so the position names
    // the step alone, never "of 0".
    [Fact]
    public async Task Detect_PlanSizeNeverCaptured_IsLeftOutOfThePositionAsync()
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = UnfinishedJournal()
            .Skip(1)
            .Select(record => record is JournalStepStart step ? step with { Count = 0 } : record)
            .ToArray();
        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Null(recovery.StepCount);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(recovery.PublishedFiles[0], Cancellation));
        AssertNullAt(document.RootElement, "unfinishedRun.stepCount");
        Assert.StartsWith(
            "The previous test did not finish at step 2 (Random bank thrash), which likely indicates severe instability.",
            StringAt(document.RootElement, "run.endDetail"),
            StringComparison.Ordinal);
        Assert.Contains(
            "\r\nThe previous test did not finish at step 2 (Random bank thrash), which likely indicates severe instability.\r\n",
            await File.ReadAllTextAsync(recovery.PublishedFiles[1], Cancellation),
            StringComparison.Ordinal);
    }

    // A run whose id its journal never captured is named after its first line, and its rebuilt summaries carry no id;
    // a retry recognises them as its own all the same, so it publishes nothing twice and shows the FAIL again.
    [Fact]
    public void Detect_UnidentifiedRunRecoveredAgain_KeepsItsOwnSummaries()
    {
        using var root = new TestDataRoot();
        string[] journal = { "not json", "{" };
        WriteJournalLines(root, journal);
        JournalRecovery first = CrashDetector.Detect(root.Data, CreateRecoveryClock());
        Assert.Equal(2, first.PublishedFiles.Count);
        Assert.Null(first.Summary!.Run.Id);

        // The journal comes back, as after a launch that published the summaries but could not retire it.
        WriteJournalLines(root, journal);

        JournalRecovery again = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, again.Kind);
        Assert.Equal(first.LogBaseName, again.LogBaseName);
        Assert.Equal(first.Summary, again.Summary);
        Assert.Empty(again.PublishedFiles);
        Assert.Null(again.PublicationError);
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryJsonPath(root.Data, again.LogBaseName!)));
        Assert.False(File.Exists(ResultsLog.GetRecoveredSummaryTextPath(root.Data, again.LogBaseName!)));
    }

    // Only a summary recovery rebuilt may lack a run id; an empty one names no run in any summary.
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("", true, false)]
    [InlineData("   ", true, false)]
    public void TryDeserialize_RunIdMissing_AcceptedOnlyAsNullInRecoveredSummary(string? runId, bool recovered, bool accepted)
    {
        RunSummary sentinel = SentinelSummary(_runId);
        RunSummary summary = sentinel with { Run = sentinel.Run with { Id = runId, Recovered = recovered } };

        Assert.Equal(accepted, RunSummaryJson.TryDeserialize(RunSummaryJson.Serialize(summary), out RunSummary? read));
        Assert.Equal(accepted ? summary : null, read);
    }

    // Run A's begin repeats after run B's records, so A owns the end of the journal: A's FAIL is the outcome shown,
    // while B, which reached its verdict, is still published.
    [Fact]
    public async Task Detect_RepeatedBeginAfterAnotherRun_ReportsTheRunThatEndsTheJournalAsync()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        JournalRecord[] journal =
        [
            BeginRecord(_runId),
            StepStartRecord(1, TimeSpan.Zero),
            .. CompletedJournal(withReleased: true, _otherRunId),
            BeginRecord(_runId),
            StepStartRecord(2, _step1Actual),
            BeatRecord(2, 61_000, 120.5, 0, 71.5, 48.0),
        ];
        storage.Seed(JournalSamples.Lines(journal));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(_runId, recovery.RunId);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.Equal("a repeated begin record", recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId).Concat(FinalPathList(root, _otherRunId)), recovery.PublishedFiles);
        Assert.Equal(SummaryFormat.RunId(_runId), recovery.Summary!.Run.Id);
        Assert.True(recovery.Summary.Run.Partial);
        RunSummary other = ParseSummary(await File.ReadAllTextAsync(FinalPaths(root, _otherRunId).Json, Cancellation));
        Assert.Equal(SummaryFormat.RunId(_otherRunId), other.Run.Id);
        Assert.Equal(nameof(RunEndReason.FirstError), other.Run.EndReason);
        AssertEndWrittenThenMovedAside(storage, recovery, _runId, _otherRunId);
    }

    [Fact]
    public void Detect_FinalJsonIsRecoveredPartialSummary_StaysUnfinished()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal());
        JournalRecovery first = CrashDetector.Detect(root.Data, CreateRecoveryClock());
        Assert.Equal(JournalRecoveryKind.Unfinished, first.Kind);

        // The journal comes back, as after a launch that published the partial summaries but could not retire it.
        WriteJournal(root, UnfinishedJournal());

        JournalRecovery again = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // A partial summary recovery rebuilt does not show that the run finished, so its FAIL is shown again.
        Assert.Equal(JournalRecoveryKind.Unfinished, again.Kind);
        Assert.Equal(2, again.LastStepNumber);
        Assert.Equal(first.Summary, again.Summary);
        Assert.Empty(again.PublishedFiles);
        Assert.Null(again.PublicationError);
        Assert.True(again.JournalDeleted);
        AssertNoRecoveredFiles(root, _runId);
    }

    // A folder or a link at the final name is never read or followed, and a file above the summary bound is never
    // read: the link and the oversized file here hold a valid summary of this very run, which recovery would
    // otherwise keep.
    [Theory]
    [InlineData("Directory")]
    [InlineData("SymbolicLink")]
    [InlineData("Oversized")]
    public async Task Detect_FinalJsonNameHeldByFolderLinkOrOversizedFile_PublishesUnderRecoveredNameAsync(string entry)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, CompletedJournal(withReleased: true));
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        string keptSummary = RunSummaryJson.Serialize(SentinelSummary(_runId));
        switch (entry)
        {
            case "Directory":
                Directory.CreateDirectory(jsonPath);
                break;
            case "SymbolicLink":
                string target = root.Combine("elsewhere-summary.json");
                File.WriteAllText(target, keptSummary, _utf8NoBom);
                try
                {
                    _ = File.CreateSymbolicLink(jsonPath, target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Assert.Skip($"Creating a symbolic link needs SeCreateSymbolicLinkPrivilege: {exception.Message}");
                }

                break;
            default:
                // Trailing white space keeps the document valid JSON, one byte above the bound.
                File.WriteAllText(
                    jsonPath,
                    keptSummary + new string(' ', JournalReadLimits.MaxSummaryBytes + 1 - _utf8NoBom.GetByteCount(keptSummary)),
                    _utf8NoBom);
                break;
        }

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.CompletedUnpublished, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        RunSummary recovered = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(JournalSamples.AppVersion, recovered.App.Version);
        Assert.Equal(recovery.Summary, recovered);
        FileInfo kept = new(jsonPath);
        switch (entry)
        {
            case "Directory":
                Assert.True(kept.Attributes.HasFlag(FileAttributes.Directory));
                break;
            case "SymbolicLink":
                Assert.True(kept.Attributes.HasFlag(FileAttributes.ReparsePoint));
                break;
            default:
                Assert.Equal(JournalReadLimits.MaxSummaryBytes + 1L, kept.Length);
                break;
        }

        Assert.True(recovery.JournalDeleted);
    }

    // A final JSON summary of this very run that the summary reader accepts, but that lies beyond one of the summary
    // bounds, is left untouched and never rendered: the unfinished run's rebuilt summary goes to the recovered name and
    // its FAIL is shown. The first case is the one that exhausted memory: 10,000 steps, the first named by 100,000
    // characters, which the text form pads every row to.
    [Theory]
    [InlineData("TenThousandStepsWithLongName")]
    [InlineData("StringAboveBound")]
    [InlineData("EscapedStringAboveBound")]
    [InlineData("PropertyNameAboveBound")]
    [InlineData("ArrayAboveBound")]
    [InlineData("NestedBeyondDepth")]
    [InlineData("MoreStepsThanAPlan")]
    [InlineData("StepNameAboveThePlanLimit")]
    [InlineData("StepCellAboveBound")]
    public async Task Detect_FinalJsonBeyondSummaryBounds_LeftUntouched_RecoveredSummaryPublishedAsync(string shape)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal());
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        string kept = KeptSummaryOfShape(shape);
        Assert.True(RunSummaryJson.TryDeserialize(kept, out RunSummary? readable), "Only the summary bounds may refuse it.");
        Assert.Equal(SummaryFormat.RunId(_runId), readable.Run.Id);
        File.WriteAllText(jsonPath, kept, _utf8NoBom);
        byte[] keptBytes = File.ReadAllBytes(jsonPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Null(recovery.PublicationError);
        Assert.Null(recovery.JournalDamage);
        string recoveredJsonPath = ResultsLog.GetRecoveredSummaryJsonPath(root.Data, BaseNameOf(_runId));
        Assert.Equal(new[] { recoveredJsonPath, textPath }, recovery.PublishedFiles);
        Assert.Equal(keptBytes, File.ReadAllBytes(jsonPath));
        RunSummary rebuilt = ParseSummary(await File.ReadAllTextAsync(recoveredJsonPath, Cancellation));
        Assert.Equal(recovery.Summary, rebuilt);
        Assert.True(rebuilt.Run.Partial);

        // The text is the rebuilt summary's, never a rendering of the kept one.
        Assert.Equal(TextSummaryFormatter.Format(rebuilt), await File.ReadAllTextAsync(textPath, Cancellation));
        Assert.True(recovery.JournalDeleted);
    }

    // A summary at every bound at once is still this run's: it is kept, and the unfinished run is reported as already
    // published. Its end detail is written 98,304 bytes long, every character escaped, yet holds the 16,384 the bound
    // allows.
    [Fact]
    public async Task Detect_FinalJsonAtTheSummaryBounds_IsKeptAndRenderedAsync()
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal());
        (string jsonPath, string textPath) = FinalPaths(root, _runId);
        string kept = KeptSummaryOfShape("AtTheBounds");
        File.WriteAllText(jsonPath, kept, _utf8NoBom);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(new[] { textPath }, recovery.PublishedFiles);
        Assert.Equal(ParseSummary(kept), recovery.Summary);
        Assert.Equal(JournalReadLimits.MaxPlanSteps, recovery.Summary!.Steps.Count);
        Assert.Equal(JournalReadLimits.MaxSummaryStringLength, recovery.Summary.Run.EndDetail.Length);
        Assert.Equal(TextSummaryFormatter.Format(recovery.Summary), await File.ReadAllTextAsync(textPath, Cancellation));
        AssertNoRecoveredFiles(root, _runId);
        Assert.True(recovery.JournalDeleted);
    }

    // Input beyond the read bounds is left unread, never allocated: what was read is published, and the journal is
    // moved aside.
    [Theory]
    [InlineData("LongStepName", "a line longer than 1 MiB was left unread")]
    [InlineData("LargerThanBound", "the journal is larger than 16 MiB, so the rest of it was left unread")]
    public async Task Detect_JournalBeyondReadBounds_PublishesWhatWasReadAndMovesItAsideAsync(string journalCase, string expectedDamage)
    {
        using var root = new TestDataRoot();
        JournalRecord[] journal = UnfinishedJournal();
        string? tail = null;
        if (journalCase == "LongStepName")
        {
            // Step 2's record names the step with 2 MiB of text, so the run is read without it.
            int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
            journal[stepIndex] = (JournalStepStart)journal[stepIndex] with { Name = new string('x', 2 * 1024 * 1024) };
        }
        else
        {
            // Heartbeats beyond the 16 MiB bound; reading stops there, mid-line.
            string beat = JournalLineSerializer.Serialize(BeatRecord(2, 61_000, 120.5, 0, 71.5, 48.0)) + "\n";
            tail = string.Concat(Enumerable.Repeat(beat, (JournalReadLimits.MaxJournalBytes / beat.Length) + 100));
        }

        string journalPath = WriteJournal(root, journal);
        if (tail is not null)
        {
            File.AppendAllText(journalPath, tail, _utf8NoBom);
        }

        byte[] journalBytes = File.ReadAllBytes(journalPath);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(expectedDamage, recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        Assert.Equal(FinalPathList(root, _runId), recovery.PublishedFiles);
        Assert.False(recovery.InPreparation);
        RunSummary summary = ParseSummary(await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation));
        Assert.EndsWith("The progress journal held damaged data: " + expectedDamage + ".", summary.Run.EndDetail, StringComparison.Ordinal);
        if (journalCase == "LongStepName")
        {
            // Step 2's heartbeats outlive its record, so the stop is unknown rather than placed in finished step 1.
            Assert.Null(recovery.LastStepNumber);
            Assert.Contains("did not finish at an unknown step", summary.Run.EndDetail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(2, recovery.LastStepNumber);
        }

        Assert.False(File.Exists(journalPath));
        AssertMovedAsideWithEnds(root, recovery, journalBytes, _runId);
    }

    [Fact]
    public void Detect_JournalLeftUnreadWithoutAnyLine_ReportsUnfinishedRunOfUnknownStep()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();

        // A journal whose only content is a line beyond the bounds still held a run, so it is reported.
        storage.Seed(Array.Empty<string>());
        storage.ReadDamage = "a line longer than 1 MiB was left unread";

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.False(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Equal(storage.ReadDamage, recovery.JournalDamage);
        Assert.Equal(2, recovery.PublishedFiles.Count);
        Assert.Contains("did not finish at an unknown step", recovery.Summary!.Run.EndDetail, StringComparison.Ordinal);
        AssertEndWrittenThenMovedAside(storage, recovery, Guid.Empty);
    }

    // The plan's step names are at most 63 UTF-8 bytes; a longer one is a value no run writes, so it is damage, and
    // the text summary would pad every step row to it, so it is never published whole.
    [Theory]
    [InlineData("Plan", "step 2's name in the begin record is out of range")]
    [InlineData("StepRecord", "step 2's name in a step record is out of range")]
    public void Detect_StepNameAboveThePlanLimit_IsDamageAndNeverPublishedWhole(string where, string expectedDamage)
    {
        using var root = new TestDataRoot();
        string longName = new('n', 64);
        JournalRecord[] journal = UnfinishedJournal();
        int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
        if (where == "Plan")
        {
            journal[0] = WithStep((JournalBegin)journal[0], 1, step => step with { Name = longName });
        }
        else
        {
            journal[stepIndex] = (JournalStepStart)journal[stepIndex] with { Name = longName };
        }

        WriteJournal(root, journal);

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal(expectedDamage, recovery.JournalDamage);
        Assert.Equal(2, recovery.LastStepNumber);
        Assert.Equal(StepCount, recovery.StepCount);

        // The step record names the step; without the record's name the plan does. The plan stands, and its long
        // name is cut to the limit with a marker.
        Assert.Equal(RandomBankThrash, recovery.LastStepName);
        Assert.DoesNotContain(longName, RunSummaryJson.Serialize(recovery.Summary!), StringComparison.Ordinal);
        Assert.Equal(2, recovery.Summary!.Steps.Count);
        Assert.Equal(
            where == "Plan" ? new string('n', 60) + "\u2026" : RandomBankThrash,
            recovery.Summary.Steps[1].Name);
    }

    [Fact]
    public void Detect_LoneSurrogateEscapes_NeverThrowAndLeaveTheValuesOut()
    {
        using var root = new TestDataRoot();
        IReadOnlyList<string> lines = JournalSamples.Lines(UnfinishedJournal());
        JsonObject begin = JsonNode.Parse(lines[0])!.AsObject();
        begin["logBaseName"] = "PLACEHOLDER";
        string damagedBegin = begin.ToJsonString().Replace("PLACEHOLDER", "\\ud800x", StringComparison.Ordinal);
        string damagedStep = lines[5].Replace(RandomBankThrash, "\\ud800", StringComparison.Ordinal);
        Assert.False(JournalLineSerializer.TryParse(damagedBegin, out _));
        Assert.False(JournalLineSerializer.TryParse(damagedStep, out _));
        Assert.False(RunSummaryJson.TryDeserialize("{\"schemaVersion\":1,\"run\":{\"id\":\"\\ud800\"}}", out _));
        WriteJournalLines(root, lines.Skip(1).Take(4).Prepend(damagedBegin).Append(damagedStep).Concat(lines.Skip(6)));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        // The base name is left out of the begin record, so the run is named after its id; the step 2 record is
        // skipped like any malformed line, so step 2's heartbeats leave the stop unknown.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal("the begin record was damaged: logBaseName could not be read", recovery.JournalDamage);
        Assert.Equal(FallbackBaseNamePrefix + SummaryFormat.RunId(_runId), recovery.LogBaseName);
        Assert.Null(recovery.LastStepNumber);
        Assert.False(recovery.InPreparation);
        Assert.Equal(2, recovery.PublishedFiles.Count);
    }

    [Fact]
    public void Detect_MoveAsideFails_KeepsJournalInPlaceAndNextLaunchOnlyRetriesTheMove()
    {
        using var root = new TestDataRoot();
        IReadOnlyList<string> lines = JournalSamples.Lines(DamagedJournal("BeatErrors", completedRun: false));
        var storage = new FaultingJournalStorage(lines, moveAsideFailure: new IOException("Simulated move failure"));

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // The damaged journal is evidence, so a journal that cannot be moved aside stays where it is rather than being
        // deleted. Its results are published and its run has its end record, so it refuses no Start.
        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal("errors in a beat record is out of range", recovery.JournalDamage);
        Assert.Null(recovery.PublicationError);
        Assert.Null(recovery.DamagedJournalPath);
        Assert.Equal(1, storage.MoveAsideCalls);
        Assert.Equal(0, storage.DeleteCalls);
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(_runId, ParseEnd(Assert.Single(storage.Appended)).RunId);
        Assert.Equal(2, recovery.PublishedFiles.Count);

        // The next launch finds the same journal with the end record after it: nothing is published again, the run is
        // not reported again, and the move is retried.
        var kept = new FaultingJournalStorage(lines.Concat(storage.Appended));
        JournalRecovery retry = CrashDetector.Detect(root.Data, kept, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, retry.Kind);
        Assert.Equal(_runId, retry.RunId);
        Assert.Empty(retry.PublishedFiles);
        Assert.Null(retry.PublicationError);
        Assert.Equal(recovery.JournalDamage, retry.JournalDamage);
        Assert.Empty(kept.Appended);
        Assert.Equal(1, kept.MoveAsideCalls);
        Assert.Equal(0, kept.DeleteCalls);
        Assert.Equal("memory://faulting/active-run.damaged-20261028-150500.jsonl", retry.DamagedJournalPath);
    }

    [Fact]
    public void Detect_PublicationFailsForDamagedJournal_KeepsJournalWithoutEnd()
    {
        using var root = new TestDataRoot();
        using var storage = new StalledJournalStorage();
        storage.Seed(JournalSamples.Lines(DamagedJournal("BeatErrors", completedRun: false)));
        IReadOnlyList<string> lines = storage.ReadLines();
        _ = root.CreateBlockingFile("logs");

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        // A transient save failure keeps the journal for the next launch to retry, damaged or not.
        Assert.False(string.IsNullOrWhiteSpace(recovery.PublicationError));
        Assert.Equal("errors in a beat record is out of range", recovery.JournalDamage);
        Assert.Null(recovery.DamagedJournalPath);
        Assert.False(recovery.JournalDeleted);
        Assert.Equal(0, storage.AppendCallCount);
        Assert.Empty(storage.MovedAside);
        Assert.Equal(lines, storage.ReadLines());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"t\":\"step\",\"index\":\"two\"}")]
    [InlineData("{\"t\":\"begin\",\"runId\":\"a\",\"runId\":")]
    [InlineData("not json")]
    public void TrySalvageBegin_LineThatIsNotABeginObject_ReturnsFalse(string line)
    {
        Assert.False(JournalLineSerializer.TrySalvageBegin(line, out JournalBegin? begin, out IReadOnlyList<string> dropped));
        Assert.Null(begin);
        Assert.Empty(dropped);
    }

    [Fact]
    public void TrySalvageBegin_KeepsEveryFieldThatReadsAndNamesTheRest()
    {
        JournalBegin written = BeginRecord(_runId);
        JsonObject line = JsonNode.Parse(JournalLineSerializer.Serialize(written))!.AsObject();
        line["stopOnError"] = "yes";
        line["memory"] = 42;
        line["plannedBytes"] = -1;

        Assert.True(JournalLineSerializer.TrySalvageBegin(line.ToJsonString(), out JournalBegin? begin, out IReadOnlyList<string> dropped));

        // The names come in line order, which replacing a value leaves as the record writes it.
        string[] damagedFields = { "stopOnError", "memory", "plannedBytes" };
        Assert.Equal(line.Select(property => property.Key).Where(damagedFields.Contains), dropped);
        Assert.Equal(written with { StopOnError = null, Memory = new JournalMemoryInfo(), PlannedBytes = null }, begin);
    }

    // A name that is not valid UTF-16 cannot be shown, so the notice names the field by its kind only.
    [Fact]
    public void TrySalvageBegin_FieldWithUnreadableName_IsNamedByItsKind()
    {
        string line = "{\"t\":\"begin\",\"runId\":\"" + _runId.ToString("D", CultureInfo.InvariantCulture)
            + "\",\"stopOnError\":\"yes\",\"\\ud800\":1}";
        Assert.False(JournalLineSerializer.TryParse(line, out _));

        Assert.True(JournalLineSerializer.TrySalvageBegin(line, out JournalBegin? begin, out IReadOnlyList<string> dropped));

        Assert.Equal(new[] { "stopOnError", "a field with an unreadable name" }, dropped);
        Assert.Equal(_runId, begin.RunId);
    }

    // The step records are what place a run: without them, or without the record of the step its last heartbeats
    // name, the run's beats and stepEnds show it ran, but not where it stopped.
    [Theory]
    [InlineData("EveryStepRecord")]
    [InlineData("Step2Record")]
    public async Task Detect_StepRecordsMissing_ReportsUnfinishedRunAtUnknownStepAsync(string missing)
    {
        using var root = new TestDataRoot();
        WriteJournal(root, UnfinishedJournal()
            .Where(record => record is not JournalStepStart step || (missing == "Step2Record" && step.Index == 1))
            .ToArray());

        JournalRecovery recovery = CrashDetector.Detect(root.Data, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.Unfinished, recovery.Kind);
        Assert.Equal("the run's step records are missing or could not be read", recovery.JournalDamage);
        Assert.Equal(_runId, recovery.RunId);
        Assert.False(recovery.InPreparation);
        Assert.Null(recovery.LastStepNumber);
        Assert.Null(recovery.LastStepName);
        RunSummary summary = ParseSummary(await File.ReadAllTextAsync(FinalPaths(root, _runId).Json, Cancellation));
        Assert.Contains("did not finish at an unknown step", summary.Run.EndDetail, StringComparison.Ordinal);

        // The summary agrees with the outcome: step 1's stepEnd is kept as a finished step, not taken as the stop.
        Assert.Null(summary.UnfinishedRun!.LastStepIndex);
        Assert.Null(summary.UnfinishedRun.LastStepName);
        Assert.True(Assert.Single(summary.Steps).Completed);
        Assert.Equal(DamagedJournalPath(root), recovery.DamagedJournalPath);
    }

    // A journal kept because it could neither be moved nor deleted holds the end recovery appended without an id; the
    // next launch takes it as the unidentified run's end, publishes nothing more, and retires the journal again.
    [Fact]
    public void Detect_UnidentifiedRunWithItsEnd_PublishesNothingAndRetiresJournal()
    {
        using var root = new TestDataRoot();
        string end = JournalLineSerializer.Serialize(new JournalEnd { Published = true, Utc = _recoveryUtc });
        var storage = new FaultingJournalStorage(new[] { "not json", "{", end });

        JournalRecovery recovery = CrashDetector.Detect(root.Data, storage, CreateRecoveryClock());

        Assert.Equal(JournalRecoveryKind.None, recovery.Kind);
        Assert.Null(recovery.RunId);
        Assert.Empty(recovery.PublishedFiles);
        Assert.Null(recovery.PublicationError);
        Assert.Equal("the journal's begin record is missing or could not be read", recovery.JournalDamage);
        Assert.Empty(storage.Appended);
        Assert.Equal(1, storage.MoveAsideCalls);
        Assert.Equal(0, storage.DeleteCalls);
        Assert.Equal("memory://faulting/active-run.damaged-20261028-150500.jsonl", recovery.DamagedJournalPath);
        AssertLogsEmpty(root);
    }

    [Fact]
    public void JournalRecovery_EqualsAndHashCode_IncludeTheDamageAndTheKeptJournal()
    {
        var recovery = new JournalRecovery
        {
            Kind = JournalRecoveryKind.Unfinished,
            RunId = _runId,
            JournalDamage = "a repeated begin record",
            DamagedJournalPath = "memory://journal/active-run.damaged-20261028-150500.jsonl",
        };
        JournalRecovery same = recovery with { };

        Assert.Equal(recovery, same);
        Assert.Equal(recovery.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(recovery, recovery with { JournalDamage = null });
        Assert.NotEqual(recovery, recovery with { DamagedJournalPath = null });
        Assert.NotEqual(recovery.GetHashCode(), (recovery with { JournalDamage = "a result record of another run was ignored" }).GetHashCode());
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

    // Recovery appended one end record per run and then moved the seeded journal aside, never deleting it.
    private static void AssertEndWrittenThenMovedAside(StalledJournalStorage storage, JournalRecovery recovery, params Guid[] runIds)
    {
        Assert.Equal(runIds.Select(_ => (string?)EndType), storage.RecordTypesWritten());
        Assert.Equal(runIds, storage.Lines.Select(line => ParseEnd(line).RunId));
        Assert.All(storage.Calls, call => Assert.Equal(AppendOutcome.Succeeded, call.Outcome));
        MovedAsideJournal moved = Assert.Single(storage.MovedAside);
        Assert.Equal("memory://journal/active-run.damaged-20261028-150500.jsonl", moved.Location);
        Assert.Equal(moved.Location, recovery.DamagedJournalPath);
        Assert.Equal(storage.Lines, moved.Lines.TakeLast(runIds.Length));
        Assert.False(storage.Exists());
        Assert.Equal(0, storage.DeleteCount);
        Assert.False(recovery.JournalDeleted);
    }

    // The file journal was moved aside as it was, followed by the end record recovery appended for each run.
    private static void AssertMovedAsideWithEnds(TestDataRoot root, JournalRecovery recovery, byte[] journalBytes, params Guid[] runIds)
    {
        Assert.Equal(DamagedJournalPath(root), recovery.DamagedJournalPath);
        byte[] moved = File.ReadAllBytes(DamagedJournalPath(root));
        Assert.Equal(journalBytes, moved.Take(journalBytes.Length));
        string[] ends = _utf8NoBom.GetString(moved, journalBytes.Length, moved.Length - journalBytes.Length)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(runIds, ends.Select(line => ParseEnd(line).RunId));
    }

    private static string DamagedJournalPath(TestDataRoot root) =>
        Path.GetFullPath(Path.Combine(root.Data.JournalDirectory, "active-run.damaged-20261028-150500.jsonl"));

    // The summary schema keeps every integer below 2^53, so no number a damaged journal held may be published.
    private static void AssertEveryIntegerWithinBound(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    AssertEveryIntegerWithinBound(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertEveryIntegerWithinBound(item);
                }

                break;
            case JsonValueKind.Number:
                Assert.InRange(Math.Abs(element.GetDouble()), 0.0, MaxQuantity);
                break;
        }
    }

    // The reference for the name of a run whose id is unknown: the first 32 lowercase hex digits of SHA-256 over the
    // UTF-8 of its first line.
    private static string AnonymousName(string firstLine) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(firstLine)))[..32]
            .ToLowerInvariant();

    // Plain text holds no control, format, line separator or paragraph separator character, and no surrogate
    // without its partner, which a strict UTF-8 round trip would refuse.
    private static void AssertPlainText(string text)
    {
        Assert.Equal(text, _strictUtf8.GetString(_strictUtf8.GetBytes(text)));
        foreach (Rune scalar in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(scalar);
            Assert.False(
                category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator,
                string.Create(CultureInfo.InvariantCulture, $"U+{scalar.Value:X4} ({category}) is not plain text."));
        }
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

    // Stamped with the run id, as ProgressJournal stamps every stepEnd, result and released record.
    private static JournalStepEnd FirstStepEndRecord(Guid? runId = null)
    {
        StepOutcome outcome = JournalSamples.Outcome(
            _steps[0],
            _step1Actual,
            1,
            JournalSamples.GiB,
            0,
            completed: true,
            overrun: _step1Overrun);
        return JournalStepEnd.FromOutcome(outcome) with { RunId = runId ?? _runId, Utc = _runningUtc + _step1Actual };
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
        Guid id = runId ?? _runId;
        var records = new List<JournalRecord> { BeginRecord(id, baseName) };
        if (includePrepared)
        {
            records.Add(PreparedRecord());
        }

        records.Add(StepStartRecord(1, TimeSpan.Zero));
        records.Add(BeatRecord(1, 20_000, 50.0, 0, 65.5, 49.5));
        records.Add(FirstStepEndRecord(id));
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
        Guid id = runId ?? _runId;
        var records = new List<JournalRecord>
        {
            BeginRecord(id),
            PreparedRecord(),
            StepStartRecord(1, TimeSpan.Zero),
            BeatRecord(1, 20_000, 50.0, 0, 65.5, 49.5),
            FirstStepEndRecord(id),
            StepStartRecord(2, _step1Actual),
            BeatRecord(2, 61_000, 120.5, 0, 71.5, 48.0),
            JournalStepEnd.FromOutcome(secondStep) with { RunId = id, Utc = _completedUtc },
            result with { RunId = id, Utc = _verdictUtc },
        };
        if (withReleased)
        {
            records.Add(new JournalReleased
            {
                RunId = id,
                ReleasedBytes = JournalSamples.GiB,
                ReleaseMs = ReleaseMs,
                Utc = _completedUtc + TimeSpan.FromMilliseconds(1_300),
            });
        }

        return records.ToArray();
    }

    // A journal that holds nothing a run writes, as its terminated lines and a final fragment without a terminator:
    // empty, a begin torn by a crash during its own append, alone or after a blank line, or blank lines.
    private static (IReadOnlyList<string> Lines, string? Torn) JournalWithoutUsableBegin(string journalCase)
    {
        string begin = JournalLineSerializer.Serialize(BeginRecord(_runId));
        string tornBegin = begin[..(begin.Length / 2)];
        return journalCase switch
        {
            "Empty" => (Array.Empty<string>(), null),
            "TornBegin" => (Array.Empty<string>(), tornBegin),
            "TornBeginAfterBlankLine" => (new[] { string.Empty }, tornBegin),
            "BlankLines" => (new[] { string.Empty, "   " }, null),
            _ => throw new ArgumentOutOfRangeException(nameof(journalCase), journalCase, "Unknown journal case."),
        };
    }

    // The standard unfinished journal whose begin record is missing, unreadable or damaged in one way.
    private static IReadOnlyList<string> JournalWithDamagedBegin(string journalCase)
    {
        IReadOnlyList<string> lines = JournalSamples.Lines(UnfinishedJournal());
        IEnumerable<string> body = lines.Skip(1);
        JsonObject begin = JsonNode.Parse(lines[0])!.AsObject();
        switch (journalCase)
        {
            case "NoBegin":
                return body.ToArray();
            case "NotJson":
                return new[] { "not json", "{" };
            case "LoneLineNotJson":
                return new[] { "not json" };
            case "LoneLineRunIdOnly":
                // An object holding only the run id, with no "t" discriminator: no record at all.
                return new[] { new JsonObject { ["runId"] = _runId.ToString("D", CultureInfo.InvariantCulture) }.ToJsonString() };
            case "BadRunIdFormat":
                begin["runId"] = "../../x";
                break;
            case "RunIdNFormatOnly":
                begin = new JsonObject { ["t"] = "begin", ["runId"] = _runId.ToString("N", CultureInfo.InvariantCulture) };
                break;
            case "BadStartRequestedUtc":
                begin["startRequestedUtc"] = "99999-99-99T99:99:99Z";
                break;
            case "BadStopOnError":
                begin["stopOnError"] = "yes";
                break;
            case "DiscriminatorNotFirst":
                // .NET 8 System.Text.Json reads a polymorphic record only when its discriminator comes first.
                JsonNode? discriminator = begin["t"];
                _ = begin.Remove("t");
                begin["t"] = discriminator?.DeepClone();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(journalCase), journalCase, "Unknown journal case.");
        }

        string damaged = begin.ToJsonString();
        Assert.False(JournalLineSerializer.TryParse(damaged, out _), $"The damaged begin '{damaged}' parses.");
        return body.Prepend(damaged).ToArray();
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

    // A JSON summary of this run, as text, at or one beyond the summary bounds in the named way.
    private static string KeptSummaryOfShape(string shape)
    {
        const int Bound = JournalReadLimits.MaxSummaryStringLength;
        if (shape == "TenThousandStepsWithLongName")
        {
            var steps = new JsonArray();
            for (int index = 1; index <= 10_000; index++)
            {
                steps.Add(new JsonObject { ["index"] = index, ["name"] = index == 1 ? new string('N', 100_000) : "short" });
            }

            var minimal = new JsonObject
            {
                ["schemaVersion"] = RunSummary.CurrentSchemaVersion,
                ["run"] = new JsonObject { ["id"] = SummaryFormat.RunId(_runId), ["verdict"] = "PASS", ["endReason"] = "Completed" },
                ["steps"] = steps,
            };
            return minimal.ToJsonString();
        }

        JsonObject document = JsonNode.Parse(RunSummaryJson.Serialize(SentinelSummary(_runId)))!.AsObject();
        JsonObject firstStep = document["steps"]![0]!.AsObject();
        switch (shape)
        {
            case "StringAboveBound":
                document["run"]!["endDetail"] = new string('d', Bound + 1);
                break;
            case "EscapedStringAboveBound":
                // The writer escapes every non-ASCII character, so each one is written as six bytes.
                document["run"]!["endDetail"] = new string('\u00E9', Bound + 1);
                break;
            case "PropertyNameAboveBound":
                document[new string('k', Bound + 1)] = 1;
                break;
            case "ArrayAboveBound":
                document["errors"]!["records"] = Objects(JournalReadLimits.MaxSummaryArrayLength + 1);
                break;
            case "NestedBeyondDepth":
                document["extra"] = NestedArrays(JournalReadLimits.MaxSummaryDepth);
                break;
            case "MoreStepsThanAPlan":
                document["steps"] = Copies(firstStep, JournalReadLimits.MaxPlanSteps + 1);
                break;
            case "StepNameAboveThePlanLimit":
                firstStep["name"] = new string('n', 64);
                break;
            case "StepCellAboveBound":
                firstStep["pattern"] = new string('p', 65);
                break;
            case "AtTheBounds":
                document["run"]!["endDetail"] = new string('\u00E9', Bound);
                document[new string('k', Bound)] = 1;
                document["errors"]!["records"] = Objects(JournalReadLimits.MaxSummaryArrayLength);
                document["extra"] = NestedArrays(JournalReadLimits.MaxSummaryDepth - 1);
                firstStep["name"] = new string('n', 63);
                firstStep["pattern"] = new string('p', 64);
                document["steps"] = Copies(firstStep, JournalReadLimits.MaxPlanSteps);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown summary shape.");
        }

        return document.ToJsonString();

        static JsonArray Objects(int count)
        {
            var array = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                array.Add(new JsonObject());
            }

            return array;
        }

        static JsonArray Copies(JsonObject item, int count)
        {
            var array = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                array.Add(item.DeepClone());
            }

            return array;
        }

        // Arrays nested this many deep, inside the document's own object, which is one level more.
        static JsonArray NestedArrays(int depth)
        {
            var nested = new JsonArray();
            for (int level = 1; level < depth; level++)
            {
                nested = new JsonArray(nested);
            }

            return nested;
        }
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
            journal = journal.Append(secondStepEnd with { RunId = runId ?? _runId, Utc = _lastBeatUtc }).ToArray();
        }

        return journal.Select(record => (damage, record) switch
        {
            // The finding's case: two budgets whose sum overflows a long.
            ("TimeBudgetOverflow", JournalBegin begin) => WithStep(
                WithStep(begin, 0, step => step with { TimeBudgetMs = long.MaxValue }),
                1,
                step => step with { TimeBudgetMs = 1 }),
            ("TimeBudgetTotal", JournalBegin begin) => WithStep(begin, 0, step => step with { TimeBudgetMs = MaxQuantity }),
            ("StepCountAboveBound", JournalBegin begin) => begin with
            {
                Steps = Enumerable.Range(0, JournalReadLimits.MaxPlanSteps + 1).Select(_ => begin.Steps[0]).ToArray(),
            },
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
            ("TestTypeTooLong", JournalBegin begin) => begin with { TestType = _tooLongText },
            ("AppVersionTooLong", JournalBegin begin) => begin with { AppVersion = _tooLongText },
            ("OsTooLong", JournalBegin begin) => begin with { System = begin.System with { Os = _tooLongText } },
            ("CpuNameTooLong", JournalBegin begin) => begin with { System = begin.System with { CpuName = _tooLongText } },
            ("VerdictTooLong", JournalResult result) => result with { Verdict = _tooLongText },
            ("EndDetailTooLong", JournalResult result) => result with { EndDetail = _tooLongText },
            _ => record,
        }).ToArray();
    }

    // A step name a damaged or forged journal can hold, and the one line the window and the summaries show for it.
    private static (string Planted, string Shown) UntrustedStepName(string nameCase) => nameCase switch
    {
        // 68 bytes once each control character is a space, so the name is cut after the script tag.
        "LineInjection" => (
            "Evil\r\nINJECTED LINE \u001b[31mRED\u001b[0m <script>alert(1)</script> \u0000nul \u202Ertl",
            "Evil  INJECTED LINE  [31mRED [0m <script>alert(1)</script>\u2026"),
        "SpoofedPass" => (
            "Chain\r\nSPOOFED: PASS \u001b[32m<b>ok</b>",
            "Chain  SPOOFED: PASS  [32m<b>ok</b>"),

        // Isolates, a zero-width space and a tag character (a format character beyond U+FFFF) around the name.
        "InvisibleCharacters" => ("\u2066Random\u200Bbank thrash\U000E0041\u2069", RandomBankThrash),
        "TenThousandCharacters" => (
            string.Concat(Enumerable.Repeat(RandomBankThrash + " ", 527))[..10_000],
            "Random bank thrash Random bank thrash Random bank thrash Ran\u2026"),
        _ => throw new ArgumentOutOfRangeException(nameof(nameCase), nameCase, "Unknown step name case."),
    };

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
    /// A journal whose read, delete or move aside fails, for the I/O failure paths
    /// <see cref="StalledJournalStorage"/> cannot script. Appends always succeed and are recorded.
    /// </summary>
    private sealed class FaultingJournalStorage : IJournalStorage
    {
        private readonly List<string> _lines;
        private readonly List<string> _appended = new();
        private readonly Exception? _readFailure;
        private readonly Exception? _deleteFailure;
        private readonly Exception? _moveAsideFailure;

        public FaultingJournalStorage(
            IEnumerable<string> lines,
            Exception? readFailure = null,
            Exception? deleteFailure = null,
            Exception? moveAsideFailure = null)
        {
            _lines = new List<string>(lines);
            _readFailure = readFailure;
            _deleteFailure = deleteFailure;
            _moveAsideFailure = moveAsideFailure;
        }

        public string Location => "memory://faulting/active-run.jsonl";

        public IReadOnlyList<string> Appended => _appended.ToArray();

        public int DeleteCalls { get; private set; }

        public int MoveAsideCalls { get; private set; }

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

        public string MoveAside(DateTimeOffset utc)
        {
            MoveAsideCalls++;
            if (_moveAsideFailure is not null)
            {
                throw _moveAsideFailure;
            }

            _lines.Clear();
            return "memory://faulting/active-run.damaged-"
                + utc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";
        }

        public void Dispose() => IsDisposed = true;
    }
}
