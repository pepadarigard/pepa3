import type { ScenarioDefinition } from "../api/types";

/**
 * Сценарий для файла экспорта — без значений секретных переменных: файл пересылают и выкладывают, а пароль в нём
 * остался бы открытым. После импорта значения вводятся заново (при запуске или в переменных сценария).
 */
export function exportableScenario(definition: ScenarioDefinition): { definition: ScenarioDefinition; cleared: string[] } {
  const cleared = definition.variables.filter((v) => v.secret && v.default !== null && v.default !== "").map((v) => v.name);
  return {
    definition: {
      ...definition,
      variables: definition.variables.map((v) => (v.secret ? { ...v, default: null } : v)),
    },
    cleared,
  };
}
