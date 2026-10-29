namespace RamQuick.Sensors;

/// <summary>
/// Polls the CPU and RAM temperatures at 1 Hz on one dedicated background thread and publishes each
/// snapshot to a captured synchronization context, so neither the UI nor the memory test ever waits
/// on a sensor read.
/// </summary>
/// <remarks>
/// <para>
/// Read ownership: the monitor owns its <see cref="ISensorSource"/>. Its sensor thread is the only
/// caller of the source: it calls <see cref="ISensorSource.Open"/> once, performs the first read as
/// soon as the open returns, then one read per <see cref="PollInterval"/> tick, and finally disposes
/// the source when the monitor is disposed. Reads never overlap: a tick that arrives while a read is
/// in flight is skipped, never queued, and no second read is started to work around a slow one.
/// </para>
/// <para>
/// Freshness: an <see cref="SensorStatus.Available"/> value older than <see cref="StaleAfter"/> is
/// re-marked <see cref="SensorStatus.Stale"/>, so it shows N/A and its thermal limit is inactive until
/// a fresh reading arrives. A read still outstanding after <see cref="HungAfter"/> marks the source
/// <see cref="SensorStatus.Hung"/>: both readings are N/A and no further read is made for the life of
/// the process. The blocked thread is left parked; it is a background thread, so it never keeps the
/// process alive. Every age is measured on the <see cref="TimeProvider"/> passed to the constructor.
/// </para>
/// <para>
/// The monitor does not know the run state: gating the thermal check on the Running state and
/// calling <see cref="ThermalLimits.Evaluate"/> are the run controller's responsibility.
/// </para>
/// </remarks>
public sealed class SensorMonitor : IDisposable
{
    /// <summary>
    /// The interval between reads: 1 s.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The age beyond which an available reading is re-marked <see cref="SensorStatus.Stale"/>: 3 s.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a read may stay outstanding before the source is marked <see cref="SensorStatus.Hung"/>: 5 s.
    /// </summary>
    public static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest <see cref="Dispose"/> waits for the sensor thread to finish: 1 s.
    /// </summary>
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(1);

    private readonly ISensorSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly ManualResetEvent _disposeEvent = new(false);

    // Auto-reset, so ticks that arrive while the sensor thread is busy coalesce into one read.
    private readonly AutoResetEvent _tickEvent = new(false);
    private readonly WaitHandle[] _waitHandles;

