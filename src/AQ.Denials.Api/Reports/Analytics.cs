using AQ.Denials.Core.Domain;
using AQ.Denials.Rules;

namespace AQ.Denials.Api.Reports;

public sealed record BucketSplit(string Bucket, int Count, decimal Amount);

public sealed record DimensionRow(string Key, int Count, decimal Amount);

public sealed record MoneyAtRisk(
    int OpenCount, decimal OpenAmount, IReadOnlyList<BucketSplit> Buckets,
    int PendingCount, decimal PendingAmount,
    int ZeroPaidLineCount, decimal ZeroPaidLineAmount);

public sealed record PrebillRow(string Reviewed, int Count, int Denials, decimal DenialRate);

public sealed record TrendRow(string Period, int Count, decimal Amount);

public sealed record AnalyticsReport(
    MoneyAtRisk MoneyAtRisk,
    IReadOnlyList<DimensionRow> ByPayer,
    IReadOnlyList<DimensionRow> ByCategory,
    IReadOnlyList<DimensionRow> ByProvider,
    IReadOnlyList<DimensionRow> ByCoder,
    IReadOnlyList<DimensionRow> ByFacility,
    IReadOnlyList<PrebillRow> ByPreBill,
    IReadOnlyList<TrendRow> Trend);

/// <summary>
/// The manager's first two questions, as numbers with a denominator attached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every figure here is a projection of the same canonical state the reconciliation report is
/// built from</b>, so "$16,785 recoverable" on this screen and the same figure in
/// <c>--report</c> cannot drift apart — there is one code path and one input.
/// </para>
/// <para>
/// <b>Money at risk is three different things, never summed.</b> Open denials are the workable
/// book; never-adjudicated claims are billed but undecided; accepted claims with a zero-paid
/// line were passed and then paid nothing for a service. Adding them would produce a bigger,
/// more impressive, and wrong number, because the three respond to three different actions.
/// </para>
/// </remarks>
public static class Analytics
{
    public static AnalyticsReport Build(
        IReadOnlyList<Claim> claims,
        PayerWindows windows,
        string today)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(windows);

        var open = claims.Where(c => c.Observations.Count > 0 && c.CurrentStatus == "4").ToList();

        var subjects = open.Select(c => new RecoveryAssessment.Subject(
            ClaimId: c.ClaimId,
            PayerId: c.PayerId,
            DateOfService: c.Dos,
            Charge: c.Charge,
            DenialDate: c.Observations[^1].CheckDate,
            Carcs: Carcs(c.Observations[^1])));

        var assessments = RecoveryAssessment.Assess(subjects, windows, today);
        var bucketByClaim = assessments.ToDictionary(
            a => a.ClaimId, a => a.Bucket, StringComparer.Ordinal);

        var bucketSplit = RecoveryBuckets.All
            .Select(b => new BucketSplit(
                b,
                open.Count(c => bucketByClaim.GetValueOrDefault(c.ClaimId) == b),
                open.Where(c => bucketByClaim.GetValueOrDefault(c.ClaimId) == b).Sum(c => c.Charge)))
            .ToList();

        var pending = claims.Where(c => c.Observations.Count == 0).ToList();
        var zeroPaid = ZeroPaidLines(claims);

        return new AnalyticsReport(
            MoneyAtRisk: new MoneyAtRisk(
                open.Count, open.Sum(c => c.Charge), bucketSplit,
                pending.Count, pending.Sum(c => c.Charge),
                zeroPaid.Count, zeroPaid.Sum(z => z.Amount)),
            ByPayer: Dimension(open, c => c.PayerName),
            ByCategory: Dimension(open, c => DenialCategory.Of(
                Carcs(c.Observations[^1]), c.PayerId, c.Dos)),
            ByProvider: Dimension(open, c => string.IsNullOrWhiteSpace(c.RenderingProvider)
                ? "(not recorded)" : c.RenderingProvider),
            ByCoder: Dimension(open, c => string.IsNullOrWhiteSpace(c.CoderId)
                ? "(not recorded)" : c.CoderId),
            ByFacility: Dimension(open, c => string.IsNullOrWhiteSpace(c.Facility)
                ? "(not recorded)" : c.Facility),
            ByPreBill: Prebill(claims),
            Trend: Trend(open));
    }

    private static IReadOnlyList<string> Carcs(RemitObservation observation) =>
        observation.Adjustments
                   .Select(a => a.Carc)
                   .Where(c => c.Length > 0)
                   .Distinct(StringComparer.Ordinal)
                   .ToList();

    private static IReadOnlyList<DimensionRow> Dimension(
        IReadOnlyList<Claim> source, Func<Claim, string> key) =>
        source.GroupBy(key, StringComparer.Ordinal)
              .Select(g => new DimensionRow(g.Key, g.Count(), g.Sum(c => c.Charge)))
              .OrderByDescending(d => d.Amount)
              .ThenBy(d => d.Key, StringComparer.Ordinal)
              .ToList();

    /// <summary>
    /// Claims that were accepted and then paid nothing for a service line — the bucket Q1 asked
    /// to keep separate from denials, because an accepted claim is not work the denial queue
    /// handles.
    /// </summary>
    /// <remarks>
    /// The amount is read off the remittance's own <c>SVC</c> line rather than looked up in the
    /// export and matched by position: a positional match is correct until the two files disagree
    /// about line order, which is exactly when a figure like this one would be wrong.
    /// </remarks>
    private static IReadOnlyList<(Claim Claim, decimal Amount)> ZeroPaidLines(
        IReadOnlyList<Claim> claims)
    {
        var result = new List<(Claim, decimal)>();

        foreach (var claim in claims)
        {
            if (claim.CurrentStatus != "1") continue;   // accepted only

            var observation = claim.Observations[^1];
            if (!observation.Adjustments.Any(a =>
                    string.Equals(a.Carc, "97", StringComparison.Ordinal)))
                continue;

            // Which line was paid nothing is a statement about the payment, so it is read from
            // the remittance's own SVC line rather than matched by position against the export —
            // a positional match is correct until the two files disagree about line order, which
            // is exactly when a figure like this one would be wrong.
            var amount = observation.Services
                .Where(s => s.Charge > 0m && s.Paid == 0m)
                .Sum(s => s.Charge);

            if (amount > 0m) result.Add((claim, amount));
        }

        return result;
    }

    private static IReadOnlyList<PrebillRow> Prebill(IReadOnlyList<Claim> claims)
    {
        // Denial *rate* is only meaningful over claims that have actually been decided, so a
        // claim with no remittance yet is excluded rather than counted as a non-denial. Counting
        // it would make the reviewed column look better purely because reviewed claims are
        // decided faster — a denominator doing the work the numerator cannot.
        var decided = claims.Where(c => c.Observations.Count > 0).ToList();

        return decided
            .GroupBy(c => string.Equals(c.PrebillReviewed, "Y", StringComparison.OrdinalIgnoreCase)
                    ? "Y" : "N", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var n = g.Count();
                var denials = g.Count(c => c.CurrentStatus == "4");
                return new PrebillRow(g.Key, n, denials, n == 0 ? 0m : denials / (decimal)n);
            })
            .ToList();
    }

    /// <summary>Open denials grouped by the month the payer denied them (<c>DTM*405</c>).</summary>
    private static IReadOnlyList<TrendRow> Trend(IReadOnlyList<Claim> open) =>
        open.GroupBy(c => c.Observations[^1].CheckDate.Length >= 7
                    ? c.Observations[^1].CheckDate[..7]
                    : "(unknown)", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TrendRow(g.Key, g.Count(), g.Sum(c => c.Charge)))
            .ToList();
}
