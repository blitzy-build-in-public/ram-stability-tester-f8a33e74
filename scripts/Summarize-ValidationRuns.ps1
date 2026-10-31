<#
.SYNOPSIS
    Aggregates the validation-PC runs of RAMQuick and of the baseline tools into the markdown tables
    of docs/METHODOLOGY.md, applying the censoring, interval and decision rules of the validation
    procedure and no other statistics.

.DESCRIPTION
    Reads RAMQuick JSON summaries and their .log event logs, a baseline CSV written by the user and
    the HWiNFO64 CSV logs of the stress-bound measurement, and emits the tables the user pastes over
    the templates under "## Baseline results" and "## Validation results" in docs/METHODOLOGY.md.
    Every heading and table header is copied from that document.

    Rules implemented, exactly as the validation procedure defines them:
      - RAMQuick time to FAIL is run.timeToVerdictSeconds (Start accepted to verdict shown). A FAIL
        is observed; a PASS or ABORTED run on the failing profile is right-censored at its
        timeToVerdictSeconds and counts as a miss. A FAIL without a verdict time (a run recovered
        from an unfinished journal) is excluded from the medians and still counts as a miss.
      - A baseline run with no error by 120 min, or written "> t", is right-censored; any value
        above 120 is censored at 120; censored = 1 with a smaller value is censored at that value.
      - The baseline evidence is complete only with Karhu RAM Test (default settings), TestMem5
        (extreme@anta777) and every other tool in the CSV, which the user nominated, at least 3
        runs each, under one tool name and one configuration per tool. While it is incomplete,
        every "at least 5x faster" check is indeterminate and the missing evidence is named.
      - Criteria 2, 5 and 6 count only full-length runs: plan.targetSeconds of 300/1200/3600 s
        and a coverage target of at least 200/800/2500 %, with coverage.targetReached agreeing
        with coverage.percent. Duration is judged against 300/1200/3600 s. A scaled run is listed
        as not full-length and never extends a PASS streak; FAIL and ABORTED count at any length.
      - Time remaining is gated on every PROGRESS sample after step 1: remaining_ms = -1
        ("calculating") is allowed only during step 1, so a sample after it without an estimate
        counts as outside +-10 %.
      - The five Stop trials are the STOP_REQUESTED events with reason UserStop. Thermal and
        host-fault stops are requested by RAMQuick itself; they are reported, never counted.
      - A median is reported as an interval [L, U]: L puts every censored run at its censoring time,
        U puts it at +infinity. An even count averages the two middle values at each bound. No
        Kaplan-Meier estimate, confidence interval or significance test is computed.
      - Decisions are met, not met or indeterminate (unverified for the stress bound). Indeterminate
        never counts as met.
      - Stress bound: per metric, the RAMQuick Exhaustive statistic must not exceed the larger of
        the two reference statistics, each computed identically on every log: the peak 10-s
        rolling mean for bandwidth and package power (each window spans 10 s by timestamp and
        counts when at least 9 of its 1-s slots hold a numeric sample; its mean weighs every
        covered second alike, so no window joins values across a gap and no burst of samples
        within one second stands in for other seconds) and the peak sample for DIMM temperature,
        whose value in a row is the maximum over the DIMM columns and exists only when every one
        of them holds a number. The bound is unverified, and every within-bound cell with it,
        whenever the evidence is incomplete or not comparable: a metric missing from any log;
        other than exactly two reference logs; a log not polled at 1 s (a median interval outside
        1 s +-10 %, or fewer than 90 % of its intervals within it) or whose timestamps do not
        increase; a metric whose numeric samples fill fewer than 90 % of the 1-s slots of the run
        window, a step window or a reference log's span; a log without HWiNFO's sensor-name row
        (written when logging is stopped normally) or with a blank sensor name for a selected
        column; logs whose selected columns differ in name, position, sensor or unit; or no
        summary showing a completed full-length (3600-s) Exhaustive run. It is never judged on
        the remaining metrics. Its Status line reads satisfied, exceeded or unverified, as the
        document's template does, and names everything missing.

    Output sections (markdown, UTF-8 without BOM):
      ## Baseline results
        ### Baseline runs on the failing profile   per-run baseline table
        ### Baseline medians                       per-tool median intervals and the fastest
                                                    baseline median B with its deciding tool
      ## Validation results
        ### Criteria summary                       status per criterion (1-9); criteria this script
                                                    does not evaluate stay pending
        ### Criterion 1: fail-fast effectiveness   per-run table, per-type fail-fast checks, the
                                                    overall decision and, when indeterminate, the
                                                    remedy
        ### Criterion 2: no false failures         consecutive-PASS checks per clean profile
        ### Criteria 5 and 6: coverage and duration at full length
                                                    duration, coverage and time-remaining gate per
                                                    full-length clean run
        ### Criterion 7: UI behaviour and stop latency
                                                    stop-request to Completed latency per user Stop
                                                    trial, stops RAMQuick requested itself and
                                                    memory release time (both reported only)
        ### Stress-bound measurement               whole-run statistics against the reference
                                                    workloads, the verdict, and per-step statistics
    A section whose inputs were not supplied reads "_not supplied_", except the stress-bound
    section: it always carries its Status line, naming every missing log and metric, and its
    tables, with "_not supplied_" in the cells of absent logs.

.PARAMETER SelfTest
    Runs the built-in cases of the censoring, interval and decision rules, the log and HWiNFO
    parsers, the summary and log-record checks and the emitted headers, in memory. Prints
    "PASS <case>" or "FAIL <case>: expected ... got ..." per case and exits 0 only when every case
    passes. When docs/METHODOLOGY.md exists, one case also checks that every emitted heading and
    table header appears in it verbatim.

.PARAMETER FailingProfileRuns
    Folder of RAMQuick runs on the failing profile (criterion 1): ten interleaved runs each of
    Quick, Standard and Exhaustive with Stop on error on. Every *-summary.json in the folder (not
    recursive, *-recovered-summary.json included, *.tmp ignored) is read together with the .log of
    the same base name.

    Supplied evidence that cannot be used stops the script rather than being left out, because
    leaving a run out could let the rest meet a criterion. Each summary must be a schema-1 RAMQuick
    summary whose fields this script reads have the types and ranges the product writes, and whose
    run.id and run.testType match its file name. A folder without summaries, or any summary that
    fails these checks, is an input error that names each file and its problems. The exception is a
    <base>-summary.json that fails them beside a <base>-recovered-summary.json: crash recovery
    publishes the rebuilt summary under that name, so the failing file is skipped with a warning.
    In the .log, a PREPARED, STEP_START, STEP_END, PROGRESS, STOP_REQUESTED or COMPLETED line that
    does not parse or lacks a field this script reads is an input error naming the file and line.
    A last line without a line terminator, a write cut short by a crash, is ignored with a warning.
    A missing .log leaves the checks that need it indeterminate.

.PARAMETER PassingProfileRuns
    Folder of RAMQuick runs on the known-passing profile (criteria 2, 5, 6 and 7), read like
    -FailingProfileRuns.

.PARAMETER JedecRuns
    Folder of RAMQuick runs on JEDEC defaults (criteria 2, 5, 6 and 7), read like
    -FailingProfileRuns.

.PARAMETER BaselineCsv
    Baseline CSV with the columns tool,run,minutesToFirstError,censored and an optional
    configuration column. censored is 1/0 or true/false. minutesToFirstError is a number with a
    decimal point, or "> t" for a run that ended without an error at t minutes. A tool name
    containing "Karhu" is Karhu RAM Test, and one containing "TestMem5" (or written "TM5") is
    TestMem5; every other tool is a nominated one. A configuration, when given, must be exactly
    the tool's procedure configuration, ignoring case, repeated spaces and one pair of surrounding
    quotes or backticks (Karhu: default, defaults or default settings; TestMem5: extreme@anta777 or
    extreme@anta777.cfg); any other label, such as "Non-default settings", is an evidence gap.

.PARAMETER ReferenceLogs
    The two HWiNFO64 CSV logs of the reference workloads, in this order: llama.cpp CPU inference,
    then the 7-Zip benchmark (7z b). Each log's display name is its file name without extension.
    The references and -StressRunLog must be logged with the same tool at 1 s polling (at least
    90 % of the intervals 1 s +-10 %, and a numeric sample in at least 90 % of the seconds
    measured) and must yield the same columns for every metric: the same header names, in the
    same positions among same-named columns, from the same sensors and in convertible units.
    The sensors are read from HWiNFO's sensor-name row, which HWiNFO writes at the end of the
    log, below a repeated header and with blank Date and Time, when logging is stopped normally;
    a log without that row, or with a blank sensor name for a selected column, cannot show its
    sensors. Otherwise the bound is unverified.

.PARAMETER StressRunLog
    The HWiNFO64 CSV log recorded during the full RAMQuick Exhaustive run, polled at 1 s, with
    the same sensor columns as -ReferenceLogs and ended with HWiNFO's sensor-name row (see
    -ReferenceLogs).

.PARAMETER StressRunSummary
    The -summary.json of that Exhaustive run, required for a satisfied or exceeded bound: it must
    show a completed full-length Exhaustive run (run.testType Exhaustive, run.endReason Completed,
    plan.targetSeconds 3600, neither recovered nor partial) with a known run window, and show that
    the run itself was full length: at least 85 % of 3600 s (3060 s) of run time by both
    run.durationSeconds and the run window, and all 19 steps of the plan listed in steps[] and
    finished (each with an actualSeconds). Any other summary, or none, leaves the bound
    unverified. The run window (run.startedUtc to the .log's COMPLETED or run.endedUtc) restricts
    the whole-run statistics; without it the whole log is measured and reported. Its .log, beside
    it, supplies the step windows of the per-step statistics. The summary and its .log are checked
    as for -FailingProfileRuns; a summary that fails is an input error.

.PARAMETER BandwidthColumns
    Wildcard patterns for the DRAM bandwidth columns of the HWiNFO header. Every pattern must match;
    the first column matching each pattern is converted to GB/s and the contributions are summed per
    row (read + write). Default: '*Memory Read Bandwidth*', '*Memory Write Bandwidth*'. The columns
    selected must be the same in every log (see -ReferenceLogs).

.PARAMETER PackagePowerColumns
    Wildcard patterns for the CPU package power column. The first column matching the first pattern
    that matches anything is used. Default: '*CPU Package Power*'. The column selected must be the
    same in every log (see -ReferenceLogs).

.PARAMETER DimmTemperatureColumns
    Wildcard patterns for the DIMM (SPD) temperature columns. The maximum across every matching
    column is used per row, and only in a row where every matching column holds a number: a row
    with an empty DIMM cell has no DIMM temperature, so a DIMM column empty throughout leaves the
    metric missing and sporadic empty cells count against its time coverage. Default:
    '*DIMM*Temperature*', '*SPD Hub*Temperature*'. Every log must yield the same DIMM sensors
    (see -ReferenceLogs), so a run that logs fewer DIMMs than the references is unverified.

.PARAMETER HwInfoUtcOffsetMinutes
    UTC offset, in minutes, of the local Date and Time columns HWiNFO64 writes (for example 60 for
    UTC+01:00). When omitted, the local time zone of this machine converts them.

.PARAMETER OutputPath
    The markdown file to write. Default: artifacts/validation/validation-tables.md under the
    repository root. Its folder is created when missing.

.EXAMPLE
    .\scripts\Summarize-ValidationRuns.ps1 -SelfTest

    Runs the built-in rule checks; exit code 0 when every case passes. scripts/build.ps1 runs this.

.EXAMPLE
    .\scripts\Summarize-ValidationRuns.ps1 -FailingProfileRuns D:\val\failing -PassingProfileRuns D:\val\passing -JedecRuns D:\val\jedec -BaselineCsv D:\val\baseline.csv -ReferenceLogs D:\val\llama.csv, D:\val\7zip.csv -StressRunLog D:\val\exhaustive-hwinfo.csv -StressRunSummary D:\val\passing\RamQuick-20261028-140500-Exhaustive-0123456789abcdef0123456789abcdef-summary.json

    Writes every table to artifacts\validation\validation-tables.md and to the output stream.

.NOTES
    Exit codes. Summarize: 0 when the report was produced, whatever its decisions; 1 for an input
    error (a missing folder or file, a runs folder without summaries, a summary that is malformed,
    of an unsupported schema version or not the run its file name identifies, a malformed event-log
    record, an unreadable CSV, no input at all) or an unexpected error.
    SelfTest: 0 when every case passes, 1 otherwise.

    The script reads only its inputs (and docs/METHODOLOGY.md in -SelfTest), writes only
    -OutputPath, makes no network call, installs nothing and writes no registry value. It never
    reads errors.records: error detail may be capped or absent, so only run.verdict, the run.*
    timing fields and steps[].errors are used.

    Compatible with Windows PowerShell 5.1. The source is ASCII-only because Windows PowerShell
    reads a BOM-less script as ANSI; non-ASCII output characters are built from [char] codes.
#>
[CmdletBinding(DefaultParameterSetName = 'Summarize')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')]
    [switch]$SelfTest,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$FailingProfileRuns,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$PassingProfileRuns,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$JedecRuns,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$BaselineCsv,

    [Parameter(ParameterSetName = 'Summarize')]
    [string[]]$ReferenceLogs,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$StressRunLog,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$StressRunSummary,

    [Parameter(ParameterSetName = 'Summarize')]
    [string[]]$BandwidthColumns = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*'),

    [Parameter(ParameterSetName = 'Summarize')]
    [string[]]$PackagePowerColumns = @('*CPU Package Power*'),

    [Parameter(ParameterSetName = 'Summarize')]
    [string[]]$DimmTemperatureColumns = @('*DIMM*Temperature*', '*SPD Hub*Temperature*'),

    [Parameter(ParameterSetName = 'Summarize')]
    [ValidateRange(-1440, 1440)]
    [int]$HwInfoUtcOffsetMinutes,

    [Parameter(ParameterSetName = 'Summarize')]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------

# -LiteralPath because a checkout folder such as RAMQuick[dev] is a wildcard pattern to -Path;
# ProviderPath because Path keeps a PowerShell drive or provider prefix that the .NET file APIs
# cannot open. This runs before the entry point's error handling, so a root that cannot be
# resolved is reported here.
try {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
}
catch {
    [Console]::Error.WriteLine(('Summarize-ValidationRuns: the repository root above the script folder ''{0}'' could not be resolved: {1}' -f $PSScriptRoot, $_.Exception.Message))
    exit 1
}
$Invariant = [Globalization.CultureInfo]::InvariantCulture
$FloatStyle = [Globalization.NumberStyles]::Float

# Non-ASCII output characters, built from code points because the source must stay ASCII.
$Sym = @{
    Ge        = [string][char]0x2265
    Times     = [string][char]0x00D7
    PlusMinus = [string][char]0x00B1
    Inf       = [string][char]0x221E
    Dash      = [string][char]0x2014
    Deg       = [string][char]0x00B0
}

$Decision = @{
    Met           = 'met'
    NotMet        = 'not met'
    Indeterminate = 'indeterminate'
    Unverified    = 'unverified'
    Pending       = 'pending'
}

$TestTypes = @('Quick', 'Standard', 'Exhaustive')

# The values a schema-1 summary may hold in run.verdict and run.endReason (the RunEndReason member
# names, which the product persists by name); the .log's COMPLETED end_reason uses the same names.
$Verdicts = @('PASS', 'FAIL', 'ABORTED')
$RunEndReasons = @('Completed', 'FirstError', 'UserStop', 'Thermal', 'InsufficientMemory', 'AffinityFailed',
    'HostFault', 'EngineFault', 'PreviousRunUnfinished')

# The published summary names RamQuick-<yyyyMMdd>-<HHmmss>-<TestType>-<run id>[-recovered]-summary.json
# embed the run's identity, so a document whose run.id or run.testType differs from its name is not
# the summary of that run.
$SummaryNamePattern = [regex]'(?i)^RamQuick-\d{8}-\d{6}-(?<type>[A-Za-z]+)-(?<id>[0-9a-f]{32})(?:-recovered)?-summary\.json$'

# Criterion 1: FAIL within 300 s in at least 9 of 10 runs, at least 5x faster than the fastest
# baseline median, and Standard/Exhaustive no later than Quick.
$FailWithinSeconds = 300.0
$FastRunFraction = 0.9
$FastRunMinimumRuns = 10
$SpeedRatio = 5.0

# Baseline runs with no error by 120 min are right-censored at 120 min; each tool needs 3 runs.
$BaselineCensorMinutes = 120.0
$BaselineMinimumRuns = 3

# The procedure names these two baseline tools and their configurations, so B may decide criterion 1
# only once both are present; any other tool in the baseline CSV is one the user nominated. Tool
# patterns are matched case-insensitively (-like) against the trimmed tool cell. A configuration
# cell, once normalised by ConvertTo-BaselineConfigurationKey, must equal one of the tool's
# AcceptedConfigurations exactly, ignoring case: a substring or wildcard match would also accept
# labels that name another configuration, such as "Non-default settings" or "not extreme@anta777".
$RequiredBaselineTools = @(
    [pscustomobject]@{ Name = 'Karhu RAM Test'; ToolPatterns = @('*karhu*'); Configuration = 'default settings'; AcceptedConfigurations = @('default', 'defaults', 'default settings') }
    [pscustomobject]@{ Name = 'TestMem5'; ToolPatterns = @('*testmem5*', '*testmem 5*', 'tm5', 'tm5 *'); Configuration = 'extreme@anta777'; AcceptedConfigurations = @('extreme@anta777', 'extreme@anta777.cfg') }
)

# Criterion 2: consecutive PASS runs required per type on each clean profile.
$RequiredConsecutivePasses = @{ Exhaustive = 3; Quick = 10 }

# Criteria 5 and 6: duration within +-15 % of the stated duration; time remaining within +-10 % of
# the actual remaining time on every sample whose actual remaining time is at least 10 s.
$DurationTolerance = 0.15
$TimeRemainingTolerance = 0.10
$TimeRemainingGateSeconds = 10.0

# The full-length production plans: stated duration and coverage target per type. Criteria 2, 5 and
# 6 judge only runs of these plans. A scaled plan (the automated tests' and RamQuick.TestHost's
# budgets / 10) proves nothing about the validation PC, so its summaries are not hardware evidence.
$FullLengthPlans = @{
    Quick      = [pscustomobject]@{ Seconds = 300.0; CoveragePercent = 200.0 }
    Standard   = [pscustomobject]@{ Seconds = 1200.0; CoveragePercent = 800.0 }
    Exhaustive = [pscustomobject]@{ Seconds = 3600.0; CoveragePercent = 2500.0 }
}
# plan.targetSeconds is the sum of the step budgets; this only absorbs floating-point noise in it.
$PlanSecondsTolerance = 0.001
# The verdict criterion 2 records for a PASS run that is not full-length evidence: it breaks a
# streak without counting as a FAIL.
$NotFullLengthVerdict = 'NOT FULL-LENGTH'

# Criterion 7: stop request to Completed within 2 s; five trials, one in Preparing, one in a hold.
$StopLatencyLimitSeconds = 2.0
$StopTrialsRequired = 5
# The STOP_REQUESTED reason a Stop press logs. Only those requests are trials of the Stop procedure:
# Thermal and HostFault stops are requested by RAMQuick itself, so they say nothing about Stop.
$UserStopReason = 'UserStop'

# Stress bound: 10-s rolling means over 1-s samples; HWiNFO polling must be 1 s +- 10 %, by the
# median interval and for at least 90 % of the intervals. A window, like any measured interval,
# counts only when at least 90 % of its 1-s slots hold a numeric sample (9 of 10). The run must be
# the production Exhaustive plan of src/RamQuick.Plans/TestPlans.cs, completed at full length.
$RollingWindowSeconds = 10.0
$ExpectedSamplingSeconds = 1.0
$SamplingTolerance = 0.10
$RollingWindowMinimumValues = [int][Math]::Floor((1.0 - $SamplingTolerance) * $RollingWindowSeconds / $ExpectedSamplingSeconds + 1e-9)
$StressRunTargetSeconds = $FullLengthPlans['Exhaustive'].Seconds
$StressRunTargetToleranceSeconds = $PlanSecondsTolerance
# The nineteen steps of the production Exhaustive plan (Q1-Q6, S7-S12, E13-E19): a full-length run
# lists and finishes every one of them.
$StressRunStepCount = 19

$LogTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
$OutputTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"

$HwInfoDateFormats = @('d.M.yyyy', 'dd.MM.yyyy', 'M/d/yyyy', 'yyyy-MM-dd')
$HwInfoTimeFormats = @('H:mm:ss.fff', 'H:mm:ss.ff', 'H:mm:ss.f', 'H:mm:ss')
$HwInfoTimestampFormats = [string[]]@(foreach ($dateFormat in $HwInfoDateFormats) {
        foreach ($timeFormat in $HwInfoTimeFormats) { $dateFormat + ' ' + $timeFormat }
    })

# The three stress-bound metrics, in the order of the document's table.
$StressMetrics = @(
    [pscustomobject]@{ Key = 'Bandwidth'; Label = 'DRAM read+write bandwidth'; Unit = 'GB/s'; Rolling = $true; Statistic = 'peak 10-s rolling mean' }
    [pscustomobject]@{ Key = 'PackagePower'; Label = 'CPU package power'; Unit = 'W'; Rolling = $true; Statistic = 'peak 10-s rolling mean' }
    [pscustomobject]@{ Key = 'DimmTemperature'; Label = 'DIMM temperature'; Unit = ($Sym.Deg + 'C'); Rolling = $false; Statistic = 'peak sample' }
)

# Headings and table headers, copied character for character from docs/METHODOLOGY.md: the user
# pastes the output over the templates there, and -SelfTest checks them against the document.
$Headings = [ordered]@{
    Baseline           = '## Baseline results'
    BaselineRuns       = '### Baseline runs on the failing profile'
    BaselineMedians    = '### Baseline medians'
    Validation         = '## Validation results'
    CriteriaSummary    = '### Criteria summary'
    Criterion1         = '### Criterion 1: fail-fast effectiveness'
    Criterion1Runs     = '#### RAMQuick runs on the failing profile'
    Criterion1Checks   = '#### Fail-fast checks'
    Criterion2         = '### Criterion 2: no false failures'
    Criteria56         = '### Criteria 5 and 6: coverage and duration at full length'
    TimeRemaining      = '#### Time remaining'
    Criterion7         = '### Criterion 7: UI behaviour and stop latency'
    ReleaseTime        = '#### Memory release time'
    StressBound        = '### Stress-bound measurement'
    StressPerStep      = '#### Per-step statistics'
}

$Headers = [ordered]@{
    BaselineRuns    = @('Tool', 'Configuration', 'Run', 'Minutes to first error', 'Censored')
    BaselineMedians = @('Tool', 'Runs', 'Censored runs', 'Median interval [L, U] (min)')
    CriteriaSummary = @('#', 'Criterion', 'Threshold', 'Evidence', 'Status')
    Criterion1Runs  = @('Type', 'Run', 'Verdict', 'Time to verdict (s)', 'First error (s)', 'First-error step', 'Censored')
    Criterion1Types = @('Type', 'Runs', 'FAIL within 300 s', 'Median interval [L, U] (s)', 'Ratio interval vs fastest baseline',
        ('Within 5 min (' + $Sym.Ge + ' 9 of 10)'), ('At least 5' + $Sym.Times + ' faster'), 'No later than Quick')
    Criterion2      = @('Profile', 'Test', 'Required', 'Observed', 'Status')
    Criteria56      = @('Type', 'Planned duration (s)', 'Duration (s)', 'Deviation (%)', ('Within ' + $Sym.PlusMinus + '15 %'),
        'Coverage (%)', 'Target (%)', 'Target reached')
    TimeRemaining   = @('Type', ('Gated samples (actual ' + $Sym.Ge + ' 10 s)'), ('Within ' + $Sym.PlusMinus + '10 %'),
        'Worst gated deviation (%)', 'Ungated samples (< 10 s)', 'Worst ungated deviation (%)', 'Status')
    StopLatency     = @('Trial', 'Stop point', 'Stop request (UTC)', 'Completed (UTC)', 'Latency (s)', 'Within 2 s')
    ReleaseTime     = @('Type', 'Tested bytes', 'Release (s)')
    # The document names the reference columns by workload, so -ReferenceLogs is taken in that
    # order (llama.cpp first, 7-Zip second); the file names are listed in the line above the table.
    StressBound     = @('Metric', 'Statistic', 'llama.cpp inference', '7-Zip benchmark', 'Bound (larger reference)',
        'RAMQuick Exhaustive', 'Within bound')
    StressPerStep   = @('ID', 'Step name', 'Bandwidth (GB/s)', 'Package power (W)', ('DIMM temperature (' + $Sym.Deg + 'C)'), 'Within bound')
}

$FastestBaselinePrefix = 'Fastest baseline median B:'
$StressStatusPrefix = 'Status:'
$NotSupplied = '_not supplied_'

# ---------------------------------------------------------------------------------------------
# Values: numbers, JSON fields, timestamps, formatting
# ---------------------------------------------------------------------------------------------

function ConvertFrom-InvariantNumber {
    # A double parsed in the invariant culture, or $null for empty, 'unknown' or non-numeric text.
    param([AllowNull()][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $value = 0.0
    if ([double]::TryParse($Text.Trim(), $FloatStyle, $Invariant, [ref]$value)) { return $value }
    return $null
}

function Get-JsonValue {
    # Walks a dotted path over ConvertFrom-Json objects (PSCustomObject) and JavaScriptSerializer
    # objects (IDictionary); $null when any segment is absent. StrictMode would throw on a missing
    # property, so every summary field is read through this helper.
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        if ($current -is [System.Collections.IDictionary]) {
            # Keys are matched like PSCustomObject properties (case-insensitively). A generic
            # Dictionary exposes no callable Contains in Windows PowerShell, so the keys are walked.
            $matchedKey = $null
            foreach ($key in $current.Keys) {
                if ([string]::Equals([string]$key, $segment, [StringComparison]::OrdinalIgnoreCase)) { $matchedKey = $key; break }
            }
            if ($null -eq $matchedKey) { return $null }
            $current = $current[$matchedKey]
        }
        elseif ($current -is [System.Management.Automation.PSCustomObject]) {
            $property = $current.PSObject.Properties[$segment]
            if ($null -eq $property) { return $null }
            $current = $property.Value
        }
        else {
            return $null
        }
    }
    return $current
}

function Get-JsonArray {
    # The array at a dotted path, never unrolled; empty when absent or null.
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][string]$Path)

    $value = Get-JsonValue -Object $Object -Path $Path
    if ($null -eq $value) { return , @() }
    if ($value -is [string] -or $value -is [System.Collections.IDictionary]) { return , @($value) }
    return , @($value | Where-Object { $null -ne $_ })
}

function Get-JsonNumber {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][string]$Path)

    $value = Get-JsonValue -Object $Object -Path $Path
    if ($null -eq $value -or $value -is [bool]) { return $null }
    if ($value -is [string]) { return (ConvertFrom-InvariantNumber $value) }
    try { return [double]$value } catch { return $null }
}

function Get-JsonString {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][string]$Path)

    $value = Get-JsonValue -Object $Object -Path $Path
    if ($null -eq $value) { return $null }
    return [string]$value
}

function Get-JsonBool {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][string]$Path)

    $value = Get-JsonValue -Object $Object -Path $Path
    if ($value -is [bool]) { return $value }
    if ($value -is [string]) {
        if ($value -ieq 'true') { return $true }
        if ($value -ieq 'false') { return $false }
    }
    return $null
}

function Get-JsonMember {
    # Walks a dotted path like Get-JsonValue, but tells an absent member (Present $false) from one
    # that holds null: the product writes every summary property, null included, so only a document
    # it did not write lacks one.
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        $found = $false
        if ($current -is [System.Collections.IDictionary]) {
            $matchedKey = $null
            foreach ($key in $current.Keys) {
                if ([string]::Equals([string]$key, $segment, [StringComparison]::OrdinalIgnoreCase)) { $matchedKey = $key; $found = $true; break }
            }
            if ($found) { $current = $current[$matchedKey] }
        }
        elseif ($current -is [System.Management.Automation.PSCustomObject]) {
            $property = $current.PSObject.Properties[$segment]
            if ($null -ne $property) { $current = $property.Value; $found = $true }
        }
        if (-not $found) { return [pscustomobject]@{ Present = $false; Value = $null } }
    }
    return [pscustomobject]@{ Present = $true; Value = $current }
}

function Test-JsonObject {
    # Whether a parsed value is a JSON object, in either parser's shape.
    param([AllowNull()][object]$Value)

    return ($Value -is [System.Collections.IDictionary] -or $Value -is [System.Management.Automation.PSCustomObject])
}

function Test-JsonNumber {
    # Whether a parsed value is a finite JSON number. Both parsers return Int32, Int64, Decimal or
    # Double for a number token, so a quoted "95.5" or a boolean, which Get-JsonNumber would coerce,
    # is not one.
    param([AllowNull()][object]$Value)

    if ($Value -is [int] -or $Value -is [long] -or $Value -is [decimal]) { return $true }
    if ($Value -is [double] -or $Value -is [single]) {
        $number = [double]$Value
        return -not ([double]::IsNaN($number) -or [double]::IsInfinity($number))
    }
    return $false
}

function Format-JsonFound {
    # A parsed value as a problem message quotes it: strings in double quotes, so "10.0" reads as
    # text rather than a number, shortened and kept on one line.
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' }
        return 'false'
    }
    if ($Value -is [string]) {
        $text = $Value -replace '[\x00-\x1F]', ' '
        if ($text.Length -gt 40) { $text = $text.Substring(0, 40) + '...' }
        return ('"' + $text + '"')
    }
    if (Test-JsonObject $Value) { return 'an object' }
    if ($Value -is [array]) { return 'an array' }
    return [Convert]::ToString($Value, $Invariant)
}

function ConvertFrom-JsonTimestamp {
    # An ISO 8601 UTC string, or a DateTime that a newer ConvertFrom-Json already converted, as a UTC
    # DateTimeOffset; $null when absent or unreadable.
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
            $dateTime = [DateTime]::SpecifyKind($dateTime, [DateTimeKind]::Utc)
        }
        return ([DateTimeOffset]$dateTime.ToUniversalTime())
    }
    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string]$Value, $Invariant, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
        return $parsed.ToUniversalTime()
    }
    return $null
}

function Test-JsonTimestamp {
    # Whether a parsed value is a timestamp the summary schema allows: an ISO 8601 UTC date and time,
    # which the product writes with 'Z'. Text must name UTC itself, by 'Z' or a zero offset: a
    # zone-less time is local to a machine nobody recorded, which ConvertFrom-JsonTimestamp would
    # take as UTC. The shape is checked first because DateTimeOffset.TryParse also takes local
    # formats such as 10/28/2026, which no summary writes. A value a parser already converted keeps
    # no text: a DateTimeOffset is UTC only at offset zero, and a DateTime only with Kind Utc, which
    # PowerShell 7's ConvertFrom-Json gives ISO 8601 text with 'Z' (Unspecified without a zone, Local
    # with any offset, the offset lost). Windows PowerShell's parsers convert only the "\/Date(ms)\/"
    # form, which is not ISO 8601, so there no DateTime is a schema timestamp.
    param([AllowNull()][object]$Value)

    if ($Value -is [DateTimeOffset]) { return (([DateTimeOffset]$Value).Offset -eq [TimeSpan]::Zero) }
    if ($Value -is [DateTime]) {
        return ($PSVersionTable.PSVersion.Major -ge 6 -and ([DateTime]$Value).Kind -eq [DateTimeKind]::Utc)
    }
    if ($Value -isnot [string]) { return $false }
    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]00:?00)$') { return $false }
    return ($null -ne (ConvertFrom-JsonTimestamp $Value))
}

function Get-SecondsBetween {
    param([AllowNull()][object]$From, [AllowNull()][object]$To)

    if ($null -eq $From -or $null -eq $To) { return $null }
    return ([DateTimeOffset]$To - [DateTimeOffset]$From).TotalSeconds
}

function Format-Number {
    # Invariant fixed-point text; the em dash for $null or NaN and the infinity sign for +-Infinity.
    param([AllowNull()][object]$Value, [int]$Decimals = 1)

    if ($null -eq $Value) { return $Sym.Dash }
    $number = [double]$Value
    if ([double]::IsNaN($number)) { return $Sym.Dash }
    if ([double]::IsPositiveInfinity($number)) { return $Sym.Inf }
    if ([double]::IsNegativeInfinity($number)) { return ('-' + $Sym.Inf) }
    return $number.ToString('F' + $Decimals, $Invariant)
}

function Format-SignedNumber {
    param([AllowNull()][object]$Value, [int]$Decimals = 1)

    if ($null -eq $Value) { return $Sym.Dash }
    $text = Format-Number -Value $Value -Decimals $Decimals
    if ([double]$Value -gt 0 -and -not $text.StartsWith('-')) { return ('+' + $text) }
    return $text
}

function Format-Interval {
    param([AllowNull()][object]$Low, [AllowNull()][object]$High, [int]$Decimals = 1)

    if ($null -eq $Low -or $null -eq $High) { return $Sym.Dash }
    return ('[' + (Format-Number $Low $Decimals) + ', ' + (Format-Number $High $Decimals) + ']')
}

function Format-Timestamp {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $Sym.Dash }
    return ([DateTimeOffset]$Value).ToUniversalTime().ToString($OutputTimestampFormat, $Invariant)
}

function Format-YesNo {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $Sym.Dash }
    if ([bool]$Value) { return 'yes' }
    return 'no'
}

function Format-MarkdownTableHeader {
    param([Parameter(Mandatory = $true)][string[]]$Header)

    return ('| ' + ($Header -join ' | ') + ' |')
}

function Format-MarkdownCell {
    # Escapes the cell separator and keeps every cell on one line.
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $Sym.Dash }
    $text = [string]$Value
    if ($text.Length -eq 0) { return $Sym.Dash }
    return (($text -replace '[\r\n]+', ' ') -replace '\|', '\|')
}

