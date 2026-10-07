namespace AQ.Denials.Core;

/// <summary>
/// Machine-readable exception reason codes. Every row that enters the exceptions table must carry
/// exactly one of these, so an operator can filter by cause and a test can assert counts.
/// </summary>
public static class ExceptionReason
{
    /// <summary>Remit file whose normalised payment payload duplicates an already-imported file.</summary>
    public const string DuplicatePayload = "DUPLICATE_PAYLOAD";

    /// <summary>Same claim-level natural key seen in a different remit (partial overlap).</summary>
    public const string DuplicateClaimObservation = "DUPLICATE_CLAIM_OBSERVATION";

    /// <summary>Remit references a claim id that does not exist in the claim export.</summary>
    public const string OrphanRemit = "ORPHAN_REMIT";

    /// <summary>Worklog row whose claim reference cannot be canonicalised or is not exported.</summary>
    public const string UnmatchedWorklogRow = "UNMATCHED_WORKLOG_ROW";

    /// <summary>Worklog date where both components are ≤ 12, so the reading is undecidable.</summary>
    public const string AmbiguousDate = "AMBIGUOUS_DATE";

    /// <summary>Claim in the export with no remittance at all.</summary>
    public const string UnadjudicatedClaim = "UNADJUDICATED_CLAIM";

    /// <summary>An X12 segment that could not be parsed, or is structurally impossible.</summary>
    public const string MalformedSegment = "MALFORMED_SEGMENT";

    /// <summary>A CARC/RARC that is absent from <c>carc_rarc_reference.csv</c>.</summary>
    public const string UnmappedCarcRarc = "UNMAPPED_CARC_RARC";

    /// <summary>Claim id in a foreign namespace (not <c>GPP-2026-######</c>).</summary>
    public const string ForeignClaimNamespace = "FOREIGN_CLAIM_NAMESPACE";

    /// <summary>A required input file listed in the manifest is absent.</summary>
    public const string MissingInputFile = "MISSING_INPUT_FILE";

    /// <summary><c>BPR</c> does not equal the sum of <c>CLP04</c> for that file or payer.</summary>
    public const string ReconciliationMismatch = "RECONCILIATION_MISMATCH";

    /// <summary>A claim line's <c>CLP03</c> does not equal <c>CLP04 + Σ signed CAS</c>.</summary>
    public const string AdjustmentSumMismatch = "ADJUSTMENT_SUM_MISMATCH";

    /// <summary>Envelope (ISA/GS/ST … SE/GE/IEA) counts do not agree. Warning, not fatal.</summary>
    public const string EnvelopeDefect = "ENVELOPE_DEFECT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DuplicatePayload, DuplicateClaimObservation, OrphanRemit, UnmatchedWorklogRow,
        AmbiguousDate, UnadjudicatedClaim, MalformedSegment, UnmappedCarcRarc,
        ForeignClaimNamespace, MissingInputFile, ReconciliationMismatch,
        AdjustmentSumMismatch, EnvelopeDefect,
    };
}
