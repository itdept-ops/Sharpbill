import { useCallback, useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import type { Contact, ContactList, ContactStats, ContactStatus } from "../types";

const STATUSES: ContactStatus[] = ["lead", "active", "customer", "archived"];
const STATUS_CLS: Record<string, string> = {
  lead: "info",
  active: "ok",
  customer: "warn",
  archived: "off",
};

function StatusPill({ status }: { status: string }) {
  return <span className={`status-pill ${STATUS_CLS[status] ?? "off"}`}>{status}</span>;
}

type Draft = Partial<Contact> & { first_name: string };

export function ContactsPage() {
  const { user } = useAuth();
  const canWrite = !!user?.permissions.includes("contacts.write");
  const [rows, setRows] = useState<Contact[]>([]);
  const [stats, setStats] = useState<ContactStats | null>(null);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [mine, setMine] = useState(false);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);

  const loadStats = () => api.get<ContactStats>("/api/contacts/stats").then(setStats).catch(() => setStats(null));

  const load = useCallback(() => {
    const q = new URLSearchParams();
    if (search.trim()) q.set("search", search.trim());
    if (status) q.set("status", status);
    if (mine) q.set("mine", "true");
    api
      .get<ContactList>(`/api/contacts?${q.toString()}`)
      .then((r) => setRows(r.items))
      .catch((e) => setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load" }));
  }, [search, status, mine]);

  useEffect(() => {
    const t = setTimeout(load, 200);
    return () => clearTimeout(t);
  }, [load]);
  useEffect(() => {
    loadStats();
  }, []);

  const save = async () => {
    if (!draft) return;
    setBanner(null);
    try {
      const payload = {
        first_name: draft.first_name,
        last_name: draft.last_name ?? null,
        email: draft.email ?? null,
        phone: draft.phone ?? null,
        company: draft.company ?? null,
        title: draft.title ?? null,
        status: draft.status ?? "lead",
        notes: draft.notes ?? null,
      };
      if (draft.id) await api.patch<Contact>(`/api/contacts/${draft.id}`, payload);
      else await api.post<Contact>("/api/contacts", payload);
      setDraft(null);
      load();
      loadStats();
      setBanner({ msg: "Contact saved.", ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Save failed" });
    }
  };

  const remove = async (c: Contact) => {
    setBanner(null);
    try {
      await api.del(`/api/contacts/${c.id}`);
      setRows((r) => r.filter((x) => x.id !== c.id));
      loadStats();
      setBanner({ msg: `Deleted ${c.full_name}.`, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Delete failed" });
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://contacts</h1>
      <p className="page-sub">{rows.length} shown · your customer pipeline</p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      {stats && (
        <div className="pipeline">
          {STATUSES.map((s) => {
            const n = stats.by_status.find((x) => x.status === s)?.count ?? 0;
            return (
              <button
                key={s}
                className={`pipe-stage ${status === s ? "active" : ""}`}
                onClick={() => setStatus(status === s ? "" : s)}
              >
                <span className="pipe-count">{n}</span>
                <span className={`status-pill ${STATUS_CLS[s]}`}>{s}</span>
              </button>
            );
          })}
        </div>
      )}

      <div className="filters">
        <input
          className="field-input search"
          placeholder="search name, company, email…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select className="field-input" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">all stages</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <label className="user-chip" style={{ gap: 6 }}>
          <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
          mine only
        </label>
        <span className="spacer" />
        {canWrite && (
          <button className="btn btn-primary btn-sm" onClick={() => setDraft({ first_name: "", status: "lead" })}>
            + New contact
          </button>
        )}
      </div>

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Company</th>
              <th>Title</th>
              <th>Email</th>
              <th>Stage</th>
              <th>Owner</th>
              {canWrite && <th style={{ textAlign: "right" }}>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && (
              <tr>
                <td colSpan={canWrite ? 7 : 6} className="muted">
                  No contacts yet.
                </td>
              </tr>
            )}
            {rows.map((c) => (
              <tr key={c.id}>
                <td>{c.full_name}</td>
                <td className="sans">{c.company ?? "—"}</td>
                <td className="sans">{c.title ?? "—"}</td>
                <td>{c.email ?? "—"}</td>
                <td>
                  <StatusPill status={c.status} />
                </td>
                <td className="sans">{c.owner_name ?? "—"}</td>
                {canWrite && (
                  <td>
                    <div className="row-actions">
                      <button className="icon-btn" onClick={() => setDraft({ ...c })}>
                        Edit
                      </button>
                      <button className="icon-btn danger" onClick={() => remove(c)}>
                        Delete
                      </button>
                    </div>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {draft && (
        <div className="modal" onClick={() => setDraft(null)}>
          <section className="panel panel--brackets modal-panel" onClick={(e) => e.stopPropagation()}>
            <div className="panel-header">// {draft.id ? "EDIT CONTACT" : "NEW CONTACT"}</div>
            <div className="panel-body">
              <div className="form-grid">
                <Field label="First name" v={draft.first_name} on={(v) => setDraft({ ...draft, first_name: v })} />
                <Field label="Last name" v={draft.last_name} on={(v) => setDraft({ ...draft, last_name: v })} />
                <Field label="Company" v={draft.company} on={(v) => setDraft({ ...draft, company: v })} />
                <Field label="Title" v={draft.title} on={(v) => setDraft({ ...draft, title: v })} />
                <Field label="Email" v={draft.email} on={(v) => setDraft({ ...draft, email: v })} />
                <Field label="Phone" v={draft.phone} on={(v) => setDraft({ ...draft, phone: v })} />
                <div className="field">
                  <label className="field-label">Stage</label>
                  <select
                    className="field-input"
                    value={draft.status ?? "lead"}
                    onChange={(e) => setDraft({ ...draft, status: e.target.value as ContactStatus })}
                  >
                    {STATUSES.map((s) => (
                      <option key={s} value={s}>
                        {s}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field full">
                  <label className="field-label">Notes</label>
                  <textarea
                    className="field-input"
                    rows={3}
                    value={draft.notes ?? ""}
                    onChange={(e) => setDraft({ ...draft, notes: e.target.value })}
                  />
                </div>
              </div>
              <div className="role-card-actions">
                <button className="btn btn-primary btn-sm" onClick={save} disabled={!draft.first_name.trim()}>
                  Save contact
                </button>
                <button className="btn btn-ghost btn-sm" onClick={() => setDraft(null)}>
                  Cancel
                </button>
              </div>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

function Field({ label, v, on }: { label: string; v?: string | null; on: (v: string) => void }) {
  return (
    <div className="field">
      <label className="field-label">{label}</label>
      <input className="field-input" value={v ?? ""} onChange={(e) => on(e.target.value)} />
    </div>
  );
}
