// Phase 0 pass 9: worklog date-order evidence + foreign BHC claims
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
const wl = readCsv('worklog_extracted.csv');
let firstBig = 0, secondBig = 0, ambiguous = 0, iso = 0, other = 0;
const firstBigSamples = [], secondBigSamples = [];
for (const r of wl) {
  const d = r['Date Logged'];
  if (/^\d{4}-\d{2}-\d{2}$/.test(d)) { iso++; continue; }
  const m = d.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/);
  if (m) {
    const a = Number(m[1]), b = Number(m[2]);
    if (a > 12 && b <= 12) { firstBig++; if (firstBigSamples.length < 5) firstBigSamples.push(d); }
    else if (b > 12 && a <= 12) { secondBig++; if (secondBigSamples.length < 5) secondBigSamples.push(d); }
    else if (a <= 12 && b <= 12) ambiguous++;
    else other++;
    continue;
  }
  other++;
}
const out = {
  worklogDateFormats: { iso, slashDayFirst_proven: firstBig, slashMonthFirst_proven: secondBig,
    slashAmbiguous_bothPartsLe12: ambiguous, otherFormat: other, total: wl.length },
  slashDayFirstSamples: firstBigSamples, slashMonthFirstSamples: secondBigSamples,
};

// foreign BHC claims in remits
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const bhc = [];
for (const file of files) {
  const segs = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').split('~').map(s => s.replace(/\n/g, '')).filter(Boolean);
  let ck = '', payer = '';
  for (const g of segs) { const p = g.split('*');
    if (p[0] === 'DTM' && p[1] === '405') ck = p[2];
    if (p[0] === 'REF' && p[1] === '2U') payer = p[2];
    if (p[0] === 'CLP' && p[1].startsWith('BHC'))
      bhc.push({ file, payer, check: ck, claim: p[1], status: p[2], charged: p[3], paid: p[4] }); }
}
out.foreignBhc = { rows: bhc.length, distinctClaims: [...new Set(bhc.map(b => b.claim))].length, detail: bhc };

// how many rows total / distinct per namespace
const claims = readCsv('claims_export.csv');
out.claims = { rows: claims.length, distinct: new Set(claims.map(r => r.claim_id)).size };
process.stdout.write(JSON.stringify(out, null, 2));
