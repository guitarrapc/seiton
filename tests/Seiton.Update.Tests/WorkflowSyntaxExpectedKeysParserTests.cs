using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowSyntaxExpectedKeysParser"/> heading extraction,
/// segment splitting, pipe expansion, parent-child map construction, and full parse output.
/// </summary>
public sealed class WorkflowSyntaxExpectedKeysParserTests
{
    // ExtractHeadings

    [Test]
    public async Task ExtractHeadings_ExtractsBacktickedHeadings()
    {
        var md = """
                 ## `name`

                 Some description.

                 ## `on`

                 More text.

                 ## `jobs.<job_id>.steps[*].run`
                 """;

        var headings = WorkflowSyntaxExpectedKeysParser.ExtractHeadings(md);

        await Assert.That(headings).Count().IsEqualTo(3);
        await Assert.That(headings[0]).IsEqualTo("name");
        await Assert.That(headings[1]).IsEqualTo("on");
        await Assert.That(headings[2]).IsEqualTo("jobs.<job_id>.steps[*].run");
    }

    [Test]
    public async Task ExtractHeadings_IgnoresNonBacktickedHeadings()
    {
        var md = """
                 ## About YAML syntax for workflows

                 ## `name`

                 ## How permissions are calculated

                 ### Example of `run-name`

                 ## Filter pattern cheat sheet
                 """;

        var headings = WorkflowSyntaxExpectedKeysParser.ExtractHeadings(md);

        await Assert.That(headings).Count().IsEqualTo(1);
        await Assert.That(headings[0]).IsEqualTo("name");
    }

    [Test]
    public async Task ExtractHeadings_HandlesWindowsLineEndings()
    {
        var md = "## `name`\r\n\r\n## `on`\r\n";

        var headings = WorkflowSyntaxExpectedKeysParser.ExtractHeadings(md);

        await Assert.That(headings).Count().IsEqualTo(2);
        await Assert.That(headings[0]).IsEqualTo("name");
        await Assert.That(headings[1]).IsEqualTo("on");
    }

    // SplitSegments

    [Test]
    public async Task SplitSegments_SimpleDotSeparated()
    {
        var segments = WorkflowSyntaxExpectedKeysParser.SplitSegments("jobs.<job_id>.container.image");

        await Assert.That(segments).Count().IsEqualTo(4);
        await Assert.That(segments[0]).IsEqualTo("jobs");
        await Assert.That(segments[1]).IsEqualTo("<job_id>");
        await Assert.That(segments[2]).IsEqualTo("container");
        await Assert.That(segments[3]).IsEqualTo("image");
    }

    [Test]
    public async Task SplitSegments_PreservesSquareBrackets()
    {
        var segments = WorkflowSyntaxExpectedKeysParser.SplitSegments("jobs.<job_id>.steps[*].id");

        await Assert.That(segments).Count().IsEqualTo(4);
        await Assert.That(segments[2]).IsEqualTo("steps[*]");
        await Assert.That(segments[3]).IsEqualTo("id");
    }

    [Test]
    public async Task SplitSegments_PreservesPipeSeparatedAngleBrackets()
    {
        var segments = WorkflowSyntaxExpectedKeysParser.SplitSegments(
            "on.<push|pull_request|pull_request_target>.<paths|paths-ignore>");

        await Assert.That(segments).Count().IsEqualTo(3);
        await Assert.That(segments[0]).IsEqualTo("on");
        await Assert.That(segments[1]).IsEqualTo("<push|pull_request|pull_request_target>");
        await Assert.That(segments[2]).IsEqualTo("<paths|paths-ignore>");
    }

    [Test]
    public async Task SplitSegments_SingleSegment()
    {
        var segments = WorkflowSyntaxExpectedKeysParser.SplitSegments("name");

        await Assert.That(segments).Count().IsEqualTo(1);
        await Assert.That(segments[0]).IsEqualTo("name");
    }

    // ExpandAlternatives

    [Test]
    public async Task ExpandAlternatives_NoPipes_SinglePath()
    {
        var segments = new List<string> { "on", "<event_name>", "types" };
        var expanded = WorkflowSyntaxExpectedKeysParser.ExpandAlternatives(segments);

        await Assert.That(expanded).Count().IsEqualTo(1);
        await Assert.That(expanded[0]).IsEquivalentTo(new List<string> { "on", "<event_name>", "types" });
    }

