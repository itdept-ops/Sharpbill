"""WebSocket presence: auth on connect, roster-visibility gate, and cross-origin rejection.

Covers FND-025 (the subsystem previously had zero tests) and exercises the FND-034 origin check.
"""

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from app.main import app


def test_ws_rejects_unauthenticated(client):
    """No session cookie -> the handshake is closed before accept."""
    with pytest.raises(WebSocketDisconnect):
        with client.websocket_connect("/api/ws/presence"):
            pass


def test_ws_authenticated_receives_presence_with_roster(client):
    """An admin (holds presence.view) receives the presence frame including the online roster."""
    client.post("/api/auth/dev", json={"email": "wsadmin@example.com", "role": "admin"})
    with client.websocket_connect("/api/ws/presence") as ws:
        msg = ws.receive_json()
    assert msg["type"] == "presence"
    assert msg["count"] >= 1
    assert "online" in msg  # presence.view -> roster included


def test_ws_roster_hidden_without_presence_view(client):
    """A connection lacking presence.view gets the count but not the roster (names)."""
    client.post("/api/auth/dev", json={"email": "wsadmin2@example.com", "role": "admin"})
    client.post("/api/roles", json={"name": "NoPresence", "permission_keys": []})

    viewer = TestClient(app)
    viewer.post("/api/auth/dev", json={"email": "nopres@example.com", "role": "NoPresence"})
    with viewer.websocket_connect("/api/ws/presence") as ws:
        msg = ws.receive_json()
    assert msg["type"] == "presence"
    assert "online" not in msg  # no presence.view -> roster withheld


def test_ws_rejects_cross_origin(client):
    """A cross-site Origin is refused even with a valid cookie (FND-034)."""
    client.post("/api/auth/dev", json={"email": "wso@example.com", "role": "admin"})
    with pytest.raises(WebSocketDisconnect):
        with client.websocket_connect(
            "/api/ws/presence", headers={"origin": "http://evil.example.com"}
        ):
            pass
