from datetime import UTC, datetime, timedelta

from fastapi import APIRouter, Depends, Query, Response
from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.errors import ApiError
from app.models import Contact, User
from app.permissions import CONTACTS_READ, CONTACTS_WRITE
from app.schemas.contact import (
    ContactCreate,
    ContactListOut,
    ContactOut,
    ContactStats,
    ContactUpdate,
)

router = APIRouter()

_STATUSES = ("lead", "active", "customer", "archived")


def _get(db: Session, contact_id: int) -> Contact:
    c = db.get(Contact, contact_id)
    if c is None:
        raise ApiError(404, "NOT_FOUND", "Contact not found")
    return c


def _validate_owner(db: Session, owner_id: int | None) -> None:
    if owner_id is not None and db.get(User, owner_id) is None:
        raise ApiError(400, "UNKNOWN_OWNER", "No such owner")


@router.get("", response_model=ContactListOut)
def list_contacts(
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(CONTACTS_READ)),
    search: str | None = Query(None, max_length=100),
    status: str | None = Query(None, pattern="^(lead|active|customer|archived)$"),
    owner_id: int | None = Query(None),
    mine: bool | None = Query(None),
    limit: int = Query(100, ge=1, le=200),
    offset: int = Query(0, ge=0),
) -> ContactListOut:
    stmt = select(Contact)
    if search:
        like = f"%{search.lower()}%"
        stmt = stmt.where(
            or_(
                func.lower(Contact.first_name).like(like),
                func.lower(func.coalesce(Contact.last_name, "")).like(like),
                func.lower(func.coalesce(Contact.email, "")).like(like),
                func.lower(func.coalesce(Contact.company, "")).like(like),
            )
        )
    if status:
        stmt = stmt.where(Contact.status == status)
    if owner_id is not None:
        stmt = stmt.where(Contact.owner_id == owner_id)
    if mine:
        stmt = stmt.where(Contact.owner_id == actor.id)
    total = db.scalar(select(func.count()).select_from(stmt.subquery())) or 0
    contacts = list(
        db.scalars(
            stmt.order_by(Contact.created_at.desc(), Contact.id.desc()).limit(limit).offset(offset)
        )
    )
    return ContactListOut(items=[ContactOut.from_contact(c) for c in contacts], total=total)


@router.get("/stats", response_model=ContactStats)
def contact_stats(
    db: Session = Depends(get_db), _: User = Depends(require_permission(CONTACTS_READ))
) -> ContactStats:
    total = db.scalar(select(func.count()).select_from(Contact)) or 0
    status_rows = dict(
        db.execute(select(Contact.status, func.count(Contact.id)).group_by(Contact.status)).all()
    )
    by_status = [{"status": s, "count": int(status_rows.get(s, 0))} for s in _STATUSES]

    owner_rows = db.execute(
        select(User.display_name, User.email, func.count(Contact.id))
        .select_from(Contact)
        .join(User, Contact.owner_id == User.id)
        .group_by(User.id)
        .order_by(func.count(Contact.id).desc())
        .limit(6)
    ).all()
    by_owner = [{"owner": (r[0] or r[1]), "count": int(r[2])} for r in owner_rows]

    since = (datetime.now(UTC) - timedelta(days=13)).replace(
        hour=0, minute=0, second=0, microsecond=0, tzinfo=None
    )
    rows = db.execute(
        select(func.date(Contact.created_at), func.count(Contact.id))
        .where(Contact.created_at >= since)
        .group_by(func.date(Contact.created_at))
    ).all()
    by_date = {str(r[0]): int(r[1]) for r in rows}
    created = [
        {
            "date": (since + timedelta(days=i)).date().isoformat(),
            "count": by_date.get((since + timedelta(days=i)).date().isoformat(), 0),
        }
        for i in range(14)
    ]
    return ContactStats(total=total, by_status=by_status, by_owner=by_owner, created=created)


@router.get("/{contact_id}", response_model=ContactOut)
def get_contact(
    contact_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(CONTACTS_READ)),
) -> ContactOut:
    return ContactOut.from_contact(_get(db, contact_id))


@router.post("", response_model=ContactOut, status_code=201)
def create_contact(
    body: ContactCreate,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(CONTACTS_WRITE)),
) -> ContactOut:
    _validate_owner(db, body.owner_id)
    data = body.model_dump()
    if data.get("owner_id") is None:
        data["owner_id"] = actor.id  # default ownership to the creator
    contact = Contact(**data)
    db.add(contact)
    db.commit()
    db.refresh(contact)
    return ContactOut.from_contact(contact)


@router.patch("/{contact_id}", response_model=ContactOut)
def update_contact(
    contact_id: int,
    body: ContactUpdate,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(CONTACTS_WRITE)),
) -> ContactOut:
    contact = _get(db, contact_id)
    data = body.model_dump(exclude_unset=True)
    if "owner_id" in data:
        _validate_owner(db, data["owner_id"])
    for field, value in data.items():
        setattr(contact, field, value)
    db.commit()
    db.refresh(contact)
    return ContactOut.from_contact(contact)


@router.delete("/{contact_id}", status_code=204)
def delete_contact(
    contact_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(CONTACTS_WRITE)),
) -> Response:
    db.delete(_get(db, contact_id))
    db.commit()
    return Response(status_code=204)
