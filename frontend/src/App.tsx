import { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";

import { ProtectedRoute } from "./auth/ProtectedRoute";
import { RequirePermission } from "./auth/RequirePermission";
import { AppShell } from "./components/AppShell";

// Route-level code-splitting: each page is its own chunk, so the public marketing pages don't
// ship the whole admin console (and vice-versa). Named exports are adapted to default for lazy().
const named = <T,>(p: Promise<T>, key: keyof T) => p.then((m) => ({ default: m[key] }));
const LandingPage = lazy(() => named(import("./pages/LandingPage"), "LandingPage"));
const AboutPage = lazy(() => named(import("./pages/AboutPage"), "AboutPage"));
const TechnologyPage = lazy(() => named(import("./pages/TechnologyPage"), "TechnologyPage"));
const SecurityPage = lazy(() => named(import("./pages/SecurityPage"), "SecurityPage"));
const LoginPage = lazy(() => named(import("./pages/LoginPage"), "LoginPage"));
const LegalPage = lazy(() => named(import("./pages/LegalPage"), "LegalPage"));
const DashboardPage = lazy(() => named(import("./pages/DashboardPage"), "DashboardPage"));
const ProfilePage = lazy(() => named(import("./pages/ProfilePage"), "ProfilePage"));
const AdminUsersPage = lazy(() => named(import("./pages/AdminUsersPage"), "AdminUsersPage"));
const UserDetailPage = lazy(() => named(import("./pages/UserDetailPage"), "UserDetailPage"));
const AdminRolesPage = lazy(() => named(import("./pages/AdminRolesPage"), "AdminRolesPage"));
const SettingsPage = lazy(() => named(import("./pages/SettingsPage"), "SettingsPage"));
const LogsPage = lazy(() => named(import("./pages/LogsPage"), "LogsPage"));

export default function App() {
  return (
    <Suspense fallback={<div className="page-loader">Loading…</div>}>
      <Routes>
      {/* public */}
      <Route path="/" element={<LandingPage />} />
      <Route path="/about" element={<AboutPage />} />
      <Route path="/technology" element={<TechnologyPage />} />
      <Route path="/security" element={<SecurityPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/legal/terms-of-service.html" element={<LegalPage documentKey="terms" />} />
      <Route path="/legal/eula.html" element={<LegalPage documentKey="eula" />} />
      <Route path="/legal/privacy-notice.html" element={<LegalPage documentKey="privacy" />} />
      <Route path="/legal/acceptable-use-policy.html" element={<LegalPage documentKey="aup" />} />

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
    </Suspense>
  );
}
