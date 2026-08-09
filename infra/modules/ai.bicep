// ─────────────────────────────────────────────────────────────────────────────
// The intelligence layer: Azure AI Speech (transcription) + Azure OpenAI (analysis).
//
// Both accounts set a custom subdomain and disable local auth. Custom subdomains
// are a hard requirement for Entra ID token auth on Cognitive Services — without
// one, only API keys work, and we do not want keys anywhere in this system.
// ─────────────────────────────────────────────────────────────────────────────

param appName string
param location string
param uniqueSuffix string
param tags object

@description('Principal ID of the managed identity that calls Speech and OpenAI.')
param principalId string

@description('Principal ID of the ACS resource\'s own managed identity, which needs to reach AI Services for text-to-speech.')
param acsPrincipalId string = ''

param openAiModelName string
param openAiModelVersion string
param openAiSkuName string
param openAiCapacity int

param logAnalyticsWorkspaceId string

var speechName = 'spch-${appName}-${uniqueSuffix}'
var openAiName = 'aoai-${appName}-${uniqueSuffix}'
var deploymentName = 'entraguard-analyst'
var aiServicesName = 'ai-${appName}-${uniqueSuffix}'

// ── Azure AI Speech ─────────────────────────────────────────────────────────
resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechName
  location: location
  tags: tags
  kind: 'SpeechServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: speechName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

// ── Azure AI Services (multi-service) ───────────────────────────────────────
// Separate from the Speech account above, and NOT optional.
//
// ACS Call Automation's TextSource — the spoken challenge and the closing message — is
// synthesised by ACS itself, not by this service calling Speech. For that to work the ACS
// resource must be linked to an Azure AI services MULTI-SERVICE resource (kind
// 'CognitiveServices'). A Speech-only account (kind 'SpeechServices') is not accepted for
// that link, which is why the first version failed with "the challenge could not be played":
// the call connected, then StartRecognizing could not render its prompt.
resource aiServices 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: aiServicesName
  location: location
  tags: tags
  kind: 'CognitiveServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: aiServicesName
    publicNetworkAccess: 'Enabled'
    // ACS authenticates to this resource with its own managed identity, so keys are not
    // needed — but ACS's link requires the account to accept AAD auth, which it does by
    // default. Local auth stays enabled here because disabling it has been observed to
    // break the ACS connection.
    disableLocalAuth: false
  }
}

// ACS reaches AI Services as itself. Cognitive Services User is the documented role for
// the Call Automation TTS/recognition path.
var cognitiveServicesUser = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource acsToAiServices 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acsPrincipalId)) {
  name: guid(aiServices.id, acsPrincipalId, cognitiveServicesUser)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUser)
    principalId: acsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Azure OpenAI ────────────────────────────────────────────────────────────
resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: openAiName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource analystDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: deploymentName
  sku: {
    name: openAiSkuName
    capacity: openAiCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: openAiModelName
      version: openAiModelVersion
    }
    // Fail the request rather than silently truncate when the window is oversized —
    // a silently truncated transcript would produce a confidently wrong verdict.
    raiPolicyName: 'Microsoft.DefaultV2'
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

// ── RBAC ────────────────────────────────────────────────────────────────────
var cognitiveServicesSpeechUser = 'f2dc8367-1007-4938-bd23-fe263f013447'
var cognitiveServicesOpenAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource speechRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, principalId, cognitiveServicesSpeechUser)
  scope: speech
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesSpeechUser)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, principalId, cognitiveServicesOpenAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Diagnostics ─────────────────────────────────────────────────────────────
resource speechDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-to-law'
  scope: speech
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource openAiDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-to-law'
  scope: openAi
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output aiServicesEndpoint string = aiServices.properties.endpoint
output aiServicesName string = aiServices.name
output speechName string = speech.name
output speechEndpoint string = speech.properties.endpoint
output speechResourceId string = speech.id
output openAiName string = openAi.name
output openAiEndpoint string = openAi.properties.endpoint
output openAiResourceId string = openAi.id
output openAiDeploymentName string = analystDeployment.name
