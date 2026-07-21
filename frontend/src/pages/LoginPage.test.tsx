import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import {
  LEGAL_ACCEPTANCE_LABEL,
  LEGAL_BUNDLE_VERSION,
  LEGAL_DOCUMENT_SHA256,
  type LegalManifest,
} from "../legal";
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
  legalManifest: {
    bundle_version: "2026-07-20-v2",
    effective_date: "2026-07-20",
    required_at_login: true,
    acceptance_label:
      "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge the Privacy Notice.",
    precise_location_retention_hours: 24,
    legal_acceptance_retention_days: 2555,
    documents: [
      {
        key: "terms",
        title: "Terms of Service",
        version: "2026-07-20-v2",
        sha256: "0000000000000000000000000000000000000000000000000000000000000000",
        url: "/legal/terms-of-service.html",
        acceptance: "agreement",
      },
      {
        key: "eula",
        title: "End User License Agreement",
        version: "2026-07-20-v2",
        sha256: "0000000000000000000000000000000000000000000000000000000000000000",
        url: "/legal/eula.html",
        acceptance: "agreement",
      },
      {
        key: "acceptable_use",
        title: "Acceptable Use Policy",
        version: "2026-07-20-v2",
        sha256: "0000000000000000000000000000000000000000000000000000000000000000",
        url: "/legal/acceptable-use-policy.html",
        acceptance: "agreement",
      },
      {
        key: "privacy",
        title: "Privacy Notice",
        version: "2026-07-20-v2",
        sha256: "0000000000000000000000000000000000000000000000000000000000000000",
        url: "/legal/privacy-notice.html",
        acceptance: "acknowledgement",
      },
    ],
  } as LegalManifest,
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
  mocks.legalManifest = {
    bundle_version: LEGAL_BUNDLE_VERSION,
    effective_date: "2026-07-20",
    required_at_login: true,
    acceptance_label:
      "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge the Privacy Notice.",
    precise_location_retention_hours: 24,
    legal_acceptance_retention_days: 2555,
    documents: [
      { key: "terms", title: "Terms of Service", version: LEGAL_BUNDLE_VERSION, sha256: LEGAL_DOCUMENT_SHA256.terms, url: "/legal/terms-of-service.html", acceptance: "agreement" },
      { key: "eula", title: "End User License Agreement", version: LEGAL_BUNDLE_VERSION, sha256: LEGAL_DOCUMENT_SHA256.eula, url: "/legal/eula.html", acceptance: "agreement" },
      { key: "acceptable_use", title: "Acceptable Use Policy", version: LEGAL_BUNDLE_VERSION, sha256: LEGAL_DOCUMENT_SHA256.aup, url: "/legal/acceptable-use-policy.html", acceptance: "agreement" },
      { key: "privacy", title: "Privacy Notice", version: LEGAL_BUNDLE_VERSION, sha256: LEGAL_DOCUMENT_SHA256.privacy, url: "/legal/privacy-notice.html", acceptance: "acknowledgement" },
    ],
  };
  mocks.get.mockReset().mockImplementation((path: string) => {
    if (path === "/api/auth/config") return Promise.resolve(mocks.config);
    if (path === "/api/legal/manifest") return Promise.resolve(mocks.legalManifest);
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

async function acceptanceCheckbox(): Promise<HTMLInputElement> {
  return screen.findByRole("checkbox", {
    name: /I agree to the Terms of Service.*EULA.*Acceptable Use Policy.*acknowledge the Privacy Notice/i,
  });
}

async function acceptLegalBundle(): Promise<void> {
  await flushEffects(2);
  fireEvent.click(
    screen.getByRole("checkbox", {
      name: /I agree to the Terms of Service.*EULA.*Acceptable Use Policy.*acknowledge the Privacy Notice/i,
    }),
  );
}

describe("LoginPage provider matrix", () => {
  it("offers Microsoft when it is the only effective provider and completes the nonce-bound flow", async () => {
    renderLogin();
    const button = await screen.findByRole("button", { name: /continue with microsoft/i });
    expect(document.querySelector(".google-slot")).not.toBeInTheDocument();
    expect(button).toBeDisabled();

    await acceptLegalBundle();
    expect(button).toBeEnabled();
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
      legal_accepted: true,
      legal_bundle_version: LEGAL_BUNDLE_VERSION,
    });
  });

  it("requires one unchecked agreement and exposes every legal document without losing login state", async () => {
    renderLogin();

    const checkbox = await acceptanceCheckbox();
    const locationOptIn = screen.getByRole("checkbox", {
      name: /share this device's location after sign-in/i,
    });
    const choices = screen.getByRole("group", { name: /before you continue/i });
    expect(choices.querySelectorAll(".login-consent-option")).toHaveLength(2);
    expect(checkbox.closest(".login-consent-option")).toBeInTheDocument();
    expect(locationOptIn.closest(".login-consent-option")).toBeInTheDocument();
    expect(screen.getByText("Required")).toBeInTheDocument();
    expect(screen.getByText("Optional")).toBeInTheDocument();
    expect(checkbox).not.toBeChecked();
    expect(checkbox).toBeRequired();
    expect(checkbox).toBeEnabled();
    expect(locationOptIn).not.toBeRequired();
    expect(locationOptIn).not.toBeChecked();
    expect(screen.getByText(/Draft bundle — counsel review required before production/i)).toBeInTheDocument();
    expect(
      screen.getByText(/precise coordinates scheduled for clearing after 24 hours unless held/i),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
    expect(mocks.post).not.toHaveBeenCalledWith("/api/auth/nonce");

    const agreementLabel = screen.getByText(LEGAL_ACCEPTANCE_LABEL);
    fireEvent.click(agreementLabel);
    expect(checkbox).toBeChecked();
    expect(locationOptIn).not.toBeChecked();
    fireEvent.click(agreementLabel);
    expect(checkbox).not.toBeChecked();

    fireEvent.click(screen.getByText("Share this device's location after sign-in"));
    expect(locationOptIn).toBeChecked();
    expect(checkbox).not.toBeChecked();
    fireEvent.click(screen.getByText("Share this device's location after sign-in"));
    expect(locationOptIn).not.toBeChecked();

    const expectedLinks = [
      ["Terms of Service", "/legal/terms-of-service.html"],
      ["EULA", "/legal/eula.html"],
      ["Acceptable Use Policy", "/legal/acceptable-use-policy.html"],
      ["Privacy Notice", "/legal/privacy-notice.html"],
    ];
    for (const [name, href] of expectedLinks) {
      const link = screen.getByRole("link", { name: new RegExp(`${name}.*opens in a new tab`, "i") });
      expect(link).toHaveAttribute("href", href);
      expect(link).toHaveAttribute("target", "_blank");
      expect(link).toHaveAttribute("rel", expect.stringContaining("noopener"));
      expect(link).toHaveAttribute("rel", expect.stringContaining("noreferrer"));
      expect(link.closest("label")).toBeNull();
    }
  });

  it("blocks location opt-in until the verified active retention policy is displayed", async () => {
    let resolveManifest!: (manifest: LegalManifest) => void;
    const deferredManifest = new Promise<LegalManifest>((resolve) => {
      resolveManifest = resolve;
    });
    mocks.get.mockImplementation((path: string) => {
      if (path === "/api/auth/config") return Promise.resolve(mocks.config);
      if (path === "/api/legal/manifest") return deferredManifest;
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });

    renderLogin();

    const locationOptIn = screen.getByRole("checkbox", {
      name: /share this device's location after sign-in/i,
    });
    expect(locationOptIn).toBeDisabled();
    expect(locationOptIn).not.toBeChecked();
    expect(screen.queryByText(/coordinates scheduled for clearing after 24 hours/i)).not.toBeInTheDocument();
    expect(screen.getByText(/becomes available after the current legal and retention policy loads/i)).toBeInTheDocument();

    await act(async () => {
      resolveManifest({ ...mocks.legalManifest, precise_location_retention_hours: 720 });
    });

    await waitFor(() => expect(locationOptIn).toBeEnabled());
    expect(locationOptIn).not.toBeChecked();
    expect(
      screen.getByText(/precise coordinates scheduled for clearing after 720 hours unless held/i),
    ).toBeInTheDocument();
  });

  it("fails closed when the legal manifest cannot load", async () => {
    mocks.get.mockImplementation((path: string) => {
      if (path === "/api/auth/config") return Promise.resolve(mocks.config);
      if (path === "/api/legal/manifest") return Promise.reject(new Error("offline"));
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });

    renderLogin();

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not load the current legal bundle/i);
    expect(await acceptanceCheckbox()).toBeDisabled();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
    expect(mocks.post).not.toHaveBeenCalledWith("/api/auth/nonce");
  });

  it("fails closed when the server legal bundle is newer than this web build", async () => {
    mocks.legalManifest = { ...mocks.legalManifest, bundle_version: "2026-08-01-v2" };

    renderLogin();

    expect(await screen.findByRole("alert")).toHaveTextContent(/web build does not match/i);
    expect(await acceptanceCheckbox()).toBeDisabled();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
  });

  it("fails closed when the public retention disclosure is outside supported bounds", async () => {
    mocks.legalManifest = { ...mocks.legalManifest, precise_location_retention_hours: 721 };

    renderLogin();

    expect(await screen.findByRole("alert")).toHaveTextContent(/web build does not match/i);
    expect(await acceptanceCheckbox()).toBeDisabled();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
  });

  it("fails closed when a manifest document digest does not match the rendered text", async () => {
    mocks.legalManifest = {
      ...mocks.legalManifest,
      documents: mocks.legalManifest.documents.map((document) =>
        document.key === "privacy" ? { ...document, sha256: "f".repeat(64) } : document,
      ),
    };

    renderLogin();

    expect(await screen.findByRole("alert")).toHaveTextContent(/web build does not match/i);
    expect(await acceptanceCheckbox()).toBeDisabled();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
  });

  it.each([
    [428, "LEGAL_ACCEPTANCE_REQUIRED"],
    [409, "LEGAL_BUNDLE_STALE"],
  ])("clears acceptance and blocks retry for backend legal error %s %s", async (status, code) => {
    mocks.post.mockImplementation((path: string) => {
      if (path === "/api/auth/nonce") return Promise.resolve({ nonce: "server-nonce" });
      if (path === "/api/auth/microsoft") {
        return Promise.reject(new ApiError(status, code, "Legal bundle changed"));
      }
      return Promise.reject(new Error(`Unexpected POST ${path}`));
    });

    renderLogin();
    await acceptLegalBundle();
    fireEvent.click(await screen.findByRole("button", { name: /continue with microsoft/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      code === "LEGAL_BUNDLE_STALE"
        ? /legal terms changed.*page refresh or updated web release may be required/i
        : /legal acceptance could not be verified/i,
    );
    expect(await acceptanceCheckbox()).not.toBeChecked();
    expect(screen.getByRole("button", { name: /continue with microsoft/i })).toBeDisabled();
    const manifestRequests = () =>
      mocks.get.mock.calls.filter(([path]) => path === "/api/legal/manifest");
    if (code === "LEGAL_BUNDLE_STALE") {
      await waitFor(() => expect(manifestRequests()).toHaveLength(2));
    } else {
      expect(manifestRequests()).toHaveLength(1);
    }
  });

  it("keeps acceptance immutable while a Microsoft popup is in flight", async () => {
    let resolveMicrosoftLogin!: (token: string) => void;
    mocks.microsoftLogin.mockReturnValue(
      new Promise<string>((resolve) => {
        resolveMicrosoftLogin = resolve;
      }),
    );

    renderLogin();
    await acceptLegalBundle();
    fireEvent.click(await screen.findByRole("button", { name: /continue with microsoft/i }));
    await waitFor(() => expect(mocks.microsoftLogin).toHaveBeenCalledTimes(1));

    const checkbox = await acceptanceCheckbox();
    const locationOptIn = screen.getByRole("checkbox", {
      name: /share this device's location after sign-in/i,
    });
    expect(checkbox).toBeChecked();
    expect(checkbox).toBeDisabled();
    expect(locationOptIn).not.toBeChecked();
    expect(locationOptIn).toBeDisabled();
    expect(screen.getByRole("button", { name: /opening microsoft/i })).toBeDisabled();

    await act(async () => resolveMicrosoftLogin("deferred-microsoft-token"));
    await waitFor(() =>
      expect(mocks.post).toHaveBeenCalledWith("/api/auth/microsoft", {
        id_token: "deferred-microsoft-token",
        legal_accepted: true,
        legal_bundle_version: LEGAL_BUNDLE_VERSION,
      }),
    );
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
    expect(screen.getByRole("button", { name: /continue with google/i })).toBeDisabled();
    expect(mocks.googleInitialize).not.toHaveBeenCalled();
    expect(mocks.post).not.toHaveBeenCalledWith("/api/auth/nonce", undefined, expect.anything());
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
    await acceptLegalBundle();
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
    await acceptLegalBundle();
    await flushEffects();
    const staleInitialization = googleInitialization(0);

    await act(async () => staleInitialization.callback({ credential: "google-id-token" }));
    await flushEffects();

    expect(screen.getByRole("alert")).toHaveTextContent(/Invalid Google token/i);
    expect(mocks.googleInitialize).toHaveBeenCalledTimes(2);
    expect(googleInitialization(1).nonce).toBe("server-nonce-2");
    expect(mocks.post).toHaveBeenCalledWith(
      "/api/auth/google",
      {
        id_token: "google-id-token",
        legal_accepted: true,
        legal_bundle_version: LEGAL_BUNDLE_VERSION,
      },
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
