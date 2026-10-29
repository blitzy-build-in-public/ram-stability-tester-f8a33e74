<#
.SYNOPSIS
    Scans the source tree for forbidden API use and fails when any is found.

.DESCRIPTION
    A source-level gate run by scripts/build.ps1. It reads every C# and C/C++
    source file under -SourceRoot (by default the repository's src/ folder) and
    reports each use of an API that RAMQuick must not call:

      Networking           RAMQuick initiates no network activity.
      SystemConfiguration  No registry writes, no privilege assignment and no
                           service or driver installation.
      NativeCalls          Native-call declarations (P/Invoke, NativeLibrary,
                           unmanaged function pointers, COM interface imports)
                           appear only under src/RamQuick.Interop/ and
                           src/RamQuick.Sensors/, so the UI and the other
                           libraries never touch memory or hardware directly.

    Matching is case-sensitive and runs on code text only. Comments are removed
    first; string and character literal contents are kept, because an API name
    inside a string is still suspicious. Files below a directory named bin, obj
    or .vs (compiler output, generated sources, IDE state) are not scanned.

    Each hit is written to the success output stream as one line:
      <path>:<line>: [<Group>/<Rule>] <matched text> -- <source line>
    followed by a summary line. Every hit in every file is reported.

    The script only reads files. It writes nothing to disk and makes no network
    or registry access. It is a static scan: it cannot vouch for the .NET runtime
    or third-party binaries, whose runtime behaviour is checked manually with a
    Process Monitor capture.

.PARAMETER SourceRoot
    Directory to scan. Defaults to the src/ folder of the repository that holds
    this script. Paths are reported relative to the repository root when
    SourceRoot lies inside the repository, and relative to SourceRoot's parent
    otherwise, so a fixture tree <dir>\src\RamQuick.Interop\... is classified
    exactly like the real src/RamQuick.Interop/.

.EXAMPLE
    .\scripts\check-constraints.ps1

    Scans src/ and exits 0 when no forbidden API use is found.

.EXAMPLE
    .\scripts\check-constraints.ps1 -SourceRoot C:\work\fixture\src

    Scans a fixture tree outside the repository.

.NOTES
    Exit codes: 0 = no forbidden API use; 1 = one or more forbidden uses;
    2 = the source root does not exist or the scan itself failed.

    Callers run `& .\scripts\check-constraints.ps1` and read $LASTEXITCODE; the
    script never throws on a hit. Hit lines go to the success stream, so a caller
    that captures that stream must print it for the hits to be seen.

    There is deliberately no suppression mechanism. The only exemption is the
    two native-layer folders for the NativeCalls group; a legitimate exception
    needs a deliberate edit of the rule table below.

    Compatible with Windows PowerShell 5.1. The source is ASCII-only because
    Windows PowerShell reads a BOM-less script as ANSI.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The default is applied here rather than in param(): Windows PowerShell 5.1 leaves
# $PSScriptRoot empty in parameter defaults when a script runs via powershell -File.
if (-not $PSBoundParameters.ContainsKey('SourceRoot')) {
    $SourceRoot = Join-Path (Join-Path $PSScriptRoot '..') 'src'
}

# Ordered rule table. Pattern is a case-sensitive .NET regular expression matched
# against code text (comments removed, literal contents kept). AppliesTo is
#   All                         every scanned file;
#   ManagedOutsideNativeLayers  .cs files whose repository-relative path does not
#                               start with one of $NativeLayerPrefixes.
# Within each group, the rules after a "Beyond the named APIs" comment go past the
# API names this gate was specified with. They are deliberate: they enforce the same
# prohibitions, and without them TcpListener, WebClient, or CreateSubKey followed by
# RegistryKey.SetValue would pass a gate meant to reject them. Keep them.
$ForbiddenApiRules = @(
    # Networking: the product initiates no network activity (AAP 0.12.3, 0.3.3).
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpClient'; Pattern = '\bHttpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebRequest'; Pattern = '\w*WebRequest\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'Socket'; Pattern = '\bSockets?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'TcpClient'; Pattern = '\bTcpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'UdpClient'; Pattern = '\bUdpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'Dns'; Pattern = '\bDns\b'; AppliesTo = 'All' }
    # Native equivalents, for the C++ engine under src/engine.
    [pscustomobject]@{ Group = 'Networking'; Name = 'WSAStartup'; Pattern = '\bWSAStartup\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WinHttp'; Pattern = '\bWinHttp\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'InternetOpen'; Pattern = '\bInternetOpen\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'URLDownloadToFile'; Pattern = '\bURLDownloadToFile\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'SocketCall'; Pattern = '\bsocket\s*\('; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'NetworkHeader'; Pattern = '(?i)#\s*include\s*[<"]\s*(winsock2?|ws2tcpip|winhttp|wininet|urlmon)\.h'; AppliesTo = 'All' }
    # Beyond the named APIs: listeners, legacy and WebSocket clients, mail, raw streams.
    [pscustomobject]@{ Group = 'Networking'; Name = 'TcpListener'; Pattern = '\bTcpListener\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpListener'; Pattern = '\bHttpListener\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebClient'; Pattern = '\bWebClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebSocket'; Pattern = '\w*WebSocket\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'SmtpClient'; Pattern = '\bSmtpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'NetworkStream'; Pattern = '\bNetworkStream\b'; AppliesTo = 'All' }

    # System configuration: no registry writes, privilege assignment, or service or
    # driver installation (AAP 0.12.2, 0.12.3, 0.3.3 security architecture).
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LsaAddAccountRights'; Pattern = '\bLsaAddAccountRights\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'CreateService'; Pattern = '\bCreateService[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegSetValue'; Pattern = '\bRegSetValue\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegSetKeyValue'; Pattern = '\bRegSetKeyValue\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistrySetValue'; Pattern = '\bRegistry\s*\.\s*SetValue\b'; AppliesTo = 'All' }
    # Beyond the named APIs: rights removal, service changes, driver loading and
    # installation, and the other native and managed registry-write paths.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LsaRemoveAccountRights'; Pattern = '\bLsaRemoveAccountRights\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'ChangeServiceConfig'; Pattern = '\bChangeServiceConfig2?[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'DeleteService'; Pattern = '\bDeleteService\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LoadDriver'; Pattern = '\b(Nt|Zw)LoadDriver\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'DriverInstall'; Pattern = '\b(DiInstallDriver|UpdateDriverForPlugAndPlayDevices|SetupCopyOEMInf)[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegCreateKey'; Pattern = '\bRegCreateKey\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegDelete'; Pattern = '\bRegDelete\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryKeyWrite'; Pattern = '\b(CreateSubKey|DeleteSubKey|DeleteSubKeyTree|DeleteValue)\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryWritableOpen'; Pattern = '\bOpenSubKey\s*\([^;]*,\s*(writable\s*:\s*)?true\b|\bReadWriteSubTree\b'; AppliesTo = 'All' }

    # Native calls only in the Interop and Sensors layers (AAP 0.3.3 layering,
    # 0.12.3 "native-call declarations outside Interop and Sensors").
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'PInvoke'; Pattern = '\b(DllImport|LibraryImport)(Attribute)?\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'NativeLibrary'; Pattern = '\bNativeLibrary\s*\.\s*(Load|TryLoad|GetExport|TryGetExport|SetDllImportResolver)\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'UnmanagedFunctionPointer'; Pattern = '\bdelegate\s*\*\s*unmanaged\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    # Beyond the named APIs: COM interface declarations, the remaining way managed
    # code declares calls into native code.
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'ComImport'; Pattern = '\b(ComImport|GeneratedComInterface)(Attribute)?\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
)

# Repository-relative folders (forward slashes) where native-call declarations belong.
$NativeLayerPrefixes = @('src/RamQuick.Interop/', 'src/RamQuick.Sensors/')

# C# and C/C++ sources. Besides the .cs .cpp .h .hpp .c .inl files the project uses,
# the other C++ spellings are scanned, so renaming a file cannot take it out of the gate.
$ScannedExtensions = @('.cs', '.c', '.cc', '.cpp', '.cxx', '.h', '.hpp', '.hxx', '.inl', '.ixx')

# Directory names whose contents are build output, generated code or IDE state.
$ExcludedDirectoryNames = @('bin', 'obj', '.vs')

# Longest matched text and source context printed per hit.
$MaxContextLength = 160

function Get-NormalizedPath {
    # Full path without a trailing separator (a drive root keeps its separator).
    param([string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) {
        $full = $full.TrimEnd([char[]]@('\', '/'))
    }
    return $full
}

function Get-RelativePath {
    # $FullName must lie below $BasePath. Returns a forward-slash relative path.
    param([string]$FullName, [string]$BasePath)

    return $FullName.Substring($BasePath.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
}

function Get-DisplayBase {
    # The repository root when the scanned tree lies inside the repository, otherwise
    # the scanned tree's parent, so that "src/..." prefixes classify fixture trees too.
    param([string]$SourceRootPath, [string]$RepoRoot)

    $comparison = [System.StringComparison]::OrdinalIgnoreCase
    $isRepoRoot = $SourceRootPath.Equals($RepoRoot, $comparison)
    $isInsideRepo = $SourceRootPath.StartsWith($RepoRoot.TrimEnd('\') + '\', $comparison)
    if ($isRepoRoot -or $isInsideRepo) {
        return $RepoRoot
    }
    $parent = [System.IO.Path]::GetDirectoryName($SourceRootPath)
    if ([string]::IsNullOrEmpty($parent)) {
        return $SourceRootPath
    }
    return $parent
}

function Test-ExcludedPath {
    # True when a directory segment of the source-root-relative path is excluded. Only
    # segments below the source root count, so the checkout's own location cannot
    # exclude everything.
    param([string]$RelativePath)

    $segments = $RelativePath.Split('/')
    for ($s = 0; $s -lt $segments.Length - 1; $s++) {
        if ($ExcludedDirectoryNames -contains $segments[$s]) {
            return $true
        }
    }
    return $false
}

function Test-NativeLayerPath {
    param([string]$DisplayPath)

    foreach ($prefix in $NativeLayerPrefixes) {
        if ($DisplayPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Test-RuleScope {
    # True when $Rule applies to a file with the given language and location.
    param([psobject]$Rule, [bool]$IsManaged, [bool]$InNativeLayer)

    if ($Rule.AppliesTo -eq 'All') {
        return $true
    }
    return ($IsManaged -and -not $InNativeLayer)
}

function Get-LiteralEnd {
    # $Start indexes the opening quote of a regular string or character literal.
    # Returns the index just past the closing quote, honouring backslash escapes, or
    # the line length when the literal is unterminated on this line.
    param([string]$Line, [int]$Start, [char]$Quote)

    $stops = [char[]]@([char]'\', $Quote)
    $length = $Line.Length
    $j = $Start + 1
    while ($j -lt $length) {
        $k = $Line.IndexOfAny($stops, $j)
        if ($k -lt 0) {
            return $length
        }
        if ($Line[$k] -eq [char]'\') {
            $j = $k + 2
            continue
        }
        return $k + 1
    }
    return $length
}

function Get-VerbatimStringEnd {
    # Inside a C# verbatim string ("" is an escaped quote): returns the index just past
    # the closing quote, or -1 when the string continues on the next line.
    param([string]$Line, [int]$Start)

    $j = $Start
    while ($true) {
        $q = $Line.IndexOf([char]'"', $j)
        if ($q -lt 0) {
            return -1
        }
        if ($q + 1 -lt $Line.Length -and $Line[$q + 1] -eq [char]'"') {
            $j = $q + 2
            continue
        }
        return $q + 1
    }
}

function Test-VerbatimStringStart {
    # C# verbatim strings open with @" , $@" or @$" .
    param([string]$Line, [int]$QuoteIndex)

    if ($QuoteIndex -ge 1 -and $Line[$QuoteIndex - 1] -eq [char]'@') {
        return $true
    }
    if ($QuoteIndex -ge 2 -and $Line[$QuoteIndex - 1] -eq [char]'$' -and $Line[$QuoteIndex - 2] -eq [char]'@') {
        return $true
    }
    return $false
}

function Test-DigitSeparator {
    # A C++14 digit separator (1'000'000, 0xFFFF'FFFF) continues a token that starts
    # with a digit; an apostrophe after an identifier (L'x', u8'x') or punctuation
    # opens a character literal.
    param([string]$Line, [int]$Index)

    $start = $Index
    while ($start -gt 0) {
        $previous = $Line[$start - 1]
        if ([char]::IsLetterOrDigit($previous) -or $previous -eq [char]'_' -or $previous -eq [char]'.') {
            $start--
        }
        else {
            break
        }
    }
    return ($start -lt $Index -and [char]::IsDigit($Line[$start]))
}

function Get-CodeText {
    # Returns, for each source line, its code text: comments removed (a block comment
    # becomes one space, as the compilers treat it), string and character literal
    # contents kept. Lines that hold only comments or whitespace yield ''. Block
    # comments and C# verbatim strings carry their state from line to line.
    # Known limitations, all confined to where a comment is taken to start or end:
    # C# raw string literals (""") and C++ raw strings (R"(...)") are lexed as
    # ordinary strings, quotes nested inside C# interpolation holes end the string
    # early, and a backslash-continued // comment ends at its own line.
    param([string[]]$Lines)

    $codeLines = New-Object 'string[]' $Lines.Length
    $codeStarts = [char[]]@([char]'/', [char]'"', [char]"'")
    $builder = New-Object System.Text.StringBuilder
    $inBlockComment = $false
    $inVerbatimString = $false

    for ($n = 0; $n -lt $Lines.Length; $n++) {
        $line = $Lines[$n]
        $length = $line.Length
        [void]$builder.Clear()
        $i = 0
        while ($i -lt $length) {
            if ($inBlockComment) {
                $close = $line.IndexOf('*/', $i, [System.StringComparison]::Ordinal)
                if ($close -lt 0) {
                    break
                }
                $inBlockComment = $false
                $i = $close + 2
                continue
            }
            if ($inVerbatimString) {
                $end = Get-VerbatimStringEnd -Line $line -Start $i
                if ($end -lt 0) {
                    [void]$builder.Append($line, $i, $length - $i)
                    break
                }
                [void]$builder.Append($line, $i, $end - $i)
                $inVerbatimString = $false
                $i = $end
                continue
            }

            $next = $line.IndexOfAny($codeStarts, $i)
            if ($next -lt 0) {
                [void]$builder.Append($line, $i, $length - $i)
                break
            }
            [void]$builder.Append($line, $i, $next - $i)
            $current = $line[$next]

            if ($current -eq [char]'/') {
                $following = [char]0
                if ($next + 1 -lt $length) {
                    $following = $line[$next + 1]
                }
                if ($following -eq [char]'/') {
                    break
                }
                if ($following -eq [char]'*') {
                    [void]$builder.Append(' ')
                    $inBlockComment = $true
                    $i = $next + 2
                    continue
                }
                [void]$builder.Append('/')
                $i = $next + 1
                continue
            }

            if ($current -eq [char]'"' -and (Test-VerbatimStringStart -Line $line -QuoteIndex $next)) {
                [void]$builder.Append('"')
                $inVerbatimString = $true
                $i = $next + 1
                continue
            }

            if ($current -eq [char]"'" -and (Test-DigitSeparator -Line $line -Index $next)) {
                [void]$builder.Append("'")
                $i = $next + 1
                continue
            }

            $end = Get-LiteralEnd -Line $line -Start $next -Quote $current
            [void]$builder.Append($line, $next, $end - $next)
            $i = $end
        }

        $code = $builder.ToString()
        if ($code.Trim().Length -eq 0) {
            $code = ''
        }
        $codeLines[$n] = $code
    }
    return , $codeLines
}

function Get-TruncatedText {
    param([string]$Text)

    if ($Text.Length -le $MaxContextLength) {
        return $Text
    }
    return $Text.Substring(0, $MaxContextLength - 3) + '...'
}

function Format-HitLine {
    param([string]$DisplayPath, [int]$LineNumber, [psobject]$Rule, [string]$MatchedText, [string]$SourceLine)

    return ('{0}:{1}: [{2}/{3}] {4} -- {5}' -f $DisplayPath, $LineNumber, $Rule.Group, $Rule.Name,
        (Get-TruncatedText -Text $MatchedText), (Get-TruncatedText -Text $SourceLine.Trim()))
}


try {
    if ([string]::IsNullOrWhiteSpace($SourceRoot) -or -not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        Write-Error -Message ("check-constraints: source root not found: '{0}'." -f $SourceRoot) -ErrorAction Continue
        exit 2
    }

    $repoRoot = Get-NormalizedPath -Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $sourceRootPath = Get-NormalizedPath -Path (Resolve-Path -LiteralPath $SourceRoot).ProviderPath
    $displayBase = Get-DisplayBase -SourceRootPath $sourceRootPath -RepoRoot $repoRoot

    $knownScopes = @('All', 'ManagedOutsideNativeLayers')
    $compiledRules = @(
        foreach ($rule in $ForbiddenApiRules) {
            if ($knownScopes -notcontains $rule.AppliesTo) {
                throw ("Rule {0}/{1} has unknown AppliesTo '{2}'." -f $rule.Group, $rule.Name, $rule.AppliesTo)
            }
            [pscustomobject]@{
                Group     = $rule.Group
                Name      = $rule.Name
                AppliesTo = $rule.AppliesTo
                Regex     = [System.Text.RegularExpressions.Regex]::new(
                    $rule.Pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
            }
        }
    )

    # -Force includes hidden files, so a hidden source file cannot bypass the gate.
    $sourceFiles = @(
        Get-ChildItem -LiteralPath $sourceRootPath -Recurse -File -Force |
            Where-Object {
                ($ScannedExtensions -contains $_.Extension) -and
                -not (Test-ExcludedPath -RelativePath (Get-RelativePath -FullName $_.FullName -BasePath $sourceRootPath))
            } |
            Sort-Object -Property FullName
    )

    $hitCount = 0
    $filesWithHits = 0
    foreach ($file in $sourceFiles) {
        $displayPath = Get-RelativePath -FullName $file.FullName -BasePath $displayBase
        $isManaged = $file.Extension -eq '.cs'
        $inNativeLayer = Test-NativeLayerPath -DisplayPath $displayPath
        $applicableRules = @(
            foreach ($rule in $compiledRules) {
                if (Test-RuleScope -Rule $rule -IsManaged $isManaged -InNativeLayer $inNativeLayer) {
                    $rule
                }
            }
        )

        $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
        $codeLines = Get-CodeText -Lines $lines
        $fileHits = 0
        for ($n = 0; $n -lt $codeLines.Length; $n++) {
            $code = $codeLines[$n]
            if ($code.Length -eq 0) {
                continue
            }
            foreach ($rule in $applicableRules) {
                foreach ($match in $rule.Regex.Matches($code)) {
                    $fileHits++
                    $hitLine = Format-HitLine -DisplayPath $displayPath -LineNumber ($n + 1) -Rule $rule -MatchedText $match.Value -SourceLine $lines[$n]
                    Write-Output $hitLine
                }
            }
        }
        if ($fileHits -gt 0) {
            $hitCount += $fileHits
            $filesWithHits++
        }
    }

    if ($hitCount -gt 0) {
        Write-Output ('check-constraints: {0} forbidden API use(s) in {1} file(s) (scanned {2} files).' -f $hitCount, $filesWithHits, $sourceFiles.Count)
        exit 1
    }
    Write-Output ('check-constraints: no forbidden API use found (scanned {0} files).' -f $sourceFiles.Count)
    exit 0
}
catch {
    Write-Error -Message ('check-constraints: scan failed: {0}' -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}

