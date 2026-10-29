using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using RamQuick.App.Services;
using RamQuick.App.Tests.Fakes;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Sensors;
using Xunit;

[assembly: AssemblyFixture(typeof(RamQuick.IntegrationTests.EngineBindingFixture))]

// Locked-memory, timing and process tests must never overlap: their latency and duration bounds are meaningless when
// another run competes for the memory controller and the logical processors. xunit.v3 4.x replaces the obsolete
// CollectionBehavior(DisableTestParallelization = true) with this assembly-level mode.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace RamQuick.IntegrationTests;

/// <summary>
/// The assembly fixture that binds this test process, once and before any test runs, to the test-only engine library
/// <see cref="TestEngineFileName"/>. A resolved P/Invoke is never rebound, so the binding must precede every engine
/// import; this fixture therefore touches no engine import itself, and the first one resolves through the bound
/// resolver.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper's resolver loads the library beside <c>RamQuick.Interop.dll</c>, which is this project's output root.
/// <c>TestEngineNative</c> declares the <c>rq_test_*</c> exports with its own
/// <c>[LibraryImport("ramquick_engine_test.dll")]</c>, which default probing loads from the same directory, so the
/// loader maps a single module: sessions created through the wrapper and the test exports share one
/// <c>live_session_count</c>.
/// </para>
/// <para>
/// <c>RamQuick.TestHost</c> runs in its own process from the <c>TestHost\</c> subfolder and binds the production
/// <c>ramquick_engine.dll</c>; this process never loads it.
/// </para>
/// </remarks>
public sealed class EngineBindingFixture
{
    /// <summary>The file name of the test-only engine library this process binds to.</summary>
    public const string TestEngineFileName = "ramquick_engine_test.dll";

    // 0 = not bound by a fixture, 1 = bound or binding. xUnit constructs an assembly fixture once; the guard keeps an
    // accidental second construction from calling EngineLibrary.Bind again, which would throw.
    private static int _bindState;

    /// <summary>
    /// Verifies that the test engine library was built into the output root, then binds the engine imports to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The library is missing from the output root, or an engine import resolved before the binding.
    /// </exception>
    public EngineBindingFixture()
    {
        string expectedPath = Path.Combine(AppContext.BaseDirectory, TestEngineFileName);
        if (!File.Exists(expectedPath))
        {
            // Failing here names the cause; a missing library would otherwise surface later as a DllNotFoundException
            // inside an unrelated test.
            throw new InvalidOperationException(
                $"The test engine library was not found at '{expectedPath}'. Build RamQuick.sln with Visual Studio "
                + "MSBuild for the x64 platform, which builds ramquick_engine_test.vcxproj and copies "
                + $"{TestEngineFileName} into the RamQuick.IntegrationTests output folder.");
        }

        if (Interlocked.CompareExchange(ref _bindState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            EngineLibrary.Bind(TestEngineFileName);
        }
        catch
        {
            // Reopened so a later construction reports the same binding failure instead of silently skipping it.
            Volatile.Write(ref _bindState, 0);
            throw;
        }
    }

    /// <summary>Gets the library file the engine imports of this process load.</summary>
    public string BoundFileName => EngineLibrary.BoundFileName;
}

/// <summary>
/// A dedicated STA thread that pumps a real WPF <see cref="System.Windows.Threading.Dispatcher"/> with a
/// <see cref="DispatcherSynchronizationContext"/> installed, standing in for the application's UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>RunController</c> has UI-thread affinity, and an engine session posts every event to the context captured when
/// it is created. Building the controller or the session inside <see cref="InvokeAsync(Action)"/> therefore makes the
/// tests observe events exactly as the application does: posted at <see cref="DispatcherPriority.Normal"/> to one UI
/// thread.
/// </para>
/// <para>
/// An exception that escapes a posted work item, such as an event handler, would end <see cref="Dispatcher.Run"/> and
/// with it the whole test process. It is recorded and marked handled instead, and every later
/// <c>InvokeAsync</c> or <see cref="FlushAsync(DispatcherPriority)"/> throws it, so the test that caused it fails.
/// </para>
/// </remarks>
internal sealed class DispatcherThread : IDisposable
{
    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Exception> _unhandledExceptions = new();
    private int _disposed;

