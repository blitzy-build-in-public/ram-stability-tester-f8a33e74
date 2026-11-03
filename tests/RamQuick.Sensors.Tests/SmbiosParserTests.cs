using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using RamQuick.Core;
using Xunit;

namespace RamQuick.Sensors.Tests;

/// <summary>
/// Tests for <see cref="SmbiosParser"/> and <see cref="SmbiosMemoryInventorySource"/>: the two
/// synthesised SMBIOS 3.3 fixtures decode to the expected detected-memory summary, every Type 17
/// size, configured-speed, memory-type and locator encoding follows the parser's documented rules,
/// truncated or corrupt tables of any shape never throw, and the inventory source degrades to
/// <see cref="MemoryInventory.Unknown"/> when the firmware table is missing or unreadable.
/// </summary>
/// <remarks>
/// Every table is synthesised. The fixture files and the tables <see cref="SmbiosTableBuilder"/>
/// writes carry locator strings only, never manufacturer, serial or part numbers.
/// <see cref="Builder_ReproducesFixtureLayout"/> keeps the builder byte-identical to the fixture
/// files, so a case built in code exercises the same layout as the fixtures. Timings and voltages
/// are outside the inventory, so nothing here asserts on them.
/// </remarks>
public sealed class SmbiosParserTests
{
    private const string Ddr4FixtureName = "smbios-ddr4-2dimm.bin";
    private const string Ddr5FixtureName = "smbios-ddr5-4dimm.bin";

    // The fixtures' locator strings, verbatim: Intel-style device locators with plain bank
    // locators (DDR4), and AMD-style device locators with the channel in the bank locator (DDR5).
    private const string IntelChannelADimm0 = "Controller0-ChannelA-DIMM0";
    private const string IntelChannelADimm1 = "Controller0-ChannelA-DIMM1";
    private const string IntelChannelBDimm0 = "Controller0-ChannelB-DIMM0";
    private const string IntelChannelBDimm1 = "Controller0-ChannelB-DIMM1";
    private const string Bank0 = "BANK 0";
    private const string Bank1 = "BANK 1";
    private const string Bank2 = "BANK 2";
    private const string Bank3 = "BANK 3";
    private const string AmdDimm0 = "DIMM 0";
    private const string AmdDimm1 = "DIMM 1";
    private const string AmdChannelA = "P0 CHANNEL A";
    private const string AmdChannelB = "P0 CHANNEL B";

    // RawSMBIOSData: four version bytes, then the little-endian 32-bit table Length at offset 4.
    private const int RawHeaderLength = 8;
    private const int RawHeaderTableLengthOffset = 4;

    // Offset of the Device Locator string number inside a Type 17 structure.
    private const int DeviceLocatorOffset = 0x10;

    // Type 17 formatted lengths by the SMBIOS version that introduced them.
    private const byte Type17LengthV21 = 0x15; // through Type Detail: Size, locators and type, no speed
    private const byte Type17LengthV27 = 0x22; // through Configured Memory Speed
    private const byte Type17LengthV28 = 0x28; // through Configured Voltage: no extended speeds
    private const byte Type17LengthV33 = 0x5C; // through Extended Configured Memory Speed
    private const byte Type17LengthBeyondV33 = 0x64; // as if a later version appended fields
    private const byte MinimumStructureLength = 4;

    // SMBIOS Memory Type codes.
    private const byte TypeCodeUnspecified = 0x00;
    private const byte TypeCodeOther = 0x01;
    private const byte TypeCodeUnknown = 0x02;
    private const byte TypeCodeDdr3 = 0x18;
    private const byte TypeCodeDdr4 = 0x1A;
    private const byte TypeCodeDdr5 = 0x22;
    private const byte TypeCodeLpddr5 = 0x23;

    // Size field (0x0C) encodings and the capacities the cases use.
    private const ushort SizeNotInstalled = 0x0000;
    private const ushort SizeUnknown = 0xFFFF;
    private const ushort SizeInExtendedField = 0x7FFF;
    private const ushort SizeKibGranularityFlag = 0x8000;
    private const ushort LargestMibSize = 0x7FFE;
    private const ushort FiveHundredTwelveKiBSize = SizeKibGranularityFlag | 512;
    private const ushort SixteenGiBInMiB = 16384;
    private const uint ThirtyTwoGiBInMiB = 32768;
    private const ulong OneKiB = 1UL << 10;
    private const ulong OneMiB = 1UL << 20;
    private const ulong SixteenGiB = 16UL << 30;
    private const ulong ThirtyTwoGiB = 32UL << 30;
    private const ulong SixtyFourGiB = 64UL << 30;
    private const ulong OneHundredTwentyEightGiB = 128UL << 30;

    // Bit 31 of Extended Size and Extended Configured Memory Speed is reserved.
    private const uint ReservedBit31 = 0x8000_0000;

    // Configured Memory Speed (0x20) encodings and speeds in MT/s.
    private const ushort SpeedUnknown = 0x0000;
    private const ushort SpeedInExtendedField = 0xFFFF;
    private const ushort Ddr4LowSpeedMTs = 2933;
    private const ushort Ddr4SpeedMTs = 3200;
    private const ushort Ddr4RatedSpeedMTs = 3600;
    private const ushort Ddr5SpeedMTs = 6000;

    private const ushort FixtureSlotCount = 4;

    // Robustness sweep: a fixed seed keeps every run reproducible.
    private const int RandomSeed = 20261028;
    private const int RandomTablesPerShape = 200;
    private const int MaxRandomBodyLength = 640;
    private const int MaxRandomDevices = 4;
    private const int MaxCorruptedBytes = 8;

    private static readonly string?[] _randomLocatorPool =
    [
        null,
        "   ",
        IntelChannelADimm0,
        AmdChannelB,
        "DIMM_A1",
        "P0_Node0_Channel1_Dimm0",
        "ChannelB-DIMM1",
        "SLOT1",
        "CHANNEL AB",
    ];

    public static TheoryData<ushort, uint, byte, ulong?> SizeEncodings { get; } = new()
    {
        { SixteenGiBInMiB, 0, Type17LengthV33, SixteenGiB },
        { 1, 0, Type17LengthV33, OneMiB },
        { LargestMibSize, 0, Type17LengthV33, LargestMibSize * OneMiB },
        { SizeInExtendedField, ThirtyTwoGiBInMiB, Type17LengthV33, ThirtyTwoGiB },
        { SizeInExtendedField, ThirtyTwoGiBInMiB | ReservedBit31, Type17LengthV33, ThirtyTwoGiB },
        { SizeInExtendedField, 0, Type17LengthV33, null },
        // Extended Size occupies 0x1C..0x1F: a formatted length of 0x20 just holds it, 0x1F does not.
        { SizeInExtendedField, ThirtyTwoGiBInMiB, 0x20, ThirtyTwoGiB },
        { SizeInExtendedField, ThirtyTwoGiBInMiB, 0x1F, null },
        { FiveHundredTwelveKiBSize, 0, Type17LengthV33, 512 * OneKiB },
        { SizeKibGranularityFlag, 0, Type17LengthV33, null },
        // An unknown size never defers to Extended Size.
        { SizeUnknown, ThirtyTwoGiBInMiB, Type17LengthV33, null },
    };

