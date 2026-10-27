import type { DesktopElement, Json } from "../api/types";

type SelectOption = { value: string; label: string };

/**
 * Параметры блока для выбранного элемента окна: подпись (целиком), код — если он в окне один (иначе код не помогает и
 * лишь мешает), тип — если блок его знает. Пустые значения стирают прежние, чтобы не осталось условий от другого элемента.
 * Подпись — дополнительная проверка (кнопка с тем же кодом, но другой подписью не будет нажата), но если подпись — это
 * и есть читаемый текст (`nameIsContent`), при уникальном коде она не ставится: иначе блок не нашёл бы элемент с новым текстом.
 */
export function elementConfig(element: DesktopElement, controlTypes: SelectOption[], nameIsContent = false): Record<string, Json> {
  const useId = element.automationId.length > 0 && (element.uniqueId || !element.name);
  return {
    name: nameIsContent && useId && element.uniqueId ? "" : element.name,
    nameMatch: "exact",
    // Неуникальный код без подписи всё же лучше, чем ничего: если подойдёт несколько элементов, блок остановится.
    automationId: useId ? element.automationId : "",
    controlType: controlTypes.some((o) => o.value === element.controlType) ? element.controlType : "",
    // Выбран один элемент — «взять первый/последний» от прошлого выбора не должно молча остаться.
    match: "strict",
  };
}

/** Насколько надёжно блок найдёт элемент по этим признакам. */
export function elementReliability(element: DesktopElement): "id" | "name" | "weak" {
  if (element.automationId && element.uniqueId) return "id";
  if (element.name && element.uniqueName) return "name";
  return "weak";
}

/** Блок находит элементы по подписи или коду — элемент без того и другого так не выбрать. */
export function canPick(element: DesktopElement): boolean {
  return element.name.length > 0 || element.automationId.length > 0;
}
