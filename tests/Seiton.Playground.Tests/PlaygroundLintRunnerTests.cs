using System.Text.Json;

namespace Seiton.Playground.Tests;

public sealed class PlaygroundLintRunnerTests
{
    /// <summary>
    /// Verifies that rapid sequential lint calls produce stable results.
    /// The shared static LintEngine properly clears state between calls,
    /// so repeated invocations should yield identical diagnostic counts.
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
    /// <summary>
    /// Verifies that concurrent lint calls do not corrupt shared static state
    /// (LintEngine / JsonBuffer are guarded by EngineGate).
    /// </summary>
    [Test]
    public async Task RunToJson_ConcurrentCalls_ProducesValidJson()
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

        const int parallelism = 8;
        var tasks = new Task<string>[parallelism];
        for (var i = 0; i < parallelism; i++)
        {
            tasks[i] = Task.Run(() => PlaygroundLintRunner.RunToJson(yaml, ".github/workflows/ci.yml"));
        }

        var results = await Task.WhenAll(tasks);
        int? expectedCount = null;
        foreach (var json in results)
        {
            using var doc = JsonDocument.Parse(json);
            await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
            var count = doc.RootElement.GetArrayLength();
            expectedCount ??= count;
            await Assert.That(count).IsEqualTo(expectedCount.Value);
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
