using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace RamQuick.Logging.Tests;

/// <summary>
/// A unique, disposable folder under <c>%TEMP%\RamQuick.Logging.Tests</c> that every test touching
/// the file system creates for itself, so tests run in parallel without sharing a folder.
/// </summary>
/// <remarks>
/// Failure arrangements never change ACLs or attributes and never use a system folder. An
/// unwritable location is simulated with <see cref="CreateBlockingFile(string[])"/> in one of two
/// ways: the location lies beneath the file it creates, or that file replaces a folder the fixture
/// already holds, such as a data root or its <c>logs</c> subfolder. A planted link points to
/// <see cref="CreateLinkTarget"/>, a folder inside the fixture. Cleanup is different:
/// <see cref="Dispose"/> may clear ReadOnly flags on the folder and its entries so that its
/// recursive delete succeeds; the recursive delete removes a link itself, not what it points to.
/// </remarks>
internal sealed class TestDataRoot : IDisposable
{
    /// <summary>
    /// The name of the only file <see cref="CreateLinkTarget"/> puts in the link target.
    /// </summary>
    public const string SentinelFileName = "sentinel.txt";

    /// <summary>
    /// The content of <see cref="SentinelFileName"/>.
    /// </summary>
    public const string SentinelText = "a file outside the data folder";

    private const int DeleteAttempts = 5;
    private static readonly TimeSpan _deleteBackOff = TimeSpan.FromMilliseconds(100);

    // The first junction or symbolic link at or above the temporary folder, or null when it is reached without one.
    private static readonly Lazy<string?> _linkAboveTemp = new(() =>
    {
        for (string? folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            folder is not null;
            folder = System.IO.Path.GetDirectoryName(folder))
        {
            if (new DirectoryInfo(folder).LinkTarget is not null)
            {
                return folder;
            }
        }

        return null;
    });

    /// <summary>
    /// Creates the folder and a <see cref="DataDirectory"/> rooted in it.
    /// </summary>
    public TestDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "RamQuick.Logging.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Data = DataDirectory.FromRoot(Path);
    }

    /// <summary>
    /// Gets the full path of the folder, which exists until <see cref="Dispose"/>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the data folder rooted at <see cref="Path"/>, with its <c>journal</c> and <c>logs</c>
    /// subfolders created.
    /// </summary>
    public DataDirectory Data { get; }

    /// <summary>
    /// Combines <see cref="Path"/> with <paramref name="parts"/>.
    /// </summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>
    /// Creates an empty file at <see cref="Combine(string[])"/> of <paramref name="parts"/>,
    /// creating its parent folders, and returns its full path. No location at or beneath the
    /// returned path can become a folder, so a data folder placed there is unwritable.
    /// </summary>
    /// <remarks>
    /// A folder already at the path, such as the <c>logs</c> folder the constructor creates, is
    /// deleted with everything in it first, so the file replaces it. The deletion retries while
    /// Windows briefly holds a handle and throws once the attempts run out: unlike cleanup, an
    /// arrangement that did not happen must fail the test.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The path is <see cref="Path"/> itself or lies outside it.
    /// </exception>
    public string CreateBlockingFile(params string[] parts)
    {
        string filePath = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(Combine(parts)));
        string rootPrefix = Path + System.IO.Path.DirectorySeparatorChar;

        // The replacement deletes recursively, so it must never reach the root itself, a sibling
        // test's folder or anything else outside this fixture.
        if (filePath.Length <= rootPrefix.Length
            || !filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The blocking file '{filePath}' must lie inside '{Path}'.",
                nameof(parts));
        }

        for (int attempt = 1; Directory.Exists(filePath); attempt++)
        {
            try
            {
                Directory.Delete(filePath, recursive: true);
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException)
                && attempt < DeleteAttempts)
            {
                Thread.Sleep(_deleteBackOff * attempt);
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, []);
        return filePath;
    }

    /// <summary>
    /// Creates <c>link-target</c>, the folder a planted link points to, holding only
    /// <see cref="SentinelFileName"/>, and returns its full path. It stands for a folder outside the
    /// data folder that RAMQuick must never write to.
    /// </summary>
    public string CreateLinkTarget()
    {
        string target = Combine("link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(target, SentinelFileName), SentinelText);
        return target;
    }

    /// <summary>
    /// Asserts that <paramref name="target"/>, made by <see cref="CreateLinkTarget"/>, still holds
    /// only its unchanged sentinel file: nothing was written through a link to it.
    /// </summary>
    public static void AssertLinkTargetUntouched(string target)
    {
        string sentinel = System.IO.Path.Combine(target, SentinelFileName);
        Assert.Equal(new[] { sentinel }, Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(SentinelText, File.ReadAllText(sentinel));
    }

    /// <summary>
    /// Creates a directory junction at <paramref name="link"/> pointing to <paramref name="target"/>,
    /// as a non-elevated user can, with <c>mklink /J</c>.
    /// </summary>
    /// <remarks>Throws when the junction was not created: an arrangement that did not happen must fail.</remarks>
    public static void CreateJunction(string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"mklink /J '{link}' '{target}' failed ({process.ExitCode}): {output}");
        }
    }

    /// <summary>
    /// Creates a file symbolic link at <paramref name="link"/> pointing to <paramref name="target"/>,
    /// which may not exist, or skips the test when this host refuses symbolic links (they need
    /// <c>SeCreateSymbolicLinkPrivilege</c> or Developer Mode).
    /// </summary>
    public static void CreateFileSymbolicLinkOrSkip(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This host refuses to create symbolic links: {exception.Message}");
        }
    }

    /// <summary>
    /// Skips the test when <paramref name="elevated"/> and a junction or symbolic link lies at or above
    /// the temporary folder that holds every fixture. An elevated RAMQuick refuses each data folder below
    /// a link that a non-administrator can change, so a test expecting a protected root cannot run there.
    /// </summary>
    /// <remarks>
    /// Any link counts, whoever can change it, so the decision never rests on the code under test.
    /// </remarks>
    public static void SkipWhenProtectedRootsUnavailable(bool elevated)
    {
        string? link = _linkAboveTemp.Value;
        Assert.SkipWhen(
            elevated && link is not null,
            $"The temporary folder lies below the link '{link}', below which an elevated RAMQuick may use no data "
            + "folder; set TEMP to a folder reached without links to run this test.");
    }

    /// <summary>
    /// Creates the folder at <see cref="Combine(string[])"/> of <paramref name="parts"/> as RAMQuick creates a
    /// protected data folder, changeable by SYSTEM and Administrators only, and returns its full path. It
    /// stands for a folder that only administrators can write, such as one under Program Files.
    /// </summary>
    public string CreateProtectedFolder(params string[] parts)
    {
        string folder = Combine(parts);
        ProtectedFolder.Create(folder);
        Assert.True(ProtectedFolder.IsProtected(folder), $"'{folder}' was not created protected.");
        return folder;
    }

    /// <summary>
    /// Deletes the folder and everything in it, retrying while Windows briefly holds a handle.
    /// Cleanup never fails a test.
    /// </summary>
    public void Dispose()
    {
        for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                ClearReadOnlyAttributes(Path);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Antivirus scanners, the search indexer and handles closing on another thread can
                // hold a file for a moment after a test ends.
                if (attempt < DeleteAttempts)
                {
                    Thread.Sleep(_deleteBackOff * attempt);
                }
            }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        DirectoryInfo rootInfo = new(root);
        foreach (FileSystemInfo entry in rootInfo
            .EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Append(rootInfo))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}

