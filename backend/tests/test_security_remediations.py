"""Regression coverage for the first enterprise security-remediation batch."""

from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient as RawTestClient
from pydantic import ValidationError
from sqlalchemy import func, select

from app.auth import ProviderUnavailableError
from app.auth.jwt import COOKIE_NAME, create_session_token
from app.config import settings
from app.main import app
from app.models import RequestLog, User, UserSession
from app.request_logging import flush_request_logging
from app.schemas.auth import LocationUpdate
from tests.client import DEV_AUTH_HEADERS, TestClient


def _login(client: TestClient, email: str, role: str = "user", **extra) -> dict:
    body = {"email": email, "role": role, **extra}
    response = client.post("/api/auth/dev", json=body)
    assert response.status_code == 200, response.text
    return response.json()


def test_dev_auth_requires_explicit_secret_and_does_not_mutate_on_failure(db):
    raw = RawTestClient(app)
    before = db.scalar(select(func.count()).select_from(User))

    missing = raw.post("/api/auth/dev", json={"email": "intruder@example.com", "role": "admin"})
    wrong = raw.post(
        "/api/auth/dev",
        json={"email": "intruder@example.com", "role": "admin"},
        headers={"X-Dev-Auth-Secret": "wrong-secret"},
    )

    assert missing.status_code == wrong.status_code == 404
    assert "session" not in missing.cookies and "session" not in wrong.cookies
    db.expire_all()
    assert db.scalar(select(func.count()).select_from(User)) == before


def test_dev_auth_cannot_rewrite_an_existing_user_role_or_profile():
    original = TestClient(app)
    first = _login(
        original,
        "existing@example.com",
        role="admin",
        display_name="Original Name",
    )

    second = TestClient(app)
    again = _login(
        second,
        "existing@example.com",
        role="user",
        display_name="Caller Controlled",
    )

    assert first["role"] == again["role"] == "admin"
    assert first["display_name"] == again["display_name"] == "Original Name"


def test_cross_origin_and_same_site_mutations_are_rejected_without_side_effects(monkeypatch):
    from app import main as main_module

    boundary_codes = []
    monkeypatch.setattr(
        main_module,
        "_log_boundary_rejection",
        lambda _scope, *, status_code, code: boundary_codes.append((status_code, code)),
    )
    client = TestClient(app)
    _login(client, "csrf-admin@example.com", role="admin")

    for headers in (
        {"Origin": "https://sibling.example.com"},
        {"Origin": "null"},
        {"Sec-Fetch-Site": "same-site"},
        {"Referer": "https://evil.example.net/form"},
    ):
        response = client.post("/api/auth/logout", headers=headers)
        assert response.status_code == 403
        assert response.json()["detail"]["code"] == "CSRF_REJECTED"
        assert client.get("/api/auth/me").status_code == 200

    assert boundary_codes == [(403, "CSRF_REJECTED")] * 4

    allowed = client.post(
        "/api/auth/logout",
        headers={"Origin": "http://testserver", "Sec-Fetch-Site": "same-origin"},
    )
    assert allowed.status_code == 204
    assert client.get("/api/auth/me").status_code == 401


def test_production_csrf_uses_canonical_https_origin_across_tls_termination(monkeypatch):
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "public_origin", "https://crm.example.com")

    allowed = TestClient(app).post(
        "/api/auth/nonce",
        headers={"Origin": "https://crm.example.com", "Sec-Fetch-Site": "same-origin"},
    )
    assert allowed.status_code == 201

    rejected = TestClient(app).post(
        "/api/auth/nonce",
        headers={"Origin": "http://crm.example.com", "Sec-Fetch-Site": "same-origin"},
    )
    assert rejected.status_code == 403
    assert rejected.json()["detail"]["code"] == "CSRF_REJECTED"


