using System.Globalization;

namespace RamQuick.Interop;

/// <summary>
/// The <c>rq_result</c> codes of <c>ramquick_engine.h</c>: <c>RQ_OK</c> and the negative <c>RQ_E_*</c> values. Each
/// member equals its native constant.
/// </summary>
/// <remarks>
/// <para>
/// A code returned by an engine function is a programming error or an engine fault, and the wrapper throws it as a
/// managed exception: <see cref="InvalidArgument"/> as <see cref="ArgumentException"/>, <see cref="InvalidHandle"/> as
/// <see cref="ObjectDisposedException"/>, <see cref="InvalidState"/> as <see cref="InvalidOperationException"/>, and
/// every other failure as <see cref="EngineException"/>.
/// </para>
/// <para>
/// Expected run outcomes are not returned codes. Insufficient memory, pinning failures and engine faults during a run
/// arrive through <c>on_completed</c> as an end reason and surface as a typed result, never as an exception.
/// </para>
/// </remarks>
public enum EngineResultCode : int
{
    /// <summary><c>RQ_OK</c>: the call succeeded.</summary>
    Ok = 0,

    /// <summary>
    /// <c>RQ_E_INVALID_ARGUMENT</c>: a null or invalid pointer, a wrong <c>struct_size</c> or <c>step_def_size</c>, a
    /// bad count, an empty or unterminated name, an out-of-range value, a zero budget, <c>region_bytes</c> below
    /// <c>min_region_bytes</c>, an unavailable forced instruction set or an invalid stop reason. A programming error,
    /// thrown as <see cref="ArgumentException"/>.
    /// </summary>
    InvalidArgument = -1,

    /// <summary>
    /// <c>RQ_E_INVALID_HANDLE</c>: a null session handle, or one unknown to the engine or already destroyed. Thrown as
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    InvalidHandle = -2,

    /// <summary>
    /// <c>RQ_E_INVALID_STATE</c>: the call is not valid in the session's current state, such as a second
    /// <c>rq_session_start</c>, <c>rq_session_get_result</c> before Completed, or <c>rq_session_destroy</c> from inside
    /// a callback. Thrown as <see cref="InvalidOperationException"/>.
    /// </summary>
    InvalidState = -3,

    /// <summary>
    /// <c>RQ_E_INSUFFICIENT_MEMORY</c>: not enough memory could be locked, even after shrinking the region to
    /// <c>min_region_bytes</c>.
    /// </summary>
    /// <remarks>
    /// No v1 function returns it. The condition normally arrives through <c>on_completed</c> as an end reason,
    /// <see cref="RamQuick.Core.RunEndReason.InsufficientMemory"/>, with <c>os_error</c> and <c>end_detail</c> in the
    /// end detail. Were a function ever to return it, it is thrown as <see cref="EngineException"/>.
    /// </remarks>
    InsufficientMemory = -4,

    /// <summary>
    /// <c>RQ_E_LOCK_FAILED</c>: locking failed at one size during the engine's shrink-and-retry, where each retry asks
    /// for 90 % of the previous size.
    /// </summary>
    /// <remarks>
    /// No v1 function returns it. Each failed lock attempt is logged per retry on the native side, and the final
    /// outcome is reported as <see cref="InsufficientMemory"/>. Were a function ever to return it, it is thrown as
    /// <see cref="EngineException"/>.
    /// </remarks>
    LockFailed = -5,

    /// <summary>
    /// <c>RQ_E_THREAD_CREATE</c>: <c>rq_session_start</c> could not create the coordinator thread. Thrown as
    /// <see cref="EngineException"/>, which <c>RunController</c> reports as
    /// <see cref="RamQuick.Core.RunEndReason.EngineFault"/>.
    /// </summary>
    ThreadCreate = -6,

    /// <summary>
    /// <c>RQ_E_VERSION_MISMATCH</c>: the engine's <c>rq_api_version</c> differs from the
    /// <see cref="NativeConstants.ApiVersion"/> this wrapper was built against. The engine never returns it; the
    /// wrapper's bind check raises it as <see cref="EngineException"/>, and the application reports a corrupted
    /// installation.
    /// </summary>
    VersionMismatch = -7,

    /// <summary>
    /// <c>RQ_E_AFFINITY_FAILED</c>: a worker thread could not be pinned to its logical processor.
    /// </summary>
    /// <remarks>
    /// No v1 function returns it. The condition normally arrives through <c>on_completed</c> as an end reason,
    /// <see cref="RamQuick.Core.RunEndReason.AffinityFailed"/>, whose end detail reads "Windows error n: " followed by
    /// the native <c>end_detail</c>, where n is <c>os_error</c>. Were a function ever to return it, it is thrown as
    /// <see cref="EngineException"/>.
    /// </remarks>
    AffinityFailed = -8,

    /// <summary>
    /// <c>RQ_E_INTERNAL</c>: a C++ exception was caught at the engine boundary. Returned by a function, it is thrown as
    /// <see cref="EngineException"/> carrying the <c>rq_result_message</c> text; reported as the end reason
    /// <c>internal_fault</c>, it surfaces as <see cref="RamQuick.Core.RunEndReason.EngineFault"/>.
    /// </summary>
    Internal = -99,
}

