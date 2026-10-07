# AI Usage Log

Where AI helped, where it was wrong, and how the error was caught.
Every entry names the artefact it touched so a reviewer can re-run it.

---

## Phase 0 — data profiling (2026-09-30)

### AI helped

- **PDF extraction.** The brief is a PDF and no Python is available on this machine.
  AI wrote `tools/pdf3.js` (zlib/flate stream decode + text-operator extraction) which
  recovered the full assignment text, and `tools/extract_worklog.ps1` for the xlsx. Both are
  re-runnable and produce the text quoted in the docs.
- **X12 835 segment reader design.** AI proposed the single-pass state machine (envelope →
  transaction → CLP → SVC → CAS/LQ) used by all `tools/profile_*.mjs`. The alternative —
  one regex per segment type — was rejected because `CAS` amount pairs are variable-length
  (`CAS*CO*45*53.20*97*180.00`) and a regex silently truncates at the first pair.
- **Formulating falsifiable hypotheses before measuring.** e.g. *"policy documents contradict
  `payer_rules.csv`"*, *"the resend is byte-identical so a file hash is enough"*, *"status-22
  amounts are stored positive"*. Writing them down first made it possible to report
  **not confirmed** honestly instead of finding what was expected.
- **Framing the denominator question** that became finding #1: counting every `CLP02 = 4`
  row would have reported 142 denials when only 136 are open.

### AI was wrong

**W1 — money sign handling (material).**
Parser written as:

```js
const neg = t.startsWith('-') || /^\(.*\)$/.test(t);
const n  = Number(t.replace(/[()]/g, ''));
return Math.round(n * 100) * (neg ? -1 : 1);
```

`Number('-240.00')` is already −24000 cents, so the extra multiplication flipped every
reversal to positive. AI then wrote a confident, wrong conclusion into the draft findings:

> "Q3's BPR is $2,759.00 less than ΣCLP04 — status-22 amounts are stored positive and must be
> applied as negative."

The data was right; the parser was wrong. Reversal rows are `CLP*000230*22*-240.00*-148.80`
and `CAS*CO*45*-91.20` — already negative.

- **How it was caught:** while asserting `CLP03 = CLP04 + ΣCAS` with signed values, the result
  was **−$1,691.00** instead of 0. An aggregate that "looks about right" hid it; the identity
  did not. That forced a look at a raw reversal block instead of the rollup.
- **Fix:** `if (/^\(.*\)$/.test(t)) return -round(num*100); return round(Number(t)*100);`
- **After the fix:** `BPR = ΣCLP04` = **$0.00** for all four files *and* all four payers.

**W2 — absolute value on adjustments (compensating error).**
Adjustments were summed as `|CAS|`. Combined with W1, *two* errors cancelled and
`CLP03 = CLP04 + ΣCAS` came out at exactly zero — a false pass.
- **How it was caught:** fixing W1 broke the balance by −$1,691.00, which could only happen if
  `|CAS|` was wrong too.
- **Fix:** plain signed sum. Then `ΣCLP03 − ΣCLP04 = $114,159.64 = CO $108,516.40 + PR $5,643.24`
  exactly.

**W3 — history leaking into "current state" (off-by-one, material to a published figure).**
A claim can be denied *and* re-paid inside the **same transaction** (`GPP-2026-001210`,
`era_2026Q2` tx1, same check date, same file). AI's "keep only rows from the latest
observation" filter compared `check + file` but not `transaction + segment`, so a `$0` line
from the superseded row survived. Two different code paths therefore disagreed:
**19 vs 20** accepted claims carrying a $0 line.
- **How it was caught:** computing the same statistic by a third, independent route
  (`tools/profile_lineonly.mjs`) gave 19, and pre-bill `Y + N` gave 12 + 8 = 20. The mismatch
  was chased to a specific claim rather than averaged away.
- **Fix:** rebuild the set directly from the winning observation's own service lines.
- **Numbers that moved:** claims with a `$0` line 156 → **155**; `CARC 11` in that table
  26 → **25**; pre-bill "not reviewed" denial rate 14.2% → **14.1%**; `CARC 97` claim count
  21 → **19**. All corrected in `docs/DATA_FINDINGS.md` before publication.

**W4 — file-level dedup assumption (would have been material in Phase 1).**
AI's first design note said "detect the duplicate remittance with SHA-256 of the file".
- **How it was caught:** actually hashing both Q2 files showed `7a9df943…` ≠ `86a74d76…`.
  Segment-by-segment diff showed **0 differing segments outside the envelope**.
- **Fix:** `payload_sha256` over all segments outside `ISA`/`GS`/`GE`/`IEA`. Both hashes are
  stored; dedup keys on payload. Cost of the mistake had it shipped: **+$47,461.62 paid,
  +453 claims, +41 false denials.**

