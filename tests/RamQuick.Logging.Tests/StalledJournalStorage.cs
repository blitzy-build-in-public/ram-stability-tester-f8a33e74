using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

// Linked into RamQuick.App.Tests and RamQuick.IntegrationTests as well: this file may depend on the
// BCL and the public IJournalStorage contract only, never on a test framework or a test-project type.
namespace RamQuick.Logging.Tests;

/// <summary>
/// The outcome of one <see cref="StalledJournalStorage.AppendDurable(string)"/> call.
/// </summary>
public enum AppendOutcome
{
    /// <summary>The call has not resolved yet: it is blocked, or still choosing its action.</summary>
    Pending = 0,

    /// <summary>The line was stored durably and the call returned normally.</summary>
    Succeeded = 1,

    /// <summary>The call threw and the line was not stored.</summary>
    Failed = 2,
}

/// <summary>
/// One <see cref="StalledJournalStorage.AppendDurable(string)"/> call as the substitute received it.
/// </summary>
/// <param name="Index">The 0-based position of the call among every append made on the instance.</param>
/// <param name="RecordType">
/// The record's <c>"t"</c> value, or <see langword="null"/> when the line is not a JSON object with a
/// string <c>"t"</c> property.
/// </param>
/// <param name="ManagedThreadId">The managed thread id of the calling thread.</param>
/// <param name="IsThreadPoolThread">Whether the calling thread is a thread-pool thread.</param>
/// <param name="IsBackground">Whether the calling thread is a background thread.</param>
/// <param name="Line">The line exactly as passed to the call.</param>
public sealed record AppendCall(
    int Index,
    string? RecordType,
    int ManagedThreadId,
    bool IsThreadPoolThread,
    bool IsBackground,
    string Line)
{
    private int _outcome;

    /// <summary>
    /// Gets the call's outcome. The value is live: it moves from <see cref="AppendOutcome.Pending"/>
    /// to <see cref="AppendOutcome.Succeeded"/> or <see cref="AppendOutcome.Failed"/> when the call
    /// resolves, and may be read from any thread.
    /// </summary>
    public AppendOutcome Outcome
    {
        get => (AppendOutcome)Volatile.Read(ref _outcome);
        internal set => Volatile.Write(ref _outcome, (int)value);
    }
}

/// <summary>
/// An in-memory <see cref="IJournalStorage"/> that blocks or fails appends on demand, for the
/// stalled-writer and failed-write paths of the progress journal, the step gate and the run
/// controller, and that replays seeded journals, torn final lines included, for crash recovery.
/// </summary>
/// <remarks>
/// <para>
/// Unlike production storage, every member is thread-safe: the journal's writer thread appends
/// while the test thread scripts rules, releases blocked appends and observes the results. Every
/// getter returns a snapshot copy.
/// </para>
/// <para>
/// Each append chooses one action. The tiers, in precedence order, are: a <see cref="FailAt"/> or
/// <see cref="BlockAt"/> rule for the call's index; then the earliest pending <see cref="FailNext"/>
/// or <see cref="BlockNext"/> rule whose record type matches, which the call consumes; then any
/// <see cref="FailAll"/> or <see cref="BlockAll"/> rule whose record type matches. Within a tier a
/// fail rule wins over a block rule, and rules of lower tiers stay pending. A <see langword="null"/>
/// record type in a rule matches every line. With no matching rule the append succeeds.
/// </para>
/// <para>
/// A blocked append waits, without holding the instance's lock, until <see cref="Release"/> or
/// <see cref="Dispose"/> resolves it. <see cref="Release"/> leaves the rules in place, so a
/// persistent <see cref="BlockAll"/> rule stalls the next matching append again; call
/// <see cref="ClearRules"/> first to stop stalling.
/// </para>
/// <para>
/// After <see cref="Dispose"/>, <see cref="AppendDurable"/> throws <see cref="ObjectDisposedException"/>,
/// while the observation members, <see cref="ReadLines"/>, <see cref="Exists"/>, <see cref="Delete"/>
/// and the scripting members keep working, so tests can inspect a journal its owner has disposed.
/// </para>
/// </remarks>
public sealed class StalledJournalStorage : IJournalStorage
{
    private const string SimulatedFailureMessage = "Simulated journal write failure (StalledJournalStorage)";