/// <summary>
/// Maps <c>rq_result</c> codes returned by engine functions to managed exceptions, with the engine's own text from
/// <c>rq_result_message</c>.
/// </summary>
/// <remarks>
/// End reasons delivered through <c>on_completed</c> are not handled here: they are typed results, not exceptions,
/// and this class never maps a code to a run end reason.
/// </remarks>
internal static class EngineResults
{
    // Every handle the engine validates is a session handle, owned on the managed side by EngineSession.
    private const string SessionObjectName = "EngineSession";

    // The text rq_result_message gives for a code it does not list; reused when the engine's text cannot be read.
    private const string UnknownCodeText = "Unknown result code";

    // Name shown in place of a member name for a value EngineResultCode does not define.
    private const string UnknownCodeName = "Unknown";

    /// <summary>
    /// Returns when <paramref name="result"/> is <c>RQ_OK</c>; otherwise throws the exception
    /// <see cref="CreateException(int, string)"/> maps it to.
    /// </summary>
    /// <param name="result">The <c>rq_result</c> an engine function returned.</param>
    /// <param name="operation">
    /// The operation that returned <paramref name="result"/>, normally the engine function's name, such as
    /// <c>rq_session_start</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="result"/> is <c>RQ_E_INVALID_ARGUMENT</c>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="result"/> is <c>RQ_E_INVALID_HANDLE</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> is <c>RQ_E_INVALID_STATE</c>.</exception>
    /// <exception cref="EngineException"><paramref name="result"/> is any other failure code, known or not.</exception>
    internal static void ThrowIfFailed(int result, string operation)
    {
        if (result == (int)EngineResultCode.Ok)
        {
            return;
        }

        throw CreateException(result, operation);
    }

    /// <summary>
    /// Creates, without throwing it, the managed exception for a failure code returned by an engine function.
    /// </summary>
    /// <param name="result">The failure code; any nonzero value is accepted.</param>
    /// <param name="operation">
    /// The operation that returned <paramref name="result"/>, normally the engine function's name, such as
    /// <c>rq_session_start</c>.
    /// </param>
    /// <returns>
    /// <para>
    /// For <c>RQ_E_INVALID_ARGUMENT</c>, an <see cref="ArgumentException"/>; for <c>RQ_E_INVALID_HANDLE</c>, an
    /// <see cref="ObjectDisposedException"/> naming <c>EngineSession</c>; for <c>RQ_E_INVALID_STATE</c>, an
    /// <see cref="InvalidOperationException"/>. Their message reads <c>"&lt;operation&gt;: &lt;native message&gt;"</c>.
    /// </para>
    /// <para>
    /// For every other value, including codes this enum does not define, an <see cref="EngineException"/> whose
    /// <see cref="EngineException.Code"/> keeps the raw value, whose <see cref="EngineException.NativeMessage"/> is the
    /// engine's text, and whose message reads <c>"&lt;operation&gt; failed (&lt;name&gt;, &lt;code&gt;): &lt;native
    /// message&gt;"</c>, with <c>Unknown</c> as the name of an undefined code.
    /// </para>
    /// <para>
    /// The native message comes from <c>rq_result_message</c>. If it cannot be read, for example because the engine
    /// library cannot be loaded, the member name stands in for it (or "Unknown result code" for an undefined code),
    /// and <see cref="EngineException.NativeMessage"/> is <see langword="null"/>.
    /// </para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="result"/> is <c>RQ_OK</c>, which is not a failure.
    /// </exception>
    internal static Exception CreateException(int result, string operation)
    {
        ArgumentOutOfRangeException.ThrowIfZero(result);

        var code = (EngineResultCode)result;
        string? nativeMessage = TryGetNativeMessage(result);
        string text = nativeMessage ?? FallbackText(code);

        return code switch
        {
            EngineResultCode.InvalidArgument => new ArgumentException($"{operation}: {text}"),
            EngineResultCode.InvalidHandle => new ObjectDisposedException(SessionObjectName, $"{operation}: {text}"),
            EngineResultCode.InvalidState => new InvalidOperationException($"{operation}: {text}"),
            _ => new EngineException(
                code,
                $"{operation} failed ({DisplayName(code)}, {result.ToString(CultureInfo.InvariantCulture)}): {text}",
                nativeMessage),
        };
    }

    /// <summary>
    /// Returns the engine's English description of <paramref name="code"/> from <c>rq_result_message</c>, for
    /// diagnostics and tests.
    /// </summary>
    /// <param name="code">Any result code, including a value this enum does not define.</param>
    /// <returns>
    /// The engine's text, which is "Unknown result code" for a value the engine does not list. If the text cannot be
    /// read, the member name, or "Unknown result code" for an undefined value.
    /// </returns>
    internal static string Describe(EngineResultCode code) => TryGetNativeMessage((int)code) ?? FallbackText(code);

    private static string? TryGetNativeMessage(int result)
    {
        try
        {
            return NativeMethods.GetResultMessage(result);
        }
        catch (Exception)
        {
            // The text only describes a failure that is already being reported. Whatever prevents reading it (an
            // engine library that cannot be loaded, a missing export, a failed resolver registration surfacing as a
            // TypeInitializationException) must not replace that failure, so the caller falls back to the code's name.
            return null;
        }
    }

    private static string FallbackText(EngineResultCode code) =>
        Enum.IsDefined(code) ? code.ToString() : UnknownCodeText;

    private static string DisplayName(EngineResultCode code) =>
        Enum.IsDefined(code) ? code.ToString() : UnknownCodeName;
}
