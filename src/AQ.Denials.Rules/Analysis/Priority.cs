namespace AQ.Denials.Rules.Analysis;

/// <summary>Why an item sits where it does in the queue — returned alongside the score.</summary>
public sealed record PriorityFactor(string Name, int Weight, string Because);

/// <summary>The queue ordering, as a number anyone can recompute by hand.</summary>
public sealed record PriorityScore(int Score, IReadOnlyList<PriorityFactor> Factors)
{
    /// <summary>Rendered for the UI: "recoverable +400, due in 5 days +150, …".</summary>
    public string Explain() =>
        Factors.Count == 0
            ? "no factors"
            : string.Join(", ", Factors.Select(f => $"{f.Name} {f.Weight:+0;-0;0}"));
}

/// <summary>
/// What the queue is ordered by, and why — the answer to "what should my team work on today?"
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this encodes: work on money you can still get, before money you cannot.</b> That is
/// the manager's first question (how much can we still recover?) driving her third (what should
/// each person do today?). It would be easy to order by amount alone, and that would be wrong —
/// a $900 denial whose appeal window closed three weeks ago is not today's work, while a $120
/// denial with six days left is.
/// </para>
/// <para>
/// <b>Bands, not a continuous formula.</b> A weight you can state in one sentence — "a denial due
/// this week is worth 150" — can be explained to a practice manager and asserted in a test. A
/// gradient fitted to look sensible cannot be argued with, because nobody can see what it says.
/// </para>
/// <para>
/// <b>The weights are fixed here, not learned.</b> They are a stated position about the work, and
/// <see cref="PriorityTests"/> pins the ordering properties that position implies: at otherwise
/// equal inputs, recoverable outranks blocked outranks expired, sooner outranks later, more money
/// outranks less, review-needed outranks reviewed, and preventable outranks not.
/// </para>
/// </remarks>
public static class Priority
{
    /// <summary>Applied to every factor of an item nobody can act on any more. Keeps a resolved
    /// item out of the working queue without deleting it — the history stays, the row leaves.</summary>
    public const int ClosedPenalty = -100_000;

    public static PriorityScore Compute(
        string bucket,
        int? daysUntilDeadline,
        decimal charge,
        bool requiresHumanReview,
        bool? preventableAtPrebill,
        string status)
    {
        var factors = new List<PriorityFactor>();

        /* -- 1. can the money still be recovered? -------------------------------------- */
        var (weight, because) = bucket switch
        {
            RecoveryBuckets.Recoverable =>
                (400, "window open and no policy bar — this money is still winnable"),
            RecoveryBuckets.PolicyBlocked =>
                (150, "window open but a payer policy bars the appeal, so the work is a "
                    + "rebill or a write-off decision"),
            RecoveryBuckets.Expired =>
                (50, "every denial route is closed, so only a correction or write-off remains"),
            _ => (0, "unrecognised bucket"),
        };
        factors.Add(new PriorityFactor("recoverability", weight, because));

        /* -- 2. how soon does the option disappear? ------------------------------------ */
        // Bands rather than a gradient. A denial due inside a week is a different kind of task
        // from one due in a quarter, and "due this week" is a sentence a manager can act on.
        var (urgencyWeight, urgencyBecause) = daysUntilDeadline switch
        {
            null or < 0 => (0, "no open window remains, so there is nothing to beat"),
            <= 7 => (150, $"due in {daysUntilDeadline} day(s) — it is gone if it is not worked this week"),
            <= 14 => (100, $"due in {daysUntilDeadline} days"),
            <= 30 => (50, $"due in {daysUntilDeadline} days"),
            _ => (10, $"due in {daysUntilDeadline} days — comfortable"),
        };
        factors.Add(new PriorityFactor("deadline", urgencyWeight, urgencyBecause));

        /* -- 3. how much money is it? -------------------------------------------------- */
        var (moneyWeight, moneyBecause) = charge switch
        {
            >= 500m => (100, $"${charge:0.00} at stake"),
            >= 250m => (60, $"${charge:0.00} at stake"),
            >= 100m => (30, $"${charge:0.00} at stake"),
            _ => (10, $"${charge:0.00} at stake"),
        };
        factors.Add(new PriorityFactor("amount", moneyWeight, moneyBecause));

        /* -- 4. does a human have to look before anyone can act? ----------------------- */
        if (requiresHumanReview)
            factors.Add(new PriorityFactor("needs review", 75,
                "confidence is below the threshold, so the analysis must be confirmed before "
              + "the note is used"));

        /* -- 5. could a pre-bill check have stopped it? --------------------------------- */
        // Q2's second half. Low weight on purpose: it says how *embarassing* a denial is, not how
        // much money it holds, and money must win when the two disagree.
        if (preventableAtPrebill == true)
            factors.Add(new PriorityFactor("preventable", 25,
                "a check before submission would have caught this"));

        /* -- 6. has someone already closed it? ------------------------------------------- */
        // Last, because it must not mask a real factor: a resolved item scores normally and is
        // then pushed below everything still open.
        if (IsClosed(status))
            factors.Add(new PriorityFactor("closed", ClosedPenalty,
                $"status is '{status}', so it is not work for today"));

        return new PriorityScore(factors.Sum(f => f.Weight), factors);
    }

    /// <summary>Terminal states. Kept here so the queue filter and the score cannot disagree.</summary>
    public static bool IsClosed(string status) =>
        string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "written_off", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase);
}
