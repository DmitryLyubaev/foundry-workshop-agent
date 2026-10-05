# Each output is one of the client's settings, by name: project_endpoint is FWA_PROJECT_ENDPOINT,
# resource_endpoint FWA_RESOURCE_ENDPOINT, gpt_deployment FWA_GPT_DEPLOYMENT, claude_deployment
# FWA_CLAUDE_DEPLOYMENT and appinsights_connection_string FWA_APPINSIGHTS_CONNECTION_STRING. The
# agent's name is not here: the client's default, fwa-workshop-agent, applies.
#
# The endpoints name the resource and its suffix, so they are sensitive, as is the connection
# string: with local authentication off it authenticates nothing, but it identifies the resource.

output "project_endpoint" {
  description = "The project's endpoint: FWA_PROJECT_ENDPOINT."
  value       = "https://${azurerm_cognitive_account.foundry.custom_subdomain_name}.services.ai.azure.com/api/projects/${azurerm_cognitive_account_project.workshop.name}"
  sensitive   = true
}

output "resource_endpoint" {
  description = "The Foundry resource's endpoint, under which Claude's Messages API is: FWA_RESOURCE_ENDPOINT."
  value       = "https://${azurerm_cognitive_account.foundry.custom_subdomain_name}.services.ai.azure.com/"
  sensitive   = true
}

output "gpt_deployment" {
  description = "The gpt-5.6-luna deployment's name: FWA_GPT_DEPLOYMENT."
  value       = azurerm_cognitive_deployment.gpt.name
}

output "claude_deployment" {
  description = "The claude-haiku-4-5 deployment's name: FWA_CLAUDE_DEPLOYMENT."
  value       = azapi_resource.claude.name
}

output "appinsights_connection_string" {
  description = "Application Insights' connection string: FWA_APPINSIGHTS_CONNECTION_STRING."
  value       = azurerm_application_insights.foundry.connection_string
  sensitive   = true
}
