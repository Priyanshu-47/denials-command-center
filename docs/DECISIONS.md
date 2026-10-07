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

## Phase 1 — ingestion and reconciliation

### Answers to Q1–Q5 (user decision, recorded 2026-10-07)

Each answer below **supersedes** the proposal recorded under "Questions and assumptions".

#### Q1 — three mutually exclusive money buckets, never summed into one figure

- **Open denials (headline) = claim-level only: 136 / $27,780.**
- The 19 accepted claims with a $0 line (CARC 97, $3,365) are **not denials**. They are shown
  as a separate informational bucket **"zero-paid lines on paid claims"** and are never added
  to the headline.
- The 58 never-adjudicated claims ($12,600) are a third bucket **"no remittance yet"**, with
  age since submission. Not denials — but the manager must see that this money is stuck too.
- **Why:** the headline stays auditable to a single source (`CLP02 = 4` in the 835). CARC 97
  means the claim *paid*; summing it into denials would double-count money that is already
  on the books.
- **Trade-off:** three numbers instead of one. Requires explicit labelling in the UI so a
  reader does not add them mentally.

#### Q2 — recoverability: three exclusive buckets that must sum to the open total

| Bucket | n | Amount |
|---|---|---|
| Recoverable by appeal / correction | 79 | **$16,785** |
| Blocked by policy (not appealable; prevention target) | 26 | **$4,460** |
| Expired / lost (every route closed) | 31 | **$6,535** |
| **Open total** | **136** | **$27,780** |

`79 + 26 + 31 = 136` and `16,785 + 4,460 + 6,535 = 27,780`. Asserted as an exact-sum test
(no tolerance, no overlap).

- **Window logic verified, and it did not change any number.** A denial is *lost* only when
  **every** route is closed. Routes are `appeal_window_days_from_denial` and
  `corrected_claim_window_days_from_denial`, both measured from `DTM*405`.
  Measured across all 136 open denials (`tools/recoverability_buckets.mjs`):
  - appeal window == corrected-claim window for **every** open denial (180/180, 120/120,
    60/60, 90/90 — all four payers have identical values in `payer_rules.csv`);
  - denials where the appeal window is closed but the corrected-claim window is open: **0**;
  - denials with any route open: **105**, identical to appeal-only **105**.
  **Therefore $16,785 / $4,460 / $6,535 are unchanged by the multi-route check.**
- **Timely filing is deliberately NOT a recovery route.** 7 denials still have
  `timely_filing_days_from_dos` open after every denial route has closed. TF governs *first*
  submission of a claim; the payer has already adjudicated these, so a fresh TF-eligible
  filing is not an available remedy. Recorded as **[A11]** rather than silently ignored.

**Recoverability rule + source, per denial category** (source marked *assumption* where no
document backs it):

| Category | Trigger | Recoverability rule | Window source | Policy source |
|---|---|---|---|---|
| Credentialing | CARC B7 | **Blocked** — not appealable on that basis | `payer_rules.csv` CSA77 (moot) | `CSA_PROVIDER-ENROLLMENT.md` §3 — *policy* |
| Billing – timely filing | CARC 29 | Window only; in-window = appeal, else lost | `payer_rules.csv` `appeal_window` | *assumption* (an in-window appeal is a real remedy) |
| Authorization | CARC 197 (SMP12 DOS ≥ 2026-04-01, or auth-required) | Window only | `payer_rules.csv` | `SMP_SNF-AUTH-2026.md` §5 (60d) for SMP12 — *policy*; other payers *assumption* |
| Payer error | CARC 197 on SMP12, DOS < 2026-04-01 | Window only — denial was incorrect (no auth required) | `payer_rules.csv` | `SMP_SNF-AUTH-2026.md` §3 — *policy* |
| Eligibility | CARC 27 | Window only | `payer_rules.csv` | *assumption* |
| Medical necessity | CARC 50 | Window only | `payer_rules.csv` | *assumption* |
| Coding – diagnosis | CARC 11 | Window; corrected-claim route | `payer_rules.csv` `corrected_claim_window` | `MPPO_DX-EXCL-03.md` §4 (90d) — *policy, MRD55 only*; others *assumption* |
| Coding – frequency | CARC 151 | Window; appeal must carry notes | `payer_rules.csv` `appeal_window` | `NSHP_HOSP-FREQ-07.md` §4 (180d) — *policy, NS401 only*; others *assumption* |
| Coding – modifier | CARC 97 | Window; replacement claim (freq 7) | `payer_rules.csv` `corrected_claim_window` | `ALL_PAYERS_MOD25-2026.md` §3 — *policy, all four payers* |
| Duplicate submission (unvalidated) | CARC 18 | Window — **but see Q5: next action is "no action"** | `payer_rules.csv` | *assumption* (that no appeal is warranted) |

