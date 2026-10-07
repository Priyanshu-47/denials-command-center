namespace AQ.Denials.Api.Reports;

/// <summary>
/// Wire shape for the reconciliation figures. Kept separate from <c>ReconciliationReport</c> so
/// the API contract can change without touching the arithmetic the tests pin down.
/// </summary>
public static class ReconciliationDto
{
    public static object From(Ingest.ReconciliationReport r) => new
    {
        cash = new
        {
            includingDuplicate = r.CashIncludingDuplicate,
            excludingDuplicate = r.CashExcludingDuplicate,
            duplicateExcluded = r.DuplicateCashExcluded,
            duplicateFiles = r.DuplicateFiles,
        },
        files = r.Files.Select(f => new
        {
            f.FileName, f.IsDuplicate, f.DuplicateOf, f.Transactions,
            bpr = f.BprTotal, paid = f.PaidSum, f.Difference, f.ObservationCount,
        }),
        payers = r.Payers.Select(p => new
        {
            p.PayerId, p.Transactions, bpr = p.BprTotal, paid = p.PaidSum,
            p.Difference, p.ObservationCount,
        }),
        adjustments = r.AdjustmentGroups.Select(g => new
        {
            g.GroupCode, g.Meaning, g.Count, g.Amount,
        }),
        unattributedAdjustments = r.UnattributedAdjustmentGroups.Select(g => new
        {
            g.GroupCode, g.Count, g.Amount,
        }),
        books = new
        {
            totalClaims = r.TotalClaims,
            totalCharge = r.TotalClaimCharge,
            adjudicatedClaims = r.AdjudicatedClaims,
            adjudicatedCharge = r.AdjudicatedCharge,
            unadjudicatedClaims = r.UnadjudicatedClaims,
            unadjudicatedCharge = r.UnadjudicatedCharge,
            deniedClaims = r.DeniedClaims,
            deniedCharge = r.DeniedCharge,
            orphanObservations = r.OrphanObservationCount,
            refusedByNaturalKey = r.DuplicateObservationCount,
            chargeMismatches = r.ChargeMismatchCount,
        },
        differences = new
        {
            maxFile = r.MaxFileDifference,
            maxPayer = r.MaxPayerDifference,
            allZero = r.MaxFileDifference == 0m && r.MaxPayerDifference == 0m,
        },
    };
}
