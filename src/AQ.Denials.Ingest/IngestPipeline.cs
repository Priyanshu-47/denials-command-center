using AQ.Denials.Core;
using AQ.Denials.Core.Domain;
using AQ.Denials.Ingest.Readers;
using AQ.Denials.Ingest.X12;

namespace AQ.Denials.Ingest;

/// <summary>Everything one ingestion run produced.</summary>
public sealed class IngestOutcome
{
    public required CanonicalState State { get; init; }
    public required ReconciliationReport Reconciliation { get; init; }
    public required IReadOnlyList<ExceptionRow> Exceptions { get; init; }
    public required IReadOnlyList<AuditLogEntry> AuditEntries { get; init; }

    /// <summary>Input rows across all sources and entities. Counted independently of the dispositions.</summary>
    public required int RowsIn { get; init; }

    /// <summary>Rows with no problem at all.</summary>
    public required int RowsMatched { get; init; }

    /// <summary>Rows with at least one problem. One per row, never two.</summary>
    public required int RowsExcepted { get; init; }

    /// <summary>Raw input row counts, so the conservation identity can be checked against the sources.</summary>
    public required IReadOnlyDictionary<string, int> RowsInByEntity { get; init; }

    public string Checksum => StateChecksum.Compute(State);

    public IReadOnlyDictionary<string, int> ExceptionCountsByReason =>
        Exceptions
            .GroupBy(e => e.ReasonCode, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>
    /// <c>rows_in = matched + exceptions</c>. Not a formality: it fails if a row was silently
    /// dropped (sum too small) or a row was counted twice (sum too large).
    /// </summary>
    public void AssertRowConservation()
    {
        if (RowsIn != RowsMatched + RowsExcepted)
            throw new InvalidOperationException(
                $"row conservation broken: rows_in {RowsIn} != matched {RowsMatched} "
              + $"+ exceptions {RowsExcepted} (difference {RowsIn - RowsMatched - RowsExcepted}).");

        if (RowsExcepted != Exceptions.Count)
            throw new InvalidOperationException(
                $"rows excepted {RowsExcepted} != exception rows {Exceptions.Count}.");

        foreach (var (entity, expected) in RowsInByEntity)
        {
            var actual = State.Dispositions.Count(d => d.Entity == entity);
            if (actual != expected)
                throw new InvalidOperationException(
                    $"entity '{entity}': expected {expected} input rows from the source, "
                  + $"but {actual} were classified.");
        }
    }
}

/// <summary>
/// Turns the data pack into canonical state: parse, dedup, link, flag, reconcile, and prove that
/// nothing was lost on the way through.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is <b>pure</b> — same input, same output, no database in the loop. That is what
/// makes the two hard guarantees testable: running it twice yields the same
/// <see cref="IngestOutcome.Checksum"/>, and running it with the remittance files handed over in a
/// different order yields the same checksum again. Persisting is a separate step, so a failed
/// database write cannot leave half an ingest behind.
/// </para>
/// <para>
/// Determinism comes from three deliberate choices: remittance files are processed in ordinal file
/// -name order (so "which file is the original" never depends on directory enumeration);
/// observations are sequenced by check date → file name → transaction → segment rather than by
/// arrival; and every grouping key is an ordinal string, never a locale-sensitive comparison.
/// </para>
/// </remarks>
public static class IngestPipeline
{
    public const string EntityClaimLine = "claim_line";
    public const string EntityWorklogRow = "worklog_row";
    public const string EntityRemitFile = "remit_file";
    public const string EntityRemitObservation = "remit_observation";

