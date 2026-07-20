import { test, expect, type Page } from "@playwright/test";

import { LEGAL_BUNDLE_VERSION } from "../src/legal";

// Sign in through the local-only dev-login (no OAuth keys needed) — a deterministic seam that
// still exercises the real session cookie + per-request authorization the app enforces.
async function devLogin(page: Page, email: string, role: string): Promise<void> {
  const devAuthSecret = process.env.E2E_DEV_AUTH_SECRET;
  if (!devAuthSecret) {
    throw new Error("E2E_DEV_AUTH_SECRET is required for the local-only dev login");
  }

  await page.goto("/login");
  const status = await page.evaluate(
    async ({ email, role, devAuthSecret, legalBundleVersion }) => {
      const r = await fetch("/api/auth/dev", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Dev-Auth-Secret": devAuthSecret,
        },
        body: JSON.stringify({
          email,
          role,
          legal_accepted: true,
          legal_bundle_version: legalBundleVersion,
        }),
      });
      return r.status;
    },
    { email, role, devAuthSecret, legalBundleVersion: LEGAL_BUNDLE_VERSION },
  );
  expect(status).toBe(200);
}

test("admin signs in and drives the console end to end", async ({ page }) => {
  await devLogin(page, "e2e-admin@example.com", "admin");

  await page.goto("/dashboard");
  await expect(page.getByRole("heading", { name: /dashboard/i })).toBeVisible();

  await page.goto("/admin/users");
  await expect(page.getByRole("link", { name: "e2e-admin@example.com" })).toBeVisible();

  // The permission-gated inline edit modal opens and shows the save control.
  await page.getByRole("button", { name: "Edit" }).first().click();
  await expect(page.getByText("// EDIT USER")).toBeVisible();
  await expect(page.getByRole("button", { name: "Save profile" })).toBeVisible();
});

test("admin edits a user's profile through the modal and the change persists", async ({ page }) => {
  await devLogin(page, "e2e-admin@example.com", "admin");
  await page.goto("/admin/users");

  // Open the inline edit modal for the first directory row.
  await page.getByRole("button", { name: "Edit" }).first().click();
  await expect(page.getByText("// EDIT USER")).toBeVisible();

  // Change the Title and save — a real PATCH round-trip.
  const title = `QA-${Date.now()}`;
  await page.getByLabel("Title").fill(title);
  await page.getByRole("button", { name: "Save profile" }).click();
  await expect(page.getByText("Profile saved.")).toBeVisible();

  // Reload and re-open the same row: the new title is persisted server-side.
  await page.reload();
  await page.getByRole("button", { name: "Edit" }).first().click();
  await expect(page.getByLabel("Title")).toHaveValue(title);
});

test("a plain user is denied the admin directory (RBAC enforced in the browser)", async ({
  page,
}) => {
  await devLogin(page, "e2e-user@example.com", "user");

  await page.goto("/admin/users");
  // RequirePermission("users.read") redirects a user who lacks it back to the dashboard.
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole("heading", { name: /dashboard/i })).toBeVisible();
});
