import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { FieldDescriptor, Json, VariableDefinition } from "../../api/types";
import { useScenarios } from "../../api/hooks";
import { api } from "../../api/client";
import type { AdsPowerProfile, DesktopWindow } from "../../api/types";
import { formatDuration } from "../../lib/format";

interface Props {
  field: FieldDescriptor;
  value: Json | undefined;
  onChange: (value: Json | undefined) => void;
  variables: VariableDefinition[];
  onCreateVariable?: (name: string) => void;
  error?: string;
}

/** Поле параметра блока — вид зависит от типа поля из описания блока. */
export function FieldEditor({ field, value, onChange, variables, onCreateVariable, error }: Props) {
  const effective = value ?? field.default ?? undefined;
  const text = effective === undefined || effective === null ? "" : String(effective);
  const invalid = error ? " invalid" : "";

  let control: React.ReactNode;
  switch (field.kind) {
    case "boolean":
      control = (
        <label className="check">
          <input type="checkbox" checked={effective === true} onChange={(e) => onChange(e.target.checked)} />
          {field.label}
        </label>
      );
      break;
    case "select":
      control = (
        <select className={`select${invalid}`} value={text} onChange={(e) => onChange(e.target.value)}>
          {(field.options ?? []).map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      );
      break;
    case "number":
      control = (
        <NumberInput value={typeof effective === "number" ? effective : undefined} min={field.min} max={field.max} invalid={!!error} onChange={onChange} />
      );
      break;
    case "seconds":
      control = (
        <div className="stack" style={{ gap: 4 }}>
          <div className="row" style={{ gap: 8 }}>
            <NumberInput value={typeof effective === "number" ? effective : undefined} min={field.min} max={field.max} invalid={!!error}
              placeholder={field.placeholder} onChange={onChange} />
            <span className="faint small" style={{ whiteSpace: "nowrap" }}>сек</span>
          </div>
          {typeof effective === "number" && effective >= 60 && <span className="faint small">= {formatDuration(effective * 1000)}</span>}
        </div>
      );
      break;
    case "scenario":
      control = <ScenarioSelect value={text} invalid={!!error} onChange={onChange} />;
      break;
    case "time":
      control = <input className={`input${invalid}`} type="time" value={text} onChange={(e) => onChange(e.target.value || undefined)} />;
      break;
    case "variable":
      control = <VariableSelect value={text} variables={variables} invalid={!!error} onChange={onChange} onCreate={onCreateVariable} />;
      break;
    case "multilineTemplate":
      control = (
        <WithInserts field={field} variables={variables} onInsert={(snippet) => onChange(text + snippet)}>
          <textarea className={`textarea${invalid}`} value={text} placeholder={field.placeholder ?? undefined} onChange={(e) => onChange(e.target.value)} />
        </WithInserts>
      );
      break;
    case "code":
      control = (
        <WithInserts field={field} variables={variables} onInsert={(snippet) => onChange(text ? `${text} ${snippet}` : snippet)}>
          <textarea
            className={`textarea mono${invalid}`}
            style={{ minHeight: 90 }}
            value={text}
            spellCheck={false}
            placeholder={field.placeholder ?? undefined}
            onChange={(e) => onChange(e.target.value)}
          />
        </WithInserts>
      );
      break;
    case "expression":
    case "condition":
    case "matchCondition":
      control = (
        <WithInserts field={field} variables={variables} onInsert={(snippet) => onChange(text ? `${text} ${snippet}` : snippet)}>
          <textarea
            className={`textarea mono${invalid}`}
            style={{ minHeight: 60 }}
            value={text}
            spellCheck={false}
            placeholder={field.placeholder ?? undefined}
            onChange={(e) => onChange(e.target.value)}
          />
        </WithInserts>
      );
      break;
    default:
      control = (
        <WithInserts field={field} variables={variables} onInsert={(snippet) => onChange(text + snippet)}>
          <input
            className={`input${field.kind === "path" ? " mono" : ""}${invalid}`}
            value={text}
            placeholder={field.placeholder ?? undefined}
            spellCheck={field.kind !== "path"}
            onChange={(e) => onChange(e.target.value)}
          />
        </WithInserts>
      );
  }

  return (
    <div className="field">
      {field.kind !== "boolean" && (
        <span className="field-label">
          {field.label}
          {field.required && <span className="req">*</span>}
        </span>
      )}
      {control}
      {field.lookup === "adspower-profiles" && <AdsPowerPicker onPick={(id) => onChange(id)} />}
      {field.lookup === "desktop-windows" && <WindowPicker onPick={(title) => onChange(title)} />}
      {field.lookup === "watched-folders" && <WatchedFolderPicker onPick={(folder) => onChange(folder)} />}
      {error ? <span className="field-error">{error}</span> : field.help && <span className="field-help">{field.help}</span>}
    </div>
  );
}

/** Подсказки-вставки переменных для шаблонов и выражений. */
function WithInserts({ field, variables, onInsert, children }: { field: FieldDescriptor; variables: VariableDefinition[]; onInsert: (snippet: string) => void; children: React.ReactNode }) {
  const isExpression = field.kind === "expression" || field.kind === "condition" || field.kind === "matchCondition" || field.kind === "code";
  const isTemplate = field.kind === "template" || field.kind === "multilineTemplate" || field.kind === "path";
  const eventChips = field.kind === "matchCondition" ? ["event.payload", "event.correlationKey", "event.type"] : [];
  if ((!isExpression && !isTemplate) || (variables.length === 0 && eventChips.length === 0)) return <>{children}</>;
  return (
    <>
      {children}
      <div className="chips">
        {eventChips.map((chip) => (
          <button key={chip} type="button" className="chip" title="Данные пришедшего события" onClick={() => onInsert(chip)}>
            {chip}
          </button>
        ))}
        {variables.map((v) => (
          <button key={v.name} type="button" className="chip" title={v.description || `Вставить ${v.name}`}
            onClick={() => onInsert(isExpression ? `vars.${v.name}` : `{{ vars.${v.name} }}`)}>
            {isExpression ? `vars.${v.name}` : `{{ vars.${v.name} }}`}
          </button>
        ))}
      </div>
    </>
  );
}

function NumberInput({ value, min, max, invalid, placeholder, onChange }: { value: number | undefined; min: number | null; max: number | null; invalid: boolean; placeholder?: string | null; onChange: (v: Json | undefined) => void }) {
  const [text, setText] = useState(value === undefined ? "" : String(value));
  const last = useRef(value);
  useEffect(() => {
    if (value !== last.current) {
      last.current = value;
      setText(value === undefined ? "" : String(value));
    }
  }, [value]);
  return (
    <input
      className={`input${invalid ? " invalid" : ""}`}
      type="number"
      value={text}
      min={min ?? undefined}
      max={max ?? undefined}
      placeholder={placeholder ?? undefined}
      onChange={(e) => {
        setText(e.target.value);
        const n = Number(e.target.value);
        const next = e.target.value === "" || !Number.isFinite(n) ? undefined : n;
        last.current = next;
        onChange(next);
      }}
    />
  );
}

function VariableSelect({ value, variables, invalid, onChange, onCreate }: { value: string; variables: VariableDefinition[]; invalid: boolean; onChange: (v: Json | undefined) => void; onCreate?: (name: string) => void }) {
  const declared = variables.some((v) => v.name === value);
  return (
    <select
      className={`select${invalid ? " invalid" : ""}`}
      value={value}
      onChange={(e) => {
        if (e.target.value === "__new") {
          const name = window.prompt("Имя новой переменной (буквы, цифры и «_»):", "");
          if (name && name.trim()) {
            onCreate?.(name.trim());
            onChange(name.trim());
          }
          return;
        }
        onChange(e.target.value || undefined);
      }}
    >
      <option value="">— не выбрана —</option>
      {variables.map((v) => (
        <option key={v.name} value={v.name}>
          {v.name}
        </option>
      ))}
      {value && !declared && <option value={value}>{value} (не объявлена)</option>}
      {onCreate && <option value="__new">＋ Новая переменная…</option>}
    </select>
  );
}

function ScenarioSelect({ value, invalid, onChange }: { value: string; invalid: boolean; onChange: (v: Json | undefined) => void }) {
  const scenarios = useScenarios();
  const list = scenarios.data ?? [];
  const known = list.some((s) => s.id === value);
  return (
    <select className={`select${invalid ? " invalid" : ""}`} value={value} onChange={(e) => onChange(e.target.value || undefined)}>
      <option value="">— выберите сценарий —</option>
      {list.map((s) => (
        <option key={s.id} value={s.id}>
          {s.name}
          {s.errorCount > 0 ? " (есть ошибки)" : ""}
        </option>
      ))}
      {value && !known && !scenarios.isLoading && <option value={value}>Сценарий не найден ({value.slice(-6)})</option>}
    </select>
  );
}

/** Выбор профиля из запущенного AdsPower (по кнопке: Local API ограничивает частоту запросов). */
/** Выбор окна из открытых сейчас (только в приложении Pepa для Windows). */
function WindowPicker({ onPick }: { onPick: (title: string) => void }) {
  const [state, setState] = useState<{ loading: boolean; windows: DesktopWindow[] | null; error: string | null }>({
    loading: false,
    windows: null,
    error: null,
  });
  const load = async () => {
    setState({ loading: true, windows: null, error: null });
    try {
      setState({ loading: false, windows: await api.desktopWindows(), error: null });
    } catch (e) {
      setState({ loading: false, windows: null, error: e instanceof Error ? e.message : String(e) });
    }
  };
  if (!state.windows) {
    return (
      <div className="stack" style={{ gap: 4 }}>
        <button type="button" className="chip day" onClick={load} disabled={state.loading} style={{ alignSelf: "flex-start" }}>
          {state.loading ? "Загрузка окон…" : "Выбрать из открытых окон"}
        </button>
        {state.error && <span className="field-error">{state.error}</span>}
      </div>
    );
  }
  if (state.windows.length === 0) return <span className="field-help">Открытых окон нет.</span>;
  return (
    <select className="select" defaultValue="" onChange={(e) => e.target.value && onPick(e.target.value)}>
      <option value="">— выберите окно ({state.windows.length}) —</option>
      {state.windows.map((w, i) => (
        <option key={`${w.process}-${w.title}-${i}`} value={w.title}>
          {w.title} · {w.process}
        </option>
      ))}
    </select>
  );
}

export function AdsPowerPicker({ onPick }: { onPick: (id: string) => void }) {
  const [state, setState] = useState<{ loading: boolean; profiles: AdsPowerProfile[] | null; error: string | null }>({
    loading: false,
    profiles: null,
    error: null,
  });
  const load = async () => {
    setState({ loading: true, profiles: null, error: null });
    try {
      setState({ loading: false, profiles: await api.adsPowerProfiles(), error: null });
    } catch (e) {
      setState({ loading: false, profiles: null, error: e instanceof Error ? e.message : String(e) });
    }
  };
  if (!state.profiles) {
    return (
      <div className="stack" style={{ gap: 4 }}>
        <button type="button" className="chip day" onClick={load} disabled={state.loading} style={{ alignSelf: "flex-start" }}>
          {state.loading ? "Загрузка профилей…" : "Выбрать из AdsPower"}
        </button>
        {state.error && <span className="field-error">{state.error}</span>}
      </div>
    );
  }
  if (state.profiles.length === 0) return <span className="field-help">В AdsPower нет профилей.</span>;
  return (
    <select className="select" defaultValue="" onChange={(e) => e.target.value && onPick(e.target.value)}>
      <option value="">— выберите профиль ({state.profiles.length}) —</option>
      {state.profiles.map((p) => (
        <option key={p.id} value={p.id}>
          {p.number ? `№${p.number} · ` : ""}{p.name || "без имени"} · {p.id}{p.group ? ` · ${p.group}` : ""}
        </option>
      ))}
    </select>
  );
}

/** Папки, за которыми следит Pepa («Настройки» → «Слежение за папками»). */
function WatchedFolderPicker({ onPick }: { onPick: (folder: string) => void }) {
  const watches = useQuery({ queryKey: ["watches"], queryFn: api.watches });
  if (watches.error) return <span className="field-error">{watches.error instanceof Error ? watches.error.message : String(watches.error)}</span>;
  const folders = watches.data?.watches.filter((w) => w.enabled) ?? [];
  if (watches.data && folders.length === 0) {
    return <span className="field-help">Pepa пока ни за одной папкой не следит — добавьте папку в «Настройки» → «Слежение за папками».</span>;
  }
  return (
    <select className="select" value="" onChange={(e) => e.target.value && onPick(e.target.value)} disabled={!watches.data}>
      <option value="">{watches.data ? `— выберите папку (${folders.length}) —` : "Загрузка…"}</option>
      {folders.map((w) => <option key={w.id} value={w.folder}>{w.folder} · {w.pattern}</option>)}
    </select>
  );
}
