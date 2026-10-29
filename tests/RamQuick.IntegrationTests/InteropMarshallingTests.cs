using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// The engine boundary as the wrapper sees it, over the test-only engine library this process is bound to: the
/// one-time binding, the managed mirrors of every ABI struct against the engine's own <c>sizeof</c>, the result-code
/// mapping, event delivery on the captured synchronization context, an injected affinity failure, and the stop
/// ordering that lets a woken step gate find its reason already recorded.
/// </summary>
/// <remarks>
/// Every engine session sizes its region with <see cref="TestRegions.Bytes"/> for one worker per logical processor,
/// is disposed on every path, and leaves no native session alive. No test needs elevation.
/// </remarks>
public sealed class InteropMarshallingTests
{
    private const int TestTimeoutMs = 60_000;
    private const string Operation = "test";
    private const string UnknownResultCodeText = "Unknown result code";
    private const string UnknownCodeName = "Unknown";
    private const ulong Seed = 0x5EED_0F_1A7E_D0C5UL;

    // ERROR_INVALID_PARAMETER, the error the injected failing pin reports.
    private const uint AffinityWindowsError = 87;

    private static readonly TimeSpan _stepBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _runTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gateWakeTimeout = TimeSpan.FromSeconds(10);

    private readonly EngineBindingFixture _binding;
    private readonly ITestOutputHelper _output;

    /// <summary>Initializes the tests with the process binding and the test output.</summary>
    /// <param name="binding">The assembly fixture that bound this process to the test engine library.</param>
    /// <param name="output">The test output.</param>
    public InteropMarshallingTests(EngineBindingFixture binding, ITestOutputHelper output)
    {
        _binding = binding;
        _output = output;
    }

