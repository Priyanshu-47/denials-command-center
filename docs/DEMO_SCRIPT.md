# Demo script — 6 minutes, timed

Screen recording, single take. Five seconds of silence at each `CUT` so the edit can breathe.

**Set-up before you hit record.** `docker compose up --build`, wait for `web healthy`. Two browser
tabs open on `http://localhost:5173`, both signed out. A terminal off-screen showing
`docker compose logs -f api`. Console at 100% zoom, light theme, no bookmarks bar.

**Tokens** (from `.env`, `SEED_USERS`):

| persona | token | role |
|---|---|---|
| Dana | `local-specialist-token-0000000` | specialist |
| Priya | `local-manager-token-0000000000` | manager |

Keep a sticky note with both on it. Never read the reader/ingest tokens aloud — they are not part
of the story.

---

## 0:00–0:40 — what this is

**Camera:** desktop, browser only.

> This is the Denials Command Center. Three questions, three tabs: how much money is stuck and
> can we still get it back, what should each person work on today, and which of these denials
> should never have left the building. Everything runs from `docker compose up` — database, API
> and this console.

Click each tab in turn, then come back to the sign-in.

> It is a two-role product. Dana works her own queue; Priya sees everything and moves work
> between people. Sign in as Dana.

**Paste the specialist token, click continue.** Point at the role badge.

**CUT**

## 0:40–2:00 — the specialist's queue (deliverable D)

**Tab:** Worklist.

> Dana's queue is her work plus whatever is unassigned and waiting to be picked up. It is ordered
> by a score the system can explain — nothing is hidden behind a ranking.

Click the first row's priority explanation and read it aloud:

> Recoverable, plus 400 — window open and no policy bar. Due in five days, plus 150. That is the
> whole rule. If you disagree with the ordering, you can disagree with a specific sentence.

**Open the top claim.** Scroll the timeline top to bottom.

> Billed, denied, the remittance, and then — this is the part that usually lives in someone's
> head — every action taken on it since. Who, what, when, and the value before and after.

Change the status to `in_progress` with a note, e.g. `Called payer, reference pending`.

> One change, one record. Before, after, who, when — written in the same transaction as the
> change, so there is no version of this system where the work was updated and nobody is told.

Point at the new entry in the timeline, then at the audit trail link.

> Reload the page. It is still there, because the analysis is recomputed every time but the human
> decisions are the only thing being stored.

**Reload to prove it.** **CUT**

## 2:00–3:10 — the manager sees everything

**Sign out. Sign in as Priya.**

> Same queue, but it is the whole book, and she can move work between people.

Open a claim assigned to Dana (or unassigned), and **reassign it** to `Dana`. Show the event
appear.

> Every reassignment is a line in the same history — this is the answer to "who moved my work?"

Then show the boundary:

> Dana cannot do that. Switch back to Dana's tab and try the same claim's assign button — it is
> not there, and if she calls the endpoint directly she gets a 403. And claims belonging to
> another specialist are not in her list at all; the detail route 404s, because a 403 would tell
> her the claim exists and whose it is.

**CUT**

## 3:10–4:10 — AI analysis, and what happens without it (deliverable C)

**Tab:** Worklist, manager view. Point at the confidence badge and the review flag on a row.

> Category, owning team, whether a pre-bill check would have caught it, the next action, the
> deadline, and a confidence — all computed deterministically. The model is only asked for one
> thing: the draft appeal.

**Click "generate draft" on an appealable claim.** Show the citation.

> The draft cites the exact policy section — this one is §4 of the payer's authorization
> document — and it only cites a document belonging to the payer that denied this claim. A quote
> is checked against the source after the model answers: whitespace may differ, a reworded quote
> fails and the draft is dropped.

Now the safety demonstration — **stop the model** (or, if recording offline, use a claim where no
draft exists):

> Now watch the part that matters. If the model is unavailable — no key, wrong URL, or it simply
> stops answering — the product does not change its behaviour.

