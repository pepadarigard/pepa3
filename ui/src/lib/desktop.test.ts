import { describe, expect, it } from "vitest";
import type { DesktopElement } from "../api/types";
import { canPick, elementConfig, elementReliability } from "./desktop";

const types = [{ value: "", label: "любой" }, { value: "Button", label: "кнопка" }, { value: "Edit", label: "поле ввода" }];
const element = (patch: Partial<DesktopElement>): DesktopElement => ({
  depth: 1, name: "Сохранить", controlType: "Button", automationId: "save", enabled: true, uniqueName: true, uniqueId: true, ...patch,
});

describe("выбор элемента окна", () => {
  it("подставляет подпись, код (если он один в окне) и известный тип", () => {
    expect(elementConfig(element({}), types)).toEqual({ name: "Сохранить", nameMatch: "exact", automationId: "save", controlType: "Button" });
    expect(elementConfig(element({ uniqueId: false }), types).automationId).toBe("");
    expect(elementConfig(element({ controlType: "SplitButton" }), types).controlType).toBe("");
    // Без подписи — даже неуникальный код: иначе блоку не по чему искать.
    expect(elementConfig(element({ name: "", uniqueId: false }), types).automationId).toBe("save");
    expect(canPick(element({ name: "", automationId: "" }))).toBe(false);
  });

  it("для чтения текста подпись не ставится, если элемент находится по коду: текст может измениться", () => {
    expect(elementConfig(element({ controlType: "Text", name: "Баланс: 10" }), types, true)).toMatchObject({ name: "", automationId: "save" });
    // Кода нет или он не один — искать можно только по подписи.
    expect(elementConfig(element({ name: "Баланс: 10", uniqueId: false }), types, true)).toMatchObject({ name: "Баланс: 10", automationId: "" });
  });

  it("оценивает, насколько надёжно элемент найдётся", () => {
    expect(elementReliability(element({}))).toBe("id");
    expect(elementReliability(element({ automationId: "" }))).toBe("name");
    expect(elementReliability(element({ automationId: "", uniqueName: false }))).toBe("weak");
  });
});
