using System.Diagnostics;
using System.Threading;
using RamQuick.Sensors;

// Linked into RamQuick.Sensors.Tests, RamQuick.TestHost and RamQuick.IntegrationTests as well: this
// file may depend on the BCL and the public RamQuick.Sensors contracts only, never on a test
// framework, another fake, or an App, Interop or Logging type.
namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// A scriptable <see cref="ISensorSource"/>: per-sensor temperatures and statuses, open and read
/// failures, a per-read delay and a read that does not return until released. It counts and records
/// how it is called, and never touches hardware, a driver or the sensor library.
/// </summary>
/// <remarks>
/// <para>
/// Every member is thread-safe. <see cref="Open"/>, <see cref="Read"/> and <see cref="Dispose"/> run on
/// <see cref="SensorMonitor"/>'s sensor thread while the test scripts and observes the fake on its own
/// thread. A test may also call <see cref="Dispose"/> itself to end a blocked or delayed read.
/// </para>
/// <para>
/// Capture times come from the <see cref="TimeProvider"/> passed to the constructor. Pass the monitor's
/// provider, so that freshness is judged on one clock. <see cref="ReadDelay"/> is real time, not
/// provider time, so a delayed read stays in flight while a fake clock is advanced.
/// </para>
/// <para>
/// The fake reports only <see cref="SensorStatus.Available"/>, <see cref="SensorStatus.DriverUnavailable"/>,
/// <see cref="SensorStatus.NoSensor"/> and <see cref="SensorStatus.Failed"/>, as the
/// <see cref="ISensorSource"/> contract requires: <see cref="SensorStatus.Stale"/> and
/// <see cref="SensorStatus.Hung"/> are assigned by the monitor alone.
/// </para>
/// </remarks>
public sealed class FakeSensorSource : ISensorSource
{
    /// <summary>
    /// The CPU temperature a new instance reports until a test scripts another: 55.0 °C.
    /// </summary>
    public const double DefaultCpuCelsius = 55.0;

    /// <summary>
    /// The RAM temperature, already the maximum across DIMMs, a new instance reports until a test
    /// scripts another: 45.0 °C.
    /// </summary>
    public const double DefaultRamCelsius = 45.0;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    // Guarded by _gate.
    private SensorScript _cpu = new(DefaultCpuCelsius, SensorStatus.Available);
    private SensorScript _ram = new(DefaultRamCelsius, SensorStatus.Available);
    private TimeSpan _capturedAtOffset;
    private TimeSpan _readDelay;
    private bool _blocked;
    private bool _disposed;

    private Exception? _openException;
    private Exception? _readException;

    private int _openCount;
    private int _readCount;
    private int _completedReadCount;
    private int _disposeCount;
    private int _currentConcurrentReads;
    private int _maxConcurrentReads;

    // Managed thread ids are positive, so 0 stands for "not called yet".
    private int _openThreadId;
    private int _lastReadThreadId;
    private int _disposeThreadId;

