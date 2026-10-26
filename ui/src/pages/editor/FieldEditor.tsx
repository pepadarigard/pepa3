import { useEffect, useRef, useState } from "react";
import type { FieldDescriptor, Json, VariableDefinition } from "../../api/types";

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
    case "expression":
    case "condition":
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
      {error ? <span className="field-error">{error}</span> : field.help && <span className="field-help">{field.help}</span>}
    </div>
  );
}

/** Подсказки-вставки переменных для шаблонов и выражений. */
function WithInserts({ field, variables, onInsert, children }: { field: FieldDescriptor; variables: VariableDefinition[]; onInsert: (snippet: string) => void; children: React.ReactNode }) {
  const isExpression = field.kind === "expression" || field.kind === "condition";
  const isTemplate = field.kind === "template" || field.kind === "multilineTemplate" || field.kind === "path";
  if ((!isExpression && !isTemplate) || variables.length === 0) return <>{children}</>;
  return (
    <>
      {children}
      <div className="chips">
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

function NumberInput({ value, min, max, invalid, onChange }: { value: number | undefined; min: number | null; max: number | null; invalid: boolean; onChange: (v: Json | undefined) => void }) {
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
