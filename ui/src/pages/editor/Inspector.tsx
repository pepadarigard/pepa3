import { Fragment, useMemo, useState } from "react";
import { AlertTriangle, Lock, Plus, ScanSearch, Trash2, Unlink } from "lucide-react";
import type { Json, NodeDefinition, NodeTypeDescriptor, ValidationIssue, VariableDefinition, VariableType } from "../../api/types";
import { useTelegram } from "../../api/hooks";
import { categoryColor, NodeIcon } from "../../components/Icon";
import { Badge, Button } from "../../components/ui";
import { ru } from "../../i18n/ru";
import { elementConfig } from "../../lib/desktop";
import { isFieldVisible, type PepaEdge } from "../../lib/graph";
import { navigate } from "../../router";
import { ElementPicker } from "./ElementPicker";
import { FieldEditor } from "./FieldEditor";
import type { Meta } from "./useEditor";

const effectText: Record<string, { text: string; tone: "neutral" | "info" | "attention" }> = {
  none: { text: "Без внешних действий — сохраняется вместе с шагом", tone: "neutral" },
  readOnly: { text: "Только чтение — повтор безопасен", tone: "info" },
  external: { text: "Внешнее действие — без повторов вслепую", tone: "attention" },
};

/** Блок Telegram без подключённого аккаунта при запуске завершится ошибкой — сказать об этом заранее. */
function TelegramHint() {
  const { data } = useTelegram();
  if (!data || data.state === "connected") return null;
  const text = data.state === "disconnected"
    ? "Сейчас нет связи с Telegram — Pepa подключится сама."
    : "Telegram ещё не подключён: без этого блок завершится ошибкой.";
  return (
    <div className="issue warning" style={{ border: 0, padding: 0, cursor: "default", alignItems: "flex-start" }}>
      <AlertTriangle size={14} style={{ flex: "none", marginTop: 2 }} />
      <span>
        {text}{" "}
        <button type="button" className="link-button small" onClick={() => navigate({ page: "integrations" })}>Интеграции →</button>
      </span>
    </div>
  );
}

