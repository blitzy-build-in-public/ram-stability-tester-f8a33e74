namespace RamQuick.Logging;

/// <summary>
/// Durable, line-oriented storage for the progress journal (<c>&lt;data&gt;\journal\active-run.jsonl</c>):
/// the seam between the journal's record logic and the file system.
/// </summary>
/// <remarks>
/// <para>
/// Threading: instances are not thread-safe, and a single thread uses an instance at a time.
/// During a run, <c>ProgressJournal</c>'s writer thread owns its instance and is the only thread
/// that calls <see cref="AppendDurable"/>, <see cref="Delete"/> and <see cref="IDisposable.Dispose"/>
/// on it. At launch, before any run starts, <c>CrashDetector</c> uses a separate instance over the
/// same journal. Implementations therefore need not support concurrent appends.
/// </para>
/// <para>
/// Sharing: while an implementation holds the journal open, other processes and other instances
/// must still be able to open it for reading. Tests read the journal of a live
/// <c>RamQuick.TestHost</c> process, and <see cref="ReadLines"/> must succeed while another process
/// holds the journal open for appending.
/// </para>
/// <para>
/// Lifetime: <see cref="IDisposable.Dispose"/> closes any handle the instance holds open and leaves
/// the journal in place, so a run that ends without its <c>end</c> record stays recoverable.
/// No other member may be called after it.
/// </para>
/// <para>
/// Production code uses <c>FileJournalStorage</c>; tests substitute <c>StalledJournalStorage</c>,
/// which blocks or fails on demand to exercise the stalled-writer paths.
/// </para>
/// </remarks>
public interface IJournalStorage : IDisposable
{
    /// <summary>
    /// Gets a diagnostic description of where the journal is stored, typically its full file path.
    /// </summary>
    /// <remarks>
    /// Used in failure messages such as "Progress journal could not be written: &lt;error&gt;".
    /// The value never changes for an instance and may be read from any thread.
    /// </remarks>
    string Location { get; }

    /// <summary>
    /// Returns whether a journal is present at <see cref="Location"/>.
    /// </summary>
    /// <remarks>Called by the thread that currently uses the instance.</remarks>
    /// <returns><see langword="true"/> when a journal exists, even an empty or torn one.</returns>
    bool Exists();

    /// <summary>
    /// Appends one record, <paramref name="line"/> followed by a single <c>\n</c> terminator, and
    /// returns only once that record is durable: written through to the storage device and flushed
    /// (<c>FileOptions.WriteThrough</c> followed by <c>Flush(true)</c> for a file).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is stored as UTF-8 without a byte-order mark. The journal is created when it does
    /// not exist, including after <see cref="Delete"/>.
    /// </para>
    /// <para>
    /// The call may block for an arbitrarily long time; the test substitute does so on purpose.
    /// Only <c>ProgressJournal</c>'s writer thread calls it, so callers that must bound their wait,
    /// such as the step gate, wait on the record's acknowledgment rather than on this call.
    /// </para>
    /// </remarks>
    /// <param name="line">The serialized record. The caller guarantees that it contains no <c>\r</c> or <c>\n</c>.</param>
    /// <exception cref="IOException">The record could not be written or made durable.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies write access.</exception>
    void AppendDurable(string line);

    /// <summary>
    /// Reads every line of the journal in the order written, without terminators.
    /// </summary>
    /// <remarks>
    /// The final line is returned as stored even when it is torn, that is, cut short by a crash
    /// before its terminator was written; it may then hold incomplete text. Dropping it is the
    /// parser's job (<c>JournalLineSerializer.ParseAll</c>), not the storage's. Called by
    /// <c>CrashDetector</c> at launch and by tests, including while another process holds the
    /// journal open for appending.
    /// </remarks>
    /// <returns>The journal's lines, or an empty list when no journal exists.</returns>
    /// <exception cref="IOException">An existing journal could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies read access.</exception>
    IReadOnlyList<string> ReadLines();

    /// <summary>
    /// Closes any handle this instance holds open on the journal, then deletes the journal.
    /// Does nothing when no journal exists.
    /// </summary>
    /// <remarks>
    /// Called by the instance's owning thread once the run's summaries are published and the
    /// <c>end</c> record is durable, or by <c>CrashDetector</c> after recovery. When it throws, the
    /// journal stays in place and the next launch recovers it again.
    /// </remarks>
    /// <exception cref="IOException">An existing journal could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies deletion.</exception>
    void Delete();
}
