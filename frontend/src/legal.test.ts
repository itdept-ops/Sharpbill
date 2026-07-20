import { createHash } from "node:crypto";

import { describe, expect, it } from "vitest";

import {
  canonicalizeLegalDocument,
  LEGAL_BUNDLE_VERSION,
  LEGAL_DOCUMENT_ORDER,
  LEGAL_DOCUMENT_SHA256,
  LEGAL_DOCUMENT_STATUS,
  LEGAL_DOCUMENTS,
  LEGAL_DRAFT_WARNING,
  LEGAL_EMAILS,
  LEGAL_OPERATOR_LOCATION,
  LEGAL_OPERATOR_NAME,
} from "./legal";

describe("canonical legal document digests", () => {
  it.each(LEGAL_DOCUMENT_ORDER)("binds %s to its checked-in SHA-256", (key) => {
    const digest = createHash("sha256")
      .update(canonicalizeLegalDocument(key), "utf8")
      .digest("hex");

    expect(digest).toBe(LEGAL_DOCUMENT_SHA256[key]);
  });

  it("locks the owner-selected Oregon v2 business terms", () => {
    const canonicalBundle = LEGAL_DOCUMENT_ORDER.map((key) => canonicalizeLegalDocument(key)).join(
      "\n",
    );
    const terms = canonicalizeLegalDocument("terms");
    const privacy = canonicalizeLegalDocument("privacy");

    expect(LEGAL_BUNDLE_VERSION).toBe("2026-07-20-v2");
    expect(LEGAL_DOCUMENT_STATUS).toBe("counsel-review-draft");
    expect(LEGAL_DOCUMENT_ORDER.every((key) => LEGAL_DOCUMENTS[key].version === LEGAL_BUNDLE_VERSION)).toBe(
      true,
    );
    expect(canonicalBundle).toContain(LEGAL_OPERATOR_NAME);
    expect(canonicalBundle).toContain(LEGAL_OPERATOR_LOCATION);
    expect(canonicalBundle).toContain(LEGAL_EMAILS.legal);
    expect(canonicalBundle).toContain(LEGAL_EMAILS.privacy);
    expect(canonicalBundle).toContain(LEGAL_EMAILS.support);
    expect(canonicalBundle).toContain(LEGAL_DRAFT_WARNING);
    expect(terms).toContain("at least 18 years old");
    expect(terms).toContain("without subscription fees");
    expect(terms).toContain("no contractual uptime percentage");
    expect(terms).toContain("US $100");
    expect(terms).toContain("OREGON LAW GOVERNS");
    expect(terms).toContain("Washington County, Oregon");
    expect(terms).toContain("do not require mandatory arbitration");
    expect(terms).toContain("do not create a contractual waiver of class");
    expect(privacy).toContain("select the initially unchecked location option");
    expect(privacy).toContain("eligible for bounded clearing after 24 hours");
    expect(privacy).toContain("does not sell personal data");
  });
});
