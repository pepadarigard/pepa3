import { useEffect, useState } from "react";

/** Маршрутизация по фрагменту адреса (#/…): работает и в WebView2, и в браузере без настройки сервера. */
export type Route =
  | { page: "home" }
  | { page: "scenarios" }
  | { page: "editor"; id?: string }
  | { page: "runs" }
  | { page: "history"; scenarioId?: string }
  | { page: "process"; id: string }
  | { page: "integrations" }
  | { page: "settings" };

export function parseRoute(hash: string): Route {
  const [path, query = ""] = hash.replace(/^#/, "").split("?");
  const parts = path.split("/").filter(Boolean).map(decodeURIComponent);
  const params = new URLSearchParams(query);
  switch (parts[0]) {
    case "scenarios":
      return { page: "scenarios" };
    case "editor":
      return { page: "editor", id: parts[1] };
    case "runs":
      return { page: "runs" };
    case "history":
      return { page: "history", scenarioId: params.get("scenario") ?? undefined };
    case "process":
      return parts[1] ? { page: "process", id: parts[1] } : { page: "runs" };
    case "integrations":
      return { page: "integrations" };
    case "settings":
      return { page: "settings" };
    default:
      return { page: "home" };
  }
}

export function href(route: Route): string {
  switch (route.page) {
    case "home":
      return "#/";
    case "editor":
      return route.id ? `#/editor/${encodeURIComponent(route.id)}` : "#/editor";
    case "process":
      return `#/process/${encodeURIComponent(route.id)}`;
    case "history":
      return route.scenarioId ? `#/history?scenario=${encodeURIComponent(route.scenarioId)}` : "#/history";
    default:
      return `#/${route.page}`;
  }
}

/** Перехватчик ухода со страницы (несохранённые изменения). */
let leaveGuard: (() => boolean) | null = null;

export function setLeaveGuard(guard: (() => boolean) | null) {
  leaveGuard = guard;
}

export function navigate(route: Route) {
  const target = href(route);
  if (target === window.location.hash) return;
  if (leaveGuard && !leaveGuard()) return;
  window.location.hash = target;
}

export function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.hash));
  useEffect(() => {
    let current = window.location.hash;
    const onChange = () => {
      // Переход по ссылке или кнопкой «назад» при несохранённых изменениях.
      if (leaveGuard && window.location.hash !== current && !leaveGuard()) {
        window.history.replaceState(null, "", current || "#/");
        return;
      }
      current = window.location.hash;
      setRoute(parseRoute(window.location.hash));
    };
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return route;
}
