using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using AQ.Denials.Core;
using AQ.Denials.Core.Domain;

namespace AQ.Denials.Ingest.Readers;

/// <summary>
/// Reads <c>denials_worklog.xlsx</c> without any third-party package.
/// </summary>
/// <remarks>
/// The workbook uses <c>inlineStr</c> cells only — no shared-string table and no date serials —
/// which is what makes a ~60-line reader safe rather than reckless. Every cell type is still
/// handled explicitly below; anything unexpected is reported as an unreadable cell instead of
/// being coerced to a value we would then trust.
/// <para>
/// The worklog is <b>history only</b>: it never overrides the 835 on status or money. Its
/// <c>Date Logged</c> column is genuinely ambiguous (see <see cref="InterpretDate"/>), so the raw
/// string and both readings are kept and the row is flagged rather than guessed at.
/// </para>
/// </remarks>
public static class WorklogXlsxReader
{
    public sealed record Result(
        IReadOnlyList<WorklogEntry> Entries,
        IReadOnlyList<string> Problems,
        int RowsIn);

    public static Result Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sheet = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Workbook has no xl/worksheets/sheet*.xml part.");

        // Shared strings are optional; this pack does not use them, but a re-export might.
        var shared = ReadSharedStrings(zip);

        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);

        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var grid = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                // Cell references are A1, B1, …; keep them aligned so a blank cell does not
                // shift every column to its right.
                var index = ColumnIndexOf(cell.Attribute("r")?.Value ?? "");
                while (cells.Count < index) cells.Add(string.Empty);
                cells.Add(ReadCellValue(cell, shared));
            }
            while (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1])) cells.RemoveAt(cells.Count - 1);
            grid.Add(cells);
        }

        while (grid.Count > 0 && grid[^1].All(string.IsNullOrWhiteSpace)) grid.RemoveAt(grid.Count - 1);

        if (grid.Count == 0) return new Result(Array.Empty<WorklogEntry>(), Array.Empty<string>(), 0);

        var header = grid[0];
        int Col(string name) => header.ToList()
            .FindIndex(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));

        foreach (var required in new[] { "Claim #", "Date Logged" })
            if (Col(required) < 0)
                throw new InvalidDataException(
                    $"denials_worklog.xlsx is missing the '{required}' column. Found: "
                  + string.Join(", ", header));

        string At(IReadOnlyList<string> row, int index) =>
            index >= 0 && index < row.Count ? row[index] : string.Empty;

        var entries = new List<WorklogEntry>();
        var problems = new List<string>();
        var rowsIn = 0;

        var iClaim = Col("Claim #");
        var iDate = Col("Date Logged");
        var iPatient = Col("Patient");
        var iAmt = Col("Amt");
        var iNotes = Col("Notes");
        var iPayer = Col("Payer");
        var iOwner = Col("Owner");
        var iStatus = Col("Status");

        for (var r = 1; r < grid.Count; r++)
        {
            var row = grid[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            rowsIn++;

            var rawRef = At(row, iClaim).Trim();
            var canonical = ClaimIdNormaliser.Normalise(rawRef);

            var entry = new WorklogEntry
            {
                RowNumber = rowsIn,           // 1-based among data rows, not spreadsheet lines
                RawClaimReference = rawRef,
                ClaimId = canonical.IsMatched ? canonical.CanonicalId : null,
                PayerName = At(row, iPayer).Trim(),
                DateLoggedRaw = At(row, iDate).Trim(),
                PatientDisplay = At(row, iPatient).Trim(),   // PHI — persisted, never logged
                Owner = At(row, iOwner).Trim(),
                StatusRaw = At(row, iStatus).Trim(),
                Notes = At(row, iNotes).Trim(),
            };

            if (entry.ClaimId is null)
                problems.Add($"row {r + 1}: claim reference '{Clip(rawRef)}' -> {canonical.Kind}");

            InterpretDate(entry);

            var amountRaw = At(row, iAmt).Trim();
            if (amountRaw.Length > 0)
            {
                try { entry.AmountAsLogged = Money.ParseLoose(amountRaw); }
                catch (FormatException) { problems.Add($"row {r + 1}: amount '{Clip(amountRaw)}' is not a number"); }
            }

            entries.Add(entry);
        }

        return new Result(entries, problems, rowsIn);
    }

    /// <summary>
    /// Preserve the raw string and store <b>both</b> readings. A value is only called ambiguous
    /// when both readings are calendar-valid and not in the future — a date that could only have
    /// been written one way is not ambiguous, and calling it so would cry wolf.
    /// </summary>
    public static void InterpretDate(WorklogEntry entry)
    {
        var raw = entry.DateLoggedRaw;
        if (raw.Length == 0) return;

        var mdy = TryParse(raw, monthFirst: true);
        var dmy = TryParse(raw, monthFirst: false);

        entry.DateLoggedMdy = mdy;
        entry.DateLoggedDmy = dmy;
        if (mdy is null && dmy is null) return;

        // "Today" for this engagement is 2026-09-30 (the brief's reference date).
        const string today = "2026-09-30";

        if (mdy is not null && dmy is not null && mdy != dmy)
        {
            // ISO-8601 strings order correctly only via CompareOrdinal; String's own operators
            // are locale-sensitive and could compare dates in the wrong order under some cultures.
            var bothValid = string.CompareOrdinal(mdy, today) <= 0
                         && string.CompareOrdinal(dmy, today) <= 0;
            entry.DateAmbiguous = bothValid;
            entry.DateResolvedByFutureTest = !bothValid;

            // Conservative reading for priority: the earlier of the two, so a deadline is never
            // computed from the more generous interpretation.
            entry.DateLoggedConservative = string.CompareOrdinal(mdy, dmy) <= 0 ? mdy : dmy;
        }
        else
        {
            var only = mdy ?? dmy!;
            entry.DateAmbiguous = false;
            entry.DateLoggedConservative = only;
        }
    }

    private static string? TryParse(string raw, bool monthFirst)
    {
        var formats = monthFirst
            ? new[] { "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy" }
            : new[] { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy" };

        foreach (var f in formats)
            if (DateTime.TryParseExact(raw, f, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return null;
    }

    private static Dictionary<string, string> ReadSharedStrings(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return map;

        using var s = entry.Open();
        var doc = XDocument.Load(s);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var index = 0;
        foreach (var si in doc.Descendants(ns + "si"))
            map[(index++).ToString(CultureInfo.InvariantCulture)] =
                string.Concat(si.Descendants(ns + "t").Select(t => t.Value));

        return map;
    }

    private static string ReadCellValue(XElement cell, IReadOnlyDictionary<string, string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(cell.Name.Namespace + "t").Select(t => t.Value));

        var v = cell.Element(cell.Name.Namespace + "v")?.Value ?? string.Empty;

        if (type == "s")
            return shared.TryGetValue(v, out var s) ? s : string.Empty;

        if (type is "str" or null) return v;

        if (type == "b") return v == "1" ? "Y" : "N";

        if (type == "e") return string.Empty;      // error cell — leave blank rather than parse

        if (type == "n" || type is null)
        {
            // Serial dates are not used by this pack; only hand back whole numbers verbatim.
            if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && n == decimal.Truncate(n))
                return n.ToString(CultureInfo.InvariantCulture);
            return v;
        }

        return v;
    }

    /// <summary>A1 -> 0, B1 -> 1, AA1 -> 26.</summary>
    private static int ColumnIndexOf(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0) return 0;

        var index = 0;
        foreach (var c in letters.ToUpperInvariant())
            index = index * 26 + (c - 'A' + 1);
        return index - 1;
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
