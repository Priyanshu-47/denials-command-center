# Phase 0 — Data Findings

**Source:** `AQSoft_Assignment_Data_Pack_1/`
**Assumed today:** 2026-09-30
**How to reproduce every number:** `node tools/profile_data.mjs`, `tools/profile_deep.mjs`,
`tools/profile_rules.mjs`, `tools/profile_line.mjs`, `tools/profile_final.mjs`,
`tools/profile_recon.mjs`, `tools/profile_multi.mjs`, `tools/profile_edge.mjs`,
`tools/profile_x12.mjs`. All are read-only. Nothing in this document was typed by hand
from memory; each figure comes out of one of those scripts.

**Money convention:** all internal arithmetic is in integer cents. `-240.00` and `(240.00)`
both mean negative. Sign is never discarded — see "Own-mistake log" at the bottom.

---

## 0. Top findings: what is actually driving losses

1. **$27,780 is sitting in 136 open denials. $21,245 (105 claims) is still inside the appeal
   window; $6,535 (31 claims) is already expired as of 2026-09-30.**
2. **One not-enrolled practitioner causes 26 of the 136 denials ($4,460, 19% of open denials).**
   All 26 are NPI 1606169955, DOS 2026-06-16 → 2026-08-27, Coastal Senior Advantage, CARC B7.
   The enrollment policy states enrollment is **not retroactive and cannot be appealed on that
   basis** — so these sit *inside* the 120-day window by date but have **no viable appeal path**.
   A naive window calculation reports all $4,460 as "recoverable". It is not.
3. **Exactly 9 claims were submitted after their payer's filing deadline → exactly 9 CARC-29
   denials.** Meridian PPO's 90-day window is the one that hurts (7 of 9). This is 100%
   preventable before billing and is a pure scheduling failure.
4. **Sunshine's SNF authorization rule was applied to 7 claims whose date of service precedes
   the policy's own effective date (2026-04-01).** Policy §3 says those do not require an
   authorization. Those 7 denials are payer error, appealable, not preventable. The other 19
   CARC-197 denials are genuine front-desk failures.
5. **Coder C07 denies at 19.9% vs a 7.9%–13.3% band for everyone else, and 20 of their 30
   denials are the same I10-with-I11 diagnosis edit.** One coder, one edit, ~$4,000 of charge.
6. **Pre-bill review is barely working:** denial rate 12.7% with the flag vs 14.1% without.
   A 1.4 percentage-point difference across 1,164 adjudicated claims — far too small to justify
   the claim that the current pre-bill process is catching anything.
7. **10 of 34 claims that report I10 together with I11.- were paid anyway.** The payer's own
   Excludes1 edit is applied inconsistently, so "will this deny?" is not a deterministic
   function of the claim content alone.
8. **The manual denials log is not a usable system of record:** 48 open denials were never
   logged, 25 logged claims are not denied any more, 10 logged dollar amounts disagree with the
   claim export, and 22 of 120 rows have no owner.
9. **A duplicate remittance file would have inflated the books by $47,461.62 paid and 41 false
   denials**, and ordinary file-hash deduplication does *not* catch it (see §2.4).
10. **The worklog date column cannot be parsed by one rule** — 32 rows prove day-first,
    33 rows prove month-first, 28 are genuinely ambiguous.

---

## 1. `claims_export.csv`

| Measure | Value |
|---|---|
| Data rows (claim-lines) | 1,323 |
| Distinct `claim_id` | 1,222 |
| Claim-id format | `GPP-2026-######` on 1,323/1,323 (100% uniform) |
| Lines per claim | 1 line: 1,121 · 2 lines: 101 |
| Duplicate `(claim_id, line_no)` | **0** |
| Fully duplicated rows | **0** |
| Date columns | `dos`, `submitted_date`, `patient_dob` — **ISO `yyyy-mm-dd` on 1,323/1,323** |
| Submitted before DOS | 0 |
| Unparseable `charge` | 0 |
| Total submitted charge | **$242,585.00** |
| DOS range | 2026-01-05 → 2026-08-28 |
| Submitted range | 2026-01-07 → 2026-09-15 |

The claim export is the cleanest file in the pack. Its problems are *missing* values, not
malformed ones:

| Column | Blank rows | % of 1,323 |
|---|---|---|
| `auth_number` | 1,305 | **98.6%** |
| `modifier` | 1,244 | 94.0% |
| `dx4` | 987 | 74.6% |
| `dx3` | 635 | 48.0% |
| `dx2` | 308 | 23.3% |

Everything else (`claim_id`, patient fields, `member_id`, payer, dates, NPI, facility, POS,
CPT, units, charge, `dx1`, `coder_id`, `prebill_reviewed`) is 100% populated.

### 1.1 Patient identity — churn, not collision

- 1,200 distinct `member_id` values, but only **404 distinct (first, last, DOB) persons**.
- **333 of those 404 persons appear under more than one `member_id`**; the worst case is one
  person under **8** different member IDs.
