using Xunit;

namespace RamQuick.Logging.Tests;

/// <summary>
/// A unique, disposable folder under <c>%TEMP%\RamQuick.Logging.Tests</c> that every test touching
/// the file system creates for itself, so tests run in parallel without sharing a folder.
/// </summary>
/// <remarks>
/// An unwritable location is simulated only by placing it beneath a file made with
/// <see cref="CreateBlockingFile(string[])"/>; no test changes ACLs or attributes or uses a system
/// folder.
/// </remarks>
internal sealed class TestDataRoot : IDisposable
{
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan _deleteBackOff = TimeSpan.FromMilliseconds(100);

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
    /// creating its parent folders, and returns its path. No location beneath the returned path
    /// can become a folder, so a data folder placed there is unwritable.
    /// </summary>
    public string CreateBlockingFile(params string[] parts)
    {
        string filePath = Combine(parts);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, []);
        return filePath;
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
/// Tests for <see cref="DataDirectory"/>: resolution order and fallback, the null result that
/// refuses Start, the folder layout, and the write probe.
/// </summary>
public sealed class DataDirectoryTests
{
    [Fact]
    public void Resolve_WritableAppFolder_ReturnsAppDataFolder()
    {
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
    public void Resolve_Parameterless_DoesNotThrow()
    {
        // Resolve() probes the real locations: <AppContext.BaseDirectory>\data, which is this test
        // project's temporary build output, and %LOCALAPPDATA%\RamQuick only when that fails.
        // Nothing is asserted about the machine's folders and nothing is deleted from them.
        DataDirectory? data = DataDirectory.Resolve();

        Assert.True(
            data is null || data.Kind is DataDirectoryKind.AppFolder or DataDirectoryKind.LocalAppData,
            $"Unexpected kind {data?.Kind}.");
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
}
