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
        manualChunks: {
          react: ["react", "react-dom", "react-router-dom"],
          msal: ["@azure/msal-browser"],
        },
      },
    },
  },
  server: {
    host: true, // bind 0.0.0.0 so the container port is reachable
    port: 5173,
    strictPort: true,
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
