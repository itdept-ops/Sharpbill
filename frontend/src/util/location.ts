import { api } from "../api/client";

/**
 * Optionally capture the user's location after login. This triggers the browser's native
 * permission prompt; if the user denies (or the API is unavailable), we silently do nothing.
 */
export function captureLocation(): void {
  if (!("geolocation" in navigator)) return;
  navigator.geolocation.getCurrentPosition(
    (pos) => {
      api
        .post("/api/auth/location", {
          latitude: pos.coords.latitude,
          longitude: pos.coords.longitude,
          accuracy: pos.coords.accuracy,
        })
        .catch(() => {
          /* best-effort */
        });
    },
    () => {
      /* denied or unavailable — optional, so ignore */
    },
    { enableHighAccuracy: false, timeout: 10000, maximumAge: 600000 },
  );
}
