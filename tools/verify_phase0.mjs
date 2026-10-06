// Phase 0 verification — hard assertions on the figures published in docs/DATA_FINDINGS.md.
// Run: node tools/verify_phase0.mjs   (exit code 0 = all pass)
//
// Every check below is re-derived from the raw files. If a number in the doc drifts, this fails.

import fs from 'node:fs';
import path from 'node:path';
const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
let failures = 0, checks = 0;
function ok(name, cond, detail) {
  checks++;
  if (!cond) { failures++; console.log('  FAIL  ' + name + (detail !== undefined ? '  -> ' + detail : '')); }
}
function eq(name, actual, expected) { ok(name, actual === expected, 'actual=' + actual + ' expected=' + expected); }

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
function canonId(raw) { const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i); if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s; return null; }

const DUP = 'era_2026Q2_resent_0719.835';
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();

/* ---------- parse ---------- */
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
      case 'REF': if (p[1] === '2U') t.payerId = p[2]; break;
      case 'DTM': if (!cl) t.hdtm.push(p); break;
      case 'CLP': cl = { num: p[1], status: p[2], charged: p[3], paid: p[4], svc: [], casC: [] }; t.claims.push(cl); sv = null; break;
      case 'SVC': sv = { code: p[1], charged: p[2], paid: p[3], cas: [] }; cl.svc.push(sv); break;
      case 'CAS': { const g = p[1]; for (let k = 2; k + 1 < p.length; k += 2) (sv ? sv.cas : cl.casC).push({ g, carc: p[k], amt: p[k + 1] }); break; }
      case 'PLB': t.plb = (t.plb || 0) + 1; break;
      default: break;
    }
  }
  return { file, tx };
}
const parsed = files.map(parse835);
const obs = [];
for (const p of parsed) for (const ti in p.tx) { const t = p.tx[ti];
  const check = (t.hdtm || []).find(d => d[1] === '405')?.[2] || '00000000';
  for (const cl of t.claims) obs.push({ file: p.file, fi: files.indexOf(p.file), ti: Number(ti), check,
    payerId: t.payerId, claim: canonId(cl.num), raw: cl.num, status: cl.status,
    charged: c2(cl.charged), paid: c2(cl.paid), svc: cl.svc, casC: cl.casC, plb: t.plb || 0 }); }

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canonId(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }
const chargeOf = k => (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0);

/* ordering: check -> file -> transaction -> segment */
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti) || 0);
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

console.log('\n=== A. Claim export ===');
eq('rows = 1323', claims.length, 1323);
eq('distinct claim_id = 1222', new Set(claims.map(r => r.claim_id)).size, 1222);
eq('total charge = $242,585.00 (cents)', claims.reduce((a, r) => a + (c2(r.charge) || 0), 0), 24258500);
eq('no submitted-before-DOS', claims.filter(r => r.submitted_date < r.dos).length, 0);
eq('all DOS ISO', claims.filter(r => !/^\d{4}-\d{2}-\d{2}$/.test(r.dos)).length, 0);
eq('no duplicate (claim,line)', claims.length - new Set(claims.map(r => r.claim_id + '|' + r.line_no)).size, 0);

console.log('\n=== B. Envelope / structure ===');
const expected = { 'era_2026Q1.835': [7, 346], 'era_2026Q2.835': [9, 453],
  'era_2026Q2_resent_0719.835': [9, 453], 'era_2026Q3.835': [9, 398] };
for (const p of parsed) {
  eq(p.file + ' transaction count', p.tx.length, expected[p.file][0]);
  eq(p.file + ' CLP count', p.tx.reduce((a, t) => a + t.claims.length, 0), expected[p.file][1]);
}
eq('no PLB segments anywhere', obs.reduce((a, o) => a + o.plb, 0), 0);
eq('only CLP02 in {1,4,22}', new Set(obs.map(o => o.status)).size <= 3 &&
  [...new Set(obs.map(o => o.status))].every(s => ['1', '4', '22'].includes(s)), true);
eq('only CAS group codes CO/PR',
  [...new Set(obs.flatMap(o => [...o.casC, ...o.svc.flatMap(s => s.cas)].map(e => e.g)))].sort().join(','),
  'CO,PR');

