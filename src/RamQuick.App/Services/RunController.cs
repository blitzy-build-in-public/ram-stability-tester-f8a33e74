using System.Buffers.Binary;
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
/// The outcome of <see cref="RunController.TryStart(TestType, bool)"/>. Every value other than
/// <see cref="Accepted"/> leaves the run state unchanged.
/// </summary>
public enum StartResult
{
    /// <summary>
    /// Start was accepted. The run continues asynchronously: the progress journal's <c>begin</c> record is made
    /// durable, then the run enters Preparing. That continuation can still refuse the start, with the state
    /// unchanged and a notice, when the data folder turns out to be unusable.
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
    /// not yet released its memory and published its summaries.
    /// </summary>
    Busy,

    /// <summary>
    /// Less than 1 GiB of the available memory can be planned for testing. The notice reads
    /// <see cref="UiText.LowMemoryMessage"/>.
    /// </summary>
    InsufficientMemory,

    /// <summary>
    /// No writable data folder exists, so the run could not be journalled. The notice reads
    /// <see cref="UiText.DataFolderUnusableMessage"/>.
    /// </summary>
    DataFolderUnusable,

    /// <summary>
    /// The engine could not describe the machine. The notice reads <see cref="UiText.EngineError(string)"/> with
    /// the engine's message.
    /// </summary>
    EngineUnavailable,
}

/// <summary>
/// What the controller is doing after a run's verdict, until the next Start is possible.
/// </summary>
public enum PostRunActivity
{
    /// <summary>
    /// Nothing is pending: no run has ended, or the last run's memory is released and its summaries published.
    /// </summary>
    None,

    /// <summary>
    /// The verdict is shown and the engine is releasing the locked memory; the step field reads
    /// <see cref="UiText.ReleasingMemory"/>.
    /// </summary>
    ReleasingMemory,

    /// <summary>
    /// The memory is released (or its release was given up on) and the summaries are being published; the step
    /// field reads <see cref="UiText.SavingResults"/>.
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
/// <see langword="false"/>) until that sequence has finished and the session has been disposed.
/// </para>
/// <para>
/// <b>Lifetime.</b> The controller holds the only strong reference to the run's session, and nothing static or
/// timer-driven references a run while it is in progress, so an abandoned controller, its journal and its session
/// are collectable.
/// </para>
/// </remarks>
public sealed partial class RunController : ObservableObject, IDisposable
{
    private const string AppErrorSource = "RunController";
    private const string FatalErrorSource = "UnhandledException";
    private const string EngineCallbackSourcePrefix = "EngineCallback:";
    private const string TemperatureNumberFormat = "0.0";

    private static readonly string _appVersion = ResolveAppVersion();

    private readonly IEngine _engine;
    private readonly SensorMonitor _sensorMonitor;
    private readonly IMemoryInventorySource _memoryInventory;
    private readonly IElevationService _elevation;
    private readonly IUserPrompt _prompt;
    private readonly TimeProvider _timeProvider;
    private readonly DataDirectory? _dataDirectory;
    private readonly RunControllerOptions _options;

    private RunProgress? _currentProgress;
    private SensorReadings _latestReadings;
    private ulong _totalErrors;
    private ErrorRecord? _firstError;
    private IReadOnlyList<ErrorGroup> _errorGroups = Array.Empty<ErrorGroup>();
    private bool _isErrorDetailTruncated;
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
    /// progress report. On Completed it is replaced by a final snapshot carrying the run's verified bytes.
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
    /// Gets the stored error records grouped by 8-byte word address, largest groups first. A new list instance is
    /// assigned whenever the groups change.
    /// </summary>
    public IReadOnlyList<ErrorGroup> ErrorGroups
    {
        get => _errorGroups;
        private set => SetProperty(ref _errorGroups, value);
    }

