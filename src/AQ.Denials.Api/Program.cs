using System.Security.Claims;
using System.Text.Encodings.Web;
using AQ.Denials.Api.Auth;
using AQ.Denials.Api.Data;
using AQ.Denials.Api.Reports;
using AQ.Denials.Api.State;
using AQ.Denials.Api.Worklist;
using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest;
using AQ.Denials.Llm;
using AQ.Denials.Rules;
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
    options.AddPolicy(Policies.Reader, policy => policy.RequireRole(
        Roles.Reader, Roles.Ingest, Roles.Specialist, Roles.Manager));
    options.AddPolicy(Policies.Ingest, policy => policy.RequireRole(Roles.Ingest));
    options.AddPolicy(Policies.Worklist, policy => policy.RequireRole(Roles.Specialist, Roles.Manager));
    options.AddPolicy(Policies.Manage, policy => policy.RequireRole(Roles.Manager));
});

// The LLM is never reached directly; Phase 2 supplies an implementation.
// NullLlmClient refuses rather than inventing, which is the safe default.
builder.Services.AddSingleton<ILlmClient, NullLlmClient>();

// Canonical state is derived from the data pack on demand — the same pure function that the
// tests assert against, so an endpoint can never show a number the test suite has not seen.
builder.Services.AddScoped<IStateProvider, DerivedStateProvider>();

// Reference tables (payer windows, policy library) parsed once. Reading them per request would
// make queue latency a function of the filesystem, and a second parse is a second chance for
// two reads of the same file to disagree.
builder.Services.AddSingleton<ReferenceDataService>();
builder.Services.AddScoped<WorklistService>();

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

/* ---- manager analytics (deliverable E) ------------------------------------------- */

app.MapGet("/api/analytics", (IStateProvider state, ReferenceDataService reference) =>
{
    var outcome = state.Current;
    return Results.Ok(Analytics.Build(
        outcome.State.Claims, reference.Windows, WorklistService.Today));
})
.WithName("GetAnalytics")
.WithOpenApi()
.RequireAuthorization(Policies.Reader);

app.MapGet("/api/prevention", (IStateProvider state, ReferenceDataService reference) =>
    Results.Ok(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        evaluatedClaims = state.Current.State.Claims.Count,
        checks = PreventionAnalyzer.Analyse(
            state.Current.State.Claims, reference.Windows, WorklistService.Today),
    }))
.WithName("GetPrevention")
.WithOpenApi()
.RequireAuthorization(Policies.Reader);

/* ---- worklist ---------------------------------------------------------------------
   Deliverable D. Two roles: a specialist sees their own queue and may change status and
   notes; a manager sees everything, reassigns, and runs drafting in bulk.
   ------------------------------------------------------------------------------------ */

