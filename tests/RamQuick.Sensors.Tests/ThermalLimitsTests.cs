using Xunit;

namespace RamQuick.Sensors.Tests;

/// <summary>
/// Tests for <see cref="ThermalLimits"/>: a single fresh reading at or above its limit (CPU 95 °C,
/// RAM 85 °C) trips, a reading of any other status never trips, and each sensor's limit stays active
/// while the other reading is missing.
/// </summary>
/// <remarks>
/// Ageing a reading past 3 s into <see cref="SensorStatus.Stale"/> is the sensor monitor's job and is
/// tested in <c>SensorMonitorTests</c>; here a stale reading arrives already re-marked.
/// </remarks>
public sealed class ThermalLimitsTests
{
    // Well below both limits: the reading of the sensor a case does not exercise.
    private const double CoolCelsius = 40.0;

    private static readonly DateTimeOffset _now = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

    // Derived from the enum, so a status added later is covered without editing these tests.
    public static TheoryData<SensorStatus> UnavailableStatuses { get; } = new(UnavailableStatusValues());

    public static MatrixTheoryData<ThermalSensor, SensorStatus> SensorWithOtherUnavailable { get; } =
        new(Enum.GetValues<ThermalSensor>(), UnavailableStatusValues());

    [Fact]
    public void Constants_MatchAapThresholds()
    {
        Assert.Equal(95.0, ThermalLimits.CpuLimitCelsius);
        Assert.Equal(85.0, ThermalLimits.RamLimitCelsius);
    }

    [Theory]
    [InlineData(ThermalSensor.Cpu, 95.0)]
    [InlineData(ThermalSensor.Cpu, 100.0)]
    [InlineData(ThermalSensor.Cpu, 120.0)]
    [InlineData(ThermalSensor.Ram, 85.0)]
    [InlineData(ThermalSensor.Ram, 90.0)]
    [InlineData(ThermalSensor.Ram, 120.0)]
    public void Evaluate_FreshReadingAtOrAboveLimit_ReturnsVerdictWithObservedValue(ThermalSensor sensor, double celsius)
    {
        ThermalVerdict? verdict = ThermalLimits.Evaluate(ReadingsWith(sensor, Fresh(celsius)));

        Assert.Equal(new ThermalVerdict(sensor, celsius, LimitFor(sensor)), verdict);
    }

    [Theory]
    [InlineData(ThermalSensor.Cpu)]
    [InlineData(ThermalSensor.Ram)]
    public void Evaluate_LimitBoundary_IsInclusiveWithoutTolerance(ThermalSensor sensor)
    {
        double limit = LimitFor(sensor);

        Assert.Equal(
            new ThermalVerdict(sensor, limit, limit),
            ThermalLimits.Evaluate(ReadingsWith(sensor, Fresh(limit))));
        Assert.Null(ThermalLimits.Evaluate(ReadingsWith(sensor, Fresh(Math.BitDecrement(limit)))));
    }

    // Sensor values arrive as float widened to double: (double)94.9f is slightly above 94.9 and still
    // below the limit, so the widened boundary samples must not trip either.
    [Theory]
    [InlineData(94.9, CoolCelsius)]
    [InlineData((double)94.9f, CoolCelsius)]
    [InlineData(CoolCelsius, 84.9)]
    [InlineData(CoolCelsius, (double)84.9f)]
    [InlineData(94.9, 84.9)]
    [InlineData(55.0, 45.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-10.0, -5.0)]
    public void Evaluate_FreshReadingsBelowLimits_ReturnsNull(double cpuCelsius, double ramCelsius)
    {
        Assert.Null(ThermalLimits.Evaluate(Readings(Fresh(cpuCelsius), Fresh(ramCelsius))));
    }

    [Theory]
    [MemberData(nameof(UnavailableStatuses))]
    public void Evaluate_ReadingsNotAvailable_NeverTrip(SensorStatus status)
    {
        // The monitor re-marks an old or hung reading, so a stale reading of 95 °C arrives like this.
        SensorReadings remarkedAtLimits = Readings(
            Fresh(ThermalLimits.CpuLimitCelsius).WithStatus(status),
            Fresh(ThermalLimits.RamLimitCelsius).WithStatus(status));

        Assert.Null(ThermalLimits.Evaluate(remarkedAtLimits));
        Assert.Null(ThermalLimits.Evaluate(Readings(Missing(status), Missing(status))));
        Assert.Null(ThermalLimits.Evaluate(SensorReadings.Unavailable(status, _now)));
    }

    [Theory]
    [MemberData(nameof(SensorWithOtherUnavailable))]
    public void Evaluate_OtherReadingNotAvailable_LimitStillActive(ThermalSensor sensor, SensorStatus otherStatus)
    {
        double limit = LimitFor(sensor);

        ThermalVerdict? verdict = ThermalLimits.Evaluate(ReadingsWith(sensor, Fresh(limit), Missing(otherStatus)));

        Assert.Equal(new ThermalVerdict(sensor, limit, limit), verdict);
    }

    [Fact]
    public void Evaluate_BothReadingsAboveLimits_ReturnsCpuVerdict()
    {
        ThermalVerdict? verdict = ThermalLimits.Evaluate(Readings(Fresh(96.0), Fresh(90.0)));

        Assert.Equal(new ThermalVerdict(ThermalSensor.Cpu, 96.0, ThermalLimits.CpuLimitCelsius), verdict);
    }

    [Fact]
    public void Evaluate_RepeatedCalls_CarryNoState()
    {
        SensorReadings atCpuLimit = Readings(Fresh(ThermalLimits.CpuLimitCelsius), Fresh(CoolCelsius));
        SensorReadings cool = Readings(Fresh(CoolCelsius), Fresh(CoolCelsius));

        ThermalVerdict? first = ThermalLimits.Evaluate(atCpuLimit);
        ThermalVerdict? afterTrip = ThermalLimits.Evaluate(cool);
        ThermalVerdict? second = ThermalLimits.Evaluate(atCpuLimit);

        Assert.NotNull(first);
        Assert.Null(afterTrip);
        Assert.Equal(first, second);
        Assert.Null(ThermalLimits.Evaluate(cool));
    }

    [Fact]
    public void Evaluate_NullReadings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("readings", () => ThermalLimits.Evaluate(null!));
    }

    private static SensorStatus[] UnavailableStatusValues() =>
        Enum.GetValues<SensorStatus>().Where(status => status != SensorStatus.Available).ToArray();

    private static double LimitFor(ThermalSensor sensor) => sensor switch
    {
        ThermalSensor.Cpu => ThermalLimits.CpuLimitCelsius,
        ThermalSensor.Ram => ThermalLimits.RamLimitCelsius,
        _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null),
    };

    private static SensorValue Fresh(double celsius) => SensorValue.Available(celsius, _now);

    private static SensorValue Missing(SensorStatus status) => SensorValue.Unavailable(status, _now);

    private static SensorReadings Readings(SensorValue cpu, SensorValue ram) => new(cpu, ram);

    // Puts value on sensor and a cool fresh reading on the other sensor.
    private static SensorReadings ReadingsWith(ThermalSensor sensor, SensorValue value) =>
        ReadingsWith(sensor, value, Fresh(CoolCelsius));

    private static SensorReadings ReadingsWith(ThermalSensor sensor, SensorValue value, SensorValue other) =>
        sensor switch
        {
            ThermalSensor.Cpu => Readings(value, other),
            ThermalSensor.Ram => Readings(other, value),
            _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null),
        };
}