    /// <summary>
    /// Creates a source whose CPU and RAM readings are both available, at
    /// <see cref="DefaultCpuCelsius"/> and <see cref="DefaultRamCelsius"/>.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock that stamps each reading's capture time; <see cref="TimeProvider.System"/> when
    /// <see langword="null"/>. Tests pass the same provider, typically a fake one, as the monitor.
    /// </param>
    public FakeSensorSource(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets or sets the exception <see cref="Open"/> throws after counting the call, or
    /// <see langword="null"/> for an open that succeeds.
    /// </summary>
    public Exception? OpenException
    {
        get => Volatile.Read(ref _openException);
        set => Volatile.Write(ref _openException, value);
    }

    /// <summary>
    /// Gets or sets the exception <see cref="Read"/> throws after counting, delaying and blocking, or
    /// <see langword="null"/> for reads that return a snapshot.
    /// </summary>
    public Exception? ReadException
    {
        get => Volatile.Read(ref _readException);
        set => Volatile.Write(ref _readException, value);
    }

    /// <summary>
    /// Gets or sets the real-time delay of every read, applied once reads are not blocked. Defaults to
    /// zero. <see cref="Dispose"/> cuts a delay short, and a new value applies to reads already waiting.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value assigned is negative.</exception>
    public TimeSpan ReadDelay
    {
        get
        {
            lock (_gate)
            {
                return _readDelay;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            lock (_gate)
            {
                _readDelay = value;

                // A read already waiting re-evaluates its deadline against the new delay.
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// Gets or sets the offset added to the provider's current time to stamp each reading. Defaults to
    /// zero; a negative value fabricates an old reading, for example -4 s yields one the monitor must
    /// re-mark <see cref="SensorStatus.Stale"/>.
    /// </summary>
    public TimeSpan CapturedAtOffset
    {
        get
        {
            lock (_gate)
            {
                return _capturedAtOffset;
            }
        }

        set
        {
            lock (_gate)
            {
                _capturedAtOffset = value;
            }
        }
    }

    /// <summary>
    /// Gets the number of <see cref="Open"/> calls made before disposal.
    /// </summary>
    public int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>
    /// Gets the number of reads entered, counted on entry before any block or delay.
    /// </summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    /// <summary>
    /// Gets the number of entered reads that have returned or thrown.
    /// </summary>
    public int CompletedReadCount => Volatile.Read(ref _completedReadCount);

    /// <summary>
    /// Gets the number of <see cref="Dispose"/> calls; every call is counted, repeated ones included.
    /// </summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>
    /// Gets the number of reads in flight right now.
    /// </summary>
    public int CurrentConcurrentReads => Volatile.Read(ref _currentConcurrentReads);

    /// <summary>
    /// Gets the largest number of reads ever in flight at once. The monitor never overlaps reads, so a
    /// value above 1 is a defect in the caller.
    /// </summary>
    public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

    /// <summary>
    /// Gets the managed thread id of the latest <see cref="Open"/> call, or <see langword="null"/> before the first.
    /// </summary>
    public int? OpenThreadId => ThreadIdOrNull(Volatile.Read(ref _openThreadId));

    /// <summary>
    /// Gets the managed thread id of the latest read entered, or <see langword="null"/> before the first.
    /// </summary>
    public int? LastReadThreadId => ThreadIdOrNull(Volatile.Read(ref _lastReadThreadId));

    /// <summary>
    /// Gets the managed thread id of the latest <see cref="Dispose"/> call, or <see langword="null"/> before the first.
    /// </summary>
    public int? DisposeThreadId => ThreadIdOrNull(Volatile.Read(ref _disposeThreadId));

    /// <summary>
    /// Creates a source whose CPU and RAM readings are both unavailable with the same status, as on a
    /// machine without the PawnIO driver.
    /// </summary>
    /// <param name="status">The status of both readings: <see cref="SensorStatus.DriverUnavailable"/>, <see cref="SensorStatus.NoSensor"/> or <see cref="SensorStatus.Failed"/>.</param>
    /// <param name="timeProvider">The clock that stamps capture times; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <returns>A new source reporting <paramref name="status"/> for both sensors.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the three unavailable statuses a source reports.</exception>
    public static FakeSensorSource AllUnavailable(SensorStatus status = SensorStatus.DriverUnavailable, TimeProvider? timeProvider = null)
    {
        if (status is not (SensorStatus.DriverUnavailable or SensorStatus.NoSensor or SensorStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Both sensors can be made unavailable only as DriverUnavailable, NoSensor or Failed.");
        }

        var source = new FakeSensorSource(timeProvider);
        source.SetCpu(status);
        source.SetRam(status);
        return source;
    }

    /// <summary>
    /// Makes the CPU reading available at <paramref name="celsius"/> from the next read on.
    /// </summary>
    /// <param name="celsius">The CPU temperature in degrees Celsius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="celsius"/> is NaN or infinite.</exception>
    public void SetCpu(double celsius)
    {
        RequireFinite(celsius);
        lock (_gate)
        {
            _cpu = new SensorScript(celsius, SensorStatus.Available);
        }
    }

    /// <summary>
    /// Makes the RAM reading available at <paramref name="celsius"/> from the next read on.
    /// </summary>
    /// <param name="celsius">The RAM temperature in degrees Celsius, the maximum across DIMMs.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="celsius"/> is NaN or infinite.</exception>
    public void SetRam(double celsius)
    {
        RequireFinite(celsius);
        lock (_gate)
        {
            _ram = new SensorScript(celsius, SensorStatus.Available);
        }
    }

    /// <summary>
    /// Sets the status of the CPU reading from the next read on.
    /// </summary>
    /// <param name="status">
    /// <see cref="SensorStatus.DriverUnavailable"/>, <see cref="SensorStatus.NoSensor"/> or
    /// <see cref="SensorStatus.Failed"/> make the reading unavailable; <see cref="SensorStatus.Available"/>
    /// restores it at the last scripted temperature.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="SensorStatus.Stale"/>, <see cref="SensorStatus.Hung"/> or undefined.
    /// </exception>
    public void SetCpu(SensorStatus status)
    {
        RequireScriptable(status);
        lock (_gate)
        {
            _cpu = _cpu with { Status = status };
        }
    }

    /// <summary>
    /// Sets the status of the RAM reading from the next read on.
    /// </summary>
    /// <param name="status">
    /// <see cref="SensorStatus.DriverUnavailable"/>, <see cref="SensorStatus.NoSensor"/> or
    /// <see cref="SensorStatus.Failed"/> make the reading unavailable; <see cref="SensorStatus.Available"/>
    /// restores it at the last scripted temperature.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="SensorStatus.Stale"/>, <see cref="SensorStatus.Hung"/> or undefined.
    /// </exception>
    public void SetRam(SensorStatus status)
    {
        RequireScriptable(status);
        lock (_gate)
        {
            _ram = _ram with { Status = status };
        }
    }

    /// <summary>
    /// Makes subsequent reads, and reads already in flight, wait until <see cref="ReleaseReads"/> or
    /// <see cref="Dispose"/>: the never-returning read that drives the monitor's hung-source path.
    /// </summary>
    public void BlockReads()
    {
        lock (_gate)
        {
            _blocked = true;
        }
    }

    /// <summary>
    /// Lets blocked reads continue: each then waits out <see cref="ReadDelay"/> and completes normally.
    /// </summary>
    public void ReleaseReads()
    {
        lock (_gate)
        {
            _blocked = false;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Blocks the caller until at least <paramref name="count"/> reads have been entered, or until
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="count">The <see cref="ReadCount"/> to wait for.</param>
    /// <param name="timeout">The longest real time to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <returns><see langword="true"/> when <see cref="ReadCount"/> reached <paramref name="count"/>; <see langword="false"/> on timeout.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public bool WaitForReadCount(int count, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        long startedAt = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // Read increments the count and pulses under _gate, so no entry is missed between checks.
            while (_readCount < count)
            {
                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    Monitor.Wait(_gate);
                    continue;
                }

                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_gate, ToWaitMilliseconds(remaining));
            }

            return true;
        }
    }

    /// <summary>
    /// Counts the call, records its thread, then throws <see cref="OpenException"/> when one is set.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    public void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _openCount);
            Volatile.Write(ref _openThreadId, Environment.CurrentManagedThreadId);
        }

        if (OpenException is { } openException)
        {
            throw openException;
        }
    }

    /// <summary>
    /// Counts the read, waits while reads are blocked, waits out <see cref="ReadDelay"/>, then throws
    /// <see cref="ReadException"/> when one is set or returns the scripted readings, stamped with the
    /// provider's time at return plus <see cref="CapturedAtOffset"/>.
    /// </summary>
    /// <returns>The scripted CPU and RAM readings.</returns>
    /// <exception cref="ObjectDisposedException">The source was disposed before or during the read.</exception>
    public SensorReadings Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _readCount);
            RaiseMaxConcurrentReads(Interlocked.Increment(ref _currentConcurrentReads));
            Volatile.Write(ref _lastReadThreadId, Environment.CurrentManagedThreadId);
            Monitor.PulseAll(_gate);
        }

