using System.Text.RegularExpressions;

namespace AQ.Denials.Rules.Policy;

/// <summary>
/// Turns a payer policy <c>.md</c> file into a document with addressable clauses.
/// </summary>
/// <remarks>
/// <para>
/// The pack's five policies are all written the same way — a title, an optional subtitle, an
/// <c>Effective:</c> line, then numbered clauses. This parser reads exactly that shape and is
/// deliberately strict about the one thing that matters: a clause must be <b>findable again</b>.
/// If it cannot be split, the failure happens here, at load, with the file named — not later as
/// a draft that confidently cites "section 3" of a document that has no section 3.
/// </para>
/// <para>
/// Everything else in the file is discarded except the text the clauses need. Nothing is
/// normalised, reworded or re-wrapped, because <see cref="CitationValidator"/> compares a draft's
/// claimed quotation against this text byte for byte (after whitespace collapsing).
/// </para>
/// </remarks>
public static class PolicyParser
{
    private static readonly Regex NumberedClause = new(
        @"^\s*(\d+)\.\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Heading = new(
        @"^\s*(#{1,6})\s+(.*)$",
        RegexOptions.Compiled);

    public static PolicyDocument Parse(string fileName, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException(
                $"Policy file '{fileName}' is empty. A policy with no clauses cannot be cited, "
              + "so it fails here rather than producing a draft that references nothing.");

        var lines = content.Replace("\r\n", "\n").Split('\n');

        var title = string.Empty;
        var subtitle = string.Empty;
        var clauses = new List<(string Id, List<string> Lines)>();
        (string Id, List<string> Lines)? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                CloseCurrent();
                // A second-level heading is a subtitle on these files; deeper ones are labels
                // the reader needs to find the clause, so they are folded into the title too.
                if (title.Length == 0) title = heading.Groups[2].Value.Trim();
                else if (subtitle.Length == 0) subtitle = heading.Groups[2].Value.Trim();
                continue;
            }

            var clause = NumberedClause.Match(line);
            if (clause.Success)
            {
                CloseCurrent();
                current = (clause.Groups[1].Value, new List<string> { clause.Groups[2].Value.Trim() });
                continue;
            }

            // A blank line inside a clause is paragraph spacing, not the end of the clause.
            if (line.Trim().Length == 0) continue;

            if (current is { } c)
            {
                // Continuation: the pack's clauses are one line each, but a wrapped or
                // multi-paragraph clause must not silently become a separate document.
                c.Lines.Add(line.Trim());
                current = c;
            }
            // Text before the first clause (Effective:, Applies to:) is context, not a clause.
        }
        CloseCurrent();

        void CloseCurrent()
        {
            if (current is { } c) { clauses.Add(c); current = null; }
        }

        if (clauses.Count == 0)
            throw new InvalidDataException(
                $"Policy file '{fileName}' contains no numbered clauses (lines starting '1.', "
              + "'2.', …). This system cites the exact clause a policy relies on, so a policy "
              + "without addressable clauses cannot be used. Add clause numbering, or exclude "
              + "the file.");

        var documentTitle = subtitle.Length > 0 ? $"{title} - {subtitle}" : title;
        if (documentTitle.Length == 0) documentTitle = fileName;

        return new PolicyDocument(
            FileName: fileName,
            Title: documentTitle,
            Sections: clauses
                .Select(c => new PolicySection(c.Id, string.Join(" ", c.Lines).Trim()))
                .ToList());
    }
}