/// <summary>
/// The final-path query and the folder opener this test assembly installs as <see cref="DataDirectory.FinalPathQuery"/>
/// and <see cref="DataDirectory.FolderOpener"/>, as the application installs RamQuick.Interop's, so that elevated tests
/// can establish protected roots.
/// </summary>
/// <remarks>
/// This project references no RamQuick.Interop, so it declares the same kernel32 calls itself. Tests that need
/// another query or opener, or none, open a <see cref="DataDirectory.OverrideFinalPathQuery"/> or
/// <see cref="DataDirectory.OverrideFolderOpener"/> scope rather than replace these process-wide ones, which parallel
/// tests rely on.
/// </remarks>
internal static unsafe partial class TestFinalPathQuery
{
    private const int MaxAttempts = 4;

    // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES: without data access the share mode below would hold nothing in place.
    private const uint FolderAccess = 0x00000081;

    // FILE_SHARE_READ | FILE_SHARE_WRITE, never FILE_SHARE_DELETE, so the folder cannot be renamed or deleted while open.
    private const uint FolderShare = 0x00000003;

    private const uint OpenExisting = 3;

    // FILE_FLAG_BACKUP_SEMANTICS, which opening a folder requires; links are followed.
    private const uint BackupSemantics = 0x02000000;

    /// <summary>
    /// Installs <see cref="GetFinalPath"/> and <see cref="OpenFolder"/> before any test runs.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        DataDirectory.FinalPathQuery = GetFinalPath;
        DataDirectory.FolderOpener = OpenFolder;
    }

    /// <summary>
    /// Opens the existing folder at <paramref name="path"/> as RamQuick.Interop's opener does, holding it in place while
    /// the handle stays open, or returns <see langword="null"/> when Windows refuses the open or the path names a file.
    /// </summary>
    internal static SafeFileHandle? OpenFolder(string path)
    {
        SafeFileHandle handle = CreateFile(path, FolderAccess, FolderShare, 0, OpenExisting, BackupSemantics, 0);
        if (!handle.IsInvalid && File.GetAttributes(handle).HasFlag(FileAttributes.Directory))
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    /// <summary>
    /// Returns the normalized DOS path Windows reports for the open file, without its <c>\\?\</c> prefix, or
    /// <see langword="null"/> when Windows reports none.
    /// </summary>
    internal static string? GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[260];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint length;
            fixed (char* path = buffer)
            {
                // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.
                length = GetFinalPathNameByHandle(handle, path, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                string finalPath = new(buffer, 0, (int)length);
                return finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + finalPath[8..]
                    : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
            }

            buffer = new char[length];
        }

        return null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}

/// <summary>
/// Tests for <see cref="DataDirectory"/>: resolution order and fallback, the null result that
/// refuses Start, the folder layout, and the write probe.
/// </summary>
public sealed class DataDirectoryTests
{
    [Fact]
    public void Resolve_WritableAppFolder_ReturnsAppDataFolder()
    {
        TestDataRoot.SkipWhenProtectedRootsUnavailable(Environment.IsPrivilegedProcess);
        using TestDataRoot root = new();
        string appFolder = root.Combine("app");
        string localAppDataRoot = root.Combine("local");

        DataDirectory? data = DataDirectory.Resolve(appFolder, localAppDataRoot);

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        Assert.Equal(Path.Combine(appFolder, "data"), data.RootPath);
        Assert.True(Directory.Exists(data.JournalDirectory));
        Assert.True(Directory.Exists(data.LogsDirectory));
        Assert.Empty(Directory.EnumerateFiles(data.RootPath));
        Assert.False(Directory.Exists(localAppDataRoot));
    }

    [Fact]
    public void Resolve_UnwritableAppFolder_FallsBackToLocalAppData()
    {
        TestDataRoot.SkipWhenProtectedRootsUnavailable(Environment.IsPrivilegedProcess);
        using TestDataRoot root = new();
        string appFolder = Path.Combine(root.CreateBlockingFile("blocker"), "app");
        string localAppDataRoot = root.Combine("local");

        DataDirectory? data = DataDirectory.Resolve(appFolder, localAppDataRoot);

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
        Assert.Equal(Path.Combine(localAppDataRoot, "RamQuick"), data.RootPath);
        Assert.True(Directory.Exists(data.JournalDirectory));
        Assert.True(Directory.Exists(data.LogsDirectory));
        Assert.Empty(Directory.EnumerateFiles(data.RootPath));
    }

    [Fact]
    public void Resolve_InvalidAppFolderPath_FallsBackToLocalAppData()
    {
        TestDataRoot.SkipWhenProtectedRootsUnavailable(Environment.IsPrivilegedProcess);
        using TestDataRoot root = new();
        string appFolder = root.Combine("app\0invalid");
        string localAppDataRoot = root.Combine("local");

        DataDirectory? data = DataDirectory.Resolve(appFolder, localAppDataRoot);

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
        Assert.Equal(Path.Combine(localAppDataRoot, "RamQuick"), data.RootPath);
    }

