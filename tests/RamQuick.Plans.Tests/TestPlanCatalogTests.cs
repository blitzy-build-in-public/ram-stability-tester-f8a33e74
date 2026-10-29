using System.Numerics;
using System.Text;
using RamQuick.Core;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.Plans.Tests;

/// <summary>
/// Tests for the built-in plan catalogue in <see cref="TestPlans"/>: every step against the per-type
/// step plans, the duration and coverage targets, the shared opening block, mechanism and name
/// integrity, the <see cref="TestPlan"/> and <see cref="StepDefinition"/> API, and the
/// step-to-mechanism map of <c>docs/METHODOLOGY.md</c>, which must list every step with its primary
/// mechanism and map every mechanism.
/// </summary>
public sealed class TestPlanCatalogTests
{
    private const string MapHeading = "## Step-to-mechanism map";
    private const string MethodologyDocument = "docs/METHODOLOGY.md";
    private const string EmDash = "\u2014";

    // Column positions of the step table, fixed by its contract header.
    private const int IdColumn = 0;
    private const int NameColumn = 1;
    private const int TypesColumn = 2;
    private const int PatternColumn = 3;
    private const int OrderColumn = 4;
    private const int ThreadsColumn = 5;
    private const int HoldColumn = 6;
    private const int PrimaryColumn = 7;
    private const int SecondaryColumn = 8;

    private static readonly string[] _stepTableHeader =
    [
        "ID", "Step name", "Types", "Pattern", "Order", "Threads", "Hold", "Primary mechanism", "Secondary mechanisms",
    ];

    // The per-type step plans, in Exhaustive order: Quick is the first six rows and Standard the
    // first twelve. Moving inversion always sweeps ascending then descending, and the plans store
    // SequentialAscending for those steps (Q4, S7, E13), which the engine ignores.
    private static readonly ExpectedStep[] _expectedSteps =
    [
        new("Q1", "High-toggle sweep", PatternKind.HighToggle, AccessOrder.SequentialAscending, ThreadMode.All, 0, 40, 25, InstabilityMechanism.SignalIntegrity),
        new("Q2", "Random bank thrash", PatternKind.Random, AccessOrder.BankStride, ThreadMode.All, 0, 50, 35, InstabilityMechanism.RowTimings),
        new("Q3", "Read-after-write turnaround", PatternKind.Random, AccessOrder.Turnaround, ThreadMode.All, 0, 50, 35, InstabilityMechanism.ReadWriteTurnaround),
        new("Q4", "Moving inversion, walking bits", PatternKind.MovingInversionWalking, AccessOrder.SequentialAscending, ThreadMode.All, 0, 60, 50, InstabilityMechanism.VddVddqVoltage),
        new("Q5", "Refresh hold", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 5, 50, 20, InstabilityMechanism.RefreshTimings),
        new("Q6", "Full-load random mix", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 0, 50, 35, InstabilityMechanism.SocImcVoltage),
        new("S7", "Moving inversion, random", PatternKind.MovingInversionRandom, AccessOrder.SequentialAscending, ThreadMode.All, 0, 150, 120, InstabilityMechanism.VddVddqVoltage),
        new("S8", "Checkerboard and inverted", PatternKind.Checkerboard, AccessOrder.SequentialDescending, ThreadMode.All, 0, 120, 80, InstabilityMechanism.VddVddqVoltage),
        new("S9", "High-toggle bank thrash", PatternKind.HighToggle, AccessOrder.BankStride, ThreadMode.All, 0, 120, 100, InstabilityMechanism.BankRankChannelSwitching),
        new("S10", "Refresh hold, long", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 30, 150, 40, InstabilityMechanism.RefreshTimings),
        new("S11", "Reduced-load contrast", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.Half, 0, 120, 60, InstabilityMechanism.VddVddqVoltage),
        new("S12", "Heat soak", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 0, 240, 200, InstabilityMechanism.TemperatureSensitivity),
        new("E13", "Moving inversion, walking ones and zeros", PatternKind.MovingInversionWalking, AccessOrder.SequentialAscending, ThreadMode.All, 0, 300, 250, InstabilityMechanism.VddVddqVoltage),
        new("E14", "Random turnaround, long", PatternKind.Random, AccessOrder.Turnaround, ThreadMode.All, 0, 300, 250, InstabilityMechanism.WriteTimings),
        new("E15", "Random bank thrash, long", PatternKind.Random, AccessOrder.BankStride, ThreadMode.All, 0, 300, 250, InstabilityMechanism.ActivationWindow),
        new("E16", "Refresh hold, extended", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 120, 360, 50, InstabilityMechanism.RefreshTimings),
        new("E17", "Reduced-load contrast, long", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.Half, 0, 240, 150, InstabilityMechanism.VddVddqVoltage),
        new("E18", "High-toggle random order", PatternKind.HighToggle, AccessOrder.RandomBlocks, ThreadMode.All, 0, 300, 250, InstabilityMechanism.SignalIntegrity),
        new("E19", "Heat soak, long", PatternKind.Random, AccessOrder.RandomBlocks, ThreadMode.All, 0, 600, 500, InstabilityMechanism.TemperatureSensitivity),
    ];

    public static TheoryData<TestType> TestTypes { get; } = new(Enum.GetValues<TestType>());

    public static TheoryData<int> ExhaustiveStepIndices { get; } = new(Enumerable.Range(0, _expectedSteps.Length));

    // ---- Catalogue ----

    [Theory]
    [InlineData(TestType.Quick, 6, 300, 200)]
    [InlineData(TestType.Standard, 12, 1200, 800)]
    [InlineData(TestType.Exhaustive, 19, 3600, 2500)]
    public void Plan_BuiltInType_HasPlannedStepCountDurationAndCoverage(TestType type, int stepCount, int seconds, int coveragePercent)
    {
        TestPlan plan = TestPlans.Get(type);

        PlanAssert.Equal(stepCount, plan.Steps.Count, $"{type} step count");
        PlanAssert.Equal(TimeSpan.FromSeconds(seconds), plan.TargetDuration, $"{type} target duration");
        PlanAssert.Equal(coveragePercent, plan.TargetCoveragePercent, $"{type} target coverage percent");
        PlanAssert.Equal(SumOfTimeBudgets(plan.Steps), plan.TargetDuration, $"{type} target duration against the sum of its step time budgets");
        PlanAssert.Equal(plan.Steps.Sum(step => step.CoverageBudgetPercent), plan.TargetCoveragePercent, $"{type} target coverage against the sum of its step coverage budgets");
    }

