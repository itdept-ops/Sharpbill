from datetime import datetime

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


class RetentionPolicyOut(BaseModel):
    precise_location_hours: int
    pending_accounts_days: int
    sessions_after_expiry_or_revocation_days: int
    request_activity_days: int
    erasure_grace_days: int
    disabled_accounts_days: int
    security_events_days: int
    generated_exports_retained: bool = False


class PrivacyStatusOut(BaseModel):
    policy: RetentionPolicyOut
    retention_hold: bool
    erasure_requested_at: datetime | None
    erasure_due_at: datetime | None


class PrivacyAdminStatusOut(BaseModel):
    policy: RetentionPolicyOut
    retention_hold: bool
    retention_hold_reference: str | None


class RetentionHoldUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")

    enabled: bool
    # This is deliberately a terse external case/ticket key, not free-form evidence or PII.
    reference: str | None = Field(
        default=None,
        min_length=3,
        max_length=255,
        pattern=r"^[A-Za-z0-9][A-Za-z0-9._:/-]{2,254}$",
    )

    @field_validator("reference", mode="before")
    @classmethod
    def _reference_is_trimmed(cls, value: object) -> object:
        return value.strip() if isinstance(value, str) else value

    @model_validator(mode="after")
    def _reference_matches_state(self) -> "RetentionHoldUpdate":
        if self.enabled and self.reference is None:
            raise ValueError("reference is required when enabling a retention hold")
        if not self.enabled and self.reference is not None:
            raise ValueError("reference must be omitted when releasing a retention hold")
        return self
