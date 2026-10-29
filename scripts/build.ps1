<#
.SYNOPSIS
    Builds RAMQuick, runs every automated test suite and enforces the quality gates, in one
    command.

.DESCRIPTION
    The local replacement for a CI pipeline. Steps, in order:

      1. Constraints           scripts/check-constraints.ps1: no networking, system-configuration
                               or out-of-layer native-call APIs in src/.
      2. Native style          clang-format --style=file --dry-run --Werror over every *.h and
                               *.cpp file under src/engine and tests/engine.tests (root
                               .clang-format), in batches of 50 files.
      3. Validation self-test  scripts/Summarize-ValidationRuns.ps1 -SelfTest.
      4. Build                 Visual Studio's MSBuild on RamQuick.sln with -restore, Platform
                               x64 and the chosen configuration. A failed build ends the run
                               here, because there is nothing to test.
      5. Third-party notices   Every MPL-2.0 package in the closure frozen by
                               src/RamQuick.App/packages.lock.json must have a
                               THIRD-PARTY-NOTICES.txt entry with its package ID, its exact
                               version and a non-empty Source.
      6. Native tests          artifacts/bin/native/<Configuration>/engine.tests.exe (GoogleTest).
                               Tests skipped with GTEST_SKIP (missing CPU features or
                               SeLockMemoryPrivilege) count as skipped, not failed.
      7. Managed tests         vstest.console.exe over each of the six managed test projects,
                               one step per project, excluding Category=Long unless
                               -IncludeLong is given.
      8. Coverage              With -Coverage only: line coverage merged across every suite's
                               Cobertura report and gated per library (see -Coverage).

    The static gates (1-3) run first. Apart from a failed build, every step runs even after an
    earlier step failed, so one invocation reports every problem. A summary table of every step
    (status, seconds, detail) closes the run.

    Outputs, all under artifacts/:
      artifacts/test-results/engine.tests.xml          GoogleTest XML report
      artifacts/test-results/<Project>.trx             one VSTest TRX per managed test project
      artifacts/test-results/<guid>/*.cobertura.xml    with -Coverage: the collector's reports
      artifacts/coverage/coverage.runsettings          with -Coverage: the collector settings
      artifacts/coverage/coverage-summary.md           with -Coverage: the coverage gate table
    artifacts/test-results/ is recreated empty at the start of every run, and artifacts/coverage/
    as well with -Coverage, so no stale report is ever counted. Build output stays where the
    projects put it: artifacts/bin and artifacts/obj (native), each project's bin and obj
    (managed), and vcpkg_installed/ (GoogleTest).

    The script installs nothing, writes no registry value and makes no network call itself;
    NuGet and vcpkg downloads during -restore are MSBuild's. Its only environment change is
    VCPKG_ROOT for this run when it is unset, restored on exit.

.PARAMETER Configuration
    Release (default) or Debug: the solution configuration to build and test.

.PARAMETER Coverage
    Collects line coverage of the managed libraries with the Microsoft.CodeCoverage collector
    (Cobertura format) and fails the run when a group is below its minimum:
    90 percent for src/RamQuick.Core, src/RamQuick.Plans and src/RamQuick.Analysis, and
    80 percent for src/RamQuick.Logging, src/RamQuick.Interop, src/RamQuick.Sensors,
    src/RamQuick.App/Services and src/RamQuick.App/ViewModels. Native code is held to a
    behavioural test matrix instead of a percentage and is not instrumented.

.PARAMETER IncludeLong
    Also runs the tests marked [Trait("Category","Long")]: the full-length real-plan runs on a
    1 GiB region, which take about 85 minutes. Without this switch they are filtered out with
    Category!=Long so a routine build stays short.

.EXAMPLE
    .\scripts\build.ps1

    Release build, every static gate, the native tests and the managed tests without the Long
    category.

.EXAMPLE
    .\scripts\build.ps1 -Coverage

    The same run with line coverage collected and gated.

.EXAMPLE
    .\scripts\build.ps1 -Configuration Debug -IncludeLong

    Debug build and tests, including the full-length Long tests.

.NOTES
    Toolchain (exact versions in the README and Directory.Packages.props):
      - Visual Studio 2022 Build Tools 17.14 (MSBuild 17.14), workloads
        Microsoft.VisualStudio.Workload.VCTools and
        Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools, components
        Microsoft.VisualStudio.Component.Vcpkg, Microsoft.VisualStudio.Component.VC.Llvm.Clang
        and Microsoft.VisualStudio.Component.TestTools.BuildTools. Located with vswhere and
        restricted to [17.14,18.0), so a Visual Studio 2026 (18.x) installation is never used.
      - MSVC toolset v143 14.44 and Windows SDK 10.0.26100.0.
      - .NET SDK 8.0.427, pinned in global.json (rollForward latestPatch).
      - vcpkg bundled with Visual Studio, manifest baseline
        9eec002df1cd109f8f104cd966a763daf9751489, triplet x64-windows-static (GoogleTest 1.18.0).
      - clang-format bundled with Visual Studio 17.14 (component VC.Llvm.Clang).
      - Windows PowerShell 5.1.

    Exit codes: 0 = every step passed (skipped steps allowed); 1 = at least one step failed;
    2 = a preflight or toolchain problem stopped the run before any step.

    Compatible with Windows PowerShell 5.1. The source is ASCII-only because Windows
    PowerShell reads a BOM-less script as ANSI.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Coverage,

    [switch]$IncludeLong
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------

# VS 2022 17.14 only (MSVC v143 14.44). VS 2026 (18.x) is also released and must not be picked.
$VisualStudioVersionRange = '[17.14,18.0)'
$RequiredVisualStudioComponents = @(
    'Microsoft.Component.MSBuild',
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
)
$RequiredVisualStudioWorkloads = 'Microsoft.VisualStudio.Workload.VCTools and Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools'

# global.json pins 8.0.427 with rollForward latestPatch: any 8.0.4xx patch at or above it.
$RequiredDotNetSdk = [version]'8.0.427'

$ClangFormatBatchSize = 50

# MPL-2.0 packages named by the dependency inventory. They are checked even if a nuspec's
# licence metadata were incomplete; the nuspec scan adds any other MPL-2.0 package.
$KnownMplPackageIds = @('LibreHardwareMonitorLib', 'RAMSPDToolkit-NDD', 'DiskInfoToolkit')

# tests/RamQuick.TestHost is a console host for the process-level tests, not a test project.
$ManagedTestProjects = @(
    'tests\RamQuick.Analysis.Tests\RamQuick.Analysis.Tests.csproj',
    'tests\RamQuick.Plans.Tests\RamQuick.Plans.Tests.csproj',
    'tests\RamQuick.Sensors.Tests\RamQuick.Sensors.Tests.csproj',
    'tests\RamQuick.Logging.Tests\RamQuick.Logging.Tests.csproj',
    'tests\RamQuick.App.Tests\RamQuick.App.Tests.csproj',
    'tests\RamQuick.IntegrationTests\RamQuick.IntegrationTests.csproj'
)

# Line-coverage minimums in percent. Prefixes are repository-relative with forward slashes;
# a prefix covers its subfolders (src/RamQuick.Analysis/ includes Rules/).
$CoreLibraryLineCoverageMinimum = 90
$ServiceLibraryLineCoverageMinimum = 80
$CoverageGroups = @(
    [pscustomobject]@{ Name = 'Core'; Prefix = 'src/RamQuick.Core/'; Minimum = $CoreLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'Plans'; Prefix = 'src/RamQuick.Plans/'; Minimum = $CoreLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'Analysis'; Prefix = 'src/RamQuick.Analysis/'; Minimum = $CoreLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'Logging'; Prefix = 'src/RamQuick.Logging/'; Minimum = $ServiceLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'Interop'; Prefix = 'src/RamQuick.Interop/'; Minimum = $ServiceLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'Sensors'; Prefix = 'src/RamQuick.Sensors/'; Minimum = $ServiceLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'App Services'; Prefix = 'src/RamQuick.App/Services/'; Minimum = $ServiceLibraryLineCoverageMinimum },
    [pscustomobject]@{ Name = 'App ViewModels'; Prefix = 'src/RamQuick.App/ViewModels/'; Minimum = $ServiceLibraryLineCoverageMinimum }
)

# Repository paths. Data paths read from reports are combined with [System.IO.Path] instead,
# because Join-Path fails on a drive that does not exist on this machine.
$ScriptsDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepoRoot 'RamQuick.sln'
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
$TestResultsDir = Join-Path $ArtifactsDir 'test-results'
$CoverageDir = Join-Path $ArtifactsDir 'coverage'
$RunSettingsPath = Join-Path $CoverageDir 'coverage.runsettings'
$CoverageSummaryPath = Join-Path $CoverageDir 'coverage-summary.md'
$NativeOutDir = Join-Path $ArtifactsDir (Join-Path 'bin' 'native')
$EngineTestsXmlPath = Join-Path $TestResultsDir 'engine.tests.xml'
$AppLockFilePath = Join-Path $RepoRoot (Join-Path 'src\RamQuick.App' 'packages.lock.json')
$AppAssetsFilePath = Join-Path $RepoRoot (Join-Path 'src\RamQuick.App\obj' 'project.assets.json')
$NoticesPath = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt'

$StepResults = New-Object 'System.Collections.Generic.List[object]'
$script:Toolchain = $null
$script:ExitCode = 2

# ---------------------------------------------------------------------------------------------
# General helpers
# ---------------------------------------------------------------------------------------------

function Write-Section {
    param([Parameter(Mandatory = $true)][string]$Text)

    Write-Host ''
    Write-Host ('==> {0}' -f $Text) -ForegroundColor Cyan
}

# Runs one external tool. Every native call in this script goes through here.
# Output streams to the console by default; -CaptureTo collects the lines of a small output
# (versions, paths) instead, and -Quiet discards everything for a re-run whose output has
# already been shown. Returns the tool's exit code.
function Invoke-NativeTool {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$StepName,
        [System.Collections.Generic.List[string]]$CaptureTo,
        [switch]$Quiet
    )

    # Windows PowerShell 5.1 turns a native tool's redirected stderr lines into error records,
    # which $ErrorActionPreference = 'Stop' makes terminating. Failures are read from the exit
    # code instead, never from exceptions.
    $ErrorActionPreference = 'Continue'

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw ('{0}: {1} was not found.' -f $StepName, $FilePath)
    }
    Write-Verbose ('{0}: "{1}" {2}' -f $StepName, $FilePath, ($ArgumentList -join ' '))

    if ($null -ne $CaptureTo) {
        & $FilePath @ArgumentList | ForEach-Object { $CaptureTo.Add([string]$_) }
    }
    elseif ($Quiet) {
        & $FilePath @ArgumentList 2>&1 | Out-Null
    }
    else {
        # Write-Host, not Out-Host: a caller that redirects this script's streams captures it.
        & $FilePath @ArgumentList | ForEach-Object { Write-Host $_ }
    }
    return [int]$LASTEXITCODE
}

# Prints pipeline objects as formatted text on the same stream as every other message.
function Write-HostText {
    param([Parameter(ValueFromPipeline = $true)][AllowNull()][object]$InputObject)

    begin {
        $items = New-Object 'System.Collections.Generic.List[object]'
    }
    process {
        if ($null -ne $InputObject) {
            $items.Add($InputObject)
        }
    }
    end {
        if ($items.Count -gt 0) {
            $items | Out-String -Stream | ForEach-Object { Write-Host $_.TrimEnd() }
        }
    }
}

# Emits the outcome of a build step from inside its script block.
function Write-StepOutcome {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('passed', 'failed', 'skipped')][string]$Status,
        [string]$Detail = ''
    )

    [pscustomobject]@{ PSTypeName = 'RamQuick.BuildStepOutcome'; Status = $Status; Detail = $Detail }
}

# Runs one step, times it and records its result. The script block reports through
# Write-StepOutcome; finishing without an outcome means passed, and an exception means failed.
# Anything else the block emits is printed, so no tool output is lost.
function Invoke-BuildStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$ScriptBlock
    )

    Write-Section $Name
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    # The block's output is handled as it streams, so tool output appears in order.
    $outcome = @{ Status = 'passed'; Detail = '' }
    try {
        & $ScriptBlock | ForEach-Object {
            if (($null -ne $_) -and ($_.PSObject.TypeNames -contains 'RamQuick.BuildStepOutcome')) {
                $outcome.Status = $_.Status
                $outcome.Detail = $_.Detail
            }
            else {
                $_ | Write-HostText
            }
        }
    }
    catch {
        $outcome.Status = 'failed'
        $outcome.Detail = $_.Exception.Message
    }
    $stopwatch.Stop()

    $result = [pscustomobject]@{
        Step    = $Name
        Status  = $outcome.Status
        Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        Detail  = $outcome.Detail
    }
    $StepResults.Add($result)

    $color = 'Green'
    if ($result.Status -eq 'failed') {
        $color = 'Red'
    }
    elseif ($result.Status -eq 'skipped') {
        $color = 'Yellow'
    }
    $suffix = ''
    if ($result.Detail) {
        $suffix = ' - ' + $result.Detail
    }
    Write-Host ('<== {0}: {1} ({2} s){3}' -f $Name, $result.Status, $result.Seconds, $suffix) -ForegroundColor $color
    return $result
}

# Loads an XML file without DTD processing or any resolver, so a DOCTYPE in a report can never
# trigger a download.
function Get-XmlDocument {
    param([Parameter(Mandatory = $true)][string]$Path)

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.Xml.XmlReader]::Create($stream, $settings)
        try {
            $document = New-Object System.Xml.XmlDocument
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    # The comma keeps PowerShell from enumerating the document's child nodes.
    return , $document
}

function Get-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $prefix = $RepoRoot.TrimEnd('\') + '\'
    if ($Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($prefix.Length).Replace('\', '/')
    }
    return $null
}

function Get-InvariantNumberText {
    param([Parameter(Mandatory = $true)][double]$Value)

    return $Value.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------------------

function Assert-Platform {
    if ($env:OS -ne 'Windows_NT') {
        throw 'RAMQuick builds on Windows 10/11 x64 only.'
    }
    if ($PSVersionTable.PSVersion -lt [version]'5.1') {
        throw ('Windows PowerShell 5.1 or later is required; this is PowerShell {0}.' -f $PSVersionTable.PSVersion)
    }
}

function Get-VsWhereValue {
    param(
        [Parameter(Mandatory = $true)][string]$VsWhere,
        [Parameter(Mandatory = $true)][string]$Property
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $arguments = @('-latest', '-products', '*', '-version', $VisualStudioVersionRange, '-requires') +
        $RequiredVisualStudioComponents + @('-property', $Property, '-format', 'value')
    $code = Invoke-NativeTool -FilePath $VsWhere -ArgumentList $arguments -StepName 'Preflight' -CaptureTo $lines
    if ($code -ne 0) {
        throw ('vswhere.exe failed with exit code {0}.' -f $code)
    }
    $value = @($lines | Where-Object { $_.Trim() } | Select-Object -First 1)
    if ($value.Count -eq 0) {
        return ''
    }
    return $value[0].Trim()
}

# Locates the Visual Studio 2022 17.14 installation and the tools this script runs from it.
function Find-VisualStudio {
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installHint = 'Install Visual Studio 2022 17.14 Build Tools with the workloads {0}.' -f $RequiredVisualStudioWorkloads
    if (-not (Test-Path -LiteralPath $vsWhere -PathType Leaf)) {
        throw ('vswhere.exe was not found at {0}. {1}' -f $vsWhere, $installHint)
    }

    # -products * is required to find Build Tools installations.
    $installation = Get-VsWhereValue -VsWhere $vsWhere -Property 'installationPath'
    if (-not $installation) {
        throw ('No Visual Studio {0} installation with {1} was found. {2}' -f $VisualStudioVersionRange, ($RequiredVisualStudioComponents -join ' and '), $installHint)
    }
    $displayVersion = Get-VsWhereValue -VsWhere $vsWhere -Property 'catalog_productDisplayVersion'

    $msbuild = Join-Path $installation 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        $msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
    }
    $tools = [pscustomobject]@{
        Installation   = $installation
        DisplayVersion = $displayVersion
        MSBuild        = $msbuild
        ClangFormat    = Join-Path $installation 'VC\Tools\Llvm\x64\bin\clang-format.exe'
        VcpkgRoot      = Join-Path $installation 'VC\vcpkg'
        VSTest         = Join-Path $installation 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
    }

    $missing = New-Object 'System.Collections.Generic.List[string]'
    $required = @(
        @($tools.MSBuild, 'MSBuild.exe', 'Microsoft.Component.MSBuild'),
        @($tools.ClangFormat, 'clang-format.exe', 'Microsoft.VisualStudio.Component.VC.Llvm.Clang'),
        @((Join-Path $tools.VcpkgRoot 'vcpkg.exe'), 'vcpkg.exe', 'Microsoft.VisualStudio.Component.Vcpkg'),
        @($tools.VSTest, 'vstest.console.exe', 'Microsoft.VisualStudio.Component.TestTools.BuildTools')
    )
    foreach ($entry in $required) {
        if (-not (Test-Path -LiteralPath $entry[0] -PathType Leaf)) {
            $missing.Add(('{0} (component {1})' -f $entry[1], $entry[2]))
        }
    }
    if ($missing.Count -gt 0) {
        throw ('The Visual Studio installation at {0} lacks {1}. Add the missing component with the Visual Studio Installer. {2}' -f $installation, ($missing -join ', '), $installHint)
    }
    return $tools
}


# The native test projects restore GoogleTest in vcpkg manifest mode. VCPKG_ROOT is what a
# Developer prompt provides; an existing value is respected, otherwise the vcpkg bundled with
# this Visual Studio installation is selected, and the caller sets VCPKG_ROOT for this run only.
function Get-VcpkgRoot {
    param([Parameter(Mandatory = $true)][string]$BundledRoot)

    if ($env:VCPKG_ROOT) {
        if (-not (Test-Path -LiteralPath (Join-Path $env:VCPKG_ROOT 'vcpkg.exe') -PathType Leaf)) {
            throw ('VCPKG_ROOT is set to {0}, which holds no vcpkg.exe. Unset it to use the vcpkg bundled with Visual Studio ({1}), or point it at a vcpkg root.' -f $env:VCPKG_ROOT, $BundledRoot)
        }
        return [pscustomobject]@{ Root = $env:VCPKG_ROOT; Origin = 'from VCPKG_ROOT'; SetForRun = $false }
    }
    return [pscustomobject]@{ Root = $BundledRoot; Origin = 'bundled with Visual Studio; VCPKG_ROOT set for this run'; SetForRun = $true }
}

# Returns the SDK that global.json selects: the newest 8.0.4xx release at or above 8.0.427.
function Get-DotNetSdk {
    $installHint = 'Install .NET SDK {0}.' -f $RequiredDotNetSdk
    $dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dotnet) {
        throw ('dotnet was not found on PATH. {0}' -f $installHint)
    }

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $code = Invoke-NativeTool -FilePath $dotnet.Path -ArgumentList @('--list-sdks') -StepName 'Preflight' -CaptureTo $lines
    if ($code -ne 0) {
        throw ('dotnet --list-sdks failed with exit code {0}. {1}' -f $code, $installHint)
    }

    $candidates = New-Object 'System.Collections.Generic.List[version]'
    foreach ($line in $lines) {
        # Release SDKs only: global.json sets allowPrerelease false.
        if ($line -match '^\s*(\d+\.\d+\.\d+)\s+\[') {
            $version = [version]$Matches[1]
            if (($version.Major -eq $RequiredDotNetSdk.Major) -and ($version.Minor -eq $RequiredDotNetSdk.Minor) -and
                ($version.Build -ge $RequiredDotNetSdk.Build) -and ([math]::Floor($version.Build / 100) -eq [math]::Floor($RequiredDotNetSdk.Build / 100))) {
                $candidates.Add($version)
            }
        }
    }
    if ($candidates.Count -eq 0) {
        throw ('No .NET SDK {0}.{1}.{2}xx at or above {3} is installed (found: {4}). {5}' -f $RequiredDotNetSdk.Major,
            $RequiredDotNetSdk.Minor, [math]::Floor($RequiredDotNetSdk.Build / 100), $RequiredDotNetSdk,
            $(if ($lines.Count -gt 0) { ($lines | ForEach-Object { $_.Trim() }) -join '; ' } else { 'none' }), $installHint)
    }
    $selected = $candidates | Sort-Object -Descending | Select-Object -First 1
    return [pscustomobject]@{ Version = $selected.ToString(); DotNet = $dotnet.Path }
}

function Write-ToolchainBanner {
    param(
        [Parameter(Mandatory = $true)]$Tools,
        [Parameter(Mandatory = $true)]$Sdk,
        [Parameter(Mandatory = $true)]$Vcpkg
    )

    $clangVersion = New-Object 'System.Collections.Generic.List[string]'
    $null = Invoke-NativeTool -FilePath $Tools.ClangFormat -ArgumentList @('--version') -StepName 'Preflight' -CaptureTo $clangVersion

    $onOff = @{ $true = 'on'; $false = 'off' }
    Write-Host 'RAMQuick build' -ForegroundColor Cyan
    Write-Host ('  Repository     : {0}' -f $RepoRoot)
    Write-Host ('  Visual Studio  : {0} ({1})' -f $Tools.Installation, $Tools.DisplayVersion)
    Write-Host ('  MSBuild        : {0}' -f $Tools.MSBuild)
    Write-Host ('  VSTest         : {0}' -f $Tools.VSTest)
    Write-Host ('  clang-format   : {0} ({1})' -f $Tools.ClangFormat, (($clangVersion | Where-Object { $_.Trim() }) -join ' ').Trim())
    Write-Host ('  vcpkg root     : {0} ({1})' -f $Vcpkg.Root, $Vcpkg.Origin)
    Write-Host ('  .NET SDK       : {0} ({1})' -f $Sdk.Version, $Sdk.DotNet)
    Write-Host ('  Configuration  : {0} | x64' -f $Configuration)
    Write-Host ('  Switches       : -Coverage {0}, -IncludeLong {1}' -f $onOff[[bool]$Coverage], $onOff[[bool]$IncludeLong])
}

function Invoke-ArtifactDirectoryReset {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not $Path.StartsWith($ArtifactsDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('Refusing to recreate {0}: it is outside {1}.' -f $Path, $ArtifactsDir)
    }
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $Path -Force
}


# ---------------------------------------------------------------------------------------------
# Third-party notices gate
# ---------------------------------------------------------------------------------------------

# Every package (not project) entry of every target section, distinct by ID and version,
# case-insensitively, sorted by ID.
function Get-LockFilePackage {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('{0} was not found; the restore that the build runs generates it.' -f $Path)
    }
    $lock = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -eq $lock.PSObject.Properties['dependencies']) {
        throw ('{0} has no dependencies section.' -f $Path)
    }

    $packages = @{}
    foreach ($section in $lock.dependencies.PSObject.Properties) {
        foreach ($entry in $section.Value.PSObject.Properties) {
            $typeProperty = $entry.Value.PSObject.Properties['type']
            if (($null -ne $typeProperty) -and ($typeProperty.Value -eq 'Project')) {
                continue
            }
            $resolvedProperty = $entry.Value.PSObject.Properties['resolved']
            if (($null -eq $resolvedProperty) -or (-not $resolvedProperty.Value)) {
                throw ('{0}: package {1} in section {2} has no resolved version.' -f $Path, $entry.Name, $section.Name)
            }
            $key = ('{0}|{1}' -f $entry.Name, $resolvedProperty.Value).ToLowerInvariant()
            if (-not $packages.ContainsKey($key)) {
                $packages[$key] = [pscustomobject]@{ Id = $entry.Name; Version = [string]$resolvedProperty.Value }
            }
        }
    }
    return @($packages.Values | Sort-Object -Property Id, Version)
}

# The NuGet global packages folder the restore used.
function Get-NuGetPackageRoot {
    param([Parameter(Mandatory = $true)][string]$AssetsPath)

    if (Test-Path -LiteralPath $AssetsPath -PathType Leaf) {
        # project.restore.packagesPath; read with a pattern because the assets file is large.
        $assets = [System.IO.File]::ReadAllText($AssetsPath, [System.Text.Encoding]::UTF8)
        $match = [regex]::Match($assets, '"packagesPath"\s*:\s*"((?:[^"\\]|\\.)*)"')
        if ($match.Success) {
            $root = ('{"p":"' + $match.Groups[1].Value + '"}' | ConvertFrom-Json).p
            if ($root) {
                return $root
            }
        }
    }
    if ($env:NUGET_PACKAGES) {
        return $env:NUGET_PACKAGES
    }
    return (Join-Path $env:USERPROFILE '.nuget\packages')
}

# True when the package's nuspec declares MPL-2.0: an SPDX expression containing the MPL-2.0
# token, a licence file containing "Mozilla Public License", or a licenseUrl naming the MPL.
function Test-MplLicense {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $packageDir = [System.IO.Path]::Combine($PackageRoot, $Id.ToLowerInvariant(), $Version.ToLowerInvariant())
    $nuspecPath = [System.IO.Path]::Combine($packageDir, $Id.ToLowerInvariant() + '.nuspec')
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
        throw ('{0} {1}: nuspec not found at {2}.' -f $Id, $Version, $nuspecPath)
    }
    $nuspec = Get-XmlDocument -Path $nuspecPath

    $license = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
    if ($null -ne $license) {
        $text = $license.InnerText.Trim()
        $type = $license.GetAttribute('type')
        if (($type -eq 'expression') -and ($text -match '(?<![\w.+-])MPL-2\.0(-no-copyleft-exception)?(?![\w.+-])')) {
            return $true
        }
        if (($type -eq 'file') -and $text) {
            $licensePath = [System.IO.Path]::Combine($packageDir, $text.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                throw ('{0} {1}: licence file {2} named by the nuspec was not found.' -f $Id, $Version, $licensePath)
            }
            $licenseText = [System.IO.File]::ReadAllText($licensePath)
            if ($licenseText.IndexOf('Mozilla Public License', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    }

    $licenseUrl = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='licenseUrl']")
    if (($null -ne $licenseUrl) -and ($licenseUrl.InnerText -match 'mozilla\.org/MPL|licenses\.nuget\.org/MPL-2\.0')) {
        return $true
    }
    return $false
}

# Parses THIRD-PARTY-NOTICES.txt. Entries are separated by lines made only of '=' characters;
# the first Component, Version, License, Project and Source field line of each entry is kept.
# Sections without a Component field (the preamble and the licence appendices) are ignored.
function Get-NoticeEntry {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('{0} was not found.' -f $Path)
    }
    $entries = New-Object 'System.Collections.Generic.List[object]'
    $fields = @{}
    $lines = [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -le $lines.Count; $i++) {
        $isSeparator = ($i -eq $lines.Count) -or ($lines[$i] -match '^=+\s*$')
        if ($isSeparator) {
            if ($fields.ContainsKey('Component')) {
                $entry = [ordered]@{}
                foreach ($name in 'Component', 'Version', 'License', 'Project', 'Source') {
                    $entry[$name] = $(if ($fields.ContainsKey($name)) { $fields[$name] } else { '' })
                }
                $entries.Add([pscustomobject]$entry)
            }
            $fields = @{}
            continue
        }
        if ($lines[$i] -match '^(Component|Version|License|Project|Source):\s*(.*)$') {
            if (-not $fields.ContainsKey($Matches[1])) {
                $fields[$Matches[1]] = $Matches[2].Trim()
            }
        }
    }
    return , $entries.ToArray()
}

# Checks every MPL-2.0 package of the closure for a notices entry with its exact version and a
# Source. Returns one result per MPL-2.0 package, and one per package whose licence could not
# be determined; Problem is $null for a package that passes.
function Test-MplNotice {
    param(
        [Parameter(Mandatory = $true)][object[]]$Packages,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Entries,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    foreach ($package in $Packages) {
        try {
            $isMpl = Test-MplLicense -PackageRoot $PackageRoot -Id $package.Id -Version $package.Version
        }
        catch {
            # An unreadable or missing nuspec fails the gate; every such package is reported.
            [pscustomobject]@{ Id = $package.Id; Version = $package.Version; Problem = ('licence not determined: {0}' -f $_.Exception.Message) }
            continue
        }
        if ((-not $isMpl) -and ($KnownMplPackageIds -notcontains $package.Id)) {
            continue
        }
        $sameId = @($Entries | Where-Object { $_.Component -eq $package.Id })
        $exact = @($sameId | Where-Object { $_.Version -eq $package.Version })
        $problem = $null
        if ($exact.Count -eq 0) {
            $problem = 'missing entry'
            if ($sameId.Count -gt 0) {
                $problem += (' (notices list version {0})' -f (($sameId | ForEach-Object { $_.Version }) -join ', '))
            }
        }
        elseif (@($exact | Where-Object { $_.Source }).Count -eq 0) {
            $problem = 'missing Source'
        }
        [pscustomobject]@{ Id = $package.Id; Version = $package.Version; Problem = $problem }
    }
}


# ---------------------------------------------------------------------------------------------
# Coverage gate
# ---------------------------------------------------------------------------------------------

# Collector settings for vstest.console.exe: Cobertura output, the RamQuick product assemblies
# only (the App assembly is RamQuick.dll), generated code excluded through its attributes, and
# no native instrumentation, because native code is held to a behavioural matrix instead.
function Write-RunSettingsFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $settings = @'
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="Code Coverage" uri="datacollector://Microsoft/CodeCoverage/2.0">
        <Configuration>
          <Format>Cobertura</Format>
          <CodeCoverage>
            <ModulePaths>
              <Include>
                <ModulePath>.*\\RamQuick(\.(Core|Interop|Plans|Analysis|Sensors|Logging))?\.dll$</ModulePath>
              </Include>
              <Exclude>
                <ModulePath>.*Tests\.dll$</ModulePath>
                <ModulePath>.*TestHost\.dll$</ModulePath>
              </Exclude>
            </ModulePaths>
            <Attributes>
              <Exclude>
                <Attribute>^System\.CodeDom\.Compiler\.GeneratedCodeAttribute$</Attribute>
                <Attribute>^System\.Diagnostics\.CodeAnalysis\.ExcludeFromCodeCoverageAttribute$</Attribute>
              </Exclude>
            </Attributes>
            <EnableStaticNativeInstrumentation>False</EnableStaticNativeInstrumentation>
            <EnableDynamicNativeInstrumentation>False</EnableDynamicNativeInstrumentation>
          </CodeCoverage>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
'@
    Write-Utf8File -Path $Path -Text $settings
    # Fails here, not inside the test runner, if the settings are ever edited into invalid XML.
    $null = Get-XmlDocument -Path $Path
}

function Get-CoberturaSourcePath {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [AllowEmptyCollection()][string[]]$Sources = @()
    )

    try {
        if ([System.IO.Path]::IsPathRooted($FileName)) {
            return [System.IO.Path]::GetFullPath($FileName)
        }
        $first = $null
        foreach ($source in $Sources) {
            $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($source, $FileName))
            if ($null -eq $first) {
                $first = $candidate
            }
            if ([System.IO.File]::Exists($candidate)) {
                return $candidate
            }
        }
        return $first
    }
    catch [System.ArgumentException], [System.NotSupportedException], [System.IO.PathTooLongException] {
        # A file name that is not a valid path (for example a generator's virtual file) is
        # not repository source.
        return $null
    }
}

# Merges Cobertura reports into one line map: repository-relative path (forward slashes) ->
# line number -> covered. A line counts as covered when any report records hits > 0 for it,
# because several suites cover the same library. Only files under src/ outside obj/ count.
function Measure-CoberturaCoverage {
    param([Parameter(Mandatory = $true)][string[]]$ReportPath)

    $lineMap = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.Dictionary[int,bool]]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($report in $ReportPath) {
        $document = Get-XmlDocument -Path $report
        $sources = @($document.SelectNodes('/coverage/sources/source') |
            ForEach-Object { $_.InnerText.Trim() } |
            Where-Object { $_ })
        foreach ($class in $document.SelectNodes('/coverage/packages/package/classes/class')) {
            $fileName = $class.GetAttribute('filename')
            if (-not $fileName) {
                continue
            }
            $fullPath = Get-CoberturaSourcePath -FileName $fileName -Sources $sources
            if (-not $fullPath) {
                continue
            }
            $relative = Get-RepoRelativePath -Path $fullPath
            if ((-not $relative) -or (-not $relative.StartsWith('src/', [System.StringComparison]::OrdinalIgnoreCase)) -or
                ($relative.IndexOf('/obj/', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                continue
            }
            if (-not $lineMap.ContainsKey($relative)) {
                $lineMap[$relative] = New-Object 'System.Collections.Generic.Dictionary[int,bool]'
            }
            $lines = $lineMap[$relative]
            # Class-level and method-level line elements describe the same lines; the map
            # deduplicates them.
            foreach ($line in $class.SelectNodes('.//line')) {
                $number = 0
                $hits = [long]0
                if (-not [int]::TryParse($line.GetAttribute('number'), [ref]$number)) {
                    continue
                }
                $null = [long]::TryParse($line.GetAttribute('hits'), [ref]$hits)
                $covered = $hits -gt 0
                if ($lines.ContainsKey($number)) {
                    $lines[$number] = $lines[$number] -or $covered
                }
                else {
                    $lines[$number] = $covered
                }
            }
        }
    }
    return , $lineMap
}

function Measure-CoverageGroup {
    param(
        [Parameter(Mandatory = $true)]$LineMap,
        [Parameter(Mandatory = $true)][object[]]$Groups
    )

    foreach ($group in $Groups) {
        $coverable = 0
        $covered = 0
        foreach ($file in $LineMap.GetEnumerator()) {
            if ($file.Key.StartsWith($group.Prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $coverable += $file.Value.Count
                foreach ($isCovered in $file.Value.Values) {
                    if ($isCovered) {
                        $covered++
                    }
                }
            }
        }
        # Integer comparison, so a group exactly at its minimum passes without rounding error.
        $meetsMinimum = ($coverable -gt 0) -and (($covered * 100) -ge ($group.Minimum * $coverable))
        $reason = ''
        if ($coverable -eq 0) {
            $reason = 'no coverage data'
        }
        elseif (-not $meetsMinimum) {
            $reason = 'below minimum'
        }
        [pscustomobject]@{
            Name      = $group.Name
            Prefix    = $group.Prefix
            Covered   = $covered
            Coverable = $coverable
            Percent   = $(if ($coverable -gt 0) { 100.0 * $covered / $coverable } else { $null })
            Minimum   = $group.Minimum
            Passed    = $meetsMinimum
            Reason    = $reason
        }
    }
}

function Get-CoverageSummaryText {
    param(
        [Parameter(Mandatory = $true)][object[]]$Results,
        [Parameter(Mandatory = $true)][int]$ReportCount
    )

    $builder = New-Object System.Text.StringBuilder
    $null = $builder.AppendLine('# RAMQuick line coverage')
    $null = $builder.AppendLine()
    $null = $builder.AppendLine(('Merged from {0} Cobertura report(s) under artifacts/test-results/. A line is covered when any suite executed it.' -f $ReportCount))
    $null = $builder.AppendLine()
    $null = $builder.AppendLine('| Group | Path | Covered / coverable | Line coverage | Minimum | Result |')
    $null = $builder.AppendLine('|-------|------|---------------------|---------------|---------|--------|')
    foreach ($result in $Results) {
        $percentText = 'n/a'
        if ($null -ne $result.Percent) {
            $percentText = (Get-InvariantNumberText -Value $result.Percent) + ' %'
        }
        $resultText = 'pass'
        if (-not $result.Passed) {
            $resultText = 'FAIL: ' + $result.Reason
        }
        $null = $builder.AppendLine(('| {0} | `{1}` | {2} / {3} | {4} | {5} % | {6} |' -f $result.Name, $result.Prefix,
                $result.Covered, $result.Coverable, $percentText, $result.Minimum, $resultText))
    }
    return $builder.ToString()
}


# ---------------------------------------------------------------------------------------------
# Steps
# ---------------------------------------------------------------------------------------------

function Invoke-ConstraintsGate {
    $scriptPath = Join-Path $ScriptsDir 'check-constraints.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw 'scripts/check-constraints.ps1 was not found.'
    }

    # Hit lines and the summary arrive on the success stream and are printed as they come. The
    # summary line is required as well as the exit code: a parameter-binding failure would end
    # the script without running it and leave the exit code unchanged.
    $ErrorActionPreference = 'Continue'
    $summaryLines = New-Object 'System.Collections.Generic.List[string]'
    $global:LASTEXITCODE = -1
    & $scriptPath | ForEach-Object {
        $text = [string]$_
        Write-Host $text
        if ($text.StartsWith('check-constraints: ')) {
            $summaryLines.Add($text)
        }
    }
    $code = $LASTEXITCODE
    $summary = $null
    if ($summaryLines.Count -gt 0) {
        $summary = $summaryLines[$summaryLines.Count - 1]
    }

    if ($null -eq $summary) {
        Write-StepOutcome -Status failed -Detail ('check-constraints.ps1 ended (exit code {0}) without its summary line.' -f $code)
    }
    elseif ($code -eq 0) {
        Write-StepOutcome -Status passed -Detail $summary
    }
    elseif ($code -eq 1) {
        Write-StepOutcome -Status failed -Detail $summary
    }
    else {
        Write-StepOutcome -Status failed -Detail ('check-constraints.ps1 could not complete its scan (exit code {0}).' -f $code)
    }
}

function Invoke-NativeStyleGate {
    $files = New-Object 'System.Collections.Generic.List[string]'
    $excludedSegments = @('bin', 'obj', 'x64')
    foreach ($relativeRoot in @('src\engine', 'tests\engine.tests')) {
        $root = Join-Path $RepoRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
            if (@('.h', '.cpp') -notcontains $file.Extension) {
                continue
            }
            $relativeDirectory = Get-RepoRelativePath -Path $file.DirectoryName
            $segments = @($relativeDirectory.Split('/') | Where-Object { $excludedSegments -contains $_ })
            if ($segments.Count -eq 0) {
                $files.Add($file.FullName)
            }
        }
    }
    if ($files.Count -eq 0) {
        Write-StepOutcome -Status failed -Detail 'No *.h or *.cpp file was found under src/engine or tests/engine.tests.'
        return
    }

    $sorted = @($files | Sort-Object)
    $clangFormat = $script:Toolchain.ClangFormat
    $checkArguments = @('--style=file', '--dry-run', '--Werror')
    $offenders = New-Object 'System.Collections.Generic.List[string]'
    $failedBatches = 0
    for ($start = 0; $start -lt $sorted.Count; $start += $ClangFormatBatchSize) {
        $end = [math]::Min($start + $ClangFormatBatchSize, $sorted.Count) - 1
        $batch = @($sorted[$start..$end])
        $code = Invoke-NativeTool -FilePath $clangFormat -ArgumentList ($checkArguments + $batch) -StepName 'Native style'
        if ($code -ne 0) {
            $failedBatches++
            # The batch output above names each violation; a quiet re-run per file names the files.
            foreach ($path in $batch) {
                if ((Invoke-NativeTool -FilePath $clangFormat -ArgumentList ($checkArguments + @($path)) -StepName 'Native style' -Quiet) -ne 0) {
                    $offenders.Add($path)
                }
            }
        }
    }

    if ($offenders.Count -gt 0) {
        Write-Host ('{0} file(s) are not formatted as .clang-format requires:' -f $offenders.Count) -ForegroundColor Red
        foreach ($path in $offenders) {
            Write-Host ('  {0}' -f (Get-RepoRelativePath -Path $path))
        }
        Write-Host ('Reformat each one yourself with: "{0}" -i --style=file <file>' -f $clangFormat)
        Write-StepOutcome -Status failed -Detail ('{0} of {1} native file(s) need clang-format.' -f $offenders.Count, $sorted.Count)
    }
    elseif ($failedBatches -gt 0) {
        Write-StepOutcome -Status failed -Detail ('clang-format failed on {0} batch(es) although every file passed on its own; see the output above.' -f $failedBatches)
    }
    else {
        Write-StepOutcome -Status passed -Detail ('{0} native file(s) match .clang-format.' -f $sorted.Count)
    }
}

function Invoke-ValidationSelfTest {
    $scriptPath = Join-Path $ScriptsDir 'Summarize-ValidationRuns.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw 'scripts/Summarize-ValidationRuns.ps1 was not found.'
    }

    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    & $scriptPath -SelfTest | Write-HostText
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-StepOutcome -Status passed -Detail 'Summarize-ValidationRuns.ps1 -SelfTest passed.'
    }
    else {
        Write-StepOutcome -Status failed -Detail ('Summarize-ValidationRuns.ps1 -SelfTest exited with code {0}.' -f $code)
    }
}

function Invoke-SolutionBuild {
    # -restore generates or refreshes each managed project's committed packages.lock.json
    # (RestorePackagesWithLockFile=true in Directory.Build.props) and restores GoogleTest 1.18.0
    # for the native test projects through vcpkg manifest mode (x64-windows-static) into
    # vcpkg_installed/. -nodeReuse:false and UseSharedCompilation=false leave no MSBuild node or
    # compiler server running after the build, holding files or this run's environment.
    $arguments = @(
        $SolutionPath, '-restore', '-m', '-nologo', '-v:minimal',
        ('-p:Configuration={0}' -f $Configuration), '-p:Platform=x64',
        '-nodeReuse:false', '-p:UseSharedCompilation=false'
    )
    $code = Invoke-NativeTool -FilePath $script:Toolchain.MSBuild -ArgumentList $arguments -StepName 'Build'
    if ($code -ne 0) {
        Write-StepOutcome -Status failed -Detail ('MSBuild exited with code {0}.' -f $code)
    }
    else {
        Write-StepOutcome -Status passed -Detail ('RamQuick.sln built ({0}|x64).' -f $Configuration)
    }
}

function Invoke-NoticesGate {
    $packages = @(Get-LockFilePackage -Path $AppLockFilePath)
    $packageRoot = Get-NuGetPackageRoot -AssetsPath $AppAssetsFilePath
    Write-Host ('Closure: {0} package(s) in src/RamQuick.App/packages.lock.json; NuGet packages folder {1}' -f $packages.Count, $packageRoot)
    $entries = Get-NoticeEntry -Path $NoticesPath

    $checks = @(Test-MplNotice -Packages $packages -Entries $entries -PackageRoot $packageRoot)
    $problems = @($checks | Where-Object { $_.Problem })
    foreach ($check in $checks) {
        if ($check.Problem) {
            Write-Host ('  FAIL {0} {1}: {2}' -f $check.Id, $check.Version, $check.Problem) -ForegroundColor Red
        }
        else {
            Write-Host ('  ok   {0} {1}: MPL-2.0, notices entry with Source' -f $check.Id, $check.Version)
        }
    }

    if ($problems.Count -gt 0) {
        Write-StepOutcome -Status failed -Detail (($problems | ForEach-Object { '{0} {1}: {2}' -f $_.Id, $_.Version, $_.Problem }) -join '; ')
    }
    else {
        Write-StepOutcome -Status passed -Detail ('{0} MPL-2.0 package(s) have a THIRD-PARTY-NOTICES.txt entry with Source: {1}.' -f $checks.Count,
            (($checks | ForEach-Object { '{0} {1}' -f $_.Id, $_.Version }) -join ', '))
    }
}

function Invoke-NativeTest {
    $exe = Join-Path (Join-Path $NativeOutDir $Configuration) 'engine.tests.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw ('engine.tests.exe was not found at {0}; the build did not produce it.' -f $exe)
    }

    $code = Invoke-NativeTool -FilePath $exe -ArgumentList @(('--gtest_output=xml:{0}' -f $EngineTestsXmlPath)) -StepName 'Native tests'
    $detail = 'no XML report was written'
    if (Test-Path -LiteralPath $EngineTestsXmlPath -PathType Leaf) {
        $root = (Get-XmlDocument -Path $EngineTestsXmlPath).DocumentElement
        $skipped = $root.GetAttribute('skipped')
        if (-not $skipped) {
            $skipped = $root.SelectNodes("//testcase[@result='skipped' or skipped]").Count
        }
        $detail = '{0} tests: {1} failures, {2} errors, {3} disabled, {4} skipped (GTEST_SKIP)' -f $root.GetAttribute('tests'),
            $root.GetAttribute('failures'), $root.GetAttribute('errors'), $root.GetAttribute('disabled'), $skipped
    }

    if ($code -ne 0) {
        Write-StepOutcome -Status failed -Detail ('engine.tests.exe exited with code {0}; {1}.' -f $code, $detail)
    }
    elseif (-not (Test-Path -LiteralPath $EngineTestsXmlPath -PathType Leaf)) {
        Write-StepOutcome -Status failed -Detail 'engine.tests.exe passed but wrote no XML report.'
    }
    else {
        Write-StepOutcome -Status passed -Detail $detail
    }
}

function Get-TrxCounter {
    param([Parameter(Mandatory = $true)][string]$Path)

    $document = Get-XmlDocument -Path $Path
    $counters = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) {
        return $null
    }
    return [pscustomobject]@{
        Total       = $counters.GetAttribute('total')
        Passed      = $counters.GetAttribute('passed')
        Failed      = $counters.GetAttribute('failed')
        NotExecuted = $counters.GetAttribute('notExecuted')
    }
}


# Runs one managed test project with vstest.console.exe from the Visual Studio installation.
#
# Deliberately not `msbuild <project> -t:VSTest`: Visual Studio's MSBuild ships no
# Microsoft.TestPlatform.targets, so the VSTest target does not exist there (MSB4057), and the
# VSTest host of the .NET 8 SDK (dotnet msbuild or dotnet test) cannot load the
# Microsoft.CodeCoverage 18.11 collector that Microsoft.NET.Test.Sdk brings. vstest.console.exe
# takes the same inputs: one TRX per project, the Category filter and the coverage runsettings.
function Invoke-ManagedTest {
    param([Parameter(Mandatory = $true)][string]$RelativeProjectPath)

    $projectPath = Join-Path $RepoRoot $RelativeProjectPath
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw ('{0} was not found.' -f $RelativeProjectPath.Replace('\', '/'))
    }
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

    # RamQuick.sln maps every C# project to Platform x64, so the assembly the solution build
    # produced is bin\x64\<Configuration>\<framework>\. MSBuild reports its exact path, so the
    # framework folder follows the single RamQuickTargetFramework property instead of being
    # repeated here, and nothing is rebuilt.
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $arguments = @($projectPath, '-nologo', '-getProperty:TargetPath', ('-p:Configuration={0}' -f $Configuration), '-p:Platform=x64')
    $code = Invoke-NativeTool -FilePath $script:Toolchain.MSBuild -ArgumentList $arguments -StepName $projectName -CaptureTo $lines
    $targetPath = @($lines | Where-Object { $_.Trim() } | Select-Object -Last 1)
    if (($code -ne 0) -or ($targetPath.Count -eq 0)) {
        $lines | Write-HostText
        throw ('MSBuild could not evaluate the TargetPath of {0} (exit code {1}).' -f $projectName, $code)
    }
    $assembly = $targetPath[0].Trim()
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
        throw ('{0} was not built: {1} is missing.' -f $projectName, $assembly)
    }

    # Tests tagged Requires=Hardware skip themselves at run time, so no filter is needed for
    # them, and the integration tests lock at most 1 GiB, which needs no elevation.
    $trxName = '{0}.trx' -f $projectName
    $testArguments = @(
        $assembly, '/Platform:x64',
        ('/ResultsDirectory:{0}' -f $TestResultsDir),
        ('/Logger:trx;LogFileName={0}' -f $trxName)
    )
    if (-not $IncludeLong) {
        $testArguments += '/TestCaseFilter:Category!=Long'
    }
    if ($Coverage) {
        $testArguments += '/Collect:Code Coverage'
        $testArguments += ('/Settings:{0}' -f $RunSettingsPath)
    }
    $code = Invoke-NativeTool -FilePath $script:Toolchain.VSTest -ArgumentList $testArguments -StepName $projectName

    $trxPath = Join-Path $TestResultsDir $trxName
    $detail = 'no TRX report was written'
    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        $counters = Get-TrxCounter -Path $trxPath
        if ($null -ne $counters) {
            $detail = 'total {0}, passed {1}, failed {2}, not executed {3}' -f $counters.Total, $counters.Passed, $counters.Failed, $counters.NotExecuted
        }
        else {
            $detail = 'the TRX report has no result counters'
        }
    }

    if ($code -ne 0) {
        Write-StepOutcome -Status failed -Detail ('vstest.console.exe exited with code {0}; {1}.' -f $code, $detail)
    }
    elseif (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        Write-StepOutcome -Status failed -Detail ('vstest.console.exe passed but wrote no {0}.' -f $trxName)
    }
    else {
        Write-StepOutcome -Status passed -Detail $detail
    }
}

function Invoke-CoverageGate {
    # The TRX logger also copies each attachment into its run's In\ folder; identical files are
    # counted once (the line merge would absorb them anyway, but the report count would not).
    $seen = @{}
    $reports = @(Get-ChildItem -LiteralPath $TestResultsDir -Recurse -File -Filter '*.cobertura.xml' |
        Sort-Object -Property FullName |
        Where-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $isNew = -not $seen.ContainsKey($hash)
            $seen[$hash] = $true
            $isNew
        } |
        ForEach-Object { $_.FullName })
    if ($reports.Count -eq 0) {
        Write-StepOutcome -Status failed -Detail 'No *.cobertura.xml report was found under artifacts/test-results/.'
        return
    }

    $lineMap = Measure-CoberturaCoverage -ReportPath $reports
    $results = @(Measure-CoverageGroup -LineMap $lineMap -Groups $CoverageGroups)
    $summary = Get-CoverageSummaryText -Results $results -ReportCount $reports.Count
    Write-Utf8File -Path $CoverageSummaryPath -Text $summary
    Write-Host $summary

    $failures = @($results | Where-Object { -not $_.Passed })
    if ($failures.Count -gt 0) {
        $descriptions = foreach ($failure in $failures) {
            if ($failure.Coverable -eq 0) {
                '{0}: no coverage data' -f $failure.Name
            }
            else {
                '{0} {1} % < {2} %' -f $failure.Name, (Get-InvariantNumberText -Value $failure.Percent), $failure.Minimum
            }
        }
        Write-StepOutcome -Status failed -Detail ('{0} of {1} group(s) below their minimum: {2}.' -f $failures.Count, $results.Count, ($descriptions -join '; '))
    }
    else {
        Write-StepOutcome -Status passed -Detail ('All {0} groups meet their line-coverage minimum.' -f $results.Count)
    }
}

# ---------------------------------------------------------------------------------------------
# Summary and main
# ---------------------------------------------------------------------------------------------

function Write-StepSummary {
    Write-Section 'Summary'
    $StepResults | Format-Table -Property Step, Status, Seconds, Detail -AutoSize -Wrap | Write-HostText

    Write-Host 'Artifacts:'
    $artifacts = @($EngineTestsXmlPath)
    if (Test-Path -LiteralPath $TestResultsDir -PathType Container) {
        $artifacts += @(Get-ChildItem -LiteralPath $TestResultsDir -File -Filter '*.trx' | Sort-Object -Property Name | ForEach-Object { $_.FullName })
    }
    if ($Coverage) {
        $artifacts += $CoverageSummaryPath
    }
    foreach ($path in $artifacts) {
        $state = ''
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $state = ' (not written)'
        }
        Write-Host ('  {0}{1}' -f (Get-RepoRelativePath -Path $path), $state)
    }

    $failed = @($StepResults | Where-Object { $_.Status -eq 'failed' })
    if ($failed.Count -gt 0) {
        Write-Host ('build: {0} of {1} step(s) failed: {2}.' -f $failed.Count, $StepResults.Count, (($failed | ForEach-Object { $_.Step }) -join ', ')) -ForegroundColor Red
    }
    else {
        Write-Host ('build: all {0} step(s) passed or were skipped.' -f $StepResults.Count) -ForegroundColor Green
    }
}

function Invoke-RamQuickBuild {
    try {
        Assert-Platform
        $tools = Find-VisualStudio
        $vcpkg = Get-VcpkgRoot -BundledRoot $tools.VcpkgRoot
        if ($vcpkg.SetForRun) {
            # Process-scoped, and restored when the script ends.
            $env:VCPKG_ROOT = $vcpkg.Root
        }
        $sdk = Get-DotNetSdk
        $script:Toolchain = $tools
        Write-ToolchainBanner -Tools $tools -Sdk $sdk -Vcpkg $vcpkg

        # Recreated empty so that no stale TRX, XML or Cobertura file is ever counted.
        Invoke-ArtifactDirectoryReset -Path $TestResultsDir
        if ($Coverage) {
            Invoke-ArtifactDirectoryReset -Path $CoverageDir
            Write-RunSettingsFile -Path $RunSettingsPath
        }
    }
    catch {
        Write-Host ('build: preflight failed: {0}' -f $_.Exception.Message) -ForegroundColor Red
        $script:ExitCode = 2
        return
    }

    # Cheap static gates first.
    $null = Invoke-BuildStep -Name 'Constraints' -ScriptBlock { Invoke-ConstraintsGate }
    $null = Invoke-BuildStep -Name 'Native style' -ScriptBlock { Invoke-NativeStyleGate }
    $null = Invoke-BuildStep -Name 'Validation self-test' -ScriptBlock { Invoke-ValidationSelfTest }

    # Nothing can be tested without a build, so a failed build ends the run.
    $build = Invoke-BuildStep -Name 'Build' -ScriptBlock { Invoke-SolutionBuild }
    if ($build.Status -eq 'failed') {
        Write-StepSummary
        $script:ExitCode = 1
        return
    }

    $null = Invoke-BuildStep -Name 'Third-party notices' -ScriptBlock { Invoke-NoticesGate }
    $null = Invoke-BuildStep -Name 'Native tests' -ScriptBlock { Invoke-NativeTest }
    foreach ($project in $ManagedTestProjects) {
        # The block runs synchronously inside Invoke-BuildStep and reads $project from here.
        $null = Invoke-BuildStep -Name ('Managed tests: {0}' -f [System.IO.Path]::GetFileNameWithoutExtension($project)) -ScriptBlock {
            Invoke-ManagedTest -RelativeProjectPath $project
        }
    }
    if ($Coverage) {
        $null = Invoke-BuildStep -Name 'Coverage' -ScriptBlock { Invoke-CoverageGate }
    }
    else {
        $null = Invoke-BuildStep -Name 'Coverage' -ScriptBlock { Write-StepOutcome -Status skipped -Detail 'Not requested; run with -Coverage.' }
    }

    Write-StepSummary
    $script:ExitCode = 0
    if (@($StepResults | Where-Object { $_.Status -eq 'failed' }).Count -gt 0) {
        $script:ExitCode = 1
    }
}

$originalLocation = Get-Location
$originalVcpkgRoot = $env:VCPKG_ROOT
try {
    Set-Location -LiteralPath $RepoRoot
    # Stray pipeline output is printed rather than lost; results travel through $StepResults.
    Invoke-RamQuickBuild | Write-HostText
}
catch {
    Write-Host ('build: unexpected error: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    $script:ExitCode = 1
}
finally {
    Set-Location -LiteralPath $originalLocation.Path
    $env:VCPKG_ROOT = $originalVcpkgRoot
}
exit $script:ExitCode