    /// <summary>
    /// Starts the dispatcher thread and waits at most 10 s until its dispatcher and context are installed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dispatcher could not be created, or did not start in time.</exception>
    public DispatcherThread()
    {
        // Not disposed on a timeout: the thread may still signal it afterwards.
        var ready = new ManualResetEventSlim(initialState: false);
        Dispatcher? dispatcher = null;
        SynchronizationContext? context = null;
        Exception? startFailure = null;

        _thread = new Thread(() =>
        {
            try
            {
                Dispatcher current = Dispatcher.CurrentDispatcher;
                current.UnhandledException += OnUnhandledException;
                var installed = new DispatcherSynchronizationContext(current);
                SynchronizationContext.SetSynchronizationContext(installed);
                dispatcher = current;
                context = installed;
            }
            catch (Exception exception)
            {
                startFailure = exception;
                return;
            }
            finally
            {
                ready.Set();
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "RamQuick test dispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(_startTimeout))
        {
            throw new InvalidOperationException(
                $"The test dispatcher thread did not start within {_startTimeout.TotalSeconds:0} s.");
        }

        ready.Dispose();
        if (startFailure is not null || dispatcher is null || context is null)
        {
            throw new InvalidOperationException("The test dispatcher thread could not create its dispatcher.", startFailure);
        }

        Dispatcher = dispatcher;
        Context = context;
        ManagedThreadId = _thread.ManagedThreadId;
    }

    /// <summary>Gets the dispatcher pumped by the thread.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>Gets the managed thread id of the dispatcher thread.</summary>
    public int ManagedThreadId { get; }

    /// <summary>Gets the <see cref="DispatcherSynchronizationContext"/> installed on the dispatcher thread.</summary>
    public SynchronizationContext Context { get; }

    /// <summary>Gets a value indicating whether the calling thread is the dispatcher thread.</summary>
    public bool IsCurrent => Dispatcher.CheckAccess();

    /// <summary>Gets the exceptions that escaped posted work items, oldest first.</summary>
    public IReadOnlyList<Exception> UnhandledExceptions => _unhandledExceptions.ToArray();

    /// <summary>Runs <paramref name="action"/> on the dispatcher thread.</summary>
    /// <param name="action">The work to run.</param>
    /// <returns>A task that completes when the work has run, faulted with its exception if it threw.</returns>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        return CompleteAsync(Dispatcher.InvokeAsync(action).Task);
    }

    /// <summary>Runs <paramref name="func"/> on the dispatcher thread and returns its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The work to run.</param>
    /// <returns>A task that completes with the result, faulted with the exception if the work threw.</returns>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ThrowIfDisposed();
        return CompleteAsync(Dispatcher.InvokeAsync(func).Task);
    }

    /// <summary>
    /// Starts the asynchronous body <paramref name="func"/> on the dispatcher thread, so every await inside it resumes
    /// there, and waits for the body itself rather than for its first await.
    /// </summary>
    /// <param name="func">The asynchronous work to run.</param>
    /// <returns>A task that completes when the body's task completes, with its outcome.</returns>
    public async Task InvokeAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ThrowIfDisposed();
        Task body = await CompleteAsync(Dispatcher.InvokeAsync(func).Task).ConfigureAwait(false);
        await (body ?? throw new InvalidOperationException("The asynchronous body returned no task.")).ConfigureAwait(false);
        ThrowIfFaulted();
    }

    /// <summary>
    /// Starts the asynchronous body <paramref name="func"/> on the dispatcher thread, so every await inside it resumes
    /// there, and returns the body's result.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The asynchronous work to run.</param>
    /// <returns>A task that completes with the body's result, or with its fault or cancellation.</returns>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ThrowIfDisposed();
        Task<T> body = await CompleteAsync(Dispatcher.InvokeAsync(func).Task).ConfigureAwait(false);
        T result = await (body ?? throw new InvalidOperationException("The asynchronous body returned no task.")).ConfigureAwait(false);
        ThrowIfFaulted();
        return result;
    }

    /// <summary>
    /// Waits until every work item queued above <paramref name="priority"/> has run. Engine events and continuations
    /// are posted at <see cref="DispatcherPriority.Normal"/>, so the default drains them, including those they post in
    /// turn.
    /// </summary>
    /// <param name="priority">The priority of the marker item; work queued above it runs first.</param>
    /// <returns>A task that completes when the marker item has run.</returns>
    public Task FlushAsync(DispatcherPriority priority = DispatcherPriority.Background)
    {
        ThrowIfDisposed();
        return CompleteAsync(Dispatcher.InvokeAsync(static () => { }, priority).Task);
    }

