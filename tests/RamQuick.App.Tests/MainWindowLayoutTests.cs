using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RamQuick.App.Behaviors;
using RamQuick.App.Tests.Fakes;
using RamQuick.App.ViewModels;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;
using RamQuick.Sensors;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for the main window's layout. <see cref="MainWindow"/>, never shown and bound to a real view model filled
/// with the longest texts it can show, is measured at the preferred 560-DIP width and the narrowest supported 512:
/// its fixed rows stay within the regular and compact budgets, and the minimal fallback keeps only the control row and
/// the status line fixed. The window has exactly three accessible input controls, and its Start/Stop toggle binds the
/// command the run state calls for. Also tests <see cref="WindowSizing.Compute"/>: the window bounds and active
/// layout for the worked work areas, the layout and supported-range thresholds, and the argument guards; and
/// <see cref="WorkAreaSizing"/> on a loaded window with a real handle: DPI changes that arrive before the sizing work
/// they defer has run.
/// </summary>
/// <remarks>
/// Every <see cref="MainWindow"/> is built and measured on the STA thread of <see cref="WpfStaFixture"/>, which also
/// holds the application's shared resources. This is the only test class that creates WPF windows.
/// </remarks>
public sealed class MainWindowLayoutTests : IClassFixture<WpfStaFixture>
{
    // Screen cases assume a 48-DIP taskbar and a 39-DIP caption and frame, as the worked cases do.
    private const double TaskbarHeight = 48;
    private const double FrameHeight = 39;
    private const double Tolerance = 1e-6;

    // Measured heights are compared to within one DIP: the window rounds layout to whole device pixels.
    private const double LayoutTolerance = 1;

    // The widest values the first-error message and the error count show: a seven-digit total with two group
    // separators, the highest 8-byte-aligned address, and data words with every hexadecimal digit significant.
    private const ulong LongestErrorTotal = 1_234_567;
    private const ulong WidestAddress = 0xFFFF_FFFF_FFFF_FFF8;
    private const ulong WidestWord = ulong.MaxValue;

    // A coverage multiple whose percentage has four integer digits, as a long Exhaustive run reaches.
    private const ulong WidestCoverageMultiple = 99;

    // The engine stores step names in 64-byte fields with a terminating NUL.
    private const int MaxStepNameBytes = 63;

    // Two-digit minutes: the widest "mm:ss" a run of any type shows.
    private static readonly TimeSpan _widestRunTime = TimeSpan.FromSeconds(3599);
    private static readonly TimeSpan _releaseTime = TimeSpan.FromMilliseconds(100);

    // The user's size as a fraction of the work area at the window's own DPI: more than half of it, so the same
    // pixels converted at twice that DPI cannot hold it, yet within it, so the window's own DPI never shrinks it.
    // It is raised to the window's minimum size where that is larger, since a smaller size is enlarged to it anyway.
    private const double UserSizeFraction = 0.75;

    // A WPF dispatcher that never drains would otherwise hang the test run instead of failing it.
    private static readonly TimeSpan _staThreadTimeout = TimeSpan.FromMinutes(1);

    private readonly WpfStaFixture _fixture;

