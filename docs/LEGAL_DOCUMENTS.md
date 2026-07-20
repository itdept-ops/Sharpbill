# Legal document bundle and login acceptance

- Implementation status: versioned acceptance control
- Legal-text status: counsel-review draft; not approved for production use
- Current bundle: `2026-07-20-v2`
- Scope: one KingFisher deployment operated for one organization

KingFisher presents four public documents as one versioned login bundle:

| Document | User action | Public route |
|---|---|---|
| Terms of Service | Agree | `/legal/terms-of-service.html` |
| End User License Agreement (EULA) | Agree | `/legal/eula.html` |
| Acceptable Use Policy | Agree | `/legal/acceptable-use-policy.html` |
| Privacy Notice | Acknowledge receipt | `/legal/privacy-notice.html` |

The official login flow presents the Privacy Notice link and records the user's acknowledgement.
That acknowledgement is not blanket consent to every form of personal-data processing, marketing,
or optional collection. Features that need a separate consent or choice must continue to obtain it
at the point of collection; the existing browser-mediated location choice is one example.

## Acceptance control

The browser retrieves the public legal manifest and keeps all sign-in paths unavailable until the
person checks an initially unchecked box. Each login request sends both an explicit acceptance flag
and the manifest's exact bundle version. The API rejects missing acceptance and stale or unknown
versions, so an obsolete client cannot obtain a session with an old bundle. A custom API client can
still assert `legal_accepted=true` without rendering the official UI; no server can prove that a
person read text merely from a request field. The record is therefore evidence of an authenticated
client assertion, not proof of reading or visual presentation.

Successful acceptance evidence is a server timestamp tied to the authenticated account and an
immutable snapshot of the bundle effective date, bundle/document versions, canonical content
digests, exact checkbox statement, and each document's agreement-or-acknowledgement action.
Bounded request context supports dispute investigation without storing identity-provider tokens or
document text. The official web build verifies those digests against its canonical content and
fails closed on a manifest mismatch; the approved rendered artifact must still be retained outside
the mutable deployment.
Failed identity verification, rejected admission, disabled accounts, and pending accounts do not
become successful acceptance records and do not receive a session.

Account anonymization removes acceptance request metadata that could identify a device or network.
The remaining fact retains the bundle/version/timestamp and the internal link to the already
anonymized account tombstone, so it remains pseudonymous evidence rather than anonymous data. The
repository's provisional default is 2,555 days (seven years), with hold-aware expiry. The deployment
owner must have counsel approve that period, the residual link, and any legal-hold exception before
production use.

Each record receives an expiry when it is created. A later configuration reduction is also applied
as an upper bound to existing cohorts, so shortening the policy takes effect without retaining the
old deadline. Increasing the setting does not silently extend existing evidence; a true extension
requires a separately approved, hold-aware rebaseline with owner, purpose, and expiry evidence.

### Canonical content digest

Digest schema `kingfisher-legal-document/v1` serializes UTF-8 JSON with the document's status,
effective date, title, summary, shared draft warning, and ordered sections; every section includes
its heading and ordered paragraph/bullet arrays. Navigation routes and visual styling are excluded.
A frontend invariant recomputes SHA-256 from that canonical form, while the API manifest and
acceptance row carry the same lowercase digest. Any text or material shared-warning change without
a digest update fails the invariant, and any manifest/content digest mismatch causes the official
web client to disable sign-in. The release process separately requires a new version for changed
legal text.

## Owner-selected v2 defaults

The owner supplied the business facts below and delegated conservative Oregon defaults. They are
implemented in bundle `2026-07-20-v2`, but remain counsel-review draft choices rather than legal
approval:

| Topic | v2 selection |
|---|---|
| Operator | `KingFisher`, with no unverified LLC/corporation suffix |
| Location | Hillsboro, Oregon, United States |
| Draft contacts | `legal@kingfisher.com`, `privacy@kingfisher.com`, and `support@kingfisher.com` |
| Audience | Authorized single-tenant organizational users who are at least 18 |
| Commercial model | Free service; no paid plans, billing, refunds, renewals, or service credits |
| Service level | Best effort; no contractual uptime, support-response, recovery, or maintenance SLA |
| Disputes | Conspicuous Oregon choice of law; Washington County state courts or, with federal subject-matter jurisdiction, the District of Oregon, Portland Division; no mandatory arbitration or contractual class-action waiver |
| Privacy | Dataset-specific controller/processor roles; U.S.-oriented deployment; no sale, targeted advertising, or qualifying significant-effect profiling; separate precise-location choice |
| Precise-location retention | Repository default of 24 hours, configurable from 1 through 720 hours; each capture stores its own deadline, policy reductions shorten existing captures, and policy increases do not extend an earlier deadline |
| Acceptance evidence | Repository default of 2,555 days, configurable from 1 through 3,650 days; reductions affect existing cohorts and increases do not silently extend them; pending counsel approval |
| Risk allocation | As-is warranty disclaimer; indirect-damages exclusion; greater of US $100 or prior-12-month fees aggregate cap; organization-scoped third-party-claim indemnity with an authorized-user limitation |
| Other | U.S. export/sanctions restriction, proportionate suspension/termination, assignment, severability, waiver, force-majeure, and entire-agreement terms |

