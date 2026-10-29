namespace RamQuick.Sensors;

/// <summary>
/// The temperature sensor a thermal limit applies to.
/// </summary>
public enum ThermalSensor
{
    /// <summary>
    /// The CPU temperature (package on Intel, Tctl/Tdie on AMD), limited by <see cref="ThermalLimits.CpuLimitCelsius"/>.
    /// </summary>
    Cpu = 0,

    /// <summary>
    /// The RAM temperature (the hottest DIMM sensor), limited by <see cref="ThermalLimits.RamLimitCelsius"/>.
    /// </summary>
    Ram = 1,
}

/// <summary>
/// A thermal limit that a reading reached: the run must stop with the thermal stop reason.
/// </summary>
/// <remarks>
/// The run controller builds the ABORTED reason ("Stopped for safety: CPU temperature reached 95 °C", or the RAM
/// equivalent; the texts live in <c>RamQuick.App.UiText</c>) and the <c>.log</c> thermal event from this verdict.
/// </remarks>
/// <param name="Sensor">The sensor whose reading reached its limit.</param>
/// <param name="Celsius">The reading that reached the limit, in degrees Celsius.</param>
/// <param name="LimitCelsius">The limit that was reached, in degrees Celsius.</param>
public sealed record ThermalVerdict(ThermalSensor Sensor, double Celsius, double LimitCelsius);

/// <summary>
/// The thermal safety limits and their evaluation against one sensor snapshot.
/// </summary>
/// <remarks>
/// <para>
/// A run stops on any single 1 Hz sample at or above a limit. Each limit acts only on a reading whose status is
/// <see cref="SensorStatus.Available"/>; a missing, failed, stale or hung reading disables its own limit and leaves the
/// other sensor's limit active. Freshness is guaranteed upstream: the sensor monitor re-marks readings older than 3 s
/// as <see cref="SensorStatus.Stale"/>.
/// </para>
/// <para>
/// The class is pure: it reads no clock, performs no I/O and holds no state. It does not check the run state either;
/// the run controller evaluates readings only while a run is in the Running state. The constants are the single
/// retune points for the limits.
/// </para>
/// </remarks>
public static class ThermalLimits
{
    /// <summary>
    /// The CPU temperature at or above which a run stops: 95 °C.
    /// </summary>
    /// <remarks>
    /// Known risk: AMD Ryzen 7000 and 9000 processors may sit at 95 °C by design under sustained all-core load, so on
    /// those parts a healthy system can reach this limit. Retuning means editing this constant only.
    /// </remarks>
    public const double CpuLimitCelsius = 95.0;

    /// <summary>
    /// The RAM temperature at or above which a run stops: 85 °C, the DDR5 Tcase point above which the refresh rate
    /// must double.
    /// </summary>
    /// <remarks>
    /// Known risk: the reading comes from the DIMM's SPD-hub or thermal sensor, which runs below the DRAM die
    /// temperature (about 12 °C below the module surface in published measurements), so the die may already be
    /// hotter than this limit when it trips. Retuning means editing this constant only.
    /// </remarks>
    public const double RamLimitCelsius = 85.0;

    /// <summary>
    /// Checks one sensor snapshot against the thermal limits, CPU first, then RAM.
    /// </summary>
    /// <param name="readings">The snapshot to check.</param>
    /// <returns>
    /// The verdict for the first available reading at or above its limit, or <see langword="null"/> when no available
    /// reading reaches its limit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is <see langword="null"/>.</exception>
    public static ThermalVerdict? Evaluate(SensorReadings readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        return Check(readings.Cpu, ThermalSensor.Cpu, CpuLimitCelsius)
            ?? Check(readings.Ram, ThermalSensor.Ram, RamLimitCelsius);
    }

    // Exact comparison, no epsilon: sensor values arrive as float widened to double, and (double)95.0f is exactly
    // 95.0 while (double)94.9f is about 94.9000015, so the boundary samples fall on the intended side.
    private static ThermalVerdict? Check(SensorValue value, ThermalSensor sensor, double limitCelsius) =>
        value is { Status: SensorStatus.Available, Celsius: double celsius } && celsius >= limitCelsius
            ? new ThermalVerdict(sensor, celsius, limitCelsius)
            : null;
}
