import { test, expect, type Page } from "@playwright/test";

// Sign in through the local-only dev-login (no OAuth keys needed) — a deterministic seam that
// still exercises the real session cookie + per-request authorization the app enforces.
async function devLogin(page: Page, email: string, role: string): Promise<void> {
  await page.goto("/login");
  const status = await page.evaluate(
    async ({ email, role }) => {
      const r = await fetch("/api/auth/dev", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, role }),
      });
      return r.status;
    },
    { email, role },
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

test("a plain user is denied the admin directory (RBAC enforced in the browser)", async ({
  page,
}) => {
  await devLogin(page, "e2e-user@example.com", "user");

  await page.goto("/admin/users");
  // RequirePermission("users.read") redirects a user who lacks it back to the dashboard.
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole("heading", { name: /dashboard/i })).toBeVisible();
});
