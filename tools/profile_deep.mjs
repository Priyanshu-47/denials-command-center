// Phase 0 deep pass: canonical matching, 835 money balancing, denial economics.
import fs from 'node:fs';
import path from 'node:path';
const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';

function readCsv(file) {
  const text = fs.readFileSync(path.join(ROOT, file), 'utf8').replace(/^\uFEFF/, '');
  const rows = []; let i = 0, f = '', row = [], q = false;
  while (i < text.length) {
    const c = text[i];
    if (q) { if (c === '"') { if (text[i + 1] === '"') { f += '"'; i++; } else q = false; } else f += c; }
    else if (c === '"') q = true;
    else if (c === ',') { row.push(f); f = ''; }
    else if (c === '\n') { row.push(f); rows.push(row); row = []; f = ''; }
    else if (c !== '\r') f += c;
    i++;
  }
  if (f !== '' || row.length) { row.push(f); rows.push(row); }
  const header = rows[0] || [];
  return rows.slice(1).filter(r => r.some(v => v !== '')).map(r => Object.fromEntries(header.map((h, k) => [h, r[k] ?? ''])));
}
const c2 = s => { if (s === null || s === undefined) return null;
  const t = String(s).trim().replace(/[$,\s]/g, ''); if (t === '') return null;
  if (/^\(.*\)$/.test(t)) return -Math.round(Number(t.slice(1, -1)) * 100);
  const n = Number(t); return Number.isFinite(n) ? Math.round(n * 100) : NaN; };
const usd = c => c === null || c === undefined || Number.isNaN(c) ? null : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
const tally = (a, fn) => { const m = {}; for (const x of a) { const k = fn(x); m[k] = (m[k] || 0) + 1; } return m; };

/* --- canonical claim id -------------------------------------------------
   Accepts: GPP-2026-000494 | GPP2026000494 | 000494 | gpp-2026-000494
   Returns  { canon, foreign } ; foreign = non-GPP namespace (e.g. BHC-...). */
function canonId(raw) {
  const s = String(raw ?? '').trim();
  if (!s) return { canon: null, raw: s, reason: 'empty' };
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i);
  if (m) return { canon: 'GPP-2026-' + m[2].padStart(6, '0'), raw: s, reason: null };
  if (/^\d{1,6}$/.test(s)) return { canon: 'GPP-2026-' + s.padStart(6, '0'), raw: s, reason: null };
  if (/^GPP-2026-\d{6}$/.test(s)) return { canon: s, raw: s, reason: null };
  const other = s.match(/^([A-Z]{2,4})-(\d{4})-(\d+)$/i);
  if (other) return { canon: null, raw: s, reason: 'foreign_prefix:' + other[1].toUpperCase() };
  return { canon: null, raw: s, reason: 'unrecognised:' + s.slice(0, 24) };
}

