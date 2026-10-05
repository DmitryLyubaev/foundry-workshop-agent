# Plans against mocked providers: nothing here signs in, and nothing is created. Every value is a
# placeholder.

variables {
  subscription_id              = "00000000-0000-0000-0000-000000000000"
  owner_object_id              = "11111111-1111-1111-1111-111111111111"
  ci_principal_id              = "22222222-2222-2222-2222-222222222222"
  gpt_model_version            = "2026-01-01"
  claude_provider_organization = "Example Organisation"
  claude_provider_country_code = "AU"
  claude_provider_industry     = "technology"
}

mock_provider "azurerm" {
  override_during = plan

  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry"
    }
  }

  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry/providers/Microsoft.CognitiveServices/accounts/fwa-a1b2c3"
    }
  }

  mock_resource "azurerm_cognitive_account_project" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry/providers/Microsoft.CognitiveServices/accounts/fwa-a1b2c3/projects/fwa-workshop"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry/providers/Microsoft.OperationalInsights/workspaces/log-fwa-a1b2c3"
    }
  }

  mock_resource "azurerm_application_insights" {
    defaults = {
      id                = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry/providers/Microsoft.Insights/components/appi-fwa-a1b2c3"
      connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/"
    }
  }
}

mock_provider "azapi" {
  override_during = plan
}

# Names and the subdomain are built from the suffix, so the suffix must be known at plan.
mock_provider "random" {
  override_during = plan

  mock_resource "random_string" {
    defaults = {
      result = "a1b2c3"
    }
  }
}

# The project's identity is a computed block, so its principal is set here.
override_resource {
  target          = azurerm_cognitive_account_project.workshop
  override_during = plan
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-foundry/providers/Microsoft.CognitiveServices/accounts/fwa-a1b2c3/projects/fwa-workshop"
    identity = {
      principal_id = "33333333-3333-3333-3333-333333333333"
      tenant_id    = "00000000-0000-0000-0000-000000000000"
    }
  }
}

run "foundry_resource" {
  command = plan

  assert {
    condition     = azurerm_resource_group.foundry.name == "rg-fwa-foundry" && azurerm_resource_group.foundry.location == "eastus2"
    error_message = "The resource group must be rg-fwa-foundry, in eastus2."
  }

  assert {
    condition     = azurerm_cognitive_account.foundry.kind == "AIServices" && azurerm_cognitive_account.foundry.sku_name == "S0" && azurerm_cognitive_account.foundry.location == "eastus2"
    error_message = "The Foundry resource must be kind AIServices, SKU S0, in eastus2."
  }

  assert {
    condition     = azurerm_cognitive_account.foundry.local_auth_enabled == false
    error_message = "The Foundry resource's local (key) authentication must be off."
  }

  assert {
    condition     = azurerm_cognitive_account.foundry.custom_subdomain_name == "fwa-a1b2c3" && azurerm_cognitive_account.foundry.name == "fwa-a1b2c3"
    error_message = "The Foundry resource and its custom subdomain must be fwa-<suffix>: Entra tokens need the subdomain."
  }

  assert {
    condition     = azurerm_cognitive_account.foundry.project_management_enabled == true
    error_message = "Project management must be on, or the account cannot hold a project."
  }

  assert {
    condition     = azurerm_cognitive_account.foundry.identity[0].type == "SystemAssigned"
    error_message = "The Foundry resource must have a system-assigned identity."
  }

  assert {
    condition     = azurerm_cognitive_account_project.workshop.cognitive_account_id == azurerm_cognitive_account.foundry.id && azurerm_cognitive_account_project.workshop.location == "eastus2"
    error_message = "The project must be on the Foundry resource, in eastus2."
  }
}

