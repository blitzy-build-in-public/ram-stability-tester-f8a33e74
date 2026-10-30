using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RamQuick.Analysis;
using RamQuick.App.Services;
using RamQuick.App.Tests.Fakes;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Logging.Tests;
using RamQuick.Plans;
using RamQuick.Sensors;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// The object graph a <see cref="RunController"/> runs in, built from fakes for one test: a fake clock, a queued
/// stand-in for the UI thread's context, a scriptable engine, sensors, inventory, elevation and prompt, and a data
/// folder in a unique temporary directory. Shared by the controller, view-model and window-layout tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> <see cref="Context"/> queues every post, so the test thread is the only "UI thread": the
/// drive helpers call the controller with <see cref="Context"/> installed, so its awaits capture it, and pump it on
/// the calling thread until a condition holds or a real-time bound passes. Never <c>await</c> while the context
/// is installed and never block on a task in a test method; use <see cref="RunUntil"/>, the helpers built on it, or
/// <c>Context.RunAsync</c>. <see cref="FakeTimeProvider.Advance"/> runs due timer callbacks inline; their
/// continuations land in <see cref="Context"/> and are pumped by the next helper.
/// </para>
/// <para>
/// <b>Clocks.</b> The controller's begin, publication and shutdown bounds, the journal's awaitable
/// acknowledgments and heartbeats, and the sensor monitor's polling all run on <see cref="Time"/>, which moves only
/// when a test advances it. The step gate's journal wait is real time.
/// </para>
/// <para>
/// <b>Construction.</b> The controller is built lazily by <see cref="Build"/>, or by the first use of
/// <see cref="Controller"/>, so a test can first replace options, fakes or the data folder.
/// </para>
/// </remarks>
internal sealed class ControllerHarness : IDisposable
{
    /// <summary>The fake clock's starting time.</summary>
    public static readonly DateTimeOffset StartTime = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The real-time bound of every pumping wait unless a caller passes its own.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private const int DeleteAttempts = 50;

    private static readonly TimeSpan _deleteRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly List<ProgressJournal> _journals = new();
    private RunController? _controller;
    private int _readingsDelivered;
    private int _shutdownRequestedCount;
    private bool _disposed;

    /// <summary>
    /// Creates the graph with an elevated process, a prompt that answers No, both sensors available, the default
    /// fake machine and inventory, the production options, and a data folder under <see cref="RootPath"/>.
    /// </summary>
    public ControllerHarness()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "RamQuick.App.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);

        Time = new FakeTimeProvider(StartTime);
        Context = new ImmediateSynchronizationContext(queuePosts: true);
        Engine = new FakeEngine(contextOverride: Context);
        Sensors = new FakeSensorSource(Time);
        Monitor = new SensorMonitor(Sensors, Time);
        Monitor.ReadingsChanged += (_, _) => Interlocked.Increment(ref _readingsDelivered);
        Inventory = new FakeMemoryInventorySource();
        Elevation = new FakeElevationService();
        Prompt = new FakeUserPrompt();
        StateMachine = new RunStateMachine();
        Data = DataDirectory.FromRoot(Path.Combine(RootPath, "data"));
        Options = new RunControllerOptions();
    }

    /// <summary>Gets the fake clock every bound of the graph is measured on, except the step gate's.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Gets the queued context that stands in for the UI thread.</summary>
    public ImmediateSynchronizationContext Context { get; }

    /// <summary>Gets the fake engine; its sessions raise their events on <see cref="Context"/>.</summary>
    public FakeEngine Engine { get; }

    /// <summary>Gets the scriptable sensor source, stamped by <see cref="Time"/>.</summary>
    public FakeSensorSource Sensors { get; }

    /// <summary>Gets the sensor monitor over <see cref="Sensors"/>; it polls only after <see cref="StartSensors"/>.</summary>
    public SensorMonitor Monitor { get; }

    /// <summary>Gets the memory inventory source.</summary>
    public FakeMemoryInventorySource Inventory { get; }

    /// <summary>Gets the elevation service; elevated unless a test says otherwise.</summary>
    public FakeElevationService Elevation { get; }

    /// <summary>Gets the prompt; it answers No unless a test scripts another answer.</summary>
    public FakeUserPrompt Prompt { get; }

    /// <summary>Gets the unique temporary folder that holds everything the test writes.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets or sets the data folder the controller is built with; <see langword="null"/> stands for a machine where
    /// neither data location is writable.
    /// </summary>
    public DataDirectory? Data { get; set; }

    /// <summary>Gets the single run state machine.</summary>
    public RunStateMachine StateMachine { get; }

    /// <summary>
    /// Gets or sets the controller's options. <see cref="Build"/> wraps their journal factory so that every journal it
    /// creates is captured in <see cref="Journals"/>.
    /// </summary>
    public RunControllerOptions Options { get; set; }

    /// <summary>Gets the in-memory journal storage after <see cref="UseStalledJournal"/>, or <see langword="null"/>.</summary>
    public StalledJournalStorage? Storage { get; private set; }

    /// <summary>Gets every progress journal the controller has created, in creation order.</summary>
    public IReadOnlyList<ProgressJournal> Journals
    {
        get
        {
            lock (_journals)
            {
                return _journals.ToArray();
            }
        }
    }

    /// <summary>Gets the controller, building it on first use.</summary>
    public RunController Controller => _controller ?? Build();

    /// <summary>Gets the most recently created engine session.</summary>
    /// <exception cref="InvalidOperationException">No session has been created.</exception>
    public FakeEngineSession Session =>
        Engine.LastSession ?? throw new InvalidOperationException("The controller has not created an engine session.");

    /// <summary>Gets the number of sensor snapshots the monitor has raised so far.</summary>
    public int ReadingsDelivered => Volatile.Read(ref _readingsDelivered);

    /// <summary>Gets the number of times the controller raised <see cref="RunController.ShutdownRequested"/>.</summary>
    public int ShutdownRequestedCount => Volatile.Read(ref _shutdownRequestedCount);

    /// <summary>Gets a value indicating whether the run's journal exists, in <see cref="Storage"/> or on disk.</summary>
    public bool JournalExists => Storage?.Exists() ?? (Data is { } data && File.Exists(data.JournalPath));

    /// <summary>
    /// Builds the controller once from the current fakes, data folder and options; later calls return it.
    /// </summary>
    /// <returns>The controller.</returns>
    public RunController Build()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controller is not null)
        {
            return _controller;
        }

        Func<DataDirectory, TimeProvider, ProgressJournal> factory = Options.JournalFactory;
        RunControllerOptions options = CopyOptions(
            Options,
            journalFactory: (dataDirectory, timeProvider) => Capture(factory(dataDirectory, timeProvider)));
        var controller = new RunController(Engine, StateMachine, Monitor, Inventory, Elevation, Prompt, Time, Data, options);
        controller.ShutdownRequested += (_, _) => Interlocked.Increment(ref _shutdownRequestedCount);
        _controller = controller;
        return controller;
    }

    /// <summary>
    /// Makes every run journal into one <see cref="StalledJournalStorage"/>, which the test can block or fail. The
    /// journal takes ownership of the storage, so it serves one run.
    /// </summary>
    /// <param name="journalTime">
    /// The clock of the journal's acknowledgment and heartbeat bounds; the controller's, <see cref="Time"/>, when
    /// <see langword="null"/>. A clock the test never advances keeps a blocked acknowledgment pending for good.
    /// </param>
    public void UseStalledJournal(TimeProvider? journalTime = null)
    {
        ThrowIfBuilt();
        StalledJournalStorage storage = Storage ??= new StalledJournalStorage();
        Options = CopyOptions(
            Options,
            journalFactory: (_, timeProvider) => new ProgressJournal(storage, journalTime ?? timeProvider));
    }

    /// <summary>Makes every Start run <paramref name="plan"/>, whatever test type is selected.</summary>
    /// <param name="plan">The plan to run.</param>
    public void UsePlan(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfBuilt();
        Options = CopyOptions(Options, planProvider: _ => plan);
    }

    /// <summary>Caps the bytes the controller requests from the engine, as the headless test host does.</summary>
    /// <param name="cap">The cap, in bytes.</param>
    public void UseMaxRegionBytes(ulong cap)
    {
        ThrowIfBuilt();
        RunControllerOptions current = Options;
        Options = new RunControllerOptions
        {
            PlanProvider = current.PlanProvider,
            MaxRegionBytes = cap,
            JournalFactory = current.JournalFactory,
            PublicationFallback = current.PublicationFallback,
            ShutdownCompletedTimeout = current.ShutdownCompletedTimeout,
            ShutdownReleasedTimeout = current.ShutdownReleasedTimeout,
            BeginTimeout = current.BeginTimeout,
        };
    }

    /// <summary>Builds the controller without a data folder, as on a machine where neither location is writable.</summary>
    public void WithoutDataDirectory()
    {
        ThrowIfBuilt();
        Data = null;
    }

    /// <summary>
    /// Gives the controller a data folder whose root lies beneath an existing file, so neither the folder nor its
    /// subfolders can be created and its write probe fails, with no access-control change.
    /// </summary>
    public void WithUnwritableDataDirectory()
    {
        ThrowIfBuilt();
        string blocker = Path.Combine(RootPath, "blocked");
        Data = DataDirectory.FromRoot(Path.Combine(blocker, "data"));
        Directory.Delete(blocker, recursive: true);
        File.WriteAllText(blocker, string.Empty);
    }

    /// <summary>Runs every queued callback on the calling thread.</summary>
    public void Pump() => Context.RunPending();

    /// <summary>
    /// Pumps <see cref="Context"/> on the calling thread until <paramref name="condition"/> holds.
    /// </summary>
    /// <param name="condition">The condition, evaluated on the calling thread after each drain.</param>
    /// <param name="timeout">The real-time bound; <see cref="DefaultWait"/> when <see langword="null"/>.</param>
    /// <param name="conditionText">The condition's source text, for the timeout message.</param>
    /// <exception cref="TimeoutException">The condition did not hold within the bound.</exception>
    public void RunUntil(
        Func<bool> condition,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionText = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        TimeSpan bound = timeout ?? DefaultWait;
        if (!Context.RunUntil(condition, bound))
        {
            throw new TimeoutException(
                $"'{conditionText}' did not hold within {bound.TotalSeconds:0.#} s: state {StateMachine.State}, "
                + $"post-run activity {_controller?.PostRunActivity}, can start {_controller?.CanStart}, "
                + $"{Context.PendingCount} queued post(s).");
        }
    }

    /// <summary>Calls the controller with <see cref="Context"/> installed, so its awaits resume on the "UI thread".</summary>
    /// <typeparam name="T">The call's result type.</typeparam>
    /// <param name="call">The call.</param>
    /// <returns>The call's result.</returns>
    public T OnUiThread<T>(Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using (Context.Install())
        {
            return call();
        }
    }

    /// <summary>Pumps until <paramref name="task"/> completes, then rethrows its exception, if any.</summary>
    /// <param name="task">The task.</param>
    /// <param name="timeout">The real-time bound; <see cref="DefaultWait"/> when <see langword="null"/>.</param>
    public void AwaitTask(Task task, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        RunUntil(() => task.IsCompleted, timeout);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Pumps until <paramref name="task"/> completes, then returns its result or rethrows its exception.</summary>
    /// <typeparam name="T">The task's result type.</typeparam>
    /// <param name="task">The task.</param>
    /// <param name="timeout">The real-time bound; <see cref="DefaultWait"/> when <see langword="null"/>.</param>
    /// <returns>The task's result.</returns>
    public T AwaitResult<T>(Task<T> task, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        RunUntil(() => task.IsCompleted, timeout);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs <see cref="RunController.InitializeAsync"/> to completion. Synchronous, because it pumps until the
    /// controller's task completes, so it carries no Async suffix: that suffix is reserved for awaitable methods.
    /// </summary>
    public void Initialize() => AwaitTask(OnUiThread(Controller.InitializeAsync));

    /// <summary>Calls <see cref="RunController.TryStart(TestType, bool)"/> once, without pumping.</summary>
    /// <param name="testType">The test type.</param>
    /// <param name="stopOnError">Whether the run stops at the first error.</param>
    /// <returns>The controller's answer.</returns>
    public StartResult TryStart(TestType testType = TestType.Quick, bool stopOnError = true) =>
        OnUiThread(() => Controller.TryStart(testType, stopOnError));

    /// <summary>
    /// Starts a run that must be accepted and pumps until its new session is started and the state is Preparing.
    /// </summary>
    /// <param name="testType">The test type.</param>
    /// <param name="stopOnError">Whether the run stops at the first error.</param>
    /// <returns>The run's session.</returns>
    public FakeEngineSession StartAccepted(TestType testType = TestType.Quick, bool stopOnError = true)
    {
        int sessionsBefore = Engine.Sessions.Count;
        Assert.Equal(StartResult.Accepted, TryStart(testType, stopOnError));
        RunUntil(() => Engine.Sessions.Count > sessionsBefore
            && Session.IsStarted
            && StateMachine.State == RunState.Preparing);
        return Session;
    }

    /// <summary>Raises Prepared and pumps until the run is Running.</summary>
    /// <param name="instructionSet">The kernels' instruction set; the machine's best when <see langword="null"/>.</param>
    /// <returns>The payload raised.</returns>
    public EnginePrepared Prepare(InstructionSet? instructionSet = null)
    {
        EnginePrepared prepared = Session.RaisePrepared(instructionSet: instructionSet);
        RunUntil(() => StateMachine.State == RunState.Running);
        return prepared;
    }

    /// <summary>Raises Completed and pumps until the verdict is shown; see <see cref="AwaitVerdict"/>.</summary>
    /// <param name="endReason">The engine's end reason.</param>
    /// <param name="totalErrors">The true error total.</param>
    /// <param name="osError">The Windows error of a failing call, or 0.</param>
    /// <param name="endDetail">The engine's diagnostic text, or <see langword="null"/>.</param>
    /// <returns>The payload raised.</returns>
    public EngineCompleted Complete(RunEndReason endReason, ulong totalErrors = 0, uint osError = 0, string? endDetail = null)
    {
        EngineCompleted completed = Session.RaiseCompleted(endReason, totalErrors, osError: osError, endDetail: endDetail);
        AwaitVerdict();
        return completed;
    }

    /// <summary>
    /// Pumps until the run is in a terminal state and the controller has finished concluding it: the verdict and
    /// hints are shown and the <c>result</c> record has been written, so the controller is releasing memory, saving
    /// results, or already done.
    /// </summary>
    public void AwaitVerdict() =>
        RunUntil(() => StateMachine.IsTerminal
            && (Controller.PostRunActivity != PostRunActivity.None || Controller.CanStart));

    /// <summary>Raises Released for the whole region and pumps until the summaries are published.</summary>
    /// <param name="releaseTime">The release time the engine reports.</param>
    public void Release(TimeSpan releaseTime)
    {
        Session.RaiseReleased(Session.Options.RegionBytes, releaseTime);
        WaitForPublication();
    }

    /// <summary>
    /// Pumps until the last run's summaries are published and its session disposed, so Start is offered again.
    /// </summary>
    public void WaitForPublication() =>
        RunUntil(() => Controller.CanStart && Controller.PostRunActivity == PostRunActivity.None);

    /// <summary>Starts a run, prepares it and completes it, leaving the controller waiting for Released.</summary>
    /// <param name="endReason">The engine's end reason.</param>
    /// <param name="totalErrors">The true error total.</param>
    /// <param name="testType">The test type.</param>
    /// <param name="stopOnError">Whether the run stops at the first error.</param>
    /// <returns>The run's session.</returns>
    public FakeEngineSession RunToVerdict(
        RunEndReason endReason = RunEndReason.Completed,
        ulong totalErrors = 0,
        TestType testType = TestType.Quick,
        bool stopOnError = true)
    {
        FakeEngineSession session = StartAccepted(testType, stopOnError);
        Prepare();
        Complete(endReason, totalErrors);
        return session;
    }

    /// <summary>Calls <see cref="RunController.ShutdownAsync"/> with <see cref="Context"/> installed, without pumping.</summary>
    /// <returns>The shutdown task.</returns>
    public Task Shutdown() => OnUiThread(Controller.ShutdownAsync);

    /// <summary>
    /// Starts the sensor monitor on <see cref="Context"/> and pumps until its first snapshot reached the controller.
    /// </summary>
    public void StartSensors()
    {
        _ = Controller;
        Monitor.Start(Context);
        RunUntil(() => ReadingsDelivered >= 1);
    }

    /// <summary>Advances <see cref="Time"/> by one poll interval and pumps until the next snapshot reached the controller.</summary>
    public void NextReading()
    {
        int delivered = ReadingsDelivered;
        Time.Advance(SensorMonitor.PollInterval);
        RunUntil(() => ReadingsDelivered > delivered);
    }

    /// <summary>
    /// Runs the step gate of step <paramref name="index"/> on a dedicated background thread, as the engine's
    /// coordinator does, and pumps until it returns.
    /// </summary>
    /// <param name="index">The zero-based step index.</param>
    /// <param name="timeout">The real-time bound; <see cref="DefaultWait"/> when <see langword="null"/>.</param>
    /// <returns>The decision and the real time the gate took.</returns>
    public (StepGateDecision Decision, TimeSpan Elapsed) RunGate(int index, TimeSpan? timeout = null)
    {
        long startedAt = Stopwatch.GetTimestamp();
        return AwaitGate(Session.InvokeStepGateOnBackgroundThread(index), startedAt, timeout);
    }

    /// <summary>Pumps until a gate started on a background thread returns.</summary>
    /// <param name="gate">The gate's task.</param>
    /// <param name="startedAt">The <see cref="Stopwatch"/> timestamp the elapsed time is measured from.</param>
    /// <param name="timeout">The real-time bound; <see cref="DefaultWait"/> when <see langword="null"/>.</param>
    /// <returns>The decision and the real time from <paramref name="startedAt"/> to its return.</returns>
    public (StepGateDecision Decision, TimeSpan Elapsed) AwaitGate(Task<StepGateDecision> gate, long startedAt, TimeSpan? timeout = null)
    {
        StepGateDecision decision = AwaitResult(gate, timeout);
        return (decision, Stopwatch.GetElapsedTime(startedAt));
    }

    /// <summary>Returns the path of every JSON summary in the logs folder, recovered ones included.</summary>
    /// <returns>The paths, in no particular order.</returns>
    public IReadOnlyList<string> SummaryJsonPaths() => FilesInLogs("*-summary.json");

    /// <summary>Returns the path of the logs folder's single JSON summary.</summary>
    /// <returns>The path.</returns>
    public string SummaryJsonPath() => Assert.Single(SummaryJsonPaths());

    /// <summary>Parses the logs folder's single JSON summary.</summary>
    /// <returns>The document; the caller disposes it.</returns>
    public JsonDocument ReadSummary() => JsonDocument.Parse(ReadShared(SummaryJsonPath()));

    /// <summary>Returns the text of the logs folder's single run <c>.log</c>.</summary>
    /// <returns>The log text.</returns>
    public string LogText() => ReadShared(Assert.Single(RunLogPaths()));

    /// <summary>Returns the path of every run <c>.log</c> in the logs folder; <c>app-errors.log</c> is not one.</summary>
    /// <returns>The paths.</returns>
    public IReadOnlyList<string> RunLogPaths() =>
        FilesInLogs("*.log")
            .Where(path => !string.Equals(path, RequiredData.AppErrorsLogPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Returns the text of <c>app-errors.log</c>, or an empty string while it does not exist.</summary>
    /// <returns>The text.</returns>
    public string AppErrorsLogText()
    {
        string path = RequiredData.AppErrorsLogPath;
        try
        {
            return File.Exists(path) ? ReadShared(path) : string.Empty;
        }
        catch (IOException)
        {
            // Being appended to on the thread pool right now; the caller polls again.
            return string.Empty;
        }
    }

    /// <summary>Appends <paramref name="records"/> to the file journal, as a crashed run would have left them.</summary>
    /// <param name="records">The records, each already stamped with its time.</param>
    public void WriteJournal(params JournalRecord[] records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var storage = new FileJournalStorage(RequiredData.JournalPath);
        foreach (JournalRecord record in records)
        {
            storage.AppendDurable(JournalLineSerializer.Serialize(record));
        }
    }

    /// <summary>Reads the run's journal records, from <see cref="Storage"/> or from the file journal.</summary>
    /// <returns>The records that parse, in journal order.</returns>
    public IReadOnlyList<JournalRecord> ReadJournal()
    {
        if (Storage is { } storage)
        {
            return JournalLineSerializer.ParseAll(storage.ReadLines());
        }

        using var file = new FileJournalStorage(RequiredData.JournalPath);
        return JournalLineSerializer.ParseAll(file.ReadLines());
    }

    /// <summary>
    /// Releases everything the test left behind: blocked journal appends, the controller, its journals, the sensor
    /// monitor and source, and the temporary folder.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A blocked append would otherwise hold the journal writer, and with it the journal's disposal.
        Storage?.ClearRules();
        Storage?.Release();

        _controller?.Dispose();
        foreach (ProgressJournal journal in Journals)
        {
            journal.Dispose();
        }

        Monitor.Dispose();
        Sensors.Dispose();
        Storage?.Dispose();

        // A test that installed the context and failed before disposing its scope leaves it on this thread.
        if (ReferenceEquals(SynchronizationContext.Current, Context))
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        DeleteRoot();
    }

    private DataDirectory RequiredData =>
        Data ?? throw new InvalidOperationException("The harness was configured without a data directory.");

    private static RunControllerOptions CopyOptions(
        RunControllerOptions options,
        Func<TestType, TestPlan>? planProvider = null,
        Func<DataDirectory, TimeProvider, ProgressJournal>? journalFactory = null) => new()
        {
            PlanProvider = planProvider ?? options.PlanProvider,
            MaxRegionBytes = options.MaxRegionBytes,
            JournalFactory = journalFactory ?? options.JournalFactory,
            PublicationFallback = options.PublicationFallback,
            ShutdownCompletedTimeout = options.ShutdownCompletedTimeout,
            ShutdownReleasedTimeout = options.ShutdownReleasedTimeout,
            BeginTimeout = options.BeginTimeout,
        };

    // The engine, the journal writer and the results log write these files from other threads.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private IReadOnlyList<string> FilesInLogs(string pattern)
    {
        string logs = RequiredData.LogsDirectory;
        return Directory.Exists(logs) ? Directory.GetFiles(logs, pattern) : Array.Empty<string>();
    }

    private ProgressJournal Capture(ProgressJournal journal)
    {
        lock (_journals)
        {
            _journals.Add(journal);
        }

        return journal;
    }

    private void ThrowIfBuilt()
    {
        if (_controller is not null)
        {
            throw new InvalidOperationException("Configure the harness before the controller is built.");
        }
    }

    // The thread pool may still be appending to app-errors.log, and a results log may still be draining.
    private void DeleteRoot()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }

                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < DeleteAttempts)
            {
                Thread.Sleep(_deleteRetryDelay);
            }
        }
    }
}


