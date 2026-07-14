import type { Provider } from "../types";

export function ProviderBadge({ provider }: { provider: Provider }) {
  return <span className={`badge provider provider-${provider}`}>{provider}</span>;
}
