import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import {
  Background,
  ConnectionLineType,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  applyEdgeChanges,
  applyNodeChanges,
  useReactFlow,
  type Connection,
  type EdgeChange,
  type IsValidConnection,
  type NodeChange,
  type ReactFlowInstance,
} from "@xyflow/react";
import { ArrowLeft, CalendarClock, LayoutGrid, Play, Plus, Redo2, Save, Shapes, Undo2, Workflow } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { useNodeTypes, useScenario, useScenarios } from "../../api/hooks";
import type { NodeDefinition, NodeTypes, Scenario, ValidationReport } from "../../api/types";
import { rememberEditedScenario } from "../../App";
import { useFeedback } from "../../components/feedback";
import { useRunScenario } from "../../components/RunDialog";
import { useTriggersDialog } from "../../components/triggers";
import { Badge, Button, Card, Empty, ErrorState, Loading } from "../../components/ui";
import { formatMoment } from "../../lib/format";
import { autoLayout, connect, createNode, NODE_WIDTH, newId, type PepaEdge, type PepaNode } from "../../lib/graph";
import { navigate, setLeaveGuard } from "../../router";
import { blankScenario } from "../ScenariosPage";
import { EdgeInspector, NodeInspector, ScenarioInspector } from "./Inspector";
import { IssuesPanel } from "./IssuesPanel";
import { FlowContext, nodeTypes } from "./NodeCard";
import { DRAG_TYPE, Palette } from "./Palette";
import { definitionOf, fromDefinition, snapshotOf, useEditor, type Meta } from "./useEditor";

export function EditorPage({ id }: { id?: string }) {
  if (!id) return <EditorPicker />;
  return (
    <ReactFlowProvider>
      <EditorLoader id={id} />
    </ReactFlowProvider>
  );
}

function EditorLoader({ id }: { id: string }) {
  const scenario = useScenario(id);
  const types = useNodeTypes();

  if (scenario.error instanceof ApiError && scenario.error.status === 404) {
    return <EditorPicker notice="Сценарий не найден — возможно, он удалён. Выберите другой." />;
  }
  if (scenario.error || types.error) {
    return <div className="page"><ErrorState error={scenario.error ?? types.error} onRetry={() => { void scenario.refetch(); void types.refetch(); }} /></div>;
  }
  // Открываем только свежую версию сценария, загруженную после входа в конструктор.
  if (!scenario.data || !types.data || !scenario.isFetchedAfterMount) return <div className="page"><Loading /></div>;
  return <Editor key={scenario.data.id} scenario={scenario.data} types={types.data} />;
}

