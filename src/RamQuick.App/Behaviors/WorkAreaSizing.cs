using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RamQuick.Interop;

namespace RamQuick.App.Behaviors;

/// <summary>
/// Attached behavior that sizes a <see cref="Window"/> from the work area of the monitor that holds it, and
/// publishes the active <see cref="WindowLayout"/> of the window's fixed rows through <see cref="LayoutProperty"/>.
/// </summary>
/// <remarks>
/// <para>
/// XAML usage: declare <c>xmlns:behaviors="clr-namespace:RamQuick.App.Behaviors"</c> and set
/// <c>behaviors:WorkAreaSizing.IsEnabled="True"</c> on the <c>Window</c>. The window must keep
/// <c>SizeToContent="Manual"</c> (the default), because the behavior sets <see cref="FrameworkElement.Width"/> and
/// <see cref="FrameworkElement.Height"/>, and should open with <c>WindowStartupLocation="CenterScreen"</c> or an
/// explicit position, so that the initial size is placed inside the work area.
/// </para>
/// <para>
/// Bounds and the layout come from <see cref="WindowSizing.Compute"/>. They are recomputed when the window's handle
/// is created (<see cref="Window.SourceInitialized"/>), once the real non-client height can be measured
/// (<see cref="FrameworkElement.Loaded"/>), after a DPI change (<see cref="Window.DpiChanged"/>), and when
/// <see cref="Window.LocationChanged"/> finds the window on another monitor. The work area is always that of the
/// window's own monitor, queried through <see cref="Win32Display.GetWorkArea"/>, never
/// <c>SystemParameters.WorkArea</c>, which describes only the primary monitor.
/// </para>
/// <para>
/// The initial size is applied only until the window has loaded. Later recomputes never enlarge or reset a size the
/// user chose; they only shrink the window to fit a smaller work area. A maximized or minimized window keeps its
/// size; only its minimum size and the layout follow the work area.
/// </para>
/// <para>
/// The behavior is UI-thread only. A window that is never shown has no handle, so the behavior changes nothing on
/// it: its layout stays at the default, or at whatever <see cref="SetLayout"/> sets, which is how the layout tests
/// measure the main window.
/// </para>
/// </remarks>
public static class WorkAreaSizing
{
    /// <summary>
    /// Identifies the <c>IsEnabled</c> attached property. Setting it to <see langword="true"/> on a
    /// <see cref="Window"/> attaches the behavior; setting it back to <see langword="false"/> detaches it. On any
    /// other element it has no effect.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(WorkAreaSizing),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Identifies the <c>Layout</c> attached property: the <see cref="WindowLayout"/> the behavior selected for the
    /// window's current work area. The default is <see cref="WindowLayout.Regular"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value inherits down the element tree, so the window's content can switch on it in either form:
    /// </para>
    /// <code language="xml">
    /// &lt;DataTrigger Binding="{Binding Path=(behaviors:WorkAreaSizing.Layout),
    ///                       RelativeSource={RelativeSource AncestorType=Window}}"
    ///              Value="Compact"&gt;
    ///
    /// &lt;Trigger Property="behaviors:WorkAreaSizing.Layout" Value="Compact"&gt;
    /// </code>
    /// <para>
    /// The property is settable, so a test can force a layout on a window that is never shown before measuring it.
    /// A change invalidates measure, so the fixed rows re-measure in the new layout.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout",
        typeof(WindowLayout),
        typeof(WorkAreaSizing),
        new FrameworkPropertyMetadata(
            WindowLayout.Regular,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Per-window state lives on the window itself rather than in static fields, so two windows, or two tests,
    // never share it, and it is collected with its window.
    private static readonly DependencyProperty _sizingStateProperty = DependencyProperty.RegisterAttached(
        "SizingState",
        typeof(SizingState),
        typeof(WorkAreaSizing),
        new PropertyMetadata(null));

    // Loaded deliberately keeps the estimate when the measured non-client height is within one DIP of it: that gap is
    // rounding or theme noise, not worth a second pass that re-applies the initial and minimum sizes. It is no
    // guarantee of an identical result: later recomputes keep the estimate too, near the exact 380/280-DIP thresholds
    // it can select another layout than the measured height would, and one DIP can be two physical pixels at 200 %.
    private const double NonClientHeightTolerance = 1;

    /// <summary>Gets whether the behavior is attached to an element.</summary>
    /// <param name="element">The element, normally a <see cref="Window"/>.</param>
    /// <returns>The value of the <c>IsEnabled</c> attached property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    [AttachedPropertyBrowsableForType(typeof(Window))]
    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Attaches the behavior to, or detaches it from, an element.</summary>
    /// <param name="element">The element; only a <see cref="Window"/> is sized.</param>
    /// <param name="value"><see langword="true"/> to attach the behavior; <see langword="false"/> to detach it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    /// <summary>Gets the layout in effect for an element, inherited from its window.</summary>
    /// <param name="element">The window or any element inside it.</param>
    /// <returns>The value of the <c>Layout</c> attached property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    public static WindowLayout GetLayout(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (WindowLayout)element.GetValue(LayoutProperty);
    }

