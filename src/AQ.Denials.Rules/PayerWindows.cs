namespace AQ.Denials.Rules;

/// <summary>
/// Appeal / corrected-claim / timely-filing windows, read from <c>payer_rules.csv</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two findings drive the design, both measured on this pack rather than assumed:
/// </para>
/// <list type="number">
/// <item><description>The appeal window and the corrected-claim window are <b>equal for every payer</b>
/// (180/180, 120/120, 60/60, 90/90), so "is any route still open" reduces to one test. The two
/// values are kept separate anyway: a future payer could split them, and hard-coding the equality
/// would then be wrong in the direction that loses money.</description></item>
/// <item><description>Timely filing counts from the <b>date of service</b>, the other two from the
/// <b>denial date</b>. They are different anchors and cannot be compared directly. Timely filing
/// governs a <i>first</i> submission; once a claim has been denied it is not a recovery route, so
/// it never reopens a denial whose denial-based windows have closed.</description></item>
/// </list>
/// </remarks>
public sealed class PayerWindows
{
    public sealed record Window(
        int AppealDaysFromDenial,
        int CorrectedDaysFromDenial,
        int TimelyFilingDaysFromDos,
        string? PolicyFile);

    private readonly Dictionary<string, Window> _byPayerId;

    public PayerWindows(IReadOnlyDictionary<string, Window> byPayerId) =>
        _byPayerId = new Dictionary<string, Window>(byPayerId, StringComparer.Ordinal);

    public IReadOnlyCollection<string> PayerIds => _byPayerId.Keys;

    public bool TryGet(string payerId, out Window window) =>
        _byPayerId.TryGetValue(payerId, out window!);

    public Window Get(string payerId) =>
        _byPayerId.TryGetValue(payerId, out var w)
            ? w
            : throw new KeyNotFoundException($"No window configured for payer '{payerId}'.");

    /// <summary>Last day (inclusive) an appeal or corrected claim may still be filed.</summary>
    public string DenialRouteEndDate(string payerId, string denialDate) =>
        AddDays(denialDate, Get(payerId).AppealDaysFromDenial);

    /// <summary>Last day (inclusive) a corrected claim may still be filed.</summary>
    public string CorrectedRouteEndDate(string payerId, string denialDate) =>
        AddDays(denialDate, Get(payerId).CorrectedDaysFromDenial);

    /// <summary>Last day (inclusive) to submit a <b>first</b> claim for this date of service.</summary>
    public string TimelyFilingEndDate(string payerId, string dateOfService) =>
        AddDays(dateOfService, Get(payerId).TimelyFilingDaysFromDos);

    /// <summary>
    /// True when at least one denial-based route is still open on <paramref name="today"/>.
    /// </summary>
    public bool AnyDenialRouteOpen(string payerId, string denialDate, string today)
    {
        var w = Get(payerId);

        // ISO-8601 dates compare correctly as ordinal strings, but only via CompareOrdinal:
        // String's own operators are locale-sensitive and would reorder dates under some cultures.
        var appealEnd = AddDays(denialDate, w.AppealDaysFromDenial);
        var correctedEnd = AddDays(denialDate, w.CorrectedDaysFromDenial);
        return string.CompareOrdinal(appealEnd, today) >= 0
            || string.CompareOrdinal(correctedEnd, today) >= 0;
    }

    /// <summary>Add whole days to a <c>yyyy-MM-dd</c> string, lexicographically comparable.</summary>
    public static string AddDays(string isoDate, int days)
    {
        if (isoDate.Length < 10 ||
            !DateTime.TryParseExact(isoDate[..10], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            throw new FormatException($"'{isoDate}' is not a yyyy-MM-dd date.");

        return d.AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
