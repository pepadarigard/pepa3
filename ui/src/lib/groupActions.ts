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
 * повторяет), продолжить, взять исправленный блок, остановить и повторить шаг после подтверждённой ошибки. По одному:
 * «Повторить», если итог неизвестен или действие шага уже выполнилось (повтор сделал бы его снова), «Продолжить» после
 * восстановления базы из копии (процесс мог уже сделать следующие шаги — каждый проверяют отдельно), «Считать
 * выполненным/невыполненным», «Пропустить» и «Исправить» (свои данные у каждого).
 */
const Groupable: ProcessAction[] = ["verify", "continue", "retryWithNewBlock", "retry", "cancel"];

/** Можно ли это действие для всех процессов группы сразу. */
function groupable(action: ProcessAction, members: ProcessSummary[]): boolean {
  if (!members.every((p) => p.allowedActions.includes(action))) return false;
  // Подтверждённая ошибка, и действие шага не выполнялось, — ровно тогда движок разрешает и «Исправить и повторить».
  if (action === "retry") return members.every((p) => p.attention === "stepFailed" && p.allowedActions.includes("retryWithChanges"));
  if (action === "continue") return members.every((p) => p.attention !== "restoredFromBackup");
  return true;
}

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
    const actions = Groupable.filter((action) => groupable(action, members));
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
