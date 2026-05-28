using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

/// <summary>
/// Parses GitHub Docs workflow-syntax.md headings to build a complete parent→child key
/// hierarchy for all documented workflow YAML sections.
///
/// Algorithm:
/// <list type="number">
///   <item>Extract all <c>## `path`</c> headings from the raw markdown.</item>
///   <item>For each heading, split into dot-separated segments.</item>
///   <item>Expand pipe-separated alternatives in angle brackets (e.g. <c>&lt;branches|branches-ignore&gt;</c>).</item>
///   <item>Register every concrete (non-parameter) segment as a child of its parent path.</item>
///   <item>Emit named sections for all parents that have concrete children.</item>
///   <item>Derive special sections (action-step, run-step) and supplement missing ones (credentials, runs-on).</item>
/// </list>
/// </summary>
internal sealed partial class WorkflowSyntaxExpectedKeysParser
{
    /// <summary>Matches headings like: ## `jobs.&lt;job_id&gt;.steps[*].id`</summary>
    [GeneratedRegex(@"^##\s+`([^`]+)`\s*$", RegexOptions.Compiled)]
    private static partial Regex HeadingPattern();

    /// <summary>Known heading-path → clean section-name mapping.</summary>
    private static readonly Dictionary<string, string> KnownSectionNames = new(StringComparer.Ordinal)
    {
        ["(root)"] = "workflow",
        ["on"] = "on",
        ["on.<event_name>"] = "on-event",
        ["on.push"] = "on-push",
        ["on.pull_request"] = "on-pull-request",
        ["on.pull_request_target"] = "on-pull-request-target",
        ["on.workflow_run"] = "on-workflow-run",
        ["on.workflow_call"] = "on-workflow-call",
        ["on.workflow_call.inputs.<input_id>"] = "workflow-call-input",
        ["on.workflow_call.secrets.<secret_id>"] = "workflow-call-secret",
        ["on.workflow_dispatch"] = "on-workflow-dispatch",
        ["on.workflow_dispatch.inputs.<input_id>"] = "workflow-dispatch-input",
        ["on.merge_group"] = "on-merge-group",
        ["defaults"] = "defaults",
        ["defaults.run"] = "defaults-run",
        ["jobs.<job_id>"] = "job",
        ["jobs.<job_id>.defaults"] = "job-defaults",
        ["jobs.<job_id>.defaults.run"] = "job-defaults-run",
        ["jobs.<job_id>.steps[*]"] = "step",
        ["jobs.<job_id>.steps[*].with"] = "step-with",
        ["jobs.<job_id>.strategy"] = "strategy",
        ["jobs.<job_id>.strategy.matrix"] = "strategy-matrix",
        ["jobs.<job_id>.container"] = "container",
        ["jobs.<job_id>.services.<service_id>"] = "service",
        ["jobs.<job_id>.secrets"] = "job-secrets",
    };

    /// <summary>
    /// Sections whose sub-keys are documented in body text rather than as separate headings.
    /// When heading-based extraction yields no children, these fallback lists are used.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Description, List<string> Keys)> SupplementedSections = new(StringComparer.Ordinal)
    {
        ["jobs.<job_id>.container.credentials"] = ("credentials", "Keys valid for credentials mapping", ["password", "username"]),
        ["jobs.<job_id>.runs-on"] = ("runs-on", "Keys valid for runs-on mapping form", ["group", "labels"]),
        ["on.merge_group"] = ("on-merge-group", "Expected keys for on.merge_group", ["branches", "branches-ignore"]),
    };

    /// <summary>
    /// Extra keys to merge into heading-extracted sections.
    /// These are documented in body text rather than as separate <c>##</c> headings.
    /// </summary>
    private static readonly Dictionary<string, List<string>> SupplementalKeys = new(StringComparer.Ordinal)
    {
        ["on-workflow-run"] = ["workflows"],
    };

