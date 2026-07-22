import { useCallback, useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import type { RequestLog, RequestLogList } from "../types";

const METHODS = ["GET", "POST", "PATCH", "PUT", "DELETE"];

function statusClass(code: number): string {
  if (code >= 500) return "off";
  if (code >= 400) return "warn";
  if (code >= 300) return "info";
  return "ok";
}

export function LogsPage() {
  const [rows, setRows] = useState<RequestLog[]>([]);
  const [total, setTotal] = useState<number | null>(null);
  const [nextCursor, setNextCursor] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [method, setMethod] = useState("");
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);

  const load = useCallback((beforeId: number | null, append: boolean, signal?: AbortSignal) => {
    setLoading(true);
    const q = new URLSearchParams({ limit: "200" });
    if (beforeId != null) q.set("before_id", String(beforeId));
    if (search.trim()) q.set("search", search.trim());
    if (method) q.set("method", method);
    api
      .get<RequestLogList>(`/api/admin/logs?${q.toString()}`, { signal })
      .then((r) => {
        setRows((current) => append ? [...current, ...r.items] : r.items);
        setTotal(r.total);
        setNextCursor(r.next_cursor);
      })
      .catch((e) => {
        if (!(e instanceof DOMException && e.name === "AbortError")) {
          setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load" });
        }
      })
      .finally(() => {
        if (!signal?.aborted) setLoading(false);
      });
  }, [search, method]);

  useEffect(() => {
    const controller = new AbortController();
    setRows([]);
    setTotal(null);
    setNextCursor(null);
    const timer = setTimeout(() => load(null, false, controller.signal), 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [load]);

  const filtered = search.trim() !== "" || method !== "";
  const countLabel = total != null
    ? `${total} recorded`
    : nextCursor != null
      ? `${rows.length}+ shown`
      : `${rows.length} shown`;

  return (
    <div>
      <h1 className="page-title">SYS://admin / request log</h1>
      <p className="page-sub">
        {countLabel} · endpoint · user · IP — most recent first
      </p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button aria-label="Dismiss" onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="filters">
        <input
          className="field-input search"
          placeholder="filter by endpoint path prefix…"
          aria-label="Filter logs by endpoint path"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select
          className="field-input"
          aria-label="Filter by HTTP method"
          value={method}
          onChange={(e) => setMethod(e.target.value)}
        >
          <option value="">all methods</option>
          {METHODS.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
        <span className="spacer" />
        <button className="btn btn-ghost btn-sm" disabled={loading} onClick={() => load(null, false)}>
          Refresh
        </button>
      </div>

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Method</th>
              <th>Endpoint</th>
              <th>User</th>
              <th>IP</th>
              <th style={{ textAlign: "right" }}>Status</th>
            </tr>
          </thead>
          <tbody>
            {loading && rows.length === 0 && (
              <tr>
                <td colSpan={6} className="muted">
                  Loading…
                </td>
              </tr>
            )}
            {!loading && rows.length === 0 && (
              <tr>
                <td colSpan={6} className="muted">
                  {filtered ? "No requests match this filter." : "No requests logged yet."}
                </td>
              </tr>
            )}
            {rows.map((l) => (
              <tr key={l.id}>
                <td className="mono-id" title={new Date(l.created_at).toLocaleString()}>
                  {new Date(l.created_at).toLocaleTimeString()}
                </td>
                <td>
                  <span className={`method-tag m-${l.method.toLowerCase()}`}>{l.method}</span>
                </td>
                <td>{l.path}</td>
                <td className="sans">
                  {l.user_email ?? (l.user_id != null ? `#${l.user_id}` : "—")}
                </td>
                <td className="mono-id">{l.ip ?? "—"}</td>
                <td style={{ textAlign: "right" }}>
                  <span className={`status-pill ${statusClass(l.status_code)}`}>{l.status_code}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {nextCursor != null && (
        <div style={{ marginTop: "0.75rem", textAlign: "center" }}>
          <button
            className="btn btn-ghost btn-sm"
            disabled={loading}
            onClick={() => load(nextCursor, true)}
          >
            {loading ? "Loading…" : "Load older"}
          </button>
        </div>
      )}
    </div>
  );
}