**W5 — overstating what the AI layer proves.**
AI initially drafted language implying the categorisation "achieves 100% with AI".
- **How it was caught:** the 100% comes from a **pure rules function** with no model involved
  (`docs/DATA_FINDINGS.md` §9). Claiming model credit for it would be unfalsifiable.
- **Fix:** `docs/AI_EVALUATION.md` will report **rules-only** as the primary number, state that
  the 40-row labelled set cannot demonstrate LLM added value, and test the model separately on
  refusal handling, citation grounding and adversarial inputs.

**W6 — committed a derived file containing patient names.**
`worklog_extracted.csv` (columns include `Patient`, e.g. `"Wallace, J"`) was produced by
`tools/extract_worklog.ps1` and `git add -A` swept it into the first commit alongside the
client-supplied pack.
- **How it was caught:** reviewing the commit's file list before reporting Phase 0.
- **Fix:** `git rm --cached` + `.gitignore` entry + **amending the commit**, so the blob leaves
  history rather than being removed by a follow-up commit. Recorded as decision **D13**: the
  client pack stays as received; derived extracts containing identifiers are regenerated.
- Verified no patient *values* appear in `docs/` or `tools/` — only column names.

### AI was uncertain and said so

- **CARC 18 → "Payer error"** is an extrapolation with no labelled example. It affects 6 open
  denials. Raised as **[Q5]** rather than quietly decided.
- **Whether "recoverable" should exclude the 26 credentialing denials** changes the headline
  from **$21,245** to **$16,785**. Raised as **[Q2]** with both numbers shown.
- **Whether line-level denials belong in the headline total** is **[Q1]**.
- **Pre-bill review efficacy:** 12.7% vs 14.1% is an *unadjusted* comparison over 1,164
  claims. It is not a controlled result and is stated as an association, not causation.

---

## Phase 1 — ingestion and reconciliation (2026-10-07)

### AI helped

- **Writing the conservation invariant before the code.** `rows_in = matched + exceptions` was
  agreed as a *test* first, which forced one `RowDisposition` per input row carrying exactly one
  primary reason. Every alternative design had two places to record a row's fate, and every one
  of them lost the identity as soon as a row was both unmatched *and* had a bad date.
- **Insisting on the second reconciliation population.** The first implementation summed
  adjustments over claim-linked observations only. Deriving `CO + PR = $114,159.64` as an
  independent check is what exposed the $159.60 rather than letting it disappear into a total
  that still "looked right".
- **Structuring `DECISIONS.md` entries as decision → alternatives → why → trade-off**, which
  made it awkward to write "because it is simpler" and forced the real cost of each choice out
  into the open.

### AI was wrong

**W7 — envelope control counts compared against the wrong fields (would have raised a false warning).**
`IEA01` (number of `GS` envelopes) was being checked against `ISA13` (interchange control
number), and `IEA02` (number of `ST` transaction sets) against the `GE` count.
- **How it was caught:** while writing the envelope-pairing assertions in `IngestGuardTests`,
  stating what each field is *for* made the mismatch visible on the page. It had passed every
  run before that, because all four pack files happen to have control numbers that fit.
- **Fix:** `GE01 ↔ ST count`, `IEA01 ↔ GS count`, `IEA02 ↔ ISA13`.
- **Severity:** would have made a well-formed file with a non-1 control number look corrupt.
  Per **D11** this is a warning, not a failure, so it would have been a *wrong warning* rather
  than a rejection — harder to notice, not easier.

**W8 — parsed remittance observations were never attached to their claim (material, caught by a golden test).**
`IngestPipeline` linked observations into a local list and built the file's observations, but
never added them to `claim.Observations`.
- **How it was caught:** the golden test asserted **1,164** adjudicated claims and got **0**.
  An off-by-something would have produced 1,163; zero meant the data never arrived.
- **Fix:** attach to both the claim and the file at the point the observation is accepted.
- **Numbers that were at risk:** every adjudicated/denied figure, the whole status ledger, all
  adjustment totals. None were published before the test passed.

**W9 — adjustments scoped to persisted observations only (material, $159.60).**
- **How it was caught:** `CO + PR = $114,159.64` was computed independently of the report and did
  not match.
- **Fix:** sum over all imported observations and report the unattributable portion separately
  (**D22**). Final: CO **$108,516.40**, PR **$5,643.24**, unattributed CO **$159.60** (3 events).

**W10 — X12 tag validation assumed a 2-character tag.**
The check rejected anything not exactly two characters. X12 tags are 2–3 alphanumeric.
- **Why it matters:** a validator stricter than the specification converts *valid* segments into
  exceptions, which is a data-loss bug wearing a safety costume. It also breaks the
  `rows_in = matched + exceptions` story, because those rows would appear as exceptions for a
  reason that is not a property of the data.
