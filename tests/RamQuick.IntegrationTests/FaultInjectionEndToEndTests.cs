using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RamQuick.Analysis;
using RamQuick.App;
using RamQuick.App.Services;
using RamQuick.App.ViewModels;
using RamQuick.Core;
using RamQuick.Interop;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// End-to-end fault injection through <c>RamQuick.Interop</c> over <c>ramquick_engine_test.dll</c>: every injected
/// corruption that had a verification opportunity is reported exactly once with its word, bit, step and pass; the
/// analyzer ranks the expected cause first and labels it a hint; the stored-detail cap keeps the true total; and Stop on
/// error halts the run within 2 s of the first injected error, with an end detail that numbers that error's step and
/// pass as its stored record and its <c>.log</c> line do.
/// </summary>
/// <remarks>
/// <para>
/// The four correctness runs drive an engine session directly on a <see cref="DispatcherThread"/>, over the scaled
/// Standard plan (budgets ÷ 10, holds 5 s) with Stop on error off. The cap-exhaustion and Stop-on-error runs drive the
/// production <see cref="RunController"/> and <see cref="MainViewModel"/> graph over the same plan, so the counts, the
/// grouped-list header and the hint labels are observed where the user reads them.
/// </para>
/// <para>
/// The fault layer applies each corruption after the word's last write and before its verification. Each correctness
/// spec sets a per-step pass quota (<see cref="FaultSpec.InjectedPassesPerStep"/>), so only the first passes of every
/// step inject. How many passes a step runs depends on memory bandwidth, but its injections are then bounded by the
/// plan alone: each test computes that worst case before it runs (<see cref="ReportBound"/>) and requires it to stay
/// within 2,000 reports and below the injection cap, which remains a safety net the log must never reach. The fault is
/// therefore live in every step, the matching covers every injection, and the cause analysis reads every step outcome
/// of the completed run.
/// </para>
/// <para>
/// The delay-dependent class ages data on the test library's controlled hold clock, which only a begun hold advances,
/// by the hold's configured 5 s; a windowed verification that a descheduled worker runs late still sees age 0, so the
/// 2,000 ms threshold separates hold verifications from all others whatever the host load.
/// </para>
/// </remarks>
public sealed class FaultInjectionEndToEndTests
{
    // Every expected report, stuck-bit repeats included, stays well under the 10,000-record detail cap.
    private const int MaxExpectedReports = 2_000;

    private const int FlippedBit = 17;
    private const int StuckBitIndex = 5;
    private const ulong StuckBitValue = 1;
    private const ulong WordAlignmentMask = ~7UL;

    // Region fractions of the AAP's fault classes: an address-region fault in a contiguous 1/32 of the region, and
    // spread that the bit-lane rule needs (at least 1/16 of the region).
    private const ulong AddressRegionDivisor = 32;
    private const ulong SpreadDivisor = 16;

    private const ulong DirectRunSeed = 0x5EED_2026_0F0A_17EDUL;
    private const ulong SingleBitFlipSeed = 0x0000_0000_B17F_11F5UL;
    private const ulong AddressRegionSeed = 0x0000_0000_AD0D_2E61UL;
    private const ulong DelayDependentSeed = 0x0000_0000_DE1A_7ED5UL;
    private const ulong CapExhaustionSeed = 0x0000_0000_CA9E_C5A1UL;
    private const ulong StopOnErrorSeed = 0x0000_0000_F125_7E77UL;

    // Per-(step, pass) counts, per-step pass quotas and safety caps of the correctness runs. Only the first <quota>
    // passes of each step inject, so the scaled Standard plan's worst case is fixed before the run: 128 passes x 1 word
    // over ten single-sweep steps and two moving-inversion steps of two sweeps is 1,792 reports for the spread, region
    // and stuck-bit classes, and 16 passes x 8 words over the two hold steps is 256 for the delay-dependent class. Each
    // stays within MaxExpectedReports and below its cap, so the cap never binds and no step loses the fault to it.
    private const int SingleBitFlipPerPass = 1;
    private const uint SingleBitFlipPassesPerStep = 128;
    private const long SingleBitFlipMaxInjections = MaxExpectedReports;
    private const uint StuckBitPassesPerStep = 128;
    private const long StuckBitMaxInjections = MaxExpectedReports;
    private const int AddressRegionPerPass = 1;
    private const uint AddressRegionPassesPerStep = 128;
    private const long AddressRegionMaxInjections = MaxExpectedReports;
    private const int DelayDependentPerPass = 8;
    private const uint DelayDependentPassesPerStep = 16;
    private const long DelayDependentMaxInjections = 1_500;

    // Verifications per pass of one word: moving inversion verifies twice, every other phase once.
    private const int MovingInversionSweepsPerPass = 2;
    private const int SingleSweepPerPass = 1;

    // Cap exhaustion: more than 15,000 corruptions against the 10,000-record detail cap.
    private const int CapExhaustionPerPass = 64;
    private const long CapExhaustionMaxInjections = 20_000;
    private const int CapExhaustionMinimumInjections = 15_000;

    private const int StopOnErrorPerPass = 2;
    private const long StopOnErrorMaxInjections = 1_500;

    private const double CoverageTolerance = 0.01;
    private const int MaxListedItems = 20;
    private const string SummaryErrorsProperty = "errors";
    private const string SummaryRunProperty = "run";
    private const string FailVerdict = "FAIL";

    // A run's files share one base name in the logs folder: <base>.log and <base>-summary.json.
    private const string SummaryJsonSuffix = "-summary.json";
    private const string LogExtension = ".log";

    // Every .log line ends in CRLF.
    private const string LogLineEnd = "\r\n";

    private static readonly TimeSpan _releasedTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _runTimeoutSlack = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _stopOnErrorBound = TimeSpan.FromSeconds(2);

    private readonly ITestOutputHelper _output;

    public FaultInjectionEndToEndTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    // Async methods carry the Async suffix under the repository's naming rule (IDE1006), tests included.
    [Fact(Timeout = 600_000)]
    public async Task SingleBitFlip_AllDetected_TopCauseVoltageSignalIntegrityAsync()
    {
        FaultSpec spec = FaultSpec.SingleBitFlip(
            FlippedBit,
            SingleBitFlipPerPass,
            SingleBitFlipMaxInjections,
            SingleBitFlipSeed) with
        {
            InjectedPassesPerStep = SingleBitFlipPassesPerStep,
        };
        AssertReportBoundBeforeRun(spec);

        FaultRun run = await RunDirectAsync(spec, TestContext.Current.CancellationToken);

        AssertCorrectnessRun(run);
        AssertEveryCompletedStepInjected(run);

        string[] wrongBits = run.Records
            .Where(record => record.BitPositions.Count != 1 || record.BitPositions[0] != FlippedBit)
            .Select(Describe)
            .ToArray();
        Assert.True(
            wrongBits.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{wrongBits.Length} of {run.Records.Count} records do not flip exactly bit {FlippedBit}: ")
            + Listed(wrongBits));

        string[] notRepeated = run.Records
            .Where(record => record.RereadOutcome != RereadOutcome.RereadRepeatsActual)
            .Select(Describe)
            .ToArray();
        Assert.True(
            notRepeated.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{notRepeated.Length} of {run.Records.Count} records do not report {RereadOutcome.RereadRepeatsActual}, ")
            + "although the flipped value is stored: " + Listed(notRepeated));

        ulong lowest = run.Records.Min(record => record.RegionOffset);
        ulong highest = run.Records.Max(record => record.RegionOffset);
        ulong minimumSpread = run.Prepared.TestedBytes / SpreadDivisor;
        Assert.True(
            highest - lowest >= minimumSpread,
            string.Create(CultureInfo.InvariantCulture, $"The reports span offsets 0x{lowest:X} to 0x{highest:X} ({highest - lowest} bytes), less than ")
            + string.Create(CultureInfo.InvariantCulture, $"1/{SpreadDivisor} of the tested {run.Prepared.TestedBytes} bytes ({minimumSpread} bytes)."));

        AssertTopCause(run, CauseCategory.VoltageSignalIntegrity);
    }

