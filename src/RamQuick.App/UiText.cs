using System.Globalization;
using RamQuick.Plans;
using RamQuick.Sensors;

namespace RamQuick.App;

/// <summary>
/// Every user-visible English string of the application: labels, automation names, placeholders,
/// notices, refusals, result reasons, headers, dialog texts and the composite-format templates the
/// view model fills in.
/// </summary>
/// <remarks>
/// <para>
/// The UI is English only, so the strings are constants rather than resources. XAML reads them with
/// <c>{x:Static}</c>, and tests assert displayed messages against these members, so each wording
/// exists exactly once. The members format numbers only where a message embeds one, always with
/// <see cref="CultureInfo.InvariantCulture"/>. Value formatting (durations, temperatures, coverage,
/// hexadecimal words, speeds and capacities) belongs to <c>DisplayFormat</c>, which fills the
/// templates here. Re-read inferences, cause titles and suggestions, and the PASS/FAIL/ABORTED
/// verdict words live with their owners in RamQuick.Core and RamQuick.Analysis.
/// </para>
/// <para>
/// Wording rules: a cause hint is never stated as certain, so hint texts say "Probable", "Hint:"
/// or "likely"; and an address is always called a "virtual address", because no documented
/// user-mode API yields physical addresses.
/// </para>
/// </remarks>
public static class UiText
{
    // Templates of the helper messages below. They are private because callers use the helpers,
    // which apply the singular forms and the invariant number formats.
    private const string OneErrorFormat = "{0:N0} error";
    private const string ManyErrorsFormat = "{0:N0} errors";
    private const string OneErrorBeforeStopFormat = "{0:N0} error was detected before the test stopped";
    private const string ManyErrorsBeforeStopFormat = "{0:N0} errors were detected before the test stopped";
    private const string OneErrorFoundFormat = "{0:N0} error was found.";
    private const string ManyErrorsFoundFormat = "{0:N0} errors were found.";
    private const string ThermalStopFormat = "Stopped for safety: {0} reached {1}";
    private const string AffinityFailedFormat = "Could not pin test threads to every logical processor (Windows error {0})";
    private const string JournalWriteFailedFormat = "Progress journal could not be written: {0}";
    private const string ResultsNotSavedFormat = "Results could not be saved: {0}";
    private const string EngineErrorFormat = "Engine error: {0}";
    private const string HostFaultFormat = "Stopped because of an internal error in RAMQuick: {0}";
    private const string EventLogNotSavedFormat = "Event log could not be written: {0}";
    private const string ResultsSavedWithoutEventLogFormat = "Results were saved, but the event log is incomplete: {0}";
    private const string PreviousRunUnfinishedFormat =
        "The previous test did not finish at step {0} of {1} ({2}), which likely indicates severe instability.";

    // Shortest round-trip form: an integral limit such as 95.0 prints as "95", and a retuned
    // fractional limit keeps its exact digits instead of being rounded to a misleading value.
    private const string LimitNumberFormat = "G";

    /// <summary>
    /// The product name, used as the window title and as the title of informational dialogs.
    /// </summary>
    public const string AppTitle = "RAMQuick";

    /// <summary>
    /// The test type selector's entry for <see cref="TestType.Quick"/>.
    /// </summary>
    public const string TestTypeQuick = "Quick (~5 min)";

    /// <summary>
    /// The test type selector's entry for <see cref="TestType.Standard"/>.
    /// </summary>
    public const string TestTypeStandard = "Standard (~20 min)";

    /// <summary>
    /// The test type selector's entry for <see cref="TestType.Exhaustive"/>.
    /// </summary>
    public const string TestTypeExhaustive = "Exhaustive (~60 min)";

    /// <summary>
    /// The content of the Stop on error checkbox.
    /// </summary>
    public const string StopOnErrorLabel = "Stop on error";