export function NodeInspector({ node, descriptor, variables, issues, onChange, onCreateVariable, onDelete }: {
  node: NodeDefinition;
  descriptor: NodeTypeDescriptor | undefined;
  variables: VariableDefinition[];
  issues: ValidationIssue[];
  onChange: (updater: (node: NodeDefinition) => NodeDefinition, key: string) => void;
  onCreateVariable: (name: string) => void;
  onDelete: () => void;
}) {
  if (!descriptor) {
    return (
      <div className="inspector-section">
        <h3>Неизвестный тип блока «{node.type}»</h3>
        <p className="small muted">Эта версия Pepa не знает такой блок. Удалите его или обновите приложение.</p>
        <Button variant="danger" icon={Trash2} onClick={onDelete}>Удалить блок</Button>
      </div>
    );
  }

  const fieldIssue = (name: string) => issues.find((i) => i.field === name)?.message.replace(/^«[^»]*»[:,]\s*/, "");
  const general = issues.filter((i) => !i.field || !descriptor.fields.some((f) => f.name === i.field));
  const setConfig = (name: string, value: Json | undefined) =>
    onChange((n) => {
      const config = { ...n.config };
      if (value === undefined || value === "") delete config[name];
      else config[name] = value;
      return { ...n, config };
    }, `config.${name}`);

  return (
    <>
      <div className="inspector-section">
        <div className="inspector-heading">
          <div className="pnode-icon" style={{ background: categoryColor(descriptor.category), width: 32, height: 32 }}>
            <NodeIcon name={descriptor.icon} size={17} />
          </div>
          <div style={{ minWidth: 0 }}>
            <h3>{descriptor.title}</h3>
            <div className="small muted">{descriptor.description}</div>
          </div>
        </div>
        <div className="row wrap">
          <Badge tone={effectText[descriptor.effect].tone} dot={false}>{effectText[descriptor.effect].text}</Badge>
          {descriptor.supportsVerification && <Badge tone="success" dot={false}>Умеет проверять результат</Badge>}
        </div>
        {descriptor.resourceHint && (
          <div className="small muted row" style={{ alignItems: "flex-start", gap: 6 }}>
            <Lock size={14} style={{ flex: "none", marginTop: 2 }} /> {descriptor.resourceHint}
          </div>
        )}
        {descriptor.category === "telegram" && <TelegramHint />}
        {general.map((issue, i) => (
          <div key={i} className={`issue ${issue.severity}`} style={{ border: 0, padding: 0, cursor: "default" }}>
            <AlertTriangle size={14} /> {issue.message}
          </div>
        ))}
        <label className="field">
          <span className="field-label">Подпись на схеме</span>
          <input className="input" value={node.label ?? ""} placeholder={descriptor.title}
            onChange={(e) => onChange((n) => ({ ...n, label: e.target.value || null }), "label")} />
        </label>
      </div>

      {descriptor.fields.length > 0 && (
        <div className="inspector-section">
          <h3>Параметры</h3>
          {descriptor.fields
            .filter((f) => isFieldVisible(f, node.config, descriptor.fields))
            .map((field) => (
              <Fragment key={field.name}>
                {/* Выбор элемента — перед полями элемента, после полей окна. */}
                {field.lookup === "desktop-elements" && <ElementPickerButton node={node} descriptor={descriptor} onChange={onChange} />}
                <FieldEditor
                  field={field}
                  value={node.config[field.name]}
                  variables={variables}
                  error={fieldIssue(field.name)}
                  onCreateVariable={onCreateVariable}
                  onChange={(value) => setConfig(field.name, value)}
                />
              </Fragment>
            ))}
        </div>
      )}

      {!descriptor.isStart && (
        <div className="inspector-section">
          <h3>Отладка</h3>
          <label className="row" style={{ gap: 8, alignItems: "flex-start" }}>
            <input type="checkbox" checked={node.settings.breakpoint === true} style={{ marginTop: 3 }}
              onChange={(e) => onChange((n) => ({ ...n, settings: { ...n.settings, breakpoint: e.target.checked } }), "settings.breakpoint")} />
            <span className="small">
              Пауза перед этим блоком — процесс остановится, можно посмотреть переменные и нажать «Продолжить».
            </span>
          </label>
        </div>
      )}

      {descriptor.supportsRetry && (
        <div className="inspector-section">
          <h3>Надёжность</h3>
          <p className="small muted">
            Повтор выполняется только при подтверждённой ошибке. Если неизвестно, выполнилось ли действие, Pepa не повторяет
            его сама.
          </p>
          <div className="grid-2" style={{ gap: 10 }}>
            <label className="field">
              <span className="field-label">Повторов</span>
              <input className="input" type="number" min={0} max={100} value={node.settings.retryCount}
                onChange={(e) => onChange((n) => ({ ...n, settings: { ...n.settings, retryCount: clampInt(e.target.value, 0, 100) } }), "settings.retry")} />
            </label>
            <label className="field">
              <span className="field-label">Пауза, с</span>
              <input className="input" type="number" min={0} value={node.settings.retryDelaySeconds}
                onChange={(e) => onChange((n) => ({ ...n, settings: { ...n.settings, retryDelaySeconds: clampInt(e.target.value, 0, 86400) } }), "settings.delay")} />
            </label>
          </div>
          <label className="field">
            <span className="field-label">Тайм-аут, с</span>
            <input className="input" type="number" min={1} placeholder="по умолчанию из настроек" value={node.settings.timeoutSeconds ?? ""}
              onChange={(e) => onChange((n) => ({ ...n, settings: { ...n.settings, timeoutSeconds: e.target.value === "" ? null : clampInt(e.target.value, 1, 86400) } }), "settings.timeout")} />
          </label>
          {descriptor.outputs.some((p) => p.kind === "error") && (
            <p className="small muted">Подключите выход «Ошибка», чтобы обработать неудачу в сценарии вместо остановки процесса.</p>
          )}
        </div>
      )}

      <div className="inspector-section">
        <label className="field">
          <span className="field-label">Заметка</span>
          <textarea className="textarea" style={{ minHeight: 56 }} value={node.note ?? ""} placeholder="Для себя: зачем этот шаг"
            onChange={(e) => onChange((n) => ({ ...n, note: e.target.value || null }), "note")} />
        </label>
        {!descriptor.isStart && (
          <div className="row">
            <Button variant="danger" icon={Trash2} onClick={onDelete}>Удалить блок</Button>
            <span className="small faint">или клавиша Delete</span>
          </div>
        )}
      </div>
    </>
  );
}

export function EdgeInspector({ edge, sourceTitle, targetTitle, portLabel, onDelete }: { edge: PepaEdge; sourceTitle: string; targetTitle: string; portLabel: string; onDelete: () => void }) {
  return (
    <div className="inspector-section">
      <h3>Связь</h3>
      <p className="small">
        Из <strong>{sourceTitle}</strong> (выход «{portLabel}») в <strong>{targetTitle}</strong>.
      </p>
      <p className="small muted">Чтобы перенаправить, протяните новую связь из того же выхода — старая заменится.</p>
      <div className="row">
        <Button variant="danger" icon={Unlink} onClick={onDelete}>Удалить связь</Button>
      </div>
      <span className="faint small mono">{edge.id}</span>
    </div>
  );
}

