export const LEGAL_BUNDLE_VERSION = "2026-07-20-v1" as const;
export const LEGAL_EFFECTIVE_DATE_ISO = "2026-07-20" as const;
export const LEGAL_EFFECTIVE_DATE = "July 20, 2026" as const;
export const LEGAL_ACCEPTANCE_LABEL =
  "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge the Privacy Notice." as const;

export type LegalManifestDocumentKey = "terms" | "eula" | "acceptable_use" | "privacy";

export interface LegalManifestDocument {
  key: LegalManifestDocumentKey;
  title: string;
  version: string;
  sha256: string;
  url: string;
  acceptance: "agreement" | "acknowledgement";
}

export interface LegalManifest {
  bundle_version: string;
  effective_date: string;
  required_at_login: boolean;
  acceptance_label: string;
  documents: LegalManifestDocument[];
}

export type LegalDocumentKey = "terms" | "eula" | "privacy" | "aup";

export interface LegalSection {
  heading: string;
  paragraphs?: string[];
  bullets?: string[];
}

export interface LegalDocumentDefinition {
  version: string;
  title: string;
  shortTitle: string;
  route: string;
  summary: string;
  sections: LegalSection[];
}

export const LEGAL_DOCUMENTS: Record<LegalDocumentKey, LegalDocumentDefinition> = {
  terms: {
    version: "2026-07-20-v1",
    title: "Terms of Service",
    shortTitle: "Terms",
    route: "/legal/terms-of-service.html",
    summary:
      "These terms describe the conditions for accessing and using this deployment of Kingfisher.",
    sections: [
      {
        heading: "1. Parties and scope",
        paragraphs: [
          "These Terms of Service are between the person or organization using this Kingfisher deployment (\"you\") and the organization that deploys and administers it (the \"Operator,\" \"we,\" or \"us\"). They apply to the hosted service, websites, interfaces, and related support made available by the Operator.",
          "If you use the service for an employer or another organization, you represent that you are authorized to accept these terms for that organization. The EULA, Acceptable Use Policy, and Privacy Notice are part of this legal bundle.",
        ],
      },
      {
        heading: "2. Eligibility and authorized access",
        bullets: [
          "Use the service only if you can form a binding agreement and are authorized by the Operator or your organization.",
          "Provide accurate account information and keep it current.",
          "Do not share credentials, authentication sessions, or access intended for another person.",
          "Notify the Operator promptly if you suspect account compromise or unauthorized access.",
        ],
      },
      {
        heading: "3. Service use and administration",
        paragraphs: [
          "The service provides identity, access-control, user administration, audit, profile, and related operational features. Administrators may configure onboarding, permissions, retention holds, and other controls for their deployment. Your access is limited to the permissions assigned to you and may be changed or revoked by an authorized administrator.",
          "You are responsible for the data, instructions, and configuration you submit and for ensuring that your use complies with applicable law, internal policy, and contractual obligations. You must follow the Acceptable Use Policy.",
        ],
      },
      {
        heading: "4. Data and privacy",
        paragraphs: [
          "You retain whatever rights you have in content and personal data submitted to the service. You grant the Operator the limited rights needed to host, process, transmit, secure, back up, and display that data to operate the service and meet lawful obligations.",
          "The Privacy Notice explains the categories of personal data processed, purposes, retention defaults, choices, and request mechanisms. The Operator must identify its legal basis, required notices, and any customer-specific data-processing terms before production use.",
        ],
      },
      {
        heading: "5. Availability and changes",
        paragraphs: [
          "The Operator may maintain, update, secure, suspend, or modify the service. Planned support levels, maintenance commitments, and service-level commitments—if any—must be stated in a separate written agreement. Material changes to this legal bundle require a new version and renewed acceptance where appropriate.",
        ],
      },
      {
        heading: "6. Suspension and termination",
        paragraphs: [
          "The Operator may suspend or terminate access when reasonably necessary to protect the service or others, respond to a security event or legal requirement, enforce this legal bundle, or follow an authorized customer administrator's instruction. When feasible, the Operator should provide notice and an opportunity to cure.",
          "When access ends, licenses granted to you under this bundle end. Provisions that by their nature should survive—including ownership, confidentiality, disclaimers, and agreed liability terms—continue to apply. Data handling after termination follows the Privacy Notice and any controlling customer agreement.",
        ],
      },
      {
        heading: "7. Ownership and feedback",
        paragraphs: [
          "The software, interfaces, documentation, and associated intellectual property remain owned by their respective rights holders. No rights are granted except those expressly stated in the EULA. If you voluntarily provide feedback, the Operator may use it without restriction or payment, provided it does not identify you or disclose your confidential information without permission.",
        ],
      },
      {
        heading: "8. Warranties, liability, and indemnity",
        paragraphs: [
          "This draft does not set final warranties, remedies, liability caps, exclusions, or indemnity obligations. Those terms depend on the Operator, customer relationship, jurisdiction, insurance, and risk allocation. Qualified counsel must complete and approve them before this document is used as a production agreement.",
        ],
      },
      {
        heading: "9. General terms",
        paragraphs: [
          "A final agreement must identify the Operator, notice method, governing law, forum or dispute process, assignment rules, order of precedence, waiver, severability, and any customer-specific terms. Until completed and approved by counsel, this document is a product template and not a final contract.",
        ],
      },
    ],
  },
  eula: {
    version: "2026-07-20-v1",
    title: "End User License Agreement",
    shortTitle: "EULA",
    route: "/legal/eula.html",
    summary:
      "This EULA describes the limited right to use the Kingfisher software and interfaces.",
    sections: [
      {
        heading: "1. License grant",
        paragraphs: [
          "Subject to this legal bundle and continued authorization by the Operator, you receive a limited, non-exclusive, non-transferable, non-sublicensable, revocable right to access and use the Kingfisher software and documentation solely through the deployment made available to you and solely for authorized business purposes.",
          "The software is licensed, not sold. No ownership interest is transferred to you.",
        ],
      },
      {
        heading: "2. License restrictions",
        bullets: [
          "Do not copy, sell, rent, lease, sublicense, distribute, or commercially exploit the software except as expressly authorized in writing.",
          "Do not bypass authentication, authorization, rate limits, security controls, or technical restrictions.",
          "Do not reverse engineer, decompile, or disassemble the software except where applicable law expressly permits that activity despite this restriction.",
          "Do not remove ownership, attribution, security, or legal notices.",
          "Do not use the software to build or train a competing product through unauthorized access, scraping, or extraction.",
        ],
      },
      {
        heading: "3. Authorized users and devices",
        paragraphs: [
          "Your license is tied to your authorized account. You are responsible for activity under that account and for using supported, reasonably secured devices and browsers. Automated access is prohibited unless an authorized integration or written agreement expressly permits it.",
        ],
      },
      {
        heading: "4. Updates and dependencies",
        paragraphs: [
          "The Operator may deploy patches, upgrades, security fixes, and feature changes. The software may include third-party and open-source components governed by their own license notices; those licenses control where they conflict with this EULA for the applicable component.",
        ],
      },
      {
        heading: "5. Data and telemetry",
        paragraphs: [
          "The software processes account, security, request, profile, administrative, and optional location information as described in the Privacy Notice. It must not be represented as collecting advertising telemetry or selling personal data unless the implementation and notice are changed and lawfully approved.",
        ],
      },
      {
        heading: "6. Ownership and reservation of rights",
        paragraphs: [
          "All rights not expressly granted are reserved by the applicable rights holders. Product names, branding, source code, object code, designs, and documentation may be protected by intellectual-property laws and contractual obligations.",
        ],
      },
      {
        heading: "7. Term and termination",
        paragraphs: [
          "This license begins when you accept the current legal bundle and ends when your account authorization ends or the agreement is terminated. On termination, stop using the software. Suspension or termination does not authorize the destruction of records that must be retained under law, an approved retention hold, or a controlling customer agreement.",
        ],
      },
      {
        heading: "8. Warranty and liability terms require completion",
        paragraphs: [
          "No deployment-specific warranty, support commitment, liability cap, remedy, export-control representation, or governing law is established by this draft. The Operator's counsel must supply and approve those terms before production use.",
        ],
      },
    ],
  },
  privacy: {
    version: "2026-07-20-v1",
    title: "Privacy Notice",
    shortTitle: "Privacy",
    route: "/legal/privacy-notice.html",
    summary:
      "This notice explains how a Kingfisher deployment processes personal data and the controls available to users.",
    sections: [
      {
        heading: "1. Who is responsible",
        paragraphs: [
          "The organization that deploys and administers this Kingfisher instance is the Operator and ordinarily determines why and how personal data is processed. A customer organization may instead act as controller and direct the Operator as its processor or service provider. The final notice must identify those roles, the responsible entities, and an approved privacy contact before production use.",
        ],
      },
      {
        heading: "2. Data the service processes",
        bullets: [
          "Identity data from the selected sign-in provider, including provider identifier, signed provider namespace where applicable, email address, and basic account claims.",
          "Profile and workplace data you or an administrator supplies, such as display name, title, department, phone, biography, timezone, and interface preferences.",
          "Authentication and security data, including session identifiers, timestamps, IP address, user agent, nonce and token replay defenses, access decisions, and security events.",
          "Request and administrative activity needed to operate, secure, investigate, and audit the service.",
          "Optional precise device location only when you separately opt in and grant browser permission, plus a derived place or timezone when supported.",
          "Privacy requests, erasure scheduling state, retention-hold state, and the audit evidence needed to administer those controls.",
          "Legal acceptance evidence, including bundle and document versions, acceptance time, and—until account anonymization—source IP address, user agent, and request identifier.",
        ],
      },
      {
        heading: "3. Why data is used",
        bullets: [
          "Authenticate users, manage accounts and permissions, and provide requested product functions.",
          "Protect accounts, prevent replay and abuse, investigate incidents, and enforce policy.",
          "Maintain service reliability, troubleshoot failures, and preserve accountable administrative records.",
          "Honor access, correction, location-clearing, and erasure requests when applicable.",
          "Comply with lawful obligations and establish, exercise, or defend legal claims.",
        ],
        paragraphs: [
          "The Operator must document the lawful basis for each purpose under the laws that apply to its deployment. Consent to this legal bundle is not a substitute for a required privacy-law basis.",
        ],
      },
      {
        heading: "4. Location choices",
        paragraphs: [
          "Location sharing is optional and separate from legal acceptance. The service requests browser permission only after you select the location option. You can deny browser permission, leave the option unchecked, or clear stored location from privacy controls without losing ordinary account access.",
        ],
      },
      {
        heading: "5. Retention and deletion",
        paragraphs: [
          "The default lifecycle clears precise location after 24 hours; anonymizes eligible pending accounts after 30 days; removes expired or revoked session records after 30 days; removes request activity after 90 days; schedules eligible user erasure after a 30-day grace period; anonymizes eligible disabled accounts after 365 days; and removes security events after 400 days. Generated exports are not retained by the application. Backups and external systems may follow separately documented schedules.",
          "Acceptance evidence is provisionally retained for 2,555 days (approximately seven years) and includes the accepted document versions and acceptance time. On account anonymization, source IP address, user agent, and request identifier are removed; the evidence keeps a pseudonymous internal link to the anonymized user tombstone. This period is a conservative product default—not a legal conclusion—and must be reviewed and configured by counsel for the deployment's actual obligations.",
          "An authorized retention hold pauses covered lifecycle deletion when records must be preserved for an investigation, legal obligation, or other documented purpose. Administrators are not automatically erased, and identity tombstones may retain the minimum pseudonymous technical keys needed to prevent unsafe account recreation. The final deployment notice must reconcile these defaults with actual infrastructure, law, and customer agreements.",
        ],
      },
      {
        heading: "6. Sharing and disclosures",
        paragraphs: [
          "Data may be available to authorized administrators and service personnel according to their permissions, to infrastructure and identity providers needed to operate the deployment, to a customer organization directing the service, and to authorities or other parties when lawfully required or necessary to protect rights and safety. The Operator must maintain a deployment-specific provider list and any required data-processing agreements.",
          "This product is not designed to sell personal data or use it for behavioral advertising. The Operator must update this notice before introducing materially different uses.",
        ],
      },
      {
        heading: "7. Cookies and local storage",
        paragraphs: [
          "The service uses a first-party session cookie to keep you authenticated and stores saved interface preferences with your account. Provider sign-in may use provider-controlled browser storage or cookies under that provider's own notice. The product does not require advertising cookies.",
        ],
      },
      {
        heading: "8. Your choices and requests",
        paragraphs: [
          "Depending on applicable law and the Operator's role, you may have rights to access, correct, delete, restrict, object, receive a copy, or appeal a decision concerning personal data. Available in-product privacy controls let eligible users inspect the active retention policy, clear location, request erasure, and cancel a pending erasure during its grace period.",
          "The Operator must publish an authenticated and an alternative contact path for privacy and accessibility requests. Requests may require identity verification, may be limited by law or another person's rights, and should receive a documented response within the applicable deadline.",
        ],
      },
      {
        heading: "9. Security and international processing",
        paragraphs: [
          "Kingfisher includes access controls, server-side identity verification, revocable sessions, audit events, and lifecycle controls. No system can guarantee absolute security. The Operator is responsible for production hosting, transport and database encryption, backups, monitoring, incident response, and access governance.",
          "Hosting locations and cross-border transfers depend on the deployment. The final notice must identify relevant locations and lawful transfer safeguards before personal data is transferred internationally.",
        ],
      },
      {
        heading: "10. Children, changes, and contact",
        paragraphs: [
          "The service is intended for authorized organizational users, not children acting independently. The Operator must set and disclose any jurisdiction-specific minimum age.",
          "Material notice changes require a new version and appropriate notice or renewed acknowledgment. The final notice must provide the Operator's legal name, address, privacy contact, effective date, and any regulator or representative details required by applicable law.",
        ],
      },
    ],
  },
  aup: {
    version: "2026-07-20-v1",
    title: "Acceptable Use Policy",
    shortTitle: "Acceptable Use",
    route: "/legal/acceptable-use-policy.html",
    summary:
      "This policy defines prohibited conduct intended to protect users, systems, and data.",
    sections: [
      {
        heading: "1. Use lawfully and as authorized",
        paragraphs: [
          "Use the service only for legitimate, authorized purposes and in compliance with applicable law, the Terms of Service, the EULA, organizational policy, and your assigned permissions. Do not use access to interfere with another person's rights or evade a legal or contractual obligation.",
        ],
      },
      {
        heading: "2. Security abuse is prohibited",
        bullets: [
          "Do not probe, scan, exploit, disrupt, or test a system or account without express written authorization and an agreed scope.",
          "Do not introduce malware, destructive code, credential theft, phishing, spam, denial-of-service traffic, or mechanisms intended to impair or secretly control systems.",
          "Do not bypass authentication, authorization, tenant boundaries, rate limits, retention controls, logging, or other safeguards.",
          "Do not obtain, use, disclose, or retain credentials, tokens, sessions, encryption material, or data you are not authorized to access.",
          "Do not conceal malicious activity or knowingly submit false security or audit information.",
        ],
      },
      {
        heading: "3. Data and privacy abuse is prohibited",
        bullets: [
          "Do not collect, access, correlate, export, or disclose personal or confidential data without a legitimate purpose and authorization.",
          "Do not use optional location, presence, request, profile, or audit information to stalk, harass, discriminate, or make unlawful decisions.",
          "Do not upload regulated or highly sensitive data unless the Operator has expressly approved that data class and implemented the required safeguards and agreements.",
          "Do not attempt to re-identify anonymized users or defeat deletion, retention, or privacy controls.",
        ],
      },
      {
        heading: "4. Harmful and deceptive conduct is prohibited",
        bullets: [
          "Do not impersonate others, misrepresent authority, commit fraud, or facilitate unlawful activity.",
          "Do not threaten, harass, exploit, or incite harm against a person or protected group.",
          "Do not infringe intellectual-property, confidentiality, privacy, publicity, or other rights.",
          "Do not use the service to distribute illegal content or material designed to facilitate serious wrongdoing.",
        ],
      },
      {
        heading: "5. Protect service integrity",
        bullets: [
          "Do not place unreasonable load on the service or use automation, scraping, bulk extraction, or integrations unless expressly authorized.",
          "Do not resell access or allow unauthorized people to use your account.",
          "Do not manipulate metrics, logs, evidence, workflows, or administrative records to mislead users or investigators.",
        ],
      },
      {
        heading: "6. Reporting and responsible testing",
        paragraphs: [
          "Report suspected vulnerabilities, account compromise, unsafe content, or policy violations through the Operator's approved channel. Do not publicly disclose a vulnerability or access data beyond what is necessary to demonstrate it before coordinated remediation. The Operator must publish a security contact and testing policy before inviting external research.",
        ],
      },
      {
        heading: "7. Enforcement",
        paragraphs: [
          "The Operator may investigate suspected violations, preserve relevant evidence, limit functionality, revoke sessions, suspend or terminate accounts, and notify an affected organization or lawful authority when appropriate. Responses should be proportionate, documented, and consistent with applicable law and controlling agreements.",
          "If you believe enforcement was mistaken, use the appeal or support route the Operator identifies in its finalized policy. This draft does not yet establish a final contact method or response timetable.",
        ],
      },
    ],
  },
};

