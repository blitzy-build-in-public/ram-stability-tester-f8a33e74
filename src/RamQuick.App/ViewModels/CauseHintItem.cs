using System.Globalization;
using RamQuick.Analysis;

namespace RamQuick.App.ViewModels;

/// <summary>
/// One entry of the result panel's cause hint list: a ranked <see cref="CauseHint"/> rendered as a
/// single line that always begins with <see cref="UiText.HintPrefix"/> ("Hint:"), together with
/// the evidence line shown beneath it.
/// </summary>
/// <remarks>
/// <para>
/// A cause hint is a hint, never a diagnosis. The list is headed <see cref="UiText.CauseHintsHeader"/>
/// ("Probable cause (hint)") by the view model, and every item's <see cref="Text"/> carries the
/// "Hint:" prefix, for example "Hint: Row/bank timings — 64 % — Errors appear under rapid row and
/// bank switching. Loosen tRCD, tRP or tRAS, or tFAW/tRRD."
/// </para>
/// <para>
/// The item is immutable: every display string is computed once in the constructor, so it raises no
/// change notifications and the view model replaces the whole list when the hints change. Titles come
/// from <see cref="Suggestions"/>, wording and templates from <see cref="UiText"/>, and numbers from
/// <see cref="DisplayFormat"/>, all with the invariant culture.
/// </para>
/// </remarks>
public sealed class CauseHintItem
{
    /// <summary>
    /// Creates the display item for a ranked cause hint.
    /// </summary>
    /// <param name="hint">The hint produced by the error analyzer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hint"/> is <see langword="null"/>.</exception>
    public CauseHintItem(CauseHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);

        Hint = hint;
        Rank = hint.Rank;
        Category = hint.Category;
        LikelihoodPercent = hint.LikelihoodPercent;
        Suggestion = hint.Suggestion;
        Evidence = hint.Evidence;
        Title = Suggestions.Title(hint.Category);
        LikelihoodText = DisplayFormat.WholePercent(hint.LikelihoodPercent);
        Text = ComposeText(Title, LikelihoodText, Suggestion);
    }

    /// <summary>
    /// Gets the hint this item displays.
    /// </summary>
    public CauseHint Hint { get; }

    /// <summary>
    /// Gets the 1-based position of the hint after ranking; 1 is the most likely cause.
    /// </summary>
    public int Rank { get; }

    /// <summary>
    /// Gets the probable cause the hint names.
    /// </summary>
    public CauseCategory Category { get; }

    /// <summary>
    /// Gets the hint's unrounded share of the kept hints' total score, in the range 0 to 100.
    /// <see cref="LikelihoodText"/> holds the rounded display form.
    /// </summary>
    public double LikelihoodPercent { get; }

    /// <summary>
    /// Gets the plain-language suggestion for the category, such as "Errors appear when data is read
    /// right after being written. Loosen tWR, tWTR or tRTP (and tCWL)."
    /// </summary>
    public string Suggestion { get; }

    /// <summary>
    /// Gets the one-line statement of the evidence behind the hint, shown beneath <see cref="Text"/>,
    /// such as "92 % of errors on bit 17 across 41 addresses". For the address-cluster rule it reads
    /// "errors concentrated in n % of the tested memory (virtual offsets)", a weak locality hint.
    /// </summary>
    public string Evidence { get; }

    /// <summary>
    /// Gets the short title of the category, such as "Row/bank timings", from
    /// <see cref="Suggestions.Title(CauseCategory)"/>.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the likelihood rounded to a whole percentage, such as "64 %" for 63.5, from
    /// <see cref="DisplayFormat.WholePercent(double)"/>.
    /// </summary>
    public string LikelihoodText { get; }

    /// <summary>
    /// Gets the item's display line: <see cref="UiText.HintPrefix"/>, the title, the likelihood and
    /// the suggestion, filled into <see cref="UiText.CauseHintFormat"/>, such as "Hint: Row/bank
    /// timings — 64 % — Errors appear under rapid row and bank switching. Loosen tRCD, tRP or tRAS,
    /// or tFAW/tRRD." It always starts with <see cref="UiText.HintPrefix"/>.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Returns the item's display line.
    /// </summary>
    /// <returns><see cref="Text"/>.</returns>
    public override string ToString() => Text;

    private static string ComposeText(string title, string likelihoodText, string suggestion)
    {
        string text = string.Format(
            CultureInfo.InvariantCulture,
            UiText.CauseHintFormat,
            UiText.HintPrefix,
            title,
            likelihoodText,
            suggestion);

        // The prefix is what labels the item a hint rather than a diagnosis, so it is guaranteed here
        // instead of being trusted to whichever template UiText supplies.
        return text.StartsWith(UiText.HintPrefix, StringComparison.Ordinal)
            ? text
            : UiText.HintPrefix + " " + text;
    }
}
