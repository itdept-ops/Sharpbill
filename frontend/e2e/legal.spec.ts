import { expect, test } from "@playwright/test";

test("login consent choices share one responsive, independent control pattern", async ({ page }, testInfo) => {
  await page.goto("/login");

  const choices = page.getByRole("group", { name: "Before you continue" });
  const cards = choices.locator(".login-consent-option");
  const controls = choices.locator('input[type="checkbox"]');
  const acceptance = page.getByRole("checkbox", {
    name: /I agree to the Terms of Service.*EULA.*Acceptable Use Policy.*acknowledge the Privacy Notice/i,
  });
  const location = page.getByRole("checkbox", {
    name: "Share this device's location after sign-in",
  });

  await expect(choices).toBeVisible();
  await expect(cards).toHaveCount(2);
  await expect(acceptance).toHaveAttribute("required", "");
  await expect(location).not.toHaveAttribute("required", "");

  const cardStyles = await cards.evaluateAll((elements) =>
    elements.map((element) => {
      const style = window.getComputedStyle(element);
      return {
        display: style.display,
        gridTemplateColumns: style.gridTemplateColumns,
        padding: style.padding,
        borderLeftWidth: style.borderLeftWidth,
      };
    }),
  );
  expect(cardStyles[0]).toEqual(cardStyles[1]);
  expect(cardStyles[0]?.display).toBe("grid");

  const controlSizes = await controls.evaluateAll((elements) =>
    elements.map((element) => {
      const rect = element.getBoundingClientRect();
      return { width: rect.width, height: rect.height };
    }),
  );
  expect(controlSizes).toEqual([
    { width: 24, height: 24 },
    { width: 24, height: 24 },
  ]);

  await expect(acceptance).toBeEnabled();
  await expect(location).toBeEnabled();
  await acceptance.focus();
  await page.keyboard.press("Space");
  await expect(acceptance).toBeChecked();
  await expect(location).not.toBeChecked();

  await location.focus();
  await page.keyboard.press("Space");
  await expect(location).toBeChecked();
  await expect(acceptance).toBeChecked();

  await acceptance.focus();
  await page.keyboard.press("Space");
  await expect(acceptance).not.toBeChecked();
  await expect(location).toBeChecked();

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(choices).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("login-consent-mobile.png"), fullPage: true });
});

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
  await expect(page.getByText("2026-07-20-v2").first()).toBeVisible();
  await expect(page.getByText(/KingFisher, based in Hillsboro, Oregon/i)).toBeVisible();
  await expect(page.getByText(/privacy@kingfisher\.com/i).first()).toBeVisible();
  await expect(page.getByText(/at least 18 years old/i)).toBeVisible();

  await page.emulateMedia({ media: "print" });
  expect(await page.evaluate(() => window.matchMedia("print").matches)).toBe(true);
  await expect(page.locator(".legal-document")).toHaveCSS("background-color", "rgb(255, 255, 255)");
  await expect(page.locator(".legal-sections p").first()).toHaveCSS("color", "rgb(34, 34, 34)");
  await expect(page.locator(".legal-draft strong")).toHaveCSS("color", "rgb(122, 77, 0)");
  await page.screenshot({ path: testInfo.outputPath("privacy-notice-print.png"), fullPage: true });
});
