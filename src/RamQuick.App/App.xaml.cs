using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using RamQuick.App.Services;
using RamQuick.App.ViewModels;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Sensors;

namespace RamQuick.App;

/// <summary>
/// The RAMQuick application and its composition root: checks the platform, makes this process the single instance,
/// builds the object graph by hand, shows the one window, and routes closing, an elevated restart and unhandled
/// exceptions to the run controller so the locked memory is released on every exit path.
/// </summary>
/// <remarks>
/// <para>
/// <c>App.xaml</c> sets <c>ShutdownMode="OnExplicitShutdown"</c> and no <c>StartupUri</c>: every exit goes through an
/// explicit <see cref="Application.Shutdown(int)"/> with one of these exit codes: 0 after the window has closed, 1 for
/// an unsupported platform or an unexpected startup failure, 2 when another instance is running, and 3 when the test
/// engine cannot be loaded.
/// </para>
/// <para>
/// The constructor does no work, so tests can create the application and call <c>InitializeComponent()</c> only to
/// load the shared resources of <c>App.xaml</c>.
/// </para>
/// </remarks>
public partial class App : Application
{
    private const int ExitCodeClosed = 0;
    private const int ExitCodeFailure = 1;
    private const int ExitCodeAlreadyRunning = 2;
    private const int ExitCodeCorruptedInstallation = 3;

    private const string StartupErrorSource = "App.OnStartup";
    private const string ClosingErrorSource = "App.Closing";
    private const string ExitErrorSource = "App.OnExit";
    private const string FatalErrorSource = "UnhandledException";

    // Covers an elevated restart: the instance that launched it holds the mutex until its window has closed.
    private static readonly TimeSpan _singleInstanceWait = TimeSpan.FromSeconds(5);

    private SingleInstanceGuard? _guard;
    private DataDirectory? _dataDirectory;
    private SensorMonitor? _sensorMonitor;
    private RunController? _controller;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _crashHandlersAttached;
    private bool _closing;
    private bool _closeConfirmed;
    private bool _windowClosed;

    // An exception the dispatcher leaves unhandled reaches AppDomain.UnhandledException as the same object; it is
    // reported once.
    private Exception? _reportedFatalException;