    /// <summary>
    /// Shuts the dispatcher down and waits at most 5 s for the thread to end. Idempotent.
    /// </summary>
    /// <remarks>
    /// <see cref="Dispatcher.InvokeShutdown"/> would wait without bound for a work item that never returns; a
    /// <see cref="DispatcherPriority.Send"/> shutdown request with a bounded join keeps disposal within 5 s. A thread
    /// still busy after that is a background thread, so it cannot keep the test process alive.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(_shutdownTimeout);
        }
    }

    private async Task CompleteAsync(Task operation)
    {
        ThrowIfFaulted();
        await operation.ConfigureAwait(false);
        ThrowIfFaulted();
    }

    private async Task<T> CompleteAsync<T>(Task<T> operation)
    {
        ThrowIfFaulted();
        T result = await operation.ConfigureAwait(false);
        ThrowIfFaulted();
        return result;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfFaulted()
    {
        if (_unhandledExceptions.TryPeek(out Exception? first))
        {
            throw new InvalidOperationException(
                $"{_unhandledExceptions.Count} exception(s) escaped work items posted to the test dispatcher; the first is the inner exception.",
                first);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _unhandledExceptions.Enqueue(e.Exception);
        e.Handled = true;
    }
}

/// <summary>
/// A private data folder for one test, <c>%TEMP%\RamQuick.IntegrationTests\&lt;guid&gt;</c>, with the
/// <c>journal</c> and <c>logs</c> subfolders the controller expects, deleted on disposal as far as possible.
/// </summary>
internal sealed class TempDataRoot : IDisposable
{
    private const string ParentFolderName = "RamQuick.IntegrationTests";
    private const string SummaryJsonPattern = "*-summary.json";
    private const string SummaryTextPattern = "*-summary.txt";
    private const int DeleteAttempts = 5;

    private static readonly TimeSpan _deleteRetryDelay = TimeSpan.FromMilliseconds(100);

    private int _disposed;

    /// <summary>
    /// Creates a new, empty data folder under the temporary directory.
    /// </summary>
    /// <exception cref="IOException">The folder or its subfolders could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The temporary directory is not writable.</exception>
    public TempDataRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ParentFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        try
        {
            Data = DataDirectory.FromRoot(Path);
        }
        catch
        {
            DeleteBestEffort();
            throw;
        }
    }

    /// <summary>Gets the full path of the data folder.</summary>
    public string Path { get; }

    /// <summary>Gets the data folder, rooted at <see cref="Path"/>.</summary>
    public DataDirectory Data { get; }

    /// <summary>Gets the full path of the <c>logs</c> folder that holds each run's log and summaries.</summary>
    public string LogsPath => Data.LogsDirectory;

    /// <summary>Gets the full path of the progress journal, <c>journal\active-run.jsonl</c>.</summary>
    public string JournalPath => Data.JournalPath;

    /// <summary>
    /// Returns the published JSON summaries under <see cref="LogsPath"/>, ordered by path; in-flight
    /// <c>*.tmp</c> files are excluded.
    /// </summary>
    /// <returns>The full paths of the <c>*-summary.json</c> files.</returns>
    public IReadOnlyList<string> SummaryJsonFiles() => FinalFiles(SummaryJsonPattern);

    /// <summary>
    /// Returns the published text summaries under <see cref="LogsPath"/>, ordered by path; in-flight
    /// <c>*.tmp</c> files are excluded.
    /// </summary>
    /// <returns>The full paths of the <c>*-summary.txt</c> files.</returns>
    public IReadOnlyList<string> SummaryTextFiles() => FinalFiles(SummaryTextPattern);

    /// <summary>
    /// Deletes the data folder, retrying a few times 100 ms apart. Best effort and idempotent: a file still held
    /// open, for example by an abandoned journal writer thread, leaves the folder in place.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DeleteBestEffort();
    }

    private IReadOnlyList<string> FinalFiles(string pattern)
    {
        if (!Directory.Exists(LogsPath))
        {
            return Array.Empty<string>();
        }

        // The suffix check is the authority; the search pattern only narrows the enumeration.
        string suffix = pattern[1..];
        return Directory.EnumerateFiles(LogsPath, pattern, SearchOption.TopDirectoryOnly)
            .Where(file => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void DeleteBestEffort()
    {
        for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A handle still open in this or another process; the next attempt may find it closed. After the last
                // attempt the folder stays behind in the temporary directory, which is acceptable for test data.
                if (attempt < DeleteAttempts)
                {
                    Thread.Sleep(_deleteRetryDelay);
                }
            }
        }
    }
}


