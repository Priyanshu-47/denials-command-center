using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AQ.Denials.Ingest;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The billed → paid → denied timeline must name the remittance file each row came from.
/// </summary>
/// <remarks>
/// <para>
/// Regression test for a defect found during the Azure VM deployment: <c>/api/worklist/{claimId}</c>
/// read <c>IStateProvider.Current</c> <i>inside</i> its history projection, while
/// <c>Current</c> re-runs the whole ingestion pipeline on every access (see <c>StateProvider.cs</c>).
/// The observation therefore belonged to one object graph and the remit files to another.
/// <c>RemitObservation</c> is a plain class, so <c>List.Contains</c> is reference equality and the
/// match could never succeed: every <c>sourceFile</c> came back null and the UI rendered
/// "source unknown" on every timeline row. <c>/api/claims/{claimId}</c> was unaffected because it
/// captures <c>var outcome = state.Current;</c> once.
/// </para>
/// <para>
/// The second test pins the mechanism itself, so the reason for capturing the snapshot once
/// stays documented in executable form rather than only in a comment.
/// </para>
/// </remarks>
public class ClaimTimelineTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ClaimTimelineTests(ApiFactory factory) => _factory = factory;

    private static readonly Lazy<IngestOutcome> Outcome =
        new(() => IngestPipeline.Run(DataPack.Read(TestData.DataDir)));

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /* ---- the mechanism ------------------------------------------------ */

    [Fact]
    public void Within_one_snapshot_every_observation_names_the_file_it_came_from()
    {
        var outcome = Outcome.Value;

        var observations = outcome.State.Claims
            .SelectMany(c => c.Observations)
            .ToList();

        Assert.NotEmpty(observations);

        var orphaned = observations
            .Where(o => !outcome.State.RemitFiles.Any(f => f.Observations.Contains(o)))
            .ToList();

        // An observation that no remit file claims is exactly the defect: it renders as
        // "source unknown" and the reader loses the link between a denial and its remittance.
        Assert.True(orphaned.Count == 0,
            $"{orphaned.Count} of {observations.Count} observations belong to no remit file");
    }

    [Fact]
    public void A_second_pipeline_run_is_a_different_object_graph_so_matching_must_not_cross_it()
    {
        var first = IngestPipeline.Run(DataPack.Read(TestData.DataDir));
        var second = IngestPipeline.Run(DataPack.Read(TestData.DataDir));

        var observation = first.State.Claims
            .SelectMany(c => c.Observations)
            .First();

        // Resolves inside the graph the observation came from...
        Assert.Contains(first.State.RemitFiles, f => f.Observations.Contains(observation));

        // ...and silently fails across graphs. This is why an endpoint that reads
        // IStateProvider.Current twice gets null source files, and why it must capture one
        // snapshot per request (the documented "no cache" rule in StateProvider.cs still holds:
        // one snapshot per request is not a cache and not a second source of truth).
        Assert.DoesNotContain(second.State.RemitFiles, f => f.Observations.Contains(observation));
    }

    /* ---- the endpoint ------------------------------------------------- */

    [Fact]
    public async Task Every_claim_timeline_row_names_its_source_remittance_file()
    {
        var claimId = Outcome.Value.State.Claims
            .Where(c => c.Observations.Count > 0)
            .Select(c => c.ClaimId)
            .First();

        var response = await Client(ApiFactory.ReaderToken)
            .GetAsync($"/api/claims/{claimId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var history = doc.RootElement.GetProperty("history");

        Assert.True(history.GetArrayLength() > 0, "a claim with observations must have a timeline");

        foreach (var row in history.EnumerateArray())
        {
            Assert.True(row.TryGetProperty("sourceFile", out var file),
                "every timeline row carries sourceFile");

            // xUnit builds the message eagerly, so it must not assume casing that differs
            // between the anonymous projection and the serialized payload.
            Assert.False(file.ValueKind == JsonValueKind.Null,
                $"timeline row seq={row.GetProperty("seq").GetInt32()} on claim {claimId} " +
                "has no source file — the worklist/claims projections have drifted apart again");

            Assert.EndsWith(".835", file.GetString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_claim_projection_and_the_worklist_projection_share_one_shape()
    {
        // The claim projection built by /api/claims/{id} is what the worklist detail page renders
        // too (Program.cs documents them as "same projection shape so the detail page can render
        // both without two models"). Both must therefore expose the same history column set;
        // a column present on one and missing on the other is how the sourceFile bug stayed
        // invisible to the suite for so long.
        var claimId = Outcome.Value.State.Claims
            .Where(c => c.Observations.Count > 0)
            .Select(c => c.ClaimId)
            .First();

        using var response = await Client(ApiFactory.ReaderToken)
            .GetAsync($"/api/claims/{claimId}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var row = doc.RootElement.GetProperty("history")[0];

        foreach (var expected in new[] { "seq", "payerId", "checkDate", "statusCode",
                                          "submittedCharge", "paidAmount",
                                          "patientResponsibility", "sourceFile", "adjustments" })
        {
            Assert.True(row.TryGetProperty(expected, out _),
                $"the claim timeline row is missing '{expected}'");
        }
    }
}
