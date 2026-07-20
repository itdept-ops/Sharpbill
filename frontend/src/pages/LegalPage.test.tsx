import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

vi.mock("../components/MatrixRain", () => ({ MatrixRain: () => null }));

import { LEGAL_BUNDLE_VERSION } from "../legal";
import { LegalPage } from "./LegalPage";

describe("LegalPage", () => {
  it("renders a versioned, counsel-review draft with semantic sections", () => {
    render(
      <MemoryRouter>
        <LegalPage documentKey="privacy" />
      </MemoryRouter>,
    );

    expect(screen.getByRole("heading", { level: 1, name: "Privacy Notice" })).toBeInTheDocument();
    expect(screen.getByRole("note", { name: "Draft legal notice" })).toHaveTextContent(
      /DRAFT — PENDING LEGAL COUNSEL REVIEW/i,
    );
    expect(screen.getAllByText(LEGAL_BUNDLE_VERSION)).toHaveLength(2);
    expect(screen.getByRole("heading", { level: 2, name: /Data the service processes/i })).toBeInTheDocument();
    expect(screen.getByText(/2,555 days/)).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Legal" })).toBeInTheDocument();
  });

  it("links the complete legal bundle from every document", () => {
    render(
      <MemoryRouter>
        <LegalPage documentKey="terms" />
      </MemoryRouter>,
    );

    const legalNav = screen.getByRole("navigation", { name: "Legal" });
    expect(legalNav).toHaveTextContent("Terms");
    expect(legalNav).toHaveTextContent("EULA");
    expect(legalNav).toHaveTextContent("Acceptable Use");
    expect(legalNav).toHaveTextContent("Privacy");
    expect(screen.getByRole("link", { name: "Return to sign in" })).toHaveAttribute("href", "/login");
  });
});