    [Fact]
    public void Steps_LongerTypes_OpenWithEveryStepOfTheShorterTypes()
    {
        AssertOpensWith(TestPlans.Standard, TestPlans.Quick);
        AssertOpensWith(TestPlans.Exhaustive, TestPlans.Quick);
        AssertOpensWith(TestPlans.Exhaustive, TestPlans.Standard);
    }

    [Fact]
    public void Exhaustive_StepCount_MatchesPerTypeStepPlan()
    {
        PlanAssert.Equal(_expectedSteps.Length, TestPlans.Exhaustive.Steps.Count, "Exhaustive step count against the per-type step plan");
    }

    [Theory]
    [MemberData(nameof(ExhaustiveStepIndices))]
    public void ExhaustiveStep_AtIndex_MatchesPerTypeStepPlan(int index)
    {
        ExpectedStep expected = _expectedSteps[index];
        IReadOnlyList<StepDefinition> steps = TestPlans.Exhaustive.Steps;
        Assert.True(index < steps.Count, $"Exhaustive has no step at index {index}; expected {expected.Id} '{expected.Name}'.");

        StepDefinition step = steps[index];
        string context = $"Exhaustive step {expected.Id} '{expected.Name}' (index {index})";

        PlanAssert.Equal(expected.Name, step.Name, $"{context} name");
        PlanAssert.Equal(expected.Pattern, step.Pattern, $"{context} pattern");
        PlanAssert.Equal(expected.Order, step.Order, $"{context} access order");
        PlanAssert.Equal(expected.Threads, step.Threads, $"{context} thread mode");
        PlanAssert.Equal(TimeSpan.FromSeconds(expected.HoldSeconds), step.Hold, $"{context} hold");
        PlanAssert.Equal(TimeSpan.FromSeconds(expected.BudgetSeconds), step.TimeBudget, $"{context} time budget");
        PlanAssert.Equal(expected.CoveragePercent, step.CoverageBudgetPercent, $"{context} coverage budget percent");
        PlanAssert.Equal(expected.Primary, step.PrimaryMechanism, $"{context} primary mechanism");
        PlanAssert.Equal(step.Hold > TimeSpan.Zero, step.HasHold, $"{context} HasHold");
    }

    [Fact]
    public void AllPlans_StepMechanisms_HoldOneDefinedPrimaryAndOnlyDefinedBits()
    {
        InstabilityMechanism allDefined = Enum.GetValues<InstabilityMechanism>()
            .Aggregate(InstabilityMechanism.None, (all, mechanism) => all | mechanism);
        var problems = new List<string>();

        foreach (TestPlan plan in TestPlans.All)
        {
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                StepDefinition step = plan.Steps[index];
                string label = $"{plan.Type} step '{step.Name}' (index {index})";
                uint primaryBits = (uint)step.PrimaryMechanism;

                if (primaryBits == 0 || !BitOperations.IsPow2(primaryBits) || !Enum.IsDefined(step.PrimaryMechanism))
                {
                    problems.Add($"{label} has primary mechanism '{step.PrimaryMechanism}', which is not exactly one defined InstabilityMechanism bit.");
                }

                if (!step.Mechanisms.HasFlag(step.PrimaryMechanism))
                {
                    problems.Add($"{label} lists mechanisms '{step.Mechanisms}', which do not include its primary mechanism '{step.PrimaryMechanism}'.");
                }

                InstabilityMechanism undefinedBits = step.Mechanisms & ~allDefined;
                if (undefinedBits != InstabilityMechanism.None)
                {
                    problems.Add($"{label} lists undefined mechanism bits 0x{(uint)undefinedBits:X8}.");
                }
            }
        }

