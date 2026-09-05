data "azurerm_resource_group" "application" { name = var.resource_group_name }
locals {
  tags         = { project = "RelayLab", purpose = "temporary-m3-demo", managed_by = "terraform" }
  kinds        = toset(["api", "worker", "receiver", "schema"])
  apps         = toset(["api", "worker", "receiver"])
  databases    = { api = "RelayLab", worker = "RelayLab", receiver = "RelayLabReceiver" }
  sql          = { for kind in local.apps : kind => "Server=tcp:${azurerm_mssql_server.sql.fully_qualified_domain_name},1433;Database=${local.databases[kind]};Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.runtime[kind].client_id};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;" }
  receiver_url = "https://${var.name_prefix}-receiver.${azurerm_container_app_environment.environment.default_domain}"
}
resource "azurerm_container_registry" "registry" {
  name                                         = "${var.name_prefix}acr"
  resource_group_name                          = var.resource_group_name
  location                                     = var.location
  sku                                          = "Basic"
  admin_enabled                                = false
  azuread_authentication_as_arm_policy_enabled = true
  tags                                         = local.tags
}
resource "azurerm_user_assigned_identity" "runtime" {
  for_each            = local.kinds
  name                = "${var.name_prefix}-${each.key}"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = local.tags
}
resource "azurerm_role_assignment" "pull" {
  for_each             = local.kinds
  scope                = azurerm_container_registry.registry.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.runtime[each.key].principal_id
}
resource "azurerm_role_assignment" "push" {
  scope                = azurerm_container_registry.registry.id
  role_definition_name = "AcrPush"
  principal_id         = var.deploy_object_id
}
resource "azurerm_mssql_server" "sql" {
  name                          = "${var.name_prefix}-sql"
  resource_group_name           = var.resource_group_name
  location                      = var.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  connection_policy             = "Proxy"
  public_network_access_enabled = true
  identity { type = "SystemAssigned" } # No Graph directory permissions.
  azuread_administrator {
    login_username              = "relaylab-schema"
    object_id                   = azurerm_user_assigned_identity.runtime["schema"].principal_id
    tenant_id                   = var.tenant_id
    azuread_authentication_only = true
  }
  tags = local.tags
}
# Minimal public-service demo: Azure source firewall access, Entra-only SQL authentication.
# This is not private network isolation; no public client IP ranges are enabled.
resource "azurerm_mssql_firewall_rule" "azure" {
  name             = "AzureServices"
  server_id        = azurerm_mssql_server.sql.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}
resource "azurerm_mssql_database" "database" {
  for_each             = toset(["RelayLab", "RelayLabReceiver"])
  name                 = each.key
  server_id            = azurerm_mssql_server.sql.id
  sku_name             = "Basic"
  max_size_gb          = 2
  storage_account_type = "Local"
  short_term_retention_policy { retention_days = 7 }
  tags = local.tags
}
resource "azurerm_servicebus_namespace" "bus" {
  name                = "${var.name_prefix}-bus"
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = "Standard"
  minimum_tls_version = "1.2"
  local_auth_enabled  = false
  tags                = local.tags
}
resource "azurerm_servicebus_queue" "deliveries" {
  name                                 = "deliveries"
  namespace_id                         = azurerm_servicebus_namespace.bus.id
  lock_duration                        = "PT1M"
  default_message_ttl                  = "PT1H"
  dead_lettering_on_message_expiration = true
  max_delivery_count                   = 10
  requires_duplicate_detection         = false
  max_size_in_megabytes                = 1024
}
resource "azurerm_role_assignment" "bus" {
  for_each             = toset(["Azure Service Bus Data Sender", "Azure Service Bus Data Receiver"])
  scope                = azurerm_servicebus_queue.deliveries.id
  role_definition_name = each.key
  principal_id         = azurerm_user_assigned_identity.runtime["worker"].principal_id
}
resource "azurerm_log_analytics_workspace" "logs" {
  name                = "${var.name_prefix}-logs"
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.1
  tags                = local.tags
}
resource "azurerm_application_insights" "telemetry" {
  name                         = "${var.name_prefix}-telemetry"
  resource_group_name          = var.resource_group_name
  location                     = var.location
  workspace_id                 = azurerm_log_analytics_workspace.logs.id
  application_type             = "web"
  local_authentication_enabled = false
  daily_data_cap_in_gb         = 0.1
  sampling_percentage          = 100
  tags                         = local.tags
}
resource "azurerm_role_assignment" "telemetry" {
  for_each             = local.apps
  scope                = azurerm_application_insights.telemetry.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.runtime[each.key].principal_id
}
resource "azurerm_container_app_environment" "environment" {
  name                       = "${var.name_prefix}-environment"
  resource_group_name        = var.resource_group_name
  location                   = var.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
  tags = local.tags
}
