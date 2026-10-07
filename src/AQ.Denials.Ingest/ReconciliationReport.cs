using System.Globalization;
using System.Text;
using AQ.Denials.Core;

namespace AQ.Denials.Ingest;

/// <summary>
/// Payer/file cash reconciliation: what the remittance file says was paid, what the system holds,
/// and whether the difference is zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciliation happens at transaction level, then rolls up.</b> One file carries 7–9
/// transaction sets, each with its own <c>BPR</c>, its own check date and often its own payer —
/// <c>era_2026Q1.835</c> pays CSA77, NS401, SMP12 and MRD55 in one interchange. A file-level
/// "BPR" is therefore the sum of its transaction <c>BPR</c>s, and a payer-level figure is the sum
/// of that payer's transactions across files.
/// </para>
/// <para>
/// <b>Paid is measured over every <c>CLP</c> the file contains</b>, including ones that cannot be
/// linked to a claim in the export. Reconciling only the linked subset would make a file with an
/// unknown claim on it look short by exactly that amount and hide the orphan behind an "explained
/// difference". The two figures are reported separately: <see cref="CashExcludingDuplicate"/> ties
/// to <c>BPR</c>, and <see cref="OrphanObservationCount"/> says how many payment events nobody in
/// the export can account for.
/// </para>
/// <para>
/// Every figure here is <c>decimal</c>. No difference is ever allowed to be "close enough":
/// <see cref="AssertZeroDifferences"/> throws on the first non-zero cent.
/// </para>
/// </remarks>
public sealed class ReconciliationReport
{
    public sealed record FileLine(
        string FileName,
        bool IsDuplicate,
        string? DuplicateOf,
        int Transactions,
        decimal BprTotal,
        decimal PaidSum,
        int ObservationCount)
    {
        public decimal Difference => BprTotal - PaidSum;
    }

    public sealed record PayerLine(
        string PayerId,
        int Transactions,
        decimal BprTotal,
        decimal PaidSum,
        int ObservationCount)
    {
        public decimal Difference => BprTotal - PaidSum;
    }

    /// <summary>One adjustment group (CO/PR/OA/PI) summed over every persisted adjustment.</summary>
    public sealed record GroupLine(string GroupCode, string Meaning, int Count, decimal Amount);

    public IReadOnlyList<FileLine> Files { get; init; } = Array.Empty<FileLine>();
    public IReadOnlyList<PayerLine> Payers { get; init; } = Array.Empty<PayerLine>();

    /// <summary>
    /// Adjustment groups over <b>every</b> payment event in an imported file, whether or not the
    /// claim behind it exists in the export.
    /// </summary>
    /// <remarks>
    /// Scope matters here, and getting it wrong is invisible unless you go looking: summing only
    /// the adjustments on claims we could link gave CO $108,356.80, which is $159.60 short of the
    /// $108,516.40 actually remitted — exactly the three <c>BHC-2026-…</c> payment events for
    /// claims in another billing namespace. Cash counts them, so adjustments must too, or the two
    /// halves of the same reconciliation describe different populations. The unattributable part
    /// is broken out in <see cref="UnattributedAdjustmentGroups"/> rather than hidden.
    /// </remarks>
    public IReadOnlyList<GroupLine> AdjustmentGroups { get; init; } = Array.Empty<GroupLine>();

    /// <summary>Adjustment groups on payment events that no claim in the export can account for.</summary>
    public IReadOnlyList<GroupLine> UnattributedAdjustmentGroups { get; init; } = Array.Empty<GroupLine>();

    /// <summary>Σ <c>BPR</c> over every file, duplicates included.</summary>
    public decimal CashIncludingDuplicate { get; init; }

    /// <summary>Σ <c>BPR</c> over imported files only. The number of dollars actually received.</summary>
    public decimal CashExcludingDuplicate { get; init; }

    /// <summary>Cash a duplicate resend would have double-counted had it been accepted.</summary>
    public decimal DuplicateCashExcluded { get; init; }

    public int DuplicateFiles { get; init; }

    /* ---- book totals -------------------------------------------------------- */

    public int TotalClaims { get; init; }
    public decimal TotalClaimCharge { get; init; }

    public int AdjudicatedClaims { get; init; }
    public decimal AdjudicatedCharge { get; init; }

    /// <summary>Claims in the export with no remittance at all. They are not denials.</summary>
    public int UnadjudicatedClaims { get; init; }
    public decimal UnadjudicatedCharge { get; init; }

    /// <summary>Claims whose latest status is <c>4</c> (denied).</summary>
    public int DeniedClaims { get; init; }
    public decimal DeniedCharge { get; init; }

    /// <summary>Payment events received for claims that are not in the export.</summary>
    public int OrphanObservationCount { get; init; }

