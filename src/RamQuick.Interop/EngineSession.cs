using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// Creates the native session for <see cref="EngineSession.Create"/>: the production value calls
/// <c>rq_session_create</c>, and the integration tests supply one that calls <c>rq_test_session_create</c> of
/// <c>ramquick_engine_test.dll</c> with a fault specification.
/// </summary>
/// <param name="config">
/// The marshalled configuration, whose <see cref="RqSessionConfig.Steps"/> points to the marshalled step array. Both
/// live only for the duration of the call; the engine deep-copies them.
/// </param>
/// <param name="callbacks">The callback table, valid only for the duration of the call; the engine copies it.</param>
/// <param name="session">Receives the raw native session handle on success.</param>
/// <returns>The engine's <c>rq_result</c>: <c>RQ_OK</c> or a negative <c>RQ_E_*</c> code.</returns>
internal unsafe delegate int NativeSessionFactory(RqSessionConfig* config, RqCallbacks* callbacks, nint* session);

/// <summary>
/// The production <see cref="IEngineSession"/>: one native engine session behind a <see cref="SafeSessionHandle"/>,
/// with its events copied into managed records and posted to the <see cref="SynchronizationContext"/> captured at
/// creation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The only object native code can reach is the <see cref="Interop.CallbackState"/>, through the
/// <see cref="GCHandle"/> whose address the engine holds as <c>user_data</c>; it refers back to this session only
/// weakly. Every edge of the session points outward: to its handle, its callback state, its options (which hold the
/// step gate, progress sink and fault sink) and its event subscribers. Posted work items carry the weak reference and
/// the event record, never the session. The only strong edge into a session is therefore its owner's field, so a
/// session its owner abandons undisposed becomes unreachable, and its handle's critical finalizer destroys the native
/// session, releases the locked memory and frees the GCHandle, even when a step gate's target holds the session.
/// </para>
/// <para>
/// <b>Threading.</b> The <c>OnNative*</c> handlers run on the engine's coordinator thread, called by
/// <see cref="CallbackThunks"/>, which contains every exception they throw. Only the step gate and the progress sink run
/// there synchronously; everything else is posted. <see cref="Start"/>, <see cref="RequestStop"/> and
/// <see cref="Dispose"/> may be called from any thread except that <see cref="Dispose"/> is refused inside a callback.
/// </para>
/// </remarks>
internal sealed class EngineSession : IEngineSession
{
    private const string PreparedHandlerName = "Prepared handler";
    private const string StepStartedHandlerName = "StepStarted handler";
    private const string StepFinishedHandlerName = "StepFinished handler";
    private const string ProgressChangedHandlerName = "ProgressChanged handler";
    private const string ErrorsDetectedHandlerName = "ErrorsDetected handler";
    private const string CompletedHandlerName = "Completed handler";
    private const string ReleasedHandlerName = "Released handler";

    private readonly SafeSessionHandle _handle;
    private readonly CallbackState _state;

    // Holds StepGate, ProgressSink and CallbackFault. The session holds the delegates, never the CallbackState, so a
    // delegate whose target holds the session cannot be rooted through the GCHandle.
    private readonly EngineSessionOptions _options;
    private readonly ReadOnlyCollection<StepAttributes> _steps;

    // 0 or 1, written with Interlocked or Volatile: Start, Dispose and the coordinator's callbacks run on different
    // threads.
    private int _started;
    private int _completed;
    private int _disposed;

    private EngineSession(
        SafeSessionHandle handle,
        CallbackState state,
        EngineSessionOptions options,
        ReadOnlyCollection<StepAttributes> steps)
    {
        _handle = handle;
        _state = state;
        _options = options;
        _steps = steps;
    }

    /// <inheritdoc/>
    public event EventHandler<EnginePrepared>? Prepared;

    /// <inheritdoc/>
    public event EventHandler<EngineStepStarted>? StepStarted;

    /// <inheritdoc/>
    public event EventHandler<EngineStepFinished>? StepFinished;

