// MyRegistry : état désiré d'un environnement (portée : groupe de ressources).
// Source de vérité : ce fichier + infra/params/<env>.bicepparam. Aucune modification manuelle dans le portail.
targetScope = 'resourceGroup'

@description('Nom court de l\'environnement : dev | prod')
param environmentName string
param location string = resourceGroup().location

@description('Registre ACR partagé (créé une fois, voir docs/08-gitops.md)')
param acrName string
param acrResourceGroup string

@description('Tag exact de l\'image déployée (SHA de commit). Pour prod : épinglé dans le fichier de paramètres.')
param imageTag string

param publicUrl string
param dbAdminLogin string = 'myregistryadmin'
@secure()
param dbAdminPassword string

@allowed(['dev', 'oidc'])
param authMode string = 'oidc'
param minReplicas int = 1
param maxReplicas int = 3
param postgresSku string = 'Standard_B1ms'

var prefix = 'myregistry-${environmentName}'
var tags = {
  app: 'myregistry'
  environment: environmentName
  managedBy: 'gitops'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${prefix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}'
  location: location
  tags: tags
}

module acrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-${environmentName}'
  scope: resourceGroup(acrResourceGroup)
  params: {
    acrName: acrName
    principalId: identity.properties.principalId
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${prefix}'
  location: location
  tags: tags
  sku: {
    name: postgresSku
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: dbAdminLogin
    administratorLoginPassword: dbAdminPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'myregistry'
}

// Accès depuis les services Azure (Container Apps). À remplacer par un VNet privé en production durcie.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv-${prefix}', 24)
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource dbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'db-connection'
  properties: {
    value: 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=myregistry;Username=${dbAdminLogin};Password=${dbAdminPassword};Ssl Mode=Require'
  }
}

var keyVaultSecretsUserRoleId = '4633458b-17de-4bb7-a8fb-e2a8dc4f4d4f'

resource kvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${prefix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${prefix}-web'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Multiple' // double run : permet le rollback par bascule de trafic
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        stickySessions: { affinity: 'sticky' } // requis par Blazor Server
        traffic: [{ latestRevision: true, weight: 100 }]
      }
      registries: [
        {
          server: '${acrName}.azurecr.io'
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'db-connection'
          keyVaultUrl: dbSecret.properties.secretUri
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${acrName}.azurecr.io/myregistry-web:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'MYREGISTRY_DB_CONNECTION', secretRef: 'db-connection' }
            { name: 'MYREGISTRY_PUBLIC_URL', value: publicUrl }
            { name: 'MYREGISTRY_AUTH_MODE', value: authMode }
            { name: 'MYREGISTRY_STORAGE', value: 'local' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
  dependsOn: [acrPull, kvRole]
}

output appFqdn string = app.properties.configuration.ingress.fqdn
output keyVaultName string = vault.name