    /// <summary>
    /// Starts the application: checks the platform, acquires the single-instance guard, resolves the data folder,
    /// binds the engine, composes the controller, view model and window, shows the window, starts the sensor monitor
    /// and initialises the controller, which recovers the previous run and offers the elevated restart.
    /// </summary>
    /// <remarks>
    /// Every refusal shows its message and shuts the application down with its exit code. An unexpected failure is
    /// logged to <c>&lt;data&gt;\logs\app-errors.log</c> when a data folder exists, shown, and ends the application with
    /// exit code 1; nothing escapes this method.
    /// </remarks>
    /// <param name="e">The startup arguments; RAMQuick takes no command-line arguments.</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var prompt = new MessageBoxUserPrompt();
        try
        {
            await StartAsync(prompt);
        }
        catch (Exception exception)
        {
            await ReportStartupFailureAsync(prompt, exception);
        }
    }

    /// <summary>
    /// Releases everything the application still holds, in dependency order: the view model, the run controller
    /// (with any session left after its shutdown deadline), the sensor monitor, and finally the single-instance
    /// mutex, which lets a waiting elevated instance start.
    /// </summary>
    /// <param name="e">The exit arguments, carrying the exit code passed to <see cref="Application.Shutdown(int)"/>.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_window is { } window)
        {
            window.Closing -= OnMainWindowClosingAsync;
            window.Closed -= OnMainWindowClosed;
        }

        if (_controller is { } controller)
        {
            controller.ShutdownRequested -= OnShutdownRequested;
        }

        DisposeOnExit(_viewModel);
        DisposeOnExit(_controller);
        DisposeOnExit(_sensorMonitor);

        // Last, and on the UI thread that acquired it: mutex ownership is thread-affine.
        DisposeOnExit(_guard);

        if (_crashHandlersAttached)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            _crashHandlersAttached = false;
        }

        base.OnExit(e);
    }

    // The startup sequence. Its order is binding: nothing touches the engine, the data folder or the journal before
    // this process is known to be the only instance on a supported platform.
    private async Task StartAsync(IUserPrompt prompt)
    {
        if (!PlatformCheck.EvaluateCurrent().IsSupported)
        {
            prompt.Inform(UiText.UnsupportedPlatformTitle, UiText.UnsupportedPlatformMessage);
            Shutdown(ExitCodeFailure);
            return;
        }

        // Acquired on the UI thread, which also releases it in OnExit. No window exists yet, so the wait blocks
        // nothing the user can see.
        _guard = SingleInstanceGuard.TryAcquire(_singleInstanceWait);
        if (_guard is null)
        {
            prompt.Inform(UiText.AppTitle, UiText.AlreadyRunningMessage);
            Shutdown(ExitCodeAlreadyRunning);
            return;
        }

        // The write probe flushes to the device, so it runs off the UI thread. No writable folder is not fatal: the
        // window still opens, and the run controller refuses Start with its own message.
        DataDirectory? dataDirectory = await Task.Run(DataDirectory.Resolve);
        _dataDirectory = dataDirectory;

        // The default binding, ramquick_engine.dll beside the executable, applies; only test processes rebind it.
        IEngine engine;
        try
        {
            engine = new NativeEngine();
        }
        catch (EngineException exception)
        {
            await Task.Run(() => LogAppError(dataDirectory, StartupErrorSource, exception));
            prompt.Inform(UiText.AppTitle, UiText.CorruptedInstallationMessage);
            Shutdown(ExitCodeCorruptedInstallation);
            return;
        }

        TimeProvider timeProvider = TimeProvider.System;
        var sensorSource = new LibreHardwareMonitorSensorSource(timeProvider);
        _sensorMonitor = new SensorMonitor(sensorSource, timeProvider);
        var inventory = new SmbiosMemoryInventorySource();
        var elevation = new ElevationService();

        // The application's single run state machine: the controller moves it, and the view model reads the same
        // instance.
        var stateMachine = new RunStateMachine();
        _controller = new RunController(
            engine,
            stateMachine,
            _sensorMonitor,
            inventory,
            elevation,
            prompt,
            timeProvider,
            dataDirectory);
        _viewModel = new MainViewModel(_controller, stateMachine);
        _window = new MainWindow { DataContext = _viewModel };
        MainWindow = _window;

        _window.Closing += OnMainWindowClosingAsync;
        _window.Closed += OnMainWindowClosed;
        _controller.ShutdownRequested += OnShutdownRequested;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        _crashHandlersAttached = true;

        _window.Show();

        // OnStartup runs inside a dispatcher operation, which installs the dispatcher's synchronization context;
        // readings must be raised on the UI thread, where the controller reads them.
        SynchronizationContext uiContext = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher);
        _sensorMonitor.Start(uiContext);

        // Recovers the previous run off the UI thread, reads the memory inventory and the system, and, last, offers
        // the elevated restart over the now visible window.
        await _controller.InitializeAsync();
    }

    private async Task ReportStartupFailureAsync(IUserPrompt prompt, Exception exception)
    {
        // Both loggers write synchronously and never throw; the thread pool keeps that write off the UI thread.
        await Task.Run(() => ReportFatalException(exception, StartupErrorSource));

        try
        {
            prompt.Inform(UiText.UnexpectedErrorTitle, exception.Message);
        }
        catch (Exception promptFailure)
        {
            // The dialog itself failed, for example because the dispatcher is already shutting down. The application
            // ends regardless; the failure is kept for diagnosis.
            ReportFatalException(promptFailure, StartupErrorSource);
        }

        Shutdown(ExitCodeFailure);
    }

    // Closing is deferred until the controller has stopped the run, waited for Completed and Released (or given up
    // on them), and published the summaries; the window stays responsive meanwhile and shows that progress.
    private async void OnMainWindowClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_controller is { } controller)
            {
                await controller.ShutdownAsync();
            }
        }
        catch (Exception exception)
        {
            await Task.Run(() => ReportFatalException(exception, ClosingErrorSource));
        }

        _closeConfirmed = true;
        RequestWindowClose();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _windowClosed = true;
        Shutdown(ExitCodeClosed);
    }

    // The elevated instance has started and waits for this instance's mutex: close through the deferred path, which
    // completes at once because no run can be in progress while the restart is offered.
    private void OnShutdownRequested(object? sender, EventArgs e) => RequestWindowClose();

    // Window.Close throws while the window's Closing event is being raised, and the controller can raise
    // ShutdownRequested from inside InitializeAsync. Posting the close runs it from the dispatcher loop, after the
    // raising code has returned.
    private void RequestWindowClose() => _ = Dispatcher.InvokeAsync(CloseMainWindow);

    private void CloseMainWindow()
    {
        if (_window is { } window && !_windowClosed)
        {
            window.Close();
        }
    }

    // The dispatcher exception is left unhandled, so the process ends once the session is disposed and the locked
    // memory released; the journal, which has no result record, makes the next launch report the run.
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e) =>
        ReportFatalException(e.Exception, FatalErrorSource);

    // May run on any thread; RunController.HandleFatalException is thread-safe.
    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatalException(exception, FatalErrorSource);
        }
    }

    // Logs the exception and, once the controller exists, disposes its engine session so the locked memory is
    // released before the process ends. Never throws; safe on any thread.
    private void ReportFatalException(Exception exception, string source)
    {
        if (ReferenceEquals(Interlocked.Exchange(ref _reportedFatalException, exception), exception))
        {
            return;
        }

        if (Volatile.Read(ref _controller) is { } controller)
        {
            controller.HandleFatalException(exception);
        }
        else
        {
            LogAppError(Volatile.Read(ref _dataDirectory), source, exception);
        }
    }

    private static void LogAppError(DataDirectory? dataDirectory, string source, Exception exception)
    {
        if (dataDirectory is not null)
        {
            ResultsLog.AppendAppError(dataDirectory, source, exception, TimeProvider.System.GetUtcNow());
        }
    }

    // Each disposal is isolated, so one failure never leaves a later resource, above all the mutex, unreleased. The
    // dispatcher is stopping, so the failure is logged synchronously; a queued write could be lost with the process.
    private void DisposeOnExit(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            LogAppError(_dataDirectory, ExitErrorSource, exception);
        }
    }
}