    /// <summary>
    /// The content of the Start/Stop toggle button while no run is in progress.
    /// </summary>
    public const string StartLabel = "Start";

    /// <summary>
    /// The content of the Start/Stop toggle button while a run is preparing or running.
    /// </summary>
    public const string StopLabel = "Stop";

    /// <summary>
    /// The automation name of the test type selector.
    /// </summary>
    public const string TestTypeAutomationName = "Test type";

    /// <summary>
    /// The automation name of the Stop on error checkbox.
    /// </summary>
    public const string StopOnErrorAutomationName = "Stop on error";

    /// <summary>
    /// The automation name of the Start/Stop toggle button while it starts a test.
    /// </summary>
    public const string StartAutomationName = "Start test";

    /// <summary>
    /// The automation name of the Start/Stop toggle button while it stops a test.
    /// </summary>
    public const string StopAutomationName = "Stop test";

    /// <summary>
    /// The automation name of the live readings region (temperatures, step, times, coverage, errors).
    /// </summary>
    public const string LiveReadingsAutomationName = "Live readings";

    /// <summary>
    /// The automation name of the detected-memory summary region.
    /// </summary>
    public const string MemorySummaryAutomationName = "Detected memory";

    /// <summary>
    /// The automation name of the notice line.
    /// </summary>
    public const string NoticeAutomationName = "Notice";

    /// <summary>
    /// The automation name of the result panel (verdict, reason and first-error message).
    /// </summary>
    public const string ResultAutomationName = "Result";

    /// <summary>
    /// The automation name of the scrolling detail area below the result header: the cause hints and the
    /// grouped error list, preceded in the compact and minimal layouts by the text the fixed rows shorten
    /// or omit. The area is a tab stop so the keyboard can scroll it.
    /// </summary>
    public const string ResultDetailAutomationName = "Result details";

    /// <summary>
    /// The automation name of the cause hint list.
    /// </summary>
    public const string CauseHintsAutomationName = "Probable causes";

    /// <summary>
    /// The automation name of the grouped error list.
    /// </summary>
    public const string ErrorGroupsAutomationName = "Errors by address";

    /// <summary>
    /// The label of the RAM temperature field (the hottest DIMM sensor). Also names the sensor in
    /// <see cref="ThermalStop"/>.
    /// </summary>
    public const string RamTemperatureLabel = "RAM temperature";

    /// <summary>
    /// The label of the CPU temperature field. Also names the sensor in <see cref="ThermalStop"/>.
    /// </summary>
    public const string CpuTemperatureLabel = "CPU temperature";

    /// <summary>
    /// The label of the current step field.
    /// </summary>
    public const string CurrentStepLabel = "Current step";

    /// <summary>
    /// The label of the elapsed time field.
    /// </summary>
    public const string ElapsedLabel = "Elapsed";

    /// <summary>
    /// The label of the time remaining field.
    /// </summary>
    public const string RemainingLabel = "Time remaining";

    /// <summary>
    /// The label of the coverage field.
    /// </summary>
    public const string CoverageLabel = "Coverage";

    /// <summary>
    /// The label of the running error count field.
    /// </summary>
    public const string ErrorsLabel = "Errors";

    /// <summary>
    /// The label of the installed memory capacity in the memory summary.
    /// </summary>
    public const string InstalledLabel = "Total memory";

    /// <summary>
    /// The label of the tested capacity, the memory the run could lock, in the memory summary.
    /// </summary>
    public const string TestedLabel = "Tested capacity";

    /// <summary>
    /// The label of the configured memory speed in the memory summary.
    /// </summary>
    public const string SpeedLabel = "Speed";

    /// <summary>
    /// The label of the memory channel count in the memory summary.
    /// </summary>
    public const string ChannelsLabel = "Channels";

    /// <summary>
    /// The label of the DIMM count in the memory summary.
    /// </summary>
    public const string DimmsLabel = "DIMMs";

