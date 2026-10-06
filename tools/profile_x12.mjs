// Phase 0 pass 7: X12 envelope / structural validation
import fs from 'node:fs';
import path from 'node:path';
const ROOT = 'E:/AQcode/AQSoft_Assignment_Data_Pack_1';
const out = {};
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();

for (const file of files) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1');
  const norm = raw.replace(/\r\n?/g, '\n');
  const segs = norm.split('~').map(s => s.replace(/\n/g, '')).filter(s => s.length);
  const counts = {};
  const rawTags = [];
  let cur = null;
  const tx = [];
  for (const s of segs) {
    const p = s.split('*'); const tag = p[0];
    rawTags.push(tag); counts[tag] = (counts[tag] || 0) + 1;
    if (tag === 'ST') { cur = { st: p, n: 0, first: null }; }
    if (cur) { cur.n++; if (!cur.first) cur.first = tag; }
    if (tag === 'SE') { cur.declared = Number(p[2]); cur.seControl = p[1]; tx.push(cur); cur = null; }
  }
  const isa = segs.find(s => s.startsWith('ISA*')).split('*');
  const gs = segs.find(s => s.startsWith('GS*')).split('*');
  const ge = segs.find(s => s.startsWith('GE*')).split('*');
  const iea = segs.find(s => s.startsWith('IEA*')).split('*');
  out[file] = {
    lineEnding: raw.includes('\r\n') ? 'CRLF' : raw.includes('\n') ? 'LF' : 'CR/none',
    terminator: raw.includes('~') ? '~ (tilde)' : 'MISSING',
    encodingSample: /[^\x00-\x7F]/.test(raw) ? 'contains non-ASCII' : 'ASCII only',
    isa13: isa[13], gs06: gs[6], ge02: ge[1], iea01: iea[1],
    segmentCount: segs.length,
    stCount: counts.ST, seCount: counts.SE, gsCount: counts.GS, geCount: counts.GE,
    stSeBalanced: counts.ST === counts.SE,
    geDeclaredGroups: ge[1], gsPresent: counts.GS,
    ieaDeclaredExchanges: iea[1],
    seCountMismatch: tx.filter(t => t.declared !== t.n).map(t => ({ st: t.st[2], declared: t.declared, actual: t.n })),
    transactions: tx.map(t => ({ st2: t.st[2], declared: t.declared, actual: t.n })),
    firstSegmentAfterST: [...new Set(tx.map(t => t.first))],
    tags: counts,
    emptyTransactions: tx.filter(t => t.actual <= 2).length,
    carriageReturns: (raw.match(/\r/g) || []).length,
    segmentsWithTrailingSpace: segs.filter(s => / $/.test(s)).length,
    segmentsWithEmptyElementRun: segs.filter(s => /\*\*/.test(s)).length,
    clpStatusCodes: (() => {
      const m = {}; let inTx = false;
      for (const s of segs) { const p = s.split('*');
        if (p[0] === 'ST') inTx = true;
        if (p[0] === 'SE') inTx = false;
        if (inTx && p[0] === 'CLP') m[p[2]] = (m[p[2]] || 0) + 1; }
      return m; })(),
    casGroupCodes: (() => {
      const m = {}; let inTx = false;
      for (const s of segs) { const p = s.split('*');
        if (p[0] === 'ST') inTx = true;
        if (p[0] === 'SE') inTx = false;
        if (inTx && p[0] === 'CAS') m[p[1]] = (m[p[1]] || 0) + 1; }
      return m; })(),
    lqQualifier: (() => {
      const m = {}; let inTx = false;
      for (const s of segs) { const p = s.split('*');
        if (p[0] === 'ST') inTx = true;
        if (p[0] === 'SE') inTx = false;
        if (inTx && p[0] === 'LQ') m[p[1]] = (m[p[1]] || 0) + 1; }
      return m; })(),
    clpWithPatientResp: (() => {
      let n = 0, tot = 0; let inTx = false;
      for (const s of segs) { const p = s.split('*');
        if (p[0] === 'ST') inTx = true;
        if (p[0] === 'SE') inTx = false;
        if (inTx && p[0] === 'CLP') { tot++; if (p[5] && p[5] !== '') n++; } }
      return { withResp: n, total: tot }; })(),
  };
}
process.stdout.write(JSON.stringify(out, null, 2));
