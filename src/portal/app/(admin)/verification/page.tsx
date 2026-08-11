import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, Empty, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { IconPhone, IconCheck, IconWarning } from '@/components/az/Icons';
import { runKql } from '@/lib/azure/logs';
import { getRecentVerifications } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

export default async function VerificationPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  const [ledger, live] = await Promise.all([
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h)
       | project TimeGenerated, VerificationId, SubjectUpn, ApplicationName, Result, Reason,
                 Attempts, PeakRiskDuringCall, DurationMs,
                 // Voice biometrics. Written for every verification, including the ones
                 // where nothing was compared — a blank column would be indistinguishable
                 // from the feature being switched off.
                 VoiceOutcome, VoiceScore
       | order by TimeGenerated desc
       | take 100`,
      hours,
    ),
    getRecentVerifications(),
  ]);

  const resultIndex = ledger.data.columns.indexOf('Result');
  const tally = (name: string) =>
    ledger.data.rows.filter((row) => row[resultIndex] === name).length;

  // How many of these verifications actually had a voice compared. Reported because
  // "no voice data" and "voice found nothing wrong" look identical in a table of blanks,
  // and only one of them means the factor is working.
  const voiceIndex = ledger.data.columns.indexOf('VoiceOutcome');
  const voiceScored = voiceIndex >= 0
    ? ledger.data.rows.filter((row) => {
        const outcome = row[voiceIndex];
        return outcome && outcome !== 'NotAssessed';
      }).length
    : 0;

  const passed = tally('Passed');
  const blocked = tally('BlockedCoercion');
  const failed = tally('Failed') + tally('Timeout') + tally('CallFailed');
  const total = ledger.data.rows.length;

  return (
    <>
      <Breadcrumb trail={['EntraGuard', 'Voice verification']} />
      <PageHead
        title="Voice verification"
        subtitle="Step-up authentication challenges EntraGuard placed on behalf of relying applications, and how each one resolved."
        icon={<IconPhone size={17} />}
      />
      <CommandBar />

      <div className="az-content">
        <div className="az-grid c5">
          <Card title="Passed" icon={<IconCheck size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric value={passed} label="Access granted" tone="success" />
          </Card>
          <Card title="Blocked — coercion" icon={<IconWarning size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric value={blocked} label="Correct code, refused anyway" tone={blocked > 0 ? 'error' : 'muted'} />
          </Card>
          <Card title="Failed" source="kql" degraded={ledger.degraded}>
            <Metric value={failed} label="Wrong code, timeout, or no answer" tone={failed > 0 ? 'warning' : 'muted'} />
          </Card>
          <Card title="In flight" source="live" degraded={live.degraded}>
            <Metric value={live.data.filter((v) => !v.isComplete).length} label="Awaiting response" />
          </Card>
          <Card title="Voice compared" source="kql" degraded={ledger.degraded}>
            <Metric
              value={voiceScored}
              label={voiceScored > 0
                ? 'Speaker checked against an enrolled profile'
                : 'Nobody has enrolled a voice yet'}
              tone={voiceScored > 0 ? 'success' : 'muted'}
            />
          </Card>
        </div>

        {blocked > 0 && (
          <MessageBar intent="error" title={`${blocked} verification${blocked === 1 ? '' : 's'} blocked for coercion.`}>
            The user entered the correct number match and access was refused anyway, because
            EntraGuard detected they were being coached during the verification call. These are
            the highest-signal events this system produces — a valid credential presented under
            duress. Each one warrants a conversation with the user.
          </MessageBar>
        )}

        <MessageBar intent="info" title="Why this factor is different.">
          Number matching proves the person holding the phone is the person at the browser,
          which defeats the attacker-on-phone / victim-at-browser split that ordinary push MFA
          falls to. It cannot prove the person is acting freely — someone saying &ldquo;press four
          seven&rdquo; satisfies it perfectly. Monitoring the verification call with the Analyst is
          what closes that gap.
        </MessageBar>

        <Card
          title={`Verification attempts · last ${hours}h`}
          icon={<IconPhone size={15} />}
          source="kql"
          degraded={ledger.degraded}
          flush
          footer={total > 0 ? `${passed} passed · ${blocked} blocked for coercion · ${failed} failed` : undefined}
        >
          <DataTable
            columns={ledger.data.columns.map((column) => ({
              key: column,
              label: column.replace(/([A-Z])/g, ' $1').trim(),
              align: ['Attempts', 'PeakRiskDuringCall', 'DurationMs'].includes(column) ? 'right' : 'left',
              format:
                column === 'Result' ? 'verificationResult'
                : column === 'PeakRiskDuringCall' ? 'risk'
                : 'text',
            }))}
            rows={ledger.data.rows}
            emptyTitle="No verification attempts yet"
            emptyDetail="Populates when a relying application requests a step-up voice challenge. Try the Contoso Treasury demo app at /app."
          />
        </Card>
      </div>
    </>
  );
}
