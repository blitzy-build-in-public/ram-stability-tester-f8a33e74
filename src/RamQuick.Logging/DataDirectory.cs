using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RamQuick.Logging;

/// <summary>
/// Where a <see cref="DataDirectory"/> was found.
/// </summary>
public enum DataDirectoryKind
{
    /// <summary>
    /// The <c>data</c> folder beside the executable, preferred because it keeps the portable
    /// folder self-contained.
    /// </summary>
    AppFolder = 0,

    /// <summary>
    /// <c>%LOCALAPPDATA%\RamQuick</c>, used when the app folder is not writable, for example
    /// when the portable folder sits on read-only media or under a protected location.
    /// </summary>
    LocalAppData = 1,

    /// <summary>
    /// A root named explicitly through <see cref="DataDirectory.FromRoot(string)"/>: a test's
    /// temporary folder or the folder <c>RamQuick.TestHost</c> receives with <c>--data</c>.
    /// </summary>
    Custom = 2,
}

/// <summary>
/// The folder that holds the progress journal and the results logs, and the paths derived from
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Resolve()"/> tries, in order, <c>&lt;app folder&gt;\data</c> and then
/// <c>%LOCALAPPDATA%\RamQuick</c>, and returns the first one that passes the write probe, or
/// <see langword="null"/> when neither does. A <see langword="null"/> result means Start must be
/// refused, because the run could not be journalled.
/// </para>
/// <para>
/// The write probe creates the root and its <c>journal</c> and <c>logs</c> subfolders, then
/// creates a uniquely named <c>.write-probe-&lt;guid&gt;.tmp</c> file in the root and in each
/// subfolder, writes a random value to it, flushes it to the device and reads it back through the
/// folder's path. The files are opened delete-on-close, so they are gone once the probe ends, even
/// if the process dies during the probe. An unwritable location is an expected condition: the
/// probe reports it as <see langword="false"/> and never throws.
/// </para>
/// <para>
/// Plain-folder rule: the root, <c>journal</c> and <c>logs</c> must each be a plain folder, never a
/// link or junction, because an elevated RAMQuick writing through one would write wherever it
/// points. A candidate that breaks the rule fails the probe. Folders above the root may be links
/// or junctions, so an app folder or profile reached through one stays usable, except as the
/// protected-root rule below restricts it.
/// </para>
/// <para>
/// Protected root: when the process is elevated (<see cref="Environment.IsPrivilegedProcess"/>)
/// and the candidate's volume can hold links, <see cref="Resolve()"/> uses a root only when no
/// account other than SYSTEM, Administrators and TrustedInstaller can change it, its
/// <c>journal</c> or its <c>logs</c>. A missing root is created that way: owned by
/// Administrators, inheriting nothing from its parent, changeable by SYSTEM and Administrators
/// and readable by the current account. A root that a non-elevated launch left holding nothing
/// but empty <c>journal</c> and <c>logs</c> folders is replaced by such a root. Any other
/// unprotected root is refused, so <see cref="Resolve()"/> falls back. Without this, a
/// non-elevated user could plant a link inside the data folder between the checks and the writes.
/// A data folder that an elevated launch protected is not writable by a later non-elevated
/// launch, which then uses <c>%LOCALAPPDATA%\RamQuick</c>.
/// </para>
/// <para>
/// A protected root is also refused when a junction or symbolic link above it is one that an
/// account other than SYSTEM, Administrators and TrustedInstaller could retarget, or delete and
/// create again, because such a link could be pointed elsewhere after launch. Each folder an
/// elevated process creates or deletes anything in, the root's parent and every missing folder
/// above the root included, is first opened through <see cref="FolderOpener"/> and held open, and
/// <see cref="FinalPathQuery"/> must report the held folder where its path, with the links above it
/// resolved, says it is; each of the root's folders is checked again through the open file a pin
/// creates in it. Without an installed query and opener an elevated process uses no protected root.
/// </para>
/// <para>
/// Instances are immutable and safe to share across threads. This type writes nothing outside
/// the candidate roots and performs no I/O beyond the probe, the subfolder creation and, for a
/// protected root, its creation or replacement; the journal and the logs are written by
/// <c>FileJournalStorage</c> and <c>ResultsLog</c>.
/// </para>
/// </remarks>
public sealed class DataDirectory
{
    /// <summary>
    /// File name of the progress journal inside <see cref="JournalDirectory"/>.
    /// </summary>
    public const string JournalFileName = "active-run.jsonl";

    /// <summary>
    /// Name of the data folder created beside the executable.
    /// </summary>
    public const string AppFolderDataName = "data";

    /// <summary>
    /// Name of the fallback folder created under the user's local application data folder.
    /// </summary>
    public const string LocalAppDataFolderName = "RamQuick";

    private const string JournalFolderName = "journal";
    private const string LogsFolderName = "logs";
    private const string AppErrorsLogFileName = "app-errors.log";

