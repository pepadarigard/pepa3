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

/**
 * Наборы из таблицы (например, вставленной из Excel): первая строка — имена входных переменных, каждая следующая — запуск.
 * Разделитель — табуляция (так копирует Excel), иначе «;» или «,». Входные переменные, которых нет в таблице, — общие.
 */
export function tableRuns(text: string, inputs: VariableDefinition[], common: Record<string, Json>, parse: Parse):
  { runs: Record<string, Json>[]; columns: string[] } | { error: string } {
  const rows = text.split("\n").map((l) => l.replace(/\r$/, "")).filter((l) => l.trim() !== "");
  if (rows.length < 2) return { error: "Вставьте таблицу: первая строка — имена переменных, дальше — по строке на запуск." };
  const delimiter = rows[0].includes("\t") ? "\t" : rows[0].includes(";") ? ";" : ",";
  const cells = (row: string) => row.split(delimiter).map((c) => c.trim().replace(/^"(.*)"$/, "$1"));
  const header = cells(rows[0]);
  const columns: VariableDefinition[] = [];
  for (const name of header) {
    const variable = inputs.find((v) => v.name === name);
    if (!variable) return { error: `В первой строке «${name}» — такой входной переменной у сценария нет (есть: ${inputs.map((v) => v.name).join(", ")}).` };
    if (columns.includes(variable)) return { error: `Столбец «${name}» повторяется.` };
    columns.push(variable);
  }
  if (rows.length - 1 > MaxRuns) return { error: `За один раз можно запустить не больше ${MaxRuns} процессов (строк ${rows.length - 1}).` };
  const runs: Record<string, Json>[] = [];
  for (const [index, row] of rows.slice(1).entries()) {
    const values = cells(row);
    if (values.length > columns.length) return { error: `Строка ${index + 2}: значений больше, чем столбцов (${values.length} из ${columns.length}).` };
    const run: Record<string, Json> = { ...common };
    for (const [at, variable] of columns.entries()) {
      const parsed = parse(variable, variable.type === "boolean" ? ["да", "yes", "true", "1"].includes((values[at] ?? "").toLowerCase()) : values[at] ?? "");
      if ("error" in parsed) return { error: `Строка ${index + 2}, «${variable.name}»: ${parsed.error}` };
      run[variable.name] = parsed.value;
    }
    runs.push(run);
  }
  return { runs, columns: columns.map((v) => v.name) };
}

/** Имена столбцов таблицы (первая непустая строка) — чтобы не спрашивать общее значение для того, что задано в таблице. */
export function tableColumns(text: string): string[] {
  const first = text.split("\n").find((l) => l.trim() !== "")?.replace(/\r$/, "") ?? "";
  const delimiter = first.includes("\t") ? "\t" : first.includes(";") ? ";" : ",";
  return first.split(delimiter).map((c) => c.trim().replace(/^"(.*)"$/, "$1")).filter((c) => c !== "");
}
