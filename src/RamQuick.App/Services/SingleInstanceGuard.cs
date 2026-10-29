using System.IO;

namespace RamQuick.App.Services;

/// <summary>
/// Owns the named mutex that keeps a second RAMQuick instance from starting while one is running.
/// Two instances would compete for lockable memory and for the progress journal.
/// </summary>
/// <remarks>
/// <para>
/// Mutex ownership is thread-affine. The application acquires the guard on the UI thread in
/// <c>OnStartup</c> and disposes it on the same thread in <c>OnExit</c>. Disposed on any other thread,
/// the guard closes its handle without releasing ownership, which lasts until the owning thread or the
/// process ends.
/// </para>
/// <para>
/// The mutex is re-entrant for its owner, so a second <see cref="TryAcquire"/> on the owning thread
/// succeeds. Only other threads and other processes are refused.
/// </para>
/// <para>
/// Keep the guard reachable for the life of the instance. Its handle keeps the kernel mutex alive;
/// once the last handle is closed or finalized, the object is destroyed even while owned, and
/// another instance could create the name afresh.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// The production mutex name. The <c>Local\</c> namespace is per logon session, so elevated and
    /// non-elevated instances in the same session share it; that is what lets an elevated restart take
    /// over from the non-elevated instance that launched it.
    /// </summary>
    public const string MutexName = @"Local\RamQuick.SingleInstance";

    // HRESULT_FROM_WIN32 of ERROR_PATH_NOT_FOUND, ERROR_INVALID_NAME, ERROR_BAD_PATHNAME and
    // ERROR_FILENAME_EXCED_RANGE, the HResults of the exceptions the Mutex constructor throws for them.
    private const int PathNotFoundHResult = unchecked((int)0x80070003);
    private const int InvalidNameHResult = unchecked((int)0x8007007B);
    private const int BadPathNameHResult = unchecked((int)0x800700A1);
    private const int FileNameTooLongHResult = unchecked((int)0x800700CE);

    // WaitHandle.WaitOne rejects timeouts above int.MaxValue milliseconds. Checking the range before
    // the mutex is opened means a bad argument never leaves an unowned handle behind.
    private static readonly TimeSpan _maxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly Mutex _mutex;

    // The owning Thread itself is kept rather than its managed thread id: ids are recycled once a
    // thread exits, and a recycled id would identify a thread that does not own the mutex, making
    // ReleaseMutex throw. Reference identity makes the release attempt exact.
    private readonly Thread _ownerThread;

    private int _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownerThread = Thread.CurrentThread;
    }

    /// <summary>
    /// Tries to make this process the single running instance, waiting up to
    /// <paramref name="timeout"/> for a previous instance to release the mutex. The application passes
    /// 5 seconds, which covers an elevated restart: the non-elevated instance holds the mutex until its
    /// window has closed.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait: <see cref="TimeSpan.Zero"/> to test without waiting, a positive interval of at
    /// most <see cref="int.MaxValue"/> milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely.
    /// </param>
    /// <param name="mutexName">
    /// The mutex name. The application uses the default, <see cref="MutexName"/>; tests pass a unique
    /// name so they never contend with a running RAMQuick.
    /// </param>
    /// <returns>
    /// A guard whose mutex is owned by the calling thread, or <see langword="null"/> when this process
    /// cannot become the single instance: another instance still holds the mutex after the wait, or
    /// holds it with access rights this process lacks; another kind of named object, such as an event
    /// or a semaphore, already uses the name; or Windows otherwise refuses to create or open the mutex,
    /// for example for lack of kernel resources.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutexName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="mutexName"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>, or exceeds
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    /// <exception cref="IOException">
    /// <paramref name="mutexName"/> is not a valid kernel object name: for example it names a namespace
    /// that does not exist, has nothing after its namespace prefix, or is too long.
    /// </exception>
    public static SingleInstanceGuard? TryAcquire(TimeSpan timeout, string mutexName = MutexName)
    {
        ArgumentException.ThrowIfNullOrEmpty(mutexName);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, _maxTimeout);
        }

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated instance created the mutex with its default DACL, which a non-elevated token
            // cannot open. The mutex exists, so an instance is running.
            return null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // A named event, semaphore or waitable timer holds the name, so no mutex can exist under it
            // while that object lives, and this process cannot become the single instance.
            return null;
        }
        catch (IOException exception) when (!IsMalformedName(exception))
        {
            // Windows refused the mutex for a reason other than its name, such as exhausted kernel
            // resources. That is a refusal for startup to report, not a defect to crash on.
            return null;
        }

        bool acquired = false;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance crashed or was killed while holding the mutex, and another handle
            // kept the mutex alive. Ownership has passed to this thread, and the previous run's
            // journal is recovered at startup by the crash detector.
            acquired = true;
        }
        finally
        {
            // A handle that no guard takes over is closed here on every path, an unexpected wait
            // exception included, so a refused process never keeps the mutex referenced until
            // finalization.
            if (!acquired)
            {
                mutex.Dispose();
            }
        }

        return acquired ? new SingleInstanceGuard(mutex) : null;
    }

    /// <summary>
    /// Releases the mutex and closes its handle, so another instance can start. Safe to call more than
    /// once. Call it on the thread that acquired the guard: on any other thread the handle is closed
    /// without releasing ownership, which lasts until the owning thread or the process ends.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (ReferenceEquals(Thread.CurrentThread, _ownerThread))
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    // The Win32 errors with which Windows rejects a malformed object name: a namespace or directory
    // that does not exist, an empty or badly separated name, or one too long for the object manager.
    // The name comes from the caller, so these are programming errors and propagate.
    private static bool IsMalformedName(IOException exception) =>
        exception.HResult is PathNotFoundHResult
            or InvalidNameHResult
            or BadPathNameHResult
            or FileNameTooLongHResult;
}