        PlanAssert.NoProblems(problems);
    }

    [Fact]
    public void Exhaustive_PrimaryMechanisms_CoverEveryMechanism()
    {
        InstabilityMechanism[] mechanisms = SingleMechanisms();
        Assert.Equal(10, mechanisms.Length);

        HashSet<InstabilityMechanism> primaries = TestPlans.Exhaustive.Steps.Select(step => step.PrimaryMechanism).ToHashSet();
        string[] missing = mechanisms.Where(mechanism => !primaries.Contains(mechanism)).Select(mechanism => mechanism.ToString()).ToArray();

        if (missing.Length > 0)
        {
            Assert.Fail($"No Exhaustive step has primary mechanism {string.Join(", ", missing)}.");
        }
    }

    [Fact]
    public void HoldSteps_TimeBudget_AtLeastMinimumRatioOfHold()
    {
        Assert.Equal(2.5, TestPlanValidator.MinimumHoldBudgetRatio);

        string[] expectedHoldSteps = _expectedSteps.Where(step => step.HoldSeconds > 0).Select(step => step.Name).ToArray();
        string[] holdSteps = TestPlans.Exhaustive.Steps.Where(step => step.HasHold).Select(step => step.Name).ToArray();
        PlanAssert.Equal(3, expectedHoldSteps.Length, "Hold steps in the per-type step plan");
        PlanAssert.Equal(string.Join(" | ", expectedHoldSteps), string.Join(" | ", holdSteps), "Exhaustive hold steps");

        var problems = new List<string>();
        foreach (TestPlan plan in TestPlans.All)
        {
            foreach (StepDefinition step in plan.Steps.Where(step => step.HasHold))
            {
                if (step.TimeBudget.TotalMilliseconds < TestPlanValidator.MinimumHoldBudgetRatio * step.Hold.TotalMilliseconds)
                {
                    problems.Add($"{plan.Type} step '{step.Name}' holds for {step.Hold} but has a time budget of {step.TimeBudget}, less than {TestPlanValidator.MinimumHoldBudgetRatio} times its hold.");
                }
            }
        }

        PlanAssert.NoProblems(problems);
    }

    [Theory]
    [MemberData(nameof(TestTypes))]
    public void Plan_StepNames_AreUniqueAndFitTheNativeNameField(TestType type)
    {
        Assert.Equal(63, StepDefinition.MaxNameUtf8Bytes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        IReadOnlyList<StepDefinition> steps = TestPlans.Get(type).Steps;

        for (int index = 0; index < steps.Count; index++)
        {
            string name = steps[index].Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"{type} step at index {index} has a blank name.");
                continue;
            }

            if (name.Contains('\0', StringComparison.Ordinal))
            {
                problems.Add($"{type} step '{name.Replace("\0", "\\0", StringComparison.Ordinal)}' (index {index}) contains a NUL character.");
            }

            int utf8Bytes = Encoding.UTF8.GetByteCount(name);
            if (utf8Bytes > StepDefinition.MaxNameUtf8Bytes)
            {
                problems.Add($"{type} step '{name}' (index {index}) is {utf8Bytes} UTF-8 bytes, more than {StepDefinition.MaxNameUtf8Bytes}.");
            }

            if (!seen.Add(name))
            {
                problems.Add($"{type} step '{name}' (index {index}) repeats an earlier step name.");
            }
        }

        PlanAssert.NoProblems(problems);
    }

    // ---- TestPlans.Get, TestPlans.All and TestType ----

    [Theory]
    [MemberData(nameof(TestTypes))]
    public void Get_DefinedTestType_ReturnsTheMatchingBuiltInPlan(TestType type)
    {
        TestPlan expected = type switch
        {
            TestType.Quick => TestPlans.Quick,
            TestType.Standard => TestPlans.Standard,
            TestType.Exhaustive => TestPlans.Exhaustive,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No built-in plan property for this test type."),
        };

        TestPlan plan = TestPlans.Get(type);

        Assert.Same(expected, plan);
        Assert.Equal(type, plan.Type);
    }

    [Fact]
    public void All_BuiltInPlans_ListedInTestTypeOrder()
    {
        Assert.Collection(
            TestPlans.All,
            plan => Assert.Same(TestPlans.Quick, plan),
            plan => Assert.Same(TestPlans.Standard, plan),
            plan => Assert.Same(TestPlans.Exhaustive, plan));

        for (int index = 0; index < TestPlans.All.Count; index++)
        {
            PlanAssert.Equal((TestType)index, TestPlans.All[index].Type, $"Type of TestPlans.All[{index}]");
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Get_UndefinedTestType_ThrowsArgumentOutOfRange(int value)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => TestPlans.Get((TestType)value));

        Assert.Equal("type", exception.ParamName);
    }

    [Fact]
    public void TestType_DefaultValue_IsQuick()
    {
        Assert.Equal(TestType.Quick, default(TestType));
    }

    // ---- ToStepAttributes and ToAttributes ----

    [Theory]
    [MemberData(nameof(TestTypes))]
    public void ToStepAttributes_BuiltInPlan_CopiesEveryStepWithItsZeroBasedIndex(TestType type)
    {
        TestPlan plan = TestPlans.Get(type);

        IReadOnlyList<StepAttributes> attributes = plan.ToStepAttributes();

        PlanAssert.Equal(plan.Steps.Count, attributes.Count, $"{type} step attribute count");
        for (int index = 0; index < plan.Steps.Count; index++)
        {
            StepDefinition step = plan.Steps[index];
            PlanAssert.Equal(ExpectedAttributes(step, index), attributes[index], $"{type} step attributes at index {index}");
            PlanAssert.Equal(step.HasHold, attributes[index].HasHold, $"{type} step attributes HasHold at index {index}");
        }
    }

    [Fact]
    public void ToAttributes_ExplicitIndex_CarriesTheIndexAndEveryStepValue()
    {
        StepDefinition step = TestPlans.Quick.Steps[2];

        StepAttributes attributes = step.ToAttributes(7);

        Assert.Equal(7, attributes.Index);
        Assert.Equal(ExpectedAttributes(step, 7), attributes);
    }

    [Fact]
    public void ToAttributes_NegativeIndex_ThrowsArgumentOutOfRange()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => TestPlans.Quick.Steps[0].ToAttributes(-1));

        Assert.Equal("index", exception.ParamName);
    }

    // ---- TestPlan and StepDefinition construction ----

    [Fact]
    public void Constructor_CallerChangesItsList_PlanKeepsItsSnapshot()
    {
        var steps = new List<StepDefinition> { TestPlans.Quick.Steps[0], TestPlans.Quick.Steps[1] };

        var plan = new TestPlan(TestType.Standard, steps);
        steps.Add(TestPlans.Quick.Steps[2]);

        Assert.Equal(TestType.Standard, plan.Type);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Same(TestPlans.Quick.Steps[0], plan.Steps[0]);
        Assert.Same(TestPlans.Quick.Steps[1], plan.Steps[1]);
        Assert.Equal(TimeSpan.FromSeconds(40 + 50), plan.TargetDuration);
        Assert.Equal(25 + 35, plan.TargetCoveragePercent);

        var view = plan.Steps as IList<StepDefinition>;
        Assert.NotNull(view);
        Assert.True(view.IsReadOnly, "TestPlan.Steps must be a read-only view of the snapshot.");
        Assert.Throws<NotSupportedException>(() => view.Add(TestPlans.Quick.Steps[2]));
    }

    [Fact]
    public void Constructor_NoSteps_HasZeroTargetsAndNoAttributes()
    {
        var plan = new TestPlan(TestType.Quick, []);

        Assert.Empty(plan.Steps);
        Assert.Equal(TimeSpan.Zero, plan.TargetDuration);
        Assert.Equal(0, plan.TargetCoveragePercent);
        Assert.Empty(plan.ToStepAttributes());
    }

    [Fact]
    public void Constructor_NullSteps_ThrowsArgumentNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new TestPlan(TestType.Quick, null!));

        Assert.Equal("steps", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullStep_ThrowsArgument()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new TestPlan(TestType.Quick, [TestPlans.Quick.Steps[0], null!]));

        Assert.Equal("steps", exception.ParamName);
    }

    [Fact]
    public void Constructor_CoverageSumBeyondInt32_ThrowsOverflow()
    {
        StepDefinition huge = TestPlans.Quick.Steps[0] with { CoverageBudgetPercent = int.MaxValue };

        Assert.Throws<OverflowException>(() => new TestPlan(TestType.Quick, [huge, huge with { Name = "Second" }]));
    }

    [Fact]
    public void Constructor_DurationSumBeyondTimeSpan_ThrowsOverflow()
    {
        StepDefinition endless = TestPlans.Quick.Steps[0] with { TimeBudget = TimeSpan.MaxValue };

        Assert.Throws<OverflowException>(() => new TestPlan(TestType.Quick, [endless, endless with { Name = "Second" }]));
    }

    [Fact]
    public void StepDefinition_OptionalMembersOmitted_DefaultsToAllThreadsWithoutHold()
    {
        var step = new StepDefinition
        {
            Name = "Defaults",
            Pattern = PatternKind.Random,
            Order = AccessOrder.RandomBlocks,
            TimeBudget = TimeSpan.FromSeconds(10),
            CoverageBudgetPercent = 5,
            PrimaryMechanism = InstabilityMechanism.SocImcVoltage,
            Mechanisms = InstabilityMechanism.SocImcVoltage,
        };

        Assert.Equal(ThreadMode.All, step.Threads);
        Assert.Equal(TimeSpan.Zero, step.Hold);
        Assert.False(step.HasHold);
        Assert.Empty(TestPlanValidator.Validate(new TestPlan(TestType.Quick, [step])));
    }

    [Fact]
    public void StepDefinition_SameValues_AreEqualByValue()
    {
        StepDefinition original = TestPlans.Quick.Steps[4];
        StepDefinition copy = original with { };

        Assert.NotSame(original, copy);
        Assert.Equal(original, copy);
        Assert.NotEqual(original, copy with { CoverageBudgetPercent = original.CoverageBudgetPercent + 1 });
    }


    // ---- Step-to-mechanism map of docs/METHODOLOGY.md ----

    [Fact]
    public async Task MethodologyMap_StepTable_HasTheContractHeaderAndNineCellsPerRowAsync()
    {
        IReadOnlyList<MarkdownTable> tables = await LoadMapTablesAsync();

        MarkdownTable table = StepTable(tables);

        Assert.NotEmpty(table.Rows);
    }

    [Fact]
    public async Task MethodologyMap_EveryPlanStep_ListedOnceWithItsPrimaryMechanismAsync()
    {
        MarkdownTable table = StepTable(await LoadMapTablesAsync());
        StepDefinition[] steps = TestPlans.All
            .SelectMany(plan => plan.Steps)
            .DistinctBy(step => step.Name, StringComparer.Ordinal)
            .ToArray();
        PlanAssert.Equal(_expectedSteps.Length, steps.Length, "Distinct step names across TestPlans.All");

        var problems = new List<string>();
        foreach (StepDefinition step in steps)
        {
            IReadOnlyList<string>[] rows = table.Rows
                .Where(row => string.Equals(row[NameColumn], step.Name, StringComparison.Ordinal))
                .ToArray();

            if (rows.Length == 0)
            {
                problems.Add($"Step '{step.Name}' is missing from the step-to-mechanism map in {MethodologyDocument}.");
            }
            else if (rows.Length > 1)
            {
                problems.Add($"Step '{step.Name}' is listed {rows.Length} times in the step-to-mechanism map in {MethodologyDocument}.");
            }
            else if (!string.Equals(rows[0][PrimaryColumn], step.PrimaryMechanism.ToString(), StringComparison.Ordinal))
            {
                problems.Add($"Step '{step.Name}' has primary mechanism '{rows[0][PrimaryColumn]}' in the step-to-mechanism map in {MethodologyDocument}; expected '{step.PrimaryMechanism}'.");
            }
        }

        PlanAssert.NoProblems(problems);
    }

    [Fact]
    public async Task MethodologyMap_StepTable_ListsOnlyExhaustiveStepsAsync()
    {
        MarkdownTable table = StepTable(await LoadMapTablesAsync());
        HashSet<string> names = TestPlans.Exhaustive.Steps.Select(step => step.Name).ToHashSet(StringComparer.Ordinal);

        List<string> problems = table.Rows
            .Where(row => !names.Contains(row[NameColumn]))
            .Select(row => $"Row '{string.Join(" | ", row)}' of the step-to-mechanism map in {MethodologyDocument} names '{row[NameColumn]}', which is not a step of TestPlans.Exhaustive.")
            .ToList();

        PlanAssert.NoProblems(problems);
        PlanAssert.Equal(TestPlans.Exhaustive.Steps.Count, table.Rows.Count, $"Data rows of the step table under '{MapHeading}' in {MethodologyDocument}");
    }

    [Fact]
    public async Task MethodologyMap_StepRows_MatchTheStepsTheyNameAsync()
    {
        MarkdownTable table = StepTable(await LoadMapTablesAsync());
        var problems = new List<string>();

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            // A row naming no plan step is reported by MethodologyMap_StepTable_ListsOnlyExhaustiveStepsAsync.
            StepDefinition? step = TestPlans.Exhaustive.Steps
                .FirstOrDefault(candidate => string.Equals(candidate.Name, row[NameColumn], StringComparison.Ordinal));
            if (step is null)
            {
                continue;
            }

            string label = $"Step-to-mechanism map row for step '{step.Name}'";
            ExpectedStep? expected = _expectedSteps.FirstOrDefault(candidate => string.Equals(candidate.Name, step.Name, StringComparison.Ordinal));
            if (expected is null)
            {
                problems.Add($"{label}: the step is not in the per-type step plan of this test.");
            }
            else
            {
                CompareCell(problems, label, "ID", expected.Id, row[IdColumn]);
            }

            string[] listedTypes = row[TypesColumn].Split(',', StringSplitOptions.TrimEntries);
            string[] memberOf = TypeLetters(step.Name);
            if (listedTypes.Length != memberOf.Length || !listedTypes.ToHashSet(StringComparer.Ordinal).SetEquals(memberOf))
            {
                problems.Add($"{label}: column 'Types' reads '{row[TypesColumn]}'; expected {string.Join(", ", memberOf)}.");
            }

            CompareCell(problems, label, "Pattern", step.Pattern.ToString(), row[PatternColumn]);
            CompareCell(problems, label, "Order", step.Order.ToString(), row[OrderColumn]);
            CompareCell(problems, label, "Threads", step.Threads.ToString(), row[ThreadsColumn]);
            CompareCell(problems, label, "Hold", step.HasHold ? $"{(int)step.Hold.TotalSeconds} s" : EmDash, row[HoldColumn]);
            CheckSecondaryMechanisms(problems, label, row[SecondaryColumn], step.PrimaryMechanism);
        }

        PlanAssert.NoProblems(problems);
    }

    [Fact]
    public async Task MethodologyMap_MechanismTable_MapsEveryMechanismAsync()
    {
        IReadOnlyList<MarkdownTable> tables = await LoadMapTablesAsync();
        if (tables.Count < 2)
        {
            Assert.Fail($"'{MapHeading}' in {MethodologyDocument} has no second table mapping the mechanisms.");
        }

        // A mechanism is mapped when a row of the second table holds its enum name as a whole cell.
        // The methodology document's table puts that name in its "Enum value" column, after a prose
        // mechanism name, so the lookup is not tied to one column position.
        HashSet<string> cells = tables[1].Rows.SelectMany(row => row).ToHashSet(StringComparer.Ordinal);

        List<string> problems = SingleMechanisms()
            .Select(mechanism => mechanism.ToString())
            .Where(name => !cells.Contains(name))
            .Select(name => $"Mechanism '{name}' is not mapped in the second table of '{MapHeading}' in {MethodologyDocument}.")
            .ToList();

        PlanAssert.NoProblems(problems);
    }

    // ---- Helpers ----

    private static TimeSpan SumOfTimeBudgets(IEnumerable<StepDefinition> steps) =>
        steps.Aggregate(TimeSpan.Zero, (sum, step) => sum + step.TimeBudget);

    private static InstabilityMechanism[] SingleMechanisms() =>
        Enum.GetValues<InstabilityMechanism>().Where(mechanism => mechanism != InstabilityMechanism.None).ToArray();

    private static void AssertOpensWith(TestPlan plan, TestPlan prefix)
    {
        Assert.True(
            plan.Steps.Count > prefix.Steps.Count,
            $"{plan.Type} has {plan.Steps.Count} steps, not more than the {prefix.Steps.Count} steps of {prefix.Type} it must open with.");

        for (int index = 0; index < prefix.Steps.Count; index++)
        {
            PlanAssert.Equal(prefix.Steps[index], plan.Steps[index], $"{plan.Type} step at index {index}, which must equal {prefix.Type} step at index {index}");
        }
    }

    private static StepAttributes ExpectedAttributes(StepDefinition step, int index) => new()
    {
        Index = index,
        Name = step.Name,
        Pattern = step.Pattern,
        Order = step.Order,
        Threads = step.Threads,
        Hold = step.Hold,
        TimeBudget = step.TimeBudget,
        CoverageBudgetPercent = step.CoverageBudgetPercent,
        PrimaryMechanism = step.PrimaryMechanism,
        Mechanisms = step.Mechanisms,
    };

    // The type letters of every built-in plan that contains the step, from plan membership.
    private static string[] TypeLetters(string stepName) => TestPlans.All
        .Where(plan => plan.Steps.Any(step => string.Equals(step.Name, stepName, StringComparison.Ordinal)))
        .Select(plan => plan.Type switch
        {
            TestType.Quick => "Q",
            TestType.Standard => "S",
            TestType.Exhaustive => "E",
            _ => throw new InvalidOperationException($"TestPlans.All holds a plan of undefined test type {(int)plan.Type}."),
        })
        .ToArray();

    private static void CompareCell(List<string> problems, string label, string column, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            problems.Add($"{label}: column '{column}' reads '{actual}'; expected '{expected}'.");
        }
    }

    // Secondary mechanisms are a format check only: an em dash, or InstabilityMechanism member
    // names other than the primary.
    private static void CheckSecondaryMechanisms(List<string> problems, string label, string cell, InstabilityMechanism primary)
    {
        if (string.Equals(cell, EmDash, StringComparison.Ordinal))
        {
            return;
        }

        foreach (string entry in cell.Split(',', StringSplitOptions.TrimEntries))
        {
            bool isMemberName = Enum.TryParse(entry, ignoreCase: false, out InstabilityMechanism mechanism)
                && string.Equals(mechanism.ToString(), entry, StringComparison.Ordinal)
                && mechanism != InstabilityMechanism.None
                && BitOperations.IsPow2((uint)mechanism)
                && Enum.IsDefined(mechanism);

            if (!isMemberName)
            {
                problems.Add($"{label}: column 'Secondary mechanisms' lists '{entry}', which is not an InstabilityMechanism member name; write '{EmDash}' when there is none.");
            }
            else if (mechanism == primary)
            {
                problems.Add($"{label}: column 'Secondary mechanisms' repeats the primary mechanism '{entry}'.");
            }
        }
    }

    private static MarkdownTable StepTable(IReadOnlyList<MarkdownTable> tables)
    {
        if (tables.Count == 0)
        {
            Assert.Fail($"'{MapHeading}' in {MethodologyDocument} contains no table.");
        }

        MarkdownTable table = tables[0];
        PlanAssert.Equal(
            string.Join(" | ", _stepTableHeader),
            string.Join(" | ", table.Header),
            $"Header of the first table under '{MapHeading}' in {MethodologyDocument}");

        List<string> problems = table.Rows
            .Where(row => row.Count != _stepTableHeader.Length)
            .Select(row => $"Row '{string.Join(" | ", row)}' of the step table in {MethodologyDocument} has {row.Count} cells; expected {_stepTableHeader.Length}.")
            .ToList();
        PlanAssert.NoProblems(problems);

        return table;
    }

    private static async Task<IReadOnlyList<MarkdownTable>> LoadMapTablesAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "METHODOLOGY.md");
        if (!File.Exists(path))
        {
            Assert.Fail($"{MethodologyDocument} was not copied to the test output");
        }

        string[] lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken);
        return ParseMapTables(lines);
    }

    // The section runs from its heading to the next level-2 heading; "###" subheadings stay inside
    // it. A table is a run of consecutive lines starting with '|', and prose or blank lines may sit
    // between the heading and the tables.
    private static List<MarkdownTable> ParseMapTables(string[] lines)
    {
        int headingIndex = Array.FindIndex(lines, line => string.Equals(line.Trim(), MapHeading, StringComparison.Ordinal));
        if (headingIndex < 0)
        {
            Assert.Fail($"{MethodologyDocument} has no '{MapHeading}' heading.");
        }

        var tables = new List<MarkdownTable>();
        var rows = new List<string[]>();
        for (int index = headingIndex + 1; index < lines.Length; index++)
        {
            string line = lines[index].TrimStart();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith('|'))
            {
                rows.Add(SplitRow(line));
            }
            else
            {
                FinishTable(tables, rows);
            }
        }

        FinishTable(tables, rows);
        return tables;
    }

    private static void FinishTable(List<MarkdownTable> tables, List<string[]> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        IReadOnlyList<string>[] dataRows = rows.Skip(1).Where(row => !row.All(IsSeparatorCell)).ToArray<IReadOnlyList<string>>();
        tables.Add(new MarkdownTable(rows[0], dataRows));
        rows.Clear();
    }

    // Cells are trimmed and stripped of backticks, so `Random` reads as Random.
    private static string[] SplitRow(string line)
    {
        string content = line.Trim();
        if (content.StartsWith('|'))
        {
            content = content[1..];
        }

        if (content.EndsWith('|'))
        {
            content = content[..^1];
        }

        return content
            .Split('|')
            .Select(cell => cell.Replace("`", string.Empty, StringComparison.Ordinal).Trim())
            .ToArray();
    }

    // A header separator cell: three or more dashes with optional alignment colons, ^:?-{3,}:?$.
    private static bool IsSeparatorCell(string cell)
    {
        ReadOnlySpan<char> dashes = cell.AsSpan();
        if (dashes.Length > 0 && dashes[0] == ':')
        {
            dashes = dashes[1..];
        }

        if (dashes.Length > 0 && dashes[^1] == ':')
        {
            dashes = dashes[..^1];
        }

        return dashes.Length >= 3 && dashes.IndexOfAnyExcept('-') < 0;
    }

    private sealed record ExpectedStep(
        string Id,
        string Name,
        PatternKind Pattern,
        AccessOrder Order,
        ThreadMode Threads,
        int HoldSeconds,
        int BudgetSeconds,
        int CoveragePercent,
        InstabilityMechanism Primary);

    private sealed record MarkdownTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);
}


