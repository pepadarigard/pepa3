import type { Json, VariableDefinition } from "../api/types";

/**
 * Поля «Исправить и повторить» (D-085). Секретная переменная и переменная, в значении которой есть секрет (сервер
 * показывает его маской «••••»), — пустое поле: пусто — без изменений; отправить маску обратно значило бы записать её
 * вместо секрета.
 */
export function isConcealed(v: VariableDefinition, hidden: ReadonlySet<string>): boolean {
  return v.secret || hidden.has(v.name);
}

/** Начальное значение поля: текст и число как есть, остальное — JSON (так его поймёт parseInput); скрытое — пусто. */
export function initialText(v: VariableDefinition, value: Json | undefined, hidden: ReadonlySet<string>): string | boolean {
  if (isConcealed(v, hidden)) return "";
  if (v.type === "boolean") return value === true;
  if (value === null || value === undefined) return "";
  if (v.type === "text" || v.type === "number") return String(value);
  return JSON.stringify(value, null, 2);
}

/** Многострочное поле: список, объект, «любое» и текст с переносами строк (обычное поле убрало бы переносы). */
export function isMultiline(v: VariableDefinition, initial: string | boolean | undefined): boolean {
  return v.type === "list" || v.type === "object" || v.type === "any" ||
    (v.type === "text" && typeof initial === "string" && initial.includes("\n"));
}
