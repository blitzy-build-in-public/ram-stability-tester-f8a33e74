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
      - A median is reported as an interval [L, U]: L puts every censored run at its censoring time,
        U puts it at +infinity. An even count averages the two middle values at each bound. No
        Kaplan-Meier estimate, confidence interval or significance test is computed.
      - Decisions are met, not met or indeterminate (unverified for the stress bound). Indeterminate
        never counts as met.
      - Stress bound: per metric, the RAMQuick Exhaustive statistic must not exceed the larger of
        the two reference statistics. A metric missing from any log, other than exactly two
        reference logs, or a log whose 1-s samples do not cover the run window makes the bound
        unverified; it is never judged on the remaining metrics. Its Status line reads satisfied,
        exceeded or unverified, as the document's template does.

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
                                                    clean run
        ### Criterion 7: UI behaviour and stop latency
                                                    stop-request to Completed latency per stopped
                                                    run, and memory release time (reported only)
        ### Stress-bound measurement               whole-run statistics against the reference
                                                    workloads, the verdict, and per-step statistics
    A section whose inputs were not supplied reads "_not supplied_".

.PARAMETER SelfTest
    Runs the built-in cases of the censoring, interval and decision rules, the log and HWiNFO
    parsers and the emitted headers, in memory. Prints "PASS <case>" or "FAIL <case>: expected ...
    got ..." per case and exits 0 only when every case passes. When docs/METHODOLOGY.md exists, one
    case also checks that every emitted heading and table header appears in it verbatim.

.PARAMETER FailingProfileRuns
    Folder of RAMQuick runs on the failing profile (criterion 1): ten interleaved runs each of
    Quick, Standard and Exhaustive with Stop on error on. Every *-summary.json in the folder (not
    recursive, *-recovered-summary.json included, *.tmp ignored) is read together with the .log of
    the same base name.

.PARAMETER PassingProfileRuns
    Folder of RAMQuick runs on the known-passing profile (criteria 2, 5, 6 and 7), read like
    -FailingProfileRuns.

.PARAMETER JedecRuns
    Folder of RAMQuick runs on JEDEC defaults (criteria 2, 5, 6 and 7), read like
    -FailingProfileRuns.

.PARAMETER BaselineCsv
    Baseline CSV with the columns tool,run,minutesToFirstError,censored and an optional
    configuration column. censored is 1/0 or true/false. minutesToFirstError is a number with a
    decimal point, or "> t" for a run that ended without an error at t minutes.

.PARAMETER ReferenceLogs
    The two HWiNFO64 CSV logs of the reference workloads, in this order: llama.cpp CPU inference,
    then the 7-Zip benchmark (7z b). Each log's display name is its file name without extension.

.PARAMETER StressRunLog
    The HWiNFO64 CSV log recorded during the full RAMQuick Exhaustive run.

.PARAMETER StressRunSummary
    The -summary.json of that Exhaustive run. Its .log, beside it, supplies the step windows of the
    per-step statistics; the run window restricts the whole-run statistics.

.PARAMETER BandwidthColumns
    Wildcard patterns for the DRAM bandwidth columns of the HWiNFO header. Every pattern must match;
    the first column matching each pattern is converted to GB/s and the contributions are summed per
    row (read + write). Default: '*Memory Read Bandwidth*', '*Memory Write Bandwidth*'.

.PARAMETER PackagePowerColumns
    Wildcard patterns for the CPU package power column. The first column matching the first pattern
    that matches anything is used. Default: '*CPU Package Power*'.

.PARAMETER DimmTemperatureColumns
    Wildcard patterns for the DIMM (SPD) temperature columns. The maximum across every matching
    column is used per row. Default: '*DIMM*Temperature*', '*SPD Hub*Temperature*'.

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
    error (a missing folder or file, an unreadable CSV, no input at all) or an unexpected error.
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

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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

# Criterion 1: FAIL within 300 s in at least 9 of 10 runs, at least 5x faster than the fastest
# baseline median, and Standard/Exhaustive no later than Quick.
$FailWithinSeconds = 300.0
$FastRunFraction = 0.9
$FastRunMinimumRuns = 10
$SpeedRatio = 5.0

# Baseline runs with no error by 120 min are right-censored at 120 min; each tool needs 3 runs.
$BaselineCensorMinutes = 120.0
$BaselineMinimumRuns = 3

# Criterion 2: consecutive PASS runs required per type on each clean profile.
$RequiredConsecutivePasses = @{ Exhaustive = 3; Quick = 10 }

# Criteria 5 and 6: duration within +-15 % of the plan target; time remaining within +-10 % of the
# actual remaining time on every sample whose actual remaining time is at least 10 s.
$DurationTolerance = 0.15
$TimeRemainingTolerance = 0.10
$TimeRemainingGateSeconds = 10.0

# Criterion 7: stop request to Completed within 2 s; five trials, one in Preparing, one in a hold.
$StopLatencyLimitSeconds = 2.0
$StopTrialsRequired = 5

