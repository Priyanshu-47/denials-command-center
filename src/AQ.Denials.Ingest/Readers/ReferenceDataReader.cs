using AQ.Denials.Rules;
using AQ.Denials.Rules.Policy;

namespace AQ.Denials.Ingest.Readers;

/// <summary>
/// One row of <c>payer_rules.csv</c>. The columns are named as the pack names them — the three
/// window columns each state their anchor, because an appeal window counted from the denial date
/// and a timely-filing window counted from the date of service are not interchangeable.
/// </summary>
public sealed record PayerRule(
    string PayerId,
    string PayerName,
    int TimelyFilingDaysFromDos,
    int AppealWindowDaysFromDenial,
    int CorrectedClaimWindowDaysFromDenial);

/// <summary>A CARC or RARC code and its plain-English meaning, from the pack's reference CSVs.</summary>
public sealed record ReferenceCode(string Code, string Meaning, string Kind);

/// <summary>
/// Reads the pack's reference tables. These contain <b>no patient data</b> — they are payer
/// policy, code definitions and adjustment-group definitions.
/// </summary>
/// <summary>
/// One expert-labelled row from <c>labeled_denials_sample.csv</c> — the only source in this
/// assignment that speaks to team assignment and pre-bill preventability.
/// </summary>
/// <remarks>
/// The file is an <b>answer key</b>, not an input: nothing in the running system may read it to
/// produce a result for a claim outside the sample. It is read here to check the outcome table
/// against it and by the evaluation harness, never by the pipeline.
/// </remarks>
public sealed record LabeledDenial(
    string ClaimId,
    string RootCauseCategory,
    string OwningTeam,
    bool PreventableAtPrebill);

public static class ReferenceDataReader
{
    public static IReadOnlyList<PayerRule> ReadPayerRules(string csv)
    {
        var table = Csv.Parse(csv);
        var required = new[]
        {
            "payer", "payer_id",
            "timely_filing_days_from_dos",
            "appeal_window_days_from_denial",
            "corrected_claim_window_days_from_denial",
        };
        foreach (var col in required)
            if (table.IndexOf(col) < 0)
                throw new InvalidDataException(
                    $"payer_rules.csv is missing the '{col}' column. Found: "
                  + string.Join(", ", table.Header));

        var rules = new List<PayerRule>();
        foreach (var row in table.AsDictionaries())
        {
            var payerId = row["payer_id"].Trim();
            if (payerId.Length == 0) continue;

            rules.Add(new PayerRule(
                payerId,
                row["payer"].Trim(),
                Days(row["timely_filing_days_from_dos"]),
                Days(row["appeal_window_days_from_denial"]),
                Days(row["corrected_claim_window_days_from_denial"])));
        }

        return rules;
    }

    private static int Days(string token)
    {
        if (!int.TryParse(token.Trim(), out var days) || days <= 0)
            throw new InvalidDataException($"payer_rules.csv window '{token}' is not a positive whole number of days.");
        return days;
    }

    public static IReadOnlyList<ReferenceCode> ReadCodeReference(string csv, string kind)
    {
        var table = Csv.Parse(csv);
        var codeIndex = table.IndexOf("code");
        if (codeIndex < 0)
            throw new InvalidDataException(
                $"{kind} reference CSV has no 'code' column. Found: "
              + string.Join(", ", table.Header));

        var meaningIndex = -1;
        foreach (var candidate in new[] { "description", "meaning", "label", "text" })
        {
            var i = table.IndexOf(candidate);
            if (i >= 0) { meaningIndex = i; break; }
        }

        // The pack's reference carries CARC and RARC rows in one file; keep the distinction
        // rather than flattening it, so "which CARCs are unknown" is answerable.
        var typeIndex = table.IndexOf("type");

        var codes = new List<ReferenceCode>();
        foreach (var row in table.Rows)
        {
            if (row.Count == 0 || string.IsNullOrWhiteSpace(row[codeIndex])) continue;
            var meaning = meaningIndex >= 0 && meaningIndex < row.Count
                ? row[meaningIndex].Trim()
                : string.Empty;
            var rowKind = typeIndex >= 0 && typeIndex < row.Count && row[typeIndex].Trim().Length > 0
                ? row[typeIndex].Trim()
                : kind;
            codes.Add(new ReferenceCode(row[codeIndex].Trim(), meaning, rowKind));
        }

        return codes;
    }

