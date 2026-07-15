from typing import Literal

from pydantic import field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", case_sensitive=False, extra="ignore")

    app_env: Literal["local", "production"] = "local"
    database_url: str
    db_require_tls: bool = False

    session_jwt_secret: str
    session_ttl_hours: int = 8
    cookie_secure: bool = True

    google_client_id: str = ""
    azure_client_id: str = ""
    azure_admin_tenant_id: str = ""
    # Microsoft object-ids (oid) permitted to bootstrap admin. MS ID tokens carry no verified
    # email signal, so admin bootstrap keys on the immutable oid, never the mutable email/UPN.
    azure_admin_object_ids: str = ""
    admin_emails: str = ""

    # Optional provisioning allowlists (empty = allow any verified account). When set, a
    # provider login is rejected unless it matches — closes "any internet account can sign up".
    allowed_email_domains: str = ""  # ALLOWED_EMAIL_DOMAINS, comma-separated (Google)
    allowed_azure_tenants: str = ""  # ALLOWED_AZURE_TENANTS, comma-separated tenant ids

    log_level: str = "INFO"

    # When the app sits behind a trusted reverse proxy, list its peer IP(s) here so the real
    # client IP is taken from X-Forwarded-For (for rate-limiting + the audit log) ONLY when the
    # immediate peer is that proxy. Empty = trust nobody's XFF; use the raw socket peer.
    trusted_proxy_ips: str = ""

    # Dev-only login bypass. Only honored when app_env == "local" (see is_dev_auth_enabled).
    dev_auth_enabled: bool = False

    @field_validator("session_jwt_secret")
    @classmethod
    def _secret_must_be_strong(cls, v: str) -> str:
        if len(v) < 32 or "replace-me" in v:
            raise ValueError("SESSION_JWT_SECRET must be >= 32 chars and not the placeholder value")
        return v

    @model_validator(mode="after")
    def _secure_cookie_in_production(self) -> "Settings":
        if self.app_env == "production" and not self.cookie_secure:
            raise ValueError("COOKIE_SECURE must be true when APP_ENV=production")
        return self

    @property
    def admin_email_set(self) -> set[str]:
        return {e.strip().lower() for e in self.admin_emails.split(",") if e.strip()}

    @property
    def allowed_email_domain_set(self) -> set[str]:
        return {d.strip().lower() for d in self.allowed_email_domains.split(",") if d.strip()}

    @property
    def allowed_azure_tenant_set(self) -> set[str]:
        return {t.strip() for t in self.allowed_azure_tenants.split(",") if t.strip()}

    @property
    def azure_admin_object_id_set(self) -> set[str]:
        return {o.strip() for o in self.azure_admin_object_ids.split(",") if o.strip()}

    @property
    def trusted_proxy_ip_list(self) -> list[str]:
        return [ip.strip() for ip in self.trusted_proxy_ips.split(",") if ip.strip()]

    @property
    def session_ttl_seconds(self) -> int:
        return self.session_ttl_hours * 3600

    @property
    def is_dev_auth_enabled(self) -> bool:
        """Dev login is only ever available in a local environment."""
        return self.app_env == "local" and self.dev_auth_enabled


settings = Settings()