- **0 `member_id` values are shared between two different persons.**

So there are no ID collisions. The problem is the inverse: the same person accrues member IDs
over time/across payers. Anything keyed on `member_id` alone will fragment a patient's history.

### 1.2 One auth number belongs to the wrong payer

`auth_number` is populated on only 18 rows. On Coastal Senior Advantage (`CSA77`) exactly one
row is filled, and its value is `SMPA7128557` — an **SMP-prefixed** authorization on a Coastal
claim. On SMP12, 17 rows are filled and all are `SMPA*`.

Not material to the totals (1 row), but it is a real cross-payer contamination signal and it
belongs in the exceptions view.

### 1.3 Payer / facility / coder distribution

| Payer | Rows |
|---|---|
| Northstar Health Plan `NS401` | 447 |
| Coastal Senior Advantage `CSA77` | 384 |
| Sunshine Medicaid Partners `SMP12` | 276 |
| Meridian PPO `MRD55` | 216 |

POS: `21` = 919, `31` = 404. Modifier: blank 1,244, `25` = 79. 19 distinct CPTs,
9 facilities, 8 coders (`C01`–`C08`), 25 distinct rendering providers.

---

## 2. The remittances (X12 835)

### 2.1 Structure

| File | ST/SE | CLP rows | SVC rows | segments |
|---|---|---|---|---|
| `era_2026Q1.835` | 7 | 346 | 373 | 3,023 |
| `era_2026Q2.835` | 9 | 453 | 490 | 3,953 |
| `era_2026Q2_resent_0719.835` | 9 | 453 | 490 | 3,953 |
| `era_2026Q3.835` | 9 | 398 | 432 | 3,459 |

Clean on the basics: ASCII only, LF line endings, `~` terminator present, no trailing spaces,
no non-ASCII bytes, ST/SE balanced in all four files, **`SE01` segment counts correct in every
one of the 34 transactions**, no `CAS` outside a `CLP`, no unterminated transaction.

Each `ST` carries its own `BPR`/`TRN`/`DTM*405`/`N1`/`REF*2U` header, so **one file contains
several unrelated check runs**, each with one payer and one check date. That is what makes
denial dates derivable: `DTM*405` on the transaction header is the check/production date.

| Transaction header field | Use |
|---|---|
| `DTM*405` | check date — the denial date used for window maths |
| `REF*2U` | payer id (`NS401`, `CSA77`, `SMP12`, `MRD55`) |
| `N1*PR` | payer name |

### 2.2 Envelope defect (report, do not fail)

`GE` is `GE*7*201`, `GE*9*202`, `GE*9*204`, `GE*9*203`. `GE01` is "number of functional
groups" and should be **1** — each file contains exactly one `GS`/`GE` pair. The value stored
is the number of *transaction sets*. `GE02` correctly equals `GS06`. `IEA*1*<ISA13>` is correct.

A strict validator rejects these files; a lenient parser must not. → **exception row with a
reason code, ingestion continues.**

### 2.3 Codes

- `CLP02` values used: `1` (1,447), `4` (183), `22` (11). Nothing else.
- `CAS` group codes used: **`CO` and `PR` only** — no `OA`, no `PI`.
- `LQ` qualifier: **`HE` only.**
- **No `PLB` (provider-level adjustment) segments in any file.** PLB must still be parsed and
  surfaced as a distinct section of the reconciliation report — it will arrive one day.
- **Unmapped codes: zero.** All 13 CARCs and all 8 RARC in `carc_rarc_reference.csv` are used
  by the remittances, and every code used by the remittances is in the reference. No code is
  missing from either direction.

### 2.4 The duplicate file — and why a file hash will not catch it

`era_2026Q2_resent_0719.835` vs `era_2026Q2.835`:

- Segments **outside** `ISA`/`GS`/`GE`/`IEA`: **identical, 0 differing segments out of 3,949.**
- `BPR`, `TRN`, `DTM*405`, `REF*2U`, every `CLP`/`SVC`/`CAS`/`LQ`: byte-identical.
- Only the envelope differs: control numbers `201/202` → `204`, dates `20260715` → `20260719`.

| Hash scope | Q2 | Q2_resent | Equal? |
|---|---|---|---|
| Whole file SHA-256 | `7a9df943…` | `86a74d76…` | **NO** |
| Payment payload SHA-256 | `eda2c706…` | `eda2c706…` | **YES** |

**Consequence for Phase 1: deduplication must key on a payment-payload hash, not the file
hash.** Store both. If only the file hash is used, this file is imported as new and the books
are wrong by:

- **+$47,461.62 paid**
- **+$89,895.00 charged**
- **+453 claims, +41 false denials, +490 phantom service lines**

---

## 3. Reconciliation — every identity ties to $0.00

### 3.1 Per file: `BPR` = Σ `CLP04`, and `CLP03` = `CLP04` + Σ `CAS` (signed)