def test_production_session_cookie_requires_browser_provenance_for_mutations(monkeypatch):
    client = TestClient(app)
    _login(client, "csrf-cookie-admin@example.com", role="admin")
    session_token = client.cookies.get(COOKIE_NAME)
    assert session_token is not None

    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "public_origin", "https://crm.example.com")
    # COOKIE_NAME was fixed when the local test app imported. Add the real hosted cookie name as
    # well so the middleware exercises the production browser boundary while auth remains valid.
    client.cookies.set("__Host-session", session_token)

    rejected = client.post("/api/auth/logout")
    assert rejected.status_code == 403
    assert rejected.json()["detail"]["code"] == "CSRF_REJECTED"
    assert client.get("/api/auth/me").status_code == 200

    # Cookie-free automation plus the nonce/login bootstrap surface remains compatible.
    assert TestClient(app).post("/api/auth/nonce").status_code == 201
    assert (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "cookie-free-login@example.com", "role": "user"})
        .status_code
        == 200
    )
    assert (
        client.post(
            "/api/presence/heartbeat", headers={"Sec-Fetch-Site": "same-origin"}
        ).status_code
        == 200
    )

    allowed = client.post(
        "/api/auth/logout",
        headers={"Origin": "https://crm.example.com", "Sec-Fetch-Site": "same-origin"},
    )
    assert allowed.status_code == 204


def test_trusted_forwarded_https_scheme_is_honored_in_local_proxy_topology():
    response = TestClient(app).post(
        "/api/auth/nonce",
        headers={
            "Host": "crm.example.com",
            "Origin": "https://crm.example.com",
            "X-Forwarded-Proto": "https",
            "Sec-Fetch-Site": "same-origin",
        },
    )
    assert response.status_code == 201


def test_non_admin_with_all_permissions_still_cannot_assign_admin_meta_role():
    admin = TestClient(app)
    _login(admin, "meta-admin@example.com", role="admin")
    permission_keys = [p["key"] for p in admin.get("/api/permissions").json()]
    admin.post(
        "/api/roles",
        json={"name": "AllButAdmin", "permission_keys": permission_keys},
    )
    admin_role_id = next(r["id"] for r in admin.get("/api/roles").json() if r["name"] == "admin")

    delegate = TestClient(app)
    _login(delegate, "meta-delegate@example.com", role="AllButAdmin")
    target = TestClient(app)
    target_user = _login(target, "meta-target@example.com")

    single = delegate.patch(f"/api/users/{target_user['id']}/role", json={"role_id": admin_role_id})
    bulk = delegate.post(
        "/api/users/bulk",
        json={"ids": [target_user["id"]], "action": "assign_role", "role_id": admin_role_id},
    )
    assert single.status_code == bulk.status_code == 403
    assert target.get("/api/auth/me").json()["role"] == "user"


def test_admin_role_cannot_be_the_signup_default_even_when_actor_is_admin():
    admin = TestClient(app)
    _login(admin, "default-admin@example.com", role="admin")
    admin_role_id = next(r["id"] for r in admin.get("/api/roles").json() if r["name"] == "admin")

    response = admin.put("/api/admin/settings", json={"default_role_id": admin_role_id})

    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "PROTECTED_DEFAULT_ROLE"


def test_delegates_cannot_revoke_admin_sessions_or_direct_grants():
    admin = TestClient(app)
    admin_user = _login(admin, "guarded-admin@example.com", role="admin")
    admin.post(
        "/api/roles",
        json={
            "name": "GuardDelegate",
            "permission_keys": ["users.read", "users.manage", "presence.kick"],
        },
    )
    delegate = TestClient(app)
    _login(delegate, "guard-delegate@example.com", role="GuardDelegate")

    session_id = delegate.get(f"/api/users/{admin_user['id']}/sessions").json()[0]["id"]
    revoke = delegate.delete(f"/api/users/{admin_user['id']}/sessions/{session_id}")
    direct = delegate.put(
        f"/api/users/{admin_user['id']}/permissions", json={"permission_keys": []}
    )

    assert revoke.status_code == direct.status_code == 403
    assert admin.get("/api/auth/me").status_code == 200


def test_session_jti_must_belong_to_jwt_subject(db):
    first = TestClient(app)
    first_user = _login(first, "subject-a@example.com")
    second = TestClient(app)
    second_user = _login(second, "subject-b@example.com")
    second_session = db.scalar(select(UserSession).where(UserSession.user_id == second_user["id"]))
    assert second_session is not None

    forged = TestClient(app)
    forged.cookies.set(COOKIE_NAME, create_session_token(first_user["id"], second_session.jti))
    assert forged.get("/api/auth/me").status_code == 401
    assert second.get("/api/auth/me").status_code == 200