const variableTypes: VariableType[] = ["text", "number", "boolean", "list", "object", "any"];

export function ScenarioInspector({ meta, issues, onChange }: { meta: Meta; issues: ValidationIssue[]; onChange: (updater: (meta: Meta) => Meta, key: string) => void }) {
  const scenarioIssues = issues.filter((i) => !i.nodeId && !i.edgeId);
  const updateVariable = (index: number, patch: Partial<VariableDefinition>, key: string) =>
    onChange((m) => ({ ...m, variables: m.variables.map((v, i) => (i === index ? { ...v, ...patch } : v)) }), `var.${index}.${key}`);

  return (
    <>
      <div className="inspector-section">
        <h3>Сценарий</h3>
        <p className="small muted">Выберите блок на схеме, чтобы настроить его. Здесь — общие свойства сценария.</p>
        <label className="field">
          <span className="field-label">Название <span className="req">*</span></span>
          <input className="input" value={meta.name} onChange={(e) => onChange((m) => ({ ...m, name: e.target.value }), "name")} />
        </label>
        <label className="field">
          <span className="field-label">Описание</span>
          <textarea className="textarea" style={{ minHeight: 64 }} value={meta.description}
            onChange={(e) => onChange((m) => ({ ...m, description: e.target.value }), "description")} />
        </label>
        {scenarioIssues.map((issue, i) => (
          <div key={i} className={`issue ${issue.severity}`} style={{ border: 0, padding: 0, cursor: "default" }}>
            <AlertTriangle size={14} /> {issue.message}
          </div>
        ))}
      </div>

      <div className="inspector-section">
        <div className="row">
          <h3>Переменные</h3>
          <span className="spacer" />
          <Button size="small" icon={Plus} onClick={() => onChange((m) => ({ ...m, variables: [...m.variables, { name: uniqueName(m.variables), type: "text", default: null, description: "", isInput: false }] }), "var.add")}>
            Добавить
          </Button>
        </div>
        <p className="small muted">
          У каждого запуска свои значения. В выражениях — <code>vars.имя</code>, в тексте — <code>{"{{ vars.имя }}"}</code>.
        </p>
        {meta.variables.length === 0 && <p className="small faint">Переменных пока нет.</p>}
        {meta.variables.map((variable, index) => (
          <div key={index} className="var-row">
            <div className="row">
              <input className="input mono" style={{ flex: 1 }} value={variable.name} aria-label="Имя переменной"
                onChange={(e) => updateVariable(index, { name: e.target.value.replace(/\s+/g, "_") }, "name")} />
              <select className="select" style={{ width: 110 }} value={variable.type}
                onChange={(e) => updateVariable(index, { type: e.target.value as VariableType, default: null }, "type")}>
                {variableTypes.map((t) => <option key={t} value={t}>{ru.variableTypes[t]}</option>)}
              </select>
              <Button size="small" variant="ghost" iconOnly icon={Trash2} title="Удалить переменную"
                onClick={() => onChange((m) => ({ ...m, variables: m.variables.filter((_, i) => i !== index) }), "var.remove")} />
            </div>
            <DefaultValueEditor variable={variable} onChange={(value) => updateVariable(index, { default: value }, "default")} />
            <input className="input" placeholder="Описание (необязательно)" value={variable.description}
              onChange={(e) => updateVariable(index, { description: e.target.value }, "description")} />
            <label className="check small">
              <input type="checkbox" checked={variable.isInput} onChange={(e) => updateVariable(index, { isInput: e.target.checked }, "input")} />
              Спрашивать при запуске
            </label>
          </div>
        ))}
      </div>

      <div className="inspector-section">
        <h3>Подсказки</h3>
        <ul className="small muted" style={{ margin: 0, paddingLeft: 18 }}>
          <li>Перетащите блок из левой панели на схему или нажмите на него.</li>
          <li>Соединяйте выход блока (справа) со входом другого (слева).</li>
          <li>Из каждого выхода — одна связь. Цикл — связь назад, к условию.</li>
          <li>Ctrl+Z / Ctrl+Y — отменить/вернуть, Ctrl+S — сохранить, Ctrl+D — копия выбранного.</li>
          <li>Ctrl+C / Ctrl+V — копировать и вставить блоки, в том числе в другой сценарий (нужные переменные добавятся сами).</li>
          <li>Ctrl+A — выбрать всё, Ctrl+F — найти блок на схеме. Несколько блоков — рамкой с Shift или щелчками с Ctrl.</li>
          <li>Ctrl+G — объединить выбранные блоки в группу: её можно назвать, двигать целиком и свернуть в один блок. На выполнение группы не влияют.</li>
        </ul>
      </div>
    </>
  );
}

