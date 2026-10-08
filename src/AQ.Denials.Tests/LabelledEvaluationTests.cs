using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Rules;
using AQ.Denials.Rules.Analysis;
using AQ.Denials.Rules.Policy;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// Evaluation against <c>labeled_denials_sample.csv</c> — the section-C evidence, with the
/// framing stated in the test names rather than left to the report.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is, and is not.</b> Every figure below is <b>fit to the labelled sample</b>,
/// not accuracy. Two reasons, and they are different:
/// </para>
/// <list type="number">
/// <item><description><b>The team and preventability answers are in-sample by construction.</b> They are read from
/// these same 40 rows (D25), so agreeing with them is a property of how the table was built, not
/// a measurement. Reporting 40/40 as accuracy would be circular — which the brief's C1 forbids
/// outright, and it would also be the single easiest number in this project to overstate.</description></item>
/// <item><description><b>The category itself comes from CARC codes, never from the labels.</b> So the fit is real
/// evidence that the code rules reproduce an expert's reading — it is just evidence on the same
/// 40 rows the rules were written against, which is what "in-sample" means. Genuine out-of-sample
/// evidence is carried by the held-out probe in <see cref="Taxonomy_coverage"/>, and by the
/// adversarial drafting cases elsewhere in this suite.</description></item>
/// </list>
/// <para>
/// The sample also turns out not to be one population, which is reported rather than quietly
/// filtered: four of its rows are not open denials at all.
/// </para>
/// </remarks>
public class LabelledEvaluationTests
{
    private static readonly Lazy<IngestOutcome> Cached =
        new(() => IngestPipeline.Run(DataPack.Read(TestData.DataDir)));

    private static IReadOnlyList<string> Carcs(RemitObservation observation) =>
        observation.Adjustments
                   .Select(a => a.Carc)
                   .Where(c => c.Length > 0)
                   .Distinct(StringComparer.Ordinal)
                   .ToList();

    private static IReadOnlyDictionary<string, Claim> AllClaims() =>
        Cached.Value.State.Claims.ToDictionary(c => c.ClaimId, StringComparer.Ordinal);

    private static HashSet<string> OpenDenialIds() =>
        Cached.Value.State.Claims
               .Where(c => c.Observations.Count > 0 && c.CurrentStatus == "4")
               .Select(c => c.ClaimId)
               .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<LabeledDenial> Labels() =>
        ReferenceDataReader.ReadLabeledSample(TestData.ReadText("labeled_denials_sample.csv"));

    [Fact]
    public void The_sample_fits_the_category_rules_on_every_row()
    {
        var labels = Labels();
        var claims = AllClaims();

        var correct = 0;
        var failures = new List<string>();

        foreach (var label in labels)
        {
            if (!claims.TryGetValue(label.ClaimId, out var claim))
            {
                failures.Add($"{label.ClaimId}: absent from the ingest state");
                continue;
            }

            var predicted = DenialCategory.Of(
                Carcs(claim.Observations[^1]), claim.PayerId, claim.Dos);

            if (predicted == label.RootCauseCategory) correct++;
            else
                failures.Add(
                    $"{label.ClaimId}: predicted '{predicted}' but labelled "
                  + $"'{label.RootCauseCategory}'");
        }

        // 40/40, stated as fit — see the class remarks for why this is never accuracy.
        Assert.Equal(40, labels.Count);
        Assert.Equal(40, correct);
        Assert.Empty(failures);
    }

