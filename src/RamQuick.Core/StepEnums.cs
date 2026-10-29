namespace RamQuick.Core;

/// <summary>
/// The data pattern a step writes and verifies. Values equal the native <c>RQ_PATTERN_*</c>
/// constants of <c>ramquick_engine.h</c>, so <c>RamQuick.Interop</c> casts a member directly into
/// <c>rq_step_def.pattern</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every value is a stateless function of word index, pass and seed, so verification and re-reads
/// regenerate the expected word without storing it.
/// </para>
/// <para>
/// These are logical values. Burst ordering, ECC and memory-controller data scrambling decide what
/// the DQ lines actually carry, so no pattern guarantees a particular toggle rate on the bus.
/// </para>
/// <para>
/// There is no zero member because 0 is invalid in the native API. Member names are written by name
/// into the journal and the summary's <c>steps[].pattern</c>; never rename a member.
/// </para>
/// </remarks>
public enum PatternKind
{
    /// <summary>
    /// <c>0x5555…</c> and <c>0xAAAA…</c> alternating per 64-bit word, inverted every pass, so the step
    /// covers both the checkerboard and its inverse. Native <c>RQ_PATTERN_CHECKERBOARD</c>.
    /// </summary>
    Checkerboard = 1,

    /// <summary>
    /// MemTest86-style moving inversion over walking bits: walking ones on even passes and walking
    /// zeros on odd passes, one set or cleared bit rotating with word index. Each pass writes the base
    /// ascending, then ascending verifies it and writes its complement, then descending verifies the
    /// complement and writes the base. Native <c>RQ_PATTERN_MOVING_INVERSION_WALKING</c>.
    /// </summary>
    MovingInversionWalking = 2,

    /// <summary>
    /// The same moving-inversion sweeps as <see cref="PatternKind.MovingInversionWalking"/>, over
    /// <see cref="PatternKind.Random"/> base data. Native <c>RQ_PATTERN_MOVING_INVERSION_RANDOM</c>.
    /// </summary>
    MovingInversionRandom = 3,

    /// <summary>
    /// SplitMix64 of seed, word index and pass. Native <c>RQ_PATTERN_RANDOM</c>.
    /// </summary>
    Random = 4,

    /// <summary>
    /// Logical data complemented between consecutive 64-bit words and between the 32-bit halves of
    /// each word, alternating per pass. It aims at maximum toggling on 64-bit (DDR4 channel) and
    /// 32-bit (DDR5 sub-channel) beats. Native <c>RQ_PATTERN_HIGH_TOGGLE</c>.
    /// </summary>
    HighToggle = 5,
}

/// <summary>
/// The order in which a step visits each worker's slice. Values equal the native <c>RQ_ORDER_*</c>
/// constants of <c>ramquick_engine.h</c>, so <c>RamQuick.Interop</c> casts a member directly into
/// <c>rq_step_def.access_order</c>.
/// </summary>
/// <remarks>
/// <para>
/// Orders act on virtual addresses. Page mapping, channel and rank interleaving and the controller's
/// bank hashing are invisible to software, so the strided and turnaround orders are exposure
/// heuristics: they make frequent row, bank and rank changes and write-to-read switches likely, and
/// guarantee no particular physical sequence.
/// </para>
/// <para>
/// <see cref="PatternKind.MovingInversionWalking"/> and <see cref="PatternKind.MovingInversionRandom"/>
/// imply ascending-then-descending whole-slice sweeps whatever the order, and the engine ignores this
/// value for them.
/// </para>
/// <para>
/// There is no zero member because 0 is invalid in the native API. Member names are written by name
/// into the summary's <c>steps[].accessOrder</c>; never rename a member.
/// </para>
/// </remarks>
public enum AccessOrder
{
    /// <summary>
    /// Ascending addresses, window by window. Native <c>RQ_ORDER_SEQUENTIAL_ASCENDING</c>.
    /// </summary>
    SequentialAscending = 1,

    /// <summary>
    /// Descending addresses, window by window. Native <c>RQ_ORDER_SEQUENTIAL_DESCENDING</c>.
    /// </summary>
    SequentialDescending = 2,

    /// <summary>
    /// 64-byte lines visited at power-of-two virtual strides of 8 KiB, 64 KiB and 256 KiB, cycling
    /// per pass. The start offset advances one line per sweep until every line of the window has been
    /// visited exactly once per phase. Native <c>RQ_ORDER_BANK_STRIDE</c>.
    /// </summary>
    BankStride = 3,

    /// <summary>
    /// A seeded permutation of 4 KiB blocks. Steps with a hold also write their whole slice in this
    /// order before waiting. Native <c>RQ_ORDER_RANDOM_BLOCKS</c>.
    /// </summary>
    RandomBlocks = 4,

    /// <summary>
    /// Per window of 4 KiB blocks: write block <c>i</c>, then flush and verify block <c>i − 4</c>, so
    /// reads closely follow writes to nearby addresses. Blocks 0–3 are written without verification at
    /// start-up, and the last four are verified after the final write (drain), so every block is
    /// verified exactly once per pass. Native <c>RQ_ORDER_TURNAROUND</c>.
    /// </summary>
    Turnaround = 5,
}

