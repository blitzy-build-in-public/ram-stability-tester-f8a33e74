using RamQuick.Core;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="RunStateMachine"/> and <see cref="RunStateExtensions"/>: the ten edges of the run
/// state diagram and the refusal of every other pair, the end reasons each terminal edge accepts, the
/// terminal-state precedence of end reasons, the finality of a terminal state until a new Start, the
/// <see cref="RunStateMachine.StateChanged"/> contract and the state classifications.
/// </summary>
public sealed class RunStateMachineTests
{
    private const string PassedDetail = "All steps completed";
    private const string FailedDetail = "First error confirmed with Stop on error on";
    private const string AbortedDetail = "Stopped by user";
    private const string EndDetailText = "Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12";

    // The expectations below are stated here, independently of RunStateMachine, so that no test derives
    // its expected outcome from the code it checks.

    // The run state diagram.
    private static readonly (RunState From, RunState To)[] _diagramEdges =
    [
        (RunState.Idle, RunState.Preparing),
        (RunState.Idle, RunState.Failed),
        (RunState.Preparing, RunState.Running),
        (RunState.Preparing, RunState.Aborted),
        (RunState.Running, RunState.Passed),
        (RunState.Running, RunState.Failed),
        (RunState.Running, RunState.Aborted),
        (RunState.Passed, RunState.Preparing),
        (RunState.Failed, RunState.Preparing),
        (RunState.Aborted, RunState.Preparing),
    ];

    private static readonly RunEndReason[] _stopOrFaultReasons =
    [
        RunEndReason.UserStop,
        RunEndReason.Thermal,
        RunEndReason.InsufficientMemory,
        RunEndReason.AffinityFailed,
        RunEndReason.HostFault,
        RunEndReason.EngineFault,
    ];

    // Terminal-state precedence: the engine's end reason alone decides the terminal state, and only
    // Completed looks at the true error total. Every stop or fault aborts even after errors were counted.
    private static readonly (RunEndReason Reason, ulong TotalErrors, RunState Terminal)[] _precedence =
    [
        (RunEndReason.Completed, 0, RunState.Passed),
        (RunEndReason.Completed, 3, RunState.Failed),
        (RunEndReason.FirstError, 0, RunState.Failed),
        (RunEndReason.FirstError, 3, RunState.Failed),
        (RunEndReason.UserStop, 0, RunState.Aborted),
        (RunEndReason.UserStop, 3, RunState.Aborted),
        (RunEndReason.Thermal, 0, RunState.Aborted),
        (RunEndReason.Thermal, 3, RunState.Aborted),
        (RunEndReason.InsufficientMemory, 0, RunState.Aborted),
        (RunEndReason.InsufficientMemory, 3, RunState.Aborted),
        (RunEndReason.AffinityFailed, 0, RunState.Aborted),
        (RunEndReason.AffinityFailed, 3, RunState.Aborted),
        (RunEndReason.HostFault, 0, RunState.Aborted),
        (RunEndReason.HostFault, 3, RunState.Aborted),
        (RunEndReason.EngineFault, 0, RunState.Aborted),
        (RunEndReason.EngineFault, 3, RunState.Aborted),
        (RunEndReason.PreviousRunUnfinished, 0, RunState.Failed),
        (RunEndReason.PreviousRunUnfinished, 3, RunState.Failed),
    ];

    public static TheoryData<RunState, RunState> AllowedEdgeCases => EdgeCases(_diagramEdges);

    public static TheoryData<RunState, RunState> RejectedEdgeCases =>
        EdgeCases(
            from source in Enum.GetValues<RunState>()
            from target in Enum.GetValues<RunState>()
            where !_diagramEdges.Contains((source, target))
            select (source, target));

    public static TheoryData<RunState, RunState> NonTerminalEdgeCases =>
        EdgeCases(_diagramEdges.Where(edge => !IsTerminalState(edge.To)));

    public static TheoryData<RunState, RunState, RunEndReason?> FittingReasonCases =>
        TerminalEdgeReasonCases(fitting: true);

    public static TheoryData<RunState, RunState, RunEndReason?> MisfittingReasonCases =>
        TerminalEdgeReasonCases(fitting: false);

