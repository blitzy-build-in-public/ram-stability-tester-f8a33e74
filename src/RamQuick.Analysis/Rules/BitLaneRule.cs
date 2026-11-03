using System.Globalization;
using System.Numerics;
using RamQuick.Core;

namespace RamQuick.Analysis.Rules;

/// <summary>
/// The bit-lane signature rule: errors that keep flipping the same data bit lane at addresses spread across the
/// tested region suggest a DRAM voltage or signal-integrity margin (VDD, VDDQ, drive strength or ODT), scored for
/// <see cref="CauseCategory.VoltageSignalIntegrity"/>.
/// </summary>
/// <remarks>
/// <para>
/// Evidence minimum: the errors hit at least <see cref="AnalyzerThresholds.BitLaneMinimumDistinctWords"/> distinct
/// 8-byte words, and their region offsets span at least <see cref="AnalysisInput.RegionWindowBytes"/>, one sixteenth
/// of the tested region. A stuck bit at one word, or errors confined to one small part of the region, is the address
/// cluster rule's signature rather than a lane-wide margin, so neither fires this rule.
/// </para>
/// <para>
/// A bit's lane is its index modulo <see cref="AnalysisInput.LaneModulo"/>: 64 on a 64-bit data bus, and
/// <see cref="AnalyzerThresholds.Ddr5LaneModulo"/> on DDR5, whose two 32-bit sub-channels make bits 17 and 49 one
/// lane. Each error counts once for every lane it flips, even when two of its flipped bits share a lane. The dominant
/// lane is the lane flipped by the most errors, the lowest lane index winning a tie. The score is that lane's share
/// of the analysed errors when the share reaches <see cref="AnalyzerThresholds.DominantLaneShareMinimum"/>, and 0
/// otherwise; the rule still fires with a score of 0 once its evidence minimum is met.
/// </para>
/// <para>
/// The rule is stateless and pure, so one instance may be shared and evaluated from several threads at once.
/// </para>
/// </remarks>
public sealed class BitLaneRule : ISignatureRule
{
    /// <summary>
    /// Gets the category this rule scores: always <see cref="CauseCategory.VoltageSignalIntegrity"/>.
    /// </summary>
    public CauseCategory Category => CauseCategory.VoltageSignalIntegrity;

    /// <summary>
    /// Evaluates the bit-lane signature against the run's stored error records.
    /// </summary>
    /// <param name="input">The run's stored error records, tested size and memory type.</param>
    /// <returns>
    /// <see cref="RuleResult.NotFired"/> when there are no error records, when fewer than 16 bytes were tested, when the
    /// errors hit fewer than <see cref="AnalyzerThresholds.BitLaneMinimumDistinctWords"/> distinct words, or when their
    /// region offsets span less than <see cref="AnalysisInput.RegionWindowBytes"/>. Otherwise a fired result scoring
    /// the dominant lane's share, or 0 below <see cref="AnalyzerThresholds.DominantLaneShareMinimum"/>, with evidence
    /// such as "92 % of errors on bit 17 across 41 addresses", or "… on bit 17 or 49 …" on DDR5.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public RuleResult Evaluate(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        IReadOnlyList<ErrorRecord> errors = input.Errors;
        int errorCount = input.ErrorCount;
        ulong windowBytes = input.RegionWindowBytes;
        if (errorCount == 0 || windowBytes == 0)
        {
            return RuleResult.NotFired;
        }

        if (ErrorGrouping.CountDistinctAddresses(errors) < AnalyzerThresholds.BitLaneMinimumDistinctWords)
        {
            return RuleResult.NotFired;
        }

        if (OffsetSpan(errors) < windowBytes)
        {
            return RuleResult.NotFired;
        }

        int modulo = input.LaneModulo;
        int[] laneCounts = new int[modulo];
        ulong[] laneMasks = new ulong[errorCount];
        for (int i = 0; i < errorCount; i++)
        {
            ulong lanes = LanesFlipped(errors[i].BitMask, modulo);
            laneMasks[i] = lanes;
            for (ulong remaining = lanes; remaining != 0; remaining &= remaining - 1)
            {
                laneCounts[BitOperations.TrailingZeroCount(remaining)]++;
            }
        }

        int dominantLane = 0;
        for (int lane = 1; lane < modulo; lane++)
        {
            if (laneCounts[lane] > laneCounts[dominantLane])
            {
                dominantLane = lane;
            }
        }

        if (laneCounts[dominantLane] == 0)
        {
            // Unreachable for engine-reported mismatches, whose expected and actual values always differ; a record
            // with an empty bit mask still met the evidence minimum, so the rule fires without a signature.
            return RuleResult.Fire(0d, "No data bit was flipped by the analysed errors");
        }

        double share = (double)laneCounts[dominantLane] / errorCount;
        double score = share >= AnalyzerThresholds.DominantLaneShareMinimum ? share : 0d;

        ulong dominantBit = 1UL << dominantLane;
        int laneAddresses = ErrorGrouping.CountDistinctAddresses(
            errors.Where((_, index) => (laneMasks[index] & dominantBit) != 0));

        string percent = RuleResult.FormatPercent(share);
        string evidence = modulo == AnalyzerThresholds.Ddr5LaneModulo
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{percent} of errors on bit {dominantLane} or {dominantLane + modulo} across {laneAddresses} addresses")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{percent} of errors on bit {dominantLane} across {laneAddresses} addresses");

        return RuleResult.Fire(score, evidence);
    }

    // Subtracting the minimum from the maximum keeps the span within ulong for any pair of offsets.
    private static ulong OffsetSpan(IReadOnlyList<ErrorRecord> errors)
    {
        ulong minimum = ulong.MaxValue;
        ulong maximum = ulong.MinValue;
        foreach (ErrorRecord error in errors)
        {
            ulong offset = error.RegionOffset;
            minimum = Math.Min(minimum, offset);
            maximum = Math.Max(maximum, offset);
        }

        return maximum - minimum;
    }

    // Folds the flipped bits into a mask of lanes, so a lane flipped by two bits of one word counts once.
    // BitMask is read directly because ErrorRecord.BitPositions allocates a new array on every access.
    private static ulong LanesFlipped(ulong bitMask, int modulo)
    {
        ulong lanes = 0;
        for (ulong remaining = bitMask; remaining != 0; remaining &= remaining - 1)
        {
            lanes |= 1UL << (BitOperations.TrailingZeroCount(remaining) % modulo);
        }

        return lanes;
    }
}
