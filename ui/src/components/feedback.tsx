import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { CheckCircle2, Info, XCircle } from "lucide-react";
import { Button } from "./ui";

// ───────────── Уведомления ─────────────

type ToastKind = "success" | "error" | "info";
interface ToastItem {
  id: number;
  kind: ToastKind;
  text: string;
}

interface Feedback {
  toast: (text: string, kind?: ToastKind) => void;
  error: (error: unknown) => void;
  confirm: (options: ConfirmOptions) => Promise<boolean>;
}

interface ConfirmOptions {
  title: string;
  text?: string;
  confirmLabel?: string;
  danger?: boolean;
}

const FeedbackContext = createContext<Feedback | null>(null);

export function useFeedback(): Feedback {
  const context = useContext(FeedbackContext);
  if (!context) throw new Error("FeedbackProvider не найден");
  return context;
}

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const [dialog, setDialog] = useState<(ConfirmOptions & { resolve: (value: boolean) => void }) | null>(null);
  const nextId = useRef(1);

  const toast = useCallback((text: string, kind: ToastKind = "success") => {
    const id = nextId.current++;
    setToasts((items) => [...items.filter((t) => t.text !== text).slice(-2), { id, kind, text }]);
    setTimeout(() => setToasts((items) => items.filter((t) => t.id !== id)), kind === "error" ? 8000 : 4000);
  }, []);

  const error = useCallback(
    (e: unknown) => toast(e instanceof Error ? e.message : String(e), "error"),
    [toast],
  );

  const confirm = useCallback(
    (options: ConfirmOptions) => new Promise<boolean>((resolve) => setDialog({ ...options, resolve })),
    [],
  );

  const value = useMemo(() => ({ toast, error, confirm }), [toast, error, confirm]);

  const close = (result: boolean) => {
    dialog?.resolve(result);
    setDialog(null);
  };

  return (
    <FeedbackContext.Provider value={value}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast ${t.kind}`}>
            {t.kind === "error" ? <XCircle size={18} /> : t.kind === "success" ? <CheckCircle2 size={18} /> : <Info size={18} />}
            <span>{t.text}</span>
          </div>
        ))}
      </div>
      {dialog && (
        <Dialog title={dialog.title} onClose={() => close(false)}
          footer={
            <>
              <Button onClick={() => close(false)}>Отмена</Button>
              <Button variant={dialog.danger ? "danger" : "primary"} onClick={() => close(true)} autoFocus>
                {dialog.confirmLabel ?? "Подтвердить"}
              </Button>
            </>
          }>
          {dialog.text && <p style={{ color: "var(--text-2)" }}>{dialog.text}</p>}
        </Dialog>
      )}
    </FeedbackContext.Provider>
  );
}

// ───────────── Диалог ─────────────

export function Dialog({ title, children, footer, onClose, width }: { title: ReactNode; children: ReactNode; footer?: ReactNode; onClose: () => void; width?: number }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" style={width ? { width: `min(${width}px, 100%)` } : undefined}>
        <div className="dialog-header">
          <h2>{title}</h2>
        </div>
        <div className="dialog-body">{children}</div>
        {footer && <div className="dialog-footer">{footer}</div>}
      </div>
    </div>
  );
}
