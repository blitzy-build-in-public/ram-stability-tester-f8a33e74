using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RamQuick.Analysis;
using RamQuick.Logging;
using RamQuick.Plans;
using Xunit;

namespace RamQuick.IntegrationTests;

/// <summary>
/// The only automated end-to-end run of the production <c>ramquick_engine.dll</c>: <c>RamQuick.TestHost</c> drives
/// the production controller graph over it in a separate process, and the run's published summaries are checked
/// against schema version 1.
/// </summary>
/// <remarks>
/// <para>
/// This test process is bound to <c>ramquick_engine_test.dll</c> by <see cref="EngineBindingFixture"/>, and a resolved
/// P/Invoke is never rebound, so the production engine can only be exercised in another process. The host runs from
/// the isolated <c>TestHost\</c> folder with its own <c>RamQuick.Interop.dll</c> and the default binding.
/// </para>
/// <para>
/// The schema is the <c>RamQuick.Logging</c> summary records read through <see cref="RunSummaryJson"/>, plus the value
/// types of the published document: addresses, bit masks and raw data words are <c>"0x"</c> and 16 uppercase hex
/// digits, every quantity is a JSON number, and every timestamp is an ISO 8601 UTC string. No schema file exists.
/// </para>
/// <para>
/// The run writes only beneath its own <see cref="TempDataRoot"/> and needs no elevation.
/// </para>
/// </remarks>
public sealed partial class ProductionBindingTests
{
    private const string HostExecutableName = "RamQuick.TestHost.exe";
    private const string InteropAssemblyName = "RamQuick.Interop.dll";
    private const string ProductionEngineName = "ramquick_engine.dll";

    private const int RequestedRegionMiB = 256;
    private const int StepCount = 2;
    private const int StepSeconds = 10;
    private const ulong BytesPerMiB = 1UL << 20;
    private const ulong SliceGranularityBytes = 2 * BytesPerMiB;

    private const string PassVerdictLine = TestHostLauncher.VerdictPrefix + "PASS reason=Completed";
    private const string PublishedTextSeparator = " txt=";
    private const string SummaryJsonSuffix = "-summary.json";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string UtcSuffix = "Utc";

    private static readonly TimeSpan _hostTimeout = TimeSpan.FromSeconds(180);

    private static readonly string[] _runTimestampNames = { "startRequestedUtc", "startedUtc", "endedUtc", "verdictUtc" };

    private static readonly string[] _instructionSets = { "AVX-512", "AVX2", "SSE2" };

    private static readonly string[] _pageModes = { "Large", "Standard", "Mixed" };

    private static readonly string[] _stepQuantityNames =
    {
        "plannedSeconds", "actualSeconds", "overrunSeconds", "passes", "coveragePercent", "errors",
    };

    // Addresses, bit masks and raw 64-bit words, including the error groups' word address and bit-mask union.
    private static readonly HashSet<string> _hexValueNames = new(StringComparer.Ordinal)
    {
        "address", "wordAddress", "bitMask", "bitMaskUnion", "expected", "actual", "reread",
    };

    private static readonly string[] _quantitySuffixes = { "Bytes", "Seconds", "Percent", "Count", "Index", "MaxC" };

    // Quantities whose names carry none of the suffixes above.
    private static readonly HashSet<string> _quantityNames = new(StringComparer.Ordinal)
    {
        "schemaVersion", "passes", "errors", "count", "percent", "total", "index", "rank", "sequence",
        "regionOffset", "storedCap", "storedDistinctAddresses", "logicalProcessors", "processorGroups",
        "logicalProcessor", "processorGroup", "activeWorkers", "speedMTs", "channels", "dimms",
        "timeSinceWrite", "timeIntoTest", "firstTimeIntoTest", "lastTimeIntoTest",
    };

