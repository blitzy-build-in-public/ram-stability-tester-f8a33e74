namespace RamQuick.Core;

/// <summary>
/// The single run state machine: <c>Idle → Preparing → Running → (Passed | Failed | Aborted)</c>, plus
/// <c>Preparing → Aborted</c>, the launch-only <c>Idle → Failed</c>, and a new Start from any terminal
/// state. It holds the current <see cref="RunState"/> and, in a terminal state, why the run ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>One instance, read by every component.</b> The composition root creates exactly one instance and
/// hands it to every component that needs run state. No component caches a copy: each reads
/// <see cref="State"/>, <see cref="EndReason"/> and <see cref="EndDetail"/> here, or takes the values
/// carried by <see cref="StateChanged"/>. The native engine keeps only a stop flag, never a state.
/// </para>
/// <para>
/// <b>Threading.</b> The instance has UI-thread affinity: it is mutated only on the UI thread, by
/// <c>RunController</c>, and read on the UI thread. It takes no locks. The affinity is a contract, not a
/// run-time check, because the application tests drive the machine from test threads through an
/// immediate synchronization context, which a thread check would reject.
/// </para>
/// <para>
/// <b>Rejections.</b> A transition the diagram does not contain, or a terminal transition whose reason
/// does not fit the target state, is refused with a <see langword="false"/> result and no change. That is
/// the expected outcome of event races, for example an engine Completed arriving after the state already
/// left Running. Arguments that no caller may pass, such as an undefined enumeration value, are
/// programming errors and throw.
/// </para>
/// </remarks>
public sealed class RunStateMachine
{
    /// <summary>
    /// Initializes a state machine in <see cref="RunState.Idle"/>, with no end reason and no end detail.
    /// </summary>
    public RunStateMachine()
    {
        State = RunState.Idle;
    }

    /// <summary>
    /// Raised synchronously, on the thread that made the change, after every successful transition and
    /// never after a refused one.
    /// </summary>
    /// <remarks>
    /// <see cref="State"/>, <see cref="EndReason"/> and <see cref="EndDetail"/> already hold the new values
    /// when the event is raised. A handler may itself call <see cref="TryTransition"/>, under the same
    /// rules; the properties then move on before later handlers run. Handlers should therefore rely on the
    /// <see cref="RunStateChangedEventArgs"/> they receive, which always describe the transition that
    /// raised the event.
    /// </remarks>
    public event EventHandler<RunStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Gets the current state of the run. A new instance starts in <see cref="RunState.Idle"/>.
    /// </summary>
    public RunState State { get; private set; }

    /// <summary>
    /// Gets why the run ended: set on entering a terminal state, <see langword="null"/> before the first
    /// terminal state and again from the moment a new Start enters <see cref="RunState.Preparing"/>.
    /// </summary>
    public RunEndReason? EndReason { get; private set; }

    /// <summary>
    /// Gets the human-readable detail of how the run ended, such as
    /// "Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12",
    /// "CPU temperature reached 95 °C" or an engine message. It is set only on entering a terminal state,
    /// may be <see langword="null"/> there, and is cleared with <see cref="EndReason"/> on entering
    /// <see cref="RunState.Preparing"/>.
    /// </summary>
    public string? EndDetail { get; private set; }

    /// <summary>
    /// Gets whether the current state ends a run with a verdict: <see cref="RunState.Passed"/>,
    /// <see cref="RunState.Failed"/> or <see cref="RunState.Aborted"/>.
    /// </summary>
    public bool IsTerminal => State.IsTerminal();

    /// <summary>
    /// Gets whether a run is under way: <see cref="RunState.Preparing"/> or <see cref="RunState.Running"/>.
    /// Stop is offered instead of Start, and the test settings are locked, exactly while this is true.
    /// </summary>
    public bool IsRunInProgress => State.IsInProgress();

    /// <summary>
    /// Returns whether the run state diagram contains the edge from <paramref name="from"/> to
    /// <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// The diagram's ten edges are <c>Idle → Preparing</c> and <c>Passed | Failed | Aborted → Preparing</c>
    /// (Start accepted), <c>Idle → Failed</c> (launch finds an unfinished journal),
    /// <c>Preparing → Running</c> (engine Prepared), <c>Preparing → Aborted</c> (user Stop, lock shortfall,
    /// affinity failure, host or engine fault) and <c>Running → Passed | Failed | Aborted</c>. Every other
    /// pair is refused, including self-transitions, any return to Idle and undefined values.
    /// </remarks>
    /// <param name="from">The state the transition leaves.</param>
    /// <param name="to">The state the transition enters.</param>
    /// <returns><see langword="true"/> only for the ten edges of the diagram.</returns>
    public static bool IsEdgeAllowed(RunState from, RunState to) => (from, to) switch
    {
        (RunState.Idle, RunState.Preparing) => true,
        (RunState.Idle, RunState.Failed) => true,
        (RunState.Preparing, RunState.Running) => true,
        (RunState.Preparing, RunState.Aborted) => true,
        (RunState.Running, RunState.Passed or RunState.Failed or RunState.Aborted) => true,
        (RunState.Passed or RunState.Failed or RunState.Aborted, RunState.Preparing) => true,
        _ => false,
    };

