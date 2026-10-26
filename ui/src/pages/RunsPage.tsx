import { useState } from "react";
import { Activity, AlertTriangle, Send } from "lucide-react";
import { useEvents, useProcesses, useResources } from "../api/hooks";
import { EventConflicts, EventsTable, ResourcesCard, useSendEvent } from "../components/platform";
import { ProcessActions, ProcessTable } from "../components/process";
import { Button, Card, Empty, ErrorState, Loading } from "../components/ui";
import { formatMoment } from "../lib/format";
import { attentionAdvice, explainProcess } from "../lib/status";

export function RunsPage() {
  const active = useProcesses("active");
  const events = useEvents("all");
  const resources = useResources();
  const sender = useSendEvent();
  const [eventFilter, setEventFilter] = useState<"recent" | "unmatched">("recent");

  if (active.isLoading) return <div className="page"><Loading /></div>;
  if (active.error) return <div className="page"><ErrorState error={active.error} onRetry={() => active.refetch()} /></div>;

  const processes = active.data ?? [];
  const attention = processes.filter((p) => p.status === "needsAttention");
  const working = processes.filter((p) => p.status === "running" || p.status === "waiting");
  const paused = processes.filter((p) => p.status === "paused");
  const allEvents = events.data ?? [];
  const conflicts = allEvents.filter((e) => e.state === "conflict");
  const shownEvents = (eventFilter === "unmatched" ? allEvents.filter((e) => e.state === "unmatched") : allEvents).slice(0, 50);

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Выполнение</h1>
          <p className="subtitle">Незавершённые процессы, события и занятые ресурсы.</p>
        </div>
        <div className="row">
          <Button icon={Send} onClick={sender.open}>Отправить событие</Button>
        </div>
      </header>

      {(attention.length > 0 || conflicts.length > 0) && (
        <Card title={<h2 className="row"><AlertTriangle size={17} style={{ color: "var(--attention)" }} /> Требуют решения · {attention.length + conflicts.length}</h2>}>
          <EventConflicts events={conflicts} />
          {attention.length > 0 && (
            <div className="stack" style={{ padding: 14, paddingTop: conflicts.length > 0 ? 0 : 14 }}>
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
          )}
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

      {resources.error ? <ErrorState error={resources.error} onRetry={() => resources.refetch()} /> : <ResourcesCard resources={resources.data ?? []} />}

      <Card title="События"
        actions={
          <div className="segmented">
            <button className={eventFilter === "recent" ? "active" : ""} onClick={() => setEventFilter("recent")}>Последние</button>
            <button className={eventFilter === "unmatched" ? "active" : ""} onClick={() => setEventFilter("unmatched")}>Никому не подошли</button>
          </div>
        }>
        {events.isLoading ? <Loading /> : events.error ? <ErrorState error={events.error} onRetry={() => events.refetch()} /> : <EventsTable events={shownEvents} />}
      </Card>

      {sender.element}
    </div>
  );
}
