import { describe, expect, it } from "vitest";
import type { ScenarioDefinition } from "../api/types";
import { exportableScenario } from "./export";

const scenario = {
  name: "Вход",
  variables: [
    { name: "login", type: "text", default: "anna", description: "", isInput: true },
    { name: "password", type: "text", default: "S3cr3t-Pa55", description: "", isInput: true, secret: true },
    { name: "pin", type: "text", default: null, description: "", isInput: false, secret: true },
  ],
  nodes: [],
  edges: [],
} as unknown as ScenarioDefinition;

describe("экспорт сценария", () => {
  it("значения секретных переменных не попадают в файл, остальное — как есть", () => {
    const { definition, cleared } = exportableScenario(scenario);
    expect(JSON.stringify(definition)).not.toContain("S3cr3t-Pa55");
    expect(definition.variables.map((v) => v.default)).toEqual(["anna", null, null]);
    expect(definition.variables[1].secret).toBe(true);
    expect(cleared).toEqual(["password"]);
    expect(scenario.variables[1].default).toBe("S3cr3t-Pa55");
  });
});
