// Phase 0 profiler for the AQSoft Denials Command Center data pack.
// Read-only. Emits JSON to stdout. Prints aggregate stats only (no PHI).
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
const out = {};

function readCsv(file) {
  const text = fs.readFileSync(path.join(ROOT, file), 'utf8').replace(/^\uFEFF/, '');
  const rows = [];
  let i = 0, field = '', row = [], inQ = false;
  while (i < text.length) {
    const c = text[i];
    if (inQ) {
      if (c === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else inQ = false; }
      else field += c;
    } else if (c === '"') inQ = true;
    else if (c === ',') { row.push(field); field = ''; }
    else if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; }
    else if (c !== '\r') field += c;
    i++;
  }
  if (field !== '' || row.length) { row.push(field); rows.push(row); }
  const header = rows[0] || [];
  const objs = rows.slice(1)
    .filter(r => r.some(v => v !== ''))
    .map(r => Object.fromEntries(header.map((h, k) => [h, r[k] ?? ''])));
  return { header, objs };
}

const toCents = s => {
  if (s === null || s === undefined) return null;
  const t = String(s).trim().replace(/[$,\s]/g, '');
  if (t === '') return null;
  const neg = t.startsWith('-') || /^\(.*\)$/.test(t);
  const n = Number(t.replace(/[()]/g, ''));
  if (!Number.isFinite(n)) return NaN;
  return Math.round(n * 100) * (neg ? -1 : 1);
};
const usd = c => (c === null || c === undefined || Number.isNaN(c)) ? null
  : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
const tally = (arr, fn) => { const m = {}; for (const x of arr) { const k = fn(x); m[k] = (m[k] || 0) + 1; } return m; };

/* ------------------------------------------------------------------ CLAIMS */
const claimsCsv = readCsv('claims_export.csv');
const claims = claimsCsv.objs;
const claimIdSet = new Set(claims.map(r => r.claim_id));

out.claims = {
  dataRows: claims.length,
  header: claimsCsv.header,
  distinctClaimIds: claimIdSet.size,
  claimIdFormats: tally(claims, r => /^GPP-2026-\d{6}$/.test(r.claim_id) ? 'GPP-2026-######'
    : 'OTHER:' + r.claim_id),
  linesPerClaim: (() => {
    const c = {}; for (const r of claims) c[r.claim_id] = (c[r.claim_id] || 0) + 1;
    return tally(Object.values(c), v => String(v));
  })(),
  duplicateClaimLineKeys: (() => {
    const seen = new Set(); let n = 0;
    for (const r of claims) { const k = r.claim_id + '|' + r.line_no; if (seen.has(k)) n++; else seen.add(k); }
    return n;
  })(),
  fullyDuplicatedRows: (() => {
    const seen = new Set(); let n = 0;
    for (const r of claims) { const k = JSON.stringify(r); if (seen.has(k)) n++; else seen.add(k); }
    return n;
  })(),
  dateFormats: tally(claims.flatMap(r => [
    ['dos', r.dos], ['submitted_date', r.submitted_date], ['patient_dob', r.patient_dob],
  ].map(([f, d]) => f + '|' + (/^\d{4}-\d{2}-\d{2}$/.test(d) ? 'ISO' : /^\d{2}\/\d{2}\/\d{4}$/.test(d) ? 'US'
    : /^\d{8}$/.test(d) ? 'YYYYMMDD' : d === '' ? 'EMPTY' : 'OTHER:' + d))), x => x),
  blankCounts: tally(claimsCsv.header.flatMap(h => claims.filter(r => !String(r[h] ?? '').trim()).map(() => h)), x => x),
  pos: tally(claims, r => r.pos || 'EMPTY'),
  modifiers: tally(claims, r => r.modifier === '' ? '(blank)' : r.modifier),
  payerIdPairs: tally(claims, r => r.payer + ' / ' + r.payer_id),
  distinctCpts: new Set(claims.map(r => r.cpt)).size,
  prebillReviewed: tally(claims, r => r.prebill_reviewed || 'EMPTY'),
  coderIds: tally(claims, r => r.coder_id || 'EMPTY'),
  facilities: tally(claims, r => r.facility || 'EMPTY'),
  distinctProviders: new Set(claims.map(r => r.rendering_provider)).size,
  totalSubmittedCharge: (() => { let s = 0; for (const r of claims) { const c = toCents(r.charge); if (!Number.isNaN(c)) s += c || 0; } return usd(s); })(),
  unparseableChargeRows: claims.filter(r => Number.isNaN(toCents(r.charge))).length,
  dosRange: (() => { const d = claims.map(r => r.dos).filter(Boolean).sort(); return { min: d[0], max: d[d.length - 1] }; })(),
  submittedRange: (() => { const d = claims.map(r => r.submitted_date).filter(Boolean).sort(); return { min: d[0], max: d[d.length - 1] }; })(),
  submittedBeforeDos: claims.filter(r => Date.parse(r.submitted_date) < Date.parse(r.dos)).length,
  patientIdentity: (() => {
    const byPerson = new Map(), byMember = new Map();
    for (const r of claims) {
      const p = (r.patient_first + '|' + r.patient_last + '|' + r.patient_dob).toLowerCase();
      if (!byPerson.has(p)) byPerson.set(p, new Set());
      byPerson.get(p).add(r.member_id);
      if (!byMember.has(r.member_id)) byMember.set(r.member_id, new Set());
      byMember.get(r.member_id).add(p);
    }
    let multiMember = 0, maxMember = 0;
    for (const s of byPerson.values()) { if (s.size > 1) multiMember++; maxMember = Math.max(maxMember, s.size); }
    let sharedMember = 0;
    for (const s of byMember.values()) if (s.size > 1) sharedMember++;
    return {
      distinctNameDob: byPerson.size,
      personsWithMultipleMemberIds: multiMember,
      maxMemberIdsPerPerson: maxMember,
      memberIdsSharedAcrossPersons: sharedMember,
      distinctMemberIds: byMember.size,
    };
  })(),
  authByPayer: (() => {
    const m = {};
    for (const r of claims) {
      const p = r.payer_id;
      m[p] = m[p] || { blank: 0, filled: 0, samples: new Set() };
      if (String(r.auth_number).trim()) { m[p].filled++; if (m[p].samples.size < 3) m[p].samples.add(r.auth_number); }
      else m[p].blank++;
    }
    return Object.fromEntries(Object.entries(m).map(([k, v]) => [k, { blank: v.blank, filled: v.filled, samples: [...v.samples] }]));
  })(),
  dxPairI10I11: claims.filter(r => {
    const dx = [r.dx1, r.dx2, r.dx3, r.dx4].filter(Boolean);
    return dx.some(d => d === 'I10') && dx.some(d => /^I11/.test(d));
  }).length,
};

