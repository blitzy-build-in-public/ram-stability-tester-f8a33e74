namespace RamQuick.Sensors;

/// <summary>
/// A source of CPU and RAM temperature readings: the seam between the sensor monitor and the
/// hardware sensor library, substituted by a scriptable fake in tests.
/// </summary>
/// <remarks>
/// <para>
/// Threading: a source is owned by a single thread. <c>SensorMonitor</c> runs one dedicated
/// background thread that calls <see cref="Open"/> once, then <see cref="Read"/> one call at a time,
/// then <see cref="IDisposable.Dispose"/>. No member is ever called concurrently with another, so
/// implementations need no internal locking. Any other caller, such as the hardware smoke test,
/// must honour the same order on one thread: <see cref="Open"/>, any number of
/// <see cref="Read"/> calls, then <see cref="IDisposable.Dispose"/>.
/// </para>
/// <para>
/// Dispose: called at most once, only from the sensor thread, never concurrently with
/// <see cref="Read"/>, and only when the source is not hung. It closes the underlying library or
/// driver session. It can follow an <see cref="Open"/> that threw, so it must release whatever was
/// acquired and tolerate a partial open. A hung source is abandoned without being disposed: its
/// blocked background thread stays parked and the process exit reclaims it.
/// </para>
/// </remarks>
public interface ISensorSource : IDisposable
{
    /// <summary>
    /// Opens the underlying sensor library or driver session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called exactly once by <c>SensorMonitor</c>, on its sensor thread, before any
    /// <see cref="Read"/>. It uses only what is already present on the machine: it never installs a
    /// driver, service or other component and never changes system configuration, so a missing
    /// driver is a reportable condition, not something to repair.
    /// </para>
    /// <para>
    /// Unavailability is reported through the statuses of subsequent <see cref="Read"/> results,
    /// for example <see cref="SensorStatus.DriverUnavailable"/> or <see cref="SensorStatus.NoSensor"/>,
    /// rather than by throwing. If it throws anyway, the monitor publishes
    /// <see cref="SensorStatus.Failed"/> for both readings and performs no reads.
    /// </para>
    /// </remarks>
    void Open();

    /// <summary>
    /// Captures one snapshot of the CPU and RAM temperatures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only on the sensor thread, after <see cref="Open"/>, and never concurrently: the
    /// monitor never overlaps reads and skips a tick that arrives while a read is in flight.
    /// </para>
    /// <para>
    /// Each value of the returned snapshot carries its own <see cref="SensorValue.Status"/> and
    /// <see cref="SensorValue.CapturedAt"/>, so one reading can be available while the other is not.
    /// <see cref="SensorValue.CapturedAt"/> is the source clock's time at the moment that reading was
    /// captured. The monitor judges freshness by comparing it with its own clock, so a source takes
    /// it from the same <see cref="TimeProvider"/>. A source reports
    /// <see cref="SensorStatus.Available"/>, <see cref="SensorStatus.DriverUnavailable"/>,
    /// <see cref="SensorStatus.NoSensor"/> or <see cref="SensorStatus.Failed"/>;
    /// <see cref="SensorStatus.Stale"/> and <see cref="SensorStatus.Hung"/> are assigned by the
    /// monitor.
    /// </para>
    /// <para>
    /// The call may block. If it has not returned after 5 s, the monitor marks the source
    /// <see cref="SensorStatus.Hung"/> and never calls it again. It should not throw; an exception is
    /// treated as <see cref="SensorStatus.Failed"/> for both readings for that tick.
    /// </para>
    /// </remarks>
    /// <returns>The snapshot of both readings, each with its own status and capture time.</returns>
    SensorReadings Read();
}
