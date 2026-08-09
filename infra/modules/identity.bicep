// ─────────────────────────────────────────────────────────────────────────────
// One user-assigned managed identity shared by the media service and the portal.
//
// Every Azure dependency (Speech, OpenAI, Storage, Log Analytics, the DCR) is
// reached with this identity. No connection strings, no API keys in app config.
// The Microsoft Graph app-only roles are granted separately in
// scripts/02-entra-apps.sh, because Bicep cannot write Graph app role assignments.
// ─────────────────────────────────────────────────────────────────────────────

param appName string
param environmentName string
param location string
param tags object

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appName}-${environmentName}'
  location: location
  tags: tags
}

output resourceId string = uami.id
output principalId string = uami.properties.principalId
output clientId string = uami.properties.clientId
output name string = uami.name
