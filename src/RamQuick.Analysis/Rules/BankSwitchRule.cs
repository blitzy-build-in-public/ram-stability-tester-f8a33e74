using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The bank-switching signature rule. Errors found under rapid row and bank switching, while a sequential-order step
/// passed cleanly, suggest that row or activation timings (tRCD, tRP or tRAS, or tFAW and tRRD) may be too tight. It
/// scores <see cref="CauseCategory.RowBankTiming"/>.
/// </summary>
/// <remarks>
/// <para>
/// Evidence minimum: at least <see cref="AnalyzerThresholds.MinimumCleanContrastSteps"/> step with
/// <see cref="AccessOrder.SequentialAscending"/> or <see cref="AccessOrder.SequentialDescending"/> order completed a
/// pass with a true error count of zero. That clean sequential step is the contrast: the same memory survived linear
/// traffic, so errors under switching orders point at switching rather than at the cells themselves.
/// </para>
/// <para>
/// Score: the share of the analysed errors found by <see cref="AccessOrder.BankStride"/> steps, plus
/// <see cref="AnalyzerThresholds.RandomBlocksWeight"/> times the share found by <see cref="AccessOrder.RandomBlocks"/>
/// steps without a hold. Random blocks also switch rows and banks, but less densely than bank strides, hence the
/// weight. Random-block steps with a hold are left out, because errors after an idle hold carry the retention
/// signature the hold rule scores.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once.
/// </para>
/// </remarks>
public sealed class BankSwitchRule : ISignatureRule
{
    /// <summary>
    /// Gets <see cref="CauseCategory.RowBankTiming"/>, the category whose signature score this rule produces.
    /// </summary>
    public CauseCategory Category => CauseCategory.RowBankTiming;

    /// <summary>
    /// Evaluates the bank-switching signature against the error records and step outcomes of one run.
    /// </summary>
    /// <param name="input">The run's stored error records and step outcomes.</param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records or no sequential-order step completed a pass
    /// cleanly; otherwise a fired result whose score is the bank-stride share plus the weighted random-block share,
    /// with an evidence line stating both shares.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ErrorCount == 0)
        {
            return RuleResult.NotFired;
        }

        // The plans give the moving-inversion steps (Q4, S7, E13) SequentialAscending order, although the engine
        // sweeps them ascending then descending whatever the order. A clean moving-inversion step therefore counts as
        // a clean sequential step, as Q1 and S8 do: each visits its slice linearly, which is the contrast this rule
        // needs against the row- and bank-switching orders.
        int cleanSequentialSteps = input.CountCleanCompletedSteps(
            static step => step.Order is AccessOrder.SequentialAscending or AccessOrder.SequentialDescending);
        if (cleanSequentialSteps < AnalyzerThresholds.MinimumCleanContrastSteps)
        {
            return RuleResult.NotFired;
        }

        double bankShare = input.ShareOfErrorsInSteps(static step => step.Order == AccessOrder.BankStride);
        double randomShare = input.ShareOfErrorsInSteps(
            static step => step.Order == AccessOrder.RandomBlocks && !step.HasHold);

        // The two step sets are disjoint, so the score cannot exceed 1; RuleResult.Fire clamps regardless.
        double score = bankShare + (AnalyzerThresholds.RandomBlocksWeight * randomShare);

        string evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{RuleResult.FormatPercent(bankShare)} of errors in bank-stride steps and "
            + $"{RuleResult.FormatPercent(randomShare)} in random-block steps, while a sequential step passed cleanly");

        return RuleResult.Fire(score, evidence);
    }
}
