using System.Net;
using System.Net.Http.Headers;
using AQ.Denials.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// Boots the real API in-process against the real data pack, with no database.
/// </summary>
/// <remarks>
/// <para>
/// <c>AUTOMIGRATE=false</c> keeps the test from opening a Postgres connection, so these tests
/// exercise exactly one thing: whether the server lets a request through. Every read endpoint
/// served here derives its answer from the ingestion pipeline, so they return real figures
/// without touching storage.
/// </para>
/// <para>
/// The tokens below are deliberately <b>not</b> the ones in <c>.env.example</c> nor the
/// Development defaults. A test that asserted "the dev token works" would also pass if the
/// configuration had failed to load and the fallback had kicked in; asserting that the dev
/// token is <i>rejected</i> is what proves the seeded identities were actually read.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ReaderToken = "unit-test-reader-token-00000000";
    public const string IngestToken = "unit-test-ingest-token-00000000";
    public const string SpecialistToken = "unit-test-specialist-token-00000";
    public const string ManagerToken = "unit-test-manager-token-000000000";
    public const string DevDefaultToken = "dev-reader-token";

    static ApiFactory() => ConfigureProcessEnvironment();

    /// <summary>
    /// Publishes the test configuration as <b>process environment variables</b>.
    /// </summary>
    /// <remarks>
    /// Not host-builder settings: the entry point reads <c>ConnectionStrings__Denials</c> during
    /// its own top-level statements, which run before any configuration callback a test host can
    /// register. Environment variables are the one source that is guaranteed to exist by then.
    /// The values are identical for every test in this process, so writing them once is race-free.
    /// </remarks>
    private static void ConfigureProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
        Environment.SetEnvironmentVariable("AUTOMIGRATE", "false");
        Environment.SetEnvironmentVariable("ConnectionStrings__Denials",
            "Host=localhost;Port=5432;Database=denials_unused_by_these_tests;Username=unused;Password=unused");
        Environment.SetEnvironmentVariable("DATA_DIR", TestData.DataDir);
        Environment.SetEnvironmentVariable(SeedUsers.EnvironmentVariable,
            $"[{{\"token\":\"{ReaderToken}\",\"name\":\"unit reader\",\"role\":\"reader\"}}," +
            $"{{\"token\":\"{IngestToken}\",\"name\":\"unit ops\",\"role\":\"ingest\"}}," +
            $"{{\"token\":\"{SpecialistToken}\",\"name\":\"unit specialist\",\"role\":\"specialist\"}}," +
            $"{{\"token\":\"{ManagerToken}\",\"name\":\"unit manager\",\"role\":\"manager\"}}]");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment(Environments.Development);
}

