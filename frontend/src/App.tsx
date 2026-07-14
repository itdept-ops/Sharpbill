import { Navigate, Route, Routes } from "react-router-dom";

import { ProtectedRoute } from "./auth/ProtectedRoute";
import { RequirePermission } from "./auth/RequirePermission";
import { AppShell } from "./components/AppShell";
import { AboutPage } from "./pages/AboutPage";
import { AdminRolesPage } from "./pages/AdminRolesPage";
import { AdminUsersPage } from "./pages/AdminUsersPage";
import { DashboardPage } from "./pages/DashboardPage";
import { LandingPage } from "./pages/LandingPage";
import { LogsPage } from "./pages/LogsPage";
import { LoginPage } from "./pages/LoginPage";
import { SecurityPage } from "./pages/SecurityPage";
import { ProfilePage } from "./pages/ProfilePage";
import { SettingsPage } from "./pages/SettingsPage";
import { TechnologyPage } from "./pages/TechnologyPage";
import { UserDetailPage } from "./pages/UserDetailPage";

export default function App() {
  return (
    <Routes>
      {/* public */}
      <Route path="/" element={<LandingPage />} />
      <Route path="/about" element={<AboutPage />} />
      <Route path="/technology" element={<TechnologyPage />} />
      <Route path="/security" element={<SecurityPage />} />
      <Route path="/login" element={<LoginPage />} />

      {/* authenticated (behind the app shell) */}
      <Route element={<ProtectedRoute />}>
        <Route element={<AppShell />}>
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/profile" element={<ProfilePage />} />
          <Route
            path="/admin/users"
            element={
              <RequirePermission perm="users.read">
                <AdminUsersPage />
              </RequirePermission>
            }
          />
          <Route
            path="/admin/users/:id"
            element={
              <RequirePermission perm="users.read">
                <UserDetailPage />
              </RequirePermission>
            }
          />
          <Route
            path="/admin/roles"
            element={
              <RequirePermission perm="roles.manage">
                <AdminRolesPage />
              </RequirePermission>
            }
          />
          <Route
            path="/admin/settings"
            element={
              <RequirePermission perm="settings.manage">
                <SettingsPage />
              </RequirePermission>
            }
          />
          <Route
            path="/admin/logs"
            element={
              <RequirePermission perm="logs.view">
                <LogsPage />
              </RequirePermission>
            }
          />
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
