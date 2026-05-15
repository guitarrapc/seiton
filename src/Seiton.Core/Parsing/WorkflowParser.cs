using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// Entry for a job that should be skipped during incremental parsing (D-5c).
/// The parser compares each job's positional index against this list and reuses the previous Job if matched.
/// </summary>
internal readonly struct JobSkipEntry(Utf8Slice key, Job job)
{
    /// <summary>The job ID key slice (offset+length into source).</summary>
    public readonly Utf8Slice Key = key;

    /// <summary>The previous Job AST node to reuse.</summary>
    public readonly Job Job = job;
}

/// <summary>
/// Hand-written pull-parser that converts UTF-8 YAML into the typed workflow/action metadata AST.
/// Partial class split by section: Jobs, Steps, Strategy, Events, Containers, etc.
/// </summary>
public static partial class WorkflowParser
{
    private delegate string? Utf8ScalarValidator(ReadOnlySpan<byte> valueUtf8);

    // S-1/S-2: Reusable buffers for anchor/alias diagnostics (avoids per-parse allocation).
    [ThreadStatic] private static (string Name, TextPosition Position)[]? threadstaticUnusedAnchorBuf;
    [ThreadStatic] private static (string Name, TextPosition Position, TextPosition AnchorPosition)[]? threadstaticRecursiveAliasBuf;

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

