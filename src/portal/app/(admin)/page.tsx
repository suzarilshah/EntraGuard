import Link from 'next/link';
import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { OutcomeDistribution, VerificationChart } from '@/components/az/AdminCharts';
import { getDirectoryCapabilities, getTenant } from '@/lib/azure/graph';
import { runKql } from '@/lib/azure/logs';
import { getLiveSessions, getRuntimeConfig } from '@/lib/azure/mediaService';
import { overviewQueries, queryNumber, resourceGroup, timeRange } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

export default async function OverviewPage({ searchParams }: { searchParams: Promise<{ hours?: string; simulations?: string }> }) {
  const params = await searchParams; const hours = timeRange(params.hours); const simulations = params.simulations === 'include';
  const queries = overviewQueries(hours, simulations);
  const [summary, trend, tenant, capabilities, live, config] = await Promise.all([
    runKql(queries.summary, hours), runKql(queries.trend, hours), getTenant(), getDirectoryCapabilities(), getLiveSessions(), getRuntimeConfig(),
  ]);
  const metric = (key: string) => queryNumber(summary, key);
  const count = (key: string) => metric(key)?.toLocaleString('en-US') ?? '—';
  const active = live.data.filter(call => call.isActive);
  const outcomes = [
    { label:'Checks passed', value:metric('Passed') ?? 0, color:'var(--az-blue)' },
    { label:'Coaching blocked', value:metric('Coercion') ?? 0, color:'var(--az-error)' },
    { label:'Additional MFA required', value:metric('StepUp') ?? 0, color:'var(--az-warning)' },
    { label:'Unsuccessful / interrupted', value:metric('NotCompleted') ?? 0, color:'var(--az-text-3)' },
    { label:'Legacy voice refusal', value:metric('LegacyVoiceRefusal') ?? 0, color:'var(--az-severe)' },
  ];
  return <>
    <Breadcrumb trail={['EntraGuard', 'Overview']} />
    <PageHead title="Overview" subtitle="Verification outcomes, operational state and identity context. All data is read from the connected services." icon={<AdminGlyph name="shield" size={27} />} />
    <CommandBar simulations />
    <div className="az-content">
      <Essentials items={[
        { label:'Resource group', value:resourceGroup() ?? 'Not configured' },
        { label:'Data directory', value:tenant.data?.displayName ?? 'Unavailable' },
        { label:'Call activity', value:live.degraded ? 'Unknown — service unavailable' : `${active.length} active call${active.length === 1 ? '' : 's'}` },
        { label:'Analyst deployment', value:config.data?.model ?? 'Unavailable' },
        { label:'Call / identity actions', value:config.data ? config.data.autonomousActionsEnabled ? 'Enabled by configuration' : 'Shadow mode; notifications may still occur' : 'Unknown' },
        { label:'Remediation capability', value:config.data ? config.data.riskTier === 'Graph' ? 'P2 risk actions configured' : 'Fallback containment configured' : 'Unknown' },
      ]} />
      <div className="admin-section-heading"><h2>Verification summary</h2><span>Last {hours} hours · latest outcome per verification</span></div>
      <div className="az-grid c4 admin-kpi">
        <Card title="Recorded verifications" source="kql" degraded={summary.degraded}><Metric value={count('Total')} label="Full selected window, not a capped row count" /></Card>
        <Card title="Checks passed" source="kql" degraded={summary.degraded}><Metric value={count('Passed')} label="Call checks passed; application grants are separate" /></Card>
        <Card title="Coaching blocked" source="kql" degraded={summary.degraded}><Metric value={count('Coercion')} tone={(metric('Coercion') ?? 0) > 0 ? 'error' : undefined} label="Coercion detected by the verification pipeline" /></Card>
        <Card title="Additional MFA required" source="kql" degraded={summary.degraded}><Metric value={count('StepUp')} tone={(metric('StepUp') ?? 0) > 0 ? 'warning' : undefined} label="Latest recorded outcome is StepUpRequired" /></Card>
      </div>
      <div className="az-grid c2">
        <Card title="Verification activity" source="kql" degraded={trend.degraded} footer="Duplicate deliveries are collapsed by verification ID. Only time buckets with recorded outcomes are plotted; timestamps are UTC."><VerificationChart data={trend.data} /></Card>
        <Card title="Outcome distribution" source="kql" degraded={summary.degraded} footer="A failed or interrupted call is not automatically a prevented attack."><OutcomeDistribution values={outcomes} /></Card>
      </div>
      <div className="az-grid c2">
        <Card title="Calls in progress" source="live" degraded={live.degraded} actions={<Link prefetch={false} href="/live">Open live view</Link>}>
          {active.length ? <DataTable title="Active calls" columns={[{key:'User'},{key:'Stage'},{key:'Risk',format:'risk',align:'right'}]} rows={active.map(s => [s.subjectUpn ?? 'Unresolved subject',s.stage,s.riskScore])} emptyTitle="No active calls" emptyDetail="" filterable={false} /> : <Empty title="No active calls" detail="This is call activity, not a dependency-health check. The next active call appears here." />}
        </Card>
        <Card title="Microsoft Entra context" source="graph" degraded={tenant.degraded} actions={<Link prefetch={false} href="/identity">Open directory</Link>}>
          <div className="admin-source-status"><div><strong>{tenant.data?.displayName}</strong><small>{tenant.data?.verifiedDomains.find(domain => domain.isDefault)?.name ?? 'Default domain not returned'}</small></div><span className="az-badge info">Hosting tenant</span></div>
          <div className="admin-source-status"><div><strong>Sign-in log capability</strong><small>AuditLog.Read.All and a P1/P2 tenant subscription</small></div><span className={`az-badge ${capabilities.data?.p1 ? 'success' : 'warning'}`}>{!capabilities.data ? 'Unknown' : capabilities.data.p1 ? 'P1/P2 reported' : 'P1/P2 not reported'}</span></div>
          <div className="admin-source-status"><div><strong>Identity Protection</strong><small>Risk APIs require P2 and suitable permissions</small></div><span className={`az-badge ${capabilities.data?.p2 ? 'success' : 'warning'}`}>{!capabilities.data ? 'Unknown' : capabilities.data.p2 ? 'P2 reported' : 'P2 not reported'}</span></div>
          {capabilities.degraded && <p className="admin-meta-note">{capabilities.degraded}</p>}
        </Card>
      </div>
      <div className="admin-section-heading"><h2>Investigate and operate</h2><span>Read-only views · actions stay in their owning services</span></div>
      <div className="admin-quicklinks">
        <Link prefetch={false} className="admin-quicklink" href={`/verification?hours=${hours}${simulations ? '&simulations=include' : ''}`}><AdminGlyph name="check" size={25} /><span><strong>Verification records</strong><small>Results, reasons and assurance</small></span></Link>
        <Link prefetch={false} className="admin-quicklink" href="/incidents"><AdminGlyph name="shield" size={25} /><span><strong>Microsoft Sentinel</strong><small>Incident status and investigation</small></span></Link>
        <Link prefetch={false} className="admin-quicklink" href="/health"><AdminGlyph name="resources" size={25} /><span><strong>Azure Container Apps</strong><small>Revisions, images and resource inventory</small></span></Link>
      </div>
      <p className="admin-meta-note">{simulations ? 'Simulation-labelled entries are included.' : 'Entries explicitly labelled “(simulated)” are excluded; unlabelled legacy test data may remain.'} Log Analytics ingestion can lag behind live calls.</p>
      {!summary.degraded && (metric('NotCompleted') ?? 0) > 0 && <MessageBar intent="info" title={`${count('NotCompleted')} unsuccessful or interrupted attempts.`}>Review the verification reasons to distinguish wrong answers, unanswered calls and operational failures from detected coaching.</MessageBar>}
    </div>
  </>;
}