function Format-MarkdownTable {
    # The header row, the separator row and one row per element of $Rows (each an array of cells).
    param(
        [Parameter(Mandatory = $true)][string[]]$Header,
        [AllowEmptyCollection()][object[]]$Rows
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add((Format-MarkdownTableHeader -Header $Header))
    $separators = @(foreach ($cell in $Header) { ' --- ' })
    $lines.Add('|' + ($separators -join '|') + '|')
    foreach ($row in @($Rows)) {
        $cells = @(foreach ($cell in @($row)) { Format-MarkdownCell $cell })
        if ($cells.Count -ne $Header.Count) {
            throw ('A table row has {0} cells, but its header has {1}.' -f $cells.Count, $Header.Count)
        }
        $lines.Add('| ' + ($cells -join ' | ') + ' |')
    }
    return , $lines.ToArray()
}

function Get-StepId {
    # Q1-Q6, S7-S12 and E13-E19: Standard and Exhaustive extend Quick's shared opening block, so a
    # step's 1-based index alone determines its identifier in every plan.
    param([int]$Index)

    if ($Index -le 6) { return ('Q' + $Index) }
    if ($Index -le 12) { return ('S' + $Index) }
    return ('E' + $Index)
}


# ---------------------------------------------------------------------------------------------
# Readers: RAMQuick event logs and summaries
# ---------------------------------------------------------------------------------------------

# One key=value pair of a .log line. Free text is double-quoted with \" and \\ escapes; any other
# value is unquoted and holds no space or double quote.
$LogFieldPattern = [regex]'\G[ ]*([A-Za-z_][A-Za-z0-9_]*)=("(?:[^"\\]|\\.)*"|[^ "]*)'

# The first field of a .log line as ResultsLog writes it: a lower-case snake_case key, at the line's
# start or after a space, then '='. Case-sensitive, as every [regex] match is.
$LogFirstFieldPattern = [regex]'(?:^| )[a-z][a-z0-9_]*='

# The events this script uses; every other line (ERROR detail above all) is skipped unparsed.
$LogEventsUsed = @{
    'PREPARED'       = $true
    'STEP_START'     = $true
    'STEP_END'       = $true
    'PROGRESS'       = $true
    'STOP_REQUESTED' = $true
    'COMPLETED'      = $true
}

function ConvertFrom-RunLogLine {
    # Parses one line of a RAMQuick .log, "<yyyy-MM-ddTHH:mm:ss.fffZ> <EVENT> key=value ...", into
    # Utc, Event, Fields (unquoted, unescaped text) and, for lines with step=k/N, StepNumber and
    # StepCount. k is 1-based, as every persisted step number is. $null for a line that does not
    # follow the format. ResultsLog writes each key once per line, so a repeat is damage that
    # Fields, holding only the last value, would hide; DuplicateFields names each repeated key
    # (compared as Fields compares keys, ignoring case) and is empty when there is none.
    param([AllowNull()][AllowEmptyString()][string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) { return $null }
    $firstSpace = $Line.IndexOf(' ')
    if ($firstSpace -le 0) { return $null }
    $utc = [DateTimeOffset]::MinValue
    $stampOk = [DateTimeOffset]::TryParseExact($Line.Substring(0, $firstSpace), $LogTimestampFormat, $Invariant,
        [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$utc)
    if (-not $stampOk) { return $null }

    $rest = $Line.Substring($firstSpace + 1).TrimEnd()
    $secondSpace = $rest.IndexOf(' ')
    if ($secondSpace -lt 0) {
        $eventName = $rest
        $rest = ''
    }
    else {
        $eventName = $rest.Substring(0, $secondSpace)
        $rest = $rest.Substring($secondSpace + 1)
    }
    if ($eventName -cnotmatch '^[A-Z][A-Z_]*$') { return $null }

    $fields = @{}
    $duplicateFields = New-Object 'System.Collections.Generic.List[string]'
    $position = 0
    $match = $LogFieldPattern.Match($rest, 0)
    while ($match.Success -and $match.Length -gt 0) {
        $key = $match.Groups[1].Value
        $raw = $match.Groups[2].Value
        if ($raw.StartsWith('"')) {
            $raw = [regex]::Replace($raw.Substring(1, $raw.Length - 2), '\\(.)', '$1')
        }
        if ($fields.ContainsKey($key) -and -not ($duplicateFields -contains $key)) { $duplicateFields.Add($key) }
        $fields[$key] = $raw
        $position = $match.Index + $match.Length
        $match = $match.NextMatch()
    }
    if ($rest.Substring($position).Trim().Length -gt 0) { return $null }

    $stepNumber = $null
    $stepCount = $null
    if ($fields.ContainsKey('step')) {
        # At most nine digits, so the [int] conversion cannot overflow on a damaged value; a longer
        # one leaves StepNumber $null for Get-RunLogRecordProblem to report.
        $stepMatch = [regex]::Match($fields['step'], '^(\d{1,9})(?:/(\d{1,9}))?$')
        if ($stepMatch.Success) {
            $stepNumber = [int]$stepMatch.Groups[1].Value
            if ($stepMatch.Groups[2].Success) { $stepCount = [int]$stepMatch.Groups[2].Value }
        }
    }

    return [pscustomobject]@{
        Utc             = $utc.ToUniversalTime()
        Event           = $eventName
        Fields          = $fields
        DuplicateFields = $duplicateFields.ToArray()
        StepNumber      = $stepNumber
        StepCount       = $stepCount
    }
}

function Get-RunLogLineEvent {
    # The event of $LogEventsUsed that a .log line is a record of, or $null when it records none of
    # them. Pure. Judged from the line's text before its first well-formed field
    # ($LogFirstFieldPattern; the whole line when it has none): the line records the used event
    # whose name that text contains, case-sensitively, at the leftmost position. ResultsLog writes
    # "<stamp> <EVENT>" and then " key=value" pairs, and neither the stamp nor an event name holds a
    # lower-case letter, so on an intact line that text is exactly "<stamp> <EVENT>": every value
    # and all quoted free text come later, a message naming an event cannot reach it, and none of
    # the product's other events (APP_ERROR, ERROR, ERROR_CAP_REACHED, JOURNAL_FAILURE, MEMORY,
    # NOTICE, PLAN, PLAN_TARGET, RELEASE_NOT_CONFIRMED, RELEASED, SENSOR, SIZING, START_REQUESTED,
    # STEP_OVERRUN, SUMMARY_FAILED, SUMMARY_PUBLISHED, SYSTEM, THERMAL_STOP, VERDICT) contains a
    # used name. A cut at the first '=' would end before the event when the stamp holds a stray '='
    # and so skip the line; this text instead runs past any damage to the first real field, so a
    # timestamp that is missing, split by a stray space, fused to the event or holding a stray '=',
    # and an event fused to its first key, all leave the name in it and the line is judged. A
    # corrupt event token that merely contains one, such as XSTEP_END, is no event of the product,
    # and ConvertFrom-RunLogText rejects its line as malformed.
    param([AllowNull()][AllowEmptyString()][string]$Line)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    $firstField = $LogFirstFieldPattern.Match($Line)
    $head = if ($firstField.Success) { $Line.Substring(0, $firstField.Index) } else { $Line }
    $found = $null
    $foundAt = -1
    foreach ($name in $LogEventsUsed.Keys) {
        # Ordinal: event names are written in upper case, so text differing only in case is no
        # event of the product. No used name starts another, so two cannot share the leftmost
        # position.
        $at = $head.IndexOf([string]$name, [StringComparison]::Ordinal)
        if ($at -ge 0 -and ($foundAt -lt 0 -or $at -lt $foundAt)) {
            $found = [string]$name
            $foundAt = $at
        }
    }
    return $found
}

function ConvertFrom-ProgressRecord {
    # A parsed PROGRESS line as a time-remaining sample; RemainingMs is -1 while still calculating.
    param([Parameter(Mandatory = $true)][object]$Record)

    return [pscustomobject]@{
        Utc         = $Record.Utc
        StepNumber  = $Record.StepNumber
        StepCount   = $Record.StepCount
        ElapsedMs   = (ConvertFrom-InvariantNumber $Record.Fields['elapsed_ms'])
        RemainingMs = (ConvertFrom-InvariantNumber $Record.Fields['remaining_ms'])
        Coverage    = (ConvertFrom-InvariantNumber $Record.Fields['coverage'])
        Errors      = (ConvertFrom-InvariantNumber $Record.Fields['errors'])
    }
}

function Get-LogFieldProblem {
    # The problem with one key=value field of a .log record, or $null when it holds what ResultsLog
    # writes: Count (a whole number >= 0), Remaining (a whole number of milliseconds >= 0, or -1
    # while the estimate is still being calculated), Percent (a finite number >= 0), Text (not
    # empty) or EndReason (a RunEndReason member name).
    param(
        [Parameter(Mandatory = $true)][hashtable]$Fields,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('Count', 'Remaining', 'Percent', 'Text', 'EndReason')][string]$Kind
    )

    if (-not $Fields.ContainsKey($Name)) { return ('{0} is missing' -f $Name) }
    $text = [string]$Fields[$Name]
    $none = [Globalization.NumberStyles]::None
    $whole = [decimal]0
    switch -CaseSensitive ($Kind) {
        'Count' {
            if ([decimal]::TryParse($text, $none, $Invariant, [ref]$whole)) { return $null }
            $expected = 'a whole number >= 0'
        }
        'Remaining' {
            if ($text -ceq '-1' -or [decimal]::TryParse($text, $none, $Invariant, [ref]$whole)) { return $null }
            $expected = 'a whole number of milliseconds >= 0, or -1 while calculating'
        }
        'Percent' {
            $number = ConvertFrom-InvariantNumber $text
            if ($null -ne $number -and -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0) { return $null }
            $expected = 'a finite number >= 0'
        }
        'Text' {
            if (-not [string]::IsNullOrWhiteSpace($text)) { return $null }
            return ('{0} is empty' -f $Name)
        }
        'EndReason' {
            if ($RunEndReasons -ccontains $text) { return $null }
            $expected = 'one of ' + ($RunEndReasons -join ', ')
        }
    }
    return ('{0}={1} is not {2}' -f $Name, $text, $expected)
}

function Get-RunLogRecordProblem {
    # Every reason a parsed record of an event this script uses cannot be used, empty when it can.
    # Pure. A repeated key of any name is one: which of its values the product wrote cannot be told.
    # Beyond that, only the fields the readers consume are judged: the step number that places
    # STEP_START, STEP_END and PROGRESS, the step name, the PROGRESS values of the time-remaining
    # gate, the stop reason and the end reason. PREPARED is used only for its timestamp.
    param([Parameter(Mandatory = $true)][object]$Record)

    $problems = New-Object 'System.Collections.Generic.List[string]'
    foreach ($duplicate in $Record.DuplicateFields) { $problems.Add(('{0} appears more than once' -f $duplicate)) }
    $fields = $Record.Fields
    $eventName = [string]$Record.Event
    if (@('STEP_START', 'STEP_END', 'PROGRESS') -ccontains $eventName) {
        if (-not $fields.ContainsKey('step')) { $problems.Add('step is missing') }
        elseif ($null -eq $Record.StepNumber) { $problems.Add(('step={0} is not a step number k or k/N' -f $fields['step'])) }
        elseif ($Record.StepNumber -lt 1) { $problems.Add(('step={0} is not a step number: they start at 1' -f $fields['step'])) }
        elseif ($null -ne $Record.StepCount -and $Record.StepNumber -gt $Record.StepCount) {
            $problems.Add(('step={0} is not a step number: it exceeds the step count' -f $fields['step']))
        }
    }
    $rules = @()
    switch -CaseSensitive ($eventName) {
        'STEP_START' { $rules = @(, @('name', 'Text')) }
        'PROGRESS' { $rules = @(@('elapsed_ms', 'Count'), @('remaining_ms', 'Remaining'), @('coverage', 'Percent'), @('errors', 'Count')) }
        'STOP_REQUESTED' { $rules = @(, @('reason', 'Text')) }
        'COMPLETED' { $rules = @(, @('end_reason', 'EndReason')) }
    }
    foreach ($rule in $rules) {
        $problem = Get-LogFieldProblem -Fields $fields -Name $rule[0] -Kind $rule[1]
        if ($null -ne $problem) { $problems.Add($problem) }
    }
    return , $problems.ToArray()
}

function Import-TextLine {
    # The lines of a UTF-8 text file, readable even while another process still holds it open for
    # writing, and whether the text ends with a line terminator. Both come from one read, so a line
    # the writer appends meanwhile cannot change which line is last.
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding $false), $true)
        try { $text = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lineReader = New-Object IO.StringReader($text)
    while ($null -ne ($line = $lineReader.ReadLine())) { $lines.Add($line) }
    $terminated = ($text.Length -eq 0 -or $text.EndsWith("`n") -or $text.EndsWith("`r"))
    return [pscustomobject]@{ Lines = $lines.ToArray(); Terminated = $terminated }
}

function ConvertFrom-RunLogText {
    # The events of the lines of one .log this script uses: PREPARED, STEP_START/STEP_END (step
    # windows), PROGRESS (time-remaining samples), and the first STOP_REQUESTED and COMPLETED, with
    # Exists $true. Pure, so whole logs can be checked in memory; -Path only labels messages, and
    # -Terminated says whether the text ended with a line terminator. A line of one of those events
    # (as Get-RunLogLineEvent judges it, so a damaged, missing or fused timestamp, or an event fused
    # to its first key, does not hide one) that does not parse, whose event token is not exactly
    # that event, that repeats a key, or that lacks a field its readers use, is supplied evidence
    # that cannot be used and throws "<path>:<line>: <EVENT>: <problem>" rather than being dropped.
    # Lines of every other event are not parsed.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][bool]$Terminated
    )

    $log = [pscustomobject]@{
        Path          = $Path
        Exists        = $true
        Prepared      = $null
        StepStarts    = (New-Object 'System.Collections.Generic.List[object]')
        StepEnds      = (New-Object 'System.Collections.Generic.List[object]')
        Progress      = (New-Object 'System.Collections.Generic.List[object]')
        StopRequested = $null
        Completed     = $null
    }
    $lineCount = $Lines.Count
    # Each event is appended and flushed whole, its CRLF included, so text without a final line
    # terminator ends in a write a crash cut short, and only that last line can be torn.
    if (-not $Terminated -and $lineCount -gt 0) { $lineCount-- }
    for ($lineIndex = 0; $lineIndex -lt $lineCount; $lineIndex++) {
        $line = $Lines[$lineIndex]
        $eventName = Get-RunLogLineEvent -Line $line
        if ($null -eq $eventName) { continue }

        $record = ConvertFrom-RunLogLine -Line $line
        # A token such as XSTEP_END parses, but no product event is named so.
        if ($null -eq $record -or $record.Event -cne $eventName) {
            throw ('{0}:{1}: {2}: the line does not follow the log line format "<yyyy-MM-ddTHH:mm:ss.fffZ> <EVENT> key=value ..."' -f $Path, ($lineIndex + 1), $eventName)
        }
        $problems = Get-RunLogRecordProblem -Record $record
        if ($problems.Count -gt 0) {
            throw ('{0}:{1}: {2}: {3}' -f $Path, ($lineIndex + 1), $eventName, ($problems -join '; '))
        }
        switch -CaseSensitive ($record.Event) {
            'PREPARED' { if ($null -eq $log.Prepared) { $log.Prepared = $record } }
            'STEP_START' { $log.StepStarts.Add($record) }
            'STEP_END' { $log.StepEnds.Add($record) }
            'PROGRESS' { $log.Progress.Add((ConvertFrom-ProgressRecord -Record $record)) }
            'STOP_REQUESTED' { if ($null -eq $log.StopRequested) { $log.StopRequested = $record } }
            'COMPLETED' { if ($null -eq $log.Completed) { $log.Completed = $record } }
        }
    }
    return $log
}

function Import-RunLog {
    # Reads one .log and returns its events as ConvertFrom-RunLogText judges them. Exists is $false
    # when the file is missing, which leaves the checks that need it indeterminate.
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $log = ConvertFrom-RunLogText -Path $Path -Lines @() -Terminated $true
        $log.Exists = $false
        return $log
    }
    $content = Import-TextLine -Path $Path
    # Warned before the lines are judged, so a malformed line that stops the run does not hide it.
    if (-not $content.Terminated -and $content.Lines.Count -gt 0) {
        Write-Warning ('{0}: its last line has no line terminator, a write cut short by a crash; that line is ignored.' -f $Path)
    }
    return (ConvertFrom-RunLogText -Path $Path -Lines $content.Lines -Terminated $content.Terminated)
}

function Format-JsonParserMessage {
    # A parser message on one short line. The JavaScriptSerializer behind both parsers appends the
    # whole input after the error position, which for a summary is the entire document.
    param([AllowNull()][AllowEmptyString()][string]$Message)

    $text = [regex]::Replace([string]$Message, '(?s)^(.*?)\s*\((\d+)\):.*$', '$1 (at character $2)')
    $text = ($text -replace '[\x00-\x1F]+', ' ').Trim()
    if ($text.Length -gt 300) { $text = $text.Substring(0, 300) + '...' }
    return $text
}

function Import-JsonDocument {
    # Reads and parses one JSON file. Problem is $null and Document the parsed document, or Problem
    # says why the file cannot be read or is not JSON, with the parser's message; the caller names the
    # file, and decides whether a replacement supersedes it or the input is rejected. Windows
    # PowerShell's ConvertFrom-Json rejects documents above its length limit, which a summary holding
    # 10,000 error records can exceed; JavaScriptSerializer without that limit parses those instead.
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $text = [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding $false))
    }
    catch {
        return [pscustomobject]@{ Document = $null; Problem = ('cannot be read: {0}' -f (Format-JsonParserMessage $_.Exception.Message)) }
    }
    # ConvertFrom-Json returns nothing for blank text, which would otherwise read as a null document.
    if ([string]::IsNullOrWhiteSpace($text)) {
        return [pscustomobject]@{ Document = $null; Problem = 'not valid JSON: the file is empty' }
    }

    try {
        return [pscustomobject]@{ Document = ($text | ConvertFrom-Json); Problem = $null }
    }
    catch {
        $message = $_.Exception.Message
        if ($message -notmatch 'maxJsonLength|length of the string') {
            return [pscustomobject]@{ Document = $null; Problem = ('not valid JSON: {0}' -f (Format-JsonParserMessage $message)) }
        }
    }

    try {
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = [int]::MaxValue
        $serializer.RecursionLimit = 256
        return [pscustomobject]@{ Document = $serializer.DeserializeObject($text); Problem = $null }
    }
    catch {
        return [pscustomobject]@{ Document = $null; Problem = ('not valid JSON: {0}' -f (Format-JsonParserMessage $_.Exception.Message)) }
    }
}

# The schema-1 summary fields this script reads, with the rule each must meet (see
# Get-JsonFieldProblem). Types and nullability follow RunSummary.cs; nulls are legitimate where a
# recovered Unfinished summary carries them for values its journal never captured, such as the plan
# targets and the coverage of a run whose plan was lost. RunSummary.cs also lets such a summary hold a
# null run.id or run.testType; this script still requires both, because a run it cannot identify can
# neither be counted under its type nor left out (see -FailingProfileRuns).
$SummaryFieldRules = @(
    @{ Section = 'run'; Name = 'id'; Kind = 'RunId' }
    @{ Section = 'run'; Name = 'testType'; Kind = 'Name'; Allowed = $TestTypes }
    @{ Section = 'run'; Name = 'verdict'; Kind = 'Name'; Allowed = $Verdicts }
    @{ Section = 'run'; Name = 'endReason'; Kind = 'Name'; Allowed = $RunEndReasons }
    @{ Section = 'run'; Name = 'startRequestedUtc'; Kind = 'Timestamp' }
    @{ Section = 'run'; Name = 'startedUtc'; Kind = 'Timestamp' }
    @{ Section = 'run'; Name = 'endedUtc'; Kind = 'Timestamp' }
    @{ Section = 'run'; Name = 'verdictUtc'; Kind = 'Timestamp' }
    @{ Section = 'run'; Name = 'durationSeconds'; Kind = 'OptionalNumber' }
    @{ Section = 'run'; Name = 'timeToVerdictSeconds'; Kind = 'OptionalNumber' }
    @{ Section = 'run'; Name = 'firstErrorSeconds'; Kind = 'OptionalNumber' }
    @{ Section = 'run'; Name = 'recovered'; Kind = 'Flag' }
    @{ Section = 'run'; Name = 'partial'; Kind = 'Flag' }
    @{ Section = 'plan'; Name = 'targetSeconds'; Kind = 'OptionalNumber' }
    @{ Section = 'memory'; Name = 'releaseSeconds'; Kind = 'OptionalNumber' }
    @{ Section = 'memory'; Name = 'testedBytes'; Kind = 'OptionalNumber' }
    @{ Section = 'coverage'; Name = 'percent'; Kind = 'OptionalNumber' }
    @{ Section = 'coverage'; Name = 'targetPercent'; Kind = 'OptionalNumber' }
    @{ Section = 'coverage'; Name = 'targetReached'; Kind = 'OptionalFlag' }
)

$SummaryStepRules = @(
    @{ Name = 'index'; Kind = 'Index' }
    @{ Name = 'name'; Kind = 'Text' }
    @{ Name = 'holdSeconds'; Kind = 'Number' }
    @{ Name = 'actualSeconds'; Kind = 'OptionalNumber' }
    @{ Name = 'errors'; Kind = 'OptionalCount' }
    @{ Name = 'completed'; Kind = 'Flag' }
)

function Get-JsonFieldProblem {
    # The problem with one member of a parsed JSON object, reported under its full JSON path, or
    # $null when the member is present and meets its rule: Name (one of -Allowed, case-sensitive,
    # because enum names are persisted exactly), RunId (32 hex digits, a GUID in N form), Text,
    # Flag, OptionalFlag (also null), Number (a finite JSON number >= 0), OptionalNumber (also null),
    # Index (an integer >= 1), OptionalCount (null or an integer >= 0) or Timestamp (null or ISO 8601
    # UTC).
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Name', 'RunId', 'Text', 'Flag', 'OptionalFlag', 'Number', 'OptionalNumber', 'Index', 'OptionalCount', 'Timestamp')]
        [string]$Kind,
        [AllowNull()][string[]]$Allowed
    )

    $member = Get-JsonMember -Object $Object -Path $Name
    if (-not $member.Present) { return ('{0}: missing' -f $Path) }
    $value = $member.Value
    $isInteger = ($value -is [int] -or $value -is [long])
    switch -CaseSensitive ($Kind) {
        'Name' {
            if ($value -is [string] -and @($Allowed) -ccontains $value) { return $null }
            $expected = 'one of ' + (@($Allowed) -join ', ')
        }
        'RunId' {
            if ($value -is [string] -and $value -match '^[0-9a-fA-F]{32}$') { return $null }
            $expected = 'a run id of 32 hex digits'
        }
        'Text' {
            if ($value -is [string]) { return $null }
            $expected = 'a string'
        }
        'Flag' {
            if ($value -is [bool]) { return $null }
            $expected = 'true or false'
        }
        'OptionalFlag' {
            if ($null -eq $value -or $value -is [bool]) { return $null }
            $expected = 'null, true or false'
        }
        'Number' {
            if ((Test-JsonNumber $value) -and [double]$value -ge 0) { return $null }
            $expected = 'a finite number >= 0'
        }
        'OptionalNumber' {
            if ($null -eq $value -or ((Test-JsonNumber $value) -and [double]$value -ge 0)) { return $null }
            $expected = 'null or a finite number >= 0'
        }
        'Index' {
            if ($isInteger -and $value -ge 1) { return $null }
            $expected = 'an integer >= 1'
        }
        'OptionalCount' {
            if ($null -eq $value -or ($isInteger -and $value -ge 0)) { return $null }
            $expected = 'null or an integer >= 0'
        }
        'Timestamp' {
            if ($null -eq $value -or (Test-JsonTimestamp $value)) { return $null }
            $expected = 'null or an ISO 8601 UTC timestamp'
        }
    }
    return ('{0}: expected {1}, found {2}' -f $Path, $expected, (Format-JsonFound $value))
}

function Get-RunSummaryProblem {
    # Every reason a parsed document is not a schema-1 RAMQuick summary this script can read, each
    # under its JSON path; empty when it is one. Pure, and works on both parser shapes. Only the
    # fields the script reads are judged; errors.* is not, because error detail may be capped or
    # absent and no decision uses it. A -FileName following the published name pattern must agree
    # with run.id and run.testType; any other name skips that check.
    param(
        [AllowNull()][object]$Document,
        [AllowNull()][AllowEmptyString()][string]$FileName
    )

    $problems = New-Object 'System.Collections.Generic.List[string]'
    if (-not (Test-JsonObject $Document)) {
        $problems.Add(('(document): expected a JSON object, found {0}' -f (Format-JsonFound $Document)))
        return , $problems.ToArray()
    }
    $version = Get-JsonMember -Object $Document -Path 'schemaVersion'
    if (-not $version.Present -or -not (Test-JsonNumber $version.Value) -or [double]$version.Value -ne 1) {
        # Every other field is defined by schema 1, so a document of another version is not judged
        # field by field.
        if ($version.Present) { $found = Format-JsonFound $version.Value } else { $found = '(missing)' }
        $problems.Add(('schemaVersion: unsupported schemaVersion {0}; this script reads schema 1' -f $found))
        return , $problems.ToArray()
    }

    $sections = @{}
    foreach ($section in @('run', 'plan', 'memory', 'coverage')) {
        $member = Get-JsonMember -Object $Document -Path $section
        if (-not $member.Present) { $problems.Add(('{0}: missing' -f $section)) }
        elseif (-not (Test-JsonObject $member.Value)) { $problems.Add(('{0}: expected an object, found {1}' -f $section, (Format-JsonFound $member.Value))) }
        else { $sections[$section] = $member.Value }
    }
    foreach ($rule in $SummaryFieldRules) {
        if (-not $sections.ContainsKey($rule.Section)) { continue }
        $allowed = $null
        if ($rule.ContainsKey('Allowed')) { $allowed = $rule.Allowed }
        $problem = Get-JsonFieldProblem -Object $sections[$rule.Section] -Name $rule.Name -Path ($rule.Section + '.' + $rule.Name) -Kind $rule.Kind -Allowed $allowed
        if ($null -ne $problem) { $problems.Add($problem) }
    }

    $steps = Get-JsonMember -Object $Document -Path 'steps'
    if (-not $steps.Present) { $problems.Add('steps: missing') }
    elseif ($steps.Value -isnot [array]) { $problems.Add(('steps: expected an array, found {0}' -f (Format-JsonFound $steps.Value))) }
    else {
        # steps[].index joins a step to its STEP_START/STEP_END lines, so two entries must not share one.
        $positionOfIndex = @{}
        $position = 0
        foreach ($step in $steps.Value) {
            $stepPath = 'steps[{0}]' -f $position
            if (-not (Test-JsonObject $step)) {
                $problems.Add(('{0}: expected an object, found {1}' -f $stepPath, (Format-JsonFound $step)))
            }
            else {
                foreach ($rule in $SummaryStepRules) {
                    $problem = Get-JsonFieldProblem -Object $step -Name $rule.Name -Path ($stepPath + '.' + $rule.Name) -Kind $rule.Kind
                    if ($null -ne $problem) { $problems.Add($problem) }
                }
                $index = (Get-JsonMember -Object $step -Path 'index').Value
                if ($index -is [int] -or $index -is [long]) {
                    $key = [long]$index
                    if ($positionOfIndex.ContainsKey($key)) {
                        $problems.Add(('{0}.index: {1} is also the index of steps[{2}]' -f $stepPath, $key, $positionOfIndex[$key]))
                    }
                    else { $positionOfIndex[$key] = $position }
                }
            }
            $position++
        }
    }

    # Only values that passed their own rule are compared, so one bad value is reported once.
    $nameMatch = $SummaryNamePattern.Match([string]$FileName)
    if ($nameMatch.Success -and $sections.ContainsKey('run')) {
        $id = Get-JsonValue -Object $sections['run'] -Path 'id'
        $nameId = $nameMatch.Groups['id'].Value
        if ($id -is [string] -and $id -match '^[0-9a-fA-F]{32}$' -and -not [string]::Equals($id, $nameId, [StringComparison]::OrdinalIgnoreCase)) {
            $problems.Add(('run.id: {0} differs from the run id {1} in the file name' -f (Format-JsonFound $id), $nameId))
        }
        $type = Get-JsonValue -Object $sections['run'] -Path 'testType'
        $nameType = $nameMatch.Groups['type'].Value
        if ($type -is [string] -and $TestTypes -ccontains $type -and $type -cne $nameType) {
            $problems.Add(('run.testType: {0} differs from the test type {1} in the file name' -f (Format-JsonFound $type), $nameType))
        }
    }
    return , $problems.ToArray()
}

function Import-RunSummaryFile {
    # Reads and checks one summary file. Document is the parsed document and Problems every reason it
    # is not a schema-1 RAMQuick summary of the run its name identifies, empty when it is one. A file
    # that cannot be read or parsed reports that as its problem rather than throwing, so the folder
    # reader can let a <base>-recovered-summary.json beside it supersede it.
    param([Parameter(Mandatory = $true)][string]$Path)

    $parsed = Import-JsonDocument -Path $Path
    if ($null -ne $parsed.Problem) { $problems = [string[]]@($parsed.Problem) }
    else { $problems = Get-RunSummaryProblem -Document $parsed.Document -FileName ([IO.Path]::GetFileName($Path)) }
    return [pscustomobject]@{ Path = $Path; Document = $parsed.Document; Problems = $problems }
}

function Get-LogPathForSummary {
    # <base>-summary.json and <base>-recovered-summary.json both belong to <base>.log.
    param([Parameter(Mandatory = $true)][string]$SummaryPath)

    $folder = Split-Path -Parent $SummaryPath
    $name = Split-Path -Leaf $SummaryPath
    if ($name -like '*-recovered-summary.json') {
        $base = $name.Substring(0, $name.Length - '-recovered-summary.json'.Length)
    }
    elseif ($name -like '*-summary.json') {
        $base = $name.Substring(0, $name.Length - '-summary.json'.Length)
    }
    else {
        $base = [IO.Path]::GetFileNameWithoutExtension($name)
    }
    return (Join-Path $folder ($base + '.log'))
}

function ConvertFrom-RunSummaryDocument {
    # The fields of one summary this script uses, with its .log events. The document must be one
    # Get-RunSummaryProblem accepted: the Get-Json* readers coerce what they are given, so only that
    # check keeps a mistyped or missing field from becoming a plausible value. Error records are
    # never read: the verdict, the run.* timing fields and steps[].errors carry everything needed.
    param(
        [Parameter(Mandatory = $true)][object]$Document,
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [Parameter(Mandatory = $true)][string]$ProfileName
    )

    $steps = New-Object 'System.Collections.Generic.List[object]'
    foreach ($step in (Get-JsonArray $Document 'steps')) {
        $index = Get-JsonNumber $step 'index'
        $steps.Add([pscustomobject]@{
                Index         = [int]$index
                Name          = (Get-JsonString $step 'name')
                HoldSeconds   = (Get-JsonNumber $step 'holdSeconds')
                ActualSeconds = (Get-JsonNumber $step 'actualSeconds')
                Errors        = (Get-JsonNumber $step 'errors')
            })
    }
    $orderedSteps = @($steps | Sort-Object Index)
    $firstErrorStep = $null
    foreach ($step in $orderedSteps) {
        if ($null -ne $step.Errors -and $step.Errors -gt 0) { $firstErrorStep = $step; break }
    }

    $logPath = Get-LogPathForSummary -SummaryPath $SummaryPath
    return [pscustomobject]@{
        SummaryPath           = $SummaryPath
        FileName              = (Split-Path -Leaf $SummaryPath)
        Profile               = $ProfileName
        Ordinal               = 0
        Id                    = (Get-JsonString $Document 'run.id')
        TestType              = (Get-JsonString $Document 'run.testType')
        Verdict               = (Get-JsonString $Document 'run.verdict')
        EndReason             = (Get-JsonString $Document 'run.endReason')
        StartRequestedUtc     = (ConvertFrom-JsonTimestamp (Get-JsonValue $Document 'run.startRequestedUtc'))
        StartedUtc            = (ConvertFrom-JsonTimestamp (Get-JsonValue $Document 'run.startedUtc'))
        EndedUtc              = (ConvertFrom-JsonTimestamp (Get-JsonValue $Document 'run.endedUtc'))
        VerdictUtc            = (ConvertFrom-JsonTimestamp (Get-JsonValue $Document 'run.verdictUtc'))
        DurationSeconds       = (Get-JsonNumber $Document 'run.durationSeconds')
        TimeToVerdictSeconds  = (Get-JsonNumber $Document 'run.timeToVerdictSeconds')
        FirstErrorSeconds     = (Get-JsonNumber $Document 'run.firstErrorSeconds')
        Recovered             = (Get-JsonBool $Document 'run.recovered')
        Partial               = (Get-JsonBool $Document 'run.partial')
        TargetSeconds         = (Get-JsonNumber $Document 'plan.targetSeconds')
        CoveragePercent       = (Get-JsonNumber $Document 'coverage.percent')
        CoverageTargetPercent = (Get-JsonNumber $Document 'coverage.targetPercent')
        CoverageTargetReached = (Get-JsonBool $Document 'coverage.targetReached')
        ReleaseSeconds        = (Get-JsonNumber $Document 'memory.releaseSeconds')
        TestedBytes           = (Get-JsonNumber $Document 'memory.testedBytes')
        Steps                 = $orderedSteps
        FirstErrorStep        = $firstErrorStep
        Log                   = (Import-RunLog -Path $logPath)
    }
}

function Import-RunSummaryFolder {
    # Every summary in a folder (not recursive; *.tmp files are unpublished intermediates and not
    # read), ordered by run.startRequestedUtc, with Ordinal numbering the runs of each test type
    # from 1. Supplied evidence that cannot be used is an input error, never dropped: removing a
    # corrupt run could leave the surviving subset meeting a criterion. So a missing folder, a folder
    # without summaries, and any file that is not a schema-1 RAMQuick summary of the run its name
    # identifies throw, the last listing every such file with all its problems. The one exception
    # is a <base>-summary.json superseded by a <base>-recovered-summary.json beside it.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ProfileName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw ('The {0} runs folder was not found: {1}' -f $ProfileName, $Path)
    }
    $folder = (Resolve-Path -LiteralPath $Path).ProviderPath
    $files = @(Get-ChildItem -LiteralPath $folder -File |
            Where-Object { $_.Name -like '*-summary.json' } |
            Sort-Object Name)
    if ($files.Count -eq 0) {
        throw ('No RAMQuick summaries (*-summary.json) were found in the {0} runs folder {1}.' -f $ProfileName, $folder)
    }

    # Every file is checked before any is used, so one error names every unusable summary.
    $accepted = New-Object 'System.Collections.Generic.List[object]'
    $rejected = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in $files) {
        $check = Import-RunSummaryFile -Path $file.FullName
        if ($check.Problems.Count -eq 0) {
            $accepted.Add($check)
            continue
        }
        # Crash recovery leaves a final summary that does not parse or names another run untouched
        # and publishes the rebuilt one as <base>-recovered-summary.json, which then stands for the
        # run. The recovered file is checked on its own like every other.
        $replacement = $null
        if ($file.Name -notlike '*-recovered-summary.json') {
            $replacement = Join-Path $folder ($file.Name.Substring(0, $file.Name.Length - '-summary.json'.Length) + '-recovered-summary.json')
        }
        if ($null -ne $replacement -and (Test-Path -LiteralPath $replacement -PathType Leaf)) {
            Write-Warning ('Skipping {0}: {1}. The recovered summary {2} supersedes it.' -f $file.FullName, ($check.Problems -join '; '), $replacement)
            continue
        }
        $rejected.Add(('  {0}: {1}' -f $file.FullName, ($check.Problems -join '; ')))
    }
    if ($rejected.Count -gt 0) {
        throw (('The {0} runs folder {1} holds {2} file(s) that are not schema-1 RAMQuick summaries of the runs their names identify:' -f $ProfileName, $folder, $rejected.Count) +
            [Environment]::NewLine + ($rejected.ToArray() -join [Environment]::NewLine))
    }

    $byId = @{}
    $runs = New-Object 'System.Collections.Generic.List[object]'
    foreach ($check in $accepted) {
        $run = ConvertFrom-RunSummaryDocument -Document $check.Document -SummaryPath $check.Path -ProfileName $ProfileName
        if (-not $run.Log.Exists) {
            Write-Warning ('{0}: its event log {1} was not found; the checks that need it are indeterminate.' -f $run.FileName, $run.Log.Path)
        }

        # A run's summary and a recovered summary with the same run id describe one run; the
        # published summary is kept.
        $key = [string]$run.Id
        if ($key.Length -gt 0 -and $byId.ContainsKey($key)) {
            $kept = $byId[$key]
            Write-Warning ('{0} and {1} describe the same run {2}; only one is counted.' -f $kept.FileName, $run.FileName, $key)
            if (($kept.FileName -like '*-recovered-summary.json') -and -not ($run.FileName -like '*-recovered-summary.json')) {
                [void]$runs.Remove($kept)
                $runs.Add($run)
                $byId[$key] = $run
            }
            continue
        }
        if ($key.Length -gt 0) { $byId[$key] = $run }
        $runs.Add($run)
    }

    $ordered = @($runs | Sort-Object @{ Expression = { if ($null -eq $_.StartRequestedUtc) { 1 } else { 0 } } },
        @{ Expression = { if ($null -eq $_.StartRequestedUtc) { [long]0 } else { $_.StartRequestedUtc.UtcTicks } } },
        @{ Expression = { $_.FileName } })
    $counters = @{}
    foreach ($run in $ordered) {
        $type = [string]$run.TestType
        if (-not $counters.ContainsKey($type)) { $counters[$type] = 0 }
        $counters[$type] = $counters[$type] + 1
        $run.Ordinal = $counters[$type]
    }
    return , $ordered
}

function Get-RunsOfType {
    param([AllowEmptyCollection()][object[]]$Runs, [Parameter(Mandatory = $true)][string]$Type)

    return , @(@($Runs) | Where-Object { $null -ne $_ -and [string]$_.TestType -ceq $Type })
}

function Format-RunLabel {
    param([Parameter(Mandatory = $true)][object]$Run)

    return ('{0} ({1} #{2})' -f $Run.TestType, $Run.Profile, $Run.Ordinal)
}

# ---------------------------------------------------------------------------------------------
# Readers: baseline CSV
# ---------------------------------------------------------------------------------------------

function ConvertFrom-BaselineMinutes {
    # Applies the baseline censoring rule to one run: no error by 120 min is right-censored at 120,
    # any value above 120 (or written "> t" with t above 120) is censored at 120, "> t" and
    # censored = 1 with a smaller value are censored at that value. Throws on unreadable cells.
    param(
        [AllowNull()][AllowEmptyString()][string]$MinutesText,
        [AllowNull()][AllowEmptyString()][string]$CensoredText
    )

    $flagText = ([string]$CensoredText).Trim()
    if ($flagText -eq '1' -or $flagText -ieq 'true') { $censored = $true }
    elseif ($flagText -eq '0' -or $flagText -ieq 'false') { $censored = $false }
    else { throw ('censored must be 1, 0, true or false, not "{0}".' -f $flagText) }

    $text = ([string]$MinutesText).Trim()
    $greaterThan = $text.StartsWith('>')
    if ($greaterThan) { $text = $text.Substring(1).Trim() }
    if ($text.Length -eq 0) {
        if (-not ($censored -or $greaterThan)) { throw 'minutesToFirstError is empty for a run that is not censored.' }
        $text = $BaselineCensorMinutes.ToString('R', $Invariant)
    }
    $minutes = ConvertFrom-InvariantNumber $text
    if ($null -eq $minutes -or [double]::IsNaN($minutes) -or [double]::IsInfinity($minutes) -or $minutes -lt 0) {
        throw ('minutesToFirstError "{0}" is not a non-negative number.' -f $MinutesText)
    }

    if ($minutes -gt $BaselineCensorMinutes) {
        return [pscustomobject]@{ Value = $BaselineCensorMinutes; Censored = $true }
    }
    return [pscustomobject]@{ Value = $minutes; Censored = ($censored -or $greaterThan) }
}

function ConvertFrom-BaselineRows {
    # Baseline CSV rows (tool, run, minutesToFirstError, censored, optional configuration) as runs
    # with Value (minutes) and Censored. Throws, naming the row, on a missing or unreadable cell.
    param([AllowEmptyCollection()][object[]]$Rows)

    $runs = New-Object 'System.Collections.Generic.List[object]'
    $line = 1
    foreach ($row in @($Rows)) {
        $line++
        $tool = Get-JsonString $row 'tool'
        if ([string]::IsNullOrWhiteSpace($tool)) { throw ('Baseline CSV row {0}: the tool cell is empty.' -f $line) }
        try {
            $value = ConvertFrom-BaselineMinutes -MinutesText (Get-JsonString $row 'minutesToFirstError') -CensoredText (Get-JsonString $row 'censored')
        }
        catch {
            throw ('Baseline CSV row {0}: {1}' -f $line, $_.Exception.Message)
        }
        $configuration = Get-JsonString $row 'configuration'
        if ([string]::IsNullOrWhiteSpace($configuration)) { $configuration = $Sym.Dash }
        $runs.Add([pscustomobject]@{
                Tool          = $tool.Trim()
                Configuration = $configuration.Trim()
                Run           = ([string](Get-JsonString $row 'run')).Trim()
                Value         = $value.Value
                Censored      = $value.Censored
            })
    }
    return , $runs.ToArray()
}

function Import-BaselineCsv {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('The baseline CSV was not found: {0}' -f $Path) }
    try {
        $rows = @(Import-Csv -LiteralPath $Path)
    }
    catch {
        throw ('The baseline CSV {0} could not be read: {1}' -f $Path, $_.Exception.Message)
    }
    if ($rows.Count -eq 0) { throw ('The baseline CSV {0} holds no runs.' -f $Path) }
    $columns = @($rows[0].PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($required in @('tool', 'run', 'minutesToFirstError', 'censored')) {
        if (-not ($columns -contains $required)) {
            throw ('The baseline CSV {0} lacks the column "{1}"; required: tool,run,minutesToFirstError,censored.' -f $Path, $required)
        }
    }
    return , (ConvertFrom-BaselineRows -Rows $rows)
}

# ---------------------------------------------------------------------------------------------
# Readers: HWiNFO64 CSV logs
# ---------------------------------------------------------------------------------------------

function ConvertFrom-CsvLine {
    # Splits one CSV line; quoted cells may hold the delimiter, and "" stands for a quote.
    param([AllowNull()][AllowEmptyString()][string]$Line, [char]$Delimiter = ',')

    $text = [string]$Line
    if ($text.IndexOf('"') -lt 0) { return , $text.Split($Delimiter) }
    $d = [regex]::Escape([string]$Delimiter)
    $pattern = '\G(?:^|' + $d + ')(?:"((?:[^"]|"")*)"|([^' + $d + '"]*))'
    $cells = New-Object 'System.Collections.Generic.List[string]'
    foreach ($match in [regex]::Matches($text, $pattern)) {
        if ($match.Groups[1].Success) { $cells.Add($match.Groups[1].Value.Replace('""', '"')) }
        else { $cells.Add($match.Groups[2].Value) }
    }
    return , $cells.ToArray()
}

function Get-HwInfoUnitConversion {
    # Scale and offset that convert a column, by the unit in its "[...]" header suffix, to GB/s, W
    # or degrees Celsius. Known is $false when the unit is not recognised and values stay as logged.
    param([Parameter(Mandatory = $true)][string]$MetricKey, [Parameter(Mandatory = $true)][string]$Header)

    $unitMatch = [regex]::Match($Header, '\[([^\]]*)\]\s*$')
    $unit = ''
    if ($unitMatch.Success) { $unit = $unitMatch.Groups[1].Value.Trim() }
    $lower = $unit.ToLowerInvariant()
    switch ($MetricKey) {
        'Bandwidth' {
            switch ($lower) {
                'gb/s' { return [pscustomobject]@{ Scale = 1.0; Offset = 0.0; Known = $true; Unit = $unit } }
                'mb/s' { return [pscustomobject]@{ Scale = 0.001; Offset = 0.0; Known = $true; Unit = $unit } }
                'kb/s' { return [pscustomobject]@{ Scale = 0.000001; Offset = 0.0; Known = $true; Unit = $unit } }
            }
        }
        'PackagePower' {
            switch ($lower) {
                'w' { return [pscustomobject]@{ Scale = 1.0; Offset = 0.0; Known = $true; Unit = $unit } }
                'mw' { return [pscustomobject]@{ Scale = 0.001; Offset = 0.0; Known = $true; Unit = $unit } }
            }
        }
        'DimmTemperature' {
            if ($unit.EndsWith('F')) { return [pscustomobject]@{ Scale = (5.0 / 9.0); Offset = (-160.0 / 9.0); Known = $true; Unit = $unit } }
            if ($unit.EndsWith('C')) { return [pscustomobject]@{ Scale = 1.0; Offset = 0.0; Known = $true; Unit = $unit } }
        }
    }
    return [pscustomobject]@{ Scale = 1.0; Offset = 0.0; Known = $false; Unit = $unit }
}

