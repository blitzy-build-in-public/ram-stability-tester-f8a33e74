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
/// driver the user installed, and at least one CPU or DIMM temperature sensor. Only those missing
/// prerequisites skip it; a Failed, Stale or Hung reading, or a sensor that stops reading on the second
/// read, fails it, by the verdicts <see cref="LibreHardwareMonitorSmokeCheckTests"/> pins down without
/// hardware. It never installs, starts or prompts for a driver or service and never writes the registry;
/// a missing driver is detected only through the status the source reports. The observed readings go to
/// the test output, so a run on the validation PC records which sensors read. Select it with the trait
/// filter <c>Requires=Hardware</c>.
/// </remarks>
[Trait("Requires", "Hardware")]
public sealed class LibreHardwareMonitorSmokeTests
{
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

        if (SmokeReadingsCheck.SkipReason(first) is { } skipReason)
        {
            Assert.Skip(skipReason);
        }

        AssertNoProblems("First read", SmokeReadingsCheck.FirstReadProblems(first));

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        _ = cancellationToken.WaitHandle.WaitOne(_secondReadDelay);
        cancellationToken.ThrowIfCancellationRequested();

        SensorReadings second = source.Read();
        Report("Second read", second);

        AssertNoProblems("Second read", SmokeReadingsCheck.SecondReadProblems(first, second));
    }

    private static void AssertNoProblems(string label, IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail($"{label}: {string.Join(" ", problems)}");
        }
    }

    private static void Report(string label, SensorReadings readings) =>
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{label}: CPU {SmokeReadingsCheck.Describe(readings.Cpu)}; RAM {SmokeReadingsCheck.Describe(readings.Ram)}");
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

    // A high reading must reach the thermal limits unchanged, so the sample enters through a library
    // sensor double and UpdateAndCollect: a drop or clamp anywhere between ISensor.Value and the verdict
    // fails the exact-value assertions. The trip is stated as data rather than recomputed from the
    // limit, so a threshold mistake cannot hide behind the comparison under test. 94.9f widens to about
    // 94.9000015 and 94.99999f is the largest float below 95 (one ulp, about 7.6e-6, under it), so the
    // inclusive limit holds at the sensor's own resolution; float.MaxValue is the hottest finite reading,
    // the one any plausibility ceiling would cut.
    [Theory]
    [InlineData("CPU Package", 94.9f, false)]
    [InlineData("CPU Package", 94.99999f, false)]
    [InlineData("CPU Package", 95.0f, true)]
    [InlineData("CPU Package", 95.5f, true)]
    [InlineData("CPU Package", 125.0f, true)]
    [InlineData("CPU Package", float.MaxValue, true)]
    [InlineData("Core (Tctl/Tdie)", 94.9f, false)]
    [InlineData("Core (Tctl/Tdie)", 94.99999f, false)]
    [InlineData("Core (Tctl/Tdie)", 95.0f, true)]
    [InlineData("Core (Tctl/Tdie)", 95.5f, true)]
    [InlineData("Core (Tctl/Tdie)", 125.0f, true)]
    [InlineData("Core (Tctl/Tdie)", float.MaxValue, true)]
    public void SelectCpu_PreferredSensorNearOrAboveThermalLimit_PassesValueUnchangedToThermalLimits(
        string sensorName,
        float celsius,
        bool expectedTrip)
    {
        HardwareDouble cpu = HardwareDouble.Create(
            HardwareType.Cpu,
            [SensorDouble.Create(sensorName, SensorType.Temperature, celsius)]);

        SensorValue selected = LibreHardwareMonitorSensorSource.SelectCpu(
            LibreHardwareMonitorSensorSource.UpdateAndCollect([cpu.Hardware]),
            _now);

        ThermalVerdict? expectedVerdict = expectedTrip
            ? new ThermalVerdict(ThermalSensor.Cpu, celsius, ThermalLimits.CpuLimitCelsius)
            : null;
        Assert.Equal(Available(celsius), selected);
        Assert.Equal(expectedVerdict, ThermalLimits.Evaluate(new SensorReadings(selected, Unavailable(SensorStatus.NoSensor))));
    }

    // The RAM mirror of the CPU boundary cases at 85 °C, with 84.99999f the largest float below 85. The
    // hot DIMM sits between cooler ones, so the maximum must keep it wherever it falls in update order.
    [Theory]
    [InlineData(84.9f, false)]
    [InlineData(84.99999f, false)]
    [InlineData(85.0f, true)]
    [InlineData(85.5f, true)]
    [InlineData(120.0f, true)]
    [InlineData(float.MaxValue, true)]
    public void SelectRam_HottestDimmNearOrAboveThermalLimit_PassesValueUnchangedToThermalLimits(
        float celsius,
        bool expectedTrip)
    {
        HardwareDouble memory = HardwareDouble.Create(
            HardwareType.Memory,
            [
                SensorDouble.Create("DIMM #1", SensorType.Temperature, 40.0f),
                SensorDouble.Create("DIMM #2", SensorType.Temperature, celsius),
                SensorDouble.Create("DIMM #3", SensorType.Temperature, 45.0f),
            ]);

        SensorValue selected = LibreHardwareMonitorSensorSource.SelectRam(
            LibreHardwareMonitorSensorSource.UpdateAndCollect([memory.Hardware]),
            _now);

        ThermalVerdict? expectedVerdict = expectedTrip
            ? new ThermalVerdict(ThermalSensor.Ram, celsius, ThermalLimits.RamLimitCelsius)
            : null;
        Assert.Equal(Available(celsius), selected);
        Assert.Equal(expectedVerdict, ThermalLimits.Evaluate(new SensorReadings(Unavailable(SensorStatus.NoSensor), selected)));
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

    // The access check answers with the production decision for a non-elevated process on a machine
    // with PawnIO, so elevation alone denies access, and the test host's own elevation does not decide
    // whether this runs. Open checks access before any driver access, so the library is never created.
    [Fact]
    public void OpenAndRead_NotElevated_ReportsDriverUnavailableWithoutThrowing()
    {
        var spy = new InitializationSpy(
            LibreHardwareMonitorSensorSource.ResolveAccess(isElevated: false, driverInstalled: true),
            accessCheckThrows: false);
        using LibreHardwareMonitorSensorSource source = spy.CreateSource(new FakeTimeProvider(_now));
        SensorReadings? readings = null;

        Exception? exception = Record.Exception(() =>
        {
            source.Open();
            readings = source.Read();
        });

        Assert.Null(exception);
        Assert.Equal(1, spy.AccessCheckCount);
        Assert.Equal(0, spy.CreateCount);
        Assert.Equal(SensorReadings.Unavailable(SensorStatus.DriverUnavailable, _now), readings);
        Assert.Null(readings!.Cpu.Celsius);
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

    // The spy proves Open made no initialization attempt: neither the access check nor the library
    // construction ran. The Failed read alone could not show it, because a construction or library Open
    // that throws reports Failed too. The capture time is the injected clock's at the Read.
    [Fact]
    public void Open_AfterDispose_HasNoEffectAndReadReportsFailed()
    {
        var time = new FakeTimeProvider(_now);
        var spy = new InitializationSpy(accessAnswer: null, accessCheckThrows: false);
        LibreHardwareMonitorSensorSource source = spy.CreateSource(time);
        source.Dispose();
        time.Advance(TimeSpan.FromSeconds(2));

        source.Open();
        SensorReadings readings = source.Read();

        Assert.Equal(0, spy.AccessCheckCount);
        Assert.Equal(0, spy.CreateCount);
        Assert.Equal(SensorReadings.Unavailable(SensorStatus.Failed, _now.AddSeconds(2)), readings);
    }

    // The positive control for the after-Dispose case: the same spy counts the attempts of an Open on a
    // live source, so its zero counts there are not vacuous. Granted access reaches the factory, whose
    // failure reads as Failed: the ambiguity the spy resolves. A check that denies, throws or answers
    // Available never reaches the factory. Only the first Open has an effect.
    [Theory]
    [InlineData(null, false, 1, SensorStatus.Failed)]
    [InlineData(SensorStatus.DriverUnavailable, false, 0, SensorStatus.DriverUnavailable)]
    [InlineData(null, true, 0, SensorStatus.DriverUnavailable)]
    [InlineData(SensorStatus.Available, false, 0, SensorStatus.DriverUnavailable)]
    public void Open_AccessCheckOutcome_ChecksOnceAndCreatesComputerOnlyWhenGranted(
        SensorStatus? accessAnswer,
        bool accessCheckThrows,
        int expectedCreateCount,
        SensorStatus expectedStatus)
    {
        var spy = new InitializationSpy(accessAnswer, accessCheckThrows);
        using LibreHardwareMonitorSensorSource source = spy.CreateSource(new FakeTimeProvider(_now));

        source.Open();
        Assert.Equal(1, spy.AccessCheckCount);
        Assert.Equal(expectedCreateCount, spy.CreateCount);

        source.Open();
        SensorReadings readings = source.Read();

        Assert.Equal(1, spy.AccessCheckCount);
        Assert.Equal(expectedCreateCount, spy.CreateCount);
        Assert.Equal(SensorReadings.Unavailable(expectedStatus, _now), readings);
    }

    [Fact]
    public void Constructor_NullInitializationDelegate_ThrowsArgumentNullException()
    {
        var spy = new InitializationSpy(accessAnswer: null, accessCheckThrows: false);

        Assert.Throws<ArgumentNullException>("checkAccess", () => new LibreHardwareMonitorSensorSource(null, null!, spy.CreateComputer));
        Assert.Throws<ArgumentNullException>("createComputer", () => new LibreHardwareMonitorSensorSource(null, spy.CheckAccess, null!));
    }

    /// <summary>
    /// Counts the initialization attempts of a source's Open: its access checks and its library
    /// constructions. The construction always throws, so no test creates a real <see cref="Computer"/>
    /// or reaches the driver.
    /// </summary>
    private sealed class InitializationSpy
    {
        private readonly SensorStatus? _accessAnswer;
        private readonly bool _accessCheckThrows;

        public InitializationSpy(SensorStatus? accessAnswer, bool accessCheckThrows)
        {
            _accessAnswer = accessAnswer;
            _accessCheckThrows = accessCheckThrows;
        }

        public int AccessCheckCount { get; private set; }

        public int CreateCount { get; private set; }

        public LibreHardwareMonitorSensorSource CreateSource(TimeProvider timeProvider) =>
            new(timeProvider, CheckAccess, CreateComputer);

        public SensorStatus? CheckAccess()
        {
            AccessCheckCount++;
            return _accessCheckThrows ? throw new InvalidOperationException("Scripted access-check failure.") : _accessAnswer;
        }

        public Computer CreateComputer()
        {
            CreateCount++;
            throw new InvalidOperationException("Scripted library construction failure.");
        }
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
/// The smoke test's verdicts on the real source's readings: which first read shows a missing
/// prerequisite and skips, and which readings break the source's contract and fail the test.
/// </summary>
/// <remarks>
/// Pure, so <see cref="LibreHardwareMonitorSmokeCheckTests"/> proves on any machine, without a driver,
/// that a failing library can neither pass for missing hardware nor pass a read.
/// </remarks>
file static class SmokeReadingsCheck
{
    // Wide windows: they catch a unit or sensor-selection mistake, such as a raw register value or a
    // zero placeholder, not a hot machine.
    public const double MinPlausibleCelsius = 5.0;
    public const double MaxPlausibleCpuCelsius = 110.0;
    public const double MaxPlausibleRamCelsius = 100.0;

    /// <summary>
    /// The reason to skip when both readings of the first read show the same missing prerequisite;
    /// otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The source reports DriverUnavailable for both readings when PawnIO is absent, and NoSensor where
    /// access works but the machine has no matching sensor. Failed, Stale and Hung come from a failing
    /// library or adapter, and a lone DriverUnavailable contradicts the source's all-or-nothing access
    /// check, so none of them may pass for missing hardware.
    /// </remarks>
    public static string? SkipReason(SensorReadings first) =>
        (first.Cpu.Status, first.Ram.Status) switch
        {
            (SensorStatus.DriverUnavailable, SensorStatus.DriverUnavailable) => "PawnIO driver not installed.",
            (SensorStatus.NoSensor, SensorStatus.NoSensor) => "No temperature sensor reads on this machine.",
            _ => null,
        };

    /// <summary>
    /// The problems of a first read that <see cref="SkipReason"/> did not skip: each reading must be
    /// Available with a plausible temperature, or NoSensor.
    /// </summary>
    public static IReadOnlyList<string> FirstReadProblems(SensorReadings first)
    {
        var problems = new List<string>();
        AddReadingProblems(problems, "CPU", first.Cpu, MaxPlausibleCpuCelsius);
        AddReadingProblems(problems, "RAM", first.Ram, MaxPlausibleRamCelsius);
        return problems;
    }

    /// <summary>
    /// The problems of the second read: a reading Available in the first read must still be Available
    /// and plausible, any other must be Available and plausible or NoSensor, and no reading may be
    /// captured before the first read's.
    /// </summary>
    public static IReadOnlyList<string> SecondReadProblems(SensorReadings first, SensorReadings second)
    {
        var problems = new List<string>();
        AddSecondReadProblems(problems, "CPU", first.Cpu, second.Cpu, MaxPlausibleCpuCelsius);
        AddSecondReadProblems(problems, "RAM", first.Ram, second.Ram, MaxPlausibleRamCelsius);
        return problems;
    }

    /// <summary>
    /// The temperature or N/A, the status and the capture time of a reading, for the test output and
    /// for problem messages.
    /// </summary>
    public static string Describe(SensorValue value) =>
        value.Celsius is double celsius
            ? FormattableString.Invariant($"{celsius:F1} °C ({value.Status}, captured {value.CapturedAt:O})")
            : FormattableString.Invariant($"N/A ({value.Status}, captured {value.CapturedAt:O})");

    private static void AddReadingProblems(List<string> problems, string sensor, SensorValue value, double maxPlausibleCelsius)
    {
        // Only an Available value carries a temperature.
        if (value.Celsius is double celsius)
        {
            if (celsius < MinPlausibleCelsius || celsius > maxPlausibleCelsius)
            {
                problems.Add(FormattableString.Invariant(
                    $"{sensor} reading {Describe(value)} is outside the plausible range {MinPlausibleCelsius:F1} to {maxPlausibleCelsius:F1} °C."));
            }
        }
        else if (value.Status != SensorStatus.NoSensor)
        {
            problems.Add($"{sensor} reading {Describe(value)} is neither Available nor NoSensor.");
        }
    }

    private static void AddSecondReadProblems(
        List<string> problems,
        string sensor,
        SensorValue first,
        SensorValue second,
        double maxPlausibleCelsius)
    {
        // A sensor that read once and then stops is a library or adapter failure, NoSensor included:
        // the hardware has not gone anywhere between two reads a second apart.
        if (first.IsAvailable && !second.IsAvailable)
        {
            problems.Add($"{sensor} reading {Describe(second)} is no longer Available after the first read's {Describe(first)}.");
        }
        else
        {
            AddReadingProblems(problems, sensor, second, maxPlausibleCelsius);
        }

        if (second.CapturedAt < first.CapturedAt)
        {
            problems.Add(FormattableString.Invariant(
                $"{sensor} reading {Describe(second)} was captured before the first read's {first.CapturedAt:O}."));
        }
    }
}

/// <summary>
/// Hardware-free tests of the smoke test's verdicts on controlled readings: only a missing prerequisite
/// skips, and a failing library fails the smoke test rather than skipping or passing it.
/// </summary>
/// <remarks>
/// Untagged, unlike <see cref="LibreHardwareMonitorSmokeTests"/>, so these run on every machine,
/// including those without elevation or PawnIO where the smoke test itself skips.
/// </remarks>
public sealed class LibreHardwareMonitorSmokeCheckTests
{
    private static readonly DateTimeOffset _firstReadAt = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _secondReadAt = _firstReadAt.AddSeconds(1);

    [Theory]
    [InlineData(SensorStatus.DriverUnavailable, "PawnIO driver not installed.")]
    [InlineData(SensorStatus.NoSensor, "No temperature sensor reads on this machine.")]
    public void SkipReason_BothReadingsShowMissingPrerequisite_ReturnsThatPrerequisite(SensorStatus status, string expectedReason)
    {
        Assert.Equal(expectedReason, SmokeReadingsCheck.SkipReason(SensorReadings.Unavailable(status, _firstReadAt)));
    }

    // Failed on both sensors is what a library that fails to open or update returns; it must fail the
    // smoke test, not skip it as missing hardware.
    [Theory]
    [InlineData(SensorStatus.Failed, null, SensorStatus.Failed, null, true, true)]
    [InlineData(SensorStatus.Stale, null, SensorStatus.Stale, null, true, true)]
    [InlineData(SensorStatus.Hung, null, SensorStatus.Hung, null, true, true)]
    [InlineData(SensorStatus.DriverUnavailable, null, SensorStatus.Failed, null, true, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Failed, null, false, true)]
    [InlineData(SensorStatus.Failed, null, SensorStatus.Available, 40.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Stale, null, false, true)]
    [InlineData(SensorStatus.Hung, null, SensorStatus.Available, 40.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.DriverUnavailable, null, false, true)]
    [InlineData(SensorStatus.DriverUnavailable, null, SensorStatus.NoSensor, null, true, false)]
    [InlineData(SensorStatus.NoSensor, null, SensorStatus.Failed, null, false, true)]
    [InlineData(SensorStatus.Available, 4.9, SensorStatus.NoSensor, null, true, false)]
    [InlineData(SensorStatus.Available, 110.1, SensorStatus.Available, 40.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 4.9, false, true)]
    [InlineData(SensorStatus.NoSensor, null, SensorStatus.Available, 100.1, false, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.NoSensor, null, false, false)]
    [InlineData(SensorStatus.NoSensor, null, SensorStatus.Available, 40.0, false, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, false, false)]
    [InlineData(SensorStatus.Available, 5.0, SensorStatus.Available, 100.0, false, false)]
    [InlineData(SensorStatus.Available, 110.0, SensorStatus.Available, 5.0, false, false)]
    public void FirstRead_NoMissingPrerequisite_IsNotSkippedAndFlagsEachUnexpectedReading(
        SensorStatus cpuStatus,
        double? cpuCelsius,
        SensorStatus ramStatus,
        double? ramCelsius,
        bool cpuProblemExpected,
        bool ramProblemExpected)
    {
        var first = new SensorReadings(Reading(cpuStatus, cpuCelsius, _firstReadAt), Reading(ramStatus, ramCelsius, _firstReadAt));

        Assert.Null(SmokeReadingsCheck.SkipReason(first));
        AssertProblems(SmokeReadingsCheck.FirstReadProblems(first), first, cpuProblemExpected, ramProblemExpected);
    }

    // The first case is the one that used to pass: both sensors stop reading after a good first read.
    [Theory]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Failed, null, SensorStatus.Failed, null, true, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Failed, null, SensorStatus.Available, 41.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Available, 51.0, SensorStatus.Failed, null, false, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Stale, null, SensorStatus.Hung, null, true, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.NoSensor, null, SensorStatus.Available, 41.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Available, 51.0, SensorStatus.DriverUnavailable, null, false, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Available, 110.1, SensorStatus.Available, 41.0, true, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Available, 51.0, SensorStatus.Available, 4.9, false, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.NoSensor, null, SensorStatus.Available, 51.0, SensorStatus.Failed, null, false, true)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.Available, 40.0, SensorStatus.Available, 51.0, SensorStatus.Available, 41.0, false, false)]
    [InlineData(SensorStatus.Available, 50.0, SensorStatus.NoSensor, null, SensorStatus.Available, 51.0, SensorStatus.NoSensor, null, false, false)]
    [InlineData(SensorStatus.NoSensor, null, SensorStatus.Available, 40.0, SensorStatus.NoSensor, null, SensorStatus.Available, 41.0, false, false)]
    [InlineData(SensorStatus.NoSensor, null, SensorStatus.Available, 40.0, SensorStatus.Available, 52.0, SensorStatus.Available, 41.0, false, false)]
    public void SecondRead_AfterAcceptedFirstRead_FlagsEachReadingThatStoppedOrIsImplausible(
        SensorStatus firstCpuStatus,
        double? firstCpuCelsius,
        SensorStatus firstRamStatus,
        double? firstRamCelsius,
        SensorStatus secondCpuStatus,
        double? secondCpuCelsius,
        SensorStatus secondRamStatus,
        double? secondRamCelsius,
        bool cpuProblemExpected,
        bool ramProblemExpected)
    {
        var first = new SensorReadings(
            Reading(firstCpuStatus, firstCpuCelsius, _firstReadAt),
            Reading(firstRamStatus, firstRamCelsius, _firstReadAt));
        var second = new SensorReadings(
            Reading(secondCpuStatus, secondCpuCelsius, _secondReadAt),
            Reading(secondRamStatus, secondRamCelsius, _secondReadAt));

        AssertProblems(SmokeReadingsCheck.SecondReadProblems(first, second), second, cpuProblemExpected, ramProblemExpected);
    }

    // An equal capture time is accepted: a coarse clock can stamp two reads a second apart alike.
    [Theory]
    [InlineData(-1, 1000, true, false)]
    [InlineData(1000, -1, false, true)]
    [InlineData(-1000, -1000, true, true)]
    [InlineData(0, 0, false, false)]
    public void SecondRead_CaptureTimeRelativeToFirstRead_FlagsEachEarlierReading(
        int cpuOffsetMilliseconds,
        int ramOffsetMilliseconds,
        bool cpuProblemExpected,
        bool ramProblemExpected)
    {
        var first = new SensorReadings(SensorValue.Available(50.0, _firstReadAt), SensorValue.Available(40.0, _firstReadAt));
        var second = new SensorReadings(
            SensorValue.Available(51.0, _firstReadAt.AddMilliseconds(cpuOffsetMilliseconds)),
            SensorValue.Available(41.0, _firstReadAt.AddMilliseconds(ramOffsetMilliseconds)));

        IReadOnlyList<string> problems = SmokeReadingsCheck.SecondReadProblems(first, second);

        AssertProblems(problems, second, cpuProblemExpected, ramProblemExpected);
        Assert.All(problems, problem => Assert.Contains(FormattableString.Invariant($"{_firstReadAt:O}"), problem, StringComparison.Ordinal));
    }

    private static SensorValue Reading(SensorStatus status, double? celsius, DateTimeOffset capturedAt)
    {
        // A temperature exactly when Available, as SensorValue holds; a case scripting otherwise is malformed.
        if (status != SensorStatus.Available)
        {
            Assert.Null(celsius);
            return SensorValue.Unavailable(status, capturedAt);
        }

        return SensorValue.Available(Assert.NotNull(celsius), capturedAt);
    }

    private static void AssertProblems(
        IReadOnlyList<string> problems,
        SensorReadings flagged,
        bool cpuProblemExpected,
        bool ramProblemExpected)
    {
        AssertSensorProblem(problems, "CPU", flagged.Cpu, cpuProblemExpected);
        AssertSensorProblem(problems, "RAM", flagged.Ram, ramProblemExpected);
        Assert.Equal((cpuProblemExpected ? 1 : 0) + (ramProblemExpected ? 1 : 0), problems.Count);
    }

    // A problem names its sensor, status and value, so a failed smoke run is diagnosable from its
    // message alone.
    private static void AssertSensorProblem(IReadOnlyList<string> problems, string sensor, SensorValue value, bool problemExpected)
    {
        List<string> sensorProblems = problems.Where(problem => problem.StartsWith(sensor + " ", StringComparison.Ordinal)).ToList();
        if (!problemExpected)
        {
            Assert.Empty(sensorProblems);
            return;
        }

        string problem = Assert.Single(sensorProblems);
        Assert.Contains($"({value.Status}, captured", problem, StringComparison.Ordinal);
        Assert.Contains(
            value.Celsius is double celsius ? FormattableString.Invariant($"{celsius:F1} °C") : "N/A",
            problem,
            StringComparison.Ordinal);
    }
}

/// <summary>
/// Whether the test process runs elevated: the precondition the hardware smoke test skips without.
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
