import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: {
      email: "admin@example.com",
      role: "admin",
      permissions: ["users.read", "roles.manage", "settings.manage", "logs.view"],
      ui_prefs: null,
    },
    logout: vi.fn(),
  }),
}));
vi.mock("../presence/PresenceContext", () => ({
  PresenceProvider: ({ children }: { children: React.ReactNode }) => children,
  usePresence: () => ({ online: [], count: 0, canView: false, live: false }),
}));
vi.mock("./MatrixRain", () => ({ MatrixRain: () => null }));

import { AppShell } from "./AppShell";

describe("AppShell mobile navigation structure", () => {
  it("exposes a named, collapsible navigation with permission-aware links", () => {
    render(
      <MemoryRouter initialEntries={["/dashboard"]}>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/dashboard" element={<h1>Dashboard</h1>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );
    const toggle = screen.getByRole("button", { name: "Open navigation" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("navigation", { name: "Mobile navigation" })).toHaveClass("open");
    expect(screen.getAllByRole("link", { name: /site settings/i })).toHaveLength(2);
  });
});
