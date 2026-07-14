"""Canonical built-in permission keys and system-role definitions.

These are the permissions the app's own code checks by literal key. Admins can additionally
create new permissions at runtime (stored in the DB) and attach them to roles — those need no
constant here. The initial RBAC migration seeds exactly the built-ins below, and
`tests/test_roles.py` asserts the seed stays in sync with this module.
"""

USERS_READ = "users.read"
USERS_MANAGE = "users.manage"
ROLES_MANAGE = "roles.manage"
PRESENCE_VIEW = "presence.view"
PRESENCE_KICK = "presence.kick"
SETTINGS_MANAGE = "settings.manage"
LOGS_VIEW = "logs.view"

# (key, description) — order is the seed order.
BUILTIN_PERMISSIONS: list[tuple[str, str]] = [
    (USERS_READ, "View the user directory"),
    (USERS_MANAGE, "Change user roles, activation, and approval"),
    (ROLES_MANAGE, "Create and edit roles and permissions"),
    (PRESENCE_VIEW, "See who is currently online"),
    (PRESENCE_KICK, "Force sign-out (kick) a user's active sessions"),
    (SETTINGS_MANAGE, "Manage site settings and approve sign-ups"),
    (LOGS_VIEW, "View the request activity log"),
]

ADMIN_ROLE = "admin"
DEFAULT_ROLE = "user"

SYSTEM_ROLES: dict[str, dict] = {
    ADMIN_ROLE: {
        "description": "Full access to every feature.",
        "permissions": [
            USERS_READ,
            USERS_MANAGE,
            ROLES_MANAGE,
            PRESENCE_VIEW,
            PRESENCE_KICK,
            SETTINGS_MANAGE,
            LOGS_VIEW,
        ],
    },
    DEFAULT_ROLE: {
        "description": "Standard access for new members.",
        "permissions": [PRESENCE_VIEW],
    },
}
