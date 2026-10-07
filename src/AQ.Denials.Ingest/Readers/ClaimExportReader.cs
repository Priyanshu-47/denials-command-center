using AQ.Denials.Core;
using AQ.Denials.Core.Domain;

namespace AQ.Denials.Ingest.Readers;

/// <summary>Reads <c>claims_export.csv</c>, one row per claim line, into canonical claims.</summary>
/// <remarks>
/// The README's column list is the contract: 25 columns, verified to match the file exactly.
/// Rows are grouped into claims by <see cref="ClaimIdNormaliser"/> so the same claim id in four
/// different shapes cannot become four claims. A line whose id cannot be canonicalised is
/// returned as an exception rather than dropped or guessed at.
/// </remarks>
public static class ClaimExportReader
{
    /// <summary>One input line, before the pipeline knows whether its claim got adjudicated.</summary>
    public sealed record ClaimLineRef(
        int RowNumber,
        string RawClaimId,
        ClaimIdKind Kind,
        string? CanonicalId,
        decimal Charge);

    public sealed record Result(
        IReadOnlyList<Claim> Claims,
        IReadOnlyList<ClaimLineRef> Lines,
        int LinesIn);

    public static Result Read(string content)
    {
        var table = Csv.Parse(content);

        var required = new[]
        {
            "claim_id", "patient_first", "patient_last", "patient_dob", "member_id",
            "payer", "payer_id", "dos", "submitted_date", "rendering_npi",
            "rendering_provider", "facility", "pos", "line_no", "cpt", "modifier",
            "units", "charge", "dx1", "dx2", "dx3", "dx4", "auth_number", "coder_id",
            "prebill_reviewed",
        };

        foreach (var col in required)
            if (table.IndexOf(col) < 0)
                throw new InvalidDataException(
                    $"claims_export.csv is missing the '{col}' column. Columns found: "
                  + string.Join(", ", table.Header));

        var byId = new Dictionary<string, Claim>(StringComparer.Ordinal);
        var order = new List<string>();
        var lines = new List<ClaimLineRef>();
        var linesIn = 0;

        foreach (var row in table.AsDictionaries())
        {
            linesIn++;
            var id = ClaimIdNormaliser.Normalise(row["claim_id"]);
            var charge = Money.ParseLoose(row["charge"]) ?? 0m;

            lines.Add(new ClaimLineRef(linesIn, row["claim_id"], id.Kind,
                id.IsMatched ? id.CanonicalId : null, charge));

            if (!id.IsMatched)
            {
                // Never fold an unrecognisable reference into a claim we would then have to
                // guess the identity of. The line is kept as a disposition so it is accounted
                // for rather than dropped.
                continue;
            }

            if (!byId.TryGetValue(id.CanonicalId!, out var claim))
            {
                claim = new Claim { ClaimId = id.CanonicalId!, Charge = 0m };
                ApplyHeader(claim, row);
                byId[claim.ClaimId] = claim;
                order.Add(claim.ClaimId);
            }

            claim.Lines.Add(new ClaimLine
            {
                LineNo = ParseInt(row["line_no"], 1),
                Cpt = row["cpt"].Trim(),
                Modifier = NullIfBlank(row["modifier"]),
                Units = ParseInt(row["units"], 1),
                Charge = charge,
                Dx1 = NullIfBlank(row["dx1"]),
                Dx2 = NullIfBlank(row["dx2"]),
                Dx3 = NullIfBlank(row["dx3"]),
                Dx4 = NullIfBlank(row["dx4"]),
            });

            claim.Charge += charge;
        }

        // Deterministic line order within each claim (line_no, then cpt).
        foreach (var claim in byId.Values)
            claim.Lines.Sort((a, b) => a.LineNo != b.LineNo
                ? a.LineNo.CompareTo(b.LineNo)
                : string.CompareOrdinal(a.Cpt, b.Cpt));

        return new Result(order.Select(k => byId[k]).ToList(), lines, linesIn);
    }

    private static void ApplyHeader(Claim claim, IReadOnlyDictionary<string, string> row)
    {
        claim.PayerId = row["payer_id"].Trim();
        claim.PayerName = row["payer"].Trim();
        claim.PatientFirst = row["patient_first"].Trim();     // PHI
        claim.PatientLast = row["patient_last"].Trim();       // PHI
        claim.PatientDob = row["patient_dob"].Trim();         // PHI
        claim.MemberId = row["member_id"].Trim();             // PHI
        claim.Dos = row["dos"].Trim();
        claim.SubmittedDate = row["submitted_date"].Trim();
        claim.RenderingNpi = row["rendering_npi"].Trim();
        claim.RenderingProvider = row["rendering_provider"].Trim();
        claim.Facility = row["facility"].Trim();
        claim.Pos = row["pos"].Trim();
        claim.AuthNumber = NullIfBlank(row["auth_number"]);
        claim.CoderId = NullIfBlank(row["coder_id"]);
        claim.PrebillReviewed = row["prebill_reviewed"].Trim();
    }

    private static string? NullIfBlank(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int ParseInt(string s, int fallback) =>
        int.TryParse(s.Trim(), out var v) ? v : fallback;
}