export const LEGAL_DOCUMENT_ORDER: LegalDocumentKey[] = ["terms", "eula", "aup", "privacy"];

export const LEGAL_CANONICAL_SCHEMA = "kingfisher-legal-document/v1" as const;
export const LEGAL_DOCUMENT_STATUS = "counsel-review-draft" as const;

/**
 * Canonical legal text is compact JSON, encoded as UTF-8 with no trailing newline. Object keys
 * appear in the exact insertion order below; every string is Unicode NFC; missing paragraph or
 * bullet collections become empty arrays. Status, effective date, and the user-visible title,
 * summary, ordered section headings, paragraphs, and bullets are included. Document keys and
 * versions, routes, short navigation labels, bundle metadata, the shared draft banner, and all
 * presentation markup are excluded; versions are bound separately by the manifest and evidence.
 */
export function canonicalizeLegalDocument(key: LegalDocumentKey): string {
  const document = LEGAL_DOCUMENTS[key];
  const nfc = (value: string) => value.normalize("NFC");
  return JSON.stringify({
    schema: LEGAL_CANONICAL_SCHEMA,
    status: LEGAL_DOCUMENT_STATUS,
    effective_date: LEGAL_EFFECTIVE_DATE_ISO,
    title: nfc(document.title),
    summary: nfc(document.summary),
    sections: document.sections.map((section) => ({
      heading: nfc(section.heading),
      paragraphs: (section.paragraphs ?? []).map(nfc),
      bullets: (section.bullets ?? []).map(nfc),
    })),
  });
}

