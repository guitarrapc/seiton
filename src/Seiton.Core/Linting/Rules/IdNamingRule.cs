using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Checks that job and step <c>id:</c> values follow naming conventions.</summary>
public sealed class IdNamingRule() : RuleBase(RuleId.IdNaming)
{
    public override string Name => "Id Naming Rule";

    private WorkflowRef _workflow;
    private JobRef _currentJob;
    private StepRef _currentStep;
    private List<Utf8Slice>? _seenStepIdSlices;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _workflow = workflow;
    }

    public override void VisitJobPre(JobRef job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        _currentJob = job;
        _seenStepIdSlices = [];
        ValidateId(job.Id, "job ID");
        _currentJob = default;
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || !step.Id.HasValue)
        {
            return;
        }

        _currentStep = step;
        ValidateId(step.Id, "step ID");
        ValidateStepIdUniqueness(step);
        _currentStep = default;
    }

    public override void VisitJobPost(JobRef job)
    {
        _seenStepIdSlices = null;
    }

    public override void VisitWorkflowPost(WorkflowRef workflow)
    {
        _workflow = default;
    }

    private void ValidateId(StringRef idNode, string kind)
    {
        var value = idNode.Value;
        if (ExpressionScanHelpers.ContainsExpressionMarker(idNode.Id, Arena))
        {
            return;
        }

        if (IsValidId(value))
        {
            return;
        }

        var idText = idNode.Decode();
        var message = value.Length == 0
            ? $"{kind} should not be empty"
            : $"invalid {kind} \"{idText}\". {kind} must start with a letter or _ and contain only alphanumeric characters, -, or _";

        DiagnosticFix? fix = null;
        if (value.Length > 0 && _currentJob.HasValue)
        {
            fix = BuildJobIdFix(idNode, idText);
        }

        if (_currentJob.HasValue)
        {
            if (fix is not null)
            {
                AddJobError(_currentJob, message, idNode.Range, fix.Value);
            }
            else
            {
                AddJobError(_currentJob, message, idNode.Range);
            }
        }
        else if (_currentStep.HasValue)
        {
            AddStepError(_currentStep, message, idNode.Range);
        }
    }

    private DiagnosticFix? BuildJobIdFix(StringRef idNode, string originalId)
    {
        var newId = ToKebabCase(originalId);
        if (newId.Length == 0)
        {
            return null;
        }

        var newIdUtf8 = Encoding.UTF8.GetBytes(newId);
        if (!IsValidId(newIdUtf8))
        {
            return null;
        }

        var currentJobIdUtf8 = idNode.Value;

        if (_workflow.HasValue)
        {
            foreach (var (_, job) in _workflow.Jobs)
            {
                var existingJobIdUtf8 = job.Id.Value;
                if (SpanEqualsIgnoreCaseAscii(existingJobIdUtf8, newIdUtf8)
                    && !SpanEqualsIgnoreCaseAscii(existingJobIdUtf8, currentJobIdUtf8))
                {
                    return null;
                }
            }
        }

        var edits = new List<TextEdit>();

        // Edit for the job ID key itself
        var idEdit = BuildSliceReplacementEdit(idNode, newId);
        edits.Add(idEdit);

        // Edit for all needs references to this job ID across all jobs
        if (_workflow.HasValue && Config.Utf8Yaml is not null)
        {
            foreach (var (_, job) in _workflow.Jobs)
            {
                if (!job.Needs.HasValue)
                {
                    continue;
                }

                for (var i = 0; i < job.Needs.Count; i++)
                {
                    var needsNode = job.Needs[i];
                    if (!needsNode.HasValue)
                    {
                        continue;
                    }

                    var needsValue = needsNode.Value;
                    if (SpanEqualsIgnoreCaseAscii(needsValue, currentJobIdUtf8))
                    {
                        var needsEdit = BuildSliceReplacementEdit(needsNode, newId);
                        edits.Add(needsEdit);
                    }
                }
            }
        }

        return new DiagnosticFix($"rename job ID to '{newId}'", edits.ToArray());
    }

    private TextEdit BuildSliceReplacementEdit(StringRef node, string newText)
    {
        var slice = node.Slice;
        var offset = slice.Offset;
        var length = slice.Length;

        // If the node is quoted, expand the range to include quotes
        if (node.Quoted && Config.Utf8Yaml is not null)
        {
            var before = offset - 1;
            var after = offset + length;
            if (before >= 0 && after < Config.Utf8Yaml.Length)
            {
                var bc = Config.Utf8Yaml[before];
                var ac = Config.Utf8Yaml[after];
                if ((bc == (byte)'\'' && ac == (byte)'\'') || (bc == (byte)'"' && ac == (byte)'"'))
                {
                    offset = before;
                    length += 2;
                }
            }
        }

        return new TextEdit(offset, length, newText);
    }

    private static string ToKebabCase(string input)
    {
        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is >= 'A' and <= 'Z')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                {
                    // Insert hyphen before uppercase if previous char is lowercase/digit
                    if (i > 0)
                    {
                        var prev = input[i - 1];
                        if (prev is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                        {
                            sb.Append('-');
                        }
                    }
                }

                sb.Append((char)(c + 32)); // to lowercase
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            {
                sb.Append(c);
            }
            else if (c == '_')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                {
                    sb.Append('-');
                }
            }
            else
            {
                // Replace invalid chars (spaces, dots, etc.) with hyphen
                if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                {
                    sb.Append('-');
                }
            }
        }

        // Trim leading/trailing hyphens
        var result = sb.ToString().Trim('-');

        // Collapse consecutive hyphens
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-");
        }

        return result;
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

    private void ValidateStepIdUniqueness(StepRef step)
    {
        if (!step.Id.HasValue || _seenStepIdSlices is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var idSpan = step.Id.Value;
        if (idSpan.Length == 0)
        {
            return;
        }

        for (var i = 0; i < _seenStepIdSlices.Count; i++)
        {
            var seenSpan = _seenStepIdSlices[i].AsSpan(Config.Utf8Yaml);
            if (SpanEqualsIgnoreCaseAscii(seenSpan, idSpan))
            {
                var idText = step.Id.Decode();
                AddStepError(step, $"step id '{idText}' is duplicated in the same job (case-insensitive)", step.Id.Range);
                return;
            }
        }

        _seenStepIdSlices.Add(step.Id.Slice);
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
