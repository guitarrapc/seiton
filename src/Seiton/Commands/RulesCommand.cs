using System.Text.Json;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class RulesCommand
{
    public static int Run(string? config, OutputFormat format)
    {
        var resolvedFormat = CliConfigBridge.ResolveOutputFormat(format);
        if (resolvedFormat == OutputFormat.Sarif)
        {
            Console.Error.WriteLine("SARIF output is not supported for 'seiton rules'. Use --format text or --format json.");
            return ExitCode.InvalidOptions;
        }

        LintConfig? lintConfig = null;

        string? configPath;
        try
        {
            configPath = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        if (configPath is not null)
        {
            var (loaded, diagnostics) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork: false, enableImageNetwork: false);
            lintConfig = loaded;

            if (CheckCommand.HasConfigErrors(diagnostics, resolvedFormat, color: false, oneline: false))
                return ExitCode.FatalError;
        }

        var statuses = RuleListResolver.Resolve(lintConfig);

        switch (resolvedFormat)
        {
            case OutputFormat.Json:
                WriteJson(Console.Out, statuses);
                break;
            default:
                WriteText(Console.Out, statuses);
                break;
        }

        return ExitCode.Success;
    }

    private static void WriteText(TextWriter writer, IReadOnlyList<RuleStatus> statuses)
    {
        // Header
        writer.WriteLine($"{"Rule",-40} {"Enabled",-9} {"Type",-8} {"Document",-10} {"Reason"}");
        writer.WriteLine(new string('-', 90));

        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            var enabled = s.Enabled ? "yes" : "no";
            var type = s.Rule.IsOnline ? "online" : "local";
            var document = (s.Rule.SupportsWorkflow, s.Rule.SupportsAction) switch
            {
                (true, true) => "both",
                (true, false) => "workflow",
                (false, true) => "action",
                _ => "none",
            };

            writer.WriteLine($"{s.Rule.Id,-40} {enabled,-9} {type,-8} {document,-10} {s.Reason}");
        }

        // Footer: explain how to enable opt-in rules
        writer.WriteLine();
        writer.WriteLine("To enable an opt-in rule, add to .github/seiton.yaml:");
        writer.WriteLine("  rules:");
        writer.WriteLine("    <rule-id>:");
        writer.WriteLine("      enabled: true");
        writer.WriteLine();
        writer.WriteLine("Online rules use the GitHub API. Set GITHUB_TOKEN (or SEITON_GITHUB_TOKEN) to avoid rate limits.");
    }

    private static void WriteJson(TextWriter writer, IReadOnlyList<RuleStatus> statuses)
    {
        var entries = new RuleStatusJsonEntry[statuses.Count];
        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            entries[i] = new RuleStatusJsonEntry
            {
                Id = s.Rule.Id,
                Name = s.Rule.Name,
                Enabled = s.Enabled,
                Type = s.Rule.IsOnline ? "online" : "local",
                SupportsWorkflow = s.Rule.SupportsWorkflow,
                SupportsAction = s.Rule.SupportsAction,
                Reason = s.Reason,
            };
        }

        writer.Write(JsonSerializer.Serialize(entries, SeitonJsonContext.Default.RuleStatusJsonEntryArray));
        writer.WriteLine();
    }
}

internal sealed record RuleStatusJsonEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required string Type { get; init; }
    public required bool SupportsWorkflow { get; init; }
    public required bool SupportsAction { get; init; }
    public required string Reason { get; init; }
}
