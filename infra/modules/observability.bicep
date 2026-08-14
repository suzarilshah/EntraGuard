// ─────────────────────────────────────────────────────────────────────────────
// Log Analytics + Microsoft Sentinel + the custom-log ingestion path.
//
// Ingestion uses the Logs Ingestion API (DCE + DCR), NOT the HTTP Data Collector
// API — that one is retired on 2026-09-14 and enforces TLS 1.2+ since 2026-03-01.
//
// Note on column naming: DCR-based custom tables use explicit column names with
// no type suffix. There is no RiskScore_d here — it is RiskScore. The _s/_d/_g
// suffixes are a legacy Data Collector API artifact and do not apply.
// ─────────────────────────────────────────────────────────────────────────────

param appName string
param environmentName string
param location string
param uniqueSuffix string
param tags object

@description('Principal ID of the identity that publishes custom logs through the DCR.')
param ingestorPrincipalId string

@description('Optional: object ID of a human operator granted direct read access to telemetry.')
param operatorObjectId string = ''

var workspaceName = 'log-${appName}-${environmentName}'
var dceName = 'dce-${appName}-${uniqueSuffix}'
var dcrName = 'dcr-${appName}-${environmentName}'

// ── Workspace ───────────────────────────────────────────────────────────────
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ── Microsoft Sentinel ──────────────────────────────────────────────────────
resource sentinel 'Microsoft.SecurityInsights/onboardingStates@2024-03-01' = {
  name: 'default'
  scope: workspace
  properties: {}
}

// ── Custom tables ───────────────────────────────────────────────────────────
// Per-analysis verdicts. One row every time the Analyst agent scores a window,
// so the risk trajectory across a call is queryable after the fact.
resource callAnalysisTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: workspace
  name: 'EntraGuard_CallAnalysis_CL'
  properties: {
    schema: {
      name: 'EntraGuard_CallAnalysis_CL'
      columns: [
        { name: 'TimeGenerated',      type: 'datetime', description: 'Analysis timestamp (UTC).' }
        { name: 'SessionId',          type: 'string',   description: 'EntraGuard session correlation ID.' }
        { name: 'CallConnectionId',   type: 'string',   description: 'ACS call connection ID.' }
        { name: 'AcsCorrelationId',   type: 'string',   description: 'ACS correlation ID, for support escalation.' }
        { name: 'RiskScore',          type: 'real',     description: 'Composite scam risk, 0-100.' }
        { name: 'Confidence',         type: 'real',     description: 'Analyst confidence, 0.0-1.0.' }
        { name: 'ComplianceStage',    type: 'string',   description: 'unaware | engaged | about_to_approve | approved.' }
        { name: 'Vectors',            type: 'string',   description: 'Comma-separated social-engineering vectors detected.' }
        { name: 'Evidence',           type: 'dynamic',  description: 'Verbatim quotes with speaker and offset.' }
        { name: 'Rationale',          type: 'string',   description: 'Analyst natural-language justification.' }
        { name: 'SubjectUpn',         type: 'string',   description: 'UPN of the protected user.' }
        { name: 'SubjectObjectId',    type: 'string',   description: 'Entra ID object ID of the protected user.' }
        { name: 'CallerIdentity',     type: 'string',   description: 'Raw ACS participant ID of the suspected attacker.' }
        { name: 'TranscriptWindow',   type: 'string',   description: 'Transcript slice scored in this pass.' }
        { name: 'AnalysisLatencyMs',  type: 'int',      description: 'Analyst round-trip latency.' }
        { name: 'ModelDeployment',    type: 'string',   description: 'Azure OpenAI deployment that produced the verdict.' }
      ]
    }
    retentionInDays: 30
    totalRetentionInDays: 30
  }
}

