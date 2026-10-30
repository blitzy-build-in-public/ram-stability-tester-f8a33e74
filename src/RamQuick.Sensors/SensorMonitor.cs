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
/// the source when the monitor is disposed, unless the source hung. Reads never overlap: a tick that
/// arrives while the open or a read is in flight is skipped, never queued, and no second read is
/// started to work around a slow one.
/// </para>
/// <para>
/// Freshness: an <see cref="SensorStatus.Available"/> value older than <see cref="StaleAfter"/> is
/// re-marked <see cref="SensorStatus.Stale"/> as soon as it is that old, not at the next tick, so it
/// shows N/A and its thermal limit is inactive until a fresh reading arrives. A call to
/// <see cref="ISensorSource.Open"/> or <see cref="ISensorSource.Read"/> still outstanding after
/// <see cref="HungAfter"/> marks the source <see cref="SensorStatus.Hung"/>: both readings are N/A,
/// polling stops, and the source is never called again, not even to dispose it, for the life of the
/// process. The blocked thread is left parked; it is a background thread, so it never keeps the
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
    /// How long the open or a read may stay outstanding before the source is marked <see cref="SensorStatus.Hung"/>: 5 s.
    /// </summary>
    public static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest <see cref="Dispose"/> waits for the sensor thread to finish: 1 s.
    /// </summary>
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(1);

    // The longest due time ITimer.Change accepts.
    private const long MaxTimerDueMilliseconds = 4294967294;

    private readonly ISensorSource _source;
    private readonly TimeProvider _timeProvider;

    // The bounded wait Dispose performs for the sensor thread: Thread.Join outside tests.
    private readonly Func<Thread, TimeSpan, bool> _waitForSensorThread;
    private readonly object _gate = new();
    private readonly ManualResetEvent _disposeEvent = new(false);

    // Auto-reset, so ticks that arrive while the sensor thread is busy coalesce into one read.
    private readonly AutoResetEvent _tickEvent = new(false);
    private readonly WaitHandle[] _waitHandles;

    private SynchronizationContext? _context;
    private Thread? _thread;
    private ITimer? _timer;

    // One-shot, for the next watchdog or staleness deadline, so neither waits for a poll tick.
    private ITimer? _deadlineTimer;

    // When the source call in flight, Open or Read, began; null while the sensor thread is outside the source.
    private DateTimeOffset? _callStartedAt;
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
        : this(source, timeProvider, static (thread, timeout) => thread.Join(timeout))
    {
    }

    /// <summary>
    /// Creates a monitor over a sensor source with a substitute for the bounded wait
    /// <see cref="Dispose"/> performs for the sensor thread, so tests can check the budget that wait is
    /// given without spending it in real time.
    /// </summary>
    /// <param name="source">
    /// The sensor source. The monitor takes ownership of it: only the monitor's sensor thread opens,
    /// reads and disposes it.
    /// </param>
    /// <param name="timeProvider">The clock that drives polling and measures reading and read ages.</param>
    /// <param name="waitForSensorThread">
    /// Waits for the given sensor thread to finish, for at most the given time, and returns
    /// <see langword="true"/> when the thread finished. <see cref="Dispose"/> calls it at most once, with
    /// <see cref="DisposeTimeout"/>, and never for a hung thread or from the sensor thread itself. The
    /// public constructor passes <see cref="Thread.Join(TimeSpan)"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/>, <paramref name="timeProvider"/> or <paramref name="waitForSensorThread"/> is <see langword="null"/>.
    /// </exception>
    internal SensorMonitor(ISensorSource source, TimeProvider timeProvider, Func<Thread, TimeSpan, bool> waitForSensorThread)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(waitForSensorThread);

        _source = source;
        _timeProvider = timeProvider;
        _waitForSensorThread = waitForSensorThread;
        _waitHandles = [_disposeEvent, _tickEvent];
        _current = SensorReadings.Unavailable(SensorStatus.Stale, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Raised with each newly published snapshot: every completed read, about once per
    /// <see cref="PollInterval"/>, and every re-marking as <see cref="SensorStatus.Stale"/> or
    /// <see cref="SensorStatus.Hung"/>, each raised when its deadline passes rather than at the next tick.
    /// </summary>
    /// <remarks>
    /// Always raised through <see cref="SynchronizationContext.Post"/> on the context passed to
    /// <see cref="Start"/>, never directly on the sensor or timer thread. A snapshot that reaches the
    /// context after a newer one has already been raised is dropped, so handlers never move back to an
    /// older snapshot. A snapshot whose post the context rejects by throwing is not raised; polling
    /// continues and <see cref="Current"/> still reflects it. Nothing is raised after <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<SensorReadings>? ReadingsChanged;

    /// <summary>
    /// The latest snapshot, with every <see cref="SensorStatus.Available"/> reading older than
    /// <see cref="StaleAfter"/> re-marked <see cref="SensorStatus.Stale"/>; safe to read from any thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until the first read completes, both readings are <see cref="SensorStatus.Stale"/>: no fresh
    /// reading exists yet, so both show N/A and both thermal limits are inactive.
    /// </para>
    /// <para>
    /// Staleness is applied each time this property is read, so it never reports an
    /// <see cref="SensorStatus.Available"/> reading older than <see cref="StaleAfter"/>, not even in the
    /// moment before the re-marked snapshot is published and raised through <see cref="ReadingsChanged"/>.
    /// </para>
    /// </remarks>
    public SensorReadings Current => MarkStale(_current, _timeProvider.GetUtcNow());

    /// <summary>
    /// <see langword="true"/> once <see cref="ISensorSource.Open"/> or a read has stayed outstanding for
    /// <see cref="HungAfter"/>. From then on both readings are <see cref="SensorStatus.Hung"/> and the
    /// source is never called again.
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

            // Created idle; armed under this lock whenever the call in flight or the published snapshot changes.
            _deadlineTimer = _timeProvider.CreateTimer(OnDeadline, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Stops polling and closes the source, waiting at most <see cref="DisposeTimeout"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source is disposed on the sensor thread, never on the caller's, and only when it is not
    /// hung. A hung source is abandoned without being disposed, and its blocked thread stays parked.
    /// An open or a read that is still running when the timeout expires completes in the background,
    /// after which the sensor thread disposes the source without reading it again.
    /// </para>
    /// <para>
    /// When the monitor was never started, the source was never opened and is not disposed.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Thread? thread;
        ITimer? timer;
        ITimer? deadlineTimer;
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
            deadlineTimer = _deadlineTimer;
            _deadlineTimer = null;
            _disposeEvent.Set();
        }

        timer?.Dispose();
        deadlineTimer?.Dispose();

        // No wait for a hung thread: it is parked in Open or Read and will never touch the source again. No
        // wait from the sensor thread itself either, which is where a handler runs when the context
        // executes posts inline.
        bool threadFinished = thread is null
            || (!_hung && thread != Thread.CurrentThread && _waitForSensorThread(thread, DisposeTimeout));

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
        OpenOutcome outcome = TryOpen();
        if (outcome == OpenOutcome.Hung)
        {
            // Declared hung while Open ran: the source is abandoned, never read or disposed.
            return;
        }

        if (outcome == OpenOutcome.Failed)
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

    // Opens the source under the watchdog. Hung means the watchdog declared the source hung while Open
    // ran: nothing is published for the open, and the thread must abandon the source.
    private OpenOutcome TryOpen()
    {
        lock (_gate)
        {
            // No reading exists before the first read, so the watchdog's is the only deadline to arm.
            DateTimeOffset startedAt = _timeProvider.GetUtcNow();
            _callStartedAt = startedAt;
            ArmDeadline(startedAt);
        }

        bool opened;
        try
        {
            _source.Open();
            opened = true;
        }
        catch (Exception)
        {
            // Per the ISensorSource contract a throwing Open means both readings Failed and no reads.
            opened = false;
        }

        SensorReadings failed;
        long version;
        lock (_gate)
        {
            _callStartedAt = null;
            if (_hung)
            {
                return OpenOutcome.Hung;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (opened || _disposed)
            {
                ArmDeadline(now);
                return opened ? OpenOutcome.Opened : OpenOutcome.Failed;
            }

            failed = SensorReadings.Unavailable(SensorStatus.Failed, now);
            version = SetCurrent(failed);
            ArmDeadline(now);
        }

        Post(failed, version);
        return OpenOutcome.Failed;
    }

    // Performs one read. Returns false when the watchdog declared the source hung during the read:
    // the thread must then abandon the source without calling or disposing it again.
    private bool TryRead()
    {
        SensorReadings? remarked;
        long remarkedVersion;
        lock (_gate)
        {
            if (_disposed)
            {
                return true;
            }

            DateTimeOffset startedAt = _timeProvider.GetUtcNow();
            _callStartedAt = startedAt;

            // A staleness deadline the deadline timer has not handled yet is applied here first, so the
            // timer is only ever armed for a deadline still ahead.
            remarked = ApplyDeadlines(startedAt, out remarkedVersion);
            ArmDeadline(startedAt);
        }

        if (remarked is not null)
        {
            Post(remarked, remarkedVersion);
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
            _callStartedAt = null;
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
            ArmDeadline(now);
        }

        Post(snapshot, version);
        return true;
    }

    // Poll timer callback: signals the next read when no source call is in flight. It also applies any
    // due deadline, exactly as the deadline timer does, so the outcome is the same whichever of the two
    // runs first. It re-arms the deadline timer only when it changed the snapshot: an unchanged deadline
    // keeps its timer as armed. FakeTimeProvider runs it synchronously inside Advance, so it must stay
    // short and never block or call the source.
    private void OnTick(object? state)
    {
        SensorReadings? snapshot;
        long version;
        lock (_gate)
        {
            if (_disposed || _hung)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            snapshot = ApplyDeadlines(now, out version);
            if (snapshot is not null)
            {
                ArmDeadline(now);
            }

            if (_callStartedAt is null)
            {
                _tickEvent.Set();
            }
        }

        if (snapshot is not null)
        {
            Post(snapshot, version);
        }
    }

    // Deadline timer callback: the watchdog and the staleness re-marking. FakeTimeProvider runs it
    // synchronously inside Advance, so it must stay short and never block or call the source.
    private void OnDeadline(object? state)
    {
        SensorReadings? snapshot;
        long version;
        lock (_gate)
        {
            // Re-armed even when nothing was due yet: a timer that fired early has nothing else pending.
            DateTimeOffset now = _timeProvider.GetUtcNow();
            snapshot = ApplyDeadlines(now, out version);
            ArmDeadline(now);
        }

        if (snapshot is not null)
        {
            Post(snapshot, version);
        }
    }

    // Called under _gate. Applies every deadline passed by now: the watchdog's, which marks the source
    // hung for good, or a reading's staleness. Returns the snapshot to post, with its version, or null
    // when nothing changed.
    private SensorReadings? ApplyDeadlines(DateTimeOffset now, out long version)
    {
        version = 0;
        if (_disposed || _hung)
        {
            return null;
        }

        if (_callStartedAt is { } callStartedAt && now - callStartedAt >= HungAfter)
        {
            // The source is never called again, so nothing is left to schedule.
            _hung = true;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _deadlineTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var hung = SensorReadings.Unavailable(SensorStatus.Hung, now);
            version = SetCurrent(hung);
            return hung;
        }

        SensorReadings current = _current;
        SensorReadings marked = MarkStale(current, now);
        if (ReferenceEquals(marked, current))
        {
            return null;
        }

        version = SetCurrent(marked);
        return marked;
    }

    // Called under _gate whenever the call in flight or _current changed, or the timer fired, with every
    // deadline up to now already applied, so the next one always lies ahead of now. Arms the deadline
    // timer for it, or idles the timer when nothing is pending.
    private void ArmDeadline(DateTimeOffset now)
    {
        if (_deadlineTimer is not { } deadlineTimer || _hung)
        {
            return;
        }

        TimeSpan? dueIn = Earliest(
            _callStartedAt is { } callStartedAt ? HungAfter - (now - callStartedAt) : null,
            Earliest(TimeUntilStale(_current.Cpu, now), TimeUntilStale(_current.Ram, now)));
        deadlineTimer.Change(dueIn is { } due ? ToTimerDueTime(due) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // One tick past StaleAfter: a reading exactly StaleAfter old is still fresh.
    private static TimeSpan? TimeUntilStale(SensorValue value, DateTimeOffset now) =>
        value.IsAvailable ? value.CapturedAt - now + StaleAfter + TimeSpan.FromTicks(1) : null;

    private static TimeSpan? Earliest(TimeSpan? first, TimeSpan? second) =>
        first is null || second < first ? second : first;

    // Real timers count whole milliseconds and truncate the rest, so the due time is rounded up rather
    // than firing before the deadline. It is never zero either: FakeTimeProvider runs a timer that is
    // due at once inside Change, on the calling thread, which holds _gate.
    private static TimeSpan ToTimerDueTime(TimeSpan dueIn)
    {
        long milliseconds = (dueIn.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
        return TimeSpan.FromTicks(Math.Clamp(milliseconds, 1, MaxTimerDueMilliseconds) * TimeSpan.TicksPerMillisecond);
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
    private void Post(SensorReadings snapshot, long version)
    {
        try
        {
            _context!.Post(_ => Deliver(snapshot, version), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A context that rejects the post, for example one already shut down, costs only this
            // notification: Current already holds the snapshot. Polling must go on, and an exception
            // escaping the sensor or timer thread would terminate the process.
        }
    }

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

    private enum OpenOutcome
    {
        Opened,
        Failed,
        Hung,
    }
}
