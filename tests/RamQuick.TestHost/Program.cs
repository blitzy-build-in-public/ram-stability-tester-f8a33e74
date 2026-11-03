using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using RamQuick.App;
using RamQuick.App.Services;
using RamQuick.App.Tests.Fakes;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Plans;
using RamQuick.Sensors;

namespace RamQuick.TestHost;

/// <summary>
/// Entry point of the headless run host that the process-level integration tests launch. It runs the
/// production <see cref="RunController"/> over the production <c>ramquick_engine.dll</c> on a WPF dispatcher
/// thread, with no window, and reports the run as <c>RQHOST</c> lines on stdout.
/// </summary>
internal static class Program
{
    // An explicit Main rather than top-level statements, so that STAThread applies to the thread the dispatcher
    // pumps on.
    [STAThread]
    private static int Main(string[] args)
    {
        using var output = new HostOutput();
        if (!HostOptions.TryParse(args, out HostOptions? options, out string? error))
        {
            output.Error("usage: " + error);
            output.Plain(HostOptions.Usage);
            return HostExitCodes.Refused;
        }

        return new HostRun(options, output).Execute();
    }
}

/// <summary>
/// The host's process exit codes, a contract with <c>tests/RamQuick.IntegrationTests</c>.
/// </summary>
internal static class HostExitCodes
{
    /// <summary>The run ended PASS and the controller published both of the run's own summaries.</summary>
    public const int Passed = 0;

    /// <summary>The run ended FAIL and the controller published both of the run's own summaries.</summary>
    /// <remarks>
    /// Reached only when the production engine reports memory errors for this host's run, at the first error or in a
    /// run that completes with errors, which healthy hardware never produces. The host binds only the production
    /// engine, so it has no fault injection by design, and no process-level test drives this code.
    /// </remarks>
    public const int Failed = 1;

    /// <summary>The run ended ABORTED and the controller published both of the run's own summaries.</summary>
    public const int Aborted = 2;

    /// <summary>
    /// Nothing was run: a usage error, an invalid plan, an unusable data folder, an unavailable engine, a region too
    /// small for the machine, or a Start the controller refused.
    /// </summary>
    public const int Refused = 3;

    /// <summary>
    /// The run's own summaries could not be confirmed on disk: the controller reported that it could not save them, a
    /// summary under the run's name is missing or is not the run's own publication (another run id, a summary rebuilt
    /// from a journal, or another verdict or end reason), the run id was not found, or the run did not finish within
    /// the overall timeout.
    /// </summary>
    public const int NotPublished = 4;

    /// <summary>An unhandled exception ended the host.</summary>
    /// <remarks>
    /// Reached only through the fatal path, an exception escaping the run, the dispatcher or another thread, which is a
    /// host or controller defect that no valid command line produces, so no process-level test drives this code.
    /// </remarks>
    public const int Unhandled = 5;
}

/// <summary>
/// The host's validated command line.
/// </summary>
/// <param name="DataDirectory">The full path of the data folder that holds the run's journal and logs.</param>
/// <param name="RegionMiB">The cap on the region under test, in MiB.</param>
/// <param name="Steps">How many leading steps of the Quick plan to run.</param>
/// <param name="StepSeconds">The time budget of every step, in seconds.</param>
/// <param name="StopOnError">Whether the run stops at the first confirmed error.</param>
internal sealed record HostOptions(string DataDirectory, int RegionMiB, int Steps, int StepSeconds, bool StopOnError)
{
    /// <summary>The one usage line printed after a usage error.</summary>
    public const string Usage =
        "Usage: RamQuick.TestHost.exe --data-dir <path> --region-mib <n> --steps <k> --step-seconds <s> [--stop-on-error]";

    /// <summary>The largest accepted <c>--region-mib</c>: 1 TiB.</summary>
    public const int MaxRegionMiB = 1_048_576;

    /// <summary>The largest accepted <c>--step-seconds</c>: one hour.</summary>
    public const int MaxStepSeconds = 3_600;

    private const string DataDirOption = "--data-dir";
    private const string RegionMiBOption = "--region-mib";
    private const string StepsOption = "--steps";
    private const string StepSecondsOption = "--step-seconds";
    private const string StopOnErrorOption = "--stop-on-error";
    private const string OptionPrefix = "--";

