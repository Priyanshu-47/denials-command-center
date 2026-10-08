import { useEffect, useMemo, useState } from "react";
import { api, money, ApiError } from "../api";
import type { PreventionReport } from "../types";

/**
 * Which pre-bill checks would have stopped the most denials — with what each one costs to run.
 *
 * Every check carries two numbers, not one. "This check would have caught 26 denials" is a
 * different claim depending on whether it flags 30 claims or 1,200, so benefit is always shown
 * beside burden and neither is ever presented alone. One check reports no number at all: the pack
 * has no enrollment table, and putting a confident figure on the largest category from the
 * weakest evidence in the project would be the worst thing on this screen.
 */
export function Prevention() {
  const [data, setData] = useState<PreventionReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openRule, setOpenRule] = useState<string | null>(null);

  useEffect(() => {
    api
      .prevention()
      .then(setData)
      .catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
  }, []);

  // The export is exactly the JSON the API already sent — rebuilt here so that what a manager
  // downloads and what their pre-bill system would receive are the same bytes, not a second
  // rendering of it produced by the browser.
  const exportJson = useMemo(() => {
    if (!data) return "";
    return JSON.stringify(
      {
        version: 1,
        generatedAt: data.generatedAt,
        evaluatedClaims: data.evaluatedClaims,
        rules: data.checks.map((c) => ({
          id: c.id,
          name: c.name,
          question: c.question,
          measurable: c.measurable,
          whyNotMeasured: c.whyNotMeasured,
          confidence: c.confidence,
          blocks: c.rule.blocks ?? "unknown",
          ...c.rule,
        })),
      },
      null,
      2,
    );
  }, [data]);

  if (error) return <p className="error">{error}</p>;
  if (!data) return <p className="empty">Re-running the pack against each check…</p>;

  const measurable = data.checks.filter((c) => c.measurable);

  return (
    <>
      <div className="cards">
        <div className="card">
          <div className="label">Checks evaluated</div>
          <div className="value">{data.checks.length}</div>
          <div className="sub">{measurable.length} measurable against this pack</div>
        </div>
        <div className="card">
          <div className="label">Claims replayed</div>
          <div className="value">{data.evaluatedClaims.toLocaleString("en-US")}</div>
          <div className="sub">every submitted claim, not a sample</div>
        </div>
        <div className="card">
          <div className="label">Best single check</div>
          <div className="value" style={{ color: "var(--good)" }}>
            {measurable.length ? money(measurable[0].amountCaught) : "—"}
          </div>
          <div className="sub">
            {measurable.length
              ? `${measurable[0].denialsCaught} denials — ${measurable[0].name}`
              : "none measurable"}
          </div>
        </div>
        <div className="card">
          <div className="label">Open denials, total</div>
          <div className="value">{money(measurable[0]?.openDenialAmountTotal ?? 0)}</div>
          <div className="sub">
            {measurable[0]?.openDenialsTotal ?? 0} claims — the denominator every rate uses
          </div>
        </div>
      </div>

      <div className="notice">
        Each check is a predicate re-run against all {data.evaluatedClaims.toLocaleString("en-US")}{" "}
        claims. <b>Catch</b> is denials it would have stopped before submission; <b>burden</b> is
        how many claims it would have held up to look at them. A check that flags everything
        catches everything, so neither number means anything without the other.
      </div>

      <section className="panel">
        <div className="row-head">
          <h2>What should we check before we bill?</h2>
          <button
            className="btn primary"
            onClick={() => {
              const blob = new Blob([exportJson], { type: "application/json" });
              const url = URL.createObjectURL(blob);
              const link = document.createElement("a");
              link.href = url;
              link.download = "prevention-rules.json";
              link.click();
              URL.revokeObjectURL(url);
            }}
          >
            download rules.json
          </button>
        </div>

        <div className="cards">
          {data.checks.map((check) => (
            <div
              className={`card rule ${check.measurable ? "" : "blocked"}`}
              key={check.id}
            >
              <div className="rule-head">
                <b>{check.name}</b>
                <span className={`conf ${check.confidence}`}>{check.confidence.replace("_", " ")}</span>
              </div>

              <p className="question">{check.question}</p>

              {check.measurable ? (
                <>
                  <div className="metric">
                    <div>
                      <span className="label">would have caught</span>
                      <b>{check.denialsCaught} denials</b>
                      <span className="sub">{money(check.amountCaught)}</span>
                    </div>
                    <div>
                      <span className="label">of</span>
                      <b>
                        {check.openDenialsTotal} / {money(check.openDenialAmountTotal)}
                      </b>
                      <span className="sub">{(check.catchRate * 100).toFixed(1)}% of denials</span>
                    </div>
                  </div>

                  <div className="track thin">
                    <i style={{ width: `${check.catchRate * 100}%` }} />
                  </div>

                  <div className="burden">
                    by holding up <b>{check.claimsFlagged}</b> of {check.claimsScreened} claims
                    submitted ({(check.burdenRate * 100).toFixed(1)}%)
                    {check.burdenRate > check.catchRate && (
                      <span className="warn"> — flags more than it catches</span>
                    )}
                  </div>
                </>
              ) : (
                <div className="absent">
                  <b>Cannot be measured from this data.</b>
                  <p>{check.whyNotMeasured}</p>
                  <p className="sub">
                    This is {money(0)} of nothing and {money(0)} of everything — the largest single
                    category has no number attached to it, deliberately.
                  </p>
                </div>
              )}

              <button
                className="btn small"
                onClick={() => setOpenRule(openRule === check.id ? null : check.id)}
              >
                {openRule === check.id ? "hide rule" : "show machine rule"}
              </button>

              {openRule === check.id && <pre className="json">{JSON.stringify(check.rule, null, 2)}</pre>}
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <h2>What a pre-bill system would receive</h2>
        <p className="factors" style={{ paddingLeft: 0 }}>
          The same object as the download — no second interpretation, no prose to re-derive logic
          from. A rule whose input field does not exist in the pack exports as{" "}
          <code>cannot_evaluate_without_data</code> rather than being quietly omitted, so the gap
          travels with the rules instead of disappearing on the way to production.
        </p>
        <pre className="json full">{exportJson}</pre>
      </section>
    </>
  );
}