/* --- parse 835 (kept deliberately plain) ------------------------------- */
function parse835(file) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').replace(/\r\n?/g, '\n');
  const out = { file, tx: [], problems: [] };
  let t = null, cl = null, sv = null;
  for (const seg of raw.split('~').map(s => s.replace(/\n/g, '')).filter(Boolean)) {
    const p = seg.split('*'), tag = p[0];
    if (tag === 'ISA') { t = { envelope: 'ISA', claims: [] }; continue; }
    if (tag === 'GS') { if (t && !t.st) { /* husk, discard */ } t = { claims: [], gs: p }; continue; }
    if (tag === 'GE') { t = null; continue; }
    if (tag === 'ST') { if (!t) { out.problems.push('ST_without_GS'); t = { claims: [] }; }
      t.st = p; t.claims = []; t.headerDtm = []; cl = null; sv = null; continue; }
    if (tag === 'SE') { if (t && t.st) out.tx.push(t); t = null; cl = null; sv = null; continue; }
    if (!t) { out.problems.push('stray:' + tag); continue; }
    switch (tag) {
      case 'BPR': t.bpr = p; break;
      case 'TRN': t.trn = p; break;
      case 'N1': if (p[1] === 'PR') { t.payer = p[2]; } else if (p[1] === 'PE') { t.payee = p[2]; } break;
      case 'REF': if (p[1] === '2U') t.payerId = p[2]; if (p[1] === 'EQ') t.claimRef = p[2]; break;
      case 'DTM': if (!cl) t.headerDtm = (t.headerDtm || []).concat([p]); break;
      case 'PLB': t.plb = (t.plb || []).concat([p]); break;
      case 'CLP': cl = { num: p[1], status: p[2], charged: p[3], paid: p[4], patientResp: p[5],
          filingIndicator: p[6], ctrl: p[7], facilityType: p[8], frequency: p[9],
          svc: [], casClaim: [], lqClaim: [], dtm: [] }; t.claims.push(cl); sv = null; break;
      case 'SVC': sv = { code: p[1], charged: p[2], paid: p[3], cas: [], lq: [], dtm: [], amt: [] }; cl.svc.push(sv); break;
      case 'CAS': { const g = p[1]; for (let k = 2; k + 1 < p.length; k += 2) {
          const e = { group: g, carc: p[k], amount: p[k + 1] };
          (sv ? sv.cas : cl.casClaim).push(e); } break; }
      case 'LQ': (sv ? sv.lq : cl.lqClaim).push(p); break;
      case 'AMT': if (sv) sv.amt.push(p); break;
      case 'DTM': break;
      default: break;
    }
    if (tag === 'DTM' && cl) cl.dtm.push(p);
  }
  if (t) out.problems.push('unterminated_transaction');
  return out;
}

const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const parsed = files.map(parse835);

/* --- claim export ------------------------------------------------------ */
const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) {
  const k = canonId(r.claim_id).canon;
  if (!byCanon.has(k)) byCanon.set(k, []);
  byCanon.get(k).push(r);
}

/* --- remit rows, canonicalised ----------------------------------------- */
const remitRows = [];
const remitClaimCanon = new Map();
for (const p of parsed) for (const t of p.tx) for (const cl of t.claims) {
  const cid = canonId(cl.num);
  const checkDate = (t.headerDtm || []).find(d => d[1] === '405')?.[2] || t.bpr?.[16] || null;
  const row = { file: p.file, payer: t.payer || null, payerId: t.payerId || null,
    claimRaw: cl.num, claim: cid.canon, foreign: cid.reason,
    status: cl.status, charged: c2(cl.charged), paid: c2(cl.paid),
    patientResp: c2(cl.patientResp), frequency: cl.frequency, checkDate,
    svc: cl.svc, casClaim: cl.casClaim, lqClaim: cl.lqClaim, dtm: cl.dtm,
    txIndex: p.tx.indexOf(t) };
  remitRows.push(row);
  if (cid.canon) { const a = remitClaimCanon.get(cid.canon) || []; a.push(row); remitClaimCanon.set(cid.canon, a); }
}

const out = {};

/* 1. identifier namespaces */
out.identifiers = {
  claimExportDistinct: byCanon.size,
  remitRawDistinct: new Set(remitRows.map(r => r.claimRaw)).size,
  remitCanonDistinct: remitClaimCanon.size,
  remitForeignRows: remitRows.filter(r => r.foreign).length,
  remitForeignReasons: tally(remitRows.filter(r => r.foreign), r => r.foreign),
  remitUnparseableRows: remitRows.filter(r => !r.canon && !r.foreign).length,
  rawFormatVariants: tally(remitRows, r => /^\d+$/.test(r.claimRaw) ? 'bareDigits'
    : /^GPP-2026-/.test(r.claimRaw) ? 'GPP-2026-######'
    : /^GPP\d{10,}$/.test(r.claimRaw) ? 'GPP########'
    : 'other:' + r.claimRaw.slice(0, 14)),
};