    /// <summary>Parses UTF-8 YAML into a result containing the workflow or action metadata AST.</summary>
    /// <remarks>
    /// The returned <see cref="ParseResult"/> is a regular <see cref="IDisposable"/> class.
    /// Use <c>using var result = WorkflowParser.Parse(...);</c> to ensure the underlying <see cref="AstArena"/>
    /// is returned to the shared pool when you are done reading the AST.
    /// </remarks>
    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        var classified = ParseClassified(utf8Yaml, filePath, out var arena);
        return new ParseResult(classified.ParseResult, arena);
    }

    /// <summary>
    /// Parses UTF-8 YAML and returns the result with the arena as an out parameter.
    /// Used by internal callers that need explicit arena ownership without the <see cref="ParseResult"/> wrapper.
    /// The caller is responsible for disposing the returned arena.
    /// </summary>
    internal static ParseResultData ParseDirect(byte[] utf8Yaml, string filePath, out AstArena? arena)
    {
        var classified = ParseClassified(utf8Yaml, filePath, out arena);
        return classified.ParseResult;
    }

    /// <summary>Parses UTF-8 YAML into a <see cref="ClassifiedParseResult"/> containing the AST and document kind classification.</summary>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML source.</param>
    /// <param name="filePath">The file path for diagnostic messages and document kind hinting.</param>
    /// <param name="arena">The arena that owns pooled buffers. Caller is responsible for disposal.</param>
    internal static ClassifiedParseResult ParseClassified(byte[] utf8Yaml, string filePath, out AstArena? arena)
    {
        var pathHintKind = DocumentKindClassifier.GetPathHintKind(filePath);
        AstArena? localArena = null;

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
            localArena = AstArena.Rent(utf8Yaml);
            var diagnostics = new PooledBuffer<Diagnostic>(16);
            try
            {
                var coreResult = ParseCore(ref parseReader, localArena, utf8Yaml, parseMode, ref diagnostics);

                // Check for unused anchors and recursive aliases after parsing while the adapter is still alive
                var unusedBuf = threadstaticUnusedAnchorBuf ??= new (string, TextPosition)[32];
                var unusedAnchors = parseReader.GetUnusedAnchors(unusedBuf);
                var recursiveBuf = threadstaticRecursiveAliasBuf ??= new (string, TextPosition, TextPosition)[32];
                var recursiveAliases = parseReader.GetRecursiveAliases(recursiveBuf);

                for (var i = 0; i < unusedAnchors.Length; i++)
                {
                    var (name, pos) = unusedAnchors[i];
                    AddWarning(ref diagnostics, $"anchor \"{name}\" is defined but not used", pos);
                }

                for (var i = 0; i < recursiveAliases.Length; i++)
                {
                    var (name, pos, anchorPos) = recursiveAliases[i];
                    var message = anchorPos.Line > 0
                        ? $"recursive alias \"{name}\" is found. anchor was declared at line:{anchorPos.Line}, column:{anchorPos.Column}"
                        : $"recursive alias \"{name}\" is found";
                    AddError(ref diagnostics, message, pos);
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

                // Transfer the pooled array directly to ParseResult (no .ToArray() copy)
                var (diagArray, diagCount) = diagnostics.DetachArray();
                var parseResult = new ParseResultData(
                    coreResult.Workflow,
                    coreResult.ActionMetadata,
                    new DiagnosticList(diagArray, diagCount),
                    coreResult.HasFatalError);

                // Register the pooled diagnostics array with the arena for lifecycle management
                localArena.RegisterDiagnosticsBuffer(diagArray);

                arena = localArena;
                localArena = null;
                return new ClassifiedParseResult(
                    parseResult,
                    new DocumentKindClassification(pathHintKind, finalKind, hasHintMismatch, isAmbiguous));
            }
            finally { diagnostics.Dispose(); }
        }
        catch (Exception ex)
        {
            localArena?.Dispose();
            arena = null;
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
            var parseResult = new ParseResultData(default, default, new DiagnosticList([diagnostic]), HasFatalError: true);
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

    internal static ParseResultData ParseWithReader<TReader>(ref TReader reader, AstArena arena, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var diagnostics = new PooledBuffer<Diagnostic>(16);
        try
        {
            var result = ParseCore(ref reader, arena, source, ParseMode.Workflow, ref diagnostics);
            var (diagArray, diagCount) = diagnostics.DetachArray();
            arena.RegisterDiagnosticsBuffer(diagArray);
            return new ParseResultData(result.Workflow, result.ActionMetadata, new DiagnosticList(diagArray, diagCount), result.HasFatalError);
        }
        finally
        {
            diagnostics.Dispose();
        }
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

    private static ParseCoreResult ParseCore<TReader>(ref TReader reader, AstArena arena, ReadOnlySpan<byte> source, ParseMode parseMode, ref PooledBuffer<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        return ParseCoreInner(ref reader, arena, source, parseMode, ref diagnostics, rootSkipMask: 0);
    }

    /// <summary>
    /// Parses a workflow with a root section skip mask for incremental parsing (D-5b).
    /// Sections whose bit is set in <paramref name="rootSkipMask"/> are skipped via SkipCurrentNode().
    /// The caller must patch in previous AST nodes for skipped sections.
    /// </summary>
    internal static ParseResultData ParseIncremental(byte[] utf8Yaml, string filePath, AstArena arena, byte rootSkipMask, JobSkipEntry[]? jobSkipEntries = null)
    {
        var reader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
        var diagnostics = new PooledBuffer<Diagnostic>(16);
        try
        {
            var result = ParseCoreInner(ref reader, arena, (ReadOnlySpan<byte>)utf8Yaml, ParseMode.Workflow, ref diagnostics, rootSkipMask, jobSkipEntries);

            // Check for unused anchors and recursive aliases (same as ParseClassified)
            var unusedBuf = threadstaticUnusedAnchorBuf ??= new (string, TextPosition)[32];
            var unusedAnchors = reader.GetUnusedAnchors(unusedBuf);
            var recursiveBuf = threadstaticRecursiveAliasBuf ??= new (string, TextPosition, TextPosition)[32];
            var recursiveAliases = reader.GetRecursiveAliases(recursiveBuf);

            for (var i = 0; i < unusedAnchors.Length; i++)
            {
                var (name, pos) = unusedAnchors[i];
                AddWarning(ref diagnostics, $"anchor \"{name}\" is defined but not used", pos);
            }

            for (var i = 0; i < recursiveAliases.Length; i++)
            {
                var (name, pos, anchorPos) = recursiveAliases[i];
                var message = anchorPos.Line > 0
                    ? $"recursive alias \"{name}\" is found. anchor was declared at line:{anchorPos.Line}, column:{anchorPos.Column}"
                    : $"recursive alias \"{name}\" is found";
                AddError(ref diagnostics, message, pos);
            }

            // Stamp file path on all diagnostics
            if (!string.IsNullOrEmpty(filePath) && diagnostics.Count > 0)
            {
                var span = diagnostics.AsSpan();
                for (var i = 0; i < span.Length; i++)
                {
                    diagnostics.Replace(i, span[i] with { FilePath = filePath });
                }
            }

            // Transfer pooled array directly (no .ToArray() copy)
            var (diagArray, diagCount) = diagnostics.DetachArray();
            arena.RegisterDiagnosticsBuffer(diagArray);

            return new ParseResultData(result.Workflow, result.ActionMetadata, new DiagnosticList(diagArray, diagCount), result.HasFatalError);
        }
        finally
        {
            diagnostics.Dispose();
        }
    }

    /// <summary>Lightweight result from ParseCoreInner — diagnostics stay in the caller's PooledBuffer.</summary>
    private readonly struct ParseCoreResult(Workflow? workflow, ActionMetadata? actionMetadata, bool hasFatalError, AstArena? arena)
    {
        public readonly Workflow? Workflow = workflow;
        public readonly ActionMetadata? ActionMetadata = actionMetadata;
        public readonly bool HasFatalError = hasFatalError;
        public readonly AstArena? Arena = arena;
    }

    private static ParseCoreResult ParseCoreInner<TReader>(ref TReader reader, AstArena arena, ReadOnlySpan<byte> source, ParseMode parseMode, ref PooledBuffer<Diagnostic> diagnostics, byte rootSkipMask = 0, JobSkipEntry[]? jobSkipEntries = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        reader.SkipHeader();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "workflow root must be object", reader.CurrentStart);
            return new ParseCoreResult(default, default, hasFatalError: true, arena);
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
                AddError(ref diagnostics, "workflow key must be string", reader.CurrentStart);
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
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "workflow"))
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
                    AddError(ref diagnostics, $"workflow contains duplicate key: {WorkflowRootDuplicateKeyName(wk)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                // D-5b: Skip unchanged root sections (incremental parse).
                // The caller will patch in previous AST nodes for skipped sections.
                if (rootSkipMask != 0 && (rootSkipMask & (1 << workflowKeyOrdinal)) != 0)
                {
                    if (wk == WorkflowRootMappingKey.On) hasOn = true;
                    else if (wk == WorkflowRootMappingKey.Jobs) hasJobs = true;
                    if (!reader.End) reader.SkipCurrentNode();
                    continue;
                }

                switch (wk)
                {
                    case WorkflowRootMappingKey.Name:
                        nameNode = ParseString(ref reader, arena, ref diagnostics, "name must be string");
                        continue;
                    case WorkflowRootMappingKey.RunName:
                        runNameNode = ParseStringAndValidateExpression(
                            ref reader, arena, ref diagnostics,
                            ExpressionValidationContext.RunName,
                            "run-name must be string",
                            parseWholeValueIfNoEmbedded: false);
                        continue;
                    case WorkflowRootMappingKey.On:
                        hasOn = true;
                        if (!reader.End)
                        {
                            if (reader.CurrentKind is not YamlEventKind.Scalar and not YamlEventKind.MappingStart and not YamlEventKind.SequenceStart)
                            {
                                AddError(ref diagnostics, "on must be string, object, or array", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                onEvents = ParseOnEvents(ref reader, arena, ref diagnostics, source);
                            }
                        }

                        continue;
                    case WorkflowRootMappingKey.Jobs:
                        hasJobs = true;
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.MappingStart)
                            {
                                AddError(ref diagnostics, "jobs must be object", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else if (jobSkipEntries is { Length: > 0 })
                            {
                                jobs = ParseJobsMappingIncremental(ref reader, arena, ref diagnostics, source, jobSkipEntries);
                            }
                            else
                            {
                                jobs = ParseJobsMapping(ref reader, arena, ref diagnostics, source);
                            }
                        }

                        continue;
                    case WorkflowRootMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(
                                ref reader, arena, ref diagnostics,
                                source,
                                "workflow env must be object",
                                ExpressionValidationContext.Env,
                                "workflow env");
                        }

                        continue;
                    case WorkflowRootMappingKey.Permissions:
                        if (!reader.End)
                        {
                            permissionsNode = ParsePermissionsNode(ref reader, arena, ref diagnostics, source, "workflow permissions must be string or object");
                        }

                        continue;
                    case WorkflowRootMappingKey.Defaults:
                        if (!reader.End)
                        {
                            defaultsNode = ParseDefaultsNode(ref reader, arena, ref diagnostics, "workflow defaults must be object");
                        }

                        continue;
                    case WorkflowRootMappingKey.Concurrency:
                        if (!reader.End)
                        {
                            concurrencyNode = ParseConcurrencyNode(ref reader, arena, ref diagnostics, "workflow concurrency must be string or object", ExpressionValidationContext.Concurrency, keyMark);
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
                    AddError(ref diagnostics, $"action metadata contains duplicate key: {ActionMetadataRootDuplicateKeyName(ak)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ak)
                {
                    case ActionMetadataRootMappingKey.Description:
                        actionDescription = ParseString(ref reader, arena, ref diagnostics, "action description must be string");
                        continue;
                    case ActionMetadataRootMappingKey.Inputs:
                        if (!reader.End)
                        {
                            actionInputs = ParseActionMetadataInputs(ref reader, arena, ref diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Outputs:
                        if (!reader.End)
                        {
                            actionOutputs = ParseActionMetadataOutputs(ref reader, arena, ref diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Runs:
                        if (!reader.End)
                        {
                            actionRuns = ParseActionMetadataRuns(ref reader, arena, ref diagnostics, source);
                        }

                        continue;
                    case ActionMetadataRootMappingKey.Branding:
                        if (!reader.End)
                        {
                            actionBranding = ParseActionMetadataBranding(ref reader, arena, ref diagnostics);
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

            var keySlice = reader.GetScalarSlice();
            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var expectedKeys = parseMode == ParseMode.ActionMetadata
                ? Generated.ExpectedKeys.ActionMetadataKeys
                : Generated.ExpectedKeys.WorkflowKeys;
            var suggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownKey, expectedKeys);
            var prefix = parseMode == ParseMode.ActionMetadata
                ? "unexpected key \"{0}\" at action metadata top level."
                : "unexpected key \"{0}\" at workflow top level.";
            var message = suggestion is not null
                ? $"{string.Format(prefix, unknownKey)} did you mean \"{suggestion}\"? expected one of {expectedKeys}"
                : $"{string.Format(prefix, unknownKey)} expected one of {expectedKeys}";
            var fix = suggestion is not null
                ? new DiagnosticFix($"replace '{unknownKey}' with '{suggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, suggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, message, keyMark, fix);
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
            AddError(ref diagnostics, "\"on\" section is missing in workflow", lastRootKeyMark);
        }

        if (parseMode == ParseMode.Workflow && !hasJobs)
        {
            AddError(ref diagnostics, "\"jobs\" section is missing in workflow", lastRootKeyMark);
        }

        if (parseMode == ParseMode.ActionMetadata)
        {
            if (!actionDescription.HasValue)
            {
                AddError(ref diagnostics, "required key 'description' is missing in action metadata", new TextPosition(0, 1, 1));
            }

            if (actionRuns == null)
            {
                AddError(ref diagnostics, "required key 'runs' is missing in action metadata", new TextPosition(0, 1, 1));
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
            return new ParseCoreResult(null, actionMetadata, hasFatalError: false, arena);
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

        return new ParseCoreResult(workflow, null, hasFatalError: false, arena);
    }

    private static Permissions? ParsePermissionsNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var all = ParseString(ref reader, arena, out var needsError, out var errorMark);
            if (needsError)
            {
                AddError(ref diagnostics, "permissions value must not be empty", errorMark);
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
            AddError(ref diagnostics, error, reader.CurrentStart);
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
                    AddError(ref diagnostics, error, reader.CurrentStart);
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
                    ref diagnostics,
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

                var valueNode = ParseString(ref reader, arena, ref diagnostics, error);
                if (!valueNode.HasValue)
                {
                    continue;
                }

                // Use the slice stored in the arena (computed by ParseString's single GetScalarSlice call)
                // to avoid calling GetScalarSlice twice for the same scalar 窶・which would advance the cursor
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

            var (scopeEntries, scopeCount) = scopes.DetachArray();
            arena.RegisterSliceMapBuffer(scopeEntries);
            return new Permissions
            {
                Scopes = new SliceMap<PermissionScope>(scopeEntries, scopeCount, caseSensitive: true),
                Range = range,
            };
        }
        finally { scopes.Dispose(); }
    }

    private static Env? ParseEnvNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error, ExpressionValidationContext expressionContext, string? sectionName = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            // Check if the scalar contains an expression 窶・plain text scalars are not valid for env
            var valueUtf8 = reader.GetScalarUtf8();
            if (!ExpressionScanHelpers.ContainsExpressionMarker(valueUtf8))
            {
                AddError(ref diagnostics, $"expecting a single ${{{{...}}}} expression or mapping value for \"env\" section, but found plain text node", reader.CurrentStart);
                reader.SkipCurrentNode();
                return null;
            }

            var expression = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
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
            AddError(ref diagnostics, error, reader.CurrentStart);
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
                    AddError(ref diagnostics, error, reader.CurrentStart);
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
                    ref diagnostics,
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

                var valueNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
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

            var (varEntries, varCount) = vars.DetachArray();
            arena.RegisterSliceMapBuffer(varEntries);
            return new Env
            {
                Vars = new SliceMap<EnvVar>(varEntries, varCount, caseSensitive: true),
                Range = range,
            };
        }
        finally { vars.Dispose(); }
    }

    private static Defaults? ParseDefaultsNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string error, ExpressionValidationContext? expressionContext = null, string sectionContext = "")
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            var mark = reader.CurrentStart;
            if (reader.CurrentKind == YamlEventKind.Scalar && reader.GetScalarTag() == ScalarTag.Null)
            {
                AddError(ref diagnostics, "\"defaults\" section should have \"run\" section", mark);
                AddError(ref diagnostics, "\"defaults\" section should not be empty. please remove this section if it's unnecessary", mark);
            }
            else
            {
                AddError(ref diagnostics, error, mark);
            }

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
                AddError(ref diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "workflow defaults"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowDefaultsOuterKeyTable>(keyUtf8, out _))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(ref diagnostics, "workflow defaults contains duplicate key: run", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasRun = true;
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(ref diagnostics, "workflow defaults.run must be object", reader.CurrentStart);
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
                        AddError(ref diagnostics, "workflow defaults.run must be object", reader.CurrentStart);
                        reader.SkipCurrentNode();
                        if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                        {
                            reader.SkipCurrentNode();
                        }
                        continue;
                    }

                    var runKeyMark = reader.CurrentStart;
                    var runKeyUtf8 = reader.GetScalarUtf8();
                    if (IsMergeKey(runKeyUtf8, runKeyMark, ref diagnostics, "workflow defaults.run"))
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
                            AddError(ref diagnostics, $"workflow defaults.run contains duplicate key: {dupName}", runKeyMark);
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
                                    ? ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext.Value, "workflow defaults.run.shell must be string", false)
                                    : ParseString(ref reader, arena, ref diagnostics, "workflow defaults.run.shell must be string");
                                continue;
                            case DefaultsRunMappingKey.WorkingDirectory:
                                workingDirectoryNode = expressionContext.HasValue
                                    ? ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext.Value, "workflow defaults.run.working-directory must be string", false)
                                    : ParseString(ref reader, arena, ref diagnostics, "workflow defaults.run.working-directory must be string");
                                continue;
                            default:
                                if (!reader.End)
                                {
                                    reader.SkipCurrentNode();
                                }

                                continue;
                        }
                    }

                    var runKeySlice = reader.GetScalarSlice();
                    var unknownRunKey = Encoding.UTF8.GetString(runKeyUtf8);
                    reader.Read();
                    var runPrefix = sectionContext.Length > 0 ? $"{sectionContext}.defaults.run " : "defaults.run ";
                    var runSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownRunKey, Generated.ExpectedKeys.DefaultsRunKeys);
                    var runMessage = runSuggestion is not null
                        ? $"{runPrefix}has unexpected key \"{unknownRunKey}\" for \"run\" section. did you mean \"{runSuggestion}\"? expected one of {Generated.ExpectedKeys.DefaultsRunKeys}"
                        : $"{runPrefix}has unexpected key \"{unknownRunKey}\" for \"run\" section. expected one of {Generated.ExpectedKeys.DefaultsRunKeys}";
                    var runFix = runSuggestion is not null
                        ? new DiagnosticFix($"replace '{unknownRunKey}' with '{runSuggestion}'", [new TextEdit(runKeySlice.Offset, runKeySlice.Length, runSuggestion)])
                        : (DiagnosticFix?)null;
                    AddError(ref diagnostics, runMessage, runKeyMark, runFix);
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

            var keySlice = reader.GetScalarSlice();
            var unknownDefaultsKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var defaultsPrefix = sectionContext.Length > 0 ? $"{sectionContext}.defaults " : "defaults ";
            var defaultsSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownDefaultsKey, Generated.ExpectedKeys.DefaultsKeys);
            var defaultsMessage = defaultsSuggestion is not null
                ? $"{defaultsPrefix}has unexpected key \"{unknownDefaultsKey}\" for \"defaults\" section. did you mean \"{defaultsSuggestion}\"? expected \"run\""
                : $"{defaultsPrefix}has unexpected key \"{unknownDefaultsKey}\" for \"defaults\" section. expected \"run\"";
            var defaultsFix = defaultsSuggestion is not null
                ? new DiagnosticFix($"replace '{unknownDefaultsKey}' with '{defaultsSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, defaultsSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, defaultsMessage, keyMark, defaultsFix);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingMark, reader.CurrentEnd);
            reader.Read();
        }

        // spec ﾂｧ3.7 / ﾂｧ12: defaults.run is required in mapping form
        if (!hasRun)
        {
            AddError(ref diagnostics, "\"defaults\" section should have \"run\" section", mappingMark);
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

    private static Concurrency? ParseConcurrencyNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string error, ExpressionValidationContext expressionContext, TextPosition keyMark, string sectionContext = "")
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var group = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
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
            AddError(ref diagnostics, error, reader.CurrentStart);
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
                AddError(ref diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var innerKeyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, innerKeyMark, ref diagnostics, "concurrency"))
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
                    AddError(ref diagnostics, $"concurrency contains duplicate key: {dupName}", innerKeyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ck)
                {
                    case ConcurrencyMappingKey.Group:
                        groupNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, expressionContext, "workflow concurrency.group must be string", parseWholeValueIfNoEmbedded: false);
                        continue;
                    case ConcurrencyMappingKey.CancelInProgress:
                        cancelInProgressNode = ParseBoolOrExpression(ref reader, arena, ref diagnostics, expressionContext, "workflow concurrency.cancel-in-progress must be bool or expression");
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var innerKeySlice = reader.GetScalarSlice();
            var unknownConcurrencyKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var concurrencyPrefix = sectionContext.Length > 0 ? $"{sectionContext}.concurrency " : "concurrency ";
            var concurrencySuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownConcurrencyKey, Generated.ExpectedKeys.ConcurrencyKeys);
            var concurrencyMessage = concurrencySuggestion is not null
                ? $"{concurrencyPrefix}has unexpected key \"{unknownConcurrencyKey}\" for \"concurrency\" section. did you mean \"{concurrencySuggestion}\"? expected one of {Generated.ExpectedKeys.ConcurrencyKeys}"
                : $"{concurrencyPrefix}has unexpected key \"{unknownConcurrencyKey}\" for \"concurrency\" section. expected one of {Generated.ExpectedKeys.ConcurrencyKeys}";
            var concurrencyFix = concurrencySuggestion is not null
                ? new DiagnosticFix($"replace '{unknownConcurrencyKey}' with '{concurrencySuggestion}'", [new TextEdit(innerKeySlice.Offset, innerKeySlice.Length, concurrencySuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, concurrencyMessage, innerKeyMark, concurrencyFix);
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
            AddError(ref diagnostics, "\"concurrency\" section is missing group name", keyMark);
            return default;
        }

        return new Concurrency
        {
            Group = groupNode,
            CancelInProgress = cancelInProgressNode,
            Range = range,
        };
    }

    private static BoolNodeId ParseBoolOrExpression<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseBoolOrExpression(ref reader, arena, ref diagnostics, context, out var needsError, out var errorMark);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return node;
    }

    private static BoolNodeId ParseBoolOrExpression<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark)
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

        var expressionNode = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, context, out needsError, out errorMark, parseWholeValueIfNoEmbedded: false);
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

    private static SliceMap<Job> ParseJobsMapping<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
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
                    AddError(ref diagnostics, "job id must be string", reader.CurrentStart);
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
                    ref diagnostics,
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

                var job = ParseJobNode(ref reader, arena, ref diagnostics, source, jobId, jobIdMark, jobIdNode);
                jobs.Add(new SliceMap<Job>.Entry(jobId, job));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            var (jobEntries, jobCount) = jobs.DetachArray();
            arena.RegisterSliceMapBuffer(jobEntries);
            return new SliceMap<Job>(jobEntries, jobCount, caseSensitive: false);
        }
        finally { jobs.Dispose(); }
    }

    /// <summary>
    /// Incremental variant of <see cref="ParseJobsMapping{TReader}"/> (D-5c).
    /// For each job, checks whether it matches a skip entry (by positional index and key bytes).
    /// If matched, the job subtree is skipped via <c>SkipCurrentNode()</c> and the previous Job is reused.
    /// </summary>
    private static SliceMap<Job> ParseJobsMappingIncremental<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, JobSkipEntry[] skipEntries)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var jobs = new PooledBuffer<SliceMap<Job>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            var jobIndex = 0;
            // current is MappingStart
            reader.Read();

            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, "job id must be string", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    jobIndex++;
                    continue;
                }

                var jobIdMark = reader.CurrentStart;
                var jobId = reader.GetScalarSlice();
                var jobIdUtf8 = reader.GetScalarUtf8();

                // D-5c: Check if this job can be skipped (same position, same key bytes)
                if ((uint)jobIndex < (uint)skipEntries.Length)
                {
                    var skipEntry = skipEntries[jobIndex];
                    if (skipEntry.Job is not null &&
                        skipEntry.Key.Length == jobId.Length &&
                        source[skipEntry.Key.Offset..(skipEntry.Key.Offset + skipEntry.Key.Length)]
                            .SequenceEqual(source[jobId.Offset..(jobId.Offset + jobId.Length)]))
                    {
                        // Job matches — skip its subtree and reuse previous Job
                        // Register key for duplicate detection; if duplicate, skip without adding
                        if (!TryRegisterDynamicKey(
                            source,
                            jobIdUtf8,
                            jobId.Offset,
                            jobId.Length,
                            jobIdMark,
                            ref diagnostics,
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
                            jobIndex++;
                            continue;
                        }

                        reader.Read(); // consume job id key
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode(); // skip job body
                        }
                        jobs.Add(new SliceMap<Job>.Entry(jobId, skipEntry.Job));
                        jobIndex++;
                        continue;
                    }
                }

                if (!TryRegisterDynamicKey(
                    source,
                    jobIdUtf8,
                    jobId.Offset,
                    jobId.Length,
                    jobIdMark,
                    ref diagnostics,
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
                    jobIndex++;
                    continue;
                }

                var jobIdNode = arena.AddString(jobId, reader.IsScalarQuoted(), BuildScalarLocation(jobIdMark, jobIdUtf8.Length));
                reader.Read(); // consume job id

                if (reader.End)
                {
                    break;
                }

                var job = ParseJobNode(ref reader, arena, ref diagnostics, source, jobId, jobIdMark, jobIdNode);
                jobs.Add(new SliceMap<Job>.Entry(jobId, job));
                jobIndex++;
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            var (jobEntries, jobCount) = jobs.DetachArray();
            arena.RegisterSliceMapBuffer(jobEntries);
            return new SliceMap<Job>(jobEntries, jobCount, caseSensitive: false);
        }
        finally { jobs.Dispose(); }
    }

}
