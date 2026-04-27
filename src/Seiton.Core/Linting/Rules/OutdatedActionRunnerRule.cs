using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Flags popular actions whose <c>runs.using</c> runtime is deprecated.
/// The deprecated runner set reflects the GitHub Actions runner deprecation policy.
/// When GitHub deprecates a new runner version, add it to <see cref="DeprecatedRunners"/>.
/// </summary>
public sealed class OutdatedActionRunnerRule() : RuleBase(RuleId.OutdatedActionRunner)
{
    // Deprecated Node.js runtimes on GitHub Actions runners.
    // Update this list when GitHub announces new deprecations.
    // See: https://github.blog/changelog/ (search "deprecation of node")
    //   - node12: deprecated since 2022
    //   - node16: deprecated since 2024
    private static readonly byte[][] DeprecatedRunners =
    [
        "node12"u8.ToArray(),
        "node16"u8.ToArray(),
    ];

    public override string Name => "Outdated Action Runner Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesValue = Arena.GetStringValue(actionExec.Uses);
        if (usesValue.IsEmpty)
        {
            return;
        }

        if (!PopularActions.TryGet(usesValue, out var spec))
        {
            return;
        }

        // Check version-aware deprecation first: if the major version is known to use a deprecated runner
        var maxDeprecated = spec.GetMaxDeprecatedMajorVersion();
        if (maxDeprecated > 0 && TryExtractMajorVersion(usesValue, out var majorVersion) && majorVersion <= maxDeprecated)
        {
            var usesStr = Decode(Arena.GetStringSlice(actionExec.Uses));
            AddStepError(step, $"the runner of \"{usesStr}\" action is too old to run on GitHub Actions. update the action's version to fix this issue", Arena.GetStringRange(actionExec.Uses));
            return;
        }

        // Fallback: check the catalog's current runs.using value
        var runsUsing = spec.GetRunsUsing();
        if (runsUsing.IsEmpty)
        {
            return;
        }

        if (!IsDeprecated(runsUsing))
        {
            return;
        }

        var usesStr2 = Decode(Arena.GetStringSlice(actionExec.Uses));
        AddStepError(step, $"the runner of \"{usesStr2}\" action is too old to run on GitHub Actions. update the action's version to fix this issue", Arena.GetStringRange(actionExec.Uses));
    }

    /// <summary>
    /// Extracts the major version number from a uses value like "actions/checkout@v3".
    /// Returns false if the version tag is missing, doesn't start with 'v', or isn't a valid number.
    /// </summary>
    internal static bool TryExtractMajorVersion(ReadOnlySpan<byte> usesValue, out int majorVersion)
    {
        majorVersion = 0;
        var atIndex = usesValue.IndexOf((byte)'@');
        if (atIndex < 0 || atIndex + 2 >= usesValue.Length)
        {
            return false;
        }

        var versionPart = usesValue.Slice(atIndex + 1);

        // Must start with 'v' or 'V'
        if (versionPart[0] != (byte)'v' && versionPart[0] != (byte)'V')
        {
            return false;
        }

        versionPart = versionPart.Slice(1);

        // Parse digits until non-digit or end
        var result = 0;
        var hasDigit = false;
        for (var i = 0; i < versionPart.Length; i++)
        {
            var b = versionPart[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                result = result * 10 + (b - (byte)'0');
                hasDigit = true;
            }
            else
            {
                break; // stop at first non-digit (e.g. '.', '-')
            }
        }

        if (!hasDigit)
        {
            return false;
        }

        majorVersion = result;
        return true;
    }

    internal static bool IsDeprecated(ReadOnlySpan<byte> runsUsing)
    {
        for (var i = 0; i < DeprecatedRunners.Length; i++)
        {
            if (runsUsing.SequenceEqual(DeprecatedRunners[i]))
            {
                return true;
            }
        }

        return false;
    }
}
