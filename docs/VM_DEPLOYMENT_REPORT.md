# VM Deployment Report — Denials Command Center

**Date:** 13 October 2026
**Host:** Azure VM `JainVM` (Ubuntu 24.04.4 LTS, 4 vCPU, 16 GB RAM, 123 GB disk), West US2
**Public IP:** 20.98.93.123
**Deployed commit:** `c935411f91576764bd7391381fd911e4e6313af1` (`master`)
**Previous commit:** `c2bc466` (the delivered application) — one follow-up commit was added, see §6.

No credential, token, password or connection string appears in this document.

---

## 1. Preflight — what was inspected before changing anything

| # | Question | Finding | Action |
|---|---|---|---|
| 1 | VM reachable, enough CPU/RAM/disk? | Ubuntu 24.04.4, 4 vCPU, 15,983 MB RAM (12.5 GB free at start), 60 GB free disk | Sufficient — no resize |
| 2 | OS, disk, Docker/Compose, ports in use? | **Docker absent.** Ports 22, 11434 (ollama), 8000 (uvicorn) taken; **5173, 8080, 5432 free** | Installed Docker Engine + Compose v2 |
| 3 | Repository present, local changes? | **Not present.** `~/jainLLM` is a *different* project with uncommitted changes | Left completely untouched; cloned fresh |
| 4 | Data pack present? | **Not present** — the pack is gitignored, so a clone alone cannot run the app | Transferred 18 files (752 KB) by scp |
| 5 | Can it run within the VM's limits? | Yes; API image built in 49 s, web in 27 s | No optimisation needed |
| 6 | Effective inbound network rules? | NSG `JainVM-nsg` had **one** inbound rule: SSH/22 from `*`. Azure's default `DenyAllInBound` (65500) blocked 5173 and 8080 | Added two `/32`-restricted rules |

Pre-existing workloads deliberately preserved: `~/jainLLM` (uncommitted work), `uvicorn` on
`127.0.0.1:8000`, `ollama` on `127.0.0.1:11434`. Nothing was pruned, resized or rebooted.

---

## 2. What was built and how

| Step | Result |
|---|---|
| Docker Engine | 29.9.0 (`docker-ce` from the official Docker apt repo) |
| Compose | v5.6.0 (plugin) |
| Daemon | `systemctl is-enabled docker` → `enabled`; `is-active` → `active` |
| Privilege model | `azureuser` is **not** in the `docker` group; every command uses `sudo` (NOPASSWD). One explicit escalation instead of permanent root-equivalent group membership |
| `db` image pull | `postgres:16-alpine` |
| `api` build | **49 s** first build (publish step 11.4 s); **37 s** rebuild after the fix |
| `web` build | **27 s** |
| Layer reuse | Rebuilds touch only the service that changed; `docker compose build api` does not rebuild `web` |

Disk after build: 68 GB of 123 GB used (56 GB free).

---

## 3. Configuration

`.env` was generated **on the VM**, so no secret passed through this report, git, or a
screenshot. It contains four 48-character values (`POSTGRES_PASSWORD`, reader/ingest/
specialist/manager tokens), mode `600`, and `git check-ignore` confirms `.gitignore:11` covers it.

- `ConnectionStrings__Denials` password verified **equal** to `POSTGRES_PASSWORD` (checked by
  comparison only — neither value was printed).
- `SEED_USERS` contains all four roles. The API refuses to start outside Development without a
  specialist *and* a manager, so a missing role would have failed at startup rather than at the
  first click.
- `DATA_DIR=/home/azureuser/denials-data/AQSoft_Assignment_Data_Pack_1`, `chmod -R a-w`
  (`dr-x---r-x`) — the originals cannot be rewritten by anything inside a container.
- `LLM_PROVIDER` is **empty by design** (see §7).
- Source control: the repository is cloned over SSH with a **read-only GitHub deploy key**
  (`JainVM deployment (read-only)`), so the VM can `fetch`/`merge --ff-only` but can never push.

---

## 4. Verified results

Everything below was executed and observed. Nothing is inferred.

### 4.1 Availability

