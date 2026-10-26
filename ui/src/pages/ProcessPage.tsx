import { useMemo, useState } from "react";
import { ReactFlow } from "@xyflow/react";
import { AlertTriangle, ArrowLeft, CircleAlert, Info, ListChecks, Pencil, RotateCw, ScrollText, X } from "lucide-react";
import { useDescriptors, useProcess } from "../api/hooks";
import type { NodeTypeDescriptor, ProcessDetails, ScenarioDefinition, Step } from "../api/types";
import { processIcons, ProcessActions, ProcessStatusBadge, StepStateBadge } from "../components/process";
import { useRunScenario } from "../components/RunDialog";
import { Button, Card, Empty, ErrorState, Loading } from "../components/ui";
import { ru } from "../i18n/ru";
import { durationBetween, formatDateTime, formatMoment, preview } from "../lib/format";
import { toFlowEdges, toFlowNodes, type PepaEdge, type PepaNode } from "../lib/graph";
import { attentionAdvice, explainProcess, isActive, processTone, type Tone } from "../lib/status";
import { navigate } from "../router";
import { FlowContext, nodeTypes, type NodeRunState } from "./editor/NodeCard";

const toneColor: Record<Tone, string> = {
  info: "var(--info)",
  warn: "var(--warn)",
  neutral: "var(--neutral)",
  danger: "var(--danger)",
  success: "var(--success)",
  muted: "var(--muted)",
  attention: "var(--attention)",
};

const toneSoft: Record<Tone, string> = {
  info: "var(--info-soft)",
  warn: "var(--warn-soft)",
  neutral: "var(--neutral-soft)",
  danger: "var(--danger-soft)",
  success: "var(--success-soft)",
  muted: "var(--muted-soft)",
  attention: "var(--attention-soft)",
};

