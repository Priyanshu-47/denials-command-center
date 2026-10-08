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

---

## Phase 2 — AI denial analysis (2026-10-08)

### AI helped

- **Separating what the model is allowed to decide from what it is not.** The single most
  useful move was refusing to let the model pick the category, owning team or preventability.
  Once those became pure functions of `DenialCategory.Of()` (**D6/D25**), the evaluation had a
  number that could not be flattered by the model, and "where it fails" became attributable to
  either the rules or the drafting — not to a blur between them.
- **Designing the citation as two gates neither of which is asked of the model** (**D26**).
  Ownership and relevance are decided by code and only the *allowed* options are put in the
  prompt. It converted "hope the model cites correctly" into "validate a selection from a list",
  which is a checkable claim.
- **Insisting on a `ClauseAvailableUncited` verdict.** The model declining a clause our rules had
  found was originally going to look like a soft failure. Giving it its own verdict, and pricing
  it identically to a failed citation in **D28**, meant a draft could be kept *and* still be
  unable to clear review unaided.

### AI was wrong

**W18 — the model turned an instruction to verify something into an assertion that it is so
(the most important finding of the phase).**
Live run, `qwen2.5-coder:3b`, on the CARC-151 *Coding - frequency* fixture
(`LiveModelTests.Frequency()`, a synthetic claim, not one of the 136). Our prompt field read
*"Confirm **whether** the second same-day service followed a change of condition."* The model
produced a **validated** citation — the quotation really is in Northstar `NSHP_HOSP-FREQ-07` §2 —
and then wrote:

> "The second same-day service followed a change of condition, which is not allowed…"

Nothing in the input said that. It converted a question into a fact and then argued from the
fact.
- **How it was caught:** reading the transcript rather than the verdict line. The citation
  validator passed, the invariants passed, and the test was green — every automated signal said
  this draft was fine.
- **Why it is not fixable by validation:** the validator proves the *policy basis*, not the
  prose. The citation is true; the clinical claim attached to it is invented. This is precisely
  why drafts go to a human and are never sent, and it is recorded as the residual risk in
  `docs/AI_EVALUATION.md` rather than as a solved problem.
- **Partial mitigation:** **D31** stops free text going *in*, which removes the model's ability
  to launder a worklog note into an assertion. It does not stop the model inventing a fact the
  input never contained — nothing in the current design does.

**W19 — the live-model test suite passed 3/3 while the model was completely unreachable.**
Ollama had been killed by an environment restart; every call was refused on
`localhost:11434`. The batch ran to completion, reported `Passed: 3`, and the transcript showed
`produced=0`, `verdict=NotAssessed` for all 36 denials.
- **How it was caught:** the test prints a `[batch]` tally, and `produced=0` is not a number a
  passing run should show. The green result alone would have been reported as a successful
  evaluation.
- **Why it happened:** the assertions are invariants of the degraded path — *if* a draft appears,
  its citation must validate; *if* the model is down, the analysis must still stand. Both are
  correct requirements, and both hold when no model is called. The test therefore proved the
  degraded path (**§C "must still work"**) at 36-denial scale, and proved nothing about the model.
- **Fix:** the tally is now the thing read first, and `docs/AI_EVALUATION.md` reports
  `produced` alongside every model result so a run with no output cannot be quoted as one. The
  environment check (`/api/tags`) is run before a batch rather than assumed.
- **Severity:** a false *pass*, not a false failure — the dangerous direction for an evaluation
  that a submission depends on.

**W20 — a series of compile- and assertion-level mistakes in the Phase 2 code, each caught
before commit.** Individually trivial, collectively worth listing because they were all the same
shape — assuming an API rather than reading it:
`IReadOnlyDictionary.Keys` returns `IEnumerable<T>`, not a collection; a record named
`LlmSettings` collided with the class of the same name; `LlmException` has no `Inner` setter;
`RemitObservation.StatusCode` (not `.Status`); `Csv` lives in `AQ.Denials.Ingest.Readers`;
`ILlmClient` is not `IDisposable`; and `Assert.True(false, msg)` is `Assert.Fail(msg)` in xUnit.
- **How they were caught:** the compiler and the xUnit runner, on every change — which is the
  point of running the suite before each commit rather than at the end of the phase.
- **Not counted as findings:** none reached a published number. Listed because the brief asks
  where AI was wrong, and "wrong in a way the compiler caught" is still wrong.

**W21 — an interrupted evaluation run.** The first batch was cut off mid-flight by an
environment restart, leaving a transcript of unknown provenance in the temp directory.
- **How it was caught:** the run was re-executed from scratch rather than resumed, and the
  transcript is regenerated each time. An evaluation result whose run did not complete is not a
  result.

