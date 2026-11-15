import { useState } from "react";
import { Layers } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import type { ProcessAction, ProcessSummary, ResumeAction } from "../api/types";
import { ru } from "../i18n/ru";
import { stopGroups } from "../lib/groupActions";
import { explainProcess } from "../lib/status";
import { useFeedback } from "./feedback";
import { Button } from "./ui";

/**
 * Несколько процессов остановились на одном и том же (одна ошибка у пакета запусков, D-088): одно решение для всех —
 * повторить шаг, взять исправленный блок, проверить, остановить. Решения, которые требуют смотреть на каждый процесс
 * отдельно, здесь не предлагаются.
 */
/** Процессы группы, которые после проверки всё ещё стоят на том же шаге по той же причине («#id: почему»). */
async function stillStopped(members: ProcessSummary[], failures: string[]): Promise<string[]> {
  const failed = new Set(failures.map((f) => f.slice(1, f.indexOf(":"))));
  let now: ProcessSummary[];
  try {
    now = await api.processes("attention", undefined, 100_000);
  } catch {
    return [];
  }
  const byId = new Map(now.map((p) => [p.id, p]));
  return members
    .filter((p) => !failed.has(p.shortId))
    .flatMap((p) => {
      const current = byId.get(p.id);
      return current && current.currentNodeId === p.currentNodeId && current.attention === p.attention
        ? [`#${p.shortId}: ${current.statusMessage ?? "решите вручную"}`]
        : [];
    });
}

export function GroupActions({ processes }: { processes: ProcessSummary[] }) {
  const groups = stopGroups(processes);
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState<string | null>(null);
  if (groups.length === 0) return null;

  const apply = async (key: string, members: ProcessSummary[], action: ProcessAction) => {
    const label = ru.actions[action].label;
    if (!(await feedback.confirm({
      title: `${label} — для всех`,
      text: `${label}: ${members.length} ${members.length < 5 ? "процесса" : "процессов"}. ${ru.actions[action].hint}`,
      confirmLabel: label,
      danger: action === "cancel",
    }))) return;
    setBusy(`${key}|${action}`);
    const failures: string[] = [];
    try {
      for (const p of members) {
        try {
          if (action === "cancel") await api.cancel(p.id);
          // Решение — для процесса, каким его видели: изменился с тех пор (например, повтор из другого окна сделал итог
          // неизвестным) — сервер откажет, и процесс попадёт в «Не вышло».
          else await api.resume(p.id, action as ResumeAction, undefined, p.revision);
        } catch (e) {
          failures.push(`#${p.shortId}: ${e instanceof Error ? e.message : String(e)}`);
        }
      }
      // Проверка может не дать однозначного ответа — тогда процесс по-прежнему ждёт решения, и «Готово» было бы неправдой.
      const pending = action === "verify" ? await stillStopped(members, failures) : [];
      const done = members.length - failures.length - pending.length;
      const parts = [`Готово: ${done} из ${members.length}`];
      if (pending.length > 0) parts.push(`Ждут решения — ${pending.length}: ${pending.join("; ")}`);
      if (failures.length > 0) parts.push(`Не вышло — ${failures.join("; ")}`);
      feedback.toast(parts.join(". "), failures.length > 0 ? "error" : pending.length > 0 ? "info" : "success");
    } finally {
      setBusy(null);
      void queryClient.invalidateQueries({ queryKey: ["processes"] });
      void queryClient.invalidateQueries({ queryKey: ["overview"] });
    }
  };

  return (
    <div className="stack" style={{ gap: 8 }} data-testid="group-actions">
      {groups.map((group) => (
        <div key={group.key} className="callout">
          <Layers size={18} className="callout-icon" />
          <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
            <div>
              <strong>{group.processes.length} {group.processes.length < 5 ? "процесса" : "процессов"} «{group.scenarioName}»</strong>{" "}
              остановились на одном шаге{group.nodeLabel ? ` «${group.nodeLabel}»` : ""}: {explainProcess(group.processes[0]).title.toLowerCase()}.
            </div>
            <div className="row wrap">
              {group.actions.map((action) => (
                <Button key={action} size="small" variant={action === "cancel" ? "danger" : "default"} busy={busy === `${group.key}|${action}`}
                  title={ru.actions[action].hint} onClick={() => apply(group.key, group.processes, action)}>
                  {ru.actions[action].label} — для всех
                </Button>
              ))}
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}
