import { createHash } from "node:crypto";

import { describe, expect, it } from "vitest";

import {
  canonicalizeLegalDocument,
  LEGAL_DOCUMENT_ORDER,
  LEGAL_DOCUMENT_SHA256,
} from "./legal";

describe("canonical legal document digests", () => {
  it.each(LEGAL_DOCUMENT_ORDER)("binds %s to its checked-in SHA-256", (key) => {
    const digest = createHash("sha256")
      .update(canonicalizeLegalDocument(key), "utf8")
      .digest("hex");

    expect(digest).toBe(LEGAL_DOCUMENT_SHA256[key]);
  });
});
