// Q4: the worklog's ONLY date column is "Date Logged". Canonical appeal/corrected-claim
// windows anchor on DTM*405 (835 check date) per A1, so this answers:
//   of the 28 ambiguous rows, how many could influence ANY deadline?
// A row can influence a deadline only if no DTM*405 exists for that claim (i.e. never
// adjudicated) AND some downstream calculation falls back to Date Logged.
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
  const h = (rows[0] || []).map(s => s.replace(/^"|"$/g, ''));
  return rows.slice(1).filter(r => r.some(v => v !== ''))
    .map(r => Object.fromEntries(h.map((k, j) => [k, (r[j] ?? '').replace(/^"|"$/g, '')])));
}
const canon = raw => { const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i); if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s; return null; };

/* --- which claims have a DTM*405 (canonical deadline anchor)? ------------ */
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const anchored = new Set();               // claims with a check date
const seenRaw = new Set();
for (const file of files) {
  const segs = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').split('~')
    .map(s => s.replace(/\n/g, '')).filter(Boolean);
  let ck = null, pending = [];
  for (const g of segs) {
    const p = g.split('*');
    if (p[0] === 'DTM' && p[1] === '405') { ck = p[2]; pending = []; }
    else if (p[0] === 'CLP') {
      const c = canon(p[1]);
      if (c) { seenRaw.add(p[1]); if (ck) anchored.add(c); }
      pending.push(c);
    }
  }
}

/* --- ambiguous Date Logged ---------------------------------------------- */
const wl = readCsv('worklog_extracted.csv');
const isAmbiguous = s => {
  const m = String(s).trim().match(/^(\d{1,2})[\/\-](\d{1,2})[\/\-](\d{4})$/);
  return !!m && Number(m[1]) <= 12 && Number(m[2]) <= 12;   // both parts <= 12 → unresolved
};
const amb = wl.filter(r => isAmbiguous(r['Date Logged']));

const out = { worklogRows: wl.length, ambiguous: amb.length, breakdown: {}, rows: [] };
for (const r of amb) {
  const c = canon(r['Claim #']);
  const hasAnchor = c ? anchored.has(c) : false;
  const inExport = c ? fs.existsSync(path.join(ROOT, 'claims_export.csv')) && readCsv('claims_export.csv').some(x => x.claim_id === c) : false;
  const bucket = !c ? 'UNPARSEABLE_CLAIM_ID'
    : hasAnchor ? 'HAS_DTM405 (deadline anchored on 835, not on this date)'
    : 'NO_835_ANCHOR (never adjudicated)';
  out.breakdown[bucket] = (out.breakdown[bucket] || 0) + 1;
  out.rows.push({ raw: r['Claim #'], canon: c, dateLogged: r['Date Logged'],
    status: r.Status, anchor: hasAnchor, inExport });
}

/* could Date Logged ever be a deadline source? Only if it has no 835 anchor. */
const noAnchor = out.rows.filter(r => !r.anchor);
out.deadlineAffecting = noAnchor.length;

/* Resolvability by elimination: today = 2026-09-30. A denial cannot be logged in the
   future, so if exactly one of the two interpretations is <= today, the other is not a
   real possibility. This is elimination, not a guess — but per Q4 both interpretations are
   still stored and the conservative (earlier) one still drives priority. */
const TODAY = '2026-09-30';
const interpret = (s, order) => {
  const [a, b, y] = String(s).trim().split(/[\/\-]/).map(Number);
  const [m, d] = order === 'MDY' ? [a, b] : [b, a];
  if (m < 1 || m > 12 || d < 1 || d > 31) return null;
  return `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
};
let resolvable = 0, bothPast = 0, oneInvalid = 0;
const resolvedRows = [];
for (const r of amb) {
  const mdy = interpret(r['Date Logged'], 'MDY');
  const dmy = interpret(r['Date Logged'], 'DMY');
  const mdyOk = !!mdy && mdy <= TODAY, dmyOk = !!dmy && dmy <= TODAY;
  let how;
  if (mdy && dmy && mdy === dmy) { how = 'identical'; }
  else if (mdyOk && !dmyOk) { how = 'DMY eliminated (its date is in the future)'; resolvable++; }
  else if (dmyOk && !mdyOk) { how = 'MDY eliminated (its date is in the future)'; resolvable++; }
  else if (!mdyOk && !dmyOk) { how = 'BOTH in the future or invalid'; oneInvalid++; }
  else { how = 'both <= today, genuinely ambiguous'; bothPast++; }
  resolvedRows.push({ claim: canon(r['Claim #']), raw: r['Date Logged'], MDY: mdy, DMY: dmy, resolution: how });
}
out.byElimination = { resolvableByFutureTest: resolvable, genuinelyAmbiguous: bothPast,
  bothInvalid: oneInvalid, note: 'MDY/DMY only; a component > 12 is invalid and removes that reading.' };
out.resolvedRows = resolvedRows;
console.log(JSON.stringify(out, null, 2));
