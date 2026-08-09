// ─────────────────────────────────────────────────────────────────────────────
// Azure Container Apps — media service + portal.
//
// WHY NOT AZURE FUNCTIONS. Two independent reasons:
//   1. ACS media streaming requires our side to be a WebSocket *server*. Functions
//      cannot accept inbound WebSocket upgrades.
//   2. An incoming call rings for 30 seconds. Microsoft's own Call Automation
//      guidance warns that consumption-plan compute can spend that entire window
//      cold-starting, and the call goes unanswered. They explicitly recommend
//      Container Apps with minReplicas > 0.
//
// Hence minReplicas: 1 on the media service. That is a correctness requirement,
// not a performance tweak, and it must not be "optimised" to zero.
// ─────────────────────────────────────────────────────────────────────────────

@minLength(3)
@maxLength(12)
param appName string
param environmentName string
param location string
@minLength(6)
@maxLength(6)
param uniqueSuffix string
param tags object

param managedIdentityId string
param managedIdentityClientId string

param logAnalyticsCustomerId string
@secure()
param logAnalyticsSharedKey string

param acsEndpoint string
param speechEndpoint string
param speechRegion string
param aiServicesEndpoint string
param openAiEndpoint string
param openAiDeploymentName string
param dceEndpoint string
param dcrImmutableId string
param workspaceResourceId string
param storageAccountName string
param applicationInsightsConnectionString string

@description('Media service image. Placeholder on first deploy; scripts/deploy-apps.sh replaces it with the real build.')
param mediaServiceImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Portal image. Placeholder on first deploy.')
param portalImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

var registryName = 'cr${appName}${uniqueSuffix}'
var environmentNameFull = 'cae-${appName}-${environmentName}'
var mediaServiceName = 'ca-${appName}-media'
var portalName = 'ca-${appName}-portal'

// ── Container registry ──────────────────────────────────────────────────────
resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    // Identity-based pulls only; no admin credentials to leak.
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, managedIdentityId, acrPullRole)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
    principalId: reference(managedIdentityId, '2023-01-31').principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Container Apps environment ──────────────────────────────────────────────
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentNameFull
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    zoneRedundant: false
  }
}

// ── Media service ───────────────────────────────────────────────────────────
resource mediaService 'Microsoft.App/containerApps@2024-03-01' = {
  name: mediaServiceName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${managedIdentityId}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        // 'auto' negotiates HTTP/1.1 for the WebSocket upgrade ACS needs.
        transport: 'auto'
        allowInsecure: false
        // A media WebSocket must land on the replica holding that call's session
        // state. Without affinity a reconnect can hit a replica that has never
        // seen the call.
        stickySessions: { affinity: 'sticky' }
        corsPolicy: {
          allowedOrigins: ['*']
          allowedMethods: ['GET', 'POST', 'OPTIONS']
          allowedHeaders: ['*']
          allowCredentials: false
        }
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: managedIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mediaservice'
          image: mediaServiceImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID',              value: managedIdentityClientId }
            { name: 'ACS_ENDPOINT',                 value: acsEndpoint }
            { name: 'SPEECH_ENDPOINT',              value: speechEndpoint }
            { name: 'SPEECH_REGION',                value: speechRegion }
            { name: 'AI_SERVICES_ENDPOINT',      value: aiServicesEndpoint }
            { name: 'AOAI_ENDPOINT',                value: openAiEndpoint }
            { name: 'AOAI_DEPLOYMENT',              value: openAiDeploymentName }
            { name: 'DCE_ENDPOINT',                 value: dceEndpoint }
            { name: 'DCR_IMMUTABLE_ID',             value: dcrImmutableId }
            { name: 'LAW_RESOURCE_ID',              value: workspaceResourceId }
            { name: 'STORAGE_ACCOUNT_NAME',         value: storageAccountName }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
            { name: 'ASPNETCORE_URLS',              value: 'http://+:8080' }
            { name: 'ASPNETCORE_ENVIRONMENT',       value: 'Production' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // See the header comment. Do not set this to 0.
        minReplicas: 1
        maxReplicas: 3
        rules: [
          {
            name: 'concurrent-calls'
            http: { metadata: { concurrentRequests: '20' } }
          }
        ]
      }
    }
  }
}

// ── Portal ──────────────────────────────────────────────────────────────────
resource portal 'Microsoft.App/containerApps@2024-03-01' = {
  name: portalName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${managedIdentityId}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 3000
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: managedIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'portal'
          image: portalImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID',      value: managedIdentityClientId }
            { name: 'MEDIA_SERVICE_URL',    value: 'https://${mediaService.properties.configuration.ingress.fqdn}' }
            { name: 'LAW_RESOURCE_ID',      value: workspaceResourceId }
            { name: 'LAW_WORKSPACE_ID',     value: logAnalyticsCustomerId }
            { name: 'ACS_ENDPOINT',         value: acsEndpoint }
            { name: 'STORAGE_ACCOUNT_NAME', value: storageAccountName }
            { name: 'NODE_ENV',             value: 'production' }
            { name: 'PORT',                 value: '3000' }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

output environmentName string = environment.name
output mediaServiceName string = mediaService.name
output mediaServiceFqdn string = mediaService.properties.configuration.ingress.fqdn
output portalName string = portal.name
output portalFqdn string = portal.properties.configuration.ingress.fqdn
output containerRegistryName string = registry.name
output containerRegistryLoginServer string = registry.properties.loginServer
