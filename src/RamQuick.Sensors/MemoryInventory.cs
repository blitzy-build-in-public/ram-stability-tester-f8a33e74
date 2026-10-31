using RamQuick.Core;

namespace RamQuick.Sensors;

/// <summary>
/// The installed memory as the SMBIOS Type 17 (Memory Device) structures describe it: the
/// capacity, configured speed, channel count, DIMM count and memory type shown in the
/// detected-memory summary.
/// </summary>
/// <remarks>
/// <para>
/// Built by <c>SmbiosParser</c> from the firmware table that
/// <c>GetSystemFirmwareTable('RSMB')</c> returns, captured at Start into the progress journal's
/// <c>begin</c> record, and written as the summary's <c>memory.installedBytes</c>,
/// <c>memory.dimms</c>, <c>memory.speedMTs</c>, <c>memory.channels</c> and <c>memory.type</c>.
/// </para>
/// <para>
/// The inventory holds only capacity, configured speed, type and locator-derived channels.
/// Timings, voltages and module identity (manufacturer, part and serial numbers) are out of scope
/// for the summary and have no member here.
/// </para>
/// <para>
/// Every property rejects an impossible value both at construction and through its
/// <see langword="init"/> accessor, so a <see langword="with"/> expression cannot produce an
/// invalid inventory. Value equality compares every property.
/// </para>
/// </remarks>
/// <param name="InstalledBytes">The value of <see cref="InstalledBytes"/>.</param>
/// <param name="DimmCount">The value of <see cref="DimmCount"/>.</param>
/// <param name="SpeedMTs">The value of <see cref="SpeedMTs"/>.</param>
/// <param name="ChannelCount">The value of <see cref="ChannelCount"/>.</param>
/// <param name="MemoryType">The value of <see cref="MemoryType"/>.</param>
/// <exception cref="ArgumentOutOfRangeException">
/// <paramref name="InstalledBytes"/> is 0, <paramref name="DimmCount"/> is negative,
/// <paramref name="SpeedMTs"/> or <paramref name="ChannelCount"/> is 0 or negative, or
/// <paramref name="MemoryType"/> is not a defined <see cref="Core.MemoryType"/> value.
/// </exception>
public sealed record MemoryInventory(ulong? InstalledBytes, int DimmCount, int? SpeedMTs, int? ChannelCount, MemoryType MemoryType)
{
    private readonly ulong? _installedBytes = ValidateInstalledBytes(InstalledBytes);
    private readonly int _dimmCount = ValidateDimmCount(DimmCount);
    private readonly int? _speedMTs = ValidatePositive(SpeedMTs, nameof(SpeedMTs));
    private readonly int? _channelCount = ValidatePositive(ChannelCount, nameof(ChannelCount));
    private readonly MemoryType _memoryType = ValidateMemoryType(MemoryType);

    /// <summary>
    /// The inventory returned when the SMBIOS firmware table cannot be read or parsed: no DIMMs,
    /// every nullable field <see langword="null"/>, and <see cref="Core.MemoryType.Unknown"/>.
    /// The detected-memory summary then shows speed and channels as "Unknown".
    /// </summary>
    public static MemoryInventory Unknown { get; } = new(null, 0, null, null, MemoryType.Unknown);

    /// <summary>
    /// The installed capacity in bytes: the sum of the sizes of the populated DIMMs in SMBIOS
    /// Type 17. <see langword="null"/> when no populated DIMM was found or the size of any
    /// populated DIMM is unknown; never 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value assigned is 0.</exception>
    public ulong? InstalledBytes
    {
        get => _installedBytes;
        init => _installedBytes = ValidateInstalledBytes(value);
    }

    /// <summary>
    /// The number of populated memory devices: Type 17 structures with a non-zero size. 0 when the
    /// firmware table is unavailable or lists no populated device.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value assigned is negative.</exception>
    public int DimmCount
    {
        get => _dimmCount;
        init => _dimmCount = ValidateDimmCount(value);
    }

    /// <summary>
    /// The configured memory speed in MT/s, taken as the lowest known value across the populated
    /// DIMMs, because the modules run at a common speed that the slowest bounds.
    /// <see langword="null"/> when no populated DIMM reports a configured speed. This is the speed
    /// the memory runs at, not the module's rated maximum.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value assigned is 0 or negative.</exception>
    public int? SpeedMTs
    {
        get => _speedMTs;
        init => _speedMTs = ValidatePositive(value, nameof(SpeedMTs));
    }

    /// <summary>
    /// The number of distinct memory channels populated, derived from the Device Locator and Bank
    /// Locator strings. <see langword="null"/> when the locator of any populated DIMM cannot be
    /// parsed. The two 32-bit sub-channels of a DDR5 module are not counted separately.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value assigned is 0 or negative.</exception>
    public int? ChannelCount
    {
        get => _channelCount;
        init => _channelCount = ValidatePositive(value, nameof(ChannelCount));
    }

    /// <summary>
    /// The memory type: <see cref="Core.MemoryType.DDR4"/> or <see cref="Core.MemoryType.DDR5"/>
    /// when every populated DIMM agrees on it; <see cref="Core.MemoryType.Other"/> for any other
    /// known type or for modules of mixed known types; <see cref="Core.MemoryType.Unknown"/> when
    /// nothing is known.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value assigned is not a defined <see cref="Core.MemoryType"/> value.
    /// </exception>
    public MemoryType MemoryType
    {
        get => _memoryType;
        init => _memoryType = ValidateMemoryType(value);
    }

    private static ulong? ValidateInstalledBytes(ulong? value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InstalledBytes), value, "An installed capacity must be null (unknown) or greater than 0.");
        }

        return value;
    }

    private static int ValidateDimmCount(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DimmCount), value, "A DIMM count cannot be negative.");
        }

        return value;
    }

    private static int? ValidatePositive(int? value, string propertyName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(propertyName, value, "The value must be null (unknown) or greater than 0.");
        }

        return value;
    }

    private static MemoryType ValidateMemoryType(MemoryType value)
    {
        // An undefined value would be written verbatim as the summary's memory.type, whose member
        // names are a serialization contract.
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryType), value, "The memory type must be a defined MemoryType value.");
        }

        return value;
    }
}
