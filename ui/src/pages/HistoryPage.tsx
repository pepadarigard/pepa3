import { useMemo, useState } from "react";
import { History, Search } from "lucide-react";
import { useProcesses, useScenarios } from "../api/hooks";
import type { ProcessStatus } from "../api/types";
import { ProcessTable } from "../components/process";
import { Card, Empty, ErrorState, Loading } from "../components/ui";
import { ru } from "../i18n/ru";
import { navigate } from "../router";

const filters: { value: "all" | ProcessStatus; label: string }[] = [
  { value: "all", label: "Все" },
  { value: "completed", label: ru.processStatus.completed },
  { value: "failed", label: "С ошибкой" },
  { value: "cancelled", label: "Остановленные" },
  { value: "needsAttention", label: "Требуют решения" },
];

export function HistoryPage({ scenarioId }: { scenarioId?: string }) {
  const processes = useProcesses("all", scenarioId);
  const scenarios = useScenarios();
  const [status, setStatus] = useState<"all" | ProcessStatus>("all");
  const [query, setQuery] = useState("");

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (processes.data ?? []).filter(
      (p) =>
        (status === "all" || p.status === status) &&
        (!q || p.scenarioName.toLowerCase().includes(q) || (p.statusMessage ?? "").toLowerCase().includes(q) || p.id.includes(q)),
    );
  }, [processes.data, status, query]);

  const scenarioName = scenarioId ? scenarios.data?.find((s) => s.id === scenarioId)?.name : undefined;

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>История</h1>
          <p className="subtitle">Все запуски: шаги, результаты, ошибки и причины остановок.</p>
        </div>
      </header>

      <div className="row wrap">
        <div className="segmented">
          {filters.map((f) => (
            <button key={f.value} className={status === f.value ? "active" : ""} onClick={() => setStatus(f.value)}>
              {f.label}
            </button>
          ))}
        </div>
        <select
          className="select"
          style={{ width: 260 }}
          value={scenarioId ?? ""}
          onChange={(e) => navigate({ page: "history", scenarioId: e.target.value || undefined })}
        >
          <option value="">Все сценарии</option>
          {(scenarios.data ?? []).map((s) => (
            <option key={s.id} value={s.id}>{s.name}</option>
          ))}
          {scenarioId && !scenarioName && <option value={scenarioId}>Удалённый сценарий</option>}
        </select>
        <div style={{ position: "relative", width: 280 }}>
          <Search size={15} style={{ position: "absolute", left: 10, top: 10, color: "var(--faint)" }} />
          <input className="input" style={{ paddingLeft: 32 }} placeholder="Поиск по тексту или номеру" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
      </div>

      <Card>
        {processes.isLoading ? (
          <Loading />
        ) : processes.error ? (
          <div style={{ padding: 16 }}><ErrorState error={processes.error} onRetry={() => processes.refetch()} /></div>
        ) : filtered.length === 0 ? (
          <Empty icon={History} title="Запусков не найдено" />
        ) : (
          <ProcessTable processes={filtered} showActions={false} />
        )}
      </Card>
      {processes.data && processes.data.length >= 300 && (
        <p className="small faint">Показаны последние 300 запусков.</p>
      )}
    </div>
  );
}