function ConvertFrom-HwInfoLocalTime {
    # HWiNFO logs local wall-clock time; a fixed offset, or this machine's time zone, makes it UTC.
    # $null for a local time that does not exist (the spring-forward gap).
    param([Parameter(Mandatory = $true)][DateTime]$Local, [AllowNull()][object]$UtcOffsetMinutes)

    $unspecified = [DateTime]::SpecifyKind($Local, [DateTimeKind]::Unspecified)
    if ($null -ne $UtcOffsetMinutes) {
        return ([DateTimeOffset]::new($unspecified, [TimeSpan]::FromMinutes([double]$UtcOffsetMinutes))).ToUniversalTime()
    }
    try {
        $utc = [TimeZoneInfo]::ConvertTimeToUtc($unspecified, [TimeZoneInfo]::Local)
        return [DateTimeOffset]::new([DateTime]::SpecifyKind($utc, [DateTimeKind]::Utc))
    }
    catch {
        return $null
    }
}

function ConvertFrom-HwInfoText {
    # Parses the lines of an HWiNFO64 CSV log into 1-s samples of the three stress-bound metrics.
    # Columns 0 and 1 are Date and Time; rows whose Date and Time do not parse (the repeated header
    # and statistics rows HWiNFO appends) are skipped. Columns are matched by wildcard against the
    # header text, which may repeat names across sensors. A metric with no matching column is
    # recorded in Missing. Selections keeps the identity of every selected column (header name,
    # its position among the columns of that name, the sensor named in HWiNFO's trailing row with
    # blank Date and Time, and the unit), so the references and the run can be shown to log the
    # same sensors. A log whose timestamps do not increase, or whose median interval, or fewer than
    # 90 % of whose intervals, are not the 1-s polling both sides must share, has every metric
    # recorded in Missing: its samples are not comparable evidence. A metric whose selected
    # columns hold no number in a row has no value in that row.
    param(
        [AllowEmptyCollection()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$BandwidthPatterns,
        [Parameter(Mandatory = $true)][string[]]$PackagePowerPatterns,
        [Parameter(Mandatory = $true)][string[]]$DimmTemperaturePatterns,
        [AllowNull()][object]$UtcOffsetMinutes
    )

    $result = [pscustomobject]@{
        Name                  = $Name
        Samples               = (New-Object 'System.Collections.Generic.List[object]')
        Missing               = @{}
        Columns               = @{}
        Selections            = @{}
        UnknownUnits          = (New-Object 'System.Collections.Generic.List[string]')
        MedianIntervalSeconds = $null
        SkippedRows           = 0
    }

    $lineCount = @($Lines).Count
    $headerIndex = -1
    for ($i = 0; $i -lt $lineCount; $i++) {
        if (-not [string]::IsNullOrWhiteSpace($Lines[$i])) { $headerIndex = $i; break }
    }
    if ($headerIndex -lt 0) {
        foreach ($metric in $StressMetrics) { $result.Missing[$metric.Key] = 'the log is empty' }
        return $result
    }

    $headerLine = $Lines[$headerIndex]
    $unquoted = [regex]::Replace($headerLine, '"[^"]*"', '')
    $delimiter = [char]','
    if (($unquoted.Split(';').Count) -gt ($unquoted.Split(',').Count)) { $delimiter = [char]';' }
    $header = [string[]]@(foreach ($cell in (ConvertFrom-CsvLine -Line $headerLine -Delimiter $delimiter)) { $cell.Trim() })

    # Column selection: every bandwidth pattern contributes its first matching column (read +
    # write), the package power is the first match of the first matching pattern, and the DIMM
    # temperature is the maximum over every column that matches any pattern. A row has a value of
    # a metric only when every selected column of it holds a number: a sum without its write part
    # is not the bandwidth, and a maximum without one DIMM is the maximum of the other DIMMs only,
    # which may be cooler. A column empty throughout therefore leaves the metric missing, and
    # sporadic empty cells count against the metric's time coverage.
    $selection = @{}
    $bandwidthIndexes = New-Object 'System.Collections.Generic.List[int]'
    foreach ($pattern in $BandwidthPatterns) {
        $found = -1
        for ($c = 2; $c -lt $header.Count; $c++) { if ($header[$c] -like $pattern) { $found = $c; break } }
        if ($found -lt 0) {
            $result.Missing['Bandwidth'] = ('no column matches "{0}"' -f $pattern)
        }
        else { $bandwidthIndexes.Add($found) }
    }
    $selection['Bandwidth'] = $bandwidthIndexes

    $powerColumns = New-Object 'System.Collections.Generic.List[int]'
    foreach ($pattern in $PackagePowerPatterns) {
        for ($c = 2; $c -lt $header.Count; $c++) { if ($header[$c] -like $pattern) { $powerColumns.Add($c); break } }
        if ($powerColumns.Count -gt 0) { break }
    }
    if ($powerColumns.Count -eq 0) {
        $result.Missing['PackagePower'] = ('no column matches "{0}"' -f ($PackagePowerPatterns -join '", "'))
    }
    $selection['PackagePower'] = $powerColumns

    $temperatureColumns = New-Object 'System.Collections.Generic.List[int]'
    for ($c = 2; $c -lt $header.Count; $c++) {
        foreach ($pattern in $DimmTemperaturePatterns) {
            if ($header[$c] -like $pattern) { $temperatureColumns.Add($c); break }
        }
    }
    if ($temperatureColumns.Count -eq 0) {
        $result.Missing['DimmTemperature'] = ('no column matches "{0}"' -f ($DimmTemperaturePatterns -join '", "'))
    }
    $selection['DimmTemperature'] = $temperatureColumns

    $conversions = @{}
    foreach ($metric in $StressMetrics) {
        $names = New-Object 'System.Collections.Generic.List[string]'
        foreach ($c in $selection[$metric.Key]) {
            $conversions[('{0}:{1}' -f $metric.Key, $c)] = Get-HwInfoUnitConversion -MetricKey $metric.Key -Header $header[$c]
            if (-not $conversions[('{0}:{1}' -f $metric.Key, $c)].Known) {
                $result.UnknownUnits.Add(('{0} "{1}"' -f $metric.Label, $header[$c]))
            }
            $names.Add($header[$c])
        }
        $result.Columns[$metric.Key] = $names.ToArray()
    }

    $decimalComma = ($delimiter -eq [char]';')
    $numericCount = @{ Bandwidth = 0; PackagePower = 0; DimmTemperature = 0 }
    $columnNumericCount = @{}
    foreach ($cellKey in @($conversions.Keys)) { $columnNumericCount[$cellKey] = 0 }
    $sensorCells = $null
    $previousUtc = $null
    $notIncreasingAt = $null
    for ($i = $headerIndex + 1; $i -lt $lineCount; $i++) {
        $line = $Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $cells = ConvertFrom-CsvLine -Line $line -Delimiter $delimiter
        if ($cells.Count -lt 2) { $result.SkippedRows++; continue }
        $local = [DateTime]::MinValue
        $stampText = $cells[0].Trim() + ' ' + $cells[1].Trim()
        if (-not [DateTime]::TryParseExact($stampText, $HwInfoTimestampFormats, $Invariant, [Globalization.DateTimeStyles]::None, [ref]$local)) {
            # HWiNFO ends its log with a row whose Date and Time are blank and whose cells name the
            # sensor of each column; the last such row is the one that labels the columns.
            if ($cells[0].Trim().Length -eq 0 -and $cells[1].Trim().Length -eq 0) { $sensorCells = $cells }
            $result.SkippedRows++
            continue
        }
        $utc = ConvertFrom-HwInfoLocalTime -Local $local -UtcOffsetMinutes $UtcOffsetMinutes
        if ($null -eq $utc) { $result.SkippedRows++; continue }
        if ($null -ne $previousUtc -and $utc -le $previousUtc -and $null -eq $notIncreasingAt) { $notIncreasingAt = $stampText }
        $previousUtc = $utc

        $values = @{}
        foreach ($metric in $StressMetrics) {
            $key = $metric.Key
            $parts = New-Object 'System.Collections.Generic.List[double]'
            foreach ($c in $selection[$key]) {
                if ($c -ge $cells.Count) { continue }
                $cellText = $cells[$c].Trim()
                if ($decimalComma) { $cellText = $cellText.Replace(',', '.') }
                $number = ConvertFrom-InvariantNumber $cellText
                # NaN or Infinity text is no measurement; as a number it would poison every rolling
                # sum that follows it, so the cell counts as empty.
                if ($null -eq $number -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) { continue }
                $cellKey = ('{0}:{1}' -f $key, $c)
                $columnNumericCount[$cellKey] = $columnNumericCount[$cellKey] + 1
                $conversion = $conversions[$cellKey]
                $parts.Add($number * $conversion.Scale + $conversion.Offset)
            }
            $value = $null
            if ($key -eq 'Bandwidth') {
                if ($selection[$key].Count -gt 0 -and $parts.Count -eq $selection[$key].Count -and -not $result.Missing.ContainsKey($key)) {
                    $value = 0.0
                    foreach ($part in $parts) { $value += $part }
                }
            }
            elseif ($parts.Count -gt 0 -and $parts.Count -eq $selection[$key].Count) {
                $value = $parts[0]
                foreach ($part in $parts) { if ($part -gt $value) { $value = $part } }
            }
            if ($null -ne $value) { $numericCount[$key] = $numericCount[$key] + 1 }
            $values[$key] = $value
        }
        $result.Samples.Add([pscustomobject]@{
                Utc             = $utc
                Bandwidth       = $values['Bandwidth']
                PackagePower    = $values['PackagePower']
                DimmTemperature = $values['DimmTemperature']
            })
    }

    # Column identities. Name is the header without its unit suffix; Ordinal counts the columns of
    # that name from 1, left to right, because HWiNFO repeats a name once per sensor (one SPD Hub
    # Temperature per DIMM); Sensor is $null when the log has no HWiNFO sensor-name row.
    $ordinals = @{}
    $ordinalOf = @{}
    for ($c = 2; $c -lt $header.Count; $c++) {
        $columnName = ([regex]::Replace($header[$c], '\s*\[[^\]]*\]\s*$', '')).Trim()
        $nameKey = $columnName.ToUpperInvariant()
        if ($ordinals.ContainsKey($nameKey)) { $ordinals[$nameKey] = $ordinals[$nameKey] + 1 } else { $ordinals[$nameKey] = 1 }
        $ordinalOf[$c] = [pscustomobject]@{ Name = $columnName; Ordinal = $ordinals[$nameKey] }
    }
    foreach ($metric in $StressMetrics) {
        $identities = New-Object 'System.Collections.Generic.List[object]'
        $emptyColumns = New-Object 'System.Collections.Generic.List[object]'
        foreach ($c in $selection[$metric.Key]) {
            $sensor = $null
            if ($null -ne $sensorCells) {
                $sensor = ''
                if ($c -lt $sensorCells.Count) { $sensor = $sensorCells[$c].Trim() }
            }
            $cellKey = ('{0}:{1}' -f $metric.Key, $c)
            $conversion = $conversions[$cellKey]
            $identity = [pscustomobject]@{
                Name    = $ordinalOf[$c].Name
                Ordinal = $ordinalOf[$c].Ordinal
                Sensor  = $sensor
                Unit    = $conversion.Unit
                Known   = $conversion.Known
            }
            $identities.Add($identity)
            if ($columnNumericCount[$cellKey] -eq 0) { $emptyColumns.Add($identity) }
        }
        $result.Selections[$metric.Key] = $identities.ToArray()

        # No row with a number in every selected column: name the columns that never hold one, so
        # a DIMM sensor that stopped reporting is identified, or else say that the columns never
        # report in the same row.
        if (-not $result.Missing.ContainsKey($metric.Key) -and $numericCount[$metric.Key] -eq 0) {
            if ($emptyColumns.Count -gt 0) {
                $result.Missing[$metric.Key] = ('no numeric value in column(s) {0}' -f (Format-HwInfoSelection -Selection $emptyColumns.ToArray()))
            }
            else {
                $result.Missing[$metric.Key] = ('no row holds a numeric value in every one of the columns {0}' -f (Format-HwInfoSelection -Selection $identities.ToArray()))
            }
        }
    }

    # 1 s +- 10 %, inclusive at both ends (the 1e-9 s absorbs the rounding of tick differences).
    $intervalTolerance = $SamplingTolerance * $ExpectedSamplingSeconds + 1e-9
    $intervalCount = 0
    $regularIntervals = 0
    if ($result.Samples.Count -ge 2) {
        $intervals = New-Object 'System.Collections.Generic.List[double]'
        for ($i = 1; $i -lt $result.Samples.Count; $i++) {
            $interval = ($result.Samples[$i].Utc - $result.Samples[$i - 1].Utc).TotalSeconds
            $intervals.Add($interval)
            if ([Math]::Abs($interval - $ExpectedSamplingSeconds) -le $intervalTolerance) { $regularIntervals++ }
        }
        $intervalCount = $intervals.Count
        $result.MedianIntervalSeconds = Get-Median -Values $intervals.ToArray()
    }

    # Both sides of the comparison must share 1-s polling and a time axis that only moves forward:
    # a rolling 10-s window is defined by time, so samples at another cadence, or out of order, give
    # statistics that are not comparable with the other logs. The median alone does not show the
    # polling: a log at 1 s for half its intervals and at 100 ms for the rest has a 1-s median, so
    # at least 90 % of the intervals must themselves be 1 s +- 10 %.
    $cadenceProblem = $null
    if ($result.Samples.Count -lt 2) {
        $cadenceProblem = ('{0} row(s) with a readable Date and Time, too few to show the 1-s polling both sides must share' -f $result.Samples.Count)
    }
    elseif ($null -ne $notIncreasingAt) {
        $cadenceProblem = ('timestamps are not strictly increasing at {0}; check -HwInfoUtcOffsetMinutes or a daylight-saving change' -f $notIncreasingAt)
    }
    elseif ([Math]::Abs($result.MedianIntervalSeconds - $ExpectedSamplingSeconds) -gt $intervalTolerance) {
        $cadenceProblem = ('sampled every {0} s, not at the 1-s polling both sides must share' -f (Format-Number $result.MedianIntervalSeconds 2))
    }
    elseif ($regularIntervals -lt [Math]::Ceiling((1.0 - $SamplingTolerance) * $intervalCount - 1e-9)) {
        $sharePercent = [Math]::Floor(100.0 * $regularIntervals / $intervalCount)
        $polling = Format-Number -Value $ExpectedSamplingSeconds -Decimals 0
        $tolerancePercent = Format-Number -Value (100.0 * $SamplingTolerance) -Decimals 0
        $cadenceProblem = ('only {0} % of its sampling intervals are {1} s {2}{3} % (median {4} s); both sides must share {1}-s polling' -f
            $sharePercent, $polling, $Sym.PlusMinus, $tolerancePercent, (Format-Number -Value $result.MedianIntervalSeconds -Decimals 2))
    }
    if ($null -ne $cadenceProblem) {
        foreach ($metric in $StressMetrics) {
            if (-not $result.Missing.ContainsKey($metric.Key)) { $result.Missing[$metric.Key] = $cadenceProblem }
        }
    }
    return $result
}

function Import-HwInfoLog {
    # Reads an HWiNFO64 CSV log (ANSI, as HWiNFO writes it) and warns about every metric it cannot
    # supply as comparable evidence, a polling interval other than 1 s included, and about units it
    # does not recognise.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowNull()][object]$UtcOffsetMinutes
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('The HWiNFO64 log was not found: {0}' -f $Path) }
    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    try {
        $lines = [IO.File]::ReadAllLines($fullPath, [Text.Encoding]::Default)
    }
    catch {
        throw ('The HWiNFO64 log {0} could not be read: {1}' -f $fullPath, $_.Exception.Message)
    }
    $name = [IO.Path]::GetFileNameWithoutExtension($fullPath)
    $log = ConvertFrom-HwInfoText -Lines $lines -Name $name -BandwidthPatterns $BandwidthColumns `
        -PackagePowerPatterns $PackagePowerColumns -DimmTemperaturePatterns $DimmTemperatureColumns -UtcOffsetMinutes $UtcOffsetMinutes

    if ($log.Samples.Count -eq 0) {
        Write-Warning ('{0}: no row has a readable Date and Time.' -f $fullPath)
    }
    foreach ($unknown in $log.UnknownUnits) {
        Write-Warning ('{0}: the unit of {1} is not recognised; its values are used as logged.' -f $fullPath, $unknown)
    }
    foreach ($key in @($log.Missing.Keys)) {
        Write-Warning ('{0}: {1} is missing ({2}).' -f $fullPath, (Get-StressMetricLabel $key), $log.Missing[$key])
    }
    return $log
}


# ---------------------------------------------------------------------------------------------
# Decision rules (pure; each is covered by -SelfTest)
# ---------------------------------------------------------------------------------------------

function Get-Median {
    # The sample median; an even count averages the two middle values, so +Infinity in either gives
    # +Infinity. $null for no values.
    param([AllowNull()][AllowEmptyCollection()][double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = [double[]]$Values.Clone()
    [Array]::Sort($sorted)
    $count = $sorted.Count
    if ($count % 2 -eq 1) { return $sorted[[int][Math]::Floor($count / 2)] }
    $upper = [int]($count / 2)
    return (($sorted[$upper - 1] + $sorted[$upper]) / 2.0)
}

function Get-MedianInterval {
    # The interval [L, U] that must contain the sample median of the true failure times. L puts every
    # censored run at its censoring time, U puts it at +Infinity, so U is +Infinity whenever a
    # censored run reaches the median position. Censored values are never ranked against observed
    # ones beyond this. Each element of Values carries Value and Censored.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Values)

    $items = @(@($Values) | Where-Object { $null -ne $_ -and $null -ne $_.Value })
    $censoredCount = @($items | Where-Object { [bool]$_.Censored }).Count
    if ($items.Count -eq 0) {
        return [pscustomobject]@{ L = $null; U = $null; N = 0; CensoredCount = 0 }
    }
    $low = [double[]]@(foreach ($item in $items) { [double]$item.Value })
    $high = [double[]]@(foreach ($item in $items) {
            if ([bool]$item.Censored) { [double]::PositiveInfinity } else { [double]$item.Value }
        })
    return [pscustomobject]@{
        L             = (Get-Median -Values $low)
        U             = (Get-Median -Values $high)
        N             = $items.Count
        CensoredCount = $censoredCount
    }
}

function Get-FailTimeObservation {
    # One failing-profile run as a time-to-FAIL observation, from run.verdict and
    # run.timeToVerdictSeconds only. FAIL is observed; PASS or ABORTED is right-censored at its
    # time to verdict and is a miss. A FAIL without a verdict time (recovered from an unfinished
    # journal) is excluded from the medians and, conservatively, also counts as a miss, because an
    # unknown time never counts as within 5 minutes.
    param([Parameter(Mandatory = $true)][object]$Run)

    $verdict = ([string]$Run.Verdict).ToUpperInvariant()
    $seconds = $Run.TimeToVerdictSeconds
    if ($null -eq $seconds -or ($verdict -cne 'FAIL' -and $verdict -cne 'PASS' -and $verdict -cne 'ABORTED')) {
        return [pscustomobject]@{ Kind = 'excluded'; Value = $null; Censored = $false; Within = $false }
    }
    if ($verdict -ceq 'FAIL') {
        return [pscustomobject]@{ Kind = 'observed'; Value = [double]$seconds; Censored = $false; Within = ([double]$seconds -le $FailWithinSeconds) }
    }
    return [pscustomobject]@{ Kind = 'censored'; Value = [double]$seconds; Censored = $true; Within = $false }
}

function Get-WithinFiveMinutesDecision {
    # "Reports FAIL within 5 minutes in at least 9 of 10 runs": n counts every run of the type,
    # excluded runs included, and k the FAIL runs with a time to verdict of at most 300 s. With at
    # least 10 runs the check is met when k >= ceil(0.9 n). With fewer, two misses already decide
    # it as not met; otherwise it is indeterminate.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Observations)

    $items = @(@($Observations) | Where-Object { $null -ne $_ })
    $n = $items.Count
    $k = @($items | Where-Object { [bool]$_.Within }).Count
    $misses = $n - $k
    if ($n -ge $FastRunMinimumRuns) {
        if ($k -ge [Math]::Ceiling($FastRunFraction * $n)) { $result = $Decision.Met } else { $result = $Decision.NotMet }
    }
    elseif ($misses -ge 2) { $result = $Decision.NotMet }
    else { $result = $Decision.Indeterminate }
    return [pscustomobject]@{ Decision = $result; N = $n; K = $k; Misses = $misses }
}

function Get-FastestBaseline {
    # Per tool, the median interval of its minutes to first error; the fastest baseline median B is
    # [min L, min U] across tools. The deciding tool is the one attaining min L (and min U).
    param([AllowNull()][AllowEmptyCollection()][object[]]$Runs)

    $order = New-Object 'System.Collections.Generic.List[string]'
    $byTool = @{}
    foreach ($run in @($Runs)) {
        if ($null -eq $run) { continue }
        $key = ([string]$run.Tool).ToLowerInvariant()
        if (-not $byTool.ContainsKey($key)) {
            $byTool[$key] = New-Object 'System.Collections.Generic.List[object]'
            $order.Add($key)
        }
        $byTool[$key].Add($run)
    }

    $tools = New-Object 'System.Collections.Generic.List[object]'
    foreach ($key in $order) {
        $toolRuns = $byTool[$key].ToArray()
        $interval = Get-MedianInterval -Values $toolRuns
        $tools.Add([pscustomobject]@{
                Tool         = $toolRuns[0].Tool
                Runs         = $toolRuns.Count
                CensoredRuns = $interval.CensoredCount
                L            = $interval.L
                U            = $interval.U
            })
    }

    $bestLow = $null
    $bestHigh = $null
    foreach ($tool in $tools) {
        if ($null -eq $tool.L) { continue }
        if ($null -eq $bestLow -or $tool.L -lt $bestLow.L) { $bestLow = $tool }
        if ($null -eq $bestHigh -or $tool.U -lt $bestHigh.U) { $bestHigh = $tool }
    }
    if ($null -eq $bestLow) {
        return [pscustomobject]@{ Tools = $tools.ToArray(); L = $null; U = $null; DecidingTool = $null }
    }
    if ($bestLow.Tool -ceq $bestHigh.Tool) { $deciding = $bestLow.Tool }
    else { $deciding = ('{0} (L), {1} (U)' -f $bestLow.Tool, $bestHigh.Tool) }
    return [pscustomobject]@{ Tools = $tools.ToArray(); L = $bestLow.L; U = $bestHigh.U; DecidingTool = $deciding }
}

function ConvertTo-BaselineConfigurationKey {
    # A baseline configuration cell in the form compared with a required tool's
    # AcceptedConfigurations: trimmed, one pair of surrounding backticks or double or single quotes
    # removed (docs/METHODOLOGY.md writes `extreme@anta777`), and every run of whitespace collapsed
    # to one space. Case is left as written; the comparison ignores it.
    param([AllowNull()][AllowEmptyString()][string]$Configuration)

    $text = ([string]$Configuration).Trim()
    foreach ($quote in @('`', '"', "'")) {
        if ($text.Length -ge 2 -and $text.StartsWith($quote, [StringComparison]::Ordinal) -and $text.EndsWith($quote, [StringComparison]::Ordinal)) {
            $text = $text.Substring(1, $text.Length - 2).Trim()
            break
        }
    }
    return [regex]::Replace($text, '\s+', ' ')
}

