using '../main.bicep'

param environmentName = 'dev'
param acrName = 'myregistryacr'
param acrResourceGroup = 'rg-myregistry-shared'
// Le tag est injecté par le workflow deploy.yml (SHA du commit de develop) : -p imageTag=<sha>
param imageTag = 'bootstrap'
param publicUrl = 'https://dev.myregistry.example.com'
param authMode = 'oidc'
param minReplicas = 1
param maxReplicas = 2
param dbAdminPassword = readEnvironmentVariable('MYREGISTRY_DB_ADMIN_PASSWORD', '')
