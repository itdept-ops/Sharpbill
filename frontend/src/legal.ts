export const LEGAL_BUNDLE_VERSION = "2026-07-20-v2" as const;
export const LEGAL_EFFECTIVE_DATE_ISO = "2026-07-20" as const;
export const LEGAL_EFFECTIVE_DATE = "July 20, 2026" as const;
export const LEGAL_OPERATOR_NAME = "KingFisher" as const;
export const LEGAL_OPERATOR_LOCATION = "Hillsboro, Oregon, United States" as const;
export const LEGAL_EMAILS = {
  legal: "legal@kingfisher.com",
  privacy: "privacy@kingfisher.com",
  support: "support@kingfisher.com",
} as const;
export const LEGAL_ACCEPTANCE_LABEL =
  "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge the Privacy Notice." as const;
export const LEGAL_DRAFT_WARNING =
  "Owner-selected Oregon terms are populated, but this is not legal advice or a final agreement. Before production use, KingFisher must obtain qualified counsel review, confirm its complete legal entity and mailing address, and verify or replace the unconfirmed kingfisher.com contact mailboxes." as const;

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
  precise_location_retention_hours: number;
  legal_acceptance_retention_days: number;
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
    version: "2026-07-20-v2",
    title: "Terms of Service",
    shortTitle: "Terms",
    route: "/legal/terms-of-service.html",
    summary:
      "These terms describe the conditions for accessing and using this deployment of KingFisher.",
    sections: [
      {
        heading: "1. Parties and scope",
        paragraphs: [
          "These Terms of Service are between KingFisher (\"KingFisher,\" \"we,\" \"us,\" or the \"Operator\"), based in Hillsboro, Oregon, United States, and the person or organization using the KingFisher service (\"you\"). They govern the single-tenant hosted service, websites, interfaces, documentation, and support channels KingFisher makes available.",
          "These Terms bind you as an individual user. They bind an employer or other organization only when a representative with actual authority accepts them for that organization or the organization enters a separate written agreement. Ordinary authorization to use an account does not itself mean authority to bind the organization. The EULA and Acceptable Use Policy are incorporated contractual terms. The Privacy Notice is provided for acknowledgement and transparency; acknowledging it does not turn every privacy disclosure into consent or expand KingFisher's processing rights.",
          "The owner has designated support@kingfisher.com for support and legal@kingfisher.com for legal notices in this draft, with correspondence addressed to KingFisher, Hillsboro, Oregon, United States. Before production publication, KingFisher must verify authorized control and active monitoring of those mailboxes or replace them; until then, they are draft contact details and must not be relied on for time-sensitive notices.",
        ],
      },
      {
        heading: "2. Eligibility and authorized access",
        bullets: [
          "You must be at least 18 years old, able to form a binding agreement, and authorized by KingFisher or your organization to use the service.",
          "Provide accurate account information and keep it current.",
          "Do not share credentials, authentication sessions, or access intended for another person.",
          "Promptly use the verified support channel published by KingFisher if you suspect account compromise or unauthorized access.",
        ],
      },
      {
        heading: "3. Free service, use, and administration",
        paragraphs: [
          "KingFisher currently provides the service without subscription fees, paid plans, automatic renewals, or purchase commitments. There are therefore no payment, billing, refund, or service-credit terms. KingFisher will present a replacement agreement before introducing a paid plan.",
          "The service provides identity, access-control, user administration, audit, profile, and related operational features. Administrators may configure onboarding, permissions, retention holds, and other controls for their deployment. Your access is limited to the permissions assigned to you and may be changed or revoked by an authorized administrator.",
          "You are responsible for the data, instructions, and configuration you submit and for ensuring that your use complies with applicable law, internal policy, and contractual obligations. You must follow the Acceptable Use Policy.",
        ],
      },
      {
        heading: "4. Data and privacy",
        paragraphs: [
          "You retain whatever rights you have in content and personal data submitted to the service. You grant KingFisher the limited rights needed to host, process, transmit, secure, back up, and display that data to operate the service and meet lawful obligations.",
          "The Privacy Notice explains the categories of personal data processed, purposes, retention defaults, choices, and request mechanisms. Legal acceptance is not blanket consent to optional collection; precise location remains subject to a separate affirmative choice and browser permission.",
        ],
      },
      {
        heading: "5. Availability, support, and changes",
        paragraphs: [
          "KingFisher provides the free service on a commercially reasonable, best-effort basis. There is no contractual uptime percentage, guaranteed support response or resolution time, service credit, disaster-recovery objective, or other service-level agreement unless KingFisher signs a separate written agreement that expressly creates one.",
          "KingFisher may maintain, update, secure, suspend, modify, or discontinue all or part of the service. The repository does not provide a general service-change notification workflow, so advance notice is provided only when required by law or through a separately established operational channel. Material changes to this legal bundle receive a new version and are presented for renewed acceptance at a later login.",
        ],
      },
      {
        heading: "6. Suspension and termination",
        paragraphs: [
          "KingFisher may suspend or terminate access when reasonably necessary to protect the service or others, respond to a security event or legal requirement, enforce this legal bundle, or follow an authorized administrator's instruction. Notice or an opportunity to cure is provided only when required by applicable law or a separate written agreement; security or legal urgency may require immediate action.",
          "When access ends, licenses granted to you under this bundle end. Provisions that by their nature should survive—including ownership, disclaimers, liability limits, indemnity, dispute, and retention terms—continue to apply. Data handling after termination follows the Privacy Notice and any controlling customer agreement.",
        ],
      },
      {
        heading: "7. Ownership and feedback",
        paragraphs: [
          "The software, interfaces, documentation, and associated intellectual property remain owned by KingFisher or their respective rights holders. No rights are granted except those expressly stated in the EULA. If you voluntarily provide feedback, KingFisher may use it without restriction or payment, provided it does not identify you or disclose your confidential information without permission.",
        ],
      },
      {
        heading: "8. Warranty disclaimer",
        paragraphs: [
          "To the fullest extent permitted by law, the service is provided \"as is\" and \"as available.\" KingFisher disclaims all express, implied, and statutory warranties, including merchantability, fitness for a particular purpose, title, noninfringement, uninterrupted availability, accuracy, security, and freedom from harmful components.",
          "KingFisher does not warrant that the service will meet every legal, regulatory, records-management, business-continuity, or industry-specific requirement. You are responsible for deciding whether the service is appropriate for your authorized use. Any warranty that cannot lawfully be disclaimed is limited to the minimum scope and duration required by law.",
        ],
      },
      {
        heading: "9. Limitation of liability",
        paragraphs: [
          "To the fullest extent permitted by law, KingFisher and its owners, personnel, affiliates, licensors, and service providers will not be liable for indirect, incidental, special, exemplary, punitive, or consequential damages, or for lost profits, revenue, goodwill, data, use, or business interruption, arising from or related to the service or this legal bundle, even if advised that such damages were possible.",
          "To the fullest extent permitted by law, their aggregate liability for all claims arising from or related to the service or this legal bundle will not exceed the greater of one hundred U.S. dollars (US $100) or the amount you paid KingFisher for the service during the twelve months before the event giving rise to the claim. Because the current service has no paid plans, the US $100 amount ordinarily applies.",
          "These exclusions and limits apply regardless of the theory of liability and do not limit liability that applicable law does not permit the parties to exclude or limit.",
        ],
      },
      {
        heading: "10. Indemnity",
        paragraphs: [
          "To the extent permitted by law, an organization on whose behalf the service is used will defend, indemnify, and hold harmless KingFisher and its owners, personnel, affiliates, licensors, and service providers from third-party claims, damages, losses, judgments, penalties, costs, and reasonable legal fees arising from that organization's submitted data, unauthorized or unlawful use, or material breach of this legal bundle. An individual authorized user acting within assigned permissions does not personally assume the organization's defense obligation; an individual remains responsible for claims caused by that individual's own fraud, willful misconduct, unlawful use, or use outside granted authority. No obligation applies to the extent a claim results from KingFisher's gross negligence, willful misconduct, or violation of law.",
          "KingFisher will provide reasonable notice of a covered claim and allow the indemnifying organization or party to control its defense, subject to KingFisher's right to participate with counsel at its own expense. No settlement may admit fault by KingFisher or impose any monetary or non-monetary obligation on KingFisher without KingFisher's prior written consent.",
        ],
      },
      {
        heading: "11. OREGON CHOICE OF LAW, COURTS, AND GENERAL TERMS",
        paragraphs: [
          "SUBJECT TO ANY NON-WAIVABLE LAW, OREGON LAW GOVERNS THIS LEGAL BUNDLE, WITHOUT REGARD TO CONFLICT-OF-LAW PRINCIPLES. Once KingFisher publishes a verified legal-notice channel, the parties are encouraged to send written notice there and allow 30 days for a good-faith informal resolution before filing a claim, unless urgent injunctive relief is reasonably necessary.",
          "Subject to any non-waivable law, any court proceeding must be brought exclusively in the state courts located in Washington County, Oregon, or, where federal subject-matter jurisdiction exists, the United States District Court for the District of Oregon, Portland Division, and each party consents to personal jurisdiction there. These Terms do not require mandatory arbitration and do not create a contractual waiver of class or representative proceedings.",
          "You may not assign this agreement without KingFisher's written consent. KingFisher may assign it in connection with a merger, reorganization, sale of substantially all relevant assets, or transfer to an affiliate. Neither party is liable for delay caused by events beyond its reasonable control, except for obligations that can still reasonably be performed.",
          "These Terms, the EULA, the Acceptable Use Policy, and any expressly incorporated written agreement are the entire contractual agreement about the service and supersede earlier contractual statements on the same subject. The Privacy Notice remains a notice rather than an agreement to optional processing. If a contractual provision is unenforceable, it will be narrowed to the minimum extent necessary and the remainder will continue. A failure to enforce a provision is not a waiver. Except for the KingFisher parties expressly protected by the warranty, liability, and indemnity provisions, there are no third-party beneficiaries unless an incorporated agreement expressly says otherwise.",
        ],
      },
    ],
  },
  eula: {
    version: "2026-07-20-v2",
    title: "End User License Agreement",
    shortTitle: "EULA",
    route: "/legal/eula.html",
    summary:
      "This EULA describes the limited right to use the KingFisher software and interfaces.",
    sections: [
      {
        heading: "1. License grant",
        paragraphs: [
          "Subject to this legal bundle and continued authorization by KingFisher, you receive a limited, non-exclusive, non-transferable, non-sublicensable, revocable right to access and use the KingFisher software and documentation solely through the single-tenant deployment made available to you and solely for authorized organizational purposes.",
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
          "You must be at least 18 years old. Your license is tied to your authorized account, and you are responsible for activity under that account and for using supported, reasonably secured devices and browsers. Automated access is prohibited unless an authorized integration or written agreement expressly permits it.",
        ],
      },
      {
        heading: "4. Updates and dependencies",
        paragraphs: [
          "KingFisher may deploy patches, upgrades, security fixes, and feature changes. The software may include third-party and open-source components governed by their own license notices; those licenses control where they conflict with this EULA for the applicable component.",
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
        heading: "7. Export and sanctions compliance",
        paragraphs: [
          "You may not access, export, re-export, release, or otherwise use the software in violation of United States export-control, economic-sanctions, or import laws. You represent that you are not a person or entity barred from receiving the software under applicable United States law and will not make it available for a prohibited end use, end user, or destination.",
        ],
      },
      {
        heading: "8. Term and termination",
        paragraphs: [
          "This license begins when you accept the current legal bundle and ends when your account authorization ends or the agreement is terminated. On termination, stop using the software. Suspension or termination does not authorize the destruction of records that must be retained under law, an approved retention hold, or a controlling customer agreement.",
        ],
      },
      {
        heading: "9. Support, warranty, and liability",
        paragraphs: [
          "The software is currently provided without charge and without a service-level agreement, guaranteed support response, maintenance window, uptime commitment, or service credit. After KingFisher verifies or replaces the draft support mailbox, requests may be sent through the published support channel on a best-effort basis.",
          "The warranty disclaimers, liability exclusions, US $100 aggregate liability cap, and indemnity terms in the Terms of Service apply to this EULA and the software to the fullest extent permitted by law.",
        ],
      },
      {
        heading: "10. Governing terms",
        paragraphs: [
          "Oregon law and the exclusive court venues stated in the Terms of Service govern this EULA. The Terms do not require mandatory arbitration or create a contractual class-action waiver. If this EULA conflicts with the Terms of Service, the more specific software-license provision controls; an applicable open-source license controls for its component.",
          "The owner designated legal@kingfisher.com for license questions and formal notices in this draft, addressed to KingFisher, Hillsboro, Oregon, United States. That mailbox, domain authority, complete address, and legal-entity name must be verified or replaced before this EULA is published as operative.",
        ],
      },
    ],
  },
  privacy: {
    version: "2026-07-20-v2",
    title: "Privacy Notice",
    shortTitle: "Privacy",
    route: "/legal/privacy-notice.html",
    summary:
      "This notice explains how a KingFisher deployment processes personal data and the controls available to users.",
    sections: [
      {
        heading: "1. Operator, roles, and contact",
        paragraphs: [
          "KingFisher, based in Hillsboro, Oregon, United States, operates this single-tenant service and determines the core purposes and means for account administration, authentication, security, service operation, legal acceptance, support communications, and privacy-request handling. For those data sets, KingFisher acts as the operator and controller where that legal term applies.",
          "When a separate organization directs the business purposes for profile, directory, workplace, or other organization-submitted data, that organization is ordinarily the controller for those data and KingFisher may act as its processor or service provider. Roles are fact-specific. Production processing in that role requires a separate signed data-processing agreement covering documented instructions, purpose, data types, duration, confidentiality, security, deletion or return, rights assistance, subprocessors, compliance information, and assessment cooperation; this notice alone is not that agreement.",
          "The owner has designated privacy@kingfisher.com for privacy requests, support@kingfisher.com for support, and legal@kingfisher.com for legal notices in this draft. Before production publication, KingFisher must verify authorized control and active monitoring of each mailbox or replace it. Postal correspondence should identify KingFisher, Hillsboro, Oregon, United States; a complete service-of-process address and legal-entity form remain subject to counsel confirmation.",
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
          "Legal acceptance evidence, including the bundle effective date; bundle and document versions; canonical content digests; the exact checkbox statement and agreement-or-acknowledgement action for each document; acceptance time; and—until account anonymization—source IP address, user agent, and request identifier.",
        ],
      },
      {
        heading: "3. Purposes and legal grounds",
        bullets: [
          "Authenticate users, manage accounts and permissions, and provide requested product functions.",
          "Protect accounts, prevent replay and abuse, investigate incidents, and enforce policy.",
          "Maintain service reliability, troubleshoot failures, and preserve accountable administrative records.",
          "Honor access, correction, location-clearing, and erasure requests when applicable.",
          "Comply with lawful obligations and establish, exercise, or defend legal claims.",
        ],
        paragraphs: [
          "Depending on the data and applicable law, KingFisher processes data as needed to provide the requested service and perform this agreement, for legitimate interests in administering and securing the service, to comply with law, and to establish or defend legal claims. An organization directing workplace or customer-data use is responsible for its own lawful basis and notices.",
          "Legal-bundle acceptance is not blanket consent to personal-data processing. Future location capture occurs only when you select the initially unchecked location option for that login and grant browser permission. You may stop future capture by leaving the option unchecked and revoking browser permission. The in-product clear action ordinarily removes stored precise and derived location immediately, but a documented legal hold can temporarily block deletion. Where Oregon consent rules apply, withdrawal is intended to be at least as easy as consent and processing must stop as soon as practicable and no later than 15 days after withdrawal.",
        ],
      },
      {
        heading: "4. Location choices",
        paragraphs: [
          "Location sharing is optional and separate from legal acceptance. Precise location can be sensitive data under Oregon law. Before selection, the login page shows the active configured precise-location period. The service requests browser permission only after you select the option. Precise coordinates become eligible for bounded clearing after that period unless held; derived place and timezone remain for the active account until you clear them or an account lifecycle removes them. You can deny or revoke browser permission, leave the option unchecked, or clear stored precise and derived location without losing ordinary account access.",
          "KingFisher does not sell precise location, use it for targeted advertising, or use it to make decisions that produce legal or similarly significant effects.",
          "The official web client presents the separate location choice, but the server currently does not retain a distinct location-consent receipt or notice version and an authenticated custom client can call the location endpoint directly. A deployment that needs independently auditable consent evidence must add that control before enabling location; browser permission and this application's request record alone do not prove informed consent.",
        ],
      },
      {
        heading: "5. Retention and deletion",
        paragraphs: [
          "The repository default makes precise coordinates eligible for bounded clearing after 24 hours, with an allowed deployment setting from 1 through 720 hours; the active value appears before location selection and in-product. Other defaults anonymize eligible pending accounts after 30 days; remove expired or revoked session records after 30 days; remove request activity after 90 days; schedule eligible user erasure after a 30-day grace period; anonymize eligible disabled accounts after 365 days; and remove security events after 400 days. A documented legal hold pauses governed deletion. Generated exports are not retained by the application. Backups and external systems may follow separately documented schedules.",
          "The repository default retains legal-acceptance evidence for 2,555 days (approximately seven years), with an allowed deployment setting from 1 through 3,650 days; the active policy appears in-product. A reduction also shortens existing cohorts, while an increase does not extend an expiry already recorded. On account anonymization, source IP address, user agent, and request identifier are removed; the evidence keeps a pseudonymous internal link to the anonymized user tombstone. KingFisher selected 2,555 days for contract-evidence continuity, but counsel must confirm or shorten it for the deployment's actual obligations.",
          "An authorized retention hold pauses covered lifecycle deletion when records must be preserved for an investigation, legal obligation, or other documented purpose. Administrators are not automatically erased, and identity tombstones may retain the minimum pseudonymous technical keys needed to prevent unsafe account recreation. Application backups and separately operated infrastructure may follow documented schedules not controlled by the application; after KingFisher publishes a verified privacy channel, it may be used to request the current deployment-specific schedule.",
        ],
      },
      {
        heading: "6. Service providers and other disclosures",
        paragraphs: [
          "Authorized organizational administrators may receive account, profile, workplace, presence, administrative, and permissioned location information needed for their duties. A configured identity provider such as Google or Microsoft receives the sign-in request, nonce, and related protocol data and returns verified identity claims. Hosting, database, network, backup, monitoring, and security providers may process the categories in section 2 only as operationally necessary. KingFisher personnel, professional advisers, a lawful organizational successor, and authorities may receive relevant categories when needed for support, security, legal advice, a transaction, legal compliance, or protection of rights and safety.",
          "If an authorized user chooses the external \"Open in Google Maps\" link for stored coordinates, the user's browser sends those coordinates to Google at the user's direction, and Google's terms and privacy notice govern that separate service.",
          "Service providers are expected to process data for the contracted operational purpose and under applicable confidentiality, security, and data-processing terms. Exact provider identities, subprocessors, hosting regions, and international processing locations remain deployment release facts that must be inventoried and approved before production use. After verifying a request, KingFisher will provide any specific third-party information required by applicable law.",
          "KingFisher does not sell personal data, use personal data for targeted advertising, or profile users in furtherance of decisions that produce legal or similarly significant effects. Because those activities are not performed, there is currently no corresponding sale, targeted-advertising, or qualifying-profiling opt-out to exercise. KingFisher must update this notice and implement any required opt-out mechanism before introducing such a use.",
        ],
      },
      {
        heading: "7. Cookies and local storage",
        paragraphs: [
          "The service uses a first-party session cookie to keep you authenticated and stores saved interface preferences with your account. Provider sign-in may use provider-controlled browser storage or cookies under that provider's own notice. The product does not require advertising cookies.",
        ],
      },
      {
        heading: "8. Your choices, rights, and appeals",
        paragraphs: [
          "Depending on applicable law and KingFisher's role, you may have rights to confirm processing; access, correct, delete, or receive a portable copy of personal data; withdraw consent; or opt out of certain processing. When the Oregon Consumer Privacy Act applies, a covered consumer may request the specific third parties, other than natural persons, that received that consumer's personal data or, at KingFisher's option, the specific third parties that received any personal data. The Act's definition of consumer excludes a person acting in a commercial or employment context; other laws and voluntary in-product controls may still apply.",
          "Available in-product privacy controls let eligible account users inspect the active retention policy, clear location, request erasure, and cancel a pending erasure during its grace period. Those controls may not expose every backend record. Once the draft privacy mailbox is verified or replaced, anyone may submit a broader request through the published privacy contact; using an existing account email can assist authentication, but KingFisher will not require creation of an account solely to make a request. An authorized agent may submit a request where applicable law permits, subject to commercially reasonable verification of identity and authority.",
          "KingFisher will respond within the period required by applicable law. When the Oregon Consumer Privacy Act applies, KingFisher will respond without undue delay and no later than 45 days after receipt. When reasonably necessary, KingFisher may extend once for 45 additional days by notifying the requester and explaining the reason during the initial period. A requester may appeal a denial through the verified published privacy contact with the subject \"Privacy Appeal\"; KingFisher will decide the appeal within 45 days and provide written reasons. If the appeal is denied, the response will explain how to submit a complaint to the Oregon Attorney General. A request will be denied or limited only as applicable law permits, with the required justification.",
        ],
      },
      {
        heading: "9. Security, hosting, and international processing",
        paragraphs: [
          "The repository includes access controls, server-side identity verification, revocable sessions, audit events, and lifecycle controls. No system can guarantee absolute security. Before production use, the environment operator must validate the actual hosting, transport and database encryption, backups, monitoring, incident response, access governance, vendor contracts, and legally required breach-notification process; this application does not itself prove those external controls.",
          "KingFisher is based in Oregon and this notice is written for an intended United States-oriented organizational deployment. Onboarding does not technically restrict users by country, so that intended audience is not a geographic control. Exact hosting regions, provider processing locations, and international transfers remain release facts to inventory. The operator should not admit non-U.S. users or intentionally import non-U.S. personal data until the relevant roles, notices, contracts, and transfer safeguards are documented.",
        ],
      },
      {
        heading: "10. Age, changes, and contact",
        paragraphs: [
          "The service is limited to authorized users who are at least 18 years old. KingFisher does not knowingly permit minors to create or use accounts. Once KingFisher publishes a verified privacy channel, use it if you believe a person under 18 has provided personal data.",
          "KingFisher will version material notice changes and provide appropriate notice or renewed acknowledgement before the changed bundle governs a later login. Prior acceptance evidence remains tied to the exact earlier document versions and content digests.",
          "Draft contact details: KingFisher, Hillsboro, Oregon, United States; privacy@kingfisher.com for privacy matters; support@kingfisher.com for service help; and legal@kingfisher.com for legal notices. The city/state address, KingFisher name, domain authority, and mailbox control are owner-supplied but unverified. Counsel must confirm the complete legal-entity name and service-of-process address, and the owner must prove authorized control and monitoring of each mailbox or replace it, before production publication.",
        ],
      },
    ],
  },
  aup: {
    version: "2026-07-20-v2",
    title: "Acceptable Use Policy",
    shortTitle: "Acceptable Use",
    route: "/legal/acceptable-use-policy.html",
    summary:
      "This policy defines prohibited conduct intended to protect users, systems, and data.",
    sections: [
      {
        heading: "1. Use lawfully and as authorized",
        paragraphs: [
          "Use the KingFisher service only for legitimate, authorized organizational purposes and in compliance with applicable law, the Terms of Service, the EULA, organizational policy, and your assigned permissions. You must be at least 18 years old. Do not use access to interfere with another person's rights or evade a legal or contractual obligation.",
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
          "Do not upload personal data about minors or regulated or highly sensitive data unless KingFisher has expressly approved that data class and implemented the required safeguards, authority, and agreements.",
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
          "The owner designated support@kingfisher.com for vulnerability, compromise, unsafe-content, and policy reports in this draft. That mailbox must be verified or replaced before reliance. A report address never grants authorization to test; do not publicly disclose a vulnerability or access data beyond an expressly authorized scope, and obtain written scope from KingFisher before security testing.",
        ],
      },
      {
        heading: "7. Enforcement",
        paragraphs: [
          "KingFisher may investigate suspected violations, preserve relevant evidence, limit functionality, revoke sessions, suspend or terminate accounts, and notify an affected organization or lawful authority when appropriate. Responses should be proportionate, documented, and consistent with applicable law and controlling agreements.",
          "If you believe enforcement was mistaken, use the verified support channel published by KingFisher and include the account, decision, and basis for review. KingFisher will review the request on a best-effort basis; this policy does not create a guaranteed response time or service-level commitment.",
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
 * summary, shared draft warning, ordered section headings, paragraphs, and bullets are included.
 * Document keys and versions, routes, short navigation labels, bundle metadata, and presentation
 * markup are excluded; versions are bound separately by the manifest and evidence.
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
    draft_warning: nfc(LEGAL_DRAFT_WARNING),
    sections: document.sections.map((section) => ({
      heading: nfc(section.heading),
      paragraphs: (section.paragraphs ?? []).map(nfc),
      bullets: (section.bullets ?? []).map(nfc),
    })),
  });
}

/** SHA-256 of canonicalizeLegalDocument(key), lowercase hexadecimal. */
export const LEGAL_DOCUMENT_SHA256: Record<LegalDocumentKey, string> = {
  terms: "f5a30fded3b6b4715f13d0711c9168dd643aac48ff14164e95bc7610734fb912",
  eula: "2715b0daa99c2a553b08448eb81307affcfd2ca5ece005563eb4ad83d7fae6b3",
  privacy: "53e22a3bff270fb2215631f061cd001f89a96971e6fa3bb8374ff2f829931695",
  aup: "1290bb3dbcf3b79fb2051693ae7be6898b421daf24af1ddb037098cc1ee07217",
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
    !Number.isInteger(manifest.precise_location_retention_hours) ||
    manifest.precise_location_retention_hours < 1 ||
    manifest.precise_location_retention_hours > 720 ||
    !Number.isInteger(manifest.legal_acceptance_retention_days) ||
    manifest.legal_acceptance_retention_days < 1 ||
    manifest.legal_acceptance_retention_days > 3650 ||
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
