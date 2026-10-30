using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// Recovers, at launch, the progress journal a previous run left behind
/// (<c>&lt;data&gt;\journal\active-run.jsonl</c>): it works out how that run ended, publishes whichever of its
/// summaries are missing, and then retires the journal.
/// </summary>
/// <remarks>
/// <para>
/// Classification: a journal without a <c>begin</c> record never carried test traffic, because <c>begin</c>
/// is acknowledged before <c>rq_session_start</c>, so it is deleted and nothing is reported. A run with an
/// <c>end</c> record has published its summaries, so its journal is deleted. A run with a <c>result</c> record
/// but no <c>end</c> is <see cref="JournalRecoveryKind.CompletedUnpublished"/>. A run without a <c>result</c>
/// is <see cref="JournalRecoveryKind.Unfinished"/>: its partial summary reports FAIL with end reason
/// <see cref="RunEndReason.PreviousRunUnfinished"/> and the step at which it stopped.
/// </para>
/// <para>
/// Summaries: a file under a summary's final name that parses with the run's id is kept. A missing summary is
/// rebuilt by <see cref="RunSummary.Build"/> from the journal records alone, never from this launch's machine
/// state, and published through <see cref="ResultsLog.PublishFile"/>, the JSON before the text. A final file
/// that does not parse (<see cref="RunSummaryJson.TryDeserialize"/> refuses it), cannot be read or names
/// another run is left untouched, and the rebuilt summary goes to the recovered name,
/// <c>&lt;base&gt;-recovered-summary.json</c> or <c>.txt</c>, instead. Rebuilt summaries carry no error
/// detail and no cause hints. The crashed run's <c>.log</c> is never modified; it keeps whatever detail it
/// had flushed.
/// </para>
/// <para>
/// Damaged data: a journal line can parse and still hold a value no run writes: a quantity above
/// 2^53 - 1, the bound the summary schema keeps every integer within, or a negative plan budget. A total the
/// summary derives from the records is held to the same bound: the plan's time and coverage targets, and the
/// error total of a run without a <c>result</c>, which sums the last <c>stepEnd</c> of each step. Before a
/// summary is rebuilt, the run's records are checked. A damaged run publishes nothing,
/// <see cref="JournalRecovery.PublicationError"/> names the first damaged field and where the kept journal
/// is, and the outcome keeps the run's kind, as for a failed publication. A run whose summaries are all
/// on disk needs no rebuild, so it is retired as usual, and the other runs of the journal are recovered.
/// </para>
/// <para>
/// Crash consistency: the journal is deleted only once every summary it describes is on disk and an <c>end</c>
/// record has been appended for each run recovered here. When a publication fails, no <c>end</c> is written,
/// the journal is kept so the next launch retries, and <see cref="JournalRecovery.PublicationError"/> carries
/// the reason. A launch that stops anywhere in this procedure leaves a journal the next launch recovers to the
/// same files, so the procedure is idempotent.
/// </para>
/// <para>
/// Already published: an unfinished run whose final JSON summary already exists with its id is reported as
/// <see cref="JournalRecoveryKind.None"/> with that summary, and only a missing text summary is rendered from
/// it. That happens when the journal failed during the run after the results were published. This case
/// extends the plan's recovery cases, so that a journal write failure never turns published results into a
/// false "did not finish" report.
/// </para>
/// <para>
/// Several runs: a journal kept by a failed recovery receives the next run's records after its own, so the
/// journal is split into runs at each <c>begin</c> record, and records before the first <c>begin</c> are
/// ignored. An <c>end</c> record belongs to the run whose id it names, or, without an id, to the run it follows.
/// Every run is recovered as above, the returned outcome describes the latest run, and
/// <see cref="JournalRecovery.PublishedFiles"/> lists the files written for all of them.
/// </para>
/// <para>
/// Threading: stateless and thread-safe. It performs synchronous file I/O, so <c>RunController</c> calls it on
/// the thread pool, never on the UI thread. It never throws for an I/O failure, a damaged journal or a damaged
/// summary file. An I/O failure and a journal value out of range are reported through
/// <see cref="JournalRecovery.PublicationError"/>; a malformed journal line is skipped, and a summary file
/// that does not parse is left untouched as another run's. Files are written only under
/// <c>&lt;data&gt;\logs\</c> and <c>&lt;data&gt;\journal\</c>.
/// </para>
/// </remarks>
public static class CrashDetector
{
    // ResultsLog refuses a base name that is not a plain file name. A begin record damaged that way still has
    // its results published, under this prefix followed by the run id, so its journal can be retired.
    private const string FallbackBaseNamePrefix = "RamQuick-recovered-";

    private const string JournalUnreadablePrefix = "The progress journal could not be read: ";