| Check | Result |
|---|---|
| `docker compose ps` | 3/3 — `api` healthy, `db` healthy, `web` healthy |
| `GET /health` (host) | `200` — `claims 1222, remitFiles 4, rowsIn 2644, exceptions 90` |
| Checksum | `50c176cba51bb6d36813b883d1879f6a7b44ede9612da080caca7e0e428e1b02` — **identical to the Phase 1 anchor computed on the development machine** |
| Web UI `http://20.98.93.123:5173/` | `200`, 407 bytes, correct app shell |
| Swagger `…/8080/swagger/index.html` | `200` |
| nginx → API | `/health` and `/api/*` proxied correctly **with** the bearer token |
| Browser refresh | Session restored from `localStorage`, queue re-rendered — no breakage |

### 4.2 Ingestion idempotency

| Run | HTTP | Elapsed | `unchanged` | rowsIn / matched / exceptions | cash excl. duplicate |
|---|---|---|---|---|---|
| 1 | 200 | 3.8 s | `false` | 2644 / 2554 / 90 | 118,220.36 |
| 2 | 200 | **0.1 s** | **`true`** | identical | identical |

`2554 + 90 = 2644` and the checksum is unchanged across runs — re-ingesting cannot double-count.

### 4.3 Automated deployment smoke test

`node tools/verify_deployment.mjs --base http://20.98.93.123:8080 --nginx http://20.98.93.123:5173`

**67 / 67 passed, exit 0.** Highlights:

- Auth: no token → 401; bogus token → 401; reader → `/api/worklist` 403; reader → `POST /api/ingest` 403;
  specialist and manager → 200; manager sees all 136.
- Money: open `136 / $27,780.00`; buckets `79/$16,785` · `26/$4,460` · `31/$6,535` sum to the open book;
  never-adjudicated `58 / $12,600.00` and zero-paid `19 / $3,365.00` remain **separate**;
  `byPayer`/`byCategory`/`byProvider`/`byCoder`/`byFacility` each partition exactly `136 / $27,780.00`.
- Prevention: 5 checks, all with machine-readable rules and both benefit *and* burden;
  one honestly reports `measurable=false, confidence=not_measurable, denialsCaught=0` and exports
  `cannot_evaluate_without_data`.
- Workflow: status change persisted after reload; undeclared status → 400 with the allowed list;
  events carry `before`/`after`/`actor`/`at`; audit `Detail` carries `before -> after`;
  specialist → reassign **403**; manager → reassign **200** and persisted; unknown claim → **404**.
- Timeline: every history row names a real `.835`, and `/api/claims` and `/api/worklist` name the
  **same** file.

### 4.4 Unit / integration suite

