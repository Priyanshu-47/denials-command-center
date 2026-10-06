# AI Usage Log

Where AI helped, where it was wrong, and how the error was caught.
Every entry names the artefact it touched so a reviewer can re-run it.

---

## Phase 0 — data profiling (2026-09-30)

### AI helped

- **PDF extraction.** The brief is a PDF and no Python is available on this machine.
  AI wrote `tools/pdf3.js` (zlib/flate stream decode + text-operator extraction) which
  recovered the full assignment text, and `tools/extract_worklog.ps1` for the xlsx. Both are
  re-runnable and produce the text quoted in the docs.
- **X12 835 segment reader design.** AI proposed the single-pass state machine (envelope →
  transaction → CLP → SVC → CAS/LQ) used by all `tools/profile_*.mjs`. The alternative —
  one regex per segment type — was rejected because `CAS` amount pairs are variable-length
  (`CAS*CO*45*53.20*97*180.00`) and a regex silently truncates at the first pair.
- **Formulating falsifiable hypotheses before measuring.** e.g. *"policy documents contradict
  `payer_rules.csv`"*, *"the resend is byte-identical so a file hash is enough"*, *"status-22
  amounts are stored positive"*. Writing them down first made it possible to report
  **not confirmed** honestly instead of finding what was expected.
- **Framing the denominator question** that became finding #1: counting every `CLP02 = 4`
  row would have reported 142 denials when only 136 are open.

### AI was wrong

**W1 — money sign handling (material).**
Parser written as:

```js
const neg = t.startsWith('-') || /^\(.*\)$/.test(t);
const n  = Number(t.replace(/[()]/g, ''));
return Math.round(n * 100) * (neg ? -1 : 1);
```

`Number('-240.00')` is already −24000 cents, so the extra multiplication flipped every
reversal to positive. AI then wrote a confident, wrong conclusion into the draft findings:

> "Q3's BPR is $2,759.00 less than ΣCLP04 — status-22 amounts are stored positive and must be
> applied as negative."

The data was right; the parser was wrong. Reversal rows are `CLP*000230*22*-240.00*-148.80`
and `CAS*CO*45*-91.20` — already negative.

- **How it was caught:** while asserting `CLP03 = CLP04 + ΣCAS` with signed values, the result
  was **−$1,691.00** instead of 0. An aggregate that "looks about right" hid it; the identity
  did not. That forced a look at a raw reversal block instead of the rollup.
- **Fix:** `if (/^\(.*\)$/.test(t)) return -round(num*100); return round(Number(t)*100);`
- **After the fix:** `BPR = ΣCLP04` = **$0.00** for all four files *and* all four payers.

**W2 — absolute value on adjustments (compensating error).**
Adjustments were summed as `|CAS|`. Combined with W1, *two* errors cancelled and
`CLP03 = CLP04 + ΣCAS` came out at exactly zero — a false pass.
- **How it was caught:** fixing W1 broke the balance by −$1,691.00, which could only happen if
  `|CAS|` was wrong too.
- **Fix:** plain signed sum. Then `ΣCLP03 − ΣCLP04 = $114,159.64 = CO $108,516.40 + PR $5,643.24`
  exactly.

**W3 — history leaking into "current state" (off-by-one, material to a published figure).**
A claim can be denied *and* re-paid inside the **same transaction** (`GPP-2026-001210`,
`era_2026Q2` tx1, same check date, same file). AI's "keep only rows from the latest
observation" filter compared `check + file` but not `transaction + segment`, so a `$0` line
from the superseded row survived. Two different code paths therefore disagreed:
**19 vs 20** accepted claims carrying a $0 line.
- **How it was caught:** computing the same statistic by a third, independent route
  (`tools/profile_lineonly.mjs`) gave 19, and pre-bill `Y + N` gave 12 + 8 = 20. The mismatch
  was chased to a specific claim rather than averaged away.
- **Fix:** rebuild the set directly from the winning observation's own service lines.
- **Numbers that moved:** claims with a `$0` line 156 → **155**; `CARC 11` in that table
  26 → **25**; pre-bill "not reviewed" denial rate 14.2% → **14.1%**; `CARC 97` claim count
  21 → **19**. All corrected in `docs/DATA_FINDINGS.md` before publication.

**W4 — file-level dedup assumption (would have been material in Phase 1).**
AI's first design note said "detect the duplicate remittance with SHA-256 of the file".
- **How it was caught:** actually hashing both Q2 files showed `7a9df943…` ≠ `86a74d76…`.
  Segment-by-segment diff showed **0 differing segments outside the envelope**.
- **Fix:** `payload_sha256` over all segments outside `ISA`/`GS`/`GE`/`IEA`. Both hashes are
  stored; dedup keys on payload. Cost of the mistake had it shipped: **+$47,461.62 paid,
  +453 claims, +41 false denials.**

**W5 — overstating what the AI layer proves.**
AI initially drafted language implying the categorisation "achieves 100% with AI".
- **How it was caught:** the 100% comes from a **pure rules function** with no model involved
  (`docs/DATA_FINDINGS.md` §9). Claiming model credit for it would be unfalsifiable.
- **Fix:** `docs/AI_EVALUATION.md` will report **rules-only** as the primary number, state that
  the 40-row labelled set cannot demonstrate LLM added value, and test the model separately on
  refusal handling, citation grounding and adversarial inputs.

**W6 — committed a derived file containing patient names.**
`worklog_extracted.csv` (columns include `Patient`, e.g. `"Wallace, J"`) was produced by
`tools/extract_worklog.ps1` and `git add -A` swept it into the first commit alongside the
client-supplied pack.
- **How it was caught:** reviewing the commit's file list before reporting Phase 0.
- **Fix:** `git rm --cached` + `.gitignore` entry + **amending the commit**, so the blob leaves
  history rather than being removed by a follow-up commit. Recorded as decision **D13**: the
  client pack stays as received; derived extracts containing identifiers are regenerated.
- Verified no patient *values* appear in `docs/` or `tools/` — only column names.

### AI was uncertain and said so

- **CARC 18 → "Payer error"** is an extrapolation with no labelled example. It affects 6 open
  denials. Raised as **[Q5]** rather than quietly decided.
- **Whether "recoverable" should exclude the 26 credentialing denials** changes the headline
  from **$21,245** to **$16,785**. Raised as **[Q2]** with both numbers shown.
- **Whether line-level denials belong in the headline total** is **[Q1]**.
- **Pre-bill review efficacy:** 12.7% vs 14.1% is an *unadjusted* comparison over 1,164
  claims. It is not a controlled result and is stated as an association, not causation.