    [Test]
    public async Task ExpandAlternatives_OnePipeSegment_Expands()
    {
        var segments = new List<string> { "on", "push", "<branches|tags>" };
        var expanded = WorkflowSyntaxExpectedKeysParser.ExpandAlternatives(segments);

        await Assert.That(expanded).Count().IsEqualTo(2);
        await Assert.That(expanded[0]).IsEquivalentTo(new List<string> { "on", "push", "branches" });
        await Assert.That(expanded[1]).IsEquivalentTo(new List<string> { "on", "push", "tags" });
    }

    [Test]
    public async Task ExpandAlternatives_TwoPipeSegments_CartesianProduct()
    {
        var segments = new List<string> { "on", "<push|pull_request>", "<paths|paths-ignore>" };
        var expanded = WorkflowSyntaxExpectedKeysParser.ExpandAlternatives(segments);

        await Assert.That(expanded).Count().IsEqualTo(4);
        // push × paths
        await Assert.That(expanded[0]).IsEquivalentTo(new List<string> { "on", "push", "paths" });
        // push × paths-ignore
        await Assert.That(expanded[1]).IsEquivalentTo(new List<string> { "on", "push", "paths-ignore" });
        // pull_request × paths
        await Assert.That(expanded[2]).IsEquivalentTo(new List<string> { "on", "pull_request", "paths" });
        // pull_request × paths-ignore
        await Assert.That(expanded[3]).IsEquivalentTo(new List<string> { "on", "pull_request", "paths-ignore" });
    }

    // BuildParentChildMap

    [Test]
    public async Task BuildParentChildMap_SingleSegmentHeading_RegistersUnderRoot()
    {
        var headings = new List<string> { "name", "on", "env" };
        var map = WorkflowSyntaxExpectedKeysParser.BuildParentChildMap(headings);

        await Assert.That(map.ContainsKey("(root)")).IsTrue();
        await Assert.That(map["(root)"]).Contains("name");
        await Assert.That(map["(root)"]).Contains("on");
        await Assert.That(map["(root)"]).Contains("env");
    }

    [Test]
    public async Task BuildParentChildMap_SkipsSingleParameterChildren()
    {
        var headings = new List<string> { "jobs", "jobs.<job_id>", "jobs.<job_id>.name" };
        var map = WorkflowSyntaxExpectedKeysParser.BuildParentChildMap(headings);

        // <job_id> is a parameter → not registered as concrete child of "jobs"
        await Assert.That(map.ContainsKey("jobs")).IsFalse();
        // "name" is registered as child of "jobs.<job_id>"
        await Assert.That(map.ContainsKey("jobs.<job_id>")).IsTrue();
        await Assert.That(map["jobs.<job_id>"]).Contains("name");
    }

    [Test]
    public async Task BuildParentChildMap_ExpandsPipeAlternatives()
    {
        var headings = new List<string>
        {
            "on.<push|pull_request>.<paths|paths-ignore>",
        };
        var map = WorkflowSyntaxExpectedKeysParser.BuildParentChildMap(headings);

        // "push" and "pull_request" are registered as children of "on"
        await Assert.That(map["on"]).Contains("push");
        await Assert.That(map["on"]).Contains("pull_request");

        // "paths" and "paths-ignore" are registered under both expanded parents
        await Assert.That(map["on.push"]).Contains("paths");
        await Assert.That(map["on.push"]).Contains("paths-ignore");
        await Assert.That(map["on.pull_request"]).Contains("paths");
        await Assert.That(map["on.pull_request"]).Contains("paths-ignore");
    }

    [Test]
    public async Task BuildParentChildMap_StripsArraySubscriptFromChildKey()
    {
        var headings = new List<string>
        {
            "jobs.<job_id>.steps[*].id",
            "jobs.<job_id>.steps[*].name",
        };
        var map = WorkflowSyntaxExpectedKeysParser.BuildParentChildMap(headings);

        // "steps" (stripped from "steps[*]") is a child of "jobs.<job_id>"
        await Assert.That(map["jobs.<job_id>"]).Contains("steps");

        // "id" and "name" are children of "jobs.<job_id>.steps[*]"
        await Assert.That(map["jobs.<job_id>.steps[*]"]).Contains("id");
        await Assert.That(map["jobs.<job_id>.steps[*]"]).Contains("name");
    }

