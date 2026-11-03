using RamQuick.Core;

namespace RamQuick.Plans;

// TestPlan, TestPlans and TestPlanValidator are named with <c> rather than <see cref>, so this
// record keeps no compile-time reference to the types built on it.

/// <summary>
/// One named step of a test plan: the data pattern, access order, thread mode, hold and budgets
/// the engine's step runner interprets. Plans are data, so a step is described entirely by these
/// values and no step has code of its own.
/// </summary>
/// <remarks>
/// <para>
/// Construction accepts any values. <c>TestPlanValidator.Validate</c> checks the rules stated on
/// each property and reports every violation at once, while tests and the test host derive scaled
/// or shortened steps with <see langword="with"/> expressions that deliberately depart from the
/// shipped budgets.
/// </para>
/// <para>
/// Compiler-generated value equality compares every property. It is what lets the plan catalog
/// tests assert that the Standard and Exhaustive plans open with exactly Quick's six steps, so the
/// record holds only value-typed data and its immutable name.
/// </para>
/// <para>
/// Layers that must not reference <c>RamQuick.Plans</c> (Interop, Logging and Analysis) receive
/// the same data as <see cref="StepAttributes"/> through <see cref="ToAttributes(int)"/>.
/// </para>
/// </remarks>
public sealed record StepDefinition
{
    /// <summary>
    /// The largest number of UTF-8 bytes a <see cref="Name"/> may hold: the native
    /// <c>rq_step_def.name</c> field is 64 bytes and NUL-terminated. <c>TestPlanValidator</c>
    /// checks names against this limit.
    /// </summary>
    public const int MaxNameUtf8Bytes = 63;

    /// <summary>
    /// Gets the step's name, unique within its plan and at most <see cref="MaxNameUtf8Bytes"/>
    /// UTF-8 bytes. It is shown verbatim in the UI, as in "Read-after-write turnaround (3 of 6)",
    /// in the run's <c>.log</c>, in the JSON summary's <c>steps[].name</c> and in the
    /// step-to-mechanism map of <c>docs/METHODOLOGY.md</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the data pattern the step writes and verifies; each step has exactly one.
    /// </summary>
    public required PatternKind Pattern { get; init; }

    /// <summary>
    /// Gets the order in which the step visits each worker's slice. The moving-inversion patterns
    /// imply ascending-then-descending whole-slice sweeps whatever this value is, and the plans
    /// set <see cref="AccessOrder.SequentialAscending"/> for them.
    /// </summary>
    public required AccessOrder Order { get; init; }

    /// <summary>
    /// Gets how many workers the step keeps active. Defaults to <see cref="ThreadMode.All"/>;
    /// <see cref="ThreadMode.Half"/> activates even-indexed workers only, each covering its own
    /// slice and its partner's.
    /// </summary>
    public ThreadMode Threads { get; init; } = ThreadMode.All;

    /// <summary>
    /// Gets the idle hold between writing and verifying. <see cref="TimeSpan.Zero"/>, the default,
    /// makes the step windowed: each worker writes and then verifies one window at a time. A
    /// positive hold makes every pass write the whole slice, wait the hold, then verify the whole
    /// slice.
    /// </summary>
    /// <remarks>
    /// A hold step writes in <see cref="AccessOrder.RandomBlocks"/> order and waits on the
    /// session's stop event rather than sleeping, so a stop is never held behind the hold. Its
    /// verifications are <c>VerifyAfterHold</c>, which is how errors that appear only after data
    /// sat idle point at refresh and retention. A step with a hold must therefore declare
    /// <see cref="AccessOrder.RandomBlocks"/> and a pattern other than moving inversion, which
    /// <c>TestPlanValidator</c> checks.
    /// </remarks>
    public TimeSpan Hold { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the step's time budget, which must be greater than zero. Pass scheduling ends the step
    /// near this budget, and a plan's target duration is the sum of its steps' budgets.
    /// </summary>
    public required TimeSpan TimeBudget { get; init; }

    /// <summary>
    /// Gets the step's coverage budget as a percentage of the tested bytes, at least 1. A test
    /// type's coverage target is the sum of its steps' budgets.
    /// </summary>
    /// <remarks>
    /// The budget is a floor that time-boxed pass scheduling is expected to exceed. It never
    /// extends a step, because fail-fast ordering and the plan durations take precedence over
    /// coverage.
    /// </remarks>
    public required int CoverageBudgetPercent { get; init; }

    /// <summary>
    /// Gets the single mechanism the step chiefly targets: exactly one single-bit
    /// <see cref="InstabilityMechanism"/> value. It feeds the cause analyzer's step prior and the
    /// step-to-mechanism map of <c>docs/METHODOLOGY.md</c>.
    /// </summary>
    public required InstabilityMechanism PrimaryMechanism { get; init; }

    /// <summary>
    /// Gets every mechanism the step targets, as flags: the primary plus any secondaries. It must
    /// include <see cref="PrimaryMechanism"/>.
    /// </summary>
    /// <remarks>
    /// Plans write this value out in full rather than having it derived from the primary, so the
    /// validator's check that the primary is contained here is meaningful.
    /// </remarks>
    public required InstabilityMechanism Mechanisms { get; init; }

    /// <summary>
    /// Gets a value indicating whether the step holds written data idle before verifying it, that
    /// is, whether <see cref="Hold"/> is positive.
    /// </summary>
    public bool HasHold => Hold > TimeSpan.Zero;

    /// <summary>
    /// Copies this step into the plan-independent <see cref="StepAttributes"/> that the wrapper,
    /// the logs and the analyzer consume.
    /// </summary>
    /// <param name="index">
    /// The step's zero-based position in its plan's step list, which is also the engine's
    /// <c>step_index</c>. Display adds 1, as in "(3 of 6)".
    /// </param>
    /// <returns>The attributes, carrying every property of this step and <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public StepAttributes ToAttributes(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new StepAttributes
        {
            Index = index,
            Name = Name,
            Pattern = Pattern,
            Order = Order,
            Threads = Threads,
            Hold = Hold,
            TimeBudget = TimeBudget,
            CoverageBudgetPercent = CoverageBudgetPercent,
            PrimaryMechanism = PrimaryMechanism,
            Mechanisms = Mechanisms,
        };
    }
}
