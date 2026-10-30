using System.Globalization;
using System.Security;
using System.Text;
using System.Threading.Channels;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// The outcome of <see cref="ResultsLog.PublishSummaryAsync(RunSummary)"/>.
/// </summary>
/// <remarks>
/// A failure is an expected condition, shown as "Results could not be saved: &lt;error&gt;". The caller then
/// keeps the progress journal, so the next launch's recovery publishes whichever summary is still missing.
/// </remarks>
/// <param name="Succeeded">Whether both the JSON and the text summary were published.</param>
/// <param name="JsonPath">
/// The final path of the published JSON summary, or <see langword="null"/> when it was not published.
/// </param>
/// <param name="TextPath">
/// The final path of the published text summary, or <see langword="null"/> when it was not published.
/// </param>
/// <param name="Error">
/// The message of the first publication failure, or <see langword="null"/> when both summaries were published.
/// </param>
public sealed record SummaryPublishResult(bool Succeeded, string? JsonPath, string? TextPath, string? Error);

/// <summary>
/// The results log of one run: its timestamped event log, <c>&lt;data&gt;\logs\&lt;base&gt;.log</c>, and the
/// crash-consistent publication of its <c>&lt;base&gt;-summary.json</c> and <c>&lt;base&gt;-summary.txt</c>;
/// also the writer of <c>&lt;data&gt;\logs\app-errors.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Name reservation.</b> <see cref="OpenRun"/> reserves the base name
/// <c>RamQuick-&lt;yyyyMMdd-HHmmss&gt;-&lt;TestType&gt;-&lt;runId&gt;</c>, the run's local start time and its
/// run id in 32-hex-digit <c>N</c> form, by creating the <c>.log</c> with <see cref="FileMode.CreateNew"/>.
/// The three files of a run share that base name, which the journal's <c>begin</c> record stores.
/// </para>
/// <para>
/// <b>Line format</b>, parsed by <c>scripts/Summarize-ValidationRuns.ps1</c>: every line is
/// <c>&lt;yyyy-MM-ddTHH:mm:ss.fffZ&gt; &lt;EVENT&gt; key=value key=value …</c>, UTF-8 without a byte-order mark,
/// ending in <c>\r\n</c>, in the invariant culture. Free-text values (<c>name</c>, <c>detail</c>,
/// <c>message</c>, <c>outcome</c>, <c>os</c>, <c>cpu</c>, <c>path</c>, <c>error</c>, <c>source</c>) are always
/// double-quoted, with the locations that name the Windows account written as placeholders: the run's data folder
/// <c>&lt;data&gt;</c>, the user's folders <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c> and <c>%USERPROFILE%</c>, and the
/// user name, where it is a path segment, <c>%USERNAME%</c>. Results files are named relative to the data folder,
/// as <c>logs\&lt;file&gt;</c>. Any other value is quoted only when it is empty or holds white space, a double
/// quote or a control character. Inside quotes <c>\"</c> stands for <c>"</c> and <c>\\</c> for <c>\</c>, and
/// control characters are written as spaces, so an event never spans more lines than it writes. Numbers are plain;
/// percentages have one decimal, other doubles use the round-trip format. Hex words are <c>0x</c> plus 16 uppercase
/// digits. A value that was not captured is written <c>unknown</c>. Step and pass numbers are 1-based, as in
/// <c>step=3/6</c>, the folder-wide rule for persisted indices.
/// </para>
/// <para>
/// <b>Events</b>, in run order: <c>START_REQUESTED</c>, <c>SYSTEM</c>, <c>MEMORY</c>, <c>SIZING</c>, one
/// <c>PLAN</c> per step and <c>PLAN_TARGET</c>; <c>PREPARED</c>; per step <c>STEP_START</c>, <c>STEP_END</c>
/// and, after an overrun, <c>STEP_OVERRUN</c>; a <c>PROGRESS</c> line whenever the elapsed time crosses the
/// next multiple of 10 s; <c>ERROR</c> lines for the first <see cref="ErrorGrouping.StoredDetailCap"/>
/// errors and one <c>ERROR_CAP_REACHED</c>; <c>SENSOR</c>, <c>THERMAL_STOP</c>, <c>JOURNAL_FAILURE</c>,
/// <c>CALLBACK_FAULT</c> (an exception in an engine callback or event handler, the cause of a host-fault stop) and
/// <c>NOTICE</c> as they occur; <c>STOP_REQUESTED</c>, <c>COMPLETED</c>, <c>VERDICT</c>, <c>RELEASED</c> or
/// <c>RELEASE_NOT_CONFIRMED</c>; and <c>SUMMARY_PUBLISHED</c> or <c>SUMMARY_FAILED</c>. A run has at most two
/// <c>STOP_REQUESTED</c> lines. One records the controller's first stop request, user, thermal or host-fault,
/// stamped when it is made; later requests, which the first-writer-wins engine ignores, add none. The other records
/// the engine's own first-error stop with Stop on error on, adding <c>time_into_test_ms</c>, the decision's test
/// time on the engine clock that <c>COMPLETED elapsed_ms</c> uses. It is logged when the controller learns of that
/// stop, from the error report or at Completed, and only when that stop ended the run; reported before any request,
/// it is the run's only stop line. <c>scripts/Summarize-ValidationRuns.ps1</c> reads the first
/// <c>STOP_REQUESTED</c>: the stop-to-Completed latency is the difference of its and <c>COMPLETED</c>'s timestamps.
/// </para>
/// <para>
/// <b>app-errors.log</b>: per exception, an <c>APP_ERROR source="…"</c> header, with <c>run_id=</c> while a run is
/// in progress, then the exception's full text, every line indented by two spaces and its locations written as in
/// the <c>.log</c>, then a blank line.
/// </para>
/// <para>
/// <b>Threading.</b> Every <c>Log*</c> method may be called from any thread and never performs I/O: it formats
/// its lines and enqueues them, except <see cref="LogErrors"/>, which enqueues its capped batch of records for
/// the drain to render, so a large batch costs the caller only a reference copy. One drain loop on the thread
/// pool appends the events in order and flushes them to the device after every drained batch, so a crashed
/// run's <c>.log</c> keeps every event that reached the drain. A write failure is recorded once in
/// <see cref="WriteFailure"/> and otherwise swallowed: the run continues without its event log. Events logged
/// after <see cref="Dispose"/> are discarded.
/// </para>
/// <para>
/// The run state is never cached here: <see cref="LogVerdict"/> receives the terminal state as a parameter.
/// The summaries are the files meant for sharing: they hold memory-test data and hardware descriptors. The
/// <c>.log</c> and <c>app-errors.log</c> are local diagnostic logs: they can also hold exception text and stack
/// traces, with the account's locations written as placeholders, so they are inspected before they are shared.
/// No user or machine name is added to any of them, and nothing leaves the PC.
/// </para>
/// </remarks>
public sealed class ResultsLog : IDisposable
{
    internal const string StartRequestedEvent = "START_REQUESTED";
    internal const string SystemEvent = "SYSTEM";
    internal const string MemoryEvent = "MEMORY";
    internal const string SizingEvent = "SIZING";
    internal const string PlanEvent = "PLAN";
    internal const string PlanTargetEvent = "PLAN_TARGET";
    internal const string PreparedEvent = "PREPARED";
    internal const string StepStartEvent = "STEP_START";
    internal const string StepEndEvent = "STEP_END";
    internal const string StepOverrunEvent = "STEP_OVERRUN";
    internal const string ProgressEvent = "PROGRESS";
    internal const string ErrorEvent = "ERROR";
    internal const string ErrorCapReachedEvent = "ERROR_CAP_REACHED";
    internal const string SensorEvent = "SENSOR";
    internal const string ThermalStopEvent = "THERMAL_STOP";
    internal const string JournalFailureEvent = "JOURNAL_FAILURE";
    internal const string StopRequestedEvent = "STOP_REQUESTED";
    internal const string CompletedEvent = "COMPLETED";
    internal const string VerdictEvent = "VERDICT";
    internal const string ReleasedEvent = "RELEASED";
    internal const string ReleaseNotConfirmedEvent = "RELEASE_NOT_CONFIRMED";
    internal const string NoticeEvent = "NOTICE";
    internal const string CallbackFaultEvent = "CALLBACK_FAULT";
    internal const string SummaryPublishedEvent = "SUMMARY_PUBLISHED";
    internal const string SummaryFailedEvent = "SUMMARY_FAILED";
    internal const string AppErrorEvent = "APP_ERROR";

