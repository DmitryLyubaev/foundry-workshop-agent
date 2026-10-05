terraform {
  required_version = "~> 1.15.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.8"
    }
    # For the Claude deployment, which azurerm cannot express (it has no modelProviderData:
    # hashicorp/terraform-provider-azurerm#31140), and the project's Application Insights connection.
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.13"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id

  # infra/bootstrap registers every namespace this stack uses.
  resource_provider_registrations = "none"

  features {
    # A destroyed account stays soft-deleted, holding its name and its quota for up to 48 hours,
    # unless it is purged.
    cognitive_account {
      purge_soft_delete_on_destroy = true
    }
  }
}

provider "azapi" {
  subscription_id            = var.subscription_id
  skip_provider_registration = true
}