### Remaining production release gates

1. Confirm the complete registered legal-entity name and legal form, if any.
2. Replace the city/state reference with a complete mailing and service-of-process address.
3. Prove authorized control and active monitoring of every contact mailbox or replace it with an
   address on a controlled domain. `kingfisher.com` is used by the unrelated UK company Kingfisher
   plc, so the supplied addresses are unsafe to publish as operative channels without proof.
4. Inventory and approve the exact hosting/database/backup/monitoring providers, identity providers,
   subprocessors, regions, international processing, and any required data-processing terms.
5. Confirm the actual controller/processor split for the production dataset and the operational
   workflow for email rights requests and appeals. Execute a separate compliant data-processing
   agreement before representing KingFisher as a processor for another controller.
6. Have qualified Oregon counsel assess the warranty disclaimer, US $100 liability cap, indemnity,
   venue, evidence-retention period, and any non-waivable laws for the actual audience.
7. Verify that the selected insurance, incident response, backup, security, and support practices
   match every public statement before removing the draft banner.
8. Either keep precise location disabled or add durable consent/notice-version evidence before
   relying on KingFisher as an auditable consent system; the official UI choice and browser
   permission are not independently persisted by the server.

Until those gates close, the public documents must retain their prominent draft banner and must not
be represented as a finished contract or jurisdiction-specific legal opinion.

## Bundle history

- `2026-07-20-v1` was the initial generic counsel-review draft, preserved by source commit
  `92105a9`. No local acceptance evidence existed when v2 work began.
- `2026-07-20-v2` is a substantive replacement populated with the owner-selected Oregon facts and
  forces renewed acceptance. Alembic `0021` adds the exact displayed statement/action semantics and
  per-capture location deadline to the evidence architecture.

Final canonical SHA-256 digests for v2 (including the shared draft warning) are:

| Document | SHA-256 |
|---|---|
| Terms of Service | `f5a30fded3b6b4715f13d0711c9168dd643aac48ff14164e95bc7610734fb912` |
| End User License Agreement | `2715b0daa99c2a553b08448eb81307affcfd2ca5ece005563eb4ad83d7fae6b3` |
| Acceptable Use Policy | `1290bb3dbcf3b79fb2051693ae7be6898b421daf24af1ddb037098cc1ee07217` |
| Privacy Notice | `53e22a3bff270fb2215631f061cd001f89a96971e6fa3bb8374ff2f829931695` |

## Publishing a replacement bundle

Treat a published version as immutable. Do not silently edit legal text while reusing its version.
For every substantive replacement:

1. obtain legal and product-owner approval and retain the approval record outside source control;
2. assign a new bundle version and a version to every changed document;
3. recompute every changed document's canonical SHA-256 digest and update the public pages, digest
   invariant, API manifest, and evidence snapshot in the same change;
4. add migration or application changes if the evidence schema has changed;
5. test that unchecked, missing, and stale acceptance fail closed for every login provider;
6. verify the public pages, keyboard flow, link destinations, and mobile layout;
7. deploy the API and web artifacts as one release, then verify the manifest served by the target;
8. retain an immutable copy of the approved rendered documents and their source revision; and
9. record release time, approvers, deployment, and rollback decision.

A typo-only correction can still affect interpretation. Counsel should decide whether it receives a
new document and bundle version; engineering must not make that classification alone.

## Review ownership

Product owns accurate service behavior, privacy owns the data inventory and rights workflow,
security owns evidence protection and access, legal counsel owns the contract and jurisdictional
language, and the environment operator owns publication and retention. Review the bundle at least
annually and whenever the operator, audience, data use, provider, pricing, jurisdiction, or product
scope changes.
