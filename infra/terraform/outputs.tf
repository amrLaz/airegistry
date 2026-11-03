output "app_fqdn" {
  description = "Nom d'hôte de l'application."
  value       = azurerm_container_app.web.ingress[0].fqdn
}

output "key_vault_name" {
  value = azurerm_key_vault.vault.name
}

output "postgres_fqdn" {
  value = azurerm_postgresql_flexible_server.db.fqdn
}

output "storage_account_name" {
  value = azurerm_storage_account.blobs.name
}

output "identity_client_id" {
  description = "Client ID de l'identité managée de l'application (à donner aux droits d'accès externes si besoin)."
  value       = azurerm_user_assigned_identity.app.client_id
}
