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
import { ArrowLeft, Bug, CalendarClock, CircleDot, ClipboardPaste, Copy, Group, LayoutGrid, Play, Plus, Redo2, Save, Search, Shapes, Sparkles, Undo2, Workflow } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { useNodeTypes, useScenario, useScenarios } from "../../api/hooks";
import type { AssistantDraft, NodeDefinition, NodeTypeDescriptor, NodeTypes, Scenario, ValidationReport } from "../../api/types";
import { rememberEditedScenario } from "../../App";
import { useFeedback } from "../../components/feedback";
import { useRunScenario } from "../../components/RunDialog";
import { useTriggersDialog } from "../../components/triggers";
import { Badge, Button, Card, Empty, ErrorState, Loading } from "../../components/ui";
import { formatMoment } from "../../lib/format";
import { copyBlocks, loadClipboard, pasteBlocks, saveClipboard, type BlockClipboard } from "../../lib/clipboard";
import { autoLayout, connect, createNode, newId, NODE_WIDTH, nodeSummary, toFlowEdge, type PepaEdge, type PepaNode } from "../../lib/graph";
import {
  addGroup, canvasView, groupIdOf, groupOfNode, isGroupNodeId, moveMembers, nextGroupTitle, pruneGroups, readGroups, writeGroups,
  type CanvasNode, type EditorGroup,
} from "../../lib/groups";
import { navigate, setLeaveGuard } from "../../router";
import { blankScenario } from "../ScenariosPage";
import { EdgeInspector, NodeInspector, ScenarioInspector } from "./Inspector";
import { IssuesPanel } from "./IssuesPanel";
import { AssistantDialog } from "./AssistantDialog";
import { RecorderDialog } from "./RecorderDialog";
import { withFreeVariables } from "../../lib/recording";
import { canvasNodeTypes, GroupActionsContext, type GroupActions } from "./GroupNodes";
import { FlowContext } from "./NodeCard";
import { DRAG_TYPE, Palette } from "./Palette";
import { definitionOf, fromDefinition, snapshotOf, useEditor, type EditorState, type Meta } from "./useEditor";

