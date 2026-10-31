using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// Recovers, at launch, the progress journal a previous run left behind
/// (<c>&lt;data&gt;\journal\active-run.jsonl</c>): it works out how that run ended, publishes whichever of its
/// summaries are missing, and then retires the journal.
/// </summary>
/// <remarks>
/// <para>
/// Classification: a journal that holds nothing a run writes, being empty, blank or holding only one line that
/// does not parse and ends the journal without a terminator (<see cref="JournalContent.LastLineUnterminated"/>),
/// a <c>begin</c> record torn by a crash during its own append, never carried test traffic, because
/// <c>begin</c> is acknowledged before <c>rq_session_start</c>, so it is deleted and nothing is reported. A
/// complete line that does not parse is no torn append: it stands for a <c>begin</c> that could not be read,
/// and its run is reported. A run with an <c>end</c> record has published its summaries, so its journal is
/// deleted. A run
/// with a <c>result</c> record but no <c>end</c> is <see cref="JournalRecoveryKind.CompletedUnpublished"/>. A
/// run without a <c>result</c> is <see cref="JournalRecoveryKind.Unfinished"/>: its partial summary reports
/// FAIL with end reason <see cref="RunEndReason.PreviousRunUnfinished"/> and the step at which it stopped.
/// </para>
/// <para>
/// Summaries: a file under a summary's final name that parses with the run's id is kept. A missing summary is
/// rebuilt by <see cref="RunSummary.Build"/> from the journal records alone, never from this launch's machine
/// state, and published through <see cref="ResultsLog.PublishFile"/>, the JSON before the text. A final name
/// held by a file that does not parse (<see cref="RunSummaryJson.TryDeserialize"/> refuses it), cannot be read,
/// is larger than <see cref="JournalReadLimits.MaxSummaryBytes"/> or names another run, or by a folder or a
/// reparse point, which is never followed or read, is left untouched, and the rebuilt summary goes to the
/// recovered name, <c>&lt;base&gt;-recovered-summary.json</c> or <c>.txt</c>, instead. So is a JSON file
/// beyond the summary bounds: one nested deeper than <see cref="JournalReadLimits.MaxSummaryDepth"/>, holding an
/// array longer than <see cref="JournalReadLimits.MaxSummaryArrayLength"/> or a string longer than
/// <see cref="JournalReadLimits.MaxSummaryStringLength"/>, which is refused as its tokens are read and before
/// anything of it is materialised, or one whose steps table exceeds the plan's bounds, more than
/// <see cref="JournalReadLimits.MaxPlanSteps"/> steps, a step name longer than 63 UTF-8 bytes or another step
/// cell longer than 64 characters, which is refused before it could be rendered as text. Every summary recovery
/// rebuilds meets those bounds, so a retry keeps it. Rebuilt summaries carry no error detail and no cause hints.
/// The crashed run's <c>.log</c> is never modified; it keeps whatever detail it had flushed.
/// </para>
/// <para>
/// Untrusted input: the journal lies in a user-writable folder, so it is read through
/// <see cref="IJournalStorage.ReadContent"/> within <see cref="JournalReadLimits"/>, and what it holds is
/// checked before it is used. Damaged data never blocks publication, because a journal kept for it would
/// refuse every later Start: recovery publishes what the rest of the journal holds, names the first damage in
/// <see cref="JournalRecovery.JournalDamage"/> and in each rebuilt summary's <c>run.endDetail</c>, and then
/// moves the journal aside (<see cref="IJournalStorage.MoveAside"/>) instead of deleting it, so it is kept as
/// evidence. A journal that cannot be moved is never deleted instead: it stays in place with the <c>end</c>
/// records recovery appended, so the next launch publishes nothing again and retries the move. Damage is any of
/// these:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Part of the journal was left unread within the bounds.
/// </description></item>
/// <item><description>
/// A <c>begin</c> line that does not parse: each of its fields that reads on its own is kept, and the rest are
/// not captured (<see cref="JournalLineSerializer.TrySalvageBegin"/>). Lines before the first <c>begin</c> form
/// a run of their own with an unknown <c>begin</c>, unless they are blank or only a torn final line.
/// </description></item>
/// <item><description>
/// A <c>begin</c> repeating the run id of an earlier one, which continues that run.
/// </description></item>
/// <item><description>
/// A <c>stepEnd</c>, <c>result</c>, <c>released</c> or <c>end</c> record naming another run, which is
/// ignored. A run's id is its <c>begin</c> record's, or, when that cannot be read, the first id its other
/// records name, so a <c>result</c> another run or a tampered journal left beside it never becomes its verdict.
/// </description></item>
/// <item><description>
/// A value no run writes: a quantity above 2^53 - 1, the bound the summary schema keeps every integer within,
/// a negative plan budget, a step name longer than the plan's 63 UTF-8 bytes, or a plan of more than
/// <see cref="JournalReadLimits.MaxPlanSteps"/> steps. A total the summary derives is held to the same bound:
/// the plan's time and coverage targets, and the error total of a run without a <c>result</c>, which sums the
/// last <c>stepEnd</c> of each step. The record or value is left out: a damaged <c>result</c> leaves the run
/// unfinished, a damaged plan leaves the plan unknown, and a <c>step</c> record keeps its position without its
/// name. A plan whose only damage is a long step name stands, since every step name is cut to that limit where
/// it is shown.
/// </description></item>
/// <item><description>
/// Free text longer than 1,024 characters, which no run writes: the <c>begin</c> record's test type, app
/// version, OS or CPU name, or the <c>result</c> record's verdict or end detail. The text is left out; a
/// <c>result</c> is kept without it, its verdict then taken from its terminal state.
/// </description></item>
/// <item><description>
/// Heartbeats or step results without any <c>step</c> record, or a heartbeat naming a later step than the last
/// <c>step</c> record, whose own record the engine writes before that step reports progress. The step at which
/// the run stopped is then unknown, and no earlier step stands in for it.
/// </description></item>
/// <item><description>
/// Values a summary cannot be rendered from, or whose summary would exceed the summary bounds: the run is then
/// published as a minimal unfinished summary that keeps only its id and test type.
/// </description></item>
/// </list>
/// <para>
/// Untrusted text: a journal's text reaches the window and the summaries, so
/// <see cref="JournalRecovery.LastStepName"/> and the rebuilt summaries' step names pass through
/// <see cref="SummaryFormat.StepName"/>, and each is one line of plain text of at most
/// <see cref="SummaryFormat.MaxStepNameUtf8Bytes"/> UTF-8 bytes. A <c>step</c> record whose name that rule
/// leaves blank is named by the <c>begin</c> record's definition with the same number. A base name holding a
/// character <see cref="SummaryFormat.PlainText"/> replaces, or one <see cref="ResultsLog"/> refuses as a file
/// name, is replaced by <c>RamQuick-recovered-&lt;runId&gt;</c>.
/// </para>
/// <para>
/// Crash consistency: the journal is retired only once every summary it describes is on disk and an
/// <c>end</c> record has been appended for each run recovered here. When a publication fails, no <c>end</c> is
/// written, the journal is kept so the next launch retries, and <see cref="JournalRecovery.PublicationError"/>
/// carries the reason. A launch that stops anywhere in this procedure leaves a journal the next launch recovers
/// to the same files, so the procedure is idempotent: a run whose id is unknown is named after a hash of its
/// first line, which a retry reads again.
/// </para>
/// <para>
/// Already published: an unfinished run whose final JSON summary already exists with its id is reported as
/// <see cref="JournalRecoveryKind.None"/> with that summary, and only a missing text summary is rendered from
/// it. That happens when the journal failed during the run after the results were published. This case
/// extends the plan's recovery cases, so that a journal write failure never turns published results into a
/// false "did not finish" report. A kept summary that is itself a partial one recovery rebuilt does not count:
/// the run stays unfinished, so its FAIL is shown on every launch until the journal is retired.
/// </para>
/// <para>
/// Several runs: a journal kept by a failed recovery receives the next run's records after its own, so the
/// journal is split into runs at each <c>begin</c> record. An <c>end</c> record belongs to the run whose id it
/// names, or, without an id, to the nearest run before it whose id is unknown. Every run is recovered as above,
/// the returned outcome describes the latest run, the one whose records end the journal, even when a repeated
/// <c>begin</c> continued it after a later run's records, and <see cref="JournalRecovery.PublishedFiles"/> lists
/// the files written for all of them.
/// </para>
/// <para>
/// Threading: stateless and thread-safe. It performs synchronous file I/O, so <c>RunController</c> calls it on
/// the thread pool, never on the UI thread. It never throws for an I/O failure, a damaged journal or a damaged
/// summary file. An I/O failure is reported through <see cref="JournalRecovery.PublicationError"/>; a malformed
/// line other than a <c>begin</c> is skipped, and a summary file that does not parse is left untouched as
/// another run's. Files are written only under <c>&lt;data&gt;\logs\</c> and <c>&lt;data&gt;\journal\</c>.
/// </para>
/// </remarks>
public static class CrashDetector
{
    // ResultsLog refuses a base name that is not a plain file name or is too long, and recovery refuses one holding
    // a control, format or separator character. A begin record damaged either way still has its results published,
    // under this prefix followed by the run id, so its journal can be retired.
    private const string FallbackBaseNamePrefix = "RamQuick-recovered-";

