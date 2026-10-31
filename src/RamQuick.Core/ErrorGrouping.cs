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
/// Groups are built by <see cref="ErrorGrouping.Group"/>, or batch by batch by
/// <see cref="IncrementalErrorGrouping"/>, which keeps only the largest. Property names are a
/// serialization contract, camelCased into <c>errors.groups[]</c>; never rename one.
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
    internal static void RequireRecord(ErrorRecord? record, string paramName)
    {
        if (record is null)
        {
            throw new ArgumentException("The sequence contains a null error record.", paramName);
        }
    }

    // The single listing order of groups, shared by Group and IncrementalErrorGrouping so the two
    // can never diverge: the larger count first, then the earlier first failure, then the lower
    // word address. Negative when x is listed before y; 0 only for the same word address.
    internal static int CompareListingKeys(
        int xCount,
        TimeSpan xFirstTimeIntoTest,
        ulong xWordAddress,
        int yCount,
        TimeSpan yFirstTimeIntoTest,
        ulong yWordAddress)
    {
        int byCount = yCount.CompareTo(xCount);
        if (byCount != 0)
        {
            return byCount;
        }

        int byFirstTime = xFirstTimeIntoTest.CompareTo(yFirstTimeIntoTest);
        if (byFirstTime != 0)
        {
            return byFirstTime;
        }

        return xWordAddress.CompareTo(yWordAddress);
    }

    private static int CompareForListing(ErrorGroup x, ErrorGroup y) =>
        CompareListingKeys(x.Count, x.FirstTimeIntoTest, x.WordAddress, y.Count, y.FirstTimeIntoTest, y.WordAddress);

    // One word's running group, shared by Group and IncrementalErrorGrouping so both derive the
    // count, the union and the first and last records the same way.
    internal sealed class GroupAccumulator
    {
        private readonly ulong _wordAddress;
        private int _count;
        private ulong _bitMaskUnion;
        private ErrorRecord _first;
        private TimeSpan _lastTimeIntoTest;
        private ErrorGroup? _snapshot;

        public GroupAccumulator(ulong wordAddress, ErrorRecord record)
        {
            _wordAddress = wordAddress;
            _count = 1;
            _bitMaskUnion = record.BitMask;
            _first = record;
            _lastTimeIntoTest = record.TimeIntoTest;
        }

        public ulong WordAddress => _wordAddress;

        public int Count => _count;

        public TimeSpan FirstTimeIntoTest => _first.TimeIntoTest;

        // The IncrementalErrorGrouping batch that last added a record here; 0 before its first.
        public long BatchStamp { get; set; }

        public void Add(ErrorRecord record)
        {
            _count = checked(_count + 1);
            _snapshot = null;
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

        // The group as it stands: the same instance until the next Add, so an untouched group keeps
        // its reference across IncrementalErrorGrouping batches.
        public ErrorGroup ToGroup() => _snapshot ??= new()
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

/// <summary>
/// Groups a run's stored error records by the 8-byte word they hit, batch by batch, and keeps only
/// the largest groups, so that each batch costs time in proportion to its own size rather than to
/// every record stored before it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Result.</b> After any sequence of <see cref="Add"/> calls, <see cref="Largest"/> equals
/// <c>ErrorGrouping.Group(allAdded).Take(MaxGroups)</c> element by element, where <c>allAdded</c>
/// is every record added so far: the same groups, in the same order, by the same comparison.
/// </para>
/// <para>
/// <b>Cost.</b> Each <see cref="Add"/> updates one running group per record and then selects the
/// largest groups among those it touched and the previous <see cref="Largest"/>. That is exact
/// because adding a record only moves a group up the listing order: its count rises and its first
/// time can only fall, so a group outside the previous largest that the batch did not touch stays
/// outside. The time is proportional to the batch size plus <see cref="MaxGroups"/>, by a factor of
/// log <see cref="MaxGroups"/>, whatever the number of records added before. Memory holds one
/// running group per distinct word added, which the stored-detail cap bounds.
/// </para>
/// <para>
/// <b>Stored detail only.</b> Like <see cref="ErrorGrouping.Group"/>, the groups describe the stored
/// records that were added, at most <see cref="ErrorGrouping.StoredDetailCap"/> per run, never the
/// true error total, which comes from the engine's counters.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe: one owner thread creates the instance and makes every call.
/// The returned lists are read-only and never change, so they can be read on any thread.
/// </para>
/// </remarks>
public sealed class IncrementalErrorGrouping
{
    // Orders groups from the last listed to the first, so the selection heap's root is the weakest
    // group kept so far.
    private static readonly IComparer<ErrorGrouping.GroupAccumulator> _weakestFirst =
        Comparer<ErrorGrouping.GroupAccumulator>.Create(static (x, y) => ErrorGrouping.CompareListingKeys(
            y.Count, y.FirstTimeIntoTest, y.WordAddress, x.Count, x.FirstTimeIntoTest, x.WordAddress));

    private readonly Dictionary<ulong, ErrorGrouping.GroupAccumulator> _accumulators = new();
    private readonly List<ErrorGrouping.GroupAccumulator> _touched = new();
    private readonly PriorityQueue<ErrorGrouping.GroupAccumulator, ErrorGrouping.GroupAccumulator> _selection =
        new(_weakestFirst);

    private ErrorGrouping.GroupAccumulator[] _largestAccumulators = Array.Empty<ErrorGrouping.GroupAccumulator>();
    private IReadOnlyList<ErrorGroup> _largest = ReadOnlyCollection<ErrorGroup>.Empty;
    private long _batchStamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalErrorGrouping"/> class with no groups.
    /// </summary>
    /// <param name="maxGroups">The number of largest groups <see cref="Largest"/> holds at most.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxGroups"/> is 0 or negative.</exception>
    public IncrementalErrorGrouping(int maxGroups)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGroups);
        MaxGroups = maxGroups;
    }

    /// <summary>
    /// Gets the number of largest groups <see cref="Largest"/> holds at most.
    /// </summary>
    public int MaxGroups { get; }

    /// <summary>
    /// Gets the largest groups of the records added so far: at most <see cref="MaxGroups"/>, ordered
    /// like <see cref="ErrorGrouping.Group"/>, by <see cref="ErrorGroup.Count"/> descending, then
    /// <see cref="ErrorGroup.FirstTimeIntoTest"/> ascending, then <see cref="ErrorGroup.WordAddress"/>
    /// ascending. Empty until the first record is added. A new read-only list instance replaces it
    /// whenever its groups change, and the instance is kept while they do not.
    /// </summary>
    public IReadOnlyList<ErrorGroup> Largest => _largest;

    /// <summary>
    /// Adds a batch of stored error records to the groups and returns the new <see cref="Largest"/>.
    /// </summary>
    /// <param name="records">
    /// The records stored since the previous call, in any order; the list is read, never retained.
    /// </param>
    /// <returns>
    /// The new <see cref="Largest"/>. It is the previous instance when <paramref name="records"/> is
    /// empty or when every record landed in groups that stay outside the largest, so the listed
    /// groups are unchanged; otherwise it is a new instance.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="records"/> contains a <see langword="null"/> element. The whole batch is checked
    /// first, so a rejected batch changes no group.
    /// </exception>
    public IReadOnlyList<ErrorGroup> Add(IReadOnlyList<ErrorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        for (int index = 0; index < records.Count; index++)
        {
            ErrorGrouping.RequireRecord(records[index], nameof(records));
        }

        if (records.Count == 0)
        {
            return _largest;
        }

        long stamp = ++_batchStamp;
        _touched.Clear();
        for (int index = 0; index < records.Count; index++)
        {
            ErrorRecord record = records[index];
            ulong wordAddress = record.WordAddress;
            if (_accumulators.TryGetValue(wordAddress, out ErrorGrouping.GroupAccumulator? accumulator))
            {
                accumulator.Add(record);
            }
            else
            {
                accumulator = new ErrorGrouping.GroupAccumulator(wordAddress, record);
                _accumulators.Add(wordAddress, accumulator);
            }

            if (accumulator.BatchStamp != stamp)
            {
                accumulator.BatchStamp = stamp;
                _touched.Add(accumulator);
            }
        }

        // The candidates are the touched groups and the untouched groups of the previous largest;
        // no other group can rank among the largest now.
        _selection.Clear();
        foreach (ErrorGrouping.GroupAccumulator touched in _touched)
        {
            Offer(touched);
        }

        foreach (ErrorGrouping.GroupAccumulator previous in _largestAccumulators)
        {
            if (previous.BatchStamp != stamp)
            {
                Offer(previous);
            }
        }

        _touched.Clear();
        var selected = new ErrorGrouping.GroupAccumulator[_selection.Count];
        for (int index = selected.Length - 1; index >= 0; index--)
        {
            selected[index] = _selection.Dequeue();
        }

        if (IsUnchanged(selected, stamp))
        {
            return _largest;
        }

        var groups = new ErrorGroup[selected.Length];
        for (int index = 0; index < selected.Length; index++)
        {
            groups[index] = selected[index].ToGroup();
        }

        _largestAccumulators = selected;
        _largest = Array.AsReadOnly(groups);
        return _largest;
    }

    // Keeps the candidate while fewer than MaxGroups are kept; otherwise it replaces the weakest kept
    // group when it ranks above it.
    private void Offer(ErrorGrouping.GroupAccumulator candidate)
    {
        if (_selection.Count < MaxGroups)
        {
            _selection.Enqueue(candidate, candidate);
        }
        else
        {
            _selection.EnqueueDequeue(candidate, candidate);
        }
    }

    // The listed groups are unchanged when the same groups were selected in the same order and this
    // batch touched none of them, so each one's snapshot is the instance already listed.
    private bool IsUnchanged(ErrorGrouping.GroupAccumulator[] selected, long stamp)
    {
        if (selected.Length != _largestAccumulators.Length)
        {
            return false;
        }

        for (int index = 0; index < selected.Length; index++)
        {
            if (!ReferenceEquals(selected[index], _largestAccumulators[index]) || selected[index].BatchStamp == stamp)
            {
                return false;
            }
        }

        return true;
    }
}
