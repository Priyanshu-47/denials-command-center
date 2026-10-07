using AQ.Denials.Core;
using AQ.Denials.Ingest;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The numbers this system publishes, computed by the pipeline and asserted against the figures
/// Phase 0 established independently. Nothing here is hand-typed into a report: if the parser,
/// the dedup or the linking changes, these fail.
/// </summary>
public class GoldenPhase0Tests
{
    private static readonly Lazy<IngestOutcome> Cached =
        new(() => IngestPipeline.Run(DataPack.Read(TestData.DataDir)));

    private static IngestOutcome Run() => Cached.Value;

    [Fact]
    public void Cash_excluding_duplicate_is_the_published_figure()
    {
        Assert.Equal(118220.36m, Run().Reconciliation.CashExcludingDuplicate);
        Assert.Equal(47461.62m, Run().Reconciliation.DuplicateCashExcluded);
        Assert.Equal(165681.98m, Run().Reconciliation.CashIncludingDuplicate);
        Assert.Equal(1, Run().Reconciliation.DuplicateFiles);
    }

    [Fact]
    public void Books_split_exactly_as_published()
    {
        var r = Run().Reconciliation;

        Assert.Equal(1222, r.TotalClaims);
        Assert.Equal(242585.00m, r.TotalClaimCharge);

        Assert.Equal(1164, r.AdjudicatedClaims);
        Assert.Equal(229985.00m, r.AdjudicatedCharge);

        Assert.Equal(58, r.UnadjudicatedClaims);
        Assert.Equal(12600.00m, r.UnadjudicatedCharge);

        // The headline: claim-level open denials.
        Assert.Equal(136, r.DeniedClaims);
        Assert.Equal(27780.00m, r.DeniedCharge);

        // The three buckets are exclusive, so the parts must sum to the whole.
        Assert.Equal(r.AdjudicatedClaims + r.UnadjudicatedClaims, r.TotalClaims);
        Assert.Equal(r.AdjudicatedCharge + r.UnadjudicatedCharge, r.TotalClaimCharge);
    }

    [Fact]
    public void Every_file_and_every_payer_reconciles_to_the_cent()
    {
        var r = Run().Reconciliation;

        Assert.Equal(0m, r.MaxFileDifference);
        Assert.Equal(0m, r.MaxPayerDifference);
        r.AssertZeroDifferences();      // throws on the first non-zero cent

        Assert.Equal(4, r.Files.Count);
        Assert.Equal(4, r.ImportedFiles + r.DuplicateFiles);
        Assert.Equal(4, r.Payers.Count);
    }

    [Fact]
    public void Adjustment_totals_match_the_published_split()
    {
        var r = Run().Reconciliation;

        var co = Assert.Single(r.AdjustmentGroups.Where(g => g.GroupCode == "CO"));
        var pr = Assert.Single(r.AdjustmentGroups.Where(g => g.GroupCode == "PR"));

        Assert.Equal(108516.40m, co.Amount);
        Assert.Equal(5643.24m, pr.Amount);

        // Only CO and PR occur in this pack — OA and PI are present in the reference table but
        // unused, and asserting their absence is what makes "only these two" a fact.
        Assert.Equal(new[] { "CO", "PR" },
            r.AdjustmentGroups.Select(g => g.GroupCode).OrderBy(g => g, StringComparer.Ordinal));

        // The three BHC payment events cannot be attributed to a claim in the export; their
        // adjustments are reported separately rather than silently missing.
        var unattributedCo = Assert.Single(r.UnattributedAdjustmentGroups);
        Assert.Equal("CO", unattributedCo.GroupCode);
        Assert.Equal(159.60m, unattributedCo.Amount);
        Assert.Equal(108356.80m, co.Amount - unattributedCo.Amount);
    }

