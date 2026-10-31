using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using RamQuick.Core;
using Xunit;

namespace RamQuick.Logging.Tests;

/// <summary>
/// Tests for <see cref="ResultsLog"/> and the summaries it publishes: base-name reservation and its retry with a
/// new run id, the timestamped event log and its <c>PROGRESS</c> contract with the validation script,
/// crash-consistent publication with overwriting disabled, the JSON value types of schema version 1, the hint
/// label on the text and JSON channels (the UI channel belongs to MainViewModelTests), and app-errors.log.
/// </summary>
/// <remarks>
/// Every clock is a <see cref="FakeTimeProvider"/> standing at <see cref="JournalSamples.StartUtc"/> with UTC as
/// its local time zone, unless a test sets another zone, so expected names and stamps are the same on every
/// host. Each test owns a <see cref="TestDataRoot"/>, disposes every log it opens, and reads a <c>.log</c> only
/// after <see cref="ResultsLog.FlushAsync"/>, the log's documented drain point.
/// </remarks>
public sealed partial class ResultsLogTests
{
    private const string Quick = JournalSamples.QuickTestType;
    private const string LocalStartFormat = "yyyyMMdd-HHmmss";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string LineEnd = "\r\n";
    private const string HintLabel = "Hint \u2014 not a diagnosis";

    // The largest integer the schema bounds: the bytes a 192 GiB Exhaustive run verifies at 2,500 %.
    private const ulong ExhaustiveVerifiedBytes = 25 * TestedBytes192GiB;
    private const ulong TestedBytes192GiB = 192 * JournalSamples.GiB;
    private const ulong InstalledBytes256GiB = 256 * JournalSamples.GiB;

