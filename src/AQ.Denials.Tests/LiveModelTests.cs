using AQ.Denials.Ingest.Readers;
using AQ.Denials.Llm;
using AQ.Denials.Rules.Policy;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// Exercises a <b>real</b> model, if one is configured — the part a stub can never establish.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is a no-op unless <c>LLM_PROVIDER</c> is set.</b> Without it, each test returns
/// before doing anything and therefore passes; it is not evidence of anything. That is deliberate
/// — a suite that fails when no AI is installed would punish exactly the configuration the brief
/// requires the system to support — but it means a green run on its own proves nothing here. The
/// measured outcomes of the actual runs, with the model names and sizes, are written up in
/// <c>docs/AI_EVALUATION.md</c>.
/// </para>
/// <para>
/// Run it deliberately:
/// </para>
/// <code>
/// LLM_PROVIDER=ollama LLM_MODEL=qwen2.5-coder:3b dotnet test --filter LiveModelTests
/// </code>
/// <para>
/// These assertions are deliberately about <b>invariants, not quality</b>. Which replies were
/// good is a judgement recorded in the evaluation document; what must hold for every reply,
/// whatever the model said, is what belongs in a test.
/// </para>
/// </remarks>
public class LiveModelTests
{
    private static readonly PolicyLibrary Library =
        ReferenceDataReader.BuildPolicyLibrary(
            Path.Combine(TestData.DataDir, "payer_policies"));

    private static bool Configured
    {
        get
        {
            var provider = Environment.GetEnvironmentVariable("LLM_PROVIDER");
            return !string.IsNullOrWhiteSpace(provider);
        }
    }

    private static DraftInput Frequency() => new(
        ClaimId: "GPP-2026-000111",
        PayerName: "Northstar Health Plan",
        PayerId: "NS401",
        Category: Rules.DenialCategory.CodingFrequency,
        Carcs: ["151"],
        DeniedAmount: 145.00m,
        NextAction: "Confirm whether the second same-day service followed a change of condition.",
        DaysRemaining: 92);

    private static DraftInput TimelyFiling() => new(
        ClaimId: "GPP-2026-000222",
        PayerName: "Northstar Health Plan",
        PayerId: "NS401",
        Category: Rules.DenialCategory.TimelyFiling,
        Carcs: ["29"],
        DeniedAmount: 300.00m,
        NextAction: "Appeal inside the window or write off.",
        DaysRemaining: 12);