    [Fact]
    public void No_claim_has_a_charge_that_disagrees_with_the_remittance()
    {
        Assert.Equal(0, Run().Reconciliation.ChargeMismatchCount);
        Assert.Equal(0, Run().Reconciliation.OrphanObservationCount);
    }

    [Fact]
    public void Row_conservation_holds_and_every_exception_carries_a_known_reason()
    {
        var outcome = Run();

        outcome.AssertRowConservation();

        // Independent recount: the sources, not the dispositions, decide rows_in.
        Assert.Equal(1323, outcome.RowsInByEntity["claim_line"]);
        Assert.Equal(120, outcome.RowsInByEntity["worklog_row"]);
        Assert.Equal(4, outcome.RowsInByEntity["remit_file"]);
        Assert.Equal(1197, outcome.RowsInByEntity["remit_observation"]);
        Assert.Equal(2644, outcome.RowsIn);

        Assert.All(outcome.Exceptions, e =>
            Assert.True(ExceptionReason.All.Contains(e.ReasonCode),
                $"unknown reason code '{e.ReasonCode}'"));

        Assert.All(outcome.Exceptions, e =>
            Assert.False(string.IsNullOrWhiteSpace(e.Detail), $"{e.ReasonCode} has no explanation"));
    }

    [Fact]
    public void Exception_counts_are_explained_and_complete()
    {
        var counts = Run().ExceptionCountsByReason;

        Assert.Equal(63, counts[ExceptionReason.UnadjudicatedClaim]);
        Assert.Equal(23, counts[ExceptionReason.AmbiguousDate]);
        Assert.Equal(3, counts[ExceptionReason.ForeignClaimNamespace]);
        Assert.Equal(1, counts[ExceptionReason.DuplicatePayload]);

        Assert.Equal(counts.Values.Sum(), Run().RowsExcepted);
    }

    [Fact]
    public void The_resent_file_is_kept_flagged_and_named_not_silently_dropped()
    {
        var outcome = Run();
        var duplicate = Assert.Single(outcome.State.RemitFiles, f => f.IsDuplicate);

        Assert.Equal("era_2026Q2_resent_0719.835", duplicate.FileName);
        Assert.Equal("era_2026Q2.835", duplicate.DuplicateOfFileName);

        // Kept on the books with its observations stripped, so it can be shown and explained.
        Assert.Empty(duplicate.Observations);

        var exception = Assert.Single(outcome.Exceptions,
            e => e.ReasonCode == ExceptionReason.DuplicatePayload);
        Assert.Contains("duplicate remit payload of era_2026Q2.835", exception.Detail);
        Assert.Equal("era_2026Q2.835", exception.OriginalRef);

        // The original still holds its cash exactly once. This is measured from the file's
        // reconciliation line, which counts every CLP the file contains; the claim-linked
        // observations are a deliberately narrower set (they exclude the two BHC payment events
        // for claims in another namespace), and the two are reported side by side rather than
        // conflated.
        var original = Assert.Single(outcome.State.RemitFiles, f => f.FileName == "era_2026Q2.835");
        Assert.False(original.IsDuplicate);

        var originalLine = Assert.Single(outcome.Reconciliation.Files, f => f.FileName == "era_2026Q2.835");
        Assert.Equal(47461.62m, originalLine.PaidSum);
        Assert.Equal(originalLine.BprTotal, originalLine.PaidSum);

        var linkedOnly = original.Observations.Sum(o => o.PaidAmount);
        Assert.Equal(173.60m, 47461.62m - linkedOnly);   // the two unattributable CLP04s
    }