    /// <summary>
    /// Shown for a temperature whose reading is missing, failed, stale or hung.
    /// </summary>
    public const string NotAvailable = "N/A";

    /// <summary>
    /// Shown for a value not known yet, such as the tested capacity before memory is prepared.
    /// </summary>
    public const string NotYetKnown = "—";

    /// <summary>
    /// Shown for a memory summary value the firmware tables do not provide, such as the speed or
    /// the channel count.
    /// </summary>
    public const string Unknown = "Unknown";

    /// <summary>
    /// Shown as the time remaining until the first pass of step 1 has completed.
    /// </summary>
    public const string Calculating = "calculating…";

    /// <summary>
    /// Shown as the current step while memory is being allocated and locked.
    /// </summary>
    public const string PreparingMemory = "Preparing memory…";

    /// <summary>
    /// Shown as the current step after a run has ended, until the engine reports that its memory
    /// has been released.
    /// </summary>
    public const string ReleasingMemory = "Releasing memory…";

    /// <summary>
    /// Shown as the current step after memory has been released, until the run's summaries are
    /// published.
    /// </summary>
    public const string SavingResults = "Saving results…";

    /// <summary>
    /// The title of the dialog that offers to restart RAMQuick as Administrator.
    /// </summary>
    public const string ElevationPromptTitle = "Administrator rights needed";

    /// <summary>
    /// The question of the dialog that offers to restart RAMQuick as Administrator, shown at launch
    /// and when Start is pressed without elevation.
    /// </summary>
    public const string ElevationPromptMessage =
        "RAMQuick needs Administrator rights to lock memory for testing and to read temperature sensors. Restart as Administrator?";

    /// <summary>
    /// The notice shown while the process is not elevated.
    /// </summary>
    public const string NotElevatedNotice = "Not running as Administrator — press Start to restart as Administrator";

    /// <summary>
    /// The notice shown when only the CPU temperature is unavailable, so only the CPU thermal limit
    /// is inactive.
    /// </summary>
    public const string CpuSensorUnavailableNotice = "CPU temperature unavailable — CPU thermal limit inactive";

    /// <summary>
    /// The notice shown when only the RAM temperature is unavailable, so only the RAM thermal limit
    /// is inactive.
    /// </summary>
    public const string RamSensorUnavailableNotice = "RAM temperature unavailable — RAM thermal limit inactive";

    /// <summary>
    /// The notice shown when both temperatures are unavailable, so no thermal limit is active.
    /// </summary>
    public const string SensorsUnavailableNotice = "Temperature sensors unavailable — thermal safety inactive";

    /// <summary>
    /// The notice shown when the CPU lacks AVX2 and the engine runs its SSE2 kernels.
    /// </summary>
    public const string ReducedInstructionSetNotice = "Reduced instruction set (SSE2): this test runs slower";

    /// <summary>
    /// The notice shown at launch after a previous run's verdict was found without its published
    /// summaries, and the summaries were rebuilt and saved.
    /// </summary>
    public const string RecoveredNotice = "Results of the previous test were recovered and saved.";

    /// <summary>
    /// The message shown when Start is refused because less than 1 GiB can be planned for testing,
    /// and the result reason when locking fails below 1 GiB.
    /// </summary>
    public const string LowMemoryMessage =
        "Not enough free memory to test: at least 1 GB must be lockable. Close other applications and try again.";

    /// <summary>
    /// The message shown when Start is refused because no writable data folder exists, or the
    /// journal's first record cannot be made durable.
    /// </summary>
    public const string DataFolderUnusableMessage =
        "RAMQuick cannot save its progress journal and logs. Move it to a writable folder and try again.";

    /// <summary>
    /// The error filled into <see cref="JournalWriteFailed"/> when a progress journal record was not
    /// confirmed as durable within its 5-second bound. A timed-out write carries no error message of its
    /// own, unlike a write that failed.
    /// </summary>
    public const string JournalAcknowledgmentTimedOut = "a record was not confirmed as saved within 5 seconds";