/* ------------------------------------------------------------------ 835s */
function parse835(file) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1')
    .replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const tx = [];
  const problems = [];
  let cur = null, clm = null, svc = null;
  const segments = raw.split('~').map(s => s.replace(/\n/g, '')).filter(s => s.length);
  for (const seg of segments) {
    const p = seg.split('*');
    const tag = p[0];
    if (tag === 'ST') { cur = { stControl: p[1], stNumber: p[2], claims: [], bpr: null, trn: null, payer: null, payee: null, plb: [], headerDtm: [], lx: 0 }; clm = null; svc = null; continue; }
    if (tag === 'SE') {
      const declared = Number(p[2]);
      const actual = 0; // counted separately later
      tx.push(cur); cur = null; clm = null; svc = null; continue;
    }
    if (!cur) { problems.push('segment_outside_transaction:' + tag); continue; }
    switch (tag) {
      case 'BPR': cur.bpr = p; break;
      case 'TRN': cur.trn = p; break;
      case 'N1': if (p[1] === 'PR') cur.payer = p; else if (p[1] === 'PE') cur.payee = p; break;
      case 'LX': cur.lx++; break;
      case 'PLB': cur.plb.push(p); break;
      case 'CLP': {
        clm = { num: p[1], status: p[2], charged: p[3], paid: p[4], patientResp: p[5], filingInd: p[6],
          payerClaimCtrl: p[7], facility: p[8], frequency: p[9], svc: [], casClaim: [], lqClaim: [], dtm: [], nm1: [], ref: [], moa: [], amtClaim: [] };
        cur.claims.push(clm); svc = null; break;
      }
      case 'SVC': { svc = { code: p[1], charged: p[2], paid: p[3], cas: [], lq: [], dtm: [], amt: [] }; clm.svc.push(svc); break; }
      case 'CAS': {
        const grp = p[1];
        for (let k = 2; k + 1 < p.length; k += 2) {
          const e = { group: grp, carc: p[k], amount: p[k + 1] };
          if (!clm) { problems.push('CAS_without_CLP'); continue; }
          (svc ? svc.cas : clm.casClaim).push(e);
        }
        break;
      }
      case 'LQ': if (clm) (svc ? svc.lq : clm.lqClaim).push(p); break;
      case 'MOA': if (clm) clm.moa.push(p); break;
      case 'AMT': if (clm) (svc ? svc.amt : clm.amtClaim).push(p); break;
      case 'DTM': if (clm) clm.dtm.push(p); else cur.headerDtm.push(p); break;
      case 'NM1': if (clm) clm.nm1.push(p); break;
      case 'REF': if (clm) clm.ref.push(p); break;
      default: break;
    }
  }
  if (cur) problems.push('unterminated_ST_at_EOF');
  return { file, tx, problems, segmentCount: segments.length };
}

