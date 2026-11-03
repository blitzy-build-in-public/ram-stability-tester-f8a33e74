using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The hold signature rule: errors found only when data is verified after sitting idle point to a refresh or
/// retention margin (tREFI, tRFC, DIMM temperature), so it scores <see cref="CauseCategory.RefreshRetention"/>.
/// </summary>
/// <remarks>
/// <para>
/// Evidence minimum: at least <see cref="AnalyzerThresholds.MinimumHoldSteps"/> hold step, one whose
/// <see cref="StepAttributes.HasHold"/> is <see langword="true"/>, must have started. Without one, no error could have
/// been verified after an idle hold, so the rule does not fire, even if records claim
/// <see cref="AccessType.VerifyAfterHold"/>.
/// </para>
/// <para>
/// Score: the share of the analysed error records whose <see cref="ErrorRecord.AccessType"/> is
/// <see cref="AccessType.VerifyAfterHold"/>. Errors that appear in hold steps only score 1; errors spread across
/// windowed and hold steps score the hold steps' share.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once.
/// </para>
/// </remarks>
public sealed class HoldRule : ISignatureRule
{
    /// <summary>
    /// Gets the category this rule scores: always <see cref="CauseCategory.RefreshRetention"/>.
    /// </summary>
    public CauseCategory Category => CauseCategory.RefreshRetention;

    /// <summary>
    /// Evaluates the hold signature against the error records and step outcomes of one run.
    /// </summary>
    /// <param name="input">
    /// The run's stored error records, step outcomes, tested size, worker count and memory type.
    /// </param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records or fewer than
    /// <see cref="AnalyzerThresholds.MinimumHoldSteps"/> hold steps started; otherwise a fired result whose score is the
    /// share of errors found on verification after an idle hold, with evidence such as
    /// "100 % of errors appeared on verification after an idle hold".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ErrorCount == 0)
        {
            return RuleResult.NotFired;
        }

        if (input.CountStepsRun(static step => step.HasHold) < AnalyzerThresholds.MinimumHoldSteps)
        {
            return RuleResult.NotFired;
        }

        double share = input.ShareOfErrors(static error => error.AccessType == AccessType.VerifyAfterHold);
        string evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{RuleResult.FormatPercent(share)} of errors appeared on verification after an idle hold");

        return RuleResult.Fire(share, evidence);
    }
}
