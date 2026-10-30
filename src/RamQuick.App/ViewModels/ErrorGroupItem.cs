using System.Globalization;
using RamQuick.Core;

namespace RamQuick.App.ViewModels;

/// <summary>
/// One line of the grouped error list in the result detail: the stored error records that hit one
/// 8-byte word, shown as its labelled virtual address, how many stored records hit it, which bits
/// flipped and the step that first reported it.
/// </summary>
/// <remarks>
/// <para>
/// The item is immutable and raises no change notifications. <c>MainViewModel</c> builds one per
/// displayed <see cref="ErrorGroup"/>, at most the 100 largest, and replaces the whole list whenever
/// the controller publishes a new group list.
/// </para>
/// <para>
/// The count describes stored detail only, at most <see cref="ErrorGrouping.StoredDetailCap"/>
/// records per run, never the true error total. The list header that labels it that way once the
/// cap is reached belongs to <c>MainViewModel</c>. Every text is formatted through
/// <see cref="DisplayFormat"/> and <see cref="UiText"/> with the invariant culture.
/// </para>
/// </remarks>
public sealed class ErrorGroupItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorGroupItem"/> class and formats every
    /// displayed text of the group once.
    /// </summary>
    /// <param name="group">
    /// The error group to display, as built by <see cref="ErrorGrouping.Group"/> or
    /// <see cref="IncrementalErrorGrouping"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The group's <see cref="ErrorGroup.Count"/> is less than 1; every group holds at least one record.
    /// </exception>
    public ErrorGroupItem(ErrorGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // A count below 1 would otherwise wrap to an enormous number when widened for display.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(group.Count);

        Group = group;
        WordAddress = group.WordAddress;
        AddressText = DisplayFormat.Hex64(group.WordAddress);
        Count = group.Count;
        CountText = UiText.ErrorCount((ulong)group.Count);
        BitsText = DisplayFormat.Bits(group.BitMaskUnion);
        FirstStepName = group.FirstStepName;
        FirstTimeText = DisplayFormat.Duration(group.FirstTimeIntoTest);

        // The list line carries exactly the template's parts: the address, labelled as virtual
        // like every displayed address in v1, then the count, bits and first step. The first time
        // stays a separate property rather than an extra, unused argument.
        Text = string.Format(
            CultureInfo.InvariantCulture,
            UiText.ErrorGroupFormat,
            UiText.VirtualAddressLabel,
            AddressText,
            CountText,
            BitsText,
            FirstStepName);
    }

    /// <summary>
    /// Gets the error group this item displays.
    /// </summary>
    public ErrorGroup Group { get; }

    /// <summary>
    /// Gets the address of the 8-byte word every record in the group hit, a virtual address in v1.
    /// </summary>
    public ulong WordAddress { get; }

    /// <summary>
    /// Gets <see cref="WordAddress"/> as "0x" followed by 16 uppercase hexadecimal digits, such as
    /// "0x0000023A4F7C1D48".
    /// </summary>
    public string AddressText { get; }

    /// <summary>
    /// Gets the number of stored records in the group, at least 1. Once the detail cap is reached it
    /// can be lower than the number of errors the engine counted at this word.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets <see cref="Count"/> with its noun, such as "1 error" or "5 errors".
    /// </summary>
    public string CountText { get; }

    /// <summary>
    /// Gets the bit positions that flipped at this word in at least one stored record, ascending and
    /// comma-separated, such as "5, 17".
    /// </summary>
    public string BitsText { get; }

    /// <summary>
    /// Gets the name of the step whose record was the group's earliest, shown verbatim.
    /// </summary>
    public string FirstStepName { get; }

    /// <summary>
    /// Gets the time into the test of the group's earliest record, as "mm:ss", such as "01:12".
    /// </summary>
    public string FirstTimeText { get; }

    /// <summary>
    /// Gets the item's display line, <see cref="UiText.ErrorGroupFormat"/> filled with
    /// <see cref="UiText.VirtualAddressLabel"/>, the address, the count, the bits and the first step,
    /// such as "virtual address 0x0000023A4F7C1D48 — 3 errors — bits 17 — first in 'Random bank
    /// thrash'". The grouped error list binds this text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Returns <see cref="Text"/>, which also serves as the item's automation name and debugger display.
    /// </summary>
    /// <returns>The item's display line.</returns>
    public override string ToString() => Text;
}
