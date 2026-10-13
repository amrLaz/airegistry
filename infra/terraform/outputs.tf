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
