import { useEffect, useState } from "react";
import { Pause, Play, Square } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import type { ProcessSummary } from "../api/types";
import { useFeedback } from "./feedback";
import { Button } from "./ui";

/** Выбранные галочками процессы и действия над всеми ними: приостановить, продолжить, остановить. */
export function useProcessSelection(processes: ProcessSummary[]) {
  const [ids, setIds] = useState<Set<string>>(new Set());
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState<string | null>(null);
  // Выбор держится только за процессы, которые ещё в списке: ушедший (остановился, завершился) снимается навсегда — иначе,
  // вернувшись в список, он снова оказался бы выбранным, и «Остановить» остановило бы и его.
  const present = new Set(processes.map((p) => p.id));
  const presentKey = processes.map((p) => p.id).join(",");
  useEffect(() => {
    const now = new Set(presentKey ? presentKey.split(",") : []);
    setIds((previous) => {
      const next = new Set([...previous].filter((id) => now.has(id)));
      return next.size === previous.size ? previous : next;
    });
  }, [presentKey]);
  const chosen = processes.filter((p) => ids.has(p.id));

  const onSelect = (changed: string[], on: boolean) => {
    const next = new Set([...ids].filter((id) => present.has(id)));
    for (const id of changed) {
      if (on) next.add(id);
      else next.delete(id);
    }
    setIds(next);
  };

  const apply = async (action: "pause" | "continue" | "cancel", label: string) => {
    const targets = chosen.filter((p) => p.allowedActions.includes(action));
    if (targets.length === 0) {
      feedback.toast(`«${label}» не подходит ни к одному из выбранных процессов`, "error");
      return;
    }
    if (action === "cancel" && !(await feedback.confirm({
      title: `${label}: ${targets.length}`,
      text: `Остановить выбранные процессы (${targets.length}): ${targets.map((p) => `#${p.shortId}`).join(", ")}? Они завершатся и не продолжатся.`,
      confirmLabel: label,
      danger: true,
    }))) return;
    setBusy(action);
    const failures: string[] = [];
    for (const p of targets) {
      try {
        if (action === "pause") await api.pause(p.id);
        else if (action === "cancel") await api.cancel(p.id);
        else await api.resume(p.id, "continue");
      } catch (e) {
        failures.push(`#${p.shortId}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }
    setBusy(null);
    setIds(new Set());
    void queryClient.invalidateQueries({ queryKey: ["processes"] });
    void queryClient.invalidateQueries({ queryKey: ["overview"] });
    const skipped = chosen.length - targets.length;
    const note = skipped > 0 ? ` (${skipped} — не подходит)` : "";
    if (failures.length === 0) feedback.toast(`${label}: ${targets.length}${note}`, "success");
    else feedback.toast(`${label}: ${targets.length - failures.length} из ${targets.length}${note}. Не вышло — ${failures.join("; ")}`, "error");
  };

  const bar = chosen.length > 0 && (
    <div className="row wrap" style={{ gap: 8, padding: "10px 14px" }} data-testid="selection-bar">
      <span className="small">Выбрано: {chosen.length}</span>
      <Button size="small" icon={Pause} busy={busy === "pause"} onClick={() => apply("pause", "Приостановить")}>Приостановить</Button>
      <Button size="small" icon={Play} busy={busy === "continue"} onClick={() => apply("continue", "Продолжить")}>Продолжить</Button>
      <Button size="small" icon={Square} variant="danger" busy={busy === "cancel"} onClick={() => apply("cancel", "Остановить")}>Остановить</Button>
      <Button size="small" onClick={() => setIds(new Set())}>Снять выбор</Button>
    </div>
  );

  return { selected: ids, onSelect, bar };
}
