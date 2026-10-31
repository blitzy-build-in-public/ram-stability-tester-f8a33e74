using System.Threading;
using RamQuick.App.Services;

// Linked into RamQuick.TestHost and RamQuick.IntegrationTests as well: this file may depend on the
// BCL and the public IElevationService contract only, never on a test framework or a test-project type.
namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// An <see cref="IElevationService"/> whose elevation state and restart outcome are scripted by the
/// test, and which counts how it is used. It never starts a process, shows a UAC prompt or reads the
/// process token.
/// </summary>
/// <remarks>
/// <para>
/// Every member is thread-safe: the run controller may query elevation on a thread other than the
/// test's, while the test scripts the fake and reads its counters.
/// </para>
/// <para>
/// Unlike the production service, <see cref="IsElevated"/> can change mid-test, so a test can model
/// a different process without building a new graph. <see cref="TryRestartElevated"/> never changes
/// it: a successful restart starts another process, and the current one keeps its own token.
/// </para>
/// </remarks>
public sealed class FakeElevationService : IElevationService
{
    private bool _isElevated = true;
    private bool _restartResult;
    private int _isElevatedReadCount;
    private int _restartCallCount;

    /// <summary>
    /// Gets or sets the value <see cref="IElevationService.IsElevated"/> reports. Defaults to
    /// <see langword="true"/>. Every read increments <see cref="IsElevatedReadCount"/>.
    /// </summary>
    public bool IsElevated
    {
        get
        {
            Interlocked.Increment(ref _isElevatedReadCount);
            return Volatile.Read(ref _isElevated);
        }

        set => Volatile.Write(ref _isElevated, value);
    }

    /// <summary>
    /// Gets or sets the value <see cref="TryRestartElevated"/> returns. Defaults to
    /// <see langword="false"/>, the user declining UAC; <see langword="true"/> means an elevated
    /// process started, so the current instance must shut down.
    /// </summary>
    public bool RestartResult
    {
        get => Volatile.Read(ref _restartResult);
        set => Volatile.Write(ref _restartResult, value);
    }

    /// <summary>
    /// Gets the number of <see cref="TryRestartElevated"/> calls made on this instance.
    /// </summary>
    public int RestartCallCount => Volatile.Read(ref _restartCallCount);

    /// <summary>
    /// Gets the number of times <see cref="IsElevated"/> has been read on this instance.
    /// </summary>
    public int IsElevatedReadCount => Volatile.Read(ref _isElevatedReadCount);

    /// <summary>
    /// Records the restart request and returns <see cref="RestartResult"/>, without starting a process.
    /// </summary>
    /// <returns>The current value of <see cref="RestartResult"/>.</returns>
    public bool TryRestartElevated()
    {
        Interlocked.Increment(ref _restartCallCount);
        return Volatile.Read(ref _restartResult);
    }
}
