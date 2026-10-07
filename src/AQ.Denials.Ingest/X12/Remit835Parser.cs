using AQ.Denials.Core;

namespace AQ.Denials.Ingest.X12;

/// <summary>A parse-time problem that must surface as an exception row, not as a thrown error.</summary>
public sealed record ParserIssue(string ReasonCode, string Detail, int? SegmentIndex = null);

/// <summary>One signed adjustment parsed out of a <c>CAS</c> segment.</summary>
public readonly record struct ParsedAdjustment(string GroupCode, string Carc, decimal Amount);

/// <summary>One <c>SVC</c> line with its own adjustments.</summary>
public sealed class ParsedService
{
    public string ProcedureCode { get; set; } = string.Empty;
    public decimal Charge { get; set; }
    public decimal Paid { get; set; }
    public List<ParsedAdjustment> Adjustments { get; set; } = new();
    public List<string> Rarcs { get; set; } = new();
}

/// <summary>One <c>CLP</c> occurrence, before it is linked to a canonical claim.</summary>
public sealed class ParsedObservation
{
    public string FileName { get; init; } = string.Empty;
    public int TransactionIndex { get; init; }
    public int SegmentIndex { get; init; }

    public string PayerId { get; set; } = string.Empty;
    public string CheckDate { get; set; } = string.Empty;
    public string RawClaimReference { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public decimal SubmittedCharge { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PatientResponsibility { get; set; }

    /// <summary>
    /// The payer's own control number for this claim. It sits at <c>CLP07</c> in this pack —
    /// verified: 1,167 distinct <c>CLP07</c> values for 1,167 distinct <c>CLP01</c>, strictly 1:1,
    /// whereas <c>CLP06</c> is the constant <c>12</c> on all 1,650 observations. Reading <c>CLP06</c>
    /// would give a natural key that never varies and cannot identify anything.
    /// </summary>
    public string PayerClaimControlNumber { get; set; } = string.Empty;

    /// <summary>The raw <c>CLP06</c> value, kept for traceability.</summary>
    public string Clp06 { get; set; } = string.Empty;

    /// <summary><c>TRN02</c> / check trace for the transaction this claim arrived in.</summary>
    public string TraceNumber { get; set; } = string.Empty;

    public List<ParsedAdjustment> ClaimAdjustments { get; set; } = new();
    public List<string> ClaimRarcs { get; set; } = new();
    public List<ParsedService> Services { get; set; } = new();

    /// <summary>
    /// Claim-level natural key for a given claim identity: payer + check/trace + the payer's own
    /// claim control number + the claim reference itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards against partial overlaps between files that a whole-payload hash cannot see: if two
    /// files disagree somewhere else but carry the <i>same</i> payment event for this claim, this
    /// key collides and the second one is refused rather than counted as new cash.
    /// </para>
    /// <para>
    /// <c>CLP07</c> is used as the control number, not <c>CLP06</c>: verified on this pack,
    /// <c>CLP06</c> is the constant <c>12</c> on all 1,650 observations while <c>CLP07</c> gives
    /// 1,167 distinct values for 1,167 distinct <c>CLP01</c>, strictly 1:1 — so <c>CLP06</c> would
    /// produce a key that never varies and identifies nothing.
    /// </para>
    /// <para>
    /// The claim reference is supplied by the caller as the <b>canonical</b> id rather than the raw
    /// <c>CLP01</c>, so the same claim written as <c>GPP-2026-001234</c> in one file and
    /// <c>GPP2026001234</c> in another still collides. Segment position is deliberately not part of
    /// the key: a claim legitimately appearing twice inside one file is a correction sequence, not
    /// a double count.
    /// </para>
    /// </remarks>
    public string NaturalKeyFor(string claimReference) =>
        $"{PayerId}|{TraceNumber}|{CheckDate}|{PayerClaimControlNumber}|{claimReference}";

    /// <summary>
    /// Deterministic order key: check date → file name → transaction → segment.
    /// File <b>name</b>, not discovery index, so the result cannot depend on the order the
    /// files happened to be read in.
    /// </summary>
    public (string CheckDate, string FileName, int TransactionIndex, int SegmentIndex) SortKey =>
        (CheckDate, FileName, TransactionIndex, SegmentIndex);
}

/// <summary>
/// One transaction set (<c>ST</c>…<c>SE</c>) inside a file: one payer, one check date, one
/// cash total. This is the unit reconciliation actually works at — a single file carries 7–9 of
/// them, each with its own <c>BPR</c>, so a file-level "BPR" is the <b>sum</b> of the
/// transaction <c>BPR</c>s, not the last one seen.
/// </summary>
public sealed class ParsedTransaction
{
    public int TransactionIndex { get; init; }
    public string PayerId { get; set; } = string.Empty;
    public string CheckDate { get; set; } = string.Empty;
    public string TraceNumber { get; set; } = string.Empty;

    /// <summary><c>BPR02</c> cash for this transaction, signed.</summary>
    public decimal BprTotal { get; set; }

    public int ObservationCount { get; set; }

    /// <summary>Σ <c>CLP04</c> across the claims in this transaction. Must equal <see cref="BprTotal"/>.</summary>
    public decimal PaidSum { get; set; }
}

/// <summary>Everything one remittance file yields.</summary>
public sealed class ParsedRemit
{
    public string FileName { get; init; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;

    /// <summary>First payer identifier seen (the file's leading transaction).</summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>
    /// Every distinct payer in the file, in order. A remittance file is <b>not</b> one payer —
    /// <c>era_2026Q1.835</c> carries CSA77, NS401, SMP12 and MRD55 — so reporting a single
    /// "payer for this file" would be wrong.
    /// </summary>
    public List<string> Payers { get; } = new();

    /// <summary>
    /// Representative check date: the <c>DTM*405</c> of the file's first transaction set.
    /// Per-transaction dates are in <see cref="CheckDates"/> and on each observation — a file is
    /// not a single date when it carries 7–9 payments on different days.
    /// </summary>
    public string CheckDate { get; set; } = string.Empty;

    /// <summary>Every distinct check date in file order (one per transaction set).</summary>
    public List<string> CheckDates { get; } = new();

    /// <summary>File cash: the sum of all transaction <c>BPR</c>s.</summary>
    public decimal BprTotal { get; set; }

    public string? TraceNumber { get; set; }

    public int TransactionCount { get; set; }
    public int EnvelopeTransactions { get; set; }

    public List<ParsedTransaction> Transactions { get; } = new();
    public List<ParsedObservation> Observations { get; } = new();
    public List<ParserIssue> Issues { get; } = new();
    public List<string> PlbFacilityIds { get; } = new();
    public decimal PlbTotal { get; set; }
}

/// <summary>
/// Defensive reader for an ASC X12 835 (005010X221A1) electronic remittance advice.
/// </summary>
/// <remarks>
/// Design notes that matter for correctness:
/// <list type="bullet">
/// <item><description>Elements are read with the 1-based spec accessor <c>El(n)</c>, so
/// <c>seg.El(1)</c> is literally <c>CAS01</c>. Mixing 0-based and spec indices is how an
/// off-by-one silently shifts every money field.</description></item>
/// <item><description><c>CAS</c> carries variable-length reason/amount pairs
/// (<c>CAS*CO*45*53.20*97*180.00</c>), so it is walked two elements at a time from CAS02.
/// One regex per segment type truncates at the first pair — tried, and rejected.</description></item>
/// <item><description>Amounts parse as <c>decimal</c> and keep the sign exactly as written.</description></item>
/// <item><description>Envelope count mismatches are warnings, not failures (D11): the cash in a
/// file can reconcile perfectly even when <c>GE01</c> is wrong.</description></item>
/// <item><description>Anything structurally impossible becomes a <see cref="ParserIssue"/>, so the
/// run continues and the row lands in the exceptions view instead of crashing.</description></item>
/// </list>
/// </remarks>
public static class Remit835Parser
{
    /// <param name="fileName">Name used for ordering, reporting and the exceptions view.</param>
    /// <param name="content">Decoded text of the interchange.</param>
    /// <param name="rawBytes">
    /// The file exactly as read from disk. The whole-file hash must be over these bytes, not
    /// over the decoded string: reading text strips a BOM and would otherwise change the hash,
    /// making it impossible to reproduce the published <c>file_sha256</c> values.
    /// </param>
    public static ParsedRemit Parse(string fileName, string content, byte[]? rawBytes = null)
    {
        var result = new ParsedRemit { FileName = fileName };

        var tokenized = X12Tokenizer.Tokenize(content);
        result.FileSha256 = X12Tokenizer.Sha256Hex(rawBytes ?? System.Text.Encoding.UTF8.GetBytes(content));
        result.PayloadSha256 =
            X12Tokenizer.Sha256Hex(X12Tokenizer.NormalizedPayload(tokenized.Segments));

        foreach (var err in tokenized.Errors)
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                err.Message + " Raw: " + Truncate(err.Raw, 80), err.SegmentIndex));

        ParsedObservation? current = null;
        ParsedService? service = null;
        ParsedTransaction? txn = null;
        var currentCheckDate = string.Empty;
        var currentPayerId = string.Empty;
        var currentTrace = string.Empty;
        var txnIndex = -1;
        int stCount = 0, seCount = 0, gsCount = 0, geCount = 0, ieaCount = 0;
        string? isa13 = null, iea01 = null, iea02 = null, ge01 = null;

        foreach (var seg in tokenized.Segments)
        {
            switch (seg.Tag)
            {
                case "ISA":
                    isa13 = seg.El(13);
                    break;

                case "GS": gsCount++; break;

                case "ST":
                    stCount++;
                    txnIndex++;
                    txn = new ParsedTransaction { TransactionIndex = txnIndex };
                    result.Transactions.Add(txn);
                    break;

                case "SE":
                    seCount++;
                    txn = null;                 // transaction closed; later BPR would be a defect
                    break;

                case "GE":
                    geCount++;
                    ge01 = seg.El(1);           // GE01 = number of included transaction sets
                    break;

                case "IEA":
                    ieaCount++;
                    iea01 = seg.El(1);          // IEA01 = number of included functional groups
                    iea02 = seg.El(2);          // IEA02 = interchange control number, pairs with ISA13
                    break;

                case "BPR":
                    // BPR02 = cash for THIS transaction. A file carries one BPR per transaction
                    // set (7–9), so the file total is their SUM — assigning rather than adding
                    // would silently keep only the last payment.
                    if (seg.El(2) is { } bpr)
                    {
                        try
                        {
                            var amount = Money.ParseX12(bpr);
                            result.BprTotal += amount;
                            if (txn is not null) txn.BprTotal += amount;
                        }
                        catch (FormatException ex)
                        {
                            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                                "BPR02 is not a decimal: " + ex.Message, seg.Index));
                        }
                    }
                    break;

                case "TRN":
                    currentTrace = seg.El(2) ?? string.Empty;
                    result.TraceNumber ??= currentTrace;
                    if (txn is not null) txn.TraceNumber = currentTrace;
                    break;

                case "DTM":
                    // DTM*405 = check date, the anchor for every appeal/corrected window (A1).
                    // Exactly one per transaction set — verified 7/9/9/9 == ST counts.
                    if (seg.El(1) == "405")
                    {
                        currentCheckDate = ToIsoDate(seg.El(2) ?? string.Empty, seg.Index, result);
                        if (result.CheckDate.Length == 0) result.CheckDate = currentCheckDate;
                        if (!result.CheckDates.Contains(currentCheckDate))
                            result.CheckDates.Add(currentCheckDate);
                        if (txn is not null) txn.CheckDate = currentCheckDate;
                    }
                    break;

                case "REF":
                    // REF*2U = payer identifier, one per transaction. A single file may carry
                    // several payers (era_2026Q1 has CSA77, NS401, SMP12 and MRD55).
                    if (seg.El(1) == "2U")
                    {
                        currentPayerId = seg.El(2) ?? string.Empty;
                        if (result.PayerId.Length == 0) result.PayerId = currentPayerId;
                        if (!result.Payers.Contains(currentPayerId)) result.Payers.Add(currentPayerId);
                        if (txn is not null) txn.PayerId = currentPayerId;
                    }
                    break;

                case "CLP":
                    current = StartObservation(seg, result, currentCheckDate, currentPayerId,
                        currentTrace, txnIndex);
                    if (txn is not null)
                    {
                        txn.ObservationCount++;
                        txn.PaidSum += current.PaidAmount;
                    }
                    service = null;
                    break;

                case "SVC":
                    if (current is null)
                    {
                        result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                            "SVC segment appears outside any CLP loop.", seg.Index));
                        break;
                    }
                    service = StartService(seg, tokenized.ComponentSeparator, result);
                    current.Services.Add(service);
                    break;

