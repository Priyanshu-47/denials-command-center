using AQ.Denials.Ingest.Readers;
using AQ.Denials.Rules.Policy;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The citation rules from <b>Q3</b>, and the two gates a citation has to clear.
/// </summary>
/// <remarks>
/// Every expectation below is read off the actual policy files, not written from a description of
/// them — the point of this class is that the gate cannot be satisfied by a plausible-sounding
/// citation, so an expectation that was itself plausible would prove nothing.
/// </remarks>
public class PolicyCitationTests
{
    private const string Northstar = "NS401";
    private const string Coastal = "CSA77";
    private const string Sunshine = "SMP12";
    private const string Meridian = "MRD55";

    private static readonly PolicyLibrary Library =
        ReferenceDataReader.BuildPolicyLibrary(Path.Combine(TestData.DataDir, "payer_policies"));

    /* ---- the relevance gate: which documents actually name which codes ------------- */

    [Fact]
    public void Each_policy_is_found_by_the_codes_it_names_and_no_others()
    {
        // The gate matches "CARC 45", never a bare "45" — inside these files a naive substring
        // test would happily match the digits of "99231-99233" or "within 60 days".
        Assert.Equal(
            new[] { "ALL_PAYERS_MOD25-2026.md" },
            Library.CitableFiles(Northstar, ["97"]));

        Assert.Equal(
            new[] { "NSHP_HOSP-FREQ-07.md" },
            Library.CitableFiles(Northstar, ["151"]));

        Assert.Equal(
            new[] { "CSA_PROVIDER-ENROLLMENT.md" },
            Library.CitableFiles(Coastal, ["B7"]));

        Assert.Equal(
            new[] { "MPPO_DX-EXCL-03.md" },
            Library.CitableFiles(Meridian, ["11"]));

        Assert.Equal(
            new[] { "SMP_SNF-AUTH-2026.md" },
            Library.CitableFiles(Sunshine, ["197"]));
    }

    [Fact]
    public void The_cited_clause_is_the_one_that_names_the_code()
    {
        var sections = Library.CitableSections(Northstar, ["151"]);

        var single = Assert.Single(sections);
        Assert.Equal("NSHP_HOSP-FREQ-07.md", single.FileName);
        Assert.Equal("2", single.Section.Id);
        Assert.Contains("CARC 151", single.Section.Text, StringComparison.Ordinal);

        // Meridian's Excludes1 edit names CARC 11 in clause 2, not the opening definition.
        var meridian = Assert.Single(Library.CitableSections(Meridian, ["11"]));
        Assert.Equal("2", meridian.Section.Id);
        Assert.Contains("CARC 11", meridian.Section.Text, StringComparison.Ordinal);
    }

    /* ---- gate 1: whose document is it? -------------------------------------------- */

    [Fact]
    public void A_denial_may_only_be_argued_from_the_denying_payers_own_policy()
    {
        // Coastal's enrollment policy is the only document naming CARC B7. Northstar denying for
        // credentialing therefore has NO policy basis it may cite — it cannot borrow Coastal's
        // document, and ALL_PAYERS_MOD25-2026.md says nothing about credentialing either.
        Assert.Empty(Library.CitableFiles(Northstar, ["B7"]));

        // Which makes a Northstar draft that reaches for it a Q3 breach, not a near miss.
        Assert.Equal(
            CitationVerdict.WrongPayer,
            Library.Validate(
                new PolicyCitation("CSA_PROVIDER-ENROLLMENT.md", "2", null),
                Northstar, ["B7"]));
    }

    [Fact]
    public void The_all_payers_document_may_be_cited_by_anyone_but_is_nobody_s_own()
    {
        // It is permitted for every payer — it says so in its own preamble.
        Assert.Equal(
            new[] { "ALL_PAYERS_MOD25-2026.md" },
            Library.CitableFiles(Northstar, ["97"]));
        Assert.Equal(
            new[] { "ALL_PAYERS_MOD25-2026.md" },
            Library.CitableFiles(Sunshine, ["97"]));

        // And it is never returned as any payer's *own* document: the ownership map holds exactly
        // the four payer-specific files and nothing else.
        Assert.Equal(
            new[] { "CSA_PROVIDER-ENROLLMENT.md", "MPPO_DX-EXCL-03.md",
                    "NSHP_HOSP-FREQ-07.md", "SMP_SNF-AUTH-2026.md" },
            ReferenceDataReader.PolicyFileForPayer("unused")
                .Values.OrderBy(v => v, StringComparer.Ordinal).ToArray());
        Assert.Null(Library.OwnFileFor("ANY1"));
    }

    /* ---- gate 2: does it talk about this denial? ----------------------------------- */

