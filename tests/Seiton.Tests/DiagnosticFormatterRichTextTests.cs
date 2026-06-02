using Seiton.Core.Parsing;
using Seiton.Output;
using System.Text.Json;

namespace Seiton.Tests;

public sealed class DiagnosticFormatterRichTextTests
{
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
    public async Task Oneline_NullRuleId_UsesParseLabel()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "parse error", 1, 1, 1, 5, ruleId: null);
        var output = Render(diag, oneline: true);

        await Assert.That(output.TrimEnd()).IsEqualTo("test.yml:1:1: error [parse] parse error");
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.Text, oneline: true, color: false);
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
        // "  build:" on line 3. col 3..8 → span of 5 chars
        var source = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 3, 3, 3, 8, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        // Caret row must have exactly 5 carets (EndColumn - StartColumn = 8 - 3 = 5)
        await Assert.That(output).Contains("^^^^^");
    }

    [Test]
    public async Task Rich_SourceSnippet_MinimumOneCaret_WhenStartEqualsEnd()
    {
        var source = "on: push\n"u8.ToArray();
        var sourceMap = new Dictionary<string, byte[]> { ["ci.yml"] = source };

        // StartCol == EndCol → minimum 1 caret
        var diag = MakeDiagnostic(DiagnosticSeverity.Error, "msg", 1, 4, 1, 4, filePath: "ci.yml");
        var output = Render(diag, sourceMap: sourceMap);

        await Assert.That(output).Contains("^");
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.Text, oneline: false, color: false, sourceMap);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Json, oneline: false, color: false, sourceMap);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Json, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Json, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).DoesNotContain("\"help\"");
    }

    [Test]
    public async Task Sarif_Format_IncludesHelpInMessage()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "unpinned action", 5, 11, 5, 30, help: "to ignore this owner, add config");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
            DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false, sourceMap: null, pathBaseDirectory: baseDir);
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
    public async Task Sarif_Format_UnknownPath_UsesSafeFileUri()
    {
        var diag = MakeDiagnostic(DiagnosticSeverity.Warning, "unknown path", 1, 1, 1, 3, filePath: null);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
        writer.Flush();

        using var doc = JsonDocument.Parse(sb.ToString());
        var run = doc.RootElement.GetProperty("runs")[0];
        var location = run
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("artifactLocation");

        await Assert.That(location.GetProperty("uri").GetString()).IsEqualTo("<stdin>");
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
            DiagnosticFormatter.Write(writer, [diag], OutputFormat.Json, oneline: false, color: false, sourceMap: null, pathBaseDirectory: baseDir);
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
            DiagnosticFormatter.Write(writer, [diag], OutputFormat.Text, oneline: true, color: false, sourceMap: null, pathBaseDirectory: baseDir);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.Sarif, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, [diag], OutputFormat.GitHubActions, oneline: false, color: true);
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.GitHubActions, oneline: false, color: false);
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("::group::a%2525%0D%0Ab.yml");
        await Assert.That(output).DoesNotContain("::group::a%25\r\nb.yml");
    }

    [Test]
    public async Task GitHubActions_Format_Oneline_EscapesFilePathControlCharacters()
    {
        var filePath = "a\r\n::warning::owned";
        var diagnostics = new[]
        {
            MakeDiagnostic(DiagnosticSeverity.Warning, "first", 1, 1, 1, 5, filePath: filePath),
        };

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.GitHubActions, oneline: true, color: false);
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
        DiagnosticFormatter.Write(writer, diagnostics, OutputFormat.GitHubActions, oneline: false, color: false);
        writer.Flush();
        var output = sb.ToString();

        await Assert.That(output).Contains("--> a%0D%0A::warning::owned:1:1");
        await Assert.That(output).DoesNotContain("--> a\r\n::warning::owned:1:1");
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
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(
            writer,
            [diagnostic],
            format,
            oneline,
            color,
            sourceMap);
        writer.Flush();
        return sb.ToString();
    }
}
