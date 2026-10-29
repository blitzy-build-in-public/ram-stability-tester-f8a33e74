using System.Diagnostics;
using System.Globalization;
using RamQuick.Analysis.Rules;
using RamQuick.Core;

namespace RamQuick.Analysis;

/// <summary>
/// Turns a run's stored error records and step outcomes into ranked probable-cause hints. The analysis is a pure
/// function: it performs no I/O, reads no clock, uses no randomness and holds no mutable state, so equal inputs always
/// yield value-equal hints and tests pass synthetic <see cref="AnalysisInput"/>s directly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Analyze"/> scores each <see cref="CauseCategory"/> in the same way:
/// </para>
/// <list type="number">
/// <item><description>
/// Signature: the category's rule from <see cref="Rules"/> is evaluated; its score counts only when the rule fired,
/// otherwise the signature score is 0.
/// </description></item>
/// <item><description>
/// Prior: <see cref="AnalyzerThresholds.PriorWeight"/> times the share of the analysed errors found by steps whose
/// <see cref="StepAttributes.PrimaryMechanism"/> maps to the category through <see cref="MechanismCategoryMap"/>.
/// Secondary mechanisms never count, and <see cref="CauseCategory.RankOrDimm"/>, to which no mechanism maps, always
/// has a prior of 0. The prior lets a single error, the usual case with Stop on error on, still rank the cause its
/// failing step targets.
/// </description></item>
/// <item><description>
/// Score: the larger of the two, rounded to <see cref="AnalyzerThresholds.ScoreDecimals"/> decimal places, one policy
/// for every category. A signature scoring above the prior weight therefore outranks every prior, and a weaker
/// signature competes with the other categories' priors on score alone.
/// </description></item>
/// <item><description>
/// Evidence: the rule's evidence line when the rule fired and its score is at least the prior; otherwise the prior's
/// line, naming the share of errors from steps targeting the cause and the step of the earliest such error.
/// </description></item>
/// </list>
/// <para>
/// Categories scoring at least <see cref="AnalyzerThresholds.KeepFloor"/> are kept and ranked by score, highest
/// first, with equal scores ordered by <see cref="TieBreakOrder"/>. Each kept hint's likelihood is 100 × its score ÷
/// the sum of the kept scores. The UI shows <see cref="CauseHints.Top"/>; the text and JSON summaries list every kept
/// hint. Every result is labelled <see cref="CauseHints.HintLabel"/>: a hint, never a diagnosis.
/// </para>
/// </remarks>
public static class ErrorAnalyzer
{
    /// <summary>
    /// Gets the six stateless signature rules, one per <see cref="CauseCategory"/>, in evaluation order: bit lane,
    /// hold, bank switching, turnaround, load and address cluster. The list is read-only and its rules may be shared
    /// across threads.
    /// </summary>
    public static IReadOnlyList<ISignatureRule> Rules { get; } = CreateRules();

    /// <summary>
    /// Gets the fixed order that breaks ties between equal category scores, most context-specific first:
    /// <see cref="CauseCategory.RefreshRetention"/>, <see cref="CauseCategory.WriteReadTurnaround"/>,
    /// <see cref="CauseCategory.RowBankTiming"/>, <see cref="CauseCategory.ImcPowerDelivery"/>,
    /// <see cref="CauseCategory.RankOrDimm"/>, <see cref="CauseCategory.VoltageSignalIntegrity"/>. It lists every
    /// category exactly once and is independent of the enumeration's declared values. The list is read-only.
    /// </summary>
    public static IReadOnlyList<CauseCategory> TieBreakOrder { get; } = CreateTieBreakOrder();

    /// <summary>
    /// Ranks the probable causes of a run's errors.
    /// </summary>
    /// <param name="input">
    /// The run's stored error records, at most the stored-detail cap, its step outcomes, tested size, worker count and
    /// memory type.
    /// </param>
    /// <returns>
    /// <see cref="CauseHints.Empty"/> when the input holds no error records, as for a clean run or one whose error
    /// detail is unavailable, or when no category reaches <see cref="AnalyzerThresholds.KeepFloor"/>, which can happen
    /// only when the errors' step indices match no step outcome. Otherwise the kept hints, ranked 1 to n by score with
    /// ties broken by <see cref="TieBreakOrder"/>, whose likelihoods sum to 100.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public static CauseHints Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ErrorCount == 0)
        {
            return CauseHints.Empty;
        }

        var signatures = new Dictionary<CauseCategory, RuleResult>(Rules.Count);
        foreach (ISignatureRule rule in Rules)
        {
            signatures[rule.Category] = rule.Evaluate(input);
        }

