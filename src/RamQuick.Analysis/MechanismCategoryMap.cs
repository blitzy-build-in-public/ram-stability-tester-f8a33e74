using System.Collections.Frozen;
using RamQuick.Core;

namespace RamQuick.Analysis;

/// <summary>
/// Maps each DRAM instability mechanism a step can target to the <see cref="CauseCategory"/> that the cause
/// analyzer's step-mechanism prior credits. A category's prior is a fixed fraction of the share of errors that came
/// from steps whose primary mechanism maps to it, so a single error still ranks the cause its failing step targets.
/// </summary>
/// <remarks>
/// <para>
/// The table holds exactly one entry per single-bit <see cref="InstabilityMechanism"/> member:
/// <c>VddVddqVoltage</c> and <c>SignalIntegrity</c> map to <see cref="CauseCategory.VoltageSignalIntegrity"/>;
/// <c>SocImcVoltage</c> to <see cref="CauseCategory.ImcPowerDelivery"/>; <c>RowTimings</c>,
/// <c>ActivationWindow</c> and <c>BankRankChannelSwitching</c> to <see cref="CauseCategory.RowBankTiming"/>;
/// <c>RefreshTimings</c> and <c>TemperatureSensitivity</c> to <see cref="CauseCategory.RefreshRetention"/>;
/// <c>WriteTimings</c> and <c>ReadWriteTurnaround</c> to <see cref="CauseCategory.WriteReadTurnaround"/>.
/// </para>
/// <para>
/// <see cref="CauseCategory.RankOrDimm"/> deliberately receives no prior: no step targets a rank or DIMM, so no
/// mechanism maps to it, and that category is evidenced by the address-cluster rule alone.
/// </para>
/// <para>
/// The table is built once and never mutated, so the analyzer stays pure: it holds immutable static data and no
/// mutable static state.
/// </para>
/// </remarks>
public static class MechanismCategoryMap
{
    /// <summary>
    /// Gets the immutable mechanism-to-category table: one entry for each of the ten single-bit
    /// <see cref="InstabilityMechanism"/> members, and none for <see cref="InstabilityMechanism.None"/> or any
    /// combined value.
    /// </summary>
    public static IReadOnlyDictionary<InstabilityMechanism, CauseCategory> Entries { get; } =
        new Dictionary<InstabilityMechanism, CauseCategory>
        {
            [InstabilityMechanism.VddVddqVoltage] = CauseCategory.VoltageSignalIntegrity,
            [InstabilityMechanism.SignalIntegrity] = CauseCategory.VoltageSignalIntegrity,
            [InstabilityMechanism.SocImcVoltage] = CauseCategory.ImcPowerDelivery,
            [InstabilityMechanism.RowTimings] = CauseCategory.RowBankTiming,
            [InstabilityMechanism.ActivationWindow] = CauseCategory.RowBankTiming,
            [InstabilityMechanism.BankRankChannelSwitching] = CauseCategory.RowBankTiming,
            [InstabilityMechanism.RefreshTimings] = CauseCategory.RefreshRetention,
            [InstabilityMechanism.TemperatureSensitivity] = CauseCategory.RefreshRetention,
            [InstabilityMechanism.WriteTimings] = CauseCategory.WriteReadTurnaround,
            [InstabilityMechanism.ReadWriteTurnaround] = CauseCategory.WriteReadTurnaround,
        }.ToFrozenDictionary();

    /// <summary>
    /// Returns the cause category that a step's primary mechanism credits through the step-mechanism prior.
    /// </summary>
    /// <param name="mechanism">
    /// The step's primary mechanism (<c>StepAttributes.PrimaryMechanism</c>). Only the primary feeds the prior; the
    /// step's secondary <c>Mechanisms</c> flags are never passed here, so a step credits exactly one category.
    /// </param>
    /// <returns>
    /// The mapped category when <paramref name="mechanism"/> is exactly one of the ten single-bit members; otherwise
    /// <see langword="null"/>, which covers <see cref="InstabilityMechanism.None"/>, a combination of flags (never a
    /// primary mechanism) and any undefined value. The method never throws.
    /// </returns>
    public static CauseCategory? Map(InstabilityMechanism mechanism) =>
        Entries.TryGetValue(mechanism, out CauseCategory category) ? category : null;
}
