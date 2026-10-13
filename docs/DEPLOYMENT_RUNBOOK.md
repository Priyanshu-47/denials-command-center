# Deployment Runbook — Denials Command Center (Azure VM)

Target: `azureuser@20.98.93.123` (Azure VM `JainVM`, Ubuntu 24.04.4 LTS, 4 vCPU, 16 GB RAM,
123 GB disk). Everything below runs from a Windows PowerShell prompt on the operator machine.

No credential appears in this file. Tokens and the database password live only in the
remote `.env` (mode `600`) and were handed over privately.

---

## 1. Layout on the VM

| Path | Contents |
|---|---|
| `/home/azureuser/denials-command-center` | the repository (`master`, read-only deploy key) |
| `/home/azureuser/denials-data/AQSoft_Assignment_Data_Pack_1` | the data pack, **`chmod -R a-w`** |
| `/home/azureuser/denials-command-center/.env` | secrets, mode `600`, gitignored |
| `denials-command-center_pgdata` | PostgreSQL volume — **survives every redeploy** |
| `/home/azureuser/.ssh/denials_deploy` | ed25519 key registered on GitHub as a **read-only** deploy key |

Docker Engine **29.9.0** + Compose **v5.6.0**. `azureuser` is deliberately **not** in the
`docker` group; every command uses `sudo` (NOPASSWD). That is one explicit, auditable
escalation instead of persistent root-equivalent group membership.

---

## 2. Connect

```powershell
ssh -i "D:\DL\JainVM_key.pem" azureuser@20.98.93.123
```

Do not copy the key to the VM, do not commit it, do not paste its contents anywhere.

---

## 3. Start / stop / status / logs

```bash
cd /home/azureuser/denials-command-center

sudo docker compose ps                 # status + published ports
sudo docker compose up -d              # start (idempotent; honours depends_on health)
sudo docker compose stop               # stop, KEEP the database volume
sudo docker compose restart api        # restart one service
sudo docker compose logs -f --tail=200 api
sudo docker compose logs --tail=200 db
sudo docker compose logs --tail=200 web
```

**Never run `docker compose down -v` or `docker system prune --volumes`** — the `-v` deletes
the PostgreSQL volume holding all worklist state and audit history.

Services carry `restart: unless-stopped` and the `docker` unit is `enabled`, so the stack comes
back after a reboot or a container crash without human intervention.

---

## 4. Health checks

```bash
curl -fsS http://localhost:8080/health | jq .
sudo docker compose ps            # expect api/db "healthy", web "running"
```

`/health` is unauthenticated by design — a container probe carries no token. It must return:

```json
{"status":"ok","dataPack":"/data","claims":1222,"remitFiles":4,
 "rowsIn":2644,"exceptions":90,"checksum":"50c176cb…1b02"}
```

The checksum is the whole point: if it differs from `50c176cba51bb6d36813b883d1879f6a7b44ede9612da080caca7e0e428e1b02`
the deployment is not reading the data pack you think it is.

Full smoke test from the operator machine (covers auth, money partitions, prevention, the
claim timeline and audit — see §8 for tokens):

```powershell
node tools/verify_deployment.mjs --base http://20.98.93.123:8080 --nginx http://20.98.93.123:5173
```

---

## 5. Update / redeploy

```bash
cd /home/azureuser/denials-command-center
export GIT_SSH_COMMAND="ssh -i /home/azureuser/.ssh/denials_deploy -o IdentitiesOnly=yes"

git fetch origin
git status -sb                    # must show no local changes before merging
git merge --ff-only origin/master # refuses to clobber uncommitted work

sudo docker compose build api      # only the service that changed
sudo docker compose build web      # only if web/ changed
sudo docker compose up -d
curl -fsS http://localhost:8080/health
```

The `.env` is untracked, so a pull can never overwrite it. Record the SHA you deployed:

```bash
git rev-parse HEAD
```

### Rollback

```bash
git log --oneline -10
git checkout <known-good-sha>
sudo docker compose build api && sudo docker compose up -d
curl -fsS http://localhost:8080/health
```

Return to the branch tip with `git checkout master`. Migrations are additive
(`InitialCreate`, `AddWorklist`), so rolling the *code* back to a commit that predates a
migration leaves the newer tables in place but unused — harmless. The database volume is
never touched by a rollback.

If a build ever needs to start over, build **one service at a time**; the API image is the
only heavy one (≈50 s warm, ~4 GB during `dotnet restore`).

---

## 6. Network and access

Azure NSG `JainVM-nsg` (resource group `Jain`, West US2):