/// <summary>
/// Tests for <see cref="TestPlanValidator"/>: the built-in plans validate clean, a value at the edge
/// of each rule passes, and breaking any one rule reports a violation at the offending step, or at
/// plan level, while the same plan without the break stays clean.
/// </summary>
public sealed class TestPlanValidatorTests
{
    // Native rq_step_def.time_budget_ms and hold_ms are uint32 milliseconds.
    private const long NativeMaximumMilliseconds = uint.MaxValue;

    // The wrapper marshals the coverage percentage as basis points into the uint32 coverage_budget_bp.
    private const int NativeMaximumCoveragePercent = (int)(uint.MaxValue / 100);

    private const InstabilityMechanism UndefinedMechanismBit = (InstabilityMechanism)(1 << 20);

    // Index 0 of every single-change plan: a valid windowed step whose name no case reuses.
    private static readonly StepDefinition _leadingStep = TestPlans.Quick.Steps[1];

    // The baselines changed at index 1: Q1, a windowed step, and Q5, a step with a 5 s hold.
    private static readonly StepDefinition _windowedBaseline = TestPlans.Quick.Steps[0];
    private static readonly StepDefinition _holdBaseline = TestPlans.Quick.Steps[4];

    // "\u20AC" (euro sign) is 3 UTF-8 bytes and "\u00E9" (e acute) 2, so these names tell a byte
    // limit apart from a character limit.
    private static readonly string _name63Utf8Bytes = new('\u20AC', 21);
    private static readonly string _name64AsciiBytes = new('a', 64);
    private static readonly string _name64Utf8BytesIn32Chars = new('\u00E9', 32);