run "deployments" {
  command = plan

  assert {
    condition     = azurerm_cognitive_deployment.gpt.name == "gpt-5.6-luna" && azurerm_cognitive_deployment.gpt.model[0].format == "OpenAI" && azurerm_cognitive_deployment.gpt.model[0].name == "gpt-5.6-luna" && azurerm_cognitive_deployment.gpt.model[0].version == "2026-01-01"
    error_message = "The GPT deployment must be gpt-5.6-luna, at the pinned version."
  }

  assert {
    condition     = azurerm_cognitive_deployment.gpt.sku[0].name == "GlobalStandard" && azurerm_cognitive_deployment.gpt.sku[0].capacity == 25
    error_message = "The GPT deployment must be Global Standard with capacity 25 (thousands of TPM) by default."
  }

  assert {
    condition     = azurerm_cognitive_deployment.gpt.version_upgrade_option == "NoAutoUpgrade" && azurerm_cognitive_deployment.gpt.cognitive_account_id == azurerm_cognitive_account.foundry.id
    error_message = "The GPT deployment must never upgrade on its own, and must be on the Foundry resource."
  }

  assert {
    condition     = azapi_resource.claude.type == "Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview" && azapi_resource.claude.parent_id == azurerm_cognitive_account.foundry.id && azapi_resource.claude.name == "claude-haiku-4-5"
    error_message = "The Claude deployment must be an accounts/deployments@2025-10-01-preview named claude-haiku-4-5, on the Foundry resource."
  }

  assert {
    condition     = azapi_resource.claude.body.properties.model.format == "Anthropic" && azapi_resource.claude.body.properties.model.name == "claude-haiku-4-5" && azapi_resource.claude.body.properties.model.version == "1"
    error_message = "The Claude deployment's model must be Anthropic claude-haiku-4-5, at the pinned version."
  }

  assert {
    condition     = azapi_resource.claude.body.properties.versionUpgradeOption == "NoAutoUpgrade"
    error_message = "The Claude deployment must never upgrade on its own, as GPT's does not: the study runs on one model version throughout."
  }

  assert {
    condition     = azapi_resource.claude.body.sku.name == "GlobalStandard" && azapi_resource.claude.body.sku.capacity == 25
    error_message = "The Claude deployment must be Global Standard with capacity 25 (thousands of TPM) by default."
  }

  assert {
    condition     = azurerm_cognitive_deployment.gpt.sku[0].capacity == azapi_resource.claude.body.sku.capacity
    error_message = "Both deployments must have the same capacity: throttling waits count in a run's time, so unequal capacities would favour one engine."
  }

  assert {
    condition     = azapi_resource.claude.body.properties.modelProviderData.organizationName == "Example Organisation" && azapi_resource.claude.body.properties.modelProviderData.countryCode == "AU" && azapi_resource.claude.body.properties.modelProviderData.industry == "technology"
    error_message = "modelProviderData must come from the variables."
  }

  assert {
    condition     = azapi_resource.claude.schema_validation_enabled == false
    error_message = "The Claude deployment needs schema validation off: the embedded schema has no modelProviderData."
  }
}

run "one_capacity_for_both_deployments" {
  command = plan

  variables {
    capacity = 10
  }

  assert {
    condition     = azurerm_cognitive_deployment.gpt.sku[0].capacity == 10 && azapi_resource.claude.body.sku.capacity == 10
    error_message = "Both deployments' capacity must come from the one capacity variable."
  }
}

run "capacity_stays_small" {
  command = plan

  variables {
    capacity = 81
  }

  expect_failures = [var.capacity]
}

run "monitoring" {
  command = plan

  assert {
    condition     = azurerm_application_insights.foundry.local_authentication_enabled == false
    error_message = "Application Insights' local (key) authentication must be off, so ingestion needs Entra."
  }

  assert {
    condition     = azurerm_application_insights.foundry.workspace_id == azurerm_log_analytics_workspace.foundry.id && azurerm_application_insights.foundry.application_type == "other"
    error_message = "Application Insights must be workspace-based, on the stack's Log Analytics workspace."
  }

  assert {
    condition     = azurerm_log_analytics_workspace.foundry.local_authentication_enabled == false
    error_message = "The Log Analytics workspace's shared-key authentication must be off."
  }

  assert {
    condition     = azurerm_log_analytics_workspace.foundry.daily_quota_gb == 1
    error_message = "The Log Analytics workspace must cap its ingestion at 1 GB a day, so a runaway trace cannot grow the bill."
  }

  assert {
    condition     = azapi_resource.appinsights_connection.type == "Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01" && azapi_resource.appinsights_connection.parent_id == azurerm_cognitive_account_project.workshop.id
    error_message = "The Application Insights connection must be a projects/connections@2025-06-01 on the project."
  }

  assert {
    condition     = azapi_resource.appinsights_connection.body.properties.category == "AppInsights" && azapi_resource.appinsights_connection.body.properties.authType == "AAD" && azapi_resource.appinsights_connection.body.properties.target == azurerm_application_insights.foundry.id
    error_message = "The connection must be category AppInsights, Entra-authenticated, targeting the stack's Application Insights."
  }

  assert {
    condition     = azapi_resource.appinsights_connection.body.properties.metadata.ResourceId == azurerm_application_insights.foundry.id && azapi_resource.appinsights_connection.body.properties.metadata.ApiType == "Azure"
    error_message = "The connection's metadata must name the Application Insights resource."
  }
}