    [Fact]
    public void Resolve_NeitherWritable_ReturnsNull()
    {
        using TestDataRoot root = new();
        string appFolder = Path.Combine(root.CreateBlockingFile("app-blocker"), "app");
        string localAppDataRoot = Path.Combine(root.CreateBlockingFile("local-blocker"), "local");

        DataDirectory? data = DataDirectory.Resolve(appFolder, localAppDataRoot);

        Assert.Null(data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrBlankLocalAppDataAndUnwritableAppFolder_ReturnsNull(string? localAppDataRoot)
    {
        using TestDataRoot root = new();
        string appFolder = Path.Combine(root.CreateBlockingFile("blocker"), "app");

        DataDirectory? data = DataDirectory.Resolve(appFolder, localAppDataRoot);

        Assert.Null(data);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public void Resolve_NullOrBlankAppFolder_Throws(string? appFolder, Type expectedException)
    {
        using TestDataRoot root = new();

        Assert.Throws(expectedException, () => DataDirectory.Resolve(appFolder!, root.Combine("local")));
    }

    [Fact]
    public void Resolve_ParameterlessDefaults_AreBaseDirectoryAndUnverifiedLocalAppData()
    {
        // With no override open, Resolve() would probe beside this assembly or under the user's
        // profile, outside any test's own folder, so only its two inputs are read here.
        Assert.Equal(AppContext.BaseDirectory, DataDirectory.DefaultAppFolder);
        Assert.Equal(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            DataDirectory.DefaultLocalAppDataRoot);
    }

    [Theory]
    [InlineData(false, DataDirectoryKind.AppFolder)]
    [InlineData(true, DataDirectoryKind.LocalAppData)]
    public void Resolve_Parameterless_OverriddenHostLocations_ReturnsExactRootAndKind(
        bool appFolderBlocked,
        DataDirectoryKind expectedKind)
    {
        // The overrides take the host's shapes: AppContext.BaseDirectory always ends in a
        // separator, and the local application data folder never does.
        TestDataRoot.SkipWhenProtectedRootsUnavailable(Environment.IsPrivilegedProcess);
        using TestDataRoot root = new();
        string appFolder = appFolderBlocked
            ? Path.Combine(root.CreateBlockingFile("blocker"), "app")
            : root.Combine("app");
        string localAppDataRoot = root.Combine("local");
        string expectedRoot = expectedKind == DataDirectoryKind.AppFolder
            ? Path.Combine(appFolder, "data")
            : Path.Combine(localAppDataRoot, "RamQuick");

        DataDirectory? data;
        using (DataDirectory.OverrideHostLocations(
            appFolder + Path.DirectorySeparatorChar,
            localAppDataRoot))
        {
            data = DataDirectory.Resolve();
        }

        Assert.NotNull(data);
        Assert.Equal(expectedKind, data.Kind);
        Assert.Equal(expectedRoot, data.RootPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_Parameterless_OverriddenHostLocationsNeitherWritable_ReturnsNull(
        bool localAppDataUnknown)
    {
        using TestDataRoot root = new();
        string appFolder = Path.Combine(root.CreateBlockingFile("app-blocker"), "app");
        string? localAppDataRoot = localAppDataUnknown
            ? null
            : Path.Combine(root.CreateBlockingFile("local-blocker"), "local");

        DataDirectory? data;
        using (DataDirectory.OverrideHostLocations(
            appFolder + Path.DirectorySeparatorChar,
            localAppDataRoot))
        {
            data = DataDirectory.Resolve();
        }

        Assert.Null(data);
    }

    [Fact]
    public void OverrideHostLocations_OpenScope_IsInvisibleToOtherFlowsAndEndsAtDispose()
    {
        using TestDataRoot root = new();
        string appFolder = root.Combine("app") + Path.DirectorySeparatorChar;
        string localAppDataRoot = root.Combine("local");
        string hostLocalAppDataRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        string? otherFlowAppFolder = null;
        string? otherFlowLocalAppDataRoot = null;
        bool otherFlowEnded;

        using (DataDirectory.OverrideHostLocations(appFolder, localAppDataRoot))
        {
            Assert.Equal(appFolder, DataDirectory.DefaultAppFolder);
            Assert.Equal(localAppDataRoot, DataDirectory.DefaultLocalAppDataRoot);

            // A thread started without this flow's context stands for a parallel test or the
            // application: neither inherits the override.
            Thread otherFlow = new(() =>
            {
                otherFlowAppFolder = DataDirectory.DefaultAppFolder;
                otherFlowLocalAppDataRoot = DataDirectory.DefaultLocalAppDataRoot;
            });
            using (ExecutionContext.SuppressFlow())
            {
                otherFlow.Start();
            }

            otherFlowEnded = otherFlow.Join(TimeSpan.FromSeconds(30));
        }

        Assert.True(otherFlowEnded);
        Assert.Equal(AppContext.BaseDirectory, otherFlowAppFolder);
        Assert.Equal(hostLocalAppDataRoot, otherFlowLocalAppDataRoot);
        Assert.Equal(AppContext.BaseDirectory, DataDirectory.DefaultAppFolder);
        Assert.Equal(hostLocalAppDataRoot, DataDirectory.DefaultLocalAppDataRoot);
    }

    [Fact]
    public void OverrideHostLocations_NestedScopesDisposedAgain_RestoreEachEarlierLocation()
    {
        using TestDataRoot root = new();
        string outerAppFolder = root.Combine("outer-app") + Path.DirectorySeparatorChar;
        string outerLocalAppDataRoot = root.Combine("outer-local");
        string innerAppFolder = root.Combine("inner-app") + Path.DirectorySeparatorChar;

        IDisposable outer = DataDirectory.OverrideHostLocations(
            outerAppFolder,
            outerLocalAppDataRoot);
        IDisposable inner = DataDirectory.OverrideHostLocations(innerAppFolder, null);
        string innerAppFolderSeen = DataDirectory.DefaultAppFolder;
        string? innerLocalAppDataRootSeen = DataDirectory.DefaultLocalAppDataRoot;
        inner.Dispose();
        string outerAppFolderSeen = DataDirectory.DefaultAppFolder;
        string? outerLocalAppDataRootSeen = DataDirectory.DefaultLocalAppDataRoot;
        outer.Dispose();

        // Disposing the inner scope again must not reinstate the outer scope it once restored.
        inner.Dispose();

        Assert.Equal(innerAppFolder, innerAppFolderSeen);
        Assert.Null(innerLocalAppDataRootSeen);
        Assert.Equal(outerAppFolder, outerAppFolderSeen);
        Assert.Equal(outerLocalAppDataRoot, outerLocalAppDataRootSeen);
        Assert.Equal(AppContext.BaseDirectory, DataDirectory.DefaultAppFolder);
        Assert.Equal(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            DataDirectory.DefaultLocalAppDataRoot);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public void OverrideHostLocations_NullOrBlankAppFolder_ThrowsAndLeavesHostLocations(
        string? appFolder,
        Type expectedException)
    {
        using TestDataRoot root = new();

        Assert.Throws(
            expectedException,
            () => DataDirectory.OverrideHostLocations(appFolder!, root.Combine("local")));
        Assert.Equal(AppContext.BaseDirectory, DataDirectory.DefaultAppFolder);
    }

    [Fact]
    public void FromRoot_Paths_MatchLayout()
    {
        using TestDataRoot root = new();
        string rootPath = root.Combine("custom");

        DataDirectory data = DataDirectory.FromRoot(rootPath);

        Assert.Equal(DataDirectoryKind.Custom, data.Kind);
        Assert.Equal(rootPath, data.RootPath);
        Assert.Equal(Path.Combine(rootPath, "journal"), data.JournalDirectory);
        Assert.Equal(Path.Combine(rootPath, "journal", "active-run.jsonl"), data.JournalPath);
        Assert.Equal(Path.Combine(rootPath, "logs"), data.LogsDirectory);
        Assert.Equal(Path.Combine(rootPath, "logs", "app-errors.log"), data.AppErrorsLogPath);
        Assert.True(Directory.Exists(data.JournalDirectory));
        Assert.True(Directory.Exists(data.LogsDirectory));
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Fact]
    public void FromRoot_NonNormalisedPath_ReturnsFullPath()
    {
        using TestDataRoot root = new();

        DataDirectory data = DataDirectory.FromRoot(root.Combine("sub", "..", "custom"));

        Assert.Equal(root.Combine("custom"), data.RootPath);
    }

    [Fact]
    public void FromRoot_RootBeneathFile_ThrowsIOException()
    {
        using TestDataRoot root = new();
        string rootPath = Path.Combine(root.CreateBlockingFile("f"), "data");

        Assert.ThrowsAny<IOException>(() => DataDirectory.FromRoot(rootPath));
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public void FromRoot_NullOrBlankRoot_Throws(string? rootPath, Type expectedException)
    {
        Assert.Throws(expectedException, () => DataDirectory.FromRoot(rootPath!));
    }

    [Fact]
    public void CanWrite_WritableRoot_ReturnsTrue()
    {
        using TestDataRoot root = new();

        Assert.True(root.Data.CanWrite());
        Assert.Empty(Directory.EnumerateFiles(root.Data.RootPath));
    }

    [Fact]
    public void CanWrite_RootDeleted_RecreatesLayoutAndReturnsTrue()
    {
        using TestDataRoot root = new();
        DataDirectory data = DataDirectory.FromRoot(root.Combine("data"));
        Directory.Delete(data.RootPath, recursive: true);

        Assert.True(data.CanWrite());
        Assert.True(Directory.Exists(data.JournalDirectory));
        Assert.True(Directory.Exists(data.LogsDirectory));
    }

    [Fact]
    public void CanWrite_RootBeneathFile_ReturnsFalse()
    {
        // FromRoot throws for a root beneath a file (it creates the subfolders), so the data
        // folder is created first and a file then takes its parent folder's place.
        using TestDataRoot root = new();
        string parent = root.Combine("parent");
        DataDirectory data = DataDirectory.FromRoot(Path.Combine(parent, "data"));
        Directory.Delete(parent, recursive: true);
        root.CreateBlockingFile("parent");

        Assert.False(data.CanWrite());
    }

    [Fact]
    public void CanWrite_RootReplacedByFile_ReprobesAndReturnsFalse()
    {
        using TestDataRoot root = new();
        DataDirectory data = DataDirectory.FromRoot(root.Combine("data"));
        Assert.True(data.CanWrite());

        Directory.Delete(data.RootPath, recursive: true);
        root.CreateBlockingFile("data");

        Assert.False(data.CanWrite());
    }

    // ---------------------------------------------------------------------------------------------
    // Links planted in the data tree, and the protected root of an elevated process
    // ---------------------------------------------------------------------------------------------

    private const string ElevationRequired = "Requires an elevated process (Administrator).";

    private static readonly SecurityIdentifier _administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier _localSystem = new(WellKnownSidType.LocalSystemSid, null);

    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData(@"data\journal", false)]
    [InlineData(@"data\logs", false)]
    public void Resolve_AppDataTreeFolderIsJunction_FallsBackToLocalAppDataAndLeavesTargetUntouched(
        string linkedFolder,
        bool elevated)
    {
        // Elevated, a root holding nothing but a linked journal or logs folder is replaced instead;
        // Resolve_Elevated_PristineUnprotectedRoot_IsReplacedByProtectedRoot covers that.
        Assert.SkipWhen(elevated && !Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated);
        using TestDataRoot root = new();
        string target = root.CreateLinkTarget();
        string appFolder = root.Combine("app");
        string dataRoot = Path.Combine(appFolder, "data");
        Directory.CreateDirectory(linkedFolder == "data" ? appFolder : dataRoot);
        foreach (string subfolder in new[] { "journal", "logs" })
        {
            if (linkedFolder != "data" && linkedFolder != @"data\" + subfolder)
            {
                Directory.CreateDirectory(Path.Combine(dataRoot, subfolder));
            }
        }

        TestDataRoot.CreateJunction(Path.Combine(appFolder, linkedFolder), target);
        string localAppDataRoot = root.Combine("local");

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(elevated))
        {
            data = DataDirectory.Resolve(appFolder, localAppDataRoot);
        }

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
        Assert.Equal(Path.Combine(localAppDataRoot, "RamQuick"), data.RootPath);
        TestDataRoot.AssertLinkTargetUntouched(target);
        Assert.True(File.GetAttributes(Path.Combine(appFolder, linkedFolder)).HasFlag(FileAttributes.ReparsePoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("journal")]
    [InlineData("logs")]
    public void FromRoot_FolderIsJunction_ThrowsIOExceptionAndLeavesTargetUntouched(string linkedSubfolder)
    {
        using TestDataRoot root = new();
        string target = root.CreateLinkTarget();
        string rootPath = root.Combine("custom");
        if (linkedSubfolder.Length > 0)
        {
            Directory.CreateDirectory(rootPath);
        }

        TestDataRoot.CreateJunction(Path.Combine(rootPath, linkedSubfolder), target);

        IOException exception = Assert.ThrowsAny<IOException>(() => DataDirectory.FromRoot(rootPath));

        Assert.Contains("link or junction", exception.Message, StringComparison.Ordinal);
        TestDataRoot.AssertLinkTargetUntouched(target);
    }

    [Fact]
    public void CanWrite_LogsReplacedByJunction_ReturnsFalseAndLeavesTargetUntouched()
    {
        using TestDataRoot root = new();
        string target = root.CreateLinkTarget();
        Assert.True(root.Data.CanWrite());
        Directory.Delete(root.Data.LogsDirectory);
        TestDataRoot.CreateJunction(root.Data.LogsDirectory, target);

        Assert.False(root.Data.CanWrite());

        TestDataRoot.AssertLinkTargetUntouched(target);
    }

    [Fact]
    public void Resolve_NotElevated_CreatesRootThatInheritsItsParentsAccess()
    {
        using TestDataRoot root = new();

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(false))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        Assert.False(new DirectoryInfo(data.RootPath).GetAccessControl().AreAccessRulesProtected);
        Assert.False(DataDirectory.RequiresProtection(data.RootPath));
        Assert.True(data.CanWrite());
    }

    [Fact]
    public void OverrideElevation_NestedScopesDisposedAgain_RestoreEachEarlierValue()
    {
        bool host = Environment.IsPrivilegedProcess;

        IDisposable outer = DataDirectory.OverrideElevation(!host);
        IDisposable inner = DataDirectory.OverrideElevation(host);
        bool innerSeen = DataDirectory.IsPrivilegedProcess;
        inner.Dispose();
        bool outerSeen = DataDirectory.IsPrivilegedProcess;
        outer.Dispose();

        // Disposing the inner scope again must not reinstate the outer scope it once restored.
        inner.Dispose();

        Assert.Equal(host, innerSeen);
        Assert.Equal(!host, outerSeen);
        Assert.Equal(host, DataDirectory.IsPrivilegedProcess);
    }

    [Fact]
    public void Resolve_Elevated_MissingRoot_CreatesProtectedRootAndSubfolders()
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        AssertProtectedFolders(data);
        Assert.True(DataDirectory.RequiresProtection(data.RootPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.RootPath, "*.tmp", SearchOption.AllDirectories));

        // A root deleted after launch is created protected again at Start.
        Directory.Delete(data.RootPath, recursive: true);
        Assert.True(data.CanWrite());
        AssertProtectedFolders(data);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Resolve_Elevated_PristineUnprotectedRoot_IsReplacedByProtectedRoot(bool journalIsJunction, bool logsIsJunction)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        string target = root.CreateLinkTarget();
        string dataRoot = root.Combine("app", "data");
        Directory.CreateDirectory(dataRoot);
        Assert.SkipWhen(ProtectedFolder.IsProtected(dataRoot), "The fixture's folders are writable by administrators only.");
        PlantSubfolder(Path.Combine(dataRoot, "journal"), journalIsJunction, target);
        PlantSubfolder(Path.Combine(dataRoot, "logs"), logsIsJunction, target);

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        AssertProtectedFolders(data);
        Assert.False(File.GetAttributes(data.JournalDirectory).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.GetAttributes(data.LogsDirectory).HasFlag(FileAttributes.ReparsePoint));
        TestDataRoot.AssertLinkTargetUntouched(target);
    }

    [Theory]
    [InlineData("earlier.txt", false)]
    [InlineData(@"logs\RamQuick-earlier-summary.json", false)]
    [InlineData(@"other\file.txt", false)]
    [InlineData("earlier.txt", true)]
    public void Resolve_Elevated_UnprotectedRootWithContent_IsLeftAloneAndLocalAppDataUsed(string content, bool localAppDataRefused)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        string appFolder = root.Combine("app");
        string localAppDataRoot = root.Combine("local");
        string[] candidates = localAppDataRefused
            ? new[] { Path.Combine(appFolder, "data"), Path.Combine(localAppDataRoot, "RamQuick") }
            : new[] { Path.Combine(appFolder, "data") };
        foreach (string candidate in candidates)
        {
            Directory.CreateDirectory(Path.Combine(candidate, "journal"));
            Directory.CreateDirectory(Path.Combine(candidate, "logs"));
            Assert.SkipWhen(ProtectedFolder.IsProtected(candidate), "The fixture's folders are writable by administrators only.");
            string contentPath = Path.Combine(candidate, content);
            Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
            File.WriteAllText(contentPath, TestDataRoot.SentinelText);
        }

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(appFolder, localAppDataRoot);
        }

        // Neither location is emptied or has its access changed; with both refused, Start is refused.
        if (localAppDataRefused)
        {
            Assert.Null(data);
        }
        else
        {
            Assert.NotNull(data);
            Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
            AssertProtectedFolders(data);
        }

        foreach (string candidate in candidates)
        {
            Assert.Equal(TestDataRoot.SentinelText, File.ReadAllText(Path.Combine(candidate, content)));
            Assert.False(new DirectoryInfo(candidate).GetAccessControl().AreAccessRulesProtected);
            Assert.False(DataDirectory.RequiresProtection(candidate));
        }
    }

    [Fact]
    public void CanWrite_Elevated_ProtectedRootSwappedForUserFolder_RefusesProbeAndWrites()
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);

        // While nothing is open in it, a user who can write the app folder can rename the protected root
        // away and put a folder of their own, which they could plant links in, under its name.
        Directory.Move(data.RootPath, root.Combine("app", "moved"));
        Directory.CreateDirectory(data.LogsDirectory);
        Directory.CreateDirectory(data.JournalDirectory);
        File.WriteAllText(Path.Combine(data.RootPath, "user.txt"), TestDataRoot.SentinelText);
        Assert.SkipWhen(ProtectedFolder.IsProtected(data.RootPath), "The fixture's folders are writable by administrators only.");
        string summary = Path.Combine(data.LogsDirectory, "RamQuick-summary.json");

        Assert.False(data.CanWrite());
        Assert.ThrowsAny<IOException>(() => ResultsLog.PublishFile(summary, "{}"));
        using (var journal = new FileJournalStorage(data.JournalPath))
        {
            Assert.ThrowsAny<IOException>(() => journal.AppendDurable("{\"t\":\"begin\"}"));
        }

        Assert.False(File.Exists(summary));
        Assert.False(File.Exists(data.JournalPath));
        Assert.Equal(new[] { Path.Combine(data.RootPath, "user.txt") }, Directory.EnumerateFiles(data.RootPath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("app", false)]
    [InlineData("app", true)]
    [InlineData("local", false)]
    public void Resolve_Elevated_LocationReachedThroughUserChangeableJunction_IsRefusedAndTargetUntouched(
        string linkedLocation,
        bool linkInProtectedFolder)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();

        // A folder only administrators can write, which the user who made the junction cannot write to directly.
        string target = root.CreateProtectedFolder("admin-only");
        string link = root.Combine(linkedLocation);
        if (linkInProtectedFolder)
        {
            // Made in a user-writable folder and then moved, the junction keeps the access list that lets the user
            // retarget it, though it now lies in a protected folder and points to one.
            string staged = root.Combine("staging", linkedLocation);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            TestDataRoot.CreateJunction(staged, target);
            link = Path.Combine(root.CreateProtectedFolder("protected-holder"), linkedLocation);
            Directory.Move(staged, link);
            Assert.False(ProtectedFolder.IsProtected(link), "The access check read the junction's target, not the junction.");
        }
        else
        {
            TestDataRoot.CreateJunction(link, target);
        }

        string appFolder = linkedLocation == "app" ? link : Path.Combine(root.CreateBlockingFile("app-blocker"), "app");
        string localAppDataRoot = linkedLocation == "local" ? link : root.Combine("local");

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(appFolder, localAppDataRoot);
        }

        if (linkedLocation == "app")
        {
            Assert.NotNull(data);
            Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
            Assert.Equal(Path.Combine(localAppDataRoot, "RamQuick"), data.RootPath);
        }
        else
        {
            Assert.Null(data);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void CanWrite_Elevated_AncestorReplacedByUserJunction_RefusesProbeAndWrites()
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);
        Assert.True(data.CanWrite());

        // While nothing is open in it, a user who can write above the app folder can move it away and leave a junction
        // under its name. The junction still leads to the protected root, but the user can retarget it at any moment.
        string moved = root.Combine("app-moved");
        Directory.Move(root.Combine("app"), moved);
        TestDataRoot.CreateJunction(root.Combine("app"), moved);
        string summary = Path.Combine(data.LogsDirectory, "RamQuick-summary.json");

        Assert.False(data.CanWrite());
        Assert.ThrowsAny<IOException>(() => ResultsLog.PublishFile(summary, "{}"));
        using (var journal = new FileJournalStorage(data.JournalPath))
        {
            Assert.ThrowsAny<IOException>(() => journal.AppendDurable("{\"t\":\"begin\"}"));
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(moved, "data"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("plain target")]
    [InlineData("target below a user junction")]
    [InlineData("missing target")]
    public void Resolve_Elevated_AppFolderThroughAdministratorsOnlyJunction_IsFollowedOnlyToACheckableTarget(string targetCase)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        string realApp = root.Combine("real", "app");
        Directory.CreateDirectory(realApp);
        string linkTarget = targetCase switch
        {
            "plain target" => realApp,
            "target below a user junction" => root.Combine("user-link", "app"),
            _ => root.Combine("missing", "app"),
        };
        if (targetCase == "target below a user junction")
        {
            TestDataRoot.CreateJunction(root.Combine("user-link"), root.Combine("real"));
        }

        // Made by an elevated process in a protected folder, the junction inherits that folder's protection, as one an
        // administrator made under Program Files would.
        string appFolder = Path.Combine(root.CreateProtectedFolder("trusted"), "app");
        TestDataRoot.CreateJunction(appFolder, linkTarget);
        Assert.True(ProtectedFolder.IsProtected(appFolder));
        string localAppDataRoot = root.Combine("local");

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(appFolder, localAppDataRoot);
        }

        Assert.NotNull(data);
        if (targetCase != "plain target")
        {
            Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
            Assert.Empty(Directory.EnumerateFileSystemEntries(realApp));
            Assert.False(Directory.Exists(root.Combine("missing")));
            return;
        }

        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        Assert.Equal(Path.Combine(appFolder, "data"), data.RootPath);
        Assert.True(DataDirectory.RequiresProtection(data.RootPath));
        AssertProtectedFolders(data);
        Assert.True(data.CanWrite());
        ResultsLog.PublishFile(Path.Combine(data.LogsDirectory, "RamQuick-summary.json"), "{}");
        Assert.Equal("{}", File.ReadAllText(Path.Combine(realApp, "data", "logs", "RamQuick-summary.json")));
    }

    [Theory]
    [InlineData("query reports another folder")]
    [InlineData("no query")]
    [InlineData("no opener")]
    [InlineData("opener opens nothing")]
    [InlineData("opener opens another folder")]
    public void Pins_Elevated_FolderNotVerifiable_RefuseProtectedRootsAndWriteNothing(string unverifiable)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();
        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        {
            data = DataDirectory.Resolve(root.Combine("app"), root.Combine("local"));
        }

        Assert.NotNull(data);

        // A query reporting another folder, or an opener holding one, stands for a link that took a folder's place after
        // the checks by path; without a query or an opener, or with one that opens nothing, no folder can be verified.
        string elsewhere = root.CreateLinkTarget();
        Func<SafeFileHandle, string?>? query = DataDirectory.FinalPathQuery;
        Func<string, SafeFileHandle?>? opener = DataDirectory.FolderOpener;
        switch (unverifiable)
        {
            case "query reports another folder":
                query = handle => TestFinalPathQuery.GetFinalPath(handle) is { } path ? Path.Combine(elsewhere, Path.GetFileName(path)) : null;
                break;
            case "no query":
                query = null;
                break;
            case "no opener":
                opener = null;
                break;
            case "opener opens nothing":
                opener = _ => null;
                break;
            default:
                opener = _ => TestFinalPathQuery.OpenFolder(elsewhere);
                break;
        }

        string summary = Path.Combine(data.LogsDirectory, "RamQuick-summary.json");
        DataDirectory? refused;
        bool canWrite;
        using (DataDirectory.OverrideElevation(true))
        using (DataDirectory.OverrideFinalPathQuery(query))
        using (DataDirectory.OverrideFolderOpener(opener))
        {
            refused = DataDirectory.Resolve(root.Combine("other-app"), root.Combine("other-local"));
            canWrite = data.CanWrite();
            Assert.ThrowsAny<IOException>(() => ResultsLog.PublishFile(summary, "{}"));
            using var journal = new FileJournalStorage(data.JournalPath);
            Assert.ThrowsAny<IOException>(() => journal.AppendDurable("{\"t\":\"begin\"}"));
        }

        Assert.Null(refused);
        Assert.False(canWrite);
        Assert.Empty(Directory.EnumerateFiles(data.RootPath, "*", SearchOption.AllDirectories));
        TestDataRoot.AssertLinkTargetUntouched(elsewhere);
        foreach (string candidate in new[] { root.Combine("other-app"), root.Combine("other-local") })
        {
            // Nothing is created: without a query or an opener nothing is tried, and otherwise the folder that would
            // receive the first missing level fails its verification before anything is created in it.
            Assert.False(Directory.Exists(candidate));
        }

        // The same root passes again once the query and the opener are the installed ones, so the refusals came from
        // the verification.
        Assert.True(data.CanWrite());
    }

    [Fact]
    public void Resolve_NotElevated_AppFolderThroughJunctionWithoutQuery_UsesTheJunctionTarget()
    {
        using TestDataRoot root = new();
        string target = root.Combine("real-app");
        Directory.CreateDirectory(target);
        string appFolder = root.Combine("app");
        TestDataRoot.CreateJunction(appFolder, target);

        DataDirectory? data;
        bool canWrite;
        using (DataDirectory.OverrideElevation(false))
        using (DataDirectory.OverrideFinalPathQuery(null))
        {
            data = DataDirectory.Resolve(appFolder, root.Combine("local"));
            canWrite = data?.CanWrite() ?? false;
        }

        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        Assert.Equal(Path.Combine(appFolder, "data"), data.RootPath);
        Assert.False(DataDirectory.RequiresProtection(data.RootPath));
        Assert.True(canWrite);
        Assert.True(Directory.Exists(Path.Combine(target, "data", "journal")));
        Assert.True(Directory.Exists(Path.Combine(target, "data", "logs")));
    }

    [Fact]
    public void OverrideFinalPathQuery_NestedScopesDisposedAgain_RestoreEachEarlierQuery()
    {
        Func<SafeFileHandle, string?>? installed = DataDirectory.FinalPathQuery;
        Func<SafeFileHandle, string?> outerQuery = _ => null;

        IDisposable outer = DataDirectory.OverrideFinalPathQuery(outerQuery);
        IDisposable inner = DataDirectory.OverrideFinalPathQuery(null);
        Func<SafeFileHandle, string?>? innerSeen = DataDirectory.EffectiveFinalPathQuery;
        inner.Dispose();
        Func<SafeFileHandle, string?>? outerSeen = DataDirectory.EffectiveFinalPathQuery;
        outer.Dispose();

        // Disposing the inner scope again must not reinstate the outer scope it once restored.
        inner.Dispose();

        Assert.NotNull(installed);
        Assert.Null(innerSeen);
        Assert.Same(outerQuery, outerSeen);
        Assert.Same(installed, DataDirectory.EffectiveFinalPathQuery);
    }

    [Fact]
    public void OverrideFolderOpener_NestedScopesDisposedAgain_RestoreEachEarlierOpener()
    {
        Func<string, SafeFileHandle?>? installed = DataDirectory.FolderOpener;
        Func<string, SafeFileHandle?> outerOpener = _ => null;

        IDisposable outer = DataDirectory.OverrideFolderOpener(outerOpener);
        IDisposable inner = DataDirectory.OverrideFolderOpener(null);
        Func<string, SafeFileHandle?>? innerSeen = DataDirectory.EffectiveFolderOpener;
        inner.Dispose();
        Func<string, SafeFileHandle?>? outerSeen = DataDirectory.EffectiveFolderOpener;
        outer.Dispose();

        // Disposing the inner scope again must not reinstate the outer scope it once restored.
        inner.Dispose();

        Assert.NotNull(installed);
        Assert.Null(innerSeen);
        Assert.Same(outerOpener, outerSeen);
        Assert.Same(installed, DataDirectory.EffectiveFolderOpener);
    }

    [Theory]
    [InlineData("app", "app", false, false)]
    [InlineData("app", "app", true, false)]
    [InlineData(@"outer\app", "outer", false, false)]
    [InlineData("app", @"app\data", true, false)]
    [InlineData("app", @"app\data", true, true)]
    public void Resolve_Elevated_FolderSwappedForJunctionJustBeforeItIsHeld_NothingReachesTheTarget(
        string appRelative,
        string swappedRelative,
        bool appExists,
        bool pristineRootExists)
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();

        // A folder only administrators can write, which the user who swaps a folder for a junction to it cannot write.
        string target = root.CreateProtectedFolder("admin-only");
        string appFolder = root.Combine(appRelative);
        string dataRoot = Path.Combine(appFolder, "data");
        string swapped = root.Combine(swappedRelative);
        if (appExists)
        {
            Directory.CreateDirectory(appFolder);
        }

        if (pristineRootExists)
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "journal"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
            Assert.SkipWhen(ProtectedFolder.IsProtected(dataRoot), "The fixture's folders are writable by administrators only.");
        }

