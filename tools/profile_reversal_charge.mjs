// quick check: total charge of the 11 reversal claims
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
const claims = readCsv('claims_export.csv');
const c2 = s => Math.round(Number(String(s ?? '').replace(/[$,\s]/g, '')) * 100);
const charge = new Map();
for (const r of claims) charge.set(r.claim_id, (charge.get(r.claim_id) || 0) + c2(r.charge));

const rev = ['GPP-2026-000230', 'GPP-2026-000519', 'GPP-2026-000655', 'GPP-2026-000763',
  'GPP-2026-001023', 'GPP-2026-001237', 'GPP-2026-001532', 'GPP-2026-001665',
  'GPP-2026-001893', 'GPP-2026-001753', 'GPP-2026-002361'];
let total = 0;
for (const k of rev) { const c = charge.get(k) || 0; total += c; console.log(k, '$' + (c / 100).toFixed(2)); }
console.log('TOTAL charge of the 11 reversal claims: $' + (total / 100).toFixed(2));
