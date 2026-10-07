using System.Security.Cryptography;
using System.Text;

namespace AQ.Denials.Ingest.X12;

/// <summary>
/// One parsed X12 segment: its tag and its elements.
/// </summary>
/// <remarks>
/// Two accessors, deliberately, because mixing them is a classic off-by-one:
/// <list type="bullet">
/// <item><description><see cref="El"/> is <b>1-based and spec-notation</b>: <c>seg.El(1)</c> is
/// <c>CAS01</c>, <c>seg.El(2)</c> is <c>CAS02</c>. Use this everywhere — it reads exactly like
/// the X12 standard.</description></item>
/// <item><description><see cref="Elements"/> is the raw 0-based array after the tag. Use it only
/// when walking variable-length pairs.</description></item>
/// </list>
/// </remarks>
public readonly record struct X12Segment(string Tag, string[] Elements, int Index)
{
    public string this[int i] => i < Elements.Length ? Elements[i] : string.Empty;

    /// <summary>Spec element <paramref name="n"/> (1-based): <c>El(1)</c> for <c>CAS01</c>.</summary>
    public string? El(int n) =>
        n >= 1 && n <= Elements.Length && Elements[n - 1].Length > 0 ? Elements[n - 1] : null;

    /// <summary>Spec element <paramref name="n"/>, or empty string when absent/blank.</summary>
    public string ElOrEmpty(int n) => El(n) ?? string.Empty;

    /// <summary>Element <paramref name="i"/> (0-based), or null when absent/blank.</summary>
    public string? At(int i) => i < Elements.Length && Elements[i].Length > 0 ? Elements[i] : null;

    /// <summary>Raw segment text (tag + elements), used for hashing.</summary>
    public string Text => Elements.Length == 0 ? Tag : Tag + "*" + string.Join("*", Elements);
}

/// <summary>
/// Splits an ASC X12 interchange into segments. Deliberately forgiving: a structurally broken
/// segment becomes an error record that the pipeline turns into an exception row, never an
/// exception that kills the run.
/// </summary>
/// <remarks>
/// Delimiters are taken from <c>ISA16</c> rather than hard-coded, because a trailing
/// component-separator element is optional in practice — hard-coding the positional read of
/// ISA16 breaks on a file that omits it.
/// </remarks>
public static class X12Tokenizer
{
    public const char SegmentTerminator = '~';
    public const char ElementSeparator = '*';
    public const char ComponentSeparator = ':';

    public sealed record ParseError(int SegmentIndex, string Raw, string Message);

    public sealed record ParseResult(
        IReadOnlyList<X12Segment> Segments,
        IReadOnlyList<ParseError> Errors,
        char ComponentSeparator);

    public static ParseResult Tokenize(string content)
    {
        var segments = new List<X12Segment>();
        var errors = new List<ParseError>();
        var componentSep = ComponentSeparator;

        // ISA fixes the delimiters. It is always the first segment, and it has exactly 16
        // elements: ISA16 is the component data separator used inside elements such as
        // SVC01 "HC:99232". Read it by spec position (parts[16], parts[0] being the tag).
        var isaProbe = content.Split(SegmentTerminator, 2);
        if (isaProbe.Length > 0 && isaProbe[0].TrimStart('\r', '\n').StartsWith("ISA", StringComparison.Ordinal))
        {
            var isa = isaProbe[0].Trim('\r', '\n').Split(ElementSeparator);
            if (isa.Length >= 17 && isa[16].Length > 0) componentSep = isa[16][0];
        }

        var index = 0;
        foreach (var raw in content.Split(SegmentTerminator))
        {
            var text = raw.Trim('\r', '\n', ' ');
            if (text.Length == 0) { index++; continue; }

            if (text.Length < 2)
            {
                errors.Add(new ParseError(index, text, "Segment is too short to carry a tag."));
                index++;
                continue;
            }

            var parts = text.Split(ElementSeparator);
            var tag = parts[0];
            // X12 segment identifiers are 2 or 3 characters and may contain digits.
            // Verified against this pack: 18 distinct tags —
            //   length 2: GE GS LQ LX N1 SE ST
            //   length 3: AMT BPR CAS CLP DTM IEA ISA NM1 REF SVC TRN
            // (N1 and NM1 end in a digit, so "letters only" is also wrong.) Restricting this to
            // 2-letter alphabetic tags silently dropped every 3-letter segment — including CLP,
            // CAS and SVC — and left the parser holding an empty interchange.
            if (tag.Length is < 2 or > 3 || !tag.All(char.IsLetterOrDigit))
            {
                errors.Add(new ParseError(index, text,
                    $"Segment tag '{tag}' is not a 2-3 character alphanumeric X12 identifier."));
                index++;
                continue;
            }

            segments.Add(new X12Segment(tag, parts[1..], index));
            index++;
        }

        return new ParseResult(segments, errors, componentSep);
    }

    /// <summary>
    /// Normalised payment payload: every segment outside the ISA/GS/ST/SE/GE/IEA envelope,
    /// newline-normalised. Files identical here are the same payment even when the interchange
    /// wrapper differs.
    /// </summary>
    public static string NormalizedPayload(IEnumerable<X12Segment> segments)
    {
        var envelope = new HashSet<string>(StringComparer.Ordinal)
            { "ISA", "GS", "ST", "SE", "GE", "IEA" };
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (envelope.Contains(s.Tag)) continue;
            sb.Append(s.Text).Append('\n');
        }
        return sb.ToString();
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