/// <summary>
/// Tests for <see cref="RunController"/>: initialisation and every refused Start, the elevation offer, the run flow
/// from Start to published summaries, the grouping of stored errors (with <see cref="IncrementalErrorGrouping"/>),
/// stops and terminal precedence, the thermal limits, the step gate and journal
/// failures, crash recovery at launch, and shutdown. Every displayed message is compared with its
/// <see cref="UiText"/> member.
/// </summary>
public sealed class RunControllerTests
{
    private const ulong GiB = FakeEngineData.GiB;
    private const ulong MiB = FakeEngineData.MiB;

    // The region-offset distance FakeEngineData.Errors puts between consecutive records by default, so word n of a
    // test lies at n × WordSpacing and has its own 8-byte word.
    private const ulong WordSpacing = 4_096;

    // Real-time bounds: the step gate's journal wait is real time, and Stop must release it within 2 s.
    private static readonly TimeSpan _stopBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _gateTimeoutWait = TimeSpan.FromSeconds(15);

    // The journal-acknowledgment bound measured with a margin below it for scheduling jitter.
    private static readonly TimeSpan _gateTimeoutFloor = ProgressJournal.AcknowledgmentTimeout - TimeSpan.FromMilliseconds(250);

    // Real-time ceilings of the teardown calls: Dispose never waits, and a fatal error waits at most 3 s, here with a
    // margin for scheduling jitter.
    private static readonly TimeSpan _disposeReturnCeiling = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _fatalReturnCeiling = TimeSpan.FromSeconds(5);

    // Long enough for a gate that ignored the events still queued before it to journal its step and return.
    private static readonly TimeSpan _undeliveredEventsWindow = TimeSpan.FromMilliseconds(300);

    private static readonly IReadOnlyList<StepAttributes> _quickSteps = TestPlans.Quick.ToStepAttributes();