    /// <summary>
    /// Sections whose keys are entirely documented in body text (or referenced via reusable includes)
    /// and cannot be derived from <c>## `...`</c> headings at all. Unlike <see cref="SupplementedSections"/>
    /// these are always added regardless of heading extraction results, because some share a parent path
    /// with heading-derived sections but use a different section name (e.g. <c>workflow-call-input-field</c>
    /// vs heading-derived <c>workflow-call-input</c>).
    /// </summary>
    private static readonly List<ExpectedKeySection> AdditionalSections =
    [
        new("concurrency", "Expected keys for concurrency section", ["cancel-in-progress", "group", "queue"]),
        new("environment", "Expected keys for jobs.<job_id>.environment", ["deployment", "name", "url"]),
        new("schedule-entry", "Expected keys for on.schedule entry", ["cron", "timezone"]),
        new("webhook-event-option", "Expected keys for on.<event_name> options", ["branches", "branches-ignore", "paths", "paths-ignore", "tags", "tags-ignore", "types", "workflows"]),
        new("workflow-call-input-field", "Expected keys for on.workflow_call.inputs.<input_id> fields", ["default", "description", "required", "type"]),
        new("workflow-call-output-field", "Expected keys for on.workflow_call.outputs.<output_id> fields", ["description", "value"]),
        new("workflow-call-secret-field", "Expected keys for on.workflow_call.secrets.<secret_id> fields", ["description", "required"]),
        new("workflow-dispatch-input-field", "Expected keys for on.workflow_dispatch.inputs.<input_id> fields", ["default", "description", "options", "required", "type"]),
    ];

    /// <summary>
    /// Parses the raw workflow-syntax.md content and extracts expected key groups
    /// for all documented sections.
    /// </summary>
    public ExpectedKeysModel Parse(string markdownContent)
    {
        var headings = ExtractHeadings(markdownContent);
        var parentChildMap = BuildParentChildMap(headings);
        var sections = new List<ExpectedKeySection>();

        // Emit sections for all parents with concrete children
        foreach (var (parentPath, children) in parentChildMap.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            if (children.Count == 0)
                continue;

            var name = NormalizeSectionName(parentPath);
            var description = parentPath == "(root)"
                ? "Top-level workflow keys"
                : $"Expected keys for {parentPath}";
            var sortedKeys = children.OrderBy(static k => k, StringComparer.Ordinal).ToList();
            sections.Add(new ExpectedKeySection(name, description, sortedKeys));
        }

        // Derive action-step and run-step from step section
        var stepSection = sections.FirstOrDefault(static s => s.Name == "step");
        if (stepSection is not null)
        {
            var actionStepKeys = stepSection.Keys
                .Where(static k => k is not ("run" or "shell" or "working-directory"))
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToList();
            sections.Add(new ExpectedKeySection(
                "action-step",
                "Keys valid for action-form steps (with 'uses')",
                actionStepKeys));

            var runStepKeys = stepSection.Keys
                .Where(static k => k is not ("uses" or "with"))
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToList();
            sections.Add(new ExpectedKeySection(
                "run-step",
                "Keys valid for run-form steps (with 'run')",
                runStepKeys));
        }

        // Add supplemented sections (sub-keys documented in body text, not as headings)
        foreach (var (parentPath, (name, description, keys)) in SupplementedSections)
        {
            // Only add if heading-based extraction didn't find this section
            if (parentChildMap.ContainsKey(parentPath) && parentChildMap[parentPath].Count > 0)
                continue;

            sections.Add(new ExpectedKeySection(name, description,
                keys.OrderBy(static k => k, StringComparer.Ordinal).ToList()));
        }

        // Merge supplemental keys into heading-extracted sections
        foreach (var (sectionName, extraKeys) in SupplementalKeys)
        {
            var existing = sections.FindIndex(s => s.Name == sectionName);
            if (existing >= 0)
            {
                var section = sections[existing];
                var merged = section.Keys.Concat(extraKeys)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static k => k, StringComparer.Ordinal)
                    .ToList();
                sections[existing] = new ExpectedKeySection(section.Name, section.Description, merged);
            }
        }

        // Add body-text-only sections that cannot be derived from headings
        foreach (var additional in AdditionalSections)
        {
            sections.Add(additional);
        }