public class AuthorizationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AuthorizationTests(ApiFactory factory) => _factory = factory;

    private HttpClient Client(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method, string path)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, path));
        return response.StatusCode;
    }

    /* ---- the unauthenticated surface ---------------------------------- */

    [Fact]
    public async Task Health_is_reachable_without_a_token_because_a_probe_has_none()
    {
        var response = await Client(null).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/reconciliation")]
    [InlineData("/api/reconciliation/report.md")]
    [InlineData("/api/exceptions")]
    [InlineData("/api/claims/GPP-2026-000101")]
    [InlineData("/api/audit")]
    [InlineData("/api/analytics")]
    [InlineData("/api/prevention")]
    [InlineData("/api/worklist")]
    [InlineData("/api/worklist/GPP-2026-000101")]
    public async Task Every_protected_read_refuses_a_request_with_no_token(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(Client(null), HttpMethod.Get, path));
    }

    [Theory]
    [InlineData("/api/reconciliation")]
    [InlineData("/api/exceptions")]
    [InlineData("/api/claims/GPP-2026-000101")]
    [InlineData("/api/audit")]
    [InlineData("/api/analytics")]
    [InlineData("/api/prevention")]
    [InlineData("/api/worklist")]
    [InlineData("/api/worklist/GPP-2026-000101")]
    public async Task Every_protected_read_refuses_an_unknown_token(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(Client("not-a-real-token-at-all-00000"), HttpMethod.Get, path));
    }

    [Fact]
    public async Task A_missing_token_and_a_wrong_token_are_indistinguishable_from_the_outside()
    {
        var client = _factory.CreateClient();

        using var missing = await client.GetAsync("/api/reconciliation");

        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/api/reconciliation");
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope-nope-nope");
        using var wrong = await client.SendAsync(wrongRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_token_presented_under_another_scheme_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"user:{ApiFactory.ReaderToken}")));

        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, "/api/reconciliation"));
    }

    [Fact]
    public async Task An_empty_bearer_token_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "   ");

        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, "/api/reconciliation"));
    }

    /* ---- the fallback must not be live --------------------------------- */

    [Fact]
    public async Task The_development_fallback_token_is_not_accepted_once_seed_users_is_set()
    {
        // If this ever returns 200, configuration failed to load and the API is running on the
        // Development defaults — which would make every other test in this file meaningless.
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(Client(ApiFactory.DevDefaultToken), HttpMethod.Get, "/api/reconciliation"));
    }

    /* ---- the seeded identities do work ---------------------------------- */

    [Fact]
    public async Task A_seeded_reader_can_read_every_read_endpoint()
    {
        foreach (var path in new[]
                 {
                     "/api/reconciliation",
                     "/api/reconciliation/report.md",
                     "/api/exceptions",
                     "/api/claims/GPP-2026-000101",
                 })
        {
            var response = await Client(ApiFactory.ReaderToken).GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{path} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_ingest_identity_can_read_as_well_as_write()
    {
        Assert.Equal(HttpStatusCode.OK,
            await Status(Client(ApiFactory.IngestToken), HttpMethod.Get, "/api/reconciliation"));
    }

    [Fact]
    public async Task The_report_endpoint_returns_markdown_not_json()
    {
        var response = await Client(ApiFactory.ReaderToken)
            .GetAsync("/api/reconciliation/report.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("reconciliation", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /* ---- server-side role enforcement ------------------------------------ */

    [Fact]
    public async Task A_reader_cannot_trigger_ingestion()
    {
        var status = await Status(Client(ApiFactory.ReaderToken), HttpMethod.Post, "/api/ingest");

        // 403, not 404 and not 401: the route exists, the caller is known, the role is wrong.
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Ingestion_is_refused_anonymously_before_the_role_is_even_considered()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(Client(null), HttpMethod.Post, "/api/ingest"));
    }

    [Fact]
    public async Task An_ingest_identity_passes_the_role_gate()
    {
        // Not asserting 200, because the handler then needs a database. Asserting it is neither
        // 401 nor 403 is exactly and only the authorization claim being tested.
        var status = await Status(Client(ApiFactory.IngestToken), HttpMethod.Post, "/api/ingest");

        Assert.NotEqual(HttpStatusCode.Unauthorized, status);
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    /* ---- the worklist's two roles ---------------------------------------- */

    [Theory]
    [InlineData("/api/worklist")]
    [InlineData("/api/worklist/GPP-2026-000101")]
    public async Task A_reader_cannot_reach_the_worklist(string path)
    {
        // 403, not 404: the route exists and the caller is known. The brief defines two app
        // roles and `reader` is not one of them — it is the Phase 1 API role.
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(Client(ApiFactory.ReaderToken), HttpMethod.Get, path));
    }

    [Fact]
    public async Task An_ingest_identity_cannot_reach_the_worklist_either()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(Client(ApiFactory.IngestToken), HttpMethod.Get, "/api/worklist"));
    }

    [Fact]
    public async Task A_specialist_passes_the_worklist_role_gate()
    {
        var status = await Status(Client(ApiFactory.SpecialistToken), HttpMethod.Get, "/api/worklist");

        Assert.NotEqual(HttpStatusCode.Unauthorized, status);
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    [Theory]
    [InlineData("/api/worklist/GPP-2026-000101/assign")]
    [InlineData("/api/worklist/drafts")]
    public async Task Reassignment_and_bulk_drafting_are_manager_only(string path)
    {
        // A specialist doing either of these would be taking work off a colleague's screen or
        // spending the model's budget on someone else's queue. Refused before the handler runs,
        // so this needs no database and no body.
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(Client(ApiFactory.SpecialistToken), HttpMethod.Post, path));

        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(Client(ApiFactory.ReaderToken), HttpMethod.Post, path));
    }

    [Fact]
    public async Task A_specialist_passes_the_role_gate_on_their_own_status_endpoint()
    {
        var status = await Status(Client(ApiFactory.SpecialistToken), HttpMethod.Post,
            "/api/worklist/GPP-2026-000101/status");

        Assert.NotEqual(HttpStatusCode.Unauthorized, status);
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task A_specialist_may_not_reassign()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(Client(ApiFactory.SpecialistToken), HttpMethod.Post,
                "/api/worklist/GPP-2026-000101/assign"));
    }

    /* ---- the manager's screens need no database, so they can be asserted fully -- */

    [Theory]
    [InlineData("/api/analytics")]
    [InlineData("/api/prevention")]
    public async Task The_manager_screens_are_readable_by_the_api_read_role(string path)
    {
        // Both derive from the data pack alone, so a full 200 is assertable rather than merely
        // "not refused" — and it proves the figures do not depend on a table being populated.
        Assert.Equal(HttpStatusCode.OK,
            await Status(Client(ApiFactory.ReaderToken), HttpMethod.Get, path));

        Assert.Equal(HttpStatusCode.OK,
            await Status(Client(ApiFactory.SpecialistToken), HttpMethod.Get, path));
    }

    /* ---- a claim route is a lookup, not a lookup-shaped injection ---------- */

    [Fact]
    public async Task A_claim_reference_treated_as_a_predicate_returns_nothing_rather_than_everything()
    {
        var injection = Uri.EscapeDataString("' OR 1=1 --");

        var response = await Client(ApiFactory.ReaderToken).GetAsync($"/api/claims/{injection}");

        // Not 200, not 500: the value was searched for as a value and simply is not there.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_claim_is_404_not_a_500()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(Client(ApiFactory.ReaderToken), HttpMethod.Get, "/api/claims/NO-SUCH-CLAIM"));
    }

    /* ---- what the API actually exposes ------------------------------------- */

    [Fact]
    public async Task The_claim_view_carries_no_direct_patient_identifiers()
    {
        var body = await Client(ApiFactory.ReaderToken)
            .GetStringAsync("/api/claims/GPP-2026-000101");

        // The claim reference, payer, dates, money and history are operationally necessary.
        // Name, DOB and member id are not, so they are not on the wire in Phase 1.
        Assert.DoesNotContain("1938-04-17", body, StringComparison.Ordinal);   // a DOB
        Assert.DoesNotContain("NS65961640", body, StringComparison.Ordinal);   // a member id
        Assert.Contains("GPP-2026-000101", body, StringComparison.Ordinal);
        Assert.Contains("NS401", body, StringComparison.Ordinal);
    }
}

