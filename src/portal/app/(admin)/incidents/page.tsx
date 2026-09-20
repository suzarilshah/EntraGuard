import Link from 'next/link';
import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { getSentinelIncidents } from '@/lib/azure/logs';
import { azureResourceUrl } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export default async function IncidentsPage({searchParams}:{searchParams:Promise<{scope?:string}>}) {
  const scope=(await searchParams).scope==='all'?'all':'entraguard';
  const incidents=await getSentinelIncidents(100);
  const rows=scope==='all'?incidents.data:incidents.data.filter(incident=>incident.properties.title.toLowerCase().includes('entraguard'));
  const workspace=process.env.LAW_RESOURCE_ID;
  return <><Breadcrumb trail={['EntraGuard','Sentinel incidents']} /><PageHead title="Sentinel incidents" subtitle="Investigate workspace incidents in Microsoft Sentinel. Labels and counts below apply only to the loaded incident page." icon={<AdminGlyph name="shield" size={27}/>} />
    <CommandBar timeRange={false}>{workspace&&<a className="az-cmd" href={azureResourceUrl(workspace)} target="_blank" rel="noreferrer">Open workspace in Azure <AdminGlyph name="external" size={13}/></a>}</CommandBar>
    <nav className="admin-tabs" aria-label="Incident scope"><Link prefetch={false} href="/incidents" aria-current={scope==='entraguard'?'page':undefined}>EntraGuard-labelled</Link><Link prefetch={false} href="/incidents?scope=all" aria-current={scope==='all'?'page':undefined}>All workspace incidents</Link></nav>
    <div className="az-content"><div className="az-grid c3 admin-kpi">{['New','Active','Closed'].map(status=><Card key={status} title={`${status} · loaded page`} source="arm" degraded={incidents.degraded}><Metric value={rows.filter(row=>row.properties.status===status).length} label="In the selected scope, among the latest 100 fetched" /></Card>)}</div>
      <Card title="Incident queue" source="arm" degraded={incidents.degraded} flush footer="No close, assign or remediation controls are duplicated here. Review and act in Microsoft Sentinel. EntraGuard-label filtering is by incident title, not verified product attribution."><DataTable title="Sentinel incidents" columns={[{key:'Number'},{key:'Title',width:'wide'},{key:'Severity',format:'severity'},{key:'Status'},{key:'Created',format:'datetime'},{key:'Description',width:'wide'},{key:'Incident reference'}]} rows={rows.map(incident=>[incident.properties.incidentNumber,incident.properties.title,incident.properties.severity,incident.properties.status,incident.properties.createdTimeUtc,incident.properties.description,incident.name])} emptyTitle="No matching incidents in the loaded page" emptyDetail="This does not assert that the entire Sentinel workspace or older history is clear." /></Card>
    </div></>;
}
