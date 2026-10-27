import { describe, expect, it } from "vitest";
import { desktopRecordingToBlocks, recordingToBlocks, type RecordedAction } from "./recording";

const action = (kind: RecordedAction["kind"], extra: Partial<RecordedAction> = {}): RecordedAction =>
  ({ kind, selector: null, value: null, url: null, note: null, label: null, secret: false, ...extra });

describe("запись → блоки", () => {
  it("открывает браузер с первым адресом и строит цепочку действий", () => {
    const blocks = recordingToBlocks(
      [
        action("goto", { url: "https://shop.example/login" }),
        action("fill", { selector: "#email", value: "a@b.c", label: "Почта" }),
        action("fill", { selector: "input[name=\"pass\"]", secret: true, value: "" }),
        action("click", { selector: "button:text-is(\"Войти\")", label: "Войти" }),
        action("select", { selector: "#city", value: "Москва", label: "Город" }),
        action("uncheck", { selector: "#news", label: "Рассылка" }),
        action("press", { selector: "#q", value: "Enter" }),
        action("tab"),
        action("goto", { url: "https://shop.example/cart" }),
      ],
      { browser: "pepa", profileId: null },
    );
    expect(blocks.nodes.map((n) => n.type)).toEqual([
      "browser.open", "browser.fill", "browser.fill", "browser.click", "browser.select", "browser.check", "browser.press", "browser.tab", "browser.goto",
    ]);
    expect(blocks.nodes[0].config).toEqual({ browser: "msedge", keepLogin: true, url: "https://shop.example/login" });
    expect(blocks.nodes[2].config).toEqual({ selector: "input[name=\"pass\"]", text: "{{ vars.password }}" });
    expect(blocks.nodes[3].label).toBe("Нажать «Войти»");
    expect(blocks.nodes[5].config).toEqual({ selector: "#news", state: "uncheck" });
    expect(blocks.variables.map((v) => [v.name, v.isInput, v.secret])).toEqual([["password", true, true]]);
    expect(blocks.edges).toHaveLength(blocks.nodes.length - 1);
    expect(blocks.edges.every((e, i) => e.source === blocks.nodes[i].id && e.target === blocks.nodes[i + 1].id)).toBe(true);
  });

  it("для AdsPower начинает с профиля", () => {
    const blocks = recordingToBlocks([action("click", { selector: "#go" })], { browser: "adspower", profileId: "k1abc" });
    expect(blocks.nodes[0]).toMatchObject({ type: "adspower.open", config: { profileId: "k1abc" } });
    expect(blocks.nodes[1]).toMatchObject({ type: "browser.click", config: { selector: "#go" } });
  });

  it("у каждого поля пароля своя секретная переменная, повторный ввод в то же поле — та же", () => {
    const blocks = recordingToBlocks(
      [
        action("fill", { selector: "#old", secret: true }),
        action("fill", { selector: "#new", secret: true }),
        action("fill", { selector: "#old", secret: true }),
      ],
      { browser: "pepa", profileId: null },
    );
    expect(blocks.nodes.slice(1).map((n) => n.config.text)).toEqual(["{{ vars.password }}", "{{ vars.password2 }}", "{{ vars.password }}"]);
    expect(blocks.variables.map((v) => [v.name, v.secret])).toEqual([["password", true], ["password2", true]]);
  });
});

describe("запись в окнах Windows → блоки", () => {
  it("шаги становятся цепочкой блоков с заметками, пароль — переменной", () => {
    const blocks = desktopRecordingToBlocks([
      { type: "desktop.click", config: { window: "Блокнот", process: "notepad", name: "Сохранить" }, label: "Нажать «Сохранить»", note: "проверьте", secret: false },
      { type: "desktop.fill", config: { window: "Блокнот", process: "notepad", name: "Пароль", text: "{{ vars.password }}" }, label: "Ввести пароль", note: null, secret: true },
    ]);
    expect(blocks.nodes.map((n) => [n.type, n.label, n.note])).toEqual([
      ["desktop.click", "Нажать «Сохранить»", "проверьте"],
      ["desktop.fill", "Ввести пароль", null],
    ]);
    expect(blocks.edges).toEqual([{ id: "rec-e1", source: "rec1", sourcePort: "out", target: "rec2" }]);
    expect(blocks.variables.map((v) => [v.name, v.isInput, v.secret])).toEqual([["password", true, true]]);
    expect(desktopRecordingToBlocks([]).variables).toEqual([]);
  });

  it("два поля пароля — две секретные переменные", () => {
    const fill = (name: string, variable: string) => ({
      type: "desktop.fill", config: { window: "Смена пароля", name, text: `{{ vars.${variable} }}` }, label: "Ввести пароль", note: null,
      secret: true, secretVariable: variable,
    });
    const blocks = desktopRecordingToBlocks([fill("Старый пароль", "password"), fill("Новый пароль", "password2")]);
    expect(blocks.variables.map((v) => [v.name, v.secret, v.description])).toEqual([
      ["password", true, "Пароль 1 (вводится при запуске)"],
      ["password2", true, "Пароль 2 (вводится при запуске)"],
    ]);
  });
});
