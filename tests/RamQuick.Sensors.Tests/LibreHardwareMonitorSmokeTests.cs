using System.Reflection;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RamQuick.Sensors.Tests;

/// <summary>
/// Smoke test of <see cref="LibreHardwareMonitorSensorSource"/> against the real sensor library, the
/// only test that reads real CPU and DIMM temperature sensors.
/// </summary>
/// <remarks>
/// It runs wherever its preconditions hold and reports Skipped elsewhere: an elevated process, a PawnIO
/// driver the user installed, and at least one CPU or DIMM temperature sensor. It never installs, starts
/// or prompts for a driver or service and never writes the registry; a missing driver is detected only
/// through the status the source reports. The observed readings go to the test output, so a run on the
/// validation PC records which sensors read. Select it with the trait filter <c>Requires=Hardware</c>.
/// </remarks>
[Trait("Requires", "Hardware")]
public sealed class LibreHardwareMonitorSmokeTests
{
    // Wide windows: they catch a unit or sensor-selection mistake, such as a raw register value or a
    // zero placeholder, not a hot machine.
    private const double MinPlausibleCelsius = 5.0;
    private const double MaxPlausibleCpuCelsius = 110.0;
    private const double MaxPlausibleRamCelsius = 100.0;

    private static readonly TimeSpan _secondReadDelay = TimeSpan.FromSeconds(1);

    [Fact]
    public void OpenAndRead_ElevatedWithPawnIo_ReturnsPlausibleTemperatures()
    {
        Assert.SkipUnless(ProcessElevation.IsElevated(), "Requires an elevated process (Administrator).");

        // Open, every Read and Dispose stay on this one thread, as the ISensorSource contract requires.
        using var source = new LibreHardwareMonitorSensorSource();
        source.Open();
        SensorReadings first = source.Read();
        Report("First read", first);

        Assert.SkipWhen(
            first.Cpu.Status == SensorStatus.DriverUnavailable && first.Ram.Status == SensorStatus.DriverUnavailable,
            "PawnIO driver not installed.");
        Assert.SkipWhen(!first.Cpu.IsAvailable && !first.Ram.IsAvailable, "No temperature sensor reads on this machine.");
        AssertPlausible(first);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        _ = cancellationToken.WaitHandle.WaitOne(_secondReadDelay);
        cancellationToken.ThrowIfCancellationRequested();

        SensorReadings second = source.Read();
        Report("Second read", second);

        AssertPlausible(second);
        Assert.True(
            second.Cpu.CapturedAt >= first.Cpu.CapturedAt,
            $"The second CPU reading was captured at {second.Cpu.CapturedAt:O}, before the first at {first.Cpu.CapturedAt:O}.");
        Assert.True(
            second.Ram.CapturedAt >= first.Ram.CapturedAt,
            $"The second RAM reading was captured at {second.Ram.CapturedAt:O}, before the first at {first.Ram.CapturedAt:O}.");
    }

    private static void AssertPlausible(SensorReadings readings)
    {
        if (readings.Cpu.Celsius is double cpuCelsius)
        {
            Assert.InRange(cpuCelsius, MinPlausibleCelsius, MaxPlausibleCpuCelsius);
        }

        if (readings.Ram.Celsius is double ramCelsius)
        {
            Assert.InRange(ramCelsius, MinPlausibleCelsius, MaxPlausibleRamCelsius);
        }
    }

    private static void Report(string label, SensorReadings readings) =>
        TestContext.Current.TestOutputHelper?.WriteLine($"{label}: CPU {Describe(readings.Cpu)}; RAM {Describe(readings.Ram)}");

    private static string Describe(SensorValue value) =>
        value.Celsius is double celsius
            ? FormattableString.Invariant($"{celsius:F1} °C ({value.Status}, captured {value.CapturedAt:O})")
            : FormattableString.Invariant($"N/A ({value.Status}, captured {value.CapturedAt:O})");
}