    /// <summary>
    /// Parses <c>--data-dir &lt;path&gt; --region-mib &lt;n&gt; --steps &lt;k&gt; --step-seconds &lt;s&gt;
    /// [--stop-on-error]</c>. Options may appear in any order, each at most once; names are case-sensitive.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="options">The parsed options when the method returns <see langword="true"/>.</param>
    /// <param name="error">Why the command line was rejected when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every required option is present, once, with a value in range.</returns>
    public static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out HostOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool stopOnError = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == StopOnErrorOption)
            {
                if (stopOnError)
                {
                    error = $"option '{StopOnErrorOption}' is given more than once";
                    return false;
                }

                stopOnError = true;
                continue;
            }

            if (argument is not (DataDirOption or RegionMiBOption or StepsOption or StepSecondsOption))
            {
                error = $"unknown option '{argument}'";
                return false;
            }

            if (values.ContainsKey(argument))
            {
                error = $"option '{argument}' is given more than once";
                return false;
            }

            // A following option name is a missing value, never a value: no caller passes a path that starts with "--".
            if (index + 1 >= args.Length || args[index + 1].StartsWith(OptionPrefix, StringComparison.Ordinal))
            {
                error = $"option '{argument}' requires a value";
                return false;
            }

            index++;
            values.Add(argument, args[index]);
        }

        foreach (string required in new[] { DataDirOption, RegionMiBOption, StepsOption, StepSecondsOption })
        {
            if (!values.ContainsKey(required))
            {
                error = $"missing required option '{required}'";
                return false;
            }
        }

        if (!TryParseDataDirectory(values[DataDirOption], out string? dataDirectory, out error)
            || !TryParseNumber(RegionMiBOption, values[RegionMiBOption], MaxRegionMiB, out int regionMiB, out error)
            || !TryParseNumber(StepsOption, values[StepsOption], TestPlans.Quick.Steps.Count, out int steps, out error)
            || !TryParseNumber(StepSecondsOption, values[StepSecondsOption], MaxStepSeconds, out int stepSeconds, out error))
        {
            return false;
        }

        options = new HostOptions(dataDirectory, regionMiB, steps, stepSeconds, stopOnError);
        error = null;
        return true;
    }

    private static bool TryParseDataDirectory(
        string value,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out string? error)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"option '{DataDirOption}' requires a non-empty path";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"option '{DataDirOption}' value '{value}' is not a valid path: {exception.Message}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseNumber(
        string option,
        string value,
        int maximum,
        out int number,
        [NotNullWhen(false)] out string? error)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1 || number > maximum)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"option '{option}' must be an integer from 1 to {maximum}, not '{value}'");
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// The only writer to stdout. Lines are queued and written by one background thread, so the dispatcher thread never
/// blocks on a full pipe; every line is flushed as soon as it is written.
/// </summary>
/// <remarks>
/// Line formats (prefix <c>RQHOST</c>, single spaces, invariant culture) are a contract with
/// <c>tests/RamQuick.IntegrationTests</c>: <c>STARTED runId=&lt;32 hex&gt;</c>, <c>STATE &lt;RunState&gt;</c>,
/// <c>VERDICT &lt;PASS|FAIL|ABORTED&gt; reason=&lt;RunEndReason&gt;</c>, <c>PUBLISHED json=&lt;path&gt;
/// txt=&lt;path&gt;</c> and <c>ERROR &lt;message&gt;</c>. Every member is thread-safe.
/// </remarks>
internal sealed class HostOutput : IDisposable
{
    private const string Prefix = "RQHOST ";

    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _writer;
    private bool _completed;

