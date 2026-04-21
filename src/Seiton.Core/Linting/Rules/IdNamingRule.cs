using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class IdNamingRule : RuleBase
{
    public override string Id => "id-naming";

    public override string Name => "Id Naming Rule";

    private Job? _currentJob;
    private Step? _currentStep;

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        _currentJob = job;
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
        _currentStep = null;
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
        var message = $"{kind} '{idText}' contains invalid characters; allowed characters are [a-zA-Z0-9_-]";

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

        for (var i = 0; i < value.Length; i++)
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
}
