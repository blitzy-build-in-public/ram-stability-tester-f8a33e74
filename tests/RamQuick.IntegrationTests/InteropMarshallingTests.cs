using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// The engine boundary as the wrapper sees it, over the test-only engine library this process is bound to: the
/// one-time binding, the managed mirrors of every ABI struct against the engine's own <c>sizeof</c>, the result-code
/// mapping, event delivery on the captured synchronization context, an injected affinity failure, and the stop
/// ordering that lets a woken step gate find its reason already recorded. It also covers the wrapper's own contracts
/// at that boundary: option validation and defaults, exception construction, the rejection of payloads that cannot
/// belong to the configured session, handle and callback-state ownership on every creation failure, disposal from
/// every thread a caller may use, the containment of exceptions thrown by step gates, fault sinks, event subscribers
/// and the captured context, and the monitor work-area query that shares the engine's import resolver.
/// </summary>
/// <remarks>
/// Every engine session sizes its region with <see cref="TestRegions.Bytes"/> for one worker per logical processor,
/// is disposed on every path, and leaves no native session alive. No test needs elevation.
/// </remarks>
public sealed class InteropMarshallingTests
{
    private const int TestTimeoutMs = 60_000;
    private const string Operation = "test";
    private const string UnknownResultCodeText = "Unknown result code";
    private const string UnknownCodeName = "Unknown";
    private const ulong Seed = 0x5EED_0F_1A7E_D0C5UL;

    // ERROR_INVALID_PARAMETER, the error the injected failing pin reports.
    private const uint AffinityWindowsError = 87;

    // Sessions whose factory never reaches the engine allocate nothing, so any size the validation accepts will do.
    private const ulong UnallocatedRegionBytes = 256UL << 20;

    private const string AbiMismatchPrefix = "Engine payload does not match the configured session: ";
    private const string FactoryFailureMessage = "Session factory failure injected by the test.";
    private const string MissingSessionHandleMessage = "rq_session_create reported success without a valid session handle.";
    private const string RejectedPostMessage = "Post rejected by the test's synchronization context.";

    private static readonly TimeSpan _stepBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _runTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gateWakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(25);

    // Long enough that only the stop event, never the timeout, can end a gate's wait within _disposeBound.
    private static readonly TimeSpan _gateHoldTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _disposeBound = TimeSpan.FromSeconds(10);

    // The native callback names a fault is reported under (CallbackThunks).
    private static readonly string[] _callbackNames =
    {
        "on_prepared", "on_step_starting", "on_step_finished", "on_progress", "on_errors", "on_completed", "on_released",
    };

    private readonly EngineBindingFixture _binding;
    private readonly ITestOutputHelper _output;

    /// <summary>Initializes the tests with the process binding and the test output.</summary>
    /// <param name="binding">The assembly fixture that bound this process to the test engine library.</param>
    /// <param name="output">The test output.</param>
    public InteropMarshallingTests(EngineBindingFixture binding, ITestOutputHelper output)
    {
        _binding = binding;
        _output = output;
    }