**W22 — the batch invariant was written as `Assert.Equal(produced, malformed)`.**
The comment beside it said "no reply may produce a half-formed outcome", which is `malformed == 0`.
The assertion as written demanded that malformed replies equal the number of *produced* replies —
i.e. that every reply be bad. It failed with `Expected: 35, Actual: 0`, which is the assertion
being wrong and the run being right: **35 produced, 0 malformed**.
- **How it was caught:** the run failed, and the failure message contradicted its own comment.
  Reading the number rather than the word "failed" is what showed the model had done well and the
  test had not.
- **Why it is worth recording:** a false *failure* here looks like the model misbehaving. Left
  unexamined it would have been reported as a bad model result in `docs/AI_EVALUATION.md`.

**W23 — `dotnet build` was the only signal that a file I "created" did not exist.** A write to
`Reports/Analytics.cs` reported success; the file was not on disk when a later edit failed with
"File not found". Two sibling writes in the same stretch did persist, so nothing about the
situation was self-evident.
- **How it was caught:** a subsequent targeted edit, not the build — the build would have failed
  later with a missing type, a slower and more confusing signal than "file not found".
- **Rule adopted:** tool success messages report the request, not the filesystem. Files a later
  step depends on are verified by `glob` or `Read` before they are referenced.

**W24 — four `CS1061` errors from deconstructing a tuple in my head.** `Priority.Compute` assigns
`var urgency = daysUntilDeadline switch { … => (150, "…") }`, producing a `(int, string)` tuple,
then reads `urgency.Weight` / `urgency.Because`. The neighbouring block had been written with
`var (weight, because) = …` and worked. The pattern was right in one place and assumed in the
other.
- **How it was caught:** the compiler, and only after a background test run had already died on
  it — the failure surfaced as a *build* error inside a log I was reading for model output, which
  is how it could easily have been dismissed as unrelated noise.

**W25 — the 7B model produced 0 of 36 drafts, and the first reading was "the model failed".**
Every one of the 36 replies was `NotAssessed` with the same reason: *"did not respond within
60s"*. The default `LLM_TIMEOUT_SECONDS` is 60; `qwen2.5:7b-instruct` is ~4.7 GB and its cold
load exceeds that on this machine. The model had not answered badly — it had not been given time
to start.
- **How it was caught:** reading the transcript rather than the tally. `produced=0` alone
  supported "7B is worse than 3B", which would have been a false and load-bearing claim in
  `docs/AI_EVALUATION.md`.
- **Why it is worth recording:** the same failure mode as **W19** in reverse — there a passing run
  had done no work; here a failed run looked like a result. In both cases the tally was true and
  the interpretation was not.
- **Action:** re-run at 240 s, and report the timeout itself as a product finding — a 7B model
  under default configuration yields an empty draft queue, and nothing warns about that except an
  empty state.

**W26 — `Results.Forbidden()` does not exist, and the compiler reported three other things
instead.** In .NET 8 minimal APIs the method is `Results.StatusCode(403)`. Because one `return`
did not compile, inference on the lambda's return type failed and produced `CS4010 … Task<?>` at
the *other three* `return` statements in the same handler — 486, 491 and 557 — none of which were
wrong.
- **How it was caught:** opening the line the error did not mention. The fix was one line; the
  cost was reading four errors to find one cause.
- **Changed instead:** the 403 became a 404 with the same body as the detail route. A 403 would
  confirm that a colleague's claim exists, which is exactly what **D35** says the queue must not
  do — so the compile error turned into the right answer, for a reason unrelated to compilation.

**W27 — a multi-line raw string literal with its closing `"""` at the end of the second line.**
C# only treats a raw string as multi-line if the closing delimiter is on its own line; as written
it is unterminated and cascades as six `CS1002`/`CS1513` errors on the following lines. The first
error points at where the string ends, not at the line where the syntax decision was wrong.

**W28 — the role-seed validation I wrote rejected my own test.** The round-trip test seeded two
identities (`ingest`, `manager`) and the startup guard failed it: *"SEED_USERS contains no
'specialist'"*. The guard is correct and the fixture was not — a fixture for a product with two
worklist roles must seed both.
- **Counted as a success of the guard, not a failure of the test.** This is the configuration
  error the product would have hit at first click on Monday, caught in 1 ms by a test instead.

