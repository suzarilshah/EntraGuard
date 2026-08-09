// ─────────────────────────────────────────────────────────────────────────────
// Azure Communication Services — the telephony layer.
//
// No PSTN phone number is purchased. IncomingCall fires for ACS-identity to
// ACS-identity calls just as it does for PSTN, so interception is fully
// demonstrable over VoIP with no number provisioning or regulatory lead time.
//
// The Event Grid system topic lives here; the IncomingCall SUBSCRIPTION is
// created in scripts/04-eventgrid-subscribe.sh because it needs the Container
// App FQDN, which does not exist at infrastructure-deploy time.
// ─────────────────────────────────────────────────────────────────────────────

@minLength(3)
@maxLength(12)
param appName string
param environmentName string
@description('Deterministic per-subscription suffix. ACS names are globally unique across all of Azure, so this cannot be omitted.')
@minLength(6)
@maxLength(6)
param uniqueSuffix string
param tags object

@description('Workspace that receives ACS diagnostic logs.')
param logAnalyticsWorkspaceId string

@description('Principal ID of the managed identity that answers calls via Call Automation.')
param principalId string

resource acs 'Microsoft.Communication/communicationServices@2023-04-01' = {
  // Globally unique across every Azure tenant, not just this subscription — deploying the
  // same template to a second subscription without the suffix fails with
  // NameReservationTaken, which reads as a quota problem rather than a naming collision.
  name: 'acs-${appName}-${uniqueSuffix}'
  // ACS is a global resource; only dataLocation is regional.
  location: 'global'
  tags: tags
  // ACS authenticates to Azure AI services as itself when synthesising the verification
  // prompt. Without this identity there is nothing to grant Cognitive Services User to,
  // and TextSource playback fails at call time rather than at deploy time.
  identity: { type: 'SystemAssigned' }
  properties: {
    dataLocation: 'United States'
  }
}

// Event Grid system topic for ACS events. Declared here so the topic exists
// before compute; the subscription that points at the media service is added later.
resource systemTopic 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: 'egst-${appName}-${environmentName}'
  location: 'global'
  tags: tags
  properties: {
    source: acs.id
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

// Call Automation and call-quality telemetry into the same workspace Sentinel
// reads, so a Sentinel incident can be joined to the underlying call diagnostics.
resource acsDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-to-law'
  scope: acs
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { categoryGroup: 'allLogs', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

// Delivery failures on the IncomingCall webhook are otherwise invisible, and a
// silently undelivered event looks identical to "the demo just didn't work".
resource eventGridDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-to-law'
  scope: systemTopic
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { categoryGroup: 'allLogs', enabled: true }
    ]
  }
}

// ── RBAC ────────────────────────────────────────────────────────────────────
// ACS has no fine-grained data-plane role; Contributor on the resource is the
// documented way to let a managed identity drive Call Automation. Scoping it to
// the single ACS resource keeps the blast radius to this one service.
var contributorRole = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource acsContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acs.id, principalId, contributorRole)
  scope: acs
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRole)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output principalId string = acs.identity.principalId
output name string = acs.name
output resourceId string = acs.id
output endpoint string = 'https://${acs.properties.hostName}'
output hostName string = acs.properties.hostName
output systemTopicName string = systemTopic.name
