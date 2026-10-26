import type { QueryClient } from "@tanstack/react-query";
import { getToken } from "./client";

export type ConnectionState = "connecting" | "online" | "offline";

/**
 * Поток изменений от движка. При обрыве — переподключение с нарастающей паузой.
 * На каждое событие инвалидируются только затронутые данные.
 */
export function startEventStream(queryClient: QueryClient, onState: (state: ConnectionState) => void): () => void {
  let stopped = false;
  let controller: AbortController | null = null;
  let delay = 1000;

  const handle = (raw: string) => {
    let event: { type: string; id: string | null };
    try {
      event = JSON.parse(raw);
    } catch {
      return;
    }
    if (event.type === "process") {
      queryClient.invalidateQueries({ queryKey: ["processes"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      // Ресурсы и события меняются вместе с процессами (занят/освобождён, событие применено).
      queryClient.invalidateQueries({ queryKey: ["resources"] });
      queryClient.invalidateQueries({ queryKey: ["events"] });
      if (event.id) {
        queryClient.invalidateQueries({ queryKey: ["process", event.id] });
      }
    } else if (event.type === "events") {
      queryClient.invalidateQueries({ queryKey: ["events"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
    } else if (event.type === "resources") {
      queryClient.invalidateQueries({ queryKey: ["resources"] });
    } else if (event.type === "triggers") {
      queryClient.invalidateQueries({ queryKey: ["triggers"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
    } else if (event.type === "recorder") {
      queryClient.invalidateQueries({ queryKey: ["recorder"] });
    } else if (event.type === "integrations") {
      queryClient.invalidateQueries({ queryKey: ["telegram"] });
    } else if (event.type === "scenarios") {
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
    } else if (event.type === "scenario" && event.id) {
      queryClient.invalidateQueries({ queryKey: ["scenario", event.id] });
    }
  };

  const connect = async () => {
    while (!stopped) {
      controller = new AbortController();
      onState("connecting");
      try {
        const response = await fetch("/api/stream", {
          headers: { "X-Pepa-Token": getToken() ?? "" },
          signal: controller.signal,
        });
        if (!response.ok || !response.body) {
          throw new Error(`events ${response.status}`);
        }
        onState("online");
        delay = 1000;
        // После переподключения данные могли устареть.
        queryClient.invalidateQueries();
        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        let buffer = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += value;
          let index: number;
          while ((index = buffer.indexOf("\n\n")) >= 0) {
            const chunk = buffer.slice(0, index);
            buffer = buffer.slice(index + 2);
            for (const line of chunk.split("\n")) {
              if (line.startsWith("data: ")) handle(line.slice(6));
            }
          }
        }
      } catch {
        // Обрыв связи — попробуем снова.
      }
      if (stopped) break;
      onState("offline");
      await new Promise((resolve) => setTimeout(resolve, delay));
      delay = Math.min(delay * 2, 15000);
    }
  };

  void connect();
  return () => {
    stopped = true;
    controller?.abort();
  };
}
