resource "azurerm_role_assignment" "deployment_reports" {
  scope                = azurerm_resource_group.sast.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Risques acceptés : endpoint public protégé par Entra (clés partagées
# désactivées), rapports CI transitoires en LRS, clés gérées par Microsoft.
# Le compte n'héberge aucune file : la journalisation Queue est sans objet.
# infrastructure_encryption_enabled est ForceNew sur un compte existant.
#trivy:ignore:AVD-AZU-0012
#trivy:ignore:AVD-AZU-0057
#trivy:ignore:AVD-AZU-0058
#trivy:ignore:AVD-AZU-0060
#trivy:ignore:AVD-AZU-0061
resource "azurerm_storage_account" "reports" {
  #checkov:skip=CKV_AZURE_33:aucune file Queue sur ce compte
  #checkov:skip=CKV_AZURE_43:nom calculé ci + 18 hex, conforme mais non évaluable statiquement
  #checkov:skip=CKV_AZURE_59:réseau privé non conçu, voir PLATFORM_SAST.md
  #checkov:skip=CKV_AZURE_206:rapports CI transitoires, LRS accepté
  #checkov:skip=CKV2_AZURE_1:clés gérées par Microsoft acceptées
  #checkov:skip=CKV2_AZURE_33:réseau privé non conçu, voir PLATFORM_SAST.md
  depends_on                      = [azurerm_role_assignment.deployment_reports]
  name                            = "ci${substr(sha256(azurerm_resource_group.sast.id), 0, 18)}"
  resource_group_name             = azurerm_resource_group.sast.name
  location                        = azurerm_resource_group.sast.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
  tags                            = local.common_tags

  blob_properties {
    delete_retention_policy {
      days = 7
    }
  }
}

resource "azurerm_storage_container" "reports" {
  name                  = "reports"
  storage_account_id    = azurerm_storage_account.reports.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "app_reports" {
  scope                = azurerm_storage_account.reports.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_web_app.sast.identity[0].principal_id
}
