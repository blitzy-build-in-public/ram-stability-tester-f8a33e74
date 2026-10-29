using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RamQuick.App.Behaviors;
using RamQuick.Interop;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="WindowSizing.Compute"/>: the window bounds and active layout for the worked
/// work areas, the layout and supported-range thresholds, and the argument guards. Also tests
/// <see cref="WorkAreaSizing"/> on a loaded window with a real handle: DPI changes that arrive before
/// the sizing work they defer has run.
/// </summary>
public sealed class MainWindowLayoutTests
{
    // Screen cases assume a 48-DIP taskbar and a 39-DIP caption and frame, as the worked cases do.
    private const double TaskbarHeight = 48;
    private const double FrameHeight = 39;
    private const double Tolerance = 1e-6;

    // The user's size as a fraction of the work area at the window's own DPI: more than half of it, so the same
    // pixels converted at twice that DPI cannot hold it, yet within it, so the window's own DPI never shrinks it.
    // It is raised to the window's minimum size where that is larger, since a smaller size is enlarged to it anyway.
    private const double UserSizeFraction = 0.75;

    // A WPF dispatcher that never drains would otherwise hang the test run instead of failing it.
    private static readonly TimeSpan _staThreadTimeout = TimeSpan.FromMinutes(1);

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
}
