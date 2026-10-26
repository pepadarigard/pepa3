import type { EdgeDefinition, Json, NodeDefinition, NodeTypeDescriptor, VariableDefinition } from "../api/types";
import { newId, toFlowEdge, type PepaEdge, type PepaNode } from "./graph";

/** Скопированные блоки: определения, связи между ними и переменные, в которые блоки сохраняют результат. */
export interface BlockClipboard {
  kind: "pepa-blocks";
  version: 1;
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  variables: VariableDefinition[];
}

const STORAGE_KEY = "pepa.clipboard";

/** Выбранные блоки (кроме «Начала») и связи, у которых оба конца среди выбранных. */
export function copyBlocks(
  nodes: PepaNode[],
  edges: PepaEdge[],
  variables: VariableDefinition[],
  descriptors: Map<string, NodeTypeDescriptor>,
  isStart: (id: string) => boolean,
): BlockClipboard | null {
  const selected = nodes.filter((n) => n.selected && !isStart(n.id));
  if (selected.length === 0) return null;
  const ids = new Set(selected.map((n) => n.id));
  const used = new Set<string>();
  for (const node of selected) {
    for (const name of referencedVariables(node.data.definition, descriptors)) used.add(name);
  }
  return {
    kind: "pepa-blocks",
    version: 1,
    nodes: selected.map((n) => ({
      ...structuredClone(n.data.definition),
      position: { x: Math.round(n.position.x), y: Math.round(n.position.y) },
    })),
    edges: edges
      .filter((e) => ids.has(e.source) && ids.has(e.target))
      .map((e) => ({ id: e.id, source: e.source, sourcePort: e.sourceHandle ?? "out", target: e.target })),
    variables: variables.filter((v) => used.has(v.name)).map((v) => structuredClone(v)),
  };
}

/** Имена переменных из полей «Сохранить в» и подобных (поля вида «переменная»). */
export function referencedVariables(node: NodeDefinition, descriptors: Map<string, NodeTypeDescriptor>): string[] {
  const descriptor = descriptors.get(node.type);
  if (!descriptor) return [];
  return descriptor.fields
    .filter((f) => f.kind === "variable")
    .map((f) => node.config[f.name])
    .filter((v): v is string => typeof v === "string" && v.length > 0);
}

/**
 * Вставить блоки: новые идентификаторы, связи между вставленными, недостающие переменные. Группа ставится так,
 * чтобы её левый верхний угол оказался в <paramref name="at"/>.
 */
export function pasteBlocks(
  clipboard: BlockClipboard,
  at: { x: number; y: number },
  existingVariables: VariableDefinition[],
): { nodes: PepaNode[]; edges: PepaEdge[]; variables: VariableDefinition[] } {
  const minX = Math.min(...clipboard.nodes.map((n) => n.position?.x ?? 0));
  const minY = Math.min(...clipboard.nodes.map((n) => n.position?.y ?? 0));
  const ids = new Map<string, string>();
  const nodes: PepaNode[] = clipboard.nodes.map((n) => {
    const id = newId("n");
    ids.set(n.id, id);
    const position = { x: Math.round(at.x + (n.position?.x ?? 0) - minX), y: Math.round(at.y + (n.position?.y ?? 0) - minY) };
    return { id, type: "pepa", position, selected: true, data: { definition: { ...structuredClone(n), id, position } } };
  });
  const edges = clipboard.edges
    .filter((e) => ids.has(e.source) && ids.has(e.target))
    .map((e) => toFlowEdge({ id: newId("e"), source: ids.get(e.source)!, sourcePort: e.sourcePort, target: ids.get(e.target)! }));
  const known = new Set(existingVariables.map((v) => v.name));
  const variables = clipboard.variables.filter((v) => !known.has(v.name));
  return { nodes, edges, variables };
}

export function saveClipboard(clipboard: BlockClipboard): boolean {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(clipboard));
    return true;
  } catch {
    return false;
  }
}

export function loadClipboard(): BlockClipboard | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Json;
    return isClipboard(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isClipboard(value: unknown): value is BlockClipboard {
  const v = value as Partial<BlockClipboard> | null;
  return !!v && v.kind === "pepa-blocks" && v.version === 1 && Array.isArray(v.nodes) && v.nodes.length > 0 &&
    Array.isArray(v.edges) && Array.isArray(v.variables);
}
