namespace RamQuick.App.Services;

/// <summary>
/// Detects whether the process runs elevated, and restarts it elevated through UAC consent.
/// </summary>
/// <remarks>
/// <para>
/// A test needs an elevated process: locking the region under test and reading the temperature
/// sensors both require Administrator rights. The application manifest declares
/// <c>asInvoker</c>, so a non-elevated launch still starts, is detected through
/// <see cref="IsElevated"/>, and is offered a restart as Administrator.
/// </para>
/// <para>
/// Elevation happens only through the user's consent at the UAC prompt. Implementations never
/// assign or enable a privilege, never write the registry and never change a system setting.
/// </para>
/// <para>
/// The production implementation is <c>ElevationService</c>; tests substitute a fake, so no test
/// starts a process or shows a UAC prompt.
/// </para>
/// </remarks>
public interface IElevationService
{
    /// <summary>
    /// Gets a value indicating whether the current process token is a member of the built-in
    /// Administrators role with elevation in effect.
    /// </summary>
    /// <value>
    /// <see langword="true"/> for a UAC-elevated process; <see langword="false"/> otherwise,
    /// including for an administrator account running with its filtered, non-elevated token.
    /// </value>
    /// <remarks>
    /// The value is stable for the lifetime of the process: a token's elevation cannot change
    /// while the process runs, so gaining elevation always means a new process.
    /// </remarks>
    bool IsElevated { get; }

    /// <summary>
    /// Starts the same executable again, elevated, through the UAC consent prompt.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only when the elevated process was started; the caller must then shut
    /// the current instance down (the run controller raises its shutdown request and the
    /// application closes its window). <see langword="false"/> when the user declined the UAC
    /// prompt (Win32 error 1223, <c>ERROR_CANCELLED</c>) or the process could not be started; the
    /// current instance keeps running, non-elevated.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method never throws: every failure to start the process is reported as
    /// <see langword="false"/>.
    /// </para>
    /// <para>
    /// The new instance starts without arguments. It waits up to 5 seconds for the single-instance
    /// mutex (<c>SingleInstanceGuard</c>, <c>Local\RamQuick.SingleInstance</c>), which the current
    /// instance holds until it exits, so it takes over once the current instance has shut down.
    /// </para>
    /// <para>
    /// Starting that process is the only effect: no privilege is assigned and no registry or
    /// system setting is changed.
    /// </para>
    /// </remarks>
    bool TryRestartElevated();
}
