namespace AQ.Denials.Core.Domain;

/// <summary>
/// Where an open denial sits in the team's workflow.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the only strings the queue accepts.</b> A status is a shared vocabulary between a
/// person's afternoon and a database column, so it is enumerated rather than free text: a
/// specialist typing "nearly done" creates a state the manager's filter cannot select, and the
/// item silently disappears from every view except the one that created it.
/// </para>
/// <para>
/// <see cref="Priority.IsClosed"/> reads this list too, so "is this finished?" has exactly one
/// answer whether it is being asked by the ordering or by a filter.
/// </para>
/// </remarks>
public static class WorkStatus
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string AwaitingPayer = "awaiting_payer";
    public const string Resolved = "resolved";
    public const string WrittenOff = "written_off";

    public static readonly IReadOnlyList<string> All =
        [Open, InProgress, AwaitingPayer, Resolved, WrittenOff];

    public static bool IsValid(string status) =>
        All.Contains(status, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A specialist's or manager's unit of work, keyed to a claim by its <b>natural key</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately no foreign key to <c>Claims`.</b> <c>POST /api/ingest</c> truncates and rebuilds
/// the claim tables whenever the data pack changes, and a cascade would delete every status,
/// assignment and note the team had entered — user-entered history would be destroyed by a routine
/// re-ingest. Storing the claim id as text means the work survives, and a claim that disappears
/// from the pack surfaces as an orphan to be looked at rather than as nothing.
/// </para>
/// <para>
/// <b>Only mutable human state is stored.</b> Category, team, preventability, next action,
/// confidence and priority are recomputed from the canonical pipeline on every read (D16), so the
/// queue can never show a routing decision that disagrees with the reconciliation the numbers came
/// from, and re-ingesting cannot leave stale analysis behind.
/// </para>
/// </remarks>
public class WorkItem
{
    public int Id { get; set; }

    /// <summary>Canonical claim id. The natural key, and the only link to the claim.</summary>
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>One of <see cref="WorkStatus"/>. Defaults to open when nothing has been entered.</summary>
    public string Status { get; set; } = WorkStatus.Open;

    /// <summary>Who is working it. Null means unassigned — visible to a manager, not a queue.</summary>
    public string? Assignee { get; set; }

    /// <summary>Free-text note from the last change. Claim ids and codes only — never a name or DOB.</summary>
    public string? LastNote { get; set; }

    /// <summary>
    /// The generated appeal/correction note, after this system re-validated it. Null when no draft
    /// has been requested yet, or when the AI service was unavailable. Never contains patient
    /// identifiers: the prompt carries structured fields only (D31), so what is stored here was
    /// built without them.
    /// </summary>
    public string? DraftBody { get; set; }

    /// <summary>
    /// The citation verdict as text (a value of <c>CitationVerdict</c> in AQ.Denials.Rules), or
    /// null if no draft has been run. Stored as the name so this assembly stays free of a
    /// dependency on the rules that produce it.
    /// </summary>
    public string? DraftVerdict { get; set; }

    /// <summary>Why there is no draft, or what a reviewer must know before using one.</summary>
    public string? DraftUnavailableReason { get; set; }

    public DateTimeOffset? DraftAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who created the row, so an item with no events still has an owner of record.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    // Deliberately no `Events` navigation. The history is queried by claim id (see WorkItemEvent):
    // an EF navigation would pair by convention and then cascade, and an audit trail that goes
    // away with the record it describes is not an audit trail.
}

/// <summary>
/// One recorded change: <b>who, what, when, before and after</b>.
/// </summary>
/// <remarks>
/// Written in the same transaction as the change itself. An audit trail that is written *after*
/// the commit has a window in which the change happened and nothing says so — and an audit trail
/// with an "after" but no "before" can tell you what the system says now, never what it used to
/// say, which is the half a dispute is actually about.
/// </remarks>
public class WorkItemEvent
{
    public int Id { get; set; }

    public string ClaimId { get; set; } = string.Empty;

    /// <summary>When. Server clock, never a client-supplied timestamp.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The authenticated name behind the token — not a role, a person.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Which field moved: <c>status</c>, <c>assignee</c> or <c>note</c>.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Value before the change. Null for the first assignment of a field.</summary>
    public string? Before { get; set; }

    /// <summary>Value after the change.</summary>
    public string? After { get; set; }

    public string? Note { get; set; }
}