    private SynchronizationContext? _context;
    private Thread? _thread;
    private ITimer? _timer;
    private DateTimeOffset? _readStartedAt;
    private long _publishedVersion;
    private long _deliveredVersion;
    private volatile SensorReadings _current;
    private volatile bool _hung;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a monitor over a sensor source. Nothing is opened or started until <see cref="Start"/>.
    /// </summary>
    /// <param name="source">
    /// The sensor source. The monitor takes ownership of it: only the monitor's sensor thread opens,
    /// reads and disposes it.
    /// </param>
    /// <param name="timeProvider">The clock that drives polling and measures reading and read ages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public SensorMonitor(ISensorSource source, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _source = source;
        _timeProvider = timeProvider;
        _waitHandles = [_disposeEvent, _tickEvent];
        _current = SensorReadings.Unavailable(SensorStatus.Stale, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Raised with each newly published snapshot: every completed read, about once per
    /// <see cref="PollInterval"/>, and every re-marking as <see cref="SensorStatus.Stale"/> or
    /// <see cref="SensorStatus.Hung"/>.
    /// </summary>
    /// <remarks>
    /// Always raised through <see cref="SynchronizationContext.Post"/> on the context passed to
    /// <see cref="Start"/>, never directly on the sensor or timer thread. A snapshot that reaches the
    /// context after a newer one has already been raised is dropped, so handlers never move back to an
    /// older snapshot. Nothing is raised after <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<SensorReadings>? ReadingsChanged;

    /// <summary>
    /// The latest published snapshot; safe to read from any thread.
    /// </summary>
    /// <remarks>
    /// Until the first read completes, both readings are <see cref="SensorStatus.Stale"/>: no fresh
    /// reading exists yet, so both show N/A and both thermal limits are inactive.
    /// </remarks>
    public SensorReadings Current => _current;

    /// <summary>
    /// <see langword="true"/> once a read has stayed outstanding for <see cref="HungAfter"/>. From then on
    /// both readings are <see cref="SensorStatus.Hung"/> and the source is never called again.
    /// </summary>
    public bool IsHung => _hung;

    /// <summary>
    /// Starts the sensor thread, which opens the source and reads it immediately, and the
    /// <see cref="PollInterval"/> timer that schedules every later read.
    /// </summary>
    /// <param name="context">The context, normally the UI thread's, on which <see cref="ReadingsChanged"/> is raised.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The monitor has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The monitor has been disposed.</exception>
    public void Start(SynchronizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException("The sensor monitor has already been started.");
            }

            _context = context;

            // Background, so a thread parked in a hung read can never keep the process alive.
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RAMQuick sensors",
                Priority = ThreadPriority.Normal,
            };
            thread.Start();
            _thread = thread;
            _timer = _timeProvider.CreateTimer(OnTick, null, PollInterval, PollInterval);
        }
    }

    /// <summary>
    /// Stops polling and closes the source, waiting at most <see cref="DisposeTimeout"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source is disposed on the sensor thread, never on the caller's, and only when it is not
    /// hung. A hung source is abandoned without being disposed, and its blocked thread stays parked.
    /// A read that is still running when the timeout expires completes in the background, after which
    /// the sensor thread disposes the source.
    /// </para>
    /// <para>
    /// When the monitor was never started, the source was never opened and is not disposed.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Thread? thread;
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            timer = _timer;
            _timer = null;
            _disposeEvent.Set();
        }

        timer?.Dispose();

        // No wait for a hung thread: it is parked in Read and will never touch the source again. No
        // wait from the sensor thread itself either, which is where a handler runs when the context
        // executes posts inline.
        bool threadFinished = thread is null
            || (!_hung && thread != Thread.CurrentThread && thread.Join(DisposeTimeout));

        // A thread still running may yet wait on these handles, so they are left to their finalizers.
        if (threadFinished)
        {
            _disposeEvent.Dispose();
            _tickEvent.Dispose();
        }
    }

    // Sensor thread entry point: the only code that calls the source.
    private void Run()
    {
        if (!TryOpen())
        {
            _disposeEvent.WaitOne();
            CloseSource();
            return;
        }

        // The immediate first read stands in for every tick that arrived while Open ran.
        _tickEvent.Reset();
        if (!TryRead())
        {
            return;
        }

        while (WaitHandle.WaitAny(_waitHandles) != 0)
        {
            if (!TryRead())
            {
                return;
            }
        }

        CloseSource();
    }

    private bool TryOpen()
    {
        try
        {
            _source.Open();
            return true;
        }
        catch (Exception)
        {
            // Per the ISensorSource contract a throwing Open means both readings Failed and no reads.
            var failed = SensorReadings.Unavailable(SensorStatus.Failed, _timeProvider.GetUtcNow());
            long version;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                version = SetCurrent(failed);
            }

            Post(failed, version);
            return false;
        }
    }

    // Performs one read. Returns false when the watchdog declared the source hung during the read:
    // the thread must then abandon the source without calling or disposing it again.
    private bool TryRead()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return true;
            }

            _readStartedAt = _timeProvider.GetUtcNow();
        }

        SensorReadings? readings;
        try
        {
            readings = _source.Read();
        }
        catch (Exception)
        {
            // A throwing source must never end the sensor thread; the exception is that tick's Failed result.
            readings = null;
        }

        SensorReadings snapshot;
        long version;
        lock (_gate)
        {
            _readStartedAt = null;
            if (_hung)
            {
                return false;
            }

            if (_disposed)
            {
                return true;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            snapshot = readings is null
                ? SensorReadings.Unavailable(SensorStatus.Failed, now)
                : MarkStale(readings, now);
            version = SetCurrent(snapshot);
        }

        Post(snapshot, version);
        return true;
    }

    // Timer callback: schedules reads and acts as the watchdog. FakeTimeProvider runs it synchronously
    // inside Advance, so it must stay short and never block or call the source.
    private void OnTick(object? state)
    {
        SensorReadings snapshot;
        long version;
        lock (_gate)
        {
            if (_disposed || _hung)
            {
                return;
            }

            if (_readStartedAt is not { } readStartedAt)
            {
                _tickEvent.Set();
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now - readStartedAt >= HungAfter)
            {
                _hung = true;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                snapshot = SensorReadings.Unavailable(SensorStatus.Hung, now);
            }
            else
            {
                SensorReadings current = _current;
                snapshot = MarkStale(current, now);
                if (ReferenceEquals(snapshot, current))
                {
                    return;
                }
            }

            version = SetCurrent(snapshot);
        }

        Post(snapshot, version);
    }

    private static SensorReadings MarkStale(SensorReadings readings, DateTimeOffset now)
    {
        SensorValue cpu = MarkStale(readings.Cpu, now);
        SensorValue ram = MarkStale(readings.Ram, now);
        return ReferenceEquals(cpu, readings.Cpu) && ReferenceEquals(ram, readings.Ram)
            ? readings
            : new SensorReadings(cpu, ram);
    }

    private static SensorValue MarkStale(SensorValue value, DateTimeOffset now) =>
        value.IsAvailable && now - value.CapturedAt > StaleAfter ? value.WithStatus(SensorStatus.Stale) : value;

    // Called under _gate.
    private long SetCurrent(SensorReadings snapshot)
    {
        _current = snapshot;
        return ++_publishedVersion;
    }

    // Called outside _gate: an inline context runs handlers right here, and a handler that calls
    // Dispose must not find the lock held.
    private void Post(SensorReadings snapshot, long version) =>
        _context!.Post(_ => Deliver(snapshot, version), null);

    // The sensor thread and the watchdog tick both publish, and their posts can reach the context in a
    // different order from their updates of _current. Raising only snapshots newer than the last one
    // raised keeps handlers from ending on an older snapshot than Current.
    private void Deliver(SensorReadings snapshot, long version)
    {
        if (_disposed)
        {
            return;
        }

        long delivered = Interlocked.Read(ref _deliveredVersion);
        while (version > delivered)
        {
            long observed = Interlocked.CompareExchange(ref _deliveredVersion, version, delivered);
            if (observed == delivered)
            {
                ReadingsChanged?.Invoke(this, snapshot);
                return;
            }

            delivered = observed;
        }
    }

    private void CloseSource()
    {
        try
        {
            _source.Dispose();
        }
        catch (Exception)
        {
            // The source closes only as the monitor shuts down, so nothing remains to report a failure
            // to, and an exception escaping this background thread would terminate the process.
        }
    }
}
