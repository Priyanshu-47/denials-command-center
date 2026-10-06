// README.txt column/caveat audit — every column the README declares, with its actual content.
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
  const h = rows[0] || [];
  return rows.slice(1).filter(r => r.some(v => v !== '')).map(r => Object.fromEntries(h.map((k, j) => [k, r[j] ?? ''])));
}
const tally = (a, f) => a.reduce((m, x) => { const k = f(x); m[k] = (m[k] || 0) + 1; return m; }, {});

const c = readCsv('claims_export.csv');
const out = { rowCount: c.length };
for (const col of ['units', 'facility', 'pos', 'prebill_reviewed', 'coder_id', 'line_no']) {
  const vals = tally(c, r => r[col]);
  const keys = Object.keys(vals);
  out[col] = keys.length <= 12 ? vals
    : { distinct: keys.length, blanks: vals[''] ?? 0, sample: keys.slice(0, 8) };
}
out.renderingProviderDistinct = new Set(c.map(r => r.rendering_provider)).size;
out.dx1Distinct = [...new Set(c.map(r => r.dx1))].sort();
out.dosRange = [c.map(r => r.dos).sort()[0], c.map(r => r.dos).sort().at(-1)];
out.submittedRange = [c.map(r => r.submitted_date).sort()[0], c.map(r => r.submitted_date).sort().at(-1)];
out.payerPairs = tally(c, r => r.payer + ' | ' + r.payer_id);

// X12 implementation-convention version markers (README says "ASC X12 835 5010")
out.x12Version = {};
for (const f of fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort()) {
  const t = fs.readFileSync(path.join(ROOT, 'remits', f), 'latin1');
  const gs = (t.match(/GS\*[^\n~]*/) || [''])[0].split('*');
  const st = (t.match(/ST\*835\*[^\n~]*/) || [''])[0].split('*');
  out.x12Version[f] = { GS08: gs.at(-1), ST03: st.at(-1) };
}

// reference files actually loaded
out.referenceFiles = {
  carc_rarc_reference: readCsv('carc_rarc_reference.csv').length,
  claim_adjustment_group_codes: readCsv('claim_adjustment_group_codes.csv'),
  payer_rules: readCsv('payer_rules.csv').length,
  labeled_denials_sample: readCsv('labeled_denials_sample.csv').length,
};
console.log(JSON.stringify(out, null, 2));