function Get-BaselineEvidenceGap {
    # What the baseline runs lack before B may decide criterion 1: Karhu RAM Test (default settings)
    # and TestMem5 (extreme@anta777), each under one tool name, and every tool, nominated ones
    # included, with at least 3 runs of one configuration and no run listed twice. A missing
    # configuration cell is accepted; any other cell of a required tool must be one of its
    # AcceptedConfigurations. Tools are grouped by name ignoring case, as in
    # Get-FastestBaseline. Returns one self-contained sentence per gap, none when complete.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Runs)

    $order = New-Object 'System.Collections.Generic.List[string]'
    $byTool = @{}
    foreach ($run in @($Runs)) {
        if ($null -eq $run) { continue }
        $key = ([string]$run.Tool).ToLowerInvariant()
        if (-not $byTool.ContainsKey($key)) {
            $byTool[$key] = New-Object 'System.Collections.Generic.List[object]'
            $order.Add($key)
        }
        $byTool[$key].Add($run)
    }

    $requiredOf = @{}
    foreach ($key in $order) {
        $name = ([string]$byTool[$key][0].Tool).Trim()
        $requiredOf[$key] = @(foreach ($required in $RequiredBaselineTools) {
                foreach ($pattern in $required.ToolPatterns) {
                    if ($name -like $pattern) { $required; break }
                }
            })
    }

    $gaps = New-Object 'System.Collections.Generic.List[string]'
    foreach ($required in $RequiredBaselineTools) {
        $names = @(foreach ($key in $order) {
                if (@($requiredOf[$key] | Where-Object { $_.Name -ceq $required.Name }).Count -gt 0) { $byTool[$key][0].Tool }
            })
        if ($names.Count -eq 0) {
            $gaps.Add(('{0} ({1}) has no run in the baseline CSV; at least {2} are required.' -f $required.Name, $required.Configuration, $BaselineMinimumRuns))
        }
        elseif ($names.Count -gt 1) {
            $gaps.Add(('{0} is listed under several tool names ({1}); use one name per tool.' -f $required.Name, ($names -join ', ')))
        }
    }

    foreach ($key in $order) {
        $toolRuns = $byTool[$key].ToArray()
        $tool = $toolRuns[0].Tool
        if ($toolRuns.Count -lt $BaselineMinimumRuns) {
            $gaps.Add(('{0} has {1} run(s); at least {2} are required.' -f $tool, $toolRuns.Count, $BaselineMinimumRuns))
        }

        # One configuration may be written several ways, so cells are told apart by their
        # normalised key ignoring case, and every accepted spelling of a required tool's
        # configuration is that one configuration. $configurations keeps the first cell written
        # for each distinct key, so the gaps quote the user's own text.
        $requiredTools = @($requiredOf[$key])
        $configurations = New-Object 'System.Collections.Generic.List[string]'
        $configurationKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $runIds = New-Object 'System.Collections.Generic.List[string]'
        $repeatedIds = New-Object 'System.Collections.Generic.List[string]'
        foreach ($run in $toolRuns) {
            $configuration = ([string]$run.Configuration).Trim()
            if ($configuration.Length -gt 0 -and $configuration -cne $Sym.Dash) {
                $configurationKey = ConvertTo-BaselineConfigurationKey -Configuration $configuration
                if ($requiredTools.Count -eq 1 -and @($requiredTools[0].AcceptedConfigurations | Where-Object { [string]::Equals($configurationKey, $_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                    $configurationKey = $requiredTools[0].Configuration
                }
                if ($configurationKeys.Add($configurationKey)) { $configurations.Add($configuration) }
            }
            $runId = ([string]$run.Run).Trim()
            if ($runId.Length -eq 0) { continue }
            if (-not ($runIds -contains $runId)) { $runIds.Add($runId) }
            elseif (-not ($repeatedIds -contains $runId)) { $repeatedIds.Add($runId) }
        }
        if ($configurations.Count -gt 1) {
            $gaps.Add(('{0} mixes configurations ({1}); each tool''s median needs one configuration.' -f $tool, ($configurations -join ', ')))
        }

        if ($requiredTools.Count -gt 1) {
            $gaps.Add(('{0} matches more than one required tool ({1}); use one name per tool.' -f $tool, (@(foreach ($required in $requiredTools) { $required.Name }) -join ', ')))
        }
        elseif ($requiredTools.Count -eq 1) {
            foreach ($configuration in $configurations) {
                $configurationKey = ConvertTo-BaselineConfigurationKey -Configuration $configuration
                if (@($requiredTools[0].AcceptedConfigurations | Where-Object { [string]::Equals($configurationKey, $_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) {
                    $gaps.Add(('{0} runs use configuration "{1}", not {2}.' -f $tool, $configuration, $requiredTools[0].Configuration))
                }
            }
        }
        foreach ($runId in $repeatedIds) { $gaps.Add(('{0} lists run {1} more than once.' -f $tool, $runId)) }
    }
    return , $gaps.ToArray()
}

function Get-Ratio {
    # A ratio of interval bounds with x / Infinity = 0, Infinity / x = Infinity and x / 0 = Infinity.
    param([double]$Numerator, [double]$Denominator)

    if ([double]::IsPositiveInfinity($Numerator)) { return [double]::PositiveInfinity }
    if ([double]::IsPositiveInfinity($Denominator)) { return 0.0 }
    if ($Denominator -eq 0) { return [double]::PositiveInfinity }
    return ($Numerator / $Denominator)
}

function Get-RatioDecision {
    # "At least 5x faster than the fastest baseline median", decided from the intervals (seconds):
    # met when min L(baseline) >= 5 x U(type), not met when min U(baseline) < 5 x L(type),
    # indeterminate otherwise. The ratio interval is [min L(baseline) / U(type), min U(baseline) /
    # L(type)].
    param(
        [AllowNull()][object]$BaselineLowSeconds,
        [AllowNull()][object]$BaselineHighSeconds,
        [AllowNull()][object]$TypeLow,
        [AllowNull()][object]$TypeHigh
    )

    if ($null -eq $BaselineLowSeconds -or $null -eq $BaselineHighSeconds -or $null -eq $TypeLow -or $null -eq $TypeHigh) {
        return [pscustomobject]@{ Decision = $Decision.Indeterminate; RatioLow = $null; RatioHigh = $null }
    }
    $bl = [double]$BaselineLowSeconds
    $bu = [double]$BaselineHighSeconds
    $tl = [double]$TypeLow
    $tu = [double]$TypeHigh
    if ($bl -ge $SpeedRatio * $tu) { $result = $Decision.Met }
    elseif ($bu -lt $SpeedRatio * $tl) { $result = $Decision.NotMet }
    else { $result = $Decision.Indeterminate }
    return [pscustomobject]@{
        Decision  = $result
        RatioLow  = (Get-Ratio -Numerator $bl -Denominator $tu)
        RatioHigh = (Get-Ratio -Numerator $bu -Denominator $tl)
    }
}

function Get-NoLaterDecision {
    # "Standard or Exhaustive fails no later than Quick": met when U(type) <= L(Quick), not met
    # when L(type) > U(Quick), indeterminate otherwise.
    param(
        [AllowNull()][object]$TypeLow,
        [AllowNull()][object]$TypeHigh,
        [AllowNull()][object]$QuickLow,
        [AllowNull()][object]$QuickHigh
    )

    if ($null -eq $TypeLow -or $null -eq $TypeHigh -or $null -eq $QuickLow -or $null -eq $QuickHigh) { return $Decision.Indeterminate }
    if ([double]$TypeHigh -le [double]$QuickLow) { return $Decision.Met }
    if ([double]$TypeLow -gt [double]$QuickHigh) { return $Decision.NotMet }
    return $Decision.Indeterminate
}

function Get-OverallDecision {
    # Met only when every check is met; not met when any is not met; indeterminate otherwise.
    param([AllowNull()][AllowEmptyCollection()][string[]]$Decisions)

    $items = @(@($Decisions) | Where-Object { $null -ne $_ })
    if ($items.Count -eq 0) { return $Decision.Indeterminate }
    if ($items -contains $Decision.NotMet) { return $Decision.NotMet }
    if (@($items | Where-Object { $_ -cne $Decision.Met }).Count -eq 0) { return $Decision.Met }
    return $Decision.Indeterminate
}

function Get-ConsecutivePassDecision {
    # Criterion 2 for one type on one clean profile, in start order: not met when any run is FAIL,
    # met when the longest PASS streak reaches the requirement, indeterminate otherwise. ABORTED
    # (or any other verdict) breaks a streak, and so does $NotFullLengthVerdict, a PASS that is not
    # full-length evidence. FailRuns, AbortedRuns and IneligibleRuns are 1-based positions.
    param([AllowNull()][AllowEmptyCollection()][string[]]$Verdicts, [Parameter(Mandatory = $true)][int]$Required)

    $longest = 0
    $current = 0
    $position = 0
    $failRuns = New-Object 'System.Collections.Generic.List[int]'
    $abortedRuns = New-Object 'System.Collections.Generic.List[int]'
    $ineligibleRuns = New-Object 'System.Collections.Generic.List[int]'
    foreach ($verdict in @($Verdicts)) {
        $position++
        $text = ([string]$verdict).ToUpperInvariant()
        if ($text -ceq 'PASS') {
            $current++
            if ($current -gt $longest) { $longest = $current }
            continue
        }
        $current = 0
        if ($text -ceq 'FAIL') { $failRuns.Add($position) }
        elseif ([string]$verdict -ceq $NotFullLengthVerdict) { $ineligibleRuns.Add($position) }
        else { $abortedRuns.Add($position) }
    }
    if ($failRuns.Count -gt 0) { $result = $Decision.NotMet }
    elseif ($longest -ge $Required) { $result = $Decision.Met }
    else { $result = $Decision.Indeterminate }
    return [pscustomobject]@{
        Decision       = $result
        LongestStreak  = $longest
        Required       = $Required
        N              = $position
        FailRuns       = $failRuns.ToArray()
        AbortedRuns    = $abortedRuns.ToArray()
        IneligibleRuns = $ineligibleRuns.ToArray()
    }
}

function Test-DurationWithinTolerance {
    # A clean run's measured duration against its plan target: within when the absolute deviation is
    # at most 15 % of the target. DeviationPercent is signed (positive = longer than planned).
    param([AllowNull()][object]$DurationSeconds, [AllowNull()][object]$TargetSeconds)

    if ($null -eq $DurationSeconds -or $null -eq $TargetSeconds -or [double]$TargetSeconds -le 0) {
        return [pscustomobject]@{ Within = $null; DeviationPercent = $null }
    }
    $difference = [double]$DurationSeconds - [double]$TargetSeconds
    return [pscustomobject]@{
        Within           = ([Math]::Abs($difference) -le $DurationTolerance * [double]$TargetSeconds)
        DeviationPercent = (100.0 * $difference / [double]$TargetSeconds)
    }
}

function Measure-TimeRemaining {
    # The time-remaining gate over the PROGRESS samples of one clean run. Only samples after step 1
    # has ended (1-based step >= 2) are used: the engine reports remaining_ms = -1 (calculating)
    # only until step 1 completes its first pass, so a sample after step 1 without an estimate
    # (-1, or no remaining_ms) is an unavailable estimate, counted in UnavailableCount. Actual
    # remaining = EndUtc - sample time. Every sample with actual remaining >= 10 s is gated at
    # +-10 % of the actual time, and an unavailable one is outside it. Samples under 10 s are
    # counted and their worst error reported, but not gated: the mm:ss display and pass jitter make
    # +-10 % unattainable there (an open deviation the user may reject). The worst deviations are
    # over the samples that carry an estimate.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Samples, [AllowNull()][object]$EndUtc)

    $gated = 0
    $gatedWithin = 0
    $worstGated = $null
    $ungated = 0
    $worstUngated = $null
    $unavailable = 0
    if ($null -ne $EndUtc) {
        foreach ($sample in @($Samples)) {
            if ($null -eq $sample -or $null -eq $sample.StepNumber) { continue }
            if ([int]$sample.StepNumber -lt 2) { continue }
            $actual = ([DateTimeOffset]$EndUtc - [DateTimeOffset]$sample.Utc).TotalSeconds
            $estimated = ($null -ne $sample.RemainingMs -and [double]$sample.RemainingMs -ge 0)
            if (-not $estimated) { $unavailable++ }
            if ($actual -ge $TimeRemainingGateSeconds) {
                $gated++
                if ($estimated) {
                    $absoluteError = [Math]::Abs([double]$sample.RemainingMs / 1000.0 - $actual)
                    if ($absoluteError -le $TimeRemainingTolerance * $actual) { $gatedWithin++ }
                    $percent = 100.0 * $absoluteError / $actual
                    if ($null -eq $worstGated -or $percent -gt $worstGated) { $worstGated = $percent }
                }
            }
            else {
                $ungated++
                if ($estimated -and $actual -gt 0) {
                    $percent = 100.0 * [Math]::Abs([double]$sample.RemainingMs / 1000.0 - $actual) / $actual
                    if ($null -eq $worstUngated -or $percent -gt $worstUngated) { $worstUngated = $percent }
                }
            }
        }
    }
    if ($null -eq $EndUtc -or $gated -eq 0) { $result = $Decision.Indeterminate }
    elseif ($gatedWithin -lt $gated) { $result = $Decision.NotMet }
    else { $result = $Decision.Met }
    return [pscustomobject]@{
        Decision            = $result
        GatedCount          = $gated
        GatedWithin         = $gatedWithin
        GatedFailures       = ($gated - $gatedWithin)
        WorstGatedPercent   = $worstGated
        UngatedCount        = $ungated
        WorstUngatedPercent = $worstUngated
        UnavailableCount    = $unavailable
    }
}

function Measure-RollingMeanPeak {
    # The peak 10-s rolling mean of time-ordered samples, each carrying Utc and the value in the
    # property named by Key ($null where the log has no number). Time is cut into the 1-s slots of
    # Get-SampleSlot, counted from the first sample. The window ending at a sample holds the samples
    # whose Utc lies in (Utc - 10 s, Utc]; it is complete when at least 9 distinct slots inside it
    # hold a numeric value, and its mean is the mean over those slots of each slot's mean value.
    # Windows are bounded by time, never by sample count, so a gap in the log never joins values
    # from either side into one window, and a burst of samples within one second neither completes
    # a window nor weighs that second more than another. With 1-s polling, one sample per slot, the
    # mean is the plain mean of the window's numeric samples. $null when no window is complete.
    # Each sample enters and leaves its slot's running sum once, and a window spans at most 11
    # slots, so the cost is linear in the samples.
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$Samples,
        [string]$Key = 'Value',
        [double]$WindowSeconds = $RollingWindowSeconds,
        [int]$MinimumValues = $RollingWindowMinimumValues
    )

    if ($null -eq $Samples -or $Samples.Count -eq 0 -or $WindowSeconds -le 0 -or $MinimumValues -le 0) { return $null }
    $windowTicks = [long]($WindowSeconds * [TimeSpan]::TicksPerSecond)
    $slotOf = Get-SampleSlot -Samples $Samples
    $slotSums = New-Object 'System.Collections.Generic.Dictionary[long,double]'
    $slotCounts = New-Object 'System.Collections.Generic.Dictionary[long,int]'
    $peak = $null
    $start = 0
    for ($i = 0; $i -lt $Samples.Count; $i++) {
        $ticks = ([DateTimeOffset]$Samples[$i].Utc).UtcTicks
        $slot = $slotOf[$i]
        $value = $Samples[$i].$Key
        if ($null -ne $value) {
            if ($slotCounts.ContainsKey($slot)) {
                $slotSums[$slot] = $slotSums[$slot] + [double]$value
                $slotCounts[$slot] = $slotCounts[$slot] + 1
            }
            else {
                $slotSums[$slot] = [double]$value
                $slotCounts[$slot] = 1
            }
        }
        $openTicks = $ticks - $windowTicks
        while ($start -lt $i -and ([DateTimeOffset]$Samples[$start].Utc).UtcTicks -le $openTicks) {
            $leaving = $Samples[$start].$Key
            if ($null -ne $leaving) {
                $leavingSlot = $slotOf[$start]
                if ($slotCounts[$leavingSlot] -le 1) {
                    # An emptied slot is dropped, so its running sum starts afresh if it refills.
                    [void]$slotCounts.Remove($leavingSlot)
                    [void]$slotSums.Remove($leavingSlot)
                }
                else {
                    $slotSums[$leavingSlot] = $slotSums[$leavingSlot] - [double]$leaving
                    $slotCounts[$leavingSlot] = $slotCounts[$leavingSlot] - 1
                }
            }
            $start++
        }
        $covered = 0
        $slotMeans = 0.0
        for ($s = $slotOf[$start]; $s -le $slot; $s++) {
            if ($slotCounts.ContainsKey($s)) {
                $covered++
                $slotMeans += $slotSums[$s] / $slotCounts[$s]
            }
        }
        if ($covered -ge $MinimumValues) {
            $mean = $slotMeans / $covered
            if ($null -eq $peak -or $mean -gt $peak) { $peak = $mean }
        }
    }
    return $peak
}

function Get-SampleSlot {
    # The 1-s slot of every sample, in order: whole seconds from the first sample, rounded to the
    # nearest, so the slots are centred on the first sample's own second. HWiNFO polls with a few
    # milliseconds of jitter; slots bounded at whole seconds from the first sample would put a
    # sample a millisecond early into its neighbour's slot and leave its own slot empty, while
    # centred slots keep every 1-s sample in its own slot. A burst of samples within one second
    # still falls into one or two slots.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Samples)

    $count = @($Samples).Count
    $slots = New-Object 'long[]' $count
    if ($count -eq 0) { return , $slots }
    $slotTicks = $ExpectedSamplingSeconds * [TimeSpan]::TicksPerSecond
    $originTicks = ([DateTimeOffset]$Samples[0].Utc).UtcTicks
    for ($i = 0; $i -lt $count; $i++) {
        $slots[$i] = [long][Math]::Floor([double](([DateTimeOffset]$Samples[$i].Utc).UtcTicks - $originTicks) / $slotTicks + 0.5)
    }
    return , $slots
}

function Measure-Peak {
    # The maximum sample; $null without samples.
    param([AllowNull()][AllowEmptyCollection()][double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $peak = $Values[0]
    foreach ($value in $Values) { if ($value -gt $peak) { $peak = $value } }
    return $peak
}

function Get-StressMetricLabel {
    param([Parameter(Mandatory = $true)][string]$Key)

    foreach ($metric in $StressMetrics) { if ($metric.Key -ceq $Key) { return $metric.Label } }
    return $Key
}

function Measure-HwInfoStatistic {
    # The stress-bound statistics of one HWiNFO log, over the samples inside [FromUtc, ToUtc] when
    # given, otherwise over the whole log: the peak 10-s rolling mean for bandwidth and package
    # power, the peak sample for DIMM temperature. The same rules serve the references, the whole
    # run and each step. A statistic that cannot be computed is $null, with its reason in Reasons.
    param(
        [Parameter(Mandatory = $true)][object]$Log,
        [AllowNull()][object]$FromUtc,
        [AllowNull()][object]$ToUtc
    )

    $values = @{}
    $reasons = @{}
    $windowed = ($null -ne $FromUtc -or $null -ne $ToUtc)

    # Rows outside the interval are dropped; a row inside it stays in place even where a metric's
    # cell is empty, so the rolling windows keep the log's own time axis.
    $inside = @(foreach ($sample in $Log.Samples) {
            if ($null -ne $FromUtc -and $sample.Utc -lt [DateTimeOffset]$FromUtc) { continue }
            if ($null -ne $ToUtc -and $sample.Utc -gt [DateTimeOffset]$ToUtc) { continue }
            $sample
        })
    # The interval measured: the window given, or for a reference log its own first to last row.
    $spanSeconds = $null
    $spanFrom = $FromUtc
    $spanTo = $ToUtc
    if ($inside.Count -gt 0) {
        if ($null -eq $spanFrom) { $spanFrom = $inside[0].Utc }
        if ($null -eq $spanTo) { $spanTo = $inside[$inside.Count - 1].Utc }
    }
    if ($null -ne $spanFrom -and $null -ne $spanTo) {
        $spanSeconds = ([DateTimeOffset]$spanTo - [DateTimeOffset]$spanFrom).TotalSeconds
    }
    $slotOf = Get-SampleSlot -Samples $inside

    foreach ($metric in $StressMetrics) {
        $key = $metric.Key
        $values[$key] = $null
        if ($Log.Missing.ContainsKey($key)) {
            $reasons[$key] = $Log.Missing[$key]
            continue
        }
        $numeric = [double[]]@(foreach ($sample in $inside) {
                $value = $sample.$key
                if ($null -ne $value) { [double]$value }
            })
        if ($numeric.Count -eq 0) {
            if ($windowed) { $reasons[$key] = 'no numeric sample inside the window; check -HwInfoUtcOffsetMinutes' }
            else { $reasons[$key] = 'no numeric sample' }
            continue
        }
        # A statistic over an interval is a statistic over that whole interval only when the
        # metric's numeric samples cover at least 90 % of its seconds: the run window, a step
        # window, or a reference log's own span. Coverage is counted in time, as the distinct 1-s
        # slots (Get-SampleSlot, from the first row inside) holding a numeric value, so samples
        # crowded into part of the interval never stand in for the seconds left unlogged. A log
        # that covers part of it (a wrong UTC offset, logging started late or stopped early, a
        # sensor that stopped reporting) is missing evidence, never a smaller sample to judge on.
        if ($null -ne $spanSeconds -and $spanSeconds -gt 0) {
            $needed = [Math]::Floor((1.0 - $SamplingTolerance) * $spanSeconds / $ExpectedSamplingSeconds)
            $slots = New-Object 'System.Collections.Generic.HashSet[long]'
            for ($i = 0; $i -lt $inside.Count; $i++) {
                if ($null -ne $inside[$i].$key) { [void]$slots.Add($slotOf[$i]) }
            }
            if ($slots.Count -lt $needed) {
                if ($windowed) {
                    $reasons[$key] = ('{0} of the window''s {1} s hold a numeric sample, fewer than the {2} that 1-s polling gives; check -HwInfoUtcOffsetMinutes' -f $slots.Count, (Format-Number $spanSeconds 0), $needed)
                }
                else {
                    $reasons[$key] = ('{0} of the log''s {1} s hold a numeric sample, fewer than the {2} that 1-s polling gives' -f $slots.Count, (Format-Number $spanSeconds 0), $needed)
                }
                continue
            }
        }
        if ($metric.Rolling) {
            $values[$key] = Measure-RollingMeanPeak -Samples $inside -Key $key
            if ($null -eq $values[$key]) {
                $reasons[$key] = ('no {0}-s window has a numeric sample in {1} of its 1-s slots' -f (Format-Number $RollingWindowSeconds 0), $RollingWindowMinimumValues)
            }
        }
        else {
            $values[$key] = Measure-Peak -Values $numeric
        }
    }
    return [pscustomobject]@{
        Name                  = $Log.Name
        Values                = $values
        Reasons               = $reasons
        MedianIntervalSeconds = $Log.MedianIntervalSeconds
    }
}

function Format-HwInfoSelection {
    # The selected columns of one metric as text: name, position among same-named columns, unit
    # and sensor, for example 'SPD Hub Temperature #2 [C] (DDR5 DIMM [#1])'.
    param([AllowNull()][AllowEmptyCollection()][object[]]$Selection)

    $items = @(@($Selection) | Where-Object { $null -ne $_ })
    if ($items.Count -eq 0) { return 'no column' }
    $texts = @(foreach ($item in $items) {
            $text = ('{0} #{1}' -f $item.Name, $item.Ordinal)
            if (-not [string]::IsNullOrEmpty($item.Unit)) { $text += (' [' + $item.Unit + ']') }
            if (-not [string]::IsNullOrEmpty($item.Sensor)) { $text += (' (' + $item.Sensor + ')') }
            $text
        })
    return ($texts -join ', ')
}

function Get-HwInfoSelectionMismatch {
    # The stress-bound comparison holds only when the references and the run log the same sensors
    # with the same tool. Per metric, every log that supplies it must name the sensor of each
    # selected column in HWiNFO's sensor-name row, which HWiNFO writes below a repeated header, with
    # blank Date and Time, when logging is stopped normally. A log without that row, or with a blank
    # cell for a selected column, cannot show which sensors it logged, so it yields a missing-
    # evidence entry rather than passing as a match. Every log must then select the same columns
    # as the first log whose sensors are named: the same count and, position by position, the same
    # name, the same position among columns of that name, the same sensor, and a unit converted to
    # the same scale (both recognised, or the same unit text). A log already entered as unnamed is
    # compared on everything but its sensors, so it is not entered twice. Returns the missing-
    # evidence entries; empty when every log agrees. The -*Columns patterns stay the only selection
    # mechanism: no identity mapping between different or unnamed sensors is accepted, because the
    # bound is defined over the same sensors on both sides, and evidence that cannot show its
    # sensors stays unverified.
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$References,
        [AllowNull()][object]$Run
    )

    $logs = @(@(@($References) + @($Run)) | Where-Object { $null -ne $_ })
    $problems = New-Object 'System.Collections.Generic.List[string]'
    foreach ($metric in $StressMetrics) {
        $key = $metric.Key
        $supplying = @($logs | Where-Object { -not $_.Missing.ContainsKey($key) })
        $named = New-Object 'bool[]' $supplying.Count
        $anchor = -1
        for ($j = 0; $j -lt $supplying.Count; $j++) {
            $selected = @($supplying[$j].Selections[$key])
            $unnamed = @($selected | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Sensor) })
            if ($unnamed.Count -eq 0) {
                $named[$j] = $true
                if ($anchor -lt 0) { $anchor = $j }
            }
            elseif (@($unnamed | Where-Object { $null -eq $_.Sensor }).Count -gt 0) {
                $problems.Add(('{0} sensors in {1} cannot be identified (the log has no HWiNFO sensor-name row; stop HWiNFO logging normally so it writes one)' -f $metric.Label, $supplying[$j].Name))
            }
            else {
                $problems.Add(('{0} sensors in {1} cannot be identified (its HWiNFO sensor-name row leaves {2} blank)' -f $metric.Label, $supplying[$j].Name, (Format-HwInfoSelection -Selection $unnamed)))
            }
        }
        if ($supplying.Count -eq 0) { continue }
        if ($anchor -lt 0) { $anchor = 0 }
        $first = $supplying[$anchor]
        $differing = New-Object 'System.Collections.Generic.List[string]'
        for ($j = 0; $j -lt $supplying.Count; $j++) {
            if ($j -eq $anchor) { continue }
            $log = $supplying[$j]
            $compareSensors = ($named[$anchor] -and $named[$j])
            $expected = @($first.Selections[$key])
            $actual = @($log.Selections[$key])
            $same = ($expected.Count -eq $actual.Count)
            for ($p = 0; $same -and $p -lt $expected.Count; $p++) {
                $e = $expected[$p]
                $a = $actual[$p]
                if (-not [string]::Equals([string]$e.Name, [string]$a.Name, [StringComparison]::OrdinalIgnoreCase)) { $same = $false }
                elseif ([int]$e.Ordinal -ne [int]$a.Ordinal) { $same = $false }
                elseif ($compareSensors -and -not [string]::Equals([string]$e.Sensor, [string]$a.Sensor, [StringComparison]::OrdinalIgnoreCase)) { $same = $false }
                elseif (-not (([bool]$e.Known -and [bool]$a.Known) -or ([string]$e.Unit -ceq [string]$a.Unit))) { $same = $false }
            }
            if (-not $same) { $differing.Add(('{0} logs {1}' -f $log.Name, (Format-HwInfoSelection $actual))) }
        }
        if ($differing.Count -gt 0) {
            $problems.Add(('{0} from the same sensors in every log ({1} logs {2} but {3})' -f $metric.Label, $first.Name, (Format-HwInfoSelection @($first.Selections[$key])), ($differing -join ' and ')))
        }
    }
    return , $problems.ToArray()
}

function Get-StressRunWindow {
    # The UTC window of the stress run: from run.startedUtc (else run.startRequestedUtc) to the
    # .log's COMPLETED (else run.endedUtc). Known only when both ends exist and the end is later.
    param([AllowNull()][object]$Run)

    $from = $null
    $to = $null
    if ($null -ne $Run) {
        $from = $Run.StartedUtc
        if ($null -eq $from) { $from = $Run.StartRequestedUtc }
        $to = $Run.EndedUtc
        if ($null -ne $Run.Log -and $null -ne $Run.Log.Completed) { $to = $Run.Log.Completed.Utc }
    }
    $known = ($null -ne $from -and $null -ne $to -and [DateTimeOffset]$to -gt [DateTimeOffset]$from)
    return [pscustomobject]@{ FromUtc = $from; ToUtc = $to; Known = $known }
}

function Get-StressRunProblem {
    # The stress bound is defined over a full Exhaustive run, so the summary must show one: the
    # Exhaustive type, ended Completed, neither recovered nor partial, the production plan's
    # 3600-s target and a known run window. The end reason and the target state what was planned,
    # not what ran, so the run itself must also be full length: run.durationSeconds and the run
    # window each at least the criteria 5 and 6 floor (the target less $DurationTolerance), and
    # steps[] listing the production plan's steps 1 to $StressRunStepCount once each, every one
    # with an actualSeconds (a step that never finished has none). There is no upper limit: a step
    # overrun is recorded rather than hidden, and a longer completed run is still a whole run.
    # Each shortfall is returned as missing evidence; an empty result means the run is eligible.
    param([AllowNull()][object]$Run)

    $problems = New-Object 'System.Collections.Generic.List[string]'
    if ($null -eq $Run) {
        $problems.Add('RAMQuick Exhaustive summary (not supplied; -StressRunSummary gives the run window and shows the run is a completed full-length Exhaustive run)')
        return , $problems.ToArray()
    }
    $wanted = 'a completed full-length Exhaustive run'
    if ([string]::IsNullOrEmpty($Run.TestType)) { $problems.Add(('{0} (the summary names no test type)' -f $wanted)) }
    elseif ([string]$Run.TestType -cne 'Exhaustive') { $problems.Add(('{0} (the summary is a {1} run)' -f $wanted, $Run.TestType)) }
    if ([string]::IsNullOrEmpty($Run.EndReason)) { $problems.Add(('{0} (the summary gives no end reason)' -f $wanted)) }
    elseif ([string]$Run.EndReason -cne 'Completed') { $problems.Add(('{0} (the run ended with {1}, not Completed)' -f $wanted, $Run.EndReason)) }
    $flags = @()
    if ($true -eq $Run.Recovered) { $flags += 'recovered from an unfinished run' }
    if ($true -eq $Run.Partial) { $flags += 'partial' }
    if ($flags.Count -gt 0) { $problems.Add(('{0} (the summary is {1})' -f $wanted, ($flags -join ' and '))) }
    if ($null -eq $Run.TargetSeconds) { $problems.Add(('{0} (the summary gives no plan.targetSeconds)' -f $wanted)) }
    elseif ([Math]::Abs([double]$Run.TargetSeconds - $StressRunTargetSeconds) -gt $StressRunTargetToleranceSeconds) {
        $problems.Add(('{0} (plan.targetSeconds is {1} s, not the {2} s of the production Exhaustive plan)' -f $wanted, (Format-Number $Run.TargetSeconds 0), (Format-Number $StressRunTargetSeconds 0)))
    }
    $minimumSeconds = (1.0 - $DurationTolerance) * $StressRunTargetSeconds
    if ($null -eq $Run.DurationSeconds -or [double]::IsNaN([double]$Run.DurationSeconds)) {
        $problems.Add(('{0} (the summary gives no run.durationSeconds)' -f $wanted))
    }
    elseif ([double]$Run.DurationSeconds -lt $minimumSeconds) {
        $problems.Add(('{0} (the run lasted {1} s, shorter than the {2} s a full-length {3}-s run needs)' -f $wanted, (Format-Number -Value $Run.DurationSeconds -Decimals 1), (Format-Number -Value $minimumSeconds -Decimals 0), (Format-Number -Value $StressRunTargetSeconds -Decimals 0)))
    }
    $window = Get-StressRunWindow -Run $Run
    if ($null -eq $window.FromUtc -or $null -eq $window.ToUtc) {
        $problems.Add('the run window (the summary and its .log give no run start or end time)')
    }
    elseif (-not $window.Known) {
        $problems.Add(('the run window (its end {0} is not after its start {1})' -f (Format-Timestamp $window.ToUtc), (Format-Timestamp $window.FromUtc)))
    }
    else {
        $windowSeconds = ([DateTimeOffset]$window.ToUtc - [DateTimeOffset]$window.FromUtc).TotalSeconds
        if ($windowSeconds -lt $minimumSeconds) {
            $problems.Add(('the run window ({0} s from {1} to {2}, shorter than a full-length run)' -f (Format-Number -Value $windowSeconds -Decimals 1), (Format-Timestamp -Value $window.FromUtc), (Format-Timestamp -Value $window.ToUtc)))
        }
    }

    # Each production step once, by its 1-based index; an index outside 1 to $StressRunStepCount or
    # listed twice means the summary is not of the production plan.
    $listedSteps = @(@($Run.Steps) | Where-Object { $null -ne $_ })
    $stepsByIndex = @{}
    $foreignSteps = 0
    foreach ($step in $listedSteps) {
        $index = [int]$step.Index
        if ($index -lt 1 -or $index -gt $StressRunStepCount -or $stepsByIndex.ContainsKey($index)) { $foreignSteps++ }
        else { $stepsByIndex[$index] = $step }
    }
    if ($foreignSteps -gt 0) {
        $problems.Add(('{0} (the summary lists {1} steps, not steps 1 to {2} once each)' -f $wanted, $listedSteps.Count, $StressRunStepCount))
    }
    elseif ($stepsByIndex.Count -lt $StressRunStepCount) {
        $problems.Add(('{0} (the summary lists {1} of the {2} steps)' -f $wanted, $stepsByIndex.Count, $StressRunStepCount))
    }
    for ($index = 1; $index -le $StressRunStepCount; $index++) {
        if ($stepsByIndex.ContainsKey($index) -and $null -eq $stepsByIndex[$index].ActualSeconds) {
            $problems.Add(('{0} (step {1} never finished)' -f $wanted, $index))
            break
        }
    }
    return , $problems.ToArray()
}

function Get-StressBoundVerdict {
    # The workload-referenced stress bound. Per metric, met when the RAMQuick statistic is at most
    # the larger of the two reference statistics. Overall met only when all three metrics are
    # present in all three logs and each is met. The whole list of missing evidence is built
    # first: a metric missing from any log or not measured because a log is absent, other than
    # exactly two reference logs, and every entry of Problems (an ineligible stress run, logs that
    # do not record the same sensors). When that list is not empty the bound and every row are
    # unverified, naming everything missing, with the raw values kept: no metric is judged on the
    # remaining ones. Otherwise not met, naming the exceeding metrics, or met.
    # References and Run carry Name, Values and Reasons (from Measure-HwInfoStatistic).
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$References,
        [AllowNull()][object]$Run,
        [AllowNull()][AllowEmptyCollection()][string[]]$Problems
    )

    $refs = @(@($References) | Where-Object { $null -ne $_ })
    $missing = New-Object 'System.Collections.Generic.List[string]'
    $exceeding = New-Object 'System.Collections.Generic.List[string]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    if ($refs.Count -ne 2) { $missing.Add(('exactly two reference logs ({0} supplied)' -f $refs.Count)) }
    if ($null -eq $Run) { $missing.Add('RAMQuick Exhaustive log (not supplied)') }
    foreach ($problem in @($Problems)) { if (-not [string]::IsNullOrEmpty($problem)) { $missing.Add($problem) } }

    # A metric whose logs are absent is still named, so the list says which of the three metrics
    # lack a comparable value, not only which files are missing.
    $absentLogs = New-Object 'System.Collections.Generic.List[string]'
    if ($refs.Count -eq 0) { $absentLogs.Add('reference logs') }
    elseif ($refs.Count -eq 1) { $absentLogs.Add('the second reference log') }
    if ($null -eq $Run) { $absentLogs.Add('RAMQuick Exhaustive log') }

    foreach ($metric in $StressMetrics) {
        $key = $metric.Key
        $refValues = New-Object 'System.Collections.Generic.List[object]'
        $complete = ($refs.Count -eq 2)
        $named = $false
        foreach ($ref in $refs) {
            $value = $ref.Values[$key]
            $refValues.Add($value)
            if ($null -eq $value) {
                $complete = $false
                $named = $true
                $missing.Add(('{0} in {1} ({2})' -f $metric.Label, $ref.Name, $ref.Reasons[$key]))
            }
        }
        $runValue = $null
        if ($null -ne $Run) {
            $runValue = $Run.Values[$key]
            if ($null -eq $runValue) {
                $named = $true
                $missing.Add(('{0} in {1} ({2})' -f $metric.Label, $Run.Name, $Run.Reasons[$key]))
            }
        }
        $bound = $null
        if ($complete) {
            $bound = [double]$refValues[0]
            if ([double]$refValues[1] -gt $bound) { $bound = [double]$refValues[1] }
        }
        if (-not $named -and ($null -eq $bound -or $null -eq $runValue)) {
            if ($absentLogs.Count -gt 0) {
                $missing.Add(('{0} (not logged: {1} not supplied)' -f $metric.Label, ($absentLogs -join ' and ')))
            }
            else {
                $missing.Add(('{0} (no bound: {1} reference logs supplied, the bound takes exactly two)' -f $metric.Label, $refs.Count))
            }
        }
        $rows.Add([pscustomobject]@{
                Key             = $key
                Label           = $metric.Label
                ReferenceValues = $refValues.ToArray()
                Bound           = $bound
                RunValue        = $runValue
                Decision        = $Decision.Unverified
            })
    }

    # Judged only on complete evidence: with anything missing every row stays unverified.
    if ($missing.Count -eq 0) {
        foreach ($row in $rows) {
            if ([double]$row.RunValue -le [double]$row.Bound) { $row.Decision = $Decision.Met }
            else {
                $row.Decision = $Decision.NotMet
                $exceeding.Add($row.Label)
            }
        }
    }

    if ($missing.Count -gt 0) {
        $result = $Decision.Unverified
        $reason = 'missing: ' + ($missing -join '; ')
    }
    elseif ($exceeding.Count -gt 0) {
        $result = $Decision.NotMet
        $reason = 'exceeded: ' + ($exceeding -join ', ')
    }
    else {
        $result = $Decision.Met
        $reason = 'every metric is within the larger reference value'
    }
    return [pscustomobject]@{
        Decision  = $result
        Reason    = $reason
        Rows      = $rows.ToArray()
        Missing   = $missing.ToArray()
        Exceeding = $exceeding.ToArray()
    }
}


# ---------------------------------------------------------------------------------------------
# Evaluation of the supplied runs
# ---------------------------------------------------------------------------------------------

function Get-StepWindow {
    # The UTC window of every started step of a run: STEP_START to STEP_END from its .log (a step
    # without STEP_END ends at the next start or at COMPLETED), or, without step events, the run's
    # start plus the cumulative steps[].actualSeconds.
    param([Parameter(Mandatory = $true)][object]$Run)

    $windows = New-Object 'System.Collections.Generic.List[object]'
    $names = @{}
    $holds = @{}
    foreach ($step in $Run.Steps) {
        $names[$step.Index] = $step.Name
        $holds[$step.Index] = $step.HoldSeconds
    }
    $runEnd = $Run.EndedUtc
    if ($null -ne $Run.Log.Completed) { $runEnd = $Run.Log.Completed.Utc }

    $starts = @($Run.Log.StepStarts | Where-Object { $null -ne $_.StepNumber } | Sort-Object { $_.Utc.UtcTicks })
    if ($starts.Count -gt 0) {
        $ends = @{}
        foreach ($end in $Run.Log.StepEnds) {
            if ($null -ne $end.StepNumber) { $ends[[int]$end.StepNumber] = $end.Utc }
        }
        for ($i = 0; $i -lt $starts.Count; $i++) {
            $number = [int]$starts[$i].StepNumber
            $to = $null
            if ($ends.ContainsKey($number)) { $to = $ends[$number] }
            elseif ($i + 1 -lt $starts.Count) { $to = $starts[$i + 1].Utc }
            else { $to = $runEnd }
            $name = $starts[$i].Fields['name']
            if ($names.ContainsKey($number) -and $null -ne $names[$number]) { $name = $names[$number] }
            $hold = $null
            if ($holds.ContainsKey($number)) { $hold = $holds[$number] }
            $windows.Add([pscustomobject]@{ Index = $number; Name = $name; HoldSeconds = $hold; FromUtc = $starts[$i].Utc; ToUtc = $to })
        }
        return , $windows.ToArray()
    }

    $cursor = $Run.StartedUtc
    if ($null -eq $cursor) { $cursor = $Run.StartRequestedUtc }
    if ($null -eq $cursor) { return , $windows.ToArray() }
    foreach ($step in $Run.Steps) {
        if ($null -eq $step.ActualSeconds) { $to = $runEnd }
        else { $to = ([DateTimeOffset]$cursor).AddSeconds([double]$step.ActualSeconds) }
        $windows.Add([pscustomobject]@{ Index = $step.Index; Name = $step.Name; HoldSeconds = $step.HoldSeconds; FromUtc = $cursor; ToUtc = $to })
        if ($null -eq $to) { break }
        $cursor = $to
    }
    return , $windows.ToArray()
}

function Get-StopPoint {
    # Where a stop request fell: Preparing (before PREPARED), or the step running at the time, marked
    # when that step holds memory idle before verifying it.
    param([Parameter(Mandatory = $true)][object]$Run, [Parameter(Mandatory = $true)][DateTimeOffset]$StopUtc)

    if ($null -eq $Run.Log.Prepared -or $StopUtc -lt $Run.Log.Prepared.Utc) { return 'Preparing' }
    $current = $null
    foreach ($window in (Get-StepWindow -Run $Run)) {
        if ($window.FromUtc -le $StopUtc) { $current = $window }
    }
    if ($null -eq $current) { return 'Running, before step 1' }
    $text = ('step {0} ({1})' -f $current.Index, $current.Name)
    if ($null -ne $current.HoldSeconds -and [double]$current.HoldSeconds -gt 0) { $text += ', hold step' }
    return $text
}

function Get-Criterion1Result {
    # Criterion 1 from the failing-profile runs and the fastest baseline median. While the baseline
    # evidence has gaps (Get-BaselineEvidenceGap), B is not established, so every "at least 5x
    # faster" check stays indeterminate, never met or not met; its ratio interval is still shown and
    # the gaps are returned as BaselineGaps.
    param(
        [AllowEmptyCollection()][object[]]$Runs,
        [AllowNull()][object]$Fastest,
        [AllowNull()][AllowEmptyCollection()][string[]]$BaselineGaps
    )

    $runRows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($run in @($Runs)) {
        if ($TestTypes -notcontains [string]$run.TestType) { continue }
        $observation = Get-FailTimeObservation -Run $run
        if ($observation.Kind -ceq 'excluded') {
            Write-Warning ('{0} ({1}) has no verdict time; it is excluded from the medians and counted as a miss.' -f (Format-RunLabel $run), $run.FileName)
        }
        $runRows.Add([pscustomobject]@{ Run = $run; Observation = $observation })
    }

    $baselineLow = $null
    $baselineHigh = $null
    if ($null -ne $Fastest -and $null -ne $Fastest.L) {
        $baselineLow = 60.0 * [double]$Fastest.L
        $baselineHigh = 60.0 * [double]$Fastest.U
    }
    $gaps = [string[]]@(@($BaselineGaps) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    $types = New-Object 'System.Collections.Generic.List[object]'
    $quick = $null
    foreach ($type in $TestTypes) {
        $observations = @(foreach ($row in $runRows) { if ([string]$row.Run.TestType -ceq $type) { $row.Observation } })
        $medianInput = @($observations | Where-Object { $_.Kind -cne 'excluded' })
        $interval = Get-MedianInterval -Values $medianInput
        $within = Get-WithinFiveMinutesDecision -Observations $observations
        $ratio = Get-RatioDecision -BaselineLowSeconds $baselineLow -BaselineHighSeconds $baselineHigh -TypeLow $interval.L -TypeHigh $interval.U
        if ($null -ne $baselineLow -and $gaps.Count -gt 0) { $ratio.Decision = $Decision.Indeterminate }
        $entry = [pscustomobject]@{
            Type     = $type
            Interval = $interval
            Within   = $within
            Ratio    = $ratio
            NoLater  = $null
        }
        if ($type -ceq 'Quick') { $quick = $entry }
        else { $entry.NoLater = Get-NoLaterDecision -TypeLow $interval.L -TypeHigh $interval.U -QuickLow $quick.Interval.L -QuickHigh $quick.Interval.U }
        $types.Add($entry)
    }

    $checks = New-Object 'System.Collections.Generic.List[string]'
    foreach ($entry in $types) {
        $checks.Add($entry.Within.Decision)
        $checks.Add($entry.Ratio.Decision)
        if ($null -ne $entry.NoLater) { $checks.Add($entry.NoLater) }
    }
    $decidingTool = 'no baseline supplied'
    if ($null -ne $Fastest -and $null -ne $Fastest.DecidingTool) { $decidingTool = $Fastest.DecidingTool }
    return [pscustomobject]@{
        RunRows      = $runRows.ToArray()
        Types        = $types.ToArray()
        Overall      = (Get-OverallDecision -Decisions $checks.ToArray())
        DecidingTool = $decidingTool
        HasBaseline  = ($null -ne $baselineLow)
        BaselineGaps = $gaps
    }
}

function Get-FullLengthIneligibility {
    # $null when a run's summary describes the full-length production plan of its type; otherwise
    # the reason it does not. plan.targetSeconds is the sum of the plan's step budgets and
    # coverage.targetPercent its coverage target, so a scaled plan shows in both. A targetReached
    # flag that contradicts the coverage numbers beside it is not trusted. NaN fails every check.
    param([Parameter(Mandatory = $true)][object]$Run)

    $type = [string]$Run.TestType
    $plan = $null
    foreach ($known in $TestTypes) { if ($known -ceq $type) { $plan = $FullLengthPlans[$known] } }
    if ($null -eq $plan) { return ('run.testType "{0}" is not one of {1}' -f $type, ($TestTypes -join ', ')) }

    $statedSeconds = $plan.Seconds.ToString('0.###', $Invariant)
    if ($null -eq $Run.TargetSeconds) {
        return ('plan.targetSeconds is missing; the full-length {0} plan is {1} s' -f $type, $statedSeconds)
    }
    if (-not ([Math]::Abs([double]$Run.TargetSeconds - $plan.Seconds) -le $PlanSecondsTolerance)) {
        return ('plan.targetSeconds is {0} s, not the {1} s of the full-length {2} plan' -f ([double]$Run.TargetSeconds).ToString('0.###', $Invariant), $statedSeconds, $type)
    }

    $floor = $plan.CoveragePercent.ToString('0.###', $Invariant)
    if ($null -eq $Run.CoverageTargetPercent) {
        return ('coverage.targetPercent is missing; the full-length {0} plan targets {1} %' -f $type, $floor)
    }
    $targetPercent = [double]$Run.CoverageTargetPercent
    if (-not ($targetPercent -ge $plan.CoveragePercent)) {
        return ('coverage.targetPercent is {0} %, below the {1} % target of the full-length {2} plan' -f $targetPercent.ToString('0.###', $Invariant), $floor, $type)
    }

    if ($null -ne $Run.CoverageTargetReached -and $null -ne $Run.CoveragePercent) {
        $percent = [double]$Run.CoveragePercent
        $reachedByNumbers = ($percent -ge $targetPercent)
        if ([bool]$Run.CoverageTargetReached -ne $reachedByNumbers) {
            $relation = 'reaches'
            if (-not $reachedByNumbers) { $relation = 'does not reach' }
            return ('coverage.targetReached is {0}, but coverage.percent {1} % {2} coverage.targetPercent {3} %' -f
                ([string][bool]$Run.CoverageTargetReached).ToLowerInvariant(), $percent.ToString('0.###', $Invariant), $relation, $targetPercent.ToString('0.###', $Invariant))
        }
    }
    return $null
}

function Get-Criterion2Result {
    # Criterion 2 per clean profile: consecutive PASS runs of Exhaustive (3) and Quick (10). A PASS
    # that is not full-length evidence breaks a streak and is noted; FAIL and ABORTED count whatever
    # the plan, because a false failure or a stopped run is evidence at any length.
    param([Parameter(Mandatory = $true)][object[]]$Profiles)

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $notes = New-Object 'System.Collections.Generic.List[string]'
    foreach ($cleanProfile in $Profiles) {
        foreach ($type in @('Exhaustive', 'Quick')) {
            $required = $RequiredConsecutivePasses[$type]
            if ($null -eq $cleanProfile.Runs) {
                $rows.Add([pscustomobject]@{ Profile = $cleanProfile.Display; Type = $type; Required = $required; Result = $null })
                continue
            }
            $typed = Get-RunsOfType -Runs $cleanProfile.Runs -Type $type
            $verdicts = [string[]]@(foreach ($run in $typed) {
                    $verdict = [string]$run.Verdict
                    if ($verdict.ToUpperInvariant() -ceq 'PASS') {
                        $reason = Get-FullLengthIneligibility -Run $run
                        if ($null -ne $reason) {
                            $verdict = $NotFullLengthVerdict
                            $notes.Add(('{0} ({1}) is not full-length evidence: {2}; it does not count toward a PASS streak.' -f (Format-RunLabel $run), $run.FileName, $reason))
                        }
                    }
                    $verdict
                })
            $rows.Add([pscustomobject]@{
                    Profile  = $cleanProfile.Display
                    Type     = $type
                    Required = $required
                    Result   = (Get-ConsecutivePassDecision -Verdicts $verdicts -Required $required)
                })
        }
        if ($null -ne $cleanProfile.Runs) {
            foreach ($run in @($cleanProfile.Runs)) {
                if ([string]$run.TestType -ceq 'Standard' -and ([string]$run.Verdict).ToUpperInvariant() -ceq 'FAIL') {
                    $notes.Add(('{0} reported FAIL on a clean profile ({1}).' -f (Format-RunLabel $run), $run.FileName))
                }
            }
        }
    }
    return [pscustomobject]@{ Rows = $rows.ToArray(); Notes = $notes.ToArray() }
}

function Get-CleanRunResult {
    # Criteria 5 and 6 for every clean run that completed with PASS and is full-length evidence:
    # duration against the stated duration of its type, coverage against its target and the type's
    # floor, and the time-remaining gate over its PROGRESS samples. A PASS of any other plan is
    # not a row; it is listed in Ineligible with the reason.
    param([Parameter(Mandatory = $true)][object[]]$Profiles)

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $ineligible = New-Object 'System.Collections.Generic.List[object]'
    $skipped = 0
    foreach ($cleanProfile in $Profiles) {
        foreach ($run in @($cleanProfile.Runs)) {
            if ($null -eq $run -or $TestTypes -notcontains [string]$run.TestType) { continue }
            if ([string]$run.EndReason -cne 'Completed' -or ([string]$run.Verdict).ToUpperInvariant() -cne 'PASS') {
                $skipped++
                continue
            }
            $reason = Get-FullLengthIneligibility -Run $run
            if ($null -ne $reason) {
                $ineligible.Add([pscustomobject]@{ Run = $run; Reason = $reason })
                continue
            }
            $plan = $FullLengthPlans[[string]$run.TestType]
            $coverage = $Decision.Indeterminate
            if ($run.CoverageTargetReached -eq $false -or
                ($null -ne $run.CoveragePercent -and -not ([double]$run.CoveragePercent -ge $plan.CoveragePercent))) { $coverage = $Decision.NotMet }
            elseif ($run.CoverageTargetReached -eq $true -and $null -ne $run.CoveragePercent) { $coverage = $Decision.Met }
            $endUtc = $run.EndedUtc
            if ($null -ne $run.Log.Completed) { $endUtc = $run.Log.Completed.Utc }
            $rows.Add([pscustomobject]@{
                    Run            = $run
                    PlannedSeconds = $plan.Seconds
                    Duration       = (Test-DurationWithinTolerance -DurationSeconds $run.DurationSeconds -TargetSeconds $plan.Seconds)
                    Coverage       = $coverage
                    TimeRemaining  = (Measure-TimeRemaining -Samples $run.Log.Progress.ToArray() -EndUtc $endUtc)
                })
        }
    }

    $coverageChecks = New-Object 'System.Collections.Generic.List[string]'
    $durationChecks = New-Object 'System.Collections.Generic.List[string]'
    foreach ($type in $TestTypes) {
        $typed = @($rows | Where-Object { [string]$_.Run.TestType -ceq $type })
        if ($typed.Count -eq 0) {
            $coverageChecks.Add($Decision.Indeterminate)
            $durationChecks.Add($Decision.Indeterminate)
            continue
        }
        foreach ($row in $typed) {
            $coverageChecks.Add($row.Coverage)
            if ($row.Duration.Within -eq $true) { $durationChecks.Add($Decision.Met) }
            elseif ($row.Duration.Within -eq $false) { $durationChecks.Add($Decision.NotMet) }
            else { $durationChecks.Add($Decision.Indeterminate) }
            $durationChecks.Add($row.TimeRemaining.Decision)
        }
    }
    return [pscustomobject]@{
        Rows             = $rows.ToArray()
        Skipped          = $skipped
        Ineligible       = $ineligible.ToArray()
        CoverageDecision = (Get-OverallDecision -Decisions $coverageChecks.ToArray())
        DurationDecision = (Get-OverallDecision -Decisions $durationChecks.ToArray())
    }
}

function Get-StopLatencyResult {
    # Criterion 7: stop request to Completed for every clean run whose .log has a STOP_REQUESTED
    # event, and the release time of every clean run (reported, not gated). Only user Stop requests
    # are Trials and decide the check; a stop RAMQuick requested itself (Thermal, HostFault) or one
    # without a logged reason is returned in OtherStops, reported only, so it can never stand in
    # for a Stop press.
    param([Parameter(Mandatory = $true)][object[]]$Profiles)

    $trials = New-Object 'System.Collections.Generic.List[object]'
    $otherStops = New-Object 'System.Collections.Generic.List[object]'
    $releases = New-Object 'System.Collections.Generic.List[object]'
    foreach ($cleanProfile in $Profiles) {
        foreach ($run in @($cleanProfile.Runs)) {
            if ($null -eq $run) { continue }
            $releases.Add($run)
            $stop = $run.Log.StopRequested
            if ($null -eq $stop) { continue }
            $completedUtc = $null
            if ($null -ne $run.Log.Completed) { $completedUtc = $run.Log.Completed.Utc }
            $latency = Get-SecondsBetween -From $stop.Utc -To $completedUtc
            $within = $null
            if ($null -ne $latency) { $within = ($latency -le $StopLatencyLimitSeconds) }
            $item = [pscustomobject]@{
                Run          = $run
                Reason       = $stop.Fields['reason']
                StopPoint    = (Get-StopPoint -Run $run -StopUtc $stop.Utc)
                StopUtc      = $stop.Utc
                CompletedUtc = $completedUtc
                Latency      = $latency
                Within       = $within
            }
            if ([string]$stop.Fields['reason'] -ceq $UserStopReason) { $trials.Add($item) }
            else { $otherStops.Add($item) }
        }
    }

    # Five user Stop trials are required: mid-step, one during a hold and one during Preparing.
    $items = $trials.ToArray()
    if (@($items | Where-Object { $_.Within -eq $false }).Count -gt 0) { $result = $Decision.NotMet }
    elseif ($items.Count -lt $StopTrialsRequired -or
        @($items | Where-Object { $null -eq $_.Within }).Count -gt 0 -or
        @($items | Where-Object { $_.StopPoint -ceq 'Preparing' }).Count -eq 0 -or
        @($items | Where-Object { $_.StopPoint -like '*, hold step' }).Count -eq 0) { $result = $Decision.Indeterminate }
    else { $result = $Decision.Met }
    return [pscustomobject]@{ Trials = $items; OtherStops = $otherStops.ToArray(); Releases = $releases.ToArray(); Decision = $result }
}

function Get-StressResult {
    # The stress bound from the reference logs and the Exhaustive run's log, with per-step
    # statistics over the run's step windows when its summary is supplied. Every input may be
    # absent; the verdict then names what is missing. The run log is measured over the summary's
    # run window whenever the summary gives one, eligible or not, so its raw values are reported;
    # only a completed full-length Exhaustive run, with the same sensors logged on every side, can
    # satisfy or exceed the bound.
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$HwInfoReferences,
        [AllowNull()][object]$StressLog,
        [AllowNull()][object]$StressRun
    )

    $referenceLogs = @(@($HwInfoReferences) | Where-Object { $null -ne $_ })
    $references = @(foreach ($log in $referenceLogs) { Measure-HwInfoStatistic -Log $log -FromUtc $null -ToUtc $null })
    $runWindow = Get-StressRunWindow -Run $StressRun
    $runStatistic = $null
    $windowText = 'the whole log'
    if ($null -ne $StressLog) {
        $from = $null
        $to = $null
        if ($runWindow.Known) {
            $from = $runWindow.FromUtc
            $to = $runWindow.ToUtc
            $windowText = ('{0} to {1}' -f (Format-Timestamp $from), (Format-Timestamp $to))
        }
        $runStatistic = Measure-HwInfoStatistic -Log $StressLog -FromUtc $from -ToUtc $to
    }
    $problems = New-Object 'System.Collections.Generic.List[string]'
    foreach ($problem in (Get-StressRunProblem -Run $StressRun)) { $problems.Add($problem) }
    foreach ($problem in (Get-HwInfoSelectionMismatch -References $referenceLogs -Run $StressLog)) { $problems.Add($problem) }
    $verdict = Get-StressBoundVerdict -References $references -Run $runStatistic -Problems $problems.ToArray()

    $steps = New-Object 'System.Collections.Generic.List[object]'
    if ($null -ne $StressLog -and $null -ne $StressRun) {
        $bounds = @{}
        foreach ($row in $verdict.Rows) { $bounds[$row.Key] = $row.Bound }
        # A step is judged only when the whole-run bound itself is decided and the step has all
        # three values; a step is never judged on a subset of the metrics.
        $decided = ($verdict.Decision -ceq $Decision.Met -or $verdict.Decision -ceq $Decision.NotMet)
        foreach ($window in (Get-StepWindow -Run $StressRun)) {
            $statistic = Measure-HwInfoStatistic -Log $StressLog -FromUtc $window.FromUtc -ToUtc $window.ToUtc
            $within = $Decision.Unverified
            $present = @($StressMetrics | Where-Object { $null -ne $statistic.Values[$_.Key] -and $null -ne $bounds[$_.Key] }).Count
            if ($decided -and $present -eq $StressMetrics.Count) {
                $exceeded = @(foreach ($metric in $StressMetrics) {
                        if ([double]$statistic.Values[$metric.Key] -gt [double]$bounds[$metric.Key]) { $metric.Label }
                    })
                if ($exceeded.Count -gt 0) { $within = 'no (' + ($exceeded -join ', ') + ')' }
                else { $within = 'yes' }
            }
            $steps.Add([pscustomobject]@{ Window = $window; Statistic = $statistic; Within = $within })
        }
    }
    return [pscustomobject]@{
        References = $references
        Run        = $runStatistic
        WindowText = $windowText
        Verdict    = $verdict
        Steps      = $steps.ToArray()
    }
}


# ---------------------------------------------------------------------------------------------
# Markdown sections (headings and headers as in docs/METHODOLOGY.md)
# ---------------------------------------------------------------------------------------------

function Write-TableLines {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory = $true)][string[]]$Header,
        [AllowEmptyCollection()][object[]]$Rows
    )

    foreach ($line in (Format-MarkdownTable -Header $Header -Rows $Rows)) { $Lines.Add($line) }
}