See §5 for the on-VM run. On the development machine the suite passed **247 / 247** at `c935411`
(243 before this deployment's fix, +4 new regression tests).

### 4.5 Browser walkthrough (real Chromium, from the operator machine)

| Step | Observed |
|---|---|
| Sign in as specialist | Nav shows `Dana (specialist)` · badge `SPECIALIST`; `136` open denials, `$27,780`, `69` below confidence 70 |
| Queue | 136 `<tr>` rows, each with a priority explanation as its row label (e.g. `recoverability +400, deadline +100, amount +30, needs review +75, preventable +25`) |
| Claim detail | Next action, preventable = "yes", appeal window `ends 2026-10-11 (11 days left)`, `Priority 630 — …` factor breakdown, `CONFIDENCE — 60 / 100`, `baseline 50 — …` |
| Timeline | `Denied — $0.00 paid on 2026-04-14` → **`era_2026Q1.835 · submitted $205.00 · CO 11 $205.00 (line)`** |
| Audit in UI | `Priya (manager) changed assignee (empty) → Dana (specialist)`; `Dana (specialist) changed status open → in_progress` (+ note); `… in_progress → awaiting_payer` (+ note) |
| Money at risk | Four headline figures and the three mutually-exclusive buckets rendered with their "not summed" caveat; pre-bill table labelled **"An association, not a cause"** |
| Prevention | `5` checks / `4 measurable`, `1,222` claims replayed, per-check benefit **and** burden, the unmeasurable check headed **`NOT MEASURABLE`** with its reason, plus an inline `rules.json` export |
| Sign out → manager | Nav shows `Priya (manager)` · badge `MANAGER`; manager-only `Draft all (AI)` and `Reassign (manager)` controls present |
| Manager reassign | Clicked `assign to me` → audit gained `Priya (manager) changed assignee Dana (specialist) → Priya (manager)` |
| Refresh | Re-authenticated silently from `localStorage`, queue intact |

Rehearsal state was reset afterwards: `GPP-2026-001141` → `Status='open'`, `Assignee=NULL`.
The 4 `WorkItemEvents` rows were **kept** — deleting audit history to make a demo prettier is not
something this deployment will do.

---

## 5. Tests run on the VM

The suite requires the .NET SDK, which the VM did not have. It was installed **user-locally** to
`~/.dotnet` (no apt repository changes, no system packages) and the suite was run with
`DATA_DIR` pointing at the read-only pack.

Command run on the VM:

```bash
export DOTNET_ROOT=/home/azureuser/.dotnet && export PATH="$DOTNET_ROOT:$PATH"
export DATA_DIR=/home/azureuser/denials-data/AQSoft_Assignment_Data_Pack_1
unset LLM_PROVIDER LLM_MODEL LLM_TIMEOUT_SECONDS
dotnet test src/AQ.Denials.Tests/AQ.Denials.Tests.csproj -c Release --nologo
```

| | |
|---|---|
| SDK | .NET SDK **8.0.425**, installed to `~/.dotnet` (no apt/system changes) |
| Result | **`Passed! - Failed: 0, Passed: 247, Skipped: 0, Total: 247`** |
| Exit code | `0` |
| Duration | 6 s test execution, 32 s including restore and build |
| Data | same read-only pack the API serves (`DATA_DIR`) |
| Disk impact | 69 GB → 69 GB (57 % used); SDK + NuGet ≈ 1 GB total |

This is the **same commit and the same machine the deployment runs on**, so the numbers the
tests assert are the numbers the live application serves.

Environment-related tests that cannot run anywhere without a live model: `LiveModelTests` skip
because `LLM_PROVIDER` is unset (by design). This is expected, not a failure.

---

## 6. Defect found and fixed during deployment

**Null `sourceFile` on the worklist claim timeline.** The claim detail page rendered
*"source unknown"* on every timeline row while `/api/claims/{id}` named the file correctly for the
same observation.

*Cause:* `IStateProvider.Current` is a computed property that re-runs the whole ingestion pipeline
on every read (a deliberate, commented decision — no cache, no second source of truth).
`/api/claims/{id}` captures `var outcome = state.Current` once; `/api/worklist/{id}` read it again
inside its deferred history projection, so the observation came from one object graph and the
remit files from another. `RemitObservation` is a plain class, so `List.Contains` is reference
equality and the match could never succeed.

*Why it survived a green test suite:* those tests boot the API with `AUTOMIGRATE=false` and **no
database**, and `/api/worklist/*` reads `WorkItems` — so the suite structurally cannot reach the
broken endpoint.

*Fix (commit `c935411`, 2 lines + reason):* capture one snapshot per request in the worklist
handler, exactly as the claims handler already does. Not a cache: one snapshot per request is not
a second source of truth. Verified fixed in the browser (§4.5) and by the smoke test (§4.3).

*Regression coverage added (+4 tests → 247):* every observation names its file within a snapshot;
a second pipeline run is a different graph and cross-graph matching fails (the mechanism, kept
executable rather than merely commented); every timeline row names a real `.835` through
`/api/claims/{id}`; the claim projection exposes the full history column set so a column dropped
from one shape and kept in the other fails loudly.

*Gap disclosed:* the worklist endpoint's `sourceFile` is still **not** covered by the DB-free unit
suite. It is covered by `tools/verify_deployment.mjs`, which must be run against a live deployment.

**Second finding (not fixed):** worklist rows open via `<tr onClick>` with no `tabindex` or
keyboard handler, and the *Reassign (manager)* buttons are absent from the accessibility tree —
found because browser automation could not obtain an element reference for them and had to fall
back to a DOM `click()`. Mouse-driven demo is unaffected; a keyboard-only user cannot open a
claim. Recorded here rather than changed, because it is a UX change outside the deployment's
remit and the mission prioritised keeping the diff minimal.

---

## 7. AI / degraded operation

`LLM_PROVIDER` is unset, so the API runs `NullLlmClient`. Everything deterministic — category,
team, preventability, next action, window, confidence, priority — is computed without a model;
only draft appeals are affected, and the endpoint answers honestly rather than inventing a
citation. Confirmed through both the smoke test and the browser (`Draft note` panel present, no
fabricated policy text anywhere in the page).

No API key was added "to make the UI look finished", and no model was installed. The VM already
runs `ollama` on `127.0.0.1:11434` for an unrelated project; it was **left untouched and not
wired into this deployment** — cold-loading a model into a container that treats an unreachable
provider as a startup failure would have traded a reliable demo for an unreliable one.

---

## 8. Network and security posture

NSG `JainVM-nsg` (RG `Jain`, West US2) after the change:

| Rule | Prio | Port | Source | Action |
|---|---|---|---|---|
| `SSH` | 300 | 22 | `*` | pre-existing (unchanged — see limitations) |
| `Demo-Web-5173` | 310 | 5173 | `103.221.74.108` | Allow |
| `Demo-Api-8080` | 320 | 8080 | `103.221.74.108` | Allow |

- Both demo ports restricted to the operator's **current public IP as a `/32`** — the smallest
  practical source range. Verified reachable from that IP and the intended access path was
  confirmed before opening anything.
- **Port 5432 is not published.** The `db` service declares no `ports:` mapping, so PostgreSQL is
  reachable only on the internal Compose network; `docker ps` confirms `5432/tcp` with no host
  binding, and no NSG rule for 5432 exists.
- The firewall was **not** disabled, no rule was opened to `*`, and SSH was left as found.
- 5173/8080 bind `0.0.0.0` on the VM host (Compose default); enforcement is the NSG. An SSH
  tunnel (`-L 5173:localhost:5173 -L 8080:localhost:8080`) is documented as the zero-inbound
  alternative.
- API endpoints other than `/health` require a 48-character bearer token; role policies are
  enforced server-side (403 verified for reader→worklist and reader→ingest, specialist→reassign).
- `.env` mode `600`, gitignored; deploy key read-only; data pack non-writable; working tree clean.

---

## 9. Resilience

| Property | Verified |
|---|---|
| Restart policy | `restart=unless-stopped` on db, api **and** web (`docker inspect`) |
| Boot | `systemctl is-enabled docker` → `enabled` |
| Data retention | `denials-command-center_pgdata` volume untouched across the redeploy — `db` kept `Up 44 minutes` while `api` was recreated |
| Health probes | `db` and `api` have compose healthchecks; `web` waits on `api` being healthy |
| Recovery after restart | API returns to `healthy` in <3 s; `/health` re-derives the same checksum |

---

## 10. Resource usage (after deployment, idle)

| Container | CPU | Memory |
|---|---|---|
| `api` | 0.01 % | 299.3 MiB (1.87 %) |
| `db` | 3.85 % | 34.0 MiB (0.21 %) |
| `web` | 0.00 % | 5.0 MiB (0.03 %) |
| **Total containers** | — | **≈ 338 MiB** |

Host: 7,873 / 15,983 MiB used (8,110 MiB available), disk 68 GB of 123 GB (56 GB free),
load average 1.73 / 2.26 / 2.12. No memory pressure, no swap used. The stack would fit several
times over — no VM resize is warranted.

---

## 11. Known limitations and incomplete checks

1. **No TLS and no custom domain.** Plain HTTP on 5173/8080, restricted to one source IP. Not
   needed for a demo; would be the first thing to add for anything real.
2. **SSH/22 remains open to `*`** (pre-existing Azure default). Tightening it to `/32` risks
   locking the operator out if their IP changes, with recovery only via the Azure portal — a
   destructive-in-effect network change, so it was left alone and is recommended for later.
3. **The demo source IP is a `/32`.** If the operator's public IP changes, the browser will stop
   loading while SSH still works. §6 of the runbook has the exact two commands to re-issue.
4. **`LLM_PROVIDER` unset** — degraded mode by intention (§7). No 3B-vs-7B comparison exists; see
   `docs/AI_EVALUATION.md` §8 for why.
5. **VM has a pending kernel update** (`System restart required`, 28 packages). Not applied and
   not rebooted — a reboot would have been disruptive to unrelated running workloads.
6. **The worklist `sourceFile` path is not unit-tested** (needs a database the suite deliberately
   does not use); covered only by the deployment smoke test (§6).
7. **Keyboard accessibility gap** in the worklist/claim-detail controls (§6, second finding).
8. **On-VM test environment:** `LiveModelTests` skip without a model; this is the documented
   behaviour, not a failure.

---

## 12. Reproducing this deployment

All commands are in `docs/DEPLOYMENT_RUNBOOK.md`. In short:

```powershell
ssh -i "D:\DL\JainVM_key.pem" azureuser@20.98.93.123
```

```bash
cd /home/azureuser/denials-command-center
sudo docker compose ps
curl -fsS http://localhost:8080/health
git rev-parse HEAD      # c935411f91576764bd7391381fd911e4e6313af1
```
