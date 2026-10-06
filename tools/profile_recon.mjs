// Phase 0 pass 6: exact reconciliation arithmetic
import fs from 'node:fs';
import path from 'node:path';
const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
function readCsv(file) {
  const text = fs.readFileSync(path.join(ROOT, file), 'utf8').replace(/^\uFEFF/, '');
  const rows = []; let i = 0, f = '', row = [], q = false;
  while (i < text.length) { const c = text[i];
    if (q) { if (c === '"') { if (text[i + 1] === '"') { f += '"'; i++; } else q = false; } else f += c; }
    else if (c === '"') q = true;
    else if (c === ',') { row.push(f); f = ''; }
    else if (c === '\n') { row.push(f); rows.push(row); row = []; f = ''; }
    else if (c !== '\r') f += c; i++; }
  if (f !== '' || row.length) { row.push(f); rows.push(row); }
  const h = rows[0] || [];
  return rows.slice(1).filter(r => r.some(v => v !== '')).map(r => Object.fromEntries(h.map((k, j) => [k, r[j] ?? ''])));
}
const c2 = s => { if (s === null || s === undefined) return null;
  const t = String(s).trim().replace(/[$,\s]/g, ''); if (t === '') return null;
  if (/^\(.*\)$/.test(t)) return -Math.round(Number(t.slice(1, -1)) * 100);
  const n = Number(t); return Number.isFinite(n) ? Math.round(n * 100) : NaN; };
const usd = c => c === null || c === undefined || Number.isNaN(c) ? null : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
const tally = (a, fn) => { const m = {}; for (const x of a) { const k = fn(x); m[k] = (m[k] || 0) + 1; } return m; };
function canonId(raw) { const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i); if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s; return null; }
function parse835(file) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').replace(/\r\n?/g, '\n');
  const tx = []; let t = null, cl = null, sv = null;
  for (const seg of raw.split('~').map(s => s.replace(/\n/g, '')).filter(Boolean)) {
    const p = seg.split('*'); const tag = p[0];
    if (tag === 'ISA' || tag === 'GS') { t = { claims: [] }; continue; }
    if (tag === 'GE') { t = null; continue; }
    if (tag === 'ST') { if (!t) t = { claims: [] }; t.st = p; t.claims = []; t.hdtm = []; cl = null; sv = null; continue; }
    if (tag === 'SE') { if (t && t.st) tx.push(t); t = null; cl = null; sv = null; continue; }
    if (!t) continue;
    switch (tag) {
      case 'BPR': t.bpr = p; break;
      case 'N1': if (p[1] === 'PR') t.payer = p[2]; break;
      case 'REF': if (p[1] === '2U') t.payerId = p[2]; break;
      case 'DTM': if (!cl) t.hdtm.push(p); break;
      case 'CLP': cl = { num: p[1], status: p[2], charged: p[3], paid: p[4], svc: [], casC: [], lqC: [] }; t.claims.push(cl); sv = null; break;
      case 'SVC': sv = { code: p[1], charged: p[2], paid: p[3], cas: [], lq: [] }; cl.svc.push(sv); break;
      case 'CAS': { const g = p[1]; for (let k = 2; k + 1 < p.length; k += 2) (sv ? sv.cas : cl.casC).push({ g, carc: p[k], amt: p[k + 1] }); break; }
      case 'LQ': (sv ? sv.lq : cl.lqC).push(p); break;
      default: break;
    }
  }
  return { file, tx };
}
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const parsed = files.map(parse835);
const out = {};

/* per-file BPR vs CLP04 vs CLP03 */
out.perFile = parsed.map(p => {
  let bpr = 0, paid = 0, charged = 0, adj = 0;
  for (const t of p.tx) {
    if (t.bpr) { const v = c2(t.bpr[2]); if (!Number.isNaN(v)) bpr += v || 0; }
    for (const cl of t.claims) {
      const pa = c2(cl.paid), ch = c2(cl.charged);
      paid += Number.isNaN(pa) ? 0 : (pa || 0);
      charged += Number.isNaN(ch) ? 0 : (ch || 0);
      for (const e of [...cl.casC, ...cl.svc.flatMap(s => s.cas)]) { const v = c2(e.amt); if (!Number.isNaN(v)) adj += v; }
    }
  }
  const txns = p.tx.map(t => ({ payer: t.payerId, check: (t.hdtm || []).find(d => d[1] === '405')?.[2],
    bpr: usd(c2(t.bpr?.[2])), claims: t.claims.length }));
  return { file: p.file, transactions: p.tx.length, claims: p.tx.reduce((a, t) => a + t.claims.length, 0),
    bpr: usd(bpr), clp04Sum: usd(paid), clp03Sum: usd(charged), adjustmentsAbs: usd(adj),
    bprMinusPaid: usd(bpr - paid), chargeMinusPaidMinusAdj: usd(charged - paid - adj), txns };
});

/* claim-export side: latest-observation dedup */
const obs = [];
for (const p of parsed) for (const ti of p.tx.keys()) { const t = p.tx[ti];
  const check = (t.hdtm || []).find(d => d[1] === '405')?.[2] || '00000000';
  for (const cl of t.claims) obs.push({ file: p.file, fi: files.indexOf(p.file), ti, check, payerId: t.payerId,
    claim: canonId(cl.num), raw: cl.num, status: cl.status, charged: c2(cl.charged), paid: c2(cl.paid) }); }
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti));
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canonId(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }
const chargeOf = k => (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0);

