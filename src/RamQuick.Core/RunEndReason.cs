namespace RamQuick.Core;

/// <summary>
/// Why a run reached its terminal state. <c>RunStateMachine</c> resolves the terminal state
/// from this reason alone, so every component reads one precedence rule.
/// </summary>
/// <remarks>
/// <para>
/// The member names are a serialization contract: the JSON summary writes <c>run.endReason</c>
/// as the member name, and summaries recovered from a journal carry
/// <see cref="PreviousRunUnfinished"/>. Never rename a member.
/// </para>
/// <para>
/// The numeric values 0 to 8 are fixed for any persisted numeric form. They are <b>not</b> the
/// native <c>end_reason</c> codes of <c>ramquick_engine.h</c> (COMPLETED 1, FIRST_ERROR 2,
/// USER_STOP 3, THERMAL 4, HOST_FAULT 5, INSUFFICIENT_MEMORY 6, AFFINITY_FAILED 7,
/// INTERNAL_FAULT 8). Native codes are mapped by an explicit switch, never by a cast;
/// <c>INTERNAL_FAULT</c> and the <c>RQ_E_THREAD_CREATE</c> result both map to
/// <see cref="EngineFault"/>.
/// </para>
/// </remarks>
public enum RunEndReason
{
    /// <summary>
    /// Every step of the plan ran. The run is Passed when the true error total is 0, otherwise
    /// Failed.
    /// </summary>
    Completed = 0,

    /// <summary>
    /// Stop on error was on and a worker confirmed the first mismatch. The run is Failed.
    /// </summary>
    FirstError = 1,

    /// <summary>
    /// The user pressed Stop, or closed the window, while the run was Preparing or Running.
    /// The run is Aborted, and any errors already counted are still reported.
    /// </summary>
    UserStop = 2,

    /// <summary>
    /// A fresh, available reading reached CPU ≥ 95 °C or RAM ≥ 85 °C while the run was Running.
    /// The run is Aborted.
    /// </summary>
    Thermal = 3,

    /// <summary>
    /// Locking the region kept falling short until the size dropped below 1 GiB. The run is
    /// Aborted.
    /// </summary>
    InsufficientMemory = 4,

    /// <summary>
    /// A worker thread could not be pinned to its logical processor during preparation, so no
    /// verdict is produced from unpinned workers. The run is Aborted.
    /// </summary>
    AffinityFailed = 5,

    /// <summary>
    /// The host side failed the run: a progress journal write failed or timed out, or a managed
    /// engine callback threw. The run is Aborted.
    /// </summary>
    HostFault = 6,

    /// <summary>
    /// The engine faulted: native <c>internal_fault</c>, an <c>RQ_E_INTERNAL</c> result, or an
    /// <c>RQ_E_THREAD_CREATE</c> result. The run is Aborted.
    /// </summary>
    EngineFault = 7,

    /// <summary>
    /// Launch-time recovery found a progress journal with no <c>result</c> record, meaning the
    /// previous run never finished. The run is Failed; this reason is reached only from Idle.
    /// </summary>
    PreviousRunUnfinished = 8,
}