// Remediation attempts with their REAL outcome, including the degraded path.
// Auditing what was *attempted and refused* matters as much as what succeeded.
resource remediationTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: workspace
  name: 'EntraGuard_Remediation_CL'
  properties: {
    schema: {
      name: 'EntraGuard_Remediation_CL'
      columns: [
        { name: 'TimeGenerated',    type: 'datetime', description: 'Action timestamp (UTC).' }
        { name: 'SessionId',        type: 'string',   description: 'EntraGuard session correlation ID.' }
        { name: 'ActionName',       type: 'string',   description: 'Remediation tool invoked.' }
        { name: 'LadderRung',       type: 'int',      description: '1=confirmCompromised, 2=revokeSessions, 3=CA quarantine, 4=Sentinel only.' }
        { name: 'Outcome',          type: 'string',   description: 'Succeeded | Failed | Unavailable | BlockedByPolicy.' }
        { name: 'Reason',           type: 'string',   description: 'Why it succeeded, failed, or was blocked.' }
        { name: 'GraphStatusCode',  type: 'int',      description: 'HTTP status from Microsoft Graph, 0 when not called.' }
        { name: 'SubjectUpn',       type: 'string',   description: 'UPN of the protected user.' }
        { name: 'SubjectObjectId',  type: 'string',   description: 'Entra ID object ID of the protected user.' }
        { name: 'RiskScore',        type: 'real',     description: 'Risk score that triggered the action.' }
        { name: 'DecidedBy',        type: 'string',   description: 'PolicyGate | Actuator | Manual.' }
        { name: 'DurationMs',       type: 'int',      description: 'Wall-clock duration of the action.' }
      ]
    }
    retentionInDays: 30
    totalRetentionInDays: 30
  }
}

// Step-up verification attempts. Separate from CallAnalysis because these are calls
// EntraGuard ORIGINATED as an auth factor, not calls it intercepted — conflating them
// would overstate the interception rate and make "was this call ours?" unanswerable.
resource verificationTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: workspace
  name: 'EntraGuard_Verification_CL'
  properties: {
    schema: {
      name: 'EntraGuard_Verification_CL'
      columns: [
        { name: 'TimeGenerated',      type: 'datetime', description: 'Verdict timestamp (UTC).' }
        { name: 'VerificationId',     type: 'string',   description: 'Verification attempt ID.' }
        { name: 'SubjectUpn',         type: 'string',   description: 'User being verified.' }
        { name: 'SubjectObjectId',    type: 'string',   description: 'Entra ID object ID of the user.' }
        { name: 'ApplicationName',    type: 'string',   description: 'Relying party that requested the step-up.' }
        { name: 'Result',             type: 'string',   description: 'Passed | Failed | BlockedCoercion | BlockedVoiceMismatch | Timeout | CallFailed.' }
        { name: 'Reason',             type: 'string',   description: 'Why the attempt resolved this way.' }
        { name: 'GrantsAccess',       type: 'boolean',  description: 'Whether access was granted.' }
        { name: 'Attempts',           type: 'int',      description: 'Number-match attempts consumed.' }
        { name: 'PeakRiskDuringCall', type: 'real',     description: 'Highest Analyst risk observed while verifying.' }
        { name: 'CallConnectionId',   type: 'string',   description: 'ACS call connection ID.' }
        { name: 'MonitorSessionId',   type: 'string',   description: 'Correlates to the monitored media session.' }
        { name: 'DurationMs',         type: 'int',      description: 'End-to-end verification duration.' }
        { name: 'VoiceScore',         type: 'real',     description: 'Cosine similarity against the enrolled voiceprint, or empty when not assessed.' }
        { name: 'VoiceOutcome',       type: 'string',   description: 'NotAssessed | Match | Inconclusive | Mismatch.' }
        { name: 'LivenessOutcome',    type: 'string',   description: 'NotAssessed | Passed | PhraseMismatch | NoResponse.' }
        { name: 'LivenessLatencyMs',  type: 'int',      description: 'Milliseconds from prompt end to first speech. A liveness signal, not a performance metric.' }
        { name: 'SpoofScore',         type: 'real',     description: 'Presentation-attack probability from the PAD model, or empty when not assessed.' }
        { name: 'VoiceDetail',        type: 'string',   description: 'Why the voice outcome is what it is — no profile, too little speech, scorer unreachable, or the score itself.' }
        { name: 'RiskScore',          type: 'real',     description: 'Composite verification risk 0-100. Recorded and displayed; changes no access decision.' }
        { name: 'RiskBand',           type: 'string',   description: 'Low | Moderate | Elevated | High.' }
        { name: 'RiskContributors',   type: 'dynamic',  description: 'What drove the risk score, largest first, in plain language.' }
        { name: 'EndpointKind',       type: 'string',   description: 'teams | phone | browser — how the challenge was delivered, which changes what the result is worth.' }
      ]
    }
    retentionInDays: 30
    totalRetentionInDays: 30
  }
}