function Write-BaselineSection {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object[]]$Baseline,
        [AllowNull()][object]$Fastest,
        [AllowNull()][AllowEmptyCollection()][string[]]$Gaps
    )

    $Lines.Add($Headings.Baseline)
    $Lines.Add('')
    $Lines.Add($Headings.BaselineRuns)
    $Lines.Add('')
    if ($null -eq $Baseline) {
        $Lines.Add($NotSupplied)
    }
    else {
        $rows = New-Object 'System.Collections.Generic.List[object]'
        foreach ($run in $Baseline) {
            if ($run.Censored) { $minutes = '> ' + (Format-Number $run.Value 1); $flag = '1' }
            else { $minutes = Format-Number $run.Value 1; $flag = '0' }
            $rows.Add([object[]]@($run.Tool, $run.Configuration, $run.Run, $minutes, $flag))
        }
        Write-TableLines -Lines $Lines -Header $Headers.BaselineRuns -Rows $rows.ToArray()
    }
    $Lines.Add('')
    $Lines.Add($Headings.BaselineMedians)
    $Lines.Add('')
    if ($null -eq $Baseline -or $null -eq $Fastest) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        $Lines.Add($FastestBaselinePrefix + ' ' + $NotSupplied)
    }
    else {
        $rows = New-Object 'System.Collections.Generic.List[object]'
        foreach ($tool in $Fastest.Tools) {
            $rows.Add([object[]]@($tool.Tool, [string]$tool.Runs, [string]$tool.CensoredRuns, (Format-Interval $tool.L $tool.U 1)))
        }
        Write-TableLines -Lines $Lines -Header $Headers.BaselineMedians -Rows $rows.ToArray()
        $Lines.Add('')
        $Lines.Add(('{0} {1} min (deciding tool: {2})' -f $FastestBaselinePrefix, (Format-Interval $Fastest.L $Fastest.U 1), $Fastest.DecidingTool))
        foreach ($gap in @($Gaps)) {
            if ([string]::IsNullOrWhiteSpace($gap)) { continue }
            $Lines.Add('')
            $Lines.Add('Note: baseline evidence incomplete: ' + $gap)
        }
    }
    $Lines.Add('')
}

function Write-CriteriaSummarySection {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory = $true)][object[]]$Criteria
    )

    $Lines.Add($Headings.CriteriaSummary)
    $Lines.Add('')
    $rows = @(foreach ($criterion in $Criteria) {
            , ([object[]]@([string]$criterion.Number, $criterion.Name, $criterion.Threshold, $criterion.Evidence, $criterion.Status))
        })
    Write-TableLines -Lines $Lines -Header $Headers.CriteriaSummary -Rows $rows
    $Lines.Add('')
}

function Write-Criterion1Section {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object]$Result
    )

    $Lines.Add($Headings.Criterion1)
    $Lines.Add('')
    $Lines.Add($Headings.Criterion1Runs)
    $Lines.Add('')
    if ($null -eq $Result) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        $Lines.Add($Headings.Criterion1Checks)
        $Lines.Add('')
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        return
    }

    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $Result.RunRows) {
        $run = $row.Run
        $observation = $row.Observation
        switch -CaseSensitive ($observation.Kind) {
            'observed' { $time = Format-Number $observation.Value 1; $flag = '0' }
            'censored' { $time = '> ' + (Format-Number $observation.Value 1); $flag = '1' }
            default {
                if (([string]$run.Verdict).ToUpperInvariant() -ceq 'FAIL') { $time = 'excluded (recovered, no verdict time)' }
                else { $time = 'excluded (no verdict time)' }
                $flag = $Sym.Dash
            }
        }
        $step = $Sym.Dash
        if ($null -ne $run.FirstErrorStep) { $step = ('{0} ({1})' -f $run.FirstErrorStep.Index, $run.FirstErrorStep.Name) }
        $rows.Add([object[]]@($run.TestType, [string]$run.Ordinal, $run.Verdict, $time, (Format-Number $run.FirstErrorSeconds 1), $step, $flag))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no Quick, Standard or Exhaustive summary found_') }
    else { Write-TableLines -Lines $Lines -Header $Headers.Criterion1Runs -Rows $rows.ToArray() }
    $Lines.Add('')

    $Lines.Add($Headings.Criterion1Checks)
    $Lines.Add('')
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $Result.Types) {
        $ratioText = $Sym.Dash
        if ($null -ne $entry.Ratio.RatioLow) { $ratioText = Format-Interval $entry.Ratio.RatioLow $entry.Ratio.RatioHigh 2 }
        $noLater = $Sym.Dash
        if ($null -ne $entry.NoLater) { $noLater = $entry.NoLater }
        $rows.Add([object[]]@(
                $entry.Type,
                [string]$entry.Within.N,
                ('{0} of {1}' -f $entry.Within.K, $entry.Within.N),
                (Format-Interval $entry.Interval.L $entry.Interval.U 1),
                $ratioText,
                $entry.Within.Decision,
                $entry.Ratio.Decision,
                $noLater))
    }
    Write-TableLines -Lines $Lines -Header $Headers.Criterion1Types -Rows $rows.ToArray()
    $Lines.Add('')
    $Lines.Add(('Criterion 1 overall: {0}' -f $Result.Overall))
    if (-not $Result.HasBaseline) {
        $Lines.Add('')
        $Lines.Add(('No baseline CSV was supplied, so every "at least 5{0} faster" check is indeterminate.' -f $Sym.Times))
    }
    if (@($Result.BaselineGaps).Count -gt 0) {
        $Lines.Add('')
        $Lines.Add(('The baseline evidence is incomplete, so every "at least 5{0} faster" check is indeterminate: ' -f $Sym.Times) + (@($Result.BaselineGaps) -join ' '))
    }
    if ($Result.Overall -ceq $Decision.Indeterminate) {
        $Lines.Add('')
        $Lines.Add(('Remedy: add five more interleaved runs of each RAMQuick type, or three more runs of the deciding baseline tool ({0}), and re-apply the rule once; a result still indeterminate is recorded as such and the criterion is not marked met.' -f $Result.DecidingTool))
    }
    $Lines.Add('')
}

function Write-Criterion2Section {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object]$Result
    )

    $Lines.Add($Headings.Criterion2)
    $Lines.Add('')
    if ($null -eq $Result) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        return
    }
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $Result.Rows) {
        $required = ('{0} consecutive PASS' -f $row.Required)
        if ($null -eq $row.Result) {
            $rows.Add([object[]]@($row.Profile, $row.Type, $required, $NotSupplied, $Decision.Pending))
            continue
        }
        $outcome = $row.Result
        $shown = [Math]::Min($outcome.LongestStreak, $outcome.Required)
        $observed = ('{0} of {1} consecutive PASS ({2} run(s))' -f $shown, $outcome.Required, $outcome.N)
        if (@($outcome.FailRuns).Count -gt 0) { $observed += ('; FAIL: run(s) ' + (@($outcome.FailRuns) -join ', ')) }
        if (@($outcome.AbortedRuns).Count -gt 0) { $observed += ('; ABORTED or other: run(s) ' + (@($outcome.AbortedRuns) -join ', ')) }
        if (@($outcome.IneligibleRuns).Count -gt 0) { $observed += ('; not full-length: run(s) ' + (@($outcome.IneligibleRuns) -join ', ')) }
        $rows.Add([object[]]@($row.Profile, $row.Type, $required, $observed, $outcome.Decision))
    }
    $rows.Add([object[]]@('Known-passing', 'Long baseline run (Karhu to 10,000 % or three TestMem5 `extreme@anta777` cycles)',
            'zero errors', 'recorded manually', $Decision.Pending))
    Write-TableLines -Lines $Lines -Header $Headers.Criterion2 -Rows $rows.ToArray()
    foreach ($note in $Result.Notes) {
        $Lines.Add('')
        $Lines.Add('Note: ' + $note)
    }
    $Lines.Add('')
}

function Write-Criteria56Section {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object]$Result
    )

    $Lines.Add($Headings.Criteria56)
    $Lines.Add('')
    if ($null -eq $Result) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        $Lines.Add($Headings.TimeRemaining)
        $Lines.Add('')
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        return
    }

    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $Result.Rows) {
        $run = $row.Run
        $rows.Add([object[]]@(
                (Format-RunLabel $run),
                (Format-Number $row.PlannedSeconds 0),
                (Format-Number $run.DurationSeconds 1),
                (Format-SignedNumber $row.Duration.DeviationPercent 1),
                (Format-YesNo $row.Duration.Within),
                (Format-Number $run.CoveragePercent 1),
                (Format-Number $run.CoverageTargetPercent 1),
                (Format-YesNo $run.CoverageTargetReached)))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no full-length clean run completed with PASS_') }
    else { Write-TableLines -Lines $Lines -Header $Headers.Criteria56 -Rows $rows.ToArray() }
    foreach ($excluded in @($Result.Ineligible)) {
        $Lines.Add('')
        $Lines.Add(('Note: {0} ({1}) completed with PASS but is not full-length evidence ({2}), so it is not listed.' -f (Format-RunLabel $excluded.Run), $excluded.Run.FileName, $excluded.Reason))
    }
    if ($Result.Skipped -gt 0) {
        $Lines.Add('')
        $Lines.Add(('{0} run(s) in the clean-profile folders did not complete with PASS and are not listed (see Criterion 2 and Criterion 7).' -f $Result.Skipped))
    }
    $Lines.Add('')

    $Lines.Add($Headings.TimeRemaining)
    $Lines.Add('')
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $Result.Rows) {
        $remaining = $row.TimeRemaining
        $ungatedWorst = Format-Number $remaining.WorstUngatedPercent 1
        if ($remaining.UngatedCount -gt 0) { $ungatedWorst += ' (reported only)' }
        $status = $remaining.Decision
        if ($remaining.UnavailableCount -gt 0) { $status += (' ({0} sample(s) after step 1 without an estimate)' -f $remaining.UnavailableCount) }
        if (-not $row.Run.Log.Exists) { $status += ' (no .log)' }
        $rows.Add([object[]]@(
                (Format-RunLabel $row.Run),
                [string]$remaining.GatedCount,
                ('{0} of {1}' -f $remaining.GatedWithin, $remaining.GatedCount),
                (Format-Number $remaining.WorstGatedPercent 1),
                [string]$remaining.UngatedCount,
                $ungatedWorst,
                $status))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no full-length clean run completed with PASS_') }
    else {
        Write-TableLines -Lines $Lines -Header $Headers.TimeRemaining -Rows $rows.ToArray()
        $Lines.Add('')
        $Lines.Add('Only PROGRESS samples after step 1 are used; a sample there without an estimate (remaining_ms = -1, shown as calculating) counts as outside ' + $Sym.PlusMinus + '10 % when gated. Samples whose actual remaining time is under 10 s are reported only, not gated: an open deviation (see Open deviations).')
        $Lines.Add('')
        $Lines.Add(('Coverage targets on every clean run of each type: {0}. Duration and time remaining on every clean run of each type: {1}.' -f $Result.CoverageDecision, $Result.DurationDecision))
    }
    $Lines.Add('')
}

function Write-Criterion7Section {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object]$Result
    )

    $Lines.Add($Headings.Criterion7)
    $Lines.Add('')
    if ($null -eq $Result) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        $Lines.Add($Headings.ReleaseTime)
        $Lines.Add('')
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        return
    }

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $trial = 0
    foreach ($item in $Result.Trials) {
        $trial++
        # Two decimals, unlike other seconds: a 1-decimal "2.0" could hide a latency just above 2 s.
        $rows.Add([object[]]@(
                ('{0}: {1}, {2}' -f $trial, (Format-RunLabel $item.Run), $item.Reason),
                $item.StopPoint,
                (Format-Timestamp $item.StopUtc),
                (Format-Timestamp $item.CompletedUtc),
                (Format-Number $item.Latency 2),
                (Format-YesNo $item.Within)))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no run in the clean-profile folders has a user Stop request_') }
    else { Write-TableLines -Lines $Lines -Header $Headers.StopLatency -Rows $rows.ToArray() }
    $Lines.Add('')
    $Lines.Add(('Stop-latency check: {0} (five trials are required, one during Preparing and one during a hold step, each completed within 2 s of the stop request).' -f $Result.Decision))
    if (@($Result.OtherStops).Count -gt 0) {
        $Lines.Add('')
        $Lines.Add('Stops requested by RAMQuick itself are not Stop trials and are reported only:')
        foreach ($item in $Result.OtherStops) {
            $reason = [string]$item.Reason
            if ([string]::IsNullOrEmpty($reason)) { $reason = 'reason not logged' }
            $Lines.Add(('- {0}, {1}: {2}, stop request {3}, Completed {4}, latency {5} s' -f (Format-RunLabel $item.Run), $reason, $item.StopPoint,
                    (Format-Timestamp $item.StopUtc), (Format-Timestamp $item.CompletedUtc), (Format-Number $item.Latency 2)))
        }
    }
    $Lines.Add('')

    $Lines.Add($Headings.ReleaseTime)
    $Lines.Add('')
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($run in $Result.Releases) {
        $release = 'not confirmed'
        if ($null -ne $run.ReleaseSeconds) { $release = Format-Number $run.ReleaseSeconds 2 }
        $rows.Add([object[]]@((Format-RunLabel $run), (Format-Number $run.TestedBytes 0), $release))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no clean-profile run found_') }
    else {
        Write-TableLines -Lines $Lines -Header $Headers.ReleaseTime -Rows $rows.ToArray()
        $Lines.Add('')
        $Lines.Add('Release time is reported, not gated.')
    }
    $Lines.Add('')
}

function Format-HwInfoPolling {
    # The measured median polling interval of a log, as the Logs line reports it.
    param([AllowNull()][object]$MedianIntervalSeconds)

    if ($null -eq $MedianIntervalSeconds) { return 'median polling interval unknown' }
    return ('median polling {0} s' -f (Format-Number $MedianIntervalSeconds 2))
}

function Write-StressSection {
    # The stress-bound section, always with its Status line, its Logs line and both tables: with
    # inputs missing, the Status line names them and the cells show what was not supplied.
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory = $true)][object]$Result,
        [bool]$SummarySupplied
    )

    $Lines.Add($Headings.StressBound)
    $Lines.Add('')

    $verdict = $Result.Verdict
    switch -CaseSensitive ($verdict.Decision) {
        'met' { $status = 'satisfied' }
        'not met' { $status = 'exceeded' }
        default { $status = $Decision.Unverified }
    }
    $Lines.Add(('{0} {1} ({2})' -f $StressStatusPrefix, $status, $verdict.Reason))
    $Lines.Add('')
    # Each log is listed with the polling interval measured from its own timestamps, never with a
    # polling claim the script did not check.
    $names = @(foreach ($reference in $Result.References) {
            ('{0} ({1})' -f $reference.Name, (Format-HwInfoPolling $reference.MedianIntervalSeconds))
        })
    $first = $NotSupplied
    $second = $NotSupplied
    if ($names.Count -ge 1) { $first = $names[0] }
    if ($names.Count -ge 2) { $second = $names[1] }
    $runName = $NotSupplied
    if ($null -ne $Result.Run) {
        $runName = ('{0} (samples from {1}; {2})' -f $Result.Run.Name, $Result.WindowText, (Format-HwInfoPolling $Result.Run.MedianIntervalSeconds))
    }
    $Lines.Add(('Logs: llama.cpp inference = {0}; 7-Zip benchmark = {1}; RAMQuick Exhaustive = {2}.' -f $first, $second, $runName))
    if ($names.Count -gt 2) {
        $Lines.Add('')
        $Lines.Add(('Note: {0} reference logs were supplied; the bound takes exactly two.' -f $names.Count))
    }
    $Lines.Add('')

    $rows = New-Object 'System.Collections.Generic.List[object]'
    for ($m = 0; $m -lt $StressMetrics.Count; $m++) {
        $metric = $StressMetrics[$m]
        $row = $verdict.Rows[$m]
        $referenceCells = New-Object 'System.Collections.Generic.List[string]'
        for ($r = 0; $r -lt 2; $r++) {
            if ($r -ge @($row.ReferenceValues).Count) { $referenceCells.Add($NotSupplied) }
            elseif ($null -eq $row.ReferenceValues[$r]) { $referenceCells.Add('missing') }
            else { $referenceCells.Add((Format-Number $row.ReferenceValues[$r] 1)) }
        }
        $runCell = $NotSupplied
        if ($null -ne $Result.Run) {
            if ($null -eq $row.RunValue) { $runCell = 'missing' } else { $runCell = Format-Number $row.RunValue 1 }
        }
        switch -CaseSensitive ($row.Decision) {
            'met' { $within = 'yes' }
            'not met' { $within = 'no' }
            default { $within = $Decision.Unverified }
        }
        $rows.Add([object[]]@(
                ('{0} ({1})' -f $metric.Label, $metric.Unit),
                $metric.Statistic,
                $referenceCells[0],
                $referenceCells[1],
                (Format-Number $row.Bound 1),
                $runCell,
                $within))
    }
    Write-TableLines -Lines $Lines -Header $Headers.StressBound -Rows $rows.ToArray()
    $Lines.Add('')
    $Lines.Add('The bound is unverified whenever a metric is missing from any log, the logs are not comparable (1-s polling, the same sensors) or the summary does not show a completed full-length Exhaustive run; it is never judged on the remaining metrics.')
    $Lines.Add('')

    $Lines.Add($Headings.StressPerStep)
    $Lines.Add('')
    if (@($Result.Steps).Count -eq 0) {
        if ($SummarySupplied -and $null -ne $Result.Run) { $stepText = '_no step window found in the Exhaustive run_' }
        else { $stepText = $NotSupplied }
        $placeholder = [object[]]@($Sym.Dash, $stepText, $Sym.Dash, $Sym.Dash, $Sym.Dash, $Decision.Unverified)
        Write-TableLines -Lines $Lines -Header $Headers.StressPerStep -Rows @(, $placeholder)
    }
    else {
        $rows = New-Object 'System.Collections.Generic.List[object]'
        foreach ($step in $Result.Steps) {
            $values = $step.Statistic.Values
            $rows.Add([object[]]@(
                    (Get-StepId -Index $step.Window.Index),
                    $step.Window.Name,
                    (Format-Number $values['Bandwidth'] 1),
                    (Format-Number $values['PackagePower'] 1),
                    (Format-Number $values['DimmTemperature'] 1),
                    $step.Within))
        }
        Write-TableLines -Lines $Lines -Header $Headers.StressPerStep -Rows $rows.ToArray()
        $Lines.Add('')
        $Lines.Add('A step that exceeds the bound is retuned in `src/RamQuick.Plans/TestPlans.cs` only (for example `Half` threads or a hold) and re-measured.')
    }
    $Lines.Add('')
}


function Get-PartialCriterionStatus {
    # A criterion the script evaluates only in part: a violation or an undecided result shows as
    # such, but a met script part stays pending until the rest is recorded.
    param([Parameter(Mandatory = $true)][string]$ScriptDecision)

    if ($ScriptDecision -ceq $Decision.Met) { return $Decision.Pending }
    return $ScriptDecision
}

function Get-CriteriaSummary {
    # Criteria 1-9 with the status this script can establish; criteria 3, 4, 8 and 9 are evaluated
    # by the automated suites and manual checks and stay pending here.
    param(
        [AllowNull()][object]$Criterion1,
        [AllowNull()][object]$Criterion2,
        [AllowNull()][object]$CleanRuns,
        [AllowNull()][object]$StopLatency
    )

    $notEvaluated = 'not evaluated by this script'
    if ($null -eq $Criterion1) { $status1 = $Decision.Pending; $evidence1 = 'failing-profile runs not supplied' }
    else {
        $status1 = $Criterion1.Overall; $evidence1 = 'Fail-fast checks (summary JSONs and baseline CSV)'
        if (@($Criterion1.BaselineGaps).Count -gt 0) { $evidence1 += '; baseline evidence incomplete' }
    }

    if ($null -eq $Criterion2) { $status2 = $Decision.Pending; $evidence2 = 'clean-profile runs not supplied' }
    else {
        $decisions = @(foreach ($row in $Criterion2.Rows) {
                if ($null -eq $row.Result) { $Decision.Indeterminate } else { $row.Result.Decision }
            })
        $status2 = Get-PartialCriterionStatus -ScriptDecision (Get-OverallDecision -Decisions $decisions)
        $evidence2 = 'Criterion 2 table (summary verdicts); the long baseline run is recorded manually'
    }

    if ($null -eq $CleanRuns) {
        $status5 = $Decision.Pending; $evidence5 = 'clean-profile runs not supplied'
        $status6 = $Decision.Pending; $evidence6 = 'clean-profile runs not supplied'
    }
    else {
        $status5 = Get-PartialCriterionStatus -ScriptDecision $CleanRuns.CoverageDecision
        $evidence5 = ('full-length coverage targets: {0}; the 1 % accuracy check is automated' -f $CleanRuns.CoverageDecision)
        $status6 = $CleanRuns.DurationDecision
        $evidence6 = 'Criteria 5 and 6 tables (run.durationSeconds and .log PROGRESS lines)'
    }

    if ($null -eq $StopLatency) { $status7 = $Decision.Pending; $evidence7 = 'clean-profile runs not supplied' }
    else {
        $status7 = Get-PartialCriterionStatus -ScriptDecision $StopLatency.Decision
        $evidence7 = ('stop-latency trials: {0}; UI spot checks are recorded manually' -f $StopLatency.Decision)
    }

    return @(
        [pscustomobject]@{ Number = 1; Name = 'Fail-fast effectiveness'; Status = $status1; Evidence = $evidence1
            Threshold = ('Each type reports FAIL within 300 s in ' + $Sym.Ge + ' 9 of 10 runs and is ' + $Sym.Ge + ' 5' + $Sym.Times + ' faster than the fastest baseline median; Standard and Exhaustive fail no later than Quick')
        }
        [pscustomobject]@{ Number = 2; Name = 'No false failures'; Status = $status2; Evidence = $evidence2
            Threshold = 'Exhaustive 3 and Quick 10 consecutive PASS on the known-passing profile and on JEDEC defaults; a long baseline run passes on the known-passing profile'
        }
        [pscustomobject]@{ Number = 3; Name = 'Detection correctness'; Status = $Decision.Pending; Evidence = $notEvaluated
            Threshold = '100 % of injected faults detected with correct address, bit and step'
        }
        [pscustomobject]@{ Number = 4; Name = 'Cause hints'; Status = $Decision.Pending; Evidence = $notEvaluated
            Threshold = 'Top-ranked cause matches each injected signature; output labelled as a hint'
        }
        [pscustomobject]@{ Number = 5; Name = 'Coverage accuracy'; Status = $status5; Evidence = $evidence5
            Threshold = 'Coverage within 1 % of an independent count; each type reaches its target on a clean run'
        }
        [pscustomobject]@{ Number = 6; Name = 'Duration accuracy'; Status = $status6; Evidence = $evidence6
            Threshold = ('Clean run within ' + $Sym.PlusMinus + '15 % of the stated duration; time remaining within ' + $Sym.PlusMinus + '10 % of actual after the first step')
        }
        [pscustomobject]@{ Number = 7; Name = 'UI behaviour'; Status = $status7; Evidence = $evidence7
            Threshold = ('Live fields ' + $Sym.Ge + ' 1 Hz; Stop halts within 2 s; Stop on error on halts on the first error, off keeps a running count')
        }
        [pscustomobject]@{ Number = 8; Name = 'Edge cases'; Status = $Decision.Pending; Evidence = $notEvaluated
            Threshold = 'Low free memory, non-elevated launch, missing sensors, thermal abort and crash recovery behave as specified'
        }
        [pscustomobject]@{ Number = 9; Name = 'Methodology'; Status = $Decision.Pending; Evidence = $notEvaluated
            Threshold = 'docs/METHODOLOGY.md covers the research and maps each step to its mechanism'
        }
    )
}

# ---------------------------------------------------------------------------------------------
# Summarize
# ---------------------------------------------------------------------------------------------

$Usage = @'
Usage: Summarize-ValidationRuns.ps1 -SelfTest
       Summarize-ValidationRuns.ps1 [-FailingProfileRuns <dir>] [-PassingProfileRuns <dir>] [-JedecRuns <dir>]
           [-BaselineCsv <file>] [-ReferenceLogs <llama.cpp.csv>, <7zip.csv>] [-StressRunLog <file>]
           [-StressRunSummary <file>] [-BandwidthColumns <pattern[]>] [-PackagePowerColumns <pattern[]>]
           [-DimmTemperatureColumns <pattern[]>] [-HwInfoUtcOffsetMinutes <int>] [-OutputPath <file>]
At least one input is required. Get-Help .\scripts\Summarize-ValidationRuns.ps1 -Full describes every parameter.
'@

function Get-ReferenceLogPath {
    # powershell.exe -File passes "a.csv,b.csv" as one string; split it when it names no file itself.
    param([AllowNull()][string[]]$Paths)

    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        if (-not (Test-Path -LiteralPath $path) -and $path.Contains(',')) {
            foreach ($part in $path.Split(',')) { if (-not [string]::IsNullOrWhiteSpace($part)) { $result.Add($part.Trim()) } }
        }
        else { $result.Add($path) }
    }
    return , $result.ToArray()
}

function Invoke-Summarize {
    # Loads every supplied input, evaluates the criteria and writes the markdown report. Returns the
    # report text and its path, or $null when no input was given. Input errors throw.
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Bound)

    $inputNames = @('FailingProfileRuns', 'PassingProfileRuns', 'JedecRuns', 'BaselineCsv', 'ReferenceLogs', 'StressRunLog', 'StressRunSummary')
    $boundNames = @($Bound.Keys)
    $given = @($inputNames | Where-Object { ($boundNames -contains $_) -and -not [string]::IsNullOrWhiteSpace([string]($Bound[$_] -join '')) })
    if ($given.Count -eq 0) {
        [Console]::Error.WriteLine($Usage)
        return $null
    }

    if (($boundNames -contains 'OutputPath') -and -not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $outputFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    }
    else {
        $outputFile = Join-Path (Join-Path (Join-Path $RepoRoot 'artifacts') 'validation') 'validation-tables.md'
    }
    $utcOffset = $null
    if ($boundNames -contains 'HwInfoUtcOffsetMinutes') { $utcOffset = $HwInfoUtcOffsetMinutes }

    $failing = $null
    if ($given -contains 'FailingProfileRuns') { $failing = Import-RunSummaryFolder -Path $FailingProfileRuns -ProfileName 'failing' }
    $profiles = New-Object 'System.Collections.Generic.List[object]'
    $passing = $null
    if ($given -contains 'PassingProfileRuns') { $passing = Import-RunSummaryFolder -Path $PassingProfileRuns -ProfileName 'known-passing' }
    $profiles.Add([pscustomobject]@{ Display = 'Known-passing'; Runs = $passing })
    $jedec = $null
    if ($given -contains 'JedecRuns') { $jedec = Import-RunSummaryFolder -Path $JedecRuns -ProfileName 'JEDEC defaults' }
    $profiles.Add([pscustomobject]@{ Display = 'JEDEC defaults'; Runs = $jedec })
    $cleanSupplied = ($null -ne $passing -or $null -ne $jedec)

    $baseline = $null
    $baselineGaps = @()
    $fastest = $null
    if ($given -contains 'BaselineCsv') {
        $baseline = Import-BaselineCsv -Path $BaselineCsv
        $fastest = Get-FastestBaseline -Runs $baseline
        $baselineGaps = Get-BaselineEvidenceGap -Runs $baseline
        foreach ($gap in $baselineGaps) { Write-Warning ('Baseline evidence incomplete: ' + $gap) }
    }

    $referenceLogList = New-Object 'System.Collections.Generic.List[object]'
    if ($given -contains 'ReferenceLogs') {
        foreach ($path in (Get-ReferenceLogPath -Paths $ReferenceLogs)) {
            $referenceLogList.Add((Import-HwInfoLog -Path $path -UtcOffsetMinutes $utcOffset))
        }
    }
    $stressLog = $null
    if ($given -contains 'StressRunLog') { $stressLog = Import-HwInfoLog -Path $StressRunLog -UtcOffsetMinutes $utcOffset }
    $stressRun = $null
    if ($given -contains 'StressRunSummary') {
        if (-not (Test-Path -LiteralPath $StressRunSummary -PathType Leaf)) { throw ('The stress-run summary was not found: {0}' -f $StressRunSummary) }
        $summaryPath = (Resolve-Path -LiteralPath $StressRunSummary).ProviderPath
        $check = Import-RunSummaryFile -Path $summaryPath
        if ($check.Problems.Count -gt 0) {
            throw ('The stress-run summary {0} is not a schema-1 RAMQuick summary of the run its name identifies: {1}' -f $summaryPath, ($check.Problems -join '; '))
        }
        $stressRun = ConvertFrom-RunSummaryDocument -Document $check.Document -SummaryPath $summaryPath -ProfileName 'stress run'
        if ([string]$stressRun.TestType -cne 'Exhaustive') {
            Write-Warning ('The stress-run summary {0} is a {1} run; the stress bound is defined over a full Exhaustive run.' -f $summaryPath, $stressRun.TestType)
        }
        if (-not $stressRun.Log.Exists) {
            Write-Warning ('The stress run''s event log {0} was not found; step windows fall back to steps[].actualSeconds.' -f $stressRun.Log.Path)
        }
    }

    $criterion1 = $null
    if ($null -ne $failing) { $criterion1 = Get-Criterion1Result -Runs $failing -Fastest $fastest -BaselineGaps $baselineGaps }
    $criterion2 = $null
    $cleanRuns = $null
    $stopLatency = $null
    if ($cleanSupplied) {
        $profileArray = $profiles.ToArray()
        $criterion2 = Get-Criterion2Result -Profiles $profileArray
        $cleanRuns = Get-CleanRunResult -Profiles $profileArray
        $stopLatency = Get-StopLatencyResult -Profiles $profileArray
    }
    # Always evaluated: with no stress input the section still carries its Status line, naming
    # every missing log and metric, as the document's template requires.
    $stress = Get-StressResult -HwInfoReferences $referenceLogList.ToArray() -StressLog $stressLog -StressRun $stressRun

    $md = New-Object 'System.Collections.Generic.List[string]'
    $md.Add('<!-- Generated by scripts/Summarize-ValidationRuns.ps1 on ' + [DateTimeOffset]::UtcNow.ToString($OutputTimestampFormat, $Invariant) + '. Paste each section over its template in docs/METHODOLOGY.md. -->')
    $md.Add('')
    Write-BaselineSection -Lines $md -Baseline $baseline -Fastest $fastest -Gaps $baselineGaps
    $md.Add($Headings.Validation)
    $md.Add('')
    Write-CriteriaSummarySection -Lines $md -Criteria (Get-CriteriaSummary -Criterion1 $criterion1 -Criterion2 $criterion2 -CleanRuns $cleanRuns -StopLatency $stopLatency)
    Write-Criterion1Section -Lines $md -Result $criterion1
    Write-Criterion2Section -Lines $md -Result $criterion2
    Write-Criteria56Section -Lines $md -Result $cleanRuns
    Write-Criterion7Section -Lines $md -Result $stopLatency
    Write-StressSection -Lines $md -Result $stress -SummarySupplied ($null -ne $stressRun)

    $text = ($md.ToArray() -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine
    $outputFolder = Split-Path -Parent $outputFile
    if (-not [string]::IsNullOrEmpty($outputFolder) -and -not (Test-Path -LiteralPath $outputFolder -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $outputFolder -Force
    }
    [IO.File]::WriteAllText($outputFile, $text, (New-Object Text.UTF8Encoding $false))
    return [pscustomobject]@{ Markdown = $text; Path = $outputFile }
}


# ---------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------

function Format-TestValue {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return 'null' }
    if ($Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) { return ([double]$Value).ToString('R', $Invariant) }
    if ($Value -is [string]) { return ('"' + $Value + '"') }
    if ($Value -is [DateTimeOffset]) { return $Value.ToString('o', $Invariant) }
    return [string]$Value
}

function Assert-Near {
    # Numeric equality within 1e-9; infinities and $null must match exactly.
    param([AllowNull()][object]$Actual, [AllowNull()][object]$Expected, [Parameter(Mandatory = $true)][string]$What)

    $ok = $false
    if ($null -eq $Expected) { $ok = ($null -eq $Actual) }
    elseif ($null -ne $Actual) {
        $e = [double]$Expected
        $a = [double]$Actual
        if ([double]::IsInfinity($e) -or [double]::IsInfinity($a)) { $ok = ($e -eq $a) }
        else { $ok = ([Math]::Abs($e - $a) -le 1e-9) }
    }
    if (-not $ok) { throw ('{0}: expected {1} got {2}' -f $What, (Format-TestValue $Expected), (Format-TestValue $Actual)) }
}

function Assert-Equal {
    # Exact (case-sensitive) equality of strings, booleans and integers; $null must match $null.
    param([AllowNull()][object]$Actual, [AllowNull()][object]$Expected, [Parameter(Mandatory = $true)][string]$What)

    $ok = $false
    if ($null -eq $Expected) { $ok = ($null -eq $Actual) }
    elseif ($null -ne $Actual) {
        if ($Expected -is [string]) { $ok = ([string]$Actual -ceq $Expected) }
        else { $ok = ($Actual -eq $Expected) }
    }
    if (-not $ok) { throw ('{0}: expected {1} got {2}' -f $What, (Format-TestValue $Expected), (Format-TestValue $Actual)) }
}

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory = $true)][string]$What)

    if (-not $Condition) { throw ('{0}: expected true got false' -f $What) }
}

function Get-TestObservation {
    # A synthetic median-interval input value.
    param([double]$Value, [bool]$Censored)

    return [pscustomobject]@{ Value = $Value; Censored = $Censored }
}

function Get-TestCleanRun {
    # A synthetic clean-profile run, as far as the criteria 2, 5 and 6 rules read it; by default a
    # full-length Quick PASS whose .log exists and holds no PROGRESS sample.
    param(
        [string]$Type = 'Quick',
        [string]$Verdict = 'PASS',
        [AllowNull()][object]$TargetSeconds = 300.0,
        [AllowNull()][object]$DurationSeconds = 306.0,
        [AllowNull()][object]$CoveragePercent = 240.0,
        [AllowNull()][object]$CoverageTargetPercent = 200.0,
        [AllowNull()][object]$CoverageTargetReached = $true,
        [int]$Ordinal = 1
    )

    return [pscustomobject]@{
        TestType              = $Type
        Verdict               = $Verdict
        EndReason             = 'Completed'
        DurationSeconds       = $DurationSeconds
        TargetSeconds         = $TargetSeconds
        CoveragePercent       = $CoveragePercent
        CoverageTargetPercent = $CoverageTargetPercent
        CoverageTargetReached = $CoverageTargetReached
        EndedUtc              = [DateTimeOffset]::new(2026, 10, 28, 14, 30, 0, [TimeSpan]::Zero)
        Profile               = 'known-passing'
        Ordinal               = $Ordinal
        FileName              = ('RamQuick-{0}-{1}-summary.json' -f $Type, $Ordinal)
        Log                   = [pscustomobject]@{ Exists = $true; Progress = (New-Object 'System.Collections.Generic.List[object]'); Completed = $null }
    }
}

