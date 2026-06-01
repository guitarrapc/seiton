using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class RulesCommand
{
    public static int Run(string? config, OutputFormat format)
        => Run(config, format, Console.Out, Console.Error);

    internal static int Run(string? config, OutputFormat format, TextWriter output, TextWriter error)
    {
        var resolvedFormat = CliConfigBridge.ResolveOutputFormat(format);
        if (resolvedFormat == OutputFormat.Sarif)
        {
            error.WriteLine("SARIF output is not supported for 'seiton rules'. Use --format text or --format json.");
            return ExitCode.InvalidOptions;
        }

        LintConfig? lintConfig = null;

        ConfigPathResolution configResolution;
        try
        {
            configResolution = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var configPath = configResolution.Path;

        if (configPath is not null)
        {
            var (loaded, diagnostics) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork: false, enableImageNetwork: false);
            lintConfig = loaded;

            if (CheckCommand.HasConfigErrors(diagnostics, resolvedFormat, color: false, oneline: false, error))
                return ExitCode.FatalError;
        }

        var statuses = RuleListResolver.Resolve(lintConfig);

        switch (resolvedFormat)
        {
            case OutputFormat.Json:
                WriteJson(output, statuses);
                break;
            default:
                WriteText(output, statuses);
                break;
        }

        return ExitCode.Success;
    }

    private static void WriteText(TextWriter writer, IReadOnlyList<RuleStatus> statuses)
    {
        // Header
        writer.WriteLine($"{"Rule",-40} {"Enabled",-9} {"Type",-8} {"Severity",-10} {"Fix",-5} {"Document",-10} {"Reason"}");
        writer.WriteLine(new string('-', 105));

        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            var enabled = s.Enabled ? "yes" : "no";
            var type = s.Rule.IsOnline ? "online" : "local";
            var severity = s.Rule.DefaultSeverity;
            var fix = s.Rule.SupportsAutoFix ? "yes" : "no";
            var document = (s.Rule.SupportsWorkflow, s.Rule.SupportsAction) switch
            {
                (true, true) => "both",
                (true, false) => "workflow",
                (false, true) => "action",
                _ => "none",
            };

            writer.WriteLine($"{s.Rule.Id,-40} {enabled,-9} {type,-8} {severity,-10} {fix,-5} {document,-10} {s.Reason}");
        }

        // Summary
        var enabledCount = 0;
        for (var j = 0; j < statuses.Count; j++)
        {
            if (statuses[j].Enabled) enabledCount++;
        }
        writer.WriteLine();
        writer.WriteLine($"{statuses.Count} rules total ({enabledCount} enabled, {statuses.Count - enabledCount} disabled)");

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
                DefaultSeverity = s.Rule.DefaultSeverity,
                SupportsAutoFix = s.Rule.SupportsAutoFix,
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
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }
    [JsonPropertyName("type")]
    public required string Type { get; init; }
    [JsonPropertyName("defaultSeverity")]
    public required string DefaultSeverity { get; init; }
    [JsonPropertyName("supportsAutoFix")]
    public required bool SupportsAutoFix { get; init; }
    [JsonPropertyName("supportsWorkflow")]
    public required bool SupportsWorkflow { get; init; }
    [JsonPropertyName("supportsAction")]
    public required bool SupportsAction { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