**Show the same claim with no draft:** analysis, bucket, deadline, priority, next action all
present; only the draft is missing, with the reason stated.

> Nothing here is generated. The queue, the money and the deadline all still work; only the
> drafted note is gone, and it says why. Low-confidence items are routed to a person either way.

**CUT**

## 4:10–5:20 — money at risk (deliverable E)

**Tab:** Money at risk.

> How much is stuck — and the honest answer is three different numbers, never added together.

Point at the four cards in turn.

> 136 open denials, $27,780 — the workable book. 58 claims, $12,600, billed and never adjudicated
> — not denials, no remittance yet. 19 accepted claims with a line that was passed and paid
> nothing — separate, because a paid claim is not work the denial queue handles. Adding these
> together would give a bigger, more impressive, wrong number.

Point at the bucket bars.

> Of the 136: $16,785 still recoverable, $4,460 blocked by payer policy, $6,535 expired. Those
> three always sum to the open book — there is a test for that.

Click through **Why → Which payer → Coder → Facility**.

> Breakdowns by payer, reason, provider, coder and facility — all cuts of the same 136, so every
> one of them adds back to $27,780.

Point at the pre-bill table and its caveat.

> Claims reviewed before billing deny at X% versus Y% unreviewed. An association, not a cause —
> and it only counts claims that have been decided, so the denominator cannot move under you.

**CUT**

## 5:20–6:10 — prevention, and what we cannot measure

**Tab:** Prevention.

> Which pre-bill checks would have stopped the most denials — each one re-run against all 1,222
> claims, not estimated.

Read the top card's two numbers together:

> This one would have caught 26 denials, $X — by holding up 30 claims out of 1,222. Both numbers
> or neither: a check that flags everything catches everything.

**Scroll to the check with no number** (`provider-enrolled-on-dos`).

> This is the largest single category, and this product will not put a figure on it. There is no
> enrollment table in the data pack, so it reports "cannot be evaluated without data" rather than
> a confident zero or a guess — and it still exports, so the gap travels with the rules.

**Click "show machine rule"**, then **download rules.json** and open it.

> Every check exports as machine-readable JSON a pre-bill system can apply. The rules carry
> confidence, and the one we cannot measure says so in the file.

**CUT**

## 6:10–6:40 — the honest limits (problem memo)

**Camera:** browser, or the problem memo if you prefer a document.

> Three things this got wrong or could not do. The labelled sample has 40 rows and four of them
> are not open denials — so the headline accuracy is reported as fit to a labelled sample, never
> as accuracy. One category in the real data — CARC 45 — has no mapping in the taxonomy at all,
> and it shows up as unmapped rather than being forced into the nearest bucket. And one of the
> payer policies makes a claim about credentialing that our own analysis treats as not
> retroactive; that disagreement is in the memo, not smoothed over.

**CUT**

## 6:40–7:00 — how to run it

> `cp .env.example .env`, point `DATA_DIR` at the unzipped pack, `docker compose up`. Four seeded
> identities, tests on parsing, money, reconciliation, idempotency, priority, prevention,
> authorization and audit. If no LLM is configured it starts anyway and tells you.

**Camera:** terminal with `docker compose ps` green, then back to the app title.

> Denials Command Center. Correct first, confident second.

---

## Recording notes

- **Never** paste a real-looking patient name, member id or date of birth on screen or read one
  aloud. The seeded claim ids (`GPP-2026-…`) are safe to show; anything from `claims_export.csv`
  beyond them is not.
- If a model call takes longer than five seconds on camera, cut the wait and hold on the
  "generating…" state — do not narrate silence.
- If something fails during the take, record the failure and say what it means. A take where the
  model is unreachable is a *better* demo of C than one where it works, and the script already
  has a slot for it.
- Total target: **6:40–7:00**, inside the 5–8 minute window with room for a slow read.