/* 2. true match rates after canonicalisation */
const remitCanonSet = new Set(remitClaimCanon.keys());
const claimIds = [...byCanon.keys()];
out.matching = {
  claimsInExport: claimIds.length,
  claimsSeenInSomeRemit: claimIds.filter(k => remitCanonSet.has(k)).length,
  claimsNeverAdjudicated: claimIds.filter(k => !remitCanonSet.has(k)).length,
  remitClaimsNotInExport: [...remitCanonSet.keys()].filter(k => !byCanon.has(k)).length,
  remitClaimsNotInExportSample: [...remitCanonSet.keys()].filter(k => !byCanon.has(k)).slice(0, 8),
};

/* 3. amount balancing per claim: charged vs paid + sum(adjustments) */
let balOK = 0, balBad = 0; const balBadSamples = [];
let lineBalOK = 0, lineBalBad = 0; const lineBadSamples = [];
let adjTotalCO = 0, adjTotalPR = 0, adjTotalOA = 0, adjTotalPI = 0;
for (const r of remitRows) {
  let adj = 0;
  for (const e of [...r.casClaim, ...r.svc.flatMap(s => s.cas)]) {
    const v = c2(e.amount); if (Number.isNaN(v)) continue;
    adj += Math.abs(v);
    if (e.group === 'CO') adjTotalCO += Math.abs(v);
    else if (e.group === 'PR') adjTotalPR += Math.abs(v);
    else if (e.group === 'OA') adjTotalOA += Math.abs(v);
    else if (e.group === 'PI') adjTotalPI += Math.abs(v);
  }
  const lhs = (r.charged || 0), rhs = (r.paid || 0) + adj;
  if (lhs === rhs) balOK++; else { balBad++; if (balBadSamples.length < 6) balBadSamples.push(`${r.claimRaw} ${r.file} charged=${usd(r.charged)} paid=${usd(r.paid)} adj=${usd(adj)} diff=${usd(lhs - rhs)}`); }
  for (const s of r.svc) {
    const ladj = s.cas.reduce((a, e) => { const v = c2(e.amount); return a + (Number.isNaN(v) ? 0 : Math.abs(v)); }, 0);
    const sc = c2(s.charged), sp = c2(s.paid);
    if (Number.isFinite(sc) && sc === (sp || 0) + ladj) lineBalOK++; else { lineBalBad++; if (lineBadSamples.length < 6) lineBadSamples.push(`${r.claimRaw} ${s.code} ch=${usd(sc)} paid=${usd(sp)} adj=${usd(ladj)}`); }
  }
}
out.moneyBalance = {
  claimLevelBalanced: balOK, claimLevelUnbalanced: balBad, claimLevelBadSamples: balBadSamples,
  lineLevelBalanced: lineBalOK, lineLevelUnbalanced: lineBalBad, lineLevelBadSamples: lineBadSamples,
  adjustmentsByGroup: { CO: usd(adjTotalCO), PR: usd(adjTotalPR), OA: usd(adjTotalOA), PI: usd(adjTotalPI) },
};

/* 4. BPR vs sum(paid) per file; reversals */
out.bprReconciliation = parsed.map(p => {
  let bpr = 0, paidSum = 0, negPaid = 0, posStatus22 = 0, status22Paid = 0;
  const txn = [];
  for (const t of p.tx) {
    let tb = 0, tp = 0;
    if (t.bpr) { const v = c2(t.bpr[2]); if (!Number.isNaN(v)) tb += v || 0; }
    for (const cl of t.claims) { const v = c2(cl.paid); if (!Number.isNaN(v)) tp += v || 0;
      if (v < 0) negPaid++;
      if (cl.status === '22') { posStatus22++; status22Paid += Number.isNaN(v) ? 0 : v; } }
    bpr += tb; paidSum += tp;
    txn.push({ st: t.st?.[1], n: t.st?.[2], payer: t.payer, payerId: t.payerId, checkDate: (t.headerDtm || []).find(d => d[1] === '405')?.[2], bpr: usd(tb), paidSum: usd(tp), diff: usd(tb - tp), claims: t.claims.length, denied: t.claims.filter(c => c.status === '4').length, reversed: t.claims.filter(c => c.status === '22').length });
  }
  return { file: p.file, bprTotal: usd(bpr), claimPaidTotal: usd(paidSum), diff: usd(bpr - paidSum), negativePaidSegments: negPaid, status22Count: posStatus22, status22PaidTotal: usd(status22Paid), transactions: txn, problems: p.problems };
});