// Biometric lifecycle. Separate from Verification because these are not authentication
// attempts — they are the consent record. GDPR Article 9 treats a voiceprint as special
// category data, and the two events a regulator asks for first are when consent was given
// and when it was withdrawn. Neither was reaching the SIEM at all: enrolment and deletion
// only ever wrote ILogger lines, which are not an audit trail.
resource biometricTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: workspace
  name: 'EntraGuard_Biometric_CL'
  properties: {
    schema: {
      name: 'EntraGuard_Biometric_CL'
      columns: [
        { name: 'TimeGenerated',   type: 'datetime', description: 'Event timestamp (UTC).' }
        { name: 'EventType',       type: 'string',   description: 'Enrolled | ReEnrolled | EnrolmentFailed | Deleted.' }
        { name: 'SubjectUpn',      type: 'string',   description: 'Whose voiceprint this is.' }
        { name: 'SubjectObjectId', type: 'string',   description: 'Entra ID object ID of the user.' }
        { name: 'SubjectTenantId', type: 'string',   description: 'Home tenant of the user.' }
        { name: 'ConsentVersion',  type: 'string',   description: 'Version of the consent text the user agreed to.' }
        { name: 'ConsentAt',       type: 'datetime', description: 'When that consent was recorded.' }
        { name: 'PhraseCount',     type: 'int',      description: 'Utterances captured for the template.' }
        { name: 'SelfConsistency', type: 'real',     description: 'Minimum pairwise agreement between those utterances.' }
        { name: 'Reason',          type: 'string',   description: 'Why an enrolment failed, or how a deletion was initiated.' }
        { name: 'UsedMfa',         type: 'boolean',  description: 'Whether a second factor was proven before enrolling.' }
      ]
    }
    retentionInDays: 30
    totalRetentionInDays: 30
  }
}

// ── Data Collection Endpoint ────────────────────────────────────────────────
resource dce 'Microsoft.Insights/dataCollectionEndpoints@2023-03-11' = {
  name: dceName
  location: location
  tags: tags
  properties: {
    networkAcls: { publicNetworkAccess: 'Enabled' }
  }
}

