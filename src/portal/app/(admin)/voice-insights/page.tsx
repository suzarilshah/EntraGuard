import { Breadcrumb } from '@/components/az/Chrome';
import { CommandBar } from '@/components/az/CommandBar';
import { Card, MessageBar, PageHead } from '@/components/az/Surfaces';
import { DataTable } from '@/components/az/DataTable';
import { AdminGlyph } from '@/components/az/AdminGlyph';
import { runKql } from '@/lib/azure/logs';
import { timeRange, verificationBase } from '@/lib/adminData';

export const dynamic = 'force-dynamic';
export default async function VoiceInsights({ searchParams }: { searchParams: Promise<{ hours?: string; simulations?: string }> }) {
  const params = await searchParams; const hours = timeRange(params.hours); const base = verificationBase(hours, params.simulations === 'include');
  const [scores, followUps, lifecycle] = await Promise.all([
    runKql(`${base} | where VoiceOutcome in ("Match","Mismatch","Inconclusive") and isnotnull(VoiceScore)
      | summarize Calls=count(), Lowest=min(VoiceScore), Median=percentile(VoiceScore,50), Highest=max(VoiceScore) by VoiceOutcome | order by VoiceOutcome asc`, hours),
    runKql(`${base} | where FollowUpsAsked > 0 | mv-expand Probe=FollowUps
      | extend Facet=coalesce(tostring(Probe.Facet),tostring(Probe.facet)), Correct=coalesce(tobool(Probe.Correct),tobool(Probe.correct)), Heard=coalesce(tobool(Probe.Answered),tobool(Probe.answered))
      | summarize Asked=count(), Confirmed=countif(Correct), NoSpeech=countif(not(Heard)) by Facet | order by Asked desc`, hours),
    runKql(`EntraGuard_Biometric_CL | where TimeGenerated > ago(${hours}h)
      | project TimeGenerated,EventType,SubjectUpn,ConsentVersion,PhraseCount,SelfConsistency,UsedMfa,Reason | order by TimeGenerated desc | take 100`, hours),
  ]);
  return <><Breadcrumb trail={['EntraGuard','Voice insights']} /><PageHead title="Voice insights" subtitle="Supplementary voice evidence, follow-up outcomes and profile lifecycle. These measurements are not a validated accuracy benchmark." icon={<AdminGlyph name="activity" size={27} />} /><CommandBar simulations />
    <div className="az-content"><MessageBar intent="info" title="A comparison is not an identity guarantee.">No scored voice can mean no profile, insufficient speech or an unavailable scorer. It does not establish that nobody has enrolled. Threshold enforcement is controlled by the service, not this view.</MessageBar>
      <div className="az-grid c2"><Card title="Voice comparisons by outcome" source="kql" degraded={scores.degraded} flush footer="Zero similarity is a valid score and is retained. Match/mismatch labels are model outcomes, not ground-truth genuine/impostor labels."><DataTable title="Voice comparisons" columns={scores.data.columns.map(key => ({key,align:key==='VoiceOutcome'?'left':'right'}))} rows={scores.data.rows} emptyTitle="No assessed comparisons in this window" emptyDetail="Profiles, audio availability and scorer connectivity all affect coverage." /></Card>
      <Card title="Follow-up evidence" source="kql" degraded={followUps.degraded} flush footer="Follow-up outcomes supplement evidence. They are not an independent pass/fail decision."><DataTable title="Follow-up evidence" columns={followUps.data.columns.map(key=>({key}))} rows={followUps.data.rows} emptyTitle="No follow-ups recorded" emptyDetail="Not every verification needs a follow-up question." /></Card></div>
      <Card title="Voice profile lifecycle" source="kql" degraded={lifecycle.degraded} flush footer="Latest 100 lifecycle records. This table can include enrollment rehearsals; simulation filtering above applies to verification-derived panels."><DataTable title="Voice profile lifecycle" columns={lifecycle.data.columns.map(key=>({key,format:key==='TimeGenerated'?'datetime':key==='UsedMfa'?'boolean':'text'}))} rows={lifecycle.data.rows} emptyTitle="No lifecycle events in this window" emptyDetail="Enrollment, re-enrollment, failure and deletion events appear after ingestion." /></Card>
    </div></>;
}
