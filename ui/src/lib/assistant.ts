import type { AssistantDraft, EdgeDefinition, NodeDefinition } from "../api/types";
import type { BlockClipboard } from "./clipboard";

/** Черновик помощника → группа блоков для вставки (как скопированные блоки: новые id, связи внутри группы, переменные). */
export function draftToBlocks(draft: AssistantDraft): BlockClipboard {
  return {
    kind: "pepa-blocks",
    version: 1,
    nodes: draft.nodes.map((n) => structuredClone(n)),
    edges: draft.edges.map((e) => ({ ...e })),
    variables: draft.variables.map((v) => structuredClone(v)),
  };
}

/** Что меняет правка помощника — чтобы пользователь увидел это до того, как применить. */
export interface DraftChanges {
  added: NodeDefinition[];
  removed: NodeDefinition[];
  /** Блок и что в нём изменилось: «подпись», «заметка», имена полей. */
  changed: { node: NodeDefinition; what: string[] }[];
  linksAdded: number;
  linksRemoved: number;
}

const linkKey = (e: Pick<EdgeDefinition, "source" | "sourcePort" | "target">) => `${e.source}\u0000${e.sourcePort}\u0000${e.target}`;

/**
 * Сравнить сценарий с правкой помощника. «Начало» и связь из него не сравниваются: правка их не содержит (к первому
 * блоку правки «Начало» подключается само).
 */
export function draftChanges(nodes: NodeDefinition[], edges: EdgeDefinition[], draft: AssistantDraft, startId: string | null): DraftChanges {
  const before = new Map(nodes.filter((n) => n.id !== startId).map((n) => [n.id, n]));
  const after = new Map(draft.nodes.map((n) => [n.id, n]));
  const changed: DraftChanges["changed"] = [];
  for (const node of draft.nodes) {
    const old = before.get(node.id);
    if (!old) continue;
    const what: string[] = [];
    if (old.type !== node.type) what.push("тип");
    if ((old.label ?? "") !== (node.label ?? "")) what.push("подпись");
    if ((old.note ?? "") !== (node.note ?? "")) what.push("заметка");
    for (const field of new Set([...Object.keys(old.config), ...Object.keys(node.config)])) {
      if (JSON.stringify(old.config[field] ?? null) !== JSON.stringify(node.config[field] ?? null)) what.push(field);
    }
    if (what.length > 0) changed.push({ node, what });
  }

  const oldLinks = new Set(edges.filter((e) => e.source !== startId).map(linkKey));
  const newLinks = new Set(draft.edges.map(linkKey));
  return {
    added: draft.nodes.filter((n) => !before.has(n.id)),
    removed: [...before.values()].filter((n) => !after.has(n.id)),
    changed,
    linksAdded: [...newLinks].filter((k) => !oldLinks.has(k)).length,
    linksRemoved: [...oldLinks].filter((k) => !newLinks.has(k)).length,
  };
}

export function hasChanges(changes: DraftChanges): boolean {
  return changes.added.length + changes.removed.length + changes.changed.length + changes.linksAdded + changes.linksRemoved > 0;
}
