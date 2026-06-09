using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using System.Text;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
public class RunEnvContextDirectUseBenchmark
{
    public enum Scenario
    {
        PosixSimpleUnquoted,
        PosixSimpleSingleQuoted,
        PosixComplexSingleQuoted,
        PwshSimpleSingleQuotedWithDefaults,
        PosixCompositeExpression,
    }

    [Params(Scenario.PosixSimpleUnquoted, Scenario.PosixSimpleSingleQuoted, Scenario.PosixComplexSingleQuoted, Scenario.PwshSimpleSingleQuotedWithDefaults, Scenario.PosixCompositeExpression)]
    public Scenario Case { get; set; }

    [Params(false, true)]
    public bool FixEnabled { get; set; }

    private byte[] _yamlBytes = [];
    private string _filePath = string.Empty;
    private LintEngine _engine = null!;
    private LintConfig _config = null!;

    [GlobalSetup]
    public void Setup()
    {
        var yaml = Case switch
        {
            Scenario.PosixSimpleUnquoted => BuildPosixSimpleUnquoted(),
            Scenario.PosixSimpleSingleQuoted => BuildPosixSimpleSingleQuoted(),
            Scenario.PosixComplexSingleQuoted => BuildPosixComplexSingleQuoted(),
            Scenario.PwshSimpleSingleQuotedWithDefaults => BuildPwshSimpleSingleQuotedWithDefaults(),
            Scenario.PosixCompositeExpression => BuildPosixCompositeExpression(),
            _ => BuildPosixSimpleUnquoted(),
        };

        _yamlBytes = Encoding.UTF8.GetBytes(yaml);
        _filePath = $"bench-run-env-{Case.ToString().ToLowerInvariant()}.yml";
        _engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        _config = new LintConfig
        {
            Utf8Yaml = _yamlBytes,
            FilePath = _filePath,
            Fix = new FixConfig
            {
                Enabled = FixEnabled,
                Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 },
            }
        };
    }

    [Benchmark]
    public int CheckAndCountFixes()
    {
        using var result = _engine.Check(_yamlBytes, _filePath, _config);
        var fixCount = 0;
        for (var i = 0; i < result.Diagnostics.Length; i++)
        {
            if (result.Diagnostics[i].Fix is not null)
            {
                fixCount++;
            }
        }

        return fixCount;
    }

    private static string BuildPosixSimpleUnquoted() => """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo "${{ env.VERSION }}"
        """;

    private static string BuildPosixSimpleSingleQuoted() => """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo '${{ env.VERSION }}'
        """;

    private static string BuildPosixComplexSingleQuoted() => """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo 'pre-${{ env.VERSION }}-post'
        """;

    private static string BuildPwshSimpleSingleQuotedWithDefaults() => """
        on: push
        jobs:
          build:
            runs-on: windows-latest
            defaults:
              run:
                shell: pwsh
            steps:
              - run: Write-Host '${{ env.VERSION }}'
        """;

    private static string BuildPosixCompositeExpression() => """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo "${{ env.VERSION || 'fallback' }}"
        """;
}