| File | Txn | CLP | BPR | ΣCLP04 | BPR − ΣCLP04 | ΣCLP03 | CLP03−CLP04−ΣCAS |
|---|---|---|---|---|---|---|---|
| Q1 | 7 | 346 | $38,012.20 | $38,012.20 | **$0.00** | $68,690.00 | **$0.00** |
| Q2 | 9 | 453 | $47,461.62 | $47,461.62 | **$0.00** | $89,895.00 | **$0.00** |
| Q2_resent | 9 | 453 | $47,461.62 | $47,461.62 | **$0.00** | $89,895.00 | **$0.00** |
| Q3 | 9 | 398 | $32,746.54 | $32,746.54 | **$0.00** | $73,795.00 | **$0.00** |

### 3.2 Per payer (duplicate resent file excluded)

| Payer | BPR (cash) | ΣCLP04 | Diff | ΣCLP03 (charges) |
|---|---|---|---|---|
| `CSA77` | $32,382.60 | $32,382.60 | **$0.00** | $66,785.00 |
| `MRD55` | $18,115.16 | $18,115.16 | **$0.00** | $37,650.00 |
| `NS401` | $45,173.20 | $45,173.20 | **$0.00** | $81,685.00 |
| `SMP12` | $22,549.40 | $22,549.40 | **$0.00** | $46,260.00 |
| **Total** | **$118,220.36** | **$118,220.36** | **$0.00** | **$232,380.00** |

### 3.3 Adjustments by group code (signed)

| Group | Amount |
|---|---|
| `CO` contractual obligation | **$108,516.40** |
| `PR` patient responsibility | **$5,643.24** |
| `OA` | $0.00 (never used) |
| `PI` | $0.00 (never used) |
| **Total** | **$114,159.64** |

Check: ΣCLP03 − ΣCLP04 = $232,380.00 − $118,220.36 = **$114,159.64** ✓ exact.

### 3.4 Claim export vs remittance

| | Claims | Charge |
|---|---|---|
| Claim export, all | 1,222 | $242,585.00 |
| …adjudicated in at least one remit | 1,164 | $229,985.00 |
| …never appears in any remit | **58** | **$12,600.00** |

- `229,985.00 + 12,600.00 = 242,585.00` ✓
- For the 1,164 adjudicated claims, **claim-export charge equals remittance `CLP03` on all
  1,164 — 0 mismatches.**
- **Every remittance claim number matches a claim-export id after normalisation — 0 orphans**
  (the 3 foreign `BHC-*` claims are the only exception, see §6).

---

## 4. Claim-identifier normalisation (Phase 1 requirement)

The same claim appears under four shapes across the pack:

| Shape | Example | Where |
|---|---|---|
| `GPP-2026-######` | `GPP-2026-000494` | claim export, worklog (92 rows), Q1 |
| `GPP########` | `GPP2026000494` | Q1 (113 rows) |
| bare 6 digits | `000494` | Q1 (74), Q2, Q3 (352 rows total) |
| foreign prefix | `BHC-2026-456493` | Q2, Q3 (5 rows) |

Raw remit claim numbers: **1,167 distinct strings → 1,164 distinct claims** (3 strings are
format variants of real claims, plus 5 foreign rows).

After canonicalisation:

- 1,164 / 1,222 export claims are adjudicated; **58 never appear**.
- 0 canonical remit claims are missing from the export.

Normalisation rule (to be implemented once, in one place, and tested):

```
strip whitespace -> case-fold the GPP prefix
GPP[-_ ]?YYYY[-_ ]?N(1..6)  -> GPP-2026-<N zero-padded to 6>
N(1..6)                     -> GPP-2026-<N zero-padded to 6>
GPP-2026-NNNNNN             -> unchanged
<other>-YYYY-NNNNNN         -> foreign namespace, do NOT guess -> exceptions
```

---

## 5. Denials, reversals and re-adjudication

### 5.1 The status ledger

Excluding the duplicate resent file, every claim's observation sequence is one of:

| Pattern | Claims | Meaning |
|---|---|---|
| `1` | 1,020 | paid, single observation |
| `4` | 125 | denied, single observation |
| `1>22>4` | **11** | paid → reversed → re-adjudicated as denied |
| `4>1` | **6** | denied → later paid (**recovered**) |
| `1>1` | **2** | paid twice inside one file with different amounts |

**19 claims have more than one observation; 14 span more than one file; 0 claims were paid in
more than one distinct file.**

### 5.2 Reversals are already signed — do not flip them

A reversal row looks like:

```
CLP*000230*22*-240.00*-148.80**12*SMP123078402693*31*1
SVC*HC:99305*-240.00*-148.80**1
CAS*CO*45*-91.20
```

`CLP03`, `CLP04` **and** `CAS` are all negative. The correct invariant is a plain signed sum:

```
CLP03 = CLP04 + Σ CAS(signed)
```

which holds on **1,650 of 1,650** claim observations and **1,785 of 1,785** service lines
once signs are respected (§8).

