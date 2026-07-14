import type { ReactNode } from "react";

export function Panel({
  title,
  right,
  brackets = true,
  className = "",
  children,
}: {
  title?: string;
  right?: ReactNode;
  brackets?: boolean;
  className?: string;
  children: ReactNode;
}) {
  return (
    <section className={`panel ${brackets ? "panel--brackets" : ""} ${className}`}>
      {title && (
        <div className="panel-header">
          <span>{title}</span>
          {right}
        </div>
      )}
      <div className="panel-body">{children}</div>
    </section>
  );
}
