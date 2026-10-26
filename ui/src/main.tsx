import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import "@xyflow/react/dist/style.css";
import "./styles/app.css";
import { App } from "./App";
import { initToken } from "./api/client";
import { FeedbackProvider } from "./components/feedback";
import { applyTheme, loadTheme } from "./lib/theme";

initToken();
applyTheme(loadTheme());

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 2_000,
      retry: (count, error) => count < 2 && !(error instanceof Error && "status" in error && (error as { status: number }).status === 404),
      refetchOnWindowFocus: true,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <FeedbackProvider>
        <App />
      </FeedbackProvider>
    </QueryClientProvider>
  </StrictMode>,
);
