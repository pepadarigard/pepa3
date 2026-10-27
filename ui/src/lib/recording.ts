import type { Json, NodeDefinition, RecordedDesktopStep, VariableDefinition } from "../api/types";
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
  const passwordFields = new Map<string, string>();
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
          // У каждого поля пароля — своя переменная (старый и новый пароль — разные значения).
          let name = passwordFields.get(selector);
          if (!name) {
            name = passwordFields.size === 0 ? "password" : `password${passwordFields.size + 1}`;
            passwordFields.set(selector, name);
            variables.push({ name, type: "text", default: null, description: "Пароль (вводится при запуске)", isInput: true, secret: true });
          }
          add("browser.fill", { selector, text: `{{ vars.${name} }}` }, "Ввести пароль", action.note);
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

/**
 * Записанные шаги в окнах Windows → цепочка блоков (сервер уже собрал параметры блоков). Пояснения к шагам становятся
 * заметками блоков — их видно на схеме. Пароль не записывается — вместо него переменная «password» (вводится при запуске).
 */
export function desktopRecordingToBlocks(steps: RecordedDesktopStep[]): BlockClipboard {
  const nodes: NodeDefinition[] = steps.map((step, i) => ({
    id: `rec${i + 1}`,
    type: step.type,
    label: step.label,
    position: { x: i * 280, y: 0 },
    config: structuredClone(step.config),
    settings,
    note: step.note,
  }));
  // У каждого поля пароля — своя переменная (password, password2…): старый и новый пароль — разные значения.
  const names = [...new Set(steps.filter((s) => s.secret).map((s) => s.secretVariable ?? "password"))];
  const variables: VariableDefinition[] = names.map((name, i) => ({
    name, type: "text", default: null, isInput: true, secret: true,
    description: names.length > 1 ? `Пароль ${i + 1} (вводится при запуске)` : "Пароль (вводится при запуске)",
  }));
  const edges = nodes.slice(1).map((node, i) => ({ id: `rec-e${i + 1}`, source: nodes[i].id, sourcePort: "out", target: node.id }));
  return { kind: "pepa-blocks", version: 1, nodes, edges, variables };
}

/**
 * Переменные записи (пароли) не должны совпасть с уже существующими в сценарии: иначе новый блок молча взял бы чужое
 * значение (пароль другой программы) или обычную, не скрытую переменную. Совпавшие получают следующий свободный номер
 * (password → password3), обращения к ним в блоках переписываются.
 */
export function withFreeVariables(blocks: BlockClipboard, taken: string[]): BlockClipboard {
  const existing = new Set(taken);
  // Свои имена, которые ни с чем не совпали, тоже заняты: переименованная переменная не должна взять одно из них.
  const used = new Set([...taken, ...blocks.variables.map((v) => v.name)]);
  const renames = new Map<string, string>();
  for (const variable of blocks.variables) {
    if (!existing.has(variable.name)) continue;
    const base = variable.name.replace(/\d+$/, "");
    let n = 2;
    while (used.has(`${base}${n}`)) n++;
    renames.set(variable.name, `${base}${n}`);
    used.add(`${base}${n}`);
  }
  if (renames.size === 0) return blocks;
  const rewrite = (value: Json): Json => {
    if (typeof value === "string") {
      return value.replace(/vars\.([A-Za-z_][A-Za-z0-9_]*)/g, (all, name: string) => (renames.has(name) ? `vars.${renames.get(name)}` : all));
    }
    if (Array.isArray(value)) return value.map(rewrite);
    if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, rewrite(v)]));
    return value;
  };
  return {
    ...blocks,
    nodes: blocks.nodes.map((node) => ({ ...node, config: rewrite(node.config) as Record<string, Json> })),
    variables: blocks.variables.map((v) => (renames.has(v.name) ? { ...v, name: renames.get(v.name)! } : v)),
  };
}
