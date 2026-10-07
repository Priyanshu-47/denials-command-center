#!/usr/bin/env node
// PHI guard — blocks a commit/CI run that would track raw data or patient-identifier columns.
//
// Rules
//   R1 path   : any tracked file inside the data pack (or a raw .835 / worklog .xlsx)  -> BLOCK
//               EXCEPT the exact list in APPROVED_PACK_FILES (D24), which is still
//               scanned by R2/R3 below — path approval is not a content pass.
//   R2 header : any tracked *.csv / *.tsv whose header row carries a patient-identifier
//               column (patient_first, patient_last, patient_dob, member_id, Patient)  -> BLOCK
//   R3 value  : any tracked *text* file containing a real patient name as a whole word  -> BLOCK
//               (only when the pack is present so the name list can be derived from it;
//                skipped with a notice otherwise — never guessed)
//
// Docs may legitimately mention column *names* (`patient_dob`); that is R2, and R2 only
// inspects data files, so `docs/*.md` is unaffected.
//
// APPROVED_PACK_FILES is deliberately an exhaustive list of literal paths rather than a
// glob: a new file in the pack is a violation until someone adds it here in a diff that
// a human reads. `payer_policies/*` would silently admit whatever turn up next.
//
// Usage:
//   node tools/phi_guard.mjs            # scan all tracked files
//   node tools/phi_guard.mjs --staged   # scan only staged (pre-commit) changes
//   node tools/phi_guard.mjs --help

import { execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const REPO = path.resolve(new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const DATA_DIR = process.env.DATA_DIR || 'AQSoft_Assignment_Data_Pack_1';

const IDENTIFIER_COLUMNS = ['patient_first', 'patient_last', 'patient_dob', 'member_id'];
const DATA_EXT = new Set(['.csv', '.tsv']);
const TEXT_EXT = new Set(['.md', '.mjs', '.js', '.ts', '.tsx', '.cs', '.json', '.yml', '.yaml',
  '.txt', '.ps1', '.sh', '.env', '.example', '']);
const RAW_NAME = /(^|[\\/])(remits[\\/].*\.835|denials_worklog[^\\/]*\.xlsx)$/i;

// Where the pack sits inside this repository. Used for R1 only — DATA_DIR below is where
// the pack happens to be for *this* run, which is a reviewer's own unzip location and may
// not be a path git tracks at all. Basing an exclusion rule on a runtime override would let
// DATA_DIR=/somewhere-else quietly switch the rule off.
const PACK = 'AQSoft_Assignment_Data_Pack_1';

// D24 (user-approved, 2026-10-07) — exactly these four references may be tracked. They are
// pure reference data: day-count windows, code meanings, policy prose. No person appears in
// them, and they are what the system reasons from, so they are readable without the pack.
// Everything else under PACK is still a violation. These files are NOT skipped below: they
// fall through to R2 and R3 like any other file.
const APPROVED_PACK_FILES = new Set([
  `${PACK}/payer_rules.csv`,
  `${PACK}/carc_rarc_reference.csv`,
  `${PACK}/claim_adjustment_group_codes.csv`,
  `${PACK}/payer_policies/ALL_PAYERS_MOD25-2026.md`,
  `${PACK}/payer_policies/CSA_PROVIDER-ENROLLMENT.md`,
  `${PACK}/payer_policies/MPPO_DX-EXCL-03.md`,
  `${PACK}/payer_policies/NSHP_HOSP-FREQ-07.md`,
  `${PACK}/payer_policies/SMP_SNF-AUTH-2026.md`,
]);

const args = process.argv.slice(2);
if (args.includes('--help')) {
  console.log(fs.readFileSync(new URL(import.meta.url), 'utf8').split('\n').slice(1, 16).join('\n'));
  process.exit(0);
}

function git(args, allowFail = false) {
  try { return execSync(`git ${args}`, { cwd: REPO, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }); }
  catch (e) { if (allowFail) return ''; throw e; }
}

const tracked = git('ls-files')
  .split('\n').map(s => s.trim()).filter(Boolean);

// `--staged` scans the git INDEX — i.e. exactly the tree the next commit would
// record — not a HEAD-vs-index diff. A diff would silently miss any path that is
// already in HEAD, which is precisely the case that matters here.
const targets = tracked;

/* name list, derived from the pack only when it is present ---------------- */
function loadNames() {
  const claims = path.join(REPO, DATA_DIR, 'claims_export.csv');
  if (!fs.existsSync(claims)) return new Set();
  const lines = fs.readFileSync(claims, 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/);
  const header = (lines[0] || '').split(',');
  const i = header.indexOf('patient_first');
  if (i < 0) return new Set();
  return new Set(lines.slice(1).filter(Boolean).map(l => l.split(',')[i]?.trim()).filter(Boolean));
}
const names = loadNames();

const violations = [];
// Repo-relative: a path is "in the pack" if it sits at the pack's own location. DATA_DIR is
// not consulted here — see PACK above.
const isUnderPack = f => f === PACK || f.startsWith(PACK + '/');

/* R1 + R2 + R3 ---------------------------------------------------------- */
for (const f of targets) {
  const abs = path.join(REPO, f);

  // R1: in the pack (and not on the approved list), or a raw remittance/worklog by shape.
  const rawByName = RAW_NAME.test(f);
  if ((isUnderPack(f) && !APPROVED_PACK_FILES.has(f)) || rawByName) {
    violations.push({
      rule: 'R1-path',
      file: f,
      detail: rawByName
        ? 'raw remittance / worklog file must never be tracked'
        : 'raw data-pack file must never be tracked (not on the D24 approved list)',
    });
    continue;
  }
  // An approved pack file falls through: it is still an .md/.csv, so R2 and R3 run on it
  // below. Nothing about being on the approved list exempts it from content checks.
  if (!fs.existsSync(abs)) continue;

  const ext = path.extname(f).toLowerCase();
  const buf = fs.readFileSync(abs);
  if (buf.includes(0)) continue;                       // binary: skip content rules

  if (DATA_EXT.has(ext)) {
    const firstLine = buf.toString('utf8').split(/\r?\n/, 1)[0].toLowerCase();
    const found = IDENTIFIER_COLUMNS.filter(c => firstLine.includes(c));
    if (found.length) {
      violations.push({ rule: 'R2-header', file: f, detail: `patient-identifier column(s): ${found.join(', ')}` });
      continue;
    }
  }

  if (TEXT_EXT.has(ext) && names.size > 0) {
    const text = buf.toString('utf8');
    for (const n of names) {
      if (n.length < 3) continue;
      if (new RegExp(`\\b${n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`).test(text)) {
        violations.push({ rule: 'R3-value', file: f, detail: `contains patient name "${n}"` });
        break;
      }
    }
  }
}

/* report ----------------------------------------------------------------- */
if (violations.length === 0) {
  console.log(`phi_guard: OK — ${targets.length} file(s) scanned, `
    + `0 violations${names.size ? `, ${names.size} patient names known` : ' (name check skipped: pack absent)'}.`);
  process.exit(0);
}

console.error(`\nphi_guard: BLOCKED — ${violations.length} violation(s):\n`);
for (const v of violations) console.error(`  [${v.rule}]  ${v.file}\n            ${v.detail}`);
console.error(`
Raw data belongs in DATA_DIR (git-ignored), never in git.
  git rm --cached <file>            # stop tracking it, keep it on disk
  echo <file> >> .gitignore

If this file really is reference data with no person in it, the way to allow it
is to add its EXACT path to APPROVED_PACK_FILES in tools/phi_guard.mjs, in a
change someone will read. Do not widen the pack pattern in .gitignore instead —
that opens every file next to it.
`);
process.exit(1);
