import type { Edge, Node } from "@xyflow/react";
import type {
  EdgeDefinition,
  FieldDescriptor,
  Json,
  NodeDefinition,
  NodeTypeDescriptor,
  ScenarioDefinition,
} from "../api/types";

/** Данные блока на холсте: определение из сценария (единственный источник правды). */
export interface PepaNodeData extends Record<string, unknown> {
  definition: NodeDefinition;
}

export type PepaNode = Node<PepaNodeData, "pepa">;
export type PepaEdge = Edge<{ port: string }>;

export const NODE_WIDTH = 240;

export function newId(prefix: string): string {
  const random = Math.random().toString(36).slice(2, 8);
  return `${prefix}${Date.now().toString(36).slice(-4)}${random}`;
}

export function toFlowNodes(definition: ScenarioDefinition): PepaNode[] {
  return definition.nodes.map((node) => ({
    id: node.id,
    type: "pepa",
    position: { x: node.position?.x ?? 0, y: node.position?.y ?? 0 },
    data: { definition: node },
  }));
}

export function edgeStyle(port: string): Pick<PepaEdge, "className" | "label" | "animated"> {
  return {
    className: port === "error" ? "edge-error" : port === "false" ? "edge-false" : port === "true" ? "edge-true" : "",
    label: undefined,
    animated: false,
  };
}

export function toFlowEdges(definition: ScenarioDefinition): PepaEdge[] {
  return definition.edges.map((edge) => toFlowEdge(edge));
}

export function toFlowEdge(edge: EdgeDefinition): PepaEdge {
  return {
    id: edge.id,
    source: edge.source,
    sourceHandle: edge.sourcePort,
    target: edge.target,
    targetHandle: "in",
    type: "smoothstep",
    data: { port: edge.sourcePort },
    ...edgeStyle(edge.sourcePort),
  };
}

export function toDefinition(
  meta: Pick<ScenarioDefinition, "id" | "name" | "description" | "variables" | "schemaVersion" | "editor">,
  nodes: PepaNode[],
  edges: PepaEdge[],
): ScenarioDefinition {
  return {
    ...meta,
    nodes: nodes.map((n) => ({
      ...n.data.definition,
      position: { x: Math.round(n.position.x), y: Math.round(n.position.y) },
    })),
    edges: edges.map((e) => ({
      id: e.id,
      source: e.source,
      sourcePort: e.sourceHandle ?? "out",
      target: e.target,
    })),
  };
}

/** Начальные значения параметров из описания блока. */
export function defaultConfig(descriptor: NodeTypeDescriptor): Record<string, Json> {
  const config: Record<string, Json> = {};
  for (const field of descriptor.fields) {
    if (field.default !== null && field.default !== undefined) config[field.name] = field.default;
  }
  return config;
}

export function createNode(descriptor: NodeTypeDescriptor, position: { x: number; y: number }): NodeDefinition {
  return {
    id: newId("n"),
    type: descriptor.type,
    label: null,
    position,
    config: defaultConfig(descriptor),
    settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null },
    note: null,
  };
}

export function isFieldVisible(field: FieldDescriptor, config: Record<string, Json>, fields: FieldDescriptor[]): boolean {
  if (!field.visibleWhen) return true;
  const other = fields.find((f) => f.name === field.visibleWhen!.field);
  const raw = config[field.visibleWhen.field] ?? other?.default ?? null;
  return raw !== null && field.visibleWhen.values.includes(String(raw));
}

/** Короткое описание блока для карточки на схеме. */
export function nodeSummary(node: NodeDefinition, descriptor: NodeTypeDescriptor | undefined): string {
  if (!descriptor?.summaryField) return "";
  const field = descriptor.fields.find((f) => f.name === descriptor.summaryField);
  const value = node.config[descriptor.summaryField] ?? field?.default;
  if (value === null || value === undefined || value === "") return "";
  if (descriptor.type === "wait") {
    if (node.config.mode === "untilTime") return `до ${node.config.time ?? "—"}`;
    const unitField = descriptor.fields.find((f) => f.name === "unit");
    const unitValue = node.config.unit ?? unitField?.default ?? "seconds";
    const unitLabel = unitField?.options?.find((o) => o.value === unitValue)?.label ?? "";
    return `${value} ${unitLabel}`.trim();
  }
  if (field?.kind === "select") {
    return field.options?.find((o) => o.value === value)?.label ?? String(value);
  }
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * Простая послойная раскладка слева направо: слой = длина пути от «Начала» (без обратных связей),
 * внутри слоя — по порядку обхода. Обратные связи (циклы) не сдвигают блоки.
 */
export function autoLayout(nodes: PepaNode[], edges: PepaEdge[], startId: string | undefined): Map<string, { x: number; y: number }> {
  const outgoing = new Map<string, string[]>();
  for (const e of edges) {
    if (!outgoing.has(e.source)) outgoing.set(e.source, []);
    outgoing.get(e.source)!.push(e.target);
  }

  const layer = new Map<string, number>();
  const order: string[] = [];
  const visit = (id: string, depth: number, stack: Set<string>) => {
    if (stack.has(id)) return; // обратная связь
    const known = layer.get(id);
    if (known !== undefined && known >= depth) return;
    if (known === undefined) order.push(id);
    layer.set(id, depth);
    stack.add(id);
    for (const next of outgoing.get(id) ?? []) visit(next, depth + 1, stack);
    stack.delete(id);
  };
  if (startId) visit(startId, 0, new Set());

  // Недостижимые блоки — отдельной строкой снизу.
  const maxLayer = Math.max(0, ...layer.values());
  let orphanIndex = 0;
  for (const n of nodes) {
    if (!layer.has(n.id)) {
      layer.set(n.id, orphanIndex++ % (maxLayer + 1));
      order.push(n.id);
    }
  }

  const rows = new Map<number, number>();
  const positions = new Map<string, { x: number; y: number }>();
  for (const id of order) {
    const l = layer.get(id)!;
    const row = rows.get(l) ?? 0;
    rows.set(l, row + 1);
    positions.set(id, { x: l * (NODE_WIDTH + 70), y: row * 150 });
  }
  return positions;
}

/** Ровно одна связь из каждого выхода: новая связь из занятого выхода заменяет старую. */
export function connect(edges: PepaEdge[], source: string, sourcePort: string, target: string): PepaEdge[] {
  const rest = edges.filter((e) => !(e.source === source && (e.sourceHandle ?? "out") === sourcePort));
  return [...rest, toFlowEdge({ id: newId("e"), source, sourcePort, target })];
}
