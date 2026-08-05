using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates that local/composite action invocations provide required inputs and don't pass unknown ones.</summary>
public sealed class LocalActionInputsRule() : RuleBase(RuleId.LocalActionInputs)
{
    private readonly Dictionary<string, (ActionMetadata? Metadata, byte[]? Source, AstArena? Arena, DiagnosticList? ParseDiagnostics)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _metadataCheckedPaths = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "Local Action Inputs Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        DisposeCachedArenas();
        _cache.Clear();
        _metadataCheckedPaths.Clear();
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null
            || string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        if (step.Exec.Kind != StepExecKind.Action)
        {
            return;
        }

        var action = step.Exec.AsAction();
        if (!action.Uses.HasText)
        {
            return;
        }

        var uses = action.Uses.Value;
        if (!TryResolveLocalActionYamlPath(uses, out var actionYamlPath, out var actionDisplayPath, out var invalidRef))
        {
            if (invalidRef)
            {
                AddStepError(step, "local action uses must not contain '@ref'", BuildUsesLocation(action));
            }

            return;
        }

        if (!File.Exists(actionYamlPath))
        {
            return;
        }

        if (!TryGetCachedAction(actionYamlPath, out var meta, out var actionSource, out var actionArena) || meta is null || actionSource is null || actionArena is null)
        {
            return;
        }

        if (_metadataCheckedPaths.Add(actionYamlPath))
        {
            ValidateRunsUsing(step, action, meta, actionSource, actionArena);
            ValidateMetadata(step, action, meta, actionSource, actionArena, actionYamlPath, actionDisplayPath);
        }

        if (!meta.Inputs.HasValue || meta.Inputs.Count == 0)
        {
            if (action.Inputs.HasValue)
            {
                foreach (var pair in action.Inputs)
                {
                    var inputName = pair.Key.Decode();
                    AddStepError(
                        step,
                        $"local action does not declare inputs; unknown input '{inputName}'",
                        pair.Value.Range);
                }
            }

            return;
        }

        if (action.Inputs.HasValue)
        {
            foreach (var pair in action.Inputs)
            {
                var inputName = pair.Key.Decode();
                if (!TryFindMetadataInput(actionSource, actionArena, meta.Inputs, inputName, out var inputDef))
                {
                    AddStepError(step, FormatUnknownInputMessage(actionSource, actionArena, inputName, meta.Inputs), pair.Value.Range);
                    continue;
                }

                if (inputDef.DeprecationMessage.HasValue && HasNodeValue(inputDef.DeprecationMessage, actionArena))
                {
                    var depText = DecodeSlice(actionSource, actionArena.GetStringSlice(inputDef.DeprecationMessage));
                    AddStepWarning(step, $"input '{inputName}' is deprecated: {depText}", pair.Value.Range);
                }
            }
        }

