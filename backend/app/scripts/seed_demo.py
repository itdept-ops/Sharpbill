"""Seed realistic demo data so the dashboard and user directory look full.

Run locally:  docker compose exec api python -m app.scripts.seed_demo
Idempotent for users (keyed on email). Local environment only.
"""

import random
from datetime import UTC, datetime, timedelta

from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal
from app.models import Permission, Role, User, UserIdentity

LOCATIONS = ["Remote", "Austin TX", "Denver CO", "NYC", "Seattle WA", "Miami FL", "Chicago IL"]

# (first, last, department, title, role_name, status)
PEOPLE = [
    ("Maria", "Gonzalez", "Operations", "Ops Lead", "Manager", "active"),
    ("David", "Okafor", "Support", "Support Rep", "user", "active"),
    ("Priya", "Patel", "Success", "CSM", "user", "active"),
    ("Jordan", "Lee", "Security", "Security Analyst", "Auditor", "active"),
    ("Sam", "Rivera", "Support", "Support Rep", "user", "active"),
    ("Elena", "Novak", "Marketing", "Coordinator", "user", "active"),
    ("Marcus", "Brooks", "Operations", "Coordinator", "user", "pending"),
    ("Hana", "Ito", "Success", "CSM", "user", "pending"),
    ("Diego", "Costa", "Support", "Support Rep", "user", "disabled"),
    ("Nora", "Haddad", "Finance", "Analyst", "Manager", "active"),
    ("Owen", "Bauer", "Operations", "Coordinator", "user", "active"),
    ("Ruby", "Flores", "Security", "Security Analyst", "Auditor", "active"),
]


def _ensure_role(db, name, desc, keys) -> Role:
    role = db.scalar(select(Role).where(Role.name == name))
    if role:
        return role
    perms = list(db.scalars(select(Permission).where(Permission.key.in_(keys))))
    role = Role(name=name, description=desc, is_system=False, permissions=perms)
    db.add(role)
    db.flush()
    return role


def run() -> None:
    if settings.app_env != "local":
        print("Refusing to seed outside a local environment (APP_ENV != local).")
        return

    rnd = random.Random(42)
    now = datetime.now(UTC).replace(tzinfo=None)

    with SessionLocal() as db:
        # Two custom demo roles showcasing distinct permission sets.
        manager = _ensure_role(
            db,
            "Manager",
            "Team lead: reads the directory and can kick sessions",
            ["users.read", "presence.view", "presence.kick"],
        )
        auditor = _ensure_role(
            db,
            "Auditor",
            "Read-only oversight: directory + request log",
            ["users.read", "logs.view", "presence.view"],
        )
        by_name = {
            "Manager": manager,
            "Auditor": auditor,
            "user": db.scalar(select(Role).where(Role.name == "user")),
            "admin": db.scalar(select(Role).where(Role.name == "admin")),
        }

        new_users = 0
        for first, last, dept, title, role_name, status in PEOPLE:
            email = f"{first}.{last}@example.com".lower()
            if db.scalar(select(User).where(User.email == email)):
                continue
            created = now - timedelta(days=rnd.randint(0, 13), hours=rnd.randint(0, 23))
            u = User(
                email=email,
                display_name=f"{first} {last}",
                title=title,
                department=dept,
                location=rnd.choice(LOCATIONS),
                timezone="UTC",
                bio=f"{title} on the {dept} team.",
                role=by_name[role_name],
                is_active=(status != "disabled"),
                is_approved=(status != "pending"),
                created_at=created,
                last_login_at=created,
                # Stamp active users as recently seen so the dashboard's "online" metric and the
                # live-presence roster aren't flat zero right after seeding.
                last_seen_at=(now if status == "active" else None),
            )
            db.add(u)
            db.flush()
            db.add(
                UserIdentity(user=u, provider="dev", provider_subject=email, provider_email=email)
            )
            new_users += 1
        db.commit()
        total = len(list(db.scalars(select(User))))
        print(
            f"Seeded {new_users} new users. Total users: {total} (custom roles: Manager, Auditor)."
        )


if __name__ == "__main__":
    run()
