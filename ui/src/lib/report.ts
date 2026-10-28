import type { ProcessDetails } from "../api/types";
import { ru } from "../i18n/ru";
import { formatDateTime } from "./format";

const MAX_STEPS = 60;
const MAX_JOURNAL = 120;

/**
 * Текстовый отчёт о процессе — чтобы отправить его тому, кто помогает разобраться. В отчёт не попадают значения
 * переменных и параметры шагов (там бывают личные данные, тексты сообщений, пароли): только ход процесса, состояния,
 * ошибки и журнал.
 */
export function processReport(details: ProcessDetails, version: string | null): string {
  const p = details.process;
  const lines: string[] = [];
  lines.push("Отчёт о процессе Pepa" + (version ? ` (Pepa ${version})` : ""));
  lines.push(`Сценарий: «${p.scenarioName}», версия ${p.scenarioVersion}`);
  lines.push(`Процесс: ${p.id}`);
  lines.push(`Состояние: ${ru.processStatus[p.status]}` + (p.attention !== "none" ? ` (${p.attention})` : "") +
    (p.pauseReason !== "none" ? `, пауза: ${p.pauseReason}` : ""));
  if (p.statusMessage) lines.push(`Пояснение: ${p.statusMessage}`);
  lines.push(`Запущен: ${formatDateTime(p.createdAt)}` + (p.finishedAt ? `, завершён: ${formatDateTime(p.finishedAt)}` : "") + `, шагов: ${p.stepCount}`);
  if (p.currentNodeLabel) lines.push(`Сейчас: «${p.currentNodeLabel}»`);
  if (details.activeNodeIds.length > 1) lines.push(`Ветвей сейчас: ${details.activeNodeIds.length}`);
  if (details.heldResources.length > 0) lines.push(`Занятые ресурсы: ${details.heldResources.join(", ")}`);
  const variables = Object.keys(details.variables);
  if (variables.length > 0) lines.push(`Переменные (только имена): ${variables.join(", ")}`);

  lines.push("", `Шаги${details.steps.length > MAX_STEPS ? ` (последние ${MAX_STEPS} из ${details.steps.length})` : ""}:`);
  for (const s of details.steps.slice(-MAX_STEPS)) {
    const parts = [
      `#${s.id}`,
      formatDateTime(s.startedAt ?? s.plannedAt),
      `«${s.nodeLabel}» (${s.nodeType})`,
      s.attempt > 1 ? `попытка ${s.attempt}` : null,
      `— ${ru.stepState[s.state]}`,
      s.port ? `→ ${s.port}` : null,
    ].filter(Boolean);
    lines.push(parts.join(" "));
    if (s.error) lines.push(`    ошибка: ${s.error}`);
    if (s.resolution) lines.push(`    решение: ${s.resolution}`);
    // Сам снимок и адрес страницы в отчёт не попадают (там бывают личные данные) — только что он есть.
    if (s.output?.snapshot) lines.push("    снимок экрана в момент ошибки — на странице процесса (в отчёт не входит)");
  }

  lines.push("", `Журнал${details.journal.length > MAX_JOURNAL ? ` (последние ${MAX_JOURNAL} из ${details.journal.length})` : ""}:`);
  for (const j of details.journal.slice(-MAX_JOURNAL)) {
    lines.push(`${formatDateTime(j.at)} [${j.level}] ${j.message}`);
  }
  return lines.join("\n");
}
