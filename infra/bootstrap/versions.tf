terraform {
  required_version = "~> 1.15.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.8"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id

  # Registration happens here, for both stacks: the owner applies this one and may register
  # namespaces. "none" is written out so that only this list is registered, whatever the provider's
  # default. Microsoft.AlertsManagement is there because creating Application Insights also creates
  # its smart-detection alert rule.
  resource_provider_registrations = "none"
  resource_providers_to_register = [
    "Microsoft.Storage",
    "Microsoft.CognitiveServices",
    "Microsoft.OperationalInsights",
    "Microsoft.Insights",
    "Microsoft.AlertsManagement",
  ]

  # Storage data-plane calls authenticate with Entra ID, so they work on an account whose shared
  # keys are off.
  storage_use_azuread = true

  features {}
}

# The tenant is the one the owner signed in to with az login.
provider "azuread" {}