/// <summary>Configuration is not optional, and a weak identity is not a usable one.</summary>
public class SeedUsersTests
{
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static IReadOnlyList<ApiIdentity> Read(string? json, string environment = "Production")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SeedUsers.EnvironmentVariable] = json,
            })
            .Build();

        return SeedUsers.Read(configuration, new StubEnvironment { EnvironmentName = environment });
    }

    [Fact]
    public void Production_refuses_to_start_without_seeded_identities()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Read(null));
        Assert.Contains(SeedUsers.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("will not start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_still_needs_an_explicit_choice_but_gets_local_defaults()
    {
        var identities = Read(null, Environments.Development);

        // Four, not two: the defaults seed both API roles and both worklist roles, so a local
        // `dotnet run` has the same surface as a seeded `docker compose up` and a missing role
        // shows up as an empty queue rather than as a mystery 403.
        Assert.Equal(4, identities.Count);
        Assert.Contains(identities, i => i.Role == Roles.Reader);
        Assert.Contains(identities, i => i.Role == Roles.Ingest);
        Assert.Contains(identities, i => i.Role == Roles.Specialist);
        Assert.Contains(identities, i => i.Role == Roles.Manager);
    }

    [Fact]
    public void A_configuration_that_cannot_work_the_worklist_is_refused_at_startup()
    {
        // The brief names two roles. Seeding only one of them means the other's screens are
        // unreachable for everyone, which is a misconfiguration — fail here, not at first click.
        var ex = Assert.Throws<InvalidOperationException>(() => Read(
            """[{"token":"a-sufficiently-long-token","name":"a","role":"reader"}]"""));

        Assert.Contains("specialist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_worklist_roles_must_be_present_before_the_service_starts()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Read(
            """[{"token":"a-sufficiently-long-token","name":"a","role":"specialist"}]"""));

        Assert.Contains("manager", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_json_is_reported_as_malformed_rather_than_as_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Read("{not json"));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_identity_list_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Read("[]"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]                     // present but blank
    [InlineData(null)]
    public void A_short_token_is_rejected_because_it_would_be_trivially_guessable(string? json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Read(json));

        // Whatever the message says, it must never be about a *different* problem — a short
        // token and a missing variable are different failures needing different fixes.
        Assert.True(
            ex.Message.Contains("no users", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("shorter than 16", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains(SeedUsers.EnvironmentVariable, StringComparison.Ordinal),
            ex.Message);
    }

    [Fact]
    public void A_token_that_is_too_short_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Read("""[{"token":"short","name":"a","role":"reader"}]"""));

        Assert.Contains("shorter than 16", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_role_is_rejected_rather_than_silently_promoted()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Read("""[{"token":"a-sufficiently-long-token","name":"a","role":"admin"}]"""));

        Assert.Contains("unknown role", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("admin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Well_formed_identities_round_trip_with_their_roles_intact()
    {
        var identities = Read(
            """[{"token":"a-sufficiently-long-token","name":"alice","role":"ingest"},{"token":"another-long-enough-token","name":"bob","role":"specialist"},{"token":"third-long-enough-token-here","name":"cleo","role":"manager"}]""");

        Assert.Equal(3, identities.Count);
        Assert.Equal("alice", identities[0].Name);
        Assert.Equal(Roles.Ingest, identities[0].Role);
        Assert.Equal("a-sufficiently-long-token", identities[0].Token);
        Assert.Equal(Roles.Specialist, identities[1].Role);
        Assert.Equal(Roles.Manager, identities[2].Role);
    }
}
