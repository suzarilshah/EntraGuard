/** Input validation and query construction shared by the operator blades. */
export const TIME_RANGES = [1, 6, 24, 168, 720] as const;
export function timeRange(value?: string | null): number {
  const hours = Number(value);
  return TIME_RANGES.includes(hours as typeof TIME_RANGES[number]) ? hours : 24;
}

export function verificationBase(hours: number, includeSimulated = false): string {
  const safeHours = timeRange(String(hours));
  return `EntraGuard_Verification_CL
| where TimeGenerated > ago(${safeHours}h)
| where isnotempty(VerificationId)
| summarize arg_max(TimeGenerated, *) by VerificationId
${includeSimulated ? '' : '| where ApplicationName !contains "(simulated)"'}`;
}

export function overviewQueries(hours: number, includeSimulated = false) {
  const base = verificationBase(hours, includeSimulated);
  const bin = hours > 168 ? '1d' : hours > 24 ? '6h' : '1h';
  return {
    summary: `${base}
| summarize Total=count(), Passed=countif(Result == "Passed"),
    Coercion=countif(Result == "BlockedCoercion"), StepUp=countif(Result == "StepUpRequired"),
    NotCompleted=countif(Result in ("Failed", "Timeout", "CallFailed")),
    LegacyVoiceRefusal=countif(Result == "BlockedVoiceMismatch"),
    VoiceCompared=countif(VoiceOutcome in ("Match", "Mismatch", "Inconclusive")),
    MedianDurationMs=percentile(DurationMs, 50)`,
    trend: `${base}
| summarize Passed=countif(Result == "Passed"), Coercion=countif(Result == "BlockedCoercion"),
    StepUp=countif(Result == "StepUpRequired"), Other=countif(Result !in ("Passed", "BlockedCoercion", "StepUpRequired")) by bin(TimeGenerated, ${bin})
| order by TimeGenerated asc`,
    recent: `${base}
| project TimeGenerated, VerificationId, SubjectUpn, ApplicationName, Result, Reason,
    AssuranceLevel=column_ifexists("AssuranceLevel", ""), EndpointKind, DurationMs, PeakRiskDuringCall
| order by TimeGenerated desc | take 100`,
  };
}

export function resourceGroup(): string | undefined {
  return process.env.AZURE_RESOURCE_GROUP || process.env.LAW_RESOURCE_ID?.match(/\/resourceGroups\/([^/]+)\//i)?.[1];
}
export function azureResourceUrl(id: string): string {
  return `https://portal.azure.com/#resource${id}/overview`;
}
export function queryNumber(result: { data: { columns: string[]; rows: unknown[][] }; degraded?: string }, key: string): number | null {
  if (result.degraded || !result.data.rows.length) return null;
  const value = result.data.rows[0][result.data.columns.indexOf(key)];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}
export function odataPrefix(value: string): string { return value.trim().slice(0, 80).replaceAll("'", "''"); }
