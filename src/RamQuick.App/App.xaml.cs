using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
/// an unsupported platform, an unexpected startup failure or a main window that failed to close, 2 when another
/// instance is running, and 3 when the test engine cannot be loaded.
/// </para>
/// <para>
/// The constructor does no work, so tests can create the application and call <c>InitializeComponent()</c> only to
/// load the shared resources of <c>App.xaml</c>.
/// </para>
/// <para>
/// The application also announces the main window's live regions to assistive technology; see
/// <see cref="LiveRegionProperty"/>. It returns keyboard focus to a control that was disabled while it held focus, as
/// the Start/Stop toggle is around every run transition, once that control is enabled again; see
/// <see cref="RestoresKeyboardFocusProperty"/>.
/// </para>
/// </remarks>
public sealed partial class App : Application
{
    /// <summary>
    /// Identifies the <c>LiveRegion</c> attached property: the <see cref="LiveRegion"/> whose text a
    /// <see cref="TextBlock"/> shows, which makes it one copy of that live region. The default is
    /// <see cref="LiveRegion.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF never raises <see cref="AutomationEvents.LiveRegionChanged"/> by itself, so a text binding alone does not
    /// tell a screen reader that a verdict, the first error or a notice has appeared while keyboard focus is elsewhere.
    /// The application raises it, from the view, without code-behind. Each copy of a live region opts in from XAML with
    /// three settings: <c>AutomationProperties.LiveSetting</c> other than <c>Off</c>, this property naming the region,
    /// and <c>NotifyOnTargetUpdated=True</c> on the binding that supplies the announced text:
    /// </para>
    /// <code language="xml">
    /// &lt;TextBlock AutomationProperties.LiveSetting="Polite"
    ///            app:App.LiveRegion="Notice"
    ///            Text="{Binding NoticeText, NotifyOnTargetUpdated=True}" /&gt;
    /// </code>
    /// <para>
    /// The announced text is the copy's automation name, or its text when no name is set. A change is announced once
    /// the binding, template and layout work it causes has run, so the properties one change raises together lead to
    /// one announcement, and only when it is a change: the notice and the result when their text differs from the
    /// one last announced, the first-error message only when it appears, not as its error count grows. Text that is
    /// re-raised unchanged, as the notice is on every sensor reading, is never announced again.
    /// </para>
    /// <para>
    /// The copies of a region in one window share what has been announced, and only a copy that the current layout
    /// shows announces, so a region shown twice, as the compact layout shows the notice and the first error, and a
    /// template copy created when the layout changes, never repeat an announcement. An empty text clears the region,
    /// so the next text is announced even when it equals the last one, as when a new run ends with the same verdict.
    /// </para>
    /// <para>
    /// A change is raised only while a UI Automation client listens for it. A screen reader that is already running
    /// connects to a newly shown window only after it has appeared, so a change that no client hears is tried again
    /// every 250 ms for two seconds, and at every later update of its text, until one does.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty LiveRegionProperty = DependencyProperty.RegisterAttached(
        "LiveRegion",
        typeof(LiveRegion),
        typeof(App),
        new PropertyMetadata(LiveRegion.None));

    /// <summary>
    /// Identifies the <c>RestoresKeyboardFocus</c> attached property: whether an element takes keyboard focus back
    /// when it is enabled again after it was disabled while it held focus. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF moves keyboard focus off a focused element as soon as it is disabled, to its nearest focusable ancestor,
    /// which in the main window is the window itself, and never gives it back. The Start/Stop toggle is disabled
    /// while it has focus at every run transition: from an accepted Start until Preparing, while the journal's
    /// <c>begin</c> record becomes durable, and after the verdict through "Releasing memory…" and "Saving results…".
    /// Without this property a keyboard or screen-reader user who presses Space or Enter on Start is left on the bare
    /// window, and the next key press neither stops the run nor starts the next one. The element opts in from XAML:
    /// </para>
    /// <code language="xml">
    /// &lt;Button app:App.RestoresKeyboardFocus="True" /&gt;
    /// </code>
    /// <para>
    /// Focus returns only while the element is still the focused element of its focus scope, which WPF leaves
    /// unchanged when focus falls to the window, and keyboard focus is on the window, on the scope or nowhere: focus
    /// the user has moved to another control stays there. In a window that is not active, focus returns when the
    /// window is next activated, so the element never takes the foreground from another application.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty RestoresKeyboardFocusProperty = DependencyProperty.RegisterAttached(
        "RestoresKeyboardFocus",
        typeof(bool),
        typeof(App),
        new PropertyMetadata(false, OnRestoresKeyboardFocusChanged));

    private const int ExitCodeClosed = 0;
    private const int ExitCodeFailure = 1;
    private const int ExitCodeAlreadyRunning = 2;
    private const int ExitCodeCorruptedInstallation = 3;

    private const string StartupErrorSource = "App.OnStartup";
    private const string ClosingErrorSource = "App.Closing";
    private const string ExitErrorSource = "App.OnExit";
    private const string FatalErrorSource = "UnhandledException";
    private const string LiveRegionErrorSource = "App.LiveRegion";

    // How many times a live-region change that no UI Automation client heard is tried again, and how often: about two
    // seconds, which covers a running screen reader connecting to the window once it has appeared.
    private const int LiveRegionRetries = 8;

    // How many times a close of the main window that failed is tried again, and how often: about five seconds, the
    // time an elevated instance waits for this one's mutex. A refusal that outlasts them, as a Closing raise held open
    // by a modal dialog can, is left to Application.Shutdown, whose own close then fails too and so reaches
    // DispatcherUnhandledException and the fatal report.
    private const int CloseRetries = 50;

    // Covers an elevated restart: the instance that launched it holds the mutex until its window has closed.
    private static readonly TimeSpan _singleInstanceWait = TimeSpan.FromSeconds(5);

    // A crash or startup-failure report waits at most this long for its app-errors.log write, the bound the results
    // log gives its own drain at close. The wait never holds the dispatcher: it runs on the thread pool, or in the
    // AppDomain handler, after which the process ends at once, so an unawaited write would be lost with it. The bound
    // keeps a stalled disk from holding the exit.
    private static readonly TimeSpan _appErrorWriteBound = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan _liveRegionRetryInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _closeRetryInterval = TimeSpan.FromMilliseconds(100);

    // Per-window live-region state lives on the window itself, as WorkAreaSizing keeps its sizing state, so two
    // windows, or two tests, never share it, and it is collected with its window.
    private static readonly DependencyProperty _liveRegionStateProperty = DependencyProperty.RegisterAttached(
        "LiveRegionState",
        typeof(Dictionary<LiveRegion, LiveRegionState>),
        typeof(App),
        new PropertyMetadata(null));

    private SingleInstanceGuard? _guard;
    private DataDirectory? _dataDirectory;
    private SensorMonitor? _sensorMonitor;
    private RunController? _controller;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _crashHandlersAttached;
    private bool _closing;
    private bool _closeConfirmed;
    private bool _closeFailed;
    private bool _windowClosed;

    // An exception the dispatcher leaves unhandled reaches AppDomain.UnhandledException as the same object; it is
    // reported once.
    private Exception? _reportedFatalException;

    // Set by the first dispatcher exception, which a pool thread reports and then rethrows. Any other dispatcher
    // exception finds it set and is left unhandled, and closing is refused until the rethrow ends the process.
    private int _dispatcherExceptionClaimed;

    // Registered once per process, before any window exists: Main constructs the application before anything else,
    // and the test fixture that loads App.xaml constructs it too. One class handler adds nothing to the text elements
    // that are not live regions.
    static App()
    {
        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            Binding.TargetUpdatedEvent,
            new EventHandler<DataTransferEventArgs>(OnTextBlockTargetUpdated));
    }

    /// <summary>
    /// Gets or sets the test seam that stands in for UI Automation's listener check. When set, it is called with each
    /// text element and text the application is about to announce, and returns whether a listener hears it, in place
    /// of checking for a listening client and a shown window; the event is raised only when it returns
    /// <see langword="true"/>. <see langword="null"/>, the default, is the only value outside tests.
    /// </summary>
    internal static Func<TextBlock, string, bool>? AnnouncementSink { get; set; }

    /// <summary>Gets the live region a text element shows.</summary>
    /// <param name="element">The text element.</param>
    /// <returns>The value of the <c>LiveRegion</c> attached property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    [AttachedPropertyBrowsableForType(typeof(TextBlock))]
    public static LiveRegion GetLiveRegion(TextBlock element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (LiveRegion)element.GetValue(LiveRegionProperty);
    }

    /// <summary>
    /// Makes a text element one copy of a live region, or, with <see cref="LiveRegion.None"/>, no live region.
    /// </summary>
    /// <param name="element">The text element.</param>
    /// <param name="value">The live region whose text the element shows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    public static void SetLiveRegion(TextBlock element, LiveRegion value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LiveRegionProperty, value);
    }

    /// <summary>Gets whether an element takes keyboard focus back when it is enabled again.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The value of the <c>RestoresKeyboardFocus</c> attached property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    [AttachedPropertyBrowsableForType(typeof(UIElement))]
    public static bool GetRestoresKeyboardFocus(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(RestoresKeyboardFocusProperty);
    }

    /// <summary>
    /// Sets whether an element takes keyboard focus back when it is enabled again after it was disabled while it held
    /// focus; see <see cref="RestoresKeyboardFocusProperty"/>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="value"><see langword="true"/> to return focus to the element; <see langword="false"/> to leave it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    public static void SetRestoresKeyboardFocus(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RestoresKeyboardFocusProperty, value);
    }

    /// <summary>
    /// Starts the application: checks the platform, acquires the single-instance guard, resolves the data folder,
    /// binds the engine, composes the controller, view model and window, shows the window, starts the sensor monitor
    /// and initialises the controller, which reads the system and the memory inventory, offers the elevated restart
    /// when the process is not elevated, and then recovers the previous run.
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
    /// <remarks>
    /// This runs on the UI thread, so nothing here waits. The sensor monitor, whose disposal joins its thread, has
    /// already been disposed on the thread pool before the window closed or a startup failure shut the application
    /// down, so its source is still closed before the mutex is released.
    /// </remarks>
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

        // Polled rather than waited for, so the UI thread never blocks; every attempt runs on it, the thread that
        // releases the guard in OnExit, because mutex ownership is thread-affine.
        _guard = await SingleInstanceGuard.TryAcquireAsync(Dispatcher, _singleInstanceWait);
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

        // Reads the system and the memory inventory off the UI thread, offers the elevated restart over the now visible
        // window when the process is not elevated, and only then recovers the previous run off the UI thread, so that
        // an accepted restart leaves the recovery to the elevated instance.
        await _controller.InitializeAsync();
    }

    private async Task ReportStartupFailureAsync(IUserPrompt prompt, Exception exception)
    {
        // Both reporters wait a bounded time for their write and never throw; the thread pool keeps even that wait off
        // the UI thread.
        await Task.Run(() => ReportFatalException(exception, StartupErrorSource));

        try
        {
            prompt.Inform(UiText.UnexpectedErrorTitle, SummaryFormat.RedactPersonalPaths(exception.Message));
        }
        catch (Exception promptFailure)
        {
            // The dialog itself failed, for example because the dispatcher is already shutting down. The application
            // ends regardless; the failure is kept for diagnosis.
            await Task.Run(() => ReportFatalException(promptFailure, StartupErrorSource));
        }

        await DisposeSensorMonitorOffUiThreadAsync();
        Shutdown(ExitCodeFailure);
    }

    // Closing is deferred until the controller has stopped the run, waited for Completed and Released (or given up
    // on them), and published the summaries; the window stays responsive meanwhile and shows that progress.
    private async void OnMainWindowClosingAsync(object? sender, CancelEventArgs e)
    {
        // A dispatcher exception is being reported and its rethrow ends the process. The disabled window still takes
        // a close from its title bar, and a normal exit now would end the process before the report and the crash.
        if (Volatile.Read(ref _dispatcherExceptionClaimed) != 0)
        {
            e.Cancel = true;
            return;
        }

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

        await DisposeSensorMonitorOffUiThreadAsync();
        _closeConfirmed = true;
        RequestWindowClose();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _windowClosed = true;
        Shutdown(_closeFailed ? ExitCodeFailure : ExitCodeClosed);
    }

    // The elevated instance has started and waits for this instance's mutex: close through the deferred path, which
    // completes at once because no run can be in progress while the restart is offered.
    private void OnShutdownRequested(object? sender, EventArgs e) => RequestWindowClose();

    // Window.Close throws while the window's Closing event is being raised, and the controller can raise
    // ShutdownRequested from inside InitializeAsync. Posting the close runs it from the dispatcher loop, after the
    // raising code has returned. InvokeAsync keeps an exception its delegate throws in the operation's task, where
    // nothing observes it, and the window would stay open with the exit, or an elevated restart's takeover, never
    // finished; so CloseMainWindow contains a failed close itself and ends the application.
    private void RequestWindowClose() => _ = Dispatcher.InvokeAsync(CloseMainWindow);

    private void CloseMainWindow()
    {
        if (_window is { } window && !_windowClosed)
        {
            try
            {
                window.Close();
            }
            catch (Exception exception)
            {
                EndAfterCloseFailureAsync(window, exception);
            }
        }
    }

    // Concludes the run as the deferred close does, reports the failure and ends the application with exit code 1.
    // async void, so that an exception escaping it is raised on the dispatcher, where DispatcherUnhandledException
    // reports it, rather than kept in a task.
    private async void EndAfterCloseFailureAsync(Window window, Exception exception)
    {
        // A title-bar close meanwhile stays deferred, and whichever close ends the application exits with failure.
        _closing = true;
        _closeFailed = true;

        // Returns the deferred close's work when that has run, and runs it when the close failed before the Closing
        // handler did, as an elevated takeover's can. It never throws, and it comes first so that a run in progress is
        // concluded before the report disposes what is left of its session.
        if (_controller is { } controller)
        {
            await controller.ShutdownAsync();
        }

        await Task.Run(() => ReportFatalException(exception, ClosingErrorSource));
        await DisposeSensorMonitorOffUiThreadAsync();
        _closeConfirmed = true;

        // Application.Shutdown closes the window as Window.Close does, so while a Closing event is still being raised,
        // from a nested dispatcher frame, it would fail too and skip OnExit, which releases the mutex. The close is
        // therefore retried until that raise has returned; once the window has closed, its Closed handler has already
        // shut the application down and the call below does nothing.
        for (int retry = 0; retry < CloseRetries && !TryCloseMainWindow(window); retry++)
        {
            await Task.Delay(_closeRetryInterval);
        }

        // A claimed dispatcher exception is being reported, and its rethrow ends the process; exiting now would end it
        // before the report.
        if (Volatile.Read(ref _dispatcherExceptionClaimed) == 0)
        {
            Shutdown(ExitCodeFailure);
        }
    }

    // Whether the main window has closed. Window.Close refuses with InvalidOperationException while the window is
    // closing; the failure that led to the retries has already been reported.
    private bool TryCloseMainWindow(Window window)
    {
        if (!_windowClosed)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return _windowClosed;
    }

    // The report disposes the session and writes the error, which can take up to its bound, so it runs on the thread
    // pool and the UI thread does not wait: the first exception is handled, the window is disabled, and the pool thread
    // rethrows the exception once the report has finished. Any other exception raised meanwhile finds the claim taken
    // and is left unhandled. Either way the process ends as an unhandled crash; Windows reclaims any locked pages left,
    // and the journal, which has no result record, makes the next launch report the run.
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _dispatcherExceptionClaimed, 1) != 0)
        {
            return;
        }

        e.Handled = true;
        Exception exception = e.Exception;
        _ = ThreadPool.QueueUserWorkItem(_ => ReportFatalThenRethrow(exception));
        if (_window is { } window)
        {
            window.IsEnabled = false;
        }
    }

    // The rethrow is unhandled on this pool thread, which always ends the process, and AppDomain.UnhandledException
    // finds the exception already reported. It is not rethrown on the dispatcher: there, an exception that unwinds
    // through a message box's modal loop can be swallowed, which would leave the disabled window running.
    private void ReportFatalThenRethrow(Exception exception)
    {
        ReportFatalException(exception, FatalErrorSource);
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    // May run on any thread; RunController.HandleFatalException is thread-safe. The process ends as soon as this
    // returns, so the report's bounded wait runs here. It reaches the UI thread only with an exception that has
    // escaped the dispatcher, which no longer pumps.
    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatalException(exception, FatalErrorSource);
        }
    }

    // Logs the exception and, once the controller exists, disposes its engine session so the locked memory is
    // released before the process ends. Each waits only a bounded time, so a hung engine or a stalled disk never
    // holds the exit; the wait blocks the calling thread, so the UI thread hands this to the thread pool. Never
    // throws; safe on any thread.
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
            _ = ResultsLog.TryAppendAppError(dataDirectory, source, exception, TimeProvider.System.GetUtcNow(), _appErrorWriteBound);
        }
    }

    // Each disposal is isolated, so one failure never leaves a later resource, above all the mutex, unreleased. The
    // failure's write is queued to the thread pool because the UI thread never blocks; at exit it can be lost if the
    // process ends before it lands.
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
            if (_dataDirectory is { } dataDirectory)
            {
                ResultsLog.QueueAppError(dataDirectory, ExitErrorSource, exception, TimeProvider.System.GetUtcNow());
            }
        }
    }

    // SensorMonitor.Dispose joins the sensor thread for up to a second, so it runs on the thread pool before the
    // application shuts down; the call in OnExit, on the UI thread, then finds the monitor disposed.
    private async Task DisposeSensorMonitorOffUiThreadAsync()
    {
        if (_sensorMonitor is { } sensorMonitor)
        {
            await Task.Run(() => DisposeOnExit(sensorMonitor));
        }
    }

    // A live region's text was transferred, changed or not. A cleared region is recorded at once: a run can clear it
    // and show the same text again before the dispatcher judges either, and that text is then a change. Any other text
    // is judged below binding, layout and template work, so the text, the copy's visibility and the layout triggers all
    // reflect the whole change, and the several properties one change raises are judged on their final values.
    private static void OnTextBlockTargetUpdated(object? sender, DataTransferEventArgs e)
    {
        try
        {
            if (sender is not TextBlock textBlock || !ReferenceEquals(e.TargetObject, textBlock))
            {
                return;
            }

            LiveRegion region = GetLiveRegion(textBlock);
            if (region == LiveRegion.None)
            {
                return;
            }

            if (AnnouncedText(textBlock).Length > 0)
            {
                _ = textBlock.Dispatcher.InvokeAsync(() => AnnounceLiveRegionChange(textBlock), DispatcherPriority.Background);
            }
            else if (PlacementOf(textBlock).Window is { } window)
            {
                LiveRegionStateOf(window, region).Clear();
            }
        }
        catch (Exception exception)
        {
            ReportLiveRegionFailure(exception);
        }
    }

    // Announces a copy's text when it is a change no copy of its region has announced, and the current layout shows
    // this copy. Never throws: a failed announcement must not end the run, so it is logged instead.
    private static void AnnounceLiveRegionChange(TextBlock textBlock)
    {
        try
        {
            LiveRegion region = GetLiveRegion(textBlock);
            (Window? window, bool isShown) = PlacementOf(textBlock);
            if (region == LiveRegion.None || window is null)
            {
                return;
            }

            LiveRegionState state = LiveRegionStateOf(window, region);
            string text = AnnouncedText(textBlock);
            if (text.Length == 0)
            {
                state.Clear();
                return;
            }

            // A collapsed copy, or one not marked live, leaves the change to the copy the layout shows.
            if (!isShown
                || AutomationProperties.GetLiveSetting(textBlock) == AutomationLiveSetting.Off
                || IsSameChange(region, state.Announced, text))
            {
                return;
            }

            bool heard = AnnouncementSink is { } sink ? sink(textBlock, text) : CanBeHeard(textBlock);
            if (!heard)
            {
                state.WaitForListener(region, textBlock, text);
                return;
            }

            // Recorded before the event is raised, so a raise that fails is reported once rather than at every later
            // update of the same text.
            state.MarkAnnounced(text);
            (UIElementAutomationPeer.FromElement(textBlock) ?? UIElementAutomationPeer.CreatePeerForElement(textBlock))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception exception)
        {
            ReportLiveRegionFailure(exception);
        }
    }

    // Logged off the UI thread, as the application's other error reports are.
    private static void ReportLiveRegionFailure(Exception exception)
    {
        DataDirectory? dataDirectory = Current is App app ? Volatile.Read(ref app._dataDirectory) : null;
        _ = Task.Run(() => LogAppError(dataDirectory, LiveRegionErrorSource, exception));
    }

    // A UI Automation client listens for live-region changes, and the element is in a shown window that can carry the
    // event to it.
    private static bool CanBeHeard(TextBlock textBlock) =>
        AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)
        && PresentationSource.FromVisual(textBlock) is not null;

    // What a screen reader reads for the element, and so what its live-region change announces: its automation name
    // where the element sets one, as the result does to carry the verdict with its reason, otherwise its text, which is
    // then its automation name too. A set name that is empty leaves the region empty.
    private static string AnnouncedText(TextBlock textBlock) =>
        DependencyPropertyHelper.GetValueSource(textBlock, AutomationProperties.NameProperty).BaseValueSource
            != BaseValueSource.Default
            ? AutomationProperties.GetName(textBlock) ?? string.Empty
            : textBlock.Text ?? string.Empty;

    // Whether a region's text is a change it has already announced. The first-error message is announced when it
    // appears and its error count then grows in place, so any two of its texts are the same change; the result and the
    // notice change whenever their text does.
    private static bool IsSameChange(LiveRegion region, string announced, string text) =>
        region == LiveRegion.FirstError
            ? announced.Length > 0
            : string.Equals(announced, text, StringComparison.Ordinal);

    // The window that holds an element, and whether the current layout shows the element there: neither it nor an
    // ancestor below the window is collapsed or hidden. The window's own visibility does not count, so a window that is
    // never shown, as in the layout tests, decides as a shown one would; CanBeHeard decides whether it can be heard.
    // Template content is reached through visual parents, content that has not been laid out yet through logical ones.
    private static (Window? Window, bool IsShown) PlacementOf(DependencyObject element)
    {
        bool isShown = true;
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Window window)
            {
                return (window, isShown);
            }

            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                isShown = false;
            }

            current = (current is Visual ? VisualTreeHelper.GetParent(current) : null) ?? LogicalTreeHelper.GetParent(current);
        }

        return (null, false);
    }

    private static LiveRegionState LiveRegionStateOf(Window window, LiveRegion region)
    {
        if (window.GetValue(_liveRegionStateProperty) is not Dictionary<LiveRegion, LiveRegionState> regions)
        {
            regions = new Dictionary<LiveRegion, LiveRegionState>();
            window.SetValue(_liveRegionStateProperty, regions);
        }

        if (!regions.TryGetValue(region, out LiveRegionState? state))
        {
            state = new LiveRegionState();
            regions.Add(region, state);
        }

        return state;
    }

    // Only the element's return to enabled is followed: WPF has already moved focus off it when it was disabled. The
    // handler is removed first, so setting the property again never subscribes twice. Its accessors take a UIElement,
    // so a value set on any other element through the property itself is ignored.
    private static void OnRestoresKeyboardFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsEnabledChanged -= OnRestoringElementIsEnabledChanged;
        if (e.NewValue is true)
        {
            element.IsEnabledChanged += OnRestoringElementIsEnabledChanged;
        }
    }

    private static void OnRestoringElementIsEnabledChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement element && e.NewValue is true)
        {
            QueueKeyboardFocusRestore(element);
        }
    }

    // Disabling a focused element queues WPF's own focus re-evaluation at Input priority, which moves focus to the
    // window. The restore is queued at the same priority, so it runs after that re-evaluation and finds where it left
    // focus, even when the element was disabled and enabled again within one dispatcher turn.
    private static void QueueKeyboardFocusRestore(UIElement element) =>
        _ = element.Dispatcher.InvokeAsync(() => RestoreKeyboardFocus(element), DispatcherPriority.Input);

    // Returns keyboard focus to an enabled element that lost it by being disabled. Its focus scope still names it as
    // the focused element only while the user has not moved focus elsewhere, and the element takes focus back only
    // from the window, the scope or nowhere, never from another control.
    private static void RestoreKeyboardFocus(UIElement element)
    {
        // An element outside a shown window, as in a window never shown or already closed, cannot hold focus.
        if (!GetRestoresKeyboardFocus(element)
            || !element.IsEnabled
            || element.IsKeyboardFocusWithin
            || PresentationSource.FromVisual(element) is null)
        {
            return;
        }

        DependencyObject? scope = FocusManager.GetFocusScope(element);
        if (scope is null || !ReferenceEquals(FocusManager.GetFocusedElement(scope), element))
        {
            return;
        }

        if (Window.GetWindow(element) is not { } window)
        {
            return;
        }

        // Focusing an element of an inactive window would activate it and take the foreground from another
        // application, so the restore waits for the user to come back to the window.
        if (!window.IsActive)
        {
            window.Activated -= OnRestoringWindowActivated;
            window.Activated += OnRestoringWindowActivated;
            return;
        }

        IInputElement? focused = Keyboard.FocusedElement;
        if (focused is null || ReferenceEquals(focused, window) || ReferenceEquals(focused, scope))
        {
            element.Focus();
        }
    }

    // On activation WPF puts keyboard focus back where it was when the window was deactivated, the window itself when
    // focus had fallen there, after this event; the restore is queued behind that work. One activation serves every
    // waiting restore, since a window's focus scope names one focused element.
    private static void OnRestoringWindowActivated(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Activated -= OnRestoringWindowActivated;
        if (FocusManager.GetFocusedElement(window) is UIElement element && GetRestoresKeyboardFocus(element))
        {
            QueueKeyboardFocusRestore(element);
        }
    }

    // One live region of one window: the text it last announced, and a change still waiting for a listener. Used on
    // the window's UI thread only.
    private sealed class LiveRegionState
    {
        private DispatcherTimer? _retryTimer;
        private TextBlock? _retryTarget;
        private string? _unheard;
        private int _retriesLeft;

        // The text last announced; empty before the first announcement and once the region has been cleared.
        public string Announced { get; private set; } = string.Empty;

        public void MarkAnnounced(string text)
        {
            Announced = text;
            StopWaiting();
        }

        // The region shows nothing, as at the start of a run: whatever it shows next is a change, even the same text.
        public void Clear()
        {
            Announced = string.Empty;
            StopWaiting();
        }

        // Tries an unheard change again from the copy that could not announce it. A new change gets every retry; the
        // same change re-raised gets only those left, so a region no client listens to stops retrying.
        public void WaitForListener(LiveRegion region, TextBlock textBlock, string text)
        {
            if (_unheard is null || !IsSameChange(region, _unheard, text))
            {
                _retriesLeft = LiveRegionRetries;
            }

            _unheard = text;
            _retryTarget = textBlock;
            if (_retriesLeft == 0 || _retryTimer is { IsEnabled: true })
            {
                return;
            }

            _retriesLeft--;
            if (_retryTimer is null)
            {
                _retryTimer = new DispatcherTimer(DispatcherPriority.Background, textBlock.Dispatcher)
                {
                    Interval = _liveRegionRetryInterval,
                };
                _retryTimer.Tick += OnRetryTick;
            }

            _retryTimer.Start();
        }

        private void StopWaiting()
        {
            _retryTimer?.Stop();
            _retryTarget = null;
            _unheard = null;
            _retriesLeft = 0;
        }

        private void OnRetryTick(object? sender, EventArgs e)
        {
            _retryTimer?.Stop();
            if (_retryTarget is { } target)
            {
                AnnounceLiveRegionChange(target);
            }
        }
    }
}

/// <summary>
/// A live region of the main window: text that assistive technology announces when it changes, whichever control has
/// keyboard focus. Set on each copy of the region's text through <see cref="App.LiveRegionProperty"/>.
/// </summary>
public enum LiveRegion
{
    /// <summary>Not a live region; changes are never announced. The default.</summary>
    None,

    /// <summary>
    /// The verdict with its reason, shown in a terminal state: announced when a verdict appears and whenever the
    /// verdict or its reason changes.
    /// </summary>
    Result,

    /// <summary>
    /// The first-error message: announced once when it appears, not again as its error count grows.
    /// </summary>
    FirstError,

    /// <summary>The notice line: announced whenever it shows a different, non-empty notice.</summary>
    Notice,
}