const remitFiles = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const parsed = Object.fromEntries(remitFiles.map(f => [f, parse835(f)]));

out.remits = {};
const byClaimNumber = new Map();
const refCsv = readCsv('carc_rarc_reference.csv');
const refCarc = new Set(refCsv.objs.filter(r => r.type === 'CARC').map(r => r.code));
const refRarc = new Set(refCsv.objs.filter(r => r.type.toUpperCase() === 'RARC').map(r => r.code));
const usedCarc = new Set(), usedRarc = new Set();

for (const [file, r] of Object.entries(parsed)) {
  let bprTotal = 0, claimCharged = 0, claimPaid = 0, svcCharged = 0, svcPaid = 0;
  let claimSvcChargeMismatch = 0, claimSvcPaidMismatch = 0;
  let casGroupsPerLevel = 0, svcWithoutCas = 0, svcWithoutAmtB6 = 0;
  const statuses = {}, groups = {}, carcC = {}, rarcC = {};
  const claimNums = [];
  let casItemCount = 0;
  for (const t of r.tx) {
    if (t.bpr) { const v = toCents(t.bpr[2]); if (!Number.isNaN(v)) bprTotal += v || 0; }
    for (const c of t.claims) {
      claimNums.push(c.num);
      statuses[c.status] = (statuses[c.status] || 0) + 1;
      const ch = toCents(c.charged), pa = toCents(c.paid);
      if (!Number.isNaN(ch)) claimCharged += ch || 0;
      if (!Number.isNaN(pa)) claimPaid += pa || 0;
      const arr = byClaimNumber.get(c.num) || [];
      arr.push({ file, status: c.status, charged: ch, paid: pa, billedOn: t.bpr ? t.bpr[16] : null, checkDate: t.bpr ? t.bpr[16] : null, txIndex: r.tx.indexOf(t) });
      byClaimNumber.set(c.num, arr);
      let sch = 0, spa = 0;
      for (const ce of c.casClaim) { groups[ce.group] = (groups[ce.group] || 0) + 1; carcC[ce.carc] = (carcC[ce.carc] || 0) + 1; casItemCount++; usedCarc.add(ce.carc); casGroupsPerLevel++; }
      for (const l of c.lqClaim) { const code = (l[2] || '').trim(); if (code) { rarcC[code] = (rarcC[code] || 0) + 1; usedRarc.add(code); } }
      if (c.svc.length === 0) svcWithoutCas++;
      for (const s of c.svc) {
        const sh = toCents(s.charged), sp = toCents(s.paid);
        if (!Number.isNaN(sh)) { svcCharged += sh; sch += sh; }
        if (!Number.isNaN(sp)) { svcPaid += sp; spa += sp; }
        if (s.cas.length === 0 && (c.status === '4' || c.status === '22')) svcWithoutCas++;
        let hasB6 = false;
        for (const ce of s.cas) { groups[ce.group] = (groups[ce.group] || 0) + 1; carcC[ce.carc] = (carcC[ce.carc] || 0) + 1; casItemCount++; usedCarc.add(ce.carc); if (ce.group === 'PR') hasB6 = true; }
        for (const l of s.lq) { const code = (l[2] || '').trim(); if (code) { rarcC[code] = (rarcC[code] || 0) + 1; usedRarc.add(code); } }
        if (!hasB6 && c.status === '1') svcWithoutAmtB6++;
      }
      if (Number.isFinite(ch) && ch !== sch) claimSvcChargeMismatch++;
      if (Number.isFinite(pa) && spa !== pa) claimSvcPaidMismatch++;
    }
  }
  out.remits[file] = {
    transactions: r.tx.length,
    claims: r.tx.reduce((a, t) => a + t.claims.length, 0),
    svcLines: r.tx.reduce((a, t) => a + t.claims.reduce((b, c) => b + c.svc.length, 0), 0),
    segmentCount: r.segmentCount,
    parseProblems: r.problems,
    claimStatusCounts: statuses,
    groupCodeCounts: groups,
    carcCounts: carcC,
    rarcCounts: rarcC,
    claimLevelCasItemCount: casItemCount,
    bprTotal: usd(bprTotal),
    claimChargedTotal: usd(claimCharged),
    claimPaidTotal: usd(claimPaid),
    svcChargedTotal: usd(svcCharged),
    svcPaidTotal: usd(svcPaid),
    claimVsSvcChargeMismatchCount: claimSvcChargeMismatch,
    claimVsSvcPaidMismatchCount: claimSvcPaidMismatch,
    deniedClaimsWithoutSvcCas: svcWithoutCas,
    claimNumberFormats: tally(claimNums, n => /^\d{6}$/.test(n) ? 'sixDigit' : /^GPP\d{10,}$/.test(n) ? 'GPP+digits' : 'other:' + n),
    sampleClaimNumbers: claimNums.slice(0, 3),
    hasPlb: r.tx.some(t => t.plb.length > 0),
    plbSegments: r.tx.flatMap(t => t.plb).length,
    checkDates: [...new Set(r.tx.map(t => t.bpr ? t.bpr[16] : null).filter(Boolean))],
  };
}

