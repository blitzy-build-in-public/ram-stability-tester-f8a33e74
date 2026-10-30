using System.Globalization;
using System.Numerics;
using System.Text;
using RamQuick.Core;

namespace RamQuick.Plans;

/// <summary>
/// One rule a test plan breaks, as reported by <see cref="TestPlanValidator.Validate"/>.
/// </summary>
/// <param name="StepIndex">
/// The zero-based position of the offending step in <see cref="TestPlan.Steps"/>, or
/// <see langword="null"/> for a plan-level violation such as an empty plan or an undefined test
/// type.
/// </param>
/// <param name="StepName">
/// The offending step's <see cref="StepDefinition.Name"/> exactly as declared, which may itself be
/// the fault (blank, too long or containing a NUL character). <see langword="null"/> for a
/// plan-level violation, or when the step's name is <see langword="null"/>.
/// </param>
/// <param name="Message">
/// A short English sentence naming the step, the rule it breaks and the offending value, for
/// example "Step 'Refresh hold' (index 4) holds for 5 s but has a time budget of 5 s, less than
/// 2.5 × its hold."
/// </param>
public sealed record PlanViolation(int? StepIndex, string? StepName, string Message);

/// <summary>
/// Checks a <see cref="TestPlan"/> against the step-definition rules, the native field ranges, the
/// step shapes the engine runs and the hold rule, and reports every violation at once.
/// </summary>
/// <remarks>
/// <para>
/// Validation is advisory data checking. Neither <see cref="TestPlan"/> nor
/// <see cref="StepDefinition"/> calls it, so tests can build scaled plans that deliberately break
/// a rule, such as budgets divided by 10 with the 5 s holds kept, and the engine still runs them.
/// The managed wrapper rejects values it cannot marshal, and step shapes the engine does not run,
/// before any native call, and the engine validates its own inputs again.
/// </para>
/// <para>
/// The built-in plans in <c>TestPlans</c> validate with no violations.
/// </para>
/// </remarks>
public static class TestPlanValidator
{
    /// <summary>
    /// The smallest ratio of a hold step's <see cref="StepDefinition.TimeBudget"/> to its
    /// <see cref="StepDefinition.Hold"/>: 2.5. Each pass of a hold step lasts at least its hold,
    /// so this floor leaves room in the budget for more than one pass instead of a single pass
    /// that overruns it.
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> evaluates the rule exactly as 2 × budget ≥ 5 × hold in ticks; this
    /// constant is derived from that integer pair, so the two cannot drift apart.
    /// </remarks>
    public const double MinimumHoldBudgetRatio = (double)HoldRatioNumerator / HoldRatioDenominator;

    // The hold rule as an exact integer ratio: budget ≥ (5 / 2) × hold.
    private const int HoldRatioNumerator = 5;
    private const int HoldRatioDenominator = 2;

    // Native rq_step_def.time_budget_ms and hold_ms are uint32 milliseconds.
    private const long MaxNativeMillisecondTicks = uint.MaxValue * TimeSpan.TicksPerMillisecond;

    // The wrapper marshals the percentage as basis points into the uint32 coverage_budget_bp.
    private const int MaxCoverageBudgetPercent = (int)(uint.MaxValue / 100);

    private static readonly InstabilityMechanism _definedMechanisms = CombineDefinedMechanisms();

