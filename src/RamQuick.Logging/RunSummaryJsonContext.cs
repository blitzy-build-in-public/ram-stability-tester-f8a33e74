using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamQuick.Logging;

/// <summary>
/// Converts <see cref="DateTimeOffset"/> values to and from ISO 8601 UTC strings with millisecond
/// precision and a <c>Z</c> suffix, for example <c>"2026-10-28T14:05:51.120Z"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Writing converts the value to UTC and truncates it to whole milliseconds, so every timestamp in the
/// journal and the summaries has one exact representation. Reading accepts any ISO 8601 date and time; text
/// without an offset is taken as UTC, and the result always carries offset zero.
/// </para>
/// <para>
/// Both JSON contexts register this converter, and System.Text.Json applies it to
/// <see cref="Nullable{T}"/> values of <see cref="DateTimeOffset"/> as well, where <c>null</c> stays
/// <c>null</c>.
/// </para>
/// </remarks>
public sealed class UtcTimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string WriteFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const DateTimeStyles ReadStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Reads an ISO 8601 timestamp string as a UTC <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="reader">The reader, positioned on the value's token.</param>
    /// <param name="typeToConvert">The type being converted, <see cref="DateTimeOffset"/>.</param>
    /// <param name="options">The serializer options in use.</param>
    /// <returns>The timestamp, with offset zero.</returns>
    /// <exception cref="JsonException">
    /// The token is not a string, or the string is not a date and time.
    /// </exception>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A timestamp must be a JSON string, not a {reader.TokenType} token.");
        }

        // TryParse rather than Parse: System.Text.Json passes a converter's FormatException through
        // unwrapped, which would break the never-throwing journal and summary parsers built on this
        // converter. A JsonException is reported as malformed JSON, with the document path.
        string? text = reader.GetString();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, ReadStyles, out DateTimeOffset value))
        {
            throw new JsonException($"'{text}' is not an ISO 8601 timestamp.");
        }

        return value;
    }

    /// <summary>
    /// Writes <paramref name="value"/> as a UTC ISO 8601 string with millisecond precision and a <c>Z</c>
    /// suffix.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The timestamp; any sub-millisecond part is truncated.</param>
    /// <param name="options">The serializer options in use.</param>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToUniversalTime().ToString(WriteFormat, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The System.Text.Json source-generated serialization contract of the JSON summary,
/// <c>&lt;base&gt;-summary.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Options: property names are camelCased, the document is indented for reading, and
/// <see cref="JsonIgnoreCondition.Never"/> writes every <c>null</c> rather than omitting it, because a
/// <c>null</c> marks a value the run never captured. Timestamps go through
/// <see cref="UtcTimestampJsonConverter"/>.
/// </para>
/// <para>
/// Value types: every quantity (bytes, counts, offsets, durations, percentages, temperatures) is a JSON
/// number, and the summary records never hold an enumeration, only its member name as a string. Addresses,
/// bit masks and raw 64-bit data words are held by the summary records as strings already formatted by
/// <see cref="SummaryFormat.Hex64"/>, so no <see cref="ulong"/> converter is registered here: one would
/// apply to every <see cref="ulong"/>, byte counts and error totals included, and wrongly write them as hex
/// text.
/// </para>
/// <para>
/// Use <see cref="RunSummaryJson"/> rather than this context directly; it applies the schema checks a
/// summary read back must pass.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = new[] { typeof(UtcTimestampJsonConverter) })]
[JsonSerializable(typeof(RunSummary))]
public sealed partial class RunSummaryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The System.Text.Json source-generated serialization contract of the progress journal's JSON Lines
/// records.
/// </summary>
/// <remarks>
/// Property names are camelCased, each record is written on one line, enumerations are written by member
/// name (flags as a comma-separated list, for example <c>"RowTimings, ActivationWindow"</c>), and timestamps
/// go through <see cref="UtcTimestampJsonConverter"/>. The derived record types are reached through the
/// <see cref="JsonDerivedTypeAttribute"/> list on <see cref="JournalRecord"/>, and are serialized only
/// through the base type, by <see cref="JournalLineSerializer"/>, so that the <c>"t"</c> discriminator
/// leads every line.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UseStringEnumConverter = true,
    Converters = new[] { typeof(UtcTimestampJsonConverter) })]
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Serializes and reads back the JSON summary of a run through <see cref="RunSummaryJsonContext"/>.
/// </summary>
/// <remarks>
/// Stateless and thread-safe. No reflection-based serialization is used.
/// </remarks>
public static class RunSummaryJson
{
    /// <summary>
    /// Serializes a summary as the indented JSON document published as <c>&lt;base&gt;-summary.json</c>.
    /// </summary>
    /// <param name="summary">The summary to serialize.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A <see cref="double"/> member is NaN or infinite, which JSON cannot represent;
    /// <see cref="RunSummary.Build"/> never produces one.
    /// </exception>
    public static string Serialize(RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return JsonSerializer.Serialize(summary, RunSummaryJsonContext.Default.RunSummary);
    }