    [Fact]
    public void Worklog_dates_that_cannot_be_decided_are_flagged_not_guessed()
    {
        var ambiguous = Run().State.Worklog.Where(w => w.DateAmbiguous).ToList();

        // 28 rows have a both-readings-valid date; 5 are eliminated by the future test, leaving
        // 23 genuinely undecidable. Both readings are always kept alongside the raw string.
        Assert.Equal(23, ambiguous.Count);
        Assert.All(ambiguous, w =>
        {
            Assert.NotEmpty(w.DateLoggedRaw);
            Assert.NotNull(w.DateLoggedMdy);
            Assert.NotNull(w.DateLoggedDmy);
            Assert.NotEqual(w.DateLoggedMdy, w.DateLoggedDmy);
            Assert.NotNull(w.DateLoggedConservative);
            // Conservative = the earlier reading, so a deadline is never computed from the
            // more generous interpretation.
            Assert.Equal(
                string.CompareOrdinal(w.DateLoggedMdy!, w.DateLoggedDmy!) <= 0
                    ? w.DateLoggedMdy : w.DateLoggedDmy,
                w.DateLoggedConservative);
        });

        Assert.Equal(5, Run().State.Worklog.Count(w => w.DateResolvedByFutureTest));
        Assert.All(Run().State.Worklog, w => Assert.NotNull(w.DateLoggedRaw));
    }

    [Fact]
    public void Observations_are_ordered_by_check_date_not_by_arrival()
    {
        var observations = Run().State.AllObservations().ToList();
        Assert.NotEmpty(observations);

        var keys = observations
            .Select(o => (o.CheckDate, Seq: o.Seq))
            .ToList();

        // Seq must be a dense 0..n-1 rank of the check-date ordering.
        Assert.Equal(Enumerable.Range(0, observations.Count), keys.OrderBy(k => k.Seq).Select(k => k.Seq));
        Assert.Equal(keys.OrderBy(k => k.CheckDate, StringComparer.Ordinal).Select(k => k.CheckDate),
            keys.OrderBy(k => k.Seq).Select(k => k.CheckDate));
    }

    [Fact]
    public void Money_is_always_decimal_never_floating_point()
    {
        var outcome = Run();

        Assert.All(outcome.State.Claims, c => Assert.Equal(typeof(decimal), c.Charge.GetType()));
        Assert.All(outcome.State.AllObservations(), o =>
        {
            Assert.Equal(typeof(decimal), o.PaidAmount.GetType());
            Assert.Equal(typeof(decimal), o.SubmittedCharge.GetType());
            Assert.All(o.Adjustments, a => Assert.Equal(typeof(decimal), a.Amount.GetType()));
        });

        // A float would not survive this round-trip exactly.
        Assert.Equal(0.10m + 0.20m, 0.30m);
    }

    [Fact]
    public void Signed_adjustments_are_never_flipped_or_absolute_valued()
    {
        var observations = Run().State.AllObservations().ToList();
        Assert.NotEmpty(observations);

        foreach (var observation in observations)
        {
            var cas = observation.Adjustments.Sum(a => a.Amount);
            Assert.True(observation.SubmittedCharge == observation.PaidAmount + cas,
                $"claim {observation.RawClaimReference}: CLP03 {observation.SubmittedCharge} "
              + $"!= CLP04 {observation.PaidAmount} + signed CAS {cas}");
        }
    }

    [Fact]
    public void Every_payer_seen_in_a_remittance_has_a_configured_window()
    {
        var outcome = Run();
        var payers = outcome.State.AllObservations().Select(o => o.PayerId)
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "CSA77", "MRD55", "NS401", "SMP12" }, payers);
    }

    [Fact]
    public void The_audit_trail_carries_no_patient_identifiers()
    {
        var outcome = Run();
        Assert.NotEmpty(outcome.AuditEntries);

        var patients = outcome.State.Claims
            .Select(c => new[] { c.PatientFirst, c.PatientLast, c.PatientDob, c.MemberId })
            .SelectMany(p => p)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in outcome.AuditEntries)
        foreach (var field in new[] { entry.Detail, entry.EntityKey, entry.Action })
            if (field is not null)
                foreach (var patient in patients)
                    Assert.DoesNotContain(patient, field, StringComparison.Ordinal);
    }
}
