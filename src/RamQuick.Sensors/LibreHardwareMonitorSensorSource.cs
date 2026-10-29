using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace RamQuick.Sensors;

/// <summary>
/// One sensor value flattened out of the LibreHardwareMonitorLib hardware tree, the input of the pure
/// selection helpers of <see cref="LibreHardwareMonitorSensorSource"/>.
/// </summary>
/// <param name="HardwareType">
/// The type of the top-level hardware the sensor belongs to; a sensor of a sub-hardware carries its
/// parent's type.
/// </param>
/// <param name="SensorName">The sensor's display name, for example <c>CPU Package</c> or <c>DIMM #1</c>.</param>
/// <param name="SensorType">
/// The sensor's type. Names alone are ambiguous: a power sensor is also named <c>CPU Package</c>.
/// </param>
/// <param name="Value">
/// The value from the latest update, or <see langword="null"/> when the sensor has none or its hardware
/// failed to update.
/// </param>
internal readonly record struct SensorSample(HardwareType HardwareType, string SensorName, SensorType SensorType, float? Value);

/// <summary>
/// The production <see cref="ISensorSource"/>: CPU and DIMM temperatures read through
/// LibreHardwareMonitorLib over the PawnIO driver.
/// </summary>
/// <remarks>
/// <para>
/// Only the CPU and memory hardware groups are enabled, and only their temperature sensors are read.
/// The CPU reading is the Intel package temperature (<c>CPU Package</c>) or, on AMD processors,
/// <c>Core (Tctl/Tdie)</c>. The RAM reading is the maximum of the <c>DIMM #N</c> temperature sensors,
/// which DDR4 and DDR5 modules with a thermal sensor expose over SMBus; on DDR5 that sensor sits in the
/// SPD hub.
/// </para>
/// <para>
/// Every hardware path needs the PawnIO driver and an elevated process. The library opens PawnIO but
/// never installs it, and this class never installs, starts or prompts for any driver or service and
/// never writes the registry. When either precondition is missing, <see cref="Open"/> does not create
/// the library's <c>Computer</c> at all, so no driver access is attempted, and every
/// <see cref="Read"/> reports <see cref="SensorStatus.DriverUnavailable"/> for both readings.
/// </para>
/// <para>
/// Threading: not thread-safe, by the <see cref="ISensorSource"/> contract. One thread calls
/// <see cref="Open"/>, then <see cref="Read"/>, then <see cref="Dispose"/>. No member throws.
/// </para>
/// </remarks>
public sealed class LibreHardwareMonitorSensorSource : ISensorSource
{
    /// <summary>
    /// The Intel CPU package temperature sensor. A power sensor shares the name, so the sensor type is
    /// matched too.
    /// </summary>
    internal const string IntelPackageSensorName = "CPU Package";

    /// <summary>
    /// The AMD Zen control temperature sensor, which reads Tctl or Tdie.
    /// </summary>
    internal const string AmdTctlTdieSensorName = "Core (Tctl/Tdie)";

    /// <summary>
    /// The prefix of a DIMM's live temperature sensor name, <c>DIMM #N</c>. The module's resolution and
    /// thermal-limit metadata are temperature sensors too, under longer names.
    /// </summary>
    internal const string DimmSensorNamePrefix = "DIMM #";

    /// <summary>
    /// CPU temperature sensor names in order of preference; the first name present decides the reading.
    /// </summary>
    private static readonly string[] _cpuSensorNamesByPreference = [IntelPackageSensorName, AmdTctlTdieSensorName];

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The opened library session; <see langword="null"/> before <see cref="Open"/>, when access was
    /// denied or opening failed, and after <see cref="Dispose"/>.
    /// </summary>
    private Computer? _computer;

    /// <summary>
    /// The status both readings carry while <see cref="_computer"/> is <see langword="null"/>.
    /// </summary>
    private SensorStatus _unavailableStatus = SensorStatus.Failed;

    private bool _openAttempted;
    private bool _disposed;

