using System.Globalization;
using RamQuick.Analysis.Rules;
using RamQuick.Core;
using static System.FormattableString;
using static RamQuick.Analysis.Tests.SyntheticErrorSets;

namespace RamQuick.Analysis.Tests;

/// <summary>
/// Tests for <see cref="ErrorAnalyzer"/> on synthetic error sets: the six signature heuristics and their evidence
/// minimums, the step-mechanism prior, ranking by the larger of signature and prior, the keep floor, likelihood
/// normalisation, the tie-break order, the hint label, the suggestion texts and the analyzer's purity.
/// </summary>
/// <remarks>
/// Expected scores are written in terms of <see cref="AnalyzerThresholds"/> constants and of shares of the analysed
/// records; only <see cref="Thresholds_MatchSpecifiedValues"/> pins the constants themselves, so retuning a threshold
/// is a visible, deliberate change. The injected fault classes are asserted end to end in
/// <c>InjectedSignatureTests</c>; here they only join the named sets every ranking invariant is checked on.
/// </remarks>
public sealed class ErrorAnalyzerTests
{
    private const int ScorePrecision = 9;
    private const double LikelihoodTolerance = 1e-9;
    private const string QuickProgressionPrefix = "QuickProgression";
    private const string IsolatedPrefix = "SingleErrorIsolated";

    private static readonly ISignatureRule _bitLaneRule = new BitLaneRule();
    private static readonly ISignatureRule _holdRule = new HoldRule();
    private static readonly ISignatureRule _bankSwitchRule = new BankSwitchRule();
    private static readonly ISignatureRule _turnaroundRule = new TurnaroundRule();
    private static readonly ISignatureRule _loadRule = new LoadRule();
    private static readonly ISignatureRule _clusterRule = new ClusterRule();

    // Declared before the theory data built from it: static initialisers run in textual order.
    private static readonly IReadOnlyDictionary<string, Func<AnalysisInput>> _namedSets = CreateNamedSets();

    private static readonly string[] _certaintyWording =
        ["definitely", "certainly", "is caused by", "the cause is", "diagnosed"];

    /// <summary>Every named synthetic set, so ranking invariants are checked on each.</summary>
    public static TheoryData<string> NamedSets { get; } = new(_namedSets.Keys);

    /// <summary>The ten single-bit mechanisms with the category the specification maps each to.</summary>
    public static TheoryData<InstabilityMechanism, CauseCategory> MechanismCategories { get; } = new()
    {
        { InstabilityMechanism.VddVddqVoltage, CauseCategory.VoltageSignalIntegrity },
        { InstabilityMechanism.SignalIntegrity, CauseCategory.VoltageSignalIntegrity },
        { InstabilityMechanism.SocImcVoltage, CauseCategory.ImcPowerDelivery },
        { InstabilityMechanism.RowTimings, CauseCategory.RowBankTiming },
        { InstabilityMechanism.ActivationWindow, CauseCategory.RowBankTiming },
        { InstabilityMechanism.BankRankChannelSwitching, CauseCategory.RowBankTiming },
        { InstabilityMechanism.RefreshTimings, CauseCategory.RefreshRetention },
        { InstabilityMechanism.TemperatureSensitivity, CauseCategory.RefreshRetention },
        { InstabilityMechanism.WriteTimings, CauseCategory.WriteReadTurnaround },
        { InstabilityMechanism.ReadWriteTurnaround, CauseCategory.WriteReadTurnaround },
    };

    /// <summary>Every cause category.</summary>
    public static TheoryData<CauseCategory> Categories { get; } = new(Enum.GetValues<CauseCategory>());

    /// <summary>The positions of <see cref="ErrorAnalyzer.Rules"/>.</summary>
    public static TheoryData<int> RuleIndices { get; } = new(Enumerable.Range(0, ErrorAnalyzer.Rules.Count));

    // ---- Constants and catalogue ----

    [Fact]
    public void Thresholds_MatchSpecifiedValues()
    {
        Assert.Equal(0.8, AnalyzerThresholds.DominantLaneShareMinimum);
        Assert.Equal(16, AnalyzerThresholds.RegionWindowDivisor);
        Assert.Equal(64, AnalyzerThresholds.LaneModulo);
        Assert.Equal(32, AnalyzerThresholds.Ddr5LaneModulo);
        Assert.Equal(0.3, AnalyzerThresholds.PriorWeight);
        Assert.Equal(0.05, AnalyzerThresholds.KeepFloor);
        Assert.Equal(0.5, AnalyzerThresholds.RandomBlocksWeight);
        Assert.Equal(2, AnalyzerThresholds.BitLaneMinimumDistinctWords);
        Assert.Equal(3, AnalyzerThresholds.ClusterMinimumErrors);
        Assert.Equal(2, AnalyzerThresholds.ClusterMinimumErrorsAtOneWord);
        Assert.Equal(1, AnalyzerThresholds.MinimumHoldSteps);
        Assert.Equal(1, AnalyzerThresholds.MinimumCleanContrastSteps);
        Assert.Equal(3, AnalyzerThresholds.DisplayedHintCount);
        Assert.Equal(ErrorGrouping.StoredDetailCap, AnalyzerThresholds.MaxAnalysedRecords);
        Assert.Equal(10_000, AnalyzerThresholds.MaxAnalysedRecords);
        Assert.InRange(AnalyzerThresholds.ScoreDecimals, 2, 15);
    }

    [Fact]
    public void Rules_SixRulesInEvaluationOrder_EachScoresItsOwnCategory()
    {
        CauseCategory[] expected =
        [
            CauseCategory.VoltageSignalIntegrity,
            CauseCategory.RefreshRetention,
            CauseCategory.RowBankTiming,
            CauseCategory.WriteReadTurnaround,
            CauseCategory.ImcPowerDelivery,
            CauseCategory.RankOrDimm,
        ];

        Assert.Collection(
            ErrorAnalyzer.Rules,
            rule => Assert.IsType<BitLaneRule>(rule),
            rule => Assert.IsType<HoldRule>(rule),
            rule => Assert.IsType<BankSwitchRule>(rule),
            rule => Assert.IsType<TurnaroundRule>(rule),
            rule => Assert.IsType<LoadRule>(rule),
            rule => Assert.IsType<ClusterRule>(rule));
        Assert.Equal(expected, ErrorAnalyzer.Rules.Select(rule => rule.Category));
        Assert.Equal(expected, new[] { _bitLaneRule, _holdRule, _bankSwitchRule, _turnaroundRule, _loadRule, _clusterRule }
            .Select(rule => rule.Category));
        Assert.Equal(Enum.GetValues<CauseCategory>().Order(), expected.Order());
    }

    [Fact]
    public void TieBreakOrder_IsSpecifiedOrder_ListingEveryCategoryOnce()
    {
        CauseCategory[] expected =
        [
            CauseCategory.RefreshRetention,
            CauseCategory.WriteReadTurnaround,
            CauseCategory.RowBankTiming,
            CauseCategory.ImcPowerDelivery,
            CauseCategory.RankOrDimm,
            CauseCategory.VoltageSignalIntegrity,
        ];

        Assert.Equal(expected, ErrorAnalyzer.TieBreakOrder);
        Assert.Equal(Enum.GetValues<CauseCategory>().Order(), ErrorAnalyzer.TieBreakOrder.Order());
    }

