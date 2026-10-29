namespace RamQuick.Logging;

/// <summary>
/// What <c>CrashDetector.Detect</c> found in the progress journal at launch, and therefore what the window
/// shows before the first Start.
/// </summary>
public enum JournalRecoveryKind
{
    /// <summary>
    /// Nothing to report: there was no journal, it held no <c>begin</c> record, so no test ever started, or
    /// the run's summaries were already published, as its <c>end</c> record or a final summary carrying its
    /// run id shows. The window stays Idle with no recovery message.
    /// </summary>
    None,

    /// <summary>
    /// The journal held a <c>result</c> record but no <c>end</c> record: the previous run reached its
    /// verdict, and the process ended before both summaries were published. Each missing summary has now
    /// been rebuilt from the journal and published. The window stays Idle, and its notice line reads
    /// "Results of the previous test were recovered and saved."
    /// </summary>
    CompletedUnpublished,

    /// <summary>
    /// The journal held no <c>result</c> record: the previous run never reached a verdict, because the
    /// system hung or rebooted or the process was killed. A partial FAIL summary has been rebuilt from the
    /// journal and published. The window moves Idle → Failed, and its result panel reads "The previous test
    /// did not finish at step n of N (name), which likely indicates severe instability.", or, when the run
    /// stopped before step 1, "The previous test did not finish while preparing memory (before step 1),
    /// which likely indicates severe instability."
    /// </summary>
    Unfinished,
}

/// <summary>
/// The outcome of journal recovery at launch: which case applied, where an unfinished run stopped, and what
/// recovery published. Returned by <c>CrashDetector.Detect</c> and read by <c>RunController</c>.
/// </summary>
/// <remarks>
/// <para>
/// Data only. <c>RunController</c> composes every user-visible message from <c>UiText</c> using these
/// values; no message text is held here.
/// </para>
/// <para>
/// Numbering: like every step number persisted by <c>RamQuick.Logging</c>, <see cref="LastStepNumber"/> is
/// 1-based, matching the UI's "step n of N", so it is used as it stands, with no conversion.
/// </para>
/// <para>
/// Immutability: <see cref="PublishedFiles"/> is a read-only copy of the list supplied at construction, so
/// an instance cannot change after <c>CrashDetector</c> returns it to another thread. Value equality
/// compares <see cref="PublishedFiles"/> element by element.
/// </para>
/// </remarks>
public sealed record JournalRecovery
{
    private readonly IReadOnlyList<string> _publishedFiles = Array.Empty<string>();

    /// <summary>
    /// Gets the shared result for a launch with nothing to recover: <see cref="Kind"/> is
    /// <see cref="JournalRecoveryKind.None"/> and every other member holds its default.
    /// </summary>
    public static JournalRecovery None { get; } = new() { Kind = JournalRecoveryKind.None };

    /// <summary>
    /// Gets which recovery case applied, and so which message the window shows.
    /// </summary>
    public JournalRecoveryKind Kind { get; init; }

    /// <summary>
    /// Gets the recovered run's id, from its <c>begin</c> record, or <see langword="null"/> when no run was
    /// identified.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// Gets the results file base name the recovered run reserved,
    /// <c>RamQuick-&lt;yyyyMMdd-HHmmss&gt;-&lt;TestType&gt;-&lt;runId&gt;</c>, shared by its <c>.log</c> and
    /// both summaries; <see langword="null"/> when no run was identified.
    /// </summary>
    public string? LogBaseName { get; init; }

    /// <summary>
    /// Gets the recovered run's test type name, for example <c>"Quick"</c>, or <see langword="null"/> when no
    /// run was identified.
    /// </summary>
    public string? TestType { get; init; }

    /// <summary>
    /// Gets a value indicating whether an <see cref="JournalRecoveryKind.Unfinished"/> run stopped while
    /// preparing memory: its journal held no <c>step</c> record. The result panel then reads "The previous
    /// test did not finish while preparing memory (before step 1), which likely indicates severe
    /// instability." Always <see langword="false"/> for the other kinds.
    /// </summary>
    public bool InPreparation { get; init; }

    /// <summary>
    /// Gets the 1-based number of the last step an <see cref="JournalRecoveryKind.Unfinished"/> run started,
    /// the n of "did not finish at step n of N (name)"; <see langword="null"/> when
    /// <see cref="InPreparation"/> is <see langword="true"/> and for the other kinds.
    /// </summary>
    public int? LastStepNumber { get; init; }

