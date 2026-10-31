using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RamQuick.Analysis;
using RamQuick.App.Services;
using RamQuick.App.Tests.Fakes;
using RamQuick.App.ViewModels;
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
    private readonly RecoveryFaultingClock _clock;
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

        Time = _clock = new RecoveryFaultingClock(StartTime);
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
    /// Makes every run journal write to one <see cref="StalledJournalStorage"/>, which the test can block or fail,
    /// through a handle of its own, as every run's journal opens its own handle on the one file
    /// <c>active-run.jsonl</c>. A journal closing its handle leaves the shared storage open for the next run, and a
    /// journal deleting through its handle deletes the shared journal, whichever run wrote it.
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
            journalFactory: (_, timeProvider) =>
                new ProgressJournal(new SharedJournalHandle(storage), journalTime ?? timeProvider));
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

    /// <summary>
    /// Makes the next crash recovery the controller runs throw <paramref name="fault"/> when it reads the clock, which
    /// it does once it holds a run, so a recovery failure reaches the controller as an exception.
    /// </summary>
    /// <param name="fault">The exception to throw, once.</param>
    public void FailNextRecovery(Exception fault) => _clock.FailNextRecovery(fault);

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
    /// Pumps until the run is in a terminal state and the controller has finished concluding it
    /// (<see cref="RunController.ConclusionTask"/>): the verdict and hints are shown and the <c>result</c> record has
    /// been acknowledged, has failed or has timed out, so the controller is releasing memory, saving results, or
    /// already done.
    /// </summary>
    public void AwaitVerdict() =>
        RunUntil(() => StateMachine.IsTerminal && Controller.ConclusionTask.IsCompleted);

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
    public Task ShutdownAsync() => OnUiThread(Controller.ShutdownAsync);

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
        return AwaitGate(Session.InvokeStepGateOnBackgroundThreadAsync(index), startedAt, timeout);
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
    /// monitor and source, and the temporary folder. The folder is deleted only after a bounded wait for the
    /// controller to go quiet: for a Start still in flight to end, for its background work
    /// (<see cref="RunController.HasBackgroundWork"/>) to finish, and for every journal's writer to retire.
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

        // App-error writes and journal appends recreate the folders they write to, so one that lands after the
        // deletion leaves the root behind. A withdrawn or refused Start discards its reserved .log and retires its
        // journal only as its continuations run on Context, so an unfinished one is pumped; a finished one is not,
        // so no unrelated queued post runs after the test. Neither wait throws when its bound passes: a test that
        // leaves work hung still gets as much of its folder deleted as can be.
        if (_controller is { } controller)
        {
            if (!controller.StartTask.IsCompleted)
            {
                _ = Context.RunUntil(() => controller.StartTask.IsCompleted, DefaultWait);
            }

            _ = SpinWait.SpinUntil(
                () => !controller.HasBackgroundWork && Journals.All(journal => journal.Retired.IsCompleted),
                DefaultWait);
        }

        // A test that installed the context and failed before disposing its scope leaves it on this thread.
        if (ReferenceEquals(SynchronizationContext.Current, Context))
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        DeleteFolder(RootPath);
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

    /// <summary>
    /// Deletes a test's temporary folder, retrying while the thread pool may still be appending to app-errors.log or a
    /// results log may still be draining into it.
    /// </summary>
    /// <param name="rootPath">The folder.</param>
    public static void DeleteFolder(string rootPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }

                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < DeleteAttempts)
            {
                Thread.Sleep(_deleteRetryDelay);
            }
        }
    }

    /// <summary>
    /// One journal's handle on the harness's shared <see cref="StalledJournalStorage"/>, standing for the file handle
    /// each run's journal opens on <c>active-run.jsonl</c>: appends after <see cref="Dispose"/> throw, as on a
    /// closed file, while every other member, deletion included, acts on the shared journal.
    /// </summary>
    private sealed class SharedJournalHandle : IJournalStorage
    {
        private readonly StalledJournalStorage _shared;
        private int _closed;

        public SharedJournalHandle(StalledJournalStorage shared) => _shared = shared;

        public string Location => _shared.Location;

        public bool Exists() => _shared.Exists();

        public void AppendDurable(string line)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            _shared.AppendDurable(line);
        }

        public IReadOnlyList<string> ReadLines() => _shared.ReadLines();

        public JournalContent ReadContent() => _shared.ReadContent();

        public void Delete() => _shared.Delete();

        public string MoveAside(DateTimeOffset utc) => _shared.MoveAside(utc);

        public void Dispose() => Volatile.Write(ref _closed, 1);
    }

    /// <summary>
    /// The harness clock: a <see cref="FakeTimeProvider"/> whose reading, once armed, throws for the next caller inside
    /// <see cref="CrashDetector"/>. Recovery reads the clock once it holds a run, and no journal content makes it
    /// throw, so this is how a test reaches the controller's handling of a recovery that fails unexpectedly.
    /// </summary>
    private sealed class RecoveryFaultingClock : FakeTimeProvider
    {
        private Exception? _recoveryFault;

        public RecoveryFaultingClock(DateTimeOffset start)
            : base(start)
        {
        }

        public void FailNextRecovery(Exception fault)
        {
            ArgumentNullException.ThrowIfNull(fault);
            Volatile.Write(ref _recoveryFault, fault);
        }

        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref _recoveryFault) is { } fault
                && CalledFromCrashDetector()
                && ReferenceEquals(Interlocked.CompareExchange(ref _recoveryFault, null, fault), fault))
            {
                throw fault;
            }

            return base.GetUtcNow();
        }

        // Recovery's own frame reads the clock, and is too large to be inlined into its caller.
        private static bool CalledFromCrashDetector() =>
            new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.DeclaringType == typeof(CrashDetector));
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

    // The forced collections a retirement-wait test runs at most before judging what a stalled journal writer keeps
    // alive; the pause after each lets thread-pool work started by the controller's disposal finish.
    private const int CollectionAttempts = 10;

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

    // Longer than ProgressJournal.Dispose's 1 s wait for a stalled writer, so a Start re-enabled once that bounded wait
    // returns, rather than once the writer has retired, shows within it.
    private static readonly TimeSpan _journalDisposeWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan _collectionPause = TimeSpan.FromMilliseconds(50);

    // How long a held app-error write stays held once the test has ended, so the harness's disposal is already under
    // way, with the write still running, when the write ends.
    private static readonly TimeSpan _heldWriteRelease = TimeSpan.FromMilliseconds(200);

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

        // min(0.90 × 1.5 GiB, 1.5 GiB − 1 GiB) plans 0.5 GiB, below the 1 GiB floor. Memory is judged after the data
        // folder's probe, so the Start is accepted first and refused by its continuation.
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.InsufficientMemory, harness.AwaitResult(harness.Controller.StartTask));

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

        // The probe runs asynchronously, so the Start is accepted first and refused once the probe fails.
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.False(harness.Controller.CanStart);
        harness.RunUntil(() => harness.Controller.CanStart);
        Assert.Equal(StartResult.DataFolderUnusable, harness.AwaitResult(harness.Controller.StartTask));

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
    }

    [Fact]
    public void TryStart_BeginRecordNotDurableWithinBound_RefusesAsUnusableDataFolderUntilWriterRetires()
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
        harness.RunUntil(() => harness.Controller.NoticeMessage == UiText.DataFolderUnusableMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);

        // The journal's Dispose gives up on the stalled writer after 1 s, but the writer still owns the journal file:
        // once its begin append returns it deletes that file, which by then would hold the next run's journal.
        ProgressJournal refused = Assert.Single(harness.Journals);
        Assert.False(harness.Context.RunUntil(() => harness.Controller.CanStart, _journalDisposeWindow));
        Assert.Equal(StartResult.Busy, harness.TryStart());
        Assert.False(refused.Retired.IsCompleted);
        Assert.False(harness.Controller.StartTask.IsCompleted);
        Assert.Equal(0, storage.DeleteCount);

        storage.Release();
        harness.RunUntil(() => harness.Controller.CanStart);
        Assert.Equal(StartResult.DataFolderUnusable, harness.AwaitResult(harness.Controller.StartTask));
        Assert.True(refused.Retired.IsCompleted);
        Assert.Equal(1, storage.DeleteCount);
        Assert.False(harness.JournalExists);

        // The name the refused Start reserved is given back.
        Assert.Empty(harness.RunLogPaths());

        // The next run's journal is the same file, and nothing of the refused run touches it any more.
        harness.StartAccepted();
        harness.Pump();
        JournalBegin begin = Assert.Single(harness.ReadJournal().OfType<JournalBegin>());
        Assert.Equal(RunIdOf(harness), begin.RunId.ToString("N"));
        Assert.Equal(1, storage.DeleteCount);
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

        // Start is offered again only once the journal has given up on its begin record too, and its writer, released
        // from the stalled append, has deleted the begin-only journal and retired.
        Assert.False(harness.Controller.CanStart);
        harness.Time.Advance(ProgressJournal.AcknowledgmentTimeout - harness.Options.BeginTimeout);
        storage.Release();
        harness.RunUntil(() => harness.Controller.CanStart);
        Assert.True(storage.IsDeleted);
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

        Assert.Equal(StartResult.DataFolderUnusable, harness.AwaitResult(harness.Controller.StartTask));
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
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;
        harness.Initialize();
        harness.Engine.QuerySystemException = new EngineException(EngineResultCode.Internal, "rq_query_system failed");

        // The engine is asked only after the data folder's probe, so the Start is accepted first.
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.EngineUnavailable, harness.AwaitResult(harness.Controller.StartTask));

        Assert.Equal(UiText.EngineError("rq_query_system failed"), harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
        Assert.Empty(harness.RunLogPaths());
        Assert.False(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("rq_query_system failed", StringComparison.Ordinal));
    }

    // The data folder is validated before memory: an unwritable folder is what a Start reports even when too little
    // memory is free or the engine cannot describe the machine, and the engine is not asked at all.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryStart_DataFolderUnwritableAndMemoryOrEngineFails_RefusesForTheDataFolder(bool engineFails)
    {
        using var harness = new ControllerHarness();
        harness.WithUnwritableDataDirectory();
        int transitions = 0;
        harness.StateMachine.StateChanged += (_, _) => transitions++;
        harness.Initialize();
        if (engineFails)
        {
            harness.Engine.QuerySystemException = new EngineException(EngineResultCode.Internal, "rq_query_system failed");
        }
        else
        {
            // 1.5 GiB available plans only 0.5 GiB, which on its own is refused as low memory.
            harness.Engine.WithAvailableBytes(3 * GiB / 2);
        }

        int queriesBefore = harness.Engine.QuerySystemCount;

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.DataFolderUnusable, harness.AwaitResult(harness.Controller.StartTask));

        Assert.Equal(UiText.DataFolderUnusableMessage, harness.Controller.NoticeMessage);
        Assert.Equal(queriesBefore, harness.Engine.QuerySystemCount);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, transitions);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
        Assert.True(harness.Controller.CanStart);
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
        Assert.Equal(StartResult.Accepted, harness.AwaitResult(harness.Controller.StartTask));

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

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.InsufficientMemory, harness.AwaitResult(harness.Controller.StartTask));

        Assert.Equal(UiText.LowMemoryMessage, harness.Controller.NoticeMessage);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Empty(harness.Journals);
        Assert.Empty(harness.RunLogPaths());
        Assert.True(harness.Controller.CanStart);
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
    public void Completed_ResultRecordStalled_ReportsReleasingMemoryFromTheTerminalTransitionAndPublishesAfterTheRecord()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        harness.Initialize();
        using var viewModel = new MainViewModel(harness.Controller, harness.StateMachine);
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        storage.BlockNext("result");

        session.RaiseCompleted(RunEndReason.Completed);
        harness.RunUntil(() => harness.StateMachine.IsTerminal);

        // The activity is set in the frame of the terminal transition, so with Start unavailable the step field never
        // reads "—", although the result record's acknowledgment is still pending.
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        Assert.Equal(UiText.ReleasingMemory, viewModel.CurrentStepText);
        Assert.False(viewModel.CanStart);
        harness.RunUntil(() => storage.BlockedCount == 1);
        Assert.False(harness.Controller.ConclusionTask.IsCompleted);

        // Released does not let the summaries overtake the result record.
        session.RaiseReleased(session.Options.RegionBytes, TimeSpan.FromMilliseconds(600));
        harness.Pump();
        Assert.Empty(harness.SummaryJsonPaths());
        Assert.Equal(UiText.ReleasingMemory, viewModel.CurrentStepText);

        storage.Release();
        harness.WaitForPublication();
        Assert.Equal(new[] { "begin", "prepared", "result", "released", "end" }, storage.RecordTypesWritten());
        Assert.Single(harness.SummaryJsonPaths());
        Assert.Equal(UiText.NotYetKnown, viewModel.CurrentStepText);
    }

    // The fallback frees only the publication. Start waits for Released itself, which a slow release of a large region
    // delays and a chunk Windows refuses to free withholds; a controller that gives up on it leaves Start unavailable.
    [Theory]
    [InlineData("Released", false)]
    [InlineData("Released", true)]
    [InlineData("Dispose", false)]
    [InlineData("ShutdownAsync", false)]
    public void Released_NotWithinFallback_PublishesWithUnconfirmedReleaseButStartWaitsForReleased(string ending, bool publicationFails)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.RunToVerdict();
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        if (publicationFails)
        {
            // A file already holding the JSON summary's final name makes the publication fail.
            string baseName = Path.GetFileNameWithoutExtension(Assert.Single(harness.RunLogPaths()));
            File.WriteAllText(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName), "{}");
        }

        harness.Time.Advance(harness.Options.PublicationFallback);
        harness.RunUntil(() => harness.Controller.NoticeMessage?.EndsWith(UiText.ReleaseNotConfirmedNotice, StringComparison.Ordinal) == true);

        // The summaries are published without a release time, and any notice already shown stays ahead of the new one.
        string? earlierNotice = null;
        if (publicationFails)
        {
            string notice = harness.Controller.NoticeMessage!;
            earlierNotice = notice[..^(UiText.NoticeSeparator.Length + UiText.ReleaseNotConfirmedNotice.Length)];
            Assert.StartsWith(UiText.ResultsNotSaved(string.Empty), earlierNotice, StringComparison.Ordinal);
            Assert.Equal(earlierNotice + UiText.NoticeSeparator + UiText.ReleaseNotConfirmedNotice, notice);
            Assert.True(harness.JournalExists);
        }
        else
        {
            Assert.Equal(UiText.ReleaseNotConfirmedNotice, harness.Controller.NoticeMessage);
            using JsonDocument summary = harness.ReadSummary();
            Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("memory").GetProperty("releaseSeconds").ValueKind);
            Assert.Equal("PASS", summary.RootElement.GetProperty("run").GetProperty("verdict").GetString());
            Assert.False(harness.JournalExists);
        }

        // ResultsLog's wording is internal to RamQuick.Logging; this is the wording the results-log contract names.
        Assert.Contains("memory release not confirmed", harness.LogText(), StringComparison.Ordinal);

        // The fallback is no Released: the session stays subscribed and undisposed, and Start unavailable.
        harness.Pump();
        Assert.False(harness.Controller.CanStart);
        Assert.Equal(StartResult.Busy, harness.TryStart());
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        Assert.Equal(0, session.DisposeCount);
        Assert.False(harness.Controller.PostRunTask.IsCompleted);

        switch (ending)
        {
            case "Released":
                // A late Released ends the teardown: the session is disposed and Start offered, without the notice.
                harness.Release(TimeSpan.FromSeconds(42));
                Assert.Equal(earlierNotice, harness.Controller.NoticeMessage);
                Assert.Equal(1, session.DisposeCount);
                harness.StartAccepted();
                return;
            case "Dispose":
                harness.Controller.Dispose();
                break;
            default:
                // Past its deadline the shutdown disposes the session without waiting for Released.
                harness.AwaitTask(harness.ShutdownAsync());
                break;
        }

        // The controller gave up on Released: the teardown ends, and Start stays unavailable with the notice shown.
        harness.AwaitTask(harness.Controller.PostRunTask);
        harness.RunUntil(() => session.IsDisposed);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(harness.Controller.CanStart);
        Assert.Equal(StartResult.Busy, harness.TryStart());
        Assert.Equal(PostRunActivity.ReleasingMemory, harness.Controller.PostRunActivity);
        Assert.Equal(UiText.ReleaseNotConfirmedNotice, harness.Controller.NoticeMessage);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completed_BeforeFirstProgress_FinalSnapshotCarriesTheSummarysCoverage(bool prepared)
    {
        // A stop can interrupt step 1's first pass before the first progress tick, and the engine sends no final tick,
        // so only Completed carries the verified bytes. Without Prepared there is no tested size, so coverage is 0.
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        IReadOnlyList<StepAttributes> steps = session.Options.Steps;
        ulong verifiedBytes = session.Options.RegionBytes / 8 * 3;
        TimeSpan elapsed = TimeSpan.FromMilliseconds(200);
        ulong totalErrors = 2;
        ulong testedBytes = 0;
        if (prepared)
        {
            testedBytes = harness.Prepare().TestedBytes;
            (StepGateDecision decision, _) = harness.RunGate(0);
            Assert.Equal(StepGateDecision.Continue, decision);
            session.RaiseStepStarted(0);
            session.RaiseStepFinished(0, actualDuration: elapsed, passes: 0, verifiedBytes: verifiedBytes, errors: totalErrors, completed: false);
        }

        harness.Pump();
        Assert.Null(harness.Controller.CurrentProgress);

        session.RaiseCompleted(RunEndReason.UserStop, totalErrors, verifiedBytes: verifiedBytes, elapsed: elapsed);
        harness.AwaitVerdict();

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        RunProgress? final = harness.Controller.CurrentProgress;
        Assert.NotNull(final);
        Assert.Equal(0, final.StepIndex);
        Assert.Equal(steps.Count, final.StepCount);
        Assert.Equal(steps[0].Name, final.StepName);
        Assert.Equal(elapsed, final.Elapsed);
        Assert.Null(final.Remaining);
        Assert.Equal(verifiedBytes, final.VerifiedBytes);
        Assert.Equal(testedBytes, final.RegionBytes);
        Assert.Equal(prepared ? 100.0 * verifiedBytes / testedBytes : 0, final.CoveragePercent);
        Assert.Equal(totalErrors, final.TotalErrors);
        Assert.Equal(totalErrors, harness.Controller.TotalErrors);

        harness.Release(TimeSpan.FromMilliseconds(100));
        using JsonDocument summary = harness.ReadSummary();
        JsonElement root = summary.RootElement;
        Assert.Equal(final.CoveragePercent, root.GetProperty("coverage").GetProperty("percent").GetDouble());
        Assert.Equal(totalErrors, root.GetProperty("errors").GetProperty("total").GetUInt64());
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

    [Fact]
    public void Publication_EndAppendStalledPastJournalDispose_KeepsStartUnavailableUntilWriterRetires()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        harness.Initialize();
        FakeEngineSession session = harness.RunToVerdict();
        Guid endedRunId = Assert.Single(harness.ReadJournal().OfType<JournalBegin>()).RunId;
        storage.BlockNext("end");

        // Publication waits 5 s for the end record, then tears the run down.
        session.RaiseReleased(session.Options.RegionBytes, TimeSpan.FromMilliseconds(400));
        harness.RunUntil(() => storage.BlockedCount == 1);
        harness.Time.Advance(ProgressJournal.AcknowledgmentTimeout);
        harness.RunUntil(() => session.IsDisposed);
        Assert.Single(harness.SummaryJsonPaths());

        // The journal's Dispose gives up on the stalled writer after 1 s, but once its end append returns the writer
        // deletes the journal file, which by then would hold the next run's journal.
        ProgressJournal ended = Assert.Single(harness.Journals);
        Assert.False(harness.Context.RunUntil(() => harness.Controller.CanStart, _journalDisposeWindow));
        Assert.Equal(StartResult.Busy, harness.TryStart());
        Assert.Equal(PostRunActivity.SavingResults, harness.Controller.PostRunActivity);
        Assert.False(ended.Retired.IsCompleted);
        Assert.Equal(0, storage.DeleteCount);

        storage.Release();
        harness.WaitForPublication();
        Assert.True(ended.Retired.IsCompleted);
        Assert.Equal(1, storage.DeleteCount);
        Assert.False(harness.JournalExists);

        // The next run's journal is the same file, and nothing of the ended run touches it any more.
        harness.StartAccepted();
        harness.Pump();
        JournalBegin begin = Assert.Single(harness.ReadJournal().OfType<JournalBegin>());
        Assert.NotEqual(endedRunId, begin.RunId);
        Assert.Equal(1, storage.DeleteCount);
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

    // A managed exception from the engine seam can name a path in the user's profile. app-errors.log, the local
    // diagnostic log, keeps the full exception; the displayed reason, the journal's result record, both shareable
    // summaries and the .log's COMPLETED and VERDICT lines do not.
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
        Assert.Equal(UiText.EngineError(Redacted), harness.Controller.ResultMessage);
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

        Assert.Equal(StartResult.Withdrawn, harness.AwaitResult(harness.Controller.StartTask));
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

        Assert.Equal(StartResult.Withdrawn, harness.AwaitResult(harness.Controller.StartTask));
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

    // A stop the engine refuses records no reason, so no Completed answers it: the run ends at once as the engine
    // fault, and the teardown's disposal, which stops the engine and releases the memory, alone gates the next Start.
    [Fact]
    public void Stop_EngineRefusesTheUserStop_AbortsAsEngineFaultAndOffersStartOnlyAfterDisposal()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        var refusal = new EngineException(
            EngineResultCode.Internal,
            "rq_session_stop failed (Internal, -99): Internal engine error",
            "Internal engine error");
        session.RequestStopException = refusal;
        session.BlockDispose();
        try
        {
            // Called with the harness context installed, as the Stop command is, so the conclusion it begins
            // resumes on the "UI thread".
            _ = harness.OnUiThread(() =>
            {
                harness.Controller.RequestStop();
                return true;
            });

            Assert.Equal(RunState.Aborted, harness.StateMachine.State);
            Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
            Assert.Equal(refusal.Message, harness.StateMachine.EndDetail);
            Assert.Equal(UiText.EngineError(refusal.Message), harness.Controller.ResultMessage);
            Assert.Empty(session.StopRequests);
            Assert.False(session.StopEvent.WaitOne(0), "The stop event was set although the engine refused the stop.");

            // A disposed session raises no Released, so the summaries are published without waiting for one, and
            // Start stays unavailable while the disposal is still running.
            harness.RunUntil(() => session.DisposeCount == 1);
            Assert.Single(harness.SummaryJsonPaths());
            harness.Pump();
            Assert.False(harness.Controller.CanStart);
        }
        finally
        {
            session.ReleaseDispose();
        }

        harness.WaitForPublication();
        Assert.True(session.IsDisposed);
        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement run = summary.RootElement.GetProperty("run");
            Assert.Equal("ABORTED", run.GetProperty("verdict").GetString());
            Assert.Equal(nameof(RunEndReason.EngineFault), run.GetProperty("endReason").GetString());
        }

        // The disposal's own stop was refused too and reported, not thrown. Neither refusal was a recorded stop, so the
        // .log has no STOP_REQUESTED line; app-errors.log has both.
        (string callbackName, Exception reported) = Assert.Single(session.CallbackFaults);
        Assert.Equal("rq_session_stop", callbackName);
        Assert.Same(refusal, reported);
        Assert.DoesNotContain(RunLogLines(harness), line => EventOf(line) == "STOP_REQUESTED");
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("source=\"RunController.RequestEngineStop\"", StringComparison.Ordinal)
            && harness.AppErrorsLogText().Contains("source=\"EngineCallback:rq_session_stop\"", StringComparison.Ordinal)
            && harness.AppErrorsLogText().Contains(refusal.Message, StringComparison.Ordinal));
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

    // The UI thread stalls while the sensor thread keeps reading: the post of a reading at both limits waits in the
    // queue, behind fresh cool readings, until it is older than the monitor's freshness bound. Its late delivery must
    // neither stop the run nor be shown or recorded as a fresh temperature.
    [Fact]
    public void Readings_AtLimitsButDeliveredAfterStaleAfter_NeverStopAndNewestFreshReadingIsShown()
    {
        const double CoolCpuCelsius = 60.0;
        const double CoolRamCelsius = 50.0;
        using var harness = new ControllerHarness();
        harness.StartSensors();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        harness.Pump();

        harness.Sensors.SetCpu(ThermalLimits.CpuLimitCelsius);
        harness.Sensors.SetRam(ThermalLimits.RamLimitCelsius);
        DateTimeOffset hotCapturedAt = ReadWithoutPumping(harness);
        harness.Sensors.SetCpu(CoolCpuCelsius);
        harness.Sensors.SetRam(CoolRamCelsius);
        DateTimeOffset coolCapturedAt = hotCapturedAt;
        for (int read = 0; read < 3; read++)
        {
            coolCapturedAt = ReadWithoutPumping(harness);
        }

        // Half a second after the last read, no tick due: the reading at the limits is 3.5 s old.
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(harness.Time.GetUtcNow() - hotCapturedAt > SensorMonitor.StaleAfter);
        harness.RunUntil(() => harness.Controller.LatestReadings.Cpu.CapturedAt == coolCapturedAt);
        harness.Pump();

        Assert.Empty(session.StopRequests);
        Assert.Equal(RunState.Running, harness.StateMachine.State);
        SensorReadings shown = harness.Controller.LatestReadings;
        Assert.Equal(SensorStatus.Available, shown.Cpu.Status);
        Assert.Equal(SensorStatus.Available, shown.Ram.Status);
        Assert.Equal(CoolCpuCelsius, shown.Cpu.Celsius);
        Assert.Equal(CoolRamCelsius, shown.Ram.Celsius);

        // The summary's peaks are fresh readings only, so the late reading at the limits is not among them.
        harness.Complete(RunEndReason.Completed);
        harness.Release(TimeSpan.FromMilliseconds(100));
        using JsonDocument summary = harness.ReadSummary();
        JsonElement temperatures = summary.RootElement.GetProperty("temperatures");
        Assert.Equal(CoolCpuCelsius, temperatures.GetProperty("cpuMaxC").GetDouble());
        Assert.Equal(CoolRamCelsius, temperatures.GetProperty("ramMaxC").GetDouble());
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
        Task<StepGateDecision> first = session.InvokeStepGateOnBackgroundThreadAsync(0);
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
        Task<StepGateDecision> second = session.InvokeStepGateOnBackgroundThreadAsync(1);
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

        Task<StepGateDecision> gate = session.InvokeStepGateOnBackgroundThreadAsync(0);

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
    public void StepGate_StopWhileEarlierEventsUndelivered_AbortsWithinTwoSecondsWithoutHostFault()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        // The UI context is not pumped, so the gate's barrier stays queued.
        Task<StepGateDecision> gate = session.InvokeStepGateOnBackgroundThreadAsync(0);
        Assert.False(
            SpinWait.SpinUntil(() => gate.IsCompleted, _undeliveredEventsWindow),
            "The gate returned while its barrier was still queued.");
        long stoppedAt = Stopwatch.GetTimestamp();
        harness.Controller.RequestStop();
        Assert.True(
            SpinWait.SpinUntil(() => gate.IsCompleted, _stopBound),
            "The gate did not honour the stop while its barrier was queued.");
        Assert.True(harness.Context.PendingCount > 0, "The barrier ran although the UI context was never pumped.");

        // Pumping runs the barrier after the gate has disposed its event; nothing may reach the dispatcher.
        (StepGateDecision decision, TimeSpan elapsed) = harness.AwaitGate(gate, stoppedAt);

        Assert.Equal(StepGateDecision.Abort, decision);
        Assert.True(elapsed < _stopBound, $"The gate returned {elapsed.TotalMilliseconds:0} ms after Stop.");
        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        Assert.Equal(RunEndReason.UserStop, session.FirstRecordedEndReason);
        Assert.Empty(harness.ReadJournal().OfType<JournalStepStart>());

        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.UserStop, harness.StateMachine.EndReason);
        Assert.Equal(UiText.StoppedByUser, harness.Controller.ResultMessage);
    }

    [Fact]
    public void StepGate_EarlierEventsUndeliveredForTheWholeBound_RequestsHostFaultBeforeAbort()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        // The UI context is not pumped for the gate's whole real-time bound, so its barrier never runs in time.
        long startedAt = Stopwatch.GetTimestamp();
        Task<StepGateDecision> gate = session.InvokeStepGateOnBackgroundThreadAsync(0);
        Assert.True(
            SpinWait.SpinUntil(() => gate.IsCompleted, _gateTimeoutWait),
            "The gate never gave up on its undelivered barrier.");
        Assert.True(harness.Context.PendingCount > 0, "The barrier ran although the UI context was never pumped.");

        // Pumping runs the barrier after the gate has disposed its event; nothing may reach the dispatcher.
        (StepGateDecision decision, TimeSpan elapsed) = harness.AwaitGate(gate, startedAt);

        Assert.Equal(StepGateDecision.Abort, decision);
        Assert.True(elapsed >= _gateTimeoutFloor, $"The gate gave up after {elapsed.TotalMilliseconds:0} ms.");
        GateInvocation invocation = Assert.Single(session.GateInvocations);
        Assert.Equal(1, invocation.StopRequestCountAtReturn);
        Assert.Equal(new[] { StopReason.HostFault }, session.StopRequests);
        Assert.Empty(harness.ReadJournal().OfType<JournalStepStart>());

        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(UiText.JournalWriteFailed(UiText.JournalAcknowledgmentTimedOut), harness.Controller.ResultMessage);
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

    // The failed write names the journal in the user's profile: the displayed reason and the persisted cause are
    // redacted alike.
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
        string redacted = UiText.JournalWriteFailed(
            @"The process cannot access the file '%USERPROFILE%\RamQuick\data\journal\active-run.jsonl'.");
        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
        Assert.Equal(redacted, harness.Controller.ResultMessage);

        harness.Release(TimeSpan.FromMilliseconds(100));
        AssertPublishedEndDetail(harness, redacted);
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

    // A host-fault stop the engine refuses on the coordinator is only recorded there; the UI thread answers it on the
    // next session event, as the engine fault, unless the engine's own Completed comes first and settles the run.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackFault_EngineRefusesItsHostFaultStop_AnsweredOnTheNextEventUnlessCompletedComesFirst(bool completedFirst)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        var fault = new InvalidOperationException("progress sink failed");
        var refusal = new EngineException(
            EngineResultCode.Internal,
            "rq_session_stop failed (Internal, -99): Internal engine error",
            "Internal engine error");
        session.RequestStopException = refusal;

        // The test thread plays the coordinator: the controller's sink and then the session ask for a host-fault stop,
        // and the engine refuses both.
        session.InvokeCallbackFault("on_progress", fault);

        Assert.Equal(RunState.Running, harness.StateMachine.State);
        Assert.Empty(session.StopRequests);
        Assert.Equal(new[] { "on_progress", "rq_session_stop" }, session.CallbackFaults.Select(reported => reported.CallbackName));

        if (completedFirst)
        {
            harness.Complete(RunEndReason.HostFault);
            Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
            Assert.Equal(UiText.HostFault(fault.Message), harness.Controller.ResultMessage);
            harness.Release(TimeSpan.FromMilliseconds(100));
        }
        else
        {
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
            harness.AwaitVerdict();
            Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
            Assert.Equal(UiText.EngineError(refusal.Message), harness.Controller.ResultMessage);
            harness.WaitForPublication();
        }

        Assert.Equal(RunState.Aborted, harness.StateMachine.State);
        Assert.True(session.IsDisposed);
        Assert.DoesNotContain(RunLogLines(harness), line => EventOf(line) == "STOP_REQUESTED");
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("source=\"RunController.RequestEngineStop\"", StringComparison.Ordinal)
            && harness.AppErrorsLogText().Contains("source=\"EngineCallback:rq_session_stop\"", StringComparison.Ordinal));
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

    // A notice or result reason that substitutes a runtime text is redacted like that text's persisted copies: an
    // exception naming a file in the user's profile, or a publication failure naming the data folder, shows the
    // UiText wording with placeholders, never the path or the account name.
    [Theory]
    [InlineData("QuerySystem")]
    [InlineData("CreateSession")]
    [InlineData("SessionStart")]
    [InlineData("CallbackFault")]
    [InlineData("Publication")]
    public void DisplayedFailure_TextNamesPersonalPath_ShowsWordingWithPlaceholdersOnly(string failurePoint)
    {
        using var harness = new ControllerHarness();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string secretPath = Path.Combine(profile, "AppData", "Local", "RamQuick", "secret.bin");
        var failure = new UnauthorizedAccessException("Access to the path '" + secretPath + "' is denied.");
        string[] placeholders = { "%USERPROFILE%", "%LOCALAPPDATA%" };
        string expectedPrefix;
        string displayed;
        switch (failurePoint)
        {
            case "QuerySystem":
                harness.Initialize();
                harness.Engine.QuerySystemException = failure;
                Assert.Equal(StartResult.Accepted, harness.TryStart());
                Assert.Equal(StartResult.EngineUnavailable, harness.AwaitResult(harness.Controller.StartTask));
                expectedPrefix = UiText.EngineError(string.Empty);
                displayed = harness.Controller.NoticeMessage!;
                break;

            case "CreateSession" or "SessionStart":
                if (failurePoint == "CreateSession")
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
                Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
                expectedPrefix = UiText.EngineError(string.Empty);
                displayed = harness.Controller.ResultMessage!;
                Assert.Equal(UiText.EngineError(harness.StateMachine.EndDetail!), displayed);
                break;

            case "CallbackFault":
                harness.Initialize();
                FakeEngineSession session = harness.StartAccepted();
                harness.Prepare();
                session.InvokeCallbackFault("on_progress", failure);
                session.CompleteWithFirstRecordedReason();
                harness.AwaitVerdict();
                Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
                expectedPrefix = UiText.HostFault(string.Empty);
                displayed = harness.Controller.ResultMessage!;
                Assert.Equal(harness.StateMachine.EndDetail, displayed);
                break;

            default:
                // A folder stands where publication writes the JSON summary's temporary file ("<final name>.tmp"),
                // so, with no access-control change, publication fails with an access error naming that file. The
                // data folder lies in the temporary folder and holds a segment named after the account.
                DataDirectory data = DataDirectory.FromRoot(Path.Combine(harness.RootPath, Environment.UserName, "data"));
                harness.Data = data;
                harness.Initialize();
                harness.RunToVerdict();
                string baseName = Path.GetFileNameWithoutExtension(Assert.Single(harness.RunLogPaths()));
                Directory.CreateDirectory(ResultsLog.GetSummaryJsonPath(data, baseName) + ".tmp");
                harness.Release(TimeSpan.FromMilliseconds(100));
                placeholders = new[] { "%TEMP%" };
                expectedPrefix = UiText.ResultsNotSaved(string.Empty);
                displayed = harness.Controller.NoticeMessage!;
                break;
        }

        Assert.StartsWith(expectedPrefix, displayed, StringComparison.Ordinal);
        Assert.True(
            placeholders.Any(placeholder => displayed.Contains(placeholder, StringComparison.Ordinal)),
            $"'{displayed}' holds none of {string.Join(", ", placeholders)}.");
        Assert.DoesNotContain(profile, displayed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.RootPath, displayed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\" + Environment.UserName + @"\", displayed, StringComparison.OrdinalIgnoreCase);
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
    public void InitializeAsync_JournalStepNameHoldsLineBreaksAndControls_ShowsTheReasonOnOneLine()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        JournalRecord[] journal = UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 2);
        int stepIndex = Array.FindLastIndex(journal, record => record is JournalStepStart);
        journal[stepIndex] = (JournalStepStart)journal[stepIndex] with
        {
            Name = "Chain\r\nSPOOFED: PASS \u001b[32m<b>ok</b>\u0000\u202E",
        };
        harness.WriteJournal(journal);

        harness.Initialize();

        // A forged line break must not put a "PASS" line beside the FAIL badge, and a NUL must not cut the
        // reason's automation name short: the step name is shown as one line of plain text.
        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        string? reason = harness.Controller.ResultMessage;
        Assert.Equal(UiText.PreviousRunUnfinished(2, 3, "Chain  SPOOFED: PASS  [32m<b>ok</b>"), reason);
        Assert.DoesNotContain(
            reason!,
            character => CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format);
    }

    // A fault's runtime text is shown as one line of plain text, as a journal step name is: a forged line break must
    // not put a line beside the ABORTED badge or under the notice, and an escape, a NUL, a bidirectional control or a
    // line separator must not hide, cut or reorder the text. Each replaced character is one space, as in the text
    // summary. A run's persisted detail is that same line: the state machine's, which the VERDICT line writes, the
    // result record's and both summaries' state what the window shows, and no .log line begins with the forged text.
    // Redaction comes first: the other account's folder name ends at the line break and is redacted alone, and the
    // forged text after it is kept. It comes last too: the user name followed by an escape is redacted once the
    // escape is a space, as every persisted copy redacts it.
    [Theory]
    [InlineData("CallbackFault")]
    [InlineData("JournalFailure")]
    [InlineData("SessionStart")]
    [InlineData("EngineFaultDetail")]
    [InlineData("QuerySystem")]
    public void DisplayedFailure_TextHoldsLineBreaksAndControls_ShowsAndPersistsOneLineOfPlainText(string failurePoint)
    {
        const string OneLine =
            @"QA sink boom at '%LOCALAPPDATA%\RamQuick\x.tmp' after C:\Users\<user>  "
            + @"2026-01-01T00:00:00Z APP_ERROR source=""Forged""  [31m   done in D:\share\<user> [0m";
        const string ForgedLine = "2026-01-01T00:00:00Z APP_ERROR";
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string message = "QA sink boom at '" + Path.Combine(localAppData, "RamQuick", "x.tmp") + "'"
            + @" after C:\Users\Alice Smith" + "\r\n"
            + ForgedLine + " source=\"Forged\" \u001b[31m\u0000\u202E\u2028done"
            + @" in D:\share\" + Environment.UserName + "\u001b[0m";
        string expected;
        string? persisted;
        string displayed;
        switch (failurePoint)
        {
            case "CallbackFault":
                harness.Initialize();
                harness.StartAccepted();
                harness.Prepare();
                harness.Session.InvokeCallbackFault("on_progress", new InvalidOperationException(message));
                harness.Session.CompleteWithFirstRecordedReason();
                harness.AwaitVerdict();
                Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
                expected = UiText.HostFault(OneLine);
                persisted = expected;
                displayed = harness.Controller.ResultMessage!;
                harness.Release(TimeSpan.FromMilliseconds(100));
                break;

            case "JournalFailure":
                harness.Initialize();
                FakeEngineSession session = harness.StartAccepted();
                harness.Prepare();
                ProgressJournal journal = Assert.Single(harness.Journals);
                harness.Storage!.FailNext("stepEnd", new IOException(message));
                session.RaiseStepFinished(0);
                harness.RunUntil(() => journal.Failure is not null);
                StepAttributes next = session.Options.Steps[1];
                session.PublishProgress(FakeEngineData.Progress(
                    next.Index,
                    session.Options.Steps.Count,
                    next.Name,
                    TimeSpan.FromSeconds(45),
                    TimeSpan.FromSeconds(255),
                    2 * GiB,
                    session.Options.RegionBytes,
                    0));
                harness.RunUntil(() => session.StopRequests.Count > 0);
                session.CompleteWithFirstRecordedReason();
                harness.AwaitVerdict();
                Assert.Equal(RunEndReason.HostFault, harness.StateMachine.EndReason);
                expected = UiText.JournalWriteFailed(OneLine);
                persisted = expected;
                displayed = harness.Controller.ResultMessage!;
                harness.Release(TimeSpan.FromMilliseconds(100));
                break;

            case "SessionStart":
                harness.Engine.StartException = new IOException(message);
                harness.Initialize();
                Assert.Equal(StartResult.Accepted, harness.TryStart());
                harness.WaitForPublication();
                Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
                expected = UiText.EngineError(OneLine);
                persisted = OneLine;
                displayed = harness.Controller.ResultMessage!;
                break;

            case "EngineFaultDetail":
                harness.Initialize();
                harness.StartAccepted();
                harness.Prepare();
                harness.Complete(RunEndReason.EngineFault, endDetail: message);
                Assert.Equal(RunEndReason.EngineFault, harness.StateMachine.EndReason);
                expected = UiText.EngineError(OneLine);
                persisted = OneLine;
                displayed = harness.Controller.ResultMessage!;
                harness.Release(TimeSpan.FromMilliseconds(100));
                break;

            default:
                harness.Initialize();
                harness.Engine.QuerySystemException = new InvalidOperationException(message);
                Assert.Equal(StartResult.Accepted, harness.TryStart());
                Assert.Equal(StartResult.EngineUnavailable, harness.AwaitResult(harness.Controller.StartTask));
                expected = UiText.EngineError(OneLine);
                persisted = null;
                displayed = harness.Controller.NoticeMessage!;
                break;
        }

        Assert.Equal(expected, displayed);
        Assert.DoesNotContain(
            displayed,
            character => CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator);
        Assert.DoesNotContain(profile, displayed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", displayed, StringComparison.OrdinalIgnoreCase);
        if (persisted is null)
        {
            return;
        }

        Assert.Equal(persisted, harness.StateMachine.EndDetail);
        AssertPublishedEndDetail(harness, persisted);
        Assert.DoesNotContain(
            harness.LogText().Split('\r', '\n'),
            line => line.StartsWith(ForgedLine, StringComparison.Ordinal));
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

    // A damaged journal still shows the crashed run's FAIL, at its step when the journal shows it, publishes what can
    // be rebuilt, and tells the user what was damaged and where the journal was kept, so the next Start can run.
    [Theory]
    [InlineData("ForeignPassResult", "a result record of another run was ignored", true)]
    [InlineData("MissingBegin", "the journal's begin record is missing or could not be read", true)]
    [InlineData("NotJson", "the journal's begin record is missing or could not be read", false)]
    public void InitializeAsync_DamagedJournal_FailsNamesTheDamageAndKeepsTheJournalAside(
        string journalCase,
        string expectedDamage,
        bool stepKnown)
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        JournalRecord[] journal = UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 2);
        switch (journalCase)
        {
            case "ForeignPassResult":
                DateTimeOffset completed = ControllerHarness.StartTime - TimeSpan.FromMinutes(30);
                JournalResult foreign = JournalResult.Create(
                    RunState.Passed,
                    RunEndReason.Completed,
                    null,
                    0,
                    GiB,
                    TimeSpan.FromSeconds(30),
                    null,
                    3,
                    completed,
                    completed + TimeSpan.FromMilliseconds(40));
                harness.WriteJournal(journal.Append(foreign with { RunId = Guid.NewGuid(), Utc = completed }).ToArray());
                break;
            case "MissingBegin":
                harness.WriteJournal(journal.Skip(1).ToArray());
                break;
            default:
                Directory.CreateDirectory(harness.Data!.JournalDirectory);
                File.WriteAllText(harness.Data.JournalPath, "not json\n{\n");
                break;
        }

        harness.Initialize();

        Assert.Equal(RunState.Failed, harness.StateMachine.State);
        Assert.Equal(RunEndReason.PreviousRunUnfinished, harness.StateMachine.EndReason);
        Assert.Equal(
            stepKnown ? UiText.PreviousRunUnfinished(2, 3, _quickSteps[1].Name) : UiText.PreviousRunUnfinishedStepUnknown,
            harness.Controller.ResultMessage);
        Assert.False(harness.JournalExists);
        string kept = Assert.Single(Directory.GetFiles(Path.GetFullPath(harness.Data!.JournalDirectory), "active-run.damaged-*.jsonl"));
        Assert.Equal(
            UiText.RecoveredDamagedNotice(expectedDamage, SummaryFormat.RedactPersonalPaths(kept)),
            harness.Controller.NoticeMessage);
        Assert.True(harness.Controller.CanStart);
        using (JsonDocument summary = harness.ReadSummary())
        {
            JsonElement run = summary.RootElement.GetProperty("run");
            Assert.Equal("FAIL", run.GetProperty("verdict").GetString());
            Assert.True(run.GetProperty("partial").GetBoolean());
            Assert.EndsWith(
                "The progress journal held damaged data: " + expectedDamage + ".",
                run.GetProperty("endDetail").GetString(),
                StringComparison.Ordinal);
        }

        // The kept journal never blocks the next run.
        harness.StartAccepted();
        Assert.Null(harness.Controller.ResultMessage);
        Assert.NotEqual(runId, Assert.IsType<JournalBegin>(harness.ReadJournal()[0]).RunId);
    }

    [Theory]
    [InlineData(null, "Results of the previous test were recovered and saved, but its progress journal held damaged data: a repeated begin record.")]
    [InlineData(
        @"C:\RamQuick\data\journal\active-run.damaged-20261028-140000.jsonl",
        "Results of the previous test were recovered and saved, but its progress journal held damaged data: a repeated begin record. The damaged journal was kept as 'C:\\RamQuick\\data\\journal\\active-run.damaged-20261028-140000.jsonl'.")]
    public void UiText_DamagedJournalWording_NamesTheDamageAndTheKeptJournal(string? keptPath, string expectedNotice)
    {
        Assert.Equal(expectedNotice, UiText.RecoveredDamagedNotice("a repeated begin record", keptPath));
        Assert.Equal(
            "The previous test did not finish, which likely indicates severe instability. The step at which it stopped is unknown because its progress journal is damaged.",
            UiText.PreviousRunUnfinishedStepUnknown);
        Assert.Throws<ArgumentNullException>(() => UiText.RecoveredDamagedNotice(null!, keptPath));
    }

    [Fact]
    public void TryStart_DamagedJournalOnDisk_PublishesItWithDamagedNoticeAndStarts()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        JournalRecord[] journal = CompletedJournal(runId, baseName);

        // The released record names no run, so it is not taken as this run's.
        journal[^1] = ((JournalReleased)journal[^1]) with { RunId = Guid.Empty };
        harness.WriteJournal(journal);

        FakeEngineSession session = harness.StartAccepted();

        string kept = Assert.Single(Directory.GetFiles(Path.GetFullPath(harness.Data!.JournalDirectory), "active-run.damaged-*.jsonl"));
        Assert.Equal(
            UiText.RecoveredDamagedNotice("a released record of another run was ignored", SummaryFormat.RedactPersonalPaths(kept)),
            harness.Controller.NoticeMessage);
        Assert.True(File.Exists(ResultsLog.GetSummaryJsonPath(harness.Data!, baseName)));
        Assert.True(session.IsStarted);
        Assert.NotEqual(runId, Assert.IsType<JournalBegin>(harness.ReadJournal()[0]).RunId);
    }

    // A recovery that fails is a failure to save the earlier run's results, which the user is told of; the folder
    // itself is usable, so it is never reported as unusable.
    [Fact]
    public void InitializeAsync_RecoveryThrows_ShowsResultsNotSavedAndLogsTheError()
    {
        using var harness = new ControllerHarness();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(UnfinishedJournal(runId, baseName, stepCount: 3, lastStepNumber: 2));
        harness.FailNextRecovery(new InvalidOperationException("Simulated recovery fault"));

        harness.Initialize();

        Assert.Equal(UiText.ResultsNotSaved("Simulated recovery fault"), harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.True(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("Simulated recovery fault", StringComparison.Ordinal));
    }

    [Fact]
    public void TryStart_RecoveryOfJournalOnDiskThrows_RefusesWithResultsNotSavedNotDataFolderUnusable()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        (Guid runId, string baseName) = NewRunIdentity(TestType.Quick);
        harness.WriteJournal(CompletedJournal(runId, baseName));
        harness.FailNextRecovery(new InvalidOperationException("Simulated recovery fault"));

        Assert.Equal(StartResult.Accepted, harness.TryStart());
        Assert.Equal(StartResult.DataFolderUnusable, harness.AwaitResult(harness.Controller.StartTask));

        Assert.Equal(UiText.ResultsNotSaved("Simulated recovery fault"), harness.Controller.NoticeMessage);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.True(harness.JournalExists);
        Assert.True(harness.Controller.CanStart);
        harness.RunUntil(() => harness.AppErrorsLogText().Contains("Simulated recovery fault", StringComparison.Ordinal));

        // The next Start retries the recovery, which retires the journal, and runs.
        FakeEngineSession session = harness.StartAccepted();
        Assert.True(session.IsStarted);
        Assert.NotEqual(runId, Assert.IsType<JournalBegin>(harness.ReadJournal()[0]).RunId);
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

        Task shutdown = harness.ShutdownAsync();

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
        Assert.Same(shutdown, harness.ShutdownAsync());
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

        Task shutdown = harness.ShutdownAsync();
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
        Task shutdown = harness.ShutdownAsync();
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

        Task shutdown = harness.ShutdownAsync();
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

        Task shutdown = harness.ShutdownAsync();
        harness.AwaitTask(shutdown);

        Assert.Equal(StartResult.Withdrawn, harness.AwaitResult(harness.Controller.StartTask));
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Empty(harness.Engine.Sessions);
        Assert.False(harness.JournalExists);
        Assert.Empty(harness.RunLogPaths());
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }

    [Fact]
    public void ShutdownAsync_WithdrawnStartsWriterStalled_CompletesByDeadlineWithoutAwaitingItsRetirement()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        StalledJournalStorage storage = harness.Storage!;
        storage.BlockNext("begin");
        harness.Initialize();
        Assert.Equal(StartResult.Accepted, harness.TryStart());
        harness.RunUntil(() => storage.BlockedCount == 1);

        // The withdrawn Start waits for its writer to retire, which a stalled append prevents; the shutdown does not.
        Task shutdown = harness.ShutdownAsync();
        harness.Time.Advance(harness.Options.ShutdownReleasedTimeout);
        harness.AwaitTask(shutdown);

        ProgressJournal withdrawn = Assert.Single(harness.Journals);
        Assert.False(withdrawn.Retired.IsCompleted);
        Assert.Equal(1, storage.BlockedCount);
        Assert.Equal(RunState.Idle, harness.StateMachine.State);
        Assert.Empty(harness.Engine.Sessions);
        Assert.Equal(StartResult.Busy, harness.TryStart());
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("end")]
    public void RetirementWait_ControllerDisposedWhileWriterStalled_IsCollectedBeforeWriterReturns(string stalledRecord)
    {
        string rootPath = DirectControllerGraph.CreateRoot();
        try
        {
            var storage = new StalledJournalStorage();
            try
            {
                WeakReference controller = AbandonInStalledRetirementWait(storage, rootPath, stalledRecord);

                // The writer thread stays parked in the append, and must keep neither the disposed controller, which
                // awaits its retirement, nor anything else of the run alive.
                for (int attempt = 0; attempt < CollectionAttempts && controller.IsAlive; attempt++)
                {
                    ForceFullCollection();
                    Thread.Sleep(_collectionPause);
                }

                Assert.Equal(1, storage.BlockedCount);
                Assert.False(
                    controller.IsAlive,
                    $"A journal writer stalled in its {stalledRecord} append kept the disposed controller alive.");
            }
            finally
            {
                storage.Release();
            }

            // Released, the writer finishes the append and deletes the begin-only or ended journal, with nobody left to
            // notify of its retirement.
            Assert.True(
                SpinWait.SpinUntil(() => storage.DeleteCount == 1, ControllerHarness.DefaultWait),
                $"The writer released from its {stalledRecord} append never deleted the journal.");
            Assert.Equal(AppendOutcome.Succeeded, Assert.Single(storage.Calls, call => call.RecordType == stalledRecord).Outcome);
        }
        finally
        {
            ControllerHarness.DeleteFolder(rootPath);
        }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("end")]
    public void RetirementWait_CollectionsWhileWriterStalled_LiveControllerOffersStartOnceWriterRetires(string stalledRecord)
    {
        string rootPath = DirectControllerGraph.CreateRoot();
        var storage = new StalledJournalStorage();
        try
        {
            using var graph = new DirectControllerGraph(storage, rootPath);
            graph.EnterStalledRetirementWait(stalledRecord);

            // The stalled writer holds the retirement signal only weakly, so only the live graph, held here as the
            // controller's owner holds it, keeps the awaited journal, and with it the signal, reachable.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ForceFullCollection();
                graph.Context.RunPending();
            }

            Assert.False(graph.Controller.CanStart);
            Assert.Equal(1, storage.BlockedCount);

            storage.Release();
            graph.RunUntil(() => graph.Controller.CanStart);
            Assert.Equal(1, storage.DeleteCount);
        }
        finally
        {
            storage.Release();
            ControllerHarness.DeleteFolder(rootPath);
        }
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
            Task shutdown = harness.ShutdownAsync();
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

    // A Start refused because the engine cannot describe the machine queues its app-errors.log entry on the thread
    // pool, where it may still be running when the test ends; a write that lands after the folder's deletion
    // recreates it. The exception's text is formatted only once released, which holds the write past the test's end.
    [Fact]
    public void HasBackgroundWork_AppErrorWriteHeldPastTestEnd_HarnessDeletesFolderOnlyOnceWriteEnds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        var harness = new ControllerHarness();
        try
        {
            harness.Initialize();
            harness.Engine.QuerySystemException = new HeldTextException(entered, release);
            Assert.Equal(StartResult.Accepted, harness.TryStart());
            Assert.Equal(StartResult.EngineUnavailable, harness.AwaitResult(harness.Controller.StartTask));
            Assert.True(
                entered.Wait(ControllerHarness.DefaultWait, cancellation),
                "The app-error write never formatted the exception.");
            Assert.True(harness.Controller.HasBackgroundWork);

            _ = Task.Run(
                async () =>
                {
                    await Task.Delay(_heldWriteRelease, cancellation);
                    release.Set();
                },
                cancellation);
            harness.Dispose();

            Assert.False(harness.Controller.HasBackgroundWork);
            Assert.False(Directory.Exists(harness.RootPath));
        }
        finally
        {
            // A failure before the disposal still lets the write end, so the folder is deleted and nothing hangs.
            release.Set();
            harness.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Sensor staging
    // ---------------------------------------------------------------------------------------------

    // Advances the clock one poll interval without pumping, as a stalled UI thread would, and waits until the
    // monitor holds the read that tick starts, which it posts next. Returns that read's capture time.
    private static DateTimeOffset ReadWithoutPumping(ControllerHarness harness)
    {
        int completedBefore = harness.Sensors.CompletedReadCount;
        harness.Time.Advance(SensorMonitor.PollInterval);
        DateTimeOffset capturedAt = harness.Time.GetUtcNow();
        Assert.True(
            SpinWait.SpinUntil(
                () => harness.Sensors.CompletedReadCount > completedBefore && harness.Monitor.Current.Cpu.CapturedAt == capturedAt,
                ControllerHarness.DefaultWait),
            $"The read at {capturedAt:O} did not complete within {ControllerHarness.DefaultWait.TotalSeconds:0.#} s.");
        return capturedAt;
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
                records.Add(JournalStepEnd.FromOutcome(outcome) with { RunId = runId, Utc = running + elapsed });
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
            JournalStepEnd.FromOutcome(outcome) with { RunId = runId, Utc = completed },
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
                verdict) with { RunId = runId, Utc = verdict },
            new JournalReleased
            {
                RunId = runId,
                ReleasedBytes = 20 * GiB,
                ReleaseMs = 900,
                Utc = completed + TimeSpan.FromSeconds(1),
            },
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Retirement-wait graphs
    // ---------------------------------------------------------------------------------------------

    // Not inlined and not async, so no frame or state machine of the caller keeps the graph reachable: the controller is
    // left disposed in its wait for a journal writer stalled in the begin or end append, tracked only by the returned
    // weak reference.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonInStalledRetirementWait(StalledJournalStorage storage, string rootPath, string stalledRecord)
    {
        var graph = new DirectControllerGraph(storage, rootPath);
        graph.EnterStalledRetirementWait(stalledRecord);
        graph.Dispose();
        return new WeakReference(graph.Controller);
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// A controller over the fakes, built without <see cref="ControllerHarness"/>, which references the controller, every
    /// journal and the engine's sessions for the whole test. Here only the graph references them, so a test that lets the
    /// graph go can tell whether anything else keeps the controller alive. Every journal writes straight to one
    /// <see cref="StalledJournalStorage"/>, which its writer closes on exit.
    /// </summary>
    private sealed class DirectControllerGraph : IDisposable
    {
        private readonly StalledJournalStorage _storage;
        private readonly FakeTimeProvider _time = new(ControllerHarness.StartTime);
        private readonly RunStateMachine _stateMachine = new();
        private readonly FakeEngine _engine;
        private readonly FakeSensorSource _sensors;
        private readonly SensorMonitor _monitor;
        private readonly RunControllerOptions _options;

        public DirectControllerGraph(StalledJournalStorage storage, string rootPath)
        {
            _storage = storage;
            _engine = new FakeEngine(contextOverride: Context);
            _sensors = new FakeSensorSource(_time);
            _monitor = new SensorMonitor(_sensors, _time);
            _options = new RunControllerOptions
            {
                JournalFactory = (_, timeProvider) => new ProgressJournal(storage, timeProvider),
            };
            Controller = new RunController(
                _engine,
                _stateMachine,
                _monitor,
                new FakeMemoryInventorySource(),
                new FakeElevationService(),
                new FakeUserPrompt(),
                _time,
                DataDirectory.FromRoot(Path.Combine(rootPath, "data")),
                _options);
        }

        /// <summary>Gets the queued context that stands in for the UI thread.</summary>
        public ImmediateSynchronizationContext Context { get; } = new(queuePosts: true);

        /// <summary>Gets the controller.</summary>
        public RunController Controller { get; }

        /// <summary>Creates a unique temporary folder for one test's data folder.</summary>
        /// <returns>The folder's path.</returns>
        public static string CreateRoot()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "RamQuick.App.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return rootPath;
        }

        /// <summary>
        /// Drives the controller until it awaits the retirement of a journal whose writer is stalled in its
        /// <paramref name="stalledRecord"/> append past the journal's 1 s Dispose wait: a Start refused because its
        /// <c>begin</c> record timed out, or a published run whose <c>end</c> record timed out.
        /// </summary>
        /// <param name="stalledRecord"><c>"begin"</c> or <c>"end"</c>.</param>
        public void EnterStalledRetirementWait(string stalledRecord)
        {
            if (stalledRecord == "begin")
            {
                _storage.BlockNext("begin");
            }

            Task initialized = OnUiThread(Controller.InitializeAsync);
            RunUntil(() => initialized.IsCompleted);
            Assert.True(initialized.IsCompletedSuccessfully);
            Assert.Equal(StartResult.Accepted, OnUiThread(() => Controller.TryStart(TestType.Quick, stopOnError: true)));
            if (stalledRecord == "begin")
            {
                // The controller and the journal give up on the begin record together, both after 5 s on this clock.
                RunUntil(() => _storage.BlockedCount == 1);
                _time.Advance(_options.BeginTimeout);
                RunUntil(() => Controller.NoticeMessage == UiText.DataFolderUnusableMessage);
            }
            else
            {
                RunUntil(() => _engine.LastSession is { IsStarted: true } && _stateMachine.State == RunState.Preparing);
                FakeEngineSession session = _engine.LastSession!;
                session.RaisePrepared();
                RunUntil(() => _stateMachine.State == RunState.Running);
                session.RaiseCompleted(RunEndReason.Completed);
                RunUntil(() => _stateMachine.IsTerminal && Controller.ConclusionTask.IsCompleted);

                // Publication waits 5 s for the end record, then tears the run down.
                _storage.BlockNext("end");
                session.RaiseReleased(session.Options.RegionBytes, TimeSpan.FromMilliseconds(400));
                RunUntil(() => _storage.BlockedCount == 1);
                _time.Advance(ProgressJournal.AcknowledgmentTimeout);
                RunUntil(() => session.IsDisposed);
            }

            // Longer than the journal's Dispose wait, after which the controller awaits the stalled writer's retirement.
            Assert.False(Context.RunUntil(() => Controller.CanStart, _journalDisposeWindow));
            Assert.Equal(1, _storage.BlockedCount);
        }

        /// <summary>Pumps <see cref="Context"/> on the calling thread until <paramref name="condition"/> holds.</summary>
        /// <param name="condition">The condition, evaluated on the calling thread after each drain.</param>
        /// <param name="conditionText">The condition's source text, for the timeout message.</param>
        /// <exception cref="TimeoutException">The condition did not hold within <see cref="ControllerHarness.DefaultWait"/>.</exception>
        public void RunUntil(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? conditionText = null)
        {
            if (!Context.RunUntil(condition, ControllerHarness.DefaultWait))
            {
                throw new TimeoutException(
                    $"'{conditionText}' did not hold within {ControllerHarness.DefaultWait.TotalSeconds:0} s: state "
                    + $"{_stateMachine.State}, can start {Controller.CanStart}, {Context.PendingCount} queued post(s).");
            }
        }

        /// <summary>Disposes the controller, runs what its disposal queued, and disposes the sensor monitor and source.</summary>
        public void Dispose()
        {
            Controller.Dispose();
            Context.RunPending();
            _monitor.Dispose();
            _sensors.Dispose();
        }

        private T OnUiThread<T>(Func<T> call)
        {
            using (Context.Install())
            {
                return call();
            }
        }
    }

    /// <summary>
    /// An exception whose text is formatted only once the test releases it, after signalling that formatting began:
    /// app-errors.log formats the exception on the thread-pool thread that writes the entry, so the write is held
    /// there, as a stalled disk would hold it.
    /// </summary>
    private sealed class HeldTextException : Exception
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public HeldTextException(ManualResetEventSlim entered, ManualResetEventSlim release)
            : base("the engine cannot describe the machine")
        {
            _entered = entered;
            _release = release;
        }

        // Bounded, so a write the test never releases still ends and its folder can still be deleted.
        public override string ToString()
        {
            _entered.Set();
            _ = _release.Wait(ControllerHarness.DefaultWait);
            return base.ToString();
        }
    }
}

