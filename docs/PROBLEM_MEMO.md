# Problem Memo

*Problems found beyond what the brief asked for. One page, most material first. Every figure is
produced by a test; the test name is given so it can be re-run.*

---

### 1. The labelled sample is two populations, and four rows do not belong to the headline

**4 of the 40 rows in `labeled_denials_sample.csv` are not open denials.** Their claims are
`CurrentStatus=1` (paid), each carrying CARC 97 + 45 with a partial payment — Q1's separate
"zero-paid lines on paid claims" bucket. The other 36 are inside the 136 / $27,780.

This matters because all four are the sample's **entire** representation of
`Coding - modifier`, and that category occurs **0 times** among the 136 open denials — every
CARC-97 row in this pack sits on a paid claim. So the label set and the production workload
disagree about what this category is for.

*Effect:* reporting "40/40 on the open denials" would be false. The honest figures are 36
in-population, 4 outside it.
*Test:* `Four_of_the_forty_labelled_rows_are_not_open_denials`.

### 2. CARC 45 has no category

`carc_rarc_reference.csv` defines CARC 45 as *"charge exceeds fee schedule/maximum
allowable"*. It is a real denial reason and has **no branch** in `DenialCategory.Of` — it falls
to `UNMAPPED`.

It fails safe rather than silently: `UNMAPPED` routes to *Denials (appeal)* with
`PreventableAtPrebill = null`, and scores 25/100, well under the 70 review threshold. It is also
currently **latent** — no open denial's category is decided by an unmapped code (`UNMAPPED` = 0 of
136).

*Effect:* the first remit containing a standalone CARC 45 arrives as a human-review item with no
team of record. Decide the category, or record that fee-schedule denials are intentionally
unowned.
*Tests:* `The_reference_file_s_carcs_either_map_to_a_category_or_fail_safe_to_one`,
`The_categories_open_in_production_are_the_rules_real_workload`.

### 3. A citation that validates can still contain an invented clinical fact

Live run (`qwen2.5-coder:3b`, denial `GPP-2026-000111`) returned a **validated** citation — the
quote is genuinely in Northstar §2 — and then wrote:

> "The second same-day service followed a change of condition, which is not allowed…"

No input stated that. Our field said *"Confirm **whether** the second same-day service followed a
change of condition"*: **the model converted an instruction to verify something into an assertion
that it is so**, and then argued from the assertion.

*Effect:* citation validation proves the *policy basis*, not the prose. This is the specific gap
the drafting layer does not close, and it is why drafts route to a human rather than being sent.
*Run:* `LiveModelTests` (see `docs/AI_EVALUATION.md`).

### 4. Open items carried from Phase 1

Four known gaps, unchanged by this phase: the audit-log test runs against an in-memory store
rather than PostgreSQL; no test asserts `stored == derived` after `POST /api/ingest`; five
future-test-resolved dates are counted but not surfaced in the report; `GS08`/`ST01` are not
asserted (an ISA/GS envelope could carry the wrong version unnoticed).

### 5. Money and data loose ends

- **$159.60 in CO adjustments cannot be attributed to any claim** — 3 BHC events, reported as
  `UnattributedAdjustmentGroups` rather than forced onto a claim (D22).
- **README items still unused:** the `pos` (place-of-service) mapping, the stated Jan–Aug date
  range (never validated against the data), and the client name.

### 6. What is *not* a problem, stated so it is not re-litigated

Timely filing (CARC 29) has no policy document — that is correct, not a gap. Its windows live in
`payer_rules.csv`, which is a date table. The drafting layer reports "no policy basis found"
rather than borrowing a neighbouring payer's document, and a test pins that behaviour.