    /// <summary>
    /// Reads a summary document back, accepting it only when it is a schema version
    /// <see cref="RunSummary.CurrentSchemaVersion"/> summary that names its run.
    /// </summary>
    /// <param name="json">The document text.</param>
    /// <param name="summary">
    /// The summary when the method returns <see langword="true"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="json"/> parses as a summary whose
    /// <see cref="RunSummary.SchemaVersion"/> is <see cref="RunSummary.CurrentSchemaVersion"/> and whose
    /// <c>run.id</c> is present; <see langword="false"/> for blank, malformed or mistyped JSON, another
    /// schema version, or a missing run or run id. Never throws.
    /// </returns>
    /// <remarks>
    /// Crash recovery uses it to decide whether a final summary already on disk parses with the run id of
    /// the journal being recovered; the caller compares <see cref="SummaryRun.Id"/>.
    /// </remarks>
    public static bool TryDeserialize(string json, [NotNullWhen(true)] out RunSummary? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        RunSummary? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, RunSummaryJsonContext.Default.RunSummary);
        }
        catch (Exception exception) when (JsonCodec.IsUnreadableText(exception))
        {
            return false;
        }

        // The init accessors turn an omitted or null run, and an omitted or null id, into empty values,
        // so a missing run id reads back as empty text.
        if (parsed is null
            || parsed.SchemaVersion != RunSummary.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(parsed.Run.Id))
        {
            return false;
        }

        summary = parsed;
        return true;
    }
}

/// <summary>
/// Serializes progress-journal records to single JSON lines and parses them back, through
/// <see cref="JournalJsonContext"/>.
/// </summary>
/// <remarks>
/// Stateless and thread-safe. No reflection-based serialization is used.
/// </remarks>
public static class JournalLineSerializer
{
    /// <summary>
    /// Serializes a record as one journal line, its <c>"t"</c> discriminator first.
    /// </summary>
    /// <param name="record">The record to serialize.</param>
    /// <returns>
    /// The JSON object on a single line, without a terminator. Control characters inside strings are
    /// escaped, so the line never contains <c>\r</c> or <c>\n</c>.
    /// </returns>
    /// <remarks>
    /// Always serializes through the <see cref="JournalRecord"/> base type: .NET 8 System.Text.Json reads a
    /// polymorphic object only when its discriminator is the first property, and only the base type's
    /// contract writes the discriminator at all.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="record"/> is of a type <see cref="JournalRecord"/> does not list as a derived type.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A <see cref="double"/> member is NaN or infinite, which JSON cannot represent.
    /// </exception>
    public static string Serialize(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(record, JournalJsonContext.Default.JournalRecord);
    }

    /// <summary>
    /// Parses one journal line.
    /// </summary>
    /// <param name="line">The line, without its terminator.</param>
    /// <param name="record">
    /// The record when the method returns <see langword="true"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="line"/> holds a record of a known type;
    /// <see langword="false"/> for a blank, torn or otherwise malformed line, a line whose <c>"t"</c>
    /// discriminator is missing, not first or unknown, and a line whose values have the wrong JSON types.
    /// Never throws.
    /// </returns>
    public static bool TryParse(string line, [NotNullWhen(true)] out JournalRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            record = JsonSerializer.Deserialize(line, JournalJsonContext.Default.JournalRecord);
        }
        catch (Exception exception) when (JsonCodec.IsUnreadableText(exception))
        {
            record = null;
            return false;
        }

        return record is not null;
    }

    /// <summary>
    /// Parses a journal's lines in the order written, keeping every line that holds a record.
    /// </summary>
    /// <param name="lines">The journal's lines, as <see cref="IJournalStorage.ReadLines"/> returns them.</param>
    /// <returns>The parsed records, in order; a read-only list.</returns>
    /// <remarks>
    /// Recovery is best-effort: a line <see cref="TryParse"/> rejects is skipped rather than failing the
    /// whole journal. That covers the torn final line a crash leaves when it interrupts an append, and any
    /// other damaged line. Element <see langword="null"/>s are skipped the same way.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<JournalRecord> ParseAll(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var records = new List<JournalRecord>(lines.Count);
        foreach (string line in lines)
        {
            if (TryParse(line, out JournalRecord? record))
            {
                records.Add(record);
            }
        }

        return records.AsReadOnly();
    }
}

/// <summary>
/// The failure classification shared by the never-throwing parsers.
/// </summary>
internal static class JsonCodec
{
    /// <summary>
    /// Returns whether <paramref name="exception"/> reports text that cannot be read as the requested
    /// contract, as opposed to a programming error.
    /// </summary>
    /// <param name="exception">The exception System.Text.Json raised.</param>
    /// <returns>
    /// <see langword="true"/> for a <see cref="JsonException"/> (malformed JSON, a value of the wrong JSON
    /// type, an unknown discriminator or an unreadable timestamp), a <see cref="NotSupportedException"/>
    /// (an object without a usable discriminator, which would need the abstract base type to be created)
    /// and an <see cref="ArgumentException"/> (text that is not valid UTF-16 and so cannot be transcoded to
    /// UTF-8).
    /// </returns>
    internal static bool IsUnreadableText(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;
}
