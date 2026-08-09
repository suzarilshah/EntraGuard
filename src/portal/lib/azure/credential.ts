import { DefaultAzureCredential, type TokenCredential } from '@azure/identity';

/**
 * Server-side Azure credential.
 *
 * AZURE_CLIENT_ID selects the user-assigned managed identity in Azure; locally this falls
 * through to the developer's `az login`. Every Azure call in this portal runs server-side
 * with this credential — no Azure token is ever handed to the browser, so a compromised
 * browser session cannot read Graph or the workspace directly.
 */
let cached: TokenCredential | undefined;

export function azureCredential(): TokenCredential {
  cached ??= new DefaultAzureCredential({
    managedIdentityClientId: process.env.AZURE_CLIENT_ID,
  });
  return cached;
}

/** Provenance of a panel's data, rendered as a badge so nothing is silently synthetic. */
export type Provenance = 'graph' | 'kql' | 'live' | 'arm';

/**
 * Result envelope for every data adapter.
 *
 * `degraded` is the reason a panel could not load real data — a missing licence, an
 * unconsented permission, an unreachable service. The UI renders that reason verbatim
 * rather than showing an empty state, because "zero risky users" and "we are not allowed
 * to see risky users" mean very different things to whoever is reading the screen.
 */
export interface DataResult<T> {
  data: T;
  provenance: Provenance;
  degraded?: string;
  fetchedAt: string;
}

export function ok<T>(data: T, provenance: Provenance): DataResult<T> {
  return { data, provenance, fetchedAt: new Date().toISOString() };
}

/**
 * Hard ceiling on how long any single Azure call may delay a page render.
 *
 * The dashboard fans out to six independent Azure APIs. Without a bound, the slowest one
 * dictates the page load — and a hung managed-identity token acquisition or a cold Log
 * Analytics query can stall for a minute or more, which reads to the viewer as a broken
 * page rather than a slow panel.
 *
 * A panel that times out degrades to its "limited" state with a reason. The other five
 * still render. That is strictly better than a blank screen.
 */
export const AZURE_CALL_TIMEOUT_MS = 8000;

/** Reject with a clear, attributable message if a promise outruns the budget. */
export async function withTimeout<T>(
  operation: (signal: AbortSignal) => Promise<T>,
  label: string,
  timeoutMs: number = AZURE_CALL_TIMEOUT_MS,
): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  try {
    return await operation(controller.signal);
  } catch (error) {
    if (controller.signal.aborted) {
      throw new Error(`${label} did not respond within ${timeoutMs / 1000}s.`);
    }
    throw error;
  } finally {
    clearTimeout(timer);
  }
}

export function degraded<T>(data: T, provenance: Provenance, reason: string): DataResult<T> {
  return { data, provenance, degraded: reason, fetchedAt: new Date().toISOString() };
}