app.MapGet("/api/worklist", async (
        ClaimsPrincipal user, WorklistService worklist,
        string? status, string? team, string? bucket, string? assignee,
        bool? review, string? q,
        CancellationToken cancellationToken) =>
{
    var (name, role) = Actor(user);
    var rows = await worklist.BuildAsync(cancellationToken);
    var isManager = role == Roles.Manager;

    // "Own queue" for a specialist means: mine, plus what nobody has picked up yet. Strictly
    // "assigned to me only" would render an untouched pack as an empty screen on first login,
    // and a queue you cannot take work from is a report, not a queue. Items assigned to a
    // *different* specialist stay hidden — that half is what "own" is protecting.
    if (!isManager)
        rows = rows.Where(r => r.Assignee is null || r.Assignee == name).ToList();

    if (!string.IsNullOrWhiteSpace(status))
        rows = rows.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(team))
        rows = rows.Where(r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(bucket))
        rows = rows.Where(r => string.Equals(r.Bucket, bucket, StringComparison.OrdinalIgnoreCase)).ToList();
    if (review == true)
        rows = rows.Where(r => r.RequiresHumanReview).ToList();
    if (review == false)
        rows = rows.Where(r => !r.RequiresHumanReview).ToList();
    if (!string.IsNullOrWhiteSpace(assignee))
        rows = rows.Where(r => string.Equals(r.Assignee, assignee, StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(q))
        rows = rows.Where(r =>
            r.ClaimId.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.PayerName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

    var visible = rows;
    return Results.Ok(new
    {
        role,
        you = name,
        total = visible.Count,
        needsReview = visible.Count(r => r.RequiresHumanReview),
        moneyAtStake = visible.Sum(r => r.Charge),
        byStatus = visible.GroupBy(r => r.Status, StringComparer.OrdinalIgnoreCase)
                          .OrderBy(g => g.Key, StringComparer.Ordinal)
                          .Select(g => new { status = g.Key, count = g.Count(),
                                             amount = g.Sum(r => r.Charge) }),
        byBucket = visible.GroupBy(r => r.Bucket, StringComparer.Ordinal)
                          .OrderBy(g => g.Key, StringComparer.Ordinal)
                          .Select(g => new { bucket = g.Key, count = g.Count(),
                                             amount = g.Sum(r => r.Charge) }),
        rows = visible.Select(Shape),
    });
})
.WithName("GetWorklist")
.WithOpenApi()
.RequireAuthorization(Policies.Worklist);

app.MapGet("/api/worklist/{claimId}", async (
        string claimId, ClaimsPrincipal user, WorklistService worklist,
        AppDbContext db, IStateProvider state, CancellationToken cancellationToken) =>
{
    var (name, role) = Actor(user);
    var row = (await worklist.BuildAsync(cancellationToken))
        .FirstOrDefault(r => string.Equals(r.ClaimId, claimId, StringComparison.Ordinal));

    if (row is null)
        return Results.NotFound(new { error = "not_an_open_denial", claimId });

    if (role != Roles.Manager && row.Assignee is not null && row.Assignee != name)
        return Results.NotFound(new { error = "not_an_open_denial", claimId });
        // Same 404 as "does not exist": telling a specialist that a colleague's claim exists
        // but is theirs to see turns the queue into a directory of who is working on what.

    var events = await db.WorkItemEvents.AsNoTracking()
        .Where(e => e.ClaimId == claimId)
        .OrderBy(e => e.At).ThenBy(e => e.Id)
        .Select(e => new { e.At, e.Actor, e.Field, e.Before, e.After, e.Note })
        .ToListAsync(cancellationToken);

    // The claim itself, for the billed → paid → denied timeline. Same projection shape as
    // /api/claims/{id} so the detail page can render both without two models.
    var claim = state.Current.State.Claims
        .FirstOrDefault(c => string.Equals(c.ClaimId, claimId, StringComparison.Ordinal));

    return Results.Ok(new
    {
        row = Shape(row),
        events,
        claim = claim is null ? null : new
        {
            claim.ClaimId, claim.PayerId, claim.PayerName, claim.Dos, claim.SubmittedDate,
            charge = claim.Charge, claim.Adjudicated, claim.CurrentStatus,
            paidAmount = claim.PaidAmount, lifetimePaid = claim.LifetimePaid,
            claim.LastCheckDate,
            lines = claim.Lines.Select(l => new
            {
                l.LineNo, l.Cpt, l.Modifier, l.Units, l.Charge, l.Dx1, l.Dx2, l.Dx3, l.Dx4,
            }),
            history = claim.Observations.OrderBy(o => o.Seq).Select(o => new
            {
                o.Seq, o.PayerId, o.CheckDate, o.StatusCode,
                submittedCharge = o.SubmittedCharge, o.PaidAmount, o.PatientResponsibility,
                sourceFile = state.Current.State.RemitFiles
                    .Where(f => f.Observations.Contains(o))
                    .Select(f => f.FileName).FirstOrDefault(),
                adjustments = o.Adjustments.Select(a => new
                {
                    a.GroupCode, a.Carc, a.Amount, scope = a.IsServiceLevel ? "service" : "claim",
                }),
            }),
        },
    });
})
.WithName("GetWorklistItem")
.WithOpenApi()
.RequireAuthorization(Policies.Worklist);

app.MapPost("/api/worklist/{claimId}/status", async (
        string claimId, StatusRequest body, ClaimsPrincipal user, WorklistService worklist,
        AppDbContext db, CancellationToken cancellationToken) =>
{
    var (name, role) = Actor(user);

    if (!WorkStatus.IsValid(body.Status ?? string.Empty))
        return Results.BadRequest(new { error = "unknown_status", allowed = WorkStatus.All });

    var rows = await worklist.BuildAsync(cancellationToken);
    var row = rows.FirstOrDefault(r => string.Equals(r.ClaimId, claimId, StringComparison.Ordinal));
    if (row is null)
        return Results.NotFound(new { error = "not_an_open_denial", claimId });

    // A specialist may move their own work and unassigned work, not somebody else's. Same
    // 404 as the detail route, for the same reason: a 403 would confirm that a colleague's
    // claim exists, and the queue is not a directory of who is working on what.
    if (role != Roles.Manager && row.Assignee is not null && row.Assignee != name)
        return Results.NotFound(new { error = "not_an_open_denial", claimId });

    var now = DateTimeOffset.UtcNow;
    var item = await db.WorkItems
        .FirstOrDefaultAsync(w => w.ClaimId == claimId, cancellationToken);

    if (item is null)
    {
        item = new WorkItem
        {
            ClaimId = claimId,
            CreatedBy = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);   // gives the row an id before events attach
    }

    var before = item.Status;
    var changed = !string.Equals(before, body.Status, StringComparison.OrdinalIgnoreCase);

    // One transaction for the change and its audit: an audit trail written after the commit has
    // a window in which the change happened and nothing says so.
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

    if (changed)
    {
        item.Status = body.Status!;
        db.WorkItemEvents.Add(new WorkItemEvent
        {
            ClaimId = claimId,
            At = now,
            Actor = name,
            Field = "status",
            Before = before,
            After = item.Status,
            Note = Truncate(body.Note, 2000),
        });
    }

    if (!string.IsNullOrWhiteSpace(body.Note))
        item.LastNote = Truncate(body.Note, 2000);

    item.UpdatedAt = now;

    db.AuditLog.Add(new AQ.Denials.Core.Domain.AuditLogEntry
    {
        At = now,
        Actor = name,
        Action = changed ? "worklist.status" : "worklist.note",
        EntityType = "WorkItem",
        EntityKey = claimId,
        Detail = changed ? $"{before} -> {item.Status}" : "note updated",
    });

    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    var after = (await worklist.BuildAsync(cancellationToken))
        .FirstOrDefault(r => string.Equals(r.ClaimId, claimId, StringComparison.Ordinal));

    return Results.Ok(new { claimId, status = item.Status, changed, row = after is null ? null : Shape(after) });
})
.WithName("SetWorklistStatus")
.WithOpenApi()
.RequireAuthorization(Policies.Worklist);

app.MapPost("/api/worklist/{claimId}/assign", async (
        string claimId, AssignRequest body, ClaimsPrincipal user, WorklistService worklist,
        AppDbContext db, CancellationToken cancellationToken) =>
{
    var (name, _) = Actor(user);

    var rows = await worklist.BuildAsync(cancellationToken);
    var row = rows.FirstOrDefault(r => string.Equals(r.ClaimId, claimId, StringComparison.Ordinal));
    if (row is null)
        return Results.NotFound(new { error = "not_an_open_denial", claimId });

    var now = DateTimeOffset.UtcNow;
    var item = await db.WorkItems
        .FirstOrDefaultAsync(w => w.ClaimId == claimId, cancellationToken);

    if (item is null)
    {
        item = new WorkItem
        {
            ClaimId = claimId,
            CreatedBy = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    var before = item.Assignee;
    var after = string.IsNullOrWhiteSpace(body.Assignee) ? null : Truncate(body.Assignee, 64);
    var changed = !string.Equals(before, after, StringComparison.Ordinal);

    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

    if (changed)
    {
        item.Assignee = after;
        db.WorkItemEvents.Add(new WorkItemEvent
        {
            ClaimId = claimId,
            At = now,
            Actor = name,
            Field = "assignee",
            Before = before,
            After = after,
            Note = Truncate(body.Note, 2000),
        });
    }

    item.UpdatedAt = now;

    db.AuditLog.Add(new AQ.Denials.Core.Domain.AuditLogEntry
    {
        At = now,
        Actor = name,
        Action = "worklist.assign",
        EntityType = "WorkItem",
        EntityKey = claimId,
        Detail = $"{before ?? "(unassigned)"} -> {after ?? "(unassigned)"}",
    });

    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    return Results.Ok(new { claimId, assignee = item.Assignee, changed });
})
.WithName("AssignWorklistItem")
.WithOpenApi()
.RequireAuthorization(Policies.Manage);

app.MapPost("/api/worklist/{claimId}/draft", async (
        string claimId, ClaimsPrincipal user, WorklistService worklist,
        CancellationToken cancellationToken) =>
{
    var (name, _) = Actor(user);
    var row = await worklist.DraftOneAsync(claimId, name, cancellationToken);

    return row is null
        ? Results.NotFound(new { error = "not_an_open_denial", claimId })
        : Results.Ok(new { claimId, produced = row.HasDraft, verdict = row.DraftVerdict,
                           reason = row.DraftReason, confidence = row.Confidence,
                           row = Shape(row) });
})
.WithName("DraftWorklistItem")
.WithOpenApi()
.RequireAuthorization(Policies.Worklist);

app.MapPost("/api/worklist/drafts", async (
        ClaimsPrincipal user, WorklistService worklist, AppDbContext db,
        int? limit, CancellationToken cancellationToken) =>
{
    var (name, _) = Actor(user);
    var rows = await worklist.BuildAsync(cancellationToken);

    // Oldest window first: if the run is cut short, what survives is the work that was closest
    // to expiring, not the work that happened to sort first by claim id.
    var targets = rows.Where(r => !r.HasDraft)
                       .OrderBy(r => r.DaysRemaining ?? int.MaxValue)
                       .ThenBy(r => r.ClaimId, StringComparer.Ordinal)
                       .Take(limit is > 0 ? limit.Value : int.MaxValue)
                       .Select(r => r.ClaimId)
                       .ToList();

    var produced = 0;
    var unavailable = 0;

    foreach (var claimId in targets)
    {
        var row = await worklist.DraftOneAsync(claimId, name, cancellationToken);
        if (row is null) continue;
        if (row.HasDraft) produced++; else unavailable++;
    }

    var all = await worklist.BuildAsync(cancellationToken);
    return Results.Ok(new
    {
        attempted = targets.Count,
        produced,
        unavailable,
        stillMissing = all.Count(r => !r.HasDraft),
        needsReview = all.Count(r => r.RequiresHumanReview),
        aiConfigured = all.FirstOrDefault()?.DraftVerdict is not null || produced > 0,
    });
})
.WithName("DraftAllWorklistItems")
.WithOpenApi()
.RequireAuthorization(Policies.Manage);

app.Run();

/// <summary>Who is calling, as the token says. The role is read from the server-side claim only.</summary>
static (string Name, string Role) Actor(ClaimsPrincipal user) =>
    (user.Identity?.Name ?? "unknown",
     user.FindFirstValue(ClaimTypes.Role) ?? string.Empty);

/// <summary>Bound to a length at the edge so nothing unbounded reaches an audit table.</summary>
static string? Truncate(string? value, int max) =>
    string.IsNullOrEmpty(value) ? value
    : value.Length <= max ? value
    : value[..max] + "…";

/// <summary>
/// The queue projection. One shape, used by list, detail and every mutation's response, so the
/// screen cannot show a field the moment after it has been changed to something else.
/// </summary>
static object Shape(WorklistRow r) => new
{
    r.ClaimId, r.PayerId, r.PayerName, r.Category, r.Team,
    r.PreventableAtPrebill, r.CoveredByLabeledSample, r.NextAction,
    r.Bucket, r.AnyDenialRouteOpen, r.DenialDate, r.AppealEnds, r.DaysRemaining,
    r.Charge, r.Status, r.Assignee, r.LastNote,
    r.Confidence, r.RequiresHumanReview, r.ConfidenceFactors,
    r.Priority, r.PriorityExplain,
    r.HasDraft, r.DraftBody, r.DraftVerdict, r.DraftReason, r.DraftAt,
};

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
