using System.Globalization;
using System.Numerics;
using System.Text;
using RamQuick.Core;
using RamQuick.Sensors;

namespace RamQuick.App.ViewModels;

/// <summary>
/// The single number formatter behind every display string the main window binds: durations,
/// temperatures, coverage, hexadecimal words, speeds, capacities, counts, percentages, step
/// progress, bit lists and the first-error message.
/// </summary>
/// <remarks>
/// <para>
/// Every number is formatted with <see cref="CultureInfo.InvariantCulture"/>, so the displayed
/// text, which tests assert verbatim, is the same on every machine whatever its regional
/// settings. The UI is English only. Words and composite-format templates come from
/// <see cref="UiText"/>; only pure number and unit formats are declared here.
/// </para>
/// <para>
/// The class is pure: it holds no state and performs no I/O, so its members are safe to call from
/// any thread and from binding getters.
/// </para>
/// </remarks>
public static class DisplayFormat
{
    // Minutes and seconds, each at least two digits; minutes are total minutes, never wrapped at an hour.
    private const string DurationFormat = "{0:00}:{1:00}";

    // A whole-number percentage, such as a cause hint's likelihood: "64 %".
    private const string WholePercentFormat = "{0} %";

    // Prefix and digit format of a full 64-bit word, shared with the JSON summary: "0x0000023A4F7C1D48".
    private const string HexPrefix = "0x";
    private const string HexWordFormat = "X16";

    // Coverage percentages and capacities in gigabytes carry one decimal.
    private const string OneDecimalFormat = "0.0";

    // Temperatures and likelihoods are rounded to whole numbers before display.
    private const string WholeNumberFormat = "0";

    // The true error total with invariant thousands separators: "12,345".
    private const string GroupedIntegerFormat = "N0";

    // Separator between the ascending bit indices of a bit list: "5, 17".
    private const string BitSeparator = ", ";

    // Windows (Explorer, Task Manager, System settings) labels binary gigabytes "GB", so 32 GiB of
    // installed memory reads "32.0 GB" here, matching what the user sees elsewhere in Windows.
    private const double BytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;

    /// <summary>
    /// Formats a duration as minutes and seconds, "mm:ss", truncated to whole seconds.
    /// </summary>
    /// <param name="value">The duration. A negative duration is shown as zero.</param>
    /// <returns>
    /// Total minutes and the remaining seconds, each at least two digits: 300 s gives "05:00",
    /// 72.9 s gives "01:12", 3600 s gives "60:00" and 6000 s gives "100:00".
    /// </returns>
    public static string Duration(TimeSpan value)
    {
        // Integer division of non-negative ticks is the floor, exactly, for every TimeSpan value.
        long totalSeconds = value.Ticks <= 0 ? 0 : value.Ticks / TimeSpan.TicksPerSecond;
        return string.Format(CultureInfo.InvariantCulture, DurationFormat, totalSeconds / 60, totalSeconds % 60);
    }

    /// <summary>
    /// Formats the engine's estimate of the time remaining.
    /// </summary>
    /// <param name="value">
    /// The estimate, or <see langword="null"/> while the engine still reports it as being calculated
    /// (<c>remaining_ms = −1</c>, until step 1 completes its first pass).
    /// </param>
    /// <returns>
    /// <see cref="UiText.Calculating"/> ("calculating…") for <see langword="null"/>; otherwise
    /// <see cref="Duration"/> of the estimate.
    /// </returns>
    public static string Remaining(TimeSpan? value) =>
        value is { } remaining ? Duration(remaining) : UiText.Calculating;

    /// <summary>
    /// Formats a temperature reading in whole degrees Celsius.
    /// </summary>
    /// <param name="value">The reading.</param>
    /// <returns>
    /// The temperature rounded half away from zero, such as "48 °C" for 47.5, when the reading is
    /// available; otherwise <see cref="UiText.NotAvailable"/> ("N/A"), which covers a missing driver,
    /// a missing sensor, a failed, stale or hung read.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static string Temperature(SensorValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAvailable || value.Celsius is not { } celsius)
        {
            return UiText.NotAvailable;
        }

