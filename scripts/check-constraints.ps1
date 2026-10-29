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
      NativeCalls          Native-call declarations (P/Invoke, unmanaged function
                           pointers, COM interface imports) and any use of
                           NativeLibrary appear only under src/RamQuick.Interop/
                           and src/RamQuick.Sensors/, so the UI and the other
                           libraries never touch memory or hardware directly.

    Matching is case-sensitive and runs over each file's code text as a whole:
    comments are removed first and line breaks kept, so a call split over
    several lines is still found, and it is reported at the line where it
    starts. String and character literal contents are kept, because an API
    name inside a string is still suspicious. A block comment that is never
    closed fails the scan (exit 2), because it would hide the rest of its file.

    Files below a .vs directory (IDE state), or below a bin or obj directory
    beside a project file (that project's build output and generated sources),
    are not scanned; a bin or obj directory anywhere else is scanned, because the
    SDK compiles it. Junctions and directory symbolic links are followed, and a
    reparse point the scan cannot follow fails the scan (exit 2).

    Each hit is written to the success output stream as one line:
      <path>:<line>: [<Group>/<Rule>] <matched text> -- <source line>
    where <line> and <source line> are those of the line the match starts on,
    and a matched text that spans lines has each whitespace run collapsed to
    one space. A summary line follows. Every hit in every file is reported.

    The script only reads files. It writes nothing to disk and makes no network
    or registry access. It is a static scan: it cannot vouch for the .NET runtime
    or third-party binaries, whose runtime behaviour is checked manually with a
    Process Monitor capture.

.PARAMETER Arguments
    The whole command line, which the script parses itself. Accepted syntax:

      check-constraints.ps1 [[-SourceRoot] <directory>]

    -SourceRoot <directory> names the directory to scan. The name is spelled in
    full, in any letter case, and may end with a colon (-SourceRoot:<directory>).
    The directory may instead be given as the only positional value; without
    one, the src/ folder of the repository that holds this script is scanned. A
    directory whose name starts with '-' is written as .\<name>. Paths are
    reported relative to the repository root when the directory lies inside the
    repository, and relative to the directory's parent otherwise, so a fixture
    tree <dir>\src\RamQuick.Interop\... is classified exactly like the real
    src/RamQuick.Interop/.

    Anything else exits 2: another parameter such as -Help, -SourceRoot without
    a value, a second directory, or an in-process list of directories. An
    in-process $null is a blank source root and also exits 2.

    The script parses its own arguments because PowerShell's binder would reject
    a malformed command line before any script code runs, and powershell.exe
    -File would then exit 1, the code for forbidden API use.

.EXAMPLE
    .\scripts\check-constraints.ps1

    Scans src/ and exits 0 when no forbidden API use is found.

.EXAMPLE
    .\scripts\check-constraints.ps1 -SourceRoot C:\work\fixture\src

    Scans a fixture tree outside the repository.

.NOTES
    Exit codes: 0 = no forbidden API use; 1 = one or more forbidden uses;
    2 = a malformed command line (an unknown parameter, -SourceRoot without a
    value, an extra value), a source root that does not exist, or a scan that
    failed.

    The script ends every exit 0 and exit 1 with a "check-constraints:" summary
    line on the success stream, and exit 1 means forbidden API use only together
    with the "check-constraints: <N> forbidden API use(s) ..." line. Every exit 2
    the script takes writes a "check-constraints:" error.

    A few command lines still never reach the script, because PowerShell binds
    the common parameters of [CmdletBinding()] and the -Arguments parameter
    itself (also through a prefix such as -A); -Arguments <directory> then acts
    as the positional directory. -ErrorAction or -Arguments with no value, or
    -Arguments followed by two values, fails binding:
    powershell.exe -File then exits 1 with no summary line, and an in-process
    call raises a ParameterBindingException that leaves $LASTEXITCODE unchanged
    and stops the caller only under $ErrorActionPreference = 'Stop'.
    powershell.exe -File also exits 0 for -? without printing anything. Callers
    such as scripts/build.ps1 must therefore require the summary line, not only
    the exit code.

    powershell.exe -File drops "-SourceRoot:" when nothing, or an empty quoted
    string, follows the colon, so the script never sees it and scans the default
    root. In a PowerShell command line, "-SourceRoot:" with nothing after it is a
    parse error in the caller.

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
    # The only declared parameter, so that every argument reaches the script and is
    # parsed below. A declared -SourceRoot would let PowerShell reject a malformed
    # command line (no value, given twice, an extra positional value) before any
    # script code runs, and powershell.exe -File then exits 1, the forbidden-API code.
    # object[] rather than string[] keeps an in-process $null or list value intact,
    # so it can be told apart from a path.
    [Parameter(ValueFromRemainingArguments = $true)]
    [object[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-ParameterNameToken {
    param([object]$Token)

    return ($Token -is [string]) -and $Token.StartsWith('-')
}

# Parses [[-SourceRoot] <directory>] from the catch-all's tokens. Returns Root (the
# given directory, or $null when none is given) and Problem (why the command line is
# malformed, or $null).
function Get-SourceRootArgument {
    param([object[]]$Token)

    $root = $null
    $index = 0
    while ($index -lt $Token.Count) {
        $current = $Token[$index]
        $index++
        $isName = Test-ParameterNameToken -Token $current
        if ($isName -and $current -notmatch '^-SourceRoot:?$') {
            return [pscustomobject]@{ Root = $null; Problem = "unexpected argument '$current'" }
        }
        if ($null -ne $root) {
            return [pscustomobject]@{ Root = $null; Problem = "unexpected argument '$current': the source root is already given" }
        }
        $value = $current
        if ($isName) {
            # An in-process -SourceRoot:<value> keeps its colon and always carries its value,
            # even one that starts with '-'. Without the colon, a following '-' token is the
            # next parameter, as PowerShell's own binder reads it.
            $hasValue = $index -lt $Token.Count
            if ($hasValue -and -not $current.EndsWith(':')) {
                $hasValue = -not (Test-ParameterNameToken -Token $Token[$index])
            }
            if (-not $hasValue) {
                return [pscustomobject]@{ Root = $null; Problem = '-SourceRoot requires a directory' }
            }
            $value = $Token[$index]
            $index++
        }
        if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
            return [pscustomobject]@{ Root = $null; Problem = 'the source root must be one directory, not a list' }
        }
        # An in-process $null becomes '', which the scan rejects as a blank source root.
        $root = [string]$value
    }
    return [pscustomobject]@{ Root = $root; Problem = $null }
}

# An unbound catch-all is $null, which @() would turn into one $null token.
$argumentTokens = @()
if ($PSBoundParameters.ContainsKey('Arguments')) {
    $argumentTokens = @($Arguments)
}
$parsedArguments = Get-SourceRootArgument -Token $argumentTokens
if ($null -ne $parsedArguments.Problem) {
    Write-Error -Message ('check-constraints: {0}; usage: check-constraints.ps1 [[-SourceRoot] <directory>].' -f $parsedArguments.Problem) -ErrorAction Continue
    exit 2
}
$SourceRoot = $parsedArguments.Root

# The default is applied here rather than as a parameter default: Windows PowerShell 5.1
# leaves $PSScriptRoot empty in parameter defaults when a script runs via powershell -File.
if ($null -eq $SourceRoot) {
    $SourceRoot = Join-Path (Join-Path $PSScriptRoot '..') 'src'
}

# Ordered rule table. Pattern is a .NET regular expression, case-sensitive unless it
# opens with (?i), matched against code text (comments removed, literal contents
# kept). AppliesTo is
#   All                         every scanned file;
#   ManagedOutsideNativeLayers  .cs files whose repository-relative path does not
#                               start with one of $NativeLayerPrefixes.
# A pattern names whole API spellings (an enumerated family, or a fixed prefix plus
# the A/W, Ex and similar suffixes the API really has), never an open-ended prefix or
# suffix, so an unrelated identifier such as RegDeleted or InternetOpenness passes.
# A pattern must also match the same way over one line or over a whole file's code
# text with its line breaks kept: no ^ or $ anchors, every [^x]* run also stops at
# ';', no repeated (* or +) group that holds a quantifier of its own unless the
# repetition is atomic, (?>...), so it cannot backtrack (an optional group may), and
# no unbounded quantifier ahead of the first literal or \b other than inside a
# lookbehind, so matching stays linear in the text length.
# Within each group, the rules after a "Beyond the named APIs" comment go past the
# API names this gate was specified with. They are deliberate: they enforce the same
# prohibitions, and without them TcpListener, WebClient, or CreateSubKey followed by
# RegistryKey.SetValue would pass a gate meant to reject them. Keep them.
$ForbiddenApiRules = @(
    # Networking: the product initiates no network activity (AAP 0.12.3, 0.3.3).
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpClient'; Pattern = '\bHttpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebRequest'; Pattern = '\b(WebRequest(Methods)?|HttpWebRequest|FtpWebRequest|FileWebRequest|IWebRequestCreate|PackWebRequest(Factory)?)\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'Socket'; Pattern = '\bSockets?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'TcpClient'; Pattern = '\bTcpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'UdpClient'; Pattern = '\bUdpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'Dns'; Pattern = '\bDns\b'; AppliesTo = 'All' }
    # Native equivalents, for the C++ engine under src/engine.
    [pscustomobject]@{ Group = 'Networking'; Name = 'WSAStartup'; Pattern = '\bWSAStartup\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WinHttp'; Pattern = '\bWinHttp[A-Z]\w*'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'InternetOpen'; Pattern = '\bInternetOpen(Url)?[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'URLDownloadToFile'; Pattern = '\bURLDownloadToFile\w*'; AppliesTo = 'All' }
    # Winsock socket(af, type, protocol): a call is told apart by the comma after its
    # first argument, which real code wraps in casts and parentheses to any depth and
    # across lines, so only a comma outside every parenthesis counts. A member socket()
    # reached through '.', '->' or 'X::' is the product's own method and passes; the
    # global '::socket(' still counts. A declaration passes because a call's first
    # argument is never 'type name' ('int socket(int a, int b);') or a bare type
    # keyword ('int socket(int, int, int);'), and an empty list or prose such as
    # "cores per socket (logical)" has no such comma. The argument run is atomic and
    # ends at the next socket token that could start a call of its own, which is then
    # judged by itself; a run that crossed such tokens would rescan the same text once
    # per token, so a file of unclosed 'socket(' would take quadratic time.
    [pscustomobject]@{ Group = 'Networking'; Name = 'SocketCall'; Pattern = '(?<!(\.|->|[\w>]::)\s*)\bsocket\s*\(\s*(?=[^\s,)])(?!\w+\s+\w|(bool|char|char8_t|char16_t|char32_t|wchar_t|short|int|long|signed|unsigned|float|double|void|const|volatile|struct|class|enum)[\s*&]*[,)])(?>(?:(?!(?<!\.|->|[\w>]::)\bsocket[\s(])[^(),;]|(?<p>\()|(?<-p>\))|(?(p),|(?!)))*)(?(p)(?!)),'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'NetworkHeader'; Pattern = '(?i)#\s*include\s*[<"]\s*(winsock2?|ws2tcpip|winhttp|wininet|urlmon)\.h'; AppliesTo = 'All' }
    # Beyond the named APIs: listeners, legacy and WebSocket clients, mail, raw streams,
    # HTTP message handlers and ICMP.
    [pscustomobject]@{ Group = 'Networking'; Name = 'TcpListener'; Pattern = '\bTcpListener\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpListener'; Pattern = '\bHttpListener\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebClient'; Pattern = '\bWebClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'WebSocket'; Pattern = '\b(Client|HttpListener|Value)?WebSocket(s|Options|CloseStatus|Context|CreationOptions|DeflateOptions|Error|Exception|MessageFlags|MessageType|ReceiveResult|State|AbortHandle|BeginClientHandshake|BeginServerHandshake|CompleteAction|CreateClientHandle|CreateServerHandle|DeleteHandle|EndClientHandshake|EndServerHandshake|GetAction|GetGlobalProperty|Receive|Send)?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'SmtpClient'; Pattern = '\bSmtpClient\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'NetworkStream'; Pattern = '\bNetworkStream\b'; AppliesTo = 'All' }
    # HTTP without the HttpClient name (AAP 0.12.3, no network activity): HttpClient's
    # base class HttpMessageInvoker sends requests itself through HttpClientHandler or
    # SocketsHttpHandler. WinHttpHandler is caught by the WinHttp rule above.
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpMessageInvoker'; Pattern = '\bHttpMessageInvoker\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'HttpHandler'; Pattern = '\b(HttpClientHandler|SocketsHttpHandler)\b'; AppliesTo = 'All' }
    # ICMP echo (AAP 0.12.3): System.Net.NetworkInformation, home of Ping, wherever a
    # directive or qualified name spells it; a Ping constructed under such a using
    # directive; and the native IcmpSendEcho family. A member or field of the product
    # that is merely named Ping does not count.
    [pscustomobject]@{ Group = 'Networking'; Name = 'NetworkInformation'; Pattern = '\bNetworkInformation\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'Ping'; Pattern = '\bnew\s+Ping\s*[({]'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'Networking'; Name = 'IcmpSendEcho'; Pattern = '\bIcmp6?(CreateFile|SendEcho2?(Ex)?)\b'; AppliesTo = 'All' }

    # System configuration: no registry writes, privilege assignment, or service or
    # driver installation (AAP 0.12.2, 0.12.3, 0.3.3 security architecture).
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LsaAddAccountRights'; Pattern = '\bLsaAddAccountRights\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'CreateService'; Pattern = '\bCreateService[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegSetValue'; Pattern = '\bRegSetValue(Ex)?[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegSetKeyValue'; Pattern = '\bRegSetKeyValue[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistrySetValue'; Pattern = '\bRegistry\s*\.\s*SetValue\b'; AppliesTo = 'All' }
    # Beyond the named APIs: rights removal, service changes, driver loading and
    # installation, and the other native and managed registry-write paths.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LsaRemoveAccountRights'; Pattern = '\bLsaRemoveAccountRights\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'ChangeServiceConfig'; Pattern = '\bChangeServiceConfig2?[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'DeleteService'; Pattern = '\bDeleteService\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'LoadDriver'; Pattern = '\b(Nt|Zw)LoadDriver\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'DriverInstall'; Pattern = '\b(DiInstallDriver|UpdateDriverForPlugAndPlayDevices|SetupCopyOEMInf)[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegCreateKey'; Pattern = '\bRegCreateKey(Ex)?(Transacted)?[AW]?\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegDelete'; Pattern = '\bRegDelete(Key(Ex|Value|Transacted)?|Value|Tree)[AW]?\b'; AppliesTo = 'All' }
    # RegistryKey write methods as member calls, so a method of the product that
    # happens to share one of these names is not mistaken for one.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryKeyWrite'; Pattern = '\.\s*(CreateSubKey|DeleteSubKey|DeleteSubKeyTree|DeleteValue)\b'; AppliesTo = 'All' }
    # A writable open: true as a later argument, or writable: true in any position, of
    # the same OpenSubKey call. The argument scan skips literals and balanced nested
    # parentheses (the depth group) and stops at the call's own ')' or a ';', so a true
    # in the next call never counts. It also stops at a nested OpenSubKey(, which is
    # judged on its own; otherwise each unclosed OpenSubKey( would rescan the rest of
    # the text. Its groups are atomic, so the repeated groups cannot backtrack.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryWritableOpen'; Pattern = '\bOpenSubKey\s*\(\s*(?:writable\s*:\s*true\b|(?>(?:"(?>[^"\\\r\n;]|\\[^\r\n;])*"|(?:\$@|@\$?)"(?>[^";]|"")*"|''(?>[^''\\\r\n;]|\\[^\r\n;])+''|(?!\bOpenSubKey\s*\()[^(),;]|(?<depth>\()|(?<-depth>\))|,(?(depth)|(?!\s*(?:writable\s*:\s*)?true\b)))*)(?(depth)(?!)),\s*(?:writable\s*:\s*)?true\b)|\bReadWriteSubTree\b'; AppliesTo = 'All' }
    # The other way to open a key for writing (AAP 0.12.2 and 0.12.3, no registry
    # writes): a RegistryRights member that grants write access, as in
    # OpenSubKey(name, RegistryRights.WriteKey). ReadKey and the other read rights pass.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryWriteRights'; Pattern = '\bRegistryRights\s*\.\s*(WriteKey|SetValue|CreateSubKey|CreateLink|Delete|FullControl|ChangePermissions|TakeOwnership)\b'; AppliesTo = 'All' }
    # Registry.SetValue reached without the Registry.SetValue spelling (AAP 0.12.2 and
    # 0.12.3, no registry writes): SetValue on a static hive key, which is writable,
    # and a using alias or using static directive for Microsoft.Win32.Registry, after
    # which Reg.SetValue or a bare SetValue writes the registry. A bare SetValue( is
    # not a rule of its own, because WPF's DependencyObject.SetValue has that spelling.
    # The same directive for RegistryRights would hide its write members from the rule
    # above, so it counts too.
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryHiveSetValue'; Pattern = '\bRegistry\s*\.\s*(ClassesRoot|CurrentConfig|CurrentUser|LocalMachine|PerformanceData|Users)\s*\.\s*SetValue\b'; AppliesTo = 'All' }
    [pscustomobject]@{ Group = 'SystemConfiguration'; Name = 'RegistryUsingAlias'; Pattern = '\busing\s+(static\s+|@?\w+\s*=\s*)(global\s*::\s*)?(Microsoft\s*\.\s*Win32\s*\.\s*Registry|System\s*\.\s*Security\s*\.\s*AccessControl\s*\.\s*RegistryRights)\b'; AppliesTo = 'All' }

    # Native calls only in the Interop and Sensors layers (AAP 0.3.3 layering,
    # 0.12.3 "native-call declarations outside Interop and Sensors").
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'PInvoke'; Pattern = '\b(DllImport|LibraryImport)(Attribute)?\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    # Any use of the NativeLibrary type, not a list of its members: Free and any later
    # member are native access too, and a using alias or using static directive is
    # caught where it names the type.
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'NativeLibrary'; Pattern = '\bNativeLibrary\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'UnmanagedFunctionPointer'; Pattern = '\bdelegate\s*\*\s*unmanaged\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
    # Beyond the named APIs: COM interface declarations, the remaining way managed
    # code declares calls into native code.
    [pscustomobject]@{ Group = 'NativeCalls'; Name = 'ComImport'; Pattern = '\b(ComImport|GeneratedComInterface)(Attribute)?\b'; AppliesTo = 'ManagedOutsideNativeLayers' }
)

# Repository-relative folders (forward slashes) where native-call declarations and
# NativeLibrary use belong.
$NativeLayerPrefixes = @('src/RamQuick.Interop/', 'src/RamQuick.Sensors/')

# C# and C/C++ sources. Besides the .cs .cpp .h .hpp .c .inl files the project uses,
# the other C++ spellings are scanned, so renaming a file cannot take it out of the gate.
$ScannedExtensions = @('.cs', '.c', '.cc', '.cpp', '.cxx', '.h', '.hpp', '.hxx', '.inl', '.ixx')

# Directory names skipped wherever they appear: Visual Studio's IDE state, which the
# SDK never compiles.
$ExcludedDirectoryNames = @('.vs')

# Project build output and generated sources, skipped only directly beside a project
# file, where the SDK puts its bin and obj folders and excludes them from compilation.
# A bin or obj folder anywhere else, such as Helpers\bin\, is compiled like any other
# source folder, so skipping every folder of that name, as a plain name filter would,
# lets a P/Invoke hide there.
$ProjectOutputDirectoryNames = @('bin', 'obj')

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

function Resolve-DirectoryRealPath {
    # The location a directory's contents really come from, used to recognise a link
    # that leads back into the directories being walked. A plain directory is its
    # parent's real location plus its name. A junction or directory symbolic link is
    # its target, a relative target taken from the parent's real location, as Windows
    # resolves it. Any other reparse point, or a link whose target is missing, throws,
    # so the gate fails rather than passing a directory it could not follow.
    param([System.IO.DirectoryInfo]$Directory, [string]$ParentRealPath)

    if (($Directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        return [System.IO.Path]::Combine($ParentRealPath, $Directory.Name)
    }
    $link = Get-Item -LiteralPath $Directory.FullName -Force
    $targets = @($link.Target)
    $isLink = @('Junction', 'SymbolicLink') -contains $link.LinkType
    if (-not $isLink -or $targets.Count -ne 1 -or [string]::IsNullOrWhiteSpace($targets[0])) {
        throw ("cannot resolve the target of reparse point '{0}'." -f $Directory.FullName)
    }
    $target = Get-NormalizedPath -Path ([System.IO.Path]::Combine($ParentRealPath, $targets[0]))
    if (-not [System.IO.Directory]::Exists($target)) {
        throw ("link '{0}' points to '{1}', which does not exist." -f $Directory.FullName, $target)
    }
    return $target
}

function Get-SourceFile {
    # Every file below $RootPath with a scanned extension, hidden and system files
    # included, so no file attribute takes a source out of the gate. The walk follows
    # junctions and directory symbolic links, which the compiler follows and
    # Get-ChildItem -Recurse in Windows PowerShell 5.1 does not, and reports each file
    # under its path through the link, the path its project compiles it from. Each
    # directory carries the real locations of the directories above it, so a link back
    # to one of them ends the descent instead of looping; its files are scanned there.
    # Only directories below $RootPath are tested against the skipped names, so the
    # checkout's own location cannot exclude everything.
    param([string]$RootPath)

    $pending = New-Object 'System.Collections.Generic.Stack[object]'
    $pending.Push([pscustomobject]@{ Directory = [System.IO.DirectoryInfo]::new($RootPath); RealPaths = @($RootPath) })
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $holdsProject = $false
        foreach ($file in $current.Directory.GetFiles()) {
            # *.csproj, *.vcxproj and every other *.*proj MSBuild project file.
            if ($file.Extension -match '^\.\w*proj$') {
                $holdsProject = $true
            }
            if ($ScannedExtensions -contains $file.Extension) {
                $file
            }
        }
        $realPath = $current.RealPaths[-1]
        foreach ($child in $current.Directory.GetDirectories()) {
            $isProjectOutput = $holdsProject -and ($ProjectOutputDirectoryNames -contains $child.Name)
            if ($isProjectOutput -or ($ExcludedDirectoryNames -contains $child.Name)) {
                continue
            }
            $childRealPath = Resolve-DirectoryRealPath -Directory $child -ParentRealPath $realPath
            if ($current.RealPaths -contains $childRealPath) {
                continue
            }
            $pending.Push([pscustomobject]@{ Directory = $child; RealPaths = @($current.RealPaths) + $childRealPath })
        }
    }
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

function Get-EscapeEnd {
    # $Index indexes a backslash inside a literal. Returns the index after the escaped
    # character. A backslash before CRLF escapes both, because a C/C++ line splice
    # continues the literal on the next line.
    param([string]$Text, [int]$Index)

    $end = $Index + 2
    if ($end -lt $Text.Length -and $Text[$Index + 1] -ceq [char]"`r" -and $Text[$end] -ceq [char]"`n") {
        $end++
    }
    return [Math]::Min($end, $Text.Length)
}

function Get-LiteralEnd {
    # $Start indexes the opening quote of a regular string or character literal.
    # Returns the index just past the closing quote, honouring backslash escapes. A
    # literal left open ends at its line break, which neither language lets a literal
    # hold, so one stray quote cannot turn the rest of the file into a literal.
    param([string]$Text, [int]$Start, [char]$Quote)

    $stops = [char[]]@([char]'\', $Quote, [char]"`r", [char]"`n")
    $length = $Text.Length
    $j = $Start + 1
    while ($j -lt $length) {
        $k = $Text.IndexOfAny($stops, $j)
        if ($k -lt 0) {
            return $length
        }
        $current = $Text[$k]
        if ($current -ceq [char]'\') {
            $j = Get-EscapeEnd -Text $Text -Index $k
            continue
        }
        if ($current -ceq $Quote) {
            return $k + 1
        }
        return $k
    }
    return $length
}

function Get-VerbatimStringEnd {
    # $Start indexes the first character after the opening quote of a C# verbatim
    # string ("" is an escaped quote). Returns the index just past the closing quote, or
    # the text length when the string is never closed.
    param([string]$Text, [int]$Start)

    $j = $Start
    while ($true) {
        $q = $Text.IndexOf([char]'"', $j)
        if ($q -lt 0) {
            return $Text.Length
        }
        if ($q + 1 -lt $Text.Length -and $Text[$q + 1] -ceq [char]'"') {
            $j = $q + 2
            continue
        }
        return $q + 1
    }
}

function Test-CppRawStringStart {
    # True when the quote at $Quote ends a raw-string prefix that is a whole token (R,
    # u8R, uR, UR or LR), so an identifier that merely ends in R, as in FOOR"x", is
    # followed by an ordinary string.
    param([string]$Text, [int]$Quote)

    if ($Quote -lt 1 -or $Text[$Quote - 1] -cne [char]'R') {
        return $false
    }
    $start = $Quote - 1
    if ($start -ge 2 -and $Text[$start - 2] -ceq [char]'u' -and $Text[$start - 1] -ceq [char]'8') {
        $start -= 2
    }
    elseif ($start -ge 1 -and 'uUL'.IndexOf($Text[$start - 1]) -ge 0) {
        $start--
    }
    if ($start -eq 0) {
        return $true
    }
    $previous = $Text[$start - 1]
    return -not ([char]::IsLetterOrDigit($previous) -or $previous -ceq [char]'_')
}

function Get-CppRawStringEnd {
    # $Quote indexes the quote of a C++ raw string R"delim( ... )delim". Returns the
    # index just past the closing )delim", the text length when it is never closed, or
    # -1 when no valid delimiter (at most 16 characters, then '(') follows, in which
    # case the caller lexes an ordinary string.
    param([string]$Text, [int]$Quote)

    $searchLength = [Math]::Min($Text.Length - $Quote - 1, 17)
    if ($searchLength -le 0) {
        return -1
    }
    $open = $Text.IndexOf([char]'(', $Quote + 1, $searchLength)
    if ($open -lt 0) {
        return -1
    }
    $delimiter = $Text.Substring($Quote + 1, $open - $Quote - 1)
    if ($delimiter.IndexOfAny([char[]]@([char]' ', [char]')', [char]'\', [char]"`t", [char]"`v", [char]"`f", [char]"`r", [char]"`n")) -ge 0) {
        return -1
    }
    $closing = ')' + $delimiter + '"'
    $close = $Text.IndexOf($closing, $open + 1, [System.StringComparison]::Ordinal)
    if ($close -lt 0) {
        return $Text.Length
    }
    return $close + $closing.Length
}

function Test-DigitSeparator {
    # A C++14 digit separator (1'000'000, 0xFFFF'FFFF) continues a token that starts
    # with a digit; an apostrophe after an identifier (L'x', u8'x') or punctuation
    # opens a character literal. $TokenTail, a right-to-left regex, finds the token
    # ending at $Index without a character-by-character walk back over a long token.
    param([string]$Text, [int]$Index, [System.Text.RegularExpressions.Regex]$TokenTail)

    $token = $TokenTail.Match($Text, 0, $Index)
    return ($token.Success -and [char]::IsDigit($Text[$token.Index]))
}

function Get-CodeText {
    # Returns the code text of a whole source file, exactly as long as the file: every
    # comment character becomes a space and every line break stays, so a match offset is
    # also a source offset and a call split over lines is still one match. String and
    # character literal contents are kept. Literals are lexed by the file's language:
    # C# regular, verbatim, raw and interpolated strings, with interpolation holes lexed
    # as code, and C/C++ raw strings and digit separators. A quote or comment marker
    # inside any of them therefore cannot end the literal early or hide later code.
    # A block comment still open at the end of the file throws, because it would hide
    # everything after it. Any other literal left open runs to its line break or to the
    # end of the file and stays code text, which can only add hits.
    # Known limitations: a backslash-continued // comment ends at its own line, and C#
    # #if-excluded text and directive messages (#region, #error) are lexed as code, so
    # a comment marker inside them still opens a comment.
    param([string]$Text, [bool]$IsManaged, [string]$Path)

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    $nonBreak = [System.Text.RegularExpressions.Regex]::new('[^\r\n]', $regexOptions)
    $sameCharRun = [System.Text.RegularExpressions.Regex]::new('\G(.)\1*',
        $regexOptions -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $lineBreaks = [char[]]@([char]"`r", [char]"`n")
    if ($IsManaged) {
        $codeStops = [char[]]@([char]'/', [char]'"', [char]"'", [char]'$', [char]'@')
        $holeStops = [char[]]@([char]'/', [char]'"', [char]"'", [char]'$', [char]'@',
            [char]'{', [char]'}', [char]'(', [char]')', [char]'[', [char]']', [char]':')
        $regularTextStops = [char[]]@([char]'"', [char]'\', [char]'{', [char]"`r", [char]"`n")
        $otherTextStops = [char[]]@([char]'"', [char]'{')
        $regularFormatStops = [char[]]@([char]'}', [char]"`r", [char]"`n")
        # Prefix ($ or $$..., @, $@ or @$) and quote run of a C# string literal.
        $stringOpen = [System.Text.RegularExpressions.Regex]::new(
            '\G(?:(?<d>\$+)(?<v>@)?|(?<v>@)(?<d>\$*))?(?<q>"+)', $regexOptions)
        $prefixRun = [System.Text.RegularExpressions.Regex]::new('\G[$@]+', $regexOptions)
    }
    else {
        $codeStops = [char[]]@([char]'/', [char]'"', [char]"'")
        $tokenTail = [System.Text.RegularExpressions.Regex]::new('[\p{L}\p{Nd}_.]+\z',
            $regexOptions -bor [System.Text.RegularExpressions.RegexOptions]::RightToLeft)
        # The rest of a pp-number after a digit separator, so a long 1'000'000'... run
        # is skipped in one step instead of being walked back at every apostrophe.
        $numberRest = [System.Text.RegularExpressions.Regex]::new(
            '\G(?:[\p{L}\p{Nd}_.]|''(?=[\p{L}\p{Nd}_]))*', $regexOptions)
    }

    # The innermost lexing context is on top: Code frames (the file itself, or an
    # interpolation hole of the Text frame below it) and Text frames (the literal text
    # of a C# interpolated string, whose Form is Regular, Verbatim or Raw).
    $frames = New-Object 'System.Collections.Generic.Stack[hashtable]'
    $frames.Push(@{ Kind = 'Code'; Hole = $false })
    $length = $Text.Length
    $code = New-Object System.Text.StringBuilder ($length + 1)
    $i = 0
    while ($i -lt $length) {
        $frame = $frames.Peek()

        if ($frame.Kind -eq 'Text') {
            if ($frame.Form -eq 'Regular') {
                $next = $Text.IndexOfAny($regularTextStops, $i)
            }
            else {
                $next = $Text.IndexOfAny($otherTextStops, $i)
            }
            if ($next -lt 0) {
                [void]$code.Append($Text, $i, $length - $i)
                break
            }
            [void]$code.Append($Text, $i, $next - $i)
            $current = $Text[$next]
            $end = $next + 1
            if ($current -ceq [char]'\') {
                $end = Get-EscapeEnd -Text $Text -Index $next
            }
            elseif ($current -ceq [char]'{') {
                $run = $sameCharRun.Match($Text, $next).Length
                if ($frame.Form -eq 'Raw') {
                    # Fewer braces than dollar signs are literal text; with enough, the
                    # last Dollars of them open a hole that the same count closes.
                    $end = $next + $run
                    if ($run -ge $frame.Dollars) {
                        $frames.Push(@{ Kind = 'Code'; Hole = $true; Depth = 0; Parens = 0; Close = $frame.Dollars; Form = $frame.Form })
                    }
                }
                elseif ($run -ge 2) {
                    $end = $next + 2
                }
                else {
                    $frames.Push(@{ Kind = 'Code'; Hole = $true; Depth = 0; Parens = 0; Close = 1; Form = $frame.Form })
                }
            }
            elseif ($current -ceq [char]'"') {
                if ($frame.Form -eq 'Raw') {
                    $run = $sameCharRun.Match($Text, $next).Length
                    $end = $next + $run
                    if ($run -ge $frame.Quotes) {
                        [void]$frames.Pop()
                    }
                }
                elseif ($frame.Form -eq 'Verbatim' -and $end -lt $length -and $Text[$end] -ceq [char]'"') {
                    $end++
                }
                else {
                    [void]$frames.Pop()
                }
            }
            else {
                # A regular interpolated string cannot span lines: one left open ends
                # at its line break, which the enclosing code then lexes.
                [void]$frames.Pop()
                $end = $next
            }
            [void]$code.Append($Text, $next, $end - $next)
            $i = $end
            continue
        }

        if ($frame.Hole) {
            $next = $Text.IndexOfAny($holeStops, $i)
        }
        else {
            $next = $Text.IndexOfAny($codeStops, $i)
        }
        if ($next -lt 0) {
            [void]$code.Append($Text, $i, $length - $i)
            break
        }
        [void]$code.Append($Text, $i, $next - $i)
        $current = $Text[$next]
        $following = [char]0
        if ($next + 1 -lt $length) {
            $following = $Text[$next + 1]
        }
        $end = $next + 1

        if ($current -ceq [char]'/') {
            if ($following -ceq [char]'/') {
                $end = $Text.IndexOfAny($lineBreaks, $next)
                if ($end -lt 0) {
                    $end = $length
                }
                [void]$code.Append([char]' ', $end - $next)
                $i = $end
                continue
            }
            if ($following -ceq [char]'*') {
                $close = $Text.IndexOf('*/', $next + 2, [System.StringComparison]::Ordinal)
                if ($close -lt 0) {
                    $line = [System.Text.RegularExpressions.Regex]::Matches($Text.Substring(0, $next), '\r\n?|\n').Count + 1
                    throw ('{0}:{1}: block comment is never closed, so the code after it cannot be scanned.' -f $Path, $line)
                }
                $end = $close + 2
                [void]$code.Append($nonBreak.Replace($Text.Substring($next, $end - $next), ' '))
                $i = $end
                continue
            }
        }
        elseif ($current -ceq [char]"'") {
            if (-not $IsManaged -and (Test-DigitSeparator -Text $Text -Index $next -TokenTail $tokenTail)) {
                $end = $next + 1 + $numberRest.Match($Text, $next + 1).Length
            }
            else {
                $end = Get-LiteralEnd -Text $Text -Start $next -Quote $current
            }
        }
        elseif (-not $IsManaged) {
            $end = -1
            if (Test-CppRawStringStart -Text $Text -Quote $next) {
                $end = Get-CppRawStringEnd -Text $Text -Quote $next
            }
            if ($end -lt 0) {
                $end = Get-LiteralEnd -Text $Text -Start $next -Quote $current
            }
        }
        elseif ($current -ceq [char]'"' -or $current -ceq [char]'$' -or $current -ceq [char]'@') {
            $open = $stringOpen.Match($Text, $next)
            if (-not $open.Success) {
                # A verbatim identifier such as @class: no string opens here.
                $end = $next + $prefixRun.Match($Text, $next).Length
            }
            else {
                $dollars = $open.Groups['d'].Length
                $quotes = $open.Groups['q'].Length
                $quoteStart = $open.Groups['q'].Index
                if ($open.Groups['v'].Success) {
                    $end = $quoteStart + 1
                    if ($dollars -gt 0) {
                        $frames.Push(@{ Kind = 'Text'; Form = 'Verbatim'; Quotes = 1; Dollars = 1 })
                    }
                    else {
                        $end = Get-VerbatimStringEnd -Text $Text -Start $end
                    }
                }
                elseif ($quotes -ge 3) {
                    # A raw string: it ends at the first run of as many quotes as
                    # opened it, and holes open with as many braces as it has dollars.
                    $end = $quoteStart + $quotes
                    if ($dollars -gt 0) {
                        $frames.Push(@{ Kind = 'Text'; Form = 'Raw'; Quotes = $quotes; Dollars = $dollars })
                    }
                    else {
                        $close = $Text.IndexOf([string]::new([char]'"', $quotes), $end, [System.StringComparison]::Ordinal)
                        if ($close -lt 0) {
                            $end = $length
                        }
                        else {
                            $end = $close + $sameCharRun.Match($Text, $close).Length
                        }
                    }
                }
                elseif ($quotes -eq 2) {
                    $end = $quoteStart + 2
                }
                elseif ($dollars -gt 0) {
                    $end = $quoteStart + 1
                    $frames.Push(@{ Kind = 'Text'; Form = 'Regular'; Quotes = 1; Dollars = 1 })
                }
                else {
                    $end = Get-LiteralEnd -Text $Text -Start $quoteStart -Quote ([char]'"')
                }
            }
        }
        elseif ($current -ceq [char]'{') {
            $frame.Depth++
        }
        elseif ($current -ceq [char]'}') {
            if ($frame.Depth -gt 0) {
                $frame.Depth--
            }
            else {
                $end = $next + [Math]::Min($sameCharRun.Match($Text, $next).Length, $frame.Close)
                [void]$frames.Pop()
            }
        }
        elseif ($current -ceq [char]'(' -or $current -ceq [char]'[') {
            $frame.Parens++
        }
        elseif ($current -ceq [char]')' -or $current -ceq [char]']') {
            if ($frame.Parens -gt 0) {
                $frame.Parens--
            }
        }
        elseif ($frame.Depth -eq 0 -and $frame.Parens -eq 0 -and $following -cne [char]':' -and $Text[$next - 1] -cne [char]':') {
            # A ':' at the top of a hole starts its format clause, which is literal text
            # up to the closing brace; '::', as in global::, is code.
            if ($frame.Form -eq 'Regular') {
                $end = $Text.IndexOfAny($regularFormatStops, $next)
            }
            else {
                $end = $Text.IndexOf([char]'}', $next)
            }
            if ($end -lt 0) {
                $end = $length
            }
            elseif ($Text[$end] -cne [char]'}') {
                # The line break ends both the hole and its regular string.
                [void]$frames.Pop()
                [void]$frames.Pop()
            }
        }
        [void]$code.Append($Text, $next, $end - $next)
        $i = $end
    }
    return $code.ToString()
}

function Get-LineStartOffset {
    # The offset at which each line starts, splitting at CRLF, LF and CR as
    # File.ReadAllLines does, so reported line numbers match an editor's.
    param([string]$Text)

    $starts = New-Object 'System.Collections.Generic.List[int]'
    $starts.Add(0)
    foreach ($lineBreak in [System.Text.RegularExpressions.Regex]::Matches($Text, '\r\n?|\n')) {
        $starts.Add($lineBreak.Index + $lineBreak.Length)
    }
    return , $starts.ToArray()
}

function Get-TruncatedText {
    param([string]$Text)

    if ($Text.Length -le $MaxContextLength) {
        return $Text
    }
    return $Text.Substring(0, $MaxContextLength - 3) + '...'
}

function Format-HitLine {
    # A match can span lines and blanked comments; collapsing its whitespace runs keeps
    # every hit on one output line.
    param([string]$DisplayPath, [int]$LineNumber, [psobject]$Rule, [string]$MatchedText, [string]$SourceLine)

    $matchedOneLine = [System.Text.RegularExpressions.Regex]::Replace($MatchedText, '\s+', ' ')
    return ('{0}:{1}: [{2}/{3}] {4} -- {5}' -f $DisplayPath, $LineNumber, $Rule.Group, $Rule.Name,
        (Get-TruncatedText -Text $matchedOneLine), (Get-TruncatedText -Text $SourceLine.Trim()))
}


try {
    if ([string]::IsNullOrWhiteSpace($SourceRoot) -or -not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        Write-Error -Message ("check-constraints: source root not found: '{0}'." -f $SourceRoot) -ErrorAction Continue
        exit 2
    }

    # -LiteralPath because a checkout folder such as RAMQuick[dev] is a wildcard pattern
    # to -Path; ProviderPath because Path carries a provider prefix on a UNC share.
    $repoRoot = Get-NormalizedPath -Path (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
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
                # A backstop for a pattern that turns out to backtrack on some input: the
                # scan then fails (exit 2) instead of hanging the build. A whole 5 MB file
                # takes the slowest rule under 0.1 s, so 10 s per match is far beyond any
                # legitimate source, even on a heavily loaded host.
                Regex     = [System.Text.RegularExpressions.Regex]::new(
                    $rule.Pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant,
                    [TimeSpan]::FromSeconds(10))
            }
        }
    )

    # Sorted so the output is the same whatever order the file system lists entries in.
    $sourceFiles = @(Get-SourceFile -RootPath $sourceRootPath | Sort-Object -Property FullName)

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

        # Each rule runs once over the whole file's code text, so a call split over lines
        # is still one match. Hits are then ordered by the line each match starts on, then
        # rule table order, then column.
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        $codeText = Get-CodeText -Text $text -IsManaged $isManaged -Path $displayPath
        $fileHits = New-Object 'System.Collections.Generic.List[object]'
        for ($r = 0; $r -lt $applicableRules.Count; $r++) {
            # The regex's own timeout message names neither the file nor the rule.
            try {
                foreach ($match in $applicableRules[$r].Regex.Matches($codeText)) {
                    $fileHits.Add([pscustomobject]@{ Line = 0; RuleOrder = $r; Index = $match.Index; Value = $match.Value })
                }
            }
            catch [System.Text.RegularExpressions.RegexMatchTimeoutException] {
                throw ('{0}: rule {1}/{2} exceeded its match time limit of {3} s.' -f $displayPath,
                    $applicableRules[$r].Group, $applicableRules[$r].Name, $_.Exception.MatchTimeout.TotalSeconds)
            }
        }
        if ($fileHits.Count -gt 0) {
            $lineStarts = Get-LineStartOffset -Text $text
            foreach ($hit in $fileHits) {
                $lineIndex = [Array]::BinarySearch($lineStarts, $hit.Index)
                if ($lineIndex -lt 0) {
                    $lineIndex = (-bnot $lineIndex) - 1
                }
                $hit.Line = $lineIndex + 1
            }
            foreach ($hit in ($fileHits | Sort-Object -Property Line, RuleOrder, Index)) {
                $lineStart = $lineStarts[$hit.Line - 1]
                $lineEnd = $text.IndexOfAny([char[]]@([char]"`r", [char]"`n"), $lineStart)
                if ($lineEnd -lt 0) {
                    $lineEnd = $text.Length
                }
                $sourceLine = $text.Substring($lineStart, $lineEnd - $lineStart)
                Write-Output (Format-HitLine -DisplayPath $displayPath -LineNumber $hit.Line -Rule $applicableRules[$hit.RuleOrder] -MatchedText $hit.Value -SourceLine $sourceLine)
            }
            $hitCount += $fileHits.Count
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

