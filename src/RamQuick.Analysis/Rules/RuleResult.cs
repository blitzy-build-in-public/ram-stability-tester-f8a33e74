using System.Globalization;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The outcome of evaluating one signature rule against an analysis input: whether the rule's evidence minimum
/// was met and, if so, how strongly the error records match its signature.
/// </summary>
/// <param name="Fired">
/// <see langword="true"/> when the rule's evidence minimum (its precondition) was met, so that <paramref name="Score"/>
/// reflects the error records; <see langword="false"/> when the rule had nothing to judge.
/// </param>
/// <param name="Score">
/// The signature score in the range 0 to 1. A rule can fire with a score of 0, for example when the evidence
/// minimum is met but the signature itself is absent.
/// </param>
/// <param name="Evidence">
/// A one-line plain-English statement of the evidence behind the score when the rule fired; otherwise
/// <see langword="null"/>.
/// </param>
public sealed record RuleResult(bool Fired, double Score, string? Evidence)
{
    /// <summary>
    /// The result a rule returns when its evidence minimum is not met, or when there are no error records to
    /// analyse. It has <see cref="Fired"/> <see langword="false"/>, a <see cref="Score"/> of 0 and no
    /// <see cref="Evidence"/>.
    /// </summary>
    public static RuleResult NotFired { get; } = new(false, 0d, null);

    /// <summary>
    /// Creates the result of a rule whose evidence minimum was met.
    /// </summary>
    /// <param name="score">
    /// The signature score. Values outside 0 to 1 are clamped into that range, and NaN becomes 0, so a rule's
    /// arithmetic can never push a category's score out of range.
    /// </param>
    /// <param name="evidence">The one-line evidence statement shown with the hint.</param>
    /// <returns>A fired result carrying the normalised score and the evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="evidence"/> is empty or consists only of white space.</exception>
    public static RuleResult Fire(double score, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        // Math.Clamp propagates NaN, so NaN is mapped to 0 before clamping.
        double normalised = double.IsNaN(score) ? 0d : Math.Clamp(score, 0d, 1d);

        return new RuleResult(true, normalised, evidence);
    }

    /// <summary>
    /// Formats a share of error records as a whole percentage for evidence lines, for example 0.92 as "92 %".
    /// The share is clamped to 0 to 1 (NaN becomes 0), rounded half away from zero, and formatted with the
    /// invariant culture so evidence text never depends on the thread culture.
    /// </summary>
    /// <param name="share">The fraction of the analysed error records, nominally 0 to 1.</param>
    /// <returns>The percentage followed by a space and the percent sign.</returns>
    internal static string FormatPercent(double share)
    {
        double clamped = double.IsNaN(share) ? 0d : Math.Clamp(share, 0d, 1d);
        double percent = Math.Round(clamped * 100d, MidpointRounding.AwayFromZero);

        return percent.ToString("0", CultureInfo.InvariantCulture) + " %";
    }
}
