using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RamQuick.Analysis;
using RamQuick.App.Services;
using RamQuick.Core;
using RamQuick.Plans;
using RamQuick.Sensors;

namespace RamQuick.App.ViewModels;

/// <summary>
/// One entry of the test type selector: the test type it selects and the text it shows.
/// </summary>
/// <param name="Value">The test type the entry selects; the selector's <c>SelectedValuePath</c>.</param>
/// <param name="Text">The entry's display text, such as "Quick (~5 min)"; the selector's <c>DisplayMemberPath</c>.</param>
public sealed record TestTypeOption(TestType Value, string Text)
{
    /// <summary>
    /// Returns <see cref="Text"/>, which also serves as the entry's automation name, the selector's selected-item
    /// name, and its debugger display.
    /// </summary>
    /// <returns>The entry's display text.</returns>
    public override string ToString() => Text;
}

/// <summary>
/// The single view model of the main window and its only <c>DataContext</c>: the three controls (test type
/// selector, Stop on error, Start/Stop toggle), the live readings, the detected-memory summary, the notice line
/// and the result panel with its cause hints and grouped error list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Projection only.</b> Every displayed value is computed on read from the <see cref="RunController"/> and the
/// application's single <see cref="RunStateMachine"/>; nothing is cached except the two item lists, which are
/// rebuilt when the controller publishes new hints or groups. <see cref="State"/> delegates to the state machine,
/// so the view model never holds a copy of the run state. Wording comes from <see cref="UiText"/> and every number
/// is formatted by <see cref="DisplayFormat"/> with the invariant culture.
/// </para>
/// <para>
/// <b>Threading.</b> The controller raises its property changes and the state machine its transitions on the UI
/// thread, so this class needs no dispatcher, starts no timer or thread and performs no I/O. Sensor readings reach
/// it only through <see cref="RunController.LatestReadings"/>, the same snapshot the thermal limits act on.
/// </para>
/// <para>
/// <b>Update rate.</b> Every progress tick (at most 4 Hz) and every sensor reading (1 Hz) raises
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> for every live field, even when its text is unchanged, so
/// each live field is refreshed at least once a second while a run is in progress.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The largest number of error groups the result panel lists: the 100 largest, which are exactly the
    /// controller's groups, since it holds only <see cref="RunController.MaxErrorGroups"/> of them, largest first.
    /// </summary>
    public const int MaxDisplayedErrorGroups = RunController.MaxErrorGroups;

    private static readonly IReadOnlyList<TestTypeOption> _testTypeOptions = Array.AsReadOnly(
        Enum.GetValues<TestType>().Select(type => new TestTypeOption(type, UiText.TestTypeDisplay(type))).ToArray());

    // Every field refreshed on each progress tick and sensor reading, whether or not its text changed.
    private static readonly string[] _liveFieldNames =
    [
        nameof(RamTemperatureText),
        nameof(CpuTemperatureText),
        nameof(CurrentStepText),
        nameof(ElapsedText),
        nameof(RemainingText),
        nameof(CoverageText),
        nameof(ErrorCountText),
        nameof(StatusLineText),
    ];

    private static readonly string[] _noticeFieldNames =
    [
        nameof(NoticeText),
        nameof(HasNotice),
    ];

    private static readonly string[] _memorySummaryFieldNames =
    [
        nameof(InstalledText),
        nameof(SpeedText),
        nameof(ChannelsText),
        nameof(DimmsText),
    ];

    private static readonly string[] _stateFieldNames =
    [
        nameof(State),
        nameof(IsRunInProgress),
        nameof(CanStart),
        nameof(CanStop),
        nameof(CanChangeSettings),
        nameof(VerdictText),
        nameof(HasResult),
        nameof(ResultReasonText),
        nameof(ResultAnnouncementText),
        nameof(HasFirstError),
        nameof(FirstErrorText),
        nameof(HasCauseHints),
        nameof(HasErrorGroups),
    ];

    private static readonly string[] _totalErrorsFieldNames =
    [
        nameof(ErrorCountText),
        nameof(FirstErrorText),
        nameof(HasFirstError),
        nameof(HasErrorGroups),
        nameof(CoverageText),
        nameof(StatusLineText),
    ];

    private readonly RunController _controller;
    private readonly RunStateMachine _stateMachine;

    private IReadOnlyList<CauseHintItem> _causeHints = Array.Empty<CauseHintItem>();
    private IReadOnlyList<ErrorGroupItem> _errorGroups = Array.Empty<ErrorGroupItem>();

    // The controller's group list the displayed items were built from. The controller assigns a new list instance
    // whenever its groups change, so an unchanged reference means the items are current.
    private IReadOnlyList<ErrorGroup>? _errorGroupsSource;
    private bool _disposed;

    /// <summary>
    /// The test type the next run uses, bound to the test type selector. <see cref="TestType.Quick"/> at launch.
    /// The selector is enabled only while <see cref="CanChangeSettings"/> is <see langword="true"/>.
    /// </summary>
    [ObservableProperty]
    private TestType _selectedTestType = TestType.Quick;

    /// <summary>
    /// Whether the next run stops at its first confirmed error, bound to the Stop on error checkbox. Checked at
    /// launch. The checkbox is enabled only while <see cref="CanChangeSettings"/> is <see langword="true"/>.
    /// </summary>
    [ObservableProperty]
    private bool _stopOnError = true;

    /// <summary>
    /// Initializes the main view model over the run controller and the application's single run state machine, and
    /// subscribes to both until <see cref="Dispose"/>.
    /// </summary>
    /// <param name="controller">The run controller whose observable state the window shows.</param>
    /// <param name="stateMachine">
    /// The application's single run state machine: the instance <paramref name="controller"/> moves, exposed as its
    /// <see cref="RunController.StateMachine"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="stateMachine"/> is not the instance <paramref name="controller"/> moves.
    /// </exception>
    public MainViewModel(RunController controller, RunStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(stateMachine);

        // Every component reads the run state from one state machine; a second instance would never move, and the
        // window would show Idle for the whole run.
        if (!ReferenceEquals(controller.StateMachine, stateMachine))
        {
            throw new ArgumentException(
                "The view model must read the same run state machine that the run controller moves.",
                nameof(stateMachine));
        }

        _controller = controller;
        _stateMachine = stateMachine;

        RefreshCauseHints();
        RefreshErrorGroups();

        _controller.PropertyChanged += OnControllerPropertyChanged;
        _stateMachine.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Gets the entries of the test type selector, in <see cref="TestType"/> order: "Quick (~5 min)",
    /// "Standard (~20 min)" and "Exhaustive (~60 min)".
    /// </summary>
    public IReadOnlyList<TestTypeOption> TestTypeOptions => _testTypeOptions;

    /// <summary>
    /// Gets the current run state, read from the single <see cref="RunStateMachine"/> on every access. The verdict
    /// badge binds it through the <c>RunStateToBrush</c> converter.
    /// </summary>
    public RunState State => _stateMachine.State;

    /// <summary>
    /// Gets a value indicating whether a run is preparing or running. The Start/Stop toggle's style trigger binds
    /// it: <see langword="true"/> shows "Stop" bound to <see cref="StopCommand"/>, <see langword="false"/> shows
    /// "Start" bound to <see cref="StartCommand"/>.
    /// </summary>
    public bool IsRunInProgress => _stateMachine.IsRunInProgress;

    /// <summary>
    /// Gets a value indicating whether <see cref="StartCommand"/> can execute: no run is in progress and the
    /// controller can accept a Start, which it cannot until launch recovery has finished and the previous run has
    /// released its memory and published its summaries.
    /// </summary>
    /// <remarks>
    /// Start stays enabled while the process is not elevated: the controller then shows the offer to restart as
    /// Administrator instead of starting, so the user always has a route to elevation without a fourth control.
    /// </remarks>
    public bool CanStart => !IsRunInProgress && _controller.CanStart;

    /// <summary>
    /// Gets a value indicating whether <see cref="StopCommand"/> can execute: the run is preparing or running.
    /// </summary>
    public bool CanStop => State is RunState.Preparing or RunState.Running;

    /// <summary>
    /// Gets a value indicating whether the test type selector and the Stop on error checkbox are enabled. They stay
    /// locked for the whole run and until the run has released its memory and published its summaries.
    /// </summary>
    public bool CanChangeSettings => !_stateMachine.IsRunInProgress && _controller.CanStart;

    /// <summary>
    /// Gets the RAM temperature, the hottest DIMM sensor, such as "48 °C"; "N/A" when the reading is missing,
    /// failed, stale or hung. Always shown, refreshed with every 1 Hz reading.
    /// </summary>
    public string RamTemperatureText => DisplayFormat.Temperature(_controller.LatestReadings.Ram);

    /// <summary>
    /// Gets the CPU temperature, such as "71 °C"; "N/A" when the reading is missing, failed, stale or hung. Always
    /// shown, refreshed with every 1 Hz reading.
    /// </summary>
    public string CpuTemperatureText => DisplayFormat.Temperature(_controller.LatestReadings.Cpu);

    /// <summary>
    /// Gets the current step field: "Releasing memory…" then "Saving results…" after a run's verdict until Start is
    /// possible again; "Preparing memory…" in Preparing; the step name with its number and the step count, such as
    /// "Read-after-write turnaround (3 of 6)", in Running; "—" otherwise.
    /// </summary>
    public string CurrentStepText => _controller.PostRunActivity switch
    {
        PostRunActivity.ReleasingMemory => UiText.ReleasingMemory,
        PostRunActivity.SavingResults => UiText.SavingResults,
        _ => State switch
        {
            RunState.Preparing => UiText.PreparingMemory,
            RunState.Running => RunningStepText(),
            _ => UiText.NotYetKnown,
        },
    };

    /// <summary>
    /// Gets the time elapsed since the run entered Running, as "mm:ss": "00:00" before the first progress tick,
    /// and "—" in every state other than Running.
    /// </summary>
    public string ElapsedText => State == RunState.Running
        ? DisplayFormat.Duration(_controller.CurrentProgress?.Elapsed ?? TimeSpan.Zero)
        : UiText.NotYetKnown;

    /// <summary>
    /// Gets the engine's estimate of the time remaining, as "mm:ss", or "calculating…" until step 1 has completed
    /// its first pass; "—" in every state other than Running. The estimate keeps counting down while errors accrue
    /// with Stop on error off.
    /// </summary>
    public string RemainingText => State == RunState.Running
        ? DisplayFormat.Remaining(_controller.CurrentProgress?.Remaining)
        : UiText.NotYetKnown;

    /// <summary>
    /// Gets the coverage reached with the test type's target, such as "412.6 % (target 200 %)", in Running and in
    /// the terminal state of a run of this session; "—" otherwise.
    /// </summary>
    public string CoverageText => RunFiguresPlan is { } plan
        ? DisplayFormat.Coverage(_controller.CurrentProgress?.CoveragePercent ?? 0, plan.TargetCoveragePercent)
        : UiText.NotYetKnown;

    /// <summary>
    /// Gets the true error total from the engine's counters, such as "12,345", in Running and in the terminal state
    /// of a run of this session; "—" otherwise. It keeps rising after the 10,000-record detail cap is reached.
    /// </summary>
    public string ErrorCountText => RunFiguresPlan is not null
        ? DisplayFormat.ErrorTotal(_controller.TotalErrors)
        : UiText.NotYetKnown;

    /// <summary>
    /// Gets the installed memory capacity from the firmware tables, such as "32.0 GB", or the total physical memory
    /// the engine reports when the firmware tables give none; "Unknown" when neither is known.
    /// </summary>
    public string InstalledText =>
        DisplayFormat.Capacity(_controller.Inventory.InstalledBytes ?? _controller.System?.TotalPhysicalBytes);

    /// <summary>
    /// Gets the capacity under test, the memory the run could lock, such as "28.0 GB": "—" until the engine reports
    /// Prepared, and the shrunken size when locking had to shrink the region.
    /// </summary>
    public string TestedText => _controller.TestedBytes is { } testedBytes
        ? DisplayFormat.Capacity(testedBytes)
        : UiText.NotYetKnown;

    /// <summary>
    /// Gets the configured memory speed, such as "6000 MT/s", or "Unknown" when the firmware tables do not report it.
    /// </summary>
    public string SpeedText => DisplayFormat.Speed(_controller.Inventory.SpeedMTs);

    /// <summary>
    /// Gets the number of populated memory channels, such as "2", or "Unknown" when the module locators cannot be
    /// parsed.
    /// </summary>
    public string ChannelsText => DisplayFormat.Count(_controller.Inventory.ChannelCount);

    /// <summary>
    /// Gets the number of populated DIMMs, such as "4", or "Unknown" when the firmware tables list none.
    /// </summary>
    public string DimmsText =>
        DisplayFormat.Count(_controller.Inventory.DimmCount > 0 ? _controller.Inventory.DimmCount : null);

    /// <summary>
    /// Gets the notice line: every notice that applies, in a fixed order and joined by
    /// <see cref="UiText.NoticeSeparator"/>; empty when none applies. The order is the controller's notice (a
    /// refused Start, a recovery outcome or a failure to save results), the not-elevated notice, the reduced
    /// instruction set notice and the sensor availability notice, which names the thermal limit that is inactive.
    /// </summary>
    public string NoticeText
    {
        get
        {
            var parts = new List<string>(4);
            if (_controller.NoticeMessage is { Length: > 0 } controllerNotice)
            {
                parts.Add(controllerNotice);
            }

            // InitializeAsync publishes the system snapshot before the elevation check, so before the snapshot the
            // elevation flag still holds its initial false and must not be read as "not elevated".
            if (!_controller.IsElevated && _controller.System is not null)
            {
                parts.Add(UiText.NotElevatedNotice);
            }

            if (_controller.IsReducedInstructionSet)
            {
                parts.Add(UiText.ReducedInstructionSetNotice);
            }

            if (SensorNotice(_controller.LatestReadings) is { } sensorNotice)
            {
                parts.Add(sensorNotice);
            }

            return string.Join(UiText.NoticeSeparator, parts);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the notice line is shown: <see cref="NoticeText"/> is not empty.
    /// </summary>
    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>
    /// Gets the verdict badge text: "PASS", "FAIL" or "ABORTED" in a terminal state, empty otherwise.
    /// </summary>
    public string VerdictText => State.ToVerdictText() ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether the result panel shows a verdict: the run state is Passed, Failed or Aborted.
    /// </summary>
    public bool HasResult => State.IsTerminal();

    /// <summary>
    /// Gets the reason shown with the verdict, such as "Stopped by user", a thermal stop, the "previous test did not
    /// finish" message of a run recovered at launch, or an ABORTED reason with the errors detected before the stop;
    /// empty when there is none.
    /// </summary>
    public string ResultReasonText => _controller.ResultMessage ?? string.Empty;

    /// <summary>
    /// Gets the verdict and its reason as the one sentence a screen reader announces for the result, such as
    /// "ABORTED: Stopped by user" (<see cref="UiText.ResultAnnouncementFormat"/>); the verdict alone when it has no
    /// reason, and empty when there is no verdict.
    /// </summary>
    public string ResultAnnouncementText
    {
        get
        {
            string verdict = VerdictText;
            if (verdict.Length == 0)
            {
                return string.Empty;
            }

            string reason = ResultReasonText;
            return reason.Length == 0
                ? verdict
                : string.Format(CultureInfo.InvariantCulture, UiText.ResultAnnouncementFormat, verdict, reason);
        }
    }

    /// <summary>
    /// Gets the first-error message, such as "Step 'Random bank thrash' at 01:12 — 3 errors — virtual address
    /// 0x0000023A4F7C1D48 — expected 0x…, actual 0x…, re-read 0x… (repeated wrong value: likely write or storage
    /// fault)"; empty when the run found no error.
    /// </summary>
    public string FirstErrorText => _controller.FirstError is { } firstError
        ? DisplayFormat.FirstError(firstError, _controller.TotalErrors)
        : string.Empty;

    /// <summary>
    /// Gets a value indicating whether the first-error message is shown: an error was found and the run is running
    /// or failed, or it was aborted after counting errors.
    /// </summary>
    public bool HasFirstError => _controller.FirstError is not null && ShowsErrorDetail;

    /// <summary>
    /// Gets the header of the cause hint list, "Probable cause (hint)": every hint is a probable cause, never a
    /// diagnosis.
    /// </summary>
    public string CauseHintsHeader => UiText.CauseHintsHeader;

    /// <summary>
    /// Gets the highest-ranked cause hints of the last run that ended with errors, at most three, each line starting
    /// with "Hint:". Replaced as a whole when the controller publishes new hints.
    /// </summary>
    public IReadOnlyList<CauseHintItem> CauseHints => _causeHints;

    /// <summary>
    /// Gets a value indicating whether the cause hint list is shown: the run failed or was aborted, and the analyzer
    /// produced at least one hint.
    /// </summary>
    public bool HasCauseHints => State is (RunState.Failed or RunState.Aborted) && _causeHints.Count > 0;

    /// <summary>
    /// Gets the header of the grouped error list: "Errors by address" while fewer than 10,000 records are stored, and
    /// "Grouped from the first 10,000 errors" once the stored records reach the detail cap
    /// (<see cref="RunController.IsStoredDetailCapReached"/>), at exactly 10,000 errors included, because later errors
    /// are no longer stored and the groups describe the stored records only.
    /// </summary>
    public string ErrorGroupsHeader => _controller.IsStoredDetailCapReached
        ? UiText.ErrorGroupsTruncatedHeader
        : UiText.ErrorGroupsHeader;

    /// <summary>
    /// Gets the largest error groups by 8-byte word address, at most <see cref="MaxDisplayedErrorGroups"/>, largest
    /// first. Replaced as a whole when the controller publishes a new group list, which error batches do at most
    /// 10 times a second.
    /// </summary>
    public IReadOnlyList<ErrorGroupItem> ErrorGroups => _errorGroups;

    /// <summary>
    /// Gets a value indicating whether the grouped error list is shown: it has items and the run is running or
    /// failed, or it was aborted after counting errors.
    /// </summary>
    public bool HasErrorGroups => _errorGroups.Count > 0 && ShowsErrorDetail;

    /// <summary>
    /// Gets the one-line status of the minimal window layout: the current step, the time remaining, the error total
    /// with its noun, and the verdict, or the run state while there is no verdict, such as
    /// "Random bank thrash (2 of 6) · 03:41 · 1 error · Running". The error total reads "—" whenever
    /// <see cref="ErrorCountText"/> does, as in "— · — · — · Idle".
    /// </summary>
    public string StatusLineText => string.Format(
        CultureInfo.InvariantCulture,
        UiText.StatusLineFormat,
        CurrentStepText,
        RemainingText,
        RunFiguresPlan is not null ? UiText.ErrorCount(_controller.TotalErrors) : UiText.NotYetKnown,
        VerdictText.Length > 0 ? VerdictText : State.ToString());

    // The plan whose coverage target the run figures show: the current run's in Running, the last run's in a
    // terminal state. A FAIL recovered at launch from an earlier session's journal ran no plan in this session, so
    // its figures read "—" rather than this session's zeros.
    private TestPlan? RunFiguresPlan =>
        State == RunState.Running || State.IsTerminal() ? _controller.ActivePlan : null;

    // Error detail belongs to a running or failed run, and to an aborted run only when it counted errors before the
    // stop; a clean abort shows no stale detail.
    private bool ShowsErrorDetail => State switch
    {
        RunState.Running or RunState.Failed => true,
        RunState.Aborted => _controller.TotalErrors > 0,
        _ => false,
    };

    /// <summary>
    /// Unsubscribes from the run controller and the state machine. Idempotent; the view model raises no further
    /// change notifications afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _stateMachine.StateChanged -= OnStateChanged;
    }


    /// <summary>
    /// Asks the controller to start a run of <see cref="SelectedTestType"/> with <see cref="StopOnError"/>.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="StartResult"/> needs no handling here: a refusal surfaces through the controller's
    /// notice or the elevation dialog it shows itself, and an accepted Start through the state machine.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_disposed)
        {
            return;
        }

        StartResult result = _controller.TryStart(SelectedTestType, StopOnError);
        Debug.Assert(
            result != StartResult.Accepted || !_controller.CanStart,
            "An accepted Start must disable further Starts until the run has released its memory and been published.");
    }

    /// <summary>
    /// Asks the controller to stop the run in progress with the user-stop reason. Never blocks.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _controller.RequestStop();
    }

    private static string? SensorNotice(SensorReadings readings) =>
        (readings.Cpu.IsAvailable, readings.Ram.IsAvailable) switch
        {
            (false, false) => UiText.SensorsUnavailableNotice,
            (false, true) => UiText.CpuSensorUnavailableNotice,
            (true, false) => UiText.RamSensorUnavailableNotice,
            _ => null,
        };

    private string RunningStepText()
    {
        if (_controller.CurrentProgress is { } progress)
        {
            return DisplayFormat.StepProgress(progress.StepName, progress.StepIndex, progress.StepCount);
        }

        // Running is entered on Prepared, before the first progress tick, when the run is about to begin step 1.
        if (_controller.ActivePlan is { Steps.Count: > 0 } plan)
        {
            return DisplayFormat.StepProgress(plan.Steps[0].Name, 0, plan.Steps.Count);
        }

        return UiText.NotYetKnown;
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(RunController.CurrentProgress):
                RaiseAll(_liveFieldNames);
                break;

            case nameof(RunController.LatestReadings):
                RaiseAll(_liveFieldNames);
                RaiseAll(_noticeFieldNames);
                break;

            case nameof(RunController.TotalErrors):
                RaiseAll(_totalErrorsFieldNames);
                break;

            case nameof(RunController.FirstError):
                OnPropertyChanged(nameof(FirstErrorText));
                OnPropertyChanged(nameof(HasFirstError));
                break;

            case nameof(RunController.ErrorGroups):
                if (RefreshErrorGroups())
                {
                    OnPropertyChanged(nameof(ErrorGroups));
                }

                OnPropertyChanged(nameof(HasErrorGroups));
                break;

            case nameof(RunController.IsStoredDetailCapReached):
                OnPropertyChanged(nameof(ErrorGroupsHeader));
                break;

            case nameof(RunController.CauseHints):
                RefreshCauseHints();
                OnPropertyChanged(nameof(CauseHints));
                OnPropertyChanged(nameof(HasCauseHints));
                break;

            case nameof(RunController.ResultMessage):
                OnPropertyChanged(nameof(ResultReasonText));
                OnPropertyChanged(nameof(ResultAnnouncementText));
                break;

            case nameof(RunController.NoticeMessage):
            case nameof(RunController.IsElevated):
            case nameof(RunController.IsReducedInstructionSet):
                RaiseAll(_noticeFieldNames);
                break;

            case nameof(RunController.System):
                RaiseAll(_noticeFieldNames);
                OnPropertyChanged(nameof(InstalledText));
                break;

            case nameof(RunController.Inventory):
                RaiseAll(_memorySummaryFieldNames);
                break;

            case nameof(RunController.TestedBytes):
                OnPropertyChanged(nameof(TestedText));
                break;

            case nameof(RunController.ActivePlan):
                // The status line repeats the step and the error total, so it follows them.
                OnPropertyChanged(nameof(CoverageText));
                OnPropertyChanged(nameof(ErrorCountText));
                OnPropertyChanged(nameof(CurrentStepText));
                OnPropertyChanged(nameof(StatusLineText));
                break;

            case nameof(RunController.PostRunActivity):
                OnPropertyChanged(nameof(CurrentStepText));
                OnPropertyChanged(nameof(StatusLineText));
                break;

            case nameof(RunController.CanStart):
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanChangeSettings));
                NotifyCommands();
                break;

            case null or "":
                // A refresh of every controller property: rebuild the lists and re-read everything.
                RefreshCauseHints();
                RefreshErrorGroups();
                OnPropertyChanged(string.Empty);
                NotifyCommands();
                break;
        }
    }

    private void OnStateChanged(object? sender, RunStateChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Entering Preparing clears the previous result in the controller, which raises its own property changes
        // right after this transition; the fields raised here re-read the state-dependent projections.
        RaiseAll(_stateFieldNames);
        RaiseAll(_liveFieldNames);
        RaiseAll(_noticeFieldNames);
        NotifyCommands();
    }

    private void RaiseAll(string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    // CommunityToolkit commands do not listen to CommandManager.RequerySuggested, so every change that can flip
    // CanStart or CanStop re-evaluates both commands explicitly.
    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCauseHints()
    {
        IReadOnlyList<CauseHint> top = _controller.CauseHints.Top();
        _causeHints = top.Count == 0
            ? Array.Empty<CauseHintItem>()
            : Array.AsReadOnly(top.Select(hint => new CauseHintItem(hint)).ToArray());
    }

    // Returns whether the displayed items were rebuilt, which happens only when the controller published a new list.
    private bool RefreshErrorGroups()
    {
        IReadOnlyList<ErrorGroup> source = _controller.ErrorGroups;
        if (ReferenceEquals(source, _errorGroupsSource))
        {
            return false;
        }

        _errorGroupsSource = source;
        int count = Math.Min(source.Count, MaxDisplayedErrorGroups);
        if (count == 0)
        {
            _errorGroups = Array.Empty<ErrorGroupItem>();
            return true;
        }

        var items = new ErrorGroupItem[count];
        for (int index = 0; index < count; index++)
        {
            items[index] = new ErrorGroupItem(source[index]);
        }

        _errorGroups = Array.AsReadOnly(items);
        return true;
    }
}
