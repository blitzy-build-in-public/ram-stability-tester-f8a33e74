using System.Collections.Immutable;
using RamQuick.Core;

namespace RamQuick.Plans;

/// <summary>
/// The built-in plans for the three test types: declarative, ordered lists of named steps, each
/// carrying its pattern, access order, thread mode, hold, time budget and coverage budget. The
/// engine's step runner interprets only that data, so a plan is retuned here and nowhere else.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// <description>
/// Standard and Exhaustive begin with Quick's six steps, the very same step instances and therefore
/// identical in every parameter and budget. The longer types receive the same exposure, in the same
/// order and at the same times, as Quick, which makes their failure times comparable to Quick's.
/// </description>
/// </item>
/// <item>
/// <description>
/// The step order is a design hypothesis, not a measured fact: dense full-bandwidth traffic from the
/// first second, with the no-wait stresses ahead of the steps that cost idle time, is expected to
/// expose marginal settings soonest. Hardware validation of fail-fast effectiveness (criterion 1)
/// tests it. If it is missed, step order and budgets are retuned in this file only, keeping the
/// shared opening block and Quick's 300 s total.
/// </description>
/// </item>
/// <item>
/// <description>
/// Coverage budgets are floors that time-boxed pass scheduling is expected to exceed, and they never
/// extend a step. Time governs pass scheduling, so fail-fast ordering and the plan durations are
/// never traded for coverage.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every step stays within the workload-referenced stress bound. Patterns, access orders, thread
/// modes and holds are the only levers: no step runs row-hammer-style loops or alternates over a
/// small address set. A step found to exceed the bound is retuned here only, for example to
/// <see cref="ThreadMode.Half"/> threads or with a hold.
/// </para>
/// <para>
/// Step names appear character for character in the step-to-mechanism map of
/// <c>docs/METHODOLOGY.md</c>, with the same primary mechanism, so renaming a step means changing
/// both; <c>TestPlanCatalogTests</c> fails otherwise. Display texts such as "Quick (~5 min)" belong
/// to the application, and scaled plans for tests are derived from these steps by the tests.
/// </para>
/// </remarks>
public static class TestPlans
{
    // Static initialisers run in textual order: the steps are declared first, then the step lists
    // built from them, then the plans built from the lists, so no plan can observe a null step.

    // ---- Quick: the shared opening block, 300 s, 200 % ----

    // Q1 leads: signal-integrity and VDDQ margins fail on the first heavy transfers at high
    // frequency, and a too-low tRFC surfaces under any dense traffic.
    private static readonly StepDefinition _q1HighToggleSweep = new()
    {
        Name = "High-toggle sweep",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.SequentialAscending,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(40),
        CoverageBudgetPercent = 25,
        PrimaryMechanism = InstabilityMechanism.SignalIntegrity,
        Mechanisms = InstabilityMechanism.SignalIntegrity
            | InstabilityMechanism.VddVddqVoltage
            | InstabilityMechanism.RefreshTimings,
    };

    // Q2 second: row and bank activation pressure, which most often limits tightened primary
    // timings, needs no wait and runs at full bandwidth.
    private static readonly StepDefinition _q2RandomBankThrash = new()
    {
        Name = "Random bank thrash",
        Pattern = PatternKind.Random,
        Order = AccessOrder.BankStride,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.RowTimings,
        Mechanisms = InstabilityMechanism.RowTimings
            | InstabilityMechanism.ActivationWindow
            | InstabilityMechanism.BankRankChannelSwitching,
    };

    // Q3 third: write-to-read turnaround, the other no-wait access-order stress that most often
    // limits tightened secondary timings.
    private static readonly StepDefinition _q3ReadAfterWriteTurnaround = new()
    {
        Name = "Read-after-write turnaround",
        Pattern = PatternKind.Random,
        Order = AccessOrder.Turnaround,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.ReadWriteTurnaround,
        Mechanisms = InstabilityMechanism.ReadWriteTurnaround
            | InstabilityMechanism.WriteTimings,
    };

