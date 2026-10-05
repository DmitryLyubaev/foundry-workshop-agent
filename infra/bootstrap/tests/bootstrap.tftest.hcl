# Plans against mocked providers: nothing here signs in, and nothing is created. Every value is a
# placeholder.

variables {
  subscription_id   = "00000000-0000-0000-0000-000000000000"
  github_repository = "example-owner/example-repo"
}

mock_provider "azurerm" {
  override_during = plan

  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-bootstrap/providers/Microsoft.Storage/storageAccounts/stfwastatea1b2c3"
    }
  }

  mock_resource "azurerm_storage_container" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-bootstrap/providers/Microsoft.Storage/storageAccounts/stfwastatea1b2c3/blobServices/default/containers/tfstate-foundry"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      object_id = "11111111-1111-1111-1111-111111111111"
      tenant_id = "00000000-0000-0000-0000-000000000000"
    }
  }
}

mock_provider "azuread" {
  override_during = plan

  mock_resource "azuread_application_registration" {
    defaults = {
      id        = "/applications/44444444-4444-4444-4444-444444444444"
      client_id = "55555555-5555-5555-5555-555555555555"
    }
  }

  mock_resource "azuread_service_principal" {
    defaults = {
      object_id = "22222222-2222-2222-2222-222222222222"
    }
  }
}

# The account's name is built from the suffix, so the suffix must be known at plan.
mock_provider "random" {
  override_during = plan

  mock_resource "random_string" {
    defaults = {
      result = "a1b2c3"
    }
  }
}

run "state_storage" {
  command = plan

  assert {
    condition     = azurerm_resource_group.bootstrap.name == "rg-fwa-bootstrap" && azurerm_resource_group.bootstrap.location == "eastus2"
    error_message = "The resource group must be rg-fwa-bootstrap, in eastus2."
  }

  assert {
    condition     = azurerm_storage_account.state.name == "stfwastatea1b2c3" && azurerm_storage_account.state.resource_group_name == "rg-fwa-bootstrap"
    error_message = "The state account must be stfwastate followed by the suffix, in the bootstrap group."
  }

  assert {
    condition     = azurerm_storage_account.state.shared_access_key_enabled == false
    error_message = "The state account's shared keys must be off."
  }

  assert {
    condition     = azurerm_storage_account.state.default_to_oauth_authentication == true
    error_message = "The state account must default to OAuth (Entra) authentication."
  }

  assert {
    condition     = azurerm_storage_account.state.local_user_enabled == false && azurerm_storage_account.state.allow_nested_items_to_be_public == false
    error_message = "The state account's local users and public blob access must be off."
  }

  assert {
    condition     = azurerm_storage_account.state.blob_properties[0].versioning_enabled == true
    error_message = "Blob versioning must be on, so a damaged state can be recovered."
  }

  assert {
    condition     = azurerm_storage_container.foundry.name == "tfstate-foundry" && azurerm_storage_container.foundry.storage_account_id == azurerm_storage_account.state.id && azurerm_storage_container.foundry.container_access_type == "private"
    error_message = "The container must be tfstate-foundry, private, in the state account."
  }

  assert {
    condition     = output.state_storage_account == "stfwastatea1b2c3"
    error_message = "state_storage_account must be the state account's name."
  }
}

run "owner_state_role" {
  command = plan

  assert {
    condition     = azurerm_role_assignment.owner_state_foundry.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"
    error_message = "The owner's role must be Storage Blob Data Contributor, by GUID."
  }

  assert {
    condition     = azurerm_role_assignment.owner_state_foundry.scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fwa-bootstrap/providers/Microsoft.Storage/storageAccounts/stfwastatea1b2c3/blobServices/default/containers/tfstate-foundry"
    error_message = "The owner's role must be on the tfstate-foundry container only, not the account."
  }

  assert {
    condition     = azurerm_role_assignment.owner_state_foundry.principal_id == "11111111-1111-1111-1111-111111111111"
    error_message = "The owner's role must go to the signed-in owner."
  }
}

run "ci_identity" {
  command = plan

  assert {
    condition     = azuread_service_principal.ci.client_id == azuread_application_registration.ci.client_id
    error_message = "The service principal must belong to the CI application."
  }

  assert {
    condition     = azuread_application_federated_identity_credential.live_eval.application_id == azuread_application_registration.ci.id
    error_message = "The federated credential must be on the CI application."
  }

  assert {
    condition     = azuread_application_federated_identity_credential.live_eval.subject == "repo:example-owner/example-repo:environment:live-eval"
    error_message = "The federated subject must be repo:<owner>/<repo>:environment:live-eval."
  }

  assert {
    condition     = azuread_application_federated_identity_credential.live_eval.issuer == "https://token.actions.githubusercontent.com"
    error_message = "The issuer must be GitHub Actions' token issuer."
  }

  assert {
    condition     = azuread_application_federated_identity_credential.live_eval.audiences == tolist(["api://AzureADTokenExchange"])
    error_message = "The audience must be exactly api://AzureADTokenExchange."
  }

  assert {
    condition     = output.ci_client_id == "55555555-5555-5555-5555-555555555555" && output.ci_principal_id == "22222222-2222-2222-2222-222222222222"
    error_message = "ci_client_id and ci_principal_id must be the CI application's client ID and its service principal's object ID."
  }

  assert {
    condition     = output.tenant_id == "00000000-0000-0000-0000-000000000000" && output.subscription_id == "00000000-0000-0000-0000-000000000000"
    error_message = "tenant_id and subscription_id must be the owner's tenant and the subscription."
  }
}

run "tags" {
  command = plan

  # Role assignments, containers and Entra objects take no Azure tags.
  assert {
    condition     = azurerm_resource_group.bootstrap.tags["project"] == "foundry-workshop-agent" && azurerm_storage_account.state.tags["project"] == "foundry-workshop-agent"
    error_message = "Every taggable Azure resource must carry project = foundry-workshop-agent."
  }
}

# The subject is built from the repository, so a value that could widen it is refused.
run "repository_with_a_claim_rejected" {
  command = plan

  variables {
    github_repository = "example-owner/example-repo:ref:refs/heads/main"
  }

  expect_failures = [var.github_repository]
}

run "repository_without_owner_rejected" {
  command = plan

  variables {
    github_repository = "example-repo"
  }

  expect_failures = [var.github_repository]
}

run "subscription_not_a_guid_rejected" {
  command = plan

  variables {
    subscription_id = "my-subscription"
  }

  expect_failures = [var.subscription_id]
}