    /// <summary>
    /// Creates a source that has not opened the sensor library yet.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock that stamps every reading's <see cref="SensorValue.CapturedAt"/>; it must be the clock
    /// the sensor monitor judges freshness with. Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    public LibreHardwareMonitorSensorSource(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Checks hardware access and, when it is available, opens the library's CPU and memory groups.
    /// </summary>
    /// <remarks>
    /// The access check is read-only: a query of the process token's Administrator membership and the
    /// library's check of the PawnIO installation registration. Without elevation or without PawnIO no
    /// <c>Computer</c> is created and later reads report <see cref="SensorStatus.DriverUnavailable"/>. A
    /// failure while opening the library is reported as <see cref="SensorStatus.Failed"/> by later reads.
    /// The method never throws; only its first call has an effect, and it has none after
    /// <see cref="Dispose"/>.
    /// </remarks>
    public void Open()
    {
        if (_openAttempted || _disposed)
        {
            return;
        }

        _openAttempted = true;

        if (ResolveAccess(IsProcessElevated(), IsPawnIoInstalled()) is { } deniedStatus)
        {
            _unavailableStatus = deniedStatus;
            return;
        }

        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsMemoryEnabled = true,
                IsBatteryEnabled = false,
                IsControllerEnabled = false,
                IsGpuEnabled = false,
                IsMotherboardEnabled = false,
                IsNetworkEnabled = false,
                IsPowerMonitorEnabled = false,
                IsPsuEnabled = false,
                IsStorageEnabled = false,
            };
            computer.Open();
        }
        catch (Exception)
        {
            if (computer is not null)
            {
                _ = TryClose(computer);
            }

            _unavailableStatus = SensorStatus.Failed;
            return;
        }

        _computer = computer;
    }

    /// <summary>
    /// Updates the CPU and memory hardware and captures one snapshot of both temperatures.
    /// </summary>
    /// <remarks>
    /// Without an opened library, both readings carry the status <see cref="Open"/> determined:
    /// <see cref="SensorStatus.DriverUnavailable"/> when access was denied, otherwise
    /// <see cref="SensorStatus.Failed"/>. Each reading is otherwise selected independently, so one can be
    /// available while the other reports <see cref="SensorStatus.NoSensor"/> or
    /// <see cref="SensorStatus.Failed"/>. The capture time is taken after the hardware update. No
    /// plausibility limit is applied beyond rejecting NaN and infinity: a high reading must reach the
    /// thermal limits. The method never throws; an unexpected library failure yields
    /// <see cref="SensorStatus.Failed"/> for both readings.
    /// </remarks>
    /// <returns>The snapshot of both readings, each with its own status and capture time.</returns>
    public SensorReadings Read()
    {
        Computer? computer = _computer;
        if (computer is null)
        {
            return SensorReadings.Unavailable(_unavailableStatus, _timeProvider.GetUtcNow());
        }

        try
        {
            List<SensorSample> samples = UpdateAndCollect(computer.Hardware);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            return new SensorReadings(SelectCpu(samples, now), SelectRam(samples, now));
        }
        catch (Exception)
        {
            return SensorReadings.Unavailable(SensorStatus.Failed, _timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Closes the sensor library session, if one was opened. Idempotent and never throws.
    /// </summary>
    /// <remarks>
    /// The sensor monitor calls it on its sensor thread, and never for a hung source. After it returns,
    /// <see cref="Open"/> has no effect and <see cref="Read"/> reports <see cref="SensorStatus.Failed"/>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Computer? computer = _computer;
        _computer = null;
        _unavailableStatus = SensorStatus.Failed;

        if (computer is not null)
        {
            _ = TryClose(computer);
        }
    }

    /// <summary>
    /// Decides whether the hardware access path may be used.
    /// </summary>
    /// <param name="isElevated">Whether the process token is a member of the Administrators group.</param>
    /// <param name="driverInstalled">Whether the PawnIO driver is registered as installed.</param>
    /// <returns>
    /// <see cref="SensorStatus.DriverUnavailable"/> when either precondition is missing;
    /// <see langword="null"/> when the library may be opened.
    /// </returns>
    internal static SensorStatus? ResolveAccess(bool isElevated, bool driverInstalled) =>
        isElevated && driverInstalled ? null : SensorStatus.DriverUnavailable;

    /// <summary>
    /// Selects the CPU temperature: the <c>CPU Package</c> temperature sensor of CPU hardware, else its
    /// <c>Core (Tctl/Tdie)</c> temperature sensor, compared ordinally.
    /// </summary>
    /// <remarks>
    /// The first preferred name present decides the reading. Where several processor packages each
    /// report the sensor, the hottest finite value is taken.
    /// </remarks>
    /// <param name="samples">The flattened sensor values of one update.</param>
    /// <param name="now">The capture time stamped on the result.</param>
    /// <returns>
    /// <see cref="SensorStatus.Available"/> with the maximum finite value; <see cref="SensorStatus.Failed"/>
    /// when matching sensors exist but none holds a finite value; <see cref="SensorStatus.NoSensor"/> when
    /// none exists.
    /// </returns>
    internal static SensorValue SelectCpu(IEnumerable<SensorSample> samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);
        IReadOnlyCollection<SensorSample> snapshot = samples as IReadOnlyCollection<SensorSample> ?? samples.ToList();

        foreach (string sensorName in _cpuSensorNamesByPreference)
        {
            (bool found, double? maximum) = MaxFinite(
                snapshot,
                sample => sample.HardwareType == HardwareType.Cpu
                    && sample.SensorType == SensorType.Temperature
                    && string.Equals(sample.SensorName, sensorName, StringComparison.Ordinal));

            if (found)
            {
                return ToSensorValue(found: true, maximum, now);
            }
        }

        return SensorValue.Unavailable(SensorStatus.NoSensor, now);
    }

    /// <summary>
    /// Selects the RAM temperature: the maximum across the <c>DIMM #N</c> temperature sensors of memory
    /// hardware.
    /// </summary>
    /// <remarks>
    /// Only names that are exactly <c>DIMM #</c> followed by decimal digits match, which excludes the
    /// resolution and thermal-limit metadata sensors of the same module. Sensors without a finite value
    /// are skipped while the others count.
    /// </remarks>
    /// <param name="samples">The flattened sensor values of one update.</param>
    /// <param name="now">The capture time stamped on the result.</param>
    /// <returns>
    /// <see cref="SensorStatus.Available"/> with the maximum finite value; <see cref="SensorStatus.Failed"/>
    /// when DIMM sensors exist but none holds a finite value; <see cref="SensorStatus.NoSensor"/> when the
    /// modules expose no thermal sensor.
    /// </returns>
    internal static SensorValue SelectRam(IEnumerable<SensorSample> samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);

        (bool found, double? maximum) = MaxFinite(
            samples,
            sample => sample.HardwareType == HardwareType.Memory
                && sample.SensorType == SensorType.Temperature
                && IsDimmSensorName(sample.SensorName));

        return ToSensorValue(found, maximum, now);
    }

    /// <summary>
    /// Whether a sensor name is a DIMM's live temperature name: <c>DIMM #</c> followed by one or more
    /// ASCII digits and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not the regular expression <c>^DIMM #\d+$</c>: in .NET <c>\d</c> also matches
    /// non-ASCII digits and <c>$</c> also matches before a trailing newline, so the pattern would accept
    /// names the library never gives a DIMM temperature sensor.
    /// </remarks>
    /// <param name="sensorName">The sensor name to test.</param>
    /// <returns><see langword="true"/> for names such as <c>DIMM #1</c>; otherwise <see langword="false"/>.</returns>
    internal static bool IsDimmSensorName(string? sensorName)
    {
        if (sensorName is null || !sensorName.StartsWith(DimmSensorNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> index = sensorName.AsSpan(DimmSensorNamePrefix.Length);
        return !index.IsEmpty && !index.ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>
    /// Updates every CPU and memory hardware item and its sub-hardware, and flattens their sensors.
    /// Hardware of any other type is neither updated nor read.
    /// </summary>
    /// <remarks>
    /// An item whose update throws contributes its sensors with a <see langword="null"/> value, so a
    /// failed update reads as <see cref="SensorStatus.Failed"/> rather than as the previous update's
    /// value.
    /// </remarks>
    /// <param name="hardware">The library's top-level hardware.</param>
    /// <returns>The sensor values, each tagged with its top-level hardware's type.</returns>
    internal static List<SensorSample> UpdateAndCollect(IEnumerable<IHardware> hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        var samples = new List<SensorSample>();
        foreach (IHardware item in hardware)
        {
            HardwareType hardwareType = item.HardwareType;
            if (hardwareType is HardwareType.Cpu or HardwareType.Memory)
            {
                CollectUpdated(item, hardwareType, samples);
            }
        }

        return samples;
    }

    private static void CollectUpdated(IHardware hardware, HardwareType topLevelType, List<SensorSample> samples)
    {
        bool updated = TryUpdate(hardware);
        foreach (ISensor sensor in hardware.Sensors ?? [])
        {
            samples.Add(new SensorSample(topLevelType, sensor.Name ?? string.Empty, sensor.SensorType, updated ? sensor.Value : null));
        }

        foreach (IHardware subHardware in hardware.SubHardware ?? [])
        {
            CollectUpdated(subHardware, topLevelType, samples);
        }
    }

    private static (bool Found, double? Maximum) MaxFinite(IEnumerable<SensorSample> samples, Func<SensorSample, bool> matches)
    {
        bool found = false;
        double? maximum = null;
        foreach (SensorSample sample in samples)
        {
            if (!matches(sample))
            {
                continue;
            }

            found = true;
            if (sample.Value is float value && float.IsFinite(value))
            {
                maximum = maximum is { } current ? Math.Max(current, value) : value;
            }
        }

        return (found, maximum);
    }

    private static SensorValue ToSensorValue(bool found, double? maximum, DateTimeOffset now) =>
        maximum is { } celsius
            ? SensorValue.Available(celsius, now)
            : SensorValue.Unavailable(found ? SensorStatus.Failed : SensorStatus.NoSensor, now);

    private static bool TryUpdate(IHardware hardware)
    {
        try
        {
            hardware.Update();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Closes the library session. Closing happens only on the way out of the source, so a failure
    /// leaves nothing further to release here and is reported to the caller only as the result.
    /// </summary>
    private static bool TryClose(Computer computer)
    {
        try
        {
            computer.Close();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the PawnIO driver is registered as installed. The library answers from a registry read of
    /// the driver's installation entry; it neither loads nor installs the driver.
    /// </summary>
    private static bool IsPawnIoInstalled()
    {
        try
        {
            return PawnIo.IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