    /// <summary>
    /// Resolves the terminal state a run reaches for <paramref name="reason"/>. The engine's single
    /// first-writer-wins end reason alone decides it.
    /// </summary>
    /// <remarks>
    /// <see cref="RunEndReason.Completed"/> gives <see cref="RunState.Passed"/> when
    /// <paramref name="totalErrors"/> is 0 and <see cref="RunState.Failed"/> otherwise.
    /// <see cref="RunEndReason.FirstError"/> and <see cref="RunEndReason.PreviousRunUnfinished"/> give
    /// <see cref="RunState.Failed"/>. Every stop or fault reason gives <see cref="RunState.Aborted"/>, even
    /// when errors were already counted: the run never reached a verdict, and the errors are reported
    /// alongside the ABORTED result.
    /// </remarks>
    /// <param name="reason">The reason the run ended.</param>
    /// <param name="totalErrors">
    /// The true error total from the engine's counters, never the count of stored error records, which
    /// stops at the detail cap.
    /// </param>
    /// <returns>
    /// <see cref="RunState.Passed"/>, <see cref="RunState.Failed"/> or <see cref="RunState.Aborted"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a defined <see cref="RunEndReason"/>.
    /// </exception>
    public static RunState ResolveTerminalState(RunEndReason reason, ulong totalErrors) => reason switch
    {
        RunEndReason.Completed => totalErrors == 0 ? RunState.Passed : RunState.Failed,
        RunEndReason.FirstError or RunEndReason.PreviousRunUnfinished => RunState.Failed,
        RunEndReason.UserStop
            or RunEndReason.Thermal
            or RunEndReason.InsufficientMemory
            or RunEndReason.AffinityFailed
            or RunEndReason.HostFault
            or RunEndReason.EngineFault => RunState.Aborted,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "The run end reason is not a defined value."),
    };

    /// <summary>
    /// Moves the run to <paramref name="target"/> if the diagram allows the edge from the current state
    /// and, for a terminal target, <paramref name="endReason"/> fits it. Only <c>RunController</c> calls
    /// this method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal target needs a reason that fits it: <see cref="RunState.Passed"/> takes
    /// <see cref="RunEndReason.Completed"/>; <see cref="RunState.Failed"/> takes
    /// <see cref="RunEndReason.Completed"/> or <see cref="RunEndReason.FirstError"/>;
    /// <see cref="RunState.Aborted"/> takes a stop or fault reason. <c>Idle → Failed</c> applies only at
    /// launch, so it takes only <see cref="RunEndReason.PreviousRunUnfinished"/>, and that reason is
    /// accepted on no other edge.
    /// </para>
    /// <para>
    /// Entering <see cref="RunState.Preparing"/> clears <see cref="EndReason"/> and <see cref="EndDetail"/>,
    /// because a new Start clears the previous result; they stay <see langword="null"/> through
    /// <see cref="RunState.Running"/>. Entering a terminal state sets them to the supplied values. The
    /// properties are updated before <see cref="StateChanged"/> is raised.
    /// </para>
    /// <para>
    /// Prefer <see cref="TryFinish"/> for terminal transitions: it applies the precedence rule of
    /// <see cref="ResolveTerminalState"/>, so the target always follows from the reason.
    /// </para>
    /// </remarks>
    /// <param name="target">The state to enter.</param>
    /// <param name="endReason">
    /// Why the run ended. Required for a terminal target and forbidden for a non-terminal one.
    /// </param>
    /// <param name="endDetail">
    /// The human-readable end detail. Allowed for a terminal target only, and optional there.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the transition was made and <see cref="StateChanged"/> raised;
    /// <see langword="false"/>, with no change and no event, when the edge is not in the diagram or the
    /// reason does not fit the target.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="target"/> is not a defined <see cref="RunState"/>, or <paramref name="endReason"/>
    /// is not <see langword="null"/> and not a defined <see cref="RunEndReason"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> is <see cref="RunState.Idle"/>, <see cref="RunState.Preparing"/> or
    /// <see cref="RunState.Running"/>, and <paramref name="endReason"/> or <paramref name="endDetail"/> is
    /// not <see langword="null"/>.
    /// </exception>
    public bool TryTransition(RunState target, RunEndReason? endReason = null, string? endDetail = null)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target run state is not a defined value.");
        }

        if (endReason is { } definedReason && !Enum.IsDefined(definedReason))
        {
            throw new ArgumentOutOfRangeException(nameof(endReason), endReason, "The run end reason is not a defined value.");
        }

        bool targetIsTerminal = target.IsTerminal();
        if (!targetIsTerminal && (endReason is not null || endDetail is not null))
        {
            throw new ArgumentException(
                $"An end reason or end detail can only accompany a terminal state, not {target}.",
                endReason is not null ? nameof(endReason) : nameof(endDetail));
        }

        if (!IsEdgeAllowed(State, target))
        {
            return false;
        }

        if (targetIsTerminal && !IsReasonAllowed(State, target, endReason))
        {
            return false;
        }

        RunState previous = State;
        State = target;
        EndReason = targetIsTerminal ? endReason : null;
        EndDetail = targetIsTerminal ? endDetail : null;

        StateChanged?.Invoke(this, new RunStateChangedEventArgs(previous, target, EndReason, EndDetail));
        return true;
    }

    /// <summary>
    /// Ends the run for <paramref name="reason"/>: resolves the terminal state with
    /// <see cref="ResolveTerminalState"/> and enters it through <see cref="TryTransition"/>. Only
    /// <c>RunController</c> calls this method, on engine Completed and, with
    /// <see cref="RunEndReason.PreviousRunUnfinished"/>, for launch recovery of an unfinished run.
    /// </summary>
    /// <param name="reason">The reason the run ended, as the engine or journal recovery reported it.</param>
    /// <param name="totalErrors">
    /// The true error total from the engine's counters, never the count of stored error records.
    /// </param>
    /// <param name="endDetail">The human-readable end detail, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the terminal state was entered; <see langword="false"/>, with no change
    /// and no event, when the current state cannot reach it with this reason, for example a Completed from
    /// Preparing or a second end after the run already ended.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a defined <see cref="RunEndReason"/>.
    /// </exception>
    public bool TryFinish(RunEndReason reason, ulong totalErrors, string? endDetail = null)
    {
        return TryTransition(ResolveTerminalState(reason, totalErrors), reason, endDetail);
    }

    // Called only for a terminal edge IsEdgeAllowed accepted, which leaves Idle, Preparing or Running.
    // The one edge out of Idle is launch-time recovery, and it alone carries PreviousRunUnfinished.
    private static bool IsReasonAllowed(RunState from, RunState target, RunEndReason? endReason) =>
        (from, target, endReason) switch
        {
            (_, _, null) => false,
            (RunState.Idle, RunState.Failed, RunEndReason.PreviousRunUnfinished) => true,
            (RunState.Idle, _, _) => false,
            (_, _, RunEndReason.PreviousRunUnfinished) => false,
            (_, RunState.Passed, RunEndReason.Completed) => true,
            (_, RunState.Failed, RunEndReason.Completed or RunEndReason.FirstError) => true,
            (_, RunState.Aborted, RunEndReason.UserStop
                or RunEndReason.Thermal
                or RunEndReason.InsufficientMemory
                or RunEndReason.AffinityFailed
                or RunEndReason.HostFault
                or RunEndReason.EngineFault) => true,
            _ => false,
        };
}

/// <summary>
/// Describes one successful transition of the <see cref="RunStateMachine"/>.
/// </summary>
public sealed class RunStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes the description of a transition.
    /// </summary>
    /// <param name="previous">The state the run left.</param>
    /// <param name="current">The state the run entered.</param>
    /// <param name="endReason">Why the run ended, when <paramref name="current"/> is terminal.</param>
    /// <param name="endDetail">The human-readable end detail, when <paramref name="current"/> is terminal.</param>
    public RunStateChangedEventArgs(RunState previous, RunState current, RunEndReason? endReason, string? endDetail)
    {
        Previous = previous;
        Current = current;
        EndReason = endReason;
        EndDetail = endDetail;
    }

    /// <summary>
    /// Gets the state the run left.
    /// </summary>
    public RunState Previous { get; }

    /// <summary>
    /// Gets the state the run entered.
    /// </summary>
    public RunState Current { get; }

    /// <summary>
    /// Gets why the run ended when <see cref="Current"/> is terminal; otherwise <see langword="null"/>.
    /// </summary>
    public RunEndReason? EndReason { get; }

    /// <summary>
    /// Gets the human-readable end detail when <see cref="Current"/> is terminal and one was supplied;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? EndDetail { get; }
}
