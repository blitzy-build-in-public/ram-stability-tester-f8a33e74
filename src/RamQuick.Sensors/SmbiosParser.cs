using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using RamQuick.Core;

namespace RamQuick.Sensors;

/// <summary>
/// Decodes the raw SMBIOS table that <c>GetSystemFirmwareTable('RSMB')</c> returns into the
/// <see cref="MemoryInventory"/> shown in the detected-memory summary.
/// </summary>
/// <remarks>
/// <para>
/// The parser is pure: it performs no I/O and holds no mutable state, so any thread may call it.
/// It never throws for malformed input. Every field is read only when the structure's formatted
/// length covers it, the table walk stops at the first corrupt or truncated structure, and a
/// table without a populated memory device gives <see cref="MemoryInventory.Unknown"/>.
/// </para>
/// <para>
/// Only SMBIOS Type 17 (Memory Device) structures are decoded, at their SMBIOS 3.x offsets:
/// Size (0x0C) with Extended Size (0x1C), Device Locator (0x10), Bank Locator (0x11), Memory Type
/// (0x12), and Configured Memory Speed (0x20) with Extended Configured Memory Speed (0x58).
/// Configured Memory Speed is read as MT/s for every SMBIOS version, as SMBIOS 3.1 and later
/// define it, so the header's version bytes are not consulted. The rated Speed field (0x15),
/// voltages, timings and module identity strings are never read: the summary reports only
/// capacity, configured speed, type and locator-derived channels.
/// </para>
/// <para>
/// Size decoding: 0x0000 is an empty slot, and the device is ignored entirely. 0xFFFF is a
/// populated device of unknown size. 0x7FFF defers to Extended Size (bits 30:0, MiB), which is
/// unknown when it is 0 or beyond the structure. With bit 15 set the value is bits 14:0 in KiB,
/// and 0 KiB is unknown; otherwise it is MiB. A structure too short to hold Size is ignored,
/// because it cannot be told apart from an empty slot. A missing Memory Type reads as 0x02
/// (Unknown). A Configured Memory Speed of 0 is unknown; 0xFFFF defers to Extended Configured
/// Memory Speed (bits 30:0, MT/s), which is unknown when it is 0 or beyond the structure.
/// Locator strings are decoded as Latin-1 and trimmed; a blank string reads as
/// <see langword="null"/>.
/// </para>
/// <para>
/// Aggregation over the populated devices: <see cref="MemoryInventory.DimmCount"/> counts them;
/// <see cref="MemoryInventory.InstalledBytes"/> sums their sizes and is <see langword="null"/>
/// when any size is unknown; <see cref="MemoryInventory.SpeedMTs"/> is the lowest known
/// configured speed; <see cref="MemoryInventory.MemoryType"/> is
/// <see cref="MemoryType.DDR4"/> (code 0x1A) or <see cref="MemoryType.DDR5"/> (code 0x22) when
/// every device with a known code agrees on it, <see cref="MemoryType.Unknown"/> when no device
/// has a known code (codes 0x00, 0x01 Other and 0x02 Unknown are all treated as unknown), and
/// <see cref="MemoryType.Other"/> for any other known type or a mix of known types.
/// </para>
/// <para>
/// <see cref="MemoryInventory.ChannelCount"/> is the number of distinct channel keys, and
/// <see langword="null"/> (shown "Unknown") when any populated device yields no key. Keys come from
/// the Device Locator (D) and Bank Locator (B), matched case-insensitively and culture-invariantly:
/// </para>
/// <list type="number">
/// <item><description>
/// Explicit channel. D, then B, is searched for <c>CHANNEL</c> followed by any spaces, underscores
/// or hyphens and an identifier: one letter not followed by another letter, or a run of digits.
/// The upper-cased identifier is the channel. The key is prefixed with every controller token
/// found, in the order socket, node, controller, each searched in D first and then in B: a whole
/// token <c>P&lt;n&gt;</c> (as in AMD's "P0 CHANNEL A"), <c>NODE&lt;n&gt;</c> and
/// <c>CONTROLLER&lt;n&gt;</c>, where spaces, underscores or hyphens may separate the word from
/// its number and are dropped. The key is the tokens joined by "/", a colon, then the channel.
/// Intel's "Controller0-ChannelA-DIMM0" gives <c>CONTROLLER0:A</c>; "Controller1-ChannelA-DIMM1"
/// gives <c>CONTROLLER1:A</c>; D "DIMM 0" with B "P0 CHANNEL B" gives <c>P0:B</c>;
/// "P0_Node0_Channel1_Dimm0" gives <c>P0/NODE0:1</c>; "ChannelB-DIMM1" gives <c>:B</c>.
/// </description></item>
/// <item><description>
/// Slot letter. Otherwise, when D in full matches an optional <c>DIMM</c>, an optional space,
/// underscore or hyphen, a letter A to H, another optional separator and digits ("DIMM_A1",
/// "DIMM A2", "A1", "B2"), the key is a colon followed by the upper-cased letter, for example
/// <c>:A</c>.
/// </description></item>
/// <item><description>
/// Otherwise the device has no key, as with "DIMM 1" and "BANK 0", or "SLOT1".
/// </description></item>
/// </list>
/// <para>
/// The count is of populated memory channels as the board names them. The two 32-bit
/// sub-channels of a DDR5 module are not counted separately, and boards whose locators name no
/// channel yield <see langword="null"/>.
/// </para>
/// </remarks>
public static partial class SmbiosParser
{
    // RawSMBIOSData: Used20CallingMethod, SMBIOSMajorVersion, SMBIOSMinorVersion, DmiRevision
    // (one byte each), then the little-endian 32-bit table Length at offset 4.
    private const int RawHeaderLength = 8;
    private const int RawHeaderTableLengthOffset = 4;

