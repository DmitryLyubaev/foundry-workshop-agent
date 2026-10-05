locals {
  tags = {
    project = "foundry-workshop-agent"
  }

  # Built-in role definitions go by GUID rather than name, while the Foundry role rename rolls out.
  roles = {
    foundry_user                 = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d"
    monitoring_metrics_publisher = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb"
  }

  # Each deployment is named after its model, as the client expects.
  gpt_model    = "gpt-5.6-luna"
  claude_model = "claude-haiku-4-5"
}

resource "azurerm_resource_group" "foundry" {
  name     = "rg-fwa-foundry"
  location = var.location
  tags     = local.tags
}

# The account's name and subdomain must be unique, and a soft-deleted account keeps its name for
# up to 48 hours, so each create takes a fresh suffix.
resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

# The Foundry resource. Keyless: with local auth off, only Entra tokens work, and token auth needs
# the custom subdomain.
resource "azurerm_cognitive_account" "foundry" {
  name                       = "fwa-${random_string.suffix.result}"
  resource_group_name        = azurerm_resource_group.foundry.name
  location                   = azurerm_resource_group.foundry.location
  kind                       = "AIServices"
  sku_name                   = "S0"
  custom_subdomain_name      = "fwa-${random_string.suffix.result}"
  local_auth_enabled         = false
  project_management_enabled = true

  identity {
    type = "SystemAssigned"
  }

  tags = local.tags
}

resource "azurerm_cognitive_account_project" "workshop" {
  name                 = "fwa-workshop"
  cognitive_account_id = azurerm_cognitive_account.foundry.id
  location             = azurerm_cognitive_account.foundry.location
  display_name         = "Foundry workshop agent"
  description          = "The workshop agent's GPT prompt agent, its evaluations and its traces."

  identity {
    type = "SystemAssigned"
  }

  tags = local.tags
}

# Deployments on one account must be made one at a time, or Foundry answers 409, so each waits for
# the change before it: the project, then GPT, then Claude.
resource "azurerm_cognitive_deployment" "gpt" {
  name                   = local.gpt_model
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"

  model {
    format  = "OpenAI"
    name    = local.gpt_model
    version = var.gpt_model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.gpt_capacity
  }

  depends_on = [azurerm_cognitive_account_project.workshop]
}

# Claude, through azapi: azurerm cannot pass modelProviderData (terraform-provider-azurerm#31140).
# The first apply accepts Anthropic's Marketplace terms on the owner's behalf, with the details in
# modelProviderData. The API version is the Claude starter kit's; azapi's embedded schema for it has
# no modelProviderData, so schema validation is off, as the kit has it.
resource "azapi_resource" "claude" {
  type                      = "Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview"
  name                      = local.claude_model
  parent_id                 = azurerm_cognitive_account.foundry.id
  schema_validation_enabled = false

  body = {
    sku = {
      name     = "GlobalStandard"
      capacity = var.claude_capacity
    }
    properties = {
      model = {
        format  = "Anthropic"
        name    = local.claude_model
        version = var.claude_model_version
      }
      modelProviderData = {
        organizationName = var.claude_provider_organization
        countryCode      = var.claude_provider_country_code
        industry         = var.claude_provider_industry
      }
    }
  }

  tags = local.tags

  depends_on = [azurerm_cognitive_deployment.gpt]
}

resource "azurerm_log_analytics_workspace" "foundry" {
  name                = "log-fwa-${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.foundry.name
  location            = azurerm_resource_group.foundry.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # Queries and ingestion through Entra only, as for Application Insights.
  local_authentication_enabled = false

  tags = local.tags
}

# Workspace-based. In azurerm 5.x local authentication is local_authentication_enabled (4.x had
# local_authentication_disabled). With it off, the instrumentation key in the connection string
# authenticates nothing: every exporter signs in through Entra and needs Monitoring Metrics
# Publisher.
resource "azurerm_application_insights" "foundry" {
  name                = "appi-fwa-${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.foundry.name
  location            = azurerm_resource_group.foundry.location
  workspace_id        = azurerm_log_analytics_workspace.foundry.id
  application_type    = "other"

  local_authentication_enabled = false

  tags = local.tags
}

# The project's Application Insights connection, which turns on Foundry's server-side tracing. It
# authenticates as the project's managed identity (Entra) rather than with the connection string as
# an API key, because local authentication is off; that identity gets Monitoring Metrics Publisher
# below. A project allows one connection of this category.
resource "azapi_resource" "appinsights_connection" {
  type      = "Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01"
  name      = "appinsights"
  parent_id = azurerm_cognitive_account_project.workshop.id

  body = {
    properties = {
      category      = "AppInsights"
      authType      = "AAD"
      target        = azurerm_application_insights.foundry.id
      isSharedToAll = false
      metadata = {
        ApiType    = "Azure"
        ResourceId = azurerm_application_insights.foundry.id
      }
    }
  }
}

# Foundry User: data actions on the project (prompt agents, responses, evaluations), and no right to
# deploy models.

resource "azurerm_role_assignment" "owner_project_foundry_user" {
  scope              = azurerm_cognitive_account_project.workshop.id
  role_definition_id = local.roles.foundry_user
  principal_id       = var.owner_object_id
}

resource "azurerm_role_assignment" "ci_project_foundry_user" {
  scope              = azurerm_cognitive_account_project.workshop.id
  role_definition_id = local.roles.foundry_user
  principal_id       = var.ci_principal_id
  principal_type     = "ServicePrincipal"
}

# The project's identity acts on the resource for the project, as Foundry's documented minimum set
# requires.
resource "azurerm_role_assignment" "project_resource_foundry_user" {
  scope              = azurerm_cognitive_account.foundry.id
  role_definition_id = local.roles.foundry_user
  principal_id       = azurerm_cognitive_account_project.workshop.identity[0].principal_id
  principal_type     = "ServicePrincipal"
}

# Claude's Messages API is on the resource's endpoint, not the project's, and a role on the project
# does not reach the resource. Only the owner runs the Claude engine; CI runs GPT only.
resource "azurerm_role_assignment" "owner_resource_foundry_user" {
  scope              = azurerm_cognitive_account.foundry.id
  role_definition_id = local.roles.foundry_user
  principal_id       = var.owner_object_id
}

# Monitoring Metrics Publisher lets an identity ingest into Application Insights with Entra: the
# owner's and CI's trace exporters, and the project's identity for server-side tracing.

resource "azurerm_role_assignment" "owner_appinsights_publisher" {
  scope              = azurerm_application_insights.foundry.id
  role_definition_id = local.roles.monitoring_metrics_publisher
  principal_id       = var.owner_object_id
}

resource "azurerm_role_assignment" "ci_appinsights_publisher" {
  scope              = azurerm_application_insights.foundry.id
  role_definition_id = local.roles.monitoring_metrics_publisher
  principal_id       = var.ci_principal_id
  principal_type     = "ServicePrincipal"
}

resource "azurerm_role_assignment" "project_appinsights_publisher" {
  scope              = azurerm_application_insights.foundry.id
  role_definition_id = local.roles.monitoring_metrics_publisher
  principal_id       = azurerm_cognitive_account_project.workshop.identity[0].principal_id
  principal_type     = "ServicePrincipal"
}
