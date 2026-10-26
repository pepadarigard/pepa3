import type { NodeCategory, NodeTypeDescriptor } from "../../api/types";
import { categoryColor, NodeIcon } from "../../components/Icon";

export const DRAG_TYPE = "application/x-pepa-node";

export function Palette({ categories, descriptors, onAdd }: { categories: NodeCategory[]; descriptors: NodeTypeDescriptor[]; onAdd: (type: string) => void }) {
  const known = new Set(categories.map((c) => c.id));
  const groups = [
    ...categories.map((c) => ({ id: c.id, title: c.title })),
    ...(descriptors.some((d) => !known.has(d.category)) ? [{ id: "__other", title: "Другое" }] : []),
  ];

  return (
    <aside className="palette" aria-label="Блоки">
      {groups.map((group) => {
        const items = descriptors.filter((d) => (group.id === "__other" ? !known.has(d.category) : d.category === group.id) && !d.isStart);
        if (items.length === 0) return null;
        return (
          <div key={group.id} className="palette-group">
            <h4>{group.title}</h4>
            {items.map((d) => (
              <button
                key={d.type}
                className="palette-item"
                draggable
                title={`${d.description}\nНажмите или перетащите на схему.`}
                onDragStart={(e) => {
                  e.dataTransfer.setData(DRAG_TYPE, d.type);
                  e.dataTransfer.effectAllowed = "move";
                }}
                onClick={() => onAdd(d.type)}
              >
                <span className="palette-icon" style={{ background: categoryColor(d.category) }}>
                  <NodeIcon name={d.icon} size={14} />
                </span>
                <span className="truncate">{d.title}</span>
              </button>
            ))}
          </div>
        );
      })}
      <div className="small faint" style={{ padding: "0 4px" }}>
        Браузер, Windows-приложения, AdsPower и Telegram появятся здесь по мере подключения интеграций.
      </div>
    </aside>
  );
}
