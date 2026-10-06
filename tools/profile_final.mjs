// Phase 0 pass 5: final cross-tabs needed for DATA_FINDINGS.md
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

const obs = [];
for (const p of parsed) for (const ti of p.tx.keys()) { const t = p.tx[ti];
  const check = (t.hdtm || []).find(d => d[1] === '405')?.[2] || t.bpr?.[16] || '00000000';
  for (const cl of t.claims) obs.push({ file: p.file, fi: files.indexOf(p.file), ti, check,
    payerId: t.payerId, claim: canonId(cl.num), raw: cl.num, status: cl.status,
    charged: c2(cl.charged), paid: c2(cl.paid), svc: cl.svc, casC: cl.casC, lqC: cl.lqC }); }

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

const openDenials = [...latest.entries()].filter(([, o]) => o.status === '4');
const POLICY_EFF = '2026-04-01';
function cat(o) {
  const carcs = [...new Set([...o.casC, ...o.svc.flatMap(s => s.cas)].map(e => e.carc))];
  const dos = line1(o.claim).dos;
  if (carcs.includes('29')) return 'Billing - timely filing';
  if (carcs.includes('B7')) return 'Credentialing';
  if (carcs.includes('197')) return (o.payerId === 'SMP12' && dos < POLICY_EFF) ? 'Payer error' : 'Authorization';
  if (carcs.includes('27')) return 'Eligibility';
  if (carcs.includes('50')) return 'Medical necessity';
  if (carcs.includes('11')) return 'Coding - diagnosis';
  if (carcs.includes('151')) return 'Coding - frequency';
  if (carcs.includes('97')) return 'Coding - modifier';
  if (carcs.includes('18')) return 'Payer error';
  return 'UNMAPPED';
}
const rules = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
const TODAY = Date.parse('2026-09-30'), day = 86400000;
const daysLeftOf = o => {
  const pol = rules[o.payerId]; if (!pol) return null;
  return Math.round((Date.parse(fmt(o.check)) + Number(pol.appeal_window_days_from_denial) * day - TODAY) / day);
};
const fmt = v => v.slice(0, 4) + '-' + v.slice(4, 6) + '-' + v.slice(6, 8);

/* 1. category x recoverable/lost  (the money table) */
const cats = [...new Set(openDenials.map(([k, o]) => cat(o)))];
out.categoryByRecoverability = cats.map(c => {
  const g = openDenials.filter(([k, o]) => cat(o) === c);
  const rec = g.filter(([k, o]) => daysLeftOf(o) >= 0);
  const lost = g.filter(([k, o]) => daysLeftOf(o) < 0);
  return { category: c, total: g.length, totalCharged: usd(g.reduce((a, [k]) => a + chargeOf(k), 0)),
    recoverable: rec.length, recoverableCharged: usd(rec.reduce((a, [k]) => a + chargeOf(k), 0)),
    windowExpired: lost.length, windowExpiredCharged: usd(lost.reduce((a, [k]) => a + chargeOf(k), 0)) };
}).sort((a, b) => b.total - a.total);

/* 2. CSA B7: policy says no appeal basis - how many sit inside the window? */
const b7 = openDenials.filter(([k, o]) => cat(o) === 'Credentialing');
out.csaB7 = { n: b7.length, charged: usd(b7.reduce((a, [k]) => a + chargeOf(k), 0)),
  withinAppealWindow: b7.filter(([k, o]) => daysLeftOf(o) >= 0).length,
  withinAppealWindowCharged: usd(b7.filter(([k, o]) => daysLeftOf(o) >= 0).reduce((a, [k]) => a + chargeOf(k), 0)),
  dateRange: (() => { const d = b7.map(([k]) => line1(k).dos).sort(); return { min: d[0], max: d[d.length - 1] }; })() };

/* 3. preventable-before-billing split using the labeled taxonomy's preventable column */
const lab = readCsv('labeled_denials_sample.csv');
const preventableByCat = Object.fromEntries(lab.map(r => [r.root_cause_category, r.preventable_at_prebill]));
out.preventability = (() => {
  let prev = 0, prevCharged = 0, nonPrev = 0, nonPrevCharged = 0, unknown = 0, unknownCharged = 0;
  for (const [k, o] of openDenials) {
    const p = preventableByCat[cat(o)];
    if (p === 'Yes') { prev++; prevCharged += chargeOf(k); }
    else if (p === 'No') { nonPrev++; nonPrevCharged += chargeOf(k); }
    else { unknown++; unknownCharged += chargeOf(k); }
  }
  return { preventable: { n: prev, charged: usd(prevCharged) },
    notPreventable: { n: nonPrev, charged: usd(nonPrevCharged) },
    categoryNotCoveredByLabels: { n: unknown, charged: usd(unknownCharged) },
    labelsCoveringCategories: Object.keys(preventableByCat) };
})();