    [Test]
    public async Task BuildParentChildMap_WildcardParentKeepsParameterInPath()
    {
        var headings = new List<string>
        {
            "on.<event_name>.types",
        };
        var map = WorkflowSyntaxExpectedKeysParser.BuildParentChildMap(headings);

        // "types" is a child of "on.<event_name>" (parameter is kept in parent path)
        await Assert.That(map.ContainsKey("on.<event_name>")).IsTrue();
        await Assert.That(map["on.<event_name>"]).Contains("types");

        // <event_name> is NOT registered as a concrete child of "on"
        var onChildren = map.GetValueOrDefault("on");
        if (onChildren is not null)
        {
            await Assert.That(onChildren).DoesNotContain("<event_name>");
        }
    }

    // NormalizeSectionName

    [Test]
    public async Task NormalizeSectionName_KnownPaths()
    {
        await Assert.That(WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("(root)")).IsEqualTo("workflow");
        await Assert.That(WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("jobs.<job_id>")).IsEqualTo("job");
        await Assert.That(WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("jobs.<job_id>.container")).IsEqualTo("container");
        await Assert.That(WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("jobs.<job_id>.steps[*]")).IsEqualTo("step");
        await Assert.That(WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("on.<event_name>")).IsEqualTo("on-event");
    }

    [Test]
    public async Task NormalizeSectionName_FallbackGeneration()
    {
        // Unknown paths should get algorithmic names
        var name = WorkflowSyntaxExpectedKeysParser.NormalizeSectionName("some.unknown.path");
        await Assert.That(name).IsEqualTo("some-unknown-path");
    }

    // Full Parse integration

    [Test]
    public async Task Parse_SyntheticMarkdown_ExtractsExpectedSections()
    {
        var md = """
                 ## `name`

                 ## `on`

                 ## `on.schedule`

                 ## `on.workflow_call`

                 ## `on.workflow_call.inputs`

                 ## `on.workflow_call.inputs.<input_id>.type`

                 ## `jobs`

                 ## `jobs.<job_id>`

                 ## `jobs.<job_id>.name`

                 ## `jobs.<job_id>.runs-on`

                 ## `jobs.<job_id>.steps`

                 ## `jobs.<job_id>.steps[*].id`

                 ## `jobs.<job_id>.steps[*].run`

                 ## `jobs.<job_id>.steps[*].uses`

                 ## `jobs.<job_id>.container`

                 ## `jobs.<job_id>.container.image`

                 ## `jobs.<job_id>.container.env`
                 """;

        var parser = new WorkflowSyntaxExpectedKeysParser();
        var model = parser.Parse(md);

        // Check workflow (root) section
        var workflow = model.Sections.FirstOrDefault(s => s.Name == "workflow");
        await Assert.That(workflow).IsNotNull();
        await Assert.That(workflow!.Keys).Contains("name");
        await Assert.That(workflow.Keys).Contains("on");
        await Assert.That(workflow.Keys).Contains("jobs");

        // Check on section
        var on = model.Sections.FirstOrDefault(s => s.Name == "on");
        await Assert.That(on).IsNotNull();
        await Assert.That(on!.Keys).Contains("schedule");
        await Assert.That(on.Keys).Contains("workflow_call");

        // Check job section
        var job = model.Sections.FirstOrDefault(s => s.Name == "job");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Keys).Contains("name");
        await Assert.That(job.Keys).Contains("runs-on");
        await Assert.That(job.Keys).Contains("steps");
        await Assert.That(job.Keys).Contains("container");

        // Check step section (from steps[*] children)
        var step = model.Sections.FirstOrDefault(s => s.Name == "step");
        await Assert.That(step).IsNotNull();
        await Assert.That(step!.Keys).Contains("id");
        await Assert.That(step.Keys).Contains("run");
        await Assert.That(step.Keys).Contains("uses");