/// <summary>
/// How many workers a step keeps active. Values equal the native <c>RQ_THREADS_*</c> constants of
/// <c>ramquick_engine.h</c>, so <c>RamQuick.Interop</c> casts a member directly into
/// <c>rq_step_def.thread_mode</c>.
/// </summary>
/// <remarks>
/// There is no zero member because 0 is invalid in the native API. Member names are written by name
/// into the summary's <c>steps[].threadMode</c>; never rename a member.
/// </remarks>
public enum ThreadMode
{
    /// <summary>
    /// Every worker is active, one pinned to each logical processor, each on its own slice.
    /// Native <c>RQ_THREADS_ALL</c>.
    /// </summary>
    All = 1,

    /// <summary>
    /// Only even-indexed workers are active, each covering its own slice and its odd partner's, so
    /// the same memory is tested at half the thread load. It is the contrast mode for the analyzer's
    /// load rule: errors under <see cref="ThreadMode.All"/> that are absent under this mode suggest
    /// SoC/IMC voltage or power delivery. Native <c>RQ_THREADS_HALF</c>.
    /// </summary>
    Half = 2,
}

/// <summary>
/// The DRAM instability mechanisms a step targets, as flags.
/// </summary>
/// <remarks>
/// <para>
/// A step's <c>PrimaryMechanism</c> is exactly one single-bit value; it feeds the cause analyzer's
/// step prior and the step-to-mechanism map. A step's <c>Mechanisms</c> combines every mechanism the
/// step targets and always includes the primary. <c>RamQuick.Plans.TestPlanValidator</c> enforces
/// both rules.
/// </para>
/// <para>
/// The member names are a contract: <c>docs/METHODOLOGY.md</c> lists them in its step-to-mechanism
/// map, <c>TestPlanCatalogTests</c> asserts that every member appears there,
/// <c>RamQuick.Analysis.MechanismCategoryMap</c> maps each one to a cause category, and the summary
/// writes <c>steps[].primaryMechanism</c> by name. Never rename a member, and add no composite
/// member, because consumers enumerate the single-bit values.
/// </para>
/// <para>
/// The engine never sees these values: <c>rq_step_def</c> has no mechanism field.
/// </para>
/// </remarks>
[Flags]
public enum InstabilityMechanism
{
    /// <summary>
    /// No mechanism: the empty combination. Never valid as a step's primary mechanism.
    /// </summary>
    None = 0,

    /// <summary>
    /// Signal integrity at high frequency: ODT, RTT and drive strength. Typical signature: errors
    /// recurring on one or a few bit lanes, data-pattern dependent, within seconds of heavy traffic.
    /// </summary>
    SignalIntegrity = 1 << 0,

    /// <summary>
    /// Insufficient DRAM VDD/VDDQ: cell and I/O margin. Typical signature: scattered single-bit flips,
    /// often on one lane for VDDQ, worse when hot.
    /// </summary>
    VddVddqVoltage = 1 << 1,

    /// <summary>
    /// Row timings tRCD, tRP and tRAS. Typical signature: errors only under row-miss-heavy orders,
    /// at scattered addresses.
    /// </summary>
    RowTimings = 1 << 2,

    /// <summary>
    /// Activation-window timings tFAW and tRRD. Typical signature: errors under dense activations
    /// across banks in short windows.
    /// </summary>
    ActivationWindow = 1 << 3,

    /// <summary>
    /// Bank, rank and channel switching. Typical signature: errors under orders that cross banks and
    /// ranks, unaffected by delay.
    /// </summary>
    BankRankChannelSwitching = 1 << 4,

    /// <summary>
    /// Write timings tWR, tWTR, tRTP and tCWL. Typical signature: errors on reads issued just after
    /// writes to nearby addresses.
    /// </summary>
    WriteTimings = 1 << 5,

    /// <summary>
    /// Read/write bus turnaround. Typical signature: errors when reads follow writes to nearby
    /// addresses within about a millisecond.
    /// </summary>
    ReadWriteTurnaround = 1 << 6,

    /// <summary>
    /// Refresh timings tREFI and tRFC, and cell retention. Typical signature: too low a tRFC gives
    /// fast errors or a hang under any heavy traffic; too high a tREFI gives errors only on
    /// verification after an idle hold, worse when hot.
    /// </summary>
    RefreshTimings = 1 << 7,

    /// <summary>
    /// SoC/IMC voltage and power delivery. Typical signature: errors spread over addresses and bits,
    /// present at full thread load and absent at half load.
    /// </summary>
    SocImcVoltage = 1 << 8,

    /// <summary>
    /// Temperature sensitivity: warm-up failures. Typical signature: errors that appear only after
    /// warm-up and rise with DIMM temperature.
    /// </summary>
    TemperatureSensitivity = 1 << 9,
}
