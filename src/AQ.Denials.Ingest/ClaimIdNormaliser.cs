namespace AQ.Denials.Ingest;

/// <summary>How a raw claim reference was classified.</summary>
public enum ClaimIdKind
{
    /// <summary>Canonicalised to <c>GPP-YYYY-NNNNNN</c>.</summary>
    Matched,

    /// <summary>A recognisable but foreign id namespace, e.g. <c>BHC-2026-123456</c>.</summary>
    Foreign,

    /// <summary>Not an id we understand. Never guessed at.</summary>
    Unparseable,
}

public readonly record struct ClaimIdResult(string? CanonicalId, ClaimIdKind Kind, string Raw)
{
    public bool IsMatched => Kind == ClaimIdKind.Matched;
}

/// <summary>
/// The single place a claim reference is turned into a canonical id (D2). Every reader —
/// 835, claim export, worklog — goes through here, so two sources cannot disagree about
/// what "the same claim" means.
/// </summary>
/// <remarks>
/// <code>
/// trim → case-fold the GPP prefix
/// GPP[-_ ]?YYYY[-_ ]?N(1..6)  → GPP-&lt;YYYY&gt;-&lt;N zero-padded to 6&gt;
/// N(1..6)                     → GPP-2026-&lt;N zero-padded to 6&gt;   (single series in this pack)
/// GPP-2026-NNNNNN             → unchanged
/// anything else               → null → exceptions, never guessed
/// </code>
/// Anything that is recognisably an id from another namespace is reported as
/// <see cref="ClaimIdKind.Foreign"/> rather than being mangled into the GPP series — matching
/// a foreign id by stripping digits would fabricate a link that does not exist.
/// </remarks>
public static class ClaimIdNormaliser
{
    /// <summary>The only claim series present in this data pack.</summary>
    public const string DefaultSeries = "2026";

    public static ClaimIdResult Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ClaimIdResult(null, ClaimIdKind.Unparseable, raw ?? string.Empty);

        var s = raw.Trim();

        // GPP[-_ ]?YYYY[-_ ]?N(1..6)
        var m = System.Text.RegularExpressions.Regex.Match(
            s, @"^(?<prefix>GPP)[-_ ]?(?<year>\d{4})[-_ ]?(?<n>\d{1,6})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            return new ClaimIdResult(Format(m.Groups["year"].Value, m.Groups["n"].Value),
                ClaimIdKind.Matched, raw);

        // Bare digits (the worklog uses these a lot)
        m = System.Text.RegularExpressions.Regex.Match(s, @"^\d{1,6}$");
        if (m.Success)
            return new ClaimIdResult(Format(DefaultSeries, s), ClaimIdKind.Matched, raw);

        // GPP-2026-NNNNNN already canonical (case-insensitive prefix)
        m = System.Text.RegularExpressions.Regex.Match(
            s, @"^GPP-(?<year>\d{4})-(?<n>\d{6})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            return new ClaimIdResult(Format(m.Groups["year"].Value, m.Groups["n"].Value),
                ClaimIdKind.Matched, raw);

        // Another series entirely, e.g. BHC-2026-123456
        m = System.Text.RegularExpressions.Regex.Match(
            s, @"^(?<prefix>[A-Za-z]{3})[-_ ]?(?<year>\d{4})[-_ ]?(?<n>\d{1,6})$");
        if (m.Success)
            return new ClaimIdResult(null, ClaimIdKind.Foreign, raw);

        return new ClaimIdResult(null, ClaimIdKind.Unparseable, raw);
    }

    private static string Format(string year, string digits) =>
        $"GPP-{year}-{digits.PadLeft(6, '0')}";
}
