import type { EventState, ProcessStatus, ProcessSummary, StepState } from "../api/types";
import { ru } from "../i18n/ru";
import { formatMoment, relative } from "./format";

export type Tone = "info" | "warn" | "neutral" | "danger" | "success" | "muted" | "attention";

export const processTone: Record<ProcessStatus, Tone> = {
  running: "info",
  waiting: "warn",
  paused: "neutral",
  needsAttention: "attention",
  completed: "success",
  failed: "danger",
  cancelled: "muted",
};

export const stepTone: Record<StepState, Tone> = {
  planned: "neutral",
  running: "info",
  waiting: "warn",
  succeeded: "success",
  failed: "danger",
  unknown: "attention",
  skipped: "muted",
  cancelled: "muted",
};

export const eventTone: Record<EventState, Tone> = {
  received: "info",
  matched: "warn",
  delivered: "success",
  unmatched: "muted",
  conflict: "attention",
  ignored: "muted",
};

/** Чего ждёт процесс — коротко, для таблиц. */
export function waitText(p: ProcessSummary): string | null {
  if (p.waitKind === "timer" && p.wakeAt) return `Ждёт до ${formatMoment(p.wakeAt)} (${relative(p.wakeAt)})`;
  if (p.waitKind === "retry" && p.wakeAt) return `Повтор после ошибки ${relative(p.wakeAt)}`;
  if ((p.waitKind === "event" || p.waitKind === "resource") && p.waitingFor) {
    const limit = p.wakeAt ? `, не дольше чем до ${formatMoment(p.wakeAt)}` : "";
    return p.waitingFor.startsWith("событие получено") || p.waitingFor.startsWith("вызванный сценарий завершился")
      ? capitalize(p.waitingFor)
      : `Ждёт ${p.waitingFor}${limit}`;
  }
  return null;
}

const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);

export const isActive = (status: ProcessStatus) =>
  status === "running" || status === "waiting" || status === "paused" || status === "needsAttention";

/** Объяснение состояния процесса простыми словами. */
export function explainProcess(p: ProcessSummary): { title: string; text: string } {
  const step = p.currentNodeLabel ? `«${p.currentNodeLabel}»` : "";
  switch (p.status) {
    case "running":
      return {
        title: step ? `Выполняется шаг ${step}` : "Выполняется",
        text: "Процесс работает. Его можно приостановить — он остановится между шагами.",
      };
    case "waiting":
      return {
        title: waitText(p) ?? (p.wakeAt ? `Ждёт до ${formatMoment(p.wakeAt)} (${relative(p.wakeAt)})` : "Ожидает"),
        text:
          "Ожидание сохранено и не занимает ресурсов. Если закрыть приложение, после запуска процесс будет " +
          "приостановлен — его нужно будет возобновить вручную.",
      };
    case "paused":
      if (p.pauseReason === "breakpoint") {
        return {
          title: "Пауза перед блоком (отладка)",
          text:
            (p.statusMessage ?? "Процесс остановился перед блоком.") +
            " «Продолжить» выполнит этот блок, «Продолжить без остановок» — весь процесс до конца.",
        };
      }
      return p.pauseReason === "restart"
        ? {
            title: "Приостановлен после перезапуска",
            text:
              p.statusMessage ??
              "Приложение закрывалось. Ничего не продолжается само — проверьте процесс и нажмите «Продолжить».",
          }
        : { title: "Приостановлен вами", text: "Нажмите «Продолжить», чтобы возобновить с того же места." };
    case "needsAttention":
      return { title: attentionTitle(p), text: p.statusMessage ?? "" };
    case "completed":
      return { title: ru.processStatus.completed, text: p.statusMessage ?? "Процесс завершён успешно." };
    case "failed":
      return { title: ru.processStatus.failed, text: p.statusMessage ?? "" };
    case "cancelled":
      return { title: ru.processStatus.cancelled, text: p.statusMessage ?? "Процесс остановлен пользователем." };
  }
}

function attentionTitle(p: ProcessSummary): string {
  switch (p.attention) {
    case "unknownStep":
      return "Неизвестно, выполнилось ли действие";
    case "stepFailed":
      return "Шаг не выполнен";
    case "stepLimit":
      return "Слишком много шагов — возможно, цикл";
    default:
      return "Нужна ваша проверка";
  }
}

/** Подсказка, что делать, для процессов, требующих решения. */
export function attentionAdvice(p: ProcessSummary): string | null {
  if (p.status !== "needsAttention") return null;
  switch (p.attention) {
    case "unknownStep":
      return p.allowedActions.includes("verify")
        ? "Чтобы не выполнить действие дважды, процесс остановлен. Начните с «Проверить и продолжить»: Pepa сверит результат, не повторяя действие."
        : "Чтобы не выполнить действие дважды, процесс остановлен. Проверьте результат сами и выберите: считать выполненным, повторить или остановить.";
    case "stepFailed":
      return "Подтверждено, что шаг не выполнен — повтор безопасен. Устраните причину и нажмите «Выполнить шаг ещё раз» или пропустите шаг.";
    case "stepLimit":
      return "Проверьте журнал: если цикл задуман, нажмите «Продолжить», иначе остановите процесс и исправьте сценарий.";
    default:
      return "Проверьте журнал процесса.";
  }
}
