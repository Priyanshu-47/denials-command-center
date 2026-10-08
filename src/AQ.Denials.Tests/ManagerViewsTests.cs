using AQ.Denials.Api.Reports;
using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Rules;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The manager's two screens: how much money is stuck and where, and which pre-bill checks
/// would have stopped it.
/// </summary>
/// <remarks>
/// <para>
/// Both are money views, so both are pinned to the figures reconciliation already established.
/// A number that appears on two screens and disagrees is worse than a number that appears once,
/// because neither screen can tell you which one to believe.
/// </para>
/// <para>
/// The prevention checks are asserted on <b>structure and bounds</b> as well as value: a check
/// that "catches" every denial by flagging every claim is not a check, and the pairing of
/// <c>DenialsCaught</c> with <c>ClaimsFlagged</c> is what makes the difference visible.
/// </para>
/// </remarks>
public class ManagerViewsTests
{
    private const string Today = "2026-09-30";

    private static readonly Lazy<IngestOutcome> Cached =
        new(() => IngestPipeline.Run(DataPack.Read(TestData.DataDir)));

    private static IngestOutcome Run() => Cached.Value;

    private static PayerWindows Windows() =>
        ReferenceDataReader.BuildPayerWindows(
            ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv")));

    private static AnalyticsReport Report() =>
        Analytics.Build(Run().State.Claims, Windows(), Today);

    private static IReadOnlyList<PreventionCheck> Checks() =>
        PreventionAnalyzer.Analyse(Run().State.Claims, Windows(), Today);

    private static string Describe(IEnumerable<PreventionCheck> checks) =>
        string.Join(" | ", checks.Select(c =>
            $"{c.Id}: flagged {c.ClaimsFlagged}/{c.ClaimsScreened}, "
          + $"caught {c.DenialsCaught}/{c.OpenDenialsTotal} = ${c.AmountCaught} ({c.Confidence})"));

    /* -- Q1: how much is stuck, and how much can still be recovered ------------------- */

    [Fact]
    public void Money_at_risk_separates_the_three_things_that_respond_to_three_actions()
    {
        var m = Report().MoneyAtRisk;

        Assert.Equal(136, m.OpenCount);
        Assert.Equal(27780.00m, m.OpenAmount);

        // Billed and still undecided — not denials, and not counted as such.
        Assert.Equal(58, m.PendingCount);
        Assert.Equal(12600.00m, m.PendingAmount);

        // Accepted claims whose remittance passed the claim and then paid a line nothing
        // (CARC 97) — Q1 asked for this to stay separate from denials, because it is not work
        // the denial queue handles.
        Assert.Equal(19, m.ZeroPaidLineCount);
        Assert.Equal(3365.00m, m.ZeroPaidLineAmount);
    }

    [Fact]
    public void The_three_buckets_still_sum_to_the_open_book_on_this_screen()
    {
        var m = Report().MoneyAtRisk;

        Assert.Equal(RecoveryBuckets.All, m.Buckets.Select(b => b.Bucket).ToArray());
        Assert.Equal(79, m.Buckets[0].Count);
        Assert.Equal(16785.00m, m.Buckets[0].Amount);
        Assert.Equal(26, m.Buckets[1].Count);
        Assert.Equal(4460.00m, m.Buckets[1].Amount);
        Assert.Equal(31, m.Buckets[2].Count);
        Assert.Equal(6535.00m, m.Buckets[2].Amount);

        Assert.Equal(m.OpenCount, m.Buckets.Sum(b => b.Count));
        Assert.Equal(m.OpenAmount, m.Buckets.Sum(b => b.Amount));
    }

    [Fact]
    public void Category_breakdown_matches_the_distribution_established_in_phase_one()
    {
        var rows = Report().ByCategory;

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Credentialing"] = 26,
            ["Coding - diagnosis"] = 24,
            ["Authorization"] = 19,
            ["Eligibility"] = 17,
            ["Medical necessity"] = 17,
            ["Coding - frequency"] = 11,
            ["Billing - timely filing"] = 9,
            ["Payer error"] = 7,
            ["Duplicate submission (unvalidated)"] = 6,
        };

        foreach (var (key, count) in expected)
        {
            var row = rows.FirstOrDefault(r => r.Key == key);
            Assert.True(row is not null,
                $"category '{key}' missing from the analytics breakdown. Found: "
              + string.Join(", ", rows.Select(r => $"{r.Key}={r.Count}")));
            Assert.Equal(count, row!.Count);
        }

        // Nothing extra, nothing missing: the breakdown is a partition of the 136.
        Assert.Equal(136, rows.Sum(r => r.Count));
        Assert.Equal(27780.00m, rows.Sum(r => r.Amount));
    }

