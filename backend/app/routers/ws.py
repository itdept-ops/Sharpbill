import asyncio
from dataclasses import dataclass
from datetime import UTC, datetime

import jwt as pyjwt
from fastapi import APIRouter, WebSocket, WebSocketDisconnect

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.db import SessionLocal
from app.models import User
from app.permissions import PRESENCE_VIEW

router = APIRouter()


@dataclass
class Conn:
    ws: WebSocket
    user_id: int
    name: str | None
    role: str
    can_view: bool


class PresenceHub:
    """In-memory, connection-based presence (single process — fine for local/dev)."""

    def __init__(self) -> None:
        self._conns: list[Conn] = []
        self._lock = asyncio.Lock()

    def _roster(self) -> list[dict]:
        seen: dict[int, dict] = {}
        for c in self._conns:
            seen[c.user_id] = {"id": c.user_id, "display_name": c.name, "role": c.role}
        return list(seen.values())

    async def add(self, conn: Conn) -> None:
        async with self._lock:
            self._conns.append(conn)
        await self.broadcast()

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
    return user


_RECHECK_SECONDS = 30
_LAST_SEEN_THROTTLE = 15


def _refresh_conn(conn: Conn, user: User) -> bool:
    """Sync frozen connection fields with the live user; returns True if anything changed."""
    cv = PRESENCE_VIEW in user.permission_keys
    if cv != conn.can_view or user.role_name != conn.role or user.display_name != conn.name:
        conn.can_view = cv
        conn.role = user.role_name
        conn.name = user.display_name
        return True
    return False


def _touch(db, user: User) -> None:
    now = datetime.now(UTC).replace(tzinfo=None)
    if user.last_seen_at is None or (now - user.last_seen_at).total_seconds() > _LAST_SEEN_THROTTLE:
        user.last_seen_at = now
        db.commit()


@router.websocket("/presence")
async def presence_ws(websocket: WebSocket) -> None:
    with SessionLocal() as db:
        user = _authenticate(db, websocket.cookies.get(COOKIE_NAME))
        if user is None:
            await websocket.close(code=1008)  # policy violation
            return
        conn = Conn(
            ws=websocket,
            user_id=user.id,
            name=user.display_name,
            role=user.role_name,
            can_view=PRESENCE_VIEW in user.permission_keys,
        )
        _touch(db, user)

    await websocket.accept()
    await hub.add(conn)
    try:
        while True:
            try:
                # Wake at least every _RECHECK_SECONDS even if the client sends nothing, so a
                # kicked/deactivated/un-approved user is severed promptly (not just on a frame).
                await asyncio.wait_for(websocket.receive_text(), timeout=_RECHECK_SECONDS)
            except TimeoutError:
                pass
            with SessionLocal() as db:
                user = _authenticate(db, websocket.cookies.get(COOKIE_NAME))
                if user is None:  # revoked since connect -> cut the channel
                    await websocket.close(code=1008)
                    break
                changed = _refresh_conn(conn, user)
                _touch(db, user)  # throttled write
            if changed:
                await hub.broadcast()
    except WebSocketDisconnect:
        pass
    finally:
        await hub.remove(conn)
