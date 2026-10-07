using AQ.Denials.Core;
using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Rules;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// Deadline and recoverability rules, exercised against the real pack so the published
/// 136 = 79 + 26 + 31 split is produced by the code rather than typed into a document.
/// </summary>
public class RecoverabilityTests
{
    private const string Today = "2026-09-30";

    private static Lazy<IngestOutcome> Cached =>
        new(() => IngestPipeline.Run(DataPack.Read(TestData.DataDir)));

    private static PayerWindows Windows() =>
        ReferenceDataReader.BuildPayerWindows(
            ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv")));

    /// <summary>
    /// The claims currently denied, with the check date of the denying remittance and the CARCs
    /// that produced the denial. Mapping lives here rather than in production code because the
    /// presentation layer that will own it is Phase 3; the rules underneath are what matter now.
    /// </summary>
    private static List<RecoveryAssessment.Subject> DeniedSubjects()
    {
        var subjects = new List<RecoveryAssessment.Subject>();

        foreach (var claim in Cached.Value.State.Claims)
        {
            if (claim.Observations.Count == 0 || claim.CurrentStatus != "4") continue;

            var denying = claim.Observations[^1];
            var carcs = denying.Adjustments
                .Select(a => a.Carc)
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            subjects.Add(new RecoveryAssessment.Subject(
                claim.ClaimId, claim.PayerId, claim.Dos, claim.Charge,
                denying.CheckDate, carcs));
        }

        return subjects;
    }

    [Fact]
    public void Every_denied_claim_is_seen_exactly_once()
    {
        var subjects = DeniedSubjects();

        Assert.Equal(136, subjects.Count);
        Assert.Equal(136, subjects.Select(s => s.ClaimId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(27780.00m, subjects.Sum(s => s.Charge));
    }

    [Fact]
    public void The_three_buckets_are_exclusive_and_exhaustive()
    {
        var assessments = RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today);

        Assert.Equal(136, assessments.Count);
        Assert.All(assessments, a => Assert.Contains(a.Bucket, RecoveryBuckets.All));

        // No claim may be assessed under two categories, and no bucket may be blank.
        Assert.Equal(assessments.Count,
            assessments.Select(a => a.ClaimId).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(assessments, a => a.Category == DenialCategory.Fallback);
    }

    [Fact]
    public void The_published_split_is_reproduced_exactly()
    {
        var table = RecoveryAssessment.Summarise(
            RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today));

        table.AssertIdentity();     // 136 == 79 + 26 + 31 and $27,780 == the three parts

        Assert.Equal(136, table.TotalOpen);
        Assert.Equal(27780.00m, table.TotalOpenAmount);

        Assert.Equal(79, table.TotalRecoverable);
        Assert.Equal(16785.00m, table.TotalRecoverableAmount);

        Assert.Equal(26, table.TotalPolicyBlocked);
        Assert.Equal(4460.00m, table.TotalPolicyBlockedAmount);

        Assert.Equal(31, table.TotalExpired);
        Assert.Equal(6535.00m, table.TotalExpiredAmount);
    }

    [Fact]
    public void A_denial_can_only_be_blocked_by_a_policy_of_its_own_payer()
    {
        var assessments = RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today);
        var blocked = assessments.Where(a => a.Bucket == RecoveryBuckets.PolicyBlocked).ToList();

        // Every policy-blocked denial is credentialing: the payer will not retroactively enroll a
        // provider, so the window being open does not make the appeal winnable.
        Assert.Equal(26, blocked.Count);
        Assert.All(blocked, b => Assert.Equal(DenialCategory.Credentialing, b.Category));
        Assert.All(blocked, b => Assert.True(b.AnyDenialRouteOpen));
    }

    [Fact]
    public void An_expired_denial_is_expired_because_its_own_windows_closed()
    {
        var assessments = RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today);
        var expired = assessments.Where(a => a.Bucket == RecoveryBuckets.Expired).ToList();

        Assert.Equal(31, expired.Count);
        Assert.All(expired, e => Assert.False(e.AnyDenialRouteOpen));
        Assert.All(expired, e => Assert.True(
            string.CompareOrdinal(e.AppealEnds, Today) < 0,
            $"{e.ClaimId} appeal ends {e.AppealEnds}, which is still open"));
    }

