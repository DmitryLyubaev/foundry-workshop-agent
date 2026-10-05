# Partial configuration: the account's name is an identifier, so it is passed at init,
#   terraform init -backend-config=storage_account_name=<state_storage_account>
# where <state_storage_account> is infra/bootstrap's output of that name. Bootstrap creates the
# container and gives the owner a data role on it. The account's shared keys are off, so the backend
# authenticates through Entra ID.
terraform {
  backend "azurerm" {
    container_name   = "tfstate-foundry"
    key              = "foundry.tfstate"
    use_azuread_auth = true
  }
}
