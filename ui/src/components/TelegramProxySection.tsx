import { useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Network, ShieldCheck } from "lucide-react";
import { api } from "../api/client";
import type { TelegramProxy, TelegramProxyKind, TelegramProxyRequest, TelegramStatus } from "../api/types";
import { Button, Callout } from "./ui";

/** Как показать сохранённый прокси одной строкой (без пароля и секрета). */
export function describeProxy(proxy: TelegramProxy): string {
  const address = proxy.host.includes(":") ? `[${proxy.host}]:${proxy.port}` : `${proxy.host}:${proxy.port}`;
  switch (proxy.kind) {
    case "socks5":
      return `SOCKS5 ${address}${proxy.username ? ` (логин ${proxy.username})` : ""}`;
    case "mtproxy":
      return `MTProxy ${address}`;
    default:
      return "без прокси — напрямую";
  }
}

/** Прокси для Telegram: ссылкой из Telegram или полями; проверка до сохранения; после сохранения — переподключение. */
export function TelegramProxySection({ status }: { status: TelegramStatus }) {
  const client = useQueryClient();
  const saved = status.proxy;
  const [open, setOpen] = useState(saved.problem !== null);
  const [link, setLink] = useState("");
  const [kind, setKind] = useState<TelegramProxyKind>(saved.kind === "none" ? "socks5" : saved.kind);
  const [host, setHost] = useState(saved.host);
  const [port, setPort] = useState(saved.port ? String(saved.port) : "");
  const [username, setUsername] = useState(saved.username ?? "");
  const [password, setPassword] = useState("");
  const [clearPassword, setClearPassword] = useState(false);
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState<"save" | "check" | "remove" | null>(null);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);

  // Тот же прокси, что сохранён: пустой пароль или секрет означает «оставить сохранённый».
  const sameProxy = saved.kind === kind && saved.host === host.trim() && String(saved.port) === port.trim();
  const keepsPassword = sameProxy && saved.hasPassword && (saved.username ?? "") === username.trim() && !clearPassword;
  const keepsSecret = sameProxy && saved.hasSecret;
  const useLink = link.trim().length > 0;
  const portValid = /^\d+$/.test(port.trim()) && Number(port) >= 1 && Number(port) <= 65535;
  const valid = useLink || (host.trim().length > 0 && portValid && (kind !== "mtproxy" || secret.trim().length > 0 || keepsSecret));

  const request = (): TelegramProxyRequest => {
    if (useLink) return { link: link.trim() };
    return {
      kind,
      host: host.trim(),
      port: Number(port.trim()),
      username: kind === "socks5" ? username.trim() || null : null,
      password: kind === "socks5" ? (password.length > 0 ? password : clearPassword ? "" : null) : null,
      secret: kind === "mtproxy" ? secret.trim() || null : null,
    };
  };

  const act = async (which: "save" | "check" | "remove") => {
    setBusy(which);
    setResult(null);
    try {
      if (which === "check") {
        setResult(await api.telegramCheckProxy(request()));
        return;
      }
      const next = await api.telegramSetProxy(which === "remove" ? { kind: "none" } : request());
      client.setQueryData(["telegram"], next);
      setLink("");
      setPassword("");
      setSecret("");
      setClearPassword(false);
      if (next.proxy.kind !== "none") {
        setKind(next.proxy.kind);
        setHost(next.proxy.host);
        setPort(String(next.proxy.port));
        setUsername(next.proxy.username ?? "");
      }
      setResult({ ok: true, message: which === "remove" ? "Прокси убран — Pepa подключается к Telegram напрямую." : `Сохранено: ${describeProxy(next.proxy)}.` });
    } catch (e) {
      setResult({ ok: false, message: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy(null);
    }
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (valid && !busy) void act("save");
  };

  return (
    <div className="stack" style={{ gap: 8 }} data-testid="telegram-proxy">
      <div className="row small">
        <Network size={15} />
        <span>
          Прокси: <strong data-testid="telegram-proxy-summary">{saved.problem ? "настройки не прочитались" : describeProxy(saved)}</strong>
        </span>
        <button type="button" className="link-button small" onClick={() => setOpen(!open)}>
          {open ? "Скрыть" : "Настроить"}
        </button>
      </div>
      {saved.problem && <Callout tone="danger">{saved.problem}</Callout>}

      {open && (
        <form className="stack" style={{ gap: 10 }} onSubmit={submit}>
          <p className="small muted">
            Нужен, если Telegram недоступен в вашей сети напрямую. Подойдёт SOCKS5 или MTProxy (прокси Telegram с секретом). Через прокси идут все
            соединения с Telegram, включая файлы.
          </p>
          <label className="field">
            <span className="field-label">Ссылка на прокси</span>
            <input className="input mono" autoComplete="off" value={link} placeholder="tg://proxy?server=…&port=…&secret=…  или  socks5://логин:пароль@хост:порт"
              onChange={(e) => setLink(e.target.value)} />
            <span className="field-help">Ссылка из Telegram (tg://proxy, tg://socks, t.me/proxy, t.me/socks) или от поставщика. Если она указана, поля ниже не нужны.</span>
          </label>

          {!useLink && (
            <>
              <div className="grid-3">
                <label className="field">
                  <span className="field-label">Вид</span>
                  <select className="select" value={kind} onChange={(e) => setKind(e.target.value as TelegramProxyKind)}>
                    <option value="socks5">SOCKS5</option>
                    <option value="mtproxy">MTProxy</option>
                  </select>
                </label>
                <label className="field">
                  <span className="field-label">Адрес</span>
                  <input className="input mono" autoComplete="off" value={host} placeholder="proxy.example.com" onChange={(e) => setHost(e.target.value)} />
                </label>
                <label className="field">
                  <span className="field-label">Порт</span>
                  <input className="input mono" inputMode="numeric" autoComplete="off" value={port} placeholder={kind === "mtproxy" ? "443" : "1080"}
                    onChange={(e) => setPort(e.target.value)} />
                </label>
              </div>
              {kind === "socks5" ? (
                <div className="grid-3">
                  <label className="field">
                    <span className="field-label">Логин</span>
                    <input className="input mono" autoComplete="off" value={username} placeholder="если нужен" onChange={(e) => setUsername(e.target.value)} />
                  </label>
                  <label className="field">
                    <span className="field-label">Пароль</span>
                    <input className="input mono" type="password" autoComplete="new-password" value={password}
                      placeholder={keepsPassword ? "сохранён — можно оставить пустым" : "если нужен"}
                      onChange={(e) => {
                        setPassword(e.target.value);
                        setClearPassword(false);
                      }} />
                    {keepsPassword && password.length === 0 && (
                      <button type="button" className="link-button small" style={{ alignSelf: "flex-start" }} onClick={() => setClearPassword(true)}>
                        Убрать сохранённый пароль
                      </button>
                    )}
                  </label>
                </div>
              ) : (
                <label className="field">
                  <span className="field-label">Секрет</span>
                  <input className="input mono" type="password" autoComplete="off" value={secret}
                    placeholder={keepsSecret ? "сохранён — можно оставить пустым" : "из ссылки: secret=…"}
                    onChange={(e) => setSecret(e.target.value)} />
                </label>
              )}
            </>
          )}

          {result && <Callout tone={result.ok ? "success" : "danger"}>{result.message}</Callout>}
          <div className="row wrap">
            <Button type="submit" variant="primary" size="small" busy={busy === "save"} disabled={!valid || busy !== null}>
              Сохранить и подключиться
            </Button>
            <Button size="small" icon={ShieldCheck} busy={busy === "check"} disabled={!valid || busy !== null}
              onClick={() => void act("check")}>
              Проверить
            </Button>
            {saved.kind !== "none" && (
              <Button size="small" variant="ghost" busy={busy === "remove"} disabled={busy !== null} onClick={() => void act("remove")}>
                Без прокси
              </Button>
            )}
          </div>
          <p className="small faint">Пароль и секрет хранятся на этом компьютере в зашифрованном виде и в интерфейс не возвращаются.</p>
        </form>
      )}
    </div>
  );
}
