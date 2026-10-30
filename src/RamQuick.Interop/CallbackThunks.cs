using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RamQuick.Interop;

/// <summary>
/// The engine's callback entry points: static <c>[UnmanagedCallersOnly]</c> thunks whose addresses fill
/// <see cref="RqCallbacks"/>, each routing one native callback to the <see cref="EngineSession"/> it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The engine holds function pointers to static methods, not delegates, so there is nothing for the garbage collector
/// to free while native code can still call it. The thunks capture nothing: the only per-session state they see is
/// <c>user_data</c>, the address of the <see cref="GCHandle"/> that roots the session's <see cref="CallbackState"/>.
/// </para>
/// <para>
/// Each call resolves its session through the weak <see cref="CallbackState.Session"/> reference and holds it strongly
/// in a local only for the duration of that call, so no callback keeps an abandoned session alive. A session that is
/// not attached or has been collected is dead: its events are dropped, and its step gate returns
/// <see cref="NativeConstants.ActionAbort"/>, which the engine records as <c>host_fault</c> when no stop reason exists.
/// </para>
/// <para>
/// No managed exception may unwind into native frames, because the runtime then terminates the process. Every thunk
/// therefore catches every exception and reports it to a live session through
/// <see cref="EngineSession.ReportCallbackFault"/>, which requests a host-fault stop, so the run ends ABORTED rather
/// than continuing with lost events. A faulted step gate also returns Abort, so the step starts no memory traffic.
/// </para>
/// <para>
/// Every callback runs on the session's coordinator thread and returns as soon as its handler does: only the step gate
/// may block, for at most its journal-acknowledgment timeout.
/// </para>
/// </remarks>
internal static unsafe class CallbackThunks
{
    // Callback nesting depth on the current thread; nonzero only on an engine coordinator inside a thunk.
    [ThreadStatic]
    private static int _depth;

    /// <summary>
    /// Gets a value indicating whether the current thread is running inside an engine callback, where
    /// <c>rq_session_destroy</c> cannot run because it would have to join the coordinator that is calling it.
    /// </summary>
    internal static bool IsInCallback => _depth > 0;

