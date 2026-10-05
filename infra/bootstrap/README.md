# Bootstrap stack

The owner applies this stack once, from their own machine, before anything else in Azure. It holds
what `infra/foundry` and the live CI workflow stand on: the storage for `infra/foundry`'s state,
and the identity CI signs in as. Its own state stays local, in this folder, git-ignored. The
design is in the [spec](../../docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md),
§6.1.

**Not yet applied.** This page describes what the code configures. Every value on it is a
placeholder.

## What it holds

| Resource | Name | Notes |
|---|---|---|
| Resource group | `rg-fwa-bootstrap` | in `eastus2`; tagged `project = foundry-workshop-agent` |
| Storage account | `stfwastate<suffix>` | Standard LRS; shared keys off, `default_to_oauth_authentication` on, local users off, no public blobs; blob versioning on, 7-day soft delete |
| Container | `tfstate-foundry` | private; `infra/foundry`'s state, key `foundry.tfstate` |
| Role assignment | `owner_state_foundry` | the owner (the signed-in principal), `Storage Blob Data Contributor` (`ba92f5b4-2d11-453d-a403-e96b0029c9fe`), on that container only |
| Entra application | `foundry-workshop-agent-live-eval` | the CI identity; no secret, no certificate |
| Service principal | | the application's, which `infra/foundry` gives its roles |
| Federated credential | `github-environment-live-eval` | issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, subject `repo:<owner>/<repo>:environment:live-eval` |

`<suffix>` is six random lowercase letters and digits, generated on the first apply.

- **Keyless.** The state account accepts only Entra ID, so the owner needs the data role above even
  as the subscription's owner: Owner and Contributor carry no data actions.
- **One credential, one environment.** GitHub gives a token with that subject only to a job that
  runs in this repository's `live-eval` environment. A pull request, a fork, or a job without the
  environment cannot sign in. The environment's deployment rule (`main` only) and its required
  reviewer are what hold it to `main`, so set them before the first live run.
- **CI gets nothing here.** Its Foundry roles are in `infra/foundry`. It cannot read any state or
  write role assignments.
- **Resource providers are registered here,** for both stacks: Storage, CognitiveServices,
  OperationalInsights, Insights and AlertsManagement. `infra/foundry` registers none.

## Inputs

| Variable | Value | Default |
|---|---|---|
| `subscription_id` | the subscription: `az account show --query id -o tsv` | none |
| `github_repository` | `<owner>/<repo>` as GitHub's OIDC `sub` claim writes it | none |
| `location` | the region | `eastus2` |

Put them in a git-ignored `terraform.tfvars` in this folder, in your own window:

```hcl
subscription_id   = "00000000-0000-0000-0000-000000000000"
github_repository = "<owner>/<repo>"
```

Never commit it. `git check-ignore -v infra/bootstrap/terraform.tfvars` must name the ignore rule.

If the owner's account or organisation uses GitHub's immutable-ID subject template, the claim reads
`repo:<owner>@<id>/<repo>@<id>:…`; set `github_repository` to that `<owner>@<id>/<repo>@<id>`
part, exactly.

## Terraform runs in WSL

Every Terraform command runs in Ubuntu under WSL, with the Azure CLI signed in there
(`az login`), and with a data directory per stack:

```bash
cd /mnt/<drive>/<path-to-clone>/infra/bootstrap
export TF_DATA_DIR="$HOME/tfdata/fwa-bootstrap" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
mkdir -p "$TF_PLUGIN_CACHE_DIR"
terraform init
```

From Windows, one command runs the same way:

```powershell
wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/bootstrap && export TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap TF_PLUGIN_CACHE_DIR=$HOME/.terraform.d/plugin-cache && terraform plan'
```

Keep `--exec`, so that `$HOME` reaches `bash -c` unexpanded.

## Plan and apply

**The apply creates an Entra application and its federated credential: it needs the owner's
explicit yes.** Plan, read the plan, then apply it:

```bash
terraform plan -out=tfplan
terraform apply tfplan
rm tfplan
```

The plan must add a resource group, a storage account, a container, a role assignment, a random
suffix, an Entra application, its service principal and one federated credential, and nothing
else. A saved plan holds resolved values, so it is git-ignored and deleted after use.

## Copy the outputs into GitHub

Every output is sensitive, so `apply` prints none of them. Read each one on purpose, and copy the
first three into the GitHub environment `live-eval` as **secrets**, never into a file:

| Output | Goes to |
|---|---|
| `ci_client_id` | environment secret `AZURE_CLIENT_ID` |
| `tenant_id` | environment secret `AZURE_TENANT_ID` |
| `subscription_id` | environment secret `AZURE_SUBSCRIPTION_ID` |
| `ci_principal_id` | `infra/foundry`'s `ci_principal_id`, in its git-ignored `terraform.tfvars` |
| `state_storage_account` | `infra/foundry`'s `terraform init -backend-config=storage_account_name=<value>` |

In PowerShell on Windows, from the repository root, the value goes straight from Terraform to
GitHub without being shown:

```powershell
wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw ci_client_id' | gh secret set AZURE_CLIENT_ID --env live-eval
```

and the same for `tenant_id` (`AZURE_TENANT_ID`) and `subscription_id` (`AZURE_SUBSCRIPTION_ID`).

## Destroy

Destroy `infra/foundry` first: this stack holds its state. Then:

```bash
terraform destroy
```

Destroying the storage account deletes `infra/foundry`'s state with it. The Entra application goes
to Entra's deleted items for 30 days; `az ad app list-deleted` shows it, and it can be restored or
removed there. The CI identity's secrets in the `live-eval` environment then point at nothing:
delete them too.

This stack creates no Foundry account. Purging a soft-deleted one is in
[`../foundry/README.md`](../foundry/README.md#destroy-and-purge).

## Tests

```bash
terraform init -backend=false -input=false
terraform validate
terraform test
```

The tests plan against mocked providers, and never sign in, apply or destroy anything. CI runs
them, with `terraform fmt -check`, on every pull request to `main`. The static checks over
`infra/` (no key output unmarked as sensitive, no identifier in a default or file, values files and
state git-ignored) are in `tests/Workshop.Agent.Tests/StaticInfraTests.cs`.