    private const string JournalUnreadablePrefix = "The progress journal could not be read: ";

    private const string BeginRecord = "the begin record";

    private const string MissingBeginDamage = "the journal's begin record is missing or could not be read";

    private const string DamagedBeginDamage = "the begin record was damaged";

    private const string RepeatedBeginDamage = "a repeated begin record";

    private const string MissingStepsDamage = "the run's step records are missing or could not be read";

    private const string UnrenderableDamage = "the summary could not be rendered from the journal's values";

    // The summary schema keeps every integer quantity below 2^53, so that a JSON number reads back exactly. No run
    // writes a larger one, so a journal value above this bound is damage, and no summary states it.
    private const long MaxQuantity = (1L << 53) - 1;

    // A run whose id is unknown is named by this many hex digits of a hash of its first line.
    private const int AnonymousNameBytes = 16;

    // The plan's limit on a step name, StepDefinition.MaxNameUtf8Bytes in RamQuick.Plans, which this library does not
    // reference. A longer name is a value no run writes, and the text summary pads every step row to the longest.
    private const int MaxStepNameUtf8Bytes = 63;

    // The longest free text a record may carry into a summary: the test type, app version, OS and CPU names of the
    // begin record, and the verdict and end detail of the result record. No run writes more than a few hundred
    // characters there. Held to this, a rebuilt end detail stays within JournalReadLimits.MaxSummaryStringLength
    // even after the damage note is appended and redaction widens each personal name to its placeholder.
    private const int MaxRecordTextLength = 1_024;

