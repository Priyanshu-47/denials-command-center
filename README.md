# Denials Command Center

A denial-management system for payer remittance (835) files, a claims export,
and a manual denials tracker — ingestion and reconciliation, AI denial analysis
with appeals, a two-role worklist, and manager analytics with pre-bill
prevention rules. Everything it reports is derived from those files by code; no
figure in this repository was typed in by hand.

**Stack (fixed by the brief):** C# / .NET 8 Web API · EF Core · PostgreSQL ·
React + TypeScript (Vite). Python is used only for AI evaluation, if at all.

---

## Quick start

Three steps, in this order:

```bash
# 1. Unzip the assignment data pack somewhere on your machine.
#    You should end up with a folder containing README.txt, claims_export.csv,
#    denials_worklog.xlsx, labeled_denials_sample.csv, payer_rules.csv,
#    carc_rarc_reference.csv, claim_adjustment_group_codes.csv,
#    payer_policies/ and remits/.

# 2. Tell the system where that folder is, and seed your identities.
cp .env.example .env
#    then edit .env: set DATA_DIR to the unzipped folder's path.

# 3. Start everything.
docker compose up --build
```

The **web console** is then on **http://localhost:5173** (nginx inside the
container), the API on **http://localhost:8080**, Swagger UI at
**http://localhost:8080/swagger**, and the health probe at
**http://localhost:8080/health** (no token required — a container probe has
none).

Sign in to the console with one of the four seeded tokens from `.env`
(`SEED_USERS`). The two the product is built around:

| token | role | sees |
|---|---|---|
| `local-specialist-token-0000000` | `specialist` | own queue + unassigned pool; status, notes, drafts |
| `local-manager-token-0000000000` | `manager` | everything; reassign, bulk drafting, analytics |

The console talks only to its own origin — nginx serves the bundle and forwards
`/api`, so there is no CORS surface and no second URL a mis-set variable could
send the bearer token to.

The data pack is mounted into the container **read-only** at `/data`. Nothing
in this system writes to, relocates, or deletes the originals.

> `docker compose up` will refuse to start with a clear message if `DATA_DIR`
> or `SEED_USERS` is unset, or if `POSTGRES_PASSWORD` is still the placeholder
> — it will not start half-configured.

### If the data pack is missing

The API fails fast on startup and lists **every** file it could not find in a
single message, rather than starting and quietly ingesting what happened to be
present.

---

## Headline figures

Every number below is produced by code from the data pack and asserted by the
test suite — none was typed in, and none appears in only one place.

| | |
|---|---|
| Claims submitted | **1,222** / **$242,585.00** |
| Remittance cash (excluding duplicate files) | **$118,220.36** |
| Duplicate-file cash, reported separately | **$47,461.62** |
| Exception rows | **2,644 = 2,554 + 90** (`rows_in = matched + exceptions`) |
| **Open denials** | **136** / **$27,780.00** |
| ↳ still recoverable | **79** / **$16,785.00** |
| ↳ blocked by payer policy | **26** / **$4,460.00** |
| ↳ expired / lost | **31** / **$6,535.00** |
| Billed but never adjudicated (no remittance) | **58** / **$12,600.00** |
| Accepted claims with a $0 service line | **19** / **$3,365.00** |

Two rules about these numbers:

- **The three recovery buckets always sum to the open book** (79 + 26 + 31 = 136,
  $16,785 + $4,460 + $6,535 = $27,780). `ManagerViewsTests` asserts it.
- **Money at risk is never one number.** The 136 open denials, the 58 never
  adjudicated, and the 19 zero-paid lines respond to three different actions and
  are never added together — on the API, on the screen, or in this table.

---

## Environment variables

Everything is read from the environment. There are no secrets in this
repository.

