using AQ.Denials.Api.Data;
using AQ.Denials.Api.State;
using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Llm;
using AQ.Denials.Rules;
using AQ.Denials.Rules.Analysis;
using AQ.Denials.Rules.Policy;
using Microsoft.EntityFrameworkCore;

namespace AQ.Denials.Api.Worklist;

/// <summary>
/// Reference data read from the pack once and kept. Parsing five CSVs per request would make the
/// queue's latency a function of the filesystem, and a second parse is a second chance for two
/// reads to disagree.
/// </summary>
public sealed class ReferenceDataService
{
    private readonly Lazy<IReadOnlyList<PayerRule>> _rules;
    private readonly Lazy<PolicyLibrary> _policies;

    public ReferenceDataService(IConfiguration configuration)
    {
        var dir = configuration["DATA_DIR"] ?? DataPack.DefaultDataDir;

        _rules = new Lazy<IReadOnlyList<PayerRule>>(() =>
            ReferenceDataReader.ReadPayerRules(DataPack.Read(dir).PayerRulesCsv));

        _policies = new Lazy<PolicyLibrary>(() =>
            ReferenceDataReader.BuildPolicyLibrary(DataPack.Read(dir).PolicyDirectory));
    }

    public IReadOnlyList<PayerRule> PayerRules => _rules.Value;

    public PolicyLibrary Policies => _policies.Value;

    public PayerWindows Windows => ReferenceDataReader.BuildPayerWindows(PayerRules);

    public IReadOnlyDictionary<string, string> PayerNames =>
        PayerRules.ToDictionary(r => r.PayerId, r => r.PayerName, StringComparer.Ordinal);
}

/// <summary>Body of <c>POST /api/worklist/{claimId}/status</c>.</summary>
public sealed record StatusRequest(string? Status, string? Note);

/// <summary>Body of <c>POST /api/worklist/{claimId}/assign</c>. A blank assignee unassigns.</summary>
public sealed record AssignRequest(string? Assignee, string? Note);

/// <summary>One row of the queue, fully computed.</summary>
public sealed record WorklistRow(
    string ClaimId,
    string PayerId,
    string PayerName,
    string Category,
    string Team,
    bool? PreventableAtPrebill,
    bool CoveredByLabeledSample,
    string NextAction,
    string Bucket,
    bool AnyDenialRouteOpen,
    string DenialDate,
    string AppealEnds,
    int? DaysRemaining,
    decimal Charge,
    string Status,
    string? Assignee,
    string? LastNote,
    int Confidence,
    bool RequiresHumanReview,
    IReadOnlyList<string> ConfidenceFactors,
    int Priority,
    string PriorityExplain,
    bool HasDraft,
    string? DraftBody,
    string? DraftVerdict,
    string? DraftReason,
    DateTimeOffset? DraftAt);

/// <summary>
/// Builds the queue: the canonical analysis for every open denial, merged with whatever the team
/// has recorded against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split is the point.</b> Category, team, preventability, next action, bucket, deadline,
/// confidence and priority are recomputed from the pure pipeline on every read; only status,
/// assignee, notes and generated drafts come from the database. So the queue can never show a
/// routing decision that disagrees with the reconciliation the money came from, and re-running
/// ingestion cannot leave stale analysis sitting next to fresh figures.
/// </para>
/// <para>
/// <b>Nothing here needs the model.</b> Drafts are generated on demand and cached; a queue with
/// no AI configured is identical except that every <c>HasDraft</c> is false and confidence says
/// why. That is the brief's "must still work (degraded)" requirement, met by construction rather
/// than by an error path.
/// </para>
/// </remarks>
public sealed class WorklistService
{
    /// <summary>The brief fixes the clock. Every window in this system is measured from here.</summary>
    public const string Today = "2026-09-30";

    private readonly IStateProvider _state;
    private readonly ReferenceDataService _reference;
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;

    public WorklistService(IStateProvider state, ReferenceDataService reference, AppDbContext db, ILlmClient llm)
    {
        _state = state;
        _reference = reference;
        _db = db;
        _llm = llm;
    }

    private static IReadOnlyList<string> Carcs(RemitObservation observation) =>
        observation.Adjustments
                   .Select(a => a.Carc)
                   .Where(c => c.Length > 0)
                   .Distinct(StringComparer.Ordinal)
                   .ToList();

    /// <summary>All open denials with their analysis, in queue order.</summary>
    public async Task<IReadOnlyList<WorklistRow>> BuildAsync(CancellationToken cancellationToken)
    {
        var outcome = _state.Current;
        var windows = _reference.Windows;
        var names = _reference.PayerNames;

        var denied = outcome.State.Claims
            .Where(c => c.Observations.Count > 0 && c.CurrentStatus == "4")
            .ToList();

        var subjects = denied.Select(c => new RecoveryAssessment.Subject(
            ClaimId: c.ClaimId,
            PayerId: c.PayerId,
            DateOfService: c.Dos,
            Charge: c.Charge,
            DenialDate: c.Observations[^1].CheckDate,
            Carcs: Carcs(c.Observations[^1])));

        var assessments = RecoveryAssessment.Assess(subjects, windows, Today);
        var byClaim = assessments.ToDictionary(a => a.ClaimId, StringComparer.Ordinal);

        var work = await _db.WorkItems.AsNoTracking()
            .ToDictionaryAsync(w => w.ClaimId, StringComparer.Ordinal, cancellationToken);

        var rows = new List<WorklistRow>(denied.Count);

        foreach (var claim in denied)
        {
            if (!byClaim.TryGetValue(claim.ClaimId, out var assessment))
                continue;   // cannot happen: Assess covers exactly the list it was given

            rows.Add(Build(claim, assessment, work.GetValueOrDefault(claim.ClaimId), names));
        }

        return rows.OrderByDescending(r => r.Priority)
                   .ThenBy(r => r.ClaimId, StringComparer.Ordinal)
                   .ToList();
    }

