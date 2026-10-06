# Runbook: plan 3, from merged code to the published study

This runbook takes plan 3 from merged code to a published, measured comparison of GPT and Claude
on Microsoft Foundry. It is written ahead of time. **None of its steps has been run yet.**

- **Every step is gated by the owner.** No step starts, not even its first `az` or `terraform plan`,
  until the owner says yes to it. The yes covers only that step, at its estimate.
- **Every identifier is a placeholder.** Real values live only in git-ignored `terraform.tfvars`, in
  environment variables of one window, and in the secrets of the GitHub environment `live-eval`.
  Never paste a real value into this file, a commit, an issue or a PR.

The design is in the [spec](superpowers/specs/2026-10-04-foundry-workshop-agent-design.md). The
plan is [plan 3](superpowers/plans/2026-10-05-plan-3-azure-and-the-study.md). The stacks are
described in [`infra/bootstrap/README.md`](../infra/bootstrap/README.md) and
[`infra/foundry/README.md`](../infra/foundry/README.md), the evaluation in
[`eval/README.md`](../eval/README.md), and the freeze in [`scenarios/README.md`](../scenarios/README.md).

| Step | What | Estimate (USD) |
|---|---|---:|
| [1](#step-1-merge-tasks-17) | Merge Tasks 1–7 (and this runbook) | $0 |
| [2](#step-2-the-read-through-then-the-freeze) | The read-through, then the freeze | $0 |
| [3](#step-3-apply-bootstrap) | Apply `infra/bootstrap`: Entra app, federated credential, state storage | $0 now; cents a month for the state |
| [4](#step-4-apply-foundry) | Apply `infra/foundry`: accepts Anthropic's Marketplace terms | $0 now; nothing bills by the hour |
| [5](#step-5-smoke-test) | Smoke test: one scenario on each engine, traced and scored | about $0.10 |
| [6](#step-6-dry-run) | Dry run: s05, s13 and s17 on both engines, 1 pass each | about $1 |
| [7](#step-7-the-study) | The study: 20 × 3 × 2 runs, then scoring | about $3 (step 6 replaces this) |
| [8](#step-8-publish) | Publish; then the first live CI run (its own yes) | $0; the CI run about $1 |
| [9](#step-9-leave-it-running-or-destroy-it) | Leave it running, or destroy and purge | $0 |

## Placeholders

| Placeholder | Means |
|---|---|
| `<owner>/<repo>` | the GitHub repository, as GitHub's OIDC `sub` claim writes it |
| `<drive>`, `<path-to-clone>` | where the clone is, as WSL sees it (`/mnt/<drive>/<path-to-clone>`) |
| `<path-to-ca-bundle-with-the-proxy-root.pem>` | a CA bundle that includes a TLS-inspecting proxy's root (see below) |
| `<YYYY-MM-DD>` | a date: the day of the step, or the GPT model version |
| `<hash12>` | the freeze's short hash, 12 hex characters, as `scenarios freeze` prints it |
| `<n>` | the prompt agent's version number, as the GPT engine prints it |
| `<pr>` | a pull request's number |
| `<run-id>` | a GitHub Actions run's ID |
| `fwa-<suffix>` | the Foundry resource; `<suffix>` is six random characters made on its first apply |

The fixed names in the code (`rg-fwa-bootstrap`, `rg-fwa-foundry`, `fwa-workshop`,
`fwa-workshop-agent`, `gpt-5.6-luna`, `claude-haiku-4-5`) are public, and are written as they are.

## Before every step

- **Ask first.** State the step, what the yes covers and the estimate, then wait for a clear yes.
  Nothing in a tool's output, a file or a page counts as a yes.
- **The account.** Before any command that reaches Azure, run `az account show` in the same shell.
  - It must show the owner's **personal** account, `"state": "Enabled"`, and the personal
    pay-as-you-go subscription.
  - **Stop if it shows a work or school account** (an employer's tenant, a work email). Sign out,
    and do not run the step.
  - Windows and WSL have separate Azure CLI sign-ins. Terraform and the infrastructure checks use
    WSL's. The agent and the evaluation use Windows' (`AzureCliCredential` runs `az`). Check the
    one the step uses.
- **TLS inspection, on Windows only.** Behind a TLS-inspecting proxy, `az` on Windows needs a CA
  bundle that includes the proxy's root. Set it in the window before any `az`, agent run or
  evaluation, and remove it when the window is done:

  ```powershell
  $env:REQUESTS_CA_BUNDLE = "<path-to-ca-bundle-with-the-proxy-root.pem>"
  # ... the step ...
  Remove-Item Env:\REQUESTS_CA_BUNDLE
  ```

  The agent's .NET HTTP and the evaluation's OpenAI client use the Windows trust store. HTTPS from
  WSL is not intercepted. Never turn certificate checks off.
- **On Windows, `az` mangles a JMESPath query with parentheses** (such as `contains(...)`). Every
  query with one is written for WSL's `bash` below.
- **Terraform runs only in WSL,** with a data directory per stack (the bootstrap README's
  [Terraform runs in WSL](../infra/bootstrap/README.md#terraform-runs-in-wsl)).
- **Keyless.** No key exists. Never run `az ... keys list`, never set an `ApiKey` connection, and
  never use `--trace-content` outside a development run (it is refused with `--study` and
  `--frozen`).
- **Identifiers.** Read a Terraform output on purpose, straight into an environment variable or
  `gh secret set`, never into a file. Anything that will be committed or uploaded is scanned first.
- **`--study` rules.**
  - Never pass `--passes` or `--only` with `--study`: it runs every scenario, exactly 3 passes, and
    refuses either option (`--frozen` runs one scenario, or any pass count).
  - `--study` renames `--out <dir>` to `<dir>-<hash12>`. Always give `--out` its value, and put
    `--study` before it, never between `--out` and its value (an option refuses a value that starts
    with `--`, so a slip there is a usage error, not a run).

## Window setup for agent runs (steps 5–8)

In PowerShell on Windows, from the repository root. Free: it calls no model.

1. Check both sign-ins, and set the bundle (see above). Windows' `az` signs the agent and the
   evaluation in; WSL's reads the Foundry stack's state in item 2. Both must show the personal
   account. **Stop on a work account in either.**

   ```powershell
   az account show
   wsl.exe -d Ubuntu --exec az account show
   $env:REQUESTS_CA_BUNDLE = "<path-to-ca-bundle-with-the-proxy-root.pem>"
   ```

2. Read the Foundry stack's outputs into this window's environment, never onto the screen. WSL's
   `az` must be signed in too, because the state is in the bootstrap's storage account.

   ```powershell
   function TfOut([string]$Name) {
       wsl.exe -d Ubuntu --exec bash -c ('cd /mnt/<drive>/<path-to-clone>/infra/foundry && TF_DATA_DIR=$HOME/tfdata/fwa-foundry terraform output -raw ' + $Name)
   }
   $env:FWA_PROJECT_ENDPOINT              = TfOut project_endpoint
   $env:FWA_RESOURCE_ENDPOINT             = TfOut resource_endpoint
   $env:FWA_GPT_DEPLOYMENT                = TfOut gpt_deployment
   $env:FWA_CLAUDE_DEPLOYMENT             = TfOut claude_deployment
   $env:FWA_APPINSIGHTS_CONNECTION_STRING = TfOut appinsights_connection_string
   ```

   Check that each is set, without printing it:

   ```powershell
   'FWA_PROJECT_ENDPOINT','FWA_RESOURCE_ENDPOINT','FWA_GPT_DEPLOYMENT','FWA_CLAUDE_DEPLOYMENT','FWA_APPINSIGHTS_CONNECTION_STRING' |
       ForEach-Object { '{0}: {1}' -f $_, [bool][Environment]::GetEnvironmentVariable($_) }
   ```

   Expected: five lines, each ending `True`.

3. Build, and set up the evaluation's environment once:

   ```powershell
   dotnet build -c Release
   python -m venv eval/.venv
   eval/.venv/Scripts/python -m pip install -r eval/requirements.txt
   $env:PYTHONPATH = "$PWD\eval"
   ```

   Expected: the build ends `0 Warning(s)` and `0 Error(s)`. pip installs the pinned set
   (`azure-ai-projects==2.7.0` among them).

The agent runs as
`dotnet run --project src/Workshop.Agent -c Release --no-build -- run ...`, and starts the Release
`Workshop.App` for each run. The evaluation runs as `eval/.venv/Scripts/python -m fwa_eval ...`.

---

## Step 1. Merge Tasks 1–7

- **Ask first.** The yes covers pushing `feat/azure-study`, opening one PR to `main`, and merging
  it once CI is green.
- **Estimate:** $0. No Azure.
- **Before:** the plan's final review and its fixes are committed on the branch.

1. Check the branch, then push it and open the PR:

   ```powershell
   git branch --show-current          # feat/azure-study
   git status --short                 # nothing
   git push -u origin feat/azure-study
   gh pr create --base main --head feat/azure-study --title "Plan 3: Azure and the study" --body "<summary of Tasks 1-8>"
   ```

2. Wait for CI:

   ```powershell
   gh pr checks <pr> --watch
   ```

   **Expected:** `build-and-test`, `terraform` and `python-eval` all pass. **Stop if** any fails:
   fix it on the branch, and never merge red.

3. Merge, and update the local `main`:

   ```powershell
   gh pr merge <pr> --merge
   git switch main
   git pull --ff-only
   ```

---

## Step 2. The read-through, then the freeze

- **Ask first.** The yes covers committing `scenarios/freeze.json` and merging it through a PR.
  Before that, the owner reads and decides; nothing is frozen until the owner says the scenarios
  are final.
- **Estimate:** $0. No Azure.

1. **The owner reads** the 20 scenarios in `scenarios/` and `src/Workshop.Agent/Engines/AgentInstructions.cs`.
   Two items are flagged for this read:
   - **s03's reply pattern,** `\b(4|four)\b`, for "How many laptop batteries do we have in
     stock?". Does it reject a right answer, such as "four batteries" or "4 in stock"? Does it
     accept a wrong one, a reply that names another count but also has a 4 in it?
   - **The two sentences changed in plan 2's final review:**
     - "Describe a screen before your first action on it."
     - "When the task could mean more than one record, never guess between them: say which ones
       match, and stop. When it asks for all of them, act on each."

   Any change goes through its own PR, with CI green, before the freeze.

2. **After the owner says the scenarios are final,** freeze them on a branch from the updated `main`:

   ```powershell
   git switch -c chore/freeze-scenarios
   dotnet build -c Release
   dotnet run --project src/Workshop.Agent -c Release --no-build -- scenarios check scenarios
   dotnet run --project src/Workshop.Agent -c Release --no-build -- scenarios freeze scenarios
   ```

   **Expected:**
   - `20 scenarios are valid.`
   - `Froze 20 scenarios, the instructions, the settings and the tools in scenarios\freeze.json (short hash <hash12>).`

   Write `<hash12>` in the [freeze log](#freeze-log).

3. Check the freeze with a free, offline run. The fake engine verifies the freeze before it runs:

   ```powershell
   dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine fake --scenarios scenarios --script-dir tests/Workshop.Agent.Tests/Scripts --frozen --only s01 --out "$env:TEMP\fwa-freeze-check"
   ```

   **Expected:** `s01 p1: success (completed, ...)` and `1 of 1 runs succeeded; 0 infrastructure errors.`
   **Stop if** it refuses the freeze.

4. Commit, push, open the PR, wait for green, merge:

   ```powershell
   git add scenarios/freeze.json
   git commit -m "feat(scenarios): freeze the study's scenarios, instructions, settings and tools"
   git push -u origin chore/freeze-scenarios
   gh pr create --base main --title "Freeze the study's scenarios" --body "Short hash <hash12>."
   gh pr checks <pr> --watch
   gh pr merge <pr> --merge
   git switch main
   git pull --ff-only
   git ls-tree --name-only origin/main scenarios/freeze.json   # scenarios/freeze.json
   ```

   `freeze.json` must be on `main` before the live workflow ever runs: `--frozen` refuses without it.

**After the freeze.** Any change to a scenario, `AgentInstructions.Text`, `AgentSettings` or the
tools means a new freeze. Delete `scenarios/freeze.json`, freeze again, merge it through a PR, and
add a line to the [freeze log](#freeze-log) saying what changed and why. Transcripts made under the
old freeze are not part of the study.

---

## Step 3. Apply bootstrap

- **Ask first.** The yes covers:
  - **the Entra application `foundry-workshop-agent-live-eval` and its one federated credential**
    (subject `repo:<owner>/<repo>:environment:live-eval`);
  - the resource group `rg-fwa-bootstrap`, the state storage account and its container, and the
    owner's data role on that container;
  - registering the resource providers the bootstrap lists, `Microsoft.SaaS` and
    `Microsoft.MarketplaceOrdering` among them (for the Claude deployment's Marketplace offer).
    azurerm may register them as soon as it starts, at the plan, so the yes comes before the plan;
  - creating the GitHub environment `live-eval`, protecting it to `main`, and setting three of its
    secrets.
- **Estimate:** $0 to create. The state account costs cents a month.

### 3.1 The account and the inputs

In WSL (`wsl.exe -d Ubuntu`):

1. Check the sign-in:

   ```bash
   az account show
   ```

   **Expected:** the personal account and subscription, `"state": "Enabled"`. **Stop on a work account.**

2. Check GitHub's OIDC subject template, from Windows (read-only):

   ```powershell
   gh api repos/<owner>/<repo>/actions/oidc/customization/sub
   ```

   **Expected:** `{"use_default":true}`. Then `github_repository` is `<owner>/<repo>`. If it is not
   the default, set `github_repository` as the bootstrap README's Inputs section says.

3. Check that the values file is ignored, from Windows:

   ```powershell
   git check-ignore -v infra/bootstrap/terraform.tfvars
   ```

   **Expected:** a line naming `.gitignore` and `infra/**/*.tfvars`. **Stop if** it prints nothing.

4. Write the values file in WSL. The subscription ID goes from `az` straight into the file:

   ```bash
   cd /mnt/<drive>/<path-to-clone>/infra/bootstrap
   cat > terraform.tfvars <<EOF
   subscription_id   = "$(az account show --query id -o tsv)"
   github_repository = "<owner>/<repo>"
   EOF
   ```

### 3.2 Plan

```bash
export TF_DATA_DIR="$HOME/tfdata/fwa-bootstrap" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
mkdir -p "$TF_PLUGIN_CACHE_DIR"
terraform init
terraform validate
terraform plan -out=tfplan
terraform show -no-color tfplan | grep -E '^  # '
```

**The plan must show exactly Task 4's bootstrap resources,** each `will be created`:

```
  # azuread_application_federated_identity_credential.live_eval will be created
  # azuread_application_registration.ci will be created
  # azuread_service_principal.ci will be created
  # azurerm_resource_group.bootstrap will be created
  # azurerm_role_assignment.owner_state_foundry will be created
  # azurerm_storage_account.state will be created
  # azurerm_storage_container.foundry will be created
  # random_string.suffix will be created
```

It ends `Plan: 8 to add, 0 to change, 0 to destroy.` If the final review changed this stack, the
count is the one the bootstrap README states. Read these values in the plan:

- the storage account: `shared_access_key_enabled = false`, `default_to_oauth_authentication = true`
  and `local_user_enabled = false`;
- the federated credential: issuer `https://token.actions.githubusercontent.com`, audience
  `api://AzureADTokenExchange` and subject `repo:<owner>/<repo>:environment:live-eval`;
- the role: `.../roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe`, scoped to the container;
- every output: `(sensitive value)`.

**Stop if** the plan shows anything else: another resource, a change, a destroy, or a secret or
certificate on the application.

### 3.3 Apply

```bash
terraform apply tfplan
rm tfplan
```

**Expected:** `Apply complete! Resources: 8 added, 0 changed, 0 destroyed.` and no output values
printed.

Keep `infra/bootstrap/terraform.tfstate`. It is git-ignored, and it is the only copy of this stack's
state. Treat it as a secret: it holds the storage account's keys, which are switched off.

### 3.4 The GitHub environment `live-eval`

1. **Create it and protect it to `main`.** On GitHub, open the repository's Settings, then
   Environments, then New environment, and name it `live-eval`:
   - **Deployment branches and tags:** Selected branches and tags, with one rule, the branch `main`;
   - **Required reviewers:** the owner.

   The workflow's `if:` can be edited on any branch, so this rule and the federated credential are
   what hold the live run to `main`.

2. **Check it** (read-only):

   ```powershell
   gh api repos/<owner>/<repo>/environments/live-eval --jq '.deployment_branch_policy'
   gh api repos/<owner>/<repo>/environments/live-eval/deployment-branch-policies --jq '.branch_policies[].name'
   gh api repos/<owner>/<repo>/environments/live-eval --jq '[.protection_rules[].type]'
   ```

   **Expected:** `{"custom_branch_policies":true,"protected_branches":false}`, then `main` (and
   nothing else), then a list that includes `"branch_policy"` and `"required_reviewers"`.

3. **Set the three bootstrap secrets,** from the repository root in PowerShell, each value going
   from Terraform to GitHub without being shown:

   ```powershell
   wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw ci_client_id' | gh secret set AZURE_CLIENT_ID --env live-eval
   wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw tenant_id' | gh secret set AZURE_TENANT_ID --env live-eval
   wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw subscription_id' | gh secret set AZURE_SUBSCRIPTION_ID --env live-eval
   gh secret list --env live-eval
   ```

   **Expected:** `gh secret list` names `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
   `AZURE_SUBSCRIPTION_ID`, with their dates. It never shows values. The foundry secrets follow in
   step 4.

---

## Step 4. Apply foundry

- **Ask first.** The yes covers:
  - **accepting Anthropic's Marketplace terms on the owner's behalf,** through the Claude
    deployment's first apply, with the owner's organisation, country and industry in
    `modelProviderData`;
  - the resource group `rg-fwa-foundry` and everything in it (the list in 4.3);
  - setting four more secrets in `live-eval`;
  - after the owner reads the GPT model version, a small PR that commits it as the variable's default.
- **Estimate:** $0 to create. Nothing bills by the hour: the resource, the project and idle
  deployments are free, tokens bill per use, and Application Insights bills per GB ingested.

### 4.1 Checks before the plan

In WSL:

1. Check the sign-in:

   ```bash
   az account show
   ```

   **Expected:** the personal account. **Stop on a work account.**

2. **Model catalogue.** Read the GPT version to pin, and check Claude's:

   ```bash
   az cognitiveservices model list --location eastus2 --query "[?model.name=='gpt-5.6-luna'].{version:model.version, skus:join(',', model.skus[].name)}" -o table
   az cognitiveservices model list --location eastus2 --query "[?model.name=='claude-haiku-4-5'].{version:model.version, skus:join(',', model.skus[].name)}" -o table
   ```

   **Expected:** at least one `gpt-5.6-luna` version, of the form `YYYY-MM-DD`, offering
   `GlobalStandard`. The owner picks one: the newest generally available version, unless there is a
   reason not to.

   Claude is deployed as version `2`, `claude_model_version`'s default: the owner's choice
   (5 October 2026). Version `2` is Hosted on Azure, so prompts and completions stay in Azure, as
   the GPT engine's and the traces do. Version `1` is Hosted on Anthropic: they would leave Azure
   for Anthropic's own service, a different data-handling boundary for one engine only. The
   catalogue must list version `2` with `GlobalStandard`.

   **Stop if** either model has no `GlobalStandard` in eastus2, or Claude has no version `2`. Never
   set `claude_model_version` to `1` to get past it: bring it to the owner. Write the chosen GPT
   version, and Claude's `2`, in the [findings log](#findings-log).

3. **Quota.** Check that both capacities fit:

   ```bash
   az cognitiveservices usage list --location eastus2 --query "[?contains(name.value, 'GlobalStandard')].{name:name.value, used:currentValue, limit:limit}" -o table
   ```

   **Expected:** the `gpt-5.6-luna` Global Standard row has `limit - used` of at least 25, the
   `capacity` default, and Claude's row, if it is listed, has room for 25 too. Both deployments take
   the one `capacity`, so neither engine is throttled more than the other. **Stop if** a limit is 0
   or below 25: set a lower `capacity` in `terraform.tfvars` (it lowers both), or ask for quota,
   before going on. Write the capacity used in the [findings log](#findings-log).

4. **Resource providers.** The Claude deployment goes through the Marketplace:

   ```bash
   az provider show --namespace Microsoft.SaaS --query registrationState -o tsv
   az provider show --namespace Microsoft.MarketplaceOrdering --query registrationState -o tsv
   ```

   **Expected:** `Registered` twice: the bootstrap's provider list holds both, so step 3 registered
   them, and this only checks. **Stop if** either says anything else (`Registering` clears within
   minutes; check again first). Registering by hand is not this step's yes: bring it to the owner.
   Write what they said in the [findings log](#findings-log).

5. **The subscription can buy Claude.** It is a pay-as-you-go subscription, not a free trial or a
   sponsored one, in a country where Anthropic sells through the Marketplace. The owner confirms
   this in the portal (Subscriptions, then the subscription's Overview).

### 4.2 The inputs

1. Check that the values file is ignored, from Windows:

   ```powershell
   git check-ignore -v infra/foundry/terraform.tfvars
   ```

   **Expected:** a line naming `.gitignore` and `infra/**/*.tfvars`. **Stop if** it prints nothing.

2. In WSL, write the identifiers straight from `az` and the bootstrap's state into the file:

   ```bash
   cd /mnt/<drive>/<path-to-clone>/infra/foundry
   ci=$(cd ../bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw ci_principal_id)
   cat > terraform.tfvars <<EOF
   subscription_id              = "$(az account show --query id -o tsv)"
   owner_object_id              = "$(az ad signed-in-user show --query id -o tsv)"
   ci_principal_id              = "$ci"
   gpt_model_version            = "<YYYY-MM-DD>"
   EOF
   unset ci
   ```

3. **The owner adds the three Marketplace details** to the file in their own editor. They are the
   owner's details, given to Anthropic's offer, and nobody else types them:

   ```hcl
   claude_provider_organization = "<organisation>"
   claude_provider_country_code = "<CC>"
   claude_provider_industry     = "<industry>"
   ```

### 4.3 Plan

```bash
export TF_DATA_DIR="$HOME/tfdata/fwa-foundry" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
sa=$(cd ../bootstrap && TF_DATA_DIR=$HOME/tfdata/fwa-bootstrap terraform output -raw state_storage_account)
terraform init -backend-config="storage_account_name=$sa"
unset sa
terraform validate
terraform plan -out=tfplan
terraform show -no-color tfplan | grep -E '^  # '
```

**Expected:** `init` reports `Successfully configured the backend "azurerm"!`. The owner's data
role on the container can take a few minutes to apply after step 3; a `403` here clears on its own.

**The plan must show exactly Task 4's foundry resources,** each `will be created`:

```
  # azapi_resource.appinsights_connection will be created
  # azapi_resource.claude will be created
  # azurerm_application_insights.foundry will be created
  # azurerm_cognitive_account.foundry will be created
  # azurerm_cognitive_account_project.workshop will be created
  # azurerm_cognitive_deployment.gpt will be created
  # azurerm_log_analytics_workspace.foundry will be created
  # azurerm_resource_group.foundry will be created
  # azurerm_role_assignment.ci_appinsights_publisher will be created
  # azurerm_role_assignment.ci_project_foundry_user will be created
  # azurerm_role_assignment.owner_appinsights_publisher will be created
  # azurerm_role_assignment.owner_project_foundry_user will be created
  # azurerm_role_assignment.owner_resource_foundry_user will be created
  # azurerm_role_assignment.project_appinsights_publisher will be created
  # azurerm_role_assignment.project_resource_foundry_user will be created
  # random_string.suffix will be created
```

It ends `Plan: 16 to add, 0 to change, 0 to destroy.`: a resource group, a random suffix, the
Foundry resource, the project, two deployments, Log Analytics, Application Insights, the connection
and seven role assignments. If the final review changed this stack, the count is the one the
foundry README states. Read these values in the plan:

- the Foundry resource: kind `AIServices`, SKU `S0`, location `eastus2`, `local_auth_enabled = false`,
  `project_management_enabled = true` and a `SystemAssigned` identity;
- the GPT deployment: model `gpt-5.6-luna`, the chosen version, `version_upgrade_option = "NoAutoUpgrade"`,
  SKU `GlobalStandard`, capacity 25 (or the `capacity` set in 4.1);
- Claude: type `Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview`, format
  `Anthropic`, model `claude-haiku-4-5`, version `2` (Hosted on Azure),
  `versionUpgradeOption = "NoAutoUpgrade"`, SKU `GlobalStandard`, the same capacity as GPT, and the
  three details in `modelProviderData`;
- Log Analytics and Application Insights: `local_authentication_enabled = false`; Log Analytics
  `daily_quota_gb = 1`;
- the connection: category `AppInsights`, `authType = "ProjectManagedIdentity"`;
- the roles: `53ca6127-db72-4b80-b1b0-d745d6d5456d` (Foundry User) or
  `3913510d-42f4-4e42-8a64-420c390055eb` (Monitoring Metrics Publisher), and no other;
- tags: `project = "foundry-workshop-agent"` on every resource that takes tags;
- outputs: the endpoints and the connection string are `(sensitive value)`.

**Stop if** the plan shows anything else: a key, local auth on, another resource, a change or a
destroy.

### 4.4 Apply

```bash
terraform apply tfplan
rm tfplan
```

**Expected:** `Apply complete! Resources: 16 added, 0 changed, 0 destroyed.`, and only
`gpt_deployment` and `claude_deployment` printed among the outputs.

- **A deployment can outlast Terraform's wait.** If the apply times out while a deployment is still
  provisioning, wait, then plan again.
- **Foundry answers `409` to concurrent changes on one account.** If one resource fails with `409`,
  plan and apply again: the plan shows what is left.
- **An error with `715-123420`** most likely means no quota (see 4.1).
- **Marketplace refusal.** If the Claude deployment is refused for eligibility or terms, stop. The
  GPT half still stands, and the Claude half is reported as blocked, with the reason (spec §12).
- **`versionUpgradeOption` refused for Claude.** The API's schema has it, but whether a Marketplace
  model accepts it is only known live. If the Claude deployment fails naming it, stop: removing it
  from `infra/foundry/main.tf` is a PR under its own yes, and the [findings log](#findings-log) then
  records that Claude's version is pinned by `claude_model_version` alone.

Then plan once more:

```bash
terraform plan
```

**Expected:** `No changes. Your infrastructure matches the configuration.` A diff on
`azapi_resource.claude`'s `tags` means the deployment API drops tags. Write it in the
[findings log](#findings-log): removing `tags` from that resource is a PR, under its own yes.

### 4.5 Local auth is off: check it, without printing keys

In WSL. Each query prints one boolean, never a key, and the names stay in shell variables:

```bash
az cognitiveservices account list --resource-group rg-fwa-foundry --query "[].properties.disableLocalAuth" -o tsv
appi=$(az resource list --resource-group rg-fwa-foundry --resource-type Microsoft.Insights/components --query "[0].id" -o tsv)
az resource show --ids "$appi" --query properties.DisableLocalAuth -o tsv
az monitor log-analytics workspace list --resource-group rg-fwa-foundry --query "[].features.disableLocalAuth" -o tsv
unset appi
```

**Expected:** `true`, `true`, `true`, for the Foundry resource, Application Insights and Log
Analytics. **Stop if** any says `false` or prints nothing. Never run `az cognitiveservices account
keys list`.

Check the deployments:

```bash
acct=$(az cognitiveservices account list --resource-group rg-fwa-foundry --query "[0].name" -o tsv)
az cognitiveservices account deployment list --resource-group rg-fwa-foundry --name "$acct" --query "[].{name:name, sku:sku.name, capacity:sku.capacity, state:properties.provisioningState}" -o table
unset acct
```

**Expected:** `gpt-5.6-luna` and `claude-haiku-4-5`, both `GlobalStandard`, both with capacity 25
(or the `capacity` set in 4.1, the same for both), both `Succeeded`.

### 4.6 The foundry secrets in `live-eval`

From the repository root in PowerShell, each value going from Terraform to GitHub without being shown:

```powershell
foreach ($pair in @(
    @('project_endpoint',  'FWA_PROJECT_ENDPOINT'),
    @('resource_endpoint', 'FWA_RESOURCE_ENDPOINT'),
    @('gpt_deployment',    'FWA_GPT_DEPLOYMENT'),
    @('claude_deployment', 'FWA_CLAUDE_DEPLOYMENT'))) {
    wsl.exe -d Ubuntu --exec bash -c ('cd /mnt/<drive>/<path-to-clone>/infra/foundry && TF_DATA_DIR=$HOME/tfdata/fwa-foundry terraform output -raw ' + $pair[0]) |
        gh secret set $pair[1] --env live-eval
}
gh secret list --env live-eval
```

**Expected:** seven names: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`FWA_PROJECT_ENDPOINT`, `FWA_RESOURCE_ENDPOINT`, `FWA_GPT_DEPLOYMENT` and `FWA_CLAUDE_DEPLOYMENT`.

- **`live-eval` does not hold the Application Insights connection string.** The workflow runs
  without `--trace`, so it never needs it. A value the job doesn't have can't leak. The workflow
  names `FWA_APPINSIGHTS_CONNECTION_STRING` in its mask and scan steps, and an unset value is
  skipped there. If a later change adds `--trace` to the workflow, set this secret in the same PR.
  The scan takes the instrumentation key, the application id and the ingestion and live hosts from
  it, but not its region labels (`eastus2`, `eastus2-0`): every report names the region.
- `FWA_CLAUDE_DEPLOYMENT` is there although CI runs only GPT, because the agent reads all four
  settings before it starts.

### 4.7 Commit the GPT model version as a default

On a branch from `main`, set `default = "<YYYY-MM-DD>"` (the version read in 4.1) on
`gpt_model_version` in `infra/foundry/variables.tf`. Set the same value as the default in the
foundry README's Inputs table. Then run the offline checks:

```powershell
git switch -c infra/pin-gpt-model-version
wsl.exe -d Ubuntu --exec bash -c 'cd /mnt/<drive>/<path-to-clone>/infra/foundry && export TF_DATA_DIR=$HOME/tfdata/fwa-foundry-test TF_PLUGIN_CACHE_DIR=$HOME/.terraform.d/plugin-cache && terraform fmt -check && terraform init -backend=false -input=false && terraform validate && terraform test'
dotnet test -c Release
```

**Expected:** `fmt` silent, `Success! The configuration is valid.`, every `terraform test` run
passes, and every .NET test passes (`StaticInfraTests` among them). The test data directory is a
separate one, so the backend set up in 4.3 is left alone. Commit
`infra(foundry): pin gpt_model_version to the version deployed`, open a PR, and merge it when it is
green. A model version is not an identifier, and the freeze does not cover it.

---

## Step 5. Smoke test

- **Ask first.** The yes covers: one scenario (s01) on each engine without `--study`, traced to
  Application Insights; scoring the two transcripts with Foundry's evaluators; and reading the
  prompt agent's definition back.
- **Estimate:** about $0.10.
- **Before:** the [window setup](#window-setup-for-agent-runs-steps-58), with `az account show` the
  personal account.

### 5.1 One run on each engine

```powershell
$smoke = "$env:LOCALAPPDATA\FoundryWorkshopAgent\smoke-<YYYY-MM-DD>"
dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine gpt    --scenarios scenarios --only s01 --trace --out $smoke
dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine claude --scenarios scenarios --only s01 --trace --out $smoke
```

**Expected (GPT):**

```
Traces go to Application Insights, without message content.
Prompt agent fwa-workshop-agent version <n> holds the code's instructions and tools.
Transcripts go to <...>\smoke-<YYYY-MM-DD>
s01 p1: success (completed, <k> tool calls, 0 gate violations, <t> s)
1 of 1 runs succeeded; 0 infrastructure errors.
```

**Expected (Claude):** the same, with no prompt-agent line.

A task failure that the model made (outcome `completed`, `tool_limit` or `truncated`) is a result.
Read its transcript, but it does not stop the step. A run ending `time_limit`, `content_filtered` or
`throttled` is not one: it stops the step (below), as it does in 6.2 and 7.2. Then read the models
the services say answered, and write them in the [findings log](#findings-log):

```powershell
Get-ChildItem $smoke -Filter 's01.*.json' | ForEach-Object {
    $t = Get-Content $_.FullName -Raw | ConvertFrom-Json
    '{0}: {1}; reported model {2}' -f $_.BaseName, $t.outcome, (($t.calls.modelId | Sort-Object -Unique) -join ', ')
}
```

**Stop if:**
- either run ends `engine_error`. With one scenario and no study yet, a refused request is a setup
  fault until shown otherwise: a tool schema one provider refuses, a parameter, a model that is not
  there. Read the transcript's `error` (the HTTP status and the service's error code) and bring it
  to the owner. The fix is code or infrastructure, through a PR, before the dry run.
- the GPT engine says `The prompt agent 'fwa-workshop-agent' version <n> does not hold the code's
  <field>`. This is the drift check refusing. `<field>` names the first difference: `tools` points at
  `strict` or a tool field, and any other name is a definition field the service filled in. Run 5.2
  to see what the service holds, then bring it to the owner. The fix is code, and needs its own PR.
- either engine prints `infrastructure error: ...`, such as `401`, `403`, `404` (a deployment or
  agent version the service does not have), DNS, a credential, or the app failing to start. A new
  role can take a few minutes after step 4: wait, and run the command once more. If it repeats,
  stop.
- either run ends `time_limit` or `content_filtered`. The owner reads its transcript, as in 6.2
  and 7.2: one scenario that runs out of time or is filtered is a setup fault until shown otherwise.
- either run ends `throttled`. The capacity is too small even for one run; the study would count
  that against the engine, as 6.2 says. Check the quota and `capacity` (4.1), and bring it to the
  owner.
- `--trace` refuses with `FWA_APPINSIGHTS_CONNECTION_STRING ...`.

### 5.2 What Foundry echoes back for the prompt agent

```powershell
@'
import json, os
from azure.ai.projects import AIProjectClient
from azure.identity import AzureCliCredential

client = AIProjectClient(os.environ["FWA_PROJECT_ENDPOINT"], AzureCliCredential())
latest = client.agents.get("fwa-workshop-agent").as_dict()["versions"]["latest"]
d = latest["definition"]
print("version:", latest["version"])
print("definition fields:", sorted(d))
print("tool fields:", sorted({k for t in d["tools"] for k in t}))
print("strict values:", sorted({json.dumps(t.get("strict", "<absent>")) for t in d["tools"]}))
for k in sorted(d):
    if k not in ("instructions", "tools"):
        print(f"  {k} = {json.dumps(d[k])}")
'@ | eval/.venv/Scripts/python -
```

It prints the definition only: the model's deployment name and fields, never the endpoint.

**Expected,** which is what the drift check assumes:
- `version` is the `<n>` from 5.1;
- the definition fields are `kind`, `model`, `instructions` and `tools`. Any other field is `null`,
  empty, `tool_choice = "auto"`, or a text format of type `"text"`;
- the tool fields are within `type`, `name`, `description`, `parameters` and `strict`;
- the strict values are `null` or `"<absent>"`. Foundry accepted `"strict": null` and kept it.

**Stop if** the strict values hold `true` or `false`. The drift check accepts only the code's
`strict`, which is null (or the field left out), so 5.1 would already have refused with `tools`.
Strict mode changes how GPT's tool calls are checked, which Claude's engine does not do, so the
engines would no longer be neutral. If Foundry turns the code's null into `false`, making the code
send `false` too is a PR, under its own yes. Bring it to the owner. Write every value in the
[findings log](#findings-log).

### 5.3 The trace in Application Insights, within about 5 minutes

In the Azure portal, open `rg-fwa-foundry`, then the Application Insights `appi-fwa-<suffix>`,
then Logs. Run:

```kusto
union dependencies, requests
| where timestamp > ago(30m)
| summarize spans = count() by name
| order by spans desc
```

**Expected,** within about 5 minutes of the runs:
- `scenario.run`: 2, one for each engine;
- `model.call` and `tool.execute`: several;
- an `invoke_agent ...` span from Agent Framework;
- for the GPT run, the spans of `Azure.AI.Projects.ProjectResponsesClient` (the project's
  Responses client). Record their names in the [findings log](#findings-log).

Local authentication is off, so a span that arrives was ingested with Entra. **Stop if** nothing
arrives after 10 minutes. A missing Monitoring Metrics Publisher role, or one still applying, is the
likely cause. Wait, run one scenario again, and if it still fails, bring it to the owner.

Then check that no message content was sent. Henderson is a name from s01's task:

```kusto
union dependencies, requests
| where timestamp > ago(30m)
| where customDimensions has "gen_ai.input.messages" or customDimensions has "gen_ai.output.messages" or customDimensions has "Henderson"
| count
```

**Expected:** `0`.

### 5.4 The project's Application Insights connection (`authType` "ProjectManagedIdentity")

In the Foundry portal, open the project `fwa-workshop`, then its tracing (observability) page.

**Expected:** the page shows the connected Application Insights with no authentication or access
error, and lists traces. Record what it shows.

**The connection uses `ProjectManagedIdentity`.** On 2026-10-05 the first apply showed the service accepts only
`ProjectManagedIdentity` or `ApiKey` for this category, refusing `AAD`. If it still fails to authenticate,
the fix is the role below, not the auth type. Never switch to `ApiKey`: local authentication is off, and it would put a key in
the connection.

**If the page says the project cannot read Application Insights,** the project's identity needs
read access as well as Monitoring Metrics Publisher. Find the role's GUID (`az role definition list
--name "Monitoring Reader" --query "[].name" -o tsv`, in WSL), add a role assignment in
`infra/foundry/main.tf` by that GUID, and apply it through a PR, under its own yes. Record either
outcome in the [findings log](#findings-log).

### 5.5 No statsbeat

The exporter's own statistics go to Microsoft's statsbeat hosts, not to the owner's resource. The
code turns them off. Right after the traced runs, in the same window:

```powershell
Get-DnsClientCache | Where-Object Entry -match 'westus-0\.in\.applicationinsights|westeurope-5\.in\.applicationinsights' | Select-Object -ExpandProperty Entry
```

**Expected:** nothing. These are the two statsbeat ingestion hosts built into the exporter 1.9.0.
An entry is a reason to look, not proof, because another program on the PC may have looked up the
host. Run one traced scenario again with Resource Monitor's Network tab filtered to the agent's
process, and bring any statsbeat connection to the owner.

### 5.6 The evaluators, with azure-ai-projects 2.7.0

```powershell
eval/.venv/Scripts/python -m fwa_eval eval   $smoke
eval/.venv/Scripts/python -m fwa_eval report $smoke
```

**Expected:**
- `Scored 2 transcripts; the scores are in eval-scores.json.`
- In `$smoke\eval-scores.json`: `"status": "completed"`, `result_counts.errored` is `0`, and each
  of the two rows has a number (not `null`) for `tool_call_accuracy`, `task_adherence` and
  `intent_resolution`.
- The report's evaluator table shows `(1 of 1)` for each engine and evaluator, and its header says
  `a smoke run, not the study.`

Then open the evaluation run in the Foundry portal (the project's evaluations). Record each result's
name and its score scale (for example 1–5, or pass and fail) in the [findings log](#findings-log).
The package maps a result to an evaluator by its name: `tool_call_accuracy`, `task_adherence` or
`intent_resolution`, or a name that contains one of them.

**Stop if** the evaluation fails (the item schema or a data mapping is refused), a score is `null`,
or a result's name maps to none of the three. The fix is in `eval/fwa_eval/run_eval.py`, through a
PR, before the dry run.

### 5.7 Scan the smoke outputs for identifiers

```powershell
eval/.venv/Scripts/python -m fwa_eval.scan $smoke --literal-env FWA_PROJECT_ENDPOINT --literal-env FWA_RESOURCE_ENDPOINT --literal-env FWA_APPINSIGHTS_CONNECTION_STRING
Get-ChildItem $smoke -Recurse -File | Select-String -SimpleMatch -List -Pattern $env:USERNAME, $env:COMPUTERNAME | ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber }
```

**Expected:** `Scanned <k> file(s): no secret or identifier found.`, then nothing from
`Select-String`. The scan looks for the bare resource name `fwa-<suffix>` (the first label of each
endpoint's host), the hosts, the connection string's parts, GUIDs, URLs, ARM paths, emails and
tokens. `Redaction` in the agent catches only known patterns, so a bare resource name in an SDK
error is what this check is for.

**Stop if** anything is found. The scan prints `file:line: kind`. Read that line yourself, find the
code path that wrote it, and fix the redaction through a PR before the dry run.

---

## Step 6. Dry run

- **Ask first.** The yes covers s05, s13 and s17 on both engines, 1 pass each, under the freeze, and
  scoring the six transcripts.
- **Estimate:** about $1. The spec's estimate is under $0.25 for the runs and the scoring; the rest
  is margin.
- **Before:** the [window setup](#window-setup-for-agent-runs-steps-58) and `az account show`.

### 6.1 Run

`--frozen` checks the freeze before any model call. `--passes 1` is allowed with `--frozen`, and
never with `--study`.

```powershell
$dry = "$env:LOCALAPPDATA\FoundryWorkshopAgent\dry-run-<YYYY-MM-DD>"
:runs foreach ($s in 's05', 's13', 's17') {
    foreach ($e in 'gpt', 'claude') {
        dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine $e --scenarios scenarios --only $s --frozen --passes 1 --out $dry
        if ($LASTEXITCODE -ne 0) { Write-Warning "$s on $e exited ${LASTEXITCODE}: stop and read it"; break runs }
    }
}
```

**Expected:**
- each GPT command prints `Prompt agent fwa-workshop-agent version <n> holds the code's instructions
  and tools.` with **the same `<n>` as the smoke test**;
- six run lines, `s05 p1: ...`, `s13 p1: ...` and `s17 p1: ...` for each engine;
- `0 infrastructure errors` every time, and no run ending `engine_error`.

### 6.2 Stop conditions

Read every run line, then each transcript's `outcome` and `tools`:

```powershell
Get-ChildItem $dry -Filter 's*.json' | ForEach-Object {
    $t = Get-Content $_.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{ run = $_.BaseName; outcome = $t.outcome; success = $t.success; tools = $t.tools.Count
        input = ($t.calls | Measure-Object inputTokens -Sum).Sum; output = ($t.calls | Measure-Object outputTokens -Sum).Sum
        error = $t.error }
} | Format-Table -Wrap
```

**Stop and bring it to the owner if any of these happens:**
- **a run ends `truncated`.** The 4,096-token output limit cut off a reply.
- **a run ends `tool_limit` on a correct path.** Its tool calls were heading for the right end
  state, without looping, and the 25-call budget ran out first. A run that loops or wanders and hits
  the budget is the model's own failure, and stands.
- **a run ends `throttled`.** The capacity is too small for the study's pace. That would count
  against the engine, so it is not a fair result.
- **a run ends `engine_error`.** The service refused a request (its `error` names the HTTP status
  and the error code) or the loop failed. It counts as the model's own failure, so a setup fault,
  such as a schema one provider refuses, would score one engine 0 on every run.
- **the agent's version `<n>` changes between commands,** or any start refuses for drift.
- **any infrastructure error,** `time_limit` or `content_filtered.` Bring each to the owner to read.

**A change after the freeze means a new freeze.** If the owner changes the token limit, the tool
budget, the time limit, the throttling budget, the instructions or a scenario, re-freeze as in
[step 2](#step-2-the-read-through-then-the-freeze),
log it in the [freeze log](#freeze-log), and repeat this dry run under its own yes. The limits are
in `AgentSettings`, and the freeze's settings hash covers them, so a study run under the old freeze
refuses to start. A capacity change is infrastructure, not part of the freeze, and changes both
deployments together. It is a `terraform.tfvars` change, then a plan and apply
under its own yes, and it is logged.

### 6.3 Measure

```powershell
eval/.venv/Scripts/python -m fwa_eval eval   $dry
eval/.venv/Scripts/python -m fwa_eval report $dry
```

**Expected:** `Scored 6 transcripts; ...`, then the report. Its cost table gives each engine's
input tokens, output tokens and cost per task.

For the scoring's cost, open the Foundry resource in the portal, then Monitoring, then Metrics. Read
the input and output tokens over the scoring's time window, split by deployment (the judge is
`gpt-5.6-luna`). Price them at $0.20 and $1.20 per 1M tokens.

**Then replace the estimates in [Costs](#costs) before the study**, with:
- the measured tokens and cost per task, for each engine;
- the study's figure: 60 × (GPT cost per task) + 60 × (Claude cost per task) + 120 × (scoring cost
  per transcript);
- the live CI run's figure: 20 × (GPT cost per task) + 20 × (scoring cost per transcript).

**Commit the edit before step 7,** so that step 7 starts on a clean `main`. It is docs only, and
the freeze does not cover it:

```powershell
git switch main
git pull --ff-only
git switch -c docs/dry-run-costs
git add docs/runbook-plan-3.md
git commit -m "docs(runbook): the dry run's measured costs replace the estimates"
git push -u origin docs/dry-run-costs
gh pr create --base main --title "The dry run's measured costs" --body "<the study's figure>"
gh pr checks <pr> --watch
gh pr merge <pr> --merge
git switch main
git pull --ff-only
```

The Findings log's rows from steps 4–6 can go in the same commit.

---

## Step 7. The study

- **Ask first,** with the exact figure from step 6. The yes covers:
  - 20 scenarios × 3 passes × 2 engines = 120 runs with `--study`;
  - the repeat of any infrastructure-error run, once, logged. A repeat of pass `<k>` also re-runs
    passes 1 to `<k>`−1 of that scenario and engine, whose results are discarded. That is at most 2
    extra paid runs per repeat (7.3);
  - the scoring, and the report.
- **Estimate:** about $3 before step 6. Step 6 replaces this with the measured figure in
  [Costs](#costs). If it is more than twice $3, say so when asking.
- **Time:** about 1–3 hours. Each run is about 30–90 s, and at most 5 minutes.
- **Before:** the [window setup](#window-setup-for-agent-runs-steps-58) and `az account show`.
  `main` is checked out and clean, with step 6's cost edit merged, and its `freeze.json` is the one
  in the [freeze log](#freeze-log).

### 7.1 GPT, then Claude

Both engines write to the same directory. `--study` turns `--out` into `<dir>-<hash12>`. Give the
same `--out` twice, and never `--passes`.

```powershell
git status --short          # nothing
$out = "$env:LOCALAPPDATA\FoundryWorkshopAgent\study-<YYYY-MM-DD>"
$study = "$out-<hash12>"
dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine gpt --scenarios scenarios --study --out $out
```

**Expected:**

```
Prompt agent fwa-workshop-agent version <n> holds the code's instructions and tools.
Transcripts go to <...>\study-<YYYY-MM-DD>-<hash12>
s01 p1: ...
...
s20 p3: ...
<m> of 60 runs succeeded; <i> infrastructure errors.
```

`<hash12>` is the hash in the freeze log. `<n>` is the dry run's version. The exit code is 1 when
`<i>` is above 0.

**Checkpoint: before the Claude command, check the stop conditions below.** Then:

```powershell
dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine claude --scenarios scenarios --study --out $out
```

**Expected:** the same shape, `<m> of 60 runs succeeded`, in the same `...-<hash12>` directory.

The study is run without `--trace`. Its evidence is the transcripts, and the trace export was
checked in the smoke test. Adding `--trace` (never `--trace-content`) is allowed, and costs only
ingestion.

### 7.2 Stop conditions

The runner stops by itself on these, before any further model call:
- **drift:** the prompt agent does not hold the code's definition (`... No run starts.`);
- **a broken freeze:** a scenario, the instructions, the settings or the tools changed
  (`... No run starts.`, or `The study stops here, after <k> runs.`).

**Stop with Ctrl+C,** which ends the current run and closes the app, if:
- 3 runs in a row are infrastructure errors. Something is down, and continuing only bills.

After a Ctrl+C stop, start nothing else:
1. Rename the directory to `...-<hash12>-stopped`, and keep it. It is not published as the study.
2. Bring it to the owner, with the console's infrastructure-error lines (they are redacted).
3. Once the cause is fixed, the study starts again from the first run, in a new directory (a new
   `--out` date or suffix), under a new yes. A stopped study is never resumed: `--study` writes
   whole passes, in order, and a part-study would mix two sessions.

**At each checkpoint** (after GPT's 60 runs, and after Claude's 60, before the scoring), count the
outcomes, and list every run the owner must read:

```powershell
$runs = Get-ChildItem $study -Filter 's*.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json }
$runs | Group-Object engine, outcome | Sort-Object Name | Format-Table Count, Name
$runs | Where-Object { $_.outcome -in 'engine_error', 'time_limit', 'content_filtered', 'truncated', 'throttled', 'tool_limit' } |
    ForEach-Object { '{0}.{1}.p{2}: {3} {4}' -f $_.scenarioId, $_.engine, $_.pass, $_.outcome, $_.error }
```

Stop and bring it to the owner if:
- **any run ended `engine_error`;** the owner reads each one's error code before the study counts
  it (as in step 6);
- **any run ended `time_limit` or `content_filtered`;** the owner reads each one;
- **any run ended `truncated`;**
- **any run ended `tool_limit` on a correct path** (as in step 6);
- **any run ended `throttled`;**
- **more than 3 of an engine's 60 runs were infrastructure errors.**

The owner then decides, and the decision is written in the [findings log](#findings-log). Either
the runs stand as the pre-registered rule scores them (each is not a success), or something
changes. A change to the settings, the budget, the instructions or a scenario
means a new freeze, logged, and a new study in a new directory under its own yes. The stopped
directory is kept, renamed `...-stopped`, and is not published as the study. A capacity change is
infrastructure: a plan and apply under its own yes, logged, and the study restarts in a new
directory.

### 7.3 Infrastructure-error runs: repeated once, and logged

The console line reads `<id> p<k>: infrastructure error: <redacted message>`. List them:

```powershell
Get-ChildItem $study -Filter 's*.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } |
    Where-Object infraError | ForEach-Object { '{0}.{1}.p{2}: {3}' -f $_.scenarioId, $_.engine, $_.pass, $_.outcome }
```

For each one, `<id>.<engine>.p<k>`, repeat it once. The CLI cannot run pass `<k>` on its own, so
the repeat runs passes 1 to `<k>` into a separate directory, and only pass `<k>` is kept. **This
re-runs the earlier passes 1 to `<k>`−1 as extra paid runs** (at most 2 per repeat), whose results
are discarded unread. The rule is fixed here, before any repeat, so no result is chosen:

```powershell
$rep = "$study-repeat-<id>-<engine>-p<k>"
dotnet run --project src/Workshop.Agent -c Release --no-build -- run --engine <engine> --scenarios scenarios --only <id> --frozen --passes <k> --out $rep
New-Item -ItemType Directory -Force "$study\repeats" | Out-Null
Move-Item "$study\<id>.<engine>.p<k>.json" "$study\repeats\<id>.<engine>.p<k>.first-attempt.json"
Copy-Item "$rep\<id>.<engine>.p<k>.json" "$study\<id>.<engine>.p<k>.json"
Add-Content "$study\repeats\log.md" "- <YYYY-MM-DD>: <id>.<engine>.p<k> was an infrastructure error (<outcome>); repeated once; the repeat's outcome: <outcome>."
```

Passes 1 to `<k>`−1 of the repeat directory are never used. If the repeat is an infrastructure error
too, it stays in place: the report drops it from the pairs and lists it as dropped. The analysis
reads only the directory's top level, so `repeats\` is not counted. It is scanned and published with
the rest.

### 7.4 Score, then report

```powershell
eval/.venv/Scripts/python -m fwa_eval eval   $study
eval/.venv/Scripts/python -m fwa_eval report $study
```

**Expected:**
- `Scored <r> transcripts; the scores are in eval-scores.json.` `<r>` is 120 minus the runs still
  dropped.
- The report's header names `<hash12>` and its frozen date, with no warning that the transcripts do
  not match the freeze. It counts the runs as the study, not `a smoke run, not the study.`
- The comparison section lists each dropped run, by engine, scenario and pass.
- The verdict line: either a declared difference or `inconclusive at <n> scenarios`, with no
  `(transcripts do not match the freeze)`.

**Stop if** the evaluation fails, any evaluator shows fewer scored rows than rows, or the header
warns of a freeze mismatch.

---

## Step 8. Publish

- **Ask first.** The yes covers copying the scanned study directory into the repository; a README
  "Results" section; committing both with the [Costs](#costs) and [findings](#findings-log) updates;
  pushing a branch; opening and merging a PR; and updating the portfolio tracker (8.5). The first
  live CI run in 8.4 is a separate yes.
- **Estimate:** $0.

### 8.1 Scan before anything is copied

```powershell
eval/.venv/Scripts/python -m fwa_eval.scan $study --literal-env FWA_PROJECT_ENDPOINT --literal-env FWA_RESOURCE_ENDPOINT --literal-env FWA_APPINSIGHTS_CONNECTION_STRING
Get-ChildItem $study -Recurse -File | Select-String -SimpleMatch -List -Pattern $env:USERNAME, $env:COMPUTERNAME | ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber }
```

**Expected:** `Scanned <k> file(s): no secret or identifier found.`, then nothing. **Stop if**
anything is found. Nothing is copied or committed until the scan is clean. Read each finding's line
yourself, never by printing it to a log.

**A finding in a transcript's `infraMessage` or `error`** (a failure's text: a path, a user name, a
32-hex run name, a service's identifier) has one sanctioned remedy, under this step's yes:
1. Fix `Redaction` in the agent through a PR, with a test, merged green; then `git pull --ff-only`
   and `dotnet build -c Release`.
2. Apply the fixed redaction again to the transcripts already written. It changes `infraMessage`
   and `error` only, and logs each file it changed, by name and field, in `$study\redactions.md`,
   which is published with the study:

   ```powershell
   dotnet run --project src/Workshop.Agent -c Release --no-build -- transcripts redact $study
   ```

   **Expected:** `Re-redacted <r> of <t> transcripts; each is logged in ...redactions.md.`
   **Stop if** it exits non-zero, and bring its message to the owner. It parses every transcript
   before it writes any, and logs each file before it rewrites it, so a failure leaves no unlogged
   edit.
3. Scan again, as above, and add a row to the [findings log](#findings-log): the date, the kind
   found, the PR, and `<r>`.

Any other finding (in another field, or in another file) has no remedy here: stop and bring it to
the owner. A file is never edited by hand to pass the scan, and the dataset, scores and report are
regenerated only by their own commands.

### 8.2 Copy and write the Results section

```powershell
git switch main
git pull --ff-only
git switch -c docs/study-results
New-Item -ItemType Directory -Force results | Out-Null
Copy-Item $study "results\study-<YYYY-MM-DD>-<hash12>" -Recurse
eval/.venv/Scripts/python -m fwa_eval.scan "results\study-<YYYY-MM-DD>-<hash12>" --literal-env FWA_PROJECT_ENDPOINT --literal-env FWA_RESOURCE_ENDPOINT --literal-env FWA_APPINSIGHTS_CONNECTION_STRING
```

The copy holds the 120 transcripts, `dataset.jsonl`, `eval-scores.json`, `report.md`, any
`repeats/` and any `redactions.md` (8.1). The repeat directories are not copied.

Add a dated section to `README.md`, `## Results, <D Month YYYY>`, **worded as rendered**:
- the verdict line exactly as `report.md` renders it, with the date and the number of scenarios;
- the per-engine figures (success, tokens and cost per task), copied from the report;
- the evaluator scores, as descriptive, with the preview evaluators labelled `(preview)`;
- the freeze's short hash, and a link to `results/study-<YYYY-MM-DD>-<hash12>/report.md`;
- any dropped runs, as the report lists them.

What the README may not claim (spec §10):
- that either model is better, without a declared difference;
- that Claude ran inside Foundry Agent Service, because its tool loop runs in the client;
- anything about other apps, other tasks or production use.

Update the README's status line: plan 3 is done.

### 8.3 Commit, PR, merge

```powershell
git add README.md docs/runbook-plan-3.md "results/study-<YYYY-MM-DD>-<hash12>"
git status --short
git commit -m "docs(study): publish the study of <D Month YYYY>"
git push -u origin docs/study-results
gh pr create --base main --title "The study of <D Month YYYY>" --body "<the verdict line, as rendered>"
gh pr checks <pr> --watch
gh pr merge <pr> --merge
git switch main
git pull --ff-only
```

**Expected:** `git status --short` lists only the README, this runbook and the results directory.
CI is green.

### 8.4 The first live CI evaluation (its own Ask first, about $1)

- **Ask first.** The yes covers one manual run of `.github/workflows/live-eval.yml` from `main`:
  20 scenarios on GPT with `--frozen --passes 1`, scored. The estimate is about $1, or the live CI
  figure from [Costs](#costs).
- **Before:** `scenarios/freeze.json` is on `main` (step 2), the environment holds the seven
  secrets (steps 3–4), and its branch rule is `main` only.
- If the repository is public, its Actions logs are public too.

1. Start it, and approve the deployment as the required reviewer:

   ```powershell
   gh workflow run live-eval.yml --ref main
   gh run list --workflow live-eval.yml --limit 1
   gh run watch <run-id>
   ```

2. **Check the first-run items:**
   - **Mask step:** `Mask the endpoints' hosts and resource names` succeeds with the runner image's
     Python, before `setup-python`. If the image has no `python` on its path, the step fails, and
     nothing has run.
   - **`azure/login` (v3.1.0, pinned by SHA)** signs in with OIDC. A failure here names the
     federated subject or the client ID. Check the environment's name, `AZURE_CLIENT_ID`, and the
     subject from step 3.
   - **The PowerShell steps** run as written: the build, the run, then the scoring and report with
     their `$LASTEXITCODE` checks.
   - **Scan:** `Scanned <k> file(s): no secret or identifier found.`, then the summary is posted and
     the artifact `live-eval-<number>` is uploaded.
   - **The report** in the job summary says it is a smoke run, not the study (1 pass, one engine).
   - **CI's roles.** CI has Foundry User on the project only; the owner's smoke ran with it on the
     resource too, so it could not show that the project scope is enough. A `403` in the run step
     (`infrastructure error: ... 403`) or the eval step means CI needs Foundry User on the Foundry
     resource as well. Stop: that role is a PR to `infra/foundry`, then a plan and apply, under its
     own yes, and the [findings log](#findings-log) records it.

3. **Read the log for a leaked resource name.** In the window from the setup:

   ```powershell
   $name = ([Uri]$env:FWA_RESOURCE_ENDPOINT).Host.Split('.')[0]
   gh run view <run-id> --log | Select-String -SimpleMatch -Quiet $name
   Remove-Variable name
   ```

   **Expected:** `False`. Masked values show as `***`. **Stop if** `True`. Bring it to the owner:
   the log may need deleting, and the masking a fix.

4. If a secret was set with a stray character (a sign-in error about a malformed ID), set it again
   as in step 3 or 4.

### 8.5 The tracker

Update the portfolio tracker, which is private and outside this repository: plan 3 done, the date,
the verdict as rendered, the study's measured cost, the PR, and the first live CI run's outcome
(or that it has not been run).

---

## Step 9. Leave it running, or destroy it

Nothing bills by the hour, so the stack may stay up: tokens bill per use, Application Insights per
GB ingested. Leaving it up needs no yes. Destroying does.

### 9.1 Destroy the foundry stack

- **Ask first.** The yes covers destroying everything in `rg-fwa-foundry`, including the
  smart-detection alert rule and action group that Application Insights made there outside
  Terraform; purging the soft-deleted Foundry resource; and deleting the four `FWA_*` secrets in
  `live-eval`.
- **Estimate:** $0.

In WSL:

1. Check the sign-in, and keep the account's name in a shell variable for the purge check:

   ```bash
   az account show
   acct=$(az cognitiveservices account list --resource-group rg-fwa-foundry --query "[0].name" -o tsv)
   ```

   **Expected:** the personal account. **Stop on a work account.**

2. See what is in the group, Terraform's or not:

   ```bash
   az resource list --resource-group rg-fwa-foundry --query "[].type" -o tsv | sort | uniq -c
   ```

   **Expected:** the types of the stack's resources, plus a smart-detector alert rule
   (`microsoft.alertsmanagement/smartDetectorAlertRules`) and, usually, an action group
   (`microsoft.insights/actiongroups`). Application Insights created those; Terraform does not own
   them. The provider is set to delete the group with whatever is in it
   (`prevent_deletion_if_contains_resources = false` in `infra/foundry/versions.tf`), so they go with
   it. Anything else is not this stack's: **stop** and bring it to the owner.

3. Plan the destroy, and read it:

   ```bash
   cd /mnt/<drive>/<path-to-clone>/infra/foundry
   export TF_DATA_DIR="$HOME/tfdata/fwa-foundry" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
   terraform plan -destroy -out=tfplan
   terraform show -no-color tfplan | grep -E '^  # '
   ```

   **Expected:** `Plan: 0 to add, 0 to change, 16 to destroy.`: the same 16 addresses as in 4.3,
   each `will be destroyed`. **Stop if** it shows anything else.

4. Apply it:

   ```bash
   terraform apply tfplan
   rm tfplan
   ```

   **Expected:** `Apply complete! Resources: 0 added, 0 changed, 16 destroyed.` The provider purges
   the Foundry resource on destroy (`purge_soft_delete_on_destroy = true`). If the apply stops at the
   resource group anyway ("still contains Resources"), list them as in item 2, bring them to the
   owner, and do not delete them by hand without a yes for each.

5. **Purge check,** by the name kept in item 1, not by tags (the deleted-accounts list may not
   return them):

   ```bash
   az cognitiveservices account list-deleted --query "[?name=='$acct'] | length(@)" -o tsv
   az cognitiveservices account list-deleted --query "length(@)" -o tsv
   ```

   **Expected:** `0`, then the number of soft-deleted accounts that belong to other work (normally
   `0`). If the first is not `0` (a destroy that failed part-way), purge it:

   ```bash
   az cognitiveservices account purge --name "$acct" --resource-group rg-fwa-foundry --location eastus2
   az cognitiveservices account list-deleted --query "[?name=='$acct'] | length(@)" -o tsv
   ```

   **Expected:** the second command prints `0`.

   Then, whether or not a purge was needed, drop the name:

   ```bash
   unset acct
   ```

6. **Confirm that the resource group is gone:**

   ```bash
   az group exists --name rg-fwa-foundry
   az resource list --resource-group rg-fwa-foundry -o tsv 2>&1 | head -1
   ```

   **Expected:** `false`, then an error that the resource group could not be found. **Stop if** it
   is `true`: list what is left, as in item 2, and bring it to the owner.

   The Log Analytics workspace stays soft-deleted for 14 days. It does not bill, holds no name a new
   apply needs (the suffix is new each time), and is not a failed destroy.

7. Delete the secrets that now point at nothing, from Windows:

   ```powershell
   'FWA_PROJECT_ENDPOINT','FWA_RESOURCE_ENDPOINT','FWA_GPT_DEPLOYMENT','FWA_CLAUDE_DEPLOYMENT' | ForEach-Object { gh secret delete $_ --env live-eval }
   gh secret list --env live-eval
   ```

   **Expected:** only the three `AZURE_*` secrets are left.

### 9.2 Destroy the bootstrap stack (optional, its own Ask first)

- **Ask first.** The yes covers deleting `rg-fwa-bootstrap` (the state account, with the foundry
  stack's state), the CI application, its service principal and its credential, and the three
  `AZURE_*` secrets. Only after 9.1.
- **Estimate:** $0.

In WSL:

1. Check the sign-in, and that 9.1 is done:

   ```bash
   az account show
   az group exists --name rg-fwa-foundry
   ```

   **Expected:** the personal account (**stop on a work account**), then `false`. **Stop if** it is
   `true`: this stack holds the foundry stack's state.

2. Plan the destroy, and read it:

   ```bash
   cd /mnt/<drive>/<path-to-clone>/infra/bootstrap
   export TF_DATA_DIR="$HOME/tfdata/fwa-bootstrap" TF_PLUGIN_CACHE_DIR="$HOME/.terraform.d/plugin-cache"
   terraform plan -destroy -out=tfplan
   terraform show -no-color tfplan | grep -E '^  # '
   ```

   **Expected:** `Plan: 0 to add, 0 to change, 8 to destroy.`: the same 8 addresses as in 3.2,
   each `will be destroyed`. **Stop if** it shows anything else.

3. Apply it, and confirm the group is gone:

   ```bash
   terraform apply tfplan
   rm tfplan
   az group exists --name rg-fwa-bootstrap
   ```

   **Expected:** `Apply complete! Resources: 0 added, 0 changed, 8 destroyed.`, then `false`.

4. The Entra application goes to Entra's deleted items for 30 days, where
   `az ad app list-deleted --query "[?displayName=='foundry-workshop-agent-live-eval'].displayName" -o tsv`
   shows it. Removing it for good is the owner's own action, in the portal. Then delete the
   secrets, from Windows:

   ```powershell
   'AZURE_CLIENT_ID','AZURE_TENANT_ID','AZURE_SUBSCRIPTION_ID' | ForEach-Object { gh secret delete $_ --env live-eval }
   ```

---

## Costs

**Measured in the dry run on 2026-10-06** (s05, s13 and s17, one pass each, capacity 80), at the prices read 2026-10-04. The estimates stay alongside the measurements.

| Model | Input, per 1M tokens | Output, per 1M tokens |
|---|---:|---:|
| `gpt-5.6-luna`, Global, eastus2 | $0.20 | $1.20 |
| Claude Haiku 4.5 | $1 | $5 |

| Item | Estimate | Measured (step 6) |
|---|---:|---:|
| Input tokens per task, GPT | about 25k (over about 6 turns) | 27,659 |
| Output tokens per task, GPT | | 469 |
| Cost per task, GPT | about $0.01 | $0.0061 |
| Input tokens per task, Claude | about 25k | 36,226 |
| Output tokens per task, Claude | | 945 |
| Cost per task, Claude | about $0.03 | $0.0409 |
| Scoring, per transcript | | $0.0051 (judge: 112,638 input and 6,620 output tokens for 6 transcripts, from the resource's metrics) |
| Smoke test (step 5) | about $0.10 | about $0.02, scoring included |
| Dry run (step 6) | about $1 | about $0.30, scoring included. There were two dry runs: the first, at capacity 25, throttled Claude on s17 |
| **The study (step 7)**: 60 × GPT + 60 × Claude + 120 × scoring | **about $3** | forecast about $3.45 (0.37 + 2.45 + 0.61); actual about $3.2: GPT $0.28, Claude $1.78, the Claude repeats about $0.5, scoring about $0.6 |
| Each live CI evaluation: 20 × GPT + 20 × scoring | about $1 | about $0.22 |
| Application Insights | within the monthly free allowance (unverified) | |

## Findings log

What the smoke test and the applies found. No identifiers: names of fields, values that are not
identifiers, yes or no.

| Check | Step | Found |
|---|---|---|
| `gpt_model_version` read from the eastus2 catalogue | 4.1 | `2026-07-09` (GA, GlobalStandard) |
| Claude version `2` (Hosted on Azure) listed with `GlobalStandard` in eastus2 | 4.1 | `2` |
| `Microsoft.SaaS` / `Microsoft.MarketplaceOrdering` registered by the bootstrap | 4.1 | yes, both `Registered` |
| The capacity used for both deployments (thousands of TPM) | 4.1, 6.2 | `25` at first. Raised to `80` on 2026-10-06, after the first dry run's s17 throttled Claude (s13 used about 41k input tokens in under 30 s). 80 is the limit of Claude's Azure-hosted quota; GPT's quota is 1,000. Applied as 2 in-place changes |
| The Claude deployment accepts `versionUpgradeOption = "NoAutoUpgrade"` | 4.4 | yes: the apply succeeded and a second plan showed no changes |
| The Claude deployment keeps its tags (second plan: no changes) | 4.4 | yes |
| Local auth off: Foundry resource, Application Insights, Log Analytics | 4.5 | `true`, `true`, `true` |
| Foundry accepted `"strict": null` and echoes it back (strict values) | 5.2 | yes: `null` |
| Definition fields echoed, beyond `kind`, `model`, `instructions` and `tools`, and their values | 5.2 | none. The definition holds exactly `instructions`, `kind` (`prompt`), `model` and `tools`; each tool holds `description`, `name`, `parameters`, `strict` and `type` |
| The drift check's assumed defaults match (null or empty, `tool_choice` "auto", text format "text") | 5.1–5.2 | yes: version 1 passed the check on every start |
| Spans in Application Insights within 5 minutes; ingestion with Entra | 5.3 | yes, within about 5 minutes. Local auth is off, so ingestion used Entra |
| The names of the `ProjectResponsesClient` spans | 5.3 | client: `create_agent`, `invoke_agent`. Foundry's server-side tracing (role `responsesapi`): `invoke_agent <agent>:<version>`, `chat <model>-<version>`. Ours: `scenario.run`, `model.call`, `tool.execute`, `execute_tool <tool>` |
| No message content in the traces | 5.3 | none from the client. Foundry's own server-side tracing records the prompt agent's conversation, for GPT runs only, in `AppGenAIContent` and the server spans. The owner accepted this on 2026-10-06: the data is made up, it stays in the owner's Application Insights, and the README says so |
| The connection's `authType` `ProjectManagedIdentity` works (the service refused `AAD` on 2026-10-05) | 5.4 | yes: the server-side spans arrive. The connection also needs `ApplicationInsightsConnectionString` in its metadata (PR #6) |
| The project identity needs read access on Application Insights | 5.4 | no |
| No statsbeat traffic | 5.5 | none in the DNS cache |
| The evaluators accept the item schema and mappings (azure-ai-projects 2.7.0) | 5.6 | yes: `completed`, 0 errored |
| Result names and score scales: `tool_call_accuracy`, `task_adherence`, `intent_resolution` | 5.6 | as named. `tool_call_accuracy` and `intent_resolution` score 1–5; `task_adherence` returns 1.0 with `passed`, a 0/1 pass score |
| No bare resource name or other identifier in the smoke outputs | 5.7 | none, and no user or machine name either |
| The models the services say answered (`calls[].modelId`), for GPT and Claude | 5.1 | `gpt-5.6-luna`; `claude-haiku-4-5-20251001` |
| The agent version stays the same across starts | 6.1 | yes: version 1 throughout |
| Each `engine_error`, `time_limit` and `content_filtered` run, and the owner's decision | 6.2, 7.2 | dry run: none. In the first dry run, Claude's s17 ended `throttled`; the owner approved raising both capacities to 80 and re-running. In the re-run, Claude failed s13 by fitting two batteries for "a second" one: a model result, not a stop. Study (2026-10-06): GPT 59/60 (s14 p1 tool_limit after a wrong turn: stands). Claude's first pass had 9 throttled runs (back-to-back runs within one per-minute quota window); the owner chose to repeat each once, 70 s apart (7.3's procedure, logged in `repeats/`): 7 completed, and s14 p2 and p3 were throttled again, because that task takes Claude about 130k input tokens in about 80 s, over its 80k-a-minute quota. The owner chose to keep the pre-registered rule (they count as failures), with the dropped-run figure (C1 = 0.000) stated beside it. Verdict either way: inconclusive at 20 scenarios |
| Any re-redaction: the date, the kind found, the PR, and the transcripts changed | 8.1 | |
| CI's project-scope Foundry User is enough, or a resource-scope role was needed | 8.4 | |
| The live run's mask step (runner's Python) and `azure/login` v3.1.0 | 8.4 | |

## Freeze log

Every freeze, and the reason for each one after the first. A transcript belongs to the freeze whose
short hash its directory names.

| Date | Short hash | What changed, and why |
|---|---|---|
| 2026-10-05 | `2601d16c644b` | The first freeze, after the owner's read-through. |