    [Fact]
    public void Bind_SecondCall_Throws()
    {
        Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
        Assert.Equal(EngineLibrary.BoundFileName, _binding.BoundFileName);

        // Neither rebinding to the same library nor back to the production one is accepted once bound.
        foreach (string fileName in new[] { "ramquick_engine_test.dll", "ramquick_engine.dll" })
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => EngineLibrary.Bind(fileName));
            _output.WriteLine($"Bind(\"{fileName}\") threw: {exception.Message}");
            Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
        }

        // The rejected calls left the binding working: the API-version check and a system probe still resolve.
        SystemSnapshot system = TestEngines.Create().QuerySystem();
        Assert.True(system.LogicalProcessors > 0, $"rq_query_system reported {system.LogicalProcessors} logical processors.");
        Assert.Equal("ramquick_engine_test.dll", EngineLibrary.BoundFileName);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(@"C:ramquick_engine_test.dll")]
    [InlineData(@"\ramquick_engine_test.dll")]
    public void Bind_BlankOrRootedButNotFullyQualifiedName_ThrowsArgumentExceptionAndKeepsBinding(string fileName)
    {
        // A drive-relative or root-relative name would depend on the current drive or directory, which the resolver never
        // consults, so it is refused as an argument error before the once-per-process check.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => EngineLibrary.Bind(fileName));
        _output.WriteLine($"Bind(\"{fileName}\") threw: {exception.Message}");
        Assert.Equal("fileName", exception.ParamName);
        Assert.Equal(EngineBindingFixture.TestEngineFileName, EngineLibrary.BoundFileName);
    }

    [Fact]
    public void StructSizes_ManagedMirrors_MatchNative()
    {
        RqStructSizes native = SessionProbe.GetStructSizes();
        Assert.Equal(56u, native.StructSize);
        Assert.Equal(0u, native.Reserved0);
        Assert.Equal(56, Unsafe.SizeOf<RqStructSizes>());

        var rows = new StructSizeRow[]
        {
            new("rq_system_info", nameof(RqSystemInfo), Unsafe.SizeOf<RqSystemInfo>(), RqSystemInfo.Size, native.SystemInfo, 112),
            new("rq_step_def", nameof(RqStepDef), Unsafe.SizeOf<RqStepDef>(), RqStepDef.Size, native.StepDef, 96),
            new("rq_session_config", nameof(RqSessionConfig), Unsafe.SizeOf<RqSessionConfig>(), RqSessionConfig.Size, native.SessionConfig, 64),
            new("rq_callbacks", nameof(RqCallbacks), Unsafe.SizeOf<RqCallbacks>(), RqCallbacks.Size, native.Callbacks, 72),
            new("rq_prepared", nameof(RqPrepared), Unsafe.SizeOf<RqPrepared>(), RqPrepared.Size, native.Prepared, 40),
            new("rq_step_start", nameof(RqStepStart), Unsafe.SizeOf<RqStepStart>(), RqStepStart.Size, native.StepStart, 80),
            new("rq_step_result", nameof(RqStepResult), Unsafe.SizeOf<RqStepResult>(), RqStepResult.Size, native.StepResult, 48),
            new("rq_progress", nameof(RqProgress), Unsafe.SizeOf<RqProgress>(), RqProgress.Size, native.Progress, 72),
            new("rq_error_record", nameof(RqErrorRecord), Unsafe.SizeOf<RqErrorRecord>(), RqErrorRecord.Size, native.ErrorRecord, 112),
            new("rq_run_result", nameof(RqRunResult), Unsafe.SizeOf<RqRunResult>(), RqRunResult.Size, native.RunResult, 192),
            new("rq_fault_spec", nameof(RqFaultSpec), Unsafe.SizeOf<RqFaultSpec>(), ExpectedSizes.FaultSpec, native.FaultSpec, 72),
            new("rq_injected_fault", nameof(RqInjectedFault), Unsafe.SizeOf<RqInjectedFault>(), ExpectedSizes.InjectedFault, native.InjectedFault, 40),
        };

        foreach (StructSizeRow row in rows)
        {
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.NativeName}: native {row.Native}, managed {row.Mirror} {row.Managed} (Size constant {row.SizeConstant}), ABI {row.Abi}"));
        }

        // Every row is checked, so one run reports every mismatched mirror rather than the first.
        Assert.Multiple(rows.Select(row => (Action)row.Check).ToArray());
    }

    [Theory]
    [InlineData(-1, typeof(ArgumentException))]
    [InlineData(-2, typeof(ObjectDisposedException))]
    [InlineData(-3, typeof(InvalidOperationException))]
    [InlineData(-4, typeof(EngineException))]
    [InlineData(-5, typeof(EngineException))]
    [InlineData(-6, typeof(EngineException))]
    [InlineData(-7, typeof(EngineException))]
    [InlineData(-8, typeof(EngineException))]
    [InlineData(-99, typeof(EngineException))]
    [InlineData(-12345, typeof(EngineException))]
    public void ThrowIfFailed_FailureCode_ThrowsMappedException(int code, Type expectedType)
    {
        var resultCode = (EngineResultCode)code;
        bool known = Enum.IsDefined(resultCode);

        // EngineResults.Describe returns rq_result_message's text, or the member name when the engine cannot be read.
        // The checks below rule that fallback out, so a match proves the text came from the engine.
        string nativeText = EngineResults.Describe(resultCode);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"rq_result_message({code}) = \"{nativeText}\""));
        Assert.False(string.IsNullOrEmpty(nativeText), $"rq_result_message({code}) returned no text.");
        if (known)
        {
            Assert.NotEqual(UnknownResultCodeText, nativeText);
            Assert.NotEqual(resultCode.ToString(), nativeText);
        }
        else
        {
            Assert.Equal(UnknownResultCodeText, nativeText);
        }

        Exception exception = Assert.ThrowsAny<Exception>(() => EngineResults.ThrowIfFailed(code, Operation));
        _output.WriteLine($"{exception.GetType().Name}: {exception.Message}");

        // Exact type, not assignability: ObjectDisposedException derives from InvalidOperationException.
        Assert.IsType(expectedType, exception);

        if (exception is EngineException engineException)
        {
            Assert.Equal(resultCode, engineException.Code);
            Assert.Equal(nativeText, engineException.NativeMessage);
            string name = known ? resultCode.ToString() : UnknownCodeName;
            Assert.Equal(
                string.Create(CultureInfo.InvariantCulture, $"{Operation} failed ({name}, {code}): {nativeText}"),
                engineException.Message);
        }
        else
        {
            Assert.StartsWith($"{Operation}: {nativeText}", exception.Message, StringComparison.Ordinal);
        }

        if (exception is ObjectDisposedException disposed)
        {
            Assert.Equal("EngineSession", disposed.ObjectName);
        }
    }

    [Fact]
    public void ThrowIfFailed_Ok_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => EngineResults.ThrowIfFailed((int)EngineResultCode.Ok, Operation)));

        // RQ_OK is not a failure, so no exception is ever created for it.
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineResults.CreateException((int)EngineResultCode.Ok, Operation));
    }

    [Fact]
    public void EngineException_Constructors_KeepCodeTextsAndInnerException()
    {
        var cause = new InvalidOperationException("The failure that caused the engine error.");

        // Without a result code the exception stands for RQ_E_INTERNAL and carries no engine text.
        var parameterless = new EngineException();
        var withMessage = new EngineException("Engine failed.");
        var withInner = new EngineException("Engine failed.", cause);
        Assert.All(new[] { parameterless, withMessage, withInner }, exception =>
        {
            Assert.Equal(EngineResultCode.Internal, exception.Code);
            Assert.Null(exception.NativeMessage);
        });
        Assert.False(string.IsNullOrEmpty(parameterless.Message), "The parameterless EngineException has no message.");
        Assert.Null(parameterless.InnerException);
        Assert.Equal("Engine failed.", withMessage.Message);
        Assert.Null(withMessage.InnerException);
        Assert.Equal("Engine failed.", withInner.Message);
        Assert.Same(cause, withInner.InnerException);

        // A code the enum does not define is kept as returned, so diagnostics still show the raw number.
        var withCode = new EngineException((EngineResultCode)(-12345), "rq_session_start failed (Unknown, -12345): text", "text", cause);
        Assert.Equal(-12345, (int)withCode.Code);
        Assert.Equal("rq_session_start failed (Unknown, -12345): text", withCode.Message);
        Assert.Equal("text", withCode.NativeMessage);
        Assert.Same(cause, withCode.InnerException);
    }

    [Fact]
    public void Options_OptionalMembersUnset_TakeDocumentedDefaultsAndNativeExtremesValidate()
    {
        var options = new EngineSessionOptions
        {
            RegionBytes = EngineSessionOptions.DefaultMinRegionBytes,
            WorkerCount = 1,
            Steps = OneStepAttributes(),
        };

        Assert.Equal(1UL << 30, EngineSessionOptions.DefaultMinRegionBytes);
        Assert.Equal(EngineSessionOptions.DefaultMinRegionBytes, options.MinRegionBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(250), EngineSessionOptions.DefaultProgressInterval);
        Assert.Equal(EngineSessionOptions.DefaultProgressInterval, options.ProgressInterval);
        Assert.Equal(10_000, options.ErrorDetailCap);
        Assert.Equal(0UL, options.Seed);
        Assert.False(options.StopOnError);
        Assert.Null(options.IsaOverride);
        Assert.Null(options.StepGate);
        Assert.Null(options.ProgressSink);
        Assert.Null(options.CallbackFault);
        options.Validate();

        // The extremes each native field can carry are accepted: validation refuses only what the engine cannot take.
        StepAttributes step = options.Steps[0];
        var extremes = new[]
        {
            new EngineSessionOptions
            {
                RegionBytes = 1,
                MinRegionBytes = 1,
                WorkerCount = 1,
                ErrorDetailCap = 1,
                ProgressInterval = TimeSpan.FromMilliseconds(1),
                IsaOverride = InstructionSet.Sse2,
                Steps = new[] { step with { Name = "n", TimeBudget = TimeSpan.FromMilliseconds(1), CoverageBudgetPercent = 1 } },
            },
            new EngineSessionOptions
            {
                RegionBytes = ulong.MaxValue,
                MinRegionBytes = ulong.MaxValue,
                WorkerCount = int.MaxValue,
                ErrorDetailCap = int.MaxValue,
                ProgressInterval = TimeSpan.FromMilliseconds(uint.MaxValue),
                IsaOverride = InstructionSet.Avx512,
                Steps = new[]
                {
                    step with
                    {
                        Name = new string('n', NativeConstants.NameSize - 1),
                        Hold = TimeSpan.FromMilliseconds(1),
                        TimeBudget = TimeSpan.FromMilliseconds(uint.MaxValue),
                        CoverageBudgetPercent = (int)(uint.MaxValue / 100),
                    },
                },
            },
        };
        Assert.All(extremes, extreme => extreme.Validate());
    }

    [Theory]
    [InlineData("ProgressIntervalBelowOneMillisecond", typeof(ArgumentOutOfRangeException), "ProgressInterval", "must be between 1 ms and 4,294,967,295 ms")]
    [InlineData("ProgressIntervalBeyondNativeRange", typeof(ArgumentOutOfRangeException), "ProgressInterval", "must be between 1 ms and 4,294,967,295 ms")]
    [InlineData("IsaOverrideZeroInsteadOfNull", typeof(ArgumentOutOfRangeException), "IsaOverride", "IsaOverride must be null or a defined InstructionSet.")]
    [InlineData("StepsNull", typeof(ArgumentException), "Steps", "A session needs at least one step.")]
    [InlineData("StepsEmpty", typeof(ArgumentException), "Steps", "A session needs at least one step.")]
    [InlineData("StepNull", typeof(ArgumentException), "Steps", "Steps[0] must not be null.")]
    [InlineData("StepIndexNotItsPosition", typeof(ArgumentException), "Steps", "Steps[0] has Index 1; a step's Index must equal its position in Steps.")]
    [InlineData("StepNameEmpty", typeof(ArgumentException), "Steps", "Steps[0] has an empty name.")]
    [InlineData("StepNameWithNul", typeof(ArgumentException), "Steps", "Steps[0] has a name containing a NUL character")]
    [InlineData("StepNameBeyond63Utf8Bytes", typeof(ArgumentException), "Steps", "which is 64 UTF-8 bytes; the native name field holds at most 63.")]
    [InlineData("StepNameWithUnpairedSurrogate", typeof(ArgumentException), "Steps", "which contains an unpaired surrogate and is not valid UTF-16.")]
    [InlineData("StepPatternUndefined", typeof(ArgumentException), "Steps", "Steps[0] has the undefined PatternKind value 99.")]
    [InlineData("StepOrderUndefined", typeof(ArgumentException), "Steps", "Steps[0] has the undefined AccessOrder value 99.")]
    [InlineData("StepThreadsUndefined", typeof(ArgumentException), "Steps", "Steps[0] has the undefined ThreadMode value 99.")]
    [InlineData("StepTimeBudgetBelowOneMillisecond", typeof(ArgumentException), "Steps", "Steps[0] has the time budget")]
    [InlineData("StepHoldBelowOneMillisecond", typeof(ArgumentException), "Steps", "Steps[0] has the hold")]
    [InlineData("StepCoverageBudgetZero", typeof(ArgumentException), "Steps", "Steps[0] has the coverage budget 0 %")]
    [InlineData("StepCoverageBudgetBeyondNativeRange", typeof(ArgumentException), "Steps", "Steps[0] has the coverage budget 2147483647 %")]
    public void Validate_OptionTheEngineCannotTake_ThrowsNamingTheOption(
        string optionCase,
        Type expectedType,
        string expectedParamName,
        string expectedText)
    {
        EngineSessionOptions options = InvalidOptions(optionCase);

        Exception exception = Assert.ThrowsAny<Exception>(options.Validate);
        _output.WriteLine($"{optionCase}: {exception.GetType().Name}: {exception.Message}");

        // Exact type, not assignability: ArgumentOutOfRangeException derives from ArgumentException.
        Assert.IsType(expectedType, exception);
        Assert.Equal(expectedParamName, Assert.IsAssignableFrom<ArgumentException>(exception).ParamName);
        Assert.Contains(expectedText, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PreparedStructSize", "rq_prepared.struct_size is 39, but API version 1 defines 40 bytes.")]
    [InlineData("PreparedWorkerCountBeyondInt32", "rq_prepared.worker_count is 4294967295, beyond the supported range.")]
    [InlineData("PreparedAddressKindPhysical", "rq_prepared.address_kind is 2 (physical), which is reserved and never reported by API version 1.")]
    [InlineData("PreparedAddressKindUndefined", "rq_prepared.address_kind is 9, which is not a defined value.")]
    [InlineData("StepStartCountNotThePlans", "rq_step_start.count is 2, but the session has 1 steps.")]
    [InlineData("StepStartIndexBeyondPlan", "rq_step_start.index is 1, but the session has 1 steps.")]
    [InlineData("StepResultCompletedNotBoolean", "rq_step_result.completed is 2, but a boolean must be 0 or 1.")]
    [InlineData("ErrorBatchBeyondDetailCap", "the on_errors batch holds 2 records, beyond the session's error_detail_cap of 1.")]
    [InlineData("RunResultMoreStepResultsThanSteps", "rq_run_result.step_result_count is 2, but the session has 1 steps.")]
    [InlineData("RunResultStepsCompletedBeyondResults", "rq_run_result.steps_completed is 1, beyond step_result_count 0.")]
    [InlineData("RunResultNullStepResults", "rq_run_result.step_results is null with a nonzero step_result_count.")]
    public void EventMapper_PayloadOutsideConfiguredSession_ThrowsAbiMismatch(string payloadCase, string expectedDetail)
    {
        // The thunk reports this exception as a host fault, so the run ends ABORTED instead of continuing on misread data.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MapMalformedPayload(payloadCase));
        _output.WriteLine($"{payloadCase}: {exception.Message}");
        Assert.Equal(AbiMismatchPrefix + expectedDetail, exception.Message);
    }

    [Theory]
    [InlineData(1u, RunEndReason.Completed)]
    [InlineData(2u, RunEndReason.FirstError)]
    [InlineData(3u, RunEndReason.UserStop)]
    [InlineData(4u, RunEndReason.Thermal)]
    [InlineData(5u, RunEndReason.HostFault)]
    [InlineData(6u, RunEndReason.InsufficientMemory)]
    [InlineData(7u, RunEndReason.AffinityFailed)]
    [InlineData(8u, RunEndReason.EngineFault)]
    [InlineData(0u, RunEndReason.EngineFault)]
    [InlineData(99u, RunEndReason.EngineFault)]
    public void MapEndReason_NativeEndReason_MapsByTableWithUnknownCodesAsEngineFault(uint nativeEndReason, RunEndReason expected)
    {
        Assert.Equal(expected, EngineEventMapper.MapEndReason(nativeEndReason));
    }

    [Theory]
    [InlineData(RunEndReason.Completed, 0u, "", null)]
    [InlineData(RunEndReason.AffinityFailed, 87u, "SetThreadGroupAffinity failed: group 1, processor 12", "Windows error 87: SetThreadGroupAffinity failed: group 1, processor 12")]
    [InlineData(RunEndReason.AffinityFailed, 87u, "", "Windows error 87")]
    [InlineData(RunEndReason.InsufficientMemory, 1453u, "VirtualLock failed at the minimum region", "Windows error 1453: VirtualLock failed at the minimum region")]
    [InlineData(RunEndReason.InsufficientMemory, 0u, "VirtualLock failed at the minimum region", "VirtualLock failed at the minimum region")]
    [InlineData(RunEndReason.EngineFault, 8u, null, "Windows error 8")]
    [InlineData(RunEndReason.EngineFault, 0u, "bad allocation", "bad allocation")]
    [InlineData(RunEndReason.UserStop, 5u, "", null)]
    public void FormatEndDetail_ReasonOsErrorAndNativeDetail_ComposeTheStateMachineDetail(
        RunEndReason reason,
        uint osError,
        string? nativeDetail,
        string? expected)
    {
        Assert.Equal(expected, EngineEventMapper.FormatEndDetail(reason, osError, nativeDetail));
    }

    [Fact]
    public void EventRecords_CollectionReplacedThroughInit_IsCopiedAndRejectsNull()
    {
        IReadOnlyList<StepAttributes> steps = OneStepAttributes();
        var nativeOutcome = new RqStepResult { Index = 0, Completed = 1, Passes = 3, ActualMs = 2000, VerifiedBytes = 1UL << 30 };
        StepOutcome outcome = EngineEventMapper.ToOutcome(in nativeOutcome, steps);
        RqErrorRecord nativeError = SingleBitFlip();
        ErrorRecord error = EngineEventMapper.ToErrorRecord(in nativeError, steps);

        // A with-expression goes through the init accessor, which copies like the constructor does.
        var records = new List<ErrorRecord> { error };
        EngineErrorsDetected batch = new EngineErrorsDetected(Array.Empty<ErrorRecord>(), 1) with { Records = records };
        records.Clear();
        Assert.Same(error, Assert.Single(batch.Records));
        Assert.IsNotType<ErrorRecord[]>(batch.Records);

        var outcomes = new List<StepOutcome> { outcome };
        var withoutResults = new EngineCompleted(
            RunEndReason.Completed,
            0,
            outcome.VerifiedBytes,
            outcome.ActualDuration,
            null,
            1,
            Array.Empty<StepOutcome>(),
            0,
            null,
            null);
        EngineCompleted completed = withoutResults with { StepResults = outcomes };
        outcomes.Clear();
        Assert.Same(outcome, Assert.Single(completed.StepResults));
        Assert.IsNotType<StepOutcome[]>(completed.StepResults);

        // One instance reaches every subscriber, so a null list or a null element is refused at the boundary.
        ArgumentException nullRecord = Assert.Throws<ArgumentException>(() => batch with { Records = new ErrorRecord[] { error, null! } });
        Assert.Equal("Records", nullRecord.ParamName);
        Assert.StartsWith("The element at index 1 is null.", nullRecord.Message, StringComparison.Ordinal);
        Assert.Equal("Records", Assert.Throws<ArgumentNullException>(() => batch with { Records = null! }).ParamName);

        ArgumentException nullOutcome = Assert.Throws<ArgumentException>(() => completed with { StepResults = new StepOutcome[] { null! } });
        Assert.Equal("StepResults", nullOutcome.ParamName);
        Assert.StartsWith("The element at index 0 is null.", nullOutcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void FixedUtf8_ZeroCapacityOrEmbeddedNul_ReadsEmptyAndRefusesToWrite()
    {
        // A field of capacity 0 holds no text, and its pointer may be null.
        Assert.Equal(string.Empty, FixedUtf8.Read(null, 0));

        // An embedded NUL would end the engine's copy of the text early, so it is refused before any byte is written.
        byte[] field = new byte[NativeConstants.NameSize];
        ArgumentException exception = Assert.Throws<ArgumentException>(() => WriteFixedUtf8("Random\0data", field));
        Assert.Equal("value", exception.ParamName);
        Assert.All(field, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void SafeSessionHandle_InvalidCallbackHandleOrSession_IsRefusedAndCallbackStateReleasedOnce()
    {
        // Only an allocated GCHandle to a CallbackState can be handed to the engine as user_data.
        ArgumentException unallocated = Assert.Throws<ArgumentException>(() => new SafeSessionHandle(default));
        Assert.Equal("callbackHandle", unallocated.ParamName);
        GCHandle foreign = GCHandle.Alloc(new object());
        try
        {
            Assert.Equal("callbackHandle", Assert.Throws<ArgumentException>(() => new SafeSessionHandle(foreign)).ParamName);
        }
        finally
        {
            foreign.Free();
        }

        var stopEvent = new ManualResetEvent(initialState: false);
        var state = new CallbackState(new SynchronizationContext(), stopEvent);
        using var handle = new SafeSessionHandle(GCHandle.Alloc(state));
        Assert.NotEqual(nint.Zero, handle.CallbackUserData);
        Assert.Same(state, GCHandle.FromIntPtr(handle.CallbackUserData).Target);

        // A success without a usable session handle leaves the handle invalid, so the runtime never destroys a session.
        foreach (nint missing in new nint[] { 0, -1 })
        {
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => handle.Attach(missing));
            Assert.Equal(MissingSessionHandleMessage, refused.Message);
            Assert.True(handle.IsInvalid, $"Attach({missing}) left the handle valid.");
        }

        // The creation failure path releases the callback state once; a second release is a no-op.
        handle.ReleaseCallbackHandleWithoutSession();
        Assert.Throws<ObjectDisposedException>(() => stopEvent.WaitOne(0));
        handle.ReleaseCallbackHandleWithoutSession();

        // Once released, the state can neither be handed to the engine nor back a session.
        Assert.Throws<InvalidOperationException>(() => handle.CallbackUserData);
        InvalidOperationException releasedAttach = Assert.Throws<InvalidOperationException>(() => handle.Attach(0x1000));
        Assert.Equal("The callback state of this handle has already been released.", releasedAttach.Message);
        Assert.True(handle.IsInvalid, "Attach after the callback state was released left the handle valid.");

        // A closed handle never runs ReleaseHandle, so attaching to it would leak the session.
        handle.Dispose();
        Assert.Throws<ObjectDisposedException>(() => handle.Attach(0x1000));
    }

    [Theory]
    [InlineData("Throws", FactoryFailureMessage)]
    [InlineData("SucceedsWithoutHandle", MissingSessionHandleMessage)]
    [InlineData("SucceedsWithInvalidHandle", MissingSessionHandleMessage)]
    public unsafe void Create_FactoryFailsAfterCallbackStateAllocated_ReleasesCallbackStateAndThrows(
        string factoryCase,
        string expectedMessage)
    {
        int liveBefore = SessionProbe.LiveSessionCount();
        CallbackState? captured = null;
        NativeSessionFactory factory = (config, callbacks, session) =>
        {
            // The table EngineSession built: its user_data roots the session's callback state.
            captured = GCHandle.FromIntPtr((nint)callbacks->UserData).Target as CallbackState;
            switch (factoryCase)
            {
                case "Throws":
                    throw new InvalidOperationException(FactoryFailureMessage);
                case "SucceedsWithoutHandle":
                    *session = 0;
                    return (int)EngineResultCode.Ok;
                case "SucceedsWithInvalidHandle":
                    *session = -1;
                    return (int)EngineResultCode.Ok;
                default:
                    throw new ArgumentOutOfRangeException(nameof(factoryCase), factoryCase, "Unknown factory case.");
            }
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => EngineSession.Create(UnallocatedOptions(), factory));
        _output.WriteLine($"{factoryCase}: {exception.Message}");
        Assert.Equal(expectedMessage, exception.Message);

        // No native session exists, so the runtime will never run ReleaseHandle: creation released the state itself.
        CallbackState state = Assert.IsType<CallbackState>(captured);
        Assert.Throws<ObjectDisposedException>(() => state.StopEvent.WaitOne(0));
        Assert.Equal(liveBefore, SessionProbe.LiveSessionCount());
    }

    [Fact]
    public void CreateSession_WorkerCountAboveLogicalProcessors_ThrowsArgumentException()
    {
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(system, OneStepPlan(), workerCount: system.LogicalProcessors + 1);

        // The managed validation accepts any positive worker count; only rq_session_create knows the machine, so this
        // RQ_E_INVALID_ARGUMENT comes back across the real boundary.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => engine.CreateSession(options));
        _output.WriteLine(exception.Message);
        Assert.StartsWith(
            $"rq_session_create: {EngineResults.Describe(EngineResultCode.InvalidArgument)}",
            exception.Message,
            StringComparison.Ordinal);
        AssertNoLiveSessions();
    }

    [Fact]
    public void Session_CreatedNotStarted_KeepsItsStepCopyRefusesReattachAndDisposesCleanly()
    {
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        int liveBefore = SessionProbe.LiveSessionCount();
        var callerSteps = new List<StepAttributes>(OneStepAttributes());
        StepAttributes original = callerSteps[0];
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib256);
        var options = new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = system.LogicalProcessors,
            Seed = Seed,
            Steps = callerSteps,
        };

        EngineSession session = Assert.IsType<EngineSession>(engine.CreateSession(options));
        ManualResetEvent stopEvent = session.CallbackState.StopEvent;
        try
        {
            Assert.Equal(liveBefore + 1, SessionProbe.LiveSessionCount());

            // The session reports steps from the copy it configured the engine with, never from the caller's list.
            callerSteps[0] = original with { Name = "Renamed after creation" };
            callerSteps.Add(original with { Index = 1 });
            Assert.Equal(original, Assert.Single(session.Steps));
            Assert.Throws<NotSupportedException>(() => ((IList<StepAttributes>)session.Steps).Add(original));

            // The callback state routes to one session only, and the attached native session can be neither replaced
            // nor have its callback state released while it can still call back with user_data.
            InvalidOperationException reattach = Assert.Throws<InvalidOperationException>(() => session.CallbackState.AttachSession(session));
            Assert.Equal("A session is already attached to this callback state.", reattach.Message);
            InvalidOperationException replace = Assert.Throws<InvalidOperationException>(
                () => session.Handle.Attach(session.Handle.DangerousGetHandle()));
            Assert.Equal("A native session is already attached to this handle.", replace.Message);
            InvalidOperationException release = Assert.Throws<InvalidOperationException>(session.Handle.ReleaseCallbackHandleWithoutSession);
            _output.WriteLine($"ReleaseCallbackHandleWithoutSession on a live session threw: {release.Message}");
            Assert.False(stopEvent.WaitOne(0), "The stop event was set before any stop was requested.");
        }
        finally
        {
            session.Dispose();
        }

        // Disposing a session that never started stops and destroys it, and only then releases its callback state.
        Assert.Equal(liveBefore, SessionProbe.LiveSessionCount());
        Assert.True(session.Handle.IsClosed, "Dispose left the session handle open.");
        Assert.Throws<ObjectDisposedException>(() => stopEvent.WaitOne(0));
        session.Dispose();
        Assert.Equal(liveBefore, SessionProbe.LiveSessionCount());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Start_CalledTwiceThenAfterDispose_ThrowsInvalidOperationThenObjectDisposedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan()));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // A stop recorded before start makes the engine raise Completed and Released at once, with no preparation,
            // so the run locks no memory and the first Start still succeeds.
            session.RequestStop(StopReason.User);
            session.Start();

            // The session's own guard reports the engine's RQ_E_INVALID_STATE contract without a second native call.
            InvalidOperationException secondStart = Assert.Throws<InvalidOperationException>(session.Start);
            _output.WriteLine($"Second Start threw: {secondStart.Message}");
            Assert.Contains("already been started", secondStart.Message, StringComparison.Ordinal);

            Assert.Throws<ArgumentOutOfRangeException>(() => session.RequestStop((StopReason)99));

            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine($"Completed: {completed.EndReason}, detail '{completed.EndDetail}'");

            Assert.Equal(RunEndReason.UserStop, completed.EndReason);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Null(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
        }
        finally
        {
            session.Dispose();
        }

        ObjectDisposedException afterDispose = Assert.Throws<ObjectDisposedException>(session.Start);
        _output.WriteLine($"Start after Dispose threw: {afterDispose.Message}");

        // After disposal RequestStop does nothing and Dispose is idempotent.
        session.RequestStop(StopReason.User);
        session.Dispose();
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetResult_BeforeStartAndAfterCompleted_CopiesTheRunResultTheEventCarriedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan()));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // rq_session_get_result answers RQ_E_INVALID_STATE until the run has completed, which reads as no result.
            Assert.Null(SessionProbe.GetResult(session));

            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            EngineCompleted copied = Assert.IsType<EngineCompleted>(SessionProbe.GetResult(session));
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Completed {completed.EndReason}, {completed.StepsCompleted} step(s), {completed.VerifiedBytes} B; copied {copied.EndReason}, {copied.StepsCompleted} step(s), {copied.VerifiedBytes} B"));

            // The copy maps the same header and the session-owned step results, so it equals what Completed delivered.
            Assert.Equal(RunEndReason.Completed, copied.EndReason);
            Assert.Equal(completed.EndReason, copied.EndReason);
            Assert.Equal(completed.TotalErrors, copied.TotalErrors);
            Assert.Equal(completed.VerifiedBytes, copied.VerifiedBytes);
            Assert.Equal(completed.Elapsed, copied.Elapsed);
            Assert.Equal(completed.FirstErrorTime, copied.FirstErrorTime);
            Assert.Equal(completed.StepsCompleted, copied.StepsCompleted);
            Assert.Equal(completed.OsError, copied.OsError);
            Assert.Equal(completed.NativeEndDetail, copied.NativeEndDetail);
            Assert.Equal(completed.EndDetail, copied.EndDetail);
            Assert.Equal(completed.StepResults, copied.StepResults);
            Assert.True(Assert.Single(copied.StepResults).VerifiedBytes > 0, "The copied step result verified no bytes.");
        }
        finally
        {
            session.Dispose();
        }

        // The step results live only until rq_session_destroy, so a disposed session refuses the copy.
        Assert.Throws<ObjectDisposedException>(() => SessionProbe.GetResult(session));
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepGate_DisposesItsSessionThenAborts_DisposeIsRefusedAndRunEndsWithHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // Set once the session exists; the gate runs only after Start, so it always finds the session.
        var sessionBox = new StrongBox<IEngineSession?>();
        var disposeAttempt = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        StepGate gate = (_, _) =>
        {
            IEngineSession current = Volatile.Read(ref sessionBox.Value)
                ?? throw new InvalidOperationException("The step gate ran before the test published its session.");
            disposeAttempt.TrySetResult(Record.Exception(current.Dispose));

            // Abort with no stop requested: the session records host_fault itself before the engine sees the Abort.
            return StepGateDecision.Abort;
        };

        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate));
        Volatile.Write(ref sessionBox.Value, session);
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            Exception? disposeFailure = await disposeAttempt.Task.WaitAsync(_runTimeout, cancellation);
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine($"Dispose inside the gate: {disposeFailure?.GetType().Name}: {disposeFailure?.Message}; Completed {completed.EndReason}");

            // rq_session_destroy would have to join the coordinator that is calling the gate, so Dispose is refused and
            // the session stays usable: it completes, releases and is disposed normally below.
            InvalidOperationException refused = Assert.IsType<InvalidOperationException>(disposeFailure);
            Assert.Contains("cannot be disposed from inside an engine callback", refused.Message, StringComparison.Ordinal);
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.NotNull(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Empty(recorder.StepFinished);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Equal(0UL, independentVerifiedBytes);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StepGate_Throws_FaultReportedOnceAndRunEndsWithHostFaultAsync(bool faultSinkThrows)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        var gateFailure = new InvalidOperationException("Step gate failure injected by the test.");
        var faults = new ConcurrentQueue<(string CallbackName, Exception Exception)>();
        CallbackFaultSink faultSink = (callbackName, exception) =>
        {
            faults.Enqueue((callbackName, exception));
            if (faultSinkThrows)
            {
                // The session discards what the sink throws; the host-fault stop must follow regardless.
                throw new InvalidOperationException("Fault sink failure injected by the test.");
            }
        };

        IEngineSession session = engine.CreateSession(
            CreateOptions(system, OneStepPlan(), gate: (_, _) => throw gateFailure, faultSink: faultSink));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine(string.Join("; ", faults.Select(fault => $"{fault.CallbackName}: {fault.Exception.Message}")));

            // The exception never reached native frames: it was reported once, the step started no traffic, and the run
            // ended with the host-fault stop the report requested.
            (string callbackName, Exception reported) = Assert.Single(faults);
            Assert.Equal("on_step_starting", callbackName);
            Assert.Same(gateFailure, reported);
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.Empty(recorder.StepStarted);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Equal(0UL, independentVerifiedBytes);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task EventSubscriber_Throws_LaterSubscribersStillReceiveAndRunEndsWithHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        var subscriberFailure = new InvalidOperationException("Prepared subscriber failure injected by the test.");
        var faults = new ConcurrentQueue<(string CallbackName, Exception Exception)>();
        EngineSessionOptions options = CreateOptions(
            system,
            OneStepPlan(),
            faultSink: (callbackName, exception) => faults.Enqueue((callbackName, exception)));

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            // Subscribed ahead of the recorder, so the recorder receiving Prepared proves the fault skipped no one.
            session.Prepared += (_, _) => throw subscriberFailure;
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            await ui.FlushAsync();
            _output.WriteLine($"Faults: {string.Join("; ", faults.Select(fault => fault.CallbackName))}; Completed {completed.EndReason}");

            Assert.NotNull(recorder.Prepared);
            (string callbackName, Exception reported) = Assert.Single(faults);
            Assert.Equal("Prepared handler", callbackName);
            Assert.Same(subscriberFailure, reported);

            // The lost event stops the run, so it ends ABORTED rather than continuing as if every subscriber had seen it.
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
        }
        finally
        {
            session.Dispose();
        }

        // The exception was contained inside the posted delivery and never reached the dispatcher.
        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Dispose_FromCompletedSubscriber_SkipsLaterSubscribersAndReleasedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(CreateOptions(system, OneStepPlan())));
        var disposedBySubscriber = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // An owner may dispose from an event handler on its context; only the engine's coordinator may not.
            session.Completed += (_, _) =>
            {
                session.Dispose();
                disposedBySubscriber.TrySetResult();
            };
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // A stop recorded before start ends the run at once, with no preparation and no locked memory.
            session.RequestStop(StopReason.User);
            session.Start();
            await disposedBySubscriber.Task.WaitAsync(_runTimeout, cancellation);

            // Dispose returned only after rq_session_destroy, so every event the engine raised has been posted by now.
            await ui.FlushAsync();
            AssertNoLiveSessions();

            // Once Dispose has begun no subscriber runs: neither the later Completed subscriber nor any Released one.
            Assert.Null(recorder.Completed);
            Assert.Null(recorder.Released);
            Assert.Empty(recorder.Events);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Dispose_WhileStepGateWaitsOnStopEvent_WakesGateAndDestroysWithoutRaisingCompletedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateWoken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StepGate gate = (_, stopEvent) =>
        {
            // Stands in for a gate waiting on its journal acknowledgment: only the stop event can end the wait in time.
            gateEntered.TrySetResult();
            gateWoken.TrySetResult(stopEvent.WaitOne(_gateHoldTimeout));
            return StepGateDecision.Abort;
        };

        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate));
        EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
        long disposeStarted;
        try
        {
            session.Start();
            await gateEntered.Task.WaitAsync(_runTimeout, cancellation);
            disposeStarted = Stopwatch.GetTimestamp();
        }
        finally
        {
            // The run is in progress, so Dispose first requests a host-fault stop, which sets the stop event.
            session.Dispose();
        }

        TimeSpan disposeTime = Stopwatch.GetElapsedTime(disposeStarted);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Dispose returned after {disposeTime.TotalSeconds:0.000} s"));

        Assert.True(gateWoken.Task.IsCompletedSuccessfully, "Dispose returned while the step gate was still running.");
        Assert.True(await gateWoken.Task, $"The step gate's wait timed out after {_gateHoldTimeout.TotalSeconds:0} s instead of being woken.");
        Assert.True(disposeTime < _disposeBound, $"Dispose took {disposeTime.TotalSeconds:0.000} s, not under {_disposeBound.TotalSeconds:0} s.");
        AssertNoLiveSessions();

        // The session was marked disposed before the stop, and rq_session_destroy suppresses every callback raised after
        // it begins: whichever happens first, neither Completed nor Released reaches a subscriber.
        Assert.Null(recorder.Completed);
        Assert.Null(recorder.Released);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Callbacks_CapturedContextRejectsPosts_EachFaultReportedAndRunEndsWithHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        var faults = new ConcurrentQueue<(string CallbackName, Exception Exception)>();
        EngineSessionOptions options = CreateOptions(
            system,
            OneStepPlan(),
            faultSink: (callbackName, exception) => faults.Enqueue((callbackName, exception)));

        // The session captures the context current at creation, so every event it raises is posted to this one.
        IEngineSession session;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
        try
        {
            session = engine.CreateSession(options);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        try
        {
            session.Start();

            // No event can reach a subscriber, so the run's end is read through rq_session_get_result.
            EngineCompleted? result = null;
            await WaitUntilAsync(() => (result = SessionProbe.GetResult(session)) is not null, "the run has completed", cancellation);
            await WaitUntilAsync(() => faults.Any(fault => fault.CallbackName == "on_released"), "on_released has been reported", cancellation);
            EngineCompleted completed = Assert.IsType<EngineCompleted>(result);
            string[] reported = faults.Select(fault => fault.CallbackName).ToArray();
            _output.WriteLine($"Faults: {string.Join(", ", reported)}; Completed {completed.EndReason}");

            // A callback whose event cannot be posted throws inside its thunk, which reports the fault under the callback's
            // name and requests a host-fault stop: the first lost event ends the run ABORTED.
            Assert.Equal("on_prepared", reported[0]);
            Assert.Contains("on_completed", reported);
            Assert.Equal("on_released", reported[^1]);
            Assert.All(faults, fault =>
            {
                Assert.Contains(fault.CallbackName, _callbackNames);
                Assert.Equal(RejectedPostMessage, Assert.IsType<InvalidOperationException>(fault.Exception).Message);
            });
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.Equal(0, completed.StepsCompleted);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Callbacks_UserDataResolvesNoSession_EventsDroppedAndGateAbortsWithHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = CreateEngineWithoutUserData();
        SystemSnapshot system = engine.QuerySystem();
        int gateCalls = 0;
        IEngineSession session = engine.CreateSession(CreateOptions(
            system,
            OneStepPlan(),
            gate: (_, _) =>
            {
                Interlocked.Increment(ref gateCalls);
                return StepGateDecision.Continue;
            }));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted? result = null;
            await WaitUntilAsync(() => (result = SessionProbe.GetResult(session)) is not null, "the run has completed", cancellation);
            EngineCompleted completed = Assert.IsType<EngineCompleted>(result);
            _output.WriteLine($"Completed {completed.EndReason}, '{completed.EndDetail}'; gate calls {Volatile.Read(ref gateCalls)}");

            // A thunk that resolves no session treats it as dead: it drops every event, and its step gate returns Abort
            // without consulting the options' gate, which the engine records as host_fault.
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.Equal(0, Volatile.Read(ref gateCalls));
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Empty(recorder.Events);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Events_SessionCreatedOnDispatcher_ArriveOnCapturedContextAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = TwoStepPlan();

        var gateThreads = new ConcurrentQueue<int>();
        var sinkThreads = new ConcurrentQueue<int>();
        EngineSessionOptions options = CreateOptions(
            system,
            plan,
            gate: (_, _) =>
            {
                gateThreads.Enqueue(Environment.CurrentManagedThreadId);
                return StepGateDecision.Continue;
            },
            sink: _ => sinkThreads.Enqueue(Environment.CurrentManagedThreadId));

        // Created on the dispatcher thread, so the session captures the dispatcher's synchronization context.
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            EngineReleased released = await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            await ui.FlushAsync();

            IReadOnlyList<RecordedEngineEvent> events = recorder.Events;
            WriteEvents(events, ui.ManagedThreadId);

            // WPF installs a fresh DispatcherSynchronizationContext for each dispatched operation, so the context is
            // identified by its type and the dispatcher thread rather than by instance.
            Assert.All(events, recorded =>
            {
                Assert.True(
                    recorded.ThreadId == ui.ManagedThreadId,
                    $"{recorded.Kind} ran on managed thread {recorded.ThreadId}, not on the dispatcher thread {ui.ManagedThreadId}.");
                Assert.IsType<DispatcherSynchronizationContext>(recorded.Context);
            });

            // Only the step gate and the progress sink run synchronously on the engine's coordinator thread.
            int[] coordinatorThreads = gateThreads.Concat(sinkThreads).Distinct().ToArray();
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Gate calls {gateThreads.Count}, sink calls {sinkThreads.Count}, on threads [{string.Join(", ", coordinatorThreads)}]"));
            Assert.Equal(plan.Steps.Count, gateThreads.Count);
            Assert.NotEmpty(sinkThreads);
            Assert.DoesNotContain(ui.ManagedThreadId, coordinatorThreads);
            Assert.Single(coordinatorThreads);

            EngineEventKind[] kinds = events.Select(recorded => recorded.Kind).ToArray();
            EngineEventKind[] lifecycle = kinds
                .Where(kind => kind is not (EngineEventKind.ProgressChanged or EngineEventKind.ErrorsDetected))
                .ToArray();
            Assert.Equal(
                new[]
                {
                    EngineEventKind.Prepared,
                    EngineEventKind.StepStarted,
                    EngineEventKind.StepFinished,
                    EngineEventKind.StepStarted,
                    EngineEventKind.StepFinished,
                    EngineEventKind.Completed,
                    EngineEventKind.Released,
                },
                lifecycle);

            int preparedAt = Array.IndexOf(kinds, EngineEventKind.Prepared);
            int completedAt = Array.IndexOf(kinds, EngineEventKind.Completed);
            int[] progressAt = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] == EngineEventKind.ProgressChanged).ToArray();
            Assert.NotEmpty(progressAt);
            Assert.All(progressAt, index => Assert.InRange(index, preparedAt + 1, completedAt - 1));

            EnginePrepared prepared = Assert.IsType<EnginePrepared>(recorder.Prepared);
            Assert.Equal(AddressKind.Virtual, prepared.AddressKind);
            Assert.Equal(options.RegionBytes, prepared.TestedBytes);
            Assert.Equal(system.LogicalProcessors, prepared.WorkerCount);

            IReadOnlyList<EngineStepStarted> started = recorder.StepStarted;
            Assert.Equal(new[] { 0, 1 }, started.Select(step => step.Index));
            Assert.All(started, step => Assert.Equal(plan.Steps.Count, step.Count));
            Assert.Equal(new[] { TestPlans.Quick.Steps[0].Name, TestPlans.Quick.Steps[1].Name }, started.Select(step => step.Name));

            IReadOnlyList<EngineStepFinished> finished = recorder.StepFinished;
            Assert.Equal(new[] { 0, 1 }, finished.Select(step => step.Outcome.Attributes.Index));
            Assert.All(finished, step =>
            {
                Assert.True(step.Outcome.Completed, $"Step {step.Outcome.Attributes.Index} did not complete.");
                Assert.True(step.Outcome.VerifiedBytes > 0, $"Step {step.Outcome.Attributes.Index} verified no bytes.");
                Assert.Equal(0UL, step.Outcome.Errors);
            });

            Assert.Equal(RunEndReason.Completed, completed.EndReason);
            Assert.Equal(0UL, completed.TotalErrors);
            Assert.Null(completed.FirstErrorTime);
            Assert.Equal(plan.Steps.Count, completed.StepsCompleted);
            Assert.Equal(0u, completed.OsError);
            Assert.Null(completed.EndDetail);
            Assert.Empty(recorder.Errors);
            Assert.True(released.ReleasedBytes > 0, $"Released reported {released.ReleasedBytes} bytes.");
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AffinityFailure_InjectedWindowsError87_SurfacesAsAffinityFailedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create(FaultSpec.AffinityFailure(worker: 0, windowsError: AffinityWindowsError));
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(system, TwoStepPlan());

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            WriteEvents(recorder.Events, ui.ManagedThreadId);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Completed: {completed.EndReason}, os_error {completed.OsError}, end_detail '{completed.NativeEndDetail}', EndDetail '{completed.EndDetail}'"));

            Assert.Equal(RunEndReason.AffinityFailed, completed.EndReason);
            Assert.Equal(AffinityWindowsError, completed.OsError);
            Assert.False(string.IsNullOrEmpty(completed.NativeEndDetail), "The engine reported no end_detail for affinity_failed.");
            Assert.Contains("SetThreadGroupAffinity", completed.NativeEndDetail, StringComparison.Ordinal);
            Assert.StartsWith("Windows error 87: ", completed.EndDetail, StringComparison.Ordinal);
            Assert.Equal("Windows error 87: " + completed.NativeEndDetail, completed.EndDetail);

            // A preparation failure raises Completed and Released only: no worker ran unpinned, and no step started.
            Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
            Assert.Null(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Equal(0UL, independentVerifiedBytes);
            Assert.All(recorder.EventThreadIds, threadId => Assert.Equal(ui.ManagedThreadId, threadId));
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(StopReason.User, 3u, RunEndReason.UserStop)]
    [InlineData(StopReason.Thermal, 4u, RunEndReason.Thermal)]
    [InlineData(StopReason.HostFault, 5u, RunEndReason.HostFault)]
    public async Task RequestStop_WhileGateWaitsOnStopEvent_GateFindsReasonRecordedAsync(
        StopReason reason,
        uint expectedPendingReason,
        RunEndReason expectedEndReason)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(expectedPendingReason, SessionProbe.ExpectedPendingReason(reason));

        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // Set once the session exists; the gate runs only after Start, so it always finds the session.
        var sessionBox = new StrongBox<IEngineSession?>();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateObservation = new TaskCompletionSource<GateObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        StepGate gate = (step, stopEvent) =>
        {
            try
            {
                gateEntered.TrySetResult();
                bool woken = stopEvent.WaitOne(_gateWakeTimeout);

                // Read the moment the event wakes the gate: RequestStop sets the event only after rq_session_stop has
                // returned, so the reason must already be recorded.
                IEngineSession current = Volatile.Read(ref sessionBox.Value)
                    ?? throw new InvalidOperationException("The step gate ran before the test published its session.");
                uint pending = SessionProbe.GetPendingStopReason(current);
                gateObservation.TrySetResult(new GateObservation(woken, pending, step.Index));
            }
            catch (Exception exception)
            {
                gateObservation.TrySetException(exception);
                throw;
            }

            return StepGateDecision.Abort;
        };

        IEngineSession session = engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate));
        Volatile.Write(ref sessionBox.Value, session);
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            session.Start();
            await gateEntered.Task.WaitAsync(_runTimeout, cancellation);

            // The gate now blocks inside on_step_starting, before any traffic of step 0, and nothing has stopped the run.
            Assert.Equal(SessionProbe.NoPendingReason, SessionProbe.GetPendingStopReason(session));

            session.RequestStop(reason);
            GateObservation observed = await gateObservation.Task.WaitAsync(_runTimeout, cancellation);
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{reason}: gate woken {observed.Woken}, pending reason {observed.PendingReason} at step {observed.StepIndex}; "
                + $"Completed {completed.EndReason} ('{completed.EndDetail}'), steps completed {completed.StepsCompleted}, "
                + $"verified {completed.VerifiedBytes} B, independently counted {independentVerifiedBytes} B"));

            Assert.True(observed.Woken, $"The step gate was not woken by the stop event within {_gateWakeTimeout.TotalSeconds:0} s of {reason}.");
            Assert.Equal(expectedPendingReason, observed.PendingReason);
            Assert.Equal(0, observed.StepIndex);
            Assert.Equal(expectedEndReason, completed.EndReason);

            // The aborted step started no traffic.
            Assert.NotNull(recorder.Prepared);
            Assert.Empty(recorder.StepStarted);
            Assert.Empty(recorder.StepFinished);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            Assert.Equal(0UL, completed.VerifiedBytes);
            Assert.Equal(0UL, independentVerifiedBytes);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetWorkArea_ZeroLiveAndDestroyedWindow_ReportsTheMonitorOnlyForTheLiveWindowAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Assert.Null(Win32Display.GetWorkArea(0));

        // A hidden probe window, created and destroyed on a dispatcher thread. Its user32 imports resolve through the
        // assembly's engine resolver, which this process registered when it bound the engine and which must hand every
        // other library back to default probing.
        using var ui = new DispatcherThread();
        (nint hwnd, MonitorWorkArea? live) = await ui.InvokeAsync(() =>
        {
            using var probe = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("RamQuick work-area probe") { Width = 1, Height = 1, WindowStyle = 0 });
            return (probe.Handle, Win32Display.GetWorkArea(probe.Handle));
        }).WaitAsync(_runTimeout, cancellation);
        await ui.FlushAsync().WaitAsync(_runTimeout, cancellation);

        MonitorWorkArea area = Assert.NotNull(live);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Window 0x{hwnd:X}: monitor 0x{area.Monitor:X}, work area {area.Width} x {area.Height} at ({area.Left}, {area.Top})"));
        Assert.NotEqual(nint.Zero, hwnd);
        Assert.NotEqual(nint.Zero, area.Monitor);
        Assert.True(area.Width > 0 && area.Height > 0, $"The work area is {area.Width} x {area.Height} pixels.");

        // A destroyed window maps to no monitor, which reads as "keep the current size" rather than an exception.
        Assert.Null(Win32Display.GetWorkArea(hwnd));
    }

    private static TestPlan OneStepPlan() => ScaledPlans.Custom((0, _stepBudget));

    private static TestPlan TwoStepPlan() => ScaledPlans.Custom((0, _stepBudget), (1, _stepBudget));

    private static IReadOnlyList<StepAttributes> OneStepAttributes() => ScaledPlans.Attributes(OneStepPlan());

    private static EngineSessionOptions CreateOptions(
        SystemSnapshot system,
        TestPlan plan,
        int? workerCount = null,
        StepGate? gate = null,
        ProgressSink? sink = null,
        CallbackFaultSink? faultSink = null)
    {
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib256);
        return new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = workerCount ?? system.LogicalProcessors,
            Seed = Seed,
            Steps = ScaledPlans.Attributes(plan),
            StepGate = gate,
            ProgressSink = sink,
            CallbackFault = faultSink,
        };
    }

    // Options for sessions whose factory never reaches the engine; optionally with one option the engine cannot take.
    private static EngineSessionOptions UnallocatedOptions(
        IReadOnlyList<StepAttributes>? steps = null,
        TimeSpan? progressInterval = null,
        InstructionSet? isaOverride = null)
    {
        return new EngineSessionOptions
        {
            RegionBytes = UnallocatedRegionBytes,
            MinRegionBytes = UnallocatedRegionBytes,
            WorkerCount = 1,
            ProgressInterval = progressInterval ?? EngineSessionOptions.DefaultProgressInterval,
            IsaOverride = isaOverride,
            Steps = steps ?? OneStepAttributes(),
        };
    }

    private static EngineSessionOptions InvalidOptions(string optionCase)
    {
        StepAttributes step = OneStepAttributes()[0];
        TimeSpan belowOneMillisecond = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1);
        return optionCase switch
        {
            "ProgressIntervalBelowOneMillisecond" => UnallocatedOptions(progressInterval: belowOneMillisecond),
            "ProgressIntervalBeyondNativeRange" => UnallocatedOptions(progressInterval: TimeSpan.FromMilliseconds(uint.MaxValue + 1.0)),

            // RQ_ISA_AUTO is expressed as null, never as the native 0.
            "IsaOverrideZeroInsteadOfNull" => UnallocatedOptions(isaOverride: (InstructionSet)0),

            // Steps is non-nullable, but a caller without nullable analysis can still assign null.
            "StepsNull" => new EngineSessionOptions
            {
                RegionBytes = UnallocatedRegionBytes,
                MinRegionBytes = UnallocatedRegionBytes,
                WorkerCount = 1,
                Steps = null!,
            },
            "StepsEmpty" => UnallocatedOptions(Array.Empty<StepAttributes>()),
            "StepNull" => UnallocatedOptions(new StepAttributes[] { null! }),
            "StepIndexNotItsPosition" => UnallocatedOptions(new[] { step with { Index = 1 } }),
            "StepNameEmpty" => UnallocatedOptions(new[] { step with { Name = string.Empty } }),
            "StepNameWithNul" => UnallocatedOptions(new[] { step with { Name = "Random\0data" } }),

            // 32 two-byte characters: 64 UTF-8 bytes, one more than the 64-byte field holds before its NUL.
            "StepNameBeyond63Utf8Bytes" => UnallocatedOptions(new[] { step with { Name = new string('\u00E9', 32) } }),
            "StepNameWithUnpairedSurrogate" => UnallocatedOptions(new[] { step with { Name = "Random data \uD800" } }),
            "StepPatternUndefined" => UnallocatedOptions(new[] { step with { Pattern = (PatternKind)99 } }),
            "StepOrderUndefined" => UnallocatedOptions(new[] { step with { Order = (AccessOrder)99 } }),
            "StepThreadsUndefined" => UnallocatedOptions(new[] { step with { Threads = (ThreadMode)99 } }),
            "StepTimeBudgetBelowOneMillisecond" => UnallocatedOptions(new[] { step with { TimeBudget = belowOneMillisecond } }),

            // A positive hold below 1 ms would reach the engine as 0, a windowed step, while the analyzer sees a hold.
            "StepHoldBelowOneMillisecond" => UnallocatedOptions(new[] { step with { Hold = TimeSpan.FromTicks(1) } }),
            "StepCoverageBudgetZero" => UnallocatedOptions(new[] { step with { CoverageBudgetPercent = 0 } }),

            // Percent × 100 must fit rq_step_def.coverage_budget_bp, a uint32_t.
            "StepCoverageBudgetBeyondNativeRange" => UnallocatedOptions(new[] { step with { CoverageBudgetPercent = int.MaxValue } }),
            _ => throw new ArgumentOutOfRangeException(nameof(optionCase), optionCase, "Unknown option case."),
        };
    }

    // Maps one payload the engine of the configured one-step session could never send; every case throws.
    private static unsafe object MapMalformedPayload(string payloadCase)
    {
        IReadOnlyList<StepAttributes> steps = OneStepAttributes();
        var prepared = new RqPrepared
        {
            StructSize = RqPrepared.Size,
            WorkerCount = 1,
            PageMode = NativeConstants.PageModeStandard,
            IsaSelected = NativeConstants.IsaAvx2,
            AddressKind = NativeConstants.AddressKindVirtual,
        };
        var stepStart = new RqStepStart { StructSize = RqStepStart.Size, Index = 0, Count = 1 };
        var stepResult = new RqStepResult { Index = 0, Completed = 1, Passes = 1 };
        var runResult = new RqRunResult
        {
            StructSize = RqRunResult.Size,
            EndReason = NativeConstants.EndCompleted,
            FirstErrorMs = NativeConstants.NoFirstError,
        };

        switch (payloadCase)
        {
            case "PreparedStructSize":
                prepared.StructSize = RqPrepared.Size - 1;
                return EngineEventMapper.ToPrepared(in prepared);
            case "PreparedWorkerCountBeyondInt32":
                prepared.WorkerCount = uint.MaxValue;
                return EngineEventMapper.ToPrepared(in prepared);
            case "PreparedAddressKindPhysical":
                prepared.AddressKind = NativeConstants.AddressKindPhysical;
                return EngineEventMapper.ToPrepared(in prepared);
            case "PreparedAddressKindUndefined":
                prepared.AddressKind = 9;
                return EngineEventMapper.ToPrepared(in prepared);
            case "StepStartCountNotThePlans":
                stepStart.Count = 2;
                return EngineEventMapper.ToStepStarted(in stepStart, steps);
            case "StepStartIndexBeyondPlan":
                stepStart.Index = 1;
                return EngineEventMapper.ToStepStarted(in stepStart, steps);
            case "StepResultCompletedNotBoolean":
                stepResult.Completed = 2;
                return EngineEventMapper.ToOutcome(in stepResult, steps);
            case "ErrorBatchBeyondDetailCap":
                ReadOnlySpan<RqErrorRecord> batch = stackalloc RqErrorRecord[] { SingleBitFlip(), SingleBitFlip() };
                return EngineEventMapper.ToErrorRecords(batch, steps, detailCap: 1);
            case "RunResultMoreStepResultsThanSteps":
                RqStepResult* twoResults = stackalloc RqStepResult[] { stepResult, stepResult };
                runResult.StepResultCount = 2;
                runResult.StepResults = twoResults;
                return EngineEventMapper.ToCompleted(in runResult, steps);
            case "RunResultStepsCompletedBeyondResults":
                runResult.StepsCompleted = 1;
                return EngineEventMapper.ToCompleted(in runResult, steps);
            case "RunResultNullStepResults":
                runResult.StepResultCount = 1;
                return EngineEventMapper.ToCompleted(in runResult, steps);
            default:
                throw new ArgumentOutOfRangeException(nameof(payloadCase), payloadCase, "Unknown payload case.");
        }
    }

    // A confirmed single-bit flip of bit 17 in step 0, as the engine reports one.
    private static RqErrorRecord SingleBitFlip() => new()
    {
        Sequence = 1,
        Address = 0x7FF0_0000_1000,
        RegionOffset = 0x1000,
        Expected = 0,
        Actual = 1UL << 17,
        Reread = 1UL << 17,
        BitMask = 1UL << 17,
        TimeSinceWriteUs = 50,
        TimeIntoTestMs = 1500,
        StepIndex = 0,
        PassIndex = 0,
        AccessType = NativeConstants.AccessVerifyAfterWrite,
        RereadOutcome = NativeConstants.RereadRepeatsActual,
        AddressKind = NativeConstants.AddressKindVirtual,
        WorkerIndex = 0,
        LogicalProcessor = 0,
        ProcessorGroup = 0,
        ActiveWorkers = 1,
    };

    private static unsafe int WriteFixedUtf8(string text, byte[] field)
    {
        fixed (byte* buffer = field)
        {
            return FixedUtf8.Write(text, buffer, field.Length);
        }
    }

    // An engine whose sessions receive a null user_data: every thunk then resolves no session, as for a session that is
    // not attached or has been collected.
    private static unsafe NativeEngine CreateEngineWithoutUserData() => new((config, callbacks, session) =>
    {
        // A local of the lambda, so its address is fixed; the engine copies the table before returning.
        RqCallbacks table = *callbacks;
        table.UserData = null;
        return TestEngineNative.SessionCreate(config, &table, null, session);
    });

    // Polls for a state no event signals, for sessions whose events never reach a subscriber.
    private static async Task WaitUntilAsync(Func<bool> condition, string description, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > _runTimeout)
            {
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Timed out after {_runTimeout.TotalSeconds:0} s waiting until {description}."));
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static void AssertNoLiveSessions()
    {
        int live = SessionProbe.LiveSessionCount();
        Assert.True(live == 0, $"{live} native engine session(s) are still alive after the test disposed its session.");
    }

    private void WriteEvents(IReadOnlyList<RecordedEngineEvent> events, int dispatcherThreadId)
    {
        int progressCount = events.Count(recorded => recorded.Kind == EngineEventKind.ProgressChanged);
        string lifecycle = string.Join(
            " -> ",
            events.Where(recorded => recorded.Kind != EngineEventKind.ProgressChanged).Select(recorded => recorded.Kind));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Events: {lifecycle}; ProgressChanged x{progressCount}; dispatcher thread {dispatcherThreadId}, "
            + $"event threads [{string.Join(", ", events.Select(recorded => recorded.ThreadId).Distinct())}]"));
    }

    // One ABI struct: the engine's sizeof, the managed mirror's size and Size constant, and the size the ABI fixes.
    private sealed record StructSizeRow(string NativeName, string Mirror, int Managed, int SizeConstant, uint Native, int Abi)
    {
        public void Check()
        {
            Assert.True(
                Managed == Native,
                string.Create(CultureInfo.InvariantCulture, $"{Mirror} is {Managed} bytes, but the engine's sizeof({NativeName}) is {Native}."));
            Assert.True(
                Managed == Abi,
                string.Create(CultureInfo.InvariantCulture, $"{Mirror} is {Managed} bytes, but the ABI fixes {NativeName} at {Abi}."));
            Assert.True(
                SizeConstant == Abi,
                string.Create(CultureInfo.InvariantCulture, $"The size constant for {Mirror} is {SizeConstant}, but the ABI fixes {NativeName} at {Abi}."));
        }
    }

    // What the step gate saw when the stop event woke it.
    private sealed record GateObservation(bool Woken, uint PendingReason, int StepIndex);

    // A captured context that cannot accept work, so every event a session posts to it throws inside its callback.
    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException(RejectedPostMessage);

        public override SynchronizationContext CreateCopy() => this;
    }
}

