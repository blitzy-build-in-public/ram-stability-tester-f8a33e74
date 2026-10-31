<#
.SYNOPSIS
    Publishes RAMQuick as a self-contained portable folder for Windows 10/11 x64 and zips it.

.DESCRIPTION
    Produces the single delivery of RAMQuick: one folder that runs from any location without
    installation, without an installed .NET or Visual C++ runtime and without network access.
    Steps, in order:

      1. Toolchain   Locates Visual Studio 2022 17.14 Build Tools with vswhere, then MSBuild and
                     the newest MSVC dumpbin inside that installation.
      2. Clean       Removes artifacts/publish/RamQuick-win-x64/ and
                     artifacts/publish/RamQuick-win-x64.zip left by an earlier run, so no stale
                     file can reach the new zip.
      3. Publish     Visual Studio's MSBuild on src/RamQuick.App/RamQuick.App.csproj with
                     -restore -t:Publish -p:PublishProfile=Portable -p:Configuration=Release
                     -p:Platform=x64. The profile, Properties/PublishProfiles/Portable.pubxml,
                     makes the output self-contained win-x64 with ReadyToRun, neither trimmed nor
                     single-file, so nothing is extracted to %TEMP% at run time. The csproj's
                     reference to src/engine/ramquick_engine.vcxproj builds the static-CRT engine
                     DLL, and the csproj copies it beside RamQuick.exe.
      4. Verify      The folder root must hold RamQuick.exe beside RamQuick.dll (so no single-file
                     bundle), ramquick_engine.dll, the six RamQuick libraries,
                     LibreHardwareMonitorLib.dll, CommunityToolkit.Mvvm.dll and the
                     self-contained .NET and WPF runtime (hostfxr.dll, coreclr.dll,
                     PresentationFramework.dll, wpfgfx_cor3.dll). No test-only item may appear
                     anywhere in the tree: the fault-injecting ramquick_engine_test.dll,
                     engine.tests.exe, RamQuick.TestHost, test assemblies, xUnit, the test
                     platform, FakeTimeProvider or GoogleTest. dumpbin /dependents must show that
                     ramquick_engine.dll needs no Visual C++ or Universal CRT DLL. Every violation
                     is listed before the run fails.
      5. Package     Copies THIRD-PARTY-NOTICES.txt, README.md and docs/METHODOLOGY.md into the
                     folder at their repository-relative paths, so the README's link to
                     docs/METHODOLOGY.md also resolves in the folder, then zips it to
                     artifacts/publish/RamQuick-win-x64.zip, which extracts to a single
                     RamQuick-win-x64 folder.

    Outputs, both under artifacts/publish/:
      artifacts/publish/RamQuick-win-x64/       the portable folder
      artifacts/publish/RamQuick-win-x64.zip    the same folder, zipped
    The closing summary prints the folder path, its file count and size, and the zip's path,
    size and SHA-256 hash.

    The script installs nothing, writes no registry value, signs nothing and makes no network
    call itself; NuGet downloads of packages and runtime packs during -restore are MSBuild's.
    Installers, MSIX packages, code signing and auto-update are not part of the delivery.

.EXAMPLE
    .\scripts\publish-portable.ps1

    Publishes, verifies and zips the portable folder under artifacts/publish/.

.NOTES
    Toolchain (exact versions in the README and Directory.Packages.props):
      - Visual Studio 2022 Build Tools 17.14 (MSBuild 17.14, MSVC v143 14.44), workloads
        Microsoft.VisualStudio.Workload.VCTools and
        Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools. Located with vswhere and
        restricted to [17.14,18.0), so a Visual Studio 2026 (18.x) installation is never used.
      - Windows SDK 10.0.26100.0.
      - .NET SDK 8.0.427, pinned in global.json (rollForward latestPatch).
      - Windows PowerShell 5.1.

    The script takes no parameters: the delivery is always Release, x64 and profile Portable.

    Exit codes: 0 = the folder and zip were produced and verified; 1 = the publish, the
    verification or the packaging failed, or an unexpected error ended the run; 2 = a preflight
    problem (not Windows, a repository root that cannot be resolved, a missing tool) stopped the
    run before anything was built. Every run the script itself ends closes with a line that
    begins "publish-portable:".

    Compatible with Windows PowerShell 5.1. The source is saved as UTF-8 with a byte order
    mark, as .editorconfig requires, and stays ASCII-only, because Windows PowerShell 5.1
    reads a BOM-less script as ANSI.
