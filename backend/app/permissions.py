"""Canonical built-in permission keys and system-role definitions.

These are the permissions the app's own code checks by literal key. Admins can additionally
create new permissions at runtime (stored in the DB) and attach them to roles — those need no
constant here. The initial RBAC migration seeds exactly the built-ins below, and
`tests/test_roles.py` asserts the seed stays in sync with this module.
"""

USERS_READ = "users.read"
USERS_MANAGE = "users.manage"
USERS_EXPORT = "users.export"
ROLES_MANAGE = "roles.manage"
PRESENCE_VIEW = "presence.view"
PRESENCE_KICK = "presence.kick"
SETTINGS_MANAGE = "settings.manage"
LOGS_VIEW = "logs.view"
SECURITY_EVENTS_VIEW = "security_events.view"
PRIVACY_MANAGE = "privacy.manage"

# (key, description) — order is the seed order.
BUILTIN_PERMISSIONS: list[tuple[str, str]] = [
    (USERS_READ, "View the user directory"),
    (USERS_MANAGE, "Manage user profiles, activation, and approval"),
    (USERS_EXPORT, "Export the user directory as CSV"),
    (ROLES_MANAGE, "Create and edit roles and permissions"),
    (PRESENCE_VIEW, "See who is currently online"),
    (PRESENCE_KICK, "Force sign-out (kick) a user's active sessions"),
    (SETTINGS_MANAGE, "Manage site-wide configuration"),
    (LOGS_VIEW, "View the request activity log"),
    (SECURITY_EVENTS_VIEW, "View and export durable security events"),
    (PRIVACY_MANAGE, "Manage privacy requests, retention, and legal holds"),
]

ADMIN_ROLE = "admin"
DEFAULT_ROLE = "user"

SYSTEM_ROLES: dict[str, dict] = {
    ADMIN_ROLE: {
        "description": "Full access to every feature.",
        "permissions": [
            USERS_READ,
            USERS_MANAGE,
            USERS_EXPORT,
            ROLES_MANAGE,
            PRESENCE_VIEW,
            PRESENCE_KICK,
            SETTINGS_MANAGE,
            LOGS_VIEW,
            SECURITY_EVENTS_VIEW,
            PRIVACY_MANAGE,
        ],
    },
    DEFAULT_ROLE: {
        "description": "Standard access for new members.",
        "permissions": [PRESENCE_VIEW],
    },
}
