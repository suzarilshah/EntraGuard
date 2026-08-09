import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { bandFor } from '@/components/az/RiskMeter';
import { IconSiem } from '@/components/az/Icons';
import { getRiskTrend, getSentinelIncidents, runKql } from '@/lib/azure/logs';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

/** Shown verbatim so it can be pasted straight into Log Analytics. */
const QUERY = `EntraGuard_CallAnalysis_CL
| where TimeGenerated > ago(24h)
| project TimeGenerated, SessionId, RiskScore, Confidence, ComplianceStage, Vectors, SubjectUpn
| order by TimeGenerated desc`;

export default async function SentinelPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  const [incidents, trend, raw] = await Promise.all([
    getSentinelIncidents(25),
    getRiskTrend(hours),
    runKql(QUERY.replace('ago(24h)', `ago(${hours}h)`) + '\n| take 40', hours),
  ]);

  const peakIndex = trend.data.columns.indexOf('PeakRisk');
  const peaks = trend.data.rows.map((row) => Number(row[peakIndex] ?? 0));
  const maxPeak = Math.max(1, ...peaks);
  const high = incidents.data.filter((i) => i.properties.severity === 'High').length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Microsoft Sentinel']} />
      <PageHead
        title="Microsoft Sentinel"
        subtitle="Voice-channel detections land in the same workspace as the rest of the estate, so a scam call is correlatable with the sign-in it was trying to subvert."
        icon={<IconSiem size={17} />}
      />
      <CommandBar />

      <div className="az-content">
        <div className="az-grid c3">
          <Card title="Incidents" icon={<IconSiem size={15} />} source="arm" degraded={incidents.degraded}>
            <div className="az-metric-row">
              <Metric value={incidents.data.length} label="Open" />
              <Metric value={high} label="High severity" tone={high > 0 ? 'error' : 'muted'} />
            </div>
          </Card>

          <Card title={`Assessments · last ${hours}h`} source="kql" degraded={raw.degraded}>
            <Metric value={raw.data.rows.length} label="Rows in workspace" />
          </Card>

          <Card title="Peak risk" source="kql" degraded={trend.degraded}>
            <Metric
              value={peaks.length ? Math.round(Math.max(...peaks)) : 0}
              label="Highest score in window"
              tone={peaks.length ? bandFor(Math.max(...peaks)) : 'muted'}
            />
          </Card>
        </div>

        <Card title={`Peak risk per hour · last ${hours}h`} icon={<IconSiem size={15} />} source="kql" degraded={trend.degraded}>
          {trend.data.rows.length === 0 ? (
            <Empty title="No data in window" detail="The trend fills as calls are intercepted and scored." />
          ) : (
            <div className="az-bars">
              {trend.data.rows.map((row, index) => {
                const peak = Number(row[peakIndex] ?? 0);
                const band = bandFor(peak);
                return (
                  <span
                    key={index}
                    className="az-bar"
                    title={`Peak risk ${peak.toFixed(0)}`}
                    style={{
                      height: `${Math.max(2, (peak / maxPeak) * 100)}%`,
                      background: `var(--az-${band === 'success' ? 'success' : band})`,
                    }}
                  />
                );
              })}
            </div>
          )}
        </Card>

        <Card title="Incidents" icon={<IconSiem size={15} />} source="arm" degraded={incidents.degraded} flush>
          <DataTable
            columns={[
              { key: '#', align: 'right' },
              { key: 'Severity', format: 'severity' },
              { key: 'Title' },
              { key: 'Status' },
              { key: 'Created' },
            ]}
            rows={incidents.data.map((incident) => [
              incident.properties.incidentNumber,
              incident.properties.severity,
              incident.properties.title,
              incident.properties.status,
              incident.properties.createdTimeUtc,
            ])}
            emptyTitle="Queue clear"
            emptyDetail="EntraGuard raises an incident directly during a live call; the scheduled analytics rule raises one within five minutes as the durable path."
          />
        </Card>

        <Card
          title="EntraGuard_CallAnalysis_CL"
          icon={<IconSiem size={15} />}
          source="kql"
          degraded={raw.degraded}
          flush
          footer="Column names carry no type suffix — DCR-based custom tables use explicit schemas, so it is RiskScore, not RiskScore_d."
        >
          <pre
            className="mono"
            style={{
              margin: 0, padding: '12px 14px', background: '#00000040',
              borderBottom: '1px solid var(--az-border-soft)',
              color: 'var(--az-text-2)', overflowX: 'auto', fontSize: 11.5, lineHeight: 1.6,
            }}
          >
            {QUERY.replace('ago(24h)', `ago(${hours}h)`)}
          </pre>
          <DataTable
            columns={raw.data.columns.map((column) => ({
              key: column,
              align: ['RiskScore', 'Confidence'].includes(column) ? 'right' : 'left',
              format: column === 'RiskScore' ? 'risk' : 'text',
            }))}
            rows={raw.data.rows}
            emptyTitle="No rows"
            emptyDetail="Custom tables are created at deploy time and populate on the first intercepted or simulated call."
          />
        </Card>

        <MessageBar intent="info" title="Ingestion path.">
          Written through the Logs Ingestion API using a data collection endpoint and rule, not
          the HTTP Data Collector API — that one retires on 14 September 2026.
        </MessageBar>
      </div>
    </>
  );
}
