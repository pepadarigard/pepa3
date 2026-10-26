import { useState } from "react";
import { AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, XCircle } from "lucide-react";
import type { ValidationReport } from "../../api/types";
import { plural } from "../../lib/format";

export function IssuesPanel({ report, checking, onSelect }: { report: ValidationReport | null; checking: boolean; onSelect: (nodeId: string) => void }) {
  const [open, setOpen] = useState(true);
  if (!report) return null;
  const { errorCount, warningCount } = report;

  if (checking) {
    return (
      <div className="canvas-issues" data-state="checking">
        <div className="card row small" style={{ padding: "8px 12px" }}>
          <span className="spinner" style={{ width: 12, height: 12 }} />
          Проверяю сценарий…
        </div>
      </div>
    );
  }

  if (errorCount === 0 && warningCount === 0) {
    return (
      <div className="canvas-issues" data-state="valid">
        <div className="card row small" style={{ padding: "8px 12px" }}>
          <CheckCircle2 size={15} style={{ color: "var(--success)" }} />
          Ошибок нет — сценарий можно запускать.
        </div>
      </div>
    );
  }

  return (
    <div className="canvas-issues" data-state={errorCount > 0 ? "errors" : "warnings"}>
      <div className="card">
        <button className="row small" style={{ border: 0, background: "none", padding: "8px 12px", font: "inherit", cursor: "pointer", width: "100%", color: "var(--text)" }}
          onClick={() => setOpen(!open)}>
          {open ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
          {errorCount > 0 && <span style={{ color: "var(--danger)", fontWeight: 600 }}>{errorCount} {plural(errorCount, "ошибка", "ошибки", "ошибок")}</span>}
          {warningCount > 0 && <span style={{ color: "var(--warn)", fontWeight: 600 }}>{warningCount} {plural(warningCount, "замечание", "замечания", "замечаний")}</span>}
          <span className="muted">{errorCount > 0 ? "— запуск невозможен" : "— запускать можно"}</span>
        </button>
        {open && (
          <div className="issues-panel">
            {report.issues.map((issue, i) => (
              <div key={i} className={`issue ${issue.severity}`} onClick={() => issue.nodeId && onSelect(issue.nodeId)}
                style={{ cursor: issue.nodeId ? "pointer" : "default" }}>
                {issue.severity === "error" ? <XCircle size={14} /> : <AlertTriangle size={14} />}
                <span>{issue.message}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
