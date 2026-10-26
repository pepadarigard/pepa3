import { describe, expect, it } from "vitest";
import type { ScenarioDefinition } from "../api/types";
import { toFlowEdges, toFlowNodes } from "./graph";
import { addGroup, canvasView, groupBounds, moveMembers, nextGroupTitle, pruneGroups, readGroups, writeGroups, type EditorGroup } from "./groups";

const settings = { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null };
const scenario: ScenarioDefinition = {
  schemaVersion: 1,
  id: "s1",
  name: "Группы",
  description: "",
  variables: [],
  editor: null,
  nodes: [
    { id: "start", type: "start", label: null, position: { x: 0, y: 0 }, config: {}, settings, note: null },
    { id: "a", type: "log", label: "Первый", position: { x: 300, y: 0 }, config: {}, settings, note: null },
    { id: "b", type: "log", label: "Второй", position: { x: 600, y: 100 }, config: {}, settings, note: null },
    { id: "end", type: "end", label: null, position: { x: 900, y: 0 }, config: {}, settings, note: null },
  ],
  edges: [
    { id: "e1", source: "start", sourcePort: "out", target: "a" },
    { id: "e2", source: "a", sourcePort: "out", target: "b" },
    { id: "e3", source: "b", sourcePort: "error", target: "end" },
  ],
};

const group = (collapsed = false): EditorGroup => ({ id: "g1", title: "Вход", nodeIds: ["a", "b"], collapsed });

describe("группы блоков", () => {
  it("сохраняются в данных редактора и читаются обратно, повреждённые записи пропускаются", () => {
    const editor = writeGroups({ zoom: 1 }, [group(true)]);
    expect(editor).toEqual({ zoom: 1, groups: [{ id: "g1", title: "Вход", nodeIds: ["a", "b"], collapsed: true }] });
    expect(readGroups(editor)).toEqual([group(true)]);
    expect(readGroups({ groups: [null, { id: 1 }, { id: "x", nodeIds: ["a", 2] }] })).toEqual([{ id: "x", title: "Группа", nodeIds: ["a"], collapsed: false }]);
    expect(writeGroups({ groups: [] }, [])).toBeNull();
  });

  it("блок состоит только в одной группе, а удалённые блоки уходят из групп", () => {
    const groups = addGroup([group()], ["b", "end"], "g2", nextGroupTitle([group()]));
    expect(groups).toEqual([
      { id: "g1", title: "Вход", nodeIds: ["a"], collapsed: false },
      { id: "g2", title: "Группа 1", nodeIds: ["b", "end"], collapsed: false },
    ]);
    expect(pruneGroups(groups, new Set(["start", "b"]))).toEqual([{ id: "g2", title: "Группа 1", nodeIds: ["b"], collapsed: false }]);
    expect(pruneGroups(groups, new Set(["a", "b", "end"]))).toBe(groups);
  });

  it("рамка охватывает блоки с полями, а перетаскивание рамки двигает её блоки", () => {
    const nodes = toFlowNodes(scenario);
    expect(groupBounds(group(), nodes)).toEqual({ x: 276, y: -58, width: 588, height: 278 });
    const moved = moveMembers(nodes, group(), 10, -20, new Set(["b"]));
    expect(moved.find((n) => n.id === "a")!.position).toEqual({ x: 310, y: -20 });
    expect(moved.find((n) => n.id === "b")!.position).toEqual({ x: 600, y: 100 });
  });

  it("развёрнутая группа — рамка позади блоков, связи как были", () => {
    const view = canvasView(toFlowNodes(scenario), toFlowEdges(scenario), [group()], (n) => n.data.definition.label ?? n.id);
    expect(view.nodes[0]).toMatchObject({ id: "group:g1", type: "groupFrame", data: { count: 2 } });
    expect(view.nodes.filter((n) => n.hidden)).toHaveLength(0);
    expect(view.edges).toEqual(toFlowEdges(scenario));
  });

  it("свёрнутая группа — один блок: внутренние связи скрыты, внешние идут к нему", () => {
    const issues = new Map([["b", 2]]);
    const view = canvasView(toFlowNodes(scenario), toFlowEdges(scenario), [group(true)], (n) => n.data.definition.label ?? n.id, issues);
    const card = view.nodes.find((n) => n.id === "group:g1")!;
    expect(card).toMatchObject({ type: "groupCard", position: { x: 300, y: 0 }, data: { count: 2, titles: ["Первый", "Второй"], issues: 2 } });
    expect(view.nodes.filter((n) => n.hidden).map((n) => n.id)).toEqual(["a", "b"]);
    const byId = new Map(view.edges.map((e) => [e.id, e]));
    expect(byId.get("e1")).toMatchObject({ source: "start", target: "group:g1", targetHandle: "in", selectable: false });
    expect(byId.get("e2")!.hidden).toBe(true);
    // Цвет связи по выходу (ошибка) сохраняется, хотя выход теперь общий.
    expect(byId.get("e3")).toMatchObject({ source: "group:g1", sourceHandle: "out", target: "end", className: "edge-error" });
  });
});