    [Fact]
    public void Every_open_denial_has_at_least_one_route_left()
    {
        var assessments = RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today);
        var open = assessments.Where(a => a.AnyDenialRouteOpen).ToList();

        // 79 recoverable + 26 policy-blocked = 105 still inside their window.
        Assert.Equal(105, open.Count);
        Assert.Equal(21245.00m, open.Sum(a => a.Charge));
    }

    [Fact]
    public void Appeal_and_corrected_claim_windows_are_equal_for_every_payer_here()
    {
        var windows = Windows();

        foreach (var payerId in windows.PayerIds)
        {
            var w = windows.Get(payerId);
            Assert.True(w.AppealDaysFromDenial == w.CorrectedDaysFromDenial,
                $"{payerId}: appeal {w.AppealDaysFromDenial} != corrected {w.CorrectedDaysFromDenial}");

            // Measured on the pack, not assumed: 180/120/90/60.
            Assert.Contains(w.AppealDaysFromDenial, new[] { 60, 90, 120, 180 });
        }

        // Which means "any route open" and "appeal open" must agree exactly — if a future payer
        // splits them, this assertion is the first thing to fail, deliberately.
        var assessments = RecoveryAssessment.Assess(DeniedSubjects(), Windows(), Today);
        Assert.Equal(assessments.Count(a => a.AnyDenialRouteOpen),
            assessments.Count(a => string.CompareOrdinal(a.AppealEnds, Today) >= 0));
    }

    [Fact]
    public void Timely_filing_never_reopens_a_denial_whose_denial_routes_have_closed()
    {
        // Seven denials sit inside their timely-filing window measured from the date of service
        // while every denial-based route is closed. Counting them as recoverable would overstate
        // the workable book by seven claims; they must land in Expired.
        var subjects = DeniedSubjects();
        var assessments = RecoveryAssessment.Assess(subjects, Windows(), Today);
        var windows = Windows();

        var staleButFilingOpen = assessments
            .Where(a => !a.AnyDenialRouteOpen)
            .Where(a =>
            {
                var s = subjects.Single(x => x.ClaimId == a.ClaimId);
                var tf = windows.TimelyFilingEndDate(a.PayerId, s.DateOfService);
                return string.CompareOrdinal(tf, Today) >= 0;
            })
            .ToList();

        Assert.Equal(7, staleButFilingOpen.Count);
        Assert.All(staleButFilingOpen, a =>
            Assert.Equal(RecoveryBuckets.Expired, a.Bucket));
    }
}

/// <summary>Window arithmetic in isolation, including the error paths the pack never reaches.</summary>
public class PayerWindowsTests
{
    [Fact]
    public void Adding_days_crosses_month_and_year_boundaries_correctly()
    {
        Assert.Equal("2026-01-31", PayerWindows.AddDays("2026-01-01", 30));
        Assert.Equal("2026-03-01", PayerWindows.AddDays("2026-02-28", 1));   // non-leap year
        Assert.Equal("2027-01-01", PayerWindows.AddDays("2026-12-31", 1));
        Assert.Equal("2026-06-15", PayerWindows.AddDays("2026-06-15", 0));
    }

    [Fact]
    public void A_malformed_date_fails_loudly_instead_of_producing_a_wrong_deadline()
    {
        Assert.Throws<FormatException>(() => PayerWindows.AddDays("2026-13-45", 30));
        Assert.Throws<FormatException>(() => PayerWindows.AddDays("06/15/2026", 30));
        Assert.Throws<FormatException>(() => PayerWindows.AddDays("", 30));
        Assert.Throws<FormatException>(() => PayerWindows.AddDays("garbage", 30));
    }