/// <summary>
/// Builds the production <see cref="RunController"/> graph for a test, with only the hardware-facing seams faked, and
/// waits on it from the test thread.
/// </summary>
/// <remarks>
/// The controller and its state machine have UI-thread affinity: <see cref="Create"/> runs on a
/// <see cref="DispatcherThread"/>, and every wait evaluates its condition there.
/// </remarks>
internal static class ControllerGraph
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Creates the controller graph over <paramref name="engine"/>. Must run on a <see cref="DispatcherThread"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sensors report <see cref="SensorStatus.DriverUnavailable"/>, and the <see cref="SensorMonitor"/> is never
    /// started, so no sensor thread or timer references the run: the graph the abandonment tests need, with both
    /// thermal limits inactive.
    /// </para>
    /// <para>
    /// The elevation fake reports an elevated process: locking up to 1 GiB works without elevation once the working
    /// set is raised, so the tests run unelevated. The prompt answers No and never blocks. The memory inventory is the
    /// fake's DDR5 default.
    /// </para>
    /// <para>
    /// The controller is not initialised; call <see cref="RunController.InitializeAsync"/> on the dispatcher, or use
    /// <see cref="CreateInitializedAsync"/>, before <see cref="RunController.TryStart"/>.
    /// </para>
    /// </remarks>
    /// <param name="engine">The engine the controller drives.</param>
    /// <param name="data">The data folder, or <see langword="null"/> for "no writable data folder".</param>
    /// <param name="options">The controller tunables; the production defaults when <see langword="null"/>.</param>
    /// <param name="timeProvider">The controller's clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="InvalidOperationException">The caller is not on a dispatcher thread.</exception>
    public static Graph Create(
        IEngine engine,
        DataDirectory? data,
        RunControllerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
        {
            throw new InvalidOperationException(
                "ControllerGraph.Create must run on a DispatcherThread (inside DispatcherThread.InvokeAsync), because "
                + "RunController has UI-thread affinity and its engine sessions post their events to the context "
                + "captured there.");
        }

        TimeProvider time = timeProvider ?? TimeProvider.System;
        var sensorMonitor = new SensorMonitor(FakeSensorSource.AllUnavailable(SensorStatus.DriverUnavailable, time), time);
        var stateMachine = new RunStateMachine();
        var elevation = new FakeElevationService { IsElevated = true };
        var prompt = new FakeUserPrompt();

        RunController controller;
        try
        {
            controller = new RunController(
                engine,
                stateMachine,
                sensorMonitor,
                new FakeMemoryInventorySource(),
                elevation,
                prompt,
                time,
                data,
                options);
        }
        catch
        {
            sensorMonitor.Dispose();
            throw;
        }

        return new Graph(controller, stateMachine, sensorMonitor, prompt, elevation);
    }

    /// <summary>
    /// Creates the controller graph on <paramref name="ui"/> and runs <see cref="RunController.InitializeAsync"/> there,
    /// so <see cref="RunController.TryStart"/> can be accepted.
    /// </summary>
    /// <param name="ui">The dispatcher thread that owns the graph.</param>
    /// <param name="engine">The engine the controller drives.</param>
    /// <param name="data">The data folder, or <see langword="null"/> for "no writable data folder".</param>
    /// <param name="options">The controller tunables; the production defaults when <see langword="null"/>.</param>
    /// <param name="timeProvider">The controller's clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <returns>The initialised graph.</returns>
    public static Task<Graph> CreateInitializedAsync(
        DispatcherThread ui,
        IEngine engine,
        DataDirectory? data,
        RunControllerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return ui.InvokeAsync(async () =>
        {
            Graph graph = Create(engine, data, options, timeProvider);
            try
            {
                await graph.Controller.InitializeAsync();
            }
            catch
            {
                graph.Controller.Dispose();
                graph.SensorMonitor.Dispose();
                throw;
            }

            return graph;
        });
    }

    /// <summary>
    /// Waits until <paramref name="condition"/>, evaluated on the dispatcher thread at most 25 ms apart, holds.
    /// </summary>
    /// <param name="ui">The dispatcher thread the condition is evaluated on.</param>
    /// <param name="condition">The condition; it may read UI-affine objects.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="description">What is awaited, completing "waiting for …" in the timeout message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition did not hold within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
    public static Task WaitUntilAsync(DispatcherThread ui, Func<bool> condition, TimeSpan timeout, string description) =>
        WaitCoreAsync(ui, condition, timeout, description, diagnostics: null);

    /// <summary>
    /// Waits until <paramref name="condition"/>, evaluated on the dispatcher thread at most 25 ms apart, holds; a
    /// timeout reports <paramref name="controller"/>'s run state, post-run activity and messages.
    /// </summary>
    /// <param name="ui">The dispatcher thread that owns <paramref name="controller"/>.</param>
    /// <param name="controller">The controller described in the timeout message.</param>
    /// <param name="condition">The condition; it may read UI-affine objects.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="description">What is awaited, completing "waiting for …" in the timeout message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition did not hold within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
    public static Task WaitUntilAsync(
        DispatcherThread ui,
        RunController controller,
        Func<bool> condition,
        TimeSpan timeout,
        string description)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return WaitCoreAsync(ui, condition, timeout, description, () => Describe(controller));
    }

    /// <summary>
    /// Waits until the run has a verdict, its memory is released and its summaries are published: the state is
    /// terminal, <see cref="RunController.CanStart"/> is <see langword="true"/> and
    /// <see cref="RunController.PostRunActivity"/> is <see cref="PostRunActivity.None"/>.
    /// </summary>
    /// <param name="ui">The dispatcher thread that owns <paramref name="controller"/>.</param>
    /// <param name="controller">The controller running the test.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <returns>A task that completes once the run is concluded and published.</returns>
    /// <exception cref="TimeoutException">The run did not conclude and publish within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
    public static Task WaitForTerminalAndPublishedAsync(DispatcherThread ui, RunController controller, TimeSpan timeout) =>
        WaitUntilAsync(
            ui,
            controller,
            () => controller.StateMachine.State.IsTerminal()
                && controller.CanStart
                && controller.PostRunActivity == PostRunActivity.None,
            timeout,
            "a terminal state with the memory released and the summaries published");

    /// <summary>
    /// Shuts the controller down as the window's closing does, then disposes it and the sensor monitor, all on the
    /// dispatcher thread.
    /// </summary>
    /// <param name="ui">The dispatcher thread that owns the graph.</param>
    /// <param name="graph">The graph to shut down.</param>
    /// <returns>A task that completes when the graph is disposed.</returns>
    public static Task ShutdownAndDisposeAsync(DispatcherThread ui, Graph graph)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(graph);
        return ui.InvokeAsync(async () =>
        {
            try
            {
                await graph.Controller.ShutdownAsync();
            }
            finally
            {
                graph.Controller.Dispose();
                graph.SensorMonitor.Dispose();
            }
        });
    }

    private static async Task WaitCoreAsync(
        DispatcherThread ui,
        Func<bool> condition,
        TimeSpan timeout,
        string description,
        Func<string>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A timeout must be non-negative or infinite.");
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await ui.InvokeAsync(condition).ConfigureAwait(false))
            {
                return;
            }

            if (timeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= timeout)
            {
                string state = diagnostics is null
                    ? string.Empty
                    : "; " + await ui.InvokeAsync(diagnostics).ConfigureAwait(false);
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Timed out after {timeout.TotalSeconds:0.###} s waiting for {description}{state}."));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Describe(RunController controller)
    {
        RunStateMachine stateMachine = controller.StateMachine;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"state {stateMachine.State}, end reason {stateMachine.EndReason?.ToString() ?? "none"}, "
            + $"end detail '{stateMachine.EndDetail}', post-run activity {controller.PostRunActivity}, "
            + $"can start {controller.CanStart}, result message '{controller.ResultMessage}', "
            + $"notice '{controller.NoticeMessage}'");
    }

    /// <summary>The objects of one controller graph that tests drive or inspect.</summary>
    /// <param name="Controller">The production run controller.</param>
    /// <param name="StateMachine">The graph's single run state machine.</param>
    /// <param name="SensorMonitor">The sensor monitor, never started.</param>
    /// <param name="Prompt">The prompt fake, which records the elevation offers.</param>
    /// <param name="Elevation">The elevation fake, elevated by default.</param>
    public sealed record Graph(
        RunController Controller,
        RunStateMachine StateMachine,
        SensorMonitor SensorMonitor,
        FakeUserPrompt Prompt,
        FakeElevationService Elevation);
}