    // Every structure starts with Type (byte), Length (byte) and Handle (word).
    private const int StructureHeaderLength = 4;

    // A final guard on the table walk, which ends on its own because every structure advances it
    // by at least six bytes. The bound also keeps the size sum below 2^63 (4096 devices of under
    // 2^51 bytes each), so the sum cannot overflow.
    private const int MaxStructures = 4096;

    private const byte MemoryDeviceStructureType = 17;
    private const byte EndOfTableStructureType = 127;

    // Type 17 (Memory Device) field offsets, SMBIOS 3.x.
    private const int SizeOffset = 0x0C;
    private const int DeviceLocatorOffset = 0x10;
    private const int BankLocatorOffset = 0x11;
    private const int MemoryTypeOffset = 0x12;
    private const int ExtendedSizeOffset = 0x1C;
    private const int ConfiguredSpeedOffset = 0x20;
    private const int ExtendedConfiguredSpeedOffset = 0x58;

    private const ushort SizeNotInstalled = 0x0000;
    private const ushort SizeUnknown = 0xFFFF;
    private const ushort SizeInExtendedField = 0x7FFF;
    private const ushort SizeKibGranularityBit = 0x8000;
    private const int SizeKibValueMask = 0x7FFF;
    private const ushort ConfiguredSpeedUnknown = 0x0000;
    private const ushort ConfiguredSpeedInExtendedField = 0xFFFF;

    // Bit 31 of Extended Size and Extended Configured Memory Speed is reserved.
    private const uint ExtendedValueMask = 0x7FFF_FFFF;

    private const ulong BytesPerKiB = 1024;
    private const ulong BytesPerMiB = 1024 * 1024;

    private const byte MemoryTypeCodeUnspecified = 0x00;
    private const byte MemoryTypeCodeOther = 0x01;
    private const byte MemoryTypeCodeUnknown = 0x02;
    private const byte MemoryTypeCodeDdr4 = 0x1A;
    private const byte MemoryTypeCodeDdr5 = 0x22;

    private const RegexOptions LocatorRegexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Builds the memory inventory from a raw SMBIOS table.
    /// </summary>
    /// <param name="rawSmbiosData">
    /// The complete buffer that <c>GetSystemFirmwareTable('RSMB')</c> returns: the 8-byte
    /// <c>RawSMBIOSData</c> header (<c>Used20CallingMethod</c>, <c>SMBIOSMajorVersion</c>,
    /// <c>SMBIOSMinorVersion</c>, <c>DmiRevision</c>, then the little-endian 32-bit table
    /// <c>Length</c>) followed by the SMBIOS structure table.
    /// </param>
    /// <returns>
    /// The inventory aggregated over the populated memory devices by the rules in the remarks of
    /// <see cref="SmbiosParser"/>, or <see cref="MemoryInventory.Unknown"/> when the buffer holds no
    /// populated Type 17 device, including when it is empty, truncated or corrupt. Malformed input
    /// never throws.
    /// </returns>
    public static MemoryInventory Parse(ReadOnlySpan<byte> rawSmbiosData)
    {
        IReadOnlyList<SmbiosMemoryDevice> devices = ParseDevices(rawSmbiosData);
        return devices.Count == 0 ? MemoryInventory.Unknown : Aggregate(devices);
    }