        string degrees = RoundToWhole(celsius).ToString(WholeNumberFormat, CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture, UiText.TemperatureFormat, degrees);
    }

    /// <summary>
    /// Formats the coverage reached so far together with the test type's target.
    /// </summary>
    /// <param name="percent">
    /// The coverage percentage, unrounded. NaN, infinite and negative values are shown as 0.
    /// </param>
    /// <param name="targetPercent">The test type's coverage target, in whole percent.</param>
    /// <returns>The coverage to one decimal and the target, such as "412.6 % (target 200 %)".</returns>
    public static string Coverage(double percent, int targetPercent)
    {
        double shown = double.IsFinite(percent) && percent > 0 ? percent : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            UiText.CoverageFormat,
            shown.ToString(OneDecimalFormat, CultureInfo.InvariantCulture),
            targetPercent.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formats a 64-bit word, such as an address or a data value, as hexadecimal with all 16 digits.
    /// </summary>
    /// <param name="value">The word.</param>
    /// <returns>"0x" followed by 16 uppercase hexadecimal digits, such as "0x0000023A4F7C1D48".</returns>
    public static string Hex64(ulong value) =>
        HexPrefix + value.ToString(HexWordFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the configured memory speed.
    /// </summary>
    /// <param name="megaTransfersPerSecond">
    /// The transfer rate in MT/s, or <see langword="null"/> when the firmware tables do not report it.
    /// </param>
    /// <returns>
    /// The rate, such as "6000 MT/s"; <see cref="UiText.Unknown"/> for <see langword="null"/>, zero or a
    /// negative rate.
    /// </returns>
    public static string Speed(int? megaTransfersPerSecond)
    {
        if (megaTransfersPerSecond is not { } rate || rate <= 0)
        {
            return UiText.Unknown;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            UiText.SpeedFormat,
            rate.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formats a memory capacity in binary gigabytes (2^30 bytes), labelled "GB" as Windows does.
    /// </summary>
    /// <param name="bytes">The capacity in bytes, or <see langword="null"/> when it is not known.</param>
    /// <returns>
    /// The capacity to one decimal, such as "32.0 GB" for 34,359,738,368 bytes;
    /// <see cref="UiText.Unknown"/> for <see langword="null"/>.
    /// </returns>
    public static string Capacity(ulong? bytes)
    {
        if (bytes is not { } byteCount)
        {
            return UiText.Unknown;
        }

        double gigabytes = byteCount / BytesPerGigabyte;
        return string.Format(
            CultureInfo.InvariantCulture,
            UiText.CapacityFormat,
            gigabytes.ToString(OneDecimalFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formats a count from the memory summary, such as the channel or DIMM count.
    /// </summary>
    /// <param name="value">The count, or <see langword="null"/> when it is not known.</param>
    /// <returns>
    /// The count as a plain integer, such as "2"; <see cref="UiText.Unknown"/> for
    /// <see langword="null"/> or a negative count.
    /// </returns>
    public static string Count(int? value)
    {
        if (value is not { } count || count < 0)
        {
            return UiText.Unknown;
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats the true error total, taken from the engine's counters and never derived from the
    /// stored error records.
    /// </summary>
    /// <param name="value">The error total.</param>
    /// <returns>The total with thousands separators, such as "12,345".</returns>
    public static string ErrorTotal(ulong value) =>
        value.ToString(GroupedIntegerFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a percentage rounded to a whole number, such as a cause hint's likelihood.
    /// </summary>
    /// <param name="percent">The percentage, unrounded. NaN and infinite values are shown as 0.</param>
    /// <returns>The percentage rounded half away from zero, such as "64 %" for 63.6 or 63.5.</returns>
    public static string WholePercent(double percent)
    {
        double shown = double.IsFinite(percent) ? RoundToWhole(percent) : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            WholePercentFormat,
            shown.ToString(WholeNumberFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formats the current step as its name followed by its 1-based number and the step count.
    /// </summary>
    /// <param name="name">The step name, shown verbatim.</param>
    /// <param name="stepIndex">The zero-based index of the step; 1 is added for display.</param>
    /// <param name="stepCount">The number of steps in the plan.</param>
    /// <returns>The step text, such as "Read-after-write turnaround (3 of 6)" for index 2 of 6.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static string StepProgress(string name, int stepIndex, int stepCount)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Widened so that adding 1 cannot overflow.
        long stepNumber = stepIndex + 1L;
        return string.Format(
            CultureInfo.InvariantCulture,
            UiText.StepProgressFormat,
            name,
            stepNumber.ToString(CultureInfo.InvariantCulture),
            stepCount.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formats the set bits of a mask as their indices in ascending order.
    /// </summary>
    /// <param name="mask">The bit mask, such as the flipped bits of an error group.</param>
    /// <returns>
    /// The bit indices 0 to 63 joined by ", ", such as "5, 17" for bits 5 and 17;
    /// <see cref="UiText.NotYetKnown"/> ("—") when no bit is set.
    /// </returns>
    public static string Bits(ulong mask)
    {
        if (mask == 0)
        {
            return UiText.NotYetKnown;
        }

        // Each index has at most two digits and its separator two characters, so four characters
        // per set bit is enough for the builder never to reallocate.
        var builder = new StringBuilder(4 * BitOperations.PopCount(mask));
        ulong remaining = mask;
        while (remaining != 0)
        {
            if (builder.Length > 0)
            {
                builder.Append(BitSeparator);
            }

            builder.Append(CultureInfo.InvariantCulture, $"{BitOperations.TrailingZeroCount(remaining)}");
            remaining &= remaining - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the first-error message of the result panel.
    /// </summary>
    /// <param name="record">The first confirmed error of the run.</param>
    /// <param name="totalErrors">The true error total so far, from the engine's counters.</param>
    /// <returns>
    /// <see cref="UiText.FirstErrorFormat"/> filled with the step name, the time into the test, the
    /// error count, the "virtual address" label, the address, the expected, actual and re-read words,
    /// and the re-read inference, such as "Step 'Random bank thrash' at 01:12 — 3 errors — virtual
    /// address 0x0000023A4F7C1D48 — expected 0x…, actual 0x…, re-read 0x… (repeated wrong value:
    /// likely write or storage fault)".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The record's <see cref="ErrorRecord.RereadOutcome"/> is not a defined value.
    /// </exception>
    public static string FirstError(ErrorRecord record, ulong totalErrors)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A first-error record is itself one error, so the message never reads "0 errors" when its
        // total arrives after the record.
        ulong shownTotal = Math.Max(totalErrors, 1UL);

        // Every v1 address is virtual: no documented user-mode API yields physical addresses.
        return string.Format(
            CultureInfo.InvariantCulture,
            UiText.FirstErrorFormat,
            record.StepName,
            Duration(record.TimeIntoTest),
            UiText.ErrorCount(shownTotal),
            UiText.VirtualAddressLabel,
            Hex64(record.Address),
            Hex64(record.Expected),
            Hex64(record.Actual),
            Hex64(record.Reread),
            RereadOutcomeText.Describe(record.RereadOutcome));
    }

    // Rounds half away from zero, as displays conventionally do, and folds −0 into 0 so that a value
    // such as −0.4 never prints as "-0".
    private static double RoundToWhole(double value)
    {
        double rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}
