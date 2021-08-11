param namePrefix string
param location string
param tags object = {}
param retentionInDays int = 90

resource workspace 'Microsoft.OperationalInsights/workspaces@2021-06-01' = {
  name: '${namePrefix}-law'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
  }
}

/*
  Workspace-based Application Insights. The classic standalone kind was
  already on notice by the time this was written and retires in 2024.
*/
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    // Notification volume is high and mostly uninteresting. Sampling keeps
    // the bill sane; the metrics below are pre-aggregated so they stay exact.
    SamplingPercentage: 20
  }
}

/*
  The alert that matters: notifications being decided but never sent.

  A failure to deliver is visible. A decision to deliver that quietly never
  reaches the push provider is not, and it looks identical to a quiet period.
*/
resource deadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-deferred-backlog'
  location: 'global'
  tags: tags
  properties: {
    severity: 2
    enabled: true
    scopes: [ resourceId('Microsoft.ServiceBus/namespaces', '${namePrefix}-sb') ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'DeadLetteredMessages'
          metricName: 'DeadletteredMessages'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    autoMitigate: true
    actions: []
  }
}

output workspaceId string = workspace.id
output instrumentationKey string = insights.properties.InstrumentationKey
output connectionString string = insights.properties.ConnectionString
output appInsightsId string = insights.id