        var kept = new List<Candidate>(TieBreakOrder.Count);
        for (int tiePosition = 0; tiePosition < TieBreakOrder.Count; tiePosition++)
        {
            CauseCategory category = TieBreakOrder[tiePosition];
            RuleResult signature = signatures.TryGetValue(category, out RuleResult? result)
                ? result
                : RuleResult.NotFired;

            double priorShare = input.ShareOfErrorsInSteps(
                step => MechanismCategoryMap.Map(step.PrimaryMechanism) == category);

            // Rounding both terms equals rounding their maximum, and lets the evidence choice below compare the same
            // noise-free values the ranking compares.
            double signatureScore = RoundScore(signature.Fired ? signature.Score : 0d);
            double priorScore = RoundScore(AnalyzerThresholds.PriorWeight * priorShare);
            double score = Math.Max(signatureScore, priorScore);

            if (score < AnalyzerThresholds.KeepFloor)
            {
                continue;
            }

            string evidence = signature.Fired && signatureScore >= priorScore
                ? signature.Evidence
                    ?? throw new InvalidOperationException(
                        $"The {category} signature rule fired without an evidence line.")
                : PriorEvidence(input, category, priorShare);

            kept.Add(new Candidate(category, tiePosition, score, evidence));
        }

        if (kept.Count == 0)
        {
            return CauseHints.Empty;
        }

        Candidate[] ranked = kept
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.TiePosition)
            .ToArray();

        double totalScore = 0d;
        foreach (Candidate candidate in ranked)
        {
            totalScore += candidate.Score;
        }

        var hints = new CauseHint[ranked.Length];
        for (int i = 0; i < ranked.Length; i++)
        {
            Candidate candidate = ranked[i];

            // Dividing first keeps the likelihood within 0 to 100: a floating-point sum of positive scores is never
            // below any one of them, so the quotient never exceeds 1, and a lone hint gets exactly 100.
            double likelihoodPercent = 100d * (candidate.Score / totalScore);

            hints[i] = new CauseHint(
                i + 1,
                candidate.Category,
                candidate.Score,
                likelihoodPercent,
                Suggestions.For(candidate.Category),
                candidate.Evidence);
        }

        return new CauseHints(hints);
    }

    private static double RoundScore(double score) =>
        Math.Round(score, AnalyzerThresholds.ScoreDecimals, MidpointRounding.AwayFromZero);

    // Called only when the prior outscores the signature, so the prior share is positive and at least one analysed
    // error comes from a step that maps to the category.
    private static string PriorEvidence(AnalysisInput input, CauseCategory category, double priorShare)
    {
        ErrorRecord? earliest = null;
        StepAttributes? earliestStep = null;
        foreach (ErrorRecord error in input.Errors)
        {
            if (earliest is not null && error.Sequence >= earliest.Sequence)
            {
                continue;
            }

            StepAttributes? step = input.StepOf(error);
            if (step is not null && MechanismCategoryMap.Map(step.PrimaryMechanism) == category)
            {
                earliest = error;
                earliestStep = step;
            }
        }

        if (earliest is null || earliestStep is null)
        {
            throw new InvalidOperationException(
                $"The {category} prior is positive, but no analysed error comes from a step targeting it.");
        }

        string stepName = string.IsNullOrWhiteSpace(earliest.StepName) ? earliestStep.Name : earliest.StepName;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{RuleResult.FormatPercent(priorShare)} of errors in steps targeting this cause, e.g. '{stepName}'");
    }

    private static IReadOnlyList<ISignatureRule> CreateRules()
    {
        ISignatureRule[] rules =
        [
            new BitLaneRule(),
            new HoldRule(),
            new BankSwitchRule(),
            new TurnaroundRule(),
            new LoadRule(),
            new ClusterRule(),
        ];

        Debug.Assert(
            rules.Select(static rule => rule.Category).Distinct().Count() == rules.Length
                && rules.Length == Enum.GetValues<CauseCategory>().Length,
            "Each cause category must be scored by exactly one signature rule.");

        return Array.AsReadOnly(rules);
    }

    private static IReadOnlyList<CauseCategory> CreateTieBreakOrder()
    {
        CauseCategory[] order =
        [
            CauseCategory.RefreshRetention,
            CauseCategory.WriteReadTurnaround,
            CauseCategory.RowBankTiming,
            CauseCategory.ImcPowerDelivery,
            CauseCategory.RankOrDimm,
            CauseCategory.VoltageSignalIntegrity,
        ];

        Debug.Assert(
            order.Distinct().Count() == order.Length && order.Length == Enum.GetValues<CauseCategory>().Length,
            "The tie-break order must list every cause category exactly once.");

        return Array.AsReadOnly(order);
    }

    private readonly record struct Candidate(CauseCategory Category, int TiePosition, double Score, string Evidence);
}
