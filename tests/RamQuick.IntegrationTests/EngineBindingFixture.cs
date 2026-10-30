using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
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
/// it is created. Building the controller or the session inside <see cref="InvokeAsync(Action, TimeSpan?)"/> therefore
/// makes the tests observe events exactly as the application does: posted at <see cref="DispatcherPriority.Normal"/> to
/// one UI thread.
/// </para>
/// <para>
/// An exception that escapes a posted work item, such as an event handler, would end <see cref="Dispatcher.Run"/> and
/// with it the whole test process. It is recorded and marked handled instead, and every later
/// <c>InvokeAsync</c> or <see cref="FlushAsync(DispatcherPriority, TimeSpan?)"/> throws it, so the test that caused it
/// fails.
/// </para>
/// <para>
/// No wait on the dispatcher is unbounded: a blocked or saturated dispatcher must fail the test that waits on it, not
/// hang it, and xUnit's cancellation reaches only a wait that observes its token. Every work item gets one deadline,
/// <see cref="DefaultOperationTimeout"/> unless the caller passes its own, covering both its start on the dispatcher and,
/// for an asynchronous body, the body's completion; test cancellation ends the wait too. A work item that is still
/// queued or running when its deadline passes marks the dispatcher stalled: until that item has run, later work is
/// refused at once rather than queued behind it, so cleanup after the timeout never waits on the stalled dispatcher
/// again.
/// </para>
/// </remarks>
internal sealed class DispatcherThread : IDisposable
{
    /// <summary>
    /// The deadline of a work item whose caller passes none: 60 s from queueing to the end of its run or of its
    /// asynchronous body.
    /// </summary>
    /// <remarks>
    /// The longest legitimate body is <c>RunController.ShutdownAsync</c>, bounded by its production options at about
    /// 33 s (a withdrawn Start 10 s, Completed 3 s, Released 10 s and publication 10 s), so 60 s fails only a
    /// dispatcher or body that is stuck.
    /// </remarks>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(5);

    // Task.WaitAsync refuses bounds past about 49.7 days. Capping at int.MaxValue milliseconds (24.8 days) refuses such
    // a bound before its work item is queued, rather than after the work has been posted.
    private static readonly TimeSpan _maxOperationTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Exception> _unhandledExceptions = new();
    private WorkItem? _stalled;
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

    /// <summary>
    /// Gets a value indicating whether <see cref="Dispose"/> has requested the shutdown and the dispatcher thread has
    /// ended. It stays <see langword="false"/> while a work item that never returns keeps the thread running.
    /// </summary>
    public bool ShutdownCompleted => Volatile.Read(ref _disposed) != 0 && !_thread.IsAlive;

    /// <summary>Runs <paramref name="action"/> on the dispatcher thread.</summary>
    /// <param name="action">The work to run.</param>
    /// <param name="timeout">
    /// The deadline from queueing to the end of the run; <see cref="DefaultOperationTimeout"/> when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A task that completes when the work has run, faulted with its exception if it threw, with a
    /// <see cref="DispatcherTimeoutException"/> if the deadline passed first, or cancelled with the test.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive, or exceeds 24.8 days.</exception>
    /// <exception cref="ObjectDisposedException">The dispatcher thread has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A work item that timed out earlier still blocks the dispatcher.</exception>
    public Task InvokeAsync(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        TimeSpan bound = ResolveTimeout(timeout);
        ThrowIfDisposed();
        ThrowIfStalled();
        long queuedAt = Stopwatch.GetTimestamp();
        DispatcherOperation operation = Dispatcher.InvokeAsync(action);
        return CompleteAsync(new WorkItem(operation, action, DispatcherPriority.Normal, bound, queuedAt), operation.Task);
    }

    /// <summary>Runs <paramref name="func"/> on the dispatcher thread and returns its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The work to run.</param>
    /// <param name="timeout">
    /// The deadline from queueing to the end of the run; <see cref="DefaultOperationTimeout"/> when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A task that completes with the result, faulted with the exception if the work threw, with a
    /// <see cref="DispatcherTimeoutException"/> if the deadline passed first, or cancelled with the test.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive, or exceeds 24.8 days.</exception>
    /// <exception cref="ObjectDisposedException">The dispatcher thread has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A work item that timed out earlier still blocks the dispatcher.</exception>
    public Task<T> InvokeAsync<T>(Func<T> func, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(func);
        TimeSpan bound = ResolveTimeout(timeout);
        ThrowIfDisposed();
        ThrowIfStalled();
        long queuedAt = Stopwatch.GetTimestamp();
        DispatcherOperation<T> operation = Dispatcher.InvokeAsync(func);
        return CompleteAsync(new WorkItem(operation, func, DispatcherPriority.Normal, bound, queuedAt), operation.Task);
    }

