# Fix Pack — FND-028: Correct the documentation drifts

**Severity:** Low · **Domain:** Documentation & DX · **Effort:** XS · **Docs-only, no code change**

## Context
`C:\dev\kingfisher-crm`. Several README/CASE_STUDY claims no longer match the code. Fix the docs to match
reality (do **not** change code to match the docs unless the product genuinely intends the described feature).

## Drifts to fix (each verified against source)

1. **Login page: Microsoft button + dev-login form don't exist.**
   - `README.md:207-212` says "Google / Microsoft buttons appear on the login page" and "with
     `DEV_AUTH_ENABLED=true`, the login page shows a dev form (any email, and a role picker populated with
     every role … from `GET /api/auth/dev/roles`)."
   - Reality: `frontend/src/pages/LoginPage.tsx` renders **Google only** ("Sign in with your Google account
     to continue."). No Microsoft button, no dev form. The e2e signs in via a raw `fetch("/api/auth/dev")`
     (`frontend/e2e/access-control.spec.ts:9`), not a UI form. `msal.ts` and the `/api/auth/microsoft`
     backend route exist but are UI-unreachable.
   - **Fix:** update the README "Signing in" section to describe the actual Google-only login page and note
     that Microsoft/dev-login are backend-capable but not surfaced in the current UI (or, if the form is
     intended, file it as a feature — but that's a code change, not this docs pack).

2. **Migration count.** `README.md:140` and `:277` say "migrations 0001…0006"; the repo has **ten**
   (`0001`…`0010`, `alembic heads` → `0010`). Fix the range in both places.

3. **ADMIN_EMAILS semantics.** `README.md:212` says "The first email in `ADMIN_EMAILS` becomes an admin."
   Reality: `backend/app/auth/service.py:46` checks `ident.email in settings.admin_email_set` — **every**
   listed email is promoted (subject to provider verification / tenant match). Fix the wording.

4. **Test count.** `CASE_STUDY.md:69` says "86 tests"; the backend suite is **95** (`pytest` collects 95).
   Update the number (and consider phrasing it as "90+" to avoid future drift), or make it a range.

## Constraints — do NOT change
- Docs only. Do not add the Microsoft button / dev form to the UI here (separate feature decision).
- Keep the READMEs' tone and structure; just correct the facts.
- Don't overstate: describe what the code does today.

## Acceptance criteria
- The four claims above match the code.
- A quick grep confirms no remaining "0001…0006", "first email", "86 tests", or Microsoft/dev-form login
  claims that contradict `LoginPage.tsx`.