function Editor({ scenario, types }: { scenario: Scenario; types: NodeTypes }) {
  const descriptors = useMemo(() => new Map(types.nodeTypes.map((d) => [d.type, d])), [types]);
  const editor = useEditor(scenario.definition);
  const { state } = editor;
  const [version, setVersion] = useState(scenario.version);
  const [savedSnapshot, setSavedSnapshot] = useState(() => snapshotOf(fromDefinition(scenario.definition)));
  const [report, setReport] = useState<ValidationReport>(scenario.validation);
  const [checking, setChecking] = useState(false);
  const [saving, setSaving] = useState(false);
  const validated = useRef({ snapshot: savedSnapshot, report: scenario.validation });
  const latestCheck = useRef(0);
  const canvas = useRef<HTMLDivElement>(null);
  const flow = useReactFlow<PepaNode, PepaEdge>();
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const runner = useRunScenario();
  const triggersDialog = useTriggersDialog();
  const dirty = editor.snapshot !== savedSnapshot;

  useEffect(() => rememberEditedScenario(scenario.id), [scenario.id]);

  // Проверка на сервере тем же валидатором, что и перед запуском. Учитывается только ответ на последний
  // запрос; для уже проверенного состояния (например, после «вернуть») результат берётся из памяти.
  useEffect(() => {
    const snapshot = editor.snapshot;
    const ticket = ++latestCheck.current;
    if (snapshot === validated.current.snapshot) {
      setReport(validated.current.report);
      setChecking(false);
      return;
    }
    setChecking(true);
    const handle = setTimeout(async () => {
      try {
        const result = await api.validate(JSON.parse(snapshot));
        if (ticket !== latestCheck.current) return;
        validated.current = { snapshot, report: result };
        setReport(result);
        setChecking(false);
      } catch {
        if (ticket === latestCheck.current) setChecking(false);
      }
    }, 400);
    return () => clearTimeout(handle);
  }, [editor.snapshot]);

  // Защита несохранённых изменений.
  useEffect(() => {
    setLeaveGuard(dirty ? () => window.confirm("Есть несохранённые изменения. Уйти без сохранения?") : null);
    if (!dirty) return () => setLeaveGuard(null);
    const onUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
    };
    window.addEventListener("beforeunload", onUnload);
    return () => {
      window.removeEventListener("beforeunload", onUnload);
      setLeaveGuard(null);
    };
  }, [dirty]);

  const issuesByNode = useMemo(() => {
    const map = new Map<string, number>();
    for (const issue of report.issues) {
      if (issue.nodeId && issue.severity === "error") map.set(issue.nodeId, (map.get(issue.nodeId) ?? 0) + 1);
    }
    return map;
  }, [report]);

  const isStart = useCallback((nodeId: string) => {
    const node = editor.state.nodes.find((n) => n.id === nodeId);
    return !!node && descriptors.get(node.data.definition.type)?.isStart === true;
  }, [descriptors, editor.state.nodes]);

  // ───────────── Сохранение и запуск ─────────────

  const save = useCallback(async (): Promise<Scenario | null> => {
    setSaving(true);
    try {
      const definition = definitionOf(editor.state);
      const saved = await api.saveScenario(scenario.id, definition, version);
      setVersion(saved.version);
      setSavedSnapshot(JSON.stringify(definition));
      validated.current = { snapshot: JSON.stringify(definition), report: saved.validation };
      if (JSON.stringify(definitionOf(editor.state)) === validated.current.snapshot) setReport(saved.validation);
      queryClient.setQueryData(["scenario", scenario.id], saved);
      void queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      feedback.toast(saved.validation.isValid ? "Сохранено" : "Сохранено как черновик — в сценарии есть ошибки", saved.validation.isValid ? "success" : "info");
      return saved;
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        feedback.toast("Сценарий изменили в другом окне. Скопируйте свои правки и откройте сценарий заново.", "error");
      } else {
        feedback.error(e);
      }
      return null;
    } finally {
      setSaving(false);
    }
  }, [editor.state, feedback, queryClient, scenario.id, version]);

  const run = async () => {
    let valid = report.isValid;
    if (dirty) {
      const saved = await save();
      if (!saved) return;
      valid = saved.validation.isValid;
    }
    if (!valid) {
      feedback.toast("Исправьте ошибки в сценарии — список внизу схемы.", "error");
      return;
    }
    await runner.run(scenario.id);
  };

  // ───────────── Изменения схемы ─────────────

  const onNodesChange = useCallback((changes: NodeChange<PepaNode>[]) => {
    const allowed = changes.filter((c) => !(c.type === "remove" && isStart(c.id)));
    if (allowed.length !== changes.length) feedback.toast("Блок «Начало» удалить нельзя.", "info");
    if (allowed.some((c) => c.type === "remove")) {
      editor.commit((s) => {
        const nodes = applyNodeChanges(allowed, s.nodes);
        const ids = new Set(nodes.map((n) => n.id));
        return { ...s, nodes, edges: s.edges.filter((e) => ids.has(e.source) && ids.has(e.target)) };
      });
    } else if (allowed.length > 0) {
      editor.replace((s) => ({ ...s, nodes: applyNodeChanges(allowed, s.nodes) }));
    }
  }, [editor, feedback, isStart]);

  const onEdgesChange = useCallback((changes: EdgeChange<PepaEdge>[]) => {
    const update = (s: typeof state) => ({ ...s, edges: applyEdgeChanges(changes, s.edges) });
    if (changes.some((c) => c.type === "remove")) editor.commit(update);
    else editor.replace(update);
  }, [editor]);

  const onConnect = useCallback((c: Connection) => {
    editor.commit((s) => ({ ...s, edges: connect(s.edges, c.source, c.sourceHandle ?? "out", c.target) }));
  }, [editor]);

  const isValidConnection: IsValidConnection<PepaEdge> = useCallback(
    (c) => c.source !== c.target && !isStart(c.target),
    [isStart],
  );

  const addNode = useCallback((type: string, position?: { x: number; y: number }) => {
    const descriptor = descriptors.get(type);
    if (!descriptor) return;
    let at = position;
    if (!at) {
      const rect = canvas.current?.getBoundingClientRect();
      const center = rect ? flow.screenToFlowPosition({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }) : { x: 0, y: 0 };
      at = freeSpot(editor.state.nodes, { x: Math.round(center.x - NODE_WIDTH / 2), y: Math.round(center.y - 40) });
    }
    const definition = createNode(descriptor, at);
    editor.commit((s) => ({
      ...s,
      nodes: [
        ...s.nodes.map((n) => ({ ...n, selected: false })),
        { id: definition.id, type: "pepa", position: at, data: { definition }, selected: true },
      ],
      edges: s.edges.map((e) => ({ ...e, selected: false })),
    }));
  }, [descriptors, editor, flow]);

  const onDrop = (e: DragEvent) => {
    const type = e.dataTransfer.getData(DRAG_TYPE);
    if (!type) return;
    e.preventDefault();
    const p = flow.screenToFlowPosition({ x: e.clientX, y: e.clientY });
    addNode(type, { x: p.x - NODE_WIDTH / 2, y: p.y - 24 });
  };

  const updateNode = useCallback((nodeId: string, updater: (n: NodeDefinition) => NodeDefinition, key: string) => {
    editor.commit((s) => ({
      ...s,
      nodes: s.nodes.map((n) => (n.id === nodeId ? { ...n, data: { definition: updater(n.data.definition) } } : n)),
    }), `${nodeId}.${key}`);
  }, [editor]);

  const updateMeta = useCallback((updater: (m: Meta) => Meta, key: string) => {
    editor.commit((s) => ({ ...s, meta: updater(s.meta) }), `meta.${key}`);
  }, [editor]);

  const createVariable = (name: string) =>
    updateMeta((m) => (m.variables.some((v) => v.name === name) ? m : { ...m, variables: [...m.variables, { name, type: "any", default: null, description: "", isInput: false }] }), "var.create");

  const removeNode = (nodeId: string) => {
    if (isStart(nodeId)) return;
    editor.commit((s) => ({ ...s, nodes: s.nodes.filter((n) => n.id !== nodeId), edges: s.edges.filter((e) => e.source !== nodeId && e.target !== nodeId) }));
  };

  const removeEdge = (edgeId: string) => editor.commit((s) => ({ ...s, edges: s.edges.filter((e) => e.id !== edgeId) }));

  const duplicateSelected = useCallback(() => {
    const selected = editor.state.nodes.filter((n) => n.selected && !isStart(n.id));
    if (selected.length !== 1) return;
    const source = selected[0];
    const copy: NodeDefinition = { ...structuredClone(source.data.definition), id: newId("n") };
    const position = { x: source.position.x + 40, y: source.position.y + 40 };
    editor.commit((s) => ({
      ...s,
      nodes: [...s.nodes.map((n) => ({ ...n, selected: false })), { id: copy.id, type: "pepa", position, data: { definition: { ...copy, position } }, selected: true }],
    }));
  }, [editor, isStart]);

  const selectNode = (nodeId: string) => {
    editor.replace((s) => ({
      ...s,
      nodes: s.nodes.map((n) => ({ ...n, selected: n.id === nodeId })),
      edges: s.edges.map((e) => ({ ...e, selected: false })),
    }));
    void flow.fitView({ nodes: [{ id: nodeId }], duration: 300, maxZoom: 1.1, padding: 0.6 });
  };

  const layout = () => {
    const start = editor.state.nodes.find((n) => isStart(n.id));
    const positions = autoLayout(editor.state.nodes, editor.state.edges, start?.id);
    editor.commit((s) => ({ ...s, nodes: s.nodes.map((n) => ({ ...n, position: positions.get(n.id) ?? n.position })) }));
    setTimeout(() => void flow.fitView({ duration: 300, padding: 0.15 }), 30);
  };

  // ───────────── Клавиатура ─────────────

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      if (!mod) return;
      const key = e.key.toLowerCase();
      if (key === "s" || key === "ы") {
        e.preventDefault();
        void save();
        return;
      }
      const typing = (e.target as HTMLElement | null)?.closest("input, textarea, select, [contenteditable]");
      if (typing) return;
      if ((key === "z" || key === "я") && !e.shiftKey) {
        e.preventDefault();
        editor.undo();
      } else if (key === "y" || key === "н" || ((key === "z" || key === "я") && e.shiftKey)) {
        e.preventDefault();
        editor.redo();
      } else if (key === "d" || key === "в") {
        e.preventDefault();
        duplicateSelected();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [duplicateSelected, editor, save]);

  // ───────────── Отрисовка ─────────────

  const selectedNodes = state.nodes.filter((n) => n.selected);
  const selectedNode = selectedNodes.length === 1 ? selectedNodes[0] : undefined;
  const selectedEdge = !selectedNode ? state.edges.find((e) => e.selected) : undefined;
  const titleOf = (nodeId: string) => {
    const n = state.nodes.find((x) => x.id === nodeId);
    return n ? `«${n.data.definition.label || descriptors.get(n.data.definition.type)?.title || n.data.definition.type}»` : nodeId;
  };

  return (
    <div className="editor">
      <div className="editor-top">
        <Button variant="ghost" size="small" icon={ArrowLeft} onClick={() => navigate({ page: "scenarios" })}>Сценарии</Button>
        <div className="editor-title truncate" title={state.meta.name}>{state.meta.name || "Без названия"}</div>
        <Badge tone={dirty ? "warn" : "success"}>{dirty ? "Не сохранено" : "Сохранено"}</Badge>
        <span className="small faint">версия {version} · изменён {formatMoment(scenario.updatedAt)}</span>
        <span className="spacer" />
        <Button size="small" variant="ghost" iconOnly icon={Undo2} title="Отменить (Ctrl+Z)" disabled={!editor.canUndo} onClick={editor.undo} />
        <Button size="small" variant="ghost" iconOnly icon={Redo2} title="Вернуть (Ctrl+Y)" disabled={!editor.canRedo} onClick={editor.redo} />
        <Button size="small" icon={LayoutGrid} title="Расставить блоки автоматически" onClick={layout}>Упорядочить</Button>
        <Button size="small" icon={CalendarClock} title="Запуск по расписанию и по событиям"
          onClick={() => triggersDialog.open(scenario.id, state.meta.name || scenario.name)}>
          Автозапуск
        </Button>
        <Button size="small" icon={Save} busy={saving} disabled={!dirty} onClick={() => void save()} title="Сохранить (Ctrl+S)">Сохранить</Button>
        <Button size="small" variant="primary" icon={Play} onClick={() => void run()} title={report.isValid ? "Запустить новый процесс" : "Есть ошибки — запуск невозможен"}>
          Запустить
        </Button>
      </div>
      <div className="editor-body">
        <Palette categories={types.categories} descriptors={types.nodeTypes} onAdd={(type) => addNode(type)} />
        <div className="canvas" ref={canvas} onDrop={onDrop} onDragOver={(e) => {
          if (e.dataTransfer.types.includes(DRAG_TYPE)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "move";
          }
        }}>
          <FlowContext.Provider value={{ descriptors, issues: issuesByNode }}>
            <ReactFlow<PepaNode, PepaEdge>
              nodes={state.nodes}
              edges={state.edges}
              nodeTypes={nodeTypes}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onConnect={onConnect}
              isValidConnection={isValidConnection}
              onNodeDragStart={() => editor.checkpoint()}
              deleteKeyCode={["Delete", "Backspace"]}
              connectionLineType={ConnectionLineType.SmoothStep}
              defaultEdgeOptions={{ type: "smoothstep" }}
              snapToGrid
              snapGrid={[10, 10]}
              onInit={(instance) => openReadable(instance, canvas.current, isStart)}
              minZoom={0.2}
              maxZoom={1.8}
              proOptions={{ hideAttribution: true }}
            >
              <Background gap={20} size={1.2} />
              <Controls showInteractive={false} />
              <MiniMap pannable zoomable />
            </ReactFlow>
          </FlowContext.Provider>
          <IssuesPanel report={report} checking={checking} onSelect={selectNode} />
        </div>
        <aside className="inspector" aria-label="Свойства">
          {selectedNode ? (
            <NodeInspector
              key={selectedNode.id}
              node={selectedNode.data.definition}
              descriptor={descriptors.get(selectedNode.data.definition.type)}
              variables={state.meta.variables}
              issues={report.issues.filter((i) => i.nodeId === selectedNode.id)}
              onChange={(updater, key) => updateNode(selectedNode.id, updater, key)}
              onCreateVariable={createVariable}
              onDelete={() => removeNode(selectedNode.id)}
            />
          ) : selectedEdge ? (
            <EdgeInspector
              edge={selectedEdge}
              sourceTitle={titleOf(selectedEdge.source)}
              targetTitle={titleOf(selectedEdge.target)}
              portLabel={descriptors.get(state.nodes.find((n) => n.id === selectedEdge.source)?.data.definition.type ?? "")?.outputs.find((p) => p.id === (selectedEdge.sourceHandle ?? "out"))?.label ?? selectedEdge.sourceHandle ?? "out"}
              onDelete={() => removeEdge(selectedEdge.id)}
            />
          ) : (
            <ScenarioInspector meta={state.meta} issues={report.issues} onChange={updateMeta} />
          )}
        </aside>
      </div>
      {runner.element}
      {triggersDialog.element}
    </div>
  );
}