/** SHA-256 of canonicalizeLegalDocument(key), lowercase hexadecimal. */
export const LEGAL_DOCUMENT_SHA256: Record<LegalDocumentKey, string> = {
  terms: "2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e",
  eula: "16bd045a449990e3f7325f0d67d81d4fee54f679ec53164835f0c19725e25638",
  privacy: "fb96f77cc9846282c9555105994d0dc9b400c2a6eaf35e15b390b5a3c5db2d3d",
  aup: "d4391a0abe57885964606521039a4cca0151f8e11d95c628efc51b603eefdb0d",
};

const MANIFEST_DOCUMENT_BINDINGS: Record<
  LegalManifestDocumentKey,
  { documentKey: LegalDocumentKey; acceptance: "agreement" | "acknowledgement" }
> = {
  terms: { documentKey: "terms", acceptance: "agreement" },
  eula: { documentKey: "eula", acceptance: "agreement" },
  acceptable_use: { documentKey: "aup", acceptance: "agreement" },
  privacy: { documentKey: "privacy", acceptance: "acknowledgement" },
};

/** Fail closed when the server requires legal content this web build cannot display verbatim. */
export function isSupportedLegalManifest(manifest: LegalManifest): boolean {
  if (
    manifest.bundle_version !== LEGAL_BUNDLE_VERSION ||
    manifest.effective_date !== LEGAL_EFFECTIVE_DATE_ISO ||
    manifest.required_at_login !== true ||
    manifest.acceptance_label !== LEGAL_ACCEPTANCE_LABEL ||
    !Array.isArray(manifest.documents) ||
    manifest.documents.length !== Object.keys(MANIFEST_DOCUMENT_BINDINGS).length
  ) {
    return false;
  }

  return (Object.keys(MANIFEST_DOCUMENT_BINDINGS) as LegalManifestDocumentKey[]).every((key) => {
    const binding = MANIFEST_DOCUMENT_BINDINGS[key];
    const expected = LEGAL_DOCUMENTS[binding.documentKey];
    const actual = manifest.documents.find((document) => document.key === key);
    return (
      actual?.title === expected.title &&
      actual.version === expected.version &&
      actual.sha256 === LEGAL_DOCUMENT_SHA256[binding.documentKey] &&
      actual.url === expected.route &&
      actual.acceptance === binding.acceptance
    );
  });
}
