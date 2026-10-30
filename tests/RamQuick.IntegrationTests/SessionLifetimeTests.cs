using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RamQuick.App.Services;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// Abandonment tests of the session ownership graph: no root a run creates (the native <c>GCHandle</c>, the journal
/// writer thread and its heartbeat timer, the work items posted to the UI dispatcher) may have a strong path to an
/// <c>EngineSession</c> or to its owner. A session whose owner lets it go undisposed must therefore be collected, its
/// <c>SafeSessionHandle</c>'s critical finalizer must destroy the native session, releasing the locked region, and free
/// the <c>GCHandle</c>, after which the <c>CallbackState</c> is collected too. A disposed session must be destroyed the
/// same way when a callback still holds its handle, so that the last release runs on the coordinator.
/// </summary>
/// <remarks>
/// <para>
/// Collection is observed through weak references only. Each graph is built and started inside
/// <see cref="MethodImplOptions.NoInlining"/> synchronous helpers whose strong references go into a
/// <see cref="KeepAlive"/>, the only strong holder while the test waits for callbacks. Debug builds keep a method's
/// locals alive until it returns and hoist every local of an async method into its state machine, so a strong local in
/// a test method would itself keep the graph alive.
/// </para>
/// <para>
/// A failure names each object that is still reachable, which points at the edge that roots it: a
/// <c>CallbackState</c> holding its session strongly keeps "session" alive, a journal writer referencing its
/// <c>ProgressJournal</c> keeps "journal" alive, and a callback thunk table holding delegates keeps their targets,
/// "controller" or "gate target", alive.
/// </para>
/// <para>
/// <c>rq_test_live_session_count</c> reaching 0 proves that <c>rq_session_destroy</c> ran for the abandoned session.
/// Neither abandonment test disposes or shuts down what it abandons: that would replace the finalizer path under test.
/// </para>
/// </remarks>
public sealed class SessionLifetimeTests
{
    private const int PlanStepCount = 3;

    // "Callbacks in flight": the first step's gate has run and progress is flowing through the sink.
    private const int RequiredGateCalls = 1;
    private const int RequiredProgressSinkCalls = 3;

    // Long enough that the run is still in its first or second step when it is abandoned and collected.
    private static readonly TimeSpan _stepBudget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan _callbackTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _collectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _collectionPause = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _settleTimeout = TimeSpan.FromSeconds(10);

    // The callback-held handle race: how long the gate keeps holding the handle for the concurrent Dispose to dispose
    // it, how often it looks, and how long Dispose may take to stop, join and release the region once the gate lets go.
    private static readonly TimeSpan _heldHandleDisposeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _heldHandlePollInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan _disposeTimeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public SessionLifetimeTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    public async Task ProductionGraph_Abandoned_IsCollectedAndDestroyedAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertNoLiveSessionAsync();

        using var ui = new DispatcherThread();

        // The abandoned journal's writer thread is a background thread that may still hold the journal file open when
        // the test ends; it holds no run object, so that is expected, and deleting the folder is best effort.
        using var root = new TempDataRoot();
        DataDirectory data = root.Data;
        var keepAlive = new KeepAlive();
        var journalProbe = new JournalProbe();

        var tracked = new TrackedReferences();
        try
        {
            ProductionGraphProbe probe = await ui.InvokeAsync(() => BuildProductionGraph(keepAlive, data, journalProbe));
            StartOutcome start = await ui.InvokeAsync(() => InitializeAndStartAsync(keepAlive));
            Assert.True(
                start.Result == StartResult.Accepted,
                $"TryStart returned {start.Result} with the notice '{start.Notice}'; the abandonment test needs a running session.");

            InstrumentedRun run = await probe.SessionCreated.WaitAsync(_callbackTimeout, cancellationToken);
            await run.Counters.WaitAsync(RequiredGateCalls, RequiredProgressSinkCalls, _callbackTimeout, cancellationToken);

            ControllerSnapshot snapshot = await ui.InvokeAsync(() => ReadController(keepAlive));
            Assert.True(
                snapshot.State == RunState.Running,
                $"The run must be Running when it is abandoned, with callbacks in flight; it is {snapshot.Description}.");

            WeakReference journal = journalProbe.Journal
                ?? throw new InvalidOperationException("The controller started a run without creating its journal through the factory.");
            tracked
                .Add("controller", probe.Controller)
                .Add("journal", journal)
                .Add("session", run.Session)
                .Add("callback state", run.CallbackState);
            AssertAliveBeforeAbandonment(tracked);
            ReportAbandonment(run.Counters, $"the controller graph ({snapshot.Description})", run.LatestProgress);
        }
        catch
        {
            await ReleaseAfterFailureAsync(ui, keepAlive);
            throw;
        }

