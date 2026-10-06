import fs from 'node:fs';
const j = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const section = process.argv[3];
if (section) {
  const key = Object.keys(j).find(k => k.toLowerCase() === section.toLowerCase());
  if (!key) { console.error('no such section: ' + section + ' — have: ' + Object.keys(j).join(', ')); process.exit(1); }
  console.log(JSON.stringify(j[key], null, 2));
  process.exit(0);
}
const p = (label, v) => { console.log('\n=== ' + label + ' ==='); console.log(JSON.stringify(v, null, 2)); };
p('CLAIMS', j.claims);
p('REMITS', j.remits);
p('CROSS-FILE', j.remitsCrossFile);
p('Q2 vs RESENT', j.q2VsResent);
p('REFERENCE', j.reference);
p('MATCHING', j.matching);
p('WORKLOG', j.worklog);
p('WORKLOG vs REMIT', j.worklogVsRemit);
p('PAYER RULES', j.payerRules);
p('LABELED', j.labeled);
