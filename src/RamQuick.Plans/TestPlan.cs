using RamQuick.Core;

namespace RamQuick.Plans;

// TestPlans and TestPlanValidator are named with <c> rather than <see cref>, so this record keeps
// no compile-time reference to the types built on it.

/// <summary>
/// One test type's ordered list of steps, in the fail-fast order the engine runs them, together
/// with the duration and coverage targets that follow from the steps' budgets.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TargetDuration"/> and <see cref="TargetCoveragePercent"/> are derived sums of the
/// steps' <see cref="StepDefinition.TimeBudget"/> and
/// <see cref="StepDefinition.CoverageBudgetPercent"/>, never separately declared numbers. A plan is
/// therefore retuned only by editing step budgets in <c>TestPlans</c>, and its targets follow.
/// </para>
/// <para>
/// The plan is plain data and immutable once built: the constructor takes a snapshot of the steps,
/// and <see cref="Steps"/> is a read-only view of that snapshot. Construction validates nothing
/// beyond null arguments, so an empty or rule-breaking plan can be built and then reported by
/// <c>TestPlanValidator.Validate</c>, and tests can build scaled or shortened plans from the
/// built-in steps with <see langword="with"/> expressions.
/// </para>
/// <para>
/// Plans are equal when they implement the same type with equal steps in the same order. Every
/// property is get-only, so a <see langword="with"/> expression on a plan can only copy it, never
/// pair steps with targets that are not their sums.
/// </para>
/// </remarks>
public sealed record TestPlan
{
    /// <summary>
    /// Initializes a new plan from a snapshot of <paramref name="steps"/> and computes its targets.
    /// </summary>
    /// <param name="type">
    /// The test type the plan implements. It is stored as given; <c>TestPlanValidator</c> reports
    /// an undefined value.
    /// </param>
    /// <param name="steps">
    /// The steps in run order. The sequence is enumerated once and copied, so later changes to the
    /// caller's collection do not affect the plan. Empty sequences and steps that break the plan
    /// rules are accepted.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="steps"/> contains a <see langword="null"/> step.</exception>
    /// <exception cref="OverflowException">
    /// The sum of the steps' time budgets exceeds <see cref="TimeSpan.MaxValue"/>, or the sum of
    /// their coverage budgets exceeds <see cref="int.MaxValue"/>.
    /// </exception>
    public TestPlan(TestType type, IEnumerable<StepDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        StepDefinition[] snapshot = steps.ToArray();
        TimeSpan duration = TimeSpan.Zero;
        int coveragePercent = 0;

        for (int index = 0; index < snapshot.Length; index++)
        {
            StepDefinition step = snapshot[index];
            if (step is null)
            {
                throw new ArgumentException($"Step at index {index} is null.", nameof(steps));
            }

            // TimeSpan addition throws OverflowException on its own; the coverage sum is checked
            // explicitly so an overflow surfaces as the programming error it is.
            duration += step.TimeBudget;
            coveragePercent = checked(coveragePercent + step.CoverageBudgetPercent);
        }

        Type = type;
        Steps = Array.AsReadOnly(snapshot);
        TargetDuration = duration;
        TargetCoveragePercent = coveragePercent;
    }

    /// <summary>
    /// Gets the test type this plan implements.
    /// </summary>
    public TestType Type { get; }

    /// <summary>
    /// Gets the plan's steps in run order, the fail-fast order in which the engine executes them.
    /// The list is a read-only view of the snapshot taken at construction and cannot be cast back
    /// to a mutable collection.
    /// </summary>
    public IReadOnlyList<StepDefinition> Steps { get; }

    /// <summary>
    /// Gets the plan's target duration: the sum of the steps' time budgets, or
    /// <see cref="TimeSpan.Zero"/> for an empty plan. The engine schedules passes against each
    /// step's budget, so a clean run is expected to last about this long.
    /// </summary>
    public TimeSpan TargetDuration { get; }

    /// <summary>
    /// Gets the plan's coverage target as a percentage of the tested bytes: the sum of the steps'
    /// coverage budgets, or 0 for an empty plan. A run reaches its target when its reported
    /// coverage is at least this value.
    /// </summary>
    public int TargetCoveragePercent { get; }

    /// <summary>
    /// Maps every step to the plan-independent <see cref="StepAttributes"/> consumed by the engine
    /// wrapper, the progress journal and the results summary.
    /// </summary>
    /// <returns>
    /// A new read-only list with one entry per step, in run order. Entry <c>i</c> carries
    /// <see cref="StepAttributes.Index"/> <c>i</c>, zero-based, which is also the engine's
    /// <c>step_index</c>.
    /// </returns>
    public IReadOnlyList<StepAttributes> ToStepAttributes() =>
        Array.AsReadOnly(Steps.Select((step, index) => step.ToAttributes(index)).ToArray());

    // Generated record equality would compare Steps by list reference, so two plans built from
    // equal steps would read as different. The targets are sums of the steps, but comparing them
    // first rejects most unequal plans before the step-by-step walk.

    /// <summary>
    /// Returns whether <paramref name="other"/> implements the same test type with the same targets
    /// and equal <see cref="Steps"/>, compared element by element in run order.
    /// </summary>
    /// <param name="other">The plan to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(TestPlan? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Type == other.Type
            && TargetDuration == other.TargetDuration
            && TargetCoveragePercent == other.TargetCoveragePercent
            && Steps.SequenceEqual(other.Steps));

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(TestPlan)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Type);
        hash.Add(TargetDuration);
        hash.Add(TargetCoveragePercent);
        foreach (StepDefinition step in Steps)
        {
            hash.Add(step);
        }

        return hash.ToHashCode();
    }
}
