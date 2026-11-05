targetScope = 'resourceGroup'

@description('Name of the dedicated sample Log Analytics workspace.')
param workspaceName string

@description('Name of the dedicated sample Application Insights component.')
param applicationInsightsName string

@allowed([
  'westus2'
])
param location string = 'westus2'

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output applicationInsightsResourceId string = applicationInsights.id
output workspaceResourceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
