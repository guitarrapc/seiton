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

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec)
        {
            return;
        }

        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var usesText = Arena.GetStringValue(actionExec.Uses);
        if (!PopularActions.TryGet(usesText, out var actionSpec))
        {
            return;
        }

        var actionName = GetCachedActionName(Arena.GetStringSlice(actionExec.Uses));

        // Check unknown inputs
        if (actionExec.Inputs is { Count: > 0 } inputs)
        {
            string[]? inputNames = null;
            string? availableInputs = null;
            var url = ActionRefHelpers.BuildGitHubUrl(actionName);
            var urlSuffix = url is not null ? $" see {url}" : "";

            foreach (var pair in inputs)
            {
                if (actionSpec.IsInputAllowed(pair.Key.AsSpan(Config.Utf8Yaml)))
                {
                    // Check deprecated inputs
                    var deprecationMessage = actionSpec.GetDeprecatedInputMessage(pair.Key.AsSpan(Config.Utf8Yaml));
                    if (!deprecationMessage.IsEmpty)
                    {
                        var inputName = Encoding.UTF8.GetString(pair.Key.AsSpan(Config.Utf8Yaml));
                        var message = Encoding.UTF8.GetString(deprecationMessage);
                        AddStepWarning(step, $"avoid using deprecated input \"{inputName}\" in action \"{actionName}\": {message}", Arena.GetStringRange(pair.Value));
                    }

                    continue;
                }

                var unknownInputName = Encoding.UTF8.GetString(pair.Key.AsSpan(Config.Utf8Yaml));
                inputNames ??= actionSpec.GetInputNames();
                var suggestion = FindClosestInput(unknownInputName, inputNames);
                availableInputs ??= FormatAvailableInputs(inputNames);
                var unknownMessage = suggestion is not null
                    ? $"unknown input '{unknownInputName}' for action '{actionName}'. available inputs are {availableInputs}. did you mean '{suggestion}'?{urlSuffix}"
                    : $"unknown input '{unknownInputName}' for action '{actionName}'. available inputs are {availableInputs}.{urlSuffix}";

                DiagnosticFix? fix = null;
                if (suggestion is not null && Config.Fix.Enabled)
                {
                    fix = new DiagnosticFix(
                        $"replace '{unknownInputName}' with '{suggestion}'",
                        [new TextEdit(pair.Key.Offset, pair.Key.Length, suggestion)]);
                }

                if (fix is not null)
                {
                    AddStepWarning(step, unknownMessage, Arena.GetStringRange(pair.Value), fix.Value);
                }
                else
                {
                    AddStepWarning(step, unknownMessage, Arena.GetStringRange(pair.Value));
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
            AddStepWarning(step, $"missing required input '{requiredName}' for action '{actionName}'");
        }
    }

    private bool IsInputProvided(ExecAction actionExec, ReadOnlySpan<byte> inputNameUtf8)
    {
        if (actionExec.Inputs is not { Count: > 0 } inputs || Config.Utf8Yaml is null)
        {
            return false;
        }

        foreach (var pair in inputs)
        {
            if (SpanHelpers.EqualsAsciiIgnoreCase(pair.Key.AsSpan(Config.Utf8Yaml), inputNameUtf8))
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
        string? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;

        for (var i = 0; i < inputNames.Length; i++)
        {
            var candidate = inputNames[i];
            var distance = EditDistance.ComputeIgnoreCase(unknownInput, candidate);
            if (distance < bestDistance && distance <= maxDistance)
            {
                bestDistance = distance;
                best = candidate;
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

    private static string FormatAvailableInputs(string[] inputNames)
    {
        return string.Join(", ", inputNames.Select(static n => $"\"{n}\""));
    }
}
