using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// How an acknowledged journal record ended up, as reported by <see cref="JournalAck"/> and by the awaitable
/// <see cref="ProgressJournal"/> methods.
/// </summary>
public enum JournalAckStatus
{
    /// <summary>
    /// The record is durable on disk: its append was written through and flushed. For <c>end</c>, the journal
    /// was also deleted.
    /// </summary>
    Acknowledged = 0,

    /// <summary>
    /// The record could not be made durable, or the journal was closed before it could be written. The
    /// acknowledgment's <see cref="JournalAck.Failure"/> says why.
    /// </summary>
    Failed = 1,

    /// <summary>
    /// <see cref="JournalAck.Wait(WaitHandle, TimeSpan)"/> returned because its stop event was signalled before
    /// the record was acknowledged.
    /// </summary>
    Stopped = 2,

    /// <summary>
    /// The record was not acknowledged within the wait's bound, normally
    /// <see cref="ProgressJournal.AcknowledgmentTimeout"/>.
    /// </summary>
    TimedOut = 3,
}

/// <summary>
/// Why the progress journal could not write a record.
/// </summary>
/// <param name="RecordType">
/// The journal record type that failed, its <c>"t"</c> discriminator: <c>begin</c>, <c>prepared</c>, <c>step</c>,
/// <c>stepEnd</c>, <c>beat</c>, <c>result</c>, <c>released</c> or <c>end</c>. A failure to delete the journal
/// reports <c>end</c> after the <c>end</c> record and <c>begin</c> when a run whose <c>begin</c> was not acknowledged
/// is cleaned up; a failure to close the storage reports <c>close</c>.
/// </param>
/// <param name="Message">
/// A short English description of the error, suitable for "Progress journal could not be written: &lt;error&gt;".
/// </param>
/// <param name="Exception">The exception that caused the failure, or <see langword="null"/> when there was none.</param>
/// <param name="OccurredUtc">The UTC time the failure was observed, from the journal's <see cref="TimeProvider"/>.</param>
public sealed record JournalFailure(string RecordType, string Message, Exception? Exception, DateTimeOffset OccurredUtc);

/// <summary>
/// The durable acknowledgment of one journal record: completed exactly once by the journal's writer thread, as
/// success once the record is durable, or as failure carrying a <see cref="JournalFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// An acknowledgment holds no reference to the journal, its writer or any caller, so a run object that only an
/// acknowledgment can reach is collectable. The writer drops its reference once the record is written.
/// </para>
/// <para>
/// Threading: every member may be called from any thread, before and after completion. Continuations of
/// <see cref="Completion"/> never run on the writer thread.
/// </para>
/// </remarks>
public sealed class JournalAck
{
    private readonly TaskCompletionSource<JournalAckStatus> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The Wait calls that found the acknowledgment pending, newest first, which Complete takes all at once and signals.
    // Pushed and taken with Interlocked rather than under a lock: a waiter woken by Complete would contend for a lock
    // Complete still held, and a contended lock gets a kernel event that only a garbage collection releases.
    private Waiter? _waiters;

    private JournalFailure? _failure;
    private int _completed;

    internal JournalAck()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the writer has completed the acknowledgment, as success or failure.
    /// </summary>
    public bool IsCompleted => _completion.Task.IsCompleted;

    /// <summary>
    /// Gets why the record could not be written, or <see langword="null"/> while the acknowledgment is pending
    /// and after a success.
    /// </summary>
    public JournalFailure? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// Gets a task that completes with <see cref="JournalAckStatus.Acknowledged"/> or
    /// <see cref="JournalAckStatus.Failed"/> when the writer completes the acknowledgment. It never faults and is
    /// never cancelled; a caller that must bound its wait applies its own timeout.
    /// </summary>
    public Task<JournalAckStatus> Completion => _completion.Task;

