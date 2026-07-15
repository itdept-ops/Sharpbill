import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// Control the "current user" the gate sees.
const state = vi.hoisted(() => ({ user: null as { permissions: string[] } | null }));
vi.mock("./AuthContext", () => ({ useAuth: () => ({ user: state.user }) }));

import { RequirePermission } from "./RequirePermission";

function renderGated(perm: string) {
  return render(
    <MemoryRouter>
      <RequirePermission perm={perm}>
        <div>secret-content</div>
      </RequirePermission>
    </MemoryRouter>,
  );
}

describe("RequirePermission", () => {
  it("renders children when the user holds the permission", () => {
    state.user = { permissions: ["users.read", "presence.view"] };
    renderGated("users.read");
    expect(screen.getByText("secret-content")).toBeInTheDocument();
  });

  it("blocks (redirects away) when the user lacks the permission", () => {
    state.user = { permissions: ["presence.view"] };
    renderGated("users.read");
    expect(screen.queryByText("secret-content")).not.toBeInTheDocument();
  });

  it("blocks when there is no authenticated user", () => {
    state.user = null;
    renderGated("users.read");
    expect(screen.queryByText("secret-content")).not.toBeInTheDocument();
  });
});