#>
[CmdletBinding()]
param()

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

# Files the portable folder must hold at its root. RamQuick.exe beside RamQuick.dll proves the
# app is not a single-file bundle; the last four prove the .NET and WPF runtime are bundled.
$RequiredRootFiles = @(
    'RamQuick.exe',
    'RamQuick.dll',
    'ramquick_engine.dll',
    'RamQuick.Core.dll',
    'RamQuick.Interop.dll',
    'RamQuick.Plans.dll',
    'RamQuick.Analysis.dll',
    'RamQuick.Sensors.dll',
    'RamQuick.Logging.dll',
    'LibreHardwareMonitorLib.dll',
    'CommunityToolkit.Mvvm.dll',
    'hostfxr.dll',
    'coreclr.dll',
    'PresentationFramework.dll',
    'wpfgfx_cor3.dll'
)

# Test-only items, matched against every file and folder name in the tree. The fault-injecting
# engine DLL and the test packages must never ship.
$ForbiddenItemPatterns = @(
    'ramquick_engine_test.dll',
    'engine.tests.exe',
    'RamQuick.TestHost*',
    '*Tests.dll',
    'xunit*.dll',
    'Microsoft.TestPlatform*',
    'Microsoft.VisualStudio.TestPlatform*',
    'testhost*',
    'Microsoft.Extensions.TimeProvider.Testing.dll',
    'gtest*'
)

# Dynamic C runtime DLLs (release and debug). The engine links the CRT statically (/MT), so a
# portable folder never depends on an installed Visual C++ Redistributable.
$ForbiddenEngineDependencyPatterns = @(
    'vcruntime*.dll',
    'msvcp*.dll',
    'ucrtbase*.dll',
    'api-ms-win-crt-*.dll'
)

# Repository-relative paths, kept relative in the folder: README.md links to
# docs/METHODOLOGY.md.
$ReleaseDocuments = @('THIRD-PARTY-NOTICES.txt', 'README.md', 'docs\METHODOLOGY.md')

# -LiteralPath because a checkout folder such as RAMQuick[dev] is a wildcard pattern to -Path;
# ProviderPath because Path keeps a PowerShell drive or provider prefix that MSBuild and the
# .NET file APIs cannot open. A root that cannot be resolved is a preflight failure.
try {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
}
catch {
    Write-Host ('publish-portable: preflight failed: the repository root above the script folder ''{0}'' could not be resolved: {1}' -f $PSScriptRoot, $_.Exception.Message) -ForegroundColor Red
    exit 2
}
$AppProjectPath = Join-Path $RepoRoot 'src\RamQuick.App\RamQuick.App.csproj'
$PublishRoot = Join-Path $RepoRoot 'artifacts\publish'
# Must match PublishDir in src/RamQuick.App/Properties/PublishProfiles/Portable.pubxml.
$PublishDir = Join-Path $PublishRoot 'RamQuick-win-x64'
$ZipPath = Join-Path $PublishRoot 'RamQuick-win-x64.zip'

$script:ExitCode = 1

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
# (paths, a dependency list) instead. Returns the tool's exit code.
function Invoke-NativeTool {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$StepName,
        [System.Collections.Generic.List[string]]$CaptureTo
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
    else {
        # Write-Host, not Out-Host: a caller that redirects this script's streams captures it.
        & $FilePath @ArgumentList | ForEach-Object { Write-Host $_ }
    }
    return [int]$LASTEXITCODE
}

function Get-MegabyteText {
    param([Parameter(Mandatory = $true)][double]$Bytes)

    return ($Bytes / 1MB).ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture)
}

# ---------------------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------------------

