using Seiton.Update.Generators;
using Seiton.Update.Model;

namespace Seiton.Update.Tests;

public sealed class RunnerLabelsCSharpGeneratorTests
{
    [Test]
    public async Task Generate_WithDeprecatedLabels_EmitsDeprecatedHostedLabelHelpers()
    {
        var model = new RunnerLabelsModel(
            ["ubuntu-24.04"],
            [],
            ["ubuntu-22.04", "ubuntu-22.04-arm"]);

        var generator = new RunnerLabelsCSharpGenerator();
        var output = generator.Generate(model);

        await Assert.That(output).Contains("internal const string DeprecatedLabelList = \"\\\"ubuntu-22.04\\\", \\\"ubuntu-22.04-arm\\\"\";");
        await Assert.That(output).Contains("internal static bool IsDeprecatedHostedLabel(ReadOnlySpan<byte> labelUtf8)");
        await Assert.That(output).Contains("EqualsAsciiIgnoreCase(labelUtf8, \"ubuntu-22.04\"u8)");
        await Assert.That(output).Contains("EqualsAsciiIgnoreCase(labelUtf8, \"ubuntu-22.04-arm\"u8)");
    }

    [Test]
    public async Task Generate_WithoutDeprecatedLabels_EmitsEmptyDeprecatedHelpers()
    {
        var model = new RunnerLabelsModel(["ubuntu-24.04"], [], []);

        var generator = new RunnerLabelsCSharpGenerator();
        var output = generator.Generate(model);

        await Assert.That(output).Contains("internal const string DeprecatedLabelList = \"\";");
        await Assert.That(output).Contains("internal static bool IsDeprecatedHostedLabel(ReadOnlySpan<byte> labelUtf8)");
        await Assert.That(output).Contains("return false;");
    }
}
