from datetime import UTC, datetime, timedelta

# A user counts as "online" if seen within this window. The frontend heartbeats well
# inside it (every ~20s) so a live tab stays green.
ONLINE_WINDOW_SECONDS = 90


def online_cutoff() -> datetime:
    return (datetime.now(UTC) - timedelta(seconds=ONLINE_WINDOW_SECONDS)).replace(tzinfo=None)


def is_online(last_seen: datetime | None) -> bool:
    return last_seen is not None and last_seen >= online_cutoff()
