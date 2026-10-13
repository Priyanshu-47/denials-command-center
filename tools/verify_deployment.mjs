#!/usr/bin/env node
// ---------------------------------------------------------------------
// Deployment smoke test for the Denials Command Center.
//
// Why this exists: src/AQ.Denials.Tests boots the API with AUTOMIGRATE=false
// and no database, so it can never exercise /api/worklist/* — those read
// WorkItems. The defect fixed in Program.cs (a null sourceFile on every
// claim timeline row) was therefore invisible to the unit suite and was
// only found by probing a real deployment. This script covers that gap.
//
// It mutates data: it sets a status and reassigns a claim. Run it against a
// deployment you are willing to leave with one claim marked in_progress and
// assigned to the demo specialist.
//
// Tokens come from the environment or a .env file and are never printed:
//   DENIALS_READER_TOKEN / DENIALS_INGEST_TOKEN /
//   DENIALS_SPECIALIST_TOKEN / DENIALS_MANAGER_TOKEN
//
//   node tools/verify_deployment.mjs --base http://HOST:8080
// ---------------------------------------------------------------------
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};

const BASE = opt("--base", "http://localhost:8080");
const NGINX = opt("--nginx", BASE.replace(/8080/, "5173"));
const ENV_FILE = opt("--env", resolve(process.cwd(), ".env"));

// Every anchor below is asserted by the C# suite as well; a deployment that
// disagrees with them is serving different numbers than the tests proved.
const ANCHOR = {
  checksum: "50c176cba51bb6d36813b883d1879f6a7b44ede9612da080caca7e0e428e1b02",
  claims: 1222,
  remitFiles: 4,
  rowsIn: 2644,
  exceptions: 90,
  openCount: 136,
  openAmount: 27780.0,
  pending: [58, 12600.0],
  zeroPaid: [19, 3365.0],
  buckets: { RECOVERABLE: [79, 16785.0], POLICY_BLOCKED: [26, 4460.0], EXPIRED: [31, 6535.0] },
};