    private static readonly DateTimeOffset _t0 = JournalSamples.StartUtc;
    private static readonly TimeSpan _ceiling = TimeSpan.FromSeconds(10);
    private static readonly byte[] _utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] _sentinel = Encoding.ASCII.GetBytes("sentinel: a file an earlier run left here");

    private static readonly Guid _g1 = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid _g2 = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid _g3 = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid _g4 = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid _g5 = new("55555555-5555-4555-8555-555555555555");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static CultureInfo Invariant => CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------------------------------------
    // Naming and reservation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OpenRun_CreatesLogWithBaseName()
    {
        using var root = new TestDataRoot();
        var runIds = new ScriptedRunIds(JournalSamples.RunIdA);
        using ResultsLog log = Open(root, CreateTime(), runIds);

        string expected = "RamQuick-" + _t0.ToString(LocalStartFormat, Invariant) + "-Quick-"
            + JournalSamples.RunIdA.ToString("N", Invariant);
        string logs = root.Data.LogsDirectory;

        Assert.Equal(expected, log.BaseName);
        Assert.Equal(JournalSamples.BaseName(JournalSamples.RunIdA, _t0), log.BaseName);
        Assert.Matches(QuickBaseNamePattern(), log.BaseName);
        Assert.Equal(JournalSamples.RunIdA, log.RunId);
        Assert.Equal(1, runIds.Calls);
        Assert.Same(root.Data, log.DataDirectory);

        Assert.Equal(Path.Combine(logs, expected + ".log"), log.LogPath);
        Assert.Equal(ResultsLog.GetLogPath(root.Data, expected), log.LogPath);
        Assert.True(File.Exists(log.LogPath));
        Assert.Equal(Path.Combine(logs, expected + "-summary.json"), log.SummaryJsonPath);
        Assert.Equal(ResultsLog.GetSummaryJsonPath(root.Data, log.BaseName), log.SummaryJsonPath);
        Assert.Equal(Path.Combine(logs, expected + "-summary.txt"), log.SummaryTextPath);
        Assert.Equal(ResultsLog.GetSummaryTextPath(root.Data, log.BaseName), log.SummaryTextPath);
        Assert.Equal(
            Path.Combine(logs, expected + "-recovered-summary.json"),
            ResultsLog.GetRecoveredSummaryJsonPath(root.Data, log.BaseName));
        Assert.Equal(
            Path.Combine(logs, expected + "-recovered-summary.txt"),
            ResultsLog.GetRecoveredSummaryTextPath(root.Data, log.BaseName));

        // The name is reserved, not published: neither summary exists yet.
        Assert.False(File.Exists(log.SummaryJsonPath));
        Assert.False(File.Exists(log.SummaryTextPath));
        Assert.Null(log.WriteFailure);
    }

    [Fact]
    public void OpenRun_NonUtcLocalZone_NamesFilesByLocalStartTime()
    {
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();
        TimeSpan offset = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("RamQuick.Tests+0930", offset, "Test +09:30", "Test +09:30"));

        using ResultsLog log = Open(root, time, new ScriptedRunIds(JournalSamples.RunIdA));

        // 14:05:51 UTC is 23:35:51 at +09:30: the stamp is the local wall-clock time, not UTC.
        Assert.Equal(JournalSamples.BaseName(JournalSamples.RunIdA, _t0.ToOffset(offset)), log.BaseName);
        Assert.Contains("-20261028-233551-", log.BaseName, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRun_TwoRunsSameSecond_DistinctNames()
    {
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();

        using ResultsLog first = Assert.IsType<ResultsLog>(ResultsLog.OpenRun(root.Data, Quick, _t0, time));
        using ResultsLog second = Assert.IsType<ResultsLog>(ResultsLog.OpenRun(root.Data, Quick, _t0, time));

        Assert.NotEqual(first.BaseName, second.BaseName);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(first.LogPath, second.LogPath);
        Assert.True(File.Exists(first.LogPath));
        Assert.True(File.Exists(second.LogPath));
        foreach (ResultsLog log in new[] { first, second })
        {
            Assert.Matches(QuickBaseNamePattern(), log.BaseName);
            Assert.Equal(JournalSamples.BaseName(log.RunId, _t0), log.BaseName);
        }
    }

    [Fact]
    public void OpenRun_LogNameExists_RetriesWithNewRunId()
    {
        using var root = new TestDataRoot();
        string takenPath = ResultsLog.GetLogPath(root.Data, JournalSamples.BaseName(_g1, _t0));
        File.WriteAllBytes(takenPath, _sentinel);
        var runIds = new ScriptedRunIds(_g1, _g2);

        using ResultsLog log = Open(root, CreateTime(), runIds);

        Assert.Equal(_g2, log.RunId);
        Assert.Equal(2, runIds.Calls);
        Assert.Equal(JournalSamples.BaseName(_g2, _t0), log.BaseName);
        Assert.True(File.Exists(log.LogPath));

        // The earlier run's file is neither reused nor overwritten.
        Assert.Equal(_sentinel, File.ReadAllBytes(takenPath));
    }

    [Fact]
    public void OpenRun_FourCollisions_ReturnsNull()
    {
        using var root = new TestDataRoot();
        Guid[] taken = { _g1, _g2, _g3, _g4 };
        foreach (Guid id in taken)
        {
            File.WriteAllBytes(ResultsLog.GetLogPath(root.Data, JournalSamples.BaseName(id, _t0)), _sentinel);
        }

        // A fifth id would succeed, so a null result proves the attempts stop after the first and three retries.
        var runIds = new ScriptedRunIds(_g1, _g2, _g3, _g4, _g5);

        ResultsLog? log = ResultsLog.OpenRun(root.Data, Quick, _t0, CreateTime(), runIds.Next);

        Assert.Null(log);
        Assert.Equal(4, runIds.Calls);
        Assert.False(File.Exists(ResultsLog.GetLogPath(root.Data, JournalSamples.BaseName(_g5, _t0))));
        foreach (Guid id in taken)
        {
            Assert.Equal(_sentinel, File.ReadAllBytes(ResultsLog.GetLogPath(root.Data, JournalSamples.BaseName(id, _t0))));
        }
    }

    [Fact]
    public void OpenRun_LogsDirectoryBlocked_ReturnsNull()
    {
        using var root = new TestDataRoot();
        string blocker = root.CreateBlockingFile("logs");

        ResultsLog? log = ResultsLog.OpenRun(root.Data, Quick, _t0, CreateTime(), new ScriptedRunIds(_g1).Next);

        Assert.Null(log);
        Assert.True(File.Exists(blocker));
        Assert.False(Directory.Exists(root.Data.LogsDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Quick Test")]
    [InlineData("Quick-1")]
    [InlineData("Quick\\..\\x")]
    [InlineData("Qu\u00EFck")]
    public void OpenRun_TestTypeNotAsciiLettersOrDigits_ThrowsArgumentException(string testType)
    {
        using var root = new TestDataRoot();
        var runIds = new ScriptedRunIds(_g1);

        Assert.Throws<ArgumentException>(() => ResultsLog.OpenRun(root.Data, testType, _t0, CreateTime(), runIds.Next));

        Assert.Equal(0, runIds.Calls);
        Assert.Empty(Directory.GetFiles(root.Data.LogsDirectory));
    }

    [Fact]
    public void OpenRun_RunIdSourceReturnsEmptyGuid_ThrowsArgumentException()
    {
        using var root = new TestDataRoot();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ResultsLog.OpenRun(root.Data, Quick, _t0, CreateTime(), () => Guid.Empty));

        Assert.Equal("createRunId", exception.ParamName);
        Assert.Empty(Directory.GetFiles(root.Data.LogsDirectory));
    }

    [Fact]
    public void OpenRun_NullArgument_ThrowsArgumentNullException()
    {
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();

        Assert.Throws<ArgumentNullException>(() => ResultsLog.OpenRun(null!, Quick, _t0, time));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.OpenRun(root.Data, null!, _t0, time));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.OpenRun(root.Data, Quick, _t0, null!));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    [InlineData("..\\outside")]
    [InlineData("sub/name")]
    [InlineData("name:stream")]
    public void GetPaths_UnsafeBaseName_ThrowArgumentException(string baseName)
    {
        using var root = new TestDataRoot();

        Assert.Throws<ArgumentException>(() => ResultsLog.GetLogPath(root.Data, baseName));
        Assert.Throws<ArgumentException>(() => ResultsLog.GetSummaryJsonPath(root.Data, baseName));
        Assert.Throws<ArgumentException>(() => ResultsLog.GetSummaryTextPath(root.Data, baseName));
        Assert.Throws<ArgumentException>(() => ResultsLog.GetRecoveredSummaryJsonPath(root.Data, baseName));
        Assert.Throws<ArgumentException>(() => ResultsLog.GetRecoveredSummaryTextPath(root.Data, baseName));
    }

    // ---------------------------------------------------------------------------------------------
    // Event log
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LogEvents_WriteTimestampedLinesInOrderAsync()
    {
        const string ThermalDetail = "Stopped for safety: CPU temperature reached 95 \u00B0C";
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();
        using ResultsLog log = Open(root, time, new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<StepAttributes> steps = JournalSamples.QuickSteps();
        ulong gib = JournalSamples.GiB;

        log.LogRunStarting(JournalSamples.BeginA(_t0));
        time.Advance(TimeSpan.FromMilliseconds(1_250));
        DateTimeOffset preparedUtc = time.GetUtcNow();
        log.LogPrepared(JournalSamples.Prepared());
        log.LogStepStarted(0, steps.Count, steps[0].Name);
        time.Advance(TimeSpan.FromSeconds(10));
        log.LogProgress(JournalSamples.Progress(0, steps.Count, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(290), gib / 4, gib, 0));
        log.LogErrors(JournalSamples.ErrorRecords(1), 1);
        log.LogStepFinished(
            JournalSamples.Outcome(steps[0], TimeSpan.FromSeconds(42), 4, 4 * gib, 1, completed: true, overrun: TimeSpan.FromSeconds(2)),
            steps.Count);
        log.LogSensorEvent("DIMM #1", "Stale", "RAM temperature unavailable \u2014 RAM thermal limit inactive");
        log.LogJournalFailure("Simulated \"journal\" failure at C:\\data\\journal");
        log.LogNotice("Results could not be saved:\r\ndisk full");
        DateTimeOffset readingUtc = time.GetUtcNow();
        log.LogThermalStop("CPU", 95.2, 95, readingUtc);
        log.LogStopRequested(readingUtc, nameof(RunEndReason.Thermal));
        DateTimeOffset completedUtc = readingUtc + TimeSpan.FromMilliseconds(400);
        log.LogCompleted(
            completedUtc,
            RunEndReason.Thermal,
            ThermalDetail,
            1,
            5 * gib,
            TimeSpan.FromSeconds(75.5),
            TimeSpan.FromSeconds(72));
        log.LogVerdict(completedUtc + TimeSpan.FromMilliseconds(40), RunState.Aborted, RunEndReason.Thermal, ThermalDetail, 1);
        log.LogReleased(completedUtc + TimeSpan.FromMilliseconds(900), gib, TimeSpan.FromMilliseconds(850));
        log.LogReleaseNotConfirmed(completedUtc + TimeSpan.FromSeconds(30));

        string[] lines = await ReadLogLinesAsync(log);

        string[] expectedEvents =
        {
            "START_REQUESTED", "SYSTEM", "MEMORY", "SIZING", "PLAN", "PLAN", "PLAN", "PLAN", "PLAN", "PLAN",
            "PLAN_TARGET", "PREPARED", "STEP_START", "PROGRESS", "ERROR", "STEP_END", "STEP_OVERRUN", "SENSOR",
            "JOURNAL_FAILURE", "NOTICE", "THERMAL_STOP", "STOP_REQUESTED", "COMPLETED", "VERDICT", "RELEASED",
            "RELEASE_NOT_CONFIRMED",
        };
        Assert.Equal(expectedEvents, lines.Select(EventOf));

        // Stamps: Start accepted, the clock for events logged without a time, and the caller's time otherwise.
        Assert.StartsWith(Utc(_t0) + " START_REQUESTED ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith(Utc(_t0) + " SYSTEM ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith(Utc(preparedUtc) + " PREPARED ", Line(lines, "PREPARED"), StringComparison.Ordinal);
        Assert.StartsWith(Utc(readingUtc) + " THERMAL_STOP ", Line(lines, "THERMAL_STOP"), StringComparison.Ordinal);
        Assert.Equal(
            TimeSpan.FromMilliseconds(400),
            StampOf(Line(lines, "COMPLETED")) - StampOf(Line(lines, "STOP_REQUESTED")));

        // Configuration, sizing, page mode and instruction set.
        AssertFields(
            lines[0],
            "run_id=" + JournalSamples.RunIdA.ToString("N", Invariant),
            "test_type=Quick",
            "stop_on_error=true",
            "app_version=" + JournalSamples.AppVersion);
        AssertFields(
            Line(lines, "SYSTEM"),
            "os=" + Quoted(JournalSamples.OsA),
            "cpu=\"Machine A Test CPU\"",
            "logical_processors=7",
            "processor_groups=1",
            "best_isa=AVX2",
            "total_phys_bytes=34359738368",
            "avail_phys_bytes=30000000000",
            "large_page_min_bytes=2097152",
            "lock_privilege=false");
        AssertFields(Line(lines, "MEMORY"), "installed_bytes=34359738368", "type=DDR5", "speed_mts=6000", "channels=2", "dimms=2");
        AssertFields(Line(lines, "SIZING"), "available_bytes=30000000000", "planned_bytes=1073741824");
        string[] plans = lines.Where(line => EventOf(line) == "PLAN").ToArray();
        AssertFields(
            plans[2],
            "step=3/6",
            "name=\"Read-after-write turnaround\"",
            "pattern=Random",
            "order=Turnaround",
            "threads=All",
            "hold_ms=0",
            "budget_ms=50000",
            "coverage_budget=35",
            "primary_mechanism=ReadWriteTurnaround");
        AssertFields(plans[4], "step=5/6", "name=\"Refresh hold\"", "hold_ms=5000", "primary_mechanism=RefreshTimings");
        AssertFields(Line(lines, "PLAN_TARGET"), "target_seconds=300", "target_coverage=200.0");
        AssertFields(
            Line(lines, "PREPARED"),
            "tested_bytes=1073741824",
            "workers=7",
            "page_mode=Standard",
            "isa=AVX2",
            "address_kind=virtual",
            "prepare_ms=1250");

        // Steps, progress and the error.
        AssertFields(Line(lines, "STEP_START"), "step=1/6", "name=\"High-toggle sweep\"");
        AssertFields(Line(lines, "PROGRESS"), "step=1/6", "elapsed_ms=10000", "remaining_ms=290000", "coverage=25.0", "errors=0");
        AssertFields(Line(lines, "ERROR"), "seq=0", "step=2/6", "address=0x0000023A4F7C1D48", "address_kind=virtual", "total=1");
        AssertFields(
            Line(lines, "STEP_END"),
            "step=1/6",
            "name=\"High-toggle sweep\"",
            "completed=true",
            "actual_ms=42000",
            "passes=4",
            "verified_bytes=4294967296",
            "coverage=400.0",
            "errors=1",
            "overrun_ms=2000");
        AssertFields(Line(lines, "STEP_OVERRUN"), "step=1/6", "overrun_ms=2000");

        // Free text is always quoted and escaped, a token only when it must be, and a line break never splits an event.
        AssertFields(
            Line(lines, "SENSOR"),
            "sensor=\"DIMM #1\"",
            "status=Stale",
            "message=\"RAM temperature unavailable \u2014 RAM thermal limit inactive\"");
        AssertFields(Line(lines, "JOURNAL_FAILURE"), "message=\"Simulated \\\"journal\\\" failure at C:\\\\data\\\\journal\"");
        AssertFields(Line(lines, "NOTICE"), "message=\"Results could not be saved:  disk full\"");

        // Thermal stop, stop reason, Completed, verdict and release.
        AssertFields(Line(lines, "THERMAL_STOP"), "sensor=CPU", "celsius=95.2", "limit_celsius=95");
        AssertFields(Line(lines, "STOP_REQUESTED"), "reason=Thermal");
        AssertFields(
            Line(lines, "COMPLETED"),
            "end_reason=Thermal",
            "total_errors=1",
            "verified_bytes=5368709120",
            "elapsed_ms=75500",
            "first_error_ms=72000",
            "detail=" + Quoted(ThermalDetail));
        AssertFields(
            Line(lines, "VERDICT"),
            "verdict=ABORTED",
            "state=Aborted",
            "reason=Thermal",
            "errors=1",
            "detail=" + Quoted(ThermalDetail));
        AssertFields(Line(lines, "RELEASED"), "bytes=1073741824", "release_ms=850");
        AssertFields(Line(lines, "RELEASE_NOT_CONFIRMED"), "message=\"memory release not confirmed\"");
    }

    [Theory]
    [InlineData(RunState.Passed, RunEndReason.Completed, 0UL, "PASS")]
    [InlineData(RunState.Failed, RunEndReason.FirstError, 1UL, "FAIL")]
    [InlineData(RunState.Failed, RunEndReason.Completed, 3UL, "FAIL")]
    [InlineData(RunState.Aborted, RunEndReason.UserStop, 3UL, "ABORTED")]
    public async Task LogVerdict_TerminalState_WritesVerdictStateAndReasonAsync(
        RunState terminalState,
        RunEndReason endReason,
        ulong totalErrors,
        string verdict)
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        TimeSpan? firstError = totalErrors == 0 ? null : TimeSpan.FromSeconds(12.5);

        log.LogCompleted(_t0, endReason, null, totalErrors, JournalSamples.GiB, TimeSpan.FromSeconds(30), firstError);
        log.LogVerdict(_t0 + TimeSpan.FromMilliseconds(40), terminalState, endReason, null, totalErrors);

        string[] lines = await ReadLogLinesAsync(log);

        Assert.Equal(new[] { "COMPLETED", "VERDICT" }, lines.Select(EventOf));
        AssertFields(
            lines[0],
            "end_reason=" + endReason,
            "total_errors=" + totalErrors.ToString(Invariant),
            "first_error_ms=" + (firstError is null ? "-1" : "12500"),
            "detail=\"\"");
        AssertFields(
            lines[1],
            "verdict=" + verdict,
            "state=" + terminalState,
            "reason=" + endReason,
            "errors=" + totalErrors.ToString(Invariant),
            "detail=\"\"");
    }

    [Theory]
    [InlineData(RunState.Idle)]
    [InlineData(RunState.Preparing)]
    [InlineData(RunState.Running)]
    [InlineData((RunState)42)]
    public async Task LogVerdict_NotTerminalState_ThrowsAndWritesNothingAsync(RunState state)
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => log.LogVerdict(_t0, state, RunEndReason.Completed, null, 0));

        Assert.Equal("terminalState", exception.ParamName);
        Assert.Empty(await ReadLogLinesAsync(log));
    }

    [Theory]
    [InlineData("UserStop", "reason=UserStop")]
    [InlineData("C:\\stop", "reason=C:\\stop")]
    [InlineData("", "reason=\"\"")]
    [InlineData("User Stop", "reason=\"User Stop\"")]
    [InlineData("User\tStop", "reason=\"User Stop\"")]
    [InlineData("Say \"stop\" C:\\x", "reason=\"Say \\\"stop\\\" C:\\\\x\"")]
    public async Task LogStopRequested_ReasonToken_QuotedOnlyWhenNeededAsync(string reason, string expectedField)
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));

        log.LogStopRequested(_t0, reason);

        string line = Assert.Single(await ReadLogLinesAsync(log));
        Assert.Equal(Utc(_t0) + " STOP_REQUESTED " + expectedField, line);
    }

    [Fact]
    public async Task LogStopRequested_EngineDecisionTime_WritesWholeMillisecondsIntoTestAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));

        log.LogStopRequested(_t0, nameof(RunEndReason.FirstError), TimeSpan.FromTicks(521_259_999));

        string line = Assert.Single(await ReadLogLinesAsync(log));
        Assert.Equal(Utc(_t0) + " STOP_REQUESTED reason=FirstError time_into_test_ms=52125", line);
    }

    [Theory]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.DataFolder)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.LocalAppData)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.RoamingAppData)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.UserProfile)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.UserNameSegment)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.ProfileSibling)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.ForeignProfile)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.ComputerName)]
    [InlineData(FreeTextEvent.JournalFailure, AccountLocation.Placeholders)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.DataFolder)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.LocalAppData)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.RoamingAppData)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.UserProfile)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.UserNameSegment)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.ProfileSibling)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.ForeignProfile)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.ComputerName)]
    [InlineData(FreeTextEvent.Notice, AccountLocation.Placeholders)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.DataFolder)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.LocalAppData)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.RoamingAppData)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.UserProfile)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.UserNameSegment)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.ProfileSibling)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.ForeignProfile)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.ComputerName)]
    [InlineData(FreeTextEvent.CallbackFault, AccountLocation.Placeholders)]
    public async Task LogFreeText_AccountLocations_WrittenAsPlaceholdersAsync(FreeTextEvent freeTextEvent, AccountLocation location)
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        (string path, string expectedPath) = AccountLocationText(location, root);
        string message = $"The process cannot access the file '{path}' because it is being used by another process.";
        string redacted = message.Replace(path, expectedPath, StringComparison.Ordinal);

        (string eventName, string key, string valuePrefix) = LogFreeText(log, freeTextEvent, message);

        // A redacted text redacts to itself: logged a second time, it is written unchanged.
        _ = LogFreeText(log, freeTextEvent, redacted);

        string[] lines = await ReadLogLinesAsync(log);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal(eventName, EventOf(line));
            AssertFields(line, key + "=" + Quoted(valuePrefix + redacted));
        });
    }

    [Fact]
    public async Task LogCallbackFault_WritesSourceAndExceptionTypeAndMessageAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        DateTimeOffset caughtUtc = _t0 + TimeSpan.FromMilliseconds(1_500);

        var exception = new InvalidOperationException("Sink \"failed\"\r\nagain");

        log.LogCallbackFault(caughtUtc, "EngineCallback:ProgressChanged handler", exception);

        string line = Assert.Single(await ReadLogLinesAsync(log));
        Assert.Equal(
            Utc(caughtUtc) + " CALLBACK_FAULT source=\"EngineCallback:ProgressChanged handler\""
                + " error=\"System.InvalidOperationException: Sink \\\"failed\\\"  again\"",
            line);
    }

    [Fact]
    public async Task LogEvents_ReservedOrUndefinedValues_WrittenByNameAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        ErrorRecord damaged = JournalSamples.ErrorRecords(1)[0] with
        {
            AddressKind = (AddressKind)7,
            RereadOutcome = (RereadOutcome)9,
        };

        // A reserved or out-of-range value is logged as written, never thrown on: the run keeps its event log.
        log.LogPrepared(JournalSamples.Prepared() with { AddressKind = AddressKind.Physical });
        log.LogPrepared(JournalSamples.Prepared() with { AddressKind = (AddressKind)7, IsaSelected = (InstructionSet)9 });
        log.LogErrors(new[] { damaged }, 1);

        string[] lines = await ReadLogLinesAsync(log);

        Assert.Equal(new[] { "PREPARED", "PREPARED", "ERROR" }, lines.Select(EventOf));
        AssertFields(lines[0], "isa=AVX2", "address_kind=physical");
        AssertFields(lines[1], "isa=9", "address_kind=7");
        AssertFields(lines[2], "address_kind=7", "outcome=\"9\"");
    }

    [Fact]
    public async Task LogProgress_EveryTenSimulatedSeconds_WritesProgressLineAsync()
    {
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();
        using ResultsLog log = Open(root, time, new ScriptedRunIds(JournalSamples.RunIdA));
        CultureInfo original = CultureInfo.CurrentCulture;

        // Lines are formatted on the calling thread, so a comma-decimal culture there must not reach them.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            for (int second = 0; second <= 25; second++)
            {
                log.LogProgress(ProgressAt(second));
                time.Advance(TimeSpan.FromSeconds(1));
            }

            // A late tick that overshoots several multiples writes one line; the next is due at the next multiple.
            log.LogProgress(ProgressAt(47));
            log.LogProgress(ProgressAt(49));
            log.LogProgress(ProgressAt(50));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        string[] lines = await ReadLogLinesAsync(log);

        Assert.All(lines, line => Assert.Matches(ProgressLinePattern(), line));
        Assert.Equal(
            new[]
            {
                Utc(_t0 + TimeSpan.FromSeconds(10)) + " PROGRESS step=2/6 elapsed_ms=10000 remaining_ms=-1 coverage=50.0 errors=1",
                Utc(_t0 + TimeSpan.FromSeconds(20)) + " PROGRESS step=3/6 elapsed_ms=20000 remaining_ms=280000 coverage=100.0 errors=2",
                Utc(_t0 + TimeSpan.FromSeconds(26)) + " PROGRESS step=5/6 elapsed_ms=47000 remaining_ms=253000 coverage=235.0 errors=6",
                Utc(_t0 + TimeSpan.FromSeconds(26)) + " PROGRESS step=6/6 elapsed_ms=50000 remaining_ms=250000 coverage=250.0 errors=7",
            },
            lines);
    }


    [Fact]
    public async Task LogErrors_WritesEachRecordUpToCapAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<ErrorRecord> records = JournalSamples.ErrorRecords(3);
        log.LogRunStarting(JournalSamples.BeginA(_t0));

        log.LogErrors(records, 3);
        log.LogErrors(Array.Empty<ErrorRecord>(), 3);

        string[] lines = await ReadLogLinesAsync(log);
        string[] errorLines = lines.Where(line => EventOf(line) == "ERROR").ToArray();

        Assert.Equal(records.Count, errorLines.Length);
        Assert.DoesNotContain(lines, line => EventOf(line) == "ERROR_CAP_REACHED");
        for (int i = 0; i < records.Count; i++)
        {
            ErrorRecord record = records[i];
            AssertFields(
                errorLines[i],
                "seq=" + i.ToString(Invariant),
                "step=2/6",
                "name=\"Random bank thrash\"",
                "pass=1",
                "time_into_test_ms=" + (72_000 + i).ToString(Invariant),
                "address=" + SummaryFormat.Hex64(record.Address),
                "address_kind=virtual",
                "expected=" + SummaryFormat.Hex64(record.Expected),
                "actual=" + SummaryFormat.Hex64(record.Actual),
                "reread=" + SummaryFormat.Hex64(record.Reread),
                "bit_mask=" + SummaryFormat.Hex64(record.Expected ^ record.Actual),
                "bits=" + string.Join(',', record.BitPositions.Select(bit => bit.ToString(Invariant))),
                "outcome=" + Quoted(RereadOutcomeText.Describe(record.RereadOutcome)),
                "access=VerifyAfterWrite",
                "time_since_write_us=750",
                "active_workers=7",
                "total=3");

            MatchCollection hexFields = ErrorHexFieldPattern().Matches(errorLines[i]);
            Assert.Equal(5, hexFields.Count);
            Assert.All(hexFields, field => Assert.Matches(Hex64Pattern(), field.Groups["value"].Value));
        }

        AssertFields(
            errorLines[0],
            "address=0x0000023A4F7C1D48",
            "expected=0x5A3C5A3C5A3C5A3C",
            "actual=0x5A3E5A3C5A3C5A3C",
            "reread=0x5A3E5A3C5A3C5A3C",
            "bit_mask=0x0002000000000000",
            "bits=49",
            "outcome=\"repeated wrong value: likely write or storage fault\"");
    }

    [Fact]
    public async Task LogErrors_BatchCrossesCap_StopsAtStoredDetailCapAsync()
    {
        const int FirstBatch = ErrorGrouping.StoredDetailCap - 2;
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<ErrorRecord> records = JournalSamples.ErrorRecords(ErrorGrouping.StoredDetailCap + 2);

        // No run-start line: the plan's step count is unknown, so steps are written without "/N".
        log.LogErrors(records.Take(FirstBatch).ToArray(), FirstBatch);
        log.LogErrors(records.Skip(FirstBatch).ToArray(), (ulong)records.Count);

        // Past the cap, batches stop carrying records and later ones are not logged at all.
        log.LogErrors(Array.Empty<ErrorRecord>(), 15_000);
        log.LogErrors(JournalSamples.ErrorRecords(1), 15_001);

        string[] lines = await ReadLogLinesAsync(log);
        string[] errorLines = lines.Where(line => EventOf(line) == "ERROR").ToArray();

        Assert.Equal(ErrorGrouping.StoredDetailCap, errorLines.Length);
        Assert.Equal(ErrorGrouping.StoredDetailCap + 1, lines.Length);
        AssertFields(errorLines[0], "seq=0", "step=2", "total=9998");
        AssertFields(errorLines[^1], "seq=9999", "step=2", "total=10002");
        string capLine = Assert.Single(lines, line => EventOf(line) == "ERROR_CAP_REACHED");
        Assert.Equal(lines[^1], capLine);
        AssertFields(capLine, "stored=10000", "total=10002");
    }

    [Fact]
    public async Task LogErrors_CallerReusesItsList_LogHoldsRecordsAsPassedAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<ErrorRecord> passed = JournalSamples.ErrorRecords(3);
        var reused = new ReusableRecordList(passed);

        // The caller refills its list for its next batch as soon as the call returns, before or after the drain
        // has rendered this one.
        log.LogErrors(reused, 3);
        reused.Refill(JournalSamples.ErrorRecords(8)[7]);

        string[] lines = await ReadLogLinesAsync(log);

        Assert.Equal(passed.Count, lines.Length);
        for (int i = 0; i < passed.Count; i++)
        {
            AssertFields(
                lines[i],
                "seq=" + i.ToString(Invariant),
                "time_into_test_ms=" + (72_000 + i).ToString(Invariant),
                "address=" + SummaryFormat.Hex64(passed[i].Address),
                "actual=" + SummaryFormat.Hex64(passed[i].Actual));
        }

        // Only the call itself read the caller's list: the drain rendered the log's own copy.
        Assert.Equal(0, reused.ReadsOffCallerThread);
    }

    [Fact]
    public async Task LogErrors_LinesCarryTimeAndStepCountOfTheCallAsync()
    {
        const int LargeBatch = 1_000;
        using var root = new TestDataRoot();
        FakeTimeProvider time = CreateTime();
        using ResultsLog log = Open(root, time, new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<ErrorRecord> records = JournalSamples.ErrorRecords(LargeBatch + 2);
        log.LogRunStarting(JournalSamples.BeginA(_t0));

        // The large batch keeps the drain rendering while the clock and the plan's step count move on.
        log.LogErrors(records.Take(LargeBatch).ToArray(), LargeBatch);
        time.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset secondCallUtc = time.GetUtcNow();
        log.LogErrors(new[] { records[LargeBatch] }, LargeBatch + 1);
        time.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset thirdCallUtc = time.GetUtcNow();
        log.LogRunStarting(JournalSamples.BeginA(_t0, stepCount: 3));
        log.LogErrors(new[] { records[LargeBatch + 1] }, LargeBatch + 2);
        time.Advance(TimeSpan.FromSeconds(1));

        string[] lines = await ReadLogLinesAsync(log);
        string[] errorLines = lines.Where(line => EventOf(line) == "ERROR").ToArray();

        Assert.Equal(records.Count, errorLines.Length);
        Assert.All(errorLines[..LargeBatch], line =>
        {
            Assert.StartsWith(Utc(_t0) + " ERROR ", line, StringComparison.Ordinal);
            AssertFields(line, "step=2/6", "total=1000");
        });
        Assert.StartsWith(Utc(secondCallUtc) + " ERROR ", errorLines[LargeBatch], StringComparison.Ordinal);
        AssertFields(errorLines[LargeBatch], "seq=1000", "step=2/6", "total=1001");
        Assert.StartsWith(Utc(thirdCallUtc) + " ERROR ", errorLines[^1], StringComparison.Ordinal);
        AssertFields(errorLines[^1], "seq=1001", "step=2/3", "total=1002");
    }

    [Fact]
    public async Task LogErrors_BatchesOfManyWritesBetweenEvents_KeepQueueOrderAsync()
    {
        // Each batch spans several of the drain's writes, and the second one reaches the cap.
        const int FirstBatch = 600;
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        IReadOnlyList<ErrorRecord> records = JournalSamples.ErrorRecords(ErrorGrouping.StoredDetailCap + 5);

        log.LogStepStarted(1, 6, records[0].StepName);
        log.LogErrors(records.Take(FirstBatch).ToArray(), FirstBatch);
        log.LogNotice("between batches");
        log.LogErrors(records.Skip(FirstBatch).ToArray(), (ulong)records.Count);
        log.LogNotice("after the cap");

        string[] lines = await ReadLogLinesAsync(log);

        string[] expectedEvents = new[] { "STEP_START" }
            .Concat(Enumerable.Repeat("ERROR", FirstBatch))
            .Append("NOTICE")
            .Concat(Enumerable.Repeat("ERROR", ErrorGrouping.StoredDetailCap - FirstBatch))
            .Append("ERROR_CAP_REACHED")
            .Append("NOTICE")
            .ToArray();
        Assert.Equal(expectedEvents, lines.Select(EventOf));
        AssertFields(lines[FirstBatch + 1], "message=\"between batches\"");
        AssertFields(Line(lines, "ERROR_CAP_REACHED"), "stored=10000", "total=10005");
        AssertFields(lines[^1], "message=\"after the cap\"");

        // The records keep the caller's order across the drain's writes and across the two batches.
        string[] errorLines = lines.Where(line => EventOf(line) == "ERROR").ToArray();
        for (int i = 0; i < errorLines.Length; i++)
        {
            AssertFields(errorLines[i], "seq=" + i.ToString(Invariant));
        }
    }

    [Fact]
    public async Task LogMethods_InvalidArguments_ThrowAndWriteNothingAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        StepAttributes step = JournalSamples.QuickSteps()[0];
        StepOutcome outcome = JournalSamples.Outcome(step, step.TimeBudget, 1, JournalSamples.GiB, 0, completed: true);
        ErrorRecord valid = JournalSamples.ErrorRecords(1)[0];

        Assert.Throws<ArgumentNullException>(() => log.LogRunStarting(null!));
        Assert.Throws<ArgumentNullException>(() => log.LogPrepared(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.LogStepStarted(-1, 6, step.Name));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.LogStepStarted(0, 0, step.Name));
        Assert.Throws<ArgumentNullException>(() => log.LogStepStarted(0, 6, null!));
        Assert.Throws<ArgumentNullException>(() => log.LogStepFinished(null!, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.LogStepFinished(outcome, 0));
        Assert.Throws<ArgumentNullException>(() => log.LogProgress(null!));
        Assert.Throws<ArgumentNullException>(() => log.LogErrors(null!, 0));

        // A null element is rejected before any record of the batch is logged.
        ArgumentException nullElement = Assert.Throws<ArgumentException>(() => log.LogErrors(new[] { valid, null! }, 2));
        Assert.Equal("records", nullElement.ParamName);

        Assert.Throws<ArgumentNullException>(() => log.LogSensorEvent(null!, "Stale", "message"));
        Assert.Throws<ArgumentNullException>(() => log.LogSensorEvent("RAM", null!, "message"));
        Assert.Throws<ArgumentNullException>(() => log.LogSensorEvent("RAM", "Stale", null!));
        Assert.Throws<ArgumentNullException>(() => log.LogThermalStop(null!, 95, 95, _t0));
        Assert.Throws<ArgumentNullException>(() => log.LogJournalFailure(null!));
        Assert.Throws<ArgumentNullException>(() => log.LogStopRequested(_t0, null!));
        Assert.Throws<ArgumentNullException>(() => log.LogNotice(null!));
        Assert.Throws<ArgumentNullException>(() => log.LogCallbackFault(_t0, null!, new InvalidOperationException("Unused")));
        Assert.Throws<ArgumentException>(() => log.LogCallbackFault(_t0, " ", new InvalidOperationException("Unused")));
        Assert.Throws<ArgumentNullException>(() => log.LogCallbackFault(_t0, "EngineCallback:on_progress", null!));

        Assert.Empty(await ReadLogLinesAsync(log));
    }

    [Fact]
    public async Task Dispose_ClosesLogAndDiscardsLaterEventsAsync()
    {
        using var root = new TestDataRoot();
        ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        log.LogNotice("before dispose");

        log.Dispose();
        log.LogNotice("after dispose");

        // After Dispose, the flush task is the drain itself, which closes the file when it ends.
        await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
        log.Dispose();

        using (var exclusive = new FileStream(log.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.CanWrite);
        }

        string text = await File.ReadAllTextAsync(log.LogPath, Cancellation);
        Assert.Contains("NOTICE message=\"before dispose\"" + LineEnd, text, StringComparison.Ordinal);
        Assert.DoesNotContain("after dispose", text, StringComparison.Ordinal);
        Assert.Null(log.WriteFailure);
    }

    // A byte-range lock held through a second handle makes the drain's writes fail with a lock violation, with no
    // access-control change. The first failure is kept for the run controller to report, and the log keeps accepting
    // events and completing flushes, so the run is never held by its event log.
    [Fact]
    public async Task LogEvents_WriteFails_RecordsFirstFailureAndKeepsCompletingFlushesAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        log.LogNotice("before the lock");
        await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
        Assert.Null(log.WriteFailure);

        using (var blocker = new FileStream(log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            blocker.Lock(0, long.MaxValue);
            try
            {
                log.LogNotice("while locked");
                await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
                IOException failure = Assert.IsAssignableFrom<IOException>(log.WriteFailure);

                log.LogNotice("still locked");
                await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
                Assert.Same(failure, log.WriteFailure);
            }
            finally
            {
                blocker.Unlock(0, long.MaxValue);
            }
        }

        log.LogNotice("after the lock");
        await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
        log.Dispose();
        await log.FlushAsync().WaitAsync(_ceiling, Cancellation);

        Assert.NotNull(log.WriteFailure);
        string text = Encoding.UTF8.GetString(await ReadSharedBytesAsync(log.LogPath));
        Assert.Contains("NOTICE message=\"before the lock\"" + LineEnd, text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Publication
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PublishSummaryAsync_WritesJsonAndText_NoTempLeftAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(3), TwoHints());

        SummaryPublishResult result = await log.PublishSummaryAsync(summary).WaitAsync(_ceiling, Cancellation);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(log.SummaryJsonPath, result.JsonPath);
        Assert.Equal(log.SummaryTextPath, result.TextPath);
        Assert.True(File.Exists(log.SummaryJsonPath));
        Assert.True(File.Exists(log.SummaryTextPath));
        Assert.Empty(Directory.GetFiles(root.Data.LogsDirectory, "*.tmp"));

        byte[] jsonBytes = await File.ReadAllBytesAsync(log.SummaryJsonPath, Cancellation);
        byte[] textBytes = await File.ReadAllBytesAsync(log.SummaryTextPath, Cancellation);
        Assert.False(jsonBytes.AsSpan().StartsWith(_utf8Bom), "The JSON summary starts with a byte-order mark.");
        Assert.False(textBytes.AsSpan().StartsWith(_utf8Bom), "The text summary starts with a byte-order mark.");
        string json = Encoding.UTF8.GetString(jsonBytes);
        string text = Encoding.UTF8.GetString(textBytes);

        Assert.Equal(RunSummaryJson.Serialize(summary), json);
        Assert.True(RunSummaryJson.TryDeserialize(json, out RunSummary? readBack));
        Assert.Equal(summary, readBack);
        Assert.Equal(summary.GetHashCode(), readBack.GetHashCode());
        Assert.Equal(TextSummaryFormatter.Format(summary), text);
        Assert.Equal(JournalSamples.RunIdA, TextSummaryFormatter.TryParseRunId(text));

        // The .log names each file relative to the data folder, whose location can name the Windows account.
        string[] lines = await ReadLogLinesAsync(log);
        Assert.Equal(new[] { "SUMMARY_PUBLISHED", "SUMMARY_PUBLISHED" }, lines.Select(EventOf));
        AssertFields(lines[0], "path=" + Quoted(@"logs\" + log.BaseName + "-summary.json"));
        AssertFields(lines[1], "path=" + Quoted(@"logs\" + log.BaseName + "-summary.txt"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishSummaryAsync_FinalFileExists_FailsAndLeavesFileUntouchedAsync(bool jsonExists)
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        string existing = jsonExists ? log.SummaryJsonPath : log.SummaryTextPath;
        string other = jsonExists ? log.SummaryTextPath : log.SummaryJsonPath;
        await File.WriteAllBytesAsync(existing, _sentinel, Cancellation);
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(3), TwoHints());

        SummaryPublishResult result = await log.PublishSummaryAsync(summary).WaitAsync(_ceiling, Cancellation);

        // A failure is reported, never thrown, and its message is the notice's "<error>".
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(jsonExists ? null : log.SummaryJsonPath, result.JsonPath);
        Assert.Equal(jsonExists ? log.SummaryTextPath : null, result.TextPath);

        // The rename never overwrites, and the other summary is still published.
        Assert.Equal(_sentinel, await File.ReadAllBytesAsync(existing, Cancellation));
        Assert.True(File.Exists(other));
        Assert.Empty(Directory.GetFiles(root.Data.LogsDirectory, "*.tmp"));

        // Paths are relative to the data folder, and the error writes the data folder's location as <data>.
        string[] lines = await ReadLogLinesAsync(log);
        string failed = Assert.Single(lines, line => EventOf(line) == "SUMMARY_FAILED");
        AssertFields(
            failed,
            "path=" + Quoted(@"logs\" + Path.GetFileName(existing)),
            "error=" + Quoted(result.Error!.Replace(root.Data.RootPath, "<data>", StringComparison.OrdinalIgnoreCase)));
        string published = Assert.Single(lines, line => EventOf(line) == "SUMMARY_PUBLISHED");
        AssertFields(published, "path=" + Quoted(@"logs\" + Path.GetFileName(other)));
        string escapedRoot = Quoted(root.Data.RootPath)[1..^1];
        Assert.All(lines, line => Assert.DoesNotContain(escapedRoot, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PublishSummaryAsync_SummaryOfAnotherRun_ThrowsAndPublishesNothingAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        RunSummary otherRun = CompletedSummary(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty, runId: JournalSamples.RunIdB);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => { _ = log.PublishSummaryAsync(otherRun); });
        Assert.Throws<ArgumentNullException>(() => { _ = log.PublishSummaryAsync(null!); });

        Assert.Equal("summary", exception.ParamName);
        Assert.False(File.Exists(log.SummaryJsonPath));
        Assert.False(File.Exists(log.SummaryTextPath));
        Assert.Empty(await ReadLogLinesAsync(log));
    }

    [Fact]
    public void PublishFile_ExistingTarget_DoesNotOverwrite()
    {
        using var root = new TestDataRoot();
        string target = root.Combine("logs", "earlier-summary.json");
        File.WriteAllBytes(target, _sentinel);

        Assert.ThrowsAny<IOException>(() => ResultsLog.PublishFile(target, "{\"replacement\":true}"));

        Assert.Equal(_sentinel, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void PublishFile_NewTarget_WritesViaTemp()
    {
        const string Content = "Coverage: 412.6 % \u2014 CPU max: 71.5 \u00B0C\r\n";
        using var root = new TestDataRoot();
        string target = root.Combine("logs", "new-summary.txt");
        string temp = target + ".tmp";

        // A longer temporary file left by an earlier crash is replaced, never appended to or kept.
        File.WriteAllText(temp, new string('x', 4096));

        ResultsLog.PublishFile(target, Content);

        Assert.False(File.Exists(temp));
        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Content), File.ReadAllBytes(target));
    }

    [Fact]
    public void PublishFile_InvalidArguments_ThrowAndWriteNothing()
    {
        using var root = new TestDataRoot();
        string target = root.Combine("logs", "unused-summary.json");

        Assert.Throws<ArgumentNullException>(() => ResultsLog.PublishFile(null!, "content"));
        Assert.Throws<ArgumentException>(() => ResultsLog.PublishFile("   ", "content"));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.PublishFile(target, null!));

        Assert.Empty(Directory.GetFiles(root.Data.LogsDirectory));
    }


    // ---------------------------------------------------------------------------------------------
    // JSON value types, schema version 1
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SummaryJson_ValueTypes_MatchSchemaAsync()
    {
        using var root = new TestDataRoot();
        using ResultsLog failedLog = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        using ResultsLog passedLog = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdB));
        IReadOnlyList<ErrorRecord> records = JournalSamples.ErrorRecords(3);

        // A 256 GiB machine testing 192 GiB, whose run verified about 5 x 10^12 bytes (2,500 %). The schema holds
        // verified bytes only as coverage, so errors.total carries a count of the same magnitude.
        RunSummarySource source = JournalSamples.CompletedRunSource(records, TwoHints(), ExhaustiveVerifiedBytes);
        source = source with
        {
            Begin = source.Begin with
            {
                Memory = source.Begin.Memory with { InstalledBytes = InstalledBytes256GiB },
                AvailableBytes = 240 * JournalSamples.GiB,
                PlannedBytes = TestedBytes192GiB,
            },
            Prepared = source.Prepared! with { TestedBytes = TestedBytes192GiB },
            Result = source.Result! with { VerifiedBytes = ExhaustiveVerifiedBytes },
        };
        RunSummary failed = RunSummary.Build(source);
        RunSummary passed = CompletedSummary(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty, runId: JournalSamples.RunIdB);

        using JsonDocument failedDocument = await PublishAndParseAsync(failedLog, failed);
        using JsonDocument passedDocument = await PublishAndParseAsync(passedLog, passed);
        JsonElement json = failedDocument.RootElement;

        Assert.Equal(1, Kind(JsonValueKind.Number, json, "schemaVersion").GetInt32());
        Assert.Equal(RunSummarySource.DefaultAppName, Kind(JsonValueKind.String, json, "app", "name").GetString());
        Assert.Equal(JournalSamples.AppVersion, Kind(JsonValueKind.String, json, "app", "version").GetString());

        // run.*
        string? id = Kind(JsonValueKind.String, json, "run", "id").GetString();
        Assert.True(Guid.TryParseExact(id, "N", out Guid runId), $"run.id '{id}' is not an N-format GUID.");
        Assert.Equal(failedLog.RunId, runId);
        Assert.Equal(Quick, Kind(JsonValueKind.String, json, "run", "testType").GetString());
        Kind(JsonValueKind.False, json, "run", "stopOnError");
        string? verdict = Kind(JsonValueKind.String, json, "run", "verdict").GetString();
        Assert.Contains(verdict, new[] { "PASS", "FAIL", "ABORTED" });
        Assert.Equal("FAIL", verdict);
        string? endReason = Kind(JsonValueKind.String, json, "run", "endReason").GetString();
        Assert.True(
            Enum.TryParse(endReason, ignoreCase: false, out RunEndReason reason)
                && Enum.IsDefined(reason)
                && reason.ToString() == endReason,
            $"run.endReason '{endReason}' is not a RunEndReason name.");
        Assert.Equal(nameof(RunEndReason.Completed), endReason);
        Assert.Equal(string.Empty, Kind(JsonValueKind.String, json, "run", "endDetail").GetString());
        foreach (string stamp in new[] { "startRequestedUtc", "startedUtc", "endedUtc", "verdictUtc" })
        {
            Assert.Matches(IsoUtcPattern(), Kind(JsonValueKind.String, json, "run", stamp).GetString()!);
        }

        Assert.Equal(Utc(_t0), At(json, "run", "startRequestedUtc").GetString());
        Assert.Equal(300.0, Kind(JsonValueKind.Number, json, "run", "durationSeconds").GetDouble());
        Assert.Equal(301.29, Kind(JsonValueKind.Number, json, "run", "timeToVerdictSeconds").GetDouble(), 6);
        Assert.Equal(72.0, Kind(JsonValueKind.Number, json, "run", "firstErrorSeconds").GetDouble());
        Kind(JsonValueKind.False, json, "run", "recovered");
        Kind(JsonValueKind.False, json, "run", "partial");

        // system.*
        Assert.Equal(JournalSamples.OsA, Kind(JsonValueKind.String, json, "system", "os").GetString());
        Kind(JsonValueKind.String, json, "system", "cpu");
        Assert.Equal(7, Kind(JsonValueKind.Number, json, "system", "logicalProcessors").GetInt32());
        Assert.Equal(1, Kind(JsonValueKind.Number, json, "system", "processorGroups").GetInt32());
        Assert.Equal("AVX2", Kind(JsonValueKind.String, json, "system", "instructionSet").GetString());
        Kind(JsonValueKind.False, json, "system", "reducedInstructionSet");

        // memory.*: byte counts are exact JSON integers, never hex text or exponent notation.
        AssertExactInteger(InstalledBytes256GiB, json, "memory", "installedBytes");
        AssertExactInteger(240 * JournalSamples.GiB, json, "memory", "availableBytesAtStart");
        AssertExactInteger(TestedBytes192GiB, json, "memory", "plannedBytes");
        AssertExactInteger(TestedBytes192GiB, json, "memory", "testedBytes");
        Assert.Equal(0.85, Kind(JsonValueKind.Number, json, "memory", "releaseSeconds").GetDouble());
        Assert.Equal("Standard", Kind(JsonValueKind.String, json, "memory", "pageMode").GetString());
        Assert.Equal("Virtual", Kind(JsonValueKind.String, json, "memory", "addressKind").GetString());
        Assert.Equal("DDR5", Kind(JsonValueKind.String, json, "memory", "type").GetString());
        Assert.Equal(6000, Kind(JsonValueKind.Number, json, "memory", "speedMTs").GetInt32());
        Assert.Equal(2, Kind(JsonValueKind.Number, json, "memory", "channels").GetInt32());
        Assert.Equal(2, Kind(JsonValueKind.Number, json, "memory", "dimms").GetInt32());

        // plan.*, steps[], coverage.* and temperatures.*
        Assert.Equal(300.0, Kind(JsonValueKind.Number, json, "plan", "targetSeconds").GetDouble());
        Assert.Equal(200.0, Kind(JsonValueKind.Number, json, "plan", "targetCoveragePercent").GetDouble());
        JsonElement[] steps = Kind(JsonValueKind.Array, json, "steps").EnumerateArray().ToArray();
        Assert.Equal(6, steps.Length);
        for (int i = 0; i < steps.Length; i++)
        {
            Assert.Equal(i + 1, Kind(JsonValueKind.Number, steps[i], "index").GetInt32());
            foreach (string text in new[] { "name", "pattern", "accessOrder", "threadMode", "primaryMechanism" })
            {
                Kind(JsonValueKind.String, steps[i], text);
            }

            string[] numbers =
            {
                "holdSeconds", "plannedSeconds", "actualSeconds", "overrunSeconds", "plannedCoveragePercent", "coveragePercent",
            };
            foreach (string number in numbers)
            {
                Kind(JsonValueKind.Number, steps[i], number);
            }

            Assert.Equal(4, Kind(JsonValueKind.Number, steps[i], "passes").GetInt32());
            Assert.True(Kind(JsonValueKind.Number, steps[i], "errors").TryGetUInt64(out _));
            Kind(JsonValueKind.True, steps[i], "completed");
        }

        Assert.Equal(40.0, At(steps[0], "actualSeconds").GetDouble());
        Assert.Equal(5.0, At(steps[4], "holdSeconds").GetDouble());
        Assert.Equal(2500.0, Kind(JsonValueKind.Number, json, "coverage", "percent").GetDouble());
        Assert.Equal(200.0, Kind(JsonValueKind.Number, json, "coverage", "targetPercent").GetDouble());
        Kind(JsonValueKind.True, json, "coverage", "targetReached");
        Assert.Equal(71.5, Kind(JsonValueKind.Number, json, "temperatures", "cpuMaxC").GetDouble());
        Assert.Equal(48.0, Kind(JsonValueKind.Number, json, "temperatures", "ramMaxC").GetDouble());
        Assert.Equal(JournalSamples.AvailableStatus, Kind(JsonValueKind.String, json, "temperatures", "cpuStatus").GetString());
        Assert.Equal(JournalSamples.AvailableStatus, Kind(JsonValueKind.String, json, "temperatures", "ramStatus").GetString());

        // errors.*: counts are numbers; addresses, masks and data words are 0x-prefixed upper-case hex strings.
        AssertExactInteger(ExhaustiveVerifiedBytes, json, "errors", "total");
        Kind(JsonValueKind.False, json, "errors", "truncated");
        Kind(JsonValueKind.True, json, "errors", "detailAvailable");
        Assert.Equal(3, Kind(JsonValueKind.Number, json, "errors", "storedCount").GetInt32());
        Assert.Equal(ErrorGrouping.StoredDetailCap, Kind(JsonValueKind.Number, json, "errors", "storedCap").GetInt32());
        Assert.Equal(3, Kind(JsonValueKind.Number, json, "errors", "storedDistinctAddresses").GetInt32());
        foreach (JsonElement group in Kind(JsonValueKind.Array, json, "errors", "groups").EnumerateArray())
        {
            Assert.Matches(Hex64Pattern(), Kind(JsonValueKind.String, group, "wordAddress").GetString()!);
            Assert.Matches(Hex64Pattern(), Kind(JsonValueKind.String, group, "bitMaskUnion").GetString()!);
            Assert.Equal(1, Kind(JsonValueKind.Number, group, "count").GetInt32());
            Kind(JsonValueKind.String, group, "firstStepName");
            Kind(JsonValueKind.Number, group, "firstTimeIntoTest");
            Kind(JsonValueKind.Number, group, "lastTimeIntoTest");
        }

        JsonElement[] items = Kind(JsonValueKind.Array, json, "errors", "records").EnumerateArray().ToArray();
        Assert.Equal(records.Count, items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            JsonElement item = items[i];
            foreach (string hex in new[] { "address", "bitMask", "expected", "actual", "reread" })
            {
                Assert.Matches(Hex64Pattern(), Kind(JsonValueKind.String, item, hex).GetString()!);
            }

            ulong expected = ParseHex(At(item, "expected"));
            ulong actual = ParseHex(At(item, "actual"));
            Assert.Equal(SummaryFormat.Hex64(expected ^ actual), At(item, "bitMask").GetString());
            Assert.Equal(records[i].Address, ParseHex(At(item, "address")));
            Assert.Equal(records[i].Reread, ParseHex(At(item, "reread")));
            Assert.Equal(records[i].Sequence, Kind(JsonValueKind.Number, item, "sequence").GetUInt64());
            Assert.Equal(records[i].RegionOffset, Kind(JsonValueKind.Number, item, "regionOffset").GetUInt64());
            Assert.Equal(
                records[i].BitPositions,
                Kind(JsonValueKind.Array, item, "bitPositions").EnumerateArray().Select(bit => bit.GetInt32()));
            Assert.Equal("Virtual", Kind(JsonValueKind.String, item, "addressKind").GetString());
            Assert.Equal(records[i].RereadOutcome.ToString(), Kind(JsonValueKind.String, item, "rereadOutcome").GetString());
            Assert.Equal(nameof(AccessType.VerifyAfterWrite), Kind(JsonValueKind.String, item, "accessType").GetString());
            Assert.Equal(records[i].StepName, Kind(JsonValueKind.String, item, "stepName").GetString());
            Assert.Equal(1, Kind(JsonValueKind.Number, item, "stepIndex").GetInt32());
            Assert.Equal(0, Kind(JsonValueKind.Number, item, "passIndex").GetInt32());
            Assert.Equal(0.00075, Kind(JsonValueKind.Number, item, "timeSinceWrite").GetDouble(), 9);
            Assert.Equal(72.0 + (i / 1000.0), Kind(JsonValueKind.Number, item, "timeIntoTest").GetDouble(), 9);
            foreach (string number in new[] { "workerIndex", "logicalProcessor", "processorGroup", "activeWorkers" })
            {
                Kind(JsonValueKind.Number, item, number);
            }
        }

        Assert.Equal("0x0000023A4F7C1D48", At(items[0], "address").GetString());
        Assert.Equal("0x0002000000000000", At(items[0], "bitMask").GetString());
        Kind(JsonValueKind.Null, json, "unfinishedRun");

        // A run without errors: no first-error time, and empty detail that is still labelled.
        JsonElement clean = passedDocument.RootElement;
        Assert.Equal("PASS", Kind(JsonValueKind.String, clean, "run", "verdict").GetString());
        Kind(JsonValueKind.Null, clean, "run", "firstErrorSeconds");
        Assert.Equal(0UL, Kind(JsonValueKind.Number, clean, "errors", "total").GetUInt64());
        Assert.Equal(0, Kind(JsonValueKind.Array, clean, "errors", "records").GetArrayLength());
        Assert.Equal(0, Kind(JsonValueKind.Array, clean, "errors", "groups").GetArrayLength());
        Assert.Equal(HintLabel, Kind(JsonValueKind.String, clean, "causeHints", "label").GetString());
        Assert.Equal(0, Kind(JsonValueKind.Array, clean, "causeHints", "items").GetArrayLength());
    }

    [Theory]
    [InlineData(3, 3UL, false)]
    [InlineData(3, 50UL, false)]
    [InlineData(ErrorGrouping.StoredDetailCap, (ulong)ErrorGrouping.StoredDetailCap, false)]
    [InlineData(ErrorGrouping.StoredDetailCap, 25_000UL, true)]
    public void SummaryJson_ErrorsAreStoredOnly_TrueTotalSeparate(int storedCount, ulong totalErrors, bool truncated)
    {
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(storedCount), TwoHints(), totalErrors);

        using JsonDocument document = JsonDocument.Parse(RunSummaryJson.Serialize(summary));
        JsonElement errors = At(document.RootElement, "errors");

        // The total is the engine's true count; everything stored* describes the stored records only.
        Assert.Equal(totalErrors, Kind(JsonValueKind.Number, errors, "total").GetUInt64());
        Assert.Equal(truncated, Kind(truncated ? JsonValueKind.True : JsonValueKind.False, errors, "truncated").GetBoolean());
        Assert.Equal(storedCount, Kind(JsonValueKind.Number, errors, "storedCount").GetInt32());
        Assert.Equal(storedCount, Kind(JsonValueKind.Array, errors, "records").GetArrayLength());
        Assert.Equal(ErrorGrouping.StoredDetailCap, Kind(JsonValueKind.Number, errors, "storedCap").GetInt32());
        Assert.Equal(storedCount, Kind(JsonValueKind.Number, errors, "storedDistinctAddresses").GetInt32());
        JsonElement groups = Kind(JsonValueKind.Array, errors, "groups");
        Assert.Equal(storedCount, groups.EnumerateArray().Sum(group => At(group, "count").GetInt32()));

        // The text channel keeps the same separation, in the UI's words once the cap is reached.
        string text = TextSummaryFormatter.Format(summary);
        string stored = "Stored detail: " + storedCount.ToString("N0", Invariant) + " of cap 10,000";
        Assert.Contains("Total errors (true count): " + totalErrors.ToString("N0", Invariant) + LineEnd, text, StringComparison.Ordinal);
        Assert.Contains(
            stored + (truncated ? " (truncated: total exceeds stored detail)" : string.Empty) + LineEnd,
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            LineEnd + (storedCount >= ErrorGrouping.StoredDetailCap ? "Grouped from the first 10,000 errors" : "Errors by address") + LineEnd,
            text,
            StringComparison.Ordinal);
        Assert.Contains("Stored error records (" + storedCount.ToString("N0", Invariant) + ")" + LineEnd, text, StringComparison.Ordinal);

        // The text renders the same data as the JSON: every stored group and every stored record, even at the cap.
        Assert.Equal(summary.Errors.Groups.Count, GroupRowCount(text, "virtual address"));
        Assert.Equal(storedCount, RecordHeaderCount(text));
        Assert.DoesNotContain("Showing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("beyond the stored-detail cap", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Hint label: the JSON and text channels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SummaryJson_CauseHintsLabel_IsHintNotDiagnosis()
    {
        SummaryCauseHints hints = TwoHints();
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(3), hints);
        string json = RunSummaryJson.Serialize(summary);

        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement causeHints = At(document.RootElement, "causeHints");
            string? label = Kind(JsonValueKind.String, causeHints, "label").GetString();
            Assert.Equal(HintLabel, label);
            Assert.Equal(SummaryCauseHints.HintLabel, label);
            Assert.Equal('\u2014', label![5]);

            JsonElement[] items = Kind(JsonValueKind.Array, causeHints, "items").EnumerateArray().ToArray();
            Assert.Equal(hints.Items.Count, items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                Assert.Equal(i + 1, Kind(JsonValueKind.Number, items[i], "rank").GetInt32());
                Assert.Equal(hints.Items[i].Category, Kind(JsonValueKind.String, items[i], "category").GetString());
                Assert.Equal(hints.Items[i].LikelihoodPercent, Kind(JsonValueKind.Number, items[i], "likelihoodPercent").GetDouble());
                Assert.Equal(hints.Items[i].Suggestion, Kind(JsonValueKind.String, items[i], "suggestion").GetString());
                Assert.Equal(hints.Items[i].Evidence, Kind(JsonValueKind.String, items[i], "evidence").GetString());
            }
        }

        // A document cannot relabel the hints: the label is a constant that reading ignores.
        JsonNode relabelled = JsonNode.Parse(json)!;
        relabelled["causeHints"]!["label"] = "Diagnosis: certain";
        Assert.True(RunSummaryJson.TryDeserialize(relabelled.ToJsonString(), out RunSummary? readBack));
        Assert.Equal(HintLabel, readBack.CauseHints.Label);
        Assert.Equal(summary.CauseHints, readBack.CauseHints);
        Assert.Equal(HintLabel, SummaryCauseHints.Empty.Label);
    }


    [Fact]
    public void TextSummary_WritesLabelAboveHints_EachItemPrefixedHint()
    {
        SummaryCauseHints hints = TwoHints();
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(3), hints);

        string text = TextSummaryFormatter.Format(summary);
        string[] lines = text.Split(LineEnd);

        int labelIndex = Array.IndexOf(lines, HintLabel);
        Assert.True(labelIndex >= 0, "The text summary has no hint label line.");
        Assert.Single(lines, line => line == SummaryCauseHints.HintLabel);
        Assert.Equal("Probable cause (hint)", lines[labelIndex + 1]);
        int firstHint = Array.FindIndex(lines, line => line.TrimStart().StartsWith("Hint:", StringComparison.Ordinal));
        Assert.True(firstHint > labelIndex, "A hint is written above its label.");

        string[] hintLines = lines.Where(line => line.TrimStart().StartsWith("Hint:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(hints.Items.Count, hintLines.Length);
        for (int i = 0; i < hintLines.Length; i++)
        {
            SummaryCauseHint item = hints.Items[i];
            string percent = item.LikelihoodPercent.ToString("0", Invariant);
            Assert.Equal(
                $"Hint: {item.Rank.ToString(Invariant)}. {item.Category} \u2014 {percent} % \u2014 {item.Suggestion}",
                hintLines[i]);
            Assert.Equal("    Evidence: " + item.Evidence, lines[Array.IndexOf(lines, hintLines[i]) + 1]);
        }

        // Nothing states a cause as certain.
        Assert.Contains(HintLabel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosis:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, line => CertaintyHeadingPattern().IsMatch(line));

        // Addresses are named by their kind, and re-read outcomes are inferences.
        Assert.Contains("virtual address 0x0000023A4F7C1D48", text, StringComparison.Ordinal);
        Assert.Contains(
            "expected 0x5A3C5A3C5A3C5A3C, actual 0x5A3E5A3C5A3C5A3C, re-read 0x5A3E5A3C5A3C5A3C "
                + "(repeated wrong value: likely write or storage fault)",
            text,
            StringComparison.Ordinal);

        // Rendering never reads the current culture.
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        string rendered;
        try
        {
            rendered = TextSummaryFormatter.Format(summary);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(text, rendered);
    }

    [Fact]
    public void TextSummary_PartialRun_LabelsTimesApproximate_AndRendersUnfinishedSection()
    {
        RunSummary summary = RunSummary.Build(UnfinishedSource(out DateTimeOffset beatUtc));

        Assert.True(summary.Run.Partial);
        Assert.True(summary.Run.Recovered);
        Assert.NotNull(summary.UnfinishedRun);

        string text = TextSummaryFormatter.Format(summary);
        const string Approximate = " (approximate, last heartbeat)";
        const string Position = "at step 2 of 6 (Random bank thrash)";

        Assert.Equal(JournalSamples.RunIdA, TextSummaryFormatter.TryParseRunId(text));
        AssertTextLines(
            text,
            "This summary was rebuilt from the progress journal at the next launch.",
            "This summary is partial: the test did not finish.",
            "Verdict: FAIL",
            "End reason: PreviousRunUnfinished",
            "Detail: The previous test did not finish " + Position + ", which likely indicates severe instability. "
                + "endedUtc and durationSeconds are approximate (last heartbeat).",
            "Started: " + Utc(_t0 + TimeSpan.FromMilliseconds(1_250)),
            "Ended: " + Utc(beatUtc) + Approximate,
            "Verdict shown: n/a",
            "Duration: 65.0 s" + Approximate,
            "Time to verdict: n/a",
            "First error: n/a",
            "Release time: not confirmed",
            "CPU max: 70.5 \u00B0C (worst status: Available)",
            "RAM max: N/A (worst status: DriverUnavailable)",
            "Total errors (true count): 0",
            "Error detail is not available for this run.",
            HintLabel,
            "No cause hints (no error detail).",
            "Unfinished run",
            "The previous test did not finish " + Position + ", which likely indicates severe instability.",
            "Last heartbeat: " + Utc(beatUtc),
            "Last coverage: 450.0 %");
        int unfinishedHeading = text.IndexOf(LineEnd + "Unfinished run" + LineEnd, StringComparison.Ordinal);
        Assert.True(
            unfinishedHeading > text.IndexOf(HintLabel, StringComparison.Ordinal),
            "The unfinished-run section must close the summary.");

        // The finished step reports its actuals; the step that was running shows none and is not completed.
        string[] stepRows = text.Split(LineEnd).Where(line => StepRowPattern().IsMatch(line)).ToArray();
        Assert.Equal(2, stepRows.Length);
        Assert.Contains("High-toggle sweep", stepRows[0], StringComparison.Ordinal);
        Assert.EndsWith("Yes", stepRows[0], StringComparison.Ordinal);
        Assert.Contains("Random bank thrash", stepRows[1], StringComparison.Ordinal);
        Assert.Contains("\u2014", stepRows[1], StringComparison.Ordinal);
        Assert.EndsWith("No", stepRows[1], StringComparison.Ordinal);

        // The same partial summary on the JSON channel.
        using JsonDocument document = JsonDocument.Parse(RunSummaryJson.Serialize(summary));
        JsonElement json = document.RootElement;
        Kind(JsonValueKind.True, json, "run", "partial");
        Kind(JsonValueKind.True, json, "run", "recovered");
        Kind(JsonValueKind.Null, json, "run", "verdictUtc");
        Kind(JsonValueKind.Null, json, "run", "firstErrorSeconds");
        Kind(JsonValueKind.Null, json, "memory", "releaseSeconds");
        Kind(JsonValueKind.False, json, "errors", "detailAvailable");
        Assert.Equal(0, Kind(JsonValueKind.Array, json, "errors", "records").GetArrayLength());
        Assert.Equal(HintLabel, Kind(JsonValueKind.String, json, "causeHints", "label").GetString());
        Assert.Equal(2, Kind(JsonValueKind.Number, json, "unfinishedRun", "lastStepIndex").GetInt32());
        Assert.Equal("Random bank thrash", Kind(JsonValueKind.String, json, "unfinishedRun", "lastStepName").GetString());
        Assert.Equal(6, Kind(JsonValueKind.Number, json, "unfinishedRun", "stepCount").GetInt32());
        Assert.Equal(Utc(beatUtc), Kind(JsonValueKind.String, json, "unfinishedRun", "lastHeartbeatUtc").GetString());
        Assert.Equal(450.0, Kind(JsonValueKind.Number, json, "unfinishedRun", "lastCoveragePercent").GetDouble());
    }

    [Fact]
    public void TextSummary_UnfinishedWhilePreparing_StatesBeforeStep1AndUnknownTimes()
    {
        RunSummary summary = RunSummary.Build(new RunSummarySource
        {
            Begin = JournalSamples.BeginA(_t0) with { Utc = _t0 },
            Recovered = true,
            Partial = true,
        });

        string text = TextSummaryFormatter.Format(summary);

        AssertTextLines(
            text,
            "Detail: The previous test did not finish while preparing memory (before step 1), which likely indicates "
                + "severe instability. No heartbeat was recorded: endedUtc and durationSeconds are unknown.",
            "Started: n/a",
            "Ended: n/a",
            "Duration: n/a",
            "Instruction set: AVX2",
            "Tested: n/a",
            "Page mode: n/a",
            "Address kind: n/a",
            "No step started.",
            "The previous test did not finish while preparing memory (before step 1), which likely indicates severe instability.",
            "Last heartbeat: n/a",
            "Last coverage: n/a");

        // A time the journal never captured is unknown, not approximate.
        Assert.DoesNotContain("approximate", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 error was detected before the test stopped.")]
    [InlineData(3, "3 errors were detected before the test stopped.")]
    public void TextSummary_AbortedWithErrors_StatesErrorsDetectedBeforeStop(int errorCount, string expectedLine)
    {
        RunSummarySource source = JournalSamples.CompletedRunSource(JournalSamples.ErrorRecords(errorCount), TwoHints());
        JournalResult completed = source.Result!;
        JournalResult aborted = JournalSamples.Result(
            RunState.Aborted,
            RunEndReason.UserStop,
            (ulong)errorCount,
            completed.VerifiedBytes,
            TimeSpan.FromMilliseconds(completed.ElapsedMs),
            TimeSpan.FromSeconds(72),
            completed.StepsCompleted,
            completed.CompletedUtc,
            completed.VerdictUtc,
            "Stopped by user");
        RunSummary summary = RunSummary.Build(source with { Result = aborted with { Utc = completed.Utc } });

        string text = TextSummaryFormatter.Format(summary);

        AssertTextLines(text, "Verdict: ABORTED", "End reason: UserStop", "Detail: Stopped by user", expectedLine, HintLabel);
        using JsonDocument document = JsonDocument.Parse(RunSummaryJson.Serialize(summary));
        Assert.Equal("ABORTED", Kind(JsonValueKind.String, document.RootElement, "run", "verdict").GetString());
        Assert.Equal(nameof(RunEndReason.UserStop), Kind(JsonValueKind.String, document.RootElement, "run", "endReason").GetString());
        Assert.Equal("Stopped by user", Kind(JsonValueKind.String, document.RootElement, "run", "endDetail").GetString());
        Assert.Equal((ulong)errorCount, Kind(JsonValueKind.Number, document.RootElement, "errors", "total").GetUInt64());
    }

    [Theory]
    [InlineData(InstructionSet.Sse2, "SSE2", true)]
    [InlineData(InstructionSet.Avx2, "AVX2", false)]
    [InlineData(InstructionSet.Avx512, "AVX-512", false)]
    public void Summary_SelectedInstructionSet_ReportedOnBothChannels(InstructionSet selected, string name, bool reduced)
    {
        const string ReducedNote = "Reduced instruction set (SSE2): this test runs slower";
        RunSummarySource source = JournalSamples.CompletedRunSource(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty);
        RunSummary summary = RunSummary.Build(
            source with { Prepared = source.Prepared! with { IsaSelected = selected, PageMode = PageMode.Mixed } });

        string text = TextSummaryFormatter.Format(summary);

        AssertTextLines(text, "Instruction set: " + name, "Page mode: Mixed", "Address kind: virtual address", "Verdict: PASS");
        Assert.Equal(
            reduced,
            text.Contains("Instruction set: SSE2" + LineEnd + ReducedNote + LineEnd, StringComparison.Ordinal));
        Assert.Equal(reduced, text.Contains(ReducedNote, StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(RunSummaryJson.Serialize(summary));
        Assert.Equal(name, Kind(JsonValueKind.String, document.RootElement, "system", "instructionSet").GetString());
        Kind(reduced ? JsonValueKind.True : JsonValueKind.False, document.RootElement, "system", "reducedInstructionSet");
        Assert.Equal("Mixed", Kind(JsonValueKind.String, document.RootElement, "memory", "pageMode").GetString());
    }

    [Fact]
    public void TextSummary_DamagedDocumentValues_RenderedOnOneLineAsWritten()
    {
        // Two flipped bits, and then values only a damaged document read back could hold: a line break and a
        // forged run id line in free text, and a bit mask that is not hex.
        ErrorRecord twoBits = JournalSamples.ErrorRecords(1)[0] with { Actual = JournalSamples.FirstErrorExpected ^ 0x88 };
        RunSummary summary = CompletedSummary(new[] { twoBits }, TwoHints());
        string forged = TextSummaryFormatter.RunIdLinePrefix + JournalSamples.RunIdB.ToString("N", Invariant);
        SummaryErrorGroup group = Assert.Single(summary.Errors.Groups);
        RunSummary damaged = summary with
        {
            Run = summary.Run with { EndDetail = "Engine error\r\n" + forged + "\u2028end" },
            Errors = summary.Errors with { Groups = new[] { group, group with { BitMaskUnion = "not hex" } } },
        };

        string text = TextSummaryFormatter.Format(damaged);

        Assert.Equal(JournalSamples.RunIdA, TextSummaryFormatter.TryParseRunId(text));
        Assert.Single(
            text.Split(LineEnd),
            line => line.StartsWith(TextSummaryFormatter.RunIdLinePrefix, StringComparison.Ordinal));
        AssertTextLines(
            text,
            "Detail: Engine error  " + forged + " end",
            "  virtual address 0x0000023A4F7C1D48  1 error  bits 3, 7  first step 'Random bank thrash'"
                + "  first error at 72.000 s  last error at 72.000 s",
            "  virtual address 0x0000023A4F7C1D48  1 error  bits not hex  first step 'Random bank thrash'"
                + "  first error at 72.000 s  last error at 72.000 s",
            "  1. Step 'Random bank thrash' at 72.000 s \u2014 virtual address 0x0000023A4F7C1D48 \u2014 bits 3, 7");
    }

    [Fact]
    public void TextSummary_StoredDetail_RendersPrimaryMechanismGroupTimesAndEveryRecordField()
    {
        // Two records at one word: the AAP's example error, then one whose every other stored value differs.
        ErrorRecord first = JournalSamples.ErrorRecords(1)[0];
        ErrorRecord second = first with
        {
            Sequence = 41,
            PassIndex = 3,
            AccessType = AccessType.TurnaroundRead,
            TimeSinceWrite = TimeSpan.FromMicroseconds(2_500_125),
            TimeIntoTest = TimeSpan.FromSeconds(80.25),
            WorkerIndex = 70,
            LogicalProcessor = 6,
            ProcessorGroup = 1,
            ActiveWorkers = 128,
        };
        RunSummary summary = CompletedSummary(new[] { first, second }, TwoHints());

        string text = TextSummaryFormatter.Format(summary);

        AssertTextLines(
            text,
            "  virtual address 0x0000023A4F7C1D48  2 errors  bits 49  first step 'Random bank thrash'"
                + "  first error at 72.000 s  last error at 80.250 s",
            "Stored error records (2)",
            "  1. Step 'Random bank thrash' at 72.000 s \u2014 virtual address 0x0000023A4F7C1D48 \u2014 bits 49",
            "     expected 0x5A3C5A3C5A3C5A3C, actual 0x5A3E5A3C5A3C5A3C, re-read 0x5A3E5A3C5A3C5A3C "
                + "(repeated wrong value: likely write or storage fault)",
            "     bit mask 0x0002000000000000, sequence 0, region offset 209,460,552 bytes, step index 1, pass index 0",
            "     access type VerifyAfterWrite, time since write 0.00075 s, worker index 0, processor group 0, "
                + "logical processor 0, active workers 7",
            "  2. Step 'Random bank thrash' at 80.250 s \u2014 virtual address 0x0000023A4F7C1D48 \u2014 bits 49",
            "     bit mask 0x0002000000000000, sequence 41, region offset 209,460,552 bytes, step index 1, pass index 3",
            "     access type TurnaroundRead, time since write 2.500125 s, worker index 70, processor group 1, "
                + "logical processor 6, active workers 128");

        // Every step row carries its primary mechanism, in the JSON's order: after the hold, before the budget.
        string[] lines = text.Split(LineEnd);
        string header = Assert.Single(lines, line => line.StartsWith("#  Name", StringComparison.Ordinal));
        int column = header.IndexOf("Primary mechanism", StringComparison.Ordinal);
        Assert.InRange(
            column,
            header.IndexOf("Hold s", StringComparison.Ordinal) + 1,
            header.IndexOf("Planned s", StringComparison.Ordinal) - 1);
        string[] stepRows = lines.Where(line => StepRowPattern().IsMatch(line)).ToArray();
        Assert.Equal(summary.Steps.Count, stepRows.Length);
        for (int i = 0; i < stepRows.Length; i++)
        {
            string mechanism = summary.Steps[i].PrimaryMechanism;
            Assert.False(string.IsNullOrEmpty(mechanism), "The sample step has no primary mechanism.");
            Assert.StartsWith(mechanism + " ", stepRows[i][column..], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Virtual", "virtual address")]
    [InlineData(null, "virtual address")]
    [InlineData("", "virtual address")]
    [InlineData(" \r\n ", "virtual address")]
    [InlineData("Physical", "physical address")]
    public void TextSummary_GroupRows_NameTheSummaryAddressKind(string? addressKind, string expectedLabel)
    {
        // The schema gives a group's word address no kind of its own: it takes the summary's memory address kind,
        // and virtual, the only v1 kind, when the document did not capture one.
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(3), TwoHints());
        RunSummary rendered = summary with { Memory = summary.Memory with { AddressKind = addressKind } };

        string[] lines = TextSummaryFormatter.Format(rendered).Split(LineEnd);

        int header = Array.IndexOf(lines, "Errors by address");
        Assert.True(header >= 0, "The text summary has no group list.");
        string[] rows = lines.Skip(header + 1).TakeWhile(line => line.Length > 0).ToArray();
        Assert.Equal(summary.Errors.Groups.Count, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.StartsWith(
                "  " + expectedLabel + " " + summary.Errors.Groups[i].WordAddress + "  ",
                rows[i],
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, "1 more group is", "1 more record is")]
    [InlineData(2, "2 more groups are", "2 more records are")]
    public void TextSummary_DamagedDocumentBeyondCap_ListsCapAndCountsTheRest(int extra, string groupsLeft, string recordsLeft)
    {
        // The engine stores at most the cap, so only a damaged or foreign document read back holds more.
        RunSummary summary = CompletedSummary(JournalSamples.ErrorRecords(ErrorGrouping.StoredDetailCap), TwoHints(), 25_000UL);
        SummaryErrors errors = summary.Errors;
        RunSummary damaged = summary with
        {
            Errors = errors with
            {
                Groups = errors.Groups.Concat(Enumerable.Repeat(errors.Groups[^1], extra)).ToArray(),
                Records = errors.Records.Concat(Enumerable.Repeat(errors.Records[^1], extra)).ToArray(),
            },
        };

        string text = TextSummaryFormatter.Format(damaged);

        const string NotListed = " beyond the stored-detail cap of 10,000 and not listed.";
        AssertTextLines(
            text,
            "Grouped from the first 10,000 errors",
            "  " + groupsLeft + NotListed,
            "Stored error records (" + (ErrorGrouping.StoredDetailCap + extra).ToString("N0", Invariant) + ")",
            "  " + recordsLeft + NotListed);
        Assert.Equal(ErrorGrouping.StoredDetailCap, GroupRowCount(text, "virtual address"));
        Assert.Equal(ErrorGrouping.StoredDetailCap, RecordHeaderCount(text));
        Assert.Contains(LineEnd + "  10000. Step '", text, StringComparison.Ordinal);
        Assert.DoesNotContain(LineEnd + "  10001. Step '", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RAMQuick test summary\r\n=====================\r\nApp: RAMQuick 1.0.0\r\n")]
    [InlineData("Run ID: not-a-run-id\r\n")]
    [InlineData("Run ID: 3f2504e0-4f89-41d3-9a0c-0305e82c3301\r\n")]
    [InlineData("run id: 3f2504e04f8941d39a0c0305e82c3301\r\n")]
    [InlineData("  Run ID: 3f2504e04f8941d39a0c0305e82c3301\r\n")]
    [InlineData("Run ID: \r\nRun ID: 3f2504e04f8941d39a0c0305e82c3301\r\n")]
    [InlineData("{\"run\":{\"id\":\"3f2504e04f8941d39a0c0305e82c3301\"}}")]
    public void TextSummary_TryParseRunId_RejectsForeignText(string text)
    {
        Assert.Null(TextSummaryFormatter.TryParseRunId(text));
    }

    [Theory]
    [InlineData("Run ID: 3f2504e04f8941d39a0c0305e82c3301")]
    [InlineData("Title\nRun ID:   3F2504E04F8941D39A0C0305E82C3301  \nRun ID: 9b2d7c1a6e4f4a8b8c3d5f6e7a8b9c0d\n")]
    public void TextSummary_TryParseRunId_ReadsFirstRunIdLine(string text)
    {
        Assert.Equal(JournalSamples.RunIdA, TextSummaryFormatter.TryParseRunId(text));
    }

    [Fact]
    public void TextSummary_NullArguments_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TextSummaryFormatter.Format(null!));
        Assert.Throws<ArgumentNullException>(() => TextSummaryFormatter.TryParseRunId(null!));
        Assert.Throws<ArgumentNullException>(() => RunSummaryJson.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => SummaryFormat.RedactPersonalPaths(null!));
    }

    [Fact]
    public void SummaryFormat_Hex64RunIdAndIsaNames_UseFixedFormats()
    {
        Assert.Equal("0x0000023A4F7C1D48", SummaryFormat.Hex64(JournalSamples.FirstErrorAddress));
        Assert.Equal("0x0000000000000000", SummaryFormat.Hex64(0));
        Assert.Equal("0xFFFFFFFFFFFFFFFF", SummaryFormat.Hex64(ulong.MaxValue));
        Assert.Equal("3f2504e04f8941d39a0c0305e82c3301", SummaryFormat.RunId(JournalSamples.RunIdA));
        Assert.Equal("SSE2", SummaryFormat.InstructionSetName(InstructionSet.Sse2));
        Assert.Equal("AVX2", SummaryFormat.InstructionSetName(InstructionSet.Avx2));
        Assert.Equal("AVX-512", SummaryFormat.InstructionSetName(InstructionSet.Avx512));
        Assert.Throws<ArgumentOutOfRangeException>(() => SummaryFormat.InstructionSetName((InstructionSet)9));
    }

    [Theory]
    [InlineData("0x0000023A4F7C1D48", true, 0x0000023A4F7C1D48UL)]
    [InlineData("0X0000023a4f7c1d48", true, 0x0000023A4F7C1D48UL)]
    [InlineData("FF", true, 0xFFUL)]
    [InlineData("0xFFFFFFFFFFFFFFFF", true, ulong.MaxValue)]
    [InlineData("0x1FFFFFFFFFFFFFFFF", false, 0UL)]
    [InlineData("0x", false, 0UL)]
    [InlineData("0x12 34", false, 0UL)]
    [InlineData("not hex", false, 0UL)]
    [InlineData(null, false, 0UL)]
    public void SummaryFormat_TryParseHex64_AcceptsOnlyHexWords(string? text, bool parsed, ulong expected)
    {
        Assert.Equal(parsed, SummaryFormat.TryParseHex64(text, out ulong value));
        Assert.Equal(expected, value);
    }

    // Each case pairs a text with its redaction. The environment's folders and names are read at run time, so the
    // cases hold this host's personal values and prove that none of them survives.
    public static TheoryData<string, string> PersonalPathCases()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userName = Environment.UserName;
        string machineName = Environment.MachineName;
        return new TheoryData<string, string>
        {
            { profile + @"\Documents\RamQuick\data\journal\active-run.jsonl", @"%USERPROFILE%\Documents\RamQuick\data\journal\active-run.jsonl" },
            { "Could not find file '" + profile + @"\x.log'.", @"Could not find file '%USERPROFILE%\x.log'." },
            { profile.ToUpperInvariant().Replace('\\', '/') + "/notes.txt", "%USERPROFILE%/notes.txt" },
            { localAppData + @"\RamQuick\logs\app-errors.log", @"%LOCALAPPDATA%\RamQuick\logs\app-errors.log" },
            { roamingAppData + @"\RamQuick\settings", @"%APPDATA%\RamQuick\settings" },
            { Path.GetTempPath() + "RamQuick.tmp", @"%TEMP%\RamQuick.tmp" },
            { AppContext.BaseDirectory + "ramquick_engine.dll", @"<app folder>\ramquick_engine.dll" },
            { @"Access to the path 'D:\Users\alice\Desktop\notes.txt' is denied.", @"Access to the path 'D:\Users\<user>\Desktop\notes.txt' is denied." },
            { "Could not find 'e:/USERS/alice.contoso/AppData/x.json'.", "Could not find 'e:/USERS/<user>/AppData/x.json'." },
            { @"The journal is in C:\Users\alice.", @"The journal is in C:\Users\<user>." },
            { @"C:\Users\Alice Smith\Downloads\RamQuick\ramquick_engine.dll", @"C:\Users\<user>\Downloads\RamQuick\ramquick_engine.dll" },
            { @"C:\Users\O'Connor\Downloads\RamQuick\ramquick_engine.dll", @"C:\Users\<user>\Downloads\RamQuick\ramquick_engine.dll" },
            { @"C:\Users\j.doe [corp]\x", @"C:\Users\<user>\x" },
            { @"'C:\Users\O'Connor' is denied.", @"'C:\Users\<user>' is denied." },
            { @"""C:\Users\Alice Smith""", @"""C:\Users\<user>""" },
            { @"Access to 'C:\Users\Alice Smith'.", @"Access to 'C:\Users\<user>'." },
            { @"C:\Users\Alice Smith", @"C:\Users\<user>" },
            { "Missing C:\\Users\\Alice Smith\r\n   at RamQuick.Load()", "Missing C:\\Users\\<user>\r\n   at RamQuick.Load()" },
            { @"Copy C:\Users\Alice Smith to D:\backup failed.", @"Copy C:\Users\<user> D:\backup failed." },
            { @"Copy C:\Users\Alice Smith to 'C:\Users\O'Connor'.", @"Copy C:\Users\<user> 'C:\Users\<user>'." },
            { @"\\fileserver\home\" + userName + @"\RamQuick", @"\\fileserver\home\<user>\RamQuick" },
            { "Host " + machineName + " refused the request", "Host <machine> refused the request" },
            { @"\\" + machineName.ToLowerInvariant() + @"\share\x", @"\\<machine>\share\x" },
            { "Host " + machineName + "X refused the request", "Host " + machineName + "X refused the request" },
            { @"D:\Data\Users\alice\x", @"D:\Data\Users\alice\x" },
            { @"C:\Users\", @"C:\Users\" },
            { "Stop requested by host: host fault", "Stop requested by host: host fault" },
            { "Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12", "Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12" },
            { string.Empty, string.Empty },
        };
    }

    // Texts no exception message is expected to hold, each repeated the given number of times; the redaction must
    // survive every one of them.
    public static TheoryData<string, int> AdversarialTexts() => new()
    {
        { "\uD800 unpaired surrogate \uDC00", 1 },
        { "C:", 1 },
        { @"C:\Users", 1 },
        { @"\\", 1 },
        { "/", 1 },
        { "<user> <machine> %USERPROFILE% <app folder>", 1 },
        { "\0\r\n\t" + Environment.UserName + "\u2028" + Environment.MachineName, 1 },
        { @"C:\Users\" + Environment.UserName + @"\", 10_000 },
    };

    [Theory]
    [MemberData(nameof(PersonalPathCases))]
    public void SummaryFormat_RedactPersonalPaths_ReplacesOnlyPersonalPartsAndIsStable(string text, string expected)
    {
        string redacted = SummaryFormat.RedactPersonalPaths(text);

        Assert.Equal(expected, redacted);
        Assert.Equal(expected, SummaryFormat.RedactPersonalPaths(redacted));
    }

    [Theory]
    [MemberData(nameof(AdversarialTexts))]
    public void SummaryFormat_RedactPersonalPaths_AnyText_NeverThrowsAndIsStable(string fragment, int repetitions)
    {
        string text = string.Concat(Enumerable.Repeat(fragment, repetitions));

        string redacted = SummaryFormat.RedactPersonalPaths(text);

        Assert.Equal(redacted, SummaryFormat.RedactPersonalPaths(redacted));
        Assert.DoesNotContain(@"\" + Environment.UserName + @"\", redacted, StringComparison.OrdinalIgnoreCase);
    }

    // A computer may be named like a word of a placeholder, of the summaries or of the results log; the computer-name
    // rule still replaces the name wherever it stands as a word, and leaves every placeholder as it stands.
    [Theory]
    [InlineData("DATA")]
    [InlineData("TEMP")]
    [InlineData("APPDATA")]
    [InlineData("LOCALAPPDATA")]
    [InlineData("USERPROFILE")]
    [InlineData("USERNAME")]
    [InlineData("APP")]
    [InlineData("FOLDER")]
    [InlineData("USER")]
    [InlineData("MACHINE")]
    public void SummaryFormat_RedactPersonalPaths_ComputerNamedLikePlaceholderWord_LeavesEveryPlaceholder(string machineName)
    {
        const string Placeholders = @"<data>\journal <app folder>\x C:\Users\<user>\x \\<machine>\share %USERPROFILE%\x "
            + @"%LOCALAPPDATA%\x %APPDATA%\x %TEMP%\x D:\Users\%USERNAME%\x";
        var snapshot = new SummaryFormat.PersonalPathSnapshot(Array.Empty<(string Folder, string Token)>(), null, machineName);

        string redacted = SummaryFormat.RedactPersonalPaths(
            Placeholders + " on " + machineName.ToLowerInvariant() + @", \\" + machineName + @"\share",
            snapshot);

        Assert.Equal(Placeholders + @" on <machine>, \\<machine>\share", redacted);
        Assert.Equal(redacted, SummaryFormat.RedactPersonalPaths(redacted, snapshot));
    }

    // A result created by the controller is redacted by JournalResult.Create; one read back from a journal an
    // earlier build wrote unredacted is redacted by RunSummary.Build. Either way neither summary names the account.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EndDetail_PathInExceptionMessage_PersistedRedactedInResultAndBothSummariesAsync(bool throughCreate)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string detail = "Could not find file '" + profile + @"\Documents\RamQuick\data\journal\active-run.jsonl' or 'C:\Users\alice\x.log'.";
        string expected = @"Could not find file '%USERPROFILE%\Documents\RamQuick\data\journal\active-run.jsonl' or 'C:\Users\<user>\x.log'.";
        using var root = new TestDataRoot();
        using ResultsLog log = Open(root, CreateTime(), new ScriptedRunIds(JournalSamples.RunIdA));
        RunSummarySource source = JournalSamples.CompletedRunSource(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty);
        JournalResult completed = source.Result!;
        JournalResult created = JournalSamples.Result(
            RunState.Aborted,
            RunEndReason.EngineFault,
            completed.TotalErrors,
            completed.VerifiedBytes,
            TimeSpan.FromMilliseconds(completed.ElapsedMs),
            null,
            completed.StepsCompleted,
            completed.CompletedUtc,
            completed.VerdictUtc,
            detail);
        JournalResult result = throughCreate
            ? created with { Utc = completed.Utc }
            : created with { Utc = completed.Utc, EndDetail = detail };
        Assert.Equal(throughCreate ? expected : detail, result.EndDetail);

        RunSummary summary = RunSummary.Build(source with { Result = result });
        using JsonDocument document = await PublishAndParseAsync(log, summary);
        string text = await File.ReadAllTextAsync(log.SummaryTextPath, Cancellation);

        Assert.Equal(expected, summary.Run.EndDetail);
        Assert.Equal(expected, Kind(JsonValueKind.String, document.RootElement, "run", "endDetail").GetString());
        AssertTextLines(text, "Detail: " + expected);
        string json = await File.ReadAllTextAsync(log.SummaryJsonPath, Cancellation);
        foreach (string published in new[] { json, text })
        {
            Assert.DoesNotContain(profile, published, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(JsonEncodedText.Encode(profile).ToString(), published, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice", published, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null, RunSummarySource.DefaultAppName)]
    [InlineData("   ", RunSummarySource.DefaultAppName)]
    [InlineData("RAMQuick Portable", "RAMQuick Portable")]
    public void RunSummaryBuild_AppName_BlankFallsBackToProductName(string? appName, string expected)
    {
        RunSummarySource completed = JournalSamples.CompletedRunSource(Array.Empty<ErrorRecord>(), SummaryCauseHints.Empty);

        RunSummary summary = RunSummary.Build(completed with { AppName = appName! });

        Assert.Equal(expected, summary.App.Name);
        AssertTextLines(TextSummaryFormatter.Format(summary), "App: " + expected + " " + JournalSamples.AppVersion);
    }

    // ---------------------------------------------------------------------------------------------
    // app-errors.log
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AppendAppError_AppendsEntries_PreservesExistingAsync()
    {
        using var root = new TestDataRoot();
        Directory.Delete(root.Data.LogsDirectory, recursive: true);
        DateTimeOffset secondUtc = _t0 + TimeSpan.FromSeconds(2);

        ResultsLog.AppendAppError(root.Data, "EngineCallback:on_progress", new InvalidOperationException("First callback failure"), _t0);
        string first = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        ResultsLog.AppendAppError(root.Data, "DispatcherUnhandledException", new IOException("Second \"failure\""), secondUtc);
        byte[] bytes = await File.ReadAllBytesAsync(root.Data.AppErrorsLogPath, Cancellation);
        string both = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith(_utf8Bom), "app-errors.log starts with a byte-order mark.");
        Assert.StartsWith(first, both, StringComparison.Ordinal);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"EngineCallback:on_progress\"" + LineEnd
                + "  System.InvalidOperationException: First callback failure" + LineEnd + LineEnd,
            first);
        Assert.Equal(
            Utc(secondUtc) + " APP_ERROR source=\"DispatcherUnhandledException\"" + LineEnd
                + "  System.IO.IOException: Second \"failure\"" + LineEnd + LineEnd,
            both[first.Length..]);
        Assert.Equal(2, both.Split(LineEnd).Count(line => EventOf(line) == "APP_ERROR"));
    }

    [Fact]
    public async Task QueueAppError_WritesOnThreadPool_AppendsEntryAsync()
    {
        using var root = new TestDataRoot();
        DateTimeOffset queuedUtc = _t0 + TimeSpan.FromSeconds(5);
        ResultsLog.AppendAppError(root.Data, "EngineCallback:on_progress", new InvalidOperationException("Earlier failure"), _t0);
        string first = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);

        ResultsLog.QueueAppError(root.Data, "EngineCallback:on_step_finished", new InvalidOperationException("Queued failure"), queuedUtc);

        // Fire-and-forget by contract: there is no drain handle, so the test waits for the block to land.
        string both = await WaitForFileContentAsync(root.Data.AppErrorsLogPath, "Queued failure");
        Assert.StartsWith(first, both, StringComparison.Ordinal);
        Assert.Equal(
            Utc(queuedUtc) + " APP_ERROR source=\"EngineCallback:on_step_finished\"" + LineEnd
                + "  System.InvalidOperationException: Queued failure" + LineEnd + LineEnd,
            both[first.Length..]);
    }

    [Fact]
    public async Task AppendAppError_ExceptionTextUnformattable_WritesTypeNamesAsync()
    {
        using var root = new TestDataRoot();

        ResultsLog.AppendAppError(root.Data, "EngineCallback:on_errors", new UnformattableException(), _t0);

        string text = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"EngineCallback:on_errors\"" + LineEnd
                + "  " + typeof(UnformattableException).FullName
                + " (its text could not be formatted: System.InvalidOperationException)" + LineEnd + LineEnd,
            text);
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111", " run_id=11111111111141118111111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000", "")]
    [InlineData(null, "")]
    public async Task AppendAppError_RunInProgress_HeaderCarriesItsRunIdAsync(string? runId, string expectedField)
    {
        using var root = new TestDataRoot();
        Guid? id = runId is null ? null : Guid.Parse(runId, Invariant);

        ResultsLog.AppendAppError(root.Data, "EngineCallback:on_progress", new InvalidOperationException("Sink failed"), _t0, id);

        string text = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"EngineCallback:on_progress\"" + expectedField + LineEnd
                + "  System.InvalidOperationException: Sink failed" + LineEnd + LineEnd,
            text);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    public async Task AppendAppError_MultiLineMessage_OnlyTheHeaderStartsAtTheMarginAsync(string lineBreak)
    {
        using var root = new TestDataRoot();
        const string Forged = "2030-01-01T00:00:00.000Z APP_ERROR source=\"Forged\"";
        var exception = new InvalidOperationException("first line" + lineBreak + Forged + lineBreak + lineBreak + "last line");

        ResultsLog.AppendAppError(root.Data, "QaEdge.Forged", exception, _t0);

        // Every line of the text is indented, an empty one included, so a blank line still ends only the entry.
        string text = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"QaEdge.Forged\"" + LineEnd
                + "  System.InvalidOperationException: first line" + LineEnd
                + "  " + Forged + LineEnd
                + "  " + LineEnd
                + "  last line" + LineEnd + LineEnd,
            text);
        Assert.Single(text.Split(LineEnd), line => EventOf(line) == "APP_ERROR");
    }

    [Theory]
    [InlineData(AccountLocation.DataFolder)]
    [InlineData(AccountLocation.LocalAppData)]
    [InlineData(AccountLocation.RoamingAppData)]
    [InlineData(AccountLocation.UserProfile)]
    [InlineData(AccountLocation.UserNameSegment)]
    [InlineData(AccountLocation.ProfileSibling)]
    [InlineData(AccountLocation.ForeignProfile)]
    [InlineData(AccountLocation.ComputerName)]
    [InlineData(AccountLocation.Placeholders)]
    public async Task AppendAppError_AccountLocationsInExceptionText_WrittenAsPlaceholdersAsync(AccountLocation location)
    {
        using var root = new TestDataRoot();
        (string path, string expectedPath) = AccountLocationText(location, root);

        var exception = new IOException($"Could not find a part of the path '{path}'.");

        ResultsLog.AppendAppError(root.Data, "UnhandledException", exception, _t0);

        string text = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"UnhandledException\"" + LineEnd
                + $"  System.IO.IOException: Could not find a part of the path '{expectedPath}'." + LineEnd + LineEnd,
            text);
    }

    [Fact]
    public void AppendAppError_LocationUnwritable_SwallowsFailure()
    {
        using var root = new TestDataRoot();
        root.CreateBlockingFile("logs");

        Exception? thrown = Record.Exception(
            () => ResultsLog.AppendAppError(root.Data, "DispatcherUnhandledException", new InvalidOperationException("Lost"), _t0));

        Assert.Null(thrown);
        Assert.False(File.Exists(root.Data.AppErrorsLogPath));
    }

    [Theory]
    [InlineData(false, null, "")]
    [InlineData(true, null, "")]
    [InlineData(false, "11111111-1111-4111-8111-111111111111", " run_id=11111111111141118111111111111111")]
    public async Task TryAppendAppError_HealthyFolder_WritesBlockAndReturnsTrueAsync(bool infinite, string? runId, string expectedField)
    {
        using var root = new TestDataRoot();
        TimeSpan timeout = infinite ? Timeout.InfiniteTimeSpan : _ceiling;
        Guid? id = runId is null ? null : Guid.Parse(runId, Invariant);

        bool written = ResultsLog.TryAppendAppError(
            root.Data, "App.OnExit", new InvalidOperationException("Exit failure"), _t0, timeout, id);

        // The wait covers the flush, so the block is already on disk when the call returns.
        string text = await File.ReadAllTextAsync(root.Data.AppErrorsLogPath, Cancellation);
        Assert.True(written);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"App.OnExit\"" + expectedField + LineEnd
                + "  System.InvalidOperationException: Exit failure" + LineEnd + LineEnd,
            text);
    }

    [Fact]
    public void TryAppendAppError_LocationUnwritable_ReturnsFalseWithoutThrowing()
    {
        using var root = new TestDataRoot();
        root.CreateBlockingFile("logs");
        bool written = true;

        Exception? thrown = Record.Exception(
            () => written = ResultsLog.TryAppendAppError(root.Data, "App.OnExit", new InvalidOperationException("Lost"), _t0, _ceiling));

        Assert.Null(thrown);
        Assert.False(written);
        Assert.False(File.Exists(root.Data.AppErrorsLogPath));
    }

    [Fact]
    public async Task TryAppendAppError_WriteOutlastsBound_ReturnsFalseAndWriteFinishesAloneAsync()
    {
        using var root = new TestDataRoot();
        using var release = new ManualResetEventSlim(initialState: false);
        var stalled = new StallingException(release);
        TimeSpan bound = TimeSpan.FromMilliseconds(200);

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool written = ResultsLog.TryAppendAppError(root.Data, "App.OnExit", stalled, _t0, bound);
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        release.Set();

        Assert.False(written);
        Assert.True(elapsed < _ceiling, $"The bounded write returned after {elapsed.TotalMilliseconds:0} ms.");
        string text = await WaitForFileContentAsync(root.Data.AppErrorsLogPath, StallingException.Text);
        Assert.Equal(
            Utc(_t0) + " APP_ERROR source=\"App.OnExit\"" + LineEnd + "  " + StallingException.Text + LineEnd + LineEnd,
            text);
    }

    [Theory]
    [InlineData(-2L)]
    [InlineData(-1_000L)]
    [InlineData((long)int.MaxValue + 1)]
    public void TryAppendAppError_TimeoutOutOfRange_ThrowsAndWritesNothing(long timeoutMs)
    {
        using var root = new TestDataRoot();

        Assert.Throws<ArgumentOutOfRangeException>(
            "timeout",
            () => ResultsLog.TryAppendAppError(
                root.Data, "Source", new InvalidOperationException("Unused"), _t0, TimeSpan.FromMilliseconds(timeoutMs)));

        Assert.False(File.Exists(root.Data.AppErrorsLogPath));
    }

    [Fact]
    public void AppErrors_InvalidArguments_ThrowAndWriteNothing()
    {
        using var root = new TestDataRoot();
        var exception = new InvalidOperationException("Unused");

        Assert.Throws<ArgumentNullException>(() => ResultsLog.AppendAppError(null!, "Source", exception, _t0));
        Assert.Throws<ArgumentException>(() => ResultsLog.AppendAppError(root.Data, " ", exception, _t0));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.AppendAppError(root.Data, "Source", null!, _t0));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.QueueAppError(null!, "Source", exception, _t0));
        Assert.Throws<ArgumentException>(() => ResultsLog.QueueAppError(root.Data, string.Empty, exception, _t0));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.QueueAppError(root.Data, "Source", null!, _t0));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.TryAppendAppError(null!, "Source", exception, _t0, _ceiling));
        Assert.Throws<ArgumentException>(() => ResultsLog.TryAppendAppError(root.Data, "\t", exception, _t0, _ceiling));
        Assert.Throws<ArgumentNullException>(() => ResultsLog.TryAppendAppError(root.Data, "Source", null!, _t0, _ceiling));

        Assert.False(File.Exists(root.Data.AppErrorsLogPath));
    }


    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static FakeTimeProvider CreateTime()
    {
        FakeTimeProvider time = JournalSamples.CreateTimeProvider();
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    private static ResultsLog Open(TestDataRoot root, FakeTimeProvider time, ScriptedRunIds runIds)
    {
        ResultsLog? log = ResultsLog.OpenRun(root.Data, Quick, _t0, time, runIds.Next);
        Assert.NotNull(log);
        return log;
    }

    // Logs message through the event, and returns the event, the key of the field that carries it and the text that
    // precedes it in that field.
    private static (string EventName, string Key, string ValuePrefix) LogFreeText(
        ResultsLog log,
        FreeTextEvent freeTextEvent,
        string message)
    {
        switch (freeTextEvent)
        {
            case FreeTextEvent.JournalFailure:
                log.LogJournalFailure(message);
                return ("JOURNAL_FAILURE", "message", string.Empty);
            case FreeTextEvent.Notice:
                log.LogNotice(message);
                return ("NOTICE", "message", string.Empty);
            case FreeTextEvent.CallbackFault:
                log.LogCallbackFault(_t0, "EngineCallback:on_progress", new IOException(message));
                return ("CALLBACK_FAULT", "error", "System.IO.IOException: ");
            default:
                throw new ArgumentOutOfRangeException(nameof(freeTextEvent), freeTextEvent, "Not a free-text event of these tests.");
        }
    }

    // A path naming the account, built from the folders and name Windows reports for it, and the text the logs write
    // in its place. A folder Windows does not report, or reports as a bare volume root, and a name that cannot be one
    // path segment, name no account, so the logs write the path as it stands: every host checks either the
    // placeholder or that fallback, and none skips. Only the text is used: nothing is ever written under the profile.
    private static (string Path, string Expected) AccountLocationText(AccountLocation location, TestDataRoot root)
    {
        switch (location)
        {
            case AccountLocation.DataFolder:
                return (
                    Path.Combine(root.Data.RootPath, "journal", DataDirectory.JournalFileName),
                    @"<data>\journal\" + DataDirectory.JournalFileName);
            case AccountLocation.LocalAppData:
                (string localAppData, bool localAppDataRedacted) = KnownFolder(Environment.SpecialFolder.LocalApplicationData);
                string journal = Path.Combine(localAppData, "RamQuick", "journal", "active-run.jsonl");
                return (journal, localAppDataRedacted ? @"%LOCALAPPDATA%\RamQuick\journal\active-run.jsonl" : journal);
            case AccountLocation.RoamingAppData:
                (string roamingAppData, bool roamingAppDataRedacted) = KnownFolder(Environment.SpecialFolder.ApplicationData);
                string notes = Path.Combine(roamingAppData, "RamQuick", "notes.txt");
                return (notes, roamingAppDataRedacted ? @"%APPDATA%\RamQuick\notes.txt" : notes);
            case AccountLocation.UserProfile:
                // Case and separator style do not matter, and a trailing separator stays.
                (string profile, bool profileRedacted) = KnownFolder(Environment.SpecialFolder.UserProfile);
                string desktop = profile.ToUpperInvariant().Replace('\\', '/') + "/Desktop/";
                return (desktop, profileRedacted ? "%USERPROFILE%/Desktop/" : desktop);
            case AccountLocation.UserNameSegment:
                string userName = Environment.UserName;
                string share = @"\\fileserver\home\" + userName + @"\RamQuick";
                bool userNameRedacted = !string.IsNullOrWhiteSpace(userName) && userName.AsSpan().IndexOfAny('\\', '/') < 0;
                return (share, userNameRedacted ? @"\\fileserver\home\%USERNAME%\RamQuick" : share);
            case AccountLocation.ProfileSibling:
                // A folder whose name merely starts with the profile's is another folder, not the profile. Under
                // <drive>:\Users\ it is another account's profile folder, written <user>; elsewhere it names no
                // account and is written as it is.
                string sibling = KnownFolder(Environment.SpecialFolder.UserProfile).Folder + "-archive-7f3c";
                return (
                    sibling,
                    ProfileFolderPattern().IsMatch(sibling)
                        ? sibling[..(sibling.AsSpan().LastIndexOfAny('\\', '/') + 1)] + "<user>"
                        : sibling);
            case AccountLocation.ForeignProfile:
                // Another account's profile, such as the Downloads folder of a standard user whose session elevated
                // the app with an administrator's credentials: its folder is named after that account.
                return (
                    @"C:\Users\OtherAccount\Downloads\RamQuick\ramquick_engine.dll",
                    @"C:\Users\<user>\Downloads\RamQuick\ramquick_engine.dll");
            case AccountLocation.ComputerName:
                // A text without a separator still names the computer.
                string machineName = Environment.MachineName;
                return (machineName, string.IsNullOrWhiteSpace(machineName) ? machineName : "<machine>");
            case AccountLocation.Placeholders:
                // Every placeholder of either family, as a redacted text holds them, stays as it stands.
                const string Redacted = @"<data>\journal, <app folder>\ramquick_engine.dll, C:\Users\<user>\x, "
                    + @"\\<machine>\share, %USERPROFILE%\x, %LOCALAPPDATA%\x, %APPDATA%\x, %TEMP%\x, "
                    + @"D:\Users\%USERNAME%\x, logs\RamQuick-x.log";
                return (Redacted, Redacted);
            default:
                throw new ArgumentOutOfRangeException(nameof(location), location, "Not an account location of these tests.");
        }
    }

    // The folder without its trailing separator, and whether the logs redact it: not when Windows reports it empty,
    // white space or a bare volume root, which would otherwise rewrite every path on that volume.
    private static (string Folder, bool Redacted) KnownFolder(Environment.SpecialFolder folder)
    {
        string path = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify).Trim());
        return (path, path.Length > 0 && Path.GetPathRoot(path) is { } volume && volume.Length < path.Length);
    }

    private static RunSummary CompletedSummary(
        IReadOnlyList<ErrorRecord> errors,
        SummaryCauseHints causeHints,
        ulong? totalErrors = null,
        Guid? runId = null) =>
        RunSummary.Build(JournalSamples.CompletedRunSource(errors, causeHints, totalErrors, runId));

    private static SummaryCauseHints TwoHints() => new()
    {
        Items = new[]
        {
            new SummaryCauseHint
            {
                Rank = 1,
                Category = "Row/bank timings",
                LikelihoodPercent = 64.0,
                Suggestion = "Errors appear under rapid row and bank switching. Loosen tRCD, tRP or tRAS, or tFAW/tRRD.",
                Evidence = "100 % of errors in 'Random bank thrash' (bank-stride order)",
            },
            new SummaryCauseHint
            {
                Rank = 2,
                Category = "Voltage / signal integrity",
                LikelihoodPercent = 36.0,
                Suggestion = "Errors keep hitting the same data bit. Try slightly more DRAM VDD/VDDQ, revisit drive "
                    + "strength or ODT (RTT), or lower the frequency one step.",
                Evidence = "3 errors across 3 addresses",
            },
        },
    };

    // A run that stopped during step 2 of 6, rebuilt from its journal: step 1 finished, a heartbeat 65 s into
    // the test with 450 % coverage and only the CPU reading available, and no result or released record.
    private static RunSummarySource UnfinishedSource(out DateTimeOffset beatUtc)
    {
        IReadOnlyList<StepAttributes> steps = JournalSamples.QuickSteps();
        JournalPrepared prepared = JournalSamples.Prepared();
        DateTimeOffset running = _t0 + TimeSpan.FromMilliseconds(prepared.PreparationMs);
        DateTimeOffset firstStepEnd = running + steps[0].TimeBudget;
        TimeSpan beatElapsed = TimeSpan.FromSeconds(65);
        beatUtc = running + beatElapsed;
        ulong gib = JournalSamples.GiB;

        StepOutcome firstStep = JournalSamples.Outcome(steps[0], steps[0].TimeBudget, 4, 4 * gib, 0, completed: true);
        RunProgress lastProgress = JournalSamples.Progress(
            1,
            steps.Count,
            beatElapsed,
            TimeSpan.FromSeconds(235),
            (4 * gib) + (gib / 2),
            gib,
            0);
        return new RunSummarySource
        {
            Begin = JournalSamples.BeginA(_t0) with { Utc = _t0 },
            Prepared = prepared with { Utc = running },
            FinishedSteps = new[] { JournalStepEnd.FromOutcome(firstStep) with { Utc = firstStepEnd } },
            UnfinishedStep = new JournalStepStart { Index = 2, Count = steps.Count, Name = steps[1].Name, Utc = firstStepEnd },
            LastBeat = JournalBeat.From(lastProgress, JournalSamples.Readings(70.5, null)) with { Utc = beatUtc },
            Temperatures = SummaryTemperatures.Initial.Merge(
                70.5,
                JournalSamples.AvailableStatus,
                null,
                JournalSamples.DriverUnavailableStatus),
            ErrorDetailAvailable = false,
            Recovered = true,
            Partial = true,
        };
    }

    // second s of a run: step s / 10 (0-based), no estimate until 15 s, 5 % coverage and 1/7 error per second.
    private static RunProgress ProgressAt(int second) => JournalSamples.Progress(
        Math.Min(second / 10, 5),
        6,
        TimeSpan.FromSeconds(second),
        second < 15 ? null : TimeSpan.FromSeconds(300 - second),
        (ulong)second * JournalSamples.GiB / 20,
        JournalSamples.GiB,
        (ulong)(second / 7));

    private static async Task<JsonDocument> PublishAndParseAsync(ResultsLog log, RunSummary summary)
    {
        SummaryPublishResult result = await log.PublishSummaryAsync(summary).WaitAsync(_ceiling, Cancellation);
        Assert.True(result.Succeeded, result.Error);
        return JsonDocument.Parse(await File.ReadAllTextAsync(log.SummaryJsonPath, Cancellation));
    }

    private static async Task<string[]> ReadLogLinesAsync(ResultsLog log)
    {
        await log.FlushAsync().WaitAsync(_ceiling, Cancellation);
        Assert.Null(log.WriteFailure);

        byte[] bytes = await ReadSharedBytesAsync(log.LogPath);
        Assert.False(bytes.AsSpan().StartsWith(_utf8Bom), "The .log starts with a byte-order mark.");
        string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        if (text.Length == 0)
        {
            return Array.Empty<string>();
        }

        // Every line ends in CRLF, and no event spans a line it did not start.
        Assert.EndsWith(LineEnd, text, StringComparison.Ordinal);
        string[] lines = text[..^LineEnd.Length].Split(LineEnd);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            Assert.Matches(LogLinePattern(), line);
        });
        return lines;
    }

    // The .log stays open for writing while its run lasts, so it is read with write sharing allowed.
    private static async Task<byte[]> ReadSharedBytesAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Cancellation);
        return buffer.ToArray();
    }

    private static async Task<string> WaitForFileContentAsync(string path, string marker)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path))
            {
                string text = Encoding.UTF8.GetString(await ReadSharedBytesAsync(path));
                if (text.Contains(marker, StringComparison.Ordinal) && text.EndsWith(LineEnd + LineEnd, StringComparison.Ordinal))
                {
                    return text;
                }
            }

            Assert.True(elapsed.Elapsed < _ceiling, $"'{marker}' never reached '{path}'.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Cancellation);
        }
    }

    private static string EventOf(string line) =>
        LogLinePattern().Match(line) is { Success: true } match ? match.Groups["event"].Value : string.Empty;

    private static string Line(string[] lines, string eventName) =>
        Assert.Single(lines, line => EventOf(line) == eventName);

    // Each field is " key=value", followed by another field or the end of the line.
    private static void AssertFields(string line, params string[] fields)
    {
        foreach (string field in fields)
        {
            Assert.Contains(" " + field + " ", line + " ", StringComparison.Ordinal);
        }
    }

    private static void AssertTextLines(string text, params string[] lines)
    {
        foreach (string line in lines)
        {
            Assert.Contains(LineEnd + line + LineEnd, text, StringComparison.Ordinal);
        }
    }

    // A group row of the text summary: two spaces, the address label, then the word address.
    private static int GroupRowCount(string text, string addressLabel) =>
        text.Split(LineEnd).Count(line => line.StartsWith("  " + addressLabel + " 0x", StringComparison.Ordinal));

    private static int RecordHeaderCount(string text) =>
        text.Split(LineEnd).Count(line => RecordHeaderPattern().IsMatch(line));

    // The .log's quoting of free text: \" and \\ escaped, control characters written as spaces.
    private static string Quoted(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (char character in value)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\').Append(character);
            }
            else
            {
                builder.Append(char.IsControl(character) ? ' ' : character);
            }
        }

        return builder.Append('"').ToString();
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString(UtcFormat, Invariant);

    private static DateTimeOffset StampOf(string line) => DateTimeOffset.ParseExact(
        line[..24],
        UtcFormat,
        Invariant,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static JsonElement At(JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            Assert.True(element.TryGetProperty(name, out JsonElement child), $"The JSON has no '{name}' property.");
            element = child;
        }

        return element;
    }

    private static JsonElement Kind(JsonValueKind kind, JsonElement element, params string[] path)
    {
        JsonElement value = At(element, path);
        Assert.True(value.ValueKind == kind, $"'{string.Join('.', path)}' is a JSON {value.ValueKind}, not a {kind}.");
        return value;
    }

    // Exact as a JSON integer and as the double a JavaScript or PowerShell reader sees (every value is below 2^53).
    private static void AssertExactInteger(ulong expected, JsonElement element, params string[] path)
    {
        JsonElement value = Kind(JsonValueKind.Number, element, path);
        Assert.Equal(expected.ToString(Invariant), value.GetRawText());
        Assert.Equal(expected, value.GetUInt64());
        Assert.Equal(expected, (ulong)value.GetDouble());
    }

    private static ulong ParseHex(JsonElement element)
    {
        Assert.True(SummaryFormat.TryParseHex64(element.GetString(), out ulong value), $"'{element}' is not hex text.");
        return value;
    }

    [GeneratedRegex(@"^RamQuick-\d{8}-\d{6}-Quick-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex QuickBaseNamePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (?<event>[A-Z][A-Z_]*)(?: |$)", RegexOptions.CultureInvariant)]
    private static partial Regex LogLinePattern();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z PROGRESS step=\d+/\d+ elapsed_ms=\d+ remaining_ms=-?\d+ coverage=\d+(\.\d+)? errors=\d+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLinePattern();

    [GeneratedRegex(@" (?<key>address|expected|actual|reread|bit_mask)=(?<value>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorHexFieldPattern();

    [GeneratedRegex(@"^0x[0-9A-F]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex64Pattern();

    // A folder directly under <drive>:\Users\, where Windows keeps each account's profile named after the account.
    [GeneratedRegex(@"^[A-Za-z]:[\\/]Users[\\/][^\\/]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFolderPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoUtcPattern();

    [GeneratedRegex(@"^\s*(diagnosis|cause|root cause)\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CertaintyHeadingPattern();

    // A row of the text summary's steps table: the right-aligned step number, then two spaces.
    [GeneratedRegex(@"^ *\d+ {2}\S", RegexOptions.CultureInvariant)]
    private static partial Regex StepRowPattern();

    // The first line of a stored error record in the text summary: its 1-based number, then its step.
    [GeneratedRegex(@"^  \d+\. Step '", RegexOptions.CultureInvariant)]
    private static partial Regex RecordHeaderPattern();

    /// <summary>
    /// The events whose free-text message the redaction tests write.
    /// </summary>
    public enum FreeTextEvent
    {
        JournalFailure,
        Notice,
        CallbackFault,
    }

    /// <summary>
    /// A location or name that identifies a Windows account or the computer, as it appears in a message, or one a
    /// redaction already wrote.
    /// </summary>
    public enum AccountLocation
    {
        DataFolder,
        LocalAppData,
        RoamingAppData,
        UserProfile,
        UserNameSegment,
        ProfileSibling,
        ForeignProfile,
        ComputerName,
        Placeholders,
    }

    /// <summary>
    /// A run-id source that returns its ids in order, counts the calls and fails the test past its script.
    /// </summary>
    private sealed class ScriptedRunIds
    {
        private readonly Guid[] _ids;

        public ScriptedRunIds(params Guid[] ids) => _ids = ids;

        public int Calls { get; private set; }

        public Guid Next()
        {
            Assert.True(Calls < _ids.Length, "OpenRun drew more run ids than the test scripted.");
            return _ids[Calls++];
        }
    }

    /// <summary>
    /// A caller's list that it refills for its next batch, counting every read made off the thread that created
    /// it: a log that kept the list past the call would read it on its drain.
    /// </summary>
    private sealed class ReusableRecordList : IReadOnlyList<ErrorRecord>
    {
        private readonly int _callerThreadId = Environment.CurrentManagedThreadId;
        private readonly ErrorRecord[] _records;
        private int _readsOffCallerThread;

        public ReusableRecordList(IEnumerable<ErrorRecord> records) => _records = records.ToArray();

        public int Count => _records.Length;

        public int ReadsOffCallerThread => Volatile.Read(ref _readsOffCallerThread);

        public ErrorRecord this[int index]
        {
            get
            {
                if (Environment.CurrentManagedThreadId != _callerThreadId)
                {
                    Interlocked.Increment(ref _readsOffCallerThread);
                }

                return _records[index];
            }
        }

        public void Refill(ErrorRecord record) => Array.Fill(_records, record);

        public IEnumerator<ErrorRecord> GetEnumerator()
        {
            for (int index = 0; index < _records.Length; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// An exception whose text cannot be formatted, as a corrupted exception in a crash handler might be.
    /// </summary>
    private sealed class UnformattableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("The exception text is unavailable.");
    }

    /// <summary>
    /// An exception whose text is formatted only once the test releases it, holding the write as a stalled disk
    /// would.
    /// </summary>
    private sealed class StallingException : Exception
    {
        public const string Text = "StallingException: formatted after the release";

        private readonly ManualResetEventSlim _release;

        public StallingException(ManualResetEventSlim release) => _release = release;

        public override string ToString()
        {
            _release.Wait();
            return Text;
        }
    }
}
