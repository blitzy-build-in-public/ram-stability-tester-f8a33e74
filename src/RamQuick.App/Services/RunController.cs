using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using RamQuick.Analysis;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Plans;
using RamQuick.Sensors;

namespace RamQuick.App.Services;

/// <summary>
/// The outcome of a Start. <see cref="RunController.TryStart(TestType, bool)"/> returns at once with
/// <see cref="Accepted"/>, <see cref="NotElevated"/>, <see cref="RestartingElevated"/>, <see cref="Busy"/>, or
/// <see cref="DataFolderUnusable"/> when the controller has no data folder at all. The asynchronous continuation of
/// an accepted Start ends with <see cref="Accepted"/> once the run has entered Preparing, or with
/// <see cref="DataFolderUnusable"/>, <see cref="EngineUnavailable"/>, <see cref="InsufficientMemory"/> or
/// <see cref="Withdrawn"/>. Every value other than <see cref="Accepted"/> leaves the run state unchanged.
/// </summary>
public enum StartResult
{
    /// <summary>
    /// Start was accepted. The run continues asynchronously, in this order: the data folder is probed and a journal
    /// left by an earlier failed publication is published, the engine describes the machine and the region is sized,
    /// the results log reserves the run's base name, and the progress journal's <c>begin</c> record is made durable;
    /// then the run enters Preparing. Each of those steps can still refuse the Start, with the state unchanged and a
    /// notice, and the continuation then ends with that refusal instead.
    /// </summary>
    Accepted,

    /// <summary>
    /// The process is not elevated, and the user declined the offer to restart as Administrator or the restart
    /// could not be started.
    /// </summary>
    NotElevated,

    /// <summary>
    /// The process is not elevated, the user accepted the offer, and the elevated instance was started;
    /// <see cref="RunController.ShutdownRequested"/> has been raised so this instance closes.
    /// </summary>
    RestartingElevated,

    /// <summary>
    /// Start is not possible now: launch recovery has not finished, a run is in progress, or the previous run has
    /// not yet confirmed the release of its memory, published its summaries and retired its journal writer.
    /// </summary>
    Busy,

    /// <summary>
    /// Less than 1 GiB of the available memory can be planned for testing. The notice reads
    /// <see cref="UiText.LowMemoryMessage"/>. The continuation of an accepted Start ends with it once the data folder
    /// has proved usable, before the run's results log or journal is created.
    /// </summary>
    InsufficientMemory,

    /// <summary>
    /// The run could not be journalled. <see cref="RunController.TryStart(TestType, bool)"/> returns it at once when
    /// the controller has no data folder; the continuation of an accepted Start ends with it when the data folder's
    /// write probe fails, the results log cannot reserve the run's name, the <c>begin</c> record is not durable
    /// within 5 s, or preparing the run fails unexpectedly. The notice reads
    /// <see cref="UiText.DataFolderUnusableMessage"/>, or <see cref="UiText.ResultsNotSaved(string)"/> when the
    /// journal of an earlier run whose publication failed still cannot be published, or its recovery fails.
    /// </summary>
    DataFolderUnusable,

    /// <summary>
    /// The engine could not describe the machine. The notice reads <see cref="UiText.EngineError(string)"/> with
    /// the engine's message. The continuation of an accepted Start ends with it once the data folder has proved
    /// usable, before the run's results log or journal is created.
    /// </summary>
    EngineUnavailable,

    /// <summary>
    /// An accepted Start was withdrawn by <see cref="RunController.RequestStop"/>,
    /// <see cref="RunController.ShutdownAsync"/> or <see cref="RunController.Dispose"/> before the run entered
    /// Preparing; the state is unchanged. Only the continuation of an accepted Start ends with it.
    /// </summary>
    Withdrawn,
}

/// <summary>
/// What the controller is doing after a run's verdict, until the next Start is possible.
/// </summary>
public enum PostRunActivity
{
    /// <summary>
    /// Nothing is pending: no run has ended, or the last run's memory release is confirmed, its summaries are
    /// published and its journal writer has retired.
    /// </summary>
    None,

    /// <summary>
    /// The verdict is shown and the engine is releasing the locked memory; the step field reads
    /// <see cref="UiText.ReleasingMemory"/>. Set with the terminal transition. It returns after a publication that
    /// stopped waiting for Released, while the notice reads <see cref="UiText.ReleaseNotConfirmedNotice"/>, and
    /// stays until Released arrives, or for good once the controller gives up on it at disposal or shutdown.
    /// </summary>
    ReleasingMemory,

    /// <summary>
    /// The memory is released, or publication stopped waiting for Released after the fallback, and the summaries
    /// are being published; the step field reads <see cref="UiText.SavingResults"/>. A run whose memory was never
    /// locked goes straight here from the terminal transition.
    /// </summary>
    SavingResults,
}

/// <summary>
/// Tunables of a <see cref="RunController"/>. Production uses the defaults; tests and the headless test host
/// substitute scaled plans, smaller regions, substitute journals and shorter bounds.
/// </summary>
public sealed class RunControllerOptions
{
    /// <summary>
    /// Gets the source of the plan run for a test type. Defaults to <see cref="TestPlans.Get(TestType)"/>.
    /// </summary>
    public Func<TestType, TestPlan> PlanProvider { get; init; } = TestPlans.Get;

    /// <summary>
    /// Gets the cap on the bytes requested from the engine, or <see langword="null"/> for none (the default).
    /// </summary>
    /// <remarks>
    /// Whether Start is allowed is still judged on the uncapped sizing rule. The capped size is rounded down to
    /// whole worker slices, and the engine's shrink-and-retry floor is lowered to it when it is below 1 GiB, so a
    /// scaled region such as 512 MiB stays a valid engine configuration.
    /// </remarks>
    public ulong? MaxRegionBytes { get; init; }

    /// <summary>
    /// Gets the factory of each run's progress journal. Defaults to
    /// <see cref="ProgressJournal.Create(DataDirectory, TimeProvider)"/>, the durable file journal.
    /// </summary>
    public Func<DataDirectory, TimeProvider, ProgressJournal> JournalFactory { get; init; } = ProgressJournal.Create;

    /// <summary>
    /// Gets how long after Completed the summaries wait for Released before they are published with
    /// <c>memory.releaseSeconds: null</c>. Defaults to 30 s, measured on the controller's
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public TimeSpan PublicationFallback { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long <see cref="RunController.ShutdownAsync"/> waits for Completed after requesting a stop before
    /// it finalises the run as ABORTED itself. Defaults to 3 s, measured on the controller's
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public TimeSpan ShutdownCompletedTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets how long <see cref="RunController.ShutdownAsync"/> waits for Released before it publishes the
    /// summaries with <c>memory.releaseSeconds: null</c>. Defaults to 10 s, measured on the controller's
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public TimeSpan ShutdownReleasedTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets how long Start waits for the journal's <c>begin</c> record to become durable before it is refused as
    /// an unusable data folder. Defaults to 5 s, measured on the controller's <see cref="TimeProvider"/>.
    /// </summary>
    public TimeSpan BeginTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks every option, so that a misconfigured controller fails where it is built.
    /// </summary>
    /// <exception cref="ArgumentException">A delegate is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A bound is negative and not <see cref="Timeout.InfiniteTimeSpan"/>, or <see cref="MaxRegionBytes"/> is 0.
    /// </exception>
    internal void Validate()
    {
        if (PlanProvider is null)
        {
            throw new ArgumentException("A plan provider is required.", nameof(PlanProvider));
        }

        if (JournalFactory is null)
        {
            throw new ArgumentException("A journal factory is required.", nameof(JournalFactory));
        }

        if (MaxRegionBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRegionBytes), MaxRegionBytes, "The region cap must be positive.");
        }

        ValidateBound(PublicationFallback, nameof(PublicationFallback));
        ValidateBound(ShutdownCompletedTimeout, nameof(ShutdownCompletedTimeout));
        ValidateBound(ShutdownReleasedTimeout, nameof(ShutdownReleasedTimeout));
        ValidateBound(BeginTimeout, nameof(BeginTimeout));
    }

    private static void ValidateBound(TimeSpan bound, string name)
    {
        if (bound < TimeSpan.Zero && bound != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(name, bound, "A bound must be non-negative or infinite.");
        }
    }
}

/// <summary>
/// Orchestrates one test run at a time end to end: checks elevation, the data folder and free memory, sizes the
/// region, journals the run, creates and drives the engine session, applies the thermal limits, drives the single
/// <see cref="RunStateMachine"/>, collects error detail, analyses it, and publishes the results; at launch it
/// recovers the previous run's journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Every public member except <see cref="HandleFatalException(Exception)"/> is called on the UI
/// thread, and every observable property changes there. Engine events arrive already posted to the UI
/// <see cref="SynchronizationContext"/> by the session, and sensor readings arrive on the context the application
/// started the <see cref="SensorMonitor"/> with. Journal, results and recovery I/O, analysis and session disposal run
/// on the journal's writer thread or the thread pool, never on the UI thread. Only the step gate, the progress sink
/// and the callback-fault sink run on the engine's coordinator thread, and they touch only thread-safe objects.
/// </para>
/// <para>
/// <b>Publication.</b> The verdict is shown on Completed and journalled as the acknowledged <c>result</c> record; the
/// summaries are published only after Released (or after giving up on it 30 s later), then the journal's <c>end</c>
/// record is written and the journal deleted. A new Start stays unavailable (<see cref="CanStart"/> is
/// <see langword="false"/>) until that sequence has finished, Released has arrived, the session has been disposed and
/// the journal's writer has retired. The 30 s fallback frees only the publication: until a late Released arrives the
/// session stays subscribed and undisposed, the step field reads <see cref="UiText.ReleasingMemory"/> and the notice
/// <see cref="UiText.ReleaseNotConfirmedNotice"/>, and once the controller gives up on Released, at disposal or
/// shutdown, Start stays unavailable.
/// </para>
/// <para>
/// <b>Lifetime.</b> The controller holds the only strong reference to the run's session, and nothing static or
/// timer-driven references a run while it is in progress, so an abandoned controller, its journal and its session
/// are collectable.
/// </para>
/// </remarks>
public sealed partial class RunController : ObservableObject, IDisposable
{
    /// <summary>
    /// The number of largest error groups <see cref="ErrorGroups"/> holds at most: the 100 groups the result panel
    /// lists.
    /// </summary>
    public const int MaxErrorGroups = 100;

    private const string AppErrorSource = "RunController";
    private const string FatalErrorSource = "UnhandledException";
    private const string EngineCallbackSourcePrefix = "EngineCallback:";
    private const string TemperatureNumberFormat = "0.0";

    private static readonly string _appVersion = ResolveAppVersion();

    // How long a fatal error waits for its app-errors.log write and the session's release: the bound Completed has
    // when the window closes. A hung engine, a slow release or a stalled disk is left to the process's termination,
    // with which Windows reclaims the locked pages, and the journal stays for the next launch.
    private static readonly TimeSpan _fatalTeardownBound = TimeSpan.FromSeconds(3);

    // Publication waits at most this long for the event log to drain, as long as the log's own close waits, so a
    // stalled disk never holds the results back.
    private static readonly TimeSpan _logFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly IEngine _engine;
    private readonly SensorMonitor _sensorMonitor;
    private readonly IMemoryInventorySource _memoryInventory;
    private readonly IElevationService _elevation;
    private readonly IUserPrompt _prompt;
    private readonly TimeProvider _timeProvider;
    private readonly DataDirectory? _dataDirectory;
    private readonly RunControllerOptions _options;
    private readonly BackgroundWork _backgroundWork = new();

    private RunProgress? _currentProgress;
    private SensorReadings _latestReadings;
    private ulong _totalErrors;
    private ErrorRecord? _firstError;
    private IReadOnlyList<ErrorGroup> _errorGroups = Array.Empty<ErrorGroup>();
    private bool _isErrorDetailTruncated;
    private bool _isStoredDetailCapReached;
    private CauseHints _causeHints = CauseHints.Empty;
    private string? _resultMessage;
    private string? _noticeMessage;
    private bool _isElevated;
    private MemoryInventory _inventory = MemoryInventory.Unknown;
    private SystemSnapshot? _system;
    private ulong? _testedBytes;
    private bool _isReducedInstructionSet;
    private TestPlan? _activePlan;
    private bool _canStart;
    private PostRunActivity _postRunActivity;