    /// <summary>
    /// The message of <c>RELEASE_NOT_CONFIRMED</c>, which the summary's <c>memory.releaseSeconds: null</c>
    /// accompanies.
    /// </summary>
    internal const string ReleaseNotConfirmedMessage = "memory release not confirmed";

    private const string BaseNamePrefix = "RamQuick-";
    private const string LocalStartFormat = "yyyyMMdd-HHmmss";
    private const string LogExtension = ".log";
    private const string SummaryJsonSuffix = "-summary.json";
    private const string SummaryTextSuffix = "-summary.txt";
    private const string RecoveredSummaryJsonSuffix = "-recovered-summary.json";
    private const string RecoveredSummaryTextSuffix = "-recovered-summary.txt";
    private const string TempSuffix = ".tmp";

    // Precedes every line of an app-errors.log entry's exception text; the entry's header alone starts at the margin.
    private const string AppErrorTextIndent = "  ";

    // Three retries after the first attempt: four run ids in total before Start is refused.
    private const int MaxReservationRetries = 3;

    // Bounds one drained batch, so a steady stream of events cannot postpone the flush that ends it.
    private const int MaxBatchEntries = 256;

    // Bounds the transient text of one write: a batch of 10,000 records is rendered and written this many
    // ERROR lines at a time, never as one multi-megabyte string.
    private const int MaxErrorLinesPerWrite = 256;

    // No byte-order mark, and invalid UTF-16 never throws: an unpaired surrogate is written as U+FFFD.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly TimeSpan _progressLineInterval = TimeSpan.FromSeconds(10);

    // Closing the window waits at most this long for the drain; a drain still running closes the file itself.
    private static readonly TimeSpan _disposeDrainTimeout = TimeSpan.FromSeconds(2);

    // Serializes appends to app-errors.log within the process: several threads can fault at once.
    private static readonly object _appErrorsGate = new();

    private readonly FileStream _stream;
    private readonly TimeProvider _timeProvider;
    private readonly PersonalDataRedactor _redactor;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _drainTask;

    // Guards the formatting state below; held only while events are formatted or copied and enqueued, never
    // during I/O.
    private readonly object _gate = new();

    private Exception? _writeFailure;
    private int _disposed;

    private int _stepCount;
    private ulong _testedBytes;
    private TimeSpan _nextProgressLineAt = _progressLineInterval;
    private int _loggedErrorRecords;
    private bool _capReported;