    // No byte-order mark, and malformed bytes are read as U+FFFD rather than throwing.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly byte[] _utf8ByteOrderMark = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// Recovers the progress journal at <see cref="DataDirectory.JournalPath"/>, if there is one.
    /// </summary>
    /// <param name="dataDirectory">The data folder that holds the journal and the results logs.</param>
    /// <param name="timeProvider">
    /// The clock that stamps the <c>end</c> records recovery appends; <see cref="TimeProvider.System"/> when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>What recovery found and did; <see cref="JournalRecovery.None"/> when there is no journal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataDirectory"/> is <see langword="null"/>.</exception>
    public static JournalRecovery Detect(DataDirectory dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Detect(
            dataDirectory,
            new FileJournalStorage(dataDirectory.JournalPath),
            timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// Recovers the progress journal held by <paramref name="storage"/>, publishing summaries under
    /// <see cref="DataDirectory.LogsDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">The data folder whose <c>logs</c> folder receives the summaries.</param>
    /// <param name="storage">
    /// The journal. Ownership passes to this method: the storage is disposed before it returns or throws.
    /// </param>
    /// <param name="timeProvider">The clock that stamps the <c>end</c> records recovery appends.</param>
    /// <returns>What recovery found and did; <see cref="JournalRecovery.None"/> when there is no journal.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static JournalRecovery Detect(DataDirectory dataDirectory, IJournalStorage storage, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(storage);
        using (storage)
        {
            ArgumentNullException.ThrowIfNull(dataDirectory);
            ArgumentNullException.ThrowIfNull(timeProvider);
            return Recover(dataDirectory, storage, timeProvider);
        }
    }

    private static JournalRecovery Recover(DataDirectory dataDirectory, IJournalStorage storage, TimeProvider timeProvider)
    {
        if (!storage.Exists())
        {
            return JournalRecovery.None;
        }

        JournalContent content;
        try
        {
            content = storage.ReadContent();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            // Nothing is known about the run, so the journal stays for the next launch to recover.
            return new JournalRecovery { PublicationError = JournalUnreadablePrefix + exception.Message };
        }

        IReadOnlyList<RunRecords> runs = RunRecords.Split(content.Lines, content.Damage, content.LastLineUnterminated);
        if (runs.Count == 0)
        {
            return new JournalRecovery { JournalDeleted = TryDelete(storage) };
        }

        // Every run is recovered in journal order. The one reported is the run whose records end the journal, which a
        // repeated begin can make an earlier one.
        var publication = new Publication();
        JournalRecovery latest = JournalRecovery.None;
        int latestSegment = -1;
        foreach (RunRecords run in runs)
        {
            JournalRecovery recovered = RecoverRun(run, dataDirectory, publication);
            if (run.LastSegment > latestSegment)
            {
                latest = recovered;
                latestSegment = run.LastSegment;
            }
        }

        // Reading comes first, so a part of the journal left unread is the first damage found.
        string? damage = content.Damage ?? runs.Select(run => run.FirstDamage).FirstOrDefault(found => found is not null);
        bool deleted = false;
        string? movedTo = null;
        if (publication.Error is null)
        {
            DateTimeOffset utc = timeProvider.GetUtcNow();
            foreach (RunRecords run in runs)
            {
                if (!run.Ended)
                {
                    TryAppendEnd(storage, run.RunId, utc);
                }
            }

            // A damaged journal is kept as evidence under a name no later run uses. When it cannot be moved it stays
            // where it is, never deleted: its runs are published and carry their end records, so it refuses no
            // Start, and the next launch, finding nothing left to publish, retries the move.
            if (damage is null)
            {
                deleted = TryDelete(storage);
            }
            else
            {
                movedTo = TryMoveAside(storage, utc);
            }
        }

        return latest with
        {
            PublishedFiles = publication.Files,
            PublicationError = publication.Error,
            JournalDeleted = deleted,
            JournalDamage = damage,
            DamagedJournalPath = movedTo,
        };
    }

    private static JournalRecovery RecoverRun(RunRecords run, DataDirectory dataDirectory, Publication publication)
    {
        // Rendering comes first: a run whose values no summary can be rendered from is reduced to an unfinished
        // run first, and is then recovered as one.
        RunDocuments? documents = run.Ended ? null : run.Render();
        string baseName = UsableBaseName(dataDirectory, run);
        var identified = new JournalRecovery
        {
            RunId = run.RunId == Guid.Empty ? null : run.RunId,
            LogBaseName = baseName,
            TestType = run.Begin.TestType.Length > 0 ? run.Begin.TestType : null,
        };

        if (documents is null)
        {
            return identified;
        }

        var files = new SummaryFiles(dataDirectory, baseName, run.RunId);
        return run.Result is not null
            ? RecoverCompleted(files, documents, publication, identified)
            : RecoverUnfinished(run, files, documents, publication, identified);
    }

    // Each missing summary is rebuilt from the journal, even beside a kept JSON summary that holds the run's error
    // detail, so that a rebuilt file only ever states what the journal captured.
    private static JournalRecovery RecoverCompleted(
        SummaryFiles files,
        RunDocuments documents,
        Publication publication,
        JournalRecovery identified)
    {
        Placement<RunSummary> json = files.PlaceJson();
        Placement<string> text = files.PlaceText();
        publication.Publish(json, documents.Json);
        publication.Publish(text, documents.Text);
        return identified with
        {
            Kind = JournalRecoveryKind.CompletedUnpublished,
            Summary = json.Kept ?? documents.Summary,
        };
    }

    private static JournalRecovery RecoverUnfinished(
        RunRecords run,
        SummaryFiles files,
        RunDocuments documents,
        Publication publication,
        JournalRecovery identified)
    {
        Placement<RunSummary> json = files.PlaceJson();
        if (json.IsKeptUnderFinalName
            && json.Kept is { } published
            && !IsRecoveredPartial(published)
            && TryFormat(published) is { } publishedText)
        {
            // The results were published before the journal stopped recording, so they are authoritative and
            // the run is not reported as unfinished.
            publication.Publish(files.PlaceText(), publishedText);
            return identified with { Summary = published };
        }

        Placement<string> text = files.PlaceText();
        JournalStepStart? lastStep = run.StoppingStep;
        int stepCount = lastStep is { Count: > 0 } ? lastStep.Count : run.Begin.Steps.Count;
        publication.Publish(json, documents.Json);
        publication.Publish(text, documents.Text);
        return identified with
        {
            Kind = JournalRecoveryKind.Unfinished,
            InPreparation = run.InPreparation,
            LastStepNumber = lastStep?.Index,
            StepCount = stepCount > 0 ? stepCount : null,
            LastStepName = run.LastStepName,
            LastHeartbeatUtc = run.LastBeat?.Utc is { } utc && utc != default ? utc : null,
            Summary = json.Kept ?? documents.Summary,
        };
    }

    // A partial summary an earlier recovery published does not show that the run finished.
    private static bool IsRecoveredPartial(RunSummary summary) =>
        (summary.Run.Recovered && summary.Run.Partial)
        || string.Equals(summary.Run.EndReason, nameof(RunEndReason.PreviousRunUnfinished), StringComparison.Ordinal);

    // A kept summary is a file in a user-writable folder; one whose values cannot be rendered is not used.
    private static string? TryFormat(RunSummary summary)
    {
        try
        {
            return TextSummaryFormatter.Format(summary);
        }
        catch (Exception exception) when (IsRenderFailure(exception))
        {
            return null;
        }
    }

    // Probing through the path helper keeps one base-name rule: the one ResultsLog applies to every path. A name
    // that rule accepts can still hold a character SummaryFormat.PlainText replaces, such as a bidirectional
    // control, which would reach a published file name and every notice that names the file, so it is refused too.
    // A run whose id is unknown is named after its first line, so different journals never share names and a retry
    // maps to the same files.
    private static string UsableBaseName(DataDirectory dataDirectory, RunRecords run)
    {
        string fallback = FallbackBaseNamePrefix
            + (run.RunId == Guid.Empty ? AnonymousName(run.FirstLine) : SummaryFormat.RunId(run.RunId));
        string baseName = run.Begin.LogBaseName;
        if (!string.Equals(SummaryFormat.PlainText(baseName), baseName, StringComparison.Ordinal))
        {
            return fallback;
        }

        try
        {
            _ = ResultsLog.GetSummaryJsonPath(dataDirectory, baseName);
            return baseName;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    // The first 32 hex digits, lowercase like a run id's N form, of the SHA-256 hash of the line's UTF-8 bytes.
    private static string AnonymousName(string line)
    {
        byte[] hash = SHA256.HashData(_utf8.GetBytes(line));
        var name = new StringBuilder(AnonymousNameBytes * 2);
        foreach (byte value in hash.AsSpan(0, AnonymousNameBytes))
        {
            name.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return name.ToString();
    }

    // Finds where one summary of a run stands: kept under the first name that already holds this run's file,
    // or to be written under the first free name. A name held by anything else is skipped and never touched: a
    // file that cannot be read cannot be confirmed as this run's, and a folder or a reparse point is never read
    // or followed, so each counts as another run's.
    private static Placement<T> Place<T>(string finalPath, string recoveredPath, Func<ReadOnlyMemory<byte>, T?> readOwn)
        where T : class
    {
        foreach (string path in new[] { finalPath, recoveredPath })
        {
            SummaryEntry entry = Inspect(path);
            if (entry == SummaryEntry.Free)
            {
                return new Placement<T>(finalPath, recoveredPath, KeptPath: null, Kept: null, TargetPath: path);
            }

            if (entry == SummaryEntry.RegularFile && TryReadBytes(path) is { } content && readOwn(content) is { } own)
            {
                return new Placement<T>(finalPath, recoveredPath, KeptPath: path, Kept: own, TargetPath: null);
            }
        }

        return new Placement<T>(finalPath, recoveredPath, KeptPath: null, Kept: null, TargetPath: null);
    }

    // The entry's own attributes, which a reparse point reports without being followed.
    private static SummaryEntry Inspect(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                ? SummaryEntry.Foreign
                : SummaryEntry.RegularFile;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SummaryEntry.Free;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return SummaryEntry.Foreign;
        }
    }

    // A summary larger than the bound is refused before anything is allocated for it, and no more than its length
    // when opened is read. Its UTF-8 bytes are returned without a leading byte-order mark, so the JSON is checked
    // against the summary bounds before any of it is decoded.
    private static ReadOnlyMemory<byte>? TryReadBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length > JournalReadLimits.MaxSummaryBytes)
            {
                return null;
            }

            byte[] bytes = new byte[length];
            int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            ReadOnlyMemory<byte> content = bytes.AsMemory(0, read);
            return content.Span.StartsWith(_utf8ByteOrderMark) ? content[_utf8ByteOrderMark.Length..] : content;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return null;
        }
    }

