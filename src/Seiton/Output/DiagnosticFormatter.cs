using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Core.Parsing;

namespace Seiton.Output;

public static class DiagnosticFormatter
{
    public static void Write(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color)
    {
        switch (format)
        {
            case OutputFormat.Text:
                WriteText(writer, diagnostics, oneline, color);
                break;
            case OutputFormat.Json:
                WriteJson(writer, diagnostics);
                break;
            case OutputFormat.Sarif:
                WriteSarif(writer, diagnostics);
                break;
        }
    }

    static void WriteText(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, bool oneline, bool color)
    {
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var file = d.FilePath ?? "<unknown>";
            var line = d.Location.StartLine;
            var col = d.Location.StartColumn;
            var severity = d.Severity.ToString().ToLowerInvariant();
            var ruleId = d.RuleId ?? "parse";

            if (color)
            {
                var severityColor = d.Severity switch
                {
                    DiagnosticSeverity.Error => "\x1b[31m",   // red
                    DiagnosticSeverity.Warning => "\x1b[33m", // yellow
                    _ => "\x1b[36m",                          // cyan
                };
                const string reset = "\x1b[0m";
                const string bold = "\x1b[1m";
                const string dim = "\x1b[2m";

                writer.Write($"{bold}{file}:{line}:{col}:{reset} ");
                writer.Write($"{severityColor}{severity}{reset} ");
                writer.Write($"{dim}[{ruleId}]{reset} ");
                writer.WriteLine(d.Message);
            }
            else
            {
                writer.WriteLine($"{file}:{line}:{col}: {severity} [{ruleId}] {d.Message}");
            }
        }
    }

    static void WriteJson(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics)
    {
        var entries = new JsonDiagnosticEntry[diagnostics.Count];
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            entries[i] = new JsonDiagnosticEntry
            {
                File = d.FilePath ?? "<unknown>",
                Line = d.Location.StartLine,
                Col = d.Location.StartColumn,
                Severity = d.Severity.ToString().ToLowerInvariant(),
                RuleId = d.RuleId ?? "parse",
                Message = d.Message,
                Fixable = d.Fix is not null,
            };
        }

        writer.Write(JsonSerializer.Serialize(entries, SeitonJsonContext.Default.JsonDiagnosticEntryArray));
        writer.WriteLine();
    }

    static void WriteSarif(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics)
    {
        // Collect unique rule IDs
        var ruleSet = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId ?? "parse";
            if (!ruleSet.ContainsKey(ruleId))
                ruleSet[ruleId] = ruleSet.Count;
        }

        var rules = new SarifRule[ruleSet.Count];
        foreach (var (id, idx) in ruleSet)
        {
            rules[idx] = new SarifRule { Id = id };
        }

        var results = new SarifResult[diagnostics.Count];
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var ruleId = d.RuleId ?? "parse";
            results[i] = new SarifResult
            {
                RuleId = ruleId,
                RuleIndex = ruleSet[ruleId],
                Level = d.Severity switch
                {
                    DiagnosticSeverity.Error => "error",
                    DiagnosticSeverity.Warning => "warning",
                    _ => "note",
                },
                Message = new SarifMessage { Text = d.Message },
                Locations =
                [
                    new SarifLocation
                    {
                        PhysicalLocation = new SarifPhysicalLocation
                        {
                            ArtifactLocation = new SarifArtifactLocation
                            {
                                Uri = d.FilePath ?? "<unknown>",
                            },
                            Region = new SarifRegion
                            {
                                StartLine = d.Location.StartLine,
                                StartColumn = d.Location.StartColumn,
                                EndLine = d.Location.EndLine,
                                EndColumn = d.Location.EndColumn,
                            },
                        },
                    },
                ],
            };
        }

        var sarif = new SarifLog
        {
            Runs =
            [
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifDriver
                        {
                            Name = "seiton",
                            InformationUri = "https://github.com/guitarrapc/seiton",
                            Rules = rules,
                        },
                    },
                    Results = results,
                },
            ],
        };

        writer.Write(JsonSerializer.Serialize(sarif, SeitonJsonContext.Default.SarifLog));
        writer.WriteLine();
    }
}

// --- JSON output models ---

internal sealed class JsonDiagnosticEntry
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";
    [JsonPropertyName("line")]
    public int Line { get; set; }
    [JsonPropertyName("col")]
    public int Col { get; set; }
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    [JsonPropertyName("fixable")]
    public bool Fixable { get; set; }
}

// --- SARIF 2.1.0 output models ---

internal sealed class SarifLog
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.1.0";
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json";
    [JsonPropertyName("runs")]
    public SarifRun[] Runs { get; set; } = [];
}

internal sealed class SarifRun
{
    [JsonPropertyName("tool")]
    public SarifTool Tool { get; set; } = new();
    [JsonPropertyName("results")]
    public SarifResult[] Results { get; set; } = [];
}

internal sealed class SarifTool
{
    [JsonPropertyName("driver")]
    public SarifDriver Driver { get; set; } = new();
}

internal sealed class SarifDriver
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("informationUri")]
    public string InformationUri { get; set; } = "";
    [JsonPropertyName("rules")]
    public SarifRule[] Rules { get; set; } = [];
}

internal sealed class SarifRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

internal sealed class SarifResult
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";
    [JsonPropertyName("ruleIndex")]
    public int RuleIndex { get; set; }
    [JsonPropertyName("level")]
    public string Level { get; set; } = "";
    [JsonPropertyName("message")]
    public SarifMessage Message { get; set; } = new();
    [JsonPropertyName("locations")]
    public SarifLocation[] Locations { get; set; } = [];
}

internal sealed class SarifMessage
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal sealed class SarifLocation
{
    [JsonPropertyName("physicalLocation")]
    public SarifPhysicalLocation PhysicalLocation { get; set; } = new();
}

internal sealed class SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")]
    public SarifArtifactLocation ArtifactLocation { get; set; } = new();
    [JsonPropertyName("region")]
    public SarifRegion Region { get; set; } = new();
}

internal sealed class SarifArtifactLocation
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

internal sealed class SarifRegion
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }
    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}

// --- Source-generated JSON context for NativeAOT ---

[JsonSerializable(typeof(JsonDiagnosticEntry[]))]
[JsonSerializable(typeof(SarifLog))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SeitonJsonContext : JsonSerializerContext
{
}
