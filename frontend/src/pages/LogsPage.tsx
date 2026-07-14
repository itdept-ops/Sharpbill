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
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [method, setMethod] = useState("");
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    const q = new URLSearchParams({ limit: "200" });
    if (search.trim()) q.set("search", search.trim());
    if (method) q.set("method", method);
    api
      .get<RequestLogList>(`/api/admin/logs?${q.toString()}`)
      .then((r) => {
        setRows(r.items);
        setTotal(r.total);
      })
      .catch((e) => setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load" }))
      .finally(() => setLoading(false));
  }, [search, method]);

  useEffect(() => {
    const t = setTimeout(load, 200);
    return () => clearTimeout(t);
  }, [load]);

  const filtered = search.trim() !== "" || method !== "";

  return (
    <div>
      <h1 className="page-title">SYS://admin / request log</h1>
      <p className="page-sub">
        {total} recorded · endpoint · user · IP — most recent first
      </p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="filters">
        <input
          className="field-input search"
          placeholder="filter by endpoint path…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select className="field-input" value={method} onChange={(e) => setMethod(e.target.value)}>
          <option value="">all methods</option>
          {METHODS.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
        <span className="spacer" />
        <button className="btn btn-ghost btn-sm" onClick={load}>
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
            {loading && (
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
    </div>
  );
}
