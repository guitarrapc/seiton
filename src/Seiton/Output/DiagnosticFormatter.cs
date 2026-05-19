using System.Text;
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
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap = null)
    {
        switch (format)
        {
            case OutputFormat.Text:
                WriteText(writer, diagnostics, oneline, color, sourceMap);
                break;
            case OutputFormat.Json:
                WriteJson(writer, diagnostics);
                break;
            case OutputFormat.Sarif:
                WriteSarif(writer, diagnostics);
                break;
        }
    }

    private static void WriteText(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, bool oneline, bool color, IReadOnlyDictionary<string, byte[]>? sourceMap)
    {
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var file = d.FilePath ?? "<unknown>";
            var line = d.Location.StartLine;
            var col = d.Location.StartColumn;
            var severity = d.Severity.ToString().ToLowerInvariant();
            var ruleId = d.RuleId ?? "parse";

            if (oneline)
            {
                // Compact single-line format
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
            else
            {
                // Rich multi-line format (Rust-style)
                WriteRichDiagnostic(writer, d, file, line, col, severity, ruleId, color, sourceMap);
            }
        }
    }

    private static void WriteRichDiagnostic(
        TextWriter writer,
        Diagnostic d,
        string file,
        int line,
        int col,
        string severity,
        string ruleId,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap)
    {
        if (color)
        {
            var severityColor = d.Severity switch
            {
                DiagnosticSeverity.Error => "\x1b[31m",
                DiagnosticSeverity.Warning => "\x1b[33m",
                _ => "\x1b[36m",
            };
            const string reset = "\x1b[0m";
            const string bold = "\x1b[1m";
            const string dim = "\x1b[2m";
            const string blue = "\x1b[34m";

            // Header: error[rule-id]: message
            writer.Write($"{severityColor}{bold}{severity}[{ruleId}]{reset}{bold}: {d.Message}{reset}");
            writer.WriteLine();

            // Location arrow: --> file:line:col
            writer.WriteLine($"  {blue}-->{reset} {file}:{line}:{col}");

            // Source snippet
            WriteSourceSnippet(writer, d, file, sourceMap, color, severityColor, blue, reset, bold, dim);

            // Help text
            if (d.Help is not null)
                writer.WriteLine($"   {dim}={reset} {bold}help{reset}: {d.Help}");

            writer.WriteLine();
        }
        else
        {
            // Header: error[rule-id]: message
            writer.WriteLine($"{severity}[{ruleId}]: {d.Message}");

            // Location arrow: --> file:line:col
            writer.WriteLine($"  --> {file}:{line}:{col}");

            // Source snippet
            WriteSourceSnippet(writer, d, file, sourceMap, color, null, null, null, null, null);

            // Help text
            if (d.Help is not null)
                writer.WriteLine($"   = help: {d.Help}");

            writer.WriteLine();
        }
    }

    private static void WriteSourceSnippet(
        TextWriter writer,
        Diagnostic d,
        string file,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        bool color,
        string? severityColor,
        string? blue,
        string? reset,
        string? bold,
        string? dim)
    {
        if (sourceMap is null || !sourceMap.TryGetValue(file, out var sourceBytes))
        {
            // No source available — emit minimal gutter line
            writer.WriteLine("   |");
            return;
        }

        var startLine = d.Location.StartLine;
        var endLine = d.Location.EndLine;
        var startCol = d.Location.StartColumn;
        var endCol = d.Location.EndColumn;

        // Clamp to valid range
        if (startLine <= 0)
        {
            writer.WriteLine("   |");
            return;
        }
        if (endLine < startLine) endLine = startLine;

        var lines = ExtractLines(sourceBytes, startLine, endLine);
        if (lines.Length == 0)
        {
            writer.WriteLine("   |");
            return;
        }

        var lineNumWidth = endLine.ToString().Length;
        var gutterPad = new string(' ', lineNumWidth);

        writer.WriteLine($"   {gutterPad}|");

        if (startLine == endLine)
        {
            // Single-line span
            var sourceLine = lines[0];
            WriteGutterLine(writer, startLine, lineNumWidth, sourceLine, color, blue, reset);

            // Underline caret: columns are 1-based
            var safeStart = Math.Max(1, startCol);
            var safeEnd = endCol > safeStart ? endCol : safeStart + 1;
            var leadingSpaces = new string(' ', safeStart - 1);
            var caretLen = Math.Max(1, safeEnd - safeStart);
            var carets = new string('^', caretLen);

            if (color)
                writer.WriteLine($"   {gutterPad}| {leadingSpaces}{severityColor}{carets}{reset}");
            else
                writer.WriteLine($"   {gutterPad}| {leadingSpaces}{carets}");
        }
        else
        {
            // Multi-line span: show opening line with /  and closing line with \___^
            for (var li = 0; li < lines.Length; li++)
            {
                var ln = startLine + li;
                var sourceLine = lines[li];
                var prefix = li == 0 ? "/ " : "| ";
                WriteGutterLineWithPrefix(writer, ln, lineNumWidth, prefix, sourceLine, color, blue, reset);
            }
            // Closing underline
            var closingCarets = new string('^', Math.Max(1, endCol - 1));
            if (color)
                writer.WriteLine($"   {gutterPad}| {severityColor}|_{closingCarets}{reset}");
            else
                writer.WriteLine($"   {gutterPad}| |_{closingCarets}");
        }

        writer.WriteLine($"   {gutterPad}|");
    }

    private static void WriteGutterLine(TextWriter writer, int lineNum, int width, string sourceLine, bool color, string? blue, string? reset)
    {
        var lineStr = lineNum.ToString().PadLeft(width);
        if (color)
            writer.WriteLine($"   {blue}{lineStr}{reset} | {sourceLine}");
        else
            writer.WriteLine($"   {lineStr} | {sourceLine}");
    }

    private static void WriteGutterLineWithPrefix(TextWriter writer, int lineNum, int width, string prefix, string sourceLine, bool color, string? blue, string? reset)
    {
        var lineStr = lineNum.ToString().PadLeft(width);
        if (color)
            writer.WriteLine($"   {blue}{lineStr}{reset} |{prefix}{sourceLine}");
        else
            writer.WriteLine($"   {lineStr} |{prefix}{sourceLine}");
    }

    private static string[] ExtractLines(byte[] utf8, int startLine, int endLine)
    {
        var results = new string[endLine - startLine + 1];
        var currentLine = 1;
        var lineStart = 0;
        var resultIdx = 0;

        for (var i = 0; i <= utf8.Length; i++)
        {
            var isEnd = i == utf8.Length;
            var isNewline = !isEnd && utf8[i] == (byte)'\n';

            if (isNewline || isEnd)
            {
                if (currentLine >= startLine && currentLine <= endLine)
                {
                    // Strip trailing \r if present
                    var len = i - lineStart;
                    if (len > 0 && utf8[lineStart + len - 1] == (byte)'\r')
                        len--;
                    results[resultIdx++] = Encoding.UTF8.GetString(utf8, lineStart, len);
                }
                if (resultIdx == results.Length)
                    break;
                currentLine++;
                lineStart = i + 1;
            }
        }

        // Fill any unfilled slots (file shorter than expected)
        for (var j = resultIdx; j < results.Length; j++)
            results[j] = "";

        return results;
    }

    private static void WriteJson(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics)
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
                Help = d.Help,
            };
        }

        writer.Write(JsonSerializer.Serialize(entries, SeitonJsonContext.Default.JsonDiagnosticEntryArray));
        writer.WriteLine();
    }

    private static void WriteSarif(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics)
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
                Message = new SarifMessage { Text = d.Help is null ? d.Message : $"{d.Message}\n\nHelp: {d.Help}" },
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

