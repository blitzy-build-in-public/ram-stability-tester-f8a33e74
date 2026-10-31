using RamQuick.Core;

namespace RamQuick.Analysis;

/// <summary>
/// Everything the cause analyzer reads about one run: the stored error records, the outcome of every started step,
/// the size of the tested region, the worker count and the installed memory type. It also carries the pure queries
/// the six signature rules share, so each rule computes its shares and preconditions the same way.
/// </summary>
/// <remarks>
/// <para>
/// The input is immutable. The constructor copies both sequences, so a caller may keep changing its own lists, and the
/// exposed lists cannot be cast back to a writable collection. Every member is pure and deterministic: no I/O, no
/// clock, no randomness and no static state, so tests construct synthetic inputs directly and get identical results
/// for identical inputs. An instance is safe to read from any number of threads.
/// </para>
/// <para>
/// Shares are fractions of the error records analysed, <see cref="ErrorCount"/>, never of the run's true error total,
/// which keeps counting past the stored-detail cap. Step indices are zero-based in both <see cref="ErrorRecord"/> and
/// <see cref="StepAttributes"/> and are matched as they are, with no offset.
/// </para>
/// <para>
/// Two inputs are equal when they hold equal error records and equal step outcomes, each in the same order, and the
/// same tested bytes, worker count and memory type. No property is settable, so a <see langword="with"/> expression
/// can only copy an input and never bypass the constructor's checks.
/// </para>
/// </remarks>
public sealed record AnalysisInput
{
    private readonly ErrorRecord[] _errors;
    private readonly StepOutcome[] _steps;
    private readonly Dictionary<int, StepAttributes> _stepsByIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisInput"/> record from a run's stored error records and step
    /// outcomes.
    /// </summary>
    /// <param name="errors">
    /// The stored error records, at most <see cref="AnalyzerThresholds.MaxAnalysedRecords"/>, in any order. An empty
    /// sequence is valid.
    /// </param>
    /// <param name="steps">
    /// One outcome per started step; steps that never started have none. Each outcome's
    /// <see cref="StepAttributes.Index"/> must be unique.
    /// </param>
    /// <param name="testedBytes">
    /// The bytes under test as the engine reported them at Prepared. It may be 0, for example when preparation never
    /// finished; <see cref="RegionWindowBytes"/> is then 0 and the region-based rules cannot fire.
    /// </param>
    /// <param name="workerCount">The number of pinned workers the run used, 0 or more.</param>
    /// <param name="memoryType">The DRAM generation of the installed modules, from the memory inventory.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="errors"/> or <paramref name="steps"/> is <see langword="null"/>, or contains a
    /// <see langword="null"/> element, or a step outcome has <see langword="null"/> attributes.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="errors"/> holds more than <see cref="AnalyzerThresholds.MaxAnalysedRecords"/> records, which no
    /// run stores, so the input is refused rather than silently truncated; or two step outcomes share a step index.
    /// </exception>
    public AnalysisInput(
        IEnumerable<ErrorRecord> errors,
        IEnumerable<StepOutcome> steps,
        ulong testedBytes,
        int workerCount,
        MemoryType memoryType)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);

        ErrorRecord[] errorCopy = errors.ToArray();
        if (errorCopy.Length > AnalyzerThresholds.MaxAnalysedRecords)
        {
            throw new ArgumentException(
                $"At most {AnalyzerThresholds.MaxAnalysedRecords} error records can be analysed; "
                + $"{errorCopy.Length} were passed.",
                nameof(errors));
        }

        for (int i = 0; i < errorCopy.Length; i++)
        {
            if (errorCopy[i] is null)
            {
                throw new ArgumentNullException(nameof(errors), $"Error record {i} is null.");
            }
        }

        StepOutcome[] stepCopy = steps.ToArray();
        var stepsByIndex = new Dictionary<int, StepAttributes>(stepCopy.Length);
        for (int i = 0; i < stepCopy.Length; i++)
        {
            StepOutcome outcome = stepCopy[i]
                ?? throw new ArgumentNullException(nameof(steps), $"Step outcome {i} is null.");
            StepAttributes attributes = outcome.Attributes
                ?? throw new ArgumentNullException(nameof(steps), $"Step outcome {i} has null attributes.");

            if (!stepsByIndex.TryAdd(attributes.Index, attributes))
            {
                throw new ArgumentException(
                    $"Two step outcomes share step index {attributes.Index}.",
                    nameof(steps));
            }
        }

        _errors = errorCopy;
        _steps = stepCopy;
        _stepsByIndex = stepsByIndex;
        Errors = Array.AsReadOnly(errorCopy);
        Steps = Array.AsReadOnly(stepCopy);
        TestedBytes = testedBytes;
        WorkerCount = workerCount;
        MemoryType = memoryType;
    }

    /// <summary>
    /// Gets the stored error records analysed, in the order they were passed. Read-only.
    /// </summary>
    public IReadOnlyList<ErrorRecord> Errors { get; }

    /// <summary>
    /// Gets the outcome of every started step, in the order they were passed. Read-only.
    /// </summary>
    public IReadOnlyList<StepOutcome> Steps { get; }

    /// <summary>
    /// Gets the bytes under test, the region against which offsets and windows are measured. 0 when the engine never
    /// reported a tested size.
    /// </summary>
    public ulong TestedBytes { get; }

    /// <summary>
    /// Gets the number of pinned workers the run used.
    /// </summary>
    public int WorkerCount { get; }

    /// <summary>
    /// Gets the DRAM generation of the installed modules, which decides how bit positions fold into lanes.
    /// </summary>
    public MemoryType MemoryType { get; }

    /// <summary>
    /// Gets the number of error records analysed: the denominator of every share the rules and the step prior compute.
    /// </summary>
    public int ErrorCount => _errors.Length;

    /// <summary>
    /// Gets the modulus that folds a bit index into its data lane for <see cref="MemoryType"/>: 32 on DDR5, whose
    /// modules carry two 32-bit sub-channels, and 64 otherwise.
    /// </summary>
    public int LaneModulo => AnalyzerThresholds.LaneModuloFor(MemoryType);

    /// <summary>
    /// Gets the width, in bytes of region offset, of one sixteenth of the tested region: the spread the bit-lane rule
    /// requires and the window the address-cluster rule scans. It is 0 when fewer than 16 bytes were tested, as when
    /// <see cref="TestedBytes"/> is 0, and the region-based rules then cannot fire.
    /// </summary>
    public ulong RegionWindowBytes => TestedBytes / AnalyzerThresholds.RegionWindowDivisor;

    /// <summary>
    /// Returns the attributes of the step that found an error.
    /// </summary>
    /// <param name="error">An error record, normally one of <see cref="Errors"/>.</param>
    /// <returns>
    /// The attributes of the step outcome whose <see cref="StepAttributes.Index"/> equals
    /// <see cref="ErrorRecord.StepIndex"/>, or <see langword="null"/> when no outcome carries that index. Such an error
    /// still counts in every share's denominator, but in no step-based numerator.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public StepAttributes? StepOf(ErrorRecord error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return _stepsByIndex.TryGetValue(error.StepIndex, out StepAttributes? attributes) ? attributes : null;
    }

    /// <summary>
    /// Returns the share of the analysed error records that satisfy a condition.
    /// </summary>
    /// <param name="predicate">The condition on one error record. It must be pure.</param>
    /// <returns>
    /// The number of matching records divided by <see cref="ErrorCount"/>, in the range 0 to 1; 0 when there are no
    /// error records.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public double ShareOfErrors(Func<ErrorRecord, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_errors.Length == 0)
        {
            return 0.0;
        }

        int matching = 0;
        foreach (ErrorRecord error in _errors)
        {
            if (predicate(error))
            {
                matching++;
            }
        }

        return (double)matching / _errors.Length;
    }

    /// <summary>
    /// Returns the share of the analysed error records found by steps that satisfy a condition.
    /// </summary>
    /// <param name="predicate">
    /// The condition on a step's attributes. It must be pure; it is evaluated once per step outcome, not once per
    /// error record.
    /// </param>
    /// <returns>
    /// The number of records whose <see cref="StepOf"/> is not <see langword="null"/> and satisfies
    /// <paramref name="predicate"/>, divided by <see cref="ErrorCount"/>, in the range 0 to 1; 0 when there are no
    /// error records.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public double ShareOfErrorsInSteps(Func<StepAttributes, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_errors.Length == 0)
        {
            return 0.0;
        }

        var matchingStepIndices = new HashSet<int>();
        foreach (StepOutcome outcome in _steps)
        {
            if (predicate(outcome.Attributes))
            {
                matchingStepIndices.Add(outcome.Attributes.Index);
            }
        }

        if (matchingStepIndices.Count == 0)
        {
            return 0.0;
        }

        int matching = 0;
        foreach (ErrorRecord error in _errors)
        {
            if (matchingStepIndices.Contains(error.StepIndex))
            {
                matching++;
            }
        }

        return (double)matching / _errors.Length;
    }

    /// <summary>
    /// Returns the number of steps that satisfy a condition and completed a pass with zero errors: the contrast-step
    /// precondition of the bank-switching, turnaround and load rules.
    /// </summary>
    /// <param name="predicate">The condition on a step's attributes. It must be pure.</param>
    /// <returns>
    /// The number of step outcomes whose attributes satisfy <paramref name="predicate"/>, whose
    /// <see cref="StepOutcome.PassesCompleted"/> is at least 1 and whose true <see cref="StepOutcome.Errors"/> count is
    /// 0.
    /// </returns>
    /// <remarks>
    /// A step outcome carries no per-pass error counts, so a step that found any error at all, by the engine's true
    /// count and not by the stored records, never qualifies, even if one of its passes was itself clean. This is the
    /// conservative reading: a contrast step is only credited as clean when nothing it ran found an error.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public int CountCleanCompletedSteps(Func<StepAttributes, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int count = 0;
        foreach (StepOutcome outcome in _steps)
        {
            if (outcome.PassesCompleted >= 1 && outcome.Errors == 0 && predicate(outcome.Attributes))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns the number of started steps that satisfy a condition, such as the hold rule's "at least one hold step
    /// ran".
    /// </summary>
    /// <param name="predicate">The condition on a step's attributes. It must be pure.</param>
    /// <returns>
    /// The number of step outcomes, one per started step whether or not it completed a pass, whose attributes satisfy
    /// <paramref name="predicate"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public int CountStepsRun(Func<StepAttributes, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int count = 0;
        foreach (StepOutcome outcome in _steps)
        {
            if (predicate(outcome.Attributes))
            {
                count++;
            }
        }

        return count;
    }

    // Generated record equality would compare the copied arrays and the step lookup by reference, so two inputs built
    // from equal sequences would read as different. The lookup is derived from the step outcomes, so it is left out.

    /// <summary>
    /// Returns whether <paramref name="other"/> holds the same values, comparing <see cref="Errors"/> and
    /// <see cref="Steps"/> element by element in the order they were passed.
    /// </summary>
    /// <param name="other">The input to compare with.</param>
    /// <returns><see langword="true"/> when every value is equal.</returns>
    public bool Equals(AnalysisInput? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && TestedBytes == other.TestedBytes
            && WorkerCount == other.WorkerCount
            && MemoryType == other.MemoryType
            && Errors.SequenceEqual(other.Errors)
            && Steps.SequenceEqual(other.Steps));

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(AnalysisInput)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (ErrorRecord error in Errors)
        {
            hash.Add(error);
        }

        foreach (StepOutcome outcome in Steps)
        {
            hash.Add(outcome);
        }

        hash.Add(TestedBytes);
        hash.Add(WorkerCount);
        hash.Add(MemoryType);
        return hash.ToHashCode();
    }
}
