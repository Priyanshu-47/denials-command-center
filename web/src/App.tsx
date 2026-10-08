import { useCallback, useEffect, useState } from "react";
import { api, getToken, setToken, ApiError } from "./api";
import { Queue } from "./pages/Queue";
import { ClaimDetail } from "./pages/ClaimDetail";
import { Analytics } from "./pages/Analytics";
import { Prevention } from "./pages/Prevention";

type Tab = "queue" | "analytics" | "prevention";

/**
 * The shell: who you are, which tab you are on, and the one claim you have open.
 *
 * There is no router dependency on purpose. Three tabs and a detail route do not need one, and
 * every navigation this app performs is either a tab switch or a drill-down that carries state —
 * a URL would be a second place to keep in sync with the first.
 */
export function App() {
  const [token, setTokenState] = useState(getToken());
  const [who, setWho] = useState<{ you: string; role: string } | null>(null);
  const [tab, setTab] = useState<Tab>("queue");
  const [openClaim, setOpenClaim] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const identify = useCallback(async (value: string) => {
    setBusy(true);
    setError(null);
    try {
      setToken(value);
      // The worklist reports who the token belongs to and which role it carries; using its own
      // answer rather than decoding the token keeps one authority for identity — the server.
      const summary = await api.worklist({ status: "__probe__" });
      setTokenState(value);
      setWho({ you: summary.you, role: summary.role });
    } catch (e) {
      setToken("");
      setTokenState("");
      setWho(null);
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    if (token) void identify(token);
    // Only on first mount: re-running would loop, since identify sets the same token back.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!who) {
    return <SignIn onSubmit={identify} busy={busy} error={error} />;
  }

  if (openClaim) {
    return (
      <>
        <Header
          who={who}
          tab={tab}
          onTab={(t) => {
            setTab(t);
            setOpenClaim(null);
          }}
          onSignOut={() => {
            setToken("");
            setTokenState("");
            setWho(null);
          }}
        />
        <main className="main">
          <ClaimDetail
            claimId={openClaim}
            role={who.role}
            you={who.you}
            onBack={() => setOpenClaim(null)}
          />
        </main>
      </>
    );
  }

  return (
    <>
      <Header
        who={who}
        tab={tab}
        onTab={setTab}
        onSignOut={() => {
          setToken("");
          setTokenState("");
          setWho(null);
        }}
      />
      <main className="main">
        {tab === "queue" && <Queue role={who.role} you={who.you} onOpen={setOpenClaim} />}
        {tab === "analytics" && <Analytics />}
        {tab === "prevention" && <Prevention />}
      </main>
    </>
  );
}

function Header(props: {
  who: { you: string; role: string };
  tab: Tab;
  onTab: (t: Tab) => void;
  onSignOut: () => void;
}) {
  const tabs: { id: Tab; label: string }[] = [
    { id: "queue", label: "Worklist" },
    { id: "analytics", label: "Money at risk" },
    { id: "prevention", label: "Prevention" },
  ];

  return (
    <header className="topbar">
      <div className="brand">
        Denials <span>Command Center</span>
      </div>
      <nav className="nav">
        {tabs.map((t) => (
          <button
            key={t.id}
            className={props.tab === t.id ? "active" : ""}
            onClick={() => props.onTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </nav>
      <div className="who">
        <b>{props.who.you}</b>
        <span className="role-badge">{props.who.role}</span>
        {"  "}
        <button className="btn" onClick={props.onSignOut} style={{ padding: "2px 8px" }}>
          sign out
        </button>
      </div>
    </header>
  );
}

function SignIn(props: { onSubmit: (token: string) => void; busy: boolean; error: string | null }) {
  const [value, setValue] = useState("");

  return (
    <div className="login">
      <h1>Denials Command Center</h1>
      <p>
        Paste a seeded bearer token to continue. Two roles are defined by the brief:{" "}
        <b>specialist</b> (own queue, status and notes) and <b>manager</b> (everything, plus
        reassignment and review).
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          props.onSubmit(value.trim());
        }}
      >
        <input
          type="password"
          placeholder="bearer token"
          value={value}
          autoFocus
          onChange={(e) => setValue(e.target.value)}
        />
        <button type="submit" disabled={props.busy || !value.trim()}>
          {props.busy ? "checking…" : "continue"}
        </button>
      </form>
      {props.error && <p className="error">{props.error}</p>}
      <p style={{ fontSize: 12 }}>
        Local tokens are in <code>.env.example</code> under <code>SEED_USERS</code> — the
        specialist and manager entries.
      </p>
    </div>
  );
}
