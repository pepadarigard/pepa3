import type { ProcessAction, ProcessSummary } from "../api/types";

/** Остановившиеся на одном и том же: один сценарий, один блок, одна причина. */
export interface StopGroup {
  key: string;
  scenarioName: string;
  nodeLabel: string | null;
  attention: ProcessSummary["attention"];
  processes: ProcessSummary[];
  /** Действия, которые можно применить ко всем сразу. */
  actions: ProcessAction[];
}

/**
 * Действия для всех сразу — только те, что не требуют разбираться в каждом процессе отдельно: проверить (ничего не
 * повторяет), продолжить, взять исправленный блок, остановить и повторить шаг после подтверждённой ошибки. «Повторить» при
 * неизвестном итоге (могло выполниться), «Считать выполненным/невыполненным», «Пропустить» и «Исправить» (свои данные у
 * каждого) — по одному.
 */
const Groupable: ProcessAction[] = ["verify", "continue", "retryWithNewBlock", "retry", "cancel"];

export function stopGroups(processes: ProcessSummary[]): StopGroup[] {
  const groups = new Map<string, ProcessSummary[]>();
  for (const p of processes) {
    if (p.status !== "needsAttention") continue;
    const key = `${p.scenarioId}|${p.currentNodeId ?? ""}|${p.attention}`;
    groups.set(key, [...(groups.get(key) ?? []), p]);
  }
  const result: StopGroup[] = [];
  for (const [key, members] of groups) {
    if (members.length < 2) continue;
    const actions = Groupable.filter((action) =>
      members.every((p) => p.allowedActions.includes(action)) && !(action === "retry" && members[0].attention === "unknownStep"));
    if (actions.length === 0) continue;
    result.push({
      key,
      scenarioName: members[0].scenarioName,
      nodeLabel: members[0].currentNodeLabel,
      attention: members[0].attention,
      processes: members,
      actions,
    });
  }
  return result;
}
