import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";

import { AppErrorBoundary } from "./AppErrorBoundary";

function Broken({ shouldThrow }: { shouldThrow: boolean }) {
  if (shouldThrow) throw new Error("boom");
  return <div>Recovered content</div>;
}

describe("AppErrorBoundary", () => {
  beforeEach(() => vi.spyOn(console, "error").mockImplementation(() => undefined));

  it("shows an accessible recovery view and can retry", () => {
    const suppressExpectedWindowError = (event: ErrorEvent) => event.preventDefault();
    window.addEventListener("error", suppressExpectedWindowError);
    let shouldThrow = true;
    const { rerender } = render(
      <AppErrorBoundary>
        <Broken shouldThrow={shouldThrow} />
      </AppErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toHaveTextContent(/unexpected error/i);

    shouldThrow = false;
    rerender(
      <AppErrorBoundary>
        <Broken shouldThrow={shouldThrow} />
      </AppErrorBoundary>,
    );
    fireEvent.click(screen.getByRole("button", { name: /try again/i }));
    expect(screen.getByText("Recovered content")).toBeInTheDocument();
    window.removeEventListener("error", suppressExpectedWindowError);
  });
});
