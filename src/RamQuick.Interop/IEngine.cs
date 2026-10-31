using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// The test engine as the application sees it: a probe of the machine and a factory for engine sessions. This is the
/// managed seam over <c>ramquick_engine.dll</c>; the production implementation, <c>NativeEngine</c>, calls the flat C
/// API, and tests substitute a fake so that <c>RunController</c> and the view model run without native code.
/// </summary>
/// <remarks>
/// <para>
/// Which engine library the production implementation loads is decided once per process by
/// <see cref="EngineLibrary"/>, before the first engine call.
/// </para>
/// <para>
/// Each run's state lives in the <see cref="IEngineSession"/> that <see cref="CreateSession"/> returns, which the
/// caller owns.
/// </para>
/// </remarks>
public interface IEngine
{
    /// <summary>
    /// Describes the machine as the engine probe sees it now (<c>rq_query_system</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query has no side effects: it creates no session, allocates nothing large, and only observes whether the
    /// process token holds <c>SeLockMemoryPrivilege</c>, never enabling it. It may be called from any thread and as
    /// often as needed; each call reports the physical memory available at that moment.
    /// </para>
    /// <para>
    /// The snapshot reports the CPU name, the logical processor and processor group counts, the best instruction set
    /// the CPU and OS support, total and available physical memory, the large-page minimum and whether
    /// <c>SeLockMemoryPrivilege</c> is already held. <c>RunController</c> captures it at Start to size the region with
    /// <c>MemorySizing</c>, to set <see cref="EngineSessionOptions.WorkerCount"/> to
    /// <see cref="SystemSnapshot.LogicalProcessors"/>, and to record the machine in the progress journal's
    /// <c>begin</c> record.
    /// </para>
    /// </remarks>
    /// <returns>An immutable snapshot of the machine at the time of the call.</returns>
    /// <exception cref="EngineException">
    /// The engine reported a failure, or its API version does not match this wrapper
    /// (<see cref="EngineResultCode.VersionMismatch"/>), which the application reports as a corrupted installation.
    /// </exception>
    /// <exception cref="DllNotFoundException">The bound engine library could not be loaded.</exception>
    SystemSnapshot QuerySystem();

    /// <summary>
    /// Creates an idle engine session for one run (<c>rq_session_create</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The options are validated with <see cref="EngineSessionOptions.Validate"/>, then marshalled into the native
    /// configuration and step array, which the engine validates again and deep-copies, so later changes to
    /// <paramref name="options"/> or its step list never reach the session. Creation allocates no test memory and
    /// starts no thread: the region is allocated and locked only after <see cref="IEngineSession.Start"/>, during
    /// Preparing.
    /// </para>
    /// <para>
    /// The session captures <see cref="SynchronizationContext.Current"/> of the calling thread and posts every one of
    /// its events there, so the application calls this method on the UI thread, whose dispatcher raises them. The
    /// session itself calls only the <see cref="EngineSessionOptions.StepGate"/> and
    /// <see cref="EngineSessionOptions.ProgressSink"/> synchronously on the engine's coordinator thread; a context
    /// whose <see cref="SynchronizationContext.Post"/> runs inline raises the events there too, as
    /// <see cref="IEngineSession"/> describes.
    /// </para>
    /// <para>
    /// The caller owns the returned session and must dispose it, normally after
    /// <see cref="IEngineSession.Released"/> has been raised and the run's summaries have been published. Disposal
    /// destroys the native session and releases any memory it still holds.
    /// </para>
    /// </remarks>
    /// <param name="options">The region, workers, plan steps and coordinator hooks of the run.</param>
    /// <returns>A session that has not been started.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> is invalid: rejected by <see cref="EngineSessionOptions.Validate"/>, which throws
    /// <see cref="ArgumentOutOfRangeException"/> for an out-of-range value, or by the engine
    /// (<see cref="EngineResultCode.InvalidArgument"/>) for a machine-dependent rule, such as a worker count above the
    /// logical processor count or a forced instruction set the CPU lacks. Either is a programming error.
    /// </exception>
    /// <exception cref="EngineException">
    /// The engine reported a failure, or its API version does not match this wrapper
    /// (<see cref="EngineResultCode.VersionMismatch"/>).
    /// </exception>
    /// <exception cref="DllNotFoundException">The bound engine library could not be loaded.</exception>
    IEngineSession CreateSession(EngineSessionOptions options);
}