/// <summary>
/// Launches <c>RamQuick.TestHost.exe</c>, the headless run host bound to the production engine, from the isolated
/// <c>TestHost\</c> subfolder of this project's output, and captures its output.
/// </summary>
/// <remarks>
/// <para>
/// The host's contract: <c>--data-dir &lt;path&gt; --region-mib &lt;n&gt; --steps &lt;k&gt; --step-seconds &lt;s&gt;
/// [--stop-on-error]</c>; UTF-8 stdout lines prefixed <c>RQHOST</c> (the <c>*Prefix</c> constants); and the exit codes
/// <see cref="ExitPassed"/> to <see cref="ExitUnhandled"/>.
/// </para>
/// <para>
/// The host is started without the shell, so it never elevates, and it writes only beneath the data folder it is
/// given and to its standard streams.
/// </para>
/// </remarks>
internal static class TestHostLauncher
{
    /// <summary>The prefix of the line that reports the run id: <c>RQHOST STARTED runId=&lt;32 hex&gt;</c>.</summary>
    public const string StartedPrefix = "RQHOST STARTED runId=";

    /// <summary>The prefix of a state-transition line: <c>RQHOST STATE &lt;RunState&gt;</c>.</summary>
    public const string StatePrefix = "RQHOST STATE ";

    /// <summary>The prefix of the verdict line: <c>RQHOST VERDICT &lt;PASS|FAIL|ABORTED&gt; reason=&lt;RunEndReason&gt;</c>.</summary>
    public const string VerdictPrefix = "RQHOST VERDICT ";

