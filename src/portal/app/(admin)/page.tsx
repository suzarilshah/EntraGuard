import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { RiskMeter } from '@/components/az/RiskMeter';
import { IconShield, IconSiem, IconPerson, IconPhone } from '@/components/az/Icons';
import { getRiskyUsers, getTenant } from '@/lib/azure/graph';
import { getCallSummaries, getSentinelIncidents } from '@/lib/azure/logs';
import { getLiveSessions, getRuntimeConfig } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

export default async function OverviewPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  // Fired together: five independent Azure reads, and the slowest should not decide how
  // long the operator waits for the other four.
  const [tenant, riskyUsers, incidents, calls, live, config] = await Promise.all([
    getTenant(),
    getRiskyUsers(5),
    getSentinelIncidents(5),
    getCallSummaries(hours),
    getLiveSessions(),
    getRuntimeConfig(),
  ]);

  const active = live.data.filter((session) => session.isActive);
  const peakLive = active.reduce((max, session) => Math.max(max, session.riskScore), 0);
  const peakIndex = calls.data.columns.indexOf('PeakRisk');
  const highRisk = calls.data.rows.filter(
    (row) => typeof row[peakIndex] === 'number' && (row[peakIndex] as number) >= 80,
  ).length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Overview']} />
      <PageHead
        title="Overview"
        subtitle="Real-time voice verification and anti-scam defence for Microsoft Entra ID authentication."
      />
      <CommandBar />

      <div className="az-content">
        <Essentials
          items={[
            { label: 'Directory', value: tenant.data?.displayName ?? '—' },
            { label: 'Status', value: active.length > 0
                ? <span className="az-badge error"><span className="az-dot az-pulse" />{active.length} call(s) in progress</span>
                : <span className="az-badge success"><span className="az-dot" />Standing by</span> },
            { label: 'Analyst model', value: <span className="mono">{config.data?.model ?? '—'}</span> },
            { label: 'Remediation tier', value: !config.data
                ? <span className="az-badge">Unknown — service unreachable</span>
                : config.data.riskTier === 'Graph'
                  ? <span className="az-badge success">Full — Entra ID P2</span>
                  : <span className="az-badge warning">Degraded — no Entra ID P2</span> },
            { label: 'Scoring cadence', value: config.data ? `every ${config.data.analysisIntervalMs / 1000}s over a ${config.data.analysisWindowMs / 1000}s window` : '—' },
            // Three states, not two. When the media service is unreachable we do not know
            // the mode, and rendering "Shadow mode" would assert something false about
            // whether this system is currently allowed to act on a user's account.
            { label: 'Autonomous actions', value: !config.data
                ? <span className="az-badge">Unknown — service unreachable</span>
                : config.data.autonomousActionsEnabled
                  ? <span className="az-badge success">Enabled</span>
                  : <span className="az-badge warning">Shadow mode</span> },
          ]}
        />

        {config.data && config.data.riskTier !== 'Graph' && (
          <MessageBar intent="warning" title="Running in degraded remediation tier.">
            This tenant has no Entra ID P2, so <span className="mono">confirmCompromised</span> is
            unavailable and risk elevation will return 403. Remediation falls through to session
            revocation, Conditional Access quarantine, and a Sentinel incident. Every attempt is
            recorded with its real outcome rather than reported as a success.
          </MessageBar>
        )}

        <Card
          title={active.length > 0 ? 'Live calls' : 'No call in progress'}
          icon={<IconPhone size={15} />}
          source="live"
          degraded={live.degraded}
          actions={active.length > 0 && <span className="az-badge error"><span className="az-dot az-pulse" />Live</span>}
        >
          {active.length === 0 ? (
            <Empty
              title="Standing by"
              detail="Interception begins the moment a monitored ACS identity rings. To exercise the pipeline now, open Live calls and run a simulation."
            />
          ) : (
            <>
              <RiskMeter score={peakLive} />
              <div style={{ marginTop: 14 }}>
                <DataTable
                  filterable={false}
                  columns={[
                    { key: 'Session' }, { key: 'Protected user' }, { key: 'Stage' },
                    { key: 'Risk', align: 'right' }, { key: 'Vectors' }, { key: 'Actions taken' },
                  ]}
                  rows={active.map((session) => [
                    session.sessionId.slice(0, 10),
                    session.subjectUpn ?? '—',
                    session.stage,
                    Math.round(session.riskScore),
                    session.vectors.join(', ') || '—',
                    session.actionsTaken.filter((a) => a !== 'LogTelemetry').join(', ') || '—',
                  ])}
                  emptyTitle="No active calls"
                  emptyDetail=""
                />
              </div>
            </>
          )}
        </Card>

        <div className="az-grid c3">
          <Card title={`Calls scored · last ${hours}h`} icon={<IconPhone size={15} />} source="kql" degraded={calls.degraded}>
            <div className="az-metric-row">
              <Metric value={calls.data.rows.length} label="Intercepted" />
              <Metric value={highRisk} label="High risk" tone={highRisk > 0 ? 'error' : 'muted'} />
            </div>
          </Card>

          <Card title="Sentinel incidents" icon={<IconSiem size={15} />} source="arm" degraded={incidents.degraded}>
            {incidents.data.length === 0 ? (
              <Empty title="Queue clear" detail="No EntraGuard incidents raised." />
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                {incidents.data.slice(0, 3).map((incident) => (
                  <div key={incident.name}>
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 3 }}>
                      <span className={`az-badge ${incident.properties.severity === 'High' ? 'error' : 'warning'}`}>
                        {incident.properties.severity}
                      </span>
                      <span className="mono t-muted">#{incident.properties.incidentNumber}</span>
                    </div>
                    <div style={{ fontSize: 12.5, color: 'var(--az-text-2)', lineHeight: 1.4 }}>
                      {incident.properties.title}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </Card>

          <Card title="Identity Protection" icon={<IconPerson size={15} />} source="graph" degraded={riskyUsers.degraded}>
            {riskyUsers.data.length === 0 ? (
              <Empty
                title={riskyUsers.degraded ? 'Not readable' : 'No risky users'}
                detail={riskyUsers.degraded ? 'See the note above.' : 'Entra ID Protection reports no elevated users.'}
              />
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {riskyUsers.data.slice(0, 4).map((user) => (
                  <div key={user.id} style={{ display: 'flex', justifyContent: 'space-between', gap: 10 }}>
                    <span style={{ fontSize: 12.5, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {user.userPrincipalName}
                    </span>
                    <span className={`az-badge ${user.riskLevel === 'high' ? 'error' : 'warning'}`}>
                      {user.riskLevel}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        <Card
          title={`Intercepted calls · last ${hours}h`}
          icon={<IconShield size={15} />}
          source="kql"
          degraded={calls.degraded}
          flush
        >
          <DataTable
            columns={calls.data.columns
              .filter((column) => column !== 'Rationale')
              .map((column) => ({
                key: column,
                label: column.replace(/([A-Z])/g, ' $1').trim(),
                align: ['PeakRisk', 'PeakConfidence', 'Assessments'].includes(column) ? 'right' : 'left',
                format: column === 'PeakRisk' ? 'risk' : 'text',
              }))}
            rows={calls.data.rows.map((row) =>
              row.filter((_, index) => calls.data.columns[index] !== 'Rationale'))}
            emptyTitle="Nothing scored yet"
            emptyDetail="EntraGuard_CallAnalysis_CL fills on the first intercepted or simulated call."
          />
        </Card>
      </div>
    </>
  );
}
