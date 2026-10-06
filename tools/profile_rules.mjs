// Phase 0 pass 3: re-adjudication history, policy-rule verification, edge cases.
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
  return rows.slice(1).filter(r => r.some(v => v !== '')).map(r => Object.fromEntries(h.map((k, i2) => [k, r[i2] ?? ''])));
}
const c2 = s => { if (s === null || s === undefined) return null;
  const t = String(s).trim().replace(/[$,\s]/g, ''); if (t === '') return null;
  if (/^\(.*\)$/.test(t)) return -Math.round(Number(t.slice(1, -1)) * 100);
  const n = Number(t); return Number.isFinite(n) ? Math.round(n * 100) : NaN; };
const usd = c => c === null || c === undefined || Number.isNaN(c) ? null : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
const tally = (a, fn) => { const m = {}; for (const x of a) { const k = fn(x); m[k] = (m[k] || 0) + 1; } return m; };

function canonId(raw) {
  const s = String(raw ?? '').trim();
  if (!s) return null;
  let m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i);
  if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s;
  return null;
}
function parse835(file) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').replace(/\r\n?/g, '\n');
  const tx = []; let t = null, cl = null, sv = null;
  for (const seg of raw.split('~').map(s => s.replace(/\n/g, '')).filter(Boolean)) {
    const p = seg.split('*'), tag = p[0];
    if (tag === 'ISA') { t = { claims: [] }; continue; }
    if (tag === 'GS') { t = { claims: [] }; continue; }
    if (tag === 'GE') { t = null; continue; }
    if (tag === 'ST') { if (!t) t = { claims: [] }; t.st = p; t.claims = []; t.headerDtm = []; cl = null; sv = null; continue; }
    if (tag === 'SE') { if (t && t.st) tx.push(t); t = null; cl = null; sv = null; continue; }
    if (!t) continue;
    switch (tag) {
      case 'BPR': t.bpr = p; break;
      case 'TRN': t.trn = p; break;
      case 'N1': if (p[1] === 'PR') t.payer = p[2]; break;
      case 'REF': if (p[1] === '2U') t.payerId = p[2]; break;
      case 'DTM': if (!cl) t.headerDtm.push(p); break;
      case 'CLP': cl = { num: p[1], status: p[2], charged: p[3], paid: p[4], svc: [], casClaim: [], lqClaim: [] }; t.claims.push(cl); sv = null; break;
      case 'SVC': sv = { code: p[1], charged: p[2], paid: p[3], cas: [], lq: [] }; cl.svc.push(sv); break;
      case 'CAS': { const g = p[1]; for (let k = 2; k + 1 < p.length; k += 2) (sv ? sv.cas : cl.casClaim).push({ group: g, carc: p[k], amount: p[k + 1] }); break; }
      case 'LQ': (sv ? sv.lq : cl.lqClaim).push(p); break;
      default: break;
    }
    if (tag === 'DTM' && cl) { /* claim-level DTM ignored */ }
  }
  return { file, tx };
}
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const parsed = files.map(parse835);

// Build a chronological ledger of every CLP observation.
const obs = [];
for (const p of parsed) for (const ti of p.tx.keys()) { const t = p.tx[ti];
  const check = (t.headerDtm || []).find(d => d[1] === '405')?.[2] || t.bpr?.[16] || '00000000';
  for (const cl of t.claims) {
    const canon = canonId(cl.num);
    obs.push({ file: p.file, fi: files.indexOf(p.file), ti, check, payerId: t.payerId, payer: t.payer,
      raw: cl.num, claim: canon, status: cl.status, charged: c2(cl.charged), paid: c2(cl.paid),
      carcs: [...cl.casClaim, ...cl.svc.flatMap(s => s.cas)].map(e => e.carc),
      groups: [...cl.casClaim, ...cl.svc.flatMap(s => s.cas)].map(e => e.group),
      rarcs: [...cl.lqClaim, ...cl.svc.flatMap(s => s.lq)].map(l => (l[2] || '').trim()).filter(Boolean),
      svcCodes: cl.svc.map(s => s.code) });
  }
}

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canonId(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }
const line1 = k => byCanon.get(k)?.[0] || {};

const out = {};

/* A. every claim's status history (chronological) */
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti));

