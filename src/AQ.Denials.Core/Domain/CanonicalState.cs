using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace AQ.Denials.Core.Domain;

/// <summary>
/// Everything an ingest run produces: the canonical books, the exceptions and the row counts.
/// Deliberately an in-memory object rather than a database transaction, so idempotency and
/// order-independence can be tested without a database in the loop.
/// </summary>
public sealed class CanonicalState
{
    public List<Claim> Claims { get; } = new();

    /// <summary>Imported remittance files, in file-name order. Duplicates are retained and flagged.</summary>
    public List<RemitFile> RemitFiles { get; } = new();

    public List<WorklogEntry> Worklog { get; } = new();

    public List<RowDisposition> Dispositions { get; } = new();

    public IEnumerable<ExceptionRow> Exceptions(DateTimeOffset at) =>
        Dispositions.Where(d => d.IsProblem).Select(d => d.ToExceptionRow(at)!);

    public IEnumerable<RemitObservation> AllObservations() =>
        Claims.SelectMany(c => c.Observations);

    /* ---- the headline counts, each computed here once --------------------- */

    public int ClaimLineCount => Dispositions.Count(d => d.Entity == "claim_line");
    public int AdjudicatedClaimCount => Claims.Count(c => c.Adjudicated);
    public int UnadjudicatedClaimCount => Claims.Count(c => !c.Adjudicated);
}

/// <summary>
/// SHA-256 over a canonical serialisation of the full state.
/// </summary>
/// <remarks>
/// <para>
/// Used for two proofs that look similar but mean different things:
/// </para>
/// <list type="bullet">
/// <item><description><b>Idempotency</b> — ingest the same pack twice, checksums must match.</description></item>
/// <item><description><b>Order-independence</b> — ingest the same pack in a different file order,
/// checksums must match. This is what catches code that accidentally depends on read order
/// (a dictionary enumerated in insertion order, a "first one wins" that isn't sorted).</description></item>
/// </list>
/// <para>
/// The serialisation is explicit field-by-field, never <c>ToString()</c> on the object graph:
/// a derived property that changes shape would otherwise silently change every checksum and
/// turn both proofs into noise. Money is written invariant-culture with a fixed scale so
/// <c>1.0</c> and <c>1.00</c> cannot produce different hashes for the same value.
/// </para>
/// </remarks>
public static class StateChecksum
{
    public static string Compute(CanonicalState state)
    {
        var sb = new StringBuilder();

        foreach (var c in state.Claims.OrderBy(c => c.ClaimId, StringComparer.Ordinal))
        {
            sb.Append("C|").Append(c.ClaimId)
              .Append('|').Append(c.PayerId)
              .Append('|').Append(c.Dos)
              .Append('|').Append(c.SubmittedDate)
              .Append('|').Append(Money(c.Charge))
              .Append('|').Append(c.Adjudicated ? 1 : 0)
              .Append('|').Append(c.CurrentStatus ?? "-")
              .Append('|').Append(c.PaidAmount is null ? "-" : Money(c.PaidAmount.Value))
              .Append('|').Append(c.SubmittedCharge is null ? "-" : Money(c.SubmittedCharge.Value))
              .Append('|').Append(c.LastCheckDate ?? "-")
              .Append('|').Append(Money(c.LifetimePaid))
              .Append('|').Append(c.Lines.Count)
              .Append('|').Append(c.Observations.Count)
              .Append('\n');

            foreach (var l in c.Lines.OrderBy(l => l.LineNo).ThenBy(l => l.Cpt, StringComparer.Ordinal))
                sb.Append("L|").Append(c.ClaimId).Append('|').Append(l.LineNo)
                  .Append('|').Append(l.Cpt)
                  .Append('|').Append(l.Units)
                  .Append('|').Append(Money(l.Charge))
                  .Append('|').Append(l.Dx1 ?? "-")
                  .Append('\n');
        }

        foreach (var f in state.RemitFiles.OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            sb.Append("F|").Append(f.FileName)
              .Append('|').Append(f.PayloadSha256)
              .Append('|').Append(f.IsDuplicate ? 1 : 0)
              .Append('|').Append(f.DuplicateOfFileName ?? "-")
              .Append('|').Append(Money(f.BprTotal))
              .Append('|').Append(f.Observations.Count)
              .Append('\n');
        }

        foreach (var o in state.Claims
                     .SelectMany(c => c.Observations)
                     .OrderBy(o => o.Seq))
        {
            sb.Append("O|").Append(o.Seq)
              .Append('|').Append(o.NaturalKey)
              .Append('|').Append(o.StatusCode)
              .Append('|').Append(o.PayerId)
              .Append('|').Append(o.CheckDate)
              .Append('|').Append(Money(o.SubmittedCharge))
              .Append('|').Append(Money(o.PaidAmount))
              .Append('|').Append(Money(o.PatientResponsibility))
              .Append('|').Append(o.Adjustments.Count)
              .Append('|').Append(o.Services.Count)
              .Append('\n');

            foreach (var a in o.Adjustments
                         .OrderBy(a => a.GroupCode, StringComparer.Ordinal)
                         .ThenBy(a => a.Carc, StringComparer.Ordinal)
                         .ThenBy(a => Money(a.Amount), StringComparer.Ordinal))
                sb.Append("A|").Append(o.Seq).Append('|').Append(a.GroupCode)
                  .Append('|').Append(a.Carc)
                  .Append('|').Append(Money(a.Amount))
                  .Append(a.IsServiceLevel ? "|service" : "|claim")
                  .Append('\n');
        }

        foreach (var w in state.Worklog.OrderBy(w => w.RowNumber))
            sb.Append("W|").Append(w.RowNumber)
              .Append('|').Append(w.ClaimId ?? "-")
              .Append('|').Append(w.DateLoggedRaw)
              .Append('|').Append(w.DateLoggedConservative ?? "-")
              .Append('|').Append(w.DateAmbiguous ? 1 : 0)
              .Append('|').Append(w.AmountAsLogged is null ? "-" : Money(w.AmountAsLogged.Value))
              .Append('|').Append(w.StatusRaw)
              .Append('\n');

        foreach (var d in state.Dispositions
                     .OrderBy(d => d.Entity, StringComparer.Ordinal)
                     .ThenBy(d => d.RowNumber))
            sb.Append("X|").Append(d.Entity).Append('|').Append(d.RowNumber)
              .Append('|').Append(d.IsProblem ? 1 : 0)
              .Append('|').Append(d.ReasonCode ?? "-")
              .Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        static string Money(decimal v) =>
            v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