function Assert-Platform {
    if ($env:OS -ne 'Windows_NT') {
        throw 'RAMQuick is published on Windows 10/11 x64 only.'
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
    $code = Invoke-NativeTool -FilePath $VsWhere -ArgumentList $arguments -StepName 'Toolchain' -CaptureTo $lines
    if ($code -ne 0) {
        throw ('vswhere.exe failed with exit code {0}.' -f $code)
    }
    $value = @($lines | Where-Object { $_.Trim() } | Select-Object -First 1)
    if ($value.Count -eq 0) {
        return ''
    }
    return $value[0].Trim()
}

# The newest MSVC toolset's x64-hosted, x64-targeting dumpbin, or $null when none is installed.
function Find-DumpBin {
    param([Parameter(Mandatory = $true)][string]$Installation)

    $msvcRoot = Join-Path $Installation 'VC\Tools\MSVC'
    if (-not (Test-Path -LiteralPath $msvcRoot -PathType Container)) {
        return $null
    }
    $candidates = @(Get-ChildItem -LiteralPath $msvcRoot -Directory | ForEach-Object {
            $version = $null
            if ([version]::TryParse($_.Name, [ref]$version)) {
                $path = Join-Path $_.FullName 'bin\Hostx64\x64\dumpbin.exe'
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    [pscustomobject]@{ Version = $version; Path = $path }
                }
            }
        } | Sort-Object -Property Version -Descending)
    if ($candidates.Count -eq 0) {
        return $null
    }
    return $candidates[0].Path
}

# Locates the Visual Studio 2022 17.14 installation and the tools this script runs from it.
# The same selection as scripts/build.ps1, which has no shared module to import it from.
function Find-VisualStudio {
    $installHint = 'Install Visual Studio 2022 17.14 Build Tools with the workloads {0}.' -f $RequiredVisualStudioWorkloads
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not $programFilesX86) {
        throw ('The ProgramFiles(x86) folder is not set, so vswhere.exe cannot be located. {0}' -f $installHint)
    }
    $vsWhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
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
    $dumpBin = Find-DumpBin -Installation $installation

    $missing = New-Object 'System.Collections.Generic.List[string]'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        $missing.Add('MSBuild.exe (component Microsoft.Component.MSBuild)')
    }
    if (-not $dumpBin) {
        $missing.Add('dumpbin.exe (component Microsoft.VisualStudio.Component.VC.Tools.x86.x64)')
    }
    if ($missing.Count -gt 0) {
        throw ('The Visual Studio installation at {0} lacks {1}. Add the missing component with the Visual Studio Installer. {2}' -f $installation, ($missing -join ', '), $installHint)
    }

    return [pscustomobject]@{
        Installation   = $installation
        DisplayVersion = $displayVersion
        MSBuild        = $msbuild
        DumpBin        = $dumpBin
    }
}

function Write-ToolchainBanner {
    param([Parameter(Mandatory = $true)][pscustomobject]$Tools)

    Write-Section 'Toolchain'
    Write-Host ('  Visual Studio  : {0} ({1})' -f $Tools.DisplayVersion, $Tools.Installation)
    Write-Host ('  MSBuild        : {0}' -f $Tools.MSBuild)
    Write-Host ('  dumpbin        : {0}' -f $Tools.DumpBin)
    Write-Host ('  Project        : {0}' -f $AppProjectPath)
    Write-Host ('  Publish folder : {0}' -f $PublishDir)
}

# ---------------------------------------------------------------------------------------------
# Clean and publish
# ---------------------------------------------------------------------------------------------

