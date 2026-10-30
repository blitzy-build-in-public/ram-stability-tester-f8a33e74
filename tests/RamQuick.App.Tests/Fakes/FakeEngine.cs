using System.Collections.ObjectModel;
using System.Globalization;
using RamQuick.Core;
using RamQuick.Interop;

// Linked into RamQuick.IntegrationTests as well: this file may depend on the BCL, RamQuick.Core and the
// public RamQuick.Interop contracts only, never on a test framework, another fake, an internal Interop
// type, or an App, Logging, Sensors or Plans type.
namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// Deterministic builders for the records a <see cref="FakeEngine"/> run exchanges: the machine
/// snapshot, Prepared payloads, progress snapshots, error records and step outcomes.
/// </summary>
public static class FakeEngineData
{
    /// <summary>One mebibyte, in bytes.</summary>
    public const ulong MiB = 1UL << 20;

    /// <summary>One gibibyte, in bytes.</summary>
    public const ulong GiB = 1UL << 30;

    /// <summary>
    /// The fake virtual address of the first byte of the tested region; error addresses are this base
    /// plus their region offset.
    /// </summary>
    public const ulong BaseAddress = 0x0000_023A_0000_0000UL;

    private const ulong DefaultExpected = 0x5A3C_5A3C_5A3C_5A3CUL;

    private static readonly TimeSpan _defaultPrepareTime = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _defaultTimeIntoTest = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _defaultTimeSinceWrite = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan _defaultErrorSpacing = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Gets the machine a new <see cref="FakeEngine"/> reports: "Fake CPU" with 8 logical
    /// processors in one group, AVX2, 32 GiB installed and 24 GiB available, a 2 MiB large-page
    /// minimum and no <c>SeLockMemoryPrivilege</c>.
    /// </summary>
    public static SystemSnapshot DefaultSystem { get; } = new()
    {
        CpuName = "Fake CPU",
        LogicalProcessors = 8,
        ProcessorGroups = 1,
        BestIsa = InstructionSet.Avx2,
        TotalPhysicalBytes = 32 * GiB,
        AvailablePhysicalBytes = 24 * GiB,
        LargePageMinimum = 2 * MiB,
        LockPrivilegeHeld = false,
    };

    /// <summary>Builds a Prepared payload with virtual addresses.</summary>
    /// <param name="testedBytes">The bytes the engine locked for testing.</param>
    /// <param name="workerCount">The number of pinned workers; at least 1.</param>
    /// <param name="pageMode">The pages backing the region.</param>
    /// <param name="instructionSet">The kernels' instruction set; SSE2 is the reduced set.</param>
    /// <param name="prepareTime">The preparation time; 50 ms when null.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="workerCount"/> is below 1, or an enumeration value is undefined, or
    /// <paramref name="prepareTime"/> is negative.
    /// </exception>
    public static EnginePrepared Prepared(
        ulong testedBytes,
        int workerCount,
        PageMode pageMode = PageMode.Standard,
        InstructionSet instructionSet = InstructionSet.Avx2,
        TimeSpan? prepareTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        RequireDefined(pageMode, nameof(pageMode));
        RequireDefined(instructionSet, nameof(instructionSet));
        TimeSpan time = prepareTime ?? _defaultPrepareTime;
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero, nameof(prepareTime));

