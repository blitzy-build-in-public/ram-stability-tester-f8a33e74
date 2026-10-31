using RamQuick.App.Services;

namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// One recorded call to <see cref="FakeUserPrompt.Ask"/> or <see cref="FakeUserPrompt.Inform"/>.
/// </summary>
/// <param name="Title">The dialog title exactly as the caller passed it.</param>
/// <param name="Message">The question or message exactly as the caller passed it.</param>
/// <param name="ThreadId">
/// <see cref="Environment.CurrentManagedThreadId"/> of the calling thread, so tests can assert
/// which thread prompted.
/// </param>
public sealed record PromptCall(string Title, string Message, int ThreadId);

/// <summary>
/// An <see cref="IUserPrompt"/> that shows no UI and never blocks: it records every call and
/// answers questions from a FIFO of scripted answers, falling back to <see cref="Answer"/>.
/// </summary>
/// <remarks>
/// <para>
/// A fresh instance answers <see cref="PromptResult.No"/>, so a headless host that never scripts
/// an answer can never trigger an elevated restart.
/// </para>
/// <para>
/// Every member is thread-safe: the controller may prompt from its UI context while a test reads
/// the recorded calls from another thread. The call lists are snapshots copied under the lock.
/// </para>
/// </remarks>
public sealed class FakeUserPrompt : IUserPrompt
{
    private readonly object _gate = new();
    private readonly Queue<PromptResult> _scriptedAnswers = new();
    private readonly List<PromptCall> _askCalls = [];
    private readonly List<PromptCall> _informCalls = [];

    // PromptResult.Yes is the enum's zero value, so the safe default must be set explicitly.
    private PromptResult _answer = PromptResult.No;

    /// <summary>
    /// The answer <see cref="Ask"/> returns once the scripted answers are used up.
    /// Defaults to <see cref="PromptResult.No"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="PromptResult"/>.</exception>
    public PromptResult Answer
    {
        get
        {
            lock (_gate)
            {
                return _answer;
            }
        }

        set
        {
            ThrowIfUndefined(value);
            lock (_gate)
            {
                _answer = value;
            }
        }
    }

    /// <summary>The calls to <see cref="Ask"/>, oldest first, as a snapshot.</summary>
    public IReadOnlyList<PromptCall> AskCalls
    {
        get
        {
            lock (_gate)
            {
                return _askCalls.ToArray();
            }
        }
    }

    /// <summary>The calls to <see cref="Inform"/>, oldest first, as a snapshot.</summary>
    public IReadOnlyList<PromptCall> InformCalls
    {
        get
        {
            lock (_gate)
            {
                return _informCalls.ToArray();
            }
        }
    }

    /// <summary>The number of calls to <see cref="Ask"/> so far.</summary>
    public int AskCount
    {
        get
        {
            lock (_gate)
            {
                return _askCalls.Count;
            }
        }
    }

    /// <summary>The number of calls to <see cref="Inform"/> so far.</summary>
    public int InformCount
    {
        get
        {
            lock (_gate)
            {
                return _informCalls.Count;
            }
        }
    }

    /// <summary>
    /// Queues an answer for a later <see cref="Ask"/>. Queued answers are returned in the order
    /// they were queued, one per call, before <see cref="Answer"/> applies.
    /// </summary>
    /// <param name="answer">The answer to return.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="answer"/> is not a defined <see cref="PromptResult"/>.</exception>
    public void EnqueueAnswer(PromptResult answer)
    {
        ThrowIfUndefined(answer);
        lock (_gate)
        {
            _scriptedAnswers.Enqueue(answer);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Records the call, then returns the oldest scripted answer if one is queued, otherwise
    /// <see cref="Answer"/>. Shows no UI and returns immediately.
    /// </remarks>
    public PromptResult Ask(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var call = new PromptCall(title, message, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            _askCalls.Add(call);
            return _scriptedAnswers.TryDequeue(out PromptResult scripted) ? scripted : _answer;
        }
    }

    /// <inheritdoc />
    /// <remarks>Records the call. Shows no UI and returns immediately.</remarks>
    public void Inform(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var call = new PromptCall(title, message, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            _informCalls.Add(call);
        }
    }

    // The real prompt only ever answers Yes or No; an undefined value would make a test assert
    // behaviour the production code can never see.
    private static void ThrowIfUndefined(PromptResult answer)
    {
        if (!Enum.IsDefined(answer))
        {
            throw new ArgumentOutOfRangeException(nameof(answer), answer, "The answer must be PromptResult.Yes or PromptResult.No.");
        }
    }
}