/* 5. denial economics by payer */
const denialPolicies = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
const TODAY = Date.parse('2026-09-30');
const day = 86400000;
const denialRows = remitRows.filter(r => r.status === '4');
const revRows = remitRows.filter(r => r.status === '22');

// latest observation per claim (by file order then tx index) = deterministic
function latestPerClaim(rows) {
  const m = new Map();
  const order = files;
  for (const r of rows) {
    const key = r.claim; if (!key) continue;
    const rank = order.indexOf(r.file) * 1000 + (r.txIndex || 0);
    const prev = m.get(key);
    if (!prev || rank >= prev._rank) m.set(key, { ...r, _rank: rank });
  }
  return m;
}
const allByClaim = latestPerClaim(remitRows);

const denialByPayer = {};
const carcByPayer = {};
let deniedDollars = 0, deniedCount = 0;
const windowStats = {};
for (const r of denialRows) {
  const payer = r.payerId || 'UNKNOWN';
  denialByPayer[payer] = denialByPayer[payer] || { count: 0, charged: 0, paid: 0 };
  denialByPayer[payer].count++;
  denialByPayer[payer].charged += r.charged || 0;
  denialByPayer[payer].paid += r.paid || 0;
  deniedCount++; deniedDollars += r.charged || 0;
  for (const e of [...r.casClaim, ...r.svc.flatMap(s => s.cas)]) {
    const k = payer + '|' + e.group + '|' + e.carc;
    carcByPayer[k] = (carcByPayer[k] || 0) + 1;
  }
}
out.denials = {
  rawDenialRows: denialRows.length,
  distinctDeniedClaims: new Set(denialRows.map(r => r.claim).filter(Boolean)).size,
  chargedAtStake: usd(deniedDollars),
  byPayer: Object.fromEntries(Object.entries(denialByPayer).map(([k, v]) => [k, { count: v.count, charged: usd(v.charged), paidSoFar: usd(v.paid) }])),
  carcGroupByPayer: carcByPayer,
  reversals: { count: revRows.length, claims: [...new Set(revRows.map(r => r.claim))] },
};

/* 6. recoverability windows as of 2026-09-30 (per distinct denied claim, latest state) */
const recover = { byPayer: {}, totals: { recoverable: 0, lost: 0, unknown: 0 }, byCarc: {} };
const lostList = [], recoverList = [];
for (const [, r] of allByClaim) {
  if (r.status !== '4') continue;
  const pol = denialPolicies[r.payerId];
  const denialDate = r.checkDate ? Date.parse(fmtYmd(r.checkDate)) : NaN;
  if (!pol || !Number.isFinite(denialDate)) { recover.totals.unknown++; continue; }
  const deadline = denialDate + Number(pol.appeal_window_days_from_denial) * day;
  const daysLeft = Math.round((deadline - TODAY) / day);
  const bucket = daysLeft < 0 ? 'lost' : 'recoverable';
  recover.totals[bucket]++;
  recover.byPayer[r.payerId] = recover.byPayer[r.payerId] || { recoverable: 0, lost: 0, recoverableCharged: 0, lostCharged: 0 };
  recover.byPayer[r.payerId][bucket]++;
  recover.byPayer[r.payerId][bucket + 'Charged'] += r.charged || 0;
  const rec = { claim: r.claim, payer: r.payerId, denialDate: fmtYmd(r.checkDate), daysLeft, charged: usd(r.charged), file: r.file };
  (bucket === 'lost' ? lostList : recoverList).push(rec);
  for (const e of [...r.casClaim, ...r.svc.flatMap(s => s.cas)]) {
    const k = e.group + '|' + e.carc;
    recover.byCarc[k] = recover.byCarc[k] || { lost: 0, recoverable: 0, lostCharged: 0, recoverableCharged: 0 };
    recover.byCarc[k][bucket]++;
  }
}
recover.totals.recoverableCharged = usd(recoverList.reduce((a, x) => a + c2(x.charged), 0));
recover.totals.lostCharged = usd(lostList.reduce((a, x) => a + c2(x.charged), 0));
recover.mostUrgent = recoverList.sort((a, b) => a.daysLeft - b.daysLeft).slice(0, 10);
recover.lostSample = lostList.slice(0, 10);
out.recoverability = recover;
function fmtYmd(yyyymmdd) { return yyyymmdd.slice(0, 4) + '-' + yyyymmdd.slice(4, 6) + '-' + yyyymmdd.slice(6, 8); }