/* cross-file duplicate claim numbers */
const dupClaims = [];
for (const [num, arr] of byClaimNumber) {
  const files = [...new Set(arr.map(a => a.file))];
  if (files.length > 1) dupClaims.push({ num, files: files.length, statuses: [...new Set(arr.map(a => a.status))] });
}
out.remitsCrossFile = {
  claimNumbersInMultipleFiles: dupClaims.length,
  distinctClaimNumbersInRemits: byClaimNumber.size,
  filesPerClaimNumber: tally([...byClaimNumber.values()], a => String(new Set(a.map(x => x.file)).size)),
  multiplicity: tally([...byClaimNumber.values()], a => String(a.length)),
  statusChangesAcrossFiles: dupClaims.filter(d => d.statuses.length > 1).length,
  sample: dupClaims.slice(0, 5),
};

/* Q2 vs Q2_resent deep compare */
const strip = f => parsed[f].tx.map(t => ({
  claims: t.claims.map(c => ({ num: c.num, status: c.status, charged: c.charged, paid: c.paid, svc: c.svc.map(s => s.charged + '|' + s.paid), cas: [...c.casClaim, ...c.svc.flatMap(s => s.cas)].map(e => e.group + e.carc + e.amount) })),
}));
const a = JSON.stringify(strip('era_2026Q2.835'));
const b = JSON.stringify(strip('era_2026Q2_resent_0719.835'));
out.q2VsResent = { identicalFinancialContent: a === b, lenA: a.length, lenB: b.length };

/* CARC/RARC coverage */
out.reference = {
  rows: refCsv.objs.length,
  carcInRef: [...refCarc].sort(),
  rarcInRef: [...refRarc].sort(),
  carcUsedNotInRef: [...usedCarc].filter(c => !refCarc.has(c)).sort(),
  rarcUsedNotInRef: [...usedRarc].filter(c => !refRarc.has(c)).sort(),
  carcInRefNeverUsed: [...refCarc].filter(c => !usedCarc.has(c)).sort(),
};

/* --------------------------------------------------------- MATCHING TO CLAIMS */
const normalizedRemit = new Set();
for (const [num] of byClaimNumber) {
  normalizedRemit.add(num);
  const m = num.match(/^(\d+)$/);
  if (m) {
    const padded = 'GPP-2026-' + num.padStart(6, '0');
    normalizedRemit.add(padded);
    normalizedRemit.add('GPP' + num.padStart(6, '0'));
  } else {
    const digits = num.replace(/\D/g, '');
    if (digits) { normalizedRemit.add('GPP-2026-' + digits.slice(-6).padStart(6, '0')); normalizedRemit.add(digits); }
  }
}
let matched = 0, unmatched = 0;
const unmatchedSamples = [];
for (const id of claimIdSet) {
  if (normalizedRemit.has(id)) matched++;
  else { unmatched++; if (unmatchedSamples.length < 5) unmatchedSamples.push(id); }
}
out.matching = {
  claimIdsInExport: claimIdSet.size,
  matchedToSomeRemitClaimNumber: matched,
  notInAnyRemit: unmatched,
  notInRemitSamples: unmatchedSamples,
  remitClaimNumbersNotMatchingAnyExportId: [...byClaimNumber.keys()].filter(k => !claimIdSet.has(k)).length,
};