// ── Data Collection Rule ────────────────────────────────────────────────────
// streamDeclarations must mirror the table schemas exactly, minus TimeGenerated
// on input (Azure Monitor stamps it) — we declare it so the service can honour a
// caller-supplied timestamp, which keeps the risk trajectory ordered correctly
// even when ingestion lags behind the live call.
//
// "Exactly" is load-bearing and this file has already drifted once. Adding a column to a
// table above WITHOUT adding it here does not error: ingestion still returns 204, the
// column simply arrives empty forever. If you add a field, add it in both places and then
// prove a row lands with it populated.
resource dcr 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: dcrName
  location: location
  tags: tags
  properties: {
    dataCollectionEndpointId: dce.id
    streamDeclarations: {
      'Custom-EntraGuard_CallAnalysis_CL': {
        columns: [
          { name: 'TimeGenerated',     type: 'datetime' }
          { name: 'SessionId',         type: 'string' }
          { name: 'CallConnectionId',  type: 'string' }
          { name: 'AcsCorrelationId',  type: 'string' }
          { name: 'RiskScore',         type: 'real' }
          { name: 'Confidence',        type: 'real' }
          { name: 'ComplianceStage',   type: 'string' }
          { name: 'Vectors',           type: 'string' }
          { name: 'Evidence',          type: 'dynamic' }
          { name: 'Rationale',         type: 'string' }
          { name: 'SubjectUpn',        type: 'string' }
          { name: 'SubjectObjectId',   type: 'string' }
          { name: 'CallerIdentity',    type: 'string' }
          { name: 'TranscriptWindow',  type: 'string' }
          { name: 'AnalysisLatencyMs', type: 'int' }
          { name: 'ModelDeployment',   type: 'string' }
        ]
      }
      'Custom-EntraGuard_Remediation_CL': {
        columns: [
          { name: 'TimeGenerated',   type: 'datetime' }
          { name: 'SessionId',       type: 'string' }
          { name: 'ActionName',      type: 'string' }
          { name: 'LadderRung',      type: 'int' }
          { name: 'Outcome',         type: 'string' }
          { name: 'Reason',          type: 'string' }
          { name: 'GraphStatusCode', type: 'int' }
          { name: 'SubjectUpn',      type: 'string' }
          { name: 'SubjectObjectId', type: 'string' }
          { name: 'RiskScore',       type: 'real' }
          { name: 'DecidedBy',       type: 'string' }
          { name: 'DurationMs',      type: 'int' }
        ]
      }
      'Custom-EntraGuard_Verification_CL': {
        columns: [
          { name: 'TimeGenerated',      type: 'datetime' }
          { name: 'VerificationId',     type: 'string' }
          { name: 'SubjectUpn',         type: 'string' }
          { name: 'SubjectObjectId',    type: 'string' }
          { name: 'ApplicationName',    type: 'string' }
          { name: 'Result',             type: 'string' }
          { name: 'Reason',             type: 'string' }
          { name: 'GrantsAccess',       type: 'boolean' }
          { name: 'Attempts',           type: 'int' }
          { name: 'PeakRiskDuringCall', type: 'real' }
          { name: 'CallConnectionId',   type: 'string' }
          { name: 'MonitorSessionId',   type: 'string' }
          { name: 'DurationMs',         type: 'int' }
          // These five were the defect. The table carried VoiceScore and VoiceOutcome, this
          // declaration did not, and the Logs Ingestion API silently DROPS any column a
          // stream does not declare. Every voice score written since the feature shipped was
          // discarded, the "Voice compared" tile read zero, and the observe-then-calibrate
          // plan had no data to calibrate from — while the deploy, the table and the service
          // logs all looked correct.
          { name: 'VoiceScore',         type: 'real' }
          { name: 'VoiceOutcome',       type: 'string' }
          { name: 'LivenessOutcome',    type: 'string' }
          { name: 'LivenessLatencyMs',  type: 'int' }
          { name: 'SpoofScore',         type: 'real' }
          { name: 'VoiceDetail',        type: 'string' }
          { name: 'RiskScore',          type: 'real' }
          { name: 'RiskBand',           type: 'string' }
          { name: 'RiskContributors',   type: 'dynamic' }
          { name: 'EndpointKind',       type: 'string' }
        ]
      }
      'Custom-EntraGuard_Biometric_CL': {
        columns: [
          { name: 'TimeGenerated',   type: 'datetime' }
          { name: 'EventType',       type: 'string' }
          { name: 'SubjectUpn',      type: 'string' }
          { name: 'SubjectObjectId', type: 'string' }
          { name: 'SubjectTenantId', type: 'string' }
          { name: 'ConsentVersion',  type: 'string' }
          { name: 'ConsentAt',       type: 'datetime' }
          { name: 'PhraseCount',     type: 'int' }
          { name: 'SelfConsistency', type: 'real' }
          { name: 'Reason',          type: 'string' }
          { name: 'UsedMfa',         type: 'boolean' }
        ]
      }
    }
    destinations: {
      logAnalytics: [
        {
          workspaceResourceId: workspace.id
          name: 'entraGuardWorkspace'
        }
      ]
    }
    dataFlows: [
      {
        streams: ['Custom-EntraGuard_CallAnalysis_CL']
        destinations: ['entraGuardWorkspace']
        transformKql: 'source'
        outputStream: 'Custom-EntraGuard_CallAnalysis_CL'
      }
      {
        streams: ['Custom-EntraGuard_Remediation_CL']
        destinations: ['entraGuardWorkspace']
        transformKql: 'source'
        outputStream: 'Custom-EntraGuard_Remediation_CL'
      }
      {
        streams: ['Custom-EntraGuard_Verification_CL']
        destinations: ['entraGuardWorkspace']
        transformKql: 'source'
        outputStream: 'Custom-EntraGuard_Verification_CL'
      }
      {
        streams: ['Custom-EntraGuard_Biometric_CL']
        destinations: ['entraGuardWorkspace']
        transformKql: 'source'
        outputStream: 'Custom-EntraGuard_Biometric_CL'
      }
    ]
  }
  dependsOn: [callAnalysisTable, remediationTable, verificationTable, biometricTable]
}

