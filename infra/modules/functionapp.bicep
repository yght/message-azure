param namePrefix string
param location string
param tags object = {}

param appInsightsConnectionString string
param serviceBusNamespaceName string
param cosmosAccountName string
param keyVaultName string

@description('Consumption in dev; Elastic Premium in production for the warm instance')
@allowed([
  'Y1'
  'EP1'
])
param planSku string = 'Y1'

resource storage 'Microsoft.Storage/storageAccounts@2021-08-01' = {
  name: replace('${namePrefix}fnsa', '-', '')
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

resource plan 'Microsoft.Web/serverfarms@2021-02-01' = {
  name: '${namePrefix}-plan'
  location: location
  tags: tags
  sku: {
    name: planSku
    tier: planSku == 'Y1' ? 'Dynamic' : 'ElasticPremium'
  }
  properties: {
    reserved: true
  }
  kind: 'functionapp,linux'
}

resource functionApp 'Microsoft.Web/sites@2021-02-01' = {
  name: '${namePrefix}-fn'
  location: location
  tags: tags
  kind: 'functionapp,linux'

  /*
    System-assigned identity. Every downstream grant hangs off this, which is
    why there is not a single connection string or key in the app settings
    below.
  */
  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET|6.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      alwaysOn: planSku != 'Y1'
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        /*
          The __fullyQualifiedNamespace and __accountEndpoint forms tell the
          bindings to authenticate with the managed identity instead of
          looking for a connection string. This is the whole point of the
          identity block above.
        */
        {
          name: 'ServiceBusConnection__fullyQualifiedNamespace'
          value: '${serviceBusNamespaceName}.servicebus.windows.net'
        }
        {
          name: 'CosmosConnection__accountEndpoint'
          value: 'https://${cosmosAccountName}.documents.azure.com:443/'
        }
        {
          name: 'KeyVaultName'
          value: keyVaultName
        }
      ]
    }
  }
}

output principalId string = functionApp.identity.principalId
output functionAppName string = functionApp.name
output storageAccountName string = storage.name