| Variable | Required | Default | What it is |
|---|---|---|---|
| `DATA_DIR` | **yes** | — | Path to the unzipped data pack. Mounted `/data:ro` in compose. |
| `ConnectionStrings__Denials` | **yes** | — | ADO.NET connection string for PostgreSQL. |
| `SEED_USERS` | **yes** | — | JSON array of `{"token","name","role"}`. Tokens ≥ 16 chars; four roles: `reader`, `ingest`, `specialist`, `manager`. |
| `POSTGRES_USER` | yes | `denials` | Database role created by the `db` service. |
| `POSTGRES_PASSWORD` | **yes** | — | Database password. No code default on purpose. |
| `POSTGRES_DB` | no | `denials` | Database name. |
| `AUTOMIGRATE` | no | `true` | Apply checked-in EF migrations at startup. |
| `API_PORT` | no | `8080` | Host port for the API. |
| `WEB_PORT` | no | `5173` | Host port for the web console. |
| `ASPNETCORE_ENVIRONMENT` | no | `Production` | `Development` relaxes `SEED_USERS` to local dev defaults. |
| `LLM_PROVIDER` | no | *(empty)* | Provider id; read by `ILlmClient` only. Empty = degraded mode. |
| `LLM_BASE_URL` | no | *(provider default)* | e.g. `http://host.docker.internal:11434/v1` for Ollama outside the compose network. |
| `LLM_API_KEY` | no | *(empty)* | Provider key; never written to source, logs or DB, and never sent in a prompt. |
| `LLM_MODEL` | no | *(empty)* | Model id, e.g. `qwen2.5:7b-instruct`. |
| `LLM_TIMEOUT_SECONDS` | no | `60` | Per-request budget. **A 7B model's cold load exceeds this** — see `docs/AI_EVALUATION.md`. |

`SEED_USERS` has **no default in code**, by design: a service that falls back
to "any token works" is the single most likely way for this to reach production
looking secure and behaving open. `.env.example` ships working local tokens so
`cp .env.example .env` gives you a running system immediately — replace them
before anything is shared.

Outside `Development` the API **also refuses to start** if the seeded roles do
not include both `specialist` and `manager`. The product is defined as a
two-role application; a config seeding one of them is broken, not degraded, and
it would otherwise fail at the first click.

The supported wire format for `LLM_*` is **OpenAI-compatible chat completions**
(OpenAI, Ollama, vLLM, and Gemini gateways that expose it). The Anthropic
Messages API is **not** implemented — see `docs/DECISIONS.md` D29. Saying it
was would be one line of marketing and one broken configuration.

If `LLM_PROVIDER` is set but unreachable, the API **fails at startup** rather
than starting and quietly producing no drafts for a week. If it becomes
unreachable *after* startup, the analysis stands and only the draft is missing,
with the reason shown.

---

## Running without Docker

```bash
dotnet tool restore                                  # dotnet-ef, pinned
dotnet restore AQ.Denials.sln
dotnet test src/AQ.Denials.Tests/AQ.Denials.Tests.csproj
```

You need the .NET 8 SDK. (`dotnet --version` must report `8.x`.)

---

## Generating the reconciliation report

The report is produced by the same pipeline the API and the tests use, so the
two can never disagree.

```bash
DATA_DIR=./AQSoft_Assignment_Data_Pack_1 \
  dotnet run --project src/AQ.Denials.Api -- --report
```

With no `--out`, it prints to stdout. With `--out docs/PHASE1_RECONCILIATION.md`
it writes the file. Either way it exits **non-zero** if the system's own
invariants are broken, so it can be used as a CI gate and not just as a
document generator.

---

## API

| Method | Path | Policy | Notes |
|---|---|---|---|
| `GET` | `/health` | *(none)* | Container probe: counts, checksum, no PHI. |
| `GET` | `/api/reconciliation` | `reader` | Full reconciliation as JSON. |
| `GET` | `/api/reconciliation/report.md` | `reader` | Same figures, as Markdown. |
| `GET` | `/api/exceptions?reason=&claimId=` | `reader` | Exception rows + `rows_in = matched + exceptions`. |
| `GET` | `/api/claims/{claimId}` | `reader` | One claim, its lines, and its full remittance history. Patient identifiers are omitted. |
| `GET` | `/api/audit` | `reader` | Last 500 audit entries. |
| `POST` | `/api/ingest` | `ingest` | Re-run ingestion; idempotent by full-state checksum. |
| `GET` | `/api/analytics` | `reader` | Money at risk, breakdowns, pre-bill rates, trend. |
| `GET` | `/api/prevention` | `reader` | Pre-bill checks with benefit *and* burden, plus machine-readable rules. |
| `GET` | `/api/worklist` | `worklist` | Prioritised queue; a specialist gets own + unassigned only. |
| `GET` | `/api/worklist/{claimId}` | `worklist` | Detail: analysis, draft, timeline, before/after event history. |
| `POST` | `/api/worklist/{claimId}/status` | `worklist` | Change status with a note; writes the event. |
| `POST` | `/api/worklist/{claimId}/assign` | `manage` | Reassign. **Manager only.** |
| `POST` | `/api/worklist/{claimId}/draft` | `worklist` | Draft (or re-draft) the appeal note for one claim. |
| `POST` | `/api/worklist/drafts` | `manage` | Bulk drafting, oldest window first. **Manager only.** |