/// <summary>
/// Hardware-free tests of <see cref="LibreHardwareMonitorSensorSource"/>: the access decision, the CPU
/// and DIMM sensor selection, the flattening of the library's hardware tree, and the paths that never
/// reach the driver (a non-elevated process, a disposed source).
/// </summary>
/// <remarks>
/// The hardware tree is built from <see cref="IHardware"/> and <see cref="ISensor"/> doubles, so the
/// selection and flattening logic is exercised on machines without elevation or PawnIO.
/// </remarks>
public sealed class LibreHardwareMonitorSelectionTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false, SensorStatus.DriverUnavailable)]
    [InlineData(false, true, SensorStatus.DriverUnavailable)]
    [InlineData(true, false, SensorStatus.DriverUnavailable)]
    [InlineData(true, true, null)]
    public void ResolveAccess_ElevationAndDriver_GrantsOnlyWhenBothPresent(bool isElevated, bool driverInstalled, SensorStatus? expected)
    {
        Assert.Equal(expected, LibreHardwareMonitorSensorSource.ResolveAccess(isElevated, driverInstalled));
    }

    [Fact]
    public void SelectCpu_IntelPackageAndCoreSensors_ReturnsPackageTemperature()
    {
        SensorValue cpu = LibreHardwareMonitorSensorSource.SelectCpu(
            [CpuTemperature("CPU Core #1", 58.0f), CpuTemperature("CPU Package", 61.0f), CpuTemperature("Core Max", 64.0f)],
            _now);

        Assert.Equal(Available(61.0), cpu);
    }

    [Fact]
    public void SelectCpu_AmdTctlTdieAndCcdSensors_ReturnsTctlTdieTemperature()
    {
        SensorValue cpu = LibreHardwareMonitorSensorSource.SelectCpu(
            [CpuTemperature("Core (Tctl/Tdie)", 72.5f), CpuTemperature("CCD1 (Tdie)", 75.0f), CpuTemperature("CCDs Max (Tdie)", 76.0f)],
            _now);

        Assert.Equal(Available(72.5), cpu);
    }

    [Fact]
    public void SelectCpu_SeveralPackages_ReturnsHottestFiniteValue()
    {
        SensorValue cpu = LibreHardwareMonitorSensorSource.SelectCpu(
            [
                CpuTemperature("CPU Package", 61.0f),
                CpuTemperature("CPU Package", null),
                CpuTemperature("CPU Package", 66.0f),
                CpuTemperature("CPU Package", float.NaN),
            ],
            _now);

        Assert.Equal(Available(66.0), cpu);
    }

    // The first preferred name present decides, even when it holds no value and the other one does.
    [Theory]
    [InlineData(61.0f, SensorStatus.Available, 61.0)]
    [InlineData(null, SensorStatus.Failed, null)]
    public void SelectCpu_PackageAndTctlTdieBothPresent_PackageDecides(
        float? packageCelsius,
        SensorStatus expectedStatus,
        double? expectedCelsius)
    {
        SensorValue cpu = LibreHardwareMonitorSensorSource.SelectCpu(
            [CpuTemperature("Core (Tctl/Tdie)", 90.0f), CpuTemperature("CPU Package", packageCelsius)],
            _now);

        Assert.Equal(Expected(expectedStatus, expectedCelsius), cpu);
    }

    [Theory]
    [InlineData(HardwareType.Cpu, SensorType.Power, "CPU Package")]
    [InlineData(HardwareType.Cpu, SensorType.Load, "Core (Tctl/Tdie)")]
    [InlineData(HardwareType.Motherboard, SensorType.Temperature, "CPU Package")]
    [InlineData(HardwareType.Memory, SensorType.Temperature, "Core (Tctl/Tdie)")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "CPU Core #1")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "CCD1 (Tdie)")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "cpu package")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "CPU Package ")]
    public void SelectCpu_SensorOtherThanCpuPackageOrTctlTdieTemperature_IsIgnored(
        HardwareType hardwareType,
        SensorType sensorType,
        string sensorName)
    {
        var ignored = new SensorSample(hardwareType, sensorName, sensorType, 99.0f);

        Assert.Equal(Unavailable(SensorStatus.NoSensor), LibreHardwareMonitorSensorSource.SelectCpu([ignored], _now));
        Assert.Equal(
            Available(61.0),
            LibreHardwareMonitorSensorSource.SelectCpu([ignored, CpuTemperature("CPU Package", 61.0f)], _now));
    }

    [Fact]
    public void SelectRam_SeveralDimms_ReturnsMaximumAcrossDimms()
    {
        SensorValue ram = LibreHardwareMonitorSensorSource.SelectRam(
            [
                DimmTemperature("DIMM #1", 41.5f),
                new SensorSample(HardwareType.Memory, "Memory Used", SensorType.Data, 12.3f),
                DimmTemperature("DIMM #3", 47.0f),
            ],
            _now);

        Assert.Equal(Available(47.0), ram);
    }

    [Fact]
    public void SelectRam_SomeDimmsWithoutFiniteValue_ReturnsMaximumOfTheOthers()
    {
        SensorValue ram = LibreHardwareMonitorSensorSource.SelectRam(
            [
                DimmTemperature("DIMM #1", null),
                DimmTemperature("DIMM #2", 44.0f),
                DimmTemperature("DIMM #3", float.PositiveInfinity),
                DimmTemperature("DIMM #4", 39.0f),
            ],
            _now);

        Assert.Equal(Available(44.0), ram);
    }

    // The usual case without SPD thermal sensors: only the library's generic memory hardware reports.
    [Fact]
    public void SelectRam_MemoryHardwareWithoutDimmSensors_ReportsNoSensor()
    {
        SensorValue ram = LibreHardwareMonitorSensorSource.SelectRam(
            [
                new SensorSample(HardwareType.Memory, "Memory Used", SensorType.Data, 12.3f),
                new SensorSample(HardwareType.Memory, "Memory Available", SensorType.Data, 19.7f),
                new SensorSample(HardwareType.Memory, "Memory", SensorType.Load, 38.4f),
            ],
            _now);

        Assert.Equal(Unavailable(SensorStatus.NoSensor), ram);
    }

    [Theory]
    [InlineData(HardwareType.Memory, SensorType.Data, "Memory Used")]
    [InlineData(HardwareType.Memory, SensorType.Load, "DIMM #1")]
    [InlineData(HardwareType.Motherboard, SensorType.Temperature, "DIMM #1")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "DIMM #1")]
    [InlineData(HardwareType.Memory, SensorType.Temperature, "DIMM #1 Resolution")]
    public void SelectRam_SensorOtherThanDimmTemperatureOfMemory_IsIgnored(
        HardwareType hardwareType,
        SensorType sensorType,
        string sensorName)
    {
        var ignored = new SensorSample(hardwareType, sensorName, sensorType, 99.0f);

        Assert.Equal(Unavailable(SensorStatus.NoSensor), LibreHardwareMonitorSensorSource.SelectRam([ignored], _now));
        Assert.Equal(
            Available(40.0),
            LibreHardwareMonitorSensorSource.SelectRam([ignored, DimmTemperature("DIMM #2", 40.0f)], _now));
    }

    [Theory]
    [InlineData("DIMM #0", true)]
    [InlineData("DIMM #1", true)]
    [InlineData("DIMM #12", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("DIMM #", false)]
    [InlineData("DIMM #1 Resolution", false)]
    [InlineData("DIMM #-1", false)]
    [InlineData("dimm #1", false)]
    [InlineData(" DIMM #1", false)]
    [InlineData("DIMM #1\n", false)]
    [InlineData("DIMM #\uFF11", false)]
    public void IsDimmSensorName_Name_MatchesOnlyDimmHashFollowedByAsciiDigits(string? sensorName, bool expected)
    {
        Assert.Equal(expected, LibreHardwareMonitorSensorSource.IsDimmSensorName(sensorName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Select_MatchingSensorsWithoutFiniteValue_ReportFailed(float? celsius)
    {
        SensorValue cpu = LibreHardwareMonitorSensorSource.SelectCpu([CpuTemperature("CPU Package", celsius)], _now);
        SensorValue ram = LibreHardwareMonitorSensorSource.SelectRam(
            [DimmTemperature("DIMM #1", celsius), DimmTemperature("DIMM #2", celsius)],
            _now);

        Assert.Equal(Unavailable(SensorStatus.Failed), cpu);
        Assert.Equal(Unavailable(SensorStatus.Failed), ram);
    }

    [Fact]
    public void Select_NoSamples_ReportNoSensor()
    {
        Assert.Equal(Unavailable(SensorStatus.NoSensor), LibreHardwareMonitorSensorSource.SelectCpu([], _now));
        Assert.Equal(Unavailable(SensorStatus.NoSensor), LibreHardwareMonitorSensorSource.SelectRam([], _now));
    }

    [Fact]
    public void Helpers_NullInput_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("samples", () => LibreHardwareMonitorSensorSource.SelectCpu(null!, _now));
        Assert.Throws<ArgumentNullException>("samples", () => LibreHardwareMonitorSensorSource.SelectRam(null!, _now));
        Assert.Throws<ArgumentNullException>("hardware", () => LibreHardwareMonitorSensorSource.UpdateAndCollect(null!));
    }

    [Fact]
    public void UpdateAndCollect_CpuAndMemoryHardware_UpdatesEachAndTagsSensorsWithTopLevelType()
    {
        HardwareDouble superIo = HardwareDouble.Create(
            HardwareType.SuperIO,
            [SensorDouble.Create("CPU Package", SensorType.Temperature, 63.0f)]);
        HardwareDouble cpu = HardwareDouble.Create(
            HardwareType.Cpu,
            [
                SensorDouble.Create("CPU Package", SensorType.Temperature, 61.0f),
                SensorDouble.Create("CPU Package", SensorType.Power, 45.0f),
            ],
            [superIo.Hardware]);
        HardwareDouble memory = HardwareDouble.Create(
            HardwareType.Memory,
            [SensorDouble.Create("DIMM #1", SensorType.Temperature, 41.5f)]);

        List<SensorSample> samples = LibreHardwareMonitorSensorSource.UpdateAndCollect([cpu.Hardware, memory.Hardware]);

        Assert.Equal(
            [
                new SensorSample(HardwareType.Cpu, "CPU Package", SensorType.Temperature, 61.0f),
                new SensorSample(HardwareType.Cpu, "CPU Package", SensorType.Power, 45.0f),
                new SensorSample(HardwareType.Cpu, "CPU Package", SensorType.Temperature, 63.0f),
                new SensorSample(HardwareType.Memory, "DIMM #1", SensorType.Temperature, 41.5f),
            ],
            samples);
        Assert.Equal(1, cpu.UpdateCount);
        Assert.Equal(1, superIo.UpdateCount);
        Assert.Equal(1, memory.UpdateCount);
    }

    [Theory]
    [InlineData(HardwareType.Motherboard)]
    [InlineData(HardwareType.GpuNvidia)]
    [InlineData(HardwareType.Storage)]
    [InlineData(HardwareType.Network)]
    public void UpdateAndCollect_OtherHardwareType_IsNeitherUpdatedNorRead(HardwareType hardwareType)
    {
        HardwareDouble other = HardwareDouble.Create(
            hardwareType,
            [SensorDouble.Create("CPU Package", SensorType.Temperature, 99.0f)],
            [HardwareDouble.Create(HardwareType.Cpu, [SensorDouble.Create("DIMM #1", SensorType.Temperature, 99.0f)]).Hardware]);

        List<SensorSample> samples = LibreHardwareMonitorSensorSource.UpdateAndCollect([other.Hardware]);

        Assert.Empty(samples);
        Assert.Equal(0, other.UpdateCount);
        Assert.Equal(0, other.ContentReadCount);
    }

    // A failed update must not resurface the previous update's value as a fresh temperature.
    [Fact]
    public void UpdateAndCollect_UpdateThrows_ThatHardwareReadsFailedWhileOthersRead()
    {
        HardwareDouble cpu = HardwareDouble.Create(
            HardwareType.Cpu,
            [SensorDouble.Create("CPU Package", SensorType.Temperature, 61.0f)],
            updateThrows: true);
        HardwareDouble memory = HardwareDouble.Create(
            HardwareType.Memory,
            [SensorDouble.Create("DIMM #1", SensorType.Temperature, 41.5f)]);

        List<SensorSample> samples = LibreHardwareMonitorSensorSource.UpdateAndCollect([cpu.Hardware, memory.Hardware]);

        Assert.Equal(new SensorSample(HardwareType.Cpu, "CPU Package", SensorType.Temperature, null), samples[0]);
        Assert.Equal(Unavailable(SensorStatus.Failed), LibreHardwareMonitorSensorSource.SelectCpu(samples, _now));
        Assert.Equal(Available(41.5), LibreHardwareMonitorSensorSource.SelectRam(samples, _now));
        Assert.Equal(1, cpu.UpdateCount);
    }

    [Fact]
    public void UpdateAndCollect_MissingCollectionsAndSensorName_AreTolerated()
    {
        HardwareDouble cpu = HardwareDouble.Create(HardwareType.Cpu, sensors: null, subHardware: null);
        HardwareDouble memory = HardwareDouble.Create(
            HardwareType.Memory,
            [SensorDouble.Create(null, SensorType.Temperature, 41.5f)]);

        List<SensorSample> samples = LibreHardwareMonitorSensorSource.UpdateAndCollect([cpu.Hardware, memory.Hardware]);

        Assert.Equal([new SensorSample(HardwareType.Memory, string.Empty, SensorType.Temperature, 41.5f)], samples);
        Assert.Equal(1, cpu.UpdateCount);
    }

    // Open checks elevation before any driver access, so this path touches no hardware.
    [Fact]
    public void OpenAndRead_NotElevated_ReportsDriverUnavailableWithoutThrowing()
    {
        Assert.SkipWhen(ProcessElevation.IsElevated(), "Covers the non-elevated path only.");

        using var source = new LibreHardwareMonitorSensorSource(new FakeTimeProvider(_now));
        source.Open();
        SensorReadings readings = source.Read();

        Assert.Equal(SensorReadings.Unavailable(SensorStatus.DriverUnavailable, _now), readings);
        Assert.Null(readings.Cpu.Celsius);
        Assert.Null(readings.Ram.Celsius);
    }

    [Fact]
    public void Dispose_WithoutOpen_DoesNotThrow()
    {
        var source = new LibreHardwareMonitorSensorSource(new FakeTimeProvider(_now));

        // Dispose is documented as idempotent, so the second call is part of the contract under test.
        Exception? exception = Record.Exception(() =>
        {
            source.Dispose();
            source.Dispose();
        });

        Assert.Null(exception);
    }

    // On an elevated machine an effective Open would report DriverUnavailable or real readings, so
    // Failed shows that Open did nothing; the capture time is the injected clock's at the Read.
    [Fact]
    public void Open_AfterDispose_HasNoEffectAndReadReportsFailed()
    {
        var time = new FakeTimeProvider(_now);
        var source = new LibreHardwareMonitorSensorSource(time);
        source.Dispose();
        time.Advance(TimeSpan.FromSeconds(2));

        source.Open();
        SensorReadings readings = source.Read();

        Assert.Equal(SensorReadings.Unavailable(SensorStatus.Failed, _now.AddSeconds(2)), readings);
    }

    private static SensorSample CpuTemperature(string sensorName, float? celsius) =>
        new(HardwareType.Cpu, sensorName, SensorType.Temperature, celsius);

    private static SensorSample DimmTemperature(string sensorName, float? celsius) =>
        new(HardwareType.Memory, sensorName, SensorType.Temperature, celsius);

    private static SensorValue Available(double celsius) => SensorValue.Available(celsius, _now);

    private static SensorValue Unavailable(SensorStatus status) => SensorValue.Unavailable(status, _now);

    private static SensorValue Expected(SensorStatus status, double? celsius) =>
        celsius is double value ? Available(value) : Unavailable(status);
}

/// <summary>
/// Whether the test process runs elevated: the precondition the smoke test and the non-elevated case
/// branch on.
/// </summary>
file static class ProcessElevation
{
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// An <see cref="IHardware"/> double: the type, sensors and sub-hardware the source reads, an update
/// that can be scripted to throw, and counts of what the source touched.
/// </summary>
/// <remarks>
/// Built on <see cref="DispatchProxy"/> so only the members the source uses need an answer; any other
/// member throws <see cref="NotSupportedException"/> and fails the test that reached it. Not sealed:
/// <see cref="DispatchProxy.Create{T, TProxy}"/> derives the runtime proxy type from this class.
/// </remarks>
file class HardwareDouble : DispatchProxy
{
    private HardwareType _hardwareType;
    private ISensor[]? _sensors;
    private IHardware[]? _subHardware;
    private bool _updateThrows;

    /// <summary>How many times <see cref="IHardware.Update"/> was called.</summary>
    public int UpdateCount { get; private set; }

    /// <summary>How many times <see cref="IHardware.Sensors"/> or <see cref="IHardware.SubHardware"/> was read.</summary>
    public int ContentReadCount { get; private set; }

    /// <summary>This double as the library interface the source consumes.</summary>
    public IHardware Hardware => (IHardware)(object)this;

    public static HardwareDouble Create(
        HardwareType hardwareType,
        ISensor[]? sensors,
        IHardware[]? subHardware = null,
        bool updateThrows = false)
    {
        var hardware = (HardwareDouble)(object)DispatchProxy.Create<IHardware, HardwareDouble>();
        hardware._hardwareType = hardwareType;
        hardware._sensors = sensors;
        hardware._subHardware = subHardware;
        hardware._updateThrows = updateThrows;
        return hardware;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "get_HardwareType":
                return _hardwareType;
            case "get_Sensors":
                ContentReadCount++;
                return _sensors;
            case "get_SubHardware":
                ContentReadCount++;
                return _subHardware;
            case "Update":
                UpdateCount++;
                if (_updateThrows)
                {
                    throw new InvalidOperationException("Scripted hardware update failure.");
                }

                return null;
            default:
                throw new NotSupportedException($"The sensor source is not expected to use IHardware.{targetMethod?.Name}.");
        }
    }
}

/// <summary>
/// An <see cref="ISensor"/> double answering the name, type and value the source reads.
/// </summary>
/// <remarks>
/// Any other member throws <see cref="NotSupportedException"/>. Not sealed:
/// <see cref="DispatchProxy.Create{T, TProxy}"/> derives the runtime proxy type from this class.
/// </remarks>
file class SensorDouble : DispatchProxy
{
    private string? _name;
    private SensorType _sensorType;
    private float? _value;

    public static ISensor Create(string? name, SensorType sensorType, float? value)
    {
        ISensor sensor = DispatchProxy.Create<ISensor, SensorDouble>();
        var state = (SensorDouble)(object)sensor;
        state._name = name;
        state._sensorType = sensorType;
        state._value = value;
        return sensor;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name switch
        {
            "get_Name" => _name,
            "get_SensorType" => _sensorType,
            "get_Value" => _value,
            _ => throw new NotSupportedException($"The sensor source is not expected to use ISensor.{targetMethod?.Name}."),
        };
}