/// <summary>
/// The collection of the tests that count the process's kernel handles. The count is process-wide, so it also moves
/// with every handle a test running beside them opens or closes; they therefore run alone, after the parallel tests.
/// </summary>
[CollectionDefinition(nameof(HandleCountCollection), DisableParallelization = true)]
public sealed class HandleCountCollection
{
}

/// <summary>
/// Kernel-handle counts around many step gates of one run: neither the gate's barrier nor its journal acknowledgment
/// wait may leave a kernel object behind for the garbage collector to release.
/// </summary>
/// <remarks>
/// The count runs inside a no-GC region, because a collection and the finalizers it queues would close a leaked
/// handle and hide it. Nothing here induces a collection.
/// </remarks>
[Collection(nameof(HandleCountCollection))]
public sealed class StepGateHandleTests
{
    private const int GateCount = 200;

    // Room for every allocation the process makes during the count, the gates' own included, so that no collection
    // ends the region early.
    private const long NoGcRegionBytes = 64L << 20;

    [Fact]
    public void StepGate_ManyGates_LeaveNoKernelHandleBehind()
    {
        using var harness = new ControllerHarness();
        harness.UseStalledJournal();
        harness.Initialize();
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();

        int stepCount = session.Options.Steps.Count;
        var decisions = new StepGateDecision[GateCount];
        int released = 0;
        int finished = 0;

        // One coordinator thread runs every gate, as the engine's does, so no thread is created during the count; it
        // starts the gates only once the count has begun.
        var coordinator = new Thread(() =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref released) != 0, ControllerHarness.DefaultWait))
            {
                return;
            }

            for (int i = 0; i < GateCount; i++)
            {
                decisions[i] = session.InvokeStepGate(i % stepCount);
            }

            Volatile.Write(ref finished, 1);
        })
        {
            IsBackground = true,
            Name = "Test coordinator",
        };
        coordinator.Start();

        Assert.True(GC.TryStartNoGCRegion(NoGcRegionBytes), "The no-GC region could not be entered.");
        int before;
        int after;
        bool regionHeld;
        try
        {
            // Entering the region may have run a collection whose finalizers would close handles during the count and
            // offset a leak; they finish first.
            GC.WaitForPendingFinalizers();
            before = CurrentHandleCount();
            Volatile.Write(ref released, 1);

            // Pumped, because each gate first waits for its barrier to run on the UI context.
            harness.RunUntil(() => Volatile.Read(ref finished) != 0);
            after = CurrentHandleCount();
            regionHeld = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                GC.EndNoGCRegion();
            }
        }

        Assert.True(coordinator.Join(ControllerHarness.DefaultWait), "The test coordinator never finished.");
        Assert.True(regionHeld, "A garbage collection ran during the count, so a leaked handle could have been closed.");
        Assert.All(decisions, decision => Assert.Equal(StepGateDecision.Continue, decision));
        Assert.Equal(GateCount, session.GateInvocations.Count);
        int growth = after - before;
        Assert.True(growth < GateCount / 10, $"{GateCount} step gates left {growth} more kernel handles open.");
    }

    private static int CurrentHandleCount()
    {
        using Process self = Process.GetCurrentProcess();
        return self.HandleCount;
    }
}
