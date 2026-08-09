import { LogsQueryClient, LogsQueryResultStatus } from '@azure/monitor-query-logs';
import { azureCredential, degraded, ok, withTimeout, type DataResult } from './credential';
import { summariseAzureError } from './errors';

/**
 * Live KQL against the Log Analytics workspace Microsoft Sentinel is onboarded to.
 *
 * Column names carry no type suffix: DCR-based custom tables use explicit schemas, so it
 * is `RiskScore`, not `RiskScore_d`. The `_s`/`_d`/`_g` convention belongs to the legacy
 * HTTP Data Collector API, which EntraGuard does not use.
 */
let client: LogsQueryClient | undefined;

function logsClient(): LogsQueryClient {
  client ??= new LogsQueryClient(azureCredential());
  return client;
}

function workspaceId(): string | undefined {
  return process.env.LAW_WORKSPACE_ID;
}

export interface QueryRows {
  columns: string[];
  rows: unknown[][];
}

/** Run a KQL query, returning a degraded result rather than throwing on failure. */
export async function runKql(query: string, hours = 24): Promise<DataResult<QueryRows>> {
  const id = workspaceId();
  if (!id) {
    return degraded({ columns: [], rows: [] }, 'kql', 'LAW_WORKSPACE_ID is not configured.');
  }

  try {
    const result = await withTimeout(
      (signal) => logsClient().queryWorkspace(id, query, { duration: `PT${hours}H` }, { abortSignal: signal }),
      'Log Analytics query',
    );

    if (result.status === LogsQueryResultStatus.Success) {
      const table = result.tables[0];
      if (!table) {
        return ok({ columns: [], rows: [] }, 'kql');
      }
      return ok(
        {
          columns: table.columnDescriptors.map((c) => c.name ?? ''),
          rows: table.rows as unknown[][],
        },
        'kql',
      );
    }

    return degraded(
      { columns: [], rows: [] },
      'kql',
      `Query was only partially successful: ${result.partialError?.message ?? 'unknown error'}`,
    );
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);

    // A table that does not exist yet is the normal state of a freshly deployed workspace,
    // before the first call has been intercepted. Saying so is more useful than an error.
    if (message.includes("could not be resolved") || message.includes('BadArgumentError')) {
      return degraded(
        { columns: [], rows: [] },
        'kql',
        'No EntraGuard data in this workspace yet. The custom tables are created at deploy time and populate on the first intercepted call.',
      );
    }

    console.error('[kql]', message);
    return degraded({ columns: [], rows: [] }, 'kql', summariseAzureError(message));
  }
}

/** Risk trajectory across recent calls, for the overview trend. */
export async function getRiskTrend(hours = 24) {
  return runKql(
    `
    EntraGuard_CallAnalysis_CL
    | where TimeGenerated > ago(${hours}h)
    | summarize PeakRisk = max(RiskScore), Assessments = count() by bin(TimeGenerated, 1h)
    | order by TimeGenerated asc
    `,
    hours,
  );
}

/** One row per intercepted call, highest risk first. */
export async function getCallSummaries(hours = 24) {
  return runKql(
    `
    EntraGuard_CallAnalysis_CL
    | where TimeGenerated > ago(${hours}h)
    | summarize
        PeakRisk       = max(RiskScore),
        PeakConfidence = max(Confidence),
        Assessments    = count(),
        Vectors        = make_set(Vectors),
        FirstSeen      = min(TimeGenerated),
        LastSeen       = max(TimeGenerated),
        Rationale      = take_any(Rationale)
        by SessionId, SubjectUpn
    | order by PeakRisk desc
    | take 50
    `,
    hours,
  );
}

/**
 * Remediation ledger — what was attempted and what actually happened.
 *
 * Includes Unavailable and BlockedByPolicy outcomes. A ledger showing only successes
 * would hide exactly the behaviour that makes the degraded path trustworthy.
 */
export async function getRemediationLedger(hours = 24) {
  return runKql(
    `
    EntraGuard_Remediation_CL
    | where TimeGenerated > ago(${hours}h)
    | project TimeGenerated, SessionId, ActionName, LadderRung, Outcome, Reason,
              SubjectUpn, GraphStatusCode, DecidedBy, DurationMs
    | order by TimeGenerated desc
    | take 100
    `,
    hours,
  );
}

export interface SentinelIncident {
  name: string;
  properties: {
    title: string;
    severity: string;
    status: string;
    createdTimeUtc: string;
    incidentNumber: number;
    description?: string;
  };
}

/** Live Microsoft Sentinel incidents from the ARM control plane. */
export async function getSentinelIncidents(top = 20): Promise<DataResult<SentinelIncident[]>> {
  const workspaceResourceId = process.env.LAW_RESOURCE_ID;
  if (!workspaceResourceId) {
    return degraded([], 'arm', 'LAW_RESOURCE_ID is not configured.');
  }

  try {
    const url =
      `https://management.azure.com${workspaceResourceId}/providers/Microsoft.SecurityInsights` +
      `/incidents?api-version=2024-03-01&$top=${top}&$orderby=properties/createdTimeUtc desc`;

    const response = await withTimeout(async (signal) => {
      const token = await azureCredential().getToken(
        'https://management.azure.com/.default',
        { abortSignal: signal },
      );
      if (!token) {
        throw new Error('Could not acquire an Azure Resource Manager token.');
      }

      return fetch(url, {
        headers: { Authorization: `Bearer ${token.token}` },
        cache: 'no-store',
        signal,
      });
    }, 'Microsoft Sentinel incidents');

    if (!response.ok) {
      const text = await response.text();
      if (response.status === 403) {
        return degraded(
          [],
          'arm',
          'Sentinel incidents are unavailable. The managed identity needs Microsoft Sentinel Reader on the workspace.',
        );
      }
      return degraded([], 'arm', `Sentinel returned ${response.status}: ${text.slice(0, 300)}`);
    }

    const body = (await response.json()) as { value: SentinelIncident[] };
    return ok(body.value ?? [], 'arm');
  } catch (error) {
    const raw = error instanceof Error ? error.message : String(error);
    console.error('[sentinel]', raw);
    return degraded([], 'arm', summariseAzureError(raw));
  }
}
