# ─── App Service Plan ─────────────────────────────────────────────────────────

# Risque accepté : B1 mono-instance, la capacité n'est pas engagée (PLATFORM_SAST.md).
resource "azurerm_service_plan" "sast" {
  #checkov:skip=CKV_AZURE_211:B1 accepté, pas d'engagement de service
  #checkov:skip=CKV_AZURE_212:mono-instance acceptée, pas d'engagement de service
  #checkov:skip=CKV_AZURE_225:redondance de zone non disponible en B1
  name                = "${local.prefix}-asp"
  resource_group_name = azurerm_resource_group.sast.name
  location            = azurerm_resource_group.sast.location
  os_type             = "Linux"
  sku_name            = var.webapp_sku
  tags                = local.common_tags
}

# ─── Linux Web App (Docker container) ────────────────────────────────────────
# Sert ASP.NET Core 10 et React dans une image construite par Azure DevOps.

# Non applicable : l'API authentifie elle-même (JWT, Entra) navigateurs et CI ;
# EasyAuth ou un certificat client bloqueraient l'ingestion CI. Hub public par
# conception, joint par les agents hébergés.
#trivy:ignore:AVD-AZU-0001
#trivy:ignore:AVD-AZU-0003
resource "azurerm_linux_web_app" "sast" {
  #checkov:skip=CKV_AZURE_13:authentification portée par l'API (JWT, Entra)
  #checkov:skip=CKV_AZURE_17:aucun client ne présente de certificat, authentification portée par l'API
  #checkov:skip=CKV_AZURE_88:application sans état local, stockage Blob via identité managée
  #checkov:skip=CKV_AZURE_222:hub public joint par navigateurs et agents CI hébergés
  name                = "${local.prefix}-app"
  resource_group_name = azurerm_resource_group.sast.name
  location            = azurerm_resource_group.sast.location
  service_plan_id     = azurerm_service_plan.sast.id
  https_only          = true

  # Le déploiement passe par ACR et l'identité managée : aucune publication FTP ou Web Deploy.
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    container_registry_use_managed_identity = true
    health_check_path                       = "/api/v1/health"
    health_check_eviction_time_in_min       = 2
    ftps_state                              = "Disabled"
    http2_enabled                           = true

    application_stack {
      docker_image_name   = "sast-platform:latest"
      docker_registry_url = "https://${data.azurerm_container_registry.main.login_server}"
    }
  }

  logs {
    detailed_error_messages = true
    failed_request_tracing  = true

    http_logs {
      file_system {
        retention_in_days = 7
        retention_in_mb   = 35
      }
    }
  }

  app_settings = {
    ConnectionStrings__CodeInsights    = format("Host=%s;Database=\"%s\";Username=\"%s\";Password=\"%s\";SSL Mode=Require", azurerm_postgresql_flexible_server.sast.fqdn, replace(var.postgres_db_name, "\"", "\"\""), replace(var.postgres_admin_login, "\"", "\"\""), replace(var.postgres_admin_password, "\"", "\"\""))
    Bootstrap__AdminEmail              = var.platform_admin_email
    Bootstrap__AdminPassword           = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.sast.name};SecretName=bootstrap-admin-password)"
    Bootstrap__RequireCompleteMappings = tostring(var.p0_require_complete_mappings)
    Authentication__SigningKey         = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.sast.name};SecretName=authentication-signing-key)"
    Authentication__LocalAccountsMode  = "bootstrap-only"
    Authentication__AutoEntraSignIn    = "true"
    WEBSITES_PORT                      = "8000"
    Ingestion__BlobServiceUri          = azurerm_storage_account.reports.primary_blob_endpoint
    Ingestion__Container               = azurerm_storage_container.reports.name
    Ingestion__PlatformUrl             = "https://${local.prefix}-app.azurewebsites.net"
  }

  tags       = local.common_tags
  depends_on = [azurerm_postgresql_flexible_server_database.app, azurerm_key_vault_secret.authentication_signing_key, azurerm_key_vault_secret.bootstrap_admin_password]

  # Azure DevOps sélectionne le tag d'image à déployer.
  lifecycle {
    ignore_changes = [site_config[0].application_stack]
  }
}

# ─── AcrPull permission pour l'identité managée du Web App ────────────────────

resource "azurerm_role_assignment" "app_acr_pull" {
  scope                = data.azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_linux_web_app.sast.identity[0].principal_id
}