Authenticate with `Authorization: Bearer <token>`.

Three policies rather than role checks in handlers: `reader` (all four roles),
`worklist` (specialist + manager), `manage` (manager only). The answer to "who
may call this" lives in one place per endpoint and can be grepped.

Authorization is enforced **server-side** on every route. A missing token and
an unknown token both return `401` with an identical body — an attacker must
not be able to tell "wrong token" from "no token" by reading the response. A
valid token with the wrong role returns `403`. Tokens are compared with a
fixed-time comparison so timing does not reveal a prefix.

Two deliberate exceptions to "403 means wrong role": a claim that belongs to
another specialist returns **404**, not 403, because a 403 would confirm it
exists and is somebody's (D35). And outside `Development` the API **refuses to
start** if `SEED_USERS` is missing or seeds only one of the two worklist roles —
a permissive default here would boot green and fail at the first click.

---

## Repository layout

```
src/
  AQ.Denials.Core       money, canonical model, checksum, exception reasons
  AQ.Denials.Ingest     X12 835 parser, CSV/XLSX readers, pipeline, report
  AQ.Denials.Rules      denial categories, payer windows, recovery buckets,
                        taxonomy, recovery assessment, prevention checks,
                        priority scoring, policy parsing and citation gating
  AQ.Denials.Llm        ILlmClient, NullLlmClient, OpenAI-compatible client
  AQ.Denials.Api        EF Core, auth, endpoints, analytics, worklist, report mode
  AQ.Denials.Tests      xUnit suite (parser, money, reconciliation, guards,
                        taxonomy, citation, drafting, adversarial, evaluation,
                        priority, prevention, authorization)
web/                    React + TS console (Vite), nginx, Dockerfile
docs/                   decisions, findings, AI usage, evaluation, hours, memo
tools/                  dev-only Node scripts; PHI guard
```

---

## Data handling

* The assignment data pack is **never committed**. `.gitignore` and
  `tools/phi_guard.mjs` (wired as a pre-commit hook) both block it.
* **Four exceptions**, by user decision (D24): `payer_rules.csv`,
  `carc_rarc_reference.csv`, `claim_adjustment_group_codes.csv` and
  `payer_policies/*.md` are committed. They are pure reference data — day-count
  windows, code meanings, policy prose — with no person in any of them, and they
  are what the system reasons from, so you can read the rules without unzipping
  anything. **This does not make the repository standalone**: `claims_export.csv`
  and `remits/*.835` stay out, so `dotnet test` still needs your own pack.
* `claims_export.csv` and `*.xlsx` are ignored **by file name**, anywhere on the
  tree, not just inside the pack — unzipping to a different folder and running
  `git add .` must not pick up names, DOBs and member IDs.
* `.dockerignore` keeps the pack out of every image layer.
* Money is `decimal` in C# and `numeric(18,2)` in PostgreSQL. No `float`,
  `real` or `double` appears anywhere in the money path — a half-cent rounding
  artefact in a denial report is a wrong number, not a cosmetic one.
* Patient names, DOB and member IDs are never written to logs.
* Query parameters reach the database as parameters — there is no interpolated
  SQL in this repository.

---

## What this delivers

**A — Problem memo.** `docs/PROBLEM_MEMO.md`: what the data actually got wrong, including four
labelled rows that are not open denials, an unmapped CARC, a policy claim our analysis
disagrees with, and the gaps left by this build.

**B — Ingestion + reconciliation.**

1. Defensive X12 835 parsing; malformed segments become exception rows, never
   crashes.
2. Remit dedup by normalised payload hash (envelope excluded). A resent file is
   kept, flagged, and named — not silently dropped, and contributes `$0.00`.
3. A canonical model: one record per claim with its full payment and denial
   history.
4. Idempotency: ingesting twice, or in a different order, produces an identical
   full-state checksum.