/* 7. denials never in worklog / worklog not denied, canonicalised */
const worklog = readCsv('worklog_extracted.csv');
const wlCanon = worklog.map(r => ({ ...r, c: canonId(r['Claim #']).canon, rawReason: canonId(r['Claim #']).reason }));
const deniedCanon = new Set(denialRows.map(r => r.claim).filter(Boolean));
const wlCanonSet = new Set(wlCanon.map(r => r.c).filter(Boolean));
out.worklogReconciliation = {
  rows: worklog.length,
  parseFailures: wlCanon.filter(r => !r.c && !r.rawReason).length,
  foreignIds: wlCanon.filter(r => r.rawReason && r.rawReason.startsWith('foreign')).length,
  unrecognised: wlCanon.filter(r => r.rawReason && r.rawReason.startsWith('unrecognised')).length,
  canonicalDistinct: wlCanonSet.size,
  canonicalNotFoundInExport: [...wlCanonSet].filter(k => !byCanon.has(k)).length,
  canonicalNotFoundInExportIds: [...wlCanonSet].filter(k => !byCanon.has(k)),
  worklogClaimsDeniedInRemit: [...wlCanonSet].filter(k => deniedCanon.has(k)).length,
  worklogClaimsNOTDeniedInRemit: [...wlCanonSet].filter(k => !deniedCanon.has(k)).length,
  worklogNotDeniedIds: [...wlCanonSet].filter(k => !deniedCanon.has(k)).slice(0, 15),
  deniedClaimsNeverLogged: [...deniedCanon].filter(k => !wlCanonSet.has(k)).length,
  deniedClaimsTotal: deniedCanon.size,
  statusVariants: tally(worklog, r => r.Status),
  ownerVariants: tally(worklog, r => r.Owner),
  dateFormats: tally(worklog, r => /^\d{4}-\d{2}-\d{2}$/.test(r['Date Logged']) ? 'ISO'
    : /^\d{2}\/\d{2}\/\d{4}$/.test(r['Date Logged']) ? 'slash' : 'other'),
  slashDateAmbiguous: worklog.filter(r => { const m = r['Date Logged'].match(/^(\d{2})\/(\d{2})\/\d{4}$/); return m && Number(m[1]) <= 12 && Number(m[2]) <= 12; }).length,
  amountFormats: tally(worklog, r => /^\$/.test(r.Amt) ? 'currency' : /\./.test(r.Amt) ? 'decimal' : 'integer'),
};

/* 8. labelled sample vs actual remit outcome */
const labeled = readCsv('labeled_denials_sample.csv');
out.labeledVsRemit = labeled.map(l => {
  const k = canonId(l.claim_id).canon;
  const rows = remitClaimCanon.get(k) || [];
  return { claim: k, label: l.root_cause_category, preventable: l.preventable_at_prebill,
    remitSeen: rows.length > 0, statuses: [...new Set(rows.map(r => r.status))].join(','),
    payer: rows[0]?.payerId || (byCanon.get(k)?.[0]?.payer_id ?? null),
    denied: rows.some(r => r.status === '4') };
}).reduce((acc, x) => { acc.total++; if (!x.remitSeen) acc.notInRemit++; else if (!x.denied) acc.seenButNotDenied++;
  else acc.denied++; if (!x.denied) (acc.notDeniedList = acc.notDeniedList || []).push(x); return acc; }, { total: 0, denied: 0, seenButNotDenied: 0, notInRemit: 0 });

