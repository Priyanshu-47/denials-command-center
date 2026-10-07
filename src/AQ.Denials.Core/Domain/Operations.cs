namespace AQ.Denials.Core.Domain;

/// <summary>
/// A manual worklog row, merged as <b>history only</b>. The 835 always wins on status and
/// money; where the two disagree the disagreement is recorded as an exception row rather than
/// silently reconciled.
/// </summary>
public class WorklogEntry
{
    public int Id { get; set; }

    /// <summary>1-based row number in the spreadsheet, for "which row do I go and look at".</summary>
    public int RowNumber { get; set; }

    /// <summary>The claim reference exactly as typed in the tracker (may have odd casing/padding).</summary>
    public string RawClaimReference { get; set; } = string.Empty;

    /// <summary>Normalised claim id, null when the reference cannot be canonicalised.</summary>
    public string? ClaimId { get; set; }

    /// <summary>Payer name as typed in the tracker.</summary>
    public string PayerName { get; set; } = string.Empty;

    /// <summary>
    /// Raw <c>Date Logged</c> string, preserved verbatim. Never overwritten with a guess.
    /// </summary>
    public string DateLoggedRaw { get; set; } = string.Empty;

    /// <summary>US reading (MM/DD/yyyy) of <see cref="DateLoggedRaw"/>, null when impossible.</summary>
    public string? DateLoggedMdy { get; set; }

    /// <summary>International reading (DD/MM/yyyy) of <see cref="DateLoggedRaw"/>, null when impossible.</summary>
    public string? DateLoggedDmy { get; set; }

    /// <summary>
    /// True when both readings are calendar-valid and ≤ today, so the value is genuinely
    /// undecidable. The UI must show this as "date uncertain".
    /// </summary>
    public bool DateAmbiguous { get; set; }

    /// <summary>
    /// The earlier of the two readings — used for priority only, never written as canonical
    /// truth, and always rendered with an uncertainty flag.
    /// </summary>
    public string? DateLoggedConservative { get; set; }

    /// <summary>True when the other reading falls after today and is therefore impossible.</summary>
    public bool DateResolvedByFutureTest { get; set; }

    /// <summary>Amount as recorded by the team. Not trusted over the 835.</summary>
    public decimal? AmountAsLogged { get; set; }

    public string Owner { get; set; } = string.Empty;
    public string StatusRaw { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    /// <summary>PHI. Never logged, never returned by an endpoint, never written to a report.</summary>
    public string PatientDisplay { get; set; } = string.Empty;
}

/// <summary>One ingestion run. Everything written is attributed to a run id.</summary>
public class IngestRun
{
    public int Id { get; set; }
    public Guid RunGuid { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>SHA-256 over the canonical serialisation of the full resulting state.</summary>
    public string? FullStateChecksum { get; set; }

    /// <summary>Aggregate counters, so <c>rows_in == matched + exceptions</c> is auditable.</summary>
    public int RowsIn { get; set; }
    public int RowsMatched { get; set; }
    public int RowsExcepted { get; set; }

    public bool Succeeded { get; set; }
    public string? FailureMessage { get; set; }
}

/// <summary>Any row that could not (or should not) be folded into canonical state.</summary>
public class ExceptionRow
{
    public int Id { get; set; }
    public int? IngestRunId { get; set; }
    public IngestRun? Run { get; set; }

    /// <summary>One of <see cref="ExceptionReason"/>. Filter key for the exceptions view.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Source file the problem came from, e.g. <c>era_2026Q2.835</c>.</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Human-readable explanation. Claim ids and codes only — never patient identifiers.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Claim id involved, when known.</summary>
    public string? ClaimId { get; set; }

    /// <summary>
    /// When this row documents a duplicate, the identifier of the surviving original
    /// (a file name for <c>DUPLICATE_PAYLOAD</c>, a natural key for claim-level duplicates).
    /// </summary>
    public string? OriginalRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Append-only audit trail. Deliberately holds no patient name, DOB or member id — claim ids,
/// NPIs, run ids and codes only.
/// </summary>
public class AuditLogEntry
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityKey { get; set; }
    public string? Detail { get; set; }
    public Guid? RunGuid { get; set; }
}
