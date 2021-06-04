param namePrefix string
param location string
param tags object = {}

@description('Serverless in dev, provisioned throughput in production')
param useServerless bool = false

@description('Ignored when useServerless is true')
param throughput int = 400

resource account 'Microsoft.DocumentDB/databaseAccounts@2021-10-15' = {
  name: '${namePrefix}-cosmos'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    /*
      Session consistency, not Strong. A user must read their own writes -
      you send a message and it must be there when the list refreshes - but
      one user does not need to see another's write instantly, and Strong
      costs roughly double the RUs and pins us to a single region.
    */
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: useServerless ? [ { name: 'EnableServerless' } ] : []
    // Keys are disabled outright; access is AAD only, via managed identity.
    disableLocalAuth: true
    backupPolicy: {
      type: 'Continuous'
    }
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2021-10-15' = {
  parent: account
  name: 'messaging'
  properties: {
    resource: {
      id: 'messaging'
    }
  }
}

/*
  Partitioned on conversationId, not on messageId or userId.

  Every read this application makes is "the messages in one conversation,
  newest first". That is a single-partition query on this key and a fan-out
  on any other. The cost is that one enormous conversation becomes a hot
  partition, which we have not hit and would solve by suffixing the key.
*/
resource messages 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-10-15' = {
  parent: database
  name: 'messages'
  properties: {
    resource: {
      id: 'messages'
      partitionKey: {
        paths: [ '/conversationId' ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        /*
          Index only what we query. The default indexes every path, and on a
          write-heavy container that is most of the RU cost for no benefit -
          nobody filters on message body.
        */
        includedPaths: [
          { path: '/conversationId/?' }
          { path: '/sentAt/?' }
          { path: '/senderId/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
      defaultTtl: -1
    }
    options: useServerless ? {} : { throughput: throughput }
  }
}

/*
  Notification state, keyed by recipient. Separate container because its
  access pattern is nothing like the message container's and mixing them
  would make both indexing policies wrong.
*/
resource notificationState 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-10-15' = {
  parent: database
  name: 'notification-state'
  properties: {
    resource: {
      id: 'notification-state'
      partitionKey: {
        paths: [ '/userId' ]
        kind: 'Hash'
      }
      // Digest counters are worthless a day later; let Cosmos reap them
      // rather than running a cleanup job.
      defaultTtl: 172800
    }
    options: useServerless ? {} : { throughput: 400 }
  }
}

output accountName string = account.name
output accountId string = account.id
output databaseName string = database.name
output endpoint string = account.properties.documentEndpoint
