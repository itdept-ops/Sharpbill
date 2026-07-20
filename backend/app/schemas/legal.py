from datetime import date
from typing import Literal

from pydantic import BaseModel, Field


class LegalDocumentOut(BaseModel):
    key: Literal["terms", "eula", "acceptable_use", "privacy"]
    title: str
    version: str
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    url: str
    acceptance: Literal["agreement", "acknowledgement"]


class LegalManifestOut(BaseModel):
    bundle_version: str
    effective_date: date
    required_at_login: bool
    acceptance_label: str
    documents: list[LegalDocumentOut]
