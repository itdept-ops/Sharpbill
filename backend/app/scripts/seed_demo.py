"""Seed realistic demo data so the dashboard, directory, and contacts look full.

Run locally:  docker compose exec api python -m app.scripts.seed_demo
Idempotent for users (keyed on email); adds a fresh batch of contacts each run.
"""

import random
from datetime import UTC, datetime, timedelta

from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal
from app.models import Contact, Permission, Role, User, UserIdentity

LOCATIONS = ["Remote", "Austin TX", "Denver CO", "NYC", "Seattle WA", "Miami FL", "Chicago IL"]
DEPARTMENTS = ["Sales", "Success", "Operations", "Support", "Marketing", "Finance"]
TITLES = [
    "Account Executive",
    "CSM",
    "Ops Lead",
    "Support Rep",
    "Coordinator",
    "Analyst",
    "Manager",
]
COMPANIES = [
    "Acme Care",
    "Northwind Health",
    "Globex",
    "Initech",
    "Umbrella Home",
    "Wayne Medical",
    "Stark Clinics",
    "Soylent Foods",
    "Hooli",
    "Vandelay",
]
FIRST = [
    "Ava",
    "Liam",
    "Mia",
    "Noah",
    "Zoe",
    "Ethan",
    "Aria",
    "Kai",
    "Nora",
    "Leo",
    "Ivy",
    "Max",
    "Ruby",
    "Owen",
    "Elena",
    "Sam",
    "Priya",
    "Diego",
    "Hana",
    "Marcus",
]
LAST = [
    "Reyes",
    "Chen",
    "Patel",
    "Kim",
    "Nguyen",
    "Silva",
    "Okafor",
    "Rossi",
    "Haddad",
    "Brooks",
    "Novak",
    "Flores",
    "Bauer",
    "Cohen",
    "Mbeki",
    "Ito",
    "Costa",
    "Weber",
]

# (first, last, department, title, role_name, status)
PEOPLE = [
    ("Maria", "Gonzalez", "Sales", "Account Executive", "Sales", "active"),
    ("David", "Okafor", "Sales", "Account Executive", "Sales", "active"),
    ("Priya", "Patel", "Success", "CSM", "Sales", "active"),
    ("Jordan", "Lee", "Operations", "Ops Lead", "Manager", "active"),
    ("Sam", "Rivera", "Support", "Support Rep", "user", "active"),
    ("Elena", "Novak", "Marketing", "Coordinator", "user", "active"),
    ("Marcus", "Brooks", "Sales", "Account Executive", "Sales", "pending"),
    ("Hana", "Ito", "Success", "CSM", "Sales", "pending"),
    ("Diego", "Costa", "Support", "Support Rep", "user", "disabled"),
    ("Nora", "Haddad", "Finance", "Analyst", "Manager", "active"),
    ("Owen", "Bauer", "Operations", "Coordinator", "user", "active"),
    ("Ruby", "Flores", "Sales", "Account Executive", "Sales", "active"),
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
        sales = _ensure_role(
            db,
            "Sales",
            "Sell and manage contacts",
            ["contacts.read", "contacts.write", "presence.view"],
        )
        manager = _ensure_role(
            db,
            "Manager",
            "Team lead: read users + full contacts",
            ["users.read", "contacts.read", "contacts.write", "presence.view"],
        )
        by_name = {
            "Sales": sales,
            "Manager": manager,
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
            )
            db.add(u)
            db.flush()
            db.add(
                UserIdentity(user=u, provider="dev", provider_subject=email, provider_email=email)
            )
            new_users += 1
        db.commit()

        owners = list(db.scalars(select(User)))
        n_contacts = 45
        for i in range(n_contacts):
            first, last = rnd.choice(FIRST), rnd.choice(LAST)
            created = now - timedelta(days=rnd.randint(0, 13), hours=rnd.randint(0, 23))
            db.add(
                Contact(
                    first_name=first,
                    last_name=last,
                    email=f"{first}.{last}{i}@{rnd.choice(COMPANIES).split()[0].lower()}.io",
                    phone=f"+1-555-{rnd.randint(1000, 9999)}",
                    company=rnd.choice(COMPANIES),
                    title=rnd.choice(TITLES),
                    status=rnd.choices(
                        ["lead", "active", "customer", "archived"], weights=[5, 3, 2, 1]
                    )[0],
                    owner_id=rnd.choice(owners).id,
                    notes="Added by the demo seed.",
                    created_at=created,
                    updated_at=created,
                )
            )
        db.commit()
        print(
            f"Seeded {new_users} new users and {n_contacts} contacts. Total users: {len(owners)}."
        )


if __name__ == "__main__":
    run()
