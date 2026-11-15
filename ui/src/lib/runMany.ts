import type { Json, VariableDefinition } from "../api/types";

/** Сколько процессов можно запустить за раз (как на сервере). */
export const MaxRuns = 500;

/** Входные переменные, которые могут меняться от запуска к запуску: не секретные и не «да/нет». */
export function manyCandidates(inputs: VariableDefinition[]): VariableDefinition[] {
  const candidates = inputs.filter((v) => !v.secret && v.type !== "boolean");
  // Профиль AdsPower — первым: чаще всего запускают один сценарий для нескольких профилей.
  return [...candidates.filter((v) => v.lookup === "adspower-profiles"), ...candidates.filter((v) => v.lookup !== "adspower-profiles")];
}

type Parse = (v: VariableDefinition, raw: string | boolean | undefined) => { value: Json } | { error: string };

/** Наборы входных данных: общие значения и по одному значению меняющейся переменной на строку (пустые строки — пропуск). */
export function batchRuns(varying: VariableDefinition, text: string, common: Record<string, Json>, parse: Parse):
  { runs: Record<string, Json>[] } | { error: string } {
  const lines = text.split("\n").map((l) => l.trim()).filter((l) => l !== "");
  if (lines.length === 0) return { error: `Укажите значения «${varying.name}» — по одному на строку.` };
  if (lines.length > MaxRuns) return { error: `За один раз можно запустить не больше ${MaxRuns} процессов (указано ${lines.length}).` };
  const runs: Record<string, Json>[] = [];
  for (const [index, line] of lines.entries()) {
    const parsed = parse(varying, line);
    if ("error" in parsed) return { error: `Строка ${index + 1} («${line}»): ${parsed.error}` };
    runs.push({ ...common, [varying.name]: parsed.value });
  }
  return { runs };
}
