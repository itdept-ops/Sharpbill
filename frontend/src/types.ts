export type Provider = "google" | "microsoft" | "dev";
export type UserStatus = "active" | "pending" | "disabled";
export type SignupMode = "open" | "approval" | "closed";

export interface Identity {
  provider: Provider;
  subject: string; // immutable provider id (Google sub / Microsoft oid)
}

/** Per-user UI customization axes. Every field optional; a missing key renders at today's
 *  default. Mirrors the backend `UiPrefs` pydantic submodel. */
export interface UiPrefs {
  base_tone?: "abyss" | "ink" | "graphite" | "midnight" | "warm-black";
  background_depth?: "pure-black" | "standard" | "elevated";
  border_glow?: "hairline" | "standard" | "neon";
  glow_intensity?: "off" | "subtle" | "normal" | "intense";
  scanlines?: "off" | "subtle" | "standard" | "heavy";
  corner_radius?: "sharp" | "soft" | "round";
  motion?: "full" | "calm" | "reduced";
  rain_density?: number;
  rain_speed?: "still" | "slow" | "normal" | "fast";
  rain_glyphs?: "katakana" | "ascii" | "binary" | "hex";
  font_family?: "system" | "high-legibility" | "cascadia" | "jetbrains" | "consolas" | "menlo";
  text_scale?: "90" | "100" | "112" | "125";
  density?: "compact" | "comfortable" | "spacious";
  high_contrast_text?: boolean;
  reduce_transparency?: boolean;
  focus_ring?: "standard" | "bold" | "high-contrast";
  zebra_rows?: boolean;
  link_underlines?: boolean;
  v?: number;
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
  accent_color: string | null;
  ui_prefs: UiPrefs | null;
  role: string;
  role_id: number;
  permissions: string[]; // effective = role ∪ direct grants
  role_permissions: string[]; // inherited from the role
  direct_permissions: string[]; // granted directly to this user
  access_version: number;
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
  accent_color?: string | null;
  ui_prefs?: UiPrefs | null;
}

export interface AuthConfig {
  google: boolean;
  microsoft: boolean;
  google_client_id: string | null;
  microsoft_client_id: string | null;
  dev: boolean;
  calm: boolean;
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
  version: number;
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
  truncated: boolean;
  roster_limit: number;
}

export interface SiteSettings {
  signup_mode: SignupMode;
  allow_google: boolean;
  allow_microsoft: boolean;
  default_role_id: number;
  default_role_name: string;
  calm_mode: boolean;
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
  next_cursor: number | null;
}
