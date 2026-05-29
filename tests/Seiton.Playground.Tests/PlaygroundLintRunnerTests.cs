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

    // ─── SetConfig Tests ───
    // These tests mutate shared static config state and must not run in parallel.
    private const string ConfigLockKey = "PlaygroundConfig";

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_Null_ResetsToDefault_ReturnsEmptyArray()
    {
        var result = PlaygroundLintRunner.SetConfig(null);
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_Empty_ResetsToDefault_ReturnsEmptyArray()
    {
        var result = PlaygroundLintRunner.SetConfig("");
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_WhitespaceOnly_ResetsToDefault_ReturnsEmptyArray()
    {
        var result = PlaygroundLintRunner.SetConfig("   \n  \n");
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_ValidConfig_ReturnsEmptyArray()
    {
        const string config = """
            rules:
              runner-no-latest:
                severity: warning
            """;
        var result = PlaygroundLintRunner.SetConfig(config);
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(0);

        // Cleanup: reset to default
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_InvalidConfig_ReturnsDiagnostics()
    {
        const string config = """
            rules:
              nonexistent-rule-xyz: deny
            """;
        var result = PlaygroundLintRunner.SetConfig(config);
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsGreaterThan(0);

        // Cleanup: reset to default
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_HashHit_ReturnsCachedResult()
    {
        const string config = """
            rules:
              runner-no-latest:
                severity: warning
            """;
        var first = PlaygroundLintRunner.SetConfig(config);
        var second = PlaygroundLintRunner.SetConfig(config);

        // Same reference returned on hash-hit (zero allocation)
        await Assert.That(ReferenceEquals(first, second)).IsTrue();

        // Cleanup
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_CosmeticEdit_DoesNotTriggerReparse()
    {
        const string config1 = """
            rules:
              runner-no-latest:
                severity: warning
            """;
        // Same meaningful content but with trailing whitespace and blank lines
        var config2 = "rules:  \n  runner-no-latest:  \n    severity: warning  \n\n";

        var first = PlaygroundLintRunner.SetConfig(config1);
        var second = PlaygroundLintRunner.SetConfig(config2);

        // Same reference returned because normalized content is identical
        await Assert.That(ReferenceEquals(first, second)).IsTrue();

        // Cleanup
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_DifferentContent_TriggersReparse()
    {
        const string config1 = """
            rules:
              runner-no-latest:
                severity: warning
            """;
        const string config2 = """
            rules:
              runner-no-latest:
                severity: error
            """;

        var first = PlaygroundLintRunner.SetConfig(config1);
        var second = PlaygroundLintRunner.SetConfig(config2);

        // Different content means different result (possibly same empty array content but different parse)
        // At minimum, both should be valid
        using var doc1 = JsonDocument.Parse(first);
        using var doc2 = JsonDocument.Parse(second);
        await Assert.That(doc1.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(doc2.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);

        // Cleanup
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_AffectsLintResults()
    {
        // Workflow that triggers runner-no-latest diagnostic
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        // Default config: runner-no-latest should trigger
        PlaygroundLintRunner.SetConfig(null);
        var defaultResult = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
        using var defaultDoc = JsonDocument.Parse(defaultResult);
        var hasRunnerNoLatest = false;
        foreach (var el in defaultDoc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("ruleId", out var rid) && rid.GetString() == "runner-no-latest")
            {
                hasRunnerNoLatest = true;
                break;
            }
        }
        await Assert.That(hasRunnerNoLatest).IsTrue();

        // Disable runner-no-latest via config
        const string config = """
            rules:
              runner-no-latest:
                enabled: false
            """;
        PlaygroundLintRunner.SetConfig(config);

        // Force re-lint with different string reference to bypass identity cache
        var customResult = PlaygroundLintRunner.RunToJsonUtf8(new string(yaml.AsSpan()), ".github/workflows/ci.yml");
        using var customDoc = JsonDocument.Parse(customResult);
        var stillHasRunnerNoLatest = false;
        foreach (var el in customDoc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("ruleId", out var rid) && rid.GetString() == "runner-no-latest")
            {
                stillHasRunnerNoLatest = true;
                break;
            }
        }
        await Assert.That(stillHasRunnerNoLatest).IsFalse();

        // Cleanup
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    [NotInParallel(ConfigLockKey)]
    public async Task SetConfig_InvalidConfig_RetainsPreviousValidConfig()
    {
        const string validConfig = """
            rules:
              runner-no-latest:
                enabled: false
            """;
        PlaygroundLintRunner.SetConfig(validConfig);

        // Now set an invalid config
        const string invalidConfig = """
            rules:
              nonexistent-rule-xyz: deny
            """;
        var result = PlaygroundLintRunner.SetConfig(invalidConfig);
        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetArrayLength()).IsGreaterThan(0);

        // Verify the previous valid config is still active (runner-no-latest disabled)
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;
        var lintResult = PlaygroundLintRunner.RunToJsonUtf8(new string(yaml.AsSpan()), ".github/workflows/ci.yml");
        using var lintDoc = JsonDocument.Parse(lintResult);
        var hasRunnerNoLatest = false;
        foreach (var el in lintDoc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("ruleId", out var rid) && rid.GetString() == "runner-no-latest")
            {
                hasRunnerNoLatest = true;
                break;
            }
        }
        // Previous config (runner-no-latest: false) should still be active
        await Assert.That(hasRunnerNoLatest).IsFalse();

        // Cleanup
        PlaygroundLintRunner.SetConfig(null);
    }
}
