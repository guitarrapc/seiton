using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// Hand-written pull-parser that converts UTF-8 YAML into the typed workflow/action metadata AST.
/// Partial class split by section: Jobs, Steps, Strategy, Events, Containers, etc.
/// </summary>
public static partial class WorkflowParser
{
    private delegate string? Utf8ScalarValidator(ReadOnlySpan<byte> valueUtf8);

    private enum ParseMode
    {
        Workflow,
        ActionMetadata,
    }

    private readonly struct OnEventInfo
    {
        public OnEventInfo(string name, bool isKnown, WebhookTypes.EventSpec spec)
        {
            Name = name;
            IsKnown = isKnown;
            Spec = spec;
        }

        public string Name { get; }

        public bool IsKnown { get; }

        public WebhookTypes.EventSpec Spec { get; }
    }

    /// <summary>Parses UTF-8 YAML into a <see cref="ParseResult"/> containing the workflow or action metadata AST.</summary>
    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        return ParseClassified(utf8Yaml, filePath).ParseResult;
    }

    /// <summary>Parses UTF-8 YAML into a <see cref="ClassifiedParseResult"/> containing the AST and document kind classification.</summary>
    public static ClassifiedParseResult ParseClassified(byte[] utf8Yaml, string filePath)
    {
        var pathHintKind = DocumentKindClassifier.GetPathHintKind(filePath);

        try
        {
            var hintReader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
            var hasHints = TryReadRootStructuralHints(ref hintReader, out var hasJobs, out var hasRuns);
            var finalKind = hasHints
                ? DocumentKindClassifier.FinalizeKind(pathHintKind, hasJobs, hasRuns, out var ignoredAmbiguous, out var ignoredHintMismatch)
                : pathHintKind;

            var isAmbiguous = hasHints && hasJobs && hasRuns;
            var hasHintMismatch =
                hasHints &&
                pathHintKind == DocumentKind.ActionMetadata &&
                finalKind == DocumentKind.Workflow;

            var parseReader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
            var parseMode = finalKind == DocumentKind.ActionMetadata ? ParseMode.ActionMetadata : ParseMode.Workflow;
            var arena = AstArena.Rent(utf8Yaml);
            var parseResult = ParseCore(ref parseReader, arena, utf8Yaml, parseMode);

            // Check for unused anchors and recursive aliases after parsing while the adapter is still alive
            var unusedBuf = new (string Name, TextPosition Position)[8];
            var unusedAnchors = parseReader.GetUnusedAnchors(unusedBuf);
            var recursiveBuf = new (string Name, TextPosition Position)[8];
            var recursiveAliases = parseReader.GetRecursiveAliases(recursiveBuf);

            var diagnostics = new PooledBuffer<Diagnostic>(parseResult.Diagnostics.Length + 2 + unusedAnchors.Length + recursiveAliases.Length);
            try
            {
                for (var i = 0; i < parseResult.Diagnostics.Length; i++)
                {
                    diagnostics.Add(parseResult.Diagnostics[i]);
                }

                for (var i = 0; i < unusedAnchors.Length; i++)
                {
                    var (name, pos) = unusedAnchors[i];
                    AddWarning(ref diagnostics, $"anchor \"{name}\" is defined but not used", pos);
                }

                for (var i = 0; i < recursiveAliases.Length; i++)
                {
                    var (name, pos) = recursiveAliases[i];
                    AddError(ref diagnostics, $"recursive alias \"{name}\" is found", pos);
                }

                if (isAmbiguous)
                {
                    AddError(ref diagnostics, "document kind is ambiguous: root has both 'jobs' and 'runs'", new TextPosition(0, 1, 1));
                }

                if (hasHintMismatch)
                {
                    AddError(ref diagnostics, "path hint suggests action-metadata but root structure indicates workflow", new TextPosition(0, 1, 1));
                }

                if (!string.IsNullOrEmpty(filePath) && diagnostics.Count > 0)
                {
                    var span = diagnostics.AsSpan();
                    for (var i = 0; i < span.Length; i++)
                    {
                        diagnostics.Replace(i, span[i] with { FilePath = filePath });
                    }
                }

                parseResult = parseResult with { Diagnostics = diagnostics.ToArray() };
            }
            finally { diagnostics.Dispose(); }

            return new ClassifiedParseResult(
                parseResult,
                new DocumentKindClassification(pathHintKind, finalKind, hasHintMismatch, isAmbiguous));
        }
        catch (Exception ex)
        {
            var (startLine, startColumn) = TryExtractLineCol(ex.Message);
            var location = new TextRange(
                Start: 0,
                Length: 0,
                StartLine: startLine,
                StartColumn: startColumn,
                EndLine: startLine,
                EndColumn: startColumn);
            var diagnostic = new Diagnostic(
                Severity: DiagnosticSeverity.Error,
                Message: $"yaml parse failure: {ex.Message}",
                Location: location,
                FilePath: string.IsNullOrEmpty(filePath) ? null : filePath);
            var parseResult = new ParseResult(default, default, [diagnostic], HasFatalError: true);
            return new ClassifiedParseResult(
                parseResult,
                new DocumentKindClassification(pathHintKind, DocumentKind.Unknown, HasHintMismatch: false, IsAmbiguous: false));
        }
    }

    /// <summary>
    /// Extracts line/col from VYaml exception messages (format: "... at Line: {L}, Col: {C}, Idx: {I}").
    /// Returns (1, 1) if extraction fails.
    /// </summary>
    internal static (int Line, int Column) TryExtractLineCol(string message)
    {
        // VYaml format: "... at Line: 5, Col: 3, Idx: 42"
        const string lineMarker = "Line: ";
        const string colMarker = "Col: ";

        var lineIdx = message.LastIndexOf(lineMarker, StringComparison.Ordinal);
        if (lineIdx < 0)
        {
            return (1, 1);
        }

        var lineStart = lineIdx + lineMarker.Length;
        var lineEnd = message.IndexOf(',', lineStart);
        if (lineEnd < 0)
        {
            return (1, 1);
        }

        var colIdx = message.IndexOf(colMarker, lineEnd, StringComparison.Ordinal);
        if (colIdx < 0)
        {
            return (1, 1);
        }

        var colStart = colIdx + colMarker.Length;
        var colEnd = message.IndexOf(',', colStart);
        if (colEnd < 0)
        {
            colEnd = message.Length;
        }

        if (int.TryParse(message.AsSpan(lineStart, lineEnd - lineStart), out var line)
            && int.TryParse(message.AsSpan(colStart, colEnd - colStart), out var col))
        {
            // VYaml line is 1-based; col is 0-based → convert col to 1-based
            return (line, col + 1);
        }

        return (1, 1);
    }

    internal static ParseResult ParseWithReader<TReader>(ref TReader reader, AstArena arena, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        return ParseCore(ref reader, arena, source, ParseMode.Workflow);
    }

    private static bool TryReadRootStructuralHints<TReader>(ref TReader reader, out bool hasJobs, out bool hasRuns)
        where TReader : IYamlStreamReader, allows ref struct
    {
        hasJobs = false;
        hasRuns = false;

        reader.SkipHeader();
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            return false;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyUtf8 = reader.GetScalarUtf8();
            if (Utf8MappingDispatch.TryMatchFirstOrdered<RootStructuralHintKeyTable>(keyUtf8, out var hintOrdinal))
            {
                if (hintOrdinal == 0)
                {
                    hasJobs = true;
                }
                else
                {
                    hasRuns = true;
                }
            }

            reader.Read();
            if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                reader.SkipCurrentNode();
            }
        }

        return true;
    }

    private static ParseResult ParseCore<TReader>(ref TReader reader, AstArena arena, ReadOnlySpan<byte> source, ParseMode parseMode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var diagnostics = new List<Diagnostic>(16);

        reader.SkipHeader();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentStart);
            return new ParseResult(default, default, diagnostics.ToArray(), HasFatalError: true);
        }

        var workflowStart = reader.CurrentStart;
        var workflowRange = BuildScalarLocation(workflowStart, 1);

        reader.Read(); // skip MappingStart

        StringNodeId nameNode = default;
        StringNodeId runNameNode = default;
        Permissions? permissionsNode = null;
        Env? envNode = null;
        Defaults? defaultsNode = null;
        Concurrency? concurrencyNode = null;
        var hasOn = false;
        var hasJobs = false;
        var lastRootKeyMark = new TextPosition(0, 1, 1);
        Event[] onEvents = [];
        SliceMap<Job> jobs = default;
        ulong seen = 0;
        StringNodeId actionDescription = default;
        SliceMap<ActionMetadataInput>? actionInputs = null;
        SliceMap<ActionMetadataOutput>? actionOutputs = null;
        ActionMetadataRuns? actionRuns = null;
        ActionMetadataBranding? actionBranding = null;
        ulong actionSeen = 0;

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "workflow key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            lastRootKeyMark = keyMark;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "workflow"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowRootKeyTable>(keyUtf8, out var workflowKeyOrdinal))
            {
                reader.Read();
                var wk = (WorkflowRootMappingKey)workflowKeyOrdinal;
                if (!TrySetBit(ref seen, workflowKeyOrdinal))
                {
                    AddError(diagnostics, $"workflow contains duplicate key: {WorkflowRootDuplicateKeyName(wk)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (wk)
                {
                    case WorkflowRootMappingKey.Name:
                        nameNode = ParseString(ref reader, arena, diagnostics, "name must be scalar");
                        continue;
                    case WorkflowRootMappingKey.RunName:
                        runNameNode = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            ExpressionValidationContext.RunName,
                            "run-name must be scalar",
                            parseWholeValueIfNoEmbedded: false);
                        continue;
                    case WorkflowRootMappingKey.On:
                        hasOn = true;
                        if (!reader.End)
                        {
                            if (reader.CurrentKind is not YamlEventKind.Scalar and not YamlEventKind.MappingStart and not YamlEventKind.SequenceStart)
                            {
                                AddError(diagnostics, "on must be scalar, mapping, or sequence", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                onEvents = ParseOnEvents(ref reader, arena, diagnostics, source);
                            }
                        }

                        continue;
                    case WorkflowRootMappingKey.Jobs:
                        hasJobs = true;
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.MappingStart)
                            {
                                AddError(diagnostics, "jobs must be mapping", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                jobs = ParseJobsMapping(ref reader, arena, diagnostics, source);
                            }
                        }

                        continue;
                    case WorkflowRootMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(
                                ref reader, arena, diagnostics,
                                source,
                                "workflow env must be mapping",
                                ExpressionValidationContext.Env,
                                "workflow env");
                        }

                        continue;
                    case WorkflowRootMappingKey.Permissions:
                        if (!reader.End)
                        {
                            permissionsNode = ParsePermissionsNode(ref reader, arena, diagnostics, source, "workflow permissions must be scalar or mapping");
                        }

                        continue;
                    case WorkflowRootMappingKey.Defaults:
                        if (!reader.End)
                        {
                            defaultsNode = ParseDefaultsNode(ref reader, arena, diagnostics, "workflow defaults must be mapping");
                        }

                        continue;
                    case WorkflowRootMappingKey.Concurrency:
                        if (!reader.End)
                        {
                            concurrencyNode = ParseConcurrencyNode(ref reader, arena, diagnostics, "workflow concurrency must be scalar or mapping", ExpressionValidationContext.Concurrency);
                        }

                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            if (parseMode == ParseMode.ActionMetadata && keyUtf8.SequenceEqual("author"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (parseMode == ParseMode.ActionMetadata &&
                Utf8MappingDispatch.TryMatchFirstOrdered<ActionMetadataRootKeyTable>(keyUtf8, out var actionKeyOrdinal))
            {
                reader.Read();
                var ak = (ActionMetadataRootMappingKey)actionKeyOrdinal;
                if (!TrySetBit(ref actionSeen, actionKeyOrdinal))
                {
                    AddError(diagnostics, $"action metadata contains duplicate key: {ActionMetadataRootDuplicateKeyName(ak)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ak)
                {
                    case ActionMetadataRootMappingKey.Description:
                        actionDescription = ParseString(ref reader, arena, diagnostics, "action description must be scalar");
                        continue;
                    case ActionMetadataRootMappingKey.Inputs:
                        if (!reader.End)
                        {
                            actionInputs = ParseActionMetadataInputs(ref reader, arena, diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Outputs:
                        if (!reader.End)
                        {
                            actionOutputs = ParseActionMetadataOutputs(ref reader, arena, diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Runs:
                        if (!reader.End)
                        {
                            actionRuns = ParseActionMetadataRuns(ref reader, arena, diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Branding:
                        if (!reader.End)
                        {
                            actionBranding = ParseActionMetadataBranding(ref reader, arena, diagnostics);
                        }

                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(
                diagnostics,
                parseMode == ParseMode.ActionMetadata
                    ? $"unexpected key \"{unknownKey}\" for \"action metadata\" section. expected one of {Generated.ExpectedKeys.WorkflowKeys}"
                    : $"unexpected key \"{unknownKey}\" for \"workflow\" section. expected one of {Generated.ExpectedKeys.WorkflowKeys}",
                keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            workflowRange = BuildCompositeLocation(workflowStart, reader.CurrentEnd);
            reader.Read();
        }

        if (parseMode == ParseMode.Workflow && !hasOn)
        {
            AddError(diagnostics, "\"on\" section is missing in workflow", lastRootKeyMark);
        }

        if (parseMode == ParseMode.Workflow && !hasJobs)
        {
            AddError(diagnostics, "\"jobs\" section is missing in workflow", lastRootKeyMark);
        }

        if (parseMode == ParseMode.ActionMetadata)
        {
            if (!actionDescription.HasValue)
            {
                AddError(diagnostics, "required key 'description' is missing in action metadata", new TextPosition(0, 1, 1));
            }

            if (actionRuns == null)
            {
                AddError(diagnostics, "required key 'runs' is missing in action metadata", new TextPosition(0, 1, 1));
            }

            var actionMetadata = new ActionMetadata
            {
                Name = nameNode,
                Description = actionDescription,
                Inputs = actionInputs,
                Outputs = actionOutputs,
                Runs = actionRuns,
                Branding = actionBranding,
                Range = workflowRange,
            };
            return new ParseResult(null, actionMetadata, diagnostics.ToArray(), HasFatalError: false, arena);
        }

        var workflow = new Workflow
        {
            Name = nameNode,
            RunName = runNameNode,
            On = onEvents,
            Permissions = permissionsNode,
            Env = envNode,
            Defaults = defaultsNode,
            Concurrency = concurrencyNode,
            Jobs = jobs,
            Range = workflowRange,
        };

        return new ParseResult(workflow, null, diagnostics.ToArray(), HasFatalError: false, arena);
    }

    private static Permissions? ParsePermissionsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var all = ParseString(ref reader, arena, out var needsError, out var errorMark);
            if (needsError)
            {
                AddError(diagnostics, "permissions value must not be empty", errorMark);
            }

            return !all.HasValue
                ? null
                : new Permissions
                {
                    All = all,
                    Range = arena.GetStringRange(all),
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }


        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var scopes = new PooledBuffer<SliceMap<PermissionScope>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read(); // consume MappingStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, error, reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var keyMark = reader.CurrentStart;
                var keySlice = reader.GetScalarSlice();
                var keyUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    keyUtf8,
                    keySlice.Offset,
                    keySlice.Length,
                    keyMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "permissions"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var keyNode = arena.AddString(keySlice, reader.IsScalarQuoted(), BuildScalarLocation(keyMark, keyUtf8.Length));

                reader.Read(); // consume key
                if (reader.End)
                {
                    break;
                }

                var valueNode = ParseString(ref reader, arena, diagnostics, error);
                if (!valueNode.HasValue)
                {
                    continue;
                }

                // Use the slice stored in the arena (computed by ParseString's single GetScalarSlice call)
                // to avoid calling GetScalarSlice twice for the same scalar — which would advance the cursor
                // past the value and cause a position mismatch.
                var valueSlice = arena.GetStringSlice(valueNode);

                scopes.Add(new SliceMap<PermissionScope>.Entry(keySlice, new PermissionScope
                {
                    Name = keyNode,
                    NameText = keySlice,
                    Value = valueNode,
                    ValueText = valueSlice,
                }));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
                reader.Read();
            }

            return new Permissions
            {
                Scopes = new SliceMap<PermissionScope>(scopes.ToArray(), caseSensitive: true),
                Range = range,
            };
        }
        finally { scopes.Dispose(); }
    }

    private static Env? ParseEnvNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error, ExpressionValidationContext expressionContext, string? sectionName = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            // Check if the scalar contains an expression — plain text scalars are not valid for env
            var valueUtf8 = reader.GetScalarUtf8();
            if (!ExpressionScanHelpers.ContainsExpressionMarker(valueUtf8))
            {
                AddError(diagnostics, $"expecting a single ${{{{...}}}} expression or mapping value for \"env\" section, but found plain text node", reader.CurrentStart);
                reader.SkipCurrentNode();
                return null;
            }

            var expression = ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return !expression.HasValue
                ? null
                : new Env
                {
                    Expression = expression,
                    Range = arena.GetStringRange(expression),
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var vars = new PooledBuffer<SliceMap<EnvVar>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read(); // consume MappingStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, error, reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var keyMark = reader.CurrentStart;
                var keySlice = reader.GetScalarSlice();
                var keyUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    keyUtf8,
                    keySlice.Offset,
                    keySlice.Length,
                    keyMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    sectionName ?? error))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var keyNode = arena.AddString(keySlice, reader.IsScalarQuoted(), BuildScalarLocation(keyMark, keyUtf8.Length));

                reader.Read(); // consume key
                if (reader.End)
                {
                    break;
                }

                var valueNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
                if (!valueNode.HasValue)
                {
                    continue;
                }

                vars.Add(new SliceMap<EnvVar>.Entry(keySlice, new EnvVar
                {
                    Name = keyNode,
                    Value = valueNode,
                }));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
                reader.Read();
            }

            return new Env
            {
                Vars = new SliceMap<EnvVar>(vars.ToArray(), caseSensitive: true),
                Range = range,
            };
        }
        finally { vars.Dispose(); }
    }

    private static Defaults? ParseDefaultsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, string error, ExpressionValidationContext? expressionContext = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingMark = reader.CurrentStart;
        var range = BuildScalarLocation(mappingMark, 1);
        StringNodeId shellNode = default;
        StringNodeId workingDirectoryNode = default;
        ulong seen = 0;
        var hasRun = false;

        reader.Read(); // consume defaults mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "workflow defaults"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowDefaultsOuterKeyTable>(keyUtf8, out _))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "workflow defaults contains duplicate key: run", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasRun = true;
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, "workflow defaults.run must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var runStart = reader.CurrentStart;
                var runRange = BuildScalarLocation(runStart, 1);
                ulong runSeen = 0;
                reader.Read(); // consume run mapping
                while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, "workflow defaults.run must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                        if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                        {
                            reader.SkipCurrentNode();
                        }
                        continue;
                    }

                    var runKeyMark = reader.CurrentStart;
                    var runKeyUtf8 = reader.GetScalarUtf8();
                    if (IsMergeKey(runKeyUtf8, runKeyMark, diagnostics, "workflow defaults.run"))
                    {
                        reader.Read();
                        if (!reader.End) reader.SkipCurrentNode();
                        continue;
                    }

                    if (Utf8MappingDispatch.TryMatchFirstOrdered<DefaultsRunKeyTable>(runKeyUtf8, out var runKeyOrdinal))
                    {
                        reader.Read();
                        var drk = (DefaultsRunMappingKey)runKeyOrdinal;
                        if (!TrySetBit(ref runSeen, runKeyOrdinal))
                        {
                            var dupName = drk == DefaultsRunMappingKey.Shell ? "shell" : "working-directory";
                            AddError(diagnostics, $"workflow defaults.run contains duplicate key: {dupName}", runKeyMark);
                            if (!reader.End)
                            {
                                reader.SkipCurrentNode();
                            }

                            continue;
                        }

                        switch (drk)
                        {
                            case DefaultsRunMappingKey.Shell:
                                shellNode = expressionContext.HasValue
                                    ? ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext.Value, "workflow defaults.run.shell must be scalar", false)
                                    : ParseString(ref reader, arena, diagnostics, "workflow defaults.run.shell must be scalar");
                                continue;
                            case DefaultsRunMappingKey.WorkingDirectory:
                                workingDirectoryNode = expressionContext.HasValue
                                    ? ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext.Value, "workflow defaults.run.working-directory must be scalar", false)
                                    : ParseString(ref reader, arena, diagnostics, "workflow defaults.run.working-directory must be scalar");
                                continue;
                            default:
                                if (!reader.End)
                                {
                                    reader.SkipCurrentNode();
                                }

                                continue;
                        }
                    }

                    var unknownRunKey = Encoding.UTF8.GetString(runKeyUtf8);
                    reader.Read();
                    AddError(diagnostics, $"unexpected key \"{unknownRunKey}\" for \"run\" section. expected one of {Generated.ExpectedKeys.DefaultsRunKeys}", runKeyMark);
                    if (!reader.End) reader.SkipCurrentNode();
                }

                if (reader.CurrentKind == YamlEventKind.MappingEnd)
                {
                    runRange = BuildCompositeLocation(runStart, reader.CurrentEnd);
                    reader.Read();
                }

                range = BuildCompositeLocation(mappingMark, runRange);

                continue;
            }

            var unknownDefaultsKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"expected \"run\" key for \"defaults\" section but got \"{unknownDefaultsKey}\"", keyMark);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingMark, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.7 / §12: defaults.run is required in mapping form
        if (!hasRun)
        {
            AddError(diagnostics, "\"defaults\" section should have \"run\" section", mappingMark);
            return default;
        }

        return new Defaults
        {
            Run = new DefaultsRun
            {
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = shellNode.HasValue ? arena.GetStringRange(shellNode) : workingDirectoryNode.HasValue ? arena.GetStringRange(workingDirectoryNode) : range,
            },
            Range = range,
        };
    }

    private static Concurrency? ParseConcurrencyNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, string error, ExpressionValidationContext expressionContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var group = ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return !group.HasValue
                ? null
                : new Concurrency
                {
                    Group = group,
                    Range = arena.GetStringRange(group),
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        StringNodeId groupNode = default;
        BoolNodeId cancelInProgressNode = default;
        ulong seen = 0;
        var mappingMark = reader.CurrentStart;
        var range = BuildScalarLocation(mappingMark, 1);
        reader.Read(); // consume mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "concurrency"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ConcurrencyKeyTable>(keyUtf8, out var concurrencyKeyOrdinal))
            {
                reader.Read();
                var ck = (ConcurrencyMappingKey)concurrencyKeyOrdinal;
                if (!TrySetBit(ref seen, concurrencyKeyOrdinal))
                {
                    var dupName = ck == ConcurrencyMappingKey.Group ? "group" : "cancel-in-progress";
                    AddError(diagnostics, $"concurrency contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ck)
                {
                    case ConcurrencyMappingKey.Group:
                        groupNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, expressionContext, "workflow concurrency.group must be scalar", parseWholeValueIfNoEmbedded: false);
                        continue;
                    case ConcurrencyMappingKey.CancelInProgress:
                        cancelInProgressNode = ParseBoolOrExpression(ref reader, arena, diagnostics, expressionContext, "workflow concurrency.cancel-in-progress must be bool or expression");
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknownConcurrencyKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected key \"{unknownConcurrencyKey}\" for \"concurrency\" section. expected one of {Generated.ExpectedKeys.ConcurrencyKeys}", keyMark);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingMark, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.8 / §12: concurrency.group is required
        if (!groupNode.HasValue)
        {
            AddError(diagnostics, "group name is missing in \"concurrency\" section", mappingMark);
            return default;
        }

        return new Concurrency
        {
            Group = groupNode,
            CancelInProgress = cancelInProgressNode,
            Range = range,
        };
    }

    private static BoolNodeId ParseBoolOrExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseBoolOrExpression(ref reader, arena, diagnostics, context, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    private static BoolNodeId ParseBoolOrExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        var range = BuildScalarLocation(mark, valueUtf8.Length);

        if (TryParseBool(valueUtf8, tag, out var value))
        {
            var BoolNodeId = arena.AddBool(value, range);
            reader.Read();
            return BoolNodeId;
        }

        var expressionNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, context, out needsError, out errorMark, parseWholeValueIfNoEmbedded: false);
        if (!expressionNode.HasValue)
        {
            return default;
        }

        // If the string doesn't contain an expression, it's not a valid bool
        if (!ExpressionScanHelpers.ContainsExpressionMarker(expressionNode, arena))
        {
            needsError = true;
            errorMark = mark;
            return default;
        }

        return arena.AddBool(false, expressionNode, range);
    }

    private static SliceMap<Job> ParseJobsMapping<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var jobs = new PooledBuffer<SliceMap<Job>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            // current is MappingStart
            reader.Read();

            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "job id must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var jobIdMark = reader.CurrentStart;
                var jobId = reader.GetScalarSlice();
                var jobIdUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    jobIdUtf8,
                    jobId.Offset,
                    jobId.Length,
                    jobIdMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "jobs"))
                {
                    reader.Read(); // consume key
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var jobIdNode = arena.AddString(jobId, reader.IsScalarQuoted(), BuildScalarLocation(jobIdMark, jobIdUtf8.Length));
                reader.Read(); // consume job id

                if (reader.End)
                {
                    break;
                }

                var job = ParseJobNode(ref reader, arena, diagnostics, source, jobId, jobIdMark, jobIdNode);
                jobs.Add(new SliceMap<Job>.Entry(jobId, job));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<Job>(jobs.ToArray(), caseSensitive: false);
        }
        finally { jobs.Dispose(); }
    }

}
