using AQ.Denials.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AQ.Denials.Api.Data;

/// <summary>
/// Maps the canonical model onto PostgreSQL. Two rules run through the whole mapping and neither
/// is negotiable:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description><b>Money is <c>numeric(18,2)</c>.</b> No column carrying an amount is ever a float — a
/// half-cent rounding artefact in a denial report is a wrong number, not a cosmetic one.</description></item>
/// <item><description><b>Identifiers that reach a query are parameters.</b> Every lookup is LINQ against this
/// context; there is no interpolated SQL anywhere in the API, so a claim reference of
/// <c>' OR 1=1 --</c> is searched for as a value rather than executed as a predicate.</description></item>
/// </list>
/// <para>
/// Dates are stored as text in <c>yyyy-MM-dd</c>. They are compared ordinally in both C# and SQL,
/// which gives the same answer everywhere; a <c>timestamp</c> column would introduce a time zone
/// into a value that has none.
/// </para>
/// </remarks>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();
    public DbSet<RemitFile> RemitFiles => Set<RemitFile>();
    public DbSet<RemitObservation> RemitObservations => Set<RemitObservation>();
    public DbSet<ObservationService> ObservationServices => Set<ObservationService>();
    public DbSet<Adjustment> Adjustments => Set<Adjustment>();
    public DbSet<WorklogEntry> WorklogEntries => Set<WorklogEntry>();
    public DbSet<ExceptionRow> ExceptionRows => Set<ExceptionRow>();
    public DbSet<IngestRun> IngestRuns => Set<IngestRun>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemEvent> WorkItemEvents => Set<WorkItemEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var claim = model.Entity<Claim>();
        claim.HasKey(c => c.Id);
        claim.HasIndex(c => c.ClaimId).IsUnique();
        claim.Property(c => c.ClaimId).HasMaxLength(32);
        claim.Property(c => c.PayerId).HasMaxLength(16);
        claim.Property(c => c.Charge).HasColumnType("numeric(18,2)");
        claim.Property(c => c.LifetimePaid).HasColumnType("numeric(18,2)");
        claim.Property(c => c.PaidAmount).HasColumnType("numeric(18,2)");
        claim.Property(c => c.SubmittedCharge).HasColumnType("numeric(18,2)");
        claim.Property(c => c.Dos).HasMaxLength(10);
        claim.Property(c => c.SubmittedDate).HasMaxLength(10);
        claim.Property(c => c.LastCheckDate).HasMaxLength(10);
        claim.Property(c => c.CurrentStatus).HasMaxLength(4);
        claim.HasMany(c => c.Lines).WithOne(l => l.Claim!)
             .HasForeignKey(l => l.ClaimId).OnDelete(DeleteBehavior.Cascade);
        claim.HasMany(c => c.Observations).WithOne(o => o.Claim!)
             .HasForeignKey(o => o.ClaimId).OnDelete(DeleteBehavior.Cascade);

        var line = model.Entity<ClaimLine>();
        line.HasKey(l => l.Id);
        line.Property(l => l.Charge).HasColumnType("numeric(18,2)");
        line.Property(l => l.Cpt).HasMaxLength(16);

        var file = model.Entity<RemitFile>();
        file.HasKey(f => f.Id);
        file.HasIndex(f => f.FileName).IsUnique();
        file.HasIndex(f => f.PayloadSha256);
        file.Property(f => f.PayloadSha256).HasMaxLength(64);
        file.Property(f => f.FileSha256).HasMaxLength(64);
        file.Property(f => f.PayerId).HasMaxLength(16);
        file.Property(f => f.CheckDate).HasMaxLength(10);
        file.Property(f => f.BprTotal).HasColumnType("numeric(18,2)");
        file.Property(f => f.IsDuplicate).HasDefaultValue(false);
        file.HasMany(f => f.Observations).WithOne(o => o.RemitFile!)
            .HasForeignKey(o => o.RemitFileId).OnDelete(DeleteBehavior.Cascade);

        var observation = model.Entity<RemitObservation>();
        observation.HasKey(o => o.Id);
        observation.HasIndex(o => o.NaturalKey);
        observation.HasIndex(o => new { o.ClaimId, o.Seq });
        observation.Property(o => o.PayerId).HasMaxLength(16);
        observation.Property(o => o.CheckDate).HasMaxLength(10);
        observation.Property(o => o.StatusCode).HasMaxLength(4);
        observation.Property(o => o.NaturalKey).HasMaxLength(256);
        observation.Property(o => o.SubmittedCharge).HasColumnType("numeric(18,2)");
        observation.Property(o => o.PaidAmount).HasColumnType("numeric(18,2)");
        observation.Property(o => o.PatientResponsibility).HasColumnType("numeric(18,2)");
        observation.HasMany(o => o.Services).WithOne(s => s.RemitObservation!)
                   .HasForeignKey(s => s.RemitObservationId).OnDelete(DeleteBehavior.Cascade);

        var service = model.Entity<ObservationService>();
        service.HasKey(s => s.Id);
        service.Property(s => s.Charge).HasColumnType("numeric(18,2)");
        service.Property(s => s.Paid).HasColumnType("numeric(18,2)");
        service.Property(s => s.ProcedureCode).HasMaxLength(16);

        var adjustment = model.Entity<Adjustment>();
        adjustment.HasKey(a => a.Id);
        adjustment.Ignore(a => a.IsServiceLevel);      // computed, not a column
        adjustment.Property(a => a.Amount).HasColumnType("numeric(18,2)");
        adjustment.Property(a => a.GroupCode).HasMaxLength(4);
        adjustment.Property(a => a.Carc).HasMaxLength(16);
        adjustment.HasOne(a => a.RemitObservation).WithMany(o => o.Adjustments)
                   .HasForeignKey(a => a.RemitObservationId).OnDelete(DeleteBehavior.Cascade);
        adjustment.HasOne(a => a.Service).WithMany(s => s.Adjustments)
                   .HasForeignKey(a => a.ObservationServiceId)
                   .OnDelete(DeleteBehavior.Cascade);

        var worklog = model.Entity<WorklogEntry>();
        worklog.HasKey(w => w.Id);
        worklog.HasIndex(w => w.RowNumber).IsUnique();
        worklog.Property(w => w.AmountAsLogged).HasColumnType("numeric(18,2)");
        worklog.Ignore(w => w.PatientDisplay);   // PHI: never written to this database

        var exceptionRow = model.Entity<ExceptionRow>();
        exceptionRow.HasKey(e => e.Id);
        exceptionRow.HasIndex(e => e.ReasonCode);
        exceptionRow.HasIndex(e => e.ClaimId);
        exceptionRow.Property(e => e.ReasonCode).HasMaxLength(48);
        exceptionRow.Property(e => e.ClaimId).HasMaxLength(32);

        var run = model.Entity<IngestRun>();
        run.HasKey(r => r.Id);
        run.HasIndex(r => r.RunGuid).IsUnique();
        run.HasIndex(r => r.FullStateChecksum);

        var audit = model.Entity<AuditLogEntry>();
        audit.HasKey(a => a.Id);
        audit.HasIndex(a => a.At);
        audit.Property(a => a.Action).HasMaxLength(64);
        audit.Property(a => a.EntityType).HasMaxLength(64);

        // --- worklist ---------------------------------------------------------------
        // No FK to Claim, deliberately: /api/ingest truncates the claim tables on every
        // re-run and would otherwise cascade-delete the team's own history (see WorkItem).
        // ClaimId is a natural key here, so a claim that leaves the pack shows up as an
        // orphan to be reviewed rather than vanishing along with its audit trail.
        // Events are keyed by claim id with **no foreign key to WorkItem**, deliberately: a
        // cascade would delete the audit trail the moment the work item it describes were
        // removed, which is the one thing an audit trail exists to survive. Orphan events are
        // the correct outcome — they record what happened to a claim regardless of whether the
        // item is still there.
        var work = model.Entity<WorkItem>();
        work.HasKey(w => w.Id);
        work.HasIndex(w => w.ClaimId).IsUnique();
        work.Property(w => w.ClaimId).HasMaxLength(32);
        work.Property(w => w.Status).HasMaxLength(32);
        work.Property(w => w.Assignee).HasMaxLength(64);
        work.Property(w => w.CreatedBy).HasMaxLength(64);
        work.Property(w => w.LastNote).HasMaxLength(2000);

        var evt = model.Entity<WorkItemEvent>();
        evt.HasKey(e => e.Id);
        evt.HasIndex(e => new { e.ClaimId, e.At });
        evt.Property(e => e.ClaimId).HasMaxLength(32);
        evt.Property(e => e.Field).HasMaxLength(32);
        evt.Property(e => e.Actor).HasMaxLength(64);
        evt.Property(e => e.Note).HasMaxLength(2000);
        // before/after are bounded on purpose: they are shown in a list, and an unbounded
        // column invites pasting a patient's whole story into an audit table.
        evt.Property(e => e.Before).HasMaxLength(256);
        evt.Property(e => e.After).HasMaxLength(256);
    }
}