/* 9. claims patterns: pre-bill vs denial */
const claimRows = [];
for (const [k, rows] of byCanon) {
  const rem = remitClaimCanon.get(k) || [];
  claimRows.push({ canon: k, payer: rows[0].payer_id, prebill: rows[0].prebill_reviewed,
    coder: rows[0].coder_id, facility: rows[0].facility, pos: rows[0].pos,
    charge: rows.reduce((a, r) => a + (c2(r.charge) || 0), 0),
    denied: rem.some(r => r.status === '4'), adjudicated: rem.length > 0,
    deniedCarcs: [...new Set(rem.filter(r => r.status === '4').flatMap(r => [...r.casClaim, ...r.svc.flatMap(s => s.cas)].map(e => e.carc)))] });
}
const rate = (label, sel) => {
  const g = claimRows.filter(sel);
  const d = g.filter(x => x.denied);
  return { label, claims: g.length, adjudicated: g.filter(x => x.adjudicated).length, denied: d.length,
    denialRatePct: g.length ? +(100 * d.length / g.length).toFixed(1) : null,
    charged: usd(g.reduce((a, x) => a + x.charge, 0)), deniedCharged: usd(d.reduce((a, x) => a + x.charge, 0)) };
};
out.claimPatterns = {
  overall: rate('all', () => true),
  byPrebill: ['Y', 'N'].map(v => rate('prebill=' + v, x => x.prebill === v)),
  byPayer: ['NS401', 'CSA77', 'SMP12', 'MRD55'].map(v => rate('payer=' + v, x => x.payer === v)),
  byPos: ['21', '31'].map(v => rate('pos=' + v, x => x.pos === v)),
  byCoder: [...new Set(claimRows.map(x => x.coder))].sort().map(v => rate('coder=' + v, x => x.coder === v)),
  byFacility: [...new Set(claimRows.map(x => x.facility))].sort().map(v => rate('facility=' + v, x => x.facility === v)),
  neverAdjudicated: claimRows.filter(x => !x.adjudicated).length,
  neverAdjudicatedCharged: usd(claimRows.filter(x => !x.adjudicated).reduce((a, x) => a + x.charge, 0)),
};

/* 10. per-CPT / modifier denial signals */
const cptAgg = {};
for (const [k, rows] of byCanon) {
  const rem = remitClaimCanon.get(k) || [];
  const denied = rem.some(r => r.status === '4');
  for (const r of rows) {
    const key = r.cpt + '|' + (r.modifier || '-');
    cptAgg[key] = cptAgg[key] || { claims: new Set(), denied: new Set() };
    cptAgg[key].claims.add(k); if (denied) cptAgg[key].denied.add(k);
  }
}
out.cptSignals = Object.entries(cptAgg).map(([k, v]) => ({ cptMod: k, claims: v.claims.size, denied: v.denied.size,
  denialRatePct: +(100 * v.denied.size / v.claims.size).toFixed(1) }))
  .filter(x => x.claims >= 10).sort((a, b) => b.denialRatePct - a.denialRatePct);

/* 11. auth / policy eligibility signals */
const smpAuth = claimRows.length;
const claimsNeedingAuth = claimRows.filter(x => x.payer === 'SMP12');
out.policySignals = {
  smpClaims: claimsNeedingAuth.length,
  i10WithI11: claims.filter(r => [r.dx1, r.dx2, r.dx3, r.dx4].some(d => d === 'I10') && [r.dx1, r.dx2, r.dx3, r.dx4].some(d => /^I11/.test(d))).length,
  distinctClaimsWithI10I11: new Set(claims.filter(r => [r.dx1, r.dx2, r.dx3, r.dx4].some(d => d === 'I10') && [r.dx1, r.dx2, r.dx3, r.dx4].some(d => /^I11/.test(d))).map(r => r.claim_id)).size,
  mod25Lines: claims.filter(r => r.modifier === '25').length,
  mod25DistinctClaims: new Set(claims.filter(r => r.modifier === '25').map(r => r.claim_id)).size,
};

process.stdout.write(JSON.stringify(out, null, 2));
