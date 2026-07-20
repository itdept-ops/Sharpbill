import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

const mocks = vi.hoisted(() => ({
  config: { google: false, microsoft: true, dev: false, calm: false },
  get: vi.fn(),
  post: vi.fn(),
  setUser: vi.fn(),
  microsoftLogin: vi.fn(),
}));

vi.mock("../api/client", () => {
  class ApiError extends Error {
    status = 500;
    code = "ERROR";
  }
  return { ApiError, api: { get: mocks.get, post: mocks.post } };
});
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null, setUser: mocks.setUser }),
}));
vi.mock("../auth/msal", () => ({ microsoftLogin: mocks.microsoftLogin }));
vi.mock("../components/MatrixRain", () => ({ MatrixRain: () => null }));

import { LoginPage } from "./LoginPage";

function renderLogin() {
  return render(
    <MemoryRouter>
      <LoginPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.config = { google: false, microsoft: true, dev: false, calm: false };
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
});

describe("LoginPage provider matrix", () => {
  it("offers Microsoft when it is the only effective provider and completes the nonce-bound flow", async () => {
    renderLogin();
    const button = await screen.findByRole("button", { name: /continue with microsoft/i });
    expect(document.querySelector(".google-slot")).not.toBeInTheDocument();

    fireEvent.click(button);
    await waitFor(() => expect(mocks.microsoftLogin).toHaveBeenCalledWith("server-nonce"));
    expect(mocks.post).toHaveBeenCalledWith("/api/auth/nonce");
    expect(mocks.post).toHaveBeenCalledWith("/api/auth/microsoft", {
      id_token: "microsoft-id-token",
    });
  });

  it("does not offer a disabled Microsoft provider", async () => {
    mocks.config = { ...mocks.config, google: true, microsoft: false };
    renderLogin();
    expect(await screen.findByText(/Sign in with Google to continue/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /microsoft/i })).not.toBeInTheDocument();
  });

  it("surfaces a clear unavailable state when no provider is effective", async () => {
    mocks.config = { ...mocks.config, google: false, microsoft: false };
    renderLogin();
    expect(await screen.findByRole("alert")).toHaveTextContent(/No sign-in provider/i);
  });
});
