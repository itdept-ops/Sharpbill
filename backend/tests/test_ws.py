"""WebSocket presence: auth on connect, roster-visibility gate, and cross-origin rejection.

Covers FND-025 (the subsystem previously had zero tests) and exercises the FND-034 origin check.
"""

import asyncio

import pytest
from starlette.websockets import WebSocketDisconnect

from app.main import app
from app.routers import ws as ws_router
from app.routers.ws import Conn, PresenceHub, _wait_for_client_activity
from tests.client import TestClient


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


def test_ws_rejects_cross_scheme_origin(client):
    """A matching host on a different origin scheme is still cross-origin."""
    client.post("/api/auth/dev", json={"email": "ws-scheme@example.com", "role": "admin"})
    with pytest.raises(WebSocketDisconnect):
        with client.websocket_connect("/api/ws/presence", headers={"origin": "https://testserver"}):
            pass


def test_ws_accepts_exact_canonical_origin(client):
    client.post("/api/auth/dev", json={"email": "ws-origin@example.com", "role": "admin"})
    with client.websocket_connect(
        "/api/ws/presence", headers={"origin": "http://testserver"}
    ) as websocket:
        assert websocket.receive_json()["type"] == "presence"


def test_broadcast_is_concurrent_and_drops_a_stuck_client(monkeypatch):
    """A never-completing send is bounded without serially blocking healthy clients."""

    class ConcurrentProbe:
        def __init__(self, state):
            self.state = state
            self.messages = []

        async def send_json(self, payload):
            self.state["started"] += 1
            if self.state["started"] == 2:
                self.state["both_started"].set()
            await self.state["both_started"].wait()
            self.messages.append(payload)

    class StuckSocket:
        def __init__(self):
            self.close_started = asyncio.Event()
            self.close_codes = []

        async def send_json(self, _payload):
            await asyncio.Event().wait()

        async def receive_text(self):
            await asyncio.Event().wait()

        async def close(self, *, code):
            self.close_codes.append(code)
            self.close_started.set()
            await asyncio.Event().wait()

    async def exercise():
        monkeypatch.setattr(ws_router, "_BROADCAST_SEND_TIMEOUT_SECONDS", 0.05)
        monkeypatch.setattr(ws_router, "_CONNECTION_CLOSE_TIMEOUT_SECONDS", 0.05)
        monkeypatch.setattr(ws_router, "_RECHECK_SECONDS", 5)
        state = {"started": 0, "both_started": asyncio.Event()}
        first = ConcurrentProbe(state)
        second = ConcurrentProbe(state)
        healthy = [
            Conn(first, 1, "First", "user", True),
            Conn(second, 2, "Second", "user", True),
        ]
        stuck_socket = StuckSocket()
        stuck = Conn(stuck_socket, 3, "Stuck", "user", True)
        hub = PresenceHub()
        hub._conns = [*healthy, stuck]
        handler_wait = asyncio.create_task(_wait_for_client_activity(stuck_socket, stuck))

        await hub.broadcast()

        assert first.messages and second.messages
        assert hub._conns == healthy
        assert stuck.disconnect_requested.is_set()
        assert stuck_socket.close_started.is_set()
        assert stuck_socket.close_codes == [1011]
        assert await asyncio.wait_for(handler_wait, timeout=0.1) is False

    asyncio.run(exercise())
