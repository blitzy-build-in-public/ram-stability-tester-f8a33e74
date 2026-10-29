using System.Security;

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
/// creates a uniquely named <c>.write-probe-&lt;guid&gt;.tmp</c> file in the root, writes one byte,
/// flushes it to the device and closes it. The file is opened delete-on-close, so it is gone
/// once the probe ends, even if the process dies during the probe. An unwritable location is an
/// expected condition: the probe reports it as <see langword="false"/> and never throws.
/// </para>
/// <para>
/// Instances are immutable and safe to share across threads. This type writes nothing outside
/// the candidate roots and performs no I/O beyond the probe and the subfolder creation; the
/// journal and the logs are written by <c>FileJournalStorage</c> and <c>ResultsLog</c>.
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
    private const string ProbeFilePrefix = ".write-probe-";
    private const string ProbeFileExtension = ".tmp";

    private DataDirectory(string rootPath, DataDirectoryKind kind)
    {
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
    /// probe rather than skipped. Call this off the UI thread: the probe flushes to the device.
    /// </remarks>
    public static DataDirectory? Resolve() =>
        Resolve(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify));

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
    /// The first candidate that passes the write probe, with <see cref="Kind"/> set to
    /// <see cref="DataDirectoryKind.AppFolder"/> or <see cref="DataDirectoryKind.LocalAppData"/>;
    /// <see langword="null"/> when no candidate passes.
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
    /// create it is an error and is thrown, unlike in <see cref="Resolve()"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootPath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootPath"/> is empty, white space or not a valid path.
    /// </exception>
    /// <exception cref="IOException">
    /// A subfolder could not be created, for example because a file has its name.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the subfolders.</exception>
    public static DataDirectory FromRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        DataDirectory directory = new(Path.GetFullPath(rootPath), DataDirectoryKind.Custom);
        Directory.CreateDirectory(directory.JournalDirectory);
        Directory.CreateDirectory(directory.LogsDirectory);
        return directory;
    }

    /// <summary>
    /// Re-runs the write probe on <see cref="RootPath"/>, recreating the root and its
    /// <c>journal</c> and <c>logs</c> subfolders if they are missing.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a file could be created, written and flushed in the root;
    /// otherwise <see langword="false"/>. Never throws for an unwritable location.
    /// </returns>
    /// <remarks>
    /// The run controller calls this at Start, so a folder that became unwritable after launch
    /// refuses the run instead of failing its first journal write. Call it off the UI thread: the
    /// probe flushes to the device.
    /// </remarks>
    public bool CanWrite() => TryPrepare(RootPath);

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

        return TryPrepare(rootPath) ? new DataDirectory(rootPath, kind) : null;
    }

    private static bool TryPrepare(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, JournalFolderName));
            Directory.CreateDirectory(Path.Combine(root, LogsFolderName));

            string probePath = Path.Combine(
                root,
                ProbeFilePrefix + Guid.NewGuid().ToString("N") + ProbeFileExtension);
            using (FileStream probe = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose))
            {
                probe.WriteByte(0);
                probe.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception exception) when (IsUnusableLocation(exception))
        {
            return false;
        }
    }

    // The failures Directory.CreateDirectory, FileStream and Path.GetFullPath raise for a
    // location that cannot hold the data folder: missing or read-only media, a file in place of a
    // folder, denied access, or a path the file system rejects. Anything else is a defect and
    // propagates.
    private static bool IsUnusableLocation(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException;
}
