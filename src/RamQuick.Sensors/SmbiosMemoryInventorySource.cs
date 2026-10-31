namespace RamQuick.Sensors;

/// <summary>
/// The production <see cref="IMemoryInventorySource"/>: the detected-memory summary read from the
/// SMBIOS Type 17 (Memory Device) structures that <c>GetSystemFirmwareTable('RSMB')</c> returns.
/// </summary>
/// <remarks>
/// <para>
/// The firmware table query is read-only and needs neither a driver nor elevation, so the summary
/// is complete in a non-elevated window. No registry, WMI or driver access is involved. Decoding is
/// left to <see cref="SmbiosParser"/>, which reports only installed capacity, populated DIMM count,
/// configured speed, channel count and memory type.
/// </para>
/// <para>
/// The source is stateless: every <see cref="Read"/> queries the firmware table again and nothing
/// is cached or held between calls, so any thread may call it, concurrently included.
/// </para>
/// </remarks>
public sealed class SmbiosMemoryInventorySource : IMemoryInventorySource
{
    private readonly Func<byte[]?> _readRawTable;

    /// <summary>
    /// Creates a source that reads the raw SMBIOS table from the Windows firmware table provider.
    /// </summary>
    public SmbiosMemoryInventorySource()
        : this(NativeFirmware.ReadRawSmbiosTable)
    {
    }

    /// <summary>
    /// Creates a source over a substitute table reader, so tests can supply SMBIOS fixtures, a
    /// missing table or a failing read.
    /// </summary>
    /// <param name="readRawTable">
    /// Returns the complete <c>'RSMB'</c> buffer, <c>RawSMBIOSData</c> header included, or
    /// <see langword="null"/> when the table cannot be read.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="readRawTable"/> is <see langword="null"/>.</exception>
    internal SmbiosMemoryInventorySource(Func<byte[]?> readRawTable)
    {
        ArgumentNullException.ThrowIfNull(readRawTable);
        _readRawTable = readRawTable;
    }

    /// <summary>
    /// Reads the raw SMBIOS table and decodes the installed memory configuration from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threading: synchronous, and it may take a few milliseconds for the firmware table query.
    /// <c>RunController.InitializeAsync</c> calls it once at launch on the thread pool, never on the
    /// UI thread. Each call re-reads the table; no result is cached.
    /// </para>
    /// <para>
    /// Errors: a missing or empty table, and any failure of the table read or of decoding, give
    /// <see cref="MemoryInventory.Unknown"/> rather than an exception, so the detected-memory
    /// summary shows the configuration as unknown and the application continues. Only
    /// <see cref="OutOfMemoryException"/> propagates, because the process cannot continue safely
    /// after it.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The detected memory configuration, a partially populated inventory whose undetermined fields
    /// are <see langword="null"/>, or <see cref="MemoryInventory.Unknown"/> when the table cannot be
    /// read or holds no populated memory device.
    /// </returns>
    public MemoryInventory Read()
    {
        try
        {
            byte[]? rawTable = _readRawTable();
            if (rawTable is null || rawTable.Length == 0)
            {
                return MemoryInventory.Unknown;
            }

            return SmbiosParser.Parse(rawTable);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Firmware data is outside the application's control; an unreadable table must not stop
            // the launch, so the summary degrades to unknown values.
            return MemoryInventory.Unknown;
        }
    }
}