def test_expired_server_session_is_rejected_even_while_jwt_is_valid(db):
    client = TestClient(app)
    user = _login(client, "expired-server-session@example.com")
    session = db.scalar(select(UserSession).where(UserSession.user_id == user["id"]))
    assert session is not None
    session.expires_at = datetime.now(UTC).replace(tzinfo=None) - timedelta(seconds=1)
    db.commit()

    assert client.get("/api/auth/me").status_code == 401


def test_provider_outage_is_503_not_invalid_credentials(monkeypatch):
    from app.routers import auth as auth_router

    def unavailable(_token: str):
        raise ProviderUnavailableError("upstream detail must not be reflected")

    monkeypatch.setattr(auth_router, "verify_google_id_token", unavailable)
    response = TestClient(app).post("/api/auth/google", json={"id_token": "x"})
    assert response.status_code == 503
    assert response.json() == {
        "detail": {
            "code": "PROVIDER_UNAVAILABLE",
            "message": "Google sign-in is temporarily unavailable",
        }
    }


def test_uniform_errors_request_ids_no_store_and_login_attribution(db):
    client = TestClient(app)
    response = client.get("/api/does-not-exist", headers={"X-Request-ID": "trace-123"})
    assert response.status_code == 404
    assert response.json()["detail"] == {"code": "NOT_FOUND", "message": "Not Found"}
    assert response.headers["X-Request-ID"] == "trace-123"
    assert response.headers["Cache-Control"].startswith("no-store")

    login = client.post(
        "/api/auth/dev",
        json={"email": "attributed@example.com", "role": "user"},
        headers={**DEV_AUTH_HEADERS, "X-Request-ID": "login-trace"},
    )
    assert login.status_code == 200
    assert flush_request_logging(timeout=2.0)
    row = db.scalar(
        select(RequestLog).where(RequestLog.path == "/api/auth/dev").order_by(RequestLog.id.desc())
    )
    assert row is not None and row.user_id == login.json()["id"]


def test_token_size_and_non_finite_accuracy_are_rejected():
    client = TestClient(app)
    too_large = client.post("/api/auth/google", json={"id_token": "x" * 16_385})
    assert too_large.status_code == 422
    with pytest.raises(ValidationError):
        LocationUpdate(latitude=1, longitude=2, accuracy=float("inf"))


def test_total_request_body_cap_rejects_content_length_before_provider(monkeypatch):
    from app import main as main_module
    from app.config import settings
    from app.routers import auth as auth_router

    def must_not_run(_token: str):  # pragma: no cover - an assertion documents the boundary
        raise AssertionError("provider verification ran for an oversized body")

    def must_not_persist(*_args, **_kwargs):  # pragma: no cover - boundary assertion
        raise AssertionError("oversized traffic reached request-log persistence")

    monkeypatch.setattr(auth_router, "verify_google_id_token", must_not_run)
    monkeypatch.setattr(main_module, "record_request", must_not_persist)
    boundary_evidence = []
    monkeypatch.setattr(
        main_module,
        "_log_boundary_rejection",
        lambda scope, *, status_code, code: boundary_evidence.append(
            (status_code, code, scope["state"]["request_id"])
        ),
    )
    response = RawTestClient(app).post(
        "/api/auth/google",
        content=b"x" * (settings.request_body_max_bytes + 1),
        headers={"Content-Type": "application/json", "X-Request-ID": "oversize-length"},
    )

    assert response.status_code == 413
    assert response.json()["detail"]["code"] == "REQUEST_TOO_LARGE"
    assert response.headers["X-Request-ID"] == "oversize-length"
    assert response.headers["Cache-Control"].startswith("no-store")
    assert boundary_evidence == [(413, "REQUEST_TOO_LARGE", "oversize-length")]


def test_total_request_body_cap_also_rejects_streams_without_content_length():
    from app.config import settings

    def chunks():
        yield b'{"id_token":"'
        yield b"x" * settings.request_body_max_bytes
        yield b'"}'

    response = RawTestClient(app).post(
        "/api/auth/google",
        content=chunks(),
        headers={"Content-Type": "application/json"},
    )

    assert response.status_code == 413
    assert response.json()["detail"]["code"] == "REQUEST_TOO_LARGE"