let pass = 0;
const fails = [];
const check = (name, ok, detail = "") => {
  if (ok) pass++;
  else fails.push(name + (detail ? ` -> ${detail}` : ""));
  console.log(`  ${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  -> ${detail}` : ""}`);
};

function readTokens() {
  const fromEnv = (k) => process.env[k];
  const file = (() => {
    try { return readFileSync(ENV_FILE, "utf8"); } catch { return ""; }
  })();
  let seeded = {};
  const line = file.split(/\r?\n/).find((l) => l.startsWith("SEED_USERS="));
  if (line) {
    try {
      for (const u of JSON.parse(line.slice("SEED_USERS=".length))) seeded[u.role] = u.token;
    } catch { /* fall through to env vars */ }
  }
  return {
    reader: fromEnv("DENIALS_READER_TOKEN") ?? seeded.reader,
    ingest: fromEnv("DENIALS_INGEST_TOKEN") ?? seeded.ingest,
    specialist: fromEnv("DENIALS_SPECIALIST_TOKEN") ?? seeded.specialist,
    manager: fromEnv("DENIALS_MANAGER_TOKEN") ?? seeded.manager,
  };
}

const TOK = readTokens();

async function call(path, { token, method = "GET", body, base = BASE } = {}) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  try {
    const res = await fetch(base + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
    const text = await res.text();
    let json;
    try { json = JSON.parse(text); } catch { json = text; }
    return { status: res.status, json };
  } catch (e) {
    return { status: -1, json: String(e) };
  }
}

const m = (x) => Math.round(Number(x) * 100) / 100;

/* ---------------------------------------------------------------- */
console.log("\n1. AVAILABILITY");
const { status: hs, json: h } = await call("/health");
check("GET /health == 200", hs === 200, hs);
check("checksum matches the Phase 1 anchor", h?.checksum === ANCHOR.checksum, String(h?.checksum).slice(0, 16));
check("claims == 1222", h?.claims === ANCHOR.claims, h?.claims);
check("remitFiles == 4", h?.remitFiles === ANCHOR.remitFiles, h?.remitFiles);
check("rowsIn == 2644", h?.rowsIn === ANCHOR.rowsIn, h?.rowsIn);
check("exceptions == 90", h?.exceptions === ANCHOR.exceptions, h?.exceptions);
check("rowsIn == matched + exceptions",
  h?.rowsIn === (h?.matched ?? h?.rowsIn - h?.exceptions) + h?.exceptions);

const sw = await call("/swagger/index.html");
check("Swagger reachable", sw.status === 200, sw.status);
const web = await call("/", { base: NGINX });
check("web UI via nginx == 200", web.status === 200, web.status);
const nh = await call("/health", { base: NGINX });
check("nginx forwards /health to the API", nh.status === 200 && nh.json?.checksum === ANCHOR.checksum, nh.status);
const na = await call("/api/analytics", { token: TOK.reader, base: NGINX });
check("nginx forwards /api/* with a bearer token", na.status === 200, na.status);

/* ---------------------------------------------------------------- */
console.log("\n2. AUTHENTICATION AND ROLE BOUNDARIES");
check("no token -> 401", (await call("/api/reconciliation")).status === 401);
check("bogus token -> 401", (await call("/api/reconciliation", { token: "not-a-token-000000000000" })).status === 401);
check("reader -> /api/worklist -> 403", (await call("/api/worklist", { token: TOK.reader })).status === 403);
check("reader -> POST /api/ingest -> 403", (await call("/api/ingest", { token: TOK.reader, method: "POST", body: {} })).status === 403);
const wlMgr = await call("/api/worklist", { token: TOK.manager });
check("manager -> /api/worklist -> 200", wlMgr.status === 200, wlMgr.status);
check("manager sees all 136 open denials", wlMgr.json?.total === ANCHOR.openCount, wlMgr.json?.total);
const wlSpec = await call("/api/worklist", { token: TOK.specialist });
check("specialist -> /api/worklist -> 200", wlSpec.status === 200, wlSpec.status);
check("specialist queue is not larger than the manager's",
  (wlSpec.json?.total ?? 0) <= (wlMgr.json?.total ?? 0),
  `${wlSpec.json?.total} <= ${wlMgr.json?.total}`);

/* ---------------------------------------------------------------- */
console.log("\n3. ANALYTICS: the three money figures stay separate");
const { json: an } = await call("/api/analytics", { token: TOK.reader });
const money = an?.moneyAtRisk ?? {};
check("open denials == 136 / $27,780.00",
  money.openCount === ANCHOR.openCount && m(money.openAmount) === ANCHOR.openAmount,
  `${money.openCount} / ${m(money.openAmount)}`);
const bk = Object.fromEntries((money.buckets ?? []).map((b) => [b.bucket, [b.count, m(b.amount)]]));
for (const [name, [c, a]] of Object.entries(ANCHOR.buckets)) {
  check(`bucket ${name} == ${c} / $${a}`,
    bk[name]?.[0] === c && bk[name]?.[1] === a, JSON.stringify(bk[name]));
}
check("buckets sum to the open book",
  Object.values(bk).reduce((s, [c]) => s + c, 0) === ANCHOR.openCount &&
  m(Object.values(bk).reduce((s, [, a]) => s + a, 0)) === ANCHOR.openAmount);
check("never-adjudicated == 58 / $12,600.00 (kept separate)",
  money.pendingCount === ANCHOR.pending[0] && m(money.pendingAmount) === ANCHOR.pending[1],
  `${money.pendingCount} / ${m(money.pendingAmount)}`);
check("zero-paid lines == 19 / $3,365.00 (kept separate)",
  money.zeroPaidLineCount === ANCHOR.zeroPaid[0] && m(money.zeroPaidLineAmount) === ANCHOR.zeroPaid[1],
  `${money.zeroPaidLineCount} / ${m(money.zeroPaidLineAmount)}`);
check("the three figures are not summed into one number",
  m(money.openAmount) !== m(money.pendingAmount));
for (const dim of ["byPayer", "byCategory", "byProvider", "byCoder", "byFacility"]) {
  const rows = an?.[dim];
  if (!Array.isArray(rows)) { check(`${dim} present`, false, "missing"); continue; }
  const c = rows.reduce((s, r) => s + r.count, 0);
  const a = m(rows.reduce((s, r) => s + r.amount, 0));
  check(`${dim} partitions 136 / $27,780.00`, c === ANCHOR.openCount && a === ANCHOR.openAmount, `${c} / ${a}`);
}

/* ---------------------------------------------------------------- */
console.log("\n4. PREVENTION + machine-readable rules");
const { json: pv } = await call("/api/prevention", { token: TOK.reader });
const checks = pv?.checks ?? [];
check("five prevention checks", checks.length === 5, checks.length);
check("every check carries a machine-readable rule",
  checks.every((c) => c.rule && typeof c.rule === "object"));
check("every check reports benefit AND burden",
  checks.every((c) => "denialsCaught" in c && "claimsFlagged" in c && "catchRate" in c && "burdenRate" in c));
const nonMeasurable = checks.filter((c) => c.measurable === false);
check("at least one check honestly reports measurable=false", nonMeasurable.length >= 1, nonMeasurable.length);
if (nonMeasurable[0]) {
  check("non-measurable check is confident about being unmeasurable",
    nonMeasurable[0].confidence === "not_measurable", nonMeasurable[0].confidence);
  check("non-measurable check reports 0 denials caught", nonMeasurable[0].denialsCaught === 0, nonMeasurable[0].denialsCaught);
  check("non-measurable check explains whyNotMeasured", Boolean(nonMeasurable[0].whyNotMeasured));
  check("non-measurable check exports cannot_evaluate_without_data",
    JSON.stringify(nonMeasurable[0].rule).includes("cannot_evaluate_without_data"));
}
const DECLARED = new Set(["observed", "association", "not_measurable"]);
check("confidence values come from the declared set",
  checks.every((c) => DECLARED.has(c.confidence)),
  JSON.stringify(checks.map((c) => c.confidence)));

/* ---------------------------------------------------------------- */
console.log("\n5. WORKLIST: timeline, audit, reassignment");
const rows = wlSpec.json?.rows ?? [];
check("queue is non-empty", rows.length > 0, rows.length);
check("every row explains its priority", rows.every((r) => r.priorityExplain));
const pri = rows.map((r) => r.priority);
check("queue is ordered by priority", pri.every((v, i) => i === 0 || pri[i - 1] >= v), pri.slice(0, 5).join(","));
const top = rows[0];
console.log(`      top: ${top.claimId} priority=${top.priority} conf=${top.confidence} bucket=${top.bucket}`);

const detail = await call(`/api/worklist/${top.claimId}`, { token: TOK.specialist });
check("claim detail == 200", detail.status === 200, detail.status);
const claim = detail.json?.claim ?? {};
const hist = claim.history ?? [];
check("claim projection carries service lines", (claim.lines ?? []).length > 0, (claim.lines ?? []).length);
check("claim projection carries remittance history", hist.length > 0, hist.length);

// THE regression this script exists for.
const orphan = hist.filter((r) => !r.sourceFile || !r.sourceFile.endsWith(".835"));
check("every timeline row names its source .835 (null here = the sourceFile bug)",
  orphan.length === 0, orphan.length ? `${orphan.length}/${hist.length} rows null` : hist.length);
console.log(`      source files: ${[...new Set(hist.map((r) => r.sourceFile))].join(", ") || "(none)"}`);

const claimEndpoint = await call(`/api/claims/${top.claimId}`, { token: TOK.reader });
const hist2 = claimEndpoint.json?.history ?? [];
check("/api/claims and /api/worklist name the same source file",
  hist.length > 0 && hist2.length > 0 && hist[0].sourceFile === hist2[0].sourceFile,
  `${hist[0]?.sourceFile} vs ${hist2[0]?.sourceFile}`);

const claimId = top.claimId;
const target = (detail.json?.row?.status === "in_progress") ? "awaiting_payer" : "in_progress";
const st = await call(`/api/worklist/${claimId}/status`, {
  token: TOK.specialist, method: "POST",
  body: { status: target, note: "Deployment verification run." },
});
check("specialist can set a declared status", st.status === 200, st.status);
check("status change reported as changed", st.json?.changed === true, st.json?.changed);
const bad = await call(`/api/worklist/${claimId}/status`, {
  token: TOK.specialist, method: "POST", body: { status: "not-a-status" },
});
check("undeclared status -> 400 with the allowed list", bad.status === 400 && !!bad.json?.allowed);

const reloaded = await call(`/api/worklist/${claimId}`, { token: TOK.specialist });
check("status persisted after reload", reloaded.json?.row?.status === target, reloaded.json?.row?.status);
const events = reloaded.json?.events ?? [];
check("every action taken is recorded with before/after",
  events.length > 0 && events.every((e) => "before" in e && "after" in e && e.actor && e.at),
  events.length);
const statusEvent = events.filter((e) => e.field === "status").pop();
if (statusEvent) check("status event shows a real before -> after",
  statusEvent.before !== statusEvent.after, `${statusEvent.before} -> ${statusEvent.after}`);

const audit = await call("/api/audit", { token: TOK.reader });
const entries = audit.json?.entries ?? [];
check("audit log has entries", entries.length > 0, entries.length);
const wlEntry = entries.find((e) => String(e.action).startsWith("worklist."));
check("audit carries before -> after in Detail", !!wlEntry && String(wlEntry.detail).includes(" -> "),
  wlEntry ? wlEntry.detail : "no worklist entry");
check("audit names the actor", !!wlEntry?.actor, wlEntry?.actor);

check("specialist cannot reassign (manager only)",
  (await call(`/api/worklist/${claimId}/assign`, {
    token: TOK.specialist, method: "POST", body: { assignee: "someone-else" },
  })).status === 403);
const asg = await call(`/api/worklist/${claimId}/assign`, {
  token: TOK.manager, method: "POST", body: { assignee: "Dana (specialist)" },
});
check("manager can reassign", asg.status === 200, asg.status);
const after2 = await call(`/api/worklist/${claimId}`, { token: TOK.manager });
check("reassignment persisted after reload",
  after2.json?.row?.assignee === "Dana (specialist)", after2.json?.row?.assignee);
check("unknown claim id -> 404, not 500",
  (await call("/api/worklist/DOES-NOT-EXIST", { token: TOK.specialist })).status === 404);

/* ---------------------------------------------------------------- */
console.log("\n6. AI DEGRADED OPERATION (no provider configured)");
const dr = await call(`/api/worklist/${claimId}/draft`, { token: TOK.specialist, method: "POST", body: {} });
const body = JSON.stringify(dr.json ?? {});
check("draft endpoint responds without crashing", dr.status === 200, dr.status);
check("missing AI reports an honest reason instead of fabricating a citation",
  dr.json?.produced === false && dr.json?.verdict === "NotAssessed",
  String(dr.json?.reason ?? "").slice(0, 90));
check("no fabricated policy citation in the degraded reply",
  !/"[A-Z]{2,}_POLICY|§\s?\d/.test(body) || dr.json?.produced === true);
const wlAfter = await call("/api/worklist", { token: TOK.specialist });
check("worklist fully usable without AI", wlAfter.status === 200);
check("low-confidence rows routed to human review",
  (wlAfter.json?.rows ?? []).filter((r) => r.requiresHumanReview).length > 0);

/* ---------------------------------------------------------------- */
console.log("\n" + "=".repeat(64));
console.log(`  passed: ${pass}`);
console.log(`  failed: ${fails.length}`);
if (fails.length) {
  console.log("  FAILURES:");
  for (const f of fails) console.log("   - " + f);
}
console.log("=".repeat(64));
process.exit(fails.length ? 1 : 0);
