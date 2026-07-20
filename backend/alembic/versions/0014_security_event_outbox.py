"""Durable security-event outbox and independent delivery state.

Revision ID: 0014
Revises: 0013
Create Date: 2026-07-20

Event facts are append-only at the application boundary. Mutable retry/lease state is isolated in
a one-to-one delivery table so a future SIEM dispatcher cannot rewrite the evidence it exports.
"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0014"
down_revision: str | None = "0013"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    op.create_table(
        "security_events",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("event_type", sa.String(length=80), nullable=False),
        sa.Column("outcome", sa.String(length=16), nullable=False),
        sa.Column("severity", sa.String(length=16), nullable=False),
        sa.Column("request_id", sa.String(length=64), nullable=True),
        sa.Column("actor_user_id", sa.Integer(), nullable=True),
        sa.Column("target_type", sa.String(length=40), nullable=True),
        sa.Column("target_id", sa.String(length=128), nullable=True),
        sa.Column("source_ip", sa.String(length=45), nullable=True),
        sa.Column("metadata", mysql.JSON(), nullable=False),
        sa.Column(
            "occurred_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6)"),
        ),
        sa.Column("retention_until", mysql.DATETIME(fsp=6), nullable=False),
        sa.CheckConstraint(
            "outcome IN ('success', 'failure', 'denied')",
            name=op.f("ck_security_events_outcome_valid"),
        ),
        sa.CheckConstraint(
            "severity IN ('info', 'warning', 'critical')",
            name=op.f("ck_security_events_severity_valid"),
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_security_events")),
        **_TABLE_ARGS,
    )
    op.create_index("ix_security_events_occurred_id", "security_events", ["occurred_at", "id"])
    op.create_index("ix_security_events_type_id", "security_events", ["event_type", "id"])
    op.create_index("ix_security_events_actor_id", "security_events", ["actor_user_id", "id"])
    op.create_index("ix_security_events_request_id", "security_events", ["request_id"])
    op.create_index("ix_security_events_retention_until", "security_events", ["retention_until"])

    op.create_table(
        "security_event_deliveries",
        sa.Column("event_id", sa.BigInteger(), nullable=False),
        sa.Column(
            "status", sa.String(length=16), nullable=False, server_default=sa.text("'pending'")
        ),
        sa.Column("attempts", sa.Integer(), nullable=False, server_default=sa.text("0")),
        sa.Column(
            "next_attempt_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6)"),
        ),
        sa.Column("lease_owner", sa.String(length=64), nullable=True),
        sa.Column("lease_expires_at", mysql.DATETIME(fsp=6), nullable=True),
        sa.Column("last_attempt_at", mysql.DATETIME(fsp=6), nullable=True),
        sa.Column("delivered_at", mysql.DATETIME(fsp=6), nullable=True),
        sa.Column("last_error", sa.String(length=255), nullable=True),
        sa.Column(
            "updated_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        ),
        sa.CheckConstraint(
            "status IN ('pending', 'leased', 'retry', 'delivered', 'dead_letter')",
            name=op.f("ck_security_event_deliveries_status_valid"),
        ),
        sa.CheckConstraint(
            "attempts >= 0", name=op.f("ck_security_event_deliveries_attempts_nonnegative")
        ),
        sa.ForeignKeyConstraint(
            ["event_id"],
            ["security_events.id"],
            name=op.f("fk_security_event_deliveries_event_id_security_events"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("event_id", name=op.f("pk_security_event_deliveries")),
        **_TABLE_ARGS,
    )
    op.create_index(
        "ix_security_event_deliveries_dispatch",
        "security_event_deliveries",
        ["status", "next_attempt_at", "event_id"],
    )
    op.create_index(
        "ix_security_event_deliveries_lease",
        "security_event_deliveries",
        ["lease_expires_at"],
    )
    # The composite added in 0013 has the same leading column and also serves newest-first reads.
    op.drop_index("ix_request_logs_user_id", table_name="request_logs")


def _assert_no_audit_evidence_before_downgrade() -> None:
    # MySQL DDL auto-commits. This data-loss guard must be the first downgrade statement so a
    # refusal cannot leave the schema half-dropped. Operators must export and explicitly clear
    # retained evidence under their retention policy before removing the outbox schema.
    existing = op.get_bind().execute(sa.text("SELECT id FROM security_events LIMIT 1")).first()
    if existing is not None:
        raise RuntimeError(
            "0014 downgrade refused: security_events contains retained audit evidence; "
            "export and explicitly clear it under the approved retention policy first"
        )


def downgrade() -> None:
    _assert_no_audit_evidence_before_downgrade()
    # Restore the exact 0013 index contract before removing 0014 objects. The retained-evidence
    # refusal above remains the first DB-dependent statement and precedes every MySQL DDL action.
    op.create_index("ix_request_logs_user_id", "request_logs", ["user_id"])
    op.drop_table("security_event_deliveries")
    op.drop_table("security_events")
