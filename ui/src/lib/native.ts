// Нативные функции оконного приложения (WebView2). В обычном браузере недоступны и скрываются.

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
}

function bridge(): WebViewBridge | null {
  const chrome = (window as unknown as { chrome?: { webview?: WebViewBridge } }).chrome;
  return chrome?.webview ?? null;
}

let nextId = 1;

function call<T>(type: string, payload: Record<string, unknown> = {}): Promise<T> {
  const webview = bridge();
  if (!webview) return Promise.reject(new Error("Доступно только в приложении Pepa для Windows."));
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    const listener = (event: { data: unknown }) => {
      const data = event.data as { id?: number; ok?: boolean; result?: T; error?: string } | null;
      if (!data || data.id !== id) return;
      webview.removeEventListener("message", listener);
      if (data.ok) resolve(data.result as T);
      else reject(new Error(data.error ?? "Ошибка оконного приложения"));
    };
    webview.addEventListener("message", listener);
    webview.postMessage({ id, type, ...payload });
  });
}

export const native = {
  available: () => bridge() !== null,
  pickFolder: (initial?: string) => call<string | null>("pickFolder", { initial: initial ?? null }),
  openFolder: (path: string) => call<null>("openFolder", { path }),
};
