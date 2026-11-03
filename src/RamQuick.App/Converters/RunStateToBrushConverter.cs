using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RamQuick.Core;

namespace RamQuick.App.Converters;

/// <summary>
/// Converts a <see cref="RunState"/> into the background brush of the verdict badge: green for
/// <see cref="RunState.Passed"/>, red for <see cref="RunState.Failed"/>, amber for
/// <see cref="RunState.Aborted"/>, and <see cref="NeutralBrush"/> (transparent by default) for every
/// other value.
/// </summary>
/// <remarks>
/// <para>
/// The badge text is white and bold. Each default colour gives it a contrast ratio of at least 4.5:1,
/// the WCAG AA level for normal text: about 5.3:1 for green #1E7B34, 6.5:1 for red #B3261E and 5.2:1
/// for amber #A15C00. Colour is never the only indicator, because the badge always spells out PASS,
/// FAIL or ABORTED.
/// </para>
/// <para>
/// The default brushes are frozen, so they are immutable and safe to share across threads and across
/// converter instances. A brush assigned to one of the properties is returned exactly as given; freeze
/// it if the converter is shared across threads.
/// </para>
/// <para>
/// The conversion is one-way and pure: it reads only its input value and its own brush properties,
/// never the view model or the run state machine, and it never throws, so a binding to an unexpected
/// value degrades to <see cref="NeutralBrush"/>.
/// </para>
/// </remarks>
[ValueConversion(typeof(RunState), typeof(Brush))]
public sealed class RunStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush _defaultPassedBrush = CreateFrozen(0x1E, 0x7B, 0x34);
    private static readonly SolidColorBrush _defaultFailedBrush = CreateFrozen(0xB3, 0x26, 0x1E);
    private static readonly SolidColorBrush _defaultAbortedBrush = CreateFrozen(0xA1, 0x5C, 0x00);

    private Brush _passedBrush = _defaultPassedBrush;
    private Brush _failedBrush = _defaultFailedBrush;
    private Brush _abortedBrush = _defaultAbortedBrush;
    private Brush _neutralBrush = Brushes.Transparent;

    /// <summary>
    /// Gets or sets the brush returned for <see cref="RunState.Passed"/>. The default is a frozen
    /// green <see cref="SolidColorBrush"/>, #1E7B34.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value being set is <see langword="null"/>.</exception>
    public Brush PassedBrush
    {
        get => _passedBrush;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _passedBrush = value;
        }
    }

    /// <summary>
    /// Gets or sets the brush returned for <see cref="RunState.Failed"/>. The default is a frozen
    /// red <see cref="SolidColorBrush"/>, #B3261E.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value being set is <see langword="null"/>.</exception>
    public Brush FailedBrush
    {
        get => _failedBrush;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _failedBrush = value;
        }
    }

    /// <summary>
    /// Gets or sets the brush returned for <see cref="RunState.Aborted"/>. The default is a frozen
    /// amber <see cref="SolidColorBrush"/>, #A15C00.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value being set is <see langword="null"/>.</exception>
    public Brush AbortedBrush
    {
        get => _abortedBrush;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _abortedBrush = value;
        }
    }

    /// <summary>
    /// Gets or sets the brush returned for every value without a verdict: <see cref="RunState.Idle"/>,
    /// <see cref="RunState.Preparing"/>, <see cref="RunState.Running"/>, <see langword="null"/>,
    /// <c>DependencyProperty.UnsetValue</c> and values of any other type. The default is
    /// <see cref="Brushes.Transparent"/>, which is frozen.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value being set is <see langword="null"/>.</exception>
    public Brush NeutralBrush
    {
        get => _neutralBrush;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _neutralBrush = value;
        }
    }

    /// <summary>
    /// Returns the badge brush for a run state.
    /// </summary>
    /// <param name="value">The bound value, normally a boxed <see cref="RunState"/>.</param>
    /// <param name="targetType">The binding target type. Ignored.</param>
    /// <param name="parameter">The converter parameter. Ignored.</param>
    /// <param name="culture">The binding culture. Ignored.</param>
    /// <returns>
    /// <see cref="PassedBrush"/>, <see cref="FailedBrush"/> or <see cref="AbortedBrush"/> for the
    /// matching terminal state; <see cref="NeutralBrush"/> for any other value. Never
    /// <see langword="null"/>.
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RunState.Passed => PassedBrush,
        RunState.Failed => FailedBrush,
        RunState.Aborted => AbortedBrush,
        _ => NeutralBrush,
    };

    /// <summary>
    /// Not supported: the verdict badge binds one-way, from the run state to the brush.
    /// </summary>
    /// <param name="value">The target value. Ignored.</param>
    /// <param name="targetType">The source type. Ignored.</param>
    /// <param name="parameter">The converter parameter. Ignored.</param>
    /// <param name="culture">The binding culture. Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("RunStateToBrushConverter supports one-way bindings only.");

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
