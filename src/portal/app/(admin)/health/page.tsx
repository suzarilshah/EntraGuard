import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { IconResources } from '@/components/az/Icons';
import { getFootprint } from '@/lib/azure/resourceGraph';
import { getRuntimeConfig } from '@/lib/azure/mediaService';
import { getTenant } from '@/lib/azure/graph';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

/** Short role label per Azure resource type, so the table reads as roles not ARM strings. */
const ROLE: Record<string, string> = {
  'microsoft.communication/communicationservices': 'Telephony',
  'microsoft.cognitiveservices/accounts': 'Intelligence',
  'microsoft.operationalinsights/workspaces': 'SIEM workspace',
  'microsoft.insights/datacollectionendpoints': 'Log ingestion',
  'microsoft.insights/datacollectionrules': 'Log routing',
  'microsoft.app/containerapps': 'Compute',
  'microsoft.app/managedenvironments': 'Compute environment',
  'microsoft.storage/storageaccounts': 'Session state',
  'microsoft.containerregistry/registries': 'Image registry',
  'microsoft.managedidentity/userassignedidentities': 'Identity',
  'microsoft.eventgrid/systemtopics': 'Call events',
  'microsoft.insights/components': 'Telemetry',
};

export default async function HealthPage() {
  const [footprint, config, tenant] = await Promise.all([
    getFootprint(),
    getRuntimeConfig(),
    getTenant(),
  ]);

  const healthy = footprint.data.filter((r) => r.provisioningState === 'Succeeded').length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Resource footprint']} />
      <PageHead
        title="Resource footprint"
        subtitle="The deployed estate, read live from Azure Resource Graph. Whether the defence is running is a different question from whether it has detected anything."
        icon={<IconResources size={17} />}
      />
      <CommandBar timeRange={false} />

      <div className="az-content">
        <Essentials
          items={[
            { label: 'Directory', value: tenant.data?.displayName ?? '—' },
            { label: 'Subscription', value: <span className="mono">{process.env.AZURE_SUBSCRIPTION_ID ?? '—'}</span> },
            { label: 'Resources deployed', value: `${footprint.data.length}` },
            { label: 'Provisioned OK', value: <span className="az-badge success">{healthy} / {footprint.data.length}</span> },
            { label: 'Analyst model', value: <span className="mono">{config.data?.model ?? '—'}</span> },
            { label: 'Media service', value: <span className="mono">{process.env.MEDIA_SERVICE_URL ?? '—'}</span> },
          ]}
        />

        <div className="az-grid c3">
          <Card title="Remediation tier" source="live" degraded={config.degraded}>
            {config.data ? (
              <>
                <Metric
                  value={config.data.riskTier === 'Graph' ? 'Full' : 'Degraded'}
                  label={config.data.riskTier === 'Graph' ? 'Entra ID P2 present' : 'No Entra ID P2'}
                  tone={config.data.riskTier === 'Graph' ? 'success' : 'warning'}
                />
                <p style={{ margin: '10px 0 0', fontSize: 12, color: 'var(--az-text-3)', lineHeight: 1.5 }}>
                  {config.data.riskTier === 'Graph'
                    ? 'confirmCompromised writes real risk state to Identity Protection.'
                    : 'Risk elevation unavailable. Remediation runs session revocation, Conditional Access quarantine, and a Sentinel incident instead — every attempt recorded with its real outcome.'}
                </p>
              </>
            ) : (
              <Metric value="—" label="Media service unreachable" tone="muted" />
            )}
          </Card>

          <Card title="Autonomy" source="live" degraded={config.degraded}>
            {/* "Unknown" is a distinct state from "Shadow". Claiming the system is in
                shadow mode when we simply cannot reach it would misrepresent whether it is
                currently permitted to act on a user's account. */}
            <Metric
              value={!config.data ? 'Unknown' : config.data.autonomousActionsEnabled ? 'Live' : 'Shadow'}
              label={!config.data ? 'Service unreachable' : config.data.autonomousActionsEnabled ? 'Actions execute' : 'Reports only'}
              tone={!config.data ? 'muted' : config.data.autonomousActionsEnabled ? 'success' : 'warning'}
            />
          </Card>

          <Card title="Analyst cadence" source="live" degraded={config.degraded}>
            <div className="az-metric-row">
              <Metric value={config.data ? `${config.data.analysisIntervalMs / 1000}s` : '—'} label="Interval" />
              <Metric value={config.data ? `${config.data.analysisWindowMs / 1000}s` : '—'} label="Window" />
            </div>
          </Card>
        </div>

        <Card
          title="Deployed resources"
          icon={<IconResources size={15} />}
          source="arm"
          degraded={footprint.degraded}
          flush
          footer="Matched on the application=EntraGuard tag via Azure Resource Graph."
        >
          <DataTable
            columns={[
              { key: 'Role' },
              { key: 'Name' },
              { key: 'Type' },
              { key: 'Region' },
              { key: 'State', format: 'outcome' },
            ]}
            rows={footprint.data.map((resource) => [
              ROLE[resource.type.toLowerCase()] ?? '—',
              resource.name,
              resource.type.split('/').slice(1).join('/'),
              resource.location,
              resource.provisioningState ?? 'n/a',
            ])}
            emptyTitle="No resources found"
            emptyDetail="If the deployment succeeded, check that the managed identity has Reader at subscription scope."
          />
        </Card>

        <MessageBar intent="info" title="Every credential here is a managed identity.">
          Local auth is disabled on both Cognitive Services accounts and shared-key access is
          disabled on storage, so there is no key path to fall back to — and no connection
          string that could exist to be leaked.
        </MessageBar>
      </div>
    </>
  );
}
