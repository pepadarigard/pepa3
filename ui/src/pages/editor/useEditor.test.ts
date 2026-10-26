import { describe, expect, it } from "vitest";
import type { ScenarioDefinition } from "../../api/types";
import { writeGroups } from "../../lib/groups";
import { fromDefinition, keepSelection } from "./useEditor";

const settings = { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null };
const definition = (collapsed: boolean): ScenarioDefinition => ({
  schemaVersion: 1,
  id: "s1",
  name: "Группы",
  description: "",
  variables: [],
  editor: writeGroups(null, [{ id: "g1", title: "Вход", nodeIds: ["a"], collapsed }]),
  nodes: [
    { id: "start", type: "start", label: null, position: { x: 0, y: 0 }, config: {}, settings, note: null },
    { id: "a", type: "log", label: null, position: { x: 300, y: 0 }, config: {}, settings, note: null },
    { id: "b", type: "log", label: null, position: { x: 600, y: 0 }, config: {}, settings, note: null },
  ],
  edges: [],
});

describe("отмена и выделение", () => {
  it("выделение остаётся, но не на блоках, которые после отмены спрятаны в свёрнутой группе", () => {
    const now = fromDefinition(definition(false));
    const from = { ...now, nodes: now.nodes.map((n) => ({ ...n, selected: n.id !== "start" })) };
    const restored = keepSelection(fromDefinition(definition(true)), from);
    expect(restored.nodes.filter((n) => n.selected).map((n) => n.id)).toEqual(["b"]);
    expect(keepSelection(fromDefinition(definition(false)), from).nodes.filter((n) => n.selected).map((n) => n.id)).toEqual(["a", "b"]);
  });
});
