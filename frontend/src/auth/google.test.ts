import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const SCRIPT_SELECTOR = "script[data-kingfisher-google-identity]";

function installGoogleBrowserApi() {
  window.google = {
    accounts: {
      id: {
        initialize: vi.fn(),
        renderButton: vi.fn(),
      },
    },
  };
}

beforeEach(() => {
  vi.resetModules();
  document.querySelectorAll(SCRIPT_SELECTOR).forEach((script) => script.remove());
  delete window.google;
});

afterEach(() => {
  document.querySelectorAll(SCRIPT_SELECTOR).forEach((script) => script.remove());
  delete window.google;
});

describe("Google Identity Services loader", () => {
  it("shares one script and listener set across concurrent callers", async () => {
    const { loadGoogleIdentityServices } = await import("./google");

    const first = loadGoogleIdentityServices();
    const second = loadGoogleIdentityServices();

    expect(second).toBe(first);
    const scripts = document.querySelectorAll<HTMLScriptElement>(SCRIPT_SELECTOR);
    expect(scripts).toHaveLength(1);

    installGoogleBrowserApi();
    scripts[0].dispatchEvent(new Event("load"));
    await expect(Promise.all([first, second])).resolves.toEqual([undefined, undefined]);
    expect(document.querySelectorAll(SCRIPT_SELECTOR)).toHaveLength(1);
  });

  it("removes a failed script and creates only one clean replacement on retry", async () => {
    const { loadGoogleIdentityServices } = await import("./google");

    const failed = loadGoogleIdentityServices();
    const firstScript = document.querySelector<HTMLScriptElement>(SCRIPT_SELECTOR);
    expect(firstScript).not.toBeNull();
    firstScript?.dispatchEvent(new Event("error"));
    await expect(failed).rejects.toThrow(/failed to load/i);
    expect(document.querySelectorAll(SCRIPT_SELECTOR)).toHaveLength(0);

    const retried = loadGoogleIdentityServices();
    const replacement = document.querySelector<HTMLScriptElement>(SCRIPT_SELECTOR);
    expect(replacement).not.toBe(firstScript);
    expect(document.querySelectorAll(SCRIPT_SELECTOR)).toHaveLength(1);

    installGoogleBrowserApi();
    replacement?.dispatchEvent(new Event("load"));
    await expect(retried).resolves.toBeUndefined();
  });
});
