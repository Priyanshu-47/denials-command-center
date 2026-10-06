// Q2: three mutually exclusive recoverability buckets per denial category,
// plus which window (appeal / corrected-claim / timely-filing) actually governs each route.
import fs from 'node:fs';
import path from 'node:path';
const ROOT = process.env.DATA_DIR || 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
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
const canon = raw => { const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i); if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s; return null; };

const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const obs = [];
for (const file of files) {
  const segs = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').split('~').map(s => s.replace(/\n/g, '')).filter(Boolean);
  let ck = '00000000', tk = -1, idx = -1, pid = '', cur = null, inSvc = false;
  for (const g of segs) { const p = g.split('*'); idx++;
    if (p[0] === 'DTM' && p[1] === '405') ck = p[2];
    if (p[0] === 'ST') tk++;
    if (p[0] === 'REF' && p[1] === '2U') pid = p[2];
    if (p[0] === 'CLP') { cur = { file, fi: files.indexOf(file), tk, idx, ck, pid, claim: canon(p[1]), status: p[2], cas: [], svc: [] }; obs.push(cur); inSvc = false; }
    else if (p[0] === 'SVC' && cur) { cur.svc.push({ cas: [] }); inSvc = true; }
    else if (p[0] === 'CAS' && cur) { for (let k = 2; k + 1 < p.length; k += 2) (inSvc ? cur.svc.at(-1) : cur).cas.push(p[k]); }
  }
}
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.ck - y.ck) || (x.fi - y.fi) || (x.tk - y.tk) || (x.idx - y.idx));
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canon(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }
const chargeOf = k => (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0);
const rules = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
const POLICY_EFF = '2026-04-01';
const TODAY = Date.parse('2026-09-30'), DAY = 86400000;
const dt = v => Date.parse(v.slice(0, 4) + '-' + v.slice(4, 6) + '-' + v.slice(6, 8));

function carcsOf(o) { return [...new Set([...o.cas, ...o.svc.flatMap(s => s.cas)])]; }
function category(k, o) {
  const c = carcsOf(o), dos = (byCanon.get(k) || [])[0]?.dos;
  if (c.includes('29')) return 'Billing - timely filing';
  if (c.includes('B7')) return 'Credentialing';
  if (c.includes('197')) return (o.pid === 'SMP12' && dos < POLICY_EFF) ? 'Payer error' : 'Authorization';
  if (c.includes('27')) return 'Eligibility';
  if (c.includes('50')) return 'Medical necessity';
  if (c.includes('11')) return 'Coding - diagnosis';
  if (c.includes('151')) return 'Coding - frequency';
  if (c.includes('97')) return 'Coding - modifier';
  if (c.includes('18')) return 'Duplicate submission (unvalidated)';
  return 'UNMAPPED';
}

const open = [...latest.entries()].filter(([, o]) => o.status === '4');

/* route analysis: is there any route left for each denial? */
const routes = open.map(([k, o]) => {
  const pol = rules[o.pid];
  const denial = dt(o.ck);
  const appealEnd = denial + Number(pol.appeal_window_days_from_denial) * DAY;
  const correctedEnd = denial + Number(pol.corrected_claim_window_days_from_denial) * DAY;
  const dos = (byCanon.get(k) || [])[0].dos;
  const tfEnd = Date.parse(dos) + Number(pol.timely_filing_days_from_dos) * DAY;
  const anyRouteOpen = appealEnd >= TODAY || correctedEnd >= TODAY;
  return { k, o, appealEnd, correctedEnd, tfEnd, anyRouteOpen,
    policyBars: category(k, o) === 'Credentialing' };
});
const windowsAgree = routes.every(r => r.appealEnd === r.correctedEnd);
console.log('appeal window == corrected-claim window for every open denial? ', windowsAgree);
console.log('denials where appeal closed but corrected-claim still open:',
  routes.filter(r => r.appealEnd < TODAY && r.correctedEnd >= TODAY).length);
console.log('denials where any route (appeal OR corrected) still open:',
  routes.filter(r => r.anyRouteOpen).length,
  ' vs appeal-only:', routes.filter(r => r.appealEnd >= TODAY).length);
console.log('denials where TF-from-DOS is still open although every denial route is closed:',
  routes.filter(r => !r.anyRouteOpen && r.tfEnd >= TODAY).length,
  '(TF governs first submission, not a route for an already-adjudicated claim)\n');

/* three mutually exclusive buckets */
const bucket = r => !r.anyRouteOpen ? 'EXPIRED'
  : r.policyBars ? 'POLICY_BLOCKED'
  : 'RECOVERABLE';
const agg = {};
for (const r of routes) {
  const b = bucket(r), c = category(r.k, r.o);
  const a = agg[c] = agg[c] || { RECOVERABLE: [0, 0], POLICY_BLOCKED: [0, 0], EXPIRED: [0, 0], total: [0, 0] };
  a[b][0]++; a[b][1] += chargeOf(r.k);
  a.total[0]++; a.total[1] += chargeOf(r.k);
}
const money = c => '$' + (c / 100).toFixed(2);
const T = { RECOVERABLE: [0, 0], POLICY_BLOCKED: [0, 0], EXPIRED: [0, 0], total: [0, 0] };
console.log('| Denial category | open n/$ | Recoverable n/$ | Policy-blocked n/$ | Expired n/$ |');
console.log('|---|---|---|---|---|');
for (const [c, a] of Object.entries(agg).sort((a, b) => b[1].total[0] - a[1].total[0])) {
  for (const b of ['RECOVERABLE', 'POLICY_BLOCKED', 'EXPIRED', 'total']) { T[b][0] += a[b][0]; T[b][1] += a[b][1]; }
  console.log(`| ${c} | ${a.total[0]} / ${money(a.total[1])} | ${a.RECOVERABLE[0]} / ${money(a.RECOVERABLE[1])} | ${a.POLICY_BLOCKED[0]} / ${money(a.POLICY_BLOCKED[1])} | ${a.EXPIRED[0]} / ${money(a.EXPIRED[1])} |`);
}
console.log(`| **TOTAL** | **${T.total[0]} / ${money(T.total[1])}** | **${T.RECOVERABLE[0]} / ${money(T.RECOVERABLE[1])}** | **${T.POLICY_BLOCKED[0]} / ${money(T.POLICY_BLOCKED[1])}** | **${T.EXPIRED[0]} / ${money(T.EXPIRED[1])}** |`);
console.log('\nsum check: ' + T.RECOVERABLE[0] + ' + ' + T.POLICY_BLOCKED[0] + ' + ' + T.EXPIRED[0] + ' = ' +
  (T.RECOVERABLE[0] + T.POLICY_BLOCKED[0] + T.EXPIRED[0]) + ' (must be 136)');
console.log('money check: ' + money(T.RECOVERABLE[1]) + ' + ' + money(T.POLICY_BLOCKED[1]) + ' + ' + money(T.EXPIRED[1]) +
  ' = ' + money(T.RECOVERABLE[1] + T.POLICY_BLOCKED[1] + T.EXPIRED[1]) + ' (must be $27,780.00)');

/* CARC-18 money */
const c18 = routes.filter(r => category(r.k, r.o) === 'Duplicate submission (unvalidated)');
console.log('\nCARC-18 open denials: ' + c18.length + ' / ' + money(c18.reduce((a, r) => a + chargeOf(r.k), 0)) +
  '  bucket=' + [...new Set(c18.map(bucket))].join(','));