/* 4. never adjudicated */
const never = [...byCanon.keys()].filter(k => !hist.has(k));
out.neverAdjudicated = { count: never.length, charged: usd(never.reduce((a, k) => a + chargeOf(k), 0)),
  byPayer: tally(never, k => line1(k).payer_id),
  submittedAfter20260901: never.filter(k => line1(k).submitted_date > '2026-09-01').length,
  submittedBefore20260801: never.filter(k => line1(k).submitted_date < '2026-08-01').length,
  oldestSubmitted: (() => { const d = never.map(k => line1(k).submitted_date).sort(); return d[0]; })() };

/* 5. totals for the reconciliation report */
out.reconTotals = {
  claimExportCharge: usd(claims.reduce((a, r) => a + (c2(r.charge) || 0), 0)),
  remitRows: obs.length,
  remitRowsExcludingDuplicateResent: obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835').length,
  claimChargedTotal_dedup: usd(obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835').reduce((a, o) => a + (o.charged || 0), 0)),
  claimPaidTotal_dedup: usd(obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835').reduce((a, o) => a + (o.paid || 0), 0)),
  bprTotal: usd(parsed.flatMap(p => p.tx).reduce((a, t) => a + (t.bpr ? (c2(t.bpr[2]) || 0) : 0), 0)),
  bprTotal_excludingResent: usd(parsed.filter(p => p.file !== 'era_2026Q2_resent_0719.835').flatMap(p => p.tx).reduce((a, t) => a + (t.bpr ? (c2(t.bpr[2]) || 0) : 0), 0)),
  byPayerClaimCharged: Object.entries(tally(obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835'), o => o.payerId))
    .map(([p, n]) => ({ payer: p, rows: n, charged: usd(obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835' && o.payerId === p).reduce((a, o) => a + (o.charged || 0), 0)),
      paid: usd(obs.filter(o => o.file !== 'era_2026Q2_resent_0719.835' && o.payerId === p).reduce((a, o) => a + (o.paid || 0), 0)) })),
  status22: (() => {
    const r = obs.filter(o => o.status === '22');
    return { rows: r.length, clp04Sum: usd(r.reduce((a, o) => a + (o.paid || 0), 0)),
      claims: [...new Set(r.map(o => o.claim))],
      note: 'CLP04 stored positive; BPR nets as negative' };
  })(),
  foreignBhc: { rows: obs.filter(o => o.raw.startsWith('BHC')).length,
    claims: [...new Set(obs.filter(o => o.raw.startsWith('BHC')).map(o => o.raw))] },
};

/* 6. worklog: dollars and status conflicts */
const wl = readCsv('worklog_extracted.csv').map(r => ({ ...r, c: canonId(r['Claim #']) }));
const deniedSet = new Set(openDenials.map(([k]) => k));
out.worklogFinal = {
  rows: wl.length,
  distinctCanonicalClaims: new Set(wl.map(r => r.c).filter(Boolean)).size,
  claimIdsNeedingNormalisation: wl.filter(r => r['Claim #'] !== (r.c || '')).length,
  blankOwner: wl.filter(r => !String(r.Owner).trim()).length,
  ownersRawVariants: tally(wl, r => r.Owner),
  statusesRawVariants: tally(wl, r => r.Status),
  distinctRawStatuses: new Set(wl.map(r => r.Status)).size,
  claimsLoggedButNotCurrentlyDenied: wl.filter(r => r.c && !deniedSet.has(r.c)).length,
  deniedNeverLogged: [...deniedSet].filter(k => !new Set(wl.map(r => r.c)).has(k)).length,
  loggedAmountTotal: usd(wl.reduce((a, r) => a + (c2(r.Amt) || 0), 0)),
  amountMismatches: (() => {
    let bad = 0; const s = [];
    for (const r of wl) { if (!r.c) continue;
      const amt = c2(r.Amt), exp = chargeOf(r.c);
      if (amt !== exp) { bad++; if (s.length < 8) s.push(`${r.c} worklog=${usd(amt)} claimExport=${usd(exp)}`); } }
    return { count: bad, samples: s };
  })(),
};

process.stdout.write(JSON.stringify(out, null, 2));
