using System.Net;
using System.Text;
using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Llm;
using AQ.Denials.Rules.Analysis;
using AQ.Denials.Rules.Policy;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>A model that answers exactly as told, so the tests exercise this system rather than a model.</summary>
internal sealed class StubLlmClient : ILlmClient
{
    /// <summary>Returns the reply text for a request; null means use the default no-citation reply.</summary>
    public Func<LlmRequest, string>? OnRequest { get; set; }
    public Exception? Throw { get; set; }
    public LlmRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public string? ConfiguredProvider { get; init; } = "stub";
    public bool IsConfigured { get; init; } = true;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Calls++;
        LastRequest = request;
        if (Throw is not null) throw Throw;

        var text = OnRequest?.Invoke(request)
            ?? "CITATION_FILE: NONE\nCITATION_SECTION: NONE\nCITATION_QUOTE: NONE\nDRAFT:\n(none)";

        return Task.FromResult(new LlmResponse(
            Text: text, Model: "stub", InputTokens: 1, OutputTokens: 1, Provider: "stub"));
    }
}

/// <summary>Canned HTTP responses, so error classification is tested without a network.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return Responder(request);
    }
}

/// <summary>
/// The AI half of spec §C: grounding, the output contract, re-validation, and degradation.
/// </summary>
public class AppealDraftingTests
{
    private const string Northstar = "NS401";

    private static readonly PolicyLibrary Library =
        ReferenceDataReader.BuildPolicyLibrary(Path.Combine(TestData.DataDir, "payer_policies"));

    private static DraftInput FrequencyDenial() => new(
        ClaimId: "GPP-2026-000234",
        PayerName: "Northstar Health Plan",
        PayerId: Northstar,
        Category: "Coding - frequency",
        Carcs: ["151"],
        DeniedAmount: 145.00m,
        NextAction: "Confirm whether the second same-day service followed a change of condition.",
        DaysRemaining: 92);

    /* ---- grounding: what the model is and is not shown ----------------------------- */

