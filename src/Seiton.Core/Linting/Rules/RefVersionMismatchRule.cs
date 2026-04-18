using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class RefVersionMismatchRule : RuleBase
{
    public override string Id => "ref-version-mismatch";

    public override string Name => "Ref Version Mismatch Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.WorkflowCall is null)
        {
            return;
        }

        CheckUses(job.WorkflowCall.Uses, job, null);
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction action)
        {
            return;
        }

        CheckUses(action.Uses, null, step);
    }

    void CheckUses(StringNode usesNode, Job? job, Step? step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = usesNode.Value.AsSpan(Config.Utf8Yaml);
        if (!TryParseActionReference(uses, out var actionPath, out var reference)
            || IsFullLengthCommitSha(reference)
            || !TryExtractVersionMajor(reference, out var refMajor)
            || !TryExtractPathVersionMajor(actionPath, out var pathMajor)
            || pathMajor == refMajor)
        {
            return;
        }

        var message = $"uses ref major version 'v{refMajor}' mismatches action path version hint 'v{pathMajor}'; align ref and path intent";
        if (step is not null)
        {
            AddStepWarning(step, message, usesNode.Range);
        }
        else if (job is not null)
        {
            AddJobWarning(job, message, usesNode.Range);
        }
    }

    static bool TryParseActionReference(ReadOnlySpan<byte> uses, out ReadOnlySpan<byte> actionPath, out ReadOnlySpan<byte> reference)
    {
        actionPath = [];
        reference = [];

        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        var at = uses.LastIndexOf((byte)'@');
        if (at <= 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        actionPath = uses[..at];
        reference = uses[(at + 1)..];
        return true;
    }

    static bool TryExtractPathVersionMajor(ReadOnlySpan<byte> actionPath, out int major)
    {
        major = 0;
        var slash1 = actionPath.IndexOf((byte)'/');
        if (slash1 <= 0 || slash1 + 1 >= actionPath.Length)
        {
            return false;
        }

        var remainder = actionPath[(slash1 + 1)..];
        var slash2 = remainder.IndexOf((byte)'/');
        if (slash2 <= 0)
        {
            return TryExtractMajorFromSegment(slash2 < 0 ? remainder : remainder[..slash2], out major);
        }

        var repo = remainder[..slash2];
        if (TryExtractMajorFromSegment(repo, out major))
        {
            return true;
        }

        var subPath = remainder[(slash2 + 1)..];
        while (subPath.Length > 0)
        {
            var slash = subPath.IndexOf((byte)'/');
            var segment = slash < 0 ? subPath : subPath[..slash];
            if (TryExtractMajorFromSegment(segment, out major))
            {
                return true;
            }

            if (slash < 0)
            {
                break;
            }

            subPath = subPath[(slash + 1)..];
        }

        return false;
    }

    static bool TryExtractMajorFromSegment(ReadOnlySpan<byte> segment, out int major)
    {
        major = 0;
        if (segment.Length == 0)
        {
            return false;
        }

        var trimmed = TrimKnownExtension(segment);
        if (trimmed.Length < 2)
        {
            return false;
        }

        var candidateStart = -1;
        if ((trimmed[0] is (byte)'v' or (byte)'V') && IsDigit(trimmed[1]))
        {
            candidateStart = 1;
        }
        else
        {
            for (var i = 1; i + 1 < trimmed.Length; i++)
            {
                if ((trimmed[i - 1] is (byte)'-' or (byte)'_') && (trimmed[i] is (byte)'v' or (byte)'V') && IsDigit(trimmed[i + 1]))
                {
                    candidateStart = i + 1;
                    break;
                }
            }
        }

        if (candidateStart < 0)
        {
            return false;
        }

        var end = candidateStart;
        while (end < trimmed.Length && IsDigit(trimmed[end]))
        {
            end++;
        }

        return int.TryParse(System.Text.Encoding.UTF8.GetString(trimmed[candidateStart..end]), out major);
    }

    static ReadOnlySpan<byte> TrimKnownExtension(ReadOnlySpan<byte> segment)
    {
        if (segment.EndsWith(".yml"u8))
        {
            return segment[..^4];
        }

        if (segment.EndsWith(".yaml"u8))
        {
            return segment[..^5];
        }

        return segment;
    }

    static bool TryExtractVersionMajor(ReadOnlySpan<byte> reference, out int major)
    {
        major = 0;
        if (reference.Length < 2 || reference[0] is not ((byte)'v' or (byte)'V'))
        {
            return false;
        }

        var end = 1;
        while (end < reference.Length && IsDigit(reference[end]))
        {
            end++;
        }

        if (end == 1)
        {
            return false;
        }

        return int.TryParse(System.Text.Encoding.UTF8.GetString(reference[1..end]), out major);
    }

    static bool IsFullLengthCommitSha(ReadOnlySpan<byte> reference)
    {
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var ch = reference[i];
            var isDigit = ch is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = ch is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = ch is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    static bool IsDigit(byte value)
    {
        return value is >= (byte)'0' and <= (byte)'9';
    }
}