run "roles" {
  command = plan

  # Foundry User on the project: the owner and CI.
  assert {
    condition     = azurerm_role_assignment.owner_project_foundry_user.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d" && azurerm_role_assignment.owner_project_foundry_user.scope == azurerm_cognitive_account_project.workshop.id && azurerm_role_assignment.owner_project_foundry_user.principal_id == "11111111-1111-1111-1111-111111111111"
    error_message = "The owner must have Foundry User on the project."
  }

  assert {
    condition     = azurerm_role_assignment.ci_project_foundry_user.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d" && azurerm_role_assignment.ci_project_foundry_user.scope == azurerm_cognitive_account_project.workshop.id && azurerm_role_assignment.ci_project_foundry_user.principal_id == "22222222-2222-2222-2222-222222222222"
    error_message = "CI must have Foundry User on the project."
  }

  # Foundry User on the resource: the project's identity, and the owner for Claude's endpoint.
  assert {
    condition     = azurerm_role_assignment.project_resource_foundry_user.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d" && azurerm_role_assignment.project_resource_foundry_user.scope == azurerm_cognitive_account.foundry.id && azurerm_role_assignment.project_resource_foundry_user.principal_id == "33333333-3333-3333-3333-333333333333"
    error_message = "The project's managed identity must have Foundry User on the Foundry resource."
  }

  assert {
    condition     = azurerm_role_assignment.owner_resource_foundry_user.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d" && azurerm_role_assignment.owner_resource_foundry_user.scope == azurerm_cognitive_account.foundry.id && azurerm_role_assignment.owner_resource_foundry_user.principal_id == "11111111-1111-1111-1111-111111111111"
    error_message = "The owner must have Foundry User on the Foundry resource, where Claude's endpoint is."
  }

  # Monitoring Metrics Publisher on Application Insights: the owner, CI and the project's identity.
  assert {
    condition     = azurerm_role_assignment.owner_appinsights_publisher.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb" && azurerm_role_assignment.owner_appinsights_publisher.scope == azurerm_application_insights.foundry.id && azurerm_role_assignment.owner_appinsights_publisher.principal_id == "11111111-1111-1111-1111-111111111111"
    error_message = "The owner must have Monitoring Metrics Publisher on Application Insights."
  }

  assert {
    condition     = azurerm_role_assignment.ci_appinsights_publisher.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb" && azurerm_role_assignment.ci_appinsights_publisher.scope == azurerm_application_insights.foundry.id && azurerm_role_assignment.ci_appinsights_publisher.principal_id == "22222222-2222-2222-2222-222222222222"
    error_message = "CI must have Monitoring Metrics Publisher on Application Insights."
  }

  assert {
    condition     = azurerm_role_assignment.project_appinsights_publisher.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb" && azurerm_role_assignment.project_appinsights_publisher.scope == azurerm_application_insights.foundry.id && azurerm_role_assignment.project_appinsights_publisher.principal_id == "33333333-3333-3333-3333-333333333333"
    error_message = "The project's managed identity must have Monitoring Metrics Publisher on Application Insights, for Entra trace ingestion."
  }
}

run "tags" {
  command = plan

  # Role assignments take no tags; the connection is not a taggable resource.
  assert {
    condition = alltrue([
      for tags in [
        azurerm_resource_group.foundry.tags,
        azurerm_cognitive_account.foundry.tags,
        azurerm_cognitive_account_project.workshop.tags,
        azurerm_log_analytics_workspace.foundry.tags,
        azurerm_application_insights.foundry.tags,
        azapi_resource.claude.tags,
      ] : tags["project"] == "foundry-workshop-agent"
    ])
    error_message = "Every taggable resource must carry project = foundry-workshop-agent."
  }
}

run "outputs" {
  command = plan

  assert {
    condition     = output.resource_endpoint == "https://fwa-a1b2c3.services.ai.azure.com/"
    error_message = "resource_endpoint must be https://<subdomain>.services.ai.azure.com/."
  }

  assert {
    condition     = output.project_endpoint == "https://fwa-a1b2c3.services.ai.azure.com/api/projects/fwa-workshop"
    error_message = "project_endpoint must be https://<subdomain>.services.ai.azure.com/api/projects/<project>."
  }

  assert {
    condition     = output.gpt_deployment == "gpt-5.6-luna" && output.claude_deployment == "claude-haiku-4-5"
    error_message = "The deployment outputs must be the deployments' names."
  }

  assert {
    condition     = output.appinsights_connection_string == azurerm_application_insights.foundry.connection_string
    error_message = "appinsights_connection_string must be Application Insights' connection string."
  }
}

run "industry_must_be_lowercase" {
  command = plan

  variables {
    claude_provider_industry = "Technology"
  }

  expect_failures = [var.claude_provider_industry]
}

run "country_code_must_be_two_letters" {
  command = plan

  variables {
    claude_provider_country_code = "AUS"
  }

  expect_failures = [var.claude_provider_country_code]
}

run "owner_must_be_a_guid" {
  command = plan

  variables {
    owner_object_id = "owner@example.com"
  }

  expect_failures = [var.owner_object_id]
}
