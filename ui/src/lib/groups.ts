import type { Node } from "@xyflow/react";
import type { Json } from "../api/types";
import { NODE_WIDTH, type PepaEdge, type PepaNode } from "./graph";

/**
 * Группа блоков на схеме — только для конструктора: рамка с названием вокруг блоков, которую можно свернуть в один блок.
 * Хранится в данных редактора сценария (editor.groups); движок её не читает, поэтому на выполнение она не влияет.
 */
export interface EditorGroup {
  id: string;
  title: string;
  nodeIds: string[];
  collapsed: boolean;
}

export interface GroupNodeData extends Record<string, unknown> {
  group: EditorGroup;
  /** Сколько блоков в группе. */
  count: number;
  /** Подписи первых блоков — для свёрнутой группы. */
  titles: string[];
  /** Ошибок проверки в блоках группы. */
  issues: number;
}

export type GroupFrameNode = Node<GroupNodeData, "groupFrame">;
export type GroupCardNode = Node<GroupNodeData, "groupCard">;
export type CanvasNode = PepaNode | GroupFrameNode | GroupCardNode;

export const GROUP_PREFIX = "group:";
const PADDING = 24;
const HEADER = 34;
const DEFAULT_HEIGHT = 96;

export const isGroupNodeId = (id: string) => id.startsWith(GROUP_PREFIX);
export const groupNodeId = (group: EditorGroup) => GROUP_PREFIX + group.id;
export const groupIdOf = (nodeId: string) => nodeId.slice(GROUP_PREFIX.length);

/** Группы из данных редактора; повреждённые записи пропускаются. */
export function readGroups(editor: Record<string, Json> | null | undefined): EditorGroup[] {
  const raw = editor?.groups;
  if (!Array.isArray(raw)) return [];
  const groups: EditorGroup[] = [];
  for (const item of raw) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue;
    const { id, title, nodeIds, collapsed } = item as Record<string, Json>;
    if (typeof id !== "string" || !Array.isArray(nodeIds)) continue;
    groups.push({
      id,
      title: typeof title === "string" ? title : "Группа",
      nodeIds: nodeIds.filter((n): n is string => typeof n === "string"),
      collapsed: collapsed === true,
    });
  }
  return groups;
}

/** Данные редактора с этими группами (пустой список — без ключа groups). */
export function writeGroups(editor: Record<string, Json> | null | undefined, groups: EditorGroup[]): Record<string, Json> | null {
  const { groups: _old, ...rest } = editor ?? {};
  if (groups.length === 0) return Object.keys(rest).length > 0 ? rest : null;
  return { ...rest, groups: groups.map((g) => ({ id: g.id, title: g.title, nodeIds: g.nodeIds, collapsed: g.collapsed })) };
}

/** Новая группа из блоков: блок состоит только в одной группе, поэтому из прежних групп он уходит. */
export function addGroup(groups: EditorGroup[], nodeIds: string[], id: string, title: string): EditorGroup[] {
  const taken = new Set(nodeIds);
  const rest = groups.map((g) => ({ ...g, nodeIds: g.nodeIds.filter((n) => !taken.has(n)) })).filter((g) => g.nodeIds.length > 0);
  return [...rest, { id, title, nodeIds: [...nodeIds], collapsed: false }];
}

/** Убрать из групп удалённые блоки; группа без блоков исчезает. */
export function pruneGroups(groups: EditorGroup[], existing: Set<string>): EditorGroup[] {
  let changed = false;
  const result: EditorGroup[] = [];
  for (const group of groups) {
    const nodeIds = group.nodeIds.filter((n) => existing.has(n));
    if (nodeIds.length !== group.nodeIds.length) changed = true;
    if (nodeIds.length > 0) result.push(nodeIds.length === group.nodeIds.length ? group : { ...group, nodeIds });
  }
  return changed ? result : groups;
}

export function groupOfNode(groups: EditorGroup[], nodeId: string): EditorGroup | undefined {
  return groups.find((g) => g.nodeIds.includes(nodeId));
}

/** Следующее свободное название «Группа N». */
export function nextGroupTitle(groups: EditorGroup[]): string {
  const used = new Set(groups.map((g) => g.title));
  for (let i = 1; ; i++) {
    if (!used.has(`Группа ${i}`)) return `Группа ${i}`;
  }
}