    /// <summary>
    /// Sets the layout on an element. The behavior sets it on its window at every recompute; a test sets it to force
    /// a layout on a window that is never shown.
    /// </summary>
    /// <param name="element">The element, normally a <see cref="Window"/>; its descendants inherit the value.</param>
    /// <param name="value">The layout to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is <see langword="null"/>.</exception>
    public static void SetLayout(DependencyObject element, WindowLayout value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LayoutProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        // Only a window has a work area to fit. Any other target is ignored rather than rejected, so the XAML
        // designer and other hosts that apply the property elsewhere keep working.
        if (element is not Window window)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            Attach(window);
        }
        else
        {
            Detach(window);
        }
    }

    private static void Attach(Window window)
    {
        if (GetState(window) is not null)
        {
            return;
        }

        var state = new SizingState();
        window.SetValue(_sizingStateProperty, state);

        // Static handlers capture nothing, so these subscriptions keep no object alive but the window itself.
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.DpiChanged += OnDpiChanged;
        window.LocationChanged += OnLocationChanged;
        window.Closed += OnClosed;

        // Enabled from code on a window that already has its handle: SourceInitialized has passed, so the work area
        // is applied now, and a window that has loaded keeps the size it has.
        if (new WindowInteropHelper(window).Handle != 0)
        {
            state.IsLoaded = window.IsLoaded;
            state.NonClientHeight = state.IsLoaded && TryMeasureNonClientHeight(window, out double measured)
                ? measured
                : EstimateNonClientHeight();
            Recompute(window, state, VisualTreeHelper.GetDpi(window), applyInitialSize: !state.IsLoaded);
        }
    }

    // Idempotent: Closed detaches, and switching IsEnabled off afterwards detaches again harmlessly.
    private static void Detach(Window window)
    {
        window.SourceInitialized -= OnSourceInitialized;
        window.Loaded -= OnLoaded;
        window.DpiChanged -= OnDpiChanged;
        window.LocationChanged -= OnLocationChanged;
        window.Closed -= OnClosed;
        window.ClearValue(_sizingStateProperty);
    }

    private static SizingState? GetState(Window window) => window.GetValue(_sizingStateProperty) as SizingState;

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window || GetState(window) is not { } state)
        {
            return;
        }

        // The handle exists but the first layout pass has not run, so the non-client height can only be estimated
        // here. Sizing now, before WPF places the window, lets the startup location use the computed size.
        state.NonClientHeight = EstimateNonClientHeight();
        Recompute(window, state, VisualTreeHelper.GetDpi(window), applyInitialSize: true);
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Window window || GetState(window) is not { } state || state.IsLoaded)
        {
            return;
        }

        // Loaded follows the first layout pass, so the real non-client height replaces the estimate. The user cannot
        // have resized the window yet, so the initial size may still be applied; the same holds when the first
        // attempt found no work area.
        bool measured = TryMeasureNonClientHeight(window, out double nonClientHeight);
        if (measured && Math.Abs(nonClientHeight - state.NonClientHeight) > NonClientHeightTolerance)
        {
            state.NonClientHeight = nonClientHeight;
            Recompute(window, state, VisualTreeHelper.GetDpi(window), applyInitialSize: true);
        }
        else if (!state.HasApplied)
        {
            Recompute(window, state, VisualTreeHelper.GetDpi(window), applyInitialSize: true);
        }

        state.IsLoaded = true;
    }

    private static void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (sender is not Window window || GetState(window) is not { } state)
        {
            return;
        }

        // WPF applies the rectangle Windows suggests for the new DPI after raising this event, so a shrink-to-fit
        // run now would judge a size that is about to change. The work runs once that rectangle is in place, at
        // the DPI the event reports, which is the one the rectangle was computed for.
        //
        // Only the latest change's DPI may be used. The work area is queried when the work runs, so an earlier
        // change's DPI would be paired with the monitor the window has since moved to and give a false bound; once
        // the window has loaded, a size shrunk to that bound is never enlarged again. Each change therefore replaces
        // the pending values, and the flag coalesces the changes that arrive before the work runs.
        state.PendingDpi = e.NewDpi;
        state.PendingDpiAppliesInitialSize = !state.IsLoaded;
        if (state.DpiRecomputeQueued)
        {
            return;
        }

        state.DpiRecomputeQueued = true;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                state.DpiRecomputeQueued = false;
                RecomputeIfCurrent(window, state, state.PendingDpi, state.PendingDpiAppliesInitialSize);
            }));
    }

    private static void OnLocationChanged(object? sender, EventArgs e)
    {
        if (sender is not Window window
            || GetState(window) is not { } state
            || !state.HasApplied
            || state.RecomputeQueued)
        {
            return;
        }

        // Raised on every step of a drag, so the common case is one monitor query against the cached handle and
        // nothing more. Position alone never triggers sizing or clamping: moving the window during an ordinary
        // drag would pull it away from the cursor.
        if (Win32Display.GetWorkArea(state.WindowHandle) is not { } area || area.Monitor == state.LastMonitor)
        {
            return;
        }

        // The move that crosses to another monitor is reported before Windows sends the DPI change for it, so the
        // window may still carry the old monitor's DPI here; converting the new work area with it could shrink the
        // window wrongly. Deferred work runs after that message, and reads the DPI only then. The flag coalesces
        // the further drag steps that arrive before it runs.
        state.RecomputeQueued = true;
        bool applyInitialSize = !state.IsLoaded;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                state.RecomputeQueued = false;
                RecomputeIfCurrent(window, state, VisualTreeHelper.GetDpi(window), applyInitialSize);
            }));
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            Detach(window);
        }
    }

    // Deferred work can run after the window closed or the behavior was switched off; the state it captured is
    // then no longer the window's, and the work is dropped.
    private static void RecomputeIfCurrent(Window window, SizingState state, DpiScale dpi, bool applyInitialSize)
    {
        if (ReferenceEquals(GetState(window), state))
        {
            Recompute(window, state, dpi, applyInitialSize);
        }
    }

    private static void Recompute(Window window, SizingState state, DpiScale dpi, bool applyInitialSize)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || Win32Display.GetWorkArea(hwnd) is not { } area)
        {
            return;
        }

        // A per-monitor-aware process receives the work area in physical pixels. WPF converts the window's DIP
        // position and size with the window's own DPI, so dividing by that DPI yields coordinates WPF applies
        // unchanged. A DPI scale of zero yields an infinite size and is rejected with the empty case.
        double width = area.Width / dpi.DpiScaleX;
        double height = area.Height / dpi.DpiScaleY;
        if (!IsPositiveFinite(width) || !IsPositiveFinite(height))
        {
            return;
        }

        var workArea = new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, width, height);
        WindowSizingResult result = WindowSizing.Compute(workArea.Size, state.NonClientHeight);

        window.MinWidth = result.MinWidth;
        window.MinHeight = result.MinHeight;
        SetLayout(window, result.Layout);

        // Width and Height of a maximized or minimized window are its restore size; changing them would undo the
        // size the window returns to.
        if (window.WindowState == WindowState.Normal)
        {
            ApplySize(window, result, workArea, applyInitialSize);
        }

        state.WindowHandle = hwnd;
        state.LastMonitor = area.Monitor;
        state.HasApplied = true;
    }

    private static void ApplySize(Window window, WindowSizingResult result, Rect workArea, bool applyInitialSize)
    {
        double windowWidth = CurrentLength(window.ActualWidth, window.Width);
        double windowHeight = CurrentLength(window.ActualHeight, window.Height);
        bool resized = false;

        if (applyInitialSize)
        {
            windowWidth = result.InitialWidth;
            windowHeight = result.InitialHeight;
            window.Width = windowWidth;
            window.Height = windowHeight;
            resized = true;
        }
        else
        {
            // After loading, the size may be the user's own choice: it is only reduced, and only to what the work
            // area can show.
            if (windowWidth > workArea.Width)
            {
                windowWidth = workArea.Width;
                window.Width = windowWidth;
                resized = true;
            }

            if (windowHeight > workArea.Height)
            {
                windowHeight = workArea.Height;
                window.Height = windowHeight;
                resized = true;
            }
        }

        // Only a size the behavior itself set can have pushed the window past the work area's edges.
        if (resized)
        {
            ClampPosition(window, workArea, windowWidth, windowHeight);
        }
    }

    // An unset Left or Top (NaN) is left for WPF's startup location to fill in.
    private static void ClampPosition(Window window, Rect workArea, double windowWidth, double windowHeight)
    {
        if (!double.IsNaN(window.Left) && double.IsFinite(windowWidth))
        {
            window.Left = Math.Max(workArea.Left, Math.Min(window.Left, workArea.Right - windowWidth));
        }

        if (!double.IsNaN(window.Top) && double.IsFinite(windowHeight))
        {
            window.Top = Math.Max(workArea.Top, Math.Min(window.Top, workArea.Bottom - windowHeight));
        }
    }

    // ActualWidth and ActualHeight follow a user resize; before the first layout pass they are zero, and the
    // specified length, possibly NaN, is all there is.
    private static double CurrentLength(double actual, double specified) => actual > 0 ? actual : specified;

    // Caption plus top and bottom frame, in DIPs: about 39 on standard themes. Loaded replaces it with the
    // measured value.
    private static double EstimateNonClientHeight()
    {
        Thickness frame = SystemParameters.WindowNonClientFrameThickness;
        double estimate = frame.Top + frame.Bottom;
        return double.IsFinite(estimate) && estimate >= 0 ? estimate : 0;
    }

    // The fixed-row budgets are measured on the window's content, so everything the content does not receive
    // counts as non-client height: caption, frame, the window's border and padding, and the content's margin.
    // Content that does not stretch vertically is only as tall as it asks to be, so the difference would not be
    // the non-client height and is not used.
    private static bool TryMeasureNonClientHeight(Window window, out double nonClientHeight)
    {
        nonClientHeight = 0;
        if (window.Content is not FrameworkElement content
            || content.VerticalAlignment != VerticalAlignment.Stretch
            || !double.IsNaN(content.Height)
            || !(content.ActualHeight > 0))
        {
            return false;
        }

        double measured = window.ActualHeight - content.ActualHeight;
        if (!double.IsFinite(measured) || measured < 0)
        {
            return false;
        }

        nonClientHeight = measured;
        return true;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private sealed class SizingState
    {
        // Kept from the last recompute: a WPF window keeps one handle for its lifetime, and reusing it spares
        // LocationChanged an interop helper allocation on every drag step.
        public nint WindowHandle { get; set; }

        public nint LastMonitor { get; set; }

        // In DIPs. WPF lays out in DIPs, so it stays valid across DPI changes.
        public double NonClientHeight { get; set; }

        public bool IsLoaded { get; set; }

        public bool HasApplied { get; set; }

        public bool RecomputeQueued { get; set; }

        public DpiScale PendingDpi { get; set; }

        public bool PendingDpiAppliesInitialSize { get; set; }

        public bool DpiRecomputeQueued { get; set; }
    }
}