// ── RBAC ────────────────────────────────────────────────────────────────────
var monitoringMetricsPublisher = '3913510d-42f4-4e42-8a64-420c390055eb'
var logAnalyticsReader         = '73c42c96-874c-492b-b04d-ab87d138a893'
var sentinelContributor        = 'ab8e14d6-4a74-4a29-9ba8-549422addade'

// Scoped to the DCR, not the subscription: this identity may publish these two
// streams and nothing else.
resource dcrPublisherRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dcr.id, ingestorPrincipalId, monitoringMetricsPublisher)
  scope: dcr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisher)
    principalId: ingestorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The portal runs live KQL server-side using this same identity.
resource workspaceReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, ingestorPrincipalId, logAnalyticsReader)
  scope: workspace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReader)
    principalId: ingestorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Lets the service open a Sentinel incident directly during a live call. The scheduled
// analytics rule also produces incidents, but on a five-minute cadence — too slow to
// appear while the call is still up.
resource sentinelContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, ingestorPrincipalId, sentinelContributor)
  scope: workspace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sentinelContributor)
    principalId: ingestorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorObjectId)) {
  name: guid(workspace.id, operatorObjectId, logAnalyticsReader)
  scope: workspace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReader)
    principalId: operatorObjectId
    principalType: 'User'
  }
}

// ── Sentinel analytics rule ─────────────────────────────────────────────────
// Scheduled rather than NRT: NRT rules impose constraints that custom _CL tables
// with dynamic columns do not reliably satisfy. A 5-minute cadence is well inside
// the demo loop and behaves predictably.
resource scamDetectionRule 'Microsoft.SecurityInsights/alertRules@2024-03-01' = {
  name: guid(workspace.id, 'entraguard-high-risk-call')
  scope: workspace
  kind: 'Scheduled'
  properties: {
    displayName: 'EntraGuard — high-risk voice social engineering detected'
    description: 'A live call scored at or above the high-risk threshold by the EntraGuard Analyst agent. Correlate with Entra ID sign-in and MFA activity for the same user in the surrounding window.'
    severity: 'High'
    enabled: true
    query: '''
EntraGuard_CallAnalysis_CL
| where RiskScore >= 80 and Confidence >= 0.75
| summarize
    PeakRisk        = max(RiskScore),
    PeakConfidence  = max(Confidence),
    Vectors         = make_set(Vectors),
    FurthestStage   = max(ComplianceStage),
    Rationale       = take_any(Rationale),
    FirstSeen       = min(TimeGenerated),
    LastSeen        = max(TimeGenerated)
    by SessionId, SubjectUpn, SubjectObjectId, CallerIdentity
| extend
    AccountCustomEntity = SubjectUpn,
    TimeGenerated       = LastSeen
'''
    queryFrequency: 'PT5M'
    queryPeriod: 'PT30M'
    triggerOperator: 'GreaterThan'
    triggerThreshold: 0
    suppressionDuration: 'PT1H'
    suppressionEnabled: false
    tactics: ['InitialAccess', 'CredentialAccess', 'Persistence']
    techniques: ['T1566', 'T1621', 'T1078']
    incidentConfiguration: {
      createIncident: true
      groupingConfiguration: {
        enabled: true
        reopenClosedIncident: false
        lookbackDuration: 'PT1H'
        matchingMethod: 'Selected'
        groupByEntities: ['Account']
      }
    }
    entityMappings: [
      {
        entityType: 'Account'
        fieldMappings: [
          { identifier: 'FullName', columnName: 'SubjectUpn' }
          { identifier: 'AadUserId', columnName: 'SubjectObjectId' }
        ]
      }
    ]
    eventGroupingSettings: { aggregationKind: 'SingleAlert' }
  }
  dependsOn: [sentinel, callAnalysisTable]
}

// ── Application Insights ────────────────────────────────────────────────────
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
  }
}

output workspaceResourceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
#disable-next-line outputs-should-not-contain-secrets // Consumed only by the Container Apps environment for its log sink.
output workspaceSharedKey string = workspace.listKeys().primarySharedKey
output workspaceName string = workspace.name
output dceLogsIngestionEndpoint string = dce.properties.logsIngestion.endpoint
output dcrImmutableId string = dcr.properties.immutableId
output dcrResourceId string = dcr.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
