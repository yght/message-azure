@description('Prefix for every resource name')
param namePrefix string

@description('Location for all resources')
param location string

@description('Standard supports topics; Basic does not, and we need fan-out')
@allowed([
  'Standard'
  'Premium'
])
param sku string = 'Standard'

param tags object = {}

resource namespace 'Microsoft.ServiceBus/namespaces@2021-11-01' = {
  name: '${namePrefix}-sb'
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
  }
  properties: {
    zoneRedundant: sku == 'Premium'
  }
}

/*
  One topic per event kind rather than one topic with a type property.
  Subscriptions filter server-side, so a consumer that only cares about
  message.sent never has to receive and discard anything else.
*/
resource messageSent 'Microsoft.ServiceBus/namespaces/topics@2021-11-01' = {
  parent: namespace
  name: 'message-sent'
  properties: {
    /*
      Duplicate detection is on because the API publishes after the write
      commits, and a retry of that publish would otherwise notify twice.
      Ten minutes is comfortably longer than the longest retry chain.
    */
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P1D'
    enablePartitioning: false
    supportOrdering: true
  }
}

resource notificationsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2021-11-01' = {
  parent: messageSent
  name: 'notifications'
  properties: {
    /*
      Five attempts then dead letter. The notification function is idempotent
      per (messageId, recipientId), so a redelivery is safe; what is not safe
      is retrying forever against a push provider that is rejecting us.
    */
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P1D'
  }
}

/*
  Deferred notifications - quiet hours and digests - go here with a scheduled
  enqueue time rather than being held in the function's memory. A function
  that holds state across an invocation is a function that loses it.
*/
resource deferred 'Microsoft.ServiceBus/namespaces/queues@2021-11-01' = {
  parent: namespace
  name: 'notifications-deferred'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT1M'
    // Long enough that a notification deferred over a full night still
    // exists in the morning.
    defaultMessageTimeToLive: 'P2D'
    deadLetteringOnMessageExpiration: true
  }
}

output namespaceName string = namespace.name
output namespaceId string = namespace.id
output topicName string = messageSent.name
output subscriptionName string = notificationsSubscription.name
output deferredQueueName string = deferred.name
