import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import type { PrivacyStatus, User } from "../types";

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  patch: vi.fn(),
  post: vi.fn(),
  del: vi.fn(),
  setUser: vi.fn(),
}));
const authState = vi.hoisted(() => ({ user: null as User | null }));

vi.mock("../api/client", () => {
  class ApiError extends Error {}
  return {
    ApiError,
    api: { get: mocks.get, patch: mocks.patch, post: mocks.post, del: mocks.del },
  };
});
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: authState.user, setUser: mocks.setUser }),
}));

import { UserProfile } from "./UserProfile";

const self: User = {
  id: 7,
  email: "member@example.com",
  display_name: "Privacy Member",
  title: null,
  department: null,
  phone: null,
  location: "Portland",
  timezone: "America/Los_Angeles",
  bio: null,
  accent_color: null,
  ui_prefs: null,
  role: "user",
  role_id: 2,
  permissions: [],
  role_permissions: [],
  direct_permissions: [],
  access_version: 1,
  is_active: true,
  is_approved: true,
  status: "active",
  identities: [{ provider: "google", namespace: null, subject: "subject-7" }],
  auth_providers: ["google"],
  created_at: "2026-07-01T00:00:00Z",
  last_login_at: "2026-07-20T00:00:00Z",
  last_seen_at: "2026-07-20T00:00:00Z",
  online: true,
  last_latitude: 45.5152,
  last_longitude: -122.6784,
  last_location_accuracy: 25,
  last_location_at: "2026-07-20T12:00:00Z",
};

const policy = {
  precise_location_hours: 24,
  pending_accounts_days: 30,
  sessions_after_expiry_or_revocation_days: 30,
  request_activity_days: 90,
  erasure_grace_days: 30,
  disabled_accounts_days: 365,
  security_events_days: 400,
  generated_exports_retained: false,
};

const privacyStatus: PrivacyStatus = {
  policy,
  retention_hold: false,
  erasure_requested_at: null,
  erasure_due_at: null,
};

beforeEach(() => {
  authState.user = self;
  mocks.get.mockReset().mockImplementation((path: string) => {
    if (path === "/api/privacy") return Promise.resolve(privacyStatus);
    return Promise.reject(new Error(`Unexpected GET ${path}`));
  });
  mocks.patch.mockReset();
  mocks.post.mockReset();
  mocks.del.mockReset();
  mocks.setUser.mockReset();
});

afterEach(() => vi.restoreAllMocks());

describe("UserProfile privacy controls", () => {
  it("clears the signed-in user's saved location after explicit confirmation", async () => {
    mocks.del.mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    render(<UserProfile user={self} />);

    const clear = await screen.findByRole("button", { name: "Clear saved location" });
    expect(clear).toBeEnabled();
    await user.click(clear);

    expect(window.confirm).toHaveBeenCalledWith(expect.stringMatching(/cannot be undone/i));
    expect(mocks.del).toHaveBeenCalledWith("/api/privacy/location");
    expect(await screen.findByText("Saved location and timezone cleared.")).toBeInTheDocument();
    expect(mocks.setUser).toHaveBeenCalledWith(
      expect.objectContaining({
        location: null,
        timezone: null,
        last_latitude: null,
        last_longitude: null,
        last_location_accuracy: null,
        last_location_at: null,
      }),
    );
    expect(clear).toBeDisabled();
  });

  it("requests and cancels 30-day account erasure with an explicit warning and due date", async () => {
    const dueAt = "2026-08-19T12:00:00";
    const scheduled: PrivacyStatus = {
      ...privacyStatus,
      erasure_requested_at: "2026-07-20T12:00:00",
      erasure_due_at: dueAt,
    };
    mocks.post.mockResolvedValue(scheduled);
    mocks.del.mockResolvedValue(privacyStatus);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    render(<UserProfile user={self} />);

    await user.click(await screen.findByRole("button", { name: "Request account erasure" }));

    expect(confirm).toHaveBeenNthCalledWith(1, expect.stringMatching(/30 days.*anonymized/i));
    expect(mocks.post).toHaveBeenCalledWith("/api/privacy/erasure-request");
    const due = document.querySelector(`time[datetime="${dueAt}"]`);
    expect(due).not.toBeNull();
    expect(await screen.findByText(/Account erasure scheduled/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel account erasure" }));

    expect(confirm).toHaveBeenNthCalledWith(2, expect.stringMatching(/keep this account/i));
    expect(mocks.del).toHaveBeenCalledWith("/api/privacy/erasure-request");
    expect(await screen.findByText("Account erasure cancelled.")).toBeInTheDocument();
  });

  it("shows the active retention hold and due erasure while still allowing cancellation", async () => {
    const dueAt = "2026-08-19T12:00:00";
    mocks.get.mockResolvedValue({
      ...privacyStatus,
      retention_hold: true,
      erasure_requested_at: "2026-07-20T12:00:00",
      erasure_due_at: dueAt,
    });

    render(<UserProfile user={self} />);

    expect(await screen.findByText(/A retention hold is active/i)).toHaveTextContent(
      /existing erasure request can still be cancelled/i,
    );
    expect(screen.getByRole("button", { name: "Clear saved location" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel account erasure" })).toBeEnabled();
    expect(document.querySelector(`time[datetime="${dueAt}"]`)).not.toBeNull();
  });

  it("does not load or expose personal privacy controls while viewing another user", () => {
    authState.user = { ...self, id: 99, email: "admin@example.com", role: "admin" };

    render(<UserProfile user={self} />);

    expect(screen.queryByText("// PRIVACY")).not.toBeInTheDocument();
    expect(mocks.get).not.toHaveBeenCalledWith("/api/privacy");
  });
});
