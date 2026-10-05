# None of these is a credential, but each identifies the owner's tenant, subscription or resources,
# so all are sensitive: apply prints none of them, and each is read on purpose with
# terraform output -raw <name>. The first three become secrets of the GitHub environment live-eval
# (AZURE_CLIENT_ID, AZURE_TENANT_ID and AZURE_SUBSCRIPTION_ID), never a file.

output "ci_client_id" {
  description = "Client ID of the CI application: the live-eval environment's AZURE_CLIENT_ID secret."
  value       = azuread_application_registration.ci.client_id
  sensitive   = true
}

output "tenant_id" {
  description = "Entra tenant ID, from the owner's sign-in: the live-eval environment's AZURE_TENANT_ID secret."
  value       = data.azurerm_client_config.current.tenant_id
  sensitive   = true
}

output "subscription_id" {
  description = "Azure subscription ID: the live-eval environment's AZURE_SUBSCRIPTION_ID secret."
  value       = var.subscription_id
  sensitive   = true
}

output "ci_principal_id" {
  description = "Object ID of the CI service principal. infra/foundry takes it as ci_principal_id, to give CI its Foundry roles."
  value       = azuread_service_principal.ci.object_id
  sensitive   = true
}

output "state_storage_account" {
  description = "Name of the storage account holding infra/foundry's state: terraform init -backend-config=storage_account_name=<this>."
  value       = azurerm_storage_account.state.name
  sensitive   = true
}
