using Seiton.Cli;

namespace Seiton.Tests;

public sealed class CliOptionSuggesterTests
{
    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_UppercaseOption_SuggestsKnownOption()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--VERBOSE"], error);

        await Assert.That(wrote).IsTrue();
        await Assert.That(error.ToString()).Contains("Did you mean '--verbose'?", StringComparison.Ordinal);
    }

    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_MixedResolvableAndUnresolvable_DoesNotEmitTryLine()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--verboze", "--totally-unknown-option"], error);

        await Assert.That(wrote).IsTrue();
        await Assert.That(error.ToString()).Contains("Did you mean '--verbose'?", StringComparison.Ordinal);
        await Assert.That(error.ToString().Contains("Try:", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_SpaceContainingValue_QuotesTryCommandToken()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--confg", "C:\\Users\\me\\My Config\\seiton.yaml"], error);

        await Assert.That(wrote).IsTrue();
        await Assert.That(error.ToString()).Contains("Try: seiton --config \"C:\\Users\\me\\My Config\\seiton.yaml\"", StringComparison.Ordinal);
    }
}
