using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;
using System.Runtime.CompilerServices;
using Seiton.Core.Parsing;

namespace Seiton.Output;

public static class DiagnosticFormatter
{
    private const string SarifGeneralHelpUri = "https://github.com/guitarrapc/seiton/blob/main/docs/usage.md";
    private const string SarifRuleHelpUriPrefix = "https://github.com/guitarrapc/seiton/blob/main/docs/rules.md#";

    private static readonly string SarifDriverVersion = ToolVersionResolver.ResolveFromAssembly(typeof(DiagnosticFormatter).Assembly);

    private static readonly string[] SeverityLowerStrings = ["info", "warning", "error"];

    private static readonly JsonWriterOptions JsonDiagnosticWriterOptions = new() { SkipValidation = true, Indented = false };

    public static void Write(
        IBufferWriter<byte> output,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap = null)
    {
        Write(output, diagnostics, format, oneline, color, sourceMap, pathBaseDirectory: null);
    }

    internal static void Write(
        IBufferWriter<byte> output,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        var writer = new Utf8Writer(output);
        switch (format)
        {
            case OutputFormat.Text:
                WriteText(writer, diagnostics, oneline, color, sourceMap, pathBaseDirectory);
                break;
            case OutputFormat.GitHubActions:
                WriteGitHubActions(writer, diagnostics, oneline, sourceMap, pathBaseDirectory);
                break;
            case OutputFormat.Json:
                WriteJson(output, writer, diagnostics, pathBaseDirectory);
                break;
            case OutputFormat.Sarif:
                WriteSarif(output, writer, diagnostics, pathBaseDirectory);
                break;
        }
    }

    public static void WriteToStandardOutput(
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap = null)
    {
        WriteToStandardOutput(diagnostics, format, oneline, color, sourceMap, pathBaseDirectory: null);
    }

    internal static void WriteToStandardOutput(
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        using var buffer = new PooledByteBufferWriter(EstimateInitialCapacity(diagnostics));
        Write(buffer, diagnostics, format, oneline, color, sourceMap, pathBaseDirectory);
        FlushToStandardOutput(buffer.WrittenSpan);
    }

    internal static void FlushToStandardOutput(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        // Process console uses StreamWriter; test redirects (StringWriter) need TextWriter decode.
        if (Console.Out is not StreamWriter)
        {
            Utf8Writer.WriteToTextWriter(Console.Out, utf8);
            return;
        }

        Utf8Writer.WriteToStandardOutput(utf8);
    }

    public static void WriteToTextWriter(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap = null)
    {
        WriteToTextWriter(writer, diagnostics, format, oneline, color, sourceMap, pathBaseDirectory: null);
    }

    internal static void WriteToTextWriter(
        TextWriter writer,
        IReadOnlyList<Diagnostic> diagnostics,
        OutputFormat format,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        using var buffer = new PooledByteBufferWriter(EstimateInitialCapacity(diagnostics));
        Write(buffer, diagnostics, format, oneline, color, sourceMap, pathBaseDirectory);
        Utf8Writer.WriteToTextWriter(writer, buffer.WrittenSpan);
    }

    private static int EstimateInitialCapacity(IReadOnlyList<Diagnostic> diagnostics)
        => Math.Max(256, diagnostics.Count * 128);

    private static void WriteGitHubActions(
        Utf8Writer writer,
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
        var lineIndexCache = CreateLineIndexCache(oneline, sourceMap);
        string? currentGroupFile = null;
        string currentLineDisplay = "<unknown>";

        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var fileKey = PathDisplayResolver.NormalizeFileKey(d.FilePath);

            if (!string.Equals(currentGroupFile, fileKey, StringComparison.Ordinal))
            {
                if (currentGroupFile is not null)
                {
                    writer.WriteUtf8("::endgroup::");
                    writer.WriteNewLine();
                }

                var fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                var escaped = EscapeGitHubCommandValue(fileDisplay);
                currentLineDisplay = escaped.StartsWith("::", StringComparison.Ordinal)
                    ? string.Concat(".", escaped)
                    : escaped;
                writer.WriteUtf8("::group::");
                writer.WriteUtf8(escaped);
                writer.WriteNewLine();
                currentGroupFile = fileKey;
            }

            WriteTextDiagnostic(writer, d, fileKey, currentLineDisplay, oneline, color: false, sourceMap, lineIndexCache);
        }

