namespace RamQuick.Core;

/// <summary>
/// The kind of address an <c>ErrorRecord</c> carries, mirroring <c>RQ_ADDRESS_KIND_*</c> in
/// <c>ramquick_engine.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// The numeric values equal the native codes, so <c>RamQuick.Interop</c> casts the native
/// <c>uint32_t</c> to this type directly. Zero is invalid on both sides and has no member.
/// </para>
/// <para>
/// Member names are written by name into the summary JSON (<c>memory.addressKind</c>,
/// <c>errors.records[].addressKind</c>). Never rename a member.
/// </para>
/// </remarks>
public enum AddressKind
{
    /// <summary>
    /// The virtual address, in the engine process, of the mismatching 8-byte word. No documented
    /// user-mode Windows API yields physical addresses, so every v1 record carries this kind, and
    /// the UI and logs label the value "virtual address".
    /// </summary>
    Virtual = 1,

    /// <summary>
    /// A physical address. Reserved for a later version; v1 never produces it.
    /// </summary>
    Physical = 2,
}

/// <summary>
/// The verification access that found a mismatch, mirroring <c>RQ_ACCESS_*</c> in
/// <c>ramquick_engine.h</c>. Each value identifies the pass phase the error came from.
/// </summary>
/// <remarks>
/// <para>
/// The numeric values equal the native codes, so <c>RamQuick.Interop</c> casts the native
/// <c>uint32_t</c> to this type directly. Zero is invalid on both sides and has no member.
/// </para>
/// <para>
/// Member names are written by name into the summary JSON (<c>errors.records[].accessType</c>).
/// Never rename a member.
/// </para>
/// </remarks>
public enum AccessType
{
    /// <summary>
    /// Windowed verification: each worker writes a 64 MiB window, then verifies it, so the time
    /// since write stays short however large the slice. Used by every step without a hold that is
    /// neither moving inversion nor turnaround.
    /// </summary>
    VerifyAfterWrite = 1,

    /// <summary>
    /// Whole-slice verification after an idle hold: the slice is written, left untouched for the
    /// step's hold time, then verified. This is the signature the Hold rule looks for when it
    /// points to refresh or retention.
    /// </summary>
    VerifyAfterHold = 2,

    /// <summary>
    /// A verify-and-write sweep of a moving-inversion pass: each word is verified and its
    /// complement (or the base value, on the descending sweep) written in the same visit.
    /// </summary>
    ReadModifyWrite = 3,

    /// <summary>
    /// Turnaround verification: block <c>i − 4</c> is verified while block <c>i</c> is being
    /// written, and the last four blocks of each window are verified in the drain that follows.
    /// This is the signature the Turnaround rule looks for when it points to write-to-read
    /// timings.
    /// </summary>
    TurnaroundRead = 4,
}

/// <summary>
/// How the single re-read of a mismatching word compared with the expected and first-read values,
/// mirroring <c>RQ_REREAD_*</c> in <c>ramquick_engine.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every mismatching word is re-read once after a fence, a flush of its cache line and a second
/// fence, so the re-read cannot be served by a cached copy older than that flush. The
/// classification compares the raw <c>Expected</c>, <c>Actual</c> and <c>Reread</c> values, which
/// are always kept. Every outcome is a confirmed error; the outcome only informs the inference
/// worded by <see cref="RereadOutcomeText"/>.
/// </para>
/// <para>
/// The numeric values equal the native codes, so <c>RamQuick.Interop</c> casts the native
/// <c>uint32_t</c> to this type directly. Zero is invalid on both sides and has no member. Member
/// names are written by name into the summary JSON (<c>errors.records[].rereadOutcome</c>). Never
/// rename a member.
/// </para>
/// </remarks>
public enum RereadOutcome
{
    /// <summary>
    /// The re-read returned the expected value: the first read was wrong but the stored data
    /// appears intact.
    /// </summary>
    RereadMatchesExpected = 1,

    /// <summary>
    /// The re-read returned the same wrong value as the first read.
    /// </summary>
    RereadRepeatsActual = 2,

