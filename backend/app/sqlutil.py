"""Small SQL helpers shared across routers."""


def escape_like(term: str) -> str:
    """Escape LIKE wildcards so user input matches literally.

    Use with SQLAlchemy's ``.like(pattern, escape="\\\\")`` so a search for ``a_b`` or ``50%``
    matches those literal characters instead of treating ``_``/``%`` as wildcards.
    """
    return term.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