    /// <summary>
    /// Blocks the calling thread until the record is acknowledged, <paramref name="stopEvent"/> is signalled or
    /// <paramref name="timeout"/> elapses, whichever comes first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on the engine coordinator inside the step gate with the session's managed stop event and
    /// <see cref="ProgressJournal.AcknowledgmentTimeout"/>. The timeout is real time, never the journal's
    /// <see cref="TimeProvider"/>. An acknowledgment completed before the call returns its status at once and
    /// creates nothing. A stop event already disposed when the wait reaches it is treated as signalled; one disposed
    /// while the wait is blocked stays open until the wait ends, because the wait holds a reference to it. Nothing
    /// here throws on a completed or disposed state.
    /// </para>
    /// <para>
    /// A pending acknowledgment is waited on through a kernel event of the call's own, which the writer sets once it
    /// completes the acknowledgment and which is disposed before the call returns, so no kernel object outlives the
    /// wait. The completion task's own wait handle is not used: the task would keep that event until finalization,
    /// one more per step for as long as no garbage collection runs. Several threads may wait at once.
    /// </para>
    /// </remarks>
    /// <param name="stopEvent">The event that ends the wait early; it is only waited on, never set or reset.</param>
    /// <param name="timeout">
    /// The longest time to wait, at most <see cref="int.MaxValue"/> milliseconds, or
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </param>
    /// <returns>
    /// <see cref="JournalAckStatus.Acknowledged"/> or <see cref="JournalAckStatus.Failed"/> once the writer has
    /// completed the acknowledgment; <see cref="JournalAckStatus.Stopped"/> when the stop event was signalled
    /// first; <see cref="JournalAckStatus.TimedOut"/> when neither happened within <paramref name="timeout"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stopEvent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative and not infinite, or longer than <see cref="int.MaxValue"/>
    /// milliseconds.
    /// </exception>
    public JournalAckStatus Wait(WaitHandle stopEvent, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(stopEvent);
        if (timeout != Timeout.InfiniteTimeSpan
            && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The timeout must be non-negative and at most int.MaxValue milliseconds, or infinite.");
        }

        Task<JournalAckStatus> completion = _completion.Task;
        if (completion.IsCompleted)
        {
            return completion.Result;
        }

        var waiter = new Waiter();
        try
        {
            Waiter? head;
            do
            {
                head = Volatile.Read(ref _waiters);
                waiter.Next = head;
            }
            while (Interlocked.CompareExchange(ref _waiters, waiter, head) != head);

            // Pushed before this check, and Complete takes the waiters only after completing the task, so either the
            // completion is seen here or Complete signals this waiter.
            if (completion.IsCompleted)
            {
                return completion.Result;
            }

            int signalled = WaitHandle.WaitAny(new[] { waiter.Signal, stopEvent }, timeout);
            if (signalled == 0 || completion.IsCompleted)
            {
                return completion.Result;
            }

            return signalled == WaitHandle.WaitTimeout ? JournalAckStatus.TimedOut : JournalAckStatus.Stopped;
        }
        catch (ObjectDisposedException)
        {
            return completion.IsCompleted ? completion.Result : JournalAckStatus.Stopped;
        }
        finally
        {
            waiter.Leave();
        }
    }

    /// <summary>
    /// Creates an acknowledgment that is already completed as failed, for records offered to a journal that is
    /// closed or disposed.
    /// </summary>
    /// <param name="failure">Why the record was not written.</param>
    /// <returns>The completed acknowledgment.</returns>
    internal static JournalAck CreateFailed(JournalFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var ack = new JournalAck();
        ack.Complete(failure);
        return ack;
    }

    /// <summary>
    /// Completes the acknowledgment once; later calls are ignored.
    /// </summary>
    /// <param name="failure">Why the record was not written, or <see langword="null"/> when it is durable.</param>
    internal void Complete(JournalFailure? failure)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        // Published before the task completes, so a caller that sees Failed also sees the failure.
        Volatile.Write(ref _failure, failure);
        _completion.TrySetResult(failure is null ? JournalAckStatus.Acknowledged : JournalAckStatus.Failed);

        // After the task completes, so every waiter woken here finds its result.
        for (Waiter? waiter = Interlocked.Exchange(ref _waiters, null); waiter is not null; waiter = waiter.Next)
        {
            waiter.Wake();
        }
    }

    /// <summary>
    /// One <see cref="Wait(WaitHandle, TimeSpan)"/> call's kernel event. The call disposes it as it returns;
    /// <see cref="Complete"/> only sets it. The state keeps the two apart without a lock: the event is set only while
    /// its call still waits, and disposed only once no set is in progress.
    /// </summary>
    private sealed class Waiter
    {
        private const int Waiting = 0;
        private const int Waking = 1;
        private const int Done = 2;

        private readonly ManualResetEvent _signal = new(initialState: false);
        private int _state;

        /// <summary>Gets the event the call waits on.</summary>
        public WaitHandle Signal => _signal;

        /// <summary>Gets or sets the waiter pushed before this one; written only before this one is pushed.</summary>
        public Waiter? Next { get; set; }

        /// <summary>Sets the event, unless its call has already left.</summary>
        public void Wake()
        {
            if (Interlocked.CompareExchange(ref _state, Waking, Waiting) != Waiting)
            {
                return;
            }

            try
            {
                _signal.Set();
            }
            finally
            {
                Volatile.Write(ref _state, Done);
            }
        }

        /// <summary>
        /// Ends the call's interest in the event and disposes it, first waiting out a <see cref="Wake"/> already
        /// setting it, which takes one system call.
        /// </summary>
        public void Leave()
        {
            if (Interlocked.CompareExchange(ref _state, Done, Waiting) == Waking)
            {
                var spinner = default(SpinWait);
                while (Volatile.Read(ref _state) == Waking)
                {
                    spinner.SpinOnce();
                }
            }

            _signal.Dispose();
        }
    }
}