        // Abandons the controller graph: the InstrumentedEngine goes too, which keeps only weak references to its runs.
        keepAlive.Clear();

        CollectionOutcome outcome = await CollectUntilAsync(
            () => tracked.Alive().Count == 0 && SessionProbe.LiveSessionCount() == 0,
            ui,
            _collectionTimeout);
        string description = DescribeCollection(tracked, outcome);
        _output.WriteLine(description);

        Assert.True(outcome.Collected, description);
        Assert.False(tracked.IsAlive("controller"), "The abandoned RunController is still reachable.");
        Assert.False(tracked.IsAlive("journal"), "The abandoned run's ProgressJournal is still reachable.");
        Assert.False(tracked.IsAlive("session"), "The abandoned EngineSession is still reachable.");
        Assert.False(tracked.IsAlive("callback state"), "The abandoned session's CallbackState is still rooted.");
        Assert.Equal(0, SessionProbe.LiveSessionCount());
    }

    [Fact(Timeout = 180_000)]
    public async Task SelfReferencingGate_Abandoned_IsCollectedAndDestroyedAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertNoLiveSessionAsync();

        using var ui = new DispatcherThread();
        var keepAlive = new KeepAlive();

        var tracked = new TrackedReferences();
        try
        {
            SelfReferencingProbe probe = await ui.InvokeAsync(() => StartSelfReferencingSession(keepAlive));
            await probe.Counters.WaitAsync(RequiredGateCalls, RequiredProgressSinkCalls, _callbackTimeout, cancellationToken);

            tracked
                .Add("session", probe.Session)
                .Add("callback state", probe.CallbackState)
                .Add("gate target", probe.Target);
            AssertAliveBeforeAbandonment(tracked);
            ReportAbandonment(probe.Counters, "the self-referencing session");
        }
        catch
        {
            await ReleaseAfterFailureAsync(ui, keepAlive);
            throw;
        }

        keepAlive.Clear();

        CollectionOutcome outcome = await CollectUntilAsync(
            () => tracked.Alive().Count == 0 && SessionProbe.LiveSessionCount() == 0,
            ui,
            _collectionTimeout);
        string description = DescribeCollection(tracked, outcome);
        _output.WriteLine(description);

        Assert.True(outcome.Collected, description);
        Assert.False(tracked.IsAlive("session"), "The abandoned EngineSession, whose gate target holds it, is still reachable.");
        Assert.False(tracked.IsAlive("callback state"), "The abandoned session's CallbackState is still rooted.");
        Assert.False(tracked.IsAlive("gate target"), "The abandoned session's step-gate target is still reachable.");
        Assert.Equal(0, SessionProbe.LiveSessionCount());
    }

    /// <summary>
    /// A callback-origin <c>rq_session_*</c> call holds the session's handle, as every marshalled handle argument does,
    /// while a concurrent <c>Dispose</c> disposes it, so the last reference is dropped on the coordinator inside the
    /// callback, where <c>rq_session_destroy</c> is refused. The session must still be destroyed before
    /// <c>Dispose</c> returns, and its <c>GCHandle</c> freed, so the <c>CallbackState</c> becomes collectible.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task CallbackHeldHandle_DisposedConcurrently_IsDestroyedOffCoordinatorAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertNoLiveSessionAsync();

        using var ui = new DispatcherThread();
        var keepAlive = new KeepAlive();

        HeldHandleProbe probe;
        int liveAfterDispose;
        try
        {
            probe = await ui.InvokeAsync(() => StartHandleHoldingSession(keepAlive));
            await probe.Holding.WaitAsync(_callbackTimeout, cancellationToken);

            // Exactly the run's own session, so a count of 0 once Dispose returns is its destruction.
            int liveBeforeDispose = SessionProbe.LiveSessionCount();
            Assert.True(
                liveBeforeDispose == 1,
                string.Create(CultureInfo.InvariantCulture, $"Expected the run's session to be the only live native session before Dispose; {liveBeforeDispose} are live."));

            // The gate now holds the handle on the coordinator after its own rq_session_stop, and keeps holding until
            // this Dispose has disposed the handle, so the gate's release is the last one.
            await Task.Run(() => DisposeSession(keepAlive), cancellationToken).WaitAsync(_disposeTimeout, cancellationToken);
            liveAfterDispose = SessionProbe.LiveSessionCount();
        }
        catch
        {
            await ReleaseAfterFailureAsync(ui, keepAlive);
            throw;
        }

        // Drops the session and its gate; once the GCHandle is freed nothing roots the CallbackState any more.
        keepAlive.Clear();

        HeldHandleObservation observed = await probe.Observation.WaitAsync(_callbackTimeout, cancellationToken);
        CollectionOutcome outcome = await CollectUntilAsync(() => !IsTargetAlive(probe.CallbackState), ui, _collectionTimeout);
        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"Step gate: handle disposed while held {observed.DisposedWhileHeld}, last reference released by the gate "
            + $"{observed.ReleasedLast}, held for {observed.HeldFor.TotalSeconds:0.000} s, fault {observed.Fault?.GetType().Name ?? "none"}; "
            + $"live native sessions when Dispose returned: {liveAfterDispose}; "
            + $"CallbackState collected {outcome.Collected} after {outcome.Iterations} forced full collection(s) in "
            + $"{outcome.Elapsed.TotalSeconds:0.00} s.");
        _output.WriteLine(description);

        Assert.True(observed.Fault is null, $"The step gate failed, so the race was not set up: {observed.Fault}");
        Assert.True(
            observed.DisposedWhileHeld,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Dispose did not dispose the handle within {_heldHandleDisposeTimeout.TotalSeconds:0} s while the step gate held it, so "
                + $"the race under test never happened. {description}"));
        Assert.True(
            observed.ReleasedLast,
            $"The step gate's release did not drop the handle's last reference, so the release never ran inside the callback. {description}");
        Assert.True(
            liveAfterDispose == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{liveAfterDispose} native session(s) were still live when Dispose returned: the native session and its coordinator "
                + $"thread leaked, because the last release ran rq_session_destroy on the coordinator (RQ_E_INVALID_STATE) or Dispose "
                + $"returned before the release finished. {description}"));
        Assert.True(
            outcome.Collected,
            $"The CallbackState is still rooted: its GCHandle and stop event leaked, because no successful rq_session_destroy freed them. {description}");
    }

    /// <summary>
    /// Builds the production controller graph over an instrumented test engine. Runs on the dispatcher thread, because
    /// the controller has UI-thread affinity and its sessions post their events to the context captured there.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProductionGraphProbe BuildProductionGraph(KeepAlive keepAlive, DataDirectory data, JournalProbe journalProbe)
    {
        var engine = new InstrumentedEngine(TestEngines.Create(FaultSpec.None), countCallbacks: true);
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = ThreeQuickSteps();

        var sessionCreated = new TaskCompletionSource<InstrumentedRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SessionCreated += run => sessionCreated.TrySetResult(run);

        var options = new RunControllerOptions
        {
            PlanProvider = _ => plan,
            MaxRegionBytes = TestRegions.Bytes(system, TestRegions.Mib512),
            JournalFactory = journalProbe.Create,
        };

        // The real ProgressJournal over FileJournalStorage in the test's data folder, and a SensorMonitor over
        // FakeSensorSource that is never started, so no sensor thread or timer references the run.
        ControllerGraph.Graph graph = ControllerGraph.Create(engine, data, options);
        keepAlive.Add(graph);
        keepAlive.Add(engine);
        return new ProductionGraphProbe(new WeakReference(graph.Controller), sessionCreated.Task);
    }

    // The awaiting state machine holds the controller only until it completes; a completed async method's task
    // releases its state machine, so nothing here outlives the call.
    private static async Task<StartOutcome> InitializeAndStartAsync(KeepAlive keepAlive)
    {
        RunController controller = keepAlive.Get<ControllerGraph.Graph>().Controller;
        await controller.InitializeAsync();
        StartResult result = controller.TryStart(TestType.Quick, stopOnError: false);
        return new StartOutcome(result, controller.NoticeMessage);
    }

    /// <summary>
    /// Creates and starts a session over the test engine whose step gate's target holds the session itself, a cycle
    /// through the session's options. Runs on the dispatcher thread, so the session posts its events there.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SelfReferencingProbe StartSelfReferencingSession(KeepAlive keepAlive)
    {
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib256);
        var counters = new CallbackCounters();
        var target = new GateTarget(counters);

        IEngineSession session = engine.CreateSession(new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = system.LogicalProcessors,
            Steps = ScaledPlans.Attributes(ThreeQuickSteps()),
            StepGate = target.Gate,
            ProgressSink = CountingSink(counters),
        });

        // Kept before Start, so a failing Start still leaves the session to the failure cleanup.
        keepAlive.Add(session);
        keepAlive.Add(target);
        target.Session = session;
        session.Start();

        return new SelfReferencingProbe(
            new WeakReference<IEngineSession>(session),
            new WeakReference<object>(SessionProbe.GetCallbackState(session)),
            new WeakReference<GateTarget>(target),
            counters);
    }

    /// <summary>
    /// Creates and starts a one-step session over the test engine whose step gate holds the session's handle across a
    /// concurrent <c>Dispose</c>. Runs on the dispatcher thread, so the session posts its events there.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HeldHandleProbe StartHandleHoldingSession(KeepAlive keepAlive)
    {
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib256);
        var gate = new HandleHoldingGate();

        IEngineSession session = engine.CreateSession(new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = system.LogicalProcessors,
            Steps = ScaledPlans.Attributes(ScaledPlans.Custom((0, _stepBudget))),
            StepGate = gate.Gate,
        });

        // Kept before Start, so a failing Start still leaves the session to the failure cleanup.
        keepAlive.Add(session);
        keepAlive.Add(gate);
        gate.Session = (EngineSession)session;
        session.Start();

        return new HeldHandleProbe(
            gate.Holding,
            gate.Observation,
            new WeakReference<object>(SessionProbe.GetCallbackState(session)));
    }

    // Out of line, so the session lives only in this frame and never in the awaiting test's state machine.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeSession(KeepAlive keepAlive) => keepAlive.Get<IEngineSession>().Dispose();

    // A factory of its own, so the sink's closure holds the counters alone.
    private static ProgressSink CountingSink(CallbackCounters counters) => _ => counters.OnProgressSink();

    private static TestPlan ThreeQuickSteps() =>
        ScaledPlans.Custom(Enumerable.Range(0, PlanStepCount).Select(index => (index, _stepBudget)).ToArray());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ControllerSnapshot ReadController(KeepAlive keepAlive)
    {
        ControllerGraph.Graph graph = keepAlive.Get<ControllerGraph.Graph>();
        RunStateMachine stateMachine = graph.StateMachine;
        RunController controller = graph.Controller;
        return new ControllerSnapshot(
            stateMachine.State,
            string.Create(
                CultureInfo.InvariantCulture,
                $"state {stateMachine.State}, end reason {stateMachine.EndReason?.ToString() ?? "none"}, "
                + $"end detail '{stateMachine.EndDetail}', result message '{controller.ResultMessage}', "
                + $"notice '{controller.NoticeMessage}'"));
    }

    /// <summary>
    /// Forces full blocking collections, each followed by the finalizers it queued, until <paramref name="collected"/>
    /// holds or <paramref name="timeout"/> has passed.
    /// </summary>
    /// <remarks>
    /// Native destruction runs on the finalizer thread (stop, wait for in-flight callbacks, join, release), and the
    /// <c>CallbackState</c> becomes collectible only once that has freed its <c>GCHandle</c>, so the live-session
    /// count and the callback state lag the session by a collection or more: the condition is polled until stable
    /// rather than checked once.
    /// </remarks>
    private static async Task<CollectionOutcome> CollectUntilAsync(Func<bool> collected, DispatcherThread ui, TimeSpan timeout)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        int iterations = 0;
        while (true)
        {
            // Drains the events posted so far; each holds only a record and a weak session reference.
            await ui.FlushAsync();

            // On the thread pool rather than wherever the last await resumed, which can be the dispatcher thread: no
            // test or dispatcher frame is then on the collecting stack, and the dispatcher keeps pumping meanwhile.
            await Task.Run(ForceFullCollection, cancellationToken);
            iterations++;

            if (collected())
            {
                return new CollectionOutcome(true, iterations, stopwatch.Elapsed);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                return new CollectionOutcome(false, iterations, stopwatch.Elapsed);
            }

            await Task.Delay(_collectionPause, cancellationToken);
        }
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool IsTargetAlive(WeakReference reference) => reference.IsAlive;

    // Out of line, so the strong target TryGetTarget yields lives only in this frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsTargetAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    // A session still finishing its disposal, or abandoned and awaiting finalization, is given a bounded chance to go
    // first: only one that survives that is a leak, and any live session would break the count assertions below.
    private static async Task AssertNoLiveSessionAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        int live = SessionProbe.LiveSessionCount();
        while (live != 0 && stopwatch.Elapsed < _settleTimeout)
        {
            await Task.Run(ForceFullCollection, cancellationToken);
            await Task.Delay(_collectionPause, cancellationToken);
            live = SessionProbe.LiveSessionCount();
        }

        Assert.True(
            live == 0,
            string.Create(CultureInfo.InvariantCulture, $"{live} native session(s) were live before the test: a previous test leaked a session."));
    }

    private static void AssertAliveBeforeAbandonment(TrackedReferences tracked)
    {
        IReadOnlyList<string> alive = tracked.Alive();
        string[] dead = tracked.Names.Except(alive).ToArray();
        Assert.True(
            dead.Length == 0,
            $"Already collected before the graph was abandoned, so the test would prove nothing: {string.Join(", ", dead)}.");

        // Exactly the run's own session, so the count reaching 0 afterwards is its destruction.
        int live = SessionProbe.LiveSessionCount();
        Assert.True(
            live == 1,
            string.Create(CultureInfo.InvariantCulture, $"Expected the run's session to be the only live native session before abandonment; {live} are live."));
    }

    private static string DescribeCollection(TrackedReferences tracked, CollectionOutcome outcome)
    {
        IReadOnlyList<string> alive = tracked.Alive();
        int live = SessionProbe.LiveSessionCount();
        string survivors = alive.Count == 0
            ? "none"
            : string.Join(", ", alive) + ", each through a strong path from a root the run created";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"After {outcome.Iterations} forced full collection(s) in {outcome.Elapsed.TotalSeconds:0.00} s, still reachable: "
            + $"{survivors}; live native sessions: {live} (0 proves rq_session_destroy ran and released the locked region).");
    }

    // The self-referencing session has no event subscribers, so only the controller graph reports its last progress.
    private void ReportAbandonment(CallbackCounters counters, string owner, RunProgress? latestProgress = null)
    {
        string progress = latestProgress is null
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $" at step {latestProgress.StepIndex + 1} of {latestProgress.StepCount} '{latestProgress.StepName}', "
                + $"elapsed {latestProgress.Elapsed.TotalSeconds:0.0} s, coverage {latestProgress.CoveragePercent:0.0} %,");
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Abandoning {owner}{progress} after {counters.GateCalls} step-gate and {counters.ProgressSinkCalls} "
            + $"progress-sink call(s); live native sessions: {SessionProbe.LiveSessionCount()}."));
    }

    /// <summary>
    /// For a failure before the graph is abandoned only: a run left going would keep its region locked and fail the
    /// next test's precondition. A failure of this cleanup is reported, never thrown over the original one.
    /// </summary>
    private async Task ReleaseAfterFailureAsync(DispatcherThread ui, KeepAlive keepAlive)
    {
        try
        {
            if (keepAlive.TryGet(out ControllerGraph.Graph? graph))
            {
                ControllerSnapshot snapshot = await ui.InvokeAsync(() => ReadController(keepAlive));
                _output.WriteLine($"Controller at the failure: {snapshot.Description}.");
                await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
            }
            else if (keepAlive.TryGet(out IEngineSession? session))
            {
                // Dispose blocks until the engine has released the region; the thread pool keeps that off the dispatcher.
                await Task.Run(session.Dispose);
            }
        }
        catch (Exception exception)
        {
            _output.WriteLine($"Releasing the run after the failure failed: {exception}");
        }
        finally
        {
            keepAlive.Clear();
        }
    }

    /// <summary>
    /// The only strong holder of an abandonment test's graph while the test waits for callbacks; clearing it abandons
    /// the graph. Thread-safe: the dispatcher fills it and the test thread clears it.
    /// </summary>
    private sealed class KeepAlive
    {
        private readonly object _sync = new();
        private readonly List<object> _roots = new();

        public void Add(object root)
        {
            ArgumentNullException.ThrowIfNull(root);
            lock (_sync)
            {
                _roots.Add(root);
            }
        }

        public T Get<T>()
            where T : class =>
            TryGet(out T? root)
                ? root
                : throw new InvalidOperationException($"The keep-alive holds no {typeof(T).Name}: the graph was abandoned or never built.");

        public bool TryGet<T>([NotNullWhen(true)] out T? root)
            where T : class
        {
            lock (_sync)
            {
                root = _roots.OfType<T>().FirstOrDefault();
                return root is not null;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _roots.Clear();
            }
        }
    }

    /// <summary>
    /// The controller's journal factory: creates the production file journal and keeps only a weak reference to it.
    /// The controller's options hold this probe through the factory delegate, so it must hold the journal no more
    /// strongly than that.
    /// </summary>
    private sealed class JournalProbe
    {
        private WeakReference? _journal;

        public WeakReference? Journal => Volatile.Read(ref _journal);

        public ProgressJournal Create(DataDirectory data, TimeProvider timeProvider)
        {
            ProgressJournal journal = ProgressJournal.Create(data, timeProvider);
            Volatile.Write(ref _journal, new WeakReference(journal));
            return journal;
        }
    }

    /// <summary>
    /// A step gate whose target holds the session that holds the gate through its options: the cycle only a strong
    /// edge from the native <c>GCHandle</c> could root.
    /// </summary>
    private sealed class GateTarget
    {
        private readonly CallbackCounters _counters;

        public GateTarget(CallbackCounters counters) => _counters = counters;

        /// <summary>Gets or sets the session whose gate this is, the strong back-edge of the cycle.</summary>
        public IEngineSession? Session { get; set; }

        public StepGateDecision Gate(EngineStepStarted step, WaitHandle stopEvent)
        {
            _counters.OnGate();
            return StepGateDecision.Continue;
        }
    }

    /// <summary>
    /// A step gate that, on the coordinator inside <c>on_step_starting</c>, holds the session's handle exactly as a
    /// marshalled handle argument does, requests a stop through the session (a callback-origin
    /// <c>rq_session_stop</c>), and keeps holding until a concurrent <c>Dispose</c> has disposed the handle, so its
    /// release is the last reference, dropped inside the callback. It never throws and always releases its hold.
    /// </summary>
    /// <remarks>
    /// <see cref="SafeHandle.IsClosed"/> turns true only when the last reference is released, so while the gate holds
    /// one, the disposal shows only in <see cref="SafeHandle"/>'s private state word, whose bit 1 is its Disposed flag.
    /// </remarks>
    private sealed class HandleHoldingGate
    {
        private const string SafeHandleStateField = "_state";
        private const int SafeHandleDisposedBit = 0b10;

        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HeldHandleObservation> _observation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        /// <summary>Gets or sets the session whose gate this is; set before the session starts.</summary>
        public EngineSession? Session { get; set; }

        /// <summary>Gets a task that completes once the gate holds the handle and has requested its stop.</summary>
        public Task Holding => _holding.Task;

        /// <summary>Gets a task that completes with what the gate saw, once it has released the handle.</summary>
        public Task<HeldHandleObservation> Observation => _observation.Task;

        public StepGateDecision Gate(EngineStepStarted step, WaitHandle stopEvent)
        {
            // The Abort below ends the run at its first step, so no later gate call takes part in the race.
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return StepGateDecision.Abort;
            }

            SafeSessionHandle? handle = null;
            bool added = false;
            bool disposedWhileHeld = false;
            bool releasedLast = false;
            Exception? fault = null;
            var held = new Stopwatch();
            try
            {
                FieldInfo state = typeof(SafeHandle).GetField(SafeHandleStateField, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        $"SafeHandle has no private {SafeHandleStateField} field, so the gate cannot see the concurrent Dispose.");
                EngineSession session = Session
                    ?? throw new InvalidOperationException("The step gate ran before the test published its session.");
                handle = session.Handle;
                handle.DangerousAddRef(ref added);
                held.Start();
                session.RequestStop(StopReason.User);
                _holding.TrySetResult();

                while (!IsDisposed(handle, state) && held.Elapsed < _heldHandleDisposeTimeout)
                {
                    Thread.Sleep(_heldHandlePollInterval);
                }

                disposedWhileHeld = IsDisposed(handle, state);
            }
            catch (Exception exception)
            {
                fault = exception;
            }
            finally
            {
                if (added)
                {
                    try
                    {
                        handle!.DangerousRelease();

                        // The release that drops the last reference closes the handle before it returns.
                        releasedLast = handle.IsClosed;
                    }
                    catch (Exception exception)
                    {
                        fault ??= exception;
                    }
                }
            }

            // Completes the signal too when the gate failed before raising it, so the test fails at once with the cause.
            if (fault is not null)
            {
                _holding.TrySetException(fault);
            }

            _observation.TrySetResult(new HeldHandleObservation(disposedWhileHeld, releasedLast, held.Elapsed, fault));
            return StepGateDecision.Abort;
        }

        private static bool IsDisposed(SafeHandle handle, FieldInfo state) =>
            ((int)state.GetValue(handle)! & SafeHandleDisposedBit) != 0;
    }

    /// <summary>Named weak references whose targets a test expects to be collected.</summary>
    private sealed class TrackedReferences
    {
        private readonly List<(string Name, Func<bool> IsAlive)> _references = new();

        public IReadOnlyList<string> Names => _references.Select(reference => reference.Name).ToArray();

        public TrackedReferences Add(string name, WeakReference reference)
        {
            _references.Add((name, () => IsTargetAlive(reference)));
            return this;
        }

        public TrackedReferences Add<T>(string name, WeakReference<T> reference)
            where T : class
        {
            _references.Add((name, () => IsTargetAlive(reference)));
            return this;
        }

        public IReadOnlyList<string> Alive() =>
            _references.Where(reference => reference.IsAlive()).Select(reference => reference.Name).ToArray();

        public bool IsAlive(string name) => _references.Single(reference => reference.Name == name).IsAlive();
    }

    private sealed record ProductionGraphProbe(WeakReference Controller, Task<InstrumentedRun> SessionCreated);

    private sealed record SelfReferencingProbe(
        WeakReference<IEngineSession> Session,
        WeakReference<object> CallbackState,
        WeakReference<GateTarget> Target,
        CallbackCounters Counters);

    private sealed record HeldHandleProbe(
        Task Holding,
        Task<HeldHandleObservation> Observation,
        WeakReference<object> CallbackState);

    // What the handle-holding gate saw: whether Dispose disposed the handle while the gate held it, whether the gate's
    // release then dropped the last reference, how long it held the handle, and what kept it from setting up the race.
    private sealed record HeldHandleObservation(bool DisposedWhileHeld, bool ReleasedLast, TimeSpan HeldFor, Exception? Fault);

    private sealed record StartOutcome(StartResult Result, string? Notice);

    private sealed record ControllerSnapshot(RunState State, string Description);

    private readonly record struct CollectionOutcome(bool Collected, int Iterations, TimeSpan Elapsed);
}
