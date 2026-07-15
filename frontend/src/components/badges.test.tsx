import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";

import { RoleBadge, StatusPill } from "./badges";

describe("badges", () => {
  it("RoleBadge flags the admin role with the admin class", () => {
    const { container } = render(<RoleBadge role="admin" />);
    expect(screen.getByText("admin")).toBeInTheDocument();
    expect(container.firstChild).toHaveClass("role-badge", "admin");
  });

  it("RoleBadge renders a custom role without the admin class", () => {
    const { container } = render(<RoleBadge role="Auditor" />);
    expect(container.firstChild).toHaveClass("role-badge");
    expect(container.firstChild).not.toHaveClass("admin");
  });

  it("StatusPill reflects online / offline", () => {
    const { rerender } = render(<StatusPill online />);
    expect(screen.getByText(/ONLINE/)).toBeInTheDocument();
    rerender(<StatusPill online={false} />);
    expect(screen.getByText(/OFFLINE/)).toBeInTheDocument();
  });
});
