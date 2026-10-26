import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Сборка кладётся рядом с Pepa.Host — её раздаёт локальный сервер (и окно WebView2).
// В режиме разработки запросы /api проксируются к запущенному pepa-server (порт 5180).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/Pepa.Host/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://127.0.0.1:5180", changeOrigin: true },
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
