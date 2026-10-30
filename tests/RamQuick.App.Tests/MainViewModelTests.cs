using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RamQuick.Analysis;
using RamQuick.App.Converters;
using RamQuick.App.Tests.Fakes;
using RamQuick.App.ViewModels;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;
using RamQuick.Sensors;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="MainViewModel"/> and its display helpers: defaults and the minimal three-control surface,
/// the Start/Stop commands and settings lock in every run state, the live fields' at-least-1-Hz refresh, the error
/// count past the detail cap, cause hints labelled as hints, the notice line, the memory summary and the result
/// panel; then <see cref="DisplayFormat"/>, <see cref="ErrorGroupItem"/>, <see cref="CauseHintItem"/> and
/// <see cref="RunStateToBrushConverter"/> directly.
/// </summary>
/// <remarks>
/// Each test builds its own <see cref="ControllerHarness"/>, so the view model reads a real
/// <see cref="Services.RunController"/> over fakes. Expected wording always comes from <see cref="UiText"/> and
/// numbers from <see cref="DisplayFormat"/>; the only literals are invariant number formats and the
/// "virtual address" label the first-error message must carry. Tests follow the harness's threading rule: no
/// <c>await</c> and no blocking on a task, only its pumping helpers.
/// </remarks>
public sealed class MainViewModelTests
{
    private const ulong GiB = FakeEngineData.GiB;
    private const ulong MiB = FakeEngineData.MiB;

    // Progress arrives at the engine's 4 Hz; the live-field check spans five simulated seconds of it.
    private const int TicksPerSecond = 4;
    private const int SimulatedSeconds = 5;

    // The first-error message's address label, an AAP display contract: no address is ever physical.
    private const string VirtualAddressToken = "virtual address";

    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _releaseTime = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _oneSecond = TimeSpan.FromSeconds(1);

    // Every field the window must refresh at least once a second while a run is in progress.
    private static readonly string[] _liveFieldNames =
    [
        nameof(MainViewModel.RamTemperatureText),
        nameof(MainViewModel.CpuTemperatureText),
        nameof(MainViewModel.CurrentStepText),
        nameof(MainViewModel.ElapsedText),
        nameof(MainViewModel.RemainingText),
        nameof(MainViewModel.CoverageText),
        nameof(MainViewModel.ErrorCountText),
        nameof(MainViewModel.StatusLineText),
    ];

    /// <summary>Every <see cref="CauseCategory"/>, for the per-category hint item checks.</summary>
    public static TheoryData<CauseCategory> AllCauseCategories => new(Enum.GetValues<CauseCategory>());

    /// <summary>Every <see cref="TestType"/>, for the per-type selector entry checks.</summary>
    public static TheoryData<TestType> AllTestTypes => new(Enum.GetValues<TestType>());

    // ---------------------------------------------------------------------------------------------
    // Defaults, surface and settings
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_Defaults_QuickStopOnErrorAndThreeTestTypeOptions()
    {
        using var harness = new ControllerHarness();
        using MainViewModel viewModel = CreateViewModel(harness);

        Assert.Equal(TestType.Quick, viewModel.SelectedTestType);
        Assert.True(viewModel.StopOnError);
        Assert.Equal(
            new[]
            {
                new TestTypeOption(TestType.Quick, UiText.TestTypeDisplay(TestType.Quick)),
                new TestTypeOption(TestType.Standard, UiText.TestTypeDisplay(TestType.Standard)),
                new TestTypeOption(TestType.Exhaustive, UiText.TestTypeDisplay(TestType.Exhaustive)),
            },
            viewModel.TestTypeOptions);
        Assert.Equal(RunState.Idle, viewModel.State);
        Assert.Equal(UiText.CauseHintsHeader, viewModel.CauseHintsHeader);
        Assert.Equal(UiText.ErrorGroupsHeader, viewModel.ErrorGroupsHeader);
        Assert.Empty(viewModel.CauseHints);
        Assert.Empty(viewModel.ErrorGroups);
        Assert.False(viewModel.HasResult);
        Assert.Equal(string.Empty, viewModel.VerdictText);
        Assert.Equal(string.Empty, viewModel.FirstErrorText);

        // Until launch recovery has finished, nothing can start and the settings stay locked.
        AssertControls(viewModel, canStart: false, canStop: false, inProgress: false, canChangeSettings: false);
    }

    [Theory]
    [MemberData(nameof(AllTestTypes))]
    public void TestTypeOption_ToString_IsTheDisplayTextAssistiveTechnologyReads(TestType type)
    {
        using var harness = new ControllerHarness();
        using MainViewModel viewModel = CreateViewModel(harness);

        // The selector's item and selection automation names fall back to the entry's ToString, so it must be the
        // label the entry shows, never the record's member listing.
        TestTypeOption option = Assert.Single(viewModel.TestTypeOptions, candidate => candidate.Value == type);

        Assert.Equal(UiText.TestTypeDisplay(type), option.Text);
        Assert.Equal(option.Text, option.ToString());
    }

