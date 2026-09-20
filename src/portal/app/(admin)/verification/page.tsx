import Link from 'next/link';
import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { runKql } from '@/lib/azure/logs';
import { getRecentVerifications } from '@/lib/azure/mediaService';
import { overviewQueries, queryNumber, timeRange } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export default async function VerificationPage({ searchParams }: { searchParams: Promise<{ hours?: string; simulations?: string }> }) {
  const params = await searchParams; const hours = timeRange(params.hours);
  const queries = overviewQueries(hours, params.simulations === 'include');
  const [summary, records, live] = await Promise.all([runKql(queries.summary, hours), runKql(queries.recent, hours), getRecentVerifications()]);
  const count = (key: string) => queryNumber(summary, key)?.toLocaleString('en-US') ?? '—';
  const active = live.data.filter(v => !v.isComplete);
  return <>
    <Breadcrumb trail={['EntraGuard','Verifications']} />
    <PageHead title="Verifications" subtitle="Latest recorded challenge outcomes. Passing a call is distinct from consuming a receipt for application access or payment approval." icon={<AdminGlyph name="check" size={27} />} />
    <CommandBar simulations><Link className="az-cmd" prefetch={false} href={`/voice-insights?hours=${hours}`}>Voice insights</Link></CommandBar>
    <div className="az-content">
      <div className="az-grid c4 admin-kpi">
        <Card title="Recorded attempts" source="kql" degraded={summary.degraded}><Metric value={count('Total')} label={`Full ${hours}-hour window`} /></Card>
        <Card title="Checks passed" source="kql" degraded={summary.degraded}><Metric value={count('Passed')} label="Policy and application grant checks still apply" /></Card>
        <Card title="Coaching blocked" source="kql" degraded={summary.degraded}><Metric value={count('Coercion')} label="Coercion refusal, independent of code correctness" /></Card>
        <Card title="In progress" source="live" degraded={live.degraded}><Metric value={active.length} label="Current media-service registry" /></Card>
      </div>
      {active.length > 0 && <Card title="Active challenges" source="live" degraded={live.degraded} flush><DataTable title="Active challenges" columns={[{key:'Reference'},{key:'User'},{key:'Application'},{key:'Started',format:'datetime'}]} rows={active.map(v => [v.verificationId,v.upn,v.applicationName,v.startedAt])} emptyTitle="No active challenges" emptyDetail="" /></Card>}
      <Card title="Verification records" source="kql" degraded={records.degraded} flush footer="Latest 100 records after deduplication. Summary totals above cover the full selected window. Use row details for untruncated reasons; export includes loaded rows only.">
        <DataTable title="Verification records" columns={records.data.columns.map(name => ({ key:name, label: name.replace(/([A-Z])/g,' $1').trim(),
          format: name === 'Result' ? 'verificationResult' as const : name === 'TimeGenerated' ? 'datetime' as const : name === 'DurationMs' ? 'duration' as const : name === 'PeakRiskDuringCall' ? 'risk' as const : 'text' as const,
          width: name === 'Reason' ? 'wide' as const : ['Result','DurationMs','EndpointKind','AssuranceLevel'].includes(name) ? 'narrow' as const : undefined,
        }))} rows={records.data.rows} emptyTitle="No verification records in this window" emptyDetail="Try a wider window. Newly completed calls may still be waiting for telemetry ingestion." />
      </Card>
    </div>
  </>;
}