    public static TheoryData<TestType> TestTypes { get; } = new(Enum.GetValues<TestType>());

    public static TheoryData<string> BoundaryCases { get; } = new()
    {
        "Name63Utf8Bytes",
        "NamesDifferOnlyByCase",
        "Coverage1",
        "CoverageNativeMaximum",
        "BudgetNativeMaximum",
        "HoldBudgetExactlyMinimumRatio",
        "MechanismsEveryDefinedBit",
    };

    public static TheoryData<string> InvalidCases { get; } = new()
    {
        "EmptyPlan",
        "UndefinedTestType",
        "EmptyName",
        "WhiteSpaceName",
        "NullName",
        "DuplicateName",
        "Name64AsciiBytes",
        "Name64Utf8BytesIn32Chars",
        "NulInName",
        "PatternZero",
        "PatternUndefined",
        "OrderZero",
        "OrderUndefined",
        "ThreadsZero",
        "ThreadsUndefined",
        "BudgetZero",
        "BudgetNegative",
        "BudgetAboveNativeMaximum",
        "HoldNegative",
        "HoldAboveNativeMaximum",
        "HoldBudgetBelowMinimumRatio",
        "CoverageZero",
        "CoverageNegative",
        "CoverageAboveNativeMaximum",
        "PrimaryNotInMechanisms",
        "PrimaryCombined",
        "PrimaryNone",
        "PrimaryUndefinedBit",
        "MechanismsUndefinedBit",
    };

