using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Workshop.Agent.Tests;

/// <summary>
/// The live evaluation workflow's security-relevant shape (Review Focus 5), read from
/// <c>.github/workflows/live-eval.yml</c> as YAML: it runs only by hand, only from <c>main</c>, only
/// in the protected <c>live-eval</c> environment (the only one the CI identity's federated credential
/// trusts), with the least permissions OIDC needs, every action pinned by commit, a frozen 1-pass run
/// on the GPT engine signed in before it, and the outputs scanned for secrets before they leave the
/// runner. It is never run here: these checks read the file.
/// </summary>
public sealed partial class WorkflowShapeTests
{
    private static readonly string WorkflowFile = Path.Combine(RepoPaths.RepoRoot, ".github", "workflows", "live-eval.yml");

    private static readonly Lazy<YamlMappingNode> Root = new(() =>
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(WorkflowFile));
        stream.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    });

    [Fact]
    public void It_runs_only_when_dispatched_by_hand()
    {
        // No push, pull_request, pull_request_target, schedule or workflow_run: nothing a pull request or a fork can start.
        var on = Get(Root.Value, "on");
        string[] triggers = on switch
        {
            YamlScalarNode scalar => [scalar.Value!],
            YamlSequenceNode sequence => sequence.Children.Select(Text).ToArray(),
            YamlMappingNode mapping => mapping.Children.Keys.Select(Text).ToArray(),
            _ => [],
        };

        Assert.Equal(["workflow_dispatch"], triggers);
    }

    [Fact]
    public void Every_job_runs_only_from_main_in_the_live_eval_environment_on_windows()
    {
        var jobs = Jobs().ToList();
        Assert.NotEmpty(jobs);

        foreach (var (name, job) in jobs)
        {
            Assert.True(job.Children.ContainsKey("if"), $"Job '{name}' has no if: it must run only from main.");
            Assert.Equal("github.ref == 'refs/heads/main'", Unwrap(Text(Get(job, "if"))));

            // The environment is what the federated credential's subject names, and what the owner protects to main.
            var environment = Get(job, "environment");
            Assert.Equal("live-eval", environment is YamlMappingNode named ? Text(Get(named, "name")) : Text(environment));

            Assert.Equal("windows-latest", Text(Get(job, "runs-on")));
        }
    }

    [Fact]
    public void Its_permissions_are_contents_read_and_id_token_write_only()
    {
        Assert.Equal(ExpectedPermissions, Permissions(Get(Root.Value, "permissions")));

        // A job may repeat them, never widen them.
        foreach (var (name, job) in Jobs())
        {
            if (job.Children.TryGetValue(new YamlScalarNode("permissions"), out var permissions))
            {
                Assert.True(ExpectedPermissions.SequenceEqual(Permissions(permissions)), $"Job '{name}' changes the permissions.");
            }
        }
    }

    [Fact]
    public void Every_job_has_a_45_minute_timeout_and_the_workflow_runs_one_at_a_time()
    {
        foreach (var (_, job) in Jobs())
        {
            Assert.Equal("45", Text(Get(job, "timeout-minutes")));
        }

        // The evaluation gives up polling at 30 minutes, inside the job's 45: a slow cloud evaluation
        // fails its step, and the scan and the upload still run, rather than the job timing out with
        // the spend and no outputs. eval/tests/test_scan.py pins the same default from Python.
        var runEval = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "eval", "fwa_eval", "run_eval.py"));
        var polling = EvalPollingLimit().Match(runEval);
        Assert.True(polling.Success, "No timeout_seconds default in eval/fwa_eval/run_eval.py.");
        Assert.Equal(30 * 60, double.Parse(polling.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        // One run at a time; a second waits rather than cancelling a paid run part-way.
        var concurrency = Assert.IsType<YamlMappingNode>(Get(Root.Value, "concurrency"));
        Assert.False(string.IsNullOrWhiteSpace(Text(Get(concurrency, "group"))));
        Assert.Equal("false", Text(Get(concurrency, "cancel-in-progress")));
    }

    [Fact]
    public void Every_action_is_pinned_to_a_full_commit_sha_with_its_version_in_a_comment()
    {
        var uses = Steps().Where(s => s.Children.ContainsKey("uses")).Select(s => Text(Get(s, "uses"))).ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.Matches(PinnedAction(), u));

        // The comment is not in the YAML model, so the lines are read for it.
        var lines = File.ReadAllLines(WorkflowFile).Where(l => UsesLine().IsMatch(l)).ToList();
        Assert.Equal(uses.Count, lines.Count);
        Assert.All(lines, l => Assert.Matches(PinnedLineWithVersion(), l));
    }

    [Fact]
    public void It_signs_in_with_oidc_through_the_environments_secrets_before_any_azure_step()
    {
        var steps = Steps().ToList();
        var login = Single(steps, s => Uses(s).StartsWith("azure/login@", StringComparison.OrdinalIgnoreCase), "azure/login");
        var with = Assert.IsType<YamlMappingNode>(Get(login, "with"));

        // OIDC: client, tenant and subscription only, each from a secret; never a client secret in creds.
        Assert.False(with.Children.ContainsKey("creds"), "azure/login must sign in through OIDC, not with creds.");
        Assert.Equal("${{ secrets.AZURE_CLIENT_ID }}", Text(Get(with, "client-id")));
        Assert.Equal("${{ secrets.AZURE_TENANT_ID }}", Text(Get(with, "tenant-id")));
        Assert.Equal("${{ secrets.AZURE_SUBSCRIPTION_ID }}", Text(Get(with, "subscription-id")));

        // Both the agent and the evaluation sign in through the Azure CLI, so the login comes first.
        var index = steps.IndexOf(login);
        Assert.True(index < steps.IndexOf(AgentRun(steps)), "azure/login must run before the agent.");
        Assert.True(index < steps.IndexOf(Evaluation(steps)), "azure/login must run before the evaluation.");
    }

    [Fact]
    public void It_runs_the_frozen_scenarios_on_the_gpt_engine_once()
    {
        var run = Text(Get(AgentRun(Steps().ToList()), "run"));

        Assert.Contains("--engine gpt", run, StringComparison.Ordinal);
        Assert.Contains("--frozen", run, StringComparison.Ordinal);
        Assert.Contains("--passes 1", run, StringComparison.Ordinal);
        Assert.Contains("--scenarios scenarios", run, StringComparison.Ordinal);

        // The study is 3 passes and runs locally per the runbook; a CI trace never carries message content.
        Assert.DoesNotContain("--study", run, StringComparison.Ordinal);
        Assert.DoesNotContain("--trace-content", run, StringComparison.Ordinal);
    }

    [Fact]
    public void It_scores_the_run_with_foundrys_evaluators_and_posts_the_report()
    {
        var steps = Steps().ToList();
        var evaluation = Text(Get(Evaluation(steps), "run"));

        Assert.Contains("python -m fwa_eval eval", evaluation, StringComparison.Ordinal);
        Assert.Contains("python -m fwa_eval report", evaluation, StringComparison.Ordinal);
        Assert.Contains(steps, s => s.Children.ContainsKey("run") && Text(Get(s, "run")).Contains("GITHUB_STEP_SUMMARY", StringComparison.Ordinal));
    }

    [Fact]
    public void The_outputs_are_scanned_for_secrets_before_the_summary_and_the_upload()
    {
        var steps = Steps().ToList();
        var scan = Scan(steps);
        Assert.Equal("scan", Text(Get(scan, "id")));

        // always(): a failed or timed-out earlier step cannot skip the scan. It fails closed on its own
        // (exit 2 when there is no output directory), and its failure skips the summary and the upload.
        Assert.Equal("always()", Unwrap(Text(Get(scan, "if"))));

        var summary = Single(steps, s => s.Children.ContainsKey("run") && Text(Get(s, "run")).Contains("GITHUB_STEP_SUMMARY", StringComparison.Ordinal), "the summary");
        var upload = Single(steps, s => Uses(s).StartsWith("actions/upload-artifact@", StringComparison.OrdinalIgnoreCase), "the upload");

        foreach (var (step, what) in new[] { (summary, "summary"), (upload, "upload") })
        {
            Assert.True(steps.IndexOf(scan) < steps.IndexOf(step), $"The scan must run before the {what}.");

            // No if means success(): a failed scan skips it. An explicit if must still require the scan's success.
            if (step.Children.ContainsKey("if"))
            {
                Assert.Contains("steps.scan.outcome == 'success'", Text(Get(step, "if")), StringComparison.Ordinal);
            }
        }

        Assert.True(File.Exists(Path.Combine(RepoPaths.RepoRoot, "eval", "fwa_eval", "scan.py")), "eval/fwa_eval/scan.py is missing.");
    }

    [Fact]
    public void Secrets_reach_only_the_steps_that_need_them()
    {
        // No workflow- or job-level env holds a secret, where every step (and every action) would see it.
        Assert.All(new[] { Root.Value }.Concat(Jobs().Select(j => j.Job)), node =>
        {
            if (node.Children.TryGetValue(new YamlScalarNode("env"), out var env))
            {
                Assert.DoesNotContain("secrets.", Serialize(env), StringComparison.Ordinal);
            }
        });

        // The checkout leaves no token in .git/config for later steps.
        var checkout = Single(Steps().ToList(), s => Uses(s).StartsWith("actions/checkout@", StringComparison.OrdinalIgnoreCase), "actions/checkout");
        Assert.Equal("false", Text(Get(Assert.IsType<YamlMappingNode>(Get(checkout, "with")), "persist-credentials")));
    }

    [Fact]
    public void No_run_script_interpolates_an_expression()
    {
        // A ${{ }} in a script is pasted into its text before it runs: a secret would sit in the script
        // file, and an attacker-controlled value would be code. Values reach scripts through env only.
        var scripts = Steps().Where(s => s.Children.ContainsKey("run")).Select(s => Text(Get(s, "run"))).ToList();
        Assert.NotEmpty(scripts);
        Assert.All(scripts, script => Assert.DoesNotContain("${{", script, StringComparison.Ordinal));
    }

    [Fact]
    public void The_first_step_after_checkout_masks_every_endpoints_host_and_resource_name()
    {
        // GitHub masks a secret's whole value in the log, never the host or resource name inside it,
        // which an Azure error can print; so they are masked before any step that could print them.
        var steps = Steps().ToList();
        Assert.StartsWith("actions/checkout@", Uses(steps[0]), StringComparison.OrdinalIgnoreCase);

        var mask = steps[1];
        var run = Text(Get(mask, "run"));
        Assert.Contains("python -m fwa_eval.scan --mask", run, StringComparison.Ordinal);
        Assert.Equal("eval", Text(Get(mask, "working-directory")));

        var endpointSecrets = SecretsUsed().Where(s => s.Contains("ENDPOINT", StringComparison.Ordinal) || s.Contains("CONNECTION_STRING", StringComparison.Ordinal))
            .Union(["FWA_PROJECT_ENDPOINT", "FWA_RESOURCE_ENDPOINT", "FWA_APPINSIGHTS_CONNECTION_STRING"]);
        AssertReadsFromEnv(mask, endpointSecrets);
    }

    [Fact]
    public void The_scan_looks_for_every_secret_the_job_uses()
    {
        // The deployment names are the exception: infra names each deployment after its public model,
        // every transcript records it, and infra/foundry marks those outputs not sensitive.
        string[] recordedByDesign = ["FWA_GPT_DEPLOYMENT", "FWA_CLAUDE_DEPLOYMENT"];
        var secrets = SecretsUsed().Except(recordedByDesign).ToList();
        Assert.Contains("AZURE_CLIENT_ID", secrets);

        AssertReadsFromEnv(Scan(Steps().ToList()), secrets);
    }

    /// <summary>The step names each secret with --literal-env and gets its value from its own env, as <c>${{ secrets.NAME }}</c>.</summary>
    private static void AssertReadsFromEnv(YamlMappingNode step, IEnumerable<string> secrets)
    {
        var listed = LiteralEnv().Matches(Text(Get(step, "run"))).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var env = Assert.IsType<YamlMappingNode>(Get(step, "env"));
        foreach (var secret in secrets)
        {
            Assert.True(listed.Contains(secret), $"The step does not pass --literal-env {secret}.");
            Assert.Equal($"${{{{ secrets.{secret} }}}}", Text(Get(env, secret)));
        }
    }

    /// <summary>Every secret the workflow names anywhere.</summary>
    private static HashSet<string> SecretsUsed() =>
        SecretReference().Matches(File.ReadAllText(WorkflowFile)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static YamlMappingNode Scan(List<YamlMappingNode> steps) =>
        Single(steps, s => s.Children.ContainsKey("run") && Text(Get(s, "run")) is var run
            && run.Contains("python -m fwa_eval.scan", StringComparison.Ordinal) && !run.Contains("--mask", StringComparison.Ordinal), "the secret scan");

    private static readonly string[] ExpectedPermissions = ["contents: read", "id-token: write"];

    private static string[] Permissions(YamlNode node) =>
        [.. Assert.IsType<YamlMappingNode>(node).Children.Select(p => $"{Text(p.Key)}: {Text(p.Value)}").Order(StringComparer.Ordinal)];

    private static IEnumerable<(string Name, YamlMappingNode Job)> Jobs() =>
        Assert.IsType<YamlMappingNode>(Get(Root.Value, "jobs")).Children.Select(j => (Text(j.Key), Assert.IsType<YamlMappingNode>(j.Value)));

    private static IEnumerable<YamlMappingNode> Steps() =>
        Jobs().SelectMany(j => Assert.IsType<YamlSequenceNode>(Get(j.Job, "steps")).Children.Select(s => Assert.IsType<YamlMappingNode>(s)));

    private static YamlMappingNode AgentRun(List<YamlMappingNode> steps) =>
        Single(steps, s => s.Children.ContainsKey("run") && Text(Get(s, "run")).Contains("--engine", StringComparison.Ordinal), "the agent run");

    private static YamlMappingNode Evaluation(List<YamlMappingNode> steps) =>
        Single(steps, s => s.Children.ContainsKey("run") && Text(Get(s, "run")).Contains("python -m fwa_eval eval", StringComparison.Ordinal), "the evaluation");

    private static YamlMappingNode Single(List<YamlMappingNode> steps, Func<YamlMappingNode, bool> match, string what)
    {
        var found = steps.Where(match).ToList();
        Assert.True(found.Count == 1, $"Expected one step for {what}, found {found.Count}.");
        return found[0];
    }

    private static string Uses(YamlMappingNode step) => step.Children.ContainsKey("uses") ? Text(Get(step, "uses")) : "";

    private static YamlNode Get(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : throw new Xunit.Sdk.XunitException($"No '{key}' in the workflow where one is required.");

    private static string Text(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value ?? "";

    /// <summary>An <c>if</c> may be written bare or as <c>${{ … }}</c>; both mean the same.</summary>
    private static string Unwrap(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.StartsWith("${{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)
            ? trimmed[3..^2].Trim()
            : trimmed;
    }

    private static string Serialize(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedAction();

    [GeneratedRegex(@"^\s*(-\s+)?uses:", RegexOptions.CultureInvariant)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^\s*(-\s+)?uses:\s+\S+@[0-9a-f]{40}\s+#\s+v\d+(\.\d+)*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedLineWithVersion();

    [GeneratedRegex(@"\bsecrets\.([A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();

    [GeneratedRegex(@"--literal-env\s+([A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralEnv();

    [GeneratedRegex(@"timeout_seconds:\s*float\s*=\s*([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EvalPollingLimit();
}