    /// <summary>
    /// Switches stdout to UTF-8 without a byte-order mark, which callers read it as, and starts the writer thread.
    /// </summary>
    public HostOutput()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            Console.OutputEncoding = utf8;
        }
        catch (IOException)
        {
            // A process started without any console cannot set the console code page; stdout itself is still a
            // writable handle, so the same encoding is applied to a writer over it.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8));
        }

        _writer = new Thread(WriteLines)
        {
            IsBackground = true,
            Name = "RamQuick.TestHost stdout",
        };
        _writer.Start();
    }

    /// <summary>Reports the id of the run this host started.</summary>
    /// <param name="runId">The run id as 32 lower-case hexadecimal digits.</param>
    public void Started(string runId) => Event("STARTED runId=" + runId);

    /// <summary>Reports a run state transition.</summary>
    /// <param name="state">The state entered.</param>
    public void State(RunState state) => Event("STATE " + state.ToString());

    /// <summary>Reports the verdict of the run this host started.</summary>
    /// <param name="verdict">PASS, FAIL or ABORTED.</param>
    /// <param name="reason">The end reason's name, or <c>None</c>.</param>
    public void Verdict(string verdict, string reason) => Event("VERDICT " + verdict + " reason=" + reason);

    /// <summary>Reports that both of the run's own summaries are on disk and the JSON one parses.</summary>
    /// <param name="jsonPath">The full path of the JSON summary.</param>
    /// <param name="textPath">The full path of the text summary.</param>
    public void Published(string jsonPath, string textPath) => Event("PUBLISHED json=" + jsonPath + " txt=" + textPath);

    /// <summary>Reports a failure.</summary>
    /// <param name="message">The failure; line breaks are replaced with spaces.</param>
    public void Error(string message) => Event("ERROR " + message);

    /// <summary>Writes a line without the <c>RQHOST</c> prefix, which line parsers ignore.</summary>
    /// <param name="line">The line; line breaks are replaced with spaces.</param>
    public void Plain(string line) => Enqueue(line);

    /// <summary>
    /// Stops accepting lines and waits at most 2 s for the queued ones to be written. Lines offered afterwards are
    /// dropped. Idempotent.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _lines.CompleteAdding();
        }

        // A writer still blocked on a pipe nobody reads keeps the collection, so it is disposed only once drained.
        if (_writer.Join(_drainTimeout))
        {
            _lines.Dispose();
        }
    }

    /// <summary>Completes the output; see <see cref="Complete"/>.</summary>
    public void Dispose() => Complete();

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    private void Event(string text) => Enqueue(Prefix + text);

    private void Enqueue(string line)
    {
        string single = SingleLine(line);
        lock (_gate)
        {
            if (!_completed)
            {
                _lines.Add(single);
            }
        }
    }

    private void WriteLines()
    {
        bool readerGone = false;
        foreach (string line in _lines.GetConsumingEnumerable())
        {
            // Once the reading end of the pipe has closed, the remaining lines have nowhere to go; they are drained so
            // Complete never waits on them.
            if (readerGone)
            {
                continue;
            }

            try
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
            catch (IOException)
            {
                readerGone = true;
            }
        }
    }
}


/// <summary>
/// Composes the production controller graph over the production engine, drives one scaled Quick run through
/// <see cref="RunController"/> exactly as the application would, confirms its summaries on disk and maps the outcome
/// to an exit code.
/// </summary>
/// <remarks>
/// <para>
/// The controller, the state machine and every event handler here have UI-thread affinity: they run on the WPF
/// dispatcher that <see cref="Execute"/> pumps on the main thread. Every await stays on that dispatcher's context,
/// because the controller creates the engine session there and the session posts its events to the context it
/// captures. File-system probes run on the thread pool.
/// </para>
/// <para>
/// The host never creates a session, a journal or a results log itself, and never wraps the engine: the run is the
/// production controller's. It writes only beneath the data folder, through the controller, and to stdout.
/// </para>
/// </remarks>
internal sealed partial class HostRun
{
    private const string TestEngineFileName = "ramquick_engine_test.dll";
    private const string RunLogSearchPattern = "RamQuick-*.log";
    private const ulong BytesPerMiB = 1UL << 20;

    private static readonly TimeSpan _timeoutMargin = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _shutdownBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _runIdSearchLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _runIdRetryDelay = TimeSpan.FromMilliseconds(100);

    // The controller's save-failure notice up to its variable part, taken from UiText so the wording has one owner.
    private static readonly string _resultsNotSavedPrefix = UiText.ResultsNotSaved(string.Empty);

    private readonly HostOptions _options;
    private readonly HostOutput _output;
    private readonly TaskCompletionSource<WaitOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Dispatcher? _dispatcher;
    private FakeSensorSource? _sensorSource;
    private SensorMonitor? _sensorMonitor;
    private RunStateMachine? _stateMachine;

