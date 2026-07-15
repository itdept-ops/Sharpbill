export type Provider = "google" | "microsoft" | "dev";
export type UserStatus = "active" | "pending" | "disabled";
export type SignupMode = "open" | "approval" | "closed";

export interface Identity {
  provider: Provider;
  subject: string; // immutable provider id (Google sub / Microsoft oid)
}

export interface User {
  id: number;
  email: string;
  display_name: string | null;
  title: string | null;
  department: string | null;
  phone: string | null;
  location: string | null;
  timezone: string | null;
  bio: string | null;
  role: string;
  role_id: number;
  permissions: string[];
  is_active: boolean;
  is_approved: boolean;
  status: UserStatus;
  identities: Identity[];
  auth_providers: Provider[];
  created_at: string;
  last_login_at: string | null;
  last_seen_at: string | null;
  online: boolean;
  last_latitude: number | null;
  last_longitude: number | null;
  last_location_accuracy: number | null;
  last_location_at: string | null;
}

export interface UserList {
  items: User[];
  total: number;
}

export interface ProfileUpdate {
  display_name?: string | null;
  title?: string | null;
  department?: string | null;
  phone?: string | null;
  location?: string | null;
  timezone?: string | null;
  bio?: string | null;
}

export interface AuthConfig {
  google: boolean;
  microsoft: boolean;
  dev: boolean;
}

export interface DashboardData {
  stats: { total_users: number; active_users: number; online_users: number };
}

export interface Analytics {
  roles: { role: string; count: number }[];
  providers: { provider: string; count: number }[];
  signups: { date: string; count: number }[];
  status: { total: number; active: number; pending: number; disabled: number; online: number };
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
  display_name: string | null;
  role: string;
  last_seen_at: string | null;
}

export interface Presence {
  online: PresenceUser[];
  count: number;
  window_seconds: number;
}

export interface SiteSettings {
  signup_mode: SignupMode;
  allow_google: boolean;
  allow_microsoft: boolean;
  default_role_id: number;
  default_role_name: string;
  updated_at: string;
}

export interface SessionInfo {
  id: number;
  user_agent: string | null;
  ip: string | null;
  created_at: string;
  last_seen_at: string | null;
  current: boolean;
}

export interface RequestLog {
  id: number;
  method: string;
  path: string;
  user_id: number | null;
  user_email: string | null;
  ip: string | null;
  status_code: number;
  created_at: string;
}

export interface RequestLogList {
  items: RequestLog[];
  total: number;
}
