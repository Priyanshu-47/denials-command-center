using System.Text;

namespace AQ.Denials.Ingest.Readers;

/// <summary>
/// Minimal, correct CSV reader (RFC 4180): quoted fields, embedded commas, doubled quotes,
/// CR/LF and LF line endings, and a leading BOM.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled from a package because a healthcare CSV with a quoted
/// facility name such as <c>"Seabreeze Rehab &amp; Nursing"</c> is exactly the input that makes a
/// naive <c>Split(',')</c> shift every column after it — and a shifted column silently moves
/// money from one claim to another.
/// </remarks>
public static class Csv
{
    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
    {
        public int IndexOf(string column) =>
            Header.ToList().FindIndex(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));

        /// <summary>Rows as dictionaries keyed by the header. Missing cells become "".</summary>
        public IEnumerable<IReadOnlyDictionary<string, string>> AsDictionaries()
        {
            foreach (var row in Rows)
            {
                var d = new Dictionary<string, string>(Header.Count, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < Header.Count; i++)
                    d[Header[i]] = i < row.Count ? row[i] : string.Empty;
                yield return d;
            }
        }
    }

    public static Table Parse(string text)
    {
        text = text.TrimStart('﻿');

        var rows = new List<IReadOnlyList<string>>();
        var field = new StringBuilder();
        var current = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    current.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;                      // handled by '\n'
                case '\n':
                    current.Add(field.ToString());
                    field.Clear();
                    rows.Add(current);
                    current = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            rows.Add(current);
        }

        // Drop trailing blank lines.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);

        if (rows.Count == 0) return new Table(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var header = rows[0];
        var body = rows.Skip(1)
            .Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v)))
            .ToList();

        return new Table(header, body);
    }
}