        // Check derived action-step (step minus run-only)
        var actionStep = model.Sections.FirstOrDefault(s => s.Name == "action-step");
        await Assert.That(actionStep).IsNotNull();
        await Assert.That(actionStep!.Keys).Contains("id");
        await Assert.That(actionStep.Keys).Contains("uses");
        await Assert.That(actionStep.Keys).DoesNotContain("run");

        // Check derived run-step (step minus action-only)
        var runStep = model.Sections.FirstOrDefault(s => s.Name == "run-step");
        await Assert.That(runStep).IsNotNull();
        await Assert.That(runStep!.Keys).Contains("id");
        await Assert.That(runStep.Keys).Contains("run");
        await Assert.That(runStep.Keys).DoesNotContain("uses");

        // Check container section
        var container = model.Sections.FirstOrDefault(s => s.Name == "container");
        await Assert.That(container).IsNotNull();
        await Assert.That(container!.Keys).Contains("image");
        await Assert.That(container.Keys).Contains("env");

        // Check supplemented credentials section (no heading children → supplement)
        var credentials = model.Sections.FirstOrDefault(s => s.Name == "credentials");
        await Assert.That(credentials).IsNotNull();
        await Assert.That(credentials!.Keys).Contains("password");
        await Assert.That(credentials.Keys).Contains("username");

