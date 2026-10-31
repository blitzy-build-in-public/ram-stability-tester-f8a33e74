namespace RamQuick.Interop;

/// <summary>
/// A failure result returned by a <c>ramquick_engine.dll</c> function that has no more specific
/// managed exception: chiefly <see cref="EngineResultCode.ThreadCreate"/> (<c>RQ_E_THREAD_CREATE</c>),
/// <see cref="EngineResultCode.VersionMismatch"/> (<c>RQ_E_VERSION_MISMATCH</c>) and
/// <see cref="EngineResultCode.Internal"/> (<c>RQ_E_INTERNAL</c>).
/// </summary>
/// <remarks>
/// <para>
/// The wrapper's result-code mapping (<c>EngineResults</c>) composes <see cref="Exception.Message"/>,
/// for example <c>"rq_session_start failed (ThreadCreate, -6): &lt;native message&gt;"</c>, and passes
/// the <c>rq_result_message</c> text as <see cref="NativeMessage"/>. This type only carries them.
/// </para>
/// <para>
/// End reasons that arrive through <c>on_completed</c> are never thrown as this exception; they surface
/// as typed results. <c>RunController</c> treats <see cref="EngineResultCode.ThreadCreate"/> or
/// <see cref="EngineResultCode.Internal"/> from <c>Start()</c> as <c>RunEndReason.EngineFault</c>, and
/// <see cref="EngineResultCode.VersionMismatch"/> at bind time as a corrupted installation.
/// </para>
/// <para>
/// There is no serialization constructor: binary-formatter serialization of exceptions is obsolete
/// in .NET 8 (SYSLIB0051), and this exception never crosses a process or AppDomain boundary.
/// </para>
/// </remarks>
public sealed class EngineException : Exception
{
    /// <summary>
    /// Initializes a new instance with the default exception message and
    /// <see cref="Code"/> set to <see cref="EngineResultCode.Internal"/>.
    /// </summary>
    public EngineException()
    {
        Code = EngineResultCode.Internal;
    }

    /// <summary>
    /// Initializes a new instance with the specified message and
    /// <see cref="Code"/> set to <see cref="EngineResultCode.Internal"/>.
    /// </summary>
    /// <param name="message">The message that describes the failure.</param>
    public EngineException(string message)
        : base(message)
    {
        Code = EngineResultCode.Internal;
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception, and
    /// <see cref="Code"/> set to <see cref="EngineResultCode.Internal"/>.
    /// </summary>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = EngineResultCode.Internal;
    }

    /// <summary>
    /// Initializes a new instance for a result code returned by an engine function.
    /// </summary>
    /// <param name="code">
    /// The engine result code. Values outside the defined members are kept as returned, so an
    /// unknown native code still reaches diagnostics with its raw number.
    /// </param>
    /// <param name="message">The composed message, naming the failed operation and the code.</param>
    /// <param name="nativeMessage">
    /// The engine's own text for <paramref name="code"/> from <c>rq_result_message</c>, or
    /// <see langword="null"/> when it could not be obtained.
    /// </param>
    /// <param name="innerException">
    /// The exception that caused this failure, or <see langword="null"/> when there is none.
    /// </param>
    public EngineException(
        EngineResultCode code,
        string message,
        string? nativeMessage = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        NativeMessage = nativeMessage;
    }

    /// <summary>
    /// Gets the engine result code that caused this exception.
    /// <see cref="EngineResultCode.Internal"/> when the exception was created without one.
    /// </summary>
    public EngineResultCode Code { get; }

    /// <summary>
    /// Gets the engine's own text for <see cref="Code"/>, as returned by <c>rq_result_message</c>,
    /// or <see langword="null"/> when none was supplied.
    /// </summary>
    public string? NativeMessage { get; }
}
