variable "subscription_id" {
  type        = string
  description = "Azure subscription ID: az account show --query id -o tsv."

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.subscription_id))
    error_message = "subscription_id must be a subscription ID, a GUID."
  }
}

variable "location" {
  type        = string
  description = "Azure region of the bootstrap group and the state account."
  default     = "eastus2"
}

# The federated subject is built from this and the fixed environment, so the credential can only
# ever trust jobs in the live-eval environment of this one repository.
variable "github_repository" {
  type        = string
  description = "The GitHub repository the CI identity trusts, as <owner>/<repo>, exactly as GitHub's OIDC token writes it in its sub claim (with @<id> after each part if the subject template uses immutable IDs)."

  validation {
    condition     = can(regex("^[A-Za-z0-9-]+(@[0-9]+)?/[A-Za-z0-9._-]+(@[0-9]+)?$", var.github_repository))
    error_message = "github_repository must be <owner>/<repo> (optionally with @<id> after each part), with no other claim in it."
  }
}
