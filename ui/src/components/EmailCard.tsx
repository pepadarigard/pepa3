import { useEffect, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Mail, PlugZap, Trash2 } from "lucide-react";
import { api } from "../api/client";
import { useEmail } from "../api/hooks";
import type { EmailAccountRequest, EmailSecurity, EmailState, EmailStatus } from "../api/types";
import { formatDateTime } from "../lib/format";
import type { Tone } from "../lib/status";
import { useFeedback } from "./feedback";
import { Badge, Button, Callout, Spinner } from "./ui";

const stateBadge: Record<EmailState, { tone: Tone; text: string; pulse?: boolean }> = {
  notConfigured: { tone: "neutral", text: "Не подключена" },
  off: { tone: "neutral", text: "Выключена" },
  connecting: { tone: "info", text: "Подключение…", pulse: true },
  connected: { tone: "success", text: "Подключена" },
  error: { tone: "warn", text: "Нет связи", pulse: true },
};

/** Готовые настройки популярных почт: серверы IMAP и SMTP и где взять пароль приложения. */
const presets: { id: string; title: string; server: string; smtp: string; hint: string }[] = [
  { id: "yandex", title: "Яндекс", server: "imap.yandex.ru", smtp: "smtp.yandex.ru", hint: "Яндекс ID → Безопасность → Пароли приложений (тип «Почта»). В настройках почты включите «С сервера imap.yandex.ru по протоколу IMAP»." },
  { id: "mailru", title: "Mail.ru", server: "imap.mail.ru", smtp: "smtp.mail.ru", hint: "Настройки → Безопасность → Пароли для внешних приложений." },
  { id: "gmail", title: "Gmail", server: "imap.gmail.com", smtp: "smtp.gmail.com", hint: "Аккаунт Google → Безопасность → Пароли приложений (нужна двухэтапная проверка)." },
  { id: "other", title: "Другая", server: "", smtp: "", hint: "Серверы IMAP и SMTP и порты — в справке вашей почты. Обычно порт 993 (чтение) и 465 (отправка), шифрование SSL." },
];

const empty: EmailAccountRequest = {
  server: "", port: 993, security: "auto", login: "", folder: "INBOX", enabled: true, password: null,
  smtpServer: "", smtpPort: 465, smtpSecurity: "auto", sentFolder: "",
};