Every policy window that states a number agrees with `payer_rules.csv`
(90 = MRD55, 180 = NS401, 60 = SMP12), re-confirming **A8**.

#### Q3 — a denial may cite only its own payer's policy

- Enforced in the citation validator: `policy.payer == denying payer`, or the policy is
  explicitly multi-payer (`ALL_PAYERS_*`). Tested.
- If no applicable same-payer section exists, the draft **states that no policy basis was
  found**, carries **no citation**, and goes to the human-review queue. A missing citation is
  never papered over with a different payer's document.
- Directly affects `MPPO_DX-EXCL-03.md`, whose preamble claims to apply to all network payers:
  only MRD55 denials may cite it.
- The payer→policy association itself is **derived** (the pack has no policy column; it is
  inferred from the file titles' payer prefix), so it is pinned by `PolicyReferenceTests`:
  all four payers map to a file that exists and has content, the rules table and the policy
  set cover the same four payers, and `ALL_PAYERS_MOD25-2026.md` is nobody's own document.

#### Q4 — ambiguous worklog dates: store both readings, and **0 of 28 affect any deadline**

- The worklog's *only* date column is `Date Logged`; it has no deadline column.
- Canonical appeal/corrected-claim windows anchor on `DTM*405` (A1), never on `Date Logged`.
- **Measured (`tools/q4_ambiguous.mjs`): all 28 ambiguous rows belong to claims that have a
  `DTM*405`. → `deadlineAffecting = 0`.** No ambiguous date can move a deadline.
- Handling as decided: raw string stored, **both** interpretations stored, exception row
  `AMBIGUOUS_DATE`, visible UI flag, conservative (earlier) reading drives priority and is
  displayed as "date uncertain". A guess is never written into canonical data.
- **Bonus finding:** 5 of the 28 are resolvable by *elimination*, not by guessing — one
  reading falls after today (2026-09-30), so a denial cannot have been logged in the future
  (`08/11/2026`, `07/11/2026`, `08/11/2026`, `09/11/2026` → DMY impossible;
  `10/08/2026` → MDY impossible). Both readings are still stored; this is recorded as an
  annotation on the exception row, not as a resolution of the canonical value.
  Remaining **23 are genuinely ambiguous** (both readings ≤ today).

#### Q5 — CARC 18 gets its own category, and **6 of 6 turn out to need no action**

- Category **"Duplicate submission (unvalidated)"**, confidence capped low, always routed to
  human review, flagged in the eval report as *"extrapolated, not covered by labeled data"* —
  the labelled sample contains no CARC 18 example.
- **Empirical check (`tools/q5_carc18.mjs`) on same patient + DOS + CPT:**
  **6 of 6 open CARC-18 denials have an earlier sibling claim, and all 6 siblings are PAID
  (`CLP02 = 1`).**

  | Denied claim | Payer | Sibling (earlier) | Sibling status | Sibling paid |
  |---|---|---|---|---|
  | GPP-2026-002482 | NS401 | GPP-2026-001144 | 1 (paid) | 111.60 |
  | GPP-2026-002485 | SMP12 | GPP-2026-000342 | 1 (paid) | 80.60 |
  | GPP-2026-002480 | NS401 | GPP-2026-001642 | 1 (paid) | 105.40 |
  | GPP-2026-002488 | NS401 | GPP-2026-001626 | 1 (paid) | 58.90 |
  | GPP-2026-002484 | CSA77 | GPP-2026-001267 | 1 (paid) | 127.10 |
  | GPP-2026-002483 | MRD55 | GPP-2026-001368 | 1 (paid) | 64.48 |

- **Consequence:** these 6 ($910 total) are *expected* duplicates of an already-paid claim.
  The correct next action is **close / no action — do not appeal.** They still sit inside the
  $16,785 recoverable bucket by the window test, so this is reported as a **disposition
  note inside the bucket, not a re-bucketing**: removing them would break the
  `136 = 79 + 26 + 31` identity the user specified. Flagged for the user rather than changed
  unilaterally.
- Do not force a taxonomy label with no support: the category exists precisely so the system
  can say "unvalidated" instead of inventing confidence.

### C1 (correction) — 40/40 is fit-to-sample, not accuracy

- **Decision:** the taxonomy score is reported as **"fit to labeled sample (in-sample)"**,
  never as accuracy, because the rules were derived from those same 40 labels.
- Also reported: (a) a **leave-one-out / split** evaluation in which the rules are derived
  without the held-out rows; (b) a hand-written set of **15–20 adversarial / ambiguous
  held-out cases** (conflicting codes, unseen CARC+RARC combinations, injected text) with the
  observed behaviour.
- **Why:** `README.txt` says *"Use it to evaluate your AI. Do not hard-code these answers."*
  Presenting an in-sample fit as accuracy would violate that instruction and overstate the
  result. An honest 85% is worth more than a misleading 100%.
- **Trade-off:** the headline number goes down. That is the point.

### C2 (correction) — eval and reviewer-runnable code is C#, Node stays dev-only

- **Decision:** anything a reviewer must run — the AI eval harness and the Phase 0
  invariants — is **C#** (xUnit category / console project), runnable via `docker compose`.
- The Node `tools/*.mjs` scripts remain **developer profiling tools only**, clearly labelled
  as such, and are not the deliverable.
- **Ported into xUnit golden tests:** `BPR = ΣCLP04` (per file, per payer),
  `CLP03 = CLP04 + Σ signed CAS`, duplicate-payload detection, bucket sums, and the headline
  figures (136 / $27,780, 1,164 adjudicated, 58 unadjudicated, cash $118,220.36).
- **Why:** the reviewers' stack and the live round are C#/.NET; a reviewer who cannot run
  `dotnet test` cannot verify anything.

### D14 — the data pack is an input, never a commit (supersedes the earlier D14 wording)

- **Decision:** the pack is **untracked**. `.gitignore` covers `AQSoft_Assignment_Data_Pack_1/`,
  `/data/`, `*.835`, `denials_worklog*.xlsx`, `worklog_extracted.csv` and `*_extracted.csv`.
  Compose mounts the pack **read-only** via `${DATA_DIR}`.
- **Not relocated, not deleted** — the files stay exactly where they are; only their git
  status changed.
- **Fail fast:** if `DATA_DIR` is missing or incomplete, ingestion aborts with a message that
  **lists every missing file** rather than failing later with a confusing parser error.
- **Guard:** `tools/phi_guard.mjs`, wired as `.githooks/pre-commit` (enable with
  `git config core.hooksPath .githooks`) and as a CI step. Three rules —
  R1 path (pack file / raw `.835` / worklog `.xlsx`), R2 header (CSV carrying
  `patient_first|patient_last|patient_dob|member_id`), R3 value (real patient name, whole-word,
  case-sensitive, only when the pack is present to derive the name list).
- **History:** the repo had one commit containing 19 pack blobs. No remote, nothing pushed,
  so the root commit was **rewritten** rather than leaving PHI in history. Verified after the
  rewrite: **0 pack blobs, 0 pack files tracked.**
- **Why:** "treat the data as if it were real PHI" — a git object is a second, permanent,
  easily-missed copy.
- **Trade-off:** a reviewer must supply their own pack. Documented in `README.md`.

### D15 — data location is one env var, `DATA_DIR`

- **Decision:** `DATA_DIR` defaults to `./AQSoft_Assignment_Data_Pack_1` (the current
  location). Docker Compose mounts `${DATA_DIR}` at `/data:ro`. Nothing in the code hard-codes
  a path; `tools/*.mjs` read `process.env.DATA_DIR` too.
- **Why (supersedes the earlier "`./data`" wording):** the files must not be moved, and
  reviewers run from their own copy of the pack in their own location.
- **Trade-off:** one variable to set. README states the exact sequence: unzip → set
  `DATA_DIR` → `docker compose up`.

### D16 — read endpoints derive; the database is the durable record

- **Decision:** `/api/reconciliation`, `/api/reconciliation/report.md`, `/api/exceptions` and
  `/api/claims/{id}` are answered by running the pure ingestion pipeline over the data pack.
  `POST /api/ingest` is what writes the canonical state, the exception rows and the audit
  entries to PostgreSQL.
- **Alternatives considered:**
  1. *Read everything from the database.* Requires a second reconciliation builder that
     re-derives the report from rows — a second implementation of the same arithmetic, free to
     drift from the first and only detectable by comparing outputs nobody runs.
  2. *Store a serialized report snapshot on the run.* Fast, but a snapshot of a computed value
     that can disagree with the rows sitting next to it.
  3. *Derive everything, store nothing.* Loses the audit trail and the durable books, which the
     brief requires.
- **Why:** option 3's weakness and option 1's duplication are avoided by making derivation the
  only source of *figures* and storage the source of *history*. There is exactly one
  implementation of the arithmetic, and the tests that pin it are the same tests protecting
  every endpoint.
- **Trade-off:** each read re-parses four files (measured: well under a second). If that stops
  being acceptable the cache must key on the pack's file timestamps, not on a timer — a
  time-based cache would be a second source of truth.
- **Pending:** a test asserting stored rows == derived outcome. Deferred to the phase that adds
  the database-backed views; it is the natural test to write alongside them.

### D17 — authorization is server-side, seeded, and has no permissive default

- **Decision:** bearer tokens from `SEED_USERS` (JSON, env var only), two roles — `reader`
  (all `GET`s) and `ingest` (`POST /api/ingest` as well). Tokens compared with
  `CryptographicOperations.FixedTimeEquals`.
- **Alternatives:**
  1. *No auth on a localhost demo.* Fastest, and exactly what gets shipped by accident.
  2. *Full JWT/OIDC with a signing key.* Right for production; here it adds a key-management
     surface and a dependency for two endpoints, without changing what is being tested.
  3. *A permissive Development fallback.* Rejected — see below.
- **Why not a fallback:** `SeedUsers.Read` **throws** when `SEED_USERS` is unset outside
  `Development`. A service that defaults to "any token works" is the likeliest way for this to
  reach production looking secure and behaving open. `Development` has named local defaults
  (documented in the file), and `.env.example` ships working local tokens so
  `cp .env.example .env` is a running system.
- **Two bugs this decision's tests caught** (both would have shipped silently):
  1. The role claim was created as the literal string `"role"`. `RequireRole` resolves through
     `ClaimsPrincipal.IsInRole`, which reads `ClaimTypes.Role` — so every valid token
     authenticated perfectly and then authorized *nothing* (403 everywhere).
  2. `HandleChallengeAsync` wrote the error body without setting a status code.
     `Response.WriteAsJsonAsync` defaults to **200**, so every refused request looked
     successful to anything checking the status.
- **Trade-off:** tokens live in an environment variable rather than a secrets manager. For a
  local evaluation that is the right size; the variable is documented as "replace before
  anything is shared".

### D18 — direct patient identifiers are not on the wire in Phase 1

- **Decision:** `/api/claims/{id}` returns the claim reference, payer, dates, money, lines and
  full remittance history. It does **not** return `patient_first`, `patient_last`,
  `patient_dob` or `member_id`.
- **Why:** the reconciliation and denial views do not need them, and "no PHI in logs" is easier
  to keep true when the fields are never materialised in a response object that a future
  `ILogger` call could serialise. `WorklogEntry.PatientDisplay` is likewise not mapped to any
  database column (D14's minimisation, extended to storage).
- **Trade-off:** the Monday UI will need a way to see *whose* claim a row is. That is a
  deliberate, separate decision with its own authorization story rather than something that
  arrives by default. Flagged here so it is a choice and not an omission.

### D19 — one report code path, and it doubles as a gate

- **Decision:** `--report` calls `ReconciliationReport.ToMarkdown()` — the same method the
  `/api/reconciliation/report.md` endpoint returns. It then runs
  `AssertRowConservation()` and `AssertZeroDifferences()` and **exits non-zero** if either
  fails.
- **Why:** two code paths producing the same document would be two documents. Making the
  generator also a checker means the checked-in `docs/PHASE1_RECONCILIATION.md` cannot be
  regenerated with wrong numbers without the command failing.
- **Detail worth knowing:** a relative `--out` resolves against the repository root, not
  against the process working directory. `dotnet run` starts the app in the *project* folder,
  which is how the first attempt silently landed in `src/AQ.Denials.Api/docs/`.

### D20 — migrations are applied by the app, and the same command works out of band

- **Decision:** `AUTOMIGRATE=true` (default) runs `db.Database.Migrate()` at startup;
  `AUTOMIGRATE=false` plus `dotnet ef database update` produces the identical database.
- **Why:** `docker compose up` must be one command. The design-time factory
  (`DesignTimeDbContextFactory`) means creating a migration needs no running Postgres, and the
  local tool manifest (`.config/dotnet-tools.json`) pins `dotnet-ef` to **8.0.11** so the tool
  matches the EF Core version on every machine.
- **Trade-off:** two ways to reach the same schema. Mitigated by making them literally the same
  operation (`Migrate()` applies checked-in migrations; it does not invent schema).

### D21 — the natural-key guard refuses a payment event without touching cash

- **Decision:** when a `CLP` repeats a `PayerId|TraceNumber|CheckDate|PayerClaimControlNumber`
  key **across files**, the observation is refused and an exception row raised. Cash is
  unaffected.
- **Why:** cash is measured from `BPR` at transaction level, which describes the check run; the
  natural key describes whether a payment event may be attributed to a claim. Refusing
  attribution is correct and changes no money.
- **Measured:** the pack produces **0** refusals, so no published number depends on this
  behaviour today. It is documented because it is a live rule that would start firing the
  moment a second remit for the same claim arrives.
- **Trade-off:** a same-file repeat is *not* refused — it is treated as a correction, which is
  what `GPP-2026-001210` (denied and re-paid in one transaction) actually is.

### D22 — adjustment scope is the population cash describes, and the gap is reported

- **Decision:** adjustment groups are summed over **every** observation in imported files — the
  same population as `Σ CLP04` — not only over observations that could be linked to a claim in
  the export. The portion that cannot be attributed is broken out as
  `UnattributedAdjustmentGroups`.
- **Measured:** summing only linked observations gave CO **$108,356.80**, which is **$159.60**
  short of the published $108,516.40. The gap is 3 payment events on `BHC-*` claims that are
  not in this export. Reported as `CO / 3 / 159.60` rather than silently reconciled.
- **Why:** the two sides of a reconciliation must describe the same population. Adding the gap
  to the attributed total would have made the number "right" for the wrong reason.

### D23 — the checksum distinguishes claim-level from service-level adjustments

- **Decision:** the adjustment line in `StateChecksum` uses `Adjustment.IsServiceLevel`, which
  is true if **either** the foreign key **or** the navigation is populated.
- **Why it changed:** the first version tested `ObservationServiceId is null`, which in memory
  (before any save) is null for *every* adjustment — so all of them were recorded as
  claim-level and two structurally different states hashed the same. `IsServiceLevel` reads
  whichever of the two is populated at that moment.
- **Consequence:** the full-state checksum moved from `f4600390…` to `50c176cb…`. The change is
  a strictly larger set of distinctions, not a change to any figure — every reconciliation
  number in `docs/PHASE1_RECONCILIATION.md` is unchanged.
- **Trade-off:** none of substance; a checksum that under-distinguishes would have been found
  only by a test that happened to vary service-level adjustments alone.

### D24 — four reference files are committed; the rest of the pack never will be

- **Decision (user-approved, 2026-10-07):** commit `payer_rules.csv`,
  `carc_rarc_reference.csv`, `claim_adjustment_group_codes.csv` and `payer_policies/*.md`.
  Everything else in the pack stays ignored.
- **Why:** they are pure reference data — day-count windows, code meanings, and policy prose.
  No person appears in any of them, and they are the material the system *reasons from*, so a
  reviewer should be able to read what the rules are without unzipping anything.
- **Mechanics:** the pack pattern is `AQSoft_Assignment_Data_Pack_1/*`, not a trailing slash —
  git cannot re-include a file whose **parent directory** is excluded, so the directory itself
  has to stay open for the negations to reach the files. `git status --untracked-files=all`
  confirms exactly 8 files surface and 0 others.
- **Correction to an earlier framing:** this does **not** make the repository standalone.
  `claims_export.csv` and `remits/*.835` remain excluded and always will be, so `dotnet test`
  still needs a reviewer-supplied pack. The gain is *reviewability of the rules*, not
  self-containment. Stated here because it was offered as the opposite.
- **Two holes found while making the change, both closed:**
  1. `claims_export.csv` was only excluded *by path*. Pointing `DATA_DIR` at another folder and
     unzipping there would have made a file full of names, DOBs and member IDs untracked-but-
     ignorable, and `git add .` would have taken it. Now ignored **by file name**, anywhere.
  2. Same for `*.xlsx`. Deliberately broad: an xlsx is binary, so phi_guard's R3 whole-word
     name check cannot read it, and a *renamed* worklog would slip past R1's specific pattern.
     Committing a spreadsheet now needs `git add -f` and a reason — that friction is the point.
- **Trade-off:** the four committed files are assignment inputs, so the repository now carries
  part of the brief. Accepted because they are reference tables with no people in them, and
  because a rule you cannot read is a rule you cannot audit.

---

## Phase 2 — AI denial analysis (plan, written before code)

Brief §C, verbatim: *for every open denial* — root-cause category, owning team,
preventable-before-billing, recommended next action · *for appealable denials* — a draft appeal or
correction note that **cites the exact payer policy section** it relies on · show a confidence
level and route low-confidence results to human review · **must still work (degraded) if the AI
service is unavailable** · evaluate against `labeled_denials_sample.csv`, report accuracy and
where it fails and why · **treat all text from files as untrusted input**.

Judged on: *grounded, evaluated, honest about confidence, hard to fool.*

### What was true in the environment when planning started

- **No cloud LLM credential exists** anywhere in this environment; `LLM_*` in `.env` are empty.
- **Ollama 0.34.2 is installed and running**, with `qwen2.5-coder:3b` (1.9 GB). The brief permits
  "any LLM … or a local model", so this is compliant.
- Measured: first call **110 s** (81 s model load, one-off); warm calls **~1 s** at 66 in / 38 out.
  136 open denials + 40 labelled + 20 adversarial ≈ 200 calls ≈ minutes, not hours.
- `qwen2.5:7b-instruct` was requested to download in parallel (user decision) and is treated as a
  drop-in: the client is behind `ILlmClient`, so the model is a configuration line, not a design.

### The plan

| §C requirement | Design | Survives AI being down? |
|---|---|---|
| category, team, preventable, next action | **Deterministic.** `DenialCategory.Of()` already picks the category from CARC + payer + DOS; team/preventable/next-action become pure functions of that category (**D25**). The model never chooses them — already **D6**. | ✅ |
| draft note citing the exact policy section | Prompt built from **structured fields only**; the model may only choose among an enumerated list of sections already belonging to the allowed file (**D26**, **D27**). Every citation is re-read and re-validated after generation. | ✅ degrades to "no draft + reason" |
| confidence + human review queue | **Computed, never self-reported** (**D28**). Inputs: whether the category is covered by labelled data, whether a citation was found, deadline margin, whether a disposition note (Q5) applies. | ✅ |
| still works degraded | `NullLlmClient` throws `NotConfigured`; the whole analysis runs and marks drafts unavailable. Already built. | ✅ by construction |
| evaluate vs labels | C# harness (**C2**), in-sample framing (**C1**), plus ~20 hand-written adversarial/ambiguous cases (**D30**). | rule half yes; model half needs a model |
| text from files is untrusted | Prompts interpolate **no raw file text** — only parsed, length-bounded structured fields. Model output is parsed and re-validated, never trusted (**D27**). | ✅ |

### Sequencing

1. **Deterministic core** (no model): category outcomes, policy parser, citation + validator, tests.
2. **Model path**: Ollama client, factory, prompt templates, response parsing, re-validation, degradation.
3. **Evaluation**: 40 labelled + 20 adversarial → `docs/AI_EVALUATION.md`.

Nothing in step 1 or 2 depends on which model is installed; the eval in step 3 records which one
was actually used and at what size.

### D25 — team, preventability and next action are functions of the category, and are honest about where they came from

- **Decision:** `DenialCategory.Outcomes` maps each category to `(Team, PreventableAtPrebill,
  NextAction, CoveredByLabeledSample)`, in the same file as `Of()`.
- **Where the values come from, because it decides what may be claimed about them:** for the nine
  categories present in `labeled_denials_sample.csv`, team and preventability are read off the
  expert's own assignments. The sample is perfectly consistent — each category maps to exactly one
  team and one preventable value across all 40 rows — so agreement on those 40 rows is **true by
  construction and is not a result**. Reporting it as accuracy would be circular, which is what
  **C1** prohibits. The measurable figure is the category itself, which comes from CARC codes and
  never touches the labels.
- **Two categories are not in the sample at all:** `Duplicate submission (unvalidated)` and
  `UNMAPPED`. `PreventableAtPrebill` is **`bool?` and stays `null`** for them. A claim about
  whether the practice's pre-bill process could have caught something is a claim about how people
  work, and only one source speaks to it — filling it in with something plausible would be exactly
  the invented fact this project must not produce. `CoveredByLabeledSample` is the flag the
  confidence calculation reads, so "the expert never said" becomes a number rather than a silence.
- **Test:** `The_outcome_table_agrees_with_the_expert_sample_on_every_row` reads the CSV and
  checks the table against it — transcribed expectations would only prove the code matches the
  transcription. `Only_categories_the_sample_actually_covers_claim_the_sample_s_authority`
  derives the coverage assertion from the file's own category set, so a category added later is
  automatically treated as unestablished until someone labels it.
- **Trade-off:** keeping the table in code rather than a CSV means a change request edits a source
  file — but it is one file, one dictionary, with tests that fail if it drifts from the sample.

### D26 — a citation has to pass two independent gates, and neither one is asked of the model

- **Decision:** `PolicyLibrary.CitableFiles(payerId, carcs)` returns only documents that pass
  **both** gates, and that list is what the prompt offers. `Validate` re-checks a produced
  citation against the same two gates plus clause existence and quotation accuracy.
- **Gate 1 — whose document is it?** The denying payer's own file, or a file named `ALL_PAYERS*`.
  This is **Q3**, enforced structurally: the model is never shown a document it may not use, so
  it cannot cite another payer's policy even when that is the only policy mentioning the code.
- **Gate 2 — does it talk about this denial?** The document must contain a clause that names a
  CARC/RARC actually on the denial, matched as `CARC 45` and **never as a bare substring** —
  `within 60 days` and `CPT 99231-99233` both contain digits a naive test would match, and a false
  match lets a denial cite a policy that says nothing about it.
- **The two gates together produce the Q3 case the pack actually contains:** Coastal's enrollment
  policy is the only document naming CARC B7, so a *Northstar* credentialing denial has **no
  policy basis it may cite**. It cannot borrow Coastal's file, and `ALL_PAYERS_MOD25-2026.md`
  never mentions credentialing. Result: empty list, no citation, draft must say so. Timely filing
  (CARC 29) likewise appears in no policy at all — its windows live in `payer_rules.csv`, which is
  a date table, not a policy.
- **Verdicts are ordered, not pooled:** `WrongPayer` is reported before `SectionNotFound`, so a
  Q3 breach is never softened into a lesser problem because the model also guessed the clause
  number wrong.
- **`ALL_PAYERS_*` is permitted to everyone but owned by nobody:** `PolicyFileForPayer` still
  returns only the four payer-specific files, so "may cite" and "is the payer's own" stay two
  separate questions instead of collapsing into one lookup.
- **Trade-off:** the CARC gate means a policy that is relevant but never names a code is
  unciteable. Acceptable — relevance here is evidenced by the document naming the code, and an
  unevidenced relevance claim is the thing being defended against.

### D27 — policies are parsed at load, and a policy that cannot be addressed fails there

- **Decision:** `ReferenceDataReader.ReadPolicies` now returns parsed `PolicyDocument`s, not raw
  text. `PolicyParser` splits numbered clauses and throws `InvalidDataException` if it finds none.
- **Why not raw text:** byte length only proves a file is non-empty. What the system needs is to
  point at a specific clause — so a policy that parses to nothing must fail at load, with the file
  named, rather than later as a draft citing "section 3" of a document that has no section 3.
- **Raw text is deliberately not retained:** a clause's own words are what a citation is checked
  against, and keeping a second copy of the file next to them invites the two to drift.
- **Quotation checking compares after whitespace collapsing, never after rewording:** real
  documents reflow, and a correct citation must not fail because the file used two spaces after a
  full stop. `QuoteNotInSection` still catches the fabrication case — a draft claiming Northstar
  "will overturn any duplicate frequency denial on request" against a clause that says no such
  thing.
- **`TestPack`'s fixture policy gained numbered clauses** for the same reason: it existed to make
  `DataPack`'s presence check pass, and a fixture that satisfies the presence check while being
  unparsable would have hidden the failure until a pipeline test read it.

---

## Questions and assumptions

> **Q1–Q5 are answered** in "Phase 1 — ingestion and reconciliation" above. The entries below
> are retained as the record of what was asked and what was originally proposed.

Materiality = changes a number in `DATA_FINDINGS.md` or a screen a judge will see.

**Questions (raised before Phase 1 — answered above)**

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
- **[A11]** Timely filing (`timely_filing_days_from_dos`) is **not** treated as a recovery
  route for an already-adjudicated claim. TF governs first submission; the payer has already
  decided. 7 open denials still have TF open while every denial route is closed, and they are
  reported as expired rather than recoverable. Object if the business disagrees — this is the
  one judgement call in the $6,535 figure.
