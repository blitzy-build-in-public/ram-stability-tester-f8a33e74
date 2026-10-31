using RamQuick.App.Services;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for <see cref="MemorySizing"/>: a run plans min(0.90 × available, available − 1 GiB)
/// rounded down to a multiple of workers × 2 MiB, and a plan below 1 GiB refuses Start. Expected
/// byte counts are worked out by hand, never by running the sizing formula again.
/// </summary>
public sealed class MemorySizingTests
{
    private const ulong MiB = 1UL << 20;
    private const ulong GiB = 1UL << 30;
    private const ulong TiB = 1UL << 40;

    /// <summary>
    /// Every pairing of these available sizes and worker counts, for the invariants every plan
    /// must satisfy. The sizes span the saturation range, the 1 GiB threshold, uneven byte counts
    /// and the top of the <see cref="ulong"/> range; the worker counts include odd units, the
    /// 64-processor group boundary and multi-group machines.
    /// </summary>
    public static TheoryData<ulong, int> InvariantCases
    {
        get
        {
            ulong[] availableSizes =
            [
                0UL,
                512 * MiB,
                GiB,
                3 * GiB / 2,
                2 * GiB,
                3 * GiB,
                10 * GiB,
                (17 * GiB) + 123_456_789UL,
                32 * GiB,
                192 * GiB,
                TiB,
                ulong.MaxValue,
            ];
            int[] workerCounts = [1, 2, 3, 32, 64, 65, 128, 192];

            var cases = new TheoryData<ulong, int>();
            foreach (ulong availableBytes in availableSizes)
            {
                foreach (int workerCount in workerCounts)
                {
                    cases.Add(availableBytes, workerCount);
                }
            }

            return cases;
        }
    }

    [Fact]
    public void Constants_Values_MatchTheSizingRule()
    {
        Assert.Equal(0.90, MemorySizing.AvailableRatio);
        Assert.Equal(GiB, MemorySizing.HeadroomBytes);
        Assert.Equal(GiB, MemorySizing.MinimumRegionBytes);
        Assert.Equal(2 * MiB, MemorySizing.SliceGranularityBytes);
    }

    [Theory]
    // Low free memory: 1.5 GiB leaves 0.5 GiB after the 1 GiB headroom, so Start is refused.
    // RunControllerTests' low-memory refusal uses the same numbers.
    [InlineData(3 * GiB / 2, 1, 512 * MiB, false)]
    [InlineData(3 * GiB / 2, 8, 512 * MiB, false)]
    // Exactly at the minimum: a 1 GiB plan is sufficient, with 2 MiB and 64 MiB units alike.
    [InlineData(2 * GiB, 1, GiB, true)]
    [InlineData(2 * GiB, 32, GiB, true)]
    // 192 workers round in 384 MiB units, which pushes the 1 GiB headroom limit down to 768 MiB.
    [InlineData(2 * GiB, 192, 805_306_368UL, false)]
    // The 0.90 term wins on large machines: 28.8 GiB becomes 460 × 64 MiB, and 172.8 GiB
    // becomes 460 × 384 MiB.
    [InlineData(32 * GiB, 32, 30_870_077_440UL, true)]
    [InlineData(192 * GiB, 192, 185_220_464_640UL, true)]
    // Crossover: 0.9 × 10 GiB and 10 GiB − 1 GiB are both 9 GiB.
    [InlineData(10 * GiB, 1, 9 * GiB, true)]
    // 0.9 TiB is 989,560,464,998.4 bytes, which rounds down to 471,859 × 2 MiB.
    [InlineData(TiB, 1, 989_560_045_568UL, true)]
    // available − 1 GiB saturates at 0 rather than wrapping around.
    [InlineData(0UL, 1, 0UL, false)]
    [InlineData(512 * MiB, 1, 0UL, false)]
    [InlineData(GiB, 1, 0UL, false)]
    [InlineData(GiB, 192, 0UL, false)]
    // A unit of (2^31 − 1) × 2 MiB exceeds 1 TiB: nothing is planned, and nothing overflows.
    [InlineData(TiB, int.MaxValue, 0UL, false)]
    public void Plan_WorkedCase_ReturnsExpectedPlan(
        ulong availableBytes,
        int workerCount,
        ulong expectedPlannedBytes,
        bool expectedSufficient)
    {
        MemorySizingResult result = MemorySizing.Plan(availableBytes, workerCount);

        Assert.Equal(new MemorySizingResult(expectedPlannedBytes, expectedSufficient), result);
    }

    [Theory]
    [MemberData(nameof(InvariantCases))]
    public void Plan_AnyAvailableAndWorkerCount_SatisfiesSizingInvariants(ulong availableBytes, int workerCount)
    {
        MemorySizingResult result = MemorySizing.Plan(availableBytes, workerCount);

        // Exact decimal bounds, so the check does not share the implementation's double arithmetic.
        decimal plannedBytes = result.PlannedBytes;
        decimal unitBytes = (decimal)workerCount * 2 * MiB;
        decimal ratioLimitBytes = 0.90m * availableBytes;
        decimal headroomLimitBytes = availableBytes > GiB ? (decimal)availableBytes - GiB : 0m;
        decimal limitBytes = Math.Min(ratioLimitBytes, headroomLimitBytes);

        Assert.Equal(0m, plannedBytes % unitBytes);
        Assert.True(
            plannedBytes <= ratioLimitBytes,
            $"Planned {plannedBytes} bytes exceeds 0.90 × {availableBytes} = {ratioLimitBytes} bytes.");
        Assert.True(
            plannedBytes <= headroomLimitBytes,
            $"Planned {plannedBytes} bytes leaves less than 1 GiB of {availableBytes} bytes free.");
        Assert.True(
            limitBytes - plannedBytes < unitBytes,
            $"Planned {plannedBytes} bytes is not the largest multiple of {unitBytes} bytes within {limitBytes} bytes.");
        Assert.Equal(result.PlannedBytes >= GiB, result.IsSufficient);
    }

    [Theory]
    [InlineData(GiB + 1, 1, GiB)]
    [InlineData(100 * MiB, 3, 96 * MiB)]
    // An exact multiple of the unit is unchanged, and less than one unit rounds to 0.
    [InlineData(6 * MiB, 3, 6 * MiB)]
    [InlineData((2 * MiB) - 1, 1, 0UL)]
    [InlineData(0UL, 64, 0UL)]
    // 2^64 − 2 MiB, and 4,096 × (2^31 − 1) × 2 MiB = 2^64 − 2^33: the largest inputs do not overflow.
    [InlineData(ulong.MaxValue, 1, 18_446_744_073_707_454_464UL)]
    [InlineData(ulong.MaxValue, int.MaxValue, 18_446_744_065_119_617_024UL)]
    public void RoundDownToSlices_Bytes_RoundsDownToWholeWorkerUnits(ulong bytes, int workerCount, ulong expectedBytes)
    {
        Assert.Equal(expectedBytes, MemorySizing.RoundDownToSlices(bytes, workerCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Plan_WorkerCountBelowOne_ThrowsArgumentOutOfRange(int workerCount)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => MemorySizing.Plan(32 * GiB, workerCount));

        Assert.Equal("workerCount", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RoundDownToSlices_WorkerCountBelowOne_ThrowsArgumentOutOfRange(int workerCount)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => MemorySizing.RoundDownToSlices(32 * GiB, workerCount));

        Assert.Equal("workerCount", exception.ParamName);
    }
}