    /// <summary>
    /// File systems that cannot hold junctions or symbolic links, where no link can be planted and
    /// the plain-folder rule alone keeps the data folder in place.
    /// </summary>
    private static readonly HashSet<string> _linklessFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAT", "FAT32", "exFAT", "UDF", "CDFS",
    };

    /// <summary>
    /// The roots this process resolved as protected roots, by full path without a trailing
    /// separator.
    /// </summary>
    /// <remarks>
    /// <c>ResultsLog</c> and <c>FileJournalStorage</c> know only the paths they write to, and a
    /// non-elevated user can replace an unpinned root between two of their operations, so each
    /// operation looks its root up here and re-checks the protection before it writes.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, bool> _protectedRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The elevation <see cref="IsPrivilegedProcess"/> reports in the current execution flow in
    /// place of the process's own, or <see langword="null"/> when none is in effect.
    /// </summary>
    /// <remarks>
    /// Lets tests exercise both the protected and the unprotected path on one host. Like
    /// <see cref="_hostLocationsOverride"/>, a value never leaks into a flow that does not inherit
    /// it, and the application never sets one.
    /// </remarks>
    private static readonly AsyncLocal<bool?> _elevationOverride = new();

    /// <summary>
    /// The host locations <see cref="Resolve()"/> uses instead of the real ones in the current
    /// execution flow, or <see langword="null"/> when none is in effect.
    /// </summary>
    /// <remarks>
    /// Tests run in parallel and must exercise <see cref="Resolve()"/> without probing beside
    /// their own assembly or under the user's profile. A value set in one flow is invisible to
    /// every flow that does not inherit it, so an override cannot leak into a parallel test, and
    /// the application, which never sets one, always resolves the real locations.
    /// </remarks>
    private static readonly AsyncLocal<HostLocations?> _hostLocationsOverride = new();

    /// <summary>
    /// The query <see cref="EffectiveFinalPathQuery"/> returns in the current execution flow in place of
    /// <see cref="FinalPathQuery"/>, or <see langword="null"/> when none is in effect.
    /// </summary>
    /// <remarks>
    /// Lets tests simulate a folder swapped during a pin, or a host without the query, without touching
    /// the process-wide query that parallel tests use. The application never sets one.
    /// </remarks>
    private static readonly AsyncLocal<FinalPathQueryOverride?> _finalPathQueryOverride = new();

    /// <summary>
    /// The opener <see cref="EffectiveFolderOpener"/> returns in the current execution flow in place of
    /// <see cref="FolderOpener"/>, or <see langword="null"/> when none is in effect.
    /// </summary>
    /// <remarks>
    /// Lets tests swap a folder just before it is held, or simulate a host without the opener, without touching the
    /// process-wide opener that parallel tests use. The application never sets one.
    /// </remarks>
    private static readonly AsyncLocal<FolderOpenerOverride?> _folderOpenerOverride = new();

    /// <summary>
    /// Whether <see cref="CanWrite"/> requires the root to stay a protected root, as
    /// <see cref="Resolve()"/> established it.
    /// </summary>
    private readonly bool _requiresProtection;

    private DataDirectory(string rootPath, DataDirectoryKind kind, bool requiresProtection)
    {
        _requiresProtection = requiresProtection;
        RootPath = rootPath;
        Kind = kind;
        JournalDirectory = Path.Combine(rootPath, JournalFolderName);
        JournalPath = Path.Combine(JournalDirectory, JournalFileName);
        LogsDirectory = Path.Combine(rootPath, LogsFolderName);
        AppErrorsLogPath = Path.Combine(LogsDirectory, AppErrorsLogFileName);
    }

    /// <summary>
    /// Gets the full path of the data folder, normalised through <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets which location this data folder was resolved from.
    /// </summary>
    public DataDirectoryKind Kind { get; }

    /// <summary>
    /// Gets the folder that holds the progress journal: <c>&lt;data&gt;\journal</c>.
    /// </summary>
    public string JournalDirectory { get; }

    /// <summary>
    /// Gets the path of the progress journal: <c>&lt;data&gt;\journal\active-run.jsonl</c>.
    /// </summary>
    public string JournalPath { get; }

    /// <summary>
    /// Gets the folder that holds each run's <c>.log</c>, <c>-summary.json</c> and
    /// <c>-summary.txt</c> files: <c>&lt;data&gt;\logs</c>.
    /// </summary>
    public string LogsDirectory { get; }

    /// <summary>
    /// Gets the path of the log that records managed exceptions raised inside engine callbacks:
    /// <c>&lt;data&gt;\logs\app-errors.log</c>.
    /// </summary>
    public string AppErrorsLogPath { get; }

    /// <summary>
    /// Gets or sets the query that returns the path Windows reports for an open file, with every junction
    /// and symbolic link on the way resolved, which an elevated process needs to verify each data folder
    /// it writes in.
    /// </summary>
    /// <value>
    /// A function that returns the final path of the file open in its argument, without a <c>\\?\</c>
    /// prefix, or <see langword="null"/> when Windows cannot report it; <see langword="null"/>, the
    /// default, when no query is installed.
    /// </value>
    /// <remarks>
    /// This library calls no native code, so the host installs the query, the application RAMQuick.Interop's,
    /// before its first <see cref="Resolve()"/>. Without one an elevated process uses no data folder:
    /// <see cref="Resolve()"/> returns <see langword="null"/> and <see cref="CanWrite"/>
    /// <see langword="false"/>. A non-elevated process and a <see cref="FromRoot(string)"/> root never use
    /// it.
    /// </remarks>
    public static Func<SafeFileHandle, string?>? FinalPathQuery { get; set; }

    /// <summary>
    /// Gets the query a pin uses to verify a folder: <see cref="FinalPathQuery"/>, or, while an
    /// <see cref="OverrideFinalPathQuery"/> scope is open in the current execution flow, that scope's
    /// query, which may be <see langword="null"/>.
    /// </summary>
    internal static Func<SafeFileHandle, string?>? EffectiveFinalPathQuery =>
        _finalPathQueryOverride.Value is { } queryOverride ? queryOverride.Query : FinalPathQuery;

    /// <summary>
    /// Gets or sets the function that opens an existing folder and holds it in place while the handle it returns stays
    /// open, which an elevated process needs so that each data folder it verifies stays the folder it creates or deletes
    /// entries in.
    /// </summary>
    /// <value>
    /// A function that returns an open handle to the folder at its argument, reached through any junction or symbolic
    /// link on the way, which keeps the folder from being renamed or deleted, and the folders above it from being moved,
    /// until it is disposed; or <see langword="null"/> when Windows cannot open a folder there. <see langword="null"/>, the
    /// default, when no opener is installed.
    /// </value>
    /// <remarks>
    /// This library calls no native code, so the host installs the opener, the application RamQuick.Interop's, together
    /// with <see cref="FinalPathQuery"/> before its first <see cref="Resolve()"/>. Without one an elevated process uses no
    /// data folder: <see cref="Resolve()"/> returns <see langword="null"/> and <see cref="CanWrite"/>
    /// <see langword="false"/>. A non-elevated process and a <see cref="FromRoot(string)"/> root never use it.
    /// </remarks>
    public static Func<string, SafeFileHandle?>? FolderOpener { get; set; }

    /// <summary>
    /// Gets the opener a pin uses to hold a folder: <see cref="FolderOpener"/>, or, while an
    /// <see cref="OverrideFolderOpener"/> scope is open in the current execution flow, that scope's opener, which may be
    /// <see langword="null"/>.
    /// </summary>
    internal static Func<string, SafeFileHandle?>? EffectiveFolderOpener =>
        _folderOpenerOverride.Value is { } openerOverride ? openerOverride.Opener : FolderOpener;

    /// <summary>
    /// Gets the app folder <see cref="Resolve()"/> tries first:
    /// <see cref="AppContext.BaseDirectory"/>, which ends in a directory separator, or, while an
    /// <see cref="OverrideHostLocations(string, string?)"/> scope is open in the current execution
    /// flow, that scope's app folder.
    /// </summary>
    /// <remarks>
    /// Read apart from the probe so that tests can check this location without writing beside
    /// their own assembly.
    /// </remarks>
    internal static string DefaultAppFolder =>
        _hostLocationsOverride.Value?.AppFolder ?? AppContext.BaseDirectory;

    /// <summary>
    /// Gets the local application data folder <see cref="Resolve()"/> falls back to: the user's,
    /// read without verifying that it exists and empty when Windows does not know it, or, while an
    /// <see cref="OverrideHostLocations(string, string?)"/> scope is open in the current execution
    /// flow, that scope's folder, which may be <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Read apart from the probe so that tests can check this location without writing under the
    /// user's profile.
    /// </remarks>
    internal static string? DefaultLocalAppDataRoot =>
        _hostLocationsOverride.Value is { } locations
            ? locations.LocalAppDataRoot
            : Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>
    /// Makes <see cref="Resolve()"/>, in the current execution flow and the flows it starts, try
    /// <paramref name="appFolder"/> and <paramref name="localAppDataRoot"/> in place of the host's
    /// locations until the returned scope is disposed.
    /// </summary>
    /// <param name="appFolder">
    /// The folder that stands in for <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="localAppDataRoot">
    /// The folder that stands in for the local application data folder; <see langword="null"/>,
    /// empty or white space stands for a host where Windows does not know that folder.
    /// </param>
    /// <returns>
    /// A scope whose first disposal restores the locations in effect before this call; later
    /// disposals do nothing.
    /// </returns>
    /// <remarks>
    /// Dispose the scope in the flow that opened it, innermost scope first when scopes nest.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="appFolder"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="appFolder"/> is empty or white space.
    /// </exception>
    internal static IDisposable OverrideHostLocations(string appFolder, string? localAppDataRoot)
    {
        // Rejected here as Resolve rejects it, so a bad override fails where it is opened rather
        // than inside a later Resolve call.
        ArgumentException.ThrowIfNullOrWhiteSpace(appFolder);

        HostLocationsScope scope = new(_hostLocationsOverride.Value);
        _hostLocationsOverride.Value = new HostLocations(appFolder, localAppDataRoot);
        return scope;
    }

    /// <summary>
    /// Gets whether the process counts as elevated for the data folder:
    /// <see cref="Environment.IsPrivilegedProcess"/>, or, while an
    /// <see cref="OverrideElevation(bool)"/> scope is open in the current execution flow, that
    /// scope's value.
    /// </summary>
    /// <remarks>
    /// An elevated process requires a protected root from <see cref="Resolve()"/> and creates the
    /// probe's files readable by administrators only.
    /// </remarks>
    internal static bool IsPrivilegedProcess => _elevationOverride.Value ?? Environment.IsPrivilegedProcess;

    /// <summary>
    /// Makes <see cref="IsPrivilegedProcess"/>, in the current execution flow and the flows it
    /// starts, report <paramref name="isPrivileged"/> until the returned scope is disposed.
    /// </summary>
    /// <param name="isPrivileged">The elevation to report.</param>
    /// <returns>
    /// A scope whose first disposal restores the elevation in effect before this call; later
    /// disposals do nothing.
    /// </returns>
    /// <remarks>
    /// Dispose the scope in the flow that opened it, innermost scope first when scopes nest. A
    /// scope reporting elevation in a process that is not elevated makes protected roots fail,
    /// because only an elevated process may name Administrators as a folder's owner.
    /// </remarks>
    internal static IDisposable OverrideElevation(bool isPrivileged)
    {
        ElevationScope scope = new(_elevationOverride.Value);
        _elevationOverride.Value = isPrivileged;
        return scope;
    }

    /// <summary>
    /// Makes <see cref="EffectiveFinalPathQuery"/>, in the current execution flow and the flows it
    /// starts, return <paramref name="query"/> until the returned scope is disposed.
    /// </summary>
    /// <param name="query">The query to use, or <see langword="null"/> for a host that installs none.</param>
    /// <returns>
    /// A scope whose first disposal restores the query in effect before this call; later disposals do
    /// nothing.
    /// </returns>
    /// <remarks>Dispose the scope in the flow that opened it, innermost scope first when scopes nest.</remarks>
    internal static IDisposable OverrideFinalPathQuery(Func<SafeFileHandle, string?>? query)
    {
        FinalPathQueryScope scope = new(_finalPathQueryOverride.Value);
        _finalPathQueryOverride.Value = new FinalPathQueryOverride(query);
        return scope;
    }

    /// <summary>
    /// Makes <see cref="EffectiveFolderOpener"/>, in the current execution flow and the flows it starts, return
    /// <paramref name="opener"/> until the returned scope is disposed.
    /// </summary>
    /// <param name="opener">The opener to use, or <see langword="null"/> for a host that installs none.</param>
    /// <returns>
    /// A scope whose first disposal restores the opener in effect before this call; later disposals do nothing.
    /// </returns>
    /// <remarks>Dispose the scope in the flow that opened it, innermost scope first when scopes nest.</remarks>
    internal static IDisposable OverrideFolderOpener(Func<string, SafeFileHandle?>? opener)
    {
        FolderOpenerScope scope = new(_folderOpenerOverride.Value);
        _folderOpenerOverride.Value = new FolderOpenerOverride(opener);
        return scope;
    }

    /// <summary>
    /// Returns whether <paramref name="rootPath"/> is a root this process resolved as a protected
    /// root, which every later operation in it must find still protected.
    /// </summary>
    /// <param name="rootPath">A folder path; relative paths are resolved against the current directory.</param>
    /// <returns><see langword="true"/> for a protected root; otherwise <see langword="false"/>.</returns>
    internal static bool RequiresProtection(string rootPath) =>
        _protectedRoots.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)));

    /// <summary>
    /// Resolves the data folder for the running application: <c>&lt;app folder&gt;\data</c> when it
    /// passes the write probe, otherwise <c>%LOCALAPPDATA%\RamQuick</c> when that passes.
    /// </summary>
    /// <returns>
    /// The first writable data folder, or <see langword="null"/> when neither location is
    /// writable.
    /// </returns>
    /// <remarks>
    /// The app folder is <see cref="AppContext.BaseDirectory"/>. The local application data
    /// folder is read without verifying that it exists, so a missing folder is created by the
    /// probe rather than skipped. A candidate that is a link, or holds a link in place of its
    /// <c>journal</c> or <c>logs</c> folder, fails the probe; in an elevated process, so does one
    /// that is not or cannot be made a protected root (see the type's remarks). Call this off the
    /// UI thread: the probe flushes to the device.
    /// </remarks>
    public static DataDirectory? Resolve() => Resolve(DefaultAppFolder, DefaultLocalAppDataRoot);

    /// <summary>
    /// Resolves the data folder from explicit locations, trying
    /// <c>&lt;<paramref name="appFolder"/>&gt;\data</c> first and then
    /// <c>&lt;<paramref name="localAppDataRoot"/>&gt;\RamQuick</c>.
    /// </summary>
    /// <param name="appFolder">The folder that holds the executable.</param>
    /// <param name="localAppDataRoot">
    /// The user's local application data folder. When it is <see langword="null"/>, empty or
    /// white space, as <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns
    /// when the folder is unknown, the fallback is not tried.
    /// </param>
    /// <returns>
    /// The first candidate that passes the write probe, and in an elevated process is a protected
    /// root, with <see cref="Kind"/> set to <see cref="DataDirectoryKind.AppFolder"/> or
    /// <see cref="DataDirectoryKind.LocalAppData"/>; <see langword="null"/> when no candidate passes.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="appFolder"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="appFolder"/> is empty or white space.
    /// </exception>
    public static DataDirectory? Resolve(string appFolder, string? localAppDataRoot)
    {
        // An empty app folder would resolve "data" against the current directory, which may lie
        // outside the app folder; that is a caller bug, not an unwritable location.
        ArgumentException.ThrowIfNullOrWhiteSpace(appFolder);

        DataDirectory? appFolderData = TryCandidate(
            Path.Combine(appFolder, AppFolderDataName),
            DataDirectoryKind.AppFolder);
        if (appFolderData is not null)
        {
            return appFolderData;
        }

        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            return null;
        }

        return TryCandidate(
            Path.Combine(localAppDataRoot, LocalAppDataFolderName),
            DataDirectoryKind.LocalAppData);
    }

    /// <summary>
    /// Uses <paramref name="rootPath"/> as the data folder without probing it, and creates its
    /// <c>journal</c> and <c>logs</c> subfolders.
    /// </summary>
    /// <param name="rootPath">
    /// The data folder. Relative paths are resolved against the current directory.
    /// </param>
    /// <returns>A data folder with <see cref="Kind"/> set to <see cref="DataDirectoryKind.Custom"/>.</returns>
    /// <remarks>
    /// For callers that choose the folder themselves: tests rooted in a temporary folder,
    /// <c>RamQuick.TestHost --data &lt;dir&gt;</c>, and process-level tests that recover a journal
    /// from the folder a killed host used. Because the caller named the folder, a failure to
    /// create it, or a root, <c>journal</c> or <c>logs</c> that is not a plain folder, is an error
    /// and is thrown, unlike in <see cref="Resolve()"/>. The root is never required to be a
    /// protected root.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootPath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootPath"/> is empty, white space or not a valid path.
    /// </exception>
    /// <exception cref="IOException">
    /// A folder could not be created, for example because a file has its name, or the root, its
    /// <c>journal</c> or its <c>logs</c> is a link or junction.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the subfolders.</exception>
    public static DataDirectory FromRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        DataDirectory directory = new(Path.GetFullPath(rootPath), DataDirectoryKind.Custom, requiresProtection: false);

        // Each folder is checked before anything is created in it, so a root that is a link never
        // receives the subfolders in the folder it points to.
        Directory.CreateDirectory(directory.RootPath);
        DataFolderPin.RequirePlainFolder(directory.RootPath);
        foreach (string subfolder in new[] { directory.JournalDirectory, directory.LogsDirectory })
        {
            Directory.CreateDirectory(subfolder);
            DataFolderPin.RequirePlainFolder(subfolder);
        }

        return directory;
    }

    /// <summary>
    /// Re-runs the write probe on <see cref="RootPath"/>, recreating the root and its
    /// <c>journal</c> and <c>logs</c> subfolders if they are missing.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a file could be created, written and flushed in the root and in
    /// each subfolder, each of them is a plain folder and, for a root <see cref="Resolve()"/>
    /// established as a protected root, each is still protected; otherwise
    /// <see langword="false"/>. Never throws for an unwritable location.
    /// </returns>
    /// <remarks>
    /// The run controller calls this at Start, so a folder that became unwritable, or was replaced
    /// by a link or an unprotected folder, after launch refuses the run instead of failing its
    /// first journal write. A missing protected root is created protected again. Call it off the
    /// UI thread: the probe flushes to the device.
    /// </remarks>
    public bool CanWrite() => TryPrepare(RootPath, _requiresProtection);

    private static DataDirectory? TryCandidate(string candidateRoot, DataDirectoryKind kind)
    {
        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(candidateRoot);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            return null;
        }

        bool requiresProtection = NeedsProtectedRoot(rootPath);
        if (!TryPrepare(rootPath, requiresProtection))
        {
            return null;
        }

        if (requiresProtection)
        {
            _protectedRoots[Path.TrimEndingDirectorySeparator(rootPath)] = true;
        }

        return new DataDirectory(rootPath, kind, requiresProtection);
    }

    // A non-elevated user can plant a link that steers an elevated process's writes, and only on
    // a volume that can hold links. FromRoot roots are named by their caller, a test or the test
    // host, and keep the plain-folder rule alone.
    private static bool NeedsProtectedRoot(string rootPath) =>
        OperatingSystem.IsWindows() && IsPrivilegedProcess && VolumeCanHoldLinks(rootPath);

    private static bool VolumeCanHoldLinks(string rootPath)
    {
        string? volume = Path.GetPathRoot(rootPath);
        if (string.IsNullOrEmpty(volume))
        {
            return true;
        }

        try
        {
            return !_linklessFileSystems.Contains(new DriveInfo(volume).DriveFormat);
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            // A UNC path, which DriveInfo rejects, or a volume whose format cannot be read: either
            // may hold links.
            return true;
        }
    }

    private static bool TryPrepare(string root, bool requiresProtection)
    {
        SafeFileHandle? heldParent = null;
        try
        {
            if (requiresProtection && OperatingSystem.IsWindows())
            {
                // Checked before anything is created or deleted, so a root reached through a link that a
                // non-administrator can change never receives a folder, and a process that cannot hold a
                // folder open and verify it uses none.
                if (EffectiveFinalPathQuery is null || EffectiveFolderOpener is null)
                {
                    return false;
                }

                string expectedRoot = DataFolderPin.ExpectedFinalPath(root);

                // Held, and created first where missing, from before the root is created or replaced until
                // the root's pin is released, so every name created or deleted in it lies in the folder
                // Windows verified, never where a folder swapped for a junction meanwhile would lead.
                heldParent = DataFolderPin.HoldParent(root, expectedRoot);
                if (!TryEstablishProtectedRoot(root, expectedRoot))
                {
                    return false;
                }
            }
            else
            {
                Directory.CreateDirectory(root);
            }

            // The pin is the write probe: its file in each folder is created, written, flushed to
            // the device and deleted on close, and it proves each folder is plain and, when
            // required, protected.
            using (DataFolderPin.Acquire(root, requiresProtection, createSubfolders: true, JournalFolderName, LogsFolderName))
            {
                return true;
            }
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            return false;
        }
        finally
        {
            heldParent?.Dispose();
        }
    }

    // Makes root a protected root, or reports that it cannot be one. The pin that follows checks
    // the result, because a folder created here or a protection read here can be replaced before
    // the pin holds it. expectedRoot is where Windows must find root (DataFolderPin.ExpectedFinalPath).
    // The caller holds root's parent open and verified throughout.
    [SupportedOSPlatform("windows")]
    private static bool TryEstablishProtectedRoot(string root, string expectedRoot)
    {
        if (DataFolderPin.TryGetAttributes(root) is not { } attributes)
        {
            // The parent exists and is held, so the root can only be created in it. Creating an
            // existing folder, such as a junction planted under the root's name, does nothing, which
            // the pin then detects.
            ProtectedFolder.Create(root);
            return true;
        }

        return DataFolderPin.IsPlainFolder(attributes)
            && (ProtectedFolder.IsProtected(root) || TryTakeOverPristineRoot(root, expectedRoot));
    }

    // A first, non-elevated launch leaves a root holding only empty journal and logs folders. The
    // root is then replaced by a protected one. A root holding anything else may hold a user's
    // results and is never emptied, so it is refused. The access list of an existing folder is
    // never rewritten: the folder could be swapped for another between the check and the change.
    [SupportedOSPlatform("windows")]
    private static bool TryTakeOverPristineRoot(string root, string expectedRoot)
    {
        // The root is not protected yet, so it is pinned with its location verified, held open from before its
        // anchor exists: without this, a folder swapped in for it or above it since the caller's check could
        // receive the anchor or have its entries deleted.
        using (DataFolderPin pin = DataFolderPin.Acquire(root, requireProtection: false, expectedRoot, createSubfolders: false))
        {
            List<string> subfolders = new();
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (pin.IsAnchor(entry))
                {
                    continue;
                }

                string name = Path.GetFileName(entry);
                bool known = string.Equals(name, JournalFolderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, LogsFolderName, StringComparison.OrdinalIgnoreCase);
                if (!known
                    || DataFolderPin.TryGetAttributes(entry) is not { } attributes
                    || (attributes & FileAttributes.Directory) == 0)
                {
                    return false;
                }

                // Checked before anything is deleted, so a refused root is left exactly as it was. A link is
                // never listed: that would list the folder it points to.
                if (DataFolderPin.IsPlainFolder(attributes) && Directory.EnumerateFileSystemEntries(entry).Any())
                {
                    return false;
                }

                subfolders.Add(entry);
            }

            // The root is pinned, so each name still lies in it. A non-recursive delete removes a
            // link itself, never what it points to, and a plain folder only while it is empty.
            foreach (string subfolder in subfolders)
            {
                Directory.Delete(subfolder, recursive: false);
            }
        }

        // The root's pin, which holds the root open, is released first, because Windows refuses to delete a held
        // folder. The caller still holds the parent, so the name deleted and created is the one in the verified
        // parent: a root swapped for a link since is removed itself, never what it points to, and a link planted
        // in its place before the creation makes the creation do nothing, which the caller's pin then refuses.
        Directory.Delete(root, recursive: false);
        ProtectedFolder.Create(root);
        return true;
    }

    // The failures Directory.CreateDirectory, FileStream, the folder pin, the access-control calls
    // and Path.GetFullPath raise for a location that cannot hold the data folder: missing or
    // read-only media, a file in place of a folder, a link or unprotected folder, denied access, or
    // a path the file system rejects. Anything else is a defect and propagates.
    private static bool IsUnusableLocation(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException;

    /// <summary>
    /// The locations an <see cref="OverrideHostLocations(string, string?)"/> scope puts in place
    /// of the host's.
    /// </summary>
    /// <param name="AppFolder">
    /// The folder that stands in for <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="LocalAppDataRoot">
    /// The folder that stands in for the local application data folder, or
    /// <see langword="null"/> for a host where Windows does not know that folder.
    /// </param>
    private sealed record HostLocations(string AppFolder, string? LocalAppDataRoot);

    /// <summary>
    /// The scope <see cref="OverrideHostLocations(string, string?)"/> returns.
    /// </summary>
    private sealed class HostLocationsScope : IDisposable
    {
        /// <summary>
        /// The override this scope reinstates, or <see langword="null"/> for the host's locations.
        /// </summary>
        private readonly HostLocations? _previous;

        /// <summary>
        /// Whether this scope has already restored <see cref="_previous"/>.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Creates a scope that restores <paramref name="previous"/> when disposed.
        /// </summary>
        /// <param name="previous">
        /// The override in effect before this scope opened, or <see langword="null"/> for the
        /// host's locations.
        /// </param>
        public HostLocationsScope(HostLocations? previous) => _previous = previous;

        /// <summary>
        /// Restores the locations in effect before this scope opened, the first time it is called.
        /// </summary>
        public void Dispose()
        {
            // A second restore would reinstate this scope's predecessor after an outer scope had
            // already closed, so only the first disposal acts.
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hostLocationsOverride.Value = _previous;
        }
    }

    /// <summary>
    /// The scope <see cref="OverrideElevation(bool)"/> returns.
    /// </summary>
    private sealed class ElevationScope : IDisposable
    {
        /// <summary>
        /// The override this scope reinstates, or <see langword="null"/> for the process's own
        /// elevation.
        /// </summary>
        private readonly bool? _previous;

        /// <summary>
        /// Whether this scope has already restored <see cref="_previous"/>.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Creates a scope that restores <paramref name="previous"/> when disposed.
        /// </summary>
        /// <param name="previous">
        /// The override in effect before this scope opened, or <see langword="null"/> for the
        /// process's own elevation.
        /// </param>
        public ElevationScope(bool? previous) => _previous = previous;

        /// <summary>
        /// Restores the elevation in effect before this scope opened, the first time it is called.
        /// </summary>
        public void Dispose()
        {
            // As for HostLocationsScope: a second restore would reinstate a closed outer scope.
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _elevationOverride.Value = _previous;
        }
    }

    /// <summary>
    /// The query an <see cref="OverrideFinalPathQuery"/> scope puts in place of
    /// <see cref="FinalPathQuery"/>.
    /// </summary>
    /// <param name="Query">The query, or <see langword="null"/> for a host that installs none.</param>
    private sealed record FinalPathQueryOverride(Func<SafeFileHandle, string?>? Query);

    /// <summary>
    /// The scope <see cref="OverrideFinalPathQuery"/> returns.
    /// </summary>
    private sealed class FinalPathQueryScope : IDisposable
    {
        /// <summary>
        /// The override this scope reinstates, or <see langword="null"/> for <see cref="FinalPathQuery"/>.
        /// </summary>
        private readonly FinalPathQueryOverride? _previous;

        /// <summary>
        /// Whether this scope has already restored <see cref="_previous"/>.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Creates a scope that restores <paramref name="previous"/> when disposed.
        /// </summary>
        /// <param name="previous">
        /// The override in effect before this scope opened, or <see langword="null"/> for
        /// <see cref="FinalPathQuery"/>.
        /// </param>
        public FinalPathQueryScope(FinalPathQueryOverride? previous) => _previous = previous;

        /// <summary>
        /// Restores the query in effect before this scope opened, the first time it is called.
        /// </summary>
        public void Dispose()
        {
            // As for HostLocationsScope: a second restore would reinstate a closed outer scope.
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _finalPathQueryOverride.Value = _previous;
        }
    }

    /// <summary>
    /// The opener an <see cref="OverrideFolderOpener"/> scope puts in place of <see cref="FolderOpener"/>.
    /// </summary>
    /// <param name="Opener">The opener, or <see langword="null"/> for a host that installs none.</param>
    private sealed record FolderOpenerOverride(Func<string, SafeFileHandle?>? Opener);

    /// <summary>
    /// The scope <see cref="OverrideFolderOpener"/> returns.
    /// </summary>
    private sealed class FolderOpenerScope : IDisposable
    {
        /// <summary>
        /// The override this scope reinstates, or <see langword="null"/> for <see cref="FolderOpener"/>.
        /// </summary>
        private readonly FolderOpenerOverride? _previous;

        /// <summary>
        /// Whether this scope has already restored <see cref="_previous"/>.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Creates a scope that restores <paramref name="previous"/> when disposed.
        /// </summary>
        /// <param name="previous">
        /// The override in effect before this scope opened, or <see langword="null"/> for <see cref="FolderOpener"/>.
        /// </param>
        public FolderOpenerScope(FolderOpenerOverride? previous) => _previous = previous;

        /// <summary>
        /// Restores the opener in effect before this scope opened, the first time it is called.
        /// </summary>
        public void Dispose()
        {
            // As for HostLocationsScope: a second restore would reinstate a closed outer scope.
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _folderOpenerOverride.Value = _previous;
        }
    }
}

