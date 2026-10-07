namespace AQ.Denials.Rules;

/// <summary>
/// The three mutually exclusive recoverability buckets, and their aggregation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one bucket per denial, always.</b> The order of the tests is the meaning:
/// </para>
/// <list type="number">
/// <item><description><b>Expired</b> first — if no denial-based route is open any more, nothing else about
/// the denial changes what an operator can do with it this morning.</description></item>
/// <item><description><b>Policy-blocked</b> next — the window is open but a payer policy bars the appeal on its
/// face (currently credentialing: the payer will not retroactively enroll a provider, so the
/// appeal is a guaranteed loss).</description></item>
/// <item><description><b>Recoverable</b> — open window, no policy bar.</description></item>
/// </list>
/// <para>
/// Because the tests are evaluated in that order and each returns, a denial cannot land in two
/// buckets, so <c>open = recoverable + policy-blocked + expired</c> is an identity rather than a
/// hope. <see cref="BucketTable.AssertIdentity"/> asserts it on every aggregation.
/// </para>
/// <para>
/// Timely filing deliberately does <b>not</b> reopen an expired denial: it counts from the date of
/// service and governs the first submission, not a route out of an adverse determination. Seven
/// denials in this pack have an open timely-filing window with every denial route closed; counting
/// them as recoverable would overstate the workable book by 7 claims.
/// </para>
/// </remarks>
public static class RecoveryBuckets
{
    public const string Recoverable = "RECOVERABLE";
    public const string PolicyBlocked = "POLICY_BLOCKED";
    public const string Expired = "EXPIRED";

    public static readonly IReadOnlyList<string> All = [Recoverable, PolicyBlocked, Expired];

    public static string Assign(string category, bool anyDenialRouteOpen) =>
        !anyDenialRouteOpen ? Expired
        : category == DenialCategory.Credentialing ? PolicyBlocked
        : Recoverable;
}

/// <summary>One open denial as far as bucketing is concerned.</summary>
public sealed record OpenDenial(
    string ClaimId,
    string Category,
    decimal Charge,
    string Bucket);

/// <summary>Per-category and total counts/amounts for the three buckets.</summary>
public sealed class BucketTable
{
    public sealed record Line(string Category, int Open, decimal OpenAmount,
        int Recoverable, decimal RecoverableAmount,
        int PolicyBlocked, decimal PolicyBlockedAmount,
        int Expired, decimal ExpiredAmount);

    public IReadOnlyList<Line> Categories { get; init; } = Array.Empty<Line>();

    public int TotalOpen { get; init; }
    public decimal TotalOpenAmount { get; init; }
    public int TotalRecoverable { get; init; }
    public decimal TotalRecoverableAmount { get; init; }
    public int TotalPolicyBlocked { get; init; }
    public decimal TotalPolicyBlockedAmount { get; init; }
    public int TotalExpired { get; init; }
    public decimal TotalExpiredAmount { get; init; }

    /// <summary>
    /// Asserts the identity that makes the headline number trustworthy: every open denial is in
    /// exactly one bucket, in count <b>and</b> in money.
    /// </summary>
    public void AssertIdentity()
    {
        var count = TotalRecoverable + TotalPolicyBlocked + TotalExpired;
        if (count != TotalOpen)
            throw new InvalidOperationException(
                $"bucket count {count} ({TotalRecoverable}+{TotalPolicyBlocked}+{TotalExpired}) "
              + $"!= open denials {TotalOpen}.");

        var money = TotalRecoverableAmount + TotalPolicyBlockedAmount + TotalExpiredAmount;
        if (money != TotalOpenAmount)
            throw new InvalidOperationException(
                $"bucket money {money} ({TotalRecoverableAmount}+{TotalPolicyBlockedAmount}"
              + $"+{TotalExpiredAmount}) != open denials {TotalOpenAmount}.");

        foreach (var line in Categories)
        {
            var lc = line.Recoverable + line.PolicyBlocked + line.Expired;
            var lm = line.RecoverableAmount + line.PolicyBlockedAmount + line.ExpiredAmount;
            if (lc != line.Open || lm != line.OpenAmount)
                throw new InvalidOperationException(
                    $"category '{line.Category}' does not balance: {lc}/{lm} vs {line.Open}/{line.OpenAmount}.");
        }
    }

    /// <summary>Rendered as the same markdown table the report publishes.</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| Denial category | open n/$ | Recoverable n/$ | Policy-blocked n/$ | Expired n/$ |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var l in Categories)
            sb.AppendLine($"| {l.Category} | {l.Open} / {M(l.OpenAmount)} "
                        + $"| {l.Recoverable} / {M(l.RecoverableAmount)} "
                        + $"| {l.PolicyBlocked} / {M(l.PolicyBlockedAmount)} "
                        + $"| {l.Expired} / {M(l.ExpiredAmount)} |");

        sb.AppendLine($"| **TOTAL** | **{TotalOpen} / {M(TotalOpenAmount)}** "
                    + $"| **{TotalRecoverable} / {M(TotalRecoverableAmount)}** "
                    + $"| **{TotalPolicyBlocked} / {M(TotalPolicyBlockedAmount)}** "
                    + $"| **{TotalExpired} / {M(TotalExpiredAmount)}** |");
        return sb.ToString();

        static string M(decimal v) => "$" + v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public static class BucketTableBuilder
{
    public static BucketTable Build(IEnumerable<OpenDenial> denials)
    {
        var rows = denials.ToList();

        var lines = rows
            .GroupBy(d => d.Category, StringComparer.Ordinal)
            .Select(g =>
            {
                static (int n, decimal a) Pick(IEnumerable<OpenDenial> s) =>
                    (s.Count(), s.Sum(d => d.Charge));

                var rec = Pick(g.Where(d => d.Bucket == RecoveryBuckets.Recoverable));
                var pol = Pick(g.Where(d => d.Bucket == RecoveryBuckets.PolicyBlocked));
                var exp = Pick(g.Where(d => d.Bucket == RecoveryBuckets.Expired));
                var open = Pick(g);

                return new BucketTable.Line(g.Key, open.n, open.a,
                    rec.n, rec.a, pol.n, pol.a, exp.n, exp.a);
            })
            .OrderByDescending(l => l.Open)
            .ThenBy(l => l.Category, StringComparer.Ordinal)
            .ToList();

        var table = new BucketTable
        {
            Categories = lines,
            TotalOpen = lines.Sum(l => l.Open),
            TotalOpenAmount = lines.Sum(l => l.OpenAmount),
            TotalRecoverable = lines.Sum(l => l.Recoverable),
            TotalRecoverableAmount = lines.Sum(l => l.RecoverableAmount),
            TotalPolicyBlocked = lines.Sum(l => l.PolicyBlocked),
            TotalPolicyBlockedAmount = lines.Sum(l => l.PolicyBlockedAmount),
            TotalExpired = lines.Sum(l => l.Expired),
            TotalExpiredAmount = lines.Sum(l => l.ExpiredAmount),
        };

        table.AssertIdentity();
        return table;
    }
}