    [Fact(Timeout = 600_000)]
    public async Task StuckBit_AllDetected_SingleGroup_TopCauseRankOrDimmAsync()
    {
        ulong regionBytes = PlannedRegionBytes();
        ulong stuckWord = (regionBytes / 3) & WordAlignmentMask;
        FaultSpec spec = FaultSpec.StuckBit(stuckWord, StuckBitIndex, StuckBitValue, StuckBitMaxInjections) with
        {
            InjectedPassesPerStep = StuckBitPassesPerStep,
        };
        AssertReportBoundBeforeRun(spec);

        FaultRun run = await RunDirectAsync(spec, TestContext.Current.CancellationToken);

        // Injections are logged only where the expected bit differs from the stuck value, so matching them also
        // proves that no verification whose expected bit equals the stuck value reported anything.
        AssertCorrectnessRun(run);

        string[] elsewhere = run.Records
            .Where(record => (record.RegionOffset & WordAlignmentMask) != stuckWord)
            .Select(Describe)
            .ToArray();
        Assert.True(
            elsewhere.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{elsewhere.Length} of {run.Records.Count} records are not at the stuck word 0x{stuckWord:X}: ")
            + Listed(elsewhere));

        IReadOnlyList<ErrorGroup> groups = ErrorGrouping.Group(run.Records);
        Assert.True(
            groups.Count == 1 && groups[0].Count == run.Records.Count,
            string.Create(CultureInfo.InvariantCulture, $"The {run.Records.Count} stuck-bit reports form {groups.Count} group(s): ")
            + string.Join("; ", groups.Take(MaxListedItems).Select(group => string.Create(
                CultureInfo.InvariantCulture,
                $"word 0x{group.WordAddress:X} x{group.Count}, bits 0x{group.BitMaskUnion:X16}"))));

        // The group is keyed by the reported address, so it must be the stuck word's address as the fault layer stored
        // through it, which every injection of the one stuck word shares.
        ulong[] injectedAddresses = run.VerifiedInjections.Select(fault => fault.Address).Distinct().ToArray();
        Assert.True(
            injectedAddresses.Length == 1,
            string.Create(CultureInfo.InvariantCulture, $"The stuck word's {run.VerifiedInjections.Count} injections were stored through {injectedAddresses.Length} addresses: ")
            + Listed(injectedAddresses.Select(address => string.Create(CultureInfo.InvariantCulture, $"0x{address:X16}")).ToArray()));
        Assert.True(
            groups[0].WordAddress == injectedAddresses[0],
            string.Create(CultureInfo.InvariantCulture, $"The stuck-bit group is at address 0x{groups[0].WordAddress:X16}, but the stuck word's corruptions ")
            + string.Create(CultureInfo.InvariantCulture, $"were stored through 0x{injectedAddresses[0]:X16}."));

        AssertTopCause(run, CauseCategory.RankOrDimm);
    }

    [Fact(Timeout = 600_000)]
    public async Task AddressRegion_AllDetected_InsideRegion_TopCauseRankOrDimmAsync()
    {
        ulong regionBytes = PlannedRegionBytes();
        ulong offset = (regionBytes / 4) & WordAlignmentMask;
        ulong length = (regionBytes / AddressRegionDivisor) & WordAlignmentMask;
        FaultSpec spec = FaultSpec.AddressRegion(
            offset,
            length,
            AddressRegionPerPass,
            AddressRegionMaxInjections,
            AddressRegionSeed) with
        {
            InjectedPassesPerStep = AddressRegionPassesPerStep,
        };
        AssertReportBoundBeforeRun(spec);

        FaultRun run = await RunDirectAsync(spec, TestContext.Current.CancellationToken);

        AssertCorrectnessRun(run);
        AssertEveryCompletedStepInjected(run);

        string[] outside = run.Records
            .Where(record => record.RegionOffset < offset || record.RegionOffset >= offset + length)
            .Select(Describe)
            .ToArray();
        Assert.True(
            outside.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{outside.Length} of {run.Records.Count} records lie outside the faulty range ")
            + string.Create(CultureInfo.InvariantCulture, $"[0x{offset:X}, 0x{offset + length:X}): ") + Listed(outside));

        AssertTopCause(run, CauseCategory.RankOrDimm);
    }

    [Fact(Timeout = 600_000)]
    public async Task DelayDependent_DetectedOnlyAfterHold_TopCauseRefreshRetentionAsync()
    {
        FaultSpec spec = FaultSpec.DelayDependent(
            DelayDependentPerPass,
            DelayDependentMaxInjections,
            DelayDependentSeed) with
        {
            InjectedPassesPerStep = DelayDependentPassesPerStep,
        };
        Assert.Equal(TestEngineNative.DefaultDelayThresholdMs, spec.DelayThresholdMs);
        AssertReportBoundBeforeRun(spec);

        FaultRun run = await RunDirectAsync(spec, TestContext.Current.CancellationToken);

        AssertCorrectnessRun(run);

        // Hold steps exist in the plan (Q5 and S10), and only their data ages past the 2000 ms threshold: the test
        // library measures the age on its controlled hold clock, which advances by a hold's configured 5 s once every
        // worker has written and flushed its slice and the hold has begun, and by nothing else. A windowed
        // verification that host load delays by seconds therefore still sees age 0, so the assertions below hold
        // whatever the scheduling, while every hold verification sees 5,000 ms, above the unchanged threshold.
        int holdSteps = run.Plan.Steps.Count(step => step.HasHold);
        Assert.True(
            run.Records.Count >= 1,
            string.Create(CultureInfo.InvariantCulture, $"No delay-dependent fault was reported, although the plan has {holdSteps} hold step(s)."));

        string[] notAfterHold = run.Records
            .Where(record => record.AccessType != AccessType.VerifyAfterHold || !run.Plan.Steps[record.StepIndex].HasHold)
            .Select(Describe)
            .ToArray();
        Assert.True(
            notAfterHold.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{notAfterHold.Length} of {run.Records.Count} records were not found by a verification after a hold: ")
            + Listed(notAfterHold));

        string[] erroneousWindowedSteps = run.Completed.StepResults
            .Where(outcome => !outcome.Attributes.HasHold && outcome.Errors != 0)
            .Select(outcome => string.Create(CultureInfo.InvariantCulture, $"step {outcome.Attributes.Index + 1} '{outcome.Attributes.Name}': {outcome.Errors} errors"))
            .ToArray();
        Assert.True(
            erroneousWindowedSteps.Length == 0,
            "Steps without a hold counted errors, although no hold begins between their writes and verifications, so the "
            + "controlled hold clock never ages their data: "
            + Listed(erroneousWindowedSteps));

        AssertTopCause(run, CauseCategory.RefreshRetention);
    }

    [Fact(Timeout = 600_000)]
    public async Task CapExhaustion_StoresTenThousand_KeepsTrueTotalAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ulong regionBytes = PlannedRegionBytes();
        ulong offset = (regionBytes / 4) & WordAlignmentMask;
        ulong length = (regionBytes / AddressRegionDivisor) & WordAlignmentMask;
        FaultSpec spec = FaultSpec.AddressRegion(
            offset,
            length,
            CapExhaustionPerPass,
            CapExhaustionMaxInjections,
            CapExhaustionSeed);

        // The shown counts and the truncation index are written and read on the dispatcher thread only.
        var errorCountValues = new List<ulong>();
        int truncatedAtIndex = -1;

