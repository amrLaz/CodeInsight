output "webapp_url" {
  description = "SAST Platform URL. Update PLATFORM_API_URL in Commercant.Common Variable Group to this value."
  value       = "https://${azurerm_linux_web_app.sast.default_hostname}"
}

output "webapp_name" {
  description = "Web App name — valeur du secret CONTAINER_APP_NAME dans GitHub Actions."
  value       = azurerm_linux_web_app.sast.name
}

output "acr_login_server" {
  description = "Shared ACR login server — valeur du secret ACR_LOGIN_SERVER dans GitHub Actions."
  value       = data.azurerm_container_registry.main.login_server
}

output "postgres_fqdn" {
  description = "PostgreSQL Flexible Server FQDN."
  value       = azurerm_postgresql_flexible_server.sast.fqdn
}

output "resource_group_name" {
  description = "Resource group contenant les ressources SAST platform."
  value       = azurerm_resource_group.sast.name
}
