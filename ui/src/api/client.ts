import type {
  AdsPowerProfile,
  TelegramStatus,
  Info,
  NodeTypes,
  PepaEvent,
  Resource,
  SaveTrigger,
  Trigger,
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

  events: (view: "open" | "conflict" | "unmatched" | "all" = "all", limit = 200) =>
    request<PepaEvent[]>("GET", `/events?view=${view}&limit=${limit}`),
  publishEvent: (event: { source: string; type: string; correlationKey: string | null; externalId: string | null; payload: Json | null }) =>
    request<{ eventId: number; duplicate: boolean }>("POST", "/events", event),
  resolveEvent: (id: number, subscriptionId: number | null) =>
    request<{ message: string }>("POST", `/events/${id}/resolve`, { subscriptionId }),
  rerouteEvent: (id: number) => request<{ message: string }>("POST", `/events/${id}/reroute`),

  resources: () => request<Resource[]>("GET", "/resources"),
  releaseResource: (key: string) => request<{ message: string }>("POST", "/resources/release", { key }),

  triggers: (scenarioId?: string) =>
    request<Trigger[]>("GET", `/triggers${scenarioId ? `?scenarioId=${encodeURIComponent(scenarioId)}` : ""}`),
  createTrigger: (trigger: SaveTrigger) => request<Trigger>("POST", "/triggers", trigger),
  updateTrigger: (id: string, trigger: SaveTrigger) => request<Trigger>("PUT", `/triggers/${id}`, trigger),
  deleteTrigger: (id: string) => request<{ message: string }>("DELETE", `/triggers/${id}`),
  runTrigger: (id: string) => request<{ processId: string }>("POST", `/triggers/${id}/run`),
  dismissTrigger: (id: string) => request<{ message: string }>("POST", `/triggers/${id}/dismiss`),

  checkAdsPower: () => request<{ ok: boolean; message: string }>("POST", "/integrations/adspower/check"),
  adsPowerProfiles: () => request<AdsPowerProfile[]>("GET", "/integrations/adspower/profiles"),

  telegram: () => request<TelegramStatus>("GET", "/integrations/telegram"),
  telegramLogin: (login: { apiId: number; apiHash: string | null; phone: string }) =>
    request<TelegramStatus>("POST", "/integrations/telegram/login", login),
  telegramContinue: (value: string) => request<TelegramStatus>("POST", "/integrations/telegram/login/continue", { value }),
  telegramLogout: () => request<TelegramStatus>("POST", "/integrations/telegram/logout"),

  settings: () => request<Settings>("GET", "/settings"),
  saveSettings: (settings: Settings) => request<Settings>("PUT", "/settings", settings),
};