    // The run in progress or still publishing. Written on the UI thread; HandleFatalException reads it from any
    // thread, so it is published with Volatile.
    private RunContext? _run;
    private CancellationTokenSource? _pendingStart;
    private Task? _shutdownTask;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunController"/> class. Nothing is probed, prompted or recovered
    /// until <see cref="InitializeAsync"/> runs, and <see cref="CanStart"/> stays <see langword="false"/> until then.
    /// </summary>
    /// <param name="engine">The test engine.</param>
    /// <param name="stateMachine">The application's single run state machine; only this controller moves it.</param>
    /// <param name="sensorMonitor">
    /// The temperature monitor. The controller subscribes to its readings but never starts it; the application does.
    /// </param>
    /// <param name="memoryInventory">The source of the detected-memory summary.</param>
    /// <param name="elevation">Elevation detection and the elevated restart.</param>
    /// <param name="prompt">The dialog that offers the elevated restart.</param>
    /// <param name="timeProvider">
    /// The clock for timestamps and for every bound the controller applies itself: the <c>begin</c> wait, the
    /// publication fallback and the shutdown waits.
    /// </param>
    /// <param name="dataDirectory">
    /// The data folder for the journal and the results, or <see langword="null"/> when neither location is writable;
    /// every Start is then refused as an unusable data folder.
    /// </param>
    /// <param name="options">The tunables; the production defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    public RunController(
        IEngine engine,
        RunStateMachine stateMachine,
        SensorMonitor sensorMonitor,
        IMemoryInventorySource memoryInventory,
        IElevationService elevation,
        IUserPrompt prompt,
        TimeProvider timeProvider,
        DataDirectory? dataDirectory,
        RunControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(sensorMonitor);
        ArgumentNullException.ThrowIfNull(memoryInventory);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options ?? new RunControllerOptions();
        _options.Validate();

        _engine = engine;
        StateMachine = stateMachine;
        _sensorMonitor = sensorMonitor;
        _memoryInventory = memoryInventory;
        _elevation = elevation;
        _prompt = prompt;
        _timeProvider = timeProvider;
        _dataDirectory = dataDirectory;