    /// <summary>The brief's reference date. Used for the exception timestamps so runs are reproducible.</summary>
    public static readonly DateTimeOffset ReferenceNow =
        new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    public static IngestOutcome Run(IngestInput input, DateTimeOffset? now = null)
    {
        var at = now ?? ReferenceNow;

        /* ---- reference tables: read once, used everywhere ------------------ */
        var rules = ReferenceDataReader.ReadPayerRules(input.PayerRulesCsv);
        var windows = ReferenceDataReader.BuildPayerWindows(rules);
        var codeReference = ReferenceDataReader.ReadCodeReference(input.CarcRarcCsv, "CARC");
        var groupMeanings = ReferenceDataReader.ReadAdjustmentGroups(input.AdjustmentGroupsCsv);

        var knownCodes = new HashSet<(string Kind, string Code)>(
            codeReference.Select(c => (c.Kind, c.Code)));

        /* ---- claims and worklog -------------------------------------------- */
        var export = ClaimExportReader.Read(input.ClaimsCsv);
        var claimsById = export.Claims.ToDictionary(c => c.ClaimId, StringComparer.Ordinal);
        var worklog = WorklogXlsxReader.Read(input.WorklogXlsxPath);

        var state = new CanonicalState();
        foreach (var w in worklog.Entries) state.Worklog.Add(w);

        /* ---- remittances ----------------------------------------------------- */
        var payloadOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var naturalKeyOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsedFiles = new List<(RemitFile File, ParsedRemit Parsed, bool Imported)>();
        var links = new Dictionary<ParsedObservation, (RemitObservation Observation, string FileName)>();
        var grossPaid = new Dictionary<string, decimal>(StringComparer.Ordinal);

        var orphanCount = 0;
        var refusedByGuard = 0;
        var fileRow = 0;
        var observationRow = 0;

        // Ordinal name order: deterministic "original" selection for payload duplicates.
        foreach (var source in input.Remits.OrderBy(r => r.FileName, StringComparer.Ordinal))
        {
            fileRow++;
            var parsed = Remit835Parser.Parse(source.FileName, source.Content, source.RawBytes);

            var file = new RemitFile
            {
                FileName = parsed.FileName,
                FileSha256 = parsed.FileSha256,
                PayloadSha256 = parsed.PayloadSha256,
                PayerId = parsed.PayerId,
                CheckDate = parsed.CheckDate,
                BprTotal = parsed.BprTotal,
                TraceNumber = parsed.TraceNumber,
                ClaimCount = parsed.Observations.Count,
            };
            state.RemitFiles.Add(file);

            var reasons = new List<string>();
            string? primary = null;
            string? originalRef = null;

            if (payloadOwner.TryGetValue(parsed.PayloadSha256, out var owner))
            {
                // The re-sent file is kept, flagged and pointed at its original. It is not
                // dropped (that would hide the resend) and none of its cash is counted.
                file.IsDuplicate = true;
                file.DuplicateOfFileName = owner;
                primary = ExceptionReason.DuplicatePayload;
                originalRef = owner;
                reasons.Add($"duplicate remit payload of {owner}");
            }
            else
            {
                payloadOwner[parsed.PayloadSha256] = parsed.FileName;

                foreach (var issue in parsed.Issues)
                {
                    reasons.Add(issue.Detail);
                    primary ??= issue.ReasonCode;
                }

                foreach (var parsedObservation in parsed.Observations)
                {
                    observationRow++;
                    var (row, claim, observation) = ClassifyObservation(
                        parsedObservation, parsed.FileName, claimsById, naturalKeyOwner,
                        knownCodes, links, ref orphanCount, ref refusedByGuard);

                    state.Dispositions.Add(row with { RowNumber = observationRow });
                    if (observation is null) continue;

                    // The observation belongs to both books at once: the claim's history and
                    // the file that reported it. Attaching to only one of them leaves a claim
                    // looking unadjudicated while the file counts its cash.
                    claim!.Observations.Add(observation);
                    file.Observations.Add(observation);
                }
            }

            state.Dispositions.Add(new RowDisposition
            {
                SourceFile = parsed.FileName,
                Entity = EntityRemitFile,
                RowNumber = fileRow,
                IsProblem = primary is not null,
                ReasonCode = primary,
                Detail = reasons.Count == 0 ? string.Empty : string.Join("; ", reasons),
                OriginalRef = originalRef,
            });

            parsedFiles.Add((file, parsed, !file.IsDuplicate));

            // Every file's paid total, duplicate or not: each file must reconcile to $0.00 on
            // its own. Whether its cash counts is a separate, explicit decision below.
            foreach (var o in parsed.Observations)
                grossPaid[parsed.FileName] = grossPaid.GetValueOrDefault(parsed.FileName) + o.PaidAmount;
        }

        AssignSequences(links);

        // A payer with no configured window would silently disable every deadline for its
        // claims, so an unknown payer fails the run instead of producing plausible-looking dates.
        foreach (var payerId in parsedFiles
                     .SelectMany(f => f.Parsed.Payers)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(p => p, StringComparer.Ordinal))
            windows.Get(payerId);

        /* ---- canonical claim state, derived solely from the 835 ------------- */
        foreach (var claim in claimsById.Values)
        {
            claim.Observations.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            if (claim.Observations.Count == 0) { claim.Adjudicated = false; continue; }

            var latest = claim.Observations[^1];
            claim.Adjudicated = true;
            claim.CurrentStatus = latest.StatusCode;
            claim.PaidAmount = latest.PaidAmount;
            claim.SubmittedCharge = latest.SubmittedCharge;
            claim.LastCheckDate = latest.CheckDate;
            claim.LifetimePaid = claim.Observations.Sum(o => o.PaidAmount);
        }

        foreach (var claim in claimsById.Values) state.Claims.Add(claim);

        /* ---- worklog rows ----------------------------------------------------- */
        foreach (var entry in state.Worklog)
        {
            var reasons = new List<string>();
            string? primary = null;

            if (entry.ClaimId is null)
            {
                primary = ExceptionReason.UnmatchedWorklogRow;
                reasons.Add($"claim reference '{Clip(entry.RawClaimReference)}' cannot be canonicalised");
            }
            else if (!claimsById.ContainsKey(entry.ClaimId))
            {
                primary = ExceptionReason.UnmatchedWorklogRow;
                reasons.Add($"claim {entry.ClaimId} is not in claims_export.csv");
            }

            if (entry.DateAmbiguous)
            {
                primary ??= ExceptionReason.AmbiguousDate;
                reasons.Add($"date '{entry.DateLoggedRaw}' reads as {entry.DateLoggedMdy} or "
                          + $"{entry.DateLoggedDmy}; using {entry.DateLoggedConservative} for priority only");
            }

            state.Dispositions.Add(new RowDisposition
            {
                SourceFile = Path.GetFileName(input.WorklogXlsxPath),
                Entity = EntityWorklogRow,
                RowNumber = entry.RowNumber,
                IsProblem = primary is not null,
                ReasonCode = primary,
                Detail = reasons.Count == 0 ? string.Empty : string.Join("; ", reasons),
                ClaimId = entry.ClaimId,
            });
        }

        /* ---- claim-export lines ------------------------------------------------ */
        var chargeMismatchClaims = 0;
        foreach (var line in export.Lines)
        {
            var reasons = new List<string>();
            string? primary = null;

            if (line.Kind != ClaimIdKind.Matched || line.CanonicalId is null)
            {
                primary = line.Kind == ClaimIdKind.Foreign
                    ? ExceptionReason.ForeignClaimNamespace
                    : ExceptionReason.ForeignClaimNamespace;
                reasons.Add($"claim_id '{Clip(line.RawClaimId)}' -> {line.Kind}");
            }
            else if (!claimsById.TryGetValue(line.CanonicalId, out var claim))
            {
                primary = ExceptionReason.ForeignClaimNamespace;
                reasons.Add($"claim {line.CanonicalId} could not be assembled from the export");
            }
            else if (!claim.Adjudicated)
            {
                primary = ExceptionReason.UnadjudicatedClaim;
                reasons.Add($"claim {claim.ClaimId} has no remittance in any imported file");
            }
            else if (!claim.Observations.Any(o => o.SubmittedCharge == claim.Charge))
            {
                primary = ExceptionReason.ReconciliationMismatch;
                reasons.Add($"export charge {claim.Charge:0.00} matches no remitted CLP03 ("
                          + string.Join(", ",
                              claim.Observations.Select(o => o.SubmittedCharge.ToString("0.00")))
                          + ")");
                chargeMismatchClaims++;
            }

            state.Dispositions.Add(new RowDisposition
            {
                SourceFile = "claims_export.csv",
                Entity = EntityClaimLine,
                RowNumber = line.RowNumber,
                IsProblem = primary is not null,
                ReasonCode = primary,
                Detail = reasons.Count == 0 ? string.Empty : string.Join("; ", reasons),
                ClaimId = line.CanonicalId,
            });
        }

        /* ---- conservation, then reconciliation --------------------------------- */
        var rowsInByEntity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [EntityClaimLine] = export.LinesIn,
            [EntityWorklogRow] = worklog.RowsIn,
            [EntityRemitFile] = input.Remits.Count,
            [EntityRemitObservation] = observationRow,
        };