        return new EnginePrepared(
            TestedBytes: testedBytes,
            WorkerCount: workerCount,
            PageMode: pageMode,
            InstructionSet: instructionSet,
            AddressKind: AddressKind.Virtual,
            PrepareTime: time);
    }

    /// <summary>Builds a progress snapshot whose coverage is computed from the byte counts.</summary>
    /// <param name="stepIndex">The zero-based index of the current step.</param>
    /// <param name="stepCount">The number of steps in the plan.</param>
    /// <param name="name">The current step's name.</param>
    /// <param name="elapsed">The test time so far.</param>
    /// <param name="remaining">The estimated time remaining; null means "calculating…".</param>
    /// <param name="verifiedBytes">The bytes verified so far in the run.</param>
    /// <param name="regionBytes">The bytes under test.</param>
    /// <param name="totalErrors">The run's true error total so far.</param>
    /// <param name="passIndex">The zero-based pass of the current step.</param>
    /// <param name="passesPlanned">The passes the current step plans to run.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static RunProgress Progress(
        int stepIndex,
        int stepCount,
        string name,
        TimeSpan elapsed,
        TimeSpan? remaining,
        ulong verifiedBytes,
        ulong regionBytes,
        ulong totalErrors,
        int passIndex = 0,
        int passesPlanned = 1)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new RunProgress
        {
            StepIndex = stepIndex,
            StepCount = stepCount,
            StepName = name,
            PassIndex = passIndex,
            PassesPlanned = passesPlanned,
            Elapsed = elapsed,
            Remaining = remaining,
            VerifiedBytes = verifiedBytes,
            RegionBytes = regionBytes,
            CoveragePercent = RunProgress.CoveragePercentOf(verifiedBytes, regionBytes),
            TotalErrors = totalErrors,
        };
    }

    /// <summary>
    /// Builds one confirmed error: <paramref name="bit"/> flipped in the word at
    /// <paramref name="wordAddress"/>, with a re-read value consistent with
    /// <paramref name="rereadOutcome"/>.
    /// </summary>
    /// <param name="sequence">The engine's confirmation sequence number.</param>
    /// <param name="wordAddress">The 8-byte-aligned virtual address of the failing word.</param>
    /// <param name="bit">The flipped bit, 0–63.</param>
    /// <param name="stepIndex">The zero-based index of the failing step.</param>
    /// <param name="stepName">The failing step's name.</param>
    /// <param name="accessType">The verification primitive that saw the mismatch.</param>
    /// <param name="rereadOutcome">
    /// How the flushed re-read compares: the expected value, the same wrong value, or a third value.
    /// </param>
    /// <param name="timeIntoTest">The test time of the error; 1 s when null.</param>
    /// <param name="expected">The value written.</param>
    /// <param name="regionOffset">
    /// The byte offset in the tested region; when null, the address minus <see cref="BaseAddress"/>,
    /// or the address itself when it lies below the base.
    /// </param>
    /// <param name="passIndex">The zero-based pass of the failing step.</param>
    /// <param name="timeSinceWrite">The time between the word's write and its verification; 1 ms when null.</param>
    /// <param name="workerIndex">The worker that confirmed the error, also used as its logical processor.</param>
    /// <param name="activeWorkers">The workers active in the failing step.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stepName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bit"/> is outside 0–63, <paramref name="wordAddress"/> is not 8-byte aligned,
    /// or an enumeration value is undefined.
    /// </exception>
    public static ErrorRecord Error(
        ulong sequence,
        ulong wordAddress,
        int bit,
        int stepIndex,
        string stepName,
        AccessType accessType = AccessType.VerifyAfterWrite,
        RereadOutcome rereadOutcome = RereadOutcome.RereadRepeatsActual,
        TimeSpan? timeIntoTest = null,
        ulong expected = DefaultExpected,
        ulong? regionOffset = null,
        int passIndex = 0,
        TimeSpan? timeSinceWrite = null,
        int workerIndex = 0,
        int activeWorkers = 8)
    {
        ArgumentNullException.ThrowIfNull(stepName);
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 63);
        if ((wordAddress & 7UL) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wordAddress),
                wordAddress,
                "A word address must be 8-byte aligned.");
        }

        RequireDefined(accessType, nameof(accessType));
        RequireDefined(rereadOutcome, nameof(rereadOutcome));

        ulong actual = expected ^ (1UL << bit);
        ulong reread = rereadOutcome switch
        {
            RereadOutcome.RereadMatchesExpected => expected,
            RereadOutcome.RereadRepeatsActual => actual,

            // A neighbouring bit flipped instead, so the re-read differs from both values.
            _ => expected ^ (1UL << ((bit + 1) & 63)),
        };

        return new ErrorRecord
        {
            Sequence = sequence,
            Address = wordAddress,
            AddressKind = AddressKind.Virtual,
            RegionOffset = regionOffset ?? (wordAddress >= BaseAddress ? wordAddress - BaseAddress : wordAddress),
            Expected = expected,
            Actual = actual,
            Reread = reread,
            RereadOutcome = rereadOutcome,
            StepIndex = stepIndex,
            StepName = stepName,
            PassIndex = passIndex,
            AccessType = accessType,
            TimeSinceWrite = timeSinceWrite ?? _defaultTimeSinceWrite,
            TimeIntoTest = timeIntoTest ?? _defaultTimeIntoTest,
            WorkerIndex = workerIndex,
            LogicalProcessor = workerIndex,
            ProcessorGroup = 0,
            ActiveWorkers = activeWorkers,
        };
    }

    /// <summary>
    /// Returns the word address of <paramref name="regionOffset"/> in the fake tested region: the
    /// offset rounded down to 8 bytes, plus <see cref="BaseAddress"/>.
    /// </summary>
    /// <param name="regionOffset">The byte offset in the tested region.</param>
    /// <returns>The 8-byte-aligned virtual address.</returns>
    public static ulong AddressAt(ulong regionOffset) => BaseAddress + (regionOffset & ~7UL);

    /// <summary>
    /// Builds <paramref name="count"/> errors at <c>AddressAt(firstRegionOffset + i × strideBytes)</c>,
    /// with consecutive sequences and increasing times into the test.
    /// </summary>
    /// <remarks>
    /// The default 4 KiB stride, like any stride of at least 8 bytes, gives every record its own word,
    /// spread across the region; a stride of 0 repeats one word, as a stuck bit does.
    /// </remarks>
    /// <param name="count">The number of records; 0 yields an empty list.</param>
    /// <param name="firstSequence">The sequence number of the first record.</param>
    /// <param name="bit">The bit flipped in every record, 0–63.</param>
    /// <param name="stepIndex">The zero-based index of the failing step.</param>
    /// <param name="stepName">The failing step's name.</param>
    /// <param name="accessType">The verification primitive of every record.</param>
    /// <param name="firstRegionOffset">The region offset of the first record.</param>
    /// <param name="strideBytes">The region-offset distance between consecutive records.</param>
    /// <param name="firstTimeIntoTest">The test time of the first record; 1 s when null.</param>
    /// <param name="spacing">The test-time distance between consecutive records; 1 ms when null.</param>
    /// <returns>The records, in sequence order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> or <paramref name="spacing"/> is negative, or a record is invalid
    /// for <see cref="Error"/>.
    /// </exception>
    /// <exception cref="OverflowException">An offset or sequence number exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static IReadOnlyList<ErrorRecord> Errors(
        int count,
        ulong firstSequence = 1,
        int bit = 17,
        int stepIndex = 0,
        string stepName = "High-toggle sweep",
        AccessType accessType = AccessType.VerifyAfterWrite,
        ulong firstRegionOffset = 0,
        ulong strideBytes = 4096,
        TimeSpan? firstTimeIntoTest = null,
        TimeSpan? spacing = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        TimeSpan step = spacing ?? _defaultErrorSpacing;
        ArgumentOutOfRangeException.ThrowIfLessThan(step, TimeSpan.Zero, nameof(spacing));
        if (count == 0)
        {
            return ReadOnlyCollection<ErrorRecord>.Empty;
        }

        TimeSpan first = firstTimeIntoTest ?? _defaultTimeIntoTest;
        var records = new ErrorRecord[count];
        for (int i = 0; i < count; i++)
        {
            ulong offset = checked(firstRegionOffset + ((ulong)i * strideBytes));
            records[i] = Error(
                sequence: checked(firstSequence + (ulong)i),
                wordAddress: AddressAt(offset),
                bit: bit,
                stepIndex: stepIndex,
                stepName: stepName,
                accessType: accessType,
                timeIntoTest: first + TimeSpan.FromTicks(checked(step.Ticks * i)));
        }

        return Array.AsReadOnly(records);
    }

    /// <summary>Builds a step outcome from the step's attributes and the measured values.</summary>
    /// <param name="attributes">The step's attributes.</param>
    /// <param name="actualDuration">The measured duration; the step's time budget when null.</param>
    /// <param name="passes">The passes completed.</param>
    /// <param name="verifiedBytes">The bytes verified in the step.</param>
    /// <param name="errors">The step's true error count.</param>
    /// <param name="completed">Whether the step ran to the end of its schedule.</param>
    /// <param name="overrun">The time beyond the budget; zero when null.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attributes"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="passes"/>, <paramref name="actualDuration"/> or <paramref name="overrun"/> is negative.
    /// </exception>
    public static StepOutcome Outcome(
        StepAttributes attributes,
        TimeSpan? actualDuration = null,
        int passes = 1,
        ulong verifiedBytes = 0,
        ulong errors = 0,
        bool completed = true,
        TimeSpan? overrun = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentOutOfRangeException.ThrowIfNegative(passes);
        TimeSpan duration = actualDuration ?? attributes.TimeBudget;
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero, nameof(actualDuration));
        TimeSpan excess = overrun ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(excess, TimeSpan.Zero, nameof(overrun));

        return new StepOutcome
        {
            Attributes = attributes,
            ActualDuration = duration,
            PassesCompleted = passes,
            VerifiedBytes = verifiedBytes,
            Errors = errors,
            Completed = completed,
            Overrun = excess,
        };
    }

    private static void RequireDefined<TEnum>(TEnum value, string paramName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                string.Create(CultureInfo.InvariantCulture, $"{paramName} must be a defined {typeof(TEnum).Name} value."));
        }
    }
}