    /// <summary>
    /// Decodes every populated SMBIOS Type 17 memory device in a raw SMBIOS table.
    /// </summary>
    /// <param name="rawSmbiosData">
    /// The same complete <c>'RSMB'</c> buffer that <see cref="Parse"/> takes, header included.
    /// </param>
    /// <returns>
    /// The populated devices in table order. Empty slots (Size 0) and structures too short to hold
    /// Size are excluded. The list is empty when the buffer is shorter than the header or holds no
    /// populated device. Decoding stops at the end-of-table structure (type 127), at the first
    /// structure whose length is below 4 or runs past the table, and after a structure whose string
    /// set has no terminating double NUL; that last structure is still decoded, with every string
    /// <see langword="null"/>. Malformed input never throws.
    /// </returns>
    internal static IReadOnlyList<SmbiosMemoryDevice> ParseDevices(ReadOnlySpan<byte> rawSmbiosData)
    {
        var devices = new List<SmbiosMemoryDevice>();
        if (rawSmbiosData.Length < RawHeaderLength)
        {
            return devices;
        }

        // A declared Length beyond the buffer describes bytes the buffer does not hold, so only the
        // bytes present are walked.
        uint declaredTableLength = BinaryPrimitives.ReadUInt32LittleEndian(rawSmbiosData[RawHeaderTableLengthOffset..]);
        int availableTableLength = rawSmbiosData.Length - RawHeaderLength;
        int tableLength = declaredTableLength < (uint)availableTableLength ? (int)declaredTableLength : availableTableLength;
        ReadOnlySpan<byte> remaining = rawSmbiosData.Slice(RawHeaderLength, tableLength);

        for (int structureCount = 0; structureCount < MaxStructures && remaining.Length >= StructureHeaderLength; structureCount++)
        {
            byte type = remaining[0];
            int formattedLength = remaining[1];
            if (formattedLength < StructureHeaderLength || formattedLength > remaining.Length)
            {
                break;
            }

            // The string set follows the formatted area and ends with a double NUL; an empty set is
            // just the double NUL.
            int terminatorIndex = remaining[formattedLength..].IndexOf(StringSetTerminator);
            bool hasStringSet = terminatorIndex >= 0;
            ReadOnlySpan<byte> stringSet = hasStringSet
                ? remaining.Slice(formattedLength, terminatorIndex)
                : [];

            if (type == MemoryDeviceStructureType && DecodeMemoryDevice(remaining[..formattedLength], stringSet) is { } device)
            {
                devices.Add(device);
            }

            if (!hasStringSet || type == EndOfTableStructureType)
            {
                break;
            }

            remaining = remaining[(formattedLength + terminatorIndex + StringSetTerminator.Length)..];
        }

        return devices;
    }

    private static ReadOnlySpan<byte> StringSetTerminator => "\0\0"u8;

    private static SmbiosMemoryDevice? DecodeMemoryDevice(ReadOnlySpan<byte> formatted, ReadOnlySpan<byte> stringSet)
    {
        if (!Covers(formatted, SizeOffset, sizeof(ushort)))
        {
            return null;
        }

        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(formatted[SizeOffset..]);
        if (size == SizeNotInstalled)
        {
            return null;
        }

        string? deviceLocator = Covers(formatted, DeviceLocatorOffset, sizeof(byte))
            ? ReadString(stringSet, formatted[DeviceLocatorOffset])
            : null;
        string? bankLocator = Covers(formatted, BankLocatorOffset, sizeof(byte))
            ? ReadString(stringSet, formatted[BankLocatorOffset])
            : null;
        byte memoryTypeCode = Covers(formatted, MemoryTypeOffset, sizeof(byte))
            ? formatted[MemoryTypeOffset]
            : MemoryTypeCodeUnknown;

        return new SmbiosMemoryDevice(deviceLocator, bankLocator, DecodeSize(size, formatted), memoryTypeCode, DecodeConfiguredSpeed(formatted));
    }