/** Состояние с изменёнными группами (данные редактора сценария). */
function withGroups(s: EditorState, update: (groups: EditorGroup[]) => EditorGroup[]): EditorState {
  const before = readGroups(s.meta.editor);
  const after = update(before);
  return after === before ? s : { ...s, meta: { ...s.meta, editor: writeGroups(s.meta.editor, after) } };
}

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

  // ───────────── Группы ─────────────

  const groups = useMemo(() => readGroups(state.meta.editor), [state.meta.editor]);
  const view = useMemo(() => canvasView(state.nodes, state.edges, groups,
    (n) => n.data.definition.label || descriptors.get(n.data.definition.type)?.title || n.data.definition.type, issuesByNode),
  [descriptors, groups, issuesByNode, state.edges, state.nodes]);
  // Где рамки и свёрнутые группы стоят на холсте сейчас — от этого считается сдвиг при перетаскивании.
  const shownAt = useRef(new Map<string, { x: number; y: number }>());
  shownAt.current = new Map(view.nodes.filter((n) => isGroupNodeId(n.id)).map((n) => [n.id, n.position]));
  const dragFrom = useRef(new Map<string, { x: number; y: number }>());

  const createGroup = useCallback(() => {
    const hidden = new Set(groups.filter((g) => g.collapsed).flatMap((g) => g.nodeIds));
    const ids = editor.state.nodes.filter((n) => n.selected && !hidden.has(n.id)).map((n) => n.id);
    if (ids.length === 0) {
      feedback.toast("Выберите блоки для группы: щелчки с Ctrl или рамка с Shift.", "info");
      return;
    }
    editor.commit((s) => withGroups(s, (gs) => addGroup(gs, ids, newId("g"), nextGroupTitle(gs))));
    feedback.toast("Группа создана. Дважды щёлкните её название, чтобы переименовать; её можно свернуть в один блок.", "info");
  }, [editor, feedback, groups]);

  const groupActions = useMemo<GroupActions>(() => ({
    editable: true,
    rename: (groupId, title) => editor.commit((s) => withGroups(s, (gs) => gs.map((g) => (g.id === groupId ? { ...g, title } : g))), `group.${groupId}.title`),
    toggle: (groupId) => editor.commit((s) => {
      const target = readGroups(s.meta.editor).find((g) => g.id === groupId);
      const next = withGroups(s, (gs) => gs.map((g) => (g.id === groupId ? { ...g, collapsed: !g.collapsed } : g)));
      // Свёрнутые блоки не остаются выбранными: иначе Delete удалил бы невидимое.
      return target && !target.collapsed
        ? { ...next, nodes: next.nodes.map((n) => (target.nodeIds.includes(n.id) && n.selected ? { ...n, selected: false } : n)) }
        : next;
    }),
    ungroup: (groupId) => editor.commit((s) => withGroups(s, (gs) => gs.filter((g) => g.id !== groupId))),
  }), [editor]);

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

  const run = async (options?: { stepMode?: boolean }) => {
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
    await runner.run(scenario.id, options);
  };

  // ───────────── Изменения схемы ─────────────

  const onNodesChange = useCallback((all: NodeChange<CanvasNode>[]) => {
    const changes = all.filter((c) => c.type === "add" || !isGroupNodeId(c.id)) as NodeChange<PepaNode>[];
    // Рамку или свёрнутую группу тащат — вместе с ней едут её блоки (кроме тех, что выбраны и едут сами).
    const movingThemselves = new Set(changes.filter((c) => c.type === "position").map((c) => c.id));
    for (const change of all) {
      if (change.type !== "position" || !isGroupNodeId(change.id)) continue;
      if (!change.position) {
        if (change.dragging === false) dragFrom.current.delete(change.id);
        continue;
      }
      const base = dragFrom.current.get(change.id) ?? shownAt.current.get(change.id);
      const group = groups.find((g) => g.id === groupIdOf(change.id));
      if (!base || !group) continue;
      // Блоки сдвигаются на целые клетки сетки — чтобы оставались выровненными.
      const dx = Math.round((change.position.x - base.x) / 10) * 10;
      const dy = Math.round((change.position.y - base.y) / 10) * 10;
      if (dx === 0 && dy === 0) continue;
      dragFrom.current.set(change.id, { x: base.x + dx, y: base.y + dy });
      editor.replace((s) => ({ ...s, nodes: moveMembers(s.nodes, group, dx, dy, movingThemselves) }));
    }

    const allowed = changes.filter((c) => !(c.type === "remove" && isStart(c.id)));
    if (allowed.length !== changes.length) feedback.toast("Блок «Начало» удалить нельзя.", "info");
    if (allowed.some((c) => c.type === "remove")) {
      editor.commit((s) => {
        const nodes = applyNodeChanges(allowed, s.nodes);
        const ids = new Set(nodes.map((n) => n.id));
        return withGroups({ ...s, nodes, edges: s.edges.filter((e) => ids.has(e.source) && ids.has(e.target)) }, (gs) => pruneGroups(gs, ids));
      });
    } else if (allowed.length > 0) {
      editor.replace((s) => ({ ...s, nodes: applyNodeChanges(allowed, s.nodes) }));
    }
  }, [editor, feedback, groups, isStart]);

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
    editor.commit((s) => withGroups(
      { ...s, nodes: s.nodes.filter((n) => n.id !== nodeId), edges: s.edges.filter((e) => e.source !== nodeId && e.target !== nodeId) },
      (gs) => pruneGroups(gs, new Set(s.nodes.filter((n) => n.id !== nodeId).map((n) => n.id)))));
  };

  const removeEdge = (edgeId: string) => editor.commit((s) => ({ ...s, edges: s.edges.filter((e) => e.id !== edgeId) }));

  // ───────────── Копирование и вставка ─────────────

  /**
   * Вставить блоки из буфера: новые идентификаторы, связи внутри группы и недостающие переменные. С <paramref name="entryId"/>
   * первый блок группы подключается к «Началу», если из «Начала» ещё ничего не выходит.
   */
  const insertBlocks = useCallback((data: BlockClipboard, at: { x: number; y: number }, entryId: string | null = null) => {
    let target = at;
    const first = data.nodes[0];
    const minX = Math.min(...data.nodes.map((n) => n.position?.x ?? 0));
    const minY = Math.min(...data.nodes.map((n) => n.position?.y ?? 0));
    const occupied = (p: { x: number; y: number }) => editor.state.nodes.some((n) =>
      Math.abs(n.position.x - (p.x + (first.position?.x ?? 0) - minX)) < 5 && Math.abs(n.position.y - (p.y + (first.position?.y ?? 0) - minY)) < 5);
    for (let i = 0; i < 20 && occupied(target); i++) target = { x: target.x + 40, y: target.y + 40 };
    const pasted = pasteBlocks(data, target, editor.state.meta.variables);
    const entryIndex = entryId ? data.nodes.findIndex((n) => n.id === entryId) : -1;
    const start = editor.state.nodes.find((n) => isStart(n.id));
    const startOut = start ? editor.state.edges.filter((e) => e.source === start.id) : [];
    // Новый сценарий — заготовка «Начало → Конец»: её заменяют вставленные блоки (отменить — Ctrl+Z). Только нетронутая:
    // «Завершение» с подписью, заметкой или своим сообщением — уже часть сценария, его не удаляем.
    const end = startOut.length === 1 ? editor.state.nodes.find((n) => n.id === startOut[0].target)?.data.definition : undefined;
    const blankEnd = !!end && end.type === "end" && !end.label && !end.note &&
      Object.entries(end.config ?? {}).every(([key, value]) => value === "" || value === null || value === undefined || (key === "outcome" && value === "success"));
    const placeholder = editor.state.nodes.length === 2 && blankEnd ? startOut[0].target : null;
    const connect = entryIndex >= 0 && !!start && (startOut.length === 0 || placeholder !== null);
    const startEdge = connect ? [toFlowEdge({ id: newId("e"), source: start.id, sourcePort: "out", target: pasted.nodes[entryIndex].id })] : [];
    editor.commit((s) => ({
      meta: pasted.variables.length > 0 ? { ...s.meta, variables: [...s.meta.variables, ...pasted.variables] } : s.meta,
      nodes: [...s.nodes.filter((n) => !connect || n.id !== placeholder).map((n) => ({ ...n, selected: false })), ...pasted.nodes],
      edges: [...s.edges.filter((e) => !connect || e.target !== placeholder).map((e) => ({ ...e, selected: false })), ...pasted.edges, ...startEdge],
    }));
    if (pasted.variables.length > 0) {
      feedback.toast(`Добавлены переменные: ${pasted.variables.map((v) => v.name).join(", ")}`, "info");
    }
    return connect;
  }, [editor, feedback, isStart]);

  const groupOrigin = (data: BlockClipboard) => ({
    x: Math.min(...data.nodes.map((n) => n.position?.x ?? 0)),
    y: Math.min(...data.nodes.map((n) => n.position?.y ?? 0)),
  });

  /** Вставить группу блоков в свободное место у центра видимой части холста. */
  const insertAtView = useCallback((data: BlockClipboard, entryId: string | null = null) => {
    const origin = groupOrigin(data);
    const rect = canvas.current?.getBoundingClientRect();
    const center = rect ? flow.screenToFlowPosition({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }) : { x: 0, y: 0 };
    const width = Math.max(...data.nodes.map((n) => n.position?.x ?? 0)) - origin.x + NODE_WIDTH;
    return insertBlocks(data, freeSpot(editor.state.nodes, { x: Math.round(center.x - Math.min(width, 900) / 2), y: Math.round(center.y - 60) }), entryId);
  }, [editor, flow, insertBlocks]);

  const copySelected = useCallback(() => {
    const data = copyBlocks(editor.state.nodes, editor.state.edges, editor.state.meta.variables, descriptors, isStart);
    if (!data) {
      feedback.toast("Выберите блоки, которые нужно скопировать.", "info");
      return;
    }
    if (!saveClipboard(data)) {
      feedback.toast("Не удалось скопировать блоки.", "error");
      return;
    }
    feedback.toast(data.nodes.length === 1 ? "Блок скопирован — Ctrl+V, чтобы вставить" : `Скопировано блоков: ${data.nodes.length}`, "info");
  }, [descriptors, editor, feedback, isStart]);

  const pasteFromClipboard = useCallback(() => {
    const data = loadClipboard();
    if (!data) {
      feedback.toast("Буфер пуст — сначала скопируйте блоки (Ctrl+C).", "info");
      return;
    }
    const ids = new Set(editor.state.nodes.map((n) => n.id));
    const origin = groupOrigin(data);
    if (data.nodes.every((n) => ids.has(n.id))) {
      // Тот же сценарий, оригиналы на месте — рядом с ними.
      insertBlocks(data, { x: origin.x + 40, y: origin.y + 40 });
    } else {
      insertAtView(data);
    }
  }, [editor, feedback, insertAtView, insertBlocks]);

  const [recorderOpen, setRecorderOpen] = useState(false);
  const [assistantOpen, setAssistantOpen] = useState(false);

  /**
   * Правка помощника: блоки и связи сценария заменяются присланными (кроме «Начала» — оно ведёт к первому блоку правки),
   * новые переменные добавляются. Одно изменение — отменяется Ctrl+Z.
   */
  const applyAssistantEdit = useCallback((draft: AssistantDraft) => {
    editor.commit((s) => {
      const start = s.nodes.find((n) => isStart(n.id));
      const startEdge = start ? s.edges.find((e) => e.source === start.id) : undefined;
      const nodes: PepaNode[] = [
        ...(start ? [{ ...start, selected: false }] : []),
        ...draft.nodes.map((definition) => ({ id: definition.id, type: "pepa" as const, position: definition.position, data: { definition } })),
      ];
      const edges = [
        ...(start && draft.entryNodeId
          ? [toFlowEdge({ id: startEdge?.id ?? newId("e"), source: start.id, sourcePort: "out", target: draft.entryNodeId })]
          : []),
        ...draft.edges.map(toFlowEdge),
      ];
      const ids = new Set(nodes.map((n) => n.id));
      const known = new Set(s.meta.variables.map((v) => v.name));
      return withGroups({
        meta: { ...s.meta, variables: [...s.meta.variables, ...draft.variables.filter((v) => !known.has(v.name))] },
        nodes,
        edges,
      }, (gs) => pruneGroups(gs, ids));
    });
    feedback.toast("Изменения помощника применены. Проверьте схему; отменить — Ctrl+Z.", "info");
  }, [editor, feedback, isStart]);

  const duplicateSelected = useCallback(() => {
    const data = copyBlocks(editor.state.nodes, editor.state.edges, editor.state.meta.variables, descriptors, isStart);
    if (!data) return;
    const origin = groupOrigin(data);
    insertBlocks(data, { x: origin.x + 40, y: origin.y + 40 });
  }, [descriptors, editor, insertBlocks, isStart]);

  const selectAll = useCallback(() => {
    // Блоки свёрнутых групп не видны — их не выбираем (иначе Delete удалил бы их незаметно).
    const hidden = new Set(groups.filter((g) => g.collapsed).flatMap((g) => g.nodeIds));
    editor.replace((s) => ({ ...s, nodes: s.nodes.map((n) => ({ ...n, selected: !hidden.has(n.id) })), edges: s.edges.map((e) => ({ ...e, selected: false })) }));
  }, [editor, groups]);

  const [searchOpen, setSearchOpen] = useState(false);

  const selectNode = (nodeId: string) => {
    // Блок в свёрнутой группе (ошибка из списка, поиск) — сначала развернуть группу. Это не правка: в историю не
    // попадает, и Ctrl+Z отменяет последнее изменение пользователя, а не разворот.
    const hiddenIn = groupOfNode(groups, nodeId);
    editor.replace((s) => {
      const shown = hiddenIn?.collapsed ? withGroups(s, (gs) => gs.map((g) => (g.id === hiddenIn.id ? { ...g, collapsed: false } : g))) : s;
      return {
        ...shown,
        nodes: shown.nodes.map((n) => ({ ...n, selected: n.id === nodeId })),
        edges: shown.edges.map((e) => ({ ...e, selected: false })),
      };
    });
    setTimeout(() => void flow.fitView({ nodes: [{ id: nodeId }], duration: 300, maxZoom: 1.1, padding: 0.6 }), hiddenIn?.collapsed ? 60 : 0);
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
      if (key === "f" || key === "а") {
        e.preventDefault();
        setSearchOpen(true);
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
      } else if (key === "c" || key === "с") {
        // Обычное копирование текста (выделенного на странице) не перехватываем.
        if (window.getSelection()?.toString()) return;
        e.preventDefault();
        copySelected();
      } else if (key === "v" || key === "м") {
        e.preventDefault();
        pasteFromClipboard();
      } else if (key === "a" || key === "ф") {
        e.preventDefault();
        selectAll();
      } else if (key === "g" || key === "п") {
        e.preventDefault();
        createGroup();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [copySelected, createGroup, duplicateSelected, editor, pasteFromClipboard, save, selectAll]);

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
        <Button size="small" variant="ghost" iconOnly icon={Search} title="Найти блок (Ctrl+F)" onClick={() => setSearchOpen(true)} />
        <Button size="small" variant="ghost" iconOnly icon={Copy} title="Копировать выбранные блоки (Ctrl+C)" onClick={copySelected} />
        <Button size="small" variant="ghost" iconOnly icon={ClipboardPaste} title="Вставить блоки (Ctrl+V) — можно и в другой сценарий" onClick={pasteFromClipboard} />
        <Button size="small" variant="ghost" iconOnly icon={Group} title="Объединить выбранные блоки в группу (Ctrl+G) — её можно свернуть в один блок" onClick={createGroup} />
        <Button size="small" icon={Sparkles} title="Описать сценарий словами — помощник предложит блоки" onClick={() => setAssistantOpen(true)}>ИИ-помощник</Button>
        <Button size="small" icon={CircleDot} title="Записать действия в браузере или в окнах Windows и превратить их в блоки" onClick={() => setRecorderOpen(true)}>Записать</Button>
        <Button size="small" icon={LayoutGrid} title="Расставить блоки автоматически" onClick={layout}>Упорядочить</Button>
        <Button size="small" icon={CalendarClock} title="Запуск по расписанию и по событиям"
          onClick={() => triggersDialog.open(scenario.id, state.meta.name || scenario.name)}>
          Автозапуск
        </Button>
        <Button size="small" icon={Save} busy={saving} disabled={!dirty} onClick={() => void save()} title="Сохранить (Ctrl+S)">Сохранить</Button>
        <Button size="small" icon={Bug} onClick={() => void run({ stepMode: true })}
          title="Отладка: запустить с паузой перед каждым блоком — смотреть переменные и продолжать по шагу">
          Пошагово
        </Button>
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
            <GroupActionsContext.Provider value={groupActions}>
            <ReactFlow<CanvasNode, PepaEdge>
              nodes={view.nodes}
              edges={view.edges}
              nodeTypes={canvasNodeTypes}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onConnect={onConnect}
              isValidConnection={isValidConnection}
              onNodeDragStart={() => {
                editor.checkpoint();
                dragFrom.current.clear();
              }}
              onNodeDragStop={() => dragFrom.current.clear()}
              deleteKeyCode={["Delete", "Backspace"]}
              connectionLineType={ConnectionLineType.SmoothStep}
              defaultEdgeOptions={{ type: "smoothstep" }}
              snapToGrid
              snapGrid={[10, 10]}
              onInit={(instance) => openReadable(instance as unknown as ReactFlowInstance<PepaNode, PepaEdge>, canvas.current, isStart)}
              minZoom={0.2}
              maxZoom={1.8}
              proOptions={{ hideAttribution: true }}
            >
              <Background gap={20} size={1.2} />
              <Controls showInteractive={false} />
              <MiniMap pannable zoomable />
            </ReactFlow>
            </GroupActionsContext.Provider>
          </FlowContext.Provider>
          <IssuesPanel report={report} checking={checking} onSelect={selectNode} />
          {searchOpen && (
            <CanvasSearch
              nodes={state.nodes}
              descriptors={descriptors}
              onPick={(nodeId) => {
                selectNode(nodeId);
                setSearchOpen(false);
              }}
              onClose={() => setSearchOpen(false)}
            />
          )}
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
      {assistantOpen && (
        <AssistantDialog
          scenario={definitionOf(state)}
          startId={state.nodes.find((n) => isStart(n.id))?.id ?? null}
          onClose={() => setAssistantOpen(false)}
          onInsert={(blocks, entryId) => {
            const connected = insertAtView(blocks, entryId);
            feedback.toast(`Добавлено блоков от помощника: ${blocks.nodes.length}.${connected ? " Первый подключён к «Началу»." : " Соедините их со схемой."} Проверьте их перед сохранением.`, "info");
          }}
          onApply={applyAssistantEdit}
          onSelectNode={selectNode}
        />
      )}
      {recorderOpen && (
        <RecorderDialog
          onClose={() => setRecorderOpen(false)}
          onInsert={(recorded) => {
            // Пароли записи — свои переменные, даже если в сценарии уже есть «password» (например, от другой программы).
            const blocks = withFreeVariables(recorded, editor.state.meta.variables.map((v) => v.name));
            insertAtView(blocks);
            feedback.toast(`Добавлено блоков из записи: ${blocks.nodes.length}. Соедините их со схемой.`, "info");
          }}
        />
      )}
    </div>
  );
}

