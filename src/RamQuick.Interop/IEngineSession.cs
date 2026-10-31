using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// Why the host asks an engine session to stop, passed to <see cref="IEngineSession.RequestStop"/>.
/// </summary>
/// <remarks>
/// <para>
/// The values equal the native <c>RQ_STOP_USER</c>, <c>RQ_STOP_THERMAL</c> and <c>RQ_STOP_HOST_FAULT</c> codes, so the
/// wrapper passes <c>(uint)reason</c> to <c>rq_session_stop</c> unchanged. Each maps to the end reason of the same
/// meaning when it is the first reason the engine records.
/// </para>
/// <para>
/// The engine's own end reasons, a first error with Stop on error on and preparation or engine faults, compete with
/// these: whichever reason is recorded first becomes the run's end reason, so a requested stop does not by itself
/// decide the terminal state.
/// </para>
/// </remarks>
public enum StopReason : uint
{
    /// <summary>
    /// The user pressed Stop, or the main window is closing while a run is in progress. Ends the run as
    /// <see cref="RunEndReason.UserStop"/>, shown as ABORTED with "Stopped by user".
    /// </summary>
    User = 1,

    /// <summary>
    /// A fresh CPU or RAM temperature reading reached its thermal limit while the run was Running. Ends the run as
    /// <see cref="RunEndReason.Thermal"/>, shown as ABORTED with "Stopped for safety".
    /// </summary>
    Thermal = 2,

    /// <summary>
    /// The host can no longer support the run: a step journal record was not acknowledged as durable within its
    /// timeout or failed to write, the progress journal reported a write failure, or a managed exception was caught in
    /// an engine callback. Ends the run as <see cref="RunEndReason.HostFault"/>, shown as ABORTED.
    /// </summary>
    HostFault = 3,
}

/// <summary>
/// One engine run: the managed view of a native engine session, created by <c>IEngine.CreateSession</c>. This is the
/// seam <c>RunController</c> drives; the production implementation wraps <c>ramquick_engine.dll</c>, and tests
/// substitute a fake.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> <see cref="Start"/> and <see cref="RequestStop"/> never block and may be called from any thread.
/// Every event is copied into an immutable record and delivered through <see cref="SynchronizationContext.Post"/> of
/// the <see cref="SynchronizationContext"/> captured when the session was created, one event at a time and in the
/// order the engine raised it; an event still awaiting delivery holds the session only weakly, so it never keeps an
/// abandoned session alive. The session itself calls only the step gate and the progress sink of the session options
/// synchronously on the engine's coordinator thread, so the captured context decides where subscribers run. The
/// application's WPF dispatcher context raises them on the UI thread, and the thread-pool context used when none is
/// current raises them on a pool thread: neither ever raises an event on the coordinator or on a worker. A custom or
/// test context whose Post runs its work inline instead raises subscribers synchronously on the coordinator, inside
/// the engine callback. Such subscribers must not block, because the engine waits for them, and cannot dispose the
/// session, which is refused inside a callback.
/// </para>
/// <para>
/// <b>Event order.</b> A started session raises
/// <see cref="Prepared"/> → (<see cref="StepStarted"/> → <see cref="StepFinished"/>, with <see cref="ProgressChanged"/>
/// and <see cref="ErrorsDetected"/> between them)* → <see cref="Completed"/> → <see cref="Released"/>. A preparation
/// failure, such as a locking shortfall or an affinity failure, raises <see cref="Completed"/> and then
/// <see cref="Released"/> with no <see cref="Prepared"/>, and so does a session stopped before it started.
/// <see cref="Completed"/> is raised exactly once per started session and <see cref="Released"/> at most once: the
/// engine withholds <see cref="Released"/> when Windows refuses to free a chunk, because the release cannot be
/// confirmed. Every handler receives the session as <c>sender</c>.
/// </para>
/// <para>
/// <b>Disposal.</b> <see cref="IDisposable.Dispose"/> releases the native session: it stops a run still in progress,
/// waits for in-flight callbacks, joins every engine thread and releases the locked memory, so it can block until the
/// release finishes. Callers normally dispose after <see cref="Released"/> and after the run's summaries are
/// published. It is idempotent. Calling it from inside an engine callback, such as the step gate or the progress sink,
/// throws <see cref="InvalidOperationException"/>, because the engine cannot destroy a session from its own
/// coordinator. Events posted before disposal but delivered after it are dropped, and no event is raised once it
/// has returned. After disposal, <see cref="Start"/> throws <see cref="ObjectDisposedException"/> and
/// <see cref="RequestStop"/> does nothing.
/// </para>
/// </remarks>
public interface IEngineSession : IDisposable
{
    /// <summary>
    /// Raised once when the engine has allocated and locked the region under test and pinned one worker to each
    /// logical processor, before the first step starts. Not raised when preparation fails or the session was stopped
    /// before it started.
    /// </summary>
    event EventHandler<EnginePrepared>? Prepared;

