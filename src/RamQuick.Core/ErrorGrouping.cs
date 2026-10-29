using System.Collections.ObjectModel;

namespace RamQuick.Core;

/// <summary>
/// The stored error records that hit one 8-byte word, summarised as a single line: the address,
/// how many stored records hit it, which bits flipped, and when and where it first failed.
/// </summary>
/// <remarks>
/// <para>
/// A group is derived from the stored error records only, at most
/// <see cref="ErrorGrouping.StoredDetailCap"/> per run. Its <see cref="Count"/>, the number of
/// groups and the number of distinct addresses therefore describe stored detail, never the true
/// error total, which comes from the engine's counters. Consumers label them that way: once the cap
/// is reached the UI heads its list "Grouped from the first 10,000 errors", and
/// <c>RamQuick.Logging</c> writes the groups into the summary's <c>errors.groups[]</c> beside the
/// stored-detail fields <c>errors.storedCount</c>, <c>errors.storedCap</c> and
/// <c>errors.storedDistinctAddresses</c>.
/// </para>
/// <para>
/// Groups are built by <see cref="ErrorGrouping.Group"/>. Property names are a serialization
/// contract, camelCased into <c>errors.groups[]</c>; never rename one.
/// </para>
/// </remarks>
public sealed record ErrorGroup
{
    /// <summary>
    /// Gets the address of the 8-byte word every record in the group hit: the shared
    /// <see cref="ErrorRecord.WordAddress"/> of those records, a virtual address in v1.
    /// </summary>
    public required ulong WordAddress { get; init; }

    /// <summary>
    /// Gets the number of stored records in the group, at least 1. Stored records number at most
    /// <see cref="ErrorGrouping.StoredDetailCap"/>, so the count fits an <see cref="int"/>. Once the
    /// cap is reached it can be lower than the number of errors the engine counted at this word.
    /// </summary>
    public required int Count { get; init; }

    /// <summary>
    /// Gets the bitwise OR of <see cref="ErrorRecord.BitMask"/> over the group's records: every bit
    /// that flipped at this word in at least one stored record.
    /// </summary>
    public required ulong BitMaskUnion { get; init; }

    /// <summary>
    /// Gets the <see cref="ErrorRecord.StepName"/> of the group's earliest record: the one with the
    /// smallest <see cref="ErrorRecord.TimeIntoTest"/>, and among equal times the smallest
    /// <see cref="ErrorRecord.Sequence"/>.
    /// </summary>
    public required string FirstStepName { get; init; }

    /// <summary>
    /// Gets the smallest <see cref="ErrorRecord.TimeIntoTest"/> among the group's records.
    /// </summary>
    public required TimeSpan FirstTimeIntoTest { get; init; }

    /// <summary>
    /// Gets the largest <see cref="ErrorRecord.TimeIntoTest"/> among the group's records. It equals
    /// <see cref="FirstTimeIntoTest"/> when every record was confirmed at the same time.
    /// </summary>
    public required TimeSpan LastTimeIntoTest { get; init; }
}

/// <summary>
/// Groups stored error records by the 8-byte word they hit, so that repeated errors at one address
/// read as one line, and defines the cap on stored error detail.
/// </summary>
/// <remarks>
/// Every member is pure: it keeps no state, performs no I/O, and neither modifies nor retains its
/// input. Results describe the stored records passed in, never the true error total. Callers take
/// the true total from the engine's counters: <see cref="RunProgress.TotalErrors"/>, the totals
/// carried by every error batch, and the completed run result.
/// </remarks>
public static class ErrorGrouping
{
    /// <summary>
    /// The number of detailed error records a run stores at most: the engine's
    /// <c>error_detail_cap</c> and the summary's <c>errors.storedCap</c>. This is the single managed
    /// definition of the cap. <c>RunController</c> stores at most this many records and passes it to
    /// the engine as the error detail cap, <c>RamQuick.Logging</c> records it in the summary, and the
    /// UI's grouped-list header names it.
    /// </summary>
    public const int StoredDetailCap = 10_000;