/** Ближайшее к желаемому место, где новый блок не накрывает существующие. */
function freeSpot(nodes: PepaNode[], wanted: { x: number; y: number }): { x: number; y: number } {
  const height = 96;
  const overlaps = (p: { x: number; y: number }) =>
    nodes.some((n) => Math.abs(n.position.x - p.x) < NODE_WIDTH + 20 && Math.abs(n.position.y - p.y) < height + 20);
  for (let ring = 0; ring < 12; ring++) {
    for (const dy of ring === 0 ? [0] : [ring * 130, -ring * 130]) {
      const candidate = { x: wanted.x, y: wanted.y + dy };
      if (!overlaps(candidate)) return candidate;
    }
  }
  return { x: wanted.x, y: Math.max(0, ...nodes.map((n) => n.position.y)) + 150 };
}

/**
 * Открыть схему в читаемом масштабе: если весь сценарий умещается не мельче ~60 % — показать целиком,
 * иначе показать начало (от блока «Начало»), остальное — прокруткой и мини-картой.
 */
function openReadable(instance: ReactFlowInstance<PepaNode, PepaEdge>, canvas: HTMLDivElement | null, isStart: (id: string) => boolean) {
  const nodes = instance.getNodes();
  if (nodes.length === 0 || !canvas) return;
  const minX = Math.min(...nodes.map((n) => n.position.x));
  const maxX = Math.max(...nodes.map((n) => n.position.x + NODE_WIDTH));
  const readable = 0.6;
  if ((maxX - minX) * readable <= canvas.clientWidth * 0.92) {
    void instance.fitView({ padding: 0.2, maxZoom: 1 });
    return;
  }
  const start = nodes.find((n) => isStart(n.id)) ?? nodes.reduce((a, b) => (a.position.x <= b.position.x ? a : b));
  const zoom = 0.8;
  const visibleWidth = canvas.clientWidth / zoom;
  const firstNodes = nodes.filter((n) => n.position.x >= start.position.x - 40 && n.position.x + NODE_WIDTH <= start.position.x + visibleWidth - 40);
  void instance.fitView({ nodes: firstNodes.map((n) => ({ id: n.id })), padding: 0.08, maxZoom: zoom, minZoom: readable });
}

