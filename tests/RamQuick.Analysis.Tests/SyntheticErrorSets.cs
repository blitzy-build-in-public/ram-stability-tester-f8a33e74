using RamQuick.Core;

namespace RamQuick.Analysis.Tests;

/// <summary>
/// The SplitMix64 generator: a small, fast, fully deterministic 64-bit pseudo-random sequence. The synthetic sets use
/// it instead of the runtime's <c>Random</c>, whose sequence is not guaranteed across runtime versions, so every set
/// is identical on every run and machine.
/// </summary>
/// <remarks>
/// An instance is mutable and owned by the one builder call that created it; nothing shares an instance, so tests
/// that build sets in parallel never interfere.
/// </remarks>
internal sealed class SplitMix64
{
    private const ulong GoldenGamma = 0x9E37_79B9_7F4A_7C15UL;
    private const ulong MixMultiplier1 = 0xBF58_476D_1CE4_E5B9UL;
    private const ulong MixMultiplier2 = 0x94D0_49BB_1331_11EBUL;

    private ulong _state;

    /// <summary>Initializes a generator whose sequence is fixed by <paramref name="seed"/>.</summary>
    /// <param name="seed">Any 64-bit value; equal seeds give equal sequences.</param>
    public SplitMix64(ulong seed)
    {
        _state = seed;
    }

    /// <summary>
    /// Returns the one-shot SplitMix64 hash of a value: the first output of a generator seeded with it. Stateless.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <returns>A well-mixed 64-bit value.</returns>
    public static ulong Mix(ulong value)
    {
        ulong z = unchecked(value + GoldenGamma);
        z = unchecked((z ^ (z >> 30)) * MixMultiplier1);
        z = unchecked((z ^ (z >> 27)) * MixMultiplier2);
        return z ^ (z >> 31);
    }

    /// <summary>Returns the next 64-bit value of the sequence.</summary>
    /// <returns>The next value.</returns>
    public ulong Next()
    {
        _state = unchecked(_state + GoldenGamma);
        ulong z = _state;
        z = unchecked((z ^ (z >> 30)) * MixMultiplier1);
        z = unchecked((z ^ (z >> 27)) * MixMultiplier2);
        return z ^ (z >> 31);
    }

    /// <summary>Returns the next value reduced into <c>[0, exclusiveMax)</c>.</summary>
    /// <param name="exclusiveMax">The exclusive upper bound, at least 1.</param>
    /// <returns>A value below <paramref name="exclusiveMax"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveMax"/> is 0.</exception>
    public ulong NextBelow(ulong exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfZero(exclusiveMax);
        return Next() % exclusiveMax;
    }