    [Fact]
    public void The_prompt_carries_no_patient_identifiers()
    {
        // Derived from the file at run time rather than typed here: a name in test source would
        // be a name in the repository, which is the exact thing being tested against.
        var table = Csv.Parse(TestData.ReadText("claims_export.csv"));
        var first = table.AsDictionaries().First();
        var patientBits = new[]
        {
            first["patient_first"], first["patient_last"], first["patient_dob"], first["member_id"],
        }.Where(s => !string.IsNullOrWhiteSpace(s));

        var request = AppealDrafting.BuildRequest(
            FrequencyDenial(), Library.CitableSections(Northstar, ["151"]));

        var prompt = request.SystemPrompt + "\n" + string.Join("\n", request.Messages.Select(m => m.Content));

        foreach (var bit in patientBits)
            Assert.DoesNotContain(bit, prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_offers_only_clauses_this_payer_may_be_cited_from()
    {
        var request = AppealDrafting.BuildRequest(
            FrequencyDenial(), Library.CitableSections(Northstar, ["151"]));

        var user = request.Messages.Single(m => m.Role == "user").Content;

        Assert.Contains("NSHP_HOSP-FREQ-07.md", user, StringComparison.Ordinal);
        // Coastal's enrollment policy is the only document mentioning CARC B7, and it is not
        // Northstar's — so it must not even be *offered*, let alone citable.
        Assert.DoesNotContain("CSA_PROVIDER-ENROLLMENT.md", user, StringComparison.Ordinal);
        Assert.DoesNotContain("MPPO_DX-EXCL-03.md", user, StringComparison.Ordinal);
        Assert.DoesNotContain("SMP_SNF-AUTH-2026.md", user, StringComparison.Ordinal);
    }

    [Fact]
    public void A_denial_no_policy_covers_is_offered_no_clauses_at_all()
    {
        var request = AppealDrafting.BuildRequest(
            FrequencyDenial() with { Category = "Billing - timely filing", Carcs = ["29"] },
            Library.CitableSections(Northstar, ["29"]));

        var user = request.Messages.Single(m => m.Role == "user").Content;

        Assert.Contains("ALLOWED CLAUSES: none", user, StringComparison.Ordinal);
        Assert.DoesNotContain("FILE:", user, StringComparison.Ordinal);
        Assert.Contains("CITATION_FILE must be NONE", user, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_is_byte_identical_for_identical_input()
    {
        // Pure construction is what makes the assertions above meaningful rather than incidental.
        var a = AppealDrafting.BuildRequest(FrequencyDenial(), Library.CitableSections(Northstar, ["151"]));
        var b = AppealDrafting.BuildRequest(FrequencyDenial(), Library.CitableSections(Northstar, ["151"]));

        Assert.Equal(a.SystemPrompt, b.SystemPrompt);
        Assert.Equal(
            a.Messages.Select(m => m.Content),
            b.Messages.Select(m => m.Content));
    }

    /* ---- reading the reply --------------------------------------------------------- */

    [Fact]
    public void A_well_formed_reply_is_split_into_its_fields()
    {
        var parsed = AppealDrafting.Parse(
            "CITATION_FILE: NSHP_HOSP-FREQ-07.md\n"
          + "CITATION_SECTION: 2\n"
          + "CITATION_QUOTE: the second claim received will be denied (CARC 151)\n"
          + "DRAFT:\n"
          + "We request reconsideration.");

        Assert.Equal("NSHP_HOSP-FREQ-07.md", parsed.Citation!.FileName);
        Assert.Equal("2", parsed.Citation!.SectionId);
        Assert.Equal("the second claim received will be denied (CARC 151)", parsed.Citation!.Quote);
        Assert.Equal("We request reconsideration.", parsed.Body);
    }

    [Fact]
    public void Code_fences_and_lowercase_keys_are_formatting_noise_not_a_different_contract()
    {
        var parsed = AppealDrafting.Parse(
            "```text\n"
          + "citation_file: NSHP_HOSP-FREQ-07.md\n"
          + "citation_section: 2\n"
          + "citation_quote: NONE\n"
          + "draft:\n"
          + "Please reconsider.\n"
          + "```");

        Assert.Equal("NSHP_HOSP-FREQ-07.md", parsed.Citation!.FileName);
        Assert.Equal("Please reconsider.", parsed.Body);
        Assert.Null(parsed.Citation!.Quote);
    }

    [Fact]
    public void A_reply_missing_a_field_is_refused_not_approximated()
    {
        // No CITATION_QUOTE means this system cannot tell whether the quote is real, so it cannot
        // tell whether the citation is real. Taking the draft anyway would be shipping unchecked
        // text as though it had been checked.
        var ex = Assert.Throws<FormatException>(() => AppealDrafting.Parse(
            "CITATION_FILE: NSHP_HOSP-FREQ-07.md\n"
          + "CITATION_SECTION: 2\n"
          + "DRAFT:\n"
          + "Please reconsider."));

        Assert.Contains("CITATION_QUOTE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_a_section_is_not_a_citation_to_a_policy_section()
    {
        var ex = Assert.Throws<FormatException>(() => AppealDrafting.Parse(
            "CITATION_FILE: NSHP_HOSP-FREQ-07.md\n"
          + "CITATION_SECTION: NONE\n"
          + "CITATION_QUOTE: NONE\n"
          + "DRAFT:\n"
          + "Please reconsider."));

        Assert.Contains("not a citation to a policy section", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_markers_with_no_draft_text_are_refused()
    {
        Assert.Throws<FormatException>(() => AppealDrafting.Parse(
            "CITATION_FILE: NONE\nCITATION_SECTION: NONE\nCITATION_QUOTE: NONE\nDRAFT:"));
    }

    /* ---- the citation is re-checked, and a bad one takes the draft with it ---------- */

    [Fact]
    public async Task A_citation_that_validates_is_shipped()
    {
        var stub = new StubLlmClient
        {
            OnRequest = _ =>
                "CITATION_FILE: NSHP_HOSP-FREQ-07.md\n"
              + "CITATION_SECTION: 2\n"
              + "CITATION_QUOTE: the second claim received will be denied (CARC 151)\n"
              + "DRAFT:\n"
              + "Policy HOSP-FREQ-07 section 2 governs this denial; see the clause.",
        };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.True(outcome.Produced);
        Assert.Equal(CitationVerdict.Valid, outcome.Verdict);
        Assert.NotNull(outcome.ClaimedCitation);
        Assert.Null(outcome.UnavailableReason);
    }

    [Fact]
    public async Task A_citation_to_another_payers_policy_is_dropped_and_the_note_with_it()
    {
        // The model was only ever offered Northstar's clause, and reached for Coastal's anyway.
        // The check does not ask it whether that was right; it reads the ownership map.
        var stub = new StubLlmClient
        {
            OnRequest = _ =>
                "CITATION_FILE: CSA_PROVIDER-ENROLLMENT.md\n"
              + "CITATION_SECTION: 2\n"
              + "CITATION_QUOTE: Services by practitioners not yet enrolled deny with CARC B7\n"
              + "DRAFT:\n"
              + "This denial should be reversed under Coastal's enrollment policy.",
        };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Null(outcome.Body);
        Assert.Equal(CitationVerdict.WrongPayer, outcome.Verdict);
        Assert.NotNull(outcome.ClaimedCitation);          // kept, so the failure is reportable
        Assert.Contains("Discarded", outcome.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fabricated_quotation_takes_the_draft_with_it()
    {
        var stub = new StubLlmClient
        {
            OnRequest = _ =>
                "CITATION_FILE: NSHP_HOSP-FREQ-07.md\n"
              + "CITATION_SECTION: 2\n"
              + "CITATION_QUOTE: Northstar will overturn any duplicate frequency denial on request\n"
              + "DRAFT:\n"
              + "Northstar will overturn this denial.",
        };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Equal(CitationVerdict.QuoteNotInSection, outcome.Verdict);
    }

    [Fact]
    public async Task An_honest_no_policy_answer_ships_as_no_citation()
    {
        var stub = new StubLlmClient
        {
            OnRequest = _ =>
                "CITATION_FILE: NONE\nCITATION_SECTION: NONE\nCITATION_QUOTE: NONE\n"
              + "DRAFT:\nNo policy basis found for this denial.",
        };

        var outcome = await AppealDrafting.DraftAsync(
            stub, Library, FrequencyDenial() with { Carcs = ["29"] });

        Assert.True(outcome.Produced);
        Assert.Equal(CitationVerdict.NoCitation, outcome.Verdict);
        Assert.Contains("No policy basis", outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unparseable_reply_is_discarded_rather_than_shipped_unverified()
    {
        var stub = new StubLlmClient { OnRequest = _ => "Sure! Here's a strong appeal letter: …" };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Equal(CitationVerdict.NotAssessed, outcome.Verdict);
        Assert.Contains("cannot be located", outcome.UnavailableReason, StringComparison.Ordinal);
    }

    /* ---- degradation: the brief requires the system to keep working ---------------- */

    [Fact]
    public async Task No_provider_means_no_draft_but_a_clear_reason_and_a_live_analysis()
    {
        var outcome = await AppealDrafting.DraftAsync(
            new NullLlmClient(), Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Equal(CitationVerdict.NotAssessed, outcome.Verdict);
        Assert.Contains("No LLM provider is configured", outcome.UnavailableReason, StringComparison.Ordinal);
        // The reason must say what still works, not merely what does not.
        Assert.Contains("category, team, preventability", outcome.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_model_is_reported_as_an_outage_not_a_broken_system()
    {
        var stub = new StubLlmClient
        {
            Throw = new LlmException("endpoint unreachable") { Transient = true },
        };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Contains("AI service is unavailable", outcome.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("The analysis stands", outcome.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(CitationVerdict.NotAssessed, outcome.Verdict);
    }

    [Fact]
    public async Task An_unconfigured_client_is_detected_before_any_call_is_attempted()
    {
        var stub = new StubLlmClient { IsConfigured = false };

        var outcome = await AppealDrafting.DraftAsync(stub, Library, FrequencyDenial());

        Assert.False(outcome.Produced);
        Assert.Equal(0, stub.Calls);   // no wasted round trip, no exception to swallow
        Assert.Contains("LLM_PROVIDER is empty", outcome.UnavailableReason, StringComparison.Ordinal);
    }
}

/// <summary>
/// Confidence, which is computed from facts the system already holds — never asked of the model.
/// </summary>
public class ConfidenceTests
{
    [Fact]
    public void A_well_supported_denial_scores_high_and_needs_no_review()
    {
        var score = Confidence.Evaluate(
            coveredByLabeledSample: true,
            citation: CitationVerdict.Valid,
            daysUntilDeadline: 92,
            draftProduced: true,
            preventableEstablished: true);

        Assert.Equal(100, score.Score);            // 50 + 25 + 15 + 10
        Assert.False(score.RequiresHumanReview);
    }

    [Fact]
    public void An_extrapolated_denial_with_no_draft_and_a_closed_window_ends_in_review()
    {
        var score = Confidence.Evaluate(
            coveredByLabeledSample: false,
            citation: CitationVerdict.NotAssessed,
            daysUntilDeadline: -5,
            draftProduced: false,
            preventableEstablished: false);

        // 50 − 20 (window closed) − 15 (no draft) − 10 (preventability unestablished) = 5.
        Assert.Equal(5, score.Score);
        Assert.True(score.RequiresHumanReview);
        Assert.Contains("NOT in the expert sample", string.Join(" ", score.Factors),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_citation_costs_more_than_a_legitimate_absence_of_one()
    {
        var failed = Confidence.Evaluate(true, CitationVerdict.WrongPayer, 92, true, true);
        var none = Confidence.Evaluate(true, CitationVerdict.NoCitation, 92, true, true);

        Assert.True(failed.Score < none.Score,
            $"a citation that failed validation ({failed.Score}) must not score as well as "
          + $"one that genuinely does not exist ({none.Score})");
    }

    [Fact]
    public void Not_assessed_is_not_punished_as_a_disproved_citation()
    {
        // Nothing was claimed, so nothing was disproved — the no-draft penalty is what carries it.
        var notAssessed = Confidence.Evaluate(true, CitationVerdict.NotAssessed, 92, true, true);
        var failed = Confidence.Evaluate(true, CitationVerdict.WrongPayer, 92, true, true);

        Assert.True(notAssessed.Score > failed.Score);
    }

    [Fact]
    public void Every_factor_is_explained_rather_than_only_totalling()
    {
        var score = Confidence.Evaluate(true, CitationVerdict.Valid, 92, true, true);

        Assert.True(score.Factors.Count >= 5, "each contributing factor should be named");
        Assert.Contains(score.Factors, f => f.Contains("+25", StringComparison.Ordinal));
        Assert.Contains(score.Factors, f => f.Contains("+15", StringComparison.Ordinal));
        Assert.Contains(score.Factors, f => f.Contains("threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_score_stays_inside_its_scale()
    {
        Assert.Equal(Confidence.Ceiling,
            Confidence.Evaluate(true, CitationVerdict.Valid, 92, true, true).Score);
        Assert.Equal(Confidence.Floor,
            Confidence.Evaluate(false, CitationVerdict.WrongPayer, -1, false, false).Score);
    }

    [Fact]
    public void The_review_threshold_is_where_it_says_it_is()
    {
        // 70 exactly: baseline 50, +25 covered, +5 genuine no-policy answer, no window bonus,
        // draft produced, −10 because nobody has said whether this was preventable.
        var atThreshold = Confidence.Evaluate(true, CitationVerdict.NoCitation, null, true, false);
        Assert.Equal(70, atThreshold.Score);
        Assert.False(atThreshold.RequiresHumanReview);

        // One factor different — no draft instead of unestablished preventability — and the same
        // denial falls below the line. 65, not a score invented for the case.
        var justBelow = Confidence.Evaluate(true, CitationVerdict.NoCitation, null, false, true);
        Assert.Equal(65, justBelow.Score);
        Assert.True(justBelow.RequiresHumanReview);
    }

    [Fact]
    public void The_flag_agrees_with_the_score_and_the_scale_holds_for_every_input_combination()
    {
        // Not four hand-picked cases: every combination of the five inputs, so the threshold
        // cannot be correct only where someone happened to check it.
        foreach (var covered in new[] { true, false })
        foreach (var citation in Enum.GetValues<CitationVerdict>())
        foreach (var days in new int?[] { null, 92, 30, 0, -5 })
        foreach (var draft in new[] { true, false })
        foreach (var established in new[] { true, false })
        {
            var score = Confidence.Evaluate(covered, citation, days, draft, established);

            Assert.InRange(score.Score, Confidence.Floor, Confidence.Ceiling);
            Assert.Equal(
                score.Score < Confidence.Threshold,
                score.RequiresHumanReview);
            Assert.NotEmpty(score.Factors);
        }
    }
}
