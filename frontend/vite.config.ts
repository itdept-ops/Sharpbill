/// <reference types="vitest/config" />
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  test: {
    // Vitest owns the unit tests under src/**/*.test; Playwright e2e/*.spec.ts is run separately.
    include: ["src/**/*.test.{ts,tsx}"],
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
    globals: false,
    css: false,
  },
  build: {
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("node_modules/@azure/msal")) return "msal";
          if (
            id.includes("node_modules/react/") ||
            id.includes("node_modules/react-dom/") ||
            id.includes("node_modules/react-router")
          ) {
            return "react";
          }
        },
      },
    },
  },
  server: {
    host: true, // bind 0.0.0.0 so the container port is reachable
    port: 5173,
    strictPort: true,
    // When the container's 5173 is published on a different host port, the browser reaches the
    // app there — point the HMR websocket at that published port so live reload still works.
    hmr: { clientPort: Number(process.env.WEB_HOST_PORT) || 5173 },
    watch: {
      usePolling: process.env.CHOKIDAR_USEPOLLING === "true",
    },
    proxy: {
      "/api": {
        // In docker-compose this is http://api:8000; running Vite on the host falls back to localhost.
        target: process.env.VITE_API_PROXY_TARGET ?? "http://localhost:8000",
        changeOrigin: false,
        ws: true, // proxy the presence WebSocket too
      },
    },
  },
});