    /// <summary>Returns the next value reduced into <c>[0, exclusiveMax)</c> as an <see cref="int"/>.</summary>
    /// <param name="exclusiveMax">The exclusive upper bound, at least 1.</param>
    /// <returns>A value below <paramref name="exclusiveMax"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveMax"/> is below 1.</exception>
    public int NextInt(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMax, 1);
        return (int)NextBelow((ulong)exclusiveMax);
    }

    /// <summary>Permutes <paramref name="items"/> in place (Fisher–Yates), consuming one value per element.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The array to permute.</param>
    public void Shuffle<T>(T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>
/// Deterministic synthetic <see cref="AnalysisInput"/>s for the cause-analyzer tests: the step catalogue of the three
/// plans as literals, primitive record and outcome builders, and named error sets whose expected rule outcomes are
/// documented on each set.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer is pure, so its tests pass synthetic inputs directly rather than running the engine. The step data
/// mirrors <c>RamQuick.Plans.TestPlans</c> but is written out here, so retuning a plan never silently changes what
/// these tests assert about the analyzer.
/// </para>
/// <para>
/// Every builder returns new objects and every random choice comes from a <see cref="SplitMix64"/> seeded by a
/// fixed constant or an explicit seed argument, so equal calls yield value-equal records and the sets are safe to
/// build from parallel tests. The only static data is immutable: the step records and the read-only step lists.
/// </para>
/// <para>
/// Expected outcomes in the set summaries follow the cause-analysis specification: shares are fractions of the
/// records analysed, a category's score is the larger of its signature score and
/// <see cref="AnalyzerThresholds.PriorWeight"/> times the share of errors from steps whose primary mechanism maps to
/// it, scores are rounded to <see cref="AnalyzerThresholds.ScoreDecimals"/> places, and categories below
/// <see cref="AnalyzerThresholds.KeepFloor"/> are dropped. "Window" means
/// <c>TestedBytes / </c><see cref="AnalyzerThresholds.RegionWindowDivisor"/>. Category names are written as text
/// (for example <c>RowBankTiming</c>) because this file builds inputs only.
/// </para>
/// </remarks>
internal static class SyntheticErrorSets
{
    /// <summary>Tested bytes of the scaled Standard correctness runs: 512 MiB.</summary>
    internal const ulong RegionBytes512MiB = 536_870_912UL;

    /// <summary>Tested bytes of the exact-percentage cluster evidence set.</summary>
    internal const ulong ExactPercentTestedBytes = 1_000_000UL;

    /// <summary>Tested bytes of the largest supported machine, 192 GiB, for the tiny-extent cluster evidence set.</summary>
    internal const ulong RegionBytes192GiB = 192UL * 1024 * 1024 * 1024;

    /// <summary>The virtual base address of the synthetic region: <c>Address = BaseAddress + RegionOffset</c>.</summary>
    internal const ulong BaseAddress = 0x0000_023A_0000_0000UL;

    /// <summary>The default number of pinned workers.</summary>
    internal const int DefaultWorkers = 16;

    /// <summary>The seed every seeded set uses unless a test passes another.</summary>
    internal const ulong DefaultSeed = 0x5EED_2026_1028_0001UL;

    /// <summary>The bytes in one data word; every region offset is a multiple of it.</summary>
    internal const int WordBytes = 8;

    /// <summary>The default passes a clean or failing step completed.</summary>
    internal const int DefaultPasses = 2;

    /// <summary>Scaled Standard correctness runs divide every time budget by this value.</summary>
    internal const int ScaledBudgetDivisor = 10;

    /// <summary>Scaled Standard correctness runs keep every hold at this many seconds.</summary>
    internal const int ScaledHoldSeconds = 5;

    /// <summary>The address-region fault class corrupts words inside <c>1 / AddressRegionDivisor</c> of the region.</summary>
    internal const int AddressRegionDivisor = 32;

    /// <summary>The delay-dependent fault class corrupts only words whose time since write reaches this value.</summary>
    internal const int DelayThresholdMilliseconds = 2000;

    /// <summary>The bit every single-bit-flip injection flips.</summary>
    internal const int InjectedFlipBit = 17;

    /// <summary>The bit the stuck-bit injection forces.</summary>
    internal const int StuckBit = 5;

    /// <summary>The bit that shares lane <see cref="InjectedFlipBit"/> on DDR5, whose lanes are bit index modulo 32.</summary>
    internal const int Ddr5PartnerBit = InjectedFlipBit + 32;

    /// <summary>The offset of the lone error in the single-error sets, and of the cluster in the tie sets.</summary>
    internal const ulong SingleErrorOffset = RegionBytes512MiB / 2;

    /// <summary>The bit the lone error of the single-error sets flips.</summary>
    internal const int SingleErrorBit = 9;

    /// <summary>The bit a second, unrelated error flips: a different lane from every other named bit in both moduli.</summary>
    internal const int SecondErrorBit = 40;

    /// <summary>The distance between two nearby distinct words: far below one window of any region used here.</summary>
    internal const ulong NearbyWordDistance = 4096;

    private const ulong PatternSalt = 0xC0FF_EE00_DA7A_0001UL;
    private const int FirstErrorMilliseconds = 1_250;
    private const int ErrorSpacingMilliseconds = 40;
    private const int VerifyAfterWriteDelayMilliseconds = 30;
    private const int TurnaroundDelayMicroseconds = 250;
    private const int ReadModifyWriteDelayMilliseconds = 1_500;
    private const int HoldVerifyLagMilliseconds = 400;
    private const int GroupSize = 64;
    private const int InjectedFlipsPerPass = 3;
    private const int StuckReportsPerPass = 2;
    private const int DelayFlipsPerPass = 6;
    private const int ClusterTieErrors = 3;

    // ---- Step catalogue: literals mirroring RamQuick.Plans.TestPlans, natural indices 0 to 18 ----

    /// <summary>Q1, index 0: high-toggle sweep. Primary <c>SignalIntegrity</c>.</summary>
    internal static StepAttributes Q1 { get; } = new()
    {
        Index = 0,
        Name = "High-toggle sweep",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.SequentialAscending,
        TimeBudget = TimeSpan.FromSeconds(40),
        CoverageBudgetPercent = 25,
        PrimaryMechanism = InstabilityMechanism.SignalIntegrity,
        Mechanisms = InstabilityMechanism.SignalIntegrity
            | InstabilityMechanism.VddVddqVoltage
            | InstabilityMechanism.RefreshTimings,
    };

    /// <summary>Q2, index 1: random bank thrash. Primary <c>RowTimings</c>.</summary>
    internal static StepAttributes Q2 { get; } = new()
    {
        Index = 1,
        Name = "Random bank thrash",
        Pattern = PatternKind.Random,
        Order = AccessOrder.BankStride,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.RowTimings,
        Mechanisms = InstabilityMechanism.RowTimings
            | InstabilityMechanism.ActivationWindow
            | InstabilityMechanism.BankRankChannelSwitching,
    };

    /// <summary>Q3, index 2: read-after-write turnaround. Primary <c>ReadWriteTurnaround</c>.</summary>
    internal static StepAttributes Q3 { get; } = new()
    {
        Index = 2,
        Name = "Read-after-write turnaround",
        Pattern = PatternKind.Random,
        Order = AccessOrder.Turnaround,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.ReadWriteTurnaround,
        Mechanisms = InstabilityMechanism.ReadWriteTurnaround | InstabilityMechanism.WriteTimings,
    };

    /// <summary>
    /// Q4, index 3: moving inversion over walking bits. Primary <c>VddVddqVoltage</c>. Moving inversion sweeps
    /// ascending then descending whatever its order; the plans give it <see cref="AccessOrder.SequentialAscending"/>,
    /// so it counts as a sequential step for the bank-switching rule.
    /// </summary>
    internal static StepAttributes Q4 { get; } = new()
    {
        Index = 3,
        Name = "Moving inversion, walking bits",
        Pattern = PatternKind.MovingInversionWalking,
        Order = AccessOrder.SequentialAscending,
        TimeBudget = TimeSpan.FromSeconds(60),
        CoverageBudgetPercent = 50,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    /// <summary>Q5, index 4: refresh hold of 5 s. Primary <c>RefreshTimings</c>.</summary>
    internal static StepAttributes Q5 { get; } = new()
    {
        Index = 4,
        Name = "Refresh hold",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Hold = TimeSpan.FromSeconds(5),
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 20,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings | InstabilityMechanism.TemperatureSensitivity,
    };

    /// <summary>Q6, index 5: full-load random mix. Primary <c>SocImcVoltage</c>.</summary>
    internal static StepAttributes Q6 { get; } = new()
    {
        Index = 5,
        Name = "Full-load random mix",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.SocImcVoltage,
        Mechanisms = InstabilityMechanism.SocImcVoltage | InstabilityMechanism.BankRankChannelSwitching,
    };

    /// <summary>S7, index 6: moving inversion over random data. Primary <c>VddVddqVoltage</c>.</summary>
    internal static StepAttributes S7 { get; } = new()
    {
        Index = 6,
        Name = "Moving inversion, random",
        Pattern = PatternKind.MovingInversionRandom,
        Order = AccessOrder.SequentialAscending,
        TimeBudget = TimeSpan.FromSeconds(150),
        CoverageBudgetPercent = 120,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    /// <summary>S8, index 7: checkerboard, inverted every pass. Primary <c>VddVddqVoltage</c>.</summary>
    internal static StepAttributes S8 { get; } = new()
    {
        Index = 7,
        Name = "Checkerboard and inverted",
        Pattern = PatternKind.Checkerboard,
        Order = AccessOrder.SequentialDescending,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 80,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    /// <summary>S9, index 8: high-toggle bank thrash. Primary <c>BankRankChannelSwitching</c>.</summary>
    internal static StepAttributes S9 { get; } = new()
    {
        Index = 8,
        Name = "High-toggle bank thrash",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.BankStride,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 100,
        PrimaryMechanism = InstabilityMechanism.BankRankChannelSwitching,
        Mechanisms = InstabilityMechanism.BankRankChannelSwitching | InstabilityMechanism.SignalIntegrity,
    };

    /// <summary>S10, index 9: refresh hold of 30 s. Primary <c>RefreshTimings</c>.</summary>
    internal static StepAttributes S10 { get; } = new()
    {
        Index = 9,
        Name = "Refresh hold, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Hold = TimeSpan.FromSeconds(30),
        TimeBudget = TimeSpan.FromSeconds(150),
        CoverageBudgetPercent = 40,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings,
    };

    /// <summary>
    /// S11, index 10: reduced-load contrast at <see cref="ThreadMode.Half"/> threads. Primary <c>VddVddqVoltage</c>;
    /// the contrast step of the load rule.
    /// </summary>
    internal static StepAttributes S11 { get; } = new()
    {
        Index = 10,
        Name = "Reduced-load contrast",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.Half,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 60,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage | InstabilityMechanism.SocImcVoltage,
    };

    /// <summary>
    /// S12, index 11: heat soak. Primary <c>TemperatureSensitivity</c>, whose prior credits
    /// <c>RefreshRetention</c> although the step has no hold.
    /// </summary>
    internal static StepAttributes S12 { get; } = new()
    {
        Index = 11,
        Name = "Heat soak",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        TimeBudget = TimeSpan.FromSeconds(240),
        CoverageBudgetPercent = 200,
        PrimaryMechanism = InstabilityMechanism.TemperatureSensitivity,
        Mechanisms = InstabilityMechanism.TemperatureSensitivity | InstabilityMechanism.SocImcVoltage,
    };

    /// <summary>E13, index 12: moving inversion over walking ones and zeros. Primary <c>VddVddqVoltage</c>.</summary>
    internal static StepAttributes E13 { get; } = new()
    {
        Index = 12,
        Name = "Moving inversion, walking ones and zeros",
        Pattern = PatternKind.MovingInversionWalking,
        Order = AccessOrder.SequentialAscending,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    /// <summary>E14, index 13: long random turnaround. Primary <c>WriteTimings</c>.</summary>
    internal static StepAttributes E14 { get; } = new()
    {
        Index = 13,
        Name = "Random turnaround, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.Turnaround,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.WriteTimings,
        Mechanisms = InstabilityMechanism.WriteTimings,
    };

    /// <summary>E15, index 14: long random bank thrash. Primary <c>ActivationWindow</c>.</summary>
    internal static StepAttributes E15 { get; } = new()
    {
        Index = 14,
        Name = "Random bank thrash, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.BankStride,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.ActivationWindow,
        Mechanisms = InstabilityMechanism.ActivationWindow | InstabilityMechanism.RowTimings,
    };

    /// <summary>E16, index 15: refresh hold of 120 s. Primary <c>RefreshTimings</c>.</summary>
    internal static StepAttributes E16 { get; } = new()
    {
        Index = 15,
        Name = "Refresh hold, extended",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Hold = TimeSpan.FromSeconds(120),
        TimeBudget = TimeSpan.FromSeconds(360),
        CoverageBudgetPercent = 50,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings,
    };

    /// <summary>E17, index 16: long reduced-load contrast at <see cref="ThreadMode.Half"/> threads.</summary>
    internal static StepAttributes E17 { get; } = new()
    {
        Index = 16,
        Name = "Reduced-load contrast, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.Half,
        TimeBudget = TimeSpan.FromSeconds(240),
        CoverageBudgetPercent = 150,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage | InstabilityMechanism.SocImcVoltage,
    };

    /// <summary>E18, index 17: high-toggle data in random-block order. Primary <c>SignalIntegrity</c>.</summary>
    internal static StepAttributes E18 { get; } = new()
    {
        Index = 17,
        Name = "High-toggle random order",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.RandomBlocks,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.SignalIntegrity,
        Mechanisms = InstabilityMechanism.SignalIntegrity,
    };

    /// <summary>E19, index 18: long heat soak. Primary <c>TemperatureSensitivity</c>.</summary>
    internal static StepAttributes E19 { get; } = new()
    {
        Index = 18,
        Name = "Heat soak, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        TimeBudget = TimeSpan.FromSeconds(600),
        CoverageBudgetPercent = 500,
        PrimaryMechanism = InstabilityMechanism.TemperatureSensitivity,
        Mechanisms = InstabilityMechanism.TemperatureSensitivity | InstabilityMechanism.SocImcVoltage,
    };

    // Static initialisers run in textual order, so the lists follow the steps they hold.

    /// <summary>The Quick plan: Q1 to Q6 at indices 0 to 5, 300 s in total.</summary>
    internal static IReadOnlyList<StepAttributes> QuickSteps { get; } = Array.AsReadOnly(
        new[] { Q1, Q2, Q3, Q4, Q5, Q6 });

    /// <summary>The Standard plan: Q1 to S12 at indices 0 to 11, 1200 s in total.</summary>
    internal static IReadOnlyList<StepAttributes> StandardSteps { get; } = Array.AsReadOnly(
        new[] { Q1, Q2, Q3, Q4, Q5, Q6, S7, S8, S9, S10, S11, S12 });

    /// <summary>The Exhaustive plan: Q1 to E19 at indices 0 to 18, 3600 s in total.</summary>
    internal static IReadOnlyList<StepAttributes> ExhaustiveSteps { get; } = Array.AsReadOnly(
        new[] { Q1, Q2, Q3, Q4, Q5, Q6, S7, S8, S9, S10, S11, S12, E13, E14, E15, E16, E17, E18, E19 });

    /// <summary>
    /// The Standard plan as the fault-injection correctness runs scale it: every time budget divided by
    /// <see cref="ScaledBudgetDivisor"/> and every hold kept at <see cref="ScaledHoldSeconds"/> seconds, so Q5 and
    /// S10 both hold 5 s. Patterns, orders, threads, coverage budgets and mechanisms are unchanged.
    /// </summary>
    internal static IReadOnlyList<StepAttributes> ScaledStandardSteps { get; } = Array.AsReadOnly(
        StandardSteps.Select(Scale).ToArray());

    /// <summary>
    /// Returns a copy of <paramref name="step"/> placed at another position of a step list.
    /// </summary>
    /// <param name="step">The catalogue step.</param>
    /// <param name="index">The zero-based index the step takes in the list being built, 0 or more.</param>
    /// <returns>A new record equal to <paramref name="step"/> except for <see cref="StepAttributes.Index"/>.</returns>
    internal static StepAttributes WithIndex(StepAttributes step, int index)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return step with { Index = index };
    }

    /// <summary>
    /// Returns the catalogue step whose primary mechanism is <paramref name="mechanism"/>, so each of the ten
    /// mechanisms has a single-error prior case: Q1, Q2, S9, E15, Q3, E14, Q4, Q5, Q6 or S12, at its natural index.
    /// </summary>
    /// <param name="mechanism">One single-bit <see cref="InstabilityMechanism"/> member.</param>
    /// <returns>The step targeting it first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mechanism"/> is <see cref="InstabilityMechanism.None"/>, a combination or undefined.
    /// </exception>
    internal static StepAttributes RepresentativeStepFor(InstabilityMechanism mechanism) => mechanism switch
    {
        InstabilityMechanism.SignalIntegrity => Q1,
        InstabilityMechanism.RowTimings => Q2,
        InstabilityMechanism.BankRankChannelSwitching => S9,
        InstabilityMechanism.ActivationWindow => E15,
        InstabilityMechanism.ReadWriteTurnaround => Q3,
        InstabilityMechanism.WriteTimings => E14,
        InstabilityMechanism.VddVddqVoltage => Q4,
        InstabilityMechanism.RefreshTimings => Q5,
        InstabilityMechanism.SocImcVoltage => Q6,
        InstabilityMechanism.TemperatureSensitivity => S12,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mechanism), mechanism, "Not a single-bit instability mechanism."),
    };

    /// <summary>
    /// Returns the verification access the engine's pass phases give a step's errors: after the hold for hold steps,
    /// interleaved turnaround reads for the turnaround order, read-modify-write sweeps for moving inversion, and a
    /// windowed verification after write otherwise.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The access type its error records carry.</returns>
    internal static AccessType AccessTypeFor(StepAttributes step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.HasHold)
        {
            return AccessType.VerifyAfterHold;
        }

        if (step.Order == AccessOrder.Turnaround)
        {
            return AccessType.TurnaroundRead;
        }

        return step.Pattern is PatternKind.MovingInversionWalking or PatternKind.MovingInversionRandom
            ? AccessType.ReadModifyWrite
            : AccessType.VerifyAfterWrite;
    }


    // ---- Primitive builders ----

    /// <summary>
    /// Builds one error record: the word at <paramref name="regionOffset"/> read back with <paramref name="bits"/>
    /// flipped. <c>Actual = Expected ^ mask</c>, so <see cref="ErrorRecord.BitMask"/> is exactly the requested bits.
    /// </summary>
    /// <param name="step">The step that found the error; supplies the index, name, default access type and threads.</param>
    /// <param name="regionOffset">The word's offset in the tested region, a multiple of <see cref="WordBytes"/>.</param>
    /// <param name="bits">The flipped bit positions, one or more, each 0 to 63.</param>
    /// <param name="passIndex">The zero-based pass, 0 or more.</param>
    /// <param name="sequence">The engine record number; also fixes <see cref="ErrorRecord.TimeIntoTest"/>.</param>
    /// <param name="accessType">The access that found it; <see cref="AccessTypeFor"/> of the step when null.</param>
    /// <param name="timeSinceWrite">The data's age at the load; a typical age for the access when null.</param>
    /// <param name="workerIndex">The verifying worker, below <paramref name="workers"/>; even for Half steps.</param>
    /// <param name="workers">The run's worker count, at least 1.</param>
    /// <param name="expected">The value the word should hold; a SplitMix64 pattern value when null.</param>
    /// <param name="rereadOutcome">How the flushed re-read compared; the re-read value is derived from it.</param>
    /// <returns>A complete record with a virtual address of <c>BaseAddress + regionOffset</c>.</returns>
    /// <exception cref="ArgumentException">An argument is outside the documented range.</exception>
    internal static ErrorRecord Error(
        StepAttributes step,
        ulong regionOffset,
        IReadOnlyList<int> bits,
        int passIndex = 0,
        ulong sequence = 0,
        AccessType? accessType = null,
        TimeSpan? timeSinceWrite = null,
        int workerIndex = 0,
        int workers = DefaultWorkers,
        ulong? expected = null,
        RereadOutcome rereadOutcome = RereadOutcome.RereadRepeatsActual)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(bits);
        ArgumentOutOfRangeException.ThrowIfNegative(passIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(workerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(workerIndex, workers);
        if (regionOffset % WordBytes != 0)
        {
            throw new ArgumentException($"Region offset {regionOffset} is not word aligned.", nameof(regionOffset));
        }

        if (step.Threads == ThreadMode.Half && workerIndex % 2 != 0)
        {
            throw new ArgumentException("Half-thread steps run on even-indexed workers only.", nameof(workerIndex));
        }

        ulong mask = MaskOf(bits);
        ulong expectedValue = expected
            ?? SplitMix64.Mix(PatternSalt ^ regionOffset ^ ((ulong)passIndex << 40) ^ ((ulong)step.Index << 52));
        ulong actual = expectedValue ^ mask;
        AccessType access = accessType ?? AccessTypeFor(step);

        return new ErrorRecord
        {
            Sequence = sequence,
            Address = BaseAddress + regionOffset,
            AddressKind = AddressKind.Virtual,
            RegionOffset = regionOffset,
            Expected = expectedValue,
            Actual = actual,
            Reread = RereadValue(expectedValue, actual, rereadOutcome),
            RereadOutcome = rereadOutcome,
            StepIndex = step.Index,
            StepName = step.Name,
            PassIndex = passIndex,
            AccessType = access,
            TimeSinceWrite = timeSinceWrite ?? TypicalTimeSinceWrite(step, access),
            TimeIntoTest = TimeSpan.FromMilliseconds(FirstErrorMilliseconds + ((double)sequence * ErrorSpacingMilliseconds)),
            WorkerIndex = workerIndex,
            LogicalProcessor = workerIndex % GroupSize,
            ProcessorGroup = workerIndex / GroupSize,
            ActiveWorkers = step.Threads == ThreadMode.Half ? Math.Max(1, workers / 2) : workers,
        };
    }

    /// <summary>
    /// A step that completed <paramref name="passes"/> passes with zero errors: a contrast step for the
    /// bank-switching, turnaround and load rules when its attributes qualify.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="passes">Passes completed, at least 1.</param>
    /// <returns>A completed, error-free outcome.</returns>
    internal static StepOutcome Clean(StepAttributes step, int passes = DefaultPasses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        return Outcome(step, passes, errors: 0, completed: true);
    }

    /// <summary>
    /// A step that completed its schedule with <paramref name="errors"/> true errors. The true count may exceed the
    /// records supplied for the step, as after the stored-detail cap.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="errors">The engine's true error count, at least 1.</param>
    /// <param name="passes">Passes completed, at least 1.</param>
    /// <returns>A completed outcome with errors.</returns>
    internal static StepOutcome Failing(StepAttributes step, ulong errors, int passes = DefaultPasses)
    {
        ArgumentOutOfRangeException.ThrowIfZero(errors);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        return Outcome(step, passes, errors, completed: true);
    }

    /// <summary>
    /// The step a stop interrupted during its first pass, as Stop on error does at the first confirmed error: not
    /// completed, no pass finished, <paramref name="errors"/> true errors.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="errors">The engine's true error count, at least 1.</param>
    /// <returns>An interrupted outcome with errors.</returns>
    internal static StepOutcome Incomplete(StepAttributes step, ulong errors)
    {
        ArgumentOutOfRangeException.ThrowIfZero(errors);
        return Outcome(step, passes: 0, errors, completed: false);
    }

    /// <summary>
    /// A step stopped before finishing its first pass without finding an error. It is error-free but completed no
    /// pass, so it never satisfies a contrast-step precondition.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>An interrupted, error-free outcome.</returns>
    internal static StepOutcome CleanButNoPass(StepAttributes step) =>
        Outcome(step, passes: 0, errors: 0, completed: false);

    /// <summary>
    /// One completed outcome per step, each step's true error count taken from the records that carry its index:
    /// <see cref="Clean"/> when it has none, <see cref="Failing"/> otherwise.
    /// </summary>
    /// <param name="steps">The started steps, each with a unique index.</param>
    /// <param name="errors">The stored records.</param>
    /// <param name="passes">Passes every step completed, at least 1.</param>
    /// <returns>The outcomes in step order.</returns>
    internal static IReadOnlyList<StepOutcome> Outcomes(
        IEnumerable<StepAttributes> steps,
        IEnumerable<ErrorRecord> errors,
        int passes = DefaultPasses)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(errors);
        Dictionary<int, int> counts = errors.GroupBy(e => e.StepIndex).ToDictionary(g => g.Key, g => g.Count());
        return steps
            .Select(s => counts.TryGetValue(s.Index, out int count)
                ? Failing(s, (ulong)count, passes)
                : Clean(s, passes))
            .ToArray();
    }

    /// <summary>Wraps records and outcomes into an analysis input.</summary>
    /// <param name="errors">The stored records, at most <see cref="AnalyzerThresholds.MaxAnalysedRecords"/>.</param>
    /// <param name="steps">One outcome per started step.</param>
    /// <param name="testedBytes">The tested bytes.</param>
    /// <param name="workers">The worker count.</param>
    /// <param name="memoryType">The installed memory type, which fixes the lane modulus.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput Input(
        IEnumerable<ErrorRecord> errors,
        IEnumerable<StepOutcome> steps,
        ulong testedBytes = RegionBytes512MiB,
        int workers = DefaultWorkers,
        MemoryType memoryType = MemoryType.DDR4) =>
        new(errors, steps, testedBytes, workers, memoryType);

    /// <summary>
    /// The width of one region window, <c>testedBytes / </c><see cref="AnalyzerThresholds.RegionWindowDivisor"/>,
    /// the same value as <see cref="AnalysisInput.RegionWindowBytes"/>.
    /// </summary>
    /// <param name="testedBytes">The tested bytes.</param>
    /// <returns>The window in bytes of region offset; 0 below the divisor.</returns>
    internal static ulong WindowBytes(ulong testedBytes) =>
        testedBytes / (ulong)AnalyzerThresholds.RegionWindowDivisor;

    /// <summary>
    /// The first offset of the address-region fault's range for <paramref name="seed"/>: word aligned and chosen so
    /// that the whole range, <see cref="AddressRegionLength"/> bytes, lies inside the region.
    /// </summary>
    /// <param name="seed">The set's seed.</param>
    /// <param name="testedBytes">The tested bytes.</param>
    /// <returns>The range start.</returns>
    internal static ulong AddressRegionStart(ulong seed = DefaultSeed, ulong testedBytes = RegionBytes512MiB)
    {
        ulong startWords = ((testedBytes - AddressRegionLength(testedBytes)) / WordBytes) + 1;
        return SplitMix64.Mix(seed ^ 0xA11C_E5E7_0000_0032UL) % startWords * WordBytes;
    }

    /// <summary>The length of the address-region fault's contiguous range: 1/32 of the region, word aligned.</summary>
    /// <param name="testedBytes">The tested bytes.</param>
    /// <returns>The range length in bytes, below one region window.</returns>
    internal static ulong AddressRegionLength(ulong testedBytes = RegionBytes512MiB) =>
        AlignDown(testedBytes / AddressRegionDivisor);

    /// <summary>The offset of the word the stuck-bit fault forces, for <paramref name="seed"/>.</summary>
    /// <param name="seed">The set's seed.</param>
    /// <returns>A word-aligned offset inside the 512 MiB region.</returns>
    internal static ulong StuckWordOffset(ulong seed = DefaultSeed) =>
        SplitMix64.Mix(seed ^ 0x57AC_0B17_0000_0005UL) % (RegionBytes512MiB / WordBytes) * WordBytes;

    /// <summary>
    /// <paramref name="count"/> distinct word offsets spread uniformly over <c>[start, start + spanBytes)</c> by
    /// jittered stratification: one seeded word in each of <paramref name="count"/> equal strata, returned in seeded
    /// random order. Every offset is a distinct word, the first and last strata are always hit, and any window of
    /// width <c>w</c> holds at most <c>w / stratum + 2</c> of them, whatever the seed.
    /// </summary>
    /// <param name="count">The number of offsets, at least 1.</param>
    /// <param name="spanBytes">The span to cover; each stratum must hold at least one word.</param>
    /// <param name="random">The generator, advanced by the call.</param>
    /// <param name="start">The span's first offset, word aligned.</param>
    /// <returns>The offsets in random order.</returns>
    internal static ulong[] SpreadOffsets(int count, ulong spanBytes, SplitMix64 random, ulong start = 0)
    {
        ArgumentNullException.ThrowIfNull(random);
        ulong stratum = Stratum(count, spanBytes);
        var offsets = new ulong[count];
        for (int k = 0; k < count; k++)
        {
            offsets[k] = start + ((ulong)k * stratum) + (random.NextBelow(stratum / WordBytes) * WordBytes);
        }

        random.Shuffle(offsets);
        return offsets;
    }

    /// <summary>
    /// <paramref name="count"/> word offsets evenly spaced from <paramref name="start"/>, one per equal stratum of
    /// <paramref name="spanBytes"/>, in ascending order.
    /// </summary>
    /// <param name="count">The number of offsets, at least 1.</param>
    /// <param name="spanBytes">The span to cover; each stratum must hold at least one word.</param>
    /// <param name="start">The first offset, word aligned.</param>
    /// <returns>The ascending offsets; the spacing is <c>spanBytes / count</c> rounded down to a word.</returns>
    internal static ulong[] EvenOffsets(int count, ulong spanBytes, ulong start = 0)
    {
        ulong stratum = Stratum(count, spanBytes);
        return Enumerable.Range(0, count).Select(k => start + ((ulong)k * stratum)).ToArray();
    }

    /// <summary>
    /// The worker that owns <paramref name="regionOffset"/>: the region is the worker slices in worker order, and in
    /// a Half step the even worker covers its odd partner's slice too.
    /// </summary>
    /// <param name="regionOffset">The offset.</param>
    /// <param name="testedBytes">The tested bytes.</param>
    /// <param name="workers">The worker count, at least 1.</param>
    /// <param name="threads">The step's thread mode.</param>
    /// <returns>The worker index.</returns>
    internal static int WorkerFor(ulong regionOffset, ulong testedBytes, int workers, ThreadMode threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ulong slice = testedBytes / (ulong)workers;
        int worker = slice == 0 ? 0 : (int)Math.Min(regionOffset / slice, (ulong)(workers - 1));
        return threads == ThreadMode.Half ? worker & ~1 : worker;
    }

    private static StepAttributes Scale(StepAttributes step) => step with
    {
        TimeBudget = step.TimeBudget / ScaledBudgetDivisor,
        Hold = step.HasHold ? TimeSpan.FromSeconds(ScaledHoldSeconds) : TimeSpan.Zero,
    };

    private static StepOutcome Outcome(StepAttributes step, int passes, ulong errors, bool completed)
    {
        ArgumentNullException.ThrowIfNull(step);
        ulong fullVerifiedBytes = RegionBytes512MiB / 100 * (ulong)step.CoverageBudgetPercent;
        return new StepOutcome
        {
            Attributes = step,
            ActualDuration = completed ? step.TimeBudget : step.TimeBudget / 5,
            PassesCompleted = passes,
            VerifiedBytes = completed ? fullVerifiedBytes : fullVerifiedBytes / 5,
            Errors = errors,
            Completed = completed,
            Overrun = TimeSpan.Zero,
        };
    }

    private static ulong MaskOf(IReadOnlyList<int> bits)
    {
        if (bits.Count == 0)
        {
            throw new ArgumentException("At least one flipped bit is required.", nameof(bits));
        }

        ulong mask = 0;
        foreach (int bit in bits)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bit, nameof(bits));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 63, nameof(bits));
            mask |= 1UL << bit;
        }

        return mask;
    }

    private static ulong RereadValue(ulong expected, ulong actual, RereadOutcome outcome)
    {
        ulong mask = expected ^ actual;
        return outcome switch
        {
            RereadOutcome.RereadMatchesExpected => expected,
            RereadOutcome.RereadRepeatsActual => actual,

            // A third value: actual with one more bit flipped, outside the mask, so it equals neither.
            RereadOutcome.RereadDiffers => actual ^ Math.Max(~mask & (mask + 1), 1UL),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Undefined re-read outcome."),
        };
    }

    private static TimeSpan TypicalTimeSinceWrite(StepAttributes step, AccessType access) => access switch
    {
        AccessType.VerifyAfterHold => step.Hold + TimeSpan.FromMilliseconds(HoldVerifyLagMilliseconds),
        AccessType.TurnaroundRead => TimeSpan.FromMicroseconds(TurnaroundDelayMicroseconds),
        AccessType.ReadModifyWrite => TimeSpan.FromMilliseconds(ReadModifyWriteDelayMilliseconds),
        _ => TimeSpan.FromMilliseconds(VerifyAfterWriteDelayMilliseconds),
    };

    private static ulong Stratum(int count, ulong spanBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ulong stratum = AlignDown(spanBytes / (ulong)count);
        if (stratum == 0)
        {
            throw new ArgumentException($"{spanBytes} bytes cannot hold {count} distinct words.", nameof(spanBytes));
        }

        return stratum;
    }

    private static ulong AlignDown(ulong bytes) => bytes / WordBytes * WordBytes;


    // Bits 0 to 31 except InjectedFlipBit, cycling: distinct lanes under both lane moduli, never lane 17.
    private static int OtherBit(int k)
    {
        int bit = k % 31;
        return bit < InjectedFlipBit ? bit : bit + 1;
    }

    // ---- Injected fault classes: scaled Standard plan, 512 MiB, Stop on error off, every step completed ----

    /// <summary>
    /// Single-bit flips on a fixed lane: bit <see cref="InjectedFlipBit"/> flipped in three words per pass in every
    /// scaled Standard step (72 records at distinct words, spread by <see cref="SpreadOffsets"/> over the whole
    /// region), access type per step, every re-read <see cref="RereadOutcome.RereadRepeatsActual"/>.
    /// </summary>
    /// <remarks>
    /// Expected: bit lane fires (72 words, span far above one window), share 1.0 on DDR4 and DDR5 alike, so
    /// <c>VoltageSignalIntegrity</c> 1.0 ranks first. Every step has errors, so the bank-switching, turnaround and
    /// load rules do not fire. Hold fires with the Q5 and S10 share, 1/6. Cluster stays at most 6/72. The priors
    /// stay at or below 0.3 × 30/72 = 0.125.
    /// </remarks>
    /// <param name="memoryType">The memory type; bit 17 is lane 17 under both lane moduli.</param>
    /// <param name="seed">The offset seed.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput SingleBitFlipsSpread(MemoryType memoryType = MemoryType.DDR4, ulong seed = DefaultSeed)
    {
        IReadOnlyList<StepAttributes> steps = ScaledStandardSteps;
        var random = new SplitMix64(seed);
        ulong[] offsets = SpreadOffsets(steps.Count * DefaultPasses * InjectedFlipsPerPass, RegionBytes512MiB, random);
        var builder = new ErrorSetBuilder();
        int next = 0;
        foreach (StepAttributes step in steps)
        {
            for (int pass = 0; pass < DefaultPasses; pass++)
            {
                for (int k = 0; k < InjectedFlipsPerPass; k++)
                {
                    builder.Add(step, offsets[next++], [InjectedFlipBit], pass);
                }
            }
        }

        return builder.Build(Outcomes(steps, builder.Records), memoryType);
    }

    /// <summary>
    /// A stuck bit: bit <see cref="StuckBit"/> of the word at <see cref="StuckWordOffset"/> forced to 1, reported twice
    /// per pass in every scaled Standard step (48 records). Each report's expected value has that bit clear, as only
    /// opportunities where the expected bit differs from the stuck value are reported.
    /// </summary>
    /// <remarks>
    /// Expected: every record shares one <see cref="ErrorRecord.WordAddress"/>, so the bit-lane rule does not fire
    /// and the cluster rule fires (at least two errors at one word) with score 1.0: <c>RankOrDimm</c> 1.0 first, with
    /// evidence "100 % of errors concentrated in &lt; 0.01 % of the tested memory (virtual offsets)". Hold fires
    /// with 1/6; no contrast rule fires.
    /// </remarks>
    /// <param name="seed">The seed choosing the word and the pattern values.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput StuckBitAtOneWord(ulong seed = DefaultSeed)
    {
        IReadOnlyList<StepAttributes> steps = ScaledStandardSteps;
        ulong offset = StuckWordOffset(seed);
        var random = new SplitMix64(seed);
        var builder = new ErrorSetBuilder();
        foreach (StepAttributes step in steps)
        {
            for (int pass = 0; pass < DefaultPasses; pass++)
            {
                for (int k = 0; k < StuckReportsPerPass; k++)
                {
                    ulong expected = random.Next() & ~(1UL << StuckBit);
                    builder.Add(step, offset, [StuckBit], pass, expected: expected);
                }
            }
        }

        return builder.Build(Outcomes(steps, builder.Records));
    }

    /// <summary>
    /// An address-region fault: one seeded random bit flipped in each of 72 distinct words inside the contiguous
    /// range <c>[AddressRegionStart(seed), + AddressRegionLength())</c>, 1/32 of the region, three per pass in every
    /// scaled Standard step.
    /// </summary>
    /// <remarks>
    /// Expected: the span stays below one window, so the bit-lane rule does not fire, and every record lies in one
    /// window, so the cluster rule scores 1.0: <c>RankOrDimm</c> first. Hold fires with 1/6; no contrast rule fires.
    /// </remarks>
    /// <param name="seed">The seed choosing the range, the words and the bits.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput AddressRegionFlips(ulong seed = DefaultSeed)
    {
        IReadOnlyList<StepAttributes> steps = ScaledStandardSteps;
        var random = new SplitMix64(seed);
        ulong[] offsets = SpreadOffsets(
            steps.Count * DefaultPasses * InjectedFlipsPerPass,
            AddressRegionLength(),
            random,
            AddressRegionStart(seed));
        var builder = new ErrorSetBuilder();
        int next = 0;
        foreach (StepAttributes step in steps)
        {
            for (int pass = 0; pass < DefaultPasses; pass++)
            {
                for (int k = 0; k < InjectedFlipsPerPass; k++)
                {
                    builder.Add(step, offsets[next++], [random.NextInt(64)], pass);
                }
            }
        }

        return builder.Build(Outcomes(steps, builder.Records));
    }

    /// <summary>
    /// A delay-dependent fault: six words per pass corrupted only in the scaled hold steps, Q5 and S10, each with a
    /// different seeded bit, found by <see cref="AccessType.VerifyAfterHold"/> after the 5 s hold (time since write
    /// 5.4 s, above <see cref="DelayThresholdMilliseconds"/>); 24 records spread over the whole region. Every
    /// non-hold step is clean.
    /// </summary>
    /// <remarks>
    /// Expected: hold share 1.0, so <c>RefreshRetention</c> 1.0 first. The bank-switching (clean Q1), turnaround
    /// (clean Q1) and load (clean S11) rules fire with score 0, because hold-step errors count in none of their
    /// shares. The bit-lane rule fires with a dominant share of 1/24 (2/24 on DDR5), so it scores 0. Cluster stays at
    /// most 3/24.
    /// </remarks>
    /// <param name="seed">The seed choosing the words and the bits.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput DelayDependentFlips(ulong seed = DefaultSeed)
    {
        IReadOnlyList<StepAttributes> steps = ScaledStandardSteps;
        StepAttributes[] holdSteps = steps.Where(s => s.HasHold).ToArray();
        var random = new SplitMix64(seed);
        int count = holdSteps.Length * DefaultPasses * DelayFlipsPerPass;
        ulong[] offsets = SpreadOffsets(count, RegionBytes512MiB, random);
        int[] bits = Enumerable.Range(0, 64).ToArray();
        random.Shuffle(bits);
        var builder = new ErrorSetBuilder();
        int next = 0;
        foreach (StepAttributes step in holdSteps)
        {
            for (int pass = 0; pass < DefaultPasses; pass++)
            {
                for (int k = 0; k < DelayFlipsPerPass; k++)
                {
                    builder.Add(
                        step,
                        offsets[next],
                        [bits[next % bits.Length]],
                        pass,
                        AccessType.VerifyAfterHold,
                        step.Hold + TimeSpan.FromMilliseconds(HoldVerifyLagMilliseconds));
                    next++;
                }
            }
        }

        return builder.Build(Outcomes(steps, builder.Records));
    }

    // ---- Single-error prior sets: Stop on error on ----

    /// <summary>
    /// One error, at <see cref="SingleErrorOffset"/> on bit <see cref="SingleErrorBit"/>, in
    /// <see cref="RepresentativeStepFor"/> of <paramref name="mechanism"/>, the only step, interrupted by Stop on error.
    /// </summary>
    /// <remarks>
    /// Expected: no region or contrast rule can fire, so the only item is the step's prior category,
    /// <c>MechanismCategoryMap.Map(primary)</c>, at <see cref="AnalyzerThresholds.PriorWeight"/> and 100 %
    /// likelihood. For <c>RefreshTimings</c> (Q5, a hold step) the hold rule also fires in the same category, so
    /// <c>RefreshRetention</c> scores 1.0.
    /// </remarks>
    /// <param name="mechanism">One single-bit mechanism.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput SingleErrorIsolated(InstabilityMechanism mechanism)
    {
        StepAttributes step = RepresentativeStepFor(mechanism);
        var builder = new ErrorSetBuilder();
        builder.Add(step, SingleErrorOffset, [SingleErrorBit]);
        return builder.Build([Incomplete(step, 1)]);
    }

    /// <summary>
    /// The plan as Stop on error leaves it after one error: the steps before <paramref name="failingIndex"/> clean,
    /// the failing step interrupted with one error, later steps never started.
    /// </summary>
    /// <remarks>
    /// Expected, Quick: Q1 (index 0) <c>VoltageSignalIntegrity</c> 0.3 alone; Q2 <c>RowBankTiming</c> 1.0 (bank
    /// switching, clean Q1); Q3 <c>WriteReadTurnaround</c> 1.0 (turnaround, clean Q1); Q4
    /// <c>VoltageSignalIntegrity</c> 0.3 prior only (its read-modify-write error is in no rule's share); Q5
    /// <c>RefreshRetention</c> 1.0 (hold); Q6 <c>RowBankTiming</c> 0.5 (random-block weight, clean Q1) first and
    /// <c>ImcPowerDelivery</c> 0.3 second. Standard S12 (index 11), after a clean S11: <c>ImcPowerDelivery</c> 1.0
    /// (load), <c>RowBankTiming</c> 0.5, <c>RefreshRetention</c> 0.3 (prior of <c>TemperatureSensitivity</c>).
    /// </remarks>
    /// <param name="plan">The plan's steps, positions equal to indices, as in <see cref="QuickSteps"/>.</param>
    /// <param name="failingIndex">The position of the failing step.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput SingleErrorAfterCleanSteps(IReadOnlyList<StepAttributes> plan, int failingIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(failingIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(failingIndex, plan.Count);
        StepAttributes failing = plan[failingIndex];
        var builder = new ErrorSetBuilder();
        builder.Add(failing, SingleErrorOffset, [SingleErrorBit]);
        return builder.Build(plan.Take(failingIndex).Select(s => Clean(s)).Append(Incomplete(failing, 1)));
    }

    // ---- Ranking sets ----

    /// <summary>
    /// A strong signature against a prior: 20 errors in Q2 alone, the only step, at 20 evenly spaced words; 18 flip
    /// bit 17 and two flip other bits.
    /// </summary>
    /// <remarks>
    /// Expected: bit lane 0.9 (<c>VoltageSignalIntegrity</c>) outranks the Q2 prior, <c>RowBankTiming</c> 0.3; cluster
    /// 0.1 (two words per window) third. No contrast or hold rule fires.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput StrongSignatureOverPrior()
    {
        const int Total = 20;
        ulong[] offsets = EvenOffsets(Total, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < Total; k++)
        {
            int bit = k switch
            {
                5 => OtherBit(3),
                15 => SecondErrorBit,
                _ => InjectedFlipBit,
            };
            builder.Add(Q2, offsets[k], [bit]);
        }

        return builder.Build([Failing(Q2, Total)]);
    }

    /// <summary>
    /// A weak signature against a higher prior: 10 errors in Q6 alone, at offsets <c>k × RegionBytes / 10</c> (each
    /// more than one window apart), each on a different bit.
    /// </summary>
    /// <remarks>
    /// Expected: <c>ImcPowerDelivery</c> 0.3 (prior) first, <c>RankOrDimm</c> 0.1 (cluster, one error per window)
    /// second. The bit-lane rule fires with score 0 (dominant share 0.1) and is not kept.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput WeakSignatureUnderPrior()
    {
        const int Total = 10;
        ulong[] offsets = EvenOffsets(Total, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < Total; k++)
        {
            builder.Add(Q6, offsets[k], [OtherBit(k)]);
        }

        return builder.Build([Failing(Q6, Total)]);
    }


    // ---- Tie sets: equal category scores, so only the tie-break order decides ----

    /// <summary>
    /// A prior-only tie: one error in each of two steps, neither holding, at two distinct words
    /// <see cref="NearbyWordDistance"/> apart (span below one window) on different bits; both steps fail, so none is
    /// clean.
    /// </summary>
    /// <remarks>
    /// Expected: no rule fires (the bit-lane span is too small, the cluster rule sees two errors at distinct words, no
    /// hold step ran and no contrast step is clean), so the two steps' prior categories score exactly
    /// <c>0.3 × 0.5</c> each, computed identically. Pass steps of different prior categories: S12 for
    /// <c>RefreshRetention</c>, Q3 or E14 for <c>WriteReadTurnaround</c>, Q2 for <c>RowBankTiming</c>, Q6 for
    /// <c>ImcPowerDelivery</c>, Q1 or Q4 for <c>VoltageSignalIntegrity</c>. Records follow argument order, so passing
    /// the later category of the tie-break order first shows that the order, not the input, decides.
    /// </remarks>
    /// <param name="first">The step of the first record.</param>
    /// <param name="second">The step of the second record; a different index.</param>
    /// <returns>A new input.</returns>
    /// <exception cref="ArgumentException">A step holds, or both share an index.</exception>
    internal static AnalysisInput PriorOnlyTie(StepAttributes first, StepAttributes second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.HasHold || second.HasHold)
        {
            throw new ArgumentException("A hold step would fire the hold rule and break the prior-only tie.");
        }

        if (first.Index == second.Index)
        {
            throw new ArgumentException("The two steps must have different indices.", nameof(second));
        }

        var builder = new ErrorSetBuilder();
        builder.Add(first, SingleErrorOffset, [SingleErrorBit]);
        builder.Add(second, SingleErrorOffset + NearbyWordDistance, [SecondErrorBit]);
        return builder.Build([Failing(first, 1), Failing(second, 1)]);
    }

    /// <summary>Three errors at one word in Q5, found after the hold; Q5 is the only step.</summary>
    /// <remarks>Expected: hold 1.0 and cluster 1.0, so <c>RefreshRetention</c> ranks above <c>RankOrDimm</c>.</remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTieWithHold() => ClusterTie(Q5, [Failing(Q5, ClusterTieErrors)]);

    /// <summary>Three errors at one word in Q3 (turnaround reads), with a clean Q1.</summary>
    /// <remarks>
    /// Expected: turnaround 1.0 and cluster 1.0, so <c>WriteReadTurnaround</c> ranks above <c>RankOrDimm</c>; the
    /// bank-switching rule fires with score 0 (Q3 is neither bank-stride nor random-block).
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTieWithTurnaround() =>
        ClusterTie(Q3, [Clean(Q1), Failing(Q3, ClusterTieErrors)]);

    /// <summary>Three errors at one word in Q2 (bank stride), with a clean Q1.</summary>
    /// <remarks>
    /// Expected: bank switching 1.0 and cluster 1.0, so <c>RowBankTiming</c> ranks above <c>RankOrDimm</c>; the
    /// turnaround rule fires with score 0.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTieWithBankSwitch() =>
        ClusterTie(Q2, [Clean(Q1), Failing(Q2, ClusterTieErrors)]);

    /// <summary>Three errors at one word in Q6 (all threads, no hold), with a clean S11.</summary>
    /// <remarks>
    /// Expected: load 1.0 and cluster 1.0, so <c>ImcPowerDelivery</c> ranks above <c>RankOrDimm</c>; the turnaround
    /// rule fires with score 0 and the bank-switching rule does not fire (S11 is not sequential).
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTieWithLoad() =>
        ClusterTie(Q6, [Failing(Q6, ClusterTieErrors), Clean(S11)]);

    /// <summary>
    /// Five errors in Q1 alone: four on bit 17 at four adjacent words, one on another bit two windows away.
    /// </summary>
    /// <remarks>
    /// Expected: bit lane 4/5 = 0.8 (span two windows, dominant share at the minimum) and cluster 4/5 = 0.8, the same
    /// division, so <c>RankOrDimm</c> ranks above <c>VoltageSignalIntegrity</c> (whose prior is only 0.3).
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTieWithBitLane()
    {
        ulong window = WindowBytes(RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < 4; k++)
        {
            builder.Add(Q1, SingleErrorOffset + ((ulong)k * WordBytes), [InjectedFlipBit]);
        }

        builder.Add(Q1, SingleErrorOffset + (2 * window), [OtherBit(3)]);
        return builder.Build([Failing(Q1, 5)]);
    }

    /// <summary>Three errors at one word in Q2, with a clean Q1 and a clean S11.</summary>
    /// <remarks>
    /// Expected: a three-way tie at 1.0 between bank switching (<c>RowBankTiming</c>), load (<c>ImcPowerDelivery</c>,
    /// Q2 runs all threads without a hold) and cluster (<c>RankOrDimm</c>), ranked in that tie-break order; the
    /// turnaround rule fires with score 0.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ThreeWayTie() =>
        ClusterTie(Q2, [Clean(Q1), Failing(Q2, ClusterTieErrors), Clean(S11)]);

    /// <summary>
    /// One error in each of Q1, Q2, Q3, Q6 and S12, in step order, at offsets <c>k × RegionBytes / 5</c> (more than
    /// one window apart) on five different bits; every step fails.
    /// </summary>
    /// <remarks>
    /// Expected: cluster 0.2 (<c>RankOrDimm</c>) first, then a five-way prior tie at <c>0.3 × 0.2</c> in the
    /// tie-break order <c>RefreshRetention</c> (S12), <c>WriteReadTurnaround</c> (Q3), <c>RowBankTiming</c> (Q2),
    /// <c>ImcPowerDelivery</c> (Q6), <c>VoltageSignalIntegrity</c> (Q1), which is not the input order. The bit-lane
    /// rule fires with score 0 (dominant share 0.2). All six are kept.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput FivePriorTie()
    {
        StepAttributes[] steps = [Q1, Q2, Q3, Q6, S12];
        ulong[] offsets = EvenOffsets(steps.Length, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < steps.Length; k++)
        {
            builder.Add(steps[k], offsets[k], [OtherBit(k)]);
        }

        return builder.Build(steps.Select(s => Failing(s, 1)));
    }

    // ---- Keep-floor sets ----

    /// <summary>
    /// The hold share exactly at <see cref="AnalyzerThresholds.KeepFloor"/>: <c>1 / KeepFloor</c> errors (20) at evenly
    /// spaced words on different bits, all in a failing Q1 except the last, in a failing Q5 after its hold.
    /// </summary>
    /// <remarks>
    /// Expected: hold 1/20 = <c>KeepFloor</c>, so <c>RefreshRetention</c> is kept at exactly the floor (its prior,
    /// 0.3/20, is lower). Also kept: <c>VoltageSignalIntegrity</c> 0.3 × 19/20 (Q1 prior) and <c>RankOrDimm</c> 0.1
    /// (two words per window). The bit-lane rule scores 0.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput HoldShareAtKeepFloor() => HoldShareSet(KeepFloorTotal());

    /// <summary>
    /// The hold share below <see cref="AnalyzerThresholds.KeepFloor"/>: as <see cref="HoldShareAtKeepFloor"/> with
    /// <c>1.25 / KeepFloor</c> errors (25), so the hold share is 1/25.
    /// </summary>
    /// <remarks>
    /// Expected: hold 0.04 and prior 0.012, so <c>RefreshRetention</c> is dropped; <c>VoltageSignalIntegrity</c>
    /// 0.3 × 24/25 and <c>RankOrDimm</c> 2/25 are kept.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput HoldShareBelowKeepFloor() => HoldShareSet(KeepFloorTotal() * 5 / 4);

    // ---- Evidence-minimum boundary sets, one pair or more per rule ----

    /// <summary>Bit lane, not fired: three errors on bit 17 at one word in Q1 (one distinct word).</summary>
    /// <returns>A new input; the cluster rule fires with 1.0.</returns>
    internal static AnalysisInput BitLaneAllAtOneWord()
    {
        var builder = new ErrorSetBuilder();
        for (int pass = 0; pass < 3; pass++)
        {
            builder.Add(Q1, SingleErrorOffset, [InjectedFlipBit], pass);
        }

        return builder.Build([Failing(Q1, 3)]);
    }

    /// <summary>Bit lane, not fired: two words on bit 17 at offsets 0 and one window minus one word.</summary>
    /// <returns>A new input; the cluster rule does not fire either (two errors at distinct words).</returns>
    internal static AnalysisInput BitLaneSpanBelowWindow() =>
        BitLaneSpanSet(WindowBytes(RegionBytes512MiB) - WordBytes);

    /// <summary>
    /// Bit lane, fired: two words on bit 17 at offsets 0 and exactly one window; score 1.0, evidence
    /// "100 % of errors on bit 17 across 2 addresses".
    /// </summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput BitLaneSpanAtWindow() => BitLaneSpanSet(WindowBytes(RegionBytes512MiB));

    /// <summary>
    /// Bit lane dominant share: <paramref name="dominantCount"/> of <paramref name="totalCount"/> errors in Q1 flip
    /// bit 17, each other error a different lane, all at evenly spaced distinct words over the whole region.
    /// </summary>
    /// <remarks>
    /// Expected: the rule fires; 79 of 100 scores 0 (share below the 0.8 minimum), 80 of 100 and 4 of 5 score 0.8.
    /// The evidence counts <paramref name="dominantCount"/> addresses.
    /// </remarks>
    /// <param name="dominantCount">Errors on bit 17, at least 1.</param>
    /// <param name="totalCount">All errors; at most 31 more than <paramref name="dominantCount"/>.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput BitLaneDominantShare(int dominantCount, int totalCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dominantCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalCount, dominantCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalCount - dominantCount, 31, nameof(totalCount));
        ulong[] offsets = EvenOffsets(totalCount, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < totalCount; k++)
        {
            builder.Add(Q1, offsets[k], [k < dominantCount ? InjectedFlipBit : OtherBit(k - dominantCount)]);
        }

        return builder.Build([Failing(Q1, (ulong)totalCount)]);
    }

    /// <summary>Hold, not fired: one error in a failing Q6, the only step, so no hold step ran.</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput HoldWithoutHoldStep() => OneErrorIn(Q6, [Failing(Q6, 1)]);

    /// <summary>Hold, fired with score 0: one error in a failing Q6 after a clean Q5 (a hold step ran).</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput HoldWithHoldStepRun() => OneErrorIn(Q6, [Clean(Q5), Failing(Q6, 1)]);

    /// <summary>Bank switching, not fired: the sequential Q1 completed its passes but has its own error record.</summary>
    /// <returns>A new input with one error in Q1 and one in Q2, at nearby distinct words.</returns>
    internal static AnalysisInput BankSwitchSequentialFailing()
    {
        var builder = new ErrorSetBuilder();
        builder.Add(Q1, SingleErrorOffset, [SingleErrorBit]);
        builder.Add(Q2, SingleErrorOffset + NearbyWordDistance, [SecondErrorBit]);
        return builder.Build([Failing(Q1, 1), Failing(Q2, 1)]);
    }

    /// <summary>Bank switching, not fired: Q1 is error-free but completed no pass; one error in Q2.</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput BankSwitchSequentialWithoutPass() =>
        OneErrorIn(Q2, [CleanButNoPass(Q1), Failing(Q2, 1)]);

    /// <summary>
    /// Bank switching, not fired: Q1's true count is 3 but none of its records were stored, as after the detail cap;
    /// one stored error in Q2. Q1 is still not clean.
    /// </summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput BankSwitchSequentialTruncated() =>
        OneErrorIn(Q2, [Failing(Q1, 3), Failing(Q2, 1)]);

    /// <summary>Bank switching, fired with score 1.0: a clean Q1 and one error in Q2 (bank stride).</summary>
    /// <returns>A new input; the turnaround rule fires with score 0.</returns>
    internal static AnalysisInput BankSwitchSequentialClean() => OneErrorIn(Q2, [Clean(Q1), Failing(Q2, 1)]);

    /// <summary>
    /// Bank switching with mixed shares: a clean Q1; 10 errors at words more than one window apart, six in Q2, two in
    /// Q5 (after its hold) and two in Q6.
    /// </summary>
    /// <remarks>
    /// Expected: bank-stride share 0.6 plus 0.5 × random-block share 0.2 (Q5's hold errors add nothing) = 0.7, evidence
    /// "60 % of errors in bank-stride steps and 20 % in random-block steps, while a sequential step passed cleanly";
    /// <c>RowBankTiming</c> 0.7, <c>RefreshRetention</c> 0.2 (hold), <c>RankOrDimm</c> 0.1, <c>ImcPowerDelivery</c>
    /// 0.06 (prior).
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput BankSwitchMixedShares()
    {
        StepAttributes[] byError = [Q2, Q2, Q2, Q2, Q2, Q2, Q5, Q5, Q6, Q6];
        ulong[] offsets = EvenOffsets(byError.Length, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < byError.Length; k++)
        {
            builder.Add(byError[k], offsets[k], [OtherBit(k)]);
        }

        return builder.Build([Clean(Q1), Failing(Q2, 6), Failing(Q5, 2), Failing(Q6, 2)]);
    }


    /// <summary>
    /// Turnaround, not fired: one error in Q3; the only clean steps are a hold step (Q5) and a turnaround step (E14),
    /// neither of which is a contrast step.
    /// </summary>
    /// <returns>A new input; the hold rule fires with score 0.</returns>
    internal static AnalysisInput TurnaroundOnlyIneligibleStepsClean() =>
        OneErrorIn(Q3, [Failing(Q3, 1), Clean(Q5), Clean(E14)]);

    /// <summary>
    /// Turnaround, fired with score 1.0: one error in Q3 and a clean Q6 (random blocks, no hold), which also keeps the
    /// bank-switching rule from firing.
    /// </summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput TurnaroundContrastClean() => OneErrorIn(Q3, [Failing(Q3, 1), Clean(Q6)]);

    /// <summary>Load, not fired: the Half step S11 has its own error; one error each in Q6 and S11.</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput LoadHalfFailing()
    {
        var builder = new ErrorSetBuilder();
        builder.Add(Q6, SingleErrorOffset, [SingleErrorBit]);
        builder.Add(S11, SingleErrorOffset + NearbyWordDistance, [SecondErrorBit]);
        return builder.Build([Failing(Q6, 1), Failing(S11, 1)]);
    }

    /// <summary>Load, fired with score 1.0: one error in Q6 and a clean S11.</summary>
    /// <returns>A new input; the turnaround rule fires with score 0.</returns>
    internal static AnalysisInput LoadHalfClean() => OneErrorIn(Q6, [Failing(Q6, 1), Clean(S11)]);

    /// <summary>Cluster, not fired: two errors in Q6 at adjacent distinct words.</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTwoDistinctWords()
    {
        var builder = new ErrorSetBuilder();
        builder.Add(Q6, SingleErrorOffset, [SingleErrorBit]);
        builder.Add(Q6, SingleErrorOffset + WordBytes, [SecondErrorBit]);
        return builder.Build([Failing(Q6, 2)]);
    }

    /// <summary>Cluster, fired with score 1.0: two errors in Q6 at one word, in passes 0 and 1.</summary>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTwoAtOneWord()
    {
        var builder = new ErrorSetBuilder();
        builder.Add(Q6, SingleErrorOffset, [SingleErrorBit], passIndex: 0);
        builder.Add(Q6, SingleErrorOffset, [SingleErrorBit], passIndex: 1);
        return builder.Build([Failing(Q6, 2)]);
    }

    /// <summary>
    /// Cluster, fired: three errors in Q6 at distinct words a third of the region apart, so the densest window holds
    /// one and the score is 1/3.
    /// </summary>
    /// <returns>A new input; the bit-lane rule fires with score 0.</returns>
    internal static AnalysisInput ClusterThreeDistinctWords()
    {
        ulong[] offsets = EvenOffsets(3, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < offsets.Length; k++)
        {
            builder.Add(Q6, offsets[k], [OtherBit(k)]);
        }

        return builder.Build([Failing(Q6, 3)]);
    }

    /// <summary>
    /// Cluster and bit lane, not fired: three errors at one word, but the tested bytes,
    /// <c>RegionWindowDivisor − 1</c>, give a region window of 0.
    /// </summary>
    /// <returns>A new input whose only item is the Q6 prior.</returns>
    internal static AnalysisInput ClusterRegionBelowDivisor()
    {
        ulong testedBytes = (ulong)AnalyzerThresholds.RegionWindowDivisor - 1;
        var builder = new ErrorSetBuilder(testedBytes);
        for (int pass = 0; pass < 3; pass++)
        {
            builder.Add(Q6, 0, [SingleErrorBit], pass);
        }

        return builder.Build([Failing(Q6, 3)]);
    }

    // ---- Evidence-text sets ----

    /// <summary>
    /// The specification's bit-lane example: 50 errors in Q1; 46 flip bit 17 across 41 distinct evenly spaced words
    /// (the first five words twice), and four flip other bits at four further words.
    /// </summary>
    /// <remarks>Expected: bit lane 0.92, evidence exactly "92 % of errors on bit 17 across 41 addresses".</remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput AapBitLaneExample()
    {
        const int LaneWords = 41;
        const int Repeats = 5;
        int[] otherBits = [3, 30, 44, 60];
        ulong[] offsets = EvenOffsets(LaneWords, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < LaneWords; k++)
        {
            builder.Add(Q1, offsets[k], [InjectedFlipBit], passIndex: 0);
        }

        for (int k = 0; k < Repeats; k++)
        {
            builder.Add(Q1, offsets[k], [InjectedFlipBit], passIndex: 1);
        }

        for (int k = 0; k < otherBits.Length; k++)
        {
            builder.Add(Q1, offsets[(k + 1) * 10] + NearbyWordDistance, [otherBits[k]], passIndex: 1);
        }

        return builder.Build([Failing(Q1, (ulong)builder.Records.Count)]);
    }

    /// <summary>
    /// Bits 17 and 49 alternating over 20 evenly spaced words in Q1: one lane on DDR5 (bit index modulo 32), two on
    /// DDR4 (modulo 64).
    /// </summary>
    /// <remarks>
    /// Expected: on DDR4 the dominant lane is 17 (the lower of two lanes tied at 10), share 0.5, score 0, evidence
    /// "50 % of errors on bit 17 across 10 addresses"; on DDR5 share 1.0, score 1.0, evidence
    /// "100 % of errors on bit 17 or 49 across 20 addresses".
    /// </remarks>
    /// <param name="memoryType">The memory type, which fixes the lane modulus.</param>
    /// <returns>A new input.</returns>
    internal static AnalysisInput Ddr5LaneSet(MemoryType memoryType)
    {
        const int Total = 20;
        ulong[] offsets = EvenOffsets(Total, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < Total; k++)
        {
            builder.Add(Q1, offsets[k], [k % 2 == 0 ? InjectedFlipBit : Ddr5PartnerBit]);
        }

        return builder.Build([Failing(Q1, Total)], memoryType);
    }

    /// <summary>
    /// A cluster with an exactly computable extent: <see cref="ExactPercentTestedBytes"/> tested, three errors in Q6
    /// at offsets 0, 8 and 24,992 (all inside the 62,500-byte window).
    /// </summary>
    /// <remarks>
    /// Expected: cluster 1.0, extent (24,992 + 8) ÷ 1,000,000 = 2.5 %, evidence
    /// "100 % of errors concentrated in 2.5 % of the tested memory (virtual offsets)". The bit-lane span is below the
    /// window, so it does not fire.
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterExactPercent()
    {
        var builder = new ErrorSetBuilder(ExactPercentTestedBytes);
        builder.Add(Q6, 0, [SingleErrorBit]);
        builder.Add(Q6, WordBytes, [SecondErrorBit]);
        builder.Add(Q6, 24_992, [OtherBit(3)]);
        return builder.Build([Failing(Q6, 3)]);
    }

    /// <summary>
    /// A tiny cluster on a <see cref="RegionBytes192GiB"/> region: three errors in Q6 at three adjacent words in the
    /// middle of the region.
    /// </summary>
    /// <remarks>
    /// Expected: cluster 1.0 with a 24-byte extent, evidence
    /// "100 % of errors concentrated in &lt; 0.01 % of the tested memory (virtual offsets)".
    /// </remarks>
    /// <returns>A new input.</returns>
    internal static AnalysisInput ClusterTinyExtent()
    {
        ulong middle = RegionBytes192GiB / 2;
        var builder = new ErrorSetBuilder(RegionBytes192GiB);
        for (int k = 0; k < 3; k++)
        {
            builder.Add(Q6, middle + ((ulong)k * WordBytes), [OtherBit(k)]);
        }

        return builder.Build([Failing(Q6, 3)]);
    }

    // ---- Shared set shapes ----

    private static AnalysisInput ClusterTie(StepAttributes failing, IEnumerable<StepOutcome> outcomes)
    {
        var builder = new ErrorSetBuilder();
        for (int pass = 0; pass < ClusterTieErrors; pass++)
        {
            builder.Add(failing, SingleErrorOffset, [SingleErrorBit], pass);
        }

        return builder.Build(outcomes);
    }

    private static AnalysisInput OneErrorIn(StepAttributes failing, IEnumerable<StepOutcome> outcomes)
    {
        var builder = new ErrorSetBuilder();
        builder.Add(failing, SingleErrorOffset, [SingleErrorBit]);
        return builder.Build(outcomes);
    }

    private static AnalysisInput BitLaneSpanSet(ulong span)
    {
        var builder = new ErrorSetBuilder();
        builder.Add(Q1, 0, [InjectedFlipBit]);
        builder.Add(Q1, span, [InjectedFlipBit]);
        return builder.Build([Failing(Q1, 2)]);
    }

    private static int KeepFloorTotal() => (int)Math.Round(1d / AnalyzerThresholds.KeepFloor);

    private static AnalysisInput HoldShareSet(int total)
    {
        ulong[] offsets = EvenOffsets(total, RegionBytes512MiB);
        var builder = new ErrorSetBuilder();
        for (int k = 0; k < total - 1; k++)
        {
            builder.Add(Q1, offsets[k], [OtherBit(k)]);
        }

        builder.Add(Q5, offsets[total - 1], [SecondErrorBit]);
        return builder.Build([Failing(Q1, (ulong)(total - 1)), Failing(Q5, 1)]);
    }

    /// <summary>
    /// Accumulates the records of one set, numbering them in order and placing each on the worker that owns its
    /// offset, then wraps them with the set's outcomes.
    /// </summary>
    private sealed class ErrorSetBuilder
    {
        private readonly List<ErrorRecord> _records = [];
        private readonly ulong _testedBytes;
        private readonly int _workers;

        public ErrorSetBuilder(ulong testedBytes = RegionBytes512MiB, int workers = DefaultWorkers)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
            _testedBytes = testedBytes;
            _workers = workers;
        }

        public IReadOnlyList<ErrorRecord> Records => _records;

        public void Add(
            StepAttributes step,
            ulong regionOffset,
            IReadOnlyList<int> bits,
            int passIndex = 0,
            AccessType? accessType = null,
            TimeSpan? timeSinceWrite = null,
            ulong? expected = null)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(regionOffset, _testedBytes);
            _records.Add(Error(
                step,
                regionOffset,
                bits,
                passIndex,
                (ulong)_records.Count,
                accessType,
                timeSinceWrite,
                WorkerFor(regionOffset, _testedBytes, _workers, step.Threads),
                _workers,
                expected));
        }

        public AnalysisInput Build(IEnumerable<StepOutcome> outcomes, MemoryType memoryType = MemoryType.DDR4) =>
            Input(_records, outcomes, _testedBytes, _workers, memoryType);
    }
}

