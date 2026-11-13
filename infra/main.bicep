@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Short name used in resource names')
param namePrefix string = 'lwseed'

@description('Cron for the daily seed timer (UTC). Default is 09:00-12:00 IST.')
param seedSchedule string = '0 30 3-6 * * *'

@description('Linnworks System Integration application id')
@secure()
param linnworksApplicationId string = ''

@description('Linnworks System Integration application secret')
@secure()
param linnworksApplicationSecret string = ''

@description('Linnworks permanent token')
@secure()
param linnworksPermanentToken string = ''

@description('Flex Consumption instance memory in MB')
@allowed([512, 2048, 4096])
param instanceMemoryMB int = 2048

@description('Maximum Flex Consumption instances')
param maximumInstanceCount int = 40

var uniqueSuffix = uniqueString(resourceGroup().id, namePrefix)
var storageName = take(replace('st${namePrefix}${uniqueSuffix}', '-', ''), 24)
var planName = 'plan-${namePrefix}-${uniqueSuffix}'
var functionAppName = 'func-${namePrefix}-${uniqueSuffix}'
var appInsightsName = 'appi-${namePrefix}-${uniqueSuffix}'
var logAnalyticsName = 'log-${namePrefix}-${uniqueSuffix}'
var deploymentContainerName = 'app-package-${take(uniqueSuffix, 8)}'
var seedRunsContainerName = 'seed-runs'

// Storage Blob Data Owner
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-44ec-9ec3-0a8f10f7c001'
// Storage Blob Data Contributor
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: true
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource seedRunsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: seedRunsContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
    }
  }
}

resource deploymentStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource runtimeStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionAppSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: {
    // Connection string keeps BlobRunLogStore and local Azurite-compatible code paths simple.
    AzureWebJobsStorage: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storage.listKeys().keys[0].value}'
    FUNCTIONS_EXTENSION_VERSION: '~4'
    APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.properties.ConnectionString
    SeedSchedule: seedSchedule
    Seed__TimeZone: 'India Standard Time'
    Seed__LocationName: 'Default'
    Seed__Source: 'DIRECT'
    Linnworks__ApplicationId: linnworksApplicationId
    Linnworks__ApplicationSecret: linnworksApplicationSecret
    Linnworks__PermanentToken: linnworksPermanentToken
  }
  dependsOn: [
    deploymentStorageRole
    runtimeStorageRole
    deploymentContainer
    seedRunsContainer
  ]
}

output functionAppName string = functionApp.name
output functionAppHostname string = functionApp.properties.defaultHostName
output storageAccountName string = storage.name
output seedRunsContainerName string = seedRunsContainer.name
output applicationInsightsName string = applicationInsights.name
