# Legal document bundle and login acceptance

- Implementation status: versioned acceptance control
- Legal-text status: counsel-review draft; not approved for production use
- Current bundle: `2026-07-20-v1`
- Scope: one Kingfisher deployment operated for one organization

Kingfisher presents four public documents as one versioned login bundle:

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
immutable snapshot of the bundle/document versions and canonical content digests asserted by the
client release. Bounded request context supports dispute investigation without storing identity-
provider tokens or document text. The official web build verifies those digests against its
canonical content and fails closed on a manifest mismatch; the approved rendered artifact must
still be retained outside the mutable deployment.
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
effective date, title, summary, and ordered sections; every section includes its heading and ordered
paragraph/bullet arrays. Navigation routes and visual styling are excluded. A frontend invariant
recomputes SHA-256 from that canonical form, while the API manifest and acceptance row carry the
same lowercase digest. Any text change without a digest update fails the invariant, and any
manifest/content digest mismatch causes the official web client to disable sign-in. The release
process separately requires a new version for changed legal text.

## Information required before approval

The checked-in legal text deliberately does not invent facts. The deployment owner and qualified
counsel must supply and approve at least:

1. the legal name of the operator and any trading name;
2. a mailing address and support, legal-notice, and privacy contact channels;
3. the intended users, minimum age, and whether access is employee-only, customer-only, or public;
4. the product/service description, paid-plan and refund terms if any, service levels, and support
   commitments;
5. governing law, exclusive or non-exclusive venue, dispute process, and any arbitration or class-
   action terms;
6. the operator's privacy role in each deployment, applicable jurisdictions, lawful bases,
   subprocessors/recipients, international transfers, and required regional disclosures;
7. approved retention periods for legal-acceptance evidence and any statutory records; and
8. warranty, liability-cap, indemnity, sanctions/export, and termination language appropriate to
   the operator, users, and jurisdictions.

Until those inputs are approved, the public documents must retain their prominent draft banner and
must not be represented as a finished production contract or jurisdiction-specific privacy notice.

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