    // A failed move leaves the journal in place, never deleted, so its evidence survives for the next launch's move.
    private static string? TryMoveAside(IJournalStorage storage, DateTimeOffset utc)
    {
        try
        {
            return storage.MoveAside(utc);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return null;
        }
    }

    // The summaries are already on disk, so a failed end append does not stop the delete that follows: the
    // next launch would otherwise report a published run again.
    private static void TryAppendEnd(IJournalStorage storage, Guid runId, DateTimeOffset utc)
    {
        try
        {
            storage.AppendDurable(JournalLineSerializer.Serialize(new JournalEnd
            {
                RunId = runId,
                Published = true,
                Utc = utc,
            }));
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            // A kept journal is finished on the next launch: its end record, or its matching summaries, show
            // that nothing remains to publish.
        }
    }

    private static bool TryDelete(IJournalStorage storage)
    {
        try
        {
            storage.Delete();
            return true;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return false;
        }
    }

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsStepNameTooLong(string name) => _utf8.GetByteCount(name) > MaxStepNameUtf8Bytes;

    // Free text a record carries into a summary, such as an end detail or the CPU's name, is held to
    // MaxRecordTextLength.
    private static bool IsTextTooLong(string text) => text.Length > MaxRecordTextLength;

    // What building or rendering a summary raises for values JSON or the text form cannot hold.
    private static bool IsRenderFailure(Exception exception) =>
        exception is ArgumentException or JsonException or InvalidOperationException;

    // What stands under one summary name.
    private enum SummaryEntry
    {
        Free,
        RegularFile,
        Foreign,
    }

    // A run's rebuilt summary and the two documents rendered from it.
    private sealed record RunDocuments(RunSummary Summary, string Json, string Text);

    // Where one summary document of a run stands. Exactly one of KeptPath and TargetPath is set, unless both
    // names hold other files, which blocks publication.
    private readonly record struct Placement<T>(
        string FinalPath,
        string RecoveredPath,
        string? KeptPath,
        T? Kept,
        string? TargetPath)
        where T : class
    {
        public bool IsKeptUnderFinalName => string.Equals(KeptPath, FinalPath, StringComparison.Ordinal);
    }

    // The final and recovered names of a run's two summaries, and how a file under them is recognised as this
    // run's: the JSON parses within the summary bounds as a summary carrying the run id, the text carries it on its
    // run-id line.
    private sealed class SummaryFiles
    {
        private readonly DataDirectory _dataDirectory;
        private readonly string _baseName;
        private readonly Guid _runId;
        private readonly string _runIdText;

        public SummaryFiles(DataDirectory dataDirectory, string baseName, Guid runId)
        {
            _dataDirectory = dataDirectory;
            _baseName = baseName;
            _runId = runId;
            _runIdText = SummaryFormat.RunId(runId);
        }

        public Placement<RunSummary> PlaceJson() => Place(
            ResultsLog.GetSummaryJsonPath(_dataDirectory, _baseName),
            ResultsLog.GetRecoveredSummaryJsonPath(_dataDirectory, _baseName),
            ReadOwnJson);

        public Placement<string> PlaceText() => Place(
            ResultsLog.GetSummaryTextPath(_dataDirectory, _baseName),
            ResultsLog.GetRecoveredSummaryTextPath(_dataDirectory, _baseName),
            ReadOwnText);

        // A document beyond the summary bounds is never materialised, and one whose steps table exceeds the plan's
        // bounds is never rendered, so neither counts as this run's whatever id it carries.
        private RunSummary? ReadOwnJson(ReadOnlyMemory<byte> content) =>
            SummaryBounds.AdmitsTokens(content.Span)
            && RunSummaryJson.TryDeserialize(_utf8.GetString(content.Span), out RunSummary? summary)
            && SummaryBounds.AdmitsSteps(summary)
            && IsOwn(summary.Run)
                ? summary
                : null;

        // A run whose id its journal never captured is named after its begin's base name or its surrogate one, and the
        // summaries recovery rebuilds for it carry no id, so a file of its own is a recovered summary without one.
        private bool IsOwn(SummaryRun run) =>
            _runId == Guid.Empty
                ? run.Id is null && run.Recovered
                : string.Equals(run.Id, _runIdText, StringComparison.Ordinal);

        // The text's run-id line reads Unknown for a run without an id, so that run's own text also states that it
        // was recovered.
        private string? ReadOwnText(ReadOnlyMemory<byte> content)
        {
            string text = _utf8.GetString(content.Span);
            return TextSummaryFormatter.TryParseRunId(text) == _runId
                && (_runId != Guid.Empty || TextSummaryFormatter.StatesRecovered(text))
                    ? text
                    : null;
        }
    }

    // The bounds a JSON summary must meet for recovery to keep it, and that every summary recovery rebuilds meets,
    // so a retry recognises its own files: the token bounds of JournalReadLimits, checked on the UTF-8 bytes before
    // anything is materialised, and the plan's bounds on the steps table, whose text form pads every row to its
    // longest cell, so that rendering a kept summary costs no more than the file it came from.
    private static class SummaryBounds
    {
        // Every step string cell other than the name is a Core enumeration's member name, all far shorter.
        private const int MaxStepTextLength = 64;

        public static bool Admits(RunSummary summary, string json) =>
            AdmitsSteps(summary) && AdmitsTokens(_utf8.GetBytes(json));