    [Fact]
    public void Bind_SecondCall_Throws()
    {
        Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
        Assert.Equal(EngineLibrary.BoundFileName, _binding.BoundFileName);

        // Neither rebinding to the same library nor back to the production one is accepted once bound.
        foreach (string fileName in new[] { "ramquick_engine_test.dll", "ramquick_engine.dll" })
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => EngineLibrary.Bind(fileName));
            _output.WriteLine($"Bind(\"{fileName}\") threw: {exception.Message}");
            Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
        }

        // The rejected calls left the binding working: the API-version check and a system probe still resolve.
        SystemSnapshot system = TestEngines.Create().QuerySystem();
        Assert.True(system.LogicalProcessors > 0, $"rq_query_system reported {system.LogicalProcessors} logical processors.");
        Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
    }

    [Fact]
    public void StructSizes_ManagedMirrors_MatchNative()
    {
        RqStructSizes native = SessionProbe.GetStructSizes();
        Assert.Equal(56u, native.StructSize);
        Assert.Equal(0u, native.Reserved0);
        Assert.Equal(56, Unsafe.SizeOf<RqStructSizes>());

        var rows = new StructSizeRow[]
        {
            new("rq_system_info", nameof(RqSystemInfo), Unsafe.SizeOf<RqSystemInfo>(), RqSystemInfo.Size, native.SystemInfo, 112),
            new("rq_step_def", nameof(RqStepDef), Unsafe.SizeOf<RqStepDef>(), RqStepDef.Size, native.StepDef, 96),
            new("rq_session_config", nameof(RqSessionConfig), Unsafe.SizeOf<RqSessionConfig>(), RqSessionConfig.Size, native.SessionConfig, 64),
            new("rq_callbacks", nameof(RqCallbacks), Unsafe.SizeOf<RqCallbacks>(), RqCallbacks.Size, native.Callbacks, 72),
            new("rq_prepared", nameof(RqPrepared), Unsafe.SizeOf<RqPrepared>(), RqPrepared.Size, native.Prepared, 40),
            new("rq_step_start", nameof(RqStepStart), Unsafe.SizeOf<RqStepStart>(), RqStepStart.Size, native.StepStart, 80),
            new("rq_step_result", nameof(RqStepResult), Unsafe.SizeOf<RqStepResult>(), RqStepResult.Size, native.StepResult, 48),
            new("rq_progress", nameof(RqProgress), Unsafe.SizeOf<RqProgress>(), RqProgress.Size, native.Progress, 72),
            new("rq_error_record", nameof(RqErrorRecord), Unsafe.SizeOf<RqErrorRecord>(), RqErrorRecord.Size, native.ErrorRecord, 112),
            new("rq_run_result", nameof(RqRunResult), Unsafe.SizeOf<RqRunResult>(), RqRunResult.Size, native.RunResult, 192),
            new("rq_fault_spec", nameof(RqFaultSpec), Unsafe.SizeOf<RqFaultSpec>(), ExpectedSizes.FaultSpec, native.FaultSpec, 72),
            new("rq_injected_fault", nameof(RqInjectedFault), Unsafe.SizeOf<RqInjectedFault>(), ExpectedSizes.InjectedFault, native.InjectedFault, 40),
        };

        foreach (StructSizeRow row in rows)
        {
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.NativeName}: native {row.Native}, managed {row.Mirror} {row.Managed} (Size constant {row.SizeConstant}), ABI {row.Abi}"));
        }

        // Every row is checked, so one run reports every mismatched mirror rather than the first.
        Assert.Multiple(rows.Select(row => (Action)row.Check).ToArray());
    }

    [Theory]
    [InlineData(-1, typeof(ArgumentException))]
    [InlineData(-2, typeof(ObjectDisposedException))]
    [InlineData(-3, typeof(InvalidOperationException))]
    [InlineData(-4, typeof(EngineException))]
    [InlineData(-5, typeof(EngineException))]
    [InlineData(-6, typeof(EngineException))]
    [InlineData(-7, typeof(EngineException))]
    [InlineData(-8, typeof(EngineException))]
    [InlineData(-99, typeof(EngineException))]
    [InlineData(-12345, typeof(EngineException))]
    public void ThrowIfFailed_FailureCode_ThrowsMappedException(int code, Type expectedType)
    {
        var resultCode = (EngineResultCode)code;
        bool known = Enum.IsDefined(resultCode);

        // EngineResults.Describe returns rq_result_message's text, or the member name when the engine cannot be read.
        // The checks below rule that fallback out, so a match proves the text came from the engine.
        string nativeText = EngineResults.Describe(resultCode);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"rq_result_message({code}) = \"{nativeText}\""));
        Assert.False(string.IsNullOrEmpty(nativeText), $"rq_result_message({code}) returned no text.");
        if (known)
        {
            Assert.NotEqual(UnknownResultCodeText, nativeText);
            Assert.NotEqual(resultCode.ToString(), nativeText);
        }
        else
        {
            Assert.Equal(UnknownResultCodeText, nativeText);
        }

        Exception exception = Assert.ThrowsAny<Exception>(() => EngineResults.ThrowIfFailed(code, Operation));
        _output.WriteLine($"{exception.GetType().Name}: {exception.Message}");

        // Exact type, not assignability: ObjectDisposedException derives from InvalidOperationException.
        Assert.IsType(expectedType, exception);

        if (exception is EngineException engineException)
        {
            Assert.Equal(resultCode, engineException.Code);
            Assert.Equal(nativeText, engineException.NativeMessage);
            string name = known ? resultCode.ToString() : UnknownCodeName;
            Assert.Equal(
                string.Create(CultureInfo.InvariantCulture, $"{Operation} failed ({name}, {code}): {nativeText}"),
                engineException.Message);
        }
        else
        {
            Assert.StartsWith($"{Operation}: {nativeText}", exception.Message, StringComparison.Ordinal);
        }

        if (exception is ObjectDisposedException disposed)
        {
            Assert.Equal("EngineSession", disposed.ObjectName);
        }
    }

    [Fact]
    public void ThrowIfFailed_Ok_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => EngineResults.ThrowIfFailed((int)EngineResultCode.Ok, Operation)));

        // RQ_OK is not a failure, so no exception is ever created for it.
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineResults.CreateException((int)EngineResultCode.Ok, Operation));
    }

    [Fact]
    public void CreateSession_WorkerCountAboveLogicalProcessors_ThrowsArgumentException()
    {
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(system, OneStepPlan(), workerCount: system.LogicalProcessors + 1);

        // The managed validation accepts any positive worker count; only rq_session_create knows the machine, so this
        // RQ_E_INVALID_ARGUMENT comes back across the real boundary.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => engine.CreateSession(options));
        _output.WriteLine(exception.Message);
        Assert.StartsWith(
            $"rq_session_create: {EngineResults.Describe(EngineResultCode.InvalidArgument)}",
            exception.Message,
            StringComparison.Ordinal);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Start_CalledTwiceThenAfterDispose_ThrowsInvalidOperationThenObjectDisposedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan()));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // A stop recorded before start makes the engine raise Completed and Released at once, with no preparation,
            // so the run locks no memory and the first Start still succeeds.
            session.RequestStop(StopReason.User);
            session.Start();

            // The session's own guard reports the engine's RQ_E_INVALID_STATE contract without a second native call.
            InvalidOperationException secondStart = Assert.Throws<InvalidOperationException>(session.Start);
            _output.WriteLine($"Second Start threw: {secondStart.Message}");
            Assert.Contains("already been started", secondStart.Message, StringComparison.Ordinal);

            Assert.Throws<ArgumentOutOfRangeException>(() => session.RequestStop((StopReason)99));

            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine($"Completed: {completed.EndReason}, detail '{completed.EndDetail}'");

            Assert.Equal(RunEndReason.UserStop, completed.EndReason);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Null(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
        }
        finally
        {
            session.Dispose();
        }

        ObjectDisposedException afterDispose = Assert.Throws<ObjectDisposedException>(session.Start);
        _output.WriteLine($"Start after Dispose threw: {afterDispose.Message}");

        // After disposal RequestStop does nothing and Dispose is idempotent.
        session.RequestStop(StopReason.User);
        session.Dispose();
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Events_SessionCreatedOnDispatcher_ArriveOnCapturedContextAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = TwoStepPlan();

        var gateThreads = new ConcurrentQueue<int>();
        var sinkThreads = new ConcurrentQueue<int>();
        EngineSessionOptions options = CreateOptions(
            system,
            plan,
            gate: (_, _) =>
            {
                gateThreads.Enqueue(Environment.CurrentManagedThreadId);
                return StepGateDecision.Continue;
            },
            sink: _ => sinkThreads.Enqueue(Environment.CurrentManagedThreadId));

        // Created on the dispatcher thread, so the session captures the dispatcher's synchronization context.
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            EngineReleased released = await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            await ui.FlushAsync();

            IReadOnlyList<RecordedEngineEvent> events = recorder.Events;
            WriteEvents(events, ui.ManagedThreadId);

            // WPF installs a fresh DispatcherSynchronizationContext for each dispatched operation, so the context is
            // identified by its type and the dispatcher thread rather than by instance.
            Assert.All(events, recorded =>
            {
                Assert.True(
                    recorded.ThreadId == ui.ManagedThreadId,
                    $"{recorded.Kind} ran on managed thread {recorded.ThreadId}, not on the dispatcher thread {ui.ManagedThreadId}.");
                Assert.IsType<DispatcherSynchronizationContext>(recorded.Context);
            });

            // Only the step gate and the progress sink run synchronously on the engine's coordinator thread.
            int[] coordinatorThreads = gateThreads.Concat(sinkThreads).Distinct().ToArray();
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Gate calls {gateThreads.Count}, sink calls {sinkThreads.Count}, on threads [{string.Join(", ", coordinatorThreads)}]"));
            Assert.Equal(plan.Steps.Count, gateThreads.Count);
            Assert.NotEmpty(sinkThreads);
            Assert.DoesNotContain(ui.ManagedThreadId, coordinatorThreads);
            Assert.Single(coordinatorThreads);

            EngineEventKind[] kinds = events.Select(recorded => recorded.Kind).ToArray();
            EngineEventKind[] lifecycle = kinds
                .Where(kind => kind is not (EngineEventKind.ProgressChanged or EngineEventKind.ErrorsDetected))
                .ToArray();
            Assert.Equal(
                new[]
                {
                    EngineEventKind.Prepared,
                    EngineEventKind.StepStarted,
                    EngineEventKind.StepFinished,
                    EngineEventKind.StepStarted,
                    EngineEventKind.StepFinished,
                    EngineEventKind.Completed,
                    EngineEventKind.Released,
                },
                lifecycle);

            int preparedAt = Array.IndexOf(kinds, EngineEventKind.Prepared);
            int completedAt = Array.IndexOf(kinds, EngineEventKind.Completed);
            int[] progressAt = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] == EngineEventKind.ProgressChanged).ToArray();
            Assert.NotEmpty(progressAt);
            Assert.All(progressAt, index => Assert.InRange(index, preparedAt + 1, completedAt - 1));

            EnginePrepared prepared = Assert.IsType<EnginePrepared>(recorder.Prepared);
            Assert.Equal(AddressKind.Virtual, prepared.AddressKind);
            Assert.Equal(options.RegionBytes, prepared.TestedBytes);
            Assert.Equal(system.LogicalProcessors, prepared.WorkerCount);

            IReadOnlyList<EngineStepStarted> started = recorder.StepStarted;
            Assert.Equal(new[] { 0, 1 }, started.Select(step => step.Index));
            Assert.All(started, step => Assert.Equal(plan.Steps.Count, step.Count));
            Assert.Equal(new[] { TestPlans.Quick.Steps[0].Name, TestPlans.Quick.Steps[1].Name }, started.Select(step => step.Name));

            IReadOnlyList<EngineStepFinished> finished = recorder.StepFinished;
            Assert.Equal(new[] { 0, 1 }, finished.Select(step => step.Outcome.Attributes.Index));
            Assert.All(finished, step =>
            {
                Assert.True(step.Outcome.Completed, $"Step {step.Outcome.Attributes.Index} did not complete.");
                Assert.True(step.Outcome.VerifiedBytes > 0, $"Step {step.Outcome.Attributes.Index} verified no bytes.");
                Assert.Equal(0UL, step.Outcome.Errors);
            });

            Assert.Equal(RunEndReason.Completed, completed.EndReason);
            Assert.Equal(0UL, completed.TotalErrors);
            Assert.Null(completed.FirstErrorTime);
            Assert.Equal(plan.Steps.Count, completed.StepsCompleted);
            Assert.Equal(0u, completed.OsError);
            Assert.Null(completed.EndDetail);
            Assert.Empty(recorder.Errors);
            Assert.True(released.ReleasedBytes > 0, $"Released reported {released.ReleasedBytes} bytes.");
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AffinityFailure_InjectedWindowsError87_SurfacesAsAffinityFailedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create(FaultSpec.AffinityFailure(worker: 0, windowsError: AffinityWindowsError));
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(system, TwoStepPlan());

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            WriteEvents(recorder.Events, ui.ManagedThreadId);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Completed: {completed.EndReason}, os_error {completed.OsError}, end_detail '{completed.NativeEndDetail}', EndDetail '{completed.EndDetail}'"));

            Assert.Equal(RunEndReason.AffinityFailed, completed.EndReason);
            Assert.Equal(AffinityWindowsError, completed.OsError);
            Assert.False(string.IsNullOrEmpty(completed.NativeEndDetail), "The engine reported no end_detail for affinity_failed.");
            Assert.Contains("SetThreadGroupAffinity", completed.NativeEndDetail, StringComparison.Ordinal);
            Assert.StartsWith("Windows error 87: ", completed.EndDetail, StringComparison.Ordinal);
            Assert.Equal("Windows error 87: " + completed.NativeEndDetail, completed.EndDetail);

            // A preparation failure raises Completed and Released only: no worker ran unpinned, and no step started.
            Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
            Assert.Null(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Equal(0UL, independentVerifiedBytes);
            Assert.All(recorder.EventThreadIds, threadId => Assert.Equal(ui.ManagedThreadId, threadId));
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(StopReason.User, 3u, RunEndReason.UserStop)]
    [InlineData(StopReason.Thermal, 4u, RunEndReason.Thermal)]
    [InlineData(StopReason.HostFault, 5u, RunEndReason.HostFault)]
    public async Task RequestStop_WhileGateWaitsOnStopEvent_GateFindsReasonRecordedAsync(
        StopReason reason,
        uint expectedPendingReason,
        RunEndReason expectedEndReason)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(expectedPendingReason, SessionProbe.ExpectedPendingReason(reason));

        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // Set once the session exists; the gate runs only after Start, so it always finds the session.
        var sessionBox = new StrongBox<IEngineSession?>();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateObservation = new TaskCompletionSource<GateObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        StepGate gate = (step, stopEvent) =>
        {
            try
            {
                gateEntered.TrySetResult();
                bool woken = stopEvent.WaitOne(_gateWakeTimeout);

                // Read the moment the event wakes the gate: RequestStop sets the event only after rq_session_stop has
                // returned, so the reason must already be recorded.
                IEngineSession current = Volatile.Read(ref sessionBox.Value)
                    ?? throw new InvalidOperationException("The step gate ran before the test published its session.");
                uint pending = SessionProbe.GetPendingStopReason(current);
                gateObservation.TrySetResult(new GateObservation(woken, pending, step.Index));
            }
            catch (Exception exception)
            {
                gateObservation.TrySetException(exception);
                throw;
            }

            return StepGateDecision.Abort;
        };

        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate));
        Volatile.Write(ref sessionBox.Value, session);
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            await gateEntered.Task.WaitAsync(_runTimeout, cancellation);

            // The gate now blocks inside on_step_starting, before any traffic of step 0, and nothing has stopped the run.
            Assert.Equal(SessionProbe.NoPendingReason, SessionProbe.GetPendingStopReason(session));

            session.RequestStop(reason);
            GateObservation observed = await gateObservation.Task.WaitAsync(_runTimeout, cancellation);
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{reason}: gate woken {observed.Woken}, pending reason {observed.PendingReason} at step {observed.StepIndex}; "
                + $"Completed {completed.EndReason} ('{completed.EndDetail}'), steps completed {completed.StepsCompleted}, "
                + $"verified {completed.VerifiedBytes} B, independently counted {independentVerifiedBytes} B"));

            Assert.True(observed.Woken, $"The step gate was not woken by the stop event within {_gateWakeTimeout.TotalSeconds:0} s of {reason}.");
            Assert.Equal(expectedPendingReason, observed.PendingReason);
            Assert.Equal(0, observed.StepIndex);
            Assert.Equal(expectedEndReason, completed.EndReason);

            // The aborted step started no traffic.
            Assert.NotNull(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Empty(recorder.StepFinished);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Equal(0UL, independentVerifiedBytes);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    private static TestPlan OneStepPlan() => ScaledPlans.Custom((0, _stepBudget));

    private static TestPlan TwoStepPlan() => ScaledPlans.Custom((0, _stepBudget), (1, _stepBudget));

    private static EngineSessionOptions CreateOptions(
        SystemSnapshot system,
        TestPlan plan,
        int? workerCount = null,
        StepGate? gate = null,
        ProgressSink? sink = null)
    {
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib256);
        return new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = workerCount ?? system.LogicalProcessors,
            Seed = Seed,
            Steps = ScaledPlans.Attributes(plan),
            StepGate = gate,
            ProgressSink = sink,
        };
    }

    private static void AssertNoLiveSessions()
    {
        int live = SessionProbe.LiveSessionCount();
        Assert.True(live == 0, $"{live} native engine session(s) are still alive after the test disposed its session.");
    }

    private void WriteEvents(IReadOnlyList<RecordedEngineEvent> events, int dispatcherThreadId)
    {
        int progressCount = events.Count(recorded => recorded.Kind == EngineEventKind.ProgressChanged);
        string lifecycle = string.Join(
            " -> ",
            events.Where(recorded => recorded.Kind != EngineEventKind.ProgressChanged).Select(recorded => recorded.Kind));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Events: {lifecycle}; ProgressChanged x{progressCount}; dispatcher thread {dispatcherThreadId}, "
            + $"event threads [{string.Join(", ", events.Select(recorded => recorded.ThreadId).Distinct())}]"));
    }

    // One ABI struct: the engine's sizeof, the managed mirror's size and Size constant, and the size the ABI fixes.
    private sealed record StructSizeRow(string NativeName, string Mirror, int Managed, int SizeConstant, uint Native, int Abi)
    {
        public void Check()
        {
            Assert.True(
                Managed == Native,
                string.Create(CultureInfo.InvariantCulture, $"{Mirror} is {Managed} bytes, but the engine's sizeof({NativeName}) is {Native}."));
            Assert.True(
                Managed == Abi,
                string.Create(CultureInfo.InvariantCulture, $"{Mirror} is {Managed} bytes, but the ABI fixes {NativeName} at {Abi}."));
            Assert.True(
                SizeConstant == Abi,
                string.Create(CultureInfo.InvariantCulture, $"The size constant for {Mirror} is {SizeConstant}, but the ABI fixes {NativeName} at {Abi}."));
        }
    }

    // What the step gate saw when the stop event woke it.
    private sealed record GateObservation(bool Woken, uint PendingReason, int StepIndex);
}