    /// <summary>
    /// The re-read returned a value equal to neither the expected value nor the first read.
    /// </summary>
    RereadDiffers = 3,
}

/// <summary>
/// The single wording of a <see cref="RereadOutcome"/>, shared by the UI and the logs.
/// </summary>
/// <remarks>
/// <para>
/// Software cannot observe the state of a DRAM cell, so the text always states an inference,
/// never an established fault location. A repeatable read-path fault, for example, produces the
/// same values as a write or storage fault (<see cref="RereadOutcome.RereadRepeatsActual"/>),
/// which is why every inference reads "likely".
/// </para>
/// <para>
/// The strings are a display and log contract: the first-error message and the text summary
/// embed <see cref="Describe"/>, and their tests assert the exact wording.
/// </para>
/// </remarks>
public static class RereadOutcomeText
{
    private const string MatchesExpectedInference = "likely read-path fault";
    private const string RepeatsActualInference = "likely write or storage fault";
    private const string DiffersInference = "likely transient fault";

    private const string MatchesExpectedObservation = "re-read returned the expected value";
    private const string RepeatsActualObservation = "repeated wrong value";
    private const string DiffersObservation = "re-read returned a third value";

    /// <summary>
    /// Returns the inferred fault class for <paramref name="outcome"/>, always worded as likely.
    /// </summary>
    /// <param name="outcome">The re-read outcome to interpret.</param>
    /// <returns>
    /// <c>"likely read-path fault"</c> for <see cref="RereadOutcome.RereadMatchesExpected"/>,
    /// <c>"likely write or storage fault"</c> for <see cref="RereadOutcome.RereadRepeatsActual"/>,
    /// and <c>"likely transient fault"</c> for <see cref="RereadOutcome.RereadDiffers"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="RereadOutcome"/> value.
    /// </exception>
    public static string Inference(RereadOutcome outcome) => outcome switch
    {
        RereadOutcome.RereadMatchesExpected => MatchesExpectedInference,
        RereadOutcome.RereadRepeatsActual => RepeatsActualInference,
        RereadOutcome.RereadDiffers => DiffersInference,
        _ => throw UndefinedOutcome(outcome),
    };

    /// <summary>
    /// Returns what the re-read showed for <paramref name="outcome"/>, without interpretation.
    /// </summary>
    /// <param name="outcome">The re-read outcome to describe.</param>
    /// <returns>
    /// <c>"re-read returned the expected value"</c> for
    /// <see cref="RereadOutcome.RereadMatchesExpected"/>, <c>"repeated wrong value"</c> for
    /// <see cref="RereadOutcome.RereadRepeatsActual"/>, and
    /// <c>"re-read returned a third value"</c> for <see cref="RereadOutcome.RereadDiffers"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="RereadOutcome"/> value.
    /// </exception>
    public static string Observation(RereadOutcome outcome) => outcome switch
    {
        RereadOutcome.RereadMatchesExpected => MatchesExpectedObservation,
        RereadOutcome.RereadRepeatsActual => RepeatsActualObservation,
        RereadOutcome.RereadDiffers => DiffersObservation,
        _ => throw UndefinedOutcome(outcome),
    };

    /// <summary>
    /// Returns the observation followed by the inference, as shown in parentheses in the
    /// first-error message, for example <c>"repeated wrong value: likely write or storage fault"</c>.
    /// </summary>
    /// <param name="outcome">The re-read outcome to describe.</param>
    /// <returns>
    /// <see cref="Observation"/> and <see cref="Inference"/> of <paramref name="outcome"/>, joined
    /// by <c>": "</c>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="RereadOutcome"/> value.
    /// </exception>
    public static string Describe(RereadOutcome outcome) => $"{Observation(outcome)}: {Inference(outcome)}";

    private static ArgumentOutOfRangeException UndefinedOutcome(RereadOutcome outcome) =>
        new(nameof(outcome), outcome, "The re-read outcome is not a defined value.");
}