const patterns = tally([...hist.values()], a => a.map(o => o.status).join('>'));
out.statusHistories = {
  distinctClaims: hist.size,
  patterns,
  multiObservationClaims: [...hist.values()].filter(a => a.length > 1).length,
  claimsDeniedThenPaid: [...hist.entries()].filter(([, a]) => a.some(o => o.status === '4') && a[a.length - 1].status === '1').map(([k, a]) => ({ claim: k, hist: a.map(o => `${o.check}/${o.status}/${o.file}`) })),
  claimsPaidThenReversed: [...hist.entries()].filter(([, a]) => a.some(o => o.status === '22')).map(([k, a]) => ({ claim: k, hist: a.map(o => `${o.check}/${o.status}/${o.file}`), carcs: a[a.length - 1].carcs, rarcs: a[a.length - 1].rarcs, payer: a[0].payerId })),
};

/* B. open denials = latest observation has status 4 */
const latest = new Map();
for (const [k, a] of hist) latest.set(k, a[a.length - 1]);
const openDenied = [...latest.entries()].filter(([, o]) => o.status === '4');
out.openDenials = {
  count: openDenied.length,
  chargedPerClaimExport: usd(openDenied.reduce((s, [k]) => s + (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0), 0)),
  chargedPerRemitCLP03: usd(openDenied.reduce((s, [, o]) => s + (o.charged || 0), 0)),
  byPayer: Object.entries(tally(openDenied, ([k, o]) => o.payerId)).map(([payer, n]) => ({ payer, n })),
  byCarc: Object.entries(tally(openDenied.flatMap(([, o]) => [...new Set(o.carcs)]), x => x)).sort((a, b) => b[1] - a[1]),
  currentStatusOfAllClaims: Object.entries(tally([...latest.values()], o => o.status)),
};

/* C. charge agreement: claim export vs remit CLP03 */
let agree = 0, disagree = 0; const dis = [];
for (const [k, o] of latest) {
  const exp = (byCanon.get(k) || []).reduce((a, r) => a + (c2(r.charge) || 0), 0);
  if (exp === (o.charged || 0)) agree++; else { disagree++; if (dis.length < 8) dis.push(`${k} export=${usd(exp)} remit=${usd(o.charged)} payer=${o.payerId}`); }
}
out.chargeAgreement = { agree, disagree, samples: dis };

/* D. timely filing: submitted_date + TF window vs DOS */
const rules = Object.fromEntries(readCsv('payer_rules.csv').map(r => [r.payer_id, r]));
let tfViolations = 0, tfAtRisk = 0; const tfDetail = [];
for (const [k, rows] of byCanon) {
  const r0 = rows[0]; const rule = rules[r0.payer_id]; if (!rule) continue;
  const tf = Number(rule.timely_filing_days_from_dos);
  const deadline = Date.parse(r0.dos) + tf * 86400000;
  const sub = Date.parse(r0.submitted_date);
  const daysToDeadline = Math.round((deadline - sub) / 86400000);
  if (sub > deadline) { tfViolations++; tfDetail.push({ claim: k, payer: r0.payer_id, dos: r0.dos, submitted: r0.submitted_date, tfDays: tf, daysLate: -daysToDeadline }); }
  else if (daysToDeadline <= 15) tfAtRisk++;
}
out.timelyFiling = {
  claimsSubmittedAfterDeadline: tfViolations,
  claimsSubmittedWithin15DaysOfDeadline: tfAtRisk,
  detail: tfDetail.slice(0, 12),
  byPayer: Object.entries(tally(tfDetail, d => d.payer)),
  carc29DeniedClaims: [...latest.entries()].filter(([, o]) => o.carcs.includes('29')).map(([k, o]) => ({ claim: k, payer: o.payerId, check: o.check })),
};

/* E. SMP SNF authorization policy (effective DOS >= 2026-04-01, CPT 99304/5/6) */
const smp = [...byCanon.entries()].filter(([, rows]) => rows[0].payer_id === 'SMP12');
const inScope = smp.filter(([, rows]) => rows.some(r => ['99304', '99305', '99306'].includes(r.cpt)) && rows[0].dos >= '2026-04-01');
const inScopeNoAuth = inScope.filter(([, rows]) => !rows.some(r => String(r.auth_number).trim()));
const denied197 = [...latest.entries()].filter(([, o]) => o.payerId === 'SMP12' && o.carcs.includes('197'));
out.smpAuthPolicy = {
  smpClaimsWithInitialSnfCptOnOrAfter20260401: inScope.length,
  ofWhichMissingAuthNumber: inScopeNoAuth.length,
  deniedWithCarc197: denied197.length,
  denied197Claims: denied197.map(([k, o]) => { const r = line1(k); return { claim: k, dos: r.dos, cpt: (byCanon.get(k) || []).map(x => x.cpt).join('+'), authBlank: !String(r.auth_number || '').trim(), check: o.check }; }),
  claimsDenied197BeforeEffectiveDate: denied197.filter(([k]) => line1(k).dos < '2026-04-01').map(([k]) => ({ claim: k, dos: line1(k).dos })),
  smpAuthFilledAtAll: smp.filter(([, rows]) => rows.some(r => String(r.auth_number).trim())).length,
};