    private static bool Covers(ReadOnlySpan<byte> formatted, int offset, int width) => formatted.Length >= offset + width;

    private static ulong? DecodeSize(ushort size, ReadOnlySpan<byte> formatted)
    {
        if (size == SizeUnknown)
        {
            return null;
        }

        if (size == SizeInExtendedField)
        {
            if (!Covers(formatted, ExtendedSizeOffset, sizeof(uint)))
            {
                return null;
            }

            uint extendedMiB = BinaryPrimitives.ReadUInt32LittleEndian(formatted[ExtendedSizeOffset..]) & ExtendedValueMask;
            return extendedMiB == 0 ? null : extendedMiB * BytesPerMiB;
        }

        if ((size & SizeKibGranularityBit) != 0)
        {
            // 0x8000 claims a populated device of 0 KiB; an installed capacity is never 0, so the
            // size is unknown.
            int kib = size & SizeKibValueMask;
            return kib == 0 ? null : (ulong)kib * BytesPerKiB;
        }

        return size * BytesPerMiB;
    }

    private static int? DecodeConfiguredSpeed(ReadOnlySpan<byte> formatted)
    {
        if (!Covers(formatted, ConfiguredSpeedOffset, sizeof(ushort)))
        {
            return null;
        }

        ushort speed = BinaryPrimitives.ReadUInt16LittleEndian(formatted[ConfiguredSpeedOffset..]);
        if (speed == ConfiguredSpeedUnknown)
        {
            return null;
        }

        if (speed != ConfiguredSpeedInExtendedField)
        {
            return speed;
        }

        if (!Covers(formatted, ExtendedConfiguredSpeedOffset, sizeof(uint)))
        {
            return null;
        }

        // Bits 30:0 never exceed int.MaxValue.
        int extendedSpeed = (int)(BinaryPrimitives.ReadUInt32LittleEndian(formatted[ExtendedConfiguredSpeedOffset..]) & ExtendedValueMask);
        return extendedSpeed == 0 ? null : extendedSpeed;
    }

