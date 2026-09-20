import { azureCredential, degraded, ok, withTimeout, type DataResult } from './credential';
import { summariseAzureError } from './errors';
import { cache } from 'react';
import { odataPrefix, timeRange } from '../adminData';

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

export interface SignIn {
  id: string;
  createdDateTime: string;
  userPrincipalName: string;
  appDisplayName: string;
  ipAddress: string;
  riskLevelDuringSignIn: string;
  status: { errorCode: number; failureReason: string | null };
  location: { city: string | null; countryOrRegion: string | null };
  isInteractive?: boolean;
  conditionalAccessStatus?: string;
  deviceDetail?: { operatingSystem?: string; browser?: string; isManaged?: boolean; isCompliant?: boolean };
}

/**
 * Recent interactive sign-ins.
 *
 * The correlation that matters: an EntraGuard call detection is only meaningful next to
 * the authentication it was trying to subvert.
 */
export async function getRecentSignIns(top = 25, hours = 24): Promise<DataResult<SignIn[]>> {
  const since = new Date(Date.now() - timeRange(String(hours)) * 3600000).toISOString();
  const query = new URLSearchParams({ '$top': String(Math.max(1, Math.min(100, top))), '$orderby': 'createdDateTime desc',
    '$filter': `createdDateTime ge ${since}`, '$select': 'id,createdDateTime,userPrincipalName,appDisplayName,ipAddress,riskLevelDuringSignIn,status,location,isInteractive,conditionalAccessStatus,deviceDetail' });
  const result = await graphGet<{ value: SignIn[] }>(
    `/auditLogs/signIns?${query}`,
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

export interface TenantOrganization {
  displayName: string;
  id: string;
  verifiedDomains: { name: string; isDefault: boolean }[];
}

export const getTenant = cache(async (): Promise<DataResult<TenantOrganization | null>> => {
  const result = await graphGet<{ value: TenantOrganization[] }>('/organization?$select=id,displayName,verifiedDomains');

  if (result.body?.value?.[0]) {
    return ok(result.body.value[0], 'graph');
  }

  return degraded(null, 'graph', result.error ?? `Microsoft Graph returned ${result.status}.`);
});

export interface DirectoryUser { id: string; displayName: string; userPrincipalName: string; userType: string; accountEnabled?: boolean; }
export async function getDirectoryUsers(search = ''): Promise<DataResult<DirectoryUser[]>> {
  const query = new URLSearchParams({ '$select': 'id,displayName,userPrincipalName,userType,accountEnabled', '$top': '50' });
  const prefix = odataPrefix(search);
  if (prefix) query.set('$filter', `startswith(displayName,'${prefix}') or startswith(userPrincipalName,'${prefix}')`);
  else query.set('$orderby', 'displayName');
  const result = await graphGet<{ value: DirectoryUser[] }>(`/users?${query}`);
  return result.body ? ok(result.body.value, 'graph') : degraded([], 'graph', result.status === 403
    ? 'Directory users are unavailable. The managed identity needs User.Read.All or Directory.Read.All with admin consent.'
    : result.error ?? 'Directory users could not be loaded.');
}

export interface DirectoryCapabilities { p1: boolean; p2: boolean; subscriptions: string[]; }
export const getDirectoryCapabilities = cache(async (): Promise<DataResult<DirectoryCapabilities | null>> => {
  const result = await graphGet<{ value: { skuPartNumber: string; capabilityStatus: string; servicePlans: { servicePlanName: string; provisioningStatus: string }[] }[] }>(
    '/subscribedSkus?$select=skuPartNumber,capabilityStatus,servicePlans');
  if (!result.body) return degraded(null, 'graph', 'Subscription capabilities could not be read. LicenseAssignment.Read.All or Directory.Read.All is required.');
  const active = result.body.value.filter(sku => ['Enabled', 'Warning'].includes(sku.capabilityStatus));
  const plans = active.flatMap(sku => sku.servicePlans.filter(plan => plan.provisioningStatus === 'Success').map(plan => plan.servicePlanName));
  const p2 = plans.includes('AAD_PREMIUM_P2');
  return ok({ p1: p2 || plans.includes('AAD_PREMIUM'), p2, subscriptions: active.map(sku => sku.skuPartNumber) }, 'graph');
});

export interface RiskyUser { id: string; userDisplayName: string; userPrincipalName: string; riskLevel: string; riskState: string; riskDetail: string; riskLastUpdatedDateTime: string; }
export async function getRiskyUsers(): Promise<DataResult<RiskyUser[]>> {
  const result = await graphGet<{ value: RiskyUser[] }>(
    '/identityProtection/riskyUsers?$top=50&$select=id,userDisplayName,userPrincipalName,riskLevel,riskState,riskDetail,riskLastUpdatedDateTime');
  return result.body ? ok(result.body.value, 'graph') : degraded([], 'graph', [401, 403].includes(result.status)
    ? 'Identity Protection is unavailable. This API requires Entra ID P2 and an application permission such as IdentityRiskyUser.Read.All.'
    : result.error ?? 'Risky users could not be loaded.');
}