    /// <summary>Creates the tests over the shared STA thread and application resources.</summary>
    /// <param name="fixture">The STA thread every <see cref="MainWindow"/> is built and measured on.</param>
    public MainWindowLayoutTests(WpfStaFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>
    /// Work-area width and height, non-client height, and the expected initial width, initial
    /// height, minimum width, minimum height and layout, all in DIPs. A screen's work area is its
    /// pixel size divided by the scale factor, less the taskbar.
    /// </summary>
    public static TheoryData<double, double, double, double, double, double, double, WindowLayout> WorkAreaCases => new()
    {
        // 1920 × 1080 at 100 %.
        { 1920 / 1.00, (1080 / 1.00) - TaskbarHeight, FrameHeight, 560, 680, 560, 419, WindowLayout.Regular },

        // 1920 × 1080 at 150 %: H = 672, so the window opens 560 × 672.
        { 1920 / 1.50, (1080 / 1.50) - TaskbarHeight, FrameHeight, 560, 672, 560, 419, WindowLayout.Regular },

        // 1366 × 768 at 100 %.
        { 1366 / 1.00, (768 / 1.00) - TaskbarHeight, FrameHeight, 560, 680, 560, 419, WindowLayout.Regular },

        // 1366 × 768 at 125 %: H = 566.4.
        { 1366 / 1.25, (768 / 1.25) - TaskbarHeight, FrameHeight, 560, 566.4, 560, 419, WindowLayout.Regular },

        // 1366 × 768 at 150 %: H = 464, client height 425 ≥ 380.
        { 1366 / 1.50, (768 / 1.50) - TaskbarHeight, FrameHeight, 560, 464, 560, 419, WindowLayout.Regular },

        // 1366 × 768 at 200 %: H = 336, client height 297 ≥ 280, MinHeight = 319.
        { 1366 / 2.00, (768 / 2.00) - TaskbarHeight, FrameHeight, 560, 336, 560, 319, WindowLayout.Compact },

        // A 300-DIP-high work area selects the minimal fallback.
        { 683, 300, FrameHeight, 560, 300, 560, 151, WindowLayout.Minimal },

        // Narrower than the supported 512 DIPs.
        { 500, 700, FrameHeight, 500, 680, 500, 151, WindowLayout.Minimal },

        // Regular threshold, inclusive: client height 380 exactly, then just below it.
        { 800, 419, FrameHeight, 560, 419, 560, 419, WindowLayout.Regular },
        { 800, 418.99, FrameHeight, 560, 418.99, 560, 319, WindowLayout.Compact },

        // A borderless window (N = 0) is valid and meets the regular threshold at H = 380.
        { 800, 380, 0, 560, 380, 560, 380, WindowLayout.Regular },

        // Supported range, inclusive: exactly 512 × 336, then just below it on each axis.
        { 512, 336, FrameHeight, 512, 336, 512, 319, WindowLayout.Compact },
        { 511.99, 336, FrameHeight, 511.99, 336, 511.99, 151, WindowLayout.Minimal },
        { 512, 335.99, FrameHeight, 512, 335.99, 512, 151, WindowLayout.Minimal },

        // Compact threshold, inclusive: client height 280 exactly, then just below it.
        { 800, 336, 56, 560, 336, 560, 336, WindowLayout.Compact },
        { 800, 336, 56.01, 560, 336, 560, 168.01, WindowLayout.Minimal },

        // A work area shorter than the minimal budget plus the frame: MinHeight is capped at H.
        { 683, 100, FrameHeight, 560, 100, 560, 100, WindowLayout.Minimal },

        // The layout is chosen from H − N, not from the 680-DIP opening height less N: a 350-DIP
        // non-client height on a 1000-DIP work area leaves 650 DIPs, so Regular applies, and its
        // minimum height exceeds the opening height while staying within the work area.
        { 800, 1000, 350, 560, 680, 560, 730, WindowLayout.Regular },
    };

    [Theory]
    [MemberData(nameof(WorkAreaCases))]
    public void Compute_WorkArea_ReturnsExpectedBoundsAndLayout(
        double width,
        double height,
        double nonClientHeight,
        double expectedInitialWidth,
        double expectedInitialHeight,
        double expectedMinWidth,
        double expectedMinHeight,
        WindowLayout expectedLayout)
    {
        WindowSizingResult result = WindowSizing.Compute(new Size(width, height), nonClientHeight);

        Assert.Equal(expectedInitialWidth, result.InitialWidth, Tolerance);
        Assert.Equal(expectedInitialHeight, result.InitialHeight, Tolerance);
        Assert.Equal(expectedMinWidth, result.MinWidth, Tolerance);
        Assert.Equal(expectedMinHeight, result.MinHeight, Tolerance);
        Assert.Equal(expectedLayout, result.Layout);

        // No bound may exceed the work area, whatever the layout.
        Assert.InRange(result.InitialWidth, 0.0, width);
        Assert.InRange(result.InitialHeight, 0.0, height);
        Assert.InRange(result.MinWidth, 0.0, width);
        Assert.InRange(result.MinHeight, 0.0, height);
    }

    [Theory]
    [InlineData(0.0, 336.0)]
    [InlineData(512.0, 0.0)]
    [InlineData(double.NaN, 336.0)]
    [InlineData(512.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 336.0)]
    [InlineData(512.0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NegativeInfinity)]
    public void Compute_InvalidWorkArea_ThrowsArgumentOutOfRange(double width, double height)
    {
        Size workArea = CreateWorkArea(width, height);

        Assert.Throws<ArgumentOutOfRangeException>(
            "workArea",
            () => WindowSizing.Compute(workArea, FrameHeight));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Compute_InvalidNonClientHeight_ThrowsArgumentOutOfRange(double invalidNonClientHeight)
    {
        Size workArea = new(1920, 1032);

        Assert.Throws<ArgumentOutOfRangeException>(
            "nonClientHeight",
            () => WindowSizing.Compute(workArea, invalidNonClientHeight));
    }

    /// <summary>
    /// Measures the fixed rows (the top area and the result header) of the never-shown main window in the regular and
    /// compact layouts, at the preferred and the narrowest supported width, with the longest texts in place: while
    /// running, the longest step name, the both-sensors notice and a full-length first-error message; after a user
    /// stop with errors, the ABORTED reason with its error count beside the badge as well.
    /// </summary>
    /// <param name="runState">The state the run is driven to: <see cref="RunState.Running"/> or <see cref="RunState.Aborted"/>.</param>
    /// <param name="layout">The layout forced on the window.</param>
    /// <param name="width">The width the root grid is measured at, in DIPs.</param>
    /// <returns>A task that completes when the measurement has been checked on the STA thread.</returns>
    [Theory]
    [InlineData(RunState.Running, WindowLayout.Regular, WindowSizing.PreferredWidth)]
    [InlineData(RunState.Running, WindowLayout.Regular, WindowSizing.MinimalWorkAreaWidth)]
    [InlineData(RunState.Running, WindowLayout.Compact, WindowSizing.PreferredWidth)]
    [InlineData(RunState.Running, WindowLayout.Compact, WindowSizing.MinimalWorkAreaWidth)]
    [InlineData(RunState.Aborted, WindowLayout.Regular, WindowSizing.PreferredWidth)]
    [InlineData(RunState.Aborted, WindowLayout.Regular, WindowSizing.MinimalWorkAreaWidth)]
    [InlineData(RunState.Aborted, WindowLayout.Compact, WindowSizing.PreferredWidth)]
    [InlineData(RunState.Aborted, WindowLayout.Compact, WindowSizing.MinimalWorkAreaWidth)]
    public async Task FixedRows_LongestTexts_StayWithinLayoutBudgetAsync(RunState runState, WindowLayout layout, double width)
    {
        await _fixture.RunAsync(async () =>
        {
            using LongestTextScene scene = LongestTextScene.Create();
            scene.RunTo(runState);

            FixedRows rows = await scene.MeasureAsync(layout, width);

            AssertWithinBudget(rows, layout, width);
            AssertRowsHoldTheirContent(scene.Window, rows);
            AssertLongestTexts(scene, runState);

            MainWindow window = scene.Window;
            MainViewModel viewModel = scene.ViewModel;
            foreach (string text in FixedTexts(viewModel, runState))
            {
                Assert.NotEmpty(LaidOutTextBlocks(FixedRowsOf(window), text));
            }

            if (layout == WindowLayout.Compact)
            {
                // The fixed rows show the notice and the first error on one line each, and the detail area opens
                // with their full text.
                foreach (string condensed in new[] { viewModel.NoticeText, viewModel.FirstErrorText })
                {
                    TextBlock fixedLine = Assert.Single(LaidOutTextBlocks(FixedRowsOf(window), condensed));
                    Assert.True(
                        fixedLine.ActualHeight < 2 * LineHeight(fixedLine),
                        FormattableString.Invariant($"The compact fixed rows show '{condensed}' on {fixedLine.ActualHeight:F1} DIPs, more than one line."));
                    Assert.Single(LaidOutTextBlocks(window.DetailScroller, condensed));
                }
            }
            else
            {
                Assert.Empty(LaidOutTextBlocks(window.DetailScroller, viewModel.NoticeText));
                Assert.Empty(LaidOutTextBlocks(window.DetailScroller, viewModel.FirstErrorText));
            }

            AssertDetailLineShownAtMinimumHeight(scene, layout, width);
        });
    }

    /// <summary>
    /// Measures the minimal fallback at the narrowest supported width with the longest texts: only the control row and
    /// the one-line status stay fixed, within the budget <see cref="WindowSizing.Compute"/> reserves for them, and the
    /// notice, the first error and the result open the scrolling detail area instead.
    /// </summary>
    /// <param name="runState">The state the run is driven to: <see cref="RunState.Running"/> or <see cref="RunState.Aborted"/>.</param>
    /// <returns>A task that completes when the measurement has been checked on the STA thread.</returns>
    [Theory]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Aborted)]
    public async Task FixedRows_MinimalLayoutAtNarrowestWidth_KeepOnlyControlRowAndStatusLineAsync(RunState runState)
    {
        await _fixture.RunAsync(async () =>
        {
            using LongestTextScene scene = LongestTextScene.Create();
            scene.RunTo(runState);

            FixedRows rows = await scene.MeasureAsync(WindowLayout.Minimal, WindowSizing.MinimalWorkAreaWidth);

            AssertWithinBudget(rows, WindowLayout.Minimal, WindowSizing.MinimalWorkAreaWidth);
            AssertRowsHoldTheirContent(scene.Window, rows);
            AssertLongestTexts(scene, runState);

            MainWindow window = scene.Window;
            MainViewModel viewModel = scene.ViewModel;
            Assert.Equal(Visibility.Collapsed, window.ResultHeader.Visibility);
            Assert.Single(LaidOutTextBlocks(window.FixedArea, viewModel.StatusLineText));
            Assert.Equal(Visibility.Visible, window.DetailScroller.Visibility);
            Assert.True(window.DetailScroller.ActualHeight > 0, "The minimal layout's detail area was not laid out.");

            // While running the detail area also holds the live readings with the step; after the stop, the result.
            var detailTexts = new List<string>
            {
                viewModel.NoticeText,
                viewModel.FirstErrorText,
                runState == RunState.Running ? viewModel.CurrentStepText : viewModel.ResultReasonText,
            };

            foreach (string text in detailTexts)
            {
                Assert.Empty(LaidOutTextBlocks(FixedRowsOf(window), text));
                Assert.NotEmpty(LaidOutTextBlocks(window.DetailScroller, text));
            }

            AssertDetailLineShownAtMinimumHeight(scene, WindowLayout.Minimal, WindowSizing.MinimalWorkAreaWidth);
        });
    }

