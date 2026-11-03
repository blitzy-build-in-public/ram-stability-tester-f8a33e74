namespace RamQuick.Analysis;

/// <summary>
/// The plain-language suggestion and the short title that accompany each <see cref="CauseCategory"/> in a cause
/// hint. The UI result panel, the readable text summary and the JSON summary all take their wording from here,
/// so every channel shows the same text.
/// </summary>
/// <remarks>
/// Every text is a hint, never a diagnosis: none states a cause as certain. The hint label and the UI's "Hint:"
/// prefix and list header are owned elsewhere and are not part of these texts.
/// </remarks>
public static class Suggestions
{
    /// <summary>The suggestion for <see cref="CauseCategory.VoltageSignalIntegrity"/>.</summary>
    /// <remarks>
    /// The text opens with the bit-lane signature even when the category ranks through the step-mechanism prior
    /// alone, for example after a single error in a moving-inversion step. The wording is fixed by the product
    /// specification; the hint's evidence line states the evidence actually found.
    /// </remarks>
    public const string VoltageSignalIntegritySuggestion =
        "Errors keep hitting the same data bit. Try slightly more DRAM VDD/VDDQ, revisit drive strength or ODT (RTT), or lower the frequency one step.";

    /// <summary>The suggestion for <see cref="CauseCategory.RefreshRetention"/>.</summary>
    public const string RefreshRetentionSuggestion =
        "Errors appear only after memory sat idle. Lower tREFI, raise tRFC, or improve DIMM cooling.";

    /// <summary>The suggestion for <see cref="CauseCategory.RowBankTiming"/>.</summary>
    public const string RowBankTimingSuggestion =
        "Errors appear under rapid row and bank switching. Loosen tRCD, tRP or tRAS, or tFAW/tRRD.";

    /// <summary>The suggestion for <see cref="CauseCategory.WriteReadTurnaround"/>.</summary>
    public const string WriteReadTurnaroundSuggestion =
        "Errors appear when data is read right after being written. Loosen tWR, tWTR or tRTP (and tCWL).";

    /// <summary>The suggestion for <see cref="CauseCategory.ImcPowerDelivery"/>.</summary>
    public const string ImcPowerDeliverySuggestion =
        "Errors appear only with every core loading memory. Raise SoC/IMC voltage (VDDIO or VDD2) slightly, or check CPU power delivery.";

    /// <summary>The suggestion for <see cref="CauseCategory.RankOrDimm"/>.</summary>
    /// <remarks>
    /// Error offsets are virtual and physical memory is interleaved across channels, ranks and banks, so the text
    /// speaks of "one part of memory" and never names a specific rank or DIMM.
    /// </remarks>
    public const string RankOrDimmSuggestion =
        "Errors concentrate in one part of memory. One rank or DIMM may be marginal; test the DIMMs one at a time or reseat them.";

    /// <summary>The title for <see cref="CauseCategory.VoltageSignalIntegrity"/>.</summary>
    public const string VoltageSignalIntegrityTitle = "DRAM voltage / signal integrity";

    /// <summary>The title for <see cref="CauseCategory.RefreshRetention"/>.</summary>
    public const string RefreshRetentionTitle = "Refresh / retention";

    /// <summary>The title for <see cref="CauseCategory.RowBankTiming"/>.</summary>
    public const string RowBankTimingTitle = "Row/bank timings";

    /// <summary>The title for <see cref="CauseCategory.WriteReadTurnaround"/>.</summary>
    public const string WriteReadTurnaroundTitle = "Write/read turnaround";

    /// <summary>The title for <see cref="CauseCategory.ImcPowerDelivery"/>.</summary>
    public const string ImcPowerDeliveryTitle = "IMC voltage / power delivery";

    /// <summary>The title for <see cref="CauseCategory.RankOrDimm"/>.</summary>
    /// <remarks>A weak locality hint: it names a part of the tested memory, never a specific rank or DIMM.</remarks>
    public const string RankOrDimmTitle = "One part of memory (rank or DIMM)";

    /// <summary>
    /// Returns the plain-language suggestion shown with a cause hint of the given category.
    /// </summary>
    /// <param name="category">The cause category of the hint.</param>
    /// <returns>One of the <c>…Suggestion</c> constants of this class.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is not a defined <see cref="CauseCategory"/> member.
    /// </exception>
    public static string For(CauseCategory category) => category switch
    {
        CauseCategory.VoltageSignalIntegrity => VoltageSignalIntegritySuggestion,
        CauseCategory.RefreshRetention => RefreshRetentionSuggestion,
        CauseCategory.RowBankTiming => RowBankTimingSuggestion,
        CauseCategory.WriteReadTurnaround => WriteReadTurnaroundSuggestion,
        CauseCategory.ImcPowerDelivery => ImcPowerDeliverySuggestion,
        CauseCategory.RankOrDimm => RankOrDimmSuggestion,
        _ => throw UndefinedCategory(category),
    };

    /// <summary>
    /// Returns the short title that names a cause hint's category, for example "Row/bank timings".
    /// </summary>
    /// <param name="category">The cause category of the hint.</param>
    /// <returns>One of the <c>…Title</c> constants of this class.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is not a defined <see cref="CauseCategory"/> member.
    /// </exception>
    public static string Title(CauseCategory category) => category switch
    {
        CauseCategory.VoltageSignalIntegrity => VoltageSignalIntegrityTitle,
        CauseCategory.RefreshRetention => RefreshRetentionTitle,
        CauseCategory.RowBankTiming => RowBankTimingTitle,
        CauseCategory.WriteReadTurnaround => WriteReadTurnaroundTitle,
        CauseCategory.ImcPowerDelivery => ImcPowerDeliveryTitle,
        CauseCategory.RankOrDimm => RankOrDimmTitle,
        _ => throw UndefinedCategory(category),
    };

    private static ArgumentOutOfRangeException UndefinedCategory(CauseCategory category) =>
        new(nameof(category), category, "The value is not a defined CauseCategory member.");
}
