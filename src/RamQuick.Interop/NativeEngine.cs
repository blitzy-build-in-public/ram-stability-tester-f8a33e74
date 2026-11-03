using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Interop;

/// <summary>
/// The production <see cref="IEngine"/>: probes the machine and creates sessions through the flat C API of the engine
/// library that <see cref="EngineLibrary"/> binds, <c>ramquick_engine.dll</c> unless the process rebinds it.
/// </summary>
/// <remarks>
/// <para>
/// Construction is the bind check. The constructor loads the bound library through its first engine call,
/// <c>rq_api_version</c>, and refuses to bind unless the engine reports the API version this wrapper was built against,
/// so a mismatched or unloadable engine surfaces once, at composition, as an <see cref="EngineException"/> with
/// <see cref="EngineResultCode.VersionMismatch"/>, which the application reports as a corrupted installation. A process
/// that rebinds the engine must call <see cref="EngineLibrary.Bind(string)"/> before constructing this type.
/// </para>
/// <para>
/// The instance holds no per-run state and is safe to use from any thread. Each run's state lives in the
/// <see cref="IEngineSession"/> that <see cref="CreateSession"/> returns, which the caller owns and disposes.
/// </para>
/// </remarks>
public sealed unsafe class NativeEngine : IEngine
{
    private readonly NativeSessionFactory _sessionFactory;

    /// <summary>
    /// Binds to the engine library selected by <see cref="EngineLibrary"/> and verifies its API version. Sessions are
    /// created with <c>rq_session_create</c>.
    /// </summary>
    /// <exception cref="EngineException">
    /// The engine library could not be loaded, lacks the <c>rq_api_version</c> export, is not a valid x64 image, or
    /// reports an API version other than the one this wrapper was built against. <see cref="EngineException.Code"/> is
    /// <see cref="EngineResultCode.VersionMismatch"/> in every case.
    /// </exception>
    public NativeEngine()
        : this(DefaultFactory)
    {
    }

    /// <summary>
    /// Binds to the engine library selected by <see cref="EngineLibrary"/>, verifies its API version, and creates
    /// sessions through <paramref name="sessionFactory"/>. The integration tests pass a factory that calls
    /// <c>rq_test_session_create</c> of <c>ramquick_engine_test.dll</c> to build fault-injecting sessions.
    /// </summary>
    /// <param name="sessionFactory">The native creation call each <see cref="CreateSession"/> uses.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionFactory"/> is null.</exception>
    /// <exception cref="EngineException">
    /// The engine library could not be bound or reports another API version
    /// (<see cref="EngineResultCode.VersionMismatch"/>).
    /// </exception>
    internal NativeEngine(NativeSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        VerifyApiVersion();
        _sessionFactory = sessionFactory;
    }

    /// <inheritdoc/>
    public SystemSnapshot QuerySystem()
    {
        RqSystemInfo info = default;
        info.StructSize = RqSystemInfo.Size;
        EngineResults.ThrowIfFailed(NativeMethods.RqQuerySystem(&info), "rq_query_system");

        // InstructionSet shares the native RQ_ISA_* values, so the cast converts by value. isa_best is never
        // RQ_ISA_AUTO (0); any undefined value means the engine broke its contract, not that the machine is unusual.
        var bestIsa = (InstructionSet)info.IsaBest;
        if (!Enum.IsDefined(bestIsa))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"rq_query_system reported an undefined best instruction set ({info.IsaBest})."));
        }

        return new SystemSnapshot
        {
            CpuName = FixedUtf8.Read(info.CpuName, NativeConstants.NameSize).Trim(),
            LogicalProcessors = checked((int)info.LogicalProcessors),
            ProcessorGroups = checked((int)info.ProcessorGroups),
            BestIsa = bestIsa,
            TotalPhysicalBytes = info.TotalPhysBytes,
            AvailablePhysicalBytes = info.AvailPhysBytes,
            LargePageMinimum = info.LargePageMinBytes,
            LockPrivilegeHeld = info.LockPrivilegeHeld != 0,
        };
    }

    /// <inheritdoc/>
    public IEngineSession CreateSession(EngineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return EngineSession.Create(options, _sessionFactory);
    }

    private static int DefaultFactory(RqSessionConfig* config, RqCallbacks* callbacks, nint* session) =>
        NativeMethods.RqSessionCreate(config, callbacks, session);

    private static void VerifyApiVersion()
    {
        uint actual;
        try
        {
            // The first engine call of the process: it runs NativeMethods' static constructor, which registers the
            // EngineLibrary resolver and closes rebinding, and then loads the bound library.
            actual = NativeMethods.RqApiVersion();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // A missing, truncated, foreign-architecture or pre-versioning engine is the same fault to the user as a
            // version mismatch: the installation is not the one this wrapper ships with.
            throw new EngineException(
                EngineResultCode.VersionMismatch,
                $"{EngineLibrary.BoundFileName} could not be loaded ({ex.GetType().Name}). "
                + "The RAMQuick installation is corrupted or incomplete.",
                innerException: ex);
        }

        if (actual != NativeConstants.ApiVersion)
        {
            throw new EngineException(
                EngineResultCode.VersionMismatch,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Engine API version {actual} does not match the expected version {NativeConstants.ApiVersion}. "
                    + $"The RAMQuick installation is corrupted or incomplete."));
        }
    }
}
