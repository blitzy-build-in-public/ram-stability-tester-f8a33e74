namespace RamQuick.Core;

/// <summary>
/// An immutable snapshot of a run's progress, built from one engine progress tick
/// (<c>rq_progress</c>, at most 4 Hz) by <c>RamQuick.Interop</c>.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper publishes the latest snapshot on the engine's coordinator thread by swapping a
/// reference, and the journal writer thread reads it for its heartbeat records. Because every
/// property is init-only, a reference swap is a complete, atomic publication: a reader can never
/// observe a snapshot that is half old and half new. Use a <c>with</c> expression to derive a
/// changed copy; the original is left untouched.
/// </para>
/// <para>
/// Every step and pass index in <c>RamQuick.Core</c> is zero-based. Display adds 1, as in
/// "Read-after-write turnaround (3 of 6)". <c>RamQuick.Interop</c> converts wherever a native field
/// uses a different base.
/// </para>
/// </remarks>
public sealed record RunProgress
{
    /// <summary>
    /// Gets the zero-based index of the current step in the plan's step array.
    /// Source: <c>rq_progress.step_index</c>.
    /// </summary>
    public required int StepIndex { get; init; }

    /// <summary>
    /// Gets the number of steps in the plan being run.
    /// Source: <c>rq_progress.step_count</c>.
    /// </summary>
    public required int StepCount { get; init; }

    /// <summary>
    /// Gets the current step's name, exactly as the plan defines it and the UI shows it.
    /// <c>rq_progress</c> carries no name; the wrapper takes it from the step definition at
    /// <see cref="StepIndex"/>, which is the same text the engine reports in <c>rq_step_start.name</c>.
    /// </summary>
    public required string StepName { get; init; }

    /// <summary>
    /// Gets the zero-based index of the pass currently running within the step.
    /// Source: <c>rq_progress.pass_index</c>.
    /// </summary>
    public required int PassIndex { get; init; }

    /// <summary>
    /// Gets the number of passes currently planned for the step,
    /// <c>max(1, round(time budget / mean pass duration))</c>. The engine recomputes it after every
    /// pass, so it may change between snapshots.
    /// Source: <c>rq_progress.passes_planned</c>.
    /// </summary>
    public required int PassesPlanned { get; init; }

    /// <summary>
    /// Gets the time elapsed since the run entered Running.
    /// Source: <c>rq_progress.elapsed_ms</c>, in milliseconds.
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets the engine's estimate of the time left in the run, or <see langword="null"/> while the
    /// estimate is still being calculated, which the UI shows as "calculating…". The value is null
    /// until step 1 completes its first pass, and otherwise the current step's remaining term plus
    /// the time budgets of every later step. It is never negative.
    /// Source: <c>rq_progress.remaining_ms</c>, in milliseconds, where −1 maps to null.
    /// </summary>
    public required TimeSpan? Remaining { get; init; }

    /// <summary>
    /// Gets the session's total of verified bytes: bytes whose 8-byte word was read and compared
    /// with its expected value. Moving inversion counts twice per pass; re-reads, and data written
    /// but not yet verified, do not count.
    /// Source: <c>rq_progress.verified_bytes</c>, in bytes.
    /// </summary>
    public required ulong VerifiedBytes { get; init; }

    /// <summary>
    /// Gets the size of the region under test, <c>slice × workers</c>, as locked by the engine.
    /// Source: <c>rq_progress.region_bytes</c>, in bytes.
    /// </summary>
    public required ulong RegionBytes { get; init; }

    /// <summary>
    /// Gets the coverage so far, as a percentage: 100 × <see cref="VerifiedBytes"/> ÷
    /// <see cref="RegionBytes"/>, unrounded. 100 means every tested byte was verified once.
    /// Not a native field: it is computed with <see cref="CoveragePercentOf"/>.
    /// </summary>
    public required double CoveragePercent { get; init; }

    /// <summary>
    /// Gets the true number of errors detected so far, taken from the engine's counters. It keeps
    /// counting after the stored-detail cap is reached and is never derived from stored records.
    /// Source: <c>rq_progress.total_errors</c>.
    /// </summary>
    public required ulong TotalErrors { get; init; }

    /// <summary>
    /// Computes coverage as a percentage: 100 × <paramref name="verifiedBytes"/> ÷
    /// <paramref name="regionBytes"/>. Every layer that reports coverage (progress, per-step and
    /// total summary figures, journal recovery) uses this one formula, so their values agree.
    /// </summary>
    /// <param name="verifiedBytes">Bytes verified, in bytes.</param>
    /// <param name="regionBytes">Bytes under test, in bytes.</param>
    /// <returns>
    /// The unrounded coverage percentage, or 0 when <paramref name="regionBytes"/> is 0.
    /// </returns>
    public static double CoveragePercentOf(ulong verifiedBytes, ulong regionBytes)
    {
        // Computed in double so the product cannot overflow; display code does the rounding.
        return regionBytes == 0 ? 0 : 100.0 * verifiedBytes / regionBytes;
    }
}
