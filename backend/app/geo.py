"""Offline reverse-geocoding: derive a place label + IANA timezone from GPS coordinates.

Both lookups are fully offline (bundled datasets) — no external geocoding service, so coordinates
never leave the box. Datasets load lazily on first use and are cached process-wide.
"""

import logging

_log = logging.getLogger("app.geo")
_tf = None  # TimezoneFinder is heavy to construct; build it once, lazily.


def timezone_for(lat: float, lng: float) -> str | None:
    """IANA timezone name for the coordinate, e.g. 'America/New_York'."""
    global _tf
    try:
        if _tf is None:
            from timezonefinder import TimezoneFinder

            _tf = TimezoneFinder()
        return _tf.timezone_at(lat=lat, lng=lng)
    except Exception:
        _log.exception("timezone lookup failed")
        return None


def place_for(lat: float, lng: float) -> str | None:
    """Nearest-place label 'City, Region, CC' for the coordinate, or None."""
    try:
        import reverse_geocoder as rg

        r = rg.search((lat, lng), mode=1)[0]  # mode=1: single-threaded (server-safe)
        parts = [r.get("name"), r.get("admin1"), r.get("cc")]
        return ", ".join(p for p in parts if p) or None
    except Exception:
        _log.exception("reverse geocode failed")
        return None
