import { azureCredential, degraded, ok, withTimeout, type DataResult } from './credential';
import { summariseAzureError } from './errors';

/**
 * Azure Resource Graph — live inventory of the EntraGuard footprint.
 *
 * The health view answers "is the thing that is supposed to be protecting us actually
 * deployed and running", which is a different question from "did it detect anything".
 */
export interface ResourceRow {
  name: string;
  type: string;
  location: string;
  resourceGroup: string;
  provisioningState?: string;
}

export async function getFootprint(): Promise<DataResult<ResourceRow[]>> {
  const subscriptionId = process.env.AZURE_SUBSCRIPTION_ID;
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
            | where tags['application'] =~ 'EntraGuard'
            | project name, type, location, resourceGroup,
                      provisioningState = tostring(properties.provisioningState)
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
          'Resource inventory is unavailable. The managed identity needs Reader at subscription scope.',
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
