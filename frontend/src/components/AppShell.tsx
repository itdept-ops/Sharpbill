import { useEffect, useRef, useState } from "react";
import { Link, NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { PresenceProvider, usePresence } from "../presence/PresenceContext";
import { RoleBadge } from "./badges";
import { MatrixRain } from "./MatrixRain";
import { DEFAULT_RAIN_DENSITY } from "../util/theme";

function breadcrumb(pathname: string): string {
  const parts = pathname.replace(/^\/+/, "").split("/").filter(Boolean);
  return parts.length ? parts.join(" / ") : "dashboard";
}

function ConsoleNavigation({
  can,
  onNavigate,
}: {
  can: (permission: string) => boolean;
  onNavigate?: () => void;
}) {
  return (
    <>
      <div className="rail-brand">
        <span className="logo-glyph" aria-hidden="true">◈</span> KINGFISHER
      </div>
      <div className="rail-section">Operations</div>
      <NavLink to="/dashboard" className="rail-item" onClick={onNavigate}>▸ Dashboard</NavLink>
      <NavLink to="/profile" className="rail-item" onClick={onNavigate}>▸ My Profile</NavLink>
      {(can("users.read") || can("roles.manage") || can("settings.manage") || can("logs.view")) && (
        <div className="rail-section">Admin</div>
      )}
      {can("users.read") && (
        <NavLink to="/admin/users" className="rail-item" onClick={onNavigate}>▸ Users</NavLink>
      )}
      {can("roles.manage") && (
        <NavLink to="/admin/roles" className="rail-item" onClick={onNavigate}>▸ Roles &amp; Access</NavLink>
      )}
      {can("settings.manage") && (
        <NavLink to="/admin/settings" className="rail-item" onClick={onNavigate}>▸ Site Settings</NavLink>
      )}
      {can("logs.view") && (
        <NavLink to="/admin/logs" className="rail-item" onClick={onNavigate}>▸ Request Log</NavLink>
      )}
      <div className="rail-spacer" />
      <div className="rail-section">Info</div>
      <NavLink to="/technology" className="rail-item" onClick={onNavigate}>▸ Technology</NavLink>
      <NavLink to="/about" className="rail-item" onClick={onNavigate}>▸ About</NavLink>
    </>
  );
}

function ShellInner() {
  const { user, logout } = useAuth();
  const presence = usePresence();
  const location = useLocation();
  const navigate = useNavigate();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButtonRef = useRef<HTMLButtonElement>(null);

  const can = (p: string) => !!user?.permissions.includes(p);

  useEffect(() => setMenuOpen(false), [location.pathname]);
  useEffect(() => {
    if (!menuOpen) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMenuOpen(false);
        menuButtonRef.current?.focus();
      }
    };
    document.addEventListener("keydown", closeOnEscape);
    return () => document.removeEventListener("keydown", closeOnEscape);
  }, [menuOpen]);

  const handleLogout = async () => {
    await logout();
    navigate("/login", { replace: true });
  };

  // Console rain honors the user's density preference (0 hides it entirely).
  const rainOpacity = user?.ui_prefs?.rain_density ?? DEFAULT_RAIN_DENSITY;

  return (
    <div className="shell">
      {rainOpacity > 0 && <MatrixRain opacity={rainOpacity} />}
      <nav className="rail" aria-label="Primary navigation">
        <ConsoleNavigation can={can} />
      </nav>

      <header className="topbar">
        <button
          ref={menuButtonRef}
          className="mobile-menu-button"
          type="button"
          aria-label={menuOpen ? "Close navigation" : "Open navigation"}
          aria-controls="mobile-navigation"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen((open) => !open)}
        >
          {menuOpen ? "✕" : "☰"}
        </button>
        <span className="breadcrumb">
          KF://<span className="path"> {breadcrumb(location.pathname)}</span>
        </span>
        <span className="spacer" />
        {import.meta.env.DEV && <span className="env-pill">DEV</span>}
        {presence.canView && (
          <span className="online-count">
            <span className="online-dot on" /> {presence.count} online
          </span>
        )}
        {user && (
          <span className="user-chip">
            <Link to="/profile" className="email">
              {user.email}
            </Link>
            <RoleBadge role={user.role} />
          </span>
        )}
        <button className="calm-toggle" onClick={handleLogout}>
          Log out
        </button>
      </header>

      <nav
        id="mobile-navigation"
        className={`mobile-nav ${menuOpen ? "open" : ""}`}
        aria-label="Mobile navigation"
        aria-hidden={!menuOpen}
      >
        <ConsoleNavigation can={can} onNavigate={() => setMenuOpen(false)} />
      </nav>

      <div className="status-line" aria-hidden="true" />

      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}

export function AppShell() {
  return (
    <PresenceProvider>
      <ShellInner />
    </PresenceProvider>
  );
}