    /// <inheritdoc/>
    public event EventHandler<RunProgress>? ProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<EngineErrorsDetected>? ErrorsDetected;

    /// <inheritdoc/>
    public event EventHandler<EngineCompleted>? Completed;

    /// <inheritdoc/>
    public event EventHandler<EngineReleased>? Released;

    /// <summary>
    /// Gets the session's immutable copy of the plan's steps, taken once the options were validated. It is the list the
    /// engine was configured with, and the source of every step name and attribute the session reports.
    /// </summary>
    internal IReadOnlyList<StepAttributes> Steps => _steps;

    /// <summary>
    /// Gets the handle that owns the native session, for the integration tests' <c>rq_test_*</c> queries, which must
    /// bracket their raw-handle calls with <see cref="SafeHandle.DangerousAddRef(ref bool)"/>.
    /// </summary>
    internal SafeSessionHandle Handle => _handle;

    /// <summary>
    /// Gets the object the native <c>user_data</c> roots, for the lifetime tests' weak-reference assertions.
    /// </summary>
    internal CallbackState CallbackState => _state;

    /// <summary>
    /// Validates <paramref name="options"/>, marshals them into <c>rq_session_config</c> and <c>rq_step_def[]</c> and
    /// creates an idle native session through <paramref name="factory"/>. Allocates nothing large and starts no thread.
    /// </summary>
    /// <param name="options">The session's configuration and hooks.</param>
    /// <param name="factory">The native creation call: <c>rq_session_create</c>, or a test substitute.</param>
    /// <returns>The session, owned by the caller, who disposes it once <see cref="Released"/> has been raised.</returns>
    /// <remarks>
    /// Events are posted to <see cref="SynchronizationContext.Current"/> as captured here. When no context is current,
    /// as in <c>RamQuick.TestHost</c> or a test without a dispatcher, a plain <see cref="SynchronizationContext"/> is
    /// used, whose <see cref="SynchronizationContext.Post"/> queues to the thread pool, so subscribers never run on the
    /// coordinator.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="EngineSessionOptions.Validate"/> rejected the options, or the engine returned
    /// <c>RQ_E_INVALID_ARGUMENT</c>, for example for a worker count above the logical processor count.
    /// </exception>
    /// <exception cref="EngineException">The engine returned another failure code, such as <c>RQ_E_INTERNAL</c>.</exception>
    internal static EngineSession Create(EngineSessionOptions options, NativeSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);

        options.Validate();
        ReadOnlyCollection<StepAttributes> steps = Array.AsReadOnly(options.Steps.ToArray());
        SynchronizationContext context = SynchronizationContext.Current ?? new SynchronizationContext();

        var stopEvent = new ManualResetEvent(initialState: false);
        CallbackState state;
        GCHandle callbackHandle;
        try
        {
            state = new CallbackState(context, stopEvent);
            callbackHandle = GCHandle.Alloc(state);
        }
        catch
        {
            stopEvent.Dispose();
            throw;
        }

        SafeSessionHandle handle;
        try
        {
            handle = new SafeSessionHandle(callbackHandle);
        }
        catch
        {
            callbackHandle.Free();
            stopEvent.Dispose();
            throw;
        }

        // From here the handle owns the stop event and the GCHandle; every failure path releases both through it.
        EngineSession session;
        int result;
        nint nativeSession;
        try
        {
            session = new EngineSession(handle, state, options, steps);

            // Attached before the engine receives user_data, so the first callback already resolves the session.
            state.AttachSession(session);
            result = CreateNative(options, steps, handle.CallbackUserData, factory, out nativeSession);
        }
        catch
        {
            DiscardWithoutSession(handle);
            throw;
        }

        if (result != (int)EngineResultCode.Ok)
        {
            DiscardWithoutSession(handle);
            EngineResults.ThrowIfFailed(result, "rq_session_create");
        }

        try
        {
            handle.Attach(nativeSession);
        }
        catch
        {
            // Only an engine that reports success without a usable handle lands here; no session was started, so no
            // callback can resolve the user_data released below.
            DiscardWithoutSession(handle);
            throw;
        }