        for (var i = 0; i < meta.Inputs.Count; i++)
        {
            ref readonly var def = ref actionArena.GetActionMetadataInputAt(meta.Inputs, i);
            if (def.Required.HasValue && actionArena.GetBoolValue(def.Required))
            {
                // required is true - check if default is set
            }
            else
            {
                continue;
            }

            if (def.Default.HasValue)
            {
                continue;
            }

            var name = DecodeSlice(actionSource, def.Key);
            if (action.Inputs.HasValue && ContainsInputName(action.Inputs, name))
            {
                continue;
            }

            AddStepError(
                step,
                $"required input '{name}' is not set for local action",
                BuildUsesLocation(action));
        }
    }

    private void DisposeCachedArenas()
    {
        foreach (var entry in _cache.Values)
        {
            entry.Arena?.Dispose();
        }
    }

    private bool TryGetCachedAction(string actionYamlPath, out ActionMetadata? metadata, out byte[]? source, out AstArena? arena)
    {
        if (_cache.TryGetValue(actionYamlPath, out var entry))
        {
            metadata = entry.Metadata;
            source = entry.Source;
            arena = entry.Arena;
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(actionYamlPath);
        }
        catch
        {
            _cache[actionYamlPath] = (null, null, null, null);
            metadata = null;
            source = null;
            arena = null;
            return true;
        }

        var parseResult = WorkflowParser.ParseDirect(bytes, actionYamlPath, out var parsedArena);
        if (parseResult.HasFatalError || parseResult.ActionMetadata is null)
        {
            parsedArena?.Dispose();
            _cache[actionYamlPath] = (null, null, null, null);
            metadata = null;
            source = null;
            arena = null;
            return true;
        }

        _cache[actionYamlPath] = (parseResult.ActionMetadata, bytes, parsedArena, parseResult.Diagnostics);
        metadata = parseResult.ActionMetadata;
        source = bytes;
        arena = parsedArena;
        return true;
    }

    private void ValidateRunsUsing(StepRef step, ExecActionRef action, ActionMetadata meta, byte[] actionSource, AstArena actionArena)
    {
        if (!meta.Runs.HasValue)
        {
            return;
        }

        ref readonly var runs = ref actionArena.GetActionMetadataRuns(meta.Runs);
        if (!HasNodeValue(runs.Using, actionArena))
        {
            return;
        }

        var span = actionArena.GetStringSlice(runs.Using).AsSpan(actionSource);
        if (span.IsEmpty)
        {
            return;
        }

        if (Utf8EqualsAsciiIgnoreCase(span, "node20"u8)
            || Utf8EqualsAsciiIgnoreCase(span, "node24"u8)
            || Utf8EqualsAsciiIgnoreCase(span, "composite"u8)
            || Utf8EqualsAsciiIgnoreCase(span, "docker"u8))
        {
            return;
        }

        if (Utf8EqualsAsciiIgnoreCase(span, "node12"u8)
            || Utf8EqualsAsciiIgnoreCase(span, "node16"u8))
        {
            AddStepError(
                step,
                $"local action uses deprecated runner '{Encoding.UTF8.GetString(span)}' (runs.using); use node20, node24, composite, or docker",
                BuildUsesLocation(action));
            return;
        }

        AddStepError(
            step,
            $"local action has invalid runs.using '{Encoding.UTF8.GetString(span)}'; expected node20, node24, composite, or docker",
            BuildUsesLocation(action));
    }

    private void ValidateMetadata(StepRef step, ExecActionRef action, ActionMetadata meta, byte[] actionSource, AstArena actionArena, string actionYamlPath, string displayPath)
    {
        var usesLocation = BuildUsesLocation(action);
        var actionName = meta.Name.HasValue && HasNodeValue(meta.Name, actionArena)
            ? DecodeSlice(actionSource, actionArena.GetStringSlice(meta.Name))
            : Encoding.UTF8.GetString(action.Uses.Value);
        var actionDir = Path.GetDirectoryName(actionYamlPath) ?? actionYamlPath;
        var displayDir = displayPath.Contains('/') ? displayPath[..displayPath.LastIndexOf('/')] : displayPath;

        // 1. description is required
        if (!meta.Description.HasValue || !HasNodeValue(meta.Description, actionArena))
        {
            AddStepError(step, $"description is required in metadata of \"{actionName}\" action at \"{displayPath}\"", usesLocation);
        }

        if (meta.Runs.HasValue)
        {
            ref readonly var runs = ref actionArena.GetActionMetadataRuns(meta.Runs);
            var isJsAction = IsJavaScriptAction(in runs, actionSource, actionArena);

            // 2. env not allowed for JavaScript actions
            if (isJsAction && runs.Env.HasValue)
            {
                AddStepError(step, $"\"env\" is not allowed in \"runs\" section because \"{actionName}\" is a JavaScript action", usesLocation);
            }

            // 3. File existence for JavaScript entry points (main, pre, post)
            if (isJsAction)
            {
                ValidateJsEntryPoint(step, runs.Main, "main", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
                ValidateJsEntryPoint(step, runs.Pre, "pre", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
                ValidateJsEntryPoint(step, runs.Post, "post", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
            }
        }

        // 4-5. Forward branding diagnostics from parser
        if (_cache.TryGetValue(actionYamlPath, out var cached) && cached.ParseDiagnostics is { Length: > 0 } parseDiags)
        {
            foreach (var diag in parseDiags)
            {
                if (diag.Message.StartsWith("invalid branding", StringComparison.Ordinal))
                {
                    AddStepError(step, $"{diag.Message} in metadata of \"{actionName}\" action at \"{displayPath}\"", usesLocation);
                }
            }
        }
    }

    private static bool IsJavaScriptAction(in ActionMetadataRunsData runs, byte[] actionSource, AstArena actionArena)
    {
        if (!runs.Using.HasValue || !HasNodeValue(runs.Using, actionArena))
        {
            return false;
        }

        var span = actionArena.GetStringSlice(runs.Using).AsSpan(actionSource);
        return span.Length >= 4
            && (span[0] == (byte)'n' || span[0] == (byte)'N')
            && (span[1] == (byte)'o' || span[1] == (byte)'O')
            && (span[2] == (byte)'d' || span[2] == (byte)'D')
            && (span[3] == (byte)'e' || span[3] == (byte)'E');
    }

    private void ValidateJsEntryPoint(StepRef step, StringNodeId entryPoint, string keyName, string actionName, string actionDir, string displayDir, byte[] actionSource, AstArena actionArena, TextRange usesLocation)
    {
        if (!entryPoint.HasValue || !HasNodeValue(entryPoint, actionArena))
        {
            return;
        }

        var fileName = DecodeSlice(actionSource, actionArena.GetStringSlice(entryPoint));
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(actionDir, fileName));
        }
        catch
        {
            return;
        }

        if (!File.Exists(fullPath))
        {
            AddStepError(step, $"file \"{fileName}\" does not exist in \"{displayDir}\". it is specified at \"{keyName}\" key in \"runs\" section in \"{actionName}\" action", usesLocation);
        }
    }

    private static bool TryFindMetadataInput(
        byte[] source,
        AstArena arena,
        NodeRange inputs,
        string name,
        out ActionMetadataInputData input)
    {
        for (var i = 0; i < inputs.Count; i++)
        {
            ref readonly var row = ref arena.GetActionMetadataInputAt(inputs, i);
            if (string.Equals(DecodeSlice(source, row.Key), name, StringComparison.OrdinalIgnoreCase))
            {
                input = row;
                return true;
            }
        }

        input = default;
        return false;
    }

    private static bool ContainsInputName(ActionInputRefMap provided, string name)
    {
        foreach (var kv in provided)
        {
            if (string.Equals(kv.Key.Decode(), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatUnknownInputMessage(byte[] source, AstArena arena, string inputName, NodeRange declared)
    {
        var names = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            names.Add(DecodeSlice(source, arena.GetActionMetadataInputAt(declared, i).Key));
        }

        names.Sort(StringComparer.Ordinal);
        return $"unknown local action input '{inputName}'; declared inputs are: {string.Join(", ", names)}";
    }

    private static string DecodeSlice(byte[] source, Utf8Slice slice)
    {
        if (slice.Length <= 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(slice.AsSpan(source));
    }

    private static bool Utf8EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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

    private bool TryResolveLocalActionYamlPath(ReadOnlySpan<byte> uses, out string actionYamlPath, out string displayPath, out bool invalidRefFormat)
    {
        actionYamlPath = string.Empty;
        displayPath = string.Empty;
        invalidRefFormat = false;

        if (!ActionRefHelpers.IsLocalActionUses(uses))
        {
            return false;
        }

        if (uses.IndexOf((byte)'@') >= 0)
        {
            invalidRefFormat = true;
            return false;
        }

        var usesStr = Encoding.UTF8.GetString(uses); // Keep forward slashes for display
        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(Config.FilePath!, usesStr);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }

        var normalizedResolvedPath = ActionRefHelpers.NormalizeFullPath(baseDirectory, usesStr);
        if (normalizedResolvedPath is null)
        {
            return false;
        }

        var resolvedPath = normalizedResolvedPath;

        if (File.Exists(resolvedPath))
        {
            var fileName = Path.GetFileName(resolvedPath);
            if (fileName.Equals("action.yml", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("action.yaml", StringComparison.OrdinalIgnoreCase))
            {
                actionYamlPath = resolvedPath;
                displayPath = usesStr;
                return true;
            }
        }

        if (Directory.Exists(resolvedPath))
        {
            var yml = Path.Combine(resolvedPath, "action.yml");
            var yaml = Path.Combine(resolvedPath, "action.yaml");
            if (File.Exists(yml))
            {
                actionYamlPath = yml;
                displayPath = usesStr + "/action.yml";
                return true;
            }

            if (File.Exists(yaml))
            {
                actionYamlPath = yaml;
                displayPath = usesStr + "/action.yaml";
                return true;
            }
        }

        return false;
    }

}
