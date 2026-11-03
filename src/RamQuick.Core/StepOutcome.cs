namespace RamQuick.Core;

/// <summary>
/// The plan-independent description of one test step: its data pattern, access order, thread
/// mode, hold, time and coverage budgets, and the instability mechanisms it targets.
/// </summary>
/// <remarks>
/// <para>
/// This is the step contract shared by every layer that must not reference <c>RamQuick.Plans</c>:
/// <c>RamQuick.Interop</c> marshals it into <c>rq_step_def</c>, <c>RamQuick.Logging</c> writes it
/// into the journal's <c>begin</c> record and the summary's <c>steps[]</c>, and
/// <c>RamQuick.Analysis</c> reads it for its signature rules and step priors.
/// <c>RamQuick.Plans.StepDefinition</c> maps onto this type.
/// </para>
/// <para>
/// The record is plain data and validates nothing, so a recovered journal can always be read back
/// into it. The invariants stated on each property are enforced where steps enter a run:
/// <c>RamQuick.Plans.TestPlanValidator</c> checks every plan on the managed side, and the engine's
/// <c>rq_session_create</c> rejects an invalid step array with <c>RQ_E_INVALID_ARGUMENT</c>.
/// </para>
/// <para>
/// Value equality compares every property, which lets tests assert that the Standard and
/// Exhaustive plans open with exactly Quick's steps. Property names are a serialization contract,
/// camelCased into the summary by <c>RamQuick.Logging</c>; never rename one.
/// </para>
/// </remarks>
public sealed record StepAttributes
{
    /// <summary>
    /// Gets the zero-based position of the step in its plan's step array. Display adds 1, as in
    /// "Read-after-write turnaround (3 of 6)".
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the step's name, shown verbatim in the UI, the log and the summary. It is unique within
    /// a plan and at most 63 UTF-8 bytes, so it fits the NUL-terminated 64-byte
    /// <c>rq_step_def.name</c> field.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the data pattern the step writes and verifies. Native <c>rq_step_def.pattern</c>.
    /// </summary>
    public required PatternKind Pattern { get; init; }

    /// <summary>
    /// Gets the order in which the step visits each worker's slice. The moving-inversion patterns
    /// imply ascending-then-descending sweeps whatever this value is.
    /// Native <c>rq_step_def.access_order</c>.
    /// </summary>
    public required AccessOrder Order { get; init; }

    /// <summary>
    /// Gets how many workers the step keeps active. Defaults to <see cref="ThreadMode.All"/>.
    /// Native <c>rq_step_def.thread_mode</c>.
    /// </summary>
    public ThreadMode Threads { get; init; } = ThreadMode.All;

    /// <summary>
    /// Gets the idle hold between writing and verifying. <see cref="TimeSpan.Zero"/>, the default,
    /// makes the step windowed: each worker writes and then verifies one 64 MiB window at a time.
    /// A positive hold makes every pass write the whole slice, wait the hold, then verify the whole
    /// slice. Native <c>rq_step_def.hold_ms</c>, in milliseconds.
    /// </summary>
    public TimeSpan Hold { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the step's time budget, which is greater than zero. Pass scheduling ends the step near
    /// this budget, and a plan's target duration is the sum of its steps' budgets.
    /// Native <c>rq_step_def.time_budget_ms</c>, in milliseconds.
    /// </summary>
    public required TimeSpan TimeBudget { get; init; }

    /// <summary>
    /// Gets the step's coverage budget as a percentage of the tested bytes, at least 1. A plan's
    /// coverage target is the sum of its steps' budgets. It is a floor that the time-boxed pass
    /// scheduling is expected to exceed, and it never extends a step.
    /// Native <c>rq_step_def.coverage_budget_bp</c>, in basis points (percent × 100).
    /// </summary>
    public required int CoverageBudgetPercent { get; init; }

    /// <summary>
    /// Gets the single mechanism the step chiefly targets: exactly one single-bit
    /// <see cref="InstabilityMechanism"/> value, never <see cref="InstabilityMechanism.None"/>.
    /// It feeds the cause analyzer's step prior and the step-to-mechanism map in
    /// <c>docs/METHODOLOGY.md</c>.
    /// </summary>
    public required InstabilityMechanism PrimaryMechanism { get; init; }

    /// <summary>
    /// Gets every mechanism the step targets, as flags. It always includes
    /// <see cref="PrimaryMechanism"/>.
    /// </summary>
    public required InstabilityMechanism Mechanisms { get; init; }

    /// <summary>
    /// Gets a value indicating whether the step holds written data idle before verifying it, that
    /// is, whether <see cref="Hold"/> is positive. This is the one definition of a hold step: the
    /// analyzer's "steps without a hold", the native <c>hold_ms</c> and the summary's
    /// <c>holdSeconds</c> all rest on it.
    /// </summary>
    public bool HasHold => Hold > TimeSpan.Zero;
}

/// <summary>
/// The outcome of one started step: its attributes together with what the engine measured.
/// </summary>
/// <remarks>
/// <para>
/// A run produces exactly one outcome per step that started, built from the engine's
/// <c>rq_step_result</c> or, for a recovered journal, from its <c>stepEnd</c> records plus the
/// unfinished step. Steps that never started have no outcome.
/// </para>
/// <para>
/// The cause analyzer's rule preconditions, such as "a step that completed a pass with zero
/// errors", read <see cref="PassesCompleted"/> and <see cref="Errors"/>. Property names are a
/// serialization contract, camelCased into the summary's <c>steps[]</c> by
/// <c>RamQuick.Logging</c>; never rename one.
/// </para>
/// </remarks>
public sealed record StepOutcome
{
    /// <summary>
    /// Gets the attributes of the step, as planned.
    /// </summary>
    public required StepAttributes Attributes { get; init; }

    /// <summary>
    /// Gets the time the step actually ran.
    /// Source: <c>rq_step_result.actual_ms</c>, in milliseconds.
    /// </summary>
    public required TimeSpan ActualDuration { get; init; }

    /// <summary>
    /// Gets the number of passes the step finished. A pass a stop interrupted is not counted.
    /// Source: <c>rq_step_result.passes</c>.
    /// </summary>
    public required int PassesCompleted { get; init; }

    /// <summary>
    /// Gets the bytes this step verified, the basis of its per-step coverage. Moving inversion
    /// counts twice per pass; re-reads, and data written but not yet verified, do not count.
    /// Source: <c>rq_step_result.verified_bytes</c>, in bytes.
    /// </summary>
    public required ulong VerifiedBytes { get; init; }

    /// <summary>
    /// Gets the true number of errors the step detected, taken from the engine's counters. It
    /// keeps counting past the stored-detail cap and is never derived from stored error records.
    /// Source: <c>rq_step_result.errors</c>.
    /// </summary>
    public required ulong Errors { get; init; }

    /// <summary>
    /// Gets a value indicating whether the step ran to the end of its schedule. It is
    /// <see langword="false"/> for the step a stop interrupted and for the unfinished step of a
    /// recovered journal.
    /// Source: <c>rq_step_result.completed</c>.
    /// </summary>
    public required bool Completed { get; init; }

    /// <summary>
    /// Gets how far the step ran past its time budget because a single pass was longer than the
    /// budget, or <see cref="TimeSpan.Zero"/> when it stayed within the budget.
    /// Source: <c>rq_step_result.overrun_ms</c>, in milliseconds.
    /// </summary>
    public required TimeSpan Overrun { get; init; }
}
