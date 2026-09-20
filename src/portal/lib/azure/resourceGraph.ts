import { azureCredential, degraded, ok, withTimeout, type DataResult } from './credential';
import { summariseAzureError } from './errors';
import { resourceGroup } from '../adminData';

/**
 * Azure Resource Graph — live inventory of the EntraGuard footprint.
 *
 * The health view answers "is the thing that is supposed to be protecting us actually
 * deployed and running", which is a different question from "did it detect anything".
 */
export interface ResourceRow {
  id: string;
  name: string;
  type: string;
  location: string;
  resourceGroup: string;
  provisioningState?: string;
  runningStatus?: string;
  readyRevision?: string;
  image?: string;
  fqdn?: string;
  external?: boolean;
  minReplicas?: number;
  maxReplicas?: number;
}

export async function getFootprint(): Promise<DataResult<ResourceRow[]>> {
  const subscriptionId = process.env.AZURE_SUBSCRIPTION_ID;
  const group = resourceGroup();
  if (!subscriptionId) {
    return degraded([], 'arm', 'AZURE_SUBSCRIPTION_ID is not configured.');
  }

  try {
    const response = await withTimeout(async (signal) => {
      const token = await azureCredential().getToken(
        'https://management.azure.com/.default',
        { abortSignal: signal },
      );
      if (!token) {
        throw new Error('Could not acquire an Azure Resource Manager token.');
      }

      return fetch(
      'https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01',
      {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token.token}`,
          'Content-Type': 'application/json',
        },
        cache: 'no-store',
        signal,
        body: JSON.stringify({
          subscriptions: [subscriptionId],
          query: `
            Resources
            | where ${group ? `resourceGroup =~ ${JSON.stringify(group)}` : "tags['application'] =~ 'EntraGuard'"}
            | project id, name, type, location, resourceGroup,
                      provisioningState = tostring(properties.provisioningState),
                      runningStatus = tostring(properties.runningStatus),
                      readyRevision = tostring(properties.latestReadyRevisionName),
                      image = tostring(properties.template.containers[0].image),
                      fqdn = tostring(properties.configuration.ingress.fqdn),
                      external = tobool(properties.configuration.ingress.external),
                      minReplicas = toint(properties.template.scale.minReplicas),
                      maxReplicas = toint(properties.template.scale.maxReplicas)
            | order by type asc
          `,
        }),
      },
      );
    }, 'Azure Resource Graph');

    if (!response.ok) {
      const text = await response.text();
      if (response.status === 403) {
        return degraded(
          [],
          'arm',
            'Resource inventory is unavailable. The managed identity needs Reader access to the target resource group/resources.',
        );
      }
      return degraded([], 'arm', `Resource Graph returned ${response.status}: ${text.slice(0, 300)}`);
    }

    const body = (await response.json()) as { data: ResourceRow[] };
    return ok(body.data ?? [], 'arm');
  } catch (error) {
    const raw = error instanceof Error ? error.message : String(error);
    console.error('[resourceGraph]', raw);
    return degraded([], 'arm', summariseAzureError(raw));
  }
}
