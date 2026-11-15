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
    for (const p of members) {
      try {
        if (action === "cancel") await api.cancel(p.id);
        else await api.resume(p.id, action as ResumeAction);
      } catch (e) {
        failures.push(`#${p.shortId}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }
    setBusy(null);
    void queryClient.invalidateQueries({ queryKey: ["processes"] });
    void queryClient.invalidateQueries({ queryKey: ["overview"] });
    if (failures.length === 0) feedback.toast(`Готово: ${members.length} из ${members.length}`, "success");
    else feedback.toast(`Готово: ${members.length - failures.length} из ${members.length}. Не вышло — ${failures.join("; ")}`, "error");
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