/// <summary>
/// The durable progress journal of one run, <c>&lt;data&gt;\journal\active-run.jsonl</c>: it records the run as
/// JSON Lines on its own writer thread so that a crash, hang or power loss leaves enough on disk for the next
/// launch to report the run.
/// </summary>
/// <remarks>
/// <para>
/// Records are written in the order they are offered: <c>begin</c>, <c>prepared</c>, then <c>step</c> and
/// <c>stepEnd</c> per step with 1 Hz <c>beat</c> heartbeats between them, then <c>result</c>, <c>released</c> and
/// <c>end</c>. The <c>begin</c>, <c>step</c>, <c>result</c> and <c>end</c> records are acknowledged: their
/// acknowledgment completes only once the line is durable. Every acknowledgment a caller waits on is bounded by
/// <see cref="AcknowledgmentTimeout"/>, 5 s, so a stalled disk never hangs a run.
/// </para>
/// <para>
/// Threading: every storage call happens on the writer thread, a background thread started by
/// <see cref="BeginAsync"/>, never on the UI thread or the engine coordinator. The recording methods only stamp,
/// enqueue and signal, and may be called from any thread; <see cref="RecordStepStarting"/> and
/// <see cref="PublishProgress"/> run on the coordinator, and the others normally on the UI thread. Heartbeats are
/// built by the writer from the latest snapshots published through <see cref="PublishProgress"/> and
/// <see cref="PublishReadings"/>.
/// </para>
/// <para>
/// Ownership: the writer thread and its heartbeat timer reference only a private writer core, which holds the
/// storage, the queue, the latest snapshots and the failure slot, and nothing that refers back to this journal or
/// its callers. The <see cref="Retired"/> signal belongs to this journal: the core holds it only weakly, because the
/// signal's continuations lead to the callers awaiting it. A journal its owner lets go is therefore collectable while
/// its writer thread is still alive, even one stalled for good in an append, and so is every caller awaiting its
/// retirement. For the same reason a write failure is exposed as the polled <see cref="Failure"/> property rather
/// than an event.
/// </para>
/// <para>
/// Lifetime: <see cref="EndAsync"/> deletes the journal once the run's summaries are published.
/// <see cref="Dispose"/> never deletes it, so a run disposed without <c>end</c> stays recoverable at the next
/// launch. A <c>begin</c> record that fails or is not acknowledged in time makes the writer delete the journal
/// and exit, because Start is then refused and a journal holding only <c>begin</c> would be misreported as an
/// unfinished run.
/// </para>
/// <para>
/// Every run's journal is the same file, and a writer can outlive <see cref="Dispose"/> while it is stalled in an
/// append; once that append returns it may still delete the file, after a <c>begin</c> that was not acknowledged
/// or after <c>end</c>. <see cref="Retired"/> completes when the writer can no longer touch the file, so the next
/// run's journal must not be started before it.
/// </para>
/// </remarks>
public sealed class ProgressJournal : IDisposable
{
    /// <summary>
    /// The longest time any caller waits for an acknowledged record to become durable: 5 s. The awaitable
    /// methods measure it on the journal's <see cref="TimeProvider"/>; the step gate passes it to
    /// <see cref="JournalAck.Wait(WaitHandle, TimeSpan)"/>, which measures it in real time.
    /// </summary>
    public static readonly TimeSpan AcknowledgmentTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The interval between <c>beat</c> records while the run is Running: 1 s, measured on the journal's
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private const string WriterThreadName = "RamQuick journal writer";
    private const string ClosedMessage = "The progress journal is closed.";

    private const string BeginType = "begin";
    private const string PreparedType = "prepared";
    private const string StepType = "step";
    private const string StepEndType = "stepEnd";
    private const string BeatType = "beat";
    private const string ResultType = "result";
    private const string ReleasedType = "released";
    private const string EndType = "end";

    // Not a record: reported when closing the storage fails.
    private const string CloseType = "close";

    // Dispose blocks its caller, normally the UI thread at shutdown, for at most this long; a writer stalled in
    // an append is left behind as a background thread that holds no run object, and reports through Retired once
    // it has closed the storage.
    private static readonly TimeSpan _disposeJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly JournalWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly object _lifecycleSync = new();

    // The Retired signal, referenced strongly only by this field: the writer core keeps a weak reference to it, so a
    // writer stalled for good roots no caller awaiting it. Asynchronous continuations keep those callers off the writer
    // thread, and the source drops its continuations once completed.
    private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _writerThread;
    private Guid _runId;
    private volatile bool _begun;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a journal over <paramref name="storage"/>. Nothing is written and no thread is started until
    /// <see cref="BeginAsync"/>.
    /// </summary>
    /// <param name="storage">
    /// The journal storage. Ownership passes to the journal: from <see cref="BeginAsync"/> on, only the writer
    /// thread uses and finally disposes it.
    /// </param>
    /// <param name="timeProvider">
    /// The clock that stamps every record, times the heartbeats and bounds the awaitable acknowledgments.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ProgressJournal(IJournalStorage storage, TimeProvider timeProvider)
    {
        // Finalization matters only once a writer thread exists, so BeginAsync registers it then. Suppressing it
        // first also keeps a constructor that throws from leaving a finalizer behind.
        GC.SuppressFinalize(this);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _writer = new JournalWriter(storage, timeProvider, _retired);
    }

