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

    /* ---- what a category means for the people doing the work ---------------------- */

    /// <summary>
    /// Everything downstream of the category: who owns it, whether it could have been stopped
    /// before the claim was sent, and what a specialist should do first.
    /// </summary>
    /// <param name="Team">Owning team, for routing.</param>
    /// <param name="PreventableAtPrebill">
    /// <b>Null when nobody has established it.</b> A claim about whether the practice's own
    /// pre-bill process could have caught this is a factual claim about how people work, and only
    /// one source in this assignment speaks to it: the 40-row expert sample. For the two
    /// categories that sample does not cover, this stays null rather than being filled in with
    /// something plausible — a confident "Yes" here is the difference between a system that
    /// reports what it knows and one that guesses at it.
    /// </param>
    /// <param name="NextAction">The first thing to do, written from the policy files and remits.</param>
    /// <param name="CoveredByLabeledSample">
    /// Whether <paramref name="Team"/> and <paramref name="PreventableAtPrebill"/> came from the
    /// expert's own assignments. False means they are this system's routing decision, and any
    /// confidence figure must be capped accordingly.
    /// </param>
    public sealed record CategoryOutcome(
        string Team,
        bool? PreventableAtPrebill,
        string NextAction,
        bool CoveredByLabeledSample);

    /// <summary>
    /// Category → outcome. The single place this mapping exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where these values come from, because it decides what may be claimed about them.</b>
    /// For the nine categories present in <c>labeled_denials_sample.csv</c>, team and
    /// preventability are read off the expert's own assignments: every one of the 40 rows maps a
    /// given category to exactly one team and one preventable value, with no exceptions. That
    /// means agreement <b>on those 40 rows is true by construction and is not a result</b> —
    /// reporting it as accuracy would be circular, which is precisely what <b>C1</b> prohibits.
    /// The measurable figure is the category itself, which comes from CARC codes and never
    /// touches the labels.
    /// </para>
    /// <para>
    /// <c>Duplicate submission (unvalidated)</c> and <c>UNMAPPED</c> do not appear in the sample
    /// at all, so no expert has told us their team or preventability. Their teams below are this
    /// system's routing decision (<b>Q5</b> requires duplicates to reach a human regardless) and
    /// their preventability is null. <see cref="CategoryOutcome.CoveredByLabeledSample"/> is the
    /// flag the confidence calculation reads.
    /// </para>
    /// <para>
    /// <c>NextAction</c> for the nine covered categories is written from the policy files and the
    /// remittance data, not from the sample — the sample does not carry an action column.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, CategoryOutcome> Outcomes =
        new Dictionary<string, CategoryOutcome>(StringComparer.Ordinal)
        {
            [TimelyFiling] = new(
                Team: "Billing",
                PreventableAtPrebill: true,
                NextAction: "Appeal inside the payer's timely-filing window, or write off if it has "
                          + "already closed. The window runs from date of service, so check it first.",
                CoveredByLabeledSample: true),

            [Credentialing] = new(
                Team: "Credentialing",
                PreventableAtPrebill: true,
                NextAction: "Confirm the practitioner's enrollment effective date against the date "
                          + "of service. Enrollment is not retroactive, so if the service predates "
                          + "it the claim cannot be appealed on that basis.",
                CoveredByLabeledSample: true),

            [Authorization] = new(
                Team: "Front desk / Authorization",
                PreventableAtPrebill: true,
                NextAction: "Attach the authorization number and resubmit, or appeal with the "
                          + "authorization evidence included.",
                CoveredByLabeledSample: true),

            [Eligibility] = new(
                Team: "Front desk / Eligibility",
                PreventableAtPrebill: true,
                NextAction: "Re-verify eligibility for the date of service and correct the member "
                          + "identifier or payer before rebilling.",
                CoveredByLabeledSample: true),

            [MedicalNecessity] = new(
                Team: "Coding / Clinical",
                PreventableAtPrebill: false,
                NextAction: "Appeal with the clinical record supporting medical necessity. Pre-bill "
                          + "checks cannot settle a judgement the payer makes after the fact.",
                CoveredByLabeledSample: true),

            [CodingDiagnosis] = new(
                Team: "Coding",
                PreventableAtPrebill: true,
                NextAction: "Review the diagnosis pairing against the payer's ICD-10-CM edit and "
                          + "correct the coding before resubmitting.",
                CoveredByLabeledSample: true),

            [CodingFrequency] = new(
                Team: "Coding",
                PreventableAtPrebill: true,
                NextAction: "Confirm whether the second same-day service followed a significant "
                          + "change of condition. If it did, rebill with modifier 25 and a "
                          + "distinct diagnosis, attaching both progress notes.",
                CoveredByLabeledSample: true),

            [CodingModifier] = new(
                Team: "Coding",
                PreventableAtPrebill: true,
                NextAction: "Add modifier 25 with supporting documentation, or correct and "
                          + "resubmit as a replacement claim inside the payer's corrected-claim "
                          + "window.",
                CoveredByLabeledSample: true),

            [PayerError] = new(
                Team: "Denials (appeal)",
                PreventableAtPrebill: false,
                NextAction: "Dispute with the payer: the remittance does not match either the "
                          + "claim as submitted or the contract. No pre-bill check prevents this.",
                CoveredByLabeledSample: true),

            // --- below: NOT present in the 40-row expert sample -------------------------
            [DuplicateUnvalidated] = new(
                Team: "Denials (appeal)",
                PreventableAtPrebill: null,
                NextAction: "Do not appeal. An earlier paid sibling exists, so close the denial — "
                          + "but confirm that sibling first, because the sample that would tell us "
                          + "this is reliable does not cover this category. Always human review.",
                CoveredByLabeledSample: false),

            [Fallback] = new(
                Team: "Denials (appeal)",
                PreventableAtPrebill: null,
                NextAction: "No mapping exists for this CARC. Route to a human before taking any "
                          + "action; do not infer a category from neighbouring codes.",
                CoveredByLabeledSample: false),
        };

    /// <exception cref="KeyNotFoundException">
    /// The category is not in <see cref="Outcomes"/>. Impossible while every value in
    /// <see cref="All"/> has an entry, which <c>DenialCategoryTests</c> asserts — so this throws
    /// at build-test time rather than quietly returning a plausible default at run time.
    /// </exception>
    public static CategoryOutcome OutcomeFor(string category)
    {
        if (Outcomes.TryGetValue(category, out var outcome)) return outcome;
        throw new KeyNotFoundException(
            $"No outcome table entry for denial category '{category}'. Every category in "
          + $"{nameof(All)} must have one; see {nameof(Outcomes)}.");
    }
}