    /// <summary>
    /// Walks every element the window declares, its data templates included: the only input controls are the test
    /// type selector, the Stop on error checkbox and the Start/Stop toggle, each named for assistive technology and
    /// reached by Tab in that order.
    /// </summary>
    /// <returns>A task that completes when the walk has been checked on the STA thread.</returns>
    [Fact]
    public async Task InputControls_MainWindow_AreExactlyTheThreeAccessibleTabStopsAsync()
    {
        await _fixture.RunAsync(() =>
        {
            using LongestTextScene scene = LongestTextScene.Create();
            MainWindow window = scene.Window;
            Control[] expected = [window.TestTypeSelector, window.StopOnErrorCheckBox, window.StartStopButton];

            IReadOnlyList<Control> found = InputControlsIn(window);

            Assert.Equal(expected.Select(Describe), found.OrderBy(control => control.TabIndex).Select(Describe));
            Assert.Equal(expected, found.OrderBy(control => control.TabIndex));
            Assert.Equal(new[] { 0, 1, 2 }, expected.Select(control => control.TabIndex));
            foreach (Control control in expected)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)),
                    $"{Describe(control)} has no automation name.");
                Assert.True(control.Focusable, $"{Describe(control)} cannot take keyboard focus.");
                Assert.True(control.IsTabStop, $"{Describe(control)} is not a tab stop.");
            }
        });
    }

    /// <summary>
    /// Follows one run through the window: the single toggle binds Start while idle, Stop while the run is in progress,
    /// and Start again once the stopped run has released its memory and published its summaries, with matching label,
    /// automation name and enablement.
    /// </summary>
    /// <returns>A task that completes when the run has been checked on the STA thread.</returns>
    [Fact]
    public async Task StartStopButton_IdleRunningThenPublished_BindsStartThenStopThenStartAsync()
    {
        await _fixture.RunAsync(async () =>
        {
            using LongestTextScene scene = LongestTextScene.Create();
            MainViewModel viewModel = scene.ViewModel;
            Button toggle = scene.Window.StartStopButton;

            await SettleAsync();
            Assert.Equal(RunState.Idle, viewModel.State);
            AssertToggle(toggle, viewModel.StartCommand, UiText.StartLabel, UiText.StartAutomationName);

            scene.RunTo(RunState.Running);
            await SettleAsync();
            Assert.True(viewModel.IsRunInProgress);
            AssertToggle(toggle, viewModel.StopCommand, UiText.StopLabel, UiText.StopAutomationName);

            scene.RunTo(RunState.Aborted);
            await SettleAsync();
            Assert.False(viewModel.IsRunInProgress);
            Assert.True(viewModel.CanStart);
            AssertToggle(toggle, viewModel.StartCommand, UiText.StartLabel, UiText.StartAutomationName);
        });
    }

    /// <summary>
    /// Raises DPI changes on the window, each a scale factor relative to the window's own DPI, before any of the
    /// sizing work they defer runs, and checks that the window ends sized for the last DPI alone: its user-chosen
    /// size shrunk only to what that DPI's work area can show, and its minimum size and layout computed for it.
    /// </summary>
    /// <param name="scaleFactors">The DPI of each change, as a multiple of the window's own DPI, in order.</param>
    [Theory]
    // A to B, then back to A before the work runs: B's DPI must not be paired with A's work area, which would
    // shrink the window to half its work area for good.
    [InlineData(2.0, 1.0)]
    // A single change to B is still honoured: the window shrinks to B's work area.
    [InlineData(2.0)]
    public void DpiChanged_RaisedBeforeDeferredSizingRuns_SizesForLatestDpi(params double[] scaleFactors) =>
        RunOnStaThread(() =>
        {
            var window = new Window { ShowInTaskbar = false, ShowActivated = false };
            WorkAreaSizing.SetIsEnabled(window, true);
            try
            {
                nint hwnd = new WindowInteropHelper(window).EnsureHandle();
                DrainDispatcher();

                // The window is never shown, so Loaded is raised by hand to put the behavior in its post-load state,
                // where a recompute may only shrink a size the user chose.
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));

                MonitorWorkArea monitorWorkArea = Assert.NotNull(Win32Display.GetWorkArea(hwnd));
                DpiScale windowDpi = VisualTreeHelper.GetDpi(window);
                Size workArea = ToDips(monitorWorkArea, windowDpi);

                // A window without content gives Loaded nothing to measure, so the behavior keeps its estimate of
                // the non-client height: the caption and frame thickness the system reports.
                Thickness frame = SystemParameters.WindowNonClientFrameThickness;
                double nonClientHeight = frame.Top + frame.Bottom;
                Assert.Equal(WindowSizing.Compute(workArea, nonClientHeight).MinHeight, window.MinHeight, Tolerance);

                double userWidth = Math.Max(workArea.Width * UserSizeFraction, window.MinWidth);
                double userHeight = Math.Max(workArea.Height * UserSizeFraction, window.MinHeight);
                window.Width = userWidth;
                window.Height = userHeight;

                DpiScale currentDpi = windowDpi;
                foreach (double scaleFactor in scaleFactors)
                {
                    var newDpi = new DpiScale(windowDpi.DpiScaleX * scaleFactor, windowDpi.DpiScaleY * scaleFactor);
                    window.RaiseEvent(CreateDpiChangedEventArgs(currentDpi, newDpi, window));
                    currentDpi = newDpi;
                }

                DrainDispatcher();

                Size finalWorkArea = ToDips(monitorWorkArea, currentDpi);
                WindowSizingResult expected = WindowSizing.Compute(finalWorkArea, nonClientHeight);
                Assert.Equal(Math.Min(userWidth, finalWorkArea.Width), window.Width, Tolerance);
                Assert.Equal(Math.Min(userHeight, finalWorkArea.Height), window.Height, Tolerance);
                Assert.Equal(expected.MinWidth, window.MinWidth, Tolerance);
                Assert.Equal(expected.MinHeight, window.MinHeight, Tolerance);
                Assert.Equal(expected.Layout, WorkAreaSizing.GetLayout(window));
            }
            finally
            {
                window.Close();
            }
        });

    // WPF's Size constructor rejects negative dimensions, so the only negative work area that can
    // reach Compute is Size.Empty, whose width and height are both negative infinity.
    private static Size CreateWorkArea(double width, double height) =>
        double.IsNegativeInfinity(width) && double.IsNegativeInfinity(height)
            ? Size.Empty
            : new Size(width, height);

    // xUnit v3 runs tests on thread-pool threads, but a WPF window needs an STA thread with its own dispatcher.
    private static void RunOnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(_staThreadTimeout), "The STA test thread did not finish in time.");
        failure?.Throw();
    }

    // Runs every queued operation down to ContextIdle, which includes the behavior's Loaded-priority sizing work.
    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    // WPF raises DpiChanged only from a real WM_DPICHANGED and exposes no public constructor for its arguments.
    // Failing to find the constructor fails the test, so a WPF change cannot silently skip the scenario.
    private static DpiChangedEventArgs CreateDpiChangedEventArgs(DpiScale oldDpi, DpiScale newDpi, Window source)
    {
        ConstructorInfo? constructor = typeof(DpiChangedEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(DpiScale), typeof(DpiScale), typeof(RoutedEvent), typeof(object)]);
        Assert.NotNull(constructor);
        return (DpiChangedEventArgs)constructor.Invoke([oldDpi, newDpi, Window.DpiChangedEvent, source]);
    }

    private static Size ToDips(MonitorWorkArea area, DpiScale dpi) =>
        new(area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);

    // Runs the bindings, triggers and template work that earlier changes queued on the fixture's dispatcher.
    private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ContextIdle);

    // The measured height is written to the test output, so passing runs record how much of each budget is used.
    private static void AssertWithinBudget(FixedRows rows, WindowLayout layout, double width)
    {
        double budget = WindowSizing.FixedRowsBudget(layout);
        string measurement = FormattableString.Invariant(
            $"{layout} fixed rows at {width} DIPs: {rows.Height:F1} DIPs (top area {rows.TopArea:F1}, result header {rows.ResultHeader:F1}); budget {budget} DIPs.");
        TestContext.Current.TestOutputHelper?.WriteLine(measurement);
        Assert.True(rows.Height <= budget, measurement);
    }

    // At the window's minimum height its client area is the layout's budget plus 40 DIPs of detail, which must still
    // show the first detail line once the root margin and the separator above the detail area are taken out too.
    // Runs last in a test: it lays the grid out again at that height.
    private static void AssertDetailLineShownAtMinimumHeight(LongestTextScene scene, WindowLayout layout, double width)
    {
        double clientHeight = WindowSizing.FixedRowsBudget(layout) + WindowSizing.MinimumDetailHeight;
        double detailHeight = scene.DetailHeightAt(width, clientHeight);
        double lineHeight = LineHeight(scene.Window.DetailScroller);
        string measurement = FormattableString.Invariant(
            $"{layout} detail area at the minimum client height of {clientHeight} DIPs: {detailHeight:F1} DIPs; one line {lineHeight:F1} DIPs.");
        TestContext.Current.TestOutputHelper?.WriteLine(measurement);
        Assert.True(detailHeight >= lineHeight, measurement);
    }

    // The fixed rows are Auto-sized, so each must be exactly as tall as its element: a row with a fixed height, or one
    // that clipped its content, would make the budget check meaningless.
    private static void AssertRowsHoldTheirContent(MainWindow window, FixedRows rows)
    {
        Assert.Equal(SlotHeight(window.FixedArea), rows.TopArea, LayoutTolerance);
        Assert.Equal(SlotHeight(window.ResultHeader), rows.ResultHeader, LayoutTolerance);
    }

    // The budget is measured with the longest texts only if the view model really carries them.
    private static void AssertLongestTexts(LongestTextScene scene, RunState runState)
    {
        MainViewModel viewModel = scene.ViewModel;
        Assert.Equal(runState, viewModel.State);
        Assert.Equal(UiText.SensorsUnavailableNotice, viewModel.NoticeText);
        Assert.True(viewModel.HasFirstError);
        Assert.Equal(DisplayFormat.FirstError(scene.FirstError, LongestErrorTotal), viewModel.FirstErrorText);
        if (runState == RunState.Running)
        {
            Assert.Equal(DisplayFormat.StepProgress(scene.Step.Name, scene.StepIndex, scene.StepCount), viewModel.CurrentStepText);
        }
        else
        {
            Assert.True(viewModel.HasResult);
            Assert.True(viewModel.HasCauseHints);
            Assert.Equal(
                UiText.StoppedByUser + UiText.NoticeSeparator + UiText.ErrorsBeforeStop(LongestErrorTotal),
                viewModel.ResultReasonText);
        }
    }

    // The texts the regular and compact fixed rows must show; minimal moves them to the detail area.
    private static IEnumerable<string> FixedTexts(MainViewModel viewModel, RunState runState) =>
        runState == RunState.Running
            ? [viewModel.NoticeText, viewModel.FirstErrorText, viewModel.CurrentStepText]
            : [viewModel.NoticeText, viewModel.FirstErrorText, viewModel.ResultReasonText, viewModel.VerdictText];

    private static void AssertToggle(Button toggle, ICommand command, string label, string automationName)
    {
        Assert.Same(command, toggle.Command);
        Assert.Equal<object>(label, toggle.Content);
        Assert.Equal(automationName, AutomationProperties.GetName(toggle));
        Assert.True(command.CanExecute(null), $"'{label}' cannot execute.");
        Assert.True(toggle.IsEnabled, $"The '{label}' toggle is disabled.");
    }

    private static FrameworkElement[] FixedRowsOf(MainWindow window) => [window.FixedArea, window.ResultHeader];

    private static double SlotHeight(FrameworkElement element) =>
        element.Visibility == Visibility.Collapsed ? 0 : element.ActualHeight + element.Margin.Top + element.Margin.Bottom;

    // One unwrapped line of the font an element's text inherits; a wrapped text is at least twice as tall.
    private static double LineHeight(DependencyObject element) =>
        TextElement.GetFontFamily(element).LineSpacing * TextElement.GetFontSize(element);

    private static IReadOnlyList<TextBlock> LaidOutTextBlocks(IEnumerable<FrameworkElement> roots, string text) =>
        roots.SelectMany(root => LaidOutTextBlocks(root, text)).ToArray();

    // The text blocks under a root that show exactly the text in the measured layout.
    private static IReadOnlyList<TextBlock> LaidOutTextBlocks(DependencyObject root, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return VisualDescendants(root)
            .OfType<TextBlock>()
            .Where(textBlock => string.Equals(textBlock.Text, text, StringComparison.Ordinal) && IsLaidOut(textBlock, root))
            .ToArray();
    }

    // Shown in the measured layout: arranged with a size, and neither the element nor any ancestor up to the root
    // collapsed or hidden. A window that is never shown has no presentation source, so IsVisible cannot tell.
    private static bool IsLaidOut(FrameworkElement element, DependencyObject root)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out DependencyObject? current))
        {
            yield return current;
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    // The logical tree holds every element the window declares, collapsed ones included, but not the content of its
    // data templates, so the template each content or items control presents is loaded and walked as well.
    private static IReadOnlyList<Control> InputControlsIn(Window window)
    {
        var found = new List<Control>();
        var pending = new Stack<DependencyObject>();
        pending.Push(window);
        while (pending.TryPop(out DependencyObject? current))
        {
            if (current is ButtonBase or Selector or TextBoxBase or RangeBase or PasswordBox)
            {
                found.Add((Control)current);
            }

            DataTemplate? template = current switch
            {
                ContentControl contentControl => contentControl.ContentTemplate,
                ItemsControl itemsControl => itemsControl.ItemTemplate,
                _ => null,
            };
            if (template?.LoadContent() is { } templateContent)
            {
                pending.Push(templateContent);
            }

            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject element)
                {
                    pending.Push(element);
                }
            }
        }

        return found;
    }

    private static string Describe(Control control) => control.GetType().Name + " '" + control.Name + "'";

    /// <summary>The heights of the two fixed rows of the main window's root grid, in DIPs.</summary>
    /// <param name="TopArea">Row 0: the control row, live readings, memory summary and notice.</param>
    /// <param name="ResultHeader">Row 1: the verdict badge, reason and first-error message.</param>
    private readonly record struct FixedRows(double TopArea, double ResultHeader)
    {
        public double Height => TopArea + ResultHeader;
    }

    /// <summary>
    /// A never-shown <see cref="MainWindow"/> bound to a real <see cref="MainViewModel"/> over a
    /// <see cref="ControllerHarness"/>: elevated, AVX2, both temperature sensors without a driver, so the notice is the
    /// both-sensors notice alone, and every Start running the plan that holds the longest step name. Created, driven,
    /// measured and disposed on the fixture's STA thread only.
    /// </summary>
    private sealed class LongestTextScene : IDisposable
    {
        private readonly TestPlan _plan;
        private FakeEngineSession? _session;
        private ErrorRecord? _firstError;
        private bool _disposed;

        private LongestTextScene(ControllerHarness harness, TestPlan plan, int stepIndex)
        {
            Harness = harness;
            _plan = plan;
            StepIndex = stepIndex;
            ViewModel = new MainViewModel(harness.Controller, harness.StateMachine);
            Window = new MainWindow { DataContext = ViewModel };
        }

        public ControllerHarness Harness { get; }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public int StepIndex { get; }

        public int StepCount => _plan.Steps.Count;

        public StepDefinition Step => _plan.Steps[StepIndex];

        public ErrorRecord FirstError =>
            _firstError ?? throw new InvalidOperationException("The scene has not reported an error yet.");

        // Idle, with the first sensor snapshot delivered and launch initialisation finished, so Start is offered.
        public static LongestTextScene Create()
        {
            (TestPlan plan, int stepIndex) = LongestStep();
            var harness = new ControllerHarness();
            LongestTextScene? scene = null;
            try
            {
                harness.Sensors.SetCpu(SensorStatus.DriverUnavailable);
                harness.Sensors.SetRam(SensorStatus.DriverUnavailable);
                harness.UsePlan(plan);
                scene = new LongestTextScene(harness, plan, stepIndex);
                harness.StartSensors();
                harness.Initialize();
                Assert.Equal(InstructionSet.Avx2, harness.Controller.System?.BestIsa);
                return scene;
            }
            catch
            {
                if (scene is null)
                {
                    harness.Dispose();
                }
                else
                {
                    scene.Dispose();
                }

                throw;
            }
        }

        // Running: in the longest-named step, with Stop on error off and an error total far beyond the one stored
        // record. Aborted: then stopped by the user, its memory released and its summaries published.
        public void RunTo(RunState state)
        {
            switch (state)
            {
                case RunState.Running:
                    StartWithLongestTexts();
                    break;

                case RunState.Aborted:
                    if (_session is null)
                    {
                        StartWithLongestTexts();
                    }

                    StopByUserAndPublish();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, "A scene runs to Running or Aborted only.");
            }
        }

        // Forces the layout on the window and lays out its root grid at the width with unbounded height, so the Auto
        // rows take exactly what their content needs.
        public async Task<FixedRows> MeasureAsync(WindowLayout layout, double width)
        {
            // The harness installs its queued context only while it drives the controller; layout runs on the
            // dispatcher's own.
            Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);

            WorkAreaSizing.SetLayout(Window, layout);
            Assert.Equal(layout, WorkAreaSizing.GetLayout(Window.RootGrid));

            // Template content, the bindings that find the window from it and the layout triggers they feed settle in
            // dispatcher work the first pass queues, so the queue is drained and the grid laid out again.
            LayOut(width);
            await SettleAsync();
            LayOut(width);

            RowDefinitionCollection rowDefinitions = Window.RootGrid.RowDefinitions;
            return new FixedRows(rowDefinitions[0].ActualHeight, rowDefinitions[1].ActualHeight);
        }

        // Lays the root grid out in a client area of the given size, as a window of that client size would, and
        // returns the height of the detail area's viewport.
        public double DetailHeightAt(double width, double clientHeight)
        {
            Grid rootGrid = Window.RootGrid;
            rootGrid.Measure(new Size(width, clientHeight));
            rootGrid.Arrange(new Rect(0, 0, width, clientHeight));
            rootGrid.UpdateLayout();
            return Window.DetailScroller.ActualHeight;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ViewModel.Dispose();

            // A window that was never shown only raises Closing and Closed, and leaves the application's windows.
            Window.Close();
            Harness.Dispose();
        }

        // The longest step name of every plan by UTF-8 size, run in the plan with the most steps that holds it, so the
        // current step also shows the widest "n of N".
        private static (TestPlan Plan, int StepIndex) LongestStep()
        {
            StepDefinition longest = TestPlans.All
                .SelectMany(plan => plan.Steps)
                .MaxBy(step => Encoding.UTF8.GetByteCount(step.Name))!;
            Assert.InRange(Encoding.UTF8.GetByteCount(longest.Name), 1, MaxStepNameBytes);

            TestPlan plan = TestPlans.All.Where(candidate => candidate.Steps.Contains(longest)).MaxBy(candidate => candidate.Steps.Count)!;
            int stepIndex = plan.Steps
                .Select((step, index) => (step, index))
                .First(entry => ReferenceEquals(entry.step, longest))
                .index;
            return (plan, stepIndex);
        }

        // The longest first-error message: the widest address and words, the widest time into the test, and the
        // re-read outcome whose description is longest.
        private static ErrorRecord WidestFirstError(int stepIndex, string stepName)
        {
            RereadOutcome outcome = Enum.GetValues<RereadOutcome>().MaxBy(candidate => RereadOutcomeText.Describe(candidate).Length);
            return FakeEngineData.Error(
                sequence: 1,
                wordAddress: WidestAddress,
                bit: 0,
                stepIndex: stepIndex,
                stepName: stepName,
                rereadOutcome: outcome,
                timeIntoTest: _widestRunTime,
                expected: WidestWord,
                regionOffset: 0);
        }

        private void StartWithLongestTexts()
        {
            FakeEngineSession session = Harness.StartAccepted(stopOnError: false);
            _session = session;
            Harness.Prepare();
            session.RaiseStepStarted(StepIndex);

            ErrorRecord firstError = WidestFirstError(StepIndex, Step.Name);
            session.RaiseErrors([firstError], LongestErrorTotal);
            ulong regionBytes = session.Options.RegionBytes;
            RunProgress progress = session.PublishProgress(FakeEngineData.Progress(
                StepIndex,
                StepCount,
                Step.Name,
                elapsed: _widestRunTime,
                remaining: _widestRunTime,
                verifiedBytes: checked(regionBytes * WidestCoverageMultiple),
                regionBytes: regionBytes,
                totalErrors: LongestErrorTotal));
            Harness.RunUntil(() => ReferenceEquals(Harness.Controller.CurrentProgress, progress)
                && firstError.Equals(Harness.Controller.FirstError)
                && Harness.Controller.TotalErrors == LongestErrorTotal);
            _firstError = firstError;
        }

        private void StopByUserAndPublish()
        {
            FakeEngineSession session = _session ?? throw new InvalidOperationException("The scene has not started a run.");
            Harness.Controller.RequestStop();
            session.RaiseStepFinished(StepIndex, errors: LongestErrorTotal, completed: false);
            session.CompleteWithFirstRecordedReason(totalErrors: LongestErrorTotal);
            Harness.AwaitVerdict();
            Harness.Release(_releaseTime);
        }

        private void LayOut(double width)
        {
            Grid rootGrid = Window.RootGrid;
            rootGrid.Measure(new Size(width, double.PositiveInfinity));
            rootGrid.Arrange(new Rect(0, 0, width, rootGrid.DesiredSize.Height));
            rootGrid.UpdateLayout();
        }
    }
}