    /// <summary>
    /// Stops the writer of a journal that was never disposed and that nothing can reach any more.
    /// </summary>
    /// <remarks>
    /// No further record can be offered to such a journal, so its writer finishes the queued records, closes the
    /// storage and exits, rather than appending heartbeats from a stale snapshot for the life of the process. The
    /// journal is left in place.
    /// </remarks>
    ~ProgressJournal()
    {
        _writer.RequestStop();
    }

    /// <summary>
    /// Gets the first write failure the writer observed, or <see langword="null"/> while every write has
    /// succeeded.
    /// </summary>
    /// <remarks>
    /// First writer wins: a later failure never replaces the first. Any thread may read it; <c>RunController</c>
    /// reads it on every Progress event and requests a <c>host_fault</c> stop once it is set. Failures of
    /// unacknowledged records (<c>prepared</c>, <c>stepEnd</c>, <c>beat</c>, <c>released</c>) are reported only
    /// here, and block nothing.
    /// </remarks>
    public JournalFailure? Failure => _writer.Failure;

    /// <summary>
    /// Gets a task that completes once this journal can no longer touch its storage: its writer thread has
    /// deleted the journal where it had to, closed the storage, completed every pending acknowledgment and exited;
    /// or, for a journal whose <see cref="BeginAsync"/> was never called, <see cref="Dispose"/> has closed the
    /// storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every run's journal is the same file, so its owner starts the next run's journal only after this task has
    /// completed. <see cref="Dispose"/> waits at most 1 s for the writer: a writer stalled in an append outlives it,
    /// and once the append returns it may still delete the file, after a <c>begin</c> that was not acknowledged in
    /// time or after <c>end</c>.
    /// </para>
    /// <para>
    /// The task never faults and is never cancelled. It has no bound of its own: it stays pending until
    /// <see cref="EndAsync"/>, a <c>begin</c> that failed or timed out, <see cref="Dispose"/> or finalization
    /// stops the writer, and after that for as long as an append stays stalled. A caller that must bound its wait
    /// applies its own timeout.
    /// </para>
    /// <para>
    /// Lifetime: the task completes only while this journal is reachable, so a caller awaits it while still holding
    /// the journal. The journal holds the task's completion source, and its writer holds that source only weakly, so
    /// that a writer stalled for good in an append keeps neither the journal nor any caller awaiting the task alive.
    /// Once the journal is collected, which also happens to a journal finalized without <see cref="Dispose"/>, the
    /// task never completes, and a caller that kept only the task waits for good.
    /// </para>
    /// <para>
    /// Threading: any thread may read it, before and after <see cref="Dispose"/>, and its continuations never run
    /// on the writer thread. It reaches an awaiting continuation only until it completes.
    /// </para>
    /// </remarks>
    public Task Retired => _retired.Task;

