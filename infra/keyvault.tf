# ─── Key Vault ────────────────────────────────────────────────────────────────
# Stocke les secrets applicatifs et les expose à l'App Service via Key Vault
# References (@Microsoft.KeyVault) — jamais en clair dans app_settings.

# Risque accepté : App Service sans intégration VNet et agents Terraform hébergés
# joignent le coffre par son endpoint public, protégé par RBAC Entra.
#trivy:ignore:AVD-AZU-0013
resource "azurerm_key_vault" "sast" {
  #checkov:skip=CKV_AZURE_109:réseau privé non conçu, voir PLATFORM_SAST.md
  #checkov:skip=CKV_AZURE_189:réseau privé non conçu, voir PLATFORM_SAST.md
  #checkov:skip=CKV2_AZURE_32:réseau privé non conçu, voir PLATFORM_SAST.md
  # KV names: 3-24 chars, alphanumeric + hyphens, globally unique.
  name                       = "up-fr-odi-sast-prod-kv"
  resource_group_name        = azurerm_resource_group.sast.name
  location                   = azurerm_resource_group.sast.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  purge_protection_enabled   = true
  tags                       = local.common_tags
}

# Donne au principal Terraform (CI/CD ou opérateur) le droit d'écrire les secrets.
resource "azurerm_role_assignment" "kv_admin" {
  scope                = azurerm_key_vault.sast.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Donne à l'identité managée de l'App Service le droit de lire les secrets.
resource "azurerm_role_assignment" "app_kv_secrets_user" {
  scope                = azurerm_key_vault.sast.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.sast.identity[0].principal_id
}

resource "azurerm_key_vault_secret" "authentication_signing_key" {
  name            = "authentication-signing-key"
  value           = var.authentication_signing_key
  content_type    = "text/plain"
  expiration_date = var.key_vault_secret_expiration_date
  key_vault_id    = azurerm_key_vault.sast.id
  depends_on      = [azurerm_role_assignment.kv_admin]
}

resource "azurerm_key_vault_secret" "bootstrap_admin_password" {
  name            = "bootstrap-admin-password"
  value           = var.platform_admin_password
  content_type    = "text/plain"
  expiration_date = var.key_vault_secret_expiration_date
  key_vault_id    = azurerm_key_vault.sast.id
  depends_on      = [azurerm_role_assignment.kv_admin]
}