    public static TheoryData<byte, ushort, uint, int?> ConfiguredSpeedEncodings { get; } = new()
    {
        { Type17LengthV33, Ddr4SpeedMTs, 0, Ddr4SpeedMTs },
        { Type17LengthV33, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs },
        { Type17LengthV33, SpeedInExtendedField, Ddr5SpeedMTs | ReservedBit31, Ddr5SpeedMTs },
        { Type17LengthV33, SpeedInExtendedField, 0, null },
        // 0 is unknown and never defers to the extended field.
        { Type17LengthV33, SpeedUnknown, Ddr5SpeedMTs, null },
        // Extended Configured Memory Speed occupies 0x58..0x5B.
        { Type17LengthV33 - 1, SpeedInExtendedField, Ddr5SpeedMTs, null },
        { Type17LengthV28, Ddr4LowSpeedMTs, 0, Ddr4LowSpeedMTs },
        { Type17LengthV28, SpeedInExtendedField, Ddr5SpeedMTs, null },
        // Configured Memory Speed occupies 0x20..0x21.
        { Type17LengthV27, Ddr4LowSpeedMTs, 0, Ddr4LowSpeedMTs },
        { Type17LengthV27 - 1, Ddr4LowSpeedMTs, 0, null },
        { Type17LengthV21, Ddr4SpeedMTs, 0, null },
    };

    public static TheoryData<byte[], MemoryType> MemoryTypeCodes { get; } = new()
    {
        { [TypeCodeDdr4], MemoryType.DDR4 },
        { [TypeCodeDdr4, TypeCodeDdr4], MemoryType.DDR4 },
        { [TypeCodeDdr5, TypeCodeDdr5], MemoryType.DDR5 },
        { [TypeCodeDdr3], MemoryType.Other },
        { [TypeCodeLpddr5], MemoryType.Other },
        { [TypeCodeDdr3, TypeCodeDdr3], MemoryType.Other },
        { [TypeCodeUnspecified], MemoryType.Unknown },
        { [TypeCodeOther], MemoryType.Unknown },
        { [TypeCodeUnknown], MemoryType.Unknown },
        { [TypeCodeUnknown, TypeCodeOther, TypeCodeUnspecified], MemoryType.Unknown },
        // Codes that say nothing are skipped; only the known codes must agree.
        { [TypeCodeUnknown, TypeCodeDdr4, TypeCodeOther], MemoryType.DDR4 },
        { [TypeCodeDdr5, TypeCodeUnspecified], MemoryType.DDR5 },
        { [TypeCodeDdr4, TypeCodeDdr5], MemoryType.Other },
        { [TypeCodeDdr5, TypeCodeDdr3, TypeCodeDdr5], MemoryType.Other },
    };

    public static TheoryData<string?[], string?[], int?> ChannelLocators { get; } = new()
    {
        // Explicit channel in the device locator, with the controller token as prefix.
        { [IntelChannelADimm1, IntelChannelBDimm1], [Bank1, Bank3], 2 },
        { [IntelChannelADimm0, IntelChannelADimm1], [Bank0, Bank1], 1 },
        { ["Controller0-ChannelA-DIMM0", "Controller1-ChannelA-DIMM0"], [Bank0, Bank2], 2 },
        { [IntelChannelADimm0, "CONTROLLER0-CHANNELA-DIMM1", "controller0-channela-dimm0"], [null, null, null], 1 },
        // Explicit channel in the bank locator, with the socket token as prefix.
        { [AmdDimm0, AmdDimm1, AmdDimm0, AmdDimm1], [AmdChannelA, AmdChannelA, AmdChannelB, AmdChannelB], 2 },
        { [AmdDimm0, AmdDimm0], [AmdChannelA, "P1 CHANNEL A"], 2 },
        // Socket and node tokens, numeric channel identifiers.
        { ["P0_Node0_Channel1_Dimm0", "P0_Node0_Channel1_Dimm1", "P0_Node0_Channel0_Dimm0"], [null, null, null], 2 },
        { ["P0_Node0_Channel1_Dimm0", "P0_Node1_Channel1_Dimm0"], [null, null], 2 },
        { ["Channel 10", "Channel 2", "Channel_10-DIMM1"], [null, null, null], 2 },
        // A channel without any controller token.
        { ["ChannelB-DIMM1", "ChannelB-DIMM0"], [null, null], 1 },
        // The device locator's channel wins over the bank locator's.
        { ["ChannelA-DIMM0", "ChannelA-DIMM1"], ["CHANNEL B", "CHANNEL A"], 1 },
        // Slot letters in a device locator that names no channel.
        { ["DIMM_A1", "DIMM A2", "A1", "B2"], [null, null, null, null], 2 },
        { ["DIMM-B1", "dimm_b2"], [Bank0, Bank1], 1 },
        // No key: the whole count is unknown, even when other devices have one.
        { [AmdDimm1], [Bank0], null },
        { ["SLOT1"], [null], null },
        { ["CHANNEL AB"], [null], null },
        { [null], [null], null },
        { [IntelChannelADimm0, "SLOT1"], [Bank0, Bank1], null },
    };

    // One impossible value per row, every other argument valid.
    public static TheoryData<string, ulong?, int, int?, int?, MemoryType> ImpossibleInventoryValues { get; } = new()
    {
        { nameof(MemoryInventory.InstalledBytes), 0, 1, Ddr4SpeedMTs, 1, MemoryType.DDR4 },
        { nameof(MemoryInventory.DimmCount), SixteenGiB, -1, Ddr4SpeedMTs, 1, MemoryType.DDR4 },
        { nameof(MemoryInventory.SpeedMTs), SixteenGiB, 1, 0, 1, MemoryType.DDR4 },
        { nameof(MemoryInventory.SpeedMTs), SixteenGiB, 1, -Ddr4SpeedMTs, 1, MemoryType.DDR4 },
        { nameof(MemoryInventory.ChannelCount), SixteenGiB, 1, Ddr4SpeedMTs, 0, MemoryType.DDR4 },
        { nameof(MemoryInventory.ChannelCount), SixteenGiB, 1, Ddr4SpeedMTs, -1, MemoryType.DDR4 },
        { nameof(MemoryInventory.MemoryType), SixteenGiB, 1, Ddr4SpeedMTs, 1, (MemoryType)(-1) },
        { nameof(MemoryInventory.MemoryType), SixteenGiB, 1, Ddr4SpeedMTs, 1, MemoryType.Other + 1 },
    };