- **Fix:** `tag.Length is < 2 or > 3 || !tag.All(char.IsLetterOrDigit)`.

**W11 — the claim natural key omitted the claim reference.**
Without `+ CLP` the guard could not tell two different claims apart, so a legitimate second
observation was refused as `DUPLICATE_CLAIM_OBSERVATION`.
- **How it was caught:** `IngestGuardTests` — an orphan-remit test expected 1 adjudicated claim
  and got 0, because the *known* claim's own payment event had been refused.
- **Fix:** `NaturalKeyFor(claimReference)`, carrying `PayerId|TraceNumber|CheckDate|PayerClaimControlNumber|<canonical id>`.
  The canonical id rather than the raw `CLP01` so `GPP-2026-001234` and `GPP2026001234` still
  collide as they should.

**W12 — role claim written as the literal string `"role"` (authorization was effectively absent).**
`RequireRole` resolves through `ClaimsPrincipal.IsInRole`, which reads `ClaimTypes.Role`.
- **How it was caught:** the very first `AuthorizationTests` run. Every valid token returned
  **403** — and, separately, requests with *no* token returned **200**, which is the next bug.
- **Fix:** emit `ClaimTypes.Role` and `ClaimTypes.Name`.

**W13 — 401 and 403 responses were returning HTTP 200.**
`HandleChallengeAsync` / `HandleForbiddenAsync` wrote a JSON body but never set the status
code; `Response.WriteAsJsonAsync` defaults to **200**.
- **How it was caught:** the same test run — `Expected: Unauthorized, Actual: OK`. Overriding
  the base method replaced behaviour that came for free.
- **Why it matters more than it looks:** a refused request that reports success is the worst
  possible failure mode for an authorization layer, because monitoring, retries and every
  caller agree that everything is fine.
- **Fix:** set `StatusCodes.Status401Unauthorized` / `403Forbidden` explicitly before writing,
  and assert in tests that the 401 body is byte-identical for a missing and an unknown token.

**W14 — test configuration injected through host-builder settings never reached the entry point.**
`WebApplicationFactory` settings are applied after the API's top-level statements have already
read `ConnectionStrings__Denials` and thrown.
- **How it was caught:** 23 tests failing at `Program.cs:line 32` with "connection string is not
  set" rather than any assertion.
- **Fix:** publish the test configuration as **process environment variables** in a static
  constructor of the factory — the one source guaranteed to exist before the entry point runs,
  and race-free because every test in the process uses the same values.

**W15 — `--out` resolved against the wrong working directory.**
`dotnet run` starts the app in the *project* folder, so the deliverable landed in
`src/AQ.Denials.Api/docs/` while the command reported success.
- **How it was caught:** checking the file actually existed rather than trusting the exit code
  — the same habit that caught **W16**.
- **Fix:** resolve relative `--out` against the repository root.

**W16 — a shell pipeline swallowed `phi_guard`'s exit code.**
The guard ran but its non-zero status was consumed by the following command in the pipeline, so
`git commit` reported success regardless.
- **How it was caught:** re-running the guard as the last command in the chain and reading
  `$LASTEXITCODE` explicitly.
- **Fix:** check `$LASTEXITCODE` after every command that can fail; the pre-commit hook is now
  verified by attempting a real commit that *must* be rejected, not by reading its source.

**W17 — a PowerShell glob counted more rows than the segment it claimed to count.**
`-like 'DTM*405*'` matches any line containing `DTM`, then `405` later — not the X12 segment
`DTM*405`. It over-counted.
- **How it was caught:** the total did not match the number of transactions.
- **Fix:** X12 scans run through `tools/*.mjs` with a real tokenizer; the PowerShell one-liners
  are no longer used for segment statistics.

### AI was uncertain and said so

- **Whether read endpoints should hit the database** was a real fork (**D16**). Chosen for one
  code path over DB-backed reads; flagged as a decision with a stated cost rather than a
  settled preference, with the compensating test (`stored == derived`) written down as pending
  rather than quietly assumed.
- **Default seeded credentials in `.env.example` and `docker compose`.** The brief asks for
  seeded users and a one-command start, which argues for defaults; a real deployment argues
  against them. Resolved as: defaults exist for local evaluation, `SEED_USERS` has **no default
  in code**, and both README and `.env.example` say to replace them. Object if you would rather
  the compose file refuse to start without explicit tokens.
- **Whether the claim view should carry patient identity** (**D18**). Left out for Phase 1
  because nothing in this phase needs it; called out as a decision so that when Phase 2's UI
  needs it, it is a deliberate grant and not an oversight.
- **The checksum value changed** during the phase (`f4600390…` → `50c176cb…`) because **D23**
  made it distinguish claim-level from service-level adjustments. No reconciliation figure
  moved. Stated plainly rather than presented as if the checksum were always what it is now.
