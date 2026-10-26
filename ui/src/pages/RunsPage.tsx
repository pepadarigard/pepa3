import { Activity, AlertTriangle, Info } from "lucide-react";
import { useProcesses } from "../api/hooks";
import { ProcessActions, ProcessTable } from "../components/process";
import { Card, Empty, ErrorState, Loading } from "../components/ui";
import { formatMoment } from "../lib/format";
import { attentionAdvice, explainProcess } from "../lib/status";
import { navigate } from "../router";

export function RunsPage() {
  const active = useProcesses("active");

  if (active.isLoading) return <div className="page"><Loading /></div>;
  if (active.error) return <div className="page"><ErrorState error={active.error} onRetry={() => active.refetch()} /></div>;

  const processes = active.data ?? [];
  const attention = processes.filter((p) => p.status === "needsAttention");
  const working = processes.filter((p) => p.status === "running" || p.status === "waiting");
  const paused = processes.filter((p) => p.status === "paused");

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Выполнение</h1>
          <p className="subtitle">Незавершённые процессы: работают, ждут, приостановлены или ждут вашего решения.</p>
        </div>
      </header>

      {attention.length > 0 && (
        <Card title={<h2 className="row"><AlertTriangle size={17} style={{ color: "var(--attention)" }} /> Требуют решения · {attention.length}</h2>}>
          <div className="stack" style={{ padding: 14 }}>
            {attention.map((p) => {
              const explanation = explainProcess(p);
              return (
                <div key={p.id} className="callout attention">
                  <AlertTriangle size={18} className="callout-icon" />
                  <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
                    <div className="row wrap">
                      <a href={`#/process/${p.id}`} style={{ fontWeight: 620 }}>{p.scenarioName}</a>
                      <span className="faint mono small">#{p.shortId}</span>
                      <span className="faint small">· запущен {formatMoment(p.createdAt)}</span>
                    </div>
                    <div><strong>{explanation.title}.</strong> <span className="small">{explanation.text}</span></div>
                    <div className="small muted">{attentionAdvice(p)}</div>
                    <ProcessActions process={p} size="small" />
                  </div>
                </div>
              );
            })}
          </div>
        </Card>
      )}

      <Card title={`Работают и ждут · ${working.length}`}>
        {working.length > 0 ? <ProcessTable processes={working} /> : <Empty icon={Activity} title="Сейчас ничего не выполняется" />}
      </Card>

      {paused.length > 0 && (
        <Card title={`Приостановлены · ${paused.length}`}>
          <ProcessTable processes={paused} />
        </Card>
      )}

      <div className="callout info" onClick={() => navigate({ page: "history" })} style={{ cursor: "pointer" }}>
        <Info size={18} className="callout-icon" />
        <div className="small" style={{ color: "var(--text-2)" }}>
          Очереди событий и занятые ресурсы (профили браузера, окна, мышь и клавиатура) появятся здесь вместе
          с событиями и менеджером ресурсов — это следующий этап разработки. Завершённые процессы — в «Истории».
        </div>
      </div>
    </div>
  );
}
