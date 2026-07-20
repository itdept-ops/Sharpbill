"""Versioned legal manifest, login precondition, and immutable acceptance evidence."""

from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta
from typing import Literal

from fastapi import Request
from sqlalchemy.orm import Session

from app.config import settings
from app.errors import ApiError
from app.models import LegalAcceptance
from app.security_events import add_security_event

CURRENT_LEGAL_BUNDLE_VERSION = "2026-07-20-v1"
LEGAL_BUNDLE_EFFECTIVE_DATE = date(2026, 7, 20)
# Digests bind the compact, NFC-normalized ``kingfisher-legal-document/v1`` canonical JSON
# artifact used by the official web build. The canonicalization contract is tested in frontend;
# these server constants are the acceptance authority and immutable evidence snapshot.


@dataclass(frozen=True)
class LegalDocument:
    key: Literal["terms", "eula", "acceptable_use", "privacy"]
    title: str
    version: str
    sha256: str
    url: str
    acceptance: Literal["agreement", "acknowledgement"]


LEGAL_DOCUMENTS = (
    LegalDocument(
        key="terms",
        title="Terms of Service",
        version="2026-07-20-v1",
        sha256="2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e",
        url="/legal/terms-of-service.html",
        acceptance="agreement",
    ),
    LegalDocument(
        key="eula",
        title="End User License Agreement",
        version="2026-07-20-v1",
        sha256="16bd045a449990e3f7325f0d67d81d4fee54f679ec53164835f0c19725e25638",
        url="/legal/eula.html",
        acceptance="agreement",
    ),
    LegalDocument(
        key="acceptable_use",
        title="Acceptable Use Policy",
        version="2026-07-20-v1",
        sha256="d4391a0abe57885964606521039a4cca0151f8e11d95c628efc51b603eefdb0d",
        url="/legal/acceptable-use-policy.html",
        acceptance="agreement",
    ),
    LegalDocument(
        key="privacy",
        title="Privacy Notice",
        version="2026-07-20-v1",
        sha256="fb96f77cc9846282c9555105994d0dc9b400c2a6eaf35e15b390b5a3c5db2d3d",
        url="/legal/privacy-notice.html",
        acceptance="acknowledgement",
    ),
)


def require_current_legal_acceptance(*, accepted: bool, bundle_version: str) -> None:
    """Fail deterministically before provider work or session issuance."""
    if not accepted:
        raise ApiError(
            428,
            "LEGAL_ACCEPTANCE_REQUIRED",
            "You must agree to the current legal terms and acknowledge the Privacy Notice",
        )
    if bundle_version != CURRENT_LEGAL_BUNDLE_VERSION:
        raise ApiError(
            409,
            "LEGAL_BUNDLE_STALE",
            "The legal terms changed; review and accept the current versions before signing in",
        )


def _document_version(key: str) -> str:
    return next(document.version for document in LEGAL_DOCUMENTS if document.key == key)


def _document_sha256(key: str) -> str:
    return next(document.sha256 for document in LEGAL_DOCUMENTS if document.key == key)


def add_legal_acceptance(
    db: Session,
    *,
    user_id: int,
    request: Request,
    accepted_at: datetime | None = None,
) -> LegalAcceptance:
    """Stage the current bundle snapshot and audit event in the session transaction."""
    current = accepted_at or datetime.now(UTC).replace(tzinfo=None)
    user_agent = request.headers.get("user-agent")
    source_ip = request.client.host if request.client else None
    request_id = getattr(request.state, "request_id", None)
    acceptance = LegalAcceptance(
        user_id=user_id,
        bundle_version=CURRENT_LEGAL_BUNDLE_VERSION,
        terms_version=_document_version("terms"),
        eula_version=_document_version("eula"),
        acceptable_use_version=_document_version("acceptable_use"),
        privacy_version=_document_version("privacy"),
        terms_sha256=_document_sha256("terms"),
        eula_sha256=_document_sha256("eula"),
        acceptable_use_sha256=_document_sha256("acceptable_use"),
        privacy_sha256=_document_sha256("privacy"),
        accepted_at=current,
        retention_until=current + timedelta(days=settings.legal_acceptance_retention_days),
        source_ip=(str(source_ip)[:45] if source_ip else None),
        user_agent=(user_agent[:400] if user_agent else None),
        request_id=(str(request_id)[:64] if request_id else None),
    )
    db.add(acceptance)
    add_security_event(
        db,
        event_type="legal.accepted",
        outcome="success",
        request=request,
        actor_user_id=user_id,
        target_type="legal_bundle",
        target_id=CURRENT_LEGAL_BUNDLE_VERSION,
        metadata={
            "bundle_version": CURRENT_LEGAL_BUNDLE_VERSION,
            "terms_version": acceptance.terms_version,
            "eula_version": acceptance.eula_version,
            "acceptable_use_version": acceptance.acceptable_use_version,
            "privacy_version": acceptance.privacy_version,
            "terms_sha256": acceptance.terms_sha256,
            "eula_sha256": acceptance.eula_sha256,
            "acceptable_use_sha256": acceptance.acceptable_use_sha256,
            "privacy_sha256": acceptance.privacy_sha256,
        },
    )
    return acceptance