    private const string JournalDamagedPrefix = "The progress journal holds damaged data: ";

    // The summary schema keeps every integer quantity below 2^53, so that a JSON number reads back exactly. No run
    // writes a larger one, so a journal value above this bound is damage, and no summary is rebuilt from it.
    private const long MaxQuantity = (1L << 53) - 1;

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

        IReadOnlyList<JournalRecord> records;
        try
        {
            records = JournalLineSerializer.ParseAll(storage.ReadLines());
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            // Nothing is known about the run, so the journal stays for the next launch to recover.
            return new JournalRecovery { PublicationError = JournalUnreadablePrefix + exception.Message };
        }

        IReadOnlyList<RunRecords> runs = RunRecords.Split(records);
        if (runs.Count == 0)
        {
            return new JournalRecovery { JournalDeleted = TryDelete(storage) };
        }

        var publication = new Publication(storage.Location);
        JournalRecovery latest = JournalRecovery.None;
        foreach (RunRecords run in runs)
        {
            latest = RecoverRun(run, dataDirectory, publication);
        }

        bool deleted = false;
        if (publication.Error is null)
        {
            DateTimeOffset utc = timeProvider.GetUtcNow();
            foreach (RunRecords run in runs)
            {
                if (!run.Ended)
                {
                    TryAppendEnd(storage, run.Begin.RunId, utc);
                }
            }

            deleted = TryDelete(storage);
        }

