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
/// same journal, and is the only caller of <see cref="MoveAside"/>. Implementations therefore need
/// not support concurrent appends.
/// </para>
/// <para>
/// Sharing: while an implementation holds the journal open, other processes and other instances
/// must still be able to open it for reading. Tests read the journal of a live
/// <c>RamQuick.TestHost</c> process, and <see cref="ReadLines"/> and <see cref="ReadContent"/> must
/// succeed while another process holds the journal open for appending.
/// </para>
/// <para>
/// Untrusted input: the journal is a file in a user-writable folder, so recovery reads it through
/// <see cref="ReadContent"/>, which never holds more of it than <see cref="JournalReadLimits"/>
/// allows, and reports what it left unread as damage rather than failing.
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
    /// Reads the journal's lines in the order written, as <see cref="ReadLines"/> does, holding no more
    /// of it than <see cref="JournalReadLimits"/> allows, and reports what it left unread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal lies in a user-writable folder, so its size is not trusted. A line longer than
    /// <see cref="JournalReadLimits.MaxLineBytes"/> is left out, and reading stops after
    /// <see cref="JournalReadLimits.MaxJournalBytes"/> bytes or <see cref="JournalReadLimits.MaxLines"/>
    /// lines; <see cref="JournalContent.Damage"/> then says why. A final line without a terminator is
    /// returned as stored, as by <see cref="ReadLines"/>, and <see cref="JournalContent.LastLineUnterminated"/>
    /// reports it.
    /// </para>
    /// <para>
    /// The default implementation, for storages that hold their lines in memory, returns
    /// <see cref="ReadLines"/> with no damage and no unterminated last line.
    /// </para>
    /// </remarks>
    /// <returns>The lines read and the reason any part of the journal was left unread.</returns>
    /// <exception cref="IOException">An existing journal could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies read access.</exception>
    JournalContent ReadContent() => new(ReadLines(), Damage: null);

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

    /// <summary>
    /// Closes any handle this instance holds open on the journal, then moves the journal to a name
    /// beside it that no later run uses, so that the next run starts a new journal while the damaged
    /// one stays available as evidence.
    /// </summary>
    /// <remarks>
    /// Called by <c>CrashDetector</c> instead of <see cref="Delete"/> once every run of a journal that
    /// held damaged data is published and has its <c>end</c> record. When it throws, the journal stays
    /// where it was.
    /// </remarks>
    /// <param name="utc">
    /// The time recovery retired the journal; the new name carries it, as
    /// <c>active-run.damaged-&lt;yyyyMMdd-HHmmss&gt;.jsonl</c> in UTC for a file.
    /// </param>
    /// <returns>A diagnostic description of where the journal now is, typically its full file path.</returns>
    /// <exception cref="IOException">The journal does not exist or could not be moved.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies the move.</exception>
    string MoveAside(DateTimeOffset utc);
}

/// <summary>
/// The bounds within which recovery reads the progress journal and the summary files beside it. Both lie
/// in a user-writable folder, so their size is not trusted: input beyond a bound is left unread and
/// reported as damage, never allocated.
/// </summary>
/// <remarks>
/// <para>
/// Each bound leaves a wide margin above what a run writes. An Exhaustive run journals about 1 MB; its
/// largest record, the <c>begin</c> record holding the full plan, is tens of kilobytes, and its summaries
/// are smaller still.
/// </para>
/// <para>
/// A JSON summary is checked against <see cref="MaxSummaryDepth"/>, <see cref="MaxSummaryArrayLength"/> and
/// <see cref="MaxSummaryStringLength"/> as a stream of tokens before anything of it is materialised, and,
/// once read, its steps table is held to the plan's bounds, at most <see cref="MaxPlanSteps"/> steps, so that
/// rendering a kept summary as text costs no more than the file it came from.
/// </para>
/// </remarks>
public static class JournalReadLimits
{
    /// <summary>
    /// The most bytes of a journal that are read: 16 MiB. Reading stops at this bound, and a line it cuts
    /// short is left out.
    /// </summary>
    public const int MaxJournalBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The longest journal line that is read, in bytes without its terminator: 1 MiB. A longer line is
    /// skipped without being held in memory.
    /// </summary>
    public const int MaxLineBytes = 1024 * 1024;

    /// <summary>
    /// The most journal lines that are read: 262,144, more than 70 times the one-a-second heartbeats of an
    /// hour-long Exhaustive run.
    /// </summary>
    public const int MaxLines = 262_144;

    /// <summary>
    /// The most plan steps a recovered <c>begin</c> record may hold: 1,024. A longer plan is not rebuilt
    /// into a summary.
    /// </summary>
    public const int MaxPlanSteps = 1_024;

    /// <summary>
    /// The largest summary file recovery reads to decide whether it is the recovered run's own: 32 MiB.
    /// A larger file is never read and counts as another run's.
    /// </summary>
    public const int MaxSummaryBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The deepest nesting of objects and arrays a JSON summary recovery keeps may hold: 16. The schema nests
    /// five deep, at an error record's <c>bitPositions</c>. A deeper document counts as another run's.
    /// </summary>
    public const int MaxSummaryDepth = 16;

    /// <summary>
    /// The most elements any one array of a JSON summary recovery keeps may hold: 10,000, the cap on the error
    /// records a run stores. A document with a longer array counts as another run's.
    /// </summary>
    public const int MaxSummaryArrayLength = 10_000;

    /// <summary>
    /// The longest string, value or property name, that a JSON summary recovery keeps may hold: 16,384
    /// characters once unescaped. No run writes one of more than a few hundred. A document with a longer
    /// string counts as another run's, and recovery rebuilds no summary that holds one.
    /// </summary>
    public const int MaxSummaryStringLength = 16_384;
}

/// <summary>
/// What <see cref="IJournalStorage.ReadContent"/> read from the journal.
/// </summary>
/// <param name="Lines">The lines read, in the order written, without terminators.</param>
/// <param name="Damage">
/// Why part of the journal was left unread within <see cref="JournalReadLimits"/>, as a phrase without a
/// trailing period, or <see langword="null"/> when all of it was read.
/// </param>
/// <param name="LastLineUnterminated">
/// Whether the last of <paramref name="Lines"/> is the end of the journal with no terminator after it, as an
/// append cut short by a crash leaves it: only such a line can be torn. <see langword="false"/> when the last
/// line read was terminated, when there are no lines, and when a storage cannot tell, as the default
/// <see cref="IJournalStorage.ReadContent"/> cannot.
/// </param>
public sealed record JournalContent(IReadOnlyList<string> Lines, string? Damage, bool LastLineUnterminated = false);
