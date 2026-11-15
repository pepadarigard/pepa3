import { describe, expect, it } from "vitest";
import type { ProcessSummary } from "../api/types";
import { stopGroups } from "./groupActions";

const stopped = (id: string, extra: Partial<ProcessSummary> = {}): ProcessSummary => ({
  id, shortId: id, scenarioId: "s", scenarioName: "Профили", scenarioVersion: 1, status: "needsAttention", attention: "stepFailed",
  pauseReason: "none", statusMessage: null, createdAt: "2026-01-01T10:00:00Z", updatedAt: "2026-01-01T10:00:00Z", finishedAt: null,
  stepCount: 3, currentNodeId: "click", currentNodeLabel: "Нажать", wakeAt: null, waitKind: "none", waitingFor: null, parentProcessId: null,
  allowedActions: ["retry", "retryWithChanges", "retryWithNewBlock", "skip", "cancel"], ...extra,
}) as ProcessSummary;

describe("действия для всех остановившихся на одном", () => {
  it("собирает процессы с одним сценарием, блоком и причиной и предлагает безопасные для всех действия", () => {
    const groups = stopGroups([stopped("a"), stopped("b"), stopped("c", { currentNodeId: "fill" }), stopped("d", { status: "waiting" })]);
    expect(groups).toHaveLength(1);
    expect(groups[0].processes.map((p) => p.id)).toEqual(["a", "b"]);
    // «Исправить» и «Пропустить» — у каждого своё решение.
    expect(groups[0].actions).toEqual(["retryWithNewBlock", "retry", "cancel"]);
  });

  it("не предлагает повтор для всех при неизвестном итоге — действие могло выполниться", () => {
    const unknown = { attention: "unknownStep" as const, allowedActions: ["verify", "retry", "markSucceeded", "markFailed", "cancel"] as ProcessSummary["allowedActions"] };
    const [group] = stopGroups([stopped("a", unknown), stopped("b", unknown)]);
    expect(group.actions).toEqual(["verify", "cancel"]);
  });

  it("не предлагает повтор для всех, если действие шага уже выполнилось (или в группе есть такие)", () => {
    const ran = { allowedActions: ["retry", "skip", "cancel"] as ProcessSummary["allowedActions"] };
    expect(stopGroups([stopped("a", ran), stopped("b", ran)])[0].actions).toEqual(["cancel"]);
    expect(stopGroups([stopped("a"), stopped("b", ran)])[0].actions).toEqual(["cancel"]);
    // Ошибка движка — тоже не «подтверждено, что шаг не выполнен».
    const engine = { attention: "engineError" as const, allowedActions: ["retry", "cancel"] as ProcessSummary["allowedActions"] };
    expect(stopGroups([stopped("a", engine), stopped("b", engine)])[0].actions).toEqual(["cancel"]);
  });

  it("после восстановления базы из копии «Продолжить» — по одному: процесс мог уже сделать следующие шаги", () => {
    const restored = { attention: "restoredFromBackup" as const, allowedActions: ["continue", "cancel"] as ProcessSummary["allowedActions"] };
    expect(stopGroups([stopped("a", restored), stopped("b", restored)])[0].actions).toEqual(["cancel"]);
  });
});
