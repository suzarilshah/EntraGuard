// ─────────────────────────────────────────────────────────────────────────────
// Session state (Table) and transcript archive (Blob).
//
// Table holds live + historical call sessions so the portal's /sessions view
// survives a container restart. Blob holds full transcripts, which are too large
// and too PII-sensitive to push into Log Analytics wholesale — Sentinel gets the
// structured verdict and evidence spans, not the raw conversation.
// ─────────────────────────────────────────────────────────────────────────────

@minLength(3)
@maxLength(12)
param appName string
param location string
@minLength(6)
@maxLength(6)
param uniqueSuffix string
param tags object

@description('Principal ID of the managed identity that reads and writes session state.')
param principalId string

var accountName = 'st${appName}${uniqueSuffix}'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: accountName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Data-plane access is identity-only. Shared keys are disabled so a leaked
    // connection string cannot exist in the first place.
    allowSharedKeyAccess: false
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource sessionsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'Sessions'
}

// Maps Entra ID object IDs to ACS communication user IDs. Needed by the
// token-broker fallback path when ACS direct Entra auth is unavailable.
resource identityMapTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'IdentityMap'
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource transcriptsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'transcripts'
  properties: { publicAccess: 'None' }
}

// ── RBAC ────────────────────────────────────────────────────────────────────
var storageTableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageBlobDataContributor  = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource tableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageTableDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributor)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output accountName string = storageAccount.name
output tableEndpoint string = storageAccount.properties.primaryEndpoints.table
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
