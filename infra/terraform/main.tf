# MyRegistry : état désiré d'un environnement. Source de vérité : ce dossier + envs/<env>.tfvars.
# Aucune modification manuelle dans le portail Azure (détectée par le workflow de dérive).

data "azurerm_client_config" "current" {}

data "azurerm_resource_group" "env" {
  name = var.resource_group_name
}

data "azurerm_container_registry" "shared" {
  name                = var.acr_name
  resource_group_name = var.acr_resource_group
}

locals {
  prefix = "myregistry-${var.environment_name}"
  tags = {
    app         = "myregistry"
    environment = var.environment_name
    managed_by  = "terraform"
  }
}

resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.prefix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.env.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${local.prefix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.env.name
  tags                = local.tags
}

# L'application tire son image du registre partagé avec son identité managée (pas de mot de passe de registre).
resource "azurerm_role_assignment" "acr_pull" {
  scope                = data.azurerm_container_registry.shared.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_postgresql_flexible_server" "db" {
  name                          = "psql-${local.prefix}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.env.name
  version                       = "16"
  administrator_login           = var.db_admin_login
  administrator_password        = var.db_admin_password
  sku_name                      = var.postgres_sku
  storage_mb                    = 32768
  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  public_network_access_enabled = true
  tags                          = local.tags

  lifecycle {
    # Azure choisit la zone au premier déploiement ; ne pas forcer un déplacement ensuite.
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "app" {
  name      = "myregistry"
  server_id = azurerm_postgresql_flexible_server.db.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# Accès depuis les services Azure (Container Apps). À remplacer par un réseau privé pour une prod durcie.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.db.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_key_vault" "vault" {
  name                       = substr("kv-${local.prefix}", 0, 24)
  location                   = var.location
  resource_group_name        = data.azurerm_resource_group.env.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  purge_protection_enabled   = var.keyvault_purge_protection
  tags                       = local.tags
}

# Le pipeline (identité OIDC) doit pouvoir écrire le secret ; l'application, seulement le lire.
resource "azurerm_role_assignment" "kv_deployer" {
  scope                = azurerm_key_vault.vault.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "kv_app" {
  scope                = azurerm_key_vault.vault.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_key_vault_secret" "db_connection" {
  name         = "db-connection"
  key_vault_id = azurerm_key_vault.vault.id
  value        = "Host=${azurerm_postgresql_flexible_server.db.fqdn};Port=5432;Database=${azurerm_postgresql_flexible_server_database.app.name};Username=${var.db_admin_login};Password=${var.db_admin_password};Ssl Mode=Require"

  # La propagation du rôle RBAC n'est pas instantanée : le secret n'est créé qu'après l'attribution.
  depends_on = [azurerm_role_assignment.kv_deployer]
}

resource "azurerm_container_app_environment" "env" {
  name                       = "cae-${local.prefix}"
  location                   = var.location
  resource_group_name        = data.azurerm_resource_group.env.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  tags                       = local.tags
}

resource "azurerm_container_app" "web" {
  name                         = "ca-${local.prefix}-web"
  container_app_environment_id = azurerm_container_app_environment.env.id
  resource_group_name          = data.azurerm_resource_group.env.name
  revision_mode                = "Multiple" # double run : rollback par bascule de trafic
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = data.azurerm_container_registry.shared.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  secret {
    name                = "db-connection"
    key_vault_secret_id = azurerm_key_vault_secret.db_connection.versionless_id
    identity            = azurerm_user_assigned_identity.app.id
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "web"
      image  = "${data.azurerm_container_registry.shared.login_server}/myregistry-web:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "MYREGISTRY_DB_CONNECTION"
        secret_name = "db-connection"
      }
      env {
        name  = "MYREGISTRY_PUBLIC_URL"
        value = var.public_url
      }
      env {
        name  = "MYREGISTRY_AUTH_MODE"
        value = var.auth_mode
      }
      env {
        name  = "MYREGISTRY_STORAGE"
        value = "local"
      }

      liveness_probe {
        transport = "HTTP"
        path      = "/health"
        port      = 8080
      }

      readiness_probe {
        transport = "HTTP"
        path      = "/health/ready"
        port      = 8080
      }
    }

    http_scale_rule {
      name                = "http"
      concurrent_requests = "50"
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull, azurerm_role_assignment.kv_app]
}