function Get-TestRun {
    # A synthetic failing-profile run, as far as the fail-time rules read it.
    param([string]$Verdict, [AllowNull()][object]$Seconds)

    return [pscustomobject]@{ Verdict = $Verdict; TimeToVerdictSeconds = $Seconds }
}

function Get-TestFailingProfileRun {
    # A synthetic failing-profile run of one type, as far as Get-Criterion1Result reads it.
    param([string]$Type, [string]$Verdict, [AllowNull()][object]$Seconds, [int]$Ordinal)

    return [pscustomobject]@{
        TestType = $Type; Verdict = $Verdict; TimeToVerdictSeconds = $Seconds
        FileName = ('{0}-{1}.summary.json' -f $Type, $Ordinal); Profile = 'failing'; Ordinal = $Ordinal
    }
}

function Get-TestBaselineRow {
    # A synthetic baseline CSV row; an empty configuration omits the column, as a four-column CSV does.
    param([string]$Tool, [string]$Run, [string]$Minutes, [string]$Censored, [string]$Configuration)

    $row = [ordered]@{ tool = $Tool; run = $Run; minutesToFirstError = $Minutes; censored = $Censored }
    if (-not [string]::IsNullOrEmpty($Configuration)) { $row['configuration'] = $Configuration }
    return [pscustomobject]$row
}

function Get-TestStatistic {
    # A synthetic per-log stress statistic; $null marks a missing metric.
    param([string]$Name, [AllowNull()][object]$Bandwidth, [AllowNull()][object]$PackagePower, [AllowNull()][object]$DimmTemperature)

    $values = @{ Bandwidth = $Bandwidth; PackagePower = $PackagePower; DimmTemperature = $DimmTemperature }
    $reasons = @{}
    foreach ($key in @($values.Keys)) { if ($null -eq $values[$key]) { $reasons[$key] = 'no column' } }
    return [pscustomobject]@{ Name = $Name; Values = $values; Reasons = $reasons }
}

function Get-TestSampleSet {
    # Synthetic time-ordered samples for the rolling-mean rule: Value (or $null) at a UTC time that
    # is Seconds[i] after a fixed start, or i seconds after it when Seconds is not given.
    param([AllowEmptyCollection()][object[]]$Values, [AllowNull()][double[]]$Seconds)

    $start = [DateTimeOffset]::new(2026, 10, 28, 12, 0, 0, [TimeSpan]::Zero)
    $samples = New-Object 'System.Collections.Generic.List[object]'
    for ($i = 0; $i -lt @($Values).Count; $i++) {
        $offset = [double]$i
        if ($null -ne $Seconds) { $offset = $Seconds[$i] }
        $samples.Add([pscustomobject]@{ Utc = $start.AddSeconds($offset); Value = $Values[$i] })
    }
    return , $samples.ToArray()
}

function Get-TestStressRun {
    # A synthetic stress-run summary, as far as the stress-run eligibility rule reads it: by
    # default a completed 3600-s Exhaustive run whose window (run.startedUtc to run.endedUtc) is
    # WindowSeconds long and whose steps[] lists steps 1 to StepCount, every one finished except
    # UnfinishedStep (0 for none), which has no actualSeconds.
    param(
        [string]$TestType = 'Exhaustive',
        [string]$EndReason = 'Completed',
        [AllowNull()][object]$TargetSeconds = 3600.0,
        [bool]$Recovered = $false,
        [bool]$StartKnown = $true,
        [AllowNull()][object]$DurationSeconds = 3600.0,
        [double]$WindowSeconds = 3600.0,
        [int]$StepCount = $StressRunStepCount,
        [int]$UnfinishedStep = 0
    )

    $start = [DateTimeOffset]::new(2026, 10, 28, 14, 0, 20, [TimeSpan]::Zero)
    $started = $start
    $startRequested = $start.AddSeconds(-20)
    if (-not $StartKnown) { $started = $null; $startRequested = $null }
    $steps = @(for ($index = 1; $index -le $StepCount; $index++) {
            $actual = [double]$WindowSeconds / [double]$StepCount
            if ($index -eq $UnfinishedStep) { $actual = $null }
            [pscustomobject]@{ Index = $index; Name = ('Step {0}' -f $index); HoldSeconds = $null; ActualSeconds = $actual; Errors = 0.0 }
        })
    return [pscustomobject]@{
        TestType          = $TestType
        EndReason         = $EndReason
        Recovered         = $Recovered
        Partial           = $Recovered
        TargetSeconds     = $TargetSeconds
        DurationSeconds   = $DurationSeconds
        StartRequestedUtc = $startRequested
        StartedUtc        = $started
        EndedUtc          = $start.AddSeconds($WindowSeconds)
        Steps             = $steps
        Log               = [pscustomobject]@{ Completed = $null }
    }
}

function Get-TestSummaryJson {
    # A summary as RamQuick.TestHost publishes it: a scaled two-step Quick PASS run, published as
    # RamQuick-20261030-021529-Quick-13ae3ce49e4046f4a7e9789c7f9355fe-summary.json.
    return @'
{
  "schemaVersion": 1,
  "app": { "name": "RAMQuick", "version": "1.0.0" },
  "run": {
    "id": "13ae3ce49e4046f4a7e9789c7f9355fe",
    "testType": "Quick",
    "stopOnError": false,
    "verdict": "PASS",
    "endReason": "Completed",
    "endDetail": null,
    "startRequestedUtc": "2026-10-30T02:15:29.828Z",
    "startedUtc": "2026-10-30T02:15:30.107Z",
    "endedUtc": "2026-10-30T02:15:54.641Z",
    "verdictUtc": "2026-10-30T02:15:54.647Z",
    "durationSeconds": 24.536,
    "timeToVerdictSeconds": 24.81916,
    "firstErrorSeconds": null,
    "recovered": false,
    "partial": false
  },
  "system": { "os": "Microsoft Windows 10.0.26100", "cpu": "INTEL(R) XEON(R) PLATINUM 8573C", "logicalProcessors": 128, "processorGroups": 2, "instructionSet": "AVX-512", "reducedInstructionSet": false },
  "memory": { "installedBytes": 34359738368, "availableBytesAtStart": 1042102509568, "plannedBytes": 937645047808, "testedBytes": 268435456, "releaseSeconds": 0.035, "pageMode": "Standard", "addressKind": "Virtual", "type": "DDR5", "speedMTs": 6000, "channels": 2, "dimms": 2 },
  "plan": { "targetSeconds": 24, "targetCoveragePercent": 60 },
  "steps": [
    { "index": 1, "name": "High-toggle sweep", "pattern": "HighToggle", "accessOrder": "SequentialAscending", "threadMode": "All", "holdSeconds": 0, "primaryMechanism": "SignalIntegrity", "plannedSeconds": 12, "plannedCoveragePercent": 25, "actualSeconds": 11.99, "overrunSeconds": 0, "passes": 343, "coveragePercent": 34300, "errors": 0, "completed": true },
    { "index": 2, "name": "Random bank thrash", "pattern": "Random", "accessOrder": "BankStride", "threadMode": "All", "holdSeconds": 0, "primaryMechanism": "RowTimings", "plannedSeconds": 12, "plannedCoveragePercent": 35, "actualSeconds": 12.507, "overrunSeconds": 0.507, "passes": 269, "coveragePercent": 26900, "errors": 0, "completed": true }
  ],
  "coverage": { "percent": 61200, "targetPercent": 60, "targetReached": true },
  "temperatures": { "cpuMaxC": null, "ramMaxC": null, "cpuStatus": "Stale", "ramStatus": "Stale" },
  "errors": { "total": 0, "truncated": false, "detailAvailable": true, "storedCount": 0, "storedCap": 10000, "storedDistinctAddresses": 0, "groups": [], "records": [] },
  "causeHints": { "label": "Hint \u2014 not a diagnosis", "items": [] },
  "unfinishedRun": null
}
'@
}

function ConvertFrom-TestJson {
    # One JSON text in both shapes Import-JsonDocument can return: ConvertFrom-Json objects and
    # JavaScriptSerializer dictionaries.
    param([Parameter(Mandatory = $true)][string]$Text)

    Add-Type -AssemblyName System.Web.Extensions
    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    return , @(($Text | ConvertFrom-Json), $serializer.DeserializeObject($Text))
}

function Edit-TestText {
    # Replaces text that must be present, so a fixture edit can never silently test nothing.
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Find,
        [AllowEmptyString()][string]$Replace
    )

    if (-not $Text.Contains($Find)) { throw ('test fixture: {0} not found' -f $Find) }
    return $Text.Replace($Find, $Replace)
}

function Get-TestStopRun {
    # A synthetic clean-profile Quick run with one STOP_REQUESTED, as far as the stop-latency rules
    # read it. Step 1 has no hold and step 2 holds 5 s; 'mid' stops in step 1, 'hold' in step 2 and
    # 'Preparing' before PREPARED. An empty Reason leaves the reason field out of the stop record.
    param(
        [Parameter(Mandatory = $true)][int]$Ordinal,
        [AllowEmptyString()][string]$Reason,
        [Parameter(Mandatory = $true)][ValidateSet('mid', 'hold', 'Preparing')][string]$StopPoint,
        [Parameter(Mandatory = $true)][double]$LatencySeconds
    )

    $start = [DateTimeOffset]::new(2026, 11, 14, 0, 0, 0, [TimeSpan]::Zero).AddHours($Ordinal)
    $startedUtc = $null
    $prepared = $null
    $stepStarts = New-Object 'System.Collections.Generic.List[object]'
    $stepEnds = New-Object 'System.Collections.Generic.List[object]'
    if ($StopPoint -ceq 'Preparing') { $stopUtc = $start.AddSeconds(2) }
    else {
        $startedUtc = $start.AddSeconds(5)
        $prepared = [pscustomobject]@{ Utc = $startedUtc }
        $stepStarts.Add([pscustomobject]@{ Utc = $startedUtc; StepNumber = 1; Fields = @{ name = 'Sweep' } })
        if ($StopPoint -ceq 'mid') { $stopUtc = $start.AddSeconds(30) }
        else {
            $stepEnds.Add([pscustomobject]@{ Utc = $start.AddSeconds(65); StepNumber = 1; Fields = @{ name = 'Sweep' } })
            $stepStarts.Add([pscustomobject]@{ Utc = $start.AddSeconds(65); StepNumber = 2; Fields = @{ name = 'Refresh hold' } })
            $stopUtc = $start.AddSeconds(70)
        }
    }
    $stopFields = @{}
    if (-not [string]::IsNullOrEmpty($Reason)) { $stopFields['reason'] = $Reason }
    $completedUtc = $stopUtc.AddSeconds($LatencySeconds)

    return [pscustomobject]@{
        TestType          = 'Quick'
        Profile           = 'known-passing'
        Ordinal           = $Ordinal
        FileName          = ('stop-trial-{0}-summary.json' -f $Ordinal)
        Steps             = @(
            [pscustomobject]@{ Index = 1; Name = 'Sweep'; HoldSeconds = 0.0 }
            [pscustomobject]@{ Index = 2; Name = 'Refresh hold'; HoldSeconds = 5.0 }
        )
        StartRequestedUtc = $start
        StartedUtc        = $startedUtc
        EndedUtc          = $completedUtc
        ReleaseSeconds    = 0.42
        TestedBytes       = 1073741824
        Log               = [pscustomobject]@{
            Exists        = $true
            Prepared      = $prepared
            StepStarts    = $stepStarts.ToArray()
            StepEnds      = $stepEnds.ToArray()
            StopRequested = [pscustomobject]@{ Utc = $stopUtc; Fields = $stopFields }
            Completed     = [pscustomobject]@{ Utc = $completedUtc }
        }
    }
}

function Get-DocumentStrings {
    # Every heading, table header and line prefix the report emits, for the document cross-check.
    $strings = New-Object 'System.Collections.Generic.List[string]'
    foreach ($heading in $Headings.Values) { $strings.Add($heading) }
    foreach ($header in $Headers.Values) { $strings.Add((Format-MarkdownTableHeader -Header $header)) }
    $strings.Add($FastestBaselinePrefix)
    $strings.Add($StressStatusPrefix)
    return , $strings.ToArray()
}

