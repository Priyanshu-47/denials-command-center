// Phase 0 pass 10: resolve claim-vs-line count for accepted claims carrying a $0 line
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
const c2 = s => { if (s === null || s === undefined) return null;
  const t = String(s).trim().replace(/[$,\s]/g, ''); if (t === '') return null;
  if (/^\(.*\)$/.test(t)) return -Math.round(Number(t.slice(1, -1)) * 100);
  const n = Number(t); return Number.isFinite(n) ? Math.round(n * 100) : NaN; };
const usd = c => c === null || c === undefined || Number.isNaN(c) ? null : (c < 0 ? '-' : '') + '$' + (Math.abs(c) / 100).toFixed(2);
function canonId(raw) { const s = String(raw ?? '').trim(); if (!s) return null;
  const m = s.match(/^(?:GPP)[-_ ]?(\d{4})[-_ ]?(\d{1,6})$/i); if (m) return 'GPP-2026-' + m[2].padStart(6, '0');
  if (/^\d{1,6}$/.test(s)) return 'GPP-2026-' + s.padStart(6, '0');
  if (/^GPP-2026-\d{6}$/.test(s)) return s; return null; }
function parse835(file) {
  const raw = fs.readFileSync(path.join(ROOT, 'remits', file), 'latin1').replace(/\r\n?/g, '\n');
  const tx = []; let t = null, cl = null, sv = null;
  for (const seg of raw.split('~').map(s => s.replace(/\n/g, '')).filter(Boolean)) {
    const p = seg.split('*'); const tag = p[0];
    if (tag === 'ISA' || tag === 'GS') { t = { claims: [] }; continue; }
    if (tag === 'GE') { t = null; continue; }
    if (tag === 'ST') { if (!t) t = { claims: [] }; t.st = p; t.claims = []; t.hdtm = []; cl = null; sv = null; continue; }
    if (tag === 'SE') { if (t && t.st) tx.push(t); t = null; cl = null; sv = null; continue; }
    if (!t) continue;
    switch (tag) {
      case 'BPR': t.bpr = p; break;
      case 'REF': if (p[1] === '2U') t.payerId = p[2]; break;
      case 'DTM': if (!cl) t.hdtm.push(p); break;
      case 'CLP': cl = { num: p[1], status: p[2], charged: p[3], paid: p[4], svc: [], casC: [] }; t.claims.push(cl); sv = null; break;
      case 'SVC': sv = { code: p[1], charged: p[2], paid: p[3], cas: [] }; cl.svc.push(sv); break;
      case 'CAS': { const g = p[1]; for (let k = 2; k + 1 < p.length; k += 2) (sv ? sv.cas : cl.casC).push({ g, carc: p[k], amt: p[k + 1] }); break; }
      default: break;
    }
  }
  return { file, tx };
}
const files = fs.readdirSync(path.join(ROOT, 'remits')).filter(f => f.endsWith('.835')).sort();
const parsed = files.map(parse835);
const obs = [];
for (const p of parsed) for (const ti in p.tx) { const t = p.tx[ti];
  const check = (t.hdtm || []).find(d => d[1] === '405')?.[2] || '00000000';
  for (const cl of t.claims) obs.push({ file: p.file, fi: files.indexOf(p.file), ti: Number(ti), check,
    payerId: t.payerId, claim: canonId(cl.num), status: cl.status, charged: c2(cl.charged), paid: c2(cl.paid), svc: cl.svc }); }
const hist = new Map();
for (const o of obs) { if (!o.claim) continue; const a = hist.get(o.claim) || []; a.push(o); hist.set(o.claim, a); }
for (const a of hist.values()) a.sort((x, y) => (x.check - y.check) || (x.fi - y.fi) || (x.ti - y.ti) || 0);
const latest = new Map([...hist].map(([k, a]) => [k, a[a.length - 1]]));

const claims = readCsv('claims_export.csv');
const byCanon = new Map();
for (const r of claims) { const k = canonId(r.claim_id); if (!byCanon.has(k)) byCanon.set(k, []); byCanon.get(k).push(r); }

// claims whose LATEST observation is accepted (status 1) but which have a $0 paid line in it
const out = [];
for (const [k, o] of latest) {
  if (o.status !== '1') continue;
  const zero = o.svc.filter(s => c2(s.paid) === 0 && c2(s.charged) > 0);
  if (!zero.length) continue;
  out.push({ claim: k, file: o.file, check: o.check, prebill: byCanon.get(k)?.[0].prebill_reviewed,
    lines: zero.map(s => `${s.code} ${usd(c2(s.charged))} [${s.cas.map(c => c.carc).join('/')}]`) });
}
console.log('claims accepted but carrying a $0 line in their latest observation:', out.length);
console.log('total $0 line charge:', usd(out.reduce((a, x) => a + x.lines.reduce((b, l) => b + c2(l.split(' ')[1].replace('$', '')), 0), 0)));
console.log('by prebill:', out.reduce((m, x) => ((m[x.prebill] = (m[x.prebill] || 0) + 1), m), {}));
console.log('by file:', out.reduce((m, x) => ((m[x.file] = (m[x.file] || 0) + 1), m), {}));
for (const x of out) console.log(' ', x.claim, x.check, x.file.replace('era_2026', ''), 'prebill=' + x.prebill, x.lines.join(' | '));
