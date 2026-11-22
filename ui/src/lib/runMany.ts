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

/** Разделитель таблицы: табуляция (так копирует Excel), иначе «;» или «,» — по первой строке. */
function delimiterOf(text: string): string {
  const first = text.replace(/\r\n?/g, "\n").split("\n").find((l) => l.trim() !== "") ?? "";
  return first.includes("\t") ? "\t" : first.includes(";") ? ";" : ",";
}

/**
 * Разобрать таблицу с учётом кавычек (как её копирует Excel): в ячейке в кавычках разделитель и перенос строки — часть
 * текста, а «""» — кавычка. Пустые строки (вне кавычек) пропускаются. Незакрытая кавычка — ошибка: иначе ячейка с
 * переносом строки стала бы двумя запусками со сдвинутыми значениями.
 */
export function parseTable(text: string): { rows: string[][] } | { error: string } {
  const source = text.replace(/\r\n?/g, "\n");
  const delimiter = delimiterOf(source);
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = "";
  let quoted = false;
  let wasQuoted = false;
  let line = 1;
  let rowLine = 1;
  const endCell = () => {
    row.push(wasQuoted ? cell : cell.trim());
    cell = "";
    wasQuoted = false;
  };
  const endRow = () => {
    endCell();
    if (row.length > 1 || row[0] !== "") rows.push(row);
    row = [];
    rowLine = line;
  };
  for (let i = 0; i < source.length; i++) {
    const c = source[i];
    if (quoted) {
      if (c === '"') {
        if (source[i + 1] === '"') {
          cell += '"';
          i++;
        } else {
          quoted = false;
        }
      } else {
        if (c === "\n") line++;
        cell += c;
      }
      continue;
    }
    if (c === '"' && cell.trim() === "" && !wasQuoted) {
      quoted = true;
      wasQuoted = true;
      cell = "";
    } else if (c === delimiter) {
      endCell();
    } else if (c === "\n") {
      line++;
      endRow();
    } else if (wasQuoted) {
      if (c.trim() !== "") return { error: `Строка ${rowLine}: после закрывающей кавычки — «${c}». Кавычку внутри ячейки пишут дважды: «""».` };
    } else {
      cell += c;
    }
  }
  if (quoted) return { error: `Строка ${rowLine}: кавычка не закрыта.` };
  endRow();
  return { rows };
}

const TrueWords = ["да", "yes", "true", "1", "истина"];
const FalseWords = ["нет", "no", "false", "0", "ложь"];

/**
 * Наборы из таблицы (например, вставленной из Excel): первая строка — имена входных переменных, каждая следующая — запуск.
 * Разделитель — табуляция (так копирует Excel), иначе «;» или «,». Входные переменные, которых нет в таблице, — общие.
 * В каждой строке — столько значений, сколько столбцов (пустое — явно), «да/нет» — только узнаваемыми словами: иначе
 * значения сдвинулись бы или молча стали «нет», и процессы запустились бы не с теми данными.
 */
export function tableRuns(text: string, inputs: VariableDefinition[], common: Record<string, Json>, parse: Parse):
  { runs: Record<string, Json>[]; columns: string[] } | { error: string } {
  const table = parseTable(text);
  if ("error" in table) return table;
  const rows = table.rows;
  if (rows.length < 2) return { error: "Вставьте таблицу: первая строка — имена переменных, дальше — по строке на запуск." };
  const header = rows[0];
  const columns: VariableDefinition[] = [];
  for (const name of header) {
    const variable = inputs.find((v) => v.name === name);
    if (!variable) return { error: `В первой строке «${name}» — такой входной переменной у сценария нет (есть: ${inputs.map((v) => v.name).join(", ")}).` };
    if (columns.includes(variable)) return { error: `Столбец «${name}» повторяется.` };
    columns.push(variable);
  }
  if (rows.length - 1 > MaxRuns) return { error: `За один раз можно запустить не больше ${MaxRuns} процессов (строк ${rows.length - 1}).` };
  const runs: Record<string, Json>[] = [];
  for (const [index, values] of rows.slice(1).entries()) {
    if (values.length !== columns.length) {
      return {
        error: values.length > columns.length
          ? `Строка ${index + 2}: значений больше, чем столбцов (${values.length} из ${columns.length}).`
          : `Строка ${index + 2}: значений меньше, чем столбцов (${values.length} из ${columns.length}) — пустое значение укажите явно (разделитель в конце).`,
      };
    }
    const run: Record<string, Json> = { ...common };
    for (const [at, variable] of columns.entries()) {
      const cell = values[at];
      let raw: string | boolean = cell;
      if (variable.type === "boolean") {
        const word = cell.toLowerCase();
        if (TrueWords.includes(word)) raw = true;
        else if (FalseWords.includes(word)) raw = false;
        else return { error: `Строка ${index + 2}, «${variable.name}»: нужно «да» или «нет»${cell === "" ? " (ячейка пустая)" : ` («${cell}»)`}.` };
      }
      const parsed = parse(variable, raw);
      if ("error" in parsed) return { error: `Строка ${index + 2}, «${variable.name}»: ${parsed.error}` };
      run[variable.name] = parsed.value;
    }
    runs.push(run);
  }
  return { runs, columns: columns.map((v) => v.name) };
}

/** Сколько запусков в таблице (строк после заголовка); незаконченная таблица — 0. */
export function tableRunCount(text: string): number {
  const table = parseTable(text);
  return "error" in table ? 0 : Math.max(0, table.rows.length - 1);
}

/** Имена столбцов таблицы (первая непустая строка) — чтобы не спрашивать общее значение для того, что задано в таблице. */
export function tableColumns(text: string): string[] {
  const table = parseTable(text);
  return "error" in table ? [] : (table.rows[0] ?? []).filter((c) => c !== "");
}

/**
 * Ключ отправки (запуск, несколько запусков): повторная отправка с тем же ключом не запускает процессы снова. Сервер
 * принимает 6–64 знака из латиницы, цифр, «-» и «_».
 */
export function sendKey(prefix: string): string {
  const random = typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID().replace(/-/g, "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}${Math.random().toString(36).slice(2)}`;
  return `${prefix}${random}`.slice(0, 40).padEnd(8, "0");
}
