namespace AQ.Denials.Rules;

/// <summary>
/// The deterministic denial taxonomy. This is the <b>single place</b> a category, its owning
/// team and its preventability are decided; the LLM never chooses them (D4).
/// </summary>
/// <remarks>
/// <para>
/// Ordering matters and is part of the contract: <c>CARC 29</c> (timely filing) wins over
/// everything because no amount of recoding recovers a claim the payer will not accept, and
/// <c>CARC B7</c> (credentialing) wins next because policy — not coding — is what bars the route.
/// The <c>197</c> case is the only category that depends on payer <b>and</b> date of service:
/// Sunshine Medicaid's SNF authorization policy only applies to dates of service on or after
/// <c>2026-04-01</c>, so a 197 before that date is payer error, not an authorization miss.
/// </para>
/// <para>
/// Adding a category means editing this file and its test — nothing else in the system has to
/// change. <see cref="Fallback"/> is deliberately its own value so an uncategorised denial is
/// visible rather than silently bucketed into something plausible.
/// </para>
/// </remarks>
public static class DenialCategory
{
    public const string TimelyFiling = "Billing - timely filing";
    public const string Credentialing = "Credentialing";
    public const string Authorization = "Authorization";
    public const string Eligibility = "Eligibility";
    public const string MedicalNecessity = "Medical necessity";
    public const string CodingDiagnosis = "Coding - diagnosis";
    public const string CodingFrequency = "Coding - frequency";
    public const string CodingModifier = "Coding - modifier";
    public const string PayerError = "Payer error";
    public const string DuplicateUnvalidated = "Duplicate submission (unvalidated)";
    public const string Fallback = "UNMAPPED";

    /// <summary>
    /// Sunshine Medicaid Partners' SNF authorization bulletin applies to dates of service on or
    /// after this date; earlier claims cannot require an authorization that did not exist.
    /// </summary>
    public const string SmpSnfAuthEffectiveDate = "2026-04-01";

    public const string SmpPayerId = "SMP12";

    public static readonly IReadOnlyList<string> All =
    [
        TimelyFiling, Credentialing, Authorization, Eligibility, MedicalNecessity,
        CodingDiagnosis, CodingFrequency, CodingModifier, PayerError,
        DuplicateUnvalidated, Fallback,
    ];

    /// <param name="carcs">Every distinct CARC on the observation, claim level and service level.</param>
    /// <param name="payerId">The denying payer's own identifier (<c>REF*2U</c>).</param>
    /// <param name="dateOfService"><c>yyyy-MM-dd</c>; only consulted for the CARC-197 branch.</param>
    public static string Of(IEnumerable<string> carcs, string payerId, string? dateOfService)
    {
        var set = carcs.Select(c => c.Trim()).Where(c => c.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (set.Contains("29")) return TimelyFiling;
        if (set.Contains("B7")) return Credentialing;
        if (set.Contains("197"))
            return payerId == SmpPayerId && IsBefore(dateOfService, SmpSnfAuthEffectiveDate)
                ? PayerError
                : Authorization;
        if (set.Contains("27")) return Eligibility;
        if (set.Contains("50")) return MedicalNecessity;
        if (set.Contains("11")) return CodingDiagnosis;
        if (set.Contains("151")) return CodingFrequency;
        if (set.Contains("97")) return CodingModifier;
        if (set.Contains("18")) return DuplicateUnvalidated;

        return Fallback;
    }

    private static bool IsBefore(string? date, string bound) =>
        !string.IsNullOrEmpty(date) && string.CompareOrdinal(date, bound) < 0;
}
