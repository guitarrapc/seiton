using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;
using Seiton.Core.Parsing;

namespace Seiton.Output;

public static class DiagnosticFormatter
{
    private const string SarifGeneralHelpUri = "https://github.com/guitarrapc/seiton/blob/main/docs/usage.md";
    private const string SarifRuleHelpUriPrefix = "https://github.com/guitarrapc/seiton/blob/main/docs/rules.md#";

    private static readonly string SarifDriverVersion = ToolVersionResolver.ResolveFromAssembly(typeof(DiagnosticFormatter).Assembly);

    public static void Write(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap = null)
    {
        Write(writer, diagnostics, format, oneline, color, sourceMap, pathBaseDirectory: null);
    }

    internal static void Write(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        switch (format)
        {
            case OutputFormat.Text:
                WriteText(writer, diagnostics, oneline, color, sourceMap, pathBaseDirectory);
                break;
            case OutputFormat.GitHubActions:
                // GitHub Actions format should always be plain text without ANSI escapes.
                WriteGitHubActions(writer, diagnostics, oneline, sourceMap, pathBaseDirectory);
                break;
            case OutputFormat.Json:
                WriteJson(writer, diagnostics, pathBaseDirectory);
                break;
            case OutputFormat.Sarif:
                WriteSarif(writer, diagnostics, pathBaseDirectory);
                break;
        }
    }

    private static void WriteGitHubActions(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        bool oneline,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        string? currentGroupFile = null;
        string? currentGroupDisplay = null;
        string currentLineDisplay = "<unknown>";

        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var fileKey = d.FilePath ?? "<unknown>";

            if (!string.Equals(currentGroupFile, fileKey, StringComparison.Ordinal))
            {
                if (currentGroupFile is not null)
                {
                    writer.WriteLine("::endgroup::");
                }

                var fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                currentGroupDisplay = EscapeGitHubCommandValue(fileDisplay);
                currentLineDisplay = currentGroupDisplay;
                writer.Write("::group::");
                writer.WriteLine(currentGroupDisplay);
                currentGroupFile = fileKey;
            }

            WriteTextDiagnostic(writer, d, fileKey, currentLineDisplay, oneline, color: false, sourceMap);
        }

