using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RamQuick.App;
using RamQuick.App.Tests.Fakes;
using RamQuick.Core;
using RamQuick.Logging;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// Process-level crash recovery: a real run of <c>RamQuick.TestHost</c> over the production engine is killed in the
/// middle of step 2, and the next launch's <c>RunController</c> reports it from the journal it left behind.
/// </summary>
/// <remarks>
/// The host is a separate process bound to the production <c>ramquick_engine.dll</c>; this process never runs an
/// engine session here. The relaunched controller drives a <see cref="FakeEngine"/>, because recovery reads only the
/// journal and must never take a value from the new launch's machine or clock.
/// </remarks>
public sealed class CrashRecoveryProcessTests
{
    private const int RequestedRegionMiB = 512;
    private const int PlanStepCount = 3;
    private const int StepSeconds = 20;
    private const int KilledStepNumber = 2;
    private const int JournalTailLineCount = 20;

    // The wording users read, held literally so that a change to UiText cannot silently change it.
    private const string ExpectedUnfinishedMessage =
        "The previous test did not finish at step 2 of 3 (Random bank thrash), which likely indicates severe instability.";

    private const string StepRecord = "step";
    private const string StepEndRecord = "stepEnd";
    private const string BeatRecord = "beat";
    private const string BeginRecord = "begin";
    private const string ResultRecord = "result";

    private static readonly TimeSpan _journalPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _killPointTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan _killedExitTimeout = TimeSpan.FromSeconds(10);

    // The relaunch clock runs an hour ahead of the crash, so a summary time taken from it instead of the journal
    // lands at least 30 minutes after anything the killed run recorded.
    private static readonly TimeSpan _relaunchClockLead = TimeSpan.FromHours(1);
    private static readonly TimeSpan _relaunchClockMargin = TimeSpan.FromMinutes(30);

    // Covers the millisecond truncation of journal timestamps; both processes read the same system clock.
    private static readonly TimeSpan _wallClockTolerance = TimeSpan.FromSeconds(5);

    private static readonly int[] _hostExitCodes =
    [
        TestHostLauncher.ExitPassed,
        TestHostLauncher.ExitFailed,
        TestHostLauncher.ExitAborted,
        TestHostLauncher.ExitRefused,
        TestHostLauncher.ExitNotPublished,
        TestHostLauncher.ExitUnhandled,
    ];

    private readonly ITestOutputHelper _output;

    public CrashRecoveryProcessTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    // Async methods carry the Async suffix under the repository's naming rule (IDE1006), tests included.
    [Fact(Timeout = 300_000)]
    public async Task KilledMidStep_NextLaunchReportsUnfinishedRunAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string killedStepName = TestPlans.Quick.Steps[KilledStepNumber - 1].Name;
        using var root = new TempDataRoot();

        KilledRun killed = await RunHostUntilKilledInStepTwoAsync(root, cancellationToken);

