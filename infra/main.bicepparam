using 'main.bicep'

param location = 'eastus'
param namePrefix = 'lwseed'
param seedSchedule = '0 30 3-6 * * *'
// Pass secrets at deploy time:
//   az deployment group create ... --parameters linnworksApplicationId=... linnworksApplicationSecret=... linnworksPermanentToken=...
param linnworksApplicationId = ''
param linnworksApplicationSecret = ''
param linnworksPermanentToken = ''
