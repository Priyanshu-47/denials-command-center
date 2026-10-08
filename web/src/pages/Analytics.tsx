import { useEffect, useState } from "react";
import { api, money, money2, ApiError } from "../api";
import type { Analytics, Dimension } from "../types";

type Cut = "byPayer" | "byCategory" | "byProvider" | "byCoder" | "byFacility";

const CUTS: { id: Cut; label: string; question: string }[] = [
  { id: "byCategory", label: "Why", question: "Why are we being denied?" },
  { id: "byPayer", label: "Which payer", question: "Which payer is holding the money?" },
  { id: "byProvider", label: "Provider", question: "Who rendered the denied care?" },
  { id: "byCoder", label: "Coder", question: "Where did the coding come from?" },
  { id: "byFacility", label: "Facility", question: "Where was the care delivered?" },
];

/**
 * Q1 and Q2, as a screen.
 *
 * Three figures are deliberately shown side by side and never summed: open denials, claims
 * never adjudicated, and accepted claims whose line was paid nothing. They are different
 * problems needing different actions, and a single "total at risk" would be a larger, more
 * satisfying, and wrong number.
 */
export function Analytics() {
  const [data, setData] = useState<Analytics | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [cut, setCut] = useState<Cut>("byCategory");

  useEffect(() => {
    api
      .analytics()
      .then(setData)
      .catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
  }, []);

  if (error) return <p className="error">{error}</p>;
  if (!data) return <p className="empty">Loading the money view…</p>;

  const m = data.moneyAtRisk;
  const rows: Dimension[] = data[cut];

  return (
    <>
      <div className="cards">
        <div className="card">
          <div className="label">Open denials</div>
          <div className="value">{money(m.openAmount)}</div>
          <div className="sub">{m.openCount} claims — the workable book</div>
        </div>
        <div className="card">
          <div className="label">Still recoverable</div>
          <div className="value" style={{ color: "var(--good)" }}>
            {money(m.buckets[0]?.amount ?? 0)}
          </div>
          <div className="sub">
            {m.buckets[0]?.count ?? 0} claims — window open, no policy bar
          </div>
        </div>
        <div className="card">
          <div className="label">Never adjudicated</div>
          <div className="value">{money(m.pendingAmount)}</div>
          <div className="sub">{m.pendingCount} claims — no remittance yet, not denials</div>
        </div>
        <div className="card">
          <div className="label">Passed, paid nothing</div>
          <div className="value">{money(m.zeroPaidLineAmount)}</div>
          <div className="sub">
            {m.zeroPaidLineCount} accepted claims with a $0 line — separate by design
          </div>
        </div>
      </div>

      <section className="panel">
        <h2>How much can we still get back?</h2>
        <div className="bars">
          {m.buckets.map((b) => (
            <div className="bar" key={b.bucket}>
              <span>{b.bucket.replace("_", " ").toLowerCase()}</span>
              <div className="track">
                <i style={{ width: `${m.openAmount ? (b.amount / m.openAmount) * 100 : 0}%` }} />
              </div>
              <span className="val">
                {b.count} · {money(b.amount)}
              </span>
            </div>
          ))}
        </div>
        <p className="factors" style={{ paddingLeft: 0 }}>
          The three are mutually exclusive and always sum to {m.openCount} /{" "}
          {money(m.openAmount)}. Expired means no denial route remains — it is not counted as
          recoverable, and a claim whose filing window is open does not reopen it.
        </p>
      </section>

      <section className="panel">
        <h2>Why are we being denied?</h2>
        <div className="filters">
          {CUTS.map((c) => (
            <button
              key={c.id}
              className={`btn ${cut === c.id ? "primary" : ""}`}
              onClick={() => setCut(c.id)}
            >
              {c.label}
            </button>
          ))}
          <span style={{ color: "var(--muted)", fontSize: 13 }}>
            {CUTS.find((c) => c.id === cut)?.question}
          </span>
        </div>

        <div className="bars">
          {rows.map((r) => (
            <div className="bar" key={r.key}>
              <span title={r.key}>{r.key}</span>
              <div className="track">
                <i style={{ width: `${(r.amount / (m.openAmount || 1)) * 100}%` }} />
              </div>
              <span className="val">
                {r.count} · {money(r.amount)}
              </span>
            </div>
          ))}
        </div>
      </section>

      <div className="detail">
        <section className="panel">
          <h2>Which of these should never have left the building?</h2>
          <table>
            <thead>
              <tr>
                <th>Pre-bill review</th>
                <th className="num">Decided claims</th>
                <th className="num">Denials</th>
                <th className="num">Denial rate</th>
              </tr>
            </thead>
            <tbody>
              {data.byPreBill.map((r) => (
                <tr key={r.reviewed} style={{ cursor: "default" }}>
                  <td>
                    <b>{r.reviewed === "Y" ? "Reviewed before billing" : "Not reviewed"}</b>
                  </td>
                  <td className="num">{r.count}</td>
                  <td className="num">{r.denials}</td>
                  <td className="num">{(r.denialRate * 100).toFixed(1)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="notice">
            An <b>association, not a cause</b>: this counts claims that have been decided, and
            reviewed claims may simply have been the cleaner ones. It answers "where is the gap",
            not "review would have prevented this".
          </div>
        </section>

        <section className="panel">
          <h2>Denials by month the payer denied them</h2>
          <div className="bars">
            {data.trend.map((t) => (
              <div className="bar" key={t.period} style={{ gridTemplateColumns: "80px 1fr 110px" }}>
                <span>{t.period}</span>
                <div className="track">
                  <i
                    style={{
                      width: `${
                        (t.amount / Math.max(...data.trend.map((x) => x.amount), 1)) * 100
                      }%`,
                    }}
                  />
                </div>
                <span className="val">
                  {t.count} · {money(t.amount)}
                </span>
              </div>
            ))}
          </div>
          <p className="factors" style={{ paddingLeft: 0 }}>
            Anchored on the check date of the denying remittance (<code>DTM*405</code>), not the
            date someone typed it into the tracker.
          </p>
          <p style={{ color: "var(--muted)", fontSize: 12 }}>
            Largest single denial:{" "}
            {money2(Math.max(...data.byCategory.map((c) => c.amount), 0))} in{" "}
            {data.byCategory[0]?.key ?? "—"}.
          </p>
        </section>
      </div>
    </>
  );
}