    /// <summary>
    /// Gets a value indicating whether the stored-detail cap (10,000 records) was reached while more errors were
    /// counted, so that <see cref="ErrorGroups"/> describe the stored records only.
    /// </summary>
    public bool IsErrorDetailTruncated
    {
        get => _isErrorDetailTruncated;
        private set => SetProperty(ref _isErrorDetailTruncated, value);
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
    /// finished, no Start is pending, no run is in progress, and the last run has released its memory and published
    /// its summaries.
    /// </summary>
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
    /// Gets the asynchronous continuation of the last accepted Start; tests await it deterministically.
    /// </summary>
    internal Task StartTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets the result, release-wait, publication and disposal pipeline of the last run that ended; tests await it.
    /// </summary>
    internal Task PostRunTask { get; private set; } = Task.CompletedTask;


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
    /// An unfinished journal moves the state Idle â†’ Failed with the "did not finish" reason, shown without a dialog.
    /// A run whose verdict was journalled but whose summaries were missing stays Idle with the recovery notice. A
    /// failed probe or recovery is written to the application error log and does not stop initialisation.
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
                ReportAppError(nameof(InitializeAsync), exception);
            }
        }

        CanStart = !_disposed;
    }

    /// <summary>
    /// Requests a run of <paramref name="testType"/>. Returns at once and never blocks on I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal leaves the run state unchanged. Elevation is checked first, and a non-elevated Start shows the
    /// elevation offer instead of starting. A missing data folder, an engine that cannot describe the machine, and
    /// less than 1 GiB of plannable memory are refused at once with a notice.
    /// </para>
    /// <para>
    /// An accepted Start continues asynchronously: the data folder is probed, a journal left by an earlier failed
    /// publication is recovered, the results log reserves the run's base name, and the journal's <c>begin</c> record
    /// is made durable within 5 s. Only then does the run enter Preparing and the engine session start. A failure
    /// there refuses the Start with <see cref="UiText.DataFolderUnusableMessage"/> (or the save failure of the
    /// earlier run) and re-enables Start, with the state unchanged.
    /// </para>
    /// </remarks>
    /// <param name="testType">The test type to run.</param>
    /// <param name="stopOnError">Whether the run stops at the first confirmed error.</param>
    /// <returns><see cref="StartResult.Accepted"/>, or why the Start was refused.</returns>
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

        SystemSnapshot snapshot;
        try
        {
            snapshot = _engine.QuerySystem();
        }
        catch (Exception exception)
        {
            ReportAppError(nameof(TryStart), exception);
            NoticeMessage = UiText.EngineError(exception.Message);
            return StartResult.EngineUnavailable;
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
            NoticeMessage = UiText.LowMemoryMessage;
            return StartResult.InsufficientMemory;
        }

        TestPlan plan = _options.PlanProvider(testType)
            ?? throw new InvalidOperationException($"The plan provider returned no plan for {testType}.");

        var request = new StartRequest(
            testType,
            stopOnError,
            plan,
            plan.ToStepAttributes(),
            snapshot,
            Inventory,
            sizing.PlannedBytes,
            regionBytes,
            Math.Min(MemorySizing.MinimumRegionBytes, regionBytes),
            _timeProvider.GetUtcNow(),
            dataDirectory);

        CanStart = false;
        NoticeMessage = null;
        var pending = new CancellationTokenSource();
        _pendingStart = pending;
        StartTask = RunStartAsync(request, pending);
        return StartResult.Accepted;
    }

    /// <summary>
    /// Requests that the current run stop with the user-stop reason. Never blocks.
    /// </summary>
    /// <remarks>
    /// While an accepted Start is still making its <c>begin</c> record durable, the Start is withdrawn with the state
    /// unchanged. In Preparing, including a step's journal wait, and in Running, the engine is asked to stop and the
    /// run ends ABORTED with "Stopped by user" within 2 s. Otherwise nothing happens.
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
        }
    }


    /// <summary>
    /// Ends the controller's work when the main window closes. Never throws; later calls return the same work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pending Start is withdrawn. A run in progress is stopped with the user-stop reason, and Completed is awaited
    /// for at most <see cref="RunControllerOptions.ShutdownCompletedTimeout"/> (3 s); if it never arrives, the run is
    /// finalised as ABORTED from the controller's own totals. Released is then awaited for at most
    /// <see cref="RunControllerOptions.ShutdownReleasedTimeout"/> (10 s) after the conclusion, and the summaries are
    /// published with <c>memory.releaseSeconds: null</c> if it has not arrived.
    /// </para>
    /// <para>
    /// The session's disposal, which releases the locked memory, is awaited only until that deadline: once it has
    /// passed the method returns and the process may exit, and Windows reclaims the pages. Every bound is measured on
    /// the controller's <see cref="TimeProvider"/>. Start is refused from the moment this method is called.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the controller's work is finished or its deadline has passed.</returns>
    public Task ShutdownAsync()
    {
        _shutdownTask ??= ShutdownCoreAsync();
        return _shutdownTask;
    }

    /// <summary>
    /// Records an exception that is about to end the process and releases the run's locked memory. Called from the
    /// application's unhandled-exception handlers on any thread; never throws.
    /// </summary>
    /// <remarks>
    /// The exception is appended synchronously to <c>&lt;data&gt;\logs\app-errors.log</c> when a data folder exists.
    /// The session is then disposed, which stops the engine, joins its threads and releases the memory; its journal
    /// is left in place, so the next launch reports the run.
    /// </remarks>
    /// <param name="exception">The unhandled exception; nothing is logged when it is <see langword="null"/>.</param>
    public void HandleFatalException(Exception exception)
    {
        DataDirectory? dataDirectory = _dataDirectory;
        if (dataDirectory is not null && exception is not null)
        {
            ResultsLog.AppendAppError(dataDirectory, FatalErrorSource, exception, _timeProvider.GetUtcNow());
        }

        try
        {
            Volatile.Read(ref _run)?.TakeSession()?.Dispose();
        }
        catch (Exception disposeFailure)
        {
            // The session refuses disposal from inside an engine callback, which is where a coordinator-thread crash
            // lands; the process is ending, and Windows reclaims the locked pages with it.
            if (dataDirectory is not null)
            {
                ResultsLog.AppendAppError(dataDirectory, FatalErrorSource, disposeFailure, _timeProvider.GetUtcNow());
            }
        }
    }

    /// <summary>
    /// Releases everything the controller still holds: unsubscribes from the sensor monitor, withdraws a pending
    /// Start, and disposes the current session (releasing its memory), journal and results log. Idempotent.
    /// </summary>
    /// <remarks>
    /// Normally called at exit after <see cref="ShutdownAsync"/>, when nothing is left. Disposing a session can block
    /// until the engine has released the region. A journal disposed before its <c>end</c> record is kept on disk, so
    /// the next launch recovers the run.
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
            if (run.TakeSession() is { } session)
            {
                Unsubscribe(session);
                session.Dispose();
            }

            run.Journal.Dispose();
            run.Log.Dispose();
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

    private static string UnfinishedRunMessage(JournalRecovery recovery) =>
        recovery is { InPreparation: false, LastStepNumber: int number, StepCount: int count }
        && number >= 1
        && number <= count
            ? UiText.PreviousRunUnfinished(number, count, recovery.LastStepName ?? string.Empty)
            : UiText.PreviousRunUnfinishedPreparing;

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
            }

            bool completed = await CompletesWithinAsync(
                run.ConclusionStarted.Task,
                RemainingUntil(shutdownStart, _options.ShutdownCompletedTimeout));
            if (!completed && !run.Concluded)
            {
                BeginConclusion(run, ConclusionFromOwnTotals(run), releaseExpected: run.SessionStarted);
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

        // Past the deadline the process may exit with the session still releasing; Windows reclaims the pages.
        _ = await CompletesWithinAsync(run.Pipeline, RemainingUntil(run.ConcludedUtc, _options.ShutdownReleasedTimeout));
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
            NoticeMessage = UiText.ResultsNotSaved(error);
        }
    }

    private async Task RunStartAsync(StartRequest request, CancellationTokenSource pending)
    {
        ResultsLog? log = null;
        ProgressJournal? journal = null;
        Task<JournalAckStatus>? beginTask = null;
        try
        {
            DataFolderPreparation preparation = await Task.Run(() => PrepareDataFolder(request));
            if (preparation.RecoveryNotice is { } recoveryNotice)
            {
                NoticeMessage = recoveryNotice;
            }

            log = preparation.Log;
            if (log is null || pending.IsCancellationRequested)
            {
                await RefuseStartAsync(pending, preparation.RefusalNotice, journal, beginTask, log);
                return;
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
                await RefuseStartAsync(pending, notice: null, journal, beginTask, log);
                return;
            }

            if (status != JournalAckStatus.Acknowledged)
            {
                await RefuseStartAsync(pending, UiText.DataFolderUnusableMessage, journal, beginTask, log);
                return;
            }

            BeginRun(request, pending, journal, log, begin);
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
            }
            else
            {
                await RefuseStartAsync(pending, UiText.DataFolderUnusableMessage, journal, beginTask, log);
            }
        }
    }

    // Runs on the thread pool: the probe, the recovery of a kept journal and the name reservation all do file I/O.
    private DataFolderPreparation PrepareDataFolder(StartRequest request)
    {
        DataDirectory dataDirectory = request.DataDirectory;
        if (!dataDirectory.CanWrite())
        {
            return new DataFolderPreparation(null, UiText.DataFolderUnusableMessage, null);
        }

        // A journal still on disk holds a run whose summaries could not be published. The new run would append to
        // that file and delete it with its own end record, losing the earlier run's records, so publication is
        // retried first, and Start is refused while it still fails.
        string? recoveryNotice = null;
        if (File.Exists(dataDirectory.JournalPath))
        {
            JournalRecovery recovery = CrashDetector.Detect(dataDirectory, _timeProvider);
            if (recovery.PublicationError is { } error)
            {
                return new DataFolderPreparation(null, UiText.ResultsNotSaved(error), null);
            }

            if (recovery.PublishedFiles.Count > 0)
            {
                recoveryNotice = UiText.RecoveredNotice;
            }
        }

        ResultsLog? log = ResultsLog.OpenRun(
            dataDirectory,
            request.TestType.ToString(),
            request.StartRequestedUtc,
            _timeProvider);
        return log is null
            ? new DataFolderPreparation(null, UiText.DataFolderUnusableMessage, recoveryNotice)
            : new DataFolderPreparation(log, null, recoveryNotice);
    }

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

    // Leaves the state unchanged and re-enables Start only once the journal is retired, so the next run's journal
    // can never be deleted by this one's late end record.
    private async Task RefuseStartAsync(
        CancellationTokenSource pending,
        string? notice,
        ProgressJournal? journal,
        Task<JournalAckStatus>? beginTask,
        ResultsLog? log)
    {
        if (notice is not null)
        {
            NoticeMessage = notice;
        }

        try
        {
            if (journal is not null)
            {
                await RetireUnstartedJournalAsync(journal, beginTask);
            }

            if (log is not null)
            {
                await Task.Run(() => DiscardReservedLog(log));
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
        var run = new RunContext(request, journal, log, begin, _timeProvider);
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
        var options = new EngineSessionOptions
        {
            RegionBytes = request.RegionBytes,
            MinRegionBytes = request.MinRegionBytes,
            WorkerCount = request.Snapshot.LogicalProcessors,
            Steps = request.Steps,
            StopOnError = request.StopOnError,
            Seed = SeedFrom(run.Begin.RunId),
            StepGate = (step, stopEvent) => GateStep(run, step, stopEvent),
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
    // thread-safe, and the run's atomically published fields.
    private static StepGateDecision GateStep(RunContext run, EngineStepStarted step, WaitHandle stopEvent)
    {
        try
        {
            JournalAck ack = run.Journal.RecordStepStarting(step.Index, step.Count, step.Name);
            JournalAckStatus status = ack.Wait(stopEvent, ProgressJournal.AcknowledgmentTimeout);
            return status switch
            {
                JournalAckStatus.Acknowledged => StepGateDecision.Continue,

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

    // The host-fault stop is recorded before Abort is returned, so the aborted step starts no traffic and the run
    // ends HostFault rather than with an unexplained gate abort.
    private static StepGateDecision FailStep(RunContext run, string failure)
    {
        run.TryRecordJournalFailure(failure);
        run.Session?.RequestStop(StopReason.HostFault);
        return StepGateDecision.Abort;
    }

    // Runs on the engine coordinator or the UI thread; non-blocking, and the app-error write goes to the thread pool.
    private static void RecordCallbackFault(RunContext run, string callbackName, Exception exception)
    {
        ResultsLog.QueueAppError(
            run.Request.DataDirectory,
            EngineCallbackSourcePrefix + callbackName,
            exception,
            run.TimeProvider.GetUtcNow());
        run.TryRecordCallbackFault(exception.Message);
        run.Session?.RequestStop(StopReason.HostFault);
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
                ErrorGroups = ErrorGrouping.Group(run.Stored);
                run.Log.LogErrors(accepted, Math.Max(TotalErrors, e.TotalErrors));
            }

            if (e.TotalErrors > TotalErrors)
            {
                TotalErrors = e.TotalErrors;
            }

            UpdateTruncation(run);
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
        }
    }

    // An exception in a handler must never reach the dispatcher mid-run: it is logged and ends the run HostFault.
    private void OnHandlerFault(RunContext run, string handlerName, Exception exception)
    {
        ReportAppError(handlerName, exception);
        run.TryRecordCallbackFault(exception.Message);
        if (!run.Concluded && StateMachine.IsRunInProgress)
        {
            RequestEngineStop(run, StopReason.HostFault);
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

    private static void LogJournalFailureOnce(RunContext run)
    {
        if (!run.JournalFailureLogged && run.JournalFailureText is { } failure)
        {
            run.JournalFailureLogged = true;
            run.Log.LogJournalFailure(failure);
        }
    }

    private void RequestEngineStop(RunContext run, StopReason reason)
    {
        run.Session?.RequestStop(reason);
        string reasonName = reason switch
        {
            StopReason.User => nameof(RunEndReason.UserStop),
            StopReason.Thermal => nameof(RunEndReason.Thermal),
            _ => nameof(RunEndReason.HostFault),
        };
        run.Log.LogStopRequested(_timeProvider.GetUtcNow(), reasonName);
    }

    private void UpdateTruncation(RunContext run) =>
        IsErrorDetailTruncated = run.Stored.Count >= ErrorGrouping.StoredDetailCap
            && TotalErrors > (ulong)run.Stored.Count;

    private void OnReadingsChanged(object? sender, SensorReadings readings)
    {
        RunContext? run = _run;
        try
        {
            LatestReadings = readings;
            if (run is { Concluded: false } && StateMachine.IsRunInProgress)
            {
                ObserveReadings(run, readings, evaluateThermal: StateMachine.State == RunState.Running);
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

    // Used only when the engine stays silent past the shutdown bound: the run ends ABORTED as a user stop with what
    // the controller itself observed.
    private Conclusion ConclusionFromOwnTotals(RunContext run)
    {
        RunProgress? progress = CurrentProgress;
        IReadOnlyList<StepOutcome> outcomes = run.OutcomesSnapshot();
        return new Conclusion(
            RunEndReason.UserStop,
            TotalErrors,
            progress?.VerifiedBytes ?? 0,
            progress?.Elapsed ?? TimeSpan.Zero,
            FirstError?.TimeIntoTest,
            outcomes.Count(outcome => outcome.Completed),
            outcomes,
            0,
            null,
            _timeProvider.GetUtcNow());
    }

    private void BeginConclusion(RunContext run, Conclusion conclusion, bool releaseExpected)
    {
        run.Concluded = true;
        run.ConcludedUtc = conclusion.CompletedUtc;
        run.ReleaseExpected = releaseExpected;
        run.ConclusionStarted.TrySetResult();
        run.Pipeline = RunPostRunPipelineAsync(run, conclusion);
        PostRunTask = run.Pipeline;
    }

    // Verdict now, summaries after Released: result record, then released, then both summaries, then end, then the
    // session's disposal, and only then is Start offered again. The pipeline never faults.
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
            NoticeMessage = UiText.ResultsNotSaved(exception.Message);
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
        if (CurrentProgress is { } progress)
        {
            CurrentProgress = progress with
            {
                VerifiedBytes = conclusion.VerifiedBytes,
                CoveragePercent = RunProgress.CoveragePercentOf(
                    conclusion.VerifiedBytes,
                    TestedBytes ?? progress.RegionBytes),
                Elapsed = conclusion.Elapsed > progress.Elapsed ? conclusion.Elapsed : progress.Elapsed,
                TotalErrors = TotalErrors,
            };
        }

        IReadOnlyList<StepOutcome> outcomes = DistinctSteps(
            conclusion.StepResults.Count > 0 ? conclusion.StepResults : run.OutcomesSnapshot());
        run.FinalOutcomes = outcomes;

        LogJournalFailureOnce(run);
        run.Log.LogCompleted(
            conclusion.CompletedUtc,
            conclusion.EndReason,
            conclusion.EndDetail,
            TotalErrors,
            conclusion.VerifiedBytes,
            conclusion.Elapsed,
            conclusion.FirstErrorTime);

        string reason;
        if (StateMachine.TryFinish(conclusion.EndReason, TotalErrors, conclusion.EndDetail))
        {
            reason = ResultReason(run, conclusion);
        }
        else
        {
            // An end the current state cannot take, such as Completed before Prepared, breaks the engine's event
            // contract; the run still ends, as an engine fault, so the state machine never stays in progress.
            string detail = conclusion.EndDetail ?? conclusion.EndReason.ToString();
            if (!StateMachine.TryFinish(RunEndReason.EngineFault, TotalErrors, detail))
            {
                throw new InvalidOperationException($"A run cannot end from {StateMachine.State}.");
            }

            reason = UiText.EngineError(detail);
        }

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

        PostRunActivity = run.ReleaseExpected ? PostRunActivity.ReleasingMemory : PostRunActivity.SavingResults;
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
            : conclusion.EndDetail ?? conclusion.EndReason.ToString(),
        RunEndReason.InsufficientMemory => UiText.LowMemoryMessage,
        RunEndReason.AffinityFailed => UiText.AffinityFailed(conclusion.OsError),
        RunEndReason.HostFault => run.JournalFailureText is { } journalFailure
            ? UiText.JournalWriteFailed(journalFailure)
            : UiText.EngineError(run.CallbackFaultText ?? conclusion.EndDetail ?? conclusion.EndReason.ToString()),
        _ => UiText.EngineError(conclusion.EndDetail ?? conclusion.EndReason.ToString()),
    };

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

    // The fallback is measured from the conclusion, so publication never waits more than 30 s for Released.
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

        if (published.Succeeded)
        {
            JournalAckStatus ended = await run.Journal.EndAsync();
            if (ended != JournalAckStatus.Acknowledged)
            {
                run.Log.LogJournalFailure(run.Journal.Failure?.Message ?? ended.ToString());
            }

            return;
        }

        // The verdict stays displayed, and the journal is kept so the next launch retries the publication.
        string failure = UiText.ResultsNotSaved(published.Error ?? string.Empty);
        NoticeMessage = failure;
        run.Log.LogNotice(failure);
    }

    // Disposal joins the engine's threads and releases the locked region, which can take long on a large region,
    // so it runs on the thread pool; Start is offered again only after it.
    private async Task TeardownAsync(RunContext run)
    {
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

        if (ReferenceEquals(_run, run))
        {
            Volatile.Write(ref _run, null);
        }

        PostRunActivity = PostRunActivity.None;
        CanStart = !_disposed;
    }

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

    private void ReportAppError(string where, Exception exception)
    {
        if (_dataDirectory is { } dataDirectory)
        {
            ResultsLog.QueueAppError(dataDirectory, $"{AppErrorSource}.{where}", exception, _timeProvider.GetUtcNow());
        }
    }

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

    private sealed record DataFolderPreparation(ResultsLog? Log, string? RefusalNotice, string? RecoveryNotice);

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
        DateTimeOffset CompletedUtc);

    private sealed record ReleaseNotice(EngineReleased Released, DateTimeOffset ReceivedUtc);

    /// <summary>
    /// The state of one accepted run, from Preparing until its session is disposed. The session, journal-failure
    /// and callback-fault fields are published atomically because the coordinator thread reads or writes them;
    /// everything else belongs to the UI thread.
    /// </summary>
    private sealed class RunContext
    {
        private readonly List<StepOutcome> _outcomes = new();
        private IEngineSession? _session;
        private string? _journalFailureText;
        private string? _callbackFaultText;

        public RunContext(
            StartRequest request,
            ProgressJournal journal,
            ResultsLog log,
            JournalBegin begin,
            TimeProvider timeProvider)
        {
            Request = request;
            Journal = journal;
            Log = log;
            Begin = begin;
            TimeProvider = timeProvider;
            WorkerCount = request.Snapshot.LogicalProcessors;
        }

        public StartRequest Request { get; }

        public ProgressJournal Journal { get; }

        public ResultsLog Log { get; }

        public JournalBegin Begin { get; }

        public TimeProvider TimeProvider { get; }

        public IEngineSession? Session => Volatile.Read(ref _session);

        public string? JournalFailureText => Volatile.Read(ref _journalFailureText);

        public string? CallbackFaultText => Volatile.Read(ref _callbackFaultText);

        public bool SessionStarted { get; set; }

        public bool Concluded { get; set; }

        public DateTimeOffset ConcludedUtc { get; set; }

        public bool ReleaseExpected { get; set; }

        public JournalPrepared? Prepared { get; set; }

        public int WorkerCount { get; set; }

        public List<ErrorRecord> Stored { get; } = new();

        public IReadOnlyList<StepOutcome> FinalOutcomes { get; set; } = Array.Empty<StepOutcome>();

        public JournalResult? Result { get; set; }

        public SummaryTemperatures Temperatures { get; set; } = SummaryTemperatures.Initial;

        public ThermalVerdict? ThermalVerdict { get; set; }

        public SensorStatus? LastCpuStatus { get; set; }

        public SensorStatus? LastRamStatus { get; set; }

        public bool JournalFailureAnswered { get; set; }

        public bool JournalFailureLogged { get; set; }

        public TaskCompletionSource ConclusionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConclusionDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PublicationDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReleaseNotice?> ReleaseSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Pipeline { get; set; } = Task.CompletedTask;

        public void AttachSession(IEngineSession session) => Volatile.Write(ref _session, session);

        public IEngineSession? TakeSession() => Interlocked.Exchange(ref _session, null);

        // First writer wins: the gate's failure and the journal's own failure describe the same root cause.
        public void TryRecordJournalFailure(string failure) =>
            Interlocked.CompareExchange(ref _journalFailureText, failure, null);

        public void TryRecordCallbackFault(string failure) =>
            Interlocked.CompareExchange(ref _callbackFaultText, failure, null);

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
    }
}

