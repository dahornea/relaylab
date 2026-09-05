variable "subscription_id" { type = string }
variable "tenant_id" { type = string }
variable "name_prefix" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{5,15}$", var.name_prefix))
    error_message = "Use the bootstrap's 6-16 character name prefix."
  }
}
variable "location" { default = "westeurope" }
variable "resource_group_name" { type = string }
variable "deploy_object_id" { type = string }
variable "api_audience" { type = string }
variable "receiver_audience" { type = string }
variable "deploy_schema_jobs" { default = false }
variable "deploy_apps" { default = false }
variable "images" {
  type    = map(string)
  default = {}
  validation {
    condition     = alltrue([for image in values(var.images) : can(regex("^[a-z0-9]+\\.azurecr\\.io/relaylab-(api|worker|receiver)@sha256:[a-f0-9]{64}$", image))])
    error_message = "Only immutable ACR digest references are accepted."
  }
}
variable "schema_images" {
  type    = map(string)
  default = {}
  validation {
    condition     = alltrue([for image in values(var.schema_images) : can(regex("^[a-z0-9]+\\.azurecr\\.io/relaylab-(api|receiver)@sha256:[a-f0-9]{64}$", image))])
    error_message = "Schema jobs require immutable API and receiver image digests."
  }
}
