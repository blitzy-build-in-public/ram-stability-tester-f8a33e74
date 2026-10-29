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
/// Tests for <see cref="WorkAreaSizing"/> on a loaded window with a real handle: DPI changes that arrive before
/// the sizing work they defer has run.
/// </summary>
public sealed class WorkAreaSizingTests
{
    private const double Tolerance = 1e-6;

    // The user's size as a fraction of the work area at the window's own DPI: more than half of it, so the same
    // pixels converted at twice that DPI cannot hold it, yet within it, so the window's own DPI never shrinks it.
    // It is raised to the window's minimum size where that is larger, since a smaller size is enlarged to it anyway.
    private const double UserSizeFraction = 0.75;

    // A WPF dispatcher that never drains would otherwise hang the test run instead of failing it.
    private static readonly TimeSpan _staThreadTimeout = TimeSpan.FromMinutes(1);

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
