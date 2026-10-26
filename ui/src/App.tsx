import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Activity, History, House, KeyRound, Plug, Settings, Shapes, Workflow, type LucideIcon } from "lucide-react";
import { getToken, setToken } from "./api/client";
import { startEventStream, type ConnectionState } from "./api/events";
import { useInfo, useOverview } from "./api/hooks";
import { Button } from "./components/ui";
import { ru } from "./i18n/ru";
import { href, useRoute, type Route } from "./router";
import { HomePage } from "./pages/HomePage";
import { ScenariosPage } from "./pages/ScenariosPage";
import { EditorPage } from "./pages/editor/EditorPage";
import { RunsPage } from "./pages/RunsPage";
import { HistoryPage } from "./pages/HistoryPage";
import { ProcessPage } from "./pages/ProcessPage";
import { IntegrationsPage } from "./pages/IntegrationsPage";
import { SettingsPage } from "./pages/SettingsPage";

export function App() {
  const [hasToken, setHasToken] = useState(() => !!getToken());
  if (!hasToken) return <TokenGate onToken={() => setHasToken(true)} />;
  return <Shell />;
}

function Shell() {
  const route = useRoute();
  const queryClient = useQueryClient();
  const [connection, setConnection] = useState<ConnectionState>("connecting");
  useEffect(() => startEventStream(queryClient, setConnection), [queryClient]);

  return (
    <div className={`shell${route.page === "editor" ? " compact" : ""}`}>
      <Sidebar route={route} connection={connection} compact={route.page === "editor"} />
      <main className="main" key={route.page === "editor" ? `editor-${route.id ?? ""}` : route.page}>
        <Page route={route} />
      </main>
    </div>
  );
}

function Page({ route }: { route: Route }) {
  switch (route.page) {
    case "home":
      return <HomePage />;
    case "scenarios":
      return <ScenariosPage />;
    case "editor":
      return <EditorPage id={route.id} />;
    case "runs":
      return <RunsPage />;
    case "history":
      return <HistoryPage scenarioId={route.scenarioId} />;
    case "process":
      return <ProcessPage id={route.id} />;
    case "integrations":
      return <IntegrationsPage />;
    case "settings":
      return <SettingsPage />;
  }
}

const navItems: { route: Route; label: string; icon: LucideIcon; match: Route["page"][] }[] = [
  { route: { page: "home" }, label: ru.nav.home, icon: House, match: ["home"] },
  { route: { page: "scenarios" }, label: ru.nav.scenarios, icon: Workflow, match: ["scenarios"] },
  { route: { page: "editor" }, label: ru.nav.editor, icon: Shapes, match: ["editor"] },
  { route: { page: "runs" }, label: ru.nav.runs, icon: Activity, match: ["runs", "process"] },
  { route: { page: "history" }, label: ru.nav.history, icon: History, match: ["history"] },
  { route: { page: "integrations" }, label: ru.nav.integrations, icon: Plug, match: ["integrations"] },
  { route: { page: "settings" }, label: ru.nav.settings, icon: Settings, match: ["settings"] },
];

function Sidebar({ route, connection, compact }: { route: Route; connection: ConnectionState; compact: boolean }) {
  const overview = useOverview();
  const info = useInfo();
  const attention = overview.data?.counts.needsAttention ?? 0;
  const lastEditor = lastEditedScenario();

  return (
    <nav className={`sidebar${compact ? " compact" : ""}`} aria-label="Разделы">
      <div className="brand">
        <div className="brand-mark">
          <Workflow size={18} />
        </div>
        <div className="brand-text">
          <div className="brand-name">{ru.app.name}</div>
          <div className="brand-tag">{ru.app.tagline}</div>
        </div>
      </div>
      {navItems.map((item) => {
        const target = item.route.page === "editor" && lastEditor ? { page: "editor" as const, id: lastEditor } : item.route;
        const active = item.match.includes(route.page);
        return (
          <a key={item.label} className={`nav-item${active ? " active" : ""}`} href={href(target)} title={compact ? item.label : undefined}>
            <item.icon size={17} />
            <span className="nav-label">{item.label}</span>
            {item.route.page === "runs" && attention > 0 && (
              <span className="nav-badge" title="Процессы, требующие решения">{attention}</span>
            )}
          </a>
        );
      })}
      <div className="sidebar-footer">
        <div className="conn" title={ru.connection[connection]}>
          <span className={`conn-dot ${connection}`} />
          <span className="conn-text">{ru.connection[connection]}</span>
        </div>
        {info.data && <div className="faint version">Версия {info.data.version}</div>}
      </div>
    </nav>
  );
}

export function lastEditedScenario(): string | null {
  try {
    return localStorage.getItem("pepa.lastScenario");
  } catch {
    return null;
  }
}

export function rememberEditedScenario(id: string) {
  try {
    localStorage.setItem("pepa.lastScenario", id);
  } catch {
    // Не критично.
  }
}

function TokenGate({ onToken }: { onToken: () => void }) {
  const [value, setValue] = useState("");
  return (
    <div className="gate">
      <div className="card padded stack" style={{ width: "min(460px, 100%)" }}>
        <div className="row">
          <KeyRound size={20} />
          <h1 style={{ fontSize: 18 }}>Нужен ключ доступа</h1>
        </div>
        <p className="muted">
          Откройте Pepa из приложения или по ссылке, которую напечатал сервер (в ней есть <code>#token=…</code>). Можно
          вставить ключ вручную:
        </p>
        <input className="input mono" placeholder="ключ доступа" value={value} onChange={(e) => setValue(e.target.value)} />
        <div className="row">
          <span className="spacer" />
          <Button
            variant="primary"
            disabled={!value.trim()}
            onClick={() => {
              setToken(value.trim());
              onToken();
            }}
          >
            Войти
          </Button>
        </div>
      </div>
    </div>
  );
}