    /// <summary>
    /// The result reason of a run the user stopped.
    /// </summary>
    public const string StoppedByUser = "Stopped by user";

    /// <summary>
    /// The result reason of a run that completed every step without an error.
    /// </summary>
    public const string PassedReason = "No errors were found.";

    /// <summary>
    /// The result reason of a run that halted at its first confirmed error because Stop on error
    /// was on.
    /// </summary>
    public const string StoppedAtFirstErrorReason = "Stopped at the first confirmed error.";

    /// <summary>
    /// The result reason shown at launch when the previous run's journal ends while it was still
    /// preparing memory, before step 1 started. <see cref="PreviousRunUnfinished"/> covers a run
    /// that ended inside a step.
    /// </summary>
    public const string PreviousRunUnfinishedPreparing =
        "The previous test did not finish while preparing memory (before step 1), which likely indicates severe instability.";

    /// <summary>
    /// The header of the cause hint list. Every hint is a probable cause, never a diagnosis.
    /// </summary>
    public const string CauseHintsHeader = "Probable cause (hint)";

    /// <summary>
    /// The prefix of every cause hint item, filled into <see cref="CauseHintFormat"/>.
    /// </summary>
    public const string HintPrefix = "Hint:";

    /// <summary>
    /// The header of the grouped error list while every error so far is stored in detail.
    /// </summary>
    public const string ErrorGroupsHeader = "Errors by address";

    /// <summary>
    /// The header of the grouped error list once the 10,000-record detail cap is reached: the groups
    /// are derived from the stored records only, while the error count stays the true total.
    /// </summary>
    public const string ErrorGroupsTruncatedHeader = "Grouped from the first 10,000 errors";

    /// <summary>
    /// The word placed before every displayed address, filled into <see cref="FirstErrorFormat"/> and
    /// <see cref="ErrorGroupFormat"/>. Addresses are virtual because no documented user-mode API
    /// yields physical addresses.
    /// </summary>
    public const string VirtualAddressLabel = "virtual address";

    /// <summary>
    /// The title of the dialog shown before exiting on an unsupported platform.
    /// </summary>
    public const string UnsupportedPlatformTitle = "Unsupported system";

    /// <summary>
    /// The message of the dialog shown before exiting when the operating system is not x64 or is
    /// older than Windows 10 version 1607.
    /// </summary>
    public const string UnsupportedPlatformMessage =
        "RAMQuick supports Windows 10 and Windows 11 on x64 PCs only, so it cannot run on this system.";

    /// <summary>
    /// The message shown by a second instance before it exits.
    /// </summary>
    public const string AlreadyRunningMessage = "RAMQuick is already running";

    /// <summary>
    /// The message shown when the test engine DLL is missing or reports a different API version.
    /// </summary>
    public const string CorruptedInstallationMessage =
        "The RAMQuick test engine could not be loaded. The installation appears to be damaged; extract RAMQuick again.";

    /// <summary>
    /// The title of the dialog that reports an unexpected application error.
    /// </summary>
    public const string UnexpectedErrorTitle = "RAMQuick error";

    /// <summary>
    /// Composite format of the current step field: {0} the step name, {1} its 1-based number,
    /// {2} the step count. Example: "Read-after-write turnaround (3 of 6)".
    /// </summary>
    public const string StepProgressFormat = "{0} ({1} of {2})";

    /// <summary>
    /// Composite format of the coverage field: {0} the coverage percentage, {1} the test type's
    /// target percentage. Example: "412.6 % (target 200 %)".
    /// </summary>
    public const string CoverageFormat = "{0} % (target {1} %)";

    /// <summary>
    /// Composite format of a temperature: {0} the degrees Celsius. Example: "48 °C".
    /// </summary>
    public const string TemperatureFormat = "{0} °C";

