import { expect, test } from "@playwright/test";

test("signed-out legal acceptance gate and draft documents are reachable", async ({ page }, testInfo) => {
  await page.goto("/login");

  const acceptance = page.getByRole("checkbox", {
    name: /I agree to the Terms of Service.*EULA.*Acceptable Use Policy.*acknowledge the Privacy Notice/i,
  });
  await expect(acceptance).toBeVisible();
  await expect(acceptance).not.toBeChecked();
  await expect(acceptance).toBeEnabled();

  const links = [
    ["Terms of Service", "/legal/terms-of-service.html"],
    ["EULA", "/legal/eula.html"],
    ["Acceptable Use Policy", "/legal/acceptable-use-policy.html"],
    ["Privacy Notice", "/legal/privacy-notice.html"],
  ] as const;
  for (const [name, href] of links) {
    const link = page.getByRole("link", { name: new RegExp(`${name}.*opens in a new tab`, "i") });
    await expect(link).toHaveAttribute("href", href);
    await expect(link).toHaveAttribute("target", "_blank");
    await expect(link).toHaveAttribute("rel", /noopener/);
  }

  await page.goto("/legal/privacy-notice.html");
  await expect(page.getByRole("heading", { level: 1, name: "Privacy Notice" })).toBeVisible();
  await expect(page.getByRole("note", { name: "Draft legal notice" })).toContainText(
    "DRAFT — PENDING LEGAL COUNSEL REVIEW",
  );

  await page.emulateMedia({ media: "print" });
  expect(await page.evaluate(() => window.matchMedia("print").matches)).toBe(true);
  await expect(page.locator(".legal-document")).toHaveCSS("background-color", "rgb(255, 255, 255)");
  await expect(page.locator(".legal-sections p").first()).toHaveCSS("color", "rgb(34, 34, 34)");
  await expect(page.locator(".legal-draft strong")).toHaveCSS("color", "rgb(122, 77, 0)");
  await page.screenshot({ path: testInfo.outputPath("privacy-notice-print.png"), fullPage: true });
});
