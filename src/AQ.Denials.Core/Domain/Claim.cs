namespace AQ.Denials.Core.Domain;

/// <summary>
/// Canonical claim: exactly one row per normalised claim id, holding identity plus every
/// remittance observation that ever happened to it. The claim export is the source of truth for
/// charge and clinical fields; the 835 is the source of truth for status and money.
/// </summary>
public class Claim
{
    public int Id { get; set; }

    /// <summary>Normalised id, always <c>GPP-2026-NNNNNN</c>. See <c>ClaimIdNormaliser</c>.</summary>
    public string ClaimId { get; set; } = string.Empty;

    public string PayerId { get; set; } = string.Empty;
    public string PayerName { get; set; } = string.Empty;

    /// <summary>PHI. Persisted because this is the billing system's own record; never logged.</summary>
    public string PatientFirst { get; set; } = string.Empty;

    /// <summary>PHI. Persisted, never logged.</summary>
    public string PatientLast { get; set; } = string.Empty;

    /// <summary>PHI. Persisted as <c>yyyy-MM-dd</c>, never logged.</summary>
    public string PatientDob { get; set; } = string.Empty;

    /// <summary>PHI. Persisted, never logged.</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Date of service, <c>yyyy-MM-dd</c>.</summary>
    public string Dos { get; set; } = string.Empty;

    /// <summary>Date submitted to the payer, <c>yyyy-MM-dd</c>.</summary>
    public string SubmittedDate { get; set; } = string.Empty;

    public string RenderingNpi { get; set; } = string.Empty;
    public string RenderingProvider { get; set; } = string.Empty;
    public string Facility { get; set; } = string.Empty;

    /// <summary>Place of service: 21 = hospital, 31 = nursing facility.</summary>
    public string Pos { get; set; } = string.Empty;

    public string? AuthNumber { get; set; }
    public string? CoderId { get; set; }

    /// <summary><c>Y</c>/<c>N</c> from the export.</summary>
    public string PrebillReviewed { get; set; } = string.Empty;

    /// <summary>Sum of <see cref="ClaimLine.Charge"/>. Positive.</summary>
    public decimal Charge { get; set; }

    /* ---- adjudication state, derived solely from the 835 ------------------ */

    /// <summary>False until any remittance observation exists for this claim.</summary>
    public bool Adjudicated { get; set; }

    /// <summary>
    /// Latest <c>CLP02</c> claim status code (1 = paid, 2 = reduced, 4 = denied,
    /// 22 = reversal/corrected …). Null while unadjudicated.
    /// </summary>
    public string? CurrentStatus { get; set; }

    /// <summary>Latest <c>CLP04</c> paid amount, signed.</summary>
    public decimal? PaidAmount { get; set; }

    /// <summary>Latest <c>CLP03</c> submitted charge as adjudicated, signed.</summary>
    public decimal? SubmittedCharge { get; set; }

    /// <summary>Check date (<c>DTM*405</c>) of the latest observation, <c>yyyy-MM-dd</c>.</summary>
    public string? LastCheckDate { get; set; }

    /// <summary>Money ever paid across all observations, signed (reversals reduce it).</summary>
    public decimal LifetimePaid { get; set; }

    public List<ClaimLine> Lines { get; set; } = new();
    public List<RemitObservation> Observations { get; set; } = new();
}

/// <summary>One line of the claim as exported by the billing system.</summary>
public class ClaimLine
{
    public int Id { get; set; }
    public int ClaimId { get; set; }
    public Claim? Claim { get; set; }

    public int LineNo { get; set; }
    public string Cpt { get; set; } = string.Empty;
    public string? Modifier { get; set; }
    public int Units { get; set; }

    /// <summary>Positive amount billed for this line.</summary>
    public decimal Charge { get; set; }

    public string? Dx1 { get; set; }
    public string? Dx2 { get; set; }
    public string? Dx3 { get; set; }
    public string? Dx4 { get; set; }
}
