// Phase 0 pass 8: identify every claim with more than one remit observation
import fs from 'node:fs';
import path from 'node:path';
const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
const canon = raw => {
  const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i);
  if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s;
  return null;
};
const DUP = 'era_2026Q2_resent_0719.835';
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const obs = [];
for (const file of files) {
  const segs = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').split('~').map(s => s.replace(/\n/g, '')).filter(Boolean);
  let ck = '00000000', tk = -1, idx = -1;
  for (const g of segs) { const p = g.split('*'); idx++;
    if (p[0] === 'DTM' && p[1] === '405') ck = p[2];
    if (p[0] === 'ST') tk++;
    if (p[0] === 'CLP') obs.push({ file, fi: files.indexOf(file), tk, idx, ck,
      raw: p[1], claim: canon(p[1]), status: p[2], charged: p[3], paid: p[4] }); }
}
const fmt = v => v.slice(0, 4) + '-' + v.slice(4, 6) + '-' + v.slice(6, 8);
const by = new Map();
for (const o of obs) { if (!o.claim || o.file === DUP) continue; const a = by.get(o.claim) || []; a.push(o); by.set(o.claim, a); }
const multi = [...by.entries()].filter(([, a]) => a.length > 1);
for (const [, a] of multi) a.sort((x, y) => (x.ck - y.ck) || (x.fi - y.fi) || (x.tk - y.tk) || (x.idx - y.idx));
multi.sort((a, b) => a[0].localeCompare(b[0]));
let i = 0;
for (const [k, a] of multi) {
  i++;
  console.log(String(i).padStart(2) + '. ' + k + '  ' +
    a.map(o => fmt(o.ck) + ' ' + o.file.replace('era_2026', '').replace('.835', '') + '/tx' + (o.tk + 1) +
      ' -> st' + o.status + ' chg=' + o.charged + ' paid=' + o.paid).join('  |  '));
}
console.log('multi-observation claims (duplicate resent file excluded):', multi.length);
console.log('of which span more than one distinct file:', multi.filter(([, a]) => new Set(a.map(o => o.file)).size > 1).length);
console.log('of which include a reversal (status 22):', multi.filter(([, a]) => a.some(o => o.status === '22')).length);
console.log('of which include a denial (status 4):', multi.filter(([, a]) => a.some(o => o.status === '4')).length);
console.log('paid in more than one distinct file:', multi.filter(([, a]) => new Set(a.filter(o => o.status === '1').map(o => o.file)).size > 1).length);
