import { useCallback, useMemo, useRef, useState } from "react";
import type { ScenarioDefinition } from "../../api/types";
import { toDefinition, toFlowEdges, toFlowNodes, type PepaEdge, type PepaNode } from "../../lib/graph";

export type Meta = Pick<ScenarioDefinition, "id" | "name" | "description" | "variables" | "schemaVersion" | "editor">;

export interface EditorState {
  meta: Meta;
  nodes: PepaNode[];
  edges: PepaEdge[];
}

const HISTORY_LIMIT = 100;
const COALESCE_MS = 800;

export function fromDefinition(definition: ScenarioDefinition): EditorState {
  const { nodes: _nodes, edges: _edges, ...meta } = definition;
  return { meta, nodes: toFlowNodes(definition), edges: toFlowEdges(definition) };
}

export function definitionOf(state: EditorState): ScenarioDefinition {
  return toDefinition(state.meta, state.nodes, state.edges);
}

/** Сравнение без учёта выделения и служебных полей React Flow. */
export function snapshotOf(state: EditorState): string {
  return JSON.stringify(definitionOf(state));
}

/**
 * Состояние конструктора с историей. commit — изменение, которое можно отменить; replace — без истории
 * (выделение, промежуточные положения при перетаскивании). Подряд идущие правки одного поля склеиваются.
 * Текущее состояние держится и в ref: обработчики React Flow могут вызываться несколько раз до перерисовки.
 */
export function useEditor(initial: ScenarioDefinition) {
  const [state, setState] = useState<EditorState>(() => fromDefinition(initial));
  const current = useRef(state);
  const past = useRef<EditorState[]>([]);
  const future = useRef<EditorState[]>([]);
  const last = useRef<{ key: string | null; at: number }>({ key: null, at: 0 });

  const set = useCallback((next: EditorState) => {
    current.current = next;
    setState(next);
  }, []);

  const push = (snapshot: EditorState) => {
    past.current = [...past.current.slice(-HISTORY_LIMIT + 1), snapshot];
    future.current = [];
  };

  const replace = useCallback((updater: (s: EditorState) => EditorState) => {
    const next = updater(current.current);
    if (next !== current.current) set(next);
  }, [set]);

  const commit = useCallback((updater: (s: EditorState) => EditorState, key: string | null = null) => {
    const before = current.current;
    const next = updater(before);
    if (next === before) return;
    const now = Date.now();
    const coalesce = key !== null && last.current.key === key && now - last.current.at < COALESCE_MS;
    if (!coalesce) push(before);
    last.current = { key, at: now };
    set(next);
  }, [set]);

  /** Запомнить состояние перед серией изменений (например, перед перетаскиванием блока). */
  const checkpoint = useCallback(() => {
    push(current.current);
    last.current = { key: null, at: 0 };
  }, []);

  const undo = useCallback(() => {
    const previous = past.current.at(-1);
    if (!previous) return;
    past.current = past.current.slice(0, -1);
    future.current = [...future.current, current.current];
    last.current = { key: null, at: 0 };
    set(keepSelection(previous, current.current));
  }, [set]);

  const redo = useCallback(() => {
    const next = future.current.at(-1);
    if (!next) return;
    future.current = future.current.slice(0, -1);
    past.current = [...past.current, current.current];
    last.current = { key: null, at: 0 };
    set(keepSelection(next, current.current));
  }, [set]);

  const reset = useCallback((definition: ScenarioDefinition) => {
    past.current = [];
    future.current = [];
    last.current = { key: null, at: 0 };
    set(fromDefinition(definition));
  }, [set]);

  const snapshot = useMemo(() => snapshotOf(state), [state]);

  return {
    state,
    snapshot,
    commit,
    replace,
    checkpoint,
    undo,
    redo,
    reset,
    canUndo: past.current.length > 0,
    canRedo: future.current.length > 0,
  };
}

/** После отмены сохраняем выделение тех блоков, что ещё существуют, — иначе панель свойств «прыгает». */
function keepSelection(target: EditorState, from: EditorState): EditorState {
  const selected = new Set(from.nodes.filter((n) => n.selected).map((n) => n.id));
  return { ...target, nodes: target.nodes.map((n) => ({ ...n, selected: selected.has(n.id) })) };
}