    /// <summary>
    /// Raised when a step starts, only for steps whose step gate returned Continue. A step the gate aborted starts no
    /// memory traffic and raises neither this event nor <see cref="StepFinished"/>.
    /// </summary>
    event EventHandler<EngineStepStarted>? StepStarted;

    /// <summary>
    /// Raised once for every step that raised <see cref="StepStarted"/>, after its last pass, including a step a stop
    /// interrupted, whose outcome is not completed.
    /// </summary>
    event EventHandler<EngineStepFinished>? StepFinished;

    /// <summary>
    /// Raised with each progress snapshot, at most four times a second. The progress sink of the session options
    /// receives the same snapshot synchronously on the coordinator before this event is posted.
    /// </summary>
    event EventHandler<RunProgress>? ProgressChanged;

    /// <summary>
    /// Raised with each batch of confirmed errors, at most ten times a second. Once the detail cap is reached, batches
    /// carry no records while their true error total keeps rising.
    /// </summary>
    event EventHandler<EngineErrorsDetected>? ErrorsDetected;

    /// <summary>
    /// Raised exactly once per started session, after every worker has joined, with the run's end reason and true
    /// totals. The end reason is the first one the engine recorded and alone decides the terminal state. The locked
    /// memory is released afterwards, and <see cref="Released"/> follows. When the engine's result cannot be read or
    /// delivered, it is still raised, once and before <see cref="Released"/>, as a host fault whose end detail names
    /// the fault and the engine's own reason.
    /// </summary>
    event EventHandler<EngineCompleted>? Completed;

    /// <summary>
    /// Raised at most once per started session, after <see cref="Completed"/>, when the locked memory has been
    /// released; not raised when the engine cannot free every chunk. It is the last event of the session, and the run's
    /// summaries are published only after it, or without a release time once the host stops waiting for it.
    /// </summary>
    event EventHandler<EngineReleased>? Released;

    /// <summary>
    /// Starts the engine's coordinator thread and returns immediately; preparation, the steps and every event follow
    /// asynchronously. Allowed once per session.
    /// </summary>
    /// <remarks>
    /// If a stop was requested before this call, the engine raises <see cref="Completed"/>, with the end reason mapped
    /// from the first requested <see cref="StopReason"/>, and then <see cref="Released"/> straight away, with no
    /// <see cref="Prepared"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The session has already been started.</exception>
    /// <exception cref="EngineException">
    /// The engine could not start the run: <see cref="EngineResultCode.ThreadCreate"/> when the coordinator thread
    /// could not be created, or <see cref="EngineResultCode.Internal"/>. No event follows; the caller treats either as
    /// <see cref="RunEndReason.EngineFault"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    void Start();

    /// <summary>
    /// Requests that the run stop. Idempotent and non-blocking; callable from any thread, before or after
    /// <see cref="Start"/>, including from inside the step gate on the coordinator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first reason recorded for the session wins and becomes the run's end reason; a later request, with the same
    /// or another reason, changes nothing. Reasons the engine records itself, such as a first error with Stop on error
    /// on, compete on the same first-writer-wins basis.
    /// </para>
    /// <para>
    /// Ordering is binding: the native reason is recorded, and <c>rq_session_stop</c> has returned, before the managed
    /// stop event that releases a waiting step gate is set. A gate woken by that event therefore always finds its
    /// reason already recorded and can never return Abort before the reason exists. A stop the engine refuses records
    /// no reason and leaves the event unset.
    /// </para>
    /// <para>
    /// While a run is in progress, the engine reports the stop through <see cref="Completed"/> within 2 seconds of the
    /// request. After the session has been disposed, this method does nothing.
    /// </para>
    /// </remarks>
    /// <param name="reason">Why the host is stopping the run.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a defined <see cref="StopReason"/> value.
    /// </exception>
    /// <exception cref="EngineException">
    /// The engine could not record the stop, for example with <see cref="EngineResultCode.Internal"/>. The stop event
    /// is not set and the run is not stopped; the caller treats it as <see cref="RunEndReason.EngineFault"/>.
    /// </exception>
    void RequestStop(StopReason reason);
}
