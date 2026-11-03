using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// A deterministic stand-in for the UI thread's <see cref="SynchronizationContext"/>, so tests can
/// observe work that production code posts to its captured context without a WPF dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// Inline mode (the default) runs every <see cref="Post"/> immediately on the posting thread.
/// A callback exception therefore propagates to the poster, which may be a background thread such
/// as the sensor thread, where it surfaces far from the test. Tests that post from background
/// threads should prefer queued mode.
/// </para>
/// <para>
/// Queued mode stores posts from any thread until the test drains them on its own thread with
/// <see cref="RunPending"/>, <see cref="RunUntil"/> or <see cref="RunAsync"/>, which makes the
/// test thread the context's single "UI thread".
/// </para>
/// <para>
/// Every callback runs with <see cref="SynchronizationContext.Current"/> set to this instance, and
/// <see cref="CreateCopy"/> returns this instance, so captured-context identity checks hold.
/// Exceptions thrown by callbacks are never swallowed.
/// </para>
/// </remarks>
public sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    // Longest single wait while pumping, which bounds how late a condition or a task completion
    // that arrives without a post is noticed.
    private static readonly TimeSpan _pumpSlice = TimeSpan.FromMilliseconds(10);

    private static readonly TimeSpan _defaultRunTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private int _postCount;

    /// <summary>Creates a context that runs posts inline, or queues them when <paramref name="queuePosts"/> is true.</summary>
    /// <param name="queuePosts">True to queue posts until a pump method drains them.</param>
    public ImmediateSynchronizationContext(bool queuePosts = false)
    {
        QueuesPosts = queuePosts;
    }

    /// <summary>Gets whether posts are queued (true) or run inline on the posting thread (false).</summary>
    public bool QueuesPosts { get; }

    /// <summary>Gets the number of queued callbacks not yet run. Always 0 in inline mode.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Gets the total number of accepted <see cref="Post"/> calls, in either mode.</summary>
    public int PostCount => Volatile.Read(ref _postCount);

    /// <summary>
    /// Runs <paramref name="d"/> inline on the calling thread, or queues it for a pump method in
    /// queued mode. Safe to call from any thread.
    /// </summary>
    /// <param name="d">The callback to run.</param>
    /// <param name="state">The state passed to the callback.</param>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Interlocked.Increment(ref _postCount);

        if (!QueuesPosts)
        {
            Execute(d, state);
            return;
        }

        lock (_gate)
        {
            _queue.Enqueue((d, state));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Runs <paramref name="d"/> inline on the calling thread, in either mode.</summary>
    /// <param name="d">The callback to run.</param>
    /// <param name="state">The state passed to the callback.</param>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Execute(d, state);
    }

    /// <summary>Returns this instance, so a captured copy compares equal to the original.</summary>
    /// <returns>This context.</returns>
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs queued callbacks one at a time on the calling thread until the queue is empty,
    /// including callbacks posted while draining.
    /// </summary>
    /// <remarks>
    /// A callback exception propagates to the caller at once and leaves the remaining callbacks
    /// queued. The drain has no time limit, so work that keeps reposting itself never lets it
    /// return; <see cref="RunUntil"/> and <see cref="RunAsync"/> bound their drains.
    /// </remarks>
    /// <returns>The number of callbacks run; always 0 in inline mode.</returns>
    public int RunPending()
    {
        int run = 0;
        while (TryDequeue(out SendOrPostCallback? callback, out object? state))
        {
            run++;
            Execute(callback, state);
        }

        return run;
    }

    /// <summary>
    /// Blocks until at least one callback is queued or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="timeout">The longest time to wait; <see cref="TimeSpan.Zero"/> only checks.</param>
    /// <returns>True when a callback is queued; false on timeout.</returns>
    public bool WaitForPending(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        long start = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            while (_queue.Count == 0)
            {
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_gate, ToWaitMilliseconds(remaining));
            }

            return true;
        }
    }

    /// <summary>
    /// Pumps queued callbacks on the calling thread until <paramref name="condition"/> holds or
    /// <paramref name="timeout"/> of real time elapses.
    /// </summary>
    /// <remarks>
    /// The condition is evaluated after each drain, the first time before any wait, so a condition
    /// that already holds returns at once. Between drains the method waits for a post for at most
    /// 10 ms, so in inline mode it polls the condition at that interval. Each drain stops between
    /// callbacks once <paramref name="timeout"/> has elapsed, so work that keeps reposting itself
    /// cannot hold the method past its limit; the condition is still evaluated after that last
    /// drain, so a condition that holds at the deadline returns true.
    /// </remarks>
    /// <param name="condition">The condition to reach; evaluated on the calling thread.</param>
    /// <param name="timeout">The real-time limit.</param>
    /// <returns>True when the condition held; false on timeout.</returns>
    public bool RunUntil(Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            RunPendingUntil(start, timeout);
            if (condition())
            {
                return true;
            }

            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            WaitForPending(remaining < _pumpSlice ? remaining : _pumpSlice);
        }
    }

    /// <summary>
    /// Makes this instance the calling thread's <see cref="SynchronizationContext.Current"/> until
    /// the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Disposing the scope restores the previous context. It is idempotent and must happen on the
    /// thread that called <see cref="Install"/>; disposing on another thread throws
    /// <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <returns>The scope that restores the previous context.</returns>
    public IDisposable Install()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        return new InstallScope(previous);
    }

    /// <summary>
    /// Runs an asynchronous test body with this instance installed, pumping its posted
    /// continuations on the calling thread until the body's task completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is invoked while this instance is installed, so its awaits capture it and their
    /// continuations are posted here. The method pumps synchronously, waiting at most 10 ms between
    /// drains for a post or the task's completion, then drains once more after completion. It never
    /// blocks on the task itself, so queued continuations cannot deadlock it; it returns the
    /// completed task, and awaiting that task rethrows the body's exception.
    /// </para>
    /// <para>
    /// Every drain, the final one included, stops between callbacks once the limit has elapsed, so
    /// a body that keeps yielding, or callbacks that keep reposting themselves, cannot hold the
    /// method past its limit.
    /// </para>
    /// <para>
    /// A synchronous exception from <paramref name="body"/>, a callback exception raised while
    /// pumping, and the timeout all propagate to the caller after the previous context is restored.
    /// </para>
    /// </remarks>
    /// <param name="body">The asynchronous body to run.</param>
    /// <param name="timeout">The real-time limit; 10 seconds when null.</param>
    /// <returns>The body's task, already completed.</returns>
    /// <exception cref="TimeoutException">
    /// The body's task did not complete within the limit, or it completed but posted callbacks were
    /// still arriving when the limit elapsed. In the second case a faulted body's exception is the
    /// inner exception.
    /// </exception>
    public Task RunAsync(Func<Task> body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        TimeSpan limit = timeout ?? _defaultRunTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero, nameof(timeout));
        long start = Stopwatch.GetTimestamp();

        using IDisposable scope = Install();
        Task task = body() ?? throw new InvalidOperationException("The asynchronous body returned a null task.");

        while (!task.IsCompleted)
        {
            RunPendingUntil(start, limit);
            if (task.IsCompleted)
            {
                break;
            }

            TimeSpan remaining = limit - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The asynchronous body did not complete within {limit}; {PendingCount} posted callback(s) are still pending.");
            }

            WaitForPending(remaining < _pumpSlice ? remaining : _pumpSlice);
        }

        if (!RunPendingUntil(start, limit))
        {
            throw new TimeoutException(
                $"The asynchronous body completed, but posted callbacks were still arriving when the limit of {limit} elapsed; {PendingCount} posted callback(s) are still pending.",
                task.Exception);
        }

        return task;
    }

    // Runs queued callbacks like RunPending, but checks the limit after each one, so work that keeps
    // reposting itself cannot hold a timeout-bearing pump past its deadline. A callback exception
    // still propagates at once. Returns true when the queue was found empty, and false when the
    // limit elapsed with callbacks still queued.
    private bool RunPendingUntil(long start, TimeSpan limit)
    {
        while (TryDequeue(out SendOrPostCallback? callback, out object? state))
        {
            Execute(callback, state);
            if (Stopwatch.GetElapsedTime(start) >= limit)
            {
                return PendingCount == 0;
            }
        }

        return true;
    }

    // Runs a callback as the context's own thread would: with Current set to this instance, and
    // the caller's context restored afterwards even when the callback throws.
    private void Execute(SendOrPostCallback callback, object? state)
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            callback(state);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    // Dequeues under the lock; callbacks run outside it because they may post again.
    private bool TryDequeue([NotNullWhen(true)] out SendOrPostCallback? callback, out object? state)
    {
        lock (_gate)
        {
            if (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
            {
                callback = item.Callback;
                state = item.State;
                return true;
            }
        }

        callback = null;
        state = null;
        return false;
    }

    // Rounds up, so a sub-millisecond remainder waits once instead of spinning on zero-length
    // waits, and caps the value at the largest timeout Monitor.Wait accepts.
    private static int ToWaitMilliseconds(TimeSpan remaining) =>
        (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);

    private sealed class InstallScope : IDisposable
    {
        private readonly SynchronizationContext? _previous;
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private bool _disposed;

        public InstallScope(SynchronizationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // SynchronizationContext.Current is per thread: restoring it elsewhere would leave
            // this context installed on the original thread and clobber the other thread's.
            if (Environment.CurrentManagedThreadId != _threadId)
            {
                throw new InvalidOperationException(
                    "The context scope must be disposed on the thread that installed it.");
            }

            _disposed = true;
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }
}
