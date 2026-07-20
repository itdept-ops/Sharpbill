import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

vi.mock("../components/MatrixRain", () => ({ MatrixRain: () => null }));

import { LEGAL_BUNDLE_VERSION, LEGAL_DRAFT_WARNING } from "../legal";
import { LegalPage } from "./LegalPage";

describe("LegalPage", () => {
  it("renders a versioned, counsel-review draft with semantic sections", () => {
    render(
      <MemoryRouter>
        <LegalPage documentKey="privacy" />
      </MemoryRouter>,
    );

    expect(screen.getByRole("heading", { level: 1, name: "Privacy Notice" })).toBeInTheDocument();
    const draftNotice = screen.getByRole("note", { name: "Draft legal notice" });
    expect(draftNotice).toHaveTextContent(
      /DRAFT — PENDING LEGAL COUNSEL REVIEW/i,
    );
    expect(draftNotice).toHaveTextContent(LEGAL_DRAFT_WARNING);
    expect(screen.getAllByText(LEGAL_BUNDLE_VERSION)).toHaveLength(2);
    expect(screen.getByRole("heading", { level: 2, name: /Data the service processes/i })).toBeInTheDocument();
    expect(screen.getByText(/2,555 days/)).toBeInTheDocument();
    expect(screen.getByText(/KingFisher, based in Hillsboro, Oregon/i)).toBeInTheDocument();
    expect(screen.getAllByText(/privacy@kingfisher\.com/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/at least 18 years old/i)).toBeInTheDocument();
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