    private ResultsLog(DataDirectory dataDirectory, Guid runId, string baseName, FileStream stream, TimeProvider timeProvider)
    {
        DataDirectory = dataDirectory;
        RunId = runId;
        BaseName = baseName;
        LogPath = GetLogPath(dataDirectory, baseName);
        SummaryJsonPath = GetSummaryJsonPath(dataDirectory, baseName);
        SummaryTextPath = GetSummaryTextPath(dataDirectory, baseName);
        _stream = stream;
        _timeProvider = timeProvider;
        _redactor = new PersonalDataRedactor(dataDirectory.RootPath);
        _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _drainTask = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Gets the data folder the run's files live in.
    /// </summary>
    public DataDirectory DataDirectory { get; }

    /// <summary>
    /// Gets the run id, whose 32-hex-digit <c>N</c> form ends <see cref="BaseName"/> and is the summary's
    /// <c>run.id</c>.
    /// </summary>
    public Guid RunId { get; }

    /// <summary>
    /// Gets the reserved base name, <c>RamQuick-&lt;yyyyMMdd-HHmmss&gt;-&lt;TestType&gt;-&lt;runId&gt;</c>, shared
    /// by the <c>.log</c> and both summaries and stored in the journal's <c>begin</c> record.
    /// </summary>
    public string BaseName { get; }

    /// <summary>
    /// Gets the path of the run's event log, <c>&lt;data&gt;\logs\&lt;base&gt;.log</c>.
    /// </summary>
    public string LogPath { get; }

    /// <summary>
    /// Gets the final path of the JSON summary, <c>&lt;data&gt;\logs\&lt;base&gt;-summary.json</c>.
    /// </summary>
    public string SummaryJsonPath { get; }

    /// <summary>
    /// Gets the final path of the text summary, <c>&lt;data&gt;\logs\&lt;base&gt;-summary.txt</c>.
    /// </summary>
    public string SummaryTextPath { get; }

    /// <summary>
    /// Gets the first failure to write or flush the <c>.log</c>, or <see langword="null"/> while every write
    /// has succeeded. Set once by the drain thread; the run continues after it.
    /// </summary>
    public Exception? WriteFailure => Volatile.Read(ref _writeFailure);

    /// <summary>
    /// Opens the results log of a run that is about to start: reserves its base name by creating its
    /// <c>.log</c>, and keeps that file open for the run's events.
    /// </summary>
    /// <param name="dataDirectory">The data folder; the <c>.log</c> is created in its <c>logs</c> folder.</param>
    /// <param name="testType">The test type name, ASCII letters and digits only, for example <c>"Quick"</c>.</param>
    /// <param name="startRequestedUtc">The time Start was accepted; its local time names the files.</param>
    /// <param name="timeProvider">
    /// The clock: its <see cref="TimeProvider.LocalTimeZone"/> converts <paramref name="startRequestedUtc"/>,
    /// and it stamps every event logged without an explicit time.
    /// </param>
    /// <param name="createRunId">The run-id source, <see cref="Guid.NewGuid"/> when <see langword="null"/>.</param>
    /// <returns>
    /// The open results log, or <see langword="null"/> when no base name could be reserved: the <c>.log</c>
    /// name was taken (or could not be created) for four run ids in a row, or the logs folder is not
    /// writable. The caller then refuses Start as an unusable data folder.
    /// </returns>
    /// <remarks>
    /// Performs a little synchronous file I/O, so call it off the UI thread. Every failed attempt draws a new
    /// run id, so a collision with an earlier run's files is never resolved by reusing or overwriting them.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="testType"/> is empty or holds a character other than an ASCII letter or digit, or
    /// <paramref name="createRunId"/> returned <see cref="Guid.Empty"/>.
    /// </exception>
    public static ResultsLog? OpenRun(
        DataDirectory dataDirectory,
        string testType,
        DateTimeOffset startRequestedUtc,
        TimeProvider timeProvider,
        Func<Guid>? createRunId = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(testType);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (testType.Length == 0 || !testType.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"The test type '{testType}' must consist of ASCII letters and digits only.", nameof(testType));
        }

        Func<Guid> nextRunId = createRunId ?? Guid.NewGuid;
        string localStart = TimeZoneInfo.ConvertTime(startRequestedUtc, timeProvider.LocalTimeZone)
            .ToString(LocalStartFormat, CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateDirectory(dataDirectory.LogsDirectory);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            return null;
        }

        for (int attempt = 0; attempt <= MaxReservationRetries; attempt++)
        {
            Guid runId = nextRunId();
            if (runId == Guid.Empty)
            {
                throw new ArgumentException("The run-id source returned an empty GUID.", nameof(createRunId));
            }

            string baseName = BaseNamePrefix + localStart + "-" + testType + "-" + SummaryFormat.RunId(runId);
            FileStream stream;
            try
            {
                stream = new FileStream(
                    GetLogPath(dataDirectory, baseName),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (IOException)
            {
                // The name is taken (FileMode.CreateNew), or the create failed transiently: draw a new run id.
                continue;
            }
            catch (Exception exception) when (IsUnusableLocation(exception))
            {
                return null;
            }

            try
            {
                return new ResultsLog(dataDirectory, runId, baseName, stream, timeProvider);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the path of a run's event log, <c>&lt;data&gt;\logs\&lt;baseName&gt;.log</c>.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="baseName">The run's base name, as stored in the journal's <c>begin</c> record.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseName"/> is empty, white space, <c>.</c> or <c>..</c>, or holds a character that is not
    /// valid in a file name, such as a directory separator.
    /// </exception>
    public static string GetLogPath(DataDirectory dataDirectory, string baseName) =>
        LogsFile(dataDirectory, baseName, LogExtension);

    /// <summary>
    /// Returns the final path of a run's JSON summary, <c>&lt;data&gt;\logs\&lt;baseName&gt;-summary.json</c>.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="baseName">The run's base name.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is not a usable file name.</exception>
    public static string GetSummaryJsonPath(DataDirectory dataDirectory, string baseName) =>
        LogsFile(dataDirectory, baseName, SummaryJsonSuffix);

    /// <summary>
    /// Returns the final path of a run's text summary, <c>&lt;data&gt;\logs\&lt;baseName&gt;-summary.txt</c>.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="baseName">The run's base name.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is not a usable file name.</exception>
    public static string GetSummaryTextPath(DataDirectory dataDirectory, string baseName) =>
        LogsFile(dataDirectory, baseName, SummaryTextSuffix);

    /// <summary>
    /// Returns the path recovery publishes a rebuilt JSON summary to when the final name is held by a file that
    /// does not parse or names another run: <c>&lt;data&gt;\logs\&lt;baseName&gt;-recovered-summary.json</c>.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="baseName">The run's base name.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is not a usable file name.</exception>
    public static string GetRecoveredSummaryJsonPath(DataDirectory dataDirectory, string baseName) =>
        LogsFile(dataDirectory, baseName, RecoveredSummaryJsonSuffix);

    /// <summary>
    /// Returns the path recovery publishes a rebuilt text summary to when the final name is held by a file that
    /// does not parse or names another run: <c>&lt;data&gt;\logs\&lt;baseName&gt;-recovered-summary.txt</c>.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="baseName">The run's base name.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is not a usable file name.</exception>
    public static string GetRecoveredSummaryTextPath(DataDirectory dataDirectory, string baseName) =>
        LogsFile(dataDirectory, baseName, RecoveredSummaryTextSuffix);

    /// <summary>
    /// Logs the run's configuration: <c>START_REQUESTED</c> at <see cref="JournalBegin.StartRequestedUtc"/>,
    /// then <c>SYSTEM</c>, <c>MEMORY</c>, <c>SIZING</c>, one <c>PLAN</c> line per step and <c>PLAN_TARGET</c>.
    /// </summary>
    /// <param name="begin">The run's <c>begin</c> journal record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="begin"/> is <see langword="null"/>.</exception>
    public void LogRunStarting(JournalBegin begin)
    {
        ArgumentNullException.ThrowIfNull(begin);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        int stepCount = begin.Steps.Count;
        JournalSystemInfo system = begin.System;
        JournalMemoryInfo memory = begin.Memory;
        EventText text = new EventText(_redactor)
            .Line(begin.StartRequestedUtc, StartRequestedEvent)
            .Token("run_id", SummaryFormat.RunId(begin.RunId))
            .Token("test_type", begin.TestType)
            .Flag("stop_on_error", begin.StopOnError)
            .Token("app_version", begin.AppVersion)
            .Line(now, SystemEvent)
            .Text("os", system.Os)
            .Text("cpu", system.CpuName)
            .Number("logical_processors", system.LogicalProcessors)
            .Number("processor_groups", system.ProcessorGroups)
            .Token("best_isa", IsaName(system.BestIsa))
            .Number("total_phys_bytes", system.TotalPhysicalBytes)
            .Number("avail_phys_bytes", system.AvailablePhysicalBytes)
            .Number("large_page_min_bytes", system.LargePageMinimum)
            .Flag("lock_privilege", system.LockPrivilegeHeld)
            .Line(now, MemoryEvent)
            .Optional("installed_bytes", memory.InstalledBytes)
            .Token("type", memory.Type.ToString())
            .Optional("speed_mts", memory.SpeedMTs)
            .Optional("channels", memory.Channels)
            .Optional("dimms", memory.Dimms)
            .Line(now, SizingEvent)
            .Optional("available_bytes", begin.AvailableBytes)
            .Optional("planned_bytes", begin.PlannedBytes);

        // A damaged journal line can hold a null step; recovery skips it, and so does the log.
        foreach (JournalStep step in begin.Steps.Where(step => step is not null))
        {
            text.Line(now, PlanEvent)
                .StepNumber(step.Number, stepCount)
                .Text("name", step.Name)
                .Token("pattern", step.Pattern.ToString())
                .Token("order", step.AccessOrder.ToString())
                .Token("threads", step.ThreadMode.ToString())
                .Number("hold_ms", step.HoldMs)
                .Number("budget_ms", step.TimeBudgetMs)
                .Number("coverage_budget", step.CoverageBudgetPercent)
                .Token("primary_mechanism", step.PrimaryMechanism.ToString());
        }

        text.Line(now, PlanTargetEvent)
            .Real("target_seconds", begin.TargetSeconds)
            .Percent("target_coverage", begin.TargetCoveragePercent);

        lock (_gate)
        {
            _stepCount = stepCount;
            Enqueue(text);
        }
    }

    /// <summary>
    /// Logs <c>PREPARED</c> and keeps the tested bytes, the denominator of the per-step coverage that
    /// <see cref="LogStepFinished"/> writes.
    /// </summary>
    /// <param name="prepared">The run's <c>prepared</c> journal record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prepared"/> is <see langword="null"/>.</exception>
    public void LogPrepared(JournalPrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        EventText text = new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), PreparedEvent)
            .Number("tested_bytes", prepared.TestedBytes)
            .Number("workers", prepared.Workers)
            .Token("page_mode", prepared.PageMode.ToString())
            .Token("isa", IsaName(prepared.IsaSelected))
            .Token("address_kind", AddressKindName(prepared.AddressKind))
            .Number("prepare_ms", prepared.PreparationMs);

        lock (_gate)
        {
            _testedBytes = prepared.TestedBytes;
            Enqueue(text);
        }
    }

    /// <summary>
    /// Logs <c>STEP_START step=k/N name="…"</c>.
    /// </summary>
    /// <param name="stepIndex">The step's 0-based index; the line writes it 1-based.</param>
    /// <param name="stepCount">The number of steps in the plan.</param>
    /// <param name="stepName">The step's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stepName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stepIndex"/> is negative or <paramref name="stepCount"/> is not positive.
    /// </exception>
    public void LogStepStarted(int stepIndex, int stepCount, string stepName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stepIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
        ArgumentNullException.ThrowIfNull(stepName);

        Enqueue(new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), StepStartEvent)
            .StepNumber(stepIndex + 1, stepCount)
            .Text("name", stepName));
    }

    /// <summary>
    /// Logs <c>STEP_END</c> with the step's passes, verified bytes, coverage of the tested bytes, true error
    /// count and overrun, followed by <c>STEP_OVERRUN</c> when the step ran past its budget.
    /// </summary>
    /// <param name="outcome">The step's outcome.</param>
    /// <param name="stepCount">The number of steps in the plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepCount"/> is not positive.</exception>
    public void LogStepFinished(StepOutcome outcome, int stepCount)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        int stepNumber = outcome.Attributes.Index + 1;
        long overrunMs = JournalValues.WholeMilliseconds(outcome.Overrun);

        lock (_gate)
        {
            EventText text = new EventText(_redactor)
                .Line(now, StepEndEvent)
                .StepNumber(stepNumber, stepCount)
                .Text("name", outcome.Attributes.Name)
                .Flag("completed", outcome.Completed)
                .Number("actual_ms", JournalValues.WholeMilliseconds(outcome.ActualDuration))
                .Number("passes", outcome.PassesCompleted)
                .Number("verified_bytes", outcome.VerifiedBytes)
                .Percent("coverage", RunProgress.CoveragePercentOf(outcome.VerifiedBytes, _testedBytes))
                .Number("errors", outcome.Errors)
                .Number("overrun_ms", overrunMs);
            if (overrunMs > 0)
            {
                text.Line(now, StepOverrunEvent)
                    .StepNumber(stepNumber, stepCount)
                    .Number("overrun_ms", overrunMs);
            }

            Enqueue(text);
        }
    }

