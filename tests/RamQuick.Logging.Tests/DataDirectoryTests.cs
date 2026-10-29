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
/// already holds, such as a data root or its <c>logs</c> subfolder. Cleanup is different:
/// <see cref="Dispose"/> may clear ReadOnly flags on the folder and its entries so that its
/// recursive delete succeeds.
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