        // The latest moment a user can swap the folder: after RAMQuick worked out where it must lie, and created it if it
        // was missing, just before RAMQuick holds it. It holds nothing but empty folders then.
        Func<string, SafeFileHandle?> installedOpener = DataDirectory.FolderOpener!;
        bool swappedForJunction = false;
        SafeFileHandle? SwapThenOpen(string path)
        {
            if (!swappedForJunction && IsSamePath(path, swapped))
            {
                Directory.Delete(swapped, recursive: true);
                TestDataRoot.CreateJunction(swapped, target);
                swappedForJunction = true;
            }

            return installedOpener(path);
        }

        // Even an anchor created and deleted again in the target would be reported.
        ConcurrentQueue<string> createdInTarget = new();
        using FileSystemWatcher watcher = new(target)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        watcher.Created += (_, e) => createdInTarget.Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => createdInTarget.Enqueue(e.FullPath);
        watcher.EnableRaisingEvents = true;

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        using (DataDirectory.OverrideFolderOpener(SwapThenOpen))
        {
            data = DataDirectory.Resolve(appFolder, root.Combine("local"));
        }

        // Events arrive in order, so once the marker's has arrived, any earlier one has too.
        string marker = Path.Combine(target, "marker.txt");
        File.WriteAllText(marker, TestDataRoot.SentinelText);
        Assert.True(SpinWait.SpinUntil(() => createdInTarget.Contains(marker), TimeSpan.FromSeconds(30)), "The watcher reported nothing.");

