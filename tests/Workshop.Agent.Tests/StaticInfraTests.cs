using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Workshop.Agent.Tests;

/// <summary>
/// Static checks over <c>infra/</c> (Review Focus 4): no key or connection string leaves an output
/// unmarked, no identifier sits in a committed default or file, keys stay off, and the values
/// files and state are git-ignored. They read files only; <c>terraform test</c> checks behaviour.
/// </summary>
public sealed partial class StaticInfraTests
{
    /// <summary>Built-in role definitions the stacks name by GUID: Foundry User, Monitoring Metrics Publisher, Storage Blob Data Contributor.</summary>
    private static readonly HashSet<string> RoleGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "53ca6127-db72-4b80-b1b0-d745d6d5456d",
        "3913510d-42f4-4e42-8a64-420c390055eb",
        "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
    };

    /// <summary>Variables that hold an identifier or the owner's details, so must never have a default.</summary>
    private static readonly string[] NoDefault =
    [
        "subscription_id",
        "github_repository",
        "owner_object_id",
        "ci_principal_id",
        "claude_provider_organization",
        "claude_provider_country_code",
        "claude_provider_industry",
    ];

    [Fact]
    public void Both_stacks_exist_with_their_tests()
    {
        foreach (var stack in new[] { "bootstrap", "foundry" })
        {
            var directory = Path.Combine(RepoPaths.Infra, stack);
            foreach (var file in new[] { "versions.tf", "variables.tf", "main.tf", "outputs.tf", "README.md", ".terraform.lock.hcl" })
            {
                Assert.True(File.Exists(Path.Combine(directory, file)), $"infra/{stack}/{file} is missing.");
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory, "tests"), "*.tftest.hcl"));
        }

        Assert.True(File.Exists(Path.Combine(RepoPaths.Infra, "foundry", "backend.tf")), "infra/foundry/backend.tf is missing.");
    }

    [Fact]
    public void Every_key_or_connection_string_output_is_sensitive()
    {
        var outputs = Blocks("output").ToList();
        Assert.NotEmpty(outputs);

        var unmarked = outputs.Where(o => LooksLikeAKey(o.Name, o.Body) && !IsSensitive(o.Body)).Select(o => $"{o.File}: output \"{o.Name}\"").ToList();

        Assert.True(unmarked.Count == 0, "Outputs that carry a key or connection string must be sensitive: " + string.Join(", ", unmarked));
    }

    [Theory]
    [InlineData("primary_key", "value = azurerm_cognitive_account.a.name")]
    [InlineData("storage_key", "value = var.x")]
    [InlineData("instrumentation_key", "value = var.x")]
    [InlineData("appinsights_connection_string", "value = var.x")]
    [InlineData("anything", "value = azurerm_cognitive_account.a.primary_access_key")]
    [InlineData("insights", "value = azurerm_application_insights.a.connection_string")]
    [InlineData("insights", "value = azurerm_application_insights.a.instrumentation_key")]
    public void The_key_check_flags(string name, string body) =>
        Assert.True(LooksLikeAKey(name, body) && !IsSensitive(body));

    [Theory]
    [InlineData("endpoint", "value = azurerm_cognitive_account.a.endpoint")]
    [InlineData("gpt_deployment", "value = azurerm_cognitive_deployment.a.name")]
    [InlineData("instrumentation_key", "value = var.x\n  sensitive = true")]
    public void The_key_check_passes(string name, string body) =>
        Assert.False(LooksLikeAKey(name, body) && !IsSensitive(body));

    [Fact]
    public void Identifier_outputs_are_sensitive()
    {
        string[] expected =
        [
            "ci_client_id", "ci_principal_id", "tenant_id", "subscription_id", "state_storage_account",
            "project_endpoint", "resource_endpoint", "appinsights_connection_string",
        ];
        var outputs = Blocks("output").ToDictionary(o => o.Name, o => o.Body);

        foreach (var name in expected)
        {
            Assert.True(outputs.TryGetValue(name, out var body), $"No output \"{name}\".");
            Assert.True(IsSensitive(body), $"Output \"{name}\" carries an identifier and must be sensitive.");
        }
    }

    [Fact]
    public void Foundry_outputs_map_one_to_one_onto_the_FWA_variables()
    {
        var outputs = Blocks("output").Where(o => o.File.Contains("foundry", StringComparison.Ordinal)).Select(o => o.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["appinsights_connection_string", "claude_deployment", "gpt_deployment", "project_endpoint", "resource_endpoint"], outputs);
    }

    [Fact]
    public void No_variable_default_holds_an_identifier()
    {
        var variables = Blocks("variable").ToList();
        Assert.NotEmpty(variables);

        foreach (var variable in variables)
        {
            var value = Default(variable.Body);
            if (NoDefault.Contains(variable.Name))
            {
                Assert.True(value is null, $"{variable.File}: variable \"{variable.Name}\" holds an identifier or the owner's details, and must have no default.");
            }
            else if (value is not null)
            {
                Assert.False(LooksLikeAnIdentifier(value), $"{variable.File}: variable \"{variable.Name}\" has an identifier-like default: it must be a placeholder or nothing.");
            }
        }

        // Every name in the list is a real variable, so a rename cannot quietly empty the check.
        Assert.All(NoDefault, name => Assert.Contains(variables, v => v.Name == name));
    }

    [Theory]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"/subscriptions/x/resourceGroups/rg\"")]
    [InlineData("\"https://fwa-a1b2c3.services.ai.azure.com/\"")]
    [InlineData("\"stfwastatea1b2c3.blob.core.windows.net\"")]
    [InlineData("\"someone@example.com\"")]
    [InlineData("\"fwa-a1b2c3\"")]
    [InlineData("\"stfwastate9z8y7x\"")]
    [InlineData("\"appi-fwa-0a1b2c\"")]
    public void The_identifier_check_flags(string value) => Assert.True(LooksLikeAnIdentifier(value));

    [Theory]
    [InlineData("\"eastus2\"")]
    [InlineData("50")]
    [InlineData("\"1\"")]
    [InlineData("\"fwa-workshop\"")]
    public void The_identifier_check_passes(string value) => Assert.False(LooksLikeAnIdentifier(value));

    [Fact]
    public void Every_guid_under_infra_is_a_built_in_role_or_a_placeholder()
    {
        var found = new List<string>();
        foreach (var file in InfraFiles("*"))
        {
            foreach (Match guid in Guid().Matches(File.ReadAllText(file)))
            {
                if (!RoleGuids.Contains(guid.Value) && !IsPlaceholder(guid.Value))
                {
                    found.Add($"{Relative(file)}: {guid.Value}");
                }
            }
        }

        Assert.True(found.Count == 0, "Only built-in role GUIDs and repeated-digit placeholders may appear under infra/: " + string.Join(", ", found));
    }

    [Fact]
    public void Keys_stay_off()
    {
        var foundry = File.ReadAllText(Path.Combine(RepoPaths.Infra, "foundry", "main.tf"));
        var bootstrap = File.ReadAllText(Path.Combine(RepoPaths.Infra, "bootstrap", "main.tf"));

        Assert.Matches(@"(?m)^\s*local_auth_enabled\s*=\s*false\s*$", foundry);
        Assert.Equal(2, Regex.Matches(foundry, @"(?m)^\s*local_authentication_enabled\s*=\s*false\s*$").Count);
        Assert.Matches(@"(?m)^\s*shared_access_key_enabled\s*=\s*false\s*$", bootstrap);
        foreach (var file in InfraFiles("*.tf"))
        {
            Assert.DoesNotMatch(@"(?m)^\s*(local_auth_enabled|local_authentication_enabled|shared_access_key_enabled)\s*=\s*true", File.ReadAllText(file));
        }
    }

    [Fact]
    public void Foundry_destroy_deletes_its_group_with_what_Application_Insights_made_outside_Terraform()
    {
        // Application Insights creates a smart-detection alert rule and an action group in its group,
        // which Terraform does not own; with the provider's default, destroy stops at the group.
        var foundry = File.ReadAllText(Path.Combine(RepoPaths.Infra, "foundry", "versions.tf"));
        var bootstrap = File.ReadAllText(Path.Combine(RepoPaths.Infra, "bootstrap", "versions.tf"));

        Assert.Matches(@"(?s)features\s*\{.*?resource_group\s*\{\s*prevent_deletion_if_contains_resources\s*=\s*false\s*\}", foundry);
        // The bootstrap's group holds the state account: it keeps the provider's guard.
        Assert.DoesNotMatch(@"prevent_deletion_if_contains_resources\s*=\s*false", bootstrap);
    }

    [Fact]
    public void Bootstrap_registers_every_namespace_both_stacks_need()
    {
        // A provider's configuration is beyond terraform test's reach, so the list is pinned here. The
        // Claude deployment goes through the Marketplace: SaaS and MarketplaceOrdering are among them.
        var bootstrap = File.ReadAllText(Path.Combine(RepoPaths.Infra, "bootstrap", "versions.tf"));
        var list = Regex.Match(bootstrap, @"(?s)resource_providers_to_register\s*=\s*\[(?<items>.*?)\]");

        Assert.True(list.Success, "bootstrap's azurerm provider must list resource_providers_to_register.");
        Assert.Equal(
            ["Microsoft.Storage", "Microsoft.CognitiveServices", "Microsoft.OperationalInsights", "Microsoft.Insights", "Microsoft.AlertsManagement", "Microsoft.SaaS", "Microsoft.MarketplaceOrdering"],
            Regex.Matches(list.Groups["items"].Value, @"""(?<name>[^""]+)""").Select(m => m.Groups["name"].Value));
        Assert.Matches(@"(?m)^\s*resource_provider_registrations\s*=\s*""none""\s*$", bootstrap);
    }

    [Fact]
    public void The_insights_connection_waits_for_the_deployments()
    {
        // Foundry answers 409 to concurrent changes on one account: the connection comes after Claude, which comes after GPT.
        var connection = Assert.Single(Blocks(Path.Combine(RepoPaths.Infra, "foundry", "main.tf"), "resource", "azapi_resource"), b => b.Name == "appinsights_connection");
        var claude = Assert.Single(Blocks(Path.Combine(RepoPaths.Infra, "foundry", "main.tf"), "resource", "azapi_resource"), b => b.Name == "claude");

        Assert.Matches(@"(?m)^\s*depends_on\s*=\s*\[\s*azapi_resource\.claude\s*\]", connection.Body);
        Assert.Matches(@"(?m)^\s*depends_on\s*=\s*\[\s*azurerm_cognitive_deployment\.gpt\s*\]", claude.Body);
    }

    [Fact]
    public void There_is_exactly_one_federated_credential_and_it_names_the_environment()
    {
        var credentials = InfraFiles("*.tf")
            .SelectMany(file => Blocks(file, "resource", "azuread_application_federated_identity_credential"))
            .ToList();

        var credential = Assert.Single(credentials);
        Assert.DoesNotMatch(@"(?m)^\s*(count|for_each)\s*=", credential.Body);
        Assert.Contains(":environment:live-eval", File.ReadAllText(Path.Combine(RepoPaths.Infra, "bootstrap", "main.tf")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("infra/bootstrap/terraform.tfvars")]
    [InlineData("infra/foundry/terraform.tfvars")]
    [InlineData("infra/foundry/prod.auto.tfvars")]
    [InlineData("infra/bootstrap/terraform.tfstate")]
    [InlineData("infra/bootstrap/terraform.tfstate.backup")]
    [InlineData("infra/foundry/tfplan")]
    [InlineData("infra/foundry/.terraform/terraform.tfstate")]
    [InlineData("infra/foundry/backend_override.tf")]
    public void Values_state_and_plans_are_git_ignored(string path)
    {
        var (code, _) = Git("check-ignore", "--no-index", "-q", path);

        Assert.True(code == 0, $"{path} must be git-ignored.");
    }

    [Fact]
    public void No_values_or_state_file_is_tracked()
    {
        var (code, output) = Git("ls-files", "--", "infra");
        Assert.Equal(0, code);

        var tracked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => Regex.IsMatch(f, @"\.tfvars(\.json)?$|\.tfstate|(^|/)tfplan$|\.tfplan$"))
            .ToList();

        Assert.True(tracked.Count == 0, "Tracked values or state files: " + string.Join(", ", tracked));
    }

    private sealed record Block(string File, string Name, string Body);

    private static IEnumerable<Block> Blocks(string kind) => InfraFiles("*.tf").SelectMany(file => Blocks(file, kind, null));

    /// <summary>
    /// The top-level blocks of <paramref name="kind"/> in a file: <c>output "name" { … }</c>, or, with
    /// <paramref name="type"/>, <c>resource "type" "name" { … }</c>. The body is found by matching braces
    /// outside strings and comments, which is all of HCL these files use.
    /// </summary>
    private static IEnumerable<Block> Blocks(string file, string kind, string? type)
    {
        var text = File.ReadAllText(file);
        var header = type is null
            ? new Regex($@"(?m)^{kind}\s+""(?<name>[^""]+)""\s*\{{")
            : new Regex($@"(?m)^{kind}\s+""{Regex.Escape(type)}""\s+""(?<name>[^""]+)""\s*\{{");

        foreach (Match match in header.Matches(text))
        {
            var start = match.Index + match.Length;
            yield return new Block(Relative(file), match.Groups["name"].Value, text[start..BodyEnd(text, start)]);
        }
    }

    private static int BodyEnd(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }
                    }

                    break;
                case '#':
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        throw new InvalidDataException("Unbalanced braces.");
    }

    /// <summary>The text of a variable's <c>default</c> to the end of its line, or null when it has none.</summary>
    private static string? Default(string body)
    {
        var match = Regex.Match(body, @"(?m)^\s*default\s*=\s*(?<value>.*)$");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static bool LooksLikeAKey(string name, string body) =>
        KeyName().IsMatch(name) || KeyReference().IsMatch(Regex.Match(body, @"(?m)^\s*value\s*=.*$").Value);

    private static bool IsSensitive(string body) => Regex.IsMatch(body, @"(?m)^\s*sensitive\s*=\s*true\s*$");

    private static bool LooksLikeAnIdentifier(string value) =>
        Guid().IsMatch(value)
        || value.Contains("/subscriptions/", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(value, @"\.(azure\.com|azure\.net|windows\.net|microsoft\.com|anthropic\.com)\b", RegexOptions.IgnoreCase)
        || Regex.IsMatch(value, @"[\w.+-]+@[\w-]+\.[\w.]+")
        || GeneratedName().IsMatch(value);

    /// <summary>A GUID made of one repeated digit, such as all zeros: a placeholder, not an identifier.</summary>
    private static bool IsPlaceholder(string guid) => guid.Replace("-", "", StringComparison.Ordinal).Distinct().Count() == 1;

    // Only the files git would publish: tracked ones, and new ones that aren't ignored. The owner's
    // git-ignored values files and local state hold real identifiers by design, and must never be
    // scanned into a test message.
    private static IEnumerable<string> InfraFiles(string pattern)
    {
        var (code, output) = Git("ls-files", "--cached", "--others", "--exclude-standard", "--", "infra");
        Assert.True(code == 0, "git ls-files failed under infra/.");
        var publishable = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => Path.GetFullPath(Path.Combine(RepoPaths.RepoRoot, f)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(RepoPaths.Infra, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".terraform"))
            .Where(f => publishable.Contains(Path.GetFullPath(f)));
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoPaths.RepoRoot, file).Replace('\\', '/');

    private static (int Code, string Output) Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = RepoPaths.RepoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = git.StandardOutput.ReadToEnd();
        git.StandardError.ReadToEnd();
        git.WaitForExit();
        return (git.ExitCode, output);
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    // primary_key, *_key, connection_string, instrumentation_key: by name.
    [GeneratedRegex(@"(^|_)key$|connection_string|instrumentation_key")]
    private static partial Regex KeyName();

    // An attribute that holds a key or connection string, read into an output's value.
    [GeneratedRegex(@"\.\w*(_key|connection_string)\b")]
    private static partial Regex KeyReference();

    // The shape of this repo's generated names: a prefix and a six-character random suffix with a digit in it.
    [GeneratedRegex(@"\b(fwa|stfwastate|log-fwa|appi-fwa)-?(?=[a-z0-9]*[0-9])[a-z0-9]{6}\b")]
    private static partial Regex GeneratedName();
}
