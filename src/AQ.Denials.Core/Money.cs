using System.Globalization;

namespace AQ.Denials.Core;

/// <summary>
/// Money helpers. Every monetary value in this system is a <see cref="decimal"/> stored in
/// dollars and cents (PostgreSQL <c>numeric(18,2)</c>). Floating point is never used for money:
/// <c>double</c>/<c>float</c> appear nowhere in the money path.
/// </summary>
/// <remarks>
/// <para><b>Sign convention (explicit, and the same everywhere):</b></para>
/// <list type="bullet">
/// <item><description><c>ClaimLine.Charge</c> — always positive (the amount billed).</description></item>
/// <item><description><c>Claim.SubmittedCharge</c> (CLP03) — positive for a normal claim, negative for a
/// reversal.</description></item>
/// <item><description><c>Claim.PaidAmount</c> (CLP04) — positive when the payer paid, negative on a
/// take-back.</description></item>
/// <item><description><c>Adjustment.Amount</c> (CAS) — <b>signed exactly as it appears in the 835</b>.
/// Contractual write-offs and patient-responsibility amounts are negative; a few reversals carry
/// positive CAS amounts. We never flip a sign and never take an absolute value.</description></item>
/// </list>
/// <para>The governing invariant, asserted on every observation:</para>
/// <code>CLP03 == CLP04 + sum(CAS, signed)</code>
/// </remarks>
public static class Money
{
    /// <summary>Multiplier from dollars to whole cents.</summary>
    public const decimal DollarsToCents = 100m;

    /// <summary>Parse an X12 decimal such as <c>-240.00</c>, <c>1460.10</c> or <c>0.00</c>.</summary>
    /// <exception cref="FormatException">The token is not a plain decimal number.</exception>
    public static decimal ParseX12(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new FormatException("X12 amount is empty.");

        var t = token.Trim();
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"X12 amount '{token}' is not a decimal number.");

        return value;
    }

    /// <summary>
    /// Parse a human/CSV money token such as <c>$1,460.10</c>, <c>(240.00)</c> or <c>-240.00</c>.
    /// Parenthesised values are negative, matching accounting convention.
    /// </summary>
    public static decimal? ParseLoose(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim().Replace("$", string.Empty).Replace(",", string.Empty);
        if (t.Length == 0) return null;

        if (t.StartsWith('(') && t.EndsWith(')'))
            t = "-" + t[1..^1];

        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Money token '{token}' is not a decimal number.");

        return value;
    }

    /// <summary>Render a signed amount as an X12 decimal, e.g. <c>-240.00</c>.</summary>
    public static string ToX12(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Render for display, keeping the sign visible: <c>-$240.00</c> / <c>$240.00</c>.</summary>
    public static string Display(decimal value) =>
        value < 0 ? $"-${Math.Abs(value):0.00}" : $"${value:0.00}";

    /// <summary>Convert dollars to integer cents. Lossless only when the value has at most 2 dp.</summary>
    public static long ToCents(decimal dollars) =>
        checked((long)decimal.Round(dollars * DollarsToCents, 0, MidpointRounding.AwayFromZero));

    /// <summary>Convert integer cents back to dollars.</summary>
    public static decimal FromCents(long cents) => cents / DollarsToCents;
}
