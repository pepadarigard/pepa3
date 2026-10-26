import { describe, expect, it } from "vitest";
import type { AssistantDraft } from "../api/types";
import { draftToBlocks } from "./assistant";
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
