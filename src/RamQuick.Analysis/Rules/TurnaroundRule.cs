using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The turnaround signature rule: errors found by reads issued right after writes to nearby addresses suggest that
/// write-recovery or read/write turnaround timings (tWR, tWTR, tRTP and tCWL) are too tight, and score the
/// <see cref="CauseCategory.WriteReadTurnaround"/> category.
/// </summary>
/// <remarks>
/// <para>
/// Evidence minimum: at least <see cref="AnalyzerThresholds.MinimumCleanContrastSteps"/> contrast step must have
/// completed a pass with zero errors. A contrast step is one whose access order is not
/// <see cref="AccessOrder.Turnaround"/> and that has no hold: it exercises memory without the turnaround order's
/// interleaving of each block's verification with the writing of the block four positions ahead, so a clean one
/// suggests the errors depend on that interleaving rather than on traffic alone. Hold steps are excluded because
/// their idle wait tests retention rather than turnaround.
/// </para>
/// <para>
/// Score: the share of the analysed error records whose access was <see cref="AccessType.TurnaroundRead"/>. The
/// engine records that access for the steady-state and the drain verifications of every turnaround window alike, so
/// every error a turnaround step finds counts. The rule can fire with a score of 0 when the contrast step ran clean
/// but no error came from a turnaround read.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once.
/// </para>
/// </remarks>
public sealed class TurnaroundRule : ISignatureRule
{
    /// <summary>
    /// Gets the cause category this rule scores: always <see cref="CauseCategory.WriteReadTurnaround"/>.
    /// </summary>
    public CauseCategory Category => CauseCategory.WriteReadTurnaround;

    /// <summary>
    /// Evaluates the turnaround signature against the error records and step outcomes of one run.
    /// </summary>
    /// <param name="input">
    /// The run's stored error records, step outcomes, tested size, worker count and memory type.
    /// </param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records, or when fewer than
    /// <see cref="AnalyzerThresholds.MinimumCleanContrastSteps"/> non-turnaround steps without a hold completed a pass
    /// with zero errors. Otherwise a fired result whose score is the share of error records with
    /// <see cref="AccessType.TurnaroundRead"/>, with an evidence line stating that share.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ErrorCount == 0)
        {
            return RuleResult.NotFired;
        }

        int cleanContrastSteps = input.CountCleanCompletedSteps(
            step => step.Order != AccessOrder.Turnaround && !step.HasHold);
        if (cleanContrastSteps < AnalyzerThresholds.MinimumCleanContrastSteps)
        {
            return RuleResult.NotFired;
        }

        double share = input.ShareOfErrors(error => error.AccessType == AccessType.TurnaroundRead);
        string percent = RuleResult.FormatPercent(share);
        string evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{percent} of errors on reads right after writes, while other steps passed cleanly");

        return RuleResult.Fire(share, evidence);
    }
}