        // Reads the document token by token, never materialising a value: its nesting is held to MaxSummaryDepth by
        // the reader, each array's element count to MaxSummaryArrayLength and each string to MaxSummaryStringLength.
        // A document that is not well-formed JSON is refused as well, as the summary reader refuses it.
        public static bool AdmitsTokens(ReadOnlySpan<byte> utf8Json)
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = JournalReadLimits.MaxSummaryDepth });

            // Per open container, by depth: whether it is an array, and how many elements it has held so far.
            Span<bool> isArray = stackalloc bool[JournalReadLimits.MaxSummaryDepth];
            Span<int> elements = stackalloc int[JournalReadLimits.MaxSummaryDepth];
            char[]? unescaped = null;
            try
            {
                while (reader.Read())
                {
                    JsonTokenType token = reader.TokenType;
                    if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        continue;
                    }

                    if (token == JsonTokenType.PropertyName)
                    {
                        if (!WithinStringBound(ref reader, ref unescaped))
                        {
                            return false;
                        }

                        continue;
                    }

                    // A value is one element of the array that holds it.
                    int depth = reader.CurrentDepth;
                    if (depth > 0
                        && isArray[depth - 1]
                        && ++elements[depth - 1] > JournalReadLimits.MaxSummaryArrayLength)
                    {
                        return false;
                    }

                    if (token is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        // The reader refuses nesting beyond MaxDepth, so a container never opens past the tables.
                        if (depth >= isArray.Length)
                        {
                            return false;
                        }

                        isArray[depth] = token == JsonTokenType.StartArray;
                        elements[depth] = 0;
                    }
                    else if (token == JsonTokenType.String && !WithinStringBound(ref reader, ref unescaped))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // The steps table of a summary that read back: no more steps than a plan holds, a name within the plan's
        // limit, and every other string cell an enumeration's name could fill.
        public static bool AdmitsSteps(RunSummary summary)
        {
            if (summary.Steps.Count > JournalReadLimits.MaxPlanSteps)
            {
                return false;
            }

            foreach (SummaryStep step in summary.Steps)
            {
                if (IsStepNameTooLong(step.Name)
                    || step.Pattern.Length > MaxStepTextLength
                    || step.AccessOrder.Length > MaxStepTextLength
                    || step.ThreadMode.Length > MaxStepTextLength
                    || step.PrimaryMechanism.Length > MaxStepTextLength)
                {
                    return false;
                }
            }

            return true;
        }

        // A string's length is counted in UTF-16 characters once unescaped, which is never more than its bytes as
        // written, so only a string written longer than the bound is measured. An escaped one is unescaped into a
        // buffer of the bound's size, which a longer string overflows.
        private static bool WithinStringBound(ref Utf8JsonReader reader, ref char[]? buffer)
        {
            ReadOnlySpan<byte> written = reader.ValueSpan;
            if (written.Length <= JournalReadLimits.MaxSummaryStringLength)
            {
                return true;
            }

            if (!reader.ValueIsEscaped)
            {
                return _utf8.GetCharCount(written) <= JournalReadLimits.MaxSummaryStringLength;
            }

            buffer ??= new char[JournalReadLimits.MaxSummaryStringLength];
            try
            {
                _ = reader.CopyString(buffer);
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Longer than the buffer, or an escape that is not valid UTF-16, which the summary reader refuses.
                return false;
            }
        }
    }

    // The files one recovery wrote, in order, and the first reason a summary could not be written.
    private sealed class Publication
    {
        private readonly List<string> _files = new();

        public IReadOnlyList<string> Files => _files;

        public string? Error { get; private set; }

        // A document whose name already holds this run's file is kept; only a due one is written.
        public void Publish<T>(Placement<T> placement, string content)
            where T : class
        {
            if (placement.TargetPath is { } target)
            {
                try
                {
                    ResultsLog.PublishFile(target, content);
                    _files.Add(target);
                }
                catch (Exception exception) when (IsIoFailure(exception))
                {
                    Error ??= exception.Message;
                }
            }
            else if (placement.KeptPath is null)
            {
                Error ??= $"'{placement.FinalPath}' and '{placement.RecoveredPath}' already hold files of another run.";
            }
        }
    }

    // One journal line and the record it holds, if any. A begin line that does not parse keeps what of it reads.
    private readonly record struct JournalLine(string Text, JournalRecord? Record, string? BeginDamage)
    {
        public static JournalLine Read(string text)
        {
            if (JournalLineSerializer.TryParse(text, out JournalRecord? record))
            {
                return new JournalLine(text, record, BeginDamage: null);
            }

            if (JournalLineSerializer.TrySalvageBegin(text, out JournalBegin? begin, out IReadOnlyList<string> dropped))
            {
                string damage = dropped.Count == 0
                    ? DamagedBeginDamage
                    : $"{DamagedBeginDamage}: {string.Join(", ", dropped.Distinct(StringComparer.Ordinal))} could not be read";
                return new JournalLine(text, begin, damage);
            }

            return new JournalLine(text, Record: null, BeginDamage: null);
        }
    }

    // The records of one run: those from its begin record up to the next begin record, and those after a begin that
    // repeats its id. Split builds every run of a journal, then checks each: records of another run and values no
    // run writes are left out and noted as damage, so a summary rebuilt from it states only what the run captured.
    private sealed class RunRecords
    {
        private readonly List<JournalRecord> _records = new();
        private readonly List<JournalStepStart> _steps = new();
        private readonly List<JournalStepEnd> _stepEnds = new();
        private readonly List<JournalBeat> _beats = new();
        private readonly bool _beginKnown;
        private readonly bool _journalPartlyUnread;

        // A stepEnd or beat record was read, so a step ran, whatever became of the records.
        private bool _stepActivitySeen;

        // Render reduced the run to its identity.
        private bool _reduced;

        private RunRecords(JournalBegin begin, bool beginKnown, string firstLine, string? readerDamage)
        {
            Begin = begin;
            _beginKnown = beginKnown;
            FirstLine = firstLine;
            _journalPartlyUnread = readerDamage is not null;
            if (readerDamage is not null)
            {
                NoteDamage(readerDamage);
            }
        }

        // The begin record once checked, holding the run's id; a run with an unknown begin holds an empty one.
        public JournalBegin Begin { get; private set; }

        // The begin record's id, or when it has none, the first id this run's stepEnd, result, released or end
        // records name; Guid.Empty when the run is unidentified.
        public Guid RunId { get; private set; }

        // The run's first line, which names a run whose id is unknown.
        public string FirstLine { get; }

        // The journal line at which the run's last segment starts: its begin record, or the begin that repeats its id
        // last, and 0 for the lines before the first begin. The run with the highest owns the end of the journal.
        public int LastSegment { get; private set; }

        public bool Ended { get; private set; }

        public JournalResult? Result { get; private set; }

        public string? FirstDamage { get; private set; }

        // The most recently started step.
        public JournalStepStart? LastStep => _steps.Count > 0 ? _steps[^1] : null;

        // The step the run stopped in, as far as its step records show: the last one started, unless a heartbeat names
        // a later step. The engine writes a step's record before the step reports progress, so such a heartbeat shows
        // that the later step's record is missing, and then where the run stopped is unknown.
        public JournalStepStart? StoppingStep => StepRecordMissing ? null : LastStep;

        public JournalBeat? LastBeat => _beats.Count > 0 ? _beats[^1] : null;

        public string? LastStepName
        {
            get
            {
                if (StoppingStep is not { } step)
                {
                    return null;
                }

                // The window shows this name, so it is held to the summaries' step-name rule, and a name that rule
                // leaves blank is taken from this journal's own definition of the step.
                return SummaryFormat.StepName(step.Name)
                    ?? SummaryFormat.StepName(
                        Begin.Steps.FirstOrDefault(definition => definition is not null && definition.Number == step.Index)?.Name);
            }
        }

        // Only a run whose begin was read, whose journal was read whole and which holds no record a step writes is
        // known never to have reached step 1. Without step data otherwise, the step at which it stopped is unknown.
        public bool InPreparation =>
            _beginKnown && !_journalPartlyUnread && !_reduced && !_stepActivitySeen && _steps.Count == 0;

        private JournalPrepared? Prepared { get; set; }

        private JournalReleased? Released { get; set; }

        private bool StepRecordMissing => LastStep is { } step && _beats.Exists(beat => beat.Step > step.Index);

        // The step that was running when an unfinished run stopped: the last one started, unless it finished.
        private JournalStepStart? UnfinishedStep =>
            LastStep is { } step && !_stepEnds.Any(end => end.Index == step.Index) ? step : null;

        // Splits the journal's lines into runs at each begin record. Lines before the first begin form a run with an
        // unknown begin unless they hold nothing a run writes: blank lines only, or one line that does not parse and
        // is the journal's last, with no terminator after it, which is a begin torn by a crash during its own
        // append, before any traffic. A complete line that does not parse was no torn append, so it is a begin that
        // could not be read; and when the journal holds no begin and part of it was left unread, that part may have
        // held one.
        public static IReadOnlyList<RunRecords> Split(
            IReadOnlyList<string> lines,
            string? readerDamage,
            bool lastLineUnterminated)
        {
            var entries = new List<JournalLine>(lines.Count);
            foreach (string line in lines)
            {
                entries.Add(JournalLine.Read(line));
            }

            int firstBegin = entries.FindIndex(entry => entry.Record is JournalBegin);
            int leadingCount = firstBegin < 0 ? entries.Count : firstBegin;
            List<JournalLine> leading = entries.GetRange(0, leadingCount);
            var runs = new List<RunRecords>();
            var ends = new List<(RunRecords Owner, JournalEnd End)>();
            RunRecords? current = null;

            int[] textLines = Enumerable.Range(0, leadingCount)
                .Where(index => !string.IsNullOrWhiteSpace(leading[index].Text))
                .ToArray();
            bool onlyTornBegin = textLines.Length == 1
                && textLines[0] == entries.Count - 1
                && lastLineUnterminated;
            bool leadingHoldsNothing = !leading.Any(entry => entry.Record is not null)
                && (textLines.Length == 0 || onlyTornBegin)
                && !(firstBegin < 0 && readerDamage is not null);
            if (!leadingHoldsNothing)
            {
                string firstLine = leading.Select(entry => entry.Text).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                    ?? string.Empty;
                current = new RunRecords(new JournalBegin(), beginKnown: false, firstLine, readerDamage);
                current.NoteDamage(MissingBeginDamage);
                runs.Add(current);
                foreach (JournalLine entry in leading)
                {
                    if (entry.Record is { } record)
                    {
                        current.Collect(record, ends);
                    }
                }
            }

            for (int index = leadingCount; index < entries.Count; index++)
            {
                JournalLine entry = entries[index];
                if (entry.Record is JournalBegin begin)
                {
                    RunRecords? repeated = begin.RunId == Guid.Empty
                        ? null
                        : runs.Find(run => run._beginKnown && run.Begin.RunId == begin.RunId);
                    if (repeated is not null)
                    {
                        repeated.NoteDamage(RepeatedBeginDamage);
                        repeated.LastSegment = index;
                        current = repeated;
                        continue;
                    }

                    current = new RunRecords(begin, beginKnown: true, entry.Text, readerDamage) { LastSegment = index };
                    if (entry.BeginDamage is { } beginDamage)
                    {
                        current.NoteDamage(beginDamage);
                    }

                    runs.Add(current);
                }
                else if (entry.Record is { } record)
                {
                    current?.Collect(record, ends);
                }
            }

            foreach (RunRecords run in runs)
            {
                run.ResolveRunId();
            }

            foreach ((RunRecords owner, JournalEnd end) in ends)
            {
                ResolveEnd(runs, owner, end);
            }

            foreach (RunRecords run in runs)
            {
                run.Attach();
            }

            return runs;
        }

        // Renders the run's summary. A run whose values no summary can be rendered from keeps only its identity, and
        // is reported as unfinished at an unknown step.
        public RunDocuments Render()
        {
            if (TryRender(ToSource()) is { } documents)
            {
                return documents;
            }

            NoteDamage(UnrenderableDamage);
            _reduced = true;
            Prepared = null;
            Result = null;
            Released = null;
            _steps.Clear();
            _stepEnds.Clear();
            _beats.Clear();

            // The base name names the files and is never rendered, so it stays.
            Begin = new JournalBegin { RunId = RunId, LogBaseName = Begin.LogBaseName, TestType = Begin.TestType };
            if (TryRender(ToSource()) is { } minimal)
            {
                return minimal;
            }

            Begin = Begin with { TestType = string.Empty };
            RunSummary summary = RunSummary.Build(ToSource());
            return new RunDocuments(summary, RunSummaryJson.Serialize(summary), TextSummaryFormatter.Format(summary));
        }

        // Every value comes from this run's records. Build keeps the last stepEnd per step number, folds the
        // last heartbeat into the temperatures, and reports a run without a result as unfinished.
        public RunSummarySource ToSource() => new()
        {
            Begin = Begin,
            Prepared = Prepared,
            FinishedSteps = _stepEnds,
            UnfinishedStep = Result is null ? UnfinishedStep : null,
            Result = Result,
            Released = Released,
            LastBeat = LastBeat,
            StoredErrors = Array.Empty<ErrorRecord>(),
            ErrorDetailAvailable = false,
            Temperatures = _beats.Aggregate(
                SummaryTemperatures.Initial,
                static (peaks, beat) => peaks.Merge(beat.CpuC, beat.CpuStatus, beat.RamC, beat.RamStatus)),
            CauseHints = SummaryCauseHints.Empty,
            Recovered = true,
            Partial = Result is null,
            JournalDamage = FirstDamage,

            // As recovery reports it: an unfinished run no step record places, and that may have left preparation.
            StepPositionUnknown = Result is null && StoppingStep is null && !InPreparation,
        };

        // A rendered summary that recovery would not keep, were a retry to find it, is not rendered: the retry would
        // otherwise publish it again under the recovered name.
        private static RunDocuments? TryRender(RunSummarySource source)
        {
            try
            {
                RunSummary summary = RunSummary.Build(source);
                string json = RunSummaryJson.Serialize(summary);
                return SummaryBounds.Admits(summary, json)
                    ? new RunDocuments(summary, json, TextSummaryFormatter.Format(summary))
                    : null;
            }
            catch (Exception exception) when (IsRenderFailure(exception))
            {
                return null;
            }
        }

        // An end record ends every run with the id it names. One without an id is the one recovery appends, after
        // every run, for a run whose id is unknown, so it ends the nearest such run not yet ended at or before the
        // run it follows. An end that ends no run is another run's, which no run of this journal can be.
        private static void ResolveEnd(List<RunRecords> runs, RunRecords owner, JournalEnd end)
        {
            if (end.RunId != Guid.Empty)
            {
                bool named = false;
                foreach (RunRecords run in runs.Where(run => run.RunId == end.RunId))
                {
                    run.Ended = true;
                    named = true;
                }

                if (!named)
                {
                    owner.NoteDamage(ForeignRecord("end"));
                }

                return;
            }

            RunRecords? unidentified = runs
                .Take(runs.IndexOf(owner) + 1)
                .LastOrDefault(run => run.RunId == Guid.Empty && !run.Ended);
            if (unidentified is null)
            {
                owner.NoteDamage(ForeignRecord("end"));
            }
            else
            {
                unidentified.Ended = true;
            }
        }

        private static Guid RunIdOf(JournalRecord record) => record switch
        {
            JournalStepEnd stepEnd => stepEnd.RunId,
            JournalResult result => result.RunId,
            JournalReleased released => released.RunId,
            JournalEnd end => end.RunId,
            _ => Guid.Empty,
        };

        // Budgets and holds are written from non-negative plan durations, so each must lie within 0 to MaxQuantity,
        // and the time and coverage budgets' totals, the plan's targets, within MaxQuantity; no plan holds more than
        // MaxPlanSteps steps. A null step entry, which a damaged line can hold, is skipped, as Build skips it.
        private static string? PlanDamage(JournalBegin begin)
        {
            if (begin.Steps.Count > JournalReadLimits.MaxPlanSteps)
            {
                return OutOfRange("the step count", BeginRecord);
            }

            long totalBudgetMs = 0;
            long totalCoveragePercent = 0;
            for (int position = 0; position < begin.Steps.Count; position++)
            {
                if (begin.Steps[position] is not { } step)
                {
                    continue;
                }

                // Named by position, as JournalBegin.Create numbers the steps, so a damaged number cannot
                // misname the step.
                string? field = step.TimeBudgetMs is < 0 or > MaxQuantity ? "timeBudgetMs"
                    : step.HoldMs is < 0 or > MaxQuantity ? "holdMs"
                    : step.CoverageBudgetPercent < 0 ? "coverageBudgetPercent"
                    : null;
                if (field is not null)
                {
                    return OutOfRange(StepField(position + 1, field), BeginRecord);
                }

                // Each total and each term are at most MaxQuantity, so neither sum can overflow before it is compared.
                totalBudgetMs += step.TimeBudgetMs;
                totalCoveragePercent += step.CoverageBudgetPercent;
                string? total = totalBudgetMs > MaxQuantity ? "timeBudgetMs"
                    : totalCoveragePercent > MaxQuantity ? "coverageBudgetPercent"
                    : null;
                if (total is not null)
                {
                    return OutOfRange("the steps' total " + total, BeginRecord);
                }
            }

            return null;
        }

        // A plan's step names are at most MaxStepNameUtf8Bytes, so a longer one is damage, named by position as
        // PlanDamage names a field.
        private static string? LongStepNameDamage(JournalBegin begin)
        {
            for (int position = 0; position < begin.Steps.Count; position++)
            {
                if (begin.Steps[position] is { } step && IsStepNameTooLong(step.Name))
                {
                    return OutOfRange(StepField(position + 1, "name"), BeginRecord);
                }
            }

            return null;
        }

        // A measured duration may be negative after a clock step and Build clamps it, so only its upper bound is
        // checked.
        private static string? StepEndDamage(JournalStepEnd end)
        {
            string? field = end.ActualMs > MaxQuantity ? "actualMs"
                : end.OverrunMs > MaxQuantity ? "overrunMs"
                : end.VerifiedBytes > MaxQuantity ? "verifiedBytes"
                : end.Errors > MaxQuantity ? "errors"
                : null;
            return field is null ? null : OutOfRange(StepField(end.Index, field), "a stepEnd record");
        }

        private static string? BeatDamage(JournalBeat beat)
        {
            string? field = beat.ElapsedMs > MaxQuantity ? "elapsedMs"
                : beat.Errors > MaxQuantity ? "errors"
                : null;
            return field is null ? null : OutOfRange(field, "a beat record");
        }

        private static string? ResultDamage(JournalResult result)
        {
            string? field = result.TotalErrors > MaxQuantity ? "totalErrors"
                : result.VerifiedBytes > MaxQuantity ? "verifiedBytes"
                : result.ElapsedMs > MaxQuantity ? "elapsedMs"
                : result.FirstErrorMs > MaxQuantity ? "firstErrorMs"
                : null;
            return field is null ? null : OutOfRange(field, "the result record");
        }

        private static string? ReleasedDamage(JournalReleased released)
        {
            string? field = released.ReleaseMs > MaxQuantity ? "releaseMs"
                : released.ReleasedBytes > MaxQuantity ? "releasedBytes"
                : null;
            return field is null ? null : OutOfRange(field, "the released record");
        }

        private static string StepField(int number, string field) =>
            string.Create(CultureInfo.InvariantCulture, $"step {number}'s {field}");

        private static string OutOfRange(string field, string record) => $"{field} in {record} is out of range";

        // The record types are "end", "stepEnd", "result" and "released"; only "end" takes "an".
        private static string ForeignRecord(string recordType) =>
            $"{("aeiou".Contains(recordType[0], StringComparison.Ordinal) ? "an" : "a")} {recordType} record of another run was ignored";

        private void NoteDamage(string damage) => FirstDamage ??= damage;

        private void Collect(JournalRecord record, List<(RunRecords Owner, JournalEnd End)> ends)
        {
            _records.Add(record);
            if (record is JournalEnd end)
            {
                ends.Add((this, end));
            }
        }

        private void ResolveRunId() =>
            RunId = Begin.RunId != Guid.Empty
                ? Begin.RunId
                : _records.Select(RunIdOf).FirstOrDefault(id => id != Guid.Empty);

        // Takes this run's records: the outcome records only when they name this run, since one naming another is a
        // tampered or foreign record that must never become this run's verdict. Then leaves out every value no run
        // writes.
        private void Attach()
        {
            foreach (JournalRecord record in _records)
            {
                switch (record)
                {
                    case JournalPrepared prepared:
                        Prepared ??= prepared;
                        break;
                    case JournalStepStart step when IsStepNameTooLong(step.Name):
                        // The step's position stands; its name comes from the plan instead.
                        NoteDamage(OutOfRange(StepField(step.Index, "name"), "a step record"));
                        _steps.Add(step with { Name = string.Empty });
                        break;
                    case JournalStepStart step:
                        _steps.Add(step);
                        break;
                    case JournalBeat beat:
                        _stepActivitySeen = true;
                        _beats.Add(beat);
                        break;
                    case JournalStepEnd stepEnd when stepEnd.RunId == RunId:
                        _stepActivitySeen = true;
                        _stepEnds.Add(stepEnd);
                        break;
                    case JournalStepEnd:
                        _stepActivitySeen = true;
                        NoteDamage(ForeignRecord("stepEnd"));
                        break;
                    case JournalResult result when result.RunId == RunId:
                        Result = result;
                        break;
                    case JournalResult:
                        NoteDamage(ForeignRecord("result"));
                        break;
                    case JournalReleased released when released.RunId == RunId:
                        Released = released;
                        break;
                    case JournalReleased:
                        NoteDamage(ForeignRecord("released"));
                        break;
                }
            }

            DropDamagedValues();
            if ((_steps.Count == 0 && _stepActivitySeen) || StepRecordMissing)
            {
                NoteDamage(MissingStepsDamage);
            }
        }

        // Leaves out each value no run writes, as the remarks of CrashDetector list them, noting the first. A
        // damaged result leaves the run unfinished, so the steps' error total is checked only after it.
        private void DropDamagedValues()
        {
            JournalBegin begin = Begin;
            if (PlanDamage(begin) is { } planDamage)
            {
                NoteDamage(planDamage);
                begin = begin with { Steps = Array.Empty<JournalStep>() };
            }
            else if (LongStepNameDamage(begin) is { } nameDamage)
            {
                // The plan stands: every name is shown and published through SummaryFormat.StepName, which cuts it
                // to the plan's limit, so only the damage is noted.
                NoteDamage(nameDamage);
            }

            if (begin.AvailableBytes > MaxQuantity)
            {
                NoteDamage(OutOfRange("availableBytes", BeginRecord));
                begin = begin with { AvailableBytes = null };
            }

            if (begin.PlannedBytes > MaxQuantity)
            {
                NoteDamage(OutOfRange("plannedBytes", BeginRecord));
                begin = begin with { PlannedBytes = null };
            }

            if (begin.Memory.InstalledBytes > MaxQuantity)
            {
                NoteDamage(OutOfRange("memory.installedBytes", BeginRecord));
                begin = begin with { Memory = begin.Memory with { InstalledBytes = null } };
            }

            begin = WithoutLongText(begin);

            // The summary names the run by the id its records were attributed to.
            Begin = begin with { RunId = RunId };

            if (Prepared is { TestedBytes: > MaxQuantity })
            {
                NoteDamage(OutOfRange("testedBytes", "the prepared record"));
                Prepared = null;
            }

            KeepUndamaged(_stepEnds, StepEndDamage);
            KeepUndamaged(_beats, BeatDamage);

            if (Result is { } result && ResultDamage(result) is { } resultDamage)
            {
                NoteDamage(resultDamage);
                Result = null;
            }

            // Text too long for a summary is left out of a result that is kept: the verdict then comes from the
            // terminal state, and the end detail is empty.
            if (Result is { } kept && IsTextTooLong(kept.Verdict))
            {
                NoteDamage(OutOfRange("verdict", "the result record"));
                Result = kept with { Verdict = string.Empty };
            }

            if (Result is { } detailed && IsTextTooLong(detailed.EndDetail))
            {
                NoteDamage(OutOfRange("endDetail", "the result record"));
                Result = detailed with { EndDetail = string.Empty };
            }

            if (Released is { } released && ReleasedDamage(released) is { } releasedDamage)
            {
                NoteDamage(releasedDamage);
                Released = null;
            }

            // A run with a result reports the result's total, so only a run without one publishes this sum.
            if (Result is null && StepErrorsTotalExceedsBound())
            {
                NoteDamage(OutOfRange("the steps' total errors", "the stepEnd records"));
                _stepEnds.Clear();
            }
        }

        // Leaves out each free-text field of the begin record too long for a summary, which then reports it as not
        // captured.
        private JournalBegin WithoutLongText(JournalBegin begin)
        {
            if (IsTextTooLong(begin.TestType))
            {
                NoteDamage(OutOfRange("testType", BeginRecord));
                begin = begin with { TestType = string.Empty };
            }

            if (IsTextTooLong(begin.AppVersion))
            {
                NoteDamage(OutOfRange("appVersion", BeginRecord));
                begin = begin with { AppVersion = string.Empty };
            }

            if (IsTextTooLong(begin.System.Os))
            {
                NoteDamage(OutOfRange("system.os", BeginRecord));
                begin = begin with { System = begin.System with { Os = string.Empty } };
            }

            if (IsTextTooLong(begin.System.CpuName))
            {
                NoteDamage(OutOfRange("system.cpuName", BeginRecord));
                begin = begin with { System = begin.System with { CpuName = string.Empty } };
            }

            return begin;
        }

        private void KeepUndamaged<T>(List<T> records, Func<T, string?> damageOf)
        {
            var kept = new List<T>(records.Count);
            foreach (T record in records)
            {
                if (damageOf(record) is { } damage)
                {
                    NoteDamage(damage);
                }
                else
                {
                    kept.Add(record);
                }
            }

            records.Clear();
            records.AddRange(kept);
        }

        // Whether the sum Build takes for the error total of a run without a result, over the last stepEnd per step
        // number, exceeds MaxQuantity. Every term is already within MaxQuantity, and the sum is compared after each
        // one, so it cannot overflow before it is found too large.
        private bool StepErrorsTotalExceedsBound()
        {
            var latestErrors = new Dictionary<int, ulong>();
            foreach (JournalStepEnd end in _stepEnds)
            {
                latestErrors[end.Index] = end.Errors;
            }

            ulong total = 0;
            foreach (ulong errors in latestErrors.Values)
            {
                total += errors;
                if (total > MaxQuantity)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
