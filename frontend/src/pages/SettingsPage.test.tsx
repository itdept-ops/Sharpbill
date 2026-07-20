import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

const mocks = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), post: vi.fn() }));

vi.mock("../api/client", () => {
  class ApiError extends Error {}
  return { ApiError, api: { get: mocks.get, put: mocks.put, post: mocks.post } };
});
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { permissions: ["settings.manage"] } }),
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

beforeEach(() => {
  mocks.get.mockReset().mockImplementation((path: string) => {
    if (path === "/api/admin/settings") return Promise.resolve(settings);
    return Promise.reject(new Error(`Unexpected GET ${path}`));
  });
  mocks.put.mockReset();
  mocks.post.mockReset();
});

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
});
