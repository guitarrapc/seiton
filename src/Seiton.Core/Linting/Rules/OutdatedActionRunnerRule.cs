using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags popular actions that use a deprecated Node.js runner (node12 / node16).</summary>
public sealed class OutdatedActionRunnerRule() : RuleBase(RuleId.OutdatedActionRunner)
{
    // Each entry: (ActionName, MinimumNonDeprecatedMajor, DeprecatedRunnerName)
    // Actions with major version < MinimumNonDeprecatedMajor use a deprecated runner.
    private static readonly (byte[] ActionName, int MinMajor, string Runner)[] entries =
    [
        ("actions/cache"u8.ToArray(), 4, "node16"),
        ("actions/checkout"u8.ToArray(), 4, "node16"),
        ("actions/download-artifact"u8.ToArray(), 4, "node16"),
        ("actions/setup-dotnet"u8.ToArray(), 4, "node16"),
        ("actions/setup-go"u8.ToArray(), 5, "node16"),
        ("actions/setup-java"u8.ToArray(), 4, "node16"),
        ("actions/setup-node"u8.ToArray(), 4, "node16"),
        ("actions/setup-python"u8.ToArray(), 5, "node16"),
        ("actions/upload-artifact"u8.ToArray(), 4, "node16"),
        ("docker/login-action"u8.ToArray(), 3, "node16"),
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

        // Find @ separator
        var atIndex = usesValue.IndexOf((byte)'@');
        if (atIndex <= 0 || atIndex >= usesValue.Length - 1)
        {
            return;
        }

        var actionName = usesValue.Slice(0, atIndex);
        var versionTag = usesValue.Slice(atIndex + 1);

        // Parse major version from vNN or vNN.x.y
        if (!TryParseMajorVersion(versionTag, out var major))
        {
            return;
        }

        // Check against known deprecated entries
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            if (!SpanHelpers.EqualsAsciiIgnoreCase(actionName, entry.ActionName))
            {
                continue;
            }

            if (major < entry.MinMajor)
            {
                var usesStr = Decode(Arena.GetStringSlice(actionExec.Uses));
                AddStepError(step, $"the runner of \"{usesStr}\" action is too old to run on GitHub Actions. update the action's version to fix this problem", Arena.GetStringRange(actionExec.Uses));
            }

            return;
        }
    }

    private static bool TryParseMajorVersion(ReadOnlySpan<byte> versionTag, out int major)
    {
        major = 0;
        if (versionTag.IsEmpty || (versionTag[0] != (byte)'v' && versionTag[0] != (byte)'V'))
        {
            return false;
        }

        var i = 1;
        if (i >= versionTag.Length || versionTag[i] < (byte)'0' || versionTag[i] > (byte)'9')
        {
            return false;
        }

        while (i < versionTag.Length && versionTag[i] >= (byte)'0' && versionTag[i] <= (byte)'9')
        {
            major = major * 10 + (versionTag[i] - (byte)'0');
            i++;
        }

        // Accept vNN, vNN.x.y, etc. (anything after digits is fine)
        return true;
    }
}
