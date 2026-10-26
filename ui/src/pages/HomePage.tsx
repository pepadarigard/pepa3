import { AlertTriangle, CheckCircle2, FolderOpen, Hourglass, Loader2, Play, Plus, RotateCcw, Workflow } from "lucide-react";
import { useInfo, useOverview, useScenarios } from "../api/hooks";
import { ProcessActions, ProcessStatusBadge, ProcessTable } from "../components/process";
import { useRunScenario } from "../components/RunDialog";
import { Button, Callout, Card, Empty, ErrorState, Loading } from "../components/ui";
import { formatMoment } from "../lib/format";
import { native } from "../lib/native";
import { attentionAdvice, explainProcess } from "../lib/status";
import { navigate } from "../router";

export function HomePage() {
  const overview = useOverview();
  const scenarios = useScenarios();
  const info = useInfo();
  const runner = useRunScenario();

  if (overview.isLoading) return <div className="page"><Loading /></div>;
  if (overview.error) return <div className="page"><ErrorState error={overview.error} onRetry={() => overview.refetch()} /></div>;
  const data = overview.data!;
  const recovery = info.data?.recovery;
  const recovered = (recovery?.paused ?? 0) + (recovery?.unknownSteps ?? 0);

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Главная</h1>
          <p className="subtitle">Что происходит в платформе прямо сейчас.</p>
        </div>
        <div className="row">
          <Button icon={Plus} onClick={() => navigate({ page: "scenarios" })}>Новый сценарий</Button>
        </div>
      </header>

      {recovered > 0 && (
        <Callout
          tone="attention"
          title="После перезапуска процессы ждут вашей проверки"
          actions={<Button size="small" icon={RotateCcw} onClick={() => navigate({ page: "runs" })}>Открыть «Выполнение»</Button>}
        >
          Приложение закрывалось, пока работали процессы. Ничего не продолжилось само: приостановлено — {recovery?.paused ?? 0},
          шагов с неизвестным итогом — {recovery?.unknownSteps ?? 0}. Проверьте каждый процесс и возобновите вручную.
        </Callout>
      )}

      <div className="grid-stats">
        <Stat icon={Loader2} tone="var(--info)" value={data.counts.running} label="Выполняются" onClick={() => navigate({ page: "runs" })} />
        <Stat icon={Hourglass} tone="var(--warn)" value={data.counts.waiting + data.counts.paused} label="Ждут или на паузе" onClick={() => navigate({ page: "runs" })} />
        <Stat icon={AlertTriangle} tone="var(--attention)" value={data.counts.needsAttention} label="Требуют решения" onClick={() => navigate({ page: "runs" })} />
        <Stat icon={CheckCircle2} tone="var(--success)" value={data.counts.completedToday} label={`Завершено сегодня${data.counts.failedToday ? ` · с ошибкой ${data.counts.failedToday}` : ""}`} onClick={() => navigate({ page: "history" })} />
      </div>

      {data.attention.length > 0 && (
        <Card title={`Требуют решения · ${data.attention.length}`}>
          <div className="stack" style={{ padding: 14 }}>
            {data.attention.slice(0, 5).map((p) => {
              const explanation = explainProcess(p);
              return (
                <div key={p.id} className="callout attention" style={{ cursor: "pointer" }} onClick={() => navigate({ page: "process", id: p.id })}>
                  <AlertTriangle size={18} className="callout-icon" />
                  <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
                    <div className="row">
                      <strong>{p.scenarioName}</strong>
                      <span className="faint mono small">#{p.shortId}</span>
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

      <div className="grid-2">
        <Card title="Быстрый запуск" actions={<a href="#/scenarios" className="small">Все сценарии</a>}>
          {scenarios.data && scenarios.data.length > 0 ? (
            <div className="picker-list">
              {scenarios.data.slice(0, 6).map((s) => (
                <div key={s.id} className="picker-item" onClick={() => navigate({ page: "editor", id: s.id })}>
                  <Workflow size={18} className="muted" />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="truncate" style={{ fontWeight: 560 }}>{s.name}</div>
                    <div className="small faint">
                      {s.lastRun ? <>Последний запуск {formatMoment(s.lastRun.createdAt)}</> : "Ещё не запускался"}
                    </div>
                  </div>
                  {s.lastRun && <ProcessStatusBadge status={s.lastRun.status} />}
                  <Button size="small" icon={Play} disabled={s.errorCount > 0} title={s.errorCount > 0 ? "В сценарии есть ошибки" : "Запустить"}
                    onClick={(e) => { e.stopPropagation(); void runner.run(s.id); }}>
                    Запустить
                  </Button>
                </div>
              ))}
            </div>
          ) : (
            <Empty icon={Workflow} title="Сценариев пока нет">Создайте первый на странице «Сценарии».</Empty>
          )}
        </Card>

        <Card title="Сейчас работают" actions={<a href="#/runs" className="small">Выполнение</a>}>
          {data.active.length > 0 ? (
            <div className="picker-list">
              {data.active.slice(0, 6).map((p) => (
                <div key={p.id} className="picker-item" onClick={() => navigate({ page: "process", id: p.id })}>
                  <ProcessStatusBadge status={p.status} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="truncate" style={{ fontWeight: 560 }}>{p.scenarioName}</div>
                    <div className="small faint truncate">{explainProcess(p).title}</div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <Empty icon={Loader2} title="Ничего не выполняется">Запущенные процессы появятся здесь.</Empty>
          )}
        </Card>
      </div>

      <Card title="Последние завершённые" actions={<a href="#/history" className="small">История</a>}>
        {data.recent.length > 0 ? (
          <ProcessTable processes={data.recent} showActions={false} />
        ) : (
          <Empty icon={CheckCircle2} title="Завершённых запусков пока нет" />
        )}
      </Card>

      {info.data && (
        <p className="small faint row">
          <FolderOpen size={14} /> Рабочая папка файловых действий: <code>{info.data.workspaceRoot}</code>
          {native.available() && (
            <Button size="small" variant="ghost" onClick={() => native.openFolder(info.data!.workspaceRoot).catch(() => undefined)}>
              Открыть
            </Button>
          )}
        </p>
      )}
      {runner.element}
    </div>
  );
}

function Stat({ icon: Icon, tone, value, label, onClick }: { icon: typeof Loader2; tone: string; value: number; label: string; onClick?: () => void }) {
  return (
    <div className={`card stat${onClick ? " clickable" : ""}`} onClick={onClick}>
      <span className="stat-label">
        <Icon size={14} style={{ color: tone }} /> {label}
      </span>
      <span className="stat-value">{value}</span>
    </div>
  );
}
