using RamQuick.Core;

namespace RamQuick.Analysis.Tests;

/// <summary>
/// Success criterion 4 on synthetic sets: for each fault class the fault-injection correctness runs inject, the
/// analyzer's top-ranked cause is the expected category, decided by a strictly higher score, and the result is
/// labelled as a hint. Single-bit flips on a fixed lane rank <see cref="CauseCategory.VoltageSignalIntegrity"/>, a
/// stuck bit and an address-region fault rank <see cref="CauseCategory.RankOrDimm"/>, and a delay-dependent fault ranks
/// <see cref="CauseCategory.RefreshRetention"/>. The engine-driven counterpart is <c>FaultInjectionEndToEndTests</c>
/// in <c>RamQuick.IntegrationTests</c>.
/// </summary>
/// <remarks>
/// Each class runs under the default seed and three further fixed seeds, so the expected ranking cannot rest on one
/// lucky draw of offsets, words or bits. Every case builds its input fresh from <see cref="SyntheticErrorSets"/>; the
/// analyzer is pure, so the tests need no I/O or clock and are safe to run in parallel.
/// </remarks>
public sealed class InjectedSignatureTests
{
    // The label text the product specification fixes, checked beside the constant so that neither can drift alone.
    private const string ExpectedHintLabel = "Hint \u2014 not a diagnosis";

    // Cluster evidence must speak of locality in the tested memory only: offsets are virtual and memory is
    // interleaved, so a cluster never identifies a rank or DIMM.
    private const string ClusterEvidencePhrase = "of the tested memory (virtual offsets)";

    private const double LikelihoodSumTolerance = 1e-9;

    private const ulong FurtherSeedA = 0x0000_0000_0000_0001UL;
    private const ulong FurtherSeedB = 0x0123_4567_89AB_CDEFUL;
    private const ulong FurtherSeedC = 0xC0DE_D00D_0BAD_F00DUL;

    private static readonly ulong[] _furtherSeeds = [FurtherSeedA, FurtherSeedB, FurtherSeedC];

    private static readonly ulong[] _allSeeds = [SyntheticErrorSets.DefaultSeed, .. _furtherSeeds];

    private static readonly MemoryType[] _laneMemoryTypes = [MemoryType.DDR4, MemoryType.DDR5];

    public static TheoryData<ulong> Seeds { get; } = new(_allSeeds);

    public static TheoryData<ulong> FurtherSeeds { get; } = new(_furtherSeeds);

    public static TheoryData<MemoryType, ulong> LaneCases { get; } = CreateLaneCases();

    // ---- One theory per injected fault class ----

