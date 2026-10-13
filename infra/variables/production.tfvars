# Non-secret variables for the production environment.
# postgres_admin_password is provided at apply time via TF_VAR_postgres_admin_password.

project             = "sast"
program             = "DevSecOps"
env                 = "prod"
location            = "westeurope"
resource_group_name = "up-france-odi-sast-prod-rg"

# ACR: shared registry "upgroup" in fd-devops — referenced as data source, not managed here

webapp_sku = "B1"

postgres_admin_login = "sast_admin"
postgres_db_name     = "sast_platform"
postgres_storage_gb  = 32

# developer_ip = "x.x.x.x"   # uncomment for direct DB access from your workstation