        writer.WriteUtf8("::endgroup::");
        writer.WriteNewLine();
    }

    private static void WriteText(
        Utf8Writer writer,
        IReadOnlyList<Diagnostic> diagnostics,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        string? pathBaseDirectory)
    {
        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        var lineIndexCache = CreateLineIndexCache(oneline, sourceMap);
        string? previousFileKey = null;
        string previousDisplayPath = "<unknown>";
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var fileKey = PathDisplayResolver.NormalizeFileKey(d.FilePath);
            string fileDisplay;
            if (string.Equals(previousFileKey, fileKey, StringComparison.Ordinal))
            {
                fileDisplay = previousDisplayPath;
            }
            else
            {
                fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                previousFileKey = fileKey;
                previousDisplayPath = fileDisplay;
            }
            WriteTextDiagnostic(writer, d, fileKey, fileDisplay, oneline, color, sourceMap, lineIndexCache);
        }
    }

    private static Dictionary<string, YamlLineIndex>? CreateLineIndexCache(
        bool oneline,
        IReadOnlyDictionary<string, byte[]>? sourceMap)
    {
        if (oneline || sourceMap is null)
        {
            return null;
        }

        return new Dictionary<string, YamlLineIndex>(StringComparer.Ordinal);
    }

    private const int GitHubEscapeStackLimit = 512;

    private static string EscapeGitHubCommandValue(string value)
    {
        var span = value.AsSpan();
        var firstEscapedIndex = span.IndexOfAny('%', '\r', '\n');
        if (firstEscapedIndex < 0)
        {
            return value;
        }

        if (value.Length <= GitHubEscapeStackLimit)
        {
            Span<char> buffer = stackalloc char[value.Length * 3];
            var written = WriteGitHubEscapedChars(span, buffer);
            return buffer[..written].ToString();
        }

        var builder = new StringBuilder(value.Length + 8);
        builder.Append(value, 0, firstEscapedIndex);
        AppendGitHubEscapedChars(span[firstEscapedIndex..], builder);
        return builder.ToString();
    }

    private static int WriteGitHubEscapedChars(ReadOnlySpan<char> value, Span<char> destination)
    {
        var written = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '%':
                    "%25".AsSpan().CopyTo(destination[written..]);
                    written += 3;
                    break;
                case '\r':
                    "%0D".AsSpan().CopyTo(destination[written..]);
                    written += 3;
                    break;
                case '\n':
                    "%0A".AsSpan().CopyTo(destination[written..]);
                    written += 3;
                    break;
                default:
                    destination[written++] = value[i];
                    break;
            }
        }

        return written;
    }

    private static void AppendGitHubEscapedChars(ReadOnlySpan<char> value, StringBuilder builder)
    {
        for (var i = 0; i < value.Length; i++)
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
    }

    private static void WriteTextDiagnostic(
        Utf8Writer writer,
        Diagnostic d,
        string fileKey,
        string fileDisplay,
        bool oneline,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        Dictionary<string, YamlLineIndex>? lineIndexCache)
    {
        var line = d.Location.StartLine;
        var col = d.Location.StartColumn;
        var severity = GetSeverityLowerString(d.Severity);
        var ruleId = d.RuleId ?? "parse";

        if (oneline)
        {
            WriteOnelineDiagnostic(writer, d, fileDisplay, line, col, severity, ruleId, color);
            return;
        }

        // Rich multi-line format (Rust-style)
        WriteRichDiagnostic(writer, d, fileKey, fileDisplay, line, col, severity, ruleId, color, sourceMap, lineIndexCache);
    }

    private static void WriteOnelineDiagnostic(
        Utf8Writer writer,
        Diagnostic d,
        string fileDisplay,
        int line,
        int col,
        string severity,
        string ruleId,
        bool color)
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

            writer.Write(bold);
            writer.Write(fileDisplay);
            writer.Write(':');
            writer.Write(line);
            writer.Write(':');
            writer.Write(col);
            writer.Write(':');
            writer.Write(reset);
            writer.Write(' ');
            writer.Write(severityColor);
            writer.Write(severity);
            writer.Write(reset);
            writer.Write(' ');
            writer.Write(dim);
            writer.Write('[');
            writer.Write(ruleId);
            writer.Write(']');
            writer.Write(reset);
            writer.Write(' ');
            writer.WriteLine(d.Message);
            return;
        }

        writer.Write(fileDisplay);
        writer.Write(':');
        writer.Write(line);
        writer.Write(':');
        writer.Write(col);
        writer.Write(": ");
        writer.Write(severity);
        writer.Write(" [");
        writer.Write(ruleId);
        writer.Write("] ");
        writer.WriteLine(d.Message);
    }

    private static void WriteRichDiagnostic(
        Utf8Writer writer,
        Diagnostic d,
        string sourceFileKey,
        string displayFile,
        int line,
        int col,
        string severity,
        string ruleId,
        bool color,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        Dictionary<string, YamlLineIndex>? lineIndexCache)
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
            writer.Write(severityColor);
            writer.Write(bold);
            writer.Write(severity);
            writer.Write('[');
            writer.Write(ruleId);
            writer.Write(']');
            writer.Write(reset);
            writer.Write(bold);
            writer.Write(": ");
            writer.Write(d.Message);
            writer.Write(reset);
            writer.WriteLine();

            // Location arrow: --> file:line:col
            writer.Write("  ");
            writer.Write(blue);
            writer.Write("-->");
            writer.Write(reset);
            writer.Write(' ');
            writer.Write(displayFile);
            writer.Write(':');
            writer.Write(line);
            writer.Write(':');
            writer.WriteLine(col);

            WriteContextSnippet(writer, d, sourceFileKey, sourceMap, lineIndexCache, color, severityColor, blue, reset, bold, dim);

            // Help text
            if (d.Help is not null)
            {
                writer.Write("   ");
                writer.Write(dim);
                writer.Write('=');
                writer.Write(reset);
                writer.Write(' ');
                writer.Write(bold);
                writer.Write("help");
                writer.Write(reset);
                writer.Write(": ");
                writer.WriteLine(d.Help);
            }

            writer.WriteLine();
        }
        else
        {
            // Header: error[rule-id]: message
            writer.Write(severity);
            writer.Write('[');
            writer.Write(ruleId);
            writer.Write("]: ");
            writer.WriteLine(d.Message);

            // Location arrow: --> file:line:col
            writer.Write("  --> ");
            writer.Write(displayFile);
            writer.Write(':');
            writer.Write(line);
            writer.Write(':');
            writer.WriteLine(col);

            WriteContextSnippet(writer, d, sourceFileKey, sourceMap, lineIndexCache, color, null, null, null, null, null);

            // Help text
            if (d.Help is not null)
            {
                writer.Write("   = help: ");
                writer.WriteLine(d.Help);
            }

            writer.WriteLine();
        }
    }

    private static void WriteContextSnippet(
        Utf8Writer writer,
        Diagnostic d,
        string file,
        IReadOnlyDictionary<string, byte[]>? sourceMap,
        Dictionary<string, YamlLineIndex>? lineIndexCache,
        bool color,
        string? severityColor,
        string? blue,
        string? reset,
        string? bold,
        string? dim)
    {
        if (sourceMap is not null
            && sourceMap.TryGetValue(file, out var sourceBytes)
            && d.Location.StartLine == d.Location.EndLine
            && TryWriteStructureContextSnippet(
                writer,
                d,
                sourceBytes,
                file,
                lineIndexCache,
                color,
                severityColor,
                blue,
                reset))
        {
            return;
        }

        WriteSourceSnippet(writer, d, file, sourceMap, color, severityColor, blue, reset, bold, dim);
    }

    private static bool TryWriteStructureContextSnippet(
        Utf8Writer writer,
        Diagnostic d,
        byte[] sourceBytes,
        string file,
        Dictionary<string, YamlLineIndex>? lineIndexCache,
        bool color,
        string? severityColor,
        string? blue,
        string? reset)
    {
        lineIndexCache ??= new Dictionary<string, YamlLineIndex>(1, StringComparer.Ordinal);
        if (!lineIndexCache.TryGetValue(file, out var cachedIndex))
        {
            cachedIndex = YamlLineIndex.Create(sourceBytes);
            lineIndexCache[file] = cachedIndex;
        }

        if (!StructureSnippetBuilder.TryBuild(sourceBytes, d, cachedIndex, out var lineIndex, out var lines)
            || lines.IsEmpty)
        {
            return false;
        }

        using var linesLease = lines;
        lineIndexCache[file] = lineIndex;

        var lastLineNumber = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines.Entries[i].IsEllipsis)
            {
                lastLineNumber = Math.Max(lastLineNumber, lines.Entries[i].LineNumber);
            }
        }

        var lineNumWidth = DecimalFormat.CountDigits(lastLineNumber);
        var caretLine = d.Location.StartLine;
        var hasCaretLineInEntries = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines.Entries[i].IsEllipsis && lines.Entries[i].LineNumber == caretLine)
            {
                hasCaretLineInEntries = true;
                break;
            }
        }

        if (!hasCaretLineInEntries)
        {
            caretLine = lines.HighlightLine1Based;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            ref readonly var entry = ref lines.Entries[i];
            if (entry.IsEllipsis)
            {
                writer.Write("   ");
                WriteRepeatedChar(writer, ' ', lineNumWidth);
                writer.Write(" | ...");
                writer.WriteNewLine();
                continue;
            }

            WriteGutterLine(writer, entry.LineNumber, lineNumWidth, entry.LineUtf8.Span, color, blue, reset);

            if (entry.LineNumber == caretLine)
            {
                WriteSingleLineCaret(
                    writer,
                    entry.LineUtf8.Span,
                    lineNumWidth,
                    d.Location.StartColumn,
                    d.Location.EndColumn,
                    color,
                    severityColor,
                    reset);
            }
        }

        return true;
    }

    private static void WriteSingleLineCaret(
        Utf8Writer writer,
        ReadOnlySpan<byte> sourceLine,
        int lineNumWidth,
        int startCol,
        int endCol,
        bool color,
        string? severityColor,
        string? reset)
    {
        var safeStart = Math.Max(1, startCol);
        var safeEnd = endCol >= safeStart ? endCol : safeStart;
        var prefixWidth = SourceDisplayWidth.GetWidthBeforeColumn(sourceLine, safeStart);
        var caretLen = Math.Max(
            1,
            SourceDisplayWidth.GetWidthBetweenColumnsInclusive(sourceLine, safeStart, safeEnd));

        writer.Write("   ");
        WriteRepeatedChar(writer, ' ', lineNumWidth);
        writer.Write(" | ");
        WriteRepeatedChar(writer, ' ', prefixWidth);
        if (color)
        {
            writer.Write(severityColor!);
            WriteRepeatedChar(writer, '^', caretLen);
            writer.Write(reset!);
        }
        else
        {
            WriteRepeatedChar(writer, '^', caretLen);
        }

        writer.WriteLine();
    }

    private static void WriteSourceSnippet(
        Utf8Writer writer,
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

        var lineCount = endLine - startLine + 1;
        var sourceSpan = sourceBytes.AsSpan();
        LineSlice[]? rentedSlices = null;
        var lineSlices = lineCount <= MaxStackLineSlices
            ? stackalloc LineSlice[lineCount]
            : (rentedSlices = ArrayPool<LineSlice>.Shared.Rent(lineCount)).AsSpan(0, lineCount);
        try
        {
            ExtractLineSlices(sourceSpan, startLine, endLine, lineSlices);

            var lineNumWidth = DecimalFormat.CountDigits(endLine);

            WriteGutterSeparator(writer, lineNumWidth);

            if (startLine == endLine)
            {
                // Single-line span
                var sourceLine = lineSlices[0].AsSpan(sourceSpan);
                WriteGutterLine(writer, startLine, lineNumWidth, sourceLine, color, blue, reset);

                // Underline caret: columns are 1-based byte positions; pad by terminal display width.
                var safeStart = Math.Max(1, startCol);
                var safeEnd = endCol >= safeStart ? endCol : safeStart;
                var prefixWidth = SourceDisplayWidth.GetWidthBeforeColumn(sourceLine, safeStart);
                var caretLen = Math.Max(
                    1,
                    SourceDisplayWidth.GetWidthBetweenColumnsInclusive(sourceLine, safeStart, safeEnd));

                writer.Write("   ");
                WriteRepeatedChar(writer, ' ', lineNumWidth);
                writer.Write(" | ");
                WriteRepeatedChar(writer, ' ', prefixWidth);
                if (color)
                {
                    writer.Write(severityColor!);
                    WriteRepeatedChar(writer, '^', caretLen);
                    writer.Write(reset!);
                }
                else
                {
                    WriteRepeatedChar(writer, '^', caretLen);
                }

                writer.WriteLine();
            }
            else
            {
                // Multi-line span: show opening line with /  and closing line with \___^
                for (var li = 0; li < lineSlices.Length; li++)
                {
                    var ln = startLine + li;
                    var prefix = li == 0 ? "/ " : "| ";
                    WriteGutterLineWithPrefix(
                        writer,
                        ln,
                        lineNumWidth,
                        prefix,
                        lineSlices[li].AsSpan(sourceSpan),
                        color,
                        blue,
                        reset);
                }

                // Closing underline on the last line (columns 2..endCol inclusive).
                var lastLine = lineSlices[^1].AsSpan(sourceSpan);
                var closingEndColumn = Math.Max(2, endCol);
                var closingCaretLen = Math.Max(
                    1,
                    SourceDisplayWidth.GetWidthBetweenColumnsInclusive(lastLine, 2, closingEndColumn));
                writer.Write("   ");
                WriteRepeatedChar(writer, ' ', lineNumWidth);
                writer.Write(" | ");
                if (color)
                {
                    writer.Write(severityColor!);
                    writer.Write("|_");
                    WriteRepeatedChar(writer, '^', closingCaretLen);
                    writer.Write(reset!);
                }
                else
                {
                    writer.Write("|_");
                    WriteRepeatedChar(writer, '^', closingCaretLen);
                }

                writer.WriteLine();
            }

            WriteGutterSeparator(writer, lineNumWidth);
        }
        finally
        {
            if (rentedSlices is not null)
            {
                ArrayPool<LineSlice>.Shared.Return(rentedSlices);
            }
        }
    }

    private static void WriteGutterSeparator(Utf8Writer writer, int lineNumWidth)
    {
        writer.Write("   ");
        WriteRepeatedChar(writer, ' ', lineNumWidth);
        writer.WriteLine(" |");
    }

    private static void WriteGutterLine(Utf8Writer writer, int lineNum, int width, ReadOnlySpan<byte> sourceLine, bool color, string? blue, string? reset)
    {
        writer.Write("   ");
        if (color)
        {
            writer.Write(blue!);
            WritePaddedDecimal(writer, lineNum, width);
            writer.Write(reset!);
        }
        else
        {
            WritePaddedDecimal(writer, lineNum, width);
        }

        writer.Write(" | ");
        writer.WriteLiteral(sourceLine);
        writer.WriteNewLine();
    }

    private static void WriteGutterLineWithPrefix(
        Utf8Writer writer,
        int lineNum,
        int width,
        string prefix,
        ReadOnlySpan<byte> sourceLine,
        bool color,
        string? blue,
        string? reset)
    {
        writer.Write("   ");
        if (color)
        {
            writer.Write(blue!);
            WritePaddedDecimal(writer, lineNum, width);
            writer.Write(reset!);
        }
        else
        {
            WritePaddedDecimal(writer, lineNum, width);
        }

        writer.Write(" |");
        writer.Write(prefix);
        writer.WriteLiteral(sourceLine);
        writer.WriteNewLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRepeatedChar(Utf8Writer writer, char c, int count)
        => writer.WriteRepeated((byte)c, count);

    private static void WritePaddedDecimal(Utf8Writer writer, int value, int minWidth)
        => writer.WritePaddedDecimal(value, minWidth);

    private const int MaxStackLineSlices = 16;

    private readonly record struct LineSlice(int Start, int Length)
    {
        public ReadOnlySpan<byte> AsSpan(ReadOnlySpan<byte> source) => source.Slice(Start, Length);
    }

    private static void ExtractLineSlices(ReadOnlySpan<byte> utf8, int startLine, int endLine, Span<LineSlice> results)
    {
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
                    var len = i - lineStart;
                    if (len > 0 && utf8[lineStart + len - 1] == (byte)'\r')
                    {
                        len--;
                    }

                    results[resultIdx++] = new LineSlice(lineStart, len);
                }

                if (resultIdx == results.Length)
                {
                    break;
                }

                currentLine++;
                lineStart = i + 1;
            }
        }

        for (var j = resultIdx; j < results.Length; j++)
        {
            results[j] = default;
        }
    }

    private static void WriteJson(IBufferWriter<byte> output, Utf8Writer writer, IReadOnlyList<Diagnostic> diagnostics, string? pathBaseDirectory)
    {
        var pathResolver = new PathDisplayResolver(pathBaseDirectory);
        using var json = new Utf8JsonWriter(output, JsonDiagnosticWriterOptions);

        json.WriteStartArray();
        string? previousFileKey = null;
        string previousDisplayPath = "<unknown>";
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var fileKey = PathDisplayResolver.NormalizeFileKey(d.FilePath);
            string fileDisplay;
            if (string.Equals(previousFileKey, fileKey, StringComparison.Ordinal))
            {
                fileDisplay = previousDisplayPath;
            }
            else
            {
                fileDisplay = pathResolver.GetDisplayPath(d.FilePath);
                previousFileKey = fileKey;
                previousDisplayPath = fileDisplay;
            }

            json.WriteStartObject();
            json.WriteString("file"u8, fileDisplay);
            json.WriteNumber("line"u8, d.Location.StartLine);
            json.WriteNumber("col"u8, d.Location.StartColumn);
            json.WriteString("severity"u8, GetSeverityLowerString(d.Severity));
            json.WriteString("ruleId"u8, d.RuleId ?? "parse");
            json.WriteString("message"u8, d.Message);
            json.WriteBoolean("fixable"u8, d.Fix is not null);
            if (d.Help is not null)
            {
                json.WriteString("help"u8, d.Help);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.Flush();

        writer.WriteNewLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetSeverityLowerString(DiagnosticSeverity severity)
    {
        var index = (int)severity;
        return (uint)index < (uint)SeverityLowerStrings.Length
            ? SeverityLowerStrings[index]
            : severity.ToString().ToLowerInvariant();
    }

    private static void WriteSarif(IBufferWriter<byte> output, Utf8Writer writer, IReadOnlyList<Diagnostic> diagnostics, string? pathBaseDirectory)
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

        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true, Indented = true });

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
        string? previousFileKey = null;
        SarifArtifactLocation? previousArtifactLocation = null;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            var ruleId = d.RuleId ?? "parse";
            var fileKey = PathDisplayResolver.NormalizeFileKey(d.FilePath);
            SarifArtifactLocation artifactLocation;
            if (string.Equals(previousFileKey, fileKey, StringComparison.Ordinal) && previousArtifactLocation is not null)
            {
                artifactLocation = previousArtifactLocation;
            }
            else
            {
                artifactLocation = pathResolver.ResolveSarifArtifactLocation(d.FilePath);
                previousFileKey = fileKey;
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

        writer.WriteNewLine();
    }

    private static string BuildSarifRuleHelpUri(string ruleId)
    {
        if (string.Equals(ruleId, "parse", StringComparison.Ordinal))
            return SarifGeneralHelpUri;

        return string.Concat(SarifRuleHelpUriPrefix, ruleId);
    }
}

// --- Source-generated JSON context for NativeAOT (rules command) ---

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
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (sizeHint == 0)
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
