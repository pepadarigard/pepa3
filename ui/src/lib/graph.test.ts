import { describe, expect, it } from "vitest";
import type { NodeTypeDescriptor, ScenarioDefinition } from "../api/types";
import { autoLayout, connect, createNode, isFieldVisible, nodeSummary, toDefinition, toFlowEdges, toFlowNodes } from "./graph";

const scenario: ScenarioDefinition = {
  schemaVersion: 1,
  id: "s1",
  name: "Тест",
  description: "",
  variables: [],
  editor: null,
  nodes: [
    { id: "start", type: "start", label: null, position: { x: 0, y: 0 }, config: {}, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
    { id: "c", type: "condition", label: "Проверка", position: { x: 300, y: 0 }, config: { expression: "vars.i < 3" }, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
    { id: "end", type: "end", label: null, position: { x: 600, y: 0 }, config: {}, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
  ],
  edges: [
    { id: "e1", source: "start", sourcePort: "out", target: "c" },
    { id: "e2", source: "c", sourcePort: "true", target: "c" },
    { id: "e3", source: "c", sourcePort: "false", target: "end" },
  ],
};

const waitDescriptor: NodeTypeDescriptor = {
  type: "wait",
  category: "logic",
  title: "Ожидание",
  description: "",
  icon: "timer",
  effect: "none",
  supportsVerification: false,
  isStart: false,
  isEnd: false,
  outputs: [{ id: "out", label: "Далее", kind: "normal", required: true }],
  summaryField: "amount",
  supportsRetry: false,
  resourceHint: null,
  fields: [
    { name: "mode", label: "Как", kind: "select", required: false, default: "duration", help: null, placeholder: null, min: null, max: null, visibleWhen: null, lookup: null, options: [{ value: "duration", label: "Время" }, { value: "untilTime", label: "До" }] },
    { name: "amount", label: "Сколько", kind: "number", required: true, default: 10, help: null, placeholder: null, min: 0, max: null, options: null, visibleWhen: { field: "mode", values: ["duration"] }, lookup: null },
    { name: "unit", label: "Ед.", kind: "select", required: false, default: "seconds", help: null, placeholder: null, min: null, max: null, visibleWhen: { field: "mode", values: ["duration"] }, options: [{ value: "seconds", label: "секунд" }, { value: "minutes", label: "минут" }], lookup: null },
    { name: "time", label: "Время", kind: "time", required: true, default: null, help: null, placeholder: null, min: null, max: null, options: null, visibleWhen: { field: "mode", values: ["untilTime"] }, lookup: null },
  ],
};

describe("graph conversion", () => {
  it("round-trips scenario through flow nodes and edges", () => {
    const nodes = toFlowNodes(scenario);
    const edges = toFlowEdges(scenario);
    const back = toDefinition(scenario, nodes, edges);
    expect(back).toEqual(scenario);
  });

  it("marks branch edges with classes", () => {
    const edges = toFlowEdges(scenario);
    expect(edges.find((e) => e.id === "e2")!.className).toBe("edge-true");
    expect(edges.find((e) => e.id === "e3")!.className).toBe("edge-false");
  });

  it("replaces existing edge from the same port", () => {
    const edges = connect(toFlowEdges(scenario), "c", "false", "start");
    const fromFalse = edges.filter((e) => e.source === "c" && e.sourceHandle === "false");
    expect(fromFalse).toHaveLength(1);
    expect(fromFalse[0].target).toBe("start");
    expect(edges).toHaveLength(3);
  });
});

describe("node helpers", () => {
  it("creates node with defaults", () => {
    const node = createNode(waitDescriptor, { x: 10, y: 20 });
    expect(node.config).toEqual({ mode: "duration", amount: 10, unit: "seconds" });
    expect(node.type).toBe("wait");
  });

  it("evaluates field visibility", () => {
    const amount = waitDescriptor.fields[1];
    const time = waitDescriptor.fields[3];
    expect(isFieldVisible(amount, {}, waitDescriptor.fields)).toBe(true);
    expect(isFieldVisible(time, {}, waitDescriptor.fields)).toBe(false);
    expect(isFieldVisible(time, { mode: "untilTime" }, waitDescriptor.fields)).toBe(true);
  });

  it("summarizes wait node", () => {
    const node = createNode(waitDescriptor, { x: 0, y: 0 });
    expect(nodeSummary(node, waitDescriptor)).toBe("10 секунд");
    expect(nodeSummary({ ...node, config: { mode: "untilTime", time: "09:30" } }, waitDescriptor)).toBe("до 09:30");
  });
});

describe("auto layout", () => {
  it("places nodes in layers from start ignoring back edges", () => {
    const positions = autoLayout(toFlowNodes(scenario), toFlowEdges(scenario), "start");
    expect(positions.get("start")!.x).toBeLessThan(positions.get("c")!.x);
    expect(positions.get("c")!.x).toBeLessThan(positions.get("end")!.x);
  });
});
