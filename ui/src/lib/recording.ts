import type { NodeDefinition, VariableDefinition } from "../api/types";
import type { BlockClipboard } from "./clipboard";

/** Записанное действие (как его присылает сервер). */
export interface RecordedAction {
  kind: "goto" | "click" | "fill" | "select" | "check" | "uncheck" | "press" | "tab";
  selector: string | null;
  value: string | null;
  url: string | null;
  note: string | null;
  label: string | null;
  secret: boolean;
}

export interface RecordingSource {
  browser: "pepa" | "adspower";
  profileId: string | null;
}

const settings = { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null };

const short = (text: string | null, max = 28) => {
  const t = (text ?? "").trim();
  return t.length > max ? `${t.slice(0, max)}…` : t;
};

/**
 * Записанные действия → цепочка блоков: открыть браузер (или профиль AdsPower) с первым адресом, затем по блоку на
 * действие. Пароль не записывается — вместо него подставляется переменная «password» (вводится при запуске).
 */
export function recordingToBlocks(actions: RecordedAction[], source: RecordingSource): BlockClipboard {
  const nodes: NodeDefinition[] = [];
  const variables: VariableDefinition[] = [];
  const add = (type: string, config: Record<string, string | boolean>, label: string | null, note: string | null = null) => {
    nodes.push({
      id: `rec${nodes.length + 1}`,
      type,
      label,
      position: { x: nodes.length * 280, y: 0 },
      config,
      settings,
      note,
    });
  };

  const rest = [...actions];
  const firstUrl = rest[0]?.kind === "goto" ? rest.shift()?.url ?? null : null;
  if (source.browser === "adspower") {
    add("adspower.open", { profileId: source.profileId ?? "", ...(firstUrl ? { url: firstUrl } : {}) }, null);
  } else {
    add("browser.open", { browser: "msedge", keepLogin: true, ...(firstUrl ? { url: firstUrl } : {}) }, null);
  }

  for (const action of rest) {
    const selector = action.selector ?? "";
    switch (action.kind) {
      case "goto":
        add("browser.goto", { url: action.url ?? "" }, null, action.note);
        break;
      case "click":
        add("browser.click", { selector }, action.label ? `Нажать «${short(action.label)}»` : null, action.note);
        break;
      case "fill":
        if (action.secret) {
          if (!variables.some((v) => v.name === "password")) {
            variables.push({ name: "password", type: "text", default: null, description: "Пароль (вводится при запуске)", isInput: true });
          }
          add("browser.fill", { selector, text: "{{ vars.password }}" }, "Ввести пароль", action.note);
        } else {
          add("browser.fill", { selector, text: action.value ?? "" }, action.label ? `Ввести «${short(action.label)}»` : null, action.note);
        }
        break;
      case "select":
        add("browser.select", { selector, option: action.value ?? "" }, action.label ? `Выбрать «${short(action.label)}»` : null, action.note);
        break;
      case "check":
      case "uncheck":
        add("browser.check", { selector, state: action.kind }, action.label ? `${action.kind === "check" ? "Отметить" : "Снять"} «${short(action.label)}»` : null, action.note);
        break;
      case "press":
        add("browser.press", { key: action.value ?? "Enter", selector }, null, action.note);
        break;
      case "tab":
        add("browser.tab", { which: "newest" }, null, action.note);
        break;
    }
  }

  const edges = nodes.slice(1).map((node, i) => ({ id: `rec-e${i + 1}`, source: nodes[i].id, sourcePort: "out", target: node.id }));
  return { kind: "pepa-blocks", version: 1, nodes, edges, variables };
}