        return session;
    }

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // The engine refuses a second start with RQ_E_INVALID_STATE as well; the managed guard reports it without a
        // native call and keeps a second caller from racing the first into the engine.
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The engine session has already been started.");
        }

        EngineResults.ThrowIfFailed(NativeMethods.RqSessionStart(_handle), "rq_session_start");
    }

    /// <inheritdoc/>
    public void RequestStop(StopReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, $"{nameof(reason)} must be a defined {nameof(StopReason)}.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        StopCore(reason);
    }

    /// <summary>
    /// Releases the native session: stops a run still in progress, waits for in-flight callbacks, joins every engine
    /// thread, releases the locked memory, then disposes the stop event and frees the GCHandle. Idempotent; blocks until
    /// the release finishes, so callers dispose after <see cref="Released"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Called from inside an engine callback, such as the step gate or the progress sink, where
    /// <c>rq_session_destroy</c> would have to join the coordinator that is calling it.
    /// </exception>
    public void Dispose()
    {
        if (CallbackThunks.IsInCallback)
        {
            throw new InvalidOperationException(
                "An engine session cannot be disposed from inside an engine callback, such as its step gate or progress sink.");
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A run still in progress is stopped first, which also sets the stop event, so a step gate waiting on its
        // journal acknowledgment wakes at once instead of holding rq_session_destroy for its full timeout.
        if (Volatile.Read(ref _completed) == 0)
        {
            StopCore(StopReason.HostFault);
        }

        _handle.Dispose();
    }

    /// <summary>
    /// Handles <c>on_prepared</c> on the coordinator: copies the payload and posts <see cref="Prepared"/>.
    /// </summary>
    /// <param name="info">The engine's payload, valid only during the call.</param>
    internal void OnNativePrepared(in RqPrepared info)
    {
        Post(EngineEventMapper.ToPrepared(in info), static (session, e) => session.Raise(session.Prepared, e, PreparedHandlerName));
    }

    /// <summary>
    /// Handles <c>on_step_starting</c>, the step gate, synchronously on the coordinator before any memory traffic of the
    /// step.
    /// </summary>
    /// <param name="info">The engine's payload, valid only during the call.</param>
    /// <returns>
    /// <see cref="NativeConstants.ActionContinue"/> to run the step; <see cref="NativeConstants.ActionAbort"/> to start
    /// no traffic for it and end the run.
    /// </returns>
    /// <remarks>
    /// The gate may block, for at most its journal-acknowledgment timeout. An Abort the stop event did not cause is
    /// preceded by a host-fault stop request, so the run never ends on an Abort without a recorded reason; the first
    /// recorded reason wins, so a gate that requested a stop itself keeps its reason. <see cref="StepStarted"/> is posted
    /// only for a step allowed to run, so the UI never shows a step that started no traffic.
    /// </remarks>
    internal uint OnNativeStepStarting(in RqStepStart info)
    {
        EngineStepStarted started = EngineEventMapper.ToStepStarted(in info, _steps);
        StepGate? gate = _options.StepGate;
        StepGateDecision decision = gate is null ? StepGateDecision.Continue : gate(started, _state.StopEvent);

        // Any value other than Continue, an undefined one included, starts no traffic.
        if (decision != StepGateDecision.Continue)
        {
            if (!_state.StopEvent.WaitOne(0))
            {
                RequestStop(StopReason.HostFault);
            }

            return NativeConstants.ActionAbort;
        }

        Post(started, static (session, e) => session.Raise(session.StepStarted, e, StepStartedHandlerName));
        return NativeConstants.ActionContinue;
    }

    /// <summary>
    /// Handles <c>on_step_finished</c> on the coordinator: copies the step result and posts <see cref="StepFinished"/>.
    /// </summary>
    /// <param name="result">The engine's payload, valid only during the call.</param>
    internal void OnNativeStepFinished(in RqStepResult result)
    {
        var finished = new EngineStepFinished(EngineEventMapper.ToOutcome(in result, _steps));
        Post(finished, static (session, e) => session.Raise(session.StepFinished, e, StepFinishedHandlerName));
    }

    /// <summary>
    /// Handles <c>on_progress</c> on the coordinator: copies the snapshot, hands it to the progress sink synchronously
    /// and then posts <see cref="ProgressChanged"/>.
    /// </summary>
    /// <param name="progress">The engine's payload, valid only during the call.</param>
    /// <remarks>
    /// The sink runs before the post so that the journal's 1 Hz heartbeat sees the latest progress even while the UI
    /// context is busy. An exception it throws reaches the thunk, which stops the run with a host fault.
    /// </remarks>
    internal void OnNativeProgress(in RqProgress progress)
    {
        RunProgress snapshot = EngineEventMapper.ToProgress(in progress, _steps);
        _options.ProgressSink?.Invoke(snapshot);
        Post(snapshot, static (session, e) => session.Raise(session.ProgressChanged, e, ProgressChangedHandlerName));
    }

    /// <summary>
    /// Handles <c>on_errors</c> on the coordinator: copies the batch, which may be empty once the detail cap is reached,
    /// and posts <see cref="ErrorsDetected"/> with the true total.
    /// </summary>
    /// <param name="records">The engine's records, valid only during the call.</param>
    /// <param name="totalErrors">The run's exact true error total so far.</param>
    internal void OnNativeErrors(ReadOnlySpan<RqErrorRecord> records, ulong totalErrors)
    {
        IReadOnlyList<ErrorRecord> mapped = EngineEventMapper.ToErrorRecords(records, _steps, _options.ErrorDetailCap);
        var batch = new EngineErrorsDetected(mapped, totalErrors);
        Post(batch, static (session, e) => session.Raise(session.ErrorsDetected, e, ErrorsDetectedHandlerName));
    }

    /// <summary>
    /// Handles <c>on_completed</c> on the coordinator: records that the run has ended, copies the run result and posts
    /// <see cref="Completed"/>.
    /// </summary>
    /// <param name="result">The engine's payload, whose step results are valid only during the call.</param>
    internal void OnNativeCompleted(in RqRunResult result)
    {
        // Recorded before mapping: the native run has ended whether or not its payload maps, and Dispose then has no
        // stop left to request.
        Volatile.Write(ref _completed, 1);
        EngineCompleted completed = EngineEventMapper.ToCompleted(in result, _steps);
        Post(completed, static (session, e) => session.Raise(session.Completed, e, CompletedHandlerName));
    }

    /// <summary>
    /// Handles <c>on_released</c> on the coordinator: posts <see cref="Released"/> with the released bytes and release
    /// time.
    /// </summary>
    /// <param name="releasedBytes">The engine's <c>released_bytes</c>.</param>
    /// <param name="releaseMs">The engine's <c>release_ms</c>.</param>
    internal void OnNativeReleased(ulong releasedBytes, ulong releaseMs)
    {
        EngineReleased released = EngineEventMapper.ToReleased(releasedBytes, releaseMs);
        Post(released, static (session, e) => session.Raise(session.Released, e, ReleasedHandlerName));
    }

    /// <summary>
    /// Reports a managed exception caught in an engine callback or an event subscriber to the options' fault sink, then
    /// requests a host-fault stop, so the run ends ABORTED rather than continuing with lost events.
    /// </summary>
    /// <param name="callbackName">The callback or event in which the fault was caught, such as <c>on_progress</c>.</param>
    /// <param name="exception">The exception that was caught.</param>
    /// <remarks>
    /// The sink's owner writes the fault to <c>&lt;data&gt;\logs\app-errors.log</c>. An exception the sink throws is
    /// discarded, because this runs where no exception may unwind: inside a callback thunk or a posted event delivery.
    /// </remarks>
    internal void ReportCallbackFault(string callbackName, Exception exception)
    {
        try
        {
            _options.CallbackFault?.Invoke(callbackName, exception);
        }
        catch (Exception)
        {
            // Deliberately discarded: the fault sink must not throw, and its failure must not hide the original fault,
            // which the stop below still reports as a host fault.
        }

        RequestStop(StopReason.HostFault);
    }

    /// <summary>
    /// Copies the run result through <c>rq_session_get_result</c>, for the integration tests.
    /// </summary>
    /// <returns>The run result, or <see langword="null"/> before the engine has completed the run.</returns>
    /// <exception cref="ObjectDisposedException">The session's handle has been released.</exception>
    /// <exception cref="InvalidOperationException">The engine's result does not match the configured session.</exception>
    internal unsafe EngineCompleted? GetResult()
    {
        // The handle is held for the copy and the mapping: the step results the copy points to are session-owned and
        // stay valid only until rq_session_destroy, which a concurrent Dispose would otherwise run.
        bool added = false;
        _handle.DangerousAddRef(ref added);
        try
        {
            RqRunResult result = default;
            result.StructSize = RqRunResult.Size;
            int code = NativeMethods.RqSessionGetResult(_handle, &result);
            if (code == (int)EngineResultCode.InvalidState)
            {
                return null;
            }

            EngineResults.ThrowIfFailed(code, "rq_session_get_result");
            return EngineEventMapper.ToCompleted(in result, _steps);
        }
        finally
        {
            if (added)
            {
                _handle.DangerousRelease();
            }
        }
    }

    // Marshals the options into the native structs and calls the factory. Everything native reads lives on this stack
    // frame or in an array pinned for the call: rq_session_create deep-copies the configuration, the steps, their names
    // and the callback table before it returns.
    private static unsafe int CreateNative(
        EngineSessionOptions options,
        ReadOnlyCollection<StepAttributes> steps,
        nint userData,
        NativeSessionFactory factory,
        out nint nativeSession)
    {
        // Zero-initialised, so every reserved field is 0 as the ABI requires.
        var stepDefs = new RqStepDef[steps.Count];
        fixed (RqStepDef* stepDefsPointer = stepDefs)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                WriteStepDef(steps[i], &stepDefsPointer[i]);
            }

            RqSessionConfig config = new()
            {
                StructSize = RqSessionConfig.Size,
                StepDefSize = RqStepDef.Size,
                RegionBytes = options.RegionBytes,
                MinRegionBytes = options.MinRegionBytes,
                Seed = options.Seed,
                WorkerCount = (uint)options.WorkerCount,
                StopOnError = options.StopOnError ? 1u : 0u,
                ErrorDetailCap = (uint)options.ErrorDetailCap,
                ProgressIntervalMs = ToNativeMilliseconds(options.ProgressInterval),
                IsaOverride = options.IsaOverride is { } isa ? (uint)isa : NativeConstants.IsaAuto,
                StepCount = (uint)steps.Count,
                Steps = stepDefsPointer,
            };

            RqCallbacks callbacks = CallbackThunks.CreateTable(userData);
            nint raw = 0;
            int result = factory(&config, &callbacks, &raw);
            nativeSession = raw;
            return result;
        }
    }

    // The managed and native enum numberings are equal (NativeConstants), and Validate has range-checked every value, so
    // the conversions below are plain casts.
    private static unsafe void WriteStepDef(StepAttributes step, RqStepDef* def)
    {
        _ = FixedUtf8.Write(step.Name, def->Name, NativeConstants.NameSize);
        def->Pattern = (uint)step.Pattern;
        def->AccessOrder = (uint)step.Order;
        def->ThreadMode = (uint)step.Threads;
        def->HoldMs = step.Hold == TimeSpan.Zero ? 0u : ToNativeMilliseconds(step.Hold);
        def->TimeBudgetMs = ToNativeMilliseconds(step.TimeBudget);

        // Basis points of percent: 25 % is 2500. Validate bounds the percentage so the product fits a uint32_t.
        def->CoverageBudgetBp = checked((uint)step.CoverageBudgetPercent * 100u);
        def->Reserved0 = 0;
        def->Reserved1 = 0;
    }

    // Whole milliseconds by integer division of the ticks, the same truncation EngineSessionOptions.Validate checked,
    // so no floating-point rounding can move a value across the range it validated.
    private static uint ToNativeMilliseconds(TimeSpan value) => checked((uint)(value.Ticks / TimeSpan.TicksPerMillisecond));

    // The rq_session_create failure path: no native session exists, so the runtime never runs ReleaseHandle and the
    // stop event and GCHandle are released here.
    private static void DiscardWithoutSession(SafeSessionHandle handle)
    {
        handle.ReleaseCallbackHandleWithoutSession();
        handle.Dispose();
    }

    // Records the native reason first and only then sets the managed stop event: a step gate woken by the event must
    // find its reason already recorded, so it can never return Abort before the reason exists. Shared by RequestStop and
    // by Dispose, which has already marked the session disposed.
    private void StopCore(StopReason reason)
    {
        int result;
        try
        {
            result = NativeMethods.RqSessionStop(_handle, (uint)reason);
        }
        catch (ObjectDisposedException)
        {
            // A concurrent Dispose closed the handle; the session is stopped and destroyed by then.
            return;
        }

        // Stop is idempotent: RQ_E_INVALID_HANDLE after destruction or any later engine refusal changes nothing the
        // caller can act on. Only an invalid reason is a programming error, which RequestStop's enum check excludes.
        if (result == (int)EngineResultCode.InvalidArgument)
        {
            EngineResults.ThrowIfFailed(result, "rq_session_stop");
        }

        try
        {
            _state.StopEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // The handle released the event after rq_session_destroy returned; no gate can be waiting on it any more.
        }
    }

    // Posts one event record to the captured context. The work item holds the record and the weak session reference
    // only, never the session, so a queued event cannot keep an abandoned session alive.
    private void Post<TEvent>(TEvent record, Action<EngineSession, TEvent> raise)
        where TEvent : class
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _state.Context.Post(PostedEvent<TEvent>.Deliver, new PostedEvent<TEvent>(_state.Session, record, raise));
    }

    // Raises an event on the captured context, one subscriber at a time, so a subscriber that throws neither skips the
    // others nor lets its exception unwind into the context; a context that runs posts inline, such as
    // ImmediateSynchronizationContext in the tests, would otherwise carry it into native frames. A caught exception
    // stops the run with a host fault. No subscriber runs once Dispose has begun.
    private void Raise<TEvent>(EventHandler<TEvent>? handler, TEvent args, string handlerName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                ((EventHandler<TEvent>)subscriber)(this, args);
            }
            catch (Exception exception)
            {
                try
                {
                    ReportCallbackFault(handlerName, exception);
                }
                catch (Exception)
                {
                    // Deliberately discarded: an exception escaping a posted delivery would terminate the process on
                    // the thread pool. ReportCallbackFault swallows the sink's exceptions, so only a failed stop
                    // request can land here, and the remaining subscribers still receive the event.
                }
            }
        }
    }

    // A posted event: the weak session reference, the record and a static raise delegate. Deliver is a cached static
    // callback, so posting allocates only this carrier and captures nothing.
    private sealed class PostedEvent<TEvent>
        where TEvent : class
    {
        private readonly WeakReference<EngineSession> _session;
        private readonly TEvent _record;
        private readonly Action<EngineSession, TEvent> _raise;

        internal PostedEvent(WeakReference<EngineSession> session, TEvent record, Action<EngineSession, TEvent> raise)
        {
            _session = session;
            _record = record;
            _raise = raise;
        }

        internal static SendOrPostCallback Deliver { get; } = static state => ((PostedEvent<TEvent>)state!).Raise();

        private void Raise()
        {
            // A session collected before delivery has no subscribers left to notify.
            if (_session.TryGetTarget(out EngineSession? session))
            {
                _raise(session, _record);
            }
        }
    }
}

