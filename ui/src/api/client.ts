import type {
  Info,
  NodeTypes,
  Overview,
  ProcessDetails,
  ProcessSummary,
  ResumeAction,
  Scenario,
  ScenarioDefinition,
  ScenarioSummary,
  Settings,
  ValidationReport,
  Json,
} from "./types";

const TOKEN_KEY = "pepa.token";
let memoryToken: string | null = null;

/** Токен приходит во фрагменте адреса (#token=…), который не отправляется на сервер. */
export function initToken(): string | null {
  const match = window.location.hash.match(/token=([^&]+)/);
  if (match) {
    const token = decodeURIComponent(match[1]);
    try {
      sessionStorage.setItem(TOKEN_KEY, token);
    } catch {
      // Хранилище недоступно — токен останется только в памяти.
    }
    memoryToken = token;
    window.history.replaceState(null, "", window.location.pathname + "#/");
    return token;
  }
  try {
    memoryToken = sessionStorage.getItem(TOKEN_KEY) ?? memoryToken;
  } catch {
    // Нет доступа к хранилищу.
  }
  return memoryToken;
}

export function setToken(token: string) {
  memoryToken = token;
  try {
    sessionStorage.setItem(TOKEN_KEY, token);
  } catch {
    // Нет доступа к хранилищу.
  }
}

export function getToken() {
  return memoryToken;
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly validation?: ValidationReport,
  ) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api${path}`, {
      method,
      headers: {
        "X-Pepa-Token": memoryToken ?? "",
        ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new ApiError("Нет связи с движком Pepa. Проверьте, что приложение запущено.", 0);
  }

  const text = await response.text();
  const data = text ? safeParse(text) : undefined;
  if (!response.ok) {
    const body = (data && typeof data === "object" ? data : {}) as { error?: unknown; validation?: ValidationReport };
    const message = typeof body.error === "string" && body.error ? body.error : `Ошибка ${response.status}`;
    throw new ApiError(message, response.status, body.validation ?? undefined);
  }
  return data as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

export const api = {
  info: () => request<Info>("GET", "/info"),
  nodeTypes: () => request<NodeTypes>("GET", "/node-types"),
  overview: () => request<Overview>("GET", "/overview"),

  scenarios: () => request<ScenarioSummary[]>("GET", "/scenarios"),
  scenario: (id: string) => request<Scenario>("GET", `/scenarios/${id}`),
  createScenario: (definition: Partial<ScenarioDefinition>) => request<Scenario>("POST", "/scenarios", definition),
  saveScenario: (id: string, definition: ScenarioDefinition, expectedVersion: number) =>
    request<Scenario>("PUT", `/scenarios/${id}`, { definition, expectedVersion }),
  deleteScenario: (id: string) => request<{ message: string }>("DELETE", `/scenarios/${id}`),
  duplicateScenario: (id: string) => request<Scenario>("POST", `/scenarios/${id}/duplicate`),
  validate: (definition: ScenarioDefinition) => request<ValidationReport>("POST", "/scenarios/validate", definition),
  run: (id: string, inputs?: Record<string, Json>) =>
    request<{ processId: string }>("POST", `/scenarios/${id}/run`, { inputs: inputs ?? null }),

  processes: (view: "active" | "finished" | "attention" | "all", scenarioId?: string, limit = 200) =>
    request<ProcessSummary[]>(
      "GET",
      `/processes?view=${view}&limit=${limit}${scenarioId ? `&scenarioId=${encodeURIComponent(scenarioId)}` : ""}`,
    ),
  process: (id: string) => request<ProcessDetails>("GET", `/processes/${id}`),
  pause: (id: string) => request<{ message: string }>("POST", `/processes/${id}/pause`),
  cancel: (id: string) => request<{ message: string }>("POST", `/processes/${id}/cancel`),
  resume: (id: string, action: ResumeAction) =>
    request<{ message: string }>("POST", `/processes/${id}/resume`, { action }),

  settings: () => request<Settings>("GET", "/settings"),
  saveSettings: (settings: Settings) => request<Settings>("PUT", "/settings", settings),
};