        try
        {
            SensorScript cpu;
            SensorScript ram;
            TimeSpan capturedAtOffset;
            lock (_gate)
            {
                WaitWhileBlockedOrDelayed();
                ObjectDisposedException.ThrowIf(_disposed, this);
                cpu = _cpu;
                ram = _ram;
                capturedAtOffset = _capturedAtOffset;
            }

            // Thrown outside _gate, so the test's handling of it can never contend with the fake's lock.
            if (ReadException is { } readException)
            {
                throw readException;
            }

            DateTimeOffset capturedAt = _timeProvider.GetUtcNow() + capturedAtOffset;
            return new SensorReadings(cpu.ToValue(capturedAt), ram.ToValue(capturedAt));
        }
        finally
        {
            Interlocked.Decrement(ref _currentConcurrentReads);
            Interlocked.Increment(ref _completedReadCount);
        }
    }

    /// <summary>
    /// Marks the source disposed and wakes every blocked or delayed read, which then throws
    /// <see cref="ObjectDisposedException"/>. Never throws; every call is counted.
    /// </summary>
    /// <remarks>
    /// Nothing is released: the fake waits on its own lock only, so a reader still waking up never
    /// touches a disposed primitive.
    /// </remarks>
    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        Volatile.Write(ref _disposeThreadId, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private static void RequireFinite(double celsius)
    {
        if (!double.IsFinite(celsius))
        {
            throw new ArgumentOutOfRangeException(nameof(celsius), celsius, "A scripted temperature must be a finite number.");
        }
    }

    private static void RequireScriptable(SensorStatus status)
    {
        if (status is not (SensorStatus.Available or SensorStatus.DriverUnavailable or SensorStatus.NoSensor or SensorStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A source reports only Available, DriverUnavailable, NoSensor or Failed; Stale and Hung are assigned by SensorMonitor.");
        }
    }

    private static int? ThreadIdOrNull(int threadId) => threadId == 0 ? null : threadId;

    // Rounds up, so a sub-millisecond remainder waits 1 ms rather than spinning on zero-length waits.
    private static int ToWaitMilliseconds(TimeSpan remaining) =>
        (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);

    private void RaiseMaxConcurrentReads(int concurrentReads)
    {
        int max = Volatile.Read(ref _maxConcurrentReads);
        while (concurrentReads > max)
        {
            int observed = Interlocked.CompareExchange(ref _maxConcurrentReads, concurrentReads, max);
            if (observed == max)
            {
                return;
            }

            max = observed;
        }
    }

    // Called under _gate; Monitor.Wait releases it, so the test can script, release or dispose the
    // fake meanwhile. Returns once the source is disposed, or once reads are unblocked and the current
    // ReadDelay has elapsed since this read was first unblocked. A block applied during the delay is
    // honoured when the read next wakes.
    private void WaitWhileBlockedOrDelayed()
    {
        long? delayStartedAt = null;
        while (!_disposed)
        {
            if (_blocked)
            {
                Monitor.Wait(_gate);
                continue;
            }

            delayStartedAt ??= Stopwatch.GetTimestamp();
            TimeSpan remaining = _readDelay - Stopwatch.GetElapsedTime(delayStartedAt.Value);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            Monitor.Wait(_gate, ToWaitMilliseconds(remaining));
        }
    }

    // One sensor's scripted state. Celsius keeps the last scripted temperature while Status is
    // unavailable, so restoring Available brings it back.
    private readonly record struct SensorScript(double Celsius, SensorStatus Status)
    {
        public SensorValue ToValue(DateTimeOffset capturedAt) =>
            Status == SensorStatus.Available
                ? SensorValue.Available(Celsius, capturedAt)
                : SensorValue.Unavailable(Status, capturedAt);
    }
}
