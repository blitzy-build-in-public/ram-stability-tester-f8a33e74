using System.Globalization;
using System.Text;
using RamQuick.Core;

namespace RamQuick.Logging;

/// <summary>
/// Renders a <see cref="RunSummary"/> as the readable text summary, <c>&lt;base&gt;-summary.txt</c>, and reads
/// the run id back from such a text.
/// </summary>
/// <remarks>
/// <para>
/// The text holds the same data as the JSON summary, in sections: the run's identity, result, configuration,
/// times, system, memory, steps, coverage, temperatures, errors, cause hints and, for a run that never reached
/// a verdict, where it stopped.
/// </para>
/// <para>
/// Rendering is pure and deterministic: no clock, environment or current culture is read. Numbers, dates and
/// times use the invariant culture and every line ends with <c>"\r\n"</c>, so equal summaries give
/// byte-identical text on every machine. Free text taken from the summary, such as a step name or an end
/// detail, stays on its own line: control characters in it are written as spaces.
/// </para>
/// <para>
/// The cause hints are always written below <see cref="SummaryCauseHints.HintLabel"/>, as the UI and the JSON
/// summary label them, and every item starts with <c>"Hint:"</c>; no wording states a cause as certain.
/// Addresses are named by their kind, <c>"virtual address"</c> in v1.
/// </para>
/// </remarks>
public static class TextSummaryFormatter
{
    /// <summary>
    /// The start of the header line that carries the run id, <c>"Run ID: "</c>. The rest of that line is the
    /// run GUID in its 32-hex-digit <c>N</c> form.
    /// </summary>
    public const string RunIdLinePrefix = "Run ID: ";

    private const string NewLine = "\r\n";
    private const string Title = "RAMQuick test summary";
    private const string CauseHintsHeading = "Probable cause (hint)";
    private const string NotCaptured = "n/a";
    private const string Unknown = "Unknown";
    private const string NotAvailable = "N/A";
    private const string EmDash = "\u2014";
    private const string Separator = " \u2014 ";
    private const string DegreesCelsius = " \u00B0C";
    private const string ApproximateSuffix = " (approximate, last heartbeat)";
    private const string AbortedVerdict = "ABORTED";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string OneDecimal = "0.0";
    private const string UpToOneDecimal = "0.#";
    private const string UpToThreeDecimals = "0.0##";
    private const string ThreeDecimals = "0.000";
    private const string TwoDecimals = "0.00";
    private const string WholeNumber = "0";
    private const string GroupedDigits = "N0";
    private const int ListedGroupLimit = 100;
    private const int ListedRecordLimit = 100;
    private const double BytesPerGibibyte = 1024.0 * 1024.0 * 1024.0;

    // Bounds the whole-second conversion of a plan target; no plan comes near it.
    private const double MaxRenderedSeconds = 1e12;

    // The steps table: each column's header and whether its cells are right-aligned (the numeric ones).
    private static readonly (string Header, bool RightAligned)[] _stepColumns =
    {
        ("#", true),
        ("Name", false),
        ("Pattern", false),
        ("Order", false),
        ("Threads", false),
        ("Hold s", true),
        ("Planned s", true),
        ("Actual s", true),
        ("Overrun s", true),
        ("Passes", true),
        ("Planned cov %", true),
        ("Cov %", true),
        ("Errors", true),
        ("Completed", false),
    };

    private static CultureInfo Invariant => CultureInfo.InvariantCulture;