/// <summary>
/// Holds a data folder, and optionally subfolders of it, in place while one file operation runs in
/// them, after proving that each is a plain folder and, where required, a protected one.
/// </summary>
/// <remarks>
/// <para>
/// Why: an elevated RAMQuick writes in folders where a non-elevated user may have planted a
/// junction or symbolic link, which would carry its writes into a folder that user cannot write
/// to. Checking a folder's attributes before a write is not enough, because the folder can be
/// swapped for a link between the check and the write.
/// </para>
/// <para>
/// How: each folder receives an anchor, a uniquely named <c>.write-probe-&lt;guid&gt;.tmp</c> file
/// opened delete-on-close and written with a random value. Windows refuses to rename or delete a
/// folder, or any folder above it, while a file in it is open, so while the anchor is open the
/// path keeps naming the folder that holds it. The folder is checked again once the anchor exists,
/// and the anchor is read back through the folder's path: a matching value proves that the path
/// names the folder the anchor pins. In an elevated process the anchor is readable by
/// administrators only, so a non-elevated user cannot copy its value into a decoy.
/// </para>
/// <para>
/// A protected folder (<see cref="ProtectedFolder"/>) lets no non-administrator create, replace or
/// delete an entry in it, so a name checked there keeps naming the same file while the pin holds
/// the folder. The data folders an elevated process resolves are protected, and
/// <see cref="ForFolder"/> re-checks their protection on every operation.
/// </para>
/// <para>
/// A link above the pinned root is not held in place by the anchor, which pins only the folders it
/// actually lies in. Without protection, such links are accepted: the app folder or the user's
/// profile may be reached through a junction, and refusing those would refuse ordinary
/// installations. With protection, every link above the root must be one only administrators can
/// change (<see cref="ExpectedFinalPath"/>), and Windows must report each anchor, through its open
/// handle, at the path those links lead to.
/// </para>
/// <para>
/// Verified folder handles: an anchor is created by path, so a folder swapped for a link just before
/// it would first receive the anchor wherever the link leads. An elevated RAMQuick therefore holds
/// each folder open and verified while it creates or deletes anything in it: with protection, or
/// with an expected location as for a root about to be replaced, each folder is first opened through
/// <see cref="DataDirectory.FolderOpener"/>, which keeps it from being renamed or deleted and the
/// folders above it from being moved, and <see cref="DataDirectory.FinalPathQuery"/> must report the
/// held folder at its expected path before its anchor is created. The pin keeps those handles until
/// it is disposed. <see cref="HoldParent"/> does the same for the folder above a root, and for each
/// missing folder above it, which it creates one level at a time inside the held level above.
/// </para>
/// <para>
/// Pins are taken per operation, so nothing stays open between operations; a file an operation
/// leaves open, such as the journal or a run's <c>.log</c>, keeps pinning its folders by itself.
/// </para>
/// </remarks>
internal sealed class DataFolderPin : IDisposable
{
    private const string AnchorFilePrefix = ".write-probe-";
    private const string AnchorFileExtension = ".tmp";
    private const int NonceLength = 16;

