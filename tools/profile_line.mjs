// Phase 0 pass 4: line-level denials, the CARC-197 effective-date rule, $ at stake.
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
const usd = c => c === null || c === undefined || Number.isNaN(c) ? null
  : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
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

const obs = [];
for (const p of parsed) for (const ti of p.tx.keys()) { const t = p.tx[ti];
  const check = (t.hdtm || []).find(d => d[1] === '405')?.[2] || t.bpr?.[16] || '00000000';
  for (const cl of t.claims) obs.push({
    file: p.file, fi: files.indexOf(p.file), ti, check, payerId: t.payerId, payer: t.payer,
    claim: canonId(cl.num), raw: cl.num, status: cl.status, charged: c2(cl.charged), paid: c2(cl.paid),
    svc: cl.svc, casC: cl.casC, lqC: cl.lqC }); }

const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti));
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canonId(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }
const line1 = k => byCanon.get(k)?.[0] || {};
const chargeOf = k => (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0);
const out = {};

/* 1. LINE-level denials: a service line paid $0 while the claim is accepted */
const lineDenied = [];   // {claim, file, check, status, lineCode, lineCharged, carc, group}
for (const o of obs) for (const s of o.svc) {
  const sp = c2(s.paid), sc = c2(s.charged);
  if (sp === 0 && sc > 0) {
    const carc = s.cas.map(e => e.carc);
    lineDenied.push({ claim: o.claim, status: o.status, file: o.file, check: o.check, payer: o.payerId,
      code: s.code, charged: sc, paid: sp, carc, rarc: s.lq.map(l => (l[2] || '').trim()).filter(Boolean) });
  }
}
/* latest observation only: build the $0-line set directly from the winning row, so that a
   claim denied then re-paid inside the SAME check+file cannot leak history into it */
const latestLineDenied = new Map();
for (const [k, o] of latest) {
  const arr = [];
  for (const s of o.svc) {
    const sp = c2(s.paid), sc = c2(s.charged);
    if (sp === 0 && sc > 0) arr.push({ claim: k, status: o.status, file: o.file, check: o.check,
      payer: o.payerId, code: s.code, charged: sc, paid: sp,
      carc: s.cas.map(e => e.carc), rarc: s.lq.map(l => (l[2] || '').trim()).filter(Boolean) });
  }
  if (arr.length) latestLineDenied.set(k, arr);
}
out.lineLevelDenials = {
  serviceLinesPaidZero_allObservations: lineDenied.length,
  onClaimStatus4: lineDenied.filter(l => l.status === '4').length,
  onClaimStatus1: lineDenied.filter(l => l.status === '1').length,
  distinctClaimsWithAZeroPaidLine_latestObservation: latestLineDenied.size,
  acceptedClaimsWithAZeroPaidLine_inLatestObservation: [...latest.entries()]
    .filter(([, o]) => o.status === '1' && o.svc.some(s => c2(s.paid) === 0 && c2(s.charged) > 0)).length,
  acceptedLinesPaidZero: [...latestLineDenied.values()].flat().filter(l => l.status === '1').length,
  zeroPaidLineDollars_acceptedClaimsOnly: usd([...latest.entries()]
    .filter(([, o]) => o.status === '1')
    .flatMap(([, o]) => o.svc).filter(s => c2(s.paid) === 0 && c2(s.charged) > 0)
    .reduce((a, s) => a + c2(s.charged), 0)),
  byCarc: Object.entries(tally([...latestLineDenied.values()].flat(), l => l.carc.join('+') || 'none')).sort((a, b) => b[1] - a[1]),
  sample: [...latestLineDenied.entries()].slice(0, 6).map(([k, v]) => ({ claim: k, status: v[0].status,
    lines: v.map(l => `${l.code} ${usd(l.charged)}->${usd(l.paid)} [${l.carc.join('/')}]`) })),
};

/* 2. Is CARC 97 always a line-level denial on an accepted claim? (latest observation only) */
const carc97claims = [...latest.entries()]
  .filter(([, o]) => o.svc.some(s => s.cas.some(e => e.carc === '97')))
  .map(([k]) => k);
out.carc97 = {
  claimsWithCarc97OnALine: carc97claims.length,
  ofWhichClaimAccepted: carc97claims.filter(k => latest.get(k)?.status === '1').length,
  ofWhichClaimDenied: carc97claims.filter(k => latest.get(k)?.status === '4').length,
  claimsCarryingModifier25AmongThem: carc97claims.filter(k => (byCanon.get(k) || []).some(r => r.modifier === '25')).length,
  charges: usd(carc97claims.reduce((a, k) => a + chargeOf(k), 0)),
  zeroLineCharges: usd(carc97claims.filter(k => latest.get(k)?.status === '1')
    .flatMap(k => latest.get(k).svc)
    .filter(s => s.cas.some(e => e.carc === '97') && c2(s.paid) === 0)
    .reduce((a, s) => a + c2(s.charged), 0)),
};