    [Theory]
    [MemberData(nameof(LaneCases))]
    public void Analyze_SingleBitFlipsOnFixedLane_TopIsVoltageSignalIntegrity(MemoryType memoryType, ulong seed)
    {
        AnalysisInput input = SyntheticErrorSets.SingleBitFlipsSpread(memoryType, seed);

        // Bit 17 is lane 17 under the 64-bit DDR4 modulus and the 32-bit DDR5 modulus alike.
        Assert.Equal(memoryType, input.MemoryType);
        Assert.Equal(SyntheticErrorSets.InjectedFlipBit, SyntheticErrorSets.InjectedFlipBit % input.LaneModulo);
        Assert.All(input.Errors, error =>
        {
            Assert.Equal(SyntheticErrorSets.InjectedFlipBit, Assert.Single(error.BitPositions));
            Assert.Equal(RereadOutcome.RereadRepeatsActual, error.RereadOutcome);
        });
        Assert.Equal(input.ErrorCount, ErrorGrouping.CountDistinctAddresses(input.Errors));
        Assert.True(
            OffsetSpan(input) >= input.RegionWindowBytes,
            FormattableString.Invariant(
                $"The flips span {OffsetSpan(input)} bytes, less than one region window of {input.RegionWindowBytes}."));
        Assert.Equal(
            SyntheticErrorSets.ScaledStandardSteps.Select(static step => step.Index).Order(),
            ErrorStepIndices(input));

        CauseHints result = ErrorAnalyzer.Analyze(input);

        CauseHint top = AssertTopRanked(result, CauseCategory.VoltageSignalIntegrity);
        Assert.Contains(FormattableString.Invariant($"bit {SyntheticErrorSets.InjectedFlipBit} "), top.Evidence);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Analyze_StuckBitAtOneWord_TopIsRankOrDimm(ulong seed)
    {
        AnalysisInput input = SyntheticErrorSets.StuckBitAtOneWord(seed);
        ulong stuckWord = SyntheticErrorSets.BaseAddress + SyntheticErrorSets.StuckWordOffset(seed);

        Assert.All(input.Errors, error =>
        {
            Assert.Equal(stuckWord, error.WordAddress);
            Assert.Equal(SyntheticErrorSets.StuckBit, Assert.Single(error.BitPositions));
        });
        ErrorGroup group = Assert.Single(ErrorGrouping.Group(input.Errors));
        Assert.Equal(stuckWord, group.WordAddress);
        Assert.Equal(input.ErrorCount, group.Count);
        Assert.Equal(1UL << SyntheticErrorSets.StuckBit, group.BitMaskUnion);

        CauseHints result = ErrorAnalyzer.Analyze(input);

        CauseHint top = AssertTopRanked(result, CauseCategory.RankOrDimm);
        AssertClusterEvidence(top);

        // One fixed bit at one word is no lane-wide margin: the bit-lane category may carry its step prior, never a
        // signature score above it.
        Assert.All(result.Items, item =>
        {
            if (item.Category == CauseCategory.VoltageSignalIntegrity)
            {
                Assert.True(
                    item.Score <= AnalyzerThresholds.PriorWeight,
                    FormattableString.Invariant($"The bit-lane category scored {item.Score}: {Describe(result)}."));
            }
        });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Analyze_AddressRegionFlips_TopIsRankOrDimm(ulong seed)
    {
        AnalysisInput input = SyntheticErrorSets.AddressRegionFlips(seed);
        ulong start = SyntheticErrorSets.AddressRegionStart(seed);
        ulong length = SyntheticErrorSets.AddressRegionLength();

        Assert.Equal(input.TestedBytes / (ulong)SyntheticErrorSets.AddressRegionDivisor, length);
        Assert.True(
            length < input.RegionWindowBytes,
            FormattableString.Invariant(
                $"The injected range of {length} bytes is not smaller than one region window of {input.RegionWindowBytes}."));
        Assert.True(start + length <= input.TestedBytes, "The injected range extends past the tested region.");
        Assert.All(input.Errors, error => Assert.InRange(error.RegionOffset, start, start + length - 1));
        Assert.Equal(input.ErrorCount, ErrorGrouping.CountDistinctAddresses(input.Errors));

        CauseHints result = ErrorAnalyzer.Analyze(input);

        CauseHint top = AssertTopRanked(result, CauseCategory.RankOrDimm);
        AssertClusterEvidence(top);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Analyze_DelayDependentFlips_TopIsRefreshRetention(ulong seed)
    {
        AnalysisInput input = SyntheticErrorSets.DelayDependentFlips(seed);
        TimeSpan delayThreshold = TimeSpan.FromMilliseconds(SyntheticErrorSets.DelayThresholdMilliseconds);

        Assert.All(input.Errors, error =>
        {
            Assert.Equal(AccessType.VerifyAfterHold, error.AccessType);
            Assert.True(
                error.TimeSinceWrite >= delayThreshold,
                FormattableString.Invariant($"Record {error.Sequence} is only {error.TimeSinceWrite} old."));
            StepAttributes? step = input.StepOf(error);
            Assert.NotNull(step);
            Assert.True(step.HasHold, $"Record {error.Sequence} belongs to '{step.Name}', a step without a hold.");
        });
        Assert.All(input.Steps, outcome =>
        {
            if (outcome.Attributes.HasHold)
            {
                Assert.True(outcome.Errors > 0, $"Hold step '{outcome.Attributes.Name}' found no error.");
            }
            else
            {
                Assert.Equal(0UL, outcome.Errors);
                Assert.True(
                    outcome.PassesCompleted >= 1,
                    $"Step '{outcome.Attributes.Name}' completed no pass, so it is not a clean step.");
            }
        });

        CauseHints result = ErrorAnalyzer.Analyze(input);

        AssertTopRanked(result, CauseCategory.RefreshRetention);
    }

    // The re-runs under further seeds guard against a lucky seed only if each seed changes what is injected.
    [Theory]
    [MemberData(nameof(FurtherSeeds))]
    public void FaultClassSets_FurtherSeed_ChangeInjectedOffsets(ulong seed)
    {
        ulong defaultSeed = SyntheticErrorSets.DefaultSeed;

        Assert.NotEqual(
            Offsets(SyntheticErrorSets.SingleBitFlipsSpread(MemoryType.DDR4, defaultSeed)),
            Offsets(SyntheticErrorSets.SingleBitFlipsSpread(MemoryType.DDR4, seed)));
        Assert.NotEqual(
            Offsets(SyntheticErrorSets.StuckBitAtOneWord(defaultSeed)),
            Offsets(SyntheticErrorSets.StuckBitAtOneWord(seed)));
        Assert.NotEqual(
            Offsets(SyntheticErrorSets.AddressRegionFlips(defaultSeed)),
            Offsets(SyntheticErrorSets.AddressRegionFlips(seed)));
        Assert.NotEqual(
            Offsets(SyntheticErrorSets.DelayDependentFlips(defaultSeed)),
            Offsets(SyntheticErrorSets.DelayDependentFlips(seed)));
    }

    // ---- Shared assertions ----

    /// <summary>
    /// Asserts that <paramref name="expected"/> ranks first by a strictly higher score and likelihood than every other
    /// hint, that its own signature rather than a step prior decides it, that it carries its suggestion and an evidence
    /// line, that the likelihoods sum to 100, that the UI subset starts with it, and that the result carries the hint
    /// label.
    /// </summary>
    private static CauseHint AssertTopRanked(CauseHints result, CauseCategory expected)
    {
        Assert.Equal(CauseHints.HintLabel, result.Label);
        Assert.Equal(ExpectedHintLabel, result.Label);
        Assert.NotEmpty(result.Items);

        CauseHint top = result.Items[0];
        string ranking = Describe(result);
        Assert.True(top.Category == expected, $"Expected {expected} to rank first; ranking: {ranking}.");
        Assert.Equal(1, top.Rank);

        // A tie decided by the fixed tie-break order would not show that the signature ranks the expected cause.
        Assert.All(result.Items.Skip(1), other =>
        {
            Assert.True(other.Score < top.Score, $"{other.Category} ties or outscores {expected}; ranking: {ranking}.");
            Assert.True(
                other.LikelihoodPercent < top.LikelihoodPercent,
                $"{other.Category} is as likely as {expected}; ranking: {ranking}.");
        });

        // Only a signature can score above the prior weight, so the top score shows the fault's own signature won.
        Assert.True(
            top.Score > AnalyzerThresholds.PriorWeight,
            $"{expected} ranks first on its step prior alone; ranking: {ranking}.");
        Assert.Equal(Suggestions.For(expected), top.Suggestion);
        Assert.False(string.IsNullOrWhiteSpace(top.Evidence), $"{expected} has no evidence line.");
        Assert.Equal(100d, result.Items.Sum(static item => item.LikelihoodPercent), LikelihoodSumTolerance);

        IReadOnlyList<CauseHint> shown = result.Top();
        Assert.InRange(shown.Count, 1, AnalyzerThresholds.DisplayedHintCount);
        Assert.Equal(expected, shown[0].Category);

        return top;
    }

    private static void AssertClusterEvidence(CauseHint hint)
    {
        Assert.Contains(ClusterEvidencePhrase, hint.Evidence);
        Assert.DoesNotContain("rank", hint.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DIMM", hint.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private static TheoryData<MemoryType, ulong> CreateLaneCases()
    {
        var cases = new TheoryData<MemoryType, ulong>();
        foreach (MemoryType memoryType in _laneMemoryTypes)
        {
            foreach (ulong seed in _allSeeds)
            {
                cases.Add(memoryType, seed);
            }
        }

        return cases;
    }

    private static string Describe(CauseHints result) => string.Join(
        "; ",
        result.Items.Select(static item => FormattableString.Invariant(
            $"{item.Rank}. {item.Category} {item.Score} ({item.Evidence})")));

    private static ulong OffsetSpan(AnalysisInput input) =>
        input.Errors.Max(static error => error.RegionOffset) - input.Errors.Min(static error => error.RegionOffset);

    private static int[] ErrorStepIndices(AnalysisInput input) =>
        input.Errors.Select(static error => error.StepIndex).Distinct().Order().ToArray();

    private static ulong[] Offsets(AnalysisInput input) =>
        input.Errors.Select(static error => error.RegionOffset).ToArray();
}