    /// <summary>Payment events refused because their natural key was already seen in another file.</summary>
    public int DuplicateObservationCount { get; init; }

    /// <summary>Claims where the export charge and the remitted <c>CLP03</c> disagree.</summary>
    public int ChargeMismatchCount { get; init; }

    public int ImportedFiles => Files.Count(f => !f.IsDuplicate);

    public decimal MaxFileDifference =>
        Files.Any(f => !f.IsDuplicate)
            ? Files.Where(f => !f.IsDuplicate).Max(f => Math.Abs(f.Difference))
            : 0m;

    public decimal MaxPayerDifference =>
        Payers.Count == 0 ? 0m : Payers.Max(p => Math.Abs(p.Difference));

    /// <summary>Throws unless every file and every payer reconciles to the cent.</summary>
    public void AssertZeroDifferences()
    {
        foreach (var f in Files.Where(f => !f.IsDuplicate))
            if (f.Difference != 0m)
                throw new InvalidOperationException(
                    $"{f.FileName}: BPR {M(f.BprTotal)} != paid {M(f.PaidSum)} "
                  + $"(difference {M(f.Difference)}).");

        foreach (var p in Payers)
            if (p.Difference != 0m)
                throw new InvalidOperationException(
                    $"payer {p.PayerId}: BPR {M(p.BprTotal)} != paid {M(p.PaidSum)} "
                  + $"(difference {M(p.Difference)}).");
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        sb.AppendLine("## Reconciliation — payer and file");
        sb.AppendLine();
        sb.AppendLine("| File | duplicate | transactions | BPR (file cash) | Σ CLP04 | difference |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var f in Files)
            sb.AppendLine($"| {f.FileName} | {(f.IsDuplicate ? $"yes → {f.DuplicateOf}" : "no")} "
                        + $"| {f.Transactions} | {M(f.BprTotal)} | {M(f.PaidSum)} | {M(f.Difference)} |");
        sb.AppendLine();

        sb.AppendLine("| Payer | transactions | BPR | Σ CLP04 | difference |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var p in Payers)
            sb.AppendLine($"| {p.PayerId} | {p.Transactions} | {M(p.BprTotal)} | {M(p.PaidSum)} | {M(p.Difference)} |");
        sb.AppendLine();

        sb.AppendLine("## Adjustments by claim adjustment group code");
        sb.AppendLine();
        sb.AppendLine("| Group | meaning | count | signed amount |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var g in AdjustmentGroups)
            sb.AppendLine($"| {g.GroupCode} | {g.Meaning} | {g.Count} | {M(g.Amount)} |");
        sb.AppendLine();

        if (UnattributedAdjustmentGroups.Count > 0)
        {
            sb.AppendLine("Of which on payment events with no matching claim in the export:");
            sb.AppendLine();
            sb.AppendLine("| Group | count | signed amount |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in UnattributedAdjustmentGroups)
                sb.AppendLine($"| {g.GroupCode} | {g.Count} | {M(g.Amount)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Cash and books");
        sb.AppendLine();
        sb.AppendLine("| Figure | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Cash including duplicate | {M(CashIncludingDuplicate)} |");
        sb.AppendLine($"| Duplicate cash excluded ({DuplicateFiles} file(s)) | {M(DuplicateCashExcluded)} |");
        sb.AppendLine($"| **Cash excluding duplicate** | **{M(CashExcludingDuplicate)}** |");
        sb.AppendLine($"| Claims in export | {TotalClaims} |");
        sb.AppendLine($"| Export charge | {M(TotalClaimCharge)} |");
        sb.AppendLine($"| Adjudicated claims | {AdjudicatedClaims} |");
        sb.AppendLine($"| Adjudicated charge | {M(AdjudicatedCharge)} |");
        sb.AppendLine($"| Never adjudicated claims | {UnadjudicatedClaims} |");
        sb.AppendLine($"| Never adjudicated charge | {M(UnadjudicatedCharge)} |");
        sb.AppendLine($"| Denied (latest status 4) | {DeniedClaims} |");
        sb.AppendLine($"| Denied charge | {M(DeniedCharge)} |");
        sb.AppendLine($"| Orphan payment events (claim not in export) | {OrphanObservationCount} |");
        sb.AppendLine($"| Payment events refused by natural key guard | {DuplicateObservationCount} |");
        sb.AppendLine($"| Claims where export charge != remitted CLP03 | {ChargeMismatchCount} |");
        sb.AppendLine($"| Max file difference | {M(MaxFileDifference)} |");
        sb.AppendLine($"| Max payer difference | {M(MaxPayerDifference)} |");

        return sb.ToString();
    }

    private static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
