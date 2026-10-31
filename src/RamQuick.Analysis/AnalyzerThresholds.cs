using RamQuick.Core;

namespace RamQuick.Analysis;

/// <summary>
/// Every numeric cut-off the cause analyzer and its six signature rules use: evidence minimums, score thresholds,
/// rule weights, the step-mechanism prior weight, the keep floor and the ranking precision.
/// </summary>
/// <remarks>
/// <para>
/// These are the analyzer's only tunables. They are named constants rather than settings, so retuning a heuristic
/// is an edit here and nowhere else; <c>ErrorAnalyzer</c> and the rules under <c>Rules/</c> carry no literal
/// thresholds of their own.
/// </para>
/// <para>
/// Shares are fractions of the error records analysed, in the range 0 to 1, and every minimum is inclusive: a value
/// equal to its threshold meets it.
/// </para>
/// <para>
/// The class holds only constants and pure static methods, so the analyzer stays free of mutable static state.
/// </para>
/// </remarks>
public static class AnalyzerThresholds
{
    /// <summary>
    /// Bit-lane rule: the smallest share of errors that must flip the dominant bit lane for the rule to score. When
    /// the dominant lane's share reaches this value the score is that share; below it the score is 0.
    /// </summary>
    public const double DominantLaneShareMinimum = 0.8;

    /// <summary>
    /// Bit-lane and address-cluster rules: the divisor that defines one sixteenth of the tested region. The bit-lane
    /// rule needs its distinct words spread over at least <c>TestedBytes / RegionWindowDivisor</c> bytes of region
    /// offset, and the cluster rule scores the share of errors inside the densest window of that width.
    /// </summary>
    public const int RegionWindowDivisor = 16;

    /// <summary>
    /// Bit-lane rule: the lane of a bit is its index modulo this value on DDR4 and on any other or unknown memory
    /// type, matching a 64-bit data bus.
    /// </summary>
    public const int LaneModulo = 64;

    /// <summary>
    /// Bit-lane rule: the lane of a bit is its index modulo this value on DDR5, whose modules carry two independent
    /// 32-bit sub-channels, so bits 17 and 49 fall in the same lane.
    /// </summary>
    public const int Ddr5LaneModulo = 32;

    /// <summary>
    /// Step-mechanism prior: a category's prior is this weight times the share of errors from steps whose primary
    /// mechanism maps to the category. A category's score is the larger of its signature score and its prior, so a
    /// signature scoring above this weight outranks every prior.
    /// </summary>
    public const double PriorWeight = 0.3;

    /// <summary>
    /// Ranking: the smallest category score kept as a hint. Categories scoring below it are dropped, and the
    /// likelihoods of the kept categories are normalised over their summed scores.
    /// </summary>
    public const double KeepFloor = 0.05;

    /// <summary>
    /// Bank-switching rule: the weight applied to the share of errors found in <c>RandomBlocks</c> steps without a
    /// hold. The score is the share in <c>BankStride</c> steps plus this weight times that share.
    /// </summary>
    public const double RandomBlocksWeight = 0.5;

    /// <summary>
    /// Bit-lane rule: the evidence minimum, the number of distinct 8-byte words the errors must hit before the rule
    /// fires.
    /// </summary>
    public const int BitLaneMinimumDistinctWords = 2;

    /// <summary>
    /// Address-cluster rule: the evidence minimum in error records. The rule fires with at least this many errors,
    /// or with at least <see cref="ClusterMinimumErrorsAtOneWord"/> errors at the same 8-byte word.
    /// </summary>
    public const int ClusterMinimumErrors = 3;

    /// <summary>
    /// Address-cluster rule: the alternative evidence minimum, the number of errors at one 8-byte word that fires
    /// the rule even when fewer than <see cref="ClusterMinimumErrors"/> errors were recorded.
    /// </summary>
    public const int ClusterMinimumErrorsAtOneWord = 2;

    /// <summary>
    /// Hold rule: the evidence minimum, the number of hold steps that must have run before the rule fires.
    /// </summary>
    public const int MinimumHoldSteps = 1;

    /// <summary>
    /// Bank-switching, turnaround and load rules: the evidence minimum, the number of contrast steps that must have
    /// completed a pass with zero errors before the rule fires. The contrast steps are sequential-order steps for the
    /// bank-switching rule, non-turnaround steps without a hold for the turnaround rule, and <c>Half</c>-thread steps
    /// for the load rule.
    /// </summary>
    public const int MinimumCleanContrastSteps = 1;

    /// <summary>
    /// Ranking: the number of top-ranked hints the UI result panel shows. The text and JSON summaries list every
    /// kept hint.
    /// </summary>
    public const int DisplayedHintCount = 3;

    /// <summary>
    /// Ranking: the number of decimal places category scores are rounded to before ranking. Equal evidence then
    /// yields an exact tie, resolved by the fixed tie-break order, instead of an order decided by floating-point
    /// noise such as 0.1 + 0.2 differing from 0.3.
    /// </summary>
    public const int ScoreDecimals = 9;

    /// <summary>
    /// Analysis input: the largest number of error records analysed, the run's stored-detail cap
    /// (<see cref="ErrorGrouping.StoredDetailCap"/>). Shares are fractions of these stored records, never of the
    /// true error total.
    /// </summary>
    public const int MaxAnalysedRecords = ErrorGrouping.StoredDetailCap;

    /// <summary>
    /// Bit-lane rule: returns the modulus that folds a bit index into its lane for the installed memory type.
    /// </summary>
    /// <param name="memoryType">The DRAM generation of the installed modules, from the memory inventory.</param>
    /// <returns>
    /// <see cref="Ddr5LaneModulo"/> for <see cref="MemoryType.DDR5"/>; <see cref="LaneModulo"/> for
    /// <see cref="MemoryType.DDR4"/>, <see cref="MemoryType.Other"/>, <see cref="MemoryType.Unknown"/> and any
    /// undefined value. The method never throws.
    /// </returns>
    public static int LaneModuloFor(MemoryType memoryType) =>
        memoryType == MemoryType.DDR5 ? Ddr5LaneModulo : LaneModulo;
}