internal sealed record JsonDiagnosticEntry
{
    [JsonPropertyName("file")]
    public required string File { get; init; }
    [JsonPropertyName("line")]
    public required int Line { get; init; }
    [JsonPropertyName("col")]
    public required int Col { get; init; }
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("fixable")]
    public required bool Fixable { get; init; }
    [JsonPropertyName("help")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Help { get; init; }
}

// --- SARIF 2.1.0 output models ---

internal sealed record SarifLog
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "2.1.0";
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json";
    [JsonPropertyName("runs")]
    public required SarifRun[] Runs { get; init; }
}

internal sealed record SarifRun
{
    [JsonPropertyName("tool")]
    public required SarifTool Tool { get; init; }
    [JsonPropertyName("results")]
    public required SarifResult[] Results { get; init; }
}

internal sealed record SarifTool
{
    [JsonPropertyName("driver")]
    public required SarifDriver Driver { get; init; }
}

internal sealed record SarifDriver
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("informationUri")]
    public required string InformationUri { get; init; }
    [JsonPropertyName("rules")]
    public required SarifRule[] Rules { get; init; }
}

internal sealed record SarifRule
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

internal sealed record SarifResult
{
    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }
    [JsonPropertyName("ruleIndex")]
    public required int RuleIndex { get; init; }
    [JsonPropertyName("level")]
    public required string Level { get; init; }
    [JsonPropertyName("message")]
    public required SarifMessage Message { get; init; }
    [JsonPropertyName("locations")]
    public required SarifLocation[] Locations { get; init; }
}

internal sealed record SarifMessage
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

internal sealed record SarifLocation
{
    [JsonPropertyName("physicalLocation")]
    public required SarifPhysicalLocation PhysicalLocation { get; init; }
}

internal sealed record SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")]
    public required SarifArtifactLocation ArtifactLocation { get; init; }
    [JsonPropertyName("region")]
    public required SarifRegion Region { get; init; }
}

internal sealed record SarifArtifactLocation
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

internal sealed record SarifRegion
{
    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }
    [JsonPropertyName("startColumn")]
    public required int StartColumn { get; init; }
    [JsonPropertyName("endLine")]
    public required int EndLine { get; init; }
    [JsonPropertyName("endColumn")]
    public required int EndColumn { get; init; }
}

// --- Source-generated JSON context for NativeAOT ---

[JsonSerializable(typeof(JsonDiagnosticEntry[]))]
[JsonSerializable(typeof(SarifLog))]
[JsonSerializable(typeof(Commands.RuleStatusJsonEntry[]))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SeitonJsonContext : JsonSerializerContext
{
}
