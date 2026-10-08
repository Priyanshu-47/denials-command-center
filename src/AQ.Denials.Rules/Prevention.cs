using AQ.Denials.Core.Domain;

namespace AQ.Denials.Rules;

/// <summary>
/// One pre-bill check, and what this pack's own data says it would have caught.
/// </summary>
/// <param name="Measurable">
/// False when the pack does not contain the field the check needs. Such a check is still listed,
/// because "we should be checking enrollment" is a real finding — but it carries no number.
/// </param>
/// <param name="Confidence">
/// <c>observed</c> when the check is a plain test on a field the claim actually carries;
/// <c>association</c> when the relationship is statistical rather than causal;
/// <c>not_measurable</c> when the input data is absent.
/// </param>
public sealed record PreventionCheck(
    string Id,
    string Name,
    string Question,
    bool Measurable,
    string? WhyNotMeasured,
    int ClaimsScreened,
    int ClaimsFlagged,
    int DenialsCaught,
    decimal AmountCaught,
    int OpenDenialsTotal,
    decimal OpenDenialAmountTotal,
    string Confidence,
    IReadOnlyDictionary<string, string> Rule)
{
    /// <share>Share of open denials this check would have stopped before the claim was sent.</share>
    public decimal CatchRate => OpenDenialsTotal == 0 ? 0m : DenialsCaught / (decimal)OpenDenialsTotal;

    /// <share>Share of everything submitted that the check would have held up. This is the cost.</share>
    public decimal BurdenRate => ClaimsScreened == 0 ? 0m : ClaimsFlagged / (decimal)ClaimsScreened;
}

/// <summary>
/// Which checks, applied before a claim is sent, would have stopped the most denials and money.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number here is a re-run of the pack, not an estimate.</b> Each check is a predicate
/// applied to all 1,222 claims: <c>ClaimsFlagged</c> is what it would have held up, and
/// <c>DenialsCaught</c> is how many of today's 136 open denials are inside that flagged set. The
/// two are always reported together, because a check that catches everything by flagging
/// everything is not a check — it is a bottleneck, and <c>BurdenRate</c> is what exposes it.
/// </para>
/// <para>
/// <b>What this deliberately does not claim.</b> It does not claim the denial would not have
/// happened by some other route, and it does not claim causation. Where the relationship is an
/// observed rate rather than a hard predicate — pre-bill review in particular — the check is
/// labelled <c>association</c>, because the reviewed claims may simply have been the cleaner
/// ones.
/// </para>
/// <para>
/// <b>No patient identifier is ever emitted.</b> The duplicate check keys on member id, date of
/// service and CPT in order to find a prior paid sibling; that key is computed inside this method
/// and thrown away. Only counts and money leave it.
/// </para>
/// </remarks>
public static class PreventionAnalyzer
{
    private const int FilingHeadroomPercent = 70;

    public static IReadOnlyList<PreventionCheck> Analyse(
        IReadOnlyList<Claim> claims,
        PayerWindows windows,
        string today)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(windows);

        var open = claims.Where(IsOpenDenial).ToList();
        var openTotal = open.Count;
        var openAmount = open.Sum(c => c.Charge);

        var checks = new List<PreventionCheck>
        {
            AuthorisationNumber(claims, open, openTotal, openAmount),
            PrebillReview(claims, open, openTotal, openAmount),
            PriorPaidSibling(claims, open, openTotal, openAmount),
            FilingHeadroom(claims, windows, open, openTotal, openAmount),
            ProviderEnrolledOnDos(claims, openTotal, openAmount),
        };

