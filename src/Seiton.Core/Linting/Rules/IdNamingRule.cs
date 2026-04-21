using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class IdNamingRule : RuleBase
{
    public override string Id => "id-naming";

    public override string Name => "Id Naming Rule";

    private Job? _currentJob;
    private Step? _currentStep;
    private List<Utf8Slice>? _seenStepIdSlices;

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        _currentJob = job;
        _seenStepIdSlices = [];
        ValidateId(job.Id, "job id");
        _currentJob = null;
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Id is null)
        {
            return;
        }

        _currentStep = step;
        ValidateId(step.Id, "step id");
        ValidateStepIdUniqueness(step);
        _currentStep = null;
    }

    public override void VisitJobPost(Job job)
    {
        _seenStepIdSlices = null;
    }

    private void ValidateId(StringNode idNode, string kind)
    {
        var value = idNode.Value.AsSpan(Config.Utf8Yaml);
        if (idNode.Expression is not null || value.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (IsValidId(value))
        {
            return;
        }

        var idText = Decode(idNode.Value);
        var message = $"{kind} '{idText}' contains invalid characters; first character must be [a-zA-Z_], and remaining characters must be [a-zA-Z0-9_-]";

        if (_currentJob is not null)
        {
            AddJobError(_currentJob, message, idNode.Range);
        }
        else if (_currentStep is not null)
        {
            AddStepError(_currentStep, message, idNode.Range);
        }
    }

    private static bool IsValidId(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        var first = value[0];
        var firstIsUpper = first is >= (byte)'A' and <= (byte)'Z';
        var firstIsLower = first is >= (byte)'a' and <= (byte)'z';
        var firstIsUnderscore = first == (byte)'_';
        if (!firstIsUpper && !firstIsLower && !firstIsUnderscore)
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var b = value[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isUpper = b is >= (byte)'A' and <= (byte)'Z';
            var isLower = b is >= (byte)'a' and <= (byte)'z';
            var isDash = b == (byte)'-';
            var isUnderscore = b == (byte)'_';
            if (!isDigit && !isUpper && !isLower && !isDash && !isUnderscore)
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateStepIdUniqueness(Step step)
    {
        if (step.Id is null || _seenStepIdSlices is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var idSpan = step.Id.Value.AsSpan(Config.Utf8Yaml);
        if (idSpan.Length == 0)
        {
            return;
        }

        for (var i = 0; i < _seenStepIdSlices.Count; i++)
        {
            var seenSpan = _seenStepIdSlices[i].AsSpan(Config.Utf8Yaml);
            if (SpanEqualsIgnoreCaseAscii(seenSpan, idSpan))
            {
                var idText = Decode(step.Id.Value);
                AddStepError(step, $"step id '{idText}' is duplicated in the same job (case-insensitive)", step.Id.Range);
                return;
            }
        }

        _seenStepIdSlices.Add(step.Id.Value);
    }

    private static bool SpanEqualsIgnoreCaseAscii(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }
}
