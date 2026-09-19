// Key Vault holding the RS256 certificate EntraGuard signs External Authentication Method
// responses with.
//
// A separate module rather than a few resources bolted onto identity.bicep, because this is
// the only key material in the deployment and the blast radius of getting its access wrong
// is the whole point: the private key here signs assertions that Microsoft Entra ID accepts
// as proof that a person completed multifactor authentication.

@description('Base name, e.g. entraguard.')
param appName string

@description('Environment name, e.g. demo.')
param environmentName string

@description('Deployment region.')
param location string

@description('Principal ID of the user-assigned managed identity that signs.')
param signerPrincipalId string

@description('Suffix keeping the globally unique vault name distinct per deployment.')
param uniqueSuffix string

// Key Vault names are globally unique, limited to 24 characters, and alphanumeric with
// hyphens. The suffix is what keeps two deployments of this template from colliding.
var vaultName = take('kv-${appName}-${uniqueSuffix}', 24)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: {
    application: appName
    environment: environmentName
  }
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // RBAC rather than access policies. Access policies are per-object-id lists that drift
    // and cannot be reasoned about from a role assignment, and Microsoft's own guidance is
    // to prefer RBAC for new vaults.
    enableRbacAuthorization: true

    // Recovery, not convenience. A deleted signing certificate is not merely an outage of
    // this feature: every tenant that added EntraGuard as an authentication method has a
    // Conditional Access policy pointing at keys that no longer exist, and every sign-in
    // behind that policy fails until it is restored or the method is removed.
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true

    publicNetworkAccess: 'Enabled'
  }
}

// Crypto User, not Crypto Officer, and deliberately not Administrator.
//
// The service needs exactly two verbs: read the public certificate to publish it in JWKS,
// and ask the vault to sign a digest. It never needs to create, rotate, export or delete a
// key, and rotation is an operator action rather than something the request path can reach.
// This is also what keeps the README's "managed identity throughout" claim true — the
// private key is never exportable and never enters the container.
resource cryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, signerPrincipalId, 'KeyVaultCryptoUser')
  scope: vault
  properties: {
    // Key Vault Crypto User
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '12338af0-0e69-4776-bea7-57ae8d297424'
    )
    principalId: signerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Reading the certificate is a separate plane from using the key, so it is a separate role.
//
// Reader, NOT "Key Vault Certificate User". Certificate User exists for applications that
// need the full certificate including its private key, and granting it here would make the
// key exportable by this service — contradicting the paragraph above and the README's
// "managed identity throughout" claim. What JWKS actually needs is the public certificate
// and its key id, which is metadata, which is Reader.
resource certificateReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, signerPrincipalId, 'KeyVaultReader')
  scope: vault
  properties: {
    // Key Vault Reader
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '21090545-7ca7-4776-b22c-e363652d74d2'
    )
    principalId: signerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output vaultUri string = vault.properties.vaultUri
output vaultName string = vault.name