        var rowsIn = rowsInByEntity.Values.Sum();
        var rowsExcepted = state.Dispositions.Count(d => d.IsProblem);
        var rowsMatched = state.Dispositions.Count - rowsExcepted;

        var reconciliation = BuildReconciliation(state, parsedFiles, grossPaid, groupMeanings,
            links.Keys, orphanCount, refusedByGuard, chargeMismatchClaims);

        var exceptions = state.Exceptions(at).ToList();
        var audit = BuildAudit(at, state, reconciliation, rowsIn, rowsMatched, rowsExcepted);

        return new IngestOutcome
        {
            State = state,
            Reconciliation = reconciliation,
            Exceptions = exceptions,
            AuditEntries = audit,
            RowsIn = rowsIn,
            RowsMatched = rowsMatched,
            RowsExcepted = rowsExcepted,
            RowsInByEntity = rowsInByEntity,
        };
    }

    /// <summary>
    /// Classify one <c>CLP</c> occurrence. Returns the disposition and, when the observation is
    /// accepted, the claim it belongs to plus the row to persist.
    /// </summary>
    private static (RowDisposition Row, Claim? Claim, RemitObservation? Observation)
        ClassifyObservation(
            ParsedObservation parsed,
            string fileName,
            IReadOnlyDictionary<string, Claim> claimsById,
            IDictionary<string, string> naturalKeyOwner,
            IReadOnlySet<(string Kind, string Code)> knownCodes,
            IDictionary<ParsedObservation, (RemitObservation Observation, string FileName)> links,
            ref int orphanCount,
            ref int refusedByGuard)
    {
        var reasons = new List<string>();
        string? primary = null;
        string? claimId = null;
        string? originalRef = null;
        Claim? ownerClaim = null;
        RemitObservation? accepted = null;

        var norm = ClaimIdNormaliser.Normalise(parsed.RawClaimReference);

        if (!norm.IsMatched)
        {
            primary = ExceptionReason.ForeignClaimNamespace;
            reasons.Add($"CLP01 '{Clip(parsed.RawClaimReference)}' -> {norm.Kind}");
        }
        else if (!claimsById.TryGetValue(norm.CanonicalId!, out ownerClaim))
        {
            primary = ExceptionReason.OrphanRemit;
            reasons.Add($"claim {norm.CanonicalId} is not in claims_export.csv");
            claimId = norm.CanonicalId;
            orphanCount++;
        }
        else
        {
            var naturalKey = parsed.NaturalKeyFor(norm.CanonicalId!);
            if (naturalKeyOwner.TryGetValue(naturalKey, out var owner) && owner != fileName)
            {
                // Same payment event already accepted from a different file: refuse it rather
                // than count the same dollars twice. Within a file a repeat is a correction,
                // not a resend.
                primary = ExceptionReason.DuplicateClaimObservation;
                reasons.Add($"natural key already accepted from {owner}");
                originalRef = owner;
                claimId = norm.CanonicalId;
                refusedByGuard++;
            }
            else
            {
                naturalKeyOwner[naturalKey] = fileName;
                claimId = norm.CanonicalId;

                var unmapped = Codes(parsed, "CARC", knownCodes)
                    .Concat(Codes(parsed, "RARC", knownCodes))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList();

                if (unmapped.Count > 0)
                {
                    primary = ExceptionReason.UnmappedCarcRarc;
                    reasons.Add("not in carc_rarc_reference.csv: " + string.Join(", ", unmapped));
                }

                var casSum = parsed.ClaimAdjustments.Sum(a => a.Amount)
                           + parsed.Services.Sum(s => s.Adjustments.Sum(a => a.Amount));

                if (parsed.SubmittedCharge != parsed.PaidAmount + casSum)
                {
                    primary ??= ExceptionReason.AdjustmentSumMismatch;
                    reasons.Add($"CLP03 {parsed.SubmittedCharge:0.00} != CLP04 {parsed.PaidAmount:0.00} "
                              + $"+ signed CAS {casSum:0.00}");
                }

                accepted = Materialise(parsed, naturalKey);
                links[parsed] = (accepted, fileName);
            }
        }

        var row = new RowDisposition
        {
            SourceFile = fileName,
            Entity = EntityRemitObservation,
            RowNumber = 0,                          // filled in by the caller
            IsProblem = primary is not null,
            ReasonCode = primary,
            Detail = reasons.Count == 0 ? string.Empty : string.Join("; ", reasons),
            ClaimId = claimId,
            OriginalRef = originalRef,
        };

        return (row, ownerClaim, accepted);
    }