    // Windows follows at most 63 links while opening one path, so a longer chain cannot be opened either.
    private const int MaxLinkHops = 63;

    private static readonly char[] _separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    private readonly List<(string Path, FileStream Stream)> _anchors;

    // The verified folders held open while the pin lasts, each from before its anchor was created.
    private readonly List<SafeFileHandle> _folders;

    private DataFolderPin(List<(string Path, FileStream Stream)> anchors, List<SafeFileHandle> folders)
    {
        _anchors = anchors;
        _folders = folders;
    }

    /// <summary>
    /// Pins <paramref name="rootPath"/> and the named subfolders of it.
    /// </summary>
    /// <param name="rootPath">The folder to pin first; it must exist.</param>
    /// <param name="requireProtection">
    /// Whether every pinned folder must be a protected folder, as for a root an elevated process
    /// resolved. A subfolder created then is created protected.
    /// </param>
    /// <param name="createSubfolders">
    /// Whether a missing subfolder is created, inside the pinned root only.
    /// </param>
    /// <param name="subfolderNames">The names of the subfolders to pin, in order.</param>
    /// <returns>The pin; dispose it once the operation ends.</returns>
    /// <exception cref="IOException">
    /// A folder is missing, is a file, link or junction, or could not receive its anchor; or, when
    /// protection is required, a folder is not protected, a link above the root can be changed by
    /// another account, or a folder cannot be held open and verified.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">A folder denies the anchor's creation.</exception>
    internal static DataFolderPin Acquire(
        string rootPath,
        bool requireProtection,
        bool createSubfolders,
        params string[] subfolderNames) =>
        Acquire(
            rootPath,
            requireProtection,
            // Worked out before any anchor exists, from the path and the links only administrators can
            // change, so a folder swapped in during the pin cannot carry the expectation with it.
            requireProtection && OperatingSystem.IsWindows() ? ExpectedFinalPath(rootPath) : null,
            createSubfolders,
            subfolderNames);

