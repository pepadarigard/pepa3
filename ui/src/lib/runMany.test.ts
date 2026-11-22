import { describe, expect, it } from "vitest";
import type { VariableDefinition } from "../api/types";
import { parseInput } from "../components/RunDialog";
import { batchRuns, manyCandidates, parseTable, sendKey, tableColumns, tableRunCount, tableRuns } from "./runMany";

const variable = (name: string, type: VariableDefinition["type"], extra: Partial<VariableDefinition> = {}): VariableDefinition =>
  ({ name, type, secret: false, isInput: true, default: null, description: null, ...extra }) as unknown as VariableDefinition;

describe("несколько запусков", () => {
  it("предлагает несекретные переменные, профиль AdsPower — первым", () => {
    const inputs = [variable("chat", "text"), variable("password", "text", { secret: true }), variable("ok", "boolean"),
      variable("profile", "text", { lookup: "adspower-profiles" } as Partial<VariableDefinition>)];
    expect(manyCandidates(inputs).map((v) => v.name)).toEqual(["profile", "chat"]);
  });

  it("делает по набору на строку, пустые строки пропускает, ошибку называет строкой", () => {
    const result = batchRuns(variable("profile", "text"), "k1\n\n  k2  \n", { chat: "@ivan" }, parseInput);
    expect(result).toEqual({ runs: [{ chat: "@ivan", profile: "k1" }, { chat: "@ivan", profile: "k2" }] });
    expect(batchRuns(variable("count", "number"), "1\nмного", {}, parseInput)).toEqual({ error: expect.stringContaining("Строка 2 («много»)") });
    expect(batchRuns(variable("profile", "text"), " \n", {}, parseInput)).toEqual({ error: expect.stringContaining("по одному на строку") });
  });
});

describe("несколько запусков из таблицы", () => {
  const inputs = [variable("profile", "text"), variable("login", "text"), variable("count", "number"), variable("chat", "text")];

  it("берёт столбцы по именам из первой строки, остальные входные — общие", () => {
    const result = tableRuns("profile\tlogin\tcount\nk1\tanna\t2\nk2\tboris\t3\n", inputs, { chat: "@ivan" }, parseInput);
    expect(result).toEqual({
      runs: [{ chat: "@ivan", profile: "k1", login: "anna", count: 2 }, { chat: "@ivan", profile: "k2", login: "boris", count: 3 }],
      columns: ["profile", "login", "count"],
    });
    expect(tableRuns("profile;login\nk1;anna", inputs, {}, parseInput)).toEqual({ runs: [{ profile: "k1", login: "anna" }], columns: ["profile", "login"] });
  });

  it("называет неизвестный столбец и строку с ошибкой", () => {
    expect(tableRuns("profil\tlogin\nk1\tanna", inputs, {}, parseInput)).toEqual({ error: expect.stringContaining("«profil»") });
    expect(tableRuns("profile\tcount\nk1\tмного", inputs, {}, parseInput)).toEqual({ error: expect.stringContaining("Строка 2, «count»") });
    expect(tableRuns("profile", inputs, {}, parseInput)).toEqual({ error: expect.stringContaining("Вставьте таблицу") });
  });

  it("ячейка Excel с переносом строки и кавычками — одна ячейка, а не два запуска", () => {
    const text = 'message\tprofile\r\n"Привет\nвсем"\tk1\r\n"Он сказал ""да"""\tk2\r\n';
    expect(tableRuns(text, [variable("message", "text"), variable("profile", "text")], {}, parseInput)).toEqual({
      runs: [{ message: "Привет\nвсем", profile: "k1" }, { message: 'Он сказал "да"', profile: "k2" }],
      columns: ["message", "profile"],
    });
    expect(tableRunCount(text)).toBe(2);
    expect(tableColumns(text)).toEqual(["message", "profile"]);
  });

  it("незакрытая кавычка и лишнее после кавычки — ошибка с номером строки", () => {
    expect(parseTable('profile\tmessage\nk1\t"Привет')).toEqual({ error: expect.stringContaining("Строка 2: кавычка не закрыта") });
    expect(parseTable('profile\tmessage\nk1\t"Привет" всем')).toEqual({ error: expect.stringContaining("Строка 2: после закрывающей кавычки") });
    expect(tableRunCount('profile\tmessage\nk1\t"Привет')).toBe(0);
  });

  it("строка с меньшим числом значений не запускается, явно пустое значение — можно", () => {
    expect(tableRuns("profile;login\nk1", inputs, {}, parseInput)).toEqual({ error: expect.stringContaining("Строка 2: значений меньше") });
    expect(tableRuns("profile;login\nk1;", inputs, {}, parseInput)).toEqual({ runs: [{ profile: "k1", login: "" }], columns: ["profile", "login"] });
    expect(tableRuns("profile;login\nk1;a;b", inputs, {}, parseInput)).toEqual({ error: expect.stringContaining("значений больше") });
  });

  it("«да/нет» — только узнаваемыми словами, в том числе «ИСТИНА» из русского Excel", () => {
    const flags = [variable("profile", "text"), variable("dry", "boolean")];
    expect(tableRuns("profile\tdry\nk1\tИСТИНА\nk2\tЛОЖЬ\nk3\tда\nk4\tno", flags, {}, parseInput)).toEqual({
      runs: [{ profile: "k1", dry: true }, { profile: "k2", dry: false }, { profile: "k3", dry: true }, { profile: "k4", dry: false }],
      columns: ["profile", "dry"],
    });
    expect(tableRuns("profile\tdry\nk1\tага", flags, {}, parseInput)).toEqual({ error: expect.stringContaining("Строка 2, «dry»: нужно «да» или «нет» («ага»)") });
    expect(tableRuns("profile\tdry\nk1\t", flags, {}, parseInput)).toEqual({ error: expect.stringContaining("ячейка пустая") });
  });
});

describe("sendKey", () => {
  it("подходит серверу и не повторяется", () => {
    const keys = new Set(Array.from({ length: 200 }, () => sendKey("r")));
    expect(keys.size).toBe(200);
    for (const key of keys) expect(key).toMatch(/^[A-Za-z0-9_-]{6,64}$/);
  });
});