    // ---------------------------------------------------------------------------------------------
    // Initialisation and refused Starts
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InstructionSet.Avx512, false)]
    [InlineData(InstructionSet.Avx2, false)]
    [InlineData(InstructionSet.Sse2, true)]
    public void InitializeAsync_Elevated_PublishesMachineAndInventoryAndOffersStart(InstructionSet bestIsa, bool reduced)
    {
        using var harness = new ControllerHarness();
        harness.Engine.WithIsa(bestIsa);
        var inventory = new MemoryInventory(64 * GiB, 4, 6400, 2, MemoryType.DDR5);
        harness.Inventory.Inventory = inventory;

        Assert.False(harness.Controller.CanStart);
        harness.Initialize();

        Assert.Equal(harness.Engine.System, harness.Controller.System);
        Assert.Equal(inventory, harness.Controller.Inventory);
        Assert.Equal(reduced, harness.Controller.IsReducedInstructionSet);
        Assert.True(harness.Controller.IsElevated);
        Assert.True(harness.Controller.CanStart);
        Assert.Equal(PostRunActivity.None, harness.Controller.PostRunActivity);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Prompt.AskCount);
        Assert.Null(harness.Controller.ResultMessage);
        Assert.Null(harness.Controller.NoticeMessage);
    }

    [Fact]
    public void InitializeAsync_ProbesThrow_LogsAppErrorsAndStillOffersStart()
    {
        using var harness = new ControllerHarness();
        harness.Engine.QuerySystemException = new InvalidOperationException("system probe failed");
        harness.Inventory.ReadException = new InvalidOperationException("inventory probe failed");

        harness.Initialize();

        Assert.Null(harness.Controller.System);
        Assert.Equal(MemoryInventory.Unknown, harness.Controller.Inventory);
        Assert.False(harness.Controller.IsReducedInstructionSet);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("system probe failed", StringComparison.Ordinal)
            && harness.AppErrorsLogText().Contains("inventory probe failed", StringComparison.Ordinal));
    }

    [Fact]
    public void InitializeAsync_CalledTwice_SecondCallFails()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();

        Task second = harness.OnUiThread(harness.Controller.InitializeAsync);

        Assert.True(second.IsFaulted);
        Assert.IsType<InvalidOperationException>(second.Exception!.InnerException);
    }

    [Fact]
    public void TryStart_BeforeInitialization_IsBusy()
    {
        using var harness = new ControllerHarness();

        Assert.Equal(StartResult.Busy, harness.TryStart());

        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
    }

    [Fact]
    public void TryStart_LessThanOneGibibytePlannable_RefusesWithStateUnchanged()
    {
        using var harness = new ControllerHarness();
        harness.Engine.WithAvailableBytes(3 * GiB / 2).WithLogicalProcessors(8);
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;
        harness.Initialize();

        // min(0.90 × 1.5 GiB, 1.5 GiB − 1 GiB) plans 0.5 GiB, below the 1 GiB floor.
        Assert.Equal(StartResult.InsufficientMemory, harness.TryStart());
        harness.Pump();

        Assert.Equal(UiText.LowMemoryMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
        Assert.Empty(harness.RunLogPaths());
        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
    }

    [Fact]
    public void TryStart_BothDataLocationsUnwritable_RefusesAsUnusableDataFolder()
    {
        using var harness = new ControllerHarness();

        // Files stand where both data folders would be created, so neither probe can succeed.
        string appFolder = Path.Combine(harness.RootPath, "app-folder-file");
        string localAppData = Path.Combine(harness.RootPath, "local-app-data-file");
        File.WriteAllText(appFolder, string.Empty);
        File.WriteAllText(localAppData, string.Empty);
        DataDirectory? resolved = DataDirectory.Resolve(appFolder, localAppData);
        Assert.Null(resolved);
        harness.WithoutDataDirectory();
        harness.Initialize();

        Assert.Equal(StartResult.DataFolderUnusable, harness.TryStart());

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
        Assert.True(harness.Controller.CanStart);
    }

    [Fact]
    public void TryStart_DataFolderBecameUnwritable_RefusesAfterProbeWithStateUnchanged()
    {
        using var harness = new ControllerHarness();
        harness.WithUnwritableDataDirectory();
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;
        harness.Initialize();

        // The probe runs asynchronously, so the Start is accepted first and withdrawn once the probe fails.
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.False(harness.Controller.CanStart);
        harness.RunUntil(() => harness.Controller.CanStart);

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
    }

    [Fact]
    public void TryStart_BeginRecordNotDurableWithinBound_RefusesAsUnusableDataFolder()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        storage.BlockNext("begin");
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;
        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.RunUntil(() => storage.BlockedCount == 1);
        Assert.False(harness.Controller.CanStart);

        // The 5 s bound is measured on the controller's clock.
        harness.Time.Advance(harness.Options.BeginTimeout);
        harness.RunUntil(() => harness.Controller.CanStart);

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);

        // The name the refused Start reserved is given back.
        Assert.Empty(harness.RunLogPaths());
    }

    [Fact]
    public void TryStart_BeginNotDurableWithinShorterControllerBound_RefusesBeforeJournalGivesUp()
    {
        using var harness = new ControllerHarness();
        harness.Options = new RunControllerOptions { BeginTimeout = TimeSpan.FromSeconds(2) };
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        storage.BlockNext("begin");
        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.RunUntil(() => storage.BlockedCount == 1);
        harness.Time.Advance(harness.Options.BeginTimeout);
        harness.RunUntil(() => harness.Controller.NoticeMessage == UiText.DataFolderUnusableMessage);

        // Start is offered again only once the journal has given up on its begin record too.
        Assert.False(harness.Controller.CanStart);
        harness.Time.Advance(ProgressJournal.AcknowledgmentTimeout - harness.Options.BeginTimeout);
        harness.RunUntil(() => harness.Controller.CanStart);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
    }

    [Fact]
    public void TryStart_JournalCannotBeCreated_RefusesAsUnusableDataFolder()
    {
        using var harness = new ControllerHarness();
        harness.Options = new RunControllerOptions
        {
            JournalFactory = (_, _) => throw new IOException("the journal folder vanished"),
        };
        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.RunUntil(() => harness.Controller.CanStart);

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.RunLogPaths());
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("the journal folder vanished", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(RunControllerOptions.PlanProvider))]
    [InlineData(nameof(RunControllerOptions.JournalFactory))]
    [InlineData(nameof(RunControllerOptions.MaxRegionBytes))]
    [InlineData(nameof(RunControllerOptions.PublicationFallback))]
    [InlineData(nameof(RunControllerOptions.ShutdownCompletedTimeout))]
    [InlineData(nameof(RunControllerOptions.ShutdownReleasedTimeout))]
    [InlineData(nameof(RunControllerOptions.BeginTimeout))]
    public void Constructor_InvalidOption_ThrowsNamingTheOption(string option)
    {
        using var harness = new ControllerHarness();
        TimeSpan negative = TimeSpan.FromSeconds(-1);
        RunControllerOptions options = option switch
        {
            nameof(RunControllerOptions.PlanProvider) => new() { PlanProvider = null! },
            nameof(RunControllerOptions.JournalFactory) => new() { JournalFactory = null! },
            nameof(RunControllerOptions.MaxRegionBytes) => new() { MaxRegionBytes = 0 },
            nameof(RunControllerOptions.PublicationFallback) => new() { PublicationFallback = negative },
            nameof(RunControllerOptions.ShutdownCompletedTimeout) => new() { ShutdownCompletedTimeout = negative },
            nameof(RunControllerOptions.ShutdownReleasedTimeout) => new() { ShutdownReleasedTimeout = negative },
            _ => new() { BeginTimeout = negative },
        };

        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new RunController(
            harness.Engine,
            harness.StateMachine,
            harness.Monitor,
            harness.Inventory,
            harness.Elevation,
            harness.Prompt,
            harness.Time,
            harness.Data,
            options));

        Assert.Equal(option, exception.ParamName);
    }


    [Fact]
    public void TryStart_WhilePendingRunningOrPublishing_IsBusy()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.Busy, harness.TryStart());
        harness.RunUntil(() => harness.StateMachine.State == RunState.Preparing && harness.Engine.Sessions.Count == 1);
        Assert.Equal(StartResult.Busy, harness.TryStart());

        harness.Prepare();
        Assert.Equal(StartResult.Busy, harness.TryStart());

        harness.Complete(RunEndReason.Completed);
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        Assert.Equal(StartResult.Busy, harness.TryStart());

        harness.Release(TimeSpan.FromMilliseconds(100));
        Assert.Single(harness.Engine.Sessions);
        harness.StartAccepted();
        Assert.Equal(2, harness.Engine.Sessions.Count);
    }

    [Fact]
    public void TryStart_EngineCannotDescribeMachine_RefusesWithEngineError()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        harness.Engine.QuerySystemException = new EngineException(EngineResultCode.Internal, "rq_query_system failed");

        Assert.Equal(StartResult.EngineUnavailable, harness.TryStart());

        Assert.Equal(UiText.EngineError("rq_query_system failed"), harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("rq_query_system failed", StringComparison.Ordinal));
    }

    [Fact]
    public void TryStart_JournalLeftByFailedPublication_PublishesItBeforeStarting()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(CompletedJournal(runId, baseName));

        FakeEngineSession session = harness.StartAccepted();

        Assert.Equal(UiText.RecoveredNotice, harness.Controller.NoticeMessage);
        Assert.True(File.Exists(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName)));
        Assert.True(File.Exists(ResultsLog.GetSummaryTextPath(harness.Data!, baseName)));
        Assert.True(session.IsStarted);
        JournalBegin begin = Assert.IsType<JournalBegin>(harness.ReadJournal()[0]);
        Assert.NotEqual(runId, begin.RunId);
    }


    // ---------------------------------------------------------------------------------------------
    // Elevation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NotElevated_UserDeclines_PromptsAtLaunchAndOnStartWithoutStarting()
    {
        using var harness = new ControllerHarness();
        harness.Elevation.IsElevated = false;
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;

        harness.Initialize();

        PromptCall launchPrompt = Assert.Single(harness.Prompt.AskCalls);
        Assert.Equal(UiText.ElevationPromptTitle, launchPrompt.Title);
        Assert.Equal(UiText.ElevationPromptMessage, launchPrompt.Message);
        Assert.False(harness.Controller.IsElevated);
        Assert.True(harness.Controller.CanStart);
        Assert.Equal(0, harness.Elevation.RestartCallCount);

        Assert.Equal(StartResult.NotElevated, harness.TryStart());

        Assert.Equal(2, harness.Prompt.AskCount);
        PromptCall startPrompt = harness.Prompt.AskCalls[1];
        Assert.Equal(UiText.ElevationPromptTitle, startPrompt.Title);
        Assert.Equal(UiText.ElevationPromptMessage, startPrompt.Message);
        Assert.Equal(0, harness.Elevation.RestartCallCount);
        Assert.Equal(0, harness.ShutdownRequestedCount);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.True(harness.Controller.CanStart);
    }

    [Fact]
    public void TryStart_NotElevatedAndRestartStarted_RequestsShutdown()
    {
        using var harness = new ControllerHarness();
        harness.Elevation.IsElevated = false;
        harness.Elevation.RestartResult = true;
        harness.Prompt.EnqueueAnswer(PromptResult.No);
        harness.Prompt.Answer = PromptResult.Yes;
        harness.Initialize();
        Assert.Equal(0, harness.ShutdownRequestedCount);

        Assert.Equal(StartResult.RestartingElevated, harness.TryStart());

        Assert.Equal(1, harness.Elevation.RestartCallCount);
        Assert.Equal(1, harness.ShutdownRequestedCount);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
    }

    [Fact]
    public void InitializeAsync_NotElevatedAndRestartStarted_LeavesRecoveryToElevatedInstance()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 2));
        harness.Elevation.IsElevated = false;
        harness.Elevation.RestartResult = true;
        harness.Prompt.Answer = PromptResult.Yes;

        harness.Initialize();

        Assert.Equal(1, harness.Elevation.RestartCallCount);
        Assert.Equal(1, harness.ShutdownRequestedCount);
        Assert.True(harness.JournalExists);
        Assert.Empty(harness.SummaryJsonPaths());
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Null(harness.Controller.ResultMessage);
        Assert.False(harness.Controller.CanStart);
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }

    [Fact]
    public void TryStart_NotElevatedAndUacDeclined_StaysNotElevated()
    {
        using var harness = new ControllerHarness();
        harness.Elevation.IsElevated = false;
        harness.Elevation.RestartResult = false;
        harness.Prompt.Answer = PromptResult.Yes;
        harness.Initialize();
        Assert.Equal(1, harness.Elevation.RestartCallCount);
        Assert.True(harness.Controller.CanStart);

        Assert.Equal(StartResult.NotElevated, harness.TryStart());

        Assert.Equal(2, harness.Elevation.RestartCallCount);
        Assert.Equal(0, harness.ShutdownRequestedCount);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Run flow, publication and release
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TestType.Quick, true)]
    [InlineData(TestType.Standard, false)]
    [InlineData(TestType.Exhaustive, true)]
    public void TryStart_Accepted_ConfiguresSessionFromSizingAndPlan(TestType testType, bool stopOnError)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();

        FakeEngineSession session = harness.StartAccepted(testType, stopOnError);

        SystemSnapshot system = harness.Engine.System;
        TestPlan plan = TestPlans.Get(testType);
        EngineSessionOptions options = session.Options;
        Assert.Equal(MemorySizing.Plan(system.AvailablePhysicalBytes, system.LogicalProcessors).PlannedBytes, options.RegionBytes);
        Assert.Equal(MemorySizing.MinimumRegionBytes, options.MinRegionBytes);
        Assert.Equal(system.LogicalProcessors, options.WorkerCount);
        Assert.Equal(plan.ToStepAttributes(), options.Steps);
        Assert.Equal(stopOnError, options.StopOnError);
        Assert.NotNull(options.StepGate);
        Assert.NotNull(options.ProgressSink);
        Assert.NotNull(options.CallbackFault);
        Assert.Same(plan, harness.Controller.ActivePlan);
        Assert.False(harness.Controller.CanStart);
        Assert.Null(harness.Controller.TestedBytes);

        Assert.True(harness.JournalExists);
        JournalBegin begin = Assert.IsType<JournalBegin>(Assert.Single(harness.ReadJournal()));
        Assert.Equal(testType.ToString(), begin.TestType);
        Assert.Equal(stopOnError, begin.StopOnError);
        Assert.Equal(options.Steps.Count, begin.Steps.Count);
        Assert.Equal(options.RegionBytes, begin.PlannedBytes);
        Assert.Single(harness.RunLogPaths());
    }

    [Fact]
    public void TryStart_RegionCap_ScalesRegionButJudgesSufficiencyOnUncappedPlan()
    {
        // 261 MiB is not a whole number of 8 × 2 MiB slices, so the cap rounds down to 256 MiB.
        using var harness = new ControllerHarness();
        harness.UseMaxRegionBytes(261 * MiB);
        harness.Initialize();

        FakeEngineSession session = harness.StartAccepted();

        Assert.Equal(256 * MiB, session.Options.RegionBytes);
        Assert.Equal(256 * MiB, session.Options.MinRegionBytes);
        JournalBegin begin = Assert.IsType<JournalBegin>(Assert.Single(harness.ReadJournal()));
        Assert.Equal(
            MemorySizing.Plan(harness.Engine.System.AvailablePhysicalBytes, harness.Engine.System.LogicalProcessors).PlannedBytes,
            begin.PlannedBytes);
    }

    [Fact]
    public void TryStart_RegionCapOnInsufficientMemory_StillRefuses()
    {
        using var harness = new ControllerHarness();
        harness.UseMaxRegionBytes(256 * MiB);
        harness.Engine.WithAvailableBytes(3 * GiB / 2);
        harness.Initialize();

        Assert.Equal(StartResult.InsufficientMemory, harness.TryStart());

        Assert.Equal(UiText.LowMemoryMessage, harness.Controller.NoticeMessage);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
    }

    [Fact]
    public void Completed_CleanRun_ShowsVerdictAtOnceAndPublishesOnlyAfterReleased()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        EnginePrepared prepared = harness.Prepare();
        Assert.Equal(prepared.TestedBytes, harness.Controller.TestedBytes);
        for (int index = 0; index < session.Options.Steps.Count; index++)
        {
            session.RaiseStepStarted(index);
            session.RaiseStepFinished(index);
        }

        harness.Complete(RunEndReason.Completed);

        Assert.Equal(RunState.Passed, harness.StateMachine.State);
        Assert.Equal(UiText.PassedReason, harness.Controller.ResultMessage);
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        Assert.False(harness.Controller.CanStart);
        Assert.True(harness.Controller.CauseHints.IsEmpty);
        Assert.Empty(harness.SummaryJsonPaths());
        Assert.True(harness.JournalExists);
        Assert.False(session.IsDisposed);
        Assert.Equal(string.Empty, Assert.Single(harness.ReadJournal().OfType<JournalResult>()).EndDetail);

        harness.Release(TimeSpan.FromMilliseconds(1250));

        using JsonDocument summary = harness.ReadSummary();
        JsonElement root = summary.RootElement;
        Assert.Equal(1.25, root.GetProperty("memory").GetProperty("releaseSeconds").GetDouble());
        Assert.Equal("PASS", root.GetProperty("run").GetProperty("verdict").GetString());
        Assert.Equal(nameof(RunEndReason.Completed), root.GetProperty("run").GetProperty("endReason").GetString());
        Assert.Equal(string.Empty, root.GetProperty("run").GetProperty("endDetail").GetString());
        Assert.Equal(session.Options.Steps.Count, root.GetProperty("steps").GetArrayLength());
        Assert.True(File.Exists(TextSummaryPathOf(harness.SummaryJsonPath())));
        Assert.DoesNotContain("\r\nDetail:", File.ReadAllText(TextSummaryPathOf(harness.SummaryJsonPath())), StringComparison.Ordinal);
        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        Assert.Equal(PostRunActivity.None, harness.Controller.PostRunActivity);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(RunState.Passed, harness.StateMachine.State);
    }

    [Fact]
    public void Released_NotWithinFallback_PublishesWithUnconfirmedRelease()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.RunToVerdict();
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);

        harness.Time.Advance(harness.Options.PublicationFallback);
        harness.WaitForPublication();

        using JsonDocument summary = harness.ReadSummary();
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("memory").GetProperty("releaseSeconds").ValueKind);
        Assert.Equal("PASS", summary.RootElement.GetProperty("run").GetProperty("verdict").GetString());

        // ResultsLog's wording is internal to RamQuick.Logging; this is the wording the results-log contract names.
        Assert.Contains("memory release not confirmed", harness.LogText(), StringComparison.Ordinal);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(harness.JournalExists);
    }

    [Theory]
    [InlineData(RunEndReason.Completed, true, RunState.Passed)]
    [InlineData(RunEndReason.UserStop, true, RunState.Aborted)]
    [InlineData(RunEndReason.FirstError, true, RunState.Failed)]
    [InlineData(RunEndReason.Thermal, true, RunState.Aborted)]
    [InlineData(RunEndReason.AffinityFailed, false, RunState.Aborted)]
    [InlineData(RunEndReason.HostFault, true, RunState.Aborted)]
    [InlineData(RunEndReason.EngineFault, true, RunState.Aborted)]
    [InlineData(RunEndReason.InsufficientMemory, false, RunState.Aborted)]
    public void Run_AnyEndReason_DisposesSessionExactlyOnceAfterPublication(RunEndReason endReason, bool prepared, RunState expected)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        if (prepared)
        {
            harness.Prepare();
        }

        harness.Complete(endReason, totalErrors: endReason == RunEndReason.FirstError ? 1UL : 0UL);
        Assert.Equal(expected, harness.StateMachine.State);
        Assert.Equal(endReason, harness.StateMachine.EndReason);
        Assert.Equal(0, session.DisposeCount);

        harness.Release(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, session.DisposeCount);
        using JsonDocument summary = harness.ReadSummary();
        Assert.Equal(expected.ToVerdictText(), summary.RootElement.GetProperty("run").GetProperty("verdict").GetString());
        Assert.Equal(endReason.ToString(), summary.RootElement.GetProperty("run").GetProperty("endReason").GetString());
        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
    }

    [Fact]
    public void Completed_LockShortfallBeforePrepared_AbortsWithLowMemoryMessage()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        harness.StartAccepted();

        harness.Complete(RunEndReason.InsufficientMemory, osError: 1453, endDetail: "VirtualLock failed at 1 GiB");

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.InsufficientMemory, harness.StateMachine.EndReason);
        Assert.Equal(UiText.LowMemoryMessage, harness.Controller.ResultMessage);
        Assert.Null(harness.Controller.TestedBytes);
    }

    [Fact]
    public void Completed_AffinityFailure_AbortsWithWindowsError()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        harness.StartAccepted();

        harness.Complete(RunEndReason.AffinityFailed, osError: 87, endDetail: "SetThreadGroupAffinity failed: group 1, processor 12");

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.AffinityFailed, harness.StateMachine.EndReason);
        Assert.Equal(UiText.AffinityFailed(87), harness.Controller.ResultMessage);
        Assert.Equal("Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12", harness.StateMachine.EndDetail);
    }

    [Fact]
    public void Completed_CleanRunBeforePrepared_EndsAsEngineFault()
    {
        // A PASS cannot follow Preparing, so an engine that breaks its event order still ends the run.
        using var harness = new ControllerHarness();
        harness.Initialize();
        harness.StartAccepted();

        harness.Complete(RunEndReason.Completed);

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.EngineError(nameof(RunEndReason.Completed)), harness.Controller.ResultMessage);
    }

    [Fact]
    public void Publication_FinalSummaryNameTaken_ShowsSaveFailureKeepsJournalAndRetriesAtNextStart()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        harness.RunToVerdict();
        string baseName = Path.GetFileNameWithoutExtension(Assert.Single(harness.RunLogPaths()));
        string takenJsonPath = ResultsLog.GetSummaryJsonPath(harness.Data!, baseName);
        File.WriteAllText(takenJsonPath, "{}");

        harness.Release(TimeSpan.FromMilliseconds(100));

        Assert.Equal(RunState.Passed, harness.StateMachine.State);
        Assert.Equal(UiText.PassedReason, harness.Controller.ResultMessage);
        Assert.StartsWith(UiText.ResultsNotSaved(string.Empty), harness.Controller.NoticeMessage, StringComparison.Ordinal);
        Assert.True(harness.JournalExists);
        Assert.Equal("{}", File.ReadAllText(takenJsonPath));

        // The kept journal is published under the recovered name before the next run may reuse the journal.
        harness.StartAccepted();
        Assert.Equal(UiText.RecoveredNotice, harness.Controller.NoticeMessage);
        Assert.True(File.Exists(ResultsLog.GetRecoveredSummaryJsonPath(harness.Data!, baseName)));
        Assert.Equal("{}", File.ReadAllText(takenJsonPath));
    }

    // The .log is made to fail mid-run, with no access-control change, by a byte-range lock held through a second
    // handle: the drain's writes then fail with a lock violation. The run goes on, the failure is reported once, and
    // the final notice says how much of the results was saved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventLog_WriteFailsMidRun_ReportedOnceAndFinalNoticeTellsWhatWasSaved(bool publicationFails)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        string logPath = Assert.Single(harness.RunLogPaths());
        string baseName = Path.GetFileNameWithoutExtension(logPath);
        StepAttributes step = session.Options.Steps[0];
        RunProgress progress = FakeEngineData.Progress(
            step.Index, session.Options.Steps.Count, step.Name, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(290), GiB, session.Options.RegionBytes, 0);
        using var blocker = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        blocker.Lock(0, long.MaxValue);
        try
        {
            // Every tenth simulated second logs a PROGRESS line; the drain fails to write it, and the next Progress
            // event finds the failure.
            Stopwatch waited = Stopwatch.StartNew();
            for (int second = 11; harness.Controller.NoticeMessage is null; second++)
            {
                Assert.True(waited.Elapsed < ControllerHarness.DefaultWait, "The event log's write failure was never reported.");
                session.PublishProgress(progress with { Elapsed = TimeSpan.FromSeconds(second) });
                harness.Pump();
                Thread.Sleep(10);
            }

            Assert.StartsWith(UiText.EventLogNotSaved(string.Empty), harness.Controller.NoticeMessage, StringComparison.Ordinal);
            Assert.Empty(session.StopRequests);
            harness.RunUntil(() => harness.AppErrorsLogText().Contains("source=\"RunController.ResultsLog\"", StringComparison.Ordinal));

            if (publicationFails)
            {
                File.WriteAllText(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName), "{}");
            }

            harness.Complete(RunEndReason.Completed);
            harness.Release(TimeSpan.FromMilliseconds(100));

            Assert.Equal(RunState.Passed, harness.StateMachine.State);
            Assert.Equal(UiText.PassedReason, harness.Controller.ResultMessage);
            Assert.True(File.Exists(ResultsLog.GetSummaryTextPath(harness.Data!, baseName)));
            string notice = harness.Controller.NoticeMessage!;
            if (publicationFails)
            {
                Assert.StartsWith(UiText.ResultsNotSaved(string.Empty), notice, StringComparison.Ordinal);
                Assert.Contains(UiText.NoticeSeparator + UiText.EventLogNotSaved(string.Empty), notice, StringComparison.Ordinal);
                Assert.True(harness.JournalExists);
            }
            else
            {
                Assert.True(File.Exists(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName)));
                Assert.StartsWith(UiText.ResultsSavedWithoutEventLog(string.Empty), notice, StringComparison.Ordinal);
                Assert.False(harness.JournalExists);
            }

            Assert.Equal(1, CountOf(harness.AppErrorsLogText(), "source=\"RunController.ResultsLog\""));
        }
        finally
        {
            blocker.Unlock(0, long.MaxValue);
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionStartup_EngineException_AbortsAsEngineFaultAndReenablesStart(bool failCreation)
    {
        using var harness = new ControllerHarness();
        var failure = new EngineException(
            EngineResultCode.ThreadCreate,
            "rq_session_start failed (ThreadCreate, -6): the coordinator thread could not be created");
        if (failCreation)
        {
            harness.Engine.CreateSessionException = failure;
        }
        else
        {
            harness.Engine.StartException = failure;
        }

        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.WaitForPublication();

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
        Assert.Equal(failure.Message, harness.StateMachine.EndDetail);
        Assert.Equal(UiText.EngineError(failure.Message), harness.Controller.ResultMessage);
        Assert.Equal(1, harness.Engine.CreateSessionCount);
        if (failCreation)
        {
            Assert.Empty(harness.Engine.Sessions);
        }
        else
        {
            FakeEngineSession session = Assert.Single(harness.Engine.Sessions);
            Assert.Equal(1, session.StartCount);
            Assert.False(session.IsStarted);
            Assert.Equal(1, session.DisposeCount);
        }

        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement run = summary.RootElement.GetProperty("run");
            Assert.Equal("ABORTED", run.GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.EngineFault), run.GetProperty("endReason").GetString());
        }

        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains(failure.Message, StringComparison.Ordinal));
    }

    // A managed exception from the engine seam can name a path in the user's profile. The display keeps it, and so
    // does app-errors.log, the local diagnostic log; the journal's result record, both shareable summaries and the
    // .log's COMPLETED and VERDICT lines do not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionStartup_PathBearingException_PersistsRedactedDetailAndLogsFullMessage(bool failCreation)
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var failure = new IOException(
            "Could not load '" + profile + @"\Downloads\RamQuick\ramquick_engine.dll', "
            + @"'C:\Users\Alice Smith\Downloads\RamQuick\ramquick_engine.dll', "
            + @"'C:\Users\O'Connor\Downloads\RamQuick\ramquick_engine.dll' or 'C:\Users\alice\AppData\Local\Temp\rq.tmp'.");
        const string Redacted =
            @"Could not load '%USERPROFILE%\Downloads\RamQuick\ramquick_engine.dll', "
            + @"'C:\Users\<user>\Downloads\RamQuick\ramquick_engine.dll', "
            + @"'C:\Users\<user>\Downloads\RamQuick\ramquick_engine.dll' or 'C:\Users\<user>\AppData\Local\Temp\rq.tmp'.";
        if (failCreation)
        {
            harness.Engine.CreateSessionException = failure;
        }
        else
        {
            harness.Engine.StartException = failure;
        }

        harness.Initialize();

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.WaitForPublication();

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.EngineError(failure.Message), harness.Controller.ResultMessage);
        Assert.Equal(Redacted, harness.StateMachine.EndDetail);
        AssertPublishedEndDetail(harness, Redacted);
        string json = File.ReadAllText(harness.SummaryJsonPath());
        string text = File.ReadAllText(TextSummaryPathOf(harness.SummaryJsonPath()));
        string journal = string.Join("\n", harness.Storage!.Lines);

        // The .log's own path lines name the data folder, so only the lines carrying the end detail are checked.
        string[] detailLines = harness.LogText()
            .Split("\r\n")
            .Where(line => line.Contains(" COMPLETED ", StringComparison.Ordinal) || line.Contains(" VERDICT ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, detailLines.Length);
        foreach (string persisted in new[] { json, text, journal }.Concat(detailLines))
        {
            Assert.DoesNotContain(profile, persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(JsonEncodedText.Encode(profile).ToString(), persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profile.Replace(@"\", @"\\", StringComparison.Ordinal), persisted, StringComparison.OrdinalIgnoreCase);
            foreach (string name in new[] { "Alice", "Smith", "O'Connor", "Connor" })
            {
                Assert.DoesNotContain(name, persisted, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.All(detailLines, line => Assert.Contains("%USERPROFILE%", line, StringComparison.Ordinal));

        // app-errors.log keeps the exception's full text; as a local log it writes the account's own folders as
        // placeholders too.
        string logged = typeof(IOException).FullName + ": Could not load '%USERPROFILE%" + @"\Downloads\RamQuick\ramquick_engine.dll'";
        harness.RunUntil(() => harness.AppErrorsLogText().Contains(logged, StringComparison.Ordinal));
    }

    [Fact]
    public void TryStart_AfterTerminalState_ClearsPreviousResult()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession first = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes failingStep = first.Options.Steps[1];
        first.RaiseErrors(FakeEngineData.Errors(3, stepIndex: failingStep.Index, stepName: failingStep.Name), 3);
        first.RaiseStepFinished(failingStep.Index, errors: 3);
        first.PublishProgress(FakeEngineData.Progress(
            failingStep.Index, first.Options.Steps.Count, failingStep.Name, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(210), 4 * GiB, first.Options.RegionBytes, 3));
        harness.Complete(RunEndReason.Completed, totalErrors: 3);

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(UiText.FailedReason(3), harness.Controller.ResultMessage);
        Assert.NotNull(harness.Controller.FirstError);
        Assert.NotEmpty(harness.Controller.ErrorGroups);
        Assert.False(harness.Controller.CauseHints.IsEmpty);
        Assert.NotNull(harness.Controller.TestedBytes);
        Assert.NotNull(harness.Controller.CurrentProgress);
        harness.Release(TimeSpan.FromMilliseconds(100));

        harness.StartAccepted();

        Assert.Equal(RunState.Preparing, harness.StateMachine.State);
        Assert.Null(harness.StateMachine.EndReason);
        Assert.Null(harness.Controller.ResultMessage);
        Assert.Null(harness.Controller.FirstError);
        Assert.Empty(harness.Controller.ErrorGroups);
        Assert.True(harness.Controller.CauseHints.IsEmpty);
        Assert.Null(harness.Controller.TestedBytes);
        Assert.Null(harness.Controller.CurrentProgress);
        Assert.Equal(0UL, harness.Controller.TotalErrors);
        Assert.False(harness.Controller.IsErrorDetailTruncated);
        Assert.Equal(2, harness.Engine.Sessions.Count);
    }

    [Fact]
    public void Errors_BeyondDetailCap_KeepTrueTotalAndMarkDetailTruncated()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        RunProgress progress = FakeEngineData.Progress(
            0, session.Options.Steps.Count, step.Name, TimeSpan.FromSeconds(10), null, GiB, session.Options.RegionBytes, 0);
        session.PublishProgress(progress);
        harness.RunUntil(() => ReferenceEquals(harness.Controller.CurrentProgress, progress));

        IReadOnlyList<ErrorRecord> stored = FakeEngineData.Errors(ErrorGrouping.StoredDetailCap, stepName: step.Name);
        session.RaiseErrors(stored, (ulong)stored.Count);
        harness.RunUntil(() => harness.Controller.TotalErrors == (ulong)stored.Count);
        Assert.False(harness.Controller.IsErrorDetailTruncated);
        Assert.Equal(stored[0], harness.Controller.FirstError);
        Assert.Equal(ErrorGrouping.Group(stored).Take(RunController.MaxErrorGroups), harness.Controller.ErrorGroups);

        // Past the cap, batches carry no records, and the true total keeps rising through batches and progress.
        session.RaiseErrors(Array.Empty<ErrorRecord>(), 15_000);
        harness.RunUntil(() => harness.Controller.TotalErrors == 15_000);
        Assert.True(harness.Controller.IsErrorDetailTruncated);
        session.PublishProgress(progress with { TotalErrors = 16_000 });
        harness.RunUntil(() => harness.Controller.TotalErrors == 16_000);
        Assert.Equal(ErrorGrouping.Group(stored).Take(RunController.MaxErrorGroups), harness.Controller.ErrorGroups);

        harness.Complete(RunEndReason.Completed, totalErrors: 16_500);

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(16_500UL, harness.Controller.TotalErrors);
        Assert.Equal(UiText.FailedReason(16_500), harness.Controller.ResultMessage);
        Assert.Equal(16_500UL, harness.Controller.CurrentProgress!.TotalErrors);
        harness.Release(TimeSpan.FromMilliseconds(100));
        using JsonDocument summary = harness.ReadSummary();
        JsonElement errors = summary.RootElement.GetProperty("errors");
        Assert.Equal(16_500UL, errors.GetProperty("total").GetUInt64());
        Assert.Equal(ErrorGrouping.StoredDetailCap, errors.GetProperty("storedCount").GetInt32());
        Assert.True(errors.GetProperty("truncated").GetBoolean());
    }

    // ---------------------------------------------------------------------------------------------
    // Error grouping
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ErrorGroups_AcrossBatches_EqualLargestGroupsOfAcceptedRecordsAsRanksChange()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes firstStep = session.Options.Steps[0];
        StepAttributes laterStep = session.Options.Steps[1];
        var accepted = new List<ErrorRecord>();
        ulong nextSequence = 1;
        ulong total = 0;

        // 150 words hit once each, 1 ms apart: the 100 earliest are listed, ending with word 99.
        RaiseAndCheck(Spread(150, firstWord: 0, TimeSpan.FromSeconds(1), firstStep));
        Assert.Equal(Word(99), harness.Controller.ErrorGroups[^1].WordAddress);

        // Five unlisted words gain repeats and overtake every listed group; 30 new words arrive too late to rank.
        RaiseAndCheck(
        [
            .. Enumerable.Range(120, 5).SelectMany(word => Hits(word, 3, TimeSpan.FromSeconds(2), firstStep)),
            .. Spread(30, firstWord: 200, TimeSpan.FromSeconds(2.1), firstStep),
        ]);
        Assert.Equal(Word(120), harness.Controller.ErrorGroups[0].WordAddress);
        Assert.Equal(4, harness.Controller.ErrorGroups[0].Count);
        Assert.Equal(Word(94), harness.Controller.ErrorGroups[^1].WordAddress);

        // A later batch gives a listed word and an unlisted word an earlier first error, from a later step.
        RaiseAndCheck(
        [
            .. Hits(140, 1, TimeSpan.FromSeconds(0.5), laterStep),
            .. Hits(50, 1, TimeSpan.FromSeconds(0.4), laterStep),
        ]);
        ErrorGroup earliestPair = harness.Controller.ErrorGroups[5];
        Assert.Equal(Word(50), earliestPair.WordAddress);
        Assert.Equal(TimeSpan.FromSeconds(0.4), earliestPair.FirstTimeIntoTest);
        Assert.Equal(laterStep.Name, earliestPair.FirstStepName);
        Assert.Equal(Word(140), harness.Controller.ErrorGroups[6].WordAddress);

        // The batch that crosses the detail cap is stored only up to it: word 130's repeats rank first, while word
        // 131's repeats lie beyond the cap and never count.
        int room = ErrorGrouping.StoredDetailCap - accepted.Count;
        RaiseAndCheck(
        [
            .. Hits(130, 10, TimeSpan.FromSeconds(3), laterStep),
            .. Spread(room - 10, firstWord: 1_000, TimeSpan.FromSeconds(3.1), laterStep),
            .. Hits(131, 50, TimeSpan.FromSeconds(4), laterStep),
        ]);
        Assert.Equal(ErrorGrouping.StoredDetailCap, accepted.Count);
        Assert.True(harness.Controller.IsErrorDetailTruncated);
        Assert.Equal(Word(130), harness.Controller.ErrorGroups[0].WordAddress);
        Assert.Equal(11, harness.Controller.ErrorGroups[0].Count);
        Assert.DoesNotContain(harness.Controller.ErrorGroups, group => group.WordAddress == Word(131));

        void RaiseAndCheck(IReadOnlyList<ErrorRecord> batch)
        {
            total += (ulong)batch.Count;
            session.RaiseErrors(batch, total);
            harness.RunUntil(() => harness.Controller.TotalErrors == total);
            accepted.AddRange(batch.Take(ErrorGrouping.StoredDetailCap - accepted.Count));
            Assert.Equal(ErrorGrouping.Group(accepted).Take(RunController.MaxErrorGroups), harness.Controller.ErrorGroups);
        }

        IReadOnlyList<ErrorRecord> Spread(int count, int firstWord, TimeSpan firstTime, StepAttributes step)
        {
            IReadOnlyList<ErrorRecord> records = FakeEngineData.Errors(
                count,
                nextSequence,
                stepIndex: step.Index,
                stepName: step.Name,
                firstRegionOffset: (ulong)firstWord * WordSpacing,
                strideBytes: WordSpacing,
                firstTimeIntoTest: firstTime);
            nextSequence += (ulong)count;
            return records;
        }

        IReadOnlyList<ErrorRecord> Hits(int word, int count, TimeSpan time, StepAttributes step)
        {
            IReadOnlyList<ErrorRecord> records = FakeEngineData.Errors(
                count,
                nextSequence,
                stepIndex: step.Index,
                stepName: step.Name,
                firstRegionOffset: (ulong)word * WordSpacing,
                strideBytes: 0,
                firstTimeIntoTest: time,
                spacing: TimeSpan.Zero);
            nextSequence += (ulong)count;
            return records;
        }

        static ulong Word(int word) => FakeEngineData.AddressAt((ulong)word * WordSpacing);
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(300, 2)]
    public void ErrorGroups_BatchOnlyInUnlistedGroups_KeepsListInstanceAndRaisesNoChange(int word, int hits)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];

        // Words 0-99 are hit three times each and listed; words 100-149 are hit once each and are not. The batch
        // then brings a stored unlisted word (120) or a new word (300) to at most two records, still below the list.
        ErrorRecord[] stored =
        [
            .. Enumerable.Range(0, RunController.MaxErrorGroups).SelectMany(listedWord => FakeEngineData.Errors(
                3,
                (ulong)(listedWord * 3) + 1,
                stepIndex: step.Index,
                stepName: step.Name,
                firstRegionOffset: (ulong)listedWord * WordSpacing,
                strideBytes: 0)),
            .. FakeEngineData.Errors(
                50,
                firstSequence: 1_001,
                stepIndex: step.Index,
                stepName: step.Name,
                firstRegionOffset: 100 * WordSpacing,
                strideBytes: WordSpacing),
        ];
        session.RaiseErrors(stored, (ulong)stored.Length);
        harness.RunUntil(() => harness.Controller.TotalErrors == (ulong)stored.Length);
        IReadOnlyList<ErrorGroup> listed = harness.Controller.ErrorGroups;
        Assert.Equal(RunController.MaxErrorGroups, listed.Count);

        var changed = new List<string?>();
        harness.Controller.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        IReadOnlyList<ErrorRecord> batch = FakeEngineData.Errors(
            hits,
            firstSequence: 2_001,
            stepIndex: step.Index,
            stepName: step.Name,
            firstRegionOffset: (ulong)word * WordSpacing,
            strideBytes: 0,
            firstTimeIntoTest: TimeSpan.FromSeconds(10));
        ulong total = (ulong)(stored.Length + batch.Count);
        session.RaiseErrors(batch, total);
        harness.RunUntil(() => harness.Controller.TotalErrors == total);

        Assert.Same(listed, harness.Controller.ErrorGroups);
        Assert.Contains(nameof(RunController.TotalErrors), changed);
        Assert.DoesNotContain(nameof(RunController.ErrorGroups), changed);
        Assert.Equal(ErrorGrouping.Group([.. stored, .. batch]).Take(RunController.MaxErrorGroups), listed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(RunController.MaxErrorGroups)]
    public void IncrementalErrorGrouping_ChurningBatches_MatchesFullGroupingAndKeepsInstanceOnlyWhenUnchanged(int maxGroups)
    {
        var grouping = new IncrementalErrorGrouping(maxGroups);
        var added = new List<ErrorRecord>();
        IReadOnlyList<ErrorGroup> previous = grouping.Largest;
        Assert.Empty(previous);
        ulong sequence = 1;

        // Deterministic churn over 160 words: batch sizes 1-50, repeats, new words, and first errors earlier than
        // those already stored.
        for (int batchIndex = 0; batchIndex < 60; batchIndex++)
        {
            int size = 1 + ((batchIndex * 37) % 50);
            var batch = new ErrorRecord[size];
            StepAttributes step = _quickSteps[batchIndex % _quickSteps.Count];
            for (int index = 0; index < size; index++)
            {
                int word = ((batchIndex * 131) + (index * 17) + ((index * index) % 23)) % 160;
                batch[index] = FakeEngineData.Error(
                    sequence++,
                    FakeEngineData.AddressAt((ulong)word * 8),
                    bit: (batchIndex + index) % 64,
                    stepIndex: step.Index,
                    stepName: step.Name,
                    timeIntoTest: TimeSpan.FromMilliseconds(((batchIndex * 7_919) + (index * 104_729)) % 5_000));
            }

            added.AddRange(batch);
            IReadOnlyList<ErrorGroup> expected = ErrorGrouping.Group(added).Take(maxGroups).ToArray();
            IReadOnlyList<ErrorGroup> result = grouping.Add(batch);

            Assert.Same(result, grouping.Largest);
            Assert.Equal(expected, result);
            if (expected.SequenceEqual(previous))
            {
                Assert.Same(previous, result);
            }
            else
            {
                Assert.NotSame(previous, result);
            }

            previous = result;
        }

        Assert.Same(previous, grouping.Add(Array.Empty<ErrorRecord>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IncrementalErrorGrouping_MaxGroupsNotPositive_Throws(int maxGroups) =>
        Assert.Throws<ArgumentOutOfRangeException>("maxGroups", () => new IncrementalErrorGrouping(maxGroups));

    [Fact]
    public void IncrementalErrorGrouping_NullBatch_Throws()
    {
        var grouping = new IncrementalErrorGrouping(RunController.MaxErrorGroups);

        Assert.Throws<ArgumentNullException>("records", () => grouping.Add(null!));
    }

    [Fact]
    public void IncrementalErrorGrouping_BatchWithNullRecord_ThrowsBeforeChangingAnyGroup()
    {
        var grouping = new IncrementalErrorGrouping(RunController.MaxErrorGroups);
        ErrorRecord first = FakeEngineData.Error(1, FakeEngineData.AddressAt(0), bit: 3, stepIndex: 0, stepName: "High-toggle sweep");
        ErrorRecord second = FakeEngineData.Error(2, FakeEngineData.AddressAt(0), bit: 5, stepIndex: 0, stepName: "High-toggle sweep");
        IReadOnlyList<ErrorGroup> listed = grouping.Add([first]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => grouping.Add([second, null!]));

        Assert.Equal("records", exception.ParamName);
        Assert.Same(listed, grouping.Largest);
        Assert.Equal(1, Assert.Single(listed).Count);

        // The valid record of the rejected batch was not counted: adding it now makes the group's second record.
        Assert.Equal(2, Assert.Single(grouping.Add([second])).Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Stops and terminal precedence
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestStop_InPreparingOrRunning_StopsAsUserAndAbortsWithStoppedByUser(bool running)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        if (running)
        {
            harness.Prepare();
        }

        harness.Controller.RequestStop();

        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedByUser, harness.Controller.ResultMessage);

        // A stop after the verdict asks the engine for nothing more.
        harness.Controller.RequestStop();
        Assert.Single(session.StopRequests);
        harness.Release(TimeSpan.FromMilliseconds(200));
        using JsonDocument summary = harness.ReadSummary();
        Assert.Equal("ABORTED", summary.RootElement.GetProperty("run").GetProperty("verdict").GetString());
    }

    [Fact]
    public void RequestStop_WhileStartPending_WithdrawsStartWithStateUnchanged()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.Controller.RequestStop();
        harness.RunUntil(() => harness.Controller.CanStart);

        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Empty(harness.Engine.Sessions);
        Assert.Null(harness.Controller.NoticeMessage);
        Assert.Empty(harness.RunLogPaths());
        Assert.False(harness.JournalExists);
    }

    [Fact]
    public void RequestStop_DuringBeginWait_WithdrawsStartAndRetiresJournal()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        storage.BlockNext("begin");
        harness.Initialize();
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.RunUntil(() => storage.BlockedCount == 1);

        harness.Controller.RequestStop();
        harness.Pump();
        Assert.False(harness.Controller.CanStart);

        // The begin record becomes durable after Start gave up on it, so the journal is ended and deleted.
        storage.Release();
        harness.RunUntil(() => harness.Controller.CanStart);

        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Empty(harness.Engine.Sessions);
        Assert.Null(harness.Controller.NoticeMessage);
        Assert.Equal(new[] { "begin", "end" }, storage.RecordTypesWritten());
        Assert.False(harness.JournalExists);
        Assert.Empty(harness.RunLogPaths());
    }


    [Fact]
    public void RequestStop_WhenIdle_DoesNothing()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();

        harness.Controller.RequestStop();
        harness.Pump();

        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.True(harness.Controller.CanStart);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
    }

    [Fact]
    public void RequestStop_AfterErrors_AbortsWithErrorCountAndLabelledHints()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        session.RaiseErrors(FakeEngineData.Errors(3, stepIndex: step.Index, stepName: step.Name), 3);
        harness.RunUntil(() => harness.Controller.TotalErrors == 3);

        harness.Controller.RequestStop();
        session.RaiseStepFinished(step.Index, errors: 3, completed: false);
        session.CompleteWithFirstRecordedReason(totalErrors: 3);
        harness.AwaitVerdict();

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedByUser + UiText.NoticeSeparator + UiText.ErrorsBeforeStop(3), harness.Controller.ResultMessage);
        Assert.NotEmpty(harness.Controller.CauseHints.Items);
        Assert.Equal(CauseHints.HintLabel, harness.Controller.CauseHints.Label);
        Assert.Equal(3UL, harness.Controller.TotalErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_RacingReasons_FirstRecordedReasonDecidesTerminalState(bool userStopFirst)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: true);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        session.RaiseErrors(FakeEngineData.Errors(1, stepIndex: step.Index, stepName: step.Name), 1);
        harness.RunUntil(() => harness.Controller.TotalErrors == 1);

        if (userStopFirst)
        {
            harness.Controller.RequestStop();
            session.SimulateEngineStop(RunEndReason.FirstError);
        }
        else
        {
            session.SimulateEngineStop(RunEndReason.FirstError);
            harness.Controller.RequestStop();
        }

        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        session.CompleteWithFirstRecordedReason(totalErrors: 1);
        harness.AwaitVerdict();

        if (userStopFirst)
        {
            Assert.Equal(RunState.Aborted, harness.StateMachine.State);
            Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
            Assert.Equal(UiText.StoppedByUser + UiText.NoticeSeparator + UiText.ErrorsBeforeStop(1), harness.Controller.ResultMessage);
        }
        else
        {
            Assert.Equal(RunState.Failed, harness.StateMachine.State);
            Assert.Equal(RunEndReason.FirstError, harness.StateMachine.EndReason);
            Assert.Equal(UiText.StoppedAtFirstErrorReason, harness.Controller.ResultMessage);
        }
    }

    [Fact]
    public void Stop_SeveralRequests_LogsOnlyTheFirstStopDecision()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        harness.Controller.RequestStop();
        harness.Controller.RequestStop();
        session.InvokeCallbackFault("on_progress", new InvalidOperationException("progress sink failed after the stop"));

        // The engine keeps the first reason, so every later request changes nothing there and adds no line here.
        Assert.Equal(new[] { StopReason.User, StopReason.User, StopReason.HostFault, StopReason.HostFault }, session.StopRequests);
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        harness.Release(TimeSpan.FromMilliseconds(100));

        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        string stop = Assert.Single(RunLogLines(harness), line => EventOf(line) == "STOP_REQUESTED");
        Assert.EndsWith(" STOP_REQUESTED reason=UserStop", stop, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstErrorStop_LogsOneStopDecisionWithTheEngineTime(bool errorReported)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: true);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        TimeSpan firstErrorTime = TimeSpan.FromSeconds(12.5);
        if (errorReported)
        {
            ErrorRecord error = FakeEngineData.Error(
                1, FakeEngineData.AddressAt(4 * MiB), 3, step.Index, step.Name, timeIntoTest: firstErrorTime);
            session.RaiseErrors(new[] { error }, 1);
            harness.RunUntil(() => harness.Controller.TotalErrors == 1);
        }

        // The engine stops itself at the first confirmed error; the controller requests nothing. Without an error
        // report the line comes from the conclusion.
        session.SimulateEngineStop(RunEndReason.FirstError);
        session.CompleteWithFirstRecordedReason(totalErrors: 1, firstErrorTime: firstErrorTime);
        harness.AwaitVerdict();
        harness.Release(TimeSpan.FromMilliseconds(100));

        Assert.Empty(session.StopRequests);
        string[] lines = RunLogLines(harness);
        string stop = Assert.Single(lines, line => EventOf(line) == "STOP_REQUESTED");
        Assert.EndsWith(" STOP_REQUESTED reason=FirstError time_into_test_ms=12500", stop, StringComparison.Ordinal);
        Assert.True(
            Array.IndexOf(lines, stop) < Array.FindIndex(lines, line => EventOf(line) == "COMPLETED"),
            "STOP_REQUESTED does not precede COMPLETED.");
        AssertStampsNonDecreasing(lines);
    }

    // The engine records its first-error stop before reporting the error, so a user Stop can reach the controller
    // after it. The user's line stays as the record of that request; Completed's end reason decides whether the
    // first error, which then carries the engine's time, was a stop decision of its own.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstErrorStop_UserStopRacesIt_LogsTheFirstErrorLineOnlyWhenItEndedTheRun(bool engineStoppedFirst)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: true);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        TimeSpan firstErrorTime = TimeSpan.FromSeconds(12);
        if (engineStoppedFirst)
        {
            session.SimulateEngineStop(RunEndReason.FirstError);
            harness.Controller.RequestStop();
        }
        else
        {
            harness.Controller.RequestStop();
            session.SimulateEngineStop(RunEndReason.FirstError);
        }

        ErrorRecord error = FakeEngineData.Error(
            1, FakeEngineData.AddressAt(4 * MiB), 3, step.Index, step.Name, timeIntoTest: firstErrorTime);
        session.RaiseErrors(new[] { error }, 1);
        harness.RunUntil(() => harness.Controller.TotalErrors == 1);
        session.CompleteWithFirstRecordedReason(
            totalErrors: 1, elapsed: TimeSpan.FromMilliseconds(12_300), firstErrorTime: firstErrorTime);
        harness.AwaitVerdict();
        harness.Release(TimeSpan.FromMilliseconds(100));

        RunEndReason winner = engineStoppedFirst ? RunEndReason.FirstError : RunEndReason.UserStop;
        Assert.Equal(winner, harness.StateMachine.EndReason);
        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        string[] lines = RunLogLines(harness);
        string[] events = lines
            .Where(line => EventOf(line) is "STOP_REQUESTED" or "COMPLETED")
            .Select(line => line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..])
            .ToArray();
        string[] expectedStops = engineStoppedFirst
            ? new[] { "STOP_REQUESTED reason=UserStop", "STOP_REQUESTED reason=FirstError time_into_test_ms=12000" }
            : new[] { "STOP_REQUESTED reason=UserStop" };
        Assert.Equal(expectedStops, events[..^1]);
        Assert.StartsWith(
            $"COMPLETED end_reason={winner} total_errors=1 verified_bytes=0 elapsed_ms=12300 first_error_ms=12000 ",
            events[^1],
            StringComparison.Ordinal);
        AssertStampsNonDecreasing(lines);
    }

    [Fact]
    public void Completed_FirstErrorWithStopOnError_FailsWithSingleErrorPriorHint()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted(stopOnError: true);
        harness.Prepare();

        // "Random bank thrash" targets row timings, so its single error ranks the row/bank category by prior alone.
        StepAttributes step = session.Options.Steps[1];
        Assert.Equal(InstabilityMechanism.RowTimings, step.PrimaryMechanism);
        ErrorRecord error = FakeEngineData.Error(
            1, FakeEngineData.AddressAt(64 * MiB), 9, step.Index, step.Name, timeIntoTest: TimeSpan.FromSeconds(52));
        session.RaiseErrors(new[] { error }, 1);
        session.SimulateEngineStop(RunEndReason.FirstError);
        session.RaiseStepFinished(step.Index, errors: 1, completed: false);
        session.CompleteWithFirstRecordedReason(totalErrors: 1, firstErrorTime: error.TimeIntoTest);
        harness.AwaitVerdict();

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(RunEndReason.FirstError, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedAtFirstErrorReason, harness.Controller.ResultMessage);
        Assert.Equal(error, harness.Controller.FirstError);
        Assert.Empty(session.StopRequests);
        CauseHint hint = Assert.Single(harness.Controller.CauseHints.Items);
        Assert.Equal(CauseCategory.RowBankTiming, hint.Category);
        Assert.Equal(CauseHints.HintLabel, harness.Controller.CauseHints.Label);
    }

    // ---------------------------------------------------------------------------------------------
    // Thermal limits
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ThermalSensor.Cpu)]
    [InlineData(ThermalSensor.Ram)]
    public void Readings_FreshReadingAtLimit_StopsForSafetyOnlyWhileRunning(ThermalSensor sensor)
    {
        using var harness = new ControllerHarness();
        harness.StartSensors();
        harness.Initialize();
        double limit = sensor == ThermalSensor.Cpu ? ThermalLimits.CpuLimitCelsius : ThermalLimits.RamLimitCelsius;
        if (sensor == ThermalSensor.Cpu)
        {
            harness.Sensors.SetCpu(limit);
        }
        else
        {
            harness.Sensors.SetRam(limit);
        }

        // Idle: shown, but no run to stop.
        harness.NextReading();
        SensorValue shown = sensor == ThermalSensor.Cpu ? harness.Controller.LatestReadings.Cpu : harness.Controller.LatestReadings.Ram;
        Assert.Equal(limit, shown.Celsius);
        Assert.Equal(SensorStatus.Available, shown.Status);

        // Preparing: the limits are not yet in force.
        FakeEngineSession session = harness.StartAccepted();
        harness.NextReading();
        Assert.Empty(session.StopRequests);

        harness.Prepare();
        harness.NextReading();
        Assert.Equal(new[] { StopReason.Thermal }, session.StopRequests);

        // One thermal stop per run, however many readings stay at the limit.
        harness.NextReading();
        Assert.Single(session.StopRequests);

        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.Thermal, harness.StateMachine.EndReason);
        Assert.Equal(UiText.ThermalStop(sensor, limit), harness.Controller.ResultMessage);
    }

    [Theory]
    [InlineData(94.9, FakeSensorSource.DefaultRamCelsius, false)]
    [InlineData(FakeSensorSource.DefaultCpuCelsius, 84.9, false)]
    [InlineData(ThermalLimits.CpuLimitCelsius, FakeSensorSource.DefaultRamCelsius, true)]
    [InlineData(FakeSensorSource.DefaultCpuCelsius, ThermalLimits.RamLimitCelsius, true)]
    public void Readings_BelowLimitOrStale_NeverStop(double cpuCelsius, double ramCelsius, bool stale)
    {
        using var harness = new ControllerHarness();
        harness.StartSensors();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        harness.Sensors.SetCpu(cpuCelsius);
        harness.Sensors.SetRam(ramCelsius);
        if (stale)
        {
            // Captured longer ago than the monitor's freshness bound, so it arrives already Stale.
            harness.Sensors.CapturedAtOffset = -(SensorMonitor.StaleAfter + TimeSpan.FromSeconds(1));
        }

        harness.NextReading();
        harness.NextReading();

        Assert.Empty(session.StopRequests);
        Assert.Equal(RunState.Running, harness.StateMachine.State);
        SensorReadings readings = harness.Controller.LatestReadings;
        if (stale)
        {
            Assert.Equal(SensorStatus.Stale, readings.Cpu.Status);
            Assert.Equal(SensorStatus.Stale, readings.Ram.Status);
        }
        else
        {
            Assert.Equal(cpuCelsius, readings.Cpu.Celsius);
            Assert.Equal(ramCelsius, readings.Ram.Celsius);
        }
    }


    [Fact]
    public void Prepared_ReducedInstructionSet_FlagsReducedSet()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        Assert.False(harness.Controller.IsReducedInstructionSet);
        harness.StartAccepted();

        EnginePrepared prepared = harness.Prepare(InstructionSet.Sse2);

        Assert.True(prepared.IsReducedInstructionSet);
        Assert.True(harness.Controller.IsReducedInstructionSet);
        Assert.Equal(prepared.TestedBytes, harness.Controller.TestedBytes);
    }

    // ---------------------------------------------------------------------------------------------
    // Step gate and journal failures
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StepGate_StepRecordAcknowledged_ContinuesAndJournalsTheStep()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        (StepGateDecision decision, _) = harness.RunGate(0);

        Assert.Equal(StepGateDecision.Continue, decision);
        GateInvocation invocation = Assert.Single(session.GateInvocations);
        Assert.NotEqual(Environment.CurrentManagedThreadId, invocation.ThreadId);
        Assert.Empty(session.StopRequests);
        JournalStepStart step = Assert.Single(harness.ReadJournal().OfType<JournalStepStart>());
        Assert.Equal(1, step.Index);
        Assert.Equal(session.Options.Steps.Count, step.Count);
        Assert.Equal(session.Options.Steps[0].Name, step.Name);
    }

    [Fact]
    public void StepGate_EarlierEventsNotYetDelivered_JournalsPreparedAndStepEndBeforeTheStep()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();

        // The engine raises Prepared before step 1's gate; the UI thread has not delivered it yet.
        session.RaisePrepared();
        long firstStartedAt = Stopwatch.GetTimestamp();
        Task<StepGateDecision> first = session.InvokeStepGateOnBackgroundThread(0);
        Assert.False(
            SpinWait.SpinUntil(() => first.IsCompleted, _undeliveredEventsWindow),
            "Step 1's gate returned while Prepared was still undelivered.");
        Assert.Empty(harness.ReadJournal().OfType<JournalStepStart>());

        (StepGateDecision firstDecision, _) = harness.AwaitGate(first, firstStartedAt);
        Assert.Equal(StepGateDecision.Continue, firstDecision);
        Assert.Collection(
            harness.ReadJournal(),
            record => Assert.IsType<JournalBegin>(record),
            record => Assert.IsType<JournalPrepared>(record),
            record => Assert.Equal(1, Assert.IsType<JournalStepStart>(record).Index));

        // Likewise step 1's StepFinished before step 2's gate.
        session.RaiseStepFinished(0);
        long secondStartedAt = Stopwatch.GetTimestamp();
        Task<StepGateDecision> second = session.InvokeStepGateOnBackgroundThread(1);
        Assert.False(
            SpinWait.SpinUntil(() => second.IsCompleted, _undeliveredEventsWindow),
            "Step 2's gate returned while step 1's StepFinished was still undelivered.");
        Assert.DoesNotContain(harness.ReadJournal(), record => record is JournalStepStart { Index: 2 });

        (StepGateDecision secondDecision, _) = harness.AwaitGate(second, secondStartedAt);
        Assert.Equal(StepGateDecision.Continue, secondDecision);
        Assert.Collection(
            harness.ReadJournal(),
            record => Assert.IsType<JournalBegin>(record),
            record => Assert.IsType<JournalPrepared>(record),
            record => Assert.Equal(1, Assert.IsType<JournalStepStart>(record).Index),
            record => Assert.Equal(1, Assert.IsType<JournalStepEnd>(record).Index),
            record => Assert.Equal(2, Assert.IsType<JournalStepStart>(record).Index));
        Assert.Empty(session.StopRequests);
    }

    [Fact]
    public void StepGate_StopDuringJournalWait_AbortsWithinTwoSecondsWithoutHostFault()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        storage.BlockNext("step");

        Task<StepGateDecision> gate = session.InvokeStepGateOnBackgroundThread(0);

        // Pumped, because the gate journals its step only once the UI context has run its barrier.
        harness.RunUntil(() => storage.BlockedCount > 0);
        long stoppedAt = Stopwatch.GetTimestamp();
        harness.Controller.RequestStop();
        (StepGateDecision decision, TimeSpan elapsed) = harness.AwaitGate(gate, stoppedAt);

        Assert.Equal(StepGateDecision.Abort, decision);
        Assert.True(elapsed < _stopBound, $"The gate returned {elapsed.TotalMilliseconds:0} ms after Stop.");
        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        Assert.Equal(RunEndReason.UserStop, session.FirstRecordedEndReason);

        storage.Release();
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedByUser, harness.Controller.ResultMessage);
    }

    [Fact]
    public void StepGate_AcknowledgmentTimesOut_RequestsHostFaultBeforeAbort()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        storage.BlockNext("step");

        // The gate's bound is real time: the fake clock is never advanced here.
        (StepGateDecision decision, TimeSpan elapsed) = harness.RunGate(0, _gateTimeoutWait);

        Assert.Equal(StepGateDecision.Abort, decision);
        Assert.True(elapsed >= _gateTimeoutFloor, $"The gate gave up after {elapsed.TotalMilliseconds:0} ms.");
        GateInvocation invocation = Assert.Single(session.GateInvocations);
        Assert.Equal(1, invocation.StopRequestCountAtReturn);
        Assert.Equal(new[] { StopReason.HostFault }, session.StopRequests);

        // Completed arrives a second after the failure, so a failure logged only at the conclusion would show.
        DateTimeOffset failedAt = harness.Time.GetUtcNow();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        storage.Release();
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.JournalWriteFailed(UiText.JournalAcknowledgmentTimedOut), harness.Controller.ResultMessage);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertGateFailureLogged(RunLogLines(harness), UiText.JournalAcknowledgmentTimedOut, failedAt);
    }

    [Fact]
    public void StepGate_StepRecordWriteFails_RequestsHostFaultBeforeAbort()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        harness.Storage!.FailNext("step");
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        (StepGateDecision decision, _) = harness.RunGate(0);

        Assert.Equal(StepGateDecision.Abort, decision);
        GateInvocation invocation = Assert.Single(session.GateInvocations);
        Assert.Equal(1, invocation.StopRequestCountAtReturn);
        Assert.Equal(new[] { StopReason.HostFault }, session.StopRequests);
        JournalFailure failure = Assert.IsType<JournalFailure>(Assert.Single(harness.Journals).Failure);
        Assert.Equal("step", failure.RecordType);

        DateTimeOffset failedAt = harness.Time.GetUtcNow();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.JournalWriteFailed(failure.Message), harness.Controller.ResultMessage);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertPublishedEndDetail(harness, UiText.JournalWriteFailed(failure.Message));
        AssertGateFailureLogged(RunLogLines(harness), failure.Message, failedAt);
    }

    // The failed write names the journal in the user's profile: the display keeps the path, and the persisted cause
    // is redacted like every persisted end detail.
    [Theory]
    [InlineData("stepEnd")]
    [InlineData("beat")]
    public void JournalFailure_SeenOnNextProgress_RequestsHostFaultOnceAndAbortsWithJournalMessage(string recordType)
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        ProgressJournal journal = Assert.Single(harness.Journals);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        harness.Storage!.FailNext(
            recordType,
            new IOException("The process cannot access the file '" + profile + @"\RamQuick\data\journal\active-run.jsonl'."));
        StepAttributes next = session.Options.Steps[1];
        RunProgress progress = FakeEngineData.Progress(
            next.Index, session.Options.Steps.Count, next.Name, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(255), 2 * GiB, session.Options.RegionBytes, 0);

        if (recordType == "beat")
        {
            // The heartbeat writes the snapshot the progress sink stored, once the journal's clock reaches it.
            session.PublishProgress(progress with { Elapsed = TimeSpan.FromSeconds(44) });
            harness.Pump();
            harness.Time.Advance(ProgressJournal.HeartbeatInterval);
        }
        else
        {
            session.RaiseStepFinished(0);
        }

        harness.RunUntil(() => journal.Failure is not null);
        JournalFailure failure = Assert.IsType<JournalFailure>(journal.Failure);
        Assert.Equal(recordType, failure.RecordType);
        Assert.Empty(session.StopRequests);

        session.PublishProgress(progress);
        harness.RunUntil(() => session.StopRequests.Count > 0);
        Assert.Equal(new[] { StopReason.HostFault }, session.StopRequests);

        session.PublishProgress(progress with { Elapsed = TimeSpan.FromSeconds(46) });
        harness.Pump();
        Assert.Single(session.StopRequests);

        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.JournalWriteFailed(failure.Message), harness.Controller.ResultMessage);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertPublishedEndDetail(
            harness,
            UiText.JournalWriteFailed(@"The process cannot access the file '%USERPROFILE%\RamQuick\data\journal\active-run.jsonl'."));
    }

    [Fact]
    public void CallbackFault_RequestsHostFaultAndWritesAppErrorLog()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        var fault = new InvalidOperationException("progress sink failed");

        session.InvokeCallbackFault("on_progress", fault);

        Assert.NotEmpty(session.StopRequests);
        Assert.All(session.StopRequests, reason => Assert.Equal(StopReason.HostFault, reason));
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("EngineCallback:on_progress", StringComparison.Ordinal)
            && harness.AppErrorsLogText().Contains(fault.Message, StringComparison.Ordinal));

        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.HostFault(fault.Message), harness.Controller.ResultMessage);
        Assert.Equal(UiText.HostFault(fault.Message), harness.StateMachine.EndDetail);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertPublishedEndDetail(harness, UiText.HostFault(fault.Message));
        AssertFaultLogged(harness, "EngineCallback:on_progress", fault);
    }

    // A host-fault stop with no cause recorded by the controller, such as a step gate whose session was already
    // collected, keeps the engine's own detail, under the application's wording rather than the engine-fault one.
    [Fact]
    public void HostFault_WithoutRecordedCause_ShowsApplicationWordingAndPersistsEngineDetail()
    {
        const string EngineDetail = "Stop requested by host: host fault";
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        harness.Initialize();
        harness.StartAccepted();
        harness.Prepare();

        harness.Complete(RunEndReason.HostFault, endDetail: EngineDetail);

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(UiText.HostFault(EngineDetail), harness.Controller.ResultMessage);
        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertPublishedEndDetail(harness, EngineDetail);
    }

    [Fact]
    public void HandlerFault_LogsItsCauseAndOneHostFaultStopTiedToTheRun()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        var fault = new InvalidOperationException("progress display failed");
        int thrown = 0;
        harness.Controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunController.CurrentProgress) && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw fault;
            }
        };

        StepAttributes step = session.Options.Steps[0];
        session.PublishProgress(FakeEngineData.Progress(
            step.Index,
            session.Options.Steps.Count,
            step.Name,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(295),
            GiB,
            session.Options.RegionBytes,
            0));
        harness.RunUntil(() => session.StopRequests.Count > 0);

        Assert.Equal(new[] { StopReason.HostFault }, session.StopRequests);
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.HostFault(fault.Message), harness.Controller.ResultMessage);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertFaultLogged(harness, "RunController.OnSessionProgressChanged", fault);
    }

    // ---------------------------------------------------------------------------------------------
    // Crash recovery at launch
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void InitializeAsync_JournalEndsInsideStep_FailsWithThatStepAndPublishesPartialSummary()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 2));

        harness.Initialize();

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(RunEndReason.PreviousRunUnfinished, harness.StateMachine.EndReason);
        Assert.Equal(UiText.PreviousRunUnfinished(2, 3, _quickSteps[1].Name), harness.Controller.ResultMessage);
        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        Assert.Equal(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName), harness.SummaryJsonPath());
        Assert.True(File.Exists(ResultsLog.GetSummaryTextPath(harness.Data!, baseName)));
        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement run = summary.RootElement.GetProperty("run");
            Assert.Equal("FAIL", run.GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.PreviousRunUnfinished), run.GetProperty("endReason").GetString());
            Assert.True(run.GetProperty("recovered").GetBoolean());
            Assert.True(run.GetProperty("partial").GetBoolean());
        }

        // A new Start leaves the recovered FAIL behind.
        harness.StartAccepted();
        Assert.Null(harness.Controller.ResultMessage);
    }

    [Fact]
    public void InitializeAsync_JournalEndsBeforeFirstStep_FailsWithPreparingMessage()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 0));

        harness.Initialize();

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(RunEndReason.PreviousRunUnfinished, harness.StateMachine.EndReason);
        Assert.Equal(UiText.PreviousRunUnfinishedPreparing, harness.Controller.ResultMessage);
        Assert.False(harness.JournalExists);
        Assert.Equal(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName), harness.SummaryJsonPath());
    }

    [Fact]
    public void InitializeAsync_ResultWithoutEnd_PublishesMissingSummariesAndStaysIdle()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(CompletedJournal(runId, baseName));

        harness.Initialize();

        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(UiText.RecoveredNotice, harness.Controller.NoticeMessage);
        Assert.Null(harness.Controller.ResultMessage);
        Assert.True(File.Exists(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName)));
        Assert.True(File.Exists(ResultsLog.GetSummaryTextPath(harness.Data!, baseName)));
        using (JsonDocument summary = harness.ReadSummary())
        {
            Assert.Equal("FAIL", summary.RootElement.GetProperty("run").GetProperty("verdict").GetString());
        }

        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
    }

    // ---------------------------------------------------------------------------------------------
    // Shutdown, disposal and fatal errors
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ShutdownAsync_WhileRunningWithTimelyEngine_StopsAsUserAndPublishesAborted()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        Task shutdown = harness.Shutdown();

        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(StartResult.Busy, harness.TryStart());
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        session.RaiseReleased(session.Options.RegionBytes, TimeSpan.FromMilliseconds(300));
        harness.AwaitTask(shutdown);

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement root = summary.RootElement;
            Assert.Equal("ABORTED", root.GetProperty("run").GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.UserStop), root.GetProperty("run").GetProperty("endReason").GetString());
            Assert.Equal(0.3, root.GetProperty("memory").GetProperty("releaseSeconds").GetDouble());
        }

        Assert.Equal(1, session.DisposeCount);
        Assert.False(harness.JournalExists);
        Assert.Same(shutdown, harness.Shutdown());
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }

    [Fact]
    public void ShutdownAsync_EngineSilent_FinalisesAbortedThenPublishesWithoutConfirmedRelease()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        RunProgress progress = FakeEngineData.Progress(
            0, session.Options.Steps.Count, step.Name, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(280), 3 * GiB, session.Options.RegionBytes, 0);
        session.PublishProgress(progress);
        harness.RunUntil(() => ReferenceEquals(harness.Controller.CurrentProgress, progress));

        Task shutdown = harness.Shutdown();
        Assert.Equal(new[] { StopReason.User }, session.StopRequests);

        // No Completed within 3 s: the controller concludes the run as a user stop from its own totals.
        harness.Time.Advance(harness.Options.ShutdownCompletedTimeout);
        harness.RunUntil(() => harness.StateMachine.IsTerminal
            && harness.Controller.PostRunActivity == PostRunActivity.ReleasingMemory);
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedByUser, harness.Controller.ResultMessage);
        Assert.False(shutdown.IsCompleted);
        Assert.Empty(harness.SummaryJsonPaths());

        // No Released within 10 s more: the summaries are published without a release time.
        harness.Time.Advance(harness.Options.ShutdownReleasedTimeout);
        harness.AwaitTask(shutdown);

        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement root = summary.RootElement;
            Assert.Equal("ABORTED", root.GetProperty("run").GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.UserStop), root.GetProperty("run").GetProperty("endReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("memory").GetProperty("releaseSeconds").ValueKind);
        }

        // The clock stands at the deadline, so the shutdown has not waited for the disposal running on the thread pool.
        harness.RunUntil(() => session.IsDisposed);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(harness.JournalExists);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void ShutdownAsync_EngineSilentAfterStepsStarted_ListsEveryStartedStepOnce(int startedSteps, bool lastFinished)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        for (int index = 0; index < startedSteps; index++)
        {
            // The gate runs on a coordinator thread while the UI context is pumped, because it waits for that context
            // to deliver the events raised before it; the engine raises StepStarted only once the gate continues.
            (StepGateDecision decision, _) = harness.RunGate(index);
            Assert.Equal(StepGateDecision.Continue, decision);
            session.RaiseStepStarted(index);
            if (index < startedSteps - 1 || lastFinished)
            {
                session.RaiseStepFinished(index);
            }
        }

        StepAttributes last = session.Options.Steps[startedSteps - 1];
        RunProgress progress = FakeEngineData.Progress(
            startedSteps - 1, session.Options.Steps.Count, last.Name, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(280), 3 * GiB, session.Options.RegionBytes, 0);
        session.PublishProgress(progress);
        harness.RunUntil(() => ReferenceEquals(harness.Controller.CurrentProgress, progress));

        // Neither Completed nor Released ever arrives: the controller concludes and publishes the run from its own totals.
        Task shutdown = harness.Shutdown();
        harness.Time.Advance(harness.Options.ShutdownCompletedTimeout);
        harness.RunUntil(() => harness.StateMachine.IsTerminal
            && harness.Controller.PostRunActivity == PostRunActivity.ReleasingMemory);
        harness.Time.Advance(harness.Options.ShutdownReleasedTimeout);
        harness.AwaitTask(shutdown);

        string jsonPath = harness.SummaryJsonPath();
        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement root = summary.RootElement;
            Assert.Equal("ABORTED", root.GetProperty("run").GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.UserStop), root.GetProperty("run").GetProperty("endReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("memory").GetProperty("releaseSeconds").ValueKind);
            JsonElement[] steps = root.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Equal(startedSteps, steps.Length);
            for (int index = 0; index < startedSteps; index++)
            {
                bool finished = index < startedSteps - 1 || lastFinished;
                JsonElement step = steps[index];
                Assert.Equal(index + 1, step.GetProperty("index").GetInt32());
                Assert.Equal(session.Options.Steps[index].Name, step.GetProperty("name").GetString());
                Assert.Equal(finished, step.GetProperty("completed").GetBoolean());
                Assert.Equal(finished ? JsonValueKind.Number : JsonValueKind.Null, step.GetProperty("actualSeconds").ValueKind);
            }
        }

        // The text summary's steps table ends each row with its Completed column.
        string row = Assert.Single(
            File.ReadAllLines(TextSummaryPathOf(jsonPath)),
            line => line.Contains(last.Name, StringComparison.Ordinal));
        Assert.EndsWith(lastFinished ? "Yes" : "No", row.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownAsync_WhenIdle_CompletesAtOnceAndRefusesStart()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();

        Task shutdown = harness.Shutdown();
        harness.AwaitTask(shutdown);

        Assert.Equal(StartResult.Busy, harness.TryStart());
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
    }

    [Fact]
    public void ShutdownAsync_WhileStartPending_WithdrawsStartAndCompletes()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        Assert.Equal(StartResult.Accepted, harness.TryStart());

        Task shutdown = harness.Shutdown();
        harness.AwaitTask(shutdown);

        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Empty(harness.Engine.Sessions);
        Assert.False(harness.JournalExists);
        Assert.Empty(harness.RunLogPaths());
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }


    [Fact]
    public void Dispose_DuringRun_ReleasesSessionAndKeepsJournalForRecovery()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        harness.Controller.Dispose();
        harness.Controller.Dispose();

        // The session is disposed on the thread pool.
        harness.RunUntil(() => session.IsDisposed);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(harness.JournalExists);
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }

    [Fact]
    public void Dispose_SessionDisposalBlocks_ReturnsAtOnceAndDisposesOnAnotherThread()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        session.BlockDispose();
        try
        {
            long startedAt = Stopwatch.GetTimestamp();
            harness.Controller.Dispose();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

            Assert.True(elapsed < _disposeReturnCeiling, $"Dispose returned after {elapsed.TotalMilliseconds:0} ms.");
            harness.RunUntil(() => session.DisposeCount == 1);
            Assert.NotEqual(Environment.CurrentManagedThreadId, session.DisposeThreadId);
            Assert.False(session.IsDisposed);
            Assert.True(harness.JournalExists);
        }
        finally
        {
            session.ReleaseDispose();
        }

        harness.RunUntil(() => session.IsDisposed);
    }

    [Fact]
    public void ShutdownAsync_PublicationStalledPastDeadline_StartsSessionDisposalWithoutAwaitingIt()
    {
        using var harness = new ControllerHarness();

        // The journal's own bounds run on a clock that never moves, so the blocked end record holds the publication.
        harness.UseStalledJournal(journalTime: new FakeTimeProvider(ControllerHarness.StartTime));
        StalledJournalStorage storage = harness.Storage!;
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        session.BlockDispose();
        storage.BlockNext("end");
        try
        {
            Task shutdown = harness.Shutdown();
            harness.Time.Advance(harness.Options.ShutdownCompletedTimeout);
            harness.RunUntil(() => harness.StateMachine.IsTerminal
                && harness.Controller.PostRunActivity == PostRunActivity.ReleasingMemory);
            harness.Time.Advance(harness.Options.ShutdownReleasedTimeout);
            harness.RunUntil(() => storage.BlockedCount == 1);
            Assert.Equal(0, session.DisposeCount);
            Assert.False(shutdown.IsCompleted);

            // The publication misses the deadline: the release is started, and the shutdown ends without waiting for it.
            harness.Time.Advance(harness.Options.ShutdownReleasedTimeout);
            harness.AwaitTask(shutdown);
            harness.RunUntil(() => session.DisposeCount == 1);

            Assert.NotEqual(Environment.CurrentManagedThreadId, session.DisposeThreadId);
            Assert.False(session.IsDisposed);
            Assert.Equal(RunState.Aborted, harness.StateMachine.State);
            Assert.Single(harness.SummaryJsonPaths());
            Assert.True(harness.JournalExists);
        }
        finally
        {
            session.ReleaseDispose();
        }

        harness.RunUntil(() => session.IsDisposed);
    }

    [Fact]
    public void HandleFatalException_DuringRun_LogsAtOnceAndReleasesSessionKeepingJournal()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        var crash = new InvalidOperationException("dispatcher crash");

        Exception? thrown = Record.Exception(() => harness.Controller.HandleFatalException(crash));

        Assert.Null(thrown);
        string appErrors = harness.AppErrorsLogText();
        Assert.Contains(
            " APP_ERROR source=\"UnhandledException\" run_id=" + RunIdOf(harness) + "\r\n",
            appErrors,
            StringComparison.Ordinal);
        Assert.Contains(crash.Message, appErrors, StringComparison.Ordinal);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(harness.JournalExists);
    }

    [Fact]
    public void HandleFatalException_SessionDisposalBlocks_ReturnsWithinBoundAndStillLogs()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        session.BlockDispose();
        var crash = new InvalidOperationException("crash while the engine hangs");
        try
        {
            long startedAt = Stopwatch.GetTimestamp();
            Exception? thrown = Record.Exception(() => harness.Controller.HandleFatalException(crash));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

            Assert.Null(thrown);
            Assert.True(elapsed < _fatalReturnCeiling, $"HandleFatalException returned after {elapsed.TotalSeconds:0.0} s.");
            Assert.Contains(crash.Message, harness.AppErrorsLogText(), StringComparison.Ordinal);
            Assert.Equal(1, session.DisposeCount);
            Assert.NotEqual(Environment.CurrentManagedThreadId, session.DisposeThreadId);
            Assert.False(session.IsDisposed);
            Assert.True(harness.JournalExists);
        }
        finally
        {
            session.ReleaseDispose();
        }

        harness.RunUntil(() => session.IsDisposed);
    }

    [Fact]
    public void HandleFatalException_WithoutDataDirectory_NeverThrows()
    {
        using var harness = new ControllerHarness();
        harness.WithoutDataDirectory();
        harness.Initialize();

        Exception? thrown = Record.Exception(() => harness.Controller.HandleFatalException(new InvalidOperationException("crash")));

        Assert.Null(thrown);
        Assert.False(File.Exists(Path.Combine(harness.RootPath, "data", "logs", "app-errors.log")));
    }

    // ---------------------------------------------------------------------------------------------
    // Run log readers
    // ---------------------------------------------------------------------------------------------

    // The run's .log, read after publication: the session's teardown disposes the log, which drains it.
    private static string[] RunLogLines(ControllerHarness harness) =>
        harness.LogText().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static string EventOf(string line) => line.Split(' ')[1];

    private static DateTimeOffset StampOf(string line) => DateTimeOffset.ParseExact(
        line[..24],
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // A step-gate failure is logged as it happens: JOURNAL_FAILURE, then its single host-fault STOP_REQUESTED, both
    // stamped at the failure, and only then COMPLETED, stamped later; no stamp in the log decreases.
    private static void AssertGateFailureLogged(string[] lines, string failure, DateTimeOffset failedAt)
    {
        Assert.Equal(
            new[] { "JOURNAL_FAILURE", "STOP_REQUESTED", "COMPLETED" },
            lines.Select(EventOf).Where(name => name is "JOURNAL_FAILURE" or "STOP_REQUESTED" or "COMPLETED").Take(3));
        string journalFailure = lines.First(line => EventOf(line) == "JOURNAL_FAILURE");
        Assert.EndsWith($" JOURNAL_FAILURE message=\"{failure}\"", journalFailure, StringComparison.Ordinal);
        Assert.Equal(failedAt, StampOf(journalFailure));
        string stop = Assert.Single(lines, line => EventOf(line) == "STOP_REQUESTED");
        Assert.EndsWith(" STOP_REQUESTED reason=HostFault", stop, StringComparison.Ordinal);
        Assert.Equal(failedAt, StampOf(stop));
        Assert.True(StampOf(lines.First(line => EventOf(line) == "COMPLETED")) > failedAt, "COMPLETED is stamped at the failure.");
        AssertStampsNonDecreasing(lines);
    }

    // The run id that ends the run's .log name, the 32-hex-digit form every artefact of the run uses.
    private static string RunIdOf(ControllerHarness harness) =>
        Path.GetFileNameWithoutExtension(Assert.Single(harness.RunLogPaths()))[^32..];

    // A callback or handler fault is logged as CALLBACK_FAULT, then one host-fault STOP_REQUESTED, then COMPLETED,
    // and its app-errors.log entry carries the source and the run's id.
    private static void AssertFaultLogged(ControllerHarness harness, string source, Exception fault)
    {
        string[] lines = RunLogLines(harness);
        string cause = Assert.Single(lines, line => EventOf(line) == "CALLBACK_FAULT");
        Assert.EndsWith(
            $" CALLBACK_FAULT source=\"{source}\" error=\"{fault.GetType().FullName}: {fault.Message}\"",
            cause,
            StringComparison.Ordinal);
        string stop = Assert.Single(lines, line => EventOf(line) == "STOP_REQUESTED");
        Assert.EndsWith(" STOP_REQUESTED reason=HostFault", stop, StringComparison.Ordinal);
        int completed = Array.FindIndex(lines, line => EventOf(line) == "COMPLETED");
        Assert.True(
            Array.IndexOf(lines, cause) < Array.IndexOf(lines, stop) && Array.IndexOf(lines, stop) < completed,
            "The fault, its stop and COMPLETED are out of order.");
        AssertStampsNonDecreasing(lines);

        string header = $" APP_ERROR source=\"{source}\" run_id={RunIdOf(harness)}\r\n";
        harness.RunUntil(() => harness.AppErrorsLogText().Contains(header, StringComparison.Ordinal));
    }

    private static void AssertStampsNonDecreasing(string[] lines)
    {
        for (int index = 1; index < lines.Length; index++)
        {
            Assert.True(
                StampOf(lines[index]) >= StampOf(lines[index - 1]),
                $"Line {index + 1} is stamped before line {index}: '{lines[index]}' after '{lines[index - 1]}'.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Journal builders for crash recovery
    // ---------------------------------------------------------------------------------------------

    // The crashed run started an hour before this launch, on the fake engine's default machine.
    private static DateTimeOffset CrashedRunStart => ControllerHarness.StartTime - TimeSpan.FromHours(1);

    private static (Guid RunId, string BaseName) NewRunIdentity(TestType testType)
    {
        Guid runId = Guid.NewGuid();
        return (runId, $"RamQuick-20261028-130000-{testType}-{runId:N}");
    }

    private static string TextSummaryPathOf(string jsonSummaryPath) => jsonSummaryPath[..^".json".Length] + ".txt";

    // Reads what a published run persisted as its end detail: the journal's result record, from the stalled storage's
    // append history, which outlives the journal's deletion; run.endDetail of the JSON summary; and the text summary's
    // "Detail:" line.
    private static void AssertPublishedEndDetail(ControllerHarness harness, string expected)
    {
        StalledJournalStorage storage = harness.Storage
            ?? throw new InvalidOperationException("Only a stalled journal keeps its result record after publication.");
        JournalResult result = Assert.Single(JournalLineSerializer.ParseAll(storage.Lines).OfType<JournalResult>());
        Assert.Equal(expected, result.EndDetail);
        using (JsonDocument summary = harness.ReadSummary())
        {
            Assert.Equal(expected, summary.RootElement.GetProperty("run").GetProperty("endDetail").GetString());
        }

        string text = File.ReadAllText(TextSummaryPathOf(harness.SummaryJsonPath()));
        Assert.Contains("\r\nDetail: " + expected + "\r\n", text, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static JournalBegin BeginRecord(Guid runId, string baseName, int stepCount) =>
        JournalBegin.Create(
            runId,
            baseName,
            nameof(TestType.Quick),
            stopOnError: true,
            CrashedRunStart,
            "1.0.0",
            _quickSteps.Take(stepCount).ToArray(),
            FakeEngineData.DefaultSystem,
            "Microsoft Windows 10.0.26100",
            new JournalMemoryInfo { InstalledBytes = 32 * GiB, Type = MemoryType.DDR5, SpeedMTs = 6000, Channels = 2, Dimms = 2 },
            availableBytes: 24 * GiB,
            plannedBytes: 20 * GiB) with { Utc = CrashedRunStart };

    private static JournalPrepared PreparedRecord(DateTimeOffset utc) => new()
    {
        TestedBytes = 20 * GiB,
        Workers = FakeEngineData.DefaultSystem.LogicalProcessors,
        PageMode = PageMode.Standard,
        IsaSelected = InstructionSet.Avx2,
        AddressKind = AddressKind.Virtual,
        PreparationMs = 1_500,
        Utc = utc,
    };

    // A run killed during step lastStepNumber (1-based) of stepCount, or during preparation when it is 0: every
    // earlier step started and finished, and a heartbeat followed the last step record.
    private static JournalRecord[] UnfinishedJournal(Guid runId, string baseName, int stepCount, int lastStepNumber)
    {
        DateTimeOffset running = CrashedRunStart + TimeSpan.FromSeconds(2);
        var records = new List<JournalRecord> { BeginRecord(runId, baseName, stepCount), PreparedRecord(running) };
        TimeSpan elapsed = TimeSpan.Zero;
        for (int number = 1; number <= lastStepNumber; number++)
        {
            StepAttributes step = _quickSteps[number - 1];
            records.Add(new JournalStepStart { Index = number, Count = stepCount, Name = step.Name, Utc = running + elapsed });
            if (number < lastStepNumber)
            {
                elapsed += step.TimeBudget;
                StepOutcome outcome = FakeEngineData.Outcome(step, verifiedBytes: 5 * GiB);
                records.Add(JournalStepEnd.FromOutcome(outcome) with { Utc = running + elapsed });
            }
        }

        if (lastStepNumber > 0)
        {
            elapsed += TimeSpan.FromSeconds(11);
            records.Add(new JournalBeat
            {
                Step = lastStepNumber,
                ElapsedMs = (long)elapsed.TotalMilliseconds,
                Coverage = 30.5,
                Errors = 0,
                CpuC = 70.5,
                RamC = 47.0,
                CpuStatus = nameof(SensorStatus.Available),
                RamStatus = nameof(SensorStatus.Available),
                Utc = running + elapsed,
            });
        }

        return records.ToArray();
    }

    // A run whose FAIL verdict was journalled in step 1, with its memory released, but whose summaries were never
    // published: no end record.
    private static JournalRecord[] CompletedJournal(Guid runId, string baseName)
    {
        DateTimeOffset running = CrashedRunStart + TimeSpan.FromSeconds(2);
        StepAttributes first = _quickSteps[0];
        TimeSpan elapsed = TimeSpan.FromSeconds(12);
        DateTimeOffset completed = running + elapsed;
        DateTimeOffset verdict = completed + TimeSpan.FromMilliseconds(40);
        StepOutcome outcome = FakeEngineData.Outcome(first, elapsed, passes: 0, verifiedBytes: GiB, errors: 1, completed: false);
        return new JournalRecord[]
        {
            BeginRecord(runId, baseName, stepCount: 3),
            PreparedRecord(running),
            new JournalStepStart { Index = 1, Count = 3, Name = first.Name, Utc = running },
            JournalStepEnd.FromOutcome(outcome) with { Utc = completed },
            JournalResult.Create(
                RunState.Failed,
                RunEndReason.FirstError,
                null,
                1,
                GiB,
                elapsed,
                TimeSpan.FromSeconds(11),
                0,
                completed,
                verdict) with { Utc = verdict },
            new JournalReleased { ReleasedBytes = 20 * GiB, ReleaseMs = 900, Utc = completed + TimeSpan.FromSeconds(1) },
        };
    }
}

