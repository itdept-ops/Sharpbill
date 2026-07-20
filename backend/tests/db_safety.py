"""Pure safety policy for the destructive real-MySQL test harness."""

import re
import secrets

from sqlalchemy.engine import URL, make_url

DESTRUCTIVE_TEST_ACK = "kingfisher-ephemeral-test-db"
_ALLOWED_HOSTS = {"127.0.0.1", "::1", "localhost", "mysql"}
_SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_]+$")


def validate_test_database_base(
    main_url: str | URL,
    candidate_url: str | URL,
    *,
    destructive_ack: str | None,
) -> URL:
    """Validate every destructive precondition before any engine can connect."""
    if destructive_ack != DESTRUCTIVE_TEST_ACK:
        raise RuntimeError("Refusing destructive tests: explicit test-database ack is missing")

    main = make_url(main_url) if isinstance(main_url, str) else main_url
    candidate = make_url(candidate_url) if isinstance(candidate_url, str) else candidate_url
    if not main.drivername.startswith("mysql") or not candidate.drivername.startswith("mysql"):
        raise RuntimeError("Refusing destructive tests: only the MySQL test harness is supported")
    if main.host not in _ALLOWED_HOSTS or candidate.host not in _ALLOWED_HOSTS:
        raise RuntimeError("Refusing destructive tests: database host is not local/Compose")
    if (main.host, main.port or 3306) != (candidate.host, candidate.port or 3306):
        raise RuntimeError("Refusing destructive tests: test DB must use the local main DB server")
    if not candidate.username or candidate.username.lower() == "root":
        raise RuntimeError("Refusing destructive tests: test application URL must be non-root")

    main_name = main.database or ""
    candidate_name = candidate.database or ""
    if candidate_name == main_name or "_test" not in candidate_name.lower():
        raise RuntimeError("Refusing destructive tests: base DB must be distinct and contain _test")
    if len(candidate_name) > 48 or not _SAFE_IDENTIFIER.fullmatch(candidate_name):
        raise RuntimeError("Refusing destructive tests: unsafe test database identifier")
    if not _SAFE_IDENTIFIER.fullmatch(candidate.username):
        raise RuntimeError("Refusing destructive tests: unsafe test database username")
    return candidate


def randomized_test_database_url(base_url: URL) -> URL:
    """Return a unique run-scoped database URL that remains within MySQL's identifier limit."""
    return base_url.set(database=f"{base_url.database}_run_{secrets.token_hex(4)}")