    [Theory]
    [InlineData(Ddr4FixtureName)]
    [InlineData(Ddr5FixtureName)]
    public void Builder_ReproducesFixtureLayout(string fixtureName)
    {
        byte[] fixture = LoadFixture(fixtureName);
        byte[] built = FixtureEquivalent(fixtureName).Build();

        Assert.Equal(fixture, built);
        Assert.Equal(SmbiosParser.Parse(fixture), SmbiosParser.Parse(built));
    }

    [Fact]
    public void Parse_Ddr4Fixture_ReportsTwoPopulatedDimms()
    {
        // The fixture lists four slots; the two of Size 0 are empty and not counted.
        MemoryInventory inventory = SmbiosParser.Parse(LoadFixture(Ddr4FixtureName));

        Assert.Equal(ExpectedInventory(Ddr4FixtureName), inventory);
    }

    [Fact]
    public void Parse_Ddr5Fixture_ReportsFourDimmsWithExtendedConfiguredSpeed()
    {
        // Every Configured Memory Speed word of the fixture is 0xFFFF, so 6000 MT/s can only come
        // from Extended Configured Memory Speed (see Builder_ReproducesFixtureLayout).
        MemoryInventory inventory = SmbiosParser.Parse(LoadFixture(Ddr5FixtureName));

        Assert.Equal(ExpectedInventory(Ddr5FixtureName), inventory);
    }

    [Fact]
    public void ParseDevices_Ddr4Fixture_ReturnsPopulatedDevicesWithLocators()
    {
        SmbiosMemoryDevice[] expected =
        [
            new(IntelChannelADimm1, Bank1, SixteenGiB, TypeCodeDdr4, Ddr4SpeedMTs),
            new(IntelChannelBDimm1, Bank3, SixteenGiB, TypeCodeDdr4, Ddr4SpeedMTs),
        ];

        Assert.Equal(expected, SmbiosParser.ParseDevices(LoadFixture(Ddr4FixtureName)));
    }

    [Fact]
    public void ParseDevices_Ddr5Fixture_ReturnsFourDevices()
    {
        SmbiosMemoryDevice[] expected =
        [
            new(AmdDimm0, AmdChannelA, SixteenGiB, TypeCodeDdr5, Ddr5SpeedMTs),
            new(AmdDimm1, AmdChannelA, SixteenGiB, TypeCodeDdr5, Ddr5SpeedMTs),
            new(AmdDimm0, AmdChannelB, SixteenGiB, TypeCodeDdr5, Ddr5SpeedMTs),
            new(AmdDimm1, AmdChannelB, SixteenGiB, TypeCodeDdr5, Ddr5SpeedMTs),
        ];

        Assert.Equal(expected, SmbiosParser.ParseDevices(LoadFixture(Ddr5FixtureName)));
    }

    [Theory]
    [MemberData(nameof(SizeEncodings))]
    public void Parse_SizeField_DecodedPerEncoding(ushort size, uint extendedSizeMiB, byte formattedLength, ulong? expectedBytes)
    {
        byte[] table = new SmbiosTableBuilder()
            .AddType17(size, extendedSizeMiB, TypeCodeDdr4, Ddr4SpeedMTs, Ddr4SpeedMTs, 0, 0, IntelChannelADimm0, Bank0, formattedLength)
            .AddEnd()
            .Build();

        SmbiosMemoryDevice device = Assert.Single(SmbiosParser.ParseDevices(table));
        MemoryInventory inventory = SmbiosParser.Parse(table);

        Assert.Equal(expectedBytes, device.SizeBytes);
        Assert.Equal(expectedBytes, inventory.InstalledBytes);
        Assert.Equal(1, inventory.DimmCount);
    }

    [Fact]
    public void Parse_SizeZero_SlotExcluded()
    {
        byte[] oneEmptySlot = new SmbiosTableBuilder()
            .AddType17(SizeNotInstalled, ThirtyTwoGiBInMiB, TypeCodeDdr4, Ddr4SpeedMTs, Ddr4SpeedMTs, 0, 0, IntelChannelADimm0, Bank0)
            .AddDimm(IntelChannelBDimm0, Bank2)
            .AddEnd()
            .Build();

        SmbiosMemoryDevice device = Assert.Single(SmbiosParser.ParseDevices(oneEmptySlot));
        Assert.Equal(IntelChannelBDimm0, device.DeviceLocator);
        Assert.Equal(new MemoryInventory(SixteenGiB, DimmCount: 1, Ddr4SpeedMTs, ChannelCount: 1, MemoryType.DDR4), SmbiosParser.Parse(oneEmptySlot));
    }

    [Fact]
    public void Parse_SizeUnknown_CountedWithoutInstalledBytes()
    {
        // A device of unknown size is populated, so it counts, but the sum becomes unknown.
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0)
            .AddType17(SizeUnknown, 0, TypeCodeDdr4, Ddr4SpeedMTs, Ddr4SpeedMTs, 0, 0, IntelChannelBDimm0, Bank2)
            .AddEnd()
            .Build();