# Removes the previous portable folder and zip. DeleteExistingFiles in Portable.pubxml is honoured
# only by the Web SDK's FileSystem publish, not by this Microsoft.NET.Sdk project, so the folder
# is emptied here: a file left by an earlier publish, or run data an earlier manual start wrote to
# <exe folder>\data, would otherwise ship in the new zip.
function Invoke-PublishOutputReset {
    foreach ($path in @($PublishDir, $ZipPath)) {
        if (-not $path.StartsWith($PublishRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw ('Refusing to remove {0}: it is outside {1}.' -f $path, $PublishRoot)
        }
        if (Test-Path -LiteralPath $path) {
            Write-Host ('Removing {0}' -f $path)
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

function Invoke-PortablePublish {
    param([Parameter(Mandatory = $true)][pscustomobject]$Tools)

    # The command is the documented one: -restore -t:Publish -p:PublishProfile=Portable
    # -p:Configuration=Release -p:Platform=x64. The profile alone owns RuntimeIdentifier,
    # SelfContained, PublishReadyToRun, PublishTrimmed, PublishSingleFile and PublishDir, so none
    # of them is passed here. Known risk: if restore does not see the profile's ReadyToRun or
    # self-contained settings, the SDK fails with NETSDK1094 (ReadyToRun) or NETSDK1047 (no
    # win-x64 target in project.assets.json). The remedy belongs in Portable.pubxml or the csproj,
    # never in overrides on this command line.
    # The remaining switches change no publish property:
    # - RestoreLockedMode=true fails the restore instead of rewriting a packages.lock.json, so the
    #   folder ships exactly the closure frozen by the committed lock files, the closure the
    #   third-party notices gate in scripts/build.ps1 checks (Directory.Build.props leaves this
    #   switch to the scripts).
    # - -nodeReuse:false and UseSharedCompilation=false leave no MSBuild node or compiler server
    #   running after the script, holding files in the publish folder, as in scripts/build.ps1.
    $arguments = @(
        $AppProjectPath, '-restore', '-t:Publish', '-p:PublishProfile=Portable',
        '-p:Configuration=Release', '-p:Platform=x64',
        '-nologo', '-v:minimal', '-m',
        '-p:RestoreLockedMode=true', '-nodeReuse:false', '-p:UseSharedCompilation=false'
    )
    return (Invoke-NativeTool -FilePath $Tools.MSBuild -ArgumentList $arguments -StepName 'Publish')
}

# ---------------------------------------------------------------------------------------------
# Verification
# ---------------------------------------------------------------------------------------------

# Returns one message per violation: a required root file that is missing, or a test-only item
# anywhere in the tree. Returns nothing for a conforming folder.
function Test-PortableFolder {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$RequiredFile,
        [Parameter(Mandatory = $true)][string[]]$ForbiddenPattern
    )

    $violations = New-Object 'System.Collections.Generic.List[string]'
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        $violations.Add(('The publish folder {0} does not exist.' -f $Path))
        return $violations.ToArray()
    }

    foreach ($name in $RequiredFile) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) {
            $violations.Add(('Missing at the folder root: {0}' -f $name))
        }
    }

    $rootLength = (Get-Item -LiteralPath $Path).FullName.TrimEnd('\').Length + 1
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Recurse -Force)) {
        foreach ($pattern in $ForbiddenPattern) {
            if ($item.Name -like $pattern) {
                $violations.Add(('Test-only item must not ship: {0} (matches {1})' -f $item.FullName.Substring($rootLength), $pattern))
                break
            }
        }
    }
    return $violations.ToArray()
}

# Extracts the DLL names from "dumpbin /dependents" output: the lines of its "Image has the
# following dependencies:" and "... delay load dependencies:" lists, which end at "Summary".
function Get-DumpBinDependency {
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Line
    )

    $dependencies = New-Object 'System.Collections.Generic.List[string]'
    $inList = $false
    foreach ($text in $Line) {
        $trimmed = $text.Trim()
        if ($trimmed -match '^Image has the following (delay load )?dependencies:$') {
            $inList = $true
        }
        elseif ($trimmed -eq 'Summary') {
            $inList = $false
        }
        elseif ($inList -and ($trimmed -match '^\S+\.dll$')) {
            $dependencies.Add($trimmed)
        }
    }
    return $dependencies.ToArray()
}

