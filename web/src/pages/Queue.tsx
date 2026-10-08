import { useCallback, useEffect, useMemo, useState } from "react";
import { api, money, ApiError } from "../api";
import type { WorklistResponse, WorklistRow } from "../types";

/**
 * The queue: what a specialist opens on Monday morning.
 *
 * Ordering is the product decision, so it is not re-derived here — the API sends rows already
 * sorted by `Priority` and this screen shows the score next to each row with its reasons, so the
 * order on screen can be argued with rather than merely accepted.
 */
export function Queue(props: {
  role: string;
  you: string;
  onOpen: (claimId: string) => void;
}) {
  const [data, setData] = useState<WorklistResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [status, setStatus] = useState("");
  const [bucket, setBucket] = useState("");
  const [team, setTeam] = useState("");
  const [review, setReview] = useState("");
  const [search, setSearch] = useState("");
  const [running, setRunning] = useState(false);
  const [runResult, setRunResult] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(
        await api.worklist({
          status,
          bucket,
          team,
          review,
          q: search.trim(),
        }),
      );
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [status, bucket, team, review, search]);

  useEffect(() => {
    void load();
  }, [load]);

  const teams = useMemo(
    () => [...new Set((data?.rows ?? []).map((r) => r.team))].sort(),
    [data?.rows],
  );

  async function draftAll() {
    setRunning(true);
    setRunResult(null);
    try {
      const result = await api.draftAll();
      setRunResult(
        `${result.produced} draft(s) produced, ${result.unavailable} unavailable ` +
          `(${result.stillMissing} still without a draft). ` +
          `${result.needsReview} item(s) below the confidence threshold.`,
      );
      await load();
    } catch (e) {
      setRunResult(e instanceof ApiError ? e.message : String(e));
    } finally {
      setRunning(false);
    }
  }

  if (loading && !data) return <p className="empty">Loading the queue…</p>;

  return (
    <>
      <div className="cards">
        <div className="card">
          <div className="label">In this view</div>
          <div className="value">{data?.total ?? 0}</div>
          <div className="sub">open denials</div>
        </div>
        <div className="card">
          <div className="label">Money at stake</div>
          <div className="value">{money(data?.moneyAtStake ?? 0)}</div>
          <div className="sub">denied charge in view</div>
        </div>
        <div className="card">
          <div className="label">Needs review</div>
          <div className="value">{data?.needsReview ?? 0}</div>
          <div className="sub">below confidence {70}</div>
        </div>
        <div className="card">
          <div className="label">Signed in as</div>
          <div className="value" style={{ fontSize: 18 }}>
            {data?.you ?? props.you}
          </div>
          <div className="sub">
            <span className="role-badge">{data?.role ?? props.role}</span>
          </div>
        </div>
      </div>

      <div className="filters">
        <input
          placeholder="search claim, category, payer"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{ minWidth: 240 }}
        />
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">any status</option>
          <option value="open">open</option>
          <option value="in_progress">in progress</option>
          <option value="awaiting_payer">awaiting payer</option>
          <option value="resolved">resolved</option>
          <option value="written_off">written off</option>
        </select>
        <select value={bucket} onChange={(e) => setBucket(e.target.value)}>
          <option value="">any recoverability</option>
          <option value="RECOVERABLE">recoverable</option>
          <option value="POLICY_BLOCKED">policy-blocked</option>
          <option value="EXPIRED">expired</option>
        </select>
        <select value={team} onChange={(e) => setTeam(e.target.value)}>
          <option value="">any team</option>
          {teams.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <select value={review} onChange={(e) => setReview(e.target.value)}>
          <option value="">review or not</option>
          <option value="true">needs review only</option>
          <option value="false">clear only</option>
        </select>

        {props.role === "manager" && (
          <button className="btn primary" onClick={draftAll} disabled={running}>
            {running ? "drafting…" : "Draft all (AI)"}
          </button>
        )}
      </div>

      {runResult && <div className="notice">{runResult}</div>}
      {error && <p className="error">{error}</p>}

      {!data || data.rows.length === 0 ? (
        <p className="empty">Nothing matches this filter.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Claim</th>
              <th>Payer</th>
              <th>Category → team</th>
              <th>Bucket</th>
              <th className="num">Amount</th>
              <th className="num">Days left</th>
              <th className="num">Confidence</th>
              <th className="num">Priority</th>
              <th>Status</th>
              <th>Owner</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((row) => (
              <Row key={row.claimId} row={row} onOpen={props.onOpen} />
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

function Row(props: { row: WorklistRow; onOpen: (claimId: string) => void }) {
  const r = props.row;
  const meterClass = r.confidence >= 70 ? "meter high" : "meter low";

  return (
    <tr onClick={() => props.onOpen(r.claimId)} title={r.priorityExplain}>
      <td>
        <b>{r.claimId}</b>
        {r.requiresHumanReview && (
          <>
            {" "}
            <span className="pill review">review</span>
          </>
        )}
        {r.hasDraft && <> <span className="pill ok">draft</span></>}
      </td>
      <td>{r.payerName}</td>
      <td>
        {r.category}
        <div className="sub" style={{ color: "var(--muted)", fontSize: 12 }}>
          {r.team}
          {r.preventableAtPrebill === null && " · preventability unknown"}
          {r.preventableAtPrebill === true && " · preventable"}
        </div>
      </td>
      <td>
        <span className={`pill ${r.bucket}`}>{r.bucket.replace("_", " ").toLowerCase()}</span>
      </td>
      <td className="num">{money(r.charge)}</td>
      <td className="num">{r.daysRemaining ?? "—"}</td>
      <td className="num">
        {r.confidence}
        <div className={meterClass}>
          <i style={{ width: `${r.confidence}%` }} />
        </div>
      </td>
      <td className="num" title={r.confidenceFactors.join("\n")}>
        {r.priority}
      </td>
      <td>{r.status.replace("_", " ")}</td>
      <td>{r.assignee ?? <span style={{ color: "var(--muted)" }}>unassigned</span>}</td>
    </tr>
  );
}