    /// <summary>
    /// Pins <paramref name="rootPath"/> and the named subfolders of it, verifying each folder where
    /// <paramref name="expectedRoot"/> says it lies before anything is created in it.
    /// </summary>
    /// <param name="rootPath">The folder to pin first; it must exist.</param>
    /// <param name="requireProtection">
    /// Whether every pinned folder must be a protected folder. A subfolder created then is created protected.
    /// </param>
    /// <param name="expectedRoot">
    /// Where Windows must find <paramref name="rootPath"/>, as <see cref="ExpectedFinalPath"/> returned it, so that
    /// each folder is held open and verified there before its anchor is created, protected or not; or
    /// <see langword="null"/> for no verification, which <paramref name="requireProtection"/> refuses.
    /// </param>
    /// <param name="createSubfolders">
    /// Whether a missing subfolder is created, inside the pinned root only.
    /// </param>
    /// <param name="subfolderNames">The names of the subfolders to pin, in order.</param>
    /// <returns>The pin; dispose it once the operation ends.</returns>
    /// <exception cref="IOException">As for <see cref="Acquire(string, bool, bool, string[])"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder denies the anchor's creation.</exception>
    internal static DataFolderPin Acquire(
        string rootPath,
        bool requireProtection,
        string? expectedRoot,
        bool createSubfolders,
        params string[] subfolderNames)
    {
        var anchors = new List<(string Path, FileStream Stream)>(subfolderNames.Length + 1);
        var folders = new List<SafeFileHandle>(subfolderNames.Length + 1);
        try
        {
            Anchor(rootPath, requireProtection, expectedRoot, anchors, folders);
            foreach (string name in subfolderNames)
            {
                string folder = Path.Combine(rootPath, name);
                if (createSubfolders && TryGetAttributes(folder) is null)
                {
                    CreateSubfolder(folder, requireProtection);
                }

                Anchor(folder, requireProtection, expectedRoot is null ? null : Path.Combine(expectedRoot, name), anchors, folders);
            }

            return new DataFolderPin(anchors, folders);
        }
        catch
        {
            CloseAll(anchors, folders);
            throw;
        }
    }