    /// <summary>
    /// Starts the asynchronous body <paramref name="func"/> on the dispatcher thread, so every await inside it resumes
    /// there, and waits for the body itself rather than for its first await.
    /// </summary>
    /// <param name="func">The asynchronous work to run.</param>
    /// <param name="timeout">
    /// The one deadline, from queueing to the body's completion, covering both its start on the dispatcher and the
    /// body itself; <see cref="DefaultOperationTimeout"/> when <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A task that completes when the body's task completes, with its outcome, with a
    /// <see cref="DispatcherTimeoutException"/> if the deadline passed first, or cancelled with the test. A body that
    /// outlives its deadline keeps running on the dispatcher, unobserved.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive, or exceeds 24.8 days.</exception>
    /// <exception cref="ObjectDisposedException">The dispatcher thread has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, or the body returned no task.
    /// </exception>
    public async Task InvokeAsync(Func<Task> func, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(func);
        TimeSpan bound = ResolveTimeout(timeout);
        ThrowIfDisposed();
        ThrowIfStalled();
        long queuedAt = Stopwatch.GetTimestamp();
        DispatcherOperation<Task> operation = Dispatcher.InvokeAsync(func);
        var item = new WorkItem(operation, func, DispatcherPriority.Normal, bound, queuedAt);
        Task body = await CompleteAsync(item, operation.Task).ConfigureAwait(false);
        await WaitForBodyAsync(
            item,
            body ?? throw new InvalidOperationException("The asynchronous body returned no task.")).ConfigureAwait(false);
        ThrowIfFaulted();
    }