    [Fact]
    public void An_unknown_payer_has_no_window_rather_than_a_default_one()
    {
        var windows = ReferenceDataReader.BuildPayerWindows(
            ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv")));

        Assert.Throws<KeyNotFoundException>(() => windows.Get("ZZZZZ"));
        Assert.False(windows.TryGet("ZZZZZ", out _));

        // A default would silently give every claim the same deadline.
        Assert.True(windows.TryGet("NS401", out var ns401));
        Assert.Equal(180, ns401.AppealDaysFromDenial);
    }

    [Fact]
    public void Window_boundaries_are_inclusive()
    {
        var windows = ReferenceDataReader.BuildPayerWindows(
            ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv")));

        // Meridian PPO: 90 days from the denial date, and 90 days is still open.
        Assert.True(windows.AnyDenialRouteOpen("MRD55", "2026-07-02", "2026-09-30"));
        Assert.Equal("2026-09-30", windows.DenialRouteEndDate("MRD55", "2026-07-02"));
        Assert.False(windows.AnyDenialRouteOpen("MRD55", "2026-07-01", "2026-09-30"));
    }
}

/// <summary>Bucket assignment and the exact-sum invariant, on inputs where all three are reachable.</summary>
public class RecoveryBucketsTests
{
    [Fact]
    public void Expired_wins_over_a_policy_bar_whatever_the_category()
    {
        // Evaluated in that order deliberately: an out-of-window denial is out of window no
        // matter what the payer's policy says about it.
        Assert.Equal(RecoveryBuckets.Expired,
            RecoveryBuckets.Assign(DenialCategory.Credentialing, anyDenialRouteOpen: false));
        Assert.Equal(RecoveryBuckets.PolicyBlocked,
            RecoveryBuckets.Assign(DenialCategory.Credentialing, anyDenialRouteOpen: true));
        Assert.Equal(RecoveryBuckets.Recoverable,
            RecoveryBuckets.Assign(DenialCategory.Authorization, anyDenialRouteOpen: true));
        Assert.Equal(RecoveryBuckets.Expired,
            RecoveryBuckets.Assign(DenialCategory.Authorization, anyDenialRouteOpen: false));
    }

    [Fact]
    public void The_identity_is_asserted_on_every_aggregation()
    {
        var table = BucketTableBuilder.Build(new[]
        {
            new OpenDenial("C1", DenialCategory.Authorization, 100m, RecoveryBuckets.Recoverable),
            new OpenDenial("C2", DenialCategory.Credentialing, 50m, RecoveryBuckets.PolicyBlocked),
            new OpenDenial("C3", DenialCategory.TimelyFiling, 25m, RecoveryBuckets.Expired),
        });

        Assert.Equal(3, table.TotalOpen);
        Assert.Equal(175m, table.TotalOpenAmount);
        Assert.Equal(1, table.TotalRecoverable);
        Assert.Equal(1, table.TotalPolicyBlocked);
        Assert.Equal(1, table.TotalExpired);
        table.AssertIdentity();
    }

    [Fact]
    public void The_summary_refuses_a_claim_that_appears_twice()
    {
        var duplicate = new[]
        {
            new RecoveryAssessment.Subject("C1", "NS401", "2026-05-01", 100m, "2026-07-01", ["11"]),
            new RecoveryAssessment.Subject("C1", "NS401", "2026-05-01", 100m, "2026-07-01", ["11"]),
        };

        var windows = ReferenceDataReader.BuildPayerWindows(
            ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecoveryAssessment.Summarise(
                RecoveryAssessment.Assess(duplicate, windows, "2026-09-30")));

        Assert.Contains("assessed more than once", ex.Message);
    }
}

/// <summary>The deterministic category taxonomy — the single place a category is decided.</summary>
public class DenialCategoryTests
{
    [Fact]
    public void Timely_filing_beats_everything_else()
    {
        // A claim the payer will not accept at all cannot be recovered by recoding it.
        Assert.Equal(DenialCategory.TimelyFiling,
            DenialCategory.Of(["29", "11", "B7"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.TimelyFiling,
            DenialCategory.Of(["B7", "29"], "SMP12", "2026-05-01"));
    }

    [Fact]
    public void Credentialing_beats_the_clinical_categories()
    {
        Assert.Equal(DenialCategory.Credentialing,
            DenialCategory.Of(["B7", "197", "50"], "CSA77", "2026-05-01"));
    }

    [Fact]
    public void An_authorization_denial_before_the_policy_effective_date_is_payer_error()
    {
        // Sunshine Medicaid's SNF authorization bulletin applies to dates of service on or after
        // 2026-04-01; a claim before that date cannot have required an authorization that did not
        // exist, so the payer is at fault rather than the biller.
        Assert.Equal(DenialCategory.PayerError,
            DenialCategory.Of(["197"], "SMP12", "2026-03-31"));
        Assert.Equal(DenialCategory.Authorization,
            DenialCategory.Of(["197"], "SMP12", "2026-04-01"));
        Assert.Equal(DenialCategory.Authorization,
            DenialCategory.Of(["197"], "NS401", "2026-03-31"));   // other payers unaffected
    }

    [Fact]
    public void Each_clinical_carC_maps_to_its_own_category()
    {
        Assert.Equal(DenialCategory.Eligibility, DenialCategory.Of(["27"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.MedicalNecessity, DenialCategory.Of(["50"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.CodingDiagnosis, DenialCategory.Of(["11"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.CodingFrequency, DenialCategory.Of(["151"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.CodingModifier, DenialCategory.Of(["97"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.DuplicateUnvalidated, DenialCategory.Of(["18"], "NS401", "2026-05-01"));
    }

    [Fact]
    public void An_uncategorised_denial_is_visible_rather_than_absorbed()
    {
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of(["45"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of([], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of([""], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.Fallback,
            DenialCategory.Of(["45"], "NS401", dateOfService: null));
    }

    [Fact]
    public void CarC_lookups_ignore_whitespace_but_never_match_substrings()
    {
        Assert.Equal(DenialCategory.CodingDiagnosis, DenialCategory.Of([" 11 "], "NS401", "2026-05-01"));
        // "111" is not CARC 11 and "1" is not CARC 11.
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of(["111"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.CodingModifier,
            DenialCategory.Of(["111", "97"], "NS401", "2026-05-01"));
    }

    /* ---- what each category means for the people doing the work --------------------- */

    [Fact]
    public void Every_category_can_be_routed_and_has_something_to_do_first()
    {
        // A category with no outcome row would throw at run time; this proves it cannot.
        Assert.Equal(DenialCategory.All.Count, DenialCategory.Outcomes.Count);

        foreach (var category in DenialCategory.All)
        {
            var outcome = DenialCategory.OutcomeFor(category);

            Assert.False(string.IsNullOrWhiteSpace(outcome.Team),
                $"{category} has no owning team");
            Assert.False(string.IsNullOrWhiteSpace(outcome.NextAction),
                $"{category} has no next action");
        }
    }

    [Fact]
    public void An_unknown_category_fails_loudly_instead_of_returning_a_plausible_default()
    {
        // The dangerous version of this returns Fallback's team and an action that reads fine —
        // and the mistake is never seen again.
        var ex = Assert.Throws<KeyNotFoundException>(
            () => DenialCategory.OutcomeFor("Something invented"));
        Assert.Contains("Something invented", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outcome_table_agrees_with_the_expert_sample_on_every_row()
    {
        var labels = ReferenceDataReader.ReadLabeledSample(
            TestData.ReadText("labeled_denials_sample.csv"));

        // Transcribed-by-hand expectations would only prove the code matches the transcription.
        // This reads the expert's own file and checks the table against it, so a mistyped team
        // or a flipped preventability flag fails here rather than in a report someone believes.
        foreach (var label in labels)
        {
            var outcome = DenialCategory.OutcomeFor(label.RootCauseCategory);

            Assert.True(outcome.CoveredByLabeledSample,
                $"'{label.RootCauseCategory}' is in the sample but the table treats it as uncovered");
            Assert.Equal(label.OwningTeam, outcome.Team);
            Assert.Equal(label.PreventableAtPrebill, outcome.PreventableAtPrebill);
        }
    }

    [Fact]
    public void Only_categories_the_sample_actually_covers_claim_the_sample_s_authority()
    {
        var labels = ReferenceDataReader.ReadLabeledSample(
            TestData.ReadText("labeled_denials_sample.csv"));

        var coveredBySample = labels
            .Select(l => l.RootCauseCategory)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(coveredBySample);

        foreach (var category in DenialCategory.All)
        {
            var outcome = DenialCategory.OutcomeFor(category);

            // Derived, not hard-coded: the flag must equal "does the expert's file say anything
            // about this category" — so a new category added to the taxonomy is automatically
            // treated as unestablished until someone labels it.
            Assert.Equal(
                coveredBySample.Contains(category),
                outcome.CoveredByLabeledSample);
        }

        // And where nobody has established preventability, the answer is null — not a confident
        // Yes that happens to look reasonable.
        foreach (var category in DenialCategory.All.Where(
                     c => !coveredBySample.Contains(c)))
        {
            Assert.Null(DenialCategory.OutcomeFor(category).PreventableAtPrebill);
        }
    }
}
