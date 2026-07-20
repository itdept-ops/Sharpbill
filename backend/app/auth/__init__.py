from dataclasses import dataclass
from typing import Literal


class ProviderTokenError(Exception):
    """Raised when a provider ID token fails verification for any reason."""


class ProviderUnavailableError(Exception):
    """Raised when an identity provider cannot be reached or its keys cannot be fetched."""


@dataclass(frozen=True)
class VerifiedIdentity:
    provider: Literal["google", "microsoft"]
    subject: str  # Google 'sub' / Microsoft 'oid'
    email: str  # lowercased
    display_name: str
    tenant_id: str | None = None  # Microsoft 'tid'; None for Google
    hosted_domain: str | None = None  # Google signed 'hd'; None for Microsoft/consumer Google
