import type { AssistantDraft } from "../api/types";
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