    private readonly object _sync = new();
    private readonly List<string> _journalLines = new();
    private readonly List<string> _durableLines = new();
    private readonly List<AppendCall> _calls = new();
    private readonly Dictionary<int, Exception?> _failAt = new();
    private readonly HashSet<int> _blockAt = new();
    private readonly List<FailRule> _failNext = new();
    private readonly List<string?> _blockNext = new();
    private readonly List<FailRule> _failAll = new();
    private readonly List<string?> _blockAll = new();
    private readonly List<BlockedAppend> _blocked = new();
    private bool _journalPresent;
    private bool _isDeleted;
    private int _deleteCount;
    private bool _disposed;

    /// <summary>
    /// Initializes an empty storage: no journal exists until a line is seeded or appended.
    /// </summary>
    /// <param name="location">The diagnostic location reported by <see cref="Location"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="location"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="location"/> is <see langword="null"/>.</exception>
    public StalledJournalStorage(string location = "memory://journal/active-run.jsonl")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        Location = location;
    }

    private enum AppendAction
    {
        Succeed,
        Fail,
        Block,
    }

    /// <summary>Gets the diagnostic location given to the constructor.</summary>
    public string Location { get; }

    /// <summary>
    /// Gets the lines stored by successful appends, in order, excluding seeded lines.
    /// <see cref="Delete"/> clears them.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_sync)
            {
                return _durableLines.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets every <see cref="AppendDurable"/> call in call order, whatever its outcome, including
    /// calls made after <see cref="Dispose"/>. The list is a snapshot; each <see cref="AppendCall"/>
    /// in it reports its live <see cref="AppendCall.Outcome"/>.
    /// </summary>
    public IReadOnlyList<AppendCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>Gets the number of <see cref="AppendDurable"/> calls made so far.</summary>
    public int AppendCallCount
    {
        get
        {
            lock (_sync)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>Gets the number of appends blocked right now.</summary>
    public int BlockedCount
    {
        get
        {
            lock (_sync)
            {
                return _blocked.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether <see cref="Delete"/> was called and no line has been seeded or appended since.
    /// </summary>
    public bool IsDeleted
    {
        get
        {
            lock (_sync)
            {
                return _isDeleted;
            }
        }
    }

    /// <summary>Gets the number of <see cref="Delete"/> calls made so far.</summary>
    public int DeleteCount
    {
        get
        {
            lock (_sync)
            {
                return _deleteCount;
            }
        }
    }

    /// <summary>
    /// Returns whether a journal is present: a line was seeded or appended, or an empty journal
    /// was seeded, and <see cref="Delete"/> has not been called since.
    /// </summary>
    /// <returns><see langword="true"/> when a journal exists, even an empty or torn one.</returns>
    public bool Exists()
    {
        lock (_sync)
        {
            return _journalPresent;
        }
    }

    /// <summary>
    /// Records the call, then succeeds, throws or blocks as the scripted rules decide.
    /// A blocked call returns or throws once <see cref="Release"/> or <see cref="Dispose"/>
    /// resolves it.
    /// </summary>
    /// <param name="line">The serialized record, without <c>\r</c> or <c>\n</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="line"/> contains a line terminator, which breaks the caller's contract; the
    /// call is not recorded.
    /// </exception>
    /// <exception cref="IOException">
    /// A fail rule without its own exception matched, or <see cref="Release"/> failed the blocked
    /// call. A fail rule that carries an exception throws that exception instead.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The instance was disposed before or while the call was blocked.</exception>
    public void AppendDurable(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\r') || line.Contains('\n'))
        {
            throw new ArgumentException("A journal record must not contain a line terminator.", nameof(line));
        }

        string? recordType = ReadRecordType(line);
        BlockedAppend blocked;
        lock (_sync)
        {
            var call = new AppendCall(
                _calls.Count,
                recordType,
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.IsThreadPoolThread,
                Thread.CurrentThread.IsBackground,
                line);
            _calls.Add(call);

            if (_disposed)
            {
                call.Outcome = AppendOutcome.Failed;
                throw new ObjectDisposedException(nameof(StalledJournalStorage));
            }

            AppendAction action = SelectActionLocked(call.Index, recordType, out Exception? failure);
            if (action == AppendAction.Fail)
            {
                call.Outcome = AppendOutcome.Failed;
                throw failure ?? CreateSimulatedFailure();
            }

            if (action == AppendAction.Succeed)
            {
                CommitLocked(call);
                return;
            }

            blocked = new BlockedAppend(call);
            _blocked.Add(blocked);
            Monitor.PulseAll(_sync);
        }

        // Waiting outside the lock keeps Release, Dispose and every observer live while stalled.
        blocked.Gate.Wait();

        Exception? outcomeFailure;
        lock (_sync)
        {
            // The resolver sets the gate while holding the lock, so it has finished with the gate here.
            outcomeFailure = blocked.Failure;
            blocked.Gate.Dispose();
        }

        if (outcomeFailure is not null)
        {
            throw outcomeFailure;
        }
    }

    /// <summary>
    /// Returns every stored line in storage order, seeded and appended, without terminators.
    /// A seeded torn line is returned exactly as seeded.
    /// </summary>
    /// <returns>A snapshot of the journal's lines, or an empty list when no journal exists.</returns>
    public IReadOnlyList<string> ReadLines()
    {
        lock (_sync)
        {
            return _journalLines.ToArray();
        }
    }

    /// <summary>
    /// Deletes the journal: clears the seeded and appended lines, sets <see cref="IsDeleted"/> and
    /// increments <see cref="DeleteCount"/>. <see cref="Exists"/> then returns <see langword="false"/>
    /// until a line is seeded or appended.
    /// </summary>
    public void Delete()
    {
        lock (_sync)
        {
            _journalLines.Clear();
            _durableLines.Clear();
            _journalPresent = false;
            _isDeleted = true;
            _deleteCount++;
        }
    }

    /// <summary>
    /// Fails every blocked append with <see cref="ObjectDisposedException"/> and makes later appends
    /// throw it. Leaves the stored lines in place, as production storage leaves the journal file.
    /// Idempotent, and never waits on another thread.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (BlockedAppend blocked in _blocked)
            {
                blocked.Failure = new ObjectDisposedException(nameof(StalledJournalStorage));
                blocked.Call.Outcome = AppendOutcome.Failed;
                blocked.Gate.Set();
            }

            _blocked.Clear();

            // Wakes WaitForBlocked callers: no append can block from now on.
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Stores lines verbatim, after any lines already stored, for crash-recovery inputs such as a
    /// torn final line. Seeded lines are not recorded as calls and are excluded from
    /// <see cref="Lines"/>. Makes <see cref="Exists"/> <see langword="true"/>, even for an empty
    /// sequence, and clears <see cref="IsDeleted"/>.
    /// </summary>
    /// <param name="rawLines">The lines to store, without terminators.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rawLines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rawLines"/> contains a <see langword="null"/> line.</exception>
    public void Seed(IEnumerable<string> rawLines)
    {
        ArgumentNullException.ThrowIfNull(rawLines);
        var lines = new List<string>(rawLines);
        foreach (string line in lines)
        {
            if (line is null)
            {
                throw new ArgumentException("A seeded journal line must not be null.", nameof(rawLines));
            }
        }

        lock (_sync)
        {
            _journalLines.AddRange(lines);
            _journalPresent = true;
            _isDeleted = false;
        }
    }

    /// <summary>
    /// Makes the next append whose record type matches block until <see cref="Release"/>; one-shot.
    /// </summary>
    /// <param name="recordType">The <c>"t"</c> value to match, or <see langword="null"/> for any line.</param>
    public void BlockNext(string? recordType = null)
    {
        lock (_sync)
        {
            _blockNext.Add(recordType);
        }
    }

    /// <summary>
    /// Makes the append with the given 0-based call index block until <see cref="Release"/>; one-shot.
    /// </summary>
    /// <param name="callIndex">The 0-based index the call will receive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="callIndex"/> is negative.</exception>
    public void BlockAt(int callIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(callIndex);
        lock (_sync)
        {
            _blockAt.Add(callIndex);
        }
    }

    /// <summary>
    /// Makes every append whose record type matches block until <see cref="Release"/>; persistent
    /// until <see cref="ClearRules"/>.
    /// </summary>
    /// <param name="recordType">The <c>"t"</c> value to match, or <see langword="null"/> for any line.</param>
    public void BlockAll(string? recordType = null)
    {
        lock (_sync)
        {
            _blockAll.Add(recordType);
        }
    }

    /// <summary>
    /// Makes the next append whose record type matches throw; one-shot.
    /// </summary>
    /// <param name="recordType">The <c>"t"</c> value to match, or <see langword="null"/> for any line.</param>
    /// <param name="exception">
    /// The exception to throw, or <see langword="null"/> for a new <see cref="IOException"/>.
    /// </param>
    public void FailNext(string? recordType = null, Exception? exception = null)
    {
        lock (_sync)
        {
            _failNext.Add(new FailRule(recordType, exception));
        }
    }

    /// <summary>
    /// Makes the append with the given 0-based call index throw; one-shot.
    /// </summary>
    /// <param name="callIndex">The 0-based index the call will receive.</param>
    /// <param name="exception">
    /// The exception to throw, or <see langword="null"/> for a new <see cref="IOException"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="callIndex"/> is negative.</exception>
    public void FailAt(int callIndex, Exception? exception = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(callIndex);
        lock (_sync)
        {
            _failAt[callIndex] = exception;
        }
    }

    /// <summary>
    /// Makes every append whose record type matches throw; persistent until <see cref="ClearRules"/>.
    /// </summary>
    /// <param name="recordType">The <c>"t"</c> value to match, or <see langword="null"/> for any line.</param>
    /// <param name="exception">
    /// The exception to throw on every match, or <see langword="null"/> for a new
    /// <see cref="IOException"/> each time.
    /// </param>
    public void FailAll(string? recordType = null, Exception? exception = null)
    {
        lock (_sync)
        {
            _failAll.Add(new FailRule(recordType, exception));
        }
    }

    /// <summary>
    /// Resolves every append blocked at the moment of the call. On success each line is stored, and
    /// its call marked <see cref="AppendOutcome.Succeeded"/>, before this method returns; on failure
    /// each call is marked <see cref="AppendOutcome.Failed"/> and throws a new
    /// <see cref="IOException"/>. Rules stay in place.
    /// </summary>
    /// <param name="failBlocked">
    /// <see langword="true"/> to make the blocked appends throw; <see langword="false"/> to let them succeed.
    /// </param>
    public void Release(bool failBlocked = false)
    {
        lock (_sync)
        {
            foreach (BlockedAppend blocked in _blocked)
            {
                if (failBlocked)
                {
                    blocked.Failure = CreateSimulatedFailure();
                    blocked.Call.Outcome = AppendOutcome.Failed;
                }
                else
                {
                    CommitLocked(blocked.Call);
                }

                blocked.Gate.Set();
            }

            _blocked.Clear();
        }
    }

    /// <summary>
    /// Removes every pending one-shot and persistent rule. Appends already blocked stay blocked
    /// until <see cref="Release"/> or <see cref="Dispose"/>.
    /// </summary>
    public void ClearRules()
    {
        lock (_sync)
        {
            _failAt.Clear();
            _blockAt.Clear();
            _failNext.Clear();
            _blockNext.Clear();
            _failAll.Clear();
            _blockAll.Clear();
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> appends are blocked at the same time.
    /// </summary>
    /// <param name="timeout">
    /// The longest time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait without a bound.
    /// </param>
    /// <param name="count">The number of simultaneously blocked appends to wait for.</param>
    /// <returns>
    /// <see langword="true"/> once that many appends are blocked; <see langword="false"/> when the
    /// timeout expires first or the instance is disposed, since no append can block after that.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative and not infinite, or <paramref name="count"/> is less than 1.
    /// </exception>
    public bool WaitForBlocked(TimeSpan timeout, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        if (!infinite && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be non-negative or infinite.");
        }

        long deadline = infinite ? long.MaxValue : Environment.TickCount64 + (long)Math.Ceiling(timeout.TotalMilliseconds);
        lock (_sync)
        {
            while (_blocked.Count < count)
            {
                if (_disposed)
                {
                    return false;
                }

                int waitMilliseconds = Timeout.Infinite;
                if (!infinite)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        return false;
                    }

                    waitMilliseconds = (int)Math.Min(remaining, int.MaxValue);
                }

                Monitor.Wait(_sync, waitMilliseconds);
            }

            return true;
        }
    }

    /// <summary>
    /// Returns the <c>"t"</c> value of each line in <see cref="Lines"/>, in order, or
    /// <see langword="null"/> for a line that has none.
    /// </summary>
    /// <returns>A snapshot of the record types of the durable lines.</returns>
    public IReadOnlyList<string?> RecordTypesWritten()
    {
        string[] lines;
        lock (_sync)
        {
            lines = _durableLines.ToArray();
        }

        var recordTypes = new string?[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            recordTypes[i] = ReadRecordType(lines[i]);
        }

        return recordTypes;
    }

    private static bool Matches(string? ruleRecordType, string? recordType) =>
        ruleRecordType is null || string.Equals(ruleRecordType, recordType, StringComparison.Ordinal);

    private static IOException CreateSimulatedFailure() => new(SimulatedFailureMessage);

    /// <summary>
    /// Reads the journal record type, the string <c>"t"</c> property of a JSON object line.
    /// Never throws: a torn, non-JSON or non-object line, or a missing or non-string <c>"t"</c>,
    /// yields <see langword="null"/>.
    /// </summary>
    private static string? ReadRecordType(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("t", out JsonElement type)
                && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Text that cannot be transcoded to UTF-8, such as a lone surrogate, is not a record.
            return null;
        }
    }

    private AppendAction SelectActionLocked(int callIndex, string? recordType, out Exception? failure)
    {
        failure = null;

        bool blockAtIndex = _blockAt.Remove(callIndex);
        if (_failAt.Remove(callIndex, out Exception? failAtException))
        {
            failure = failAtException;
            return AppendAction.Fail;
        }

        if (blockAtIndex)
        {
            return AppendAction.Block;
        }

        int failNext = _failNext.FindIndex(rule => Matches(rule.RecordType, recordType));
        if (failNext >= 0)
        {
            failure = _failNext[failNext].Exception;
            _failNext.RemoveAt(failNext);
            return AppendAction.Fail;
        }

        int blockNext = _blockNext.FindIndex(rule => Matches(rule, recordType));
        if (blockNext >= 0)
        {
            _blockNext.RemoveAt(blockNext);
            return AppendAction.Block;
        }

        foreach (FailRule rule in _failAll)
        {
            if (Matches(rule.RecordType, recordType))
            {
                failure = rule.Exception;
                return AppendAction.Fail;
            }
        }

        foreach (string? rule in _blockAll)
        {
            if (Matches(rule, recordType))
            {
                return AppendAction.Block;
            }
        }

        return AppendAction.Succeed;
    }

    private void CommitLocked(AppendCall call)
    {
        _journalLines.Add(call.Line);
        _durableLines.Add(call.Line);
        _journalPresent = true;
        _isDeleted = false;
        call.Outcome = AppendOutcome.Succeeded;
    }

    private readonly record struct FailRule(string? RecordType, Exception? Exception);

    /// <summary>
    /// An append waiting on its own gate. The resolver sets <see cref="Failure"/> and the outcome,
    /// then the gate, all under the instance's lock; the waiting thread disposes the gate.
    /// </summary>
    private sealed class BlockedAppend
    {
        public BlockedAppend(AppendCall call)
        {
            Call = call;
        }

        public AppendCall Call { get; }

        public ManualResetEventSlim Gate { get; } = new(initialState: false);

        public Exception? Failure { get; set; }
    }
}