    // Paths a PASS summary always holds; the value-type walk must have checked each of them.
    private static readonly string[] _walkedPaths =
    {
        "schemaVersion", "run.verdictUtc", "run.durationSeconds", "run.firstErrorSeconds", "memory.testedBytes",
        "memory.releaseSeconds", "plan.targetCoveragePercent", "steps[0].passes", "steps[1].errors",
        "coverage.percent", "errors.total", "errors.storedCount",
    };

    private readonly ITestOutputHelper _output;

    /// <summary>Initializes the tests with the output that receives the host's stdout and stderr.</summary>
    /// <param name="output">The test output.</param>
    public ProductionBindingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestHostFolder_HoldsProductionEngineOnly()
    {
        string hostDirectory = TestHostLauncher.HostDirectory;
        Assert.True(Directory.Exists(hostDirectory), $"The TestHost folder '{hostDirectory}' does not exist.");

        foreach (string required in new[] { HostExecutableName, InteropAssemblyName, ProductionEngineName })
        {
            string path = Path.Combine(hostDirectory, required);
            Assert.True(File.Exists(path), $"'{path}' is missing from the TestHost folder.");
        }

        string[] testEngines = Directory.GetFiles(
            hostDirectory,
            EngineBindingFixture.TestEngineFileName,
            SearchOption.AllDirectories);
        Assert.True(
            testEngines.Length == 0,
            $"The test-only engine must never sit in the TestHost folder: {string.Join(", ", testEngines)}");
    }

    // Async methods carry the Async suffix that the repository's naming rule (IDE1006) requires.
    [Fact(Timeout = 240_000)]
    public async Task ProductionEngine_TwoStepRun_PublishesValidPassSummaryAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var root = new TempDataRoot();
        int regionMiB = TestHostLauncher.RegionMiBFor(RequestedRegionMiB);
        await using var host = TestHostLauncher.Start(
            TestHostLauncher.Arguments(root.Path, regionMiB, steps: StepCount, stepSeconds: StepSeconds));

        int exitCode;
        try
        {
            exitCode = await host.WaitForExitAsync(_hostTimeout, cancellation);
        }
        finally
        {
            _output.WriteLine(host.DescribeOutput());
        }

        string described = host.DescribeOutput();
        IReadOnlyList<string> stdOut = host.StdOut;

        Assert.True(
            exitCode == TestHostLauncher.ExitPassed,
            $"RamQuick.TestHost exited with {exitCode}, not {TestHostLauncher.ExitPassed} (PASS).{Environment.NewLine}{described}");

        string[] errorLines = stdOut.Where(line => line.StartsWith(TestHostLauncher.ErrorPrefix, StringComparison.Ordinal)).ToArray();
        Assert.True(errorLines.Length == 0, $"The host reported errors.{Environment.NewLine}{described}");

        Guid runId = ParseStartedRunId(stdOut, described);
        Assert.True(
            stdOut.Contains(PassVerdictLine, StringComparer.Ordinal),
            $"The host did not report '{PassVerdictLine}'.{Environment.NewLine}{described}");

        (string jsonPath, string textPath) = ParsePublishedPaths(stdOut, root.Data, runId, described);
        Assert.True(File.Exists(jsonPath), $"The published JSON summary '{jsonPath}' does not exist.");
        Assert.True(File.Exists(textPath), $"The published text summary '{textPath}' does not exist.");
        Assert.Equal(new[] { jsonPath }, root.SummaryJsonFiles(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { textPath }, root.SummaryTextFiles(), StringComparer.OrdinalIgnoreCase);

        string[] temporaryFiles = Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories);
        Assert.True(temporaryFiles.Length == 0, $"Publication left temporary files: {string.Join(", ", temporaryFiles)}");
        Assert.False(File.Exists(root.JournalPath), $"The journal '{root.JournalPath}' was not deleted after publication.");

        // The production engine ran only inside the host; this process stays bound to the test engine.
        using (Process current = Process.GetCurrentProcess())
        {
            Assert.DoesNotContain(
                current.Modules.Cast<ProcessModule>(),
                module => string.Equals(module.ModuleName, ProductionEngineName, StringComparison.OrdinalIgnoreCase));
        }