    private static IEnumerable<string> Codes(
        ParsedObservation parsed, string kind, IReadOnlySet<(string Kind, string Code)> known)
    {
        if (kind == "CARC")
        {
            foreach (var a in parsed.ClaimAdjustments)
                if (!known.Contains(("CARC", a.Carc))) yield return $"CARC {a.Carc}";
            foreach (var s in parsed.Services)
                foreach (var a in s.Adjustments)
                    if (!known.Contains(("CARC", a.Carc))) yield return $"CARC {a.Carc}";
        }
        else
        {
            foreach (var r in parsed.ClaimRarcs)
                if (!known.Contains(("RARC", r))) yield return $"RARC {r}";
            foreach (var s in parsed.Services)
                foreach (var r in s.Rarcs)
                    if (!known.Contains(("RARC", r))) yield return $"RARC {r}";
        }
    }

    private static RemitObservation Materialise(ParsedObservation parsed, string naturalKey)
    {
        var observation = new RemitObservation
        {
            RawClaimReference = parsed.RawClaimReference,
            StatusCode = parsed.StatusCode,
            SubmittedCharge = parsed.SubmittedCharge,
            PaidAmount = parsed.PaidAmount,
            PatientResponsibility = parsed.PatientResponsibility,
            PayerId = parsed.PayerId,
            CheckDate = parsed.CheckDate,
            NaturalKey = naturalKey,
            RemarkCodes = string.Join(";", parsed.ClaimRarcs),
            Services = new List<ObservationService>(),
        };

        foreach (var adjustment in parsed.ClaimAdjustments)
            observation.Adjustments.Add(new Adjustment
            {
                GroupCode = adjustment.GroupCode,
                Carc = adjustment.Carc,
                Amount = adjustment.Amount,
            });

        foreach (var parsedService in parsed.Services)
        {
            var service = new ObservationService
            {
                ProcedureCode = parsedService.ProcedureCode,
                Charge = parsedService.Charge,
                Paid = parsedService.Paid,
                RemarkCodes = string.Join(";", parsedService.Rarcs),
            };
            observation.Services.Add(service);

            foreach (var adjustment in parsedService.Adjustments)
                observation.Adjustments.Add(new Adjustment
                {
                    GroupCode = adjustment.GroupCode,
                    Carc = adjustment.Carc,
                    Amount = adjustment.Amount,
                    Service = service,
                });
        }

        return observation;
    }

