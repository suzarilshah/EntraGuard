// ─────────────────────────────────────────────────────────────────────────────
// EntraGuard — root deployment (subscription scope)
//
// Creates the resource group and wires every module. Two things are deliberately
// NOT here:
//   · The Event Grid IncomingCall subscription — it needs the Container App FQDN,
//     which does not exist until compute is deployed. See scripts/04-eventgrid-subscribe.sh.
//   · Entra ID app registrations — Bicep cannot write to Microsoft Graph.
//     See scripts/02-entra-apps.sh.
// ─────────────────────────────────────────────────────────────────────────────
targetScope = 'subscription'

@description('Short name used to derive every resource name. Lowercase alphanumeric.')
@minLength(3)
@maxLength(12)
param appName string = 'entraguard'

@description('Deployment environment discriminator.')
@allowed(['demo', 'dev', 'prod'])
param environmentName string = 'demo'

@description('Azure region. Must offer ACS Call Automation, AI Speech, and the chosen Azure OpenAI model.')
param location string = 'eastus'

@description('Azure OpenAI model to deploy. Resolved by scripts/00-preflight.sh — do not hardcode a guess.')
param openAiModelName string = 'gpt-5-mini'

@description('Model version, as reported by the preflight availability probe.')
param openAiModelVersion string

@description('Deployment SKU for the model (GlobalStandard where offered).')
param openAiSkuName string = 'GlobalStandard'

@description('Throughput in thousands of tokens per minute.')
param openAiCapacity int = 30

@description('Object ID of the human operator who should be able to read telemetry directly.')
param operatorObjectId string = ''

param tags object = {
  application: 'EntraGuard'
  environment: environmentName
  purpose: 'microsoft-garage-hackathon'
}

var resourceGroupName = 'rg-${appName}-${environmentName}'
// Deterministic 6-char suffix keeps globally-unique names (storage, ACA, AOAI) stable
// across redeploys — critical because redeploying must not orphan the Event Grid
// subscription that points at the old FQDN.
var uniqueSuffix = substring(uniqueString(subscription().subscriptionId, resourceGroupName), 0, 6)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  name: 'deploy-identity'
  scope: rg
  params: {
    appName: appName
    environmentName: environmentName
    location: location
    tags: tags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'deploy-observability'
  scope: rg
  params: {
    appName: appName
    environmentName: environmentName
    location: location
    uniqueSuffix: uniqueSuffix
    tags: tags
    ingestorPrincipalId: identity.outputs.principalId
    operatorObjectId: operatorObjectId
  }
}

module storage 'modules/storage.bicep' = {
  name: 'deploy-storage'
  scope: rg
  params: {
    appName: appName
    location: location
    uniqueSuffix: uniqueSuffix
    tags: tags
    principalId: identity.outputs.principalId
  }
}

module communication 'modules/communication.bicep' = {
  name: 'deploy-communication'
  scope: rg
  params: {
    appName: appName
    environmentName: environmentName
    uniqueSuffix: uniqueSuffix
    tags: tags
    logAnalyticsWorkspaceId: observability.outputs.workspaceResourceId
    principalId: identity.outputs.principalId
  }
}

module ai 'modules/ai.bicep' = {
  name: 'deploy-ai'
  scope: rg
  params: {
    appName: appName
    location: location
    uniqueSuffix: uniqueSuffix
    tags: tags
    principalId: identity.outputs.principalId
    acsPrincipalId: communication.outputs.principalId
    openAiModelName: openAiModelName
    openAiModelVersion: openAiModelVersion
    openAiSkuName: openAiSkuName
    openAiCapacity: openAiCapacity
    logAnalyticsWorkspaceId: observability.outputs.workspaceResourceId
  }
}

module compute 'modules/compute.bicep' = {
  name: 'deploy-compute'
  scope: rg
  params: {
    appName: appName
    environmentName: environmentName
    location: location
    uniqueSuffix: uniqueSuffix
    tags: tags
    managedIdentityId: identity.outputs.resourceId
    managedIdentityClientId: identity.outputs.clientId
    logAnalyticsCustomerId: observability.outputs.workspaceCustomerId
    logAnalyticsSharedKey: observability.outputs.workspaceSharedKey
    acsEndpoint: communication.outputs.endpoint
    speechEndpoint: ai.outputs.speechEndpoint
    speechRegion: location
    aiServicesEndpoint: ai.outputs.aiServicesEndpoint
    openAiEndpoint: ai.outputs.openAiEndpoint
    openAiDeploymentName: ai.outputs.openAiDeploymentName
    dceEndpoint: observability.outputs.dceLogsIngestionEndpoint
    dcrImmutableId: observability.outputs.dcrImmutableId
    workspaceResourceId: observability.outputs.workspaceResourceId
    storageAccountName: storage.outputs.accountName
    applicationInsightsConnectionString: observability.outputs.appInsightsConnectionString
  }
}

// ── Outputs consumed by the post-deploy scripts ─────────────────────────────
output resourceGroupName string = rg.name
output location string = location

output managedIdentityClientId string = identity.outputs.clientId
output managedIdentityPrincipalId string = identity.outputs.principalId
output managedIdentityName string = identity.outputs.name

output acsName string = communication.outputs.name
output acsEndpoint string = communication.outputs.endpoint

output speechEndpoint string = ai.outputs.speechEndpoint
output aiServicesEndpoint string = ai.outputs.aiServicesEndpoint
output openAiEndpoint string = ai.outputs.openAiEndpoint
output openAiDeploymentName string = ai.outputs.openAiDeploymentName

output workspaceResourceId string = observability.outputs.workspaceResourceId
output workspaceCustomerId string = observability.outputs.workspaceCustomerId
output dceLogsIngestionEndpoint string = observability.outputs.dceLogsIngestionEndpoint
output dcrImmutableId string = observability.outputs.dcrImmutableId

output storageAccountName string = storage.outputs.accountName

output mediaServiceName string = compute.outputs.mediaServiceName
output mediaServiceFqdn string = compute.outputs.mediaServiceFqdn
output portalName string = compute.outputs.portalName
output portalFqdn string = compute.outputs.portalFqdn
output containerRegistryName string = compute.outputs.containerRegistryName
output containerRegistryLoginServer string = compute.outputs.containerRegistryLoginServer
output containerAppEnvironmentName string = compute.outputs.environmentName

// Added so a clean infra run yields the same environment the hand-made apps did. Both were
// previously created by `az containerapp create` and existed nowhere in source — a fresh
// deployment silently produced a system with no voice scoring and no relying-party app.
output voiceprintName string = compute.outputs.voiceprintName
output voiceprintUrl string = compute.outputs.voiceprintUrl
output treasuryName string = compute.outputs.treasuryName
output treasuryFqdn string = compute.outputs.treasuryFqdn
