using System.Numerics;

namespace RamQuick.Core;

/// <summary>
/// One confirmed memory error: a verification load that returned a value other than the one the
/// word should hold, together with its cache-flushed re-read. Built by <c>RamQuick.Interop</c>
/// from one <c>rq_error_record</c> of an engine error batch.
/// </summary>
/// <remarks>
/// <para>
/// The record carries every field the error analysis reads (address, bit position, step, access
/// type, time since write, thread) and every field the UI and logs display (step name, time into
/// test, address, expected, actual and re-read values). At most 10,000 records are stored per run;
/// the true error total is kept separately from engine counters and is never derived from them.
/// </para>
/// <para>
/// The record is plain immutable data with value equality. Its stored properties are init-only
/// and required, so a record is always complete; use a <c>with</c> expression to derive a changed
/// copy. <see cref="BitMask"/>, <see cref="BitPositions"/> and <see cref="WordAddress"/> are
/// computed from the stored properties on every access and have no backing fields, so they can
/// never disagree with the values they derive from and take no part in equality.
/// </para>
/// <para>
/// The computed members are get-only. System.Text.Json serializes get-only properties by default,
/// so <c>RamQuick.Logging</c> decides whether the summary's <c>errors.records[]</c> includes them;
/// this type carries no serialization attributes. Property names are camelCased into that array,
/// so none may be renamed.
/// </para>
/// <para>
/// Every step and pass index in <c>RamQuick.Core</c> is zero-based. Display adds 1.
/// </para>
/// </remarks>
public sealed record ErrorRecord
{
    /// <summary>
    /// Gets the engine's record number within the run: the confirmation order, starting at 0 and
    /// increasing monotonically across every worker.
    /// Source: <c>rq_error_record.sequence</c>.
    /// </summary>
    public required ulong Sequence { get; init; }

    /// <summary>
    /// Gets the address of the mismatching 8-byte word, of kind <see cref="AddressKind"/>.
    /// Source: <c>rq_error_record.address</c>.
    /// </summary>
    public required ulong Address { get; init; }

    /// <summary>
    /// Gets the kind of <see cref="Address"/>. Every v1 record carries
    /// <see cref="Core.AddressKind.Virtual"/>, which the UI and logs label "virtual address",
    /// because no documented user-mode Windows API yields physical addresses.
    /// Source: <c>rq_error_record.address_kind</c>.
    /// </summary>
    public required AddressKind AddressKind { get; init; }

    /// <summary>
    /// Gets the logical byte offset of the word within the tested region, in
    /// <c>[0, tested bytes)</c>. The region is the worker slices concatenated in worker order, each
    /// slice its chunks in order. Chunks are separate allocations, so the offset is not
    /// <see cref="Address"/> minus a base address. The Bit lane and Address cluster rules use it
    /// to measure spread and to place their windows.
    /// Source: <c>rq_error_record.region_offset</c>, in bytes.
    /// </summary>
    public required ulong RegionOffset { get; init; }

    /// <summary>
    /// Gets the raw 64-bit value the word should hold. Always kept.
    /// Source: <c>rq_error_record.expected</c>.
    /// </summary>
    public required ulong Expected { get; init; }

    /// <summary>
    /// Gets the raw 64-bit value the verification load returned. Always kept.
    /// Source: <c>rq_error_record.actual</c>.
    /// </summary>
    public required ulong Actual { get; init; }

    /// <summary>
    /// Gets the raw 64-bit value of the single re-read, issued after a fence, a flush of the word's
    /// cache line and a second fence. Always kept.
    /// Source: <c>rq_error_record.reread</c>.
    /// </summary>
    public required ulong Reread { get; init; }

    /// <summary>
    /// Gets how <see cref="Reread"/> compared with <see cref="Expected"/> and
    /// <see cref="Actual"/>. It is an inference, never an established cell state; word it through
    /// <see cref="RereadOutcomeText"/>.
    /// Source: <c>rq_error_record.reread_outcome</c>.
    /// </summary>
    public required RereadOutcome RereadOutcome { get; init; }

