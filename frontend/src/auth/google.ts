const GOOGLE_IDENTITY_SCRIPT_URL = "https://accounts.google.com/gsi/client";
const GOOGLE_IDENTITY_SCRIPT_SELECTOR = "script[data-sharpbill-google-identity]";
const GOOGLE_IDENTITY_LOAD_TIMEOUT_MS = 15_000;

// Backend login nonces currently live for ten minutes. Rotate two minutes early so a button that
// has been sitting on the login page never starts an authentication attempt at the expiry edge.
export const GOOGLE_NONCE_REFRESH_MS = 8 * 60_000;

let googleScriptPromise: Promise<void> | null = null;

function googleScript(): HTMLScriptElement {
  const existing = document.querySelector<HTMLScriptElement>(GOOGLE_IDENTITY_SCRIPT_SELECTOR);
  if (existing && existing.dataset.sharpbillGoogleLoaded !== "true") return existing;
  // A completed script without window.google is corrupt (for example, an extension removed the
  // API). Loading events do not fire twice, so replace it instead of waiting forever.
  existing?.remove();

  const script = document.createElement("script");
  script.src = GOOGLE_IDENTITY_SCRIPT_URL;
  script.async = true;
  script.defer = true;
  script.dataset.sharpbillGoogleIdentity = "true";
  document.head.appendChild(script);
  return script;
}

/** Load Google Identity Services once, sharing one script and one listener set across callers. */
export function loadGoogleIdentityServices(): Promise<void> {
  if (window.google) return Promise.resolve();
  if (googleScriptPromise) return googleScriptPromise;

  const script = googleScript();
  googleScriptPromise = new Promise<void>((resolve, reject) => {
    let settled = false;
    const finish = (error?: Error) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      script.removeEventListener("load", onLoad);
      script.removeEventListener("error", onError);
      if (error) {
        script.remove();
        reject(error);
      } else {
        resolve();
      }
    };
    const onLoad = () => {
      script.dataset.sharpbillGoogleLoaded = "true";
      if (window.google) finish();
      else finish(new Error("Google Identity Services loaded without its browser API"));
    };
    const onError = () => finish(new Error("Google Identity Services failed to load"));
    const timeout = window.setTimeout(
      () => finish(new Error("Google Identity Services timed out while loading")),
      GOOGLE_IDENTITY_LOAD_TIMEOUT_MS,
    );

    script.addEventListener("load", onLoad, { once: true });
    script.addEventListener("error", onError, { once: true });
  }).finally(() => {
    // Keep only in-flight work cached. Once loaded, window.google is the source of truth; after a
    // failure, a later retry creates one clean replacement script rather than stacking listeners.
    googleScriptPromise = null;
  });

  return googleScriptPromise;
}
