// Q5: for every open CARC-18 denial, is there an earlier claim for the same
// patient + DOS + CPT, and was it paid? (an expected duplicate of a paid claim
// means "no action", which changes the next step)
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
    if (p[0] === 'CLP') { cur = { file, fi: files.indexOf(file), tk, idx, ck, pid, claim: canon(p[1]), status: p[2], paid: p[4], cas: [], svc: [] }; obs.push(cur); inSvc = false; }
    else if (p[0] === 'SVC' && cur) { cur.svc.push({ code: p[1].replace(/^[A-Z]{2}:/, ''), cas: [] }); inSvc = true; }
    else if (p[0] === 'CAS' && cur) { for (let k = 2; k + 1 < p.length; k += 2) (inSvc ? cur.svc.at(-1) : cur).cas.push(p[k]); }
  }
}
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.ck - y.ck) || (x.fi - y.fi) || (x.tk - y.tk) || (x.idx - y.idx));
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

const claims = readCsv('claims_export.csv');
const open = [...latest.entries()].filter(([, o]) => o.status === '4');
const c18 = open.filter(([, o]) => [...o.cas, ...o.svc.flatMap(s => s.cas)].includes('18'));

// group every claim line by patient + DOS + CPT
const key = r => [r.patient_first, r.patient_last, r.patient_dob, r.dos, r.cpt].map(x => x.toLowerCase()).join('|');
const groups = new Map();
for (const r of claims) { const k = key(r); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(r); }

console.log('open CARC-18 (Exact duplicate claim/service) denials:', c18.length, '\n');
let withTwin = 0;
for (const [k, o] of c18) {
  const rows = claims.filter(r => r.claim_id === k);
  const twins = [];
  for (const r of rows) for (const other of groups.get(key(r)) || []) {
    if (other.claim_id === k) continue;
    const lo = latest.get(other.claim_id);
    twins.push({ claim: other.claim_id, submitted: other.submitted_date, payer: other.payer_id,
      status: lo ? lo.status : 'NO-REMIT', paid: lo ? lo.paid : null });
  }
  if (twins.length) withTwin++;
  console.log(`  ${k}  payer=${o.pid}  lines=${rows.map(r => r.cpt).join('+')}  submitted=${rows[0].submitted_date}`);
  console.log(`      same patient+DOS+CPT siblings: ${twins.length ? JSON.stringify(twins) : 'NONE'}`);
}
console.log(`\n  ${withTwin} of ${c18.length} have at least one sibling claim for the same patient+DOS+CPT`);
