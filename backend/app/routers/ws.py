import asyncio
import time
from dataclasses import dataclass
from datetime import UTC, datetime
from urllib.parse import urlparse

import jwt as pyjwt
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from starlette.concurrency import run_in_threadpool

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.auth.sessions import active_session
from app.db import SessionLocal
from app.models import User
from app.permissions import PRESENCE_VIEW

router = APIRouter()

# Bounds on the in-memory connection pool (single process): stop one authenticated client from
# growing the roster/memory without limit.
_MAX_CONNS_PER_USER = 5
_MAX_CONNS_TOTAL = 500
# Wake at least this often even without a client frame (so a kicked user is severed promptly)...
_RECHECK_SECONDS = 30
# ...but never re-authenticate (a DB round-trip) more than once per this interval, so a client
# spamming frames can't hammer the DB / event loop.
_MIN_REAUTH_INTERVAL = 5
_LAST_SEEN_THROTTLE = 15


@dataclass
class Conn:
    ws: WebSocket
    user_id: int
    name: str | None
    role: str
    can_view: bool


class PresenceHub:
    """In-memory, connection-based presence (single process — fine for local/dev).

    NOTE: this reflects who is connected over a live WebSocket *right now*, which is a subset of
    the last_seen-based "online" set surfaced by the REST endpoints (a tab on the polling
    fallback is online but not in this roster). Treat it as "connected live", not authoritative.
    """

    def __init__(self) -> None:
        self._conns: list[Conn] = []
        self._lock = asyncio.Lock()

    def _roster(self) -> list[dict]:
        seen: dict[int, dict] = {}
        for c in self._conns:
            seen[c.user_id] = {"id": c.user_id, "display_name": c.name, "role": c.role}
        return list(seen.values())

    async def add(self, conn: Conn) -> bool:
        """Register a connection. Returns False if the global cap is hit (caller should close)."""
        evicted: list[Conn] = []
        async with self._lock:
            if len(self._conns) >= _MAX_CONNS_TOTAL:
                return False
            mine = [c for c in self._conns if c.user_id == conn.user_id]
            # Enforce the per-user cap by evicting this user's oldest connections.
            while len(mine) >= _MAX_CONNS_PER_USER:
                victim = mine.pop(0)
                self._conns.remove(victim)
                evicted.append(victim)
            self._conns.append(conn)
        for c in evicted:
            try:
                await c.ws.close(code=1000)
            except Exception:
                pass
        await self.broadcast()
        return True

    async def remove(self, conn: Conn) -> None:
        async with self._lock:
            self._conns = [c for c in self._conns if c is not conn]
        await self.broadcast()

    async def broadcast(self) -> None:
        roster = self._roster()
        count = len(roster)
        dead: list[Conn] = []
        for c in list(self._conns):
            try:
                payload = {"type": "presence", "count": count}
                if c.can_view:
                    payload["online"] = roster
                await c.ws.send_json(payload)
            except Exception:
                dead.append(c)
        if dead:
            async with self._lock:
                self._conns = [c for c in self._conns if c not in dead]


hub = PresenceHub()


def _authenticate(db, token: str | None) -> User | None:
    if not token:
        return None
    try:
        payload = decode_session_token(token)
    except (pyjwt.InvalidTokenError, ValueError, KeyError):
        return None
    user = db.get(User, int(payload.get("sub", 0)))
    if user is None or not user.is_active or not user.is_approved:
        return None
    if user.session_valid_after is not None:
        cutoff = int(user.session_valid_after.replace(tzinfo=UTC).timestamp())
        if int(payload.get("iat", 0)) <= cutoff:
            return None
    if active_session(db, payload.get("jti", "")) is None:  # per-device revocation
        return None
    return user


def _touch(db, user: User) -> None:
    now = datetime.now(UTC).replace(tzinfo=None)
    if user.last_seen_at is None or (now - user.last_seen_at).total_seconds() > _LAST_SEEN_THROTTLE:
        user.last_seen_at = now
        db.commit()


def _auth_snapshot(token: str | None) -> tuple[int, str | None, str, bool] | None:
    """Sync: authenticate + throttled last_seen touch, returning a detached snapshot.

    Runs in a threadpool so the synchronous MySQL round-trips never block the async event loop.
    Returns plain values (not the ORM object) so nothing detached is touched afterwards.
    """
    with SessionLocal() as db:
        user = _authenticate(db, token)
        if user is None:
            return None
        snap = (user.id, user.display_name, user.role_name, PRESENCE_VIEW in user.permission_keys)
        _touch(db, user)
        return snap


def _origin_ok(websocket: WebSocket) -> bool:
    """Reject a cross-site WebSocket handshake (defense-in-depth beyond the SameSite cookie).

    Browsers always send Origin on a WS handshake; a non-browser client (no Origin) is allowed
    since it still needs the session cookie. Same-origin means Origin.host == Host.
    """
    origin = websocket.headers.get("origin")
    if not origin:
        return True
    return urlparse(origin).netloc == websocket.headers.get("host", "")


def _refresh_conn(conn: Conn, name: str | None, role: str, can_view: bool) -> bool:
    """Sync a connection's cached fields with the live user; True if anything changed."""
    if can_view != conn.can_view or role != conn.role or name != conn.name:
        conn.can_view = can_view
        conn.role = role
        conn.name = name
        return True
    return False


@router.websocket("/presence")
async def presence_ws(websocket: WebSocket) -> None:
    if not _origin_ok(websocket):
        await websocket.close(code=1008)  # cross-origin handshake
        return

    token = websocket.cookies.get(COOKIE_NAME)
    snap = await run_in_threadpool(_auth_snapshot, token)
    if snap is None:
        await websocket.close(code=1008)  # policy violation
        return
    uid, name, role, can_view = snap
    conn = Conn(ws=websocket, user_id=uid, name=name, role=role, can_view=can_view)

    await websocket.accept()
    if not await hub.add(conn):
        await websocket.close(code=1013)  # global cap reached — try again later
        return

    last_auth = time.monotonic()
    try:
        while True:
            try:
                await asyncio.wait_for(websocket.receive_text(), timeout=_RECHECK_SECONDS)
            except TimeoutError:
                pass
            now = time.monotonic()
            if now - last_auth < _MIN_REAUTH_INTERVAL:
                continue  # throttle re-auth so a frame flood can't hammer the DB
            last_auth = now
            snap = await run_in_threadpool(_auth_snapshot, websocket.cookies.get(COOKIE_NAME))
            if snap is None:  # revoked since connect -> cut the channel
                await websocket.close(code=1008)
                break
            _, name, role, can_view = snap
            if _refresh_conn(conn, name, role, can_view):
                await hub.broadcast()
    except WebSocketDisconnect:
        pass
    finally:
        await hub.remove(conn)
