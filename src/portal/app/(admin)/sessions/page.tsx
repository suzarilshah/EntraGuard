import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { IconHistory } from '@/components/az/Icons';
import { getCallSummaries } from '@/lib/azure/logs';
import { getLiveSessions } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

export default async function SessionsPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 168);

  const [calls, live] = await Promise.all([getCallSummaries(hours), getLiveSessions()]);

  const columns = calls.data.columns;
  const peakIndex = columns.indexOf('PeakRisk');
  const rationaleIndex = columns.indexOf('Rationale');
  const sessionIndex = columns.indexOf('SessionId');
  const upnIndex = columns.indexOf('SubjectUpn');

  const highRisk = calls.data.rows.filter((row) => Number(row[peakIndex] ?? 0) >= 80).length;
  const benign = calls.data.rows.filter((row) => Number(row[peakIndex] ?? 0) < 40).length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Call history']} />
      <PageHead
        title="Call history"
        subtitle="Every intercepted call, benign ones included."
        icon={<IconHistory size={17} />}
      />
      <CommandBar />

      <div className="az-content">
        <div className="az-grid c4">
          <Card title="Active now" source="live" degraded={live.degraded}>
            <Metric value={live.data.filter((s) => s.isActive).length} label="Calls in progress" />
          </Card>
          <Card title="Scored" source="kql" degraded={calls.degraded}>
            <Metric value={calls.data.rows.length} label={`In last ${hours}h`} />
          </Card>
          <Card title="High risk" source="kql" degraded={calls.degraded}>
            <Metric value={highRisk} label="Reached 80+" tone={highRisk > 0 ? 'error' : 'muted'} />
          </Card>
          <Card title="Benign" source="kql" degraded={calls.degraded}>
            <Metric value={benign} label="Scored under 40" tone="success" />
          </Card>
        </div>

        <MessageBar intent="info" title="Benign calls matter as much as malicious ones.">
          A detector you only ever watch fire is a detector you cannot evaluate — and
          EntraGuard&rsquo;s false positives land on people who have done nothing wrong.
        </MessageBar>

        <Card title="Intercepted calls" icon={<IconHistory size={15} />} source="kql" degraded={calls.degraded} flush>
          {calls.data.rows.length === 0 ? (
            <Empty
              title="No history yet"
              detail="Call summaries are aggregated from EntraGuard_CallAnalysis_CL. Run a simulation from Live calls to populate it."
            />
          ) : (
            <DataTable
              columns={[
                { key: 'PeakRisk', label: 'Peak risk', align: 'right', format: 'risk' },
                { key: 'SessionId', label: 'Session' },
                { key: 'SubjectUpn', label: 'Protected user' },
                { key: 'Rationale', label: 'Analyst rationale' },
              ]}
              rows={calls.data.rows.map((row) => [
                row[peakIndex],
                String(row[sessionIndex] ?? '').slice(0, 12),
                row[upnIndex] ?? '—',
                rationaleIndex >= 0 ? row[rationaleIndex] : '—',
              ])}
              emptyTitle="No history yet"
              emptyDetail=""
            />
          )}
        </Card>
      </div>
    </>
  );
}
