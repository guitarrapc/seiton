using Seiton.Playground;

namespace Seiton.Playground.Tests;

public sealed class PlaygroundSharePayloadTests
{
    private const string SampleYaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n";
    private const string SampleConfig = "rules:\n  runner-no-latest:\n    severity: warning\n";
    private const string SamplePath = ".github/workflows/ci.yml";

    [Test]
    public async Task EncodeDecode_V2RoundTrip_RestoresYamlConfigAndFilePath()
    {
        var state = new PlaygroundSharePayload.State(SampleYaml, SampleConfig, SamplePath);
        var hash = PlaygroundSharePayload.Encode(state);

        await Assert.That(PlaygroundSharePayload.TryDecode(hash, out var decoded, out var error)).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(decoded!.Yaml).IsEqualTo(SampleYaml);
        await Assert.That(decoded.Config).IsEqualTo(SampleConfig);
        await Assert.That(decoded.FilePath).IsEqualTo(SamplePath);
    }

    [Test]
    public async Task EncodeDecode_V2WithEmptyConfig_RestoresEmptyConfig()
    {
        var state = new PlaygroundSharePayload.State(SampleYaml, "", SamplePath);
        var hash = PlaygroundSharePayload.Encode(state);

        await Assert.That(PlaygroundSharePayload.TryDecode(hash, out var decoded, out _)).IsTrue();
        await Assert.That(decoded!.Config).IsEqualTo("");
    }

    [Test]
    public async Task TryDecode_V1LegacyYamlOnly_RestoresYamlOnly()
    {
        var hash = PlaygroundSharePayload.EncodeLegacyYamlOnly(SampleYaml);

        await Assert.That(PlaygroundSharePayload.TryDecode(hash, out var decoded, out var error)).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(decoded!.Yaml).IsEqualTo(SampleYaml);
        await Assert.That(decoded.Config).IsEqualTo("");
        await Assert.That(decoded.FilePath).IsEqualTo(PlaygroundSharePayload.DefaultFilePath);
    }

    [Test]
    public async Task TryDecode_InvalidBase64_ReturnsFalse()
    {
        await Assert.That(PlaygroundSharePayload.TryDecode("not!!!valid", out _, out var error)).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task TryDecode_CorruptZlibPayload_ReturnsFalse()
    {
        // Valid base64url wrapping bytes that are not a zlib deflate stream.
        var broken = PlaygroundSharePayload.EncodeLegacyYamlOnly("x");
        broken = broken[..^4] + "XXXX";

        await Assert.That(PlaygroundSharePayload.TryDecode(broken, out _, out var error)).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Encode_YamlOnlyVariant_OmitsConfigFromPayload()
    {
        var full = PlaygroundSharePayload.Encode(new PlaygroundSharePayload.State(SampleYaml, SampleConfig, SamplePath));
        var yamlOnly = PlaygroundSharePayload.EncodeYamlOnly(SampleYaml, SamplePath);

        await Assert.That(yamlOnly.Length < full.Length).IsTrue();
        await Assert.That(PlaygroundSharePayload.TryDecode(yamlOnly, out var decoded, out _)).IsTrue();
        await Assert.That(decoded!.Yaml).IsEqualTo(SampleYaml);
        await Assert.That(decoded.Config).IsEqualTo("");
    }

    [Test]
    public async Task FormatClipboardBundle_ContainsWorkflowAndConfigSections()
    {
        var text = PlaygroundSharePayload.FormatClipboardBundle(SampleYaml, SampleConfig, SamplePath);

        await Assert.That(text).Contains(SampleYaml);
        await Assert.That(text).Contains(SampleConfig);
        await Assert.That(text).Contains(SamplePath);
        await Assert.That(text).Contains("workflow", StringComparison.OrdinalIgnoreCase);
        await Assert.That(text).Contains("config", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Encode_V2_UsesBase64UrlAlphabet()
    {
        var hash = PlaygroundSharePayload.Encode(new PlaygroundSharePayload.State(SampleYaml, SampleConfig, SamplePath));
        await Assert.That(hash.Contains('+')).IsFalse();
        await Assert.That(hash.Contains('/')).IsFalse();
        await Assert.That(hash.Contains('=')).IsFalse();
    }

    [Test]
    public async Task IsWithinShareLimits_ShortHash_ReturnsTrue()
    {
        var hash = PlaygroundSharePayload.Encode(new PlaygroundSharePayload.State("on: push\n", "", SamplePath));
        await Assert.That(PlaygroundSharePayload.IsHashWithinLimits(hash)).IsTrue();
    }

    [Test]
    public async Task IsWithinShareLimits_HashOverLimit_ReturnsFalse()
    {
        var over = new string('a', PlaygroundSharePayload.MaxHashLength + 1);
        await Assert.That(PlaygroundSharePayload.IsHashWithinLimits(over)).IsFalse();
    }

    [Test]
    public async Task IsWithinShareLimits_UrlOverLimit_ReturnsFalse()
    {
        var over = $"https://example.invalid/#{new string('a', PlaygroundSharePayload.MaxUrlLength)}";
        await Assert.That(PlaygroundSharePayload.IsUrlWithinLimits(over)).IsFalse();
    }

    [Test]
    public async Task TryDecode_V2BlankPath_FallsBackToDefaultPath()
    {
        var hash = PlaygroundSharePayload.Encode(new PlaygroundSharePayload.State(SampleYaml, SampleConfig, "  "));
        await Assert.That(PlaygroundSharePayload.TryDecode(hash, out var decoded, out _)).IsTrue();
        await Assert.That(decoded!.FilePath).IsEqualTo(PlaygroundSharePayload.DefaultFilePath);
    }
}