export function ProcessPage({ id }: { id: string }) {
  const details = useProcess(id);
  const descriptors = useDescriptors();
  const runner = useRunScenario();
  const [selectedNode, setSelectedNode] = useState<string | null>(null);
  const [selectedStep, setSelectedStep] = useState<number | null>(null);

  if (details.isLoading) return <div className="page"><Loading /></div>;
  if (details.error) return <div className="page"><ErrorState error={details.error} onRetry={() => details.refetch()} /></div>;
  const data = details.data!;
  const p = data.process;
  const explanation = explainProcess(p);
  const advice = attentionAdvice(p);
  const tone = processTone[p.status];
  const StatusIcon = processIcons[p.status];
  const steps = selectedNode ? data.steps.filter((s) => s.nodeId === selectedNode) : data.steps;
  const step = data.steps.find((s) => s.id === selectedStep) ?? null;

  return (
    <div className="page">
      <div className="row">
        <Button variant="ghost" size="small" icon={ArrowLeft} onClick={() => navigate(isActive(p.status) ? { page: "runs" } : { page: "history" })}>
          {isActive(p.status) ? "Выполнение" : "История"}
        </Button>
      </div>
      <header className="page-header">
        <div>
          <div className="row">
            <h1>{p.scenarioName}</h1>
            <ProcessStatusBadge status={p.status} />
          </div>
          <p className="subtitle">
            Процесс <span className="mono">#{p.shortId}</span> · запущен {formatDateTime(p.createdAt)}
            {p.finishedAt && <> · завершён {formatDateTime(p.finishedAt)}</>} · длительность {durationBetween(p.createdAt, p.finishedAt)} ·
            шагов {p.stepCount} · версия сценария {p.scenarioVersion}
          </p>
        </div>
        <div className="row">
          <Button icon={Pencil} onClick={() => navigate({ page: "editor", id: p.scenarioId })}>Сценарий</Button>
          {!isActive(p.status) && <Button icon={RotateCw} onClick={() => runner.run(p.scenarioId)}>Запустить снова</Button>}
        </div>
      </header>

      <div className="card process-hero" style={{ borderColor: p.status === "needsAttention" ? "var(--attention)" : undefined }}>
        <div className="icon-wrap" style={{ background: toneSoft[tone], color: toneColor[tone] }}>
          <StatusIcon size={20} />
        </div>
        <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
          <h2>{explanation.title}</h2>
          {explanation.text && <p style={{ color: "var(--text-2)" }}>{explanation.text}</p>}
          {advice && <p className="small muted">{advice}</p>}
          {p.allowedActions.length > 0 && <div style={{ marginTop: 6 }}><ProcessActions process={p} /></div>}
        </div>
      </div>

      <Card title="Схема" actions={selectedNode && <Button size="small" variant="ghost" icon={X} onClick={() => setSelectedNode(null)}>Показать все шаги</Button>}>
        <ProcessGraph definition={data.definition} details={data} descriptors={descriptors} selected={selectedNode} onSelect={(nodeId) => { setSelectedNode(nodeId); setSelectedStep(null); }} />
        <div className="small muted" style={{ padding: "8px 14px", borderTop: "1px solid var(--border)" }}>
          Цветом отмечен итог последнего выполнения каждого блока, «×N» — сколько раз он выполнялся. Нажмите на блок, чтобы увидеть только его шаги.
        </div>
      </Card>

      <div className="grid-2" style={{ gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)", alignItems: "start" }}>
        <div className="stack" style={{ gap: 16 }}>
          {step && <StepDetails step={step} descriptor={descriptors.get(step.nodeType)} onClose={() => setSelectedStep(null)} />}
          <Card title={<h2 className="row"><ListChecks size={16} /> Шаги{selectedNode ? " блока" : ""} · {steps.length}</h2>}>
            <div style={{ padding: "6px 16px 12px", maxHeight: 640, overflow: "auto" }}>
              {steps.length === 0 ? (
                <Empty icon={ListChecks} title="Шагов пока нет" />
              ) : (
                <div className="timeline">
                  {[...steps].reverse().map((s) => (
                    <div key={s.id} className={`timeline-item${s.id === selectedStep ? " selected" : ""}`}>
                      <span className="timeline-dot" style={{ background: toneSoft[stepToneOf(s)], color: toneColor[stepToneOf(s)] }}>●</span>
                      <div className="timeline-body" onClick={() => setSelectedStep(s.id === selectedStep ? null : s.id)}>
                        <div className="row wrap">
                          <strong>{s.nodeLabel}</strong>
                          <StepStateBadge state={s.state} />
                          {s.attempt > 1 && <span className="small faint">попытка {s.attempt}</span>}
                        </div>
                        <div className="small faint">{formatMoment(s.startedAt ?? s.plannedAt)}{s.finishedAt && s.startedAt ? ` · ${durationBetween(s.startedAt, s.finishedAt)}` : ""}</div>
                        {s.error && <div className="small" style={{ color: s.state === "failed" || s.state === "unknown" ? "var(--danger)" : "var(--muted)" }}>{s.error}</div>}
                        {s.resolution && <div className="small muted">{s.resolution}</div>}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </Card>
        </div>

        <div className="stack" style={{ gap: 16 }}>
          <Card title={<h2 className="row"><ScrollText size={16} /> Журнал</h2>}>
            <div style={{ padding: "4px 16px 10px", maxHeight: 420, overflow: "auto" }}>
              {data.journal.length === 0 ? (
                <Empty icon={ScrollText} title="Записей нет" />
              ) : (
                [...data.journal].reverse().map((j) => (
                  <div key={j.id} className={`journal-row ${j.level}`}>
                    <span className="time">{formatMoment(j.at)}</span>
                    {j.level === "error" ? <CircleAlert size={15} /> : j.level === "warning" ? <AlertTriangle size={15} /> : <Info size={15} />}
                    <span>{j.message}</span>
                  </div>
                ))
              )}
            </div>
          </Card>
          <Card title="Переменные процесса">
            <div style={{ padding: 14 }} className="stack">
              {Object.keys(data.variables).length === 0 ? (
                <span className="small faint">Переменных нет.</span>
              ) : (
                <dl className="kv">
                  {Object.entries(data.variables).map(([name, value]) => (
                    <FragmentKV key={name} name={name} value={preview(value, 400)} />
                  ))}
                </dl>
              )}
              {data.lastError && (
                <div className="small">
                  <div className="muted">Последняя ошибка (<code>lastError</code>):</div>
                  <pre className="json">{JSON.stringify(data.lastError, null, 2)}</pre>
                </div>
              )}
            </div>
          </Card>
        </div>
      </div>
      {runner.element}
    </div>
  );
}

function FragmentKV({ name, value }: { name: string; value: string }) {
  return (
    <>
      <dt>{name}</dt>
      <dd>{value}</dd>
    </>
  );
}

function stepToneOf(step: Step): Tone {
  switch (step.state) {
    case "succeeded":
      return "success";
    case "failed":
      return "danger";
    case "unknown":
      return "attention";
    case "running":
      return "info";
    case "waiting":
      return "warn";
    default:
      return "muted";
  }
}

function StepDetails({ step, descriptor, onClose }: { step: Step; descriptor: NodeTypeDescriptor | undefined; onClose: () => void }) {
  return (
    <Card title={`Шаг: ${step.nodeLabel}`} actions={<Button size="small" variant="ghost" iconOnly icon={X} title="Закрыть" onClick={onClose} />}>
      <div className="stack" style={{ padding: 14 }}>
        <div className="row wrap">
          <StepStateBadge state={step.state} />
          <span className="small muted">{descriptor?.title ?? step.nodeType}</span>
          {step.attempt > 1 && <span className="small faint">попытка {step.attempt}</span>}
          {step.effect === "external" && <span className="small faint">· внешнее действие</span>}
        </div>
        <dl className="kv">
          <dt>Запланирован</dt><dd>{formatDateTime(step.plannedAt)}</dd>
          <dt>Начат</dt><dd>{formatDateTime(step.startedAt)}</dd>
          <dt>Завершён</dt><dd>{formatDateTime(step.finishedAt)}</dd>
          {step.port && <><dt>Выход</dt><dd>{descriptor?.outputs.find((o) => o.id === step.port)?.label ?? step.port}</dd></>}
        </dl>
        {step.error && <div className="callout danger small"><CircleAlert size={16} className="callout-icon" /><span>{step.error}</span></div>}
        {step.resolution && <div className="callout info small"><Info size={16} className="callout-icon" /><span>{step.resolution}</span></div>}
        {step.input && Object.keys(step.input).length > 0 && (
          <div className="small">
            <div className="muted">Параметры (уже с подставленными значениями):</div>
            <pre className="json">{JSON.stringify(step.input, null, 2)}</pre>
          </div>
        )}
        {step.output && (
          <div className="small">
            <div className="muted">Результат:</div>
            <pre className="json">{JSON.stringify(step.output, null, 2)}</pre>
          </div>
        )}
        <span className="small faint">{ru.stepState[step.state]} · шаг №{step.id}</span>
      </div>
    </Card>
  );
}

function ProcessGraph({ definition, details, descriptors, selected, onSelect }: {
  definition: ScenarioDefinition;
  details: ProcessDetails;
  descriptors: Map<string, NodeTypeDescriptor>;
  selected: string | null;
  onSelect: (nodeId: string | null) => void;
}) {
  const nodes = useMemo<PepaNode[]>(
    () => toFlowNodes(definition).map((n) => ({ ...n, selected: n.id === selected, draggable: false, connectable: false })),
    [definition, selected],
  );
  const edges = useMemo<PepaEdge[]>(() => toFlowEdges(definition).map((e) => ({ ...e, selectable: false })), [definition]);
  const states = useMemo(() => {
    const map = new Map<string, NodeRunState>();
    for (const step of details.steps) {
      const existing = map.get(step.nodeId);
      map.set(step.nodeId, { state: step.state, count: (existing?.count ?? 0) + 1 });
    }
    return map;
  }, [details.steps]);

  return (
    <div className="process-graph flow-readonly">
      <FlowContext.Provider value={{ descriptors, states }}>
        <ReactFlow<PepaNode, PepaEdge>
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable
          onNodeClick={(_, node) => onSelect(node.id === selected ? null : node.id)}
          onPaneClick={() => onSelect(null)}
          fitView
          fitViewOptions={{ padding: 0.12, maxZoom: 0.9, minZoom: 0.5 }}
          minZoom={0.15}
          proOptions={{ hideAttribution: true }}
        />
      </FlowContext.Provider>
    </div>
  );
}
