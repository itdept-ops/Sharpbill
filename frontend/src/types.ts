export type Provider = "google" | "microsoft" | "dev";

export interface Identity {
  provider: Provider;
  subject: string; // immutable provider id (Google sub / Microsoft oid)
}

export interface User {
  id: number;
  email: string;
  display_name: string | null;
  role: string;
  role_id: number;
  permissions: string[];
  is_active: boolean;
  identities: Identity[];
  auth_providers: Provider[];
  created_at: string;
  last_login_at: string | null;
  last_seen_at: string | null;
  online: boolean;
}

export interface UserList {
  items: User[];
  total: number;
}

export interface AuthConfig {
  google: boolean;
  microsoft: boolean;
  dev: boolean;
}

export interface DashboardData {
  message: string;
  stats: { total_users: number; active_users: number; online_users: number };
}

export interface Permission {
  id: number;
  key: string;
  description: string | null;
  is_system: boolean;
}

export interface Role {
  id: number;
  name: string;
  description: string | null;
  is_system: boolean;
  permissions: Permission[];
  user_count: number;
}

export interface PresenceUser {
  id: number;
  email: string;
  display_name: string | null;
  role: string;
  last_seen_at: string | null;
}

export interface Presence {
  online: PresenceUser[];
  count: number;
  window_seconds: number;
}