    /// <summary>
    /// Builds the callback table for <c>rq_session_create</c>, which deep-copies it, so the returned value may live on
    /// the caller's stack.
    /// </summary>
    /// <param name="userData">
    /// The <c>user_data</c> the engine passes back to every callback: <see cref="GCHandle.ToIntPtr"/> of the handle
    /// that roots the session's <see cref="CallbackState"/>. It must stay allocated until <c>rq_session_destroy</c>
    /// returns.
    /// </param>
    /// <returns>A table with every function pointer set to its thunk.</returns>
    internal static RqCallbacks CreateTable(nint userData) => new()
    {
        StructSize = RqCallbacks.Size,
        Reserved0 = 0,
        UserData = (void*)userData,
        OnPrepared = &OnPrepared,
        OnStepStarting = &OnStepStarting,
        OnStepFinished = &OnStepFinished,
        OnProgress = &OnProgress,
        OnErrors = &OnErrors,
        OnCompleted = &OnCompleted,
        OnReleased = &OnReleased,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPrepared(void* userData, RqPrepared* info) => _ = Run(userData, new PreparedCall(info));

    // The step gate: a dead or faulted session yields Abort, so the step starts no traffic.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint OnStepStarting(void* userData, RqStepStart* info) => Run(userData, new StepStartingCall(info));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStepFinished(void* userData, RqStepResult* result) =>
        _ = Run(userData, new StepFinishedCall(result));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnProgress(void* userData, RqProgress* progress) =>
        _ = Run(userData, new ProgressCall(progress));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnErrors(void* userData, RqErrorRecord* records, uint count, ulong totalErrors) =>
        _ = Run(userData, new ErrorsCall(records, count, totalErrors));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnCompleted(void* userData, RqRunResult* result) =>
        _ = Run(userData, new CompletedCall(result));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnReleased(void* userData, ulong releasedBytes, ulong releaseMs) =>
        _ = Run(userData, new ReleasedCall(releasedBytes, releaseMs));

    /// <summary>
    /// The body every thunk shares: resolves the session, delivers the call to its handler and contains every
    /// exception.
    /// </summary>
    /// <returns>
    /// The handler's <c>RQ_ACTION_*</c> result for a live session; <see cref="NativeConstants.ActionAbort"/> for a dead
    /// session or a fault. Only the step gate returns it to the engine; the other thunks discard it.
    /// </returns>
    private static uint Run<TCall>(void* userData, TCall call)
        where TCall : struct, IThunkCall
    {
        _depth++;
        EngineSession? session = null;
        try
        {
            return TryResolve(userData, out session) ? call.Deliver(session) : NativeConstants.ActionAbort;
        }
        catch (Exception exception)
        {
            ReportFault(session, call.CallbackName, exception);
            return NativeConstants.ActionAbort;
        }
        finally
        {
            _depth--;
        }
    }

    private static bool TryResolve(void* userData, [NotNullWhen(true)] out EngineSession? session)
    {
        session = null;
        if (userData == null)
        {
            return false;
        }

        return GCHandle.FromIntPtr((nint)userData).Target is CallbackState state
            && state.Session.TryGetTarget(out session);
    }

    private static void ReportFault(EngineSession? session, string callbackName, Exception exception)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            session.ReportCallbackFault(callbackName, exception);
        }
        catch (Exception)
        {
            // Deliberately discarded: nothing may unwind into the engine. ReportCallbackFault already swallows the
            // fault sink's exceptions, so only its stop request can land here, and a faulted step gate still returns
            // Abort, which the engine ends as host_fault.
        }
    }

    // The engine never passes a null payload; one would be an ABI violation, reported as a host fault rather than
    // dereferenced.
    private static ref readonly T Payload<T>(T* payload, string callbackName)
        where T : unmanaged
    {
        if (payload == null)
        {
            throw new InvalidOperationException($"The engine raised {callbackName} with a null payload pointer.");
        }

        return ref *payload;
    }

    // One native callback's arguments plus the handler they go to. The calls are structs passed to a constrained
    // generic, so each thunk gets its own specialised Run with no boxing and no delegate.
    private interface IThunkCall
    {
        // The native callback name, as reported with a fault.
        string CallbackName { get; }

        uint Deliver(EngineSession session);
    }

    private readonly struct PreparedCall : IThunkCall
    {
        private readonly RqPrepared* _info;

        public PreparedCall(RqPrepared* info) => _info = info;

        public string CallbackName => "on_prepared";

        public uint Deliver(EngineSession session)
        {
            session.OnNativePrepared(in Payload(_info, CallbackName));
            return NativeConstants.ActionContinue;
        }
    }

    private readonly struct StepStartingCall : IThunkCall
    {
        private readonly RqStepStart* _info;

        public StepStartingCall(RqStepStart* info) => _info = info;

        public string CallbackName => "on_step_starting";

        public uint Deliver(EngineSession session) => session.OnNativeStepStarting(in Payload(_info, CallbackName));
    }

    private readonly struct StepFinishedCall : IThunkCall
    {
        private readonly RqStepResult* _result;

        public StepFinishedCall(RqStepResult* result) => _result = result;

        public string CallbackName => "on_step_finished";

        public uint Deliver(EngineSession session)
        {
            session.OnNativeStepFinished(in Payload(_result, CallbackName));
            return NativeConstants.ActionContinue;
        }
    }

    private readonly struct ProgressCall : IThunkCall
    {
        private readonly RqProgress* _progress;

        public ProgressCall(RqProgress* progress) => _progress = progress;

        public string CallbackName => "on_progress";

        public uint Deliver(EngineSession session)
        {
            session.OnNativeProgress(in Payload(_progress, CallbackName));
            return NativeConstants.ActionContinue;
        }
    }

    private readonly struct ErrorsCall : IThunkCall
    {
        private readonly RqErrorRecord* _records;
        private readonly uint _count;
        private readonly ulong _totalErrors;

        public ErrorsCall(RqErrorRecord* records, uint count, ulong totalErrors)
        {
            _records = records;
            _count = count;
            _totalErrors = totalErrors;
        }

        public string CallbackName => "on_errors";

        public uint Deliver(EngineSession session)
        {
            // The ABI allows a null pointer only with a count of 0. A nonzero count advertises records that cannot be
            // read, so it is an ABI fault, which Run reports as a host fault, rather than an empty batch that would
            // silently drop them.
            if (_records == null && _count != 0)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The engine raised {CallbackName} with a null records pointer and a count of {_count}."));
            }

            // Once the detail cap is reached, batches carry no records (count 0, null pointer) while the true total
            // keeps rising, so an empty batch is still delivered for its total.
            ReadOnlySpan<RqErrorRecord> records = _count == 0
                ? ReadOnlySpan<RqErrorRecord>.Empty
                : new ReadOnlySpan<RqErrorRecord>(_records, checked((int)_count));
            session.OnNativeErrors(records, _totalErrors);
            return NativeConstants.ActionContinue;
        }
    }

    private readonly struct CompletedCall : IThunkCall
    {
        private readonly RqRunResult* _result;

        public CompletedCall(RqRunResult* result) => _result = result;

        public string CallbackName => "on_completed";

        public uint Deliver(EngineSession session)
        {
            // Unlike the other payloads, a null result is not thrown here: on_completed is raised once, and an
            // exception would drop the run's only Completed, so the session reports the fault and still delivers a
            // host-fault Completed.
            if (_result == null)
            {
                session.OnNativeCompletedWithoutResult();
            }
            else
            {
                session.OnNativeCompleted(in *_result);
            }

            return NativeConstants.ActionContinue;
        }
    }

    private readonly struct ReleasedCall : IThunkCall
    {
        private readonly ulong _releasedBytes;
        private readonly ulong _releaseMs;

        public ReleasedCall(ulong releasedBytes, ulong releaseMs)
        {
            _releasedBytes = releasedBytes;
            _releaseMs = releaseMs;
        }

        public string CallbackName => "on_released";

        public uint Deliver(EngineSession session)
        {
            session.OnNativeReleased(_releasedBytes, _releaseMs);
            return NativeConstants.ActionContinue;
        }
    }
}