/* ------------------------------------------------------------------ WORKLOG */
const worklog = readCsv('worklog_extracted.csv').objs;
out.worklog = {
  rows: worklog.length,
  header: readCsv('worklog_extracted.csv').header,
  dateFormats: tally(worklog, r => /^\d{4}-\d{2}-\d{2}$/.test(r['Date Logged']) ? 'ISO'
    : /^\d{2}\/\d{2}\/\d{4}$/.test(r['Date Logged']) ? 'US-or-EU-slash' : 'other:' + r['Date Logged']),
  ambiguousDayFirstDates: worklog.filter(r => /^\d{2}\/\d{2}\/\d{4}$/.test(r['Date Logged'])).length,
  amountFormats: tally(worklog, r => /^\$[\d,]+\.\d{2}$/.test(r.Amt) ? 'currency'
    : /^\d+\.\d{2}$/.test(r.Amt) ? 'plainDec' : /^\d+$/.test(r.Amt) ? 'integer' : 'other:' + r.Amt),
  statuses: tally(worklog, r => JSON.stringify(r.Status)),
  owners: tally(worklog, r => JSON.stringify(r.Owner)),
  claimIdFormats: tally(worklog, r => /^GPP-2026-\d{6}$/.test(r['Claim #'].trim()) ? 'clean' : 'dirty:' + JSON.stringify(r['Claim #'])),
  claimIdsWithWhitespace: worklog.filter(r => r['Claim #'] !== r['Claim #'].trim()).length,
  notesWithQuestionMarks: worklog.filter(r => /\?/.test(r.Notes)).length,
  distinctClaimIds: new Set(worklog.map(r => r['Claim #'].trim())).size,
  duplicateClaimIds: (() => {
    const c = tally(worklog, r => r['Claim #'].trim());
    return Object.entries(c).filter(([, n]) => n > 1).length;
  })(),
  claimIdsNotInExport: (() => {
    const ids = [...new Set(worklog.map(r => r['Claim #'].trim()))];
    return ids.filter(id => !claimIdSet.has(id));
  })(),
  claimIdsNotInExportCount: (() => {
    const ids = [...new Set(worklog.map(r => r['Claim #'].trim()))];
    return ids.filter(id => !claimIdSet.has(id)).length;
  })(),
  claimIdsNotInRemit: (() => {
    const ids = [...new Set(worklog.map(r => r['Claim #'].trim()))];
    return ids.filter(id => ![...byClaimNumber.keys()].some(k => k === id || k === id.replace(/\D/g, '').replace(/^(\d{6})$/, '$1') || k.padStart(6, '0') === id.slice(-6))).length;
  })(),
};

/* ------------------------------------------- WORKLOG vs REMIT DENIAL STATUS */
const deniedByClaim = new Map();
for (const [num, arr] of byClaimNumber) {
  const norm = /^\d{6}$/.test(num) ? 'GPP-2026-' + num : num.replace(/^GPP(\d{10,})$/, 'GPP-2026-' + num.replace(/\D/g, '').slice(-6));
  const isDenied = arr.some(x => x.status === '4');
  if (isDenied) deniedByClaim.set(norm, arr);
}
let wlNotDenied = 0, wlDenied = 0;
const wlNotDeniedSamples = [];
for (const r of worklog) {
  const id = r['Claim #'].trim();
  if (deniedByClaim.has(id)) wlDenied++;
  else { wlNotDenied++; if (wlNotDeniedSamples.length < 8) wlNotDeniedSamples.push(id + ' :: ' + r.Status.trim()); }
}
out.worklogVsRemit = {
  worklogEntriesWhoseClaimIsDeniedInRemit: wlDenied,
  worklogEntriesWhoseClaimIsNOTDeniedInRemit: wlNotDenied,
  notDeniedSamples: wlNotDeniedSamples,
  deniedClaimsNeverLogged: (() => {
    const logged = new Set(worklog.map(r => r['Claim #'].trim()));
    let n = 0; for (const id of deniedByClaim.keys()) if (!logged.has(id)) n++;
    return n;
  })(),
  totalDeniedClaimNumbers: deniedByClaim.size,
};

/* ------------------------------------------------------------ PAYER RULES */
out.payerRules = readCsv('payer_rules.csv').objs;
out.labeled = (() => {
  const l = readCsv('labeled_denials_sample.csv');
  return {
    rows: l.objs.length,
    header: l.header,
    categories: tally(l.objs, r => r.root_cause_category),
    teams: tally(l.objs, r => r.owning_team),
    preventable: tally(l.objs, r => r.preventable_at_prebill),
    presentInClaimsExport: l.objs.filter(r => claimIdSet.has(r.claim_id)).length,
    missingFromClaimsExport: l.objs.filter(r => !claimIdSet.has(r.claim_id)).map(r => r.claim_id),
    presentInRemitAsDenied: l.objs.filter(r => deniedByClaim.has(r.claim_id)).length,
  };
})();

process.stdout.write(JSON.stringify(out, null, 2));
