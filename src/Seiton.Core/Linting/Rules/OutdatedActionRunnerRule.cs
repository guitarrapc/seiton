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

        var runsUsing = spec.GetRunsUsing();
        if (runsUsing.IsEmpty)
        {
            return;
        }

        if (!IsDeprecated(runsUsing))
        {
            return;
        }

        var usesStr = Decode(Arena.GetStringSlice(actionExec.Uses));
        AddStepError(step, $"the runner of \"{usesStr}\" action is too old to run on GitHub Actions. update the action's version to fix this problem", Arena.GetStringRange(actionExec.Uses));
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