    /// <summary>
    /// Renders a summary as readable text.
    /// </summary>
    /// <param name="summary">The summary to render.</param>
    /// <returns>
    /// The text, in English, every line ending with <c>"\r\n"</c>. Equal summaries always give identical
    /// text, whatever the current culture.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is <see langword="null"/>.</exception>
    public static string Format(RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var text = new StringBuilder(8192);
        AppendHeader(text, summary);
        AppendResult(text, summary.Run, summary.Errors);
        AppendConfiguration(text, summary.Run, summary.Plan);
        AppendTimes(text, summary.Run);
        AppendSystem(text, summary.System);
        AppendMemory(text, summary.Memory);
        AppendSteps(text, summary.Steps);
        AppendCoverage(text, summary.Coverage);
        AppendTemperatures(text, summary.Temperatures);
        AppendErrors(text, summary.Errors);
        AppendCauseHints(text, summary.CauseHints);
        if (summary.UnfinishedRun is { } unfinished)
        {
            AppendUnfinishedRun(text, unfinished);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads the run id from a text summary written by <see cref="Format"/>.
    /// </summary>
    /// <param name="text">The text of a <c>-summary.txt</c> file.</param>
    /// <returns>
    /// The run id on the first line that starts with <see cref="RunIdLinePrefix"/>, or
    /// <see langword="null"/> when no line starts with it or the rest of that line is not a 32-hex-digit
    /// <c>N</c>-format GUID.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static Guid? TryParseRunId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            if (line.StartsWith(RunIdLinePrefix, StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line[RunIdLinePrefix.Length..].Trim();
                return Guid.TryParseExact(value, "N", out Guid runId) ? runId : null;
            }
        }

