using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using RamQuick.App.Tests.Fakes;
using Xunit;

namespace RamQuick.Sensors.Tests;

/// <summary>
/// Tests for <see cref="SensorMonitor"/>: one dedicated background thread owns the source and reads it
/// once per simulated second, a tick during an in-flight read is skipped, a reading older than 3 s is
/// re-marked <see cref="SensorStatus.Stale"/> at its own deadline, an open or read outstanding for 5 s
/// marks the source <see cref="SensorStatus.Hung"/> for good, each sensor's availability stays
/// independent, a context that rejects posts never stops polling, and <see cref="SensorMonitor.Dispose"/>
/// returns within 1 s and closes the source only on the sensor thread.
/// </summary>
/// <remarks>
/// Polling and every age run on a <see cref="FakeTimeProvider"/>, whose <c>Advance</c> runs the monitor's
/// timer callback on the test thread. Snapshots are posted to a queued
/// <see cref="ImmediateSynchronizationContext"/> that the test thread drains, so it plays the UI thread.
/// Real time is spent only waiting for work already due on the sensor thread, each wait bounded by a
/// safety timeout rather than a sleep.
/// </remarks>
public sealed class SensorMonitorTests
{
    private static readonly TimeSpan _safetyTimeout = TimeSpan.FromSeconds(5);

    // Margin over DisposeTimeout so a heavily loaded host does not fail a correct monitor.
    private static readonly TimeSpan _disposeBound = SensorMonitor.DisposeTimeout + TimeSpan.FromSeconds(1);

    // The timer callback runs inside Advance on the UI-like thread, so it must never wait on a read.
    private static readonly TimeSpan _advanceBound = TimeSpan.FromMilliseconds(500);

    private static readonly int _ticksUntilHung = (int)(SensorMonitor.HungAfter / SensorMonitor.PollInterval);

