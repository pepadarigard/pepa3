import { AlertTriangle, Ban, CheckCircle2, Hourglass, Loader2, Pause, Play, RotateCcw, SearchCheck, SkipForward, Square, ThumbsUp, XCircle, type LucideIcon } from "lucide-react";
import type { ProcessAction, ProcessStatus, ProcessSummary, ResumeAction, StepState } from "../api/types";
import { useProcessControl } from "../api/hooks";
import { ru } from "../i18n/ru";
import { durationBetween, formatMoment, relative } from "../lib/format";
import { processTone, stepTone } from "../lib/status";
import { navigate } from "../router";
import { Badge, Button } from "./ui";
import { useFeedback } from "./feedback";

export const processIcons: Record<ProcessStatus, LucideIcon> = {
  running: Loader2,
  waiting: Hourglass,
  paused: Pause,
  needsAttention: AlertTriangle,
  completed: CheckCircle2,
  failed: XCircle,
  cancelled: Ban,
};

export function ProcessStatusBadge({ status }: { status: ProcessStatus }) {
  return (
    <Badge tone={processTone[status]} pulse={status === "running"}>
      {ru.processStatus[status]}
    </Badge>
  );
}

export function StepStateBadge({ state }: { state: StepState }) {
  return (
    <Badge tone={stepTone[state]} pulse={state === "running"}>
      {ru.stepState[state]}
    </Badge>
  );
}

const actionIcons: Record<ProcessAction, LucideIcon> = {
  pause: Pause,
  cancel: Square,
  continue: Play,
  verify: SearchCheck,
  retry: RotateCcw,
  markSucceeded: ThumbsUp,
  skip: SkipForward,
};

/** Кнопки только для действий, разрешённых движком в текущем состоянии процесса. */
export function ProcessActions({ process, size, exclude = [] }: { process: ProcessSummary; size?: "small"; exclude?: ProcessAction[] }) {
  const control = useProcessControl();
  const feedback = useFeedback();
  const actions = process.allowedActions.filter((a) => !exclude.includes(a));

  const run = async (action: ProcessAction) => {
    const confirmation = confirmationFor(process, action);
    if (confirmation && !(await feedback.confirm({ title: ru.actions[action].label, text: confirmation, confirmLabel: ru.actions[action].label, danger: action === "cancel" }))) {
      return;
    }
    try {
      const result = await control.mutateAsync({ id: process.id, action: action as "pause" | "cancel" | ResumeAction });
      feedback.toast(result.message, "success");
    } catch (e) {
      feedback.error(e);
    }
  };

  if (actions.length === 0) return null;
  // Главное действие — первое (кроме остановки), остальные — второстепенные.
  const primary = actions.find((a) => a !== "cancel" && a !== "pause");
  return (
    <div className="row wrap" onClick={(e) => e.stopPropagation()}>
      {actions.map((action) => (
        <Button
          key={action}
          size={size}
          icon={actionIcons[action]}
          variant={action === primary ? "primary" : action === "cancel" ? "danger" : "default"}
          title={ru.actions[action].hint}
          busy={control.isPending && control.variables?.action === action && control.variables.id === process.id}
          onClick={() => run(action)}
        >
          {ru.actions[action].label}
        </Button>
      ))}
    </div>
  );
}

function confirmationFor(process: ProcessSummary, action: ProcessAction): string | null {
  switch (action) {
    case "cancel":
      return ru.confirmations.cancel;
    case "retry":
      return process.attention === "unknownStep" ? ru.confirmations.retryUnknown : null;
    case "markSucceeded":
      return ru.confirmations.markSucceeded;
    case "skip":
      return ru.confirmations.skip;
    default:
      return null;
  }
}

/** Таблица процессов. */
export function ProcessTable({ processes, showScenario = true, showActions = true }: { processes: ProcessSummary[]; showScenario?: boolean; showActions?: boolean }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Состояние</th>
            {showScenario && <th>Сценарий</th>}
            <th>Где сейчас / итог</th>
            <th>Запущен</th>
            <th>Длительность</th>
            <th>Шагов</th>
            {showActions && <th />}
          </tr>
        </thead>
        <tbody>
          {processes.map((p) => (
            <tr key={p.id} className="clickable" onClick={() => navigate({ page: "process", id: p.id })}>
              <td>
                <ProcessStatusBadge status={p.status} />
              </td>
              {showScenario && (
                <td>
                  <div style={{ fontWeight: 560 }}>{p.scenarioName}</div>
                  <div className="faint small mono">#{p.shortId}</div>
                </td>
              )}
              <td style={{ maxWidth: 360 }}>
                <div className="truncate" title={p.statusMessage ?? undefined}>
                  {p.status === "waiting" && p.wakeAt
                    ? `Ждёт до ${formatMoment(p.wakeAt)} (${relative(p.wakeAt)})`
                    : p.statusMessage ?? (p.currentNodeLabel ? `Шаг «${p.currentNodeLabel}»` : "—")}
                </div>
              </td>
              <td className="muted">{formatMoment(p.createdAt)}</td>
              <td className="muted">{durationBetween(p.createdAt, p.finishedAt)}</td>
              <td className="muted">{p.stepCount}</td>
              {showActions && (
                <td style={{ textAlign: "right" }}>
                  <ProcessActions process={p} size="small" exclude={["verify", "markSucceeded", "skip", "retry"]} />
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
