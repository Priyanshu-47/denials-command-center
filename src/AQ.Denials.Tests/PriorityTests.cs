using AQ.Denials.Rules;
using AQ.Denials.Rules.Analysis;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The queue order is a product decision with money attached, so what it guarantees is asserted
/// rather than described. Every test below holds one input still and moves another, which is what
/// makes the ordering a claim instead of a preference.
/// </summary>
public class PriorityTests
{
    private static PriorityScore Score(
        string bucket = RecoveryBuckets.Recoverable,
        int? days = 45,
        decimal charge = 200m,
        bool review = false,
        bool? preventable = false,
        string status = "open") =>
        Priority.Compute(bucket, days, charge, review, preventable, status);

    /* -- what the queue is really for ------------------------------------------------- */

    [Fact]
    public void Money_that_can_still_be_recovered_outranks_money_that_cannot()
    {
        // The whole justification for the ranking. A $900 denial whose routes are closed is not
        // today's work; a $120 denial with a week left is.
        var recoverable = Score(bucket: RecoveryBuckets.Recoverable, charge: 120m);
        var expired = Score(bucket: RecoveryBuckets.Expired, charge: 900m);

        Assert.True(recoverable.Score > expired.Score,
            $"recoverable $120 scored {recoverable.Score}, expired $900 scored {expired.Score}");
    }

    [Fact]
    public void The_three_buckets_rank_in_the_order_the_recovery_question_implies()
    {
        var recoverable = Score(bucket: RecoveryBuckets.Recoverable).Score;
        var blocked = Score(bucket: RecoveryBuckets.PolicyBlocked).Score;
        var expired = Score(bucket: RecoveryBuckets.Expired).Score;

        Assert.True(recoverable > blocked, $"{recoverable} !> {blocked}");
        Assert.True(blocked > expired, $"{blocked} !> {expired}");
    }

    [Fact]
    public void A_sooner_deadline_beats_a_later_one_at_every_step_of_the_bands()
    {
        // Exercising both sides of each boundary, because a band edge is where an off-by-one in
        // the switch would silently flatten two very different deadlines onto one score.
        // Days run from 0 upward: a window that has *already* closed is a separate case (below),
        // not a step on this ladder.
        int[] days = [0, 1, 7, 8, 14, 15, 30, 31, 180];
        var scores = days.Select(d => Score(days: d).Score).ToList();

        for (var i = 1; i < scores.Count; i++)
            Assert.True(scores[i] <= scores[i - 1],
                $"day {days[i]} scored {scores[i]} against day {days[i - 1]} at {scores[i - 1]}");

        // And the top band is genuinely worth more than the bottom one, not merely non-inferior.
        Assert.True(Score(days: 3).Score > Score(days: 90).Score);
    }

    [Fact]
    public void A_closed_window_is_worth_no_urgency_at_all()
    {
        // Not "low urgency" — there is no deadline left to beat, and charging urgency for one
        // would promote denials nobody can act on ahead of denials that still have time.
        Assert.Equal(0, Score(days: -1).Factors.Single(f => f.Name == "deadline").Weight);
        Assert.Equal(0, Score(days: null).Factors.Single(f => f.Name == "deadline").Weight);
    }

    [Fact]
    public void More_money_beats_less_money_when_everything_else_is_equal()
    {
        var big = Score(charge: 900m).Score;
        var mid = Score(charge: 300m).Score;
        var small = Score(charge: 50m).Score;

        Assert.True(big > mid, $"{big} !> {mid}");
        Assert.True(mid > small, $"{mid} !> {small}");
    }

    [Fact]
    public void An_item_that_needs_review_is_surfaced_ahead_of_one_that_does_not()
    {
        // The review queue is not a sidebar: a specialist cannot act on a note nobody has
        // confirmed, so blocking work ranks above the extra effort of confirming it.
        Assert.True(Score(review: true).Score > Score(review: false).Score);
    }