        // Phase 2: the next launch, on the same data folder, initialised on its own UI thread as the application is.
        using var ui = new DispatcherThread();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.Add(_relaunchClockLead));
        ControllerGraph.Graph? graph = null;
        try
        {
            graph = await ControllerGraph.CreateInitializedAsync(ui, new FakeEngine(), root.Data, timeProvider: time);
            ControllerGraph.Graph relaunched = graph;
            LaunchOutcome launch = await ui.InvokeAsync(() => new LaunchOutcome(
                relaunched.StateMachine.State,
                relaunched.StateMachine.EndReason,
                relaunched.StateMachine.EndDetail,
                relaunched.Controller.ResultMessage,
                relaunched.Controller.NoticeMessage,
                relaunched.Controller.CanStart));

            AssertUnfinishedRunReported(launch, killed, killedStepName);
            await AssertPartialSummaryPublishedAsync(root, killed, killedStepName, time, cancellationToken);
        }
        catch
        {
            ReportFailureContext(killed.HostOutput, killed.Journal);
            throw;
        }
        finally
        {
            if (graph is not null)
            {
                await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
            }
        }
    }

    // Phase 1: a three-step run of 20-second steps over about 512 MiB, killed once step 2 has started. Windows
    // reclaims the killed host's locked memory, so nothing about the release is asserted.
    private async Task<KilledRun> RunHostUntilKilledInStepTwoAsync(TempDataRoot root, CancellationToken cancellationToken)
    {
        DateTimeOffset launchedUtc = DateTimeOffset.UtcNow;
        await using TestHostLauncher.RunningHost host = TestHostLauncher.Start(TestHostLauncher.Arguments(
            root.Path,
            TestHostLauncher.RegionMiBFor(RequestedRegionMiB),
            steps: PlanStepCount,
            stepSeconds: StepSeconds));

        JournalView? lastRead = null;
        try
        {
            lastRead = await WaitForKillPointAsync(host, root.JournalPath, view => lastRead = view, cancellationToken);

            host.Kill();
            DateTimeOffset killedUtc = DateTimeOffset.UtcNow;

            // Drains the output the host wrote before it died, so the verdict check below sees all of it.
            int exitCode = await host.WaitForExitAsync(_killedExitTimeout, cancellationToken);
            Assert.True(
                host.FindLine(TestHostLauncher.VerdictPrefix) is null && !_hostExitCodes.Contains(exitCode),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"RamQuick.TestHost ended on its own (exit code {exitCode}) instead of being killed in step {KilledStepNumber}."));

            Assert.True(File.Exists(root.JournalPath), $"The killed run's journal is missing: {root.JournalPath}.");
            JournalView journal = JournalView.Parse(await ReadJournalTextAsync(root.JournalPath, cancellationToken));
            lastRead = journal;

            Assert.False(journal.Contains(ResultRecord), "The journal holds a result record, so the run was not killed mid-step.");
            Assert.True(
                journal.FindStep(KilledStepNumber + 1) is null,
                $"Step {KilledStepNumber + 1} had started before the kill; the host was not killed in step {KilledStepNumber}.");
            Assert.True(journal.Contains(BeatRecord), "The killed run journalled no heartbeat.");

            JsonElement step = journal.FindStep(KilledStepNumber)
                ?? throw new InvalidOperationException($"The step {KilledStepNumber} record disappeared from the journal.");
            JsonElement begin = journal.First(BeginRecord)
                ?? throw new InvalidOperationException("The killed run's journal holds no begin record.");

            var killed = new KilledRun(
                journal,
                begin.GetProperty("runId").GetGuid(),
                RequiredString(begin, "logBaseName"),
                step.GetProperty("count").GetInt32(),
                RequiredString(step, "name"),
                launchedUtc,
                killedUtc,
                host.DescribeOutput());

            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Killed RamQuick.TestHost (pid {host.ProcessId}, exit code {exitCode}) {(killedUtc - launchedUtc).TotalSeconds:0.0} s after launch, in step {KilledStepNumber} of {killed.StepCount} ('{killed.StepName}'), run {killed.RunId:N}."));
            return killed;
        }
        catch
        {
            ReportFailureContext(host.DescribeOutput(), lastRead);
            throw;
        }
    }

    // Polls the journal until step 2's record and step 1's stepEnd are both on disk. The step record is written
    // synchronously by the engine coordinator's step gate, but stepEnd is journalled from the UI thread without an
    // acknowledgment, so it can land just after the next step's record; killing on the step record alone could drop
    // step 1's outcome from the partial summary.
    private static async Task<JournalView> WaitForKillPointAsync(
        TestHostLauncher.RunningHost host,
        string journalPath,
        Action<JournalView> observe,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await TryReadJournalAsync(journalPath, cancellationToken) is { } view)
            {
                observe(view);
                if (view.FindStep(KilledStepNumber) is not null && view.HasStepEnd(KilledStepNumber - 1))
                {
                    return view;
                }
            }

            if (host.Process.HasExited)
            {
                Assert.Fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"RamQuick.TestHost exited with code {host.Process.ExitCode} before step {KilledStepNumber} started.{Environment.NewLine}{host.DescribeOutput()}"));
            }

            if (stopwatch.Elapsed >= _killPointTimeout)
            {
                Assert.Fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Step {KilledStepNumber} did not start within {_killPointTimeout.TotalSeconds:0} s.{Environment.NewLine}{host.DescribeOutput()}"));
            }

            await Task.Delay(_journalPollInterval, cancellationToken);
        }
    }

    // The writer holds the journal open for appending with FileShare.Read | FileShare.Delete, so the reader must
    // share writing and deletion in turn.
    private static async Task<string> ReadJournalTextAsync(string journalPath, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };

        await using var stream = new FileStream(journalPath, options);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // Before the host creates the journal, and while Windows briefly refuses a share, there is nothing to read yet.
    private static async Task<JournalView?> TryReadJournalAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            return JournalView.Parse(await ReadJournalTextAsync(journalPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Phase 2: the relaunched controller's state and result panel text.
    private static void AssertUnfinishedRunReported(LaunchOutcome launch, KilledRun killed, string killedStepName)
    {
        Assert.Equal(RunState.Failed, launch.State);
        Assert.Equal(RunEndReason.PreviousRunUnfinished, launch.EndReason);

        Assert.Equal(killedStepName, killed.StepName);
        Assert.Equal(PlanStepCount, killed.StepCount);

        Assert.Equal(UiText.PreviousRunUnfinished(KilledStepNumber, PlanStepCount, killedStepName), launch.ResultMessage);
        Assert.Equal(ExpectedUnfinishedMessage, launch.ResultMessage);
        Assert.Equal(ExpectedUnfinishedMessage, launch.EndDetail);

        // A failed publication would replace the notice with "Results could not be saved".
        Assert.Null(launch.NoticeMessage);
        Assert.True(launch.CanStart, "Start must be offered once the unfinished run has been recovered.");
    }

    // Phase 3: the journal is retired and the partial summary of the killed run is published from it alone.
    private static async Task AssertPartialSummaryPublishedAsync(
        TempDataRoot root,
        KilledRun killed,
        string killedStepName,
        FakeTimeProvider time,
        CancellationToken cancellationToken)
    {
        Assert.False(File.Exists(root.JournalPath), $"The journal was not deleted after recovery: {root.JournalPath}.");

        string jsonPath = Assert.Single(root.SummaryJsonFiles());
        string textPath = Assert.Single(root.SummaryTextFiles());
        Assert.Equal(Path.Combine(root.LogsPath, killed.LogBaseName + "-summary.json"), jsonPath, ignoreCase: true);
        Assert.Equal(Path.Combine(root.LogsPath, killed.LogBaseName + "-summary.txt"), textPath, ignoreCase: true);
        Assert.True(
            File.Exists(Path.Combine(root.LogsPath, killed.LogBaseName + ".log")),
            "The killed run's .log must be kept beside its summaries.");

        string runId = killed.RunId.ToString("N", CultureInfo.InvariantCulture);
        string text = await File.ReadAllTextAsync(textPath, cancellationToken);
        Assert.Contains(runId, text, StringComparison.OrdinalIgnoreCase);

        string json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
        Assert.True(RunSummaryJson.TryDeserialize(json, out _), "The partial summary is not a readable schema v1 summary.");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement summary = document.RootElement;
        Assert.Equal(1, summary.GetProperty("schemaVersion").GetInt32());

        JsonElement run = summary.GetProperty("run");
        Assert.Equal(runId, RequiredString(run, "id"), ignoreCase: true);
        Assert.Equal("FAIL", RequiredString(run, "verdict"));
        Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), RequiredString(run, "endReason"));
        Assert.True(run.GetProperty("recovered").GetBoolean(), "run.recovered must be true.");
        Assert.True(run.GetProperty("partial").GetBoolean(), "run.partial must be true.");
        Assert.Equal(nameof(TestType.Quick), RequiredString(run, "testType"));

        JsonElement errors = summary.GetProperty("errors");
        Assert.False(errors.GetProperty("detailAvailable").GetBoolean(), "errors.detailAvailable must be false.");
        JsonElement records = errors.GetProperty("records");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(0, records.GetArrayLength());

        // SummaryUnfinishedRun.LastStepIndex is documented as the 1-based step number, as the journal stores it.
        JsonElement unfinished = summary.GetProperty("unfinishedRun");
        Assert.Equal(JsonValueKind.Object, unfinished.ValueKind);
        Assert.Equal(KilledStepNumber, unfinished.GetProperty("lastStepIndex").GetInt32());
        Assert.Equal(PlanStepCount, unfinished.GetProperty("stepCount").GetInt32());
        Assert.Equal(killedStepName, RequiredString(unfinished, "lastStepName"));
        Assert.Equal(ExpectedStepName(KilledStepNumber), killedStepName);

        AssertEndedAtLastHeartbeat(run, unfinished, killed, time);
        AssertStepOutcomes(summary);
    }

    // run.endedUtc is the last heartbeat's stamp: never the relaunch clock, and inside the killed run's lifetime.
    private static void AssertEndedAtLastHeartbeat(JsonElement run, JsonElement unfinished, KilledRun killed, FakeTimeProvider time)
    {
        string endedText = RequiredString(run, "endedUtc");
        Assert.EndsWith("Z", endedText, StringComparison.Ordinal);
        DateTimeOffset endedUtc = ParseUtc(endedText);
        Assert.Equal(TimeSpan.Zero, endedUtc.Offset);

        DateTimeOffset relaunchUtc = time.GetUtcNow();
        Assert.True(
            endedUtc < relaunchUtc - _relaunchClockMargin,
            $"run.endedUtc {endedText} is not earlier than the relaunch clock {relaunchUtc:O} less {_relaunchClockMargin.TotalMinutes:0} minutes, so it was not taken from the journal.");
        Assert.InRange(endedUtc, killed.LaunchedUtc - _wallClockTolerance, killed.KilledUtc + _wallClockTolerance);

        JsonElement lastBeat = killed.Journal.Last(BeatRecord)
            ?? throw new InvalidOperationException("The killed run's journal holds no heartbeat.");
        Assert.Equal(ParseUtc(RequiredString(lastBeat, "utc")), endedUtc);
        Assert.Equal(endedUtc, ParseUtc(RequiredString(unfinished, "lastHeartbeatUtc")));
    }

    // steps[] holds step 1 as finished and step 2 as started but unfinished; step 3 never began.
    private static void AssertStepOutcomes(JsonElement summary)
    {
        JsonElement[] steps = summary.GetProperty("steps").EnumerateArray().ToArray();

        JsonElement first = Assert.Single(steps, step => step.GetProperty("index").GetInt32() == 1);
        Assert.Equal(ExpectedStepName(1), RequiredString(first, "name"));
        Assert.True(first.GetProperty("completed").GetBoolean(), "Step 1 finished before the kill, so it must be completed.");

        JsonElement killedStep = Assert.Single(steps, step => step.GetProperty("index").GetInt32() == KilledStepNumber);
        Assert.Equal(ExpectedStepName(KilledStepNumber), RequiredString(killedStep, "name"));
        Assert.False(
            killedStep.GetProperty("completed").GetBoolean(),
            $"Step {KilledStepNumber} was killed while running, so it must not be completed.");

        Assert.DoesNotContain(steps, step => step.GetProperty("index").GetInt32() > KilledStepNumber);
    }

    private static string ExpectedStepName(int stepNumber) => TestPlans.Quick.Steps[stepNumber - 1].Name;

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"'{propertyName}' is null in {element.GetRawText()}.");

    private void ReportFailureContext(string hostOutput, JournalView? journal)
    {
        _output.WriteLine("RamQuick.TestHost output:");
        _output.WriteLine(hostOutput);
        _output.WriteLine(journal is null
            ? "Journal: never read."
            : $"Journal, last {JournalTailLineCount} line(s):{Environment.NewLine}{journal.Tail(JournalTailLineCount)}");
    }

    /// <summary>What the killed run left behind, captured once the host had died.</summary>
    private sealed record KilledRun(
        JournalView Journal,
        Guid RunId,
        string LogBaseName,
        int StepCount,
        string StepName,
        DateTimeOffset LaunchedUtc,
        DateTimeOffset KilledUtc,
        string HostOutput);

    /// <summary>The relaunched controller's state, read on its dispatcher thread.</summary>
    private sealed record LaunchOutcome(
        RunState State,
        RunEndReason? EndReason,
        string? EndDetail,
        string? ResultMessage,
        string? NoticeMessage,
        bool CanStart);

    /// <summary>
    /// The journal's JSON Lines as read at one moment. A line that does not parse, such as a torn last line, is kept
    /// for diagnostics and otherwise ignored, as recovery ignores it.
    /// </summary>
    private sealed class JournalView
    {
        private readonly string[] _lines;
        private readonly JsonElement[] _records;

        private JournalView(string[] lines, JsonElement[] records)
        {
            _lines = lines;
            _records = records;
        }

        public static JournalView Parse(string text)
        {
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var records = new List<JsonElement>(lines.Length);
            foreach (string line in lines)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("t", out JsonElement type)
                        && type.ValueKind == JsonValueKind.String)
                    {
                        records.Add(document.RootElement.Clone());
                    }
                }
                catch (JsonException)
                {
                    // A torn or partial line written while the host was being killed.
                }
            }

            return new JournalView(lines, records.ToArray());
        }

        public bool Contains(string recordType) => _records.Any(record => IsType(record, recordType));

        public JsonElement? First(string recordType) =>
            _records.Where(record => IsType(record, recordType)).Select(static record => (JsonElement?)record).FirstOrDefault();

        public JsonElement? Last(string recordType) =>
            _records.Where(record => IsType(record, recordType)).Select(static record => (JsonElement?)record).LastOrDefault();

        public JsonElement? FindStep(int stepNumber) =>
            _records.Where(record => IsType(record, StepRecord) && HasIndex(record, stepNumber))
                .Select(static record => (JsonElement?)record)
                .FirstOrDefault();

        public bool HasStepEnd(int stepNumber) =>
            _records.Any(record => IsType(record, StepEndRecord) && HasIndex(record, stepNumber));

        public string Tail(int lineCount) =>
            string.Join(Environment.NewLine, _lines.Skip(Math.Max(0, _lines.Length - lineCount)).Select(static line => "  " + line));

        private static bool IsType(JsonElement record, string recordType) =>
            string.Equals(record.GetProperty("t").GetString(), recordType, StringComparison.Ordinal);

        // Journal step numbers are 1-based, as in "step n of N".
        private static bool HasIndex(JsonElement record, int stepNumber) =>
            record.TryGetProperty("index", out JsonElement index)
            && index.ValueKind == JsonValueKind.Number
            && index.TryGetInt32(out int value)
            && value == stepNumber;
    }
}
