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

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _cache.Clear();
        _metadataCheckedPaths.Clear();
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null
            || string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        if (step.Exec is not ExecAction action || !HasNodeValue(action.Uses, Arena))
        {
            return;
        }

        var uses = Arena.GetStringValue(action.Uses);
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

        if (meta.Inputs is null || meta.Inputs.Value.Count == 0)
        {
            if (action.Inputs is not null)
            {
                foreach (var pair in action.Inputs.Value)
                {
                    var inputName = Decode(pair.Key);
                    AddStepError(
                        step,
                        $"local action does not declare inputs; unknown input '{inputName}'",
                        Arena.GetStringRange(pair.Value));
                }
            }

            return;
        }

        if (action.Inputs is not null)
        {
            foreach (var pair in action.Inputs.Value)
            {
                var inputName = Decode(pair.Key);
                if (!TryFindMetadataInput(actionSource, meta.Inputs.Value, inputName, out var inputDef))
                {
                    AddStepError(step, FormatUnknownInputMessage(actionSource, inputName, meta.Inputs.Value), Arena.GetStringRange(pair.Value));
                    continue;
                }

                if (inputDef.DeprecationMessage.HasValue && HasNodeValue(inputDef.DeprecationMessage, actionArena))
                {
                    var depText = DecodeSlice(actionSource, actionArena.GetStringSlice(inputDef.DeprecationMessage));
                    AddStepWarning(step, $"input '{inputName}' is deprecated: {depText}", Arena.GetStringRange(pair.Value));
                }
            }
        }

        foreach (var kv in meta.Inputs.Value)
        {
            var def = kv.Value;
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

            var name = DecodeSlice(actionSource, kv.Key);
            if (action.Inputs is not null && ContainsInputName(Config.Utf8Yaml!, action.Inputs.Value, name))
            {
                continue;
            }

            AddStepError(
                step,
                $"required input '{name}' is not set for local action",
                BuildUsesLocation(action));
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

        var parseResult = WorkflowParser.Parse(bytes, actionYamlPath);
        if (parseResult.HasFatalError || parseResult.ActionMetadata is null)
        {
            _cache[actionYamlPath] = (null, null, null, null);
            metadata = null;
            source = null;
            arena = null;
            return true;
        }

        _cache[actionYamlPath] = (parseResult.ActionMetadata, bytes, parseResult.Arena, parseResult.Diagnostics);
        metadata = parseResult.ActionMetadata;
        source = bytes;
        arena = parseResult.Arena;
        return true;
    }

    private void ValidateRunsUsing(Step step, ExecAction action, ActionMetadata meta, byte[] actionSource, AstArena actionArena)
    {
        if (meta.Runs?.Using is null || !HasNodeValue(meta.Runs.Using, actionArena))
        {
            return;
        }

        var span = actionArena.GetStringSlice(meta.Runs.Using).AsSpan(actionSource);
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

    private void ValidateMetadata(Step step, ExecAction action, ActionMetadata meta, byte[] actionSource, AstArena actionArena, string actionYamlPath, string displayPath)
    {
        var usesLocation = BuildUsesLocation(action);
        var actionName = meta.Name.HasValue && HasNodeValue(meta.Name, actionArena)
            ? DecodeSlice(actionSource, actionArena.GetStringSlice(meta.Name))
            : Encoding.UTF8.GetString(Arena.GetStringValue(action.Uses));
        var actionDir = Path.GetDirectoryName(actionYamlPath) ?? actionYamlPath;
        var displayDir = displayPath.Contains('/') ? displayPath[..displayPath.LastIndexOf('/')] : displayPath;

        // 1. description is required
        if (!meta.Description.HasValue || !HasNodeValue(meta.Description, actionArena))
        {
            AddStepError(step, $"description is required in metadata of \"{actionName}\" action at \"{displayPath}\"", usesLocation);
        }

        if (meta.Runs is not null)
        {
            var isJsAction = IsJavaScriptAction(meta.Runs, actionSource, actionArena);

            // 2. env not allowed for JavaScript actions
            if (isJsAction && meta.Runs.Env is not null)
            {
                AddStepError(step, $"\"env\" is not allowed in \"runs\" section because \"{actionName}\" is a JavaScript action", usesLocation);
            }

            // 3. File existence for JavaScript entry points (main, pre, post)
            if (isJsAction)
            {
                ValidateJsEntryPoint(step, meta.Runs.Main, "main", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
                ValidateJsEntryPoint(step, meta.Runs.Pre, "pre", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
                ValidateJsEntryPoint(step, meta.Runs.Post, "post", actionName, actionDir, displayDir, actionSource, actionArena, usesLocation);
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

    private static bool IsJavaScriptAction(ActionMetadataRuns runs, byte[] actionSource, AstArena actionArena)
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

    private void ValidateJsEntryPoint(Step step, StringNodeId entryPoint, string keyName, string actionName, string actionDir, string displayDir, byte[] actionSource, AstArena actionArena, TextRange usesLocation)
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
        SliceMap<ActionMetadataInput> inputs,
        string name,
        out ActionMetadataInput input)
    {
        foreach (var kv in inputs)
        {
            if (string.Equals(DecodeSlice(source, kv.Key), name, StringComparison.OrdinalIgnoreCase))
            {
                input = kv.Value;
                return true;
            }
        }

        input = null!;
        return false;
    }

    private static bool ContainsInputName(byte[] source, SliceMap<StringNodeId> provided, string name)
    {
        foreach (var kv in provided)
        {
            if (string.Equals(DecodeSlice(source, kv.Key), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatUnknownInputMessage(byte[] source, string inputName, SliceMap<ActionMetadataInput> declared)
    {
        var names = new List<string>(declared.Count);
        foreach (var kv in declared)
        {
            names.Add(DecodeSlice(source, kv.Key));
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

        if (!uses.StartsWith("./"u8) && !uses.StartsWith("../"u8))
        {
            return false;
        }

        if (uses.IndexOf((byte)'@') >= 0)
        {
            invalidRefFormat = true;
            return false;
        }

        var usesStr = DecodeAscii(uses); // Keep forward slashes for display
        var relativePath = usesStr.Replace('/', Path.DirectorySeparatorChar);
        var baseDirectory = ResolveLocalReferenceBaseDirectory(Config.FilePath!, relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, TrimCurrentDirectoryPrefix(relativePath)));
        }
        catch
        {
            return false;
        }

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

    private static string ResolveLocalReferenceBaseDirectory(string workflowFilePath, string localPath)
    {
        var workflowDirectory = Path.GetDirectoryName(workflowFilePath);
        if (string.IsNullOrEmpty(workflowDirectory))
        {
            return string.Empty;
        }

        if (localPath.StartsWith($".{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && TryGetRepositoryRoot(workflowFilePath, out var repositoryRoot))
        {
            return repositoryRoot;
        }

        return workflowDirectory;
    }

    private static bool TryGetRepositoryRoot(string workflowFilePath, out string repositoryRoot)
    {
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}.github{separator}workflows{separator}";
        var index = workflowFilePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            repositoryRoot = workflowFilePath[..index];
            return true;
        }

        var markerAtEnd = $"{separator}.github{separator}workflows";
        if (workflowFilePath.EndsWith(markerAtEnd, StringComparison.OrdinalIgnoreCase))
        {
            repositoryRoot = workflowFilePath[..^markerAtEnd.Length];
            return true;
        }

        repositoryRoot = string.Empty;
        return false;
    }

    private static string TrimCurrentDirectoryPrefix(string path)
    {
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }
}
