import { NavLink, Outlet, useNavigate } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { RoleBadge } from "./RoleBadge";

export function Layout() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  const handleLogout = async () => {
    await logout();
    navigate("/login", { replace: true });
  };

  return (
    <div className="app-shell">
      <header className="app-nav">
        <span className="brand">
          <span className="logo">KF</span>
          Kingfisher CRM
        </span>
        <nav className="nav-links">
          <NavLink to="/dashboard">Dashboard</NavLink>
          {user?.role === "admin" && <NavLink to="/admin/users">Users</NavLink>}
        </nav>
        <span className="nav-spacer" />
        {user && (
          <span className="user-chip">
            <span className="user-email">{user.email}</span>
            <RoleBadge role={user.role} />
          </span>
        )}
        <button className="btn-link" onClick={handleLogout}>
          Log out
        </button>
      </header>
      <main className="app-body">
        <Outlet />
      </main>
    </div>
  );
}