console.log('\n=== C. Money identities (signed, integer cents) ===');
for (const p of parsed) {
  let bpr = 0, paid = 0, charged = 0, cas = 0;
  for (const t of p.tx) { if (t.bpr) bpr += c2(t.bpr[2]) || 0;
    for (const cl of t.claims) { paid += c2(cl.paid) || 0; charged += c2(cl.charged) || 0;
      for (const e of [...cl.casC, ...cl.svc.flatMap(s => s.cas)]) cas += c2(e.amt) || 0; } }
  eq(p.file + ' BPR = sum(CLP04)', bpr, paid);
  eq(p.file + ' CLP03 = CLP04 + sum(CAS)', charged, paid + cas);
}
// claim-level balance on every observation
let balBad = 0;
for (const o of obs) { const cas = [...o.casC, ...o.svc.flatMap(s => s.cas)].reduce((a, e) => a + (c2(e.amt) || 0), 0);
  if (o.charged !== o.paid + cas) balBad++; }
eq('claim-level CLP03 = CLP04 + sum(CAS) on all 1650 observations', balBad, 0);
// service-line balance
let svcBad = 0;
for (const o of obs) for (const s of o.svc) {
  const cas = s.cas.reduce((a, e) => a + (c2(e.amt) || 0), 0);
  if (c2(s.charged) !== c2(s.paid) + cas) svcBad++; }
eq('service-line CLP05 = CLP06 + sum(CAS) on all 1785 lines', svcBad, 0);

// per payer (exclude duplicate resend)
const byPayer = {};
for (const o of obs) if (o.file !== DUP) { const b = byPayer[o.payerId] = byPayer[o.payerId] || { bpr: 0, paid: 0 };
  b.paid += o.paid || 0; }
for (const p of parsed) if (p.file !== DUP) for (const t of p.tx)
  if (t.bpr) byPayer[t.payerId].bpr += c2(t.bpr[2]) || 0;
eq('CSA77 BPR = sum(CLP04)', byPayer.CSA77.bpr, byPayer.CSA77.paid);
eq('MRD55 BPR = sum(CLP04)', byPayer.MRD55.bpr, byPayer.MRD55.paid);
eq('NS401 BPR = sum(CLP04)', byPayer.NS401.bpr, byPayer.NS401.paid);
eq('SMP12 BPR = sum(CLP04)', byPayer.SMP12.bpr, byPayer.SMP12.paid);
eq('total BPR excl resend = $118,220.36', Object.values(byPayer).reduce((a, b) => a + b.bpr, 0), 11822036);

let co = 0, pr = 0;
for (const o of obs) if (o.file !== DUP) for (const e of [...o.casC, ...o.svc.flatMap(s => s.cas)]) {
  const v = c2(e.amt) || 0; if (e.g === 'CO') co += v; else if (e.g === 'PR') pr += v; }
eq('CO total = $108,516.40', co, 10851640);
eq('PR total = $5,643.24', pr, 564324);

console.log('\n=== D. Duplicate resend ===');
const segs = f => fs.readFileSync(path.join(ROOT, 'remits', f), 'latin1').replace(/\r\n?/g, '\n')
  .split('~').map(s => s.replace(/\n/g, '')).filter(Boolean);
const stripEnv = f => segs(f).filter(g => !/^(ISA|GS|GE|IEA)\*/.test(g));
const a = stripEnv('era_2026Q2.835'), b = stripEnv(DUP);
eq('resent is segment-identical outside the envelope', a.length === b.length && a.every((x, i) => x === b[i]), true);
eq('resent differs at the file level (so file hash dedup would fail)',
  fs.readFileSync(path.join(ROOT, 'remits', 'era_2026Q2.835'), 'latin1') !== fs.readFileSync(path.join(ROOT, 'remits', DUP), 'latin1'), true);
const crypto = await import('node:crypto');
const payload = f => segs(f).filter(g => !/^(ISA|GS|ST|SE|GE|IEA)\*/.test(g)).join('~');
const h1 = crypto.createHash('sha256').update(payload('era_2026Q2.835')).digest('hex');
const h2 = crypto.createHash('sha256').update(payload(DUP)).digest('hex');
eq('payload sha256 matches', h1, h2);

