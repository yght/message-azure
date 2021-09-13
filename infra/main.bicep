targetScope = 'resourceGroup'

@description('Short environment name, used as a resource name prefix')
@allowed([
  'dev'
  'prod'
])
param environment string

param location string = resourceGroup().location

@description('Serverless Cosmos and a consumption plan in dev')
param useServerless bool = (environment == 'dev')

var namePrefix = 'msgplat-${environment}'

/*
  Names are derived here rather than read back from module outputs. Role
  assignment names and scopes have to be resolvable before the deployment
  starts, and an output is not - so anything a role assignment references
  must be computed from parameters.
*/
var serviceBusNamespaceName = '${namePrefix}-sb'
var cosmosAccountName = '${namePrefix}-cosmos'
var functionAppName = '${namePrefix}-fn'

var tags = {
  environment: environment
  application: 'message-platform'
  managedBy: 'bicep'
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    retentionInDays: environment == 'prod' ? 90 : 30
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    sku: environment == 'prod' ? 'Premium' : 'Standard'
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    useServerless: useServerless
    throughput: environment == 'prod' ? 1000 : 400
  }
}

module functionApp 'modules/functionapp.bicep' = {
  name: 'functionapp'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    appInsightsConnectionString: monitoring.outputs.connectionString
    serviceBusNamespaceName: serviceBusNamespaceName
    cosmosAccountName: cosmosAccountName
    keyVaultName: '${namePrefix}-kv'
    planSku: environment == 'prod' ? 'EP1' : 'Y1'
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    readerPrincipalId: functionApp.outputs.principalId
  }
}

/*
  Data-plane grants for the function's managed identity.

  These are not optional extras. The Service Bus namespace and the Cosmos
  account both have key-based auth disabled, so without these role
  assignments the function cannot read a single message. That is the
  intended shape: there is no fallback credential to leak.
*/

var serviceBusDataReceiver = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
var serviceBusDataSender = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2021-11-01' existing = {
  name: serviceBusNamespaceName
}

resource receiveMessages 'Microsoft.Authorization/roleAssignments@2020-10-01-preview' = {
  scope: sbNamespace
  name: guid(sbNamespace.id, functionAppName, serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiver)
    principalId: functionApp.outputs.principalId
    principalType: 'ServicePrincipal'
  }
  dependsOn: [
    serviceBus
  ]
}

// Sender as well as receiver: deferred notifications are re-enqueued onto the
// deferred queue with a scheduled delivery time.
resource sendMessages 'Microsoft.Authorization/roleAssignments@2020-10-01-preview' = {
  scope: sbNamespace
  name: guid(sbNamespace.id, functionAppName, serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSender)
    principalId: functionApp.outputs.principalId
    principalType: 'ServicePrincipal'
  }
  dependsOn: [
    serviceBus
  ]
}

/*
  Cosmos data-plane access is not an ARM role assignment. It is a separate
  resource type with its own role definitions, which is a genuine trap - the
  ARM "Cosmos DB Account Reader" role grants control-plane access and reads
  no documents whatsoever.
*/
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2021-10-15' existing = {
  name: cosmosAccountName
}

resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2021-10-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccountName, functionAppName, 'data-contributor')
  properties: {
    // Built-in Data Contributor.
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: functionApp.outputs.principalId
    scope: cosmosAccount.id
  }
  dependsOn: [
    cosmos
  ]
}

output deployedFunctionApp string = functionApp.outputs.functionAppName
output deployedServiceBusNamespace string = serviceBus.outputs.namespaceName
output cosmosEndpoint string = cosmos.outputs.endpoint
output keyVaultUri string = keyVault.outputs.vaultUri