        return null;
    }

    private static void AppendHeader(StringBuilder text, RunSummary summary)
    {
        Line(text, Title);
        Line(text, new string('=', Title.Length));
        Field(text, "App", AppText(summary.App));
        Line(text, RunIdLinePrefix + TextOrNotCaptured(summary.Run.Id));
        Field(text, "Schema version", Integer(summary.SchemaVersion));
        if (summary.Run.Recovered)
        {
            Line(text, "This summary was rebuilt from the progress journal at the next launch.");
        }

        if (summary.Run.Partial)
        {
            Line(text, "This summary is partial: the test did not finish.");
        }
    }

    private static void AppendResult(StringBuilder text, SummaryRun run, SummaryErrors errors)
    {
        Heading(text, "Result");
        Field(text, "Verdict", TextOrNotCaptured(run.Verdict));
        Field(text, "End reason", TextOrNotCaptured(run.EndReason));
        if (!string.IsNullOrWhiteSpace(run.EndDetail))
        {
            Field(text, "Detail", TextOrNotCaptured(run.EndDetail));
        }

        // An ABORTED run still reports the errors it found before the stop.
        if (string.Equals(run.Verdict, AbortedVerdict, StringComparison.Ordinal) && errors.Total > 0)
        {
            Line(
                text,
                errors.Total == 1
                    ? "1 error was detected before the test stopped."
                    : Count(errors.Total) + " errors were detected before the test stopped.");
        }
    }

    private static void AppendConfiguration(StringBuilder text, SummaryRun run, SummaryPlan plan)
    {
        Heading(text, "Configuration");
        Field(text, "Test type", TextOrNotCaptured(run.TestType));
        Field(text, "Stop on error", YesNo(run.StopOnError));
        Field(text, "Target duration", MinutesSeconds(plan.TargetSeconds));
        Field(text, "Target coverage", Percent(plan.TargetCoveragePercent, UpToOneDecimal));
    }

    private static void AppendTimes(StringBuilder text, SummaryRun run)
    {
        // A partial summary's end time and duration come from the last heartbeat. A value it never captured
        // is not approximate but unknown, so only a present value carries the label.
        string approximate = run.Partial ? ApproximateSuffix : string.Empty;

        Heading(text, "Times");
        Field(text, "Start requested", UtcOrNotCaptured(run.StartRequestedUtc));
        Field(text, "Started", UtcOrNotCaptured(run.StartedUtc));
        Field(text, "Ended", run.EndedUtc is { } ended ? Utc(ended) + approximate : NotCaptured);
        Field(text, "Verdict shown", UtcOrNotCaptured(run.VerdictUtc));
        Field(text, "Duration", run.DurationSeconds is { } duration ? Seconds(duration) + approximate : NotCaptured);
        Field(text, "Time to verdict", SecondsOrNotCaptured(run.TimeToVerdictSeconds));
        Field(text, "First error", SecondsOrNotCaptured(run.FirstErrorSeconds));
    }

    private static void AppendSystem(StringBuilder text, SummarySystem system)
    {
        Heading(text, "System");
        Field(text, "OS", TextOrNotCaptured(system.Os));
        Field(text, "CPU", TextOrNotCaptured(system.Cpu));
        Field(text, "Logical processors", IntegerOrNotCaptured(system.LogicalProcessors));
        Field(text, "Processor groups", IntegerOrNotCaptured(system.ProcessorGroups));
        Field(text, "Instruction set", TextOrNotCaptured(system.InstructionSet));
        if (system.ReducedInstructionSet == true)
        {
            Line(text, "Reduced instruction set (SSE2): this test runs slower");
        }
    }

    private static void AppendMemory(StringBuilder text, SummaryMemory memory)
    {
        Heading(text, "Memory");
        Field(text, "Installed", Capacity(memory.InstalledBytes));
        Field(text, "Available at start", Capacity(memory.AvailableBytesAtStart));
        Field(text, "Planned", Capacity(memory.PlannedBytes));
        Field(text, "Tested", Capacity(memory.TestedBytes));
        Field(
            text,
            "Release time",
            memory.ReleaseSeconds is { } release ? Number(release, UpToThreeDecimals) + " s" : "not confirmed");
        Field(text, "Page mode", TextOrNotCaptured(memory.PageMode));
        Field(
            text,
            "Address kind",
            string.IsNullOrWhiteSpace(memory.AddressKind) ? NotCaptured : AddressLabel(memory.AddressKind));
        Field(text, "Type", TextOrNotCaptured(memory.Type));
        Field(text, "Speed", memory.SpeedMTs is { } speed ? Integer(speed) + " MT/s" : Unknown);
        Field(text, "Channels", memory.Channels is { } channels ? Integer(channels) : Unknown);
        Field(text, "DIMMs", memory.Dimms is { } dimms ? Integer(dimms) : Unknown);
    }

    private static void AppendSteps(StringBuilder text, IReadOnlyList<SummaryStep> steps)
    {
        Heading(text, "Steps");
        if (steps.Count == 0)
        {
            Line(text, "No step started.");
            return;
        }

        var rows = new string[steps.Count][];
        for (int i = 0; i < steps.Count; i++)
        {
            SummaryStep step = steps[i];
            rows[i] = new[]
            {
                Integer(step.Index),
                TextOrNotCaptured(step.Name),
                TextOrNotCaptured(step.Pattern),
                TextOrNotCaptured(step.AccessOrder),
                TextOrNotCaptured(step.ThreadMode),
                Number(step.HoldSeconds, UpToOneDecimal),
                Number(step.PlannedSeconds, UpToOneDecimal),
                NumberOrDash(step.ActualSeconds, OneDecimal),
                NumberOrDash(step.OverrunSeconds, OneDecimal),
                step.Passes is { } passes ? Integer(passes) : EmDash,
                Number(step.PlannedCoveragePercent, UpToOneDecimal),
                NumberOrDash(step.CoveragePercent, OneDecimal),
                step.Errors is { } errors ? Count(errors) : EmDash,
                YesNo(step.Completed),
            };
        }

        var widths = new int[_stepColumns.Length];
        var headers = new string[_stepColumns.Length];
        for (int column = 0; column < _stepColumns.Length; column++)
        {
            headers[column] = _stepColumns[column].Header;
            widths[column] = headers[column].Length;
            foreach (string[] row in rows)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        var rules = new string[_stepColumns.Length];
        for (int column = 0; column < rules.Length; column++)
        {
            rules[column] = new string('-', widths[column]);
        }

        AppendStepRow(text, headers, widths);
        AppendStepRow(text, rules, widths);
        foreach (string[] row in rows)
        {
            AppendStepRow(text, row, widths);
        }
    }

    private static void AppendStepRow(StringBuilder text, string[] cells, int[] widths)
    {
        int last = widths.Length - 1;
        for (int column = 0; column <= last; column++)
        {
            if (column > 0)
            {
                text.Append("  ");
            }

            string cell = cells[column];
            int padding = widths[column] - cell.Length;
            if (_stepColumns[column].RightAligned)
            {
                text.Append(' ', padding).Append(cell);
            }
            else
            {
                text.Append(cell);

                // The last column is not padded, so no line ends in spaces.
                if (column < last)
                {
                    text.Append(' ', padding);
                }
            }
        }

        text.Append(NewLine);
    }

    private static void AppendCoverage(StringBuilder text, SummaryCoverage coverage)
    {
        Heading(text, "Coverage");
        Line(
            text,
            Percent(coverage.Percent, OneDecimal)
                + " (target " + Percent(coverage.TargetPercent, UpToOneDecimal) + ")"
                + Separator + "target reached: " + YesNo(coverage.TargetReached));
    }

    private static void AppendTemperatures(StringBuilder text, SummaryTemperatures temperatures)
    {
        Heading(text, "Temperatures");
        Field(text, "CPU max", Temperature(temperatures.CpuMaxC, temperatures.CpuStatus));
        Field(text, "RAM max", Temperature(temperatures.RamMaxC, temperatures.RamStatus));
    }

    private static void AppendErrors(StringBuilder text, SummaryErrors errors)
    {
        Heading(text, "Errors");
        Field(text, "Total errors (true count)", Count(errors.Total));
        if (!errors.DetailAvailable)
        {
            Line(text, "Error detail is not available for this run.");
            return;
        }

        string stored = "Stored detail: " + Count(errors.StoredCount) + " of cap " + Count(errors.StoredCap);
        Line(text, errors.Truncated ? stored + " (truncated: total exceeds stored detail)" : stored);
        Field(text, "Distinct addresses (stored)", Count(errors.StoredDistinctAddresses));

        AppendErrorGroups(text, errors);
        AppendErrorRecords(text, errors.Records);
        if (errors.Groups.Count > 0 || errors.Records.Count > 0)
        {
            Line(text, string.Empty);
            Line(text, "The JSON summary and the .log file hold the full stored detail.");
        }
    }

    private static void AppendErrorGroups(StringBuilder text, SummaryErrors errors)
    {
        IReadOnlyList<SummaryErrorGroup> groups = errors.Groups;
        if (groups.Count == 0)
        {
            return;
        }

        // Once the detail cap is reached the groups describe the stored records only, and the header says so
        // in the UI's words.
        bool capReached = errors.Truncated || (errors.StoredCap > 0 && errors.StoredCount >= errors.StoredCap);
        int cap = errors.StoredCap > 0 ? errors.StoredCap : errors.StoredCount;

        Line(text, string.Empty);
        Line(text, capReached ? "Grouped from the first " + Count(cap) + " errors" : "Errors by address");
        int shown = Math.Min(groups.Count, ListedGroupLimit);
        for (int i = 0; i < shown; i++)
        {
            SummaryErrorGroup group = groups[i];
            Line(
                text,
                "  " + TextOrNotCaptured(group.WordAddress)
                    + "  " + ErrorCount(group.Count)
                    + "  bits " + BitsOfMask(group.BitMaskUnion)
                    + "  first step '" + TextOrNotCaptured(group.FirstStepName) + "'");
        }

        if (groups.Count > shown)
        {
            Line(text, "  Showing the " + Integer(shown) + " largest of " + Count(groups.Count) + " groups.");
        }
    }

    private static void AppendErrorRecords(StringBuilder text, IReadOnlyList<SummaryErrorRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        int shown = Math.Min(records.Count, ListedRecordLimit);
        Line(text, string.Empty);
        Line(
            text,
            records.Count > shown
                ? "Stored error records (first " + Integer(shown) + " of " + Count(records.Count) + ")"
                : "Stored error records (" + Count(records.Count) + ")");
        for (int i = 0; i < shown; i++)
        {
            SummaryErrorRecord record = records[i];
            Line(
                text,
                "  " + Integer(i + 1) + ". Step '" + TextOrNotCaptured(record.StepName) + "'"
                    + " at " + Number(record.TimeIntoTest, ThreeDecimals) + " s"
                    + Separator + AddressLabel(record.AddressKind) + " " + TextOrNotCaptured(record.Address)
                    + Separator + "bits " + BitsOfPositions(record.BitPositions));
            Line(
                text,
                "     expected " + TextOrNotCaptured(record.Expected)
                    + ", actual " + TextOrNotCaptured(record.Actual)
                    + ", re-read " + TextOrNotCaptured(record.Reread)
                    + " (" + RereadText(record.RereadOutcome) + ")");
        }
    }

    private static void AppendCauseHints(StringBuilder text, SummaryCauseHints causeHints)
    {
        // The label is the section's first line, above the heading and every item, as in the UI and the JSON.
        Line(text, string.Empty);
        Line(text, causeHints.Label);
        Line(text, CauseHintsHeading);
        Line(text, new string('-', CauseHintsHeading.Length));
        if (causeHints.Items.Count == 0)
        {
            Line(text, "No cause hints (no error detail).");
            return;
        }

        foreach (SummaryCauseHint item in causeHints.Items)
        {
            Line(
                text,
                "Hint: " + Integer(item.Rank) + ". " + TextOrNotCaptured(item.Category)
                    + Separator + Percent(item.LikelihoodPercent, WholeNumber)
                    + Separator + TextOrNotCaptured(item.Suggestion));
            Line(text, "    Evidence: " + TextOrNotCaptured(item.Evidence));
        }
    }

    private static void AppendUnfinishedRun(StringBuilder text, SummaryUnfinishedRun unfinished)
    {
        string position = unfinished.LastStepIndex is not { } number
            ? "while preparing memory (before step 1)"
            : string.IsNullOrWhiteSpace(unfinished.LastStepName)
                ? "at step " + Integer(number) + " of " + Integer(unfinished.StepCount)
                : "at step " + Integer(number) + " of " + Integer(unfinished.StepCount)
                    + " (" + TextOrNotCaptured(unfinished.LastStepName) + ")";

        Heading(text, "Unfinished run");
        Line(text, "The previous test did not finish " + position + ", which likely indicates severe instability.");
        Field(text, "Last heartbeat", UtcOrNotCaptured(unfinished.LastHeartbeatUtc));
        Field(
            text,
            "Last coverage",
            unfinished.LastCoveragePercent is { } coverage ? Percent(coverage, OneDecimal) : NotCaptured);
    }

    private static void Heading(StringBuilder text, string title)
    {
        text.Append(NewLine);
        Line(text, title);
        Line(text, new string('-', title.Length));
    }

    private static void Line(StringBuilder text, string line) => text.Append(line).Append(NewLine);

    private static void Field(StringBuilder text, string label, string value) =>
        text.Append(label).Append(": ").Append(value).Append(NewLine);

    private static string AppText(SummaryApp app)
    {
        string nameAndVersion = (OneLine(app.Name) + " " + OneLine(app.Version)).Trim();
        return nameAndVersion.Length > 0 ? nameAndVersion : NotCaptured;
    }

    private static string AddressLabel(string addressKind)
    {
        string kind = OneLine(addressKind).Trim();
        return kind.Length == 0 ? "address" : kind.ToLowerInvariant() + " address";
    }

    private static string Capacity(ulong? bytes) =>
        bytes is { } value
            ? Number(value / BytesPerGibibyte, TwoDecimals) + " GiB (" + Count(value) + " bytes)"
            : NotCaptured;

    private static string Temperature(double? celsius, string status)
    {
        string reading = celsius is { } value ? Number(value, OneDecimal) + DegreesCelsius : NotAvailable;
        return reading + " (worst status: " + TextOrNotCaptured(status) + ")";
    }

    private static string ErrorCount(int count) => count == 1 ? "1 error" : Count(count) + " errors";

    // The summary writes a re-read outcome as its RereadOutcome member name; Core words its inference once for
    // the UI, the logs and this text. Any other text is shown as written.
    private static string RereadText(string outcomeName) =>
        Enum.TryParse(outcomeName, ignoreCase: false, out RereadOutcome outcome)
        && Enum.IsDefined(outcome)
        && string.Equals(outcome.ToString(), outcomeName, StringComparison.Ordinal)
            ? RereadOutcomeText.Describe(outcome)
            : TextOrNotCaptured(outcomeName);

    private static string BitsOfMask(string maskText)
    {
        if (!SummaryFormat.TryParseHex64(maskText, out ulong mask))
        {
            return TextOrNotCaptured(maskText);
        }

        var bits = new StringBuilder();
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                AppendListItem(bits, bit);
            }
        }

        return bits.Length > 0 ? bits.ToString() : "none";
    }

    private static string BitsOfPositions(IReadOnlyList<int> positions)
    {
        var bits = new StringBuilder();
        foreach (int position in positions)
        {
            AppendListItem(bits, position);
        }

        return bits.Length > 0 ? bits.ToString() : "none";
    }

    private static void AppendListItem(StringBuilder list, int value)
    {
        if (list.Length > 0)
        {
            list.Append(", ");
        }

        list.Append(Integer(value));
    }

    private static string MinutesSeconds(double seconds)
    {
        long whole = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Min(seconds, MaxRenderedSeconds) : 0;
        return (whole / 60).ToString("00", Invariant) + ":" + (whole % 60).ToString("00", Invariant);
    }

    // Free text is written on one line: a line break in a step name or an end detail would otherwise split
    // its field, or start a line that reads as another field, such as a second run id line.
    private static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool clean = true;
        foreach (char character in value)
        {
            if (BreaksLine(character))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return value;
        }

        return string.Create(
            value.Length,
            value,
            static (destination, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] = BreaksLine(source[i]) ? ' ' : source[i];
                }
            });
    }

    // Every control character, and the two Unicode separators that line readers also treat as line breaks.
    private static bool BreaksLine(char character) =>
        char.IsControl(character) || character == '\u2028' || character == '\u2029';

    private static string TextOrNotCaptured(string? value)
    {
        string text = OneLine(value).Trim();
        return text.Length > 0 ? text : NotCaptured;
    }

    private static string Integer(long value) => value.ToString(Invariant);

    private static string IntegerOrNotCaptured(int? value) => value is { } number ? Integer(number) : NotCaptured;

    private static string Count(long value) => value.ToString(GroupedDigits, Invariant);

    private static string Count(ulong value) => value.ToString(GroupedDigits, Invariant);

    private static string Number(double value, string format) => value.ToString(format, Invariant);

    private static string NumberOrDash(double? value, string format) =>
        value is { } number ? Number(number, format) : EmDash;

    private static string Percent(double value, string format) => Number(value, format) + " %";

    private static string Seconds(double value) => Number(value, OneDecimal) + " s";

    private static string SecondsOrNotCaptured(double? value) => value is { } seconds ? Seconds(seconds) : NotCaptured;

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString(UtcFormat, Invariant);

    private static string UtcOrNotCaptured(DateTimeOffset? value) => value is { } stamp ? Utc(stamp) : NotCaptured;

    private static string YesNo(bool value) => value ? "Yes" : "No";
}