                case "CAS":
                    HandleCas(seg, current, service, result);
                    break;

                case "LQ":
                    // LQ01 is the code-list qualifier (HE), LQ02 is the remark code itself.
                    if (seg.El(2) is { } lq)
                    {
                        if (service is not null) service.Rarcs.Add(lq);
                        else current?.ClaimRarcs.Add(lq);
                    }
                    break;

                case "PLB":
                    // Provider-level adjustments. This pack contains none, but the parser reports
                    // what it sees so the report proves absence rather than assuming it (A7).
                    if (seg.El(1) is { } plb) result.PlbFacilityIds.Add(plb);
                    if (seg.El(2) is { } plbAmt)
                    {
                        try { result.PlbTotal += Money.ParseX12(plbAmt); }
                        catch (FormatException ex)
                        {
                            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                                "PLB02 amount is invalid: " + ex.Message, seg.Index));
                        }
                    }
                    break;
            }
        }

        result.TransactionCount = stCount;
        result.EnvelopeTransactions = geCount;

        /* ---- envelope integrity: warnings, never failures (D11) ------------ */
        // Which count pairs with which field:
        //   GE01  = number of transaction sets inside the functional group -> ST count
        //   IEA01 = number of included functional groups                   -> GS count
        //   IEA02 = interchange control number                             -> ISA13
        // Comparing IEA01 with ISA13 matches a count against an id: always
        // "wrong", always noise, and it hides the defects that do matter.
        if (seCount != stCount)
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                $"ST count {stCount} does not match SE count {seCount}."));
        if (geCount != gsCount)
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                $"GS segment count {gsCount} does not match GE segment count {geCount}."));
        if (ge01 is not null && (!int.TryParse(ge01, out var geDeclared) || geDeclared != stCount))
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                $"GE01 declares {ge01} transaction set(s) but the file holds {stCount} ST segment(s)."));
        if (iea01 is not null && (!int.TryParse(iea01, out var ieaDeclared) || ieaDeclared != gsCount))
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                $"IEA01 declares {iea01} functional group(s) but the file holds {gsCount} GS segment(s)."));
        if (iea02 is not null && isa13 is not null && iea02 != isa13)
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                $"ISA13 '{isa13}' does not match IEA02 '{iea02}'."));
        if (ieaCount == 0)
            result.Issues.Add(new ParserIssue(ExceptionReason.EnvelopeDefect,
                "Interchange has no IEA trailer."));

        if (result.PayerId.Length == 0)
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "No REF*2U payer identifier found; payer cannot be determined from the file."));
        if (result.CheckDates.Count == 0)
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "No DTM*405 check date found; deadlines cannot be anchored for this file."));

        return result;
    }

    private static ParsedObservation StartObservation(X12Segment seg, ParsedRemit result,
        string checkDate, string payerId, string trace, int txnIndex)
    {
        var rawRef = seg.El(1);
        if (rawRef is null)
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "CLP01 (claim reference) is missing.", seg.Index));

        var status = seg.El(2);
        if (status is null)
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "CLP02 (claim status code) is missing.", seg.Index));

        var obs = new ParsedObservation
        {
            FileName = result.FileName,
            TransactionIndex = txnIndex,
            SegmentIndex = seg.Index,
            PayerId = payerId,
            CheckDate = checkDate,
            RawClaimReference = rawRef ?? string.Empty,
            StatusCode = status ?? string.Empty,
            Clp06 = seg.ElOrEmpty(6),
            PayerClaimControlNumber = seg.El(7) ?? seg.El(6) ?? string.Empty,
            TraceNumber = trace,
        };

        obs.SubmittedCharge = ReadMoney(seg, 3, result, "CLP03");
        obs.PaidAmount = ReadMoney(seg, 4, result, "CLP04");
        obs.PatientResponsibility = ReadMoney(seg, 5, result, "CLP05");

        result.Observations.Add(obs);
        return obs;
    }

    private static ParsedService StartService(X12Segment seg, char componentSep, ParsedRemit result)
    {
        var raw = seg.El(1) ?? string.Empty;
        var code = raw;
        var ci = raw.IndexOf(componentSep);
        if (ci >= 0) code = raw[(ci + 1)..];       // "HC:99232" -> "99232"

        var svc = new ParsedService { ProcedureCode = code };
        svc.Charge = ReadServiceMoney(seg, 2, result, "SVC02");
        svc.Paid = ReadServiceMoney(seg, 3, result, "SVC03");
        return svc;
    }

    /// <summary>
    /// Walk <c>CAS</c> reason/amount pairs: CAS01 = group, then CAS02/CAS03, CAS04/CAS05, …
    /// Two elements at a time, because the list is variable length.
    /// </summary>
    private static void HandleCas(X12Segment seg, ParsedObservation? obs, ParsedService? service,
        ParsedRemit result)
    {
        if (obs is null)
        {
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "CAS segment appears outside any CLP loop.", seg.Index));
            return;
        }

        var group = seg.El(1);
        if (group is null)
        {
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                "CAS01 (adjustment group code) is missing.", seg.Index));
            return;
        }

        if (seg.Elements.Length < 3)
        {
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                $"CAS has no reason/amount pair: '{seg.Text}'.", seg.Index));
            return;
        }

        for (var i = 1; i + 1 < seg.Elements.Length; i += 2)
        {
            var carc = seg.Elements[i];
            var amountToken = seg.Elements[i + 1];

            if (carc.Length == 0)
            {
                result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                    $"CAS reason code at CAS{i + 1} is empty.", seg.Index));
                continue;
            }

            decimal amount;
            try { amount = Money.ParseX12(amountToken); }
            catch (FormatException ex)
            {
                result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                    $"CAS amount for reason {carc} is invalid: {ex.Message}", seg.Index));
                continue;
            }

            var adj = new ParsedAdjustment(group, carc, amount);
            if (service is not null) service.Adjustments.Add(adj);
            else obs.ClaimAdjustments.Add(adj);
        }
    }

    private static decimal ReadMoney(X12Segment seg, int specIndex, ParsedRemit result, string field)
    {
        var token = seg.El(specIndex);
        if (token is null) return 0m;
        try { return Money.ParseX12(token); }
        catch (FormatException ex)
        {
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                $"{field} is invalid: {ex.Message}", seg.Index));
            return 0m;
        }
    }

    private static decimal ReadServiceMoney(X12Segment seg, int specIndex, ParsedRemit result, string field)
    {
        var token = seg.El(specIndex);
        if (token is null) return 0m;
        try { return Money.ParseX12(token); }
        catch (FormatException ex)
        {
            result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
                $"{field} is invalid: {ex.Message}", seg.Index));
            return 0m;
        }
    }

    /// <summary>Normalise <c>YYYYMMDD</c> to <c>yyyy-MM-dd</c>; leave anything else alone.</summary>
    private static string ToIsoDate(string value, int segIndex, ParsedRemit result)
    {
        if (value.Length == 8 && value.All(char.IsDigit))
            return $"{value[..4]}-{value[4..6]}-{value[6..]}";

        result.Issues.Add(new ParserIssue(ExceptionReason.MalformedSegment,
            $"DTM*405 check date '{value}' is not YYYYMMDD.", segIndex));
        return value;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
