import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import type { AuthConfig } from "../types";

const mocks = vi.hoisted(() => ({
  config: {
    google: false,
    microsoft: true,
    google_client_id: null,
    microsoft_client_id: "microsoft-client-id",
    dev: false,
    calm: false,
  } as AuthConfig,
  get: vi.fn(),
  post: vi.fn(),
  setUser: vi.fn(),
  microsoftLogin: vi.fn(),
  googleInitialize: vi.fn(),
  googleRenderButton: vi.fn(),
}));

vi.mock("../api/client", () => {
  class ApiError extends Error {
    constructor(
      public status: number,
      public code: string,
      message: string,
    ) {
      super(message);
    }
  }
  return { ApiError, api: { get: mocks.get, post: mocks.post } };
});
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null, setUser: mocks.setUser }),
}));
vi.mock("../auth/msal", () => ({ microsoftLogin: mocks.microsoftLogin }));
vi.mock("../components/MatrixRain", () => ({ MatrixRain: () => null }));

import { ApiError } from "../api/client";
import { GOOGLE_NONCE_REFRESH_MS } from "../auth/google";
import { LoginPage } from "./LoginPage";

type GoogleInitialization = {
  client_id: string;
  nonce?: string;
  callback: (response: { credential: string }) => Promise<void>;
};

function renderLogin() {
  return render(
    <MemoryRouter>
      <LoginPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.config = {
    google: false,
    microsoft: true,
    google_client_id: null,
    microsoft_client_id: "microsoft-client-id",
    dev: false,
    calm: false,
  };
  mocks.get.mockReset().mockImplementation((path: string) => {
    if (path === "/api/auth/config") return Promise.resolve(mocks.config);
    return Promise.reject(new Error(`Unexpected GET ${path}`));
  });
  mocks.post.mockReset().mockImplementation((path: string) => {
    if (path === "/api/auth/nonce") return Promise.resolve({ nonce: "server-nonce" });
    return Promise.resolve({ id: 7, permissions: [] });
  });
  mocks.setUser.mockReset();
  mocks.microsoftLogin.mockReset().mockResolvedValue("microsoft-id-token");
  mocks.googleInitialize.mockReset();
  mocks.googleRenderButton.mockReset();
  window.google = {
    accounts: {
      id: {
        initialize: mocks.googleInitialize,
        renderButton: mocks.googleRenderButton,
      },
    },
  };
});

afterEach(() => {
  vi.useRealTimers();
  delete window.google;
});

async function flushEffects(cycles = 6) {
  for (let cycle = 0; cycle < cycles; cycle += 1) {
    await act(async () => Promise.resolve());
  }
}

function googleInitialization(index: number): GoogleInitialization {
  return mocks.googleInitialize.mock.calls[index][0] as GoogleInitialization;
}

describe("LoginPage provider matrix", () => {
  it("offers Microsoft when it is the only effective provider and completes the nonce-bound flow", async () => {
    renderLogin();
    const button = await screen.findByRole("button", { name: /continue with microsoft/i });
    expect(document.querySelector(".google-slot")).not.toBeInTheDocument();

    fireEvent.click(button);
    await waitFor(() =>
      expect(mocks.microsoftLogin).toHaveBeenCalledWith(
        "server-nonce",
        "microsoft-client-id",
      ),
    );
    expect(mocks.post).toHaveBeenCalledWith("/api/auth/nonce");
    expect(mocks.post).toHaveBeenCalledWith("/api/auth/microsoft", {
      id_token: "microsoft-id-token",
    });
  });

  it("does not offer a disabled Microsoft provider", async () => {
    mocks.config = {
      ...mocks.config,
      google: true,
      microsoft: false,
      google_client_id: "google-client-id",
      microsoft_client_id: null,
    };
    renderLogin();
    expect(await screen.findByText(/Sign in with Google to continue/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /microsoft/i })).not.toBeInTheDocument();
  });

  it("surfaces a clear unavailable state when no provider is effective", async () => {
    mocks.config = { ...mocks.config, google: false, microsoft: false };
    renderLogin();
    expect(await screen.findByRole("alert")).toHaveTextContent(/No sign-in provider/i);
  });

  it("does not render a provider whose runtime public client ID is missing", async () => {
    mocks.config = {
      ...mocks.config,
      google: true,
      microsoft: true,
      google_client_id: null,
      microsoft_client_id: null,
    };
    renderLogin();

    expect(await screen.findByRole("alert")).toHaveTextContent(/No sign-in provider/i);
    expect(document.querySelector(".google-slot")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /microsoft/i })).not.toBeInTheDocument();
    expect(mocks.post).not.toHaveBeenCalledWith("/api/auth/nonce", undefined, expect.anything());
  });

  it("rotates the rendered Google button to a fresh nonce before the backend nonce expires", async () => {
    vi.useFakeTimers();
    mocks.config = {
      ...mocks.config,
      google: true,
      microsoft: false,
      google_client_id: "google-client-id",
      microsoft_client_id: null,
    };
    let nonceNumber = 0;
    mocks.post.mockImplementation((path: string) => {
      if (path === "/api/auth/nonce") {
        nonceNumber += 1;
        return Promise.resolve({ nonce: `server-nonce-${nonceNumber}` });
      }
      return Promise.resolve({ id: 7, permissions: [] });
    });

    renderLogin();
    await flushEffects();
    expect(mocks.googleInitialize).toHaveBeenCalledTimes(1);
    expect(googleInitialization(0)).toMatchObject({
      client_id: "google-client-id",
      nonce: "server-nonce-1",
    });

    await act(async () => vi.advanceTimersByTimeAsync(GOOGLE_NONCE_REFRESH_MS));
    await flushEffects();

    expect(mocks.googleInitialize).toHaveBeenCalledTimes(2);
    expect(googleInitialization(1)).toMatchObject({
      client_id: "google-client-id",
      nonce: "server-nonce-2",
    });
    expect(mocks.googleRenderButton).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(1);
  });

  it("abandons a consumed Google nonce and reinitializes after a backend auth failure", async () => {
    vi.useFakeTimers();
    mocks.config = {
      ...mocks.config,
      google: true,
      microsoft: false,
      google_client_id: "google-client-id",
      microsoft_client_id: null,
    };
    let nonceNumber = 0;
    mocks.post.mockImplementation((path: string) => {
      if (path === "/api/auth/nonce") {
        nonceNumber += 1;
        return Promise.resolve({ nonce: `server-nonce-${nonceNumber}` });
      }
      if (path === "/api/auth/google") {
        return Promise.reject(new ApiError(401, "INVALID_TOKEN", "Invalid Google token"));
      }
      return Promise.reject(new Error(`Unexpected POST ${path}`));
    });

    renderLogin();
    await flushEffects();
    const staleInitialization = googleInitialization(0);

    await act(async () => staleInitialization.callback({ credential: "google-id-token" }));
    await flushEffects();

    expect(screen.getByRole("alert")).toHaveTextContent(/Invalid Google token/i);
    expect(mocks.googleInitialize).toHaveBeenCalledTimes(2);
    expect(googleInitialization(1).nonce).toBe("server-nonce-2");
    expect(mocks.post).toHaveBeenCalledWith(
      "/api/auth/google",
      { id_token: "google-id-token" },
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );

    // A provider/browser duplicate callback cannot replay the credential or the consumed nonce.
    await act(async () => staleInitialization.callback({ credential: "google-id-token-replay" }));
    expect(
      mocks.post.mock.calls.filter(([path]) => path === "/api/auth/google"),
    ).toHaveLength(1);
    expect(vi.getTimerCount()).toBe(1);
  });
});