function DefaultValueEditor({ variable, onChange }: { variable: VariableDefinition; onChange: (value: Json | null) => void }) {
  const [text, setText] = useState(() => (variable.default === null || variable.default === undefined ? "" : typeof variable.default === "string" ? variable.default : JSON.stringify(variable.default)));
  const [error, setError] = useState<string | null>(null);

  if (variable.type === "boolean") {
    return (
      <label className="check small">
        <input type="checkbox" checked={variable.default === true} onChange={(e) => onChange(e.target.checked)} />
        Начальное значение: {variable.default === true ? "да" : "нет"}
      </label>
    );
  }

  const commit = (raw: string) => {
    setText(raw);
    if (raw.trim() === "") {
      setError(null);
      onChange(null);
      return;
    }
    if (variable.type === "text") {
      setError(null);
      onChange(raw);
      return;
    }
    if (variable.type === "number") {
      const n = Number(raw.replace(",", "."));
      if (Number.isFinite(n)) {
        setError(null);
        onChange(n);
      } else setError("Нужно число");
      return;
    }
    try {
      const parsed = JSON.parse(raw) as Json;
      if (variable.type === "list" && !Array.isArray(parsed)) return setError("Нужен список: [1, 2, 3]");
      if (variable.type === "object" && (typeof parsed !== "object" || parsed === null || Array.isArray(parsed))) return setError('Нужен объект: {"a": 1}');
      setError(null);
      onChange(parsed);
    } catch {
      if (variable.type === "any") {
        setError(null);
        onChange(raw);
      } else setError("Некорректный JSON");
    }
  };

  return (
    <div className="field">
      <input className={`input${variable.type === "text" ? "" : " mono"}${error ? " invalid" : ""}`} placeholder="Начальное значение (пусто — нет)" value={text} onChange={(e) => commit(e.target.value)} />
      {error && <span className="field-error">{error}</span>}
    </div>
  );
}

function uniqueName(variables: VariableDefinition[]): string {
  for (let i = 1; ; i++) {
    const name = `var${i}`;
    if (!variables.some((v) => v.name === name)) return name;
  }
}

function clampInt(raw: string, min: number, max: number): number {
  const n = Math.round(Number(raw));
  if (!Number.isFinite(n)) return min;
  return Math.min(max, Math.max(min, n));
}

/** «Выбрать элемент в окне»: окно — из параметров блока (текстом), выбранный элемент заполняет подпись, код и тип. */
function ElementPickerButton({ node, descriptor, onChange }: {
  node: NodeDefinition;
  descriptor: NodeTypeDescriptor;
  onChange: (updater: (n: NodeDefinition) => NodeDefinition, key: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const title = typeof node.config.window === "string" ? node.config.window.trim() : "";
  const process = typeof node.config.process === "string" ? node.config.process.trim() : "";
  const dynamic = title.includes("{{") || process.includes("{{");
  const typeField = descriptor.fields.find((f) => f.name === "controlType");
  const typeLabels = useMemo(() => new Map((typeField?.options ?? []).filter((o) => o.value).map((o) => [o.value, o.label])), [typeField]);
  return (
    <div className="stack" style={{ gap: 4 }}>
      <Button size="small" icon={ScanSearch} disabled={dynamic || (!title && !process)} onClick={() => setOpen(true)}
        title="Посмотреть элементы открытого окна и выбрать нужный">
        Выбрать элемент в окне
      </Button>
      {(dynamic || (!title && !process)) && (
        <span className="field-help">
          {dynamic ? "Окно задано выражением — чтобы выбрать элемент, укажите его текстом (потом можно вернуть выражение)." : "Сначала укажите окно выше."}
        </span>
      )}
      {open && (
        <ElementPicker window={title} process={process} typeLabels={typeLabels} onClose={() => setOpen(false)}
          onPick={(element) => onChange((n) => ({ ...n, config: { ...n.config, ...elementConfig(element, typeField?.options ?? [], descriptor.type === "desktop.read") } }), "element.pick")} />
      )}
    </div>
  );
}
