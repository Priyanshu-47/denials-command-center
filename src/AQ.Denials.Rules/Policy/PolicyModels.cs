namespace AQ.Denials.Rules.Policy;

/// <summary>One numbered clause of a payer policy document.</summary>
/// <param name="Id">
/// The clause number as it appears in the document (<c>"3"</c>), because the brief asks the draft
/// to cite <i>"the exact payer policy section"</i> — a section the reader can actually find.
/// </param>
/// <param name="Text">The clause's full text, unmodified from the file.</param>
public sealed record PolicySection(string Id, string Text)
{
    public override string ToString() => $"{Id}. {Text}";
}

/// <summary>A parsed payer policy file.</summary>
public sealed record PolicyDocument(
    string FileName,
    string Title,
    IReadOnlyList<PolicySection> Sections)
{
    /// <summary>Sections carrying at least one of <paramref name="carcs"/> as an explicit CARC/RARC reference.</summary>
    public IReadOnlyList<PolicySection> SectionsMentioning(IEnumerable<string> carcs)
    {
        var wanted = carcs
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0) return [];

        return Sections
            .Where(s => referencedCodes(s.Text).Any(wanted.Contains))
            .ToList();
    }

    /// <summary>
    /// CARC/RARC codes the clause names <b>explicitly</b>, i.e. preceded by the word CARC or RARC.
    /// </summary>
    /// <remarks>
    /// A bare number is never enough. <c>99231-99233</c> and <c>within 60 days</c> both contain
    /// digits that would match a naive substring test, and a false match would let a denial cite
    /// a policy that says nothing about it — the exact failure Q3 exists to prevent.
    /// </remarks>
    private static IEnumerable<string> referencedCodes(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\b(?:CARC|RARC)\s*0*([0-9]{1,3}|[A-Z][0-9]{1,3})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in matches)
            yield return m.Groups[1].Value;
    }
}

/// <summary>
/// A draft's claim that it relies on a specific clause of a specific payer policy.
/// </summary>
/// <remarks>
/// This is a <b>structured value, not free text</b>. The model does not get to hand back a
/// sentence containing a citation; it returns fields that are parsed into this record and then
/// re-checked against the real document (see <see cref="CitationValidator"/>). A citation that
/// cannot be resolved to a clause that exists is dropped, not softened.
/// </remarks>
/// <param name="FileName">Policy file name, e.g. <c>NSHP_HOSP-FREQ-07.md</c>.</param>
/// <param name="SectionId">Clause number as cited, e.g. <c>"3"</c>.</param>
/// <param name="Quote">The draft's claimed quotation, verified against the real clause text.</param>
public sealed record PolicyCitation(string FileName, string SectionId, string? Quote);