        // Check supplemented runs-on section
        var runsOn = model.Sections.FirstOrDefault(s => s.Name == "runs-on");
        await Assert.That(runsOn).IsNotNull();
        await Assert.That(runsOn!.Keys).Contains("group");
        await Assert.That(runsOn.Keys).Contains("labels");
    }

    [Test]
    public async Task Parse_SyntheticMarkdown_PipeExpansionCreatesEventSections()
    {
        var md = """
                 ## `on`

                 ## `on.<event_name>.types`

                 ## `on.<push|pull_request>.<paths|paths-ignore>`

                 ## `on.push.<branches|tags>`
                 """;

        var parser = new WorkflowSyntaxExpectedKeysParser();
        var model = parser.Parse(md);

        // on-event section (wildcard)
        var onEvent = model.Sections.FirstOrDefault(s => s.Name == "on-event");
        await Assert.That(onEvent).IsNotNull();
        await Assert.That(onEvent!.Keys).Contains("types");

        // on section gets event names from pipe expansion
        var on = model.Sections.FirstOrDefault(s => s.Name == "on");
        await Assert.That(on).IsNotNull();
        await Assert.That(on!.Keys).Contains("push");
        await Assert.That(on.Keys).Contains("pull_request");

        // on-push section
        var onPush = model.Sections.FirstOrDefault(s => s.Name == "on-push");
        await Assert.That(onPush).IsNotNull();
        await Assert.That(onPush!.Keys).Contains("branches");
        await Assert.That(onPush.Keys).Contains("tags");
        await Assert.That(onPush.Keys).Contains("paths");
        await Assert.That(onPush.Keys).Contains("paths-ignore");

        // on.pull_request gets paths from pipe expansion
        var onPr = model.Sections.FirstOrDefault(s => s.Name == "on-pull-request");
        await Assert.That(onPr).IsNotNull();
        await Assert.That(onPr!.Keys).Contains("paths");
        await Assert.That(onPr.Keys).Contains("paths-ignore");
    }

    // Contract test against committed raw markdown

    [Test]
    public async Task Parse_CommittedRawMarkdown_ExtractsComprehensiveSections()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "expected-keys", "github", "raw",
            "workflow-syntax.md");
        var markdown = File.ReadAllText(path);
        var parser = new WorkflowSyntaxExpectedKeysParser();
        var model = parser.Parse(markdown);

        // Should have a comprehensive number of sections
        await Assert.That(model.Sections.Count).IsGreaterThanOrEqualTo(25);

        // Verify key sections exist with expected minimum key counts
        var workflow = model.Sections.First(s => s.Name == "workflow");
        await Assert.That(workflow.Keys.Count).IsGreaterThanOrEqualTo(7);
        await Assert.That(workflow.Keys).Contains("name");
        await Assert.That(workflow.Keys).Contains("on");
        await Assert.That(workflow.Keys).Contains("jobs");
        await Assert.That(workflow.Keys).Contains("env");
        await Assert.That(workflow.Keys).Contains("permissions");

        var job = model.Sections.First(s => s.Name == "job");
        await Assert.That(job.Keys.Count).IsGreaterThanOrEqualTo(15);
        await Assert.That(job.Keys).Contains("name");
        await Assert.That(job.Keys).Contains("needs");
        await Assert.That(job.Keys).Contains("runs-on");
        await Assert.That(job.Keys).Contains("steps");
        await Assert.That(job.Keys).Contains("strategy");
        await Assert.That(job.Keys).Contains("container");
        await Assert.That(job.Keys).Contains("services");
        await Assert.That(job.Keys).Contains("uses");

        var step = model.Sections.First(s => s.Name == "step");
        await Assert.That(step.Keys.Count).IsGreaterThanOrEqualTo(10);
        await Assert.That(step.Keys).Contains("id");
        await Assert.That(step.Keys).Contains("if");
        await Assert.That(step.Keys).Contains("name");
        await Assert.That(step.Keys).Contains("run");
        await Assert.That(step.Keys).Contains("uses");
        await Assert.That(step.Keys).Contains("with");
        await Assert.That(step.Keys).Contains("env");
        await Assert.That(step.Keys).Contains("shell");
        await Assert.That(step.Keys).Contains("working-directory");

        // action-step should have uses/with but NOT run/shell/working-directory
        var actionStep = model.Sections.First(s => s.Name == "action-step");
        await Assert.That(actionStep.Keys).Contains("uses");
        await Assert.That(actionStep.Keys).Contains("with");
        await Assert.That(actionStep.Keys).DoesNotContain("run");
        await Assert.That(actionStep.Keys).DoesNotContain("shell");
        await Assert.That(actionStep.Keys).DoesNotContain("working-directory");

        // run-step should have run/shell/working-directory but NOT uses/with
        var runStep = model.Sections.First(s => s.Name == "run-step");
        await Assert.That(runStep.Keys).Contains("run");
        await Assert.That(runStep.Keys).Contains("shell");
        await Assert.That(runStep.Keys).Contains("working-directory");
        await Assert.That(runStep.Keys).DoesNotContain("uses");
        await Assert.That(runStep.Keys).DoesNotContain("with");

        // container and service sections
        var container = model.Sections.First(s => s.Name == "container");
        await Assert.That(container.Keys).Contains("image");
        await Assert.That(container.Keys).Contains("credentials");
        await Assert.That(container.Keys).Contains("env");
        await Assert.That(container.Keys).Contains("ports");
        await Assert.That(container.Keys).Contains("volumes");
        await Assert.That(container.Keys).Contains("options");

        var service = model.Sections.First(s => s.Name == "service");
        await Assert.That(service.Keys).Contains("image");
        await Assert.That(service.Keys).Contains("command");
        await Assert.That(service.Keys).Contains("entrypoint");

        // strategy section
        var strategy = model.Sections.First(s => s.Name == "strategy");
        await Assert.That(strategy.Keys).Contains("matrix");
        await Assert.That(strategy.Keys).Contains("fail-fast");
        await Assert.That(strategy.Keys).Contains("max-parallel");

        // on section should list known event names from pipe expansion
        var on = model.Sections.First(s => s.Name == "on");
        await Assert.That(on.Keys).Contains("push");
        await Assert.That(on.Keys).Contains("pull_request");
        await Assert.That(on.Keys).Contains("schedule");
        await Assert.That(on.Keys).Contains("workflow_call");
        await Assert.That(on.Keys).Contains("workflow_dispatch");

        // on-push should have branches, tags, paths, etc.
        var onPush = model.Sections.First(s => s.Name == "on-push");
        await Assert.That(onPush.Keys).Contains("branches");
        await Assert.That(onPush.Keys).Contains("tags");
        await Assert.That(onPush.Keys).Contains("paths");
        await Assert.That(onPush.Keys).Contains("paths-ignore");

        // supplemented sections
        var credentials = model.Sections.First(s => s.Name == "credentials");
        await Assert.That(credentials.Keys).Contains("password");
        await Assert.That(credentials.Keys).Contains("username");

        var runsOn = model.Sections.First(s => s.Name == "runs-on");
        await Assert.That(runsOn.Keys).Contains("group");
        await Assert.That(runsOn.Keys).Contains("labels");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
