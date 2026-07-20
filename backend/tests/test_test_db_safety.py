"""The destructive test harness must reject unsafe targets before creating an engine."""

import pytest

from tests.db_safety import (
    DESTRUCTIVE_TEST_ACK,
    randomized_test_database_url,
    validate_test_database_base,
)

_MAIN = "mysql+pymysql://appuser:pw@mysql:3306/appdb"
_SAFE_BASE = "mysql+pymysql://appuser:pw@mysql:3306/appdb_test_backend"


@pytest.mark.parametrize(
    ("candidate", "ack"),
    [
        (_SAFE_BASE, None),
        ("mysql+pymysql://appuser:pw@mysql:3306/appdb", DESTRUCTIVE_TEST_ACK),
        (
            "mysql+pymysql://appuser:pw@prod-db.example.com:3306/appdb_test",
            DESTRUCTIVE_TEST_ACK,
        ),
        ("mysql+pymysql://root:pw@mysql:3306/appdb_test", DESTRUCTIVE_TEST_ACK),
        ("mysql+pymysql://appuser:pw@127.0.0.1:3306/appdb_test", DESTRUCTIVE_TEST_ACK),
        ("postgresql://appuser:pw@localhost/appdb_test", DESTRUCTIVE_TEST_ACK),
    ],
)
def test_unsafe_test_database_targets_fail_closed(candidate, ack):
    with pytest.raises(RuntimeError, match="Refusing destructive tests"):
        validate_test_database_base(_MAIN, candidate, destructive_ack=ack)


def test_safe_base_is_randomized_per_run():
    base = validate_test_database_base(
        _MAIN,
        _SAFE_BASE,
        destructive_ack=DESTRUCTIVE_TEST_ACK,
    )
    first = randomized_test_database_url(base)
    second = randomized_test_database_url(base)

    assert first.database.startswith("appdb_test_backend_run_")
    assert second.database.startswith("appdb_test_backend_run_")
    assert first.database != second.database
    assert len(first.database) <= 64