        _latestReadings = sensorMonitor.Current;
        sensorMonitor.ReadingsChanged += OnReadingsChanged;
    }

    /// <summary>
    /// Raised on the UI thread when the user accepted the elevated restart and the elevated instance was started, so
    /// this instance must close.
    /// </summary>
    public event EventHandler? ShutdownRequested;

    /// <summary>
    /// Gets the application's single run state machine. Every component reads the run state from it; only this
    /// controller moves it.
    /// </summary>
    public RunStateMachine StateMachine { get; }

    /// <summary>
    /// Gets the latest progress snapshot of the current or last run, or <see langword="null"/> before its first
    /// progress report. On Completed it becomes a final snapshot carrying the run's verified bytes, even when no
    /// progress report arrived.
    /// </summary>
    /// <remarks>
    /// Raises <see cref="ObservableObject.PropertyChanged"/> on every assignment, even of a value-equal snapshot,
    /// because every tick must reach the live fields.
    /// </remarks>
    public RunProgress? CurrentProgress
    {
        get => _currentProgress;
        private set
        {
            OnPropertyChanging();
            _currentProgress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the latest CPU and RAM readings of the sensor monitor, posted once a second.
    /// </summary>
    /// <remarks>
    /// Raises <see cref="ObservableObject.PropertyChanged"/> on every assignment, even of a value-equal snapshot,
    /// because every sample must reach the live fields.
    /// </remarks>
    public SensorReadings LatestReadings
    {
        get => _latestReadings;
        private set
        {
            OnPropertyChanging();
            _latestReadings = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the true error total of the current or last run: the largest total the engine reported in progress,
    /// error batches and Completed. It keeps rising after the stored-detail cap is reached.
    /// </summary>
    public ulong TotalErrors
    {
        get => _totalErrors;
        private set => SetProperty(ref _totalErrors, value);
    }

    /// <summary>
    /// Gets the first stored error record of the current or last run, or <see langword="null"/> when it found none.
    /// </summary>
    public ErrorRecord? FirstError
    {
        get => _firstError;
        private set => SetProperty(ref _firstError, value);
    }

    /// <summary>
    /// Gets the largest groups of the current or last run's stored error records by 8-byte word address: at most
    /// <see cref="MaxErrorGroups"/>, largest first, in the order of <see cref="ErrorGrouping.Group"/>. A new list
    /// instance is assigned whenever the groups change, and the instance is kept while a batch leaves them unchanged.
    /// </summary>
    /// <remarks>
    /// The groups are derived from the stored records only, at most <see cref="ErrorGrouping.StoredDetailCap"/>, never
    /// from <see cref="TotalErrors"/>. Each error batch updates them in time proportional to its own size plus
    /// <see cref="MaxErrorGroups"/>, never to the number of records stored before it.
    /// </remarks>
    public IReadOnlyList<ErrorGroup> ErrorGroups
    {
        get => _errorGroups;
        private set => SetProperty(ref _errorGroups, value);
    }

    /// <summary>
    /// Gets a value indicating whether errors were dropped from the stored detail: the stored records reached
    /// <see cref="ErrorGrouping.StoredDetailCap"/> and <see cref="TotalErrors"/> exceeds their count. This is the
    /// condition the summary records as <c>errors.truncated</c>.
    /// </summary>
    /// <remarks>
    /// It stays <see langword="false"/> at exactly the cap with no further error, where
    /// <see cref="IsStoredDetailCapReached"/> is already <see langword="true"/>.
    /// </remarks>
    public bool IsErrorDetailTruncated
    {
        get => _isErrorDetailTruncated;
        private set => SetProperty(ref _isErrorDetailTruncated, value);
    }

    /// <summary>
    /// Gets a value indicating whether the stored records of the current or last run reached
    /// <see cref="ErrorGrouping.StoredDetailCap"/>, whatever <see cref="TotalErrors"/> is. From then on later errors
    /// are counted but not stored, so <see cref="ErrorGroups"/> are labelled as derived from the stored records only.
    /// </summary>
    public bool IsStoredDetailCapReached
    {
        get => _isStoredDetailCapReached;
        private set => SetProperty(ref _isStoredDetailCapReached, value);
    }

    /// <summary>
    /// Gets the ranked cause hints of the last run that ended with errors, always labelled as hints;
    /// <see cref="CauseHints.Empty"/> otherwise.
    /// </summary>
    public CauseHints CauseHints
    {
        get => _causeHints;
        private set => SetProperty(ref _causeHints, value);
    }

    /// <summary>
    /// Gets the reason shown with the verdict of the last run, or of the unfinished run recovered at launch, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    public string? ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    /// <summary>
    /// Gets the controller's notice: a refused Start, a recovery outcome or a failure to save results, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    public string? NoticeMessage
    {
        get => _noticeMessage;
        private set => SetProperty(ref _noticeMessage, value);
    }

    /// <summary>
    /// Gets a value indicating whether the process is elevated, as evaluated at initialisation and on every Start.
    /// </summary>
    public bool IsElevated
    {
        get => _isElevated;
        private set => SetProperty(ref _isElevated, value);
    }

    /// <summary>
    /// Gets the detected-memory summary read at initialisation; <see cref="MemoryInventory.Unknown"/> before it or
    /// when it could not be read.
    /// </summary>
    public MemoryInventory Inventory
    {
        get => _inventory;
        private set => SetProperty(ref _inventory, value);
    }

    /// <summary>
    /// Gets the engine's latest system snapshot, refreshed on every Start, or <see langword="null"/> when the engine
    /// could not describe the machine.
    /// </summary>
    public SystemSnapshot? System
    {
        get => _system;
        private set => SetProperty(ref _system, value);
    }

    /// <summary>
    /// Gets the bytes under test reported at Prepared, or <see langword="null"/> before it.
    /// </summary>
    public ulong? TestedBytes
    {
        get => _testedBytes;
        private set => SetProperty(ref _testedBytes, value);
    }

    /// <summary>
    /// Gets a value indicating whether the engine uses, or would use, the SSE2 kernels because AVX2 is unavailable.
    /// </summary>
    public bool IsReducedInstructionSet
    {
        get => _isReducedInstructionSet;
        private set => SetProperty(ref _isReducedInstructionSet, value);
    }

    /// <summary>
    /// Gets the plan of the current or last run, or <see langword="null"/> before the first run.
    /// </summary>
    public TestPlan? ActivePlan
    {
        get => _activePlan;
        private set => SetProperty(ref _activePlan, value);
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="TryStart(TestType, bool)"/> can be accepted: initialisation has
    /// finished, no Start is pending, no run is in progress, and the last run's session has reported Released, its
    /// summaries are published, its session is disposed and its journal writer has retired.
    /// </summary>
    /// <remarks>
    /// The publication fallback publishes the summaries without Released but never counts as it: until Released
    /// arrives, this stays <see langword="false"/> with the notice <see cref="UiText.ReleaseNotConfirmedNotice"/>.
    /// It stays <see langword="false"/> for good once the controller gives up on Released, at disposal or
    /// shutdown. A refused Start re-enables it only once that Start's journal writer has retired.
    /// </remarks>
    public bool CanStart
    {
        get => _canStart;
        private set => SetProperty(ref _canStart, value);
    }

    /// <summary>
    /// Gets what the controller is doing between a run's verdict and the next possible Start.
    /// </summary>
    public PostRunActivity PostRunActivity
    {
        get => _postRunActivity;
        private set => SetProperty(ref _postRunActivity, value);
    }

    /// <summary>
    /// Gets the asynchronous continuation of the last Start <see cref="TryStart(TestType, bool)"/> accepted, whose
    /// result is that Start's outcome: <see cref="StartResult.Accepted"/> once the run has entered Preparing, or
    /// <see cref="StartResult.DataFolderUnusable"/>, <see cref="StartResult.EngineUnavailable"/>,
    /// <see cref="StartResult.InsufficientMemory"/> or <see cref="StartResult.Withdrawn"/> when it ended with the
    /// state unchanged. A Start refused at once leaves it unchanged. Before the first accepted Start it is a completed
    /// task whose result is <see cref="StartResult.Accepted"/>, so a caller awaiting it never waits on a Start that
    /// was never made. Tests await it deterministically.
    /// </summary>
    /// <remarks>
    /// Holding it also keeps a refused Start's wait for its journal writer's retirement, and that journal, reachable
    /// from the controller: the writer holds the journal's retirement signal only weakly.
    /// </remarks>
    internal Task<StartResult> StartTask { get; private set; } = Task.FromResult(StartResult.Accepted);

    /// <summary>
    /// Gets the conclusion of the last run that ended: its verdict, cause hints and acknowledged <c>result</c> record,
    /// the first part of <see cref="PostRunTask"/>; tests await it. It never faults.
    /// </summary>
    internal Task ConclusionTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets the result, release-wait, publication, release-confirmation, disposal and journal-retirement pipeline of
    /// the last run that ended; tests await it.
    /// </summary>
    internal Task PostRunTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets a value indicating whether thread-pool work the controller handed off without awaiting it is still queued
    /// or running: the <c>app-errors.log</c> writes it queues, and the session, journal and results-log disposals that
    /// <see cref="Dispose"/>, <see cref="ShutdownAsync"/> and <see cref="HandleFatalException(Exception)"/> start.
    /// Tests wait for it to clear before deleting the data folder, which a late write would recreate.
    /// </summary>
    internal bool HasBackgroundWork => _backgroundWork.IsRunning;


    /// <summary>
    /// Prepares the controller once, after the window is shown: reads the system snapshot and the memory inventory,
    /// offers the elevated restart when the process is not elevated, recovers the previous run's journal, and then
    /// enables Start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The elevation offer comes before recovery: when the user accepts and the elevated instance starts,
    /// <see cref="ShutdownRequested"/> is raised and the method returns with recovery left to the elevated instance
    /// and <see cref="CanStart"/> left <see langword="false"/>.
    /// </para>
    /// <para>
    /// An unfinished journal moves the state Idle → Failed with the "did not finish" reason, shown without a dialog.
    /// A run whose verdict was journalled but whose summaries were missing stays Idle with the recovery notice. A
    /// journal that held damaged data shows <see cref="UiText.RecoveredDamagedNotice"/> instead. A failed probe or
    /// recovery is written to the application error log and does not stop initialisation; a failed recovery also
    /// shows <see cref="UiText.ResultsNotSaved(string)"/>.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when Start can be offered, or when this instance is about to close.</returns>
    /// <exception cref="InvalidOperationException">The method was already called.</exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            throw new InvalidOperationException("The run controller is initialised only once.");
        }

        _initialized = true;

        (SystemSnapshot? system, MemoryInventory inventory) = await Task.Run(ProbeMachine);
        System = system;
        Inventory = inventory;
        IsReducedInstructionSet = system?.BestIsa == InstructionSet.Sse2;

        IsElevated = _elevation.IsElevated;
        if (!IsElevated && OfferElevatedRestart())
        {
            return;
        }

        if (_dataDirectory is { } dataDirectory)
        {
            try
            {
                JournalRecovery recovery = await Task.Run(() => CrashDetector.Detect(dataDirectory, _timeProvider));
                ApplyLaunchRecovery(recovery);
            }
            catch (Exception exception)
            {
                // The previous run's results may be lost, so the user is told, not only the error log.
                ReportAppError(nameof(InitializeAsync), exception);
                NoticeMessage = UiText.ResultsNotSaved(RedactForDisplay(exception.Message));
            }
        }

        CanStart = !_disposed;
    }

    /// <summary>
    /// Requests a run of <paramref name="testType"/>. Returns at once and never blocks on I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Start validates elevation, then the data folder, then memory, then reserves the run's files, and every refusal
    /// leaves the run state unchanged. Only the checks that need no I/O run here, and each answers at once: a Start
    /// that is not possible now is <see cref="StartResult.Busy"/>; a non-elevated Start shows the elevation offer
    /// instead of starting; and a controller without a data folder refuses with
    /// <see cref="UiText.DataFolderUnusableMessage"/>.
    /// </para>
    /// <para>
    /// An accepted Start continues asynchronously, in order. Off the UI thread, the data folder is probed and a
    /// journal left by an earlier failed publication is published. Then the engine describes the machine and the
    /// region is sized, so an engine that cannot describe the machine and less than 1 GiB of plannable memory are
    /// refused only once the data folder has proved usable, and before any file of the run exists. Off the UI thread
    /// again, the results log reserves the run's base name, and the journal's <c>begin</c> record is made durable
    /// within 5 s. Only then does the run enter Preparing and the engine session start. A refusal on the way shows
    /// its notice and re-enables Start, with the state unchanged, once the refused run's journal writer, if it has
    /// one, has retired (<see cref="ProgressJournal.Retired"/>); the continuation's result, which tests read through
    /// <see cref="StartTask"/>, says which refusal it was.
    /// </para>
    /// </remarks>
    /// <param name="testType">The test type to run.</param>
    /// <param name="stopOnError">Whether the run stops at the first confirmed error.</param>
    /// <returns><see cref="StartResult.Accepted"/>, or why the Start was refused at once.</returns>
    /// <exception cref="InvalidOperationException">The plan provider returned no plan for the test type.</exception>
    public StartResult TryStart(TestType testType, bool stopOnError)
    {
        if (_disposed || _shutdownTask is not null || !CanStart || _pendingStart is not null
            || _run is not null || StateMachine.IsRunInProgress)
        {
            return StartResult.Busy;
        }

        IsElevated = _elevation.IsElevated;
        if (!IsElevated)
        {
            return OfferElevatedRestart() ? StartResult.RestartingElevated : StartResult.NotElevated;
        }

        if (_dataDirectory is not { } dataDirectory)
        {
            NoticeMessage = UiText.DataFolderUnusableMessage;
            return StartResult.DataFolderUnusable;
        }

        TestPlan plan = _options.PlanProvider(testType)
            ?? throw new InvalidOperationException($"The plan provider returned no plan for {testType}.");

        var selection = new StartSelection(
            testType,
            stopOnError,
            plan,
            plan.ToStepAttributes(),
            _timeProvider.GetUtcNow(),
            dataDirectory);

        CanStart = false;
        NoticeMessage = null;
        var pending = new CancellationTokenSource();
        _pendingStart = pending;
        StartTask = RunStartAsync(selection, pending);
        return StartResult.Accepted;
    }

    /// <summary>
    /// Requests that the current run stop with the user-stop reason. Never blocks.
    /// </summary>
    /// <remarks>
    /// While an accepted Start has not yet entered Preparing, for example while its <c>begin</c> record is being made
    /// durable, the Start is withdrawn with the state unchanged (<see cref="StartResult.Withdrawn"/>). In Preparing,
    /// including a step's journal wait, and in Running, the engine is asked to stop and the run ends ABORTED with
    /// "Stopped by user" within 2 s; if the engine refuses the stop, the run ends ABORTED at once with
    /// <see cref="UiText.EngineError(string)"/>, and the session's disposal stops the engine and releases the memory.
    /// Otherwise nothing happens.
    /// </remarks>
    public void RequestStop()
    {
        if (_pendingStart is { } pending)
        {
            pending.Cancel();
            return;
        }

        if (_run is { Concluded: false } run && StateMachine.IsRunInProgress)
        {
            RequestEngineStop(run, StopReason.User);
            AnswerStopFailure(run);
        }
    }


    /// <summary>
    /// Ends the controller's work when the main window closes. Never throws; later calls return the same work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pending Start is withdrawn. A run in progress is stopped with the user-stop reason, and Completed is awaited
    /// for at most <see cref="RunControllerOptions.ShutdownCompletedTimeout"/> (3 s); if it never arrives, the run is
    /// finalised as ABORTED from the controller's own totals, and a stop the engine refuses finalises it so at once,
    /// as an engine fault. Released is then awaited for at most
    /// <see cref="RunControllerOptions.ShutdownReleasedTimeout"/> (10 s) after the conclusion, and the summaries are
    /// published with <c>memory.releaseSeconds: null</c> if it has not arrived.
    /// </para>
    /// <para>
    /// The session's disposal, which releases the locked memory, is awaited only until that deadline. Once it has
    /// passed, a disposal not yet begun is started on the thread pool, the method returns without waiting for it,
    /// and the process may exit; Windows then reclaims the pages. Every bound is measured on the controller's
    /// <see cref="TimeProvider"/>. Start is refused from the moment this method is called.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the controller's work is finished or its deadline has passed.</returns>
    public Task ShutdownAsync()
    {
        _shutdownTask ??= ShutdownCoreAsync();
        return _shutdownTask;
    }

    /// <summary>
    /// Records an exception that is about to end the process and releases the run's locked memory. Thread-safe and
    /// never throws, but it blocks the calling thread for at most 3 s, so it must not be called on the UI thread: the
    /// application's unhandled-exception handlers hand it to the thread pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception is appended to <c>&lt;data&gt;\logs\app-errors.log</c> when a data folder exists, with the id of
    /// the run in progress, if any, and the session is disposed, which stops the engine, joins its threads and
    /// releases the memory. Both run on the thread pool, and the calling thread waits for them for at most 3 s: a hung
    /// engine, a slow release or a stalled disk is left to the process's termination, with which Windows reclaims the
    /// locked pages.
    /// </para>
    /// <para>
    /// The journal is left in place either way, so the next launch reports the run.
    /// </para>
    /// </remarks>
    /// <param name="exception">The unhandled exception; nothing is logged when it is <see langword="null"/>.</param>
    public void HandleFatalException(Exception exception)
    {
        DataDirectory? dataDirectory = _dataDirectory;
        Guid? runId = Volatile.Read(ref _run)?.Log.RunId;
        var pending = new List<Task>(2);
        if (dataDirectory is not null && exception is not null)
        {
            DateTimeOffset caughtUtc = _timeProvider.GetUtcNow();
            pending.Add(_backgroundWork.RunAsync(
                () => ResultsLog.AppendAppError(dataDirectory, FatalErrorSource, exception, caughtUtc, runId)));
        }

        if (Volatile.Read(ref _run)?.TakeSession() is { } session)
        {
            Unsubscribe(session);

            // The failure is written on the pool thread too, inside the bounded wait below, because the process ends
            // soon after this method returns and a queued write would be lost with it.
            pending.Add(DisposeOnThreadPoolAsync(session.Dispose, disposeFailure =>
            {
                if (dataDirectory is not null)
                {
                    ResultsLog.AppendAppError(
                        dataDirectory, FatalErrorSource, disposeFailure, _timeProvider.GetUtcNow(), runId);
                }
            }));
        }

        // The process ends soon after this method returns, so the release and the write get this bounded wait to finish
        // first. It blocks the calling thread for at most 3 s, which is why this must never run on the UI thread; the
        // application calls it from the thread pool. Neither task can fault, so the wait never throws; whatever is
        // still running when the bound passes ends with the process.
        _ = Task.WaitAll(pending.ToArray(), _fatalTeardownBound);
    }

    /// <summary>
    /// Releases everything the controller still holds: unsubscribes from the sensor monitor, withdraws a pending
    /// Start, and starts the disposal of the current session (releasing its memory), journal and results log.
    /// Idempotent, and never blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normally called at exit after <see cref="ShutdownAsync"/>, when nothing is left. Otherwise the session's
    /// disposal, which joins the engine's threads and releases the region, and the journal's and log's disposal run
    /// on the thread pool as independent work, so a hung engine never keeps the journal or the log open; a failure is
    /// written to the application error log.
    /// </para>
    /// <para>
    /// At exit past the shutdown deadline the process may end before that work finishes: Windows then reclaims the
    /// locked pages, and a journal without its <c>end</c> record stays on disk, so the next launch recovers the run.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sensorMonitor.ReadingsChanged -= OnReadingsChanged;
        _pendingStart?.Cancel();

        if (Volatile.Read(ref _run) is { } run)
        {
            run.ReleaseSignal.TrySetResult(null);

            // Unsubscribed below, the session can no longer report Released, so a teardown waiting for it ends.
            run.ReleaseConfirmed.TrySetResult(false);
            if (run.TakeSession() is { } session)
            {
                Unsubscribe(session);
                _ = DisposeOnThreadPoolAsync(session.Dispose, failure => ReportAppError(nameof(Dispose), failure));
            }

            _ = DisposeOnThreadPoolAsync(
                () =>
                {
                    run.Journal.Dispose();
                    run.Log.Dispose();
                },
                failure => ReportAppError(nameof(Dispose), failure));
        }
    }


    private static string ResolveAppVersion()
    {
        Assembly assembly = typeof(RunController).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? (assembly.GetName().Version ?? new Version(0, 0)).ToString()
            : informational;
    }

    // A journal that shows neither preparation nor a step of the plan is damaged, so the step is reported unknown
    // rather than guessed.
    private static string UnfinishedRunMessage(JournalRecovery recovery)
    {
        if (recovery.InPreparation)
        {
            return UiText.PreviousRunUnfinishedPreparing;
        }

        return recovery is { LastStepNumber: int number, StepCount: int count } && number >= 1 && number <= count
            ? UiText.PreviousRunUnfinished(number, count, recovery.LastStepName ?? string.Empty)
            : UiText.PreviousRunUnfinishedStepUnknown;
    }

    private async Task ShutdownCoreAsync()
    {
        try
        {
            DateTimeOffset shutdownStart = _timeProvider.GetUtcNow();
            if (_pendingStart is { } pending)
            {
                // A withdrawn Start ends by retiring its journal, so no begin-only journal is left behind.
                pending.Cancel();
                _ = await CompletesWithinAsync(StartTask, _options.ShutdownReleasedTimeout);
            }

            if (_run is { } run)
            {
                await StopRunForShutdownAsync(run, shutdownStart);
            }
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(ShutdownAsync), exception);
        }
        finally
        {
            _sensorMonitor.ReadingsChanged -= OnReadingsChanged;
        }
    }

    private async Task StopRunForShutdownAsync(RunContext run, DateTimeOffset shutdownStart)
    {
        if (!run.Concluded)
        {
            if (StateMachine.IsRunInProgress)
            {
                RequestEngineStop(run, StopReason.User);
                AnswerStopFailure(run);
            }

            bool completed = await CompletesWithinAsync(
                run.ConclusionStarted.Task,
                RemainingUntil(shutdownStart, _options.ShutdownCompletedTimeout));
            if (!completed && !run.Concluded)
            {
                BeginConclusion(
                    run,
                    ConclusionFromOwnTotals(run, RunEndReason.UserStop, endDetail: null),
                    releaseExpected: run.SessionStarted);
            }
        }

        await run.ConclusionDone.Task;
        if (run.ReleaseExpected
            && !await CompletesWithinAsync(
                run.ReleaseSignal.Task,
                RemainingUntil(run.ConcludedUtc, _options.ShutdownReleasedTimeout)))
        {
            run.ReleaseSignal.TrySetResult(null);
        }

        _ = await CompletesWithinAsync(run.PublicationDone.Task, _options.ShutdownReleasedTimeout);

        // Past the deadline the process may exit with the session still releasing; Windows reclaims the pages. A
        // pipeline still short of its teardown, or whose teardown still waits for Released, has not started the
        // release, so it is started here, on the thread pool and unawaited, and the pipeline's own teardown then finds
        // no session left.
        if (!await CompletesWithinAsync(run.Pipeline, RemainingUntil(run.ConcludedUtc, _options.ShutdownReleasedTimeout))
            && run.TakeSession() is { } session)
        {
            Unsubscribe(session);

            // The unsubscribed session can no longer report Released, so a teardown waiting for it ends, with Start
            // left unavailable.
            run.ReleaseConfirmed.TrySetResult(false);
            _ = DisposeOnThreadPoolAsync(session.Dispose, failure => ReportAppError(nameof(ShutdownAsync), failure));
        }
    }

    private (SystemSnapshot? Snapshot, MemoryInventory Inventory) ProbeMachine()
    {
        SystemSnapshot? snapshot = null;
        try
        {
            snapshot = _engine.QuerySystem();
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(InitializeAsync), exception);
        }

        MemoryInventory inventory = MemoryInventory.Unknown;
        try
        {
            inventory = _memoryInventory.Read() ?? MemoryInventory.Unknown;
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(InitializeAsync), exception);
        }

        return (snapshot, inventory);
    }

    private bool OfferElevatedRestart()
    {
        if (_prompt.Ask(UiText.ElevationPromptTitle, UiText.ElevationPromptMessage) != PromptResult.Yes
            || !_elevation.TryRestartElevated())
        {
            return false;
        }

        ShutdownRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplyLaunchRecovery(JournalRecovery recovery)
    {
        switch (recovery.Kind)
        {
            case JournalRecoveryKind.Unfinished:
                string message = UnfinishedRunMessage(recovery);
                ResultMessage = message;
                _ = StateMachine.TryTransition(RunState.Failed, RunEndReason.PreviousRunUnfinished, message);
                break;

            case JournalRecoveryKind.CompletedUnpublished:
                NoticeMessage = UiText.RecoveredNotice;
                break;

            default:
                break;
        }

        if (recovery.PublicationError is { } error)
        {
            NoticeMessage = UiText.ResultsNotSaved(RedactForDisplay(error));
        }
        else if (DamagedJournalNotice(recovery) is { } damagedNotice)
        {
            NoticeMessage = damagedNotice;
        }
    }

    // The notice of a recovery that published from a damaged journal, or null when the journal held no damage.
    private static string? DamagedJournalNotice(JournalRecovery recovery) =>
        recovery.JournalDamage is { } damage
            ? UiText.RecoveredDamagedNotice(
                RedactForDisplay(damage),
                recovery.DamagedJournalPath is { } keptPath ? RedactForDisplay(keptPath) : null)
            : null;

    private async Task<StartResult> RunStartAsync(StartSelection selection, CancellationTokenSource pending)
    {
        ResultsLog? log = null;
        ProgressJournal? journal = null;
        Task<JournalAckStatus>? beginTask = null;
        try
        {
            DataDirectory dataDirectory = selection.DataDirectory;
            DataFolderCheck check = await Task.Run(() => CheckDataFolder(dataDirectory));
            if (check.RecoveryNotice is { } recoveryNotice)
            {
                NoticeMessage = recoveryNotice;
            }

            if (check.RefusalNotice is { } folderRefusal)
            {
                return await RefuseStartAsync(
                    pending, StartResult.DataFolderUnusable, folderRefusal, journal, beginTask, log);
            }

            if (pending.IsCancellationRequested)
            {
                return await RefuseStartAsync(pending, StartResult.Withdrawn, notice: null, journal, beginTask, log);
            }

            // Memory is judged only once the data folder has proved usable, and before the results log reserves the
            // run's name, so an engine or memory refusal leaves no file behind. The query is quick and does no file
            // I/O, so it runs here, where System is published.
            SystemSnapshot snapshot;
            try
            {
                snapshot = _engine.QuerySystem();
            }
            catch (Exception exception)
            {
                ReportAppError(nameof(TryStart), exception);
                string engineError = UiText.EngineError(RedactForDisplay(exception.Message));
                return await RefuseStartAsync(
                    pending, StartResult.EngineUnavailable, engineError, journal, beginTask, log);
            }

            System = snapshot;
            IsReducedInstructionSet = snapshot.BestIsa == InstructionSet.Sse2;

            int workers = snapshot.LogicalProcessors;
            MemorySizingResult sizing = MemorySizing.Plan(snapshot.AvailablePhysicalBytes, workers);

            // Sufficiency is judged on the uncapped plan; the cap only scales the region a test host requests.
            ulong regionBytes = _options.MaxRegionBytes is { } cap
                ? MemorySizing.RoundDownToSlices(Math.Min(sizing.PlannedBytes, cap), workers)
                : sizing.PlannedBytes;
            if (!sizing.IsSufficient || regionBytes == 0)
            {
                return await RefuseStartAsync(
                    pending, StartResult.InsufficientMemory, UiText.LowMemoryMessage, journal, beginTask, log);
            }

            var request = new StartRequest(
                selection.TestType,
                selection.StopOnError,
                selection.Plan,
                selection.Steps,
                snapshot,
                Inventory,
                sizing.PlannedBytes,
                regionBytes,
                Math.Min(MemorySizing.MinimumRegionBytes, regionBytes),
                selection.StartRequestedUtc,
                dataDirectory);

            log = await Task.Run(() => ReserveRunLog(request));
            if (log is null)
            {
                return await RefuseStartAsync(
                    pending, StartResult.DataFolderUnusable, UiText.DataFolderUnusableMessage, journal, beginTask, log);
            }

            if (pending.IsCancellationRequested)
            {
                return await RefuseStartAsync(pending, StartResult.Withdrawn, notice: null, journal, beginTask, log);
            }

            journal = _options.JournalFactory(request.DataDirectory, _timeProvider);
            JournalBegin begin = JournalBegin.Create(
                log.RunId,
                log.BaseName,
                request.TestType.ToString(),
                request.StopOnError,
                request.StartRequestedUtc,
                _appVersion,
                request.Steps,
                request.Snapshot,
                RuntimeInformation.OSDescription,
                ToJournalMemory(request.Inventory),
                request.Snapshot.AvailablePhysicalBytes,
                request.PlannedBytes);

            beginTask = journal.BeginAsync(begin);
            JournalAckStatus status = await WaitForBeginAsync(beginTask, pending.Token);
            if (pending.IsCancellationRequested)
            {
                return await RefuseStartAsync(pending, StartResult.Withdrawn, notice: null, journal, beginTask, log);
            }

            if (status != JournalAckStatus.Acknowledged)
            {
                return await RefuseStartAsync(
                    pending, StartResult.DataFolderUnusable, UiText.DataFolderUnusableMessage, journal, beginTask, log);
            }

            BeginRun(request, pending, journal, log, begin);
            return StartResult.Accepted;
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
            if (_run is { } run && ReferenceEquals(run.Journal, journal))
            {
                // The run had entered Preparing: end it like an engine fault so the state machine never stays there.
                if (!run.Concluded)
                {
                    BeginConclusion(run, StartFaultConclusion(exception), releaseExpected: run.SessionStarted);
                }

                return StartResult.Accepted;
            }

            return await RefuseStartAsync(
                pending, StartResult.DataFolderUnusable, UiText.DataFolderUnusableMessage, journal, beginTask, log);
        }
    }

    // Runs on the thread pool: the write probe and the recovery of a kept journal both do file I/O.
    private DataFolderCheck CheckDataFolder(DataDirectory dataDirectory)
    {
        if (!dataDirectory.CanWrite())
        {
            return new DataFolderCheck(UiText.DataFolderUnusableMessage, null);
        }

        // A journal still on disk holds a run whose summaries could not be published. The new run would append to
        // that file and delete it with its own end record, losing the earlier run's records, so publication is
        // retried first, and Start is refused while it still fails.
        if (File.Exists(dataDirectory.JournalPath))
        {
            JournalRecovery recovery;
            try
            {
                recovery = CrashDetector.Detect(dataDirectory, _timeProvider);
            }
            catch (Exception exception)
            {
                // A recovery that fails is a failure to save the earlier run's results, not an unusable folder: the
                // folder has just proved writable.
                ReportAppError(nameof(TryStart), exception);
                return new DataFolderCheck(UiText.ResultsNotSaved(RedactForDisplay(exception.Message)), null);
            }

            if (recovery.PublicationError is { } error)
            {
                return new DataFolderCheck(UiText.ResultsNotSaved(RedactForDisplay(error)), null);
            }

            if (DamagedJournalNotice(recovery) is { } damagedNotice)
            {
                return new DataFolderCheck(null, damagedNotice);
            }

            if (recovery.PublishedFiles.Count > 0)
            {
                return new DataFolderCheck(null, UiText.RecoveredNotice);
            }
        }

        return new DataFolderCheck(null, null);
    }

    // Runs on the thread pool: the reservation creates the run's .log.
    private ResultsLog? ReserveRunLog(StartRequest request) =>
        ResultsLog.OpenRun(
            request.DataDirectory,
            request.TestType.ToString(),
            request.StartRequestedUtc,
            _timeProvider);

    private async Task<JournalAckStatus> WaitForBeginAsync(Task<JournalAckStatus> beginTask, CancellationToken cancellation)
    {
        try
        {
            return await beginTask.WaitAsync(_options.BeginTimeout, _timeProvider, cancellation);
        }
        catch (TimeoutException)
        {
            return JournalAckStatus.TimedOut;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return JournalAckStatus.Stopped;
        }
    }

    // Leaves the state unchanged and re-enables Start only once the journal's writer has retired, so the next run's
    // journal can never be deleted by this one's late begin cleanup or end record. Returns refusal, the outcome the
    // refused Start ends with.
    private async Task<StartResult> RefuseStartAsync(
        CancellationTokenSource pending,
        StartResult refusal,
        string? notice,
        ProgressJournal? journal,
        Task<JournalAckStatus>? beginTask,
        ResultsLog? log)
    {
        if (notice is not null)
        {
            NoticeMessage = notice;
        }

        // The reserved log first: the journal's writer may take arbitrarily long to retire, and the log would stay open
        // and on disk for that long. Its own try keeps a failure there from skipping the journal's retirement.
        try
        {
            if (log is not null)
            {
                await Task.Run(() => DiscardReservedLog(log));
            }
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
        }

        try
        {
            if (journal is not null)
            {
                await RetireUnstartedJournalAsync(journal, beginTask);
            }
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
        }
        finally
        {
            ClearPendingStart(pending);
            CanStart = !_disposed;
        }

        return refusal;
    }

    private static async Task RetireUnstartedJournalAsync(ProgressJournal journal, Task<JournalAckStatus>? beginTask)
    {
        // The begin wait is bounded by the journal itself. A begin that became durable after Start gave up on it
        // is ended at once; otherwise the writer deletes the begin-only journal by itself.
        if (beginTask is not null && await beginTask == JournalAckStatus.Acknowledged)
        {
            _ = await journal.EndAsync();
        }

        await Task.Run(journal.Dispose);

        // Dispose waits only 1 s for the writer. One still stalled in its begin or end append deletes the journal
        // file once the append returns, and that file is the next run's journal too. The writer holds the retirement
        // signal only weakly, so the journal this method holds, reachable through StartTask, keeps it alive for the wait.
        await journal.Retired;
    }

    private void DiscardReservedLog(ResultsLog log)
    {
        log.Dispose();
        try
        {
            // The reserved .log holds no event yet; removing it keeps a refused Start from leaving a run-less log.
            File.Delete(log.LogPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportAppError(nameof(TryStart), exception);
        }
    }

    private void ClearPendingStart(CancellationTokenSource pending)
    {
        if (ReferenceEquals(_pendingStart, pending))
        {
            _pendingStart = null;
        }

        pending.Dispose();
    }


    private void BeginRun(
        StartRequest request,
        CancellationTokenSource pending,
        ProgressJournal journal,
        ResultsLog log,
        JournalBegin begin)
    {
        ClearPendingStart(pending);
        var run = new RunContext(request, journal, log, begin, _timeProvider, _backgroundWork);
        Volatile.Write(ref _run, run);
        log.LogRunStarting(begin);

        if (!StateMachine.TryTransition(RunState.Preparing))
        {
            throw new InvalidOperationException($"A run cannot enter Preparing from {StateMachine.State}.");
        }

        CurrentProgress = null;
        TotalErrors = 0;
        FirstError = null;
        ErrorGroups = Array.Empty<ErrorGroup>();
        IsErrorDetailTruncated = false;
        IsStoredDetailCapReached = false;
        CauseHints = CauseHints.Empty;
        ResultMessage = null;
        TestedBytes = null;
        ActivePlan = request.Plan;
        PostRunActivity = PostRunActivity.None;

        ObserveReadings(run, _sensorMonitor.Current, evaluateThermal: false);
        StartSession(run);
    }

    private void StartSession(RunContext run)
    {
        StartRequest request = run.Request;

        // Read in the same synchronous frame as CreateSession, so it is the very context the session posts its
        // events to, and the step gate's barrier queues behind them.
        SynchronizationContext? eventContext = SynchronizationContext.Current;
        var options = new EngineSessionOptions
        {
            RegionBytes = request.RegionBytes,
            MinRegionBytes = request.MinRegionBytes,
            WorkerCount = request.Snapshot.LogicalProcessors,
            Steps = request.Steps,
            StopOnError = request.StopOnError,
            Seed = SeedFrom(run.Begin.RunId),
            StepGate = (step, stopEvent) => GateStep(run, eventContext, step, stopEvent),
            ProgressSink = run.Journal.PublishProgress,
            CallbackFault = (callbackName, exception) => RecordCallbackFault(run, callbackName, exception),
        };

        IEngineSession session;
        try
        {
            session = _engine.CreateSession(options);
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
            BeginConclusion(run, StartFaultConclusion(exception), releaseExpected: false);
            return;
        }

        // Attached before Start: no callback can run earlier, and every callback finds its session.
        run.AttachSession(session);
        Subscribe(session);
        try
        {
            session.Start();
            run.SessionStarted = true;
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
            BeginConclusion(run, StartFaultConclusion(exception), releaseExpected: false);
        }
    }

    // Runs on the engine coordinator before each step's memory traffic. It touches only the journal, which is
    // thread-safe, the run's atomically published fields, and the UI context, to which it first posts a barrier: the
    // prepared and stepEnd records of the events raised before this step are journalled by their UI-thread handlers,
    // and the step record must follow them, or a crash in the step's first moments would lose the previous step.
    private static StepGateDecision GateStep(
        RunContext run,
        SynchronizationContext? eventContext,
        EngineStepStarted step,
        WaitHandle stopEvent)
    {
        // One real-time budget covers the barrier and the acknowledgment, so a stalled UI thread and a stalled disk
        // together still hold the engine no longer than the journal's bound.
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            // Without a captured context the session delivers its events in order through drain work items of its own
            // on the thread pool. A barrier posted there would run beside them rather than queue behind them, so its
            // completion could not show that the earlier events' handlers have run.
            if (eventContext is not null)
            {
                switch (WaitForEarlierEvents(eventContext, stopEvent, ProgressJournal.AcknowledgmentTimeout))
                {
                    case EventBarrierOutcome.Stopped:
                        // As for a stop during the acknowledgment wait: the engine ends with the recorded reason.
                        return StepGateDecision.Abort;
                    case EventBarrierOutcome.TimedOut:
                        return FailStep(run, UiText.JournalAcknowledgmentTimedOut);
                }
            }

            JournalAck ack = run.Journal.RecordStepStarting(step.Index, step.Count, step.Name);
            TimeSpan remaining = ProgressJournal.AcknowledgmentTimeout - Stopwatch.GetElapsedTime(startedAt);
            JournalAckStatus status = ack.Wait(stopEvent, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            if (status == JournalAckStatus.Acknowledged)
            {
                // The step's traffic starts once Continue is returned, so a run the controller has to conclude itself
                // still lists this step when its StepFinished never arrives.
                run.RecordStepStarted(step);
                return StepGateDecision.Continue;
            }

            return status switch
            {
                // The stop event is set only after the stop reason is recorded, so the engine ends with that reason.
                JournalAckStatus.Stopped => StepGateDecision.Abort,
                JournalAckStatus.TimedOut => FailStep(run, UiText.JournalAcknowledgmentTimedOut),
                _ => FailStep(run, ack.Failure?.Message ?? status.ToString()),
            };
        }
        catch (Exception exception)
        {
            return FailStep(run, exception.Message);
        }
    }

    // The UI context runs its posts in order, so once the barrier has run, every handler of an event posted before it
    // has offered its record to the journal. The barrier holds only its kernel event, never a run, journal or session,
    // so a barrier still queued when its run is abandoned keeps none of them alive. The event is this wait's own and is
    // disposed as the wait returns, so no kernel object outlives the wait; a completion task's wait handle would stay
    // open until finalization, one more per step for as long as no garbage collection runs.
    private static EventBarrierOutcome WaitForEarlierEvents(
        SynchronizationContext eventContext,
        WaitHandle stopEvent,
        TimeSpan timeout)
    {
        using var barrier = new EventBarrier();
        eventContext.Post(static state => ((EventBarrier)state!).Deliver(), barrier);
        try
        {
            int signalled = WaitHandle.WaitAny(new[] { barrier.Signal, stopEvent }, timeout);
            if (signalled == 0 || barrier.IsDelivered)
            {
                return EventBarrierOutcome.Delivered;
            }

            return signalled == WaitHandle.WaitTimeout ? EventBarrierOutcome.TimedOut : EventBarrierOutcome.Stopped;
        }
        catch (ObjectDisposedException)
        {
            // Only a session being destroyed disposes its stop event, so the wait ends as a stop, as the journal
            // acknowledgment's own wait ends.
            return barrier.IsDelivered ? EventBarrierOutcome.Delivered : EventBarrierOutcome.Stopped;
        }
    }

    // The host-fault stop is recorded before Abort is returned, so the aborted step starts no traffic and the run
    // ends HostFault rather than with an unexplained gate abort. The failure is logged as it happens, ahead of its
    // stop; both lines are only enqueued, so the coordinator performs no I/O.
    private static StepGateDecision FailStep(RunContext run, string failure)
    {
        run.TryRecordJournalFailure(failure);
        LogJournalFailureOnce(run);
        RequestEngineStop(run, StopReason.HostFault);
        return StepGateDecision.Abort;
    }

    // Runs on the engine coordinator, the UI thread, or the pool thread disposing the session, which reports a stop the
    // engine refused there; non-blocking: the app-error write goes to the thread pool and the .log lines are only
    // enqueued. CALLBACK_FAULT records the cause of the host-fault stop that follows it.
    private static void RecordCallbackFault(RunContext run, string callbackName, Exception exception)
    {
        string source = EngineCallbackSourcePrefix + callbackName;
        DateTimeOffset caughtUtc = run.TimeProvider.GetUtcNow();
        run.BackgroundWork.QueueAppError(run.Request.DataDirectory, source, exception, caughtUtc, run.Log.RunId);
        run.Log.LogCallbackFault(caughtUtc, source, exception);
        run.TryRecordCallbackFault(exception.Message);
        RequestEngineStop(run, StopReason.HostFault);
    }

    private static ulong SeedFrom(Guid runId)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = runId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }

    private void Subscribe(IEngineSession session)
    {
        session.Prepared += OnSessionPrepared;
        session.StepStarted += OnSessionStepStarted;
        session.StepFinished += OnSessionStepFinished;
        session.ProgressChanged += OnSessionProgressChanged;
        session.ErrorsDetected += OnSessionErrorsDetected;
        session.Completed += OnSessionCompleted;
        session.Released += OnSessionReleased;
    }

    private void Unsubscribe(IEngineSession session)
    {
        session.Prepared -= OnSessionPrepared;
        session.StepStarted -= OnSessionStepStarted;
        session.StepFinished -= OnSessionStepFinished;
        session.ProgressChanged -= OnSessionProgressChanged;
        session.ErrorsDetected -= OnSessionErrorsDetected;
        session.Completed -= OnSessionCompleted;
        session.Released -= OnSessionReleased;
    }

    // Events of a session that is no longer the current run's are ignored.
    private RunContext? RunFor(object? sender) =>
        _run is { } run && sender is not null && ReferenceEquals(sender, run.Session) ? run : null;

    private void OnSessionPrepared(object? sender, EnginePrepared e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            var prepared = new JournalPrepared
            {
                TestedBytes = e.TestedBytes,
                Workers = e.WorkerCount,
                PageMode = e.PageMode,
                IsaSelected = e.InstructionSet,
                AddressKind = e.AddressKind,
                PreparationMs = WholeMilliseconds(e.PrepareTime),
                Utc = _timeProvider.GetUtcNow(),
            };
            run.Prepared = prepared;
            run.WorkerCount = e.WorkerCount;
            run.Journal.RecordPrepared(prepared);
            run.Log.LogPrepared(prepared);

            TestedBytes = e.TestedBytes;
            IsReducedInstructionSet = e.IsReducedInstructionSet;
            _ = StateMachine.TryTransition(RunState.Running);
            AnswerStopFailure(run);
        }
        catch (Exception exception)
        {
            OnHandlerFault(run, nameof(OnSessionPrepared), exception);
        }
    }

    private void OnSessionStepStarted(object? sender, EngineStepStarted e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            run.Log.LogStepStarted(e.Index, e.Count, e.Name);
            AnswerStopFailure(run);
        }
        catch (Exception exception)
        {
            OnHandlerFault(run, nameof(OnSessionStepStarted), exception);
        }
    }

    private void OnSessionStepFinished(object? sender, EngineStepFinished e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            run.Log.LogStepFinished(e.Outcome, run.Request.Steps.Count);
            run.Journal.RecordStepFinished(e.Outcome);
            run.RecordOutcome(e.Outcome);
            AnswerStopFailure(run);
        }
        catch (Exception exception)
        {
            OnHandlerFault(run, nameof(OnSessionStepFinished), exception);
        }
    }

    private void OnSessionProgressChanged(object? sender, RunProgress e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            CurrentProgress = e;
            if (e.TotalErrors > TotalErrors)
            {
                TotalErrors = e.TotalErrors;
            }

            UpdateTruncation(run);
            run.Log.LogProgress(e);
            AnswerJournalFailure(run);
            if (!run.LogFailureReported)
            {
                ReportLogFailure(run);
            }

            AnswerStopFailure(run);
        }
        catch (Exception exception)
        {
            OnHandlerFault(run, nameof(OnSessionProgressChanged), exception);
        }
    }

    private void OnSessionErrorsDetected(object? sender, EngineErrorsDetected e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            int room = ErrorGrouping.StoredDetailCap - run.Stored.Count;
            if (room > 0 && e.Records.Count > 0)
            {
                ErrorRecord[] accepted = e.Records.Take(room).ToArray();
                run.Stored.AddRange(accepted);
                FirstError ??= run.Stored[0];
                ErrorGroups = run.Groups.Add(accepted);
                run.Log.LogErrors(accepted, Math.Max(TotalErrors, e.TotalErrors));
            }

            // With Stop on error on, the engine stopped itself at the first confirmed error, before reporting it; the
            // record's test time is the decision's, on the engine clock that COMPLETED elapsed_ms also uses. Once a
            // stop line exists, only Completed shows whether this error ended the run, and ConcludeAsync logs it then.
            if (run.Request.StopOnError && e.Records.Count > 0 && run.TryClaimFirstErrorStopOnReport())
            {
                run.Log.LogStopRequested(
                    _timeProvider.GetUtcNow(), nameof(RunEndReason.FirstError), e.Records[0].TimeIntoTest);
            }

            if (e.TotalErrors > TotalErrors)
            {
                TotalErrors = e.TotalErrors;
            }

            UpdateTruncation(run);
            AnswerStopFailure(run);
        }
        catch (Exception exception)
        {
            OnHandlerFault(run, nameof(OnSessionErrorsDetected), exception);
        }
    }

    private void OnSessionCompleted(object? sender, EngineCompleted e)
    {
        if (RunFor(sender) is not { Concluded: false } run)
        {
            return;
        }

        try
        {
            var conclusion = new Conclusion(
                e.EndReason,
                e.TotalErrors,
                e.VerifiedBytes,
                e.Elapsed,
                e.FirstErrorTime,
                e.StepsCompleted,
                e.StepResults,
                e.OsError,
                e.EndDetail,
                _timeProvider.GetUtcNow());
            BeginConclusion(run, conclusion, releaseExpected: true);
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(OnSessionCompleted), exception);
        }
    }

    private void OnSessionReleased(object? sender, EngineReleased e)
    {
        if (RunFor(sender) is { } run)
        {
            run.ReleaseSignal.TrySetResult(new ReleaseNotice(e, _timeProvider.GetUtcNow()));

            // Also after the publication fallback: the next Start waits for this confirmation, not for the fallback.
            run.ReleaseConfirmed.TrySetResult(true);
        }
    }

    // An exception in a handler must never reach the dispatcher mid-run: it is logged and ends the run HostFault, and
    // the run's .log records it as that stop's cause.
    private void OnHandlerFault(RunContext run, string handlerName, Exception exception)
    {
        DateTimeOffset caughtUtc = _timeProvider.GetUtcNow();
        ReportAppError(handlerName, exception, caughtUtc);
        run.Log.LogCallbackFault(caughtUtc, AppErrorSourceOf(handlerName), exception);
        run.TryRecordCallbackFault(exception.Message);
        if (!run.Concluded && StateMachine.IsRunInProgress)
        {
            RequestEngineStop(run, StopReason.HostFault);
            AnswerStopFailure(run);
        }
    }

    private void AnswerJournalFailure(RunContext run)
    {
        if (run.JournalFailureAnswered || run.Journal.Failure is not { } failure)
        {
            return;
        }

        run.JournalFailureAnswered = true;
        run.TryRecordJournalFailure(failure.Message);
        LogJournalFailureOnce(run);
        RequestEngineStop(run, StopReason.HostFault);
    }

    // The event log's drain records its first write failure and carries on, so the run continues without the log, as
    // designed. Reading that record performs no I/O on the UI thread. The exception goes to app-errors.log once, with
    // its full text, and the notice says what the failure leaves of the results: before publication, that the event
    // log is not being written; after it, whether the results were saved without a complete event log or not at all.
    private void ReportLogFailure(RunContext run)
    {
        if (run.Log.WriteFailure is not { } failure)
        {
            return;
        }

        if (!run.LogFailureReported)
        {
            run.LogFailureReported = true;
            ReportAppError(nameof(ResultsLog), failure);
        }

        string failureText = RedactForDisplay(failure.Message);
        NoticeMessage = run.PublicationOutcome switch
        {
            null => UiText.EventLogNotSaved(failureText),
            { Succeeded: true } => UiText.ResultsSavedWithoutEventLog(failureText),
            { Error: var error } => UiText.ResultsNotSaved(RedactForDisplay(error ?? string.Empty))
                + UiText.NoticeSeparator
                + UiText.EventLogNotSaved(failureText),
        };
    }

    // Called from the coordinator (a failed step gate) and the UI thread (a failure seen on Progress); the first
    // recorded failure is logged once, when it is recorded.
    private static void LogJournalFailureOnce(RunContext run)
    {
        if (run.JournalFailureText is { } failure && run.TryClaimJournalFailureLog())
        {
            run.Log.LogJournalFailure(failure);
        }
    }

    // Every stop the controller asks for, from the UI or the coordinator thread: the engine records the reason before
    // this returns, and the controller's first request gets its STOP_REQUESTED line, stamped at the request, unless a
    // reported first-error stop was logged before it; later requests, which the engine ignores, add none. Logging
    // only enqueues, so the coordinator performs no I/O. A stop the engine refuses recorded no reason, so it gets no
    // line: it is kept as the run's engine fault, which AnswerStopFailure concludes the run with.
    private static void RequestEngineStop(RunContext run, StopReason reason)
    {
        bool claimed;
        try
        {
            claimed = run.RequestStop(reason);
        }
        catch (EngineException exception)
        {
            RecordStopFailure(run, exception);
            return;
        }

        if (claimed)
        {
            string reasonName = reason switch
            {
                StopReason.User => nameof(RunEndReason.UserStop),
                StopReason.Thermal => nameof(RunEndReason.Thermal),
                _ => nameof(RunEndReason.HostFault),
            };
            run.Log.LogStopRequested(run.TimeProvider.GetUtcNow(), reasonName);
        }
    }

    // Runs on the coordinator or the UI thread, so it only records and enqueues: the first refusal is kept for
    // AnswerStopFailure, and its app-errors.log entry is written on the thread pool. A later refusal is the same
    // engine fault and adds nothing.
    private static void RecordStopFailure(RunContext run, EngineException exception)
    {
        if (run.TryRecordStopFailure(exception))
        {
            run.BackgroundWork.QueueAppError(
                run.Request.DataDirectory,
                AppErrorSourceOf(nameof(RequestEngineStop)),
                exception,
                run.TimeProvider.GetUtcNow(),
                run.Log.RunId);
        }
    }

    // A stop the engine refused leaves it running with no reason recorded, so no Completed will answer the request:
    // the run is concluded here, as the engine fault the refusal is, from the controller's own totals. The teardown's
    // disposal then has rq_session_destroy stop the engine, join its threads and release the memory, and Start stays
    // unavailable until it has returned; a disposed session raises no Released, so none is awaited. Runs on the UI
    // thread, at once after a request made there and on the next session event after one the coordinator made. A
    // Completed that arrives first concludes the run with the engine's own outcome instead.
    private void AnswerStopFailure(RunContext run)
    {
        if (run.Concluded || !StateMachine.IsRunInProgress || run.StopFailure is not { } failure)
        {
            return;
        }

        BeginConclusion(
            run,
            ConclusionFromOwnTotals(run, RunEndReason.EngineFault, failure.Message),
            releaseExpected: false);
    }

    private void UpdateTruncation(RunContext run)
    {
        IsStoredDetailCapReached = run.Stored.Count >= ErrorGrouping.StoredDetailCap;
        IsErrorDetailTruncated = IsStoredDetailCapReached && TotalErrors > (ulong)run.Stored.Count;
    }

    private void OnReadingsChanged(object? sender, SensorReadings readings)
    {
        RunContext? run = _run;
        try
        {
            LatestReadings = readings;
            if (run is { Concluded: false } && StateMachine.IsRunInProgress)
            {
                ObserveReadings(run, readings, evaluateThermal: StateMachine.State == RunState.Running);
                AnswerStopFailure(run);
            }
        }
        catch (Exception exception)
        {
            if (run is not null)
            {
                OnHandlerFault(run, nameof(OnReadingsChanged), exception);
            }
            else
            {
                ReportAppError(nameof(OnReadingsChanged), exception);
            }
        }
    }

    // Thermal limits act only on readings the monitor still reports Available; it re-marks readings older than 3 s
    // as Stale, so a stale reading never stops a run.
    private void ObserveReadings(RunContext run, SensorReadings readings, bool evaluateThermal)
    {
        double? cpu = FreshCelsius(readings.Cpu);
        double? ram = FreshCelsius(readings.Ram);
        string cpuStatus = readings.Cpu.Status.ToString();
        string ramStatus = readings.Ram.Status.ToString();
        run.Journal.PublishReadings(new JournalReadings(cpu, cpuStatus, ram, ramStatus));
        run.Temperatures = run.Temperatures.Merge(cpu, cpuStatus, ram, ramStatus);
        LogSensorChange(run, ThermalSensor.Cpu, readings.Cpu);
        LogSensorChange(run, ThermalSensor.Ram, readings.Ram);

        if (!evaluateThermal || run.ThermalVerdict is not null || ThermalLimits.Evaluate(readings) is not { } verdict)
        {
            return;
        }

        run.ThermalVerdict = verdict;
        SensorValue value = verdict.Sensor == ThermalSensor.Cpu ? readings.Cpu : readings.Ram;
        run.Log.LogThermalStop(SensorName(verdict.Sensor), verdict.Celsius, verdict.LimitCelsius, value.CapturedAt);
        RequestEngineStop(run, StopReason.Thermal);
    }

    private static void LogSensorChange(RunContext run, ThermalSensor sensor, SensorValue value)
    {
        SensorStatus? previous = sensor == ThermalSensor.Cpu ? run.LastCpuStatus : run.LastRamStatus;
        if (previous == value.Status)
        {
            return;
        }

        if (sensor == ThermalSensor.Cpu)
        {
            run.LastCpuStatus = value.Status;
        }
        else
        {
            run.LastRamStatus = value.Status;
        }

        string message = FreshCelsius(value) is { } celsius
            ? string.Format(
                CultureInfo.InvariantCulture,
                UiText.TemperatureFormat,
                celsius.ToString(TemperatureNumberFormat, CultureInfo.InvariantCulture))
            : sensor == ThermalSensor.Cpu ? UiText.CpuSensorUnavailableNotice : UiText.RamSensorUnavailableNotice;
        run.Log.LogSensorEvent(SensorName(sensor), value.Status.ToString(), message);
    }

    private static double? FreshCelsius(SensorValue value) =>
        value is { Status: SensorStatus.Available, Celsius: double celsius } && double.IsFinite(celsius)
            ? celsius
            : null;

    private static string SensorName(ThermalSensor sensor) => sensor.ToString().ToUpperInvariant();

    private static long WholeMilliseconds(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : duration.Ticks / TimeSpan.TicksPerMillisecond;


    private static JournalMemoryInfo ToJournalMemory(MemoryInventory inventory) => new()
    {
        InstalledBytes = inventory.InstalledBytes,
        Type = inventory.MemoryType,
        SpeedMTs = inventory.SpeedMTs,
        Channels = inventory.ChannelCount,
        Dimms = inventory.DimmCount,
    };

    private static IReadOnlyList<StepOutcome> DistinctSteps(IEnumerable<StepOutcome> outcomes) =>
        outcomes
            .GroupBy(outcome => outcome.Attributes.Index)
            .Select(group => group.Last())
            .OrderBy(outcome => outcome.Attributes.Index)
            .ToArray();

    // The summary names each category by its display title, the form SummaryCauseHint.Category documents and the
    // text summary prints after "Hint:".
    private static SummaryCauseHints ToSummaryCauseHints(CauseHints hints) =>
        hints.IsEmpty
            ? SummaryCauseHints.Empty
            : new SummaryCauseHints
            {
                Items = hints.Items
                    .Select(hint => new SummaryCauseHint
                    {
                        Rank = hint.Rank,
                        Category = Suggestions.Title(hint.Category),
                        LikelihoodPercent = hint.LikelihoodPercent,
                        Suggestion = hint.Suggestion,
                        Evidence = hint.Evidence,
                    })
                    .ToArray(),
            };

    private Conclusion StartFaultConclusion(Exception exception) => new(
        RunEndReason.EngineFault,
        TotalErrors,
        0,
        TimeSpan.Zero,
        null,
        0,
        Array.Empty<StepOutcome>(),
        0,
        exception.Message,
        _timeProvider.GetUtcNow());

    // Used only when no Completed will settle the run: past the shutdown bound it ends as a user stop, and after a stop
    // the engine refused as an engine fault, ABORTED either way with what the controller itself observed. No step
    // result arrives with that end, so the step the gate let start without a StepFinished is carried as unfinished,
    // keeping one summary entry per started step.
    private Conclusion ConclusionFromOwnTotals(RunContext run, RunEndReason endReason, string? endDetail)
    {
        RunProgress? progress = CurrentProgress;
        IReadOnlyList<StepOutcome> outcomes = run.OutcomesSnapshot();
        JournalStepStart? unfinishedStep = run.LastStartedStep is { } started
            && !outcomes.Any(outcome => outcome.Attributes.Index == started.Index)
                ? new JournalStepStart { Index = started.Index + 1, Count = started.Count, Name = started.Name }
                : null;
        return new Conclusion(
            endReason,
            TotalErrors,
            progress?.VerifiedBytes ?? 0,
            progress?.Elapsed ?? TimeSpan.Zero,
            FirstError?.TimeIntoTest,
            outcomes.Count(outcome => outcome.Completed),
            outcomes,
            0,
            endDetail,
            _timeProvider.GetUtcNow(),
            unfinishedStep);
    }

    private void BeginConclusion(RunContext run, Conclusion conclusion, bool releaseExpected)
    {
        run.Concluded = true;
        run.ConcludedUtc = conclusion.CompletedUtc;
        run.ReleaseExpected = releaseExpected;
        run.ConclusionStarted.TrySetResult();
        ConclusionTask = run.ConclusionDone.Task;
        run.Pipeline = RunPostRunPipelineAsync(run, conclusion);
        PostRunTask = run.Pipeline;
    }

    // Verdict now, summaries after Released: result record, then released, then both summaries, then end, then the
    // confirmation of Released if the fallback published without it, the session's disposal and the journal writer's
    // retirement, and only then is Start offered again. The pipeline never faults.
    private async Task RunPostRunPipelineAsync(RunContext run, Conclusion conclusion)
    {
        try
        {
            await ConcludeAsync(run, conclusion);
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(ConcludeAsync), exception);
        }
        finally
        {
            run.ConclusionDone.TrySetResult();
        }

        try
        {
            ReleaseNotice? release = run.ReleaseExpected ? await WaitForReleaseAsync(run) : null;
            await PublishAsync(run, release);
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(PublishAsync), exception);
            run.PublicationOutcome = new SummaryPublishResult(false, null, null, exception.Message);
            NoticeMessage = UiText.ResultsNotSaved(RedactForDisplay(exception.Message));
            ReportLogFailure(run);
        }
        finally
        {
            run.PublicationDone.TrySetResult();
        }

        await TeardownAsync(run);
    }

    private async Task ConcludeAsync(RunContext run, Conclusion conclusion)
    {
        if (conclusion.TotalErrors > TotalErrors)
        {
            TotalErrors = conclusion.TotalErrors;
        }

        UpdateTruncation(run);

        // A run can end before its first progress tick, as when a stop interrupts step 1's first pass, and the engine
        // sends no final tick. The final snapshot is assigned either way, so the coverage shown is the one the summary
        // records from Completed's verified bytes.
        RunProgress progress = CurrentProgress ?? ProgressBeforeFirstTick(run, TestedBytes ?? 0);
        CurrentProgress = progress with
        {
            VerifiedBytes = conclusion.VerifiedBytes,
            CoveragePercent = RunProgress.CoveragePercentOf(
                conclusion.VerifiedBytes,
                TestedBytes ?? progress.RegionBytes),
            Elapsed = conclusion.Elapsed > progress.Elapsed ? conclusion.Elapsed : progress.Elapsed,
            TotalErrors = TotalErrors,
        };

        IReadOnlyList<StepOutcome> outcomes = DistinctSteps(
            conclusion.StepResults.Count > 0 ? conclusion.StepResults : run.OutcomesSnapshot());
        run.FinalOutcomes = outcomes;
        run.FinalUnfinishedStep = conclusion.UnfinishedStep;

        // The engine's end reason is authoritative. A first-error stop not logged yet, because its error report never
        // arrived or a request logged after the engine had stopped came first, is logged now, stamped as COMPLETED so
        // no stamp decreases. A first error that lost to an earlier stop was no stop decision and gets no line.
        if (conclusion.EndReason == RunEndReason.FirstError && run.TryClaimFirstErrorStopOnCompletion())
        {
            run.Log.LogStopRequested(
                conclusion.CompletedUtc, nameof(RunEndReason.FirstError), conclusion.FirstErrorTime);
        }

        // The engine's own detail, redacted: a start fault carries a managed exception's message, which can name a
        // path in the user's profile, and no log may record personal data. app-errors.log keeps the full exception.
        run.Log.LogCompleted(
            conclusion.CompletedUtc,
            conclusion.EndReason,
            conclusion.EndDetail is { } engineDetail ? SummaryFormat.RedactPersonalPaths(engineDetail) : null,
            TotalErrors,
            conclusion.VerifiedBytes,
            conclusion.Elapsed,
            conclusion.FirstErrorTime);

        string reason;
        if (StateMachine.TryFinish(conclusion.EndReason, TotalErrors, PersistedEndDetail(run, conclusion)))
        {
            reason = ResultReason(run, conclusion);
        }
        else
        {
            // An end the current state cannot take, such as Completed before Prepared, breaks the engine's event
            // contract; the run still ends, as an engine fault, so the state machine never stays in progress.
            string detail = conclusion.EndDetail ?? conclusion.EndReason.ToString();
            if (!StateMachine.TryFinish(RunEndReason.EngineFault, TotalErrors, RedactForDisplay(detail)))
            {
                throw new InvalidOperationException($"A run cannot end from {StateMachine.State}.");
            }

            reason = UiText.EngineError(RedactForDisplay(detail));
        }

        // Set in the frame of the terminal transition: from it until Start is possible again the step field reports
        // what the controller waits for, never the "—" of a terminal state with Start still unavailable.
        PostRunActivity = run.ReleaseExpected ? PostRunActivity.ReleasingMemory : PostRunActivity.SavingResults;

        RunState terminalState = StateMachine.State;
        // An ABORTED run keeps its stop reason and adds the count, joined by the notice separator so both texts stay
        // verbatim from UiText.
        if (terminalState == RunState.Aborted && TotalErrors > 0)
        {
            reason += UiText.NoticeSeparator + UiText.ErrorsBeforeStop(TotalErrors);
        }

        DateTimeOffset verdictUtc = _timeProvider.GetUtcNow();
        ResultMessage = reason;
        RunEndReason endReason = StateMachine.EndReason ?? conclusion.EndReason;
        string? endDetail = StateMachine.EndDetail;
        run.Log.LogVerdict(verdictUtc, terminalState, endReason, endDetail, TotalErrors);

        run.Result = JournalResult.Create(
            terminalState,
            endReason,
            endDetail,
            TotalErrors,
            conclusion.VerifiedBytes,
            conclusion.Elapsed > TimeSpan.Zero ? conclusion.Elapsed : TimeSpan.Zero,
            conclusion.FirstErrorTime is { } first && first >= TimeSpan.Zero ? first : null,
            Math.Max(0, conclusion.StepsCompleted),
            conclusion.CompletedUtc,
            verdictUtc);

        // Queued before the analysis, so the verdict is durable as soon after the terminal transition as possible.
        Task<JournalAckStatus> resultWrite = run.Journal.RecordResultAsync(run.Result);

        if (run.Stored.Count > 0 && terminalState is RunState.Failed or RunState.Aborted)
        {
            CauseHints = await AnalyseAsync(run, outcomes);
        }

        JournalAckStatus resultStatus = await resultWrite;
        if (resultStatus != JournalAckStatus.Acknowledged)
        {
            run.Log.LogJournalFailure(run.Journal.Failure?.Message ?? resultStatus.ToString());
        }
    }

    // The snapshot of a run no progress tick reached: at the step the gate last let start, or else before step 1, with
    // nothing measured yet. Passes planned is 0, the engine's value until a step's first pass ends, and remaining is
    // null, "calculating…", because no estimate ever arrived. A plan without steps never got a session, so its step
    // name stays empty.
    private static RunProgress ProgressBeforeFirstTick(RunContext run, ulong regionBytes)
    {
        IReadOnlyList<StepAttributes> steps = run.Request.Steps;
        EngineStepStarted? started = run.LastStartedStep;
        return new RunProgress
        {
            StepIndex = started?.Index ?? 0,
            StepCount = started?.Count ?? steps.Count,
            StepName = started?.Name ?? (steps.Count > 0 ? steps[0].Name : string.Empty),
            PassIndex = 0,
            PassesPlanned = 0,
            Elapsed = TimeSpan.Zero,
            Remaining = null,
            VerifiedBytes = 0,
            RegionBytes = regionBytes,
            CoveragePercent = 0,
            TotalErrors = 0,
        };
    }

    private string ResultReason(RunContext run, Conclusion conclusion) => conclusion.EndReason switch
    {
        RunEndReason.Completed => StateMachine.State == RunState.Passed
            ? UiText.PassedReason
            : UiText.FailedReason(TotalErrors),
        RunEndReason.FirstError => UiText.StoppedAtFirstErrorReason,
        RunEndReason.UserStop => UiText.StoppedByUser,

        // Only this controller requests a thermal stop, and it keeps the verdict first.
        RunEndReason.Thermal => run.ThermalVerdict is { } verdict
            ? UiText.ThermalStop(verdict.Sensor, verdict.LimitCelsius)
            : RedactForDisplay(conclusion.EndDetail ?? conclusion.EndReason.ToString()),
        RunEndReason.InsufficientMemory => UiText.LowMemoryMessage,
        RunEndReason.AffinityFailed => UiText.AffinityFailed(conclusion.OsError),
        RunEndReason.HostFault => run.JournalFailureText is { } journalFailure
            ? UiText.JournalWriteFailed(RedactForDisplay(journalFailure))
            : UiText.HostFault(
                RedactForDisplay(run.CallbackFaultText ?? conclusion.EndDetail ?? conclusion.EndReason.ToString())),
        _ => UiText.EngineError(RedactForDisplay(conclusion.EndDetail ?? conclusion.EndReason.ToString())),
    };

    // The engine reports a host-fault stop only as the host's request; the cause this controller recorded is what the
    // state machine, the VERDICT line, the result record and both summaries state. The persisted detail takes the
    // displayed reason's form, redacted and on one line, because either can carry an exception message that names a
    // path in the user's profile or holds a line break; app-errors.log keeps the full exception.
    private static string? PersistedEndDetail(RunContext run, Conclusion conclusion)
    {
        string? detail = conclusion.EndReason switch
        {
            RunEndReason.HostFault when run.JournalFailureText is { } journalFailure => UiText.JournalWriteFailed(journalFailure),
            RunEndReason.HostFault when run.CallbackFaultText is { } callbackFault => UiText.HostFault(callbackFault),
            _ => conclusion.EndDetail,
        };
        return detail is null ? null : RedactForDisplay(detail);
    }

    // Every runtime text a notice or a result reason substitutes, such as an exception message, an error the results
    // log or crash recovery reported, or an engine detail, passes through here, so the window never shows a path in
    // the user's profile, as no persisted copy of that text does. app-errors.log keeps the full exception. The text
    // is then one line of plain text, the form the text summary writes, so a line break in it cannot add a line
    // beside the verdict badge or under the notice, and an escape, a NUL or a bidirectional control cannot hide, cut
    // or reorder what the window shows. Redaction runs first because a profile folder name ends at a line break; with
    // a space in its place, the redaction would swallow the text after it. It runs again on the plain text, as every
    // persisted copy redacts it again: a user name followed by an escape or a NUL ends a name only once that
    // character is a space, and redaction leaves its own output unchanged, so the window shows what they hold.
    private static string RedactForDisplay(string text) =>
        SummaryFormat.RedactPersonalPaths(SummaryFormat.PlainText(SummaryFormat.RedactPersonalPaths(text)));

    private async Task<CauseHints> AnalyseAsync(RunContext run, IReadOnlyList<StepOutcome> outcomes)
    {
        ErrorRecord[] errors = run.Stored.ToArray();
        ulong testedBytes = TestedBytes ?? 0;
        int workers = run.WorkerCount;
        MemoryType memoryType = Inventory.MemoryType;
        try
        {
            return await Task.Run(() => ErrorAnalyzer.Analyze(
                new AnalysisInput(errors, outcomes, testedBytes, workers, memoryType)));
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(ErrorAnalyzer), exception);
            return CauseHints.Empty;
        }
    }

    // The fallback is measured from the conclusion, so publication never waits more than 30 s for Released. It frees
    // only the publication: the session stays subscribed, and TeardownAsync keeps Start unavailable until Released
    // itself arrives.
    private async Task<ReleaseNotice?> WaitForReleaseAsync(RunContext run)
    {
        Task<ReleaseNotice?> signal = run.ReleaseSignal.Task;
        if (!await CompletesWithinAsync(signal, RemainingUntil(run.ConcludedUtc, _options.PublicationFallback)))
        {
            run.ReleaseSignal.TrySetResult(null);
        }

        return await signal;
    }

    private async Task PublishAsync(RunContext run, ReleaseNotice? release)
    {
        PostRunActivity = PostRunActivity.SavingResults;

        JournalReleased? released = null;
        if (release is { } notice)
        {
            EngineReleased engineReleased = notice.Released;
            run.Journal.RecordReleased(engineReleased.ReleasedBytes, engineReleased.ReleaseTime);
            run.Log.LogReleased(notice.ReceivedUtc, engineReleased.ReleasedBytes, engineReleased.ReleaseTime);
            released = new JournalReleased
            {
                ReleasedBytes = engineReleased.ReleasedBytes,
                ReleaseMs = WholeMilliseconds(engineReleased.ReleaseTime),
                Utc = notice.ReceivedUtc,
            };
        }
        else if (run.ReleaseExpected)
        {
            run.Log.LogReleaseNotConfirmed(_timeProvider.GetUtcNow());
        }

        var source = new RunSummarySource
        {
            Begin = run.Begin,
            Prepared = run.Prepared,
            FinishedSteps = run.FinalOutcomes.Select(JournalStepEnd.FromOutcome).ToArray(),
            UnfinishedStep = run.FinalUnfinishedStep,
            Result = run.Result,
            Released = released,
            StoredErrors = run.Stored.ToArray(),
            ErrorDetailAvailable = true,
            Temperatures = run.Temperatures,
            CauseHints = ToSummaryCauseHints(CauseHints),
        };
        RunSummary summary = await Task.Run(() => RunSummary.Build(source));

        SummaryPublishResult published;
        try
        {
            published = await run.Log.PublishSummaryAsync(summary);
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(PublishAsync), exception);
            published = new SummaryPublishResult(false, null, null, exception.Message);
        }

        // Publication logged its outcome last, so once the drain has flushed it every event of the run has reached the
        // disk or met a write failure, and the notice can tell fully saved results from partially saved ones.
        _ = await CompletesWithinAsync(run.Log.FlushAsync(), _logFlushTimeout);
        run.PublicationOutcome = published;

        if (published.Succeeded)
        {
            // The summaries hold every result, so the journal is retired even when the event log is incomplete:
            // keeping it would only make the next Start report a recovery that recovers nothing.
            JournalAckStatus ended = await run.Journal.EndAsync();
            if (ended != JournalAckStatus.Acknowledged)
            {
                run.Log.LogJournalFailure(run.Journal.Failure?.Message ?? ended.ToString());
            }
        }
        else
        {
            // The verdict stays displayed, and the journal is kept so the next launch retries the publication.
            string failure = UiText.ResultsNotSaved(RedactForDisplay(published.Error ?? string.Empty));
            NoticeMessage = failure;
            run.Log.LogNotice(failure);
        }

        ReportLogFailure(run);
    }

    // Disposal joins the engine's threads and releases the locked region, which can take long on a large region,
    // so it runs on the thread pool. Start is offered again only once Released has been confirmed, the session is
    // disposed and the journal's writer has retired.
    private async Task TeardownAsync(RunContext run)
    {
        bool releaseConfirmed = !run.ReleaseExpected || await WaitForReleaseConfirmationAsync(run);

        try
        {
            if (run.Session is { } current)
            {
                Unsubscribe(current);
            }

            if (run.TakeSession() is { } session)
            {
                await Task.Run(session.Dispose);
            }
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TeardownAsync), exception);
        }

        try
        {
            await Task.Run(() =>
            {
                run.Journal.Dispose();
                run.Log.Dispose();
            });
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TeardownAsync), exception);
        }

        // Dispose waits only 1 s for the journal's writer. One still stalled in its end append deletes the journal
        // file once the append returns, and that file is the next run's journal too. The task never faults. The writer
        // holds the retirement signal only weakly, so the run, which keeps the journal and stays the current run until
        // below, keeps it alive for the wait.
        await run.Journal.Retired;

        // Closing the log writes what it still buffered, so the close can meet the log's first write failure.
        if (!run.LogFailureReported)
        {
            ReportLogFailure(run);
        }

        if (ReferenceEquals(_run, run))
        {
            Volatile.Write(ref _run, null);
        }

        // A release the controller gave up on, at disposal or shutdown, leaves Start unavailable and the release
        // notice in place: the engine may still hold the region.
        if (releaseConfirmed)
        {
            PostRunActivity = PostRunActivity.None;
            CanStart = !_disposed;
        }

        // A finished run leaves exited per-run threads, the journal's writer and the engine's coordinator that ran the
        // callbacks, whose thread handles and runtime events are released only when their Thread objects are
        // finalized. One background collection per finished run, requested after the session's disposal and the
        // writer's retirement, keeps the process at its baseline handle count instead of letting it grow until an
        // unrelated gen-2 collection. The request returns at once, so it does not block the UI thread.
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
    }

    // The publication fallback publishes without Released, yet the engine may still hold the region: it withholds
    // Released while Windows refuses to free a chunk, and a large region can take longer than the fallback to release.
    // The session therefore stays subscribed and undisposed, since a disposed session drops a late Released, and the
    // UI thread awaits the confirmation without blocking. True once Released arrives; false once the controller gives
    // up on it at disposal or shutdown.
    private async Task<bool> WaitForReleaseConfirmationAsync(RunContext run)
    {
        Task<bool> confirmation = run.ReleaseConfirmed.Task;
        if (confirmation.IsCompleted)
        {
            return await confirmation;
        }

        PostRunActivity = PostRunActivity.ReleasingMemory;
        NoticeMessage = NoticeMessage is { Length: > 0 } notice
            ? notice + UiText.NoticeSeparator + UiText.ReleaseNotConfirmedNotice
            : UiText.ReleaseNotConfirmedNotice;

        bool confirmed = await confirmation;
        if (confirmed)
        {
            NoticeMessage = WithoutReleaseNotice(NoticeMessage);
        }

        return confirmed;
    }

    // Removes only the release notice, which WaitForReleaseConfirmationAsync appended last, and keeps any notice shown
    // before it, such as a failure to save the results.
    private static string? WithoutReleaseNotice(string? notice)
    {
        const string AppendedNotice = UiText.NoticeSeparator + UiText.ReleaseNotConfirmedNotice;
        if (notice == UiText.ReleaseNotConfirmedNotice)
        {
            return null;
        }

        return notice is not null && notice.EndsWith(AppendedNotice, StringComparison.Ordinal)
            ? notice[..^AppendedNotice.Length]
            : notice;
    }

    // Disposal joins the engine's threads, or waits for the journal writer and the log's drain, so a caller that must
    // not block hands it to the thread pool. The task never faults, provided reportFailure, which receives any
    // failure on the pool thread, does not throw. It is counted as background work, and a failure report it queues is
    // counted before the disposal's own count ends, so the count never reads zero between the two.
    private Task DisposeOnThreadPoolAsync(Action dispose, Action<Exception> reportFailure) =>
        _backgroundWork.RunAsync(() =>
        {
            try
            {
                dispose();
            }
            catch (Exception failure)
            {
                reportFailure(failure);
            }
        });

    private async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        if (timeout == TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(timeout, _timeProvider);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private TimeSpan RemainingUntil(DateTimeOffset from, TimeSpan bound)
    {
        if (bound == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan remaining = from + bound - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ReportAppError(string where, Exception exception) =>
        ReportAppError(where, exception, _timeProvider.GetUtcNow());

    // The entry carries the id of the run in progress or still publishing, when there is one, so it can be tied to
    // that run's .log. Called on the UI thread and from the thread pool, so the run is read with Volatile.
    private void ReportAppError(string where, Exception exception, DateTimeOffset utc)
    {
        if (_dataDirectory is { } dataDirectory)
        {
            Guid? runId = Volatile.Read(ref _run)?.Log.RunId;
            _backgroundWork.QueueAppError(dataDirectory, AppErrorSourceOf(where), exception, utc, runId);
        }
    }

    private static string AppErrorSourceOf(string where) => $"{AppErrorSource}.{where}";

    private sealed record StartRequest(
        TestType TestType,
        bool StopOnError,
        TestPlan Plan,
        IReadOnlyList<StepAttributes> Steps,
        SystemSnapshot Snapshot,
        MemoryInventory Inventory,
        ulong PlannedBytes,
        ulong RegionBytes,
        ulong MinRegionBytes,
        DateTimeOffset StartRequestedUtc,
        DataDirectory DataDirectory);

    // What TryStart settles before the continuation: the user's choices, the plan, the request time and the folder.
    private sealed record StartSelection(
        TestType TestType,
        bool StopOnError,
        TestPlan Plan,
        IReadOnlyList<StepAttributes> Steps,
        DateTimeOffset StartRequestedUtc,
        DataDirectory DataDirectory);

    private sealed record DataFolderCheck(string? RefusalNotice, string? RecoveryNotice);

    private sealed record Conclusion(
        RunEndReason EndReason,
        ulong TotalErrors,
        ulong VerifiedBytes,
        TimeSpan Elapsed,
        TimeSpan? FirstErrorTime,
        int StepsCompleted,
        IReadOnlyList<StepOutcome> StepResults,
        uint OsError,
        string? EndDetail,
        DateTimeOffset CompletedUtc,
        JournalStepStart? UnfinishedStep = null);

    private sealed record ReleaseNotice(EngineReleased Released, DateTimeOffset ReceivedUtc);

    // How the step gate's wait for the events posted before it ended.
    private enum EventBarrierOutcome
    {
        Delivered,
        Stopped,
        TimedOut,
    }

    /// <summary>
    /// The step gate's barrier: the UI context runs <see cref="Deliver"/>, which sets the kernel event the gate waits
    /// on, and the gate disposes the event as its wait ends. The state keeps the two apart without a lock, which a
    /// woken gate would contend for and so give a kernel event that only a garbage collection releases: a barrier the
    /// context runs after the gate stopped waiting sets nothing, so no exception reaches the dispatcher, and the gate
    /// never disposes the event while it is being set.
    /// </summary>
    private sealed class EventBarrier : IDisposable
    {
        private const int Waiting = 0;
        private const int Delivering = 1;
        private const int Delivered = 2;
        private const int Abandoned = 3;

        private readonly ManualResetEvent _signal = new(initialState: false);
        private int _state;

        /// <summary>Gets the event the gate waits on.</summary>
        public WaitHandle Signal => _signal;

        /// <summary>Gets a value indicating whether the context ran the barrier while the gate still waited.</summary>
        public bool IsDelivered => Volatile.Read(ref _state) is Delivering or Delivered;

        /// <summary>Runs on the UI context: sets the event, unless the gate has already stopped waiting.</summary>
        public void Deliver()
        {
            if (Interlocked.CompareExchange(ref _state, Delivering, Waiting) != Waiting)
            {
                return;
            }

            try
            {
                _signal.Set();
            }
            finally
            {
                Volatile.Write(ref _state, Delivered);
            }
        }

        /// <summary>
        /// Runs on the gate once its wait has ended: disposes the event, first waiting out a <see cref="Deliver"/>
        /// already setting it, which takes one system call.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Waiting) == Delivering)
            {
                var spinner = default(SpinWait);
                while (Volatile.Read(ref _state) == Delivering)
                {
                    spinner.SpinOnce();
                }
            }

            _signal.Dispose();
        }
    }

    /// <summary>
    /// Counts the thread-pool work the controller hands off without awaiting it: queued <c>app-errors.log</c> writes
    /// and the disposals it leaves running. It holds the count and nothing else, so no controller, run, journal or
    /// session is reachable from it, and a run that references it adds no edge back to its owner.
    /// </summary>
    private sealed class BackgroundWork
    {
        private int _running;

        /// <summary>Gets a value indicating whether any counted work is still queued or running.</summary>
        public bool IsRunning => Volatile.Read(ref _running) != 0;

        /// <summary>
        /// Runs <paramref name="work"/> on the thread pool, counted from before it is queued until it has returned or
        /// thrown, so the count cannot read zero while the work is pending.
        /// </summary>
        /// <param name="work">The work; an exception it throws faults the returned task.</param>
        /// <returns>The work's task.</returns>
        public Task RunAsync(Action work)
        {
            _ = Interlocked.Increment(ref _running);
            return Task.Run(() =>
            {
                try
                {
                    work();
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _running);
                }
            });
        }

        /// <summary>
        /// Appends an entry to <c>app-errors.log</c> on the thread pool and returns at once, counted until the write
        /// has ended. Never blocks and performs no I/O on the calling thread, so the engine's coordinator may call it.
        /// </summary>
        public void QueueAppError(
            DataDirectory dataDirectory,
            string source,
            Exception exception,
            DateTimeOffset utc,
            Guid? runId)
        {
            // Nothing observes the task: AppendAppError swallows every failure to format or write the entry, and it
            // throws only for a missing argument or a blank source, which the controller's callers never pass, so
            // the task never faults.
            _ = RunAsync(() => ResultsLog.AppendAppError(dataDirectory, source, exception, utc, runId));
        }
    }

    /// <summary>
    /// The state of one accepted run, from Preparing until its session is disposed. The session, journal-failure,
    /// callback-fault, stop-failure and last-started-step fields are published atomically, and the stop decision is
    /// taken under a lock, because the coordinator thread reads or writes them; everything else belongs to the UI
    /// thread.
    /// </summary>
    private sealed class RunContext
    {
        private readonly List<StepOutcome> _outcomes = new();

        // Orders the controller's stop requests and guards both stop-line flags; see RequestStop.
        private readonly object _stopGate = new();
        private IEngineSession? _session;
        private string? _journalFailureText;
        private string? _callbackFaultText;
        private EngineException? _stopFailure;
        private EngineStepStarted? _lastStartedStep;
        private int _journalFailureLogged;
        private bool _stopDecisionLogged;
        private bool _firstErrorStopLogged;

        public RunContext(
            StartRequest request,
            ProgressJournal journal,
            ResultsLog log,
            JournalBegin begin,
            TimeProvider timeProvider,
            BackgroundWork backgroundWork)
        {
            Request = request;
            Journal = journal;
            Log = log;
            Begin = begin;
            TimeProvider = timeProvider;
            BackgroundWork = backgroundWork;
            WorkerCount = request.Snapshot.LogicalProcessors;
        }

        public StartRequest Request { get; }

        public ProgressJournal Journal { get; }

        public ResultsLog Log { get; }

        public JournalBegin Begin { get; }

        public TimeProvider TimeProvider { get; }

        // The controller's tracker, so the static callback-fault sink counts its app-error write like the controller's.
        public BackgroundWork BackgroundWork { get; }

        public IEngineSession? Session => Volatile.Read(ref _session);

        public string? JournalFailureText => Volatile.Read(ref _journalFailureText);

        public string? CallbackFaultText => Volatile.Read(ref _callbackFaultText);

        // The first stop the engine refused, from whichever thread requested it.
        public EngineException? StopFailure => Volatile.Read(ref _stopFailure);

        public EngineStepStarted? LastStartedStep => Volatile.Read(ref _lastStartedStep);

        public bool SessionStarted { get; set; }

        public bool Concluded { get; set; }

        public DateTimeOffset ConcludedUtc { get; set; }

        public bool ReleaseExpected { get; set; }

        public JournalPrepared? Prepared { get; set; }

        public int WorkerCount { get; set; }

        public List<ErrorRecord> Stored { get; } = new();

        // The largest groups of Stored, updated batch by batch alongside it.
        public IncrementalErrorGrouping Groups { get; } = new(MaxErrorGroups);

        public IReadOnlyList<StepOutcome> FinalOutcomes { get; set; } = Array.Empty<StepOutcome>();

        public JournalStepStart? FinalUnfinishedStep { get; set; }

        public JournalResult? Result { get; set; }

        public SummaryTemperatures Temperatures { get; set; } = SummaryTemperatures.Initial;

        public ThermalVerdict? ThermalVerdict { get; set; }

        public SensorStatus? LastCpuStatus { get; set; }

        public SensorStatus? LastRamStatus { get; set; }

        public bool JournalFailureAnswered { get; set; }

        public bool LogFailureReported { get; set; }

        public SummaryPublishResult? PublicationOutcome { get; set; }

        public TaskCompletionSource ConclusionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConclusionDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PublicationDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReleaseNotice?> ReleaseSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Whether the engine confirmed the release, kept apart from ReleaseSignal, which the publication fallback also
        // completes: true only on Released, false only once the controller gives up on it for good, at disposal or
        // when the shutdown takes the session past its deadline.
        public TaskCompletionSource<bool> ReleaseConfirmed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Pipeline { get; set; } = Task.CompletedTask;

        public void AttachSession(IEngineSession session) => Volatile.Write(ref _session, session);

        public IEngineSession? TakeSession() => Interlocked.Exchange(ref _session, null);

        // Asks the engine to stop and returns whether this request gets the run's request line, which goes to the first
        // stop the controller requests or sees reported. The engine keeps the first reason it records; requests are
        // made under one lock, so the first claim is always the request the engine received first among the
        // controller's, whichever thread made it. The engine's RequestStop never blocks, so the coordinator waits at
        // most for another one. Neither a run whose session has been taken for disposal, with no engine left to ask,
        // nor a stop the engine refuses, which records nothing and throws, makes a stop decision, so neither claims
        // the line.
        public bool RequestStop(StopReason reason)
        {
            lock (_stopGate)
            {
                if (Session is not { } session)
                {
                    return false;
                }

                session.RequestStop(reason);
                return ClaimStopDecision();
            }
        }

        // Claims the request line for the engine's own first-error stop when its error report arrives before any
        // stop line: the engine stopped itself before reporting, so that error is then the run's only stop decision.
        public bool TryClaimFirstErrorStopOnReport()
        {
            lock (_stopGate)
            {
                if (!ClaimStopDecision())
                {
                    return false;
                }

                _firstErrorStopLogged = true;
                return true;
            }
        }

        // Claims the first-error line once Completed shows that the engine's first error ended the run, whether or
        // not a request line was logged before it: any such request reached the engine after its first-error stop, so
        // the first-writer-wins engine ignored it. True at most once per run.
        public bool TryClaimFirstErrorStopOnCompletion()
        {
            lock (_stopGate)
            {
                if (_firstErrorStopLogged)
                {
                    return false;
                }

                _firstErrorStopLogged = true;
                _stopDecisionLogged = true;
                return true;
            }
        }

        // First writer wins: the gate's failure and the journal's own failure describe the same root cause.
        public void TryRecordJournalFailure(string failure) =>
            Interlocked.CompareExchange(ref _journalFailureText, failure, null);

        public void TryRecordCallbackFault(string failure) =>
            Interlocked.CompareExchange(ref _callbackFaultText, failure, null);

        // True for the first refusal only: the coordinator and the UI thread can both meet one.
        public bool TryRecordStopFailure(EngineException failure) =>
            Interlocked.CompareExchange(ref _stopFailure, failure, null) is null;

        public void RecordStepStarted(EngineStepStarted step) => Volatile.Write(ref _lastStartedStep, step);

        // True for the first caller only: the coordinator and the UI thread can both log the journal failure.
        public bool TryClaimJournalFailureLog() => Interlocked.Exchange(ref _journalFailureLogged, 1) == 0;

        public void RecordOutcome(StepOutcome outcome)
        {
            int existing = _outcomes.FindIndex(item => item.Attributes.Index == outcome.Attributes.Index);
            if (existing >= 0)
            {
                _outcomes[existing] = outcome;
            }
            else
            {
                _outcomes.Add(outcome);
            }
        }

        public IReadOnlyList<StepOutcome> OutcomesSnapshot() => _outcomes.ToArray();

        private bool ClaimStopDecision()
        {
            if (_stopDecisionLogged)
            {
                return false;
            }

            _stopDecisionLogged = true;
            return true;
        }
    }
}