    // Read by the fatal path from any thread, so it is published with Volatile.
    private RunController? _controller;

    private IReadOnlySet<string> _existingRunLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string _logsDirectory = string.Empty;
    private Task<RunIdentity?>? _runIdentity;
    private bool _startAccepted;
    private bool _sawPreparing;

    // The first save failure the controller reported after this run entered Preparing.
    private string? _saveFailure;

    private bool _verdictReported;
    private bool _graphDisposed;
    private int _fatalReported;
    private int _exitCode = HostExitCodes.Unhandled;

    /// <summary>
    /// Initializes a host run for validated options.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">The stdout channel.</param>
    public HostRun(HostOptions options, HostOutput output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        _options = options;
        _output = output;
    }

    private enum WaitOutcome
    {
        // The run this host started reached a terminal state, released its memory and published its summaries.
        Finished,

        // The controller accepted Start, then refused it before Preparing, with the state unchanged.
        Refused,
    }

    /// <summary>
    /// Runs the host to completion on the calling thread, which becomes the dispatcher thread.
    /// </summary>
    /// <returns>The process exit code, one of <see cref="HostExitCodes"/>.</returns>
    public int Execute()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        try
        {
            if (TryPrepare() is { } prepared)
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcher = dispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.UnhandledException += OnDispatcherUnhandledException;

                _ = dispatcher.InvokeAsync(() => RunAsync(prepared));
                Dispatcher.Run();

                dispatcher.UnhandledException -= OnDispatcherUnhandledException;
            }
        }
        catch (Exception exception)
        {
            ReportFatal(exception);
        }
        finally
        {
            // A fatal dispatcher exception shuts the dispatcher down before the run's own cleanup can run.
            DisposeGraph();
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        }

        return Volatile.Read(ref _exitCode);
    }

    // Everything checked before the dispatcher starts: the plan, the engine binding and the data folder. Returns null,
    // with the exit code set to Refused, when the host cannot run.
    private PreparedHost? TryPrepare()
    {
        StepDefinition[] steps = TestPlans.Quick.Steps
            .Take(_options.Steps)
            .Select(step => step with { TimeBudget = TimeSpan.FromSeconds(_options.StepSeconds) })
            .ToArray();
        var plan = new TestPlan(TestType.Quick, steps);

        IReadOnlyList<PlanViolation> violations = TestPlanValidator.Validate(plan);
        if (violations.Count > 0)
        {
            foreach (PlanViolation violation in violations)
            {
                _output.Error(violation.StepIndex is { } index
                    ? string.Create(CultureInfo.InvariantCulture, $"plan: step {index} '{violation.StepName}': {violation.Message}")
                    : "plan: " + violation.Message);
            }

            return Refuse();
        }

        string baseDirectory = AppContext.BaseDirectory;
        string enginePath = Path.Combine(baseDirectory, EngineLibrary.DefaultFileName);
        if (!File.Exists(enginePath))
        {
            _output.Error($"engine unavailable: the production engine {enginePath} is missing");
            return Refuse();
        }

        string testEnginePath = Path.Combine(baseDirectory, TestEngineFileName);
        if (File.Exists(testEnginePath))
        {
            _output.Error($"engine unavailable: {testEnginePath} must not sit beside the host, which binds the production engine only");
            return Refuse();
        }

        DataDirectory dataDirectory;
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            dataDirectory = DataDirectory.FromRoot(_options.DataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _output.Error($"data folder unusable: {_options.DataDirectory}: {exception.Message}");
            return Refuse();
        }

        IEngine engine;
        try
        {
            // The first engine call of the process. EngineLibrary.Bind is never called, so the production DLL binds.
            engine = new NativeEngine();
        }
        catch (Exception exception) when (exception is EngineException or DllNotFoundException)
        {
            _output.Error("engine unavailable: " + exception.Message);
            return Refuse();
        }

        return new PreparedHost(plan, dataDirectory, engine);
    }

    private PreparedHost? Refuse()
    {
        SetExitCode(HostExitCodes.Refused);
        return null;
    }

    private async Task RunAsync(PreparedHost prepared)
    {
        try
        {
            RunController controller = Compose(prepared);
            SetExitCode(await RunControllerAsync(controller, prepared));
        }
        catch (Exception exception)
        {
            ReportFatal(exception);
        }
        finally
        {
            await ShutdownControllerAsync();
            DisposeGraph();
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    // The application's controller graph with only the hardware-facing seams faked: sensors unavailable and never
    // polled (both thermal limits inactive), a DDR5 inventory, an elevated process, and a prompt that never blocks.
    // Every other controller option keeps its production default, the durable file journal included.
    private RunController Compose(PreparedHost prepared)
    {
        FakeSensorSource sensorSource = FakeSensorSource.AllUnavailable(SensorStatus.DriverUnavailable, TimeProvider.System);
        _sensorSource = sensorSource;
        var sensorMonitor = new SensorMonitor(sensorSource, TimeProvider.System);
        _sensorMonitor = sensorMonitor;
        var stateMachine = new RunStateMachine();
        _stateMachine = stateMachine;
        _logsDirectory = prepared.DataDirectory.LogsDirectory;

        TestPlan plan = prepared.Plan;
        var controllerOptions = new RunControllerOptions
        {
            PlanProvider = _ => plan,
            MaxRegionBytes = (ulong)_options.RegionMiB * BytesPerMiB,
        };

        var controller = new RunController(
            prepared.Engine,
            stateMachine,
            sensorMonitor,
            new FakeMemoryInventorySource(),
            new FakeElevationService { IsElevated = true },
            new FakeUserPrompt(),
            TimeProvider.System,
            prepared.DataDirectory,
            controllerOptions);
        Volatile.Write(ref _controller, controller);

        stateMachine.StateChanged += OnStateChanged;
        controller.PropertyChanged += OnControllerPropertyChanged;
        return controller;
    }

    private async Task<int> RunControllerAsync(RunController controller, PreparedHost prepared)
    {
        // As the application at launch: system probe, inventory, and recovery of a journal left in the data folder.
        await controller.InitializeAsync();

        if (LogicalProcessorsOf(controller, prepared.Engine) is not { } logicalProcessors)
        {
            return HostExitCodes.Refused;
        }

        // Below one 2 MiB slice per worker the engine would hand every worker an empty slice.
        ulong regionBytes = (ulong)_options.RegionMiB * BytesPerMiB;
        ulong requiredBytes = (ulong)logicalProcessors * MemorySizing.SliceGranularityBytes;
        if (regionBytes < requiredBytes)
        {
            _output.Error(string.Create(
                CultureInfo.InvariantCulture,
                $"region too small: {_options.RegionMiB} MiB for {logicalProcessors} logical processors (need ≥ {requiredBytes / BytesPerMiB} MiB)"));
            return HostExitCodes.Refused;
        }

        string logsDirectory = _logsDirectory;
        try
        {
            _existingRunLogs = await Task.Run(() => ListRunLogs(logsDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _output.Error($"data folder unusable: {logsDirectory} could not be listed: {exception.Message}");
            return HostExitCodes.Refused;
        }

        StartResult start = controller.TryStart(TestType.Quick, _options.StopOnError);
        if (start != StartResult.Accepted)
        {
            _output.Error($"start refused: {start}" + (controller.NoticeMessage is { } notice ? " " + notice : string.Empty));
            return HostExitCodes.Refused;
        }

        _startAccepted = true;
        EvaluateCompletion();

        TimeSpan timeout = prepared.Plan.TargetDuration + _timeoutMargin;
        if (!await CompletesWithinAsync(_completion.Task, timeout))
        {
            // The bounded ShutdownAsync in RunAsync's finally then concludes and publishes the stopped run.
            _output.Error(string.Create(
                CultureInfo.InvariantCulture,
                $"timeout after {(long)timeout.TotalSeconds} s in state {controller.StateMachine.State}"));
            controller.RequestStop();
            return HostExitCodes.NotPublished;
        }

        if (await _completion.Task == WaitOutcome.Refused)
        {
            _output.Error("start refused: " + (controller.NoticeMessage ?? "the controller withdrew the start without a notice"));
            return HostExitCodes.Refused;
        }

        // A run whose id could not be found has already reported it; its summaries cannot be located.
        RunIdentity? identity = _runIdentity is null ? null : await _runIdentity;
        if (identity is null)
        {
            return HostExitCodes.NotPublished;
        }

        DataDirectory dataDirectory = prepared.DataDirectory;
        RunStateMachine stateMachine = controller.StateMachine;
        string? expectedVerdict = stateMachine.State.ToVerdictText();
        string? expectedEndReason = stateMachine.EndReason?.ToString();
        PublicationCheck publication = await Task.Run(
            () => CheckPublication(dataDirectory, identity, expectedVerdict, expectedEndReason));

        // A save failure the controller reported means the run's own publication failed, even where files under the
        // run's name pass the check.
        if ((publication.Failure ?? _saveFailure) is { } failure)
        {
            _output.Error("results not published: " + WithControllerNotices(failure, controller.NoticeMessage));
            return HostExitCodes.NotPublished;
        }

        _output.Published(publication.JsonPath, publication.TextPath);
        return controller.StateMachine.State switch
        {
            RunState.Passed => HostExitCodes.Passed,
            RunState.Failed => HostExitCodes.Failed,
            RunState.Aborted => HostExitCodes.Aborted,
            _ => HostExitCodes.NotPublished,
        };
    }

    // The controller's save failure and current notice are appended rather than used instead of the finding, because
    // the notice can also be an unrelated recovery outcome of this Start. A notice the text already states is not
    // repeated.
    private string WithControllerNotices(string finding, string? currentNotice)
    {
        var notices = new List<string>(2);
        foreach (string? notice in new[] { _saveFailure, currentNotice })
        {
            if (notice is not null
                && !finding.Contains(notice, StringComparison.Ordinal)
                && !notices.Contains(notice, StringComparer.Ordinal))
            {
                notices.Add(notice);
            }
        }

        return notices.Count == 0 ? finding : finding + " (" + string.Join("; ", notices) + ")";
    }

    private static HashSet<string> ListRunLogs(string logsDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(logsDirectory))
        {
            return names;
        }

        // The search pattern is only a prefilter: a three-letter extension pattern also matches longer extensions.
        foreach (string path in Directory.EnumerateFiles(logsDirectory, RunLogSearchPattern))
        {
            string name = Path.GetFileName(path);
            if (RunLogName().IsMatch(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // Runs on the thread pool. The data folder belongs to this host alone, so exactly one run log appears per Start.
    private static RunLogSearch SearchNewRunLogs(string logsDirectory, IReadOnlySet<string> existing)
    {
        List<string> fresh;
        try
        {
            fresh = ListRunLogs(logsDirectory).Where(name => !existing.Contains(name)).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RunLogSearch(null, Conclusive: false, $"{logsDirectory} could not be listed: {exception.Message}");
        }

        if (fresh.Count == 0)
        {
            return new RunLogSearch(null, Conclusive: false, $"no new run log in {logsDirectory}");
        }

        if (fresh.Count > 1)
        {
            return new RunLogSearch(
                null,
                Conclusive: true,
                string.Create(CultureInfo.InvariantCulture, $"{fresh.Count} new run logs in {logsDirectory}: {string.Join(", ", fresh)}"));
        }

        string fileName = fresh[0];
        string runId = RunLogName().Match(fileName).Groups["id"].Value.ToLowerInvariant();
        var identity = new RunIdentity(runId, Path.GetFileNameWithoutExtension(fileName));
        return new RunLogSearch(identity, Conclusive: true, fileName);
    }

    // Runs on the thread pool: both summaries must exist, and the JSON one must parse, name this run and be the run's
    // own publication. A launch recovery, in this or another process, can publish a summary rebuilt from this run's
    // journal under the same name and run id; it is never the run's own, whatever its verdict.
    private static PublicationCheck CheckPublication(
        DataDirectory dataDirectory,
        RunIdentity identity,
        string? expectedVerdict,
        string? expectedEndReason)
    {
        string jsonPath = ResultsLog.GetSummaryJsonPath(dataDirectory, identity.BaseName);
        string textPath = ResultsLog.GetSummaryTextPath(dataDirectory, identity.BaseName);
        if (!File.Exists(jsonPath) || !File.Exists(textPath))
        {
            return new PublicationCheck(jsonPath, textPath, $"summary files missing for {identity.BaseName}");
        }

        string json;
        try
        {
            json = File.ReadAllText(jsonPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PublicationCheck(jsonPath, textPath, $"{jsonPath} could not be read: {exception.Message}");
        }

        if (!RunSummaryJson.TryDeserialize(json, out RunSummary? summary))
        {
            return new PublicationCheck(jsonPath, textPath, $"{jsonPath} is not a schema version {RunSummary.CurrentSchemaVersion} summary");
        }

        if (!string.Equals(summary.Run.Id, identity.RunId, StringComparison.OrdinalIgnoreCase))
        {
            return new PublicationCheck(jsonPath, textPath, $"{jsonPath} names run {summary.Run.Id}, not {identity.RunId}");
        }

        SummaryRun run = summary.Run;
        if (run.Recovered || run.Partial)
        {
            string kind = (run.Partial ? "a partial summary" : "a summary")
                + (run.Recovered ? " recovered from a journal" : string.Empty);
            return new PublicationCheck(
                jsonPath,
                textPath,
                $"{jsonPath} is {kind} (verdict {run.Verdict}, reason {run.EndReason}), not this run's own");
        }

        if (!string.Equals(run.Verdict, expectedVerdict, StringComparison.Ordinal)
            || !string.Equals(run.EndReason, expectedEndReason, StringComparison.Ordinal))
        {
            return new PublicationCheck(
                jsonPath,
                textPath,
                $"{jsonPath} records verdict {run.Verdict}, reason {run.EndReason}, "
                    + $"not this run's verdict {expectedVerdict ?? "none"}, reason {expectedEndReason ?? "none"}");
        }

        return new PublicationCheck(jsonPath, textPath, null);
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (!task.IsCompleted)
        {
            using var cancellation = new CancellationTokenSource();
            Task delay = Task.Delay(timeout, TimeProvider.System, cancellation.Token);
            Task first = await Task.WhenAny(task, delay);
            cancellation.Cancel();
            if (first != task)
            {
                return false;
            }
        }

        await task;
        return true;
    }

    [GeneratedRegex(@"^RamQuick-[0-9]{8}-[0-9]{6}-Quick-(?<id>[0-9a-fA-F]{32})\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex RunLogName();

    private int? LogicalProcessorsOf(RunController controller, IEngine engine)
    {
        if (controller.System is { } system)
        {
            return system.LogicalProcessors;
        }

        try
        {
            return engine.QuerySystem().LogicalProcessors;
        }
        catch (Exception exception) when (exception is EngineException or InvalidOperationException)
        {
            _output.Error("engine unavailable: " + exception.Message);
            return null;
        }
    }

    // Reports STARTED once the run log the controller reserved before Preparing is visible, retrying for up to 2 s.
    private async Task<RunIdentity?> FindRunIdentityAsync()
    {
        string logsDirectory = _logsDirectory;
        IReadOnlySet<string> existing = _existingRunLogs;
        long startedAt = TimeProvider.System.GetTimestamp();
        while (true)
        {
            RunLogSearch search = await Task.Run(() => SearchNewRunLogs(logsDirectory, existing));
            if (search.Identity is { } identity)
            {
                _output.Started(identity.RunId);
                return identity;
            }

            if (search.Conclusive || TimeProvider.System.GetElapsedTime(startedAt) >= _runIdSearchLimit)
            {
                _output.Error("run id not found: " + search.Detail);
                return null;
            }

            await Task.Delay(_runIdRetryDelay, TimeProvider.System);
        }
    }

    private void OnStateChanged(object? sender, RunStateChangedEventArgs e)
    {
        _output.State(e.Current);

        if (_startAccepted && !_sawPreparing && e.Current == RunState.Preparing)
        {
            _sawPreparing = true;
            _runIdentity = FindRunIdentityAsync();
        }

        // Only the run this host started has a verdict line; a Failed state from launch recovery does not.
        if (_sawPreparing && !_verdictReported && e.Current.IsTerminal())
        {
            _verdictReported = true;
            _output.Verdict(e.Current.ToVerdictText() ?? e.Current.ToString(), e.EndReason?.ToString() ?? "None");
        }

        EvaluateCompletion();
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunController.NoticeMessage) or null or "")
        {
            RecordSaveFailure();
        }

        if (e.PropertyName is nameof(RunController.CanStart)
            or nameof(RunController.PostRunActivity)
            or nameof(RunController.NoticeMessage)
            or null
            or "")
        {
            EvaluateCompletion();
        }
    }

    // The accepted Start cleared the notice, and from Preparing on only this run's publication reports a save failure,
    // so the first one seen is this run's own; a later notice never replaces it.
    private void RecordSaveFailure()
    {
        if (_sawPreparing
            && _saveFailure is null
            && _controller?.NoticeMessage is { } notice
            && notice.StartsWith(_resultsNotSavedPrefix, StringComparison.Ordinal))
        {
            _saveFailure = notice;
        }
    }

    // CanStart turns false inside an accepted TryStart and true again only once the run is over: after its summaries
    // are published and its session disposed, or after a refusal that left the state unchanged.
    private void EvaluateCompletion()
    {
        if (!_startAccepted || _completion.Task.IsCompleted || _controller is not { } controller)
        {
            return;
        }

        RunStateMachine stateMachine = controller.StateMachine;
        if (_sawPreparing)
        {
            if (stateMachine.IsTerminal && controller.CanStart && controller.PostRunActivity == PostRunActivity.None)
            {
                _completion.TrySetResult(WaitOutcome.Finished);
            }
        }
        else if (controller.CanStart && !stateMachine.IsRunInProgress)
        {
            _completion.TrySetResult(WaitOutcome.Refused);
        }
    }

    private async Task ShutdownControllerAsync()
    {
        if (_controller is not { } controller)
        {
            return;
        }

        try
        {
            if (!await CompletesWithinAsync(controller.ShutdownAsync(), _shutdownBound))
            {
                _output.Error(string.Create(
                    CultureInfo.InvariantCulture,
                    $"shutdown did not finish within {(long)_shutdownBound.TotalSeconds} s in state {controller.StateMachine.State}"));
            }
        }
        catch (Exception exception)
        {
            ReportFatal(exception);
        }
    }

    // Unsubscribes the host's handlers, then disposes in reverse order of creation. Idempotent; called on the
    // dispatcher thread, or on the same thread after the dispatcher has stopped.
    private void DisposeGraph()
    {
        if (_graphDisposed)
        {
            return;
        }

        _graphDisposed = true;
        if (_stateMachine is { } stateMachine)
        {
            stateMachine.StateChanged -= OnStateChanged;
        }

        RunController? controller = Volatile.Read(ref _controller);
        if (controller is not null)
        {
            controller.PropertyChanged -= OnControllerPropertyChanged;
        }

        DisposeReportingFailure(controller);
        DisposeReportingFailure(_sensorMonitor);
        DisposeReportingFailure(_sensorSource);
    }

    private void DisposeReportingFailure(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception exception)
        {
            ReportFatal(exception);
        }
    }

    private void SetExitCode(int exitCode)
    {
        // A fatal exception keeps the Unhandled exit code whatever the run itself returns afterwards.
        if (Volatile.Read(ref _fatalReported) == 0)
        {
            Volatile.Write(ref _exitCode, exitCode);
        }
    }

    // Thread-safe: also called from the AppDomain handler on whichever thread failed.
    private void ReportFatal(Exception exception)
    {
        Volatile.Write(ref _fatalReported, 1);
        Volatile.Write(ref _exitCode, HostExitCodes.Unhandled);

        // Logs to app-errors.log and disposes the run's session, which releases its locked memory.
        Volatile.Read(ref _controller)?.HandleFatalException(exception);
        _output.Error($"unhandled: {exception.GetType().FullName}: {exception.Message}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal(e.Exception);
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ReportFatal(e.ExceptionObject as Exception
            ?? new InvalidOperationException($"A non-exception object was thrown: {e.ExceptionObject}"));
        _output.Complete();
        Environment.Exit(HostExitCodes.Unhandled);
    }

    private sealed record PreparedHost(TestPlan Plan, DataDirectory DataDirectory, IEngine Engine);

    private sealed record RunIdentity(string RunId, string BaseName);

    // Conclusive: no retry can change the answer (found, or more than one new run log).
    private sealed record RunLogSearch(RunIdentity? Identity, bool Conclusive, string Detail);

    private sealed record PublicationCheck(string JsonPath, string TextPath, string? Failure);
}