The reversal sequence for all 11 claims is: original payment in Q1/Q2 → a `22` row in Q3 that
nets it back to zero → a `4` row in the same transaction carrying the full charge and $0 paid.

| Claim | Original paid | Reversed | Now |
|---|---|---|---|
| `GPP-2026-000230` | +$148.80 (Q1) | −$148.80 (Q3 tx1) | denied, $0 |
| `GPP-2026-000519` | +$86.80 (Q1) | −$86.80 (Q3 tx7) | denied, $0 |
| `GPP-2026-000655` | +$105.40 (Q1) | −$105.40 (Q3 tx1) | denied, $0 |
| `GPP-2026-000763` | +$93.00 (Q1) | −$93.00 (Q3 tx7) | denied, $0 |
| `GPP-2026-001023` | +$105.40 (Q1) | −$105.40 (Q3 tx1) | denied, $0 |
| `GPP-2026-001237` | +$105.40 (Q1) | −$105.40 (Q3 tx1) | denied, $0 |
| `GPP-2026-001532` | +$192.20 (Q1) | −$192.20 (Q3 tx1) | denied (Q3 tx2) |
| `GPP-2026-001665` | +$105.40 (Q1) | −$105.40 (Q3 tx1) | denied, $0 |
| `GPP-2026-001893` | +$192.20 (Q1) | −$192.20 (Q3 tx1) | denied, $0 |
| `GPP-2026-001753` | +$127.10 (Q2) | −$127.10 (Q3 tx8) | denied, $0 |
| `GPP-2026-002361` | +$117.80 (Q2) | −$117.80 (Q3 tx8) | denied, $0 |

Gross clawed back: **$1,379.50**. Combined charge of the 11 claims: **$2,225.00**.
Net cash effect on the original payments: **zero**.

**Ordering rule for Phase 1 (deterministic):** sort observations by
`check date (DTM*405)` → `file name` → `transaction index` → `segment index`.
The last row wins as "current status". The `22` and `4` rows for these claims share a check
date *and* a transaction, so the **segment index is the tie-break that matters** — without it
the current status of 11 claims is a coin flip.

### 5.3 Denied → paid (already recovered)

6 claims, **$1,110** in charge, are denied in one remit and paid in a later one:

`GPP-2026-000875`, `GPP-2026-001095`, `GPP-2026-001210`, `GPP-2026-001274`,
`GPP-2026-001708`, `GPP-2026-002283`

**Therefore: 142 claims have been denied at some point, but only 136 are denied today.**
Any number built from "every CLP with status 4" will be 6 too high and $1,110 too high.

### 5.4 Paid twice inside one file (2 claims)

| Claim | Charge | Payments in `era_2026Q1.835` | Net vs charge |
|---|---|---|---|
| `GPP-2026-001077` | $400.00 | tx3 $161.20 + tx4 $248.00 = $409.20 | **+$9.20** |
| `GPP-2026-001730` | $465.00 | tx3 $161.20 + tx4 $288.30 = $449.50 | **−$15.50** |

Two separate checks in the same file both paid the same claim. Either a partial-then-supplemental
payment with no reversing row, or a duplicate payment. `GPP-2026-001077` ends up **overpaid**
relative to its charge, which no contractual adjustment explains. → exceptions rows, human review.

### 5.5 Denial counts

| Measure | Value |
|---|---|
| CLP rows with status `4` across all 4 files | 183 |
| …of which come from the duplicate resent file | 41 |
| **Distinct claims ever denied** | **142** |
| **Claims denied as of their latest observation** | **136** |
| Claims denied then recovered | 6 |
| Charge on the 136 open denials | **$27,780.00** |

### 5.6 Claim-level vs line-level denials — a definition question

A service line can be denied while the claim is accepted:

```
CLP*001490*1*310.00*127.10**12*NS401...      <- claim accepted
SVC*HC:99221*180.00*0.00**1                   <- E/M line paid $0
CAS*CO*97*180.00                              <- bundled
SVC*HC:31500*130.00*127.10**1                 <- procedure paid
CAS*CO*45*2.90
```

- **155 distinct claims** have a $0 service line in their latest observation.
- **136** of those are claim-denied (`CLP02 = 4`).
- **19** are claim-**accepted** (`CLP02 = 1`) — **$3,365** of charge denied at line level only,
  split 12 pre-bill / 7 not.
- **CARC 97 appears on 19 claims in their latest observation. All 19 are claim-accepted; none is
  claim-denied; none carries modifier 25.** Charges $7,040, of which $3,365 is the $0 line.

**4 of the 40 expert-labelled rows are exactly these claim-accepted cases** — `001490`,
`001712`, `001820`, `002109`, all labelled *Coding - modifier / Coding / Yes*.

So the labelled set deliberately includes line-level denials. Any system that only looks at
`CLP02` will score 36/40 on the eval before it starts. **This needs an explicit decision (Q1).**

---

## 6. Exceptions inventory (what must not be silently dropped)