const never = [...byCanon.keys()].filter(k => !hist.has(k));
const adjudicated = [...byCanon.keys()].filter(k => hist.has(k));
out.claimSide = {
  claimExportTotal: usd([...byCanon.keys()].reduce((a, k) => a + chargeOf(k), 0)),
  adjudicatedClaimCount: adjudicated.length,
  adjudicatedChargedPerExport: usd(adjudicated.reduce((a, k) => a + chargeOf(k), 0)),
  neverAdjudicatedCount: never.length,
  neverAdjudicatedCharged: usd(never.reduce((a, k) => a + chargeOf(k), 0)),
  latestObsCharged: usd([...latest.values()].reduce((a, o) => a + (o.charged || 0), 0)),
  latestObsPaid: usd([...latest.values()].reduce((a, o) => a + (o.paid || 0), 0)),
  exportVsLatestChargedDiff: usd([...byCanon.keys()].reduce((a, k) => a + chargeOf(k), 0)
    - [...latest.values()].reduce((a, o) => a + (o.charged || 0), 0)
    - never.reduce((a, k) => a + chargeOf(k), 0)),
};

/* payer-level BPR vs CLP04 (excl. duplicate resent file) */
const bprByPayer = {}, paidByPayer = {}, chargedByPayer = {};
for (const p of parsed) { if (p.file === 'era_2026Q2_resent_0719.835') continue;
  for (const t of p.tx) {
    const pay = t.payerId || 'UNKNOWN';
    if (t.bpr) { const v = c2(t.bpr[2]); bprByPayer[pay] = (bprByPayer[pay] || 0) + (Number.isNaN(v) ? 0 : (v || 0)); }
    for (const cl of t.claims) {
      const pa = c2(cl.paid), ch = c2(cl.charged);
      paidByPayer[pay] = (paidByPayer[pay] || 0) + (Number.isNaN(pa) ? 0 : (pa || 0));
      chargedByPayer[pay] = (chargedByPayer[pay] || 0) + (Number.isNaN(ch) ? 0 : (ch || 0));
    } } }
out.payerRecon = Object.keys({ ...bprByPayer, ...paidByPayer }).sort().map(p => ({
  payer: p, bprTotal: usd(bprByPayer[p] || 0), clp04Sum: usd(paidByPayer[p] || 0),
  clp03Sum: usd(chargedByPayer[p] || 0), diff: usd((bprByPayer[p] || 0) - (paidByPayer[p] || 0)) }));
out.payerReconTotals = { bpr: usd(Object.values(bprByPayer).reduce((a, b) => a + b, 0)),
  clp04: usd(Object.values(paidByPayer).reduce((a, b) => a + b, 0)),
  clp03: usd(Object.values(chargedByPayer).reduce((a, b) => a + b, 0)) };

/* group code adjustment totals excluding duplicate resent */
let co = 0, pr = 0;
for (const p of parsed) { if (p.file === 'era_2026Q2_resent_0719.835') continue;
  for (const t of p.tx) for (const cl of t.claims)
    for (const e of [...cl.casC, ...cl.svc.flatMap(s => s.cas)]) { const v = c2(e.amt) || 0;
      if (e.g === 'CO') co += v; else if (e.g === 'PR') pr += v; } }
out.adjustmentsExclResent = { CO: usd(co), PR: usd(pr), total: usd(co + pr),
  identityCheck_clp03MinusClp04: usd(out.payerReconTotals.clp03 ? 0 : 0) };
out.adjustmentsIdentity = usd(out.payerRecon.reduce((a, r) => a + (c2(r.clp03Sum) || 0), 0)
  - out.payerRecon.reduce((a, r) => a + (c2(r.clp04Sum) || 0), 0));

/* identity: does charge = paid + adjustments hold on the latest observation set? */
let ok = 0, bad = 0;
for (const [k, o] of latest) { const ch = chargeOf(k);
  if (ch === (o.charged || 0)) ok++; else bad++; }
out.exportChargeEqualsRemitCharge = { ok, bad };

/* re-adjudication & duplicate-payment analysis (excluding the duplicate resent file) */
(() => {
  const real = obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835');
  const byClaim = new Map();
  for (const o of real) { if (!o.claim) continue; const a = byClaim.get(o.claim) || []; a.push(o); byClaim.set(o.claim, a); }
  for (const a of byClaim.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti) || 0);
  const pattern = tally([...byClaim.values()], a => a.map(o => o.status).join('>'));
  const multi = [...byClaim.entries()].filter(([, a]) => a.length > 1);
  const filesOf = a => [...new Set(a.map(o => o.file))];
  const paidTwice = multi.filter(([, a]) => filesOf(a).length > 1 && a.filter(o => o.status === '1').length > 1);
  out.readjudication = {
    claimsObserved: byClaim.size,
    observationCountPattern: pattern,
    claimsWithMultipleObservations_exclResent: multi.length,
    claimsSeenInMultipleDistinctFiles: multi.filter(([, a]) => filesOf(a).length > 1).length,
    claimsPaidInMoreThanOneDistinctFile: paidTwice.length,
    paidTwiceGrossPaid: usd(paidTwice.reduce((s, [, a]) => s + a.filter(o => o.status === '1').reduce((x, o) => x + (o.paid || 0), 0), 0)),
    paidTwiceNetIfLatestWins: usd(paidTwice.reduce((s, [, a]) => s + (a[a.length - 1].paid || 0), 0)),
    paidTwiceSample: paidTwice.slice(0, 6).map(([k, a]) => ({ claim: k,
      hist: a.map(o => `${o.check}/${o.status}/${o.file.replace('era_','').replace('.835','')}/${usd(o.paid)}`) })),
    reversalPattern: multi.filter(([, a]) => a.some(o => o.status === '22')).length,
  };
})();

process.stdout.write(JSON.stringify(out, null, 2));