    [Fact]
    public void A_right_payer_with_the_wrong_topic_has_no_policy_basis()
    {
        // ALL_PAYERS_MOD25-2026.md is Northstar-permitted, but it never mentions CARC 151 — so
        // citing it for a frequency denial claims a connection the document does not make.
        Assert.Equal(
            CitationVerdict.NotRelevant,
            Library.Validate(
                new PolicyCitation("ALL_PAYERS_MOD25-2026.md", "3", null),
                Northstar, ["151"]));
    }

    [Fact]
    public void A_denial_no_policy_talks_about_returns_no_citation_rather_than_a_neighbour()
    {
        // Timely filing (CARC 29) appears in no policy file. Windows live in payer_rules.csv,
        // which is a date table, not a policy — so the honest answer is "no citation", and a
        // draft must say so instead of quoting a frequency policy at an unpaid claim.
        Assert.Empty(Library.CitableFiles(Northstar, ["29"]));
        Assert.Equal(
            CitationVerdict.NoCitation,
            Library.Validate(null, Northstar, ["29"]));
    }

    [Fact]
    public void Nothing_matchable_means_nothing_citable_for_any_payer()
    {
        // An empty code set must not resolve to "everything is citable" — that would make an
        // unmapped denial the most citeable one in the system.
        Assert.Empty(Library.CitableFiles(Northstar, []));
        Assert.Empty(Library.CitableSections(Meridian, [""]));
    }

    /* ---- the model's output is re-checked, not trusted ---------------------------- */

    [Fact]
    public void A_valid_citation_survives_revalidation()
    {
        var sections = Library.CitableSections(Northstar, ["151"]);
        var section = sections.Single().Section;

        Assert.Equal(
            CitationVerdict.Valid,
            Library.Validate(
                new PolicyCitation("NSHP_HOSP-FREQ-07.md", section.Id, section.Text),
                Northstar, ["151"]));
    }

    [Fact]
    public void A_draft_that_misquotes_a_clause_is_rejected()
    {
        // The clause exists and belongs to the right payer; only the quotation is invented. This
        // is the check that makes a confidently-worded fabrication indistinguishable from a
        // correct citation — except that it is checked against the file.
        Assert.Equal(
            CitationVerdict.QuoteNotInSection,
            Library.Validate(
                new PolicyCitation(
                    "NSHP_HOSP-FREQ-07.md", "2",
                    "Northstar will overturn any duplicate frequency denial on request."),
                Northstar, ["151"]));

        // The same claim with the real words passes, so the check is not simply "reject quotes".
        Assert.Equal(
            CitationVerdict.Valid,
            Library.Validate(
                new PolicyCitation(
                    "NSHP_HOSP-FREQ-07.md", "2",
                    "the second claim received will be denied (CARC 151)"),
                Northstar, ["151"]));
    }

    [Fact]
    public void A_quotation_is_matched_ignoring_layout_not_altering_words()
    {
        // Real documents wrap and reflow; a citation that is right must not fail because the
        // file used two spaces after a full stop.
        Assert.Equal(
            CitationVerdict.Valid,
            Library.Validate(
                new PolicyCitation(
                    "NSHP_HOSP-FREQ-07.md", "2",
                    "the second claim received   will be denied"),
                Northstar, ["151"]));
    }

    [Fact]
    public void A_clause_number_the_document_does_not_contain_is_rejected()
    {
        Assert.Equal(
            CitationVerdict.SectionNotFound,
            Library.Validate(
                new PolicyCitation("NSHP_HOSP-FREQ-07.md", "99", null),
                Northstar, ["151"]));

        // Including a clause number spelled well but not written that way.
        Assert.Equal(
            CitationVerdict.SectionNotFound,
            Library.Validate(
                new PolicyCitation("NSHP_HOSP-FREQ-07.md", "02", null),
                Northstar, ["151"]));
    }

    [Fact]
    public void A_policy_file_that_never_existed_is_rejected()
    {
        Assert.Equal(
            CitationVerdict.UnknownFile,
            Library.Validate(
                new PolicyCitation("NSHP_HOSP-FREQ-07_v2.md", "2", null),
                Northstar, ["151"]));
    }

    [Fact]
    public void The_wrong_payer_is_reported_before_any_lesser_problem()
    {
        // Ordering is the contract: a Q3 breach must never be softened into "section not found"
        // because the model also got the clause number wrong.
        Assert.Equal(
            CitationVerdict.WrongPayer,
            Library.Validate(
                new PolicyCitation("CSA_PROVIDER-ENROLLMENT.md", "99", "not a quote at all"),
                Northstar, ["B7"]));
    }

    [Fact]
    public void Every_document_in_the_pack_parses_into_addressable_clauses()
    {
        Assert.Equal(5, Library.FileNames.Count);

        foreach (var name in Library.FileNames)
        {
            var document = Library.DocumentFor(name);
            Assert.NotNull(document);
            Assert.NotEmpty(document!.Sections);
            Assert.NotEmpty(document.Title);
        }
    }
}
