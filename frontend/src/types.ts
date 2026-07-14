export type Role = "admin" | "user";
export type Provider = "google" | "microsoft" | "dev";

export interface User {
  id: number;
  email: string;
  display_name: string | null;
  role: Role;
  is_active: boolean;
  auth_providers: Provider[];
  created_at: string;
  last_login_at: string | null;
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
  stats: { total_users: number; active_users: number };
}
