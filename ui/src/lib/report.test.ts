import { describe, expect, it } from "vitest";
import type { ProcessDetails } from "../api/types";
import { processReport } from "./report";

const details = {
  process: {
    id: "p-123456", shortId: "123456", scenarioId: "s1", scenarioName: "Вход на сайт", scenarioVersion: 3, status: "needsAttention",
    attention: "unknownStep", pauseReason: "none", statusMessage: "Неизвестно, нажата ли кнопка", createdAt: "2026-10-27T10:00:00Z",
    updatedAt: "2026-10-27T10:01:00Z", finishedAt: null, stepCount: 2, currentNodeId: "click", currentNodeLabel: "Нажать «Войти»",
  },
  variables: { password: "секрет-123", login: "anna" },
  lastError: null,
  steps: [
    { id: 1, nodeId: "fill", nodeType: "browser.fill", nodeLabel: "Ввести пароль", attempt: 1, effect: "external", state: "succeeded",
      input: { text: "секрет-123" }, output: null, port: "out", error: null, resolution: null, plannedAt: "2026-10-27T10:00:01Z", startedAt: null, finishedAt: null },
    { id: 2, nodeId: "click", nodeType: "browser.click", nodeLabel: "Нажать «Войти»", attempt: 2, effect: "external", state: "unknown",
      input: null, output: { snapshot: { image: "001.jpg", url: "https://bank.example/cabinet?id=anna", title: "Кабинет" } }, port: null,
      error: "Связь с браузером прервалась", resolution: null, plannedAt: "2026-10-27T10:00:02Z", startedAt: null, finishedAt: null },
  ],
  journal: [{ id: 1, stepId: 2, nodeId: "click", at: "2026-10-27T10:00:03Z", level: "error", message: "Браузер закрылся" }],
  activeNodeIds: ["click"],
  heldResources: ["adspower:k1"],
} as unknown as ProcessDetails;

describe("отчёт о процессе", () => {
  it("ход процесса, ошибки и журнал — без значений переменных и параметров шагов", () => {
    const text = processReport(details, "0.1.0");
    expect(text).toContain("Pepa 0.1.0");
    expect(text).toContain("«Вход на сайт», версия 3");
    expect(text).toContain("Неизвестно, нажата ли кнопка");
    expect(text).toContain("«Нажать «Войти»» (browser.click) попытка 2 — Итог неизвестен");
    expect(text).toContain("ошибка: Связь с браузером прервалась");
    expect(text).toContain("[error] Браузер закрылся");
    expect(text).toContain("Переменные (только имена): password, login");
    expect(text).toContain("adspower:k1");
    expect(text).not.toContain("секрет-123");
    expect(text).not.toContain("anna");
    expect(text).toContain("снимок экрана в момент ошибки — на странице процесса");
    expect(text).not.toContain("bank.example");
  });
});