    /// <summary>The prefix of the publication line: <c>RQHOST PUBLISHED json=&lt;path&gt; txt=&lt;path&gt;</c>.</summary>
    public const string PublishedPrefix = "RQHOST PUBLISHED json=";

    /// <summary>The prefix of a failure line: <c>RQHOST ERROR &lt;message&gt;</c>.</summary>
    public const string ErrorPrefix = "RQHOST ERROR ";

    /// <summary>Exit code: the run ended PASS and both summaries were published.</summary>
    public const int ExitPassed = 0;

    /// <summary>Exit code: the run ended FAIL and both summaries were published.</summary>
    public const int ExitFailed = 1;

    /// <summary>Exit code: the run ended ABORTED and both summaries were published.</summary>
    public const int ExitAborted = 2;

    /// <summary>Exit code: nothing was run (usage error, refused Start, unusable data folder, region too small).</summary>
    public const int ExitRefused = 3;

    /// <summary>Exit code: the summaries could not be confirmed on disk, or the run did not finish in time.</summary>
    public const int ExitNotPublished = 4;

    /// <summary>Exit code: an unhandled exception ended the host.</summary>
    public const int ExitUnhandled = 5;

    private const string HostFolderName = "TestHost";
    private const string HostExecutableName = "RamQuick.TestHost.exe";

    // The host refuses a region below one 2 MiB slice per logical processor; four slices per processor keep every
    // worker's slice well above that floor on many-core machines.
    private const long SliceMiB = 2;
    private const long SlicesPerProcessor = 4;

    /// <summary>Gets the folder that holds the host's build output, <c>&lt;output&gt;\TestHost</c>.</summary>
    public static string HostDirectory => Path.Combine(AppContext.BaseDirectory, HostFolderName);

    /// <summary>Gets the full path of <c>RamQuick.TestHost.exe</c>.</summary>
    public static string HostExecutable => Path.Combine(HostDirectory, HostExecutableName);

