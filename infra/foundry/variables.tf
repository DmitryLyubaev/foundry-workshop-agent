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
  description = "Azure region. Both models are offered as Global Standard in eastus2, and a deployment needs its account in a listed region."
  default     = "eastus2"
}

# Variables, not data.azurerm_client_config, so the roles go to the owner and to CI whoever's
# sign-in applies the stack.
variable "owner_object_id" {
  type        = string
  description = "Entra object ID of the owner: az ad signed-in-user show --query id -o tsv."

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.owner_object_id))
    error_message = "owner_object_id must be an Entra object ID, a GUID."
  }
}

variable "ci_principal_id" {
  type        = string
  description = "Object ID of the CI service principal: infra/bootstrap's output ci_principal_id."

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.ci_principal_id))
    error_message = "ci_principal_id must be an Entra object ID, a GUID."
  }
}

# The GPT deployment never upgrades on its own, so the study runs on one model version throughout.
variable "gpt_model_version" {
  type        = string
  description = "The gpt-5.6-luna model version to pin, as the region's model list gives it, in YYYY-MM-DD form: az cognitiveservices model list --location eastus2 --query \"[?model.name=='gpt-5.6-luna'].model.version\"."

  validation {
    condition     = can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", var.gpt_model_version))
    error_message = "gpt_model_version must be a model version of the form YYYY-MM-DD."
  }
}

variable "gpt_capacity" {
  type        = number
  description = "Capacity of the gpt-5.6-luna Global Standard deployment, in thousands of tokens per minute: 50 is 50,000 TPM. It bounds how fast spend can grow, not how much."
  default     = 50

  validation {
    condition     = var.gpt_capacity >= 1 && var.gpt_capacity <= 200 && floor(var.gpt_capacity) == var.gpt_capacity
    error_message = "gpt_capacity must be a whole number from 1 to 200 (thousands of TPM): every capacity is small."
  }
}

variable "claude_model_version" {
  type        = string
  description = "The claude-haiku-4-5 model version to pin. Foundry's Claude models are listed as version 1 (the Claude starter kit's default)."
  default     = "1"
}

variable "claude_capacity" {
  type        = number
  description = "Capacity of the claude-haiku-4-5 Global Standard deployment, in thousands of tokens per minute: 25 is 25,000 TPM (the Claude starter kit's default)."
  default     = 25

  validation {
    condition     = var.claude_capacity >= 1 && var.claude_capacity <= 80 && floor(var.claude_capacity) == var.claude_capacity
    error_message = "claude_capacity must be a whole number from 1 to 80 (thousands of TPM): every capacity is small."
  }
}

# modelProviderData. The first apply of the Claude deployment accepts Anthropic's Marketplace terms
# on the owner's behalf with these details, so none has a default: the owner sets each one, in a
# git-ignored terraform.tfvars, and says yes before that apply.
variable "claude_provider_organization" {
  type        = string
  description = "The organisation name given to Anthropic's Marketplace offer (modelProviderData.organizationName)."

  validation {
    condition     = length(trimspace(var.claude_provider_organization)) > 0
    error_message = "claude_provider_organization must not be empty."
  }
}

variable "claude_provider_country_code" {
  type        = string
  description = "The organisation's two-letter ISO country code (modelProviderData.countryCode), for example AU."

  validation {
    condition     = can(regex("^[A-Z]{2}$", var.claude_provider_country_code))
    error_message = "claude_provider_country_code must be two capital letters, an ISO 3166-1 alpha-2 code."
  }
}

variable "claude_provider_industry" {
  type        = string
  description = "The organisation's industry (modelProviderData.industry), in lowercase as the Foundry portal's list has it: technology, finance, healthcare, education, retail, manufacturing, government, media or other."

  validation {
    condition     = contains(["technology", "finance", "healthcare", "education", "retail", "manufacturing", "government", "media", "other"], var.claude_provider_industry)
    error_message = "claude_provider_industry must be one of the portal's values, in lowercase: technology, finance, healthcare, education, retail, manufacturing, government, media, other."
  }
}