/* F. Northstar hospital frequency policy (99231-99233, same patient, same DOS) */
const ns = [...byCanon.entries()].filter(([, rows]) => rows[0].payer_id === 'NS401');
const byPatientDos = new Map();
for (const [k, rows] of ns) {
  const r0 = rows[0];
  const key = (r0.patient_first + '|' + r0.patient_last + '|' + r0.patient_dob + '|' + r0.dos).toLowerCase();
  const a = byPatientDos.get(key) || []; a.push({ claim: k, cpts: rows.map(r => r.cpt), npi: r0.rendering_npi, mods: rows.map(r => r.modifier) });
  byPatientDos.set(key, a);
}
const freqGroups = [...byPatientDos.values()].filter(a => a.length > 1 && a.some(x => x.cpts.some(c => ['99231', '99232', '99233'].includes(c))));
const denied151 = [...latest.entries()].filter(([, o]) => o.payerId === 'NS401' && o.carcs.includes('151'));
out.northstarFreqPolicy = {
  samePatientSameDosGroupsWithSubsequentHospitalCare: freqGroups.length,
  groupsDetail: freqGroups.slice(0, 8).map(g => g.map(x => ({ claim: x.claim, cpts: x.cpts.join('+'), sameNpi: new Set(g.map(y => y.npi)).size === 1 }))),
  deniedWithCarc151: denied151.length,
  denied151Detail: denied151.map(([k, o]) => ({ claim: k, check: o.check, dos: line1(k).dos, rarc: o.rarcs })),
};

/* G. MPPO Excludes1 (I10 with I11.-) */
const excl = [...byCanon.entries()].filter(([, rows]) => {
  const dx = rows.flatMap(r => [r.dx1, r.dx2, r.dx3, r.dx4]).filter(Boolean);
  return dx.includes('I10') && dx.some(d => /^I11/.test(d));
});
const denied11 = [...latest.entries()].filter(([, o]) => o.carcs.includes('11'));
out.excludes1Policy = {
  claimsReportingI10WithI11: excl.length,
  byPayer: Object.entries(tally(excl, ([k, rows]) => rows[0].payer_id)),
  deniedWithCarc11: denied11.length,
  denied11ByPayer: Object.entries(tally(denied11, ([k, o]) => o.payerId)),
  denied11Claims: denied11.map(([k, o]) => { const dx = (byCanon.get(k) || []).flatMap(r => [r.dx1, r.dx2, r.dx3, r.dx4]).filter(Boolean);
    return { claim: k, payer: o.payerId, hasI10: dx.includes('I10'), hasI11: dx.some(d => /^I11/.test(d)), rarc: o.rarcs }; }),
};

/* H. Modifier 25 / CARC 97 */
const mod25 = [...byCanon.entries()].filter(([, rows]) => rows.some(r => r.modifier === '25'));
const denied97 = [...latest.entries()].filter(([, o]) => o.carcs.includes('97'));
out.mod25Policy = {
  claimsCarryingMod25: mod25.length,
  byPayer: Object.entries(tally(mod25, ([k, rows]) => rows[0].payer_id)),
  deniedWithCarc97: denied97.length,
  denied97Detail: denied97.map(([k, o]) => ({ claim: k, payer: o.payerId, hasMod25: (byCanon.get(k) || []).some(r => r.modifier === '25'), rarc: o.rarcs, cpts: (byCanon.get(k) || []).map(r => r.cpt + (r.modifier ? '-' + r.modifier : '')).join('+') })),
  mod25ClaimsNeverDenied: mod25.filter(([k]) => latest.get(k)?.status !== '4').length,
};

