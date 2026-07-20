from fastapi import APIRouter

from app.legal_acceptance import (
    CURRENT_LEGAL_BUNDLE_VERSION,
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
        acceptance_label=(
            "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge "
            "the Privacy Notice."
        ),
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
