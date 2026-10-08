import { useCallback, useEffect, useState } from "react";
import { api, money2, ApiError } from "../api";
import type { ItemDetail, WorklistRow } from "../types";

const STATUSES = [
  "open",
  "in_progress",
  "awaiting_payer",
  "resolved",
  "written_off",
] as const;

/**
 * One claim: the analysis, the draft, the billed → paid → denied timeline, and every action
 * anyone has taken on it.
 *
 * The four things the brief asks a detail page to show are deliberately in one screen because
 * they answer four different doubts — what is wrong, what to do about it, what actually happened
 * to the money, and who touched it — and a specialist switching tabs between them is the
 * difference between a two-minute job and a five-minute one.
 */
export function ClaimDetail(props: {
  claimId: string;
  role: string;
  you: string;
  onBack: () => void;
}) {
  const [data, setData] = useState<ItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setData(await api.item(props.claimId));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    }
  }, [props.claimId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function run(kind: string, action: () => Promise<unknown>, message: string) {
    setBusy(kind);
    setFlash(null);
    setError(null);
    try {
      await action();
      setFlash(message);
      setNote("");
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }

  if (error && !data) return <p className="error">{error}</p>;
  if (!data) return <p className="empty">Loading claim…</p>;

  const { row, claim, events } = data;
  const isManager = props.role === "manager";
  const locked = !isManager && row.assignee !== null && row.assignee !== props.you;

  return (
    <>
      <div className="actions" style={{ marginTop: 0, marginBottom: 14 }}>
        <button className="btn" onClick={props.onBack}>
          ← back to queue
        </button>
        <span className={`pill ${row.bucket}`}>{row.bucket.replace("_", " ").toLowerCase()}</span>
        <span className="pill">{row.category}</span>
        <span className="pill">team: {row.team}</span>
        {row.requiresHumanReview && <span className="pill review">needs human review</span>}
        <span style={{ marginLeft: "auto", fontWeight: 700 }}>{money2(row.charge)}</span>
      </div>

      {flash && <div className="notice good">{flash}</div>}
      {error && <p className="error">{error}</p>}

      <div className="detail">
        <div>
          <section className="panel">
            <h2>Analysis</h2>
            <p style={{ marginTop: 0 }}>
              <b>Recommended next action:</b> {row.nextAction}
            </p>
            <dl style={{ display: "grid", gridTemplateColumns: "180px 1fr", gap: "6px 12px", margin: 0 }}>
              <dt style={{ color: "var(--muted)" }}>Preventable before billing</dt>
              <dd style={{ margin: 0 }}>
                {row.preventableAtPrebill === null ? (
                  <>
                    <b>not established</b> — the expert sample has no position on this category, so
                    the system does not guess one.
                  </>
                ) : row.preventableAtPrebill ? (
                  "yes — a check before submission would have caught it"
                ) : (
                  "no — a pre-bill check cannot settle this"
                )}
              </dd>

              <dt style={{ color: "var(--muted)" }}>Appeal window</dt>
              <dd style={{ margin: 0 }}>
                ends <b>{row.appealEnds}</b>{" "}
                {row.daysRemaining === null
                  ? "(no open window)"
                  : row.daysRemaining <= 0
                    ? `(${Math.abs(row.daysRemaining)} days ago — closed)`
                    : `(${row.daysRemaining} days left)`}
              </dd>

              <dt style={{ color: "var(--muted)" }}>Category from labelled sample</dt>
              <dd style={{ margin: 0 }}>
                {row.coveredByLabeledSample ? "yes" : "no — this category is not in the sample"}
              </dd>

              <dt style={{ color: "var(--muted)" }}>Priority</dt>
              <dd style={{ margin: 0 }}>
                <b>{row.priority}</b> — {row.priorityExplain}
              </dd>
            </dl>

            <h3>Confidence — {row.confidence} / 100</h3>
            <ul className="factors">
              {row.confidenceFactors.map((f, i) => (
                <li key={i}>{f}</li>
              ))}
            </ul>
          </section>

          <section className="panel">
            <h2>Draft note</h2>
            {!row.hasDraft && (
              <div className="notice">
                {row.draftReason ??
                  "No draft has been generated yet. Everything above is computed without a model."}
              </div>
            )}
            {row.hasDraft && (
              <>
                <div className="draft">{row.draftBody}</div>
                {row.draftReason && <div className="notice">{row.draftReason}</div>}
              </>
            )}
            <div className="actions">
              <button
                className="btn primary"
                disabled={busy === "draft"}
                onClick={() =>
                  run("draft", () => api.draft(row.claimId), "Draft generated and re-validated.")
                }
              >
                {busy === "draft" ? "asking the model…" : row.hasDraft ? "Regenerate draft" : "Generate draft"}
              </button>
            </div>
            {row.draftVerdict && (
              <p className="factors" style={{ listStyle: "none", paddingLeft: 0 }}>
                last verdict: <b>{row.draftVerdict}</b>
                {row.draftAt && ` · ${new Date(row.draftAt).toLocaleString()}`}
              </p>
            )}
          </section>

          <section className="panel">
            <h2>Status and notes</h2>
            <div className="actions" style={{ marginTop: 0 }}>
              {STATUSES.map((s) => (
                <button
                  key={s}
                  className="btn"
                  disabled={locked || busy === `status:${s}` || row.status === s}
                  onClick={() =>
                    run(
                      `status:${s}`,
                      () => api.setStatus(row.claimId, s, note || undefined),
                      `Status set to ${s.replace("_", " ")}.`,
                    )
                  }
                >
                  {s.replace("_", " ")}
                </button>
              ))}
            </div>

            <h3>Note</h3>
            <textarea
              rows={3}
              value={note}
              placeholder="What did you find? Claim ids and codes only — no patient identifiers."
              onChange={(e) => setNote(e.target.value)}
            />

            {isManager && (
              <>
                <h3>Reassign (manager)</h3>
                <div className="actions" style={{ marginTop: 0 }}>
                  <button
                    className="btn"
                    disabled={busy === "assign:me"}
                    onClick={() =>
                      run(
                        "assign:me",
                        () => api.assign(row.claimId, props.you, note || undefined),
                        `Assigned to ${props.you}.`,
                      )
                    }
                  >
                    assign to me
                  </button>
                  <button
                    className="btn"
                    disabled={busy === "assign:none"}
                    onClick={() =>
                      run(
                        "assign:none",
                        () => api.assign(row.claimId, null, note || undefined),
                        "Unassigned.",
                      )
                    }
                  >
                    unassign
                  </button>
                </div>
              </>
            )}

            {locked && (
              <div className="notice">
                This claim is assigned to <b>{row.assignee}</b>, so you can read it but not change
                it. Ask a manager to reassign it.
              </div>
            )}
          </section>

          <section className="panel">
            <h2>Every action taken</h2>
            {events.length === 0 ? (
              <p className="empty" style={{ padding: 10 }}>
                Nothing recorded yet. The first change will appear here with who, what, when, and
                the value before and after.
              </p>
            ) : (
              <ul className="events">
                {events.map((e, i) => (
                  <li key={i}>
                    <span className="muted">
                      {new Date(e.at).toLocaleString()} · {e.actor}
                    </span>{" "}
                    changed <b>{e.field}</b>{" "}
                    <span className="arrow">
                      {e.before ?? "(empty)"} → {e.after ?? "(empty)"}
                    </span>
                    {e.note && (
                      <>
                        <br />
                        <span className="muted">{e.note}</span>
                      </>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>

        <div>
          <section className="panel">
            <h2>Timeline — billed to paid or denied</h2>
            {claim === null ? (
              <p className="empty" style={{ padding: 10 }}>
                This claim is not in the current data pack. The work item is kept so its history is
                not lost — flag it for a manager.
              </p>
            ) : (
              <>
                <p style={{ marginTop: 0, color: "var(--muted)" }}>
                  {claim.payerName} · DOS {claim.dos} · submitted {claim.submittedDate} ·{" "}
                  <b style={{ color: "var(--text)" }}>{money2(claim.charge)}</b> charged,{" "}
                  {money2(claim.lifetimePaid)} paid to date
                </p>
                <ul className="timeline">
                  <li>
                    <div>
                      <b>Submitted</b> {claim.submittedDate}
                    </div>
                    <div className="when">
                      {claim.lines.length} line(s) · {claim.payerName}
                    </div>
                  </li>
                  {claim.history.map((h) => (
                    <li key={h.seq}>
                      <div>
                        <b>
                          {statusWord(h.statusCode)} — {money2(h.paidAmount)} paid
                        </b>{" "}
                        on {h.checkDate}
                      </div>
                      <div className="when">
                        {h.sourceFile ?? "source unknown"} · submitted {money2(h.submittedCharge)}
                        {h.adjustments.length > 0 && (
                          <>
                            {" · "}
                            {h.adjustments
                              .map(
                                (a) =>
                                  `${a.groupCode} ${a.carc} ${money2(a.amount)}${a.scope === "service" ? " (line)" : ""}`,
                              )
                              .join(", ")}
                          </>
                        )}
                      </div>
                    </li>
                  ))}
                  {claim.history.length === 0 && (
                    <li>
                      <div>No remittance yet — never adjudicated.</div>
                    </li>
                  )}
                </ul>

                <h3>Lines</h3>
                <table>
                  <thead>
                    <tr>
                      <th className="num">#</th>
                      <th>CPT</th>
                      <th>Mod</th>
                      <th className="num">Units</th>
                      <th className="num">Charge</th>
                      <th>Dx</th>
                    </tr>
                  </thead>
                  <tbody>
                    {claim.lines.map((l) => (
                      <tr key={l.lineNo}>
                        <td className="num">{l.lineNo}</td>
                        <td>{l.cpt}</td>
                        <td>{l.modifier}</td>
                        <td className="num">{l.units}</td>
                        <td className="num">{money2(l.charge)}</td>
                        <td>{[l.dx1, l.dx2, l.dx3, l.dx4].filter(Boolean).join(", ")}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}
          </section>
        </div>
      </div>
    </>
  );
}

function statusWord(code: string): string {
  switch (code) {
    case "1":
      return "Paid";
    case "2":
      return "Rejected";
    case "3":
      return "Pending";
    case "4":
      return "Denied";
    case "22":
      return "Reversal / takeback";
    default:
      return `Status ${code}`;
  }
}

/** Kept exported for tests: the shape the detail page needs and nothing more. */
export type { WorklistRow };