# Lists the engine DLL's imports with dumpbin and flags every dynamic C runtime among them.
function Test-EngineDependency {
    param(
        [Parameter(Mandatory = $true)][string]$DumpBin,
        [Parameter(Mandatory = $true)][string]$EnginePath,
        [Parameter(Mandatory = $true)][string[]]$ForbiddenPattern
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $code = Invoke-NativeTool -FilePath $DumpBin -ArgumentList @('/nologo', '/dependents', $EnginePath) -StepName 'Verify' -CaptureTo $lines
    $violations = New-Object 'System.Collections.Generic.List[string]'
    $dependencies = @()
    if ($code -ne 0) {
        $violations.Add(('dumpbin /dependents failed on ramquick_engine.dll with exit code {0}: {1}' -f $code, (($lines | Where-Object { $_.Trim() }) -join ' ')))
    }
    else {
        $dependencies = @(Get-DumpBinDependency -Line $lines.ToArray())
        # The RAMQuick engine imports KERNEL32.dll for its Win32 calls, so an empty dependency list
        # means the dumpbin output was not understood and leaves its CRT linkage unverified.
        if ($dependencies.Count -eq 0) {
            $violations.Add('dumpbin /dependents listed no dependency of ramquick_engine.dll, so its C runtime linkage could not be verified.')
        }
        foreach ($dependency in $dependencies) {
            foreach ($pattern in $ForbiddenPattern) {
                if ($dependency -like $pattern) {
                    $violations.Add(('ramquick_engine.dll depends on {0}: the engine must link the C runtime statically (/MT), or the portable folder needs an installed Visual C++ runtime.' -f $dependency))
                    break
                }
            }
        }
    }
    return [pscustomobject]@{
        Dependencies = $dependencies
        Violations   = $violations.ToArray()
    }
}

# Runs every check on the published folder and returns all violations together.
function Test-PublishOutput {
    param([Parameter(Mandatory = $true)][pscustomobject]$Tools)

    $violations = New-Object 'System.Collections.Generic.List[string]'
    foreach ($violation in @(Test-PortableFolder -Path $PublishDir -RequiredFile $RequiredRootFiles -ForbiddenPattern $ForbiddenItemPatterns)) {
        $violations.Add($violation)
    }

    # A missing engine DLL is already reported above; there is nothing to inspect.
    $enginePath = Join-Path $PublishDir 'ramquick_engine.dll'
    if (Test-Path -LiteralPath $enginePath -PathType Leaf) {
        $engine = Test-EngineDependency -DumpBin $Tools.DumpBin -EnginePath $enginePath -ForbiddenPattern $ForbiddenEngineDependencyPatterns
        Write-Host ('ramquick_engine.dll dependencies: {0}' -f (@($engine.Dependencies) -join ', '))
        foreach ($violation in @($engine.Violations)) {
            $violations.Add($violation)
        }
    }

    # The folder was emptied before publishing, so run data here came from the build itself.
    $dataFolder = Join-Path $PublishDir 'data'
    if (Test-Path -LiteralPath $dataFolder -PathType Container) {
        Write-Warning ('{0} exists: run data (journal, logs) must not ship. Remove it and publish again.' -f $dataFolder)
    }
    return $violations.ToArray()
}

# ---------------------------------------------------------------------------------------------
# Packaging
# ---------------------------------------------------------------------------------------------

# Copies the notices and readme beside RamQuick.exe and the methodology to docs\ under it, each at
# its repository-relative path. Returns the paths of missing sources, in which case nothing is
# copied.
function Copy-ReleaseDocument {
    [OutputType([string])]
    param()

    $missing = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in $ReleaseDocuments) {
        $source = Join-Path $RepoRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            $missing.Add($source)
        }
    }
    if ($missing.Count -gt 0) {
        return $missing.ToArray()
    }

    foreach ($name in $ReleaseDocuments) {
        $destination = Join-Path $PublishDir $name
        $folder = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $folder -Force
        }
        Copy-Item -LiteralPath (Join-Path $RepoRoot $name) -Destination $destination -Force
        Write-Host ('Copied {0}' -f $name)
    }
}

