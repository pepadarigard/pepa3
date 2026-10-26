import type { ButtonHTMLAttributes, ReactNode } from "react";
import { AlertTriangle, CheckCircle2, Info, XCircle, type LucideIcon } from "lucide-react";
import type { Tone } from "../lib/status";

type ButtonVariant = "default" | "primary" | "danger" | "ghost";

export function Button({
  variant = "default",
  size,
  icon: IconComponent,
  iconOnly,
  busy,
  children,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: "small";
  icon?: LucideIcon;
  iconOnly?: boolean;
  busy?: boolean;
}) {
  const classes = [
    "btn",
    variant !== "default" ? variant : "",
    size ?? "",
    iconOnly ? "icon" : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <button type="button" className={classes} disabled={busy || rest.disabled} {...rest}>
      {busy ? <span className="spinner" /> : IconComponent ? <IconComponent size={size === "small" ? 14 : 16} /> : null}
      {!iconOnly && children}
    </button>
  );
}

export function Badge({ tone = "neutral", children, pulse, dot = true }: { tone?: Tone; children: ReactNode; pulse?: boolean; dot?: boolean }) {
  return (
    <span className={`badge tone-${tone}${pulse ? " pulse" : ""}`}>
      {dot && <span className="dot" />}
      {children}
    </span>
  );
}

export function Card({ title, actions, children, className, padded }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; padded?: boolean }) {
  return (
    <section className={`card${padded ? " padded" : ""}${className ? ` ${className}` : ""}`}>
      {(title || actions) && (
        <header className="card-header">
          {typeof title === "string" ? <h2>{title}</h2> : title}
          <span className="spacer" />
          {actions}
        </header>
      )}
      {children}
    </section>
  );
}

export function Empty({ icon: IconComponent, title, children }: { icon: LucideIcon; title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <IconComponent size={28} />
      <strong style={{ color: "var(--text-2)" }}>{title}</strong>
      {children && <div className="small">{children}</div>}
    </div>
  );
}

export function Spinner() {
  return <span className="spinner" />;
}

export function Loading({ text = "Загрузка…" }: { text?: string }) {
  return (
    <div className="empty">
      <Spinner />
      <span className="small">{text}</span>
    </div>
  );
}

const calloutIcons: Record<string, LucideIcon> = {
  info: Info,
  attention: AlertTriangle,
  danger: XCircle,
  success: CheckCircle2,
};

export function Callout({ tone = "info", title, children, actions }: { tone?: "info" | "attention" | "danger" | "success"; title?: ReactNode; children?: ReactNode; actions?: ReactNode }) {
  const IconComponent = calloutIcons[tone];
  return (
    <div className={`callout ${tone}`}>
      <IconComponent size={18} className="callout-icon" />
      <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
        {title && <strong>{title}</strong>}
        {children && <div className="small" style={{ color: "var(--text-2)" }}>{children}</div>}
        {actions && <div className="row wrap">{actions}</div>}
      </div>
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message = error instanceof Error ? error.message : String(error);
  return (
    <Callout tone="danger" title="Не удалось загрузить данные" actions={onRetry && <Button size="small" onClick={onRetry}>Повторить</Button>}>
      {message}
    </Callout>
  );
}
