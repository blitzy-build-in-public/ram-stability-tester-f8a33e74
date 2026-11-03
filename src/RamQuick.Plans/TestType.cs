namespace RamQuick.Plans;

// Plan members are named with <c> rather than <see cref>, so this enum keeps no compile-time
// reference back to TestPlans, which is built on it.

/// <summary>
/// The three test types, in order of increasing length and in the order the selector lists them.
/// Each type maps to one declarative step plan through <c>TestPlans.Get(TestType)</c>; that plan's
/// step budgets, not this enum, define the exact duration and coverage floor.
/// </summary>
/// <remarks>
/// Member names are persisted by name: they appear as <c>run.testType</c> in the JSON summary, in
/// the progress journal's <c>begin</c> record and in the log base name
/// <c>RamQuick-&lt;yyyyMMdd-HHmmss&gt;-&lt;TestType&gt;-&lt;runId&gt;</c>. Renaming a member is
/// therefore a schema change. <see cref="Quick"/> is the zero value, so <c>default(TestType)</c>
/// is the type selected at launch.
/// </remarks>
public enum TestType
{
    /// <summary>
    /// Quick check of about 5 minutes for the tune-test-adjust loop, ordered fail-fast. Its step
    /// list, time budgets and coverage floor are defined by <c>TestPlans.Quick</c>.
    /// </summary>
    Quick = 0,

    /// <summary>
    /// Standard confirmation of about 20 minutes: Quick's steps followed by longer inversion, hold,
    /// reduced-load contrast and heat-soak steps. Its step list, time budgets and coverage floor are
    /// defined by <c>TestPlans.Standard</c>.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Exhaustive confirmation of about 60 minutes before daily use: Standard's steps followed by
    /// longer runs of each stress. Its step list, time budgets and coverage floor are defined by
    /// <c>TestPlans.Exhaustive</c>.
    /// </summary>
    Exhaustive = 2
}
