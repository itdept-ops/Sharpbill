import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";

const mocks = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn() }));
const authState = vi.hoisted(() => ({ permissions: ["settings.manage"] as string[] }));

vi.mock("../api/client", () => {
  class ApiError extends Error {}
  return { ApiError, api: { get: mocks.get, put: mocks.put, post: mocks.post } };
});
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { permissions: authState.permissions } }),
}));

import { SettingsPage } from "./SettingsPage";

const settings = {
  signup_mode: "approval",
  allow_google: true,
  allow_microsoft: true,
  default_role_id: 2,
  default_role_name: "user",
  calm_mode: false,
  updated_at: "2026-07-20T00:00:00Z",
};

const privacyStatus = {
  policy: {
    precise_location_hours: 24,
    pending_accounts_days: 30,
    sessions_after_expiry_or_revocation_days: 30,
    request_activity_days: 90,
    erasure_grace_days: 30,
    disabled_accounts_days: 365,
    security_events_days: 400,
    generated_exports_retained: false,
  },
  retention_hold: false,
  retention_hold_reference: null,
};

beforeEach(() => {
  authState.permissions = ["settings.manage"];
  mocks.get.mockReset().mockImplementation((path: string) => {
    if (path === "/api/admin/settings") return Promise.resolve(settings);
    return Promise.reject(new Error(`Unexpected GET ${path}`));
  });
  mocks.put.mockReset();
  mocks.post.mockReset();
});

afterEach(() => vi.restoreAllMocks());

describe("SettingsPage permission-aware controls", () => {
  it("does not call APIs the current user cannot access and keeps controls named", async () => {
    render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("checkbox", { name: "Google sign-in" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Microsoft sign-in" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Calm mode" })).not.toBeChecked();
    expect(screen.getByText("users.read").parentElement).toHaveTextContent(/Requires users.read/i);
    expect(screen.getByTitle(/Changing this requires roles.manage/i)).toHaveTextContent("user");
    expect(mocks.get).toHaveBeenCalledTimes(1);
  });

  it("explains that approval is provider-wide and settings-authoritative", async () => {
    render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );

    expect(
      await screen.findByText(/cryptographically verified by an enabled provider can request/i),
    ).toHaveTextContent(/stays pending until an admin approves/i);
    expect(screen.getByText(/Sign-up mode is the admission policy/i)).toHaveTextContent(
      /Email domains are not restricted/i,
    );
  });

  it("warns that open onboarding is broad and requires a least-privileged default role", async () => {
    mocks.get.mockImplementation((path: string) => {
      if (path === "/api/admin/settings") {
        return Promise.resolve({ ...settings, signup_mode: "open" });
      }
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });

    render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );

    expect(await screen.findByText(/Any user cryptographically verified/i)).toHaveTextContent(
      /configured default role/i,
    );
    expect(screen.getByText(/Open is broad/i)).toHaveTextContent(
      /no email-domain restriction.*least-privileged/i,
    );
  });

  it("permission-gates retention controls and confirms hold enablement with an external reference", async () => {
    authState.permissions = ["settings.manage", "privacy.manage"];
    mocks.get.mockImplementation((path: string) => {
      if (path === "/api/admin/settings") return Promise.resolve(settings);
      if (path === "/api/admin/privacy") return Promise.resolve(privacyStatus);
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });
    mocks.put.mockResolvedValue({
      ...privacyStatus,
      retention_hold: true,
      retention_hold_reference: "LEGAL-2026-0042",
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );

    const reference = await screen.findByRole("textbox", { name: "External case reference" });
    expect(reference).toHaveAccessibleDescription(/terse ticket or legal-case key/i);
    const enable = screen.getByRole("button", { name: "Enable retention hold" });
    expect(enable).toBeDisabled();
    await user.type(reference, "LEGAL-2026-0042");
    expect(enable).toBeEnabled();
    await user.click(enable);

    expect(confirm).toHaveBeenCalledWith(expect.stringMatching(/suspend/i));
    expect(mocks.put).toHaveBeenCalledWith("/api/admin/privacy/hold", {
      enabled: true,
      reference: "LEGAL-2026-0042",
    });
    expect(await screen.findByText("Retention hold enabled.")).toBeInTheDocument();
    expect(screen.getByText("LEGAL-2026-0042")).toBeInTheDocument();
  });

  it("requires confirmation before releasing an active retention hold", async () => {
    authState.permissions = ["settings.manage", "privacy.manage"];
    mocks.get.mockImplementation((path: string) => {
      if (path === "/api/admin/settings") return Promise.resolve(settings);
      if (path === "/api/admin/privacy") {
        return Promise.resolve({
          ...privacyStatus,
          retention_hold: true,
          retention_hold_reference: "CASE-88",
        });
      }
      return Promise.reject(new Error(`Unexpected GET ${path}`));
    });
    mocks.put.mockResolvedValue(privacyStatus);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );

    await user.click(await screen.findByRole("button", { name: "Release retention hold" }));

    expect(confirm).toHaveBeenCalledWith(expect.stringMatching(/resume/i));
    expect(mocks.put).toHaveBeenCalledWith("/api/admin/privacy/hold", { enabled: false });
    expect(await screen.findByText("Retention hold released.")).toBeInTheDocument();
  });
});