        Assert.True(swappedForJunction, $"'{swapped}' was never held.");
        Assert.True(File.GetAttributes(swapped).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(new[] { marker }, createdInTarget);
        Assert.Equal(new[] { marker }, Directory.EnumerateFileSystemEntries(target));
        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.LocalAppData, data.Kind);
        Assert.Equal(root.Combine("local", "RamQuick"), data.RootPath);
    }

    [Fact]
    public void Resolve_Elevated_PristineRootTakeover_AppFolderCannotBeSwappedUntilTheNewRootIsPinned()
    {
        Assert.SkipUnless(Environment.IsPrivilegedProcess, ElevationRequired);
        TestDataRoot.SkipWhenProtectedRootsUnavailable(elevated: true);
        using TestDataRoot root = new();

        // The folder a swapped app folder would lead to holds an empty data folder of its own, which the takeover would
        // then delete, and create again with RAMQuick's access list.
        string target = root.CreateProtectedFolder("admin-only");
        string targetData = Path.Combine(target, "data");
        Directory.CreateDirectory(targetData);
        const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
        string targetDataSecurity = new DirectoryInfo(targetData).GetAccessControl(Sections).GetSecurityDescriptorSddlForm(Sections);
        string appFolder = root.Combine("app");
        string dataRoot = Path.Combine(appFolder, "data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "journal"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
        Assert.SkipWhen(ProtectedFolder.IsProtected(dataRoot), "The fixture's folders are writable by administrators only.");

        // From the moment RAMQuick holds the app folder, each later open, the takeover's delete and creation of the root
        // lying between two of them, is a moment a user tries to move the app folder away and leave a junction to the
        // target under its name.
        Func<string, SafeFileHandle?> installedOpener = DataDirectory.FolderOpener!;
        bool appFolderHeld = false;
        int attempts = 0;
        string? swappedBefore = null;
        SafeFileHandle? TrySwapThenOpen(string path)
        {
            if (appFolderHeld && swappedBefore is null)
            {
                attempts++;
                try
                {
                    Directory.Move(appFolder, root.Combine("app-moved"));
                    TestDataRoot.CreateJunction(appFolder, target);
                    swappedBefore = path;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Windows refuses to move a folder that a handle holds.
                }
            }

            SafeFileHandle? handle = installedOpener(path);
            appFolderHeld |= IsSamePath(path, appFolder);
            return handle;
        }

        DataDirectory? data;
        using (DataDirectory.OverrideElevation(true))
        using (DataDirectory.OverrideFolderOpener(TrySwapThenOpen))
        {
            data = DataDirectory.Resolve(appFolder, root.Combine("local"));
        }

        // One try each before the takeover's pin of the old root, the new root's pin and its two subfolders' pins.
        Assert.Null(swappedBefore);
        Assert.Equal(4, attempts);
        Assert.NotNull(data);
        Assert.Equal(DataDirectoryKind.AppFolder, data.Kind);
        Assert.Equal(dataRoot, data.RootPath);
        AssertProtectedFolders(data);
        Assert.Equal(new[] { targetData }, Directory.EnumerateFileSystemEntries(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetData));
        Assert.Equal(targetDataSecurity, new DirectoryInfo(targetData).GetAccessControl(Sections).GetSecurityDescriptorSddlForm(Sections));
    }

    [Fact]
    public void HoldParent_VolumeRootOrMissingVolume_HoldsNothing()
    {
        // Nothing lies above a volume root that a link could take the place of.
        string volume = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Null(DataFolderPin.HoldParent(volume, volume));

        // On a drive letter no volume uses, the walk up finds no folder to hold, so no level is created.
        HashSet<char> used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        char? free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => (char)letter).Where(letter => !used.Contains(letter)).Cast<char?>().LastOrDefault();
        Assert.SkipWhen(free is null, "Every drive letter is in use.");
        string missing = $@"{free}:\app\data";
        Assert.ThrowsAny<IOException>(() => DataFolderPin.HoldParent(missing, missing));
    }

    private static bool IsSamePath(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(other),
            StringComparison.OrdinalIgnoreCase);

    private static void PlantSubfolder(string path, bool asJunction, string target)
    {
        if (asJunction)
        {
            TestDataRoot.CreateJunction(path, target);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    // The root and both subfolders: owned by Administrators, not inheriting, changeable by SYSTEM and Administrators
    // only, and readable by the account running the test.
    private static void AssertProtectedFolders(DataDirectory data)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User!;
        var expected = new HashSet<(IdentityReference, FileSystemRights, AccessControlType)>
        {
            (_localSystem, FileSystemRights.FullControl, AccessControlType.Allow),
            (_administrators, FileSystemRights.FullControl, AccessControlType.Allow),
        };
        if (user != _localSystem && user != _administrators)
        {
            expected.Add((user, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        }

        foreach (string folder in new[] { data.RootPath, data.JournalDirectory, data.LogsDirectory })
        {
            DirectorySecurity security = new DirectoryInfo(folder).GetAccessControl();
            Assert.Equal(_administrators, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(security.AreAccessRulesProtected, $"'{folder}' inherits access from its parent.");
            var actual = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => (rule.IdentityReference, rule.FileSystemRights, rule.AccessControlType))
                .ToHashSet();
            Assert.Equal(expected, actual);
            Assert.True(ProtectedFolder.IsProtected(folder));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateBlockingFile_ExistingFixtureFolder_ReplacesFolderWithFile(bool folderHasContent)
    {
        using TestDataRoot root = new();
        if (folderHasContent)
        {
            string nested = Path.Combine(root.Data.LogsDirectory, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "earlier.log"), "earlier");
        }

        string blockingFile = root.CreateBlockingFile("logs");

        Assert.Equal(root.Data.LogsDirectory, blockingFile);
        Assert.True(File.Exists(root.Data.LogsDirectory));
        Assert.False(Directory.Exists(root.Data.LogsDirectory));
        // The folder cannot be recreated in the file's place, which is the failure consumers
        // arrange this file for.
        Assert.Throws<IOException>(() => Directory.CreateDirectory(root.Data.LogsDirectory));
    }

    public static TheoryData<string[]> PartsNotInsideRoot => new()
    {
        Array.Empty<string>(),
        new[] { "." },
        new[] { "logs", ".." },
        new[] { ".." },
        new[] { "..", "outside" },
        new[] { @"\outside" },
    };

    [Theory]
    [MemberData(nameof(PartsNotInsideRoot))]
    public void CreateBlockingFile_PathNotInsideRoot_ThrowsArgumentException(string[] parts)
    {
        using TestDataRoot root = new();

        Assert.Throws<ArgumentException>("parts", () => root.CreateBlockingFile(parts));
        Assert.True(Directory.Exists(root.Path));
        Assert.True(Directory.Exists(root.Data.LogsDirectory));
    }
}

/// <summary>
/// Tests for the observation contract of <see cref="StalledJournalStorage"/>:
/// <see cref="StalledJournalStorage.Lines"/> and <see cref="StalledJournalStorage.RecordTypesWritten"/>
/// are the append history and survive <see cref="StalledJournalStorage.Delete"/>, because the
/// journal's owner deletes the journal right after its final <c>end</c> record;
/// <see cref="StalledJournalStorage.ReadLines"/> and <see cref="StalledJournalStorage.Exists"/>
/// report the current journal.
/// </summary>
public sealed class StalledJournalStorageTests
{
    /// <summary>
    /// Lines seeded before the run, then the record types it appends before deleting the journal:
    /// a full run writing every record in order, and a recovery that appends only <c>end</c> to a
    /// crashed run's journal ending in a torn line.
    /// </summary>
    public static TheoryData<string[], string[]> DeleteAfterEndCases { get; } = new()
    {
        { [], ["begin", "prepared", "step", "stepEnd", "beat", "result", "released", "end"] },
        {
            ["{\"t\":\"begin\"}", "{\"t\":\"step\",\"index\":2,\"count\":6}", "{\"t\":\"beat\",\"step\":2,\"elap"],
            ["end"]
        },
    };

    [Theory]
    [MemberData(nameof(DeleteAfterEndCases))]
    public void Delete_AfterEnd_KeepsAppendHistoryAndRemovesJournal(string[] seededLines, string[] appendedTypes)
    {
        using StalledJournalStorage storage = new();
        if (seededLines.Length > 0)
        {
            storage.Seed(seededLines);
        }

        string[] appendedLines = appendedTypes.Select(RecordLine).ToArray();
        foreach (string line in appendedLines)
        {
            storage.AppendDurable(line);
        }

        storage.Delete();

        Assert.Equal(appendedTypes, storage.RecordTypesWritten());
        Assert.Equal(appendedLines, storage.Lines);
        Assert.Empty(storage.ReadLines());
        Assert.False(storage.Exists());
        Assert.True(storage.IsDeleted);
        Assert.Equal(1, storage.DeleteCount);
        Assert.Equal(appendedLines, storage.Calls.Select(call => call.Line));
        Assert.All(storage.Calls, call => Assert.Equal(AppendOutcome.Succeeded, call.Outcome));
    }

    [Fact]
    public void AppendDurable_AfterDelete_StartsNewJournalAndKeepsHistory()
    {
        using StalledJournalStorage storage = new();
        string begin = RecordLine("begin");
        string end = RecordLine("end");
        storage.AppendDurable(begin);
        storage.Delete();

        storage.AppendDurable(end);

        Assert.Equal(end, Assert.Single(storage.ReadLines()));
        Assert.Equal(new[] { begin, end }, storage.Lines);
        Assert.True(storage.Exists());
        Assert.False(storage.IsDeleted);
    }

    [Fact]
    public void Delete_AfterFailedAppend_HistoryHoldsOnlySucceededAppends()
    {
        using StalledJournalStorage storage = new();
        storage.AppendDurable(RecordLine("begin"));
        storage.FailNext("end");
        Assert.Throws<IOException>(() => storage.AppendDurable(RecordLine("end")));

        storage.Delete();

        Assert.Equal("begin", Assert.Single(storage.RecordTypesWritten()));
        Assert.Collection(
            storage.Calls,
            call => Assert.Equal(AppendOutcome.Succeeded, call.Outcome),
            call => Assert.Equal(AppendOutcome.Failed, call.Outcome));
    }

    private static string RecordLine(string recordType) => $"{{\"t\":\"{recordType}\"}}";
}
