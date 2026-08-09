import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { IconPerson, IconCheck } from '@/components/az/Icons';
import { getRecentSignIns, getRiskDetections, getRiskyUsers } from '@/lib/azure/graph';
import { getRemediationLedger } from '@/lib/azure/logs';

export const dynamic = 'force-dynamic';
export const revalidate = 0;


export default async function IdentityPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  const [riskyUsers, detections, signIns, ledger] = await Promise.all([
    getRiskyUsers(20),
    getRiskDetections(20),
    getRecentSignIns(20),
    getRemediationLedger(hours),
  ]);

  const outcomeIndex = ledger.data.columns.indexOf('Outcome');
  const tally = (name: string) =>
    ledger.data.rows.filter((row) => row[outcomeIndex] === name).length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Identity risk']} />
      <PageHead
        title="Identity risk"
        subtitle="What EntraGuard did to the identity plane, and what Entra ID Protection currently believes about these accounts."
        icon={<IconPerson size={17} />}
      />
      <CommandBar />

      <div className="az-content">
        <div className="az-grid c4">
          <Card title="Succeeded" icon={<IconCheck size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric value={tally('Succeeded')} label="Actions completed" tone="success" />
          </Card>
          <Card title="Unavailable" source="kql" degraded={ledger.degraded}>
            <Metric value={tally('Unavailable')} label="Blocked by licensing" tone="warning" />
          </Card>
          <Card title="Withheld" source="kql" degraded={ledger.degraded}>
            <Metric value={tally('BlockedByPolicy')} label="Refused by policy gate" tone="warning" />
          </Card>
          <Card title="Failed" source="kql" degraded={ledger.degraded}>
            <Metric value={tally('Failed')} label="Errored" tone={tally('Failed') > 0 ? 'error' : 'muted'} />
          </Card>
        </div>

        <MessageBar intent="info" title="The ledger records attempts, not just successes.">
          An audit trail that only shows what worked cannot answer &ldquo;why didn&rsquo;t it
          act?&rdquo; — so refusals and licensing failures are recorded exactly like completions.
        </MessageBar>

        <Card
          title="Remediation ledger"
          icon={<IconCheck size={15} />}
          source="kql"
          degraded={ledger.degraded}
          flush
        >
          <DataTable
            columns={ledger.data.columns.map((column) => ({
              key: column,
              label: column.replace(/([A-Z])/g, ' $1').trim(),
              format: column === 'Outcome' ? 'outcome' : column === 'RiskScore' ? 'risk' : 'text',
            }))}
            rows={ledger.data.rows}
            emptyTitle="No remediation recorded"
            emptyDetail="Populates the first time the policy gate authorises or withholds an action."
          />
        </Card>

        <Card
          title="Risky users — Entra ID Protection"
          icon={<IconPerson size={15} />}
          source="graph"
          degraded={riskyUsers.degraded}
          flush
        >
          <DataTable
            columns={[
              { key: 'User' },
              { key: 'Risk level', format: 'riskLevel' },
              { key: 'State' },
              { key: 'Last updated' },
            ]}
            rows={riskyUsers.data.map((user) => [
              user.userPrincipalName, user.riskLevel, user.riskState, user.riskLastUpdatedDateTime,
            ])}
            emptyTitle={riskyUsers.degraded ? 'Not readable in this tenant' : 'No risky users'}
            emptyDetail={
              riskyUsers.degraded
                ? 'EntraGuard falls back to session revocation and Conditional Access quarantine, both recorded in the ledger above.'
                : 'Entra ID Protection reports no users at elevated risk.'
            }
          />
        </Card>

        <div className="az-grid c2">
          <Card title="Risk detections" source="graph" degraded={detections.degraded} flush>
            <DataTable
              filterable={false}
              columns={[{ key: 'User' }, { key: 'Event' }, { key: 'Level' }]}
              rows={detections.data.slice(0, 12).map((d) => [d.userPrincipalName, d.riskEventType, d.riskLevel])}
              emptyTitle={detections.degraded ? 'Not readable' : 'No detections'}
              emptyDetail={detections.degraded ? 'Requires Entra ID P2 and IdentityRiskEvent.Read.All.' : 'Nothing detected recently.'}
            />
          </Card>

          <Card title="Recent sign-ins" source="graph" degraded={signIns.degraded} flush>
            <DataTable
              filterable={false}
              columns={[
                { key: 'User' }, { key: 'Application' },
                { key: 'Result', format: 'signInResult' },
              ]}
              rows={signIns.data.slice(0, 12).map((s) => [s.userPrincipalName, s.appDisplayName, s.status.errorCode])}
              emptyTitle={signIns.degraded ? 'Not readable' : 'No sign-ins'}
              emptyDetail={
                signIns.degraded
                  ? 'Sign-in logs need AuditLog.Read.All and an Entra ID P1 or P2 tenant.'
                  : 'No interactive sign-ins in the retention window.'
              }
            />
          </Card>
        </div>
      </div>
    </>
  );
}