5. An exceptions table with reason codes, and the invariant
   `rows_in = matched + exceptions` asserted on every run.
6. A reconciliation report (API endpoint + generated Markdown) that reproduces
   every Phase 0 figure.

**C — AI denial analysis.** Root cause, owning team, preventability before billing and next
action are computed **deterministically** from the taxonomy — the model never invents a category.
Appeal drafts cite the exact policy section, and only a section belonging to the payer that
denied the claim, with the quote checked against the source after the model answers. Confidence
below threshold routes the item to human review. With no LLM configured the product still runs:
only the draft is missing, and the reason is shown. Evaluation is in
`docs/AI_EVALUATION.md`, and it reports *fit to the labelled sample*, never accuracy.

**D — Worklist application.** Two roles. The specialist gets their own queue plus an unassigned
pickup pool, and can change status and add notes; the manager sees everything and reassigns.
The queue is prioritised by an explainable score. The claim detail page carries the full
timeline — billed → paid/denied → **and every action taken since**, with who, what, when, before
and after, written in the same transaction as the change.

**E — Manager analytics.** Money at risk split into recoverable versus already lost (never
summed with claims that were never adjudicated), trends and breakdowns by payer, reason,
provider, coder and facility, and a prevention view showing which pre-bill checks would have
stopped the most denials and money — each check paired with the burden it would impose, and
exported as machine-readable JSON rules.

---

## Documentation

| File | Contents |
|---|---|
| `docs/DECISIONS.md` | Every decision, the alternatives, why, and the trade-off. |
| `docs/DATA_FINDINGS.md` | What is actually in the pack, with the query behind each figure. |
| `docs/PHASE1_RECONCILIATION.md` | The generated reconciliation report (**B**). |
| `docs/PROBLEM_MEMO.md` | **A** — the problems found beyond what was asked. |
| `docs/AI_USAGE_LOG.md` | How AI was used, and where it was wrong (W1–W34). |
| `docs/AI_EVALUATION.md` | **C** — evaluation against the labelled sample, adversarial cases, and which model actually ran. |
| `docs/DEMO_SCRIPT.md` | The 5–8 minute recording script, timed. |
| `docs/HOURS.md` | Time log. *(maintained by hand — not edited by AI)* |

### Running the tests

```bash
dotnet test                      # everything except the live-model tests
npm --prefix web run build       # typecheck + bundle the console
node tools/verify_phase0.mjs     # Phase 0 invariants
node tools/phi_guard.mjs         # PHI guard (scans TRACKED files — run after `git add`) 
```

The live-model tests (`LiveModelTests`) return early and pass trivially unless
`LLM_PROVIDER` is set, so `dotnet test` is meaningful on a machine with no model.

---

## Known gaps

Listed here rather than left for a reviewer to find, because a gap nobody wrote
down is the expensive kind.

1. **No DB-backed tests for worklist mutations.** Status changes, reassignment and
   the `WorkItemEvent` before/after history are exercised through the endpoints in
   development but have no automated test: the suite deliberately never connects to
   a developer's local database, and no database was reachable from the build
   machine. Authorization for those routes *is* covered (401/403).
2. **`stored == derived` is unasserted.** The design guarantees the worklist
   recomputes its analysis on every read (D34), but nothing yet proves the stored
   human state and the derived analysis agree claim by claim.
3. **Five future-dated rows are counted but not surfaced in the report** — the
   `5 future-test-resolved dates` finding from Phase 1 is asserted, not displayed.
4. **`GS08` and `ST01` are parsed but not asserted.** The interchange identifiers
   reach the model and are not pinned by a test.
5. **No 3B-vs-7B quality comparison.** Two attempts at the larger model were
   cancelled by environment restarts and one failed at build. `docs/AI_EVALUATION.md`
   §8 records exactly what each attempt produced and why none of them is a
   measurement of the model.
6. **Draft quality is only partly scored.** `Valid` verifies the quotation, not the
   argument — three drafts contradict their own citation, and several restate
   `NextAction`. Both are documented in `AI_EVALUATION` §7 and neither is yet a test.
7. **CARC 45 has no taxonomy branch.** A real denial reason with no category; it
   fails safe to `UNMAPPED` with confidence capped and human review required. In
   `docs/PROBLEM_MEMO.md` as an unfixed finding.
