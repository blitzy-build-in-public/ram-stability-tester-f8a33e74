namespace RamQuick.Analysis;

/// <summary>
/// One ranked probable cause of the errors a run found: its category, how strongly the evidence points to it,
/// its share of the kept hints, the plain-language suggestion and the evidence line shown with it. A cause hint
/// is a hint, never a diagnosis; every channel that shows it labels it with <c>CauseHints.Label</c>.
/// </summary>
/// <remarks>
/// The short display title is not part of the hint: consumers take it from <c>Suggestions.Title</c> for the
/// <see cref="Category"/>. Every value is validated when the hint is constructed and again whenever a
/// <see langword="with"/> expression changes it, because an out-of-range value can only come from a
/// programming error in the analyzer.
/// </remarks>
/// <param name="Rank">The 1-based position of the hint after ranking; 1 is the most likely cause. At least 1.</param>
/// <param name="Category">The probable cause the hint names. A defined <see cref="CauseCategory"/>.</param>
/// <param name="Score">
/// The category score: the larger of the category's signature score and its step-mechanism prior, in the range
/// 0 to 1, as rounded by the analyzer to <c>AnalyzerThresholds.ScoreDecimals</c> decimal places.
/// </param>
/// <param name="LikelihoodPercent">
/// 100 × <paramref name="Score"/> ÷ the sum of the scores of every kept hint, in the range 0 to 100. The value is
/// unrounded; display rounding belongs to the consumers.
/// </param>
/// <param name="Suggestion">
/// The plain-language suggestion for the category, from <c>Suggestions.For</c>. Not empty or white space.
/// </param>
/// <param name="Evidence">
/// One plain-English line stating the evidence behind the score, for example
/// "92 % of errors on bit 17 across 41 addresses". Not empty or white space.
/// </param>
/// <exception cref="ArgumentOutOfRangeException">
/// <paramref name="Rank"/> is less than 1, <paramref name="Category"/> is not a defined value,
/// <paramref name="Score"/> is outside 0 to 1, or
/// <paramref name="LikelihoodPercent"/> is outside 0 to 100; NaN is outside every range.
/// </exception>
/// <exception cref="ArgumentNullException"><paramref name="Suggestion"/> or <paramref name="Evidence"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="Suggestion"/> or <paramref name="Evidence"/> is empty or white space.</exception>
public sealed record CauseHint(
    int Rank,
    CauseCategory Category,
    double Score,
    double LikelihoodPercent,
    string Suggestion,
    string Evidence)
{
    // Explicit backing fields let both the primary constructor (through these initializers) and the init
    // accessors (through with-expressions) run the same validation; value equality compares these fields.
    // Every positional property is declared explicitly, so member order (ToString, reflection-based
    // serialization) follows the positional order instead of listing a synthesized property first.
    private readonly int _rank = RequireRank(Rank);
    private readonly CauseCategory _category = RequireCategory(Category);
    private readonly double _score = RequireScore(Score);
    private readonly double _likelihoodPercent = RequireLikelihoodPercent(LikelihoodPercent);
    private readonly string _suggestion = RequireText(Suggestion, nameof(Suggestion));
    private readonly string _evidence = RequireText(Evidence, nameof(Evidence));

    /// <summary>The 1-based position of the hint after ranking; 1 is the most likely cause.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is less than 1.</exception>
    public int Rank
    {
        get => _rank;
        init => _rank = RequireRank(value);
    }

    /// <summary>The probable cause the hint names.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a defined <see cref="CauseCategory"/>.</exception>
    public CauseCategory Category
    {
        get => _category;
        init => _category = RequireCategory(value);
    }

    /// <summary>The category score, in the range 0 to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is outside 0 to 1, or NaN.</exception>
    public double Score
    {
        get => _score;
        init => _score = RequireScore(value);
    }

    /// <summary>The hint's unrounded share of the kept hints' total score, in the range 0 to 100.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is outside 0 to 100, or NaN.</exception>
    public double LikelihoodPercent
    {
        get => _likelihoodPercent;
        init => _likelihoodPercent = RequireLikelihoodPercent(value);
    }

    /// <summary>The plain-language suggestion for the category.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value set is empty or white space.</exception>
    public string Suggestion
    {
        get => _suggestion;
        init => _suggestion = RequireText(value, nameof(Suggestion));
    }

    /// <summary>The one-line plain-English evidence statement shown with the hint.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value set is empty or white space.</exception>
    public string Evidence
    {
        get => _evidence;
        init => _evidence = RequireText(value, nameof(Evidence));
    }

    private static int RequireRank(int rank) =>
        rank >= 1
            ? rank
            : throw new ArgumentOutOfRangeException(nameof(Rank), rank, "A cause hint's rank is 1-based and must be at least 1.");

    // An undefined value has no suggestion, title or persisted JSON name, so it can only be a programming error.
    private static CauseCategory RequireCategory(CauseCategory category) =>
        Enum.IsDefined(category)
            ? category
            : throw new ArgumentOutOfRangeException(nameof(Category), category, "A cause hint's category must be a defined CauseCategory.");

    private static double RequireScore(double score) =>
        RequireInRange(score, 1d, nameof(Score), "A cause hint's score must be between 0 and 1.");

    private static double RequireLikelihoodPercent(double percent) =>
        RequireInRange(percent, 100d, nameof(LikelihoodPercent), "A cause hint's likelihood must be between 0 and 100 percent.");

    private static double RequireInRange(double value, double max, string paramName, string message)
    {
        // Written as a negated range test so that NaN, for which every ordered comparison is false, is rejected.
        if (!(value >= 0d && value <= max))
        {
            throw new ArgumentOutOfRangeException(paramName, value, message);
        }

        return value;
    }

    private static string RequireText(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }
}