/* I. CSA provider enrollment (CARC B7 / RARC N570) */
const deniedB7 = [...latest.entries()].filter(([, o]) => o.carcs.includes('B7'));
out.csaEnrollment = {
  deniedWithCarcB7: deniedB7.length,
  byPayer: Object.entries(tally(deniedB7, ([k, o]) => o.payerId)),
  distinctNpis: [...new Set(deniedB7.map(([k]) => line1(k).rendering_npi))],
  distinctProviders: [...new Set(deniedB7.map(([k]) => line1(k).rendering_provider))],
  dateRange: (() => { const d = deniedB7.map(([k]) => line1(k).dos).sort(); return { min: d[0], max: d[d.length - 1] }; })(),
  otherDenialsBySameNpiElsewhere: (() => {
    const npis = new Set(deniedB7.map(([k]) => line1(k).rendering_npi));
    let otherDenied = 0, otherTotal = 0;
    for (const [k, rows] of byCanon) { const r0 = rows[0]; if (!npis.has(r0.rendering_npi)) continue;
      const st = latest.get(k)?.status; otherTotal++; if (st === '4') otherDenied++; }
    return { claimsByThoseNpis: otherTotal, denied: otherDenied };
  })(),
};

/* J. never-adjudicated claims */
const never = [...byCanon.keys()].filter(k => !hist.has(k));
out.neverAdjudicated = {
  count: never.length,
  charged: usd(never.reduce((s, k) => s + byCanon.get(k).reduce((a, r) => a + (c2(r.charge) || 0), 0), 0)),
  submittedRange: (() => { const d = never.map(k => line1(k).submitted_date).sort(); return { min: d[0], max: d[d.length - 1] }; })(),
  dosRange: (() => { const d = never.map(k => line1(k).dos).sort(); return { min: d[0], max: d[d.length - 1] }; })(),
  byPayer: Object.entries(tally(never, k => line1(k).payer_id)),
  submittedAfter20260901: never.filter(k => line1(k).submitted_date > '2026-09-01').length,
  submittedBefore20260801: never.filter(k => line1(k).submitted_date < '2026-08-01').length,
};

/* K. worklog vs actual latest status */
const wl = readCsv('worklog_extracted.csv').map(r => ({ ...r, c: canonId(r['Claim #']), raw: r['Claim #'].trim() }));
const deniedSet = new Set([...latest.entries()].filter(([, o]) => o.status === '4').map(([k]) => k));
const wlC = new Set(wl.map(r => r.c).filter(Boolean));
const wlCross = { notDenied: [], deniedNotLogged: [] };
for (const k of wlC) if (!deniedSet.has(k)) wlCross.notDenied.push({ claim: k, wlStatus: wl.find(r => r.c === k)?.Status, remitStatus: latest.get(k)?.status || 'ABSENT' });
for (const k of deniedSet) if (!wlC.has(k)) wlCross.deniedNotLogged.push(k);
out.worklogCrossCheck = {
  worklogDistinct: wlC.size,
  worklogEntriesWhoseLatestRemitStatusIsNotDenied: wlCross.notDenied.length,
  notDeniedDetail: wlCross.notDenied.slice(0, 25),
  deniedNeverLogged: wlCross.deniedNotLogged.length,
  worklogStatusVsOpen: {
    worklogSaysOpenButNotDenied: wlCross.notDenied.filter(x => /open|wip|progress|pending/i.test(x.wlStatus || '')).length,
    worklogSaysClosedButDenied: [...wlC].filter(k => deniedSet.has(k) && /closed|resolved|done/i.test(wl.find(r => r.c === k)?.Status || '')).length,
  },
};

/* L. labelled sample full cross-check */
const lab = readCsv('labeled_denials_sample.csv');
out.labeledCrossCheck = lab.map(l => {
  const k = canonId(l.claim_id);
  const o = latest.get(k);
  const wlRow = wl.find(r => r.c === k);
  return { claim: k, label: l.root_cause_category, team: l.owning_team, preventable: l.preventable_at_prebill,
    latestStatus: o?.status || 'ABSENT', carcs: o ? [...new Set(o.carcs)].join('/') : '',
    rarc: o ? [...new Set(o.rarcs)].join('/') : '', payer: o?.payerId || line1(k).payer_id,
    inWorklog: !!wlRow, worklogStatus: wlRow?.Status ?? null,
    prebill: line1(k).prebill_reviewed, cpt: (byCanon.get(k) || []).map(r => r.cpt).join('+'),
    mod: [...new Set((byCanon.get(k) || []).map(r => r.modifier).filter(Boolean))].join('+') };
});

process.stdout.write(JSON.stringify(out, null, 2));
