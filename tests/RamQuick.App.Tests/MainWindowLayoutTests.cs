using System.Windows;
using RamQuick.App.Behaviors;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="WindowSizing.Compute"/>: the window bounds and active layout for the worked
/// work areas, the layout and supported-range thresholds, and the argument guards.
/// </summary>
public sealed class MainWindowLayoutTests
{
    // Screen cases assume a 48-DIP taskbar and a 39-DIP caption and frame, as the worked cases do.
    private const double TaskbarHeight = 48;
    private const double FrameHeight = 39;
    private const double Tolerance = 1e-6;

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

    // WPF's Size constructor rejects negative dimensions, so the only negative work area that can
    // reach Compute is Size.Empty, whose width and height are both negative infinity.
    private static Size CreateWorkArea(double width, double height) =>
        double.IsNegativeInfinity(width) && double.IsNegativeInfinity(height)
            ? Size.Empty
            : new Size(width, height);
}
