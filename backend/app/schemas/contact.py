from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field

from app.models import Contact

ContactStatus = Literal["lead", "active", "customer", "archived"]


class ContactOut(BaseModel):
    id: int
    first_name: str
    last_name: str | None
    full_name: str
    email: str | None
    phone: str | None
    company: str | None
    title: str | None
    status: str
    owner_id: int | None
    owner_name: str | None
    notes: str | None
    created_at: datetime
    updated_at: datetime

    @classmethod
    def from_contact(cls, c: Contact) -> "ContactOut":
        owner_name = None
        if c.owner is not None:
            owner_name = c.owner.display_name or c.owner.email
        return cls(
            id=c.id,
            first_name=c.first_name,
            last_name=c.last_name,
            full_name=c.full_name,
            email=c.email,
            phone=c.phone,
            company=c.company,
            title=c.title,
            status=c.status,
            owner_id=c.owner_id,
            owner_name=owner_name,
            notes=c.notes,
            created_at=c.created_at,
            updated_at=c.updated_at,
        )


class ContactListOut(BaseModel):
    items: list[ContactOut]
    total: int


class ContactCreate(BaseModel):
    first_name: str = Field(min_length=1, max_length=120)
    last_name: str | None = Field(default=None, max_length=120)
    email: str | None = Field(default=None, max_length=255)
    phone: str | None = Field(default=None, max_length=40)
    company: str | None = Field(default=None, max_length=160)
    title: str | None = Field(default=None, max_length=120)
    status: ContactStatus = "lead"
    owner_id: int | None = None
    notes: str | None = Field(default=None, max_length=2000)


class ContactUpdate(BaseModel):
    first_name: str | None = Field(default=None, min_length=1, max_length=120)
    last_name: str | None = Field(default=None, max_length=120)
    email: str | None = Field(default=None, max_length=255)
    phone: str | None = Field(default=None, max_length=40)
    company: str | None = Field(default=None, max_length=160)
    title: str | None = Field(default=None, max_length=120)
    status: ContactStatus | None = None
    owner_id: int | None = None
    notes: str | None = Field(default=None, max_length=2000)


class ContactStats(BaseModel):
    total: int
    by_status: list[dict]
    by_owner: list[dict]
    created: list[dict]
