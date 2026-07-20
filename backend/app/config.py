import hashlib
import ipaddress
import re
import uuid
from typing import Literal
from urllib.parse import urlsplit

from pydantic import Field, field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy.engine import URL

_GOOGLE_CLIENT_ID_RE = re.compile(r"[0-9]{6,32}-[A-Za-z0-9_-]{8,128}\.apps\.googleusercontent\.com")


def _validate_signing_secret(value: str, label: str) -> str:
    if len(value) < 32 or "replace-me" in value.lower():
        raise ValueError(f"{label} must be >= 32 chars and not the placeholder value")
    if len(set(value)) < 8:
        raise ValueError(f"{label} has too little entropy; use secrets.token_hex(32)")
    return value


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        case_sensitive=False,
        extra="ignore",
        hide_input_in_errors=True,
    )

    # Default to the safe mode: a missing/typo'd APP_ENV must not silently enable the dev-auth
    # gate or relax the secure-cookie invariant. Local dev sets APP_ENV=local explicitly (.env).
    app_env: Literal["local", "production"] = "production"
    # DATABASE_URL remains supported for CI/operator tooling. Compose supplies separate fields so
    # URL-reserved characters in credentials cannot corrupt connection parsing.
    database_url: str = ""
    db_host: str = ""
    db_port: int = Field(default=3306, ge=1, le=65535)
    db_name: str = ""
    db_user: str = ""
    db_password: str = ""
    db_require_tls: bool = False
    db_tls_ca_path: str = "/opt/kingfisher/certs/rds-global-bundle.pem"
    # These budgets are per API process. Capacity planning must multiply
    # (pool_size + max_overflow) by every worker and service instance.
    db_pool_size: int = Field(default=5, ge=1, le=50)
    db_max_overflow: int = Field(default=5, ge=0, le=50)
    db_pool_timeout_seconds: int = Field(default=10, ge=1, le=120)
    db_pool_recycle_seconds: int = Field(default=280, ge=30, le=3600)
    db_connect_timeout_seconds: int = Field(default=5, ge=1, le=60)
    db_read_timeout_seconds: int = Field(default=30, ge=1, le=300)
    db_write_timeout_seconds: int = Field(default=30, ge=1, le=300)

    session_jwt_secret: str
    # Tokens carry an explicit issuer/audience/type contract and a derived non-secret KID.
    # During rotation, keep old signing secrets here (comma separated) until every token signed
    # by them has passed its maximum lifetime, then remove them.
    session_jwt_previous_secrets: str = ""
    session_jwt_issuer: str = Field(default="kingfisher-crm", min_length=3, max_length=255)
    session_jwt_audience: str = Field(default="kingfisher-crm-web", min_length=3, max_length=255)
    session_ttl_hours: int = Field(default=8, ge=1, le=168)
    cookie_secure: bool = True
    max_active_sessions_per_user: int = Field(default=20, ge=1, le=100)
    session_retention_days: int = Field(default=30, ge=1, le=365)
    session_prune_batch_size: int = Field(default=500, ge=100, le=10_000)
    request_body_max_bytes: int = Field(default=1_048_576, ge=16_384, le=10_485_760)
    request_log_queue_capacity: int = Field(default=2048, ge=100, le=100_000)
    request_log_retention_days: int = Field(default=90, ge=1, le=365)
    request_log_prune_batch_size: int = Field(default=2000, ge=100, le=10_000)
    request_log_shutdown_timeout_seconds: int = Field(default=5, ge=1, le=30)
    retention_worker_interval_seconds: int = Field(default=3600, ge=60, le=86_400)
    retention_worker_max_batches_per_cycle: int = Field(default=10, ge=1, le=100)
    retention_worker_shutdown_timeout_seconds: int = Field(default=10, ge=1, le=60)
    security_event_retention_days: int = Field(default=400, ge=30, le=2555)

    # Provider verification is intentionally bounded independently of the API worker pool. A
    # burst of attacker-supplied tokens must not consume every AnyIO worker while an upstream
    # certificate endpoint is slow or unavailable.
    idp_verification_max_concurrency: int = Field(default=8, ge=1, le=64)
    idp_network_max_concurrency: int = Field(default=2, ge=1, le=8)
    idp_http_connect_timeout_seconds: float = Field(default=2.0, ge=0.1, le=10.0)
    idp_http_read_timeout_seconds: float = Field(default=3.0, ge=0.1, le=15.0)
    idp_key_cache_ttl_seconds: int = Field(default=3600, ge=60, le=86_400)
    idp_key_cache_stale_seconds: int = Field(default=86_400, ge=300, le=604_800)
    idp_key_refresh_wait_seconds: float = Field(default=6.0, ge=0.5, le=30.0)
    idp_unknown_kid_backoff_seconds: float = Field(default=10.0, ge=1.0, le=300.0)
    idp_outage_backoff_initial_seconds: float = Field(default=2.0, ge=0.5, le=30.0)
    idp_outage_backoff_max_seconds: float = Field(default=60.0, ge=1.0, le=600.0)
    idp_key_document_max_bytes: int = Field(default=1_048_576, ge=16_384, le=4_194_304)

    google_client_id: str = ""
    azure_client_id: str = ""
    azure_admin_tenant_id: str = ""
    # Microsoft object-ids (oid) permitted to bootstrap admin. MS ID tokens carry no verified
    # email signal, so admin bootstrap keys on the immutable oid, never the mutable email/UPN.
    azure_admin_object_ids: str = ""
    # Google administrator bootstrap uses immutable OIDC subject IDs in production. Email-based
    # bootstrap remains a local-development convenience only.
    google_admin_subjects: str = ""
    admin_emails: str = ""

    # Optional provisioning allowlists (empty = allow any verified account). When set, a
    # provider login is rejected unless it matches — closes "any internet account can sign up".
    allowed_email_domains: str = ""  # ALLOWED_EMAIL_DOMAINS, comma-separated (Google)
    allowed_azure_tenants: str = ""  # ALLOWED_AZURE_TENANTS, comma-separated tenant ids
    # Empty allowlists require an explicit acknowledgement before new public accounts provision.
    allow_public_signup: bool = False

    log_level: str = "INFO"

    # When the app sits behind a trusted reverse proxy, list its peer IP(s) here so the real
    # client IP is taken from X-Forwarded-For (for rate-limiting + the audit log) ONLY when the
    # immediate peer is that proxy. Empty = trust nobody's XFF; use the raw socket peer.
    trusted_proxy_ips: str = ""
    # Canonical browser origin used for CSRF comparison in production. This is independent from
    # proxy-header trust, so TLS termination cannot make same-origin HTTPS mutations look HTTP.
    public_origin: str = ""

    # Dev-only login bypass. Only honored when app_env == "local" (see is_dev_auth_enabled).
    dev_auth_enabled: bool = False
    # Separate from SESSION_JWT_SECRET so exercising the dev seam never exposes a signing key.
    dev_auth_secret: str = ""

    @field_validator("session_jwt_secret")
    @classmethod
    def _secret_must_be_strong(cls, v: str) -> str:
        return _validate_signing_secret(v, "SESSION_JWT_SECRET")

    @field_validator("session_jwt_previous_secrets")
    @classmethod
    def _previous_secrets_must_be_strong(cls, value: str) -> str:
        secrets = [secret.strip() for secret in value.split(",") if secret.strip()]
        if len(secrets) > 5:
            raise ValueError("SESSION_JWT_PREVIOUS_SECRETS supports at most five rotation keys")
        for secret in secrets:
            _validate_signing_secret(secret, "SESSION_JWT_PREVIOUS_SECRETS")
        if len(secrets) != len(set(secrets)):
            raise ValueError("SESSION_JWT_PREVIOUS_SECRETS contains a duplicate key")
        return ",".join(secrets)

    @field_validator("google_client_id")
    @classmethod
    def _google_client_id_is_trimmed(cls, value: str) -> str:
        return value.strip()

    @field_validator("azure_client_id")
    @classmethod
    def _azure_client_id_is_canonical_uuid(cls, value: str) -> str:
        value = value.strip()
        if not value:
            return ""
        try:
            return str(uuid.UUID(value))
        except ValueError as exc:
            raise ValueError("AZURE_CLIENT_ID must be a UUID") from exc

    @field_validator("trusted_proxy_ips")
    @classmethod
    def _trusted_proxies_are_explicit_networks(cls, value: str) -> str:
        normalized: list[str] = []
        for item in (part.strip() for part in value.split(",")):
            if not item:
                continue
            try:
                parsed = (
                    ipaddress.ip_network(item, strict=False)
                    if "/" in item
                    else ipaddress.ip_address(item)
                )
            except ValueError as exc:
                raise ValueError(
                    "TRUSTED_PROXY_IPS entries must be explicit IP addresses or CIDR networks"
                ) from exc
            canonical = str(parsed)
            if canonical not in normalized:
                normalized.append(canonical)
        return ",".join(normalized)

    @field_validator("allowed_azure_tenants", "azure_admin_object_ids")
    @classmethod
    def _uuid_lists_are_canonical(cls, value: str) -> str:
        normalized: list[str] = []
        for item in (part.strip() for part in value.split(",")):
            if not item:
                continue
            try:
                normalized.append(str(uuid.UUID(item)))
            except ValueError as exc:
                raise ValueError("Microsoft tenant/object identifiers must be UUIDs") from exc
        return ",".join(normalized)

    @field_validator("azure_admin_tenant_id")
    @classmethod
    def _admin_tenant_is_canonical(cls, value: str) -> str:
        if not value.strip():
            return ""
        try:
            return str(uuid.UUID(value.strip()))
        except ValueError as exc:
            raise ValueError("AZURE_ADMIN_TENANT_ID must be a UUID") from exc

    @model_validator(mode="after")
    def _production_transport_guards(self) -> "Settings":
        self._resolve_database_url()
        self._validate_transport_policy()
        self._validate_identity_provider_resilience()
        self._validate_signing_keyring()
        if self.app_env == "production":
            self._validate_production_identity_policy()
        return self

    def _validate_identity_provider_resilience(self) -> None:
        if self.idp_key_cache_stale_seconds < self.idp_key_cache_ttl_seconds:
            raise ValueError(
                "IDP_KEY_CACHE_STALE_SECONDS must be greater than or equal to "
                "IDP_KEY_CACHE_TTL_SECONDS"
            )
        if self.idp_outage_backoff_max_seconds < self.idp_outage_backoff_initial_seconds:
            raise ValueError(
                "IDP_OUTAGE_BACKOFF_MAX_SECONDS must be greater than or equal to "
                "IDP_OUTAGE_BACKOFF_INITIAL_SECONDS"
            )

    def _resolve_database_url(self) -> None:
        if self.database_url.strip():
            return
        required = (self.db_host, self.db_name, self.db_user)
        if not all(value.strip() for value in required) or not self.db_password:
            raise ValueError("Configure DATABASE_URL or DB_HOST, DB_NAME, DB_USER, and DB_PASSWORD")
        self.database_url = URL.create(
            "mysql+pymysql",
            username=self.db_user,
            password=self.db_password,
            host=self.db_host,
            port=self.db_port,
            database=self.db_name,
            query={"charset": "utf8mb4"},
        ).render_as_string(hide_password=False)

    def _validate_transport_policy(self) -> None:
        if self.app_env == "production" and not self.cookie_secure:
            raise ValueError("COOKIE_SECURE must be true when APP_ENV=production")
        if self.app_env == "production" and not self.db_require_tls:
            raise ValueError("DB_REQUIRE_TLS must be true when APP_ENV=production")
        if self.db_require_tls and not self.db_tls_ca_path.strip():
            raise ValueError("DB_TLS_CA_PATH is required when DB_REQUIRE_TLS=true")
        if self.app_env == "production":
            for item in self.trusted_proxy_ip_list:
                if ipaddress.ip_network(item).prefixlen == 0:
                    raise ValueError(
                        "TRUSTED_PROXY_IPS cannot trust a world-wide CIDR in production"
                    )

    def _validate_signing_keyring(self) -> None:
        previous = self.session_jwt_previous_secret_list
        if self.session_jwt_secret in previous:
            raise ValueError("The active SESSION_JWT_SECRET cannot also be a previous key")
        if len(self.session_jwt_keyring) != 1 + len(previous):
            raise ValueError("JWT signing-key IDs collide; replace one of the configured secrets")

    def _validate_production_identity_policy(self) -> None:
        self._validate_public_origin()
        self._validate_provider_client_ids()
        self._validate_provider_admission()
        self._validate_admin_bootstrap_config()

    def _validate_provider_client_ids(self) -> None:
        if self.google_provider_configured and (
            len(self.google_client_id) > 255
            or _GOOGLE_CLIENT_ID_RE.fullmatch(self.google_client_id) is None
        ):
            raise ValueError("GOOGLE_CLIENT_ID must be a valid Google OAuth web client identifier")

    def _validate_public_origin(self) -> None:
        try:
            parsed_origin = urlsplit(self.public_origin)
            port = parsed_origin.port
        except ValueError as exc:
            raise ValueError(
                "PUBLIC_ORIGIN must be a canonical HTTPS origin in production"
            ) from exc
        if (
            parsed_origin.scheme != "https"
            or not parsed_origin.hostname
            or parsed_origin.username is not None
            or parsed_origin.password is not None
            or parsed_origin.path not in {"", "/"}
            or parsed_origin.query
            or parsed_origin.fragment
        ):
            raise ValueError("PUBLIC_ORIGIN must be a canonical HTTPS origin in production")
        hostname = parsed_origin.hostname.encode("idna").decode("ascii").lower()
        host_literal = f"[{hostname}]" if ":" in hostname else hostname
        port_suffix = f":{port}" if port not in {None, 443} else ""
        self.public_origin = f"https://{host_literal}{port_suffix}"

    def _validate_provider_admission(self) -> None:
        if not (self.google_provider_configured or self.microsoft_provider_configured):
            raise ValueError("At least one identity provider must be configured in production")
        if self.allow_public_signup:
            raise ValueError("ALLOW_PUBLIC_SIGNUP cannot be true when APP_ENV=production")
        if self.admin_email_set:
            raise ValueError(
                "ADMIN_EMAILS is local-only; use immutable GOOGLE_ADMIN_SUBJECTS in production"
            )
        if self.google_provider_configured and not self.allowed_email_domain_set:
            raise ValueError(
                "ALLOWED_EMAIL_DOMAINS is required for Google in a production deployment"
            )
        if self.microsoft_provider_configured and not self.allowed_azure_tenant_set:
            raise ValueError(
                "ALLOWED_AZURE_TENANTS is required for Microsoft in a production deployment"
            )
        if self.microsoft_provider_configured and len(self.allowed_azure_tenant_set) != 1:
            raise ValueError(
                "Exactly one ALLOWED_AZURE_TENANTS value is required by the single-tenant model"
            )

    def _validate_admin_bootstrap_config(self) -> None:
        if self.azure_admin_object_id_set and not self.azure_admin_tenant_id.strip():
            raise ValueError(
                "AZURE_ADMIN_TENANT_ID is required with AZURE_ADMIN_OBJECT_IDS in production"
            )
        if (
            self.azure_admin_tenant_id.strip()
            and self.azure_admin_tenant_id not in self.allowed_azure_tenant_set
        ):
            raise ValueError("AZURE_ADMIN_TENANT_ID must be included in ALLOWED_AZURE_TENANTS")

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
    def google_admin_subject_set(self) -> set[str]:
        return {s.strip() for s in self.google_admin_subjects.split(",") if s.strip()}

    @property
    def session_jwt_previous_secret_list(self) -> list[str]:
        return [secret.strip() for secret in self.session_jwt_previous_secrets.split(",") if secret]

    @staticmethod
    def jwt_key_id(secret: str) -> str:
        """Derive a stable, non-secret KID without putting key material in the token header."""
        return hashlib.sha256(secret.encode("utf-8")).hexdigest()[:16]

    @property
    def session_jwt_active_kid(self) -> str:
        return self.jwt_key_id(self.session_jwt_secret)

    @property
    def session_jwt_keyring(self) -> dict[str, str]:
        secrets = [self.session_jwt_secret, *self.session_jwt_previous_secret_list]
        return {self.jwt_key_id(secret): secret for secret in secrets}

    @property
    def session_cookie_name(self) -> str:
        # The __Host- prefix is browser-enforced: Secure, Path=/, and no Domain attribute.
        return "__Host-session" if self.app_env == "production" else "session"

    @property
    def google_provider_configured(self) -> bool:
        return bool(self.google_client_id.strip())

    @property
    def microsoft_provider_configured(self) -> bool:
        return bool(self.azure_client_id.strip())

    @property
    def trusted_proxy_ip_list(self) -> list[str]:
        return [ip.strip() for ip in self.trusted_proxy_ips.split(",") if ip.strip()]

    @property
    def session_ttl_seconds(self) -> int:
        return self.session_ttl_hours * 3600

    @property
    def is_dev_auth_enabled(self) -> bool:
        """Dev login requires local mode, an explicit flag, and an independent strong secret."""
        secret = self.dev_auth_secret
        strong_secret = (
            len(secret) >= 32 and "replace-me" not in secret.lower() and len(set(secret)) >= 8
        )
        return self.app_env == "local" and self.dev_auth_enabled and strong_secret


# Required values are supplied by the environment; static analysis cannot observe BaseSettings'
# runtime environment loading.
settings = Settings()  # type: ignore[call-arg]