    /// <summary>
    /// Groups stored error records by <see cref="ErrorRecord.WordAddress"/>.
    /// </summary>
    /// <param name="records">
    /// The stored error records of a run, in any order; normally at most
    /// <see cref="StoredDetailCap"/> of them. The sequence is enumerated once.
    /// </param>
    /// <returns>
    /// A read-only list holding one group per distinct word address, ordered by
    /// <see cref="ErrorGroup.Count"/> descending, then <see cref="ErrorGroup.FirstTimeIntoTest"/>
    /// ascending, then <see cref="ErrorGroup.WordAddress"/> ascending. Word addresses are unique
    /// across groups, so the order is fully deterministic. The list is empty when
    /// <paramref name="records"/> is. It holds every group; a caller showing only the largest takes
    /// them from the front.
    /// </returns>
    /// <remarks>
    /// The groups describe stored detail only. Their counts never add up to the true error total
    /// once the cap is reached, and must never be used as one.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> contains a <see langword="null"/> element.</exception>
    public static IReadOnlyList<ErrorGroup> Group(IEnumerable<ErrorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var accumulators = new Dictionary<ulong, GroupAccumulator>();
        foreach (ErrorRecord record in records)
        {
            RequireRecord(record, nameof(records));
            ulong wordAddress = record.WordAddress;
            if (accumulators.TryGetValue(wordAddress, out GroupAccumulator? accumulator))
            {
                accumulator.Add(record);
            }
            else
            {
                accumulators.Add(wordAddress, new GroupAccumulator(wordAddress, record));
            }
        }

        if (accumulators.Count == 0)
        {
            return ReadOnlyCollection<ErrorGroup>.Empty;
        }

        var groups = new ErrorGroup[accumulators.Count];
        int index = 0;
        foreach (GroupAccumulator accumulator in accumulators.Values)
        {
            groups[index++] = accumulator.ToGroup();
        }

        Array.Sort(groups, CompareForListing);
        return Array.AsReadOnly(groups);
    }

    /// <summary>
    /// Counts the distinct <see cref="ErrorRecord.WordAddress"/> values among stored error records:
    /// the summary's <c>errors.storedDistinctAddresses</c> and the address count in the cause
    /// analyzer's evidence lines.
    /// </summary>
    /// <param name="records">
    /// The stored error records of a run, in any order. The sequence is enumerated once.
    /// </param>
    /// <returns>
    /// The number of distinct word addresses, which equals the number of groups
    /// <see cref="Group"/> returns for the same records; 0 for an empty sequence.
    /// </returns>
    /// <remarks>
    /// The count describes stored detail only, never the number of addresses the engine found
    /// failing once the cap is reached.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> contains a <see langword="null"/> element.</exception>
    public static int CountDistinctAddresses(IEnumerable<ErrorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var wordAddresses = new HashSet<ulong>();
        foreach (ErrorRecord record in records)
        {
            RequireRecord(record, nameof(records));
            wordAddresses.Add(record.WordAddress);
        }

        return wordAddresses.Count;
    }

    // A null element is rejected as an argument error rather than left to surface as a
    // NullReferenceException partway through the enumeration.
    private static void RequireRecord(ErrorRecord? record, string paramName)
    {
        if (record is null)
        {
            throw new ArgumentException("The sequence contains a null error record.", paramName);
        }
    }

    private static int CompareForListing(ErrorGroup x, ErrorGroup y)
    {
        int byCount = y.Count.CompareTo(x.Count);
        if (byCount != 0)
        {
            return byCount;
        }

        int byFirstTime = x.FirstTimeIntoTest.CompareTo(y.FirstTimeIntoTest);
        if (byFirstTime != 0)
        {
            return byFirstTime;
        }

        return x.WordAddress.CompareTo(y.WordAddress);
    }

    private sealed class GroupAccumulator
    {
        private readonly ulong _wordAddress;
        private int _count;
        private ulong _bitMaskUnion;
        private ErrorRecord _first;
        private TimeSpan _lastTimeIntoTest;

        public GroupAccumulator(ulong wordAddress, ErrorRecord record)
        {
            _wordAddress = wordAddress;
            _count = 1;
            _bitMaskUnion = record.BitMask;
            _first = record;
            _lastTimeIntoTest = record.TimeIntoTest;
        }

        public void Add(ErrorRecord record)
        {
            _count = checked(_count + 1);
            _bitMaskUnion |= record.BitMask;

            if (record.TimeIntoTest < _first.TimeIntoTest
                || (record.TimeIntoTest == _first.TimeIntoTest && record.Sequence < _first.Sequence))
            {
                _first = record;
            }

            if (record.TimeIntoTest > _lastTimeIntoTest)
            {
                _lastTimeIntoTest = record.TimeIntoTest;
            }
        }

        public ErrorGroup ToGroup() => new()
        {
            WordAddress = _wordAddress,
            Count = _count,
            BitMaskUnion = _bitMaskUnion,
            FirstStepName = _first.StepName,
            FirstTimeIntoTest = _first.TimeIntoTest,
            LastTimeIntoTest = _lastTimeIntoTest,
        };
    }
}
