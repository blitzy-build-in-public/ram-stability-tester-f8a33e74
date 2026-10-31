namespace RamQuick.Core;

/// <summary>
/// The state of a memory-test run: <c>Idle → Preparing → Running → (Passed | Failed | Aborted)</c>.
/// </summary>
/// <remarks>
/// <para>
/// One <c>RunStateMachine</c> instance holds the current value; every component reads it from there
/// rather than keeping a copy. The allowed transitions are defined only by <c>RunStateMachine</c>.
/// </para>
/// <para>
/// Member names are written by name into the progress journal and the results summaries, so renaming a
/// member breaks the reading of files written by earlier builds.
/// </para>
/// </remarks>
public enum RunState
{
    /// <summary>
    /// No run has started: the state at launch. It is also the state after a launch whose journal
    /// recovery found the previous run's results already published, or rebuilt and published them
    /// (a completed but unpublished run).
    /// </summary>
    Idle = 0,

    /// <summary>
    /// Start was accepted. The engine is allocating and locking the memory under test and pinning one
    /// worker to each logical processor.
    /// </summary>
    Preparing = 1,

    /// <summary>
    /// The engine reported Prepared and the plan's steps are executing.
    /// </summary>
    Running = 2,

    /// <summary>
    /// The run completed every step with a true error total of 0.
    /// </summary>
    Passed = 3,

    /// <summary>
    /// The run completed with errors, stopped at the first error with Stop on error on, or a previous
    /// run was found unfinished at launch.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// The run stopped before a verdict could be reached: a user stop, a thermal stop, a memory-lock
    /// shortfall, a thread-affinity failure, or a host or engine fault. Errors counted before the stop
    /// are still reported.
    /// </summary>
    Aborted = 5,
}

/// <summary>
/// Pure, stateless classifications of <see cref="RunState"/> values, so every component reads run state
/// the same way.
/// </summary>
/// <remarks>
/// Every method returns a defined result for any value, including values outside the enumeration: such
/// values are neither terminal nor in progress and have no verdict text.
/// </remarks>
public static class RunStateExtensions
{
    /// <summary>
    /// Returns whether <paramref name="state"/> ends a run with a verdict.
    /// </summary>
    /// <param name="state">The state to classify.</param>
    /// <returns>
    /// <see langword="true"/> for <see cref="RunState.Passed"/>, <see cref="RunState.Failed"/> and
    /// <see cref="RunState.Aborted"/>; otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsTerminal(this RunState state) => state switch
    {
        RunState.Passed or RunState.Failed or RunState.Aborted => true,
        _ => false,
    };

    /// <summary>
    /// Returns whether a run is under way in <paramref name="state"/>, which is when Stop is offered
    /// instead of Start and the test type and Stop on error settings are locked.
    /// </summary>
    /// <param name="state">The state to classify.</param>
    /// <returns>
    /// <see langword="true"/> for <see cref="RunState.Preparing"/> and <see cref="RunState.Running"/>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsInProgress(this RunState state) => state switch
    {
        RunState.Preparing or RunState.Running => true,
        _ => false,
    };

    /// <summary>
    /// Returns the verdict wording for <paramref name="state"/>, shared by the UI verdict badge and the
    /// summary's <c>run.verdict</c> field.
    /// </summary>
    /// <param name="state">The state to describe.</param>
    /// <returns>
    /// <c>"PASS"</c> for <see cref="RunState.Passed"/>, <c>"FAIL"</c> for <see cref="RunState.Failed"/>,
    /// <c>"ABORTED"</c> for <see cref="RunState.Aborted"/>; otherwise <see langword="null"/>.
    /// </returns>
    public static string? ToVerdictText(this RunState state) => state switch
    {
        RunState.Passed => "PASS",
        RunState.Failed => "FAIL",
        RunState.Aborted => "ABORTED",
        _ => null,
    };
}