    [Fact]
    public void Every_dimension_is_a_partition_of_the_open_book()
    {
        // The four operational dimensions are cuts of the same 136, so each must account for
        // every claim and every dollar exactly once. A grouping that drops a null provider
        // would silently make the "by provider" total look smaller than the denial total.
        var report = Report();

        foreach (var dimension in new[]
                 {
                     report.ByPayer, report.ByCategory, report.ByProvider,
                     report.ByCoder, report.ByFacility,
                 })
        {
            Assert.Equal(136, dimension.Sum(d => d.Count));
            Assert.Equal(27780.00m, dimension.Sum(d => d.Amount));
        }
    }

    [Fact]
    public void Denial_rate_is_reported_over_decided_claims_only()
    {
        var rows = Report().ByPreBill;

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "N", "Y" }, rows.Select(r => r.Reviewed).OrderBy(x => x).ToArray());

        foreach (var row in rows)
        {
            Assert.InRange(row.DenialRate, 0m, 1m);
            Assert.InRange(row.Denials, 0, row.Count);
        }

        // A rate over undecided claims would be a rate over a moving denominator.
        Assert.Equal(1164, rows.Sum(r => r.Count));
        Assert.Equal(136, rows.Sum(r => r.Denials));
    }

    [Fact]
    public void The_trend_is_grouped_by_the_month_the_payer_denied()
    {
        var trend = Report().Trend;

        Assert.NotEmpty(trend);
        Assert.Equal(136, trend.Sum(t => t.Count));
        Assert.Equal(27780.00m, trend.Sum(t => t.Amount));
        Assert.All(trend, t => Assert.Matches(@"^2026-\d{2}$", t.Period));
        Assert.Equal(trend.Select(t => t.Period).OrderBy(p => p, StringComparer.Ordinal),
                     trend.Select(t => t.Period));
    }

    /* -- which checks would have stopped the most ------------------------------------- */

    [Fact]
    public void Every_check_is_paired_with_what_it_would_have_cost_to_run()
    {
        var checks = Checks();

        Assert.Equal(5, checks.Count);

        foreach (var check in checks)
        {
            // Without both halves, "this check would have caught 26 denials" is a number with
            // no denominator, and the reader cannot tell whether it flagged 30 claims or 1,200.
            Assert.Equal(136, check.OpenDenialsTotal);
            Assert.Equal(27780.00m, check.OpenDenialAmountTotal);
            Assert.InRange(check.DenialsCaught, 0, check.OpenDenialsTotal);
            Assert.InRange(check.ClaimsFlagged, 0, check.ClaimsScreened);
            Assert.InRange(check.CatchRate, 0m, 1m);
            Assert.InRange(check.BurdenRate, 0m, 1m);
            Assert.True(check.DenialsCaught == 0 || check.AmountCaught > 0m,
                $"{check.Id} caught {check.DenialsCaught} denials but ${check.AmountCaught}");

            Assert.False(string.IsNullOrWhiteSpace(check.Name));
            Assert.False(string.IsNullOrWhiteSpace(check.Question));
        }
    }

    [Fact]
    public void Checks_come_out_worst_first_so_the_list_can_be_read_top_down()
    {
        var checks = Checks();

        var caught = checks.Select(c => c.DenialsCaught).ToList();
        var sorted = caught.OrderByDescending(c => c).ToList();

        Assert.True(caught.SequenceEqual(sorted),
            "not ordered by denials caught: " + Describe(checks));

        // The identity check that makes ordering well-defined where two checks tie.
        for (var i = 1; i < checks.Count; i++)
        {
            if (checks[i - 1].DenialsCaught == checks[i].DenialsCaught)
                Assert.True(checks[i - 1].AmountCaught >= checks[i].AmountCaught,
                    Describe(checks));
        }
    }

    [Fact]
    public void A_check_whose_input_data_is_absent_says_so_instead_of_guessing()
    {
        var enrollment = Assert.Single(Checks(), c => c.Id == "provider-enrolled-on-dos");

        // The largest category in the pack has no enrollment table to check against. Reporting
        // zero would read as "nothing to do here"; inferring a number would put the most
        // confident-looking figure on the weakest evidence in the project.
        Assert.False(enrollment.Measurable);
        Assert.Equal("not_measurable", enrollment.Confidence);
        Assert.Equal(0, enrollment.DenialsCaught);
        Assert.Equal(0m, enrollment.AmountCaught);
        Assert.NotNull(enrollment.WhyNotMeasured);
        Assert.Contains("enrollment", enrollment.WhyNotMeasured, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cannot_evaluate_without_data", enrollment.Rule["status"]);
    }

    [Fact]
    public void Every_check_exports_as_a_machine_readable_rule()
    {
        // The brief asks for the checks as JSON a pre-bill system could apply, so each one must
        // carry enough structure to be executed — not a sentence describing an intention.
        foreach (var check in Checks())
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Rule["type"]));

            if (!check.Measurable) continue;   // still exports, marked as blocked on data

            Assert.Contains("blocks", check.Rule.Keys);
            switch (check.Id)
            {
                case "auth-number-present":
                    Assert.Equal("required_field", check.Rule["type"]);
                    Assert.Equal("auth_number", check.Rule["field"]);
                    Assert.Equal("not_empty", check.Rule["operator"]);
                    break;
                case "prebill-review-recorded":
                    Assert.Equal("Y", check.Rule["value"]);
                    break;
                case "prior-paid-sibling":
                    Assert.Equal("member_id,dos,cpt", check.Rule["match_on"]);
                    break;
                case "filing-headroom":
                    Assert.Contains("payer_rules.csv", check.Rule["window_source"]);
                    Assert.True(int.TryParse(check.Rule["threshold_percent"], out var pct));
                    Assert.InRange(pct, 1, 100);
                    break;
            }
        }
    }

    [Fact]
    public void No_patient_identifier_reaches_an_exported_rule()
    {
        // The duplicate check keys on member id in order to find a prior paid sibling. The key
        // is built and discarded inside the analyzer; if a member id ever reached the export,
        // a rules file emailed to a payer would be carrying PHI.
        var exported = string.Join("\n", Checks().SelectMany(c => c.Rule.Values));

        var memberIds = Run().State.Claims
            .Select(c => c.MemberId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.DoesNotContain(memberIds, id => exported.Contains(id, StringComparison.Ordinal));

        // And no patient names, for the same reason.
        var names = Run().State.Claims
            .SelectMany(c => new[] { c.PatientFirst, c.PatientLast })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.DoesNotContain(names, n => exported.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_check_that_catches_more_is_not_just_flagging_more()
    {
        // The failure mode this guards: define the check as "flag every claim" and it catches
        // all 136. Burden is reported beside benefit so that reading is impossible.
        var checks = Checks();

        var universal = checks.FirstOrDefault(c => c.ClaimsFlagged == c.ClaimsScreened);
        if (universal is null) return;

        Assert.Equal(universal.OpenDenialsTotal, universal.DenialsCaught);
        Assert.Equal(1m, universal.BurdenRate);
        Assert.True(universal.BurdenRate >= universal.CatchRate,
            $"{universal.Id} flags {universal.BurdenRate:P0} of claims to catch "
          + $"{universal.CatchRate:P0} of denials");
    }
}