    [Fact]
    public void Preventability_is_advisory_and_cannot_outrank_money()
    {
        // Q2 asks which denials should never have left the building — but that is a question
        // about process, not about where $600 is sitting. If it ever outranked the amount, the
        // queue would be ordering by embarrassment rather than by cash.
        var preventableSmall = Score(charge: 50m, preventable: true);
        var notPreventableBig = Score(charge: 500m, preventable: false);

        Assert.True(preventableSmall.Score < notPreventableBig.Score,
            $"preventable $50 scored {preventableSmall.Score}, "
          + $"not-preventable $500 scored {notPreventableBig.Score}");

        // It still counts when everything else ties.
        Assert.True(Score(preventable: true).Score > Score(preventable: false).Score);
    }

    [Fact]
    public void A_null_preventability_does_not_get_credit_for_being_true()
    {
        // Two categories have no expert answer at all (D25). Treating "nobody said" as "yes"
        // would be inventing a fact to make the queue look better.
        var unknown = Score(preventable: null).Factors;
        Assert.DoesNotContain(unknown, f => f.Name == "preventable");

        Assert.True(Score(preventable: null).Score < Score(preventable: true).Score);
        Assert.True(Score(preventable: null).Score == Score(preventable: false).Score);
    }

    /* -- the states a queue must not show as work --------------------------------------- */

    [Theory]
    [InlineData("resolved")]
    [InlineData("written_off")]
    [InlineData("closed")]
    [InlineData("RESOLVED")]       // case-insensitive: the status a UI sends, not an enum
    public void A_finished_item_falls_below_every_open_one(string status)
    {
        var finished = Score(status: status, charge: 10_000m,
            bucket: RecoveryBuckets.Recoverable, days: 1);

        // Even the best possible finished item sits under the worst possible open one, so the
        // filter and the score agree instead of one of them quietly dropping history.
        var worstOpen = Score(status: "open", charge: 1m,
            bucket: RecoveryBuckets.Expired, days: null);

        Assert.True(finished.Score < worstOpen.Score,
            $"'{status}' scored {finished.Score}, worst open scored {worstOpen.Score}");
        Assert.Contains(finished.Factors, f => f.Name == "closed");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("in_progress")]
    [InlineData("awaiting_payer")]
    public void An_unfinished_item_is_never_penalised(string status)
    {
        Assert.DoesNotContain(Score(status: status).Factors, f => f.Name == "closed");
        Assert.Equal(0, Score(status: status).Factors.Where(f => f.Name == "closed").Sum(f => f.Weight));
    }

    /* -- the score has to be explainable, not just correct --------------------------------- */

    [Fact]
    public void Every_point_of_the_score_is_named_in_a_factor()
    {
        // The UI shows "why is this number this number?". If the parts do not sum to the whole,
        // the explanation is decoration.
        var score = Score(bucket: RecoveryBuckets.Recoverable, days: 5, charge: 600m,
            review: true, preventable: true);

        Assert.Equal(score.Score, score.Factors.Sum(f => f.Weight));
        Assert.Equal(5, score.Factors.Count);
        Assert.All(score.Factors, f => Assert.False(string.IsNullOrWhiteSpace(f.Because)));
        Assert.Contains("recoverability", score.Explain());
    }

    [Fact]
    public void The_score_is_a_pure_function_of_its_inputs()
    {
        // No clock, no randomness, no ambient state: two calls with the same arguments are the
        // same answer, which is what lets the queue be tested at all.
        var a = Score(bucket: RecoveryBuckets.PolicyBlocked, days: 12, charge: 400m,
            review: true, preventable: true, status: "in_progress");
        var b = Score(bucket: RecoveryBuckets.PolicyBlocked, days: 12, charge: 400m,
            review: true, preventable: true, status: "in_progress");

        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Explain(), b.Explain());
    }

    [Fact]
    public void The_realistic_best_and_worst_stay_inside_a_readable_range()
    {
        // A queue whose scores run into five figures cannot be shown as anything but a number,
        // and a number nobody can read is not an explanation.
        var best = Score(bucket: RecoveryBuckets.Recoverable, days: 3, charge: 900m,
            review: true, preventable: true).Score;
        var worst = Score(bucket: RecoveryBuckets.Expired, days: null, charge: 10m,
            review: false, preventable: false, status: "resolved").Score;

        Assert.Equal(750, best);
        Assert.Equal(-99_940, worst);
    }
}
