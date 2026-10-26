import { Puzzle } from "lucide-react";
import { useDescriptors, usePlugins } from "../api/hooks";
import { Badge, Callout, Card, Spinner } from "./ui";

/** Расширения: куда класть, что загружено (и какие блоки добавило), что не загрузилось и почему. */
export function PluginsCard() {
  const plugins = usePlugins();
  const descriptors = useDescriptors();
  return (
    <Card title={<h2 className="row"><Puzzle size={16} /> Расширения</h2>}>
      <div className="card-body stack" style={{ gap: 10 }} data-testid="plugins-card">
        <p className="small muted">
          Расширения добавляют свои блоки. Каждое — в своей папке внутри папки расширений; после добавления или удаления
          перезапустите Pepa. Расширение работает с правами Pepa — ставьте только те, которым доверяете.
        </p>
        {plugins.isLoading && <Spinner />}
        {plugins.error && <Callout tone="danger">{plugins.error instanceof Error ? plugins.error.message : String(plugins.error)}</Callout>}
        {plugins.data && (
          <>
            <div className="small">Папка расширений: <code>{plugins.data.directory}</code></div>
            {plugins.data.plugins.length === 0 && <p className="small faint">Расширений нет.</p>}
            {plugins.data.plugins.map((p) => (
              <div key={p.id} className="plugin-row">
                <div className="row">
                  <strong>{p.name}</strong>
                  {p.version && <span className="small faint">версия {p.version}</span>}
                  <span className="small faint">папка {p.id}</span>
                  <span className="spacer" />
                  {p.error ? <Badge tone="danger">Не загружено</Badge> : <Badge tone="success">Работает</Badge>}
                </div>
                {p.error
                  ? <div className="small" style={{ color: "var(--danger)" }}>{p.error}</div>
                  : <div className="small muted">Блоки: {p.nodeTypes.map((t) => descriptors.get(t)?.title ?? t).join(", ")}</div>}
              </div>
            ))}
          </>
        )}
      </div>
    </Card>
  );
}