    /// <summary>
    /// Checks every rule of a test plan and returns all violations found, plan-level violations
    /// first and then step violations in step order.
    /// </summary>
    /// <remarks>
    /// <para>Plan-level rules:</para>
    /// <list type="bullet">
    /// <item><description><see cref="TestPlan.Type"/> is a defined <see cref="TestType"/>.</description></item>
    /// <item><description>The plan has at least one step.</description></item>
    /// </list>
    /// <para>
    /// Step rules, from the step-definition contract of <see cref="StepDefinition"/>, the
    /// <c>rq_step_def</c> field widths of <c>ramquick_engine.h</c>, the step shapes
    /// <c>rq_session_create</c> accepts, and the pass scheduler's hold rule:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="StepDefinition.Name"/> is not null, empty or white space; contains no NUL
    /// character, because the native name is NUL-terminated; is at most
    /// <see cref="StepDefinition.MaxNameUtf8Bytes"/> UTF-8 bytes, the native 64-byte
    /// <c>name</c> field; and is unique within the plan by ordinal comparison. The second and
    /// later uses of a name are reported.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.Pattern"/>, <see cref="StepDefinition.Order"/> and
    /// <see cref="StepDefinition.Threads"/> are defined members. Zero is invalid natively, and
    /// none of these enums defines it.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.TimeBudget"/> is greater than zero and at most
    /// <see cref="uint.MaxValue"/> milliseconds, the native <c>time_budget_ms</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.Hold"/> is not negative and at most <see cref="uint.MaxValue"/>
    /// milliseconds, the native <c>hold_ms</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.CoverageBudgetPercent"/> is at least 1 and at most
    /// <see cref="uint.MaxValue"/> / 100, because the wrapper marshals it as basis points into
    /// the native <c>coverage_budget_bp</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.PrimaryMechanism"/> is exactly one defined
    /// <see cref="InstabilityMechanism"/> bit: not <see cref="InstabilityMechanism.None"/>, not a
    /// combination, and not an undefined bit.
    /// </description></item>
    /// <item><description>
    /// <see cref="StepDefinition.Mechanisms"/> includes the primary mechanism and holds no bit
    /// outside the defined <see cref="InstabilityMechanism"/> members.
    /// </description></item>
    /// <item><description>
    /// The step shape rule, the shapes the engine runs: a step with a hold has a pattern other
    /// than <see cref="PatternKind.MovingInversionWalking"/> and
    /// <see cref="PatternKind.MovingInversionRandom"/>, whose own whole-slice sweeps take no hold,
    /// and the order <see cref="AccessOrder.RandomBlocks"/>, in which it writes its whole slice. A
    /// moving-inversion step with a hold is reported once, for its pattern, whatever its order.
    /// </description></item>
    /// <item><description>
    /// The hold rule: a step with a hold has a time budget of at least
    /// <see cref="MinimumHoldBudgetRatio"/> times its hold, compared exactly in ticks.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="plan">The plan to check.</param>
    /// <returns>
    /// A read-only list of every violation, empty when the plan satisfies every rule. A step that
    /// breaks several rules contributes one violation per rule.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<PlanViolation> Validate(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var violations = new List<PlanViolation>();

        if (!Enum.IsDefined(plan.Type))
        {
            violations.Add(new PlanViolation(
                null,
                null,
                string.Create(CultureInfo.InvariantCulture, $"The plan's test type {(int)plan.Type} is not a defined TestType value.")));
        }

        if (plan.Steps.Count == 0)
        {
            violations.Add(new PlanViolation(null, null, "The plan has no steps."));
        }