    /// <summary>
    /// Returns the host's command line for one run of the first <paramref name="steps"/> Quick steps.
    /// </summary>
    /// <param name="dataDir">The data folder; passed as a full path, since the host runs in its own folder.</param>
    /// <param name="regionMiB">The cap on the region under test, in MiB; see <see cref="RegionMiBFor"/>.</param>
    /// <param name="steps">How many leading Quick steps to run.</param>
    /// <param name="stepSeconds">The time budget of every step, in seconds.</param>
    /// <param name="stopOnError">Whether the run stops at the first confirmed error.</param>
    /// <returns>The arguments, one element per argument, formatted with the invariant culture.</returns>
    public static IReadOnlyList<string> Arguments(
        string dataDir,
        int regionMiB,
        int steps,
        int stepSeconds,
        bool stopOnError = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionMiB);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);

        var arguments = new List<string>
        {
            "--data-dir",
            Path.GetFullPath(dataDir),
            "--region-mib",
            regionMiB.ToString(CultureInfo.InvariantCulture),
            "--steps",
            steps.ToString(CultureInfo.InvariantCulture),
            "--step-seconds",
            stepSeconds.ToString(CultureInfo.InvariantCulture),
        };

        if (stopOnError)
        {
            arguments.Add("--stop-on-error");
        }

        return arguments;
    }

    /// <summary>
    /// Returns <paramref name="requestedMiB"/>, enlarged where needed to at least four 2 MiB slices per logical
    /// processor, so the host accepts the region on a many-core machine.
    /// </summary>
    /// <param name="requestedMiB">The region size the test asks for, in MiB.</param>
    /// <returns>The region size to pass to <see cref="Arguments"/>, in whole MiB.</returns>
    public static int RegionMiBFor(int requestedMiB)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedMiB);
        long minimumMiB = Environment.ProcessorCount * SliceMiB * SlicesPerProcessor;
        return checked((int)Math.Max(requestedMiB, minimumMiB));
    }

    /// <summary>
    /// Starts the host with <paramref name="arguments"/> and begins capturing its stdout and stderr lines.
    /// </summary>
    /// <param name="arguments">The command line, normally from <see cref="Arguments"/>.</param>
    /// <returns>The running host; dispose it to kill it if it is still running.</returns>
    /// <exception cref="InvalidOperationException">The host executable is missing or could not be started.</exception>
    public static RunningHost Start(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string executable = HostExecutable;
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"RamQuick.TestHost.exe was not found at '{executable}'. The CopyTestHostOutput target of "
                + "RamQuick.IntegrationTests.csproj copies the host's build output there; build RamQuick.sln with "
                + "Visual Studio MSBuild for the x64 platform.");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = HostDirectory,
        };

        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("No argument may be null.", nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        return RunningHost.Launch(startInfo);
    }


    /// <summary>
    /// A started <c>RamQuick.TestHost</c> process whose stdout and stderr lines are captured asynchronously, so its
    /// pipes never fill and block it.
    /// </summary>
    /// <remarks>Every member except <see cref="DisposeAsync"/> may be called from any thread.</remarks>
    public sealed class RunningHost : IAsyncDisposable
    {
        private const int DescribedLineLimit = 200;

        private static readonly TimeSpan _killTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _outputDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new();
        private readonly List<string> _stdOut = [];
        private readonly List<string> _stdErr = [];
        private readonly TaskCompletionSource _stdOutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stdErrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        private RunningHost(Process process)
        {
            Process = process;
        }

        /// <summary>Gets the host process.</summary>
        public Process Process { get; }

        /// <summary>Gets the id of the host process, recorded when it started.</summary>
        public int ProcessId { get; private set; }

        /// <summary>Gets a snapshot of the stdout lines received so far, oldest first.</summary>
        public IReadOnlyList<string> StdOut
        {
            get
            {
                lock (_gate)
                {
                    return _stdOut.ToArray();
                }
            }
        }

        /// <summary>Gets a snapshot of the stderr lines received so far, oldest first.</summary>
        public IReadOnlyList<string> StdErr
        {
            get
            {
                lock (_gate)
                {
                    return _stdErr.ToArray();
                }
            }
        }

        /// <summary>
        /// Waits for the host to exit and for its output to be read to the end.
        /// </summary>
        /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
        /// <param name="cancellationToken">Cancels the wait; the host is then killed.</param>
        /// <returns>The host's exit code.</returns>
        /// <exception cref="TimeoutException">
        /// The host did not exit within <paramref name="timeout"/>; it has been killed, and the message carries its
        /// captured output.
        /// </exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
        public async Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A timeout must be non-negative or infinite.");
            }

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(timeout);
                try
                {
                    await Process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Kill();
                    throw new TimeoutException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"RamQuick.TestHost (pid {ProcessId}) did not exit within {timeout.TotalSeconds:0.###} s and was killed.{Environment.NewLine}{DescribeOutput()}"));
                }
                catch (OperationCanceledException)
                {
                    Kill();
                    throw;
                }
            }

            try
            {
                await Task.WhenAll(_stdOutClosed.Task, _stdErrClosed.Task)
                    .WaitAsync(_outputDrainTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A process that inherited the host's pipes could hold them open after the host exited; the lines
                // captured so far are kept and the exit code still stands.
            }

            return Process.ExitCode;
        }

        /// <summary>
        /// Kills the host and its process tree, then waits at most 10 s for it to exit. Does nothing to a host that has
        /// already exited.
        /// </summary>
        /// <exception cref="TimeoutException">The host had not exited 10 s after the kill.</exception>
        public void Kill()
        {
            TryKillTree();
            if (!Process.WaitForExit(_killTimeout))
            {
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"RamQuick.TestHost (pid {ProcessId}) had not exited {_killTimeout.TotalSeconds:0} s after it was killed."));
            }
        }

        /// <summary>Returns the first stdout line that starts with <paramref name="prefix"/>.</summary>
        /// <param name="prefix">The line prefix, compared ordinally, such as <see cref="VerdictPrefix"/>.</param>
        /// <returns>The whole line, or <see langword="null"/> when no line received so far starts with the prefix.</returns>
        public string? FindLine(string prefix)
        {
            ArgumentException.ThrowIfNullOrEmpty(prefix);
            lock (_gate)
            {
                return _stdOut.Find(line => line.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Describes the captured output for an assertion message: the last 200 lines of stdout and of stderr.
        /// </summary>
        /// <returns>The description, one line per captured line.</returns>
        public string DescribeOutput()
        {
            string[] stdOut;
            string[] stdErr;
            lock (_gate)
            {
                stdOut = _stdOut.ToArray();
                stdErr = _stdErr.ToArray();
            }

            var text = new StringBuilder();
            AppendTail(text, "stdout", stdOut);
            AppendTail(text, "stderr", stdErr);
            return text.ToString();
        }

        /// <summary>
        /// Kills the host and its process tree if it is still running, waits at most 10 s for it to exit, and releases
        /// the process object. Idempotent; never throws for a host that will not die.
        /// </summary>
        /// <returns>A task that completes when the host has exited or the wait has ended.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (!Process.HasExited)
                {
                    TryKillTree();
                    using var killWait = new CancellationTokenSource(_killTimeout);
                    try
                    {
                        await Process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // A host that survives a tree kill for 10 s is left to Windows; throwing here would mask the
                        // failure of the test that is being torn down.
                    }
                }
            }
            finally
            {
                Process.Dispose();
            }
        }

        /// <summary>
        /// Starts <paramref name="startInfo"/>, which must redirect stdout and stderr, and begins reading both.
        /// </summary>
        /// <param name="startInfo">The host's start information.</param>
        /// <returns>The running host.</returns>
        /// <exception cref="InvalidOperationException">The process could not be started.</exception>
        internal static RunningHost Launch(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var host = new RunningHost(process);
            process.OutputDataReceived += host.OnStdOutReceived;
            process.ErrorDataReceived += host.OnStdErrReceived;

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"'{startInfo.FileName}' could not be started.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            try
            {
                host.ProcessId = process.Id;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                // Never leave a started host running unobserved.
                host.TryKillTree();
                process.Dispose();
                throw;
            }

            return host;
        }

        private static void AppendTail(StringBuilder text, string streamName, string[] lines)
        {
            int omitted = Math.Max(0, lines.Length - DescribedLineLimit);
            text.Append(CultureInfo.InvariantCulture, $"{streamName}: {lines.Length} line(s)");
            if (omitted > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $", the first {omitted} omitted");
            }

            text.AppendLine(":");
            foreach (string line in lines.Skip(omitted))
            {
                text.Append("  ").AppendLine(line);
            }
        }

        private void TryKillTree()
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The host has already exited, so there is nothing to kill.
            }
            catch (Win32Exception) when (Process.HasExited)
            {
                // The host was exiting while the kill was issued and has gone.
            }
        }

        private void OnStdOutReceived(object sender, DataReceivedEventArgs e) => Capture(e.Data, _stdOut, _stdOutClosed);

        private void OnStdErrReceived(object sender, DataReceivedEventArgs e) => Capture(e.Data, _stdErr, _stdErrClosed);

        private void Capture(string? line, List<string> lines, TaskCompletionSource closed)
        {
            // A null line marks the end of the stream.
            if (line is null)
            {
                closed.TrySetResult();
                return;
            }

            lock (_gate)
            {
                lines.Add(line);
            }
        }
    }
}