function EditorPicker({ notice }: { notice?: string }) {
  const scenarios = useScenarios();
  const feedback = useFeedback();
  const queryClient = useQueryClient();

  const create = async () => {
    try {
      const created = await api.createScenario(blankScenario());
      void queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      navigate({ page: "editor", id: created.id });
    } catch (e) {
      feedback.error(e);
    }
  };

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Конструктор</h1>
          <p className="subtitle">Выберите сценарий, чтобы открыть его схему, или создайте новый.</p>
        </div>
        <Button variant="primary" icon={Plus} onClick={create}>Новый сценарий</Button>
      </header>
      {notice && <div className="callout attention small">{notice}</div>}
      <Card>
        {scenarios.isLoading ? (
          <Loading />
        ) : (scenarios.data ?? []).length === 0 ? (
          <Empty icon={Shapes} title="Сценариев пока нет">Нажмите «Новый сценарий».</Empty>
        ) : (
          <div className="picker-list">
            {scenarios.data!.map((s) => (
              <div key={s.id} className="picker-item" onClick={() => navigate({ page: "editor", id: s.id })}>
                <Workflow size={18} className="muted" />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontWeight: 560 }}>{s.name}</div>
                  <div className="small faint truncate">{s.description || "Без описания"}</div>
                </div>
                {s.errorCount > 0 && <Badge tone="danger" dot={false}>ошибок: {s.errorCount}</Badge>}
                <span className="small faint">{formatMoment(s.updatedAt)}</span>
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  );
}