    /// <summary>
    /// Called on every progress event; logs one <c>PROGRESS</c> line each time <see cref="RunProgress.Elapsed"/>
    /// crosses the next multiple of 10 s, so the first line comes at 10 s or later.
    /// </summary>
    /// <param name="progress">The progress snapshot.</param>
    /// <remarks>
    /// <c>remaining_ms</c> is the engine's estimate, or <c>-1</c> while it is still being calculated, so the
    /// validation script can gate the time-remaining accuracy on samples that have an estimate.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is <see langword="null"/>.</exception>
    public void LogProgress(RunProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_gate)
        {
            if (progress.Elapsed < _nextProgressLineAt)
            {
                return;
            }

            // One line per crossing, however far a late tick overshot: the next line is due at the next multiple.
            long intervalsElapsed = progress.Elapsed.Ticks / _progressLineInterval.Ticks;
            _nextProgressLineAt = TimeSpan.FromTicks((intervalsElapsed + 1) * _progressLineInterval.Ticks);

            Enqueue(new EventText(_redactor)
                .Line(_timeProvider.GetUtcNow(), ProgressEvent)
                .StepNumber(progress.StepIndex + 1, progress.StepCount)
                .Number("elapsed_ms", JournalValues.WholeMilliseconds(progress.Elapsed))
                .Number("remaining_ms", progress.Remaining is { } remaining ? JournalValues.WholeMilliseconds(remaining) : -1)
                .Percent("coverage", progress.CoveragePercent)
                .Number("errors", progress.TotalErrors));
        }
    }

    /// <summary>
    /// Logs one <c>ERROR</c> line per record until <see cref="ErrorGrouping.StoredDetailCap"/> records have been
    /// logged, then one <c>ERROR_CAP_REACHED</c> line; records beyond the cap are not logged, and the true total
    /// keeps appearing in later <c>PROGRESS</c> and <c>COMPLETED</c> lines.
    /// </summary>
    /// <param name="records">The batch's error records; empty once the engine's detail cap is reached.</param>
    /// <param name="totalErrors">The true error total carried by the batch.</param>
    /// <remarks>
    /// <para>
    /// The address is written with its kind, <c>address_kind=virtual</c> in v1, and the re-read outcome with
    /// its observation and inference, which is never stated as an established fault.
    /// </para>
    /// <para>
    /// The records within the cap are copied by reference and enqueued; the drain renders their lines. The
    /// caller may therefore reuse <paramref name="records"/> as soon as this returns, and a large batch costs it
    /// only that copy. Every line still carries the time of this call and the step count known at it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> contains a <see langword="null"/> element.</exception>
    public void LogErrors(IReadOnlyList<ErrorRecord> records, ulong totalErrors)
    {
        ArgumentNullException.ThrowIfNull(records);
        for (int position = 0; position < records.Count; position++)
        {
            if (records[position] is null)
            {
                throw new ArgumentException($"Error record {position} is null.", nameof(records));
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_capReported)
            {
                return;
            }

            int take = Math.Min(records.Count, ErrorGrouping.StoredDetailCap - _loggedErrorRecords);
            _loggedErrorRecords += take;
            bool capReached = _loggedErrorRecords >= ErrorGrouping.StoredDetailCap;
            if (take == 0 && !capReached)
            {
                return;
            }

            _capReported = capReached;

            // Unlike the other events, these lines are rendered by the drain, not here: the run controller calls
            // this on the UI thread, and a batch of up to 10,000 records of 21 fields each is too much formatting
            // for it. The private copy lets the caller reuse its list; the time and step count are this call's.
            var accepted = new ErrorRecord[take];
            for (int position = 0; position < take; position++)
            {
                accepted[position] = records[position];
            }

            Enqueue(new ErrorBatch(now, accepted, _stepCount, totalErrors, capReached));
        }
    }

    /// <summary>
    /// Logs <c>SENSOR sensor= status= message="…"</c>, for example a reading that became unavailable or stale.
    /// </summary>
    /// <param name="sensor">The sensor, for example <c>"CPU"</c> or <c>"RAM"</c>.</param>
    /// <param name="status">The sensor status name.</param>
    /// <param name="message">What happened, in plain language.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void LogSensorEvent(string sensor, string status, string message)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(message);

        Enqueue(new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), SensorEvent)
            .Token("sensor", sensor)
            .Token("status", status)
            .Text("message", message));
    }

    /// <summary>
    /// Logs <c>THERMAL_STOP sensor= celsius= limit_celsius=</c> at <paramref name="utc"/>.
    /// </summary>
    /// <param name="sensor">The sensor whose reading reached its limit, <c>"CPU"</c> or <c>"RAM"</c>.</param>
    /// <param name="celsius">The reading, in degrees Celsius.</param>
    /// <param name="limitCelsius">The limit it reached, in degrees Celsius.</param>
    /// <param name="utc">The time of the reading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sensor"/> is <see langword="null"/>.</exception>
    public void LogThermalStop(string sensor, double celsius, double limitCelsius, DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        Enqueue(new EventText(_redactor)
            .Line(utc, ThermalStopEvent)
            .Token("sensor", sensor)
            .Real("celsius", celsius)
            .Real("limit_celsius", limitCelsius));
    }

    /// <summary>
    /// Logs <c>JOURNAL_FAILURE message="…"</c>: a progress-journal write failed or timed out.
    /// </summary>
    /// <param name="message">The failure, for example the exception message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public void LogJournalFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Enqueue(new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), JournalFailureEvent)
            .Text("message", message));
    }

    /// <summary>
    /// Logs <c>STOP_REQUESTED reason=</c> at <paramref name="utc"/>, followed by <c>time_into_test_ms=</c> when the
    /// engine's own time of the stop decision is known.
    /// </summary>
    /// <remarks>
    /// A requested stop's stamp starts its stop-latency interval. A stop the engine decides itself is stamped when
    /// the controller learns of it, so its <c>time_into_test_ms</c> starts the interval instead. The class remarks
    /// state which stops are logged.
    /// </remarks>
    /// <param name="utc">The time the stop was requested, or the time the controller learned of the decision.</param>
    /// <param name="reason">
    /// The stop reason, for example <c>"UserStop"</c>, <c>"Thermal"</c> or <c>"FirstError"</c>.
    /// </param>
    /// <param name="timeIntoTest">
    /// The engine's test time of the decision, for a stop the engine decides itself such as the first confirmed error
    /// with Stop on error on; on the same clock as <c>COMPLETED elapsed_ms</c>, so the decision-to-Completed interval
    /// is their difference. <see langword="null"/> for a stop the controller requests, and then no key is written.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    public void LogStopRequested(DateTimeOffset utc, string reason, TimeSpan? timeIntoTest = null)
    {
        ArgumentNullException.ThrowIfNull(reason);

        EventText text = new EventText(_redactor)
            .Line(utc, StopRequestedEvent)
            .Token("reason", reason);
        if (timeIntoTest is { } decided)
        {
            text.Number("time_into_test_ms", JournalValues.WholeMilliseconds(decided));
        }

        Enqueue(text);
    }

    /// <summary>
    /// Logs <c>COMPLETED</c> at <paramref name="utc"/> with the end reason and the engine's true totals.
    /// </summary>
    /// <param name="utc">The time the engine reported Completed.</param>
    /// <param name="endReason">Why the run ended.</param>
    /// <param name="endDetail">The end reason's diagnostic detail, or <see langword="null"/>.</param>
    /// <param name="totalErrors">The true error total.</param>
    /// <param name="verifiedBytes">The bytes verified over the whole run.</param>
    /// <param name="elapsed">The engine's elapsed test time.</param>
    /// <param name="firstError">
    /// The time into the test of the first error, or <see langword="null"/>, written <c>first_error_ms=-1</c>.
    /// </param>
    public void LogCompleted(
        DateTimeOffset utc,
        RunEndReason endReason,
        string? endDetail,
        ulong totalErrors,
        ulong verifiedBytes,
        TimeSpan elapsed,
        TimeSpan? firstError)
    {
        Enqueue(new EventText(_redactor)
            .Line(utc, CompletedEvent)
            .Token("end_reason", endReason.ToString())
            .Number("total_errors", totalErrors)
            .Number("verified_bytes", verifiedBytes)
            .Number("elapsed_ms", JournalValues.WholeMilliseconds(elapsed))
            .Number("first_error_ms", firstError is { } first ? JournalValues.WholeMilliseconds(first) : -1)
            .Text("detail", endDetail));
    }

    /// <summary>
    /// Logs <c>VERDICT verdict=PASS|FAIL|ABORTED state= reason= errors= detail="…"</c> at <paramref name="utc"/>.
    /// </summary>
    /// <param name="utc">The time the terminal state was shown.</param>
    /// <param name="terminalState">The terminal state just reached, read from the run state machine.</param>
    /// <param name="endReason">Why the run reached it.</param>
    /// <param name="endDetail">The end reason's diagnostic detail, or <see langword="null"/>.</param>
    /// <param name="totalErrors">The true error total.</param>
    /// <exception cref="ArgumentException"><paramref name="terminalState"/> is not a terminal state.</exception>
    public void LogVerdict(DateTimeOffset utc, RunState terminalState, RunEndReason endReason, string? endDetail, ulong totalErrors)
    {
        string verdict = (terminalState.IsTerminal() ? terminalState.ToVerdictText() : null)
            ?? throw new ArgumentException(
                $"A verdict needs a terminal state, not {terminalState}.", nameof(terminalState));

        Enqueue(new EventText(_redactor)
            .Line(utc, VerdictEvent)
            .Token("verdict", verdict)
            .Token("state", terminalState.ToString())
            .Token("reason", endReason.ToString())
            .Number("errors", totalErrors)
            .Text("detail", endDetail));
    }

    /// <summary>
    /// Logs <c>RELEASED bytes= release_ms=</c> at <paramref name="utc"/>.
    /// </summary>
    /// <param name="utc">The time the engine reported Released.</param>
    /// <param name="releasedBytes">The bytes the engine released.</param>
    /// <param name="releaseTime">The time the release took.</param>
    public void LogReleased(DateTimeOffset utc, ulong releasedBytes, TimeSpan releaseTime)
    {
        Enqueue(new EventText(_redactor)
            .Line(utc, ReleasedEvent)
            .Number("bytes", releasedBytes)
            .Number("release_ms", JournalValues.WholeMilliseconds(releaseTime)));
    }

    /// <summary>
    /// Logs <c>RELEASE_NOT_CONFIRMED message="memory release not confirmed"</c> at <paramref name="utc"/>: Released
    /// had not arrived 30 s after Completed, or by the shutdown deadline, and the summaries are published with
    /// <c>memory.releaseSeconds: null</c>.
    /// </summary>
    /// <param name="utc">The time publication gave up waiting.</param>
    public void LogReleaseNotConfirmed(DateTimeOffset utc)
    {
        Enqueue(new EventText(_redactor)
            .Line(utc, ReleaseNotConfirmedEvent)
            .Text("message", ReleaseNotConfirmedMessage));
    }

    /// <summary>
    /// Logs <c>NOTICE message="…"</c>, for anything else the run controller reports, such as a failed priority
    /// change or a failure to save the results.
    /// </summary>
    /// <param name="message">The notice.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public void LogNotice(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Enqueue(new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), NoticeEvent)
            .Text("message", message));
    }

    /// <summary>
    /// Logs <c>CALLBACK_FAULT source="…" error="…"</c> at <paramref name="utc"/>: an exception escaped an engine
    /// callback or an engine-event handler, and the run is stopped as a host fault. The line is that stop's cause;
    /// the exception's full text is in <c>app-errors.log</c> under the same source and this run's id.
    /// </summary>
    /// <param name="utc">The time the exception was caught.</param>
    /// <param name="source">
    /// Where it was caught, as its <c>app-errors.log</c> entry names it, for example
    /// <c>"EngineCallback:on_progress"</c>.
    /// </param>
    /// <param name="exception">The exception; <c>error</c> is its type's full name, a colon and its message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public void LogCallbackFault(DateTimeOffset utc, string source, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        Enqueue(new EventText(_redactor)
            .Line(utc, CallbackFaultEvent)
            .Text("source", source)
            .Text("error", exception.GetType().FullName + ": " + exception.Message));
    }

    /// <summary>
    /// Returns a task that completes once every event enqueued before this call has been written and flushed
    /// to the device, or has failed to write (see <see cref="WriteFailure"/>).
    /// </summary>
    /// <returns>
    /// The flush task; after <see cref="Dispose"/>, the task that completes when the drain has ended.
    /// </returns>
    public Task FlushAsync()
    {
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new LogEntry(null, null, request)) ? request.Task : _drainTask;
    }

    /// <summary>
    /// Publishes the run's summaries on the thread pool: the JSON first, then the text, each through
    /// <see cref="PublishFile"/>, logging <c>SUMMARY_PUBLISHED path="…"</c> or <c>SUMMARY_FAILED</c> per file.
    /// </summary>
    /// <param name="summary">The run's summary; its <c>run.id</c> must be this run's id.</param>
    /// <returns>
    /// The outcome. A file that could not be published, including one whose final name already exists, is a
    /// failure reported in the result and never thrown; the other file is still attempted.
    /// </returns>
    /// <remarks>
    /// Both documents are rendered before any file is written, so a rendering defect (a non-finite double,
    /// which <see cref="RunSummary.Build"/> never produces) faults the task with nothing published. Call it
    /// after Released, or after giving up on Released, and write the journal's <c>end</c> record only when it
    /// succeeded: on failure the journal is kept, so the next launch publishes the missing summary.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="summary"/> names another run.</exception>
    public Task<SummaryPublishResult> PublishSummaryAsync(RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string expectedId = SummaryFormat.RunId(RunId);
        if (!string.Equals(summary.Run.Id, expectedId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The summary belongs to run '{summary.Run.Id}', not to this run '{expectedId}'.", nameof(summary));
        }

        return Task.Run(() => PublishSummaries(summary));
    }

    /// <summary>
    /// Publishes <paramref name="content"/> at <paramref name="finalPath"/> crash-consistently: it is written as
    /// UTF-8 without a byte-order mark to <c>&lt;finalPath&gt;.tmp</c>, flushed to the device and closed, then
    /// renamed to <paramref name="finalPath"/> with overwriting disabled.
    /// </summary>
    /// <param name="finalPath">The final path, normally one of the summary paths of this type.</param>
    /// <param name="content">The complete file content.</param>
    /// <remarks>
    /// A crash leaves either no final file or the complete one, never a partial one. A stale temporary file
    /// left by an earlier crash is overwritten. The rename stays on one volume because the temporary file sits
    /// beside the final one. On failure the temporary file is deleted, best effort, and the exception is
    /// rethrown.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="finalPath"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="IOException">
    /// The file could not be written or flushed, or <paramref name="finalPath"/> already exists.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The location denies write access.</exception>
    public static void PublishFile(string finalPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(content);

        string tempPath = finalPath + TempSuffix;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(_utf8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Appends one block to <see cref="DataDirectory.AppErrorsLogPath"/>: an <c>APP_ERROR source="…" run_id=…</c> line
    /// stamped <paramref name="utc"/>, the exception's full text with every line indented by two spaces, and a blank
    /// line, flushed to the device. Only a header starts at the margin, so a line break inside an exception message
    /// can never forge an entry.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="source">
    /// Where the exception was caught, for example <c>"EngineCallback:on_progress"</c> or
    /// <c>"DispatcherUnhandledException"</c>.
    /// </param>
    /// <param name="exception">The exception.</param>
    /// <param name="utc">The time it was caught.</param>
    /// <param name="runId">
    /// The run in progress or still publishing when it was caught, written <c>run_id=&lt;32 hex digits&gt;</c> after
    /// the source so the entry can be tied to that run's <c>.log</c> and summaries; <see langword="null"/>, or
    /// <see cref="Guid.Empty"/>, when there is none, and then the header has no <c>run_id</c>.
    /// </param>
    /// <remarks>
    /// Synchronous, for the application's unhandled-exception handlers. It runs inside crash handlers, so it
    /// never throws for a failure to format or write the block: such a failure is swallowed. Only the
    /// exception's own text is written, with the data folder and the user's folders and name written as
    /// <c>&lt;data&gt;</c>, <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, <c>%USERPROFILE%</c> and <c>%USERNAME%</c>, as in
    /// the <c>.log</c>; no user or machine name is added.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataDirectory"/> or <paramref name="exception"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is <see langword="null"/>, empty or white space.</exception>
    public static void AppendAppError(
        DataDirectory dataDirectory,
        string source,
        Exception exception,
        DateTimeOffset utc,
        Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        WriteAppError(dataDirectory.AppErrorsLogPath, dataDirectory.RootPath, source, exception, utc, runId);
    }

    /// <summary>
    /// Performs <see cref="AppendAppError"/> on the thread pool and returns at once: fire-and-forget, with any
    /// exception observed. Safe on the engine coordinator thread, which must neither block nor perform I/O.
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <param name="source">Where the exception was caught, for example <c>"EngineCallback:on_progress"</c>.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="utc">The time it was caught.</param>
    /// <param name="runId">
    /// The run in progress or still publishing when it was caught, or <see langword="null"/> when there is none; see
    /// <see cref="AppendAppError"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataDirectory"/> or <paramref name="exception"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is <see langword="null"/>, empty or white space.</exception>
    public static void QueueAppError(
        DataDirectory dataDirectory,
        string source,
        Exception exception,
        DateTimeOffset utc,
        Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        string path = dataDirectory.AppErrorsLogPath;
        string dataRoot = dataDirectory.RootPath;
        _ = Task.Run(() => WriteAppError(path, dataRoot, source, exception, utc, runId)).ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Stops accepting events, waits at most 2 s for the queued ones to be written and flushed, and closes the
    /// <c>.log</c>.
    /// </summary>
    /// <remarks>
    /// The wait is bounded so that closing the window never hangs on a stalled disk. A drain still running
    /// when the wait ends finishes the queue and closes the file itself. Later calls do nothing.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _ = _drainTask.Wait(_disposeDrainTimeout);
    }

    private static string LogsFile(DataDirectory dataDirectory, string baseName, string suffix)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        // Recovery passes a base name read back from a journal; a damaged one must not reach outside the logs
        // folder, so only a plain file name is accepted.
        if (baseName is "." or ".." || baseName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{baseName}' is not a usable results file base name.", nameof(baseName));
        }

        return Path.Combine(dataDirectory.LogsDirectory, baseName + suffix);
    }

    // The failures Directory.CreateDirectory and FileStream raise for a logs folder that cannot hold the run's
    // files: read-only or missing media, denied access, or a path the file system rejects.
    private static bool IsUnusableLocation(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;

    private static string IsaName(InstructionSet isa) =>
        Enum.IsDefined(isa) ? SummaryFormat.InstructionSetName(isa) : isa.ToString();

    private static string AddressKindName(AddressKind kind) => kind switch
    {
        AddressKind.Virtual => "virtual",
        AddressKind.Physical => "physical",
        _ => kind.ToString(),
    };

    private static string RereadText(RereadOutcome outcome) =>
        Enum.IsDefined(outcome) ? RereadOutcomeText.Describe(outcome) : outcome.ToString();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            // Best effort: a temporary file that cannot be deleted is overwritten by the next attempt.
        }
    }

    private static void WriteAppError(
        string path,
        string dataRoot,
        string source,
        Exception exception,
        DateTimeOffset utc,
        Guid? runId)
    {
        // Runs inside crash handlers: nothing here may throw, so every failure to format or write is swallowed.
        try
        {
            var redactor = new PersonalDataRedactor(dataRoot);
            string details;
            try
            {
                details = exception.ToString();
            }
            catch (Exception formatFailure)
            {
                details = exception.GetType().FullName + " (its text could not be formatted: "
                    + formatFailure.GetType().FullName + ")";
            }

            EventText header = new EventText(redactor)
                .Line(utc, AppErrorEvent)
                .Text("source", source);
            if (runId is { } id && id != Guid.Empty)
            {
                header.Token("run_id", SummaryFormat.RunId(id));
            }

            // Every line of the text is indented, so only an entry's own header starts at the margin and a line break
            // inside an exception message can never forge one. Any Unicode line ending starts a new line.
            string block = header.Build()
                + AppErrorTextIndent
                + redactor.Redact(details).ReplaceLineEndings(EventText.NewLine + AppErrorTextIndent)
                + EventText.NewLine + EventText.NewLine;
            byte[] bytes = _utf8.GetBytes(block);

            lock (_appErrorsGate)
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed: logging a fault must never raise another one inside a crash handler.
        }
    }

    private static void AppendErrorLine(EventText text, DateTimeOffset utc, ErrorRecord record, int stepCount, ulong totalErrors)
    {
        text.Line(utc, ErrorEvent)
            .Number("seq", record.Sequence)
            .StepNumber(record.StepIndex + 1, stepCount)
            .Text("name", record.StepName)
            .Number("pass", record.PassIndex + 1L)
            .Number("time_into_test_ms", JournalValues.WholeMilliseconds(record.TimeIntoTest))
            .Hex("address", record.Address)
            .Token("address_kind", AddressKindName(record.AddressKind))
            .Hex("expected", record.Expected)
            .Hex("actual", record.Actual)
            .Hex("reread", record.Reread)
            .Hex("bit_mask", record.BitMask)
            .Token("bits", BitList(record.BitPositions))
            .Text("outcome", RereadText(record.RereadOutcome))
            .Token("access", record.AccessType.ToString())
            .Number("time_since_write_us", record.TimeSinceWrite.Ticks / TimeSpan.TicksPerMicrosecond)
            .Number("worker", record.WorkerIndex)
            .Number("lp", record.LogicalProcessor)
            .Number("group", record.ProcessorGroup)
            .Number("active_workers", record.ActiveWorkers)
            .Number("total", totalErrors);
    }

    private static string BitList(IReadOnlyList<int> positions) =>
        positions.Count == 0
            ? "none"
            : string.Join(',', positions.Select(position => position.ToString(CultureInfo.InvariantCulture)));

    private SummaryPublishResult PublishSummaries(RunSummary summary)
    {
        // Rendered before any file is touched: a rendering defect throws with nothing published.
        string json = RunSummaryJson.Serialize(summary);
        string text = TextSummaryFormatter.Format(summary);

        string? jsonError = TryPublish(SummaryJsonPath, json);
        string? textError = TryPublish(SummaryTextPath, text);
        return new SummaryPublishResult(
            Succeeded: jsonError is null && textError is null,
            JsonPath: jsonError is null ? SummaryJsonPath : null,
            TextPath: textError is null ? SummaryTextPath : null,
            Error: jsonError ?? textError);
    }

    private string? TryPublish(string finalPath, string content)
    {
        try
        {
            PublishFile(finalPath, content);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            Enqueue(new EventText(_redactor)
                .Line(_timeProvider.GetUtcNow(), SummaryFailedEvent)
                .Text("path", DataRelativePath(finalPath))
                .Text("error", exception.Message));
            return exception.Message;
        }

        Enqueue(new EventText(_redactor)
            .Line(_timeProvider.GetUtcNow(), SummaryPublishedEvent)
            .Text("path", DataRelativePath(finalPath)));
        return null;
    }

    // The data folder's own location can name the Windows account (%LOCALAPPDATA%\RamQuick), so a results file is
    // written relative to it, as logs\<file name>.
    private string DataRelativePath(string path) => Path.GetRelativePath(DataDirectory.RootPath, path);

    private void Enqueue(EventText text)
    {
        // Fails only after Dispose, when events are discarded by design.
        _ = _channel.Writer.TryWrite(new LogEntry(text.Build(), null, null));
    }

    private void Enqueue(ErrorBatch batch)
    {
        // Fails only after Dispose, when events are discarded by design.
        _ = _channel.Writer.TryWrite(new LogEntry(null, batch, null));
    }

    private async Task DrainAsync()
    {
        ChannelReader<LogEntry> reader = _channel.Reader;
        var flushRequests = new List<TaskCompletionSource>();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                for (int taken = 0; taken < MaxBatchEntries && reader.TryRead(out LogEntry entry); taken++)
                {
                    if (entry.Text is { } text)
                    {
                        Append(text);
                    }

                    if (entry.Errors is { } errors)
                    {
                        AppendErrors(errors);
                    }

                    if (entry.FlushRequest is { } request)
                    {
                        flushRequests.Add(request);
                    }
                }

                FlushToDevice();
                CompleteAll(flushRequests);
            }
        }
        finally
        {
            CompleteAll(flushRequests);
            Close();
        }
    }

    private void Append(string text)
    {
        try
        {
            _stream.Write(_utf8.GetBytes(text));
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            RecordWriteFailure(exception);
        }
    }

    private void AppendErrors(ErrorBatch batch)
    {
        ErrorRecord[] records = batch.Records;
        for (int start = 0; start < records.Length; start += MaxErrorLinesPerWrite)
        {
            int end = Math.Min(records.Length, start + MaxErrorLinesPerWrite);
            EventText text = new(_redactor);
            for (int position = start; position < end; position++)
            {
                AppendErrorLine(text, batch.Utc, records[position], batch.StepCount, batch.TotalErrors);
            }

            Append(text.Build());
        }

        if (batch.CapReached)
        {
            Append(new EventText(_redactor)
                .Line(batch.Utc, ErrorCapReachedEvent)
                .Number("stored", ErrorGrouping.StoredDetailCap)
                .Number("total", batch.TotalErrors)
                .Build());
        }
    }

    private void FlushToDevice()
    {
        try
        {
            _stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            RecordWriteFailure(exception);
        }
    }

    private void Close()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            RecordWriteFailure(exception);
        }
    }

    private void RecordWriteFailure(Exception exception) =>
        Interlocked.CompareExchange(ref _writeFailure, exception, null);

    private static void CompleteAll(List<TaskCompletionSource> requests)
    {
        foreach (TaskCompletionSource request in requests)
        {
            request.TrySetResult();
        }

        requests.Clear();
    }

    /// <summary>
    /// One item of the drain queue: the text of one event, a batch of error records for the drain to render, or
    /// a flush request.
    /// </summary>
    /// <param name="Text">The event's lines, each ending in <c>\r\n</c>, or <see langword="null"/>.</param>
    /// <param name="Errors">
    /// The records whose <c>ERROR</c> lines, and any <c>ERROR_CAP_REACHED</c> line, the drain renders and
    /// writes, or <see langword="null"/>.
    /// </param>
    /// <param name="FlushRequest">
    /// Completed once everything queued before it is flushed to the device, or <see langword="null"/>.
    /// </param>
    private readonly record struct LogEntry(string? Text, ErrorBatch? Errors, TaskCompletionSource? FlushRequest);

    /// <summary>
    /// The part of one <see cref="LogErrors"/> call that reaches the <c>.log</c>, as the drain renders it.
    /// </summary>
    /// <param name="Utc">The time of the call, which stamps every line of the batch.</param>
    /// <param name="Records">
    /// The log's own copy of the records within the cap, in the caller's order; never modified once enqueued.
    /// </param>
    /// <param name="StepCount">The plan's step count at the call, or 0 when no run start had been logged.</param>
    /// <param name="TotalErrors">The true error total the batch carried.</param>
    /// <param name="CapReached">Whether the batch reached the cap, so <c>ERROR_CAP_REACHED</c> follows its lines.</param>
    private sealed record ErrorBatch(DateTimeOffset Utc, ErrorRecord[] Records, int StepCount, ulong TotalErrors, bool CapReached);

    /// <summary>
    /// Keeps the Windows account out of free text written to the <c>.log</c> and <c>app-errors.log</c>: the run's
    /// data folder is written <c>&lt;data&gt;</c>, the user's local and roaming application data folders and
    /// profile folder <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c> and <c>%USERPROFILE%</c>, and the user name, where it
    /// is a whole path segment, <c>%USERNAME%</c>.
    /// </summary>
    /// <remarks>
    /// Folders are compared case-insensitively with <c>\</c> and <c>/</c> alike, the longest first, so a data folder
    /// under the profile is written <c>&lt;data&gt;</c>. A folder or name matches only where its path segment ends,
    /// at a separator, a quote, white space, punctuation or the end of the text, so <c>C:\Users\ann</c> never
    /// matches inside <c>C:\Users\anna</c>. A folder Windows reports empty, or as a bare volume root, is skipped.
    /// Never throws, so it is safe inside the crash handlers' app-error writes.
    /// </remarks>
    private sealed class PersonalDataRedactor
    {
        private const string DataFolderToken = "<data>";
        private const string UserNameToken = "%USERNAME%";

        // The account's folders and name are read once: they do not change while the process runs.
        private static readonly (string Folder, string Token)[] _userFolders = ReadUserFolders();
        private static readonly string? _userName = ReadUserName();

        private readonly (string Folder, string Token)[] _folders;

        /// <summary>
        /// Initializes a new instance of the <see cref="PersonalDataRedactor"/> class for the run whose data folder
        /// is <paramref name="dataRoot"/>.
        /// </summary>
        public PersonalDataRedactor(string? dataRoot)
        {
            var folders = new List<(string Folder, string Token)>(_userFolders.Length + 1);
            if (UsableFolder(dataRoot) is { } root)
            {
                folders.Add((root, DataFolderToken));
            }

            folders.AddRange(_userFolders);
            _folders = folders.OrderByDescending(entry => entry.Folder.Length).ToArray();
        }

        /// <summary>
        /// Returns <paramref name="text"/> with every folder and user-name segment replaced by its placeholder.
        /// </summary>
        public string Redact(string text)
        {
            // Every folder and every user-name segment holds or follows a directory separator.
            if (text.AsSpan().IndexOfAny('\\', '/') < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            int position = 0;
            while (position < text.Length)
            {
                if (MatchFolder(text, position) is { } match)
                {
                    builder.Append(match.Token);
                    position += match.Length;
                }
                else if (MatchesUserName(text, position))
                {
                    builder.Append(UserNameToken);
                    position += _userName!.Length;
                }
                else
                {
                    builder.Append(text[position]);
                    position++;
                }
            }

            return builder.ToString();
        }

        private (string Token, int Length)? MatchFolder(string text, int position)
        {
            if (position > 0 && char.IsLetterOrDigit(text[position - 1]))
            {
                return null;
            }

            foreach ((string folder, string token) in _folders)
            {
                if (RegionMatches(text, position, folder) && EndsSegment(text, position + folder.Length))
                {
                    return (token, folder.Length);
                }
            }

            return null;
        }

        private static bool MatchesUserName(string text, int position) =>
            _userName is { } userName
            && position > 0
            && IsSeparator(text[position - 1])
            && RegionMatches(text, position, userName)
            && EndsSegment(text, position + userName.Length);

        private static bool RegionMatches(string text, int position, string value)
        {
            if (text.Length - position < value.Length)
            {
                return false;
            }

            for (int offset = 0; offset < value.Length; offset++)
            {
                char actual = text[position + offset];
                char expected = value[offset];
                if (IsSeparator(actual) && IsSeparator(expected))
                {
                    continue;
                }

                if (char.ToUpperInvariant(actual) != char.ToUpperInvariant(expected))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool EndsSegment(string text, int end) =>
            end == text.Length
            || IsSeparator(text[end])
            || char.IsWhiteSpace(text[end])
            || text[end] is '"' or '\'' or '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>';

        private static bool IsSeparator(char character) => character is '\\' or '/';

        // A folder is matched without its trailing separator. A volume root such as C:\ names no account, and
        // replacing it would rewrite every path on that volume, so it is skipped like a folder Windows does not know.
        private static string? UsableFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            string trimmed = Path.TrimEndingDirectorySeparator(folder.Trim());
            return Path.GetPathRoot(trimmed) is { } volume && volume.Length >= trimmed.Length ? null : trimmed;
        }

        private static (string Folder, string Token)[] ReadUserFolders()
        {
            var folders = new List<(string Folder, string Token)>(3);
            AddUserFolder(folders, Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%");
            AddUserFolder(folders, Environment.SpecialFolder.ApplicationData, "%APPDATA%");
            AddUserFolder(folders, Environment.SpecialFolder.UserProfile, "%USERPROFILE%");
            return folders.ToArray();
        }

        private static void AddUserFolder(
            List<(string Folder, string Token)> folders,
            Environment.SpecialFolder folder,
            string token)
        {
            string? path;
            try
            {
                // Windows reports a folder it does not know as an empty string.
                path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch (PlatformNotSupportedException)
            {
                path = null;
            }

            if (UsableFolder(path) is { } usable)
            {
                folders.Add((usable, token));
            }
        }

        private static string? ReadUserName()
        {
            string userName;
            try
            {
                userName = Environment.UserName;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }

            // A name holding a separator could never be one path segment.
            return string.IsNullOrWhiteSpace(userName) || userName.AsSpan().IndexOfAny('\\', '/') >= 0
                ? null
                : userName;
        }
    }

    /// <summary>
    /// Formats the lines of one event in the <c>.log</c> line format described on <see cref="ResultsLog"/>.
    /// </summary>
    private sealed class EventText
    {
        /// <summary>
        /// The line terminator of the <c>.log</c> and <c>app-errors.log</c>.
        /// </summary>
        internal const string NewLine = "\r\n";

        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        private const string PercentFormat = "0.0";
        private const string RoundTripFormat = "R";
        private const string NotCaptured = "unknown";

        private readonly StringBuilder _text = new();
        private readonly PersonalDataRedactor _redactor;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventText"/> class whose free-text values pass through
        /// <paramref name="redactor"/>.
        /// </summary>
        public EventText(PersonalDataRedactor redactor) => _redactor = redactor;

        /// <summary>
        /// Ends the current line, if any, and starts <c>&lt;utc&gt; &lt;eventName&gt;</c>.
        /// </summary>
        public EventText Line(DateTimeOffset utc, string eventName)
        {
            if (_text.Length > 0)
            {
                _text.Append(NewLine);
            }

            _text.Append(utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(eventName);
            return this;
        }

        /// <summary>
        /// Appends a value that is quoted only when it needs to be.
        /// </summary>
        public EventText Token(string key, string? value)
        {
            AppendValue(Key(key), value, alwaysQuote: false);
            return this;
        }

        /// <summary>
        /// Appends a free-text value, always quoted, with the locations that name the Windows account replaced
        /// by their placeholders.
        /// </summary>
        public EventText Text(string key, string? value)
        {
            AppendValue(Key(key), value is null ? null : _redactor.Redact(value), alwaysQuote: true);
            return this;
        }

        /// <summary>
        /// Appends a signed integer.
        /// </summary>
        public EventText Number(string key, long value)
        {
            Key(key).Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Appends an unsigned integer.
        /// </summary>
        public EventText Number(string key, ulong value)
        {
            Key(key).Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Appends a signed integer, or <c>unknown</c> when it was not captured.
        /// </summary>
        public EventText Optional(string key, long? value) =>
            value is { } captured ? Number(key, captured) : Token(key, NotCaptured);

        /// <summary>
        /// Appends an unsigned integer, or <c>unknown</c> when it was not captured.
        /// </summary>
        public EventText Optional(string key, ulong? value) =>
            value is { } captured ? Number(key, captured) : Token(key, NotCaptured);

        /// <summary>
        /// Appends <c>true</c> or <c>false</c>.
        /// </summary>
        public EventText Flag(string key, bool value)
        {
            Key(key).Append(value ? "true" : "false");
            return this;
        }

        /// <summary>
        /// Appends a percentage with one decimal.
        /// </summary>
        public EventText Percent(string key, double value)
        {
            Key(key).Append(value.ToString(PercentFormat, CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Appends a double in the round-trip format.
        /// </summary>
        public EventText Real(string key, double value)
        {
            Key(key).Append(value.ToString(RoundTripFormat, CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Appends a 64-bit word as <c>0x</c> plus 16 uppercase hex digits.
        /// </summary>
        public EventText Hex(string key, ulong value)
        {
            Key(key).Append(SummaryFormat.Hex64(value));
            return this;
        }

        /// <summary>
        /// Appends <c>step=k/N</c>, or <c>step=k</c> when the plan's step count is unknown (not positive).
        /// </summary>
        public EventText StepNumber(int number, int count)
        {
            StringBuilder builder = Key("step").Append(number.ToString(CultureInfo.InvariantCulture));
            if (count > 0)
            {
                builder.Append('/').Append(count.ToString(CultureInfo.InvariantCulture));
            }

            return this;
        }

        /// <summary>
        /// Returns the event's text, its last line terminated.
        /// </summary>
        public string Build() => _text.ToString() + NewLine;

        private static bool NeedsQuotes(string value)
        {
            if (value.Length == 0)
            {
                return true;
            }

            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character) || char.IsControl(character) || character == '"')
                {
                    return true;
                }
            }

            return false;
        }

        private StringBuilder Key(string key) => _text.Append(' ').Append(key).Append('=');

        private static void AppendValue(StringBuilder builder, string? value, bool alwaysQuote)
        {
            string text = value ?? string.Empty;
            if (!alwaysQuote && !NeedsQuotes(text))
            {
                builder.Append(text);
                return;
            }

            builder.Append('"');
            foreach (char character in text)
            {
                if (character is '"' or '\\')
                {
                    builder.Append('\\').Append(character);
                }
                else if (char.IsControl(character))
                {
                    // Keeps every event on the lines it writes.
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(character);
                }
            }

            builder.Append('"');
        }
    }
}