    /// <summary>
    /// Composite format of the configured memory speed: {0} the transfer rate. Example: "6000 MT/s".
    /// </summary>
    public const string SpeedFormat = "{0} MT/s";

    /// <summary>
    /// Composite format of a memory capacity: {0} the size in gigabytes. Example: "32 GB".
    /// </summary>
    public const string CapacityFormat = "{0} GB";

    /// <summary>
    /// Composite format of the first-error message: {0} the step name, {1} the time into the test,
    /// {2} the error count (see <see cref="ErrorCount"/>), {3} <see cref="VirtualAddressLabel"/>,
    /// {4} the address, {5} the expected word, {6} the actual word, {7} the re-read word and
    /// {8} the re-read inference. Example: "Step 'Random bank thrash' at 01:12 — 3 errors —
    /// virtual address 0x0000023A4F7C1D48 — expected 0x5A3C…, actual 0x5A3E…, re-read 0x5A3E…
    /// (repeated wrong value: likely write or storage fault)".
    /// </summary>
    public const string FirstErrorFormat = "Step '{0}' at {1} — {2} — {3} {4} — expected {5}, actual {6}, re-read {7} ({8})";

    /// <summary>
    /// Composite format of a cause hint item: {0} <see cref="HintPrefix"/>, {1} the cause title,
    /// {2} the likelihood as formatted by <c>DisplayFormat.WholePercent</c>, {3} the suggestion.
    /// Example: "Hint: Row/bank timings — 64 % — Loosen tRCD…".
    /// </summary>
    /// <remarks>
    /// The template carries no % sign of its own: <c>DisplayFormat.WholePercent</c> already
    /// appends it, and the item shows that same text as its likelihood, so a literal % here would
    /// render "64 % %".
    /// </remarks>
    public const string CauseHintFormat = "{0} {1} — {2} — {3}";

    /// <summary>
    /// Composite format of a grouped error item: {0} <see cref="VirtualAddressLabel"/>, {1} the word
    /// address, {2} the error count (see <see cref="ErrorCount"/>), {3} the flipped bit positions,
    /// {4} the name of the step that first reported the word. Example: "virtual address
    /// 0x0000023A4F7C1D48 — 3 errors — bits 5, 17 — first in 'Random bank thrash'".
    /// </summary>
    public const string ErrorGroupFormat = "{0} {1} — {2} — bits {3} — first in '{4}'";

    /// <summary>
    /// Composite format of the one-line status of the minimal window layout: {0} the current step,
    /// {1} the time remaining, {2} the true error total as a number, {3} the verdict.
    /// </summary>
    /// <remarks>
    /// "errors" is a fixed field label in this compact line, not a counted noun, so callers pass the
    /// bare number and one error reads "1 errors". Messages that count errors use
    /// <see cref="ErrorCount"/> instead.
    /// </remarks>
    public const string StatusLineFormat = "{0} · {1} · {2} errors · {3}";

    /// <summary>
    /// Composite format of the automation name of a live reading or memory summary value: {0} the
    /// field's label, {1} its current value. Example: "RAM temperature: 48 °C".
    /// </summary>
    /// <remarks>
    /// A value is shown in its own text element beside its label, and a text element's automation
    /// name is its text, so without this name a screen reader would announce a bare "N/A" or "03:12".
    /// </remarks>
    public const string LabeledValueAutomationFormat = "{0}: {1}";

    /// <summary>
    /// Composite format of the result's automation name, which a screen reader announces when a
    /// verdict appears or its reason changes: {0} the verdict, {1} its reason. Example:
    /// "ABORTED: Stopped by user".
    /// </summary>
    /// <remarks>
    /// One name carries both, so the verdict and its reason are announced together rather than as
    /// two announcements that interrupt each other.
    /// </remarks>
    public const string ResultAnnouncementFormat = "{0}: {1}";

