namespace RamQuick.Interop;

/// <summary>
/// The per-session object native code can reach: the target of the
/// <see cref="System.Runtime.InteropServices.GCHandle"/> whose address the engine receives as <c>user_data</c> and
/// passes back to every callback thunk.
/// </summary>
/// <remarks>
/// <para>
/// The GCHandle is a strong root for as long as the native session exists, so everything reachable from this object
/// stays alive with it. It therefore holds only the captured synchronization context, the managed stop event and a
/// <em>weak</em> reference to the session. It holds no delegate, event, session options, session handle or
/// <see cref="EngineSession"/> field: any strong path from here to the session, including a <c>StepGate</c> whose
/// target holds the session, would root an abandoned session through the GCHandle. Its
/// <c>SafeSessionHandle</c> could then never be finalized, so the native session and its locked memory would
/// never be released. <c>SessionLifetimeTests</c> assert that an abandoned session, and this object after it, are
/// collected.
/// </para>
/// <para>
/// No delegate needs keeping alive here because the callbacks the engine calls are static
/// <c>[UnmanagedCallersOnly]</c> thunks; each call resolves <see cref="Session"/> and holds the session strongly only
/// for the duration of that call.
/// </para>
/// </remarks>
internal sealed class CallbackState
{
    private int _sessionAttached;

    /// <summary>Creates the callback state for one engine session, before the session is attached.</summary>
    /// <param name="context">
    /// The context <see cref="EngineSession"/> posts events to: the UI context in the application, or a
    /// thread-pool-posting <see cref="SynchronizationContext"/> when none was current at creation.
    /// </param>
    /// <param name="stopEvent">The session's managed stop event, created unsignalled.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="stopEvent"/> is null.
    /// </exception>
    internal CallbackState(SynchronizationContext context, ManualResetEvent stopEvent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stopEvent);

        Context = context;
        StopEvent = stopEvent;

        // A reference with no target: until AttachSession runs, a thunk resolving it finds no session and treats the
        // session as dead (events dropped, step gate aborts).
        Session = new WeakReference<EngineSession>(null!);
    }

    /// <summary>Gets the context every engine event is posted to.</summary>
    public SynchronizationContext Context { get; }

    /// <summary>
    /// Gets the session's managed stop event, which a waiting <c>StepGate</c> sees as a <see cref="WaitHandle"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="EngineSession"/> sets it only after <c>rq_session_stop</c> has returned, so a gate woken by it always
    /// finds the native stop reason already recorded. <c>SafeSessionHandle</c> disposes it after
    /// <c>rq_session_destroy</c> returns, when no callback is running or can start.
    /// </remarks>
    public ManualResetEvent StopEvent { get; }

    /// <summary>
    /// Gets the weak reference through which a callback thunk resolves its session; a reference with no target means
    /// the session is dead, either not yet attached or already collected.
    /// </summary>
    public WeakReference<EngineSession> Session { get; private set; }

    /// <summary>
    /// Attaches the session this state serves. Called exactly once, before the GCHandle is handed to the engine.
    /// </summary>
    /// <param name="session">The session whose callbacks this state routes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A session has already been attached.</exception>
    internal void AttachSession(EngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Interlocked.Exchange(ref _sessionAttached, 1) != 0)
        {
            throw new InvalidOperationException("A session is already attached to this callback state.");
        }

        Session = new WeakReference<EngineSession>(session);
    }
}
