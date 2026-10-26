import { createContext, memo, useContext, useEffect, useRef, useState } from "react";
import { Handle, Position, type NodeProps } from "@xyflow/react";
import { ChevronsDownUp, ChevronsUpDown, Group, Ungroup } from "lucide-react";
import { groupIdOf, type GroupCardNode, type GroupFrameNode } from "../../lib/groups";
import { NodeCard } from "./NodeCard";

export interface GroupActions {
  /** Конструктор (можно менять); на странице процесса группы только показываются. */
  editable: boolean;
  rename: (groupId: string, title: string) => void;
  toggle: (groupId: string) => void;
  ungroup: (groupId: string) => void;
}

export const GroupActionsContext = createContext<GroupActions>({ editable: false, rename: () => {}, toggle: () => {}, ungroup: () => {} });

export function blocksLabel(count: number): string {
  const mod10 = count % 10;
  const mod100 = count % 100;
  const word = mod10 === 1 && mod100 !== 11 ? "блок" : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? "блока" : "блоков";
  return `${count} ${word}`;
}

/** Название группы: двойной щелчок — переименовать (Enter — сохранить, Esc — отменить). */
function GroupTitle({ id, title }: { id: string; title: string }) {
  const actions = useContext(GroupActionsContext);
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(title);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (editing) input.current?.select();
  }, [editing]);

  if (!editing) {
    return (
      <strong className="group-title truncate" title={actions.editable ? "Дважды щёлкните, чтобы переименовать" : undefined}
        onDoubleClick={(e) => {
          if (!actions.editable) return;
          e.stopPropagation();
          setValue(title);
          setEditing(true);
        }}>
        {title}
      </strong>
    );
  }

  const finish = (save: boolean) => {
    setEditing(false);
    if (save && value.trim() && value.trim() !== title) actions.rename(id, value.trim());
  };
  return (
    <input ref={input} className="input group-title-input nodrag" value={value} aria-label="Название группы" maxLength={80}
      onChange={(e) => setValue(e.target.value)}
      onBlur={() => finish(true)}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === "Enter") finish(true);
        else if (e.key === "Escape") finish(false);
      }} />
  );
}

/** Развёрнутая группа: рамка позади блоков; перетаскивается за заголовок вместе с блоками. */
export const GroupFrame = memo(function GroupFrame({ id, data }: NodeProps<GroupFrameNode>) {
  const actions = useContext(GroupActionsContext);
  const groupId = groupIdOf(id);
  return (
    <div className="group-frame" data-testid="group-frame">
      <div className="group-frame-header" title="Перетащите, чтобы передвинуть группу целиком">
        <Group size={14} />
        <GroupTitle id={groupId} title={data.group.title} />
        <span className="small faint">{blocksLabel(data.count)}</span>
        {data.issues > 0 && <span className="group-issues" title="Ошибок в блоках группы">{data.issues}</span>}
        <span className="spacer" />
        {actions.editable && (
          <>
            <button type="button" className="group-button nodrag" title="Свернуть в один блок" aria-label="Свернуть группу"
              onClick={() => actions.toggle(groupId)}>
              <ChevronsDownUp size={14} />
            </button>
            <button type="button" className="group-button nodrag" title="Разгруппировать (блоки останутся на месте)" aria-label="Разгруппировать"
              onClick={() => actions.ungroup(groupId)}>
              <Ungroup size={14} />
            </button>
          </>
        )}
      </div>
    </div>
  );
});

/** Свёрнутая группа — один блок; связи с её блоками показаны от него и к нему. */
export const GroupCard = memo(function GroupCard({ id, data, selected }: NodeProps<GroupCardNode>) {
  const actions = useContext(GroupActionsContext);
  const groupId = groupIdOf(id);
  const rest = data.count - data.titles.length;
  return (
    <div className={`pnode group-card${selected ? " selected" : ""}${data.issues > 0 ? " has-error" : ""}`} data-testid="group-card"
      onDoubleClick={() => actions.toggle(groupId)} title="Свёрнутая группа. Дважды щёлкните, чтобы развернуть.">
      <Handle type="target" position={Position.Left} id="in" className="target" style={{ top: 22 }} isConnectable={false} />
      <div className="pnode-head">
        <div className="pnode-icon group-card-icon">
          <Group size={15} />
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <GroupTitle id={groupId} title={data.group.title} />
          <div className="pnode-type">Группа · {blocksLabel(data.count)}</div>
        </div>
      </div>
      <div className="pnode-summary">
        {data.titles.join(" → ")}{rest > 0 ? ` и ещё ${rest}` : ""}
      </div>
      <div className="group-card-actions">
        <button type="button" className="link-button small nodrag" onClick={() => actions.toggle(groupId)}>
          <ChevronsUpDown size={13} /> Развернуть
        </button>
      </div>
      <Handle type="source" position={Position.Right} id="out" className="source" style={{ top: 22 }} isConnectable={false} />
      {data.issues > 0 && <span className="pnode-flag" title="Ошибок в блоках группы">{data.issues}</span>}
    </div>
  );
});

/** Типы блоков холста: обычные блоки и группы (рамка, свёрнутая группа). */
export const canvasNodeTypes = { pepa: NodeCard, groupFrame: GroupFrame, groupCard: GroupCard };