    /// <summary>
    /// Pins <paramref name="folderPath"/> and the folder above it, requiring protection when that
    /// parent is a root <see cref="DataDirectory.Resolve()"/> established as a protected root.
    /// </summary>
    /// <param name="folderPath">
    /// The folder an operation writes in, for example a data folder's <c>logs</c> or
    /// <c>journal</c>; a drive root is pinned alone.
    /// </param>
    /// <param name="createFolder">Whether a missing <paramref name="folderPath"/> is created.</param>
    /// <returns>The pin; dispose it once the operation ends.</returns>
    /// <exception cref="IOException">As for <see cref="Acquire(string, bool, bool, string[])"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">As for <see cref="Acquire(string, bool, bool, string[])"/>.</exception>
    internal static DataFolderPin ForFolder(string folderPath, bool createFolder)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        string? parent = Path.GetDirectoryName(folder);
        return parent is null
            ? Acquire(folder, DataDirectory.RequiresProtection(folder), createSubfolders: false)
            : Acquire(parent, DataDirectory.RequiresProtection(parent), createFolder, Path.GetFileName(folder));
    }

    /// <summary>
    /// Holds the folder above <paramref name="folder"/> open, after verifying through the open handle that Windows
    /// finds it where <paramref name="expectedFolder"/> places it, and creates each missing folder on the way first, one
    /// level at a time, each inside the level above it while that level is held and verified.
    /// </summary>
    /// <param name="folder">The folder whose parent is held; neither need exist.</param>
    /// <param name="expectedFolder">
    /// Where Windows must find <paramref name="folder"/>, as <see cref="ExpectedFinalPath"/> returned it.
    /// </param>
    /// <returns>
    /// The handle that holds the parent, which keeps it from being renamed or deleted, and the folders above it from
    /// being moved, until the caller disposes it; or <see langword="null"/> when <paramref name="folder"/> is a volume
    /// root, above which nothing can be swapped.
    /// </returns>
    /// <remarks>
    /// The nearest existing folder above is held first. A link a non-administrator plants under a missing level's name
    /// in a held level is not followed by the creation, which leaves an existing name alone, and is refused when that
    /// level is held, so no level is ever created outside the verified folder above it. A created level inherits the
    /// access of the folder above it, as any folder would.
    /// </remarks>
    /// <exception cref="IOException">
    /// A level could not be created, is a file, or could not be held open and verified where it was expected; or the
    /// folder's volume does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// A level denies reading its attributes or the creation of the next level.
    /// </exception>
    internal static SafeFileHandle? HoldParent(string folder, string expectedFolder)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string? level = Path.GetDirectoryName(fullPath);
        if (level is null)
        {
            return null;
        }

        string expectedLevel = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(expectedFolder))
            ?? throw new IOException(
                $"'{fullPath}' cannot be verified: Windows is expected to find it at the volume root '{expectedFolder}'.");

        // A missing level holds no link, so ExpectedFinalPath placed it under its own name below the level above it.
        Stack<string> missing = new();
        while (TryGetAttributes(level) is null)
        {
            string? above = Path.GetDirectoryName(level);
            string? expectedAbove = Path.GetDirectoryName(expectedLevel);
            if (above is null || expectedAbove is null)
            {
                throw new DirectoryNotFoundException($"The volume of '{fullPath}' does not exist.");
            }

            missing.Push(Path.GetFileName(level));
            level = above;
            expectedLevel = expectedAbove;
        }

        SafeFileHandle held = HoldFolder(level, expectedLevel);
        try
        {
            while (missing.TryPop(out string? name))
            {
                level = Path.Combine(level, name);
                expectedLevel = Path.Combine(expectedLevel, name);
                Directory.CreateDirectory(level);

                // The new level is held before the one above it is released, so the path to it never goes unheld.
                SafeFileHandle next = HoldFolder(level, expectedLevel);
                held.Dispose();
                held = next;
            }

            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the attributes of the entry at <paramref name="path"/> itself, never of what a link
    /// there points to, or <see langword="null"/> when there is no entry.
    /// </summary>
    /// <param name="path">The entry's path.</param>
    /// <returns>The entry's attributes, or <see langword="null"/>.</returns>
    /// <exception cref="IOException">The attributes could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The entry denies reading its attributes.</exception>
    internal static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="attributes"/> describe a plain folder: a directory that is
    /// not a link or junction.
    /// </summary>
    /// <param name="attributes">An entry's own attributes.</param>
    /// <returns><see langword="true"/> for a plain folder.</returns>
    internal static bool IsPlainFolder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    /// <summary>
    /// Returns whether the entry at <paramref name="path"/> is a link, junction or other reparse
    /// point.
    /// </summary>
    /// <param name="path">The entry's path.</param>
    /// <returns><see langword="true"/> when the entry exists and is a reparse point.</returns>
    /// <exception cref="IOException">The attributes could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The entry denies reading its attributes.</exception>
    internal static bool IsLink(string path) =>
        TryGetAttributes(path) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Throws unless <paramref name="folder"/> is a plain folder.
    /// </summary>
    /// <param name="folder">The folder's path.</param>
    /// <exception cref="IOException">
    /// The folder is missing, is a file, or is a link or junction.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The folder denies reading its attributes.</exception>
    internal static void RequirePlainFolder(string folder)
    {
        FileAttributes attributes = File.GetAttributes(folder);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"'{folder}' is a file, not a folder.");
        }

        if (!IsPlainFolder(attributes))
        {
            throw NotPlainFolder(folder);
        }
    }

    /// <summary>
    /// Checks the location of a file about to be read without pinning it, so that the read needs no
    /// write access.
    /// </summary>
    /// <param name="filePath">The file's path.</param>
    /// <returns>
    /// <see langword="false"/> when the file's folder does not exist; otherwise
    /// <see langword="true"/>.
    /// </returns>
    /// <exception cref="IOException">
    /// The file's folder or the folder above it is not a plain folder, or the file is a link.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">An entry denies reading its attributes.</exception>
    internal static bool RequirePlainLocation(string filePath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (folder is null)
        {
            return true;
        }

        if (TryGetAttributes(folder) is not { } folderAttributes)
        {
            return false;
        }

        if (!IsPlainFolder(folderAttributes))
        {
            throw NotPlainFolder(folder);
        }

        if (Path.GetDirectoryName(folder) is { } parent
            && TryGetAttributes(parent) is { } parentAttributes
            && !IsPlainFolder(parentAttributes))
        {
            throw NotPlainFolder(parent);
        }

        if (IsLink(filePath))
        {
            throw LinkedFile(filePath);
        }

        return true;
    }

    /// <summary>
    /// Returns the path Windows reports for <paramref name="folder"/> once every junction and symbolic
    /// link above it is resolved, after proving that no account other than SYSTEM, Administrators and
    /// TrustedInstaller can replace or retarget any of those links.
    /// </summary>
    /// <param name="folder">The folder's path; it and the folders above it need not exist yet.</param>
    /// <returns>
    /// The full path of <paramref name="folder"/> with each link above it replaced by its target. The
    /// folder itself is not resolved: a pin requires it to be a plain folder.
    /// </returns>
    /// <remarks>
    /// A link counts as unchangeable when it is a protected folder (<see cref="ProtectedFolder.IsProtected"/>
    /// reads the link's own access control, not its target's) and lies in a protected folder, so it can
    /// neither be retargeted nor deleted and created again. Unlike a folder, a link is not held in place by
    /// a file open below its target, so this is what keeps it in place while a pin holds the folders.
    /// Each target is checked from its own volume down, so a link above a target is held to the same rule.
    /// Reparse points that are not links, such as cloud-file placeholders, open in place and are accepted.
    /// </remarks>
    /// <exception cref="IOException">
    /// A link above the folder can be changed by another account, points to a location that does not
    /// exist, or is one of more than 63 links on the way.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">An entry above the folder denies reading its attributes or link.</exception>
    [SupportedOSPlatform("windows")]
    internal static string ExpectedFinalPath(string folder)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string? volume = Path.GetPathRoot(fullPath);
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(volume) || parent is null)
        {
            return fullPath;
        }

        string current = volume;
        Queue<string> pending = new(NamesBelow(parent, volume));
        int hops = 0;
        while (pending.TryDequeue(out string? name))
        {
            string next = Path.Combine(current, name);
            if (TrustedLinkTarget(next, current) is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new IOException($"'{fullPath}' is reached through more than {MaxLinkHops} links.");
            }

            string? targetVolume = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(targetVolume))
            {
                throw new IOException($"'{next}' points to '{target}', which RAMQuick cannot check.");
            }

            pending = new Queue<string>(NamesBelow(target, targetVolume).Concat(pending));
            current = targetVolume;
        }

        return Path.Combine(current, Path.GetFileName(fullPath));
    }

    /// <summary>
    /// Removes the entry at <paramref name="path"/> itself when it is a file or a link of either
    /// kind. Does nothing when there is no entry or the entry is a plain folder, which RAMQuick never
    /// creates at a file's name and leaves to whoever did.
    /// </summary>
    /// <param name="path">The entry's path.</param>
    /// <exception cref="IOException">The entry could not be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The entry denies its removal.</exception>
    internal static void RemoveFileOrLink(string path)
    {
        if (TryGetAttributes(path) is not { } attributes)
        {
            return;
        }

        // Neither call follows a link: each removes the entry itself.
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
        }
        else if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
        }
    }

    /// <summary>
    /// Returns the exception that reports a file found to be a link where RAMQuick writes or reads
    /// its own file.
    /// </summary>
    /// <param name="filePath">The file's path.</param>
    /// <returns>The exception to throw.</returns>
    internal static IOException LinkedFile(string filePath) =>
        new($"'{filePath}' is a link: RAMQuick does not read or write its files through a link or junction.");

    /// <summary>
    /// Returns whether <paramref name="path"/> names one of this pin's anchors.
    /// </summary>
    /// <param name="path">A full path inside a pinned folder.</param>
    /// <returns><see langword="true"/> for an anchor of this pin.</returns>
    internal bool IsAnchor(string path) =>
        _anchors.Exists(anchor => string.Equals(anchor.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Closes the anchors, which deletes them, and the folders held open, and so releases the folders. Later calls do
    /// nothing.
    /// </summary>
    public void Dispose() => CloseAll(_anchors, _folders);

    private static void Anchor(
        string folder,
        bool requireProtection,
        string? expectedFolder,
        List<(string Path, FileStream Stream)> anchors,
        List<SafeFileHandle> folders)
    {
        RequirePlainFolder(folder);

        // Held and verified before the anchor exists, so the anchor can only be created in the folder Windows reported
        // where it was expected: a folder swapped for a link before the hold is refused here, and one held cannot be
        // swapped. Checking only through the anchor would first create it wherever a swapped folder led.
        bool verified = requireProtection || expectedFolder is not null;
        if (verified)
        {
            folders.Add(HoldFolder(folder, expectedFolder));
        }

        string anchorPath = Path.Combine(folder, AnchorFilePrefix + Guid.NewGuid().ToString("N") + AnchorFileExtension);
        FileStream anchor = CreateAnchorFile(anchorPath);
        anchors.Add((anchorPath, anchor));

        // First, so that every check by path below reads the folder the anchor pins.
        if (verified)
        {
            RequireLocation(anchorPath, anchor, expectedFolder);
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        anchor.Write(nonce);
        anchor.Flush(flushToDisk: true);

        // The folder could have been swapped between the first check and the anchor's creation.
        RequirePlainFolder(folder);
        if (!ReadsBack(anchorPath, nonce))
        {
            throw NotPlainFolder(folder);
        }

        if (requireProtection && (!OperatingSystem.IsWindows() || !ProtectedFolder.IsProtected(folder)))
        {
            throw new IOException(
                $"'{folder}' can be changed by accounts other than administrators: an elevated RAMQuick "
                + "uses only a data folder that only administrators can change.");
        }
    }

    // Within the pinned root only, so the new folder cannot land anywhere else.
    private static void CreateSubfolder(string folder, bool requireProtection)
    {
        if (requireProtection && OperatingSystem.IsWindows())
        {
            ProtectedFolder.Create(folder);
        }
        else
        {
            Directory.CreateDirectory(folder);
        }
    }

    private static FileStream CreateAnchorFile(string anchorPath) =>
        OperatingSystem.IsWindows() && DataDirectory.IsPrivilegedProcess
            ? ProtectedFolder.CreateAnchor(anchorPath)
            : new FileStream(anchorPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1, FileOptions.DeleteOnClose);

    private static bool ReadsBack(string anchorPath, byte[] nonce)
    {
        try
        {
            using FileStream reader = new(
                anchorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
            byte[] content = new byte[NonceLength + 1];
            int read = reader.ReadAtLeast(content, content.Length, throwOnEndOfStream: false);
            return read == NonceLength && content.AsSpan(0, NonceLength).SequenceEqual(nonce);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    // The open anchor holds every real folder from the volume root down to it in place, and only
    // administrators can change the links ExpectedFinalPath resolved, so a final path that matches proves
    // that the folder's path names the pinned folder until the pin is released. Checks by path alone
    // cannot prove that: the entry can be swapped between any two of them.
    private static void RequireLocation(string anchorPath, FileStream anchor, string? expectedFolder)
    {
        string folder = Path.GetDirectoryName(anchorPath) ?? anchorPath;
        Func<SafeFileHandle, string?>? query = DataDirectory.EffectiveFinalPathQuery;
        if (query is null || expectedFolder is null)
        {
            throw new IOException(
                $"'{folder}' cannot be verified through an open file: an elevated RAMQuick uses only a data folder "
                + "it can verify.");
        }

        string expected = Path.Combine(expectedFolder, Path.GetFileName(anchorPath));
        string? actual = query(anchor.SafeFileHandle);
        if (actual is null || !IsSameLocation(actual, expected))
        {
            string reached = actual is null ? "a location Windows does not report" : $"'{Path.GetDirectoryName(actual)}'";
            throw new IOException(
                $"'{folder}' leads to {reached} instead of '{expectedFolder}': an elevated RAMQuick does not write "
                + "through a folder that was swapped for a link or junction.");
        }
    }

    // Opens the folder and proves through the open handle that it is the folder expectedFolder names. While the handle
    // stays open, Windows refuses to rename or delete the folder and to move any folder above it, and only
    // administrators can change the links ExpectedFinalPath resolved, so the folder's path keeps naming the verified
    // folder: a name created or deleted through it is created or deleted there.
    private static SafeFileHandle HoldFolder(string folder, string? expectedFolder)
    {
        Func<string, SafeFileHandle?>? opener = DataDirectory.EffectiveFolderOpener;
        Func<SafeFileHandle, string?>? query = DataDirectory.EffectiveFinalPathQuery;
        if (opener is null || query is null || expectedFolder is null)
        {
            throw new IOException(
                $"'{folder}' cannot be held open and verified: an elevated RAMQuick uses only a data folder it can "
                + "verify.");
        }

        SafeFileHandle? handle = opener(folder);
        if (handle is null || handle.IsInvalid)
        {
            handle?.Dispose();
            throw new IOException(
                $"'{folder}' could not be held open: an elevated RAMQuick uses only a data folder it can verify.");
        }

        try
        {
            string? actual = query(handle);
            if (actual is null || !IsSameLocation(actual, expectedFolder))
            {
                string reached = actual is null ? "a location Windows does not report" : $"'{actual}'";
                throw new IOException(
                    $"'{folder}' leads to {reached} instead of '{expectedFolder}': an elevated RAMQuick does not create "
                    + "or delete anything in a folder that was swapped for a link or junction.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // Windows reports names in their on-disk case, and a volume root keeps its separator.
    private static bool IsSameLocation(string actual, string expected) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(actual),
            Path.TrimEndingDirectorySeparator(expected),
            StringComparison.OrdinalIgnoreCase);

    // Returns the full path of the link's target, or null when the entry is not a junction or symbolic
    // link. parent is the folder that holds the entry.
    [SupportedOSPlatform("windows")]
    private static string? TrustedLinkTarget(string path, string parent)
    {
        if (TryGetAttributes(path) is not { } attributes || (attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        // Only junctions and symbolic links redirect a path and report a target; other reparse points
        // open in place.
        string? linkTarget = new DirectoryInfo(path).LinkTarget;
        if (linkTarget is null)
        {
            return null;
        }

        // The link's own access control decides who can retarget it, and its folder's who can delete it
        // and create another under its name.
        if (!ProtectedFolder.IsProtected(path) || !ProtectedFolder.IsProtected(parent))
        {
            throw new IOException(
                $"'{path}' is a link or junction that accounts other than administrators can change: an elevated "
                + "RAMQuick does not use a data folder reached through one.");
        }

        // A relative symbolic link is relative to its folder. A target that does not exist, such as a
        // volume mount point's volume name, cannot be checked further.
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(linkTarget, parent));
        if (TryGetAttributes(target) is null)
        {
            throw new IOException($"'{path}' points to '{target}', which RAMQuick cannot check.");
        }

        return target;
    }

    private static string[] NamesBelow(string path, string volume) =>
        path[volume.Length..].Split(_separators, StringSplitOptions.RemoveEmptyEntries);

    private static IOException NotPlainFolder(string folder) =>
        new($"'{folder}' is not a plain folder: RAMQuick does not use a data folder, journal folder or "
            + "logs folder that is a link or junction.");

    // The anchors first, while their folders are still held, then the folders, innermost first.
    private static void CloseAll(List<(string Path, FileStream Stream)> anchors, List<SafeFileHandle> folders)
    {
        for (int index = anchors.Count - 1; index >= 0; index--)
        {
            anchors[index].Stream.Dispose();
        }

        anchors.Clear();
        for (int index = folders.Count - 1; index >= 0; index--)
        {
            folders[index].Dispose();
        }

        folders.Clear();
    }
}

/// <summary>
/// The access control of a protected data folder: owned by Administrators, with an access list
/// that inherits nothing from its parent and lets only SYSTEM and Administrators change the folder
/// or its entries, while the current account keeps read access.
/// </summary>
/// <remarks>
/// A non-elevated process of the same account then cannot plant a link inside the folder, because
/// it cannot create, rename or delete entries there, nor change the access list, because it does
/// not own the folder.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ProtectedFolder
{
    // GENERIC_ALL and GENERIC_WRITE, which access rules can carry as raw bits.
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    // The rights that let a principal change a folder, its entries or its access control.
    private const int ChangeRights = (int)(FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership)
        | GenericAll
        | GenericWrite;

    private static readonly SecurityIdentifier _administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier _localSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier _trustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Creates the folder at <paramref name="path"/> with the protected access control. Does
    /// nothing when an entry already has its name, which <see cref="IsProtected"/> then detects.
    /// </summary>
    /// <param name="path">The folder's path; its parent must exist for the folder alone to be created.</param>
    /// <exception cref="IOException">The folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The parent denies the creation.</exception>
    internal static void Create(string path) => new DirectoryInfo(path).Create(FolderSecurity());

    /// <summary>
    /// Returns whether the folder at <paramref name="path"/> is owned by SYSTEM, Administrators or
    /// TrustedInstaller and no other account is allowed to change it or its entries.
    /// </summary>
    /// <param name="path">The folder's path.</param>
    /// <returns>
    /// <see langword="true"/> for a protected folder; <see langword="false"/> otherwise, or when its
    /// access control cannot be read.
    /// </returns>
    /// <remarks>
    /// The check reads what the access list grants, not how it was written, so a folder that an
    /// administrator created under Program Files, where Users can only read, also counts.
    /// </remarks>
    internal static bool IsProtected(string path)
    {
        DirectorySecurity security;
        try
        {
            security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            bool appliesToFolder = (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
            bool grantsChange = ((int)rule.FileSystemRights & ChangeRights) != 0;
            if (rule.AccessControlType == AccessControlType.Allow
                && appliesToFolder
                && grantsChange
                && (rule.IdentityReference is not SecurityIdentifier grantee || !IsTrusted(grantee)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a pin's anchor file at <paramref name="path"/>, open for writing and deleted on close,
    /// readable by SYSTEM and Administrators only.
    /// </summary>
    /// <param name="path">The anchor's path, which must not exist.</param>
    /// <returns>The open anchor.</returns>
    /// <exception cref="IOException">The file could not be created, for example because it exists.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder denies the creation.</exception>
    internal static FileStream CreateAnchor(string path)
    {
        FileSecurity security = new();
        security.SetOwner(_administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(_localSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(_administrators, FileSystemRights.FullControl, AccessControlType.Allow));

        // WriteData for the value and its flush; Delete, which delete-on-close requires.
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.Delete,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.DeleteOnClose,
            security);
    }

    private static DirectorySecurity FolderSecurity()
    {
        const InheritanceFlags Inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        DirectorySecurity security = new();
        security.SetOwner(_administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            _localSystem, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            _administrators, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));

        // The account that launched RAMQuick can still read its logs and summaries.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } user && !IsTrusted(user))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                Inherited,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static bool IsTrusted(SecurityIdentifier principal) =>
        principal == _administrators || principal == _localSystem || principal == _trustedInstaller;
}
