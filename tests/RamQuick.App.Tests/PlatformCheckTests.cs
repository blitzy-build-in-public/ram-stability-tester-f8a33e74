using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RamQuick.App.Services;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="PlatformCheck"/>: RAMQuick runs only on an x64 operating system at build
/// 14393 or later, architecture is checked first, and <see cref="PlatformCheckResult"/> can never
/// pair a verdict with a contradicting reason.
/// </summary>
public sealed class PlatformCheckTests
{
    /// <summary>
    /// Every architecture other than <see cref="Architecture.X64"/>: each value .NET 8 defines, led
    /// by Arm64, which x64 emulation on Windows on Arm reports, plus one undefined value.
    /// </summary>
    public static TheoryData<Architecture> NonX64Architectures
    {
        get
        {
            var cases = new TheoryData<Architecture> { Architecture.Arm64 };
            foreach (Architecture architecture in Enum.GetValues<Architecture>())
            {
                if (architecture is not Architecture.X64 and not Architecture.Arm64)
                {
                    cases.Add(architecture);
                }
            }

            cases.Add((Architecture)int.MaxValue);
            return cases;
        }
    }

    [Fact]
    public void MinimumBuild_Value_IsWindows10Version1607()
    {
        Assert.Equal(14393, PlatformCheck.MinimumBuild);
    }

    [Theory]
    // The minimum itself, Windows 10 22H2 and Windows 11 24H2.
    [InlineData(10, 0, 14393)]
    [InlineData(10, 0, 19045)]
    [InlineData(10, 0, 26100)]
    public void Evaluate_X64AtOrAboveMinimumBuild_IsSupported(int major, int minor, int build)
    {
        PlatformCheckResult result = PlatformCheck.Evaluate(Architecture.X64, new Version(major, minor, build));

        Assert.True(result.IsSupported);
        Assert.Equal(PlatformRejection.None, result.Rejection);
        Assert.Equal(PlatformCheckResult.Supported, result);
    }

    [Theory]
    [MemberData(nameof(NonX64Architectures))]
    public void Evaluate_NonX64Architecture_RejectsNotX64(Architecture osArchitecture)
    {
        PlatformCheckResult result = PlatformCheck.Evaluate(osArchitecture, new Version(10, 0, 26100));

        Assert.Equal(new PlatformCheckResult(false, PlatformRejection.NotX64), result);
    }

    [Theory]
    // One below the minimum, Windows 10 1511 and Windows 8.1: only the build number is compared.
    [InlineData(10, 0, 14392)]
    [InlineData(10, 0, 10586)]
    [InlineData(6, 3, 9600)]
    public void Evaluate_X64BelowMinimumBuild_RejectsBuildTooOld(int major, int minor, int build)
    {
        PlatformCheckResult result = PlatformCheck.Evaluate(Architecture.X64, new Version(major, minor, build));

        Assert.Equal(new PlatformCheckResult(false, PlatformRejection.BuildTooOld), result);
    }

    [Fact]
    public void Evaluate_X64WithUndefinedBuild_RejectsBuildTooOld()
    {
        // new Version(10, 0) leaves Build at -1, which the policy treats as below the minimum.
        PlatformCheckResult result = PlatformCheck.Evaluate(Architecture.X64, new Version(10, 0));

        Assert.Equal(new PlatformCheckResult(false, PlatformRejection.BuildTooOld), result);
    }

    [Fact]
    public void Evaluate_Arm64AndBuildTooOld_RejectsNotX64First()
    {
        // Windows 10 RTM on Arm64 fails both checks; architecture is checked first.
        PlatformCheckResult result = PlatformCheck.Evaluate(Architecture.Arm64, new Version(10, 0, 10240));

        Assert.Equal(new PlatformCheckResult(false, PlatformRejection.NotX64), result);
    }

    [Fact]
    public void Evaluate_NullVersion_ThrowsArgumentNull()
    {
        ArgumentNullException exception =
            Assert.Throws<ArgumentNullException>(() => PlatformCheck.Evaluate(Architecture.X64, null!));

        Assert.Equal("osVersion", exception.ParamName);
    }

