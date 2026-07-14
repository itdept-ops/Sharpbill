from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.auth.deps import require_admin
from app.db import get_db
from app.errors import ApiError
from app.models import User
from app.schemas.user import RoleUpdateRequest, StatusUpdateRequest, UserListOut, UserOut

# require_admin on the router itself: no endpoint here can be added unprotected.
router = APIRouter(dependencies=[Depends(require_admin)])


def _get_target(db: Session, user_id: int) -> User:
    user = db.get(User, user_id)
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    return user


@router.get("", response_model=UserListOut)
def list_users(db: Session = Depends(get_db)) -> UserListOut:
    users = list(db.scalars(select(User).order_by(User.created_at.asc(), User.id.asc())))
    total = db.scalar(select(func.count()).select_from(User)) or 0
    return UserListOut(items=[UserOut.from_user(u) for u in users], total=total)


@router.patch("/{user_id}/role", response_model=UserOut)
def update_role(
    user_id: int,
    body: RoleUpdateRequest,
    db: Session = Depends(get_db),
    admin: User = Depends(require_admin),
) -> UserOut:
    if user_id == admin.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot change your own role")
    user = _get_target(db, user_id)
    user.role = body.role
    db.commit()
    return UserOut.from_user(user)


@router.patch("/{user_id}/status", response_model=UserOut)
def update_status(
    user_id: int,
    body: StatusUpdateRequest,
    db: Session = Depends(get_db),
    admin: User = Depends(require_admin),
) -> UserOut:
    if user_id == admin.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot deactivate yourself")
    user = _get_target(db, user_id)
    user.is_active = body.is_active
    db.commit()
    return UserOut.from_user(user)