    /// <summary>
    /// Sequence every accepted observation by check date → file name → transaction → segment.
    /// Arrival order never participates, so a different ingestion order cannot change state.
    /// </summary>
    private static void AssignSequences(
        IReadOnlyDictionary<ParsedObservation, (RemitObservation Observation, string FileName)> links)
    {
        var ordered = links.Keys
            .OrderBy(k => k.CheckDate, StringComparer.Ordinal)
            .ThenBy(k => links[k].FileName, StringComparer.Ordinal)
            .ThenBy(k => k.TransactionIndex)
            .ThenBy(k => k.SegmentIndex)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
            links[ordered[i]].Observation.Seq = i;
    }

    private static ReconciliationReport BuildReconciliation(
        CanonicalState state,
        IReadOnlyList<(RemitFile File, ParsedRemit Parsed, bool Imported)> parsedFiles,
        IReadOnlyDictionary<string, decimal> grossPaid,
        IReadOnlyDictionary<string, string> groupMeanings,
        IEnumerable<ParsedObservation> acceptedObservations,
        int orphanCount,
        int refusedByGuard,
        int chargeMismatchClaims)
    {
        var fileLines = new List<ReconciliationReport.FileLine>();
        var payerTotals = new Dictionary<string, (int Txn, decimal Bpr, decimal Paid, int Obs)>(
            StringComparer.Ordinal);

        decimal cashIncluding = 0m, cashExcluding = 0m, duplicateCash = 0m;
        var duplicateFiles = 0;

        foreach (var (file, parsed, imported) in parsedFiles
                     .OrderBy(f => f.File.FileName, StringComparer.Ordinal))
        {
            var paid = grossPaid.GetValueOrDefault(file.FileName);
            cashIncluding += parsed.BprTotal;

            if (!imported)
            {
                duplicateFiles++;
                duplicateCash += parsed.BprTotal;
            }
            else
            {
                cashExcluding += parsed.BprTotal;

                foreach (var txn in parsed.Transactions)
                {
                    var current = payerTotals.GetValueOrDefault(txn.PayerId);
                    payerTotals[txn.PayerId] = (
                        current.Txn + 1,
                        current.Bpr + txn.BprTotal,
                        current.Paid + txn.BprTotal,      // BPR == Σ CLP04 per transaction
                        current.Obs + txn.ObservationCount);
                }
            }

            fileLines.Add(new ReconciliationReport.FileLine(
                file.FileName, !imported, file.DuplicateOfFileName,
                parsed.Transactions.Count, parsed.BprTotal, paid, parsed.Observations.Count));
        }

        var payerLines = payerTotals
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ReconciliationReport.PayerLine(
                kv.Key, kv.Value.Txn, kv.Value.Bpr, kv.Value.Paid, kv.Value.Obs))
            .ToList();