/// <summary>
/// A scriptable <see cref="IEngine"/> that never loads the native engine: it reports a configurable
/// machine snapshot, creates <see cref="FakeEngineSession"/>s that tests drive event by event, and
/// can fail each call once or persistently.
/// </summary>
/// <remarks>
/// <para>
/// Every member is thread-safe. Sessions capture the context override passed to the constructor, or
/// else <see cref="SynchronizationContext.Current"/> of the creating thread, or else an inline context
/// that runs every post at once on the posting thread with itself installed as current.
/// </para>
/// <para>
/// Creation validates the options exactly as the production wrapper does, so a malformed plan fails
/// here as it would in the application. Nothing large is allocated and no thread is started.
/// </para>
/// </remarks>
public sealed class FakeEngine : IEngine
{
    private readonly object _gate = new();
    private readonly SynchronizationContext? _contextOverride;

    // Guarded by _gate.
    private readonly List<FakeEngineSession> _sessions = new();

    // Read and written with Volatile or Interlocked: tests script the fake while the code under test
    // calls it from other threads.
    private SystemSnapshot _system;
    private Exception? _querySystemException;
    private Exception? _createSessionException;
    private Exception? _startException;
    private int _querySystemCount;
    private int _createSessionCount;

    /// <summary>Creates a fake engine.</summary>
    /// <param name="system">The machine to report; <see cref="FakeEngineData.DefaultSystem"/> when null.</param>
    /// <param name="contextOverride">
    /// The context every session raises its events on, regardless of the creating thread; when null,
    /// sessions capture the creating thread's context, or an inline context when it has none.
    /// </param>
    public FakeEngine(SystemSnapshot? system = null, SynchronizationContext? contextOverride = null)
    {
        _system = system ?? FakeEngineData.DefaultSystem;
        _contextOverride = contextOverride;
    }

    /// <summary>Raised synchronously on the creating thread after a session has been recorded, before it is returned.</summary>
    public event EventHandler<FakeEngineSession>? SessionCreated;

    /// <summary>Gets or sets the machine <see cref="QuerySystem"/> reports and new sessions receive.</summary>
    /// <exception cref="ArgumentNullException">The value being set is null.</exception>
    public SystemSnapshot System
    {
        get => Volatile.Read(ref _system);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _system, value);
        }
    }

    /// <summary>
    /// Gets or sets an exception every <see cref="QuerySystem"/> call throws until it is cleared with
    /// null. Persistent, unlike the other injected failures.
    /// </summary>
    public Exception? QuerySystemException
    {
        get => Volatile.Read(ref _querySystemException);
        set => Volatile.Write(ref _querySystemException, value);
    }


    /// <summary>
    /// Gets or sets an exception the next <see cref="CreateSession"/> call throws. One-shot: that call
    /// clears it before throwing, so the call after it succeeds.
    /// </summary>
    public Exception? CreateSessionException
    {
        get => Volatile.Read(ref _createSessionException);
        set => Volatile.Write(ref _createSessionException, value);
    }

    /// <summary>
    /// Gets or sets an exception handed to the next session <see cref="CreateSession"/> creates, whose
    /// <see cref="FakeEngineSession.Start"/> then throws it, for example
    /// <c>new EngineException(EngineResultCode.ThreadCreate, "…")</c>. One-shot: creating that session
    /// clears it here, and throwing it clears it on the session.
    /// </summary>
    public Exception? StartException
    {
        get => Volatile.Read(ref _startException);
        set => Volatile.Write(ref _startException, value);
    }

    /// <summary>Gets the number of <see cref="QuerySystem"/> calls, failed ones included.</summary>
    public int QuerySystemCount => Volatile.Read(ref _querySystemCount);

    /// <summary>
    /// Gets the number of <see cref="CreateSession"/> calls with non-null options, failed ones included.
    /// </summary>
    public int CreateSessionCount => Volatile.Read(ref _createSessionCount);

    /// <summary>Gets a snapshot of every session created so far, in creation order.</summary>
    public IReadOnlyList<FakeEngineSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_sessions.ToArray());
            }
        }
    }

    /// <summary>Gets the most recently created session, or null before the first.</summary>
    public FakeEngineSession? LastSession
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count == 0 ? null : _sessions[^1];
            }
        }
    }

    /// <summary>Sets <see cref="SystemSnapshot.AvailablePhysicalBytes"/> of <see cref="System"/>.</summary>
    /// <param name="availableBytes">The available physical memory to report, in bytes.</param>
    /// <returns>This engine, for chaining.</returns>
    public FakeEngine WithAvailableBytes(ulong availableBytes) =>
        UpdateSystem(current => current with { AvailablePhysicalBytes = availableBytes });

    /// <summary>Sets <see cref="SystemSnapshot.LogicalProcessors"/> of <see cref="System"/>.</summary>
    /// <param name="count">The logical processor count to report; at least 1.</param>
    /// <returns>This engine, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
    public FakeEngine WithLogicalProcessors(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return UpdateSystem(current => current with { LogicalProcessors = count });
    }

    /// <summary>
    /// Sets <see cref="SystemSnapshot.BestIsa"/> of <see cref="System"/>. Sessions without an
    /// instruction-set override prepare with it, so <see cref="InstructionSet.Sse2"/> drives the
    /// reduced-instruction-set notice.
    /// </summary>
    /// <param name="isa">The best instruction set to report.</param>
    /// <returns>This engine, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="isa"/> is undefined.</exception>
    public FakeEngine WithIsa(InstructionSet isa)
    {
        if (!Enum.IsDefined(isa))
        {
            throw new ArgumentOutOfRangeException(nameof(isa), isa, "The instruction set must be a defined value.");
        }

        return UpdateSystem(current => current with { BestIsa = isa });
    }

    /// <inheritdoc/>
    /// <remarks>Counts the call, then throws <see cref="QuerySystemException"/> when one is set.</remarks>
    public SystemSnapshot QuerySystem()
    {
        Interlocked.Increment(ref _querySystemCount);
        Exception? failure = QuerySystemException;
        if (failure is not null)
        {
            throw failure;
        }

        return System;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Counts the call; throws and clears <see cref="CreateSessionException"/> when one is set;
    /// validates the options; then creates a session that consumes <see cref="StartException"/>,
    /// records it and raises <see cref="SessionCreated"/>.
    /// </remarks>
    public IEngineSession CreateSession(EngineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Interlocked.Increment(ref _createSessionCount);

        Exception? failure = Interlocked.Exchange(ref _createSessionException, null);
        if (failure is not null)
        {
            throw failure;
        }

        options.Validate();

        SynchronizationContext context = _contextOverride ?? SynchronizationContext.Current ?? InlineContext.Instance;
        Exception? startException = Interlocked.Exchange(ref _startException, null);
        var session = new FakeEngineSession(options, context, System, startException);

        lock (_gate)
        {
            _sessions.Add(session);
        }

        SessionCreated?.Invoke(this, session);
        return session;
    }

    // A compare-and-swap loop, so concurrent fluent updates and setter writes never lose each other.
    private FakeEngine UpdateSystem(Func<SystemSnapshot, SystemSnapshot> update)
    {
        while (true)
        {
            SystemSnapshot current = Volatile.Read(ref _system);
            SystemSnapshot next = update(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _system, next, current), current))
            {
                return this;
            }
        }
    }

    // Used when the creating thread has no context: runs every post at once on the posting thread,
    // as the application's dispatcher would deliver it, with itself installed as current so that
    // captured-context checks inside handlers hold.
    private sealed class InlineContext : SynchronizationContext
    {
        public static InlineContext Instance { get; } = new();

        public override void Post(SendOrPostCallback d, object? state) => Run(d, state);

        public override void Send(SendOrPostCallback d, object? state) => Run(d, state);

        public override SynchronizationContext CreateCopy() => this;

        private void Run(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
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
    }
}