    /// <summary>
    /// Starts the asynchronous body <paramref name="func"/> on the dispatcher thread, so every await inside it resumes
    /// there, and returns the body's result.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The asynchronous work to run.</param>
    /// <param name="timeout">
    /// The one deadline, from queueing to the body's completion, covering both its start on the dispatcher and the
    /// body itself; <see cref="DefaultOperationTimeout"/> when <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A task that completes with the body's result, or with its fault or cancellation, with a
    /// <see cref="DispatcherTimeoutException"/> if the deadline passed first, or cancelled with the test. A body that
    /// outlives its deadline keeps running on the dispatcher, unobserved.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive, or exceeds 24.8 days.</exception>
    /// <exception cref="ObjectDisposedException">The dispatcher thread has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, or the body returned no task.
    /// </exception>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> func, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(func);
        TimeSpan bound = ResolveTimeout(timeout);
        ThrowIfDisposed();
        ThrowIfStalled();
        long queuedAt = Stopwatch.GetTimestamp();
        DispatcherOperation<Task<T>> operation = Dispatcher.InvokeAsync(func);
        var item = new WorkItem(operation, func, DispatcherPriority.Normal, bound, queuedAt);
        Task<T> body = await CompleteAsync(item, operation.Task).ConfigureAwait(false);
        Task<T> started = body ?? throw new InvalidOperationException("The asynchronous body returned no task.");
        await WaitForBodyAsync(item, started).ConfigureAwait(false);
        T result = await started.ConfigureAwait(false);
        ThrowIfFaulted();
        return result;
    }

    /// <summary>
    /// Waits until every work item queued above <paramref name="priority"/> has run. Engine events and continuations
    /// are posted at <see cref="DispatcherPriority.Normal"/>, so the default drains them, including those they post in
    /// turn.
    /// </summary>
    /// <param name="priority">The priority of the marker item; work queued above it runs first.</param>
    /// <param name="timeout">
    /// The deadline from queueing to the marker's run; <see cref="DefaultOperationTimeout"/> when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A task that completes when the marker item has run, with a <see cref="DispatcherTimeoutException"/> if the
    /// deadline passed first, or cancelled with the test.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive, or exceeds 24.8 days.</exception>
    /// <exception cref="ObjectDisposedException">The dispatcher thread has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A work item that timed out earlier still blocks the dispatcher.</exception>
    public Task FlushAsync(DispatcherPriority priority = DispatcherPriority.Background, TimeSpan? timeout = null)
    {
        TimeSpan bound = ResolveTimeout(timeout);
        ThrowIfDisposed();
        ThrowIfStalled();
        long queuedAt = Stopwatch.GetTimestamp();
        DispatcherOperation operation = Dispatcher.InvokeAsync(static () => { }, priority);
        return CompleteAsync(new WorkItem(operation, Work: null, priority, bound, queuedAt), operation.Task);
    }

    /// <summary>
    /// Shuts the dispatcher down and waits at most 5 s for the thread to end. Idempotent, and never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Dispatcher.InvokeShutdown"/> would wait without bound for a work item that never returns; a
    /// <see cref="DispatcherPriority.Send"/> shutdown request with a bounded join keeps disposal within 5 s. A thread
    /// still busy after that is a background thread, so it cannot keep the test process alive.
    /// </para>
    /// <para>
    /// A join that times out leaves <see cref="ShutdownCompleted"/> <see langword="false"/> and is reported as a
    /// warning on the current test, naming the stalled work item when one is known. It is not thrown: disposal runs
    /// while the test's own failure, usually the timeout the stalled item caused, is propagating, and an exception
    /// here would replace it.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);

        // On the dispatcher thread itself the shutdown runs once the current work item returns, so there is nothing
        // to join.
        if (Thread.CurrentThread == _thread || _thread.Join(_shutdownTimeout))
        {
            return;
        }

        string cause = Volatile.Read(ref _stalled) is { IsPending: true } stalled
            ? string.Create(CultureInfo.InvariantCulture, $"the work item {stalled.Description} is {stalled.StatusText}")
            : "a work item posted to it has not returned";
        TestContext.Current.AddWarning(string.Create(
            CultureInfo.InvariantCulture,
            $"The test dispatcher thread was still running {_shutdownTimeout.TotalSeconds:0} s after its shutdown request: {cause}. It is a background thread and was abandoned, so work it holds may outlive the test."));
    }

    private static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        TimeSpan bound = timeout ?? DefaultOperationTimeout;
        if (bound <= TimeSpan.Zero || bound > _maxOperationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                bound,
                "A work item's deadline must be positive and at most int.MaxValue milliseconds; an unbounded wait on the dispatcher is not offered.");
        }

        return bound;
    }

    // WaitAsync's own expiry, not a TimeoutException the work threw: a fault of the work is the task's first inner
    // exception, and awaiting the bounded wait rethrows that same instance.
    private static bool IsDeadlineExpiry(TimeoutException exception, Task task) =>
        !ReferenceEquals(exception, task.Exception?.InnerException);

    private static async Task WaitForBodyAsync(WorkItem item, Task body)
    {
        try
        {
            await body.WaitAsync(item.Remaining, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (IsDeadlineExpiry(exception, body))
        {
            // The dispatcher ran the body's start, so it is not known to be stalled; later work may still get through,
            // and each item has its own deadline if it does not.
            throw new DispatcherTimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The asynchronous body {item.Description} did not complete within {item.Bound.TotalSeconds:0.###} s of being queued on the test dispatcher: it awaits work that has not finished, or the dispatcher it resumes on is blocked or saturated."),
                exception);
        }
    }

    private async Task CompleteAsync(WorkItem item, Task operation)
    {
        ThrowIfFaulted();
        await WaitForRunAsync(item, operation).ConfigureAwait(false);
        ThrowIfFaulted();
    }

    private async Task<T> CompleteAsync<T>(WorkItem item, Task<T> operation)
    {
        ThrowIfFaulted();
        await WaitForRunAsync(item, operation).ConfigureAwait(false);
        T result = await operation.ConfigureAwait(false);
        ThrowIfFaulted();
        return result;
    }

    private async Task WaitForRunAsync(WorkItem item, Task operation)
    {
        try
        {
            await operation.WaitAsync(item.Remaining, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (IsDeadlineExpiry(exception, operation))
        {
            // Read once: the item may finish while the message is built.
            string state = item.StatusText;

            // Remembered before the throw, so the cleanup that follows this failure is refused at once instead of
            // queued behind the item. The earliest item still pending is kept: it is the one the dispatcher is stuck
            // behind or in.
            if (item.IsPending && Volatile.Read(ref _stalled) is not { IsPending: true })
            {
                Volatile.Write(ref _stalled, item);
            }

            throw new DispatcherTimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The test dispatcher did not run and complete the work item {item.Description} within {item.Bound.TotalSeconds:0.###} s: the item is {state}, so the dispatcher is blocked or saturated."),
                exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfStalled()
    {
        if (Volatile.Read(ref _stalled) is { IsPending: true } stalled)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The test dispatcher is stalled: the work item {stalled.Description}, which passed its {stalled.Bound.TotalSeconds:0.###} s deadline, is {stalled.StatusText}. New work is refused rather than queued behind it."));
        }
    }

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

    /// <summary>A work item posted to the dispatcher, with its one deadline.</summary>
    /// <param name="Operation">The dispatcher's handle on the posted work.</param>
    /// <param name="Work">The posted delegate, named in messages; <see langword="null"/> for a flush marker.</param>
    /// <param name="Priority">The priority the work was posted at.</param>
    /// <param name="Bound">The deadline, measured from <paramref name="QueuedAt"/>.</param>
    /// <param name="QueuedAt">The <see cref="Stopwatch"/> timestamp taken just before the work was posted.</param>
    private sealed record WorkItem(
        DispatcherOperation Operation,
        Delegate? Work,
        DispatcherPriority Priority,
        TimeSpan Bound,
        long QueuedAt)
    {
        /// <summary>Gets a value indicating whether the work is still queued or running on the dispatcher.</summary>
        public bool IsPending =>
            Operation.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing;

        /// <summary>Gets the time left before the deadline; zero once it has passed.</summary>
        public TimeSpan Remaining
        {
            get
            {
                TimeSpan left = Bound - Stopwatch.GetElapsedTime(QueuedAt);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        /// <summary>Gets the work as a message names it: the delegate's declaring type and method, and the priority.</summary>
        public string Description => Work is null
            ? string.Create(CultureInfo.InvariantCulture, $"'flush marker' at {Priority} priority")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"'{Work.Method.DeclaringType?.FullName}.{Work.Method.Name}' at {Priority} priority");

        /// <summary>Gets the work's state on the dispatcher as a message phrases it.</summary>
        public string StatusText => Operation.Status switch
        {
            DispatcherOperationStatus.Pending => "still queued behind earlier work",
            DispatcherOperationStatus.Executing => "still running on the dispatcher thread",
            DispatcherOperationStatus.Aborted => "aborted by the dispatcher's shutdown",
            _ => "completed only after its deadline",
        };
    }
}

/// <summary>
/// The deadline of a work item posted to a <see cref="DispatcherThread"/> passed before the item ran and completed,
/// or before its asynchronous body completed.
/// </summary>
/// <remarks>
/// A distinct type, so a wait can tell a dispatcher that did not answer in time from a <see cref="TimeoutException"/>
/// the work itself threw.
/// </remarks>
internal sealed class DispatcherTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What timed out, after how long, and the work item's state on the dispatcher.</param>
    /// <param name="innerException">The expiry reported by the bounded wait.</param>
    public DispatcherTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
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
/// <see cref="DispatcherThread"/>, and every wait evaluates its condition there. Each evaluation is bounded by the
/// wait's own deadline, so a blocked dispatcher fails the wait on time instead of holding its deadline back.
/// </remarks>
internal static class ControllerGraph
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(25);

    // A responsive dispatcher services work within 250 ms, the bound the timing tests assert. This floor still gives
    // the last evaluation before the deadline a fair chance, so only a blocked or saturated dispatcher fails one.
    private static readonly TimeSpan _minimumEvaluationTimeout = TimeSpan.FromSeconds(1);

    // A timeout's description of the controller has a bound of its own, so a dispatcher that stalls only then still
    // lets the timeout be reported.
    private static readonly TimeSpan _diagnosticsTimeout = TimeSpan.FromSeconds(5);

    // RunController.ShutdownAsync bounds itself at about 33 s with the production options, and the disposals after it
    // never block.
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(60);

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
    /// <exception cref="DispatcherTimeoutException">
    /// The graph was not created and initialised within <see cref="DispatcherThread.DefaultOperationTimeout"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A work item that timed out earlier still blocks the dispatcher.</exception>
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
    /// <param name="timeout">
    /// How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no overall bound, though each evaluation of the
    /// condition stays bounded by <see cref="DispatcherThread.DefaultOperationTimeout"/>.
    /// </param>
    /// <param name="description">What is awaited, completing "waiting for …" in the timeout message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <exception cref="TimeoutException">
    /// The condition did not hold within <paramref name="timeout"/>, or the dispatcher did not evaluate it within the
    /// time left (at least 1 s, at most <see cref="DispatcherThread.DefaultOperationTimeout"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, or an exception escaped a work item posted to it.
    /// </exception>
    /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
    public static Task WaitUntilAsync(DispatcherThread ui, Func<bool> condition, TimeSpan timeout, string description) =>
        WaitCoreAsync(ui, condition, timeout, description, diagnostics: null);

    /// <summary>
    /// Waits until <paramref name="condition"/>, evaluated on the dispatcher thread at most 25 ms apart, holds; a
    /// timeout reports <paramref name="controller"/>'s run state, post-run activity and messages, read on the
    /// dispatcher within 5 s, or why they could not be read.
    /// </summary>
    /// <param name="ui">The dispatcher thread that owns <paramref name="controller"/>.</param>
    /// <param name="controller">The controller described in the timeout message.</param>
    /// <param name="condition">The condition; it may read UI-affine objects.</param>
    /// <param name="timeout">
    /// How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no overall bound, though each evaluation of the
    /// condition stays bounded by <see cref="DispatcherThread.DefaultOperationTimeout"/>.
    /// </param>
    /// <param name="description">What is awaited, completing "waiting for …" in the timeout message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <exception cref="TimeoutException">
    /// The condition did not hold within <paramref name="timeout"/>, or the dispatcher did not evaluate it within the
    /// time left (at least 1 s, at most <see cref="DispatcherThread.DefaultOperationTimeout"/>); in that second case
    /// the controller is not described, because describing it would wait on the same dispatcher.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, or an exception escaped a work item posted to it.
    /// </exception>
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
    /// <param name="timeout">
    /// How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> for no overall bound, though each evaluation of the
    /// condition stays bounded by <see cref="DispatcherThread.DefaultOperationTimeout"/>.
    /// </param>
    /// <returns>A task that completes once the run is concluded and published.</returns>
    /// <exception cref="TimeoutException">
    /// The run did not conclude and publish within <paramref name="timeout"/>, or the dispatcher did not evaluate the
    /// condition within the time left.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, or an exception escaped a work item posted to it.
    /// </exception>
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
    /// dispatcher thread, within 60 s.
    /// </summary>
    /// <remarks>
    /// The 60 s cover the queued start and the whole body: <c>RunController.ShutdownAsync</c> bounds itself at about
    /// 33 s with the production options, and the disposals never block. When the bound passes, the disposals stay in
    /// the body's <see langword="finally"/> and run if the dispatcher ever resumes, while the caller gets the timeout.
    /// </remarks>
    /// <param name="ui">The dispatcher thread that owns the graph.</param>
    /// <param name="graph">The graph to shut down.</param>
    /// <returns>A task that completes when the graph is disposed.</returns>
    /// <exception cref="DispatcherTimeoutException">The shutdown and the disposals did not complete within 60 s.</exception>
    /// <exception cref="InvalidOperationException">
    /// A work item that timed out earlier still blocks the dispatcher, so nothing was queued; or an exception escaped
    /// a work item posted to it.
    /// </exception>
    /// <exception cref="OperationCanceledException">The test was cancelled.</exception>
    public static Task ShutdownAndDisposeAsync(DispatcherThread ui, Graph graph)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(graph);
        return ui.InvokeAsync(
            async () =>
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
            },
            _shutdownTimeout);
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
        bool bounded = timeout != Timeout.InfiniteTimeSpan;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            // The deadline is the stopwatch's, never the dispatcher's: an evaluation gets only the time left, held
            // between the floor and the default work-item deadline, so a blocked dispatcher fails the wait on time.
            TimeSpan evaluationTimeout = bounded
                ? TimeSpan.FromTicks(Math.Clamp(
                    (timeout - stopwatch.Elapsed).Ticks,
                    _minimumEvaluationTimeout.Ticks,
                    DispatcherThread.DefaultOperationTimeout.Ticks))
                : DispatcherThread.DefaultOperationTimeout;
            bool holds;
            try
            {
                holds = await ui.InvokeAsync(condition, evaluationTimeout).ConfigureAwait(false);
            }
            catch (DispatcherTimeoutException exception)
            {
                // No diagnostics: describing the controller would queue more work behind the item the dispatcher has
                // not reached, and the dispatcher now refuses new work until it does.
                throw new TimeoutException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Timed out after {stopwatch.Elapsed.TotalSeconds:0.###} s waiting for {description}: the test dispatcher did not evaluate the condition within {evaluationTimeout.TotalSeconds:0.###} s, so it is blocked or saturated, and no diagnostics were requested from it."),
                    exception);
            }

            if (holds)
            {
                return;
            }

            if (bounded && stopwatch.Elapsed >= timeout)
            {
                string state = string.Empty;
                Exception? diagnosticsFailure = null;
                if (diagnostics is not null)
                {
                    (state, diagnosticsFailure) = await DescribeWithinAsync(ui, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                throw new TimeoutException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Timed out after {timeout.TotalSeconds:0.###} s waiting for {description}{state}."),
                    diagnosticsFailure);
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(string Text, Exception? Failure)> DescribeWithinAsync(
        DispatcherThread ui,
        Func<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return ("; " + await ui.InvokeAsync(diagnostics, _diagnosticsTimeout).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The timeout is reported either way. A dispatcher that is stalled, faulted or shut down, or a description
            // that throws, leaves its reason in the message and its exception as the timeout's inner exception.
            return (
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"; diagnostics unavailable ({exception.GetType().Name}: {exception.Message})"),
                exception);
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
    private const string HostDisplayName = "RamQuick.TestHost";

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

        return RunningHost.Launch(startInfo, HostDisplayName);
    }


    /// <summary>
    /// A started child process, the host: <c>RamQuick.TestHost</c>, or the child that runs an isolated test (see
    /// <see cref="IsolatedTestRun"/>). Its stdout and stderr lines are captured asynchronously, so its pipes never fill
    /// and block it.
    /// </summary>
    /// <remarks>
    /// Every member except <see cref="DisposeAsync"/> may be called from any thread. Every path that kills the host
    /// waits for it to exit, so a host that is reported killed can no longer write to a test's folders.
    /// </remarks>
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

        private RunningHost(Process process, string displayName)
        {
            Process = process;
            DisplayName = displayName;
        }

        /// <summary>Gets the host process.</summary>
        public Process Process { get; }

        /// <summary>Gets the name that messages about the host give it, such as <c>RamQuick.TestHost</c>.</summary>
        public string DisplayName { get; }

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
                        $"{DisplayName} (pid {ProcessId}) did not exit within {timeout.TotalSeconds:0.###} s and was killed.{Environment.NewLine}{DescribeOutput()}"));
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
        /// <exception cref="TimeoutException">
        /// The host had not exited 10 s after the kill; the inner exception, if any, is the kill's own failure.
        /// </exception>
        public void Kill()
        {
            if (KillAndReap() is { } failure)
            {
                throw failure;
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
                    // A host that survives a tree kill for 10 s is left to Windows; throwing here would mask the
                    // failure of the test that is being torn down. The wait blocks, so it runs off the caller's thread.
                    await Task.Run(KillAndReap).ConfigureAwait(false);
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
        /// <remarks>
        /// When reading the output cannot begin after the process has started, the process is killed and reaped
        /// before the failure is reported, so no failed launch leaves a process behind.
        /// </remarks>
        /// <param name="startInfo">The host's start information.</param>
        /// <param name="displayName">The name that messages about the host give it.</param>
        /// <returns>The running host.</returns>
        /// <exception cref="InvalidOperationException">
        /// The process could not be started, or reading its output could not begin; the process has then exited and
        /// the exception is the original one, unchanged.
        /// </exception>
        /// <exception cref="AggregateException">
        /// Reading the output could not begin, and the started process then had not exited 10 s after it was killed:
        /// the first inner exception is the original failure, the second the <see cref="TimeoutException"/> of the
        /// cleanup.
        /// </exception>
        internal static RunningHost Launch(ProcessStartInfo startInfo, string displayName)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var host = new RunningHost(process, displayName);
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
            catch (Exception readFailure)
            {
                // Never leave a started host running unobserved: it must have exited before its process object is
                // released, or it could outlive the test and hold or write to the folders cleanup deletes.
                TimeoutException? cleanupFailure = host.KillAndReap();
                process.Dispose();
                if (cleanupFailure is null)
                {
                    throw;
                }

                throw new AggregateException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Reading the output of {displayName} (pid {host.ProcessId}) could not begin, and the started process could not be reaped."),
                    readFailure,
                    cleanupFailure);
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

        /// <summary>
        /// The one kill path: kills the host's process tree, then waits at most 10 s for the host to exit.
        /// </summary>
        /// <returns>
        /// <see langword="null"/> once the host has exited; otherwise the failure to report, whose inner exception, if
        /// any, is the kill's own failure.
        /// </returns>
        private TimeoutException? KillAndReap()
        {
            Exception? killFailure = null;
            try
            {
                TryKillTree();
            }
            catch (Exception exception) when (exception is Win32Exception or AggregateException)
            {
                // A host the kill could not reach may still exit on its own within the wait below; only a host that
                // is still running then is a failure.
                killFailure = exception;
            }

            if (Process.WaitForExit(_killTimeout))
            {
                return null;
            }

            return new TimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayName} (pid {ProcessId}) had not exited {_killTimeout.TotalSeconds:0} s after it was killed."),
                killFailure);
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

/// <summary>
/// Runs one test's scenario in a child <c>RamQuick.IntegrationTests.exe</c> from this output folder, which a deadline
/// kills and which is always reaped. It is for a scenario whose failure could leave the test process unusable: a
/// blocked finalizer thread, for one, can never be unblocked, and every later collection in that process waits on it.
/// </summary>
/// <remarks>
/// <para>
/// The test calls <see cref="RunAsync"/> with its scenario. In a process where <see cref="MarkerVariable"/> is unset,
/// the call starts the child with the variable set to the test's full name and an xUnit <c>-method</c> filter for that
/// test alone, waits for it and reads its result XML. In the child, where the variable names the test, the same call
/// runs the scenario. The child's own <see cref="EngineBindingFixture"/> binds it to the test engine library once,
/// before the test runs, as in every test process.
/// </para>
/// <para>
/// xUnit exits 0 when its filter matches no test, so the exit code alone proves nothing: the child passes only when its
/// result XML holds exactly that test, passed, no error outside it, and the child exited 0.
/// </para>
/// </remarks>
internal static class IsolatedTestRun
{
    /// <summary>
    /// The environment variable that makes a test process an isolated child, naming the one test it runs as
    /// <c>Namespace.Class.Method</c>.
    /// </summary>
    public const string MarkerVariable = "RAMQUICK_ISOLATED_TEST";

    private const string ResultFileName = "isolated-result.xml";
    private const string PassedResult = "Pass";

    /// <summary>
    /// Runs <paramref name="scenario"/> in an isolated child process when a test calls it, or directly in the child
    /// that runs that test.
    /// </summary>
    /// <typeparam name="TTestClass">The test's class.</typeparam>
    /// <param name="scenario">The test's body.</param>
    /// <param name="childDeadline">
    /// How long the child may run before it is killed: the scenario's worst-case waits, plus the child runner's start,
    /// discovery and report.
    /// </param>
    /// <param name="output">The test's output, which receives the child's test output.</param>
    /// <param name="cancellationToken">
    /// The test's cancellation, <c>TestContext.Current.CancellationToken</c>; cancelling it kills the child.
    /// </param>
    /// <param name="testMethod">The test method, supplied by the compiler.</param>
    /// <returns>A task that completes once the scenario has passed, wherever it ran.</returns>
    /// <exception cref="TimeoutException">
    /// The child did not exit within <paramref name="childDeadline"/>. It has been killed and reaped, and the message
    /// carries its captured output.
    /// </exception>
    /// <exception cref="OperationCanceledException">The test was cancelled; the child has been killed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The test executable is missing, or this process is the isolated child of another test.
    /// </exception>
    public static Task RunAsync<TTestClass>(
        Func<Task> scenario,
        TimeSpan childDeadline,
        ITestOutputHelper output,
        CancellationToken cancellationToken,
        [CallerMemberName] string testMethod = "")
        where TTestClass : class
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(testMethod);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(childDeadline, TimeSpan.Zero);

        string testName = typeof(TTestClass).FullName + "." + testMethod;
        string? marker = Environment.GetEnvironmentVariable(MarkerVariable);
        if (string.IsNullOrEmpty(marker))
        {
            return RunInChildAsync(testName, childDeadline, output, cancellationToken);
        }

        if (string.Equals(marker, testName, StringComparison.Ordinal))
        {
            return scenario();
        }

        throw new InvalidOperationException(
            $"This process is the isolated child of {marker} ({MarkerVariable} is set), so it must not run {testName}.");
    }

    private static string ChildExecutable =>
        Path.Combine(AppContext.BaseDirectory, typeof(IsolatedTestRun).Assembly.GetName().Name + ".exe");

    private static async Task RunInChildAsync(
        string testName,
        TimeSpan childDeadline,
        ITestOutputHelper output,
        CancellationToken cancellationToken)
    {
        string executable = ChildExecutable;
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"The test executable was not found at '{executable}', so {testName} cannot run in an isolated child process.");
        }

        // Deleted only after the child has been reaped, so no running child holds the result file.
        using var root = new TempDataRoot();
        string resultPath = Path.Combine(root.Path, ResultFileName);
        string displayName = $"{Path.GetFileName(executable)} running {testName}";

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        string[] arguments =
        [
            "-method", testName, "-noLogo", "-noColor", "-noAutoReporters", "-showLiveOutput", "-result-xml", resultPath,
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[MarkerVariable] = testName;

        var stopwatch = Stopwatch.StartNew();
        int processId;
        int exitCode;
        string capturedOutput;
        TestHostLauncher.RunningHost child = TestHostLauncher.RunningHost.Launch(startInfo, displayName);
        await using (child.ConfigureAwait(false))
        {
            processId = child.ProcessId;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Started {displayName} as pid {processId}, to be killed after {childDeadline.TotalSeconds:0} s."));

            // On a timeout or cancellation the child's tree is killed and reaped before the exception propagates.
            exitCode = await child.WaitForExitAsync(childDeadline, cancellationToken).ConfigureAwait(false);
            capturedOutput = child.DescribeOutput();
        }

        ChildResult result = ReadResult(resultPath, testName, exitCode);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{displayName} (pid {processId}) exited with code {exitCode} after {stopwatch.Elapsed.TotalSeconds:0.0} s; its test output follows."));
        foreach (string line in result.OutputLines)
        {
            output.WriteLine(line);
        }

        if (result.Failure is not null)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{displayName} (pid {processId}, exit code {exitCode}) did not pass: {result.Failure}{Environment.NewLine}{capturedOutput}"));
        }
    }

    private static ChildResult ReadResult(string resultPath, string testName, int exitCode)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(resultPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            // A child that dies before its run ends writes no result.
            return new ChildResult(
                $"its result XML '{resultPath}' could not be read ({exception.GetType().Name}: {exception.Message}).",
                []);
        }

        string errors = DescribeErrors(document);
        XElement[] tests = document.Descendants("test").ToArray();
        if (tests.Length != 1)
        {
            return new ChildResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"its result XML holds {tests.Length} test result(s) instead of exactly one, for {testName}; xUnit exits 0 even when its filter matches no test.{errors}"),
                []);
        }

        XElement test = tests[0];
        IReadOnlyList<string> lines = OutputLines(test);
        string ran = $"{(string?)test.Attribute("type")}.{(string?)test.Attribute("method")}";
        string? outcome = (string?)test.Attribute("result");
        if (!string.Equals(ran, testName, StringComparison.Ordinal))
        {
            return new ChildResult($"it ran {ran} instead.{errors}", lines);
        }

        if (!string.Equals(outcome, PassedResult, StringComparison.Ordinal))
        {
            return new ChildResult(
                $"the test's result is {outcome ?? "missing"}.{Environment.NewLine}{DescribeFailure(test)}{errors}",
                lines);
        }

        if (errors.Length > 0)
        {
            return new ChildResult($"the test passed, but the run reported errors outside it.{errors}", lines);
        }

        if (exitCode != 0)
        {
            return new ChildResult(
                string.Create(CultureInfo.InvariantCulture, $"the test passed, but the child exited with code {exitCode}."),
                lines);
        }

        return new ChildResult(null, lines);
    }

    // xUnit records a failure as <failure exception-type="…"><message/><stack-trace/></failure>, and a skip as <reason/>.
    private static string DescribeFailure(XElement owner)
    {
        if (owner.Element("failure") is not { } failure)
        {
            return (string?)owner.Element("reason") is { } reason ? "Reason: " + reason : "No failure was recorded.";
        }

        return $"{(string?)failure.Attribute("exception-type") ?? "Exception"}: {(string?)failure.Element("message")}"
            + $"{Environment.NewLine}{(string?)failure.Element("stack-trace")}";
    }

    // Failures outside a test, such as a fixture or class cleanup that threw, are <errors><error type name>.
    private static string DescribeErrors(XDocument document)
    {
        string[] errors = document
            .Descendants("errors")
            .Elements("error")
            .Select(error => $"{(string?)error.Attribute("type")} error in {(string?)error.Attribute("name")}: {DescribeFailure(error)}")
            .ToArray();
        return errors.Length == 0
            ? string.Empty
            : Environment.NewLine + "Errors outside the test:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
    }

    private static IReadOnlyList<string> OutputLines(XElement test)
    {
        string? text = (string?)test.Element("output");
        return string.IsNullOrEmpty(text)
            ? []
            : text.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    // What the child's result XML says: why it did not pass, or null once it did, and the test's output lines.
    private sealed record ChildResult(string? Failure, IReadOnlyList<string> OutputLines);
}