        Assert.Equal(new MemoryInventory(null, DimmCount: 2, Ddr4SpeedMTs, ChannelCount: 2, MemoryType.DDR4), SmbiosParser.Parse(table));
    }

    [Theory]
    [InlineData(MinimumStructureLength)]
    [InlineData((byte)0x0C)]
    [InlineData((byte)0x0D)]
    public void ParseDevices_StructureShorterThanSizeField_Ignored(byte formattedLength)
    {
        // Size occupies 0x0C..0x0D; a structure that cannot hold it reads like an empty slot.
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, formattedLength: formattedLength)
            .AddEnd()
            .Build();

        Assert.Empty(SmbiosParser.ParseDevices(table));
        Assert.Equal(MemoryInventory.Unknown, SmbiosParser.Parse(table));
    }

    [Fact]
    public void ParseDevices_StructureJustHoldingSizeField_CountedWithSize()
    {
        const byte JustHoldingSize = 0x0E;
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, formattedLength: JustHoldingSize)
            .AddEnd()
            .Build();

        // Locators, type and speed lie beyond the structure, so only the size is known.
        Assert.Equal(new SmbiosMemoryDevice(null, null, SixteenGiB, TypeCodeUnknown, null), Assert.Single(SmbiosParser.ParseDevices(table)));
        Assert.Equal(new MemoryInventory(SixteenGiB, DimmCount: 1, null, null, MemoryType.Unknown), SmbiosParser.Parse(table));
    }

    [Theory]
    [MemberData(nameof(ConfiguredSpeedEncodings))]
    public void Parse_ConfiguredSpeedField_DecodedPerEncodingAndLength(byte formattedLength, ushort configuredSpeed, uint extendedConfiguredSpeed, int? expectedMTs)
    {
        // The rated Speed fields hold a valid value throughout, so a speed reported where the
        // configured fields are unknown or absent would have come from the wrong field.
        byte[] table = new SmbiosTableBuilder()
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr4, Ddr4RatedSpeedMTs, configuredSpeed, Ddr4RatedSpeedMTs, extendedConfiguredSpeed, IntelChannelADimm0, Bank0, formattedLength)
            .AddEnd()
            .Build();

        SmbiosMemoryDevice device = Assert.Single(SmbiosParser.ParseDevices(table));
        MemoryInventory inventory = SmbiosParser.Parse(table);

        Assert.Equal(expectedMTs, device.ConfiguredSpeedMTs);
        Assert.Equal(expectedMTs, inventory.SpeedMTs);
        Assert.Equal(1, inventory.DimmCount);
        Assert.Equal(SixteenGiB, inventory.InstalledBytes);
    }

    [Fact]
    public void Parse_ShortPre33Structure_SpeedFromAvailableFieldsOrNull()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, configuredSpeed: Ddr4LowSpeedMTs, formattedLength: Type17LengthV28)
            .AddDimm(IntelChannelBDimm0, Bank2, configuredSpeed: Ddr4SpeedMTs, formattedLength: Type17LengthV21)
            .AddEnd()
            .Build();

        Assert.Equal(
            new MemoryInventory(ThirtyTwoGiB, DimmCount: 2, Ddr4LowSpeedMTs, ChannelCount: 2, MemoryType.DDR4),
            SmbiosParser.Parse(table));
    }

    [Theory]
    [InlineData(Ddr4LowSpeedMTs, Ddr4SpeedMTs, Ddr4LowSpeedMTs)]
    [InlineData(Ddr4SpeedMTs, Ddr4LowSpeedMTs, Ddr4LowSpeedMTs)]
    [InlineData(SpeedUnknown, Ddr4SpeedMTs, Ddr4SpeedMTs)]
    [InlineData(Ddr5SpeedMTs, SpeedUnknown, Ddr5SpeedMTs)]
    public void Parse_DimmsWithDifferentSpeeds_ReportsLowestKnown(ushort firstSpeed, ushort secondSpeed, int expectedMTs)
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, configuredSpeed: firstSpeed)
            .AddDimm(IntelChannelBDimm0, Bank2, configuredSpeed: secondSpeed)
            .AddEnd()
            .Build();

        Assert.Equal(expectedMTs, SmbiosParser.Parse(table).SpeedMTs);
    }

    [Fact]
    public void Parse_NoDeviceReportsConfiguredSpeed_SpeedNullWithRestKnown()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, configuredSpeed: SpeedUnknown)
            .AddDimm(IntelChannelBDimm0, Bank2, configuredSpeed: SpeedUnknown)
            .AddEnd()
            .Build();

        Assert.Equal(new MemoryInventory(ThirtyTwoGiB, DimmCount: 2, null, ChannelCount: 2, MemoryType.DDR4), SmbiosParser.Parse(table));
    }

    [Theory]
    [MemberData(nameof(MemoryTypeCodes))]
    public void Parse_MemoryTypeCodes_ClassifiedAcrossDevices(byte[] typeCodes, MemoryType expected)
    {
        var builder = new SmbiosTableBuilder();
        foreach (byte typeCode in typeCodes)
        {
            builder.AddDimm(IntelChannelADimm0, Bank0, memoryType: typeCode);
        }

        byte[] table = builder.AddEnd().Build();

        Assert.Equal(typeCodes, SmbiosParser.ParseDevices(table).Select(device => device.MemoryTypeCode));
        Assert.Equal(expected, SmbiosParser.Parse(table).MemoryType);
    }

    [Fact]
    public void ParseDevices_StructureWithoutMemoryTypeField_ReadsUnknownCode()
    {
        // Memory Type sits at 0x12; a structure of formatted length 0x12 still holds both locators.
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0, memoryType: TypeCodeDdr5, formattedLength: 0x12)
            .AddEnd()
            .Build();

        Assert.Equal(new SmbiosMemoryDevice(IntelChannelADimm0, Bank0, SixteenGiB, TypeCodeUnknown, null), Assert.Single(SmbiosParser.ParseDevices(table)));
        Assert.Equal(MemoryType.Unknown, SmbiosParser.Parse(table).MemoryType);
    }

    [Theory]
    [MemberData(nameof(ChannelLocators))]
    public void Parse_LocatorStrings_ChannelCountPerDocumentedRules(string?[] deviceLocators, string?[] bankLocators, int? expectedChannels)
    {
        Assert.Equal(deviceLocators.Length, bankLocators.Length);
        var builder = new SmbiosTableBuilder();
        for (int i = 0; i < deviceLocators.Length; i++)
        {
            builder.AddDimm(deviceLocators[i], bankLocators[i]);
        }

        MemoryInventory inventory = SmbiosParser.Parse(builder.AddEnd().Build());

        Assert.Equal(expectedChannels, inventory.ChannelCount);
        Assert.Equal(deviceLocators.Length, inventory.DimmCount);
    }

    [Fact]
    public void Parse_UnparseableLocators_ChannelCountNull()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm("SLOT1", null)
            .AddDimm("SLOT2", "   ")
            .AddEnd()
            .Build();

        // Only the channel count is unknown; the rest of the summary is still reported.
        Assert.Equal(new MemoryInventory(ThirtyTwoGiB, DimmCount: 2, Ddr4SpeedMTs, null, MemoryType.DDR4), SmbiosParser.Parse(table));
    }

    [Fact]
    public void ParseDevices_LocatorStrings_DecodedAsLatin1AndTrimmed()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm("  DIMM_A1\t", "   ")
            .AddDimm(null, "B\u00E4nk 1")
            .AddEnd()
            .Build();

        IReadOnlyList<SmbiosMemoryDevice> devices = SmbiosParser.ParseDevices(table);

        Assert.Equal(2, devices.Count);
        Assert.Equal("DIMM_A1", devices[0].DeviceLocator);
        Assert.Null(devices[0].BankLocator);
        Assert.Null(devices[1].DeviceLocator);
        Assert.Equal("B\u00E4nk 1", devices[1].BankLocator);
    }

    [Fact]
    public void ParseDevices_LocatorStringNumberBeyondStringSet_ReadsNull()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm0, Bank0)
            .AddEnd()
            .Build();

        // The structure holds two strings; point the Device Locator at a third.
        table[RawHeaderLength + DeviceLocatorOffset] = 3;

        SmbiosMemoryDevice device = Assert.Single(SmbiosParser.ParseDevices(table));
        Assert.Null(device.DeviceLocator);
        Assert.Equal(Bank0, device.BankLocator);
    }

    [Fact]
    public void Parse_NoPopulatedMemoryDevice_ReturnsUnknown()
    {
        byte[][] tables =
        [
            new SmbiosTableBuilder().Build(),
            new SmbiosTableBuilder().AddType16(FixtureSlotCount, OneHundredTwentyEightGiB).AddEnd().Build(),
            new SmbiosTableBuilder()
                .AddType16(FixtureSlotCount, OneHundredTwentyEightGiB)
                .AddType17(SizeNotInstalled, 0, TypeCodeUnknown, SpeedUnknown, SpeedUnknown, 0, 0, IntelChannelADimm0, Bank0)
                .AddType17(SizeNotInstalled, 0, TypeCodeUnknown, SpeedUnknown, SpeedUnknown, 0, 0, IntelChannelBDimm0, Bank2)
                .AddEnd()
                .Build(),
        ];

        Assert.All(tables, table =>
        {
            Assert.Empty(SmbiosParser.ParseDevices(table));
            Assert.Equal(MemoryInventory.Unknown, SmbiosParser.Parse(table));
        });
    }

    [Theory]
    [InlineData((byte)2, (byte)6)]
    [InlineData((byte)3, (byte)0)]
    [InlineData((byte)3, (byte)7)]
    public void Parse_HeaderVersionBytes_NotConsulted(byte majorVersion, byte minorVersion)
    {
        byte[] table = FixtureEquivalent(Ddr5FixtureName).Build(majorVersion, minorVersion);

        Assert.Equal(ExpectedInventory(Ddr5FixtureName), SmbiosParser.Parse(table));
    }

    [Fact]
    public void Parse_StructuresAfterEndOfTable_Ignored()
    {
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm1, Bank1)
            .AddEnd()
            .AddDimm(IntelChannelBDimm1, Bank3)
            .Build();

        Assert.Equal(IntelChannelADimm1, Assert.Single(SmbiosParser.ParseDevices(table)).DeviceLocator);
    }

    [Fact]
    public void Parse_StructureLongerThanSmbios33_ReadsKnownOffsets()
    {
        // A later SMBIOS version may append fields; the known offsets keep their meaning.
        byte[] table = new SmbiosTableBuilder()
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr5, SpeedInExtendedField, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs, AmdDimm0, AmdChannelA, Type17LengthBeyondV33)
            .AddEnd()
            .Build();

        Assert.Equal(new MemoryInventory(SixteenGiB, DimmCount: 1, Ddr5SpeedMTs, ChannelCount: 1, MemoryType.DDR5), SmbiosParser.Parse(table));
    }


    [Fact]
    public void Parse_Empty_ReturnsUnknown()
    {
        Assert.Empty(SmbiosParser.ParseDevices(ReadOnlySpan<byte>.Empty));
        Assert.Equal(MemoryInventory.Unknown, SmbiosParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(RawHeaderLength - 1)]
    public void Parse_TruncatedHeader_ReturnsUnknown(int length)
    {
        byte[] truncated = LoadFixture(Ddr4FixtureName)[..length];

        Assert.Empty(SmbiosParser.ParseDevices(truncated));
        Assert.Equal(MemoryInventory.Unknown, SmbiosParser.Parse(truncated));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4096u)]
    [InlineData(uint.MaxValue)]
    public void Parse_HeaderLengthBeyondData_DoesNotThrow(uint excessLength)
    {
        // Only the bytes present are walked, so the declared overrun changes nothing.
        SmbiosTableBuilder builder = FixtureEquivalent(Ddr4FixtureName);
        uint declaredLength = (uint)Math.Min(uint.MaxValue, (ulong)builder.BuildStructures().Length + excessLength);

        MemoryInventory inventory = SmbiosParser.Parse(builder.Build(lengthOverride: declaredLength));

        Assert.Equal(ExpectedInventory(Ddr4FixtureName), inventory);
    }

    [Fact]
    public void Parse_HeaderLengthShorterThanData_WalksDeclaredLengthOnly()
    {
        var builder = new SmbiosTableBuilder().AddDimm(IntelChannelADimm1, Bank1);
        uint firstDimmLength = (uint)builder.BuildStructures().Length;
        byte[] table = builder.AddDimm(IntelChannelBDimm1, Bank3).AddEnd().Build(lengthOverride: firstDimmLength);

        Assert.Equal(new MemoryInventory(SixteenGiB, DimmCount: 1, Ddr4SpeedMTs, ChannelCount: 1, MemoryType.DDR4), SmbiosParser.Parse(table));
    }

    [Fact]
    public void Parse_TruncatedStructure_DoesNotThrow()
    {
        const int TruncatedLength = 0x30;
        byte[] truncatedDimm = new SmbiosTableBuilder().AddDimm(IntelChannelBDimm1, Bank3).BuildStructures()[..TruncatedLength];
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm1, Bank1)
            .AddRaw(truncatedDimm)
            .Build();

        // The walk stops at the structure that runs past the table; the device before it stays.
        SmbiosMemoryDevice device = Assert.Single(SmbiosParser.ParseDevices(table));
        Assert.Equal(IntelChannelADimm1, device.DeviceLocator);
        Assert.Equal(1, SmbiosParser.Parse(table).DimmCount);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Parse_StructureLengthBelowFour_StopsWithoutThrowing(byte formattedLength)
    {
        // A length below the 4-byte structure header must end the walk. The bad header's last two
        // bytes read as an empty string set, so a walk that accepted lengths 1 or 2 would step
        // straight onto the next device and count it.
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm1, Bank1)
            .AddRaw([17, formattedLength, 0x00, 0x00])
            .AddDimm(IntelChannelBDimm1, Bank3)
            .AddEnd()
            .Build();

        Assert.Equal(IntelChannelADimm1, Assert.Single(SmbiosParser.ParseDevices(table)).DeviceLocator);
        Assert.Equal(1, SmbiosParser.Parse(table).DimmCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Parse_MissingStringTerminator_LocatorsNullNoThrow(int strippedBytes)
    {
        // Removing the final NUL bytes leaves the last string set without its double NUL.
        byte[] table = new SmbiosTableBuilder()
            .AddDimm(IntelChannelADimm1, Bank1)
            .Build()[..^strippedBytes];

        Assert.Equal(new SmbiosMemoryDevice(null, null, SixteenGiB, TypeCodeDdr4, Ddr4SpeedMTs), Assert.Single(SmbiosParser.ParseDevices(table)));
        Assert.Equal(new MemoryInventory(SixteenGiB, DimmCount: 1, Ddr4SpeedMTs, null, MemoryType.DDR4), SmbiosParser.Parse(table));
    }

    [Theory]
    [InlineData(Ddr4FixtureName)]
    [InlineData(Ddr5FixtureName)]
    public void Parse_EveryFixturePrefix_DoesNotThrow(string fixtureName)
    {
        byte[] fixture = LoadFixture(fixtureName);
        MemoryInventory complete = ExpectedInventory(fixtureName);

        for (int length = 0; length <= fixture.Length; length++)
        {
            MemoryInventory prefixInventory = ParseConsistently(fixture[..length]);
            Assert.InRange(prefixInventory.DimmCount, 0, complete.DimmCount);
        }

        Assert.Equal(complete, ParseConsistently(fixture));
    }

    [Fact]
    public void Parse_SeededRandomTables_DoNotThrow()
    {
        var random = new Random(RandomSeed);

        for (int i = 0; i < RandomTablesPerShape; i++)
        {
            ParseConsistently(RandomBytesTable(random));
            ParseConsistently(RandomMemoryDeviceTable(random));
            ParseConsistently(CorruptedBuiltTable(random));
        }
    }

    [Fact]
    public void Unknown_HasNoDimmsAndEveryFieldUnknown()
    {
        Assert.Equal(new MemoryInventory(null, DimmCount: 0, null, null, MemoryType.Unknown), MemoryInventory.Unknown);
    }

    // ParseConsistently relies on these checks to reject any impossible aggregate, so they are
    // asserted here, on both the constructor and the init accessors a with expression uses.
    [Theory]
    [MemberData(nameof(ImpossibleInventoryValues))]
    public void MemoryInventory_ImpossibleValue_RejectedAtConstructionAndInit(
        string propertyName, ulong? installedBytes, int dimmCount, int? speedMTs, int? channelCount, MemoryType memoryType)
    {
        MemoryInventory valid = ExpectedInventory(Ddr4FixtureName);

        Assert.Throws<ArgumentOutOfRangeException>(
            propertyName,
            () => new MemoryInventory(installedBytes, dimmCount, speedMTs, channelCount, memoryType));
        Assert.Throws<ArgumentOutOfRangeException>(propertyName, () => propertyName switch
        {
            nameof(MemoryInventory.InstalledBytes) => valid with { InstalledBytes = installedBytes },
            nameof(MemoryInventory.DimmCount) => valid with { DimmCount = dimmCount },
            nameof(MemoryInventory.SpeedMTs) => valid with { SpeedMTs = speedMTs },
            nameof(MemoryInventory.ChannelCount) => valid with { ChannelCount = channelCount },
            nameof(MemoryInventory.MemoryType) => valid with { MemoryType = memoryType },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Not a MemoryInventory property."),
        });
    }

    [Theory]
    [InlineData(Ddr4FixtureName)]
    [InlineData(Ddr5FixtureName)]
    public void Read_FixtureBytes_MatchesParse(string fixtureName)
    {
        IMemoryInventorySource source = new SmbiosMemoryInventorySource(() => LoadFixture(fixtureName));

        MemoryInventory inventory = source.Read();

        Assert.Equal(SmbiosParser.Parse(LoadFixture(fixtureName)), inventory);
        Assert.Equal(ExpectedInventory(fixtureName), inventory);
    }

    [Fact]
    public void Read_TableUnavailable_ReturnsUnknown()
    {
        Assert.Equal(MemoryInventory.Unknown, new SmbiosMemoryInventorySource(() => null).Read());
        Assert.Equal(MemoryInventory.Unknown, new SmbiosMemoryInventorySource(() => []).Read());
    }

    [Fact]
    public void Read_TableReadThrows_ReturnsUnknown()
    {
        Exception[] failures =
        [
            new InvalidOperationException("The firmware table changed during the read."),
            new Win32Exception(1),
            new IOException("The firmware table provider failed."),
            new IndexOutOfRangeException(),
        ];

        Assert.All(failures, failure =>
            Assert.Equal(MemoryInventory.Unknown, new SmbiosMemoryInventorySource(() => throw failure).Read()));
    }

    [Fact]
    public void Read_TableReadOutOfMemory_Propagates()
    {
        var source = new SmbiosMemoryInventorySource(() => throw new OutOfMemoryException());

        Assert.Throws<OutOfMemoryException>(() => source.Read());
    }

    [Fact]
    public void Read_RepeatedCalls_RereadTableEachTime()
    {
        var fixtures = new Queue<string>([Ddr4FixtureName, Ddr5FixtureName]);
        var source = new SmbiosMemoryInventorySource(() => LoadFixture(fixtures.Dequeue()));

        Assert.Equal(ExpectedInventory(Ddr4FixtureName), source.Read());
        Assert.Equal(ExpectedInventory(Ddr5FixtureName), source.Read());
        Assert.Empty(fixtures);
    }

    [Fact]
    public void Constructor_NullTableReader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("readRawTable", () => new SmbiosMemoryInventorySource(null!));
    }

    [Fact]
    public void Read_RealFirmwareTable_DoesNotThrow()
    {
        // A read-only firmware table query: no driver, no elevation and no sensor access. Values
        // depend on the machine, and a virtual machine may legitimately report Unknown.
        IMemoryInventorySource source = new SmbiosMemoryInventorySource();

        MemoryInventory inventory = source.Read();

        Assert.NotNull(inventory);
        Assert.True(
            inventory.DimmCount > 0 || inventory == MemoryInventory.Unknown,
            $"An inventory without DIMMs must be Unknown, but was {inventory}.");
    }

    [Fact]
    public void ReadRawSmbiosTable_RealFirmware_ReturnsCompleteRsmbBlob()
    {
        // Windows on x64 always exposes the raw SMBIOS table, so null here means the query itself
        // is wrong, for example a provider signature with its bytes reversed.
        byte[]? rawTable = NativeFirmware.ReadRawSmbiosTable();

        Assert.NotNull(rawTable);
        Assert.True(rawTable.Length > RawHeaderLength, $"The raw SMBIOS table holds only {rawTable.Length} bytes.");
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(rawTable.AsSpan(RawHeaderTableLengthOffset));
        Assert.InRange(declaredLength, 1u, (uint)(rawTable.Length - RawHeaderLength));
        Assert.Equal(SmbiosParser.Parse(rawTable), new SmbiosMemoryInventorySource().Read());
    }

    private static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MemoryInventory ExpectedInventory(string fixtureName) => fixtureName switch
    {
        Ddr4FixtureName => new MemoryInventory(ThirtyTwoGiB, DimmCount: 2, Ddr4SpeedMTs, ChannelCount: 2, MemoryType.DDR4),
        Ddr5FixtureName => new MemoryInventory(SixtyFourGiB, DimmCount: 4, Ddr5SpeedMTs, ChannelCount: 2, MemoryType.DDR5),
        _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "Not a fixture of this suite."),
    };

    // The builder calls that write each fixture file byte for byte.
    private static SmbiosTableBuilder FixtureEquivalent(string fixtureName) => fixtureName switch
    {
        Ddr4FixtureName => new SmbiosTableBuilder()
            .AddType16(FixtureSlotCount, OneHundredTwentyEightGiB)
            .AddType17(SizeNotInstalled, 0, TypeCodeUnknown, SpeedUnknown, SpeedUnknown, 0, 0, IntelChannelADimm0, Bank0)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr4, Ddr4SpeedMTs, Ddr4SpeedMTs, 0, 0, IntelChannelADimm1, Bank1)
            .AddType17(SizeNotInstalled, 0, TypeCodeUnknown, SpeedUnknown, SpeedUnknown, 0, 0, IntelChannelBDimm0, Bank2)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr4, Ddr4SpeedMTs, Ddr4SpeedMTs, 0, 0, IntelChannelBDimm1, Bank3)
            .AddEnd(),
        Ddr5FixtureName => new SmbiosTableBuilder()
            .AddType16(FixtureSlotCount, OneHundredTwentyEightGiB)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr5, SpeedInExtendedField, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs, AmdDimm0, AmdChannelA)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr5, SpeedInExtendedField, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs, AmdDimm1, AmdChannelA)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr5, SpeedInExtendedField, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs, AmdDimm0, AmdChannelB)
            .AddType17(SixteenGiBInMiB, 0, TypeCodeDdr5, SpeedInExtendedField, SpeedInExtendedField, Ddr5SpeedMTs, Ddr5SpeedMTs, AmdDimm1, AmdChannelB)
            .AddEnd(),
        _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "Not a fixture of this suite."),
    };

    // Parses through both entry points and checks the invariants every input must keep: the
    // inventory counts exactly the decoded devices, no decoded size or speed is 0, and a table
    // without a device gives Unknown. MemoryInventory's own validation rejects any other
    // impossible aggregate by throwing.
    private static MemoryInventory ParseConsistently(byte[] table)
    {
        IReadOnlyList<SmbiosMemoryDevice> devices = SmbiosParser.ParseDevices(table);
        MemoryInventory inventory = SmbiosParser.Parse(table);

        Assert.Equal(devices.Count, inventory.DimmCount);
        Assert.All(devices, device =>
        {
            Assert.NotEqual(0UL, device.SizeBytes);
            Assert.NotEqual(0, device.ConfiguredSpeedMTs);
        });
        if (devices.Count == 0)
        {
            Assert.Equal(MemoryInventory.Unknown, inventory);
        }

        return inventory;
    }

    // Random bytes behind a header that declares either their exact length or a random one.
    private static byte[] RandomBytesTable(Random random)
    {
        byte[] body = new byte[random.Next(MaxRandomBodyLength + 1)];
        random.NextBytes(body);
        return WithRandomHeader(body, random);
    }

    // A structure stream that opens with a Type 17 header of random length, with frequent zero
    // bytes so that string terminators, and further structures, occur.
    private static byte[] RandomMemoryDeviceTable(Random random)
    {
        byte[] body = new byte[random.Next(MinimumStructureLength, MaxRandomBodyLength + 1)];
        random.NextBytes(body);
        for (int i = 0; i < body.Length; i++)
        {
            if (random.Next(4) == 0)
            {
                body[i] = 0;
            }
        }

        body[0] = 17;
        body[1] = (byte)random.Next(byte.MaxValue + 1);
        return WithRandomHeader(body, random);
    }

    // A well-formed table of devices with random field values and formatted lengths, then a few
    // bytes anywhere, header included, overwritten at random.
    private static byte[] CorruptedBuiltTable(Random random)
    {
        var builder = new SmbiosTableBuilder();
        int deviceCount = random.Next(1, MaxRandomDevices + 1);
        for (int i = 0; i < deviceCount; i++)
        {
            builder.AddType17(
                (ushort)random.Next(ushort.MaxValue + 1),
                (uint)random.NextInt64(uint.MaxValue + 1L),
                (byte)random.Next(byte.MaxValue + 1),
                (ushort)random.Next(ushort.MaxValue + 1),
                (ushort)random.Next(ushort.MaxValue + 1),
                (uint)random.NextInt64(uint.MaxValue + 1L),
                (uint)random.NextInt64(uint.MaxValue + 1L),
                _randomLocatorPool[random.Next(_randomLocatorPool.Length)],
                _randomLocatorPool[random.Next(_randomLocatorPool.Length)],
                (byte)random.Next(MinimumStructureLength, Type17LengthBeyondV33 + 1));
        }

        byte[] table = builder.AddEnd().Build();
        int corruptedBytes = random.Next(MaxCorruptedBytes + 1);
        for (int i = 0; i < corruptedBytes; i++)
        {
            table[random.Next(table.Length)] = (byte)random.Next(byte.MaxValue + 1);
        }

        return table;
    }

    private static byte[] WithRandomHeader(byte[] body, Random random)
    {
        uint declaredLength = random.Next(2) == 0 ? (uint)body.Length : (uint)random.NextInt64(uint.MaxValue + 1L);
        return new SmbiosTableBuilder().AddRaw(body).Build(lengthOverride: declaredLength);
    }


    /// <summary>
    /// Writes a raw SMBIOS table in the <c>GetSystemFirmwareTable('RSMB')</c> layout: the 8-byte
    /// <c>RawSMBIOSData</c> header, then the structures in the order they were added.
    /// </summary>
    /// <remarks>
    /// The builder follows the fixture files' conventions, so equal calls give equal bytes:
    /// handles count up from 0x0010 in table order, end-of-table has handle 0xFEFF, Type 17 widths
    /// are 64 bits for a populated slot and unknown for an empty one, the form factor is DIMM, and
    /// only the two locator strings are written. A Type 17 with a formatted length below 0x5C keeps
    /// the fields that fit, as an older SMBIOS version would write it.
    /// </remarks>
    private sealed class SmbiosTableBuilder
    {
        private const byte PhysicalMemoryArrayType = 16;
        private const byte PhysicalMemoryArrayLength = 0x17;
        private const byte MemoryDeviceType = 17;
        private const byte EndOfTableType = 127;
        private const ushort FirstHandle = 0x0010;
        private const ushort EndOfTableHandle = 0xFEFF;
        private const ushort NoHandle = 0xFFFE;
        private const byte LocationSystemBoard = 0x03;
        private const byte UseSystemMemory = 0x03;
        private const byte ErrorCorrectionNone = 0x03;
        private const uint MaximumCapacityInExtendedField = 0x8000_0000;
        private const ushort PopulatedWidthBits = 64;
        private const ushort UnknownWidth = 0xFFFF;
        private const byte FormFactorDimm = 0x09;

        // Type 16 (Physical Memory Array) offsets.
        private const int ArrayLocationOffset = 0x04;
        private const int ArrayUseOffset = 0x05;
        private const int ArrayErrorCorrectionOffset = 0x06;
        private const int ArrayMaximumCapacityOffset = 0x07;
        private const int ArrayErrorInformationHandleOffset = 0x0B;
        private const int ArrayDeviceCountOffset = 0x0D;
        private const int ArrayExtendedMaximumCapacityOffset = 0x0F;

        // Type 17 (Memory Device) offsets, SMBIOS 3.3.
        private const int DeviceArrayHandleOffset = 0x04;
        private const int DeviceErrorInformationHandleOffset = 0x06;
        private const int DeviceTotalWidthOffset = 0x08;
        private const int DeviceDataWidthOffset = 0x0A;
        private const int DeviceSizeOffset = 0x0C;
        private const int DeviceFormFactorOffset = 0x0E;
        private const int DeviceBankLocatorOffset = 0x11;
        private const int DeviceMemoryTypeOffset = 0x12;
        private const int DeviceSpeedOffset = 0x15;
        private const int DeviceExtendedSizeOffset = 0x1C;
        private const int DeviceConfiguredSpeedOffset = 0x20;
        private const int DeviceExtendedSpeedOffset = 0x54;
        private const int DeviceExtendedConfiguredSpeedOffset = 0x58;

        private const int HandleOffset = 0x02;

        private readonly List<byte> _structures = [];
        private ushort _nextHandle = FirstHandle;
        private ushort _arrayHandle = NoHandle;

        public SmbiosTableBuilder AddType16(ushort deviceCount, ulong extendedMaximumCapacityBytes)
        {
            byte[] formatted = NewStructure(PhysicalMemoryArrayType, PhysicalMemoryArrayLength, PhysicalMemoryArrayLength);
            _arrayHandle = BinaryPrimitives.ReadUInt16LittleEndian(formatted.AsSpan(HandleOffset));
            formatted[ArrayLocationOffset] = LocationSystemBoard;
            formatted[ArrayUseOffset] = UseSystemMemory;
            formatted[ArrayErrorCorrectionOffset] = ErrorCorrectionNone;
            BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(ArrayMaximumCapacityOffset), MaximumCapacityInExtendedField);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(ArrayErrorInformationHandleOffset), NoHandle);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(ArrayDeviceCountOffset), deviceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(formatted.AsSpan(ArrayExtendedMaximumCapacityOffset), extendedMaximumCapacityBytes);
            return AddStructure(formatted, []);
        }

        public SmbiosTableBuilder AddType17(
            ushort size,
            uint extendedSizeMiB,
            byte memoryType,
            ushort speed,
            ushort configuredSpeed,
            uint extendedSpeed,
            uint extendedConfiguredSpeed,
            string? deviceLocator,
            string? bankLocator,
            byte formattedLength = Type17LengthV33)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(formattedLength, MinimumStructureLength);

            var strings = new List<string>(capacity: 2);
            byte[] formatted = NewStructure(MemoryDeviceType, formattedLength, Math.Max(formattedLength, Type17LengthV33));
            ushort width = size == SizeNotInstalled ? UnknownWidth : PopulatedWidthBits;
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceArrayHandleOffset), _arrayHandle);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceErrorInformationHandleOffset), NoHandle);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceTotalWidthOffset), width);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceDataWidthOffset), width);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceSizeOffset), size);
            formatted[DeviceFormFactorOffset] = FormFactorDimm;
            formatted[DeviceLocatorOffset] = AddString(strings, deviceLocator, nameof(deviceLocator));
            formatted[DeviceBankLocatorOffset] = AddString(strings, bankLocator, nameof(bankLocator));
            formatted[DeviceMemoryTypeOffset] = memoryType;
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceSpeedOffset), speed);
            BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(DeviceExtendedSizeOffset), extendedSizeMiB);
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(DeviceConfiguredSpeedOffset), configuredSpeed);
            BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(DeviceExtendedSpeedOffset), extendedSpeed);
            BinaryPrimitives.WriteUInt32LittleEndian(formatted.AsSpan(DeviceExtendedConfiguredSpeedOffset), extendedConfiguredSpeed);
            return AddStructure(formatted.AsSpan(0, formattedLength), strings);
        }

        // A populated 16 GiB module whose rated and configured speeds agree.
        public SmbiosTableBuilder AddDimm(
            string? deviceLocator,
            string? bankLocator,
            byte memoryType = TypeCodeDdr4,
            ushort configuredSpeed = Ddr4SpeedMTs,
            byte formattedLength = Type17LengthV33) =>
            AddType17(SixteenGiBInMiB, 0, memoryType, configuredSpeed, configuredSpeed, 0, 0, deviceLocator, bankLocator, formattedLength);

        public SmbiosTableBuilder AddRaw(byte[] bytes)
        {
            _structures.AddRange(bytes);
            return this;
        }

        public SmbiosTableBuilder AddEnd()
        {
            byte[] formatted = [EndOfTableType, MinimumStructureLength, 0, 0];
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(HandleOffset), EndOfTableHandle);
            return AddStructure(formatted, []);
        }

        // The structure table alone, without the RawSMBIOSData header.
        public byte[] BuildStructures() => [.. _structures];

        public byte[] Build(byte major = 3, byte minor = 3, uint? lengthOverride = null)
        {
            byte[] structures = BuildStructures();
            byte[] table = new byte[RawHeaderLength + structures.Length];

            // Used20CallingMethod and DmiRevision stay 0, as in the fixtures.
            table[1] = major;
            table[2] = minor;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(RawHeaderTableLengthOffset), lengthOverride ?? (uint)structures.Length);
            structures.CopyTo(table, RawHeaderLength);
            return table;
        }

        // Allocates the formatted area with its Type, Length and the next handle written; the
        // area may be larger than the length so that fields beyond it can be written and dropped.
        private byte[] NewStructure(byte type, byte formattedLength, int allocatedLength)
        {
            byte[] formatted = new byte[allocatedLength];
            formatted[0] = type;
            formatted[1] = formattedLength;
            BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(HandleOffset), _nextHandle++);
            return formatted;
        }

        private static byte AddString(List<string> strings, string? value, string parameterName)
        {
            if (value is null)
            {
                return 0;
            }

            // An empty string or an embedded NUL would end the string set early.
            if (value.Length == 0 || value.Contains('\0'))
            {
                throw new ArgumentException("An SMBIOS string is non-empty and holds no NUL; pass null for an absent string.", parameterName);
            }

            strings.Add(value);
            return (byte)strings.Count;
        }

        // Appends the formatted area and its string set: each string NUL-terminated, the set ended
        // by one more NUL, and an empty set written as a double NUL.
        private SmbiosTableBuilder AddStructure(ReadOnlySpan<byte> formatted, List<string> strings)
        {
            _structures.AddRange(formatted);
            if (strings.Count == 0)
            {
                _structures.Add(0);
            }

            foreach (string value in strings)
            {
                _structures.AddRange(Encoding.Latin1.GetBytes(value));
                _structures.Add(0);
            }

            _structures.Add(0);
            return this;
        }
    }
}
