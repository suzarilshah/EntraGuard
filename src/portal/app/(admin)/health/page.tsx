import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { getFootprint } from '@/lib/azure/resourceGraph';
import { getRuntimeConfig } from '@/lib/azure/mediaService';
import { azureResourceUrl, resourceGroup } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export default async function HealthPage() {
  const [footprint, config] = await Promise.all([getFootprint(), getRuntimeConfig()]);
  const apps = footprint.data.filter(resource => resource.type.toLowerCase() === 'microsoft.app/containerapps');
  const portal = apps.find(app => app.name === (process.env.CONTAINER_APP_NAME || 'ca-entraguard-portal'));
  const group = resourceGroup();
  const subscription = process.env.AZURE_SUBSCRIPTION_ID;
  const groupUrl = group && subscription ? azureResourceUrl(`/subscriptions/${subscription}/resourceGroups/${group}`) : null;
  return <><Breadcrumb trail={['EntraGuard','Azure resources']} /><PageHead title="Azure resources" subtitle="Control-plane inventory, Container App revisions and configured runtime modes. Provisioning success is not a live dependency-health guarantee." icon={<AdminGlyph name="resources" size={27}/>} />
    <CommandBar timeRange={false}>{groupUrl && <a className="az-cmd" href={groupUrl} target="_blank" rel="noreferrer">Open resource group <AdminGlyph name="external" size={13}/></a>}</CommandBar>
    <div className="az-content"><Essentials items={[
      {label:'Resource group',value:group??'Tag-filtered inventory'}, {label:'Subscription',value:subscription??'Not configured'},
      {label:'Admin Container App',value:portal?.name??'Not returned'}, {label:'Ready revision',value:portal?.readyRevision||'Not reported'},
      {label:'Portal image',value:portal?.image||'Not reported'}, {label:'Inventory scope',value:group?'Resource group, including untagged resources':'application=EntraGuard tag'},
    ]}/>
      <div className="az-grid c3 admin-kpi">
        <Card title="Resources returned" source="arm" degraded={footprint.degraded}><Metric value={footprint.data.length} label="Objects in the returned resource inventory" /></Card>
        <Card title="Container Apps" source="arm" degraded={footprint.degraded}><Metric value={apps.length} label="Includes supporting apps such as the handbook" /></Card>
        <Card title="Configured action mode" source="live" degraded={config.degraded}><Metric value={!config.data?'Unknown':config.data.autonomousActionsEnabled?'Enabled':'Shadow'} label="Call / identity remediation configuration" /></Card>
      </div>
      <Card title="Container Apps" source="arm" degraded={footprint.degraded} flush footer="Images and revisions come from Azure Resource Graph and may lag ARM changes. Internal ingress is intentional for the speaker service; zero minimum replicas can be intentional for documentation.">
        <DataTable title="Container Apps" columns={[{key:'Application',format:'link',width:'medium'},{key:'Provisioning',format:'outcome'},{key:'Runtime',format:'outcome'},{key:'Ingress'},{key:'Replicas'},{key:'Ready revision',width:'medium'},{key:'Image',width:'wide'}]} rows={apps.map(app=>[
          {label:app.name,url:azureResourceUrl(app.id)},app.provisioningState||'Not reported',app.runningStatus||'Not reported',app.external===true?'External':app.external===false?'Internal':'Not reported',
          app.minReplicas==null||app.maxReplicas==null?'Not reported':`${app.minReplicas}–${app.maxReplicas}`,app.readyRevision,app.image,
        ])} emptyTitle="No Container Apps returned" emptyDetail="Check the resource-group scope and Reader access." />
      </Card>
      <Card title="Resource-group inventory" source="arm" degraded={footprint.degraded} flush footer="The group scope includes untagged services that a tag-only query would omit. Open a resource in Azure Portal for its full operational view.">
        <DataTable title="Azure resources" columns={[{key:'Resource',format:'link',width:'medium'},{key:'Type',width:'wide'},{key:'Location'},{key:'Provisioning',format:'outcome'}]} rows={footprint.data.map(resource=>[{label:resource.name,url:azureResourceUrl(resource.id)},resource.type,resource.location,resource.provisioningState||'Not reported'])} emptyTitle="No resources returned" emptyDetail="The configured group may be empty or outside the identity’s scope." />
      </Card>
      <MessageBar intent="info" title="Read-only control-plane view.">No scale, revision, secret or access-policy changes are made here. Shadow mode can still emit notifications and warranted incidents; it does not mean the pipeline is disabled.</MessageBar>
    </div></>;
}
