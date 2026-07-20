import { useEffect } from "react";
import { Link } from "react-router-dom";

import { LegalNav } from "../components/LegalNav";
import { MatrixRain } from "../components/MatrixRain";
import {
  LEGAL_BUNDLE_VERSION,
  LEGAL_DOCUMENTS,
  LEGAL_EFFECTIVE_DATE,
  LEGAL_EFFECTIVE_DATE_ISO,
  type LegalDocumentKey,
} from "../legal";

export function LegalPage({ documentKey }: { documentKey: LegalDocumentKey }) {
  const document = LEGAL_DOCUMENTS[documentKey];

  useEffect(() => {
    const previousTitle = window.document.title;
    window.document.title = `${document.title} | Kingfisher`;
    return () => {
      window.document.title = previousTitle;
    };
  }, [document.title]);

  return (
    <div className="legal-page">
      <MatrixRain opacity={0.06} />
      <div className="scanlines" />

      <nav className="landing-nav" aria-label="Primary navigation">
        <Link to="/" className="brand">
          <span aria-hidden="true">◈</span> KINGFISHER
        </Link>
        <span className="spacer" />
        <Link to="/">Home</Link>
        <Link to="/security">Security</Link>
        <Link to="/login">Sign in</Link>
      </nav>

      <main className="legal-wrap">
        <article className="panel panel--brackets legal-document" aria-labelledby="legal-title">
          <header className="legal-header">
            <div className="hero-eyebrow">LEGAL://{documentKey}</div>
            <h1 id="legal-title">{document.title}</h1>
            <p>{document.summary}</p>
            <dl className="legal-meta">
              <div>
                <dt>Bundle</dt>
                <dd>{LEGAL_BUNDLE_VERSION}</dd>
              </div>
              <div>
                <dt>Document version</dt>
                <dd>{document.version}</dd>
              </div>
              <div>
                <dt>Draft date</dt>
                <dd>
                  <time dateTime={LEGAL_EFFECTIVE_DATE_ISO}>{LEGAL_EFFECTIVE_DATE}</time>
                </dd>
              </div>
            </dl>
          </header>

          <aside className="legal-draft" role="note" aria-label="Draft legal notice">
            <strong>DRAFT — PENDING LEGAL COUNSEL REVIEW</strong>
            <span>
              This product template is not legal advice or a final agreement. Before production
              use, the deployment Operator must obtain qualified counsel review and complete all
              organization-, jurisdiction-, contact-, and risk-specific terms.
            </span>
          </aside>

          <div className="legal-sections">
            {document.sections.map((section) => (
              <section key={section.heading}>
                <h2>{section.heading}</h2>
                {section.paragraphs?.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
                {section.bullets && (
                  <ul>
                    {section.bullets.map((item) => <li key={item}>{item}</li>)}
                  </ul>
                )}
              </section>
            ))}
          </div>

          <footer className="legal-document-footer">
            <p>Review the complete legal bundle:</p>
            <LegalNav />
            <Link className="btn btn-ghost btn-sm" to="/login">
              Return to sign in
            </Link>
          </footer>
        </article>
      </main>
    </div>
  );
}
