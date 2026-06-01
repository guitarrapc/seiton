using Seiton.Cli;

namespace Seiton.Tests;

public sealed class CliOptionSuggesterTests
{
    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_UppercaseOption_SuggestsKnownOption()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--VERBOSE"], error);
        var message = error.ToString();

        await Assert.That(wrote).IsTrue();
        await Assert.That(message.Contains("Did you mean '--verbose'?", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_MixedResolvableAndUnresolvable_DoesNotEmitTryLine()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--verboze", "--totally-unknown-option"], error);
        var message = error.ToString();

        await Assert.That(wrote).IsTrue();
        await Assert.That(message.Contains("Did you mean '--verbose'?", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("Try:", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_SpaceContainingValue_QuotesTryCommandToken()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["--confg", "C:\\Users\\me\\My Config\\seiton.yaml"], error);
        var message = error.ToString();

        await Assert.That(wrote).IsTrue();
        await Assert.That(message.Contains("Try: seiton --config \"C:\\Users\\me\\My Config\\seiton.yaml\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryWriteSuggestionsForUnknownOptions_PreservesVvInSuggestedCommand()
    {
        using var error = new StringWriter();

        var wrote = CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(["check", "-vv", "--verboze"], error);
        var message = error.ToString();

        await Assert.That(wrote).IsTrue();
        await Assert.That(message.Contains("Did you mean '--verbose'?", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("Try: seiton check -vv --verbose", StringComparison.Ordinal)).IsTrue();
    }
}
