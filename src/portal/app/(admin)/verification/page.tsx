import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, MessageBar, Metric, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { IconPhone, IconCheck, IconWarning } from '@/components/az/Icons';
import { runKql } from '@/lib/azure/logs';
import { getRecentVerifications } from '@/lib/azure/mediaService';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

/** Right-aligned, because comparing magnitudes down a column needs the digits to line up. */
const NUMERIC = ['Attempts', 'PeakRiskDuringCall', 'DurationMs', 'RiskScore', 'VoiceScore'];

/** Sentences. These need room, and the table will not give it to them unasked. */
const WIDE = ['Reason', 'VoiceDetail'];

/** Short and fixed. Giving up their slack is what lets the sentences have it. */
const NARROW = ['TimeGenerated', 'Result', 'RiskBand', 'VoiceOutcome', 'Attempts', 'DurationMs'];

export default async function VerificationPage({
  searchParams,
}: {
  searchParams: Promise<{ hours?: string }>;
}) {
  const hours = Number((await searchParams).hours ?? 24);

  const [ledger, live, voiceScores, biometrics] = await Promise.all([
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h)
       | project TimeGenerated, VerificationId, SubjectUpn, ApplicationName, Result, Reason,
                 Attempts, PeakRiskDuringCall, DurationMs,
                 // Voice biometrics. Written for every verification, including the ones
                 // where nothing was compared — a blank column would be indistinguishable
                 // from the feature being switched off.
                 VoiceOutcome, VoiceScore, VoiceDetail, RiskScore, RiskBand
       | order by TimeGenerated desc
       | take 100`,
      hours,
    ),
    getRecentVerifications(),

    // The calibration dataset, as a distribution rather than a single number.
    //
    // Thresholds currently come from synthesised voices — genuine 0.652 to 0.865, impostor
    // -0.039 to 0.297 — and telephony will narrow that. These are the real scores that
    // replace them, so they are shown split by the verdict the call reached rather than
    // averaged into one figure that would hide the overlap that matters.
    runKql(
      `EntraGuard_Verification_CL
       | where TimeGenerated > ago(${hours}h) and isnotnull(VoiceScore) and VoiceScore != 0
       | summarize Calls = count(), Lowest = min(VoiceScore), Median = percentile(VoiceScore, 50),
                   Highest = max(VoiceScore) by VoiceOutcome
       | order by VoiceOutcome asc`,
      hours,
    ),

    // Consent and its withdrawal. GDPR Article 9 asks for exactly these two events, and
    // until recently neither left the container it happened in.
    runKql(
      `EntraGuard_Biometric_CL
       | where TimeGenerated > ago(${hours}h)
       | project TimeGenerated, EventType, SubjectUpn, ConsentVersion, PhraseCount,
                 SelfConsistency, UsedMfa, Reason
       | order by TimeGenerated desc
       | take 50`,
      hours,
    ),
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
  const blockedVoice = tally('BlockedVoiceMismatch');
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
        {/*
          Three across, two rows — not six across.
          
          Six tiles on a 1280px viewport leaves roughly 105px each, and the titles wrapped to
          three lines: "Blocked / — / coercion". A number nobody can read the label of is not
          a metric, it is decoration.
        */}
        <div className="az-grid c3">
          <Card title="Passed" icon={<IconCheck size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric value={passed} label="Access granted" tone="success" />
          </Card>
          <Card title="Blocked — coercion" icon={<IconWarning size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric value={blocked} label="Correct code, refused anyway" tone={blocked > 0 ? 'error' : 'muted'} />
          </Card>
          <Card title="Blocked — voice" icon={<IconWarning size={15} />} source="kql" degraded={ledger.degraded}>
            <Metric
              value={blockedVoice}
              label="Correct code, wrong voice"
              tone={blockedVoice > 0 ? 'error' : 'muted'}
            />
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

        {/*
          The measured score distribution, which is the only thing that can justify moving
          the thresholds. Shipped thresholds came from synthesised voices and are knowingly
          optimistic against a phone line, so this table is the evidence that replaces them.
        */}
        <Card
          title="Voice scores measured on real calls"
          icon={<IconPhone size={15} />}
          source="kql"
          degraded={voiceScores.degraded}
          footer={
            'Accept at 0.60, refuse below 0.35 — both derived from synthesised voices, where '
            + 'genuine pairs scored 0.652 to 0.865 and impostors -0.039 to 0.297. Telephony '
            + 'narrows that gap, so these are the numbers that should replace them.'
          }
        >
          <DataTable
            columns={voiceScores.data.columns.map((column) => ({
              key: column,
              align: column === 'VoiceOutcome' ? 'left' : 'right',
            }))}
            rows={voiceScores.data.rows}
            emptyTitle="No voice has been compared yet"
            emptyDetail="A score is recorded on every verification where the caller has an
                         enrolled profile and spoke for at least three seconds. Nothing is
                         estimated here — this stays empty until real calls produce real
                         numbers."
          />
        </Card>

        {/*
          Consent and its withdrawal. Article 9 of the GDPR treats a voiceprint as special
          category data, and these are the two events a regulator asks about first.
        */}
        <Card
          title="Voice profile lifecycle"
          icon={<IconCheck size={15} />}
          source="kql"
          degraded={biometrics.degraded}
          footer={
            'Enrolment requires an interactive sign-in with a second factor. Raw audio is '
            + 'never stored — three phrases become one 192-dimension template and the '
            + 'recordings are discarded. Deletion is immediate and user-initiated.'
          }
        >
          <DataTable
            columns={biometrics.data.columns.map((column) => ({ key: column }))}
            rows={biometrics.data.rows}
            emptyTitle="No voice profile has been created or removed"
            emptyDetail="Enrolments, re-recordings, failures and deletions appear here with the
                         version of the consent text the user agreed to."
          />
        </Card>

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
              align: NUMERIC.includes(column) ? 'right' : 'left',
              // Reason and VoiceDetail are sentences; everything else is a word, a number or
              // an identifier. Without this the sentences were squeezed into a two-word
              // ribbon while a timestamp sat in comfortable whitespace.
              width: WIDE.includes(column) ? 'wide' : NARROW.includes(column) ? 'narrow' : undefined,
              format:
                column === 'Result' ? 'verificationResult'
                : column === 'PeakRiskDuringCall' || column === 'RiskScore' ? 'risk'
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
