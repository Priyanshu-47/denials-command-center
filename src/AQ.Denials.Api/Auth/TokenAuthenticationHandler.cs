using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AQ.Denials.Api.Auth;

/// <summary>A seeded API identity. Roles are checked on the server; the client cannot assert them.</summary>
public sealed record ApiIdentity(string Token, string Name, string Role);

public static class Roles
{
    public const string Reader = "reader";
    public const string Ingest = "ingest";
}

public static class Policies
{
    /// <summary>Read the reconciliation, exceptions and claim views.</summary>
    public const string Reader = "reader";

    /// <summary>Trigger a re-ingestion of the data pack.</summary>
    public const string Ingest = "ingest";
}

/// <summary>
/// Reads the seeded identities from <c>SEED_USERS</c>, falling back to a local-dev default.
/// </summary>
/// <remarks>
/// <para>
/// Format: a JSON array of <c>{"token","name","role"}</c>. The tokens themselves come from the
/// environment — they are never written in source, never logged, and never returned by an
/// endpoint. <c>.env.example</c> documents the variable; <c>docker compose</c> passes it through.
/// </para>
/// <para>
/// If <c>SEED_USERS</c> is absent or malformed the service refuses to start rather than starting
/// unauthenticated. A fallback "any token works" default would be the single most likely way for
/// this to reach production looking secure and behaving open.
/// </para>
/// </remarks>
public static class SeedUsers
{
    public const string EnvironmentVariable = "SEED_USERS";

    /// <summary>Used only when no environment is configured and the environment is Development.</summary>
    public static readonly IReadOnlyList<ApiIdentity> DevelopmentDefaults =
    [
        new("dev-reader-token", "local reader", Roles.Reader),
        new("dev-ingest-token", "local ingest operator", Roles.Ingest),
    ];

    public static IReadOnlyList<ApiIdentity> Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration[EnvironmentVariable];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (environment.IsDevelopment()) return DevelopmentDefaults;
            throw new InvalidOperationException(
                $"{EnvironmentVariable} is not set. Seed at least one user, for example: "
              + "[{\"token\":\"...\",\"name\":\"alice\",\"role\":\"ingest\"}]. "
              + "The service will not start without identities.");
        }

        List<ApiIdentity>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ApiIdentity>>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is not valid JSON.", ex);
        }

        if (parsed is null || parsed.Count == 0)
            throw new InvalidOperationException($"{EnvironmentVariable} contains no users.");

        foreach (var identity in parsed)
        {
            if (string.IsNullOrWhiteSpace(identity.Token) || identity.Token.Length < 16)
                throw new InvalidOperationException(
                    $"A token in {EnvironmentVariable} is missing or shorter than 16 characters "
                  + $"(user '{identity.Name}').");

            if (identity.Role != Roles.Reader && identity.Role != Roles.Ingest)
                throw new InvalidOperationException(
                    $"User '{identity.Name}' has unknown role '{identity.Role}'. "
                  + $"Expected '{Roles.Reader}' or '{Roles.Ingest}'.");
        }

        return parsed;
    }
}

public sealed class TokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    private readonly IReadOnlyList<ApiIdentity> _identities;

    public TokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IReadOnlyList<ApiIdentity> identities)
        : base(options, logger, encoder)
        => _identities = identities;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No Authorization header at all is not an error — it is simply an unauthenticated
        // request, and the policy layer decides what that means.
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Unsupported authorization scheme."));

        var presented = header["Bearer ".Length..].Trim();
        if (presented.Length == 0)
            return Task.FromResult(AuthenticateResult.Fail("Empty bearer token."));

        var identity = Find(presented);
        if (identity is null)
            return Task.FromResult(AuthenticateResult.Fail("Unknown or expired token."));

        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, identity.Name),
                // ClaimTypes.Role, not the literal "role": AuthorizeCore resolves RequireRole
                // through ClaimsPrincipal.IsInRole, which reads exactly this claim type. A
                // short-string claim would authenticate perfectly and then authorise nothing.
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, identity.Role),
            ],
            SchemeName));

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // 401 explicitly: WriteAsJsonAsync alone leaves the status at 200, which would make a
        // refused request look like a successful one to anything that only checks the status.
        // The body is identical for a missing and an unknown token, so a probe cannot tell
        // "wrong token" from "no token" by reading the response.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = "authentication_required" });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "insufficient_role" });
    }

    private ApiIdentity? Find(string presented)
    {
        ApiIdentity? match = null;
        foreach (var identity in _identities)
        {
            var equal = FixedTimeEquals(identity.Token, presented);
            if (equal && match is null) match = identity;
        }
        return match;
    }

    /// <summary>
    /// Length-independent comparison, so response timing does not reveal how many leading
    /// characters of a token were right.
    /// </summary>
    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