| Rule | Prio | Port | Source | Purpose |
|---|---|---|---|---|
| `SSH` | 300 | 22 | `*` | pre-existing Azure default |
| `Demo-Web-5173` | 310 | 5173 | `103.221.74.108` | web console |
| `Demo-Api-8080` | 320 | 8080 | `103.221.74.108` | API + Swagger |

Everything else hits Azure's default `DenyAllInBound` (65500).

**Port 5432 is not published at all.** The `db` service in `docker-compose.yml` declares no
`ports:`, so PostgreSQL is reachable only on the internal Compose network. No NSG rule for
5432 exists or should be created.

If your public IP changes (home/office networks do), the demo will stop loading — that is the
NSG working, not an outage. Re-issue the two rules with your current IP:

```powershell
$ip = (Invoke-RestMethod ifconfig.me)
az network nsg rule create -g Jain --nsg-name JainVM-nsg --name Demo-Web-5173 --priority 310 `
  --direction Inbound --access Allow --protocol Tcp --destination-port-ranges 5173 --source-address-prefixes $ip
az network nsg rule create -g Jain --nsg-name JainVM-nsg --name Demo-Api-8080 --priority 320 `
  --direction Inbound --access Allow --protocol Tcp --destination-port-ranges 8080 --source-address-prefixes $ip
```

`az` is already authenticated on the VM (`az account show`).

### Alternative: SSH tunnel (no inbound port at all)

If you would rather open nothing inbound, delete both `Demo-*` rules and tunnel instead:

```powershell
ssh -i "D:\DL\JainVM_key.pem" -L 5173:localhost:5173 -L 8080:localhost:8080 azureuser@20.98.93.123
```

Then open `http://localhost:5173` and `http://localhost:8080/swagger` locally.

---

## 7. AI / degraded mode

`LLM_PROVIDER` is **empty on purpose**. The API starts, the whole deterministic analysis
(category, team, preventability, next action, window, confidence, priority) is computed
without a model, and only *draft appeals* are affected: the endpoint answers
`produced: false, verdict: "NotAssessed"` with the reason.

Do not set `LLM_API_KEY` "so the UI looks finished" — a fabricated policy citation in an
interview is far worse than an honest empty draft. If a provider is ever configured,
`LLM_PROVIDER` set-but-unreachable **fails at startup** by design, and `LLM_TIMEOUT_SECONDS`
(default 60) must exceed the model's cold-load time.

---

## 8. Credentials

Four seeded identities (reader, ingest, specialist, manager) are defined in the remote
`.env` under `SEED_USERS`. Tokens are 48 characters and were provided privately with the
deployment report; they are deliberately not written here or in git.

```bash
# on the VM — names only, never prints the values
grep -o '"role":"[a-z]*"' /home/azureuser/denials-command-center/.env
```

---

## 9. Troubleshooting

| Symptom | Likely cause | Do |
|---|---|---|
| Browser can't connect, SSH works | your public IP changed | §6, re-issue the two NSG rules |
| `api` restarts immediately | data pack missing/unreadable, or `SEED_USERS` missing a worklist role | `sudo docker compose logs --tail=100 api` — the API exits listing **every** missing file |
| `api` unhealthy, `db` healthy | migration or connection-string problem | check `ConnectionStrings__Denials` password equals `POSTGRES_PASSWORD` |
| `/health` checksum differs | wrong `DATA_DIR` or an incomplete pack | `ls -l $DATA_DIR` and compare with the 18 expected files |
| Web loads, API calls 401/403 | wrong or truncated token; or role does not match the policy | tokens are 48 chars; reader cannot use `/api/worklist` |
| Drafts empty | `LLM_PROVIDER` unset | expected — §7, not an outage |
| `permission denied … docker.sock` | running `docker` without `sudo` | use `sudo docker …` (§1) |
| Build runs out of memory | building everything at once | build `api` alone, then `web` |
| Claim timeline says "source unknown" | the `sourceFile` defect, fixed in `c935411` | `git log --oneline -1` must be ≥ `c935411`; re-run §4 |

---

## 10. Before the demo

```bash
cd /home/azureuser/denials-command-center
sudo docker compose ps                       # 3 services, api/db healthy
curl -fsS http://localhost:8080/health       # checksum matches
git rev-parse HEAD                           # note the SHA
```

```powershell
node tools/verify_deployment.mjs --base http://20.98.93.123:8080 --nginx http://20.98.93.123:5173
```

Reset any claim you exercised during rehearsal back to `open` so the queue starts clean:

```bash
sudo docker compose exec db psql -U denials -d denials \
  -c "update \"WorkItems\" set Status='open';"
```

Then walk `docs/DEMO_SCRIPT.md` — specialist queue → claim timeline → status/note →
manager reassignment → analytics buckets → prevention → JSON export.