    [Fact]
    public void Constants_MatchAap()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SensorMonitor.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), SensorMonitor.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(5), SensorMonitor.HungAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), SensorMonitor.DisposeTimeout);
    }

    [Fact]
    public void Constructor_NullArgument_ThrowsArgumentNullException()
    {
        using var source = new FakeSensorSource();

        Assert.Throws<ArgumentNullException>("source", () => new SensorMonitor(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>("timeProvider", () => new SensorMonitor(source, null!));
    }

    [Fact]
    public void Current_UntilFirstReadCompletes_IsStaleForBoth()
    {
        using var h = new Harness();
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);
        Assert.False(h.Monitor.IsHung);

        h.Source.BlockReads();
        h.Start(expectFirstRead: false);
        Assert.True(
            h.Source.WaitForReadCount(1, _safetyTimeout),
            $"The first read did not start within {_safetyTimeout}; reads entered: {h.Source.ReadCount}.");

        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);
        Assert.Null(ThermalLimits.Evaluate(h.Monitor.Current));

        h.ReleaseReadsAndWait();

        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(h.Monitor.Current.Ram, FakeSensorSource.DefaultRamCelsius);
    }

    [Fact]
    public void Start_OpensSourceOnceOnDedicatedBackgroundThread()
    {
        using var h = new Harness(decorate: inner => new InstrumentedSource(inner));
        h.Start();
        h.Tick(3);

        Assert.Equal(1, h.Source.OpenCount);
        int sensorThreadId = Assert.NotNull(h.Source.OpenThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, sensorThreadId);
        Assert.Equal(sensorThreadId, h.Source.LastReadThreadId);

        var recorder = Assert.IsType<InstrumentedSource>(h.MonitoredSource);
        Assert.True(recorder.OpenedOnBackgroundThread, "The source was opened on a foreground thread, which a hung read would keep alive.");
        Assert.False(recorder.OpenedOnThreadPoolThread, "The source was opened on a thread-pool thread rather than a dedicated one.");
    }

    [Fact]
    public void Start_Twice_ThrowsInvalidOperationExceptionAndKeepsOneOpen()
    {
        using var h = new Harness();
        h.Start();

        Assert.Throws<InvalidOperationException>(() => h.Monitor.Start(h.Context));

        h.Tick();
        Assert.Equal(1, h.Source.OpenCount);
    }

    [Fact]
    public void Start_NullContext_ThrowsArgumentNullExceptionAndStartsNothing()
    {
        using var h = new Harness();

        Assert.Throws<ArgumentNullException>("context", () => h.Monitor.Start(null!));
        Assert.Equal(0, h.Source.OpenCount);

        h.Start();
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
    }

    [Fact]
    public void Start_AfterDispose_ThrowsObjectDisposedException()
    {
        using var h = new Harness();
        h.Monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => h.Monitor.Start(h.Context));
        Assert.Equal(0, h.Source.OpenCount);
    }

    [Fact]
    public void Tick_EachSimulatedSecond_RaisesReadingsChangedOnCapturedContext()
    {
        using var h = new Harness();
        h.Start();
        int readsBefore = h.Source.ReadCount;
        int eventsBefore = h.EventCount;

        h.Tick(5);

        Assert.True(h.EventCount - eventsBefore >= 5, $"Expected at least 5 events in 5 simulated seconds; observed {h.EventCount - eventsBefore}.");
        Assert.Equal(readsBefore + 5, h.Source.ReadCount);
        Assert.All(h.Events, received =>
        {
            Assert.Same(h.Monitor, received.Sender);
            Assert.Same(h.Context, received.Context);
            Assert.Equal(Environment.CurrentManagedThreadId, received.ThreadId);
        });

        SensorReadings current = h.Monitor.Current;
        AssertAvailable(current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(current.Ram, FakeSensorSource.DefaultRamCelsius);
        Assert.Equal(h.Time.GetUtcNow(), current.Cpu.CapturedAt);
        Assert.Same(current, h.LastReadings);
    }

    [Fact]
    public void Tick_ValuesChange_PublishesNewValues()
    {
        using var h = new Harness();
        h.Start();

        h.Source.SetCpu(71.5);
        h.Source.SetRam(48.0);
        h.Tick();

        SensorReadings published = Assert.IsType<SensorReadings>(h.LastReadings);
        AssertAvailable(published.Cpu, 71.5);
        AssertAvailable(published.Ram, 48.0);
        Assert.Same(published, h.Monitor.Current);
    }

    [Fact]
    public void Tick_DuringSlowRead_IsSkippedAndNeverOverlaps()
    {
        using var h = new Harness();
        h.Start();
        int readsEntered = h.BlockNextRead();
        Assert.Equal(1, h.Source.CurrentConcurrentReads);

        // Two more ticks while the read is in flight: 2 s outstanding, below HungAfter.
        h.Tick(2, expectRead: false);

        Assert.Equal(readsEntered, h.Source.ReadCount);
        Assert.Equal(1, h.Source.MaxConcurrentReads);
        Assert.False(h.Monitor.IsHung);

        h.ReleaseReadsAndWait();
        Assert.Equal(readsEntered, h.Source.ReadCount);

        for (int tick = 1; tick <= 3; tick++)
        {
            h.Tick();
            Assert.Equal(readsEntered + tick, h.Source.ReadCount);
        }

        Assert.Equal(1, h.Source.MaxConcurrentReads);
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
    }

    // Readings at both limits, so a reading that stays fresh trips and one re-marked Stale does not.
    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    [InlineData(3000)]
    public void Current_ReadingNoOlderThanStaleAfter_StaysAvailable(int ageMilliseconds)
    {
        using var h = new Harness();
        ScriptReadingsAtLimits(h.Source, TimeSpan.FromMilliseconds(ageMilliseconds));
        h.Start();
        h.Tick();

        SensorReadings current = h.Monitor.Current;
        AssertAvailable(current.Cpu, ThermalLimits.CpuLimitCelsius);
        AssertAvailable(current.Ram, ThermalLimits.RamLimitCelsius);
        Assert.Equal(
            new ThermalVerdict(ThermalSensor.Cpu, ThermalLimits.CpuLimitCelsius, ThermalLimits.CpuLimitCelsius),
            ThermalLimits.Evaluate(current));
    }

    [Theory]
    [InlineData(3001)]
    [InlineData(4000)]
    public void Current_ReadingOlderThanStaleAfter_IsStaleAndNeverTrips(int ageMilliseconds)
    {
        TimeSpan age = TimeSpan.FromMilliseconds(ageMilliseconds);
        using var h = new Harness();
        ScriptReadingsAtLimits(h.Source, age);
        h.Start();
        h.Tick();

        SensorReadings current = h.Monitor.Current;
        AssertBothUnavailable(current, SensorStatus.Stale);
        Assert.Equal(h.Time.GetUtcNow() - age, current.Cpu.CapturedAt);
        Assert.Null(ThermalLimits.Evaluate(current));
        Assert.Same(current, h.LastReadings);
        Assert.False(h.Monitor.IsHung);
    }

    [Fact]
    public void Current_NoFreshReadForMoreThanStaleAfter_BecomesStaleThenRecovers()
    {
        using var h = new Harness();
        h.Start();
        DateTimeOffset freshAt = h.Monitor.Current.Cpu.CapturedAt;
        int readsEntered = h.BlockNextRead();

        // 3 s after the last fresh reading: exactly StaleAfter old, still available.
        h.Tick(2, expectRead: false);
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(h.Monitor.Current.Ram, FakeSensorSource.DefaultRamCelsius);

        // 4 s after it, with the read outstanding 3 s: stale, not yet hung.
        h.Tick(expectRead: false);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);
        Assert.Equal(freshAt, h.Monitor.Current.Cpu.CapturedAt);
        Assert.False(h.Monitor.IsHung);
        h.PumpUntil(() => h.LastReadings?.Cpu.Status == SensorStatus.Stale, "Expected a Stale snapshot to be raised");
        AssertBothUnavailable(Assert.IsType<SensorReadings>(h.LastReadings), SensorStatus.Stale);

        h.ReleaseReadsAndWait();
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        Assert.Equal(h.Time.GetUtcNow(), h.Monitor.Current.Cpu.CapturedAt);

        h.Tick();
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(h.Monitor.Current.Ram, FakeSensorSource.DefaultRamCelsius);
        Assert.Equal(readsEntered + 1, h.Source.ReadCount);
        Assert.False(h.Monitor.IsHung);
    }

    // A reading captured 200 ms after its tick, as a 200 ms read stamps it, passes StaleAfter half a
    // second before a tick: it must turn Stale then, not at the tick after.
    [Fact]
    public void Current_ReadingPassingStaleAfterBetweenTicks_IsStaleAtOnceAndNeverTrips()
    {
        const double CpuCelsius = 96.0;
        using var h = new Harness();
        h.Source.CapturedAtOffset = TimeSpan.FromMilliseconds(200);
        h.Source.SetCpu(CpuCelsius);
        h.Start();
        DateTimeOffset capturedAt = h.Monitor.Current.Cpu.CapturedAt;
        AssertAvailable(h.Monitor.Current.Cpu, CpuCelsius);

        // The next read blocks 1 s after start; at 3 s the reading is 2.8 s old and still trips its limit.
        h.BlockNextRead();
        h.Tick(2, expectRead: false);
        AssertAvailable(h.Monitor.Current.Cpu, CpuCelsius);
        Assert.Equal(
            new ThermalVerdict(ThermalSensor.Cpu, CpuCelsius, ThermalLimits.CpuLimitCelsius),
            ThermalLimits.Evaluate(h.Monitor.Current));

        // At 3.5 s the reading is 3.3 s old, half a second before the next tick.
        h.Time.Advance(TimeSpan.FromMilliseconds(500));

        SensorReadings current = h.Monitor.Current;
        AssertBothUnavailable(current, SensorStatus.Stale);
        Assert.Equal(capturedAt, current.Cpu.CapturedAt);
        Assert.Null(ThermalLimits.Evaluate(current));
        Assert.False(h.Monitor.IsHung);
        h.PumpUntil(
            () => h.LastReadings is { Cpu.Status: SensorStatus.Stale, Ram.Status: SensorStatus.Stale },
            "Expected the Stale snapshot to be raised before the next tick");
        Assert.Equal(capturedAt, Assert.IsType<SensorReadings>(h.LastReadings).Cpu.CapturedAt);
    }

    // The boundary on both paths that apply it: the deadline timer when the clock advances and due
    // timers run, and Current itself when the clock moves without running any timer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Current_ExactlyStaleAfterOld_IsAvailableAndOneTickOlder_IsStale(bool timersRun)
    {
        using var h = new Harness();
        h.Source.CapturedAtOffset = TimeSpan.FromMilliseconds(200);
        h.Start();
        DateTimeOffset capturedAt = h.Monitor.Current.Cpu.CapturedAt;
        h.BlockNextRead();

        MoveClockTo(h.Time, capturedAt + SensorMonitor.StaleAfter, timersRun);
        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(h.Monitor.Current.Ram, FakeSensorSource.DefaultRamCelsius);
        h.Context.RunPending();
        AssertAvailable(Assert.IsType<SensorReadings>(h.LastReadings).Cpu, FakeSensorSource.DefaultCpuCelsius);

        MoveClockTo(h.Time, capturedAt + SensorMonitor.StaleAfter + TimeSpan.FromTicks(1), timersRun);
        SensorReadings current = h.Monitor.Current;
        AssertBothUnavailable(current, SensorStatus.Stale);
        Assert.Equal(capturedAt, current.Cpu.CapturedAt);
        Assert.Null(ThermalLimits.Evaluate(current));
        Assert.False(h.Monitor.IsHung);

        if (timersRun)
        {
            // The deadline timer has a real timer's whole-millisecond resolution, so the re-marked
            // snapshot is raised within 1 ms of the deadline.
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.PumpUntil(
                () => h.LastReadings is { Cpu.Status: SensorStatus.Stale, Ram.Status: SensorStatus.Stale },
                "Expected the Stale snapshot to be raised within 1 ms of the deadline");
        }
        else
        {
            // No timer ran, so nothing was raised: Current applied the deadline on its own.
            h.Context.RunPending();
            AssertAvailable(Assert.IsType<SensorReadings>(h.LastReadings).Cpu, FakeSensorSource.DefaultCpuCelsius);
        }
    }

    // One reading too old and its peer fresh at its limit, in both directions: only the old reading
    // turns Stale, so the fresh one keeps its own thermal limit active.
    [Theory]
    [InlineData(ThermalSensor.Cpu, 3001)]
    [InlineData(ThermalSensor.Cpu, 4000)]
    [InlineData(ThermalSensor.Ram, 3001)]
    [InlineData(ThermalSensor.Ram, 4000)]
    public void Current_OneReadingOlderThanStaleAfter_OnlyItIsStaleAndItsFreshPeerTrips(ThermalSensor old, int ageMilliseconds)
    {
        var age = TimeSpan.FromMilliseconds(ageMilliseconds);
        ThermalSensor fresh = PeerOf(old);
        double limit = LimitFor(fresh);
        using Harness h = old == ThermalSensor.Cpu
            ? CreateHarnessAtLimits(cpuAge: age, ramAge: TimeSpan.Zero)
            : CreateHarnessAtLimits(cpuAge: TimeSpan.Zero, ramAge: age);
        h.Start();
        h.Tick();

        SensorReadings current = h.Monitor.Current;
        AssertUnavailable(ValueOf(current, old), SensorStatus.Stale);
        Assert.Equal(h.Time.GetUtcNow() - age, ValueOf(current, old).CapturedAt);
        AssertAvailable(ValueOf(current, fresh), limit);
        Assert.Equal(h.Time.GetUtcNow(), ValueOf(current, fresh).CapturedAt);
        Assert.Equal(new ThermalVerdict(fresh, limit, limit), ThermalLimits.Evaluate(current));
        Assert.Same(current, h.LastReadings);
        Assert.False(h.Monitor.IsHung);
    }

    // Readings at both limits captured a second apart, with no fresh read arriving: each turns Stale at
    // its own deadline, on both paths that apply one, and the younger trips until its own deadline.
    // The ages put both deadlines between poll ticks, and each move crosses a tick only while nothing
    // is due, so only the deadline timer, or Current itself, can apply a deadline.
    [Theory]
    [InlineData(ThermalSensor.Cpu, true)]
    [InlineData(ThermalSensor.Cpu, false)]
    [InlineData(ThermalSensor.Ram, true)]
    [InlineData(ThermalSensor.Ram, false)]
    public void Current_ReadingsCapturedASecondApart_EachTurnsStaleAtItsOwnDeadline(ThermalSensor older, bool timersRun)
    {
        var olderAge = TimeSpan.FromMilliseconds(1500);
        var youngerAge = TimeSpan.FromMilliseconds(500);
        ThermalSensor younger = PeerOf(older);
        double youngerLimit = LimitFor(younger);
        var youngerVerdict = new ThermalVerdict(younger, youngerLimit, youngerLimit);
        using Harness h = older == ThermalSensor.Cpu
            ? CreateHarnessAtLimits(cpuAge: olderAge, ramAge: youngerAge)
            : CreateHarnessAtLimits(cpuAge: youngerAge, ramAge: olderAge);
        h.Start();
        DateTimeOffset olderCapturedAt = ValueOf(h.Monitor.Current, older).CapturedAt;
        DateTimeOffset youngerCapturedAt = ValueOf(h.Monitor.Current, younger).CapturedAt;
        Assert.Equal(olderAge - youngerAge, youngerCapturedAt - olderCapturedAt);

        // The next read stays blocked; the last move below ends about 1.5 s after it started, short of HungAfter.
        h.BlockNextRead();
        h.Context.RunPending();
        int eventsWhileBlocked = h.EventCount;

        MoveClockTo(h.Time, olderCapturedAt + SensorMonitor.StaleAfter, timersRun);
        AssertAvailable(ValueOf(h.Monitor.Current, older), LimitFor(older));

        MoveClockTo(h.Time, olderCapturedAt + SensorMonitor.StaleAfter + TimeSpan.FromTicks(1), timersRun);
        SensorReadings current = h.Monitor.Current;
        AssertUnavailable(ValueOf(current, older), SensorStatus.Stale);
        Assert.Equal(olderCapturedAt, ValueOf(current, older).CapturedAt);
        AssertAvailable(ValueOf(current, younger), youngerLimit);
        Assert.Equal(youngerVerdict, ThermalLimits.Evaluate(current));

        if (timersRun)
        {
            // Within the deadline timer's whole-millisecond resolution, the older reading's deadline
            // raises a snapshot that re-marks that reading alone.
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.PumpUntil(
                () => h.LastReadings is { } readings && ValueOf(readings, older).Status == SensorStatus.Stale,
                $"Expected the snapshot re-marking the {older} reading Stale to be raised within 1 ms of its deadline");
            SensorReadings raised = Assert.IsType<SensorReadings>(h.LastReadings);
            AssertAvailable(ValueOf(raised, younger), youngerLimit);
            Assert.Equal(youngerVerdict, ThermalLimits.Evaluate(raised));
        }

        // Exactly StaleAfter old, the younger reading is still fresh, a second after its peer turned Stale.
        MoveClockTo(h.Time, youngerCapturedAt + SensorMonitor.StaleAfter, timersRun);
        AssertAvailable(ValueOf(h.Monitor.Current, younger), youngerLimit);

        MoveClockTo(h.Time, youngerCapturedAt + SensorMonitor.StaleAfter + TimeSpan.FromTicks(1), timersRun);
        current = h.Monitor.Current;
        AssertBothUnavailable(current, SensorStatus.Stale);
        Assert.Equal(olderCapturedAt, ValueOf(current, older).CapturedAt);
        Assert.Equal(youngerCapturedAt, ValueOf(current, younger).CapturedAt);
        Assert.Null(ThermalLimits.Evaluate(current));

        if (timersRun)
        {
            // The younger reading's own deadline timer, armed once its peer turned Stale, raises the last re-marking.
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.PumpUntil(
                () => h.LastReadings is { Cpu.Status: SensorStatus.Stale, Ram.Status: SensorStatus.Stale },
                $"Expected the snapshot re-marking the {younger} reading Stale to be raised within 1 ms of its own deadline");
            Assert.Null(ThermalLimits.Evaluate(Assert.IsType<SensorReadings>(h.LastReadings)));
        }
        else
        {
            // No timer ran, so nothing was raised: Current applied each deadline on its own.
            h.Context.RunPending();
            Assert.Equal(eventsWhileBlocked, h.EventCount);
        }

        Assert.False(h.Monitor.IsHung);
    }

    [Fact]
    public void Read_NotReturningWithinHungAfter_MarksSourceHungPermanently()
    {
        using var h = new Harness();
        h.Start();
        int readsEntered = h.BlockNextRead();

        TimeSpan slowestAdvance = h.Tick(_ticksUntilHung - 1, expectRead: false);
        Assert.False(h.Monitor.IsHung, $"The source was marked hung with its read outstanding less than {SensorMonitor.HungAfter}.");

        slowestAdvance = Max(slowestAdvance, h.Tick(expectRead: false));
        Assert.True(h.Monitor.IsHung, $"The source was not marked hung with its read outstanding {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);
        h.PumpUntil(
            () => h.LastReadings is { Cpu.Status: SensorStatus.Hung, Ram.Status: SensorStatus.Hung },
            "Expected the Hung snapshot to be raised");

        // The UI-like thread stays live while the read stays blocked.
        bool probeRan = false;
        h.Context.Post(_ => probeRan = true, null);
        h.Context.RunPending();
        Assert.True(probeRan, "A post to the context was not serviced while the read was hung.");

        slowestAdvance = Max(slowestAdvance, h.Tick(10, expectRead: false));
        Assert.True(
            slowestAdvance < _advanceBound,
            $"The slowest Advance took {slowestAdvance.TotalMilliseconds:F0} ms; the timer callback must never wait on a read.");
        Assert.Equal(readsEntered, h.Source.ReadCount);

        int eventsWhenHung = h.EventCount;
        h.Source.ReleaseReads();
        Assert.True(
            SpinWait.SpinUntil(() => h.Source.CompletedReadCount == readsEntered, _safetyTimeout),
            $"The released read did not return within {_safetyTimeout}; completed reads: {h.Source.CompletedReadCount}.");
        h.Tick(3, expectRead: false);
        h.Context.RunPending();

        Assert.Equal(readsEntered, h.Source.ReadCount);
        Assert.Equal(1, h.Source.MaxConcurrentReads);
        Assert.True(h.Monitor.IsHung);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);
        Assert.Equal(eventsWhenHung, h.EventCount);
        Assert.Equal(0, h.Source.DisposeCount);
    }

    [Fact]
    public void Read_FirstReadNotReturning_MarksSourceHungAndNeverReadsAgain()
    {
        using var h = new Harness();
        h.Source.BlockReads();
        h.Start(expectFirstRead: false);
        Assert.True(
            h.Source.WaitForReadCount(1, _safetyTimeout),
            $"The first read did not start within {_safetyTimeout}; reads entered: {h.Source.ReadCount}.");

        h.Tick(_ticksUntilHung - 1, expectRead: false);
        Assert.False(h.Monitor.IsHung);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);

        h.Tick(expectRead: false);
        Assert.True(h.Monitor.IsHung, $"The first read was outstanding {SensorMonitor.HungAfter} without the source being marked hung.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);

        h.Source.ReleaseReads();
        Assert.True(
            SpinWait.SpinUntil(() => h.Source.CompletedReadCount == 1, _safetyTimeout),
            $"The released first read did not return within {_safetyTimeout}.");
        h.Tick(3, expectRead: false);
        h.Context.RunPending();

        Assert.Equal(1, h.Source.ReadCount);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);
        Assert.Equal(0, h.Source.DisposeCount);
    }

    // A read that starts between ticks, here the first read after a slow open, is marked hung
    // HungAfter after it started, not at the first tick after that.
    [Fact]
    public void Read_StartedBetweenTicks_IsMarkedHungExactlyHungAfterLater()
    {
        using var h = new Harness(decorate: inner => new InstrumentedSource(inner, holdOpen: true));
        h.Source.BlockReads();
        InstrumentedSource instrumented = h.StartWithOpenHeld();
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        instrumented.ReleaseOpen();
        Assert.True(
            h.Source.WaitForReadCount(1, _safetyTimeout),
            $"The first read did not start within {_safetyTimeout}; reads entered: {h.Source.ReadCount}.");
        DateTimeOffset readStartedAt = h.Time.GetUtcNow();

        h.Time.Advance(SensorMonitor.HungAfter - TimeSpan.FromTicks(1));
        Assert.False(h.Monitor.IsHung, $"The source was marked hung with its read outstanding less than {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);

        h.Time.Advance(TimeSpan.FromTicks(1));
        Assert.True(h.Monitor.IsHung, $"The source was not marked hung with its read outstanding {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);
        Assert.Equal(readStartedAt + SensorMonitor.HungAfter, h.Monitor.Current.Cpu.CapturedAt);
        h.PumpUntil(
            () => h.LastReadings is { Cpu.Status: SensorStatus.Hung, Ram.Status: SensorStatus.Hung },
            "Expected the Hung snapshot to be raised");
        Assert.Equal(1, h.Source.ReadCount);
    }

    // A sensor-library open that never returns, as a wedged driver's would. The watchdog covers it as
    // it covers a read, and a late return or throw, before or after Dispose, changes nothing.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Open_NotReturningWithinHungAfter_MarksSourceHungAndNeverCallsItAgain(bool openThrows, bool disposeWhileOpenHeld)
    {
        using var h = new Harness(decorate: inner => new InstrumentedSource(inner, holdOpen: true));
        if (openThrows)
        {
            h.Source.OpenException = new InvalidOperationException("Simulated driver open failure.");
        }

        InstrumentedSource instrumented = h.StartWithOpenHeld();

        h.Tick(_ticksUntilHung - 1, expectRead: false);
        Assert.False(h.Monitor.IsHung, $"The source was marked hung with Open outstanding less than {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Stale);

        h.Tick(expectRead: false);
        Assert.True(h.Monitor.IsHung, $"The source was not marked hung with Open outstanding {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);
        h.PumpUntil(
            () => h.LastReadings is { Cpu.Status: SensorStatus.Hung, Ram.Status: SensorStatus.Hung },
            "Expected the Hung snapshot to be raised");
        int postsWhenHung = h.Context.PostCount;
        int eventsWhenHung = h.EventCount;

        if (disposeWhileOpenHeld)
        {
            AssertDisposeWithinBound(h.Monitor, "with Open hung");
        }

        // Open returns, or throws, late: the sensor thread must leave without another call or post.
        instrumented.ReleaseOpen();
        Thread sensorThread = Assert.IsType<Thread>(instrumented.OpenThread);
        Assert.True(
            sensorThread.Join(_safetyTimeout),
            $"The sensor thread did not exit within {_safetyTimeout} of the hung Open returning.");
        h.Tick(3, expectRead: false);
        h.Context.RunPending();

        Assert.Equal(1, h.Source.OpenCount);
        Assert.Equal(0, h.Source.ReadCount);
        Assert.Equal(0, h.Source.DisposeCount);
        Assert.Equal(postsWhenHung, h.Context.PostCount);
        Assert.Equal(eventsWhenHung, h.EventCount);
        Assert.True(h.Monitor.IsHung);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Hung);

        if (!disposeWhileOpenHeld)
        {
            AssertDisposeWithinBound(h.Monitor, "after the hung Open returned");
        }

        Assert.Equal(0, h.Source.DisposeCount);
    }

    [Fact]
    public void ReadingsChanged_OlderSnapshotArrivingAfterNewerOne_IsDropped()
    {
        var time = new FakeTimeProvider();
        using var source = new FakeSensorSource(time);
        var context = new CapturingSynchronizationContext();
        using var monitor = new SensorMonitor(source, time);
        var raised = new List<SensorReadings>();
        monitor.ReadingsChanged += (_, readings) => raised.Add(readings);

        monitor.Start(context);
        Assert.True(context.WaitForPosts(1, _safetyTimeout), $"The first read was not posted within {_safetyTimeout}.");
        source.SetCpu(71.5);
        time.Advance(SensorMonitor.PollInterval);
        Assert.True(context.WaitForPosts(2, _safetyTimeout), $"The second read was not posted within {_safetyTimeout}.");

        context.Run(1);
        context.Run(0);

        SensorReadings delivered = Assert.Single(raised);
        AssertAvailable(delivered.Cpu, 71.5);
        Assert.Same(monitor.Current, delivered);
    }

    [Fact]
    public void Dispose_WhileReadHung_ReturnsWithinDisposeTimeoutWithoutClosingSource()
    {
        using var h = new Harness();
        h.StartAndHang();

        TimeSpan took = Measure(h.Monitor.Dispose);

        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms with the source hung.");
        Assert.Equal(0, h.Source.DisposeCount);
        Assert.Equal(1, h.Source.CurrentConcurrentReads);
    }

    [Fact]
    public void Dispose_WhileReadBlockedButNotHung_ReturnsWithinDisposeTimeout()
    {
        using var h = new Harness();
        h.Start();
        h.BlockNextRead();
        h.Tick(expectRead: false);
        Assert.False(h.Monitor.IsHung);
        h.Context.RunPending();
        int eventsBefore = h.EventCount;

        TimeSpan took = Measure(h.Monitor.Dispose);

        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms with a read blocked.");
        Assert.Equal(0, h.Source.DisposeCount);

        // The read completes in the background, then the sensor thread closes the source.
        h.Source.ReleaseReads();
        Assert.True(
            SpinWait.SpinUntil(() => h.Source.DisposeCount == 1, _safetyTimeout),
            $"The sensor thread did not close the source within {_safetyTimeout} of the read returning; DisposeCount: {h.Source.DisposeCount}.");
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);
        h.Context.RunPending();
        Assert.Equal(eventsBefore, h.EventCount);
    }

    [Fact]
    public void Dispose_Healthy_ClosesSourceOnSensorThread()
    {
        using var h = new Harness();
        h.Start();
        h.Tick(2);
        int readsBefore = h.Source.ReadCount;
        int postsBefore = h.Context.PostCount;
        int eventsBefore = h.EventCount;

        TimeSpan took = Measure(h.Monitor.Dispose);

        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms.");
        Assert.Equal(1, h.Source.DisposeCount);
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, h.Source.DisposeThreadId);

        h.Tick(3, expectRead: false);
        h.Context.RunPending();
        Assert.Equal(readsBefore, h.Source.ReadCount);
        Assert.Equal(postsBefore, h.Context.PostCount);
        Assert.Equal(eventsBefore, h.EventCount);

        h.Monitor.Dispose();
        Assert.Equal(1, h.Source.DisposeCount);
    }

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrowOrCloseUnopenedSource()
    {
        using var h = new Harness();

        h.Monitor.Dispose();
        h.Monitor.Dispose();

        Assert.Equal(0, h.Source.OpenCount);
        Assert.Equal(0, h.Source.DisposeCount);
    }

    [Fact]
    public void Dispose_WithSnapshotStillQueued_RaisesNothingAfterwards()
    {
        using var h = new Harness();
        h.Start();
        int eventsBefore = h.EventCount;

        h.Tick(expectRead: false);
        Assert.True(h.Context.WaitForPending(_safetyTimeout), $"The read's snapshot was not posted within {_safetyTimeout}.");
        h.Monitor.Dispose();

        Assert.True(h.Context.RunPending() >= 1);
        Assert.Equal(eventsBefore, h.EventCount);
    }

    [Fact]
    public void Dispose_FromHandlerOnInlineContext_ReturnsWithoutWaitingOnItsOwnThread()
    {
        // Inline posts run the handler on the sensor thread itself, which Dispose must not join.
        using var h = new Harness(queuePosts: false);
        long disposeTicks = -1;
        int handlerThreadId = 0;
        h.Monitor.ReadingsChanged += (_, _) =>
        {
            // Runs on the sensor thread, where an exception would end the process: it only records.
            long startedAt = Stopwatch.GetTimestamp();
            h.Monitor.Dispose();
            Interlocked.Exchange(ref disposeTicks, Stopwatch.GetElapsedTime(startedAt).Ticks);
            Volatile.Write(ref handlerThreadId, Environment.CurrentManagedThreadId);
        };

        h.Start();
        Assert.True(
            SpinWait.SpinUntil(() => h.Source.DisposeCount == 1, _safetyTimeout),
            $"The sensor thread did not close the source within {_safetyTimeout}; DisposeCount: {h.Source.DisposeCount}.");

        TimeSpan took = TimeSpan.FromTicks(Interlocked.Read(ref disposeTicks));
        Assert.True(took >= TimeSpan.Zero, "The handler did not record its Dispose call.");
        Assert.True(took < SensorMonitor.DisposeTimeout, $"Dispose from the sensor thread took {took.TotalMilliseconds:F0} ms; it must not wait on itself.");
        Assert.Equal(h.Source.OpenThreadId, Volatile.Read(ref handlerThreadId));
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);

        int readsAtDispose = h.Source.ReadCount;
        h.Tick(2, expectRead: false);
        Assert.Equal(readsAtDispose, h.Source.ReadCount);
    }

    // A slow sensor-library open, interrupted by the window closing right after launch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_WhileOpenInProgress_ReturnsWithinDisposeTimeoutAndClosesSourceWithoutReading(bool openThrows)
    {
        using var h = new Harness(decorate: inner => new InstrumentedSource(inner, holdOpen: true));
        var instrumented = Assert.IsType<InstrumentedSource>(h.MonitoredSource);
        if (openThrows)
        {
            h.Source.OpenException = new InvalidOperationException("Simulated driver open failure.");
        }

        h.Monitor.Start(h.Context);
        Assert.True(instrumented.WaitForOpenEntered(_safetyTimeout), $"The sensor thread did not begin opening the source within {_safetyTimeout}.");

        TimeSpan took = Measure(h.Monitor.Dispose);
        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms with Open in progress.");

        instrumented.ReleaseOpen();
        Assert.True(
            SpinWait.SpinUntil(() => h.Source.DisposeCount == 1, _safetyTimeout),
            $"The sensor thread did not close the source within {_safetyTimeout} of Open returning; DisposeCount: {h.Source.DisposeCount}.");
        Assert.Equal(1, h.Source.OpenCount);
        Assert.Equal(0, h.Source.ReadCount);
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);
        Assert.Equal(0, h.Context.PostCount);
    }

    [Fact]
    public void Dispose_SourceDisposeThrows_ExceptionStaysContainedOnSensorThread()
    {
        using var h = new Harness(
            decorate: inner => new InstrumentedSource(inner, disposeException: new InvalidOperationException("Simulated driver close failure.")));
        h.Start();
        h.Tick();

        TimeSpan took = Measure(h.Monitor.Dispose);

        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms.");
        Assert.Equal(1, h.Source.DisposeCount);
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);
    }

    [Fact]
    public void Read_Throws_ReportsFailedAndKeepsPolling()
    {
        using var h = new Harness();
        h.Start();
        int readsBefore = h.Source.ReadCount;

        h.Source.ReadException = new InvalidOperationException("Simulated sensor read failure.");
        h.Tick(2);

        SensorReadings current = h.Monitor.Current;
        AssertBothUnavailable(current, SensorStatus.Failed);
        Assert.Equal(h.Time.GetUtcNow(), current.Cpu.CapturedAt);
        Assert.Null(ThermalLimits.Evaluate(current));
        Assert.False(h.Monitor.IsHung);
        Assert.Equal(readsBefore + 2, h.Source.ReadCount);

        h.Source.ReadException = null;
        h.Tick();

        AssertAvailable(h.Monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);
        AssertAvailable(h.Monitor.Current.Ram, FakeSensorSource.DefaultRamCelsius);
    }

    [Fact]
    public void Open_Throws_ReportsFailedAndPerformsNoReads()
    {
        using var h = new Harness();
        h.Source.OpenException = new InvalidOperationException("Simulated driver open failure.");

        h.Start(expectFirstRead: false);
        h.WaitForEvents(1);

        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Failed);
        Assert.False(h.Monitor.IsHung);

        // Failed values carry no temperature, so they are never re-marked Stale either.
        h.Tick(4, expectRead: false);
        h.Context.RunPending();
        Assert.Equal(0, h.Source.ReadCount);
        AssertBothUnavailable(h.Monitor.Current, SensorStatus.Failed);

        // The source contract lets Dispose follow an Open that threw, to release a partial open.
        TimeSpan took = Measure(h.Monitor.Dispose);
        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms after a failed open.");
        Assert.Equal(1, h.Source.DisposeCount);
        Assert.Equal(h.Source.OpenThreadId, h.Source.DisposeThreadId);
    }

    // The sensor thread posts every read; the timer callbacks, which FakeTimeProvider runs inside
    // Advance on the test thread, post the Stale and Hung markings. A rejected post must cost only
    // its notification on either thread: an exception escaping the sensor thread would end the process.
    [Fact]
    public void Post_ContextRejectsPosts_PollingContinuesAndCurrentKeepsUpdating()
    {
        var time = new FakeTimeProvider();
        using var source = new FakeSensorSource(time);
        var context = new ThrowingSynchronizationContext();
        using var monitor = new SensorMonitor(source, time);
        int raised = 0;
        monitor.ReadingsChanged += (_, _) => Interlocked.Increment(ref raised);

        monitor.Start(context);
        Assert.True(context.WaitForPosts(1, _safetyTimeout), $"The first read was not posted within {_safetyTimeout}.");
        AssertAvailable(monitor.Current.Cpu, FakeSensorSource.DefaultCpuCelsius);

        for (int read = 2; read <= 4; read++)
        {
            double celsius = 60.0 + read;
            source.SetCpu(celsius);
            int postsBefore = context.PostCount;
            Assert.Null(Record.Exception(() => time.Advance(SensorMonitor.PollInterval)));
            Assert.True(context.WaitForPosts(postsBefore + 1, _safetyTimeout), $"Read {read} was not posted within {_safetyTimeout}.");
            AssertAvailable(monitor.Current.Cpu, celsius);
        }

        Assert.Equal(4, source.ReadCount);

        // The fifth read blocks, so the last fresh reading turns Stale and the read then hangs.
        source.BlockReads();
        Assert.Null(Record.Exception(() => time.Advance(SensorMonitor.PollInterval)));
        Assert.True(
            source.WaitForReadCount(5, _safetyTimeout),
            $"The fifth read did not start within {_safetyTimeout}; reads entered: {source.ReadCount}.");

        int postsBeforeStale = context.PostCount;
        Assert.Null(Record.Exception(() => time.Advance(SensorMonitor.StaleAfter)));
        AssertBothUnavailable(monitor.Current, SensorStatus.Stale);
        Assert.False(monitor.IsHung);
        Assert.True(context.PostCount > postsBeforeStale, "The Stale snapshot was not posted.");

        int postsBeforeHung = context.PostCount;
        Assert.Null(Record.Exception(() => time.Advance(SensorMonitor.HungAfter - SensorMonitor.StaleAfter)));
        Assert.True(monitor.IsHung, $"The source was not marked hung with its read outstanding {SensorMonitor.HungAfter}.");
        AssertBothUnavailable(monitor.Current, SensorStatus.Hung);
        Assert.True(context.PostCount > postsBeforeHung, "The Hung snapshot was not posted.");

        Assert.Equal(0, Volatile.Read(ref raised));
        source.ReleaseReads();
    }

    [Fact]
    public void Post_ContextRejectsOpenFailure_SensorThreadStillClosesSourceOnDispose()
    {
        var time = new FakeTimeProvider();
        using var source = new FakeSensorSource(time)
        {
            OpenException = new InvalidOperationException("Simulated driver open failure."),
        };
        var context = new ThrowingSynchronizationContext();
        using var monitor = new SensorMonitor(source, time);

        monitor.Start(context);
        Assert.True(context.WaitForPosts(1, _safetyTimeout), $"The Failed snapshot was not posted within {_safetyTimeout}.");
        AssertBothUnavailable(monitor.Current, SensorStatus.Failed);

        Assert.Null(Record.Exception(() => time.Advance(SensorMonitor.HungAfter)));
        Assert.Equal(0, source.ReadCount);
        Assert.False(monitor.IsHung);

        // The sensor thread outlived its rejected post: it still waits for Dispose, then closes the source.
        AssertDisposeWithinBound(monitor, "after a rejected post");
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(source.OpenThreadId, source.DisposeThreadId);
    }

    [Theory]
    [InlineData(SensorStatus.DriverUnavailable)]
    [InlineData(SensorStatus.NoSensor)]
    [InlineData(SensorStatus.Failed)]
    public void Read_BothSensorsUnavailable_ReportsSourceStatusAndNeverTrips(SensorStatus status)
    {
        using var h = new Harness(createSource: time => FakeSensorSource.AllUnavailable(status, time));
        h.Start();
        h.Tick(2);

        SensorReadings current = h.Monitor.Current;
        AssertBothUnavailable(current, status);
        Assert.Null(ThermalLimits.Evaluate(current));
        Assert.False(h.Monitor.IsHung);
    }

    // CPU-only and RAM-only availability share one theory: the cases differ only in which sensor is scripted.
    [Theory]
    [InlineData(ThermalSensor.Cpu, SensorStatus.NoSensor)]
    [InlineData(ThermalSensor.Cpu, SensorStatus.DriverUnavailable)]
    [InlineData(ThermalSensor.Ram, SensorStatus.DriverUnavailable)]
    [InlineData(ThermalSensor.Ram, SensorStatus.NoSensor)]
    public void Read_OnlyOneSensorAvailable_ItsThermalLimitStaysActive(ThermalSensor available, SensorStatus missingStatus)
    {
        ThermalSensor missing = available == ThermalSensor.Cpu ? ThermalSensor.Ram : ThermalSensor.Cpu;
        double limit = LimitFor(available);
        using var h = new Harness();
        SetTemperature(h.Source, available, limit);
        SetStatus(h.Source, missing, missingStatus);
        h.Start();
        h.Tick();

        SensorReadings current = h.Monitor.Current;
        AssertAvailable(ValueOf(current, available), limit);
        AssertUnavailable(ValueOf(current, missing), missingStatus);
        Assert.Equal(new ThermalVerdict(available, limit, limit), ThermalLimits.Evaluate(current));
    }

    private static void ScriptReadingsAtLimits(FakeSensorSource source, TimeSpan age)
    {
        source.SetCpu(ThermalLimits.CpuLimitCelsius);
        source.SetRam(ThermalLimits.RamLimitCelsius);
        source.CapturedAtOffset = -age;
    }

    // Readings at both limits, each captured its own age before the read returns: the CPU reading
    // through the fake's offset, the RAM reading through a decorator that re-stamps it by the difference.
    private static Harness CreateHarnessAtLimits(TimeSpan cpuAge, TimeSpan ramAge)
    {
        var h = new Harness(decorate: inner => new RamRestampingSource(inner, cpuAge - ramAge));
        ScriptReadingsAtLimits(h.Source, cpuAge);
        return h;
    }

    private static ThermalSensor PeerOf(ThermalSensor sensor) => sensor switch
    {
        ThermalSensor.Cpu => ThermalSensor.Ram,
        ThermalSensor.Ram => ThermalSensor.Cpu,
        _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null),
    };

    private static double LimitFor(ThermalSensor sensor) => sensor switch
    {
        ThermalSensor.Cpu => ThermalLimits.CpuLimitCelsius,
        ThermalSensor.Ram => ThermalLimits.RamLimitCelsius,
        _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null),
    };

    private static SensorValue ValueOf(SensorReadings readings, ThermalSensor sensor) => sensor switch
    {
        ThermalSensor.Cpu => readings.Cpu,
        ThermalSensor.Ram => readings.Ram,
        _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null),
    };

    private static void SetTemperature(FakeSensorSource source, ThermalSensor sensor, double celsius)
    {
        switch (sensor)
        {
            case ThermalSensor.Cpu:
                source.SetCpu(celsius);
                break;
            case ThermalSensor.Ram:
                source.SetRam(celsius);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null);
        }
    }

    private static void SetStatus(FakeSensorSource source, ThermalSensor sensor, SensorStatus status)
    {
        switch (sensor)
        {
            case ThermalSensor.Cpu:
                source.SetCpu(status);
                break;
            case ThermalSensor.Ram:
                source.SetRam(status);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sensor), sensor, null);
        }
    }

    private static void AssertAvailable(SensorValue value, double celsius)
    {
        Assert.Equal(SensorStatus.Available, value.Status);
        Assert.True(value.IsAvailable);
        Assert.Equal(celsius, Assert.NotNull(value.Celsius));
    }

    private static void AssertUnavailable(SensorValue value, SensorStatus status)
    {
        Assert.Equal(status, value.Status);
        Assert.False(value.IsAvailable);
        Assert.Null(value.Celsius);
    }

    private static void AssertBothUnavailable(SensorReadings readings, SensorStatus status)
    {
        AssertUnavailable(readings.Cpu, status);
        AssertUnavailable(readings.Ram, status);
    }

    private static TimeSpan Measure(Action action)
    {
        long startedAt = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(startedAt);
    }

    private static void AssertDisposeWithinBound(SensorMonitor monitor, string situation)
    {
        TimeSpan took = Measure(monitor.Dispose);
        Assert.True(took < _disposeBound, $"Dispose took {took.TotalMilliseconds:F0} ms {situation}.");
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    // Advance runs every timer that falls due on the way; AdjustTime moves the clock and every timer
    // with it, so none runs.
    private static void MoveClockTo(FakeTimeProvider time, DateTimeOffset target, bool timersRun)
    {
        if (timersRun)
        {
            time.Advance(target - time.GetUtcNow());
        }
        else
        {
            time.AdjustTime(target);
        }
    }

    private static string Describe(SensorReadings? readings) =>
        readings is null ? "none" : $"CPU {Describe(readings.Cpu)}, RAM {Describe(readings.Ram)}";

    private static string Describe(SensorValue value) =>
        value.Celsius is double celsius ? $"{celsius} °C ({value.Status})" : value.Status.ToString();

    // One ReadingsChanged invocation as the handler saw it.
    private readonly record struct ReceivedEvent(object? Sender, SensorReadings Readings, SynchronizationContext? Context, int ThreadId);

    // A monitor over a FakeSensorSource on a fake clock, posting to a context the test thread drains.
    private sealed class Harness : IDisposable
    {
        private readonly object _eventsGate = new();
        private readonly List<ReceivedEvent> _events = [];
        private bool _disposed;

        public Harness(
            Func<TimeProvider, FakeSensorSource>? createSource = null,
            Func<ISensorSource, ISensorSource>? decorate = null,
            bool queuePosts = true)
        {
            Time = new FakeTimeProvider();
            Source = createSource?.Invoke(Time) ?? new FakeSensorSource(Time);
            MonitoredSource = decorate?.Invoke(Source) ?? Source;
            Context = new ImmediateSynchronizationContext(queuePosts);
            Monitor = new SensorMonitor(MonitoredSource, Time);
        }

        public FakeTimeProvider Time { get; }

        public FakeSensorSource Source { get; }

        // The source the monitor owns: Source itself, or the decorator wrapped around it.
        public ISensorSource MonitoredSource { get; }

        public ImmediateSynchronizationContext Context { get; }

        public SensorMonitor Monitor { get; }

        public int EventCount
        {
            get
            {
                lock (_eventsGate)
                {
                    return _events.Count;
                }
            }
        }

        public IReadOnlyList<ReceivedEvent> Events
        {
            get
            {
                lock (_eventsGate)
                {
                    return _events.ToArray();
                }
            }
        }

        public SensorReadings? LastReadings
        {
            get
            {
                lock (_eventsGate)
                {
                    return _events.Count == 0 ? null : _events[^1].Readings;
                }
            }
        }

        // Starts the monitor and waits until the sensor thread has opened the source and, when
        // expected, until the immediate first read has been raised.
        public void Start(bool expectFirstRead = true)
        {
            Monitor.ReadingsChanged += OnReadingsChanged;
            Monitor.Start(Context);
            Assert.True(
                SpinWait.SpinUntil(() => Source.OpenCount == 1, _safetyTimeout),
                $"The sensor thread did not open the source within {_safetyTimeout}; OpenCount: {Source.OpenCount}.");
            if (expectFirstRead)
            {
                WaitForEvents(1);
            }
        }

        // Advances the fake clock one PollInterval at a time. When a read is expected, waits after each
        // tick for that read to complete and for the snapshot it published to be raised, which also
        // proves the sensor thread is idle again. A re-marking the same tick raises first, such as a
        // Stale one, is not taken for the read's snapshot. Returns the longest real time a single Advance took.
        public TimeSpan Tick(int seconds = 1, bool expectRead = true)
        {
            TimeSpan slowestAdvance = TimeSpan.Zero;
            for (int second = 0; second < seconds; second++)
            {
                Context.RunPending();
                int completedBefore = Source.CompletedReadCount;

                slowestAdvance = Max(slowestAdvance, Measure(() => Time.Advance(SensorMonitor.PollInterval)));

                if (expectRead)
                {
                    WaitForReadSnapshot(completedBefore);
                }
            }

            return slowestAdvance;
        }

        // Starts the monitor over an InstrumentedSource that holds Open, and waits until the sensor thread
        // is inside it. Returns that source, through which the test releases the open.
        public InstrumentedSource StartWithOpenHeld()
        {
            var instrumented = Assert.IsType<InstrumentedSource>(MonitoredSource);
            Monitor.ReadingsChanged += OnReadingsChanged;
            Monitor.Start(Context);
            Assert.True(
                instrumented.WaitForOpenEntered(_safetyTimeout),
                $"The sensor thread did not begin opening the source within {_safetyTimeout}.");
            return instrumented;
        }

        // Blocks reads, then ticks once so the next read starts and stays in flight. Returns the reads entered.
        public int BlockNextRead()
        {
            Context.RunPending();
            int readsEntered = Source.ReadCount + 1;
            Source.BlockReads();
            Tick(expectRead: false);
            Assert.True(
                Source.WaitForReadCount(readsEntered, _safetyTimeout),
                $"The blocked read did not start within {_safetyTimeout}; reads entered: {Source.ReadCount}.");
            return readsEntered;
        }

        // Lets the blocked read finish and waits for its snapshot to be raised.
        public void ReleaseReadsAndWait()
        {
            Context.RunPending();
            int eventsBefore = EventCount;
            Source.ReleaseReads();
            WaitForEvents(eventsBefore + 1);
        }

        // Starts, blocks the next read and ticks until it has been outstanding HungAfter.
        public void StartAndHang()
        {
            Start();
            BlockNextRead();
            Tick(_ticksUntilHung, expectRead: false);
            Assert.True(Monitor.IsHung, $"The source was not marked hung; last snapshot: {Describe(LastReadings)}.");
        }

        public void WaitForEvents(int count) =>
            Assert.True(
                Context.RunUntil(() => EventCount >= count, _safetyTimeout),
                $"Expected {count} ReadingsChanged event(s) within {_safetyTimeout}; observed {EventCount}, " +
                $"reads entered {Source.ReadCount}, completed {Source.CompletedReadCount}.");

        public void PumpUntil(Func<bool> condition, string expectation) =>
            Assert.True(
                Context.RunUntil(condition, _safetyTimeout),
                $"{expectation} within {_safetyTimeout}; last snapshot: {Describe(LastReadings)}.");

        // A read stamps its CPU value with the clock's time plus the source's offset (a decorator
        // re-stamps the RAM value only), and a throwing read is published as Failed stamped with the
        // clock's time, while a re-marking keeps an earlier reading's stamp. The fake clock stands still
        // meanwhile, so a raised snapshot with this tick's CPU stamp is the read's own.
        private void WaitForReadSnapshot(int completedBefore)
        {
            DateTimeOffset tickedAt = Time.GetUtcNow();
            DateTimeOffset capturedAt = tickedAt + Source.CapturedAtOffset;
            PumpUntil(
                () => Source.CompletedReadCount > completedBefore
                    && LastReadings is { } readings
                    && (readings.Cpu.CapturedAt == capturedAt
                        || (readings.Cpu.Status == SensorStatus.Failed && readings.Cpu.CapturedAt == tickedAt)),
                $"Expected the snapshot of the read at {tickedAt:O} to be raised; completed reads: {Source.CompletedReadCount}");
        }

        // Releases a held Open and blocked reads first, so no sensor thread stays parked after the test.
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (MonitoredSource as InstrumentedSource)?.ReleaseOpen();
            Source.ReleaseReads();
            Monitor.Dispose();
            Source.Dispose();
        }

        // Runs wherever the context runs posts: the test thread when queued, the sensor thread when
        // inline, where an exception would end the process. It only records.
        private void OnReadingsChanged(object? sender, SensorReadings readings)
        {
            var received = new ReceivedEvent(sender, readings, SynchronizationContext.Current, Environment.CurrentManagedThreadId);
            lock (_eventsGate)
            {
                _events.Add(received);
            }
        }
    }

    // Adds only what FakeSensorSource does not offer: it records the thread that opens the source and
    // its kind, can hold Open until released, and can throw from Dispose. Every call reaches the fake.
    private sealed class InstrumentedSource : ISensorSource
    {
        private readonly ISensorSource _inner;
        private readonly Exception? _disposeException;
        private readonly object _gate = new();
        private bool _holdOpen;
        private bool _openEntered;
        private volatile Thread? _openThread;
        private volatile bool _openedOnBackgroundThread;
        private volatile bool _openedOnThreadPoolThread;

        public InstrumentedSource(ISensorSource inner, bool holdOpen = false, Exception? disposeException = null)
        {
            _inner = inner;
            _holdOpen = holdOpen;
            _disposeException = disposeException;
        }

        // Written before the inner Open counts the call, so they are visible once OpenCount is.
        public bool OpenedOnBackgroundThread => _openedOnBackgroundThread;

        public bool OpenedOnThreadPoolThread => _openedOnThreadPoolThread;

        // The sensor thread, so a test can wait for it to exit; null until Open is entered.
        public Thread? OpenThread => _openThread;

        public bool WaitForOpenEntered(TimeSpan timeout)
        {
            long startedAt = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                while (!_openEntered)
                {
                    TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                    if (remaining <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    System.Threading.Monitor.Wait(_gate, remaining);
                }

                return true;
            }
        }

        public void ReleaseOpen()
        {
            lock (_gate)
            {
                _holdOpen = false;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        public void Open()
        {
            Thread thread = Thread.CurrentThread;
            _openThread = thread;
            _openedOnBackgroundThread = thread.IsBackground;
            _openedOnThreadPoolThread = thread.IsThreadPoolThread;
            lock (_gate)
            {
                _openEntered = true;
                System.Threading.Monitor.PulseAll(_gate);
                while (_holdOpen)
                {
                    System.Threading.Monitor.Wait(_gate);
                }
            }

            _inner.Open();
        }

        public SensorReadings Read() => _inner.Read();

        public void Dispose()
        {
            _inner.Dispose();
            if (_disposeException is not null)
            {
                throw _disposeException;
            }
        }
    }

    // Adds only what FakeSensorSource does not offer: a RAM capture time apart from the CPU's, which the
    // fake stamps with one time per read, so the two readings age and turn Stale independently. It shifts
    // each read's RAM stamp by a fixed amount and leaves the CPU value, by which the harness recognises
    // a read's snapshot, untouched. Every call reaches the fake.
    private sealed class RamRestampingSource : ISensorSource
    {
        private readonly ISensorSource _inner;
        private readonly TimeSpan _ramShift;

        public RamRestampingSource(ISensorSource inner, TimeSpan ramShift)
        {
            _inner = inner;
            _ramShift = ramShift;
        }

        public void Open() => _inner.Open();

        public SensorReadings Read()
        {
            SensorReadings readings = _inner.Read();
            SensorValue ram = readings.Ram;
            DateTimeOffset capturedAt = ram.CapturedAt + _ramShift;
            return readings with
            {
                Ram = ram is { Status: SensorStatus.Available, Celsius: double celsius }
                    ? SensorValue.Available(celsius, capturedAt)
                    : SensorValue.Unavailable(ram.Status, capturedAt),
            };
        }

        public void Dispose() => _inner.Dispose();
    }

    // Holds every post until the test runs it, in any order, so a late delivery can be staged.
    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly List<(SendOrPostCallback Callback, object? State)> _posts = [];

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _posts.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                _posts.Add((d, state));
            }
        }

        public bool WaitForPosts(int count, TimeSpan timeout) => SpinWait.SpinUntil(() => PostCount >= count, timeout);

        public void Run(int index)
        {
            (SendOrPostCallback Callback, object? State) post;
            lock (_gate)
            {
                post = _posts[index];
            }

            post.Callback(post.State);
        }
    }

    // Rejects every post, as a context that has already shut down may, and counts each attempt.
    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            throw new InvalidOperationException("context closed");
        }

        public bool WaitForPosts(int count, TimeSpan timeout) => SpinWait.SpinUntil(() => PostCount >= count, timeout);
    }
}
