namespace RamQuick.Analysis;

/// <summary>
/// A probable cause of memory errors that a cause hint can name. Each category groups the BIOS settings
/// whose margin may be insufficient when the errors show its signature; a category is a hint, never a diagnosis.
/// </summary>
/// <remarks>
/// <para>
/// The declaration order is not the ranking tie-break order. Ties between equal scores are broken in the
/// fixed order <c>RefreshRetention</c>, <c>WriteReadTurnaround</c>, <c>RowBankTiming</c>,
/// <c>ImcPowerDelivery</c>, <c>RankOrDimm</c>, <c>VoltageSignalIntegrity</c>, which is owned by
/// <c>ErrorAnalyzer.TieBreakOrder</c>; never derive it from these values.
/// </para>
/// <para>
/// Member names are written by name into the JSON summary (<c>causeHints.items[].category</c>), so they
/// are a persisted contract: never rename a member or change its value.
/// </para>
/// </remarks>
public enum CauseCategory
{
    /// <summary>
    /// Errors repeating on one data bit lane suggest a DRAM voltage or signal-integrity margin: VDD or VDDQ
    /// may be too low, drive strength or ODT (RTT) may need revisiting, or the frequency may be one step too high.
    /// Evidenced by the bit-lane rule and by steps targeting the <c>VddVddqVoltage</c> and
    /// <c>SignalIntegrity</c> mechanisms.
    /// </summary>
    VoltageSignalIntegrity = 0,

    /// <summary>
    /// Errors appearing only after memory sat idle suggest a refresh or retention margin: tREFI may be too
    /// high, tRFC too low, or the DIMMs too warm. Evidenced by the hold rule and by steps targeting the
    /// <c>RefreshTimings</c> and <c>TemperatureSensitivity</c> mechanisms.
    /// </summary>
    RefreshRetention = 1,

    /// <summary>
    /// Errors appearing under rapid row and bank switching suggest that row or activation timings may be too
    /// tight: tRCD, tRP or tRAS, or tFAW and tRRD. Evidenced by the bank-switching rule and by steps targeting
    /// the <c>RowTimings</c>, <c>ActivationWindow</c> and <c>BankRankChannelSwitching</c> mechanisms.
    /// </summary>
    RowBankTiming = 2,

    /// <summary>
    /// Errors appearing when data is read right after being written suggest that write-recovery or
    /// read/write turnaround timings may be too tight: tWR, tWTR or tRTP, and tCWL. Evidenced by the
    /// turnaround rule and by steps targeting the <c>WriteTimings</c> and <c>ReadWriteTurnaround</c> mechanisms.
    /// </summary>
    WriteReadTurnaround = 3,

    /// <summary>
    /// Errors appearing only with every core loading memory suggest that the memory controller may lack
    /// margin: SoC/IMC voltage (VDDIO or VDD2) may be too low, or CPU power delivery may be marginal.
    /// Evidenced by the load rule and by steps targeting the <c>SocImcVoltage</c> mechanism.
    /// </summary>
    ImcPowerDelivery = 4,

    /// <summary>
    /// Errors concentrated in one part of the tested memory suggest that one rank or DIMM may be marginal.
    /// Evidenced by the address-cluster rule only; no step mechanism maps here. This is a weak locality hint:
    /// offsets are virtual, and physical memory is interleaved across channels, ranks and banks, so a cluster
    /// does not identify a specific rank or DIMM.
    /// </summary>
    RankOrDimm = 5,
}
