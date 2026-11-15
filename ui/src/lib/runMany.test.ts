import { describe, expect, it } from "vitest";
import type { VariableDefinition } from "../api/types";
import { parseInput } from "../components/RunDialog";
import { batchRuns, manyCandidates } from "./runMany";

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