        ControllerHarness harness = await ControllerHarness.CreateAsync(spec, regionBytes);
        try
        {
            MainViewModel viewModel = harness.ViewModel;
            RunController controller = harness.Controller;
            await harness.Ui.InvokeAsync(() =>
            {
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.ErrorCountText)
                        && ulong.TryParse(viewModel.ErrorCountText, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out ulong shown))
                    {
                        errorCountValues.Add(shown);
                    }
                };
                controller.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RunController.IsErrorDetailTruncated)
                        && controller.IsErrorDetailTruncated
                        && truncatedAtIndex < 0)
                    {
                        truncatedAtIndex = errorCountValues.Count - 1;
                    }
                };
            });

            await harness.StartAndWaitAsync(stopOnError: false);

            (InstrumentedRun engineRun, EngineCompleted completed) = harness.LastEngineRun();
            int verifiedInjections = engineRun.InjectionLog.Count(fault => fault.VerifiedOpportunity);
            Assert.True(
                verifiedInjections > CapExhaustionMinimumInjections,
                string.Create(CultureInfo.InvariantCulture, $"Precondition: more than {CapExhaustionMinimumInjections} corruptions with a verification ")
                + string.Create(CultureInfo.InvariantCulture, $"opportunity are needed to exhaust the {ErrorGrouping.StoredDetailCap}-record cap; ")
                + string.Create(CultureInfo.InvariantCulture, $"{verifiedInjections} of {engineRun.InjectionLog.Count} logged had one."));

            ControllerView view = await harness.ReadViewAsync();
            ulong[] shownCounts = await harness.Ui.InvokeAsync(() => errorCountValues.ToArray());
            int truncationIndex = await harness.Ui.InvokeAsync(() => truncatedAtIndex);
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Cap exhaustion: {verifiedInjections} verified injections, true total {view.TotalErrors}, "
                + $"{shownCounts.Length} error-count updates, truncated at update {truncationIndex}, header '{view.ErrorGroupsHeader}'."));

            Assert.True(
                view.State == RunState.Failed,
                string.Create(CultureInfo.InvariantCulture, $"Expected {RunState.Failed}, got {view.State} ({view.EndReason}, '{view.ResultMessage}')."));
            Assert.True(
                view.TotalErrors == (ulong)verifiedInjections && completed.TotalErrors == (ulong)verifiedInjections,
                string.Create(CultureInfo.InvariantCulture, $"True totals disagree: controller {view.TotalErrors}, Completed {completed.TotalErrors}, ")
                + string.Create(CultureInfo.InvariantCulture, $"verified injections {verifiedInjections}."));
            Assert.True(view.IsErrorDetailTruncated, "The controller does not report the stored detail as truncated.");
            Assert.Equal(UiText.ErrorGroupsTruncatedHeader, view.ErrorGroupsHeader);

            await AssertTruncatedSummaryAsync(harness.Root, (ulong)verifiedInjections, cancellationToken);
            AssertShownCountKeepsRising(shownCounts, truncationIndex, view.TotalErrors);
            AssertProgressKeepsRisingPastCap(engineRun);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        AssertNoLiveSessions();
    }

    [Fact(Timeout = 600_000)]
    public async Task StopOnError_FirstInjectedError_FailsWithinTwoSecondsAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ulong regionBytes = PlannedRegionBytes();
        FaultSpec spec = FaultSpec.SingleBitFlip(FlippedBit, StopOnErrorPerPass, StopOnErrorMaxInjections, StopOnErrorSeed);

        ControllerHarness harness = await ControllerHarness.CreateAsync(spec, regionBytes);
        try
        {
            await harness.StartAndWaitAsync(stopOnError: true);

            (InstrumentedRun engineRun, EngineCompleted completed) = harness.LastEngineRun();
            ControllerView view = await harness.ReadViewAsync();
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Stop on error: {view.State} ({view.EndReason}), {completed.TotalErrors} error(s), first error at "
                + $"{completed.FirstErrorTime?.TotalMilliseconds:0} ms, Completed at {completed.Elapsed.TotalMilliseconds:0} ms; "
                + $"{view.CauseHintTexts.Count} hint(s)."));

            Assert.True(
                view.State == RunState.Failed && view.EndReason == RunEndReason.FirstError,
                string.Create(CultureInfo.InvariantCulture, $"Expected {RunState.Failed} with {RunEndReason.FirstError}, got {view.State} with ")
                + string.Create(CultureInfo.InvariantCulture, $"{view.EndReason} ('{view.EndDetail}', '{view.ResultMessage}')."));

            Assert.True(
                completed.FirstErrorTime is not null,
                string.Create(CultureInfo.InvariantCulture, $"Completed reports {completed.TotalErrors} error(s) but no first-error time."));
            TimeSpan engineLatency = completed.Elapsed - completed.FirstErrorTime.Value;
            Assert.True(
                engineLatency <= _stopOnErrorBound,
                string.Create(CultureInfo.InvariantCulture, $"The run completed {engineLatency.TotalMilliseconds:0} ms after its first error ")
                + string.Create(CultureInfo.InvariantCulture, $"(first error {completed.FirstErrorTime.Value.TotalMilliseconds:0} ms, Completed ")
                + string.Create(CultureInfo.InvariantCulture, $"{completed.Elapsed.TotalMilliseconds:0} ms into the test); the bound is {_stopOnErrorBound.TotalSeconds:0} s."));

            Assert.True(
                engineRun.FirstErrorAt is not null && engineRun.CompletedAt is not null,
                "The first error batch or Completed was not received.");
            long firstBatchAt = engineRun.FirstErrorAt.Value;
            long completedAt = engineRun.CompletedAt.Value;

            // The reported first error is one of the injected corruptions, at its word, address, bit, step and pass.
            Assert.True(view.FirstError is not null, "The controller holds no first error record.");
            var firstKey = MatchKey.Of(view.FirstError);
            Assert.True(
                engineRun.InjectionLog.Any(fault => MatchKey.Of(fault) == firstKey),
                string.Create(CultureInfo.InvariantCulture, $"The first error ({Describe(view.FirstError)}) matches no injected corruption."));

            // Every error of this run is an injected corruption, confirmed inside the forwarded verification that follows
            // its store, and each call's injection time is read before it stores anything. The earliest verified
            // injection therefore precedes the first confirmed error, so the interval from it to the Completed receipt is
            // an upper bound on the required one, with no allowance for batching or dispatch.
            InjectedFault? earliest = engineRun.InjectionLog
                .Where(fault => fault.VerifiedOpportunity)
                .MinBy(fault => fault.InjectedNs);
            Assert.True(
                earliest is not null,
                string.Create(CultureInfo.InvariantCulture, $"The run reported {completed.TotalErrors} error(s), but the fault layer logged no verified injection."));
            long injectedAt = earliest.InjectedAt;

            // The session clock and Stopwatch must both read QueryPerformanceCounter: the earliest injection then follows
            // the session's creation and precedes the receipt of the first error batch, which it caused.
            Assert.True(
                engineRun.CreatedAt <= injectedAt && injectedAt <= firstBatchAt,
                string.Create(CultureInfo.InvariantCulture, $"The earliest injection's timestamp {injectedAt} lies outside [session created {engineRun.CreatedAt}, ")
                + string.Create(CultureInfo.InvariantCulture, $"first error batch received {firstBatchAt}]; the session clock and Stopwatch do not share a time base."));

            TimeSpan injectionToCompleted = Stopwatch.GetElapsedTime(injectedAt, completedAt);
            TimeSpan injectionToBatch = Stopwatch.GetElapsedTime(injectedAt, firstBatchAt);
            TimeSpan batchToCompleted = Stopwatch.GetElapsedTime(firstBatchAt, completedAt);
            TimeSpan remainder = injectionToCompleted - engineLatency;
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Earliest verified injection to Completed receipt: {injectionToCompleted.TotalMilliseconds:0.0} ms. Injection to first error batch "
                + $"receipt (confirmation, batching, dispatch): {injectionToBatch.TotalMilliseconds:0.0} ms; first batch receipt to Completed "
                + $"receipt: {batchToCompleted.TotalMilliseconds:0.0} ms; engine first error to Completed: {engineLatency.TotalMilliseconds:0} ms; "
                + $"remainder (injection to confirmation, Completed dispatch): {remainder.TotalMilliseconds:0.0} ms."));
            Assert.True(
                injectionToCompleted <= _stopOnErrorBound,
                string.Create(CultureInfo.InvariantCulture, $"Completed was received {injectionToCompleted.TotalMilliseconds:0.0} ms after the earliest verified injection, ")
                + string.Create(CultureInfo.InvariantCulture, $"which precedes the first confirmed error; the bound is {_stopOnErrorBound.TotalSeconds:0} s (engine-side ")
                + string.Create(CultureInfo.InvariantCulture, $"{engineLatency.TotalMilliseconds:0} ms, first batch receipt to Completed receipt {batchToCompleted.TotalMilliseconds:0.0} ms)."));

            Assert.True(
                view.CauseHintCount > 0,
                string.Create(CultureInfo.InvariantCulture, $"The run failed with {view.TotalErrors} error(s) but the controller holds no cause hints."));
            Assert.True(view.HasCauseHints, "The view model does not show the cause hints of the failed run.");
            Assert.Equal(UiText.CauseHintsHeader, view.CauseHintsHeader);
            string[] unlabelled = view.CauseHintTexts
                .Where(text => !text.StartsWith(UiText.HintPrefix, StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                view.CauseHintTexts.Count > 0 && unlabelled.Length == 0,
                string.Create(CultureInfo.InvariantCulture, $"{unlabelled.Length} of {view.CauseHintTexts.Count} displayed hints lack the '{UiText.HintPrefix}' prefix: ")
                + Listed(unlabelled));

            await AssertFirstErrorSummaryAsync(harness.Root, ScaledPlans.FaultInjectionStandard.Steps.Count, cancellationToken);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        AssertNoLiveSessions();
    }

    // The region every run of this class tests: TestRegions' 512 MiB request, at least four 2 MiB slices per logical
    // processor. Fault offsets are computed from it before the engine that injects them exists.
    private static ulong PlannedRegionBytes() =>
        TestRegions.Bytes(TestEngines.CreateDefault().QuerySystem(), TestRegions.Mib512);

    private static TimeSpan RunTimeout(TestPlan plan) => (plan.TargetDuration * 3) + _runTimeoutSlack;

    private static void AssertNoLiveSessions()
    {
        int live = SessionProbe.LiveSessionCount();
        Assert.True(live == 0, string.Create(CultureInfo.InvariantCulture, $"{live} engine session(s) are still alive after the test disposed its own."));
    }

    // One run of the scaled Standard plan, created and started on a dispatcher thread so its events are posted there,
    // with Stop on error off and the region as its own shrink floor.
    private async Task<FaultRun> RunDirectAsync(FaultSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        TestPlan plan = ScaledPlans.FaultInjectionStandard;

        FaultRun run;
        using (var ui = new DispatcherThread())
        {
            DirectSession? started = null;
            try
            {
                started = await ui.InvokeAsync(() => StartDirectSession(spec, plan));
                EngineCompleted completed = await started.Recorder.WaitCompletedAsync(RunTimeout(plan), cancellationToken);

                // The fault layer's ground truth lives in the native session, so it is read before disposal.
                IReadOnlyList<InjectedFault> injectionLog = SessionProbe.GetInjectionLog(started.Session);
                ulong independentVerifiedBytes = SessionProbe.GetIndependentVerifiedBytes(started.Session);

                EngineReleased released = await started.Recorder.WaitReleasedAsync(_releasedTimeout, cancellationToken);
                TimeSpan wallDuration = Stopwatch.GetElapsedTime(started.StartedAt);

                EnginePrepared? prepared = started.Recorder.Prepared;
                Assert.True(
                    prepared is not null,
                    string.Create(CultureInfo.InvariantCulture, $"The run never prepared: it ended {completed.EndReason} ('{completed.EndDetail}', ")
                    + string.Create(CultureInfo.InvariantCulture, $"Windows error {completed.OsError})."));

                run = new FaultRun(
                    spec,
                    plan,
                    started.System,
                    started.RegionBytes,
                    prepared,
                    completed,
                    released,
                    started.Recorder.Errors,
                    injectionLog,
                    independentVerifiedBytes,
                    wallDuration);
            }
            finally
            {
                started?.Session.Dispose();
            }
        }

        AssertNoLiveSessions();
        LogRun(run);
        return run;
    }

    // Runs on the dispatcher thread: the session captures that thread's context and posts every event to it.
    private static DirectSession StartDirectSession(FaultSpec spec, TestPlan plan)
    {
        NativeEngine engine = TestEngines.Create(spec);
        SystemSnapshot system = engine.QuerySystem();
        ulong regionBytes = TestRegions.Bytes(system, TestRegions.Mib512);
        IEngineSession session = engine.CreateSession(new EngineSessionOptions
        {
            RegionBytes = regionBytes,
            MinRegionBytes = regionBytes,
            WorkerCount = system.LogicalProcessors,
            Seed = DirectRunSeed,
            StopOnError = false,
            Steps = ScaledPlans.Attributes(plan),
        });

        try
        {
            EngineRunRecorder recorder = EngineRunRecorder.Attach(session);
            long startedAt = Stopwatch.GetTimestamp();
            session.Start();
            return new DirectSession(session, recorder, system, regionBytes, startedAt);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // The checks every correctness run shares: a completed run over the planned region whose injections stay within the
    // plan's bound and below the cap, in which every injection with a verification opportunity is reported exactly
    // once, the true totals agree, and the independent byte count confirms the reported coverage.
    private static void AssertCorrectnessRun(FaultRun run)
    {
        Assert.True(
            run.Completed.EndReason == RunEndReason.Completed,
            string.Create(CultureInfo.InvariantCulture, $"The run ended {run.Completed.EndReason} ('{run.Completed.EndDetail}') instead of completing every step."));

        // The spec's offsets were computed for the planned size; a shrunk region would move or drop its targets.
        Assert.True(
            run.Prepared.TestedBytes == run.RegionBytes,
            string.Create(CultureInfo.InvariantCulture, $"Precondition: the engine tested {run.Prepared.TestedBytes} bytes instead of the planned ")
            + string.Create(CultureInfo.InvariantCulture, $"{run.RegionBytes}; locking shrank the region, so the fault offsets no longer describe it."));

        // The per-step quota, not the cap, bounds the injections: a log beyond the plan's bound means the quota failed,
        // and a log at the cap means the cap ended injection early and some steps ran without the fault.
        Assert.True(
            run.InjectionLog.Count <= run.ReportBound,
            string.Create(CultureInfo.InvariantCulture, $"The fault layer logged {run.InjectionLog.Count} injections, more than the {run.ReportBound} ")
            + string.Create(CultureInfo.InvariantCulture, $"that {run.Spec.InjectedPassesPerStep} injecting passes per step allow on the plan."));
        Assert.True(
            (ulong)run.InjectionLog.Count < run.Spec.MaxInjections,
            string.Create(CultureInfo.InvariantCulture, $"The fault layer reached its safety cap of {run.Spec.MaxInjections} injections, ")
            + "so the cap, not the per-step quota, ended injection and later steps ran without the fault.");

        IReadOnlyList<InjectedFault> verified = run.VerifiedInjections;
        Assert.True(
            verified.Count is > 0 and <= MaxExpectedReports,
            string.Create(CultureInfo.InvariantCulture, $"Precondition: {verified.Count} injections had a verification opportunity; between 1 and ")
            + string.Create(CultureInfo.InvariantCulture, $"{MaxExpectedReports} are required, well under the {ErrorGrouping.StoredDetailCap}-record detail cap."));

        AssertEveryInjectionReportedOnce(verified, run.Records);

        Assert.True(
            run.Completed.TotalErrors == (ulong)run.Records.Count && run.Records.Count == verified.Count,
            string.Create(CultureInfo.InvariantCulture, $"True totals disagree: Completed {run.Completed.TotalErrors}, records {run.Records.Count}, ")
            + string.Create(CultureInfo.InvariantCulture, $"verified injections {verified.Count}."));

        AssertIndependentCoverage(run);
    }

    // Multiset matching: moving inversion verifies twice per pass, so one target can be injected, and reported, twice.
    // The fault layer logs the address it stored each corruption through, so every record's address is checked against
    // that ground truth, not merely against the record's own offset.
    private static void AssertEveryInjectionReportedOnce(IReadOnlyList<InjectedFault> verified, IReadOnlyList<ErrorRecord> records)
    {
        MatchKey[] injectedKeys = verified.Select(MatchKey.Of).ToArray();
        MatchKey[] reportedKeys = records.Select(MatchKey.Of).ToArray();
        Dictionary<MatchKey, int> injected = CountKeys(injectedKeys);
        Dictionary<MatchKey, int> reported = CountKeys(reportedKeys);

        var unreported = new List<string>();
        int unreportedTotal = 0;

        // The injected addresses still unreported, by the rest of their key: a surplus record with that rest names the
        // right word by offset, mask, step and pass but reports another address.
        var unreportedAddresses = new Dictionary<MatchKey, Queue<ulong>>();
        foreach ((MatchKey key, int count) in injected)
        {
            int seen = reported.GetValueOrDefault(key);
            if (seen < count)
            {
                unreportedTotal += count - seen;
                unreported.Add(string.Create(CultureInfo.InvariantCulture, $"{key} (injected x{count}, reported x{seen})"));
                if (!unreportedAddresses.TryGetValue(key.WithoutAddress, out Queue<ulong>? addresses))
                {
                    addresses = new Queue<ulong>();
                    unreportedAddresses.Add(key.WithoutAddress, addresses);
                }

                for (int missing = count - seen; missing > 0; missing--)
                {
                    addresses.Enqueue(key.Address);
                }
            }
        }

        var unexpected = new List<string>();
        int unexpectedTotal = 0;
        var wrongAddresses = new List<string>();
        int wrongAddressTotal = 0;
        foreach ((MatchKey key, int count) in reported)
        {
            int expected = injected.GetValueOrDefault(key);
            if (count > expected)
            {
                unexpectedTotal += count - expected;
                unexpected.Add(string.Create(CultureInfo.InvariantCulture, $"{key} (reported x{count}, injected x{expected})"));
                if (unreportedAddresses.TryGetValue(key.WithoutAddress, out Queue<ulong>? addresses))
                {
                    for (int surplus = count - expected; surplus > 0 && addresses.TryDequeue(out ulong injectedAddress); surplus--)
                    {
                        wrongAddressTotal++;
                        wrongAddresses.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"word 0x{key.WordOffset:X} mask 0x{key.BitMask:X16} step {key.StepIndex + 1} pass {key.PassIndex + 1}: "
                            + $"expected address 0x{injectedAddress:X16}, reported 0x{key.Address:X16}"));
                    }
                }
            }
        }

        if (unreportedTotal == 0 && unexpectedTotal == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"{verified.Count} verified injections and {records.Count} records do not match one to one: ");
        message.Append(CultureInfo.InvariantCulture, $"{unreportedTotal} injection(s) unreported, {unexpectedTotal} record(s) unexpected.");
        message.Append(" Unreported: ").Append(Listed(unreported)).Append(" Unexpected: ").Append(Listed(unexpected));

        if (wrongAddressTotal > 0)
        {
            message.Append(CultureInfo.InvariantCulture, $" {wrongAddressTotal} record(s) match an unreported injection's word offset, mask, step and pass ");
            message.Append("but not the virtual address its corruption was stored through: ").Append(Listed(wrongAddresses));
            message.Append(" rq_error_record.address must be the mismatching word's own address (src/engine/core/errors/error_collector).");
        }

        if (unexpectedTotal == records.Count && ConstantOffset(injectedKeys, reportedKeys) is { } delta)
        {
            message.Append(CultureInfo.InvariantCulture, $" Every record misses its injection by the constant offset {delta} bytes at the right address: ");
            message.Append("rq_error_record.region_offset and rq_injected_fault.word_offset must both be logical region offsets ");
            message.Append("(src/engine/core/errors/error_collector and tests/engine.tests/fakes).");
        }

        Assert.Fail(message.ToString());
    }

    // The offset d, other than 0, for which every record's logical offset moved by -d matches the injections as a
    // multiset, its address unchanged, or null.
    private static long? ConstantOffset(MatchKey[] injectedKeys, MatchKey[] reportedKeys)
    {
        if (reportedKeys.Length == 0 || injectedKeys.Length != reportedKeys.Length)
        {
            return null;
        }

        Dictionary<MatchKey, int> injected = CountKeys(injectedKeys);
        MatchKey first = reportedKeys[0];
        IEnumerable<ulong> candidates = injected.Keys
            .Where(key => key.Address == first.Address && key.BitMask == first.BitMask
                && key.StepIndex == first.StepIndex && key.PassIndex == first.PassIndex)
            .Select(key => key.WordOffset);

        foreach (ulong candidate in candidates)
        {
            ulong delta = unchecked(first.WordOffset - candidate);
            if (delta == 0)
            {
                continue;
            }

            Dictionary<MatchKey, int> shifted = CountKeys(reportedKeys.Select(key => key with { WordOffset = unchecked(key.WordOffset - delta) }));
            if (shifted.Count == injected.Count && shifted.All(entry => injected.GetValueOrDefault(entry.Key) == entry.Value))
            {
                return unchecked((long)delta);
            }
        }

        return null;
    }

    private static Dictionary<MatchKey, int> CountKeys(IEnumerable<MatchKey> keys)
    {
        var counts = new Dictionary<MatchKey, int>();
        foreach (MatchKey key in keys)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    // The counting layer sums the bytes passed to every verify primitive, independently of the coverage counter.
    private static void AssertIndependentCoverage(FaultRun run)
    {
        ulong reported = run.Completed.VerifiedBytes;
        ulong independent = run.IndependentVerifiedBytes;
        double deviation = independent == 0
            ? double.PositiveInfinity
            : Math.Abs((double)reported - independent) / independent;
        Assert.True(
            deviation <= CoverageTolerance,
            string.Create(CultureInfo.InvariantCulture, $"Reported verified bytes {reported} deviate {deviation:P3} from the independent count {independent}; ")
            + string.Create(CultureInfo.InvariantCulture, $"at most {CoverageTolerance:P0} is allowed."));
    }

    // The most reports a correctness spec can cause on the plan, fixed before the run: each step injects only in its
    // first InjectedPassesPerStep passes, each of which verifies every word once per sweep, moving inversion twice; a
    // stuck bit corrupts one word per sweep at most, and a delay-dependent fault only in hold steps, the one place the
    // controlled hold clock ages data. 0 quota means every pass injects, which the plan cannot bound.
    private static long ReportBound(FaultSpec spec, TestPlan plan)
    {
        long bound = 0;
        foreach (StepDefinition step in InjectingSteps(spec, plan))
        {
            int sweeps = step.Pattern is PatternKind.MovingInversionWalking or PatternKind.MovingInversionRandom
                ? MovingInversionSweepsPerPass
                : SingleSweepPerPass;
            bound = checked(bound + (spec.InjectedPassesPerStep * WordsPerSweep(spec) * sweeps));
        }

        return bound;
    }

    // The steps in which the spec can inject: every step, except that a delay-dependent fault needs a hold.
    private static IEnumerable<StepDefinition> InjectingSteps(FaultSpec spec, TestPlan plan) =>
        plan.Steps.Where(step => spec.Kind != FaultKind.DelayDependent || step.HasHold);

    // The words a spec can corrupt per sweep of a pass: the per-pass count, or the one stuck word.
    private static long WordsPerSweep(FaultSpec spec) => spec.Kind == FaultKind.StuckBit ? 1 : spec.PerPassCount;

    // The bounded-below-the-cap precondition, asserted before the run: a set quota whose worst case on the plan stays
    // within MaxExpectedReports and below the injection cap, so the cap can never end injection before the last step.
    private void AssertReportBoundBeforeRun(FaultSpec spec)
    {
        TestPlan plan = ScaledPlans.FaultInjectionStandard;
        long bound = ReportBound(spec, plan);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Report bound: {spec.InjectedPassesPerStep} injecting passes per step x {WordsPerSweep(spec)} word(s) per sweep over "
            + $"{InjectingSteps(spec, plan).Count()} of {plan.Steps.Count} steps, moving inversion sweeping twice, allow at most {bound} "
            + $"reports (limit {MaxExpectedReports}, cap {spec.MaxInjections})."));
        Assert.True(
            spec.InjectedPassesPerStep > 0 && bound is > 0 and <= MaxExpectedReports && (ulong)bound < spec.MaxInjections,
            string.Create(CultureInfo.InvariantCulture, $"Precondition: {spec.Kind} with {spec.InjectedPassesPerStep} injecting passes per step allows ")
            + string.Create(CultureInfo.InvariantCulture, $"{bound} reports on the plan; a nonzero quota whose bound is at most {MaxExpectedReports} and below ")
            + string.Create(CultureInfo.InvariantCulture, $"the cap of {spec.MaxInjections} is required."));
    }

    // A fault spread over its whole target range is verified by every pass, so every step that completed a pass
    // carries verified injections: the cause analysis then sees the fault in every step, none left clean by the cap.
    private static void AssertEveryCompletedStepInjected(FaultRun run)
    {
        string[] clean = run.Completed.StepResults
            .Where(outcome => outcome.PassesCompleted >= 1
                && !run.VerifiedInjections.Any(fault => fault.StepIndex == outcome.Attributes.Index))
            .Select(outcome => string.Create(
                CultureInfo.InvariantCulture,
                $"step {outcome.Attributes.Index + 1} '{outcome.Attributes.Name}' ({outcome.PassesCompleted} passes)"))
            .ToArray();
        Assert.True(
            clean.Length == 0,
            string.Create(CultureInfo.InvariantCulture, $"{clean.Length} step(s) completed a pass without a verified {run.Spec.Kind} injection: ")
            + Listed(clean));
    }

    // The production analysis of the whole completed run: every record and every step outcome, the clean passes after
    // each step's quota included, as the controller analyses a real run.
    private void AssertTopCause(FaultRun run, CauseCategory expected)
    {
        IReadOnlyList<StepOutcome> analysedSteps = run.Completed.StepResults;
        CauseHints hints = ErrorAnalyzer.Analyze(new AnalysisInput(
            run.Records,
            analysedSteps,
            run.Prepared.TestedBytes,
            run.Prepared.WorkerCount,
            MemoryType.Unknown));

        string scope = string.Create(
            CultureInfo.InvariantCulture,
            $"all {analysedSteps.Count} steps of {run.Plan.Steps.Count} analysed");
        string ranking = hints.IsEmpty
            ? "no hints"
            : string.Join("; ", hints.Items.Select(hint => string.Create(
                CultureInfo.InvariantCulture,
                $"#{hint.Rank} {hint.Category} score {hint.Score:0.######} ({hint.LikelihoodPercent:0.#} %): {hint.Evidence}")));
        _output.WriteLine("Cause hints (" + scope + "): " + ranking);

        Assert.Equal(CauseHints.HintLabel, hints.Label);
        Assert.True(
            !hints.IsEmpty && hints.Items[0].Category == expected,
            string.Create(CultureInfo.InvariantCulture, $"Expected {expected} ranked first for {run.Spec.Kind} faults ({scope}); ranking: {ranking}."));
    }

    private static async Task<(string FileName, JsonDocument Document)> ReadPublishedSummaryAsync(
        TempDataRoot root,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> summaries = root.SummaryJsonFiles();
        Assert.True(
            summaries.Count == 1,
            string.Create(CultureInfo.InvariantCulture, $"Expected one published JSON summary in '{root.LogsPath}', found {summaries.Count}: ") + Listed(summaries));

        string json = await File.ReadAllTextAsync(summaries[0], cancellationToken);
        return (Path.GetFileName(summaries[0]), JsonDocument.Parse(json));
    }

    private async Task AssertFirstErrorSummaryAsync(TempDataRoot root, int stepCount, CancellationToken cancellationToken)
    {
        (string fileName, JsonDocument document) = await ReadPublishedSummaryAsync(root, cancellationToken);
        using (document)
        {
            JsonElement run = document.RootElement.GetProperty(SummaryRunProperty);
            string? verdict = run.GetProperty("verdict").GetString();
            string? endReason = run.GetProperty("endReason").GetString();
            JsonElement causeHints = document.RootElement.GetProperty("causeHints");
            string? label = causeHints.GetProperty("label").GetString();
            int hintCount = causeHints.GetProperty("items").GetArrayLength();

            Assert.True(
                verdict == FailVerdict
                    && endReason == nameof(RunEndReason.FirstError)
                    && label == CauseHints.HintLabel
                    && hintCount > 0,
                string.Create(CultureInfo.InvariantCulture, $"Summary '{fileName}': run.verdict {verdict}, run.endReason {endReason}, ")
                + string.Create(CultureInfo.InvariantCulture, $"causeHints.label '{label}', {hintCount} hint(s); expected {FailVerdict}, ")
                + string.Create(CultureInfo.InvariantCulture, $"{nameof(RunEndReason.FirstError)}, '{CauseHints.HintLabel}' and at least one hint."));

            // The end detail, the first stored record and that record's .log ERROR line must number the error's step and
            // pass alike: the detail and the .log count from 1, the summary's records keep the engine's 0-based indices.
            // Every pass ends at a barrier of all workers, so whichever worker's error wrote the detail, it was found in
            // the step and pass of record 0.
            string? endDetail = run.GetProperty("endDetail").GetString();
            JsonElement records = document.RootElement.GetProperty(SummaryErrorsProperty).GetProperty("records");
            Assert.True(records.GetArrayLength() > 0, string.Create(CultureInfo.InvariantCulture, $"Summary '{fileName}' stores no error record."));
            JsonElement first = records[0];
            ulong sequence = first.GetProperty("sequence").GetUInt64();
            int stepNumber = first.GetProperty("stepIndex").GetInt32() + 1;
            int passNumber = first.GetProperty("passIndex").GetInt32() + 1;

            string logPath = Path.Combine(root.LogsPath, fileName[..^SummaryJsonSuffix.Length] + LogExtension);
            Assert.True(File.Exists(logPath), string.Create(CultureInfo.InvariantCulture, $"Summary '{fileName}' has no results log '{logPath}'."));
            string errorPrefix = string.Create(CultureInfo.InvariantCulture, $" ERROR seq={sequence} ");
            string? errorLine = (await ReadSharedTextAsync(logPath, cancellationToken))
                .Split(LogLineEnd, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains(errorPrefix, StringComparison.Ordinal));
            _output.WriteLine("Summary run.endDetail: " + (endDetail ?? "(null)"));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $".log ERROR line of errors.records[0] (seq {sequence}): {errorLine ?? "(none)"}"));

            string expectedDetail = string.Create(
                CultureInfo.InvariantCulture,
                $"First error: step {stepNumber} of {stepCount}, pass {passNumber}, worker ");
            Assert.True(
                endDetail is not null && endDetail.StartsWith(expectedDetail, StringComparison.Ordinal),
                string.Create(CultureInfo.InvariantCulture, $"Summary '{fileName}': run.endDetail '{endDetail}' does not begin '{expectedDetail}', the step and pass ")
                + string.Create(CultureInfo.InvariantCulture, $"of errors.records[0] (stepIndex {stepNumber - 1}, passIndex {passNumber - 1}) counted from 1."));

            string expectedStep = string.Create(CultureInfo.InvariantCulture, $" step={stepNumber}/{stepCount} ");
            string expectedPass = string.Create(CultureInfo.InvariantCulture, $" pass={passNumber} ");
            Assert.True(
                errorLine is not null
                    && errorLine.Contains(expectedStep, StringComparison.Ordinal)
                    && errorLine.Contains(expectedPass, StringComparison.Ordinal),
                string.Create(CultureInfo.InvariantCulture, $"'{Path.GetFileName(logPath)}': the ERROR line of record {sequence} ('{errorLine}') does not hold ")
                + string.Create(CultureInfo.InvariantCulture, $"'{expectedStep.Trim()}' and '{expectedPass.Trim()}', the numbering of run.endDetail '{endDetail}'."));
        }
    }

    // The results log is written with read sharing only, so a reader that may overlap its writer allows write sharing.
    private static async Task<string> ReadSharedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task AssertTruncatedSummaryAsync(TempDataRoot root, ulong trueTotal, CancellationToken cancellationToken)
    {
        (string fileName, JsonDocument read) = await ReadPublishedSummaryAsync(root, cancellationToken);
        using JsonDocument document = read;
        JsonElement errors = document.RootElement.GetProperty(SummaryErrorsProperty);
        string? verdict = document.RootElement.GetProperty(SummaryRunProperty).GetProperty("verdict").GetString();
        int storedCount = errors.GetProperty("storedCount").GetInt32();
        int storedCap = errors.GetProperty("storedCap").GetInt32();
        bool truncated = errors.GetProperty("truncated").GetBoolean();
        ulong total = errors.GetProperty("total").GetUInt64();

        Assert.True(
            verdict == FailVerdict
                && storedCount == ErrorGrouping.StoredDetailCap
                && storedCap == ErrorGrouping.StoredDetailCap
                && truncated
                && total == trueTotal,
            string.Create(CultureInfo.InvariantCulture, $"Summary '{fileName}': run.verdict {verdict}, errors.storedCount {storedCount}, ")
            + string.Create(CultureInfo.InvariantCulture, $"errors.storedCap {storedCap}, errors.truncated {truncated}, errors.total {total}; expected {FailVerdict}, ")
            + string.Create(CultureInfo.InvariantCulture, $"{ErrorGrouping.StoredDetailCap}, {ErrorGrouping.StoredDetailCap}, true and {trueTotal}."));
    }

    // The live count follows the true total: it never falls, it keeps rising after the cap, and it ends at the total.
    private static void AssertShownCountKeepsRising(ulong[] shownCounts, int truncationIndex, ulong trueTotal)
    {
        Assert.True(shownCounts.Length > 0, "The view model never showed an error count.");

        int firstDecrease = Enumerable.Range(1, shownCounts.Length - 1)
            .FirstOrDefault(i => shownCounts[i] < shownCounts[i - 1], -1);
        Assert.True(
            firstDecrease < 0,
            string.Create(CultureInfo.InvariantCulture, $"The shown error count fell from {(firstDecrease > 0 ? shownCounts[firstDecrease - 1] : 0)} to ")
            + string.Create(CultureInfo.InvariantCulture, $"{(firstDecrease > 0 ? shownCounts[firstDecrease] : 0)} at update {firstDecrease}."));

        Assert.True(
            truncationIndex >= 0,
            string.Create(CultureInfo.InvariantCulture, $"The detail cap was never reported while a count was shown ({shownCounts.Length} updates)."));
        ulong atTruncation = shownCounts[truncationIndex];
        ulong highestAfter = shownCounts.Skip(truncationIndex + 1).DefaultIfEmpty(atTruncation).Max();
        Assert.True(
            highestAfter > atTruncation,
            string.Create(CultureInfo.InvariantCulture, $"The shown count stayed at {atTruncation} after the detail cap was reached ")
            + string.Create(CultureInfo.InvariantCulture, $"(update {truncationIndex} of {shownCounts.Length}); it must keep rising with the true total."));

        Assert.True(
            shownCounts[^1] == trueTotal,
            string.Create(CultureInfo.InvariantCulture, $"The final shown error count is {shownCounts[^1]}, the true total {trueTotal}."));
    }

    // Once the batches stop carrying records, progress alone carries the rising total.
    private static void AssertProgressKeepsRisingPastCap(InstrumentedRun engineRun)
    {
        ulong[] totals = engineRun.Progress.Select(progress => progress.Value.TotalErrors).ToArray();
        int firstDecrease = Enumerable.Range(1, Math.Max(0, totals.Length - 1))
            .FirstOrDefault(i => totals[i] < totals[i - 1], -1);
        Assert.True(
            firstDecrease < 0,
            string.Create(CultureInfo.InvariantCulture, $"A progress total fell at tick {firstDecrease} of {totals.Length}."));

        ulong[] pastCap = totals.Where(total => total > (ulong)ErrorGrouping.StoredDetailCap).Distinct().ToArray();
        Assert.True(
            pastCap.Length >= 2 && pastCap[^1] > pastCap[0],
            string.Create(CultureInfo.InvariantCulture, $"Progress reported {pastCap.Length} distinct totals above the {ErrorGrouping.StoredDetailCap}-record cap ")
            + string.Create(CultureInfo.InvariantCulture, $"({Listed(pastCap.Select(total => total.ToString(CultureInfo.InvariantCulture)).ToArray())}); ")
            + "at least two, rising, are required.");
    }

    private void LogRun(FaultRun run)
    {
        int verified = run.VerifiedInjections.Count;
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Spec.Kind}: tested {run.Prepared.TestedBytes} of {run.RegionBytes} planned bytes, {run.Prepared.WorkerCount} workers "
            + $"({run.System.LogicalProcessors} logical processors, {run.System.ProcessorGroups} group(s)), "
            + $"ISA {run.Prepared.InstructionSet}, pages {run.Prepared.PageMode}, prepared in {run.Prepared.PrepareTime.TotalSeconds:0.00} s."));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Injections: {run.InjectionLog.Count} logged, {verified} with a verification opportunity, plan bound {run.ReportBound} "
            + $"({run.Spec.InjectedPassesPerStep} injecting passes per step), cap {run.Spec.MaxInjections}; "
            + $"records {run.Records.Count}; true total {run.Completed.TotalErrors}; end {run.Completed.EndReason}."));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Duration: test {run.Completed.Elapsed.TotalSeconds:0.0} s, wall {run.WallDuration.TotalSeconds:0.0} s, "
            + $"release {run.Released.ReleaseTime.TotalSeconds:0.00} s; verified bytes {run.Completed.VerifiedBytes}, "
            + $"independent {run.IndependentVerifiedBytes}."));

        foreach (var outcome in run.Completed.StepResults)
        {
            int index = outcome.Attributes.Index;
            TimeSpan oldestReported = run.Records
                .Where(record => record.StepIndex == index)
                .Select(record => record.TimeSinceWrite)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  step {index + 1} '{outcome.Attributes.Name}': {outcome.PassesCompleted} passes in "
                + $"{outcome.ActualDuration.TotalSeconds:0.00} s, {outcome.Errors} errors, "
                + $"{run.VerifiedInjections.Count(fault => fault.StepIndex == index)} verified injections, "
                + $"{outcome.VerifiedBytes} bytes verified, oldest reported data {oldestReported.TotalMilliseconds:0} ms."));
        }
    }

    private static string Describe(ErrorRecord record) => string.Create(
        CultureInfo.InvariantCulture,
        $"#{record.Sequence} offset 0x{record.RegionOffset:X} address 0x{record.Address:X16} step {record.StepIndex + 1} '{record.StepName}' pass {record.PassIndex + 1} "
        + $"{record.AccessType} mask 0x{record.BitMask:X16} {record.RereadOutcome} {record.TimeSinceWrite.TotalMilliseconds:0} ms since write");

    private static string Listed(IReadOnlyCollection<string> items) =>
        items.Count == 0
            ? "none."
            : string.Join("; ", items.Take(MaxListedItems))
                + (items.Count > MaxListedItems ? string.Create(CultureInfo.InvariantCulture, $"; … {items.Count - MaxListedItems} more.") : ".");
    /// <summary>
    /// The production controller graph, initialised, with its view model, over an instrumented fault-injecting engine
    /// whose plan is the scaled Standard plan and whose region is capped at the planned size.
    /// </summary>
    private sealed class ControllerHarness : IAsyncDisposable
    {
        private int _disposed;

        private ControllerHarness(
            TempDataRoot root,
            DispatcherThread ui,
            InstrumentedEngine engine,
            ControllerGraph.Graph graph,
            MainViewModel viewModel,
            ulong regionBytes)
        {
            Root = root;
            Ui = ui;
            Engine = engine;
            Graph = graph;
            ViewModel = viewModel;
            RegionBytes = regionBytes;
        }

        public TempDataRoot Root { get; }

        public DispatcherThread Ui { get; }

        public InstrumentedEngine Engine { get; }

        public ControllerGraph.Graph Graph { get; }

        public RunController Controller => Graph.Controller;

        public MainViewModel ViewModel { get; }

        public ulong RegionBytes { get; }

        public static async Task<ControllerHarness> CreateAsync(FaultSpec spec, ulong regionBytes)
        {
            var root = new TempDataRoot();
            DispatcherThread? ui = null;
            ControllerGraph.Graph? graph = null;
            try
            {
                ui = new DispatcherThread();
                var engine = new InstrumentedEngine(TestEngines.Create(spec));

                // The controller judges sufficiency on the uncapped plan, caps the region to whole worker slices and
                // lowers the engine's shrink floor to the capped size, so a scaled region stays a valid configuration.
                var options = new RunControllerOptions
                {
                    PlanProvider = _ => ScaledPlans.FaultInjectionStandard,
                    MaxRegionBytes = regionBytes,
                };

                graph = await ControllerGraph.CreateInitializedAsync(ui, engine, root.Data, options);
                ControllerGraph.Graph created = graph;
                MainViewModel viewModel = await ui.InvokeAsync(() => new MainViewModel(created.Controller, created.StateMachine));
                return new ControllerHarness(root, ui, engine, graph, viewModel, regionBytes);
            }
            catch
            {
                if (ui is not null && graph is not null)
                {
                    await ControllerGraph.ShutdownAndDisposeAsync(ui, graph);
                }

                ui?.Dispose();
                root.Dispose();
                throw;
            }
        }

        // Starts a Standard run, which the plan provider maps to the scaled plan, and waits for its verdict, the
        // release of its memory and the publication of its summaries.
        public async Task StartAndWaitAsync(bool stopOnError)
        {
            RunController controller = Controller;
            (StartResult start, string? notice) = await Ui.InvokeAsync(
                () => (controller.TryStart(TestPlans.Standard.Type, stopOnError), controller.NoticeMessage));
            Assert.True(
                start == StartResult.Accepted,
                string.Create(CultureInfo.InvariantCulture, $"TryStart returned {start} (notice '{notice}')."));

            await ControllerGraph.WaitForTerminalAndPublishedAsync(Ui, controller, RunTimeout(ScaledPlans.FaultInjectionStandard));
        }

        public (InstrumentedRun Run, EngineCompleted Completed) LastEngineRun()
        {
            InstrumentedRun? run = Engine.LastRun;
            Assert.True(run is not null, "The controller created no engine session.");
            EngineCompleted? completed = run.Completed;
            Assert.True(completed is not null, "The controller's engine session never raised Completed.");

            // The fault offsets were computed for the planned size; a shrunk region would move or drop the targets.
            ulong? tested = run.Prepared?.TestedBytes;
            Assert.True(
                tested == RegionBytes,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Precondition: the engine tested {tested} bytes instead of the capped {RegionBytes}; the fault offsets no longer describe the region."));
            return (run, completed);
        }

        public Task<ControllerView> ReadViewAsync()
        {
            RunController controller = Controller;
            MainViewModel viewModel = ViewModel;
            return Ui.InvokeAsync(() => new ControllerView(
                controller.StateMachine.State,
                controller.StateMachine.EndReason,
                controller.StateMachine.EndDetail,
                controller.ResultMessage,
                controller.TotalErrors,
                controller.IsErrorDetailTruncated,
                controller.FirstError,
                controller.CauseHints.Items.Count,
                viewModel.ErrorGroupsHeader,
                viewModel.HasCauseHints,
                viewModel.CauseHintsHeader,
                viewModel.CauseHints.Select(item => item.Text).ToArray()));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                MainViewModel viewModel = ViewModel;
                await Ui.InvokeAsync(viewModel.Dispose);
                await ControllerGraph.ShutdownAndDisposeAsync(Ui, Graph);
            }
            finally
            {
                Ui.Dispose();
                Root.Dispose();
            }
        }
    }

    /// <summary>What the controller and the view model show once a run is concluded, read on the dispatcher thread.</summary>
    private sealed record ControllerView(
        RunState State,
        RunEndReason? EndReason,
        string? EndDetail,
        string? ResultMessage,
        ulong TotalErrors,
        bool IsErrorDetailTruncated,
        ErrorRecord? FirstError,
        int CauseHintCount,
        string ErrorGroupsHeader,
        bool HasCauseHints,
        string CauseHintsHeader,
        IReadOnlyList<string> CauseHintTexts);

    /// <summary>The ground truth and engine evidence of one direct correctness run.</summary>
    private sealed record FaultRun(
        FaultSpec Spec,
        TestPlan Plan,
        SystemSnapshot System,
        ulong RegionBytes,
        EnginePrepared Prepared,
        EngineCompleted Completed,
        EngineReleased Released,
        IReadOnlyList<ErrorRecord> Records,
        IReadOnlyList<InjectedFault> InjectionLog,
        ulong IndependentVerifiedBytes,
        TimeSpan WallDuration)
    {
        /// <summary>
        /// Gets the injections whose verification ran: an opportunity is lost only to a stop or the end of the run, and
        /// such entries are excluded from matching.
        /// </summary>
        public IReadOnlyList<InjectedFault> VerifiedInjections { get; } =
            InjectionLog.Where(fault => fault.VerifiedOpportunity).ToArray();

        /// <summary>
        /// Gets the most injections the spec's per-step pass quota allows on the plan, fixed before the run.
        /// </summary>
        public long ReportBound { get; } = FaultInjectionEndToEndTests.ReportBound(Spec, Plan);
    }

    /// <summary>A started direct session with its recorder.</summary>
    private sealed record DirectSession(
        IEngineSession Session,
        EngineRunRecorder Recorder,
        SystemSnapshot System,
        ulong RegionBytes,
        long StartedAt);

    /// <summary>
    /// The matching key of the fault-injection criterion: the 8-byte word as a logical region offset and as the
    /// virtual address the corruption was stored through, the flipped bits, the zero-based step and the zero-based pass.
    /// </summary>
    /// <remarks>
    /// The record's address is compared unmasked: the engine reports the mismatching word's own address, so a record
    /// that points anywhere else, even inside the same word, is a wrong address.
    /// </remarks>
    private readonly record struct MatchKey(ulong WordOffset, ulong Address, ulong BitMask, int StepIndex, int PassIndex)
    {
        /// <summary>Gets the key without its address, which an address mismatch leaves equal.</summary>
        public MatchKey WithoutAddress => this with { Address = 0 };

        public static MatchKey Of(InjectedFault fault) =>
            new(fault.WordOffset, fault.Address, fault.BitMask, fault.StepIndex, fault.PassIndex);

        public static MatchKey Of(ErrorRecord record) =>
            new(record.RegionOffset & WordAlignmentMask, record.Address, record.BitMask, record.StepIndex, record.PassIndex);

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"word 0x{WordOffset:X} address 0x{Address:X16} mask 0x{BitMask:X16} step {StepIndex + 1} pass {PassIndex + 1}");
    }
}
