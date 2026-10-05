# Foundry stack

The Azure side of the study: a keyless Microsoft Foundry resource in `eastus2` with one project,
the two model deployments the agent runs on, and the Application Insights its traces go to. The
owner applies it from their own machine; CI never runs it. Its state is in `infra/bootstrap`'s
storage account. The design is in the
[spec](../../docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md), §6.

**Not yet applied.** This page describes what the code configures. Every value on it is a
placeholder.

## What it holds

| Resource | Name | Notes |
|---|---|---|
| Resource group | `rg-fwa-foundry` | `eastus2` |
| Foundry resource | `fwa-<suffix>` | `azurerm_cognitive_account`, kind `AIServices`, SKU `S0`; custom subdomain `fwa-<suffix>`; `local_auth_enabled = false`; `project_management_enabled = true`; system-assigned identity |
| Project | `fwa-workshop` | `azurerm_cognitive_account_project`, system-assigned identity |
| GPT deployment | `gpt-5.6-luna` | `azurerm_cognitive_deployment`, Global Standard, capacity `gpt_capacity` (default 50, thousands of TPM), version pinned by `gpt_model_version`, `NoAutoUpgrade` |
| Claude deployment | `claude-haiku-4-5` | `azapi_resource` `Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview`, format `Anthropic`, Global Standard, capacity `claude_capacity` (default 25), `modelProviderData` from the variables, schema validation off |
| Log Analytics | `log-fwa-<suffix>` | `PerGB2018`, 30 days; local authentication off |
| Application Insights | `appi-fwa-<suffix>` | workspace-based; `local_authentication_enabled = false` |
| Connection | `appinsights` | `azapi_resource` `Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01` on the project: category `AppInsights`, auth `AAD` (the project's managed identity), target the Application Insights resource |
| Role assignments | see below | |

`<suffix>` is six random lowercase letters and digits, new on each create. Every resource that
takes tags carries `project = foundry-workshop-agent`.

### Roles

All by role-definition GUID. Foundry User is `53ca6127-db72-4b80-b1b0-d745d6d5456d`; Monitoring
Metrics Publisher is `3913510d-42f4-4e42-8a64-420c390055eb`.

| Principal | Role | Scope | Why |
|---|---|---|---|
| owner | Foundry User | project | prompt agents, responses, evaluations |
| CI | Foundry User | project | the same, for the live workflow (GPT only) |
| project's identity | Foundry User | Foundry resource | Foundry's documented minimum for a project |
| owner | Foundry User | Foundry resource | Claude's Messages API is on the resource's endpoint, which a project role does not reach |
| owner | Monitoring Metrics Publisher | Application Insights | the client's trace exporter ingests with Entra |
| CI | Monitoring Metrics Publisher | Application Insights | the same, in the live workflow |
| project's identity | Monitoring Metrics Publisher | Application Insights | Foundry's server-side tracing ingests with Entra |

Foundry User cannot deploy models; the owner deploys them through this stack, as Owner.

### Nothing bills by the hour

The resource, the project and idle deployments are free; tokens bill per use and Application
Insights per GB ingested (spec §6.2). So the stack can stay up between sessions.

### Outputs

Each output is one of the client's settings:

| Output | Client setting | Sensitive |
|---|---|---|
| `project_endpoint` | `FWA_PROJECT_ENDPOINT` | yes |
| `resource_endpoint` | `FWA_RESOURCE_ENDPOINT` | yes |
| `gpt_deployment` | `FWA_GPT_DEPLOYMENT` | no |
| `claude_deployment` | `FWA_CLAUDE_DEPLOYMENT` | no |
| `appinsights_connection_string` | `FWA_APPINSIGHTS_CONNECTION_STRING` | yes |

The agent's name is not an output: the client's default, `fwa-workshop-agent`, applies. The
endpoints name the resource and its suffix, so they are identifiers. The connection string holds no
working key once local authentication is off, but it identifies the resource. Set each one in the
environment of your own shell, never in a file:

```powershell
$env:FWA_PROJECT_ENDPOINT = wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/foundry && TF_DATA_DIR=$HOME/tfdata/fwa-foundry terraform output -raw project_endpoint'
```

and the same for the others.

## Inputs

| Variable | Value | Default |
|---|---|---|
| `subscription_id` | the subscription: `az account show --query id -o tsv` | none |
| `owner_object_id` | the owner's object ID: `az ad signed-in-user show --query id -o tsv` | none |
| `ci_principal_id` | `infra/bootstrap`'s output `ci_principal_id` | none |
| `gpt_model_version` | the version to pin, `YYYY-MM-DD`: `az cognitiveservices model list --location eastus2 --query "[?model.name=='gpt-5.6-luna'].model.version"` | none |
| `claude_provider_organization` | the organisation name for Anthropic's Marketplace offer | none |
| `claude_provider_country_code` | its two-letter country code, such as `AU` | none |
| `claude_provider_industry` | its industry, in lowercase: `technology`, `finance`, `healthcare`, `education`, `retail`, `manufacturing`, `government`, `media` or `other` | none |
| `gpt_capacity` | thousands of TPM | `50` |
| `claude_capacity` | thousands of TPM | `25` |
| `claude_model_version` | the Claude version to pin | `1` |
| `location` | the region | `eastus2` |

Put them in a git-ignored `terraform.tfvars` in this folder, in your own window:

```hcl
subscription_id              = "00000000-0000-0000-0000-000000000000"
owner_object_id              = "00000000-0000-0000-0000-000000000000"
ci_principal_id              = "00000000-0000-0000-0000-000000000000"
gpt_model_version            = "<YYYY-MM-DD>"
claude_provider_organization = "<organisation>"
claude_provider_country_code = "<CC>"
claude_provider_industry     = "<industry>"
```

Never commit it. `git check-ignore -v infra/foundry/terraform.tfvars` must name the ignore rule.

## Plan and apply

Terraform runs in WSL, as [`../bootstrap`](../bootstrap/README.md#terraform-runs-in-wsl)
describes, with this stack's own data directory. Bootstrap must be applied first: it creates the
state container and gives the owner a data role on it.

```bash
cd /mnt/<drive>/<path-to-clone>/infra/foundry
export TF_DATA_DIR="$HOME/tfdata/fwa-foundry" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
terraform init -backend-config=storage_account_name=<state_storage_account>
```

**The first apply of the Claude deployment accepts Anthropic's Marketplace terms on the owner's
behalf**, with the organisation, country and industry in `modelProviderData`. It needs the owner's
explicit yes, and a pay-as-you-go subscription in a country where Anthropic sells the models.

```bash
terraform plan -out=tfplan
terraform apply tfplan
rm tfplan
```

The plan must add a resource group, a random suffix, the Foundry resource, the project, two
deployments, a Log Analytics workspace, Application Insights, the connection and seven role
assignments, and nothing else.

- **Deployments go one at a time.** The GPT deployment waits for the project and Claude waits for
  GPT, because Foundry answers `409` to concurrent changes on one account.
- **A deployment can outlast Terraform's wait.** If the apply times out while a deployment is still
  provisioning, wait, then plan again: the plan shows whether it finished.
- **An error with `715-123420` in it** most likely means no quota. Check the region's quota for the
  model and lower the capacity.
- **A new role assignment can take a few minutes to take effect.** A `403` straight after the apply
  usually clears on its own.

## Destroy and purge

```bash
terraform destroy
```

Application Insights creates a smart-detection alert rule and an action group in `rg-fwa-foundry`,
outside Terraform. The group holds this stack only, so the provider is set to delete it with whatever
is in it (`prevent_deletion_if_contains_resources = false`); without that, destroy would stop at the
group.

A destroyed Foundry resource is only soft-deleted: it keeps its name, and its deployments' quota,
for up to 48 hours. The provider is set to purge it on destroy
(`purge_soft_delete_on_destroy = true`). If an account is ever left soft-deleted anyway (a destroy
that failed part-way, or one deleted outside Terraform), list and purge it:

```bash
az cognitiveservices account list-deleted --query "[?tags.project=='foundry-workshop-agent'].{name:name, location:location}" -o table
az cognitiveservices account purge --name <account> --resource-group rg-fwa-foundry --location eastus2
```

Then check that nothing is left: this must print `false`.

```bash
az group exists --name rg-fwa-foundry
```

## Tests

```bash
terraform init -backend=false -input=false
terraform validate
terraform test
```

The tests plan against mocked providers, and never sign in, apply or destroy anything. CI runs
them, with `terraform fmt -check`, on every pull request to `main`. The static checks over
`infra/` are in `tests/Workshop.Agent.Tests/StaticInfraTests.cs`.
