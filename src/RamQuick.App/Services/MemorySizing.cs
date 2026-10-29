namespace RamQuick.App.Services;

/// <summary>
/// The outcome of <see cref="MemorySizing.Plan(ulong, int)"/>: the number of bytes a run plans to test and
/// whether that is enough for a run to start.
/// </summary>
/// <param name="PlannedBytes">
/// The planned size of the region under test, in bytes: a multiple of
/// <c>workerCount × <see cref="MemorySizing.SliceGranularityBytes"/></c>, possibly 0. This is the size requested
/// from the engine; the engine may lock less, and the size it reports at Prepared (<c>tested_bytes</c>) is the
/// coverage denominator, not this value.
/// </param>
/// <param name="IsSufficient">
/// <see langword="true"/> when <paramref name="PlannedBytes"/> is at least
/// <see cref="MemorySizing.MinimumRegionBytes"/>. When <see langword="false"/>, Start is refused and the run state
/// stays unchanged.
/// </param>
public sealed record MemorySizingResult(ulong PlannedBytes, bool IsSufficient);

/// <summary>
/// Sizes the memory region a run tests from the physical memory currently available.
/// </summary>
/// <remarks>
/// <para>
/// The planned size is <c>min(<see cref="AvailableRatio"/> × available, available − <see cref="HeadroomBytes"/>)</c>,
/// rounded down to a multiple of <c>workers × <see cref="SliceGranularityBytes"/></c>, so the region splits into one
/// equal 2 MiB-aligned slice per worker. The rule always leaves at least 10 % of available memory, and never less
/// than 1 GiB, free for Windows and other applications. A planned size below <see cref="MinimumRegionBytes"/>
/// refuses Start.
/// </para>
/// <para>
/// Worked examples (GiB = 2^30 bytes, MiB = 2^20 bytes):
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// 32 GiB available, 16 workers: min(28.8 GiB, 31 GiB) = 28.8 GiB (30,923,764,531 bytes), rounded down to a multiple of
/// 16 × 2 MiB = 32 MiB, gives 921 × 32 MiB = 30,903,631,872 bytes; sufficient.
/// </description>
/// </item>
/// <item>
/// <description>
/// 1.5 GiB available, 1 worker: min(1.35 GiB, 0.5 GiB) = 0.5 GiB (536,870,912 bytes), already a multiple of 2 MiB;
/// below 1 GiB, so insufficient.
/// </description>
/// </item>
/// </list>
/// <para>
/// The class is pure: it reads no system state. The caller supplies the available bytes, which the run controller
/// takes from the engine's system query, and shows the refusal message itself. The constants are the single retune
/// points for the sizing rule.
/// </para>
/// </remarks>
public static class MemorySizing
{
    /// <summary>
    /// The largest share of available physical memory a run plans to test: 0.90, which leaves at least 10 % free.
    /// </summary>
    public const double AvailableRatio = 0.90;

    /// <summary>
    /// The memory always left free, whatever <see cref="AvailableRatio"/> allows: 1 GiB.
    /// </summary>
    public const ulong HeadroomBytes = 1UL << 30;

    /// <summary>
    /// The smallest region a run may test: 1 GiB. A planned size below it refuses Start; it is also the engine's
    /// <c>min_region_bytes</c> floor, below which a lock shortfall stops shrinking and ends the run as insufficient
    /// memory.
    /// </summary>
    public const ulong MinimumRegionBytes = 1UL << 30;

    /// <summary>
    /// The granularity of each worker's slice: 2 MiB. It matches the engine's per-slice rounding and the x64 large-page
    /// size, so a planned region divides into equal slices that the engine does not shrink further.
    /// </summary>
    public const ulong SliceGranularityBytes = 2UL << 20;

    /// <summary>
    /// Rounds <paramref name="bytes"/> down to a multiple of <c><paramref name="workerCount"/> ×
    /// <see cref="SliceGranularityBytes"/></c>.
    /// </summary>
    /// <param name="bytes">The size to round, in bytes.</param>
    /// <param name="workerCount">The number of test workers, one per logical processor; at least 1.</param>
    /// <returns>
    /// The largest multiple of <c><paramref name="workerCount"/> × <see cref="SliceGranularityBytes"/></c> that does
    /// not exceed <paramref name="bytes"/>; 0 when <paramref name="bytes"/> is smaller than one such unit.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount"/> is less than 1.</exception>
    public static ulong RoundDownToSlices(ulong bytes, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        ulong unit = checked((ulong)workerCount * SliceGranularityBytes);
        return bytes - (bytes % unit);
    }

    /// <summary>
    /// Plans the size of the region under test from the physical memory currently available.
    /// </summary>
    /// <param name="availableBytes">The available physical memory, in bytes.</param>
    /// <param name="workerCount">The number of test workers, one per logical processor; at least 1.</param>
    /// <returns>
    /// The planned size, <c>min(<see cref="AvailableRatio"/> × <paramref name="availableBytes"/>,
    /// <paramref name="availableBytes"/> − <see cref="HeadroomBytes"/>)</c> rounded down by
    /// <see cref="RoundDownToSlices(ulong, int)"/>, and whether it reaches <see cref="MinimumRegionBytes"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount"/> is less than 1.</exception>
    public static MemorySizingResult Plan(ulong availableBytes, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        // For byte counts below 2^53 (about 9 PB) the double product is within one byte of the exact value, an error
        // the slice rounding below absorbs. With a ratio below 1 the product stays below 2^64 for every ulong input,
        // so the cast cannot overflow.
        ulong ratioLimitBytes = (ulong)Math.Floor(availableBytes * AvailableRatio);
        ulong headroomLimitBytes = availableBytes > HeadroomBytes ? availableBytes - HeadroomBytes : 0;
        ulong plannedBytes = RoundDownToSlices(Math.Min(ratioLimitBytes, headroomLimitBytes), workerCount);
        return new MemorySizingResult(plannedBytes, plannedBytes >= MinimumRegionBytes);
    }
}
