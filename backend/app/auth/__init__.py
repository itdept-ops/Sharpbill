from dataclasses import dataclass
from typing import Literal


class ProviderTokenError(Exception):
    """Raised when a provider ID token fails verification for any reason."""


@dataclass(frozen=True)
class VerifiedIdentity:
    provider: Literal["google", "microsoft"]
    subject: str  # Google 'sub' / Microsoft 'oid'
    email: str  # lowercased
    display_name: str
    tenant_id: str | None = None  # Microsoft 'tid'; None for Google
