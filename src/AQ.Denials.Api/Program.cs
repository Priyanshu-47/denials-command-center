using System.Security.Claims;
using System.Text.Encodings.Web;
using AQ.Denials.Api.Auth;
using AQ.Denials.Api.Data;
using AQ.Denials.Api.Reports;
using AQ.Denials.Api.State;
using AQ.Denials.Ingest;
using AQ.Denials.Llm;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------------------
// Standalone report mode: `dotnet run --project src/AQ.Denials.Api -- --report`
// Writes the reconciliation markdown without starting the web host or touching
// the database, so a report can be generated on a machine with no Postgres.
// ---------------------------------------------------------------------------
if (args.Contains("--report"))
{
    Report.Write(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

/* ---- configuration, read from the environment only --------------------- */

var dataDir = builder.Configuration["DATA_DIR"] ?? DataPack.DefaultDataDir;
var connectionString = builder.Configuration.GetConnectionString("Denials")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Denials is not set. Copy .env.example to .env and start with "
      + "`docker compose up`. No database URL is ever hard-coded.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Seeded identities. Fails fast and loudly if they are missing outside Development —
// see SeedUsers for why there is deliberately no permissive default.
var identities = SeedUsers.Read(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(identities);

builder.Services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Reader, policy => policy.RequireRole(Roles.Reader, Roles.Ingest));
    options.AddPolicy(Policies.Ingest, policy => policy.RequireRole(Roles.Ingest));
});

// The LLM is never reached directly; Phase 2 supplies an implementation.
// NullLlmClient refuses rather than inventing, which is the safe default.
builder.Services.AddSingleton<ILlmClient, NullLlmClient>();

// Canonical state is derived from the data pack on demand — the same pure function that the
// tests assert against, so an endpoint can never show a number the test suite has not seen.
builder.Services.AddScoped<IStateProvider, DerivedStateProvider>();

var app = builder.Build();

/* ---- no UseHttpsRedirection ------------------------------------------
   The container listens on plain HTTP inside the compose network; TLS is
   expected to terminate at a reverse proxy in front of it. Enabling the
   middleware here would log "Failed to determine the https port for
   redirect" on every request and redirect nothing.
   -------------------------------------------------------------------- */

app.UseAuthentication();
app.UseAuthorization();

/* ---- schema ---------------------------------------------------------
   `Migrate()` applies the checked-in migrations, it does not invent schema.
   Reverting AUTOMIGRATE to false and running `dotnet ef database update`
   yourself produces exactly the same database — this is a convenience, not
   a second source of truth.
   -------------------------------------------------------------------- */

