using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates input names for well-known popular actions against their declared schemas.</summary>
public sealed class PopularActionInputsRule() : RuleBase(RuleId.PopularActionInputs)
{
    // Cache last-decoded action name to avoid repeated Decode for the same uses slice
    private Utf8Slice _lastUsesSlice;
    private string? _lastActionName;

    public override string Name => "Popular Action Inputs Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        // Clear per-source cache — slice offsets are invalid across different source bytes.
        _lastUsesSlice = default;
        _lastActionName = null;
    }

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Action)
        {
            return;
        }

        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var actionExec = step.Exec.AsAction();
        var usesText = actionExec.Uses.Value;
        if (!PopularActions.TryGet(usesText, out var actionSpec))
        {
            return;
        }

        // Decoded name and GitHub URL are only consumed by diagnostic messages —
        // resolve them lazily so the clean path (all inputs valid) stays allocation-free.
        string? actionName = null;
        string ActionName() => actionName ??= GetCachedActionName(actionExec.Uses.Slice);

        // Check unknown inputs
        var inputs = actionExec.Inputs;
        if (inputs.Count > 0)
        {
            string[]? inputNames = null;
            string? availableInputs = null;
            string? urlSuffix = null;

            foreach (var pair in inputs)
            {
                if (actionSpec.IsInputAllowed(pair.Key.Bytes))
                {
                    // Check deprecated inputs
                    var deprecationMessage = actionSpec.GetDeprecatedInputMessage(pair.Key.Bytes);
                    if (!deprecationMessage.IsEmpty)
                    {
                        var inputName = pair.Key.Decode();
                        var message = Encoding.UTF8.GetString(deprecationMessage);
                        AddStepWarning(step, $"avoid using deprecated input \"{inputName}\" in action \"{ActionName()}\": {message}", pair.Value.Range);
                    }

                    continue;
                }

                var unknownInputName = pair.Key.Decode();
                inputNames ??= actionSpec.GetInputNames();
                var suggestion = FindClosestInput(unknownInputName, inputNames);
                availableInputs ??= FormatAvailableInputs(inputNames);
                urlSuffix ??= ActionRefHelpers.BuildGitHubUrl(ActionName()) is { } url ? $" see {url}" : "";
                var unknownMessage = suggestion is not null
                    ? $"unknown input '{unknownInputName}' for action '{ActionName()}'. available inputs are {availableInputs}. did you mean '{suggestion}'?{urlSuffix}"
                    : $"unknown input '{unknownInputName}' for action '{ActionName()}'. available inputs are {availableInputs}.{urlSuffix}";

                DiagnosticFix? fix = null;
                if (suggestion is not null && Config.Fix.Enabled)
                {
                    fix = new DiagnosticFix(
                        $"replace '{unknownInputName}' with '{suggestion}'",
                        [new TextEdit(pair.Key.Slice.Offset, pair.Key.Slice.Length, suggestion)]);
                }

                if (fix is not null)
                {
                    AddStepWarning(step, unknownMessage, pair.Value.Range, fix.Value);
                }
                else
                {
                    AddStepWarning(step, unknownMessage, pair.Value.Range);
                }
            }
        }

        // Check missing required inputs
        var requiredInputs = actionSpec.GetRequiredInputs();
        for (var i = 0; i < requiredInputs.Length; i++)
        {
            var requiredUtf8 = requiredInputs[i].AsSpan();
            if (IsInputProvided(actionExec, requiredUtf8))
            {
                continue;
            }

            var requiredName = Encoding.UTF8.GetString(requiredUtf8);
            AddStepWarning(step, $"missing required input '{requiredName}' for action '{ActionName()}'");
        }
    }

    private bool IsInputProvided(ExecActionRef actionExec, ReadOnlySpan<byte> inputNameUtf8)
    {
        var inputs = actionExec.Inputs;
        if (inputs.Count == 0 || Config.Utf8Yaml is null)
        {
            return false;
        }

        foreach (var pair in inputs)
        {
            if (SpanHelpers.EqualsAsciiIgnoreCase(pair.Key.Bytes, inputNameUtf8))
            {
                return true;
            }
        }

        return false;
    }

    private string GetCachedActionName(Utf8Slice usesSlice)
    {
        if (_lastActionName is not null
            && usesSlice.Length == _lastUsesSlice.Length
            && Config.Utf8Yaml is not null
            && usesSlice.AsSpan(Config.Utf8Yaml).SequenceEqual(_lastUsesSlice.AsSpan(Config.Utf8Yaml)))
        {
            _lastUsesSlice = usesSlice;
            return _lastActionName;
        }

        var name = Decode(usesSlice);
        _lastUsesSlice = usesSlice;
        _lastActionName = name;
        return name;
    }

    private static string? FindClosestInput(string unknownInput, string[] inputNames)
    {
        if (inputNames.Length == 0)
        {
            return null;
        }

        // Threshold: max distance is roughly 1/3 of the input name length, minimum 2
        var maxDistance = Math.Max(2, unknownInput.Length / 3);

        var count = inputNames.Length;
        var rented = count > 128 ? System.Buffers.ArrayPool<int>.Shared.Rent(count) : null;
        Span<int> distances = rented is not null ? rented.AsSpan(0, count) : stackalloc int[count];

        try
        {
            // Batch: builds the Myers pattern table from unknownInput once for all candidates.
            EditDistance.ComputeIgnoreCaseMany(unknownInput, inputNames, maxDistance, distances);

            string? best = null;
            var bestDistance = maxDistance + 1;
            var tied = false;

            for (var i = 0; i < count; i++)
            {
                var distance = distances[i];
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = inputNames[i];
                    tied = false;
                }
                else if (distance == bestDistance && distance <= maxDistance)
                {
                    tied = true;
                }
            }

            // When multiple candidates are equally close, suppress the suggestion
            return tied ? null : best;
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private static string FormatAvailableInputs(string[] inputNames)
    {
        return string.Join(", ", inputNames.Select(static n => $"\"{n}\""));
    }
}
