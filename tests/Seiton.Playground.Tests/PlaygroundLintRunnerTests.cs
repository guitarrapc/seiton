using System.Text.Json;

namespace Seiton.Playground.Tests;

public sealed class PlaygroundLintRunnerTests
{
    /// <summary>
    /// Verifies that rapid sequential lint calls produce stable results and don't
    /// accumulate state. After the stateless refactor, each call creates a fresh
    /// LintEngine so no cross-call contamination is possible.
    /// </summary>
    [Test]
    public async Task RunToJson_RepeatedCalls_ProducesConsistentDiagnosticCount()
    {
        const string yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        int? firstCount = null;
        for (var i = 0; i < 10; i++)
        {
            var json = PlaygroundLintRunner.RunToJson(yaml, ".github/workflows/ci.yml");
            using var doc = JsonDocument.Parse(json);
            var count = doc.RootElement.GetArrayLength();
            firstCount ??= count;
            await Assert.That(count).IsEqualTo(firstCount.Value);
        }
    }
    [Test]
    public async Task RunToJson_ValidMinimalWorkflow_ReturnsJsonArray()
    {
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var json = PlaygroundLintRunner.RunToJson(yaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task RunToJson_InvalidYaml_ContainsParserDiagnosticWithLineAndMessage()
    {
        var json = PlaygroundLintRunner.RunToJson("not: @@@", ".github/workflows/test.yml");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        await Assert.That(arr.GetArrayLength()).IsGreaterThan(0);

        var first = arr[0];
        await Assert.That(first.TryGetProperty("message", out var msg)).IsTrue();
        await Assert.That(msg.GetString()).IsNotNull();
        await Assert.That(first.TryGetProperty("line", out _)).IsTrue();
        await Assert.That(first.TryGetProperty("column", out _)).IsTrue();
        await Assert.That(first.TryGetProperty("severity", out _)).IsTrue();
    }

    [Test]
    public async Task RunToJson_UsesCamelCasePropertyNames()
    {
        var json = PlaygroundLintRunner.RunToJson("x", ".github/workflows/w.yml");
        await Assert.That(json).Contains("\"message\"");
        await Assert.That(json).Contains("\"ruleId\"");
    }

    [Test]
    public async Task RunToJson_DenyWriteAll_IncludesFixableDiagnostic()
    {
        var yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                permissions:
                  contents: read
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var json = PlaygroundLintRunner.RunToJson(yaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(json);
        var found = false;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("ruleId", out var rid) && rid.GetString() == "deny-write-all"
                && el.TryGetProperty("fixable", out var fx) && fx.GetBoolean())
            {
                found = true;
                await Assert.That(el.TryGetProperty("fixDescription", out _)).IsTrue();
                break;
            }
        }

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task ApplyAllFixes_DenyWriteAll_ReplacesPermissions()
    {
        var yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                permissions:
                  contents: read
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var fixedYaml = PlaygroundLintRunner.ApplyAllFixes(yaml, ".github/workflows/ci.yml");
        await Assert.That(fixedYaml.Contains("write-all", StringComparison.Ordinal)).IsFalse();
        var afterJson = PlaygroundLintRunner.RunToJson(fixedYaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(afterJson);
        var stillBad = false;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("ruleId", out var rid) && rid.GetString() == "deny-write-all")
            {
                stillBad = true;
                break;
            }
        }

        await Assert.That(stillBad).IsFalse();
    }
}
