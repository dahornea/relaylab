terraform {
  required_version = "= 1.16.1"
  backend "azurerm" { use_azuread_auth = true }
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "= 5.4.0" }
  }
}
provider "azurerm" {
  features {}
  subscription_id                 = var.subscription_id
  resource_provider_registrations = "none"
  storage_use_azuread             = true
}