    /// <summary>
    /// Creates the journal of a run over the production file storage at
    /// <see cref="DataDirectory.JournalPath"/>.
    /// </summary>
    /// <param name="dataDirectory">The resolved data folder.</param>
    /// <param name="timeProvider">The clock for stamps, heartbeats and acknowledgment bounds.</param>
    /// <returns>A journal that has not begun yet.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ProgressJournal Create(DataDirectory dataDirectory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new ProgressJournal(new FileJournalStorage(dataDirectory.JournalPath), timeProvider);
    }

    /// <summary>
    /// Starts the writer thread and writes the run's <c>begin</c> record, waiting at most
    /// <see cref="AcknowledgmentTimeout"/> for it to become durable. Called once, before <c>rq_session_start</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record's <see cref="JournalRecord.Utc"/> is stamped from the journal's <see cref="TimeProvider"/>, which
    /// also measures the 5 s bound. Heartbeats are armed on the writer thread once the record is durable, before
    /// the returned task completes.
    /// </para>
    /// <para>
    /// Any result other than <see cref="JournalAckStatus.Acknowledged"/> means Start must be refused. The writer
    /// then deletes the journal and exits: at once after a failed append, and after a timeout as soon as the
    /// stalled append returns, so no journal holding only <c>begin</c> survives to be misreported as an unfinished
    /// run. Later records are ignored. <see cref="Retired"/> completes once that deletion is done.
    /// </para>
    /// </remarks>
    /// <param name="begin">The run's <c>begin</c> record; its <see cref="JournalBegin.RunId"/> is reused by <c>end</c>.</param>
    /// <returns>
    /// <see cref="JournalAckStatus.Acknowledged"/>, <see cref="JournalAckStatus.Failed"/> or
    /// <see cref="JournalAckStatus.TimedOut"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="begin"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The journal has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The method has already been called on this journal.</exception>
    public Task<JournalAckStatus> BeginAsync(JournalBegin begin)
    {
        ArgumentNullException.ThrowIfNull(begin);

        var ack = new JournalAck();
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_begun || _writerThread is not null)
            {
                throw new InvalidOperationException("BeginAsync can be called only once per progress journal.");
            }

            _runId = begin.RunId;

            // Queued before the thread starts, so begin is always the first line.
            _writer.EnqueueFirst(begin with { Utc = _timeProvider.GetUtcNow() }, ack);

            var thread = new Thread(JournalWriter.RunThread) { IsBackground = true, Name = WriterThreadName };
            thread.Start(_writer);
            _writerThread = thread;
            GC.ReRegisterForFinalize(this);
            _begun = true;
        }

        return AwaitBeginAsync(_writer, ack, _timeProvider);
    }

    /// <summary>
    /// Queues the <c>prepared</c> record, without acknowledgment. Called on Prepared.
    /// </summary>
    /// <remarks>Never blocks. A no-op once the journal is disposed or its writer has closed.</remarks>
    /// <param name="prepared">What the engine reported when preparation finished.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prepared"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public void RecordPrepared(JournalPrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ThrowIfNotBegun();
        _ = TryOffer(prepared, ack: null);
    }

    /// <summary>
    /// Queues the acknowledged <c>step</c> record of a step that is about to start, and returns its
    /// acknowledgment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on the engine coordinator inside the step gate, before the step's memory traffic. It only enqueues
    /// and signals the writer: it never blocks and performs no I/O. The gate then calls
    /// <c>ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout)</c> and continues only on
    /// <see cref="JournalAckStatus.Acknowledged"/>. It aborts the step on <see cref="JournalAckStatus.Stopped"/>,
    /// and on <see cref="JournalAckStatus.Failed"/> or <see cref="JournalAckStatus.TimedOut"/> it first requests a
    /// <c>HostFault</c> stop.
    /// </para>
    /// <para>
    /// The journal persists the step's 1-based number, <paramref name="stepIndex"/> + 1, as in "step 3 of 6".
    /// Once the journal is disposed or its writer has closed, the returned acknowledgment is already failed.
    /// </para>
    /// </remarks>
    /// <param name="stepIndex">The step's 0-based index in the plan.</param>
    /// <param name="stepCount">The number of steps in the plan.</param>
    /// <param name="stepName">The step's name.</param>
    /// <returns>The record's acknowledgment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stepIndex"/> is negative or not below <paramref name="stepCount"/>, or
    /// <paramref name="stepCount"/> is less than 1.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="stepName"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public JournalAck RecordStepStarting(int stepIndex, int stepCount, string stepName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(stepIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(stepIndex, stepCount);
        ArgumentNullException.ThrowIfNull(stepName);
        ThrowIfNotBegun();

        var ack = new JournalAck();
        var record = new JournalStepStart { Index = stepIndex + 1, Count = stepCount, Name = stepName };
        return TryOffer(record, ack) ? ack : JournalAck.CreateFailed(ClosedFailure(StepType, _timeProvider));
    }

    /// <summary>
    /// Queues the <c>stepEnd</c> record of a finished or interrupted step, without acknowledgment. Called on
    /// StepFinished.
    /// </summary>
    /// <remarks>Never blocks. A no-op once the journal is disposed or its writer has closed.</remarks>
    /// <param name="outcome">What the engine measured for the step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public void RecordStepFinished(StepOutcome outcome)
    {
        JournalStepEnd record = JournalStepEnd.FromOutcome(outcome);
        ThrowIfNotBegun();
        _ = TryOffer(record, ack: null);
    }

    /// <summary>
    /// Publishes the latest progress snapshot for the next heartbeat.
    /// </summary>
    /// <remarks>
    /// Called on the engine coordinator by the session's progress sink before the Progress event is posted. It is
    /// a single atomic reference store: no lock, no allocation, no I/O. Heartbeats are written only once a snapshot
    /// has been published.
    /// </remarks>
    /// <param name="progress">The snapshot, immutable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is <see langword="null"/>.</exception>
    public void PublishProgress(RunProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _writer.PublishProgress(progress);
    }

    /// <summary>
    /// Publishes the latest CPU and RAM readings for the next heartbeat.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread whenever the sensor monitor posts. A single atomic reference store. Until the first
    /// call, heartbeats record <see cref="JournalReadings.Unknown"/>.
    /// </remarks>
    /// <param name="readings">The readings, immutable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is <see langword="null"/>.</exception>
    public void PublishReadings(JournalReadings readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        _writer.PublishReadings(readings);
    }


    /// <summary>
    /// Writes the acknowledged <c>result</c> record and stops the heartbeats, waiting at most
    /// <see cref="AcknowledgmentTimeout"/> for the record to become durable. Called immediately after the
    /// terminal transition.
    /// </summary>
    /// <remarks>
    /// The run state arrives as <see cref="JournalResult.TerminalState"/>; the journal never keeps a copy of it.
    /// No <c>beat</c> follows the <c>result</c> record. The 5 s bound is measured on the journal's
    /// <see cref="TimeProvider"/>.
    /// </remarks>
    /// <param name="result">The run's verdict and true totals.</param>
    /// <returns>
    /// <see cref="JournalAckStatus.Acknowledged"/>, <see cref="JournalAckStatus.Failed"/> (also when the journal
    /// is disposed or closed) or <see cref="JournalAckStatus.TimedOut"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The record's terminal state is not a terminal state.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public Task<JournalAckStatus> RecordResultAsync(JournalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.TerminalState.IsTerminal())
        {
            throw new ArgumentException(
                $"The result record needs a terminal state, not {result.TerminalState}.", nameof(result));
        }

        ThrowIfNotBegun();
        _writer.StopHeartbeats();

        var ack = new JournalAck();
        return TryOffer(result, ack)
            ? AwaitAcknowledgmentAsync(ack, _timeProvider)
            : Task.FromResult(JournalAckStatus.Failed);
    }

    /// <summary>
    /// Queues the <c>released</c> record, without acknowledgment. Called on Released, before the summaries are
    /// published.
    /// </summary>
    /// <remarks>Never blocks. A no-op once the journal is disposed or its writer has closed.</remarks>
    /// <param name="releasedBytes">The bytes the engine released.</param>
    /// <param name="releaseTime">The time the release took; persisted in whole milliseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="releaseTime"/> is negative.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public void RecordReleased(ulong releasedBytes, TimeSpan releaseTime)
    {
        if (releaseTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseTime), releaseTime, "A duration must not be negative.");
        }

        ThrowIfNotBegun();
        var record = new JournalReleased
        {
            ReleasedBytes = releasedBytes,
            ReleaseMs = JournalValues.WholeMilliseconds(releaseTime),
        };
        _ = TryOffer(record, ack: null);
    }

    /// <summary>
    /// Writes the acknowledged <c>end</c> record, then has the writer delete the journal, close the storage and
    /// exit. Called only after both summaries of the run are published.
    /// </summary>
    /// <remarks>
    /// The journal is deleted even when the <c>end</c> append fails: the results are already published, and a
    /// journal left behind would be misreported as an unfinished run at the next launch. The 5 s bound is measured
    /// on the journal's <see cref="TimeProvider"/>.
    /// </remarks>
    /// <returns>
    /// <see cref="JournalAckStatus.Acknowledged"/> only when the journal was deleted;
    /// <see cref="JournalAckStatus.Failed"/> when it could not be, or when the journal is disposed or closed;
    /// <see cref="JournalAckStatus.TimedOut"/> when the writer did not finish within the bound.
    /// </returns>
    /// <exception cref="InvalidOperationException"><see cref="BeginAsync"/> has not been called.</exception>
    public Task<JournalAckStatus> EndAsync()
    {
        ThrowIfNotBegun();

        var ack = new JournalAck();
        var record = new JournalEnd { RunId = _runId, Published = true };
        return TryOffer(record, ack)
            ? AwaitAcknowledgmentAsync(ack, _timeProvider)
            : Task.FromResult(JournalAckStatus.Failed);
    }

    /// <summary>
    /// Stops the heartbeats and lets the writer finish the queued records and exit, waiting at most 1 s for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never deletes the journal, so a run disposed before its <c>end</c> record stays recoverable. A writer still
    /// stalled in an append after 1 s is abandoned as a background thread that holds no run object; it finishes
    /// that append, deletes the journal if its <c>begin</c> timed out or the append was <c>end</c>, and closes the
    /// storage. The return of this method therefore does not mean the writer is gone: <see cref="Retired"/>
    /// completes when it is.
    /// </para>
    /// <para>
    /// After this call the recording methods are no-ops: <see cref="RecordStepStarting"/> returns an
    /// acknowledgment that is already failed, and the awaitable methods return
    /// <see cref="JournalAckStatus.Failed"/>. Idempotent; any thread may call it.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _writerThread;
        }

        GC.SuppressFinalize(this);
        _writer.RequestStop();
        if (thread is null)
        {
            // No writer thread ever started, so this thread is the storage's only user.
            _writer.DisposeStorage();
            _retired.TrySetResult();
            return;
        }

        // Whether the writer exited in time does not change what Dispose does: a stalled writer finishes alone.
        _ = thread.Join(_disposeJoinTimeout);
    }

    private static async Task<JournalAckStatus> AwaitBeginAsync(
        JournalWriter writer,
        JournalAck ack,
        TimeProvider timeProvider)
    {
        JournalAckStatus status = await AwaitAcknowledgmentAsync(ack, timeProvider).ConfigureAwait(false);
        if (status != JournalAckStatus.Acknowledged)
        {
            writer.Abandon();
        }

        return status;
    }

    // Static, so the awaiting continuation the acknowledgment reaches holds the acknowledgment and the clock only.
    private static async Task<JournalAckStatus> AwaitAcknowledgmentAsync(JournalAck ack, TimeProvider timeProvider)
    {
        try
        {
            return await ack.Completion.WaitAsync(AcknowledgmentTimeout, timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return JournalAckStatus.TimedOut;
        }
    }

    private static JournalFailure ClosedFailure(string recordType, TimeProvider timeProvider) =>
        new(recordType, ClosedMessage, null, timeProvider.GetUtcNow());

    private static string RecordTypeOf(JournalRecord record) => record switch
    {
        JournalBegin => BeginType,
        JournalPrepared => PreparedType,
        JournalStepStart => StepType,
        JournalStepEnd => StepEndType,
        JournalBeat => BeatType,
        JournalResult => ResultType,
        JournalReleased => ReleasedType,
        JournalEnd => EndType,
        _ => record.GetType().Name,
    };

    private void ThrowIfNotBegun()
    {
        if (!_begun)
        {
            throw new InvalidOperationException("BeginAsync must be called before any other record is written.");
        }
    }

    // Stamps the record at enqueue time and hands it to the writer; false when the journal no longer accepts
    // records.
    private bool TryOffer(JournalRecord record, JournalAck? ack) =>
        !_disposed && _writer.TryEnqueue(record with { Utc = _timeProvider.GetUtcNow() }, ack);


    /// <summary>
    /// The writer thread's core: the storage, the FIFO queue with its wake-up signal, the latest progress and
    /// readings snapshots, the heartbeat state, the first-writer-wins failure slot and a weak reference to the
    /// journal's retirement signal.
    /// </summary>
    /// <remarks>
    /// It holds no delegate, event or reference to <see cref="ProgressJournal"/>, its callers or the engine
    /// session, and holds the retirement signal only weakly, since that signal's continuations reach the callers
    /// awaiting it; the thread and the heartbeat timer reach the core through static callbacks with the core as their
    /// state. Every storage call happens on the writer thread, except <see cref="DisposeStorage"/> for a journal
    /// whose writer never started. The lock guards only queue and flag updates and is never held during I/O or
    /// while calling the <see cref="TimeProvider"/>.
    /// </remarks>
    private sealed class JournalWriter
    {
        private readonly IJournalStorage _storage;
        private readonly TimeProvider _timeProvider;
        private readonly object _sync = new();
        private readonly Queue<(JournalRecord Record, JournalAck? Ack)> _queue = new();

        // The journal's retirement signal, completed once the storage is closed for good. Weak, so a writer stalled for
        // good in an append keeps neither the journal nor its awaiting callers alive. A signal collected with its
        // journal has no awaiter left that holds the journal, so it needs no completion.
        private readonly WeakReference<TaskCompletionSource> _retired;

        private RunProgress? _latestProgress;
        private JournalReadings? _latestReadings;
        private JournalFailure? _failure;

        // Guarded by _sync.
        private ITimer? _heartbeatTimer;
        private bool _beatDue;
        private bool _beatsEnabled;
        private bool _stopRequested;
        private bool _abandoned;
        private bool _closed;

        public JournalWriter(IJournalStorage storage, TimeProvider timeProvider, TaskCompletionSource retired)
        {
            _storage = storage;
            _timeProvider = timeProvider;
            _retired = new WeakReference<TaskCompletionSource>(retired);
        }

        public JournalFailure? Failure => Volatile.Read(ref _failure);

        public static void RunThread(object? state) => ((JournalWriter)state!).Run();

        public void EnqueueFirst(JournalRecord record, JournalAck ack)
        {
            lock (_sync)
            {
                _queue.Enqueue((record, ack));
            }
        }

        public bool TryEnqueue(JournalRecord record, JournalAck? ack)
        {
            lock (_sync)
            {
                if (_closed || _stopRequested || _abandoned)
                {
                    return false;
                }

                _queue.Enqueue((record, ack));
                Monitor.Pulse(_sync);
                return true;
            }
        }

        public void PublishProgress(RunProgress progress) => Volatile.Write(ref _latestProgress, progress);

        public void PublishReadings(JournalReadings readings) => Volatile.Write(ref _latestReadings, readings);

        public void StopHeartbeats() => Quiesce(stop: false, abandon: false);

        // The writer finishes the queued records, closes the storage and exits; the journal stays in place.
        public void RequestStop() => Quiesce(stop: true, abandon: false);

        // The writer drops the queued records, deletes the journal, closes the storage and exits.
        public void Abandon() => Quiesce(stop: false, abandon: true);

        public void DisposeStorage()
        {
            try
            {
                _storage.Dispose();
            }
            catch (Exception exception)
            {
                _ = RecordFailure(CloseType, exception, "The journal could not be closed: ");
            }
        }

        // Called by the writer thread as its last act, once the storage is closed; from then on nothing of this journal
        // touches the file.
        public void MarkRetired()
        {
            if (_retired.TryGetTarget(out TaskCompletionSource? retired))
            {
                retired.TrySetResult();
            }
        }

        private void Quiesce(bool stop, bool abandon)
        {
            ITimer? timer;
            lock (_sync)
            {
                _stopRequested |= stop;
                _abandoned |= abandon;
                _beatsEnabled = false;
                _beatDue = false;
                timer = _heartbeatTimer;
                _heartbeatTimer = null;
                Monitor.Pulse(_sync);
            }

            // Outside the lock: a timer provider may invoke callbacks, which take the lock, under a lock of its own.
            timer?.Dispose();
        }

        private void Run()
        {
            bool deleteJournal = false;
            while (true)
            {
                JournalRecord? record = null;
                JournalAck? ack = null;
                lock (_sync)
                {
                    while (!_abandoned && !_stopRequested && _queue.Count == 0 && !(_beatDue && _beatsEnabled))
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_abandoned)
                    {
                        deleteJournal = true;
                        break;
                    }

                    if (_queue.Count > 0)
                    {
                        (record, ack) = _queue.Dequeue();
                    }
                    else if (_beatDue && _beatsEnabled)
                    {
                        _beatDue = false;
                    }
                    else
                    {
                        // Stop requested and nothing left to write.
                        break;
                    }
                }

                if (record is null)
                {
                    WriteBeat();
                    continue;
                }

                JournalFailure? failure = Append(record);
                if (record is JournalEnd)
                {
                    // The summaries are already published, so the journal goes even when the end append failed:
                    // left behind, it would be reported as an unfinished run at the next launch.
                    ack?.Complete(TryDeleteJournal(EndType));
                    break;
                }

                if (record is JournalBegin)
                {
                    if (failure is not null)
                    {
                        // Start is refused, and a torn or partial begin must not look like an unfinished run.
                        ack?.Complete(failure);
                        deleteJournal = true;
                        break;
                    }

                    // Armed before the acknowledgment completes, so heartbeats are timed from a point every
                    // caller of BeginAsync has already passed.
                    ArmHeartbeats();
                }

                // The writer drops its reference to the acknowledgment, and with it any awaiting continuation.
                ack?.Complete(failure);
            }

            Close(deleteJournal);

            // Last: the journal is deleted where it had to be, the storage closed and every acknowledgment completed.
            MarkRetired();
        }

        private void ArmHeartbeats()
        {
            lock (_sync)
            {
                if (_abandoned || _stopRequested)
                {
                    return;
                }

                _beatsEnabled = true;
            }

            ITimer timer = _timeProvider.CreateTimer(
                static state => ((JournalWriter)state!).SignalBeat(),
                this,
                HeartbeatInterval,
                HeartbeatInterval);
            lock (_sync)
            {
                if (_beatsEnabled)
                {
                    _heartbeatTimer = timer;
                    return;
                }
            }

            // Heartbeats were stopped while the timer was being created.
            timer.Dispose();
        }

        // Runs on the timer's thread: it only marks a beat due and wakes the writer, with no I/O.
        private void SignalBeat()
        {
            lock (_sync)
            {
                _beatDue = true;
                Monitor.Pulse(_sync);
            }
        }

        private void WriteBeat()
        {
            RunProgress? progress = Volatile.Read(ref _latestProgress);
            if (progress is null)
            {
                return;
            }

            JournalBeat beat = JournalBeat.From(progress, Volatile.Read(ref _latestReadings));

            // A failed beat is reported through Failure only; nobody waits on it.
            _ = Append(beat with { Utc = _timeProvider.GetUtcNow() });
        }

        private JournalFailure? Append(JournalRecord record)
        {
            try
            {
                _storage.AppendDurable(JournalLineSerializer.Serialize(record));
                return null;
            }
            catch (Exception exception)
            {
                // The writer carries on: later records are still attempted after a failed one.
                return RecordFailure(RecordTypeOf(record), exception, context: null);
            }
        }

        private JournalFailure? TryDeleteJournal(string recordType)
        {
            try
            {
                _storage.Delete();
                return null;
            }
            catch (Exception exception)
            {
                return RecordFailure(recordType, exception, "The journal could not be deleted: ");
            }
        }

        private void Close(bool deleteJournal)
        {
            ITimer? timer;
            (JournalRecord Record, JournalAck? Ack)[] orphaned;
            lock (_sync)
            {
                _closed = true;
                _beatsEnabled = false;
                timer = _heartbeatTimer;
                _heartbeatTimer = null;
                orphaned = _queue.ToArray();
                _queue.Clear();
            }

            timer?.Dispose();
            if (deleteJournal)
            {
                _ = TryDeleteJournal(BeginType);
            }

            DisposeStorage();
            foreach ((JournalRecord record, JournalAck? ack) in orphaned)
            {
                ack?.Complete(ClosedFailure(RecordTypeOf(record), _timeProvider));
            }
        }

        private JournalFailure RecordFailure(string recordType, Exception exception, string? context)
        {
            string error = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
            var failure = new JournalFailure(recordType, context + error, exception, _timeProvider.GetUtcNow());
            _ = Interlocked.CompareExchange(ref _failure, failure, null);
            return failure;
        }
    }
}