    [Fact]
    public void PublicSurface_MinimalUi_TwoSettingsAndOneStartStopCommandPair()
    {
        // One window, three controls: the selector and the checkbox are the only settable members, and the single
        // Start/Stop toggle binds one of exactly two commands.
        PropertyInfo[] properties = typeof(MainViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        string[] settable = properties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] commands = properties
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { nameof(MainViewModel.SelectedTestType), nameof(MainViewModel.StopOnError) }, settable);
        Assert.Equal(new[] { nameof(MainViewModel.StartCommand), nameof(MainViewModel.StopCommand) }, commands);
    }

    [Fact]
    public void Constructor_NullArgumentOrForeignStateMachine_Throws()
    {
        using var harness = new ControllerHarness();

        Assert.Throws<ArgumentNullException>("controller", () => new MainViewModel(null!, harness.StateMachine));
        Assert.Throws<ArgumentNullException>("stateMachine", () => new MainViewModel(harness.Controller, null!));

        // A second state machine would never move, so the window would show Idle for the whole run.
        Assert.Throws<ArgumentException>("stateMachine", () => new MainViewModel(harness.Controller, new RunStateMachine()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commands_UserStoppedRun_FollowRunStateUntilPublication(bool stopWhileRunning)
    {
        using var harness = new ControllerHarness();
        using MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);
        int startChanges = 0;
        int stopChanges = 0;
        viewModel.StartCommand.CanExecuteChanged += (_, _) => startChanges++;
        viewModel.StopCommand.CanExecuteChanged += (_, _) => stopChanges++;

        // Subscribed after the view model, so each snapshot sees the view model's own reaction to the transition.
        var transitions = new List<TransitionSnapshot>();
        harness.StateMachine.StateChanged += (_, e) => transitions.Add(new TransitionSnapshot(
            e.Current,
            startChanges,
            stopChanges,
            viewModel.StartCommand.CanExecute(null),
            viewModel.StopCommand.CanExecute(null),
            viewModel.IsRunInProgress,
            viewModel.CanChangeSettings));

        harness.Initialize();

        AssertControls(viewModel, canStart: true, canStop: false, inProgress: false, canChangeSettings: true);
        Assert.True(startChanges > 0, "Enabling Start at the end of initialisation must re-evaluate StartCommand.");
        Assert.Equal(UiText.NotYetKnown, viewModel.CurrentStepText);
        int startChangesBeforeRun = startChanges;
        int stopChangesBeforeRun = stopChanges;

        FakeEngineSession session = StartThroughCommand(harness, viewModel);

        AssertControls(viewModel, canStart: false, canStop: true, inProgress: true, canChangeSettings: false);
        Assert.Equal(UiText.PreparingMemory, viewModel.CurrentStepText);
        if (stopWhileRunning)
        {
            harness.Prepare();

            AssertControls(viewModel, canStart: false, canStop: true, inProgress: true, canChangeSettings: false);
            StepAttributes firstStep = session.Options.Steps[0];
            Assert.Equal(
                DisplayFormat.StepProgress(firstStep.Name, firstStep.Index, session.Options.Steps.Count),
                viewModel.CurrentStepText);
        }

        Execute(harness, viewModel.StopCommand);

        Assert.Equal(new[] { StopReason.User }, session.StopRequests);
        session.CompleteWithFirstRecordedReason();
        harness.AwaitVerdict();

        // The verdict is shown, but Start waits for Released and the published summaries.
        Assert.Equal(RunState.Aborted, viewModel.State);
        AssertControls(viewModel, canStart: false, canStop: false, inProgress: false, canChangeSettings: false);
        Assert.Equal(UiText.ReleasingMemory, viewModel.CurrentStepText);
        Assert.Equal(UiText.StoppedByUser, viewModel.ResultReasonText);
        Assert.False(viewModel.HasFirstError);
        Assert.False(viewModel.HasErrorGroups);
        Assert.False(viewModel.HasCauseHints);

        harness.Release(_releaseTime);

        AssertControls(viewModel, canStart: true, canStop: false, inProgress: false, canChangeSettings: true);
        Assert.Equal(UiText.NotYetKnown, viewModel.CurrentStepText);
        Assert.True(viewModel.HasResult);

        RunState[] expectedStates = stopWhileRunning
            ? [RunState.Preparing, RunState.Running, RunState.Aborted]
            : [RunState.Preparing, RunState.Aborted];
        Assert.Equal(expectedStates, transitions.Select(transition => transition.State));
        int previousStartChanges = startChangesBeforeRun;
        int previousStopChanges = stopChangesBeforeRun;
        foreach (TransitionSnapshot transition in transitions)
        {
            Assert.True(transition.StartChanges > previousStartChanges, $"StartCommand was not re-evaluated on entering {transition.State}.");
            Assert.True(transition.StopChanges > previousStopChanges, $"StopCommand was not re-evaluated on entering {transition.State}.");
            previousStartChanges = transition.StartChanges;
            previousStopChanges = transition.StopChanges;

            bool inProgress = transition.State is RunState.Preparing or RunState.Running;
            Assert.Equal(inProgress, transition.IsRunInProgress);
            Assert.Equal(inProgress, transition.CanStop);
            Assert.False(transition.CanStart);
            Assert.False(transition.CanChangeSettings);
        }

        // The step field reports the release, then the publication, before Start returns.
        List<object?> stepTexts = recorder.ValuesOf(nameof(MainViewModel.CurrentStepText)).ToList();
        int releasing = stepTexts.IndexOf(UiText.ReleasingMemory);
        Assert.True(releasing >= 0, $"The step field never read '{UiText.ReleasingMemory}'.");
        int saving = stepTexts.IndexOf(UiText.SavingResults, releasing + 1);
        Assert.True(saving > releasing, $"The step field never read '{UiText.SavingResults}' after '{UiText.ReleasingMemory}'.");
    }

    [Fact]
    public void StartCommand_StandardWithoutStopOnError_CreatesSessionWithTwelveStepsAndStopOnErrorOff()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);

        viewModel.SelectedTestType = TestType.Standard;
        viewModel.StopOnError = false;
        FakeEngineSession session = StartThroughCommand(harness, viewModel);

        Assert.Equal(1, recorder.RaisedCount(nameof(MainViewModel.SelectedTestType)));
        Assert.Equal(1, recorder.RaisedCount(nameof(MainViewModel.StopOnError)));
        Assert.Equal(12, session.Options.Steps.Count);
        Assert.Equal(TestPlans.Standard.ToStepAttributes(), session.Options.Steps);
        Assert.False(session.Options.StopOnError);
        Assert.Same(TestPlans.Standard, harness.Controller.ActivePlan);
    }

    [Fact]
    public void StartCommand_AfterFailedRunWithErrors_EnteringPreparingClearsThePreviousResult()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        FakeEngineSession first = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = first.Options.Steps[0];
        first.RaiseErrors(FakeEngineData.Errors(3, stepIndex: step.Index, stepName: step.Name), 3);
        harness.RunUntil(() => harness.Controller.TotalErrors == 3);
        first.RaiseStepFinished(step.Index, errors: 3);
        harness.Complete(RunEndReason.Completed, totalErrors: 3);
        harness.Release(_releaseTime);
        Assert.Equal(RunState.Failed, viewModel.State);
        Assert.True(viewModel.HasFirstError);
        Assert.True(viewModel.HasErrorGroups);
        Assert.True(viewModel.HasCauseHints);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);

        StartThroughCommand(harness, viewModel);

        Assert.False(viewModel.HasResult);
        Assert.Equal(string.Empty, viewModel.VerdictText);
        Assert.Equal(string.Empty, viewModel.ResultReasonText);
        Assert.Equal(string.Empty, viewModel.FirstErrorText);
        Assert.Empty(viewModel.CauseHints);
        Assert.Empty(viewModel.ErrorGroups);
        Assert.Equal(UiText.ErrorGroupsHeader, viewModel.ErrorGroupsHeader);
        Assert.Equal(UiText.NotYetKnown, viewModel.TestedText);
        Assert.Equal(UiText.NotYetKnown, viewModel.ErrorCountText);

        // The transition to Preparing comes before the controller clears its result, and even then no stale detail
        // or verdict is shown beside the new run.
        Assert.All(
            new[]
            {
                nameof(MainViewModel.HasResult),
                nameof(MainViewModel.HasFirstError),
                nameof(MainViewModel.HasErrorGroups),
                nameof(MainViewModel.HasCauseHints),
            },
            field =>
            {
                IReadOnlyList<object?> shown = recorder.ValuesOf(field);
                Assert.NotEmpty(shown);
                Assert.All(shown, value => Assert.False(Assert.IsType<bool>(value)));
            });
    }

    [Fact]
    public void StartCommand_NotElevated_StaysEnabledAndOffersRestartInsteadOfStarting()
    {
        using var harness = new ControllerHarness();
        harness.Elevation.IsElevated = false;
        harness.StartSensors();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        Assert.Equal(1, harness.Prompt.AskCount);

        // Start is the route to the restart offer, so it stays enabled without a fourth control.
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.Equal(UiText.NotElevatedNotice, viewModel.NoticeText);
        Assert.True(viewModel.HasNotice);

        Execute(harness, viewModel.StartCommand);
        harness.Pump();

        Assert.Equal(2, harness.Prompt.AskCount);
        PromptCall prompt = harness.Prompt.AskCalls[^1];
        Assert.Equal(UiText.ElevationPromptTitle, prompt.Title);
        Assert.Equal(UiText.ElevationPromptMessage, prompt.Message);
        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Equal(RunState.Idle, viewModel.State);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.Contains(UiText.NotElevatedNotice, viewModel.NoticeText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Live fields
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void LiveFields_FourHzProgressWhileRunning_EachRaisedInEverySimulatedSecond()
    {
        using var harness = new ControllerHarness();
        harness.StartSensors();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        int stepCount = session.Options.Steps.Count;
        ulong regionBytes = session.Options.RegionBytes;
        TimeSpan plannedDuration = TestPlans.Quick.TargetDuration;
        int readingsAtStart = harness.ReadingsDelivered;

        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);
        DateTimeOffset start = harness.Time.GetUtcNow();
        RunProgress? last = null;
        for (int tick = 1; tick <= SimulatedSeconds * TicksPerSecond; tick++)
        {
            // The clock has not moved since the sensor timer started, so every fourth tick is also a reading.
            bool readingDue = tick % TicksPerSecond == 0;
            int readingsBefore = harness.ReadingsDelivered;
            harness.Time.Advance(_progressInterval);

            TimeSpan elapsed = _progressInterval * tick;
            RunProgress progress = FakeEngineData.Progress(
                step.Index,
                stepCount,
                step.Name,
                elapsed,
                remaining: tick > TicksPerSecond ? plannedDuration - elapsed : null,
                verifiedBytes: (ulong)tick * 64 * MiB,
                regionBytes: regionBytes,
                totalErrors: 0);
            session.PublishProgress(progress);
            harness.RunUntil(() => ReferenceEquals(harness.Controller.CurrentProgress, progress)
                && (!readingDue || harness.ReadingsDelivered > readingsBefore));
            last = progress;
        }

        for (int second = 0; second < SimulatedSeconds; second++)
        {
            DateTimeOffset from = start + (_oneSecond * second);
            DateTimeOffset until = from + _oneSecond;
            foreach (string field in _liveFieldNames)
            {
                Assert.True(
                    recorder.CountBetween(field, from, until) >= 1,
                    $"{field} was not refreshed in simulated second {second + 1}.");
            }
        }

        Assert.Equal(SimulatedSeconds, harness.ReadingsDelivered - readingsAtStart);
        Assert.NotNull(last);
        Assert.Equal(DisplayFormat.Duration(last.Elapsed), viewModel.ElapsedText);
        Assert.Equal(DisplayFormat.Remaining(last.Remaining), viewModel.RemainingText);
        Assert.Equal(
            DisplayFormat.Coverage(last.CoveragePercent, TestPlans.Quick.TargetCoveragePercent),
            viewModel.CoverageText);
        Assert.Equal(DisplayFormat.Temperature(harness.Controller.LatestReadings.Ram), viewModel.RamTemperatureText);
        Assert.Equal(DisplayFormat.Temperature(harness.Controller.LatestReadings.Cpu), viewModel.CpuTemperatureText);
    }

    [Fact]
    public void TemperatureFields_OneReadingPerSecondWithoutProgress_RaisedEachSecondWithReadingOrNotAvailable()
    {
        using var harness = new ControllerHarness();
        harness.StartSensors();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);

        // Whole degrees, rounded half away from zero.
        harness.Sensors.SetCpu(71.4);
        harness.Sensors.SetRam(47.5);
        AssertTemperaturesRaisedByNextReading(harness, recorder);
        Assert.Equal(Temperature("71"), viewModel.CpuTemperatureText);
        Assert.Equal(Temperature("48"), viewModel.RamTemperatureText);

        // Each reading has its own status.
        harness.Sensors.SetCpu(SensorStatus.DriverUnavailable);
        AssertTemperaturesRaisedByNextReading(harness, recorder);
        Assert.Equal(UiText.NotAvailable, viewModel.CpuTemperatureText);
        Assert.Equal(Temperature("48"), viewModel.RamTemperatureText);

        harness.Sensors.SetRam(SensorStatus.NoSensor);
        AssertTemperaturesRaisedByNextReading(harness, recorder);
        Assert.Equal(UiText.NotAvailable, viewModel.CpuTemperatureText);
        Assert.Equal(UiText.NotAvailable, viewModel.RamTemperatureText);

        harness.Sensors.SetCpu(SensorStatus.Available);
        AssertTemperaturesRaisedByNextReading(harness, recorder);
        Assert.Equal(Temperature("71"), viewModel.CpuTemperatureText);
        Assert.Equal(UiText.NotAvailable, viewModel.RamTemperatureText);
    }

    [Fact]
    public void RunFields_PreparingThenProgress_ShowStepTimesAndCoverageAgainstPlanTarget()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);

        // Idle: no run figures yet, and no tested capacity.
        Assert.All(
            new[] { viewModel.CurrentStepText, viewModel.ElapsedText, viewModel.RemainingText, viewModel.CoverageText, viewModel.ErrorCountText, viewModel.TestedText },
            text => Assert.Equal(UiText.NotYetKnown, text));

        FakeEngineSession session = harness.StartAccepted();

        Assert.Equal(UiText.PreparingMemory, viewModel.CurrentStepText);
        Assert.All(
            new[] { viewModel.ElapsedText, viewModel.RemainingText, viewModel.CoverageText, viewModel.ErrorCountText },
            text => Assert.Equal(UiText.NotYetKnown, text));

        harness.Prepare();
        IReadOnlyList<StepAttributes> steps = session.Options.Steps;
        int target = TestPlans.Quick.TargetCoveragePercent;

        // Running before the first tick: step 1 is about to begin and nothing is measured yet.
        Assert.Equal(DisplayFormat.StepProgress(steps[0].Name, 0, steps.Count), viewModel.CurrentStepText);
        Assert.Equal(DisplayFormat.Duration(TimeSpan.Zero), viewModel.ElapsedText);
        Assert.Equal(UiText.Calculating, viewModel.RemainingText);
        Assert.Equal(DisplayFormat.Coverage(0, target), viewModel.CoverageText);
        Assert.Equal(DisplayFormat.ErrorTotal(0), viewModel.ErrorCountText);

        // Until step 1 has completed its first pass the engine reports no estimate.
        ulong regionBytes = session.Options.RegionBytes;
        RunProgress firstPass = FakeEngineData.Progress(
            steps[0].Index, steps.Count, steps[0].Name, TimeSpan.FromSeconds(10), null, regionBytes / 4, regionBytes, 0);
        PublishAndAwait(harness, session, firstPass);

        Assert.Equal(DisplayFormat.StepProgress(steps[0].Name, 0, steps.Count), viewModel.CurrentStepText);
        Assert.Equal(DisplayFormat.Duration(firstPass.Elapsed), viewModel.ElapsedText);
        Assert.Equal(UiText.Calculating, viewModel.RemainingText);
        Assert.Equal(DisplayFormat.Coverage(firstPass.CoveragePercent, target), viewModel.CoverageText);

        StepAttributes third = steps[2];
        RunProgress later = FakeEngineData.Progress(
            third.Index,
            steps.Count,
            third.Name,
            TimeSpan.FromSeconds(104.25),
            TimeSpan.FromSeconds(195.75),
            regionBytes / 1000 * 4126,
            regionBytes,
            0);
        PublishAndAwait(harness, session, later);

        Assert.Equal(DisplayFormat.StepProgress(third.Name, third.Index, steps.Count), viewModel.CurrentStepText);
        Assert.Equal(DisplayFormat.Duration(later.Elapsed), viewModel.ElapsedText);
        Assert.Equal(DisplayFormat.Remaining(later.Remaining), viewModel.RemainingText);
        Assert.Equal(DisplayFormat.Coverage(later.CoveragePercent, target), viewModel.CoverageText);
        Assert.Equal(DisplayFormat.Capacity(regionBytes), viewModel.TestedText);
    }

    // ---------------------------------------------------------------------------------------------
    // Errors, detail cap and cause hints
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ErrorFields_PastDetailCap_CountKeepsRisingAndGroupsAreLabelledStoredOnly()
    {
        const int Repeats = 1_000;
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        RunProgress progress = FakeEngineData.Progress(
            step.Index, session.Options.Steps.Count, step.Name, TimeSpan.FromSeconds(10), null, GiB, session.Options.RegionBytes, 0);
        PublishAndAwait(harness, session, progress);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);

        // Exactly the stored-detail cap: one word hit 2,000 times on bits 5 and 17, one word hit 1,000 times on
        // bit 3, and 7,000 words hit once each.
        IReadOnlyList<ErrorRecord> bit5AtFirstWord = FakeEngineData.Errors(
            Repeats, firstSequence: 1, bit: 5, stepIndex: step.Index, stepName: step.Name, firstRegionOffset: 0, strideBytes: 0);
        IReadOnlyList<ErrorRecord> bit17AtFirstWord = FakeEngineData.Errors(
            Repeats, firstSequence: 1_001, bit: 17, stepIndex: step.Index, stepName: step.Name, firstRegionOffset: 0, strideBytes: 0);
        IReadOnlyList<ErrorRecord> bit3AtSecondWord = FakeEngineData.Errors(
            Repeats, firstSequence: 2_001, bit: 3, stepIndex: step.Index, stepName: step.Name, firstRegionOffset: 8, strideBytes: 0);
        IReadOnlyList<ErrorRecord> spread = FakeEngineData.Errors(
            7_000, firstSequence: 3_001, stepIndex: step.Index, stepName: step.Name, firstRegionOffset: MiB);
        ErrorRecord[] firstBatch = [.. bit5AtFirstWord, .. bit17AtFirstWord, .. bit3AtSecondWord, .. spread.Take(2_000)];
        ErrorRecord[] secondBatch = [.. spread.Skip(2_000)];
        Assert.Equal(ErrorGrouping.StoredDetailCap, firstBatch.Length + secondBatch.Length);

        session.RaiseErrors(firstBatch, (ulong)firstBatch.Length);
        session.RaiseErrors(secondBatch, ErrorGrouping.StoredDetailCap);
        harness.RunUntil(() => harness.Controller.TotalErrors == ErrorGrouping.StoredDetailCap);

        // At the cap every error is still stored, so the header is the ordinary one.
        Assert.Equal(DisplayFormat.ErrorTotal(ErrorGrouping.StoredDetailCap), viewModel.ErrorCountText);
        Assert.Equal(UiText.ErrorGroupsHeader, viewModel.ErrorGroupsHeader);
        Assert.True(viewModel.HasErrorGroups);
        Assert.True(viewModel.HasFirstError);
        IReadOnlyList<ErrorGroupItem> groups = viewModel.ErrorGroups;
        Assert.Equal(MainViewModel.MaxDisplayedErrorGroups, groups.Count);
        Assert.Equal(
            harness.Controller.ErrorGroups.Take(MainViewModel.MaxDisplayedErrorGroups),
            groups.Select(item => item.Group));
        for (int index = 1; index < groups.Count; index++)
        {
            Assert.True(groups[index - 1].Count >= groups[index].Count, $"Group {index} is larger than the group before it.");
        }

        Assert.Equal(FakeEngineData.AddressAt(0), groups[0].WordAddress);
        Assert.Equal(2 * Repeats, groups[0].Count);
        Assert.Equal(DisplayFormat.Bits((1UL << 5) | (1UL << 17)), groups[0].BitsText);
        Assert.Equal(FakeEngineData.AddressAt(8), groups[1].WordAddress);
        Assert.Equal(Repeats, groups[1].Count);
        Assert.Equal(DisplayFormat.Bits(1UL << 3), groups[1].BitsText);

        // Past the cap the true total keeps rising through progress and through batches without records.
        session.PublishProgress(progress with { TotalErrors = 12_000 });
        harness.RunUntil(() => harness.Controller.TotalErrors == 12_000);
        Assert.Equal(DisplayFormat.ErrorTotal(12_000), viewModel.ErrorCountText);
        Assert.Equal(UiText.ErrorGroupsTruncatedHeader, viewModel.ErrorGroupsHeader);

        session.RaiseErrors(Array.Empty<ErrorRecord>(), 15_000);
        harness.RunUntil(() => harness.Controller.TotalErrors == 15_000);
        Assert.Equal(DisplayFormat.ErrorTotal(15_000), viewModel.ErrorCountText);
        Assert.Same(groups, viewModel.ErrorGroups);

        Assert.Equal(
            new[]
            {
                DisplayFormat.ErrorTotal((ulong)firstBatch.Length),
                DisplayFormat.ErrorTotal(ErrorGrouping.StoredDetailCap),
                DisplayFormat.ErrorTotal(12_000),
                DisplayFormat.ErrorTotal(15_000),
            },
            DistinctConsecutive(recorder.ValuesOf(nameof(MainViewModel.ErrorCountText)).OfType<string>()));
        Assert.Equal(
            new[] { UiText.ErrorGroupsTruncatedHeader },
            recorder.ValuesOf(nameof(MainViewModel.ErrorGroupsHeader)).OfType<string>());

        // The verdict keeps the true total and the stored-only label.
        harness.Complete(RunEndReason.Completed, totalErrors: 16_500);
        Assert.Equal(RunState.Failed, viewModel.State);
        Assert.Equal(DisplayFormat.ErrorTotal(16_500), viewModel.ErrorCountText);
        Assert.Equal(UiText.ErrorGroupsTruncatedHeader, viewModel.ErrorGroupsHeader);
        Assert.True(viewModel.HasErrorGroups);
    }

    [Theory]
    [InlineData(RunEndReason.Completed, 0UL, RunState.Passed)]
    [InlineData(RunEndReason.Completed, 3UL, RunState.Failed)]
    [InlineData(RunEndReason.FirstError, 1UL, RunState.Failed)]
    [InlineData(RunEndReason.UserStop, 3UL, RunState.Aborted)]
    [InlineData(RunEndReason.UserStop, 0UL, RunState.Aborted)]
    public void ResultPanel_TerminalState_ShowsVerdictReasonAndLabelledHintsOnlyWithErrors(
        RunEndReason endReason,
        ulong errors,
        RunState expectedState)
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        FakeEngineSession session = harness.StartAccepted(stopOnError: endReason == RunEndReason.FirstError);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        bool withErrors = errors > 0;
        if (withErrors)
        {
            session.RaiseErrors(FakeEngineData.Errors((int)errors, stepIndex: step.Index, stepName: step.Name), errors);
            harness.RunUntil(() => harness.Controller.TotalErrors == errors);

            // Error detail is shown while the run is still going; hints only with the verdict.
            Assert.True(viewModel.HasFirstError);
            Assert.True(viewModel.HasErrorGroups);
            Assert.False(viewModel.HasCauseHints);
        }

        // No verdict yet: the status line names the state instead.
        Assert.False(viewModel.HasResult);
        Assert.Equal(string.Empty, viewModel.VerdictText);
        Assert.Equal(StatusLine(viewModel, RunState.Running.ToString()), viewModel.StatusLineText);

        session.RaiseStepFinished(step.Index, errors: errors, completed: endReason == RunEndReason.Completed);
        harness.Complete(endReason, errors);

        Assert.Equal(expectedState, viewModel.State);
        Assert.True(viewModel.HasResult);
        Assert.Equal(expectedState.ToVerdictText(), viewModel.VerdictText);
        Assert.False(string.IsNullOrEmpty(viewModel.ResultReasonText));
        Assert.Equal(harness.Controller.ResultMessage, viewModel.ResultReasonText);
        Assert.Equal(StatusLine(viewModel, viewModel.VerdictText), viewModel.StatusLineText);
        Assert.Equal(DisplayFormat.ErrorTotal(errors), viewModel.ErrorCountText);
        Assert.Equal(UiText.CauseHintsHeader, viewModel.CauseHintsHeader);
        Assert.Equal(withErrors, viewModel.HasFirstError);
        Assert.Equal(withErrors, viewModel.HasErrorGroups);
        Assert.Equal(withErrors, viewModel.HasCauseHints);
        if (withErrors)
        {
            Assert.InRange(viewModel.CauseHints.Count, 1, AnalyzerThresholds.DisplayedHintCount);
            Assert.Equal(harness.Controller.CauseHints.Top(), viewModel.CauseHints.Select(item => item.Hint));
            Assert.All(viewModel.CauseHints, item => Assert.StartsWith(UiText.HintPrefix, item.Text, StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(viewModel.CauseHints);
            Assert.Empty(viewModel.ErrorGroups);
            Assert.Equal(string.Empty, viewModel.FirstErrorText);
        }
    }

    [Fact]
    public void FirstErrorText_ErrorsWhileRunning_ShowsVirtualAddressRawWordsAndRereadInference()
    {
        using var harness = new ControllerHarness();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        FakeEngineSession session = harness.StartAccepted(stopOnError: false);
        harness.Prepare();
        StepAttributes step = session.Options.Steps[1];
        ErrorRecord first = FakeEngineData.Error(
            1, FakeEngineData.AddressAt(0x4F7C_1D48), 1, step.Index, step.Name, timeIntoTest: TimeSpan.FromSeconds(72.5));
        ErrorRecord second = FakeEngineData.Error(
            2, FakeEngineData.AddressAt(0x5000_0000), 9, step.Index, step.Name, rereadOutcome: RereadOutcome.RereadDiffers, timeIntoTest: TimeSpan.FromSeconds(73));
        Assert.False(viewModel.HasFirstError);
        Assert.Equal(string.Empty, viewModel.FirstErrorText);

        session.RaiseErrors(new[] { first, second }, 3);
        harness.RunUntil(() => harness.Controller.TotalErrors == 3);

        string text = viewModel.FirstErrorText;
        Assert.True(viewModel.HasFirstError);
        Assert.Equal(DisplayFormat.FirstError(first, 3), text);
        Assert.Contains(VirtualAddressToken, text, StringComparison.Ordinal);
        Assert.Contains(first.StepName, text, StringComparison.Ordinal);
        Assert.Contains(DisplayFormat.Duration(first.TimeIntoTest), text, StringComparison.Ordinal);
        Assert.Contains(UiText.ErrorCount(3), text, StringComparison.Ordinal);
        Assert.Contains(RereadOutcomeText.Describe(first.RereadOutcome), text, StringComparison.Ordinal);
        Assert.All(
            new[] { first.Address, first.Expected, first.Actual, first.Reread },
            word => Assert.Contains(DisplayFormat.Hex64(word), text, StringComparison.Ordinal));

        // The message follows the true total, and stays with the FAIL verdict.
        RunProgress progress = FakeEngineData.Progress(
            step.Index, session.Options.Steps.Count, step.Name, TimeSpan.FromSeconds(74), null, GiB, session.Options.RegionBytes, 5);
        PublishAndAwait(harness, session, progress);
        Assert.Equal(DisplayFormat.FirstError(first, 5), viewModel.FirstErrorText);

        harness.Complete(RunEndReason.Completed, totalErrors: 5);
        Assert.Equal(RunState.Failed, viewModel.State);
        Assert.True(viewModel.HasFirstError);
        Assert.Equal(DisplayFormat.FirstError(first, 5), viewModel.FirstErrorText);
    }

    // ---------------------------------------------------------------------------------------------
    // Notices and memory summary
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(false, false, UiText.SensorsUnavailableNotice)]
    [InlineData(false, true, UiText.CpuSensorUnavailableNotice)]
    [InlineData(true, false, UiText.RamSensorUnavailableNotice)]
    public void NoticeText_SensorAvailability_NamesOnlyTheInactiveThermalLimit(
        bool cpuAvailable,
        bool ramAvailable,
        string? expectedNotice)
    {
        using var harness = new ControllerHarness();
        if (!cpuAvailable)
        {
            harness.Sensors.SetCpu(SensorStatus.DriverUnavailable);
        }

        if (!ramAvailable)
        {
            harness.Sensors.SetRam(SensorStatus.DriverUnavailable);
        }

        harness.StartSensors();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);

        // Elevated, AVX2 and no controller notice: the sensor notice is the only one that can apply.
        Assert.Equal(expectedNotice ?? string.Empty, viewModel.NoticeText);
        Assert.Equal(expectedNotice is not null, viewModel.HasNotice);
        Assert.Equal(cpuAvailable, viewModel.CpuTemperatureText != UiText.NotAvailable);
        Assert.Equal(ramAvailable, viewModel.RamTemperatureText != UiText.NotAvailable);
    }

    [Theory]
    [InlineData(InstructionSet.Sse2, true)]
    [InlineData(InstructionSet.Avx2, false)]
    [InlineData(InstructionSet.Avx512, false)]
    public void NoticeText_BestInstructionSet_ReducedSetNoticeOnlyForSse2(InstructionSet bestIsa, bool reduced)
    {
        using var harness = new ControllerHarness();
        harness.Engine.WithIsa(bestIsa);
        harness.StartSensors();
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);

        Assert.Equal(reduced ? UiText.ReducedInstructionSetNotice : string.Empty, viewModel.NoticeText);
        Assert.Equal(reduced, viewModel.HasNotice);
    }

    [Fact]
    public void NoticeText_SeveralNoticesApply_JoinedInFixedOrderWithControllerNoticeFirst()
    {
        using var harness = new ControllerHarness();
        harness.Engine.WithIsa(InstructionSet.Sse2).WithAvailableBytes(3 * GiB / 2);

        // The sensor monitor is never started, so both readings stay stale.
        harness.Initialize();
        using MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);
        Assert.Equal(UiText.NotAvailable, viewModel.CpuTemperatureText);
        Assert.Equal(UiText.NotAvailable, viewModel.RamTemperatureText);
        Assert.Equal(
            string.Join(UiText.NoticeSeparator, UiText.ReducedInstructionSetNotice, UiText.SensorsUnavailableNotice),
            viewModel.NoticeText);

        // 1.5 GiB available plans only 0.5 GiB, so Start is refused with the low-memory notice.
        Execute(harness, viewModel.StartCommand);

        Assert.Equal(0, harness.Engine.CreateSessionCount);
        Assert.Equal(RunState.Idle, viewModel.State);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.True(recorder.RaisedCount(nameof(MainViewModel.NoticeText)) >= 1, "The refused Start did not refresh the notice line.");
        Assert.True(viewModel.HasNotice);
        Assert.Equal(
            string.Join(
                UiText.NoticeSeparator,
                UiText.LowMemoryMessage,
                UiText.ReducedInstructionSetNotice,
                UiText.SensorsUnavailableNotice),
            viewModel.NoticeText);
    }

    [Fact]
    public void MemorySummary_DefaultInventory_ShowsFirmwareValuesAndTestedCapacityFromPrepared()
    {
        using var harness = new ControllerHarness();
        using MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);
        MemoryInventory inventory = FakeMemoryInventorySource.DefaultInventory;
        Assert.Equal(new MemoryInventory(32 * GiB, 2, 6000, 2, MemoryType.DDR5), inventory);

        harness.Initialize();

        Assert.All(
            new[]
            {
                nameof(MainViewModel.InstalledText),
                nameof(MainViewModel.SpeedText),
                nameof(MainViewModel.ChannelsText),
                nameof(MainViewModel.DimmsText),
            },
            field => Assert.True(recorder.RaisedCount(field) >= 1, $"{field} was not refreshed when the inventory arrived."));
        Assert.Equal(DisplayFormat.Speed(inventory.SpeedMTs), viewModel.SpeedText);
        Assert.Equal(DisplayFormat.Count(inventory.ChannelCount), viewModel.ChannelsText);
        Assert.Equal(DisplayFormat.Count(inventory.DimmCount), viewModel.DimmsText);
        Assert.Equal(DisplayFormat.Capacity(inventory.InstalledBytes), viewModel.InstalledText);
        Assert.Equal(UiText.NotYetKnown, viewModel.TestedText);

        FakeEngineSession session = harness.StartAccepted();
        Assert.Equal(UiText.NotYetKnown, viewModel.TestedText);

        // Locking had to shrink the region: the summary shows what was actually locked.
        ulong lockedBytes = 16 * GiB;
        Assert.True(lockedBytes < session.Options.RegionBytes);
        session.RaisePrepared(testedBytes: lockedBytes);
        harness.RunUntil(() => harness.StateMachine.State == RunState.Running);

        Assert.Equal(DisplayFormat.Capacity(lockedBytes), viewModel.TestedText);
        Assert.True(recorder.RaisedCount(nameof(MainViewModel.TestedText)) >= 1, "TestedText was not refreshed at Prepared.");
    }

    [Fact]
    public void MemorySummary_UnknownInventory_ShowsUnknownAndEngineTotalAsInstalled()
    {
        using var harness = new ControllerHarness();
        harness.Inventory.Inventory = MemoryInventory.Unknown;
        using MainViewModel viewModel = CreateViewModel(harness);

        // Before initialisation neither the firmware tables nor the engine have reported anything.
        Assert.Equal(UiText.Unknown, viewModel.InstalledText);

        harness.Initialize();

        Assert.Equal(UiText.Unknown, viewModel.SpeedText);
        Assert.Equal(UiText.Unknown, viewModel.ChannelsText);
        Assert.Equal(UiText.Unknown, viewModel.DimmsText);
        Assert.Equal(DisplayFormat.Capacity(harness.Engine.System.TotalPhysicalBytes), viewModel.InstalledText);
    }

    [Fact]
    public void Dispose_CalledTwice_RaisesNothingAndIgnoresCommandsAfterwards()
    {
        using var harness = new ControllerHarness();
        MainViewModel viewModel = CreateViewModel(harness);
        using var recorder = new PropertyChangeRecorder(viewModel, harness.Time);
        int commandChanges = 0;
        viewModel.StartCommand.CanExecuteChanged += (_, _) => commandChanges++;
        viewModel.StopCommand.CanExecuteChanged += (_, _) => commandChanges++;

        viewModel.Dispose();
        viewModel.Dispose();

        harness.StartSensors();
        harness.Initialize();
        Execute(harness, viewModel.StartCommand);
        harness.Pump();
        Assert.Equal(0, harness.Engine.CreateSessionCount);

        FakeEngineSession session = harness.StartAccepted();
        harness.Prepare();
        StepAttributes step = session.Options.Steps[0];
        PublishAndAwait(
            harness,
            session,
            FakeEngineData.Progress(step.Index, session.Options.Steps.Count, step.Name, _oneSecond, null, GiB, session.Options.RegionBytes, 0));
        harness.NextReading();
        Execute(harness, viewModel.StopCommand);

        Assert.Empty(session.StopRequests);
        Assert.Empty(recorder.Changes);
        Assert.Equal(0, commandChanges);
    }


    // ---------------------------------------------------------------------------------------------
    // DisplayFormat, item view models and the verdict brush, without a harness
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1.0, "00:00")]
    [InlineData(0.0, "00:00")]
    [InlineData(72.9, "01:12")]
    [InlineData(300.0, "05:00")]
    [InlineData(3600.0, "60:00")]
    [InlineData(6000.0, "100:00")]
    public void DisplayFormat_Duration_TruncatedTotalMinutesAndSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void DisplayFormat_NonInvariantCurrentCulture_FormatsEveryNumberInvariantly()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            // The culture really does format differently, so every assertion below proves invariance.
            Assert.NotEqual(12_345.6.ToString("N1", CultureInfo.InvariantCulture), 12_345.6.ToString("N1", CultureInfo.CurrentCulture));

            DateTimeOffset capturedAt = ControllerHarness.StartTime;
            Assert.Equal(UiText.Calculating, DisplayFormat.Remaining(null));
            Assert.Equal("03:41", DisplayFormat.Remaining(TimeSpan.FromSeconds(221)));
            Assert.Equal(Temperature("48"), DisplayFormat.Temperature(SensorValue.Available(47.5, capturedAt)));
            Assert.Equal(Temperature("0"), DisplayFormat.Temperature(SensorValue.Available(-0.4, capturedAt)));
            Assert.Equal(UiText.NotAvailable, DisplayFormat.Temperature(SensorValue.Unavailable(SensorStatus.Stale, capturedAt)));
            Assert.Equal(Format(UiText.CoverageFormat, "412.6", "200"), DisplayFormat.Coverage(412.6, 200));
            Assert.Equal(Format(UiText.CoverageFormat, "0.0", "200"), DisplayFormat.Coverage(double.NaN, 200));
            Assert.Equal(Format(UiText.CoverageFormat, "0.0", "800"), DisplayFormat.Coverage(-5, 800));
            Assert.Equal("0x0000023A4F7C1D48", DisplayFormat.Hex64(0x23A4F7C1D48));
            Assert.Equal("0xFFFFFFFFFFFFFFFF", DisplayFormat.Hex64(ulong.MaxValue));
            Assert.Equal(UiText.Unknown, DisplayFormat.Speed(null));
            Assert.Equal(UiText.Unknown, DisplayFormat.Speed(0));
            Assert.Equal(Format(UiText.SpeedFormat, "6000"), DisplayFormat.Speed(6000));
            Assert.Equal(UiText.Unknown, DisplayFormat.Capacity(null));
            Assert.Equal(Format(UiText.CapacityFormat, "32.0"), DisplayFormat.Capacity(32 * GiB));
            Assert.Equal(Format(UiText.CapacityFormat, "1.5"), DisplayFormat.Capacity(3 * GiB / 2));
            Assert.Equal(UiText.Unknown, DisplayFormat.Count(null));
            Assert.Equal(UiText.Unknown, DisplayFormat.Count(-1));
            Assert.Equal("4", DisplayFormat.Count(4));
            Assert.Equal("0", DisplayFormat.ErrorTotal(0));
            Assert.Equal("12,345", DisplayFormat.ErrorTotal(12_345));
            Assert.Equal("64 %", DisplayFormat.WholePercent(63.5));
            Assert.Equal("0 %", DisplayFormat.WholePercent(double.PositiveInfinity));
            Assert.Equal(
                Format(UiText.StepProgressFormat, "Read-after-write turnaround", "3", "6"),
                DisplayFormat.StepProgress("Read-after-write turnaround", 2, 6));
            Assert.Equal("1, 5, 17", DisplayFormat.Bits(0x20022));
            Assert.Equal("63", DisplayFormat.Bits(1UL << 63));
            Assert.Equal(UiText.NotYetKnown, DisplayFormat.Bits(0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData(RereadOutcome.RereadMatchesExpected, 0UL, 1UL)]
    [InlineData(RereadOutcome.RereadRepeatsActual, 3UL, 3UL)]
    [InlineData(RereadOutcome.RereadDiffers, 12_345UL, 12_345UL)]
    public void DisplayFormat_FirstError_FillsTemplateWithVirtualAddressWordsAndInference(
        RereadOutcome outcome,
        ulong totalErrors,
        ulong shownCount)
    {
        ErrorRecord record = FakeEngineData.Error(
            1, 0x0000_023A_4F7C_1D48, 17, 1, "Random bank thrash", rereadOutcome: outcome, timeIntoTest: TimeSpan.FromSeconds(72.9));

        string text = DisplayFormat.FirstError(record, totalErrors);

        // A first error is itself one error, so a total that has not arrived yet never reads "0 errors".
        string expected = Format(
            UiText.FirstErrorFormat,
            record.StepName,
            "01:12",
            UiText.ErrorCount(shownCount),
            UiText.VirtualAddressLabel,
            "0x0000023A4F7C1D48",
            DisplayFormat.Hex64(record.Expected),
            DisplayFormat.Hex64(record.Actual),
            DisplayFormat.Hex64(record.Reread),
            RereadOutcomeText.Describe(outcome));
        Assert.Equal(expected, text);
        Assert.Contains(VirtualAddressToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorGroupItem_Group_FormatsAddressCountBitsAndFirstStep()
    {
        var group = new ErrorGroup
        {
            WordAddress = 0x0000_023A_4F7C_1D48,
            Count = 3,
            BitMaskUnion = (1UL << 5) | (1UL << 17),
            FirstStepName = "Random bank thrash",
            FirstTimeIntoTest = TimeSpan.FromSeconds(72.9),
            LastTimeIntoTest = TimeSpan.FromSeconds(80),
        };

        var item = new ErrorGroupItem(group);

        Assert.Same(group, item.Group);
        Assert.Equal(group.WordAddress, item.WordAddress);
        Assert.Equal("0x0000023A4F7C1D48", item.AddressText);
        Assert.Equal(3, item.Count);
        Assert.Equal(UiText.ErrorCount(3), item.CountText);
        Assert.Equal("5, 17", item.BitsText);
        Assert.Equal(group.FirstStepName, item.FirstStepName);
        Assert.Equal("01:12", item.FirstTimeText);
        Assert.Equal(
            Format(UiText.ErrorGroupFormat, UiText.VirtualAddressLabel, item.AddressText, item.CountText, item.BitsText, group.FirstStepName),
            item.Text);

        // The displayed line labels the address as virtual, as the first-error message does.
        Assert.StartsWith(VirtualAddressToken + " 0x0000023A4F7C1D48", item.Text, StringComparison.Ordinal);
        Assert.Equal(item.Text, item.ToString());
    }

    [Fact]
    public void ErrorGroupItem_NullOrEmptyGroup_Throws()
    {
        var empty = new ErrorGroup
        {
            WordAddress = 0x0000_023A_0000_0000,
            Count = 0,
            BitMaskUnion = 1,
            FirstStepName = "High-toggle sweep",
            FirstTimeIntoTest = TimeSpan.Zero,
            LastTimeIntoTest = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentNullException>("group", () => new ErrorGroupItem(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorGroupItem(empty));
    }

    [Theory]
    [MemberData(nameof(AllCauseCategories))]
    public void CauseHintItem_Hint_TextStartsWithHintPrefixAndUsesCategoryTitle(CauseCategory category)
    {
        var hint = new CauseHint(2, category, 0.64, 63.5, Suggestions.For(category), "92 % of errors on bit 17 across 41 addresses");

        var item = new CauseHintItem(hint);

        Assert.Same(hint, item.Hint);
        Assert.Equal(2, item.Rank);
        Assert.Equal(category, item.Category);
        Assert.Equal(63.5, item.LikelihoodPercent);
        Assert.Equal(hint.Suggestion, item.Suggestion);
        Assert.Equal(hint.Evidence, item.Evidence);
        Assert.Equal(Suggestions.Title(category), item.Title);
        Assert.Equal(DisplayFormat.WholePercent(63.5), item.LikelihoodText);
        Assert.StartsWith(UiText.HintPrefix, item.Text, StringComparison.Ordinal);
        Assert.Equal(Format(UiText.CauseHintFormat, UiText.HintPrefix, item.Title, item.LikelihoodText, item.Suggestion), item.Text);
        Assert.Equal(item.Text, item.ToString());
    }

    [Fact]
    public void CauseHintItem_NullHint_Throws()
    {
        Assert.Throws<ArgumentNullException>("hint", () => new CauseHintItem(null!));
    }

    [Fact]
    public void RunStateToBrushConverter_Convert_VerdictStatesGetTheirBrushAndAnythingElseNeutral()
    {
        var converter = new RunStateToBrushConverter();

        Assert.Same(converter.PassedBrush, ConvertState(converter, RunState.Passed));
        Assert.Same(converter.FailedBrush, ConvertState(converter, RunState.Failed));
        Assert.Same(converter.AbortedBrush, ConvertState(converter, RunState.Aborted));
        Assert.All(
            new object?[] { RunState.Idle, RunState.Preparing, RunState.Running, (RunState)99, null, DependencyProperty.UnsetValue, nameof(RunState.Passed), 3 },
            value => Assert.Same(converter.NeutralBrush, ConvertState(converter, value)));

        // The defaults are frozen, so one converter instance is safe to share, and each verdict has its own colour.
        Brush[] verdictBrushes = [converter.PassedBrush, converter.FailedBrush, converter.AbortedBrush];
        Assert.All(verdictBrushes, brush => Assert.True(brush.IsFrozen));
        Assert.Distinct(verdictBrushes.Select(brush => ((SolidColorBrush)brush).Color));
        Assert.Same(Brushes.Transparent, converter.NeutralBrush);
    }

    [Fact]
    public void RunStateToBrushConverter_AssignedBrushes_ReturnedAsGivenAndNullOrConvertBackRejected()
    {
        Brush passed = Brushes.Green;
        Brush failed = Brushes.Red;
        Brush aborted = Brushes.Orange;
        Brush neutral = Brushes.Gray;
        var converter = new RunStateToBrushConverter
        {
            PassedBrush = passed,
            FailedBrush = failed,
            AbortedBrush = aborted,
            NeutralBrush = neutral,
        };

        Assert.Same(passed, ConvertState(converter, RunState.Passed));
        Assert.Same(failed, ConvertState(converter, RunState.Failed));
        Assert.Same(aborted, ConvertState(converter, RunState.Aborted));
        Assert.Same(neutral, ConvertState(converter, RunState.Running));
        Assert.Throws<ArgumentNullException>(() => converter.PassedBrush = null!);
        Assert.Throws<ArgumentNullException>(() => converter.FailedBrush = null!);
        Assert.Throws<ArgumentNullException>(() => converter.AbortedBrush = null!);
        Assert.Throws<ArgumentNullException>(() => converter.NeutralBrush = null!);
        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack(passed, typeof(RunState), null, CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static MainViewModel CreateViewModel(ControllerHarness harness) =>
        new(harness.Controller, harness.StateMachine);

    // Commands run with the harness context installed, so the controller's awaits resume on the "UI thread".
    private static void Execute(ControllerHarness harness, ICommand command)
    {
        _ = harness.OnUiThread(() =>
        {
            command.Execute(null);
            return true;
        });
    }

    // The view-model counterpart of ControllerHarness.StartAccepted: Start goes through StartCommand.
    private static FakeEngineSession StartThroughCommand(ControllerHarness harness, MainViewModel viewModel)
    {
        int sessionsBefore = harness.Engine.Sessions.Count;
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Execute(harness, viewModel.StartCommand);
        harness.RunUntil(() => harness.Engine.Sessions.Count > sessionsBefore
            && harness.Session.IsStarted
            && harness.StateMachine.State == RunState.Preparing);
        return harness.Session;
    }

    private static void PublishAndAwait(ControllerHarness harness, FakeEngineSession session, RunProgress progress)
    {
        session.PublishProgress(progress);
        harness.RunUntil(() => ReferenceEquals(harness.Controller.CurrentProgress, progress));
    }

    // A reading is delivered at the clock's new instant, so a raise at that instant belongs to this reading alone.
    private static void AssertTemperaturesRaisedByNextReading(ControllerHarness harness, PropertyChangeRecorder recorder)
    {
        harness.NextReading();
        DateTimeOffset now = harness.Time.GetUtcNow();
        Assert.True(
            recorder.CountSince(nameof(MainViewModel.RamTemperatureText), now) >= 1,
            "RamTemperatureText was not refreshed by the reading.");
        Assert.True(
            recorder.CountSince(nameof(MainViewModel.CpuTemperatureText), now) >= 1,
            "CpuTemperatureText was not refreshed by the reading.");
    }

    private static void AssertControls(
        MainViewModel viewModel,
        bool canStart,
        bool canStop,
        bool inProgress,
        bool canChangeSettings)
    {
        Assert.Equal(canStart, viewModel.CanStart);
        Assert.Equal(canStart, viewModel.StartCommand.CanExecute(null));
        Assert.Equal(canStop, viewModel.CanStop);
        Assert.Equal(canStop, viewModel.StopCommand.CanExecute(null));
        Assert.Equal(inProgress, viewModel.IsRunInProgress);
        Assert.Equal(canChangeSettings, viewModel.CanChangeSettings);
    }

    private static string StatusLine(MainViewModel viewModel, string verdictOrState) =>
        Format(UiText.StatusLineFormat, viewModel.CurrentStepText, viewModel.RemainingText, viewModel.ErrorCountText, verdictOrState);

    private static string Temperature(string degrees) => Format(UiText.TemperatureFormat, degrees);

    private static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, template, arguments);

    private static object ConvertState(RunStateToBrushConverter converter, object? value) =>
        converter.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture);

    private static string[] DistinctConsecutive(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (string value in values)
        {
            if (result.Count == 0 || !string.Equals(result[^1], value, StringComparison.Ordinal))
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }

    /// <summary>The command and settings state captured as the view model finished reacting to one transition.</summary>
    private sealed record TransitionSnapshot(
        RunState State,
        int StartChanges,
        int StopChanges,
        bool CanStart,
        bool CanStop,
        bool IsRunInProgress,
        bool CanChangeSettings);

    /// <summary>One raised <see cref="INotifyPropertyChanged.PropertyChanged"/>, stamped with the simulated time.</summary>
    private sealed record PropertyChange(string Name, DateTimeOffset At, object? Value);

    /// <summary>
    /// Records every property change of a source with the simulated time it was raised at and the property's value
    /// at that moment, so tests can assert both update rates and the sequence of displayed values.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: the view model raises its changes on the harness context, which only the test thread pumps.
    /// </remarks>
    private sealed class PropertyChangeRecorder : IDisposable
    {
        private readonly INotifyPropertyChanged _source;
        private readonly TimeProvider _time;
        private readonly List<PropertyChange> _changes = new();

        public PropertyChangeRecorder(INotifyPropertyChanged source, TimeProvider time)
        {
            _source = source;
            _time = time;
            _source.PropertyChanged += OnPropertyChanged;
        }

        public IReadOnlyList<PropertyChange> Changes => _changes;

        public int RaisedCount(string propertyName) => _changes.Count(change => change.Name == propertyName);

        // Raises at or after from.
        public int CountSince(string propertyName, DateTimeOffset from) =>
            _changes.Count(change => change.Name == propertyName && change.At >= from);

        // Raises in the half-open window [from, until).
        public int CountBetween(string propertyName, DateTimeOffset from, DateTimeOffset until) =>
            _changes.Count(change => change.Name == propertyName && change.At >= from && change.At < until);

        public IReadOnlyList<object?> ValuesOf(string propertyName) =>
            _changes.Where(change => change.Name == propertyName).Select(change => change.Value).ToArray();

        public void Dispose() => _source.PropertyChanged -= OnPropertyChanged;

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            string name = e.PropertyName ?? string.Empty;
            object? value = name.Length > 0 && sender is not null
                ? sender.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(sender)
                : null;
            _changes.Add(new PropertyChange(name, _time.GetUtcNow(), value));
        }
    }
}

