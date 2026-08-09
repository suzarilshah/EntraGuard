import { azureCredential, degraded, ok, withTimeout, type DataResult } from './credential';
import { summariseAzureError } from './errors';

const GRAPH = 'https://graph.microsoft.com/v1.0';

async function graphGet<T>(path: string): Promise<{ status: number; body: T | null; error?: string }> {
  try {
    const response = await withTimeout(async (signal) => {
      // Token acquisition is inside the budget on purpose: on a container app a cold
      // managed-identity token fetch is frequently the slow step, not the API call.
      const token = await azureCredential().getToken(
        'https://graph.microsoft.com/.default',
        { abortSignal: signal },
      );
      if (!token) {
        throw new Error('Could not acquire a Microsoft Graph token.');
      }

      return fetch(`${GRAPH}${path}`, {
        headers: { Authorization: `Bearer ${token.token}` },
        // Identity telemetry is only useful if it is current; a cached risky-user list
        // during an active incident is worse than none.
        cache: 'no-store',
        signal,
      });
    }, `Microsoft Graph (${path.split('?')[0]})`);

    if (!response.ok) {
      const text = await response.text();
      return { status: response.status, body: null, error: text.slice(0, 400) };
    }

    return { status: response.status, body: (await response.json()) as T };
  } catch (error) {
    const raw = error instanceof Error ? error.message : String(error);
    // Full text to the server log; the summary is what a panel can usefully show.
    console.error('[graph]', path, raw);
    return { status: 0, body: null, error: summariseAzureError(raw) };
  }
}

export interface RiskyUser {
  id: string;
  userPrincipalName: string;
  userDisplayName: string;
  riskLevel: string;
  riskState: string;
  riskLastUpdatedDateTime: string;
}

/**
 * Live risky users from Microsoft Entra ID Protection.
 *
 * This endpoint requires Entra ID P2. On a tenant without it Graph returns 403, and that
 * is reported as a degradation with the licensing reason attached — the one panel where
 * EntraGuard's own limitation is most likely to show, and the one most worth being honest
 * about.
 */
export async function getRiskyUsers(top = 20): Promise<DataResult<RiskyUser[]>> {
  const result = await graphGet<{ value: RiskyUser[] }>(
    `/identityProtection/riskyUsers?$top=${top}&$orderby=riskLastUpdatedDateTime desc`,
  );

  if (result.body) {
    return ok(result.body.value, 'graph');
  }

  if (result.status === 403 || result.status === 401) {
    return degraded(
      [],
      'graph',
      'Identity Protection risky users are unavailable. This tenant is missing Entra ID P2, or the service principal has not been granted IdentityRiskyUser.Read.All.',
    );
  }

  return degraded([], 'graph', result.error ?? `Microsoft Graph returned ${result.status}.`);
}

export interface SignIn {
  id: string;
  createdDateTime: string;
  userPrincipalName: string;
  appDisplayName: string;
  ipAddress: string;
  riskLevelDuringSignIn: string;
  status: { errorCode: number; failureReason: string | null };
  location: { city: string | null; countryOrRegion: string | null };
}

/**
 * Recent interactive sign-ins.
 *
 * The correlation that matters: an EntraGuard call detection is only meaningful next to
 * the authentication it was trying to subvert.
 */
export async function getRecentSignIns(top = 25): Promise<DataResult<SignIn[]>> {
  const result = await graphGet<{ value: SignIn[] }>(
    `/auditLogs/signIns?$top=${top}&$orderby=createdDateTime desc`,
  );

  if (result.body) {
    return ok(result.body.value, 'graph');
  }

  if (result.status === 403 || result.status === 401) {
    return degraded(
      [],
      'graph',
      'Sign-in logs are unavailable. The service principal needs AuditLog.Read.All with admin consent. Note that sign-in log retrieval also requires an Entra ID P1 or P2 tenant licence.',
    );
  }

  return degraded([], 'graph', result.error ?? `Microsoft Graph returned ${result.status}.`);
}

export interface RiskDetection {
  id: string;
  userPrincipalName: string;
  riskEventType: string;
  riskLevel: string;
  detectedDateTime: string;
  activity: string;
  ipAddress: string;
}

export async function getRiskDetections(top = 20): Promise<DataResult<RiskDetection[]>> {
  const result = await graphGet<{ value: RiskDetection[] }>(
    `/identityProtection/riskDetections?$top=${top}&$orderby=detectedDateTime desc`,
  );

  if (result.body) {
    return ok(result.body.value, 'graph');
  }

  if (result.status === 403 || result.status === 401) {
    return degraded(
      [],
      'graph',
      'Risk detections are unavailable. Requires Entra ID P2 and IdentityRiskEvent.Read.All.',
    );
  }

  return degraded([], 'graph', result.error ?? `Microsoft Graph returned ${result.status}.`);
}

export interface TenantOrganization {
  displayName: string;
  id: string;
  verifiedDomains: { name: string; isDefault: boolean }[];
}

export async function getTenant(): Promise<DataResult<TenantOrganization | null>> {
  const result = await graphGet<{ value: TenantOrganization[] }>('/organization');

  if (result.body?.value?.[0]) {
    return ok(result.body.value[0], 'graph');
  }

  return degraded(null, 'graph', result.error ?? `Microsoft Graph returned ${result.status}.`);
}
