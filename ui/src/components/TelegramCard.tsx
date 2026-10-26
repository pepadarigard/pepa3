import { useEffect, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { LogOut, Send } from "lucide-react";
import { api } from "../api/client";
import { useTelegram } from "../api/hooks";
import type { TelegramState, TelegramStatus } from "../api/types";
import type { Tone } from "../lib/status";
import { useFeedback } from "./feedback";
import { Badge, Button, Callout, Spinner } from "./ui";

const stateBadge: Record<TelegramState, { tone: Tone; text: string; pulse?: boolean }> = {
  notConfigured: { tone: "neutral", text: "Не подключён" },
  loginRequired: { tone: "warn", text: "Нужен вход" },
  connecting: { tone: "info", text: "Подключение…", pulse: true },
  waitingCode: { tone: "info", text: "Ждёт код" },
  waitingPassword: { tone: "info", text: "Ждёт пароль" },
  connected: { tone: "success", text: "Подключён" },
  disconnected: { tone: "warn", text: "Нет связи", pulse: true },
};

/** Подключение пользовательского аккаунта Telegram: данные приложения → код → (пароль) → подключено. */
export function TelegramCard() {
  const query = useTelegram();
  const client = useQueryClient();
  const feedback = useFeedback();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // «Начать заново» во время ввода кода — снова показать форму, не стирая сохранённые данные.
  const [restart, setRestart] = useState(false);

  const status = query.data;
  const apply = (next: TelegramStatus) => {
    client.setQueryData(["telegram"], next);
    setRestart(false);
  };
  const run = async (action: () => Promise<TelegramStatus>) => {
    setBusy(true);
    setError(null);
    try {
      apply(await action());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      void query.refetch();
    } finally {
      setBusy(false);
    }
  };
  const logout = async () => {
    const ok = await feedback.confirm({
      title: "Отключить Telegram?",
      text:
        "Pepa выйдет из аккаунта и удалит сохранённую сессию и данные входа. Процессы, которые ждут сообщения, продолжат ждать — " +
        "сообщения начнут приходить снова после повторного подключения.",
      confirmLabel: "Отключить",
      danger: true,
    });
    if (ok) await run(api.telegramLogout);
  };

  const state = status?.state;
  // Состояние сменилось не здесь (вход завершён или отменён в другом окне) — «Начать заново» больше не актуально.
  useEffect(() => {
    if (state !== "waitingCode" && state !== "waitingPassword") setRestart(false);
  }, [state]);
  const forget = async () => {
    const ok = await feedback.confirm({
      title: "Забыть данные входа Telegram?",
      text: "Pepa удалит сохранённые api_id, api_hash и номер. Для нового подключения их нужно будет ввести снова.",
      confirmLabel: "Забыть",
      danger: true,
    });
    if (ok) await run(api.telegramLogout);
  };
  const badge = state ? stateBadge[state] : null;
  const showForm = state === "notConfigured" || state === "loginRequired" || restart;
  const connected = state === "connected" || state === "disconnected";

  return (
    <div className="card integration-card" data-testid="telegram-card">
      <div className="integration-icon" style={state === "connected" ? { background: "var(--success-soft)", color: "var(--success)" } : undefined}>
        <Send size={20} />
      </div>
      <div className="stack" style={{ gap: 8, flex: 1, minWidth: 0 }}>
        <div className="row">
          <h2>Telegram</h2>
          {badge && <Badge tone={badge.tone} pulse={badge.pulse}>{badge.text}</Badge>}
        </div>
        <p className="muted small">
          Блоки «Отправить сообщение в Telegram» и «Ждать сообщение в Telegram» работают от вашего аккаунта (официальный протокол
          Telegram, как у приложения Telegram Desktop). Ответы, пришедшие, пока Pepa была закрыта, доходят до ждущих процессов после
          запуска; новые запуски по ним сами не начинаются.
        </p>

        {query.isLoading && <Spinner />}
        {query.error && <Callout tone="danger">{query.error instanceof Error ? query.error.message : String(query.error)}</Callout>}

        {status && connected && (
          <p className="small">
            Аккаунт: <strong>{status.account ?? "—"}</strong>
          </p>
        )}
        {status?.message && !showForm && (
          <Callout tone={state === "disconnected" ? "attention" : "info"}>{status.message}</Callout>
        )}
        {status?.message && showForm && !restart && state === "loginRequired" && !error && (
          <Callout tone="attention">{status.message}</Callout>
        )}
        {error && <Callout tone="danger">{error}</Callout>}

        {status && showForm && <LoginForm status={status} busy={busy} onSubmit={(login) => run(() => api.telegramLogin(login))} />}
        {status && showForm && (
          <div className="row">
            {restart && (
              <button type="button" className="link-button small" onClick={() => setRestart(false)} disabled={busy}>
                ← Вернуться к вводу {state === "waitingPassword" ? "пароля" : "кода"}
              </button>
            )}
            {!restart && state === "loginRequired" && status.apiId !== null && (
              <button type="button" className="link-button small" onClick={forget} disabled={busy}>Забыть данные входа</button>
            )}
          </div>
        )}

        {status && !showForm && state === "connecting" && (
          <div className="row small muted">
            <Spinner /> Подключение к Telegram… Это может занять до минуты.
          </div>
        )}

        {status && !showForm && (state === "waitingCode" || state === "waitingPassword") && (
          <CodeForm
            password={state === "waitingPassword"}
            busy={busy}
            onSubmit={(value) => run(() => api.telegramContinue(value))}
            onRestart={() => {
              setError(null);
              setRestart(true);
            }}
          />
        )}

        {status && connected && (
          <div className="row">
            <Button size="small" variant="ghost" icon={LogOut} busy={busy} onClick={logout}>Отключить</Button>
          </div>
        )}
      </div>
    </div>
  );
}

function LoginForm({ status, busy, onSubmit }: {
  status: TelegramStatus;
  busy: boolean;
  onSubmit: (login: { apiId: number; apiHash: string | null; phone: string }) => void;
}) {
  const [apiId, setApiId] = useState(status.apiId ? String(status.apiId) : "");
  const [apiHash, setApiHash] = useState("");
  const [phone, setPhone] = useState(status.phone ?? "");
  const hashSaved = status.apiId !== null && String(status.apiId) === apiId.trim();
  const valid = /^\d+$/.test(apiId.trim()) && (hashSaved || apiHash.trim().length > 0) && phone.replace(/\D/g, "").length >= 6;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!valid || busy) return;
    onSubmit({ apiId: Number(apiId.trim()), apiHash: apiHash.trim() || null, phone: phone.trim() });
  };

  return (
    <form className="stack" style={{ gap: 10 }} onSubmit={submit}>
      <p className="small">
        Нужны данные приложения Telegram: откройте <a href="https://my.telegram.org/apps" target="_blank" rel="noreferrer">my.telegram.org</a>,
        войдите по номеру телефона, выберите «API development tools» и создайте приложение (название и адрес — любые). Там будут{" "}
        <code>api_id</code> и <code>api_hash</code>.
      </p>
      <div className="grid-3">
        <label className="field">
          <span className="field-label">api_id</span>
          <input className="input mono" inputMode="numeric" autoComplete="off" value={apiId} placeholder="1234567"
            onChange={(e) => setApiId(e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">api_hash</span>
          <input className="input mono" type="password" autoComplete="off" value={apiHash}
            placeholder={hashSaved ? "сохранён — можно оставить пустым" : "32 символа"}
            onChange={(e) => setApiHash(e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Телефон аккаунта</span>
          <input className="input mono" type="tel" autoComplete="off" value={phone} placeholder="+79001234567"
            onChange={(e) => setPhone(e.target.value)} />
        </label>
      </div>
      <p className="small faint">
        Сессия хранится только на этом компьютере, в папке данных Pepa. По ней можно войти в ваш аккаунт — не передавайте эту папку
        другим людям. Отключить Pepa можно здесь или в Telegram: Настройки → Устройства.
      </p>
      <div className="row">
        <Button type="submit" variant="primary" size="small" icon={Send} busy={busy} disabled={!valid}>
          {busy ? "Подключение…" : "Подключить"}
        </Button>
        {busy && <span className="small muted">Ждём ответа Telegram (до минуты)…</span>}
      </div>
    </form>
  );
}

function CodeForm({ password, busy, onSubmit, onRestart }: {
  password: boolean;
  busy: boolean;
  onSubmit: (value: string) => void;
  onRestart: () => void;
}) {
  const [value, setValue] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (value.length === 0 || busy) return;
    onSubmit(value);
    setValue("");
  };
  return (
    <form className="row wrap" style={{ alignItems: "flex-end" }} onSubmit={submit}>
      <label className="field" style={{ minWidth: 220 }}>
        <span className="field-label">{password ? "Пароль двухэтапной проверки" : "Код из Telegram"}</span>
        <input className="input mono" autoFocus type={password ? "password" : "text"} inputMode={password ? undefined : "numeric"}
          autoComplete={password ? "current-password" : "one-time-code"} value={value}
          onChange={(e) => setValue(e.target.value)} />
      </label>
      <Button type="submit" variant="primary" size="small" busy={busy} disabled={value.length === 0}>Войти</Button>
      <button type="button" className="link-button small" onClick={onRestart} disabled={busy}>Начать заново</button>
    </form>
  );
}
