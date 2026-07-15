import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api, setUnauthorizedHandler } from "./client";

function mockFetch(status: number, body?: unknown) {
  const res = {
    ok: status >= 200 && status < 300,
    status,
    statusText: "error",
    json: async () => body,
    blob: async () => new Blob([typeof body === "string" ? body : JSON.stringify(body ?? "")]),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => res as unknown as Response),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  setUnauthorizedHandler(null);
});

describe("api client", () => {
  it("parses the {detail:{code,message}} envelope into an ApiError", async () => {
    mockFetch(403, { detail: { code: "FORBIDDEN", message: "Missing permission" } });
    await expect(api.get("/x")).rejects.toMatchObject({
      status: 403,
      code: "FORBIDDEN",
      message: "Missing permission",
    });
  });

  it("invokes the unauthorized handler on 401", async () => {
    const onUnauth = vi.fn();
    setUnauthorizedHandler(onUnauth);
    mockFetch(401, { detail: { code: "INVALID_SESSION", message: "x" } });
    await expect(api.get("/me")).rejects.toBeInstanceOf(ApiError);
    expect(onUnauth).toHaveBeenCalledOnce();
  });

  it("suppresses the redirect when suppressAuthRedirect is set", async () => {
    const onUnauth = vi.fn();
    setUnauthorizedHandler(onUnauth);
    mockFetch(401, {});
    await expect(api.get("/me", { suppressAuthRedirect: true })).rejects.toBeInstanceOf(ApiError);
    expect(onUnauth).not.toHaveBeenCalled();
  });

  it("returns undefined for a 204 response", async () => {
    mockFetch(204);
    await expect(api.post("/logout")).resolves.toBeUndefined();
  });

  it("getBlob returns a Blob on success and still honors the 401 handler", async () => {
    mockFetch(200, "id,email\n1,a@b.c");
    await expect(api.getBlob("/export.csv")).resolves.toBeInstanceOf(Blob);

    const onUnauth = vi.fn();
    setUnauthorizedHandler(onUnauth);
    mockFetch(401, { detail: { code: "INVALID_SESSION", message: "x" } });
    await expect(api.getBlob("/export.csv")).rejects.toBeInstanceOf(ApiError);
    expect(onUnauth).toHaveBeenCalledOnce();
  });
});