/// <summary>
/// One STA thread running a WPF dispatcher, on which <see cref="MainWindowLayoutTests"/> builds, drives and measures
/// every <see cref="MainWindow"/>, with the application's <c>App.xaml</c> resources loaded for it to resolve.
/// </summary>
/// <remarks>
/// xUnit runs tests on thread-pool threads, but WPF elements need an STA thread whose dispatcher runs the binding,
/// template and layout work they queue. Work posted through <see cref="RunAsync(Action)"/> and
/// <see cref="RunAsync(Func{Task})"/> is awaited, never blocked on, and bounded in time, so a dispatcher that stops
/// draining fails the test instead of hanging the run.
/// </remarks>
public sealed class WpfStaFixture : IDisposable
{
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _operationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private ExceptionDispatchInfo? _unhandledException;
    private bool _disposed;

    /// <summary>Loads the application resources, starts the STA thread and waits until its dispatcher runs.</summary>
    /// <exception cref="TimeoutException">A thread did not start within 30 seconds.</exception>
    public WpfStaFixture()
    {
        EnsureApplicationResources();

        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = StartStaThread(() => RunDispatcher(started), nameof(WpfStaFixture));
        if (Task.WaitAny([started.Task], _startupTimeout) < 0)
        {
            throw new TimeoutException("The WPF STA thread did not start within " + _startupTimeout + ".");
        }

        _dispatcher = started.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="body"/> on the STA thread.</summary>
    /// <param name="body">The work, which may create, drive and measure WPF elements.</param>
    /// <returns>A task that completes when the work has run, faulting with its exception or with a dispatcher fault.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The fixture has been disposed.</exception>
    public Task RunAsync(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CompleteAsync(_dispatcher.InvokeAsync(body).Task);
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the STA thread. Its awaits resume there, so it can yield to the dispatcher to
    /// let queued binding and layout work run.
    /// </summary>
    /// <param name="body">The asynchronous work, which may create, drive and measure WPF elements.</param>
    /// <returns>A task that completes when the work's task does, faulting with its exception or with a dispatcher fault.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The fixture has been disposed.</exception>
    public Task RunAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CompleteAsync(_dispatcher.InvokeAsync(body).Task.Unwrap());
    }

    /// <summary>Stops the dispatcher and waits up to ten seconds for the STA thread to end.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Queued rather than invoked: an invoke from this thread would block for good on a dispatcher that hangs. The
        // thread is a background thread, so one that does not end in time cannot keep the test process alive.
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join(_shutdownTimeout);
    }