| # | Exception | Rows | Money | Reason code (proposed) |
|---|---|---|---|---|
| 1 | Duplicate remittance (identical payment payload) | 453 CLP / 1,173 segments | $47,461.62 paid, $89,895.00 charged | `DUPLICATE_PAYLOAD` |
| 2 | Foreign claim namespace `BHC-*` | 5 CLP (3 claims) | $260.40 paid | `FOREIGN_CLAIM_NAMESPACE` |
| 3 | Claim never adjudicated | 58 claims | $12,600.00 charged | `NO_REMIT_OBSERVATION` |
| 4 | Claim paid twice in one file | 2 claims | $409.20 / $449.50 | `MULTIPLE_PAYMENT_NO_REVERSAL` |
| 5 | Envelope `GE01` count wrong | 4 files | — | `ENVELOPE_GROUP_COUNT` |
| 6 | Worklog amount ≠ claim export charge | 10 rows | see §7.4 | `AMOUNT_MISMATCH` |
| 7 | Worklog claim id needs normalisation | 42 rows | — | `CLAIM_ID_NORMALISED` |
| 8 | Worklog entry stale (not denied today) | 25 rows | — | `WORKLOG_STALE` |
| 9 | Denied claim never logged | 48 claims | — | `DENIAL_NOT_LOGGED` |
| 10 | Worklog row with no owner | 22 rows | — | `MISSING_OWNER` |
| 11 | Worklog date unparseable / ambiguous | 38 rows | — | `AMBIGUOUS_DATE` |
| 12 | Auth number from wrong payer | 1 row | — | `AUTH_PAYER_MISMATCH` |
| 13 | Submitted after timely-filing deadline | 9 claims | $1,725.00 (denied) | `SUBMITTED_AFTER_TF` |

Reconciliation identity for the ingestion pipeline:

```
rows_in  =  matched  +  exceptions        (per source file, per entity type)
```

Nothing above is a drop. Rows 1–5 are remittance-side; 6–11 are worklog-side; 12–13 are
claim-side.

---

## 7. `denials_worklog.xlsx` vs the remittances

120 data rows, sheet `Denials Log`, columns: `Date Logged`, `Claim #`, `Patient`, `Payer`,
`Amt`, `Notes`, `Owner`, `Status`.

### 7.1 Claim ids

- 111 distinct canonical claim ids; **0 unparsable**, **0 that fail to resolve** against the
  claim export once normalised.
- **42 of 120 rows need normalisation**: bare 6-digit (`000898`), lowercase (`gpp-2026-000709`),
  and **14 rows with leading/trailing whitespace**.

### 7.2 Dates — the column cannot be parsed with one rule

| Shape | Rows | Evidence |
|---|---|---|
| `yyyy-mm-dd` | 17 | unambiguous |
| `dd/mm/yyyy` **proven** (first part > 12) | **32** | `16/03/2026`, `20/05/2026`, `14/09/2026` |
| `mm/dd/yyyy` **proven** (second part > 12) | **33** | `03/28/2026`, `07/18/2026`, `09/16/2026` |
| **ambiguous** (both parts ≤ 12) | **28** | cannot be resolved from the value alone |
| other (`08-Jun-26`, `05-Sep-26`, …) | 10 | |
| **Total** | **120** | |

Both orderings are *proven present* in the same column. Any single-rule parse silently
mis-dates roughly a third of the log — and the date drives the appeal-window calculation.
→ parse defensively, flag the 28 ambiguous rows, never guess silently (**Q4**).

### 7.3 Status and owner

**10 distinct status strings** for what is really ~4 states:

| Raw | Rows | Suggested state |
|---|---|---|
| `OPEN ` (trailing space) | 14 | open |
| `Open` | 20 | open |
| `open` | 17 | open |
| `pending w/ payer` | 12 | open / submitted |
| `In progress` | 17 | in progress |
| `WIP` | 15 | in progress |
| `Resolved` | 10 | closed |
| `Closed` | 3 | closed |
| `closed` | 5 | closed |
| `done` | 7 | closed |

**7 raw owner values for 3 people**, plus 22 blanks:

| Raw | Rows |
|---|---|
| *(blank)* | **22** |
| `Rahul` | 13 |
| `Karan` | 19 |
| `Anjali` | 14 |
| `anjali ` (trailing space) | 17 |
| `Priya` | 17 |
| `priya` | 18 |

→ 3 real people: Rahul 13, Karan 19, Anjali 31, Priya 35, unassigned 22.

### 7.4 Conflicts with the remittances

| Measure | Value |
|---|---|
| Logged claims that are **not** denied today | **25** (stale — includes the 6 recovered) |
| Open denials **never logged** | **48** of 136 |
| Open denials that are logged | 88 |
| Rows whose `Amt` ≠ claim-export charge | **10** |
| Sum of `Amt` column | $24,570 |

