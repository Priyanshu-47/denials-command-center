// Where does DTM*405 actually occur? Needed because DTM*405 counts don't match ST counts
// (Q1 has 10 DTM*405 but only 7 transaction sets), and DTM*405 anchors every deadline.
import fs from 'node:fs';
import path from 'node:path';
const ROOT = process.env.DATA_DIR || 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';

for (const file of fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort()) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1');
  const segs = raw.split('~').map(s => s.replace(/[\r\n]/g, '').trim()).filter(Boolean);
  console.log(`\n### ${file}  segments=${segs.length}`);

  let st = '', clp = '', svc = '';
  const rows = [];
  segs.forEach((s, i) => {
    const p = s.split('*');
    const tag = p[0];
    if (tag === 'ST') st = p[2] ?? '';
    if (tag === 'CLP') { clp = p[1] ?? ''; svc = ''; }
    if (tag === 'SVC') svc = p[1] ?? '';
    if (tag === 'DTM' && p[1] === '405') {
      // context: which loop are we in? nearest enclosing tag before this
      let enclosing = '(header)';
      for (let j = i - 1; j >= 0 && j > i - 12; j--) {
        const t = segs[j].split('*')[0];
        if (['CLP', 'SVC', 'SE', 'ST'].includes(t)) { enclosing = t; break; }
      }
      rows.push({ i, enclosing, st, clp, svc, date: p[2], seg: s });
    }
  });

  const byEnc = rows.reduce((m, r) => (m[r.enclosing] = (m[r.enclosing] || 0) + 1, m), {});
  console.log(`  DTM*405 total=${rows.length}  by enclosing loop: ${JSON.stringify(byEnc)}`);
  for (const r of rows) {
    console.log(`    idx=${String(r.i).padStart(4)} enclosing=${r.enclosing.padEnd(4)} ST*835=${r.st} CLP=${r.clp || '-'} SVC=${r.svc || '-'}  DTM*405=${r.date}`);
  }
}
