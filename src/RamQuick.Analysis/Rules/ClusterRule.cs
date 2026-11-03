using System.Globalization;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The address-cluster rule: judges how strongly a run's errors concentrate in one part of the tested memory, the
/// signature of <see cref="CauseCategory.RankOrDimm"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rule fires when at least <see cref="AnalyzerThresholds.ClusterMinimumErrors"/> error records were analysed, or
/// when at least <see cref="AnalyzerThresholds.ClusterMinimumErrorsAtOneWord"/> of them hit the same 8-byte word. A
/// single error, or two errors at different words, is no evidence of locality. The rule cannot fire when the region
/// window, <see cref="AnalysisInput.RegionWindowBytes"/>, is 0.
/// </para>
/// <para>
/// The score is the share of the analysed records whose <see cref="ErrorRecord.RegionOffset"/> lies inside the densest
/// window of <see cref="AnalysisInput.RegionWindowBytes"/>, one sixteenth of the tested region. Among equally dense
/// windows the one starting at the lowest offset is reported, so the evidence line is deterministic. Errors spread
/// evenly over the region therefore score about 1/16, while a stuck word or a fault confined to a small range scores
/// 1.
/// </para>
/// <para>
/// This is a weak locality hint. Offsets are virtual, and even physically contiguous memory is interleaved across
/// channels, ranks and banks, so a cluster shows locality in the tested memory and never identifies a specific rank
/// or DIMM. The evidence line therefore speaks only of "the tested memory (virtual offsets)" and names neither.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once. It runs
/// in O(n log n) over the analysed records.
/// </para>
/// </remarks>
public sealed class ClusterRule : ISignatureRule
{
    // The extent is shown to two decimals of a percent; anything smaller would print as "0", which misreads as
    // "no memory at all", so it is written as an explicit bound instead.
    private const string ExtentPercentFormat = "0.##";
    private const double SmallestShownExtentPercent = 0.01;
    private const string BelowSmallestShownExtent = "< 0.01";

    // An offset locates the start of its 8-byte word, so the extent includes the whole last word.
    private const ulong WordBytes = 8;

    /// <summary>
    /// Gets the cause category this rule scores: always <see cref="CauseCategory.RankOrDimm"/>.
    /// </summary>
    public CauseCategory Category => CauseCategory.RankOrDimm;

    /// <summary>
    /// Evaluates the address-cluster signature: the share of the analysed error records inside the densest window of
    /// one sixteenth of the tested region, by region offset.
    /// </summary>
    /// <param name="input">The run's stored error records and tested size.</param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records, when the region window is 0, or when fewer
    /// than <see cref="AnalyzerThresholds.ClusterMinimumErrors"/> records were analysed and no word holds
    /// <see cref="AnalyzerThresholds.ClusterMinimumErrorsAtOneWord"/> of them. Otherwise a fired result scoring the
    /// densest window's share, with evidence such as
    /// "100 % of errors concentrated in 2.5 % of the tested memory (virtual offsets)".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int errorCount = input.ErrorCount;
        ulong window = input.RegionWindowBytes;
        if (errorCount == 0 || window == 0 || !MeetsEvidenceMinimum(input.Errors))
        {
            return RuleResult.NotFired;
        }

        ulong[] offsets = SortedOffsets(input.Errors);

        int bestStart = 0;
        int bestEnd = 0;
        int bestCount = 0;
        int end = 0;
        for (int start = 0; start < errorCount; start++)
        {
            end = Math.Max(end, start);

            // The later offset minus the earlier is compared with the window, never the earlier offset plus the
            // window, so offsets near ulong.MaxValue cannot overflow.
            while (end < errorCount && offsets[end] - offsets[start] < window)
            {
                end++;
            }

            int count = end - start;

            // Strictly greater keeps the first maximum, so ties resolve to the window starting at the lowest offset.
            if (count > bestCount)
            {
                bestCount = count;
                bestStart = start;
                bestEnd = end - 1;
            }
        }

        double share = (double)bestCount / errorCount;

        // The span is below the window, at most TestedBytes / 16, so adding one word cannot overflow.
        ulong extentBytes = offsets[bestEnd] - offsets[bestStart] + WordBytes;
        double extentPercent = extentBytes / (double)input.TestedBytes * 100d;
        string extentText = extentPercent < SmallestShownExtentPercent
            ? BelowSmallestShownExtent
            : extentPercent.ToString(ExtentPercentFormat, CultureInfo.InvariantCulture);

        string evidence = RuleResult.FormatPercent(share)
            + " of errors concentrated in "
            + extentText
            + " % of the tested memory (virtual offsets)";

        return RuleResult.Fire(share, evidence);
    }

    /// <summary>
    /// Returns whether the records meet the rule's evidence minimum: enough records overall, or enough at one word.
    /// </summary>
    /// <param name="errors">The analysed error records, at least one.</param>
    /// <returns><see langword="true"/> when the rule may fire.</returns>
    private static bool MeetsEvidenceMinimum(IReadOnlyList<ErrorRecord> errors)
    {
        if (errors.Count >= AnalyzerThresholds.ClusterMinimumErrors)
        {
            return true;
        }

        var errorsPerWord = new Dictionary<ulong, int>(errors.Count);
        foreach (ErrorRecord error in errors)
        {
            errorsPerWord.TryGetValue(error.WordAddress, out int seen);
            seen++;
            if (seen >= AnalyzerThresholds.ClusterMinimumErrorsAtOneWord)
            {
                return true;
            }

            errorsPerWord[error.WordAddress] = seen;
        }

        return false;
    }

    /// <summary>
    /// Copies the region offsets of the records into a new array sorted in ascending order.
    /// </summary>
    /// <param name="errors">The analysed error records.</param>
    /// <returns>One offset per record, duplicates kept, ascending.</returns>
    private static ulong[] SortedOffsets(IReadOnlyList<ErrorRecord> errors)
    {
        ulong[] offsets = new ulong[errors.Count];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = errors[i].RegionOffset;
        }

        Array.Sort(offsets);
        return offsets;
    }
}
