terraform {
  required_version = ">= 1.5.0"

  # Backend config is passed via -backend-config at init time.
  # See variables/backend-production.tfvars for production values.
  backend "azurerm" {}

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  resource_provider_registrations = "none"
  storage_use_azuread             = true
  features {}
}

data "azurerm_client_config" "current" {}

# ─── Resource Group ───────────────────────────────────────────────────────────

resource "azurerm_resource_group" "sast" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.common_tags
}

# ─── Locals ───────────────────────────────────────────────────────────────────

locals {
  # Naming convention: up-france-odi-{project}-{environment}-{servicename}
  prefix = "up-france-odi-${var.project}-${var.env}"

  common_tags = {
    CostCenter        = "LA FABRIK"
    Environment       = var.env
    OrganizationName  = "UP Group"
    Project           = var.project
    Program           = var.program
    Service           = var.project
    ProjectOwner      = "Guillaume CHOMAT"
    ProjectOwnerEmail = "guillaume.chomat@up.coop"
    IsProduction      = var.env != "prod" ? "Non" : "Oui"
    TechLeads         = "amr.lazraq.ext@up.coop"
  }
}
