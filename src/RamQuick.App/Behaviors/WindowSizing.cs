using System.Windows;

namespace RamQuick.App.Behaviors;

/// <summary>
/// The arrangement of the main window's fixed rows (the top area and the result header),
/// chosen from the work area of the monitor that holds the window.
/// </summary>
public enum WindowLayout
{
    /// <summary>
    /// The full layout: control row, live readings grid, detected-memory summary, notice line and
    /// result header. Its fixed rows need at most <see cref="WindowSizing.RegularFixedRowsBudget"/>
    /// (340) DIPs at a width of <see cref="WindowSizing.PreferredWidth"/> (560) DIPs.
    /// </summary>
    Regular,

    /// <summary>
    /// Live readings in three columns, the memory summary on one line, and the notice and
    /// first-error message each on one line with an ellipsis, their full text opening the detail
    /// area ahead of the cause hints. Its fixed rows need at most
    /// <see cref="WindowSizing.CompactFixedRowsBudget"/> (240) DIPs.
    /// </summary>
    Compact,

    /// <summary>
    /// The fallback for work areas below <see cref="WindowSizing.MinimalWorkAreaWidth"/> ×
    /// <see cref="WindowSizing.MinimalWorkAreaHeight"/> DIPs, or too short for
    /// <see cref="Compact"/>. Only the control row and a one-line status (step, time remaining,
    /// error count, verdict) stay fixed; everything else scrolls with the detail area.
    /// </summary>
    Minimal,
}

/// <summary>
/// Window bounds and the active layout for one work area. All values are device-independent
/// pixels (DIPs).
/// </summary>
/// <param name="InitialWidth">Width the window opens with: min(560, work-area width).</param>
/// <param name="InitialHeight">Height the window opens with: min(680, work-area height).</param>
/// <param name="MinWidth">Minimum window width: min(560, work-area width).</param>
/// <param name="MinHeight">
/// Minimum window height: the layout's fixed-row budget plus 40 DIPs of detail plus the non-client
/// height, capped at the work-area height so the minimum never exceeds the work area.
/// </param>
/// <param name="Layout">The layout whose fixed rows fit the initial client height.</param>
public sealed record WindowSizingResult(
    double InitialWidth,
    double InitialHeight,
    double MinWidth,
    double MinHeight,
    WindowLayout Layout);

/// <summary>
/// Pure computation of the main window's bounds and layout from the work area of the monitor
/// that holds it. It touches no window, visual or system setting, so it can be called from any
/// thread and tested without a dispatcher.
/// </summary>
public static class WindowSizing
{
    /// <summary>
    /// Preferred initial window width, and the largest minimum width, in DIPs: 560 (AAP 0.5.7
    /// window bounds).
    /// </summary>
    public const double PreferredWidth = 560;

    /// <summary>Preferred initial window height, in DIPs: 680 (AAP 0.5.7 window bounds).</summary>
    public const double PreferredHeight = 680;

    /// <summary>
    /// Height the <see cref="WindowLayout.Regular"/> fixed rows need at most, in DIPs, at a width of
    /// 560 DIPs: 340 (AAP 0.5.7 regular layout).
    /// </summary>
    public const double RegularFixedRowsBudget = 340;

    /// <summary>
    /// Height the <see cref="WindowLayout.Compact"/> fixed rows need at most, in DIPs: 240
    /// (AAP 0.5.7 compact layout).
    /// </summary>
    public const double CompactFixedRowsBudget = 240;

    /// <summary>
    /// Height the <see cref="WindowLayout.Minimal"/> fixed rows need at most, in DIPs: 72.
    /// AAP 0.5.7 names the minimal fallback's content but gives it no budget; this value is derived
    /// from that content: the control row (about 40 DIPs) plus a one-line status (about 32 DIPs).
    /// </summary>
    public const double MinimalFixedRowsBudget = 72;

    /// <summary>
    /// Detail-area height every layout must leave below its fixed rows, in DIPs: 40, enough for the
    /// first detail line while everything below it scrolls (AAP 0.5.7 layout selection).
    /// </summary>
    public const double MinimumDetailHeight = 40;

    /// <summary>
    /// Narrowest work area the regular and compact layouts support, in DIPs: 512 (AAP 0.5.7
    /// supported range of at least 512 × 336 DIPs).
    /// </summary>
    public const double MinimalWorkAreaWidth = 512;