    private static WorklistRow Build(
        Claim claim,
        OpenDenialAssessment assessment,
        WorkItem? stored,
        IReadOnlyDictionary<string, string> names)
    {
        var carcs = Carcs(claim.Observations[^1]);
        var category = DenialCategory.Of(carcs, claim.PayerId, claim.Dos);
        var details = DenialCategory.OutcomeFor(category);

        var days = DaysBetween(Today, assessment.AppealEnds);

        // Draft state, if the team has generated one. Absent means "not run yet", which is not
        // the same as "failed" — both score as no draft, but only the second carries a reason.
        var verdict = stored?.DraftVerdict is { } text && Enum.TryParse<CitationVerdict>(text, out var v)
            ? v
            : CitationVerdict.NotAssessed;
        var draftProduced = !string.IsNullOrWhiteSpace(stored?.DraftBody);

        var confidence = Confidence.Evaluate(
            coveredByLabeledSample: details.CoveredByLabeledSample,
            citation: verdict,
            daysUntilDeadline: days,
            draftProduced: draftProduced,
            preventableEstablished: details.PreventableAtPrebill.HasValue);

        var status = stored?.Status ?? WorkStatus.Open;
        var priority = Priority.Compute(
            assessment.Bucket, days, claim.Charge,
            confidence.RequiresHumanReview, details.PreventableAtPrebill, status);

        return new WorklistRow(
            ClaimId: claim.ClaimId,
            PayerId: claim.PayerId,
            PayerName: names.GetValueOrDefault(claim.PayerId, claim.PayerId),
            Category: category,
            Team: details.Team,
            PreventableAtPrebill: details.PreventableAtPrebill,
            CoveredByLabeledSample: details.CoveredByLabeledSample,
            NextAction: details.NextAction,
            Bucket: assessment.Bucket,
            AnyDenialRouteOpen: assessment.AnyDenialRouteOpen,
            DenialDate: assessment.DenialDate,
            AppealEnds: assessment.AppealEnds,
            DaysRemaining: days,
            Charge: claim.Charge,
            Status: status,
            Assignee: stored?.Assignee,
            LastNote: stored?.LastNote,
            Confidence: confidence.Score,
            RequiresHumanReview: confidence.RequiresHumanReview,
            ConfidenceFactors: confidence.Factors,
            Priority: priority.Score,
            PriorityExplain: priority.Explain(),
            HasDraft: draftProduced,
            DraftBody: draftProduced ? stored!.DraftBody : null,
            DraftVerdict: stored?.DraftVerdict,
            DraftReason: stored?.DraftUnavailableReason,
            DraftAt: stored?.DraftAt);
    }

    /// <summary>Whole days from <paramref name="from"/> to <paramref name="to"/>; null if unreadable.</summary>
    public static int? DaysBetween(string from, string to) =>
        DateTime.TryParse(from, out var a) && DateTime.TryParse(to, out var b)
            ? (b - a).Days
            : null;

    /// <summary>
    /// Generate (or regenerate) the draft for one claim and cache it, so the analysis survives a
    /// page reload and a model outage does not have to be re-experienced to be known about.
    /// </summary>
    /// <returns>The row as it now stands, or <c>null</c> when the claim is not an open denial.</returns>
    public async Task<WorklistRow?> DraftOneAsync(
        string claimId, string actor, CancellationToken cancellationToken)
    {
        // Read first: this endpoint must not become a way to create work items for claims that
        // are not open, and it must 404 rather than invent a queue row.
        var before = await BuildAsync(cancellationToken);
        if (before.All(r => r.ClaimId != claimId)) return null;

        var outcome = _state.Current;
        var claim = outcome.State.Claims.First(c => c.ClaimId == claimId);
        var carcs = Carcs(claim.Observations[^1]);
        var template = before.First(r => r.ClaimId == claimId);

        var draft = new DraftInput(
            ClaimId: claim.ClaimId,
            PayerName: template.PayerName,
            PayerId: claim.PayerId,
            Category: template.Category,
            Carcs: carcs,
            DeniedAmount: claim.Charge,
            NextAction: template.NextAction,
            DaysRemaining: template.DaysRemaining);

        var result = await AppealDrafting.DraftAsync(_llm, _reference.Policies, draft, cancellationToken);

        var item = await _db.WorkItems
            .FirstOrDefaultAsync(w => w.ClaimId == claimId, cancellationToken);

        if (item is null)
        {
            item = new WorkItem
            {
                ClaimId = claimId,
                Status = WorkStatus.Open,
                CreatedBy = actor,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _db.WorkItems.Add(item);
        }

        item.DraftBody = result.Body;
        item.DraftVerdict = result.Verdict.ToString();
        item.DraftUnavailableReason = result.UnavailableReason;
        item.DraftAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        // Rebuild rather than patching the record: confidence and priority both depend on the
        // draft, and two code paths computing them is how the two end up disagreeing.
        var after = await BuildAsync(cancellationToken);
        return after.FirstOrDefault(r => r.ClaimId == claimId);
    }
}
