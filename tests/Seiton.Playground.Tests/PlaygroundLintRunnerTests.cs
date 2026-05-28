using System.Text;
using System.Text.Json;

namespace Seiton.Playground.Tests;

public sealed class PlaygroundLintRunnerTests
{
    /// <summary>
    /// Verifies that rapid sequential lint calls produce stable results.
    /// Asserts full diagnostic content (message, line, ruleId) — not just count —
    /// so stale-buffer corruption from the two-buffer swap is caught.
    /// </summary>
    [Test]
    public async Task RunToJson_RepeatedCalls_ProducesConsistentDiagnostics()
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

        byte[]? firstJson = null;
        for (var i = 0; i < 10; i++)
        {
            var json = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
            firstJson ??= json;
            await Assert.That(json).IsEquivalentTo(firstJson);
        }
    }
    /// <summary>
    /// Verifies that concurrent lint calls do not corrupt shared static state
    /// (LintEngine / JsonBuffer are guarded by EngineGate).
    /// Asserts full JSON equality so message/location corruption is caught.
    /// </summary>
    [Test]
    public async Task RunToJson_ConcurrentCalls_ProducesIdenticalDiagnostics()
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

        // Obtain a reference result from a single-threaded call.
        var expected = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");

        const int parallelism = 8;
        var tasks = new Task<byte[]>[parallelism];
        for (var i = 0; i < parallelism; i++)
        {
            tasks[i] = Task.Run(() => PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml"));
        }

        var results = await Task.WhenAll(tasks);
        foreach (var json in results)
        {
            await Assert.That(json).IsEquivalentTo(expected);
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

        var json = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task RunToJson_InvalidYaml_ContainsParserDiagnosticWithLineAndMessage()
    {
        var json = PlaygroundLintRunner.RunToJsonUtf8("not: @@@", ".github/workflows/test.yml");
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
        var jsonBytes = PlaygroundLintRunner.RunToJsonUtf8("x", ".github/workflows/w.yml");
        var json = Encoding.UTF8.GetString(jsonBytes);
        await Assert.That(json).Contains("\"message\"");
        await Assert.That(json).Contains("\"ruleId\"");
    }

    [Test]
    public async Task RunToJson_SeverityValues_AreValidStrings()
    {
        var yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var json = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(json);
        var validSeverities = new HashSet<string> { "Error", "Warning", "Info" };
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            await Assert.That(el.TryGetProperty("severity", out var sev)).IsTrue();
            await Assert.That(validSeverities.Contains(sev.GetString()!)).IsTrue();
        }
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

        var json = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
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
        var afterJson = PlaygroundLintRunner.RunToJsonUtf8(fixedYaml, ".github/workflows/ci.yml");
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

    /// <summary>
    /// Regression test: verifies that linting an action.yml file (ActionMetadata path)
    /// correctly disposes the arena after use. Multiple sequential calls must not leak memory.
    /// </summary>
    [Test]
    public async Task RunToJson_ActionMetadata_RepeatedCalls_ProducesConsistentResults()
    {
        const string actionYaml = """
            name: My Action
            description: A test action
            inputs:
              name:
                description: The name
                required: true
            runs:
              using: node20
              main: index.js
            """;

        byte[]? firstJson = null;
        for (var i = 0; i < 5; i++)
        {
            var json = PlaygroundLintRunner.RunToJsonUtf8(actionYaml, "action.yml");
            firstJson ??= json;
            await Assert.That(json).IsEquivalentTo(firstJson);
        }

        // Verify it's a valid JSON array
        using var doc = JsonDocument.Parse(firstJson!);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
    }
}
