import { describe, expect, it } from "vitest";
import type { NodeTypeDescriptor, VariableDefinition } from "../api/types";
import { copyBlocks, pasteBlocks } from "./clipboard";
import type { PepaEdge, PepaNode } from "./graph";

const descriptor = (type: string, variableField?: string): NodeTypeDescriptor =>
  ({
    type,
    category: "logic",
    title: type,
    description: "",
    icon: "box",
    effect: "none",
    supportsRetry: false,
    supportsVerification: false,
    isStart: type === "start",
    isEnd: false,
    summaryField: null,
    outputs: [],
    fields: variableField ? [{ name: variableField, label: "", kind: "variable", required: false, default: null, options: null, placeholder: null, help: null, min: null, max: null, visibleWhen: null, lookup: null }] : [],
    resourceHint: null,
  }) as unknown as NodeTypeDescriptor;

const node = (id: string, type: string, x: number, y: number, config: Record<string, string> = {}, selected = true): PepaNode => ({
  id,
  type: "pepa",
  position: { x, y },
  selected,
  data: { definition: { id, type, label: null, position: { x, y }, config, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null } },
});

const edge = (id: string, source: string, target: string, port = "out"): PepaEdge => ({ id, source, target, sourceHandle: port, targetHandle: "in" });

const descriptors = new Map([
  ["start", descriptor("start")],
  ["read", descriptor("read", "saveTo")],
  ["log", descriptor("log")],
]);

describe("копирование блоков", () => {
  const variables: VariableDefinition[] = [
    { name: "text", type: "text", default: null, description: "Прочитанное", isInput: false },
    { name: "other", type: "number", default: 1, description: "", isInput: false },
  ];
  const nodes = [
    node("s", "start", 0, 0),
    node("a", "read", 100, 50, { saveTo: "text" }),
    node("b", "log", 400, 50),
    node("c", "log", 700, 50, {}, false),
  ];
  const edges = [edge("e1", "s", "a"), edge("e2", "a", "b"), edge("e3", "b", "c"), edge("e4", "a", "b", "error")];

  it("берёт выбранные блоки без «Начала», связи только внутри выбранного и нужные переменные", () => {
    const copied = copyBlocks(nodes, edges, variables, descriptors, (id) => id === "s")!;
    expect(copied.nodes.map((n) => n.id)).toEqual(["a", "b"]);
    expect(copied.edges.map((e) => e.id)).toEqual(["e2", "e4"]);
    expect(copied.variables.map((v) => v.name)).toEqual(["text"]);
  });

  it("вставляет с новыми идентификаторами, сохраняет расположение и добавляет только недостающие переменные", () => {
    const copied = copyBlocks(nodes, edges, variables, descriptors, (id) => id === "s")!;
    const pasted = pasteBlocks(copied, { x: 1000, y: 500 }, []);
    expect(pasted.nodes).toHaveLength(2);
    expect(pasted.nodes.every((n) => n.id !== "a" && n.id !== "b" && n.data.definition.id === n.id && n.selected)).toBe(true);
    expect(pasted.nodes.map((n) => n.position)).toEqual([{ x: 1000, y: 500 }, { x: 1300, y: 500 }]);
    const [first, second] = pasted.nodes.map((n) => n.id);
    expect(pasted.edges.map((e) => [e.source, e.target, e.sourceHandle])).toEqual([[first, second, "out"], [first, second, "error"]]);
    expect(pasted.variables.map((v) => v.name)).toEqual(["text"]);
    expect(pasteBlocks(copied, { x: 0, y: 0 }, variables).variables).toEqual([]);
  });

  it("ничего не копирует без выделения", () => {
    expect(copyBlocks(nodes.map((n) => ({ ...n, selected: false })), edges, variables, descriptors, () => false)).toBeNull();
  });
});
