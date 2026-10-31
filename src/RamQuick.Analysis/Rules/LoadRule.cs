using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The load signature rule: errors that appear with every worker loading memory, while a reduced-load step passed
/// cleanly, suggest that the memory controller lacks margin, through SoC/IMC voltage or CPU power delivery. It produces
/// the <see cref="CauseCategory.ImcPowerDelivery"/> score.
/// </summary>
/// <remarks>
/// <para>
/// Evidence minimum: at least <see cref="AnalyzerThresholds.MinimumCleanContrastSteps"/> <see cref="ThreadMode.Half"/>
/// step must have completed a pass with zero errors by the engine's true count. Without that contrast, errors under full
/// load say nothing about load itself, so the rule does not fire. The contrast steps are Standard's "Reduced-load
/// contrast" and Exhaustive's "Reduced-load contrast, long"; Quick has no <see cref="ThreadMode.Half"/> step, so the
/// rule never fires on a Quick run.
/// </para>
/// <para>
/// Score: the share of the analysed error records found by <see cref="ThreadMode.All"/>-thread steps without a hold.
/// Hold steps are excluded because their errors point at refresh and retention rather than at load; a run whose errors
/// all came from hold steps therefore fires with a score of 0.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once.
/// </para>
/// </remarks>
public sealed class LoadRule : ISignatureRule
{
    /// <summary>
    /// Gets the cause category this rule scores: <see cref="CauseCategory.ImcPowerDelivery"/>.
    /// </summary>
    public CauseCategory Category => CauseCategory.ImcPowerDelivery;

    /// <summary>
    /// Evaluates the load signature: the share of errors found with all workers loading memory, provided a
    /// reduced-load step passed cleanly.
    /// </summary>
    /// <param name="input">The run's stored error records, step outcomes and worker count.</param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records or no <see cref="ThreadMode.Half"/> step
    /// completed a pass with zero errors; otherwise a fired result whose score is the share of errors in
    /// <see cref="ThreadMode.All"/>-thread steps without a hold, with an evidence line such as
    /// "100 % of errors with all 16 workers loading memory, while a reduced-load step passed cleanly".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ErrorCount == 0)
        {
            return RuleResult.NotFired;
        }

        int cleanHalfSteps = input.CountCleanCompletedSteps(static step => step.Threads == ThreadMode.Half);
        if (cleanHalfSteps < AnalyzerThresholds.MinimumCleanContrastSteps)
        {
            return RuleResult.NotFired;
        }

        double share = input.ShareOfErrorsInSteps(static step => step.Threads == ThreadMode.All && !step.HasHold);
        string evidence =
            $"{RuleResult.FormatPercent(share)} of errors with all "
            + $"{input.WorkerCount.ToString(CultureInfo.InvariantCulture)} workers loading memory, "
            + "while a reduced-load step passed cleanly";

        return RuleResult.Fire(share, evidence);
    }
}