var automigrate = !bool.TryParse(builder.Configuration["AUTOMIGRATE"], out var flag) || flag;
if (automigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();

/* ---- health: unauthenticated, because a container probe has no token ----- */

app.MapGet("/health", (IStateProvider state) =>
{
    var outcome = state.Current;
    return Results.Ok(new
    {
        status = "ok",
        dataPack = dataDir,
        claims = outcome.State.Claims.Count,
        remitFiles = outcome.State.RemitFiles.Count,
        rowsIn = outcome.RowsIn,
        exceptions = outcome.RowsExcepted,
        checksum = outcome.Checksum,
    });
})
.WithName("Health")
.WithOpenApi();

/* ---- reconciliation ------------------------------------------------------ */

app.MapGet("/api/reconciliation", (IStateProvider state) =>
        Results.Ok(ReconciliationDto.From(state.Current.Reconciliation)))
    .WithName("GetReconciliation")
    .WithOpenApi()
    .RequireAuthorization(Policies.Reader);

app.MapGet("/api/reconciliation/report.md", (IStateProvider state) =>
        Results.Text(state.Current.Reconciliation.ToMarkdown(), "text/markdown"))
    .WithName("GetReconciliationReport")
    .WithOpenApi()
    .RequireAuthorization(Policies.Reader);

/* ---- exceptions view ------------------------------------------------------ */

app.MapGet("/api/exceptions", (IStateProvider state, string? reason, string? claimId) =>
{
    var query = state.Current.Exceptions.AsEnumerable();

    if (!string.IsNullOrWhiteSpace(reason))
        query = query.Where(e => string.Equals(e.ReasonCode, reason, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(claimId))
        query = query.Where(e => string.Equals(e.ClaimId, claimId, StringComparison.OrdinalIgnoreCase));

    var rows = query
        .OrderBy(e => e.ReasonCode, StringComparer.Ordinal)
        .ThenBy(e => e.SourceFile, StringComparer.Ordinal)
        .ThenBy(e => e.ClaimId, StringComparer.Ordinal)
        .Select(e => new
        {
            e.ReasonCode,
            e.SourceFile,
            e.Detail,
            e.ClaimId,
            e.OriginalRef,
            e.CreatedAt,
        })
        .ToList();

    return Results.Ok(new
    {
        total = rows.Count,
        byReason = state.Current.ExceptionCountsByReason,
        conservation = new
        {
            rowsIn = state.Current.RowsIn,
            matched = state.Current.RowsMatched,
            exceptions = state.Current.RowsExcepted,
            holds = state.Current.RowsIn == state.Current.RowsMatched + state.Current.RowsExcepted,
        },
        rows,
    });
})
.WithName("GetExceptions")
.WithOpenApi()
.RequireAuthorization(Policies.Reader);

/* ---- one claim and its full remittance history ---------------------------- */

app.MapGet("/api/claims/{claimId}", (string claimId, IStateProvider state) =>
{
    // Parameterised through LINQ: the route value is a value, never a fragment of a statement.
    var outcome = state.Current;
    var claim = outcome.State.Claims.FirstOrDefault(
        c => string.Equals(c.ClaimId, claimId, StringComparison.Ordinal));

    if (claim is null)
        return Results.NotFound(new { error = "claim_not_found", claimId });

    return Results.Ok(new
    {
        claim.ClaimId,
        claim.PayerId,
        claim.PayerName,
        claim.Dos,
        claim.SubmittedDate,
        charge = claim.Charge,
        claim.Adjudicated,
        claim.CurrentStatus,
        paidAmount = claim.PaidAmount,
        lifetimePaid = claim.LifetimePaid,
        claim.LastCheckDate,
        lines = claim.Lines.Select(l => new
        {
            l.LineNo, l.Cpt, l.Modifier, l.Units, l.Charge, l.Dx1, l.Dx2, l.Dx3, l.Dx4,
        }),
        history = claim.Observations.OrderBy(o => o.Seq).Select(o => new
        {
            o.Seq,
            o.PayerId,
            o.CheckDate,
            o.StatusCode,
            submittedCharge = o.SubmittedCharge,
            paidAmount = o.PaidAmount,
            patientResponsibility = o.PatientResponsibility,
            o.NaturalKey,
            sourceFile = outcome.State.RemitFiles
                .Where(f => f.Observations.Contains(o))
                .Select(f => f.FileName)
                .FirstOrDefault(),
            adjustments = o.Adjustments.Select(a => new
            {
                a.GroupCode, a.Carc, a.Amount, scope = a.IsServiceLevel ? "service" : "claim",
            }),
        }),
    });
})
.WithName("GetClaim")
.WithOpenApi()
.RequireAuthorization(Policies.Reader);

/* ---- audit trail ----------------------------------------------------------- */

app.MapGet("/api/audit", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var entries = await db.AuditLog.AsNoTracking()
        .OrderByDescending(a => a.At)
        .ThenByDescending(a => a.Id)
        .Take(500)
        .Select(a => new { a.At, a.Actor, a.Action, a.EntityType, a.EntityKey, a.Detail, a.RunGuid })
        .ToListAsync(cancellationToken);

    return Results.Ok(new { total = entries.Count, entries });
})
.WithName("GetAudit")
.WithOpenApi()
.RequireAuthorization(Policies.Reader);

/* ---- re-ingest --------------------------------------------------------------- */

app.MapPost("/api/ingest", async (ClaimsPrincipal user, AppDbContext db, IStateProvider state,
        CancellationToken cancellationToken) =>
{
    var outcome = state.Current;
    var actor = user.Identity?.Name ?? "unknown";

    var previous = await db.IngestRuns.AsNoTracking()
        .Where(r => r.Succeeded)
        .OrderByDescending(r => r.Id)
        .FirstOrDefaultAsync(cancellationToken);

    var unchanged = previous is not null && previous.FullStateChecksum == outcome.Checksum;

    var run = new AQ.Denials.Core.Domain.IngestRun
    {
        RunGuid = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        FullStateChecksum = outcome.Checksum,
        RowsIn = outcome.RowsIn,
        RowsMatched = outcome.RowsMatched,
        RowsExcepted = outcome.RowsExcepted,
        Succeeded = true,
    };

    // One transaction for the whole replacement: a partial write would leave books that agree
    // with neither the previous run nor the data pack, and nothing would notice.
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

    db.IngestRuns.Add(run);
    await db.SaveChangesAsync(cancellationToken);   // gives run.Id to the exception rows

    if (!unchanged)
    {
        // The pack changed since the last run, so the stored books are stale: replace them.
        // Audit entries are append-only and are never cleared.
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE "Adjustments", "ObservationServices", "RemitObservations",
                            "RemitFiles", "ClaimLines", "Claims", "WorklogEntries", "ExceptionRows"
            CASCADE;
            """,
            cancellationToken);

        Persist(db, outcome.State, run);
    }

    foreach (var entry in outcome.AuditEntries)
        db.AuditLog.Add(new AQ.Denials.Core.Domain.AuditLogEntry
        {
            At = entry.At,
            Actor = actor,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityKey = entry.EntityKey,
            Detail = entry.Detail,
            RunGuid = run.RunGuid,
        });

    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    return Results.Ok(new
    {
        runId = run.RunGuid,
        unchanged,
        checksum = outcome.Checksum,
        rowsIn = outcome.RowsIn,
        matched = outcome.RowsMatched,
        exceptions = outcome.RowsExcepted,
        claims = outcome.State.Claims.Count,
        cashExcludingDuplicate = outcome.Reconciliation.CashExcludingDuplicate,
    });
})
.WithName("RunIngest")
.WithOpenApi()
.RequireAuthorization(Policies.Ingest);

app.Run();

/// <summary>Writes the canonical state to the database. Idempotent by construction: called only
/// against a freshly truncated set of tables.</summary>
static void Persist(AppDbContext db, AQ.Denials.Core.Domain.CanonicalState state,
    AQ.Denials.Core.Domain.IngestRun run)
{
    foreach (var claim in state.Claims)
    {
        foreach (var line in claim.Lines) line.Claim = claim;
        foreach (var observation in claim.Observations)
        {
            observation.Claim = claim;
            foreach (var adjustment in observation.Adjustments)
            {
                adjustment.RemitObservation = observation;
                if (adjustment.Service is not null)
                    adjustment.Service.RemitObservation = observation;
            }
        }
        db.Claims.Add(claim);
    }

    // The observation carries the foreign key, so both navigations must agree before EF sees
    // the graph — otherwise the file rows are written with no observations attached and the
    // stored books disagree with the derived ones.
    foreach (var file in state.RemitFiles)
    {
        foreach (var observation in file.Observations)
            observation.RemitFile = file;
        db.RemitFiles.Add(file);
    }

    foreach (var entry in state.Worklog)
        db.WorklogEntries.Add(entry);

    foreach (var exception in state.Exceptions(DateTimeOffset.UtcNow))
    {
        exception.IngestRunId = run.Id;
        db.ExceptionRows.Add(exception);
    }
}

/// <summary>Public entry point for <c>WebApplicationFactory</c> in the API tests.</summary>
public partial class Program { }
