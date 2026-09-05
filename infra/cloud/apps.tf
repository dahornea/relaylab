locals {
  app_environment = { for kind in local.apps : kind => merge({
    DOTNET_ENVIRONMENT                                                   = "Production"
    ASPNETCORE_ENVIRONMENT                                               = "Production"
    ASPNETCORE_URLS                                                      = "http://+:8080"
    Azure__ClientId                                                      = azurerm_user_assigned_identity.runtime[kind].client_id
    APPLICATIONINSIGHTS_CONNECTION_STRING                                = azurerm_application_insights.telemetry.connection_string
    "ConnectionStrings__${kind == "receiver" ? "Receiver" : "RelayLab"}" = local.sql[kind]
    }, kind == "worker" ? {
    Azure__ServiceBusNamespace = "${azurerm_servicebus_namespace.bus.name}.servicebus.windows.net"
    RelayLab__Queue            = azurerm_servicebus_queue.deliveries.name
    RelayLab__DestinationUrl   = "${local.receiver_url}/webhooks"
    Security__ReceiverAudience = var.receiver_audience
    } : {
    Security__TenantId         = var.tenant_id
    Security__Audience         = kind == "api" ? var.api_audience : var.receiver_audience
    Security__CallerObjectId   = kind == "api" ? var.deploy_object_id : azurerm_user_assigned_identity.runtime["worker"].principal_id
    Security__OperatorObjectId = var.deploy_object_id
  }) }
}
resource "azurerm_container_app_job" "schema" {
  for_each                     = var.deploy_schema_jobs ? toset(["api", "receiver"]) : toset([])
  name                         = "${var.name_prefix}-schema-${each.key}"
  resource_group_name          = var.resource_group_name
  location                     = var.location
  container_app_environment_id = azurerm_container_app_environment.environment.id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 600
  replica_retry_limit          = 0
  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.runtime["schema"].id]
  }
  registry {
    server   = azurerm_container_registry.registry.login_server
    identity = azurerm_user_assigned_identity.runtime["schema"].id
  }
  template {
    container {
      name    = "schema"
      image   = var.schema_images[each.key]
      cpu     = 0.25
      memory  = "0.5Gi"
      command = ["dotnet", each.key == "api" ? "RelayLab.Api.dll" : "RelayLab.Receiver.dll", "--deploy-schema"]
      dynamic "env" {
        for_each = {
          DOTNET_ENVIRONMENT                                                  = "Production"
          Azure__ClientId                                                     = azurerm_user_assigned_identity.runtime["schema"].client_id
          "ConnectionStrings__${each.key == "api" ? "RelayLab" : "Receiver"}" = "Server=tcp:${azurerm_mssql_server.sql.fully_qualified_domain_name},1433;Database=${local.databases[each.key]};Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.runtime["schema"].client_id};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
          Schema__ApiObjectId                                                 = azurerm_user_assigned_identity.runtime["api"].principal_id
          Schema__WorkerObjectId                                              = azurerm_user_assigned_identity.runtime["worker"].principal_id
          Schema__ReceiverObjectId                                            = azurerm_user_assigned_identity.runtime["receiver"].principal_id
        }
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }
  depends_on = [azurerm_role_assignment.pull, azurerm_mssql_database.database, azurerm_mssql_firewall_rule.azure]
  tags       = local.tags
}
resource "azurerm_container_app" "application" {
  for_each                     = var.deploy_apps ? local.apps : toset([])
  name                         = "${var.name_prefix}-${each.key}"
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.environment.id
  workload_profile_name        = "Consumption"
  revision_mode                = "Single"
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.runtime[each.key].id]
  }
  registry {
    server   = azurerm_container_registry.registry.login_server
    identity = azurerm_user_assigned_identity.runtime[each.key].id
  }
  dynamic "ingress" {
    for_each = each.key == "worker" ? [] : [1]
    content {
      external_enabled           = true
      allow_insecure_connections = false
      target_port                = 8080
      transport                  = "http"
      traffic_weight {
        latest_revision = true
        percentage      = 100
      }
    }
  }
  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name    = each.key
      image   = var.images[each.key]
      cpu     = 0.25
      memory  = "0.5Gi"
      command = ["dotnet", "RelayLab.${each.key == "api" ? "Api" : each.key == "worker" ? "Worker" : "Receiver"}.dll"]
      dynamic "env" {
        for_each = local.app_environment[each.key]
        content {
          name  = env.key
          value = env.value
        }
      }
      dynamic "readiness_probe" {
        for_each = each.key == "worker" ? [] : [1]
        content {
          transport        = "HTTP"
          port             = 8080
          path             = "/health/ready"
          initial_delay    = 10
          timeout          = 5
          interval_seconds = 10
        }
      }
      dynamic "liveness_probe" {
        for_each = each.key == "worker" ? [] : [1]
        content {
          transport        = "HTTP"
          port             = 8080
          path             = "/health/live"
          initial_delay    = 30
          timeout          = 5
          interval_seconds = 30
        }
      }
    }
  }
  depends_on = [azurerm_role_assignment.pull, azurerm_role_assignment.bus, azurerm_role_assignment.telemetry]
  tags       = local.tags
}
output "deployment" {
  value = {
    resource_group    = var.resource_group_name
    name_prefix       = var.name_prefix
    registry          = azurerm_container_registry.registry.name
    registry_server   = azurerm_container_registry.registry.login_server
    api_url           = "https://${var.name_prefix}-api.${azurerm_container_app_environment.environment.default_domain}"
    receiver_url      = local.receiver_url
    api_audience      = var.api_audience
    receiver_audience = var.receiver_audience
    telemetry_app_id  = azurerm_application_insights.telemetry.app_id
    images            = var.images
    schema_version    = 1
  }
}