    /// <summary>
    /// Gets the zero-based index of the step that found the error, in the plan's step array.
    /// Source: <c>rq_error_record.step_index</c>.
    /// </summary>
    public required int StepIndex { get; init; }

    /// <summary>
    /// Gets the name of the step at <see cref="StepIndex"/>, exactly as the plan defines it and the
    /// UI shows it. <c>rq_error_record</c> carries only the index; the wrapper takes the name from
    /// its copy of the step definitions.
    /// </summary>
    public required string StepName { get; init; }

    /// <summary>
    /// Gets the zero-based index of the pass, within the step, that found the error.
    /// Source: <c>rq_error_record.pass_index</c>.
    /// </summary>
    public required int PassIndex { get; init; }

    /// <summary>
    /// Gets the verification access that found the mismatch, which identifies the pass phase the
    /// error came from.
    /// Source: <c>rq_error_record.access_type</c>.
    /// </summary>
    public required AccessType AccessType { get; init; }

    /// <summary>
    /// Gets the time from the end of the stream that last wrote the word's range to the
    /// verification load, including any hold. It is exact for steady-state and drain turnaround
    /// blocks alike. A hardware prefetch fill after the line's flush can precede the load, so the
    /// value can overstate the data's age by that interval.
    /// Source: <c>rq_error_record.time_since_write_us</c>, in microseconds.
    /// </summary>
    public required TimeSpan TimeSinceWrite { get; init; }

    /// <summary>
    /// Gets the test time at which the error was confirmed, measured from the run entering
    /// Running, the instant preparation completed.
    /// Source: <c>rq_error_record.time_into_test_ms</c>, in milliseconds.
    /// </summary>
    public required TimeSpan TimeIntoTest { get; init; }

    /// <summary>
    /// Gets the zero-based index of the worker that verified the word.
    /// Source: <c>rq_error_record.worker_index</c>.
    /// </summary>
    public required int WorkerIndex { get; init; }

    /// <summary>
    /// Gets the worker's logical processor number within its <see cref="ProcessorGroup"/>.
    /// Source: <c>rq_error_record.logical_processor</c>.
    /// </summary>
    public required int LogicalProcessor { get; init; }

    /// <summary>
    /// Gets the processor group of the worker's logical processor.
    /// Source: <c>rq_error_record.processor_group</c>.
    /// </summary>
    public required int ProcessorGroup { get; init; }

    /// <summary>
    /// Gets the number of workers active in the step: every worker in an <c>All</c>-thread step,
    /// half of them in a <c>Half</c>-thread step. The Load rule uses it to separate the two.
    /// Source: <c>rq_error_record.active_workers</c>.
    /// </summary>
    public required int ActiveWorkers { get; init; }

    /// <summary>
    /// Gets the flipped bits: <see cref="Expected"/> XOR <see cref="Actual"/>. Computed, never
    /// stored, so it cannot disagree with the values it derives from.
    /// </summary>
    public ulong BitMask => Expected ^ Actual;

    /// <summary>
    /// Gets the indices, 0 to 63 in ascending order, of the set bits of <see cref="BitMask"/>.
    /// Empty when <see cref="Expected"/> equals <see cref="Actual"/>.
    /// </summary>
    /// <remarks>
    /// Each access computes and allocates a new array. Nothing is cached, because a cached field
    /// would take part in the record's value equality. Hot loops should read
    /// <see cref="BitMask"/> directly.
    /// </remarks>
    public IReadOnlyList<int> BitPositions
    {
        get
        {
            ulong remaining = BitMask;
            int[] positions = new int[BitOperations.PopCount(remaining)];
            for (int i = 0; remaining != 0; i++)
            {
                positions[i] = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
            }

            return positions;
        }
    }

    /// <summary>
    /// Gets the address of the 8-byte word that holds <see cref="Address"/>:
    /// <c>Address &amp; ~7</c>. It is the grouping key shared with <c>ErrorGrouping</c>.
    /// </summary>
    public ulong WordAddress => Address & ~7UL;
}