/** Прямоугольник вокруг блоков группы (с полями и местом для заголовка). */
export function groupBounds(group: EditorGroup, nodes: PepaNode[]): { x: number; y: number; width: number; height: number } | null {
  const members = nodes.filter((n) => group.nodeIds.includes(n.id));
  if (members.length === 0) return null;
  const left = Math.min(...members.map((n) => n.position.x));
  const top = Math.min(...members.map((n) => n.position.y));
  const right = Math.max(...members.map((n) => n.position.x + (n.measured?.width ?? NODE_WIDTH)));
  const bottom = Math.max(...members.map((n) => n.position.y + (n.measured?.height ?? DEFAULT_HEIGHT)));
  return { x: left - PADDING, y: top - PADDING - HEADER, width: right - left + PADDING * 2, height: bottom - top + PADDING * 2 + HEADER };
}

/** Сдвинуть блоки группы (кроме тех, что двигаются сами — выбраны и перетаскиваются вместе с рамкой). */
export function moveMembers(nodes: PepaNode[], group: EditorGroup, dx: number, dy: number, except: Set<string> = new Set()): PepaNode[] {
  if (dx === 0 && dy === 0) return nodes;
  return nodes.map((n) => (group.nodeIds.includes(n.id) && !except.has(n.id)
    ? { ...n, position: { x: n.position.x + dx, y: n.position.y + dy } }
    : n));
}

/**
 * Что показать на холсте: рамки развёрнутых групп (позади блоков), блоки (у свёрнутых групп — скрыты) и свёрнутые
 * группы одним блоком. Связи с блоками свёрнутой группы идут к этому блоку; связи внутри неё скрыты. Перенаправленные
 * связи только показываются: выбрать и удалить их можно, развернув группу.
 */
export function canvasView(
  nodes: PepaNode[],
  edges: PepaEdge[],
  groups: EditorGroup[],
  titleOf: (node: PepaNode) => string,
  issues?: Map<string, number>,
): { nodes: CanvasNode[]; edges: PepaEdge[] } {
  if (groups.length === 0) return { nodes, edges };
  const collapsedOf = new Map<string, EditorGroup>();
  for (const group of groups) {
    if (group.collapsed) for (const id of group.nodeIds) collapsedOf.set(id, group);
  }

  const frames: CanvasNode[] = [];
  const cards: CanvasNode[] = [];
  for (const group of groups) {
    const bounds = groupBounds(group, nodes);
    if (!bounds) continue;
    const members = nodes.filter((n) => group.nodeIds.includes(n.id));
    const data: GroupNodeData = {
      group,
      count: members.length,
      titles: members.slice(0, 4).map(titleOf),
      issues: members.reduce((sum, n) => sum + (issues?.get(n.id) ?? 0), 0),
    };
    if (group.collapsed) {
      cards.push({
        id: groupNodeId(group), type: "groupCard", position: { x: bounds.x + PADDING, y: bounds.y + PADDING + HEADER }, data,
        deletable: false, connectable: false,
      });
    } else {
      frames.push({
        id: groupNodeId(group), type: "groupFrame", position: { x: bounds.x, y: bounds.y }, data,
        // Не ниже связей (иначе связи перекрыли бы заголовок с кнопками); позади блоков — потому что рамки идут первыми.
        style: { width: bounds.width, height: bounds.height },
        deletable: false, connectable: false, selectable: false, dragHandle: ".group-frame-header",
      });
    }
  }

  const shown = nodes.map((n) => (collapsedOf.has(n.id) ? { ...n, hidden: true, selected: false } : n));
  const routed = edges.map((e) => {
    const from = collapsedOf.get(e.source);
    const to = collapsedOf.get(e.target);
    if (!from && !to) return e;
    if (from && to && from.id === to.id) return { ...e, hidden: true, selected: false };
    return {
      ...e,
      source: from ? groupNodeId(from) : e.source,
      sourceHandle: from ? "out" : e.sourceHandle,
      target: to ? groupNodeId(to) : e.target,
      targetHandle: to ? "in" : e.targetHandle,
      selectable: false,
      deletable: false,
      selected: false,
    };
  });
  return { nodes: [...frames, ...shown, ...cards], edges: routed };
}
