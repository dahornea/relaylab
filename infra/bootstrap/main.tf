terraform {
  required_version = "= 1.16.1"
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "= 5.4.0" }
    azuread = { source = "hashicorp/azuread", version = "= 3.9.0" }
  }
}
provider "azurerm" {
  features {}
  subscription_id                 = var.subscription_id
  resource_provider_registrations = "none"
  storage_use_azuread             = true
}
provider "azuread" { tenant_id = var.tenant_id }
variable "subscription_id" { type = string }
variable "tenant_id" { type = string }
variable "name_prefix" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{5,15}$", var.name_prefix))
    error_message = "Use 6-16 lowercase alphanumeric characters, globally unique for storage and ACR names."
  }
}
variable "location" { default = "westeurope" }
variable "github_repository" { default = "dahornea/relaylab" }
variable "github_environment" { default = "relaylab-demo" }
data "azurerm_client_config" "operator" {}
data "azuread_client_config" "operator" {}
locals { tags = { project = "RelayLab", purpose = "temporary-m3-demo", managed_by = "terraform" } }
resource "azurerm_resource_group" "application" {
  name     = "${var.name_prefix}-app"
  location = var.location
  tags     = local.tags
}
resource "azurerm_resource_group" "control" {
  name     = "${var.name_prefix}-control"
  location = var.location
  tags     = local.tags
}
resource "azurerm_storage_account" "state" {
  name                            = "${var.name_prefix}state"
  resource_group_name             = azurerm_resource_group.control.name
  location                        = var.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  allow_nested_items_to_be_public = false
  tags                            = local.tags
  blob_properties {
    versioning_enabled = true
    delete_retention_policy { days = 7 }
    container_delete_retention_policy { days = 7 }
  }
}
resource "azurerm_role_assignment" "state_operator" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.operator.object_id
}
resource "azurerm_storage_container" "state" {
  name                  = "tfstate"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
  depends_on            = [azurerm_role_assignment.state_operator]
}
resource "azurerm_user_assigned_identity" "deploy" {
  name                = "${var.name_prefix}-deploy"
  resource_group_name = azurerm_resource_group.control.name
  location            = var.location
  tags                = local.tags
}
resource "azurerm_federated_identity_credential" "github" {
  name                      = "github-environment"
  user_assigned_identity_id = azurerm_user_assigned_identity.deploy.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = "https://token.actions.githubusercontent.com"
  subject                   = "repo:${var.github_repository}:environment:${var.github_environment}"
}
resource "azurerm_role_assignment" "deploy" {
  for_each             = toset(["Contributor", "Role Based Access Control Administrator"])
  scope                = azurerm_resource_group.application.id
  role_definition_name = each.key
  principal_id         = azurerm_user_assigned_identity.deploy.principal_id
}
resource "azurerm_role_assignment" "state_deploy" {
  scope                = "${azurerm_storage_account.state.id}/blobServices/default/containers/${azurerm_storage_container.state.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.deploy.principal_id
}
# Tenant changes are one-time owner bootstrap work, never delegated to the deployment identity.
resource "azuread_application" "audience" {
  for_each         = toset(["api", "receiver"])
  display_name     = "${var.name_prefix}-${each.key}"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.operator.object_id]
  api { requested_access_token_version = 2 }
  optional_claims {
    access_token { name = "idtyp" }
  }
}
resource "azuread_application_identifier_uri" "audience" {
  for_each       = azuread_application.audience
  application_id = each.value.id
  identifier_uri = "api://${each.value.client_id}"
}
resource "azuread_service_principal" "audience" {
  for_each                     = azuread_application.audience
  client_id                    = each.value.client_id
  app_role_assignment_required = false # API enforces app-only tenant/object-ID ACLs; no role-less access is implicit.
  owners                       = [data.azuread_client_config.operator.object_id]
}
output "configuration" {
  value = {
    subscription_id     = var.subscription_id
    tenant_id           = var.tenant_id
    name_prefix         = var.name_prefix
    location            = var.location
    resource_group_name = azurerm_resource_group.application.name
    deploy_client_id    = azurerm_user_assigned_identity.deploy.client_id
    deploy_object_id    = azurerm_user_assigned_identity.deploy.principal_id
    api_audience        = azuread_application.audience["api"].client_id
    receiver_audience   = azuread_application.audience["receiver"].client_id
    state_account       = azurerm_storage_account.state.name
    state_container     = azurerm_storage_container.state.name
    state_key           = "relaylab.tfstate"
  }
}
