import { createContext, memo, useContext } from "react";
import { Handle, Position, type NodeProps } from "@xyflow/react";
import type { NodeTypeDescriptor, StepState } from "../../api/types";
import { categoryColor, NodeIcon } from "../../components/Icon";
import { nodeSummary, type PepaNode } from "../../lib/graph";

export interface NodeRunState {
  state: StepState | "none";
  count: number;
}

export interface FlowContextValue {
  descriptors: Map<string, NodeTypeDescriptor>;
  /** Число ошибок проверки по блоку (конструктор). */
  issues?: Map<string, number>;
  /** Итог исполнения по блоку (страница процесса). */
  states?: Map<string, NodeRunState>;
}

export const FlowContext = createContext<FlowContextValue>({ descriptors: new Map() });

function portClass(id: string, kind: string) {
  return kind === "error" ? "error" : id;
}

export const NodeCard = memo(function NodeCard({ id, data, selected }: NodeProps<PepaNode>) {
  const context = useContext(FlowContext);
  const definition = data.definition;
  const descriptor = context.descriptors.get(definition.type);
  const outputs = descriptor?.outputs ?? [];
  const errors = context.issues?.get(id) ?? 0;
  const run = context.states ? context.states.get(id) ?? { state: "none" as const, count: 0 } : undefined;
  const summary = nodeSummary(definition, descriptor);
  const title = definition.label || descriptor?.title || definition.type;
  const singleOutput = outputs.length === 1 ? outputs[0] : null;

  const classes = [
    "pnode",
    selected ? "selected" : "",
    errors > 0 ? "has-error" : "",
    run ? `state-${run.state}` : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div className={classes} title={descriptor?.description}>
      {!descriptor?.isStart && <Handle type="target" position={Position.Left} id="in" className="target" style={{ top: 22 }} />}
      <div className="pnode-head">
        <div className="pnode-icon" style={{ background: categoryColor(descriptor?.category ?? "") }}>
          <NodeIcon name={descriptor?.icon ?? "box"} size={15} />
        </div>
        <div style={{ minWidth: 0 }}>
          <div className="pnode-title truncate">{title}</div>
          {definition.label && descriptor && <div className="pnode-type">{descriptor.title}</div>}
          {!descriptor && <div className="pnode-type" style={{ color: "var(--danger)" }}>Неизвестный тип</div>}
        </div>
      </div>
      {summary && (
        <div className="pnode-summary" title={summary}>
          {summary}
        </div>
      )}
      {singleOutput ? (
        <Handle type="source" position={Position.Right} id={singleOutput.id} className="source" style={{ top: 22 }} />
      ) : (
        outputs.length > 0 && (
          <div className="pnode-ports">
            {outputs.map((port) => (
              <div key={port.id} className={`pnode-port ${portClass(port.id, port.kind)}`}>
                {port.label}
                <Handle type="source" position={Position.Right} id={port.id} className={`source port-${portClass(port.id, port.kind)}`} />
              </div>
            ))}
          </div>
        )
      )}
      {errors > 0 && (
        <span className="pnode-flag" title="В блоке есть ошибки">
          {errors}
        </span>
      )}
      {run && run.count > 1 && (
        <span className="pnode-flag count" title="Сколько раз выполнялся">
          ×{run.count}
        </span>
      )}
    </div>
  );
});

export const nodeTypes = { pepa: NodeCard };