Amount mismatch examples (worklog vs claim export): `001568` $190 vs $450 · `001367` $180 vs
$360 · `001332` $95 vs $355 · `001066` $140 vs $400 · `001712` $130 vs $155 · `002109` $245
vs $505 · `000701` $205 vs $385 · `002004` $205 vs $385.

**Rule: the worklog is merged as history and workflow state only. The 835 is authoritative for
status and money. Every disagreement is an exception row with both values, never an overwrite.**

---

## 8. `payer_rules.csv` vs `payer_policies/*.md`

| Payer | TF (days from DOS) | Appeal (days from denial) | Corrected claim |
|---|---|---|---|
| `NS401` Northstar | 180 | 180 | 180 |
| `CSA77` Coastal | 365 | 120 | 120 |
| `SMP12` Sunshine | 120 | 60 | 60 |
| `MRD55` Meridian | 90 | 90 | 90 |

**Hypothesis "the policies contradict payer_rules.csv" → NOT confirmed.** Every window stated
in a policy matches the CSV:

| Policy statement | Value | `payer_rules.csv` | Match |
|---|---|---|---|
| `MPPO_DX-EXCL-03` §4 "corrected claims within 90 days of denial" | 90 | `MRD55` 90 | ✓ |
| `NSHP_HOSP-FREQ-07` §4 "appeals within 180 days" | 180 | `NS401` 180 | ✓ |
| `SMP_SNF-AUTH-2026` §5 "appeals within 60 days" | 60 | `SMP12` 60 | ✓ |
| `ALL_PAYERS_MOD25-2026` §3 "within each payer's corrected-claim window" | defers | — | ✓ |

Appeal window = corrected-claim window for all four payers, so there is no ambiguity about
which window governs a given denial.

**One scope problem does exist.** `MPPO_DX-EXCL-03` is titled *Meridian PPO* but its preamble
claims it "applies to all network payers using ICD-10-CM edits". CARC 11 denials occur on **all
four** payers (NS401 8, CSA77 7, SMP12 5, MRD55 4). Citing a Meridian policy on a Northstar
denial is a grounding question, not a data question → **Q3**.

### 8.1 Policy → CARC mapping, verified against the data

| Policy | Effective | CARC / RARC | Observed | Verified |
|---|---|---|---|---|
| `SMP_SNF-AUTH-2026` (99304/5/6, DOS ≥ 2026-04-01) | 2026-04-01 | 197 | 26 open denials, all `SMP12`, **0 carry an auth number** | ✓ |
| `CSA_PROVIDER-ENROLLMENT` | 2025-07-01 | B7 / N570 | 26 open denials, all `CSA77` | ✓ |
| `MPPO_DX-EXCL-03` (I10 + I11.-) | — | 11 / N657 | 24 open denials, **all 24 confirmed to report I10 + I11** | ✓ |
| `NSHP_HOSP-FREQ-07` (subsequent hospital care) | 2025-10-01 | 151 / N362 | 11 open denials, all `NS401` | ✓ |
| `ALL_PAYERS_MOD25-2026` (E/M same day as procedure) | 2026-01-01 | 97 / M15 | 19 claims, **all claim-accepted, none carries modifier 25** | ✓ |

---

## 9. Root-cause taxonomy — 40/40 against the expert labels

The 40-row labelled sample is not a free-text exercise: each label is a deterministic function of
`(CARC, payer, DOS vs policy effective date)`.

| Condition (evaluated in order) | `root_cause_category` | `owning_team` | `preventable` |
|---|---|---|---|
| CARC 29 | Billing - timely filing | Billing | Yes |
| CARC B7 | Credentialing | Credentialing | Yes |
| CARC 197 **and** payer `SMP12` **and** DOS < 2026-04-01 | Payer error | Denials (appeal) | No |
| CARC 197 (otherwise) | Authorization | Front desk / Authorization | Yes |
| CARC 27 | Eligibility | Front desk / Eligibility | Yes |
| CARC 50 | Medical necessity | Coding / Clinical | No |
| CARC 11 | Coding - diagnosis | Coding | Yes |
| CARC 151 | Coding - frequency | Coding | Yes |
| CARC 97 (line-level, claim accepted) | Coding - modifier | Coding | Yes |

**Result: 40 / 40 = 100% agreement** on category, owning team and preventable flag.
0 mismatches, 0 unmapped open denials.

The single discriminating fact between *Payer error* and *Authorization* for an otherwise
identical `CARC 197 + RARC N54` denial is **the date of service against the policy's own
effective date**:

| Label | Claims | DOS |
|---|---|---|
| Payer error | `000230`, `000655`, `001665`, `001893` | 2026-02-23, 2026-03-01, 2026-03-09, 2026-02-06 — **all before 2026-04-01** |
| Authorization | `000562`, `001550`, `001934`, `002230` | 2026-08-10, 2026-06-17, 2026-04-23, 2026-04-17 — **all on/after 2026-04-01** |