/// <summary>
/// One call of a session's step gate, recorded by <see cref="FakeEngineSession.InvokeStepGate"/>.
/// </summary>
/// <param name="Index">The zero-based index of the step the gate decided.</param>
/// <param name="Decision">The decision the engine received: the gate's, or Abort when it threw.</param>
/// <param name="StopRequestCountAtReturn">
/// The number of entries in <see cref="FakeEngineSession.StopRequests"/> when the gate returned or
/// threw, before the session's own host-fault handling. A count that already includes a
/// <see cref="StopReason.HostFault"/> request proves the gate requested that stop before returning
/// Abort.
/// </param>
/// <param name="ThreadId">The managed thread id the gate ran on.</param>
/// <param name="Exception">The exception the gate threw, or null when it returned.</param>
public sealed record GateInvocation(
    int Index,
    StepGateDecision Decision,
    int StopRequestCountAtReturn,
    int ThreadId,
    Exception? Exception);


/// <summary>
/// A scriptable <see cref="IEngineSession"/> that emulates the engine's coordinator: tests raise each
/// event, invoke the step gate and publish progress themselves, and observe the stop requests, gate
/// decisions and disposal the code under test produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wrapper semantics.</b> The session behaves as the production wrapper does. Every event is posted to
/// <see cref="Context"/> and raised there one subscriber at a time with this session as sender; a
/// subscriber that throws is reported through <see cref="InvokeCallbackFault"/> and stops the run with a
/// host fault, and the other subscribers still receive the event. The step gate and the progress sink
/// run synchronously on the calling thread, which plays the coordinator. <see cref="RequestStop"/>
/// records its reason before it sets <see cref="StopEvent"/>, and the first recorded reason wins.
/// </para>
/// <para>
/// <b>Disposal.</b> Events raised after <see cref="Dispose"/>, or posted before it and delivered after
/// it, are dropped and counted in <see cref="DroppedEventCount"/>, because the engine raises nothing
/// once the native session is destroyed. After disposal <see cref="Start"/> throws and
/// <see cref="RequestStop"/> does nothing.
/// </para>
/// <para>
/// <b>Threading.</b> Every member is thread-safe: the test thread, a coordinator-emulation thread, the
/// journal writer and the context may all touch one session. No lock is held while a delegate runs or
/// an event is posted.
/// </para>
/// </remarks>
public sealed class FakeEngineSession : IEngineSession
{
    private const string PreparedHandlerName = "Prepared handler";
    private const string StepStartedHandlerName = "StepStarted handler";
    private const string StepFinishedHandlerName = "StepFinished handler";
    private const string ProgressChangedHandlerName = "ProgressChanged handler";
    private const string ErrorsDetectedHandlerName = "ErrorsDetected handler";
    private const string CompletedHandlerName = "Completed handler";
    private const string ReleasedHandlerName = "Released handler";
    private const string StepStartingCallbackName = "on_step_starting";
    private const string ProgressCallbackName = "on_progress";
    private const string CoordinatorThreadName = "FakeEngine coordinator";

    private static readonly TimeSpan _defaultReleaseTime = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly ReadOnlyCollection<StepAttributes> _steps;

    // Never disposed: a coordinator-emulation thread may still be waiting on it when the test disposes
    // the session, and a disposed handle would throw ObjectDisposedException into that wait.
    private readonly ManualResetEvent _stopEvent = new(initialState: false);

    // Set unless a test holds Dispose with BlockDispose. Never disposed, for the reason _stopEvent is
    // not: the thread held in Dispose may still be waking from it when the test ends.
    private readonly ManualResetEventSlim _disposeGate = new(initialState: true);

    // Guarded by _gate.
    private readonly List<StopReason> _stopRequests = new();
    private readonly List<StepOutcome> _finishedSteps = new();
    private readonly List<GateInvocation> _gateInvocations = new();
    private readonly List<(string CallbackName, Exception Exception)> _callbackFaults = new();
    private RunEndReason? _firstRecordedEndReason;

    // Read and written with Volatile or Interlocked.
    private Exception? _startException;
    private Exception? _lastGateException;
    private int _startCount;
    private int _started;
    private int _completedRaised;
    private int _disposeCount;
    private int _disposeThreadId;
    private int _disposed;
    private int _droppedEventCount;

    /// <summary>Creates an unstarted session; <see cref="FakeEngine.CreateSession"/> uses it, and tests may too.</summary>
    /// <param name="options">The session's options, validated here as the wrapper validates them.</param>
    /// <param name="context">The context every event is posted to.</param>
    /// <param name="system">The machine snapshot the session prepares against.</param>
    /// <param name="startException">An exception the first <see cref="Start"/> throws, or null.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="startException"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="EngineSessionOptions.Validate"/> rejected the options.</exception>
    public FakeEngineSession(
        EngineSessionOptions options,
        SynchronizationContext context,
        SystemSnapshot system,
        Exception? startException = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(system);
        options.Validate();

        Options = options;
        Context = context;
        System = system;
        _startException = startException;

        // The wrapper copies the validated steps once, so later changes to the caller's list never reach it.
        _steps = Array.AsReadOnly(options.Steps.ToArray());
    }

    /// <inheritdoc/>
    public event EventHandler<EnginePrepared>? Prepared;

    /// <inheritdoc/>
    public event EventHandler<EngineStepStarted>? StepStarted;

    /// <inheritdoc/>
    public event EventHandler<EngineStepFinished>? StepFinished;

    /// <inheritdoc/>
    public event EventHandler<RunProgress>? ProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<EngineErrorsDetected>? ErrorsDetected;

    /// <inheritdoc/>
    public event EventHandler<EngineCompleted>? Completed;

    /// <inheritdoc/>
    public event EventHandler<EngineReleased>? Released;

    /// <summary>Gets the options the session was created with.</summary>
    public EngineSessionOptions Options { get; }

    /// <summary>Gets the context every event is posted to.</summary>
    public SynchronizationContext Context { get; }