    [Fact]
    public void EvaluateCurrent_SupportedTestHost_IsSupported()
    {
        // The automated suites run on Windows 10/11 x64, so the real architecture and build pass.
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Assert.Skip($"The test host's operating system is {RuntimeInformation.OSArchitecture}, not X64.");
        }

        PlatformCheckResult result = PlatformCheck.EvaluateCurrent();

        Assert.Equal(PlatformCheckResult.Supported, result);
    }

    [Theory]
    [InlineData(true, PlatformRejection.None)]
    [InlineData(false, PlatformRejection.NotX64)]
    [InlineData(false, PlatformRejection.BuildTooOld)]
    public void PlatformCheckResult_ConsistentPair_KeepsValues(bool isSupported, PlatformRejection rejection)
    {
        var result = new PlatformCheckResult(isSupported, rejection);

        Assert.Equal(isSupported, result.IsSupported);
        Assert.Equal(rejection, result.Rejection);
    }

    [Theory]
    [InlineData(true, PlatformRejection.NotX64)]
    [InlineData(true, PlatformRejection.BuildTooOld)]
    [InlineData(false, PlatformRejection.None)]
    public void PlatformCheckResult_ContradictingPair_ThrowsArgument(bool isSupported, PlatformRejection rejection)
    {
        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => new PlatformCheckResult(isSupported, rejection));

        Assert.Equal("isSupported", exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlatformCheckResult_UndefinedRejection_ThrowsArgumentOutOfRange(bool isSupported)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlatformCheckResult(isSupported, (PlatformRejection)99));

        Assert.Equal("rejection", exception.ParamName);
    }
}

/// <summary>
/// Tests for the host services that have no other test home: <see cref="SingleInstanceGuard"/> and
/// <see cref="ElevationService"/>. <see cref="ElevationService.TryRestartElevated"/> is never called,
/// since it would launch an elevated process.
/// </summary>
/// <remarks>
/// Every mutex name is unique to its test and never <see cref="SingleInstanceGuard.MutexName"/>, so a
/// running RAMQuick cannot interfere. Mutex ownership is thread-affine and re-entrant for its owner, so
/// each test stays on its own thread throughout, with no await, and probes contention from dedicated
/// threads that it joins.
/// </remarks>
public sealed class HostServiceTests
{
    private static readonly TimeSpan _contendedTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _threadJoinTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Names Windows rejects as malformed, one per Win32 error the guard lets propagate: a namespace
    /// that does not exist (ERROR_PATH_NOT_FOUND), nothing after the namespace prefix
    /// (ERROR_INVALID_NAME), a leading separator (ERROR_BAD_PATHNAME), and a name beyond the object
    /// manager's 32,767-character limit (ERROR_FILENAME_EXCED_RANGE). Names a few hundred characters
    /// long are accepted, whatever CreateMutex's MAX_PATH remark suggests.
    /// </summary>
    public static TheoryData<string> MalformedNames => new()
    {
        @"NoSuchNamespace\RamQuick.Tests.Malformed",
        @"Local\",
        @"\RamQuick.Tests.Malformed",
        @"Local\RamQuick.Tests." + new string('x', 40_000),
    };

    [Theory]
    // No wait, Timeout.InfiniteTimeSpan (-1 ms) and a bounded wait.
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void TryAcquire_Uncontended_ReturnsGuard(int timeoutMilliseconds)
    {
        using SingleInstanceGuard? guard =
            SingleInstanceGuard.TryAcquire(TimeSpan.FromMilliseconds(timeoutMilliseconds), UniqueMutexName());

        Assert.NotNull(guard);
    }

    [Fact]
    public void TryAcquire_HeldByAnotherThread_ReturnsNullUntilReleased()
    {
        string name = UniqueMutexName();
        SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name);
        Assert.NotNull(first);
        try
        {
            Assert.False(AcquiresOnAnotherThread(name, _contendedTimeout));
        }
        finally
        {
            first.Dispose();
        }