        writer.WriteLine("::endgroup::");
    }

    private static void WriteText(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, bool oneline, bool color, IReadOnlyDictionary<string, byte[]>? sourceMap, string? pathBaseDirectory)
    {
        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        string? previousFilePath = null;
        string previousDisplayPath = "<unknown>";
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var fileKey = d.FilePath ?? "<unknown>";
            string fileDisplay;
            if (string.Equals(previousFilePath, d.FilePath, StringComparison.Ordinal))
            {
                fileDisplay = previousDisplayPath;
            }
            else
            {
                fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                previousFilePath = d.FilePath;
                previousDisplayPath = fileDisplay;
            }
            WriteTextDiagnostic(writer, d, fileKey, fileDisplay, oneline, color, sourceMap);
        }
    }

    private static string EscapeGitHubCommandValue(string value)
    {
        var firstEscapedIndex = value.AsSpan().IndexOfAny('%', '\r', '\n');
        if (firstEscapedIndex < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        builder.Append(value, 0, firstEscapedIndex);

        for (var i = firstEscapedIndex; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '%':
                    builder.Append("%25");
                    break;
                case '\r':
                    builder.Append("%0D");
                    break;
                case '\n':
                    builder.Append("%0A");
                    break;
                default:
                    builder.Append(value[i]);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void WriteTextDiagnostic(
        TextWriter writer,
        Diagnostic d,
        string fileKey,
        string fileDisplay,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap)
    {
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

                writer.Write($"{bold}{fileDisplay}:{line}:{col}:{reset} ");
                writer.Write($"{severityColor}{severity}{reset} ");
                writer.Write($"{dim}[{ruleId}]{reset} ");
                writer.WriteLine(d.Message);
            }
            else
            {
                writer.WriteLine($"{fileDisplay}:{line}:{col}: {severity} [{ruleId}] {d.Message}");
            }

            return;
        }

        // Rich multi-line format (Rust-style)
        WriteRichDiagnostic(writer, d, fileKey, fileDisplay, line, col, severity, ruleId, color, sourceMap);
    }

    private static void WriteRichDiagnostic(
        TextWriter writer,
        Diagnostic d,
        string sourceFileKey,
        string displayFile,
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
            writer.WriteLine($"  {blue}-->{reset} {displayFile}:{line}:{col}");

            // Source snippet
            WriteSourceSnippet(writer, d, sourceFileKey, sourceMap, color, severityColor, blue, reset, bold, dim);

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
            writer.WriteLine($"  --> {displayFile}:{line}:{col}");

            // Source snippet
            WriteSourceSnippet(writer, d, sourceFileKey, sourceMap, color, null, null, null, null, null);

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

    private static void WriteJson(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, string? pathBaseDirectory)
    {
        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        string? previousFilePath = null;
        string previousDisplayPath = "<unknown>";
        var entries = new JsonDiagnosticEntry[diagnostics.Count];
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            string fileDisplay;
            if (string.Equals(previousFilePath, d.FilePath, StringComparison.Ordinal))
            {
                fileDisplay = previousDisplayPath;
            }
            else
            {
                fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                previousFilePath = d.FilePath;
                previousDisplayPath = fileDisplay;
            }
            entries[i] = new JsonDiagnosticEntry
            {
                File = fileDisplay,
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

    private static void WriteSarif(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, string? pathBaseDirectory)
    {
        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        // Collect unique rule IDs
        var ruleSet = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId ?? "parse";
            if (!ruleSet.ContainsKey(ruleId))
                ruleSet[ruleId] = ruleSet.Count;
        }

        var rules = new string[ruleSet.Count];
        foreach (var (id, idx) in ruleSet)
        {
            rules[idx] = id;
        }

        using var buffer = new PooledByteBufferWriter(Math.Max(1024, diagnostics.Count * 192));
        using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        json.WriteStartObject();
        json.WriteString("version", "2.1.0");
        json.WriteString("$schema", "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json");
        json.WriteStartArray("runs");
        json.WriteStartObject();

        json.WriteStartObject("tool");
        json.WriteStartObject("driver");
        json.WriteString("name", "seiton");
        json.WriteString("version", SarifDriverVersion);
        json.WriteString("informationUri", "https://github.com/guitarrapc/seiton");
        json.WriteStartArray("rules");
        for (var i = 0; i < rules.Length; i++)
        {
            var id = rules[i];
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("helpUri", BuildSarifRuleHelpUri(id));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteStartArray("results");
        string? previousFilePath = null;
        SarifArtifactLocation? previousArtifactLocation = null;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var ruleId = d.RuleId ?? "parse";
            SarifArtifactLocation artifactLocation;
            if (string.Equals(previousFilePath, d.FilePath, StringComparison.Ordinal) && previousArtifactLocation is not null)
            {
                artifactLocation = previousArtifactLocation;
            }
            else
            {
                artifactLocation = pathResolver.ResolveSarifArtifactLocation(d.FilePath);
                previousFilePath = d.FilePath;
                previousArtifactLocation = artifactLocation;
            }
            json.WriteStartObject();
            json.WriteString("ruleId", ruleId);
            json.WriteNumber("ruleIndex", ruleSet[ruleId]);
            json.WriteString("level", d.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "note",
            });

            json.WriteStartObject("message");
            json.WriteString("text", d.Help is null ? d.Message : $"{d.Message}\n\nHelp: {d.Help}");
            json.WriteEndObject();

            json.WriteStartArray("locations");
            json.WriteStartObject();
            json.WriteStartObject("physicalLocation");
            json.WriteStartObject("artifactLocation");
            json.WriteString("uri", artifactLocation.Uri);
            if (artifactLocation.UriBaseId is not null)
            {
                json.WriteString("uriBaseId", artifactLocation.UriBaseId);
            }
            json.WriteEndObject();

            json.WriteStartObject("region");
            json.WriteNumber("startLine", d.Location.StartLine);
            json.WriteNumber("startColumn", d.Location.StartColumn);
            json.WriteNumber("endLine", d.Location.EndLine);
            json.WriteNumber("endColumn", d.Location.EndColumn);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();

        var originalUriBaseIds = pathResolver.CreateOriginalUriBaseIds();
        if (originalUriBaseIds is not null
            && originalUriBaseIds.TryGetValue(PathDisplayResolver.SarifWorkingDirectoryBaseId, out var workingDirectoryBase))
        {
            json.WriteStartObject("originalUriBaseIds");
            json.WriteStartObject(PathDisplayResolver.SarifWorkingDirectoryBaseId);
            json.WriteString("uri", workingDirectoryBase.Uri);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();

        WriteUtf8ToTextWriter(writer, buffer.WrittenSpan);
        writer.WriteLine();
    }

    private static void WriteUtf8ToTextWriter(TextWriter writer, ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
        {
            return;
        }

        var charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount <= 2048)
        {
            Span<char> chars = stackalloc char[charCount];
            var written = Encoding.UTF8.GetChars(utf8, chars);
            writer.Write(chars[..written]);
            return;
        }

        var rented = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            var written = Encoding.UTF8.GetChars(utf8, rented);
            writer.Write(rented, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string BuildSarifRuleHelpUri(string ruleId)
    {
        if (string.Equals(ruleId, "parse", StringComparison.Ordinal))
            return SarifGeneralHelpUri;

        return string.Concat(SarifRuleHelpUriPrefix, ruleId);
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

// --- Source-generated JSON context for NativeAOT ---

[JsonSerializable(typeof(JsonDiagnosticEntry[]))]
[JsonSerializable(typeof(Commands.RuleStatusJsonEntry[]))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SeitonJsonContext : JsonSerializerContext
{
}

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _index;
    private bool _disposed;

    public PooledByteBufferWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
        _index = 0;
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public void Advance(int count)
    {
        ThrowIfDisposed();
        if ((uint)count > (uint)(_buffer.Length - _index))
            throw new ArgumentOutOfRangeException(nameof(count));
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ThrowIfDisposed();
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ThrowIfDisposed();
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1)
            sizeHint = 1;

        var available = _buffer.Length - _index;
        if (available >= sizeHint)
            return;

        var growBy = Math.Max(sizeHint, _buffer.Length);
        var newSize = checked(_buffer.Length + growBy);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _index).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _index = 0;
        _disposed = true;
    }
}