    // Only one WPF Application may exist per AppDomain, so it is created only when none exists. It is the real App,
    // because App.xaml can be loaded only into an instance of its own class, and it is created on a thread of its own
    // whose dispatcher never runs: the Application constructor queues OnStartup for its dispatcher, and App's override
    // starts RAMQuick for real (platform check, session-wide single-instance mutex, data folder, engine binding and a
    // shown window), none of which a test process may do. Its resources are shared with the fixture's thread as
    // WPF shares application resources with every UI thread: sealed, and looked up under a lock.
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }

        ExceptionDispatchInfo? failure = null;
        Thread loader = StartStaThread(
            () =>
            {
                try
                {
                    new App().InitializeComponent();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            nameof(WpfStaFixture) + " application resources");
        if (!loader.Join(_startupTimeout))
        {
            throw new TimeoutException("The application resources did not load within " + _startupTimeout + ".");
        }

        failure?.Throw();
    }

    private static Thread StartStaThread(ThreadStart body, string name)
    {
        var thread = new Thread(body)
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private void RunDispatcher(TaskCompletionSource<Dispatcher> started)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnUnhandledException;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        started.SetResult(dispatcher);
        Dispatcher.Run();
    }

    // Faults the test whose work is in flight rather than letting an exception from queued dispatcher work end the
    // STA thread, and with it the test process.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Interlocked.CompareExchange(ref _unhandledException, ExceptionDispatchInfo.Capture(e.Exception), null);
        e.Handled = true;
    }

    private async Task CompleteAsync(Task work)
    {
        await work.WaitAsync(_operationTimeout, TestContext.Current.CancellationToken);
        Interlocked.Exchange(ref _unhandledException, null)?.Throw();
    }
}
