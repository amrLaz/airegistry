using '../main.bicep'

param environmentName = 'prod'
param acrName = 'myregistryacr'
param acrResourceGroup = 'rg-myregistry-shared'
// ÉTAT DÉSIRÉ DE PROD : épinglé ici. Changer ce tag = une PR vers main (promotion), jamais un déploiement manuel.
param imageTag = 'bootstrap'
param publicUrl = 'https://myregistry.example.com'
param authMode = 'oidc'
param minReplicas = 2
param maxReplicas = 6
param postgresSku = 'Standard_D2ds_v5'
param dbAdminPassword = readEnvironmentVariable('MYREGISTRY_DB_ADMIN_PASSWORD', '')
