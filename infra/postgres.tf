# ─── PostgreSQL Flexible Server 16 ───────────────────────────────────────────
# Burstable B1ms: 1 vCPU, 2 GB RAM, 32 GB storage.
# Suitable for a SAST platform with ≤ 20 concurrent analyses.
# Upgrade to D2ds_v4 (General Purpose) if response times degrade under load.

# Risques acceptés : accès public filtré par pare-feu tant que le réseau privé
# n'est pas conçu ; sauvegardes locales, la géo-redondance forcerait la recréation.
#trivy:ignore:AVD-AZU-0022
resource "azurerm_postgresql_flexible_server" "sast" {
  #checkov:skip=CKV_AZURE_136:ForceNew sur un serveur existant, sauvegardes locales de 7 jours acceptées
  #checkov:skip=CKV2_AZURE_57:réseau privé non conçu, voir PLATFORM_SAST.md
  name                   = "${local.prefix}-pg"
  resource_group_name    = azurerm_resource_group.sast.name
  location               = azurerm_resource_group.sast.location
  version                = "16"
  administrator_login    = var.postgres_admin_login
  administrator_password = var.postgres_admin_password
  sku_name               = "B_Standard_B1ms"

  storage_mb                   = var.postgres_storage_gb * 1024
  auto_grow_enabled            = true
  backup_retention_days        = 7
  geo_redundant_backup_enabled = false

  tags = local.common_tags

  lifecycle {
    ignore_changes = [
      # Password rotations are done outside Terraform to avoid plan drift
      administrator_password,
      zone,
    ]
  }
}

# Journalise connexions et checkpoints ; limite temporairement une IP après des
# échecs de connexion répétés. Paramètres dynamiques : aucun redémarrage requis.
resource "azurerm_postgresql_flexible_server_configuration" "hardening" {
  for_each = toset(["log_checkpoints", "log_connections", "connection_throttle.enable"])

  name      = each.value
  server_id = azurerm_postgresql_flexible_server.sast.id
  value     = "on"
}

resource "azurerm_postgresql_flexible_server_database" "app" {
  name      = var.postgres_db_name
  server_id = azurerm_postgresql_flexible_server.sast.id
  charset   = "utf8"
  collation = "en_US.utf8"
}

# Allow all Azure-originated IPs (App Service outbound IPs are dynamic).
# Switch to private networking (VNet integration) for enhanced security.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  #checkov:skip=CKV2_AZURE_26:IP sortantes App Service dynamiques, réseau privé non conçu, voir PLATFORM_SAST.md
  name      = "AllowAzureServices"
  server_id = azurerm_postgresql_flexible_server.sast.id

  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "developer" {
  count     = var.developer_ip != "" ? 1 : 0
  name      = "DeveloperAccess"
  server_id = azurerm_postgresql_flexible_server.sast.id

  start_ip_address = var.developer_ip
  end_ip_address   = var.developer_ip
}
