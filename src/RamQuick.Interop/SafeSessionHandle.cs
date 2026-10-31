using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RamQuick.Interop;

/// <summary>
/// Owns one native engine session (<c>rq_session_handle</c>) together with the <see cref="GCHandle"/> to its
/// <see cref="CallbackState"/>, whose address the engine receives as <c>user_data</c>.
/// </summary>
/// <remarks>
/// <para>
/// Releasing the handle, through <see cref="SafeHandle.Dispose()"/> or, for an abandoned session, the critical
/// finalizer, calls <c>rq_session_destroy</c>. That stops a run still in progress, waits for in-flight callbacks,
/// joins the coordinator and workers, releases the locked memory and frees the native session. Only then are the
/// session's managed stop event disposed and the GCHandle freed, because until destroy returns a callback may still
/// resolve <c>user_data</c> or a step gate may still wait on the event. Once the GCHandle is freed the
/// <see cref="CallbackState"/> is collectible.
/// </para>
/// <para>
/// As a by-value P/Invoke argument this handle is reference-counted for the duration of each call, so a concurrent
/// <see cref="SafeHandle.Dispose()"/> cannot destroy the session under a running <c>rq_session_*</c> call. The release
/// then runs when that call drops the last reference, on its thread. When that thread is an engine coordinator inside a
/// callback, for example a step gate's <c>rq_session_stop</c>, the engine refuses <c>rq_session_destroy</c> there, so
/// <see cref="ReleaseHandle"/> hands destruction to a dedicated release thread, and <see cref="WaitForRelease"/> lets the
/// disposer wait for it.
/// </para>
/// <para>
/// Lifecycle: <c>EngineSession</c> constructs the handle around the GCHandle before <c>rq_session_create</c>, passes
/// <see cref="CallbackUserData"/> to it, and calls <see cref="Attach(nint)"/> immediately after it succeeds. When it
/// fails, the handle stays invalid, so the runtime never calls <see cref="ReleaseHandle"/>; the caller then calls
/// <see cref="ReleaseCallbackHandleWithoutSession"/> and disposes the handle. <see cref="Attach(nint)"/> and
/// <see cref="ReleaseCallbackHandleWithoutSession"/> run on the creating thread before the handle is shared.
/// </para>
/// </remarks>
internal sealed class SafeSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    // RQ_OK and RQ_E_INVALID_HANDLE (ramquick_engine.h): the only rq_session_destroy results after which the engine
    // holds no session that could still hand user_data to a callback.
    private const int ResultOk = 0;
    private const int ResultInvalidHandle = -2;

    private const string ReleaseThreadName = "RamQuick session release";

    // A normal (not pinned) handle: the engine only stores and returns its value, which stays stable however the
    // CallbackState moves. Not readonly, because Free() mutates the struct in place.
    private GCHandle _callbackHandle;

    // Completed once the release has run to its end, on whichever thread, failures included, so a waiter never hangs.
    // A TaskCompletionSource needs no disposal, which a handle released by its finalizer could not provide.
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Creates a handle that owns <paramref name="callbackHandle"/> and, as yet, no native session.
    /// </summary>
    /// <param name="callbackHandle">
    /// An allocated GCHandle whose target is the session's <see cref="CallbackState"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="callbackHandle"/> is not allocated, or its target is not a <see cref="CallbackState"/>.
    /// </exception>
    internal SafeSessionHandle(GCHandle callbackHandle)
        : base(ownsHandle: true)
    {
        if (!callbackHandle.IsAllocated || callbackHandle.Target is not CallbackState)
        {
            throw new ArgumentException(
                "The callback handle must be an allocated GCHandle whose target is a CallbackState.",
                nameof(callbackHandle));
        }

        _callbackHandle = callbackHandle;
    }

    /// <summary>
    /// Gets the value passed to the engine as <c>user_data</c>: the address of the GCHandle to the
    /// <see cref="CallbackState"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GCHandle has already been freed.</exception>
    internal nint CallbackUserData
    {
        get
        {
            // GCHandle.ToIntPtr does not validate: for a freed handle it returns 0, which the engine would store as
            // user_data.
            if (!_callbackHandle.IsAllocated)
            {
                throw new InvalidOperationException("The callback state of this handle has already been released.");
            }

            return GCHandle.ToIntPtr(_callbackHandle);
        }
    }

    /// <summary>
    /// Takes ownership of the native session returned by a successful <c>rq_session_create</c>. Call exactly once,
    /// immediately after that call returns <c>RQ_OK</c>.
    /// </summary>
    /// <param name="nativeSession">The session handle the engine returned.</param>
    /// <exception cref="ObjectDisposedException">This handle has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// A session is already attached, <paramref name="nativeSession"/> is not a valid session handle (0 or -1), or
    /// the GCHandle to the callback state has already been freed.
    /// </exception>
    internal void Attach(nint nativeSession)
    {
        // A closed handle never runs ReleaseHandle, so attaching to one would leak the session and its locked memory.
        ObjectDisposedException.ThrowIf(IsClosed, this);

        if (!IsInvalid)
        {
            throw new InvalidOperationException("A native session is already attached to this handle.");
        }

        if (nativeSession == 0 || nativeSession == -1)
        {
            throw new InvalidOperationException("rq_session_create reported success without a valid session handle.");
        }

        // A session created with a freed user_data would resolve a dangling GCHandle in its first callback.
        if (!_callbackHandle.IsAllocated)
        {
            throw new InvalidOperationException("The callback state of this handle has already been released.");
        }

        SetHandle(nativeSession);
    }

    /// <summary>
    /// Disposes the session's stop event and frees the GCHandle to its <see cref="CallbackState"/>, for the
    /// <c>rq_session_create</c> failure path only, where no native session exists and the runtime will never call
    /// <see cref="ReleaseHandle"/>. Idempotent. Dispose this handle afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A native session is attached: it still passes <c>user_data</c> to its callbacks, so the GCHandle may be freed
    /// only by <see cref="ReleaseHandle"/>, after <c>rq_session_destroy</c>.
    /// </exception>
    internal void ReleaseCallbackHandleWithoutSession()
    {
        if (!IsInvalid)
        {
            throw new InvalidOperationException(
                "A native session is attached; dispose the handle to destroy the session and release its callback "
                + "state.");
        }

        ReleaseCallbackState();
    }

    /// <summary>
    /// Blocks until the release that <see cref="SafeHandle.Dispose()"/> started has finished, on whichever thread
    /// dropped the last reference: the disposing thread itself, a thread whose <c>rq_session_*</c> call still held the
    /// handle, or the release thread to which a callback's last release handed destruction. Returns at once when no
    /// native session was ever attached, because the runtime then never runs <see cref="ReleaseHandle"/>.
    /// </summary>
    /// <remarks>
    /// Call it after <see cref="SafeHandle.Dispose()"/>. Never call it while the calling thread itself holds a
    /// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> reference, which keeps the release from starting, nor inside an
    /// engine callback, whose coordinator <c>rq_session_destroy</c> must join: either way the call would never return.
    /// </remarks>
    internal void WaitForRelease()
    {
        if (IsInvalid)
        {
            return;
        }

        _released.Task.Wait();
    }

    /// <summary>
    /// Destroys the native session, then disposes the stop event and frees the GCHandle to the callback state; inside an
    /// engine callback, starts a release thread that does so.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when <c>rq_session_destroy</c> returned <c>RQ_OK</c>, or when destruction was handed to
    /// the release thread; <see langword="false"/> when it returned another code, could not be called, or the release
    /// thread could not be started.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Runs once, on the thread that drops the last reference: <see cref="SafeHandle.Dispose()"/>, the critical
    /// finalizer, or a P/Invoke or <see cref="SafeHandle.DangerousRelease"/> whose reference outlived a concurrent
    /// <see cref="SafeHandle.Dispose()"/>. It must neither throw nor touch the <c>EngineSession</c>, which may already
    /// have been collected.
    /// </para>
    /// <para>
    /// That thread can be an engine coordinator inside a callback, whose own <c>rq_session_*</c> call still held the
    /// handle. <c>rq_session_destroy</c> would have to join that coordinator, so the engine refuses it there with
    /// <c>RQ_E_INVALID_STATE</c> and leaves the session alive, and a <see cref="SafeHandle"/> releases only once. The
    /// destruction therefore moves to a new background thread, which the coordinator only starts and never waits for:
    /// the release thread's <c>rq_session_destroy</c> waits for the callback to return and joins the coordinator, and
    /// only then frees the callback state.
    /// </para>
    /// </remarks>
    protected override bool ReleaseHandle()
    {
        if (!CallbackThunks.IsInCallback)
        {
            return DestroySession();
        }

        try
        {
            var releaser = new Thread(static state => ((SafeSessionHandle)state!).DestroySession())
            {
                IsBackground = true,
                Name = ReleaseThreadName,
            };
            releaser.UnsafeStart(this);
        }
        catch (Exception)
        {
            // An exception escaping here would unwind into the callback's frames. Destruction is unconfirmed, so the
            // native session may still be alive and calling back with user_data: the callback state stays rooted, as
            // after a failed destroy, and a waiting disposer is released rather than left blocked.
            _released.TrySetResult();
            return false;
        }

        return true;
    }

    // rq_session_destroy is noexcept, but binding its import on first use can raise a managed exception, for example
    // when the engine library that created the session lacks the export; the call is therefore guarded. Runs on the
    // thread that dropped the last reference or on the release thread, where an escaping exception would terminate the
    // process, and completes the release whatever the outcome.
    private bool DestroySession()
    {
        try
        {
            int result;
            try
            {
                result = NativeMethods.RqSessionDestroy(handle);
            }
            catch (Exception)
            {
                // Nothing can be reported from a finalizer or the release thread. Destruction is unconfirmed, so the
                // native session may still be alive and calling back with user_data: the callback state stays rooted,
                // as after a failed destroy.
                return false;
            }

            // Any other result may leave a live native session that still calls back with user_data, or a step gate
            // still waiting on the stop event. The callback state then stays rooted, a small leak, rather than freed
            // under them.
            if (result is ResultOk or ResultInvalidHandle)
            {
                try
                {
                    ReleaseCallbackState();
                }
                catch (Exception)
                {
                    // Nothing can be reported from a finalizer or the release thread, and the native session is
                    // already destroyed; an exception escaping here would terminate the process. The worst outcome of
                    // ignoring it is that the callback state stays rooted.
                }
            }

            return result == ResultOk;
        }
        finally
        {
            _released.TrySetResult();
        }
    }

    private void ReleaseCallbackState()
    {
        if (!_callbackHandle.IsAllocated)
        {
            return;
        }

        if (_callbackHandle.Target is CallbackState state)
        {
            state.StopEvent.Dispose();
        }

        _callbackHandle.Free();
    }
}
