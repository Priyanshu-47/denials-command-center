namespace AQ.Denials.Rules;

/// <summary>What an operator needs to know about one open denial.</summary>
public sealed record OpenDenialAssessment(
    string ClaimId,
    string PayerId,
    string Category,
    string DenialDate,
    string AppealEnds,
    string CorrectedClaimEnds,
    bool AnyDenialRouteOpen,
    string Bucket,
    decimal Charge);

/// <summary>
/// Applies <see cref="DenialCategory"/>, <see cref="PayerWindows"/> and <see cref="RecoveryBuckets"/>
/// to the claims that are currently denied, and proves the three buckets add up.
/// </summary>
/// <remarks>
/// <para>
/// The anchor is the <b>check date of the denying remittance</b> (<c>DTM*405</c>), not the date the
/// claim was submitted and not the date the denial was typed into the tracker. Measured on this
/// pack: all 28 rows with an ambiguous logged date carry a <c>DTM*405</c>, so no deadline in this
/// system is ever computed from a date we could not read.
/// </para>
/// <para>
/// A claim counts as open only if the <i>latest</i> remittance for it says status 4. Claims that
/// were denied and later paid are recoveries, not open work, and are excluded here.
/// </para>
/// </remarks>
public static class RecoveryAssessment
{
    /// <summary>One claim as this assessment needs to see it.</summary>
    public sealed record Subject(
        string ClaimId,
        string PayerId,
        string DateOfService,
        decimal Charge,
        string DenialDate,
        IEnumerable<string> Carcs);

    public static IReadOnlyList<OpenDenialAssessment> Assess(
        IEnumerable<Subject> deniedClaims,
        PayerWindows windows,
        string today)
    {
        var assessments = new List<OpenDenialAssessment>();

        foreach (var subject in deniedClaims)
        {
            var category = DenialCategory.Of(subject.Carcs, subject.PayerId, subject.DateOfService);
            var open = windows.AnyDenialRouteOpen(subject.PayerId, subject.DenialDate, today);

            assessments.Add(new OpenDenialAssessment(
                subject.ClaimId,
                subject.PayerId,
                category,
                subject.DenialDate,
                windows.DenialRouteEndDate(subject.PayerId, subject.DenialDate),
                windows.CorrectedRouteEndDate(subject.PayerId, subject.DenialDate),
                open,
                RecoveryBuckets.Assign(category, open),
                subject.Charge));
        }

        return assessments
            .OrderBy(a => a.ClaimId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Aggregate into the published table, asserting the exact-sum identity.</summary>
    public static BucketTable Summarise(IReadOnlyList<OpenDenialAssessment> assessments)
    {
        var table = BucketTableBuilder.Build(assessments.Select(a =>
            new OpenDenial(a.ClaimId, a.Category, a.Charge, a.Bucket)));

        // A claim must not appear twice under two categories: that is how "the parts sum to the
        // whole" quietly stops being true.
        var distinct = assessments.Select(a => a.ClaimId).Distinct(StringComparer.Ordinal).Count();
        if (distinct != assessments.Count)
            throw new InvalidOperationException(
                $"claim assessed more than once: {assessments.Count} rows for {distinct} claims.");

        return table;
    }
}