        var firstIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Steps.Count; index++)
        {
            ValidateStep(plan.Steps[index], index, firstIndexByName, violations);
        }

        return violations.AsReadOnly();
    }

    private static void ValidateStep(
        StepDefinition step,
        int index,
        Dictionary<string, int> firstIndexByName,
        List<PlanViolation> violations)
    {
        // Name is declared non-nullable, but a caller can still supply null through null!.
        string? name = step.Name;
        string label = DescribeStep(name, index);

        void Report(string message) => violations.Add(new PlanViolation(index, name, message));

        ValidateName(name, index, label, firstIndexByName, Report);

        if (!Enum.IsDefined(step.Pattern))
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has pattern {(int)step.Pattern}, which is not a defined PatternKind value."));
        }

        if (!Enum.IsDefined(step.Order))
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has access order {(int)step.Order}, which is not a defined AccessOrder value."));
        }

        if (!Enum.IsDefined(step.Threads))
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has thread mode {(int)step.Threads}, which is not a defined ThreadMode value."));
        }

        if (step.TimeBudget <= TimeSpan.Zero)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a time budget of {Seconds(step.TimeBudget)}; it must be greater than zero."));
        }
        else if (step.TimeBudget.Ticks > MaxNativeMillisecondTicks)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a time budget of {Seconds(step.TimeBudget)}, more than the native maximum of {uint.MaxValue} ms."));
        }

        if (step.Hold < TimeSpan.Zero)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a negative hold of {Seconds(step.Hold)}; it must be zero or positive."));
        }
        else if (step.Hold.Ticks > MaxNativeMillisecondTicks)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a hold of {Seconds(step.Hold)}, more than the native maximum of {uint.MaxValue} ms."));
        }

        if (step.CoverageBudgetPercent < 1)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a coverage budget of {step.CoverageBudgetPercent} %; it must be at least 1 %."));
        }
        else if (step.CoverageBudgetPercent > MaxCoverageBudgetPercent)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} has a coverage budget of {step.CoverageBudgetPercent} %, more than the native maximum of {MaxCoverageBudgetPercent} %."));
        }

        ValidateMechanisms(step.PrimaryMechanism, step.Mechanisms, label, Report);

        // Only the step shapes the engine runs, so a plan that validates clean also starts instead
        // of failing session creation with a bare invalid-argument code. A hold step writes its
        // whole slice in RandomBlocks order, so requiring it to declare that order keeps the order
        // the summary and the analyzer read equal to the order that runs. Undefined values are
        // already reported above, and one faulty shape gives one violation.
        if (step.HasHold && Enum.IsDefined(step.Pattern) && Enum.IsDefined(step.Order))
        {
            if (step.Pattern is PatternKind.MovingInversionWalking or PatternKind.MovingInversionRandom)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"{label} holds for {Seconds(step.Hold)} with the moving-inversion pattern {step.Pattern}; moving inversion runs its own whole-slice sweeps and takes no hold."));
            }
            else if (step.Order != AccessOrder.RandomBlocks)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"{label} holds for {Seconds(step.Hold)} with access order {step.Order}; a hold step writes its whole slice in RandomBlocks order, so its order must be RandomBlocks."));
            }
        }

        // Int128 keeps 2 × budget and 5 × hold exact for every TimeSpan, so no floating-point
        // rounding decides a budget that sits exactly at 2.5 holds.
        if (step.HasHold
            && (Int128)step.TimeBudget.Ticks * HoldRatioDenominator < (Int128)step.Hold.Ticks * HoldRatioNumerator)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"{label} holds for {Seconds(step.Hold)} but has a time budget of {Seconds(step.TimeBudget)}, less than {MinimumHoldBudgetRatio} × its hold."));
        }
    }

    private static void ValidateName(
        string? name,
        int index,
        string label,
        Dictionary<string, int> firstIndexByName,
        Action<string> report)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has a blank name; every step needs a name to show in the UI and the logs."));
        }

        if (name is null)
        {
            return;
        }

        if (name.Contains('\0', StringComparison.Ordinal))
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has a name containing a NUL character, which would cut the NUL-terminated native name short."));
        }

        int utf8Bytes = Encoding.UTF8.GetByteCount(name);
        if (utf8Bytes > StepDefinition.MaxNameUtf8Bytes)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has a name of {utf8Bytes} UTF-8 bytes, more than the {StepDefinition.MaxNameUtf8Bytes} bytes the native 64-byte name field holds."));
        }

        // A blank name is already reported; counting blanks as duplicates would only repeat it.
        if (!string.IsNullOrWhiteSpace(name) && !firstIndexByName.TryAdd(name, index))
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} repeats the name of the step at index {firstIndexByName[name]}; names must be unique within a plan."));
        }
    }

    private static void ValidateMechanisms(
        InstabilityMechanism primary,
        InstabilityMechanism mechanisms,
        string label,
        Action<string> report)
    {
        if (primary == InstabilityMechanism.None)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has no primary mechanism; it must name exactly one."));
        }
        else if (!BitOperations.IsPow2((uint)primary))
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has a primary mechanism that combines several mechanisms ({primary}); it must name exactly one."));
        }
        else if (!Enum.IsDefined(primary))
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} has primary mechanism 0x{(uint)primary:X8}, which is not a defined InstabilityMechanism member."));
        }

        if ((mechanisms & primary) != primary)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} lists mechanisms '{mechanisms}', which do not include its primary mechanism '{primary}'."));
        }

        InstabilityMechanism undefinedBits = mechanisms & ~_definedMechanisms;
        if (undefinedBits != InstabilityMechanism.None)
        {
            report(string.Create(CultureInfo.InvariantCulture, $"{label} lists mechanism bits 0x{(uint)undefinedBits:X8}, which are not defined InstabilityMechanism members."));
        }
    }

    private static string DescribeStep(string? name, int index)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Create(CultureInfo.InvariantCulture, $"The step at index {index}")
            : string.Create(CultureInfo.InvariantCulture, $"Step '{Printable(name)}' (index {index})");
    }

    // Control characters, the NUL that breaks the name rule among them, are shown escaped so a
    // message stays readable and safe to write into a log line.
    private static string Printable(string name)
    {
        if (!name.Any(char.IsControl))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 16);
        foreach (char character in name)
        {
            if (char.IsControl(character))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:X4}");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string Seconds(TimeSpan value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds} s");
    }

    private static InstabilityMechanism CombineDefinedMechanisms()
    {
        InstabilityMechanism all = InstabilityMechanism.None;
        foreach (InstabilityMechanism mechanism in Enum.GetValues<InstabilityMechanism>())
        {
            all |= mechanism;
        }

        return all;
    }
}
