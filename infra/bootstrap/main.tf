locals {
  tags = {
    project = "foundry-workshop-agent"
  }

  # Built-in role definitions go by GUID rather than name: names change (the Foundry roles were
  # renamed in 2026) and GUIDs do not.
  storage_blob_data_contributor = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"

  # The environment is fixed here, not a variable: the live workflow runs in live-eval, and that
  # environment's deployment rule is what limits the credential to main.
  federated_subject = "repo:${var.github_repository}:environment:live-eval"
}

# The owner applies this stack, so the owner's principal and tenant are the signed-in ones.
data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "bootstrap" {
  name     = "rg-fwa-bootstrap"
  location = var.location
  tags     = local.tags
}

# The state account's name must be unique across Azure.
resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

# Terraform state for infra/foundry. With shared keys and local users off, every data-plane request
# has to be authorised through Entra ID.
resource "azurerm_storage_account" "state" {
  name                     = "stfwastate${random_string.suffix.result}"
  resource_group_name      = azurerm_resource_group.bootstrap.name
  location                 = azurerm_resource_group.bootstrap.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  local_user_enabled              = false
  allow_nested_items_to_be_public = false
  min_tls_version                 = "TLS1_2"

  # A damaged or deleted state can be recovered from an earlier version or from soft delete.
  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 7
    }

    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.tags
}

resource "azurerm_storage_container" "foundry" {
  name                  = "tfstate-foundry"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
}

# The owner applies infra/foundry, so the owner reads and writes its state. Owner and Contributor
# have no data actions, so even the subscription's owner needs this role. Its scope is the
# container (in azurerm 5.x, the container's id is its Resource Manager ID), not the account.
resource "azurerm_role_assignment" "owner_state_foundry" {
  scope              = azurerm_storage_container.foundry.id
  role_definition_id = local.storage_blob_data_contributor
  principal_id       = data.azurerm_client_config.current.object_id
}

# The CI identity: an Entra application with no secret, which can sign in only through the one
# federated credential below. Its Foundry roles are granted in infra/foundry; it gets nothing here,
# so it can neither read the state nor write role assignments.
resource "azuread_application_registration" "ci" {
  display_name = "foundry-workshop-agent-live-eval"
  description  = "The CI identity of foundry-workshop-agent's live-eval GitHub environment. Federated credential only, no secret."
}

resource "azuread_service_principal" "ci" {
  client_id = azuread_application_registration.ci.client_id
}

# The only credential. GitHub issues a token with this subject only to a job running in this
# repository's live-eval environment, so a pull request, a fork, or a job without the environment
# cannot get an Azure token.
resource "azuread_application_federated_identity_credential" "live_eval" {
  application_id = azuread_application_registration.ci.id
  display_name   = "github-environment-live-eval"
  description    = "GitHub Actions, environment live-eval only."
  issuer         = "https://token.actions.githubusercontent.com"
  audiences      = ["api://AzureADTokenExchange"]
  subject        = local.federated_subject
}
