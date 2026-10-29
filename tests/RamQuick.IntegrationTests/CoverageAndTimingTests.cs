using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using RamQuick.App.Services;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Logging.Tests;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// Real engine runs through <c>RamQuick.Interop</c> that measure coverage accuracy against the counting layer's
/// independent byte count, duration and time-remaining accuracy on scaled plans, UI-thread responsiveness and progress
/// cadence on a WPF <see cref="Dispatcher"/>, and stop latency, including a stop during a stalled step-journal wait.
/// The <c>Long</c> category repeats the coverage-target check on the real Quick, Standard and Exhaustive plans.
/// </summary>
/// <remarks>
/// <para>
/// Every bound is the acceptance bound itself: coverage within 1 % of the independent count; time remaining within
/// ±10 % of the actual remaining time on every per-second sample whose actual remaining time is at least 10 s (shorter
/// samples are measured and reported but not gated, the open deviation recorded for the final seconds); run duration,
/// Running → Completed, within ±15 % of the plan's target; progress at most 1 s apart on the UI thread; a 100 ms
/// dispatcher probe serviced within 250 ms; and Completed within 2 s of a stop request. Each assertion message carries
/// the measured value and its bound, and the tables written to the test output make the margins visible in the TRX
/// results.
/// </para>
/// <para>
/// Times are <see cref="Stopwatch"/> timestamps taken when an event is received on the dispatcher, the moment the
/// application's UI would see it. Regions stay at or below 1 GiB, which locks without elevation once the working set is
/// raised. Release time is reported, never gated, because it grows with the region size.
/// </para>
/// </remarks>
public sealed class CoverageAndTimingTests
{
    private const double CoverageTolerance = 0.01;
    private const double RemainingTolerance = 0.10;
    private const double DurationTolerance = 0.15;
    private const double CoveragePercentRelativeTolerance = 1e-6;
    private const int MinimumProbeSamples = 50;
    private const int MidStepProgressOccurrence = 3;
    private const int MidPassStopStepIndex = 1;
    private const int HoldQuickStepIndex = 4;
    private const string BeatRecord = "beat";
    private const string StepRecord = "step";
    private const string PassVerdict = "PASS";
    private const ulong RunSeed = 0x2026_1028_C0DE_5EEDUL;

    // Acceptance bounds.
    private static readonly TimeSpan _remainingGateFloor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxProgressGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxProbeLatency = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _maxStopLatency = TimeSpan.FromSeconds(2);

    // Scenario timing.
    private static readonly TimeSpan _midPassStopDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan _holdStopDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _holdStepBudget = TimeSpan.FromMilliseconds(12_500);
    private static readonly TimeSpan _shortStepBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _beatStall = TimeSpan.FromSeconds(2);

    // Waits that only bound a broken run; none of them is an acceptance bound.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _eventTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _sessionCreationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _blockedAppendTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _firstBeatTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _stopCompletionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _publicationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan _completionAllowance = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _liveSessionSettleTimeout = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    /// <summary>Initializes the tests with the output that receives the measurement tables.</summary>
    /// <param name="output">The test output.</param>
    public CoverageAndTimingTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>Where <see cref="StopRequest_DirectSession_CompletesWithinTwoSecondsAsync"/> requests its stop.</summary>
    public enum StopPoint
    {
        /// <summary>Immediately after <see cref="IEngineSession.Start"/>, while the engine allocates and locks.</summary>
        Preparing,

        /// <summary>After the third progress snapshot of the first step, while its passes run.</summary>
        MidStep,

        /// <summary>One second into a step with a 5 s hold, while the hold waits on the stop event.</summary>
        Hold,
    }