console.log('\n=== E. Denials ===');
const open = [...latest.entries()].filter(([, o]) => o.status === '4');
eq('open denials = 136', open.length, 136);
eq('open denial charge = $27,780.00', open.reduce((s, [k]) => s + chargeOf(k), 0), 2778000);
const ever = [...hist.keys()].filter(k => hist.get(k).some(o => o.status === '4'));
eq('ever denied = 142', ever.length, 142);
const recovered = ever.filter(k => latest.get(k).status !== '4');
eq('denied then recovered = 6', recovered.length, 6);
eq('recovered charge = $1,110.00', recovered.reduce((s, k) => s + chargeOf(k), 0), 111000);
eq('ever(142) = open(136) + recovered(6)', 136 + recovered.length, 142);

console.log('\n=== F. Line-level denials ===');
const zeroOnLatest = [...latest.entries()]
  .filter(([, o]) => o.svc.some(s => c2(s.paid) === 0 && c2(s.charged) > 0));
eq('claims with a $0 line in latest observation = 155', zeroOnLatest.length, 155);
const accepted = zeroOnLatest.filter(([, o]) => o.status === '1');
eq('...of which claim-accepted = 19', accepted.length, 19);
eq('accepted $0-line charge = $3,365.00',
  accepted.flatMap(([, o]) => o.svc).filter(s => c2(s.paid) === 0 && c2(s.charged) > 0)
    .reduce((x, s) => x + c2(s.charged), 0), 336500);
const carc97 = zeroOnLatest.filter(([, o]) => o.svc.some(s => s.cas.some(e => e.carc === '97')));
eq('CARC 97 claims (latest) = 19', carc97.length, 19);
eq('CARC 97 all claim-accepted', carc97.every(([, o]) => o.status === '1'), true);
eq('CARC 97 none carries modifier 25',
  carc97.filter(([k]) => (byCanon.get(k) || []).some(r => r.modifier === '25')).length, 0);

console.log('\n=== G. Recoverability ===');
const rules = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
const TODAY = Date.parse('2026-09-30'), day = 86400000;
const dt = v => Date.parse(v.slice(0, 4) + '-' + v.slice(4, 6) + '-' + v.slice(6, 8));
const inWindow = open.filter(([, o]) => dt(o.check) + Number(rules[o.payerId].appeal_window_days_from_denial) * day >= TODAY);
eq('in appeal window = 105', inWindow.length, 105);
eq('in window charge = $21,245.00', inWindow.reduce((s, [k]) => s + chargeOf(k), 0), 2124500);
eq('expired = 31', open.length - inWindow.length, 31);
eq('expired charge = $6,535.00', 2778000 - inWindow.reduce((s, [k]) => s + chargeOf(k), 0), 653500);

console.log('\n=== H. Never adjudicated ===');
const never = [...byCanon.keys()].filter(k => !hist.has(k));
eq('never adjudicated = 58', never.length, 58);
eq('never adjudicated charge = $12,600.00', never.reduce((s, k) => s + chargeOf(k), 0), 1260000);
eq('adjudicated charge + never = export total',
  [...byCanon.keys()].filter(k => hist.has(k)).reduce((s, k) => s + chargeOf(k), 0) +
  never.reduce((s, k) => s + chargeOf(k), 0), 24258500);
eq('every remit claim resolves to the export (0 orphans)',
  obs.filter(o => o.raw.startsWith('GPP') || /^\d/.test(o.raw)).filter(o => !byCanon.has(o.claim)).length, 0);
// export charge == remit charge for every adjudicated claim
eq('export charge = remit CLP03 for all 1164 adjudicated claims',
  [...latest.keys()].filter(k => chargeOf(k) !== latest.get(k).charged).length, 0);

console.log('\n=== I. Timely filing ===');
const tf = readCsv('payer_rules.csv');
const byPid = Object.fromEntries(tf.map(r => [r.payer_id, r]));
const late = claims.filter(r => {
  const pol = byPid[r.payer_id]; if (!pol) return false;
  return (Date.parse(r.submitted_date) - Date.parse(r.dos)) / day > Number(pol.timely_filing_days_from_dos); });
