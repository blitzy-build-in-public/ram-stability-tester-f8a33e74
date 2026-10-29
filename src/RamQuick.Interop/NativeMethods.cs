using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RamQuick.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for the eight production exports of <c>ramquick_engine.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every export is <c>__cdecl</c> (<c>RQ_CALL</c>), exported under its undecorated name, and <c>noexcept</c>:
/// result-returning functions convert any C++ exception to <c>RQ_E_INTERNAL</c>, so no native exception ever
/// reaches these calls. Structured (SEH) faults in the engine are deliberately not translated and terminate the
/// process. <c>SetLastError</c> is never requested, because the engine reports Windows errors through
/// <c>rq_run_result.os_error</c>.
/// </para>
/// <para>
/// Results are the raw <c>rq_result</c> codes (<c>RQ_OK</c> or a negative <c>RQ_E_*</c> value); mapping them to
/// managed exceptions is the callers' job. Struct pointers address the blittable mirrors in <c>NativeStructs.cs</c>,
/// which the runtime passes straight through. A <see cref="SafeSessionHandle"/> argument is reference-counted for
/// the duration of the call, so a concurrent <see cref="SafeHandle.Dispose()"/> cannot destroy the session under a
/// running call; a handle already closed throws <see cref="ObjectDisposedException"/> before the native call.
/// </para>
/// <para>
/// Which file <see cref="LibraryName"/> loads, and from where, is decided solely by the resolver that
/// <see cref="EngineLibrary"/> registers for this assembly, so the declarations carry no search-path attribute.
/// The <c>rq_test_*</c> exports of <c>ramquick_engine_test.dll</c> are declared only by the integration tests,
/// never here.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// The import name of every engine function. It equals <see cref="EngineLibrary.DefaultFileName"/>, the name the
    /// resolver in <see cref="EngineLibrary"/> maps to the bound library file.
    /// </summary>
    internal const string LibraryName = EngineLibrary.DefaultFileName;

    // Text rq_result_message documents for any code it does not list; also used if the engine ever returns null.
    private const string UnknownResultMessage = "Unknown result code";

    // An explicit static constructor makes the type not beforefieldinit, so the runtime runs it before the first
    // static method of this class executes, and therefore before the first engine import resolves. That registers
    // the per-process resolver in time and closes EngineLibrary.Bind from then on.
    static NativeMethods()
    {
        EngineLibrary.EnsureResolverRegistered();
    }

    /// <summary>
    /// <c>rq_api_version</c>: returns the engine's <c>RQ_API_VERSION</c>. The wrapper compares it with
    /// <see cref="NativeConstants.ApiVersion"/> before any other engine call and refuses to bind on a mismatch.
    /// </summary>
    /// <returns>The engine's API version.</returns>
    /// <remarks>Constant, with no failure path; allocates nothing; callable from any thread.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "rq_api_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial uint RqApiVersion();

    /// <summary>
    /// <c>rq_query_system</c>: fills a caller-owned <see cref="RqSystemInfo"/> with the processor topology,
    /// instruction sets, physical memory, large-page granularity and whether <c>SeLockMemoryPrivilege</c> is held.
    /// </summary>
    /// <param name="info">
    /// Caller-owned and non-null, with <see cref="RqSystemInfo.StructSize"/> set to <see cref="RqSystemInfo.Size"/>.
    /// </param>
    /// <returns>
    /// <c>RQ_OK</c>; <c>RQ_E_INVALID_ARGUMENT</c> for a null pointer or a wrong struct size; <c>RQ_E_INTERNAL</c>.
    /// </returns>
    /// <remarks>
    /// No side effects: the privilege is observed, never enabled. Needs no session; callable from any thread.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "rq_query_system")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqQuerySystem(RqSystemInfo* info);

    /// <summary>
    /// <c>rq_session_create</c>: validates the configuration, its step array and the callback set, deep-copies them
    /// and creates an idle session. Allocates nothing large and starts no thread.
    /// </summary>
    /// <param name="config">
    /// The session configuration, non-null, whose <see cref="RqSessionConfig.Steps"/> points to its step array. The
    /// engine copies it, its steps and their names, so the caller may free them on return.
    /// </param>
    /// <param name="callbacks">
    /// The callback set, non-null: <see cref="RqCallbacks.StructSize"/> set and seven non-null function pointers,
    /// with <see cref="RqCallbacks.UserData"/> passed back unchanged to every callback. The engine copies the struct.
    /// </param>
    /// <param name="session">
    /// Receives the raw session handle, set to null on failure. The caller attaches a non-null handle at once to a
    /// <see cref="SafeSessionHandle"/> built before this call, so its release destroys the session on every path.
    /// </param>
    /// <returns><c>RQ_OK</c>; <c>RQ_E_INVALID_ARGUMENT</c>; <c>RQ_E_INTERNAL</c>.</returns>
    [LibraryImport(LibraryName, EntryPoint = "rq_session_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqSessionCreate(RqSessionConfig* config, RqCallbacks* callbacks, nint* session);

    /// <summary>
    /// <c>rq_session_start</c>: starts the session's coordinator thread and returns immediately; preparation, the
    /// steps and every callback follow on the coordinator. Allowed once per session.
    /// </summary>
    /// <param name="session">The session; reference-counted for the duration of the call.</param>
    /// <returns>
    /// <c>RQ_OK</c>; <c>RQ_E_INVALID_HANDLE</c>; <c>RQ_E_INVALID_STATE</c> when already started;
    /// <c>RQ_E_THREAD_CREATE</c> when the coordinator cannot be created; <c>RQ_E_INTERNAL</c>.
    /// </returns>
    /// <remarks>
    /// When a stop was recorded before start, the coordinator raises <c>on_completed</c>, with the end reason mapped
    /// from the first recorded stop reason, and then <c>on_released</c> straight away, with no <c>on_prepared</c>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException"><paramref name="session"/> is already closed.</exception>
    [LibraryImport(LibraryName, EntryPoint = "rq_session_start")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqSessionStart(SafeSessionHandle session);

    /// <summary>
    /// <c>rq_session_stop</c>: requests a stop. Idempotent and non-blocking; callable from any thread, including from
    /// inside a callback, before start and after completion.
    /// </summary>
    /// <param name="session">The session; reference-counted for the duration of the call.</param>
    /// <param name="stopReason">
    /// <see cref="NativeConstants.StopUser"/>, <see cref="NativeConstants.StopThermal"/> or
    /// <see cref="NativeConstants.StopHostFault"/>.
    /// </param>
    /// <returns>
    /// <c>RQ_OK</c>; <c>RQ_E_INVALID_HANDLE</c>; <c>RQ_E_INVALID_ARGUMENT</c> for any other stop reason;
    /// <c>RQ_E_INTERNAL</c>.
    /// </returns>
    /// <remarks>
    /// The reason is recorded before the call returns, and the first reason recorded, including the engine's own
    /// first-error and fault reasons, wins and becomes <c>rq_run_result.end_reason</c>. The wrapper therefore sets
    /// the managed stop event that releases a waiting step gate only after this call has returned. Workers observe
    /// the stop after their current 1 MiB block, holds wake at once, and preparation checks it before every chunk.
    /// </remarks>
    /// <exception cref="ObjectDisposedException"><paramref name="session"/> is already closed.</exception>
    [LibraryImport(LibraryName, EntryPoint = "rq_session_stop")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqSessionStop(SafeSessionHandle session, uint stopReason);

    /// <summary>
    /// <c>rq_session_get_result</c>: copies the run result header into a caller-owned <see cref="RqRunResult"/>.
    /// Valid once <c>on_completed</c> has been raised.
    /// </summary>
    /// <param name="session">The session; reference-counted for the duration of the call.</param>
    /// <param name="result">
    /// Caller-owned and non-null, with <see cref="RqRunResult.StructSize"/> set to <see cref="RqRunResult.Size"/>.
    /// On success its <see cref="RqRunResult.StepResults"/> points to a session-owned array of
    /// <see cref="RqRunResult.StepResultCount"/> entries that stays valid until <c>rq_session_destroy</c>.
    /// </param>
    /// <returns>
    /// <c>RQ_OK</c>; <c>RQ_E_INVALID_HANDLE</c>; <c>RQ_E_INVALID_ARGUMENT</c> for a null pointer or a wrong struct
    /// size; <c>RQ_E_INVALID_STATE</c> before Completed; <c>RQ_E_INTERNAL</c>.
    /// </returns>
    /// <exception cref="ObjectDisposedException"><paramref name="session"/> is already closed.</exception>
    [LibraryImport(LibraryName, EntryPoint = "rq_session_get_result")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqSessionGetResult(SafeSessionHandle session, RqRunResult* result);

    /// <summary>
    /// <c>rq_session_destroy</c>: stops the session if it is running, waits for in-flight callbacks, joins every
    /// thread, releases the locked memory (also when the coordinator never ran) and frees the session. No callback
    /// runs after it returns, and the handle is invalid from then on.
    /// </summary>
    /// <param name="session">
    /// The raw session handle. It is a raw value, not a <see cref="SafeSessionHandle"/>, because the only caller is
    /// <see cref="SafeSessionHandle"/>'s <c>ReleaseHandle</c>, where the handle can no longer be reference-counted.
    /// That caller guarantees no other call on the same handle races this one.
    /// </param>
    /// <returns>
    /// <c>RQ_OK</c>; <c>RQ_E_INVALID_HANDLE</c> for a null or unknown handle; <c>RQ_E_INVALID_STATE</c> when called
    /// from inside a callback, with the session still alive; <c>RQ_E_INTERNAL</c>.
    /// </returns>
    [LibraryImport(LibraryName, EntryPoint = "rq_session_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int RqSessionDestroy(nint session);

    /// <summary>
    /// <c>rq_result_message</c>: returns a static, NUL-terminated English UTF-8 description of a result code, or
    /// "Unknown result code" for a value the engine does not list. Use <see cref="GetResultMessage(int)"/>.
    /// </summary>
    /// <param name="result">An <c>rq_result</c> code.</param>
    /// <returns>A pointer to engine-owned static text that must never be freed.</returns>
    /// <remarks>Constant, with no failure path; allocates nothing; callable from any thread.</remarks>
    // The return is a raw pointer on purpose: a string return with StringMarshalling.Utf8 would make the generated
    // marshaller free the returned buffer, which is static native memory.
    [LibraryImport(LibraryName, EntryPoint = "rq_result_message")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial byte* RqResultMessage(int result);

    /// <summary>
    /// Returns the engine's English description of a result code, decoded from the static text of
    /// <c>rq_result_message</c>.
    /// </summary>
    /// <param name="result">An <c>rq_result</c> code; any value is accepted.</param>
    /// <returns>The engine's text for <paramref name="result"/>, or "Unknown result code".</returns>
    internal static string GetResultMessage(int result) =>
        Marshal.PtrToStringUTF8((nint)RqResultMessage(result)) ?? UnknownResultMessage;
}