        // Adjustments are summed over every payment event in an imported file, not over the
        // subset we could link to a claim: cash is counted the same way, and two halves of one
        // reconciliation must describe the same population. The unlinked part is reported
        // separately so the difference is visible instead of quietly absent.
        var remitted = parsedFiles
            .Where(f => f.Imported)
            .SelectMany(f => f.Parsed.Observations)
            .ToList();

        var accepted = acceptedObservations.ToHashSet();

        static IEnumerable<Adjustment> Adjustments(ParsedObservation o) =>
            o.ClaimAdjustments.Select(a => new Adjustment
                { GroupCode = a.GroupCode, Carc = a.Carc, Amount = a.Amount })
             .Concat(o.Services.SelectMany(s => s.Adjustments.Select(a => new Adjustment
                 { GroupCode = a.GroupCode, Carc = a.Carc, Amount = a.Amount })));

        var groups = AggregateGroups(remitted.SelectMany(Adjustments), groupMeanings);
        var unattributed = AggregateGroups(
            remitted.Where(o => !accepted.Contains(o)).SelectMany(Adjustments), groupMeanings);

        var adjudicated = state.Claims.Where(c => c.Adjudicated).ToList();
        var unadjudicated = state.Claims.Where(c => !c.Adjudicated).ToList();
        var denied = state.Claims.Where(c => c.CurrentStatus == "4").ToList();

