# Denials Command Center — Phase 1 (ingestion + reconciliation)

An ingestion and reconciliation system for payer remittance (835) files, a
claims export, and a manual denials tracker. Everything it reports is derived
from those files by code; no figure in this repository was typed in by hand.

**Stack (fixed by the brief):** C# / .NET 8 Web API · EF Core · PostgreSQL ·
React + TypeScript (Vite, Phase 2). Python is reserved for AI evaluation only.

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

The API is then on **http://localhost:8080**, Swagger UI at
**http://localhost:8080/swagger**, and the health probe at
**http://localhost:8080/health** (no token required — a container probe has
none).

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

## Environment variables

Everything is read from the environment. There are no secrets in this
repository.

| Variable | Required | Default | What it is |
|---|---|---|---|
| `DATA_DIR` | **yes** | — | Path to the unzipped data pack. Mounted `/data:ro` in compose. |
| `ConnectionStrings__Denials` | **yes** | — | ADO.NET connection string for PostgreSQL. |
| `SEED_USERS` | **yes** | — | JSON array of `{"token","name","role"}`. Tokens ≥ 16 chars; roles `reader` or `ingest`. |
| `POSTGRES_USER` | yes | `denials` | Database role created by the `db` service. |
| `POSTGRES_PASSWORD` | **yes** | — | Database password. No code default on purpose. |
| `POSTGRES_DB` | no | `denials` | Database name. |
| `AUTOMIGRATE` | no | `true` | Apply checked-in EF migrations at startup. |
| `API_PORT` | no | `8080` | Host port for the API. |
| `ASPNETCORE_ENVIRONMENT` | no | `Production` | `Development` relaxes `SEED_USERS` to local dev defaults. |
| `LLM_PROVIDER` | no | *(empty)* | Phase 2+. Provider id; read by `ILlmClient` only. |
| `LLM_API_KEY` | no | *(empty)* | Phase 2+. Provider key; never written to source, logs or DB. |
| `LLM_MODEL` | no | *(empty)* | Phase 2+. Model id. |

`SEED_USERS` has **no default in code**, by design: a service that falls back
to "any token works" is the single most likely way for this to reach production
looking secure and behaving open. `.env.example` ships working local tokens so
`cp .env.example .env` gives you a running system immediately — replace them
before anything is shared.

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

| Method | Path | Role | Notes |
|---|---|---|---|
| `GET` | `/health` | *(none)* | Container probe: counts, checksum, no PHI. |
| `GET` | `/api/reconciliation` | `reader` | Full reconciliation as JSON. |
| `GET` | `/api/reconciliation/report.md` | `reader` | Same figures, as Markdown. |
| `GET` | `/api/exceptions?reason=&claimId=` | `reader` | Exception rows + `rows_in = matched + exceptions`. |
| `GET` | `/api/claims/{claimId}` | `reader` | One claim, its lines, and its full remittance history. |
| `GET` | `/api/audit` | `reader` | Last 500 audit entries. |
| `POST` | `/api/ingest` | `ingest` | Re-run ingestion; idempotent by full-state checksum. |

Authenticate with `Authorization: Bearer <token>`.

Authorization is enforced **server-side** on every route. A missing token and
an unknown token both return `401` with an identical body — an attacker must
not be able to tell "wrong token" from "no token" by reading the response. A
valid token with the wrong role returns `403`. Tokens are compared with a
fixed-time comparison so timing does not reveal a prefix.

---

## Repository layout

```
src/
  AQ.Denials.Core       money, canonical model, checksum, exception reasons
  AQ.Denials.Ingest     X12 835 parser, CSV/XLSX readers, pipeline, report
  AQ.Denials.Rules      denial categories, payer windows, recovery buckets
  AQ.Denials.Llm        ILlmClient + NullLlmClient (Phase 2 fills this in)
  AQ.Denials.Api        EF Core, auth, endpoints, report mode
  AQ.Denials.Tests      xUnit suite (parser, money, reconciliation, guards)
docs/                   decisions, findings, AI usage, hours
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

## What Phase 1 delivers

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

AI, UI and analytics are **not** in this phase.

---

## Documentation

| File | Contents |
|---|---|
| `docs/DECISIONS.md` | Every decision, the alternatives, why, and the trade-off. |
| `docs/DATA_FINDINGS.md` | What is actually in the pack, with the query behind each figure. |
| `docs/AI_USAGE_LOG.md` | How AI was used, and where it was wrong. |
| `docs/HOURS.md` | Time log. |