        Assert.True(AcquiresOnAnotherThread(name, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_OwningThreadAgain_ReturnsSecondGuardAndHoldsUntilBothDisposed()
    {
        string name = UniqueMutexName();
        using (SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name))
        {
            Assert.NotNull(first);
            using (SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name))
            {
                Assert.NotNull(second);
            }

            // The first acquisition is still held, so other threads are still refused.
            Assert.False(AcquiresOnAnotherThread(name, _contendedTimeout));
        }

        Assert.True(AcquiresOnAnotherThread(name, TimeSpan.Zero));
    }

    [Fact]
    public void Dispose_CalledTwice_ReleasesWithoutThrowing()
    {
        string name = UniqueMutexName();
        SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name);
        Assert.NotNull(guard);

        guard.Dispose();
        Exception? secondDispose = Record.Exception(guard.Dispose);

        Assert.Null(secondDispose);
        Assert.True(AcquiresOnAnotherThread(name, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_OwnerThreadExitedWithoutRelease_TakesOverAbandonedMutex()
    {
        string name = UniqueMutexName();

        // The dedicated thread exits still owning the mutex, and the guard's open handle keeps the
        // abandoned mutex alive.
        SingleInstanceGuard? abandoned =
            RunOnDedicatedThread(() => SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name));
        Assert.NotNull(abandoned);
        SingleInstanceGuard? successor = null;
        try
        {
            successor = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name);
            Assert.NotNull(successor);

            // Disposed off its owning thread, the abandoned guard only closes its handle. It must not
            // release the ownership this thread now holds through the successor.
            abandoned.Dispose();
            Assert.False(AcquiresOnAnotherThread(name, _contendedTimeout));
        }
        finally
        {
            successor?.Dispose();
            abandoned.Dispose();
        }

        Assert.True(AcquiresOnAnotherThread(name, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_NameHeldByEvent_ReturnsNull()
    {
        string name = UniqueMutexName();
        using var conflicting = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, name);

        SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(TimeSpan.Zero, name);

        Assert.Null(guard);
    }

    [Theory]
    [MemberData(nameof(MalformedNames))]
    public void TryAcquire_MalformedName_ThrowsIOException(string mutexName)
    {
        Assert.ThrowsAny<IOException>(() => SingleInstanceGuard.TryAcquire(TimeSpan.Zero, mutexName));
    }

    [Theory]
    // Negative other than Timeout.InfiniteTimeSpan, and one millisecond above int.MaxValue.
    [InlineData(-2.0)]
    [InlineData(2_147_483_648.0)]
    public void TryAcquire_TimeoutOutOfRange_ThrowsArgumentOutOfRange(double timeoutMilliseconds)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SingleInstanceGuard.TryAcquire(TimeSpan.FromMilliseconds(timeoutMilliseconds), UniqueMutexName()));

        Assert.Equal("timeout", exception.ParamName);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    public void TryAcquire_MissingName_ThrowsArgument(string? mutexName, Type expectedException)
    {
        var exception = (ArgumentException)Assert.Throws(
            expectedException,
            () => SingleInstanceGuard.TryAcquire(TimeSpan.Zero, mutexName!));

        Assert.Equal("mutexName", exception.ParamName);
    }

    [Fact]
    public void IsElevated_ReadTwice_MatchesAdministratorRoleOfCurrentToken()
    {
        bool expected;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        var service = new ElevationService();
        bool first = service.IsElevated;
        bool second = service.IsElevated;

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
    }

    private static string UniqueMutexName() => @"Local\RamQuick.Tests." + Guid.NewGuid().ToString("N");

    // Acquires and disposes a guard on a dedicated thread, so the probe neither re-enters nor keeps
    // the calling thread's ownership.
    private static bool AcquiresOnAnotherThread(string mutexName, TimeSpan timeout) =>
        RunOnDedicatedThread(() =>
        {
            using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(timeout, mutexName);
            return guard is not null;
        });

    private static T RunOnDedicatedThread<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = nameof(HostServiceTests),
        };

        thread.Start();
        Assert.True(thread.Join(_threadJoinTimeout), "The dedicated test thread did not finish.");
        failure?.Throw();
        return result;
    }
}