    /// <summary>
    /// Shortest work area the regular and compact layouts support, in DIPs: 336 (AAP 0.5.7
    /// supported range of at least 512 × 336 DIPs).
    /// </summary>
    public const double MinimalWorkAreaHeight = 336;

    /// <summary>
    /// Computes the window's initial size, minimum size and active layout for a work area.
    /// </summary>
    /// <param name="workArea">
    /// Width <c>W</c> and height <c>H</c> of the work area of the monitor that holds the window, in DIPs.
    /// </param>
    /// <param name="nonClientHeight">
    /// The window's non-client height <c>N</c> (caption and frame), in DIPs. Zero is valid, for
    /// example for a borderless window.
    /// </param>
    /// <returns>
    /// Initial size min(560, W) × min(680, H); minimum width min(560, W); the layout whose fixed
    /// rows plus 40 DIPs of detail fit the initial client height min(680, H) − N; and minimum
    /// height min(F + 40 + N, H), where F is that layout's fixed-row budget.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="workArea"/> has a width or height that is NaN, infinite or not greater than
    /// zero (including <see cref="Size.Empty"/>), or <paramref name="nonClientHeight"/> is NaN,
    /// infinite or negative.
    /// </exception>
    /// <remarks>
    /// Inputs and outputs are DIPs of the work area of the monitor that holds the window, converted
    /// with that window's DPI; never <c>SystemParameters.WorkArea</c>, which describes only the
    /// primary monitor. The regular layout applies when the client height holds 340 + 40 DIPs, the
    /// compact one when it holds 240 + 40 DIPs and the work area is at least 512 × 336 DIPs, and the
    /// minimal fallback otherwise. The result never exceeds the work area: MinWidth ≤ W,
    /// MinHeight ≤ H, InitialWidth ≤ W and InitialHeight ≤ H.
    /// </remarks>
    public static WindowSizingResult Compute(Size workArea, double nonClientHeight)
    {
        double width = workArea.Width;
        double height = workArea.Height;

        if (!IsPositiveFinite(width) || !IsPositiveFinite(height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workArea),
                workArea,
                "The work-area width and height must be finite numbers of DIPs greater than zero.");
        }

        if (!double.IsFinite(nonClientHeight) || nonClientHeight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nonClientHeight),
                nonClientHeight,
                "The non-client height must be a finite number of DIPs, zero or greater.");
        }

        double initialWidth = Math.Min(PreferredWidth, width);
        double initialHeight = Math.Min(PreferredHeight, height);
        double minWidth = Math.Min(PreferredWidth, width);
        double clientHeight = initialHeight - nonClientHeight;

        WindowLayout layout = SelectLayout(width, height, clientHeight);

        // Capping at H keeps the minimum inside the work area even when the minimal fallback's
        // budget plus the frame is taller than a very short screen.
        double minHeight = Math.Min(FixedRowsBudget(layout) + MinimumDetailHeight + nonClientHeight, height);

        return new WindowSizingResult(initialWidth, initialHeight, minWidth, minHeight, layout);
    }

    /// <summary>
    /// Returns the fixed-row budget of a layout, in DIPs: the height its top area and result header
    /// may need at most.
    /// </summary>
    /// <param name="layout">The layout whose budget is requested.</param>
    /// <returns>
    /// <see cref="RegularFixedRowsBudget"/>, <see cref="CompactFixedRowsBudget"/> or
    /// <see cref="MinimalFixedRowsBudget"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="layout"/> is not a defined <see cref="WindowLayout"/> value.
    /// </exception>
    public static double FixedRowsBudget(WindowLayout layout) => layout switch
    {
        WindowLayout.Regular => RegularFixedRowsBudget,
        WindowLayout.Compact => CompactFixedRowsBudget,
        WindowLayout.Minimal => MinimalFixedRowsBudget,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "The window layout is not a defined value."),
    };

    // Plain comparisons with no tolerance: the thresholds are inclusive, so a 336-DIP-high work
    // area is still supported and the 200 % case on a 768-pixel screen (client height about 297
    // against 280) selects Compact unambiguously.
    private static WindowLayout SelectLayout(double width, double height, double clientHeight)
    {
        if (width < MinimalWorkAreaWidth
            || height < MinimalWorkAreaHeight
            || clientHeight < CompactFixedRowsBudget + MinimumDetailHeight)
        {
            return WindowLayout.Minimal;
        }

        return clientHeight >= RegularFixedRowsBudget + MinimumDetailHeight
            ? WindowLayout.Regular
            : WindowLayout.Compact;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