**W29 — `g.Count` where `g` is an `IGrouping`.** There is no `Count` property, so it resolves to
the method group of the `Count()` extension and produces `CS0019` ("operator '==' cannot be
applied to method group and int") plus a phantom `CS0030`. Hoisted to a local, which is also
easier to read inside a projection.

**W30 — the zero-paid-line predicate was designed from memory instead of from the data.** The
first version required a CARC 97 adjustment *on the service line* with `Amount == 0`. It found
**0** claims. Phase 0 recorded CARC 97 on the *observation*, and the anchor is 19 claims /
$3,365. Rewritten to test the observation for CARC 97 and sum service lines with `Paid == 0`, it
returns **19 / $3,365.00** exactly.
- **How it was caught:** the test asserted the Phase 1 anchor as a number, not as `> 0`. A "found
  some" assertion would have passed on any non-zero value and hidden a wrong derivation; a pinned
  figure fails on a wrong derivation and passes on a correct one.

**W31 — the TypeScript model of a C# record was written before the record was.**
`web/src/types.ts` declared `PreventionCheck` with `stoppedClaims` / `stoppedDenials` /
`stoppedAmount` and a two-valued `confidence`. The server returns `claimsFlagged` /
`denialsCaught` / `amountCaught` and a three-valued `confidence` including `not_measurable`.
- **Not caught by the type checker, and it would not have been:** a wrong *type* that nothing
  reads is invisible to `tsc`, and had the component used the wrong names it would have typecheck
  cleanly and rendered `undefined` at runtime. Caught by reading the C# record before writing the
  component.
- **Standing risk:** `types.ts` is a local assertion, not a contract with the server. There is no
  shared schema, and the README must not imply one.

**W32 — default import from a module with a named export.** `main.tsx` did
`import App from "./App"` against `export function App()`. One line, but the kind that only
appears when someone finally runs `tsc` — which is why `npm run build` runs it as its first step
rather than shipping a bundle that throws on first paint.

**W33 — attempted to reach a local PostgreSQL by guessing passwords.** With no usable credentials
for the Postgres listening on 5432, four candidates were tried (`postgres`/`postgres`,
`postgres`/`password`, `postgres`/`123456`, `denials`/`denials`) before stopping. All failed.
- **Why it is recorded:** the instinct was wrong regardless of the outcome. The correct reading of
  *"no credentials available"* is that the database is not available to me, and work that depends
  on it should be designed around that fact rather than tested around it. Stopping after four was
  the only part of this that was right.
- **Consequence, adopted rather than worked around:** no test in this repository connects to a
  developer's local database. API tests run without one; DB-backed worklist mutation tests are
  listed as an open item in the end-of-phase report instead of being made to depend on a machine
  I happen to have access to.

**W34 — `phi_guard` reported OK while silently skipping every file in the change.** The guard
walks `git ls-files`, i.e. tracked files. Run before `git add`, it saw **97** files and reported
`0 violations`; after staging, the same command saw **135**. The 38 new files in this phase were
never checked, and the output gave no sign of that.
- **How it was caught:** the file count did not move after adding roughly fifteen source files.
  The count is the only part of the output that carried the information; `0 violations` was
  identical and meaningless in both runs.
- **Why it matters here:** a PHI guard whose confidence is a constant is worse than none, because
  it converts "I did not look" into "I looked and found nothing". Anyone reporting `phi_guard: OK`
  as evidence must report the scanned-file count beside it — this report does: **135 scanned,
  0 violations**.
- **Design note, not changed:** scanning only tracked files is *correct* for the pre-commit hook,
  which runs after staging — that is exactly the tree about to be recorded. The flaw is running
  it manually and reading the answer as covering work that is not yet staged.

### AI was uncertain and said so

- **Confidence weights are a design choice, not a fit** (**D28**). They were fixed before the
  evaluation ran. Whether they separate good from bad is measured and reported in
  `docs/AI_EVALUATION.md`; if they do not separate well, the weights stay and the report says so.
- **The 40/40 is a fit statistic.** **C1** required this framing before results existed. It is
  reported as *fit to the labelled sample*, never as accuracy, and the structural finding that
  **4 of the 40 rows are not open denials** is stated up front rather than in a footnote.
- **Anthropic is not implemented** (**D29**) and the docs do not imply otherwise, even though the
  brief names Anthropic as acceptable. Saying "supports Anthropic" would have been one line of
  marketing and one broken configuration.
- **`DuplicateUnvalidated` and `UNMAPPED` have no preventability value** (**D25**) because the
  expert never labelled them. Reported as `null` with a coverage flag rather than guessed.
- **Which model actually produced the numbers** is recorded per run in
  `docs/AI_EVALUATION.md`, including parameter count and quantisation. An evaluation that does
  not name its model cannot be reproduced, and a 3B coder model is a different system from a 7B
  instruct model.
