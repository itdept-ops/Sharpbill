# Data retention and privacy policy

- Status: Approved application policy
- Effective date: 2026-07-20
- Scope: One KingFisher deployment and its database (one organization)
- Owners: Product owner, privacy owner, security owner, and environment operator
- Repository control status: Implemented and verified through Alembic `0021`; external backup and
  monitoring controls remain environment-owned

This is the repository's data-minimization baseline. It is an engineering policy, not legal advice
or a substitute for a jurisdiction-specific privacy assessment. A deployment may shorten a period.
Extending one requires a documented purpose, data owner, privacy/security approval, and expiry.

## Approved defaults

| Data class | Default retention | Start event | End-of-life action |
|---|---:|---|---|
| Exact latitude, longitude, and accuracy | 24 hours | Last successful capture | Store a capture-time deadline and clear exact coordinates, capture timestamp, and deadline at the earlier of that deadline or a later-shortened active policy; derived profile location/timezone may remain for the active account lifetime |
| Pending, never-approved account | 30 days | Account creation | Anonymize the account and revoke access unless approved or held |
| Session metadata, including IP and user agent | 30 days | Session expiry or revocation | Delete the session row in bounded batches |
| Operational request logs, including IP | 90 days | Request timestamp | Delete the log row in bounded batches |
| Explicit account-erasure request | 30-day grace | Verified request time | At due time, anonymize the account and revoke access unless canceled or held |
| Disabled account | 365 days | Deactivation | Anonymize the account unless reactivated or held |
| Repository security events and delivery state | 400 days | Event occurrence | Delete the event and dependent delivery state, including undelivered records, unless held |
| Versioned legal-acceptance evidence | 2,555 days (provisional) | Successful acceptance | Delete the evidence row in bounded batches unless held; account anonymization first clears IP, user agent, and request ID while retaining the internal link to the anonymized account tombstone |
| Generated CSV exports | Not retained server-side | Response generation | Stream to the authorized requester; retain only the security event recording that an export occurred |
| Active profile fields and derived location/timezone | Active account lifetime | Collection/update | Anonymize when an erasure, pending-account, or disabled-account lifecycle becomes due |

Login nonces and other short-lived anti-replay state are security controls, not business records.
They expire on their protocol lifetime and are pruned even during a privacy hold.

The 2,555-day legal-evidence period and the residual pseudonymous account link require operator and
counsel approval for the deployment's governing jurisdictions; they are not a jurisdiction-neutral
legal conclusion. Reducing the configured period shortens existing cohorts as well as new records;
increasing it does not silently extend an expiry already stamped on a record. Any governed
extension requires the same documented purpose, owner, approval, and expiry as another retention
exception. See [`LEGAL_DOCUMENTS.md`](LEGAL_DOCUMENTS.md) for the release gate.

Migration `0019` records `deactivated_at`, `erasure_requested_at`, `erasure_due_at`, and `erased_at`
on the account; adds deployment-wide `retention_hold` and `retention_hold_reference` settings; seeds
`privacy.manage` only to the built-in admin role; and removes the redundant identity-provider email
copy. Database constraints reject contradictory lifecycle timestamps, active/deactivated mismatch,
or an enabled hold without a nonblank reference.

Migration `0020` adds append-only `legal_acceptances` evidence with exact bundle/document versions
and canonical content digests, acceptance and expiry timestamps, bounded nullable request context,
database invariants, and indexes for account lookup and bounded retention. A downgrade refuses to
discard any retained evidence.

Migration `0021` snapshots the bundle effective date, exact acceptance statement, and each
document's agreement-or-acknowledgement action on every legal-evidence row. It also gives each
precise-location capture its own deadline. The retention worker uses the earlier of that stored
deadline or the current-policy cutoff, so a policy reduction applies to existing data while an
increase cannot silently extend a previously disclosed capture. Upgrade refuses unknown legal
artifacts or incoherent legacy location state instead of inventing evidence.

## Account anonymization

Lifecycle expiry anonymizes an account rather than blindly deleting its relational root. The
operation must be idempotent and transactional. It must:

1. revoke access and remove active/expired session rows;
2. replace the login/profile email with a non-routable synthetic value and clear name, department,
   title, phone, biography, exact and derived location, timezone, UI preferences, and provider-email
   copies;
