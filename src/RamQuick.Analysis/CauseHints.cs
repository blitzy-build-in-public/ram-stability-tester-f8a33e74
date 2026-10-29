namespace RamQuick.Analysis;

/// <summary>
/// The ranked probable causes the error analyzer found for one run, most likely first, always labelled
/// <see cref="HintLabel"/> ("Hint — not a diagnosis"). The hints say which cause the errors most resemble; they
/// never state a cause as certain.
/// </summary>
/// <remarks>
/// <para>
/// The label is neither a constructor argument nor a settable or stored value: <see cref="Label"/> always returns the
/// constant <see cref="HintLabel"/>, so no instance can exist without the label or with a different one.
/// </para>
/// <para>
/// Every consumer must display <see cref="Label"/> together with the items, never the items alone. The UI result
/// panel heads the list "Probable cause (hint)" and prefixes each item with "Hint:", both strings owned by
/// <c>src/RamQuick.App/UiText.cs</c>; <c>TextSummaryFormatter</c> writes the label above the list; and the JSON
/// summary writes it as <c>causeHints.label</c>. The logging library does not reference this one, so
/// <c>RunController</c> copies <see cref="HintLabel"/> into the summary.
/// </para>
/// <para>
/// The UI shows <see cref="Top"/>, the first <see cref="AnalyzerThresholds.DisplayedHintCount"/> items; the text and
/// JSON summaries list every item. Instances are immutable, so they are safe to share across threads.
/// </para>
/// </remarks>
public sealed class CauseHints
{
    /// <summary>
    /// The label every cause-hint list carries on every channel: "Hint — not a diagnosis", with an em dash (U+2014)
    /// and a space on either side. This constant is its single source.
    /// </summary>
    public const string HintLabel = "Hint \u2014 not a diagnosis";

    private readonly CauseHint[] _items;

    /// <summary>
    /// Creates a labelled list from hints that are already ranked.
    /// </summary>
    /// <param name="items">
    /// The kept hints in rank order. They are copied, so later changes to the source do not affect the list. Ranks
    /// must run 1, 2, … n in sequence order, each <see cref="CauseCategory"/> may appear at most once, and scores must
    /// not increase from one item to the next. Equal scores may appear in either order: the tie-break order belongs to
    /// the analyzer that ranks them. An empty sequence gives a labelled list with no items.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> contains a <see langword="null"/> element, a rank out of sequence, a repeated
    /// category, or a score higher than the score ranked before it.
    /// </exception>
    public CauseHints(IEnumerable<CauseHint> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CauseHint[] copy = items.ToArray();
        ValidateRanking(copy, nameof(items));

        _items = copy;
        Items = Array.AsReadOnly(copy);
    }

    /// <summary>
    /// Gets the single shared labelled list with no items, used when no error records were analysed: clean runs, and
    /// runs whose error detail is unavailable, such as summaries recovered from a journal
    /// (<c>errors.detailAvailable</c> false).
    /// </summary>
    public static CauseHints Empty { get; } = new([]);

    /// <summary>
    /// Gets the label shown with the hints: always <see cref="HintLabel"/>, on every instance, <see cref="Empty"/>
    /// included.
    /// </summary>
    public string Label => HintLabel;

    /// <summary>
    /// Gets every kept hint in rank order: <c>Items[0]</c> has rank 1, the most likely cause. The list is read-only,
    /// never <see langword="null"/> and never contains <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<CauseHint> Items { get; }

    /// <summary>
    /// Gets whether the list holds no hints.
    /// </summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Returns the highest-ranked hints, the subset the UI result panel shows.
    /// </summary>
    /// <param name="count">
    /// The largest number of hints to return; <see cref="AnalyzerThresholds.DisplayedHintCount"/> (three) by
    /// default. Zero returns an empty list.
    /// </param>
    /// <returns>
    /// A read-only list of the first <c>min(count, Items.Count)</c> items of <see cref="Items"/>, in rank order.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public IReadOnlyList<CauseHint> Top(int count = AnalyzerThresholds.DisplayedHintCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return count >= _items.Length ? Items : Array.AsReadOnly(_items[..count]);
    }

    private static void ValidateRanking(CauseHint[] items, string paramName)
    {
        var seenCategories = new HashSet<CauseCategory>();

        for (int index = 0; index < items.Length; index++)
        {
            CauseHint item = items[index];
            int expectedRank = index + 1;

            // The element type is non-nullable, but a caller can still pass a sequence holding null.
            if (item is null)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The cause hint at position {expectedRank} is null."),
                    paramName);
            }

            if (item.Rank != expectedRank)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Cause hint ranks must run 1 to n in order, but position {expectedRank} holds rank {item.Rank}."),
                    paramName);
            }

            if (!seenCategories.Add(item.Category))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Each cause category may appear once, but {item.Category} appears again at rank {item.Rank}."),
                    paramName);
            }

            if (index > 0 && item.Score > items[index - 1].Score)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Cause hints must be ordered by non-increasing score, but rank {item.Rank} scores {item.Score} above rank {index}'s {items[index - 1].Score}."),
                    paramName);
            }
        }
    }
}
