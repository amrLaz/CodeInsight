variable "project" {
  description = "Project segment of the naming convention up-france-odi-{project}-{env}-{service}."
  type        = string
  default     = "sast"
}

variable "program" {
  description = "Program/initiative this project belongs to — used in common_tags."
  type        = string
  default     = "DevSecOps"
}

variable "env" {
  description = "Deployment environment (dev, staging, prod) — used in common_tags and resource names."
  type        = string
  default     = "prod"
  validation {
    condition     = contains(["dev", "staging", "prod"], var.env)
    error_message = "env must be dev, staging, or prod."
  }
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "westeurope"
}

variable "resource_group_name" {
  description = "Resource group name. Defaults to the project naming convention."
  type        = string
  default     = "up-france-odi-sast-prod-rg"
}

# ─── App Service ──────────────────────────────────────────────────────────────

variable "webapp_sku" {
  description = "App Service Plan SKU (B1 minimum pour containers Linux)."
  type        = string
  default     = "B1"
}

variable "authentication_signing_key" {
  description = "Clé de signature JWT de l'API, fournie via TF_VAR_authentication_signing_key."
  type        = string
  sensitive   = true
  validation {
    condition     = length(var.authentication_signing_key) >= 32
    error_message = "La clé de signature JWT doit contenir au moins 32 caractères."
  }
}

variable "key_vault_secret_expiration_date" {
  description = "Expiration RFC 3339 des secrets Key Vault gérés par Terraform. Faire tourner les secrets et repousser cette date avant l'échéance."
  type        = string
  default     = "2027-09-30T00:00:00Z"

  validation {
    condition     = can(timecmp(var.key_vault_secret_expiration_date, "2000-01-01T00:00:00Z"))
    error_message = "key_vault_secret_expiration_date doit être une date RFC 3339, par exemple 2027-09-30T00:00:00Z."
  }
}

variable "platform_admin_email" {
  description = "Email du premier administrateur local créé uniquement lorsqu'aucun administrateur n'existe."
  type        = string
  default     = "admin@codeinsights.local"
}

variable "platform_admin_password" {
  description = "Secret de bootstrap du premier administrateur local. Fournir TF_VAR_platform_admin_password lorsqu'aucun administrateur n'existe ; laisser vide après initialisation."
  type        = string
  default     = ""
  sensitive   = true

  validation {
    condition     = var.platform_admin_password == "" || length(var.platform_admin_password) >= 12
    error_message = "platform_admin_password doit contenir au moins 12 caractères lorsqu'il est défini."
  }
}

variable "p0_require_complete_mappings" {
  description = "Bloque l'activation si un projet, un profil ou un token CI actif ne possède pas de rattachement P0 explicite."
  type        = bool
  default     = false
}

# ─── PostgreSQL ───────────────────────────────────────────────────────────────

variable "postgres_admin_login" {
  description = "PostgreSQL administrator username."
  type        = string
  default     = "sast_admin"
}

variable "postgres_admin_password" {
  description = "PostgreSQL administrator password. Provide via TF_VAR_postgres_admin_password — never commit."
  type        = string
  sensitive   = true
}

variable "postgres_db_name" {
  description = "Application database name."
  type        = string
  default     = "sast_platform"
}

variable "postgres_storage_gb" {
  description = "PostgreSQL storage in GB (32 minimum for Flexible Server)."
  type        = number
  default     = 32
}

variable "developer_ip" {
  description = "Developer public IP to allow direct DB access (optional). Leave empty to skip."
  type        = string
  default     = ""
}
