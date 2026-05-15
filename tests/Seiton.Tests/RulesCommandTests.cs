using System.Text.Json;
using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class RulesCommandTests
{
    [Test]
    public async Task Run_JsonFormat_UsesExpectedCamelCasePropertyNames()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = RulesCommand.Run(config: null, format: OutputFormat.Json, stdout, stderr);

        await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
        await Assert.That(stderr.ToString()).IsEqualTo(string.Empty);

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;
        await Assert.That(root.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(root.GetArrayLength()).IsGreaterThan(0);

        var jobStructure = FindRule(root, "job-structure");
        await Assert.That(jobStructure.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(jobStructure.TryGetProperty("id", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("name", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("enabled", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("type", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("defaultSeverity", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("supportsAutoFix", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("supportsWorkflow", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("supportsAction", out _)).IsTrue();
        await Assert.That(jobStructure.TryGetProperty("reason", out _)).IsTrue();

        await Assert.That(jobStructure.TryGetProperty("DefaultSeverity", out _)).IsFalse();
        await Assert.That(jobStructure.TryGetProperty("SupportsAutoFix", out _)).IsFalse();
        await Assert.That(jobStructure.GetProperty("id").GetString()).IsEqualTo("job-structure");
    }

    private static JsonElement FindRule(JsonElement root, string ruleId)
    {
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), ruleId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"Rule '{ruleId}' not found in JSON output.");
    }
}
