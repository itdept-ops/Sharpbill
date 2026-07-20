import { useCallback, useEffect, useRef, useState } from "react";
import { Link, Navigate, useLocation, useNavigate } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { GOOGLE_NONCE_REFRESH_MS, loadGoogleIdentityServices } from "../auth/google";
import { MatrixRain } from "../components/MatrixRain";
import {
  isSupportedLegalManifest,
  LEGAL_BUNDLE_VERSION,
  LEGAL_DOCUMENTS,
  type LegalManifest,
  type LegalManifestDocumentKey,
} from "../legal";
import type { AuthConfig, User } from "../types";
import { captureLocation } from "../util/location";

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

function isLegalAcceptanceError(error: unknown): error is ApiError {
  if (!(error instanceof ApiError)) return false;
  return error.code === "LEGAL_ACCEPTANCE_REQUIRED" || error.code === "LEGAL_BUNDLE_STALE";
}

export function LoginPage() {
  const { user, setUser } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? "/dashboard";

  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [configFailed, setConfigFailed] = useState(false);
  const [legalManifest, setLegalManifest] = useState<LegalManifest | null>(null);
  const [legalManifestFailed, setLegalManifestFailed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [gsiFailed, setGsiFailed] = useState(false);
  const [gsiAttempt, setGsiAttempt] = useState(0);
  const [signingIn, setSigningIn] = useState<"google" | "microsoft" | null>(null);
  const [shareLocation, setShareLocation] = useState(false);
  const [legalAccepted, setLegalAccepted] = useState(false);
  const shareLocationRef = useRef(false);
  const legalAcceptedRef = useRef(false);
  const legalBundleVersionRef = useRef<string | null>(null);
  const googleBtnRef = useRef<HTMLDivElement>(null);
  const signInInFlightRef = useRef(false);
  // Provider booleans are meaningful only with the corresponding runtime public client ID. Treat
  // an inconsistent response as unavailable instead of rendering a button that cannot work.
  const googleClientId = config?.google ? config.google_client_id : null;
  const microsoftClientId = config?.microsoft ? config.microsoft_client_id : null;
  const legalReady = legalManifest !== null && isSupportedLegalManifest(legalManifest);
  const legalBundleVersion = legalReady ? legalManifest.bundle_version : null;
  legalBundleVersionRef.current = legalBundleVersion;

  const loadConfig = useCallback((signal?: AbortSignal) => {
    setConfigFailed(false);
    api
      .get<AuthConfig>("/api/auth/config", { signal })
      .then(setConfig)
      .catch((e) => {
        if (isAbort(e) && signal?.aborted) return;
        setConfig(null);
        setConfigFailed(true);
      });
  }, []);

  const clearLegalAcceptance = useCallback(() => {
    legalAcceptedRef.current = false;
    setLegalAccepted(false);
  }, []);

  const loadLegalManifest = useCallback((signal?: AbortSignal) => {
    setLegalManifestFailed(false);
    setLegalManifest(null);
    clearLegalAcceptance();
    api
      .get<LegalManifest>("/api/legal/manifest", { signal })
      .then((manifest) => {
        setLegalManifest(manifest);
        if (!isSupportedLegalManifest(manifest)) clearLegalAcceptance();
      })
      .catch((e) => {
        if (isAbort(e) && signal?.aborted) return;
        setLegalManifest(null);
        setLegalManifestFailed(true);
        clearLegalAcceptance();
      });
  }, [clearLegalAcceptance]);

  useEffect(() => {
    const controller = new AbortController();
    loadConfig(controller.signal);
    loadLegalManifest(controller.signal);
    return () => controller.abort();
  }, [loadConfig, loadLegalManifest]);

  const onSuccess = useCallback(
    (authenticatedUser: User) => {
      setUser(authenticatedUser);
      if (shareLocationRef.current) captureLocation();
      navigate(from, { replace: true });
    },
    [from, navigate, setUser],
  );

  const authErrorMessage = useCallback((authError: unknown, fallback: string): string => {
    if (isLegalAcceptanceError(authError)) {
      clearLegalAcceptance();
      if (authError.code === "LEGAL_BUNDLE_STALE") {
        loadLegalManifest();
        return "The legal terms changed, so the current manifest is being refreshed. Review the documents and check the agreement again; a page refresh or updated web release may be required.";
      }
      return "Your legal acceptance could not be verified. Review the current documents and check the agreement again.";
    }
    return authError instanceof ApiError ? authError.message : fallback;
  }, [clearLegalAcceptance, loadLegalManifest]);

  useEffect(() => {
    if (!googleClientId || !legalAccepted || !legalBundleVersion) return;
    setGsiFailed(false);
    const controller = new AbortController();
    let cancelled = false;
    let credentialHandled = false;
    let refreshTimer: number | null = null;

    (async () => {
      try {
        await loadGoogleIdentityServices();
        const nonce = (
          await api.post<{ nonce: string }>("/api/auth/nonce", undefined, {
            signal: controller.signal,
          })
        ).nonce;
        if (cancelled) return;
        if (!window.google || !googleBtnRef.current) {
          throw new Error("Google Identity Services did not initialize");
        }
        const parent = googleBtnRef.current;
        parent.replaceChildren();
        window.google.accounts.id.initialize({
          client_id: googleClientId,
          nonce,
          callback: async (resp) => {
            // A rendered GSI button and its nonce are one-shot. Ignore duplicate/stale callbacks;
            // the next usable button is always initialized with fresh backend state.
            if (cancelled || credentialHandled || signInInFlightRef.current) return;
            credentialHandled = true;
            if (refreshTimer !== null) window.clearTimeout(refreshTimer);
            if (!legalAcceptedRef.current) {
              setError("Accept the current legal documents before signing in.");
              setGsiAttempt((attempt) => attempt + 1);
              return;
            }
            signInInFlightRef.current = true;
            setSigningIn("google");
            setError(null);
            googleBtnRef.current?.replaceChildren();
            try {
              const authenticatedUser = await api.post<User>(
                "/api/auth/google",
                {
                  id_token: resp.credential,
                  legal_accepted: true,
                  legal_bundle_version: legalBundleVersion,
                },
                { signal: controller.signal },
              );
              if (cancelled) return;
              setSigningIn(null);
              onSuccess(authenticatedUser);
            } catch (e) {
              if (cancelled || isAbort(e)) return;
              setError(authErrorMessage(e, "Google sign-in failed"));
              signInInFlightRef.current = false;
              setSigningIn(null);
              setGsiAttempt((attempt) => attempt + 1);
            }
          },
        });
        window.google.accounts.id.renderButton(parent, {
          theme: "filled_black",
          size: "large",
          width: Math.min(320, Math.max(200, Math.floor(parent.clientWidth || 320))),
        });
        refreshTimer = window.setTimeout(() => {
          if (cancelled || credentialHandled) return;
          // Invalidate this callback before scheduling the replacement to close the small gap
          // between an expired timer firing and React running the effect cleanup.
          credentialHandled = true;
          setGsiAttempt((attempt) => attempt + 1);
        }, GOOGLE_NONCE_REFRESH_MS);
      } catch (e) {
        if (!cancelled && !isAbort(e)) setGsiFailed(true);
      }
    })();

    return () => {
      cancelled = true;
      if (refreshTimer !== null) window.clearTimeout(refreshTimer);
      signInInFlightRef.current = false;
      controller.abort();
    };
  }, [authErrorMessage, googleClientId, gsiAttempt, legalAccepted, legalBundleVersion, onSuccess]);

  const signInWithMicrosoft = async () => {
    if (signInInFlightRef.current || !legalAcceptedRef.current || !legalBundleVersion) return;
    const acceptedBundleVersion = legalBundleVersion;
    signInInFlightRef.current = true;
    setSigningIn("microsoft");
    setError(null);
    try {
      const nonce = (await api.post<{ nonce: string }>("/api/auth/nonce")).nonce;
      const { microsoftLogin } = await import("../auth/msal");
      if (!microsoftClientId) throw new Error("Microsoft sign-in is unavailable");
      const idToken = await microsoftLogin(nonce, microsoftClientId);
      if (
        !legalAcceptedRef.current ||
        legalBundleVersionRef.current !== acceptedBundleVersion
      ) {
        setError("Legal acceptance changed before sign-in completed. Review and check the agreement again.");
        return;
      }
      onSuccess(
        await api.post<User>("/api/auth/microsoft", {
          id_token: idToken,
          legal_accepted: true,
          legal_bundle_version: acceptedBundleVersion,
        }),
      );
    } catch (e) {
      const cancelled = e instanceof Error && /cancel|popup_window_error/i.test(`${e.name} ${e.message}`);
      setError(
        cancelled
          ? "Microsoft sign-in was cancelled."
          : authErrorMessage(e, "Microsoft sign-in failed. Please try again."),
      );
    } finally {
      signInInFlightRef.current = false;
      setSigningIn(null);
    }
  };

  if (user) return <Navigate to={from} replace />;

  const providerLabel = googleClientId && microsoftClientId
    ? "Google or Microsoft"
    : googleClientId
      ? "Google"
      : microsoftClientId
        ? "Microsoft"
        : null;

  const legalUrl = (key: LegalManifestDocumentKey, fallback: string) =>
    legalManifest?.documents.find((document) => document.key === key)?.url ?? fallback;

  return (
    <main className="auth-wrap">
      <MatrixRain opacity={0.24} />
      <div className="scanlines" />

      <section className="panel panel--brackets auth-panel" aria-labelledby="login-title">
        <div className="panel-header">
          <h1 id="login-title" className="auth-title">
            // AUTHENTICATE<span className="cursor" />
          </h1>
        </div>
        <div className="auth-body">
          <div className="auth-brand">
            <span className="logo-glyph" style={{ color: "var(--green)", fontSize: 22 }} aria-hidden="true">
              ◈
            </span>
            <strong style={{ letterSpacing: "0.06em" }}>KINGFISHER</strong>
          </div>

          {providerLabel && (
            <p className="muted small" style={{ margin: 0 }}>
              Sign in with {providerLabel} to continue.
            </p>
          )}

          {!legalManifest && !legalManifestFailed && (
            <p className="muted small" role="status" aria-live="polite" style={{ margin: 0 }}>
              Loading the current legal bundle…
            </p>
          )}

          {(legalManifestFailed || (legalManifest !== null && !legalReady)) && (
            <div className="auth-error legal-manifest-error" role="alert">
              <span>
                {legalManifestFailed
                  ? "ERR: could not load the current legal bundle. Sign-in remains disabled."
                  : "ERR: this web build does not match the legal bundle required by the server. Sign-in remains disabled."}
              </span>
              <button className="btn btn-ghost btn-sm" type="button" onClick={() => loadLegalManifest()}>
                Retry
              </button>
            </div>
          )}

          <div className="legal-acceptance">
            <input
              id="legal-acceptance"
              type="checkbox"
              required
              disabled={!legalReady || signingIn !== null}
              checked={legalAccepted}
              aria-labelledby="legal-acceptance-copy"
              aria-describedby="legal-acceptance-hint"
              onChange={(event) => {
                legalAcceptedRef.current = event.target.checked;
                setLegalAccepted(event.target.checked);
                if (
                  event.target.checked &&
                  (error?.startsWith("The legal terms changed") ||
                    error?.startsWith("Your legal acceptance"))
                ) {
                  setError(null);
                }
              }}
            />
            <span>
              <span id="legal-acceptance-copy" className="legal-acceptance-copy">
                <label htmlFor="legal-acceptance">I agree to the</label>{" "}
                <Link
                  to={legalUrl("terms", LEGAL_DOCUMENTS.terms.route)}
                  target="_blank"
                  rel="noopener noreferrer"
                  aria-label="Terms of Service (opens in a new tab)"
                >
                  Terms of Service
                </Link>
                <label htmlFor="legal-acceptance">,</label>{" "}
                <Link
                  to={legalUrl("eula", LEGAL_DOCUMENTS.eula.route)}
                  target="_blank"
                  rel="noopener noreferrer"
                  aria-label="EULA (opens in a new tab)"
                >
                  EULA
                </Link>
                <label htmlFor="legal-acceptance">, and</label>{" "}
                <Link
                  to={legalUrl("acceptable_use", LEGAL_DOCUMENTS.aup.route)}
                  target="_blank"
                  rel="noopener noreferrer"
                  aria-label="Acceptable Use Policy (opens in a new tab)"
                >
                  Acceptable Use Policy
                </Link>
                <label htmlFor="legal-acceptance">, and acknowledge the</label>{" "}
                <Link
                  to={legalUrl("privacy", LEGAL_DOCUMENTS.privacy.route)}
                  target="_blank"
                  rel="noopener noreferrer"
                  aria-label="Privacy Notice (opens in a new tab)"
                >
                  Privacy Notice
                </Link>
                <label htmlFor="legal-acceptance">.</label>
              </span>
              <small id="legal-acceptance-hint">
                Draft bundle — counsel review required before production · required to sign in ·
                bundle {legalManifest?.bundle_version ?? LEGAL_BUNDLE_VERSION} · each document
                opens in a new tab
              </small>
            </span>
          </div>

          <label className="location-optin">
            <input
              type="checkbox"
              checked={shareLocation}
              onChange={(e) => {
                shareLocationRef.current = e.target.checked;
                setShareLocation(e.target.checked);
              }}
            />
            <span>
              Share this device&apos;s location after sign-in
              <small>Optional · used to set your place and timezone. Your browser will ask first.</small>
            </span>
          </label>

          {error && <div className="auth-error" role="alert">ERR: {error}</div>}

          {googleClientId && (!legalAccepted || !legalReady) && (
            <button className="sso-btn" type="button" disabled>
              <span className="sso-mark google-mark" aria-hidden="true">G</span>
              Continue with Google
            </button>
          )}
          {googleClientId && legalAccepted && !gsiFailed && (
            <div className="google-slot" ref={googleBtnRef} />
          )}
          {signingIn === "google" && (
            <p className="muted small" role="status" aria-live="polite" style={{ margin: 0 }}>
              Completing Google sign-in...
            </p>
          )}

          {googleClientId && legalReady && legalAccepted && gsiFailed && (
            <div className="auth-error" role="alert" style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <span>ERR: Google sign-in couldn&apos;t load. Check your connection or blockers.</span>
              <span className="spacer" />
              <button
                className="btn btn-ghost btn-sm"
                onClick={() => {
                  setGsiFailed(false);
                  setGsiAttempt((attempt) => attempt + 1);
                }}
              >
                Retry
              </button>
            </div>
          )}

          {googleClientId && microsoftClientId && <div className="auth-divider">OR</div>}

          {microsoftClientId && (
            <button
              className="sso-btn"
              type="button"
              disabled={!legalReady || !legalAccepted || signingIn !== null}
              aria-busy={signingIn === "microsoft"}
              onClick={signInWithMicrosoft}
            >
              <span className="sso-mark" aria-hidden="true">
                <span className="msq"><i /><i /><i /><i /></span>
              </span>
              {signingIn === "microsoft" ? "Opening Microsoft…" : "Continue with Microsoft"}
            </button>
          )}

          {config && !googleClientId && !microsoftClientId && !configFailed && (
            <p className="auth-error" role="alert">
              No sign-in provider is currently available. Contact an administrator.
            </p>
          )}

          {configFailed && (
            <div className="auth-error" role="alert" style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <span>ERR: could not reach the sign-in service.</span>
              <span className="spacer" />
              <button className="btn btn-ghost btn-sm" onClick={() => loadConfig()}>
                Retry
              </button>
            </div>
          )}

          <div className="auth-links">
            <Link to="/">← Back to site</Link>
            <Link to="/security">How access works →</Link>
          </div>
        </div>
      </section>
    </main>
  );
}
