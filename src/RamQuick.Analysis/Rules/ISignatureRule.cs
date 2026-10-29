namespace RamQuick.Analysis.Rules;

/// <summary>
/// One error-signature heuristic of the cause analyzer: it judges how strongly a run's error records show the
/// signature of a single <see cref="CauseCategory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every implementation honours this contract, which the analyzer and its tests rely on:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Evaluate"/> is pure and deterministic. It performs no I/O, reads no clock, uses no randomness and touches
/// no static mutable state, so the same input always yields an equal <see cref="RuleResult"/>.
/// </description></item>
/// <item><description>
/// It returns <see cref="RuleResult.NotFired"/> when the rule's evidence minimum is not met, or when the input holds no
/// error records.
/// </description></item>
/// <item><description>
/// Otherwise it returns <see cref="RuleResult.Fire(double, string)"/> with a score in the range 0 to 1. Every share is
/// a fraction of the error records analysed, <see cref="AnalysisInput.Errors"/>, never of the run's true error total,
/// which keeps counting past the stored-detail cap.
/// </description></item>
/// <item><description>
/// The evidence is one plain-English line that never states a cause as certain: every cause hint is labelled
/// <see cref="CauseHints.HintLabel"/>.
/// </description></item>
/// <item><description>
/// Every cut-off comes from <see cref="AnalyzerThresholds"/>; an implementation holds no literal thresholds of its own.
/// </description></item>
/// <item><description>
/// It does not throw for any well-formed <see cref="AnalysisInput"/>, empty error and step lists included.
/// </description></item>
/// <item><description>
/// An implementation is stateless, so one instance may be shared and evaluated from several threads at once.
/// </description></item>
/// </list>
/// </remarks>
public interface ISignatureRule
{
    /// <summary>
    /// Gets the cause category whose signature score this rule produces. The value is constant for an implementation,
    /// and the six rules each produce a different category.
    /// </summary>
    CauseCategory Category { get; }

    /// <summary>
    /// Evaluates the rule's signature against the error records and step outcomes of one run.
    /// </summary>
    /// <param name="input">
    /// The run's stored error records, step outcomes, tested size, worker count and memory type.
    /// </param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when the evidence minimum is not met or there are no error records; otherwise
    /// a fired result whose score, in the range 0 to 1, is computed from shares of <see cref="AnalysisInput.Errors"/>,
    /// with a one-line evidence statement.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    RuleResult Evaluate(AnalysisInput input);
}