    [Fact]
    public async Task A_real_model_produces_a_grounded_draft_for_a_denial_with_a_policy()
    {
        if (!Configured) return;

        // Not `using`: ILlmClient is not IDisposable (NullLlmClient has nothing to release), and
        // the concrete client's HttpClient lives for the length of this test process anyway.
        var client = LlmClientFactory.Create();
        var outcome = await AppealDrafting.DraftAsync(client, Library, Frequency());

        Console.WriteLine($"[live] model={client.ConfiguredProvider} verdict={outcome.Verdict} "
                        + $"produced={outcome.Produced}");
        Console.WriteLine("[live] body=" + (outcome.Body ?? "<none>"));
        Console.WriteLine("[live] reason=" + (outcome.UnavailableReason ?? "<none>"));

        // Invariant: a note only ships with a verdict that means "checked".
        if (outcome.Body is not null)
            Assert.Contains(outcome.Verdict, new[]
            {
                CitationVerdict.Valid,
                CitationVerdict.NoCitation,
                CitationVerdict.ClauseAvailableUncited,
            });

        // Invariant: any citation that ships was re-validated against the real file — never
        // accepted because the model said so.
        if (outcome.Verdict == CitationVerdict.Valid)
            Assert.NotNull(outcome.ClaimedCitation);

        // Invariant: no patient identifier reaches the specialist's screen through the model.
        // Derived from the pack at run time so no real name is written into this repository.
        if (outcome.Body is not null)
        {
            var table = Csv.Parse(TestData.ReadText("claims_export.csv"));
            var first = table.AsDictionaries().First();

            foreach (var field in new[] { "patient_first", "patient_last", "patient_dob", "member_id" })
            {
                var value = first[field];
                if (string.IsNullOrWhiteSpace(value)) continue;

                Assert.DoesNotContain(value, outcome.Body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Batch_over_the_labelled_open_denials()
    {
        if (!Configured) return;

        var labels = ReferenceDataReader.ReadLabeledSample(
            TestData.ReadText("labeled_denials_sample.csv"));

        var outcome = AQ.Denials.Ingest.IngestPipeline.Run(
            AQ.Denials.Ingest.DataPack.Read(TestData.DataDir));

        var rules = ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv"));
        var payerName = rules.ToDictionary(r => r.PayerId, r => r.PayerName, StringComparer.Ordinal);
        var windows = ReferenceDataReader.BuildPayerWindows(rules);

        const string today = "2026-09-30";

        var claims = outcome.State.Claims.ToDictionary(c => c.ClaimId, StringComparer.Ordinal);
        var open = outcome.State.Claims
            .Where(c => c.Observations.Count > 0 && c.CurrentStatus == "4")
            .Select(c => c.ClaimId)
            .ToHashSet(StringComparer.Ordinal);

        var client = LlmClientFactory.Create();
        var report = new System.Text.StringBuilder();
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        var produced = 0;
        var malformed = 0;

        foreach (var label in labels.Where(l => open.Contains(l.ClaimId)))
        {
            var claim = claims[label.ClaimId];
            var observation = claim.Observations[^1];
            var carcs = observation.Adjustments.Select(a => a.Carc)
                          .Where(c => c.Length > 0)
                          .Distinct(StringComparer.Ordinal).ToList();

            var category = Rules.DenialCategory.Of(carcs, claim.PayerId, claim.Dos);
            var details = Rules.DenialCategory.OutcomeFor(category);

            var end = windows.DenialRouteEndDate(claim.PayerId, observation.CheckDate);
            int? days = DateTime.TryParse(end, out var endDate)
                && DateTime.TryParse(today, out var now)
                ? (endDate - now).Days
                : null;

            var draft = new DraftInput(
                ClaimId: claim.ClaimId,
                PayerName: payerName.GetValueOrDefault(claim.PayerId, claim.PayerId),
                PayerId: claim.PayerId,
                Category: category,
                Carcs: carcs,
                DeniedAmount: claim.Charge,
                NextAction: details.NextAction,
                DaysRemaining: days);

            var result = await AppealDrafting.DraftAsync(client, Library, draft);

            tally[result.Verdict.ToString()] = tally.GetValueOrDefault(result.Verdict.ToString()) + 1;
            if (result.Produced) produced++;

            // Well-formedness, checked per reply rather than assumed from the code path.
            var wellFormed =
                result.Body is not null
                    ? result.Verdict is CitationVerdict.Valid
                          or CitationVerdict.NoCitation
                          or CitationVerdict.ClauseAvailableUncited
                    : result.Verdict is not (CitationVerdict.Valid
                          or CitationVerdict.NoCitation
                          or CitationVerdict.ClauseAvailableUncited)
                      && !string.IsNullOrWhiteSpace(result.UnavailableReason);
            if (!wellFormed) malformed++;

            report.AppendLine($"=== {claim.ClaimId} [{category}] {claim.PayerId} "
                            + $"carcs={string.Join(",", carcs)} days={days?.ToString() ?? "none"}");
            report.AppendLine($"    verdict={result.Verdict} produced={result.Produced}");
            if (result.Body is not null)
                report.AppendLine("    " + result.Body.Replace("\n", "\n    "));
            else
                report.AppendLine("    (no draft) " + result.UnavailableReason);
            report.AppendLine();
        }

        var total = tally.Values.Sum();
        Console.WriteLine($"[batch] model={client.ConfiguredProvider} denials={total} produced={produced}");
        foreach (var (k, v) in tally.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"[batch] {k}: {v}");

        var path = Path.Combine(Path.GetTempPath(), "aq_live_batch.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"[batch] full transcripts -> {path}");

        // Invariants over every real reply. Note what is NOT asserted: how many were good. That is
        // a judgement about prose and belongs in AI_EVALUATION.md, not in a test that would go
        // red every time a model has an off day.
        Assert.Equal(36, total);
        Assert.Equal(total, tally.Values.Sum());
        // Every reply that shipped must be well-formed, and every reply that did not must say
        // why. `malformed` counts either violation, so the requirement is that it is zero —
        // comparing it to `produced` (as this once did) asserted that *all* replies were bad.
        Assert.Equal(0, malformed);
    }

    [Fact]
    public async Task A_real_model_is_offered_no_citation_when_no_policy_exists()
    {
        if (!Configured) return;

        var client = LlmClientFactory.Create();
        var outcome = await AppealDrafting.DraftAsync(client, Library, TimelyFiling());

        Console.WriteLine($"[live] timely-filing verdict={outcome.Verdict} produced={outcome.Produced}");
        Console.WriteLine("[live] body=" + (outcome.Body ?? "<none>"));

        // Two acceptable answers: it says there is no policy basis (right), or it produces
        // something we then refuse to ship (safe). What must not happen is a citation to a
        // document that does not cover timely filing.
        if (outcome.Verdict == CitationVerdict.Valid)
            Assert.Fail(
                "a citation validated for a CARC-29 denial, which no policy in the pack covers: "
              + $"{outcome.ClaimedCitation?.FileName} §{outcome.ClaimedCitation?.SectionId}");
    }
}