    /// <summary>Gets the machine snapshot the session was created against.</summary>
    public SystemSnapshot System { get; }

    /// <summary>
    /// Gets or sets an exception the next <see cref="Start"/> throws. One-shot: that call clears it, and
    /// the session stays unstarted.
    /// </summary>
    public Exception? StartException
    {
        get => Volatile.Read(ref _startException);
        set => Volatile.Write(ref _startException, value);
    }

    /// <summary>Gets the number of <see cref="Start"/> calls made before disposal, failed ones included.</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>Gets a value indicating whether <see cref="Start"/> has succeeded.</summary>
    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Gets a snapshot of every reason passed to <see cref="RequestStop"/> before disposal, in call order.</summary>
    public IReadOnlyList<StopReason> StopRequests
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_stopRequests.ToArray());
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="RequestStop"/> has recorded at least one request.</summary>
    public bool IsStopRequested
    {
        get
        {
            lock (_gate)
            {
                return _stopRequests.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the session's managed stop event, the one the step gate receives. It is set only after a
    /// stop reason has been recorded, so a gate woken by it always finds <see cref="FirstRecordedEndReason"/>
    /// set.
    /// </summary>
    public WaitHandle StopEvent => _stopEvent;

    /// <summary>
    /// Gets the first end reason recorded, by a stop request, <see cref="SimulateEngineStop"/>, an aborted
    /// gate or disposal before completion, or null while none has been. The engine would end the run with it.
    /// </summary>
    public RunEndReason? FirstRecordedEndReason
    {
        get
        {
            lock (_gate)
            {
                return _firstRecordedEndReason;
            }
        }
    }

    /// <summary>Gets a snapshot of every outcome raised through <see cref="StepFinished"/>, in raise order.</summary>
    public IReadOnlyList<StepOutcome> FinishedSteps
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_finishedSteps.ToArray());
            }
        }
    }

    /// <summary>Gets a snapshot of every step-gate call made through <see cref="InvokeStepGate"/>, in call order.</summary>
    public IReadOnlyList<GateInvocation> GateInvocations
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_gateInvocations.ToArray());
            }
        }
    }

    /// <summary>Gets the exception the step gate threw most recently, or null when it never threw.</summary>
    public Exception? LastGateException => Volatile.Read(ref _lastGateException);

    /// <summary>
    /// Gets a snapshot of every fault reported through <see cref="InvokeCallbackFault"/>, in report order:
    /// gate and progress-sink exceptions and exceptions thrown by event subscribers.
    /// </summary>
    public IReadOnlyList<(string CallbackName, Exception Exception)> CallbackFaults
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_callbackFaults.ToArray());
            }
        }
    }

    /// <summary>Gets the number of <see cref="Dispose"/> calls, a call still held by <see cref="BlockDispose"/> included.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Gets the managed thread id of the first <see cref="Dispose"/> call, or 0 before it.</summary>
    public int DisposeThreadId => Volatile.Read(ref _disposeThreadId);

    /// <summary>Gets a value indicating whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets the number of events dropped because the session had been disposed.</summary>
    public int DroppedEventCount => Volatile.Read(ref _droppedEventCount);

    /// <inheritdoc/>
    /// <remarks>
    /// Non-blocking and raises no event: tests drive every event. Throws <see cref="StartException"/> once
    /// when it is set, leaving the session unstarted.
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Interlocked.Increment(ref _startCount);

        Exception? failure = Interlocked.Exchange(ref _startException, null);
        if (failure is not null)
        {
            throw failure;
        }

        // The engine refuses a second start with RQ_E_INVALID_STATE.
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The engine session has already been started.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Records the request and the first reason before setting <see cref="StopEvent"/>, the order the
    /// wrapper guarantees. Does nothing after disposal.
    /// </remarks>
    public void RequestStop(StopReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, $"{nameof(reason)} must be a defined {nameof(StopReason)}.");
        }

        if (IsDisposed)
        {
            return;
        }

        RunEndReason endReason = reason switch
        {
            StopReason.User => RunEndReason.UserStop,
            StopReason.Thermal => RunEndReason.Thermal,
            _ => RunEndReason.HostFault,
        };

        lock (_gate)
        {
            _stopRequests.Add(reason);
            _firstRecordedEndReason ??= endReason;
        }

        _stopEvent.Set();
    }

    /// <summary>
    /// Records an end reason the engine decides itself, such as a first error with Stop on error on or a
    /// preparation failure, on the same first-writer-wins basis as stop requests. Like the native stop
    /// flag it adds no stop request and leaves <see cref="StopEvent"/> unset.
    /// </summary>
    /// <param name="reason">
    /// The engine's reason, typically <see cref="RunEndReason.FirstError"/>, <see cref="RunEndReason.InsufficientMemory"/>,
    /// <see cref="RunEndReason.AffinityFailed"/> or <see cref="RunEndReason.EngineFault"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is undefined, <see cref="RunEndReason.Completed"/> or
    /// <see cref="RunEndReason.PreviousRunUnfinished"/>, none of which is a stop.
    /// </exception>
    public void SimulateEngineStop(RunEndReason reason)
    {
        if (!Enum.IsDefined(reason) || reason is RunEndReason.Completed or RunEndReason.PreviousRunUnfinished)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "The reason must be an end reason that stops a run.");
        }

        RecordEndReason(reason);
    }

    /// <summary>
    /// Marks the session disposed; idempotent apart from <see cref="DisposeCount"/>, and never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like the wrapper, disposing before <see cref="Completed"/> has been raised stops the run: it records
    /// <see cref="RunEndReason.HostFault"/> as the engine's reason, first writer winning, and then sets
    /// <see cref="StopEvent"/> so a waiting gate wakes. That stop is the engine's, so
    /// <see cref="StopRequests"/> is unchanged.
    /// </para>
    /// <para>
    /// After <see cref="BlockDispose"/>, the first call is counted and its thread recorded, then it blocks
    /// until <see cref="ReleaseDispose"/>, with the session not yet disposed, as <c>rq_session_destroy</c>
    /// blocks while it joins engine threads that never end.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Increment(ref _disposeCount) == 1)
        {
            Volatile.Write(ref _disposeThreadId, Environment.CurrentManagedThreadId);
            _disposeGate.Wait();
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _completedRaised) == 0)
        {
            RecordEndReason(RunEndReason.HostFault);
            _stopEvent.Set();
        }
    }

    /// <summary>
    /// Makes the first <see cref="Dispose"/> call block, once counted, until <see cref="ReleaseDispose"/>:
    /// the stand-in for an engine whose threads never join. A test that calls it releases the session in
    /// a <c>finally</c>, so no thread stays held past the test.
    /// </summary>
    public void BlockDispose() => _disposeGate.Reset();

    /// <summary>Lets a <see cref="Dispose"/> call held by <see cref="BlockDispose"/> finish; later calls return at once.</summary>
    public void ReleaseDispose() => _disposeGate.Set();


    /// <summary>Raises <see cref="Prepared"/>.</summary>
    /// <param name="testedBytes">The bytes locked; <see cref="EngineSessionOptions.RegionBytes"/> when null.</param>
    /// <param name="workerCount">The workers pinned; <see cref="EngineSessionOptions.WorkerCount"/> when null.</param>
    /// <param name="pageMode">The pages backing the region.</param>
    /// <param name="instructionSet">
    /// The kernels' instruction set; the options' override, or else the snapshot's best set, when null.
    /// </param>
    /// <param name="prepareTime">The preparation time; 50 ms when null.</param>
    /// <returns>The record raised.</returns>
    public EnginePrepared RaisePrepared(
        ulong? testedBytes = null,
        int? workerCount = null,
        PageMode pageMode = PageMode.Standard,
        InstructionSet? instructionSet = null,
        TimeSpan? prepareTime = null)
    {
        EnginePrepared prepared = FakeEngineData.Prepared(
            testedBytes ?? Options.RegionBytes,
            workerCount ?? Options.WorkerCount,
            pageMode,
            instructionSet ?? Options.IsaOverride ?? System.BestIsa,
            prepareTime);
        Post(prepared, static session => session.Prepared, PreparedHandlerName);
        return prepared;
    }

    /// <summary>
    /// Raises <see cref="StepStarted"/> for the step at <paramref name="index"/> without calling the
    /// gate; <see cref="RunStep"/> calls the gate first, as the engine does.
    /// </summary>
    /// <param name="index">The zero-based index of the step.</param>
    /// <returns>The record raised.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a step of the plan.</exception>
    public EngineStepStarted RaiseStepStarted(int index)
    {
        EngineStepStarted started = BuildStepStarted(index);
        Post(started, static session => session.StepStarted, StepStartedHandlerName);
        return started;
    }

    /// <summary>Records <paramref name="outcome"/> in <see cref="FinishedSteps"/> and raises <see cref="StepFinished"/>.</summary>
    /// <param name="outcome">The step's outcome.</param>
    /// <returns>The record raised.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is null.</exception>
    public EngineStepFinished RaiseStepFinished(StepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var finished = new EngineStepFinished(outcome);

        // A dropped event is one the engine never reported, so it is not recorded either.
        if (!IsDisposed)
        {
            lock (_gate)
            {
                _finishedSteps.Add(outcome);
            }
        }

        Post(finished, static session => session.StepFinished, StepFinishedHandlerName);
        return finished;
    }

    /// <summary>
    /// Builds the outcome of the step at <paramref name="index"/>, records it and raises
    /// <see cref="StepFinished"/>.
    /// </summary>
    /// <param name="index">The zero-based index of the step.</param>
    /// <param name="actualDuration">The measured duration; the step's time budget when null.</param>
    /// <param name="passes">The passes completed.</param>
    /// <param name="verifiedBytes">
    /// The bytes verified; when null, the region times the step's coverage budget, so a clean fake run
    /// reaches the plan's coverage target.
    /// </param>
    /// <param name="errors">The step's true error count.</param>
    /// <param name="completed">Whether the step ran to the end of its schedule.</param>
    /// <param name="overrun">The time beyond the budget; zero when null.</param>
    /// <returns>The outcome raised.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a step of the plan.</exception>
    /// <exception cref="OverflowException">The default verified bytes exceed <see cref="ulong.MaxValue"/>.</exception>
    public StepOutcome RaiseStepFinished(
        int index,
        TimeSpan? actualDuration = null,
        int passes = 1,
        ulong? verifiedBytes = null,
        ulong errors = 0,
        bool completed = true,
        TimeSpan? overrun = null)
    {
        StepAttributes attributes = StepAt(index);
        ulong verified = verifiedBytes ?? (checked(Options.RegionBytes * (ulong)attributes.CoverageBudgetPercent) / 100UL);
        StepOutcome outcome = FakeEngineData.Outcome(attributes, actualDuration, passes, verified, errors, completed, overrun);
        RaiseStepFinished(outcome);
        return outcome;
    }

    /// <summary>
    /// Emulates one <c>on_progress</c> callback: hands <paramref name="progress"/> to the options'
    /// progress sink synchronously on the calling thread, then raises <see cref="ProgressChanged"/>.
    /// </summary>
    /// <remarks>
    /// A sink that throws is handled as the wrapper's thunk handles it: the fault is reported through
    /// <see cref="InvokeCallbackFault"/> with <c>on_progress</c>, which stops the run with a host fault,
    /// and the snapshot is not posted. After disposal neither the sink nor the event runs.
    /// </remarks>
    /// <param name="progress">The snapshot.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is null.</exception>
    public RunProgress PublishProgress(RunProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (IsDisposed)
        {
            Interlocked.Increment(ref _droppedEventCount);
            return progress;
        }

        try
        {
            Options.ProgressSink?.Invoke(progress);
        }
        catch (Exception exception)
        {
            InvokeCallbackFault(ProgressCallbackName, exception);
            return progress;
        }

        Post(progress, static session => session.ProgressChanged, ProgressChangedHandlerName);
        return progress;
    }

    /// <summary>Raises <see cref="ErrorsDetected"/> with a copy of <paramref name="records"/>.</summary>
    /// <param name="records">
    /// The records confirmed since the previous batch; empty, with a rising total, once the detail cap
    /// has been reached.
    /// </param>
    /// <param name="totalErrors">The run's true error total so far.</param>
    /// <returns>The record raised.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> contains null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The batch is larger than <see cref="EngineSessionOptions.ErrorDetailCap"/>, which the wrapper
    /// rejects as a payload that cannot belong to the session.
    /// </exception>
    public EngineErrorsDetected RaiseErrors(IReadOnlyList<ErrorRecord> records, ulong totalErrors)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > Options.ErrorDetailCap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(records),
                records.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An error batch holds at most the session's detail cap of {Options.ErrorDetailCap} records."));
        }

        var batch = new EngineErrorsDetected(records, totalErrors);
        Post(batch, static session => session.ErrorsDetected, ErrorsDetectedHandlerName);
        return batch;
    }

    /// <summary>
    /// Raises <see cref="Completed"/>. Defaults are derived from the step results, which default to
    /// <see cref="FinishedSteps"/>.
    /// </summary>
    /// <param name="endReason">The engine's end reason.</param>
    /// <param name="totalErrors">The run's true error total.</param>
    /// <param name="verifiedBytes">The bytes verified; the sum over the step results when null.</param>
    /// <param name="elapsed">The test time; the sum of the step results' durations when null.</param>
    /// <param name="firstErrorTime">The test time of the first error, or null for none.</param>
    /// <param name="stepsCompleted">The steps that completed; counted from the step results when null.</param>
    /// <param name="stepResults">The step results; a snapshot of <see cref="FinishedSteps"/> when null.</param>
    /// <param name="osError">The Windows error code of a failing call, or 0.</param>
    /// <param name="endDetail">The engine's diagnostic text; null or empty for none.</param>
    /// <returns>
    /// The record raised. Its end detail follows the wrapper's rule: null for
    /// <see cref="RunEndReason.Completed"/>; "Windows error n: " and the diagnostic for
    /// <see cref="RunEndReason.AffinityFailed"/>, and for <see cref="RunEndReason.InsufficientMemory"/> and
    /// <see cref="RunEndReason.EngineFault"/> with a nonzero <paramref name="osError"/>; otherwise the
    /// diagnostic.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="endReason"/> is undefined or <see cref="RunEndReason.PreviousRunUnfinished"/>, a
    /// time is negative, or <paramref name="stepsCompleted"/> is outside 0 to the number of step results.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="stepResults"/> contains null.</exception>
    public EngineCompleted RaiseCompleted(
        RunEndReason endReason,
        ulong totalErrors = 0,
        ulong? verifiedBytes = null,
        TimeSpan? elapsed = null,
        TimeSpan? firstErrorTime = null,
        int? stepsCompleted = null,
        IReadOnlyList<StepOutcome>? stepResults = null,
        uint osError = 0,
        string? endDetail = null)
    {
        if (!Enum.IsDefined(endReason) || endReason == RunEndReason.PreviousRunUnfinished)
        {
            throw new ArgumentOutOfRangeException(nameof(endReason), endReason, "The engine never reports this end reason.");
        }

        IReadOnlyList<StepOutcome> results = stepResults ?? FinishedSteps;
        if (results.Any(static outcome => outcome is null))
        {
            throw new ArgumentException("The step results must not contain null.", nameof(stepResults));
        }

        ulong verified = verifiedBytes ?? results.Aggregate(0UL, static (sum, outcome) => checked(sum + outcome.VerifiedBytes));
        TimeSpan time = elapsed ?? results.Aggregate(TimeSpan.Zero, static (sum, outcome) => sum + outcome.ActualDuration);
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero, nameof(elapsed));
        if (firstErrorTime is { } firstError)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(firstError, TimeSpan.Zero, nameof(firstErrorTime));
        }

        int completedSteps = stepsCompleted ?? results.Count(static outcome => outcome.Completed);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSteps, nameof(stepsCompleted));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(completedSteps, results.Count, nameof(stepsCompleted));

        string? nativeDetail = string.IsNullOrEmpty(endDetail) ? null : endDetail;
        var completed = new EngineCompleted(
            EndReason: endReason,
            TotalErrors: totalErrors,
            VerifiedBytes: verified,
            Elapsed: time,
            FirstErrorTime: firstErrorTime,
            StepsCompleted: completedSteps,
            StepResults: results,
            OsError: osError,
            NativeEndDetail: nativeDetail,
            EndDetail: FormatEndDetail(endReason, osError, nativeDetail));

        // Marked before the post, as the wrapper does, so a handler that disposes the session finds the run ended.
        if (!IsDisposed)
        {
            Volatile.Write(ref _completedRaised, 1);
        }

        Post(completed, static session => session.Completed, CompletedHandlerName);
        return completed;
    }

    /// <summary>
    /// Raises <see cref="Completed"/> with <see cref="FirstRecordedEndReason"/>, or
    /// <see cref="RunEndReason.Completed"/> when no reason was recorded, as the engine ends a run.
    /// </summary>
    /// <param name="totalErrors">The run's true error total.</param>
    /// <param name="verifiedBytes">The bytes verified; the sum over the step results when null.</param>
    /// <param name="elapsed">The test time; the sum of the step results' durations when null.</param>
    /// <param name="firstErrorTime">The test time of the first error, or null for none.</param>
    /// <param name="stepsCompleted">The steps that completed; counted from the step results when null.</param>
    /// <param name="stepResults">The step results; a snapshot of <see cref="FinishedSteps"/> when null.</param>
    /// <param name="osError">The Windows error code of a failing call, or 0.</param>
    /// <param name="endDetail">The engine's diagnostic text; null or empty for none.</param>
    /// <returns>The record raised; see <see cref="RaiseCompleted"/>.</returns>
    public EngineCompleted CompleteWithFirstRecordedReason(
        ulong totalErrors = 0,
        ulong? verifiedBytes = null,
        TimeSpan? elapsed = null,
        TimeSpan? firstErrorTime = null,
        int? stepsCompleted = null,
        IReadOnlyList<StepOutcome>? stepResults = null,
        uint osError = 0,
        string? endDetail = null)
    {
        return RaiseCompleted(
            FirstRecordedEndReason ?? RunEndReason.Completed,
            totalErrors,
            verifiedBytes,
            elapsed,
            firstErrorTime,
            stepsCompleted,
            stepResults,
            osError,
            endDetail);
    }

    /// <summary>Raises <see cref="Released"/>.</summary>
    /// <param name="releasedBytes">The bytes unlocked and freed.</param>
    /// <param name="releaseTime">The time the release took.</param>
    /// <returns>The record raised.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="releaseTime"/> is negative.</exception>
    public EngineReleased RaiseReleased(ulong releasedBytes, TimeSpan releaseTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(releaseTime, TimeSpan.Zero);
        var released = new EngineReleased(releasedBytes, releaseTime);
        Post(released, static session => session.Released, ReleasedHandlerName);
        return released;
    }

    /// <summary>Raises <see cref="Released"/> for the whole planned region, released in 100 ms.</summary>
    /// <returns>The record raised.</returns>
    public EngineReleased RaiseReleased() => RaiseReleased(Options.RegionBytes, _defaultReleaseTime);

    /// <summary>
    /// Waits <paramref name="realDelay"/> of real time, then raises <see cref="Released"/> from the
    /// thread the delay completes on, for the delayed-release and close-while-running cases.
    /// </summary>
    /// <param name="realDelay">The real-time delay before the release.</param>
    /// <param name="releasedBytes">The bytes released; the planned region when null.</param>
    /// <param name="releaseTime">The reported release time; 100 ms when null.</param>
    /// <param name="cancellationToken">Cancels the wait; no event is raised then.</param>
    /// <returns>The record raised.</returns>
    public async Task<EngineReleased> RaiseReleasedAfterAsync(
        TimeSpan realDelay,
        ulong? releasedBytes = null,
        TimeSpan? releaseTime = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(realDelay, cancellationToken).ConfigureAwait(false);
        return RaiseReleased(releasedBytes ?? Options.RegionBytes, releaseTime ?? _defaultReleaseTime);
    }


    /// <summary>
    /// Emulates one <c>on_step_starting</c> callback: calls the options' step gate synchronously on the
    /// calling thread with <see cref="StopEvent"/> and records a <see cref="GateInvocation"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a gate every step continues and nothing is recorded. A gate that throws is handled as the
    /// wrapper's thunk handles it: the exception is stored in <see cref="LastGateException"/> and reported
    /// through <see cref="InvokeCallbackFault"/>, which requests a host-fault stop, and the step is aborted.
    /// </para>
    /// <para>
    /// Any decision other than Continue is an Abort. When the gate returns Abort while
    /// <see cref="StopEvent"/> is still unset, the session requests the host-fault stop itself, as the
    /// wrapper does, so an aborted step always ends the run with a recorded reason;
    /// <see cref="GateInvocation.StopRequestCountAtReturn"/> is taken before that request.
    /// </para>
    /// </remarks>
    /// <param name="index">The zero-based index of the step about to start.</param>
    /// <returns>The decision the engine receives.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a step of the plan.</exception>
    public StepGateDecision InvokeStepGate(int index)
    {
        EngineStepStarted step = BuildStepStarted(index);
        StepGate? gate = Options.StepGate;
        if (gate is null)
        {
            return StepGateDecision.Continue;
        }

        StepGateDecision decision;
        Exception? failure = null;
        try
        {
            decision = gate(step, _stopEvent);
        }
        catch (Exception exception)
        {
            failure = exception;
            decision = StepGateDecision.Abort;
        }

        int stopRequestCount;
        lock (_gate)
        {
            stopRequestCount = _stopRequests.Count;
        }

        if (failure is not null)
        {
            Volatile.Write(ref _lastGateException, failure);
            InvokeCallbackFault(StepStartingCallbackName, failure);
        }
        else if (decision != StepGateDecision.Continue)
        {
            decision = StepGateDecision.Abort;
            if (!_stopEvent.WaitOne(0))
            {
                RequestStop(StopReason.HostFault);
            }
        }

        var invocation = new GateInvocation(
            Index: index,
            Decision: decision,
            StopRequestCountAtReturn: stopRequestCount,
            ThreadId: Environment.CurrentManagedThreadId,
            Exception: failure);
        lock (_gate)
        {
            _gateInvocations.Add(invocation);
        }

        return decision;
    }

    /// <summary>
    /// Runs <see cref="InvokeStepGate"/> on a new dedicated background thread, as the engine's coordinator
    /// would, so a gate that blocks on its journal acknowledgment or the stop event never holds the test
    /// thread or the thread pool.
    /// </summary>
    /// <param name="index">The zero-based index of the step about to start.</param>
    /// <returns>
    /// A task that completes, with continuations run asynchronously, with the decision, or faults with an
    /// exception that escaped the gate emulation.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a step of the plan.</exception>
    public Task<StepGateDecision> InvokeStepGateOnBackgroundThread(int index)
    {
        _ = StepAt(index);
        var completion = new TaskCompletionSource<StepGateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new Thread(() =>
        {
            try
            {
                completion.SetResult(InvokeStepGate(index));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = CoordinatorThreadName,
        };

        coordinator.Start();
        return completion.Task;
    }

    /// <summary>
    /// Starts the step at <paramref name="index"/> as the engine does: calls the gate, then raises
    /// <see cref="StepStarted"/> only when it continued. An aborted step starts no traffic and raises nothing.
    /// </summary>
    /// <param name="index">The zero-based index of the step.</param>
    /// <returns>The gate's decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a step of the plan.</exception>
    public StepGateDecision RunStep(int index)
    {
        StepGateDecision decision = InvokeStepGate(index);
        if (decision == StepGateDecision.Continue)
        {
            RaiseStepStarted(index);
        }

        return decision;
    }

    /// <summary>
    /// Reports a managed exception caught in a callback or an event subscriber as the wrapper does: records
    /// it in <see cref="CallbackFaults"/>, hands it to the options' fault sink, discarding anything the sink
    /// throws because no exception may unwind into the engine, and then requests a host-fault stop.
    /// </summary>
    /// <param name="callbackName">The callback or event, such as <c>on_progress</c> or <c>Completed handler</c>.</param>
    /// <param name="exception">The exception that was caught.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void InvokeCallbackFault(string callbackName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(callbackName);
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _callbackFaults.Add((callbackName, exception));
        }

        try
        {
            Options.CallbackFault?.Invoke(callbackName, exception);
        }
        catch (Exception)
        {
            // Deliberately discarded, as the wrapper's thunk discards it: the sink must not throw, and its
            // failure must not hide the original fault, which the stop below still reports.
        }

        RequestStop(StopReason.HostFault);
    }

    // The wrapper's EngineEventMapper.FormatEndDetail, which is internal to RamQuick.Interop.
    private static string? FormatEndDetail(RunEndReason reason, uint osError, string? nativeDetail)
    {
        return reason switch
        {
            RunEndReason.Completed => null,
            RunEndReason.AffinityFailed => WithOsError(osError, nativeDetail),
            RunEndReason.InsufficientMemory or RunEndReason.EngineFault when osError != 0 => WithOsError(osError, nativeDetail),
            _ => nativeDetail,
        };
    }

    private static string WithOsError(uint osError, string? detail)
    {
        return detail is null
            ? string.Create(CultureInfo.InvariantCulture, $"Windows error {osError}")
            : string.Create(CultureInfo.InvariantCulture, $"Windows error {osError}: {detail}");
    }

    private StepAttributes StepAt(int index)
    {
        if (index < 0 || index >= _steps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                string.Create(CultureInfo.InvariantCulture, $"The plan has {_steps.Count} steps, indexed from 0."));
        }

        return _steps[index];
    }

    private EngineStepStarted BuildStepStarted(int index) => new(Step: StepAt(index), Index: index, Count: _steps.Count);

    private void RecordEndReason(RunEndReason reason)
    {
        lock (_gate)
        {
            _firstRecordedEndReason ??= reason;
        }
    }

    // Posts one event to the context; the callback reads the event field when it runs, so subscribers
    // added after the post still receive it. Returns false when the event was dropped after disposal.
    private bool Post<TEvent>(TEvent record, Func<FakeEngineSession, EventHandler<TEvent>?> select, string handlerName)
        where TEvent : class
    {
        if (IsDisposed)
        {
            Interlocked.Increment(ref _droppedEventCount);
            return false;
        }

        Context.Post(_ => Deliver(select, record, handlerName), null);
        return true;
    }

    // Raises an event one subscriber at a time, as the wrapper does, so a subscriber that throws neither
    // skips the others nor unwinds into the context; its exception stops the run with a host fault.
    private void Deliver<TEvent>(Func<FakeEngineSession, EventHandler<TEvent>?> select, TEvent record, string handlerName)
    {
        if (IsDisposed)
        {
            Interlocked.Increment(ref _droppedEventCount);
            return;
        }

        EventHandler<TEvent>? handler = select(this);
        if (handler is null)
        {
            return;
        }

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                ((EventHandler<TEvent>)subscriber)(this, record);
            }
            catch (Exception exception)
            {
                InvokeCallbackFault(handlerName, exception);
            }
        }
    }
}

