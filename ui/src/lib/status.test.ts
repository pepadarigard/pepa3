import { describe, expect, it } from "vitest";
import type { NodeTypeDescriptor, ProcessSummary } from "../api/types";
import { nodeSummary } from "./graph";
import { waitText } from "./status";

const base: ProcessSummary = {
  id: "p1234567890",
  shortId: "567890",
  scenarioId: "s",
  scenarioName: "Сценарий",
  scenarioVersion: 1,
  status: "waiting",
  attention: "none",
  pauseReason: "none",
  statusMessage: null,
  createdAt: "2026-01-01T10:00:00Z",
  updatedAt: "2026-01-01T10:00:00Z",
  finishedAt: null,
  stepCount: 3,
  currentNodeId: "w",
  currentNodeLabel: "Ждать",
  wakeAt: null,
  allowedActions: ["pause", "cancel"],
  waitKind: "none",
  waitingFor: null,
  parentProcessId: null,
};

describe("waitText", () => {
  it("описывает ожидание события и ресурса", () => {
    expect(waitText({ ...base, waitKind: "event", waitingFor: "событие «manual / reply» с ключом «A»" })).toBe(
      "Ждёт событие «manual / reply» с ключом «A»",
    );
    expect(waitText({ ...base, waitKind: "resource", waitingFor: "ресурс «profile:1»", wakeAt: "2026-01-01T10:05:00Z" })).toMatch(
      /^Ждёт ресурс «profile:1», не дольше чем до /,
    );
  });

  it("сообщает, что событие получено, без слова «ждёт»", () => {
    expect(waitText({ ...base, status: "paused", waitKind: "event", waitingFor: "событие получено — применится после возобновления" })).toBe(
      "Событие получено — применится после возобновления",
    );
  });

  it("без ожидания — ничего", () => {
    expect(waitText({ ...base, status: "running" })).toBeNull();
  });
});

describe("nodeSummary для блоков событий и сценариев", () => {
  const descriptor = (type: string, kind: "text" | "scenario", summaryField: string): NodeTypeDescriptor => ({
    type,
    category: "flow",
    title: type,
    description: "",
    icon: "box",
    effect: "none",
    supportsVerification: false,
    isStart: false,
    isEnd: false,
    outputs: [],
    fields: [{ name: summaryField, label: "", kind, required: true, default: kind === "text" ? "manual" : null, help: null, placeholder: null, options: null, min: null, max: null, visibleWhen: null, lookup: null }],
    summaryField,
    supportsRetry: false,
    resourceHint: null,
  });
  const node = (type: string, config: Record<string, string>) => ({
    id: "n", type, label: null, position: { x: 0, y: 0 }, config, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null,
  });

  it("показывает источник, вид и ключ события", () => {
    expect(nodeSummary(node("event.wait", { eventType: "reply", correlationKey: "{{ vars.order }}" }), descriptor("event.wait", "text", "source"))).toBe(
      "manual / reply · {{ vars.order }}",
    );
  });

  it("показывает название вызываемого сценария вместо идентификатора", () => {
    const names = new Map([["abc", "Дочерний"]]);
    const d = descriptor("scenario.call", "scenario", "scenario");
    expect(nodeSummary(node("scenario.call", { scenario: "abc" }), d, names)).toBe("Дочерний");
    expect(nodeSummary(node("scenario.call", { scenario: "zzz" }), d, names)).toBe("сценарий не найден");
  });
});
