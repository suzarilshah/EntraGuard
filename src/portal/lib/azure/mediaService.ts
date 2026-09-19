import { degraded, ok, type DataResult } from './credential';
import { sessionHeaders } from '../rpProxy';

/**
 * Reads live call state from the media service.
 *
 * The live view is driven by SignalR; these calls exist so a page load renders something
 * real before the first event arrives, and so the state stays inspectable when a demo
 * misbehaves.
 */
const baseUrl = () => process.env.MEDIA_SERVICE_URL?.replace(/\/$/, '') ?? '';

export interface LiveSession {
  sessionId: string;
  startedAt: string;
  subjectUpn: string | null;
  callerIdentity: string | null;
  riskScore: number;
  peakRisk: number;
  confidence: number;
  stage: string;
  vectors: string[];
  isActive: boolean;
  actionsTaken: string[];
}

export interface RuntimeConfig {
  riskTier: string;
  autonomousActionsEnabled: boolean;
  analysisIntervalMs: number;
  analysisWindowMs: number;
  model: string;
}

async function get<T>(path: string, fallback: T): Promise<DataResult<T>> {
  const base = baseUrl();
  if (!base) {
    return degraded(fallback, 'live', 'MEDIA_SERVICE_URL is not configured.');
  }

  try {
    const response = await fetch(`${base}${path}`, {
      headers: await sessionHeaders(),
      cache: 'no-store',
      // The answer path is latency-critical; a portal poll must never sit on a socket
      // long enough to matter to a call in flight.
      signal: AbortSignal.timeout(5000),
    });

    if (!response.ok) {
      return degraded(fallback, 'live', `Media service returned ${response.status}.`);
    }

    return ok((await response.json()) as T, 'live');
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return degraded(
      fallback,
      'live',
      `Media service unreachable at ${base}. ${message.slice(0, 160)}`,
    );
  }
}

export const getLiveSessions = () => get<LiveSession[]>('/api/sessions/live', []);

export const getRuntimeConfig = () =>
  get<RuntimeConfig | null>('/api/config', null);

export interface VerificationAttempt {
  verificationId: string;
  upn: string;
  applicationName: string;
  result: string;
  reason: string;
  grantsAccess: boolean;
  isComplete: boolean;
  attempts: number;
  peakRiskDuringCall: number;
  startedAt: string;
  durationMs: number;
}

/** In-flight and recently completed step-up challenges, straight from the registry. */
export const getRecentVerifications = () =>
  get<VerificationAttempt[]>('/api/operator/verifications', []);