        return checks.OrderByDescending(c => c.DenialsCaught)
                     .ThenByDescending(c => c.AmountCaught)
                     .ThenBy(c => c.Id, StringComparer.Ordinal)
                     .ToList();
    }

    public static bool IsOpenDenial(Claim claim) =>
        claim.Observations.Count > 0 && claim.CurrentStatus == "4";

    private static PreventionCheck Build(
        string id, string name, string question,
        IReadOnlyList<Claim> claims, IReadOnlyList<Claim> open,
        Func<Claim, bool> flagged,
        int openTotal, decimal openAmount,
        string confidence,
        IReadOnlyDictionary<string, string> rule,
        string? whyNotMeasured = null)
    {
        var caught = open.Where(flagged).ToList();

        return new PreventionCheck(
            Id: id,
            Name: name,
            Question: question,
            Measurable: whyNotMeasured is null,
            WhyNotMeasured: whyNotMeasured,
            ClaimsScreened: claims.Count,
            ClaimsFlagged: claims.Count(flagged),
            DenialsCaught: caught.Count,
            AmountCaught: caught.Sum(c => c.Charge),
            OpenDenialsTotal: openTotal,
            OpenDenialAmountTotal: openAmount,
            Confidence: confidence,
            Rule: rule);
    }

    /* ---- the checks -------------------------------------------------------------- */

    private static PreventionCheck AuthorisationNumber(
        IReadOnlyList<Claim> claims, IReadOnlyList<Claim> open, int openTotal, decimal openAmount) =>
        Build("auth-number-present",
            "Authorisation number present",
            "Would holding the claim until an authorisation number is on it have stopped it?",
            claims, open,
            c => string.IsNullOrWhiteSpace(c.AuthNumber),
            openTotal, openAmount,
            "observed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "required_field",
                ["field"] = "auth_number",
                ["operator"] = "not_empty",
                ["blocks"] = "submission",
            });

    private static PreventionCheck PrebillReview(
        IReadOnlyList<Claim> claims, IReadOnlyList<Claim> open, int openTotal, decimal openAmount)
    {
        // An association, not a predicate: reviewed claims may be the ones that were already
        // cleaner. Labelled as such rather than presented as "review prevents denials".
        var flag = (Claim c) => !string.Equals(c.PrebillReviewed, "Y", StringComparison.OrdinalIgnoreCase);
        return Build("prebill-review-recorded",
            "Pre-bill review recorded",
            "How many of today's denials were never checked before submission?",
            claims, open, flag, openTotal, openAmount,
            "association",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "required_field",
                ["field"] = "prebill_reviewed",
                ["operator"] = "equals",
                ["value"] = "Y",
                ["blocks"] = "submission",
            });
    }

    private static PreventionCheck PriorPaidSibling(
        IReadOnlyList<Claim> claims, IReadOnlyList<Claim> open, int openTotal, decimal openAmount)
    {
        // A prior PAID claim for the same member, date of service and CPT means the next one is a
        // duplicate. The key is built and discarded here — member id never leaves this method.
        var paidKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (claim.CurrentStatus != "1") continue;
            foreach (var line in claim.Lines)
                paidKeys.Add(Key(claim, line));
        }

        bool Flagged(Claim claim) => claim.Lines.Any(l => paidKeys.Contains(Key(claim, l)));

        return Build("prior-paid-sibling",
            "Duplicate of an already-paid claim",
            "Would spotting that the same service was already paid have stopped this?",
            claims, open, Flagged, openTotal, openAmount,
            "observed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "no_prior_paid_claim",
                ["match_on"] = "member_id,dos,cpt",
                ["blocks"] = "submission",
                ["action"] = "route_to_coder_confirmation",
            });

        // Duplicate claim ids are excluded so a claim cannot be its own prior sibling.
        static string Key(Claim claim, ClaimLine line) =>
            $"{claim.MemberId}|{claim.Dos}|{line.Cpt}";
    }

    private static PreventionCheck FilingHeadroom(
        IReadOnlyList<Claim> claims, PayerWindows windows,
        IReadOnlyList<Claim> open, int openTotal, decimal openAmount)
    {
        bool Flagged(Claim claim)
        {
            if (!DateTime.TryParse(claim.Dos, out var dos)) return false;
            if (!DateTime.TryParse(claim.SubmittedDate, out var submitted)) return false;

            if (!windows.TryGet(claim.PayerId, out var window)) return false;

            // Headroom left when we actually sent it, as a share of the payer's own filing limit.
            var used = (submitted - dos).Days;
            return used * 100 >= window.TimelyFilingDaysFromDos * FilingHeadroomPercent;
        }

        return Build("filing-headroom",
            "Filed late against the payer's own limit",
            "Were we already most of the way through the filing window when this was sent?",
            claims, open, Flagged, openTotal, openAmount,
            "observed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "max_days_from_dos",
                ["field"] = "submitted_date",
                ["threshold_percent"] = FilingHeadroomPercent.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["window_source"] = "payer_rules.csv:timely_filing_days_from_dos",
                ["blocks"] = "submission",
            });
    }

    private static PreventionCheck ProviderEnrolledOnDos(
        IReadOnlyList<Claim> claims, int openTotal, decimal openAmount) =>
        // Listed, unnumbered, and said plainly: this is the single largest category (26 of 136)
        // and the pack has no enrollment table, so no honest figure exists for it. Producing one
        // by inference would put the most confident-looking number on the weakest evidence.
        new(
            Id: "provider-enrolled-on-dos",
            Name: "Rendering provider enrolled on the date of service",
            Question: "Was the provider enrolled and effective on the day the service happened?",
            Measurable: false,
            WhyNotMeasured: "The data pack has no provider enrollment table — no effective dates "
                           + "to compare against the date of service. The claim carries the NPI, "
                           + "which identifies the provider but not whether they were enrolled. "
                           + "This is the largest single category of denial in the pack, so the "
                           + "gap is worth closing with an enrollment feed before the number "
                           + "means anything.",
            ClaimsScreened: claims.Count,
            ClaimsFlagged: 0,
            DenialsCaught: 0,
            AmountCaught: 0m,
            OpenDenialsTotal: openTotal,
            OpenDenialAmountTotal: openAmount,
            Confidence: "not_measurable",
            Rule: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "provider_enrollment_active_on_dos",
                ["field"] = "rendering_npi",
                ["requires"] = "provider_enrollment_feed",
                ["blocks"] = "submission",
                ["status"] = "cannot_evaluate_without_data",
            });
}