eq('claims submitted after their TF deadline = 9', late.length, 9);
const lateIds = new Set(late.map(r => canonId(r.claim_id)));
const carc29 = new Set(open.filter(([, o]) =>
  [...o.casC, ...o.svc.flatMap(s => s.cas)].some(e => e.carc === '29')).map(([k]) => k));
eq('CARC 29 open denials = 9', carc29.size, 9);
eq('the 9 late submissions ARE the 9 CARC 29 denials',
  [...lateIds].every(k => carc29.has(k)) && [...carc29].every(k => lateIds.has(k)), true);

console.log('\n=== J. Taxonomy vs expert labels (40 rows) ===');
const lab = readCsv('labeled_denials_sample.csv');
const PRE = '2026-04-01';
function cat(k, o) {
  const carcs = [...new Set([...o.casC, ...o.svc.flatMap(s => s.cas)].map(e => e.carc))];
  const dos = (byCanon.get(k) || [])[0]?.dos;
  if (carcs.includes('29')) return 'Billing - timely filing';
  if (carcs.includes('B7')) return 'Credentialing';
  if (carcs.includes('197')) return (o.payerId === 'SMP12' && dos < PRE) ? 'Payer error' : 'Authorization';
  if (carcs.includes('27')) return 'Eligibility';
  if (carcs.includes('50')) return 'Medical necessity';
  if (carcs.includes('11')) return 'Coding - diagnosis';
  if (carcs.includes('151')) return 'Coding - frequency';
  if (carcs.includes('97')) return 'Coding - modifier';
  if (carcs.includes('18')) return 'Payer error';
  return 'UNMAPPED';
}
let matched = 0; const mism = [];
for (const r of lab) {
  const o = latest.get(r.claim_id);
  if (!o) { mism.push(r.claim_id + ' (no remit observation)'); continue; }
  const got = cat(r.claim_id, o);
  if (got === r.root_cause_category) matched++; else mism.push(r.claim_id + ' expected=' + r.root_cause_category + ' got=' + got);
}
eq('rules-only taxonomy matches all 40 expert labels', matched, 40);
if (mism.length) console.log('        mismatches: ' + mism.join(' | '));
eq('no open denial is UNMAPPED', open.filter(([k, o]) => cat(k, o) === 'UNMAPPED').length, 0);

console.log('\n=== K. Worklog ===');
const wl = readCsv('worklog_extracted.csv');
eq('worklog rows = 120', wl.length, 120);
eq('all worklog claim ids resolve after normalisation',
  wl.filter(r => !byCanon.has(canonId(r['Claim #']))).length, 0);
eq('rows needing claim-id normalisation = 42',
  wl.filter(r => r['Claim #'] !== (canonId(r['Claim #']) || '')).length, 42);
eq('distinct status strings = 10', new Set(wl.map(r => r.Status)).size, 10);
eq('blank owners = 22', wl.filter(r => !String(r.Owner).trim()).length, 22);
const openIds = new Set(open.map(([k]) => k));
const loggedKeys = new Set(wl.map(r => canonId(r['Claim #'])).filter(Boolean));
eq('logged rows whose claim is not an open denial = 25',
  wl.filter(r => canonId(r['Claim #']) && !openIds.has(canonId(r['Claim #']))).length, 25);
eq('distinct logged claims = 111', loggedKeys.size, 111);
eq('open denials never logged = 48',
  [...openIds].filter(k => !loggedKeys.has(k)).length, 48);
eq('logged amount total = $24,570', wl.reduce((a, r) => a + (c2(r.Amt) || 0), 0), 2457000);
eq('worklog amount vs claim export mismatches = 10',
  wl.filter(r => { const k = canonId(r['Claim #']); return k && (c2(r.Amt) || 0) !== chargeOf(k); }).length, 10);

console.log('\n' + (failures === 0
  ? 'ALL ' + checks + ' CHECKS PASSED'
  : failures + ' of ' + checks + ' CHECKS FAILED'));
process.exit(failures === 0 ? 0 : 1);