        string json = await File.ReadAllTextAsync(jsonPath, Encoding.UTF8, cancellation);
        AssertSchemaVersion1(json, runId, regionMiB);

        string text = await File.ReadAllTextAsync(textPath, Encoding.UTF8, cancellation);
        Assert.Equal(runId, TextSummaryFormatter.TryParseRunId(text));
        Assert.Contains(CauseHints.HintLabel, text, StringComparison.Ordinal);
    }

    private static Guid ParseStartedRunId(IReadOnlyList<string> stdOut, string described)
    {
        string? line = stdOut.FirstOrDefault(
            candidate => candidate.StartsWith(TestHostLauncher.StartedPrefix, StringComparison.Ordinal));
        Assert.True(
            line is not null,
            $"The host did not report '{TestHostLauncher.StartedPrefix}<run id>'.{Environment.NewLine}{described}");

        Match match = StartedLine().Match(line);
        Assert.True(match.Success, $"'{line}' does not carry a run id of 32 lowercase hex digits.");
        return Guid.ParseExact(match.Groups["id"].Value, "N");
    }

    // The line is "RQHOST PUBLISHED json=<path> txt=<path>"; the last " txt=" separates the paths, so a space in the
    // data folder's path cannot split them wrongly.
    private static (string JsonPath, string TextPath) ParsePublishedPaths(
        IReadOnlyList<string> stdOut,
        DataDirectory data,
        Guid runId,
        string described)
    {
        string? line = stdOut.FirstOrDefault(
            candidate => candidate.StartsWith(TestHostLauncher.PublishedPrefix, StringComparison.Ordinal));
        Assert.True(
            line is not null,
            $"The host did not report '{TestHostLauncher.PublishedPrefix}<path>{PublishedTextSeparator}<path>'.{Environment.NewLine}{described}");

        string paths = line[TestHostLauncher.PublishedPrefix.Length..];
        int separator = paths.LastIndexOf(PublishedTextSeparator, StringComparison.Ordinal);
        Assert.True(separator > 0, $"'{line}' does not name both summaries.");

        string jsonPath = paths[..separator];
        string textPath = paths[(separator + PublishedTextSeparator.Length)..];
        string expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data.LogsDirectory));
        foreach (string path in new[] { jsonPath, textPath })
        {
            Assert.True(Path.IsPathFullyQualified(path), $"The published path '{path}' is not a full path.");
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(Path.GetFullPath(path)), ignoreCase: true);
        }

        // Both files share the run's base name, which ends in the run id.
        string jsonFileName = Path.GetFileName(jsonPath);
        Assert.EndsWith(SummaryJsonSuffix, jsonFileName, StringComparison.Ordinal);
        string baseName = jsonFileName[..^SummaryJsonSuffix.Length];
        Assert.Equal(ResultsLog.GetSummaryJsonPath(data, baseName), jsonPath, ignoreCase: true);
        Assert.Equal(ResultsLog.GetSummaryTextPath(data, baseName), textPath, ignoreCase: true);

        Match baseNameMatch = QuickBaseName().Match(baseName);
        Assert.True(baseNameMatch.Success, $"'{baseName}' is not a Quick run's base name.");
        Assert.Equal(runId, Guid.ParseExact(baseNameMatch.Groups["id"].Value, "N"));
        return (jsonPath, textPath);
    }

    private static void AssertSchemaVersion1(string json, Guid runId, int regionMiB)
    {
        Assert.True(
            RunSummaryJson.TryDeserialize(json, out RunSummary? summary),
            $"The published JSON summary is not a schema version {RunSummary.CurrentSchemaVersion} summary.");
        Assert.NotNull(summary);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var root = new SummaryNode(document.RootElement, string.Empty);

        Assert.Equal(RunSummary.CurrentSchemaVersion, root.Int32At("schemaVersion"));
        Assert.False(string.IsNullOrWhiteSpace(root.ObjectAt("app").TextAt("name")), "app.name is empty.");

        AssertRun(root.ObjectAt("run"), runId);
        AssertSystem(root.ObjectAt("system"));
        AssertMemory(root.ObjectAt("memory"), regionMiB);
        AssertPlanAndSteps(root);
        AssertOutcome(root);

        var walked = new List<string>();
        AssertValueTypes(document.RootElement, string.Empty, walked);
        foreach (string path in _walkedPaths)
        {
            Assert.True(walked.Contains(path, StringComparer.Ordinal), $"The value-type walk did not check '{path}'.");
        }

        // The typed records read back the values the document holds.
        Assert.Equal(SummaryFormat.RunId(runId), summary.Run.Id);
        Assert.Equal("PASS", summary.Run.Verdict);
        Assert.Equal(StepCount, summary.Steps.Count);
        Assert.Equal((ulong?)root.ObjectAt("memory").UInt64At("testedBytes"), summary.Memory.TestedBytes);
        Assert.Equal(0UL, summary.Errors.Total);
        Assert.Equal(CauseHints.HintLabel, summary.CauseHints.Label);
    }

    private static void AssertRun(SummaryNode run, Guid runId)
    {
        string id = run.TextAt("id");
        Assert.True(Guid.TryParseExact(id, "N", out Guid parsed), $"run.id '{id}' is not a 32-hex-digit GUID.");
        Assert.Equal(runId, parsed);
        Assert.Equal("Quick", run.TextAt("testType"));
        Assert.False(run.BooleanAt("stopOnError"));
        Assert.Equal("PASS", run.TextAt("verdict"));
        Assert.Equal("Completed", run.TextAt("endReason"));
        Assert.False(run.BooleanAt("recovered"));
        Assert.False(run.BooleanAt("partial"));

        DateTimeOffset previous = DateTimeOffset.MinValue;
        string previousName = string.Empty;
        foreach (string name in _runTimestampNames)
        {
            string text = run.TextAt(name);
            Assert.EndsWith("Z", text, StringComparison.Ordinal);
            DateTimeOffset stamp = DateTimeOffset.ParseExact(
                text,
                UtcFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            Assert.True(stamp >= previous, $"run.{name} ({text}) precedes run.{previousName}.");
            previous = stamp;
            previousName = name;
        }

        Assert.True(run.NumberAt("durationSeconds") > 0, "run.durationSeconds is not positive.");
        Assert.True(run.NumberAt("timeToVerdictSeconds") > 0, "run.timeToVerdictSeconds is not positive.");
        run.NullAt("firstErrorSeconds");
    }

    private static void AssertSystem(SummaryNode system)
    {
        int logicalProcessors = system.Int32At("logicalProcessors");
        Assert.True(logicalProcessors > 0, "system.logicalProcessors is not positive.");
        Assert.InRange(system.Int32At("processorGroups"), 1, logicalProcessors);

        string instructionSet = system.TextAt("instructionSet");
        Assert.Contains(instructionSet, _instructionSets);
        Assert.Equal(instructionSet == "SSE2", system.BooleanAt("reducedInstructionSet"));
    }

    private static void AssertMemory(SummaryNode memory, int regionMiB)
    {
        ulong regionCap = (ulong)regionMiB * BytesPerMiB;
        ulong tested = memory.UInt64At("testedBytes");
        ulong planned = memory.UInt64At("plannedBytes");
        ulong available = memory.UInt64At("availableBytesAtStart");

        Assert.True(tested > 0, "memory.testedBytes is 0.");
        Assert.True(tested % SliceGranularityBytes == 0, $"memory.testedBytes {tested} is not a whole number of 2 MiB slices.");
        // plannedBytes is the uncapped sizing rule's plan; the host's cap bounds only the region the engine tests.
        Assert.True(tested <= regionCap, $"memory.testedBytes {tested} exceeds the {regionMiB} MiB region cap.");
        Assert.True(tested <= planned, $"memory.testedBytes {tested} exceeds memory.plannedBytes {planned}.");
        Assert.True(planned <= available, $"memory.plannedBytes {planned} exceeds memory.availableBytesAtStart {available}.");

        memory.Child("installedBytes", JsonValueKind.Number, JsonValueKind.Null);
        memory.Child("releaseSeconds", JsonValueKind.Number, JsonValueKind.Null);
        Assert.Equal("Virtual", memory.TextAt("addressKind"));
        Assert.Contains(memory.TextAt("pageMode"), _pageModes);
        memory.TextAt("type");
    }

    private static void AssertPlanAndSteps(SummaryNode root)
    {
        StepDefinition[] planned = TestPlans.Quick.Steps.Take(StepCount).ToArray();
        SummaryNode plan = root.ObjectAt("plan");
        Assert.Equal((double)(StepCount * StepSeconds), plan.NumberAt("targetSeconds"));
        Assert.Equal((double)planned.Sum(step => step.CoverageBudgetPercent), plan.NumberAt("targetCoveragePercent"));

        IReadOnlyList<SummaryNode> steps = root.ItemsAt("steps");
        Assert.Equal(StepCount, steps.Count);
        for (int index = 0; index < steps.Count; index++)
        {
            SummaryNode step = steps[index];
            Assert.Equal(index + 1, step.Int32At("index"));
            Assert.Equal(planned[index].Name, step.TextAt("name"));
            foreach (string name in _stepQuantityNames)
            {
                step.Child(name, JsonValueKind.Number);
            }

            Assert.Equal((double)StepSeconds, step.NumberAt("plannedSeconds"));
            Assert.True(step.NumberAt("actualSeconds") > 0, $"{step.Location}.actualSeconds is not positive.");
            Assert.True(step.NumberAt("overrunSeconds") >= 0, $"{step.Location}.overrunSeconds is negative.");
            Assert.True(step.Int32At("passes") >= 1, $"{step.Location}.passes is below 1.");
            Assert.True(step.NumberAt("coveragePercent") > 0, $"{step.Location}.coveragePercent is not positive.");
            Assert.Equal(0UL, step.UInt64At("errors"));
            Assert.True(step.BooleanAt("completed"), $"{step.Location}.completed is false.");
        }
    }

    private static void AssertOutcome(SummaryNode root)
    {
        SummaryNode coverage = root.ObjectAt("coverage");
        double percent = coverage.NumberAt("percent");
        double targetPercent = coverage.NumberAt("targetPercent");
        Assert.True(percent > 0, "coverage.percent is not positive.");
        Assert.Equal(root.ObjectAt("plan").NumberAt("targetCoveragePercent"), targetPercent);
        Assert.Equal(percent >= targetPercent, coverage.BooleanAt("targetReached"));

        SummaryNode temperatures = root.ObjectAt("temperatures");
        temperatures.TextAt("cpuStatus");
        temperatures.TextAt("ramStatus");

        SummaryNode errors = root.ObjectAt("errors");
        Assert.Equal(0UL, errors.UInt64At("total"));
        Assert.False(errors.BooleanAt("truncated"));
        Assert.True(errors.BooleanAt("detailAvailable"), "errors.detailAvailable is false for a normal run.");
        Assert.Equal(0, errors.Int32At("storedCount"));
        Assert.True(errors.Int32At("storedCap") > 0, "errors.storedCap is not positive.");
        Assert.Equal(0, errors.Int32At("storedDistinctAddresses"));
        Assert.Empty(errors.ItemsAt("groups"));
        Assert.Empty(errors.ItemsAt("records"));

        SummaryNode causeHints = root.ObjectAt("causeHints");
        Assert.Equal(CauseHints.HintLabel, causeHints.TextAt("label"));
        Assert.Empty(causeHints.ItemsAt("items"));

        root.NullAt("unfinishedRun");
    }

    // Applies the schema's value-type rules to every property of the document, by name, wherever it appears, so the
    // check stays meaningful for documents that carry error records. Each checked property's path is added to walked.
    private static void AssertValueTypes(JsonElement element, string location, List<string> walked)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string path = location.Length == 0 ? property.Name : location + "." + property.Name;
                if (AssertValueType(property.Name, property.Value, path))
                {
                    walked.Add(path);
                }

                AssertValueTypes(property.Value, path, walked);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertValueTypes(item, string.Create(CultureInfo.InvariantCulture, $"{location}[{index}]"), walked);
                index++;
            }
        }
    }

    // Returns whether a value-type rule applies to the property's name.
    private static bool AssertValueType(string name, JsonElement value, string path)
    {
        if (_hexValueNames.Contains(name))
        {
            Assert.True(
                value.ValueKind == JsonValueKind.String && HexWord().IsMatch(value.GetString() ?? string.Empty),
                $"'{path}' must be \"0x\" and 16 uppercase hex digits, not {value.GetRawText()}.");
            return true;
        }

        if (name.EndsWith(UtcSuffix, StringComparison.Ordinal))
        {
            Assert.True(
                value.ValueKind == JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.String && UtcTimestamp().IsMatch(value.GetString() ?? string.Empty)),
                $"'{path}' must be an ISO 8601 UTC timestamp or null, not {value.GetRawText()}.");
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object && IsQuantityName(name))
        {
            Assert.True(
                value.ValueKind is JsonValueKind.Number or JsonValueKind.Null,
                $"'{path}' is a quantity and must be a number or null, not {value.GetRawText()}.");
            return true;
        }

        return false;
    }

    private static bool IsQuantityName(string name) =>
        _quantityNames.Contains(name)
        || _quantitySuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    [GeneratedRegex("^RQHOST STARTED runId=(?<id>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex StartedLine();

    [GeneratedRegex("^RamQuick-[0-9]{8}-[0-9]{6}-Quick-(?<id>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex QuickBaseName();

    [GeneratedRegex("^0x[0-9A-F]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexWord();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestamp();

    /// <summary>
    /// A JSON element of the summary with its path from the document root, whose accessors assert the property's
    /// JSON kind and name the path when it is wrong.
    /// </summary>
    /// <param name="Element">The element.</param>
    /// <param name="Location">The element's path, such as <c>steps[1]</c>; empty for the root.</param>
    private readonly record struct SummaryNode(JsonElement Element, string Location)
    {
        public SummaryNode Child(string name, params JsonValueKind[] kinds)
        {
            string path = Location.Length == 0 ? name : Location + "." + name;
            Assert.True(
                Element.ValueKind == JsonValueKind.Object,
                $"'{Location}' is {Element.ValueKind}, not an object holding '{name}'.");
            Assert.True(Element.TryGetProperty(name, out JsonElement value), $"The summary has no '{path}' property.");
            Assert.True(
                kinds.Contains(value.ValueKind),
                $"'{path}' is {value.ValueKind} ({value.GetRawText()}), not {string.Join(" or ", kinds)}.");
            return new SummaryNode(value, path);
        }

        public SummaryNode ObjectAt(string name) => Child(name, JsonValueKind.Object);

        public string TextAt(string name) => Child(name, JsonValueKind.String).Element.GetString() ?? string.Empty;

        public double NumberAt(string name) => Child(name, JsonValueKind.Number).Element.GetDouble();

        public int Int32At(string name) => Child(name, JsonValueKind.Number).Element.GetInt32();

        public ulong UInt64At(string name) => Child(name, JsonValueKind.Number).Element.GetUInt64();

        public bool BooleanAt(string name) => Child(name, JsonValueKind.True, JsonValueKind.False).Element.GetBoolean();

        public void NullAt(string name) => Child(name, JsonValueKind.Null);

        public IReadOnlyList<SummaryNode> ItemsAt(string name)
        {
            SummaryNode array = Child(name, JsonValueKind.Array);
            return array.Element.EnumerateArray()
                .Select((item, index) => new SummaryNode(
                    item,
                    string.Create(CultureInfo.InvariantCulture, $"{array.Location}[{index}]")))
                .ToArray();
        }
    }
}