    [Fact]
    public void Catalogues_AreReadOnly()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<ISignatureRule>)ErrorAnalyzer.Rules).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CauseCategory>)ErrorAnalyzer.TieBreakOrder).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<InstabilityMechanism, CauseCategory>)MechanismCategoryMap.Entries).Clear());
    }

    [Theory]
    [MemberData(nameof(RuleIndices))]
    public void Rule_NullInput_Throws(int ruleIndex)
    {
        ISignatureRule rule = ErrorAnalyzer.Rules[ruleIndex];

        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!));
    }

    [Theory]
    [MemberData(nameof(RuleIndices))]
    public void Rule_NoErrorRecords_DoesNotFire(int ruleIndex)
    {
        ISignatureRule rule = ErrorAnalyzer.Rules[ruleIndex];
        AnalysisInput input = Input([], StandardSteps.Select(step => Clean(step)));

        Assert.Equal(RuleResult.NotFired, rule.Evaluate(input));
    }

    [Theory]
    [MemberData(nameof(RuleIndices))]
    public void Rule_SameInputTwice_ReturnsEqualResults(int ruleIndex)
    {
        ISignatureRule rule = ErrorAnalyzer.Rules[ruleIndex];
        AnalysisInput input = BankSwitchMixedShares();

        Assert.Equal(rule.Evaluate(input), rule.Evaluate(input));
        Assert.Equal(rule.Evaluate(input), rule.Evaluate(BankSwitchMixedShares()));
    }

    // ---- Bit-lane rule: VoltageSignalIntegrity ----

    [Fact]
    public void BitLaneRule_SingleDistinctWord_DoesNotFire()
    {
        AnalysisInput input = BitLaneAllAtOneWord();

        AssertNotFired(_bitLaneRule.Evaluate(input));
        CauseHints hints = ErrorAnalyzer.Analyze(input);
        CauseHint voltage = HintFor(hints, CauseCategory.VoltageSignalIntegrity);
        Assert.Equal(AnalyzerThresholds.PriorWeight, voltage.Score, ScorePrecision);
        Assert.Contains("steps targeting this cause", voltage.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void BitLaneRule_SpanBelowOneWindow_DoesNotFire()
    {
        AnalysisInput input = BitLaneSpanBelowWindow();

        Assert.True(input.Errors.Max(e => e.RegionOffset) - input.Errors.Min(e => e.RegionOffset) < input.RegionWindowBytes);
        AssertNotFired(_bitLaneRule.Evaluate(input));
        Assert.Equal(AnalyzerThresholds.PriorWeight, HintFor(ErrorAnalyzer.Analyze(input), CauseCategory.VoltageSignalIntegrity).Score, ScorePrecision);
    }

    [Fact]
    public void BitLaneRule_SpanExactlyOneWindow_Fires()
    {
        AnalysisInput input = BitLaneSpanAtWindow();
        string expectedEvidence = Invariant($"100 % of errors on bit {InjectedFlipBit} across 2 addresses");

        Assert.Equal(input.RegionWindowBytes, input.Errors.Max(e => e.RegionOffset) - input.Errors.Min(e => e.RegionOffset));
        AssertFired(_bitLaneRule.Evaluate(input), 1.0, expectedEvidence);
        CauseHint top = ErrorAnalyzer.Analyze(input).Items[0];
        Assert.Equal(CauseCategory.VoltageSignalIntegrity, top.Category);
        Assert.Equal(1.0, top.Score, ScorePrecision);
        Assert.Equal(expectedEvidence, top.Evidence);
    }

    [Theory]
    [InlineData(79, 100, false)]
    [InlineData(80, 100, true)]
    [InlineData(4, 5, true)]
    [InlineData(20, 20, true)]
    public void BitLaneRule_DominantLaneShare_ScoresShareOnlyFromMinimum(int dominantCount, int totalCount, bool scoresShare)
    {
        AnalysisInput input = BitLaneDominantShare(dominantCount, totalCount);
        double share = (double)dominantCount / totalCount;
        string expectedEvidence = Invariant(
            $"{dominantCount * 100 / totalCount} % of errors on bit {InjectedFlipBit} across {dominantCount} addresses");

        Assert.Equal(scoresShare, share >= AnalyzerThresholds.DominantLaneShareMinimum);
        AssertFired(_bitLaneRule.Evaluate(input), scoresShare ? share : 0d, expectedEvidence);
        double? analysed = ScoreOf(ErrorAnalyzer.Analyze(input), CauseCategory.VoltageSignalIntegrity);
        Assert.Equal(Math.Max(scoresShare ? share : 0d, AnalyzerThresholds.PriorWeight), analysed.GetValueOrDefault(), ScorePrecision);
    }

    [Fact]
    public void BitLaneRule_SpecificationExample_ReportsSpecifiedEvidence()
    {
        AnalysisInput input = AapBitLaneExample();
        const string ExpectedEvidence = "92 % of errors on bit 17 across 41 addresses";

        AssertFired(_bitLaneRule.Evaluate(input), 0.92, ExpectedEvidence);
        CauseHint top = ErrorAnalyzer.Analyze(input).Items[0];
        Assert.Equal(CauseCategory.VoltageSignalIntegrity, top.Category);
        Assert.Equal(ExpectedEvidence, top.Evidence);
    }

    [Fact]
    public void BitLaneRule_Ddr4_KeepsBits17And49InSeparateLanes()
    {
        AnalysisInput input = Ddr5LaneSet(MemoryType.DDR4);

        Assert.Equal(AnalyzerThresholds.LaneModulo, input.LaneModulo);
        AssertFired(_bitLaneRule.Evaluate(input), 0d, Invariant($"50 % of errors on bit {InjectedFlipBit} across 10 addresses"));
        Assert.Equal(AnalyzerThresholds.PriorWeight, HintFor(ErrorAnalyzer.Analyze(input), CauseCategory.VoltageSignalIntegrity).Score, ScorePrecision);
    }

    [Fact]
    public void BitLaneRule_Ddr5_FoldsBits17And49IntoOneLane()
    {
        AnalysisInput input = Ddr5LaneSet(MemoryType.DDR5);
        string expectedEvidence = Invariant($"100 % of errors on bit {InjectedFlipBit} or {Ddr5PartnerBit} across 20 addresses");

        Assert.Equal(AnalyzerThresholds.Ddr5LaneModulo, input.LaneModulo);
        AssertFired(_bitLaneRule.Evaluate(input), 1.0, expectedEvidence);
        CauseHint top = ErrorAnalyzer.Analyze(input).Items[0];
        Assert.Equal(CauseCategory.VoltageSignalIntegrity, top.Category);
        Assert.Equal(expectedEvidence, top.Evidence);
    }

    [Theory]
    [InlineData(MemoryType.DDR4, false)]
    [InlineData(MemoryType.DDR5, true)]
    [InlineData(MemoryType.Other, false)]
    [InlineData(MemoryType.Unknown, false)]
    [InlineData((MemoryType)99, false)]
    public void LaneModulo_FollowsMemoryType(MemoryType memoryType, bool isDdr5)
    {
        int expected = isDdr5 ? AnalyzerThresholds.Ddr5LaneModulo : AnalyzerThresholds.LaneModulo;

        Assert.Equal(expected, AnalyzerThresholds.LaneModuloFor(memoryType));
        Assert.Equal(expected, Input([], [], memoryType: memoryType).LaneModulo);
    }

    [Fact]
    public void BitLaneRule_RecordsWithoutFlippedBits_FireWithZeroScore()
    {
        ErrorRecord first = Error(Q1, 0, [InjectedFlipBit]);
        ErrorRecord second = Error(Q1, WindowBytes(RegionBytes512MiB), [InjectedFlipBit], sequence: 1);
        AnalysisInput input = Input(
            [first with { Actual = first.Expected }, second with { Actual = second.Expected }],
            [Failing(Q1, 2)]);

        AssertFired(_bitLaneRule.Evaluate(input), 0d, "No data bit was flipped by the analysed errors");
        CauseHint voltage = HintFor(ErrorAnalyzer.Analyze(input), CauseCategory.VoltageSignalIntegrity);
        Assert.Equal(AnalyzerThresholds.PriorWeight, voltage.Score, ScorePrecision);
        Assert.Contains("steps targeting this cause", voltage.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void BitLaneRule_RegionWindowZero_DoesNotFire()
    {
        AnalysisInput input = ClusterRegionBelowDivisor();

        Assert.Equal(0UL, input.RegionWindowBytes);
        AssertNotFired(_bitLaneRule.Evaluate(input));
    }

    // ---- Hold rule: RefreshRetention ----

    [Fact]
    public void HoldRule_NoHoldStepRan_DoesNotFire()
    {
        AssertNotFired(_holdRule.Evaluate(HoldWithoutHoldStep()));

        // A record claiming a verification after a hold cannot fire the rule when no hold step ran.
        AnalysisInput claimed = Input(
            [Error(Q6, SingleErrorOffset, [SingleErrorBit], accessType: AccessType.VerifyAfterHold)],
            [Failing(Q6, 1)]);
        AssertNotFired(_holdRule.Evaluate(claimed));
        Assert.Null(ScoreOf(ErrorAnalyzer.Analyze(claimed), CauseCategory.RefreshRetention));
    }

    [Fact]
    public void HoldRule_HoldStepRanWithoutHoldErrors_FiresWithZeroScore()
    {
        AnalysisInput input = HoldWithHoldStepRun();

        AssertFired(_holdRule.Evaluate(input), 0d, "0 % of errors appeared on verification after an idle hold");
        Assert.Null(ScoreOf(ErrorAnalyzer.Analyze(input), CauseCategory.RefreshRetention));
    }

    [Fact]
    public void HoldRule_HoldShare_ScoresShareOfVerifyAfterHold()
    {
        AnalysisInput mixed = BankSwitchMixedShares();
        AnalysisInput holdOnly = ClusterTieWithHold();

        Assert.Equal(0.2, mixed.ShareOfErrors(e => e.AccessType == AccessType.VerifyAfterHold), ScorePrecision);
        AssertFired(_holdRule.Evaluate(mixed), 0.2, "20 % of errors appeared on verification after an idle hold");
        AssertFired(_holdRule.Evaluate(holdOnly), 1.0, "100 % of errors appeared on verification after an idle hold");
        Assert.Equal(1.0, ScoreOf(ErrorAnalyzer.Analyze(holdOnly), CauseCategory.RefreshRetention).GetValueOrDefault(), ScorePrecision);
    }

    [Fact]
    public void HoldRule_EighthOfErrorsAfterHold_RoundsEvidencePercentHalfAwayFromZero()
    {
        var errors = new List<ErrorRecord> { Error(Q5, 0, [SingleErrorBit]) };
        ulong[] offsets = EvenOffsets(7, RegionBytes512MiB, WindowBytes(RegionBytes512MiB));
        for (int k = 0; k < offsets.Length; k++)
        {
            errors.Add(Error(Q6, offsets[k], [SecondErrorBit], sequence: (ulong)(k + 1)));
        }

        AnalysisInput input = Input(errors, [Failing(Q5, 1), Failing(Q6, 7)]);

        // 1/8 is 12.5 % exactly; half away from zero gives 13, banker's rounding would give 12.
        AssertFired(_holdRule.Evaluate(input), 0.125, "13 % of errors appeared on verification after an idle hold");
    }

    // ---- Bank-switching rule: RowBankTiming ----

    [Theory]
    [InlineData(nameof(BankSwitchSequentialFailing), 0.5)]
    [InlineData(nameof(BankSwitchSequentialWithoutPass), 1.0)]
    [InlineData(nameof(BankSwitchSequentialTruncated), 1.0)]
    public void BankSwitchRule_NoCleanCompletedSequentialStep_DoesNotFire(string setName, double rowTimingPriorShare)
    {
        AnalysisInput input = NamedSet(setName);

        AssertNotFired(_bankSwitchRule.Evaluate(input));
        Assert.Equal(
            AnalyzerThresholds.PriorWeight * rowTimingPriorShare,
            HintFor(ErrorAnalyzer.Analyze(input), CauseCategory.RowBankTiming).Score,
            ScorePrecision);
    }

    [Fact]
    public void BankSwitchRule_SequentialStepWithTrueErrorsButNoStoredRecords_IsNotClean()
    {
        AnalysisInput input = BankSwitchSequentialTruncated();

        Assert.DoesNotContain(input.Errors, e => e.StepIndex == Q1.Index);
        Assert.True(Assert.Single(input.Steps, s => s.Attributes.Index == Q1.Index).Errors > 0);
        Assert.Equal(0, input.CountCleanCompletedSteps(s => s.Order == AccessOrder.SequentialAscending));
        AssertNotFired(_bankSwitchRule.Evaluate(input));
    }

    [Fact]
    public void BankSwitchRule_CleanAscendingStep_ScoresBankStrideShare()
    {
        AnalysisInput input = BankSwitchSequentialClean();
        const string ExpectedEvidence =
            "100 % of errors in bank-stride steps and 0 % in random-block steps, while a sequential step passed cleanly";

        AssertFired(_bankSwitchRule.Evaluate(input), 1.0, ExpectedEvidence);
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);
        Assert.Equal(CauseCategory.RowBankTiming, only.Category);
        Assert.Equal(1.0, only.Score, ScorePrecision);
        Assert.Equal(ExpectedEvidence, only.Evidence);
    }

    [Fact]
    public void BankSwitchRule_CleanDescendingStep_CountsAsContrast()
    {
        AnalysisInput input = Input([Error(Q2, SingleErrorOffset, [SingleErrorBit])], [Clean(S8), Failing(Q2, 1)]);

        Assert.Equal(AccessOrder.SequentialDescending, S8.Order);
        AssertFired(
            _bankSwitchRule.Evaluate(input),
            1.0,
            "100 % of errors in bank-stride steps and 0 % in random-block steps, while a sequential step passed cleanly");
    }

    [Fact]
    public void BankSwitchRule_MixedShares_WeightsRandomBlocksAndIgnoresHoldSteps()
    {
        AnalysisInput input = BankSwitchMixedShares();
        double expected = 0.6 + (AnalyzerThresholds.RandomBlocksWeight * 0.2);

        AssertFired(
            _bankSwitchRule.Evaluate(input),
            expected,
            "60 % of errors in bank-stride steps and 20 % in random-block steps, while a sequential step passed cleanly");
        Assert.Collection(
            ErrorAnalyzer.Analyze(input).Items,
            hint => AssertHint(hint, 1, CauseCategory.RowBankTiming, expected),
            hint => AssertHint(hint, 2, CauseCategory.RefreshRetention, 0.2),
            hint => AssertHint(hint, 3, CauseCategory.RankOrDimm, 0.1),
            hint => AssertHint(hint, 4, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight * 0.2));
    }

    [Fact]
    public void BankSwitchRule_ErrorsOnlyInHoldOrRandomBlockSteps_ScoreByWeight()
    {
        AnalysisInput holdOnly = Input([Error(Q5, SingleErrorOffset, [SingleErrorBit])], [Clean(Q1), Failing(Q5, 1)]);
        AnalysisInput randomBlocksOnly = SingleErrorAfterCleanSteps(QuickSteps, Q6.Index);

        AssertFired(
            _bankSwitchRule.Evaluate(holdOnly),
            0d,
            "0 % of errors in bank-stride steps and 0 % in random-block steps, while a sequential step passed cleanly");
        AssertFired(
            _bankSwitchRule.Evaluate(randomBlocksOnly),
            AnalyzerThresholds.RandomBlocksWeight,
            "0 % of errors in bank-stride steps and 100 % in random-block steps, while a sequential step passed cleanly");
    }


    // ---- Turnaround rule: WriteReadTurnaround ----

    [Fact]
    public void TurnaroundRule_OnlyTurnaroundOrHoldStepsClean_DoesNotFire()
    {
        AnalysisInput input = TurnaroundOnlyIneligibleStepsClean();

        AssertNotFired(_turnaroundRule.Evaluate(input));
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);
        AssertHint(only, 1, CauseCategory.WriteReadTurnaround, AnalyzerThresholds.PriorWeight);
    }

    [Fact]
    public void TurnaroundRule_CleanContrastStep_ScoresTurnaroundReadShare()
    {
        AnalysisInput input = TurnaroundContrastClean();
        const string ExpectedEvidence = "100 % of errors on reads right after writes, while other steps passed cleanly";

        AssertFired(_turnaroundRule.Evaluate(input), 1.0, ExpectedEvidence);
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);
        AssertHint(only, 1, CauseCategory.WriteReadTurnaround, 1.0);
        Assert.Equal(ExpectedEvidence, only.Evidence);
    }

    [Fact]
    public void TurnaroundRule_PartialShare_ScoresShareOfTurnaroundReads()
    {
        ulong[] offsets = EvenOffsets(4, RegionBytes512MiB);
        AnalysisInput input = Input(
            [
                Error(Q3, offsets[0], [SingleErrorBit]),
                Error(Q6, offsets[1], [SecondErrorBit], sequence: 1),
                Error(Q6, offsets[2], [InjectedFlipBit], sequence: 2),
                Error(Q6, offsets[3], [StuckBit], sequence: 3),
            ],
            [Clean(Q1), Failing(Q3, 1), Failing(Q6, 3)]);

        AssertFired(
            _turnaroundRule.Evaluate(input),
            0.25,
            "25 % of errors on reads right after writes, while other steps passed cleanly");
    }

    [Fact]
    public void TurnaroundRule_ContrastCleanWithoutTurnaroundReads_FiresWithZeroScore()
    {
        AnalysisInput input = ClusterTieWithBankSwitch();

        AssertFired(_turnaroundRule.Evaluate(input), 0d, "0 % of errors on reads right after writes, while other steps passed cleanly");
        Assert.Null(ScoreOf(ErrorAnalyzer.Analyze(input), CauseCategory.WriteReadTurnaround));
    }

    // ---- Load rule: ImcPowerDelivery ----

    [Fact]
    public void LoadRule_HalfStepNotClean_DoesNotFire()
    {
        AnalysisInput failing = LoadHalfFailing();
        AnalysisInput noPass = Input([Error(Q6, SingleErrorOffset, [SingleErrorBit])], [Failing(Q6, 1), CleanButNoPass(S11)]);

        AssertNotFired(_loadRule.Evaluate(failing));
        AssertNotFired(_loadRule.Evaluate(noPass));
        Assert.Equal(
            AnalyzerThresholds.PriorWeight * 0.5,
            HintFor(ErrorAnalyzer.Analyze(failing), CauseCategory.ImcPowerDelivery).Score,
            ScorePrecision);
    }

    [Fact]
    public void LoadRule_HalfStepClean_ScoresAllThreadShare()
    {
        AnalysisInput input = LoadHalfClean();
        string expectedEvidence = Invariant(
            $"100 % of errors with all {DefaultWorkers} workers loading memory, while a reduced-load step passed cleanly");

        Assert.Equal(DefaultWorkers, input.WorkerCount);
        AssertFired(_loadRule.Evaluate(input), 1.0, expectedEvidence);
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);
        AssertHint(only, 1, CauseCategory.ImcPowerDelivery, 1.0);
        Assert.Equal(expectedEvidence, only.Evidence);
    }

    [Fact]
    public void LoadRule_HoldStepErrors_AreExcludedFromShare()
    {
        AnalysisInput input = Input([Error(Q5, SingleErrorOffset, [SingleErrorBit])], [Failing(Q5, 1), Clean(S11)]);

        AssertFired(
            _loadRule.Evaluate(input),
            0d,
            Invariant($"0 % of errors with all {DefaultWorkers} workers loading memory, while a reduced-load step passed cleanly"));
    }

    [Fact]
    public void LoadRule_Evidence_NamesTheRunsWorkerCount()
    {
        const int Workers = 128;
        AnalysisInput input = Input(
            [Error(Q6, SingleErrorOffset, [SingleErrorBit], workers: Workers)],
            [Failing(Q6, 1), Clean(S11)],
            workers: Workers);

        RuleResult result = _loadRule.Evaluate(input);

        Assert.Contains(Invariant($"with all {Workers} workers loading memory"), result.Evidence, StringComparison.Ordinal);
    }

    // ---- Address-cluster rule: RankOrDimm ----

    [Fact]
    public void ClusterRule_TwoErrorsAtDistinctWords_DoesNotFire()
    {
        AnalysisInput input = ClusterTwoDistinctWords();

        AssertNotFired(_clusterRule.Evaluate(input));
        Assert.Null(ScoreOf(ErrorAnalyzer.Analyze(input), CauseCategory.RankOrDimm));
    }

    [Fact]
    public void ClusterRule_TwoErrorsAtOneWord_Fires()
    {
        AnalysisInput input = ClusterTwoAtOneWord();

        AssertFired(
            _clusterRule.Evaluate(input),
            1.0,
            "100 % of errors concentrated in < 0.01 % of the tested memory (virtual offsets)");
        Assert.Collection(
            ErrorAnalyzer.Analyze(input).Items,
            hint => AssertHint(hint, 1, CauseCategory.RankOrDimm, 1.0),
            hint => AssertHint(hint, 2, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight));
    }

    [Fact]
    public void ClusterRule_ThreeErrorsAtDistinctWords_ScoresDensestWindowShare()
    {
        AnalysisInput input = ClusterThreeDistinctWords();

        AssertFired(
            _clusterRule.Evaluate(input),
            1d / 3,
            "33 % of errors concentrated in < 0.01 % of the tested memory (virtual offsets)");
        Assert.Collection(
            ErrorAnalyzer.Analyze(input).Items,
            hint => AssertHint(hint, 1, CauseCategory.RankOrDimm, 1d / 3),
            hint => AssertHint(hint, 2, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight));
    }

    [Fact]
    public void ClusterRule_SpreadErrors_ScoreErrorsPerWindow()
    {
        Assert.Equal(0.1, _clusterRule.Evaluate(WeakSignatureUnderPrior()).Score, ScorePrecision);
        Assert.Equal(0.1, _clusterRule.Evaluate(StrongSignatureOverPrior()).Score, ScorePrecision);
        Assert.Equal(1.0, _clusterRule.Evaluate(AddressRegionFlips()).Score, ScorePrecision);
    }

    [Fact]
    public void ClusterRule_ExactExtent_ReportsComputedPercentage()
    {
        AnalysisInput input = ClusterExactPercent();

        AssertNotFired(_bitLaneRule.Evaluate(input));
        AssertFired(
            _clusterRule.Evaluate(input),
            1.0,
            "100 % of errors concentrated in 2.5 % of the tested memory (virtual offsets)");
    }

    [Fact]
    public void ClusterRule_TinyExtentOnLargestRegion_ReportsBelowSmallestShownPercentage()
    {
        AnalysisInput input = ClusterTinyExtent();

        Assert.Equal(RegionBytes192GiB, input.TestedBytes);
        AssertFired(
            _clusterRule.Evaluate(input),
            1.0,
            "100 % of errors concentrated in < 0.01 % of the tested memory (virtual offsets)");
    }

    [Fact]
    public void ClusterRule_EquallyDenseWindows_ReportsLowestOffsetWindow()
    {
        // Two clusters of three: a 25,000-byte one at the bottom of the region and a 24-byte one in the middle.
        ulong[] highOffsets = [500_016, 500_008, 500_000];
        ulong[] lowOffsets = [24_992, WordBytes, 0];
        AnalysisInput input = Input(
            highOffsets.Concat(lowOffsets).Select((offset, k) => Error(Q6, offset, [SingleErrorBit], sequence: (ulong)k)),
            [Failing(Q6, 6)],
            ExactPercentTestedBytes);

        AssertFired(
            _clusterRule.Evaluate(input),
            0.5,
            "50 % of errors concentrated in 2.5 % of the tested memory (virtual offsets)");
    }

    [Theory]
    [InlineData(nameof(ClusterTwoAtOneWord))]
    [InlineData(nameof(ClusterThreeDistinctWords))]
    [InlineData(nameof(ClusterExactPercent))]
    [InlineData(nameof(ClusterTinyExtent))]
    [InlineData(nameof(StuckBitAtOneWord))]
    [InlineData(nameof(AddressRegionFlips))]
    [InlineData(nameof(WeakSignatureUnderPrior))]
    public void ClusterRule_Evidence_SpeaksOfVirtualOffsetsAndNamesNoRankOrDimm(string setName)
    {
        RuleResult result = _clusterRule.Evaluate(NamedSet(setName));

        Assert.True(result.Fired);
        string evidence = Assert.IsType<string>(result.Evidence);
        Assert.EndsWith("of the tested memory (virtual offsets)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("rank", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DIMM", evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClusterRule_RegionWindowZero_DoesNotFire()
    {
        AnalysisInput input = ClusterRegionBelowDivisor();

        AssertNotFired(_clusterRule.Evaluate(input));
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);
        AssertHint(only, 1, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight);
    }

    // ---- RuleResult ----

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(0.42, 0.42)]
    public void RuleResult_Fire_ClampsScoreIntoUnitRange(double score, double expected)
    {
        RuleResult result = RuleResult.Fire(score, "evidence");

        Assert.True(result.Fired);
        Assert.Equal(expected, result.Score);
        Assert.Equal("evidence", result.Evidence);
    }

    [Fact]
    public void RuleResult_Fire_BlankEvidence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RuleResult.Fire(0.5, null!));
        Assert.Throws<ArgumentException>(() => RuleResult.Fire(0.5, string.Empty));
        Assert.Throws<ArgumentException>(() => RuleResult.Fire(0.5, "   "));
    }

    [Fact]
    public void RuleResult_NotFired_HasZeroScoreAndNoEvidence()
    {
        Assert.False(RuleResult.NotFired.Fired);
        Assert.Equal(0d, RuleResult.NotFired.Score);
        Assert.Null(RuleResult.NotFired.Evidence);
        Assert.Equal(new RuleResult(false, 0d, null), RuleResult.NotFired);
    }


    // ---- Step-mechanism prior ----

    [Theory]
    [MemberData(nameof(MechanismCategories))]
    public void MechanismCategoryMap_SingleMechanism_MapsToSpecifiedCategory(
        InstabilityMechanism mechanism,
        CauseCategory expected)
    {
        Assert.Equal(expected, MechanismCategoryMap.Map(mechanism));
        Assert.Equal(expected, Assert.Contains(mechanism, MechanismCategoryMap.Entries));
    }

    [Theory]
    [InlineData(InstabilityMechanism.None)]
    [InlineData(InstabilityMechanism.VddVddqVoltage | InstabilityMechanism.SignalIntegrity)]
    [InlineData(InstabilityMechanism.RefreshTimings | InstabilityMechanism.TemperatureSensitivity)]
    [InlineData((InstabilityMechanism)(1 << 20))]
    public void MechanismCategoryMap_NoneCombinedOrUndefined_MapsToNull(InstabilityMechanism mechanism)
    {
        Assert.Null(MechanismCategoryMap.Map(mechanism));
    }

    [Fact]
    public void MechanismCategoryMap_Entries_CoverEverySingleMechanismAndNeverRankOrDimm()
    {
        InstabilityMechanism[] singles = SingleMechanisms();

        Assert.Equal(10, MechanismCategoryMap.Entries.Count);
        Assert.Equal(singles.Length, MechanismCategoryMap.Entries.Count);
        Assert.All(singles, mechanism => Assert.Contains(mechanism, MechanismCategoryMap.Entries));
        Assert.DoesNotContain(InstabilityMechanism.None, MechanismCategoryMap.Entries.Keys);
        Assert.DoesNotContain(CauseCategory.RankOrDimm, MechanismCategoryMap.Entries.Values);
    }

    [Theory]
    [MemberData(nameof(MechanismCategories))]
    public void Analyze_SingleIsolatedError_RanksFailingStepsPriorCategoryAlone(
        InstabilityMechanism mechanism,
        CauseCategory expected)
    {
        StepAttributes step = RepresentativeStepFor(mechanism);

        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(SingleErrorIsolated(mechanism)).Items);

        Assert.Equal(mechanism, step.PrimaryMechanism);
        Assert.Equal(1, only.Rank);
        Assert.Equal(expected, only.Category);
        Assert.Equal(100d, only.LikelihoodPercent, ScorePrecision);
        if (step.HasHold)
        {
            // The refresh-hold step also fires the hold rule, in the same category.
            Assert.Equal(1.0, only.Score, ScorePrecision);
            Assert.Equal("100 % of errors appeared on verification after an idle hold", only.Evidence);
        }
        else
        {
            Assert.Equal(AnalyzerThresholds.PriorWeight, only.Score, ScorePrecision);
            Assert.Equal(Invariant($"100 % of errors in steps targeting this cause, e.g. '{step.Name}'"), only.Evidence);
        }
    }

    [Theory]
    [InlineData(1, CauseCategory.RowBankTiming)]
    [InlineData(2, CauseCategory.WriteReadTurnaround)]
    [InlineData(4, CauseCategory.RefreshRetention)]
    public void Analyze_QuickErrorAfterCleanSteps_FailingStepsSignatureScoresFully(int failingIndex, CauseCategory expected)
    {
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(SingleErrorAfterCleanSteps(QuickSteps, failingIndex)).Items);

        AssertHint(only, 1, expected, 1.0);
        Assert.Equal(100d, only.LikelihoodPercent, ScorePrecision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Analyze_QuickErrorAfterCleanStepsWithoutSignature_RanksVoltagePriorAlone(int failingIndex)
    {
        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(SingleErrorAfterCleanSteps(QuickSteps, failingIndex)).Items);

        AssertHint(only, 1, CauseCategory.VoltageSignalIntegrity, AnalyzerThresholds.PriorWeight);
    }

    [Fact]
    public void Analyze_QuickFullLoadStepError_RanksRandomBlockSwitchingAboveImcPrior()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(SingleErrorAfterCleanSteps(QuickSteps, Q6.Index));

        Assert.Collection(
            hints.Items,
            hint => AssertHint(hint, 1, CauseCategory.RowBankTiming, AnalyzerThresholds.RandomBlocksWeight),
            hint => AssertHint(hint, 2, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight));
    }

    [Fact]
    public void Analyze_StandardHeatSoakErrorAfterCleanContrast_RanksImcPowerDeliveryFirst()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(SingleErrorAfterCleanSteps(StandardSteps, S12.Index));

        Assert.Collection(
            hints.Items,
            hint => AssertHint(hint, 1, CauseCategory.ImcPowerDelivery, 1.0),
            hint => AssertHint(hint, 2, CauseCategory.RowBankTiming, AnalyzerThresholds.RandomBlocksWeight),
            hint => AssertHint(hint, 3, CauseCategory.RefreshRetention, AnalyzerThresholds.PriorWeight));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Analyze_QuickErrorAfterCleanSteps_KeepsFailingStepsPriorCategory(int failingIndex)
    {
        CauseCategory? mapped = MechanismCategoryMap.Map(QuickSteps[failingIndex].PrimaryMechanism);

        CauseHints hints = ErrorAnalyzer.Analyze(SingleErrorAfterCleanSteps(QuickSteps, failingIndex));

        Assert.NotNull(mapped);
        Assert.Contains(hints.Items, hint => hint.Category == mapped);
    }

    // ---- Category score: the larger of signature and prior ----

    [Fact]
    public void Analyze_StrongSignatureAgainstPrior_SignatureRanksFirst()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(StrongSignatureOverPrior());

        Assert.Collection(
            hints.Items,
            hint => AssertHint(hint, 1, CauseCategory.VoltageSignalIntegrity, 0.9),
            hint => AssertHint(hint, 2, CauseCategory.RowBankTiming, AnalyzerThresholds.PriorWeight),
            hint => AssertHint(hint, 3, CauseCategory.RankOrDimm, 0.1));
        Assert.True(hints.Items[0].Score > AnalyzerThresholds.PriorWeight);
        Assert.All(hints.Items.Skip(1), hint => Assert.True(hint.Score <= AnalyzerThresholds.PriorWeight));
    }

    [Fact]
    public void Analyze_WeakSignatureAgainstPrior_PriorRanksFirst()
    {
        AnalysisInput input = WeakSignatureUnderPrior();
        RuleResult cluster = _clusterRule.Evaluate(input);

        Assert.True(cluster.Fired);
        Assert.True(cluster.Score < AnalyzerThresholds.PriorWeight);
        RuleResult bitLane = _bitLaneRule.Evaluate(input);
        Assert.True(bitLane.Fired);
        Assert.Equal(0d, bitLane.Score);
        Assert.Collection(
            ErrorAnalyzer.Analyze(input).Items,
            hint => AssertHint(hint, 1, CauseCategory.ImcPowerDelivery, AnalyzerThresholds.PriorWeight),
            hint => AssertHint(hint, 2, CauseCategory.RankOrDimm, 0.1));
    }

    [Fact]
    public void Analyze_SignatureAndPriorInOneCategory_TakesLargerNeverSum()
    {
        AnalysisInput input = BankSwitchMixedShares();
        CauseHints hints = ErrorAnalyzer.Analyze(input);

        AssertLargerTermWins(input, hints, CauseCategory.RowBankTiming, _bankSwitchRule.Evaluate(input).Score);
        AssertLargerTermWins(input, hints, CauseCategory.RefreshRetention, _holdRule.Evaluate(input).Score);
    }

    [Fact]
    public void Analyze_PriorAboveFiredZeroSignature_ScoresPrior()
    {
        AnalysisInput input = HoldShareAtKeepFloor();
        RuleResult bitLane = _bitLaneRule.Evaluate(input);

        Assert.True(bitLane.Fired);
        Assert.Equal(0d, bitLane.Score);
        Assert.Equal(
            AnalyzerThresholds.PriorWeight * PriorShare(input, CauseCategory.VoltageSignalIntegrity),
            HintFor(ErrorAnalyzer.Analyze(input), CauseCategory.VoltageSignalIntegrity).Score,
            ScorePrecision);
    }

    [Fact]
    public void Analyze_EvidenceLine_ComesFromTheTermThatSetsTheScore()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(HoldShareAtKeepFloor());

        Assert.Equal(
            Invariant($"95 % of errors in steps targeting this cause, e.g. '{Q1.Name}'"),
            HintFor(hints, CauseCategory.VoltageSignalIntegrity).Evidence);
        Assert.Equal(
            "5 % of errors appeared on verification after an idle hold",
            HintFor(hints, CauseCategory.RefreshRetention).Evidence);
    }

    [Fact]
    public void Analyze_PriorEvidence_NamesStepOfEarliestErrorBySequence()
    {
        AnalysisInput input = Input(
            [
                Error(Q4, SingleErrorOffset, [SingleErrorBit], sequence: 7),
                Error(S8, SingleErrorOffset + NearbyWordDistance, [SecondErrorBit], sequence: 3),
            ],
            [Failing(Q4, 1), Failing(S8, 1)]);

        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);

        AssertHint(only, 1, CauseCategory.VoltageSignalIntegrity, AnalyzerThresholds.PriorWeight);
        Assert.Equal(Invariant($"100 % of errors in steps targeting this cause, e.g. '{S8.Name}'"), only.Evidence);
    }

    [Fact]
    public void Analyze_PriorEvidenceForBlankStepName_UsesStepAttributesName()
    {
        AnalysisInput input = Input(
            [Error(Q6, SingleErrorOffset, [SingleErrorBit]) with { StepName = " " }],
            [Failing(Q6, 1)]);

        CauseHint only = Assert.Single(ErrorAnalyzer.Analyze(input).Items);

        Assert.Equal(Invariant($"100 % of errors in steps targeting this cause, e.g. '{Q6.Name}'"), only.Evidence);
    }


    // ---- Keep floor and likelihood normalisation ----

    [Fact]
    public void Analyze_ScoreExactlyAtKeepFloor_IsKept()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(HoldShareAtKeepFloor());

        Assert.Collection(
            hints.Items,
            hint => AssertHint(hint, 1, CauseCategory.VoltageSignalIntegrity, AnalyzerThresholds.PriorWeight * 0.95),
            hint => AssertHint(hint, 2, CauseCategory.RankOrDimm, 0.1),
            hint => AssertHint(hint, 3, CauseCategory.RefreshRetention, AnalyzerThresholds.KeepFloor));
    }

    [Fact]
    public void Analyze_ScoreBelowKeepFloor_IsDropped()
    {
        AnalysisInput input = HoldShareBelowKeepFloor();
        RuleResult hold = _holdRule.Evaluate(input);

        Assert.True(hold.Fired);
        Assert.InRange(hold.Score, double.Epsilon, AnalyzerThresholds.KeepFloor - 1e-6);
        Assert.Collection(
            ErrorAnalyzer.Analyze(input).Items,
            hint => AssertHint(hint, 1, CauseCategory.VoltageSignalIntegrity, AnalyzerThresholds.PriorWeight * 0.96),
            hint => AssertHint(hint, 2, CauseCategory.RankOrDimm, 0.08));
    }

    [Fact]
    public void Analyze_RulesFiringWithZeroScore_AddNoHint()
    {
        AnalysisInput input = ClusterTieWithBankSwitch();

        Assert.True(_turnaroundRule.Evaluate(input).Fired);
        Assert.Equal(0d, _turnaroundRule.Evaluate(input).Score);
        Assert.DoesNotContain(ErrorAnalyzer.Analyze(input).Items, hint => hint.Category == CauseCategory.WriteReadTurnaround);
    }

    [Fact]
    public void Analyze_Likelihoods_AreScoresOverKeptTotal()
    {
        double prior = AnalyzerThresholds.PriorWeight * 0.2;
        double total = 0.2 + (5 * prior);

        Assert.Collection(
            ErrorAnalyzer.Analyze(FivePriorTie()).Items,
            hint => Assert.Equal(100d * 0.2 / total, hint.LikelihoodPercent, ScorePrecision),
            hint => Assert.Equal(100d * prior / total, hint.LikelihoodPercent, ScorePrecision),
            hint => Assert.Equal(100d * prior / total, hint.LikelihoodPercent, ScorePrecision),
            hint => Assert.Equal(100d * prior / total, hint.LikelihoodPercent, ScorePrecision),
            hint => Assert.Equal(100d * prior / total, hint.LikelihoodPercent, ScorePrecision),
            hint => Assert.Equal(100d * prior / total, hint.LikelihoodPercent, ScorePrecision));
        Assert.All(ErrorAnalyzer.Analyze(ThreeWayTie()).Items, hint => Assert.Equal(100d / 3, hint.LikelihoodPercent, ScorePrecision));
        Assert.All(ErrorAnalyzer.Analyze(ClusterTieWithHold()).Items, hint => Assert.Equal(50d, hint.LikelihoodPercent, ScorePrecision));
    }

    [Theory]
    [MemberData(nameof(NamedSets))]
    public void Analyze_NamedSet_RanksNormalisesAndLabelsEveryKeptHint(string setName)
    {
        CauseHints hints = ErrorAnalyzer.Analyze(NamedSet(setName));
        IReadOnlyList<CauseHint> items = hints.Items;
        double total = items.Sum(hint => hint.Score);

        Assert.Equal(CauseHints.HintLabel, hints.Label);
        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(hint => hint.Category).Distinct().Count());
        for (int i = 0; i < items.Count; i++)
        {
            CauseHint hint = items[i];
            Assert.Equal(i + 1, hint.Rank);
            Assert.True(hint.Score >= AnalyzerThresholds.KeepFloor, $"{hint.Category} scored {hint.Score}.");
            Assert.Equal(100d * hint.Score / total, hint.LikelihoodPercent, ScorePrecision);
            Assert.Equal(Suggestions.For(hint.Category), hint.Suggestion);
            Assert.False(string.IsNullOrWhiteSpace(hint.Evidence));
            if (i > 0)
            {
                CauseHint previous = items[i - 1];
                bool tieOrdered = previous.Score.Equals(hint.Score) && TiePosition(previous) < TiePosition(hint);
                Assert.True(previous.Score > hint.Score || tieOrdered, $"{previous.Category} must rank above {hint.Category}.");
            }
        }

        Assert.Equal(100d, items.Sum(hint => hint.LikelihoodPercent), LikelihoodTolerance);
    }

    // ---- Tie-break order ----

    [Theory]
    [InlineData(InstabilityMechanism.ReadWriteTurnaround, InstabilityMechanism.TemperatureSensitivity, CauseCategory.RefreshRetention, CauseCategory.WriteReadTurnaround)]
    [InlineData(InstabilityMechanism.TemperatureSensitivity, InstabilityMechanism.ReadWriteTurnaround, CauseCategory.RefreshRetention, CauseCategory.WriteReadTurnaround)]
    [InlineData(InstabilityMechanism.RowTimings, InstabilityMechanism.ReadWriteTurnaround, CauseCategory.WriteReadTurnaround, CauseCategory.RowBankTiming)]
    [InlineData(InstabilityMechanism.ReadWriteTurnaround, InstabilityMechanism.RowTimings, CauseCategory.WriteReadTurnaround, CauseCategory.RowBankTiming)]
    [InlineData(InstabilityMechanism.SocImcVoltage, InstabilityMechanism.RowTimings, CauseCategory.RowBankTiming, CauseCategory.ImcPowerDelivery)]
    [InlineData(InstabilityMechanism.RowTimings, InstabilityMechanism.SocImcVoltage, CauseCategory.RowBankTiming, CauseCategory.ImcPowerDelivery)]
    [InlineData(InstabilityMechanism.SignalIntegrity, InstabilityMechanism.SocImcVoltage, CauseCategory.ImcPowerDelivery, CauseCategory.VoltageSignalIntegrity)]
    [InlineData(InstabilityMechanism.SocImcVoltage, InstabilityMechanism.SignalIntegrity, CauseCategory.ImcPowerDelivery, CauseCategory.VoltageSignalIntegrity)]
    [InlineData(InstabilityMechanism.VddVddqVoltage, InstabilityMechanism.WriteTimings, CauseCategory.WriteReadTurnaround, CauseCategory.VoltageSignalIntegrity)]
    public void Analyze_PriorOnlyTie_BreaksBySpecifiedOrder(
        InstabilityMechanism firstMechanism,
        InstabilityMechanism secondMechanism,
        CauseCategory expectedFirst,
        CauseCategory expectedSecond)
    {
        AnalysisInput input = PriorOnlyTie(RepresentativeStepFor(firstMechanism), RepresentativeStepFor(secondMechanism));

        CauseHints hints = ErrorAnalyzer.Analyze(input);

        Assert.Collection(
            hints.Items,
            hint => AssertHint(hint, 1, expectedFirst, AnalyzerThresholds.PriorWeight * 0.5),
            hint => AssertHint(hint, 2, expectedSecond, AnalyzerThresholds.PriorWeight * 0.5));
        Assert.Equal(hints.Items[0].Score, hints.Items[1].Score);
        Assert.Equal(50d, hints.Items[0].LikelihoodPercent, ScorePrecision);
    }

    [Theory]
    [InlineData(nameof(ClusterTieWithHold), CauseCategory.RefreshRetention)]
    [InlineData(nameof(ClusterTieWithTurnaround), CauseCategory.WriteReadTurnaround)]
    [InlineData(nameof(ClusterTieWithBankSwitch), CauseCategory.RowBankTiming)]
    [InlineData(nameof(ClusterTieWithLoad), CauseCategory.ImcPowerDelivery)]
    public void Analyze_SignatureTiedWithCluster_EarlierCategoryRanksFirst(string setName, CauseCategory expectedFirst)
    {
        Assert.Collection(
            ErrorAnalyzer.Analyze(NamedSet(setName)).Items,
            hint => AssertHint(hint, 1, expectedFirst, 1.0),
            hint => AssertHint(hint, 2, CauseCategory.RankOrDimm, 1.0));
    }

    [Fact]
    public void Analyze_ClusterTiedWithBitLane_RankOrDimmRanksAboveVoltage()
    {
        Assert.Collection(
            ErrorAnalyzer.Analyze(ClusterTieWithBitLane()).Items,
            hint => AssertHint(hint, 1, CauseCategory.RankOrDimm, 4d / 5),
            hint => AssertHint(hint, 2, CauseCategory.VoltageSignalIntegrity, 4d / 5));
    }

    [Fact]
    public void Analyze_ThreeWayTie_RanksInSpecifiedOrder()
    {
        Assert.Collection(
            ErrorAnalyzer.Analyze(ThreeWayTie()).Items,
            hint => AssertHint(hint, 1, CauseCategory.RowBankTiming, 1.0),
            hint => AssertHint(hint, 2, CauseCategory.ImcPowerDelivery, 1.0),
            hint => AssertHint(hint, 3, CauseCategory.RankOrDimm, 1.0));
    }

    [Fact]
    public void Analyze_FiveWayPriorTie_RanksInSpecifiedOrderNotInputOrder()
    {
        double prior = AnalyzerThresholds.PriorWeight * 0.2;

        Assert.Collection(
            ErrorAnalyzer.Analyze(FivePriorTie()).Items,
            hint => AssertHint(hint, 1, CauseCategory.RankOrDimm, 0.2),
            hint => AssertHint(hint, 2, CauseCategory.RefreshRetention, prior),
            hint => AssertHint(hint, 3, CauseCategory.WriteReadTurnaround, prior),
            hint => AssertHint(hint, 4, CauseCategory.RowBankTiming, prior),
            hint => AssertHint(hint, 5, CauseCategory.ImcPowerDelivery, prior),
            hint => AssertHint(hint, 6, CauseCategory.VoltageSignalIntegrity, prior));
    }

    // ---- Top ----

    [Fact]
    public void Top_MoreKeptHintsThanDisplayed_ReturnsDisplayedCountInRankOrder()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(FivePriorTie());

        IReadOnlyList<CauseHint> top = hints.Top();

        Assert.True(hints.Items.Count > AnalyzerThresholds.DisplayedHintCount);
        Assert.Equal(AnalyzerThresholds.DisplayedHintCount, top.Count);
        Assert.Equal(hints.Items.Take(AnalyzerThresholds.DisplayedHintCount), top);
        Assert.Equal(hints.Items[0], Assert.Single(hints.Top(1)));
        Assert.Empty(hints.Top(0));
        Assert.Equal(hints.Items, hints.Top(hints.Items.Count + 1));
    }

    [Fact]
    public void Top_FewerKeptHintsThanDisplayed_ReturnsAll()
    {
        CauseHints hints = ErrorAnalyzer.Analyze(ClusterTieWithHold());

        Assert.True(hints.Items.Count < AnalyzerThresholds.DisplayedHintCount);
        Assert.Equal(hints.Items, hints.Top());
    }

    [Fact]
    public void Top_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CauseHints.Empty.Top(-1));
    }


    // ---- Hint label, suggestions and empty input ----

    [Fact]
    public void HintLabel_IsSpecifiedTextWithEmDash_OnEveryInstance()
    {
        Assert.Equal("Hint \u2014 not a diagnosis", CauseHints.HintLabel);
        Assert.Equal('\u2014', CauseHints.HintLabel[5]);
        Assert.Equal(CauseHints.HintLabel, CauseHints.Empty.Label);
        Assert.Equal(CauseHints.HintLabel, new CauseHints([]).Label);
        Assert.Equal(CauseHints.HintLabel, ErrorAnalyzer.Analyze(StrongSignatureOverPrior()).Label);
    }

    [Fact]
    public void Analyze_NoErrorRecords_ReturnsLabelledEmptyHints()
    {
        CauseHints afterCleanRun = ErrorAnalyzer.Analyze(Input([], StandardSteps.Select(step => Clean(step))));
        CauseHints withoutSteps = ErrorAnalyzer.Analyze(Input([], []));

        foreach (CauseHints hints in new[] { afterCleanRun, withoutSteps })
        {
            Assert.True(hints.IsEmpty);
            Assert.Empty(hints.Items);
            Assert.Empty(hints.Top());
            Assert.Equal(CauseHints.Empty.Label, hints.Label);
            Assert.Equal(CauseHints.Empty.Items, hints.Items);
        }
    }

    [Fact]
    public void Analyze_ErrorsFromStepsWithoutOutcome_ReturnLabelledEmptyHints()
    {
        AnalysisInput input = Input([Error(WithIndex(Q6, 42), SingleErrorOffset, [SingleErrorBit])], []);

        CauseHints hints = ErrorAnalyzer.Analyze(input);

        Assert.True(hints.IsEmpty);
        Assert.Equal(CauseHints.HintLabel, hints.Label);
    }

    [Fact]
    public void Analyze_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ErrorAnalyzer.Analyze(null!));
    }

    [Theory]
    [InlineData(CauseCategory.VoltageSignalIntegrity, "Errors keep hitting the same data bit. Try slightly more DRAM VDD/VDDQ, revisit drive strength or ODT (RTT), or lower the frequency one step.")]
    [InlineData(CauseCategory.RefreshRetention, "Errors appear only after memory sat idle. Lower tREFI, raise tRFC, or improve DIMM cooling.")]
    [InlineData(CauseCategory.RowBankTiming, "Errors appear under rapid row and bank switching. Loosen tRCD, tRP or tRAS, or tFAW/tRRD.")]
    [InlineData(CauseCategory.WriteReadTurnaround, "Errors appear when data is read right after being written. Loosen tWR, tWTR or tRTP (and tCWL).")]
    [InlineData(CauseCategory.ImcPowerDelivery, "Errors appear only with every core loading memory. Raise SoC/IMC voltage (VDDIO or VDD2) slightly, or check CPU power delivery.")]
    [InlineData(CauseCategory.RankOrDimm, "Errors concentrate in one part of memory. One rank or DIMM may be marginal; test the DIMMs one at a time or reseat them.")]
    public void Suggestions_For_ReturnsSpecifiedText(CauseCategory category, string expected)
    {
        Assert.Equal(expected, Suggestions.For(category));
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void Suggestions_SuggestionAndTitle_ArePresentAndStateNoCertainty(CauseCategory category)
    {
        string title = Suggestions.Title(category);

        Assert.False(string.IsNullOrWhiteSpace(title));
        AssertNoCertaintyWording(title);
        AssertNoCertaintyWording(Suggestions.For(category));
    }

    [Fact]
    public void Suggestions_Titles_AreDistinct()
    {
        CauseCategory[] categories = Enum.GetValues<CauseCategory>();

        Assert.Equal(categories.Length, categories.Select(Suggestions.Title).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Suggestions_UndefinedCategory_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Suggestions.For((CauseCategory)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Suggestions.Title((CauseCategory)99));
    }

    [Theory]
    [MemberData(nameof(NamedSets))]
    public void Analyze_NamedSet_LabelSuggestionsAndEvidenceStateNoCertainty(string setName)
    {
        CauseHints hints = ErrorAnalyzer.Analyze(NamedSet(setName));

        AssertNoCertaintyWording(hints.Label);
        Assert.All(hints.Items, hint =>
        {
            AssertNoCertaintyWording(hint.Suggestion);
            AssertNoCertaintyWording(hint.Evidence);
        });
    }

    // ---- Culture invariance and purity ----

    [Fact]
    public void Analyze_UnderGermanCulture_ProducesInvariantText()
    {
        string[] setNames =
        [
            nameof(AapBitLaneExample),
            nameof(ClusterExactPercent),
            nameof(LoadHalfClean),
            nameof(BankSwitchMixedShares),
            nameof(HoldShareAtKeepFloor),
        ];
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        IReadOnlyList<CauseHint>[] invariantRun;
        IReadOnlyList<CauseHint>[] germanRun;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            invariantRun = setNames.Select(name => ErrorAnalyzer.Analyze(NamedSet(name)).Items).ToArray();

            var german = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;
            germanRun = setNames.Select(name => ErrorAnalyzer.Analyze(NamedSet(name)).Items).ToArray();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        for (int i = 0; i < setNames.Length; i++)
        {
            Assert.Equal(invariantRun[i], germanRun[i]);
        }

        Assert.Equal("92 % of errors on bit 17 across 41 addresses", germanRun[0][0].Evidence);
        Assert.Equal("100 % of errors concentrated in 2.5 % of the tested memory (virtual offsets)", germanRun[1][0].Evidence);
    }

    [Theory]
    [MemberData(nameof(NamedSets))]
    public void Analyze_SameInputTwice_ReturnsEqualHints(string setName)
    {
        AnalysisInput input = NamedSet(setName);

        IReadOnlyList<CauseHint> first = ErrorAnalyzer.Analyze(input).Items;

        Assert.Equal(first, ErrorAnalyzer.Analyze(input).Items);
        Assert.Equal(first, ErrorAnalyzer.Analyze(NamedSet(setName)).Items);
    }

    [Fact]
    public void Analyze_InterleavedInputs_DoNotInfluenceEachOther()
    {
        AnalysisInput a = StrongSignatureOverPrior();
        AnalysisInput b = DelayDependentFlips();

        IReadOnlyList<CauseHint> first = ErrorAnalyzer.Analyze(a).Items;
        IReadOnlyList<CauseHint> other = ErrorAnalyzer.Analyze(b).Items;
        IReadOnlyList<CauseHint> third = ErrorAnalyzer.Analyze(a).Items;

        Assert.Equal(first, third);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Analyze_ConcurrentCallsOnOneInput_ReturnEqualHints()
    {
        AnalysisInput input = BankSwitchMixedShares();
        IReadOnlyList<CauseHint> expected = ErrorAnalyzer.Analyze(input).Items;
        var results = new IReadOnlyList<CauseHint>[32];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = TestContext.Current.CancellationToken,
        };

        Parallel.For(0, results.Length, options, i => results[i] = ErrorAnalyzer.Analyze(input).Items);

        Assert.All(results, result => Assert.Equal(expected, result));
    }

    [Fact]
    public void Analyze_LeavesInputUnchanged()
    {
        AnalysisInput input = BankSwitchMixedShares();
        ErrorRecord[] errorsBefore = input.Errors.ToArray();
        StepOutcome[] stepsBefore = input.Steps.ToArray();

        _ = ErrorAnalyzer.Analyze(input);

        Assert.Equal(errorsBefore, input.Errors);
        Assert.Equal(stepsBefore, input.Steps);
        Assert.Equal(RegionBytes512MiB, input.TestedBytes);
        Assert.Equal(DefaultWorkers, input.WorkerCount);
        Assert.Equal(MemoryType.DDR4, input.MemoryType);
    }


    // ---- AnalysisInput ----

    [Fact]
    public void AnalysisInput_DerivedQueries_ComputeOverStoredRecords()
    {
        ulong window = WindowBytes(RegionBytes512MiB);
        ErrorRecord inQ2 = Error(Q2, 0, [SingleErrorBit]);
        ErrorRecord alsoInQ2 = Error(Q2, window, [SecondErrorBit], sequence: 1);
        ErrorRecord inQ5 = Error(Q5, 2 * window, [InjectedFlipBit], sequence: 2);
        ErrorRecord orphan = Error(WithIndex(Q6, 42), 3 * window, [StuckBit], sequence: 3);
        var input = new AnalysisInput(
            [inQ2, alsoInQ2, inQ5, orphan],
            [Clean(Q1), Failing(Q2, 2), Incomplete(Q5, 1)],
            RegionBytes512MiB,
            DefaultWorkers,
            MemoryType.DDR5);

        Assert.Equal(4, input.ErrorCount);
        Assert.Equal(RegionBytes512MiB / AnalyzerThresholds.RegionWindowDivisor, input.RegionWindowBytes);
        Assert.Equal(RegionBytes512MiB, input.TestedBytes);
        Assert.Equal(DefaultWorkers, input.WorkerCount);
        Assert.Equal(MemoryType.DDR5, input.MemoryType);
        Assert.Equal(Q2, input.StepOf(inQ2));
        Assert.Equal(Q5, input.StepOf(inQ5));
        Assert.Null(input.StepOf(orphan));
        Assert.Equal(0.25, input.ShareOfErrors(e => e.AccessType == AccessType.VerifyAfterHold));
        Assert.Equal(0.5, input.ShareOfErrorsInSteps(s => s.Order == AccessOrder.BankStride));

        // A record whose step has no outcome counts in every denominator and in no step-based numerator.
        Assert.Equal(0.75, input.ShareOfErrorsInSteps(_ => true));
        Assert.Equal(0d, input.ShareOfErrorsInSteps(s => s.Threads == ThreadMode.Half));
        Assert.Equal(1, input.CountCleanCompletedSteps(_ => true));
        Assert.Equal(0, input.CountCleanCompletedSteps(s => s.Order == AccessOrder.BankStride));
        Assert.Equal(3, input.CountStepsRun(_ => true));
        Assert.Equal(1, input.CountStepsRun(s => s.HasHold));
    }

    [Fact]
    public void AnalysisInput_NoErrorRecords_SharesAreZero()
    {
        AnalysisInput input = Input([], [Clean(Q1)], testedBytes: 0);

        Assert.Equal(0, input.ErrorCount);
        Assert.Equal(0UL, input.RegionWindowBytes);
        Assert.Equal(0d, input.ShareOfErrors(_ => true));
        Assert.Equal(0d, input.ShareOfErrorsInSteps(_ => true));
        Assert.Equal(1, input.CountCleanCompletedSteps(_ => true));
    }

    [Fact]
    public void AnalysisInput_CopiesItsSequences_AndExposesReadOnlyLists()
    {
        var errors = new List<ErrorRecord> { Error(Q1, 0, [SingleErrorBit]) };
        var steps = new List<StepOutcome> { Failing(Q1, 1) };
        var input = new AnalysisInput(errors, steps, RegionBytes512MiB, DefaultWorkers, MemoryType.DDR4);

        errors.Add(Error(Q1, WordBytes, [SecondErrorBit], sequence: 1));
        steps.Add(Clean(Q2));

        Assert.Single(input.Errors);
        Assert.Single(input.Steps);
        Assert.Throws<NotSupportedException>(() => ((IList<ErrorRecord>)input.Errors).Add(errors[1]));
        Assert.Throws<NotSupportedException>(() => ((IList<StepOutcome>)input.Steps).Clear());
    }

    [Fact]
    public void AnalysisInput_RecordCount_IsBoundedByStoredDetailCap()
    {
        ErrorRecord error = Error(Q6, SingleErrorOffset, [SingleErrorBit]);
        StepOutcome[] steps = [Failing(Q6, 1)];

        var atCap = new AnalysisInput(
            Enumerable.Repeat(error, AnalyzerThresholds.MaxAnalysedRecords),
            steps,
            RegionBytes512MiB,
            DefaultWorkers,
            MemoryType.DDR4);

        Assert.Equal(AnalyzerThresholds.MaxAnalysedRecords, atCap.ErrorCount);
        Assert.Throws<ArgumentException>(() => new AnalysisInput(
            Enumerable.Repeat(error, AnalyzerThresholds.MaxAnalysedRecords + 1),
            steps,
            RegionBytes512MiB,
            DefaultWorkers,
            MemoryType.DDR4));
    }

    [Fact]
    public void AnalysisInput_InvalidArguments_Throw()
    {
        ErrorRecord error = Error(Q1, 0, [SingleErrorBit]);
        StepOutcome outcome = Failing(Q1, 1);
        const ulong Tested = RegionBytes512MiB;
        const MemoryType Ddr4 = MemoryType.DDR4;

        Assert.Throws<ArgumentNullException>(() => new AnalysisInput(null!, [outcome], Tested, 1, Ddr4));
        Assert.Throws<ArgumentNullException>(() => new AnalysisInput([error], null!, Tested, 1, Ddr4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalysisInput([error], [outcome], Tested, -1, Ddr4));
        Assert.Throws<ArgumentNullException>(() => new AnalysisInput([null!], [outcome], Tested, 1, Ddr4));
        Assert.Throws<ArgumentNullException>(() => new AnalysisInput([error], [null!], Tested, 1, Ddr4));
        Assert.Throws<ArgumentNullException>(
            () => new AnalysisInput([error], [outcome with { Attributes = null! }], Tested, 1, Ddr4));
        Assert.Throws<ArgumentException>(() => new AnalysisInput([error], [outcome, Clean(Q1)], Tested, 1, Ddr4));

        var input = new AnalysisInput([error], [outcome], Tested, 1, Ddr4);
        Assert.Throws<ArgumentNullException>(() => input.StepOf(null!));
        Assert.Throws<ArgumentNullException>(() => input.ShareOfErrors(null!));
        Assert.Throws<ArgumentNullException>(() => input.ShareOfErrorsInSteps(null!));
        Assert.Throws<ArgumentNullException>(() => input.CountCleanCompletedSteps(null!));
        Assert.Throws<ArgumentNullException>(() => input.CountStepsRun(null!));
    }

    // ---- CauseHint and CauseHints construction ----

    [Fact]
    public void CauseHint_OutOfRangeValues_Throw()
    {
        string suggestion = Suggestions.For(CauseCategory.RankOrDimm);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(0, CauseCategory.RankOrDimm, 0.5, 50, suggestion, "e"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(1, (CauseCategory)99, 0.5, 50, suggestion, "e"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(1, CauseCategory.RankOrDimm, 1.5, 50, suggestion, "e"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(1, CauseCategory.RankOrDimm, double.NaN, 50, suggestion, "e"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(1, CauseCategory.RankOrDimm, 0.5, 100.5, suggestion, "e"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CauseHint(1, CauseCategory.RankOrDimm, 0.5, -1, suggestion, "e"));
        Assert.Throws<ArgumentNullException>(() => new CauseHint(1, CauseCategory.RankOrDimm, 0.5, 50, null!, "e"));
        Assert.Throws<ArgumentException>(() => new CauseHint(1, CauseCategory.RankOrDimm, 0.5, 50, suggestion, " "));
    }

    [Fact]
    public void CauseHint_WithExpression_RevalidatesChangedValues()
    {
        CauseHint hint = Hint(1, CauseCategory.RankOrDimm, 0.5);

        Assert.Throws<ArgumentOutOfRangeException>(() => hint with { Rank = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => hint with { Category = (CauseCategory)(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => hint with { Score = -0.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => hint with { LikelihoodPercent = double.NaN });
        Assert.Throws<ArgumentException>(() => hint with { Suggestion = string.Empty });
        Assert.Throws<ArgumentNullException>(() => hint with { Evidence = null! });

        CauseHint changed = hint with { Score = 0.25, Evidence = "changed" };
        Assert.Equal(0.25, changed.Score);
        Assert.Equal("changed", changed.Evidence);
        Assert.NotEqual(hint, changed);
        Assert.Equal(hint, Hint(1, CauseCategory.RankOrDimm, 0.5));
    }

    [Fact]
    public void CauseHints_InvalidRanking_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CauseHints(null!));
        Assert.Throws<ArgumentException>(() => new CauseHints([null!]));
        Assert.Throws<ArgumentException>(() => new CauseHints([Hint(2, CauseCategory.RefreshRetention, 0.5)]));
        Assert.Throws<ArgumentException>(
            () => new CauseHints([Hint(1, CauseCategory.RefreshRetention, 0.5), Hint(2, CauseCategory.RefreshRetention, 0.4)]));
        Assert.Throws<ArgumentException>(
            () => new CauseHints([Hint(1, CauseCategory.RefreshRetention, 0.4), Hint(2, CauseCategory.RankOrDimm, 0.5)]));
    }

    [Fact]
    public void CauseHints_ValidRanking_IsCopiedAndReadOnly()
    {
        // Equal scores may come in either order: the tie-break order belongs to the analyzer.
        CauseHint[] source = [Hint(1, CauseCategory.RankOrDimm, 0.5), Hint(2, CauseCategory.RefreshRetention, 0.5)];
        var hints = new CauseHints(source);

        source[0] = Hint(1, CauseCategory.VoltageSignalIntegrity, 0.9);

        Assert.False(hints.IsEmpty);
        Assert.Equal(CauseHints.HintLabel, hints.Label);
        Assert.Equal(CauseCategory.RankOrDimm, hints.Items[0].Category);
        Assert.Throws<NotSupportedException>(() => ((IList<CauseHint>)hints.Items).Clear());
    }

    // ---- Named sets and helpers ----

    private static IReadOnlyDictionary<string, Func<AnalysisInput>> CreateNamedSets()
    {
        var sets = new Dictionary<string, Func<AnalysisInput>>(StringComparer.Ordinal)
        {
            [nameof(SingleBitFlipsSpread) + nameof(MemoryType.DDR4)] = () => SingleBitFlipsSpread(MemoryType.DDR4),
            [nameof(SingleBitFlipsSpread) + nameof(MemoryType.DDR5)] = () => SingleBitFlipsSpread(MemoryType.DDR5),
            [nameof(StuckBitAtOneWord)] = () => StuckBitAtOneWord(),
            [nameof(AddressRegionFlips)] = () => AddressRegionFlips(),
            [nameof(DelayDependentFlips)] = () => DelayDependentFlips(),
            [nameof(StrongSignatureOverPrior)] = () => StrongSignatureOverPrior(),
            [nameof(WeakSignatureUnderPrior)] = () => WeakSignatureUnderPrior(),
            [nameof(ClusterTieWithHold)] = () => ClusterTieWithHold(),
            [nameof(ClusterTieWithTurnaround)] = () => ClusterTieWithTurnaround(),
            [nameof(ClusterTieWithBankSwitch)] = () => ClusterTieWithBankSwitch(),
            [nameof(ClusterTieWithLoad)] = () => ClusterTieWithLoad(),
            [nameof(ClusterTieWithBitLane)] = () => ClusterTieWithBitLane(),
            [nameof(ThreeWayTie)] = () => ThreeWayTie(),
            [nameof(FivePriorTie)] = () => FivePriorTie(),
            [nameof(HoldShareAtKeepFloor)] = () => HoldShareAtKeepFloor(),
            [nameof(HoldShareBelowKeepFloor)] = () => HoldShareBelowKeepFloor(),
            [nameof(BitLaneAllAtOneWord)] = () => BitLaneAllAtOneWord(),
            [nameof(BitLaneSpanBelowWindow)] = () => BitLaneSpanBelowWindow(),
            [nameof(BitLaneSpanAtWindow)] = () => BitLaneSpanAtWindow(),
            [nameof(AapBitLaneExample)] = () => AapBitLaneExample(),
            [nameof(Ddr5LaneSet) + nameof(MemoryType.DDR4)] = () => Ddr5LaneSet(MemoryType.DDR4),
            [nameof(Ddr5LaneSet) + nameof(MemoryType.DDR5)] = () => Ddr5LaneSet(MemoryType.DDR5),
            [nameof(HoldWithoutHoldStep)] = () => HoldWithoutHoldStep(),
            [nameof(HoldWithHoldStepRun)] = () => HoldWithHoldStepRun(),
            [nameof(BankSwitchSequentialFailing)] = () => BankSwitchSequentialFailing(),
            [nameof(BankSwitchSequentialWithoutPass)] = () => BankSwitchSequentialWithoutPass(),
            [nameof(BankSwitchSequentialTruncated)] = () => BankSwitchSequentialTruncated(),
            [nameof(BankSwitchSequentialClean)] = () => BankSwitchSequentialClean(),
            [nameof(BankSwitchMixedShares)] = () => BankSwitchMixedShares(),
            [nameof(TurnaroundOnlyIneligibleStepsClean)] = () => TurnaroundOnlyIneligibleStepsClean(),
            [nameof(TurnaroundContrastClean)] = () => TurnaroundContrastClean(),
            [nameof(LoadHalfFailing)] = () => LoadHalfFailing(),
            [nameof(LoadHalfClean)] = () => LoadHalfClean(),
            [nameof(ClusterTwoDistinctWords)] = () => ClusterTwoDistinctWords(),
            [nameof(ClusterTwoAtOneWord)] = () => ClusterTwoAtOneWord(),
            [nameof(ClusterThreeDistinctWords)] = () => ClusterThreeDistinctWords(),
            [nameof(ClusterRegionBelowDivisor)] = () => ClusterRegionBelowDivisor(),
            [nameof(ClusterExactPercent)] = () => ClusterExactPercent(),
            [nameof(ClusterTinyExtent)] = () => ClusterTinyExtent(),
            ["StandardHeatSoakAfterContrast"] = () => SingleErrorAfterCleanSteps(StandardSteps, S12.Index),
        };

        foreach (InstabilityMechanism mechanism in SingleMechanisms())
        {
            sets[IsolatedPrefix + mechanism.ToString()] = () => SingleErrorIsolated(mechanism);
        }

        for (int index = 0; index < QuickSteps.Count; index++)
        {
            int failingIndex = index;
            sets[QuickProgressionPrefix + failingIndex.ToString(CultureInfo.InvariantCulture)] =
                () => SingleErrorAfterCleanSteps(QuickSteps, failingIndex);
        }

        return sets;
    }

    private static AnalysisInput NamedSet(string name) =>
        _namedSets.TryGetValue(name, out Func<AnalysisInput>? build)
            ? build()
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Not a named synthetic set.");

    private static InstabilityMechanism[] SingleMechanisms() =>
        Enum.GetValues<InstabilityMechanism>().Where(mechanism => mechanism != InstabilityMechanism.None).ToArray();

    private static CauseHint Hint(int rank, CauseCategory category, double score) =>
        new(rank, category, score, 50, Suggestions.For(category), "evidence");

    private static CauseHint HintFor(CauseHints hints, CauseCategory category) =>
        Assert.Single(hints.Items, hint => hint.Category == category);

    private static double? ScoreOf(CauseHints hints, CauseCategory category) =>
        hints.Items.FirstOrDefault(hint => hint.Category == category)?.Score;

    private static double PriorShare(AnalysisInput input, CauseCategory category) =>
        input.ShareOfErrorsInSteps(step => MechanismCategoryMap.Map(step.PrimaryMechanism) == category);

    private static int TiePosition(CauseHint hint)
    {
        for (int position = 0; position < ErrorAnalyzer.TieBreakOrder.Count; position++)
        {
            if (ErrorAnalyzer.TieBreakOrder[position] == hint.Category)
            {
                return position;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(hint), hint.Category, "Category missing from the tie-break order.");
    }

    private static void AssertHint(CauseHint hint, int rank, CauseCategory category, double score)
    {
        Assert.Equal(rank, hint.Rank);
        Assert.Equal(category, hint.Category);
        Assert.Equal(score, hint.Score, ScorePrecision);
        Assert.Equal(Suggestions.For(category), hint.Suggestion);
    }

    private static void AssertFired(RuleResult result, double score, string evidence)
    {
        Assert.True(result.Fired);
        Assert.Equal(score, result.Score, ScorePrecision);
        Assert.Equal(evidence, result.Evidence);
    }

    private static void AssertNotFired(RuleResult result)
    {
        Assert.False(result.Fired);
        Assert.Equal(0d, result.Score);
        Assert.Null(result.Evidence);
        Assert.Equal(RuleResult.NotFired, result);
    }

    // The category's score is the larger of its two terms; both contribute here, so their sum would differ.
    private static void AssertLargerTermWins(AnalysisInput input, CauseHints hints, CauseCategory category, double signature)
    {
        double prior = AnalyzerThresholds.PriorWeight * PriorShare(input, category);
        double score = HintFor(hints, category).Score;

        Assert.True(signature > 0d && prior > 0d, $"Both terms of {category} must be positive.");
        Assert.Equal(Math.Max(signature, prior), score, ScorePrecision);
        Assert.NotEqual(signature + prior, score, ScorePrecision);
    }

    private static void AssertNoCertaintyWording(string text)
    {
        foreach (string phrase in _certaintyWording)
        {
            Assert.DoesNotContain(phrase, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
