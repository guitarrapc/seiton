using Seiton.Core.Parsing;
using Seiton.Output;
using System.Buffers;
using System.Text.Json;

namespace Seiton.Tests;

public sealed class DiagnosticFormatterRichTextTests
{
    [Test]
    public async Task Write_Buffer_MatchesTextWriterAdapter_OnelineError()
        => await AssertBufferMatchesTextWriterAdapter(OutputFormat.Text, oneline: true, color: false);

    [Test]
    public async Task Write_Buffer_MatchesTextWriterAdapter_Json()
        => await AssertBufferMatchesTextWriterAdapter(OutputFormat.Json, oneline: false, color: false);

    [Test]
    public async Task Write_Buffer_MatchesTextWriterAdapter_Sarif()
        => await AssertBufferMatchesTextWriterAdapter(OutputFormat.Sarif, oneline: false, color: false);

    [Test]
    public async Task Write_Buffer_MatchesTextWriterAdapter_GitHubActionsOneline()
        => await AssertBufferMatchesTextWriterAdapter(OutputFormat.GitHubActions, oneline: true, color: false);

    private static async Task AssertBufferMatchesTextWriterAdapter(OutputFormat format, bool oneline, bool color)
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "buffer path", 4, 2, 4, 8);
        var buffer = new ArrayBufferWriter<byte>();
        DiagnosticFormatter.Write(buffer, [diag], format, oneline, color);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], format, oneline, color);
        writer.Flush();

        await Assert.That(Encoding.UTF8.GetString(buffer.WrittenSpan)).IsEqualTo(sb.ToString());
    }

    // --oneline format

    [Test]
    public async Task Oneline_Error_EmitsSingleLine()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "bad thing", 10, 5, 10, 10);
        var output = Render(diag, oneline: true);

        await Assert.That(output.TrimEnd()).IsEqualTo("test.yml:10:5: error [test-rule] bad thing");
    }

    [Test]
    public async Task Oneline_Warning_EmitsSingleLine()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "suspicious thing", 3, 1, 3, 8);
        var output = Render(diag, oneline: true);

        await Assert.That(output.TrimEnd()).IsEqualTo("test.yml:3:1: warning [test-rule] suspicious thing");
    }

    [Test]
    public async Task Oneline_NullRuleId_UsesSyntaxCheckLabel()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "parse error", 1, 1, 1, 5, ruleId: null);
        var output = Render(diag, oneline: true);

        await Assert.That(output.TrimEnd()).IsEqualTo("test.yml:1:1: error [syntax-check] parse error");
    }

    [Test]
    public async Task Oneline_MultipleDignostics_EachOnOwnLine()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Error, "first", 1, 1, 1, 5),
            MakeDiagnostic(DiagnosticSeverity.Warning, "second", 2, 3, 2, 8),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.Text, oneline: true, color: false);
        writer.Flush();
        var lines = sb.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("test.yml:1:1: error [test-rule] first");
        await Assert.That(lines[1]).IsEqualTo("test.yml:2:3: warning [test-rule] second");
    }

    // Rich format — header line

    [Test]
    public async Task Rich_Error_HeaderContainsSeverityRuleIdAndMessage()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "job omits permissions", 5, 3, 5, 10, ruleId: "job-permissions-required");
        var output = Render(diag);

        await Assert.That(output).Contains("error[job-permissions-required]: job omits permissions");
    }

    [Test]
    public async Task Rich_Warning_HeaderContainsWarningSeverity()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "unpinned action", 1, 1, 1, 20);
        var output = Render(diag);

        await Assert.That(output).Contains("warning[test-rule]: unpinned action");
    }

    [Test]
    public async Task Rich_LocationArrow_ContainsFileLineCol()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 12, 5, 12, 15, filePath: ".github/workflows/ci.yml");
        var output = Render(diag);

        await Assert.That(output).Contains("--> .github/workflows/ci.yml:12:5");
    }

    [Test]
    public async Task Rich_GutterBar_AlwaysEmitted()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 1, 1, 5);
        var output = Render(diag);

        // At minimum the gutter | separator must appear
        await Assert.That(output).Contains("|");
    }

    [Test]
    public async Task Rich_SourceSnippet_GutterSeparator_EmitsPipeNotAsciiCode()
    {
        var source = "on: push\njobs:\n  build:\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 2, 1, 2, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        // '|' is ASCII 124; WriteLine('|') must not emit the numeric code as decimal text.
        await Assert.That(output).DoesNotContain("\n    124\n");
        await Assert.That(output).Contains("     |\n");
        await Assert.That(output).Contains(" 2 | jobs:");
    }

    [Test]
    public async Task Rich_SourceSnippet_MultiLineSpan_GutterSeparators_EmitPipeNotAsciiCode()
    {
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "multi", 2, 1, 3, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).DoesNotContain("124");
        await Assert.That(output).Contains("     |\n");
        await Assert.That(output.Split("     |\n", StringSplitOptions.None).Length).IsEqualTo(3);
    }

    [Test]
    public async Task Rich_SourceSnippet_WideLineNumber_GutterSeparator_EmitsPipeNotAsciiCode()
    {
        var lines = Enumerable.Range(1, 120).Select(i => $"line-{i:000}");
        var source = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 100, 1, 100, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).DoesNotContain("\n      124\n");
        await Assert.That(output).Contains("       |\n");
        await Assert.That(output).Contains("100 | line-100");
    }

    [Test]
    public async Task Rich_SourceSnippet_GutterPipeColumn_Aligned_SingleDigitLineNumber()
    {
        var source = "on: push\njobs:\n  build:\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 2, 1, 2, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await AssertGutterPipeColumnsAligned(output);
    }

    [Test]
    public async Task Rich_SourceSnippet_GutterPipeColumn_Aligned_DoubleDigitLineNumber()
    {
        var lines = Enumerable.Range(1, 15).Select(i => $"line-{i}");
        var source = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 10, 1, 10, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await AssertGutterPipeColumnsAligned(output);
    }

    [Test]
    public async Task Rich_SourceSnippet_GutterPipeColumn_Aligned_TripleDigitLineNumber()
    {
        var lines = Enumerable.Range(1, 120).Select(i => $"line-{i:000}");
        var source = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 100, 1, 100, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await AssertGutterPipeColumnsAligned(output);
    }

    [Test]
    public async Task Rich_SourceSnippet_GutterPipeColumn_Aligned_MultiLineSpan()
    {
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "multi", 2, 1, 3, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await AssertGutterPipeColumnsAligned(output);
    }

    // Rich format — help annotation

    [Test]
    public async Task Rich_Help_IsEmittedWhenSet()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 1, 1, 5, help: "use explicit permissions instead");
        var output = Render(diag);

        await Assert.That(output).Contains("help: use explicit permissions instead");
    }

    [Test]
    public async Task Rich_Help_IsAbsentWhenNull()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 1, 1, 5, help: null);
        var output = Render(diag);

        await Assert.That(output).DoesNotContain("help:");
    }

    // Rich format — source snippet

    [Test]
    public async Task Rich_SourceSnippet_ShowsReferencedLine()
    {
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        // line 4 is "    runs-on: ubuntu-latest", col 5..14
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "known label", 4, 5, 4, 14, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("    runs-on: ubuntu-latest");
    }

    [Test]
    public async Task Rich_SourceSnippet_ShowsLineNumber()
    {
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 4, 5, 4, 14, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("4 |");
    }

    [Test]
    public async Task Rich_SourceSnippet_CaretLengthMatchesColumnSpan()
    {
        // "  build:" on line 3. col 3..8 (inclusive) → span of 6 chars
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 3, 3, 3, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        // Caret row must have exactly 6 carets (8 - 3 + 1 = 6)
        await Assert.That(output).Contains("^^^^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_RealisticTemplateExpression_CaretCoversWholeToken()
    {
        var source = "      - run: echo \"title is ${{ github.event.pull_request.title }}\"\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["t.yml"] = source };

        // "github.event.pull_request.title" starts at column 33 and has length 31 bytes.
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 33, 1, 63, filePath: "t.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).Contains("^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_TabPrefix_CaretAlignedToDisplayWidth()
    {
        var source = "\tfoo\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["t.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 2, 1, 5, filePath: "t.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).Contains(" 1 | \tfoo");
        await Assert.That(output).Contains("     |     ^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_WideCharacters_CaretAlignedToDisplayWidth()
    {
        var source = Encoding.UTF8.GetBytes("# 日本\n");
        var sourceMap = new Dictionary<string, byte[]> { ["t.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 3, 1, 9, filePath: "t.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).Contains(" 1 | # 日本");
        await Assert.That(output).Contains("     |   ^^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_MultiLineSpan_ClosingCaretUsesDisplayWidth()
    {
        var source = Encoding.UTF8.GetBytes("start\n\tend\n");
        var sourceMap = new Dictionary<string, byte[]> { ["t.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "multi", 1, 1, 2, 4, filePath: "t.yml");
        var output = Render(diag, sourceMap: sourceMap).ReplaceLineEndings("\n");

        await Assert.That(output).Contains("2 || \tend");
        await Assert.That(output).Contains("     | |_^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_MinimumOneCaret_WhenStartEqualsEnd()
    {
        var source = "on: push\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        // StartCol == EndCol (inclusive point range) → exactly 1 caret
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 4, 1, 4, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("     |    ^");
        await Assert.That(output).DoesNotContain("     |    ^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_NoSource_StillEmitsGutterBar()
    {
        // No source map provided — snippet should gracefully degrade
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 5, 1, 5, 10);
        var output = Render(diag, sourceMap: null);

        // Header and location arrow must still appear
        await Assert.That(output).Contains("error[test-rule]: msg");
        await Assert.That(output).Contains("--> test.yml:5:1");
        await Assert.That(output).Contains("|");
    }

    [Test]
    public async Task Rich_SourceSnippet_FileNotInSourceMap_StillEmitsGutterBar()
    {
        var sourceMap = new Dictionary<string, byte[]> { ["other.yml"] = "x\n"u8.ToArray() };
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 1, 1, 5, filePath: "missing.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("--> missing.yml:1:1");
        await Assert.That(output).Contains("|");
    }

    [Test]
    public async Task Rich_SourceSnippet_MultiLineSpan_ShowsAllLines()
    {
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        // span from line 2 to line 3
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "multi", 2, 1, 3, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("jobs:");
        await Assert.That(output).Contains("  build:");
    }

    [Test]
    public async Task Rich_SourceSnippet_LastLineOfFile_NoOutOfRange()
    {
        // File ends without trailing newline; diagnostic points at last line
        var source = Encoding.UTF8.GetBytes("on: push");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 1, 1, 1, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("on: push");
    }

    [Test]
    public async Task Rich_SourceSnippet_LineNumberBeyondFile_GracefulDegradation()
    {
        // Source has only 1 line but diagnostic says line 99
        var source = Encoding.UTF8.GetBytes("on: push");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 99, 1, 99, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        // Must not throw; must emit at least location arrow
        await Assert.That(output).Contains("--> ci.yml:99:1");
    }

    [Test]
    public async Task Rich_SourceSnippet_CrLfLines_HandledCorrectly()
    {
        // Windows-style line endings
        var source = Encoding.UTF8.GetBytes("on: push\r\njobs:\r\n  build:\r\n");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "msg", 2, 1, 2, 5, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        // The extracted line should not contain \r
        await Assert.That(output).Contains("jobs:");
        // Verify the source \r was stripped: if it wasn't, we'd see \r\r (source \r + WriteLine \r\n)
        await Assert.That(output).DoesNotContain("\r\r");
    }

    [Test]
    public async Task Rich_SourceSnippet_MultiLineSpan_OverStackLimit_UsesPooledPath()
    {
        // Use a sufficiently large span so this remains a pooled-path test
        // even if the internal stack threshold changes.
        const int lineCount = 256;
        var lines = Enumerable.Range(1, lineCount).Select(i => $"line-{i:000}");
        var source = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "pooled span", 1, 1, lineCount, 7, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("line-001");
        await Assert.That(output).Contains("line-256");
        await Assert.That(output).Contains("256 |");
    }

    // Rich format — blank line between diagnostics

    [Test]
    public async Task Rich_MultipleDiagnostics_SeparatedByBlankLine()
    {
        var source = "on: push\njobs:\n  build:\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Error, "first error", 1, 1, 1, 8, filePath: "ci.yml"),
            MakeDiagnostic(DiagnosticSeverity.Warning, "second warning", 2, 1, 2, 5, filePath: "ci.yml"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.Text, oneline: false, color: false, sourceMap);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("error[test-rule]: first error");
        await Assert.That(output).Contains("warning[test-rule]: second warning");
    }

    // Format routing — JSON and SARIF are unaffected by sourceMap

    [Test]
    public async Task Json_Format_NotAffectedBySourceMap()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "json test", 1, 1, 1, 5);
        var sourceMap = new Dictionary<string, byte[]> { ["test.yml"] = "on: push\n"u8.ToArray() };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false, sourceMap);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("\"severity\":\"error\"");
        await Assert.That(output).Contains("\"message\":\"json test\"");
    }

    [Test]
    public async Task Json_Format_IncludesHelpWhenPresent()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "test msg", 1, 1, 1, 5, help: "add config snippet");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("\"help\":\"add config snippet\"");
    }

    [Test]
    public async Task Json_Format_OmitsHelpWhenNull()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "test msg", 1, 1, 1, 5);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).DoesNotContain("\"help\"");
    }

    [Test]
    public async Task Json_Format_EmitsExpectedFields()
    {
        var diag = MakeDiagnostic(
            DiagnosticSeverity.Error,
            "job omits permissions",
            12,
            5,
            12,
            40,
            ruleId: "job-permissions-required",
            filePath: ".github/workflows/build.yml");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var root = doc.RootElement;
        await Assert.That(root.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(root.GetArrayLength()).IsEqualTo(1);

        var entry = root[0];
        await Assert.That(entry.GetProperty("file").GetString()).IsEqualTo(".github/workflows/build.yml");
        await Assert.That(entry.GetProperty("line").GetInt32()).IsEqualTo(12);
        await Assert.That(entry.GetProperty("col").GetInt32()).IsEqualTo(5);
        await Assert.That(entry.GetProperty("severity").GetString()).IsEqualTo("error");
        await Assert.That(entry.GetProperty("ruleId").GetString()).IsEqualTo("job-permissions-required");
        await Assert.That(entry.GetProperty("message").GetString()).IsEqualTo("job omits permissions");
        await Assert.That(entry.GetProperty("fixable").GetBoolean()).IsEqualTo(false);
        await Assert.That(entry.TryGetProperty("help", out _)).IsEqualTo(false);
    }

    [Test]
    public async Task Json_Format_Severity_UsesExpectedLowercaseLabels()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic((DiagnosticSeverity)0, "info msg", 1, 1, 1, 2, filePath: "info.yml"),
            MakeDiagnostic((DiagnosticSeverity)1, "warning msg", 2, 1, 2, 2, filePath: "warning.yml"),
            MakeDiagnostic((DiagnosticSeverity)2, "error msg", 3, 1, 3, 2, filePath: "error.yml"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.Json, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var entries = doc.RootElement;
        await Assert.That(entries[0].GetProperty("severity").GetString()).IsEqualTo("info");
        await Assert.That(entries[1].GetProperty("severity").GetString()).IsEqualTo("warning");
        await Assert.That(entries[2].GetProperty("severity").GetString()).IsEqualTo("error");
    }

    [Test]
    public async Task Json_Format_Severity_OutOfRangeEnum_UsesFallbackString()
    {
        var diag = MakeDiagnostic((DiagnosticSeverity)999, "unknown severity", 1, 1, 1, 2);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var severity = doc.RootElement[0].GetProperty("severity").GetString();
        await Assert.That(severity).IsEqualTo("999");
    }

    [Test]
    public async Task Json_Format_EmptyDiagnostics_EmitsEmptyArray()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();

        await Assert.That(sb.ToString()).IsEqualTo("[]" + Environment.NewLine);
    }

    [Test]
    public async Task Sarif_Format_IncludesHelpInMessage()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "unpinned action", 5, 11, 5, 30, help: "to ignore this owner, add config");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("unpinned action\\n\\nHelp: to ignore this owner, add config");
    }

    [Test]
    public async Task Sarif_Format_MessageWithoutHelp()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "plain error", 1, 1, 1, 5);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();
        using var doc = JsonDocument.Parse(sb.ToString());
        var text = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("message")
            .GetProperty("text")
            .GetString();

        await Assert.That(text).IsEqualTo("plain error");
    }

    [Test]
    public async Task Sarif_Format_WindowsAbsolutePath_EmitsRelativeUriWithBaseId()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-sarif-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, ".github", "workflows", "ci with space.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "on: push\n");

            var diag = MakeDiagnostic(
                DiagnosticSeverity.Warning,
                "uri test",
                2,
                3,
                2,
                8,
                filePath: target);

            var sb = new StringBuilder();
            using var writer = new StringWriter(sb);
            DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false, sourceMap: null, pathBaseDirectory: baseDir);
            writer.Flush();

            using var doc = JsonDocument.Parse(sb.ToString());
            var location = doc.RootElement
                .GetProperty("runs")[0]
                .GetProperty("results")[0]
                .GetProperty("locations")[0]
                .GetProperty("physicalLocation")
                .GetProperty("artifactLocation");

            var uri = location.GetProperty("uri").GetString();
            var uriBaseId = location.GetProperty("uriBaseId").GetString();

            await Assert.That(uri).IsEqualTo(".github/workflows/ci%20with%20space.yml");
            await Assert.That(uriBaseId).IsEqualTo(PathDisplayResolver.SarifWorkingDirectoryBaseId);

            var originalUriBaseIds = doc.RootElement
                .GetProperty("runs")[0]
                .GetProperty("originalUriBaseIds");
            await Assert.That(originalUriBaseIds.TryGetProperty(PathDisplayResolver.SarifWorkingDirectoryBaseId, out _)).IsTrue();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task Json_Format_EmptyOrWhitespaceFilePath_UsesUnknownSentinel()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(
            writer,
            [
                MakeDiagnostic(DiagnosticSeverity.Warning, "empty path", 1, 1, 1, 3, filePath: ""),
                MakeDiagnostic(DiagnosticSeverity.Warning, "whitespace path", 2, 1, 1, 3, filePath: "   "),
            ],
            OutputFormat.Json,
            oneline: false,
            color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var entries = doc.RootElement;
        await Assert.That(entries[0].GetProperty("file").GetString()).IsEqualTo("<unknown>");
        await Assert.That(entries[1].GetProperty("file").GetString()).IsEqualTo("<unknown>");
    }

    [Test]
    public async Task Sarif_Format_UnknownPath_UsesSafeFileUri()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "unknown path", 1, 1, 1, 3, filePath: null);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var uri = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation")
            .GetProperty("uri")
            .GetString();

        await Assert.That(uri).IsEqualTo("file:///unknown");
    }

    [Test]
    public async Task Sarif_Format_StdinSentinel_DoesNotEmitOriginalUriBaseIds()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "stdin path", 1, 1, 1, 3, filePath: "<stdin>");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var run = doc.RootElement.GetProperty("runs")[0];
        var location = run
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation");

        await Assert.That(location.GetProperty("uri").GetString()).IsEqualTo(PathDisplayResolver.StdinSarifUri);
        await Assert.That(location.TryGetProperty("uriBaseId", out _)).IsFalse();
        await Assert.That(run.TryGetProperty("originalUriBaseIds", out _)).IsFalse();
    }

    [Test]
    public async Task Sarif_Format_AbsoluteUriInput_DoesNotEmitOriginalUriBaseIds()
    {
        var diag = MakeDiagnostic(
            DiagnosticSeverity.Warning,
            "remote source",
            1,
            1,
            1,
            3,
            filePath: "https://example.com/repo/.github/workflows/ci.yml");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var run = doc.RootElement.GetProperty("runs")[0];
        await Assert.That(run.TryGetProperty("originalUriBaseIds", out _)).IsFalse();
    }

    [Test]
    public async Task Json_Format_AbsolutePath_EmitsRelativeDisplayPath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-json-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, ".github", "workflows", "ci.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "on: push\n");

            var diag = MakeDiagnostic(DiagnosticSeverity.Error, "json test", 1, 1, 1, 5, filePath: target);

            var sb = new StringBuilder();
            using var writer = new StringWriter(sb);
            DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Json, oneline: false, color: false, sourceMap: null, pathBaseDirectory: baseDir);
            writer.Flush();

            await Assert.That(sb.ToString()).Contains("\"file\":\".github/workflows/ci.yml\"");
            await Assert.That(sb.ToString()).DoesNotContain(Path.GetFullPath(target));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task Text_Oneline_AbsolutePath_EmitsRelativeDisplayPath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seiton-text-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var target = Path.Combine(baseDir, "workflow.yml");
            await File.WriteAllTextAsync(target, "on: push\n");

            var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "relative path", 1, 1, 1, 5, filePath: target);

            var sb = new StringBuilder();
            using var writer = new StringWriter(sb);
            DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Text, oneline: true, color: false, sourceMap: null, pathBaseDirectory: baseDir);
            writer.Flush();

            await Assert.That(sb.ToString().TrimEnd()).IsEqualTo("workflow.yml:1:1: warning [test-rule] relative path");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task Sarif_Format_Driver_IncludesVersionMetadata()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "version test", 1, 1, 1, 5);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var driver = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver");

        var hasVersion = driver.TryGetProperty("version", out var versionElement);
        await Assert.That(hasVersion).IsTrue();
        await Assert.That(versionElement.GetString()).IsNotNull();
        await Assert.That(versionElement.GetString()).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task Sarif_Format_Rules_IncludeHelpUriMetadata()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, ruleId: "runner-no-latest"),
            MakeDiagnostic(DiagnosticSeverity.Error, "second", 2, 1, 2, 5, ruleId: "unpinned-uses"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var rules = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver")
            .GetProperty("rules");

        await Assert.That(rules.GetArrayLength()).IsEqualTo(2);

        foreach (var rule in rules.EnumerateArray())
        {
            var id = rule.GetProperty("id").GetString();
            var hasHelpUri = rule.TryGetProperty("helpUri", out var helpUriElement);
            await Assert.That(hasHelpUri).IsTrue();
            await Assert.That(helpUriElement.GetString()).IsEqualTo($"https://github.com/guitarrapc/seiton/blob/main/docs/rules.md#{id}");
        }
    }

    [Test]
    public async Task Sarif_Format_ParseRule_UsesGeneralUsageHelpUri()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "parse error", 1, 1, 1, 1, ruleId: null);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var helpUri = doc.RootElement
            .GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver")
            .GetProperty("rules")[0]
            .GetProperty("helpUri")
            .GetString();

        await Assert.That(helpUri).IsEqualTo("https://github.com/guitarrapc/seiton/blob/main/docs/usage.md");
    }

    [Test]
    public async Task Sarif_Format_UsesOfficialOasisSchemaUrl()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "schema test", 1, 1, 1, 3);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var schema = doc.RootElement.GetProperty("$schema").GetString();

        await Assert.That(schema).IsEqualTo("https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json");
    }

    [Test]
    public async Task Sarif_Format_IsPrettyPrinted()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "pretty", 1, 1, 1, 3);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString().ReplaceLineEndings("\n");

        await Assert.That(output).Contains("\n  \"$schema\": ");
        await Assert.That(output).Contains("\n  \"runs\": [\n    {\n      \"tool\": {");
    }

    [Test]
    public async Task GitHubActions_Format_DoesNotEmitAnsi_WhenColorRequested()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "plain error", 1, 1, 1, 5);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, [diag], OutputFormat.GitHubActions, oneline: false, color: true);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("error[test-rule]: plain error");
        await Assert.That(output).DoesNotContain("\u001b[");
    }

    [Test]
    public async Task GitHubActions_Format_Oneline_EmitsGroupedDiagnosticsPerFile()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Error, "first", 1, 1, 1, 5, filePath: "a.yml"),
            MakeDiagnostic(DiagnosticSeverity.Warning, "second", 2, 1, 2, 4, filePath: "a.yml"),
            MakeDiagnostic(DiagnosticSeverity.Warning, "third", 3, 1, 3, 4, filePath: "b.yml"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("::group::a.yml");
        await Assert.That(output).Contains("::group::b.yml");
        await Assert.That(output).Contains("::endgroup::");
        await Assert.That(output).Contains("a.yml:1:1: error [test-rule] first");
        await Assert.That(output).Contains("a.yml:2:1: warning [test-rule] second");
        await Assert.That(output).Contains("b.yml:3:1: warning [test-rule] third");
    }

    [Test]
    public async Task GitHubActions_Format_Rich_EmitsGroupedDiagnosticsPerFile()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Error, "first", 1, 1, 1, 5, filePath: "a.yml"),
            MakeDiagnostic(DiagnosticSeverity.Warning, "second", 2, 1, 2, 4, filePath: "b.yml"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("::group::a.yml");
        await Assert.That(output).Contains("::group::b.yml");
        await Assert.That(output).Contains("error[test-rule]: first");
        await Assert.That(output).Contains("warning[test-rule]: second");
    }

    [Test]
    public async Task GitHubActions_Format_GroupTitle_EscapesWorkflowCommandSpecialCharacters()
    {
        var filePath = "a%25\r\nb.yml";
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, filePath: filePath),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("::group::a%2525%0D%0Ab.yml");
        await Assert.That(output).DoesNotContain("::group::a%25\r\nb.yml");
        await Assert.That(output).Contains("a%2525%0D%0Ab.yml:1:1: warning [test-rule] first");
        await Assert.That(output).DoesNotContain("a%25\r\nb.yml:1:1: warning [test-rule] first");
    }

    [Test]
    public async Task GitHubActions_Format_Oneline_EscapesFilePathControlCharactersInDiagnosticBody()
    {
        var filePath = "a\r\n::warning::owned";
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, filePath: filePath),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("a%0D%0A::warning::owned:1:1: warning [test-rule] first");
        await Assert.That(output).DoesNotContain("a\r\n::warning::owned:1:1: warning [test-rule] first");
    }

    [Test]
    public async Task GitHubActions_Format_Rich_EscapesFilePathControlCharactersInLocationLine()
    {
        var filePath = "a\r\n::warning::owned";
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, filePath: filePath),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("--> a%0D%0A::warning::owned:1:1");
        await Assert.That(output).DoesNotContain("--> a\r\n::warning::owned:1:1");
    }

    [Test]
    public async Task GitHubActions_Format_Oneline_LeadingWorkflowCommandPrefix_IsNeutralized()
    {
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, filePath: "::warning::owned"),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains(".::warning::owned:1:1: warning [test-rule] first");
        await Assert.That(output).DoesNotContain("\n::warning::owned:1:1: warning [test-rule] first");
    }

    // Helpers
    private static Diagnostic MakeDiagnostic(
        DiagnosticSeverity severity,
        string message,
        int startLine,
        int startCol,
        int endLine,
        int endCol,
        string? ruleId = "test-rule",
        string? filePath = "test.yml",
        string? help = null) =>
        new(
            Severity: severity,
            Message: message,
            Location: new TextRange(0, 0, startLine, startCol, endLine, endCol),
            RuleId: ruleId,
            FilePath: filePath,
            Help: help);

    private static string Render(
        Diagnostic diagnostic,
        bool oneline = false,
        bool color = false,
        OutputFormat format = OutputFormat.Text,
        Dictionary<string, byte[]>? sourceMap = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        DiagnosticFormatter.Write(
            buffer,
            [diagnostic],
            format,
            oneline,
            color,
            sourceMap);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Every snippet gutter row (separators, source lines, caret rows) must place
    /// <c>|</c> at the same column. Uses the first pipe on each row so multi-line
    /// continuation rows (<c>N || …</c>) still compare the gutter pipe only.
    /// </summary>
    private static async Task AssertGutterPipeColumnsAligned(string output)
    {
        var gutterLines = output
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(static line => line.StartsWith("   ", StringComparison.Ordinal) && line.Contains('|'))
            .ToArray();

        await Assert.That(gutterLines.Length).IsGreaterThan(1);

        var pipeColumns = gutterLines.Select(static line => line.IndexOf('|')).ToArray();
        var expectedColumn = pipeColumns[0];
        for (var i = 1; i < pipeColumns.Length; i++)
        {
            await Assert.That(pipeColumns[i]).IsEqualTo(expectedColumn);
        }
    }
}