3. remove direct permission grants and assign only the built-in least-privilege user role;
4. mark the account inactive, unapproved, and erased with lifecycle timestamps; and
5. clear IP, user-agent, and request-ID metadata from retained legal-acceptance evidence; and
6. preserve only the minimum opaque provider binding needed to prevent silent reprovisioning or
   reuse of a consumed administrator-bootstrap subject.

That residual provider/subject binding is a security suppression record, is not returned as a
profile, and must not contain a copied email. Releasing it would allow the same external identity to
create a fresh account when onboarding is open, so release requires a separate documented security
decision. Security events remain subject to their independent 400-day schedule.

## User and administrator workflows

- A signed-in user may clear exact and derived location immediately, request erasure, and cancel a
  pending request during the 30-day grace period. An active hold blocks clearing/scheduling but not
  cancellation, because cancellation preserves data.
- A privacy administrator may review due lifecycle work, apply/release a documented hold, and
  schedule or cancel erasure when acting on a verified request. Every action creates a security
  event with actor, target, request ID, outcome, and non-sensitive reason/reference.
- Administrator accounts are never automatically anonymized or scheduled for erasure. Transfer and
  review administrative authority first, preserving the last-administrator safety invariant.
- Ordinary user, settings, log, and security-event permissions do not imply privacy-administration
  authority. Administrative privacy operations require the dedicated least-privilege
  `privacy.manage` permission, seeded only to the built-in admin role.
- Exports are bounded and streamed. KingFisher does not retain a generated server-side export file;
  the recipient and environment owner are responsible for downloaded copies.

## Holds and exceptions

A hold must have a non-sensitive reference, authorized owner, reason in the external case system,
review date, and release approval. While a hold is active, governed GPS, profile, session, request-
log, account-lifecycle, and security-event deletion pauses. New collection is not justified merely
because a hold exists, and anti-replay nonce expiry never pauses. Hold enablement, review, and
release are auditable security events.

The environment owner must review active holds at least every 90 days. A hold without a current
owner or review record is an incident, not an indefinite retention authorization.

## Cleanup operation and evidence

Retention work must run independently of login and log-write traffic. Each class is processed in
bounded, independently committed batches so cleanup cannot hold an unbounded transaction. Operators
must monitor last success, rows examined/deleted/anonymized, oldest eligible row, failures, and hold
state. Re-running a partial cycle must be safe.

Every route that can race anonymization re-reads the account under a current row lock and replaces
older ORM identity-map state before writing. This prevents a request that began before erasure from
restoring profile, location, activity, session, approval, role, or grant data onto the retained
identity-suppression record.

A release may claim the repository policy as enforced only when real-MySQL tests prove boundary
timestamps, hold behavior, idempotent anonymization, and authorization. An environment restore drill
must separately prove that a restored backup remains isolated until migrations and all retention
work due as of restore time have completed.

## Backups and restored data

Application deletion cannot rewrite an already-created backup. Backup access, encryption,
immutability, restore authorization, and disposal therefore remain environment controls. Future
production backups have a proposed 35-day default expiry; it is **not active** until the external
environment owner approves and implements it. Legal or incident holds may supersede disposal only
through the documented hold process.

The local recovery artifacts created during the 2026-07-20 MySQL/migration rehearsal have an
approved expiry of **2026-08-03**. Before that date the workstation owner must inventory
`kingfisher_pre84_backup_20260720`, `kingfisher_pre0014_backup_20260720`,
`kingfisher_pre0017_backup_20260720`, `kingfisher_pre0019_backup_20260720`, and the isolated
upgraded source volume `kingfisher_mysql_data`. The pre-0019 cold snapshot contains 200 files whose
aggregate hashes were verified byte-for-byte against its source. On or after the
expiry, remove them through an approved, target-verified disposal procedure unless a documented
hold extends a specific artifact. Record artifact name, deletion time, operator, and outcome. This
policy does not authorize an application process to delete Docker volumes.

## Review

Review this policy at least annually and whenever data collection, identity providers, legal
requirements, backup design, or deployment topology changes. Link approved exceptions and evidence
from the release record; do not place sensitive case details in application settings or source
control.
