namespace AQ.Denials.Core.Domain;

/// <summary>
/// One imported remittance file. Two hashes are recorded because they answer different
/// questions: <see cref="FileSha256"/> is "are these bytes identical" and
/// <see cref="PayloadSha256"/> is "is this the same payment", the one that matters here.
/// </summary>
public class RemitFile
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 over the whole file, including the ISA/GS/ST envelope.</summary>
    public string FileSha256 { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 over every segment <b>outside</b> ISA/GS/ST/SE/GE/IEA — the normalised payment
    /// payload. Duplicate detection and idempotency key on this, never on <see cref="FileSha256"/>.
    /// </summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    /// <summary>Payer id taken from <c>REF*2U</c>.</summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>Check date from <c>DTM*405</c>, <c>yyyy-MM-dd</c>.</summary>
    public string CheckDate { get; set; } = string.Empty;

    /// <summary>File-level cash from <c>BPR02</c>, signed.</summary>
    public decimal BprTotal { get; set; }

    /// <summary>Trace number from <c>BPR02</c>'s paired <c>TRN</c>, when present.</summary>
    public string? TraceNumber { get; set; }

    /// <summary>True when this file's payload duplicates an earlier file; excluded from cash.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>File name of the original when <see cref="IsDuplicate"/> is true.</summary>
    public string? DuplicateOfFileName { get; set; }

    public int ClaimCount { get; set; }
    public List<RemitObservation> Observations { get; set; } = new();
}

/// <summary>
/// One <c>CLP</c> occurrence: a single adjudication event for a claim inside one file.
/// Ordered deterministically by <see cref="Seq"/> so "current state" never depends on the
/// order files happened to be read in.
/// </summary>
public class RemitObservation
{
    public int Id { get; set; }

    public int ClaimId { get; set; }
    public Claim? Claim { get; set; }

    public int RemitFileId { get; set; }
    public RemitFile? RemitFile { get; set; }

    /// <summary>
    /// Deterministic sequence: check date → file name → transaction-set index → segment index.
    /// Persisted at ingest, never recomputed at read time (a partial sort key would let history
    /// leak into "current state").
    /// </summary>
    public int Seq { get; set; }

    /// <summary>Payer id as stated by this transaction (<c>REF*2U</c>).</summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>Check date <c>DTM*405</c>, <c>yyyy-MM-dd</c>. Anchors every deadline.</summary>
    public string CheckDate { get; set; } = string.Empty;

    /// <summary><c>CLP01</c> exactly as received.</summary>
    public string RawClaimReference { get; set; } = string.Empty;

    /// <summary><c>CLP02</c> claim status code.</summary>
    public string StatusCode { get; set; } = string.Empty;

    /// <summary><c>CLP03</c> total claim charge, signed.</summary>
    public decimal SubmittedCharge { get; set; }

    /// <summary><c>CLP04</c> amount paid, signed.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary><c>CLP05</c> patient responsibility, signed.</summary>
    public decimal PatientResponsibility { get; set; }

    /// <summary>
    /// Claim-level natural key: payer + check/trace + the payer's own claim control number.
    /// Guards against partial overlaps that a whole-payload hash cannot see.
    /// </summary>
    public string NaturalKey { get; set; } = string.Empty;

    /// <summary>
    /// Remark codes (<c>LQ02</c>) at claim level, joined by <c>;</c>. Stored as one column rather
    /// than a child table because they are a read-only annotation: the CARC money lives in
    /// <see cref="Adjustments"/>, and that is what every calculation uses.
    /// </summary>
    public string RemarkCodes { get; set; } = string.Empty;

    public List<Adjustment> Adjustments { get; set; } = new();
    public List<ObservationService> Services { get; set; } = new();
}

/// <summary>An <c>SVC</c> line inside an observation.</summary>
public class ObservationService
{
    public int Id { get; set; }
    public int RemitObservationId { get; set; }
    public RemitObservation? RemitObservation { get; set; }

    /// <summary>Procedure code with any <c>XX</c> qualifier removed.</summary>
    public string ProcedureCode { get; set; } = string.Empty;

    public decimal Charge { get; set; }
    public decimal Paid { get; set; }

    /// <summary>Remark codes (<c>LQ02</c>) at service level, joined by <c>;</c>.</summary>
    public string RemarkCodes { get; set; } = string.Empty;

    public List<Adjustment> Adjustments { get; set; } = new();
}

/// <summary>
/// One signed claim adjustment group code amount from a <c>CAS</c> segment — claim-level when
/// <see cref="ObservationServiceId"/> is null, service-level otherwise.
/// </summary>
public class Adjustment
{
    public int Id { get; set; }

    public int RemitObservationId { get; set; }
    public RemitObservation? RemitObservation { get; set; }

    public int? ObservationServiceId { get; set; }
    public ObservationService? Service { get; set; }

    /// <summary>CO, PR, OA or PI. Meanings come from <c>claim_adjustment_group_codes.csv</c>.</summary>
    public string GroupCode { get; set; } = string.Empty;

    /// <summary>Claim Adjustment Reason Code, e.g. <c>11</c>, <c>B7</c>.</summary>
    public string Carc { get; set; } = string.Empty;
    /// <summary>
    /// True when this amount belongs to an <c>SVC</c> line rather than to the whole claim.
    /// </summary>
    /// <remarks>
    /// Checked against <b>either</b> the foreign key or the navigation, because they answer at
    /// different times: in memory, before a save, only <see cref="Service"/> is populated; after
    /// a load, only <see cref="ObservationServiceId"/> may be. Reading just one of them would
    /// quietly mis-file every service-level adjustment as claim-level until the first save.
    /// </remarks>
    public bool IsServiceLevel => ObservationServiceId is not null || Service is not null;

    /// <summary>Signed exactly as written in the file. Never flipped, never absolute-valued.</summary>
    public decimal Amount { get; set; }
}
