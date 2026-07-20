from fastapi import APIRouter

from app.config import settings
from app.legal_acceptance import (
    CURRENT_LEGAL_BUNDLE_VERSION,
    LEGAL_ACCEPTANCE_LABEL,
    LEGAL_BUNDLE_EFFECTIVE_DATE,
    LEGAL_DOCUMENTS,
)
from app.schemas.legal import LegalDocumentOut, LegalManifestOut

router = APIRouter()


@router.get("/manifest", response_model=LegalManifestOut)
def legal_manifest() -> LegalManifestOut:
    """Return the only legal bundle the server will accept during login."""
    return LegalManifestOut(
        bundle_version=CURRENT_LEGAL_BUNDLE_VERSION,
        effective_date=LEGAL_BUNDLE_EFFECTIVE_DATE,
        required_at_login=True,
        acceptance_label=LEGAL_ACCEPTANCE_LABEL,
        precise_location_retention_hours=settings.precise_location_retention_hours,
        legal_acceptance_retention_days=settings.legal_acceptance_retention_days,
        documents=[
            LegalDocumentOut(
                key=document.key,
                title=document.title,
                version=document.version,
                sha256=document.sha256,
                url=document.url,
                acceptance=document.acceptance,
            )
            for document in LEGAL_DOCUMENTS
        ],
    )
