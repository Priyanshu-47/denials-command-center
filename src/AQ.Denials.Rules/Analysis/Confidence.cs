using AQ.Denials.Rules.Policy;

namespace AQ.Denials.Rules.Analysis;

/// <summary>How confident the system is, and why — in that order of importance.</summary>
/// <param name="Score">0–100.</param>
/// <param name="RequiresHumanReview">True below the threshold; the item belongs in the review queue.</param>
/// <param name="Factors">
/// One human-readable line per contributing factor, both the ones that raised the score and the
/// ones that lowered it. A single number with no explanation is a number nobody can argue with
/// or improve; this list is what makes the score reviewable rather than decorative.
/// </param>
public sealed record ConfidenceScore(
    int Score,
    bool RequiresHumanReview,
    IReadOnlyList<string> Factors);

/// <summary>
/// Computes confidence from facts this system already established — never from a model's own
/// estimate of how sure it feels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the weights are stated rather than fitted.</b> The brief asks for a confidence level
/// and a review queue; it does not supply labelled confidence data, and there are 40 labelled
/// rows — far too few to fit a calibrator against and then report its result honestly. So the
/// weights below are a <b>declared design choice</b>, written down in <c>docs/DECISIONS.md</c>
/// (D28) as exactly that. What is <b>measured</b> rather than asserted is whether the resulting
/// scores actually separate right from wrong on the labelled sample, which the evaluation reports
/// directly. A number that is unexplained and untested would be the worst of both.
/// </para>
/// <para>
/// Every input is something the system knows independently of the model: whether the category has
/// expert backing, whether the citation survived validation, how much of the deadline is left,
/// whether a draft exists at all, and whether the outcome table has an established position.
/// </para>
/// </remarks>
public static class Confidence
{
    public const int Threshold = 70;
    public const int Floor = 0;
    public const int Ceiling = 100;

    /// <param name="coveredByLabeledSample">The expert sample contains this category.</param>
    /// <param name="citation">Verdict on the citation as shipped (dropped citations count against, not for).</param>
    /// <param name="daysUntilDeadline">Days left in the open window, or <c>null</c> when no window applies.</param>
    /// <param name="draftProduced">A draft was generated and survived validation.</param>
    /// <param name="preventableEstablished">The outcome table has an expert position on pre-bill preventability.</param>
    public static ConfidenceScore Evaluate(
        bool coveredByLabeledSample,
        CitationVerdict citation,
        int? daysUntilDeadline,
        bool draftProduced,
        bool preventableEstablished)
    {
        var score = 50;
        var factors = new List<string> { "baseline 50 — a claim starts unproven, not true" };

        if (coveredByLabeledSample)
        {
            score += 25;
            factors.Add("category is one of the nine the expert sample covers  +25");
        }
        else
        {
            factors.Add("category is NOT in the expert sample; team and preventability are our "
                       + "routing decision, not the expert's  +0");
        }

        switch (citation)
        {
            case CitationVerdict.Valid:
                score += 15;
                factors.Add("a policy clause was found and the citation re-validated against it  +15");
                break;

            case CitationVerdict.NoCitation:
                score += 5;
                factors.Add("no policy in this payer's documents covers the denial, and the draft "
                           + "says so rather than borrowing a neighbour's  +5");
                break;

            case CitationVerdict.NotAssessed:
                // Deliberately not the −20 that a *failed* citation gets: nothing was claimed, so
                // nothing was disproved. The separate no-draft penalty below carries this case.
                factors.Add("no draft, so no citation was claimed or checked  +0");
                break;

            case CitationVerdict.ClauseAvailableUncited:
                // −20 rather than something gentler, because it is the one penalty large enough
                // that no other factor can pull the result back over the threshold: the best
                // achievable score here is 50 + 25 + 10 − 20 = 65. This note carries a claim the
                // system can already disprove, so it always reaches a human.
                score -= 20;
                factors.Add("our rules found a policy clause this denial may be argued from, but "
                           + "the draft cited none — it must not go out saying there is nothing "
                           + "to argue with  −20");
                break;

            default:
                score -= 20;
                factors.Add($"the draft cited '{citation}', which failed validation and was "
                           + $"dropped  −20");
                break;
        }

        switch (daysUntilDeadline)
        {
            case null:
                factors.Add("no open window applies  +0");
                break;
            case > 60:
                score += 10;
                factors.Add($"{daysUntilDeadline} days left in the window  +10");
                break;
            case <= 0:
                score -= 20;
                factors.Add("the window has closed  −20");
                break;
            default:
                factors.Add($"{daysUntilDeadline} days left in the window — still open, but "
                           + "tightening  +0");
                break;
        }

        if (draftProduced)
        {
            factors.Add("a draft appeal was produced and validated  +0");
        }
        else
        {
            score -= 15;
            factors.Add("no draft: the AI service is unavailable or declined  −15");
        }

        if (!preventableEstablished)
        {
            score -= 10;
            factors.Add("the expert sample has no position on whether this was preventable "
                       + "before billing  −10");
        }

        var clamped = Math.Clamp(score, Floor, Ceiling);
        if (clamped != score)
            factors.Add($"clamped from {score} to {clamped} — the scale ends here");

        var requiresReview = clamped < Threshold;
        factors.Add(requiresReview
            ? $"below the {Threshold} threshold → human review queue"
            : $"at or above the {Threshold} threshold → no review forced by score alone");

        return new ConfidenceScore(clamped, requiresReview, factors);
    }
}
