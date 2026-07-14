import type { Role } from "../types";

export function RoleBadge({ role }: { role: Role }) {
  return <span className={`badge role-${role}`}>{role}</span>;
}