/** Поиск блока на схеме: по подписи, названию типа и кратким параметрам. */
function CanvasSearch({ nodes, descriptors, onPick, onClose }: {
  nodes: PepaNode[];
  descriptors: Map<string, NodeTypeDescriptor>;
  onPick: (nodeId: string) => void;
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const results = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return [];
    return nodes
      .map((n) => {
        const definition = n.data.definition;
        const descriptor = descriptors.get(definition.type);
        const title = definition.label || descriptor?.title || definition.type;
        const summary = nodeSummary(definition, descriptor);
        const haystack = `${title} ${descriptor?.title ?? ""} ${summary} ${definition.type} ${definition.note ?? ""}`.toLowerCase();
        return haystack.includes(q) ? { id: n.id, title, summary, type: descriptor?.title ?? definition.type } : null;
      })
      .filter((r): r is NonNullable<typeof r> => r !== null)
      .slice(0, 12);
  }, [descriptors, nodes, query]);

  return (
    <div className="canvas-search" role="search">
      <input
        className="input"
        autoFocus
        placeholder="Найти блок: подпись, тип, параметр…"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setActive(0);
        }}
        onKeyDown={(e) => {
          if (e.key === "Escape") onClose();
          else if (e.key === "ArrowDown") setActive((i) => Math.min(i + 1, results.length - 1));
          else if (e.key === "ArrowUp") setActive((i) => Math.max(i - 1, 0));
          else if (e.key === "Enter" && results[active]) onPick(results[active].id);
        }}
        onBlur={() => setTimeout(onClose, 150)}
      />
      {query.trim() && (
        <ul className="canvas-search-results">
          {results.length === 0 && <li className="small muted">Ничего не найдено</li>}
          {results.map((r, i) => (
            <li key={r.id}>
              <button type="button" className={i === active ? "active" : ""} onMouseDown={(e) => e.preventDefault()} onClick={() => onPick(r.id)}>
                <strong className="truncate">{r.title}</strong>
                <span className="small muted truncate">{r.summary ? `${r.type} · ${r.summary}` : r.type}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
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