/* 3. THE RULE: CARC 197 + SMP12 split by policy effective date 2026-04-01 */
const r197 = [...latest.entries()].filter(([, o]) => [...o.casC, ...o.svc.flatMap(s => s.cas)].some(e => e.carc === '197'));
out.carc197Split = {
  totalLatestClaimsWith197: r197.length,
  byPayer: Object.entries(tally(r197, ([k, o]) => o.payerId)),
  beforeEffectiveDate: r197.filter(([k]) => line1(k).dos < '2026-04-01').map(([k, o]) => ({ claim: k, dos: line1(k).dos, payer: o.payerId, cpt: (byCanon.get(k) || []).map(r => r.cpt).join('+') })),
  onOrAfterEffectiveDate: r197.filter(([k]) => line1(k).dos >= '2026-04-01').length,
  anyCarryingAuthNumber: r197.filter(([k]) => (byCanon.get(k) || []).some(r => String(r.auth_number).trim())).length,
};

/* 4. Rule of every open denial => CARC, $, and the labeled category it implies */
const openDenials = [...latest.entries()].filter(([, o]) => o.status === '4');
const policyEff = '2026-04-01';
function impliedCategory(o) {
  const carcs = [...new Set([...o.casC, ...o.svc.flatMap(s => s.cas)].map(e => e.carc))];
  const rarcs = [...new Set([...o.lqC, ...o.svc.flatMap(s => s.lq)].map(l => (l[2] || '').trim()).filter(Boolean))];
  const dos = line1(o.claim).dos;
  if (carcs.includes('29')) return 'Billing - timely filing';
  if (carcs.includes('B7')) return 'Credentialing';
  if (carcs.includes('197')) return (o.payerId === 'SMP12' && dos < policyEff) ? 'Payer error' : 'Authorization';
  if (carcs.includes('27')) return 'Eligibility';
  if (carcs.includes('50')) return 'Medical necessity';
  if (carcs.includes('11')) return 'Coding - diagnosis';
  if (carcs.includes('151')) return 'Coding - frequency';
  if (carcs.includes('97')) return 'Coding - modifier';
  if (carcs.includes('18')) return 'Payer error';
  return 'UNMAPPED:' + carcs.join('+');
}
out.openDenialTaxonomy = {
  count: openDenials.length,
  categories: Object.entries(tally(openDenials, ([k, o]) => impliedCategory(o))).sort((a, b) => b[1] - a[1]),
  chargedByCategory: Object.fromEntries([...new Set(openDenials.map(([k, o]) => impliedCategory(o)))].map(cat => {
    const g = openDenials.filter(([k, o]) => impliedCategory(o) === cat);
    return [cat, { n: g.length, charged: usd(g.reduce((a, [k]) => a + chargeOf(k), 0)) }];
  })),
  unmapped: openDenials.filter(([k, o]) => impliedCategory(o).startsWith('UNMAPPED')).map(([k, o]) => ({ claim: k, payer: o.payerId, carcs: [...new Set([...o.casC, ...o.svc.flatMap(s => s.cas)].map(e => e.carc))] })),
  totalCharged: usd(openDenials.reduce((a, [k]) => a + chargeOf(k), 0)),
};

/* 5. Validate the implied taxonomy against the 40 labeled rows */
const lab = readCsv('labeled_denials_sample.csv');
let match = 0; const mism = [];
for (const l of lab) {
  const k = canonId(l.claim_id);
  const o = latest.get(k);
  if (!o) { mism.push({ claim: k, issue: 'no remit row', label: l.root_cause_category }); continue; }
  const isLineOnly = o.status === '1' && latestLineDenied.has(k);
  const implied = o.status === '4' ? impliedCategory(o) : (isLineOnly ? 'Coding - modifier' : 'NOT_A_DENIAL');
  if (implied === l.root_cause_category) match++; else mism.push({ claim: k, label: l.root_cause_category, implied, status: o.status });
}
out.taxonomyVsLabels = { labeled: lab.length, matched: match, mismatched: mism.length, mismatches: mism,
  accuracyPct: +(100 * match / lab.length).toFixed(1) };