        return latest with
        {
            PublishedFiles = publication.Files,
            PublicationError = publication.Error,
            JournalDeleted = deleted,
        };
    }

    private static JournalRecovery RecoverRun(RunRecords run, DataDirectory dataDirectory, Publication publication)
    {
        JournalBegin begin = run.Begin;
        string baseName = UsableBaseName(dataDirectory, begin);
        var identified = new JournalRecovery
        {
            RunId = begin.RunId,
            LogBaseName = baseName,
            TestType = begin.TestType.Length > 0 ? begin.TestType : null,
        };

        if (run.Ended)
        {
            return identified;
        }

        var files = new SummaryFiles(dataDirectory, baseName, begin.RunId);
        return run.Result is not null
            ? RecoverCompleted(run, files, publication, identified)
            : RecoverUnfinished(run, files, publication, identified);
    }

    private static JournalRecovery RecoverCompleted(
        RunRecords run,
        SummaryFiles files,
        Publication publication,
        JournalRecovery identified)
    {
        Placement<RunSummary> json = files.PlaceJson();
        Placement<string> text = files.PlaceText();
        JournalRecovery completed = identified with
        {
            Kind = JournalRecoveryKind.CompletedUnpublished,
            Summary = json.Kept,
        };
        if (publication.RejectsDamaged(run, json, text))
        {
            return completed;
        }

        // Each missing summary is rebuilt from the journal, even beside a kept JSON summary that holds the
        // run's error detail, so that a rebuilt file only ever states what the journal captured.
        RunSummary rebuilt = RunSummary.Build(run.ToSource());
        publication.Publish(json, () => RunSummaryJson.Serialize(rebuilt));
        publication.Publish(text, () => TextSummaryFormatter.Format(rebuilt));

        return completed with { Summary = json.Kept ?? rebuilt };
    }

    private static JournalRecovery RecoverUnfinished(
        RunRecords run,
        SummaryFiles files,
        Publication publication,
        JournalRecovery identified)
    {
        Placement<RunSummary> json = files.PlaceJson();
        if (json.IsKeptUnderFinalName && json.Kept is { } published)
        {
            // The results were published before the journal stopped recording, so they are authoritative and
            // the run is not reported as unfinished.
            publication.Publish(files.PlaceText(), () => TextSummaryFormatter.Format(published));
            return identified with { Summary = published };
        }

        Placement<string> text = files.PlaceText();
        JournalStepStart? lastStep = run.LastStep;
        JournalRecovery unfinished = identified with
        {
            Kind = JournalRecoveryKind.Unfinished,
            InPreparation = lastStep is null,
            LastStepNumber = lastStep?.Index,
            StepCount = lastStep is { Count: > 0 } ? lastStep.Count : run.Begin.Steps.Count,
            LastStepName = run.LastStepName,
            LastHeartbeatUtc = run.LastBeat?.Utc is { } utc && utc != default ? utc : null,
            Summary = json.Kept,
        };
        if (publication.RejectsDamaged(run, json, text))
        {
            return unfinished;
        }

        RunSummary partial = RunSummary.Build(run.ToSource());
        publication.Publish(json, () => RunSummaryJson.Serialize(partial));
        publication.Publish(text, () => TextSummaryFormatter.Format(partial));
        return unfinished with { Summary = json.Kept ?? partial };
    }

    // Probing through the path helper keeps one base-name rule: the one ResultsLog applies to every path.
    private static string UsableBaseName(DataDirectory dataDirectory, JournalBegin begin)
    {
        try
        {
            _ = ResultsLog.GetSummaryJsonPath(dataDirectory, begin.LogBaseName);
            return begin.LogBaseName;
        }
        catch (ArgumentException)
        {
            return FallbackBaseNamePrefix + SummaryFormat.RunId(begin.RunId);
        }
    }

    // Finds where one summary of a run stands: kept under the first name that already holds this run's file,
    // or to be written under the first free name. A name held by any other file is skipped and never touched;
    // a file that cannot be read cannot be confirmed as this run's, so it counts as another file.
    private static Placement<T> Place<T>(string finalPath, string recoveredPath, Func<string, T?> readOwn)
        where T : class
    {
        foreach (string path in new[] { finalPath, recoveredPath })
        {
            if (!File.Exists(path))
            {
                return new Placement<T>(finalPath, recoveredPath, KeptPath: null, Kept: null, TargetPath: path);
            }

            if (TryReadText(path) is { } content && readOwn(content) is { } own)
            {
                return new Placement<T>(finalPath, recoveredPath, KeptPath: path, Kept: own, TargetPath: null);
            }
        }

        return new Placement<T>(finalPath, recoveredPath, KeptPath: null, Kept: null, TargetPath: null);
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
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
    // run's: the JSON parses as a summary carrying the run id, the text carries it on its run-id line.
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

        private RunSummary? ReadOwnJson(string content) =>
            RunSummaryJson.TryDeserialize(content, out RunSummary? summary)
            && string.Equals(summary.Run.Id, _runIdText, StringComparison.Ordinal)
                ? summary
                : null;

        private string? ReadOwnText(string content) =>
            TextSummaryFormatter.TryParseRunId(content) == _runId ? content : null;
    }

    // The files one recovery wrote, in order, and the first reason a summary could not be written.
    private sealed class Publication
    {
        private readonly List<string> _files = new();
        private readonly string _journalLocation;

        public Publication(string journalLocation) => _journalLocation = journalLocation;

        public IReadOnlyList<string> Files => _files;

        public string? Error { get; private set; }

        // A summary is rebuilt from the journal only when one of the run's documents is due, so only then are its
        // records checked. A damaged run publishes nothing: a rebuilt summary would state values no run measured.
        // The error keeps the journal, and the message says where it is, so the user can find the kept file.
        public bool RejectsDamaged(RunRecords run, Placement<RunSummary> json, Placement<string> text)
        {
            if ((json.TargetPath is null && text.TargetPath is null) || run.FindDamage() is not { } damage)
            {
                return false;
            }

            Error ??= $"{JournalDamagedPrefix}{damage}. The journal is kept at '{_journalLocation}'.";
            return true;
        }

        // Rendering is deferred until a file is due, so a kept summary costs no serialization.
        public void Publish<T>(Placement<T> placement, Func<string> render)
            where T : class
        {
            if (placement.TargetPath is { } target)
            {
                try
                {
                    ResultsLog.PublishFile(target, render());
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

    // The records of one run: those from its begin record up to the next begin record.
    private sealed class RunRecords
    {
        private readonly List<JournalStepStart> _steps = new();
        private readonly List<JournalStepEnd> _stepEnds = new();
        private readonly List<JournalBeat> _beats = new();

        private RunRecords(JournalBegin begin) => Begin = begin;

        public JournalBegin Begin { get; }

        public bool Ended { get; private set; }

        public JournalResult? Result { get; private set; }

        // The most recently started step.
        public JournalStepStart? LastStep => _steps.Count > 0 ? _steps[^1] : null;

        public JournalBeat? LastBeat => _beats.Count > 0 ? _beats[^1] : null;

        public string? LastStepName
        {
            get
            {
                if (LastStep is not { } step)
                {
                    return null;
                }

                string? name = step.Name.Length > 0
                    ? step.Name
                    : Begin.Steps.FirstOrDefault(definition => definition is not null && definition.Number == step.Index)?.Name;
                return string.IsNullOrEmpty(name) ? null : name;
            }
        }

        private JournalPrepared? Prepared { get; set; }

        private JournalReleased? Released { get; set; }

        // The step that was running when an unfinished run stopped: the last one started, unless it finished.
        private JournalStepStart? UnfinishedStep =>
            LastStep is { } step && !_stepEnds.Any(end => end.Index == step.Index) ? step : null;

        public static IReadOnlyList<RunRecords> Split(IReadOnlyList<JournalRecord> records)
        {
            var runs = new List<RunRecords>();
            var endedRunIds = new HashSet<Guid>();
            RunRecords? current = null;
            foreach (JournalRecord record in records)
            {
                switch (record)
                {
                    case JournalBegin begin:
                        current = new RunRecords(begin);
                        runs.Add(current);
                        break;
                    case JournalEnd end when end.RunId != Guid.Empty:
                        endedRunIds.Add(end.RunId);
                        break;
                    case JournalEnd:
                        if (current is not null)
                        {
                            current.Ended = true;
                        }

                        break;
                    default:
                        current?.Add(record);
                        break;
                }
            }

            foreach (RunRecords run in runs)
            {
                if (endedRunIds.Contains(run.Begin.RunId))
                {
                    run.Ended = true;
                }
            }

            return runs;
        }

        // The first value in this run's records that no run writes, as a short reason naming the field and its
        // record, or null when every value is in range. Every quantity must stay within MaxQuantity, and so must
        // every total Build derives from them: the plan's budgets, and the error total of a run without a result,
        // which sums the last stepEnd per step number. A total is checked after the fields it sums, so a single
        // bad value is named as itself. A measured duration may be negative after a clock step and Build clamps
        // it, so only its upper bound is checked.
        public string? FindDamage()
        {
            if (BeginDamage(Begin) is { } beginDamage)
            {
                return beginDamage;
            }

            if (Prepared is { TestedBytes: > MaxQuantity })
            {
                return OutOfRange("testedBytes", "the prepared record");
            }

            foreach (JournalStepEnd end in _stepEnds)
            {
                string? field = end.ActualMs > MaxQuantity ? "actualMs"
                    : end.OverrunMs > MaxQuantity ? "overrunMs"
                    : end.VerifiedBytes > MaxQuantity ? "verifiedBytes"
                    : end.Errors > MaxQuantity ? "errors"
                    : null;
                if (field is not null)
                {
                    return OutOfRange(StepField(end.Index, field), "a stepEnd record");
                }
            }

            foreach (JournalBeat beat in _beats)
            {
                string? field = beat.ElapsedMs > MaxQuantity ? "elapsedMs"
                    : beat.Errors > MaxQuantity ? "errors"
                    : null;
                if (field is not null)
                {
                    return OutOfRange(field, "a beat record");
                }
            }

            if (Result is { } result)
            {
                string? field = result.TotalErrors > MaxQuantity ? "totalErrors"
                    : result.VerifiedBytes > MaxQuantity ? "verifiedBytes"
                    : result.ElapsedMs > MaxQuantity ? "elapsedMs"
                    : result.FirstErrorMs > MaxQuantity ? "firstErrorMs"
                    : null;
                if (field is not null)
                {
                    return OutOfRange(field, "the result record");
                }
            }

            if (Released is { } released)
            {
                string? field = released.ReleaseMs > MaxQuantity ? "releaseMs"
                    : released.ReleasedBytes > MaxQuantity ? "releasedBytes"
                    : null;
                if (field is not null)
                {
                    return OutOfRange(field, "the released record");
                }
            }

            // A run with a result reports the result's total, so only a run without one publishes this sum.
            return Result is null && StepErrorsTotalExceedsBound()
                ? OutOfRange("the steps' total errors", "the stepEnd records")
                : null;
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
        };

        private void Add(JournalRecord record)
        {
            switch (record)
            {
                case JournalPrepared prepared:
                    Prepared ??= prepared;
                    break;
                case JournalStepStart step:
                    _steps.Add(step);
                    break;
                case JournalStepEnd stepEnd:
                    _stepEnds.Add(stepEnd);
                    break;
                case JournalBeat beat:
                    _beats.Add(beat);
                    break;
                case JournalResult result:
                    Result = result;
                    break;
                case JournalReleased released:
                    Released = released;
                    break;
            }
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

        // Budgets and holds are written from non-negative plan durations, so each must lie within 0 to MaxQuantity,
        // and the time and coverage budgets' totals, the plan's targets, within MaxQuantity. A null step entry,
        // which a damaged line can hold, is skipped, as Build skips it.
        private static string? BeginDamage(JournalBegin begin)
        {
            const string BeginRecord = "the begin record";
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

            string? quantity = begin.AvailableBytes > MaxQuantity ? "availableBytes"
                : begin.PlannedBytes > MaxQuantity ? "plannedBytes"
                : begin.Memory.InstalledBytes > MaxQuantity ? "memory.installedBytes"
                : null;
            return quantity is null ? null : OutOfRange(quantity, BeginRecord);
        }

        private static string StepField(int number, string field) =>
            string.Create(CultureInfo.InvariantCulture, $"step {number}'s {field}");

        private static string OutOfRange(string field, string record) => $"{field} in {record} is out of range";
    }
}