    [Theory]
    [MemberData(nameof(TestTypes))]
    public void Validate_BuiltInPlan_ReportsNoViolations(TestType type)
    {
        PlanAssert.NoViolations(TestPlanValidator.Validate(TestPlans.Get(type)), $"Built-in {type} plan");
    }

    [Fact]
    public void Baselines_QuickSteps_AreTheStepsTheCasesAssume()
    {
        Assert.Equal("Random bank thrash", _leadingStep.Name);
        Assert.Equal("High-toggle sweep", _windowedBaseline.Name);
        Assert.False(_windowedBaseline.HasHold);
        Assert.Equal("Refresh hold", _holdBaseline.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), _holdBaseline.Hold);
    }

    [Fact]
    public void BoundaryNames_Utf8ByteAndCharacterCounts_AreAsTheCasesIntend()
    {
        Assert.Equal(21, _name63Utf8Bytes.Length);
        Assert.Equal(63, Encoding.UTF8.GetByteCount(_name63Utf8Bytes));
        Assert.Equal(64, Encoding.UTF8.GetByteCount(_name64AsciiBytes));
        Assert.Equal(32, _name64Utf8BytesIn32Chars.Length);
        Assert.Equal(64, Encoding.UTF8.GetByteCount(_name64Utf8BytesIn32Chars));
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Validate_ValueAtTheEdgeOfItsRule_ReportsNoViolations(string caseName)
    {
        PlanAssert.NoViolations(TestPlanValidator.Validate(BuildBoundaryPlan(caseName)), $"Boundary case {caseName}");
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Validate_OneRuleBroken_ReportsAViolationAtTheOffendingStepOrPlan(string caseName)
    {
        InvalidCase invalid = BuildInvalidCase(caseName);
        PlanAssert.NoViolations(TestPlanValidator.Validate(invalid.Control), $"Control plan of case {caseName}");

        IReadOnlyList<PlanViolation> violations = TestPlanValidator.Validate(invalid.Plan);

        Assert.NotEmpty(violations);
        bool located = violations.Any(violation =>
            violation.StepIndex == invalid.StepIndex
            && string.Equals(violation.StepName, invalid.StepName, StringComparison.Ordinal));
        string expectedAt = invalid.StepIndex is null ? "plan level" : $"step index {invalid.StepIndex}";
        Assert.True(located, $"Case {caseName}: no violation at {expectedAt}. Reported: {PlanAssert.Describe(violations)}");

        foreach (PlanViolation violation in violations)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(violation.Message),
                $"Case {caseName}: a violation has a blank message. Reported: {PlanAssert.Describe(violations)}");
        }
    }

    [Fact]
    public void Validate_SeveralRulesBroken_ReportsEveryViolationPlanLevelFirstThenInStepOrder()
    {
        var plan = new TestPlan(
            (TestType)7,
            [
                _leadingStep,
                _windowedBaseline with { Pattern = (PatternKind)0 },
                _holdBaseline,
                _windowedBaseline with { Name = "Coverage broken", CoverageBudgetPercent = 0 },
                _holdBaseline with { Name = "Hold rule broken", TimeBudget = TimeSpan.FromSeconds(1) },
            ]);

        IReadOnlyList<PlanViolation> violations = TestPlanValidator.Validate(plan);
        string reported = PlanAssert.Describe(violations);

        int leadingPlanLevel = violations.TakeWhile(violation => violation.StepIndex is null).Count();
        PlanAssert.Equal(1, leadingPlanLevel, $"Plan-level violations ahead of the step violations. Reported: {reported}");
        PlanAssert.Equal(1, violations.Count(violation => violation.StepIndex is null), $"Plan-level violations. Reported: {reported}");

        int[] stepIndices = violations.Skip(leadingPlanLevel).Select(violation => violation.StepIndex ?? -1).ToArray();
        PlanAssert.Equal("1, 3, 4", string.Join(", ", stepIndices.Distinct()), $"Steps reported, in report order. Reported: {reported}");
        Assert.True(stepIndices.SequenceEqual(stepIndices.Order()), $"Step violations must follow step order. Reported: {reported}");
    }

    [Fact]
    public void Validate_NameWithControlCharacter_EscapesItInTheMessageAndKeepsTheRawName()
    {
        const string RawName = "Bad\0name";

        IReadOnlyList<PlanViolation> violations = TestPlanValidator.Validate(SingleChangePlan(_windowedBaseline with { Name = RawName }));

        PlanViolation violation = Assert.Single(violations);
        Assert.Equal(1, violation.StepIndex);
        Assert.Equal(RawName, violation.StepName);
        Assert.Contains("Bad\\u0000name", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NullPlan_ThrowsArgumentNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => TestPlanValidator.Validate(null!));

        Assert.Equal("plan", exception.ParamName);
    }

    private static TestPlan BuildBoundaryPlan(string caseName) => caseName switch
    {
        "Name63Utf8Bytes" => SingleChangePlan(_windowedBaseline with { Name = _name63Utf8Bytes }),

        // Names are unique by ordinal comparison, so a difference in case alone keeps them apart.
        "NamesDifferOnlyByCase" => new TestPlan(TestType.Quick, [_holdBaseline, _holdBaseline with { Name = "refresh hold" }]),
        "Coverage1" => SingleChangePlan(_windowedBaseline with { CoverageBudgetPercent = 1 }),
        "CoverageNativeMaximum" => SingleChangePlan(_windowedBaseline with { CoverageBudgetPercent = NativeMaximumCoveragePercent }),
        "BudgetNativeMaximum" => SingleChangePlan(_windowedBaseline with { TimeBudget = Milliseconds(NativeMaximumMilliseconds) }),

        // 12.5 s is exactly 2.5 times the 5 s hold; the rule compares in ticks, so equality passes.
        "HoldBudgetExactlyMinimumRatio" => SingleChangePlan(_holdBaseline with { TimeBudget = Milliseconds(12_500) }),
        "MechanismsEveryDefinedBit" => SingleChangePlan(_windowedBaseline with { Mechanisms = AllDefinedMechanisms() }),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown boundary case."),
    };

    private static InvalidCase BuildInvalidCase(string caseName) => caseName switch
    {
        "EmptyPlan" => new InvalidCase(new TestPlan(TestType.Quick, []), SingleChangePlan(_windowedBaseline), null, null),
        "UndefinedTestType" => new InvalidCase(
            new TestPlan((TestType)42, [_leadingStep, _windowedBaseline]),
            SingleChangePlan(_windowedBaseline),
            null,
            null),
        "EmptyName" => StepCase(_windowedBaseline, _windowedBaseline with { Name = string.Empty }),
        "WhiteSpaceName" => StepCase(_windowedBaseline, _windowedBaseline with { Name = "   " }),

        // Name is declared non-nullable, but a caller can still pass null.
        "NullName" => StepCase(_windowedBaseline, _windowedBaseline with { Name = null! }),

        // The second use of a name is the one reported.
        "DuplicateName" => new InvalidCase(
            new TestPlan(TestType.Quick, [_windowedBaseline, _leadingStep with { Name = _windowedBaseline.Name }]),
            new TestPlan(TestType.Quick, [_windowedBaseline, _leadingStep]),
            1,
            _windowedBaseline.Name),
        "Name64AsciiBytes" => StepCase(_windowedBaseline, _windowedBaseline with { Name = _name64AsciiBytes }),
        "Name64Utf8BytesIn32Chars" => StepCase(_windowedBaseline, _windowedBaseline with { Name = _name64Utf8BytesIn32Chars }),
        "NulInName" => StepCase(_windowedBaseline, _windowedBaseline with { Name = "Bad\0name" }),
        "PatternZero" => StepCase(_windowedBaseline, _windowedBaseline with { Pattern = (PatternKind)0 }),
        "PatternUndefined" => StepCase(_windowedBaseline, _windowedBaseline with { Pattern = (PatternKind)99 }),
        "OrderZero" => StepCase(_windowedBaseline, _windowedBaseline with { Order = (AccessOrder)0 }),
        "OrderUndefined" => StepCase(_windowedBaseline, _windowedBaseline with { Order = (AccessOrder)6 }),
        "ThreadsZero" => StepCase(_windowedBaseline, _windowedBaseline with { Threads = (ThreadMode)0 }),
        "ThreadsUndefined" => StepCase(_windowedBaseline, _windowedBaseline with { Threads = (ThreadMode)3 }),
        "BudgetZero" => StepCase(_windowedBaseline, _windowedBaseline with { TimeBudget = TimeSpan.Zero }),
        "BudgetNegative" => StepCase(_windowedBaseline, _windowedBaseline with { TimeBudget = TimeSpan.FromSeconds(-1) }),
        "BudgetAboveNativeMaximum" => StepCase(_windowedBaseline, _windowedBaseline with { TimeBudget = Milliseconds(NativeMaximumMilliseconds + 1) }),
        "HoldNegative" => StepCase(_windowedBaseline, _windowedBaseline with { Hold = TimeSpan.FromSeconds(-1) }),

        // No budget within the native range is 2.5 times this hold, so the hold rule fires as well.
        "HoldAboveNativeMaximum" => StepCase(
            _holdBaseline,
            _holdBaseline with
            {
                Hold = Milliseconds(NativeMaximumMilliseconds + 1),
                TimeBudget = Milliseconds(NativeMaximumMilliseconds),
            }),
        "HoldBudgetBelowMinimumRatio" => StepCase(_holdBaseline, _holdBaseline with { TimeBudget = Milliseconds(12_400) }),
        "CoverageZero" => StepCase(_windowedBaseline, _windowedBaseline with { CoverageBudgetPercent = 0 }),
        "CoverageNegative" => StepCase(_windowedBaseline, _windowedBaseline with { CoverageBudgetPercent = -5 }),
        "CoverageAboveNativeMaximum" => StepCase(_windowedBaseline, _windowedBaseline with { CoverageBudgetPercent = NativeMaximumCoveragePercent + 1 }),
        "PrimaryNotInMechanisms" => StepCase(
            _windowedBaseline,
            _windowedBaseline with
            {
                PrimaryMechanism = InstabilityMechanism.RowTimings,
                Mechanisms = InstabilityMechanism.SignalIntegrity,
            }),
        "PrimaryCombined" => StepCase(
            _windowedBaseline,
            _windowedBaseline with
            {
                PrimaryMechanism = InstabilityMechanism.SignalIntegrity | InstabilityMechanism.VddVddqVoltage,
                Mechanisms = InstabilityMechanism.SignalIntegrity | InstabilityMechanism.VddVddqVoltage,
            }),
        "PrimaryNone" => StepCase(
            _windowedBaseline,
            _windowedBaseline with
            {
                PrimaryMechanism = InstabilityMechanism.None,
                Mechanisms = InstabilityMechanism.SignalIntegrity,
            }),
        "PrimaryUndefinedBit" => StepCase(
            _windowedBaseline,
            _windowedBaseline with
            {
                PrimaryMechanism = UndefinedMechanismBit,
                Mechanisms = _windowedBaseline.Mechanisms | UndefinedMechanismBit,
            }),
        "MechanismsUndefinedBit" => StepCase(
            _windowedBaseline,
            _windowedBaseline with { Mechanisms = InstabilityMechanism.SignalIntegrity | UndefinedMechanismBit }),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown invalid case."),
    };

    // The changed step sits at index 1, behind the valid leading step; the control plan holds the
    // unchanged baseline there instead.
    private static InvalidCase StepCase(StepDefinition baseline, StepDefinition changed) =>
        new(SingleChangePlan(changed), SingleChangePlan(baseline), 1, changed.Name);

    private static TestPlan SingleChangePlan(StepDefinition step) => new(TestType.Quick, [_leadingStep, step]);

    private static TimeSpan Milliseconds(long milliseconds) => TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);

    private static InstabilityMechanism AllDefinedMechanisms() =>
        Enum.GetValues<InstabilityMechanism>().Aggregate(InstabilityMechanism.None, (all, mechanism) => all | mechanism);

    private sealed record InvalidCase(TestPlan Plan, TestPlan Control, int? StepIndex, string? StepName);
}

// Assertion helpers shared by the test classes in this file. Each failure message names the plan,
// step or mechanism at fault together with the expected and found values.
file static class PlanAssert
{
    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{context}: expected {expected}, found {actual}.");
        }
    }

    public static void NoProblems(IReadOnlyCollection<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }

    public static void NoViolations(IReadOnlyList<PlanViolation> violations, string context)
    {
        if (violations.Count > 0)
        {
            Assert.Fail($"{context} reported {violations.Count} violation(s): {Describe(violations)}");
        }
    }

    public static string Describe(IEnumerable<PlanViolation> violations) =>
        string.Join(" ", violations.Select(DescribeOne));

    private static string DescribeOne(PlanViolation violation)
    {
        string at = violation.StepIndex is null ? "plan" : $"step index {violation.StepIndex}";
        string name = violation.StepName is null ? "null" : $"'{violation.StepName.Replace("\0", "\\0", StringComparison.Ordinal)}'";
        return $"[{at}, name {name}] {violation.Message}";
    }
}