    // Q4 after the access-order stresses: the data-dependent cell-margin check needs several
    // sweeps. Moving inversion always sweeps ascending then descending, and Order is single-valued,
    // so every moving-inversion step stores SequentialAscending and the engine ignores it.
    private static readonly StepDefinition _q4MovingInversionWalkingBits = new()
    {
        Name = "Moving inversion, walking bits",
        Pattern = PatternKind.MovingInversionWalking,
        Order = AccessOrder.SequentialAscending,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(60),
        CoverageBudgetPercent = 50,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    // Q5 after the no-wait steps because a hold costs idle time. Hold passes write the whole slice
    // in RandomBlocks order, so every hold step uses that order.
    private static readonly StepDefinition _q5RefreshHold = new()
    {
        Name = "Refresh hold",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.FromSeconds(5),
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 20,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings
            | InstabilityMechanism.TemperatureSensitivity,
    };

    // Q6 closes Quick with sustained all-thread load that also warms the DIMMs ahead of the longer
    // types' contrast and heat-soak steps.
    private static readonly StepDefinition _q6FullLoadRandomMix = new()
    {
        Name = "Full-load random mix",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(50),
        CoverageBudgetPercent = 35,
        PrimaryMechanism = InstabilityMechanism.SocImcVoltage,
        Mechanisms = InstabilityMechanism.SocImcVoltage
            | InstabilityMechanism.BankRankChannelSwitching,
    };

    // ---- Standard: Quick, then S7-S12, 1200 s, 800 % ----

    // S7 opens the extension with random-data moving inversion, reaching data-dependent margins
    // that walking bits leave untouched.
    private static readonly StepDefinition _s7MovingInversionRandom = new()
    {
        Name = "Moving inversion, random",
        Pattern = PatternKind.MovingInversionRandom,
        Order = AccessOrder.SequentialAscending,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(150),
        CoverageBudgetPercent = 120,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    // S8: the engine inverts Checkerboard every pass, so this one pattern covers the checkerboard
    // and its inverse for adjacent-bit coupling; descending order sweeps opposite to Q1.
    private static readonly StepDefinition _s8CheckerboardAndInverted = new()
    {
        Name = "Checkerboard and inverted",
        Pattern = PatternKind.Checkerboard,
        Order = AccessOrder.SequentialDescending,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 80,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    // S9 combines high-toggle data with bank-stride order, so bus switching meets bank, rank and
    // channel changes.
    private static readonly StepDefinition _s9HighToggleBankThrash = new()
    {
        Name = "High-toggle bank thrash",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.BankStride,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 100,
        PrimaryMechanism = InstabilityMechanism.BankRankChannelSwitching,
        Mechanisms = InstabilityMechanism.BankRankChannelSwitching
            | InstabilityMechanism.SignalIntegrity,
    };

    // S10 holds six times longer than Q5, reaching retention margins a 5 s idle window cannot.
    private static readonly StepDefinition _s10RefreshHoldLong = new()
    {
        Name = "Refresh hold, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.FromSeconds(30),
        TimeBudget = TimeSpan.FromSeconds(150),
        CoverageBudgetPercent = 40,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings,
    };

    // S11/E17 Half threads: contrast step for the SoC/IMC load rule, since errors seen at full load
    // and absent here point at IMC voltage or power delivery.
    private static readonly StepDefinition _s11ReducedLoadContrast = new()
    {
        Name = "Reduced-load contrast",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.Half,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(120),
        CoverageBudgetPercent = 60,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage
            | InstabilityMechanism.SocImcVoltage,
    };

    // S12 last: the heat soak runs once the DIMMs are already warm, to catch warm-up failures.
    private static readonly StepDefinition _s12HeatSoak = new()
    {
        Name = "Heat soak",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(240),
        CoverageBudgetPercent = 200,
        PrimaryMechanism = InstabilityMechanism.TemperatureSensitivity,
        Mechanisms = InstabilityMechanism.TemperatureSensitivity
            | InstabilityMechanism.SocImcVoltage,
    };

    // ---- Exhaustive: Standard, then E13-E19, 3600 s, 2500 % ----

    // E13 repeats the walking-bit inversion at length, so walking ones and walking zeros, which
    // alternate per pass, each get many passes.
    private static readonly StepDefinition _e13MovingInversionWalkingOnesAndZeros = new()
    {
        Name = "Moving inversion, walking ones and zeros",
        Pattern = PatternKind.MovingInversionWalking,
        Order = AccessOrder.SequentialAscending,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage,
    };

    // E14 extends Q3's turnaround stress to reach write-recovery margins too rare for 50 s.
    private static readonly StepDefinition _e14RandomTurnaroundLong = new()
    {
        Name = "Random turnaround, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.Turnaround,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.WriteTimings,
        Mechanisms = InstabilityMechanism.WriteTimings,
    };

    // E15 extends Q2's bank thrash so dense activations across banks probe tFAW/tRRD at length.
    private static readonly StepDefinition _e15RandomBankThrashLong = new()
    {
        Name = "Random bank thrash, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.BankStride,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.ActivationWindow,
        Mechanisms = InstabilityMechanism.ActivationWindow
            | InstabilityMechanism.RowTimings,
    };

    // E16 holds longest, 120 s, on DIMMs warmed by the steps before it, for the weakest retention
    // margins.
    private static readonly StepDefinition _e16RefreshHoldExtended = new()
    {
        Name = "Refresh hold, extended",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.FromSeconds(120),
        TimeBudget = TimeSpan.FromSeconds(360),
        CoverageBudgetPercent = 50,
        PrimaryMechanism = InstabilityMechanism.RefreshTimings,
        Mechanisms = InstabilityMechanism.RefreshTimings,
    };

    // E17 Half threads: the long SoC/IMC contrast step, as S11.
    private static readonly StepDefinition _e17ReducedLoadContrastLong = new()
    {
        Name = "Reduced-load contrast, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.Half,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(240),
        CoverageBudgetPercent = 150,
        PrimaryMechanism = InstabilityMechanism.VddVddqVoltage,
        Mechanisms = InstabilityMechanism.VddVddqVoltage
            | InstabilityMechanism.SocImcVoltage,
    };

    // E18 pairs high-toggle data with random-block order, so bus toggling meets row misses rather
    // than Q1's sequential stream.
    private static readonly StepDefinition _e18HighToggleRandomOrder = new()
    {
        Name = "High-toggle random order",
        Pattern = PatternKind.HighToggle,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(300),
        CoverageBudgetPercent = 250,
        PrimaryMechanism = InstabilityMechanism.SignalIntegrity,
        Mechanisms = InstabilityMechanism.SignalIntegrity,
    };

    // E19 closes the plan with the longest heat soak, run at the highest DIMM temperature the
    // plan reaches.
    private static readonly StepDefinition _e19HeatSoakLong = new()
    {
        Name = "Heat soak, long",
        Pattern = PatternKind.Random,
        Order = AccessOrder.RandomBlocks,
        Threads = ThreadMode.All,
        Hold = TimeSpan.Zero,
        TimeBudget = TimeSpan.FromSeconds(600),
        CoverageBudgetPercent = 500,
        PrimaryMechanism = InstabilityMechanism.TemperatureSensitivity,
        Mechanisms = InstabilityMechanism.TemperatureSensitivity
            | InstabilityMechanism.SocImcVoltage,
    };

    // ---- Step lists: each longer type spreads the shorter type's list, sharing its instances ----

    private static readonly ImmutableArray<StepDefinition> _quickSteps =
    [
        _q1HighToggleSweep,
        _q2RandomBankThrash,
        _q3ReadAfterWriteTurnaround,
        _q4MovingInversionWalkingBits,
        _q5RefreshHold,
        _q6FullLoadRandomMix,
    ];

    private static readonly ImmutableArray<StepDefinition> _standardSteps =
    [
        .. _quickSteps,
        _s7MovingInversionRandom,
        _s8CheckerboardAndInverted,
        _s9HighToggleBankThrash,
        _s10RefreshHoldLong,
        _s11ReducedLoadContrast,
        _s12HeatSoak,
    ];

    private static readonly ImmutableArray<StepDefinition> _exhaustiveSteps =
    [
        .. _standardSteps,
        _e13MovingInversionWalkingOnesAndZeros,
        _e14RandomTurnaroundLong,
        _e15RandomBankThrashLong,
        _e16RefreshHoldExtended,
        _e17ReducedLoadContrastLong,
        _e18HighToggleRandomOrder,
        _e19HeatSoakLong,
    ];

    /// <summary>
    /// Gets the Quick plan: six steps lasting 300 s (about 5 minutes) with a coverage floor of
    /// 200 %, ordered fail-fast for the tune-test-adjust loop.
    /// </summary>
    /// <remarks>
    /// Its steps, in order: high-toggle sweep, random bank thrash, read-after-write turnaround,
    /// moving inversion over walking bits, a 5 s refresh hold and a full-load random mix. They
    /// form the opening block of <see cref="Standard"/> and <see cref="Exhaustive"/>.
    /// </remarks>
    public static TestPlan Quick { get; } = new(TestType.Quick, _quickSteps);

    /// <summary>
    /// Gets the Standard plan: twelve steps lasting 1200 s (about 20 minutes) with a coverage floor
    /// of 800 %, confirming a profile before daily use.
    /// </summary>
    /// <remarks>
    /// Quick's six steps, followed by random-data moving inversion, checkerboard and inverted,
    /// high-toggle bank thrash, a 30 s refresh hold, a reduced-load contrast at half the threads
    /// and a heat soak.
    /// </remarks>
    public static TestPlan Standard { get; } = new(TestType.Standard, _standardSteps);

    /// <summary>
    /// Gets the Exhaustive plan: nineteen steps lasting 3600 s (about 60 minutes) with a coverage
    /// floor of 2500 %, the most thorough confirmation before daily use.
    /// </summary>
    /// <remarks>
    /// Standard's twelve steps, followed by longer runs of each stress: walking ones and zeros
    /// inversion, random turnaround, random bank thrash, a 120 s refresh hold, a long reduced-load
    /// contrast, high-toggle data in random order and a long heat soak.
    /// </remarks>
    public static TestPlan Exhaustive { get; } = new(TestType.Exhaustive, _exhaustiveSteps);

    /// <summary>
    /// Gets every built-in plan in <see cref="TestType"/> order: <see cref="Quick"/>,
    /// <see cref="Standard"/>, <see cref="Exhaustive"/>.
    /// </summary>
    public static IReadOnlyList<TestPlan> All { get; } = Array.AsReadOnly(new[] { Quick, Standard, Exhaustive });

    /// <summary>
    /// Returns the built-in plan for a test type.
    /// </summary>
    /// <param name="type">The test type.</param>
    /// <returns>
    /// The same instance as <see cref="Quick"/>, <see cref="Standard"/> or <see cref="Exhaustive"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not a defined <see cref="TestType"/>, which is a programming error.
    /// </exception>
    public static TestPlan Get(TestType type) => type switch
    {
        TestType.Quick => Quick,
        TestType.Standard => Standard,
        TestType.Exhaustive => Exhaustive,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, $"Undefined test type {(int)type}."),
    };
}