    // Criterion 5: the engine's coverage counter against the counting layer's independent count, on a clean run.
    [Fact(Timeout = 300_000)]
    public async Task VerifiedBytes_WithinOnePercentOfIndependentCountAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create(FaultSpec.None);
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = ScaledPlans.TimingQuick;
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib512);

        using (var ui = new DispatcherThread())
        using (DirectRun run = await DirectRun.CreateAsync(ui, engine, system, plan, regionBytes))
        {
            run.Start();
            EngineCompleted completed = await run.Recorder.WaitCompletedAsync(CompletionTimeout(plan), cancellationToken);

            // Read while the native session is alive: the counting layer is destroyed with it.
            ulong counted = SessionProbe.GetIndependentVerifiedBytes(run.Session);
            AssertCleanCompletion(completed, "the full TimingQuick run");
            AssertCoverageMatchesIndependentCount(run.Recorder, completed, counted, "full TimingQuick run");

            await run.Recorder.WaitReleasedAsync(_releaseTimeout, cancellationToken);
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }

    // Criterion 5 with a stop in the middle of a pass: data written but not yet verified counts on neither side.
    [Fact(Timeout = 300_000)]
    public async Task VerifiedBytes_MidPassStop_WithinOnePercentOfIndependentCountAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create(FaultSpec.None);
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = ScaledPlans.TimingQuick;
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib512);

        using (var ui = new DispatcherThread())
        using (DirectRun run = await DirectRun.CreateAsync(ui, engine, system, plan, regionBytes))
        {
            run.Start();
            RecordedEngineEvent stepStarted = await WaitForEventAsync(
                run.Recorder,
                recorded => recorded.Kind == EngineEventKind.StepStarted
                    && ((EngineStepStarted)recorded.Payload).Index == MidPassStopStepIndex,
                occurrence: 1,
                Invariant($"the StepStarted event of step {MidPassStopStepIndex + 1}"),
                cancellationToken);
            await DelayUntilAsync(stepStarted.Timestamp, _midPassStopDelay, cancellationToken);

            long requestedAt = Stopwatch.GetTimestamp();
            run.Session.RequestStop(StopReason.User);
            EngineCompleted completed = await run.Recorder.WaitCompletedAsync(_stopCompletionTimeout, cancellationToken);
            ulong counted = SessionProbe.GetIndependentVerifiedBytes(run.Session);

            _output.WriteLine(Invariant(
                $"Stop requested {Stopwatch.GetElapsedTime(stepStarted.Timestamp, requestedAt).TotalSeconds:0.000} s into step {MidPassStopStepIndex + 1} ('{plan.Steps[MidPassStopStepIndex].Name}')."));
            Assert.True(
                completed.EndReason == RunEndReason.UserStop,
                Invariant($"The stopped run ended with {completed.EndReason}, expected {RunEndReason.UserStop}; detail '{completed.EndDetail}'."));
            Assert.True(
                completed.StepResults.Count == MidPassStopStepIndex + 1 && !completed.StepResults[MidPassStopStepIndex].Completed,
                Invariant($"The stop must interrupt step {MidPassStopStepIndex + 1}: {completed.StepResults.Count} step result(s), the last completed = {completed.StepResults.LastOrDefault()?.Completed}."));
            AssertCoverageMatchesIndependentCount(run.Recorder, completed, counted, "TimingQuick run stopped mid-pass");

            await run.Recorder.WaitReleasedAsync(_releaseTimeout, cancellationToken);
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }

    // Criterion 6 on scaled plans: Quick 30 s and Standard 120 s, the real engine with the real clock.
    [Theory(Timeout = 600_000)]
    [InlineData(TestType.Quick)]
    [InlineData(TestType.Standard)]
    public async Task TimeRemainingAndDuration_ScaledPlan_WithinBoundsAsync(TestType type)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TestPlan plan = type switch
        {
            TestType.Quick => ScaledPlans.TimingQuick,
            TestType.Standard => ScaledPlans.TimingStandard,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only the Quick and Standard plans are scaled for timing."),
        };

        NativeEngine engine = TestEngines.Create(FaultSpec.None);
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib512);

        using (var ui = new DispatcherThread())
        using (DirectRun run = await DirectRun.CreateAsync(ui, engine, system, plan, regionBytes))
        {
            run.Start();
            EngineCompleted completed = await run.Recorder.WaitCompletedAsync(CompletionTimeout(plan), cancellationToken);
            EngineReleased released = await run.Recorder.WaitReleasedAsync(_releaseTimeout, cancellationToken);
            AssertCleanCompletion(completed, Invariant($"the scaled {type} run"));

            EngineRunRecorder recorder = run.Recorder;
            long preparedAt = RequireEvent(recorder, EngineEventKind.Prepared).Timestamp;
            long completedAt = RequireEvent(recorder, EngineEventKind.Completed).Timestamp;
            long firstStepFinishedAt = (recorder.Events.FirstOrDefault(recorded =>
                    recorded.Kind == EngineEventKind.StepFinished
                    && ((EngineStepFinished)recorded.Payload).Outcome.Attributes.Index == 0)
                ?? throw new InvalidOperationException("No StepFinished event was received for step 1."))
                .Timestamp;
            IReadOnlyList<Timestamped<RunProgress>> progress = recorder.TimedProgress;

            WriteStepTable(plan, completed);
            AssertRemainingNullOnlyBeforeFirstPass(progress, firstStepFinishedAt);

            IReadOnlyList<RemainingSample> samples = SampleRemaining(progress, firstStepFinishedAt, completedAt);
            WriteRemainingTable(type, samples);
            AssertRemainingWithinTolerance(samples);

            TimeSpan duration = Stopwatch.GetElapsedTime(preparedAt, completedAt);
            double deviation = (duration - plan.TargetDuration) / plan.TargetDuration;
            _output.WriteLine(Invariant(
                $"Run duration (Prepared → Completed receipt) {duration.TotalSeconds:0.000} s against the {plan.TargetDuration.TotalSeconds:0.###} s target: {deviation:+0.00%;-0.00%} (bound ±{DurationTolerance:0%}); engine elapsed {completed.Elapsed.TotalSeconds:0.000} s; release of {released.ReleasedBytes} B took {released.ReleaseTime.TotalMilliseconds:0} ms (reported, not gated)."));
            Assert.True(
                Math.Abs(deviation) <= DurationTolerance,
                Invariant($"The scaled {type} run took {duration.TotalSeconds:0.000} s from Prepared to Completed, {deviation:+0.00%;-0.00%} from its {plan.TargetDuration.TotalSeconds:0.###} s target; the bound is ±{DurationTolerance:0%}."));
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }

    // Criterion 7: progress reaches the UI thread at least once a second and the dispatcher stays responsive, through
    // the production controller graph, with and without a stalled journal writer. The beat stall ends inside step 1,
    // because the step gate blocks the coordinator, and with it progress, while it waits for a step acknowledgment.
    [Theory(Timeout = 300_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiThread_ControllerRun_StaysResponsiveWithProgressEverySecondAsync(bool stalled)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var engine = new InstrumentedEngine(TestEngines.Create());
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Gib1);
        TestPlan plan = ScaledPlans.Custom((0, _shortStepBudget), (1, _shortStepBudget), (2, _shortStepBudget));
        StalledJournalStorage? storage = stalled ? new StalledJournalStorage() : null;
        var options = new RunControllerOptions
        {
            PlanProvider = _ => plan,
            MaxRegionBytes = regionBytes,
            JournalFactory = storage is null
                ? ProgressJournal.Create
                : (_, timeProvider) => new ProgressJournal(storage, timeProvider),
        };

        using var root = new TempDataRoot();
        using var ui = new DispatcherThread();
        ControllerGraph.Graph graph = await ControllerGraph.CreateInitializedAsync(ui, engine, root.Data, options);
        try
        {
            ResponsivenessProbe probe = ResponsivenessProbe.Start(ui);
            InstrumentedRun run;
            try
            {
                run = await StartControllerRunAsync(ui, graph, engine, TestType.Quick, regionBytes, cancellationToken);
                if (storage is not null)
                {
                    await StallBeatsInsideFirstStepAsync(storage, run, cancellationToken);
                }

                await run.CompletedTask.WaitAsync(CompletionTimeout(plan), cancellationToken);
            }
            finally
            {
                await probe.DisposeAsync();
            }

            await ControllerGraph.WaitForTerminalAndPublishedAsync(ui, graph.Controller, _publicationTimeout);
            ControllerView view = await ReadControllerAsync(ui, graph);

            WriteProbeSummary(probe);
            ProgressGap gap = LargestProgressGap(run);
            _output.WriteLine(Invariant(
                $"Largest progress gap on the UI thread, Prepared → Completed: {gap.Length.TotalMilliseconds:0} ms after {gap.After} (bound {_maxProgressGap.TotalMilliseconds:0} ms), over {run.Progress.Count} progress events."));

            Assert.True(
                view.State == RunState.Passed,
                Invariant($"The run ended {view.State} ({view.EndReason}), expected {RunState.Passed}; detail '{view.EndDetail}', result '{view.ResultMessage}', notice '{view.NoticeMessage}'."));
            Assert.True(
                gap.Length <= _maxProgressGap,
                Invariant($"Progress reached the UI thread {gap.Length.TotalMilliseconds:0} ms apart after {gap.After}; the bound is {_maxProgressGap.TotalMilliseconds:0} ms."));
            Assert.True(
                probe.Samples >= MinimumProbeSamples,
                Invariant($"The dispatcher probe completed {probe.Samples} samples; at least {MinimumProbeSamples} are required."));
            Assert.True(
                probe.Samples == probe.Posted && probe.DrainCompleted,
                Invariant($"The dispatcher serviced {probe.Samples} of {probe.Posted} probe posts (drain completed: {probe.DrainCompleted})."));
            Assert.True(
                probe.MaxLatency <= _maxProbeLatency,
                Invariant($"A dispatcher probe waited {probe.MaxLatency.TotalMilliseconds:0.0} ms from post to run; the bound is {_maxProbeLatency.TotalMilliseconds:0} ms."));

            if (storage is not null)
            {
                AppendCall[] onUiThread = storage.Calls.Where(call => call.ManagedThreadId == ui.ManagedThreadId).ToArray();
                Assert.True(
                    onUiThread.Length == 0,
                    Invariant($"{onUiThread.Length} journal storage call(s) ran on the UI thread (managed thread {ui.ManagedThreadId}), first a '{onUiThread.FirstOrDefault()?.RecordType}' record; journal I/O must never run on the dispatcher."));
            }
        }
        finally
        {
            // A stalled append left blocked by a failed assertion would hold the writer, and the shutdown, back.
            storage?.ClearRules();
            storage?.Release();
            await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
            storage?.Dispose();
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }


    // Criterion 7: Stop halts within 2 s wherever it lands. Release time is measured and reported only.
    [Theory(Timeout = 180_000)]
    [InlineData(StopPoint.Preparing)]
    [InlineData(StopPoint.MidStep)]
    [InlineData(StopPoint.Hold)]
    public async Task StopRequest_DirectSession_CompletesWithinTwoSecondsAsync(StopPoint point)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create(FaultSpec.None);
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Gib1);
        TestPlan plan = point == StopPoint.Hold
            ? ScaledPlans.Custom((HoldQuickStepIndex, _holdStepBudget))
            : ScaledPlans.Custom((0, _shortStepBudget));
        if (point == StopPoint.Hold)
        {
            Assert.True(
                plan.Steps[0].HasHold,
                Invariant($"Step '{plan.Steps[0].Name}' has no hold; the hold stop point needs one."));
        }

        using (var ui = new DispatcherThread())
        using (DirectRun run = await DirectRun.CreateAsync(ui, engine, system, plan, regionBytes))
        {
            run.Start();
            long requestedAt = point switch
            {
                StopPoint.Preparing => RequestUserStop(run),
                StopPoint.MidStep => await StopAfterProgressAsync(run, cancellationToken),
                StopPoint.Hold => await StopInsideHoldAsync(run, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Undefined stop point."),
            };

            EngineCompleted completed = await run.Recorder.WaitCompletedAsync(_stopCompletionTimeout, cancellationToken);
            long completedAt = RequireEvent(run.Recorder, EngineEventKind.Completed).Timestamp;
            EngineReleased released = await run.Recorder.WaitReleasedAsync(_releaseTimeout, cancellationToken);
            TimeSpan latency = Stopwatch.GetElapsedTime(requestedAt, completedAt);

            _output.WriteLine(Invariant(
                $"Stop at {point}: request → Completed receipt {latency.TotalMilliseconds:0} ms (bound {_maxStopLatency.TotalMilliseconds:0} ms); end reason {completed.EndReason}; prepared before the stop: {run.Recorder.Prepared is not null}; steps started {completed.StepResults.Count}; release of {released.ReleasedBytes} B took {released.ReleaseTime.TotalMilliseconds:0} ms (reported, not gated)."));
            Assert.True(
                completed.EndReason == RunEndReason.UserStop,
                Invariant($"The run stopped at {point} ended with {completed.EndReason}, expected {RunEndReason.UserStop}; detail '{completed.EndDetail}'."));
            Assert.True(
                latency <= _maxStopLatency,
                Invariant($"Completed arrived {latency.TotalMilliseconds:0} ms after the stop request at {point}; the bound is {_maxStopLatency.TotalMilliseconds:0} ms."));

            if (point != StopPoint.Preparing)
            {
                // The stop must have interrupted the step it targeted, not landed after it.
                Assert.True(
                    completed.StepResults.Count == 1 && !completed.StepResults[0].Completed,
                    Invariant($"The stop at {point} must interrupt step 1: {completed.StepResults.Count} step result(s), completed = {completed.StepResults.FirstOrDefault()?.Completed}."));
            }

            if (point == StopPoint.Hold)
            {
                // The first pass writes the slice, holds 5 s and only then verifies, so a stop 1 s after the step
                // started lands before that pass completes.
                Assert.True(
                    completed.StepResults[0].PassesCompleted == 0,
                    Invariant($"The hold stop must land inside the first pass's 5 s hold, but {completed.StepResults[0].PassesCompleted} pass(es) completed."));
            }
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }

    // Criterion 7: a stop while the step gate waits for a stalled step-journal acknowledgment still completes within
    // 2 s, through the production controller, and the gated step starts no memory traffic.
    [Fact(Timeout = 180_000)]
    public async Task StopDuringStalledStepJournalWait_CompletesWithinTwoSecondsAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var engine = new InstrumentedEngine(TestEngines.Create());
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Gib1);
        TestPlan plan = ScaledPlans.Custom((0, _shortStepBudget));

        // Only the first step record stalls: begin passes, so Start is accepted and the run enters Preparing.
        var storage = new StalledJournalStorage();
        storage.BlockNext(StepRecord);
        var options = new RunControllerOptions
        {
            PlanProvider = _ => plan,
            MaxRegionBytes = regionBytes,
            JournalFactory = (_, timeProvider) => new ProgressJournal(storage, timeProvider),
        };

        using var root = new TempDataRoot();
        using var ui = new DispatcherThread();
        ControllerGraph.Graph graph = await ControllerGraph.CreateInitializedAsync(ui, engine, root.Data, options);
        try
        {
            InstrumentedRun run = await StartControllerRunAsync(ui, graph, engine, TestType.Quick, regionBytes, cancellationToken);
            bool blocked = await Task.Run(() => storage.WaitForBlocked(_blockedAppendTimeout), cancellationToken);
            AppendCall? pending = storage.Calls.LastOrDefault(call => call.Outcome == AppendOutcome.Pending);
            Assert.True(
                blocked && pending?.RecordType == StepRecord,
                Invariant($"The step record did not stall within {_blockedAppendTimeout.TotalSeconds:0} s (blocked: {blocked}, pending record '{pending?.RecordType}')."));

            // The gate gives up on its acknowledgment after 5 s, so the stop is requested as soon as the stall is seen.
            long requestedAt = await ui.InvokeAsync(() =>
            {
                long at = Stopwatch.GetTimestamp();
                graph.Controller.RequestStop();
                return at;
            });

            await ControllerGraph.WaitUntilAsync(
                ui,
                graph.Controller,
                () => graph.StateMachine.State.IsTerminal(),
                _stopCompletionTimeout,
                "the terminal state after the stop");

            // Released at once, so the acknowledged result record behind the stalled step record is not delayed.
            storage.Release();

            EngineCompleted completed = await run.CompletedTask.WaitAsync(_stopCompletionTimeout, cancellationToken);
            long completedAt = run.CompletedAt ?? throw new InvalidOperationException("Completed has no receipt timestamp.");
            TimeSpan latency = Stopwatch.GetElapsedTime(requestedAt, completedAt);
            ControllerView view = await ReadControllerAsync(ui, graph);
            ulong independent = run.IndependentVerifiedBytes;

            _output.WriteLine(Invariant(
                $"Stop during the stalled step-journal wait: request → Completed receipt {latency.TotalMilliseconds:0} ms (bound {_maxStopLatency.TotalMilliseconds:0} ms); state {view.State} ({view.EndReason}); steps completed {completed.StepsCompleted}, started {completed.StepResults.Count}; independently verified {independent} B."));
            Assert.True(
                view.State == RunState.Aborted && view.EndReason == RunEndReason.UserStop,
                Invariant($"The run ended {view.State} ({view.EndReason}), expected {RunState.Aborted} ({RunEndReason.UserStop}); detail '{view.EndDetail}', result '{view.ResultMessage}'."));
            Assert.True(
                latency <= _maxStopLatency,
                Invariant($"The terminal state arrived {latency.TotalMilliseconds:0} ms after the stop request; the bound is {_maxStopLatency.TotalMilliseconds:0} ms."));
            Assert.True(
                completed.StepsCompleted == 0 && completed.StepResults.Count == 0 && independent == 0,
                Invariant($"The gated step must start no traffic: {completed.StepsCompleted} step(s) completed, {completed.StepResults.Count} started, {independent} B verified."));

            await ControllerGraph.WaitForTerminalAndPublishedAsync(ui, graph.Controller, _publicationTimeout);
        }
        finally
        {
            storage.ClearRules();
            storage.Release();
            await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
            storage.Dispose();
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }

    // Criterion 5 at full length: the shipped plans on 1 GiB reach their coverage targets. About 85 minutes in all, so
    // excluded from the default runs by the VSTest filter Category!=Long.
    [Theory(Timeout = 6_000_000)]
    [Trait("Category", "Long")]
    [InlineData(TestType.Quick)]
    [InlineData(TestType.Standard)]
    [InlineData(TestType.Exhaustive)]
    public async Task FullLengthPlan_OneGibibyte_PassesAndReachesCoverageTargetAsync(TestType type)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TestPlan plan = TestPlans.Get(type);
        var engine = new InstrumentedEngine(TestEngines.Create());
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Gib1);
        var options = new RunControllerOptions
        {
            PlanProvider = TestPlans.Get,
            MaxRegionBytes = regionBytes,
        };

        using var root = new TempDataRoot();
        using var ui = new DispatcherThread();
        ControllerGraph.Graph graph = await ControllerGraph.CreateInitializedAsync(ui, engine, root.Data, options);
        try
        {
            InstrumentedRun run = await StartControllerRunAsync(ui, graph, engine, type, regionBytes, cancellationToken);
            EngineCompleted completed = await run.CompletedTask.WaitAsync(LongRunBound(type), cancellationToken);
            await ControllerGraph.WaitForTerminalAndPublishedAsync(ui, graph.Controller, _publicationTimeout);
            ControllerView view = await ReadControllerAsync(ui, graph);
            ulong independent = run.IndependentVerifiedBytes;

            IReadOnlyList<string> summaries = root.SummaryJsonFiles();
            Assert.True(
                summaries.Count == 1,
                Invariant($"Expected one published JSON summary in '{root.LogsPath}', found {summaries.Count}."));
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(summaries[0], cancellationToken));
            JsonElement summary = document.RootElement;
            string? verdict = ReadPath(summary, "run.verdict").GetString();
            bool targetReached = ReadPath(summary, "coverage.targetReached").GetBoolean();
            double coveragePercent = ReadPath(summary, "coverage.percent").GetDouble();
            double targetPercent = ReadPath(summary, "coverage.targetPercent").GetDouble();
            double durationSeconds = ReadPath(summary, "run.durationSeconds").GetDouble();
            double targetSeconds = plan.TargetDuration.TotalSeconds;
            double durationDeviation = (durationSeconds - targetSeconds) / targetSeconds;

            WriteStepTable(plan, completed);
            _output.WriteLine(Invariant(
                $"{type} on {completed.VerifiedBytes} verified of {regionBytes} B: verdict {verdict}; coverage {coveragePercent:0.0} % against the {targetPercent:0} % target (reached: {targetReached}); duration {durationSeconds:0.0} s against {targetSeconds:0} s ({durationDeviation:+0.00%;-0.00%}, bound ±{DurationTolerance:0%}); reported {completed.VerifiedBytes} B against {independent} B counted independently."));

            Assert.True(
                view.State == RunState.Passed && verdict == PassVerdict,
                Invariant($"The {type} run ended {view.State} ({view.EndReason}) with summary verdict '{verdict}', expected {PassVerdict}; detail '{view.EndDetail}'."));
            Assert.True(
                targetReached && coveragePercent >= targetPercent,
                Invariant($"The {type} run reached {coveragePercent:0.0} % coverage against its {targetPercent:0} % target (targetReached {targetReached})."));
            AssertWithinOnePercent(completed.VerifiedBytes, independent, Invariant($"the {type} run's reported verified bytes"));
            Assert.True(
                Math.Abs(durationDeviation) <= DurationTolerance,
                Invariant($"The {type} run took {durationSeconds:0.0} s, {durationDeviation:+0.00%;-0.00%} from its {targetSeconds:0} s target; the bound is ±{DurationTolerance:0%}."));
        }
        finally
        {
            await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
        }

        await AssertNoLiveSessionsAsync(cancellationToken);
    }


    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    // A plan's target plus a generous allowance for preparation, scheduling slack and a loaded host; it only bounds a
    // broken run, the duration bound itself is asserted separately.
    private static TimeSpan CompletionTimeout(TestPlan plan) => plan.TargetDuration + plan.TargetDuration + _completionAllowance;

    // Each full-length plan has its own bound, all inside the theory's 100-minute timeout.
    private static TimeSpan LongRunBound(TestType type) => type switch
    {
        TestType.Quick => TimeSpan.FromMinutes(15),
        TestType.Standard => TimeSpan.FromMinutes(40),
        TestType.Exhaustive => TimeSpan.FromMinutes(95),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Undefined test type."),
    };

    private static double Deviation(ulong reported, ulong counted) =>
        reported >= counted ? reported - counted : counted - reported;

    private static void AssertCleanCompletion(EngineCompleted completed, string scenario)
    {
        Assert.True(
            completed.EndReason == RunEndReason.Completed && completed.TotalErrors == 0,
            Invariant($"{scenario} must complete cleanly, but it ended with {completed.EndReason} and {completed.TotalErrors} error(s); detail '{completed.EndDetail}'."));
    }

    private static void AssertWithinOnePercent(ulong reported, ulong counted, string what)
    {
        Assert.True(counted > 0, Invariant($"The counting layer counted no verified bytes for {what}."));
        double deviation = Deviation(reported, counted);
        double allowance = CoverageTolerance * counted;
        Assert.True(
            deviation <= allowance,
            Invariant($"{what}: {reported} B reported against {counted} B counted independently, a deviation of {deviation:0} B ({deviation / counted:0.0000%}); the bound is {CoverageTolerance:0%} ({allowance:0} B)."));
    }

    private static RecordedEngineEvent RequireEvent(EngineRunRecorder recorder, EngineEventKind kind) =>
        recorder.FirstEvent(kind) ?? throw new InvalidOperationException(Invariant($"No {kind} event was received."));

    private static JsonElement ReadPath(JsonElement root, string path)
    {
        JsonElement current = root;
        foreach (string name in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out JsonElement next))
            {
                throw new InvalidOperationException($"The summary has no '{path}' value.");
            }

            current = next;
        }

        return current;
    }

    private void AssertCoverageMatchesIndependentCount(
        EngineRunRecorder recorder,
        EngineCompleted completed,
        ulong counted,
        string scenario)
    {
        EnginePrepared prepared = recorder.Prepared
            ?? throw new InvalidOperationException(Invariant($"The {scenario} raised no Prepared event."));
        ulong stepSum = 0;
        foreach (StepOutcome outcome in completed.StepResults)
        {
            stepSum = checked(stepSum + outcome.VerifiedBytes);
        }

        IReadOnlyList<RunProgress> progress = recorder.Progress;
        _output.WriteLine(Invariant($"Coverage accounting, {scenario}, {prepared.TestedBytes} B tested by {prepared.WorkerCount} workers:"));
        _output.WriteLine("  figure                         verified bytes   deviation from counted   bound");
        _output.WriteLine(Invariant($"  counting layer (independent)  {counted,16}   {"—",22}   —"));
        _output.WriteLine(Invariant($"  Completed.VerifiedBytes       {completed.VerifiedBytes,16}   {Deviation(completed.VerifiedBytes, counted) / Math.Max(1UL, counted),22:0.0000%}   ±{CoverageTolerance:0%}"));
        _output.WriteLine(Invariant($"  sum of step results           {stepSum,16}   {Deviation(stepSum, counted) / Math.Max(1UL, counted),22:0.0000%}   ±{CoverageTolerance:0%}"));
        _output.WriteLine(Invariant($"  coverage {RunProgress.CoveragePercentOf(completed.VerifiedBytes, prepared.TestedBytes):0.0} % over {completed.StepResults.Count} step(s), {progress.Count} progress snapshots."));

        AssertWithinOnePercent(completed.VerifiedBytes, counted, Invariant($"Completed.VerifiedBytes of the {scenario}"));
        AssertWithinOnePercent(stepSum, counted, Invariant($"the sum of the step results' verified bytes of the {scenario}"));

        Assert.True(progress.Count > 0, Invariant($"The {scenario} raised no progress."));
        RunProgress last = progress[^1];
        Assert.True(
            last.VerifiedBytes <= completed.VerifiedBytes,
            Invariant($"The last progress of the {scenario} reported {last.VerifiedBytes} B verified, more than Completed's {completed.VerifiedBytes} B."));

        foreach (RunProgress snapshot in progress)
        {
            Assert.True(
                snapshot.RegionBytes == prepared.TestedBytes,
                Invariant($"A progress snapshot of step {snapshot.StepIndex + 1} reported {snapshot.RegionBytes} B under test, but Prepared reported {prepared.TestedBytes} B."));
            double expected = 100.0 * snapshot.VerifiedBytes / prepared.TestedBytes;
            double error = Math.Abs(snapshot.CoveragePercent - expected);
            Assert.True(
                error <= CoveragePercentRelativeTolerance * expected,
                Invariant($"A progress snapshot of step {snapshot.StepIndex + 1} reported {snapshot.CoveragePercent:R} % coverage for {snapshot.VerifiedBytes} of {prepared.TestedBytes} B, where 100 × verified ÷ tested is {expected:R} %; the relative bound is {CoveragePercentRelativeTolerance:0e0}."));
        }
    }

    // Null ("calculating…") is allowed only while step 1's first pass runs, and never once step 1 has finished.
    private static void AssertRemainingNullOnlyBeforeFirstPass(
        IReadOnlyList<Timestamped<RunProgress>> progress,
        long firstStepFinishedAt)
    {
        foreach (Timestamped<RunProgress> sample in progress)
        {
            RunProgress value = sample.Value;
            if (value.Remaining is not null)
            {
                continue;
            }

            Assert.True(
                value.StepIndex == 0 && value.PassIndex == 0,
                Invariant($"Remaining was null (calculating…) in step {value.StepIndex + 1}, pass {value.PassIndex + 1}; it may be null only before step 1's first pass completes."));
            Assert.True(
                sample.Timestamp < firstStepFinishedAt,
                Invariant($"Remaining was null in a progress snapshot received {Stopwatch.GetElapsedTime(firstStepFinishedAt, sample.Timestamp).TotalMilliseconds:0} ms after step 1 finished."));
        }
    }

    // One sample per wall-clock second after step 1's StepFinished: the latest progress received in that second, with
    // the actual remaining time measured to the Completed receipt.
    private static IReadOnlyList<RemainingSample> SampleRemaining(
        IReadOnlyList<Timestamped<RunProgress>> progress,
        long firstStepFinishedAt,
        long completedAt)
    {
        var samples = new List<RemainingSample>();
        foreach (IGrouping<long, Timestamped<RunProgress>> second in progress
            .Where(sample => sample.Timestamp > firstStepFinishedAt && sample.Timestamp < completedAt)
            .GroupBy(sample => sample.Since(firstStepFinishedAt).Ticks / _sampleInterval.Ticks))
        {
            Timestamped<RunProgress> latest = second.Last();
            RunProgress value = latest.Value;
            TimeSpan remaining = value.Remaining
                ?? throw new InvalidOperationException(Invariant(
                    $"Remaining was null in step {value.StepIndex + 1} after step 1 finished; it must be non-null once step 1's first pass has completed."));
            TimeSpan actual = Stopwatch.GetElapsedTime(latest.Timestamp, completedAt);
            samples.Add(new RemainingSample(second.Key + 1, value.StepIndex, value.PassIndex, remaining, actual));
        }

        return samples;
    }

    private void WriteRemainingTable(TestType type, IReadOnlyList<RemainingSample> samples)
    {
        _output.WriteLine(Invariant(
            $"Time remaining, scaled {type}: one sample per second after step 1 finished; gated (±{RemainingTolerance:0%}) where the actual remaining time is at least {_remainingGateFloor.TotalSeconds:0} s, reported only below that."));
        _output.WriteLine("  second  step  pass  remaining s    actual s     error s    error %  gated  result");
        foreach (RemainingSample sample in samples)
        {
            string result = !sample.IsGated ? "reported" : sample.IsWithinTolerance ? "ok" : "OUT OF BOUND";
            _output.WriteLine(Invariant(
                $"  {sample.Second,6}  {sample.StepIndex + 1,4}  {sample.PassIndex + 1,4}  {sample.Remaining.TotalSeconds,11:0.000}  {sample.Actual.TotalSeconds,10:0.000}  {sample.Error.TotalSeconds,10:+0.000;-0.000}  {sample.RelativeError,9:+0.00%;-0.00%}  {(sample.IsGated ? "yes" : "no"),5}  {result}"));
        }
    }

    private static void AssertRemainingWithinTolerance(IReadOnlyList<RemainingSample> samples)
    {
        RemainingSample[] gated = samples.Where(sample => sample.IsGated).ToArray();
        Assert.True(
            gated.Length > 0,
            Invariant($"None of the {samples.Count} per-second samples after step 1 had at least {_remainingGateFloor.TotalSeconds:0} s actually remaining, so the ±{RemainingTolerance:0%} bound was never checked."));

        RemainingSample[] outOfBound = gated.Where(sample => !sample.IsWithinTolerance).ToArray();
        Assert.True(
            outOfBound.Length == 0,
            Invariant($"{outOfBound.Length} of {gated.Length} gated samples deviated beyond ±{RemainingTolerance:0%}: ")
                + string.Join("; ", outOfBound.Select(sample => Invariant(
                    $"second {sample.Second}: remaining {sample.Remaining.TotalSeconds:0.000} s against {sample.Actual.TotalSeconds:0.000} s actual ({sample.RelativeError:+0.00%;-0.00%})"))));
    }

    private void WriteStepTable(TestPlan plan, EngineCompleted completed)
    {
        _output.WriteLine(Invariant($"Steps of the {plan.Type} plan ({plan.Steps.Count} steps, {plan.TargetDuration.TotalSeconds:0.###} s target):"));
        _output.WriteLine("  step  name                               planned s   actual s  overrun s  passes     verified bytes  completed");
        foreach (StepOutcome outcome in completed.StepResults)
        {
            StepAttributes step = outcome.Attributes;
            _output.WriteLine(Invariant(
                $"  {step.Index + 1,4}  {step.Name,-33}  {step.TimeBudget.TotalSeconds,9:0.000}  {outcome.ActualDuration.TotalSeconds,9:0.000}  {outcome.Overrun.TotalSeconds,9:0.000}  {outcome.PassesCompleted,6}  {outcome.VerifiedBytes,17}  {outcome.Completed}"));
        }
    }

    private void WriteProbeSummary(ResponsivenessProbe probe)
    {
        TimeSpan[] latencies = probe.Latencies.Order().ToArray();
        TimeSpan Percentile(double fraction) => latencies.Length == 0
            ? TimeSpan.Zero
            : latencies[Math.Min(latencies.Length - 1, (int)Math.Ceiling(fraction * latencies.Length) - 1)];

        _output.WriteLine(Invariant(
            $"Dispatcher probe every {ResponsivenessProbe.Interval.TotalMilliseconds:0} ms: {probe.Samples} of {probe.Posted} posts serviced; post → run latency median {Percentile(0.50).TotalMilliseconds:0.0} ms, p99 {Percentile(0.99).TotalMilliseconds:0.0} ms, max {probe.MaxLatency.TotalMilliseconds:0.0} ms (bound {_maxProbeLatency.TotalMilliseconds:0} ms, at least {MinimumProbeSamples} samples)."));
    }

    // The gaps between consecutive UI-thread receipts from Prepared, through every progress event, to Completed.
    private static ProgressGap LargestProgressGap(InstrumentedRun run)
    {
        long preparedAt = run.PreparedAt ?? throw new InvalidOperationException("The run raised no Prepared event.");
        long completedAt = run.CompletedAt ?? throw new InvalidOperationException("The run raised no Completed event.");

        var receipts = new List<(long Timestamp, string Label)> { (preparedAt, "Prepared") };
        foreach (Timestamped<RunProgress> sample in run.Progress)
        {
            if (sample.Timestamp > preparedAt && sample.Timestamp < completedAt)
            {
                receipts.Add((sample.Timestamp, Invariant(
                    $"the progress of step {sample.Value.StepIndex + 1}, pass {sample.Value.PassIndex + 1} at {sample.Since(preparedAt).TotalSeconds:0.000} s")));
            }
        }

        receipts.Add((completedAt, "Completed"));
        var largest = new ProgressGap(TimeSpan.Zero, "Prepared");
        for (int i = 1; i < receipts.Count; i++)
        {
            TimeSpan gap = Stopwatch.GetElapsedTime(receipts[i - 1].Timestamp, receipts[i].Timestamp);
            if (gap > largest.Length)
            {
                largest = new ProgressGap(gap, receipts[i - 1].Label);
            }
        }

        return largest;
    }


    // Starts a run through the controller and returns the engine session's record. A session that is never created
    // means the controller rejected its own engine configuration for the capped region.
    private static async Task<InstrumentedRun> StartControllerRunAsync(
        DispatcherThread ui,
        ControllerGraph.Graph graph,
        InstrumentedEngine engine,
        TestType type,
        ulong regionBytes,
        CancellationToken cancellationToken)
    {
        StartResult start = await ui.InvokeAsync(() => graph.Controller.TryStart(type, stopOnError: false));
        if (start != StartResult.Accepted)
        {
            ControllerView refused = await ReadControllerAsync(ui, graph);
            Assert.Fail(Invariant($"TryStart({type}) returned {start}, expected {StartResult.Accepted}; notice '{refused.NoticeMessage}'."));
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (engine.LastRun is { } run)
            {
                return run;
            }

            ControllerView view = await ReadControllerAsync(ui, graph);
            if (view.State.IsTerminal() || (view.State == RunState.Idle && view.CanStart))
            {
                throw new InvalidOperationException(Invariant(
                    $"RunController created no engine session for the capped region of {regionBytes} B: state {view.State}, end reason {view.EndReason?.ToString() ?? "none"}, detail '{view.EndDetail}', notice '{view.NoticeMessage}'. Small regions through RunController require RunControllerOptions.MaxRegionBytes to also lower EngineSessionOptions.MinRegionBytes to the capped region, which whole-slice rounding can leave below the {EngineSessionOptions.DefaultMinRegionBytes} B default."));
            }

            if (stopwatch.Elapsed >= _sessionCreationTimeout)
            {
                throw new TimeoutException(Invariant(
                    $"RunController created no engine session within {_sessionCreationTimeout.TotalSeconds:0} s of an accepted Start; state {view.State}, post-run activity {view.PostRunActivity}."));
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static Task<ControllerView> ReadControllerAsync(DispatcherThread ui, ControllerGraph.Graph graph) =>
        ui.InvokeAsync(() => new ControllerView(
            graph.StateMachine.State,
            graph.StateMachine.EndReason,
            graph.StateMachine.EndDetail,
            graph.Controller.ResultMessage,
            graph.Controller.NoticeMessage,
            graph.Controller.CanStart,
            graph.Controller.PostRunActivity));

    // Stalls the journal writer on the beat after the first one for 2 s, then lets it go, all inside step 1: beats are
    // unacknowledged, so the coordinator, and with it progress, never waits on them.
    private async Task StallBeatsInsideFirstStepAsync(
        StalledJournalStorage storage,
        InstrumentedRun run,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!storage.RecordTypesWritten().Contains(BeatRecord))
        {
            if (stopwatch.Elapsed >= _firstBeatTimeout)
            {
                throw new TimeoutException(Invariant(
                    $"No beat record was written within {_firstBeatTimeout.TotalSeconds:0} s of Start; records written: {string.Join(", ", storage.RecordTypesWritten())}."));
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }

        storage.BlockNext(BeatRecord);
        bool blocked = await Task.Run(() => storage.WaitForBlocked(_blockedAppendTimeout), cancellationToken);
        Assert.True(blocked, Invariant($"The second beat record did not stall within {_blockedAppendTimeout.TotalSeconds:0} s."));
        long stalledAt = Stopwatch.GetTimestamp();
        RunProgress? atStall = run.LatestProgress;

        await Task.Delay(_beatStall, cancellationToken);
        RunProgress? atRelease = run.LatestProgress;
        storage.Release();
        TimeSpan held = Stopwatch.GetElapsedTime(stalledAt);

        _output.WriteLine(Invariant(
            $"Journal writer stalled on a beat for {held.TotalMilliseconds:0} ms, from step {atStall?.StepIndex + 1} at {atStall?.Elapsed.TotalSeconds:0.000} s to step {atRelease?.StepIndex + 1} at {atRelease?.Elapsed.TotalSeconds:0.000} s of test time."));
        Assert.True(
            atStall is { StepIndex: 0 } && atRelease is { StepIndex: 0 },
            Invariant($"The beat stall must begin and end inside step 1, so the step 2 acknowledgment is not delayed; it ran from step {atStall?.StepIndex + 1} to step {atRelease?.StepIndex + 1}."));
    }

    private static long RequestUserStop(DirectRun run)
    {
        long requestedAt = Stopwatch.GetTimestamp();
        run.Session.RequestStop(StopReason.User);
        return requestedAt;
    }

    private static async Task<long> StopAfterProgressAsync(DirectRun run, CancellationToken cancellationToken)
    {
        await WaitForEventAsync(
            run.Recorder,
            recorded => recorded.Kind == EngineEventKind.ProgressChanged && ((RunProgress)recorded.Payload).StepIndex == 0,
            MidStepProgressOccurrence,
            Invariant($"progress snapshot {MidStepProgressOccurrence} of step 1"),
            cancellationToken);
        return RequestUserStop(run);
    }

    private static async Task<long> StopInsideHoldAsync(DirectRun run, CancellationToken cancellationToken)
    {
        RecordedEngineEvent started = await WaitForEventAsync(
            run.Recorder,
            recorded => recorded.Kind == EngineEventKind.StepStarted && ((EngineStepStarted)recorded.Payload).Index == 0,
            occurrence: 1,
            "the StepStarted event of the hold step",
            cancellationToken);
        await DelayUntilAsync(started.Timestamp, _holdStopDelay, cancellationToken);
        return RequestUserStop(run);
    }

    // Polls the recorder for the occurrence-th event that matches; fails fast when the run completes first.
    private static async Task<RecordedEngineEvent> WaitForEventAsync(
        EngineRunRecorder recorder,
        Func<RecordedEngineEvent, bool> match,
        int occurrence,
        string description,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            RecordedEngineEvent? found = recorder.Events.Where(match).Skip(occurrence - 1).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }

            if (recorder.Completed is { } completed)
            {
                throw new InvalidOperationException(Invariant(
                    $"The run completed ({completed.EndReason}, detail '{completed.EndDetail}') before {description}."));
            }

            if (stopwatch.Elapsed >= _eventTimeout)
            {
                throw new TimeoutException(Invariant($"Timed out after {_eventTimeout.TotalSeconds:0} s waiting for {description}."));
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static async Task DelayUntilAsync(long fromTimestamp, TimeSpan delay, CancellationToken cancellationToken)
    {
        TimeSpan wait = delay - Stopwatch.GetElapsedTime(fromTimestamp);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    // A session disposed on the thread pool by the controller's publication pipeline may still be finishing its
    // release, so the count is given a few seconds to settle before it is asserted.
    private static async Task AssertNoLiveSessionsAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int live = SessionProbe.LiveSessionCount();
        while (live != 0 && stopwatch.Elapsed < _liveSessionSettleTimeout)
        {
            await Task.Delay(_pollInterval, cancellationToken);
            live = SessionProbe.LiveSessionCount();
        }

        Assert.True(
            live == 0,
            Invariant($"{live} engine session(s) were still alive {_liveSessionSettleTimeout.TotalSeconds:0} s after the test released its sessions."));
    }

    /// <summary>One per-second time-remaining sample.</summary>
    /// <param name="Second">The 1-based wall-clock second after step 1 finished in which the sample was received.</param>
    /// <param name="StepIndex">The zero-based step of the snapshot.</param>
    /// <param name="PassIndex">The zero-based pass of the snapshot.</param>
    /// <param name="Remaining">The engine's estimate.</param>
    /// <param name="Actual">The time from the snapshot's receipt to the Completed receipt.</param>
    private sealed record RemainingSample(long Second, int StepIndex, int PassIndex, TimeSpan Remaining, TimeSpan Actual)
    {
        public TimeSpan Error => Remaining - Actual;

        public double RelativeError => Error / Actual;

        public bool IsGated => Actual >= _remainingGateFloor;

        public bool IsWithinTolerance => Math.Abs(Error.Ticks) <= RemainingTolerance * Actual.Ticks;
    }

    /// <summary>The largest interval between consecutive UI-thread receipts of a run.</summary>
    /// <param name="Length">The interval.</param>
    /// <param name="After">The receipt that began it.</param>
    private sealed record ProgressGap(TimeSpan Length, string After);

    /// <summary>A snapshot of the controller graph, read on the dispatcher thread.</summary>
    private readonly record struct ControllerView(
        RunState State,
        RunEndReason? EndReason,
        string? EndDetail,
        string? ResultMessage,
        string? NoticeMessage,
        bool CanStart,
        PostRunActivity PostRunActivity);


    /// <summary>
    /// One engine session created directly through <see cref="IEngine.CreateSession"/>, with an
    /// <see cref="EngineRunRecorder"/> attached before it starts.
    /// </summary>
    private sealed class DirectRun : IDisposable
    {
        private DirectRun(IEngineSession session, EngineRunRecorder recorder)
        {
            Session = session;
            Recorder = recorder;
        }

        /// <summary>Gets the session.</summary>
        public IEngineSession Session { get; }

        /// <summary>Gets the recorder of every event the session raises.</summary>
        public EngineRunRecorder Recorder { get; }

        /// <summary>
        /// Creates the session for <paramref name="plan"/> over a region of <paramref name="regionBytes"/>, one worker per
        /// logical processor. The session is created on the dispatcher thread, so it captures that thread's context and
        /// raises every event there, where the application's UI receives them.
        /// </summary>
        /// <param name="ui">The dispatcher thread whose context receives the events.</param>
        /// <param name="engine">The engine that creates the session.</param>
        /// <param name="system">The machine, for its logical processor count.</param>
        /// <param name="plan">The plan to run.</param>
        /// <param name="regionBytes">The region size, also the shrink-and-retry floor so the size never changes.</param>
        /// <returns>The run, not yet started.</returns>
        public static Task<DirectRun> CreateAsync(
            DispatcherThread ui,
            IEngine engine,
            SystemSnapshot system,
            TestPlan plan,
            ulong regionBytes)
        {
            var options = new EngineSessionOptions
            {
                RegionBytes = regionBytes,
                MinRegionBytes = regionBytes,
                WorkerCount = system.LogicalProcessors,
                Steps = ScaledPlans.Attributes(plan),
                Seed = RunSeed,
            };

            return ui.InvokeAsync(() =>
            {
                IEngineSession session = engine.CreateSession(options);
                try
                {
                    return new DirectRun(session, EngineRunRecorder.Attach(session));
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            });
        }

        /// <summary>Starts the session's coordinator.</summary>
        public void Start() => Session.Start();

        /// <summary>
        /// Disposes the session: stops a run still in progress, joins the engine threads and releases the locked region.
        /// </summary>
        public void Dispose() => Session.Dispose();
    }

    /// <summary>
    /// Measures UI-thread responsiveness. A dedicated thread posts a work item to the dispatcher every 100 ms of real
    /// time with <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/> at
    /// <see cref="DispatcherPriority.Normal"/>, the priority at which the dispatcher's synchronization context delivers
    /// engine events, and each work item records how long it waited from post to run.
    /// </summary>
    /// <remarks>
    /// Every member may be called from any thread. Disposal stops the posting thread and then drains every outstanding
    /// post, so <see cref="Samples"/> equals <see cref="Posted"/> once <see cref="DrainCompleted"/> is true.
    /// </remarks>
    private sealed class ResponsivenessProbe : IAsyncDisposable
    {
        private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(10);

        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stopSignal = new(initialState: false);
        private readonly TaskCompletionSource _threadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private readonly List<TimeSpan> _latencies = new();
        private int _posted;
        private int _disposed;
        private volatile bool _drainCompleted;

        private ResponsivenessProbe(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RamQuick responsiveness probe",
            };
        }

        /// <summary>Gets the interval between posts: 100 ms.</summary>
        public static TimeSpan Interval { get; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Gets the number of work items posted so far.</summary>
        public int Posted => Volatile.Read(ref _posted);

        /// <summary>Gets the number of posted work items that have run.</summary>
        public int Samples
        {
            get
            {
                lock (_sync)
                {
                    return _latencies.Count;
                }
            }
        }

        /// <summary>Gets the longest post → run latency so far; zero before the first sample.</summary>
        public TimeSpan MaxLatency
        {
            get
            {
                lock (_sync)
                {
                    return _latencies.Count == 0 ? TimeSpan.Zero : _latencies.Max();
                }
            }
        }

        /// <summary>Gets every post → run latency so far, in the order the work items ran.</summary>
        public IReadOnlyList<TimeSpan> Latencies
        {
            get
            {
                lock (_sync)
                {
                    return _latencies.ToArray();
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether disposal saw the posting thread end and every outstanding post run within
        /// 10 s each.
        /// </summary>
        public bool DrainCompleted => _drainCompleted;

        /// <summary>Starts probing the dispatcher of <paramref name="ui"/>.</summary>
        /// <param name="ui">The dispatcher thread to probe.</param>
        /// <returns>The running probe; dispose it to stop.</returns>
        public static ResponsivenessProbe Start(DispatcherThread ui)
        {
            ArgumentNullException.ThrowIfNull(ui);
            var probe = new ResponsivenessProbe(ui.Dispatcher);
            probe._thread.Start();
            return probe;
        }

        /// <summary>
        /// Stops posting, then waits for every outstanding post to run. Idempotent; never throws for a stalled
        /// dispatcher, which <see cref="DrainCompleted"/> reports instead.
        /// </summary>
        /// <returns>A task that completes when the probe has stopped and drained, or the drain has timed out.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopSignal.Set();
            if (!await CompletesWithinAsync(_threadEnded.Task, _drainTimeout))
            {
                return;
            }

            _stopSignal.Dispose();

            // Work items of one priority run in post order, so this marker runs after every probe post.
            DispatcherOperation marker = _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Normal);
            _drainCompleted = await CompletesWithinAsync(marker.Task, _drainTimeout);
        }

        private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            Task first = await Task.WhenAny(task, Task.Delay(timeout));
            return first == task && task.IsCompletedSuccessfully;
        }

        private void Run()
        {
            try
            {
                long origin = Stopwatch.GetTimestamp();
                long nextTick = 1;
                while (true)
                {
                    TimeSpan wait = (Interval * nextTick) - Stopwatch.GetElapsedTime(origin);
                    if (_stopSignal.Wait(wait > TimeSpan.Zero ? wait : TimeSpan.Zero))
                    {
                        return;
                    }

                    Post();

                    // A late wake-up skips the ticks it missed rather than posting a burst to catch up.
                    nextTick = Math.Max(nextTick + 1, (Stopwatch.GetElapsedTime(origin).Ticks / Interval.Ticks) + 1);
                }
            }
            finally
            {
                _threadEnded.TrySetResult();
            }
        }

        private void Post()
        {
            long postedAt = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _posted);
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Record(Stopwatch.GetElapsedTime(postedAt))));
        }

        private void Record(TimeSpan latency)
        {
            lock (_sync)
            {
                _latencies.Add(latency);
            }
        }
    }
}

