using System.Text;
using System.Buffers.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private delegate string? Utf8ScalarValidator(ReadOnlySpan<byte> valueUtf8);

    private enum ParseMode
    {
        Workflow,
        ActionMetadata,
    }

    private enum MappingKeyComparison
    {
        CaseSensitive,
        AsciiCaseInsensitive,
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

    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        return ParseClassified(utf8Yaml, filePath).ParseResult;
    }

    public static ClassifiedParseResult ParseClassified(byte[] utf8Yaml, string filePath)
    {
        var pathHintKind = DocumentKindClassifier.GetPathHintKind(filePath);

        try
        {
            var hintReader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
            var hasHints = TryReadRootStructuralHints(ref hintReader, out var hasJobs, out var hasRuns);
            var finalKind = hasHints
                ? DocumentKindClassifier.FinalizeKind(pathHintKind, hasJobs, hasRuns, out var ignoredAmbiguous, out var ignoredHintMismatch)
                : DocumentKind.Unknown;

            var isAmbiguous = hasHints && hasJobs && hasRuns;
            var hasHintMismatch =
                hasHints &&
                pathHintKind == DocumentKind.ActionMetadata &&
                finalKind == DocumentKind.Workflow;

            var parseReader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
            var parseMode = finalKind == DocumentKind.ActionMetadata ? ParseMode.ActionMetadata : ParseMode.Workflow;
            var parseResult = ParseCore(ref parseReader, utf8Yaml, parseMode);

            var diagnostics = new List<Diagnostic>(parseResult.Diagnostics.Length + 2);
            diagnostics.AddRange(parseResult.Diagnostics);

            if (isAmbiguous)
            {
                AddError(diagnostics, "document kind is ambiguous: root has both 'jobs' and 'runs'", new TextPosition(0, 1, 1));
            }

            if (hasHintMismatch)
            {
                AddError(diagnostics, "path hint suggests action-metadata but root structure indicates workflow", new TextPosition(0, 1, 1));
            }

            if (!string.IsNullOrEmpty(filePath) && diagnostics.Count > 0)
            {
                for (var i = 0; i < diagnostics.Count; i++)
                {
                    diagnostics[i] = diagnostics[i] with { FilePath = filePath };
                }
            }

            parseResult = parseResult with { Diagnostics = diagnostics.ToArray() };

            return new ClassifiedParseResult(
                parseResult,
                new DocumentKindClassification(pathHintKind, finalKind, hasHintMismatch, isAmbiguous));
        }
        catch (Exception ex)
        {
            var location = new TextRange(
                Start: 0,
                Length: 0,
                StartLine: 1,
                StartColumn: 1,
                EndLine: 1,
                EndColumn: 1);
            var diagnostic = new Diagnostic(
                Severity: DiagnosticSeverity.Error,
                Message: $"yaml parse failure: {ex.Message}",
                Location: location,
                FilePath: string.IsNullOrEmpty(filePath) ? null : filePath);
            var parseResult = new ParseResult(default, [diagnostic], HasFatalError: true);
            return new ClassifiedParseResult(
                parseResult,
                new DocumentKindClassification(pathHintKind, DocumentKind.Unknown, HasHintMismatch: false, IsAmbiguous: false));
        }
    }

    internal static ParseResult ParseWithReader<TReader>(ref TReader reader, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        return ParseCore(ref reader, source, ParseMode.Workflow);
    }

    static bool TryReadRootStructuralHints<TReader>(ref TReader reader, out bool hasJobs, out bool hasRuns)
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
            if (keyUtf8.SequenceEqual("jobs"u8))
            {
                hasJobs = true;
            }
            else if (keyUtf8.SequenceEqual("runs"u8))
            {
                hasRuns = true;
            }

            reader.Read();
            if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                reader.SkipCurrentNode();
            }
        }

        return true;
    }

    private static ParseResult ParseCore<TReader>(ref TReader reader, ReadOnlySpan<byte> source, ParseMode parseMode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var diagnostics = new List<Diagnostic>(16);

        reader.SkipHeader();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentStart);
            return new ParseResult(default, diagnostics.ToArray(), HasFatalError: true);
        }

        var workflowStart = reader.CurrentStart;
        var workflowRange = BuildScalarLocation(workflowStart, 1);

        reader.Read(); // skip MappingStart

        StringNode? nameNode = null;
        StringNode? runNameNode = null;
        Permissions? permissionsNode = null;
        Env? envNode = null;
        Defaults? defaultsNode = null;
        Concurrency? concurrencyNode = null;
        var hasOn = false;
        var hasJobs = false;
        Event[] onEvents = [];
        Dictionary<Utf8String, Job> jobs = [];
        var workflowKeys = new HashSet<Utf8String>();

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
            var keyUtf8 = reader.GetScalarUtf8();
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                workflowKeys,
                MappingKeyComparison.CaseSensitive,
                "workflow"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read(); // consume key
                nameNode = ParseString(ref reader, diagnostics, "name must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("run-name"u8))
            {
                reader.Read(); // consume key
                runNameNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Workflow,
                    "run-name must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (keyUtf8.SequenceEqual("on"u8))
            {
                reader.Read(); // consume key
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
                        onEvents = ParseOnEvents(ref reader, diagnostics);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("jobs"u8))
            {
                reader.Read(); // consume key
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
                        jobs = ParseJobsMapping(ref reader, diagnostics, source);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    envNode = ParseEnvNode(
                        ref reader,
                        diagnostics,
                        source,
                        "workflow env must be mapping",
                        ExpressionValidationContext.Workflow);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("permissions"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    permissionsNode = ParsePermissionsNode(ref reader, diagnostics, source, "workflow permissions must be scalar or mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("defaults"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    defaultsNode = ParseDefaultsNode(ref reader, diagnostics, "workflow defaults must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("concurrency"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    concurrencyNode = ParseConcurrencyNode(ref reader, diagnostics, "workflow concurrency must be scalar or mapping", ExpressionValidationContext.Workflow);
                }
                continue;
            }

            if (parseMode == ParseMode.ActionMetadata && IsActionMetadataRootKey(keyUtf8))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyText = Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key

            AddError(diagnostics, $"unexpected workflow key: {keyText}", keyMark);
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
            AddError(diagnostics, "required key 'on' is missing", new TextPosition(0, 1, 1));
        }

        if (parseMode == ParseMode.Workflow && !hasJobs)
        {
            AddError(diagnostics, "required key 'jobs' is missing", new TextPosition(0, 1, 1));
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

        return new ParseResult(workflow, diagnostics.ToArray(), HasFatalError: false);
    }

    static bool IsActionMetadataRootKey(ReadOnlySpan<byte> keyUtf8)
    {
        return keyUtf8.SequenceEqual("runs"u8)
            || keyUtf8.SequenceEqual("description"u8)
            || keyUtf8.SequenceEqual("inputs"u8)
            || keyUtf8.SequenceEqual("outputs"u8)
            || keyUtf8.SequenceEqual("branding"u8);
    }

    private static Permissions? ParsePermissionsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var all = ParseString(ref reader, diagnostics, error);
            return all is null
                ? null
                : new Permissions
                {
                    All = all,
                    Range = all.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }


        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var scopes = new Dictionary<Utf8String, PermissionScope>();
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "permissions"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyText = new Utf8String(keyUtf8);
            var keyNode = new StringNode
            {
                Value = keySlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };

            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            var valueText = reader.CurrentKind == YamlEventKind.Scalar
                ? new Utf8String(reader.GetScalarUtf8())
                : default;
            var valueNode = ParseString(ref reader, diagnostics, error);
            if (valueNode is null)
            {
                continue;
            }

            scopes[keyText] = new PermissionScope
            {
                Name = keyNode,
                NameText = keyText,
                Value = valueNode,
                ValueText = valueText,
            };
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        return new Permissions
        {
            Scopes = scopes,
            Range = range,
        };
    }

    private static Env? ParseEnvNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error, ExpressionValidationContext expressionContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return expression is null
                ? null
                : new Env
                {
                    Expression = expression,
                    Range = expression.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var vars = new Dictionary<Utf8String, EnvVar>();
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                error))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyNode = new StringNode
            {
                Value = keySlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };

            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            var valueNode = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            if (valueNode is null)
            {
                continue;
            }

            vars[keySlice.ToUtf8String(source)] = new EnvVar
            {
                Name = keyNode,
                Value = valueNode,
            };
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        return new Env
        {
            Vars = vars,
            Range = range,
        };
    }

    private static Defaults? ParseDefaultsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string error)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingMark = reader.CurrentStart;
        var range = BuildScalarLocation(mappingMark, 1);
        StringNode? shellNode = null;
        StringNode? workingDirectoryNode = null;
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "workflow defaults"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isRun = keyUtf8.SequenceEqual("run"u8);
            reader.Read(); // consume key
            if (reader.End)
            {
                break;
            }

            if (isRun)
            {
                hasRun = true;
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, "workflow defaults.run must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var runStart = reader.CurrentStart;
                var runRange = BuildScalarLocation(runStart, 1);
                var runKeys = new HashSet<Utf8String>();
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
                    if (!TryRegisterMappingKey(
                        runKeyUtf8,
                        runKeyMark,
                        diagnostics,
                        runKeys,
                        MappingKeyComparison.AsciiCaseInsensitive,
                        "workflow defaults.run"))
                    {
                        reader.Read();
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                    }

                    var isShell = runKeyUtf8.SequenceEqual("shell"u8);
                    var isWorkingDirectory = runKeyUtf8.SequenceEqual("working-directory"u8);
                    reader.Read();
                    if (reader.End)
                    {
                        break;
                    }

                    if (isShell)
                    {
                        shellNode = ParseString(ref reader, diagnostics, "workflow defaults.run.shell must be scalar");
                        continue;
                    }

                    if (isWorkingDirectory)
                    {
                        workingDirectoryNode = ParseString(ref reader, diagnostics, "workflow defaults.run.working-directory must be scalar");
                        continue;
                    }

                    AddError(diagnostics, $"unexpected workflow defaults.run key: {Encoding.UTF8.GetString(runKeyUtf8)}", runKeyMark);
                    reader.SkipCurrentNode();
                }

                if (reader.CurrentKind == YamlEventKind.MappingEnd)
                {
                    runRange = BuildCompositeLocation(runStart, reader.CurrentEnd);
                    reader.Read();
                }

                range = BuildCompositeLocation(mappingMark, runRange);

                continue;
            }

            AddError(diagnostics, $"unexpected workflow defaults key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingMark, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.7 / §12: defaults.run is required in mapping form
        if (!hasRun)
        {
            AddError(diagnostics, "defaults should have run", mappingMark);
            return null;
        }

        return new Defaults
        {
            Run = new DefaultsRun
            {
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = shellNode?.Range ?? workingDirectoryNode?.Range ?? range,
            },
            Range = range,
        };
    }

    private static Concurrency? ParseConcurrencyNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string error, ExpressionValidationContext expressionContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var group = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, error, parseWholeValueIfNoEmbedded: false);
            return group is null
                ? null
                : new Concurrency
                {
                    Group = group,
                    Range = group.Range,
                };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        StringNode? groupNode = null;
        BoolNode? cancelInProgressNode = null;
        var keys = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                keyUtf8,
                keyMark,
                diagnostics,
                keys,
                MappingKeyComparison.AsciiCaseInsensitive,
                "concurrency"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var isGroup = keyUtf8.SequenceEqual("group"u8);
            var isCancelInProgress = keyUtf8.SequenceEqual("cancel-in-progress"u8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isGroup)
            {
                groupNode = ParseStringAndValidateExpression(ref reader, diagnostics, expressionContext, "workflow concurrency.group must be scalar", parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (isCancelInProgress)
            {
                cancelInProgressNode = ParseBoolOrExpression(ref reader, diagnostics, expressionContext, "workflow concurrency.cancel-in-progress must be bool or expression");
                continue;
            }

            AddError(diagnostics, $"unexpected workflow concurrency key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingMark, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.8 / §12: concurrency.group is required
        if (groupNode is null)
        {
            AddError(diagnostics, "concurrency.group is required", mappingMark);
            return null;
        }

        return new Concurrency
        {
            Group = groupNode,
            CancelInProgress = cancelInProgressNode,
            Range = range,
        };
    }

    private static BoolNode? ParseBoolOrExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        var range = BuildScalarLocation(mark, valueUtf8.Length);

        if (TryParseBool(valueUtf8, tag, out var value))
        {
            var boolNode = new BoolNode
            {
                Value = value,
                Range = range,
            };
            reader.Read();
            return boolNode;
        }

        var expressionNode = ParseStringAndValidateExpression(ref reader, diagnostics, context, errorMessage, parseWholeValueIfNoEmbedded: false);
        if (expressionNode is null)
        {
            return null;
        }

        return new BoolNode
        {
            Value = false,
            Expression = expressionNode,
            Range = range,
        };
    }

    private static Dictionary<Utf8String, Job> ParseJobsMapping<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var jobs = new Dictionary<Utf8String, Job>();
        var seenJobIds = new HashSet<Utf8String>();
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
            if (!TryRegisterMappingKey(
                jobIdUtf8,
                jobIdMark,
                diagnostics,
                seenJobIds,
                MappingKeyComparison.AsciiCaseInsensitive,
                "jobs"))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var jobIdNode = new StringNode
            {
                Value = jobId,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(jobIdMark, jobIdUtf8.Length),
            };
            var jobKey = Utf8String.FromLowerAscii(jobIdUtf8);
            reader.Read(); // consume job id

            if (reader.End)
            {
                break;
            }

            var job = ParseJobNode(ref reader, diagnostics, source, jobId, jobIdMark, jobIdNode);
            jobs[jobKey] = job;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return jobs;
    }

}
