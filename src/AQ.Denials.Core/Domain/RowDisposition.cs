namespace AQ.Denials.Core.Domain;

/// <summary>
/// What happened to one input row. Every row read from every source gets exactly one of these —
/// that is what makes <c>rows_in = matched + exceptions</c> an identity rather than a slogan.
/// </summary>
/// <remarks>
/// <para>
/// <b>One disposition per row, carrying one primary reason.</b> A row can have several problems
/// (a worklog row whose reference is foreign <i>and</i> whose date is ambiguous); the primary
/// reason goes in <see cref="ReasonCode"/> and every reason found goes in <see cref="Detail"/>.
/// Allowing one row to raise two exception rows would break the conservation identity, and an
/// identity that is only usually true cannot be asserted by a test.
/// </para>
/// <para>
/// <b>Which rows count.</b> By source and entity: claim-export lines, worklog rows, remittance
/// files, and remittance observations taken from files that were actually imported. A rejected
/// (duplicate) file contributes its file row only — its observations were never decision units.
/// </para>
/// </remarks>
public sealed record RowDisposition
{
    /// <summary>Source file the row came from, e.g. <c>claims_export.csv</c>.</summary>
    public required string SourceFile { get; init; }

    /// <summary>Entity the row belongs to: <c>claim_line</c>, <c>worklog_row</c>, <c>remit_file</c>, <c>remit_observation</c>.</summary>
    public required string Entity { get; init; }

    /// <summary>1-based row number within the source, for "which line do I go and look at".</summary>
    public required int RowNumber { get; init; }

    /// <summary>True when this row needs an operator (or at least an engineer) to look at it.</summary>
    public required bool IsProblem { get; init; }

    /// <summary>One of <see cref="ExceptionReason"/>; null for a clean row.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Plain-language explanation. Claim ids and codes only — never a patient identifier.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Claim involved, when known.</summary>
    public string? ClaimId { get; init; }

    /// <summary>For duplicates, the file name or natural key that survived.</summary>
    public string? OriginalRef { get; init; }

    /// <summary>The row as a stored exception row, or null when clean.</summary>
    public ExceptionRow? ToExceptionRow(DateTimeOffset at) => !IsProblem ? null : new ExceptionRow
    {
        ReasonCode = ReasonCode ?? ExceptionReason.ReconciliationMismatch,
        SourceFile = SourceFile,
        Detail = Detail,
        ClaimId = ClaimId,
        OriginalRef = OriginalRef,
        CreatedAt = at,
    };
}
