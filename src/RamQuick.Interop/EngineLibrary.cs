using System.Reflection;
using System.Runtime.InteropServices;

namespace RamQuick.Interop;

/// <summary>
/// Chooses, once per process, which native engine library the engine imports of this assembly load.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="NativeLibrary.SetDllImportResolver(Assembly, DllImportResolver)"/> is registered for the
/// <c>RamQuick.Interop</c> assembly. It maps the engine import name, <see cref="DefaultFileName"/>, to
/// <see cref="BoundFileName"/>. Every other import of the assembly, such as <c>user32.dll</c>, keeps the
/// runtime's default probing.
/// </para>
/// <para>Bindings per process:</para>
/// <list type="bullet">
/// <item><description>
/// The application and <c>RamQuick.TestHost</c> never call <see cref="Bind(string)"/>, so they load the
/// production <c>ramquick_engine.dll</c>.
/// </description></item>
/// <item><description>
/// <c>RamQuick.IntegrationTests</c> calls <c>Bind("ramquick_engine_test.dll")</c> once, from its assembly-level
/// <c>EngineBindingFixture</c>, before any test runs. The test DLL exports the production API, compiled from the
/// same <c>api.cpp</c>, plus the <c>rq_test_*</c> exports.
/// </description></item>
/// </list>
/// <para>
/// The runtime never rebinds a P/Invoke that has resolved, so <see cref="Bind(string)"/> is accepted only before
/// the first engine call and at most once per process. This type is thread-safe.
/// </para>
/// </remarks>
public static class EngineLibrary
{
    /// <summary>
    /// The engine import name, and the library the engine imports load when <see cref="Bind(string)"/> is never
    /// called.
    /// </summary>
    public const string DefaultFileName = "ramquick_engine.dll";

    private static readonly object _gate = new();

    private static string _boundFileName = DefaultFileName;
    private static bool _bound;
    private static bool _resolverRegistered;
    private static bool _engineImportsArmed;
    private static bool _engineResolved;

    /// <summary>
    /// Gets the library file the engine imports load: <see cref="DefaultFileName"/> until <see cref="Bind(string)"/>
    /// succeeds, then the file name it was given.
    /// </summary>
    public static string BoundFileName
    {
        get
        {
            lock (_gate)
            {
                return _boundFileName;
            }
        }
    }

    /// <summary>
    /// Binds every engine import of this process to <paramref name="fileName"/> instead of
    /// <see cref="DefaultFileName"/>.
    /// </summary>
    /// <param name="fileName">
    /// A file name or relative path, which is resolved against the directory that holds the
    /// <c>RamQuick.Interop</c> assembly and never against the current directory; or a fully qualified path.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fileName"/> is empty or white space, or is rooted without being fully qualified, such as
    /// <c>C:engine.dll</c> or <c>\engine.dll</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The engine library is already bound, or the engine imports of this process may already have resolved.
    /// </exception>
    public static void Bind(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) && !Path.IsPathFullyQualified(fileName))
        {
            throw new ArgumentException(
                "The engine library must be named by a file name, a relative path or a fully qualified path.",
                nameof(fileName));
        }

        lock (_gate)
        {
            if (_bound)
            {
                throw new InvalidOperationException($"The engine library is already bound to '{_boundFileName}'.");
            }

            if (_engineImportsArmed || _engineResolved)
            {
                throw new InvalidOperationException(
                    "An engine import has already resolved; the engine library can no longer be rebound.");
            }

            RegisterResolverLocked();
            _boundFileName = fileName;
            _bound = true;
        }
    }

    /// <summary>
    /// Registers the resolver unless it is registered already, and closes binding: from this call on an engine
    /// import may resolve, so <see cref="Bind(string)"/> throws.
    /// </summary>
    /// <remarks>
    /// Called by the explicit static constructor of <c>NativeMethods</c>, which runs before its first engine
    /// import resolves. Idempotent.
    /// </remarks>
    internal static void EnsureResolverRegistered()
    {
        lock (_gate)
        {
            // Closed first, so a failed registration cannot leave binding open after the engine type was touched.
            _engineImportsArmed = true;
            RegisterResolverLocked();
        }
    }

    private static void RegisterResolverLocked()
    {
        if (_resolverRegistered)
        {
            return;
        }

        // The runtime accepts one resolver per assembly and throws on a second registration.
        NativeLibrary.SetDllImportResolver(typeof(EngineLibrary).Assembly, Resolve);
        _resolverRegistered = true;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // NativeMethods.LibraryName is defined to equal DefaultFileName. Keying on this constant keeps the class
        // free of a dependency on NativeMethods, whose static constructor depends on this class.
        if (!string.Equals(libraryName, DefaultFileName, StringComparison.OrdinalIgnoreCase))
        {
            // Zero hands every other import back to the runtime's default probing.
            return 0;
        }

        string fileName;
        lock (_gate)
        {
            _engineResolved = true;
            fileName = _boundFileName;
        }

        if (Path.IsPathFullyQualified(fileName))
        {
            return NativeLibrary.Load(fileName);
        }

        // The import's own search path is replaced on purpose. AssemblyDirectory probes beside RamQuick.Interop.dll,
        // and System32 confines the runtime's fallback LoadLibraryEx to the system directory, so neither the current
        // directory nor PATH is ever searched for the engine. A DllNotFoundException propagates to the caller.
        return NativeLibrary.Load(
            fileName,
            assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32);
    }
}