    private static string? ReadString(ReadOnlySpan<byte> stringSet, byte index)
    {
        if (index == 0)
        {
            return null;
        }

        // String numbers are 1-based; the set holds NUL-separated strings, its last one without the
        // trailing NUL that the terminator search consumed.
        ReadOnlySpan<byte> rest = stringSet;
        for (int current = 1; current < index; current++)
        {
            int separator = rest.IndexOf((byte)0);
            if (separator < 0)
            {
                return null;
            }

            rest = rest[(separator + 1)..];
        }

        int end = rest.IndexOf((byte)0);
        string text = Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]).Trim();
        return text.Length == 0 ? null : text;
    }

    private static MemoryInventory Aggregate(IReadOnlyList<SmbiosMemoryDevice> devices)
    {
        ulong? installedBytes = 0;
        int? speedMTs = null;
        var channelKeys = new HashSet<string>(StringComparer.Ordinal);
        bool everyChannelKnown = true;

        foreach (SmbiosMemoryDevice device in devices)
        {
            installedBytes = installedBytes is ulong total && device.SizeBytes is ulong size ? total + size : null;

            if (device.ConfiguredSpeedMTs is int speed && (speedMTs is null || speed < speedMTs))
            {
                speedMTs = speed;
            }

            string? channelKey = GetChannelKey(device.DeviceLocator, device.BankLocator);
            if (channelKey is null)
            {
                everyChannelKnown = false;
            }
            else
            {
                channelKeys.Add(channelKey);
            }
        }

        return new MemoryInventory(
            installedBytes,
            devices.Count,
            speedMTs,
            everyChannelKnown ? channelKeys.Count : null,
            ClassifyMemoryType(devices));
    }

    private static MemoryType ClassifyMemoryType(IReadOnlyList<SmbiosMemoryDevice> devices)
    {
        byte? knownCode = null;
        foreach (SmbiosMemoryDevice device in devices)
        {
            byte code = device.MemoryTypeCode;
            if (code is MemoryTypeCodeUnspecified or MemoryTypeCodeOther or MemoryTypeCodeUnknown)
            {
                continue;
            }

            if (knownCode is null)
            {
                knownCode = code;
            }
            else if (knownCode != code)
            {
                return MemoryType.Other;
            }
        }

        return knownCode switch
        {
            null => MemoryType.Unknown,
            MemoryTypeCodeDdr4 => MemoryType.DDR4,
            MemoryTypeCodeDdr5 => MemoryType.DDR5,
            _ => MemoryType.Other,
        };
    }

    private static string? GetChannelKey(string? deviceLocator, string? bankLocator)
    {
        string? channel = FindGroup(ChannelRegex(), deviceLocator, "id") ?? FindGroup(ChannelRegex(), bankLocator, "id");
        if (channel is not null)
        {
            return BuildControllerPrefix(deviceLocator, bankLocator) + ":" + channel.ToUpperInvariant();
        }

        string? slotLetter = FindGroup(SlotLetterRegex(), deviceLocator, "letter");
        return slotLetter is null ? null : ":" + slotLetter.ToUpperInvariant();
    }

    private static string BuildControllerPrefix(string? deviceLocator, string? bankLocator)
    {
        var tokens = new List<string>(capacity: 3);
        AddControllerToken(tokens, "P", SocketTokenRegex(), deviceLocator, bankLocator);
        AddControllerToken(tokens, "NODE", NodeTokenRegex(), deviceLocator, bankLocator);
        AddControllerToken(tokens, "CONTROLLER", ControllerTokenRegex(), deviceLocator, bankLocator);
        return string.Join('/', tokens);
    }

    private static void AddControllerToken(List<string> tokens, string word, Regex regex, string? deviceLocator, string? bankLocator)
    {
        string? number = FindGroup(regex, deviceLocator, "n") ?? FindGroup(regex, bankLocator, "n");
        if (number is not null)
        {
            tokens.Add(word + number);
        }
    }

    private static string? FindGroup(Regex regex, string? locator, string groupName)
    {
        if (locator is null)
        {
            return null;
        }

        Match match = regex.Match(locator);
        return match.Success ? match.Groups[groupName].Value : null;
    }

    // No match timeout: a timeout would throw, the inputs are short locator strings, and none of
    // these patterns can backtrack catastrophically.
    [GeneratedRegex(@"CHANNEL[ _-]*(?<id>[A-Z](?![A-Z])|[0-9]+)", LocatorRegexOptions)]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])P(?<n>[0-9]+)(?![A-Z0-9])", LocatorRegexOptions)]
    private static partial Regex SocketTokenRegex();

    [GeneratedRegex(@"NODE[ _-]*(?<n>[0-9]+)", LocatorRegexOptions)]
    private static partial Regex NodeTokenRegex();

    [GeneratedRegex(@"CONTROLLER[ _-]*(?<n>[0-9]+)", LocatorRegexOptions)]
    private static partial Regex ControllerTokenRegex();

    [GeneratedRegex(@"^(?:DIMM)?[ _-]?(?<letter>[A-H])[ _-]?[0-9]+$", LocatorRegexOptions)]
    private static partial Regex SlotLetterRegex();
}

/// <summary>
/// One populated SMBIOS Type 17 (Memory Device) structure, as
/// <see cref="SmbiosParser.ParseDevices"/> decodes it.
/// </summary>
/// <param name="DeviceLocator">
/// The Device Locator string (offset 0x10), Latin-1 decoded and trimmed; <see langword="null"/>
/// when absent, blank or unreadable.
/// </param>
/// <param name="BankLocator">
/// The Bank Locator string (offset 0x11), Latin-1 decoded and trimmed; <see langword="null"/> when
/// absent, blank or unreadable.
/// </param>
/// <param name="SizeBytes">
/// The module size in bytes from Size (0x0C) or Extended Size (0x1C); <see langword="null"/> when
/// unknown, never 0.
/// </param>
/// <param name="MemoryTypeCode">
/// The raw Memory Type code (offset 0x12), for example 0x1A for DDR4 or 0x22 for DDR5; 0x02
/// (Unknown) when the structure is too short to hold it.
/// </param>
/// <param name="ConfiguredSpeedMTs">
/// The configured memory speed in MT/s from Configured Memory Speed (0x20) or Extended Configured
/// Memory Speed (0x58); <see langword="null"/> when unknown, never 0.
/// </param>
internal sealed record SmbiosMemoryDevice(
    string? DeviceLocator,
    string? BankLocator,
    ulong? SizeBytes,
    byte MemoryTypeCode,
    int? ConfiguredSpeedMTs);