/** Почта: ящик, из которого «Ждать письмо» получает письма (IMAP, только чтение), и с которого «Отправить письмо» отправляет (SMTP). */
export function EmailCard() {
  const query = useEmail();
  const client = useQueryClient();
  const feedback = useFeedback();
  const status = query.data;
  const [editing, setEditing] = useState(false);
  const state = status?.state;
  const badge = state ? stateBadge[state] : null;
  const configured = status !== undefined && state !== "notConfigured";
  const showForm = status !== undefined && (!configured || editing);

  const apply = (next: EmailStatus) => {
    client.setQueryData(["email"], next);
    setEditing(false);
  };
  const clear = async () => {
    const ok = await feedback.confirm({
      title: "Отключить почту?",
      text: "Pepa забудет ящик и пароль. Процессы, которые ждут письмо, продолжат ждать, но письма, пришедшие, пока почта отключена, событиями не станут: после повторного подключения считаются только новые.",
      confirmLabel: "Отключить",
      danger: true,
    });
    if (ok) apply(await api.emailClear());
  };

  return (
    <div className="card integration-card" data-testid="email-card">
      <div className="integration-icon" style={state === "connected" ? { background: "var(--success-soft)", color: "var(--success)" } : undefined}>
        <Mail size={20} />
      </div>
      <div className="stack" style={{ gap: 8, flex: 1, minWidth: 0 }}>
        <div className="row">
          <h2>Почта</h2>
          {badge && <Badge tone={badge.tone} pulse={badge.pulse}>{badge.text}</Badge>}
        </div>
        <p className="muted small">
          Блок «Ждать письмо» получает новые письма из ящика (по IMAP) — например, код или ссылку подтверждения после регистрации на
          сайте. Pepa только читает: письма не отмечаются прочитанными и не удаляются. Письма, пришедшие, пока Pepa была закрыта, доходят
          до ждущих процессов после запуска; то, что лежало в ящике до подключения, событиями не становится. С сервером отправки (SMTP)
          работает и «Отправить письмо»: если связь оборвётся во время отправки, Pepa не отправит письмо второй раз, а поищет его в
          «Отправленных».
        </p>

        {query.isLoading && <Spinner />}
        {query.error && <Callout tone="danger">{query.error instanceof Error ? query.error.message : String(query.error)}</Callout>}

        {status?.account && !showForm && (
          <p className="small">
            Ящик: <strong>{status.account.login}</strong> · {status.account.server} · папка «{status.account.folder}»
            {status.messages !== null && <span className="muted"> · писем в папке: {status.messages}</span>}
            {status.lastCheckAt && <span className="muted"> · проверено {formatDateTime(status.lastCheckAt)}</span>}
          </p>
        )}
        {status?.account && !showForm && (
          <p className="small">
            {status.account.smtpServer
              ? <>Отправка: <strong>{status.account.smtpServer}</strong>:{status.account.smtpPort}</>
              : <span className="muted">Отправка писем выключена — чтобы работал блок «Отправить письмо», укажите сервер отправки.</span>}
          </p>
        )}
        {status?.account?.smtpServer && status.sendProblem && !showForm && <Callout tone="attention">{status.sendProblem}</Callout>}
        {status?.error && !showForm && <Callout tone="attention">{status.error}</Callout>}
        {status?.note && !showForm && <Callout tone="info">{status.note}</Callout>}

        {status && showForm && (
          <AccountForm status={status} onSaved={apply} onCancel={configured ? () => setEditing(false) : undefined} />
        )}

        {status && configured && !showForm && (
          <div className="row">
            <Button size="small" variant="ghost" onClick={() => setEditing(true)}>Изменить</Button>
            <Button size="small" variant="ghost" icon={Trash2} onClick={clear}>Отключить</Button>
          </div>
        )}
      </div>
    </div>
  );
}

