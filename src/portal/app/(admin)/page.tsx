import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Essentials } from '@/components/az/Essentials';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { RiskMeter } from '@/components/az/RiskMeter';
import { IconShield, IconSiem, IconPhone, IconCheck, IconWarning } from '@/components/az/Icons';
import { getTenant } from '@/lib/azure/graph';
import { getSentinelIncidents, runKql } from '@/lib/azure/logs';
import { getLiveSessions, getRuntimeConfig } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

/**
 * The Overview, rebuilt on the one table that has rows.
 *
 * It used to lead with EntraGuard_CallAnalysis_CL — intercepted calls — which is empty in
 * this deployment because interception only fills on a simulation somebody remembers to run.
 * Two-thirds of the page was therefore an empty state, on the first screen a visitor sees.
 * Everything below now comes from EntraGuard_Verification_CL, which records every real
 * verification, plus live session state and Sentinel incidents that are genuinely populated.
 */
export default async function OverviewPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  const [tenant, incidents, verifications, riskBands, prevented, trend, live, config] = await Promise.all([
    getTenant(),
    getSentinelIncidents(5),

    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h)
       | project TimeGenerated, SubjectUpn, ApplicationName, Result, RiskScore, RiskBand,
                 VoiceOutcome, VoiceScore, PeakRiskDuringCall, Attempts, DurationMs
       | order by TimeGenerated desc
       | take 50`,
      hours,
    ),

    // The distribution, not an average. Averaging risk across every verification hides the
    // handful that matter behind the many that are routine, which is the opposite of what
    // this number is for.
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h)
       | summarize Verifications = count(), HighestRisk = max(RiskScore) by RiskBand
       | order by HighestRisk desc`,
      hours,
    ),

    // Every sign-in this system stopped, and why.
    //
    // The headline claim of the product is "we refuse authentications that should not
    // succeed". Until now the console could only show that a refusal happened, never what it
    // objected to, so the claim rested on the reader taking it on trust.
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h) and GrantsAccess == false
       | project TimeGenerated, SubjectUpn, ApplicationName, Result, RiskScore, RiskBand,
                 Reason, EndpointKind
       | order by TimeGenerated desc
       | take 25`,
      hours,
    ),

    // Shape over time. One column per hour, so a burst against one account is visible as a
    // burst rather than averaged into a day.
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h)
       | summarize Granted = countif(GrantsAccess == true),
                   Prevented = countif(GrantsAccess == false),
                   PeakRisk = max(RiskScore)
                 by bin(TimeGenerated, 1h)
       | order by TimeGenerated asc`,
      hours,
    ),

    getLiveSessions(),
    getRuntimeConfig(),
  ]);

  const active = live.data.filter((session) => session.isActive);
  const peakLive = active.reduce((max, session) => Math.max(max, session.riskScore), 0);

  const column = (name: string) => verifications.data.columns.indexOf(name);
  const rows = verifications.data.rows;

  const resultAt = column('Result');
  const riskAt = column('RiskScore');
  const voiceAt = column('VoiceOutcome');

  const count = (predicate: (row: unknown[]) => boolean) => rows.filter(predicate).length;

  const passed = count((row) => row[resultAt] === 'Passed');
  // From the dedicated query rather than counted off the sample above: the ledger is capped
  // at 50 rows, and a count taken from a capped sample under-reports exactly when there is
  // most to report.
  const refused = prevented.data.rows.length;

  // Counted separately, because they mean very different things. "Prevented" is every
  // authentication that did not complete, including an honest user who mistyped. "Refused
  // despite a correct code" is the subset where the credential was RIGHT and EntraGuard said
  // no anyway — coercion heard, or a voice that did not match. Reporting the first under the
  // second's label would inflate the product's central claim with ordinary typos.
  const refusedDespiteCorrectCode = count((row) =>
    row[resultAt] === 'BlockedCoercion' || row[resultAt] === 'BlockedVoiceMismatch');
  const needsReview = count((row) => Number(row[riskAt] ?? 0) >= 25);
  const voiceCompared = count((row) => Boolean(row[voiceAt]) && row[voiceAt] !== 'NotAssessed');

  // The single most useful number on the page: the riskiest thing that happened, whether or
  // not it was refused. A verification that PASSED at high risk is the one nobody would
  // otherwise go looking for.
  const peakVerificationRisk = rows.reduce<number>(
    (max, row) => Math.max(max, Number(row[riskAt] ?? 0)), 0);

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Overview']} />
      <PageHead
        title="Overview"
        subtitle="Voice verification and anti-scam defence for Microsoft Entra ID sign-ins."
        icon={<IconShield size={17} />}
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
            { label: 'Scoring cadence', value: config.data
                ? `every ${config.data.analysisIntervalMs / 1000}s over a ${config.data.analysisWindowMs / 1000}s window`
                : '—' },
            // Three states, not two. When the media service is unreachable we do not know the
            // mode, and rendering "Shadow mode" would assert something false about whether
            // this system is currently allowed to act on a user's account.
            { label: 'Autonomous actions', value: !config.data
                ? <span className="az-badge">Unknown — service unreachable</span>
                : config.data.autonomousActionsEnabled
                  ? <span className="az-badge success">Enabled</span>
                  : <span className="az-badge warning">Shadow mode</span> },
            { label: 'Remediation tier', value: !config.data
                ? <span className="az-badge">Unknown — service unreachable</span>
                : config.data.riskTier === 'Graph'
                  ? <span className="az-badge success">Full — Entra ID P2</span>
                  : <span className="az-badge warning">Degraded — no Entra ID P2</span> },
          ]}
        />

        <div className="az-grid c4">
          <Card title={`Verifications · last ${hours}h`} icon={<IconPhone size={15} />} source="kql" degraded={verifications.degraded}>
            <Metric value={rows.length} label="Step-up challenges placed" />
          </Card>
          <Card title="Granted" icon={<IconCheck size={15} />} source="kql" degraded={verifications.degraded}>
            <Metric value={passed} label="Identity confirmed" tone={passed > 0 ? 'success' : 'muted'} />
          </Card>
          <Card title="Prevented" icon={<IconWarning size={15} />} source="kql" degraded={prevented.degraded}>
            <Metric
              value={refused}
              label={refusedDespiteCorrectCode > 0
                ? `Access not granted · ${refusedDespiteCorrectCode} despite a correct code`
                : 'Access not granted'}
              tone={refused > 0 ? 'error' : 'muted'}
            />
          </Card>
          <Card title="Worth a look" source="kql" degraded={verifications.degraded}>
            <Metric
              value={needsReview}
              label="Scored Moderate or above"
              tone={needsReview > 0 ? 'warning' : 'muted'}
            />
          </Card>
        </div>

        <div className="az-grid c2">
          <Card
            title="Highest verification risk"
            icon={<IconShield size={15} />}
            source="kql"
            degraded={verifications.degraded}
            footer={
              'Composite of coercion analysis, voice match, attempts consumed and how the '
              + 'user was reached. Recorded and shown; it changes no access decision.'
            }
          >
            <RiskMeter score={peakVerificationRisk} />
            <div style={{ marginTop: 14 }}>
              <DataTable
                filterable={false}
                columns={riskBands.data.columns.map((name) => ({
                  key: name,
                  align: name === 'RiskBand' ? 'left' : 'right',
                }))}
                rows={riskBands.data.rows}
                emptyTitle="Nothing scored yet"
                emptyDetail="A risk band is recorded for every verification that completes."
              />
            </div>
          </Card>

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
                detail="A verification call appears here the moment one is placed. To exercise the interception pipeline instead, open Live calls and run a simulation."
              />
            ) : (
              <>
                <RiskMeter score={peakLive} />
                <div style={{ marginTop: 14 }}>
                  <DataTable
                    filterable={false}
                    columns={[
                      { key: 'Session' }, { key: 'Protected user' }, { key: 'Stage' },
                      { key: 'Risk', align: 'right' },
                    ]}
                    rows={active.map((session) => [
                      session.sessionId.slice(0, 10),
                      session.subjectUpn ?? '—',
                      session.stage,
                      Math.round(session.riskScore),
                    ])}
                    emptyTitle="No active calls"
                    emptyDetail=""
                  />
                </div>
              </>
            )}
          </Card>
        </div>

        <Card
          title={`Verification volume · last ${hours}h`}
          icon={<IconPhone size={15} />}
          source="kql"
          degraded={trend.degraded}
          footer="One column per hour. A burst against a single account looks like a burst here rather than being averaged away."
        >
          {trend.data.rows.length === 0 ? (
            <Empty
              title="No verifications in this window"
              detail="Widen the time range, or run one from the Contoso Treasury app."
            />
          ) : (
            <div className="az-bars" role="img" aria-label="Verifications per hour">
              {trend.data.rows.map((row, index) => {
                const granted = Number(row[trend.data.columns.indexOf('Granted')] ?? 0);
                const stopped = Number(row[trend.data.columns.indexOf('Prevented')] ?? 0);
                const tallest = Math.max(
                  1,
                  ...trend.data.rows.map((r) =>
                    Number(r[trend.data.columns.indexOf('Granted')] ?? 0)
                    + Number(r[trend.data.columns.indexOf('Prevented')] ?? 0)),
                );

                return (
                  <div
                    key={index}
                    className="az-bar"
                    style={{
                      height: `${((granted + stopped) / tallest) * 100}%`,
                      // Prevented sits at the bottom in the alert colour, so the eye lands on
                      // the hours where something was stopped rather than on total volume.
                      background: stopped > 0
                        ? `linear-gradient(to top, var(--az-error) ${(stopped / (granted + stopped)) * 100}%, var(--az-blue) 0)`
                        : 'var(--az-blue)',
                    }}
                    title={`${granted} granted · ${stopped} prevented`}
                  />
                );
              })}
            </div>
          )}
        </Card>

        <Card
          title={`Prevented sign-ins · last ${hours}h`}
          icon={<IconWarning size={15} />}
          source="kql"
          degraded={prevented.degraded}
          flush
          footer="Every authentication EntraGuard refused, with the reason it gave and the risk it scored."
        >
          <DataTable
            columns={prevented.data.columns.map((name) => ({
              key: name,
              label: name.replace(/([A-Z])/g, ' $1').trim(),
              align: name === 'RiskScore' ? 'right' : 'left',
              width: name === 'Reason' ? 'wide'
                : ['TimeGenerated', 'Result', 'RiskBand', 'RiskScore', 'EndpointKind'].includes(name)
                  ? 'narrow'
                  : undefined,
              format: name === 'Result' ? 'verificationResult' : name === 'RiskScore' ? 'risk' : 'text',
            }))}
            rows={prevented.data.rows}
            emptyTitle="Nothing was refused in this window"
            emptyDetail="A row appears here whenever a verification ends without granting access — a wrong code, a timeout, coercion detected, or a voice that did not match."
          />
        </Card>

        {refused > 0 && (
          <MessageBar
            intent="error"
            title={refusedDespiteCorrectCode > 0
              ? `${refusedDespiteCorrectCode} sign-in${refusedDespiteCorrectCode === 1 ? '' : 's'} refused despite a correct number match.`
              : `${refused} sign-in${refused === 1 ? '' : 's'} prevented.`}
          >
            Each one is an authentication that did not complete. The most interesting are those
            where the user entered the RIGHT number match and was refused anyway — because
            EntraGuard heard them being coached, or because the voice did not match the enrolled
            speaker. A valid credential, presented under conditions that made it worthless.
          </MessageBar>
        )}

        <div className="az-grid c2">
          <Card title="Sentinel incidents" icon={<IconSiem size={15} />} source="arm" degraded={incidents.degraded}>
            {incidents.data.length === 0 ? (
              <Empty title="Queue clear" detail="No EntraGuard incidents raised." />
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                {incidents.data.slice(0, 4).map((incident) => (
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

          <Card
            title="Voice comparison coverage"
            icon={<IconCheck size={15} />}
            source="kql"
            degraded={verifications.degraded}
            footer={
              'A voice is only compared when the user has enrolled a profile and speaks for '
              + 'at least three seconds. Coverage is reported rather than assumed.'
            }
          >
            <div className="az-metric-row">
              <Metric value={voiceCompared} label="Compared against a profile" tone={voiceCompared > 0 ? 'success' : 'muted'} />
              <Metric value={rows.length - voiceCompared} label="Not assessed" tone="muted" />
            </div>
          </Card>
        </div>

        <Card
          title={`Recent verifications · last ${hours}h`}
          icon={<IconShield size={15} />}
          source="kql"
          degraded={verifications.degraded}
          flush
        >
          <DataTable
            columns={verifications.data.columns.map((name) => ({
              key: name,
              label: name.replace(/([A-Z])/g, ' $1').trim(),
              align: ['RiskScore', 'VoiceScore', 'PeakRiskDuringCall', 'Attempts', 'DurationMs'].includes(name)
                ? 'right'
                : 'left',
              format: name === 'Result'
                ? 'verificationResult'
                : name === 'RiskScore' || name === 'PeakRiskDuringCall'
                  ? 'risk'
                  : 'text',
            }))}
            rows={verifications.data.rows}
            emptyTitle="No verifications yet"
            emptyDetail="EntraGuard_Verification_CL fills the moment a step-up challenge completes."
          />
        </Card>
      </div>
    </>
  );
}
