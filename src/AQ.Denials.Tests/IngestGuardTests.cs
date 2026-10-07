using AQ.Denials.Core;
using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The guards the real happenstance of this data pack does not exercise: orphan remits, foreign
/// claim namespaces, cross-file natural-key collisions, unknown codes and missing inputs.
/// </summary>
/// <remarks>
/// These are tested against deliberately constructed inputs because the alternative is finding
/// out from production. Each test asserts both that the row is <b>flagged</b> and that it is
/// <b>accounted for</b> — a guard that throws the row away would still look like it worked.
/// </remarks>
public class IngestGuardTests
{
    private static TestPack NewPack(string name) => TestPack.Create(name);

    private static ExceptionRow? Reason(IngestOutcome outcome, string reason) =>
        outcome.Exceptions.FirstOrDefault(e => e.ReasonCode == reason);

    [Fact]
    public void A_remit_for_a_claim_nobody_exported_is_an_orphan_not_a_new_claim()
    {
        var pack = NewPack("orphan");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m))
                .Remit("b.835", TestPack.Simple835("GPP-2026-000002", 50.00m, 0m, "CO", "45", 50.00m,
                    interchange: 2));

            var outcome = IngestPipeline.Run(pack.Build());

            var orphan = Reason(outcome, ExceptionReason.OrphanRemit);
            Assert.NotNull(orphan);
            Assert.Contains("GPP-2026-000002", orphan!.Detail);
            Assert.Equal("GPP-2026-000002", orphan.ClaimId);

            // One claim in, and it stays exactly one claim — an unknown reference never mints a row.
            Assert.Single(outcome.State.Claims);
            Assert.Equal(1, outcome.Reconciliation.OrphanObservationCount);
            Assert.Equal(1, outcome.Reconciliation.AdjudicatedClaims);
            Assert.Equal(0, outcome.Reconciliation.UnadjudicatedClaims);

            outcome.AssertRowConservation();
            outcome.Reconciliation.AssertZeroDifferences();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void A_foreign_claim_namespace_is_reported_rather_than_mangled_into_ours()
    {
        var pack = NewPack("foreign");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m))
                // A second file from a different billing namespace entirely.
                .Remit("b.835", TestPack.Simple835("BHC-2026-456493", 50.00m, 0m, "CO", "45", 50.00m,
                    interchange: 2));

            var outcome = IngestPipeline.Run(pack.Build());

            var foreign = Assert.Single(outcome.Exceptions,
                e => e.ReasonCode == ExceptionReason.ForeignClaimNamespace);
            Assert.Contains("BHC-2026-456493", foreign.Detail);

            // Stripping the prefix to make it look like GPP-2026-456493 would fabricate a link
            // to a claim that does not exist.
            Assert.DoesNotContain(outcome.State.Claims, c => c.ClaimId == "GPP-2026-456493");
            Assert.Single(outcome.State.Claims);
            outcome.AssertRowConservation();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void The_natural_key_guard_refuses_a_claim_event_already_accepted_from_another_file()
    {
        var pack = NewPack("naturalkey");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m);

            var first = TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m);

            // Same payer, same trace, same check date, same control number — the same payment
            // event — but a different payload overall (an extra SVC line), so the whole-file
            // payload hash cannot see the overlap.
            var second = TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m)
                .Replace("SE*8*0001~", "SVC*HC:99213*100.00*0.00~\nSE*9*0001~");

            pack.Remit("a.835", first);
            pack.Remit("b.835", second);

            var outcome = IngestPipeline.Run(pack.Build());

            // Different payloads, so this is NOT caught as a duplicate file.
            Assert.Equal(0, outcome.Exceptions.Count(e => e.ReasonCode == ExceptionReason.DuplicatePayload));

            var refused = Assert.Single(outcome.Exceptions,
                e => e.ReasonCode == ExceptionReason.DuplicateClaimObservation);
            Assert.Equal("a.835", refused.OriginalRef);

            // The claim keeps exactly one adjudication event, not two.
            var claim = Assert.Single(outcome.State.Claims);
            Assert.Single(claim.Observations);

            outcome.AssertRowConservation();
            outcome.Reconciliation.AssertZeroDifferences();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void A_claim_with_no_remittance_is_flagged_as_unadjudicated_not_as_denied()
    {
        var pack = NewPack("unadjudicated");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Claim("GPP-2026-000002", 250.00m)
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 100.00m, "CO", "45", 0m,
                    status: "1"));

            var outcome = IngestPipeline.Run(pack.Build());

            Assert.Equal(2, outcome.State.Claims.Count);
            Assert.Equal(1, outcome.Reconciliation.AdjudicatedClaims);
            Assert.Equal(1, outcome.Reconciliation.UnadjudicatedClaims);
            Assert.Equal(250.00m, outcome.Reconciliation.UnadjudicatedCharge);

            // Never-adjudicated is its own bucket: it is not a denial and must not be counted
            // as recoverable work.
            Assert.Equal(0, outcome.Reconciliation.DeniedClaims);

            var unadjudicated = Assert.Single(outcome.Exceptions,
                e => e.ReasonCode == ExceptionReason.UnadjudicatedClaim);
            Assert.Equal("GPP-2026-000002", unadjudicated.ClaimId);

            outcome.AssertRowConservation();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void A_carC_missing_from_the_reference_is_flagged_but_still_counted()
    {
        var pack = NewPack("unmapped");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                // CARC 999 does not exist in carc_rarc_reference.csv.
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "999", 100.00m));

            var outcome = IngestPipeline.Run(pack.Build());

            var unmapped = Assert.Single(outcome.Exceptions,
                e => e.ReasonCode == ExceptionReason.UnmappedCarcRarc);
            Assert.Contains("CARC 999", unmapped.Detail);

            // Flagged, not dropped: the money is still on the books and the file still ties out.
            Assert.Equal(1, outcome.Reconciliation.AdjudicatedClaims);
            outcome.Reconciliation.AssertZeroDifferences();
            outcome.AssertRowConservation();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void A_malformed_segment_surfaces_as_an_exception_instead_of_crashing_the_run()
    {
        var pack = NewPack("malformed");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m)
                    .Replace("BPR*I*0.00", "BPR*I*not-a-number"));

            var outcome = IngestPipeline.Run(pack.Build());

            var file = Assert.Single(outcome.State.Dispositions,
                d => d.Entity == IngestPipeline.EntityRemitFile);
            Assert.True(file.IsProblem);
            Assert.Equal(ExceptionReason.MalformedSegment, file.ReasonCode);
            Assert.Contains("BPR02", file.Detail);

            outcome.AssertRowConservation();
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void Missing_inputs_fail_fast_and_list_every_problem_at_once()
    {
        var pack = NewPack("manifest");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Remit("a.835", TestPack.Simple835("GPP-2026-000001", 100.00m, 0m, "CO", "45", 100.00m));
            var input = pack.Build();
            Assert.NotNull(input);

            File.Delete(Path.Combine(pack.Root, "claims_export.csv"));
            File.Delete(Path.Combine(pack.Root, "payer_rules.csv"));
            Directory.Delete(Path.Combine(pack.Root, "remits"), recursive: true);

            var ex = Assert.Throws<FileNotFoundException>(() => DataPack.Read(pack.Root));

            // Every problem in one message — not one per run.
            Assert.Contains("missing file: claims_export.csv", ex.Message);
            Assert.Contains("missing file: payer_rules.csv", ex.Message);
            Assert.Contains("missing directory: remits/", ex.Message);
            Assert.Contains("Unzip the full pack", ex.Message);
        }
        finally { pack.Dispose(); }
    }

    [Fact]
    public void A_data_dir_that_does_not_exist_says_what_to_do()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(
            () => DataPack.Read(Path.Combine(Path.GetTempPath(), "definitely_not_here_" + Guid.NewGuid().ToString("N"))));

        Assert.Contains("Unzip the data pack", ex.Message);
        Assert.Contains("DATA_DIR", ex.Message);
    }

    [Fact]
    public void Claim_references_that_look_like_injection_attempts_are_rejected_not_executed()
    {
        // The normaliser is the only place a raw reference becomes an identifier; anything it
        // does not recognise stays a string and reaches the database as a parameter, never as
        // part of a statement.
        foreach (var hostile in new[]
                 {
                     "'; DROP TABLE Claims;--",
                     "\" OR 1=1 --",
                     "<script>alert(1)</script>",
                     "../../etc/passwd",
                     "GPP-2026-000001; DELETE FROM Claims",
                     "%",
                     "",
                     "   ",
                 })
        {
            var result = ClaimIdNormaliser.Normalise(hostile);
            Assert.False(result.IsMatched, $"'{hostile}' was accepted as a claim id");
            Assert.Null(result.CanonicalId);
        }
    }

    [Fact]
    public void A_hostile_reference_reaches_the_exceptions_view_verbatim_and_harmless()
    {
        var pack = NewPack("hostile");
        try
        {
            pack.Claim("GPP-2026-000001", 100.00m)
                .Remit("a.835", TestPack.Simple835("'; DROP TABLE Claims;--", 100.00m, 0m, "CO", "45", 100.00m));

            var outcome = IngestPipeline.Run(pack.Build());

            var exception = Assert.Single(outcome.Exceptions,
                e => e.ReasonCode == ExceptionReason.ForeignClaimNamespace);
            // Truncated for the view, and carried as data rather than as an identifier.
            Assert.Contains("DROP TABLE", exception.Detail);
            Assert.Null(exception.ClaimId);

            Assert.Single(outcome.State.Claims);
            outcome.AssertRowConservation();
        }
        finally { pack.Dispose(); }
    }
}
