using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace RamQuick.App.Services;

/// <summary>
/// The production <see cref="IElevationService"/>: reads elevation from the current process token,
/// and restarts the same executable elevated through the shell's <c>runas</c> verb, which shows the
/// UAC consent prompt.
/// </summary>
/// <remarks>
/// <para>
/// The application manifest declares <c>asInvoker</c>, so a non-elevated launch starts and is
/// reported here as not elevated. With <c>requireAdministrator</c> the check would always report
/// elevation and the restart path could never be reached.
/// </para>
/// <para>
/// Elevation is gained only through the user's consent at the UAC prompt. This class only reads
/// the process token and starts a process: it never assigns or enables a privilege, installs a
/// driver, writes the registry or changes a system setting. It shows no text and no dialog of its
/// own; the run controller asks the user through its prompt service before calling
/// <see cref="TryRestartElevated"/>.
/// </para>
/// <para>
/// Instances are thread-safe.
/// </para>
/// </remarks>
public sealed class ElevationService : IElevationService
{
    // ERROR_CANCELLED: ShellExecuteEx reports it when the user declines the UAC prompt.
    private const int ErrorCancelled = 1223;

    // A token's elevation cannot change while the process runs, so the role check runs once, on
    // first use, and every later read returns the same answer.
    private readonly Lazy<bool> _isElevated = new(EvaluateIsElevated, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets a value indicating whether the current process token is a member of the built-in
    /// Administrators role with elevation in effect.
    /// </summary>
    /// <value>
    /// <see langword="true"/> for a UAC-elevated process; <see langword="false"/> otherwise. An
    /// administrator account running with its UAC-filtered token is not in the Administrators role,
    /// so it reads <see langword="false"/>, which is the not-elevated signal the application acts on.
    /// <see langword="false"/> is also returned when Windows refuses to open or query the token.
    /// </value>
    /// <remarks>
    /// Evaluated once, on first read, and cached for the life of the instance.
    /// </remarks>
    public bool IsElevated => _isElevated.Value;

    /// <summary>
    /// Starts this executable again, without arguments, through the shell's <c>runas</c> verb, so
    /// Windows shows the UAC consent prompt.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only when the elevated process was started; the caller then shuts the
    /// current instance down, releasing the single-instance mutex the new instance waits for.
    /// <see langword="false"/> when the user declined the UAC prompt (Win32 error 1223,
    /// <c>ERROR_CANCELLED</c>), when the executable path is unknown, or when the shell could not
    /// start the process; the current instance keeps running, non-elevated.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method never throws, and the shell shows no error dialog of its own on failure.
    /// </para>
    /// <para>
    /// Starting the process is the only effect: no privilege is assigned and no registry or system
    /// setting is changed. The new instance starts fresh and recovers any unfinished run's journal
    /// itself at startup.
    /// </para>
    /// </remarks>
    public bool TryRestartElevated()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        ProcessStartInfo startInfo = new(path)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            // The user declined the UAC prompt: this instance stays open, non-elevated.
            return false;
        }
        catch (Win32Exception)
        {
            // The shell could not start the executable, for example because it was moved or
            // deleted after this instance started.
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            // Process.Start rejected the request before reaching the shell, or the system has no
            // shell to run the verb. Either way no elevated instance exists.
            return false;
        }
    }

    private static bool EvaluateIsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (SecurityException)
        {
            // Windows refused to open the process token or to check its membership. Reporting no
            // elevation leads only to the restart offer, never to a test without the rights it needs.
            return false;
        }
    }
}
