import { describe, expect, it } from "vitest";
import type { AssistantDraft, EdgeDefinition, NodeDefinition } from "../api/types";
import { draftChanges, draftToBlocks, hasChanges } from "./assistant";
import { pasteBlocks } from "./clipboard";

const draft: AssistantDraft = {
  summary: "Пишет в журнал.",
  questions: [],
  variables: [{ name: "name", type: "text", default: "", description: "Имя", isInput: true }],
  nodes: [
    { id: "hello", type: "log", label: "Привет", position: { x: 0, y: 0 }, config: { message: "Привет, {{ vars.name }}" }, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
    { id: "end", type: "end", label: null, position: { x: 280, y: 0 }, config: {}, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
  ],
  edges: [{ id: "hello-out-end", source: "hello", sourcePort: "out", target: "end" }],
  entryNodeId: "hello",
  problems: [],
  requests: 1,
  usage: { inputTokens: 100, outputTokens: 50, cacheReadTokens: 0 },
};

describe("draftToBlocks", () => {
  it("вставляется как обычные блоки: новые id, связь внутри группы, новые переменные", () => {
    const blocks = draftToBlocks(draft);
    const pasted = pasteBlocks(blocks, { x: 500, y: 300 }, []);
    expect(pasted.nodes).toHaveLength(2);
    expect(pasted.nodes.map((n) => n.id)).not.toContain("hello");
    expect(pasted.nodes[0].position).toEqual({ x: 500, y: 300 });
    expect(pasted.nodes[1].position).toEqual({ x: 780, y: 300 });
    expect(pasted.edges).toHaveLength(1);
    expect(pasted.edges[0].source).toBe(pasted.nodes[0].id);
    expect(pasted.variables.map((v) => v.name)).toEqual(["name"]);
    // Черновик не меняется при вставке.
    expect(draft.nodes[0].id).toBe("hello");
  });
});

describe("draftChanges", () => {
  const settings = { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null };
  const current: { nodes: NodeDefinition[]; edges: EdgeDefinition[] } = {
    nodes: [
      { id: "start", type: "start", label: null, position: { x: 0, y: 0 }, config: {}, settings, note: null },
      { id: "hello", type: "log", label: "Привет", position: { x: 280, y: 0 }, config: { message: "Привет" }, settings, note: null },
      { id: "old", type: "log", label: "Лишний", position: { x: 560, y: 0 }, config: { message: "x" }, settings, note: null },
      { id: "end", type: "end", label: null, position: { x: 840, y: 0 }, config: {}, settings, note: null },
    ],
    edges: [
      { id: "s", source: "start", sourcePort: "out", target: "hello" },
      { id: "a", source: "hello", sourcePort: "out", target: "old" },
      { id: "b", source: "old", sourcePort: "out", target: "end" },
    ],
  };

  it("показывает добавленные, удалённые и изменённые блоки и связи (без «Начала»)", () => {
    const changes = draftChanges(current.nodes, current.edges, draft, "start");
    expect(changes.added).toEqual([]);
    expect(changes.removed.map((n) => n.id)).toEqual(["old"]);
    expect(changes.changed).toEqual([{ node: draft.nodes[0], what: ["message"] }]);
    expect(changes.linksAdded).toBe(1);
    expect(changes.linksRemoved).toBe(2);
    expect(hasChanges(changes)).toBe(true);
  });

  it("без изменений — пусто", () => {
    const same = { ...draft, nodes: current.nodes.slice(1), edges: current.edges.slice(1) };
    expect(hasChanges(draftChanges(current.nodes, current.edges, same, "start"))).toBe(false);
  });

  it("изменённые настройки выполнения блока тоже видны", () => {
    const retried = current.nodes.slice(1).map((n) => (n.id === "hello" ? { ...n, settings: { ...n.settings, timeoutSeconds: 600 } } : n));
    const changes = draftChanges(current.nodes, current.edges, { ...draft, nodes: retried, edges: current.edges.slice(1) }, "start");
    expect(changes.changed).toEqual([{ node: retried[0], what: ["повторы, тайм-аут или точка останова"] }]);
  });
});
