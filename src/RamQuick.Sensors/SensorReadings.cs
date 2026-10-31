namespace RamQuick.Sensors;

/// <summary>
/// Availability of a single temperature reading. Each reading carries its own status, so the CPU
/// and RAM thermal limits are enabled or disabled independently.
/// </summary>
public enum SensorStatus
{
    /// <summary>
    /// A fresh reading that carries a temperature. This is the only status a thermal limit acts on.
    /// </summary>
    Available = 0,

    /// <summary>
    /// The low-level hardware access path cannot be used: the PawnIO driver is not installed, or the
    /// process is not elevated. RAMQuick never installs a driver, so the reading shows N/A.
    /// </summary>
    DriverUnavailable = 1,

    /// <summary>
    /// Hardware access works, but the machine exposes no matching temperature sensor.
    /// </summary>
    NoSensor = 2,

    /// <summary>
    /// The read threw or returned no numeric value.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The last reading is older than 3 s (<c>SensorMonitor.StaleAfter</c>). It shows N/A and its
    /// thermal limit stays inactive until a fresh reading arrives.
    /// </summary>
    Stale = 4,

    /// <summary>
    /// Opening the source or a read has not returned within 5 s (<c>SensorMonitor.HungAfter</c>). No
    /// further reads are made for the lifetime of the process.
    /// </summary>
    Hung = 5,
}

/// <summary>
/// One temperature reading and its availability.
/// </summary>
/// <remarks>
/// <see cref="Celsius"/> has a value if and only if <see cref="Status"/> is
/// <see cref="SensorStatus.Available"/>, so no consumer can display or act on a stale or failed
/// temperature. The record is deliberately not positional and its properties are get-only, so a
/// <c>with</c> expression cannot assign a member and break that invariant; instances come only from
/// <see cref="Available"/>, <see cref="Unavailable"/> and <see cref="WithStatus"/>.
/// </remarks>
public sealed record SensorValue
{
    private SensorValue(double? celsius, DateTimeOffset capturedAt, SensorStatus status)
    {
        Celsius = celsius;
        CapturedAt = capturedAt;
        Status = status;
    }

    /// <summary>
    /// The temperature in degrees Celsius, or <see langword="null"/> when <see cref="Status"/> is not
    /// <see cref="SensorStatus.Available"/>.
    /// </summary>
    public double? Celsius { get; }

    /// <summary>
    /// When the reading was captured, or, for an unavailable value, when that status was determined.
    /// Re-marking a value with <see cref="WithStatus"/> keeps this time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// The availability of this reading.
    /// </summary>
    public SensorStatus Status { get; }

    /// <summary>
    /// <see langword="true"/> when the value carries a temperature a thermal limit may act on.
    /// Freshness is guaranteed by the sensor monitor, which re-marks old values
    /// <see cref="SensorStatus.Stale"/>.
    /// </summary>
    public bool IsAvailable => Status == SensorStatus.Available;

    /// <summary>
    /// Creates an available reading.
    /// </summary>
    /// <param name="celsius">The temperature in degrees Celsius; must be a finite number.</param>
    /// <param name="capturedAt">When the reading was captured.</param>
    /// <returns>A value with status <see cref="SensorStatus.Available"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="celsius"/> is NaN or infinite.</exception>
    public static SensorValue Available(double celsius, DateTimeOffset capturedAt)
    {
        if (!double.IsFinite(celsius))
        {
            throw new ArgumentOutOfRangeException(nameof(celsius), celsius, "A temperature reading must be a finite number.");
        }

        return new SensorValue(celsius, capturedAt, SensorStatus.Available);
    }

    /// <summary>
    /// Creates a reading that carries no temperature.
    /// </summary>
    /// <param name="status">Why no temperature is available; any defined status except <see cref="SensorStatus.Available"/>.</param>
    /// <param name="capturedAt">When the status was determined.</param>
    /// <returns>A value with the given status and no temperature.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="SensorStatus.Available"/> or not a defined <see cref="SensorStatus"/> value.
    /// </exception>
    public static SensorValue Unavailable(SensorStatus status, DateTimeOffset capturedAt)
    {
        if (status == SensorStatus.Available || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "An unavailable reading needs a defined status other than Available.");
        }

        return new SensorValue(null, capturedAt, status);
    }

    /// <summary>
    /// Re-marks this reading as unavailable, for example <see cref="SensorStatus.Stale"/> or
    /// <see cref="SensorStatus.Hung"/>, keeping its original <see cref="CapturedAt"/>.
    /// </summary>
    /// <param name="status">The new status; any defined status except <see cref="SensorStatus.Available"/>.</param>
    /// <returns>A value with the given status, no temperature and this value's capture time.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="SensorStatus.Available"/> or not a defined <see cref="SensorStatus"/> value.
    /// An available value is created only through <see cref="Available"/>.
    /// </exception>
    public SensorValue WithStatus(SensorStatus status) => Unavailable(status, CapturedAt);
}

/// <summary>
/// One snapshot of the two temperatures RAMQuick monitors.
/// </summary>
/// <param name="Cpu">
/// The CPU temperature: the package temperature on Intel processors, Tctl/Tdie on AMD processors.
/// </param>
/// <param name="Ram">
/// The RAM temperature: the maximum across all DIMM temperature sensors. On DDR5 the sensor is the
/// SPD hub, which reads below the DRAM die temperature.
/// </param>
public sealed record SensorReadings(SensorValue Cpu, SensorValue Ram)
{
    private readonly SensorValue _cpu = Cpu ?? throw new ArgumentNullException(nameof(Cpu));
    private readonly SensorValue _ram = Ram ?? throw new ArgumentNullException(nameof(Ram));

    /// <summary>
    /// The CPU temperature: the package temperature on Intel processors, Tctl/Tdie on AMD processors.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value assigned is <see langword="null"/>.</exception>
    public SensorValue Cpu
    {
        get => _cpu;
        init => _cpu = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The RAM temperature: the maximum across all DIMM temperature sensors. On DDR5 the sensor is the
    /// SPD hub, which reads below the DRAM die temperature.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value assigned is <see langword="null"/>.</exception>
    public SensorValue Ram
    {
        get => _ram;
        init => _ram = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Creates a snapshot in which both readings have the same unavailable status: the initial
    /// snapshot, a throwing or hung source, or a missing driver.
    /// </summary>
    /// <param name="status">The status of both readings; any defined status except <see cref="SensorStatus.Available"/>.</param>
    /// <param name="capturedAt">When the status was determined.</param>
    /// <returns>A snapshot with no temperature for either sensor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="SensorStatus.Available"/> or not a defined <see cref="SensorStatus"/> value.
    /// </exception>
    public static SensorReadings Unavailable(SensorStatus status, DateTimeOffset capturedAt)
    {
        var value = SensorValue.Unavailable(status, capturedAt);
        return new SensorReadings(value, value);
    }
}
