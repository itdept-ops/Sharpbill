import { defineConfig, devices } from "@playwright/test";

// E2E drives the real running stack (Vite + FastAPI + MySQL) over the local-only dev-login seam.
// Start the stack first (docker compose up), then `npx playwright test`.
export default defineConfig({
  testDir: "./e2e",
  timeout: 30_000,
  expect: { timeout: 8_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: "line",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:5173",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