# Stress bound: 10-s rolling means over 1-s samples; HWiNFO polling must be 1 s +- 10 %.
$RollingWindowSamples = 10
$ExpectedSamplingSeconds = 1.0
$SamplingTolerance = 0.10

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
    # follow the format.
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
    $position = 0
    $match = $LogFieldPattern.Match($rest, 0)
    while ($match.Success -and $match.Length -gt 0) {
        $raw = $match.Groups[2].Value
        if ($raw.StartsWith('"')) {
            $raw = [regex]::Replace($raw.Substring(1, $raw.Length - 2), '\\(.)', '$1')
        }
        $fields[$match.Groups[1].Value] = $raw
        $position = $match.Index + $match.Length
        $match = $match.NextMatch()
    }
    if ($rest.Substring($position).Trim().Length -gt 0) { return $null }

    $stepNumber = $null
    $stepCount = $null
    if ($fields.ContainsKey('step')) {
        $stepMatch = [regex]::Match($fields['step'], '^(\d+)(?:/(\d+))?$')
        if ($stepMatch.Success) {
            $stepNumber = [int]$stepMatch.Groups[1].Value
            if ($stepMatch.Groups[2].Success) { $stepCount = [int]$stepMatch.Groups[2].Value }
        }
    }

    return [pscustomobject]@{
        Utc        = $utc.ToUniversalTime()
        Event      = $eventName
        Fields     = $fields
        StepNumber = $stepNumber
        StepCount  = $stepCount
    }
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