# Zips the folder so that it extracts to one RamQuick-win-x64 folder. Every entry name starts
# with that folder's name and uses forward slashes, as the ZIP format requires.
# Not ZipFile.CreateFromDirectory: on the .NET Framework under Windows PowerShell 5.1 it writes
# backslash separators unless the process-wide AppContext switch
# Switch.System.IO.Compression.ZipFile.UseBackslash was set to false before ZipFile first read
# it, which a script cannot guarantee in a session that already used ZipFile. Not
# Compress-Archive either: it is slow in 5.1 and mishandles some files.
function Compress-PortableFolder {
    # ZipArchive and CompressionLevel live in System.IO.Compression, CreateEntryFromFile in its
    # FileSystem companion.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    $baseName = Split-Path -Leaf $PublishDir
    $rootLength = $PublishDir.TrimEnd('\').Length + 1
    $level = [System.IO.Compression.CompressionLevel]::Optimal

    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive -ArgumentList $stream, ([System.IO.Compression.ZipArchiveMode]::Create)
        try {
            # Sorted, so the entry order is the same on every run.
            foreach ($item in @(Get-ChildItem -LiteralPath $PublishDir -Recurse -Force | Sort-Object -Property FullName)) {
                $entryName = $baseName + '/' + $item.FullName.Substring($rootLength).Replace('\', '/')
                if ($item.PSIsContainer) {
                    # A folder is implied by its files' entries; only an empty one needs its own.
                    if (@(Get-ChildItem -LiteralPath $item.FullName -Force).Count -eq 0) {
                        $null = $archive.CreateEntry($entryName + '/')
                    }
                }
                else {
                    $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $entryName, $level)
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    Write-Host ('Created {0}' -f $ZipPath)
}

function Write-PublishSummary {
    $files = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force)
    $folderBytes = [double]($files | Measure-Object -Property Length -Sum).Sum
    $zip = Get-Item -LiteralPath $ZipPath
    $hash = Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256

    Write-Section 'Summary'
    Write-Host ('  Folder  : {0}' -f $PublishDir)
    Write-Host ('  Files   : {0}, {1} MB' -f $files.Count, (Get-MegabyteText -Bytes $folderBytes))
    Write-Host ('  Zip     : {0}' -f $ZipPath)
    Write-Host ('  Size    : {0} MB ({1} bytes)' -f (Get-MegabyteText -Bytes ([double]$zip.Length)), $zip.Length)
    Write-Host ('  SHA256  : {0}' -f $hash.Hash)
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

function Invoke-PortableRelease {
    try {
        Assert-Platform
        $tools = Find-VisualStudio
    }
    catch {
        Write-Host ('publish-portable: preflight failed: {0}' -f $_.Exception.Message) -ForegroundColor Red
        $script:ExitCode = 2
        return
    }
    Write-ToolchainBanner -Tools $tools

    Write-Section 'Clean'
    Invoke-PublishOutputReset

    Write-Section 'Publish'
    $code = Invoke-PortablePublish -Tools $tools
    if ($code -ne 0) {
        Write-Host ('publish-portable: publish failed (MSBuild exit code {0}).' -f $code) -ForegroundColor Red
        $script:ExitCode = 1
        return
    }

    Write-Section 'Verify'
    $violations = @(Test-PublishOutput -Tools $tools)
    if ($violations.Count -gt 0) {
        foreach ($violation in $violations) {
            Write-Host ('  FAIL {0}' -f $violation) -ForegroundColor Red
        }
        Write-Host ('publish-portable: verification failed: {0} violation(s) in {1}.' -f $violations.Count, $PublishDir) -ForegroundColor Red
        $script:ExitCode = 1
        return
    }
    Write-Host ('Verified: {0} required files present, no test-only item, statically linked C runtime.' -f $RequiredRootFiles.Count)

    Write-Section 'Package'
    $missing = @(Copy-ReleaseDocument)
    if ($missing.Count -gt 0) {
        Write-Host ('publish-portable: packaging failed: missing {0}.' -f ($missing -join ', ')) -ForegroundColor Red
        $script:ExitCode = 1
        return
    }
    Compress-PortableFolder
    Write-PublishSummary

    Write-Host ''
    Write-Host ('publish-portable: done: {0}' -f $ZipPath) -ForegroundColor Green
    $script:ExitCode = 0
}

$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $RepoRoot
    # Stray pipeline output is printed rather than lost; the result travels through ExitCode.
    Invoke-PortableRelease | Out-String -Stream | ForEach-Object { Write-Host $_.TrimEnd() }
}
catch {
    # The stack trace comes first so that the "publish-portable:" line stays the last line.
    Write-Host $_.ScriptStackTrace
    Write-Host ('publish-portable: unexpected error: {0}' -f $_.Exception.Message) -ForegroundColor Red
    $script:ExitCode = 1
}
finally {
    Set-Location -LiteralPath $originalLocation.Path -ErrorAction SilentlyContinue
}
exit $script:ExitCode