    /// <summary>
    /// Gets the number of steps in an <see cref="JournalRecoveryKind.Unfinished"/> run's plan, the N of "did
    /// not finish at step n of N (name)"; <see langword="null"/> for the other kinds.
    /// </summary>
    public int? StepCount { get; init; }

    /// <summary>
    /// Gets the name of the step at <see cref="LastStepNumber"/>, the name of "did not finish at step n of N
    /// (name)"; <see langword="null"/> when <see cref="LastStepNumber"/> is.
    /// </summary>
    public string? LastStepName { get; init; }

    /// <summary>
    /// Gets the UTC time of an <see cref="JournalRecoveryKind.Unfinished"/> run's last heartbeat, the
    /// approximate time it stopped; <see langword="null"/> when no heartbeat was written, it did not capture
    /// its time, or the kind is another.
    /// </summary>
    public DateTimeOffset? LastHeartbeatUtc { get; init; }

    /// <summary>
    /// Gets the recovered run's summary: an existing final summary kept because it parsed with the run's id,
    /// otherwise the one rebuilt from the journal. A rebuilt summary is reported even when
    /// <see cref="PublicationError"/> shows it could not be written. <see langword="null"/> when recovery
    /// neither kept nor rebuilt one.
    /// </summary>
    public RunSummary? Summary { get; init; }

    /// <summary>
    /// Gets the full paths of the files this recovery wrote, in the order written: a read-only copy of the
    /// list supplied at construction. Empty when every summary already existed or nothing was written.
    /// </summary>
    public IReadOnlyList<string> PublishedFiles
    {
        get => _publishedFiles;

        // The snapshot is taken here rather than through the journal records' shared helper, so this
        // type depends on nothing in the library but RunSummary.
        init => _publishedFiles = value is null || value.Count == 0
            ? Array.Empty<string>()
            : Array.AsReadOnly(value.ToArray());
    }

    /// <summary>
    /// Gets the reason a summary could not be written, or <see langword="null"/> when every publication
    /// succeeded. When it is set the journal was kept, so the next launch retries publication, and the
    /// notice line reads "Results could not be saved: " followed by this text.
    /// </summary>
    public string? PublicationError { get; init; }

    /// <summary>
    /// Gets a value indicating whether recovery deleted the journal. It stays <see langword="false"/> when
    /// there was no journal, when a publication failed and the journal was kept for the next launch, or when
    /// the delete itself failed; the next launch then finishes the cleanup.
    /// </summary>
    public bool JournalDeleted { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Kind"/> is <see cref="JournalRecoveryKind.Unfinished"/>, the
    /// case that moves the window Idle → Failed.
    /// </summary>
    public bool IsUnfinished => Kind == JournalRecoveryKind.Unfinished;

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing
    /// <see cref="PublishedFiles"/> element by element.
    /// </summary>
    /// <param name="other">The recovery outcome to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(JournalRecovery? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Kind == other.Kind
            && RunId == other.RunId
            && string.Equals(LogBaseName, other.LogBaseName, StringComparison.Ordinal)
            && string.Equals(TestType, other.TestType, StringComparison.Ordinal)
            && InPreparation == other.InPreparation
            && LastStepNumber == other.LastStepNumber
            && StepCount == other.StepCount
            && string.Equals(LastStepName, other.LastStepName, StringComparison.Ordinal)
            && LastHeartbeatUtc == other.LastHeartbeatUtc
            && Summary == other.Summary
            && PublishedFiles.SequenceEqual(other.PublishedFiles, StringComparer.Ordinal)
            && string.Equals(PublicationError, other.PublicationError, StringComparison.Ordinal)
            && JournalDeleted == other.JournalDeleted);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(JournalRecovery)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(RunId);
        hash.Add(LogBaseName, StringComparer.Ordinal);
        hash.Add(TestType, StringComparer.Ordinal);
        hash.Add(InPreparation);
        hash.Add(LastStepNumber);
        hash.Add(StepCount);
        hash.Add(LastStepName, StringComparer.Ordinal);
        hash.Add(LastHeartbeatUtc);
        hash.Add(Summary);
        foreach (string path in PublishedFiles)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        hash.Add(PublicationError, StringComparer.Ordinal);
        hash.Add(JournalDeleted);
        return hash.ToHashCode();
    }
}
