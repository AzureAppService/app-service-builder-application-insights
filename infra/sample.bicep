targetScope = 'resourceGroup'

param appName string
param workspaceName string
param applicationInsightsName string

@description('Future absolute UTC demo deadline, for example yyyy-MM-ddTHH:mm:ssZ.')
param expiresAt string

@allowed([
  'westus2'
])
param location string = 'westus2'

module monitoring './main.bicep' = {
  name: 'sample-monitoring'
  params: {
    workspaceName: workspaceName
    applicationInsightsName: applicationInsightsName
    location: location
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource app 'Microsoft.Web/builderApps@2026-09-01-preview' = {
  name: appName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    versionConfiguration: {
      variables: [
        { name: 'TELEMETRY_MODE', value: 'managed-identity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsights.properties.ConnectionString }
        { name: 'SAMPLE_EXPIRES_AT', value: expiresAt }
      ]
    }
    scaling: {
      components: [
        { name: 'web', minReplicas: 0, maxReplicas: 1, httpEnabled: true }
      ]
    }
  }
  dependsOn: [monitoring]
}

resource visitorAccess 'Microsoft.Web/builderApps/authConfigs@2026-09-01-preview' = {
  parent: app
  name: 'current'
  properties: { mode: 'disabled' }
}

resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, app.id, 'Monitoring Metrics Publisher')
  scope: applicationInsights
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

output appResourceId string = app.id
output appPrincipalId string = app.identity.principalId
output applicationInsightsResourceId string = monitoring.outputs.applicationInsightsResourceId
output workspaceResourceId string = monitoring.outputs.workspaceResourceId
output workspaceCustomerId string = monitoring.outputs.workspaceCustomerId