function AccountForm({ status, onSaved, onCancel }: {
  status: EmailStatus;
  onSaved: (status: EmailStatus) => void;
  onCancel?: () => void;
}) {
  const feedback = useFeedback();
  const [form, setForm] = useState<EmailAccountRequest>(status.account
    ? { ...status.account, smtpServer: status.account.smtpServer ?? "", sentFolder: status.account.sentFolder ?? "", password: null }
    : empty);
  const [busy, setBusy] = useState<"save" | "check" | null>(null);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const preset = presets.find((p) => p.server !== "" && p.server === form.server.trim()) ?? presets[presets.length - 1];
  const passwordSaved = status.account !== null && status.account.server === form.server.trim() && status.account.login === form.login.trim();
  const sending = (form.smtpServer ?? "").trim().length > 0;
  const valid = form.server.trim().length > 0 && form.login.trim().length > 0 && form.port > 0 && (!sending || form.smtpPort > 0) &&
    (passwordSaved || (form.password ?? "").length > 0);

  useEffect(() => setResult(null), [form]);
  const set = <K extends keyof EmailAccountRequest>(key: K, value: EmailAccountRequest[K]) => setForm((f) => ({ ...f, [key]: value }));
  const request = (): EmailAccountRequest => ({ ...form, password: form.password ? form.password : null });

  const check = async () => {
    setBusy("check");
    try {
      setResult(await api.emailCheck(request()));
    } catch (e) {
      setResult({ ok: false, message: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy(null);
    }
  };
  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (!valid || busy) return;
    setBusy("save");
    try {
      onSaved(await api.emailSave(request()));
      feedback.toast("Почта подключена");
    } catch (error) {
      setResult({ ok: false, message: error instanceof Error ? error.message : String(error) });
    } finally {
      setBusy(null);
    }
  };

  return (
    <form className="stack" style={{ gap: 10 }} onSubmit={save}>
      <div className="row wrap" role="group" aria-label="Почта">
        {presets.map((p) => (
          <button key={p.id} type="button" className={`chip option${p.id === preset.id ? " active" : ""}`}
            onClick={() => setForm((f) => ({ ...f, server: p.server, port: 993, security: "auto", smtpServer: p.smtp, smtpPort: 465, smtpSecurity: "auto" }))}>
            {p.title}
          </button>
        ))}
      </div>
      <p className="small muted">Нужен пароль приложения, а не обычный пароль от почты. {preset.hint}</p>
      <div className="grid-3">
        <label className="field">
          <span className="field-label">Сервер IMAP</span>
          <input className="input mono" autoComplete="off" value={form.server} placeholder="imap.yandex.ru"
            onChange={(e) => set("server", e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Адрес почты (логин)</span>
          <input className="input mono" autoComplete="off" value={form.login} placeholder="name@yandex.ru"
            onChange={(e) => set("login", e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Пароль приложения</span>
          <input className="input mono" type="password" autoComplete="new-password" value={form.password ?? ""}
            placeholder={passwordSaved ? "сохранён — можно оставить пустым" : ""}
            onChange={(e) => set("password", e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Папка</span>
          <input className="input mono" autoComplete="off" value={form.folder} placeholder="INBOX"
            onChange={(e) => set("folder", e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Порт</span>
          <input className="input mono" inputMode="numeric" value={String(form.port)}
            onChange={(e) => set("port", Number(e.target.value.replace(/\D/g, "")) || 0)} />
        </label>
        <label className="field">
          <span className="field-label">Защита соединения</span>
          <select className="input" value={form.security} onChange={(e) => set("security", e.target.value as EmailSecurity)}>
            <option value="auto">Автоматически (993 — SSL)</option>
            <option value="ssl">SSL/TLS</option>
            <option value="startTls">STARTTLS</option>
            <option value="none">Без шифрования (только этот компьютер)</option>
          </select>
        </label>
      </div>
      <div className="grid-3">
        <label className="field">
          <span className="field-label">Сервер отправки (SMTP)</span>
          <input className="input mono" autoComplete="off" value={form.smtpServer ?? ""} placeholder="smtp.yandex.ru — пусто: не отправлять"
            onChange={(e) => set("smtpServer", e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Порт отправки</span>
          <input className="input mono" inputMode="numeric" value={String(form.smtpPort)} disabled={!sending}
            onChange={(e) => set("smtpPort", Number(e.target.value.replace(/\D/g, "")) || 0)} />
        </label>
        <label className="field">
          <span className="field-label">Защита отправки</span>
          <select className="input" value={form.smtpSecurity} disabled={!sending} onChange={(e) => set("smtpSecurity", e.target.value as EmailSecurity)}>
            <option value="auto">Автоматически (465 — SSL)</option>
            <option value="ssl">SSL/TLS</option>
            <option value="startTls">STARTTLS</option>
            <option value="none">Без шифрования (только этот компьютер)</option>
          </select>
        </label>
        <label className="field">
          <span className="field-label">Папка отправленных</span>
          <input className="input mono" autoComplete="off" value={form.sentFolder ?? ""} placeholder="найти самой" disabled={!sending}
            onChange={(e) => set("sentFolder", e.target.value)} />
        </label>
      </div>
      <p className="small faint">
        Папка — обычно INBOX («Входящие»). Логин и пароль для отправки — те же. Письма уходят от адреса ящика. Пароль хранится на этом
        компьютере зашифрованным для вашей учётной записи Windows.
      </p>
      {result && <Callout tone={result.ok ? "success" : "danger"}>{result.message}</Callout>}
      <div className="row">
        <Button type="submit" variant="primary" size="small" icon={Mail} busy={busy === "save"} disabled={!valid || busy !== null}>
          {busy === "save" ? "Подключение…" : "Подключить"}
        </Button>
        <Button size="small" variant="ghost" icon={PlugZap} busy={busy === "check"} disabled={!valid || busy !== null} onClick={check}>
          Проверить связь
        </Button>
        {onCancel && (
          <button type="button" className="link-button small" onClick={onCancel} disabled={busy !== null}>Отмена</button>
        )}
      </div>
    </form>
  );
}