    /// <summary>
    /// The separator placed between notices when several apply at once.
    /// </summary>
    public const string NoticeSeparator = "  ·  ";

    /// <summary>
    /// Returns the test type selector's entry for a test type.
    /// </summary>
    /// <param name="type">The test type.</param>
    /// <returns>
    /// <see cref="TestTypeQuick"/>, <see cref="TestTypeStandard"/> or <see cref="TestTypeExhaustive"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not a defined <see cref="TestType"/> value.
    /// </exception>
    public static string TestTypeDisplay(TestType type) => type switch
    {
        TestType.Quick => TestTypeQuick,
        TestType.Standard => TestTypeStandard,
        TestType.Exhaustive => TestTypeExhaustive,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The test type is not a defined value."),
    };

    /// <summary>
    /// Returns an error count with its noun, such as "1 error", "3 errors" or "12,345 errors".
    /// </summary>
    /// <param name="count">The number of errors.</param>
    /// <returns>The count with invariant thousands separators, followed by "error" or "errors".</returns>
    public static string ErrorCount(ulong count) =>
        string.Format(CultureInfo.InvariantCulture, count == 1 ? OneErrorFormat : ManyErrorsFormat, count);

    /// <summary>
    /// Returns the line an ABORTED result adds when errors were counted before the run stopped,
    /// such as "3 errors were detected before the test stopped".
    /// </summary>
    /// <param name="count">The true error total at the stop.</param>
    /// <returns>The sentence, in the singular for one error, without a final full stop.</returns>
    public static string ErrorsBeforeStop(ulong count) =>
        string.Format(
            CultureInfo.InvariantCulture,
            count == 1 ? OneErrorBeforeStopFormat : ManyErrorsBeforeStopFormat,
            count);

    /// <summary>
    /// Returns the result reason of a run that completed with errors, such as "3 errors were found.".
    /// </summary>
    /// <param name="count">The true error total.</param>
    /// <returns>The sentence, in the singular for one error.</returns>
    public static string FailedReason(ulong count) =>
        string.Format(CultureInfo.InvariantCulture, count == 1 ? OneErrorFoundFormat : ManyErrorsFoundFormat, count);

    /// <summary>
    /// Returns the result reason of a run stopped by a thermal limit, such as
    /// "Stopped for safety: CPU temperature reached 95 °C".
    /// </summary>
    /// <param name="sensor">The sensor whose reading reached its limit.</param>
    /// <param name="limitCelsius">
    /// The limit that was reached, in degrees Celsius. An integral limit is shown without decimals.
    /// </param>
    /// <returns>The reason, naming the sensor and its limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sensor"/> is not a defined <see cref="ThermalSensor"/> value, or
    /// <paramref name="limitCelsius"/> is NaN or infinite.
    /// </exception>
    public static string ThermalStop(ThermalSensor sensor, double limitCelsius)
    {
        string sensorLabel = sensor switch
        {
            ThermalSensor.Cpu => CpuTemperatureLabel,
            ThermalSensor.Ram => RamTemperatureLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, "The thermal sensor is not a defined value."),
        };

        if (!double.IsFinite(limitCelsius))
        {
            throw new ArgumentOutOfRangeException(nameof(limitCelsius), limitCelsius, "The thermal limit must be a finite number.");
        }

