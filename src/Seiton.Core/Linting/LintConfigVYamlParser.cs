using Seiton.Core.Parsing;
using VYaml.Serialization;

namespace Seiton.Core.Linting;

internal static class LintConfigVYamlParser
{
    public static LintConfigParseResult Parse(ReadOnlyMemory<byte> utf8Yaml, string filePath)
    {
        Dictionary<string, object?> root;
        try
        {
            root = YamlSerializer.Deserialize<Dictionary<string, object?>>(utf8Yaml)
                ?? new Dictionary<string, object?>();
        }
        catch (Exception ex)
        {
            var d = new Diagnostic(
                DiagnosticSeverity.Error,
                $"invalid lint config YAML: {ex.Message}",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath);
            return new LintConfigParseResult(
                new Dictionary<string, RuleConfig>(StringComparer.OrdinalIgnoreCase),
                [],
                new FixConfig(),
                new NetworkConfig(),
                [d]);
        }

        return LintConfigYamlDomConverter.Convert(root, filePath);
    }
}