> **Honesty note for `AI_EVALUATION.md`.** A rules-only implementation scores **100%** on the
> supplied labelled sample. Reporting an "LLM accuracy of 100%" on this sample would therefore
> be meaningless — the sample does not discriminate between rules and a model. The eval must
> report **rules-only** as the primary number, present the LLM as *enrichment* (drafting,
> disambiguation, refusal handling), and state plainly that this 40-row set cannot prove the
> LLM adds value. Testing the LLM's real contribution needs held-out or newly labelled cases.

### 9.1 Open denials by category (136 claims, $27,780)

| Category | Claims | Charge | Inside window | Expired |
|---|---|---|---|---|
| Credentialing | 26 | $4,460 | 26 / $4,460 | 0 / $0 |
| Coding - diagnosis | 24 | $5,295 | 18 / $3,905 | 6 / $1,390 |
| Authorization | 19 | $4,210 | 8 / $1,850 | **11 / $2,360** |
| Medical necessity | 17 | $3,725 | 10 / $2,280 | 7 / $1,445 |
| Eligibility | 17 | $3,660 | 14 / $2,975 | 3 / $685 |
| Payer error | 13 | $2,450 | 11 / $2,140 | 2 / $310 |
| Coding - frequency | 11 | $2,255 | 10 / $2,050 | 1 / $205 |
| Billing - timely filing | 9 | $1,725 | 8 / $1,585 | 1 / $140 |
| **Total** | **136** | **$27,780** | **105 / $21,245** | **31 / $6,535** |

Preventability using the labels' own `preventable_at_prebill` column:

- **Preventable: 106 claims, $21,605** (78% of open denials by count, 78% by dollars)
- **Not preventable: 30 claims, $6,175**
- Every one of the 8 categories in the open set has a label — 0 unclassified.

### 9.2 Recoverability as of 2026-09-30

Window = `check date (DTM*405) + appeal_window_days_from_denial`.

| Payer | Inside window | Expired |
|---|---|---|
| `NS401` | 32 / $6,720 | 2 / $385 |
| `CSA77` | 35 / $6,525 | 7 / $1,570 |
| `SMP12` | 24 / $5,160 | **17 / $3,510** |
| `MRD55` | 14 / $2,840 | 5 / $1,070 |
| **Total** | **105 / $21,245** | **31 / $6,535** |

Days remaining on the 105 that are still alive:

| Bucket | Claims |
|---|---|
| expired | 31 |
| 61+ days | 69 |
| 31–60 days | 17 |
| 15–30 days | 16 |
| **0–14 days** | **3** |

**Caveat that changes the number (Q2):** all 26 credentialing denials fall in the "inside
window" column, but `CSA_PROVIDER-ENROLLMENT` §3 states those services "are not payable and may
not be appealed on that basis". If we define recoverable as *window not expired **and** a viable
path exists*, the recoverable figure drops by those 26 claims / $4,460.

---

## 10. Claim-side patterns

### 10.1 Timely filing — 9 late submissions, 9 denials

| Claim | Payer | DOS | Submitted | Window | Days late |
|---|---|---|---|---|---|
| `GPP-2026-000234` | `MRD55` | 2026-03-03 | 2026-07-01 | 90 | 30 |
| `GPP-2026-000422` | `SMP12` | 2026-03-24 | 2026-08-11 | 120 | 20 |
| `GPP-2026-000571` | `MRD55` | 2026-03-15 | 2026-07-15 | 90 | 32 |
| `GPP-2026-000575` | `MRD55` | 2026-04-23 | 2026-08-11 | 90 | 20 |
| `GPP-2026-001570` | `MRD55` | 2026-04-23 | 2026-08-11 | 90 | 20 |
| `GPP-2026-001741` | `MRD55` | 2026-01-10 | 2026-05-06 | 90 | 26 |
| `GPP-2026-001848` | `SMP12` | 2026-03-24 | 2026-08-11 | 120 | 20 |
| `GPP-2026-002136` | `MRD55` | 2026-04-23 | 2026-08-11 | 90 | 20 |
| `GPP-2026-002156` | `MRD55` | 2026-02-19 | 2026-06-24 | 90 | 35 |

**All 9 were denied with CARC 29. 9 late submissions → 9 CARC-29 denials. No false positives,
no CARC-29 denial from a claim submitted on time.** Meridian's 90-day window accounts for 7 of 9.
This is the cleanest prevention signal in the pack.

### 10.2 Coder outlier

| Coder | Adjudicated | Denied | Rate | Dominant CARC |
|---|---|---|---|---|
| **C07** | 151 | 30 | **19.9%** | **11 (20 of 30)** |
| C03 | 128 | 17 | 13.3% | mixed |
| C02 | 155 | 20 | 12.9% | B7 (9) |
| C01 | 134 | 15 | 11.2% | mixed |
| C04 | 123 | 12 | 9.8% | mixed |
| C06 | 163 | 16 | 9.8% | mixed |
| C05 | 158 | 14 | 8.9% | mixed |
| C08 | 152 | 12 | 7.9% | mixed |

C07's denial rate is **2.5× C08's**, and the excess is almost entirely one edit.

