import { Link } from "react-router-dom";

import { LEGAL_DOCUMENT_ORDER, LEGAL_DOCUMENTS } from "../legal";

export function LegalNav({ className = "legal-nav" }: { className?: string }) {
  return (
    <nav className={className} aria-label="Legal">
      {LEGAL_DOCUMENT_ORDER.map((key) => {
        const document = LEGAL_DOCUMENTS[key];
        return (
          <Link key={key} to={document.route}>
            {document.shortTitle}
          </Link>
        );
      })}
    </nav>
  );
}