        string limit = string.Format(
            CultureInfo.InvariantCulture,
            TemperatureFormat,
            limitCelsius.ToString(LimitNumberFormat, CultureInfo.InvariantCulture));
        return string.Format(CultureInfo.InvariantCulture, ThermalStopFormat, sensorLabel, limit);
    }

    /// <summary>
    /// Returns the result reason of a run whose test threads could not all be pinned, such as
    /// "Could not pin test threads to every logical processor (Windows error 87)".
    /// </summary>
    /// <param name="windowsError">The Windows error code of the failing affinity call.</param>
    /// <returns>The reason, with the error code in decimal.</returns>
    public static string AffinityFailed(uint windowsError) =>
        string.Format(CultureInfo.InvariantCulture, AffinityFailedFormat, windowsError);

    /// <summary>
    /// Returns the result reason of a run stopped because its progress journal could not be written.
    /// </summary>
    /// <param name="error">The description of the write failure, shown verbatim.</param>
    /// <returns>"Progress journal could not be written: " followed by <paramref name="error"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string JournalWriteFailed(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Format(CultureInfo.InvariantCulture, JournalWriteFailedFormat, error);
    }

    /// <summary>
    /// Returns the notice shown when a run's summaries could not be published after its verdict.
    /// </summary>
    /// <param name="error">The description of the publication failure, shown verbatim.</param>
    /// <returns>"Results could not be saved: " followed by <paramref name="error"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string ResultsNotSaved(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Format(CultureInfo.InvariantCulture, ResultsNotSavedFormat, error);
    }

    /// <summary>
    /// Returns the result reason of a run ended by an engine fault.
    /// </summary>
    /// <param name="message">The engine's diagnostic message, shown verbatim.</param>
    /// <returns>"Engine error: " followed by <paramref name="message"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public static string EngineError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return string.Format(CultureInfo.InvariantCulture, EngineErrorFormat, message);
    }

    /// <summary>
    /// Returns the result reason of a run stopped as a host fault for a cause inside the application rather
    /// than the engine, such as an exception in one of its callbacks or event handlers.
    /// <see cref="EngineError"/> stays reserved for engine faults, and <see cref="JournalWriteFailed"/> names
    /// a journal write failure.
    /// </summary>
    /// <param name="message">The description of the fault, shown verbatim.</param>
    /// <returns>"Stopped because of an internal error in RAMQuick: " followed by <paramref name="message"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public static string HostFault(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return string.Format(CultureInfo.InvariantCulture, HostFaultFormat, message);
    }

    /// <summary>
    /// Returns the notice shown while a run continues after its <c>.log</c> event log could not be written.
    /// </summary>
    /// <param name="error">The description of the write failure, shown verbatim.</param>
    /// <returns>"Event log could not be written: " followed by <paramref name="error"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string EventLogNotSaved(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Format(CultureInfo.InvariantCulture, EventLogNotSavedFormat, error);
    }

    /// <summary>
    /// Returns the notice shown when a run's summaries were published but its <c>.log</c> event log lost
    /// events, so its results are saved only in part.
    /// </summary>
    /// <param name="error">The description of the event log's write failure, shown verbatim.</param>
    /// <returns>"Results were saved, but the event log is incomplete: " followed by <paramref name="error"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string ResultsSavedWithoutEventLog(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Format(CultureInfo.InvariantCulture, ResultsSavedWithoutEventLogFormat, error);
    }

    /// <summary>
    /// Returns the result reason shown at launch when the previous run's journal ends inside a step,
    /// such as "The previous test did not finish at step 2 of 3 (Random bank thrash), which likely
    /// indicates severe instability.". <see cref="PreviousRunUnfinishedPreparing"/> covers a run
    /// that ended before step 1.
    /// </summary>
    /// <param name="stepNumber">The 1-based number of the step that did not finish.</param>
    /// <param name="stepCount">The number of steps in the run's plan.</param>
    /// <param name="stepName">The name of the step that did not finish, shown verbatim.</param>
    /// <returns>The reason, naming the step by number, count and name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stepName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stepNumber"/> is less than 1 or greater than <paramref name="stepCount"/>.
    /// </exception>
    public static string PreviousRunUnfinished(int stepNumber, int stepCount, string stepName)
    {
        ArgumentNullException.ThrowIfNull(stepName);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stepNumber, stepCount);
        return string.Format(CultureInfo.InvariantCulture, PreviousRunUnfinishedFormat, stepNumber, stepCount, stepName);
    }
}