function Import-TextLine {
    # Lines of a UTF-8 text file, readable even while another process still holds it open for writing.
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding $false), $true)
        try {
            $lines = New-Object 'System.Collections.Generic.List[string]'
            while ($null -ne ($line = $reader.ReadLine())) { $lines.Add($line) }
            return , $lines.ToArray()
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Import-RunLog {
    # The events of one .log this script uses: PREPARED, STEP_START/STEP_END (step windows),
    # PROGRESS (time-remaining samples), and the first STOP_REQUESTED and COMPLETED. Exists is
    # $false when the file is missing.
    param([Parameter(Mandatory = $true)][string]$Path)

    $log = [pscustomobject]@{
        Path          = $Path
        Exists        = $false
        Prepared      = $null
        StepStarts    = (New-Object 'System.Collections.Generic.List[object]')
        StepEnds      = (New-Object 'System.Collections.Generic.List[object]')
        Progress      = (New-Object 'System.Collections.Generic.List[object]')
        StopRequested = $null
        Completed     = $null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $log }
    $log.Exists = $true

    foreach ($line in (Import-TextLine -Path $Path)) {
        $firstSpace = $line.IndexOf(' ')
        if ($firstSpace -lt 0) { continue }
        $secondSpace = $line.IndexOf(' ', $firstSpace + 1)
        if ($secondSpace -lt 0) { $eventName = $line.Substring($firstSpace + 1) }
        else { $eventName = $line.Substring($firstSpace + 1, $secondSpace - $firstSpace - 1) }
        if (-not $LogEventsUsed.ContainsKey($eventName)) { continue }

        $record = ConvertFrom-RunLogLine -Line $line
        if ($null -eq $record) { continue }
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

function Import-JsonDocument {
    # The parsed document, or $null (with a warning) when it cannot be read. Windows PowerShell's
    # ConvertFrom-Json rejects documents above its length limit, which a summary holding 10,000
    # error records can exceed; JavaScriptSerializer without that limit parses those instead.
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $text = [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding $false))
    }
    catch {
        Write-Warning ('Skipping {0}: {1}' -f $Path, $_.Exception.Message)
        return $null
    }

    try {
        return ($text | ConvertFrom-Json)
    }
    catch {
        $message = $_.Exception.Message
        if ($message -notmatch 'maxJsonLength|length of the string') {
            Write-Warning ('Skipping {0}: not valid JSON ({1})' -f $Path, $message)
            return $null
        }
    }

    try {
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = [int]::MaxValue
        $serializer.RecursionLimit = 256
        return , $serializer.DeserializeObject($text)
    }
    catch {
        Write-Warning ('Skipping {0}: not valid JSON ({1})' -f $Path, $_.Exception.Message)
        return $null
    }
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
    # The fields of one schema-1 summary this script uses, with its .log events. Error records are
    # never read: the verdict, the run.* timing fields and steps[].errors carry everything needed.
    param(
        [Parameter(Mandatory = $true)][object]$Document,
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [Parameter(Mandatory = $true)][string]$ProfileName
    )

    $steps = New-Object 'System.Collections.Generic.List[object]'
    foreach ($step in (Get-JsonArray $Document 'steps')) {
        $index = Get-JsonNumber $step 'index'
        if ($null -eq $index) { continue }
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
    # Every schema-1 summary in a folder (not recursive), ordered by run.startRequestedUtc, with
    # Ordinal numbering the runs of each test type from 1. A folder that does not exist is an input
    # error; a file that does not parse, or has another schema version, is skipped with a warning.
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

    $byId = @{}
    $runs = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in $files) {
        $document = Import-JsonDocument -Path $file.FullName
        if ($null -eq $document) { continue }
        $schemaVersion = Get-JsonNumber $document 'schemaVersion'
        if ($schemaVersion -ne 1) {
            Write-Warning ('Skipping {0}: schemaVersion is {1}, not 1.' -f $file.FullName, (Format-Number $schemaVersion 0))
            continue
        }
        $run = ConvertFrom-RunSummaryDocument -Document $document -SummaryPath $file.FullName -ProfileName $ProfileName
        if (-not $run.Log.Exists) {
            Write-Warning ('{0}: its event log {1} was not found; the checks that need it are indeterminate.' -f $file.Name, $run.Log.Path)
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
        if ($TestTypes -notcontains $type) {
            Write-Warning ('{0}: unknown test type "{1}"; the run is not counted.' -f $run.FileName, $type)
        }
    }
    if ($ordered.Count -eq 0) {
        Write-Warning ('No schema-1 summaries were found in {0}.' -f $folder)
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
    # recorded in Missing.
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
    # temperature is the maximum over every column that matches any pattern.
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
    for ($i = $headerIndex + 1; $i -lt $lineCount; $i++) {
        $line = $Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $cells = ConvertFrom-CsvLine -Line $line -Delimiter $delimiter
        if ($cells.Count -lt 2) { $result.SkippedRows++; continue }
        $local = [DateTime]::MinValue
        $stampText = $cells[0].Trim() + ' ' + $cells[1].Trim()
        if (-not [DateTime]::TryParseExact($stampText, $HwInfoTimestampFormats, $Invariant, [Globalization.DateTimeStyles]::None, [ref]$local)) {
            $result.SkippedRows++
            continue
        }
        $utc = ConvertFrom-HwInfoLocalTime -Local $local -UtcOffsetMinutes $UtcOffsetMinutes
        if ($null -eq $utc) { $result.SkippedRows++; continue }

        $values = @{}
        foreach ($metric in $StressMetrics) {
            $key = $metric.Key
            $parts = New-Object 'System.Collections.Generic.List[double]'
            foreach ($c in $selection[$key]) {
                if ($c -ge $cells.Count) { continue }
                $cellText = $cells[$c].Trim()
                if ($decimalComma) { $cellText = $cellText.Replace(',', '.') }
                $number = ConvertFrom-InvariantNumber $cellText
                if ($null -eq $number) { continue }
                $conversion = $conversions[('{0}:{1}' -f $key, $c)]
                $parts.Add($number * $conversion.Scale + $conversion.Offset)
            }
            $value = $null
            if ($key -eq 'Bandwidth') {
                if ($selection[$key].Count -gt 0 -and $parts.Count -eq $selection[$key].Count -and -not $result.Missing.ContainsKey($key)) {
                    $value = 0.0
                    foreach ($part in $parts) { $value += $part }
                }
            }
            elseif ($parts.Count -gt 0) {
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

    foreach ($metric in $StressMetrics) {
        if (-not $result.Missing.ContainsKey($metric.Key) -and $numericCount[$metric.Key] -eq 0) {
            $result.Missing[$metric.Key] = ('no numeric value in column(s) "{0}"' -f ($result.Columns[$metric.Key] -join '", "'))
        }
    }

    if ($result.Samples.Count -ge 2) {
        $intervals = New-Object 'System.Collections.Generic.List[double]'
        for ($i = 1; $i -lt $result.Samples.Count; $i++) {
            $intervals.Add(($result.Samples[$i].Utc - $result.Samples[$i - 1].Utc).TotalSeconds)
        }
        $result.MedianIntervalSeconds = Get-Median -Values $intervals.ToArray()
    }
    return $result
}

function Import-HwInfoLog {
    # Reads an HWiNFO64 CSV log (ANSI, as HWiNFO writes it) and warns when its polling interval is
    # not the 1 s that both sides of the stress-bound comparison must share.
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
    elseif ($null -eq $log.MedianIntervalSeconds -or
        [Math]::Abs($log.MedianIntervalSeconds - $ExpectedSamplingSeconds) -gt $SamplingTolerance * $ExpectedSamplingSeconds) {
        Write-Warning ('{0}: the median sampling interval is {1} s; both sides of the comparison must be polled at 1 s.' -f $fullPath, (Format-Number $log.MedianIntervalSeconds 2))
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
    # (or any other verdict) breaks a streak. FailRuns and AbortedRuns are 1-based positions.
    param([AllowNull()][AllowEmptyCollection()][string[]]$Verdicts, [Parameter(Mandatory = $true)][int]$Required)

    $longest = 0
    $current = 0
    $position = 0
    $failRuns = New-Object 'System.Collections.Generic.List[int]'
    $abortedRuns = New-Object 'System.Collections.Generic.List[int]'
    foreach ($verdict in @($Verdicts)) {
        $position++
        $text = ([string]$verdict).ToUpperInvariant()
        if ($text -ceq 'PASS') {
            $current++
            if ($current -gt $longest) { $longest = $current }
            continue
        }
        $current = 0
        if ($text -ceq 'FAIL') { $failRuns.Add($position) } else { $abortedRuns.Add($position) }
    }
    if ($failRuns.Count -gt 0) { $result = $Decision.NotMet }
    elseif ($longest -ge $Required) { $result = $Decision.Met }
    else { $result = $Decision.Indeterminate }
    return [pscustomobject]@{
        Decision      = $result
        LongestStreak = $longest
        Required      = $Required
        N             = $position
        FailRuns      = $failRuns.ToArray()
        AbortedRuns   = $abortedRuns.ToArray()
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
    # has ended (1-based step >= 2) with an estimate (remaining_ms >= 0; -1 means calculating) are
    # used. Actual remaining = EndUtc - sample time. Every sample with actual remaining >= 10 s is
    # gated at +-10 % of the actual time. Samples under 10 s are counted and their worst error
    # reported, but not gated: the mm:ss display and pass jitter make +-10 % unattainable there
    # (an open deviation the user may reject).
    param([AllowNull()][AllowEmptyCollection()][object[]]$Samples, [AllowNull()][object]$EndUtc)

    $gated = 0
    $gatedWithin = 0
    $worstGated = $null
    $ungated = 0
    $worstUngated = $null
    if ($null -ne $EndUtc) {
        foreach ($sample in @($Samples)) {
            if ($null -eq $sample -or $null -eq $sample.StepNumber -or $null -eq $sample.RemainingMs) { continue }
            if ([int]$sample.StepNumber -lt 2 -or [double]$sample.RemainingMs -lt 0) { continue }
            $actual = ([DateTimeOffset]$EndUtc - [DateTimeOffset]$sample.Utc).TotalSeconds
            $displayed = [double]$sample.RemainingMs / 1000.0
            if ($actual -ge $TimeRemainingGateSeconds) {
                $gated++
                $absoluteError = [Math]::Abs($displayed - $actual)
                if ($absoluteError -le $TimeRemainingTolerance * $actual) { $gatedWithin++ }
                $percent = 100.0 * $absoluteError / $actual
                if ($null -eq $worstGated -or $percent -gt $worstGated) { $worstGated = $percent }
            }
            else {
                $ungated++
                if ($actual -gt 0) {
                    $percent = 100.0 * [Math]::Abs($displayed - $actual) / $actual
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
    }
}

function Measure-RollingMeanPeak {
    # The peak of the mean over every window of 10 consecutive 1-s samples; $null with fewer
    # samples than one window.
    param([AllowNull()][AllowEmptyCollection()][double[]]$Values, [int]$Window = $RollingWindowSamples)

    if ($null -eq $Values -or $Window -le 0 -or $Values.Count -lt $Window) { return $null }
    $peak = [double]::NegativeInfinity
    for ($start = 0; $start -le $Values.Count - $Window; $start++) {
        $sum = 0.0
        for ($i = $start; $i -lt $start + $Window; $i++) { $sum += $Values[$i] }
        $mean = $sum / $Window
        if ($mean -gt $peak) { $peak = $mean }
    }
    return $peak
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
    # given: the peak 10-s rolling mean for bandwidth and package power, the peak sample for DIMM
    # temperature. A statistic that cannot be computed is $null, with its reason in Reasons.
    param(
        [Parameter(Mandatory = $true)][object]$Log,
        [AllowNull()][object]$FromUtc,
        [AllowNull()][object]$ToUtc
    )

    $values = @{}
    $reasons = @{}
    $windowed = ($null -ne $FromUtc -or $null -ne $ToUtc)

    # A statistic over a window is a statistic over that whole window only when the 1-s samples
    # cover it; a log that covers part of it (a wrong UTC offset, logging started late) is
    # missing evidence, never a smaller sample to judge on.
    $coverageGap = $null
    if ($null -ne $FromUtc -and $null -ne $ToUtc) {
        $windowSeconds = ([DateTimeOffset]$ToUtc - [DateTimeOffset]$FromUtc).TotalSeconds
        $inside = @($Log.Samples | Where-Object { $_.Utc -ge [DateTimeOffset]$FromUtc -and $_.Utc -le [DateTimeOffset]$ToUtc }).Count
        $needed = [Math]::Floor((1.0 - $SamplingTolerance) * $windowSeconds / $ExpectedSamplingSeconds)
        if ($windowSeconds -gt 0 -and $inside -lt $needed) {
            $coverageGap = ('the log has {0} samples inside a {1}-s window, fewer than the {2} that 1-s polling gives; check -HwInfoUtcOffsetMinutes' -f $inside, (Format-Number $windowSeconds 0), $needed)
        }
    }

    foreach ($metric in $StressMetrics) {
        $key = $metric.Key
        $values[$key] = $null
        if ($Log.Missing.ContainsKey($key)) {
            $reasons[$key] = $Log.Missing[$key]
            continue
        }
        if ($null -ne $coverageGap) {
            $reasons[$key] = $coverageGap
            continue
        }
        $series = [double[]]@(foreach ($sample in $Log.Samples) {
                if ($null -ne $FromUtc -and $sample.Utc -lt [DateTimeOffset]$FromUtc) { continue }
                if ($null -ne $ToUtc -and $sample.Utc -gt [DateTimeOffset]$ToUtc) { continue }
                $value = $sample.$key
                if ($null -ne $value) { [double]$value }
            })
        if ($series.Count -eq 0) {
            if ($windowed) { $reasons[$key] = 'no sample inside the run window; check -HwInfoUtcOffsetMinutes' }
            else { $reasons[$key] = 'no numeric sample' }
            continue
        }
        if ($metric.Rolling) {
            $values[$key] = Measure-RollingMeanPeak -Values $series
            if ($null -eq $values[$key]) {
                $reasons[$key] = ('fewer than {0} samples' -f $RollingWindowSamples)
            }
        }
        else {
            $values[$key] = Measure-Peak -Values $series
        }
    }
    return [pscustomobject]@{ Name = $Log.Name; Values = $values; Reasons = $reasons }
}

function Get-StressBoundVerdict {
    # The workload-referenced stress bound. Per metric, met when the RAMQuick statistic is at most
    # the larger of the two reference statistics. Overall met only when all three metrics are
    # present in all three logs and each is met. When any metric is missing from any log, or there
    # are not exactly two reference logs, the bound is unverified, naming what is missing: it is
    # never judged on the remaining metrics. Otherwise not met, naming the exceeding metrics.
    # References and Run carry Name, Values and Reasons (from Measure-HwInfoStatistic).
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$References,
        [AllowNull()][object]$Run
    )

    $refs = @(@($References) | Where-Object { $null -ne $_ })
    $missing = New-Object 'System.Collections.Generic.List[string]'
    $exceeding = New-Object 'System.Collections.Generic.List[string]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    if ($refs.Count -ne 2) { $missing.Add(('exactly two reference logs ({0} supplied)' -f $refs.Count)) }
    if ($null -eq $Run) { $missing.Add('RAMQuick Exhaustive log (not supplied)') }

    foreach ($metric in $StressMetrics) {
        $key = $metric.Key
        $refValues = New-Object 'System.Collections.Generic.List[object]'
        $complete = ($refs.Count -eq 2)
        foreach ($ref in $refs) {
            $value = $ref.Values[$key]
            $refValues.Add($value)
            if ($null -eq $value) {
                $complete = $false
                $missing.Add(('{0} in {1} ({2})' -f $metric.Label, $ref.Name, $ref.Reasons[$key]))
            }
        }
        $runValue = $null
        if ($null -ne $Run) {
            $runValue = $Run.Values[$key]
            if ($null -eq $runValue) {
                $missing.Add(('{0} in {1} ({2})' -f $metric.Label, $Run.Name, $Run.Reasons[$key]))
            }
        }
        $bound = $null
        if ($complete) {
            $bound = [double]$refValues[0]
            if ([double]$refValues[1] -gt $bound) { $bound = [double]$refValues[1] }
        }
        if ($null -ne $bound -and $null -ne $runValue) {
            if ([double]$runValue -le $bound) { $metricDecision = $Decision.Met }
            else {
                $metricDecision = $Decision.NotMet
                $exceeding.Add($metric.Label)
            }
        }
        else { $metricDecision = $Decision.Unverified }
        $rows.Add([pscustomobject]@{
                Key             = $key
                Label           = $metric.Label
                ReferenceValues = $refValues.ToArray()
                Bound           = $bound
                RunValue        = $runValue
                Decision        = $metricDecision
            })
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
    # Criterion 1 from the failing-profile runs and the fastest baseline median.
    param(
        [AllowEmptyCollection()][object[]]$Runs,
        [AllowNull()][object]$Fastest
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

    $types = New-Object 'System.Collections.Generic.List[object]'
    $quick = $null
    foreach ($type in $TestTypes) {
        $observations = @(foreach ($row in $runRows) { if ([string]$row.Run.TestType -ceq $type) { $row.Observation } })
        $medianInput = @($observations | Where-Object { $_.Kind -cne 'excluded' })
        $interval = Get-MedianInterval -Values $medianInput
        $within = Get-WithinFiveMinutesDecision -Observations $observations
        $ratio = Get-RatioDecision -BaselineLowSeconds $baselineLow -BaselineHighSeconds $baselineHigh -TypeLow $interval.L -TypeHigh $interval.U
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
    }
}

function Get-Criterion2Result {
    # Criterion 2 per clean profile: consecutive PASS runs of Exhaustive (3) and Quick (10).
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
            $verdicts = [string[]]@(foreach ($run in $typed) { [string]$run.Verdict })
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
    # Criteria 5 and 6 for every clean run that completed with PASS: duration against the plan
    # target, coverage target, and the time-remaining gate over its PROGRESS samples.
    param([Parameter(Mandatory = $true)][object[]]$Profiles)

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $skipped = 0
    foreach ($cleanProfile in $Profiles) {
        foreach ($run in @($cleanProfile.Runs)) {
            if ($null -eq $run -or $TestTypes -notcontains [string]$run.TestType) { continue }
            if ([string]$run.EndReason -cne 'Completed' -or ([string]$run.Verdict).ToUpperInvariant() -cne 'PASS') {
                $skipped++
                continue
            }
            $endUtc = $run.EndedUtc
            if ($null -ne $run.Log.Completed) { $endUtc = $run.Log.Completed.Utc }
            $rows.Add([pscustomobject]@{
                    Run           = $run
                    Duration      = (Test-DurationWithinTolerance -DurationSeconds $run.DurationSeconds -TargetSeconds $run.TargetSeconds)
                    TimeRemaining = (Measure-TimeRemaining -Samples $run.Log.Progress.ToArray() -EndUtc $endUtc)
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
            if ($row.Run.CoverageTargetReached -eq $true) { $coverageChecks.Add($Decision.Met) }
            elseif ($row.Run.CoverageTargetReached -eq $false) { $coverageChecks.Add($Decision.NotMet) }
            else { $coverageChecks.Add($Decision.Indeterminate) }
            if ($row.Duration.Within -eq $true) { $durationChecks.Add($Decision.Met) }
            elseif ($row.Duration.Within -eq $false) { $durationChecks.Add($Decision.NotMet) }
            else { $durationChecks.Add($Decision.Indeterminate) }
            $durationChecks.Add($row.TimeRemaining.Decision)
        }
    }
    return [pscustomobject]@{
        Rows             = $rows.ToArray()
        Skipped          = $skipped
        CoverageDecision = (Get-OverallDecision -Decisions $coverageChecks.ToArray())
        DurationDecision = (Get-OverallDecision -Decisions $durationChecks.ToArray())
    }
}

function Get-StopLatencyResult {
    # Criterion 7: stop request to Completed for every clean run whose .log has a STOP_REQUESTED
    # event, and the release time of every clean run (reported, not gated).
    param([Parameter(Mandatory = $true)][object[]]$Profiles)

    $trials = New-Object 'System.Collections.Generic.List[object]'
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
            $trials.Add([pscustomobject]@{
                    Run          = $run
                    Reason       = $stop.Fields['reason']
                    StopPoint    = (Get-StopPoint -Run $run -StopUtc $stop.Utc)
                    StopUtc      = $stop.Utc
                    CompletedUtc = $completedUtc
                    Latency      = $latency
                    Within       = $within
                })
        }
    }

    # Five trials are required: mid-step, one during a hold and one during Preparing.
    $items = $trials.ToArray()
    if (@($items | Where-Object { $_.Within -eq $false }).Count -gt 0) { $result = $Decision.NotMet }
    elseif ($items.Count -lt $StopTrialsRequired -or
        @($items | Where-Object { $null -eq $_.Within }).Count -gt 0 -or
        @($items | Where-Object { $_.StopPoint -ceq 'Preparing' }).Count -eq 0 -or
        @($items | Where-Object { $_.StopPoint -like '*, hold step' }).Count -eq 0) { $result = $Decision.Indeterminate }
    else { $result = $Decision.Met }
    return [pscustomobject]@{ Trials = $items; Releases = $releases.ToArray(); Decision = $result }
}

function Get-StressResult {
    # The stress bound from the reference logs and the Exhaustive run's log, with per-step
    # statistics over the run's step windows when its summary is supplied.
    param(
        [AllowEmptyCollection()][object[]]$HwInfoReferences,
        [AllowNull()][object]$StressLog,
        [AllowNull()][object]$StressRun
    )

    $references = @(foreach ($log in @($HwInfoReferences)) { if ($null -ne $log) { Measure-HwInfoStatistic -Log $log -FromUtc $null -ToUtc $null } })
    $runStatistic = $null
    $windowText = 'the whole log'
    if ($null -ne $StressLog) {
        $from = $null
        $to = $null
        if ($null -ne $StressRun) {
            $from = $StressRun.StartedUtc
            if ($null -eq $from) { $from = $StressRun.StartRequestedUtc }
            $to = $StressRun.EndedUtc
            if ($null -ne $StressRun.Log.Completed) { $to = $StressRun.Log.Completed.Utc }
            $windowText = ('{0} to {1}' -f (Format-Timestamp $from), (Format-Timestamp $to))
        }
        $runStatistic = Measure-HwInfoStatistic -Log $StressLog -FromUtc $from -ToUtc $to
    }
    $verdict = Get-StressBoundVerdict -References $references -Run $runStatistic

    $steps = New-Object 'System.Collections.Generic.List[object]'
    if ($null -ne $StressLog -and $null -ne $StressRun) {
        $bounds = @{}
        foreach ($row in $verdict.Rows) { $bounds[$row.Key] = $row.Bound }
        foreach ($window in (Get-StepWindow -Run $StressRun)) {
            $statistic = Measure-HwInfoStatistic -Log $StressLog -FromUtc $window.FromUtc -ToUtc $window.ToUtc
            $exceeded = New-Object 'System.Collections.Generic.List[string]'
            $judged = 0
            foreach ($metric in $StressMetrics) {
                $value = $statistic.Values[$metric.Key]
                $bound = $bounds[$metric.Key]
                if ($null -eq $value -or $null -eq $bound) { continue }
                $judged++
                if ([double]$value -gt [double]$bound) { $exceeded.Add($metric.Label) }
            }
            if ($exceeded.Count -gt 0) { $within = 'no (' + ($exceeded -join ', ') + ')' }
            elseif ($judged -eq $StressMetrics.Count) { $within = 'yes' }
            else { $within = $Decision.Unverified }
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
        [AllowNull()][object]$Fastest
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
        foreach ($tool in $Fastest.Tools) {
            if ($tool.Runs -lt $BaselineMinimumRuns) {
                $Lines.Add('')
                $Lines.Add(('Note: {0} has {1} run(s); at least {2} runs per tool are required.' -f $tool.Tool, $tool.Runs, $BaselineMinimumRuns))
            }
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
                (Format-Number $run.TargetSeconds 0),
                (Format-Number $run.DurationSeconds 1),
                (Format-SignedNumber $row.Duration.DeviationPercent 1),
                (Format-YesNo $row.Duration.Within),
                (Format-Number $run.CoveragePercent 1),
                (Format-Number $run.CoverageTargetPercent 1),
                (Format-YesNo $run.CoverageTargetReached)))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no clean run completed with PASS_') }
    else { Write-TableLines -Lines $Lines -Header $Headers.Criteria56 -Rows $rows.ToArray() }
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
    if ($rows.Count -eq 0) { $Lines.Add('_no clean run completed with PASS_') }
    else {
        Write-TableLines -Lines $Lines -Header $Headers.TimeRemaining -Rows $rows.ToArray()
        $Lines.Add('')
        $Lines.Add('Only PROGRESS samples after step 1 that carry an estimate are used. Samples whose actual remaining time is under 10 s are reported only, not gated: an open deviation (see Open deviations).')
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
        $reason = $item.Reason
        if ([string]::IsNullOrEmpty($reason)) { $reason = 'reason not logged' }
        # Two decimals, unlike other seconds: a 1-decimal "2.0" could hide a latency just above 2 s.
        $rows.Add([object[]]@(
                ('{0}: {1}, {2}' -f $trial, (Format-RunLabel $item.Run), $reason),
                $item.StopPoint,
                (Format-Timestamp $item.StopUtc),
                (Format-Timestamp $item.CompletedUtc),
                (Format-Number $item.Latency 2),
                (Format-YesNo $item.Within)))
    }
    if ($rows.Count -eq 0) { $Lines.Add('_no run in the clean-profile folders has a stop request_') }
    else { Write-TableLines -Lines $Lines -Header $Headers.StopLatency -Rows $rows.ToArray() }
    $Lines.Add('')
    $Lines.Add(('Stop-latency check: {0} (five trials are required, one during Preparing and one during a hold step, each completed within 2 s of the stop request).' -f $Result.Decision))
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

function Write-StressSection {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowNull()][object]$Result,
        [bool]$SummarySupplied
    )

    $Lines.Add($Headings.StressBound)
    $Lines.Add('')
    if ($null -eq $Result) {
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        $Lines.Add($Headings.StressPerStep)
        $Lines.Add('')
        $Lines.Add($NotSupplied)
        $Lines.Add('')
        return
    }

    $verdict = $Result.Verdict
    switch -CaseSensitive ($verdict.Decision) {
        'met' { $status = 'satisfied' }
        'not met' { $status = 'exceeded' }
        default { $status = $Decision.Unverified }
    }
    $Lines.Add(('{0} {1} ({2})' -f $StressStatusPrefix, $status, $verdict.Reason))
    $Lines.Add('')
    $names = @(foreach ($reference in $Result.References) { $reference.Name })
    $first = $NotSupplied
    $second = $NotSupplied
    if ($names.Count -ge 1) { $first = $names[0] }
    if ($names.Count -ge 2) { $second = $names[1] }
    $runName = $NotSupplied
    if ($null -ne $Result.Run) { $runName = ('{0} (samples from {1})' -f $Result.Run.Name, $Result.WindowText) }
    $Lines.Add(('Logs: llama.cpp inference = {0}; 7-Zip benchmark = {1}; RAMQuick Exhaustive = {2}. HWiNFO64 at 1 s polling on every log.' -f $first, $second, $runName))
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
    $Lines.Add('The bound is unverified whenever a metric is missing from any log; it is never judged on the remaining metrics.')
    $Lines.Add('')

    $Lines.Add($Headings.StressPerStep)
    $Lines.Add('')
    if (@($Result.Steps).Count -eq 0) {
        if ($SummarySupplied -and $null -ne $Result.Run) { $Lines.Add('_no step window found in the Exhaustive run_') }
        else { $Lines.Add($NotSupplied) }
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
    else { $status1 = $Criterion1.Overall; $evidence1 = 'Fail-fast checks (summary JSONs and baseline CSV)' }

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
    $fastest = $null
    if ($given -contains 'BaselineCsv') {
        $baseline = Import-BaselineCsv -Path $BaselineCsv
        $fastest = Get-FastestBaseline -Runs $baseline
        foreach ($tool in $fastest.Tools) {
            if ($tool.Runs -lt $BaselineMinimumRuns) {
                Write-Warning ('Baseline tool {0} has {1} run(s); at least {2} are required.' -f $tool.Tool, $tool.Runs, $BaselineMinimumRuns)
            }
        }
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
        $document = Import-JsonDocument -Path $summaryPath
        if ($null -eq $document -or (Get-JsonNumber $document 'schemaVersion') -ne 1) {
            throw ('The stress-run summary {0} is not a readable schema-1 summary.' -f $summaryPath)
        }
        $stressRun = ConvertFrom-RunSummaryDocument -Document $document -SummaryPath $summaryPath -ProfileName 'stress run'
        if ([string]$stressRun.TestType -cne 'Exhaustive') {
            Write-Warning ('The stress-run summary {0} is a {1} run; the stress bound is defined over a full Exhaustive run.' -f $summaryPath, $stressRun.TestType)
        }
        if (-not $stressRun.Log.Exists) {
            Write-Warning ('The stress run''s event log {0} was not found; step windows fall back to steps[].actualSeconds.' -f $stressRun.Log.Path)
        }
    }

    $criterion1 = $null
    if ($null -ne $failing) { $criterion1 = Get-Criterion1Result -Runs $failing -Fastest $fastest }
    $criterion2 = $null
    $cleanRuns = $null
    $stopLatency = $null
    if ($cleanSupplied) {
        $profileArray = $profiles.ToArray()
        $criterion2 = Get-Criterion2Result -Profiles $profileArray
        $cleanRuns = Get-CleanRunResult -Profiles $profileArray
        $stopLatency = Get-StopLatencyResult -Profiles $profileArray
    }
    $stress = $null
    if ($referenceLogList.Count -gt 0 -or $null -ne $stressLog) {
        $stress = Get-StressResult -HwInfoReferences $referenceLogList.ToArray() -StressLog $stressLog -StressRun $stressRun
    }

    $md = New-Object 'System.Collections.Generic.List[string]'
    $md.Add('<!-- Generated by scripts/Summarize-ValidationRuns.ps1 on ' + [DateTimeOffset]::UtcNow.ToString($OutputTimestampFormat, $Invariant) + '. Paste each section over its template in docs/METHODOLOGY.md. -->')
    $md.Add('')
    Write-BaselineSection -Lines $md -Baseline $baseline -Fastest $fastest
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

function Get-TestRun {
    # A synthetic failing-profile run, as far as the fail-time rules read it.
    param([string]$Verdict, [AllowNull()][object]$Seconds)

    return [pscustomobject]@{ Verdict = $Verdict; TimeToVerdictSeconds = $Seconds }
}

function Get-TestStatistic {
    # A synthetic per-log stress statistic; $null marks a missing metric.
    param([string]$Name, [AllowNull()][object]$Bandwidth, [AllowNull()][object]$PackagePower, [AllowNull()][object]$DimmTemperature)

    $values = @{ Bandwidth = $Bandwidth; PackagePower = $PackagePower; DimmTemperature = $DimmTemperature }
    $reasons = @{}
    foreach ($key in @($values.Keys)) { if ($null -eq $values[$key]) { $reasons[$key] = 'no column' } }
    return [pscustomobject]@{ Name = $Name; Values = $values; Reasons = $reasons }
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
        $ignored = Measure-TimeRemaining -Samples @($calculating, [pscustomobject]@{ Utc = $end.AddSeconds(-50); StepNumber = 1; RemainingMs = 1.0 }) -EndUtc $end
        Assert-Equal $ignored.GatedCount 0 'remaining_ms=-1 and step 1 samples are ignored'
        Assert-Equal $ignored.UngatedCount 0 'no ungated sample either'

        $quoted = ConvertFrom-RunLogLine -Line '2026-10-28T14:05:51.120Z STEP_START step=3/6 name="Read-after-write \"turnaround\" \\ x"'
        Assert-Equal $quoted.Fields['name'] 'Read-after-write "turnaround" \ x' 'quoted name'
        $stop = ConvertFrom-RunLogLine -Line '2026-10-28T14:06:00.000Z STOP_REQUESTED reason=UserStop'
        Assert-Equal $stop.Fields['reason'] 'UserStop' 'stop reason'
        Assert-Equal (ConvertFrom-RunLogLine -Line 'not a log line') $null 'malformed line'
    }

    $cases['duration within +-15 %'] = {
        $within = Test-DurationWithinTolerance -DurationSeconds 340 -TargetSeconds 300
        Assert-Equal $within.Within $true '340 s against 300 s'
        Assert-Near $within.DeviationPercent (100.0 * 40.0 / 300.0) 'deviation 13.3 %'
        $outside = Test-DurationWithinTolerance -DurationSeconds 350 -TargetSeconds 300
        Assert-Equal $outside.Within $false '350 s against 300 s'
        Assert-Near $outside.DeviationPercent (100.0 * 50.0 / 300.0) 'deviation 16.7 %'
    }

    $cases['rolling mean and peak'] = {
        $values = [double[]]@(foreach ($i in 1..20) { if ($i -ge 11 -and $i -le 15) { 30.0 } else { 10.0 } })
        Assert-Near (Measure-RollingMeanPeak -Values $values) 20 'peak 10-s mean'
        Assert-Near (Measure-Peak -Values $values) 30 'peak sample'
        Assert-Near (Measure-RollingMeanPeak -Values ([double[]]@(1, 2, 3))) $null 'fewer than 10 samples'
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
    }

    $cases['markdown: criterion-1 per-type header and cell escaping'] = {
        $expected = '| Type | Runs | FAIL within 300 s | Median interval [L, U] (s) | Ratio interval vs fastest baseline | Within 5 min (' +
            [char]0x2265 + ' 9 of 10) | At least 5' + [char]0x00D7 + ' faster | No later than Quick |'
        Assert-Equal (Format-MarkdownTableHeader -Header $Headers.Criterion1Types) $expected 'header'
        $table = Format-MarkdownTable -Header @('A', 'B') -Rows @(, [object[]]@('x|y', $null))
        Assert-Equal $table[2] ('| x\|y | ' + [char]0x2014 + ' |') 'escaped row'
        Assert-Equal (Format-Interval 200 ([double]::PositiveInfinity) 1) ('[200.0, ' + [char]0x221E + ']') 'interval with infinity'
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