    [Fact]
    public void Four_of_the_forty_labelled_rows_are_not_open_denials()
    {
        // The sample mixes two populations, and dropping the four silently would leave a reader
        // believing all 40 sit inside the 136. They do not: these claims were PAID (status 1),
        // each carrying a CARC-97 zero-paid line alongside CARC 45 — Q1's "zero-paid lines on
        // paid claims", a bucket reported separately from the headline 136 / $27,780.
        var labels = Labels();
        var claims = AllClaims();
        var open = OpenDenialIds();

        var notOpen = labels.Where(l => !open.Contains(l.ClaimId)).ToList();

        Assert.Equal(4, notOpen.Count);
        Assert.Equal(36, labels.Count - notOpen.Count);

        foreach (var label in notOpen)
        {
            var claim = claims[label.ClaimId];
            var observation = claim.Observations[^1];

            Assert.Equal("1", claim.CurrentStatus);
            Assert.Equal(new[] { "45", "97" }, Carcs(observation).OrderBy(c => c, StringComparer.Ordinal));
            // Partial payment, not a denial: money moved on these claims.
            Assert.True(observation.PaidAmount > 0,
                $"{label.ClaimId} is paid {observation.PaidAmount}, so it is not an unpaid denial");
        }

        // And they are exactly the rows the sample labels 'Coding - modifier' — the whole of that
        // category's representation in the sample is outside the open-denial population.
        Assert.All(notOpen, l => Assert.Equal(DenialCategory.CodingModifier, l.RootCauseCategory));
        Assert.Equal(
            4,
            labels.Count(l => l.RootCauseCategory == DenialCategory.CodingModifier));

        // The other 36 really are inside the headline figure, not merely "status 4 somewhere".
        Assert.Equal(36, labels.Count(l => open.Contains(l.ClaimId)));
        Assert.Equal(136, open.Count);
    }

    [Fact]
    public void The_categories_open_in_production_are_the_rules_real_workload()
    {
        // The 136 open denials are where this taxonomy actually has to perform; the labels only
        // tell us it agreed with an expert on 40 rows. These counts sum to 136 exactly.
        var counts = OpenDenialIds()
            .Select(id => Cached.Value.State.Claims.First(c => c.ClaimId == id))
            .GroupBy(c => DenialCategory.Of(Carcs(c.Observations[^1]), c.PayerId, c.Dos),
                    StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Assert.Equal(136, counts.Values.Sum());

        Assert.Equal(26, counts[DenialCategory.Credentialing]);
        Assert.Equal(24, counts[DenialCategory.CodingDiagnosis]);
        Assert.Equal(19, counts[DenialCategory.Authorization]);
        Assert.Equal(17, counts[DenialCategory.Eligibility]);
        Assert.Equal(17, counts[DenialCategory.MedicalNecessity]);
        Assert.Equal(11, counts[DenialCategory.CodingFrequency]);
        Assert.Equal(9, counts[DenialCategory.TimelyFiling]);
        Assert.Equal(7, counts[DenialCategory.PayerError]);
        Assert.Equal(6, counts[DenialCategory.DuplicateUnvalidated]);

        // Two absences worth stating rather than leaving to be noticed:
        //
        // * 'Coding - modifier' occurs zero times — every CARC-97 row in this pack sits on a PAID
        //   claim, so a category the sample is entirely made of never occurs where the work is.
        // * 'UNMAPPED' occurs zero times, so no open denial currently escapes the taxonomy.
        Assert.False(counts.ContainsKey(DenialCategory.CodingModifier));
        Assert.False(counts.ContainsKey(DenialCategory.Fallback));
    }

    [Fact]
    public void An_unmapped_carC_never_masks_a_mapped_one()
    {
        // CARC 45 ('charge exceeds fee schedule') has no branch in the taxonomy. If an unmapped
        // code could drag a denial into UNMAPPED, the most common fee-schedule denial would erase
        // every other signal on the claim. It must not: the mapped code still decides.
        Assert.Equal(
            DenialCategory.CodingDiagnosis,
            DenialCategory.Of(["45", "11"], "NS401", "2026-05-01"));

        Assert.Equal(
            DenialCategory.DuplicateUnvalidated,
            DenialCategory.Of(["45", "18"], "NS401", "2026-05-01"));

        // And a denial carrying only patient-responsibility adjustments has no category at all.
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of(["1", "2", "3"], "NS401", "2026-05-01"));
        Assert.Equal(DenialCategory.Fallback, DenialCategory.Of([], "NS401", "2026-05-01"));
    }