    /// <summary>
    /// The four CARC group codes (<c>CO</c>, <c>OA</c>, <c>PI</c>, <c>PR</c>). Adjustments are
    /// bucketed by this group, and the group's meaning is read rather than hard-coded.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadAdjustmentGroups(string csv)
    {
        var table = Csv.Parse(csv);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (row.Count < 2 || string.IsNullOrWhiteSpace(row[0])) continue;
            map[row[0].Trim()] = row.Count > 1 ? row[1].Trim() : string.Empty;
        }
        return map;
    }

    /// <summary>
    /// Read the 40-row expert answer key. Used by tests and the evaluation harness only.
    /// </summary>
    /// <remarks>
    /// The header is checked rather than assumed because a shifted column would silently turn
    /// every label into a different label — the one failure that would make an evaluation report
    /// confidently wrong instead of merely incomplete.
    /// </remarks>
    /// <exception cref="InvalidDataException">A required column is missing, or a preventability flag is not Yes/No.</exception>
    public static IReadOnlyList<LabeledDenial> ReadLabeledSample(string csv)
    {
        var table = Csv.Parse(csv);
        var required = new[]
        {
            "claim_id", "root_cause_category", "owning_team", "preventable_at_prebill",
        };
        foreach (var col in required)
            if (table.IndexOf(col) < 0)
                throw new InvalidDataException(
                    $"labeled_denials_sample.csv is missing the '{col}' column. Found: "
                  + string.Join(", ", table.Header));

        var rows = new List<LabeledDenial>();
        foreach (var row in table.AsDictionaries())
        {
            var claimId = row["claim_id"].Trim();
            if (claimId.Length == 0) continue;

            var flag = row["preventable_at_prebill"].Trim();
            bool preventable = flag.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? true
                             : flag.Equals("No", StringComparison.OrdinalIgnoreCase) ? false
                             : throw new InvalidDataException(
                                 $"labeled_denials_sample.csv row '{claimId}' has "
                               + $"preventable_at_prebill='{flag}', which is neither Yes nor No. "
                               + "Guessing would corrupt every preventability figure downstream.");

            rows.Add(new LabeledDenial(
                claimId,
                row["root_cause_category"].Trim(),
                row["owning_team"].Trim(),
                preventable));
        }

        return rows;
    }

    /// <summary>
    /// Read and parse every policy markdown file, keyed by file name.
    /// </summary>
    /// <remarks>
    /// Parsing happens here rather than at the point of citation so that a policy the system
    /// cannot address is found at load, with the file named, instead of surfacing later as a
    /// draft that cites a clause number the document does not contain. Raw text is deliberately
    /// not retained: a clause's own words are what a citation is checked against, and keeping a
    /// second copy of the file alongside it invites the two to drift.
    /// </remarks>
    /// <exception cref="InvalidDataException">A file parses to zero addressable clauses.</exception>
    public static IReadOnlyDictionary<string, PolicyDocument> ReadPolicies(string directory)
    {
        var map = new Dictionary<string, PolicyDocument>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return map;

        foreach (var file in Directory.EnumerateFiles(directory, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            map[name] = PolicyParser.Parse(name, File.ReadAllText(file));
        }

        return map;
    }

    /// <summary>Build <see cref="PayerWindows"/> from the rules table (single place to change).</summary>
    public static PayerWindows BuildPayerWindows(IEnumerable<PayerRule> rules)
    {
        var map = new Dictionary<string, PayerWindows.Window>(StringComparer.Ordinal);
        foreach (var r in rules)
            map[r.PayerId] = new PayerWindows.Window(
                AppealDaysFromDenial: r.AppealWindowDaysFromDenial,
                CorrectedDaysFromDenial: r.CorrectedClaimWindowDaysFromDenial,
                TimelyFilingDaysFromDos: r.TimelyFilingDaysFromDos,
                PolicyFile: null);

        return new PayerWindows(map);
    }

    /// <summary>
    /// Map each payer to its policy document. The pack has no policy column, so the association
    /// is derived from the file titles' payer prefix and is asserted by a test — if a new policy
    /// file appears with an unknown prefix the test fails rather than leaving it uncitable.
    /// </summary>
    /// <remarks>
    /// Every payer also follows <c>ALL_PAYERS_MOD25-2026.md</c> by its own preamble. It is
    /// deliberately <b>absent from this map</b>, because the map answers "whose own document is
    /// this?" and that file is nobody's (see Q3). It remains <b>citable</b> by any payer —
    /// <see cref="PolicyLibrary"/> adds it back on the relevance gate, so the two questions stay
    /// separate instead of being conflated into one lookup.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> PolicyFileForPayer(string policyDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NS401"] = "NSHP_HOSP-FREQ-07.md",
            ["CSA77"] = "CSA_PROVIDER-ENROLLMENT.md",
            ["SMP12"] = "SMP_SNF-AUTH-2026.md",
            ["MRD55"] = "MPPO_DX-EXCL-03.md",
        };

        return map;
    }

    /// <summary>
    /// Load the whole policy set and the payer→document ownership into the object that decides
    /// what a given denial may be argued from. One call, so the two halves cannot be wired
    /// together differently in two places.
    /// </summary>
    public static PolicyLibrary BuildPolicyLibrary(string policyDirectory) =>
        new(ReadPolicies(policyDirectory), PolicyFileForPayer(policyDirectory));
}
