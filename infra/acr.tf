# ─── Azure Container Registry (existing shared registry) ──────────────────────
# The SAST platform images are pushed into the existing shared ACR "upgroup"
# in the fd-devops resource group. We reference it as a data source — Terraform
# does not manage its lifecycle.
#
# Resource ID:
#   /subscriptions/6936e3c2-8d29-4883-892b-9490850e9caa
#   /resourceGroups/fd-devops
#   /providers/Microsoft.ContainerRegistry/registries/upgroup

data "azurerm_container_registry" "main" {
  name                = "upgroup"
  resource_group_name = "fd-devops"
}