/* 6. Recoverability with line-level denials included (money view) */
const TODAY = Date.parse('2026-09-30'), day = 86400000;
const rules = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
const bucket = { recoverable: { n: 0, amt: 0 }, lost: { n: 0, amt: 0 } };
const byPayerBucket = {};
for (const [k, o] of openDenials) {
  const pol = rules[o.payerId]; if (!pol) continue;
  const dd = Date.parse(fmt(o.check));
  const dl = dd + Number(pol.appeal_window_days_from_denial) * day;
  const daysLeft = Math.round((dl - TODAY) / day);
  const b = daysLeft < 0 ? 'lost' : 'recoverable';
  bucket[b].n++; bucket[b].amt += chargeOf(k);
  byPayerBucket[o.payerId] = byPayerBucket[o.payerId] || { recoverable: { n: 0, amt: 0 }, lost: { n: 0, amt: 0 } };
  byPayerBucket[o.payerId][b].n++; byPayerBucket[o.payerId][b].amt += chargeOf(k);
}
out.recoverabilityFinal = {
  openDenials: openDenials.length,
  recoverable: { n: bucket.recoverable.n, charged: usd(bucket.recoverable.amt) },
  lost: { n: bucket.lost.n, charged: usd(bucket.lost.amt) },
  byPayer: (() => {
    const res = {};
    for (const p of Object.keys(byPayerBucket).sort()) {
      const v = byPayerBucket[p];
      res[p] = { recoverable: { n: v.recoverable.n, charged: usd(v.recoverable.amt) },
                 lost: { n: v.lost.n, charged: usd(v.lost.amt) } };
    }
    return res;
  })(),
  daysLeftDistribution: Object.entries(tally(openDenials, ([k, o]) => {
    const pol = rules[o.payerId]; if (!pol) return 'no-policy';
    const daysLeft = Math.round((Date.parse(fmt(o.check)) + Number(pol.appeal_window_days_from_denial) * day - TODAY) / day);
    return daysLeft < 0 ? 'expired' : daysLeft <= 14 ? '0-14d' : daysLeft <= 30 ? '15-30d' : daysLeft <= 60 ? '31-60d' : '61+d';
  })),
};
function fmt(v) { return v.slice(0, 4) + '-' + v.slice(4, 6) + '-' + v.slice(6, 8); }

/* 7. dollars actually recovered vs never recovered (resolved denials) */
const everDenied = [...hist.entries()].filter(([, a]) => a.some(o => o.status === '4'));
const resolved = everDenied.filter(([k]) => latest.get(k)?.status === '1');
out.denialOutcomes = {
  everDenied: everDenied.length,
  currentlyDenied: openDenials.length,
  resolvedNowPaid: resolved.length,
  resolvedCharged: usd(resolved.reduce((a, [k]) => a + chargeOf(k), 0)),
  resolvedClaims: resolved.map(([k]) => ({ claim: k, charged: usd(chargeOf(k)) })),
};

/* 8. pre-bill flag vs denial, restricted to adjudicated claims.
      NOTE: a claim may have several observations inside ONE check+file (denied then paid in
      the same transaction), so "has a $0 line" must be evaluated on the latest observation
      itself, not on any historical row that happens to share check+file. */
const zeroLineOnLatestAccepted = new Set(
  [...latest.entries()]
    .filter(([, o]) => o.status === '1' && o.svc.some(s => c2(s.paid) === 0 && c2(s.charged) > 0))
    .map(([k]) => k));
out.prebillEffect = ['Y', 'N'].map(v => {
  const g = [...byCanon.entries()].filter(([, rows]) => rows[0].prebill_reviewed === v);
  const adj = g.filter(([k]) => latest.has(k));
  const den = adj.filter(([k]) => latest.get(k).status === '4');
  const lineDen = adj.filter(([k]) => zeroLineOnLatestAccepted.has(k));
  return { prebill: v, claims: g.length, adjudicated: adj.length, claimDenied: den.length,
    lineDeniedOnAccepted: lineDen.length,
    denialRatePct: adj.length ? +(100 * (den.length + lineDen.length) / adj.length).toFixed(1) : null,
    deniedCharged: usd(den.reduce((a, [k]) => a + chargeOf(k), 0)) };
});

/* 9. coder C07 deep-dive (21% denial rate) */
out.coderOutliers = [...new Set(claims.map(r => r.coder_id))].sort().map(c => {
  const g = [...byCanon.entries()].filter(([, rows]) => rows[0].coder_id === c);
  const adj = g.filter(([k]) => latest.has(k));
  const den = adj.filter(([k]) => latest.get(k).status === '4');
  const carcs = tally(den.flatMap(([k]) => [...new Set([...latest.get(k).casC, ...latest.get(k).svc.flatMap(s => s.cas)].map(e => e.carc))]), x => x);
  return { coder: c, claims: g.length, adjudicated: adj.length, denied: den.length,
    denialRatePct: adj.length ? +(100 * den.length / adj.length).toFixed(1) : null, carcs };
}).sort((a, b) => b.denialRatePct - a.denialRatePct);

process.stdout.write(JSON.stringify(out, null, 2));
