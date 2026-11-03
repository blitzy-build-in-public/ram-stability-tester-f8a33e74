namespace RamQuick.Sensors;

/// <summary>
/// A source of the installed memory configuration shown in the detected-memory summary: the seam
/// between the run controller and the firmware tables, implemented by
/// <c>SmbiosMemoryInventorySource</c> over SMBIOS Type 17 and substituted by a scripted fake in
/// tests.
/// </summary>
/// <remarks>
/// <para>
/// Scope: a source reports only installed capacity, populated DIMM count, configured speed,
/// channel count and memory type. It never reads or writes memory timings, voltages or any other
/// BIOS setting.
/// </para>
/// <para>
/// Privileges: a source uses only data the operating system already exposes to an unprivileged
/// process. It needs neither a driver nor elevation, so the summary is complete in a non-elevated
/// window as well.
/// </para>
/// <para>
/// A source holds no resources between calls, so it is not <see cref="IDisposable"/>.
/// </para>
/// </remarks>
public interface IMemoryInventorySource
{
    /// <summary>
    /// Reads the detected memory configuration: installed capacity, populated DIMM count,
    /// configured speed, channel count and memory type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threading: the call is synchronous and may take a few milliseconds, because it performs a
    /// firmware-table query. Callers invoke it off the UI thread; <c>RunController.InitializeAsync</c>
    /// runs it on the thread pool.
    /// </para>
    /// <para>
    /// Errors: missing, truncated or malformed firmware data never causes an exception. When nothing
    /// can be read, the result is <see cref="MemoryInventory.Unknown"/>; when only some fields can be
    /// determined, the result is a partially populated inventory whose undetermined fields are
    /// <see langword="null"/>, which the detected-memory summary shows as unknown.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The detected memory configuration, <see cref="MemoryInventory.Unknown"/> when it cannot be
    /// determined, or a partially populated inventory with <see langword="null"/> fields.
    /// </returns>
    MemoryInventory Read();
}