        // Sort all sections by name for deterministic output
        sections.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return new ExpectedKeysModel(sections);
    }

    /// <summary>Extracts all <c>## `...`</c> heading paths from the markdown.</summary>
    internal static List<string> ExtractHeadings(string markdownContent)
    {
        var headings = new List<string>();
        var regex = HeadingPattern();

        foreach (var line in markdownContent.Split('\n'))
        {
            var match = regex.Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                headings.Add(match.Groups[1].Value);
            }
        }

        return headings;
    }

    /// <summary>
    /// Builds a map of parent-path → set of direct concrete child key names
    /// from all extracted heading paths.
    /// </summary>
    internal static Dictionary<string, SortedSet<string>> BuildParentChildMap(List<string> headings)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var heading in headings)
        {
            var segments = SplitSegments(heading);
            var expandedPaths = ExpandAlternatives(segments);

            foreach (var path in expandedPaths)
            {
                for (var i = 0; i < path.Count; i++)
                {
                    var parentPath = i == 0
                        ? "(root)"
                        : string.Join(".", path.Take(i));
                    var childSeg = path[i];

                    // Skip single-parameter wildcards like <job_id>, <event_name>
                    if (IsSingleParameter(childSeg))
                        continue;

                    // Strip [*] array subscript from child key name
                    var childKey = childSeg.EndsWith("[*]", StringComparison.Ordinal)
                        ? childSeg[..^3]
                        : childSeg;

                    if (!map.TryGetValue(parentPath, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.Ordinal);
                        map[parentPath] = set;
                    }

                    set.Add(childKey);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Splits a heading path into segments by <c>.</c>, preserving bracket contents.
    /// e.g. <c>jobs.&lt;job_id&gt;.steps[*].id</c> → [jobs, &lt;job_id&gt;, steps[*], id]
    /// </summary>
    internal static List<string> SplitSegments(string headingPath)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < headingPath.Length; i++)
        {
            var c = headingPath[i];
            switch (c)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case '.' when depth == 0:
                    if (i > start)
                        segments.Add(headingPath[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < headingPath.Length)
            segments.Add(headingPath[start..]);

        return segments;
    }

    /// <summary>
    /// Expands pipe-separated alternatives in angle-bracketed segments into the
    /// cartesian product of all combinations.
    /// e.g. [on, &lt;push|pull_request&gt;, &lt;paths|paths-ignore&gt;] →
    ///   [[on,push,paths], [on,push,paths-ignore], [on,pull_request,paths], [on,pull_request,paths-ignore]]
    /// Single parameters like &lt;event_name&gt; are kept as-is (not expanded).
    /// </summary>
    internal static List<List<string>> ExpandAlternatives(List<string> segments)
    {
        var alternatives = new List<List<string>>(segments.Count);

        foreach (var seg in segments)
        {
            if (seg.StartsWith('<') && seg.EndsWith('>') && seg.Contains('|'))
            {
                // Pipe-separated alternatives: <a|b|c> → [a, b, c]
                var inner = seg[1..^1];
                alternatives.Add([.. inner.Split('|')]);
            }
            else
            {
                alternatives.Add([seg]);
            }
        }

        // Compute cartesian product
        var result = new List<List<string>> { new() };
        foreach (var alts in alternatives)
        {
            var newResult = new List<List<string>>(result.Count * alts.Count);
            foreach (var existing in result)
            {
                foreach (var alt in alts)
                {
                    var combined = new List<string>(existing.Count + 1);
                    combined.AddRange(existing);
                    combined.Add(alt);
                    newResult.Add(combined);
                }
            }

            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Returns true if the segment is a single-parameter wildcard (e.g. &lt;job_id&gt;),
    /// which should NOT be registered as a concrete child key.
    /// Pipe-separated alternatives (e.g. &lt;branches|branches-ignore&gt;) are already expanded
    /// and will not appear here.
    /// </summary>
    private static bool IsSingleParameter(string segment)
    {
        return segment.StartsWith('<') && segment.EndsWith('>');
    }

    /// <summary>
    /// Converts a raw parent path to a clean kebab-case section name.
    /// Uses explicit mapping for known paths; falls back to algorithmic generation.
    /// </summary>
    internal static string NormalizeSectionName(string parentPath)
    {
        if (KnownSectionNames.TryGetValue(parentPath, out var name))
            return name;

        // Fallback: strip parameter segments, [*], and convert dots to hyphens
        return GenerateSectionName(parentPath);
    }

    private static string GenerateSectionName(string parentPath)
    {
        var segments = SplitSegments(parentPath);
        var concreteSegments = new List<string>();
        foreach (var seg in segments)
        {
            if (IsSingleParameter(seg))
                continue;

            var cleaned = seg.EndsWith("[*]", StringComparison.Ordinal)
                ? seg[..^3]
                : seg;
            // Convert underscores to hyphens for consistency
            concreteSegments.Add(cleaned.Replace('_', '-'));
        }

        return string.Join("-", concreteSegments);
    }
}
