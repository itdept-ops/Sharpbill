import type { Provider } from "../types";

export function RoleBadge({ role }: { role: string }) {
  return <span className={`role-badge ${role === "admin" ? "admin" : ""}`}>{role}</span>;
}

export function ProviderBadge({ provider }: { provider: Provider }) {
  return <span className={`provider-badge ${provider}`}>{provider}</span>;
}

export function OnlineDot({ online }: { online: boolean }) {
  return (
    <span className={`online-dot ${online ? "on" : ""}`} title={online ? "online" : "offline"} />
  );
}

export function StatusPill({ online }: { online: boolean }) {
  return online ? (
    <span className="status-pill ok">● ONLINE</span>
  ) : (
    <span className="status-pill off">○ OFFLINE</span>
  );
}
