from sqlalchemy import select

from app.config import settings
from app.models import User
from app.scripts.seed_demo import run


def test_demo_seed_respects_deactivation_lifecycle_constraint(db, monkeypatch):
    monkeypatch.setattr(settings, "app_env", "local")

    run()

    db.rollback()
    disabled = db.scalar(select(User).where(User.email == "diego.costa@example.com"))
    assert disabled is not None
    assert not disabled.is_active
    assert disabled.deactivated_at == disabled.created_at
