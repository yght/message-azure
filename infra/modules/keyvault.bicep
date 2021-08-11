param namePrefix string
param location string
param tags object = {}

@description('Object id of the function app managed identity')
param readerPrincipalId string

resource vault 'Microsoft.KeyVault/vaults@2021-11-01-preview' = {
  name: '${namePrefix}-kv'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    /*
      RBAC rather than access policies. Access policies cannot express "read
      one secret", only "read all secrets", and they are invisible to the
      normal Azure permissions tooling.
    */
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // On, deliberately. Recovering a vault somebody deleted during an
    // incident is worth the inconvenience of not being able to reuse a name.
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource secretsRead 'Microsoft.Authorization/roleAssignments@2020-10-01-preview' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
