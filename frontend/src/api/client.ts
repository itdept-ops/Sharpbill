export class ApiError extends Error {
  status: number;
  code: string;
  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(fn: (() => void) | null): void {
  onUnauthorized = fn;
}

interface RequestOpts {
  suppressAuthRedirect?: boolean;
}

async function request<T>(path: string, init: RequestInit = {}, opts: RequestOpts = {}): Promise<T> {
  const res = await fetch(path, {
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", ...(init.headers ?? {}) },
    ...init,
  });

  if (res.status === 401 && !opts.suppressAuthRedirect) {
    onUnauthorized?.();
  }

  if (!res.ok) {
    let code = "ERROR";
    let message = res.statusText;
    try {
      const body = await res.json();
      const detail = body?.detail;
      if (typeof detail === "string") {
        message = detail;
      } else if (detail && typeof detail === "object") {
        message = detail.message ?? message;
        code = detail.code ?? code;
      }
    } catch {
      /* non-JSON body */
    }
    throw new ApiError(res.status, code, message);
  }

  return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
}

// Fetch a binary response (e.g. a CSV download) while reusing the shared 401 redirect and
// {detail:{code,message}} error handling that JSON requests get.
async function requestBlob(path: string): Promise<Blob> {
  const res = await fetch(path, { credentials: "same-origin" });
  if (res.status === 401) onUnauthorized?.();
  if (!res.ok) {
    let code = "ERROR";
    let message = res.statusText;
    try {
      const detail = (await res.json())?.detail;
      if (detail && typeof detail === "object") {
        message = detail.message ?? message;
        code = detail.code ?? code;
      }
    } catch {
      /* non-JSON body */
    }
    throw new ApiError(res.status, code, message);
  }
  return res.blob();
}

export const api = {
  get: <T>(path: string, opts?: RequestOpts) => request<T>(path, {}, opts),
  getBlob: (path: string) => requestBlob(path),
  post: <T>(path: string, body?: unknown, opts?: RequestOpts) =>
    request<T>(path, { method: "POST", body: body ? JSON.stringify(body) : undefined }, opts),
  patch: <T>(path: string, body: unknown, opts?: RequestOpts) =>
    request<T>(path, { method: "PATCH", body: JSON.stringify(body) }, opts),
  put: <T>(path: string, body: unknown, opts?: RequestOpts) =>
    request<T>(path, { method: "PUT", body: JSON.stringify(body) }, opts),
  del: <T>(path: string, opts?: RequestOpts) => request<T>(path, { method: "DELETE" }, opts),
};