    public static TheoryData<RunEndReason, ulong, RunState> PrecedenceCases
    {
        get
        {
            var data = new TheoryData<RunEndReason, ulong, RunState>();
            foreach ((RunEndReason reason, ulong totalErrors, RunState terminal) in _precedence)
            {
                data.Add(reason, totalErrors, terminal);
            }

            return data;
        }
    }

    public static TheoryData<RunState, RunEndReason, ulong, RunState> AcceptedFinishCases
    {
        get
        {
            var data = new TheoryData<RunState, RunEndReason, ulong, RunState>();
            foreach (RunState source in Enum.GetValues<RunState>())
            {
                foreach ((RunEndReason reason, ulong totalErrors, RunState terminal) in _precedence)
                {
                    if (CanFinish(source, reason, terminal))
                    {
                        data.Add(source, reason, totalErrors, terminal);
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<RunState, RunEndReason, ulong> RefusedFinishCases
    {
        get
        {
            var data = new TheoryData<RunState, RunEndReason, ulong>();
            foreach (RunState source in Enum.GetValues<RunState>())
            {
                foreach ((RunEndReason reason, ulong totalErrors, RunState terminal) in _precedence)
                {
                    if (!CanFinish(source, reason, terminal))
                    {
                        data.Add(source, reason, totalErrors);
                    }
                }
            }

            return data;
        }
    }

    // State, terminal, in progress and verdict text.
    public static TheoryData<RunState, bool, bool, string?> StateClassificationCases => new()
    {
        { RunState.Idle, false, false, null },
        { RunState.Preparing, false, true, null },
        { RunState.Running, false, true, null },
        { RunState.Passed, true, false, "PASS" },
        { RunState.Failed, true, false, "FAIL" },
        { RunState.Aborted, true, false, "ABORTED" },
    };

    [Fact]
    public void Constructor_NewMachine_IsIdleWithNoEndReasonOrDetail()
    {
        var machine = new RunStateMachine();

        Assert.Equal(RunState.Idle, machine.State);
        Assert.Null(machine.EndReason);
        Assert.Null(machine.EndDetail);
    }

    [Theory]
    [MemberData(nameof(AllowedEdgeCases))]
    public void IsEdgeAllowed_DiagramEdge_ReturnsTrue(RunState source, RunState target)
    {
        Assert.True(RunStateMachine.IsEdgeAllowed(source, target));
    }

    [Theory]
    [InlineData((RunState)42, RunState.Preparing)]
    [InlineData(RunState.Idle, (RunState)42)]
    [InlineData((RunState)(-1), (RunState)(-1))]
    public void IsEdgeAllowed_UndefinedState_ReturnsFalse(RunState source, RunState target)
    {
        Assert.False(RunStateMachine.IsEdgeAllowed(source, target));
    }

    [Theory]
    [MemberData(nameof(RejectedEdgeCases))]
    public void TryTransition_PairOutsideDiagram_IsRefusedWithoutChange(RunState source, RunState target)
    {
        Assert.False(RunStateMachine.IsEdgeAllowed(source, target));

        RunStateMachine machine = DriveTo(source);

        // A terminal target gets a reason that fits it, so only the missing edge can refuse it.
        RunEndReason? reason = FittingReasons(RunState.Running, target) is [RunEndReason first, ..] ? first : null;
        string? detail = reason is null ? null : EndDetailText;
        AssertRefusedWithoutChange(machine, () => machine.TryTransition(target, reason, detail));
    }

    [Theory]
    [MemberData(nameof(NonTerminalEdgeCases))]
    public void TryTransition_StartOrPreparedEdge_EntersTargetAndClearsEndReasonAndDetail(
        RunState source,
        RunState target)
    {
        RunStateMachine machine = DriveTo(source);
        List<RunStateChangedEventArgs> changes = RecordChanges(machine);

        Assert.True(machine.TryTransition(target));

        AssertEntered(machine, changes, source, target, null, null);
    }

    [Theory]
    [MemberData(nameof(FittingReasonCases))]
    public void TryTransition_TerminalEdgeWithFittingReason_EntersTargetWithReason(
        RunState source,
        RunState target,
        RunEndReason? reason)
    {
        RunStateMachine machine = DriveTo(source);
        List<RunStateChangedEventArgs> changes = RecordChanges(machine);

        // The end detail is optional for a terminal target.
        Assert.True(machine.TryTransition(target, reason));

        AssertEntered(machine, changes, source, target, reason, null);
    }

    [Theory]
    [MemberData(nameof(MisfittingReasonCases))]
    public void TryTransition_TerminalEdgeWithMisfittingReason_IsRefusedWithoutChange(
        RunState source,
        RunState target,
        RunEndReason? reason)
    {
        RunStateMachine machine = DriveTo(source);

        AssertRefusedWithoutChange(machine, () => machine.TryTransition(target, reason, EndDetailText));
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void ResolveTerminalState_ReasonAndErrorTotal_ReturnsPrecedenceState(
        RunEndReason reason,
        ulong totalErrors,
        RunState expected)
    {
        Assert.Equal(expected, RunStateMachine.ResolveTerminalState(reason, totalErrors));
    }

    [Theory]
    [MemberData(nameof(AcceptedFinishCases))]
    public void TryFinish_ReasonTheCurrentStateCanEnd_EntersPrecedenceStateWithReasonAndDetail(
        RunState source,
        RunEndReason reason,
        ulong totalErrors,
        RunState expected)
    {
        RunStateMachine machine = DriveTo(source);
        List<RunStateChangedEventArgs> changes = RecordChanges(machine);

        Assert.True(machine.TryFinish(reason, totalErrors, EndDetailText));

        AssertEntered(machine, changes, source, expected, reason, EndDetailText);
    }

    // From a terminal state this also proves that the first terminal reason sticks: a later end, such as
    // a first error arriving after a user stop, changes nothing until a new Start enters Preparing.
    [Theory]
    [MemberData(nameof(RefusedFinishCases))]
    public void TryFinish_ReasonTheCurrentStateCannotEnd_IsRefusedWithoutChange(
        RunState source,
        RunEndReason reason,
        ulong totalErrors)
    {
        RunStateMachine machine = DriveTo(source);

        AssertRefusedWithoutChange(machine, () => machine.TryFinish(reason, totalErrors, EndDetailText));
    }

    [Fact]
    public void StateChanged_RunStoppedByUser_RaisesEachTransitionInOrderWithTheMachineAsSender()
    {
        var machine = new RunStateMachine();
        var raised = new List<(object? Sender, RunStateChangedEventArgs Change)>();
        machine.StateChanged += (sender, change) => raised.Add((sender, change));

        Assert.True(machine.TryTransition(RunState.Preparing));
        Assert.True(machine.TryTransition(RunState.Running));
        Assert.True(machine.TryTransition(RunState.Aborted, RunEndReason.UserStop, AbortedDetail));

        Assert.All(raised, entry => Assert.Same(machine, entry.Sender));
        Assert.Equal(
            new (RunState, RunState, RunEndReason?, string?)[]
            {
                (RunState.Idle, RunState.Preparing, null, null),
                (RunState.Preparing, RunState.Running, null, null),
                (RunState.Running, RunState.Aborted, RunEndReason.UserStop, AbortedDetail),
            },
            raised.Select(entry => Describe(entry.Change)));
    }

    [Fact]
    public void StateChanged_HandlerMakesAnotherTransition_EachEventDescribesItsOwnTransition()
    {
        RunStateMachine machine = DriveTo(RunState.Preparing);
        var seenByLaterHandler = new List<RunStateChangedEventArgs>();
        machine.StateChanged += (_, change) =>
        {
            if (change.Current == RunState.Running)
            {
                Assert.True(machine.TryFinish(RunEndReason.UserStop, 0, AbortedDetail));
            }
        };
        machine.StateChanged += (_, change) => seenByLaterHandler.Add(change);

        Assert.True(machine.TryTransition(RunState.Running));

        // The nested transition reaches the later handler first; the outer event still describes its own.
        Assert.Equal(RunState.Aborted, machine.State);
        Assert.Equal(
            new (RunState, RunState, RunEndReason?, string?)[]
            {
                (RunState.Running, RunState.Aborted, RunEndReason.UserStop, AbortedDetail),
                (RunState.Preparing, RunState.Running, null, null),
            },
            seenByLaterHandler.Select(Describe));
    }

    [Theory]
    [MemberData(nameof(StateClassificationCases))]
    public void RunStateExtensions_DefinedState_ClassifiesStateAndNamesVerdict(
        RunState state,
        bool terminal,
        bool inProgress,
        string? verdict)
    {
        Assert.Equal(terminal, state.IsTerminal());
        Assert.Equal(inProgress, state.IsInProgress());
        Assert.Equal(verdict, state.ToVerdictText());

        RunStateMachine machine = DriveTo(state);

        Assert.Equal(terminal, machine.IsTerminal);
        Assert.Equal(inProgress, machine.IsRunInProgress);
    }

    [Theory]
    [InlineData((RunState)42)]
    [InlineData((RunState)(-1))]
    public void RunStateExtensions_UndefinedState_IsNeitherTerminalNorInProgressAndHasNoVerdict(RunState state)
    {
        Assert.False(state.IsTerminal());
        Assert.False(state.IsInProgress());
        Assert.Null(state.ToVerdictText());
    }

    [Fact]
    public void ResolveTerminalState_UndefinedReason_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            "reason",
            () => RunStateMachine.ResolveTerminalState((RunEndReason)99, 0));
    }

    [Fact]
    public void TryFinish_UndefinedReason_ThrowsWithoutChange()
    {
        RunStateMachine machine = DriveTo(RunState.Running);

        AssertThrowsWithoutChange<ArgumentOutOfRangeException>(
            machine,
            () => machine.TryFinish((RunEndReason)99, 0, EndDetailText),
            "reason");
    }

    [Fact]
    public void TryTransition_UndefinedTarget_ThrowsWithoutChange()
    {
        RunStateMachine machine = DriveTo(RunState.Running);

        AssertThrowsWithoutChange<ArgumentOutOfRangeException>(
            machine,
            () => machine.TryTransition((RunState)42),
            "target");
    }

    [Fact]
    public void TryTransition_UndefinedEndReason_ThrowsWithoutChange()
    {
        RunStateMachine machine = DriveTo(RunState.Running);

        AssertThrowsWithoutChange<ArgumentOutOfRangeException>(
            machine,
            () => machine.TryTransition(RunState.Aborted, (RunEndReason)99, EndDetailText),
            "endReason");
    }

    [Theory]
    [InlineData(RunState.Running, RunEndReason.UserStop, null, "endReason")]
    [InlineData(RunState.Running, null, EndDetailText, "endDetail")]
    [InlineData(RunState.Preparing, RunEndReason.Completed, EndDetailText, "endReason")]
    [InlineData(RunState.Idle, null, EndDetailText, "endDetail")]
    public void TryTransition_EndReasonOrDetailWithNonTerminalTarget_ThrowsWithoutChange(
        RunState target,
        RunEndReason? reason,
        string? detail,
        string expectedParamName)
    {
        // Preparing → Running is an edge, so the guard, not the diagram, refuses the first case.
        RunStateMachine machine = DriveTo(RunState.Preparing);

        AssertThrowsWithoutChange<ArgumentException>(
            machine,
            () => machine.TryTransition(target, reason, detail),
            expectedParamName);
    }


    private static bool IsTerminalState(RunState state) =>
        state is RunState.Passed or RunState.Failed or RunState.Aborted;

    // The end reasons each terminal edge accepts. Idle → Failed is launch recovery, so it alone carries
    // PreviousRunUnfinished. The engine reports whichever stop reason it recorded first, even before
    // Prepared, so every stop or fault reason leads out of Preparing as well as out of Running.
    private static RunEndReason[] FittingReasons(RunState source, RunState target) => (source, target) switch
    {
        (RunState.Idle, RunState.Failed) => [RunEndReason.PreviousRunUnfinished],
        (RunState.Preparing or RunState.Running, RunState.Aborted) => _stopOrFaultReasons,
        (RunState.Running, RunState.Passed) => [RunEndReason.Completed],
        (RunState.Running, RunState.Failed) => [RunEndReason.Completed, RunEndReason.FirstError],
        _ => [],
    };

    // Whether a run in the source state can end for the reason: through a terminal edge out of that
    // state that accepts the reason.
    private static bool CanFinish(RunState source, RunEndReason reason, RunState terminal) =>
        _diagramEdges.Contains((source, terminal)) && FittingReasons(source, terminal).Contains(reason);

    private static TheoryData<RunState, RunState> EdgeCases(IEnumerable<(RunState From, RunState To)> edges)
    {
        var data = new TheoryData<RunState, RunState>();
        foreach ((RunState source, RunState target) in edges)
        {
            data.Add(source, target);
        }

        return data;
    }

    // Every terminal edge with every end reason and with none, split by whether the reason fits the edge.
    private static TheoryData<RunState, RunState, RunEndReason?> TerminalEdgeReasonCases(bool fitting)
    {
        var data = new TheoryData<RunState, RunState, RunEndReason?>();
        foreach ((RunState source, RunState target) in _diagramEdges.Where(edge => IsTerminalState(edge.To)))
        {
            RunEndReason[] fittingReasons = FittingReasons(source, target);
            if (!fitting)
            {
                data.Add(source, target, null);
            }

            foreach (RunEndReason reason in Enum.GetValues<RunEndReason>())
            {
                if (fittingReasons.Contains(reason) == fitting)
                {
                    data.Add(source, target, reason);
                }
            }
        }

        return data;
    }

    // Reaches the state through diagram edges only. Terminal states carry an end reason and detail, so a
    // test starting there can tell whether they were kept or cleared.
    private static RunStateMachine DriveTo(RunState state)
    {
        var machine = new RunStateMachine();
        if (state == RunState.Idle)
        {
            return machine;
        }

        Assert.True(machine.TryTransition(RunState.Preparing));
        if (state == RunState.Preparing)
        {
            return machine;
        }

        Assert.True(machine.TryTransition(RunState.Running));
        bool reached = state switch
        {
            RunState.Running => true,
            RunState.Passed => machine.TryFinish(RunEndReason.Completed, 0, PassedDetail),
            RunState.Failed => machine.TryFinish(RunEndReason.FirstError, 1, FailedDetail),
            RunState.Aborted => machine.TryFinish(RunEndReason.UserStop, 2, AbortedDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a defined run state."),
        };

        Assert.True(reached);
        Assert.Equal(state, machine.State);
        return machine;
    }

    private static List<RunStateChangedEventArgs> RecordChanges(RunStateMachine machine)
    {
        var changes = new List<RunStateChangedEventArgs>();
        machine.StateChanged += (_, change) => changes.Add(change);
        return changes;
    }

    private static (RunState Previous, RunState Current, RunEndReason? EndReason, string? EndDetail) Describe(
        RunStateChangedEventArgs change) =>
        (change.Previous, change.Current, change.EndReason, change.EndDetail);

    private static (RunState State, RunEndReason? EndReason, string? EndDetail) Snapshot(RunStateMachine machine) =>
        (machine.State, machine.EndReason, machine.EndDetail);

    private static void AssertEntered(
        RunStateMachine machine,
        List<RunStateChangedEventArgs> changes,
        RunState previous,
        RunState current,
        RunEndReason? endReason,
        string? endDetail)
    {
        Assert.Equal((current, endReason, endDetail), Snapshot(machine));
        RunStateChangedEventArgs change = Assert.Single(changes);
        Assert.Equal((previous, current, endReason, endDetail), Describe(change));
    }

    private static void AssertRefusedWithoutChange(RunStateMachine machine, Func<bool> attempt)
    {
        (RunState, RunEndReason?, string?) before = Snapshot(machine);
        List<RunStateChangedEventArgs> changes = RecordChanges(machine);

        Assert.False(attempt());

        Assert.Equal(before, Snapshot(machine));
        Assert.Empty(changes);
    }

    private static void AssertThrowsWithoutChange<TException>(
        RunStateMachine machine,
        Func<bool> attempt,
        string expectedParamName)
        where TException : ArgumentException
    {
        (RunState, RunEndReason?, string?) before = Snapshot(machine);
        List<RunStateChangedEventArgs> changes = RecordChanges(machine);

        Assert.Throws<TException>(expectedParamName, () => attempt());

        Assert.Equal(before, Snapshot(machine));
        Assert.Empty(changes);
    }
}