### 10.3 Pre-bill review flag

| Flag | Adjudicated | Claim-denied | Line-only denied | Denial rate | Charge denied |
|---|---|---|---|---|---|
| `Y` | 616 | 66 | 12 | **12.7%** | $12,970 |
| `N` | 548 | 70 | 7 | **14.1%** | $14,810 |

A **1.4 percentage-point** difference. Either the pre-bill review is not catching the denials
that matter, or the flag is applied inconsistently. Worth stating as a finding rather than
claiming pre-bill review "works".

### 10.4 Never adjudicated

58 claims / **$12,600** appear in the claim export but in no remittance.

| Payer | Claims |
|---|---|
| `CSA77` | 18 |
| `NS401` | 17 |
| `SMP12` | 14 |
| `MRD55` | 9 |

- 19 were submitted after 2026-09-01 → legitimately pending (latest remit run is 2026-09-18).
- **5 were submitted before 2026-08-01 and still have no remittance** → investigation candidates.
- Oldest submission with no remittance: **2026-03-10**.

### 10.5 Excludes1 edit is applied inconsistently

34 claims report `I10` together with an `I11.-` code (`NS401` 12, `CSA77` 8, `SMP12` 8,
`MRD55` 6). **24 denied with CARC 11 — so 10 were paid.** The edit is not deterministic on the
claim content alone, which matters for both the rule layer and the confidence score.

### 10.6 Credentialing — one practitioner

All 26 `CARC B7` denials are **NPI 1606169955**, DOS 2026-06-16 → 2026-08-27. That practitioner
has 32 claims in the export of which 27 are denied. No other provider produces a B7 denial.

---

## 11. Own-mistake log (Phase 0)

Two errors I made while profiling, both caught before anything was built.

**M1 — sign handling.** My first parser did
`neg = t.startsWith('-'); n = Number(t); return round(n*100) * (neg ? -1 : 1)`.
`Number('-240.00')` is already negative, so the extra multiplication flipped reversals to
positive. This produced a convincing but **false** conclusion:

> "Q3's BPR is $2,759.00 less than ΣCLP04 — status-22 amounts are stored positive and must be
> applied as negative."

The data was right and my parser was wrong. Reversal rows are `-240.00`, `-148.80`, `-91.20` —
already negative. With the fix, `BPR = ΣCLP04` for **all four files and all four payers, diff
$0.00**.

**How it was caught:** I tried to satisfy `CLP03 = CLP04 + ΣCAS` with signed values and got
`−$1,691.00` instead of zero, which forced me to look at the raw reversal block rather than
trust the aggregate.

**M2 — absolute values on adjustments.** I had summed `|CAS|`. That happens to make
`CLP03 = CLP04 + ΣCAS` come out at zero *when the sign is also being flipped* — two errors
canceling. Fixed to a plain signed sum; all identities then held exactly.

**Rule carried into Phase 1:** money is integer cents, sign is preserved end to end, and a
reconciliation identity must be asserted in a unit test — an aggregate that "looks about right"
hides sign bugs.

**M3 — history leaking into "current state".** The "$0 line on a claim-accepted claim" count
came out as 19 by one route and 20 by another. Cause: `GPP-2026-001210` is denied *and* re-paid
inside the **same transaction** (`era_2026Q2` tx1, same check date, same file), so my
"keep only rows from the latest observation" filter — which compared check + file but not
transaction + segment — retained a $0 line from the superseded row. Fixed by rebuilding the
set directly from the winning observation's own service lines. Side effect: the total count of
claims with a $0 line went 156 → 155, `CARC 11` in that table went 26 → 25, and the pre-bill
"not reviewed" denial rate went 14.2% → 14.1%.

**Rule carried into Phase 1:** "current state" must be derived from a single winning
observation selected by a fully-qualified ordering key (check → file → transaction → segment),
never by filtering history on a partial key. Two numbers that *should* be equal must be
computed by two different routes and asserted equal in a test.

---

## 12. What is *not* wrong (so we do not waste time on it)

- Claim export dates: 100% ISO, zero submitted-before-DOS.
- Claim export keys: zero duplicate `(claim_id, line_no)`, zero duplicated rows.
- Charge parsing: 0 failures across 1,323 rows.
- X12 basics: ST/SE balanced, `SE01` counts correct in all 34 transactions, terminator present,
  ASCII only, no stray segments, no `CAS` without `CLP`.
- CARC/RARC coverage: **zero unmapped codes in either direction**.
- `CLP03 = CLP04 + ΣCAS`: holds on 1,650/1,650 claim observations and 1,785/1,785 service lines.
- `CLP03` vs SVC-level charges: 0 mismatches; `CLP04` vs SVC-level payments: 0 mismatches.
- Claim-export charge vs remittance `CLP03`: 0 mismatches across 1,164 claims.
- Policy windows vs `payer_rules.csv`: no contradictions.
- Worklog claim ids: 0 that fail to resolve after normalisation.