    [Fact]
    public void The_reference_file_s_carcs_either_map_to_a_category_or_fail_safe_to_one()
    {
        // Held out: these expectations come from carc_rarc_reference.csv's own definitions and
        // the priority order in DenialCategory.Of, not from the 40 label rows.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["11"] = DenialCategory.CodingDiagnosis,       // "diagnosis is inconsistent with the procedure"
            ["18"] = DenialCategory.DuplicateUnvalidated,  // "exact duplicate claim/service"
            ["27"] = DenialCategory.Eligibility,           // "expenses incurred after coverage terminated"
            ["29"] = DenialCategory.TimelyFiling,          // "time limit for filing has expired"
            ["50"] = DenialCategory.MedicalNecessity,      // "not deemed a medical necessity"
            ["97"] = DenialCategory.CodingModifier,        // "benefit included in payment for another service"
            ["151"] = DenialCategory.CodingFrequency,      // "does not support this many/frequency of services"
            ["197"] = DenialCategory.Authorization,        // "precertification/authorization absent"
            ["B7"] = DenialCategory.Credentialing,         // "not certified/eligible to be paid"
        };

        foreach (var (carc, category) in expected)
            Assert.Equal(category, DenialCategory.Of([carc], "NS401", "2026-05-01"));

        // The four that do not map, and why each is the right answer rather than a miss:
        foreach (var carc in new[] { "1", "2", "3", "45" })
            Assert.Equal(DenialCategory.Fallback, DenialCategory.Of([carc], "NS401", "2026-05-01"));

        // 1/2/3 are deductible, coinsurance and copay — money the patient owes on a PAID claim,
        // not a denial reason, so UNMAPPED is correct.
        //
        // 45 is different: "charge exceeds fee schedule" IS a denial reason and has no category.
        // That is a real gap, recorded in PROBLEM_MEMO. It fails safe rather than silently —
        // UNMAPPED routes to a human with preventability null and confidence capped — and it is
        // currently latent, because no open denial's category is determined by an unmapped code.
        var fallback = DenialCategory.OutcomeFor(DenialCategory.Fallback);
        Assert.Null(fallback.PreventableAtPrebill);
        Assert.False(fallback.CoveredByLabeledSample);

        // The safety property, computed exactly as the pipeline computes it: an unmapped CARC
        // lands in the review queue instead of quietly receiving someone's best guess.
        var score = Confidence.Evaluate(
            coveredByLabeledSample: fallback.CoveredByLabeledSample,
            citation: CitationVerdict.NotAssessed,
            daysUntilDeadline: null,
            draftProduced: false,
            preventableEstablished: fallback.PreventableAtPrebill.HasValue);

        Assert.True(score.RequiresHumanReview,
            $"an unmapped CARC must be reviewed, but scored {score.Score}");
    }

    [Fact]
    public void Priority_order_holds_for_combinations_the_sample_never_contains()
    {
        // Also held out: pairs whose resolution depends on the declared order, chosen so that a
        // reordering would change the answer.
        Assert.Equal(DenialCategory.TimelyFiling,
            DenialCategory.Of(["18", "29"], "NS401", "2026-05-01"));   // 29 outranks 18
        Assert.Equal(DenialCategory.Credentialing,
            DenialCategory.Of(["B7", "197"], "NS401", "2026-05-01"));  // B7 outranks 197
        Assert.Equal(DenialCategory.MedicalNecessity,
            DenialCategory.Of(["11", "50"], "NS401", "2026-05-01"));   // 50 outranks 11
        Assert.Equal(DenialCategory.CodingFrequency,
            DenialCategory.Of(["97", "151"], "NS401", "2026-05-01"));  // 151 outranks 97
        Assert.Equal(DenialCategory.Eligibility,
            DenialCategory.Of(["50", "27"], "NS401", "2026-05-01"));   // 27 outranks 50
    }
}