function Invoke-SelfTest {
    # Runs every case in memory and returns the exit code: 0 when all pass, 1 otherwise.
    $cases = [ordered]@{}

    $cases['median-interval: ABORTED censored at 5 s beside FAIL 200 s and 210 s'] = {
        $interval = Get-MedianInterval -Values @((Get-TestObservation 5 $true), (Get-TestObservation 200 $false), (Get-TestObservation 210 $false))
        Assert-Near $interval.L 200 'L'
        Assert-Near $interval.U 210 'U (the censored 5 s is not ranked below 200)'
        Assert-Equal $interval.N 3 'N'
        Assert-Equal $interval.CensoredCount 1 'CensoredCount'
    }

    $cases['median-interval: censored 5 s beside observed 200 s (even count)'] = {
        $interval = Get-MedianInterval -Values @((Get-TestObservation 5 $true), (Get-TestObservation 200 $false))
        Assert-Near $interval.L 102.5 'L'
        Assert-Near $interval.U ([double]::PositiveInfinity) 'U'
    }

    $cases['median-interval: censored runs at the median position'] = {
        $interval = Get-MedianInterval -Values @((Get-TestObservation 100 $false), (Get-TestObservation 150 $true), (Get-TestObservation 160 $true))
        Assert-Near $interval.L 150 'L'
        Assert-Near $interval.U ([double]::PositiveInfinity) 'U'
    }

    $cases['median-interval: no censoring and no values'] = {
        $odd = Get-MedianInterval -Values @((Get-TestObservation 300 $false), (Get-TestObservation 100 $false), (Get-TestObservation 200 $false))
        Assert-Near $odd.L 200 'odd L'
        Assert-Near $odd.U 200 'odd U'
        $even = Get-MedianInterval -Values @((Get-TestObservation 100 $false), (Get-TestObservation 200 $false), (Get-TestObservation 300 $false), (Get-TestObservation 400 $false))
        Assert-Near $even.L 250 'even L'
        Assert-Near $even.U 250 'even U'
        $empty = Get-MedianInterval -Values @()
        Assert-Near $empty.L $null 'empty L'
        Assert-Near $empty.U $null 'empty U'
        Assert-Equal $empty.N 0 'empty N'
    }

    $cases['baseline: censoring rule and fastest baseline median'] = {
        $rows = @(
            [pscustomobject]@{ tool = 'A'; run = '1'; minutesToFirstError = '10'; censored = '0' }
            [pscustomobject]@{ tool = 'A'; run = '2'; minutesToFirstError = '12'; censored = 'false' }
            [pscustomobject]@{ tool = 'A'; run = '3'; minutesToFirstError = '130'; censored = '0' }
            [pscustomobject]@{ tool = 'B'; run = '1'; minutesToFirstError = '20'; censored = '0' }
            [pscustomobject]@{ tool = 'B'; run = '2'; minutesToFirstError = '25'; censored = '0' }
            [pscustomobject]@{ tool = 'B'; run = '3'; minutesToFirstError = '30'; censored = '0'; configuration = 'extreme@anta777' }
        )
        $runs = ConvertFrom-BaselineRows -Rows $rows
        Assert-Near $runs[2].Value 120 'A run 3 value'
        Assert-Equal $runs[2].Censored $true 'A run 3 censored'
        Assert-Equal $runs[0].Configuration $Sym.Dash 'missing configuration'
        Assert-Equal $runs[5].Configuration 'extreme@anta777' 'configuration'
        $fastest = Get-FastestBaseline -Runs $runs
        Assert-Near $fastest.Tools[0].L 12 'A L'
        Assert-Near $fastest.Tools[0].U 12 'A U'
        Assert-Near $fastest.Tools[1].L 25 'B L'
        Assert-Near $fastest.L 12 'B L bound'
        Assert-Near $fastest.U 12 'B U bound'
        Assert-Equal $fastest.DecidingTool 'A' 'deciding tool'
        $greater = ConvertFrom-BaselineMinutes -MinutesText '> 120' -CensoredText '1'
        Assert-Near $greater.Value 120 '"> 120" value'
        Assert-Equal $greater.Censored $true '"> 120" censored'
        $stopped = ConvertFrom-BaselineMinutes -MinutesText '45.5' -CensoredText 'true'
        Assert-Near $stopped.Value 45.5 'censored 45.5 value'
        Assert-Equal $stopped.Censored $true 'censored 45.5 flag'
        $rejected = $false
        try { $null = ConvertFrom-BaselineMinutes -MinutesText '12' -CensoredText 'maybe' } catch { $rejected = $true }
        Assert-True $rejected 'an unreadable censored cell is rejected'
    }

    $cases['baseline: evidence gaps for Karhu, TestMem5 and nominated tools'] = {
        $complete = ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                Get-TestBaselineRow 'Karhu RAM Test' $i '30' '0' 'Default settings'
                Get-TestBaselineRow 'TestMem5' $i '40' '0' 'extreme@anta777'
                Get-TestBaselineRow 'HCI MemTest' $i '50' '0' '400 %'
            })
        Assert-Equal ((Get-BaselineEvidenceGap -Runs $complete) -join ' | ') '' 'complete evidence'
        $noConfiguration = ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                Get-TestBaselineRow 'karhu' $i '30' '0'
                Get-TestBaselineRow 'TM5' $i '> 120' '1'
            })
        Assert-Equal ((Get-BaselineEvidenceGap -Runs $noConfiguration) -join ' | ') '' 'no configuration column'

        $oneRow = Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows @(Get-TestBaselineRow 'Karhu RAM Test' '1' '> 120' '1'))
        Assert-Equal ($oneRow -join ' | ') ('TestMem5 (extreme@anta777) has no run in the baseline CSV; at least 3 are required. | ' +
            'Karhu RAM Test has 1 run(s); at least 3 are required.') 'one Karhu row'

        $flawed = Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows @(
                (Get-TestBaselineRow 'Karhu RAM Test' '1' '30' '0' 'Default settings'),
                (Get-TestBaselineRow 'Karhu RAM Test' '2' '31' '0' 'Default settings'),
                (Get-TestBaselineRow 'Karhu RAM Test' '2' '32' '0' 'Default settings'),
                (Get-TestBaselineRow 'TestMem5' '1' '40' '0' '1usmus_v3'),
                (Get-TestBaselineRow 'TestMem5' '2' '41' '0' '1usmus_v3'),
                (Get-TestBaselineRow 'TestMem5' '3' '42' '0' '1usmus_v3'),
                (Get-TestBaselineRow 'HCI MemTest' '1' '50' '0')))
        Assert-Equal ($flawed -join ' | ') ('Karhu RAM Test lists run 2 more than once. | TestMem5 runs use configuration "1usmus_v3", not extreme@anta777. | ' +
            'HCI MemTest has 1 run(s); at least 3 are required.') 'duplicate run, TestMem5 1usmus_v3, one nominated run'

        $split = Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                    Get-TestBaselineRow 'Karhu RAM Test' $i '30' '0'
                    Get-TestBaselineRow 'Karhu' $i '31' '0'
                    Get-TestBaselineRow 'TestMem5' $i '40' '0'
                }))
        Assert-Equal ($split -join ' | ') 'Karhu RAM Test is listed under several tool names (Karhu RAM Test, Karhu); use one name per tool.' 'one tool, two names'
        $mixed = Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows (@(foreach ($i in 1..3) {
                        Get-TestBaselineRow 'Karhu RAM Test' $i '30' '0'
                        Get-TestBaselineRow 'TestMem5' $i '40' '0' 'extreme@anta777'
                    }) + @(Get-TestBaselineRow 'TestMem5' '4' '41' '0' '1usmus_v3')))
        Assert-Equal ($mixed -join ' | ') ('TestMem5 mixes configurations (extreme@anta777, 1usmus_v3); each tool''s median needs one configuration. | ' +
            'TestMem5 runs use configuration "1usmus_v3", not extreme@anta777.') 'mixed configurations'
        $both = Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) { Get-TestBaselineRow 'Karhu and TestMem5' $i '30' '0' }))
        Assert-Equal ($both -join ' | ') 'Karhu and TestMem5 matches more than one required tool (Karhu RAM Test, TestMem5); use one name per tool.' 'one name, two tools'

        # Configuration cells are matched exactly after normalisation (case, repeated spaces, one
        # pair of surrounding backticks or quotes); a label that merely contains the procedure
        # configuration names another one. Each entry: Karhu cell, TestMem5 cell, expected gaps.
        foreach ($configurationCase in @(
                @('Default settings', 'extreme@anta777', ''),
                @('default', '`extreme@anta777`', ''),
                @('"Defaults"', 'EXTREME@anta777', ''),
                @('  DEFAULT   Settings ', 'extreme@anta777.cfg', ''),
                @('', "'extreme@anta777'", ''),
                @('Non-default settings', 'extreme@anta777', 'Karhu RAM Test runs use configuration "Non-default settings", not default settings.'),
                @('Non-default settings (CPU cache enabled)', '', 'Karhu RAM Test runs use configuration "Non-default settings (CPU cache enabled)", not default settings.'),
                @('Default settings', 'not extreme@anta777', 'TestMem5 runs use configuration "not extreme@anta777", not extreme@anta777.'),
                @('', 'extreme@anta777 modified', 'TestMem5 runs use configuration "extreme@anta777 modified", not extreme@anta777.'))) {
            $configurationRuns = ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                    Get-TestBaselineRow 'Karhu RAM Test' $i '30' '0' $configurationCase[0]
                    Get-TestBaselineRow 'TestMem5' $i '40' '0' $configurationCase[1]
                })
            Assert-Equal ((Get-BaselineEvidenceGap -Runs $configurationRuns) -join ' | ') $configurationCase[2] ('configuration Karhu "{0}", TestMem5 "{1}"' -f $configurationCase[0], $configurationCase[1])
        }

        # A tool's runs share one configuration when their cells normalise to the same key ignoring
        # case, and every accepted spelling of a required tool's configuration is that one
        # configuration. Each entry: the three Karhu, TestMem5 and HCI MemTest cells (no HCI MemTest
        # runs when empty) and the expected gaps.
        foreach ($spellingCase in @(
                [pscustomobject]@{ Karhu = @('default', 'Default settings', 'defaults'); TestMem5 = @('`extreme@anta777`', 'extreme@anta777', 'EXTREME@anta777.cfg'); Nominated = @(); Expected = '' }
                [pscustomobject]@{ Karhu = @('', '', ''); TestMem5 = @('1usmus_v3', '`1usmus_v3`', '1USMUS_V3'); Nominated = @(); Expected = 'TestMem5 runs use configuration "1usmus_v3", not extreme@anta777.' }
                [pscustomobject]@{ Karhu = @('', '', ''); TestMem5 = @('', '', ''); Nominated = @('X', ' x ', 'X'); Expected = '' }
                [pscustomobject]@{ Karhu = @('', '', ''); TestMem5 = @('', '', ''); Nominated = @('A', 'B', 'A'); Expected = 'HCI MemTest mixes configurations (A, B); each tool''s median needs one configuration.' })) {
            $spellingRows = @(foreach ($i in 0..2) {
                    Get-TestBaselineRow 'Karhu RAM Test' ($i + 1) '30' '0' $spellingCase.Karhu[$i]
                    Get-TestBaselineRow 'TestMem5' ($i + 1) '40' '0' $spellingCase.TestMem5[$i]
                    if ($spellingCase.Nominated.Count -gt 0) { Get-TestBaselineRow 'HCI MemTest' ($i + 1) '50' '0' $spellingCase.Nominated[$i] }
                })
            Assert-Equal ((Get-BaselineEvidenceGap -Runs (ConvertFrom-BaselineRows -Rows $spellingRows)) -join ' | ') $spellingCase.Expected ('configuration spellings Karhu "{0}", TestMem5 "{1}", HCI MemTest "{2}"' -f ($spellingCase.Karhu -join '", "'), ($spellingCase.TestMem5 -join '", "'), ($spellingCase.Nominated -join '", "'))
        }
    }

    $cases['criterion 1: incomplete baseline evidence is never met'] = {
        $runs = @(foreach ($i in 1..10) { foreach ($type in $TestTypes) { Get-TestFailingProfileRun $type 'FAIL' 60 $i } })
        $oneRow = ConvertFrom-BaselineRows -Rows @(Get-TestBaselineRow 'Karhu RAM Test' '1' '> 120' '1')
        $incomplete = Get-Criterion1Result -Runs $runs -Fastest (Get-FastestBaseline -Runs $oneRow) -BaselineGaps (Get-BaselineEvidenceGap -Runs $oneRow)
        foreach ($entry in $incomplete.Types) {
            Assert-Equal $entry.Ratio.Decision 'indeterminate' ('one Karhu row: ' + $entry.Type + ' ratio')
            Assert-Near $entry.Ratio.RatioLow 120 ('one Karhu row: ' + $entry.Type + ' ratio low still shown')
        }
        Assert-Equal $incomplete.Overall 'indeterminate' 'one Karhu row: overall'
        Assert-Equal @($incomplete.BaselineGaps).Count 2 'one Karhu row: gaps carried'
        $summary = Get-CriteriaSummary -Criterion1 $incomplete -Criterion2 $null -CleanRuns $null -StopLatency $null
        Assert-Equal $summary[0].Evidence 'Fail-fast checks (summary JSONs and baseline CSV); baseline evidence incomplete' 'one Karhu row: summary evidence'
        Assert-Equal $summary[0].Status 'indeterminate' 'one Karhu row: summary status'

        $complete = ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                Get-TestBaselineRow 'Karhu RAM Test' $i '> 120' '1'
                Get-TestBaselineRow 'TestMem5' $i '> 120' '1'
            })
        $met = Get-Criterion1Result -Runs $runs -Fastest (Get-FastestBaseline -Runs $complete) -BaselineGaps (Get-BaselineEvidenceGap -Runs $complete)
        foreach ($entry in $met.Types) { Assert-Equal $entry.Ratio.Decision 'met' ('complete baseline: ' + $entry.Type + ' ratio') }
        Assert-Equal $met.Overall 'met' 'complete baseline: overall'
        Assert-Equal @($met.BaselineGaps).Count 0 'complete baseline: no gaps'

        # Three runs of each required tool, but Karhu with non-default settings: B is not the
        # procedure's baseline, so no type's 60x ratio is met.
        $nonDefault = ConvertFrom-BaselineRows -Rows @(foreach ($i in 1..3) {
                Get-TestBaselineRow 'Karhu RAM Test' $i '60' '0' 'Non-default settings'
                Get-TestBaselineRow 'TestMem5' $i '60' '0' 'extreme@anta777'
            })
        $nonDefaultResult = Get-Criterion1Result -Runs $runs -Fastest (Get-FastestBaseline -Runs $nonDefault) -BaselineGaps (Get-BaselineEvidenceGap -Runs $nonDefault)
        foreach ($entry in $nonDefaultResult.Types) {
            Assert-Equal $entry.Ratio.Decision 'indeterminate' ('Karhu non-default settings: ' + $entry.Type + ' ratio')
            Assert-Near $entry.Ratio.RatioLow 60 ('Karhu non-default settings: ' + $entry.Type + ' ratio low still shown')
        }
        Assert-Equal $nonDefaultResult.Overall 'indeterminate' 'Karhu non-default settings: overall'
        Assert-Equal (@($nonDefaultResult.BaselineGaps) -join ' | ') 'Karhu RAM Test runs use configuration "Non-default settings", not default settings.' 'Karhu non-default settings: gap carried'
    }

    $cases['ratio: at least 5x faster against B = 720 s'] = {
        $met = Get-RatioDecision -BaselineLowSeconds 720 -BaselineHighSeconds 720 -TypeLow 100 -TypeHigh 140
        Assert-Equal $met.Decision 'met' 'Quick [100, 140]'
        Assert-Near $met.RatioLow (720.0 / 140.0) 'ratio low'
        Assert-Near $met.RatioHigh 7.2 'ratio high'
        Assert-Equal (Get-RatioDecision -BaselineLowSeconds 720 -BaselineHighSeconds 720 -TypeLow 150 -TypeHigh 160).Decision 'not met' 'Quick [150, 160]'
        Assert-Equal (Get-RatioDecision -BaselineLowSeconds 720 -BaselineHighSeconds 720 -TypeLow 130 -TypeHigh 150).Decision 'indeterminate' 'Quick [130, 150]'
        $open = Get-RatioDecision -BaselineLowSeconds 720 -BaselineHighSeconds 720 -TypeLow 100 -TypeHigh ([double]::PositiveInfinity)
        Assert-Near $open.RatioLow 0 'x / infinity'
        Assert-Equal (Get-RatioDecision -BaselineLowSeconds $null -BaselineHighSeconds $null -TypeLow 100 -TypeHigh 140).Decision 'indeterminate' 'no baseline'
    }

    $cases['ratio: 10-minute baseline and 4-minute Quick meet 5 min but not 5x'] = {
        $observations = @(foreach ($i in 1..10) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 240) })
        $within = Get-WithinFiveMinutesDecision -Observations $observations
        Assert-Equal $within.Decision 'met' 'within 5 minutes'
        $interval = Get-MedianInterval -Values $observations
        $ratio = Get-RatioDecision -BaselineLowSeconds 600 -BaselineHighSeconds 600 -TypeLow $interval.L -TypeHigh $interval.U
        Assert-Equal $ratio.Decision 'not met' 'at least 5x faster'
    }

    $cases['no later than Quick [200, 210]'] = {
        Assert-Equal (Get-NoLaterDecision -TypeLow 190 -TypeHigh 200 -QuickLow 200 -QuickHigh 210) 'met' 'Standard [190, 200]'
        Assert-Equal (Get-NoLaterDecision -TypeLow 215 -TypeHigh 230 -QuickLow 200 -QuickHigh 210) 'not met' 'Standard [215, 230]'
        Assert-Equal (Get-NoLaterDecision -TypeLow 205 -TypeHigh 220 -QuickLow 200 -QuickHigh 210) 'indeterminate' 'Standard [205, 220]'
    }

    $cases['within 5 minutes: 9 of 10 and too few runs'] = {
        $nine = @(foreach ($i in 1..9) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 120) }) + @(Get-FailTimeObservation -Run (Get-TestRun 'PASS' 300.5))
        Assert-Equal (Get-WithinFiveMinutesDecision -Observations $nine).Decision 'met' '9 of 10'
        $eight = @(foreach ($i in 1..8) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 120) }) + @(
            (Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 301)), (Get-FailTimeObservation -Run (Get-TestRun 'ABORTED' 5)))
        Assert-Equal (Get-WithinFiveMinutesDecision -Observations $eight).Decision 'not met' '8 of 10'
        $sevenOneMiss = @(foreach ($i in 1..6) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 120) }) + @(Get-FailTimeObservation -Run (Get-TestRun 'PASS' 300))
        Assert-Equal (Get-WithinFiveMinutesDecision -Observations $sevenOneMiss).Decision 'indeterminate' '7 runs, 1 miss'
        $sevenTwoMisses = @(foreach ($i in 1..5) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 120) }) + @(
            (Get-FailTimeObservation -Run (Get-TestRun 'PASS' 300)), (Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 400)))
        Assert-Equal (Get-WithinFiveMinutesDecision -Observations $sevenTwoMisses).Decision 'not met' '7 runs, 2 misses'
        $recovered = Get-FailTimeObservation -Run (Get-TestRun 'FAIL' $null)
        Assert-Equal $recovered.Kind 'excluded' 'recovered FAIL kind'
        $withRecovered = @(foreach ($i in 1..9) { Get-FailTimeObservation -Run (Get-TestRun 'FAIL' 120) }) + @($recovered)
        $fiveMinutes = Get-WithinFiveMinutesDecision -Observations $withRecovered
        Assert-Equal $fiveMinutes.N 10 'recovered FAIL counted in n'
        Assert-Equal $fiveMinutes.K 9 'recovered FAIL is a miss'
        $medianInput = @($withRecovered | Where-Object { $_.Kind -cne 'excluded' })
        Assert-Equal (Get-MedianInterval -Values $medianInput).N 9 'recovered FAIL excluded from the median input'
    }

    $cases['criterion 2: consecutive PASS streaks'] = {
        $tenPass = [string[]]@(foreach ($i in 1..10) { 'PASS' })
        Assert-Equal (Get-ConsecutivePassDecision -Verdicts $tenPass -Required 10).Decision 'met' 'Quick 10 x PASS'
        $ninePassFail = [string[]](@(foreach ($i in 1..9) { 'PASS' }) + @('FAIL'))
        $failed = Get-ConsecutivePassDecision -Verdicts $ninePassFail -Required 10
        Assert-Equal $failed.Decision 'not met' 'Quick 9 x PASS then FAIL'
        Assert-Equal @($failed.FailRuns)[0] 10 'FAIL position'
        $fivePass = [string[]]@(foreach ($i in 1..5) { 'PASS' })
        $short = Get-ConsecutivePassDecision -Verdicts $fivePass -Required 10
        Assert-Equal $short.Decision 'indeterminate' 'Quick 5 x PASS'
        Assert-Equal $short.LongestStreak 5 'streak length'
        Assert-Equal (Get-ConsecutivePassDecision -Verdicts ([string[]]@('PASS', 'PASS', 'PASS')) -Required 3).Decision 'met' 'Exhaustive 3 x PASS'
        $broken = Get-ConsecutivePassDecision -Verdicts ([string[]]@('PASS', 'ABORTED', 'PASS', 'PASS')) -Required 3
        Assert-Equal $broken.Decision 'indeterminate' 'ABORTED breaks a streak'
        Assert-Equal @($broken.AbortedRuns)[0] 2 'ABORTED position'
        $marked = Get-ConsecutivePassDecision -Verdicts ([string[]]@('PASS', 'PASS', $NotFullLengthVerdict, 'PASS', 'PASS')) -Required 3
        Assert-Equal -Actual $marked.Decision -Expected 'indeterminate' -What 'a PASS that is not full-length evidence breaks a streak'
        Assert-Equal -Actual $marked.LongestStreak -Expected 2 -What 'streak length around the not full-length run'
        Assert-Equal -Actual @($marked.IneligibleRuns)[0] -Expected 3 -What 'not full-length position'
        Assert-Equal -Actual @($marked.AbortedRuns).Count -Expected 0 -What 'not full-length is not ABORTED'
        Assert-Equal -Actual @($marked.FailRuns).Count -Expected 0 -What 'not full-length is not FAIL'
    }

    $cases['time remaining: gate, ungated samples and PROGRESS lines'] = {
        $end = [DateTimeOffset]::new(2026, 10, 28, 14, 30, 0, [TimeSpan]::Zero)
        $pass = @([pscustomobject]@{ Utc = $end.AddSeconds(-100); StepNumber = 2; RemainingMs = 108000.0 })
        $passResult = Measure-TimeRemaining -Samples $pass -EndUtc $end
        Assert-Equal $passResult.Decision 'met' 'actual 100 s, displayed 108 s'
        Assert-Near $passResult.WorstGatedPercent 8 'worst gated error'
        $fail = @([pscustomobject]@{ Utc = $end.AddSeconds(-100); StepNumber = 2; RemainingMs = 111000.0 })
        Assert-Equal (Measure-TimeRemaining -Samples $fail -EndUtc $end).Decision 'not met' 'actual 100 s, displayed 111 s'
        $late = @(
            [pscustomobject]@{ Utc = $end.AddSeconds(-100); StepNumber = 3; RemainingMs = 100000.0 }
            [pscustomobject]@{ Utc = $end.AddSeconds(-9); StepNumber = 6; RemainingMs = 5000.0 }
        )
        $lateResult = Measure-TimeRemaining -Samples $late -EndUtc $end
        Assert-Equal $lateResult.Decision 'met' 'a sample under 10 s is not gated'
        Assert-Equal $lateResult.GatedCount 1 'gated count'
        Assert-Equal $lateResult.UngatedCount 1 'ungated count'
        Assert-Near $lateResult.WorstUngatedPercent (100.0 * 4.0 / 9.0) 'worst ungated error'

        $line = '2026-10-28T14:05:52.120Z PROGRESS step=3/6 elapsed_ms=104250 remaining_ms=195750 coverage=412.6 errors=0'
        $record = ConvertFrom-RunLogLine -Line $line
        Assert-Equal $record.Event 'PROGRESS' 'event'
        Assert-Equal $record.StepNumber 3 'step k (1-based)'
        Assert-Equal $record.StepCount 6 'step count'
        Assert-Equal $record.Utc.UtcTicks ([DateTimeOffset]::new(2026, 10, 28, 14, 5, 52, 120, [TimeSpan]::Zero)).UtcTicks 'timestamp'
        $sample = ConvertFrom-ProgressRecord -Record $record
        Assert-Near $sample.ElapsedMs 104250 'elapsed_ms'
        Assert-Near $sample.RemainingMs 195750 'remaining_ms'
        Assert-Near $sample.Coverage 412.6 'coverage'
        Assert-Near $sample.Errors 0 'errors'

        $calculating = ConvertFrom-ProgressRecord -Record (ConvertFrom-RunLogLine -Line '2026-10-28T14:00:10.000Z PROGRESS step=2/6 elapsed_ms=10000 remaining_ms=-1 coverage=4.0 errors=0')
        Assert-Near $calculating.RemainingMs -1 'remaining_ms=-1'
        $unavailable = Measure-TimeRemaining -Samples @($calculating, [pscustomobject]@{ Utc = $end.AddSeconds(-50); StepNumber = 1; RemainingMs = 1.0 }) -EndUtc $end
        Assert-Equal -Actual $unavailable.GatedCount -Expected 1 -What 'a step-2 remaining_ms=-1 sample is gated and the step-1 sample ignored'
        Assert-Equal -Actual $unavailable.GatedWithin -Expected 0 -What 'an unavailable estimate is outside +-10 %'
        Assert-Equal -Actual $unavailable.Decision -Expected 'not met' -What 'remaining_ms=-1 after step 1'
        Assert-Equal -Actual $unavailable.UnavailableCount -Expected 1 -What 'unavailable estimates after step 1'
        Assert-Equal -Actual $unavailable.UngatedCount -Expected 0 -What 'no ungated sample'
        Assert-Near -Actual $unavailable.WorstGatedPercent -Expected $null -What 'no deviation without an estimate'
        $missing = ConvertFrom-ProgressRecord -Record (ConvertFrom-RunLogLine -Line '2026-10-28T14:29:55.000Z PROGRESS step=6/6 elapsed_ms=1790000 coverage=240.0 errors=0')
        Assert-Near -Actual $missing.RemainingMs -Expected $null -What 'a PROGRESS line without remaining_ms'
        $lateUnavailable = Measure-TimeRemaining -Samples @($pass[0], $missing) -EndUtc $end
        Assert-Equal -Actual $lateUnavailable.Decision -Expected 'met' -What 'an unavailable sample under 10 s is reported, not gated'
        Assert-Equal -Actual $lateUnavailable.UngatedCount -Expected 1 -What 'the unavailable sample under 10 s is ungated'
        Assert-Equal -Actual $lateUnavailable.UnavailableCount -Expected 1 -What 'the unavailable sample under 10 s is counted'
        Assert-Near -Actual $lateUnavailable.WorstUngatedPercent -Expected $null -What 'no ungated deviation without an estimate'

        $quoted = ConvertFrom-RunLogLine -Line '2026-10-28T14:05:51.120Z STEP_START step=3/6 name="Read-after-write \"turnaround\" \\ x"'
        Assert-Equal $quoted.Fields['name'] 'Read-after-write "turnaround" \ x' 'quoted name'
        $stop = ConvertFrom-RunLogLine -Line '2026-10-28T14:06:00.000Z STOP_REQUESTED reason=UserStop'
        Assert-Equal $stop.Fields['reason'] 'UserStop' 'stop reason'
        Assert-Equal (ConvertFrom-RunLogLine -Line 'not a log line') $null 'malformed line'
    }

    $cases['time remaining: one accurate estimate followed only by -1 samples is not met'] = {
        $end = [DateTimeOffset]::new(2026, 10, 28, 14, 30, 0, [TimeSpan]::Zero)
        $samples = @(
            [pscustomobject]@{ Utc = $end.AddSeconds(-200); StepNumber = 2; RemainingMs = 200000.0 }
            [pscustomobject]@{ Utc = $end.AddSeconds(-190); StepNumber = 2; RemainingMs = -1.0 }
            [pscustomobject]@{ Utc = $end.AddSeconds(-100); StepNumber = 4; RemainingMs = -1.0 }
        )
        $outcome = Measure-TimeRemaining -Samples $samples -EndUtc $end
        Assert-Equal -Actual $outcome.Decision -Expected 'not met' -What 'one accurate estimate, then two unavailable'
        Assert-Equal -Actual $outcome.GatedCount -Expected 3 -What 'every sample after step 1 is gated'
        Assert-Equal -Actual $outcome.GatedWithin -Expected 1 -What 'only the accurate estimate is within +-10 %'
        Assert-Equal -Actual $outcome.GatedFailures -Expected 2 -What 'the unavailable estimates are failures'
        Assert-Equal -Actual $outcome.UnavailableCount -Expected 2 -What 'unavailable estimates after step 1'
        Assert-Near -Actual $outcome.WorstGatedPercent -Expected 0 -What 'worst deviation over the samples with an estimate'
    }

    $cases['time remaining: a -1 sample in step 1 alone stays indeterminate'] = {
        $end = [DateTimeOffset]::new(2026, 10, 28, 14, 30, 0, [TimeSpan]::Zero)
        $outcome = Measure-TimeRemaining -Samples @([pscustomobject]@{ Utc = $end.AddSeconds(-290); StepNumber = 1; RemainingMs = -1.0 }) -EndUtc $end
        Assert-Equal -Actual $outcome.Decision -Expected 'indeterminate' -What 'calculating during step 1 only'
        Assert-Equal -Actual $outcome.GatedCount -Expected 0 -What 'a step-1 sample is not gated'
        Assert-Equal -Actual $outcome.UngatedCount -Expected 0 -What 'a step-1 sample is not reported'
        Assert-Equal -Actual $outcome.UnavailableCount -Expected 0 -What 'calculating in step 1 is expected'
    }

    $cases['duration within +-15 %'] = {
        $within = Test-DurationWithinTolerance -DurationSeconds 340 -TargetSeconds 300
        Assert-Equal $within.Within $true '340 s against 300 s'
        Assert-Near $within.DeviationPercent (100.0 * 40.0 / 300.0) 'deviation 13.3 %'
        $outside = Test-DurationWithinTolerance -DurationSeconds 350 -TargetSeconds 300
        Assert-Equal $outside.Within $false '350 s against 300 s'
        Assert-Near $outside.DeviationPercent (100.0 * 50.0 / 300.0) 'deviation 16.7 %'
    }

    $cases['full-length eligibility: scaled and inconsistent summaries are not full-length evidence'] = {
        Assert-Equal -Actual (Get-FullLengthIneligibility -Run (Get-TestCleanRun)) -Expected $null -What 'full-length Quick: 300 s, target 200 %, 240 %, reached'
        Assert-Equal -Actual (Get-FullLengthIneligibility -Run (Get-TestCleanRun -TargetSeconds 30)) -What 'scaled Quick (30 s)' `
            -Expected 'plan.targetSeconds is 30 s, not the 300 s of the full-length Quick plan'
        Assert-Equal -Actual (Get-FullLengthIneligibility -Run (Get-TestCleanRun -CoveragePercent 25 -CoverageTargetPercent 20)) -What 'coverage target 20 %' `
            -Expected 'coverage.targetPercent is 20 %, below the 200 % target of the full-length Quick plan'
        $standard = Get-TestCleanRun -Type 'Standard' -TargetSeconds 1200 -DurationSeconds 1210 -CoveragePercent 500 -CoverageTargetPercent 800 -CoverageTargetReached $true
        Assert-Equal -Actual (Get-FullLengthIneligibility -Run $standard) -What 'targetReached true at 500 % of 800 %' `
            -Expected 'coverage.targetReached is true, but coverage.percent 500 % does not reach coverage.targetPercent 800 %'
        Assert-True -Condition ($null -ne (Get-FullLengthIneligibility -Run (Get-TestCleanRun -TargetSeconds $null))) -What 'plan.targetSeconds missing'
        Assert-True -Condition ($null -ne (Get-FullLengthIneligibility -Run (Get-TestCleanRun -Type 'quick'))) -What 'test type matched case-sensitively'

        $scaled = Get-CleanRunResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = @(Get-TestCleanRun -TargetSeconds 30 -DurationSeconds 30.6) })
        Assert-Equal -Actual @($scaled.Rows).Count -Expected 0 -What 'a scaled PASS is not a criteria 5 and 6 row'
        Assert-Equal -Actual @($scaled.Ineligible).Count -Expected 1 -What 'a scaled PASS is listed as not full-length'
        Assert-Equal -Actual $scaled.CoverageDecision -Expected 'indeterminate' -What 'coverage without a full-length run'
        Assert-Equal -Actual $scaled.DurationDecision -Expected 'indeterminate' -What 'duration without a full-length run'
        $row = @((Get-CleanRunResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = @(Get-TestCleanRun -DurationSeconds 340) })).Rows)[0]
        Assert-Near -Actual $row.PlannedSeconds -Expected 300 -What 'planned seconds of the full-length Quick plan'
        Assert-Equal -Actual $row.Duration.Within -Expected $true -What '340 s against the stated 300 s'
        Assert-Equal -Actual $row.Coverage -Expected 'met' -What '240 % reaches its target and the 200 % floor'
        $short = @((Get-CleanRunResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = @(Get-TestCleanRun -CoveragePercent 190 -CoverageTargetReached $false) })).Rows)[0]
        Assert-Equal -Actual $short.Coverage -Expected 'not met' -What '190 % of a 200 % target'
        $unknown = @((Get-CleanRunResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = @(Get-TestCleanRun -CoverageTargetReached $null) })).Rows)[0]
        Assert-Equal -Actual $unknown.Coverage -Expected 'indeterminate' -What 'targetReached missing'

        $quickRuns = @(foreach ($i in 1..10) { if ($i -eq 4) { Get-TestCleanRun -Ordinal $i -TargetSeconds 30 } else { Get-TestCleanRun -Ordinal $i } })
        $criterion2 = Get-Criterion2Result -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = $quickRuns })
        $quick = @($criterion2.Rows | Where-Object { $_.Type -ceq 'Quick' })[0].Result
        Assert-Equal -Actual $quick.Decision -Expected 'indeterminate' -What 'ten Quick PASS, one of them scaled'
        Assert-Equal -Actual @($quick.IneligibleRuns)[0] -Expected 4 -What 'scaled run position'
        Assert-Equal -Actual $quick.LongestStreak -Expected 6 -What 'longest full-length streak'
        Assert-Equal -Actual @($criterion2.Notes).Count -Expected 1 -What 'one note per scaled run'
        Assert-True -Condition (@($criterion2.Notes)[0] -like '*is not full-length evidence: plan.targetSeconds is 30 s*') -What 'the note names the reason'
    }

    $cases['rolling mean and peak'] = {
        $values = [double[]]@(foreach ($i in 1..20) { if ($i -ge 11 -and $i -le 15) { 30.0 } else { 10.0 } })
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $values)) -Expected 20 -What 'peak 10-s mean'
        Assert-Near (Measure-Peak -Values $values) 30 'peak sample'
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values @(1.0, 2.0, 3.0))) -Expected $null -What 'fewer samples than a window'
    }

    $cases['rolling mean: 10-s windows by timestamp across a gap and empty cells'] = {
        # 30 s of rows absent between two runs of five 45 spikes over a base of 30: joined by
        # sample count the ten spikes would make a 45 window, but no 10-s window holds them all.
        # The peak is the window after the gap that holds the five spikes and four base samples,
        # complete with 9 of its 10 seconds logged.
        $seconds = [double[]]@(@(0..29) + @(60..89))
        $gapValues = @(foreach ($t in $seconds) {
                if (($t -ge 25 -and $t -le 29) -or ($t -ge 60 -and $t -le 64)) { 45.0 } else { 30.0 }
            })
        $gapPeak = Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $gapValues -Seconds $seconds)
        Assert-Near -Actual $gapPeak -Expected ((5.0 * 45.0 + 4.0 * 30.0) / 9.0) -What 'peak over complete 10-s windows only'
        Assert-True ($gapPeak -lt 45.0) 'spikes on either side of the gap are not joined'

        # An empty cell stays in place: the window ending at 11 s holds nine numeric values (3 s is
        # empty), mean (8 x 10 + 100) / 9 = 20. Dropping the cell and counting ten values gives 19.
        $nullSeconds = [double[]]@(1..12)
        $nullValues = @(foreach ($t in $nullSeconds) { if ($t -eq 3) { $null } elseif ($t -eq 11) { 100.0 } else { 10.0 } })
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $nullValues -Seconds $nullSeconds)) -Expected 20 -What 'empty cell kept on the time axis'
        $twoEmpty = @(foreach ($t in 1..10) { if ($t -eq 3 -or $t -eq 4) { $null } else { 10.0 } })
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $twoEmpty -Seconds ([double[]]@(1..10)))) -Expected $null -What 'a window with 8 of 10 values is not complete'
    }

    $cases['rolling mean: 1-s slots, not samples, complete a window and weigh in its mean'] = {
        # Ten samples within one second fill one or two of its 1-s slots: counted by samples they
        # would make a complete window, counted by seconds they do not.
        $burstSeconds = [double[]]@(foreach ($k in 0..9) { $k / 10.0 })
        $burst = Get-TestSampleSet -Values @(foreach ($k in 0..9) { 50.0 }) -Seconds $burstSeconds
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples $burst) -Expected $null -What 'a 1-s burst of 10 samples is no complete window'

        # Nine seconds at 10 and a tenth second polled five times at 100: every second weighs the
        # same, so the peak is (9 x 10 + 100) / 10 = 19, not the sample mean (90 + 500) / 14.
        $weightSeconds = [double[]]@(@(0..8) + @(9.0, 9.1, 9.2, 9.3, 9.4))
        $weightValues = @(foreach ($t in $weightSeconds) { if ($t -ge 9) { 100.0 } else { 10.0 } })
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $weightValues -Seconds $weightSeconds)) -Expected 19 -What 'mean over covered seconds, not over samples'

        # Polling jitter of 4 ms either side of each second keeps every sample in its own slot,
        # though every other sample lands 8 ms short of a whole second after the first one: the
        # window of the first nine samples is complete, (8 x 10 + 100) / 9 = 20.
        $jitterSeconds = [double[]]@(foreach ($k in 0..9) { if ($k % 2 -eq 0) { $k + 0.004 } else { $k - 0.004 } })
        $jitterValues = @(foreach ($k in 0..9) { if ($k -eq 5) { 100.0 } else { 10.0 } })
        Assert-Near -Actual (Measure-RollingMeanPeak -Samples (Get-TestSampleSet -Values $jitterValues -Seconds $jitterSeconds)) -Expected 20 -What 'jittered 1-s polling fills one slot per sample'
    }

    $cases['stress bound: met, not met and unverified'] = {
        $references = @((Get-TestStatistic 'llama' 50 100 50), (Get-TestStatistic '7zip' 40 120 45))
        $met = Get-StressBoundVerdict -References $references -Run (Get-TestStatistic 'exhaustive' 45 110 48)
        Assert-Equal $met.Decision 'met' 'bandwidth 45 against {50, 40}'
        Assert-Near $met.Rows[0].Bound 50 'bandwidth bound'
        $notMet = Get-StressBoundVerdict -References $references -Run (Get-TestStatistic 'exhaustive' 55 110 48)
        Assert-Equal $notMet.Decision 'not met' 'bandwidth 55 against {50, 40}'
        Assert-True (@($notMet.Exceeding) -contains 'DRAM read+write bandwidth') 'exceeding metric named'
        $missingPower = @((Get-TestStatistic 'llama' 50 100 50), (Get-TestStatistic '7zip' 40 $null 45))
        $unverified = Get-StressBoundVerdict -References $missingPower -Run (Get-TestStatistic 'exhaustive' 55 110 48)
        Assert-Equal $unverified.Decision 'unverified' 'CPU package power missing from one log'
        Assert-True ($unverified.Reason -like '*CPU package power*') 'missing metric named'
        $oneReference = Get-StressBoundVerdict -References @((Get-TestStatistic 'llama' 50 100 50)) -Run (Get-TestStatistic 'exhaustive' 45 110 48)
        Assert-Equal $oneReference.Decision 'unverified' 'one reference log only'
        Assert-Equal (Get-StressBoundVerdict -References $references -Run $null).Decision 'unverified' 'no RAMQuick log'
    }

    $cases['stress bound: incomplete evidence leaves every row unverified'] = {
        $llama = Get-TestStatistic -Name 'llama' -Bandwidth 50 -PackagePower 100 -DimmTemperature 50
        $noPower = Get-TestStatistic -Name '7zip' -Bandwidth 40 -PackagePower $null -DimmTemperature 45
        $hot = Get-TestStatistic -Name 'exhaustive' -Bandwidth 55 -PackagePower 110 -DimmTemperature 48
        $partial = Get-StressBoundVerdict -References @($llama, $noPower) -Run $hot
        Assert-Equal -Actual $partial.Decision -Expected 'unverified' -What 'power missing'
        Assert-Equal -Actual $partial.Rows[0].Decision -Expected 'unverified' -What 'bandwidth 55 above 50 is not judged without power'
        Assert-Equal -Actual $partial.Rows[2].Decision -Expected 'unverified' -What 'DIMM temperature is not judged without power'
        Assert-Equal -Actual @($partial.Exceeding).Count -Expected 0 -What 'no exceeding metric on incomplete evidence'
        Assert-Near -Actual $partial.Rows[0].RunValue -Expected 55 -What 'raw run value kept'
        Assert-Near -Actual $partial.Rows[0].Bound -Expected 50 -What 'raw bound kept'

        $sevenZip = Get-TestStatistic -Name '7zip' -Bandwidth 40 -PackagePower 120 -DimmTemperature 45
        $within = Get-TestStatistic -Name 'exhaustive' -Bandwidth 45 -PackagePower 110 -DimmTemperature 48
        $problem = 'a completed full-length Exhaustive run (the summary is a Quick run)'
        $ineligible = Get-StressBoundVerdict -References @($llama, $sevenZip) -Run $within -Problems @($problem)
        Assert-Equal -Actual $ineligible.Decision -Expected 'unverified' -What 'complete metrics from an ineligible run'
        Assert-True ($ineligible.Reason -like ('*' + $problem + '*')) 'the problem is named'
        Assert-Equal -Actual @($ineligible.Rows | Where-Object { $_.Decision -cne 'unverified' }).Count -Expected 0 -What 'every row unverified'

        $nothing = Get-StressBoundVerdict -References @() -Run $null
        Assert-Equal -Actual $nothing.Decision -Expected 'unverified' -What 'no input'
        foreach ($metric in $StressMetrics) {
            Assert-True ($nothing.Reason -like ('*' + $metric.Label + ' (not logged: reference logs and RAMQuick Exhaustive log not supplied)*')) ('{0} named without logs' -f $metric.Label)
        }
    }

    $cases['stress run eligibility: a completed full-length Exhaustive run only'] = {
        # The rule returns its problems as one array (never unrolled), so it is read directly.
        Assert-Equal -Actual (Get-StressRunProblem -Run (Get-TestStressRun)).Count -Expected 0 -What 'completed 3600-s Exhaustive run'
        $none = Get-StressRunProblem -Run $null
        Assert-Equal -Actual $none.Count -Expected 1 -What 'no summary'
        Assert-True ($none[0] -like 'RAMQuick Exhaustive summary (not supplied*') 'missing summary named'
        $ineligible = [ordered]@{
            'Quick run'          = @{ Run = (Get-TestStressRun -TestType 'Quick' -TargetSeconds 300.0); Like = '*the summary is a Quick run*' }
            'user stop'          = @{ Run = (Get-TestStressRun -EndReason 'UserStop'); Like = '*ended with UserStop, not Completed*' }
            'recovered summary'  = @{ Run = (Get-TestStressRun -EndReason 'PreviousRunUnfinished' -Recovered $true); Like = '*recovered from an unfinished run and partial*' }
            'scaled plan'        = @{ Run = (Get-TestStressRun -TargetSeconds 360.0); Like = '*plan.targetSeconds is 360 s, not the 3600 s*' }
            'no plan target'     = @{ Run = (Get-TestStressRun -TargetSeconds $null); Like = '*gives no plan.targetSeconds*' }
            'unknown run window' = @{ Run = (Get-TestStressRun -StartKnown $false); Like = 'the run window (*' }
            'short run'          = @{ Run = (Get-TestStressRun -DurationSeconds 300.0); Like = '*(the run lasted 300.0 s, shorter than the 3060 s a full-length 3600-s run needs)' }
            'no run duration'    = @{ Run = (Get-TestStressRun -DurationSeconds $null); Like = '*(the summary gives no run.durationSeconds)' }
            'short run window'   = @{ Run = (Get-TestStressRun -WindowSeconds 300.0); Like = 'the run window (300.0 s from 2026-10-28T14:00:20.000Z to 2026-10-28T14:05:20.000Z, shorter than a full-length run)' }
            'six steps'          = @{ Run = (Get-TestStressRun -StepCount 6); Like = '*(the summary lists 6 of the 19 steps)' }
            'twenty steps'       = @{ Run = (Get-TestStressRun -StepCount 20); Like = '*(the summary lists 20 steps, not steps 1 to 19 once each)' }
            'unfinished step'    = @{ Run = (Get-TestStressRun -UnfinishedStep 7); Like = '*(step 7 never finished)' }
        }
        foreach ($name in $ineligible.Keys) {
            $problems = Get-StressRunProblem -Run $ineligible[$name].Run
            Assert-True ($problems.Count -gt 0) ('{0} is ineligible' -f $name)
            Assert-True (@($problems | Where-Object { $_ -like $ineligible[$name].Like }).Count -eq 1) ('{0} named: {1}' -f $name, ($problems -join ' | '))
        }
        # A Completed end and the 3600-s target say what was planned, not what ran: each shortfall
        # of the run itself is ineligible on its own, and a short run names all of them.
        foreach ($name in @('short run', 'no run duration', 'short run window', 'six steps', 'twenty steps', 'unfinished step')) {
            Assert-Equal -Actual (Get-StressRunProblem -Run $ineligible[$name].Run).Count -Expected 1 -What ('{0} is its only shortfall' -f $name)
        }
        $shortRun = Get-StressRunProblem -Run (Get-TestStressRun -DurationSeconds 300.0 -WindowSeconds 300.0 -StepCount 6)
        Assert-Equal -Actual $shortRun.Count -Expected 3 -What ('a 300-s six-step run planned at 3600 s names its duration, window and steps: ' + ($shortRun -join ' | '))
        # The floor is 85 % of 3600 s with no upper limit: an overrunning completed run is whole.
        foreach ($seconds in @(3060.0, 3900.0)) {
            Assert-Equal -Actual (Get-StressRunProblem -Run (Get-TestStressRun -DurationSeconds $seconds -WindowSeconds $seconds)).Count -Expected 0 -What ('completed {0}-s Exhaustive run' -f $seconds)
        }
        $reversed = Get-TestStressRun
        $reversed.Log = [pscustomobject]@{ Completed = [pscustomobject]@{ Utc = $reversed.StartedUtc.AddSeconds(-1) } }
        $reversedProblems = Get-StressRunProblem -Run $reversed
        Assert-Equal -Actual $reversedProblems.Count -Expected 1 -What 'only the window is wrong'
        Assert-True ($reversedProblems[0] -like 'the run window (its end * is not after its start *)') ('window reason: ' + $reversedProblems[0])
    }

    $cases['stress section: no input still names every missing log and metric'] = {
        $result = Get-StressResult -HwInfoReferences @() -StressLog $null -StressRun $null
        Assert-Equal -Actual $result.Verdict.Decision -Expected 'unverified' -What 'decision'
        foreach ($part in @('exactly two reference logs (0 supplied)', 'RAMQuick Exhaustive log (not supplied)', 'RAMQuick Exhaustive summary (not supplied')) {
            Assert-True ($result.Verdict.Reason.Contains($part)) ('reason names ' + $part)
        }
        foreach ($metric in $StressMetrics) { Assert-True ($result.Verdict.Reason.Contains($metric.Label)) ('reason names ' + $metric.Label) }
        $lines = New-Object 'System.Collections.Generic.List[string]'
        Write-StressSection -Lines $lines -Result $result -SummarySupplied $false
        $text = $lines.ToArray()
        Assert-True (@($text | Where-Object { $_.StartsWith('Status: unverified (missing: ') }).Count -eq 1) 'Status line'
        Assert-True ($text -contains (Format-MarkdownTableHeader -Header $Headers.StressBound)) 'stress table header'
        Assert-True ($text -contains (Format-MarkdownTableHeader -Header $Headers.StressPerStep)) 'per-step table header'
        $placeholder = '| ' + $Sym.Dash + ' | _not supplied_ | ' + $Sym.Dash + ' | ' + $Sym.Dash + ' | ' + $Sym.Dash + ' | unverified |'
        Assert-True ($text -contains $placeholder) 'per-step placeholder row'
        Assert-Equal -Actual @($text | Where-Object { $_ -ceq '_not supplied_' }).Count -Expected 0 -What 'no bare _not supplied_ line'
    }

    $cases['HWiNFO CSV: duplicate headers, row maximum, read + write sum, trailing rows'] = {
        $deg = $Sym.Deg
        $lines = [string[]]@(
            ('"Date","Time","Memory Read Bandwidth [MB/s]","Memory Write Bandwidth [MB/s]","CPU Package Power [W]","DIMM Temperature [' + $deg + 'C]","DIMM Temperature [' + $deg + 'C]","Core Clock [MHz]","Core Clock [MHz]",')
            '29.10.2026,13:51:01.000,30000,10000,100.5,40,42,5000,5000,'
            '29.10.2026,13:51:02.000,31000,10000,101.5,41,40,5000,5000,'
            '29.10.2026,13:51:03.000,30000,12000,99.0,43,44,5000,5000,'
            ('"Date","Time","Memory Read Bandwidth [MB/s]","Memory Write Bandwidth [MB/s]","CPU Package Power [W]","DIMM Temperature [' + $deg + 'C]","DIMM Temperature [' + $deg + 'C]","Core Clock [MHz]","Core Clock [MHz]",')
            '"","","CPU [#0]: Intel","CPU [#0]: Intel","CPU [#0]: Intel","DIMM #0","DIMM #1","CPU","CPU",'
        )
        $log = ConvertFrom-HwInfoText -Lines $lines -Name 'synthetic' -BandwidthPatterns @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*') `
            -PackagePowerPatterns @('*CPU Package Power*') -DimmTemperaturePatterns @('*DIMM*Temperature*', '*SPD Hub*Temperature*') -UtcOffsetMinutes 0
        Assert-Equal $log.Samples.Count 3 'samples'
        Assert-Equal $log.SkippedRows 2 'trailing header and statistics rows skipped'
        Assert-Equal $log.Missing.Count 0 'no metric missing'
        Assert-Near $log.Samples[0].Bandwidth 40 'row 1 read + write (GB/s)'
        Assert-Near $log.Samples[1].Bandwidth 41 'row 2 read + write (GB/s)'
        Assert-Near $log.Samples[2].Bandwidth 42 'row 3 read + write (GB/s)'
        Assert-Near $log.Samples[0].PackagePower 100.5 'row 1 package power'
        Assert-Near $log.Samples[0].DimmTemperature 42 'row 1 DIMM maximum'
        Assert-Near $log.Samples[1].DimmTemperature 41 'row 2 DIMM maximum'
        Assert-Near $log.Samples[2].DimmTemperature 44 'row 3 DIMM maximum'
        Assert-Equal $log.Samples[0].Utc.UtcTicks ([DateTimeOffset]::new(2026, 10, 29, 13, 51, 1, [TimeSpan]::Zero)).UtcTicks 'row 1 UTC'
        Assert-Near $log.MedianIntervalSeconds 1 'sampling interval'
        $dimms = @($log.Selections['DimmTemperature'])
        Assert-Equal -Actual $dimms.Count -Expected 2 -What 'two DIMM columns selected'
        Assert-Equal -Actual $dimms[0].Name -Expected 'DIMM Temperature' -What 'column name without its unit'
        Assert-Equal -Actual $dimms[0].Ordinal -Expected 1 -What 'first DIMM Temperature column'
        Assert-Equal -Actual $dimms[1].Ordinal -Expected 2 -What 'second DIMM Temperature column'
        Assert-Equal -Actual $dimms[0].Sensor -Expected 'DIMM #0' -What 'first sensor from the trailing sensor row'
        Assert-Equal -Actual $dimms[1].Sensor -Expected 'DIMM #1' -What 'second sensor from the trailing sensor row'
        Assert-Equal -Actual $dimms[0].Unit -Expected ($deg + 'C') -What 'unit'
        Assert-Equal -Actual $dimms[0].Known -Expected $true -What 'unit recognised'
        $write = @($log.Selections['Bandwidth'])[1]
        Assert-Equal -Actual $write.Name -Expected 'Memory Write Bandwidth' -What 'write bandwidth column'
        Assert-Equal -Actual $write.Sensor -Expected 'CPU [#0]: Intel' -What 'bandwidth sensor'

        $semicolon = [string[]]@(
            'Date;Time;Memory Read Bandwidth [GB/s];Memory Write Bandwidth [GB/s];CPU Package Power [W]'
            '29.10.2026;13:51:01.000;12,5;7,5;88,25'
        )
        $german = ConvertFrom-HwInfoText -Lines $semicolon -Name 'semicolon' -BandwidthPatterns @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*') `
            -PackagePowerPatterns @('*CPU Package Power*') -DimmTemperaturePatterns @('*DIMM*Temperature*') -UtcOffsetMinutes 60
        Assert-Near $german.Samples[0].Bandwidth 20 'semicolon log with decimal commas'
        Assert-Near $german.Samples[0].PackagePower 88.25 'decimal comma power'
        Assert-True ($german.Missing.ContainsKey('DimmTemperature')) 'a metric without a column is missing'
        Assert-Equal $german.Samples[0].Utc.UtcTicks ([DateTimeOffset]::new(2026, 10, 29, 12, 51, 1, [TimeSpan]::Zero)).UtcTicks 'UTC+01:00 offset applied'
        Assert-Equal -Actual @($german.Selections['DimmTemperature']).Count -Expected 0 -What 'no DIMM column selected'
        Assert-Equal -Actual @($german.Selections['PackagePower'])[0].Sensor -Expected $null -What 'no sensor row, no sensor'
    }

    $cases['HWiNFO CSV: DIMM temperature only from rows where every DIMM column reports'] = {
        $patterns = @{
            BandwidthPatterns       = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*')
            PackagePowerPatterns    = @('*CPU Package Power*')
            DimmTemperaturePatterns = @('*DIMM*Temperature*')
            UtcOffsetMinutes        = 0
        }
        $dimm = 'DIMM Temperature [' + $Sym.Deg + 'C]'
        $header = '"Date","Time","Memory Read Bandwidth [GB/s]","Memory Write Bandwidth [GB/s]","CPU Package Power [W]","' + $dimm + '","' + $dimm + '",'
        $sensors = '"","","Memory","Memory","CPU [#0]","DIMM #0","DIMM #1",'
        # Row 2 has no DIMM #1 reading: the cooler DIMM #0 alone is not the DIMM maximum, so the
        # row has no DIMM temperature and the empty cell counts against the time coverage.
        $gap = ConvertFrom-HwInfoText -Lines ([string[]]@($header, '29.10.2026,13:51:00.000,30,10,100,40,48,',
                '29.10.2026,13:51:01.000,30,10,100,41,,', '29.10.2026,13:51:02.000,30,10,100,42,49,', $header, $sensors)) -Name 'dimm-gap' @patterns
        Assert-Near -Actual $gap.Samples[0].DimmTemperature -Expected 48 -What 'row 1 DIMM maximum'
        Assert-Near -Actual $gap.Samples[1].DimmTemperature -Expected $null -What 'a row with an empty DIMM cell has no DIMM temperature'
        Assert-Near -Actual $gap.Samples[2].DimmTemperature -Expected 49 -What 'row 3 DIMM maximum'
        Assert-Equal -Actual $gap.Missing.Count -Expected 0 -What 'a sporadic empty cell leaves the metric to the coverage rule'

        # DIMM #1 never reports: no row has every DIMM, so the metric is missing with the silent
        # column named, and no row falls back to DIMM #0 alone.
        $silent = ConvertFrom-HwInfoText -Lines ([string[]]@($header, '29.10.2026,13:51:00.000,30,10,100,40,,',
                '29.10.2026,13:51:01.000,30,10,100,41,,', '29.10.2026,13:51:02.000,30,10,100,42,,', $header, $sensors)) -Name 'dimm1-silent' @patterns
        Assert-Equal -Actual $silent.Missing['DimmTemperature'] -Expected ('no numeric value in column(s) DIMM Temperature #2 [' + $Sym.Deg + 'C] (DIMM #1)') -What 'the empty DIMM column is named'
        Assert-Equal -Actual @($silent.Samples | Where-Object { $null -ne $_.DimmTemperature }).Count -Expected 0 -What 'no row uses DIMM #0 alone'
        Assert-Near -Actual $silent.Samples[0].Bandwidth -Expected 40 -What 'the other metrics are still read'
        $statistic = Measure-HwInfoStatistic -Log $silent -FromUtc $null -ToUtc $null
        Assert-Near -Actual $statistic.Values['DimmTemperature'] -Expected $null -What 'no DIMM statistic without DIMM #1'
    }

    $cases['HWiNFO CSV: 1-s polling, increasing timestamps, non-finite cells and per-metric coverage'] = {
        $patterns = @{
            BandwidthPatterns       = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*')
            PackagePowerPatterns    = @('*CPU Package Power*')
            DimmTemperaturePatterns = @('*DIMM*Temperature*')
            UtcOffsetMinutes        = 0
        }
        $header = '"Date","Time","Memory Read Bandwidth [GB/s]","Memory Write Bandwidth [GB/s]","CPU Package Power [W]","DIMM Temperature [' + $Sym.Deg + 'C]",'
        $fiveSecond = [string[]]@($header, '29.10.2026,13:51:00.000,30,10,100,45,', '29.10.2026,13:51:05.000,30,10,100,45,', '29.10.2026,13:51:10.000,30,10,100,45,')
        $slow = ConvertFrom-HwInfoText -Lines $fiveSecond -Name 'slow' @patterns
        Assert-Equal -Actual $slow.Missing.Count -Expected 3 -What 'every metric of a 5-s log is missing'
        Assert-True ($slow.Missing['Bandwidth'] -like 'sampled every 5.00 s, not at the 1-s polling*') ('5-s reason: ' + $slow.Missing['Bandwidth'])
        $repeated = [string[]]@($header, '29.10.2026,13:51:00.000,30,10,100,45,', '29.10.2026,13:51:01.000,30,10,100,45,', '29.10.2026,13:51:01.000,30,10,100,45,', '29.10.2026,13:51:02.000,30,10,100,45,')
        $backwards = ConvertFrom-HwInfoText -Lines $repeated -Name 'repeated' @patterns
        Assert-Equal -Actual $backwards.Missing.Count -Expected 3 -What 'every metric of a log with a repeated timestamp is missing'
        Assert-True ($backwards.Missing['PackagePower'] -like 'timestamps are not strictly increasing at 29.10.2026 13:51:01.000*') ('order reason: ' + $backwards.Missing['PackagePower'])
        $single = ConvertFrom-HwInfoText -Lines ([string[]]@($header, '29.10.2026,13:51:00.000,30,10,100,45,')) -Name 'single' @patterns
        Assert-True ($single.Missing['DimmTemperature'] -like '1 row(s) with a readable Date and Time, too few*') 'one row cannot show the polling'
        $nonFinite = ConvertFrom-HwInfoText -Lines ([string[]]@($header, '29.10.2026,13:51:00.000,30,10,NaN,Infinity,', '29.10.2026,13:51:01.000,30,10,100,45,')) -Name 'non-finite' @patterns
        Assert-Near -Actual $nonFinite.Samples[0].PackagePower -Expected $null -What 'a NaN cell is empty'
        Assert-Near -Actual $nonFinite.Samples[0].DimmTemperature -Expected $null -What 'an Infinity cell is empty'
        Assert-Near -Actual $nonFinite.Samples[1].PackagePower -Expected 100 -What 'the next row is read'

        # Package power reports for 5 of 20 rows: of the log's own 19 s fewer than the 17 that 1-s
        # polling gives hold a numeric sample, so it is missing while the other metrics are measured.
        $rows = @(foreach ($s in 0..19) {
                $power = ''
                if ($s -lt 5) { $power = '100' }
                ('29.10.2026,13:51:{0:00}.000,30,10,{1},45,' -f $s, $power)
            })
        $dropout = ConvertFrom-HwInfoText -Lines ([string[]](@($header) + $rows)) -Name 'dropout' @patterns
        Assert-Equal -Actual $dropout.Missing.Count -Expected 0 -What 'the log itself is comparable'
        $whole = Measure-HwInfoStatistic -Log $dropout -FromUtc $null -ToUtc $null
        Assert-Near -Actual $whole.Values['Bandwidth'] -Expected 40 -What 'bandwidth measured'
        Assert-Near -Actual $whole.Values['DimmTemperature'] -Expected 45 -What 'DIMM temperature measured'
        Assert-Near -Actual $whole.Values['PackagePower'] -Expected $null -What 'power below 90 % coverage'
        Assert-True ($whole.Reasons['PackagePower'] -like '5 of the log''s 19 s hold a numeric sample, fewer than the 17*') ('coverage reason: ' + $whole.Reasons['PackagePower'])
        $from = [DateTimeOffset]::new(2026, 10, 29, 13, 51, 0, [TimeSpan]::Zero)
        $windowed = Measure-HwInfoStatistic -Log $dropout -FromUtc $from -ToUtc $from.AddSeconds(40)
        Assert-Near -Actual $windowed.Values['Bandwidth'] -Expected $null -What 'a 40-s window holds 20 samples'
        Assert-True ($windowed.Reasons['Bandwidth'] -like '20 of the window''s 40 s hold a numeric sample, fewer than the 36*check -HwInfoUtcOffsetMinutes') ('window reason: ' + $windowed.Reasons['Bandwidth'])
    }

    $cases['HWiNFO CSV: 1-s polling by the share of intervals and coverage by 1-s slots'] = {
        $patterns = @{
            BandwidthPatterns       = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*')
            PackagePowerPatterns    = @('*CPU Package Power*')
            DimmTemperaturePatterns = @('*DIMM*Temperature*')
            UtcOffsetMinutes        = 0
        }
        $header = '"Date","Time","Memory Read Bandwidth [GB/s]","Memory Write Bandwidth [GB/s]","CPU Package Power [W]","DIMM Temperature [' + $Sym.Deg + 'C]",'
        $start = [DateTime]::new(2026, 10, 29, 13, 51, 0)

        # 30 intervals of 1 s, then 20 of 100 ms: the median interval is 1 s, but only 60 % of the
        # intervals are 1 s +- 10 %, so the log does not show 1-s polling.
        $mixedOffsets = @(@(0..30) + @(foreach ($k in 1..20) { 30 + $k / 10.0 }))
        $mixedRows = @(foreach ($offset in $mixedOffsets) { ('29.10.2026,{0},30,10,100,45,' -f $start.AddSeconds($offset).ToString('H:mm:ss.fff', $Invariant)) })
        $mixed = ConvertFrom-HwInfoText -Lines ([string[]](@($header) + $mixedRows)) -Name 'mixed' @patterns
        Assert-Near -Actual $mixed.MedianIntervalSeconds -Expected 1 -What 'median interval of the mixed log'
        Assert-Equal -Actual $mixed.Missing.Count -Expected 3 -What 'every metric of the mixed log is missing'
        $shareReason = 'only 60 % of its sampling intervals are 1 s ' + $Sym.PlusMinus + '10 % (median 1.00 s); both sides must share 1-s polling'
        Assert-Equal -Actual $mixed.Missing['PackagePower'] -Expected $shareReason -What 'share reason'

        # 86 rows at 1 s and a burst of 8 rows 100 ms apart: 91 % of the intervals are 1 s and the
        # 94 numeric samples exceed the 90 a 100-s window needs, but they fill only 87 of its 1-s
        # slots, so they do not cover the seconds left unlogged.
        $denseOffsets = @(@(0..85) + @(foreach ($k in 1..8) { 85 + $k / 10.0 }))
        $denseRows = @(foreach ($offset in $denseOffsets) { ('29.10.2026,{0},30,10,100,45,' -f $start.AddSeconds($offset).ToString('H:mm:ss.fff', $Invariant)) })
        $dense = ConvertFrom-HwInfoText -Lines ([string[]](@($header) + $denseRows)) -Name 'dense' @patterns
        Assert-Equal -Actual $dense.Missing.Count -Expected 0 -What 'at least 90 % of the intervals are 1 s'
        Assert-Equal -Actual $dense.Samples.Count -Expected 94 -What 'dense samples'
        $from = [DateTimeOffset]::new(2026, 10, 29, 13, 51, 0, [TimeSpan]::Zero)
        $covered = Measure-HwInfoStatistic -Log $dense -FromUtc $from -ToUtc $from.AddSeconds(100)
        Assert-Near -Actual $covered.Values['Bandwidth'] -Expected $null -What 'dense samples do not cover unlogged seconds'
        Assert-True -Condition ($covered.Reasons['Bandwidth'] -like '87 of the window''s 100 s hold a numeric sample, fewer than the 90 *; check -HwInfoUtcOffsetMinutes') -What ('slot reason: ' + $covered.Reasons['Bandwidth'])
        Assert-Near -Actual $covered.Values['DimmTemperature'] -Expected $null -What 'the peak sample needs the same coverage'
        $whole = Measure-HwInfoStatistic -Log $dense -FromUtc $null -ToUtc $null
        Assert-Near -Actual $whole.Values['Bandwidth'] -Expected 40 -What 'the log''s own 86 s are covered'
    }

    $cases['HWiNFO CSV: references and run must log the same sensors'] = {
        $patterns = @{
            BandwidthPatterns       = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*')
            PackagePowerPatterns    = @('*CPU Package Power*')
            DimmTemperaturePatterns = @('*SPD Hub*Temperature*')
            UtcOffsetMinutes        = 0
        }
        $spd = 'SPD Hub Temperature [' + $Sym.Deg + 'C]'
        $twoDimms = [string[]]@(
            ('"Date","Time","Memory Read Bandwidth [MB/s]","Memory Write Bandwidth [MB/s]","CPU Package Power [W]","' + $spd + '","' + $spd + '",')
            '29.10.2026,13:51:00.000,30000,10000,100,45,50,'
            '29.10.2026,13:51:01.000,30000,10000,100,45,50,'
            '"","","Memory","Memory","CPU [#0]","DDR5 DIMM [#0]","DDR5 DIMM [#1]",'
        )
        $oneDimm = [string[]]@(
            ('"Date","Time","Memory Read Bandwidth [GB/s]","Memory Write Bandwidth [GB/s]","CPU Package Power [W]","' + $spd + '",')
            '29.10.2026,14:51:00.000,28,10,110,45,'
            '29.10.2026,14:51:01.000,28,10,110,45,'
            '"","","Memory","Memory","CPU [#0]","DDR5 DIMM [#0]",'
        )
        $llama = ConvertFrom-HwInfoText -Lines $twoDimms -Name 'llama' @patterns
        $sevenZip = ConvertFrom-HwInfoText -Lines $twoDimms -Name '7zip' @patterns
        $run = ConvertFrom-HwInfoText -Lines $oneDimm -Name 'exhaustive-dimm0' @patterns
        $mismatch = Get-HwInfoSelectionMismatch -References @($llama, $sevenZip) -Run $run
        Assert-Equal -Actual $mismatch.Count -Expected 1 -What 'one mismatching metric'
        $column = 'SPD Hub Temperature #{0} [' + $Sym.Deg + 'C] (DDR5 DIMM [#{1}])'
        $expected = ('DIMM temperature from the same sensors in every log (llama logs {0}, {1} but exhaustive-dimm0 logs {0})' -f ($column -f 1, 0), ($column -f 2, 1))
        Assert-Equal -Actual $mismatch[0] -Expected $expected -What 'mismatch names both logs'' columns'
        # The same sensors logged in GB/s instead of MB/s: both units convert, so the logs agree.
        $gigabytes = [string[]]@(@($twoDimms[0].Replace('[MB/s]', '[GB/s]')) + @($twoDimms | Select-Object -Skip 1))
        $same = ConvertFrom-HwInfoText -Lines $gigabytes -Name 'exhaustive' @patterns
        Assert-Equal -Actual (Get-HwInfoSelectionMismatch -References @($llama, $sevenZip) -Run $same).Count -Expected 0 -What 'the same sensors in convertible units'
        $references = @(
            (Get-TestStatistic -Name 'llama' -Bandwidth 40 -PackagePower 100 -DimmTemperature 50),
            (Get-TestStatistic -Name '7zip' -Bandwidth 35 -PackagePower 120 -DimmTemperature 48))
        $cooler = Get-TestStatistic -Name 'exhaustive-dimm0' -Bandwidth 38 -PackagePower 110 -DimmTemperature 45
        $verdict = Get-StressBoundVerdict -References $references -Run $cooler -Problems $mismatch
        Assert-Equal -Actual $verdict.Decision -Expected 'unverified' -What 'fewer DIMMs than the references are not judged'
    }

    $cases['HWiNFO CSV: every log must name its sensors in the sensor-name row'] = {
        $patterns = @{
            BandwidthPatterns       = @('*Memory Read Bandwidth*', '*Memory Write Bandwidth*')
            PackagePowerPatterns    = @('*CPU Package Power*')
            DimmTemperaturePatterns = @('*SPD Hub*Temperature*')
            UtcOffsetMinutes        = 0
        }
        $spd = 'SPD Hub Temperature [' + $Sym.Deg + 'C]'
        $header = ('"Date","Time","Memory Read Bandwidth [GB/s]","Memory Write Bandwidth [GB/s]","CPU Package Power [W]","' + $spd + '","' + $spd + '",')
        $rows = @('29.10.2026,13:51:00.000,30,10,100,45,50,', '29.10.2026,13:51:01.000,30,10,100,45,50,')
        $sensors = '"","","Memory","Memory","CPU [#0]","DDR5 DIMM [#0]","DDR5 DIMM [#1]",'
        $named = [string[]](@($header) + $rows + @($header, $sensors))
        $llama = ConvertFrom-HwInfoText -Lines $named -Name 'llama' @patterns
        $sevenZip = ConvertFrom-HwInfoText -Lines $named -Name '7zip' @patterns
        $exhaustive = ConvertFrom-HwInfoText -Lines $named -Name 'exhaustive' @patterns
        Assert-Equal -Actual (Get-HwInfoSelectionMismatch -References @($llama, $sevenZip) -Run $exhaustive).Count -Expected 0 -What 'every log names the same sensors'

        # A log that HWiNFO did not stop normally ends without the row: its sensors are unknown,
        # which is missing evidence for every metric it supplies, never a match.
        $unnamed = ConvertFrom-HwInfoText -Lines ([string[]](@($header) + $rows)) -Name 'run-no-sensor-row' @patterns
        $entries = Get-HwInfoSelectionMismatch -References @($llama, $sevenZip) -Run $unnamed
        Assert-Equal -Actual $entries.Count -Expected 3 -What 'one entry per metric of the unnamed run log'
        foreach ($metric in $StressMetrics) {
            $expected = ('{0} sensors in run-no-sensor-row cannot be identified (the log has no HWiNFO sensor-name row; stop HWiNFO logging normally so it writes one)' -f $metric.Label)
            Assert-True -Condition ($entries -contains $expected) -What ('entry for ' + $metric.Label + ': ' + ($entries -join ' | '))
        }
        $first = Get-HwInfoSelectionMismatch -References @($unnamed, $sevenZip) -Run $llama
        Assert-Equal -Actual @($first | Where-Object { $_ -like '* sensors in run-no-sensor-row cannot be identified *' }).Count -Expected 3 -What 'an unnamed first reference'
        Assert-Equal -Actual @($first | Where-Object { $_ -like '*from the same sensors in every log*' }).Count -Expected 0 -What 'the unnamed log is entered once'

        # A blank sensor name for a selected column names that column.
        $blankLines = [string[]](@($header) + $rows + @($header, $sensors.Replace('"DDR5 DIMM [#1]"', '""')))
        $blank = ConvertFrom-HwInfoText -Lines $blankLines -Name 'run-blank-dimm' @patterns
        $blankEntries = Get-HwInfoSelectionMismatch -References @($llama, $sevenZip) -Run $blank
        Assert-Equal -Actual $blankEntries.Count -Expected 1 -What 'only the DIMM temperature lacks a sensor name'
        $blankReason = 'DIMM temperature sensors in run-blank-dimm cannot be identified (its HWiNFO sensor-name row leaves SPD Hub Temperature #2 [' + $Sym.Deg + 'C] blank)'
        Assert-Equal -Actual $blankEntries[0] -Expected $blankReason -What 'blank sensor name'
        $verdict = Get-StressBoundVerdict -References @((Get-TestStatistic -Name 'llama' -Bandwidth 40 -PackagePower 100 -DimmTemperature 50),
            (Get-TestStatistic -Name '7zip' -Bandwidth 35 -PackagePower 120 -DimmTemperature 48)) -Run (Get-TestStatistic -Name 'run-no-sensor-row' -Bandwidth 30 -PackagePower 90 -DimmTemperature 45) -Problems $entries
        Assert-Equal -Actual $verdict.Decision -Expected 'unverified' -What 'unknown sensors are not judged'
    }

    $cases['markdown: criterion-1 per-type header and cell escaping'] = {
        $expected = '| Type | Runs | FAIL within 300 s | Median interval [L, U] (s) | Ratio interval vs fastest baseline | Within 5 min (' +
            [char]0x2265 + ' 9 of 10) | At least 5' + [char]0x00D7 + ' faster | No later than Quick |'
        Assert-Equal (Format-MarkdownTableHeader -Header $Headers.Criterion1Types) $expected 'header'
        $table = Format-MarkdownTable -Header @('A', 'B') -Rows @(, [object[]]@('x|y', $null))
        Assert-Equal $table[2] ('| x\|y | ' + [char]0x2014 + ' |') 'escaped row'
        Assert-Equal (Format-Interval 200 ([double]::PositiveInfinity) 1) ('[200.0, ' + [char]0x221E + ']') 'interval with infinity'
    }

    $cases['criterion 7: only user Stop requests are Stop trials'] = {
        $midRuns = @(foreach ($ordinal in 1..3) { Get-TestStopRun -Ordinal $ordinal -Reason 'UserStop' -StopPoint 'mid' -LatencySeconds 0.8 })
        $userHold = Get-TestStopRun -Ordinal 4 -Reason 'UserStop' -StopPoint 'hold' -LatencySeconds 0.8
        $userPreparing = Get-TestStopRun -Ordinal 5 -Reason 'UserStop' -StopPoint 'Preparing' -LatencySeconds 0.8
        $userOnly = Get-StopLatencyResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = ($midRuns + @($userHold, $userPreparing)) })
        Assert-Equal $userOnly.Decision 'met' 'three mid-step, one hold-step and one Preparing user Stop'
        Assert-Equal @($userOnly.Trials).Count 5 'user Stop trials'
        Assert-Equal @($userOnly.OtherStops).Count 0 'no other stops'

        $thermalHold = Get-TestStopRun -Ordinal 4 -Reason 'Thermal' -StopPoint 'hold' -LatencySeconds 0.8
        $hostFaultPreparing = Get-TestStopRun -Ordinal 5 -Reason 'HostFault' -StopPoint 'Preparing' -LatencySeconds 0.8
        $automatic = Get-StopLatencyResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = ($midRuns + @($thermalHold, $hostFaultPreparing)) })
        Assert-Equal $automatic.Decision 'indeterminate' 'Thermal hold-step and HostFault Preparing stops are not trials'
        Assert-Equal @($automatic.Trials).Count 3 'user Stop trials beside automatic stops'
        Assert-Equal @($automatic.OtherStops).Count 2 'automatic stops reported'
        Assert-Equal $automatic.OtherStops[0].Reason 'Thermal' 'first automatic stop'
        Assert-Equal $automatic.OtherStops[1].Reason 'HostFault' 'second automatic stop'
        $lines = New-Object 'System.Collections.Generic.List[string]'
        Write-Criterion7Section -Lines $lines -Result $automatic
        Assert-Equal @($lines | Where-Object { $_ -clike '| [0-9]: *' }).Count 3 'trial rows'
        Assert-True ($lines.Contains('Stops requested by RAMQuick itself are not Stop trials and are reported only:')) 'reported-only line'
        Assert-True ($lines.Contains('- Quick (known-passing #4), Thermal: step 2 (Refresh hold), hold step, stop request 2026-11-14T04:01:10.000Z, Completed 2026-11-14T04:01:10.800Z, latency 0.80 s')) 'Thermal stop bullet'
        Assert-True ($lines.Contains('- Quick (known-passing #5), HostFault: Preparing, stop request 2026-11-14T05:00:02.000Z, Completed 2026-11-14T05:00:02.800Z, latency 0.80 s')) 'HostFault stop bullet'

        $unlogged = Get-StopLatencyResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = @(Get-TestStopRun -Ordinal 6 -Reason '' -StopPoint 'mid' -LatencySeconds 0.8) })
        Assert-Equal @($unlogged.Trials).Count 0 'a stop without a logged reason is not a trial'
        Assert-Equal @($unlogged.OtherStops).Count 1 'a stop without a logged reason is reported'
        $lines = New-Object 'System.Collections.Generic.List[string]'
        Write-Criterion7Section -Lines $lines -Result $unlogged
        Assert-True ($lines.Contains('_no run in the clean-profile folders has a user Stop request_')) 'no user Stop request'
        Assert-True ($lines.Contains('- Quick (known-passing #6), reason not logged: step 1 (Sweep), stop request 2026-11-14T06:00:30.000Z, Completed 2026-11-14T06:00:30.800Z, latency 0.80 s')) 'unlogged reason bullet'

        $slowThermal = Get-TestStopRun -Ordinal 6 -Reason 'Thermal' -StopPoint 'mid' -LatencySeconds 3.0
        $withSlowThermal = Get-StopLatencyResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = ($midRuns + @($userHold, $userPreparing, $slowThermal)) })
        Assert-Equal $withSlowThermal.Decision 'met' 'a 3 s Thermal stop is reported, not gated'
        Assert-Equal @($withSlowThermal.Trials).Count 5 'user Stop trials beside a slow Thermal stop'
        Assert-Equal @($withSlowThermal.OtherStops).Count 1 'slow Thermal stop reported'
        $slowUser = Get-TestStopRun -Ordinal 1 -Reason 'UserStop' -StopPoint 'mid' -LatencySeconds 2.5
        $notMet = Get-StopLatencyResult -Profiles @([pscustomobject]@{ Display = 'Known-passing'; Runs = (@($slowUser) + $midRuns[1..2] + @($userHold, $userPreparing)) })
        Assert-Equal $notMet.Decision 'not met' 'a user Stop completed 2.5 s after the request'
    }

    $cases['json access: ConvertFrom-Json and JavaScriptSerializer shapes'] = {
        $json = '{"schemaVersion":1,"run":{"verdict":"FAIL","timeToVerdictSeconds":95.5,"firstErrorSeconds":null,"recovered":false,"startRequestedUtc":"2026-10-28T14:00:00.250Z"},"steps":[{"index":1,"errors":0},{"index":2,"errors":3}]}'
        $fromCmdlet = $json | ConvertFrom-Json
        $dictionary = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $run = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $run['verdict'] = 'FAIL'
        $run['timeToVerdictSeconds'] = [decimal]95.5
        $run['firstErrorSeconds'] = $null
        $run['recovered'] = $false
        $run['startRequestedUtc'] = '2026-10-28T14:00:00.250Z'
        $dictionary['schemaVersion'] = 1
        $dictionary['run'] = $run
        $stepOne = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $stepOne['index'] = 1
        $stepOne['errors'] = 0
        $stepTwo = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $stepTwo['index'] = 2
        $stepTwo['errors'] = 3
        $dictionary['steps'] = [object[]]@($stepOne, $stepTwo)
        $expectedStart = [DateTimeOffset]::new(2026, 10, 28, 14, 0, 0, 250, [TimeSpan]::Zero)
        foreach ($document in @($fromCmdlet, $dictionary)) {
            $shape = $document.GetType().Name
            Assert-Equal (Get-JsonString $document 'run.verdict') 'FAIL' ('verdict, ' + $shape)
            Assert-Near (Get-JsonNumber $document 'run.timeToVerdictSeconds') 95.5 ('timeToVerdictSeconds, ' + $shape)
            Assert-Near (Get-JsonNumber $document 'run.firstErrorSeconds') $null ('null number, ' + $shape)
            Assert-Equal (Get-JsonBool $document 'run.recovered') $false ('bool, ' + $shape)
            Assert-Near (Get-JsonNumber $document 'run.absent.deeper') $null ('missing segment, ' + $shape)
            Assert-Equal (ConvertFrom-JsonTimestamp (Get-JsonValue $document 'run.startRequestedUtc')).UtcTicks $expectedStart.UtcTicks ('timestamp, ' + $shape)
            $steps = Get-JsonArray $document 'steps'
            Assert-Equal $steps.Count 2 ('steps, ' + $shape)
            Assert-Near (Get-JsonNumber $steps[1] 'errors') 3 ('step errors, ' + $shape)
            Assert-Equal (Get-JsonArray $document 'absent').Count 0 ('missing array, ' + $shape)
        }
        Assert-Equal (ConvertFrom-JsonTimestamp ([DateTime]::SpecifyKind([DateTime]::new(2026, 10, 28, 14, 0, 0), [DateTimeKind]::Utc))).UtcTicks ([DateTimeOffset]::new(2026, 10, 28, 14, 0, 0, [TimeSpan]::Zero)).UtcTicks 'DateTime already converted'
    }

    $cases['summary check: TestHost and recovered Unfinished summaries accepted in both shapes'] = {
        $text = Get-TestSummaryJson
        $base = 'RamQuick-20261030-021529-Quick-13ae3ce49e4046f4a7e9789c7f9355fe'
        # An Unfinished recovery: FAIL, PreviousRunUnfinished, null for what the journal never captured.
        $recovered = $text
        $edits = [ordered]@{
            '"verdict": "PASS"'                       = '"verdict": "FAIL"'
            '"endReason": "Completed"'                = '"endReason": "PreviousRunUnfinished"'
            '"endedUtc": "2026-10-30T02:15:54.641Z"'   = '"endedUtc": null'
            '"verdictUtc": "2026-10-30T02:15:54.647Z"' = '"verdictUtc": null'
            '"durationSeconds": 24.536'               = '"durationSeconds": null'
            '"timeToVerdictSeconds": 24.81916'        = '"timeToVerdictSeconds": null'
            '"recovered": false'                      = '"recovered": true'
            '"partial": false'                        = '"partial": true'
            '"testedBytes": 268435456'                = '"testedBytes": null'
            '"releaseSeconds": 0.035'                 = '"releaseSeconds": null'
            '"targetSeconds": 24,'                    = '"targetSeconds": null,'
            '"percent": 61200, "targetPercent": 60, "targetReached": true' =
            '"percent": null, "targetPercent": null, "targetReached": null'
            '"actualSeconds": 12.507, "overrunSeconds": 0.507, "passes": 269, "coveragePercent": 26900, "errors": 0, "completed": true' =
            '"actualSeconds": null, "overrunSeconds": null, "passes": null, "coveragePercent": null, "errors": null, "completed": false'
        }
        foreach ($find in $edits.Keys) { $recovered = Edit-TestText -Text $recovered -Find $find -Replace $edits[$find] }
        foreach ($document in (ConvertFrom-TestJson $text)) {
            $problems = Get-RunSummaryProblem -Document $document -FileName ($base + '-summary.json')
            Assert-Equal -Actual ($problems -join '; ') -Expected '' -What ('TestHost summary, ' + $document.GetType().Name)
        }
        foreach ($document in (ConvertFrom-TestJson $recovered)) {
            $problems = Get-RunSummaryProblem -Document $document -FileName ($base + '-recovered-summary.json')
            Assert-Equal -Actual ($problems -join '; ') -Expected '' -What ('recovered Unfinished summary, ' + $document.GetType().Name)
        }
    }

    $cases['summary check: each rejected field is named by its JSON path'] = {
        $text = Get-TestSummaryJson
        $name = 'RamQuick-20261030-021529-Quick-13ae3ce49e4046f4a7e9789c7f9355fe-summary.json'
        $mutations = @(
            @{ Find = '"schemaVersion": 1'; Replace = '"schemaVersion": 2'; Problem = 'schemaVersion: unsupported schemaVersion 2;' }
            @{ Find = '"schemaVersion": 1,'; Replace = ''; Problem = 'schemaVersion: unsupported schemaVersion (missing);' }
            @{ Find = '"timeToVerdictSeconds": 24.81916,'; Replace = ''; Problem = 'run.timeToVerdictSeconds: missing' }
            @{ Find = '"durationSeconds": 24.536'; Replace = '"durationSeconds": "10.0"'; Problem = 'run.durationSeconds: expected null or a finite number >= 0, found "10.0"' }
            @{ Find = '"durationSeconds": 24.536'; Replace = '"durationSeconds": -1.5'; Problem = 'run.durationSeconds: expected null or a finite number >= 0, found -1.5' }
            @{ Find = '"verdict": "PASS"'; Replace = '"verdict": "MAYBE"'; Problem = 'run.verdict: expected one of PASS, FAIL, ABORTED' }
            @{ Find = '"id": "13ae3ce49e4046f4a7e9789c7f9355fe"'; Replace = '"id": "13ae3ce4-9e40"'; Problem = 'run.id: expected a run id of 32 hex digits' }
            @{ Find = '"testType": "Quick"'; Replace = '"testType": "Turbo"'; Problem = 'run.testType: expected one of Quick, Standard, Exhaustive' }
            @{ Find = '"testType": "Quick"'; Replace = '"testType": null'; Problem = 'run.testType: expected one of Quick, Standard, Exhaustive, found null' }
            @{ Find = '"id": "13ae3ce49e4046f4a7e9789c7f9355fe"'; Replace = '"id": null'; Problem = 'run.id: expected a run id of 32 hex digits, found null' }
            @{ Find = '"targetReached": true'; Replace = '"targetReached": "yes"'; Problem = 'coverage.targetReached: expected null, true or false' }
            @{ Find = '"targetSeconds": 24,'; Replace = '"targetSeconds": -24,'; Problem = 'plan.targetSeconds: expected null or a finite number >= 0' }
            @{ Find = '{ "index": 1, '; Replace = '{ '; Problem = 'steps[0].index: missing' }
            @{ Find = '"startRequestedUtc": "2026-10-30T02:15:29.828Z"'; Replace = '"startRequestedUtc": "2026-10-30T02:15:29.828"'; Problem = 'run.startRequestedUtc: expected null or an ISO 8601 UTC timestamp' }
            @{ Find = '"startRequestedUtc": "2026-10-30T02:15:29.828Z"'; Replace = '"startRequestedUtc": "2026-10-30T04:15:29.828+02:00"'; Problem = 'run.startRequestedUtc: expected null or an ISO 8601 UTC timestamp' }
            @{ Find = '"startRequestedUtc": "2026-10-30T02:15:29.828Z"'; Replace = '"startRequestedUtc": "\/Date(1793326529828)\/"'; Problem = 'run.startRequestedUtc: expected null or an ISO 8601 UTC timestamp' }
        )
        foreach ($mutation in $mutations) {
            foreach ($document in (ConvertFrom-TestJson (Edit-TestText -Text $text -Find $mutation.Find -Replace $mutation.Replace))) {
                $what = '{0} -> {1}, {2}' -f $mutation.Find, $mutation.Replace, $document.GetType().Name
                $problems = Get-RunSummaryProblem -Document $document -FileName $name
                Assert-Equal -Actual $problems.Count -Expected 1 -What ($what + ': problem count (' + ($problems -join '; ') + ')')
                Assert-True $problems[0].StartsWith($mutation.Problem) ($what + ': ' + $problems[0])
            }
        }
        # A zero offset names UTC as 'Z' does; a converted value is judged by what it kept.
        $timestamps = @(
            @{ Value = '2026-10-30T02:15:29.828+00:00'; Allowed = $true }
            @{ Value = '2026-10-30T02:15:29.828-0000'; Allowed = $true }
            @{ Value = [DateTimeOffset]::new(2026, 10, 30, 2, 15, 29, [TimeSpan]::Zero); Allowed = $true }
            @{ Value = [DateTimeOffset]::new(2026, 10, 30, 4, 15, 29, [TimeSpan]::FromHours(2)); Allowed = $false }
            @{ Value = [DateTime]::new(2026, 10, 30, 2, 15, 29, [DateTimeKind]::Unspecified); Allowed = $false }
            @{ Value = [DateTime]::new(2026, 10, 30, 2, 15, 29, [DateTimeKind]::Local); Allowed = $false }
        )
        foreach ($timestamp in $timestamps) {
            Assert-Equal -Actual (Test-JsonTimestamp $timestamp.Value) -Expected $timestamp.Allowed -What ('timestamp ' + (Format-TestValue $timestamp.Value))
        }

        $valid = $text | ConvertFrom-Json
        $otherId = Get-RunSummaryProblem -Document $valid -FileName 'RamQuick-20261030-021529-Quick-0123456789abcdef0123456789abcdef-summary.json'
        Assert-Equal -Actual ($otherId -join '; ') -What 'run id against the file name' `
            -Expected 'run.id: "13ae3ce49e4046f4a7e9789c7f9355fe" differs from the run id 0123456789abcdef0123456789abcdef in the file name'
        $otherType = Get-RunSummaryProblem -Document $valid -FileName 'RamQuick-20261030-021529-Standard-13ae3ce49e4046f4a7e9789c7f9355fe-recovered-summary.json'
        Assert-Equal -Actual ($otherType -join '; ') -What 'test type against the file name' `
            -Expected 'run.testType: "Quick" differs from the test type Standard in the file name'
        $upperCaseId = Get-RunSummaryProblem -Document $valid -FileName 'RamQuick-20261030-021529-Quick-13AE3CE49E4046F4A7E9789C7F9355FE-summary.json'
        Assert-Equal -Actual $upperCaseId.Count -Expected 0 -What 'run id compared case-insensitively'
        $renamed = Get-RunSummaryProblem -Document $valid -FileName 'renamed-summary.json'
        Assert-Equal -Actual $renamed.Count -Expected 0 -What 'a name outside the pattern skips the identity check'
        Assert-True ((Get-RunSummaryProblem -Document $null -FileName $name)[0].StartsWith('(document): expected a JSON object')) 'an empty document'
    }

    $cases['log records: STEP, PROGRESS, STOP_REQUESTED and COMPLETED field rules'] = {
        $accepted = @(
            '2026-10-30T02:15:50.162Z PROGRESS step=2/2 elapsed_ms=20059 remaining_ms=3937 coverage=50399.2 errors=0'
            '2026-10-30T02:15:40.127Z PROGRESS step=1/2 elapsed_ms=10024 remaining_ms=-1 coverage=4.0 errors=0'
            '2026-10-30T02:15:30.120Z STEP_START step=1/2 name="High-toggle sweep"'
            '2026-10-30T02:15:42.105Z STEP_END step=1 name="High-toggle sweep" completed=true'
            '2026-10-30T02:15:45.000Z STOP_REQUESTED reason=UserStop'
            '2026-10-30T02:15:54.641Z COMPLETED end_reason=Completed total_errors=0 elapsed_ms=24536 first_error_ms=-1 detail=""'
            '2026-10-30T02:15:30.119Z PREPARED tested_bytes=268435456 workers=128 page_mode=Standard isa=AVX-512 address_kind=virtual prepare_ms=146'
            '2026-10-30T02:15:54.612Z STEP_END step=2/2 name="Random bank thrash" completed=true actual_ms=12507 passes=269 verified_bytes=72209137664 coverage=26900.0 errors=0 overrun_ms=507'
        )
        foreach ($line in $accepted) {
            $problems = Get-RunLogRecordProblem -Record (ConvertFrom-RunLogLine -Line $line)
            Assert-Equal -Actual ($problems -join '; ') -Expected '' -What $line
        }
        $rejected = [ordered]@{
            '2026-10-30T02:15:50.162Z PROGRESS step=2/2 elapsed_ms=20059 remaining_ms=abc coverage=50399.2 errors=0' = 'remaining_ms=abc is not'
            '2026-10-30T02:15:50.162Z PROGRESS step=2/2 elapsed_ms=20059 remaining_ms=3937 coverage=50399.2'          = 'errors is missing'
            '2026-10-30T02:15:50.162Z PROGRESS step=3/2 elapsed_ms=20059 remaining_ms=3937 coverage=50399.2 errors=0' = 'step=3/2 is not a step number'
            '2026-10-30T02:15:45.000Z STOP_REQUESTED'                                                               = 'reason is missing'
            '2026-10-30T02:15:54.641Z COMPLETED end_reason=Done total_errors=0'                                     = 'end_reason=Done is not one of'
        }
        foreach ($line in $rejected.Keys) {
            $problems = Get-RunLogRecordProblem -Record (ConvertFrom-RunLogLine -Line $line)
            Assert-Equal -Actual $problems.Count -Expected 1 -What ($line + ': problem count (' + ($problems -join '; ') + ')')
            Assert-True $problems[0].StartsWith($rejected[$line]) ($line + ': ' + $problems[0])
        }

        foreach ($line in $accepted) {
            Assert-Equal -Actual (Get-RunLogLineEvent -Line $line) -Expected $line.Split(' ')[1] -What ('relevance: ' + $line)
        }
        $damaged = @(
            '2026-10-30 02:15:02 STEP_END step=1/2 name="High-toggle sweep" completed=true actual_ms=4988 passes=157 verified_bytes=42144366592 coverage=15700.0 errors=0 overrun_ms=0'
            'STEP_END step=1/2 name="High-toggle sweep" completed=true actual_ms=4988 passes=157 verified_bytes=42144366592 coverage=15700.0 errors=0 overrun_ms=0'
        )
        foreach ($line in $damaged) {
            Assert-Equal -Actual (Get-RunLogLineEvent -Line $line) -Expected 'STEP_END' -What ('relevance of a damaged timestamp: ' + $line)
            Assert-Equal -Actual (ConvertFrom-RunLogLine -Line $line) -Expected $null -What ('a damaged timestamp is rejected: ' + $line)
        }
        $fused = @(
            '2026-10-30T02:15:02.509ZSTEP_END step=1/2 name="x"'
            '2026-10-30T02:15:02.509Z STEP_ENDstep=1/2 name="x"'
        )
        foreach ($line in $fused) {
            Assert-Equal -Actual (Get-RunLogLineEvent -Line $line) -Expected 'STEP_END' -What ('relevance of a fused event: ' + $line)
            Assert-Equal -Actual (ConvertFrom-RunLogLine -Line $line) -Expected $null -What ('a fused event is rejected: ' + $line)
        }
        $corrupt = '2026-10-30T02:15:02.509Z XSTEP_END step=1/2 name="x"'
        Assert-Equal -Actual (Get-RunLogLineEvent -Line $corrupt) -Expected 'STEP_END' -What ('relevance of a corrupt event token: ' + $corrupt)
        Assert-Equal -Actual (ConvertFrom-RunLogLine -Line $corrupt).Event -Expected 'XSTEP_END' `
            -What ('a corrupt event token differs from the event ConvertFrom-RunLogText requires: ' + $corrupt)
        $otherEvents = @('APP_ERROR', 'ERROR', 'ERROR_CAP_REACHED', 'JOURNAL_FAILURE', 'MEMORY', 'NOTICE', 'PLAN', 'PLAN_TARGET',
            'RELEASE_NOT_CONFIRMED', 'RELEASED', 'SENSOR', 'SIZING', 'START_REQUESTED', 'STEP_OVERRUN', 'SUMMARY_FAILED',
            'SUMMARY_PUBLISHED', 'SYSTEM', 'THERMAL_STOP', 'VERDICT')
        foreach ($otherEvent in $otherEvents) {
            $line = '2026-10-30T02:15:54.612Z ' + $otherEvent + ' key=1'
            Assert-Equal -Actual (Get-RunLogLineEvent -Line $line) -Expected $null -What ('another event of the product: ' + $line)
        }
        $notRelevant = @(
            '2026-10-30T02:14:57.368Z SENSOR sensor=CPU status=Stale message="STEP_END in text"'
            '2026-10-30T02:15:01.250Z ERROR seq=1 step=1/2 name="High-toggle sweep" pass=3 time_into_test_ms=3732 address=0x000001F2DEADBEE0 address_kind=virtual expected=0xFFFFFFFFFFFFFFFF actual=0xFFFFFFFFFFFFFFFE reread=0xFFFFFFFFFFFFFFFF bit_mask=0x0000000000000001 bits=0 outcome="reread matches expected" access=Read'
            '2026-10-30T02:15:54.612Z STEP_OVERRUN step=2/2 overrun_ms=507'
            '2026-10-30T02:15:54.612Z step_end step=2/2 name="Random bank thrash"'
            ''
        )
        foreach ($line in $notRelevant) {
            Assert-Equal -Actual (Get-RunLogLineEvent -Line $line) -Expected $null -What ('not relevant: ' + $line)
        }
    }

    $cases['log import: whole logs, torn final line and errors named by line'] = {
        # The .log of the TestHost run Get-TestSummaryJson describes, its data-folder path shortened.
        $intact = [string[]]@(
            '2026-10-30T02:15:29.828Z START_REQUESTED run_id=13ae3ce49e4046f4a7e9789c7f9355fe test_type=Quick stop_on_error=false app_version=1.0.0+27c6fda749c34440364597fced6a4ade730bcd48'
            '2026-10-30T02:15:29.934Z SYSTEM os="Microsoft Windows 10.0.26100" cpu="INTEL(R) XEON(R) PLATINUM 8573C" logical_processors=128 processor_groups=2 best_isa=AVX-512 total_phys_bytes=1099506118656 avail_phys_bytes=1042102509568 large_page_min_bytes=2097152 lock_privilege=false'
            '2026-10-30T02:15:29.934Z MEMORY installed_bytes=34359738368 type=DDR5 speed_mts=6000 channels=2 dimms=2'
            '2026-10-30T02:15:29.934Z SIZING available_bytes=1042102509568 planned_bytes=937645047808'
            '2026-10-30T02:15:29.934Z PLAN step=1/2 name="High-toggle sweep" pattern=HighToggle order=SequentialAscending threads=All hold_ms=0 budget_ms=12000 coverage_budget=25 primary_mechanism=SignalIntegrity'
            '2026-10-30T02:15:29.934Z PLAN step=2/2 name="Random bank thrash" pattern=Random order=BankStride threads=All hold_ms=0 budget_ms=12000 coverage_budget=35 primary_mechanism=RowTimings'
            '2026-10-30T02:15:29.934Z PLAN_TARGET target_seconds=24 target_coverage=60.0'
            ('2026-10-30T02:15:29.945Z SENSOR sensor=CPU status=Stale message="CPU temperature unavailable ' + [char]0x2014 + ' CPU thermal limit inactive"')
            '2026-10-30T02:15:30.119Z PREPARED tested_bytes=268435456 workers=128 page_mode=Standard isa=AVX-512 address_kind=virtual prepare_ms=146'
            '2026-10-30T02:15:30.120Z STEP_START step=1/2 name="High-toggle sweep"'
            '2026-10-30T02:15:40.127Z PROGRESS step=1/2 elapsed_ms=10024 remaining_ms=13980 coverage=28898.4 errors=0'
            '2026-10-30T02:15:42.105Z STEP_END step=1/2 name="High-toggle sweep" completed=true actual_ms=11990 passes=343 verified_bytes=92073361408 coverage=34300.0 errors=0 overrun_ms=0'
            '2026-10-30T02:15:42.106Z STEP_START step=2/2 name="Random bank thrash"'
            '2026-10-30T02:15:50.162Z PROGRESS step=2/2 elapsed_ms=20059 remaining_ms=3937 coverage=50399.2 errors=0'
            '2026-10-30T02:15:54.612Z STEP_END step=2/2 name="Random bank thrash" completed=true actual_ms=12507 passes=269 verified_bytes=72209137664 coverage=26900.0 errors=0 overrun_ms=507'
            '2026-10-30T02:15:54.612Z STEP_OVERRUN step=2/2 overrun_ms=507'
            '2026-10-30T02:15:54.641Z COMPLETED end_reason=Completed total_errors=0 verified_bytes=164282499072 elapsed_ms=24536 first_error_ms=-1 detail=""'
            '2026-10-30T02:15:54.647Z VERDICT verdict=PASS state=Passed reason=Completed errors=0 detail=""'
            '2026-10-30T02:15:54.678Z RELEASED bytes=268435456 release_ms=35'
            '2026-10-30T02:15:54.729Z SUMMARY_PUBLISHED path="C:\\RAMQuick\\data\\logs\\RamQuick-20261030-021529-Quick-13ae3ce49e4046f4a7e9789c7f9355fe-summary.json"'
        )
        $label = 'synthetic.log'
        $log = ConvertFrom-RunLogText -Path $label -Lines $intact -Terminated $true
        Assert-Equal -Actual $log.Exists -Expected $true -What 'exists'
        Assert-True ($null -ne $log.Prepared) 'PREPARED'
        Assert-Equal -Actual $log.StepStarts.Count -Expected 2 -What 'STEP_START records'
        Assert-Equal -Actual $log.StepEnds.Count -Expected 2 -What 'STEP_END records'
        Assert-Equal -Actual $log.Progress.Count -Expected 2 -What 'PROGRESS samples'
        Assert-Near -Actual $log.Progress[1].RemainingMs -Expected 3937 -What 'last remaining_ms'
        Assert-Equal -Actual $log.StopRequested -Expected $null -What 'no STOP_REQUESTED'
        Assert-Equal -Actual $log.Completed.Fields['end_reason'] -Expected 'Completed' -What 'COMPLETED end_reason'

        $torn = '2026-10-30T02:15:54.750Z PROGRESS step=2/2 elap'
        $tornLog = ConvertFrom-RunLogText -Path $label -Lines ($intact + $torn) -Terminated $false
        Assert-Equal -Actual $tornLog.Progress.Count -Expected 2 -What 'an unterminated final line is dropped'

        $equalsInStamp = [string[]]$intact.Clone()
        $equalsInStamp[13] = '2026-10-28T14:03=30.000Z PROGRESS step=2/2 elapsed_ms=210000 remaining_ms=0 coverage=160 errors=0'
        $repeatedKey = [string[]]$intact.Clone()
        $repeatedKey[10] = '2026-10-30T02:15:40.127Z PROGRESS step=1/2 elapsed_ms=10024 remaining_ms=bad remaining_ms=13980 coverage=28898.4 errors=0'
        $malformed = @(
            @{ Lines = $equalsInStamp; Expected = 'synthetic.log:14: PROGRESS: the line does not follow the log line format' }
            @{ Lines = $repeatedKey; Expected = 'synthetic.log:11: PROGRESS: remaining_ms appears more than once' }
            # The same torn text, terminated, is a whole line and is judged.
            @{ Lines = [string[]]($intact + $torn); Expected = 'synthetic.log:21: PROGRESS: the line does not follow the log line format' }
        )
        foreach ($case in $malformed) {
            $message = $null
            try { [void](ConvertFrom-RunLogText -Path $label -Lines $case.Lines -Terminated $true) }
            catch { $message = $_.Exception.Message }
            Assert-True ($null -ne $message -and $message.StartsWith($case.Expected)) ('throws "{0}...", got: {1}' -f $case.Expected, [string]$message)
        }
    }

    $cases['methodology: emitted headings and headers appear in docs/METHODOLOGY.md'] = {
        $documentPath = Join-Path (Join-Path $RepoRoot 'docs') 'METHODOLOGY.md'
        if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) { return 'SKIP docs/METHODOLOGY.md not found' }
        $document = [IO.File]::ReadAllText($documentPath, (New-Object Text.UTF8Encoding $false))
        $expected = Get-DocumentStrings
        $absent = @(foreach ($text in $expected) { if (-not $document.Contains($text)) { $text } })
        if ($absent.Count -gt 0) { throw ('missing from docs/METHODOLOGY.md: ' + ($absent -join ' || ')) }
    }

    $passed = 0
    $failed = 0
    foreach ($name in $cases.Keys) {
        try {
            $outcome = @(& $cases[$name])
            $skip = @($outcome | Where-Object { $_ -is [string] -and $_.StartsWith('SKIP ') })
            if ($skip.Count -gt 0) {
                Write-Host ('SKIP {0}: {1}' -f $name, $skip[0].Substring(5))
                continue
            }
            Write-Host ('PASS {0}' -f $name)
            $passed++
        }
        catch {
            Write-Host ('FAIL {0}: {1}' -f $name, $_.Exception.Message)
            $failed++
        }
    }
    Write-Host ('Self-test: {0} passed, {1} failed.' -f $passed, $failed)
    if ($failed -gt 0) { return 1 }
    return 0
}

# ---------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -ceq 'SelfTest') {
    exit (Invoke-SelfTest)
}

try {
    $report = Invoke-Summarize -Bound $PSBoundParameters
}
catch {
    [Console]::Error.WriteLine(('Summarize-ValidationRuns: {0}' -f $_.Exception.Message))
    exit 1
}
if ($null -eq $report) { exit 1 }
Write-Output $report.Markdown
Write-Host ('Wrote {0}' -f $report.Path)
exit 0
