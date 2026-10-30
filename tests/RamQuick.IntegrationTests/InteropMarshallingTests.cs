using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Logging;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// The engine boundary as the wrapper sees it, over the test-only engine library this process is bound to: the
/// one-time binding, the managed mirrors of every ABI struct against the engine's own <c>sizeof</c>, the result-code
/// mapping, the step limit and step shapes the wrapper checks before any native call, event delivery on the captured
/// synchronization context, one event at a time in engine order whatever the context, a Completed that survives a
/// rejected terminal payload or a refused post, an injected affinity failure, and the stop ordering that lets a woken
/// step gate find its reason already recorded. It also covers the wrapper's own contracts at that boundary: option
/// validation and defaults, exception construction, the rejection of payloads that cannot belong to the configured
/// session, handle and callback-state ownership on every creation failure, disposal from every thread a caller may
/// use, the containment of exceptions thrown by progress sinks, step gates, fault sinks, event subscribers and the
/// captured context as logged host faults, and the monitor work-area query that shares the engine's import resolver.
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

    // The app-errors.log source prefix RunController gives a fault caught in an engine callback or an event subscriber.
    private const string EngineCallbackSourcePrefix = "EngineCallback:";

    // The engine's default end_detail for a user stop, as a test-built terminal payload carries it.
    private const string UserStopNativeDetail = "Stop requested by host: user";

    // Header values of the terminal payloads RaiseTerminalPayload builds.
    private const ulong TerminalPayloadErrors = 3;
    private const ulong TerminalPayloadVerifiedBytes = 64UL << 20;
    private const ulong TerminalPayloadElapsedMs = 1_500;
    private const ulong TerminalPayloadFirstErrorMs = 250;

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

    // A hold and a budget that meet the plan validator's 2.5 × hold rule, so a shape case differs from a valid step in
    // its shape alone.
    private static readonly TimeSpan _shapeCaseHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _shapeCaseBudget = TimeSpan.FromSeconds(10);

    // How long the first lifecycle handlers of a delivery-order test block: long enough that an event delivered
    // independently of them would overtake them.
    private static readonly TimeSpan _slowHandlerDelay = TimeSpan.FromMilliseconds(300);

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
            new("rq_fault_spec", nameof(RqFaultSpec), Unsafe.SizeOf<RqFaultSpec>(), ExpectedSizes.FaultSpec, native.FaultSpec, 80),
            new("rq_injected_fault", nameof(RqInjectedFault), Unsafe.SizeOf<RqInjectedFault>(), ExpectedSizes.InjectedFault, native.InjectedFault, 56),
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
                    // A hold step must declare RandomBlocks, the order it writes its whole slice in.
                    step with
                    {
                        Name = new string('n', NativeConstants.NameSize - 1),
                        Order = AccessOrder.RandomBlocks,
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

    [Fact]
    public void CreateSession_MaxStepCountSteps_AcceptedByValidateAndTheEngine()
    {
        // The managed limit is the engine's k_max_step_count: a list exactly at it passes both checks, so the managed
        // guard rejects nothing the engine would run.
        Assert.Equal(1024, EngineSessionOptions.MaxStepCount);

        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(system, DistinctSteps(EngineSessionOptions.MaxStepCount));

        Assert.Null(Record.Exception(options.Validate));
        IEngineSession session = engine.CreateSession(options);
        session.Dispose();
        AssertNoLiveSessions();
    }

    [Fact]
    public void CreateSession_StepCountAboveMax_RejectedFromTheCountBeforeAnyStepIsRead()
    {
        var factory = new RecordingSessionFactory();
        NativeEngine engine = factory.CreateEngine();
        SystemSnapshot system = engine.QuerySystem();
        var steps = new UnreadableStepList(EngineSessionOptions.MaxStepCount + 1);
        EngineSessionOptions options = CreateOptions(system, steps);

        ArgumentException validateError = Assert.Throws<ArgumentException>(options.Validate);
        _output.WriteLine($"Validate threw: {validateError.Message}");
        Assert.Equal(nameof(EngineSessionOptions.Steps), validateError.ParamName);
        Assert.Contains("1024", validateError.Message, StringComparison.Ordinal);
        Assert.Contains("1025", validateError.Message, StringComparison.Ordinal);
        Assert.Equal(0, steps.Reads);

        // The session validates before it copies the list or marshals a step, so no element is read and the engine is
        // never called.
        ArgumentException createError = Assert.Throws<ArgumentException>(() => engine.CreateSession(options));
        _output.WriteLine($"CreateSession threw: {createError.Message}");
        Assert.Equal(nameof(EngineSessionOptions.Steps), createError.ParamName);
        Assert.Equal(0, steps.Reads);
        Assert.Equal(0, factory.Calls);
        AssertNoLiveSessions();
    }

    // Each case is a Standard step given a hold: on an order other than RandomBlocks, or on a moving-inversion pattern.
    // The first is the plan-data repro, Quick's high-toggle sweep with a 2 s hold.
    [Theory]
    [InlineData(0, PatternKind.HighToggle, AccessOrder.SequentialAscending, "must be RandomBlocks")]
    [InlineData(1, PatternKind.Random, AccessOrder.BankStride, "must be RandomBlocks")]
    [InlineData(2, PatternKind.Random, AccessOrder.Turnaround, "must be RandomBlocks")]
    [InlineData(3, PatternKind.MovingInversionWalking, AccessOrder.RandomBlocks, "takes no hold")]
    [InlineData(6, PatternKind.MovingInversionRandom, AccessOrder.SequentialAscending, "takes no hold")]
    public void CreateSession_HoldStepShapeTheEngineDoesNotRun_RejectedNamingTheStepBeforeAnyNativeCall(
        int standardStepIndex,
        PatternKind pattern,
        AccessOrder order,
        string expectedRule)
    {
        StepDefinition baseline = TestPlans.Standard.Steps[standardStepIndex];
        Assert.Equal(pattern, baseline.Pattern);
        StepAttributes step = baseline.ToAttributes(0) with
        {
            Order = order,
            Hold = _shapeCaseHold,
            TimeBudget = _shapeCaseBudget,
        };

        var factory = new RecordingSessionFactory();
        NativeEngine engine = factory.CreateEngine();
        EngineSessionOptions options = CreateOptions(engine.QuerySystem(), new[] { step });

        // Without the managed shape rules the engine's bare RQ_E_INVALID_ARGUMENT would be the only report.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => engine.CreateSession(options));
        _output.WriteLine(exception.Message);
        Assert.Equal(nameof(EngineSessionOptions.Steps), exception.ParamName);
        Assert.Contains("Steps[0]", exception.Message, StringComparison.Ordinal);
        Assert.Contains(baseline.Name, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedRule, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.Calls);
        AssertNoLiveSessions();
    }

    [Theory]
    [InlineData("MovingInversionOnTurnaroundWithoutHold")]
    [InlineData("HoldOnRandomBlocksWithHighTogglePattern")]
    [InlineData("RefreshHoldAsShipped")]
    public void CreateSession_StepShapeTheEngineRuns_AcceptedByValidateAndTheEngine(string caseName)
    {
        StepAttributes step = BuildSupportedShape(caseName);
        NativeEngine engine = TestEngines.Create();
        EngineSessionOptions options = CreateOptions(engine.QuerySystem(), new[] { step });

        // The real rq_session_create accepts the shape, so the managed rules reject nothing the engine runs.
        Assert.Null(Record.Exception(options.Validate));
        IEngineSession session = engine.CreateSession(options);
        session.Dispose();
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Start_CalledTwiceThenAfterDispose_ThrowsInvalidOperationThenObjectDisposedAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var ui = new DispatcherThread();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // Created on the dispatcher, the context the App creates its sessions on, so its events arrive on that thread;
        // Events_NoSynchronizationContext_RaisedOneAtATimeInEngineOrderAsync covers a session created without one.
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(CreateOptions(system, OneStepPlan())));
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
        Assert.Empty(ui.UnhandledExceptions);
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
        using var ui = new DispatcherThread();
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

        // Created on the dispatcher, the context the App creates its sessions on, so its events arrive on that thread;
        // Events_NoSynchronizationContext_RaisedOneAtATimeInEngineOrderAsync covers a session created without one.
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate)));
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

        Assert.Empty(ui.UnhandledExceptions);
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

        using var ui = new DispatcherThread();
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

        // Created on the dispatcher, the context the App creates its sessions on, so its events arrive on that thread;
        // Events_NoSynchronizationContext_RaisedOneAtATimeInEngineOrderAsync covers a session created without one.
        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(CreateOptions(system, OneStepPlan(), gate: gate)));
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

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallbackFault_ProgressSinkThrows_ContainedLoggedAndRunEndsHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        using var ui = new DispatcherThread();
        var faults = new CallbackFaultLog(dataRoot.Data);
        var thrown = new InvalidOperationException("Injected ProgressSink failure " + Guid.NewGuid().ToString("N"));
        int sinkCalls = 0;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // The sink throws on every snapshot; the engine raises the first on_progress at the first tick of step 0.
        EngineSessionOptions options = CreateOptions(
            system,
            OneStepPlan(),
            sink: _ =>
            {
                Interlocked.Increment(ref sinkCalls);
                throw thrown;
            },
            faultSink: faults.Record);

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            FaultedRun run = await RunUntilReleasedAsync(session, recorder, ui, cancellation);
            int calls = Volatile.Read(ref sinkCalls);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Progress sink calls {calls}, faults reported {faults.Faults.Count}"));

            // The on_progress thunk contained every throw on the coordinator and reported each one.
            AssertHostFaultContained(run, recorder, faults, ui, "on_progress", thrown, caughtOnDispatcher: false);
            Assert.Equal(calls, faults.Faults.Count);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallbackFault_EventSubscriberThrows_LaterSubscriberServedLoggedAndRunEndsHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        using var ui = new DispatcherThread();
        var faults = new CallbackFaultLog(dataRoot.Data);
        var thrown = new InvalidOperationException("Injected Prepared subscriber failure " + Guid.NewGuid().ToString("N"));
        int throwingHandlerCalls = 0;
        var gateWakes = new ConcurrentQueue<bool>();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();

        // The gate holds step 0 until the stop event wakes it, so the run cannot end before the subscriber's fault stops
        // it, however late the dispatcher delivers Prepared. Woken, it aborts the step under the reason already recorded;
        // a stop recorded before step 0 was reached skips the gate altogether.
        EngineSessionOptions options = CreateOptions(
            system,
            OneStepPlan(),
            gate: (_, stopEvent) =>
            {
                bool woken = stopEvent.WaitOne(_gateWakeTimeout);
                gateWakes.Enqueue(woken);
                return woken ? StepGateDecision.Abort : StepGateDecision.Continue;
            },
            faultSink: faults.Record);

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            // Subscribed before the recorder, so the recorder is a later subscriber to the same Prepared delivery.
            session.Prepared += (_, _) =>
            {
                Interlocked.Increment(ref throwingHandlerCalls);
                throw thrown;
            };
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            FaultedRun run = await RunUntilReleasedAsync(session, recorder, ui, cancellation);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Throwing Prepared handler calls {Volatile.Read(ref throwingHandlerCalls)}, gate wakes [{string.Join(", ", gateWakes)}]"));

            // Contained per subscriber on the dispatcher: the throwing handler ran once, and the recorder after it still
            // received Prepared. The lost event stops the run, so it ends ABORTED rather than continuing as if every
            // subscriber had seen it.
            Assert.Equal(1, Volatile.Read(ref throwingHandlerCalls));
            Assert.Single(faults.Faults);
            AssertHostFaultContained(run, recorder, faults, ui, "Prepared handler", thrown, caughtOnDispatcher: true);
            Assert.True(
                gateWakes.Count <= 1 && !gateWakes.Contains(false),
                $"The step gate was not woken by the subscriber's stop within {_gateWakeTimeout.TotalSeconds:0} s: [{string.Join(", ", gateWakes)}].");
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
    public async Task CallbackFault_StepGateThrows_StepStartsNoTrafficLoggedAndRunEndsHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        using var ui = new DispatcherThread();
        var faults = new CallbackFaultLog(dataRoot.Data);
        var thrown = new InvalidOperationException("Injected StepGate failure " + Guid.NewGuid().ToString("N"));
        var gateSteps = new ConcurrentQueue<int>();
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        EngineSessionOptions options = CreateOptions(
            system,
            TwoStepPlan(),
            gate: (step, _) =>
            {
                gateSteps.Enqueue(step.Index);
                throw thrown;
            },
            faultSink: faults.Record);

        IEngineSession session = await ui.InvokeAsync(() => engine.CreateSession(options));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            FaultedRun run = await RunUntilReleasedAsync(session, recorder, ui, cancellation);
            _output.WriteLine($"Step gate calls for steps [{string.Join(", ", gateSteps)}]");

            // The on_step_starting thunk contained the throw on the coordinator and answered Abort, which ended the run at
            // step 0 instead of going on to step 1.
            Assert.Equal(new[] { 0 }, gateSteps);
            Assert.Single(faults.Faults);
            AssertHostFaultContained(run, recorder, faults, ui, "on_step_starting", thrown, caughtOnDispatcher: false);

            // The aborted step started no traffic: nothing was reported, verified or independently counted.
            Assert.Empty(recorder.StepStarted);
            Assert.Empty(recorder.StepFinished);
            Assert.Equal(0, run.Completed.StepsCompleted);
            Assert.Empty(run.Completed.StepResults);
            Assert.Equal(0UL, run.Completed.VerifiedBytes);
            Assert.Equal(0UL, run.IndependentVerifiedBytes);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Empty(ui.UnhandledExceptions);
        AssertNoLiveSessions();
    }

    // Starts the session and follows its run through Released. The pending stop reason and the independently counted
    // verified bytes are read once Completed has arrived, while the session is still alive.
    private async Task<FaultedRun> RunUntilReleasedAsync(
        IEngineSession session,
        EngineRunRecorder recorder,
        DispatcherThread ui,
        CancellationToken cancellation)
    {
        session.Start();
        EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
        uint pendingReason = SessionProbe.GetPendingStopReason(session);
        ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(session);
        EngineReleased released = await recorder.WaitReleasedAsync(_runTimeout, cancellation);
        await ui.FlushAsync();

        WriteEvents(recorder.Events, ui.ManagedThreadId);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Completed: {completed.EndReason} ('{completed.EndDetail}'), pending reason {pendingReason}, steps completed "
            + $"{completed.StepsCompleted}, verified {completed.VerifiedBytes} B, independently counted {independentVerifiedBytes} B; "
            + $"released {released.ReleasedBytes} B in {released.ReleaseTime.TotalMilliseconds:0} ms"));
        return new FaultedRun(completed, released, pendingReason, independentVerifiedBytes);
    }

    // The callback-exception contract every faulted run meets: each throw reached the fault sink under its callback name,
    // on the thread that caught it, and was logged to app-errors.log; the host-fault stop it requested is the recorded
    // reason and the end reason; and the run still delivered Completed then Released on the captured context, releasing
    // the memory it had prepared.
    private void AssertHostFaultContained(
        FaultedRun run,
        EngineRunRecorder recorder,
        CallbackFaultLog faults,
        DispatcherThread ui,
        string callbackName,
        Exception thrown,
        bool caughtOnDispatcher)
    {
        IReadOnlyList<RecordedCallbackFault> reported = faults.Faults;
        string logPath = faults.Data.AppErrorsLogPath;
        string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        string sourceLine = $"APP_ERROR source=\"{EngineCallbackSourcePrefix}{callbackName}\"";
        int logged = log.Split('\n').Count(line => line.Contains(sourceLine, StringComparison.Ordinal));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Faults reported: {reported.Count} [{string.Join(", ", reported.Select(fault => $"{fault.CallbackName} on thread {fault.ThreadId}").Distinct())}], "
            + $"dispatcher thread {ui.ManagedThreadId}; app-errors.log blocks for {callbackName}: {logged}"));

        Assert.NotEmpty(reported);
        Assert.All(reported, fault =>
        {
            Assert.Equal(callbackName, fault.CallbackName);
            Assert.Same(thrown, fault.Exception);
            if (caughtOnDispatcher)
            {
                Assert.Equal(ui.ManagedThreadId, fault.ThreadId);
            }
            else
            {
                Assert.NotEqual(ui.ManagedThreadId, fault.ThreadId);
            }
        });

        // Callback faults are logged to <data>\logs\app-errors.log, here under the test's own data folder.
        Assert.Equal(Path.Combine(faults.Data.RootPath, "logs", "app-errors.log"), logPath, ignoreCase: true);
        Assert.True(File.Exists(logPath), $"No app-errors.log was written at {logPath}.");
        Assert.Equal(reported.Count, logged);
        Assert.Contains(thrown.Message, log, StringComparison.Ordinal);

        Assert.Equal(RunEndReason.HostFault, run.Completed.EndReason);
        Assert.Equal(SessionProbe.ExpectedPendingReason(StopReason.HostFault), run.PendingReason);

        IReadOnlyList<EngineEventKind> kinds = recorder.EventKinds;
        Assert.Single(kinds, kind => kind == EngineEventKind.Completed);
        Assert.Single(kinds, kind => kind == EngineEventKind.Released);
        Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, kinds.TakeLast(2));
        Assert.All(recorder.EventThreadIds, threadId => Assert.Equal(ui.ManagedThreadId, threadId));
        Assert.NotNull(recorder.Prepared);
        Assert.True(run.Released.ReleasedBytes > 0, $"Released reported {run.Released.ReleasedBytes} bytes after Prepared.");
    }

    // A run a callback fault stopped, followed through Released, with the native probes read before disposal.
    private sealed record FaultedRun(
        EngineCompleted Completed,
        EngineReleased Released,
        uint PendingReason,
        ulong IndependentVerifiedBytes);

    // One fault the session reported to its CallbackFault sink, with the managed thread it was reported on.
    private sealed record RecordedCallbackFault(string CallbackName, Exception Exception, int ThreadId);

    // The tests' CallbackFault sink. It writes app-errors.log as RunController's sink does, but synchronously, so the log
    // is complete once the run has ended. A callback's fault is reported on the coordinator and an event subscriber's on
    // the dispatcher, so recording is thread-safe.
    private sealed class CallbackFaultLog
    {
        private readonly ConcurrentQueue<RecordedCallbackFault> _faults = new();

        public CallbackFaultLog(DataDirectory data) => Data = data;

        public DataDirectory Data { get; }

        public IReadOnlyList<RecordedCallbackFault> Faults => _faults.ToArray();

        // Recorded before the write, so a logging failure cannot hide the fault from the test.
        public void Record(string callbackName, Exception exception)
        {
            _faults.Enqueue(new RecordedCallbackFault(callbackName, exception, Environment.CurrentManagedThreadId));
            ResultsLog.AppendAppError(Data, EngineCallbackSourcePrefix + callbackName, exception, DateTimeOffset.UtcNow);
        }
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Events_NoSynchronizationContext_RaisedOneAtATimeInEngineOrderAsync(bool stopBeforeStart)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        TestPlan plan = TwoStepPlan();
        var coordinatorThreads = new ConcurrentQueue<int>();
        EngineSessionOptions options = CreateOptions(
            system,
            plan,
            gate: (_, _) =>
            {
                coordinatorThreads.Enqueue(Environment.CurrentManagedThreadId);
                return StepGateDecision.Continue;
            },
            sink: _ => coordinatorThreads.Enqueue(Environment.CurrentManagedThreadId));

        // No context is current, as on a host thread without a dispatcher, so the session posts to the thread pool,
        // which on its own runs separate posts in parallel and in no fixed order.
        IEngineSession session = CreateSessionUnder(context: null, engine, options);
        try
        {
            // The probe brackets every delivery, and holds Prepared and Completed long enough for any later event
            // delivered independently to overtake them.
            var probe = new DeliveryProbe(_slowHandlerDelay, EngineEventKind.Prepared, EngineEventKind.Completed);
            probe.SubscribeFirst(session);
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            probe.SubscribeLast(session);

            if (stopBeforeStart)
            {
                session.RequestStop(StopReason.User);
            }

            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);

            IReadOnlyList<ProbedDelivery> deliveries = probe.Deliveries;
            EngineEventKind[] kinds = recorder.EventKinds.ToArray();
            int[] coordinator = coordinatorThreads.Distinct().ToArray();
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Events: {string.Join(" -> ", kinds.Where(kind => kind != EngineEventKind.ProgressChanged))}; "
                + $"ProgressChanged x{kinds.Count(kind => kind == EngineEventKind.ProgressChanged)}; "
                + $"delivery threads [{string.Join(", ", deliveries.Select(delivery => delivery.ThreadId).Distinct())}]; "
                + $"most concurrent deliveries {probe.MaxConcurrentDeliveries}; coordinator threads [{string.Join(", ", coordinator)}]"));

            // One delivery at a time, each seen whole by every subscriber in turn, none inside an engine callback.
            Assert.Equal(1, probe.MaxConcurrentDeliveries);
            Assert.Equal(kinds, deliveries.Select(delivery => delivery.Kind));
            Assert.All(deliveries, delivery =>
            {
                Assert.False(delivery.InEngineCallback, $"{delivery.Kind} was raised inside an engine callback.");
                Assert.DoesNotContain(delivery.ThreadId, coordinator);
            });

            if (stopBeforeStart)
            {
                Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, kinds);
                Assert.Equal(RunEndReason.UserStop, completed.EndReason);
                Assert.Empty(completed.StepResults);
            }
            else
            {
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
                Assert.All(
                    Enumerable.Range(0, kinds.Length).Where(i => kinds[i] == EngineEventKind.ProgressChanged),
                    index => Assert.InRange(index, preparedAt + 1, completedAt - 1));

                Assert.Equal(RunEndReason.Completed, completed.EndReason);
                Assert.Equal(plan.Steps.Count, completed.StepsCompleted);
                Assert.Equal(plan.Steps.Count, completed.StepResults.Count);

                // The step outcomes the mapper handed over are the event's own snapshot: read-only, and no array to
                // cast to.
                AssertReadOnlySnapshot(completed.StepResults);
            }
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Completed_ContextRefusesItsPost_StillRaisedBeforeReleasedAsHostFaultAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        var faults = new CallbackFaultLog(dataRoot.Data);
        var context = new RefusingSynchronizationContext(refusals: 1);
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = CreateSessionUnder(context, engine, CreateOptions(system, OneStepPlan(), faultSink: faults.Record));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // A stop before start makes the engine raise Completed and then Released, so Completed's post is the first
            // the context receives, and the one it refuses.
            session.RequestStop(StopReason.User);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            EngineCompleted engineResult = Assert.IsType<EngineCompleted>(SessionProbe.GetResult(session));
            uint pendingReason = SessionProbe.GetPendingStopReason(session);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Posts {context.Posts}; Completed {completed.EndReason} ('{completed.EndDetail}'); engine result {engineResult.EndReason} ('{engineResult.EndDetail}')"));

            RecordedCallbackFault fault = Assert.Single(faults.Faults);
            Assert.Equal("on_completed", fault.CallbackName);
            Assert.Same(context.Refusal, fault.Exception);
            Assert.Contains(context.Refusal.Message, File.ReadAllText(faults.Data.AppErrorsLogPath), StringComparison.Ordinal);

            // The refused Completed stayed queued, and Released's post drained it first: one Completed, then Released,
            // from one further post, never more posts than events.
            Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
            Assert.Equal(2, context.Posts);

            // The managed outcome is the host fault the callback-exception policy requires. The engine's own
            // first-writer reason is kept natively and named in the detail, with its diagnostics intact.
            Assert.Equal(RunEndReason.UserStop, engineResult.EndReason);
            Assert.Equal(SessionProbe.ExpectedPendingReason(StopReason.User), pendingReason);
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.Equal(engineResult.NativeEndDetail, completed.NativeEndDetail);
            Assert.Equal(engineResult.OsError, completed.OsError);
            Assert.Equal(engineResult.TotalErrors, completed.TotalErrors);
            Assert.Equal(engineResult.StepResults, completed.StepResults);
            Assert.Equal(
                $"{context.Refusal.Message} The engine ended the run with {RunEndReason.UserStop}: {engineResult.EndDetail}",
                completed.EndDetail);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true, (uint)RqRunResult.Size, 0u, "null payload pointer", false)]
    [InlineData(false, (uint)RqRunResult.Size, 1u, "step_results is null with a nonzero step_result_count", true)]
    [InlineData(false, 96u, 0u, "struct_size is 96", false)]
    public async Task Completed_TerminalPayloadRejected_RaisedOnceAsHostFaultFromTrustedHeaderAsync(
        bool nullResult,
        uint structSize,
        uint stepResultCount,
        string expectedFault,
        bool headerTrusted)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        var faults = new CallbackFaultLog(dataRoot.Data);
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = CreateSessionUnder(context: null, engine, CreateOptions(system, OneStepPlan(), faultSink: faults.Record));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);

            // Never started, so the engine itself never raises on_completed; the session's thunk receives only this
            // payload, which the engine would have frozen and raised once.
            Exception? escaped = Record.Exception(() => RaiseTerminalPayload(session, nullResult, structSize, stepResultCount));
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            uint pendingReason = SessionProbe.GetPendingStopReason(session);
            _output.WriteLine($"Completed {completed.EndReason} ('{completed.EndDetail}')");

            Assert.Null(escaped);
            RecordedCallbackFault fault = Assert.Single(faults.Faults);
            Assert.Equal("on_completed", fault.CallbackName);
            Assert.IsType<InvalidOperationException>(fault.Exception);
            Assert.Contains(expectedFault, fault.Exception.Message, StringComparison.Ordinal);
            Assert.Contains(fault.Exception.Message, File.ReadAllText(faults.Data.AppErrorsLogPath), StringComparison.Ordinal);
            Assert.Equal(SessionProbe.ExpectedPendingReason(StopReason.HostFault), pendingReason);

            Assert.Equal(new[] { EngineEventKind.Completed }, recorder.EventKinds);
            Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            Assert.Equal(0, completed.StepsCompleted);
            Assert.Empty(completed.StepResults);
            if (headerTrusted)
            {
                Assert.Equal(TerminalPayloadErrors, completed.TotalErrors);
                Assert.Equal(TerminalPayloadVerifiedBytes, completed.VerifiedBytes);
                Assert.Equal(TimeSpan.FromMilliseconds(TerminalPayloadElapsedMs), completed.Elapsed);
                Assert.Equal<TimeSpan?>(TimeSpan.FromMilliseconds(TerminalPayloadFirstErrorMs), completed.FirstErrorTime);
                Assert.Equal(UserStopNativeDetail, completed.NativeEndDetail);
                Assert.Equal(
                    $"{fault.Exception.Message} The engine ended the run with {RunEndReason.UserStop}: {UserStopNativeDetail}",
                    completed.EndDetail);
            }
            else
            {
                Assert.Equal(0UL, completed.TotalErrors);
                Assert.Equal(0UL, completed.VerifiedBytes);
                Assert.Equal(TimeSpan.Zero, completed.Elapsed);
                Assert.Null(completed.FirstErrorTime);
                Assert.Null(completed.NativeEndDetail);
                Assert.Equal($"{fault.Exception.Message} The engine's end reason could not be read.", completed.EndDetail);
            }

            Assert.Equal(0u, completed.OsError);
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(2u, true)]
    [InlineData(0u, false)]
    public async Task Errors_NullRecordsPointer_RejectedAsHostFaultUnlessCountIsZeroAsync(uint count, bool rejected)
    {
        const ulong TotalErrors = 7;
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var dataRoot = new TempDataRoot();
        var faults = new CallbackFaultLog(dataRoot.Data);
        NativeEngine engine = TestEngines.Create();
        SystemSnapshot system = engine.QuerySystem();
        IEngineSession session = CreateSessionUnder(context: null, engine, CreateOptions(system, OneStepPlan(), faultSink: faults.Record));
        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            Exception? escaped = Record.Exception(() => RaiseErrorsWithNullRecords(session, count, TotalErrors));
            uint pendingReason = SessionProbe.GetPendingStopReason(session);

            // Stopped before start, the run raises Completed and Released after anything the batch queued, so their
            // arrival shows every delivery of the batch has happened.
            session.RequestStop(StopReason.User);
            session.Start();
            EngineCompleted completed = await recorder.WaitCompletedAsync(_runTimeout, cancellation);
            await recorder.WaitReleasedAsync(_runTimeout, cancellation);
            _output.WriteLine($"Events: {string.Join(" -> ", recorder.EventKinds)}; Completed {completed.EndReason}; pending reason after the batch {pendingReason}");

            Assert.Null(escaped);
            if (rejected)
            {
                RecordedCallbackFault fault = Assert.Single(faults.Faults);
                Assert.Equal("on_errors", fault.CallbackName);
                Assert.IsType<InvalidOperationException>(fault.Exception);
                Assert.Contains("null records pointer", fault.Exception.Message, StringComparison.Ordinal);
                Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"count of {count}"), fault.Exception.Message, StringComparison.Ordinal);
                Assert.Equal(SessionProbe.ExpectedPendingReason(StopReason.HostFault), pendingReason);
                Assert.Empty(recorder.ErrorBatches);
                Assert.Equal(new[] { EngineEventKind.Completed, EngineEventKind.Released }, recorder.EventKinds);
                Assert.Equal(RunEndReason.HostFault, completed.EndReason);
            }
            else
            {
                Assert.Empty(faults.Faults);
                Assert.Equal(SessionProbe.NoPendingReason, pendingReason);
                EngineErrorsDetected batch = Assert.Single(recorder.ErrorBatches);
                Assert.Empty(batch.Records);
                Assert.Equal(TotalErrors, batch.TotalErrors);
                Assert.Equal(
                    new[] { EngineEventKind.ErrorsDetected, EngineEventKind.Completed, EngineEventKind.Released },
                    recorder.EventKinds);
                Assert.Equal(RunEndReason.UserStop, completed.EndReason);
            }
        }
        finally
        {
            session.Dispose();
        }

        AssertNoLiveSessions();
    }

    [Fact]
    public unsafe void EventLists_MappedFromPayload_BecomeTheEventSnapshotWhileCallerListsAreCopied()
    {
        IReadOnlyList<StepAttributes> steps = ScaledPlans.Attributes(TwoStepPlan());
        RqErrorRecord[] nativeRecords = { ConfirmedError(sequence: 0, stepIndex: 0), ConfirmedError(sequence: 1, stepIndex: 1) };

        // The mapper's own array becomes the batch's snapshot rather than being copied a second time.
        IReadOnlyList<ErrorRecord> mapped = EngineEventMapper.ToErrorRecords(nativeRecords, steps, detailCap: nativeRecords.Length);
        var batch = new EngineErrorsDetected(mapped, TotalErrors: 2);
        Assert.Same(mapped, batch.Records);
        AssertReadOnlySnapshot(batch.Records);

        // A list a caller still holds writable is copied, even when it arrives read-only, so a later write never
        // reaches the event.
        var callerList = new List<ErrorRecord>(mapped);
        var callerBatch = new EngineErrorsDetected(callerList.AsReadOnly(), TotalErrors: 2);
        callerList[0] = callerList[1];
        Assert.Same(mapped[0], callerBatch.Records[0]);
        AssertReadOnlySnapshot(callerBatch.Records);

        // The run result's step outcomes are the record's own snapshot as well: another record built from them shares
        // them instead of copying, because nothing can write them.
        var nativeSteps = new RqStepResult[]
        {
            new() { Index = 0, Completed = 1, Passes = 1, ActualMs = 10, VerifiedBytes = 1UL << 20 },
            new() { Index = 1, Completed = 1, Passes = 1, ActualMs = 10, VerifiedBytes = 1UL << 20 },
        };
        EngineCompleted completed;
        fixed (RqStepResult* stepResults = nativeSteps)
        {
            RqRunResult result = default;
            result.StructSize = RqRunResult.Size;
            result.EndReason = NativeConstants.EndCompleted;
            result.StepsCompleted = (uint)nativeSteps.Length;
            result.StepResultCount = (uint)nativeSteps.Length;
            result.FirstErrorMs = NativeConstants.NoFirstError;
            result.StepResults = stepResults;
            completed = EngineEventMapper.ToCompleted(in result, steps);
        }

        Assert.Equal(new[] { 0, 1 }, completed.StepResults.Select(outcome => outcome.Attributes.Index));
        AssertReadOnlySnapshot(completed.StepResults);
        Assert.Same(completed.StepResults, (completed with { StepResults = completed.StepResults }).StepResults);
        StepOutcome[] callerOutcomes = completed.StepResults.ToArray();
        Assert.NotSame(callerOutcomes, (completed with { StepResults = callerOutcomes }).StepResults);
    }

    // Creates the session with context current on this thread, which is what CreateSession captures, and restores the
    // thread's own context before the test awaits anything.
    private static IEngineSession CreateSessionUnder(SynchronizationContext? context, NativeEngine engine, EngineSessionOptions options)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return engine.CreateSession(options);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // Raises on_completed on the session's thunk with a user-stop payload whose step results the ABI forbids or whose
    // struct_size is foreign, or with a null pointer.
    private static unsafe void RaiseTerminalPayload(IEngineSession session, bool nullResult, uint structSize, uint stepResultCount)
    {
        if (nullResult)
        {
            SessionProbe.RaiseCompleted(session, null);
            return;
        }

        RqRunResult result = default;
        result.StructSize = structSize;
        result.EndReason = NativeConstants.EndUserStop;
        result.StepsCompleted = 0;
        result.StepResultCount = stepResultCount;
        result.TotalErrors = TerminalPayloadErrors;
        result.VerifiedBytes = TerminalPayloadVerifiedBytes;
        result.ElapsedMs = TerminalPayloadElapsedMs;
        result.FirstErrorMs = TerminalPayloadFirstErrorMs;
        result.StepResults = null;
        result.OsError = 0;
        _ = FixedUtf8.Write(UserStopNativeDetail, result.EndDetail, NativeConstants.EndDetailSize);
        SessionProbe.RaiseCompleted(session, &result);
    }

    private static unsafe void RaiseErrorsWithNullRecords(IEngineSession session, uint count, ulong totalErrors) =>
        SessionProbe.RaiseErrors(session, null, count, totalErrors);

    // A record the mapper accepts: a single flipped bit, verified after write, whose re-read matched the expected value.
    private static RqErrorRecord ConfirmedError(ulong sequence, uint stepIndex)
    {
        const ulong Expected = 0x5555_5555_5555_5555UL;
        const ulong Actual = Expected ^ 0x10UL;
        return new RqErrorRecord
        {
            Sequence = sequence,
            Address = 0x1000 + (sequence * 8),
            RegionOffset = sequence * 8,
            Expected = Expected,
            Actual = Actual,
            Reread = Expected,
            BitMask = Expected ^ Actual,
            StepIndex = stepIndex,
            AccessType = (uint)AccessType.VerifyAfterWrite,
            RereadOutcome = (uint)RereadOutcome.RereadMatchesExpected,
            AddressKind = NativeConstants.AddressKindVirtual,
            ActiveWorkers = 1,
        };
    }

    // An event collection must be read-only through every interface it implements and must not be an array a
    // subscriber could cast to and write.
    private static void AssertReadOnlySnapshot<T>(IReadOnlyList<T> list)
        where T : class
    {
        Assert.False(list is T[], $"The event list is a writable {typeof(T).Name}[].");
        IList<T> asList = Assert.IsAssignableFrom<IList<T>>(list);
        Assert.True(asList.IsReadOnly, "The event list reports itself writable.");
        Assert.Throws<NotSupportedException>(() => asList[0] = asList[asList.Count - 1]);
    }

    // One delivery as the first subscriber saw it: the event, its thread, and whether it ran inside an engine callback.
    private sealed record ProbedDelivery(EngineEventKind Kind, int ThreadId, bool InEngineCallback);

    // Brackets every delivery of a session, from its first subscriber to its last, to count overlapping deliveries and
    // record where each ran. Deliveries of the slow kinds are held at entry, so that any event delivered independently
    // of them would overtake them.
    private sealed class DeliveryProbe
    {
        private readonly TimeSpan _delay;
        private readonly EngineEventKind[] _slowKinds;
        private readonly ConcurrentQueue<ProbedDelivery> _deliveries = new();
        private int _active;
        private int _maxActive;

        public DeliveryProbe(TimeSpan delay, params EngineEventKind[] slowKinds)
        {
            _delay = delay;
            _slowKinds = slowKinds;
        }

        public int MaxConcurrentDeliveries => Volatile.Read(ref _maxActive);

        public IReadOnlyList<ProbedDelivery> Deliveries => _deliveries.ToArray();

        // Subscribed before every other handler of the session.
        public void SubscribeFirst(IEngineSession session) => SubscribeAll(session, Enter);

        // Subscribed after every other handler of the session.
        public void SubscribeLast(IEngineSession session) => SubscribeAll(session, _ => Interlocked.Decrement(ref _active));

        private static void SubscribeAll(IEngineSession session, Action<EngineEventKind> handler)
        {
            session.Prepared += (_, _) => handler(EngineEventKind.Prepared);
            session.StepStarted += (_, _) => handler(EngineEventKind.StepStarted);
            session.StepFinished += (_, _) => handler(EngineEventKind.StepFinished);
            session.ProgressChanged += (_, _) => handler(EngineEventKind.ProgressChanged);
            session.ErrorsDetected += (_, _) => handler(EngineEventKind.ErrorsDetected);
            session.Completed += (_, _) => handler(EngineEventKind.Completed);
            session.Released += (_, _) => handler(EngineEventKind.Released);
        }

        private void Enter(EngineEventKind kind)
        {
            int active = Interlocked.Increment(ref _active);
            int observed = Volatile.Read(ref _maxActive);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref _maxActive, active, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            _deliveries.Enqueue(new ProbedDelivery(kind, Environment.CurrentManagedThreadId, CallbackThunks.IsInCallback));
            if (Array.IndexOf(_slowKinds, kind) >= 0)
            {
                Thread.Sleep(_delay);
            }
        }
    }

    // A context that refuses its first posts, as a context that has shut down does, and runs every later post on the
    // thread pool. It counts the posts it receives.
    private sealed class RefusingSynchronizationContext : SynchronizationContext
    {
        private int _refusalsLeft;
        private int _posts;

        public RefusingSynchronizationContext(int refusals)
        {
            _refusalsLeft = refusals;
            Refusal = new InvalidOperationException("Injected refusal of SynchronizationContext.Post " + Guid.NewGuid().ToString("N"));
        }

        // The exception every refused post throws.
        public InvalidOperationException Refusal { get; }

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Decrement(ref _refusalsLeft) >= 0)
            {
                throw Refusal;
            }

            base.Post(d, state);
        }

        public override SynchronizationContext CreateCopy() => this;
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

    // Moving inversion ignores the order it declares and has no hold (the Quick step 4 control run with Turnaround); a
    // hold on RandomBlocks with a pattern other than moving inversion; and Quick's refresh hold as shipped.
    private static StepAttributes BuildSupportedShape(string caseName) => caseName switch
    {
        "MovingInversionOnTurnaroundWithoutHold" => TestPlans.Quick.Steps[3].ToAttributes(0) with { Order = AccessOrder.Turnaround },
        "HoldOnRandomBlocksWithHighTogglePattern" => TestPlans.Quick.Steps[0].ToAttributes(0) with
        {
            Order = AccessOrder.RandomBlocks,
            Hold = _shapeCaseHold,
            TimeBudget = _shapeCaseBudget,
        },
        "RefreshHoldAsShipped" => TestPlans.Quick.Steps[4].ToAttributes(0),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown supported step shape."),
    };

    // Copies of Quick's second step, each at its own position and with its own name, because the engine requires the
    // names of a plan to be unique.
    private static StepAttributes[] DistinctSteps(int count)
    {
        StepAttributes template = TestPlans.Quick.Steps[1].ToAttributes(0);
        return Enumerable.Range(0, count)
            .Select(index => template with { Index = index, Name = string.Create(CultureInfo.InvariantCulture, $"Step {index:D4}") })
            .ToArray();
    }

    private static EngineSessionOptions CreateOptions(
        SystemSnapshot system,
        TestPlan plan,
        int? workerCount = null,
        StepGate? gate = null,
        ProgressSink? sink = null,
        CallbackFaultSink? faultSink = null) =>
        CreateOptions(system, ScaledPlans.Attributes(plan), workerCount, gate, sink, faultSink);

    private static EngineSessionOptions CreateOptions(
        SystemSnapshot system,
        IReadOnlyList<StepAttributes> steps,
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
            Steps = steps,
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

    // A step list that reports a count but counts and fails every read of its elements, through the indexer or either
    // enumerator, so a validator that traversed, copied or marshalled the list is caught however it read it.
    private sealed class UnreadableStepList : IReadOnlyList<StepAttributes>
    {
        private int _reads;

        public UnreadableStepList(int count)
        {
            Count = count;
        }

        public int Count { get; }

        public int Reads => Volatile.Read(ref _reads);

        public StepAttributes this[int index] =>
            throw RecordRead(string.Create(CultureInfo.InvariantCulture, $"the indexer [{index}]"));

        public IEnumerator<StepAttributes> GetEnumerator() => throw RecordRead("GetEnumerator()");

        IEnumerator IEnumerable.GetEnumerator() => throw RecordRead("IEnumerable.GetEnumerator()");

        private InvalidOperationException RecordRead(string member)
        {
            Interlocked.Increment(ref _reads);
            return new InvalidOperationException(
                $"The step list was read through {member}; a list longer than the limit must be rejected from its count alone.");
        }
    }

    // A native session factory that counts its calls and refuses each with RQ_E_INVALID_ARGUMENT, creating no native
    // session, so a test can tell a managed rejection, which never calls it, from the engine's.
    private sealed unsafe class RecordingSessionFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public NativeEngine CreateEngine() => new((_, _, _) =>
        {
            Interlocked.Increment(ref _calls);
            return (int)EngineResultCode.InvalidArgument;
        });
    }
}

