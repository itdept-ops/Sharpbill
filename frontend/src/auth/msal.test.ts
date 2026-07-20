import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  construct: vi.fn(),
  initialize: vi.fn(),
  loginPopup: vi.fn(),
}));

vi.mock("@azure/msal-browser", () => ({
  BrowserCacheLocation: { MemoryStorage: "memoryStorage" },
  PublicClientApplication: class {
    constructor(config: unknown) {
      mocks.construct(config);
    }

    initialize() {
      return mocks.initialize();
    }

    loginPopup(request: unknown) {
      return mocks.loginPopup(request);
    }
  },
}));

import { microsoftLogin } from "./msal";

beforeEach(() => {
  mocks.construct.mockReset();
  mocks.initialize.mockReset().mockResolvedValue(undefined);
  mocks.loginPopup.mockReset().mockResolvedValue({ idToken: "signed-token" });
});

describe("microsoftLogin", () => {
  it("keeps MSAL tokens in page memory and forwards the one-time nonce", async () => {
    await expect(microsoftLogin("one-time-nonce", "client-id")).resolves.toBe("signed-token");

    expect(mocks.construct).toHaveBeenCalledWith(
      expect.objectContaining({
        cache: { cacheLocation: "memoryStorage" },
      }),
    );
    expect(mocks.loginPopup).toHaveBeenCalledWith(
      expect.objectContaining({ nonce: "one-time-nonce" }),
    );
  });
});
