# Decision Log

Format: **Decision → Alternatives considered → Why → Trade-off.**
Assumptions are marked **[A#]** and are also listed as questions for the user at the end.
Every number cited here is produced by a script under `tools/` and reproduced in
`docs/DATA_FINDINGS.md`.

---

## Phase 0

### D1. Tooling: Node for profiling, no Python

- **Decision:** all Phase 0 profiling is done with read-only Node ESM scripts in `tools/`.
- **Alternatives:** Python (unavailable — only the Windows Store stub is present); PowerShell
  (workable, and `tools/extract_worklog.ps1` was used for the xlsx, but poor for JSON/text
  processing: PowerShell's `>` writes UTF-16 and chokes on duplicate JSON keys such as
  `"Open"` / `"open"`, which this data has).
- **Why:** Node v24.16.0 is the only working runtime on the machine; ESM scripts keep each
  pass re-runnable and auditable.
- **Trade-off:** the assignment's AI eval harness is specified as "Python if easier". It is
  not easier here, so the eval harness will also be Node unless the user objects. Recorded
  under **[A9]**.

### D2. Claim-identifier canonicalisation

- **Decision:** one function, `canonId`, used everywhere:

  ```
  trim → case-fold GPP prefix
  GPP[-_ ]?YYYY[-_ ]?N(1..6)  → GPP-2026-<N zero-padded to 6>
  N(1..6)                     → GPP-2026-<N zero-padded to 6>
  GPP-2026-NNNNNN             → unchanged
  anything else               → null → exceptions, never guessed
  ```

- **Alternatives:** (a) match on raw string — fails, the same claim has 4 shapes;
  (b) match on patient + DOS + charge — fuzzy, will merge distinct claims; (c) strip all
  non-digits — collapses `GPP-2026-000494` and a bare `000494` but also eats the `2026`.
- **Why:** it is total, reversible, and testable. Result: 1,167 raw remit strings → 1,164
  claims; 0 canonical remit claims missing from the export; 58 export claims never adjudicated.
- **Trade-off:** the foreign `BHC-2026-######` namespace (5 rows, 3 claims) is deliberately
  *not* canonicalised into `GPP`. It goes to exceptions as `FOREIGN_CLAIM_NAMESPACE`.
  Guessing it would fabricate a match.

### D3. Deduplicate the resend by payment-payload hash, not file hash

- **Decision:** compute two hashes per file — `file_sha256` (bytes) and `payload_sha256`
  (all segments outside `ISA`/`GS`/`GE`/`IEA`). Import idempotency and duplicate detection
  key on `payload_sha256`.
- **Alternatives:** (a) file hash — **demonstrably wrong here**; (b) `BPR` trace number —
  works for this pack but is a header field a payer can legitimately change; (c) business key
  (payer + check date + claim set) — expensive and needs an ordering definition.
- **Why:** `era_2026Q2.835` and `era_2026Q2_resent_0719.835` are byte-identical outside the
  four envelope segments (0 differing segments of 3,949) but have **different** whole-file
  SHA-256 (`7a9df943…` vs `86a74d76…`) and **identical** payload SHA-256 (`eda2c706…`).
  A file-hash import would have booked **+$47,461.62 paid, +$89,895.00 charged, +453 claims,
  +41 false denials**.
- **Trade-off:** a payer that re-wraps genuinely different content in the same envelope still
  dedupes correctly (payload differs); a payer that changes one whitespace segment in a resend
  will *not* dedupe — so we also store the payload hash *and* record `DUPLICATE_PAYLOAD`
  exceptions for review rather than silently dropping.

### D4. Observation ordering: check → file → transaction → segment

- **Decision:** "current status of a claim" = the last observation under
  `DTM*405` (check date) → file name → `ST` index → segment index.
- **Alternatives:** (a) file mtime — meaningless, files arrive out of order; (b) check date
  only — ties abound; (c) max(check date, file date) — same problem.
- **Why:** 11 claims carry a `status 22` reversal and a `status 4` re-adjudication in the
  **same transaction of the same file with the same check date** (combined charge $2,225.00).
  Only the segment index separates them. Without it the current status of 11 claims is a coin
  flip and depends on engine sort stability.
- **Trade-off:** the segment index is only stable if we persist it at ingest time. It will be
  stored per observation (`seq`) rather than recomputed.

### D5. Money is integer cents with sign preserved end to end

- **Decision:** `decimal`-free: parse to `long` cents. Accept `-240.00` and `(240.00)` as
  negative. Never take an absolute value of an adjustment. The governing invariant is a plain
  signed sum: `CLP03 = CLP04 + Σ CAS(signed)`.
- **Alternatives:** `decimal` (fine, but cents-as-integer removes any rounding ambiguity in
  C# too and makes equality assertions trivial); `double` (banned by the brief).
- **Why:** two of my own Phase 0 errors came from sign handling (see `DATA_FINDINGS.md` §11,
  M1/M2). After the fix every identity ties to **$0.00**: per file and per payer
  `BPR = Σ CLP04`; `ΣCLP03 − ΣCLP04 = Σ CAS` = $114,159.64 = CO $108,516.40 + PR $5,643.24.
- **Trade-off:** grouping codes other than `CO`/`PR` (`OA`, `PI`) are not present in this pack.
  They must still be parsed and reported as their own rows in the reconciliation report with
  `$0.00`, otherwise the "nothing was silently dropped" claim is unprovable.

### D6. Taxonomy is deterministic; the LLM does not get to invent the category

- **Decision:** `root_cause_category` / `owning_team` / `preventable_at_prebill` come from a
  pure function of `(CARC, RARC, payer, DOS vs policy effective date)`. The LLM is used only
  for (a) appeal-letter drafting with citations, (b) free-text notes on exceptions, (c) a
  second opinion that is *checked against* the rule and reported as agreement/disagreement —
  never as an override.
- **Alternatives:** ask the model for the category — it would score well on the labelled set
  while being unfalsifiable and non-reproducible; rules + model vote — adds latency and a
  source of non-determinism for no gain on this data.
- **Why:** the rules already achieve **40/40 (100%)** against `labeled_denials_sample.csv`.
  A model that "also scores 100%" would be claiming credit for something the rules do. The
  honest eval reports **rules-only as the primary number** and states that this 40-row sample
  cannot demonstrate that the LLM adds value.
- **Trade-off:** we will look less impressive on the headline metric. The brief ranks honesty
  and correctness above polish, and the alternative is a number we cannot defend.

### D7. Reversal and re-adjudication are modelled, not flattened

- **Decision:** keep every observation as an immutable row (`remit_observation`), derive
  current state and a net-cash position from them. Do not overwrite.
- **Alternatives:** keep only the latest row (loses the audit trail and breaks cash
  reconciliation); collapse to a balance (hides the $1,379.50 clawback entirely).
- **Why:** 19 claims have more than one observation: 11 paid→reversed→denied, 6
  denied→recovered, 2 paid twice. Cash (`$118,220.36`) and balance sheet
  (`latestObsPaid $117,637.56`) are different numbers and both are asked for by different
  stakeholders; only an immutable ledger explains the gap.
- **Trade-off:** slightly more storage and one extra join. Worth it — the 2 claims paid twice
  inside one file (net **+$9.20** and **−$15.50** vs charge) are invisible otherwise.

### D8. Two numbers for "denied", both shown

- **Decision:** report `ever_denied = 142` and `open_denied = 136` ($27,780) side by side,
  and treat a service line at $0 on a claim-accepted claim as a **third** figure
  (19 claims / $3,365) rather than folding it in.
- **Alternatives:** one number — the brief's judging criteria punish a wrong number more than
  a complicated one; a single "denials = 142" overstates today's book by $1,110 and 6 claims.
- **Why:** `DATA_FINDINGS.md` §5.3/§5.6. The labelled sample deliberately contains 4
  line-level-only denials, so the product must detect both levels; but silently adding
  $3,365 to the claim-level total would misstate cash at risk.
- **Trade-off:** three numbers on the dashboard instead of one. Mitigated by labelling them
  explicitly: *ever denied / open / line-level only*.

### D9. The worklog is merged as history, never as truth

- **Decision:** `835` is authoritative for status and money. The worklog contributes owner,
  free-text notes and workflow state. Every disagreement becomes an exception row carrying
  **both** values.
- **Alternatives:** trust the worklog (it disagrees on 10 dollar amounts, 25 stale rows, and
  48 open denials are missing from it); discard the worklog (loses owner assignment and notes,
  which the Specialist worklist needs).
- **Why:** 120 rows / 111 claims, 10 raw status strings, 7 raw owner values for 3 people
  (+22 blanks), 42 claim ids needing normalisation, and a date column that mixes day-first and
  month-first. It is a human artifact and it is not a ledger.
- **Trade-off:** the exceptions view will be populated with 25 `WORKLOG_STALE` +
  48 `DENIAL_NOT_LOGGED` + 10 `AMOUNT_MISMATCH` rows on day one. That is the point.

### D10. Parse worklog dates conservatively; never guess

- **Decision:** `yyyy-mm-dd` → trust. `dd/mm/yyyy` where the first part > 12 → day-first.
  `mm/dd/yyyy` where the second part > 12 → month-first. Both parts ≤ 12 → **flag
  `AMBIGUOUS_DATE` with the raw string, do not resolve**. `08-Jun-26` style → parse, note the
  format.
- **Alternatives:** assume US order for all (silently mis-dates the 32 proven day-first rows);
  assume UK order (silently mis-dates the 33 proven month-first rows); drop the column.
- **Why:** both orderings are *proven present in the same column* — 32 rows have a first part
  > 12, 33 rows have a second part > 12, and 28 are genuinely ambiguous. The date feeds the
  appeal-window calculation, so a silent mis-parse moves money.
- **Trade-off:** 28 rows land in exceptions instead of being dated. Correct behaviour; see
  **[Q4]**.

### D11. Envelope defects are warnings, not failures

- **Decision:** `GE*7*201` (and 9/9/9 in the other files) declares 7 or 9 functional groups
  where exactly one `GS`/`GE` pair exists. Emit `ENVELOPE_GROUP_COUNT` as a warning and
  continue ingestion.
- **Alternatives:** hard-fail (the whole pack would be rejected and nothing else could be
  read); ignore (an X12 claim of "validated" that is not true).
- **Why:** `GE01` should be 1. Everything inside the envelope is internally consistent —
  `SE01` segment counts are correct in all 34 transactions, `ST`/`SE` balance, `IEA01`/`ISA13`
  match. The defect is real but non-blocking; a strict validator rejects, a lenient parser must
  still *report*.
- **Trade-off:** we accept files a strict validator would not. Disclosed on the exceptions
  screen and in the reconciliation report.

### D12. Every ingestion run must satisfy `rows_in = matched + exceptions`

- **Decision:** per source file and per entity type, the pipeline asserts row conservation and
  writes it to the audit log. Mismatch fails the run.
- **Why:** this is the cheapest possible proof that nothing was silently dropped — which is
  the single most likely way to produce a wrong total while looking correct.
- **Trade-off:** a slightly more rigid ingestion shape (staging → classify → persist).

### D13. Derived files containing patient identifiers are regenerated, never committed

- **Decision:** `AQSoft_Assignment_Data_Pack_1/worklog_extracted.csv` is git-ignored. It is
  rebuilt on demand with `powershell -File tools/extract_worklog.ps1`. No application log,
  doc or eval artefact may contain a patient name, DOB or member id — claim ids and NPIs only.
- **Alternatives:** commit it (a second, greppable plaintext copy of names that already sit in
  the client's `.xlsx`); delete it (breaks every profiling script).
- **Why:** the brief says treat all data as real PHI. The client-supplied pack is kept as
  received because it *is* the input and reproducibility depends on it, but we do not create
  additional plaintext copies of identifiers that are trivially regenerable.
- **Trade-off:** one extra step before running the tools. Documented in `.gitignore` and in
  `README.md`. `docs/` and `tools/` are audited for names — column *names* like `patient_dob`
  appear, patient *values* do not.
- Note: rendering-provider names and NPIs are not patient identifiers and are used freely
  (e.g. the single NPI driving all 26 credentialing denials).

---

```
AQcode/
├─ docker-compose.yml               # db + api + web, seeded users & data
├─ .env.example                     # LL M_PROVIDER, LLM_API_KEY, DB_*, SEED_PASSWORD
├─ README.md
├─ AQSoft_Assignment_Brief.pdf
├─ AQSoft_Assignment_Data_Pack_1/   # read-only input, never modified
├─ docs/
│  ├─ DECISIONS.md                  # this file
│  ├─ DATA_FINDINGS.md              # Phase 0 deliverable
│  ├─ AI_USAGE_LOG.md
│  ├─ HOURS.md                      # user-maintained
│  ├─ PROBLEM_MEMO.md               # <= 1 page
│  ├─ AI_EVALUATION.md
│  └─ DEMO_SCRIPT.md                # 5-8 min
├─ tools/                           # read-only profiling + the eval harness runner
│  ├─ profile_*.mjs
│  └─ extract_worklog.ps1
├─ src/
│  ├─ AQ.Denials.Api/               # ASP.NET Core 8 minimal API + auth + audit
│  ├─ AQ.Denials.Core/              # entities, money (Cents), domain rules
│  ├─ AQ.Denials.Ingest/            # 835 parser, CSV parsers, idempotency, reconciliation
│  │  └─ X12/                       # segment reader, envelope, CARC/RARC registry
│  ├─ AQ.Denials.Rules/             # taxonomy, windows, priority weights  <- ONE place to change
│  ├─ AQ.Denials.Llm/               # ILlmClient + provider impl, prompt templates, citation check
│  └─ AQ.Denials.Tests/             # xunit
├─ web/                             # React + TS (Vite)
│  └─ src/{pages,components,api,roles}
└─ eval/                            # AI eval harness (Node unless user prefers Python)
   ├─ cases/*.jsonl                 # inputs, expected, adversarial
   └─ run.mjs                       # writes eval/results.json -> docs/AI_EVALUATION.md
```

Change-request hot spots, each deliberately in exactly one file:

| What changes | Where |
|---|---|
| parsing / segment handling | `src/AQ.Denials.Ingest/X12/` |
| claim-id mapping tables | `src/AQ.Denials.Ingest/ClaimIdNormaliser.cs` |
| root-cause rules | `src/AQ.Denials.Rules/RootCauseRules.cs` |
| windows / policy tables | `src/AQ.Denials.Rules/PayerWindows.cs` |
| priority weights | `src/AQ.Denials.Rules/PriorityScoring.cs` |

---

## Questions and assumptions

Materiality = changes a number in `DATA_FINDINGS.md` or a screen a judge will see.

**Questions (need an answer before Phase 1)**

- **[Q1] Denial scope — material.** Should "open denials" be claim-level only
  (**136 / $27,780**), or claim-level plus line-level on accepted claims
  (**+19 / +$3,365**)? The labelled set contains 4 line-level rows, so the product must detect
  both; the question is whether they appear in the headline total. *Proposed:* show all three,
  never sum them into one figure.
- **[Q2] Definition of "recoverable" — material.** Window-only gives **105 / $21,245**. But
  all 26 credentialing denials ($4,460) are inside the window while
  `CSA_PROVIDER-ENROLLMENT` §3 says those services "may not be appealed on that basis".
  *Proposed:* two independent flags, `window_expired` and `policy_bars_appeal`, and report
  "105 in window, of which 79 have a stated appeal path". That makes the number
  **79 / $16,785** on the stricter definition. Which does the business want as the headline?
- **[Q3] Policy citation scope.** `MPPO_DX-EXCL-03` is a *Meridian PPO* policy whose preamble
  claims it applies to all network payers. CARC 11 denials occur on all four payers. May a
  Northstar denial cite the Meridian file? *Proposed:* no — cite only a policy belonging to
  that payer (plus `ALL_PAYERS_*`), otherwise cite `carc_rarc_reference.csv`. Say so in the
  citation footer.
- **[Q4] Worklog dates.** 28 rows have both parts ≤ 12 and cannot be resolved. *Proposed:*
  leave undated, flag `AMBIGUOUS_DATE`, show raw. Confirm this beats picking an order.
- **[Q5] CARC 18 taxonomy.** Labeled sample has no CARC 18 example; I mapped it to
  *Payer error*. It affects 6 open denials / ~$1,100. Confirm or correct.

**Assumptions (state them; object if wrong)**

- **[A1]** "Today" = **2026-09-30**. Latest remit check date is 2026-09-18.
- **[A2]** The claim export is canonical for charge; the 835 is canonical for status and cash.
  They agree on all 1,164 adjudicated claims, so this costs nothing today.
- **[A3]** Appeal window = corrected-claim window for all four payers (they are equal in
  `payer_rules.csv`), measured from `DTM*405` on the transaction header.
- **[A4]** The 58 never-adjudicated claims ($12,600) are *pending*, not lost. 5 submitted
  before 2026-08-01 with no remittance are flagged for investigation.
- **[A5]** A `status 22` row is a takeback of a prior payment. Its amounts are already
  negative in the file; no sign flipping is applied.
- **[A6]** File-level `BPR` is the cash total for that check run; per-file and per-payer
  `BPR = Σ CLP04` must hold or the run fails.
- **[A7]** No `PLB` segments exist in this pack. The parser still implements them and reports
  `$0.00` with a count, so the report proves absence rather than assuming it.
- **[A8]** `payer_policies/*.md` and `payer_rules.csv` agree on every stated window — verified,
  not assumed. Contradiction hypothesis **not confirmed**.
- **[A9]** AI eval harness in **Node**, not Python, because Python is unavailable on this
  machine. Swap to Python if the environment changes.
- **[A10]** No PHI goes into logs, git, or prompt outputs — claim ids and NPIs only, never
  patient names or DOBs. (The worklog contains a `Patient` column; it is not copied anywhere.)
