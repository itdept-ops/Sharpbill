# Fix Packs — Sharpbill audit 2026-07-15

> Rebranded archival copies: product labels and repository-root references were normalized for Sharpbill; finding scope remains tied to the 2026-07-15 audit.

Each file is a **self-contained prompt** a fresh session (zero prior context) can execute. They cover all 43
findings from `../code-review-report.html`. Order by the waves in `../remediation-roadmap.md`.

| File | Findings | Severity | Wave |
|------|----------|----------|------|
| `FND-001_react-router-dom-cve.md` | FND-001 | High | 0 |
| `FND-005-006-007_lockout-and-grant-guards.md` | FND-005, 006, 007 | Medium | 0 |
| `FND-028_docs-drift.md` | FND-028 | Low | 0 |
| `FND-002-003-004_rbac-hierarchy-and-toctou.md` | FND-002, 003, 004 | Medium | 1 |
| `FND-008_microsoft-admin-bootstrap-oid.md` | FND-008 | Medium | 1 |
| `FND-011-012-030_privacy-and-session-lifecycle.md` | FND-011, 012, 030 | Medium/Low | 1 |
| `FND-016-018-025-034-035_websocket.md` | FND-016, 018, 025, 034, 035 | Medium/Low | 2 |
| `FND-017-019-029_correctness-and-migration.md` | FND-017, 019, 029 | Medium/Low | 2 |
| `FND-026_testing-jwt-and-config-guards.md` | FND-026 | Medium | 2 |
| `FND-013-014-015-039_performance.md` | FND-013, 014, 015, 039 | Medium/Low | 3 |
| `FND-020-022-023-040_supply-chain-and-ops.md` | FND-020, 022, 023, 040 | Medium/Low | 4 |
| `FND-009-010_proxy-ip-and-public-docs.md` | FND-009, 010 | Medium | 4 |
| `FND-021_nginx-security-headers.md` | FND-021 | Medium | 4 |
| `FND-024-027-036-041-042_frontend-a11y.md` | FND-024, 027, 036, 041, 042 | Medium/Low | 5 |
| `FND-031-032-033-037-038-043_low-hardening.md` | FND-031, 032, 033, 037, 038, 043 | Low | deferred |

**Ground rules for any executing session:** this repo has strong existing tests — keep `docker compose exec
api pytest` (95 passing) and `cd frontend && npm run lint && npm run test && npm run build` green, keep the
`{ "detail": { "code", "message" } }` error envelope, keep a single Alembic head with working `downgrade()`,
and do **not** touch `deploy/` or AWS deployment config (out of scope for this audit).