        return new ReconciliationReport
        {
            Files = fileLines,
            Payers = payerLines,
            AdjustmentGroups = groups,
            UnattributedAdjustmentGroups = unattributed,
            CashIncludingDuplicate = cashIncluding,
            CashExcludingDuplicate = cashExcluding,
            DuplicateCashExcluded = duplicateCash,
            DuplicateFiles = duplicateFiles,
            TotalClaims = state.Claims.Count,
            TotalClaimCharge = state.Claims.Sum(c => c.Charge),
            AdjudicatedClaims = adjudicated.Count,
            AdjudicatedCharge = adjudicated.Sum(c => c.Charge),
            UnadjudicatedClaims = unadjudicated.Count,
            UnadjudicatedCharge = unadjudicated.Sum(c => c.Charge),
            DeniedClaims = denied.Count,
            DeniedCharge = denied.Sum(c => c.Charge),
            OrphanObservationCount = orphanCount,
            DuplicateObservationCount = refusedByGuard,
            ChargeMismatchCount = chargeMismatchClaims,
        };
    }

    private static IReadOnlyList<ReconciliationReport.GroupLine> AggregateGroups(
        IEnumerable<Adjustment> adjustments,
        IReadOnlyDictionary<string, string> groupMeanings) =>
        adjustments
            .GroupBy(a => a.GroupCode, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(a => a.Amount))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReconciliationReport.GroupLine(
                g.Key,
                groupMeanings.GetValueOrDefault(g.Key, "(unknown group code)"),
                g.Count(),
                g.Sum(a => a.Amount)))
            .ToList();

    /// <summary>
    /// Audit entries for the run. Claim ids, codes, counts and file names only — no patient
    /// name, date of birth or member id ever reaches the audit trail.
    /// </summary>
    private static IReadOnlyList<AuditLogEntry> BuildAudit(
        DateTimeOffset at,
        CanonicalState state,
        ReconciliationReport reconciliation,
        int rowsIn,
        int rowsMatched,
        int rowsExcepted)
    {
        var entries = new List<AuditLogEntry>
        {
            new()
            {
                At = at,
                Actor = "ingest-pipeline",
                Action = "ingest.completed",
                EntityType = "IngestRun",
                Detail = $"rows_in={rowsIn} matched={rowsMatched} exceptions={rowsExcepted} "
                       + $"claims={state.Claims.Count} cash_excl_duplicate={reconciliation.CashExcludingDuplicate:0.00}",
            },
        };

        foreach (var file in state.RemitFiles.OrderBy(f => f.FileName, StringComparer.Ordinal))
            entries.Add(new AuditLogEntry
            {
                At = at,
                Actor = "ingest-pipeline",
                Action = file.IsDuplicate ? "remit.rejected.duplicate" : "remit.imported",
                EntityType = "RemitFile",
                EntityKey = file.FileName,
                Detail = file.IsDuplicate
                    ? $"duplicate payload of {file.DuplicateOfFileName}; cash excluded"
                    : $"payload={file.PayloadSha256[..16]}… bpr={file.BprTotal:0.00}",
            });

        foreach (var (reason, count) in state.Exceptions(at)
                     .GroupBy(e => e.ReasonCode, StringComparer.Ordinal)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal)
                     .Select(g => (g.Key, g.Count())))
            entries.Add(new AuditLogEntry
            {
                At = at,
                Actor = "ingest-pipeline",
                Action = "exceptions.raised",
                EntityType = "ExceptionRow",
                EntityKey = reason,
                Detail = $"count={count}",
            });

        return entries;
    }

    private static string Clip(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= 40 ? s : s[..40] + "…";
}
